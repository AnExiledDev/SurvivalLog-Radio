using System.Text;
using Radio.Core;
using Xunit;

namespace Radio.Tests;

public class IcyStreamTests
{
    private const int Interval = 16;

    private static byte[] Audio(int seed, int length = Interval) => Enumerable.Range(seed, length).Select(i => (byte)i).ToArray();

    /// <summary>One metadata block: the length byte, then the text padded with NULs to a multiple of 16.</summary>
    private static byte[] Meta(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var blocks = (bytes.Length + 15) / 16;
        var block = new byte[1 + blocks * 16];
        block[0] = (byte)blocks;
        bytes.CopyTo(block, 1);

        return block;
    }

    private static (byte[] Audio, List<string> Titles) ReadAll(byte[] wire, int readSize)
    {
        var titles = new List<string>();
        using var icy = new IcyStream(new MemoryStream(wire), Interval, titles.Add);
        var audio = new MemoryStream();
        var buffer = new byte[readSize];
        int read;

        while ((read = icy.Read(buffer, 0, buffer.Length)) > 0) audio.Write(buffer, 0, read);

        return (audio.ToArray(), titles);
    }

    private static byte[] Wire(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public void TheAudioComesOutWithoutTheTitlesInIt(int readSize)
    {
        var wire = Wire(Audio(0), Meta("StreamTitle='A';"), Audio(20), Meta("StreamTitle='A';"), Audio(40), new byte[] { 0 }, Audio(60), Meta("StreamTitle='B';"), Audio(80, 5));

        var (audio, _) = ReadAll(wire, readSize);

        Assert.Equal(Wire(Audio(0), Audio(20), Audio(40), Audio(60), Audio(80, 5)), audio);
    }

    [Fact]
    public void EachTitleIsToldOnceUntilItChanges()
    {
        var wire = Wire(Audio(0), Meta("StreamTitle='A';"), Audio(20), Meta("StreamTitle='A';"), Audio(40), new byte[] { 0 }, Audio(60), Meta("StreamTitle='B';"), Audio(80));

        var (_, titles) = ReadAll(wire, 4096);

        Assert.Equal(new[] { "A", "B" }, titles);
    }

    [Fact]
    public void AStreamThatEndsWhereTheTitleShouldBeJustEnds()
    {
        var wire = Wire(Audio(0), new byte[] { 2 }, Encoding.ASCII.GetBytes("StreamTi"));

        var (audio, titles) = ReadAll(wire, 4096);

        Assert.Equal(Audio(0), audio);
        Assert.Empty(titles);
    }
}

public class IcyTitleTests
{
    private static string? Title(string text) => IcyStream.ParseTitle(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void TheTitleIsTheTextBetweenItsQuotes()
    {
        Assert.Equal("Artist - Song", Title("StreamTitle='Artist - Song';StreamUrl='http://radio.example';"));
    }

    [Fact]
    public void AnApostropheInsideTheTitleIsKept()
    {
        Assert.Equal("Don't Stop", Title("StreamTitle='Don't Stop';"));
    }

    [Fact]
    public void NulPaddingAndSpacesAreDropped()
    {
        Assert.Equal("Song", IcyStream.ParseTitle(Encoding.ASCII.GetBytes("StreamTitle=' Song ';\0\0\0\0\0\0")));
    }

    [Fact]
    public void ATitleMissingItsEndRunsToTheEndOfTheBlock()
    {
        Assert.Equal("Cut off", Title("StreamTitle='Cut off"));
    }

    [Fact]
    public void ABlockWithoutATitleGivesNothing()
    {
        Assert.Null(Title("StreamUrl='http://radio.example';"));
    }
}

public class TextDecodingTests
{
    private const string Chinese = "中文电台";

    [Fact]
    public void Utf8TextIsReadAsUtf8()
    {
        Assert.Equal(Chinese + " FM", TextDecoding.Decode(Encoding.UTF8.GetBytes(Chinese + " FM")));
    }

    [Fact]
    public void ChineseServersThatSendGbkAreRead()
    {
        var gbk = CodePagesEncodingProvider.Instance.GetEncoding(54936)!.GetBytes(Chinese + " FM");

        Assert.Equal(Chinese + " FM", TextDecoding.Decode(gbk));
    }

    [Fact]
    public void OldWesternServersThatSendLatin1AreRead()
    {
        Assert.Equal("Caf\u00e9 Bar", TextDecoding.Decode(new byte[] { 0x43, 0x61, 0x66, 0xE9, 0x20, 0x42, 0x61, 0x72 }));
    }
}
