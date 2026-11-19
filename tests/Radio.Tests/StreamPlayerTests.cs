using System.Security.Cryptography;
using System.Text;
using Radio.Core;
using Xunit;

namespace Radio.Tests;

public sealed class StreamPlayerTests : IDisposable
{
    private const string HlsType = "application/vnd.apple.mpegurl";

    private readonly FakeServer _server = new();
    private readonly FakeDecoder _decoder = new();
    private readonly List<string> _openedWithSystem = new();
    private StreamPlayer? _player;
    private bool _systemCannotDecode;
    private IFileDecoder? _systemFile;

    public void Dispose() => _player?.Stop();

    private StreamPlayer Play(string url)
    {
        var tools = new StreamTools(_server.Connect, () => _decoder, OpenWithSystem, "Radio/test");
        _player = new StreamPlayer(new Uri(url), tools);
        _player.Start();

        return _player;
    }

    private IFileDecoder OpenWithSystem(string link)
    {
        lock (_openedWithSystem) _openedWithSystem.Add(link);

        if (_systemCannotDecode) throw new UnsupportedAudio("stream.bin");

        return _systemFile ?? new FakeFileDecoder(3);
    }

    private static List<PlayerEvent> UntilDone(IPlayback player, int timeoutMs = 5000) =>
        Events.Until(player, e => e is PlayerEvent.Ended or PlayerEvent.Failed, timeoutMs);

    /// <summary>A whole file: the server says how long it is.</summary>
    private static byte[] File(string contentType, byte[] body) => FakeServer.Http(200, $"Content-Type: {contentType}\r\nContent-Length: {body.Length}\r\n", body);

    private static byte[] File(string contentType, string body) => File(contentType, Encoding.UTF8.GetBytes(body));

    private static byte[] Mp3WithoutTag()
    {
        var mp3 = Samples.Read("tone.mp3");

        return mp3.AsSpan(Id3.TagLength(mp3)).ToArray();
    }

    /// <summary>A title block as a Shoutcast server sends it: a length byte, then the text padded to 16 bytes.</summary>
    private static byte[] TitleBlock(string title)
    {
        var text = Encoding.UTF8.GetBytes($"StreamTitle='{title}';");
        var block = new byte[1 + (text.Length + 15) / 16 * 16];
        block[0] = (byte)((text.Length + 15) / 16);
        text.CopyTo(block, 1);

        return block;
    }

    [Fact]
    public void ALiveStationPlaysAndShowsItsSongTitle()
    {
        var audio = Mp3WithoutTag();
        var head = FakeServer.Ascii($"HTTP/1.1 200 OK\r\nContent-Type: audio/mpeg\r\nicy-metaint: {audio.Length}\r\n\r\n");
        _server.Answer("http://radio.example/live", () => new EndlessStream(head, audio.Concat(TitleBlock("Artist - Song")).ToArray()));

        var events = Events.Until(Play("http://radio.example/live"), e => e is PlayerEvent.Title);

        Assert.Contains(new PlayerEvent.Playing("Mp3 44100 Hz, 2 ch"), events);
        Assert.Equal(new PlayerEvent.Title("Artist - Song"), events[^1]);
        Assert.Contains("Icy-MetaData: 1\r\n", _server.LastConnection.Request);
    }

    [Fact]
    public void StoppingALiveStationClosesItsConnection()
    {
        var audio = Mp3WithoutTag();
        _server.Answer("http://radio.example/live", () => new EndlessStream(FakeServer.Ascii("ICY 200 OK\r\n\r\n"), audio));
        var player = Play("http://radio.example/live");
        Events.Until(player, e => e is PlayerEvent.Playing);

        player.Stop();

        Events.WaitFor(() => _server.LastConnection.IsClosed);
    }

