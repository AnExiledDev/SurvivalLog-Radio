using System;
using System.IO;
using System.Text;

namespace Radio.Core;

/// <summary>
/// A Shoutcast or Icecast stream with song titles mixed in. Every icy-metaint bytes of audio the server puts one
/// length byte (times 16) and that much text, like StreamTitle='Artist - Song';. This hands out the audio alone and
/// reports each new title.
/// </summary>
public sealed class IcyStream : ReadOnlyStream
{
    private readonly Stream _inner;
    private readonly int _metaInterval;
    private readonly Action<string> _onTitle;
    private readonly byte[] _meta = new byte[255 * 16];
    private int _audioLeft;
    private string? _lastTitle;

    public IcyStream(Stream inner, int metaInterval, Action<string> onTitle)
    {
        _inner = inner;
        _metaInterval = metaInterval;
        _audioLeft = metaInterval;
        _onTitle = onTitle;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (count == 0) return 0;

        if (_audioLeft == 0)
        {
            if (!ReadMeta()) return 0;

            _audioLeft = _metaInterval;
        }

        var read = _inner.Read(buffer, offset, Math.Min(count, _audioLeft));

        if (read > 0) _audioLeft -= read;

        return read;
    }

    /// <summary>False when the stream ended where the metadata should have been.</summary>
    private bool ReadMeta()
    {
        var lengthByte = _inner.ReadByte();

        if (lengthByte < 0) return false;

        var length = lengthByte * 16;

        if (length == 0) return true;

        if (!ReadExactly(_inner, _meta, length)) return false;

        var title = ParseTitle(_meta.AsSpan(0, length));

        if (title != null && title != _lastTitle)
        {
            _lastTitle = title;
            _onTitle(title);
        }

        return true;
    }

    /// <summary>The text between StreamTitle=' and ';, or null when the block has none.</summary>
    public static string? ParseTitle(ReadOnlySpan<byte> meta)
    {
        var end = meta.Length;

        while (end > 0 && meta[end - 1] == 0) end--;

        var text = TextDecoding.Decode(meta.Slice(0, end));
        const string Start = "StreamTitle='";
        var from = text.IndexOf(Start, StringComparison.Ordinal);

        if (from < 0) return null;

        from += Start.Length;
        var to = text.IndexOf("';", from, StringComparison.Ordinal);

        if (to < 0) to = text.LastIndexOf('\'');

        if (to < from) to = text.Length;

        return text.Substring(from, to - from).Trim();
    }

    internal static bool ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var have = 0;

        while (have < count)
        {
            var read = stream.Read(buffer, have, count - have);

            if (read <= 0) return false;

            have += read;
        }

        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();

        base.Dispose(disposing);
    }
}

/// <summary>
/// Text from a server that doesn't say its encoding. Most send UTF-8; many Chinese servers send GBK; old western ones
/// send Latin-1. Strict UTF-8 is tried first because GBK and Latin-1 bytes are almost never valid UTF-8.
/// </summary>
public static class TextDecoding
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Encoding? Chinese = FindChinese();

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
        }

        if (Chinese != null && LooksLikeGbk(bytes)) return Chinese.GetString(bytes);

        return Encoding.Latin1.GetString(bytes);
    }

    /// <summary>GBK writes each Chinese character as a lead byte 0x81-0xFE and a trail byte 0x40-0xFE.</summary>
    private static bool LooksLikeGbk(ReadOnlySpan<byte> bytes)
    {
        var pairs = 0;

        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] < 0x80) continue;

            if (bytes[i] == 0x80 || bytes[i] == 0xFF || i + 1 >= bytes.Length || bytes[i + 1] < 0x40 || bytes[i + 1] == 0x7F || bytes[i + 1] == 0xFF) return false;

            pairs++;
            i++;
        }

        return pairs > 0;
    }

    private static Encoding? FindChinese()
    {
        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(54936);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
