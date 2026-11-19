using Radio.Core;
using Xunit;

namespace Radio.Tests;

internal sealed class FrameList : IFrameSink, IByteSink
{
    private readonly FrameSplitter _splitter;

    public FrameList() => _splitter = new FrameSplitter(this);

    public List<(FrameFormat Format, byte[] Bytes)> Frames { get; } = new();

    public FrameSplitter Splitter => _splitter;

    public void Frame(FrameFormat format, ReadOnlySpan<byte> frame) => Frames.Add((format, frame.ToArray()));

    public void Bytes(ReadOnlySpan<byte> data) => _splitter.Bytes(data);

    public void Feed(byte[] data, int chunk)
    {
        for (var i = 0; i < data.Length; i += chunk) Bytes(data.AsSpan(i, Math.Min(chunk, data.Length - i)));
    }
}

public class FrameSplitterTests
{
    private static byte[] Mp3Frames()
    {
        var file = Samples.Read("tone.mp3");

        return file.AsSpan(Id3.TagLength(file)).ToArray();
    }

    [Fact]
    public void EveryMp3FrameOfAFileComesOutWithItsFormat()
    {
        var list = new FrameList();

        list.Feed(Mp3Frames(), 4096);

        // ffprobe counts 40 audio frames, and the Info frame in front of them is a frame too.
        Assert.Equal(41, list.Frames.Count);
        Assert.All(list.Frames, f => Assert.Equal("Mp3 44100 Hz, 2 ch", f.Format.ToString()));
        Assert.Equal(0, list.Splitter.SkippedBytes);
    }

    [Fact]
    public void Mp3FramesKeepTheirHeaderForTheDecoder()
    {
        var list = new FrameList();

        list.Feed(Mp3Frames(), 4096);

        Assert.All(list.Frames, f => Assert.Equal(0xFF, f.Bytes[0]));
        Assert.Equal(Mp3Frames().Length, list.Frames.Sum(f => f.Bytes.Length));
    }

    [Fact]
    public void AdtsHeadersAreCutOffAndTheAacConfigComesWithTheFrames()
    {
        var file = Samples.Read("tone.aac");
        var list = new FrameList();

        list.Feed(file, 4096);

        Assert.Equal(48, list.Frames.Count);
        Assert.All(list.Frames, f => Assert.Equal("Aac 48000 Hz, 2 ch", f.Format.ToString()));

        // AAC LC is object type 2, 48 kHz is rate index 3, and stereo is channel config 2.
        Assert.Equal(new byte[] { 0x11, 0x90 }, list.Frames[0].Format.Config);
        Assert.Equal(file.Length - 48 * 7, list.Frames.Sum(f => f.Bytes.Length));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(1000)]
    public void HowTheBytesArriveDoesNotChangeTheFrames(int chunk)
    {
        var whole = new FrameList();
        var pieces = new FrameList();

        whole.Feed(Mp3Frames(), int.MaxValue);
        pieces.Feed(Mp3Frames(), chunk);

        Assert.Equal(whole.Frames.Select(f => f.Bytes), pieces.Frames.Select(f => f.Bytes));
    }

    [Fact]
    public void NoiseBeforeTheFirstFrameIsSkipped()
    {
        var noise = new byte[3000];
        new Random(7).NextBytes(noise);

        // Lone sync bytes in the noise, each followed by something that is no frame.
        for (var i = 0; i < noise.Length; i++) noise[i] = (byte)(i % 300 == 0 ? 0xFF : noise[i] & 0x7F);

        var list = new FrameList();

        list.Feed(noise.Concat(Mp3Frames()).ToArray(), 4096);

        Assert.Equal(41, list.Frames.Count);
        Assert.Equal(noise.Length, list.Splitter.SkippedBytes);
    }

    [Fact]
    public void AGapInTheMiddleLosesNoFrameAfterIt()
    {
        var frames = Mp3Frames();
        var cut = new FrameList();
        cut.Feed(frames, int.MaxValue);
        var tenth = cut.Frames.Take(10).Sum(f => f.Bytes.Length);
        var withGap = frames.Take(tenth).Concat(new byte[100]).Concat(frames.Skip(tenth)).ToArray();
        var list = new FrameList();

        list.Feed(withGap, 4096);

        Assert.Equal(41, list.Frames.Count);
    }

    [Fact]
    public void OneLoneHeaderIsNotMistakenForAudio()
    {
        // A sync pattern inside a tag or junk, with no second frame after it.
        var fake = new byte[] { 0xFF, 0xFB, 0x50, 0x00 }.Concat(new byte[400]).ToArray();
        var list = new FrameList();

        list.Feed(fake, 4096);

        Assert.Empty(list.Frames);
        Assert.False(list.Splitter.IsLocked);
    }
}

public class HeaderTests
{
    [Fact]
    public void AnMpeg1Layer3HeaderGivesItsLengthRateAndChannels()
    {
        Assert.True(MpegHeader.TryRead(new byte[] { 0xFF, 0xFB, 0x50, 0x00 }, out var length, out var codec, out var rate, out var channels, out var layer));

        // 144 * 64 kbps / 44100 Hz, without padding.
        Assert.Equal(208, length);
        Assert.Equal(Codec.Mp3, codec);
        Assert.Equal(44100, rate);
        Assert.Equal(2, channels);
        Assert.Equal(3, layer);
    }

