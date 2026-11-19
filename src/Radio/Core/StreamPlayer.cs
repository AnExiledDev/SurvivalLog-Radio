using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Radio.Core;

/// <summary>What a stream reader needs from the outside world. Every piece runs on the reader's thread.</summary>
public sealed class StreamTools
{
    public StreamTools(Connector connect, Func<IFrameDecoder> makeDecoder, FileOpener openWithSystem, string userAgent)
    {
        Connect = connect;
        MakeDecoder = makeDecoder;
        OpenWithSystem = openWithSystem;
        UserAgent = userAgent;
    }

    public Connector Connect { get; }

    public Func<IFrameDecoder> MakeDecoder { get; }

    /// <summary>For links the reader can't split itself, like MP4 or FLAC files.</summary>
    public FileOpener OpenWithSystem { get; }

    public string UserAgent { get; }
}

/// <summary>
/// Plays an internet radio link on its own thread: Shoutcast and Icecast streams, HLS, and .pls or .m3u files that
/// point at them. It follows a link through up to three lists, reconnects after a drop, and gives up on a link that
/// never played after a few tries.
/// </summary>
public sealed class StreamPlayer : IPlayback
{
    public const double BufferSeconds = 8;
    public const double PrebufferSeconds = 1.5;
    private const int MaxListDepth = 3;
    private const int PeekBytes = 4096;
    private const int MaxListBytes = 256 * 1024;
    private const int MaxSegmentBytes = 32 * 1024 * 1024;
    private const int MaxSkippedBeforeGivingUp = 512 * 1024;
    private const double MinRefreshSeconds = 0.25;

    /// <summary>A live HLS list asked for this many times in a row with nothing new has gone quiet: three segments long.</summary>
    private const int MaxQuietRefreshes = 6;

    private readonly Uri _link;
    private readonly StreamTools _tools;
    private readonly CancellationTokenSource _stop = new();
    private readonly EventQueue _events = new();
    private readonly Dictionary<Uri, byte[]> _keys = new();
    private readonly Backoff _backoff = new();
    private readonly Resampler _resampler;
    private IFrameDecoder? _decoder;
    private bool _hasPlayedThisConnection;

    public StreamPlayer(Uri link, StreamTools tools)
    {
        _link = link;
        _tools = tools;
        Ring = new SampleRing(BufferSeconds, PrebufferSeconds);
        _resampler = new Resampler(WriteToRing);
    }

    public SampleRing Ring { get; }

    private CancellationToken Cancel => _stop.Token;

    public void Start() => new Thread(Run) { IsBackground = true, Name = "Radio stream" }.Start();

    public bool TryTakeEvent(out PlayerEvent? playerEvent) => _events.TryTake(out playerEvent);

    public void Stop()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Run()
    {
        try
        {
            RunUntilDone();
        }
        finally
        {
            _decoder?.Dispose();
        }
    }

    private void RunUntilDone()
    {
        while (!Cancel.IsCancellationRequested)
        {
            Trouble why;
            string detail;
            _events.Add(new PlayerEvent.Connecting());
            _hasPlayedThisConnection = false;

            try
            {
                var hasReachedTheEnd = Open(_link, 0);

                // Stop closes the connection, which also ends the read under it. That isn't a drop.
                if (Cancel.IsCancellationRequested) return;

                if (hasReachedTheEnd)
                {
                    Ring.Finish();
                    _events.Add(new PlayerEvent.Ended());
                    return;
                }

                why = Trouble.CannotConnect;
                detail = "the stream ended";
            }
            catch (Exception) when (Cancel.IsCancellationRequested)
            {
                return;
            }
            catch (UnsupportedAudio e)
            {
                _events.Add(new PlayerEvent.Failed(Trouble.Unsupported, e.Format));
                return;
            }
            catch (NotAStream e)
            {
                _events.Add(new PlayerEvent.Failed(Trouble.NotAStream, e.Message));
                return;
            }
            catch (HttpFailure e) when (e.Status is >= 400 and < 500)
            {
                // The status line shows this, so it stays the same in every language.
                why = Trouble.Refused;
                detail = "HTTP " + e.Status.ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                why = Trouble.CannotConnect;
                detail = e.GetType().Name + ": " + e.Message;
            }

            var wait = _backoff.Next();

            if (wait == null)
            {
                _events.Add(new PlayerEvent.Failed(why, detail));
                return;
            }

            _events.Add(new PlayerEvent.Retrying(wait.Value, why, detail));

            if (Cancel.WaitHandle.WaitOne(wait.Value * 1000)) return;
        }
    }

