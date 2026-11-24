using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Radio.Core;
using UnityEngine;

namespace Radio.Adapters;

/// <summary>The radio's files on the player's PC: the music folder, the clipboard, the favorites and what played in each save.</summary>
internal sealed class RadioFiles : IRadioFiles
{
    private readonly SafeFile _favorites;
    private readonly SafeFile _resume;

    public RadioFiles(string favoritesPath, string resumePath)
    {
        _favorites = new SafeFile(favoritesPath);
        _resume = new SafeFile(resumePath);
    }

    /// <summary>Runs off the game thread. A missing folder is made, so the player has somewhere to put songs.</summary>
    public Task<MusicList> ScanMusic(string folder, CancellationToken cancel) => Task.Run(() =>
    {
        Directory.CreateDirectory(folder);

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var songs = new List<string>();

        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            cancel.ThrowIfCancellationRequested();

            if (MusicFolder.IsSong(file)) songs.Add(file);
        }

        return MusicFolder.Scan(songs, folder);
    }, cancel);

    public string? OpenMusicFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + folder + "\"") { UseShellExecute = true });
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public string ReadClipboard() => GUIUtility.systemCopyBuffer ?? "";

    public string? LoadFavorites() => _favorites.Read();

    public void SaveFavorites(string json) => _favorites.Write(json);

    public string? LoadResume() => _resume.Read();

    public void SaveResume(string json) => _resume.Write(json);

    /// <summary>Why the last save of either file failed, once. Main thread, so the reason reaches the log.</summary>
    public string? TakeSaveProblem() => _favorites.TakeProblem() ?? _resume.TakeProblem();

    /// <summary>The game is closing: give a save that is still writing a moment to finish.</summary>
    public void Finish(TimeSpan wait)
    {
        _favorites.Finish(wait);
        _resume.Finish(wait);
    }
}

/// <summary>
/// One of the radio's own files. It writes off the game thread, to a new file that then takes the old one's place, so a
/// crash mid-write leaves the old one. Writes that come in while one is going collapse into the newest.
/// </summary>
internal sealed class SafeFile
{
    private readonly string _path;
    private readonly object _lock = new();
    private string? _waiting;
    private Task? _writing;
    private volatile string? _problem;

    public SafeFile(string path) => _path = path;

    public string? Read() => File.Exists(_path) ? File.ReadAllText(_path) : null;

    public void Write(string text)
    {
        lock (_lock)
        {
            _waiting = text;

            if (_writing != null) return;

            _writing = Task.Run(WriteWaiting);
        }
    }

    public string? TakeProblem()
    {
        var problem = _problem;

        if (problem != null) _problem = null;

        return problem;
    }

    public void Finish(TimeSpan wait)
    {
        Task? writing;

        lock (_lock) writing = _writing;

        writing?.Wait(wait);
    }

    private void WriteWaiting()
    {
        while (true)
        {
            string text;

            lock (_lock)
            {
                if (_waiting == null)
                {
                    _writing = null;
                    return;
                }

                text = _waiting;
                _waiting = null;
            }

            try
            {
                var newFile = _path + ".new";
                File.WriteAllText(newFile, text);
                File.Move(newFile, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                _problem = ex.Message;
            }
        }
    }
}