    [Fact]
    public void AnMpeg2MonoHeaderUsesHalfTheRate()
    {
        Assert.True(MpegHeader.TryRead(new byte[] { 0xFF, 0xF3, 0x92, 0xC0 }, out var length, out _, out var rate, out var channels, out _));

        // 72 * 80 kbps / 22050 Hz, plus one byte of padding.
        Assert.Equal(262, length);
        Assert.Equal(22050, rate);
        Assert.Equal(1, channels);
    }

    [Fact]
    public void Layer2IsItsOwnCodec()
    {
        Assert.True(MpegHeader.TryRead(new byte[] { 0xFF, 0xFD, 0x90, 0x00 }, out _, out var codec, out _, out _, out var layer));

        Assert.Equal(Codec.MpegLayer12, codec);
        Assert.Equal(2, layer);
    }

    [Theory]
    [InlineData(0xEB, 0x50)]
    [InlineData(0xFB, 0x00)]
    [InlineData(0xFB, 0xF0)]
    [InlineData(0xFB, 0x5C)]
    public void ReservedAndFreeFormatHeadersAreRefused(int second, int third)
    {
        Assert.False(MpegHeader.TryRead(new byte[] { 0xFF, (byte)second, (byte)third, 0x00 }, out _, out _, out _, out _, out _));
    }

    [Fact]
    public void AnAdtsHeaderGivesItsFrameLengthAndConfig()
    {
        var file = Samples.Read("tone.aac");

        Assert.True(AdtsHeader.TryRead(file, out var length, out var headerLength, out var rate, out var channels, out var config));

        Assert.Equal(7, headerLength);
        Assert.Equal(48000, rate);
        Assert.Equal(2, channels);
        Assert.Equal(0x1190, config);
        Assert.True(AdtsHeader.TryRead(file.AsSpan(length), out _, out _, out _, out _, out _));
    }

    [Fact]
    public void AnAdtsHeaderWithAChecksumIsNineBytes()
    {
        var header = new byte[] { 0xFF, 0xF0, 0x4C, 0x80, 0x20, 0x00, 0x00, 0x00, 0x00 };

        Assert.True(AdtsHeader.TryRead(header, out var length, out var headerLength, out _, out _, out _));

        Assert.Equal(9, headerLength);
        Assert.Equal(256, length);
    }

    [Fact]
    public void Id3TagLengthCountsTheHeaderAndAnyFooter()
    {
        var tag = new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 0x33 };
        var withFooter = (byte[])tag.Clone();
        withFooter[5] = 0x10;

        Assert.Equal(61, Id3.TagLength(tag));
        Assert.Equal(71, Id3.TagLength(withFooter));
        Assert.Equal(61, Id3.TagLength(Samples.Read("tone.mp3")));
    }

    [Fact]
    public void Id3TagLengthAsksForMoreBytesOnlyWhenATagMightStart()
    {
        Assert.Equal(-1, Id3.TagLength(ReadOnlySpan<byte>.Empty));
        Assert.Equal(-1, Id3.TagLength(new[] { (byte)'I' }));
        Assert.Equal(-1, Id3.TagLength(new[] { (byte)'I', (byte)'D', (byte)'3', (byte)3 }));
        Assert.Equal(0, Id3.TagLength(new[] { (byte)'Q' }));
        Assert.Equal(0, Id3.TagLength(new byte[] { 0xFF, 0xFB, 0x50, 0x00 }));
    }
}

public class TsDemuxTests
{
    [Fact]
    public void TheAacTrackOfATransportStreamComesOutAsFrames()
    {
        var list = new FrameList();
        var demux = new TsDemux(list);

        demux.Bytes(Samples.Read("tone.ts"));

        Assert.Equal(TsAudio.Playable, demux.Audio);
        Assert.Equal(0x0F, demux.StreamType);
        Assert.Equal(48, list.Frames.Count);
        Assert.All(list.Frames, f => Assert.Equal("Aac 48000 Hz, 2 ch", f.Format.ToString()));
    }

    [Fact]
    public void PacketsSplitAnywhereGiveTheSameFrames()
    {
        var whole = new FrameList();
        var pieces = new FrameList();
        var ts = Samples.Read("tone.ts");
        var demux = new TsDemux(pieces);

        new TsDemux(whole).Bytes(ts);

        for (var i = 0; i < ts.Length; i += 100) demux.Bytes(ts.AsSpan(i, Math.Min(100, ts.Length - i)));

        Assert.Equal(whole.Frames.Select(f => f.Bytes), pieces.Frames.Select(f => f.Bytes));
    }

    [Fact]
    public void AnAc3TrackIsReportedAsUnsupported()
    {
        var list = new FrameList();
        var demux = new TsDemux(list);

        demux.Bytes(Samples.Read("ac3.ts"));

        Assert.Equal(TsAudio.Unsupported, demux.Audio);
        Assert.Equal(0x81, demux.StreamType);
        Assert.Empty(list.Frames);
    }

    [Fact]
    public void ATransportStreamIsKnownByItsSyncBytes()
    {
        var ts = Samples.Read("tone.ts");

        Assert.True(TsDemux.LooksLikeTs(ts));
        Assert.False(TsDemux.LooksLikeTs(ts.AsSpan(0, 376)));
        Assert.False(TsDemux.LooksLikeTs(ts.AsSpan(1)));
        Assert.False(TsDemux.LooksLikeTs(Samples.Read("tone.mp3")));
    }
}
