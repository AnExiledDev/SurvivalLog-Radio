using System;
using System.Globalization;

namespace Radio.Core;

/// <summary>A message from the page. Everything else the page could send is ignored.</summary>
public abstract record PageCommand
{
    public const string Prefix = "slmod|radio|";

    private PageCommand()
    {
    }

    public sealed record Ready : PageCommand;

    public sealed record NoScript : PageCommand;

    /// <summary>A diagnostic line from the page script, for the log.</summary>
    public sealed record Note(string Text) : PageCommand;

    /// <summary>A text box in the page gained or lost focus, so the game's keys should pause or resume.</summary>
    public sealed record Focus(bool IsTyping) : PageCommand;

    /// <summary>The page wants everything again, as when the Radio tab opens.</summary>
    public sealed record Show : PageCommand;

    /// <summary>Search Radio Browser for stations by name.</summary>
    public sealed record Search(string Query) : PageCommand;

    /// <summary>Show one of the lists.</summary>
    public sealed record Browse(Tab Tab) : PageCommand;

    /// <summary>Play a station from the list on screen, the favorites, or the one playing.</summary>
    public sealed record Play(string StationId) : PageCommand;

    /// <summary>Play a link the player pasted or typed.</summary>
    public sealed record Link(string Text) : PageCommand;

    public sealed record Star(string StationId, bool IsFavorite) : PageCommand;

    /// <summary>Play the music folder from this song.</summary>
    public sealed record Song(int Index) : PageCommand;

    public sealed record Next : PageCommand;

    public sealed record Stop : PageCommand;

    public sealed record Volume(int Percent) : PageCommand;

    public sealed record OpenFolder : PageCommand;

    public sealed record Scan : PageCommand;

    /// <summary>Put the clipboard's text in the link box.</summary>
    public sealed record Paste : PageCommand;

    public static bool IsOurs(string? message) => message != null && message.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Free text (a search, a link, a station id) comes URI-encoded, so it never holds the separator.</summary>
    public static PageCommand? Parse(string? message)
    {
        if (message == null || !IsOurs(message)) return null;

        var body = message.Substring(Prefix.Length);

        if (body.StartsWith("diag|", StringComparison.Ordinal)) return new Note(body.Substring("diag|".Length));

        var parts = body.Split('|');

        return (parts[0], parts.Length) switch
        {
            ("ready", 1) => new Ready(),
            ("noscript", 1) => new NoScript(),
            ("focus", 2) when IsSwitch(parts[1]) => new Focus(parts[1] == "1"),
            ("show", 1) => new Show(),
            ("search", 2) => new Search(Decode(parts[1])),
            ("browse", 2) when TryTab(parts[1], out var tab) => new Browse(tab),
            ("play", 2) when parts[1].Length > 0 => new Play(Decode(parts[1])),
            ("url", 2) when parts[1].Length > 0 => new Link(Decode(parts[1])),
            ("fav", 3) when parts[1].Length > 0 && IsSwitch(parts[2]) => new Star(Decode(parts[1]), parts[2] == "1"),
            ("file", 2) when TryNumber(parts[1], out var index) => new Song(index),
            ("next", 1) => new Next(),
            ("stop", 1) => new Stop(),
            ("volume", 2) when TryNumber(parts[1], out var percent) && percent <= 100 => new Volume(percent),
            ("folder", 1) => new OpenFolder(),
            ("scan", 1) => new Scan(),
            ("paste", 1) => new Paste(),
            _ => null,
        };
    }

    public static string TabName(Tab tab) => tab switch
    {
        Tab.Popular => "popular",
        Tab.China => "china",
        Tab.Chinese => "chinese",
        Tab.Favorites => "favorites",
        Tab.Music => "music",
        _ => "search",
    };

    private static bool TryTab(string text, out Tab tab)
    {
        foreach (Tab each in Enum.GetValues(typeof(Tab)))
        {
            if (each != Tab.Search && TabName(each) == text)
            {
                tab = each;
                return true;
            }
        }

        tab = Tab.Popular;
        return false;
    }

    private static string Decode(string text) => Uri.UnescapeDataString(text);

    private static bool IsSwitch(string text) => text is "0" or "1";

    private static bool TryNumber(string text, out int number) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out number);
}

/// <summary>The lists on the Radio tab.</summary>
public enum Tab
{
    Popular,
    China,
    Chinese,
    Favorites,
    Music,
    Search,
}
