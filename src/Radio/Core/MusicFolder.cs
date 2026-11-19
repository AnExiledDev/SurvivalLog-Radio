using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Radio.Core;

/// <summary>The songs found in a folder: where they are, the names to show, and those names as the page's rows.</summary>
public sealed record MusicList(string Folder, IReadOnlyList<string> Paths, IReadOnlyList<string> Names, string RowsJson);

/// <summary>Which files in the music folder are songs, in what order, and what to call them.</summary>
public static class MusicFolder
{
    public const int MaxSongs = 5000;

    /// <summary>What Windows 10 and 11 decode without extra installs.</summary>
    private static readonly HashSet<string> SongTypes = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".aac", ".wma", ".wav", ".flac", ".mp4" };

    public static bool IsSong(string path) => SongTypes.Contains(Path.GetExtension(path));

    /// <summary>Everything the Music tab needs from a folder's files. Runs off the game thread, so the sorting and JSON cost the game nothing.</summary>
    public static MusicList Scan(IEnumerable<string> files, string root)
    {
        var paths = Songs(files, root);
        var names = paths.ConvertAll(path => DisplayName(root, path));

        return new MusicList(root, paths, names, RadioView.SongRows(names));
    }

    /// <summary>The songs among the files, sorted the way a person would: "2 Song" before "10 Song".</summary>
    public static List<string> Songs(IEnumerable<string> files, string root) =>
        files.Where(IsSong).OrderBy(f => Relative(root, f), NaturalOrder.Instance).Take(MaxSongs).ToList();

    /// <summary>The name to show: the file name without its type, with the subfolder in front when it is in one.</summary>
    public static string DisplayName(string root, string path)
    {
        var relative = Relative(root, path);
        var folder = Path.GetDirectoryName(relative);
        var name = Path.GetFileNameWithoutExtension(relative);

        return string.IsNullOrEmpty(folder) ? name : folder.Replace('\\', '/') + "/" + name;
    }

    private static string Relative(string root, string path)
    {
        if (root.Length == 0) return path;

        try
        {
            return Path.GetRelativePath(root, path);
        }
        catch (ArgumentException)
        {
            return path;
        }
    }

    /// <summary>Compares names with runs of digits as numbers.</summary>
    private sealed class NaturalOrder : IComparer<string>
    {
        public static readonly NaturalOrder Instance = new();

        public int Compare(string? a, string? b)
        {
            a ??= "";
            b ??= "";
            var i = 0;
            var j = 0;

            while (i < a.Length && j < b.Length)
            {
                if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
                {
                    var startA = i;
                    var startB = j;

                    while (i < a.Length && char.IsDigit(a[i])) i++;

                    while (j < b.Length && char.IsDigit(b[j])) j++;

                    var numberA = a.AsSpan(startA, i - startA).TrimStart('0');
                    var numberB = b.AsSpan(startB, j - startB).TrimStart('0');

                    if (numberA.Length != numberB.Length) return numberA.Length.CompareTo(numberB.Length);

                    var byDigits = numberA.CompareTo(numberB, StringComparison.Ordinal);

                    if (byDigits != 0) return byDigits;

                    continue;
                }

                var byChar = string.Compare(a, i, b, j, 1, StringComparison.OrdinalIgnoreCase);

                if (byChar != 0) return byChar;

                i++;
                j++;
            }

            return (a.Length - i).CompareTo(b.Length - j);
        }
    }
}
