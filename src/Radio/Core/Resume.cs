using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Radio.Core;

/// <summary>
/// What was playing when the player last left a save: a station, or a song from the music folder, and the record
/// player it played on, by the id it had then.
/// </summary>
public sealed record ResumePoint(long PlayerId, Station? Station, string Song);

/// <summary>
/// The resume point of each save, kept in a file of Radio's own so nothing goes in the save. Saves are named by their
/// file name. The newest saves are kept when there are too many.
/// </summary>
public sealed class ResumeList
{
    public const int Max = 100;

    private readonly List<(string Save, ResumePoint Point)> _saves;

    private ResumeList(List<(string Save, ResumePoint Point)> saves) => _saves = saves;

    public static ResumeList Empty() => new(new());

    public ResumePoint? Get(string save) => _saves.Find(s => s.Save == save).Point;

    /// <summary>Puts the save's point last, as the newest. False when nothing changed.</summary>
    public bool Set(string save, ResumePoint point)
    {
        var index = _saves.FindIndex(s => s.Save == save);

        if (index == _saves.Count - 1 && index >= 0 && _saves[index].Point == point) return false;

        if (index >= 0) _saves.RemoveAt(index);

        _saves.Add((save, point));

        if (_saves.Count > Max) _saves.RemoveAt(0);

        return true;
    }

    /// <summary>False when the save had no point.</summary>
    public bool Remove(string save) => _saves.RemoveAll(s => s.Save == save) > 0;

    public string ToJson()
    {
        var items = _saves.Select(s =>
        {
            var item = new Dictionary<string, object> { ["save"] = s.Save, ["player"] = s.Point.PlayerId };

            if (s.Point.Station is { } station) item["station"] = Favorites.Item(station);
            else item["song"] = s.Point.Song;

            return item;
        });

        return JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The saved points. A file that can't be read gives none, and the next save writes over it.</summary>
    public static ResumeList Parse(string? json)
    {
        var saves = new List<(string, ResumePoint)>();

        if (string.IsNullOrWhiteSpace(json)) return new(saves);

        try
        {
            using var document = JsonDocument.Parse(json!);

            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var save = Favorites.Text(item, "save");

                if (save.Length == 0 || !item.TryGetProperty("player", out var p) || !p.TryGetInt64(out var player)) continue;

                var station = item.TryGetProperty("station", out var s) ? Favorites.ReadStation(s) : null;
                var song = Favorites.Text(item, "song");

                if (station == null && song.Length == 0) continue;

                saves.RemoveAll(x => x.Item1 == save);
                saves.Add((save, new ResumePoint(player, station, station == null ? song : "")));
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            saves.Clear();
        }

        while (saves.Count > Max) saves.RemoveAt(0);

        return new(saves);
    }
}
