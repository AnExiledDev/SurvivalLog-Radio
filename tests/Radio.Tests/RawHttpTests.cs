using System.Text;
using Radio.Core;
using Xunit;

namespace Radio.Tests;

public class RawHttpTests
{
    private readonly FakeServer _server = new();

    private HttpReply Get(string url, bool askForTitles = false) => RawHttp.Get(_server.Connect, new Uri(url), "Radio/test", askForTitles, CancellationToken.None);

    private static string Body(HttpReply reply) => Encoding.UTF8.GetString(RawHttp.ReadAll(reply, 1 << 20));

    [Fact]
    public void TheRequestAsksForTitlesAndNamesAPortThatIsNotTheDefault()
    {
        _server.Answer("http://radio.example:8000/live?id=3", FakeServer.Http(200, "", ""));

        using var reply = Get("http://radio.example:8000/live?id=3", askForTitles: true);
        var request = _server.LastConnection.Request;

        Assert.StartsWith("GET /live?id=3 HTTP/1.1\r\n", request);
        Assert.Contains("Host: radio.example:8000\r\n", request);
        Assert.Contains("Icy-MetaData: 1\r\n", request);
        Assert.Contains("User-Agent: Radio/test\r\n", request);
        Assert.EndsWith("\r\n\r\n", request);
    }

    [Fact]
    public void APlainRequestLeavesTheDefaultPortAndTitlesOut()
    {
        _server.Answer("https://radio.example/live", FakeServer.Http(200, "", ""));

        using var reply = Get("https://radio.example/live");
        var request = _server.LastConnection.Request;

        Assert.Contains("Host: radio.example\r\n", request);
        Assert.DoesNotContain("Icy-MetaData", request);
    }

    [Fact]
    public void AChunkedBodyIsPutBackTogether()
    {
        _server.Answer("http://radio.example/list", FakeServer.Http(200, "Transfer-Encoding: chunked\r\n", "4\r\nWiki\r\n5;note=x\r\npedia\r\n0\r\n\r\n"));

        using var reply = Get("http://radio.example/list");

        Assert.Equal("Wikipedia", Body(reply));
    }

    [Fact]
    public void ABodyStopsAtItsContentLength()
    {
        _server.Answer("http://radio.example/list", FakeServer.Http(200, "Content-Length: 5\r\n", "Hello and more"));

        using var reply = Get("http://radio.example/list");

        Assert.Equal("Hello", Body(reply));
    }

    [Fact]
    public void ABodyCutShortOfItsContentLengthIsAnError()
    {
        _server.Answer("http://radio.example/list", FakeServer.Http(200, "Content-Length: 50\r\n", "Hello"));

        using var reply = Get("http://radio.example/list");

        Assert.Throws<IOException>(() => Body(reply));
    }

    [Fact]
    public void RedirectsAreFollowedAndTheReplyKnowsWhereItEndedUp()
    {
        _server.Answer("http://radio.example/old", FakeServer.Http(302, "Location: /new/live\r\n", ""));
        _server.Answer("http://radio.example/new/live", FakeServer.Http(301, "Location: https://cdn.example/live\r\n", ""));
        _server.Answer("https://cdn.example/live", FakeServer.Http(200, "Content-Length: 2\r\n", "ok"));

        using var reply = Get("http://radio.example/old");

        Assert.Equal("https://cdn.example/live", reply.Url.AbsoluteUri);
        Assert.Equal("ok", Body(reply));
    }

    [Fact]
    public void ARedirectLoopGivesUp()
    {
        _server.Answer("http://radio.example/loop", FakeServer.Http(302, "Location: /loop\r\n", ""));

        var failure = Assert.Throws<HttpFailure>(() => Get("http://radio.example/loop"));

        Assert.Equal(302, failure.Status);
        Assert.Equal(RawHttp.MaxRedirects + 1, _server.Asked.Count);
    }

    [Fact]
    public void ShoutcastsIcyStatusLineIsAccepted()
    {
        _server.Answer("http://radio.example/live", FakeServer.Ascii("ICY 200 OK\r\nicy-metaint: 8192\r\nContent-Type: audio/aacp\r\n\r\nxyz"));

        using var reply = Get("http://radio.example/live");

        Assert.Equal(200, reply.Status);
        Assert.Equal("8192", reply.Header("Icy-MetaInt"));
        Assert.Equal("audio/aacp", reply.ContentType);
    }

    [Fact]
    public void LinesEndingInABareLineFeedAreRead()
    {
        _server.Answer("http://radio.example/live", FakeServer.Ascii("HTTP/1.0 200 OK\nContent-Length: 2\n\nhi"));

        using var reply = Get("http://radio.example/live");

        Assert.Equal("hi", Body(reply));
    }

    [Fact]
    public void TheContentTypeIsTheMediaTypeAloneInLowerCase()
    {
        _server.Answer("http://radio.example/live", FakeServer.Http(200, "Content-Type: Audio/MPEG; charset=x\r\n", ""));

        using var reply = Get("http://radio.example/live");

        Assert.Equal("audio/mpeg", reply.ContentType);
    }

    [Fact]
    public void AnErrorStatusFailsWithItsNumberAndClosesTheConnection()
    {
        _server.Answer("http://radio.example/live", FakeServer.Http(500, "", "oops"));

        var failure = Assert.Throws<HttpFailure>(() => Get("http://radio.example/live"));

        Assert.Equal(500, failure.Status);
        Assert.True(_server.LastConnection.IsClosed);
    }

    [Fact]
    public void ALinkThatIsNotHttpIsRefusedWithoutConnecting()
    {
        var failure = Assert.Throws<HttpFailure>(() => Get("ftp://radio.example/live.mp3"));

        Assert.Equal(0, failure.Status);
        Assert.Empty(_server.Asked);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SSH-2.0-OpenSSH_9.6\r\n")]
    public void AServerThatDoesNotSpeakHttpFails(string answer)
    {
        _server.Answer("http://radio.example/live", FakeServer.Ascii(answer));

        var failure = Assert.Throws<HttpFailure>(() => Get("http://radio.example/live"));

        Assert.Equal(0, failure.Status);
    }

    [Fact]
    public void ReadAllRefusesAnAnswerThatIsTooLarge()
    {
        _server.Answer("http://radio.example/list", FakeServer.Http(200, "", new byte[5000]));

        using var reply = Get("http://radio.example/list");

        Assert.Throws<HttpFailure>(() => RawHttp.ReadAll(reply, 4096));
    }
}
