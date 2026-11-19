using System.Diagnostics;
using System.Text;
using Radio.Core;

namespace Radio.Tests;

/// <summary>The short sine-tone files in Samples, made with ffmpeg.</summary>
internal static class Samples
{
    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", name));
}

/// <summary>Stands in for the internet: each link gets a canned answer, and every connection is remembered.</summary>
internal sealed class FakeServer
{
    private readonly Dictionary<string, Func<Stream>> _answers = new();
    private readonly List<string> _asked = new();
    private readonly List<FakeConnection> _connections = new();

    public IReadOnlyList<string> Asked
    {
        get
        {
            lock (_asked) return _asked.ToList();
        }
    }

    public FakeConnection LastConnection
    {
        get
        {
            lock (_asked) return _connections[^1];
        }
    }

    public void Answer(string url, byte[] answer) => _answers[url] = () => new MemoryStream(answer);

    public void Answer(string url, Func<Stream> answer) => _answers[url] = answer;

    public Stream Connect(Uri uri, CancellationToken cancel)
    {
        if (!_answers.TryGetValue(uri.AbsoluteUri, out var answer))
        {
            lock (_asked) _asked.Add(uri.AbsoluteUri);

            throw new IOException("no server at " + uri);
        }

        var connection = new FakeConnection(answer());

        lock (_asked)
        {
            _asked.Add(uri.AbsoluteUri);
            _connections.Add(connection);
        }

        return connection;
    }

    public static byte[] Http(int status, string headers, byte[] body) => Ascii($"HTTP/1.1 {status} X\r\n{headers}\r\n").Concat(body).ToArray();

    public static byte[] Http(int status, string headers, string body) => Http(status, headers, Encoding.UTF8.GetBytes(body));

    public static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}

/// <summary>One connection: it keeps what was sent and answers from a stream.</summary>
internal sealed class FakeConnection : Stream
{
    private readonly Stream _answer;
    private readonly MemoryStream _request = new();
    private volatile bool _isClosed;

    public FakeConnection(Stream answer) => _answer = answer;

    public string Request
    {
        get
        {
            lock (_request) return Encoding.ASCII.GetString(_request.ToArray());
        }
    }

    public bool IsClosed => _isClosed;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_isClosed) throw new ObjectDisposedException(nameof(FakeConnection));

        return _answer.Read(buffer, offset, count);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_request) _request.Write(buffer, offset, count);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _isClosed = true;
        base.Dispose(disposing);
    }
}

/// <summary>A live station: its head, then the same bytes over and over until the connection closes.</summary>
internal sealed class EndlessStream : Stream
{
    private readonly byte[] _head;
    private readonly byte[] _loop;
    private long _position;

    public EndlessStream(byte[] head, byte[] loop)
    {
        _head = head;
        _loop = loop;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var take = Math.Min(count, 4096);

        for (var i = 0; i < take; i++, _position++)
        {
            buffer[offset + i] = _position < _head.Length ? _head[_position] : _loop[(_position - _head.Length) % _loop.Length];
        }

        return take;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

/// <summary>Turns every frame into a fixed amount of quiet sound at the frame's rate, and remembers what it saw.</summary>
internal sealed class FakeDecoder : IFrameDecoder
{
    public const int FramesPerCall = 1152;

    private readonly float[] _sound = Enumerable.Repeat(0.25f, FramesPerCall * 2).ToArray();

    public int Frames { get; private set; }

    public FrameFormat? LastFormat { get; private set; }

    public void Decode(FrameFormat format, ReadOnlySpan<byte> frame, IPcmSink output)
    {
        Frames++;
        LastFormat = format;
        output.Pcm(_sound, 2, format.SampleRate);
    }

    public void Dispose()
    {
    }
}

/// <summary>A song or a stream the system decoder reads: a few blocks of sound, then the end.</summary>
internal sealed class FakeFileDecoder : IFileDecoder
{
    private readonly float[] _block;
    private int _blocksLeft;

    public FakeFileDecoder(int blocks, int framesPerBlock = 4800)
    {
        _blocksLeft = blocks;
        _block = Enumerable.Repeat(0.5f, framesPerBlock * 2).ToArray();
    }

    public bool Read(IPcmSink output)
    {
        if (_blocksLeft == 0) return false;

        _blocksLeft--;
        output.Pcm(_block, 2, SampleRing.Rate);

        return true;
    }

    public void Dispose()
    {
    }
}

internal static class Events
{
    /// <summary>Collects a reader's events until one matches, or fails after the time is up.</summary>
    public static List<PlayerEvent> Until(IPlayback playback, Func<PlayerEvent, bool> isLast, int timeoutMs = 5000)
    {
        var events = new List<PlayerEvent>();
        var clock = Stopwatch.StartNew();

        while (clock.ElapsedMilliseconds < timeoutMs)
        {
            if (playback.TryTakeEvent(out var playerEvent) && playerEvent != null)
            {
                events.Add(playerEvent);

                if (isLast(playerEvent)) return events;

                continue;
            }

            Thread.Sleep(5);
        }

        throw new TimeoutException("the reader never sent the event; it sent: " + string.Join(", ", events));
    }

    /// <summary>Waits for something another thread does.</summary>
    public static void WaitFor(Func<bool> isDone, int timeoutMs = 5000)
    {
        var clock = Stopwatch.StartNew();

        while (!isDone())
        {
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("it never happened");

            Thread.Sleep(5);
        }
    }
}
