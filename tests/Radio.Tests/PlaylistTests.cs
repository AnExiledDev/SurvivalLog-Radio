using System.Security.Cryptography;
using System.Text;
using Radio.Core;
using Xunit;

namespace Radio.Tests;

public class SniffTests
{
    private static readonly Uri Plain = new("http://radio.example/stream");

    private static BodyKind Kind(string head, string contentType = "audio/mpeg", string url = "http://radio.example/stream") =>
        Sniff.Kind(contentType, new Uri(url), Encoding.ASCII.GetBytes(head));

    private static BodyKind Kind(byte[] head, string contentType = "audio/mpeg") => Sniff.Kind(contentType, Plain, head);

    [Fact]
    public void ATransportStreamIsKnownByItsBytes()
    {
        Assert.Equal(BodyKind.Ts, Kind(Samples.Read("tone.ts"), "text/html"));
    }

    [Fact]
    public void AnM3uWithHlsTagsIsAStreamAndOneWithoutIsAListOfLinks()
    {
        Assert.Equal(BodyKind.Hls, Kind("#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\na.ts\n"));
        Assert.Equal(BodyKind.LinkList, Kind("#EXTM3U\n#EXTINF:-1,Station\nhttp://radio.example/live\n"));
        Assert.Equal(BodyKind.LinkList, Kind("#EXTM3U\n#EXTINF:-1,Station\nhttp://radio.example/live\n", "audio/x-mpegurl"));
        Assert.Equal(BodyKind.LinkList, Kind("http://radio.example/live\n", "audio/x-mpegurl"));
        Assert.Equal(BodyKind.Hls, Kind("#EXTM3U\n#EXTINF:6,\na.ts\n", "application/vnd.apple.mpegurl"));
    }

    [Fact]
    public void APlsFileIsAListOfLinks()
    {
        Assert.Equal(BodyKind.LinkList, Kind("[playlist]\nFile1=http://radio.example/live\n"));
        Assert.Equal(BodyKind.LinkList, Kind("\r\n[Playlist]\nFile1=http://radio.example/live\n"));
    }

    [Fact]
    public void AWebPageIsKnownEvenAfterAByteOrderMark()
    {
        Assert.Equal(BodyKind.WebPage, Kind("<!DOCTYPE html><html>", "audio/mpeg"));
        Assert.Equal(BodyKind.WebPage, Kind(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.ASCII.GetBytes("<html>")).ToArray()));
    }

    [Theory]
    [InlineData("OggS")]
    [InlineData("fLaC")]
    [InlineData("\0\0\0 ftypM4A ")]
    [InlineData("RIFF\0\0\0\0WAVE")]
    public void AudioTheReaderCannotSplitGoesToTheSystemDecoder(string head)
    {
        Assert.Equal(BodyKind.OtherAudio, Kind(head));
    }

    [Fact]
    public void WmaIsKnownByItsAsfHeader()
    {
        Assert.Equal(BodyKind.OtherAudio, Kind(new byte[] { 0x30, 0x26, 0xB2, 0x75, 0x8E, 0x66, 0xCF, 0x11 }));
    }

    [Fact]
    public void Mp3BytesWinOverAWrongContentType()
    {
        Assert.Equal(BodyKind.Frames, Kind(new byte[] { 0xFF, 0xFB, 0x50, 0x00 }, "text/html"));
        Assert.Equal(BodyKind.Frames, Kind(Samples.Read("tone.mp3"), "application/octet-stream"));
    }

    [Theory]
    [InlineData("application/vnd.apple.mpegurl", "http://radio.example/stream", BodyKind.LinkList)]
    [InlineData("application/octet-stream", "http://radio.example/live/INDEX.M3U8", BodyKind.LinkList)]
    [InlineData("audio/x-scpls", "http://radio.example/stream", BodyKind.LinkList)]
    [InlineData("application/octet-stream", "http://radio.example/listen.pls", BodyKind.LinkList)]
    [InlineData("application/octet-stream", "http://radio.example/listen.m3u", BodyKind.LinkList)]
    [InlineData("text/html; charset=utf-8", "http://radio.example/stream", BodyKind.WebPage)]
    [InlineData("video/mp2t", "http://radio.example/stream", BodyKind.Ts)]
    [InlineData("audio/ogg", "http://radio.example/stream", BodyKind.OtherAudio)]
    [InlineData("audio/aacp", "http://radio.example/stream", BodyKind.Frames)]
    [InlineData("", "http://radio.example/stream", BodyKind.Frames)]
    public void WhenTheBytesSayNothingTheTypeAndLinkDecide(string contentType, string url, BodyKind kind)
    {
        Assert.Equal(kind, Kind("\0\0\0\0\0\0\0\0", contentType, url));
    }
}

