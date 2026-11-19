using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Radio.Core;

/// <summary>A station from Radio Browser, or a link the player pasted.</summary>
public sealed class Station
{
    public const string LinkPrefix = "url:";

    public Station(string id, string name, string url, string country, string codec, int bitrate)
    {
        Id = id;
        Name = name;
        Url = url;
        Country = country;
        Codec = codec;
        Bitrate = bitrate;
    }

    /// <summary>Radio Browser's station id, or "url:" and the link for a pasted one.</summary>
    public string Id { get; }

    public string Name { get; }

    /// <summary>The stream link as Radio Browser last saw it working.</summary>
    public string Url { get; }

    public string Country { get; }

    public string Codec { get; }

    /// <summary>In kbps, 0 when unknown.</summary>
    public int Bitrate { get; }

    public bool IsLink => Id.StartsWith(LinkPrefix, StringComparison.Ordinal);

    public static Station FromLink(Uri link) => new(LinkPrefix + link.AbsoluteUri, link.Host, link.AbsoluteUri, "", "", 0);

    /// <summary>The small line under the name: country, format and quality, whichever are known.</summary>
    public string Details()
    {
        var parts = new List<string>(3);

        if (Country.Length > 0) parts.Add(Country);

        if (Codec.Length > 0 && Codec != "UNKNOWN") parts.Add(Codec);

        if (Bitrate > 0) parts.Add(Bitrate.ToString(CultureInfo.InvariantCulture) + " kbps");

        return string.Join(", ", parts);
    }
}

public enum StationList
{
    Popular,
    China,
    Chinese,
    Search,
}

/// <summary>Fetches a web page as text. The adapter runs it off the game thread.</summary>
public delegate Task<string> TextFetcher(Uri uri, CancellationToken cancel);

/// <summary>
/// Radio Browser (radio-browser.info), the free community list of about 50,000 stations worldwide. It runs on a few
/// servers; this tries them in turn and keeps using the one that answered.
/// </summary>
public sealed class RadioBrowser
{
    public const int PopularCount = 100;
    public const int ListLimit = 300;
    public const int SearchLimit = 200;

    private static readonly string[] Servers = { "all.api.radio-browser.info", "de1.api.radio-browser.info", "de2.api.radio-browser.info" };

    /// <summary>
    /// Formats it can play on any PC. Ogg and Opus need Windows' media extensions, which many PCs don't have, and FLAC
    /// stations are rare, so they aren't listed. A pasted link to one still gets tried.
    /// </summary>
    private static readonly HashSet<string> PlayableCodecs = new(StringComparer.OrdinalIgnoreCase) { "MP3", "AAC", "AAC+", "MPEG", "UNKNOWN", "" };

    private readonly TextFetcher _fetch;
    private int _server;

    public RadioBrowser(TextFetcher fetch) => _fetch = fetch;

    public async Task<IReadOnlyList<Station>> List(StationList list, string query, CancellationToken cancel)
    {
        const string Sorted = "hidebroken=true&order=clickcount&reverse=true";

        switch (list)
        {
            case StationList.Popular:
                return Parse(await Fetch($"/json/stations/topclick/{PopularCount}?hidebroken=true", cancel).ConfigureAwait(false));

            case StationList.China:
                return Parse(await Fetch($"/json/stations/bycountrycodeexact/CN?{Sorted}&limit={ListLimit}", cancel).ConfigureAwait(false));

            case StationList.Chinese:
                // Stations say their language in different words; these three cover Chinese.
                var lists = await Task.WhenAll(
                    new[] { "chinese", "mandarin", "cantonese" }.Select(language => Fetch($"/json/stations/search?language={language}&{Sorted}&limit={ListLimit}", cancel))).ConfigureAwait(false);

                return Merge(lists.Select(Parse), ListLimit);

            default:
                if (query.Trim().Length == 0) return Array.Empty<Station>();

                return Parse(await Fetch($"/json/stations/search?name={Uri.EscapeDataString(query.Trim())}&{Sorted}&limit={SearchLimit}", cancel).ConfigureAwait(false));
        }
    }

    /// <summary>
    /// The station's current stream link. Asking for it also counts a listen on Radio Browser, which is how its
    /// popular list works. Null when the station is gone.
    /// </summary>
    public async Task<Uri?> Resolve(string id, CancellationToken cancel)
    {
        using var document = JsonDocument.Parse(await Fetch("/json/url/" + Uri.EscapeDataString(id), cancel).ConfigureAwait(false));
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) return null;

