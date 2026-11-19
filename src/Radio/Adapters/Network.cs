using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Radio.Adapters;

/// <summary>
/// The radio's connections to the internet. Streams get a plain socket, with TLS for https, so the reader can see
/// every byte the station sends. Both streams and station lists go through the proxy Windows is set to use, since
/// many players need one to reach stations abroad.
/// </summary>
internal sealed class Network : IDisposable
{
    public const string UserAgent = "SurvivalLog-Radio/" + Plugin.Version;

    private static readonly TimeSpan ConnectTime = TimeSpan.FromSeconds(10);

    /// <summary>A station that sends nothing for this long has dropped.</summary>
    private const int ReadTimeoutMs = 20000;

    private const int MaxProxyReplyBytes = 8192;

    private readonly HttpClient _http;

    public Network()
    {
        _http = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    /// <summary>The <see cref="Core.TextFetcher"/> for Radio Browser.</summary>
    public Task<string> FetchText(Uri uri, CancellationToken cancel) => _http.GetStringAsync(uri, cancel);

    /// <summary>The <see cref="Core.Connector"/> for streams. Runs on the stream's own thread.</summary>
    public Stream Connect(Uri uri, CancellationToken cancel)
    {
        var proxy = ProxyFor(uri);
        var stream = proxy == null ? Open(uri.IdnHost, uri.Port, cancel) : OpenThroughProxy(proxy, uri, cancel);

        if (uri.Scheme != Uri.UriSchemeHttps) return stream;

        try
        {
            return Secure(stream, uri.IdnHost, cancel);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public void Dispose() => _http.Dispose();

    private static Uri? ProxyFor(Uri uri)
    {
        var proxy = HttpClient.DefaultProxy;

        if (proxy.IsBypassed(uri)) return null;

        var found = proxy.GetProxy(uri);

        return found == null || found == uri ? null : found;
    }

    private static Stream Open(string host, int port, CancellationToken cancel)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timeout.CancelAfter(ConnectTime);
            socket.ConnectAsync(host, port, timeout.Token).AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            socket.Dispose();
            throw new IOException($"{host}:{port} didn't answer in {ConnectTime.TotalSeconds} seconds");
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new NetworkStream(socket, ownsSocket: true) { ReadTimeout = ReadTimeoutMs, WriteTimeout = ReadTimeoutMs };
    }

    /// <summary>Asks the proxy for a tunnel to the station with CONNECT, then talks to the station through it.</summary>
    private static Stream OpenThroughProxy(Uri proxy, Uri target, CancellationToken cancel)
    {
        var stream = Open(proxy.IdnHost, proxy.Port, cancel);

        try
        {
            var host = target.HostNameType == UriHostNameType.IPv6 ? $"[{target.IdnHost}]:{target.Port}" : $"{target.IdnHost}:{target.Port}";
            var request = Encoding.ASCII.GetBytes($"CONNECT {host} HTTP/1.1\r\nHost: {host}\r\nUser-Agent: {UserAgent}\r\n\r\n");
            stream.Write(request);
            var status = ReadProxyStatus(stream);

            if (!status.StartsWith("HTTP/1.", StringComparison.Ordinal) || status.Length < 12 || status[9] != '2')
                throw new IOException($"The proxy wouldn't connect to {host}: {status}");

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Reads the proxy's answer up to the blank line, byte by byte so nothing of the station's is read with it.</summary>
    private static string ReadProxyStatus(Stream stream)
    {
        var reply = new StringBuilder();
        var one = new byte[1];

        while (reply.Length < MaxProxyReplyBytes)
        {
            if (stream.Read(one, 0, 1) == 0) throw new IOException("The proxy closed the connection");

            reply.Append((char)one[0]);

            if (reply.Length >= 4 && reply[^1] == '\n' && reply[^2] == '\r' && reply[^3] == '\n' && reply[^4] == '\r') break;
        }

        var text = reply.ToString();
        var end = text.IndexOf('\r');

        return end < 0 ? text : text.Substring(0, end);
    }

    private static Stream Secure(Stream stream, string host, CancellationToken cancel)
    {
        var tls = new SslStream(stream, leaveInnerStreamOpen: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(ConnectTime);

        try
        {
            tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new IOException($"{host} didn't finish the secure connection in {ConnectTime.TotalSeconds} seconds");
        }

        return tls;
    }
}