public class LinkListTests
{
    private static readonly Uri Base = new("http://radio.example/lists/station.pls");

    [Fact]
    public void APlsGivesItsFirstFileLink()
    {
        var pls = "[playlist]\r\nNumberOfEntries=2\r\nFile1=http://a.example:8000/live\r\nTitle1=A\r\nLength1=-1\r\nFile2=http://b.example/live\r\n";

        Assert.Equal("http://a.example:8000/live", LinkList.FirstLink(pls, Base)?.AbsoluteUri);
    }

    [Fact]
    public void AnM3uGivesItsFirstLink()
    {
        var m3u = "#EXTM3U\n#EXTINF:-1,Station\nhttps://cdn.example/live.mp3\nhttps://backup.example/live.mp3\n";

        Assert.Equal("https://cdn.example/live.mp3", LinkList.FirstLink(m3u, Base)?.AbsoluteUri);
    }

    [Fact]
    public void ALinkWithAQueryIsKeptWhole()
    {
        Assert.Equal("http://a.example/play?id=7&type=mp3", LinkList.FirstLink("http://a.example/play?id=7&type=mp3\n", Base)?.AbsoluteUri);
    }

    [Theory]
    [InlineData("live.mp3", "http://radio.example/lists/live.mp3")]
    [InlineData("files/live.mp3", "http://radio.example/lists/files/live.mp3")]
    [InlineData("File1=../live.mp3", "http://radio.example/live.mp3")]
    public void RelativeLinksAreReadFromTheListsOwnLink(string line, string expected)
    {
        Assert.Equal(expected, LinkList.FirstLink(line + "\n", Base)?.AbsoluteUri);
    }

    [Fact]
    public void LinksThatAreNotHttpAreSkipped()
    {
        Assert.Equal("http://ok.example/live", LinkList.FirstLink("mms://old.example/live\nrtsp://old.example/live\nhttp://ok.example/live\n", Base)?.AbsoluteUri);
    }

    [Fact]
    public void AListWithNoLinksGivesNothing()
    {
        Assert.Null(LinkList.FirstLink("[playlist]\nNumberOfEntries=0\nVersion=2\n", Base));
        Assert.Null(LinkList.FirstLink("", Base));
    }
}

public class SegmentCryptoTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
    private static readonly byte[] Plain = Encoding.ASCII.GetBytes("segment bytes that do not fill a whole block");

    private static byte[] Encrypt(byte[] iv)
    {
        using var aes = Aes.Create();
        aes.Key = Key;

        return aes.EncryptCbc(Plain, iv, PaddingMode.PKCS7);
    }

    [Fact]
    public void ASegmentWithItsOwnIvDecrypts()
    {
        var iv = Enumerable.Range(100, 16).Select(i => (byte)i).ToArray();

        Assert.Equal(Plain, SegmentCrypto.Decrypt(Encrypt(iv), Key, iv, sequence: 99));
    }

    [Fact]
    public void WithoutAnIvTheSequenceNumberIsTheIv()
    {
        Assert.Equal(Plain, SegmentCrypto.Decrypt(Encrypt(SegmentCrypto.SequenceIv(4242)), Key, null, sequence: 4242));
    }

    [Fact]
    public void TheSequenceIvIsTheNumberBigEndianInSixteenBytes()
    {
        var iv = SegmentCrypto.SequenceIv(0x0102030405);

        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5 }, iv);
    }
}
