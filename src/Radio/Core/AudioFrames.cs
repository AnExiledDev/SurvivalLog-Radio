using System;

namespace Radio.Core;

public enum Codec
{
    /// <summary>MPEG audio layer 3.</summary>
    Mp3,

    /// <summary>MPEG audio layers 1 and 2, which Windows decodes with a different decoder than MP3.</summary>
    MpegLayer12,

    /// <summary>AAC, handed to the decoder as raw frames with the AudioSpecificConfig beside them.</summary>
    Aac,
}

/// <summary>What a run of frames is: the codec, its rate and channels, and for AAC the 2-byte AudioSpecificConfig.</summary>
public sealed class FrameFormat
{
    public FrameFormat(Codec codec, int sampleRate, int channels, byte[] config)
    {
        Codec = codec;
        SampleRate = sampleRate;
        Channels = channels;
        Config = config;
    }

    public Codec Codec { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public byte[] Config { get; }

    public bool IsSameAs(FrameFormat? other) =>
        other != null && other.Codec == Codec && other.SampleRate == SampleRate && other.Channels == Channels && other.Config.AsSpan().SequenceEqual(Config);

    public override string ToString() => $"{Codec} {SampleRate} Hz, {Channels} ch";
}

/// <summary>Where the bytes of a stream go next.</summary>
public interface IByteSink
{
    void Bytes(ReadOnlySpan<byte> data);
}

/// <summary>Where whole audio frames go: to the decoder.</summary>
public interface IFrameSink
{
    void Frame(FrameFormat format, ReadOnlySpan<byte> frame);
}

/// <summary>
/// Cuts a stream of MP3 or ADTS AAC bytes into frames. Which of the two it is comes from the bytes: it locks on once
/// two headers of the same kind sit back to back, so a stray sync pattern in a tag or in the middle of the audio
/// doesn't fool it, and it finds the next frame again after a gap. ADTS headers are cut off; the AAC decoder takes
/// raw frames.
/// </summary>
public sealed class FrameSplitter : IByteSink
{
    private const int MaxBuffered = 256 * 1024;
    private const int LongestHeader = 9;
    private static readonly Family[] Families = { Family.Mpeg, Family.Adts };

    private readonly IFrameSink _sink;
    private byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;
    private Family _locked;

    public FrameSplitter(IFrameSink sink) => _sink = sink;

    private enum Family
    {
        None,
        Mpeg,
        Adts,
    }

    /// <summary>True once it has found the frames; until then the stream may not be MP3 or AAC at all.</summary>
    public bool IsLocked => _locked != Family.None;

    /// <summary>How many bytes it has dropped while looking for frames; a lot of them means the stream is something else.</summary>
    public long SkippedBytes { get; private set; }

    public void Bytes(ReadOnlySpan<byte> data)
    {
        Append(data);
        Split();
    }

    /// <summary>A new segment or connection: what was half read belongs to the old one.</summary>
    public void Reset()
    {
        _start = 0;
        _end = 0;
        _locked = Family.None;
    }

    private void Split()
    {
        while (true)
        {
            var available = _end - _start;

            if (available < LongestHeader) return;

            var here = _buffer.AsSpan(_start, available);

            if (_locked == Family.None)
            {
                if (!TryLock(here)) return;

                continue;
            }

            if (!TryHeader(here, _locked, out var header))
            {
                _locked = Family.None;
                Skip(1);
                continue;
            }

            if (available < header.Length) return;

            _sink.Frame(header.Format, here.Slice(header.HeaderLength, header.Length - header.HeaderLength));
            _start += header.Length;
        }
    }