        return Uri.TryCreate(url.GetString(), UriKind.Absolute, out var link) ? link : null;
    }

    private async Task<string> Fetch(string pathAndQuery, CancellationToken cancel)
    {
        Exception? last = null;

        for (var i = 0; i < Servers.Length; i++)
        {
            var server = (Volatile.Read(ref _server) + i) % Servers.Length;

            try
            {
                var text = await _fetch(new Uri("https://" + Servers[server] + pathAndQuery), cancel).ConfigureAwait(false);
                Volatile.Write(ref _server, server);
                return text;
            }
            catch (Exception e) when (!cancel.IsCancellationRequested)
            {
                last = e;
            }
        }

        throw last ?? new InvalidOperationException("no Radio Browser server");
    }

    /// <summary>Stations from Radio Browser's JSON, without the ones it can't play, broken ones and repeats.</summary>
    public static IReadOnlyList<Station> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var stations = new List<Station>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (document.RootElement.ValueKind != JsonValueKind.Array) return stations;

        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var id = Text(item, "stationuuid");
            var url = Text(item, "url_resolved");

            if (url.Length == 0) url = Text(item, "url");

            var codec = Text(item, "codec").Trim();
            var name = Clean(Text(item, "name"));

            if (id.Length == 0 || name.Length == 0 || !IsHttp(url) || !PlayableCodecs.Contains(codec) || !seen.Add(url)) continue;

            stations.Add(new Station(id, name, url, Clean(Text(item, "country")), codec.ToUpperInvariant(), Number(item, "bitrate")));
        }

        return stations;
    }

    /// <summary>Several lists as one, each station once, in the order of the first list it appears in.</summary>
    public static IReadOnlyList<Station> Merge(IEnumerable<IReadOnlyList<Station>> lists, int limit)
    {
        var merged = new List<Station>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = lists.ToList();

        // Take them in turns, so the top of every list makes it in before the tail of any.
        for (var rank = 0; merged.Count < limit; rank++)
        {
            var anyLeft = false;

            foreach (var list in all)
            {
                if (rank >= list.Count) continue;

                anyLeft = true;

                if (seen.Add(list[rank].Id)) merged.Add(list[rank]);
            }

            if (!anyLeft) break;
        }

        return merged.Count > limit ? merged.GetRange(0, limit) : merged;
    }

    private static string Text(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static int Number(JsonElement item, string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) && n > 0 ? n : 0;

    /// <summary>Names often carry tabs, line breaks and runs of spaces.</summary>
    private static string Clean(string text) => string.Join(" ", text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsHttp(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>The stations the player starred, kept in a file of their own so they last across saves.</summary>
public sealed class Favorites
{
    public const int Max = 500;

    private readonly List<Station> _stations;

    public Favorites(IEnumerable<Station> stations) => _stations = stations.Take(Max).ToList();

    public IReadOnlyList<Station> All => _stations;

    public bool Contains(string id) => _stations.Exists(s => s.Id == id);

    /// <summary>Stars or unstars a station. False when nothing changed.</summary>
    public bool Set(Station station, bool isFavorite)
    {
        var index = _stations.FindIndex(s => s.Id == station.Id);

        if (isFavorite == index >= 0) return false;

        if (isFavorite)
        {
            if (_stations.Count >= Max) return false;

            _stations.Add(station);
        }
        else
        {
            _stations.RemoveAt(index);
        }

        return true;
    }

    public string ToJson()
    {
        var items = _stations.Select(s => new Dictionary<string, object>
        {
            ["id"] = s.Id, ["name"] = s.Name, ["url"] = s.Url, ["country"] = s.Country, ["codec"] = s.Codec, ["bitrate"] = s.Bitrate,
        });

        return JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The saved list; an unreadable file gives an empty one, and the caller keeps the file as it was.</summary>
    public static Favorites Parse(string? json, out bool isReadable)
    {
        isReadable = true;

        if (string.IsNullOrWhiteSpace(json)) return new Favorites(Array.Empty<Station>());

        try
        {
            using var document = JsonDocument.Parse(json!);
            var stations = new List<Station>();

            foreach (var item in document.RootElement.EnumerateArray())
            {
                var id = Get(item, "id");
                var url = Get(item, "url");

                if (id.Length == 0 || url.Length == 0) continue;

                var bitrate = item.TryGetProperty("bitrate", out var b) && b.ValueKind == JsonValueKind.Number && b.TryGetInt32(out var n) ? n : 0;
                stations.Add(new Station(id, Get(item, "name"), url, Get(item, "country"), Get(item, "codec"), bitrate));
            }

            return new Favorites(stations);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException)
        {
            isReadable = false;
            return new Favorites(Array.Empty<Station>());
        }
    }

    private static string Get(JsonElement item, string name) => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
