using System;
using System.Collections.Generic;
using System.Globalization;

namespace Radio.Core;

/// <summary>How one HLS segment is encrypted.</summary>
public sealed class SegmentKey
{
    public SegmentKey(Uri uri, byte[]? iv)
    {
        Uri = uri;
        Iv = iv;
    }

    public Uri Uri { get; }

    /// <summary>The IV the playlist gives, or null when the segment's sequence number is the IV.</summary>
    public byte[]? Iv { get; }
}

public sealed class Segment
{
    public Segment(Uri uri, long sequence, double seconds, SegmentKey? key, bool startsNewPart)
    {
        Uri = uri;
        Sequence = sequence;
        Seconds = seconds;
        Key = key;
        StartsNewPart = startsNewPart;
    }

    public Uri Uri { get; }

    public long Sequence { get; }

    public double Seconds { get; }

    /// <summary>Null when the segment isn't encrypted.</summary>
    public SegmentKey? Key { get; }

    /// <summary>The playlist put a discontinuity before it: the encoder may have changed.</summary>
    public bool StartsNewPart { get; }
}

/// <summary>A playlist of segments: a live window that moves, or a whole recording when it has an end.</summary>
public sealed class MediaPlaylist
{
    public MediaPlaylist(IReadOnlyList<Segment> segments, double targetSeconds, bool hasEnd, string? problem)
    {
        Segments = segments;
        TargetSeconds = targetSeconds;
        HasEnd = hasEnd;
        Problem = problem;
    }

    public IReadOnlyList<Segment> Segments { get; }

    public double TargetSeconds { get; }

    public bool HasEnd { get; }

    /// <summary>Why the segments can't be played, like SAMPLE-AES or fMP4, or null when they can.</summary>
    public string? Problem { get; }
}

/// <summary>HLS playlists: the master list of qualities, and the media list of segments.</summary>
public static class Hls
{
    private static readonly string[] VideoCodecs = { "avc1", "avc3", "hvc1", "hev1", "vp09", "av01" };

    /// <summary>True when an M3U text uses HLS tags, so it is a stream and not a plain list of links.</summary>
    public static bool IsPlaylist(string text) => text.TrimStart().StartsWith("#EXTM3U", StringComparison.Ordinal) && text.IndexOf("#EXT-X-", StringComparison.Ordinal) >= 0;

    /// <summary>True for a master playlist, the list of qualities that each have their own playlist.</summary>
    public static bool IsMaster(string text) => text.IndexOf("#EXT-X-STREAM-INF", StringComparison.Ordinal) >= 0;