    /// <summary>Plays a link until it ends or drops. True when it reached a real end, like the end of a recording.</summary>
    private bool Open(Uri link, int depth)
    {
        using var reply = Get(link, askForTitles: true);
        using var closeOnStop = Cancel.Register(reply.Dispose);
        var body = reply.Body;

        if (int.TryParse(reply.Header("icy-metaint"), NumberStyles.None, CultureInfo.InvariantCulture, out var metaInterval) && metaInterval > 0)
            body = new IcyStream(body, metaInterval, title => _events.Add(new PlayerEvent.Title(title)));

        var head = Peek(body);
        var kind = Sniff.Kind(reply.ContentType, reply.Url, head);
        _events.Add(new PlayerEvent.Note($"{reply.Url} answered {reply.Status}, {reply.ContentType}, read as {kind}"));

        switch (kind)
        {
            case BodyKind.LinkList:
                if (depth >= MaxListDepth) throw new NotAStream("too many lists inside lists");

                var next = LinkList.FirstLink(ReadText(head, body), reply.Url) ?? throw new NotAStream("the list has no links");
                return Open(next, depth + 1);

            case BodyKind.Hls:
                return PlayHls(ReadText(head, body), reply.Url);

            case BodyKind.WebPage:
                throw new NotAStream("the link is a web page");

            case BodyKind.OtherAudio:
                return PlayWithSystem(reply.Url, Sniff.AudioName(reply.FullContentType, head));

            default:
                PlayDirect(head, body, kind == BodyKind.Ts);
                return reply.Header("Content-Length") != null;
        }
    }

    private void PlayDirect(byte[] head, Stream body, bool isTs)
    {
        var frames = new FrameSink(this);
        var splitter = new FrameSplitter(frames);
        var demux = isTs ? new TsDemux(splitter) : null;
        IByteSink input = demux != null ? demux : splitter;
        var skip = Math.Max(0, Id3.TagLength(head));
        var buffer = new byte[16 * 1024];

        if (skip < head.Length) input.Bytes(head.AsSpan(skip));
        else SkipBytes(body, skip - head.Length, buffer);

        // A short file may fit in the head, and its format still has to be checked.
        CheckIsAudio(splitter, demux);

        while (true)
        {
            var read = body.Read(buffer, 0, buffer.Length);

            if (read <= 0) return;

            input.Bytes(buffer.AsSpan(0, read));
            CheckIsAudio(splitter, demux);
        }
    }

    private bool PlayHls(string text, Uri url)
    {
        if (Hls.IsMaster(text))
        {
            var variant = Hls.PickVariant(text, url) ?? throw new NotAStream("the HLS list has no streams");
            (text, url) = GetText(variant);
        }

        var frames = new FrameSink(this);
        var splitter = new FrameSplitter(frames);
        TsDemux? demux = null;
        var tracker = new HlsTracker();
        var segmentsRead = 0;
        var quietRefreshes = 0;

        while (true)
        {
            var playlist = Hls.ParseMedia(text, url);

            if (playlist.Problem != null) throw new UnsupportedAudio(playlist.Problem);

            var next = tracker.Next(playlist);

            foreach (var segment in next)
            {
                var bytes = GetSegment(segment);

                if (bytes == null) continue;

                if (segment.StartsNewPart)
                {
                    splitter.Reset();
                    demux?.Reset();
                }

                if (TsDemux.LooksLikeTs(bytes))
                {
                    demux ??= new TsDemux(splitter);
                    demux.Bytes(bytes);
                }
                else
                {
                    // Packed audio: ADTS or MP3 frames with an ID3 tag in front.
                    var skip = Math.Max(0, Id3.TagLength(bytes));

                    if (skip < bytes.Length) splitter.Bytes(bytes.AsSpan(skip));
                }

                segmentsRead++;
                CheckIsAudio(splitter, demux);

                if (segmentsRead >= 2 && !_hasPlayedThisConnection) throw new NotAStream("the HLS segments have no sound this mod can read");
            }

            if (playlist.HasEnd) return true;

            if (next.Count > 0)
            {
                quietRefreshes = 0;
            }
            else
            {
                // The sound ran out long ago, so connecting again is the only way back.
                if (++quietRefreshes > MaxQuietRefreshes) throw new IOException("the HLS list stopped getting new segments");

                if (Cancel.WaitHandle.WaitOne(TimeSpan.FromSeconds(Math.Max(MinRefreshSeconds, playlist.TargetSeconds / 2)))) return false;
            }

            (text, url) = GetText(url);
        }
    }

