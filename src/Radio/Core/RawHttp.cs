using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Radio.Core;

/// <summary>Opens a connected byte stream to the host in the link, with TLS for https.</summary>
public delegate Stream Connector(Uri uri, CancellationToken cancel);

/// <summary>A server answered with something other than the audio or file asked for.</summary>
public sealed class HttpFailure : Exception
{
    public HttpFailure(int status, string message) : base(message) => Status = status;

    public int Status { get; }
}

/// <summary>One answer from a server: its status, headers and body. Disposing it closes the connection.</summary>
public sealed class HttpReply : IDisposable
{
    private readonly Dictionary<string, string> _headers;

    internal HttpReply(int status, Dictionary<string, string> headers, Stream body, Uri url)
    {
        Status = status;
        _headers = headers;
        Body = body;
        Url = url;
    }

    public int Status { get; }

    public Stream Body { get; }

    /// <summary>The link the answer came from, after any redirects.</summary>
    public Uri Url { get; }

    public string? Header(string name) => _headers.TryGetValue(name, out var value) ? value : null;

    /// <summary>The media type alone, lower case, without its parameters.</summary>
    public string ContentType => FullContentType.Split(';')[0].Trim();

    /// <summary>The media type with its parameters, lower case, since "audio/ogg; codecs=opus" says more than "audio/ogg".</summary>
    public string FullContentType => (Header("Content-Type") ?? "").ToLowerInvariant();

    public void Dispose() => Body.Dispose();
}

/// <summary>
/// A small HTTP/1.1 client for radio streams. HttpClient refuses Shoutcast's "ICY 200 OK" status line, and it can't
/// hand over the raw body of a stream that never ends, so this one is written for that: redirects, chunked bodies,
/// and the Icy-MetaData header that asks the server to send song titles.
/// </summary>
public static class RawHttp
{
    public const int MaxRedirects = 5;
    private const int MaxHeaderBytes = 64 * 1024;

    public static HttpReply Get(Connector connect, Uri uri, string userAgent, bool askForTitles, CancellationToken cancel)
    {
        for (var hop = 0; ; hop++)
        {
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) throw new HttpFailure(0, "not an http or https link: " + uri);

            var stream = connect(uri, cancel);

            // A blocked read only wakes up when the connection closes, so stopping closes it.
            using var closeOnStop = cancel.Register(stream.Dispose);

            try
            {
                var request = new StringBuilder()
                    .Append("GET ").Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n")
                    .Append("Host: ").Append(uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
                    .Append("User-Agent: ").Append(userAgent).Append("\r\n")
                    .Append("Accept: */*\r\n");

                if (askForTitles) request.Append("Icy-MetaData: 1\r\n");

                request.Append("Connection: close\r\n\r\n");

                var bytes = Encoding.ASCII.GetBytes(request.ToString());
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();

                var source = new BufferedSource(stream);
                var (status, headers) = ReadHead(source);

                if ((status is 301 or 302 or 303 or 307 or 308) && headers.TryGetValue("Location", out var location))
                {
                    stream.Dispose();

                    if (hop >= MaxRedirects) throw new HttpFailure(status, "too many redirects");

                    uri = new Uri(uri, location);
                    continue;
                }

                if (status is < 200 or > 299) throw new HttpFailure(status, "the server answered " + status.ToString(CultureInfo.InvariantCulture));

                Stream body = source;

                if (headers.TryGetValue("Transfer-Encoding", out var encoding) && encoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0) body = new ChunkedStream(source);
                else if (headers.TryGetValue("Content-Length", out var lengthText) && long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length)) body = new LimitedStream(source, length);

                return new HttpReply(status, headers, body, uri);
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }
    }

    /// <summary>Reads a whole body as bytes, refusing one larger than the limit.</summary>
    public static byte[] ReadAll(HttpReply reply, int maxBytes)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[16 * 1024];

