using System;
using System.Security.Cryptography;
using System.Text;

namespace Radio.Core;

/// <summary>What the first bytes of an answer turned out to be.</summary>
public enum BodyKind
{
    /// <summary>MP3 or AAC frames, maybe after an ID3 tag.</summary>
    Frames,

    /// <summary>An MPEG transport stream.</summary>
    Ts,

    /// <summary>An HLS playlist.</summary>
    Hls,

    /// <summary>A .pls or plain .m3u file that points at the real stream.</summary>
    LinkList,

    /// <summary>A web page, not a stream.</summary>
    WebPage,

    /// <summary>Audio in a format the stream reader can't split itself, like Ogg, FLAC or MP4.</summary>
    OtherAudio,
}

public static class Sniff
{
    /// <summary>
    /// Decides what an answer is from its content type, the end of its link and its first bytes. The bytes win when
    /// they are clear, because many servers send the wrong type.
    /// </summary>
    public static BodyKind Kind(string contentType, Uri url, ReadOnlySpan<byte> head)
    {
        var path = url.AbsolutePath.ToLowerInvariant();
        var withoutBom = head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF ? head.Slice(3) : head;
        var text = Encoding.ASCII.GetString(withoutBom.Slice(0, Math.Min(withoutBom.Length, 512))).TrimStart(' ', '\r', '\n', '\t');

        if (TsDemux.LooksLikeTs(head)) return BodyKind.Ts;

        // HLS lists carry #EXT-X- tags near the top. An .m3u that only has #EXTINF lines is a plain list of links, even
        // when it comes as audio/x-mpegurl, which servers send for both.
        if (text.StartsWith("#EXTM3U", StringComparison.Ordinal))
            return text.IndexOf("#EXT-X-", StringComparison.Ordinal) >= 0 || contentType.Contains("vnd.apple.mpegurl") ? BodyKind.Hls : BodyKind.LinkList;

        if (text.StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase)) return BodyKind.LinkList;

        if (text.StartsWith("<", StringComparison.Ordinal)) return BodyKind.WebPage;

        if (StartsWith(head, "OggS") || StartsWith(head, "fLaC") || (head.Length >= 8 && StartsWith(head.Slice(4), "ftyp")) || StartsWith(head, "RIFF") || IsAsf(head)) return BodyKind.OtherAudio;

        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0) return BodyKind.Frames;

        if (StartsWith(head, "ID3")) return BodyKind.Frames;

        // Every HLS list starts with #EXTM3U, so a list without it can only be a list of links.
        if (contentType.Contains("mpegurl") || contentType.Contains("scpls") || path.EndsWith(".m3u8", StringComparison.Ordinal) || path.EndsWith(".m3u", StringComparison.Ordinal) || path.EndsWith(".pls", StringComparison.Ordinal))
            return BodyKind.LinkList;

        if (contentType.StartsWith("text/html", StringComparison.Ordinal)) return BodyKind.WebPage;

        if (contentType.Contains("mp2t")) return BodyKind.Ts;

        if (contentType.Contains("ogg") || contentType.Contains("opus") || contentType.Contains("flac") || contentType.Contains("mp4") || contentType.Contains("wav")) return BodyKind.OtherAudio;

        return BodyKind.Frames;
    }

    /// <summary>A short name for audio that goes to the system decoder, from its first bytes or its type. Null when neither says.</summary>
    public static string? AudioName(string contentType, ReadOnlySpan<byte> head)
    {
        contentType = contentType.ToLowerInvariant();

        if (contentType.Contains("opus") || (StartsWith(head, "OggS") && head.IndexOf(OpusHead) >= 0)) return "Opus";

        if (StartsWith(head, "OggS") || contentType.Contains("ogg")) return "Ogg";

        if (StartsWith(head, "fLaC") || contentType.Contains("flac")) return "FLAC";

        if ((head.Length >= 8 && StartsWith(head.Slice(4), "ftyp")) || contentType.Contains("mp4")) return "MP4";

        if (StartsWith(head, "RIFF") || contentType.Contains("wav")) return "WAV";

        if (IsAsf(head)) return "WMA";

        return null;
    }

    /// <summary>An Ogg stream of Opus says so in its first page.</summary>
    private static ReadOnlySpan<byte> OpusHead => "OpusHead"u8;

    /// <summary>WMA and other ASF files start with this GUID.</summary>
    private static bool IsAsf(ReadOnlySpan<byte> head) => head.Length >= 4 && head[0] == 0x30 && head[1] == 0x26 && head[2] == 0xB2 && head[3] == 0x75;

    private static bool StartsWith(ReadOnlySpan<byte> data, string ascii)
    {
        if (data.Length < ascii.Length) return false;

        for (var i = 0; i < ascii.Length; i++)
        {
            if (data[i] != (byte)ascii[i]) return false;
        }

        return true;
    }
}

/// <summary>.pls and .m3u files: a station's link that is a list of its real stream links.</summary>
public static class LinkList
{
    /// <summary>The first http or https link in the list, or null when it has none.</summary>
    public static Uri? FirstLink(string text, Uri baseUri)
    {
        foreach (var raw in Hls.Lines(text))
        {
            var line = raw.Trim();

            if (line.Length == 0 || line[0] == '#' || line[0] == '[') continue;

            var equals = line.IndexOf('=');
            var scheme = line.IndexOf("://", StringComparison.Ordinal);

            if (equals > 0 && (scheme < 0 || equals < scheme))
            {
                // PLS writes "File1=http://..."; Title1=, Length1= and NumberOfEntries= hold no link.
                if (!line.StartsWith("File", StringComparison.OrdinalIgnoreCase)) continue;

                line = line.Substring(equals + 1).Trim();
            }

            if (Uri.TryCreate(baseUri, line, out var link) && (link.Scheme == Uri.UriSchemeHttp || link.Scheme == Uri.UriSchemeHttps)) return link;
        }

        return null;
    }
}

/// <summary>Turns HLS AES-128 segments back into plain ones.</summary>
public static class SegmentCrypto
{
    public static byte[] Decrypt(byte[] data, byte[] key, byte[]? iv, long sequence)
    {
        using var aes = Aes.Create();
        aes.Key = key;

        return aes.DecryptCbc(data, iv ?? SequenceIv(sequence), PaddingMode.PKCS7);
    }

    /// <summary>When the playlist gives no IV, it is the segment's sequence number as a 16-byte big-endian number.</summary>
    public static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];

        for (var i = 0; i < 8; i++) iv[15 - i] = (byte)(sequence >> (8 * i));

        return iv;
    }
}