    /// <summary>Finds two back to back headers of one kind and drops what came before them. False when it needs more bytes.</summary>
    private bool TryLock(ReadOnlySpan<byte> here)
    {
        for (var i = 0; i + LongestHeader <= here.Length; i++)
        {
            if (here[i] != 0xFF) continue;

            foreach (var family in Families)
            {
                if (!TryHeader(here.Slice(i), family, out var first)) continue;

                var next = i + first.Length;

                if (next + LongestHeader > here.Length)
                {
                    // The first header looks right but the bytes after it haven't arrived yet.
                    Skip(i);
                    return false;
                }

                if (TryHeader(here.Slice(next), family, out var second) && second.Format.SampleRate == first.Format.SampleRate && second.Format.Codec == first.Format.Codec)
                {
                    Skip(i);
                    _locked = family;
                    return true;
                }
            }
        }

        Skip(Math.Max(0, here.Length - LongestHeader + 1));
        return false;
    }

    private void Skip(int count)
    {
        _start += count;
        SkippedBytes += count;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        if (_end + data.Length > _buffer.Length)
        {
            var kept = _end - _start;

            if (kept + data.Length > MaxBuffered)
            {
                // Nothing in here is a frame; keep only the tail a header could start in.
                var tail = Math.Min(kept, LongestHeader);
                SkippedBytes += kept - tail;
                _start = _end - tail;
                kept = tail;
            }

            if (kept + data.Length > _buffer.Length) Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, kept + data.Length));

            Buffer.BlockCopy(_buffer, _start, _buffer, 0, kept);
            _start = 0;
            _end = kept;
        }

        data.CopyTo(_buffer.AsSpan(_end));
        _end += data.Length;
    }

    private readonly struct Header
    {
        public Header(int length, int headerLength, FrameFormat format)
        {
            Length = length;
            HeaderLength = headerLength;
            Format = format;
        }

        public int Length { get; }

        /// <summary>How much of the front is header the decoder doesn't want: all of ADTS's, none of MPEG's.</summary>
        public int HeaderLength { get; }

        public FrameFormat Format { get; }
    }

    private FrameFormat? _lastMpeg;
    private FrameFormat? _lastAdts;

    private bool TryHeader(ReadOnlySpan<byte> h, Family family, out Header header)
    {
        header = default;

        if (family == Family.Mpeg)
        {
            if (!MpegHeader.TryRead(h, out var length, out var codec, out var rate, out var channels, out var layer)) return false;

            header = new Header(length, 0, Reuse(ref _lastMpeg, codec, rate, channels, layer, 1));
            return true;
        }

        if (!AdtsHeader.TryRead(h, out var adtsLength, out var headerLength, out var adtsRate, out var adtsChannels, out var config)) return false;

        header = new Header(adtsLength, headerLength, Reuse(ref _lastAdts, Codec.Aac, adtsRate, adtsChannels, config, 2));
        return true;
    }

    /// <summary>
    /// The same format object for every frame of a run, so the decoder can tell a change by reference and nothing is
    /// made per frame. The config is the MPEG layer as one byte, or AAC's 2-byte AudioSpecificConfig.
    /// </summary>
    private static FrameFormat Reuse(ref FrameFormat? last, Codec codec, int rate, int channels, int config, int configLength)
    {
        if (last != null && last.Codec == codec && last.SampleRate == rate && last.Channels == channels && last.Config.Length == configLength && ConfigValue(last.Config) == config) return last;

        var bytes = configLength == 1 ? new[] { (byte)config } : new[] { (byte)(config >> 8), (byte)config };
        last = new FrameFormat(codec, rate, channels, bytes);
        return last;
    }

    private static int ConfigValue(byte[] config) => config.Length == 1 ? config[0] : (config[0] << 8) | config[1];
}