    /// <summary>
    /// Stop closes the connection, so the read under it ends. If the player took that for a dropped station, it would
    /// say it is trying again after the player already stopped it.
    /// </summary>
    [Fact]
    public void AStoppedStationSaysNothingMore()
    {
        var held = new HeldFileDecoder();
        _systemFile = held;
        _server.Answer("http://radio.example/live.ogg", () => new EndlessStream(FakeServer.Ascii("HTTP/1.1 200 OK\r\nContent-Type: audio/ogg\r\n\r\n"), Encoding.ASCII.GetBytes("OggS").Concat(new byte[2000]).ToArray()));
        var player = Play("http://radio.example/live.ogg");
        Events.WaitFor(() => held.IsReading);
        while (player.TryTakeEvent(out _))
        {
        }

        player.Stop();
        held.LetGo();
        Events.WaitFor(() => held.IsClosed);
        Thread.Sleep(100);

        Assert.False(player.TryTakeEvent(out var after), "it sent " + after);
    }

    [Fact]
    public void AFileWithAnEndPlaysToTheEnd()
    {
        var mp3 = Samples.Read("tone.mp3");
        _server.Answer("http://radio.example/show.mp3", File("audio/mpeg", mp3));

        var events = UntilDone(Play("http://radio.example/show.mp3"));

        Assert.IsType<PlayerEvent.Ended>(events[^1]);
        Assert.Equal(41, _decoder.Frames);
    }

    [Fact]
    public void AStationThatDropsIsConnectedAgain()
    {
        var mp3 = Mp3WithoutTag();
        _server.Answer("http://radio.example/live", FakeServer.Http(200, "Content-Type: audio/mpeg\r\n", mp3));
        var player = Play("http://radio.example/live");

        var events = Events.Until(player, e => e is PlayerEvent.Playing);
        events.AddRange(Events.Until(player, e => e is PlayerEvent.Playing));

        Assert.Contains(new PlayerEvent.Retrying(1, Trouble.CannotConnect, "the stream ended"), events);
        Assert.Equal(2, _server.Asked.Count);
    }

    [Fact]
    public void ALinkTheServerRefusesIsTriedAgain()
    {
        _server.Answer("http://radio.example/live", FakeServer.Http(404, "", "not here"));

        var events = Events.Until(Play("http://radio.example/live"), e => e is PlayerEvent.Retrying);

        Assert.Equal(new PlayerEvent.Retrying(1, Trouble.Refused, "HTTP 404"), events[^1]);
    }

    [Fact]
    public void ALinkThatNeverPlayedIsGivenUpAfterAFewTries()
    {
        _server.Answer("http://radio.example/live", FakeServer.Http(404, "", "not here"));

        var events = UntilDone(Play("http://radio.example/live"), timeoutMs: 8000);

        Assert.Equal(new PlayerEvent.Failed(Trouble.Refused, "HTTP 404"), events[^1]);
        Assert.Equal(Backoff.TriesBeforePlaying, _server.Asked.Count);
    }

    [Fact]
    public void AWebPageIsNotAStation()
    {
        _server.Answer("http://radio.example/", File("text/html", "<!DOCTYPE html><html><body>Listen live!</body></html>"));

        var events = UntilDone(Play("http://radio.example/"));

        Assert.Equal(new PlayerEvent.Failed(Trouble.NotAStream, "the link is a web page"), events[^1]);
    }

    [Fact]
    public void APlsFileIsFollowedToTheStream()
    {
        _server.Answer("http://radio.example/listen.pls", File("audio/x-scpls", "[playlist]\nNumberOfEntries=1\nFile1=http://radio.example/show.mp3\n"));
        _server.Answer("http://radio.example/show.mp3", File("audio/mpeg", Samples.Read("tone.mp3")));

        var events = UntilDone(Play("http://radio.example/listen.pls"));

        Assert.IsType<PlayerEvent.Ended>(events[^1]);
        Assert.Equal(new[] { "http://radio.example/listen.pls", "http://radio.example/show.mp3" }, _server.Asked);
    }

    [Fact]
    public void AListThatPointsAtItselfDoesNotGoRoundForever()
    {
        _server.Answer("http://radio.example/loop.m3u", File("audio/x-mpegurl", "loop.m3u\n"));

        var events = UntilDone(Play("http://radio.example/loop.m3u"));

        Assert.Equal(new PlayerEvent.Failed(Trouble.NotAStream, "too many lists inside lists"), events[^1]);
    }