    /// <summary>
    /// Picks which playlist of a master list to play. A separate audio track comes first, since the other choices
    /// carry video too. Then the best audio-only quality, and failing that the smallest one, which wastes the least
    /// on video the radio throws away.
    /// </summary>
    public static Uri? PickVariant(string text, Uri baseUri)
    {
        Uri? audioTrack = null;
        var audioTrackIsDefault = false;
        Uri? bestAudioOnly = null;
        var bestAudioOnlyBandwidth = -1L;
        Uri? smallest = null;
        var smallestBandwidth = long.MaxValue;
        Dictionary<string, string>? pendingVariant = null;

        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();

            if (line.Length == 0) continue;

            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var attributes = Attributes(line.Substring("#EXT-X-MEDIA:".Length));
                var isDefault = Get(attributes, "DEFAULT") == "YES";

                if (Get(attributes, "TYPE") == "AUDIO" && Get(attributes, "URI") is { } uri && (audioTrack == null || (isDefault && !audioTrackIsDefault)))
                {
                    audioTrack = new Uri(baseUri, uri);
                    audioTrackIsDefault = isDefault;
                }

                continue;
            }

            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                pendingVariant = Attributes(line.Substring("#EXT-X-STREAM-INF:".Length));
                continue;
            }

            if (line[0] == '#' || pendingVariant == null) continue;

            var variant = new Uri(baseUri, line);
            var bandwidth = long.TryParse(Get(pendingVariant, "BANDWIDTH"), NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : 0;

            if (IsAudioOnly(pendingVariant) && bandwidth > bestAudioOnlyBandwidth)
            {
                bestAudioOnly = variant;
                bestAudioOnlyBandwidth = bandwidth;
            }

            if (bandwidth < smallestBandwidth)
            {
                smallest = variant;
                smallestBandwidth = bandwidth;
            }

            pendingVariant = null;
        }

        return audioTrack ?? bestAudioOnly ?? smallest;
    }

    public static MediaPlaylist ParseMedia(string text, Uri baseUri)
    {
        var segments = new List<Segment>();
        var target = 6.0;
        var sequence = 0L;
        var hasEnd = false;
        string? problem = null;
        SegmentKey? key = null;
        var seconds = 0.0;
        var isNewPart = false;

        foreach (var raw in Lines(text))
        {
            var line = raw.Trim();

            if (line.Length == 0) continue;

            if (line[0] != '#')
            {
                segments.Add(new Segment(new Uri(baseUri, line), sequence++, seconds, key, isNewPart));
                seconds = 0;
                isNewPart = false;
                continue;
            }

            var (tag, value) = Tag(line);

            switch (tag)
            {
                case "#EXT-X-TARGETDURATION":
                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && t > 0) target = t;

                    break;

                case "#EXT-X-MEDIA-SEQUENCE":
                    if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var s)) sequence = s;

                    break;

                case "#EXTINF":
                    var comma = value.IndexOf(',');
                    double.TryParse(comma >= 0 ? value.Substring(0, comma) : value, NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
                    break;

                case "#EXT-X-KEY":
                    key = ReadKey(Attributes(value), baseUri, ref problem);
                    break;

                case "#EXT-X-ENDLIST":
                    hasEnd = true;
                    break;

                case "#EXT-X-DISCONTINUITY":
                    isNewPart = true;
                    break;

                case "#EXT-X-MAP":
                    problem ??= "fMP4 segments";
                    break;
            }
        }

        return new MediaPlaylist(segments, target, hasEnd, problem);
    }

    private static SegmentKey? ReadKey(Dictionary<string, string> attributes, Uri baseUri, ref string? problem)
    {
        var method = Get(attributes, "METHOD");

        if (method == null || method == "NONE") return null;

        if (method != "AES-128" || Get(attributes, "URI") is not { } uri)
        {
            problem ??= method + " encryption";
            return null;
        }

        return new SegmentKey(new Uri(baseUri, uri), ParseIv(Get(attributes, "IV")));
    }

    private static byte[]? ParseIv(string? text)
    {
        if (text == null) return null;

        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text.Substring(2) : text;
        hex = hex.PadLeft(32, '0');

        if (hex.Length != 32) return null;

        var iv = new byte[16];

        for (var i = 0; i < 16; i++)
        {
            if (!byte.TryParse(hex.AsSpan(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out iv[i])) return null;
        }

        return iv;
    }

    private static bool IsAudioOnly(Dictionary<string, string> variant)
    {
        if (variant.ContainsKey("RESOLUTION")) return false;

        var codecs = Get(variant, "CODECS");

        if (codecs == null) return false;

        foreach (var video in VideoCodecs)
        {
            if (codecs.IndexOf(video, StringComparison.OrdinalIgnoreCase) >= 0) return false;
        }

        return true;
    }

    private static (string Tag, string Value) Tag(string line)
    {
        var colon = line.IndexOf(':');

        return colon < 0 ? (line, "") : (line.Substring(0, colon), line.Substring(colon + 1));
    }

    /// <summary>KEY=VALUE pairs split by commas, where a value in quotes may hold commas.</summary>
    public static Dictionary<string, string> Attributes(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;

        while (i < text.Length)
        {
            var equals = text.IndexOf('=', i);

            if (equals < 0) break;

            var name = text.Substring(i, equals - i).Trim().TrimStart(',').Trim();
            i = equals + 1;
            string value;

            if (i < text.Length && text[i] == '"')
            {
                var close = text.IndexOf('"', i + 1);

                if (close < 0) close = text.Length;

                value = text.Substring(i + 1, close - i - 1);
                i = close + 1;
            }
            else
            {
                var comma = text.IndexOf(',', i);

                if (comma < 0) comma = text.Length;

                value = text.Substring(i, comma - i).Trim();
                i = comma;
            }

            if (i < text.Length && text[i] == ',') i++;

            if (name.Length > 0) result[name] = value;
        }

        return result;
    }

    private static string? Get(Dictionary<string, string> attributes, string name) => attributes.TryGetValue(name, out var value) ? value : null;

    internal static IEnumerable<string> Lines(string text) => text.Split('\n');
}

/// <summary>
/// Which segments of a live playlist to fetch next. A live list is a window that moves forward as the station
/// adds segments, so it is fetched again and again; this keeps track of the last one played.
/// </summary>
public sealed class HlsTracker
{
    /// <summary>How far behind the live end to start, in segments: enough to fill the buffer at once.</summary>
    public const int StartBehind = 3;

    /// <summary>More waiting segments than this means it fell behind; it skips ahead to the live end again.</summary>
    public const int MaxBehind = 8;

    private long _last = -1;

    public bool HasStarted => _last >= 0;

    /// <summary>The segments after the last one played, and marks them played.</summary>
    public List<Segment> Next(MediaPlaylist playlist)
    {
        var all = playlist.Segments;
        var from = 0;

        if (!HasStarted)
        {
            if (!playlist.HasEnd) from = Math.Max(0, all.Count - StartBehind);
        }
        else if (all.Count > 0 && all[all.Count - 1].Sequence < _last - MaxBehind)
        {
            // The numbers went back a long way: the station restarted its list, so start again at its live end.
            if (!playlist.HasEnd) from = Math.Max(0, all.Count - StartBehind);
        }
        else
        {
            while (from < all.Count && all[from].Sequence <= _last) from++;

            if (!playlist.HasEnd && all.Count - from > MaxBehind) from = all.Count - StartBehind;
        }

        var next = new List<Segment>(all.Count - from);

        for (var i = from; i < all.Count; i++) next.Add(all[i]);

        if (next.Count > 0) _last = next[next.Count - 1].Sequence;

        return next;
    }
}