/// <summary>The 4-byte MPEG audio frame header.</summary>
public static class MpegHeader
{
    private static readonly int[] V1L1 = { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448 };
    private static readonly int[] V1L2 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384 };
    private static readonly int[] V1L3 = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320 };
    private static readonly int[] V2L1 = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256 };
    private static readonly int[] V2L23 = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160 };
    private static readonly int[] Rates1 = { 44100, 48000, 32000 };

    public static bool TryRead(ReadOnlySpan<byte> h, out int length, out Codec codec, out int sampleRate, out int channels, out int layer)
    {
        length = 0;
        codec = Codec.Mp3;
        sampleRate = 0;
        channels = 0;
        layer = 0;

        if (h.Length < 4 || h[0] != 0xFF || (h[1] & 0xE0) != 0xE0) return false;

        var version = (h[1] >> 3) & 3;
        var layerBits = (h[1] >> 1) & 3;
        var bitrateIndex = h[2] >> 4;
        var rateIndex = (h[2] >> 2) & 3;

        // Version 1 is reserved, layer bits 0 are ADTS's, bitrate 0 is free format and 15 is invalid, rate 3 is reserved.
        if (version == 1 || layerBits == 0 || bitrateIndex is 0 or 15 || rateIndex == 3) return false;

        layer = 4 - layerBits;
        var isVersion1 = version == 3;
        var table = (isVersion1, layer) switch
        {
            (true, 1) => V1L1,
            (true, 2) => V1L2,
            (true, _) => V1L3,
            (false, 1) => V2L1,
            _ => V2L23,
        };
        var bitrate = table[bitrateIndex] * 1000;
        sampleRate = Rates1[rateIndex] >> (version switch { 3 => 0, 2 => 1, _ => 2 });
        var padding = (h[2] >> 1) & 1;
        length = layer switch
        {
            1 => (12 * bitrate / sampleRate + padding) * 4,
            2 => 144 * bitrate / sampleRate + padding,
            _ => (isVersion1 ? 144 : 72) * bitrate / sampleRate + padding,
        };
        channels = (h[3] >> 6) == 3 ? 1 : 2;
        codec = layer == 3 ? Codec.Mp3 : Codec.MpegLayer12;

        return length >= 4;
    }
}

/// <summary>The 7-byte (9 with a checksum) ADTS header in front of each AAC frame.</summary>
public static class AdtsHeader
{
    public static readonly int[] Rates = { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };

    /// <summary>Reads one header. The config is the 2-byte AudioSpecificConfig as a number, high byte first.</summary>
    public static bool TryRead(ReadOnlySpan<byte> h, out int length, out int headerLength, out int sampleRate, out int channels, out int config)
    {
        length = 0;
        headerLength = 0;
        sampleRate = 0;
        channels = 0;
        config = 0;

        if (h.Length < 7 || h[0] != 0xFF || (h[1] & 0xF6) != 0xF0) return false;

        var profile = (h[2] >> 6) & 3;
        var rateIndex = (h[2] >> 2) & 0xF;
        var channelConfig = ((h[2] & 1) << 2) | (h[3] >> 6);

        if (rateIndex >= Rates.Length || profile == 3) return false;

        headerLength = (h[1] & 1) == 1 ? 7 : 9;
        length = ((h[3] & 3) << 11) | (h[4] << 3) | (h[5] >> 5);

        if (length <= headerLength) return false;

        sampleRate = Rates[rateIndex];
        channels = channelConfig == 0 ? 2 : channelConfig;

        // AudioSpecificConfig: 5 bits object type (the ADTS profile plus one), 4 bits rate index, 4 bits channels.
        config = ((profile + 1) << 11) | (rateIndex << 7) | (channelConfig << 3);

        return true;
    }
}

/// <summary>ID3v2 tags: a stream or an HLS audio segment may start with one.</summary>
public static class Id3
{
    /// <summary>How many bytes the tag at the start takes, 0 when there is none, -1 when more bytes are needed to tell.</summary>
    public static int TagLength(ReadOnlySpan<byte> data)
    {
        if (data.Length < 3) return data.Length == 0 || data[0] == 'I' ? -1 : 0;

        if (data[0] != 'I' || data[1] != 'D' || data[2] != '3') return 0;

        if (data.Length < 10) return -1;

        var size = (data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F);
        var hasFooter = (data[5] & 0x10) != 0;

        return 10 + size + (hasFooter ? 10 : 0);
    }
}