    [Fact]
    public void Ac3SoundIsNamedAsUnsupported()
    {
        _server.Answer("http://radio.example/tv.ts", File("video/mp2t", Samples.Read("ac3.ts")));

        var events = UntilDone(Play("http://radio.example/tv.ts"));

        Assert.Equal(new PlayerEvent.Failed(Trouble.Unsupported, "AC-3"), events[^1]);
    }

    [Fact]
    public void OggAndOtherFormatsGoToTheSystemDecoder()
    {
        _server.Answer("http://radio.example/show.ogg", File("audio/ogg", Encoding.ASCII.GetBytes("OggS").Concat(new byte[2000]).ToArray()));

        var events = UntilDone(Play("http://radio.example/show.ogg"));

        Assert.Contains(new PlayerEvent.Playing("system decoder"), events);
        Assert.IsType<PlayerEvent.Ended>(events[^1]);
        Assert.Equal(new[] { "http://radio.example/show.ogg" }, _openedWithSystem);
    }

    [Theory]
    [InlineData("OggS", "audio/ogg", "Ogg")]
    [InlineData("fLaC", "audio/flac", "FLAC")]
    [InlineData("\0\0\0 ftypM4A ", "audio/mp4", "MP4")]
    [InlineData("\0\0\0\0\0\0\0\0", "audio/ogg; codecs=opus", "Opus")]
    [InlineData("OggS\0\x02\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\0\x01\x13OpusHead", "audio/ogg", "Opus")]
    public void AFormatTheSystemCannotDecodeIsNamedByWhatTheStationSent(string head, string contentType, string name)
    {
        _systemCannotDecode = true;
        _server.Answer("http://radio.example/live", File(contentType, Encoding.ASCII.GetBytes(head).Concat(new byte[2000]).ToArray()));

        var events = UntilDone(Play("http://radio.example/live"));

        Assert.Equal(new PlayerEvent.Failed(Trouble.Unsupported, name), events[^1]);
    }

    [Fact]
    public void AnHlsStationPlaysTheAudioQualityAndAllItsSegments()
    {
        _server.Answer("http://radio.example/hls/master.m3u8", File(HlsType, "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=640000,RESOLUTION=640x360\nvideo.m3u8\n#EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS=\"mp4a.40.2\"\naudio.m3u8\n"));
        _server.Answer("http://radio.example/hls/audio.m3u8", File(HlsType, "#EXTM3U\n#EXT-X-TARGETDURATION:1\n#EXTINF:1,\nseg1.ts\n#EXTINF:1,\nseg2.ts\n#EXT-X-ENDLIST\n"));
        _server.Answer("http://radio.example/hls/seg1.ts", File("video/mp2t", Samples.Read("tone.ts")));
        _server.Answer("http://radio.example/hls/seg2.ts", File("video/mp2t", Samples.Read("tone.ts")));

        var events = UntilDone(Play("http://radio.example/hls/master.m3u8"));

        Assert.IsType<PlayerEvent.Ended>(events[^1]);
        Assert.Contains(new PlayerEvent.Playing("Aac 48000 Hz, 2 ch"), events);
        Assert.Equal(96, _decoder.Frames);
        Assert.DoesNotContain("http://radio.example/hls/video.m3u8", _server.Asked);
    }

    [Fact]
    public void EncryptedHlsSegmentsAreDecryptedWithTheirSequenceNumber()
    {
        var key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        using var aes = Aes.Create();
        aes.Key = key;
        var segment = aes.EncryptCbc(Samples.Read("tone.ts"), SegmentCrypto.SequenceIv(7), PaddingMode.PKCS7);
        _server.Answer("http://radio.example/hls/live.m3u8", File(HlsType, "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:7\n#EXT-X-KEY:METHOD=AES-128,URI=\"key\"\n#EXTINF:1,\nseg.ts\n#EXT-X-ENDLIST\n"));
        _server.Answer("http://radio.example/hls/key", File("application/octet-stream", key));
        _server.Answer("http://radio.example/hls/seg.ts", File("video/mp2t", segment));

        var events = UntilDone(Play("http://radio.example/hls/live.m3u8"));

        Assert.IsType<PlayerEvent.Ended>(events[^1]);
        Assert.Equal(48, _decoder.Frames);
    }

