using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Radio.Core;

public enum Tone
{
    None,

    /// <summary>Sound is playing.</summary>
    Good,

    /// <summary>Connecting, buffering or paused.</summary>
    Waiting,

    /// <summary>It stopped because something went wrong.</summary>
    Bad,
}

/// <summary>The status line: a key page.js looks the words up by, and the one value they need, if any.</summary>
public sealed record StatusLine(string Key, string Arg, Tone Tone)
{
    public static readonly StatusLine None = new("", "", Tone.None);
}

/// <summary>
/// The now playing bar. Kept as a value so the session can tell when it changed by comparing it with the one it last
/// sent, and send nothing when they match.
/// </summary>
public sealed record NowView(
    bool IsOn,
    bool IsHere,
    string Kind,
    string StationId,
    int Song,
    string Name,
    string Title,
    StatusLine Status,
    int Volume,
    bool CanNext)
{
    public const string StationKind = "station";
    public const string FolderKind = "folder";

    public static NowView Off(int volume, StatusLine status) => new(false, false, "", "", -1, "", "", status, volume, false);
}

/// <summary>The list on screen. The rows are JSON built once, off the game thread when the list is big.</summary>
public sealed record ListView(Tab Tab, bool IsLoading, string Query, string Note, string NoteArg, string Folder, string RowsJson)
{
    public const string NoRows = "[]";
}

/// <summary>Writes what page.js draws the Radio tab from.</summary>
public static class RadioView
{
    public static string Now(NowView now) => Write(json =>
    {
        json.WriteStartObject();
        json.WriteBoolean("on", now.IsOn);
        json.WriteBoolean("here", now.IsHere);
        json.WriteString("kind", now.Kind);
        json.WriteString("id", now.StationId);
        json.WriteNumber("song", now.Song);
        json.WriteString("name", now.Name);
        json.WriteString("title", now.Title);
        json.WriteString("status", now.Status.Key);
        json.WriteString("arg", now.Status.Arg);
        json.WriteString("tone", ToneName(now.Status.Tone));
        json.WriteNumber("volume", now.Volume);
        json.WriteBoolean("canNext", now.CanNext);
        json.WriteEndObject();
    });

    public static string List(ListView list) => Write(json =>
    {
        json.WriteStartObject();
        json.WriteString("tab", PageCommand.TabName(list.Tab));
        json.WriteBoolean("loading", list.IsLoading);
        json.WriteString("query", list.Query);
        json.WriteString("note", list.Note);
        json.WriteString("noteArg", list.NoteArg);
        json.WriteString("folder", list.Folder);
        json.WritePropertyName("rows");
        json.WriteRawValue(list.RowsJson, skipInputValidation: true);
        json.WriteEndObject();
    });

    /// <summary>One object per station: its id, its name, and the small line under it.</summary>
    public static string StationRows(IReadOnlyList<Station> stations) => Write(json =>
    {
        json.WriteStartArray();

        foreach (var station in stations)
        {
            json.WriteStartObject();
            json.WriteString("id", station.Id);
            json.WriteString("name", station.Name);
            json.WriteString("meta", station.Details());
            json.WriteEndObject();
        }

        json.WriteEndArray();
    });

    public static string SongRows(IReadOnlyList<string> names) => Strings(names);

    /// <summary>The starred station ids, sent apart from the list so a star doesn't resend every row.</summary>
    public static string Favorites(Favorites favorites)
    {
        var ids = new List<string>(favorites.All.Count);

        foreach (var station in favorites.All) ids.Add(station.Id);

        return Strings(ids);
    }

    private static string Strings(IReadOnlyList<string> items) => Write(json =>
    {
        json.WriteStartArray();

        foreach (var item in items) json.WriteStringValue(item);

        json.WriteEndArray();
    });

    private static string ToneName(Tone tone) => tone switch
    {
        Tone.Good => "good",
        Tone.Waiting => "wait",
        Tone.Bad => "bad",
        _ => "",
    };

    private static string Write(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();

        using (var json = new Utf8JsonWriter(buffer))
        {
            write(json);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