        while (true)
        {
            var read = reply.Body.Read(buffer, 0, buffer.Length);

            if (read <= 0) return memory.ToArray();

            if (memory.Length + read > maxBytes) throw new HttpFailure(reply.Status, "the answer is too large");

            memory.Write(buffer, 0, read);
        }
    }

    private static (int Status, Dictionary<string, string> Headers) ReadHead(BufferedSource source)
    {
        var total = 0;
        var statusLine = source.ReadLine(ref total, MaxHeaderBytes) ?? throw new HttpFailure(0, "the server closed the connection without answering");

        // "HTTP/1.1 200 OK", "HTTP/1.0 302 Found", or Shoutcast's "ICY 200 OK".
        var parts = statusLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2 || !(parts[0].StartsWith("HTTP/", StringComparison.Ordinal) || parts[0] == "ICY") || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var status))
            throw new HttpFailure(0, "the server did not answer with HTTP");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var line = source.ReadLine(ref total, MaxHeaderBytes) ?? throw new HttpFailure(status, "the server closed the connection in its headers");

            if (line.Length == 0) return (status, headers);

            var colon = line.IndexOf(':');

            if (colon <= 0) continue;

            headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
        }
    }
}

/// <summary>Reads header lines from a connection, then hands out the bytes after them as the body.</summary>
internal sealed class BufferedSource : ReadOnlyStream
{
    private readonly Stream _inner;
    private readonly byte[] _buffer = new byte[8192];
    private int _start;
    private int _end;

    public BufferedSource(Stream inner) => _inner = inner;

    /// <summary>One line without its line break, or null when the connection closed first.</summary>
    public string? ReadLine(ref int total, int max)
    {
        var line = new StringBuilder();

        while (true)
        {
            if (_start == _end && !Fill()) return line.Length > 0 ? line.ToString() : null;

            var b = _buffer[_start++];

            if (++total > max) throw new HttpFailure(0, "the server's headers are too long");

            if (b == '\n')
            {
                if (line.Length > 0 && line[line.Length - 1] == '\r') line.Length--;

                return line.ToString();
            }

            line.Append((char)b);
        }
    }

    public int ReadByteOrEnd()
    {
        if (_start == _end && !Fill()) return -1;

        return _buffer[_start++];
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;

        if (_start < _end)
        {
            var take = Math.Min(count, _end - _start);
            Buffer.BlockCopy(_buffer, _start, buffer, offset, take);
            _start += take;
            return take;
        }

        return _inner.Read(buffer, offset, count);
    }

    private bool Fill()
    {
        _start = 0;
        _end = _inner.Read(_buffer, 0, _buffer.Length);

        if (_end > 0) return true;

        _end = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();

        base.Dispose(disposing);
    }
}

/// <summary>A body sent in chunks, each with its size in hex before it.</summary>
internal sealed class ChunkedStream : ReadOnlyStream
{
    private readonly BufferedSource _source;
    private long _left;
    private bool _isDone;

    public ChunkedStream(BufferedSource source) => _source = source;

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_isDone || count == 0) return 0;

        if (_left == 0 && !NextChunk()) return 0;

        var read = _source.Read(buffer, offset, (int)Math.Min(count, _left));

        if (read <= 0) throw new IOException("the connection closed inside a chunk");

        _left -= read;

        // The line break after each chunk's data.
        if (_left == 0) SkipLine();

        return read;
    }

    private bool NextChunk()
    {
        var total = 0;
        var line = _source.ReadLine(ref total, 1024);

        if (line == null) return Finish();

        var semicolon = line.IndexOf(';');
        var sizeText = (semicolon >= 0 ? line.Substring(0, semicolon) : line).Trim();

        if (!long.TryParse(sizeText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _left) || _left < 0) throw new IOException("bad chunk size: " + line);

        return _left != 0 || Finish();
    }

    private void SkipLine()
    {
        var total = 0;
        _source.ReadLine(ref total, 1024);
    }

    private bool Finish()
    {
        _isDone = true;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();

        base.Dispose(disposing);
    }
}

/// <summary>A body with a Content-Length: it ends after that many bytes.</summary>
internal sealed class LimitedStream : ReadOnlyStream
{
    private readonly Stream _inner;
    private long _left;

    public LimitedStream(Stream inner, long length)
    {
        _inner = inner;
        _left = length;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_left <= 0 || count == 0) return 0;

        var read = _inner.Read(buffer, offset, (int)Math.Min(count, _left));

        if (read <= 0) throw new IOException("the connection closed before the whole answer arrived");

        _left -= read;
        return read;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();

        base.Dispose(disposing);
    }
}

/// <summary>The parts of Stream every read-only wrapper here leaves the same.</summary>
public abstract class ReadOnlyStream : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