    [Fact]
    public void Fmp4HlsIsNamedAsUnsupported()
    {
        _server.Answer("http://radio.example/hls/live.m3u8", File(HlsType, "#EXTM3U\n#EXT-X-MAP:URI=\"init.mp4\"\n#EXTINF:1,\nseg.m4s\n"));

        var events = UntilDone(Play("http://radio.example/hls/live.m3u8"));

        Assert.Equal(new PlayerEvent.Failed(Trouble.Unsupported, "fMP4 segments"), events[^1]);
    }

    [Fact]
    public void ASegmentTheStationAlreadyDroppedIsSkipped()
    {
        _server.Answer("http://radio.example/hls/live.m3u8", File(HlsType, "#EXTM3U\n#EXTINF:1,\ngone.ts\n#EXTINF:1,\nseg.ts\n#EXT-X-ENDLIST\n"));
        _server.Answer("http://radio.example/hls/gone.ts", FakeServer.Http(404, "", ""));
        _server.Answer("http://radio.example/hls/seg.ts", File("video/mp2t", Samples.Read("tone.ts")));

        var events = UntilDone(Play("http://radio.example/hls/live.m3u8"));

        Assert.Contains(events, e => e is PlayerEvent.Note n && n.Text.StartsWith("skipped segment 0:", StringComparison.Ordinal));
        Assert.IsType<PlayerEvent.Ended>(events[^1]);
        Assert.Equal(48, _decoder.Frames);
    }

    [Fact]
    public void HlsSegmentsWithNoSoundAreNotAStation()
    {
        _server.Answer("http://radio.example/hls/live.m3u8", File(HlsType, "#EXTM3U\n#EXTINF:1,\na.bin\n#EXTINF:1,\nb.bin\n#EXTINF:1,\nc.bin\n"));
        _server.Answer("http://radio.example/hls/a.bin", File("application/octet-stream", new byte[2000]));
        _server.Answer("http://radio.example/hls/b.bin", File("application/octet-stream", new byte[2000]));
        _server.Answer("http://radio.example/hls/c.bin", File("application/octet-stream", new byte[2000]));

        var events = UntilDone(Play("http://radio.example/hls/live.m3u8"));

        Assert.Equal(new PlayerEvent.Failed(Trouble.NotAStream, "the HLS segments have no sound this mod can read"), events[^1]);
    }

    /// <summary>A live list that stops getting new segments would otherwise be asked for again forever, in silence.</summary>
    [Fact]
    public void ALiveHlsListThatStopsGrowingConnectsAgain()
    {
        _server.Answer("http://radio.example/hls/live.m3u8", File(HlsType, "#EXTM3U\n#EXT-X-TARGETDURATION:0.1\n#EXTINF:0.1,\nseg.ts\n"));
        _server.Answer("http://radio.example/hls/seg.ts", File("video/mp2t", Samples.Read("tone.ts")));

        var events = Events.Until(Play("http://radio.example/hls/live.m3u8"), e => e is PlayerEvent.Retrying);

        Assert.Equal(48, _decoder.Frames);
        Assert.Equal(new PlayerEvent.Retrying(1, Trouble.CannotConnect, "IOException: the HLS list stopped getting new segments"), events[^1]);
    }

    /// <summary>A station the system decoder is waiting on, the way a slow one keeps it waiting, until the test lets it go.</summary>
    private sealed class HeldFileDecoder : IFileDecoder
    {
        private readonly ManualResetEventSlim _letGo = new();
        private volatile bool _isReading;
        private volatile bool _isClosed;

        public bool IsReading => _isReading;

        public bool IsClosed => _isClosed;

        public bool Read(IPcmSink output)
        {
            _isReading = true;
            _letGo.Wait(5000);

            return true;
        }

        public void LetGo() => _letGo.Set();

        public void Dispose() => _isClosed = true;
    }
}