    /// <summary>A segment's bytes, decrypted, or null when the station already dropped it.</summary>
    private byte[]? GetSegment(Segment segment)
    {
        byte[] bytes;

        try
        {
            bytes = GetBytes(segment.Uri, MaxSegmentBytes);
        }
        catch (HttpFailure e) when (e.Status is >= 400 and < 500)
        {
            _events.Add(new PlayerEvent.Note("skipped segment " + segment.Sequence.ToString(CultureInfo.InvariantCulture) + ": " + e.Message));
            return null;
        }

        if (segment.Key == null) return bytes;

        if (!_keys.TryGetValue(segment.Key.Uri, out var key))
        {
            key = GetBytes(segment.Key.Uri, 64);

            if (key.Length != 16) throw new UnsupportedAudio("HLS key of " + key.Length.ToString(CultureInfo.InvariantCulture) + " bytes");

            _keys[segment.Key.Uri] = key;
        }

        return SegmentCrypto.Decrypt(bytes, key, segment.Key.Iv, segment.Sequence);
    }

    /// <param name="name">What the station sent, like "Ogg", for the status line when Windows can't decode it.</param>
    private bool PlayWithSystem(Uri link, string? name)
    {
        IFileDecoder file;

        try
        {
            file = _tools.OpenWithSystem(link.AbsoluteUri);
        }
        catch (UnsupportedAudio) when (name != null)
        {
            throw new UnsupportedAudio(name);
        }

        using var _ = file;

        while (file.Read(_resampler))
        {
            if (Cancel.IsCancellationRequested) return false;

            Played("system decoder");
        }

        return true;
    }

    private void CheckIsAudio(FrameSplitter splitter, TsDemux? demux)
    {
        if (demux is { Audio: TsAudio.Unsupported }) throw new UnsupportedAudio(TsName(demux.StreamType));

        if (demux is { Audio: TsAudio.None }) throw new NotAStream("the stream has no sound");

        if (!splitter.IsLocked && !_hasPlayedThisConnection && splitter.SkippedBytes > MaxSkippedBeforeGivingUp) throw new NotAStream("no MP3 or AAC sound found");
    }

    private static string TsName(int streamType) => streamType switch
    {
        0x11 => "AAC LATM",
        0x81 => "AC-3",
        0x87 => "E-AC-3",
        _ => "stream type " + streamType.ToString(CultureInfo.InvariantCulture),
    };

    private void Played(string format)
    {
        if (_hasPlayedThisConnection) return;

        _hasPlayedThisConnection = true;
        _backoff.Played();
        _events.Add(new PlayerEvent.Playing(format));
    }

    private void WriteToRing(ReadOnlySpan<float> frames)
    {
        if (!Ring.WriteAll(frames, Cancel)) throw new OperationCanceledException(Cancel);
    }

    private HttpReply Get(Uri link, bool askForTitles) => RawHttp.Get(_tools.Connect, link, _tools.UserAgent, askForTitles, Cancel);

    private (string Text, Uri Url) GetText(Uri link)
    {
        using var reply = Get(link, askForTitles: false);
        using var closeOnStop = Cancel.Register(reply.Dispose);

        return (Encoding.UTF8.GetString(RawHttp.ReadAll(reply, MaxListBytes)), reply.Url);
    }

    private byte[] GetBytes(Uri link, int maxBytes)
    {
        using var reply = Get(link, askForTitles: false);
        using var closeOnStop = Cancel.Register(reply.Dispose);

        return RawHttp.ReadAll(reply, maxBytes);
    }

    private static byte[] Peek(Stream body)
    {
        var head = new byte[PeekBytes];
        var have = 0;

        // Enough to tell a transport stream (three sync bytes) or a tag; a live stream may send less at first.
        while (have < 1024)
        {
            var read = body.Read(head, have, head.Length - have);

            if (read <= 0) break;

            have += read;
        }

        Array.Resize(ref head, have);
        return head;
    }

    private static string ReadText(byte[] head, Stream body)
    {
        using var memory = new MemoryStream();
        memory.Write(head, 0, head.Length);
        var buffer = new byte[8192];

        while (memory.Length < MaxListBytes)
        {
            var read = body.Read(buffer, 0, buffer.Length);

            if (read <= 0) break;

            memory.Write(buffer, 0, read);
        }

        return TextDecoding.Decode(memory.ToArray());
    }

    private static void SkipBytes(Stream body, int count, byte[] buffer)
    {
        while (count > 0)
        {
            var read = body.Read(buffer, 0, Math.Min(count, buffer.Length));

            if (read <= 0) return;

            count -= read;
        }
    }

    /// <summary>Hands frames to the decoder, which is made on first use so it lives on this thread.</summary>
    private sealed class FrameSink : IFrameSink
    {
        private readonly StreamPlayer _player;

        public FrameSink(StreamPlayer player) => _player = player;

        public void Frame(FrameFormat format, ReadOnlySpan<byte> frame)
        {
            _player._decoder ??= _player._tools.MakeDecoder();
            _player._decoder.Decode(format, frame, _player._resampler);
            _player.Played(format.ToString());
        }
    }
}

/// <summary>The link leads somewhere that has no sound, like a web page.</summary>
public sealed class NotAStream : Exception
{
    public NotAStream(string message) : base(message)
    {
    }
}
