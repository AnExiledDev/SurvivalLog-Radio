using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Radio.Core;
using UnityEngine;

namespace Radio.Adapters;

/// <summary>The radio's files on the player's PC: the music folder, the clipboard and the saved stations.</summary>
internal sealed class RadioFiles : IRadioFiles
{
    private readonly string _favoritesPath;
    private readonly object _saveLock = new();
    private string? _waitingSave;
    private Task? _saving;
    private volatile string? _saveProblem;

    public RadioFiles(string favoritesPath) => _favoritesPath = favoritesPath;

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

    public string? LoadFavorites() => File.Exists(_favoritesPath) ? File.ReadAllText(_favoritesPath) : null;

    /// <summary>
    /// Writes off the game thread, to a new file that then takes the old one's place, so a crash mid-write leaves the
    /// old list. Saves that come in while one is writing collapse into the newest.
    /// </summary>
    public void SaveFavorites(string json)
    {
        lock (_saveLock)
        {
            _waitingSave = json;

            if (_saving != null) return;

            _saving = Task.Run(WriteWaiting);
        }
    }

    /// <summary>Why the last save failed, once. Main thread, so the reason reaches the log.</summary>
    public string? TakeSaveProblem()
    {
        var problem = _saveProblem;

        if (problem != null) _saveProblem = null;

        return problem;
    }

    /// <summary>The game is closing: give a save that is still writing a moment to finish.</summary>
    public void Finish(TimeSpan wait)
    {
        Task? saving;

        lock (_saveLock) saving = _saving;

        saving?.Wait(wait);
    }

    private void WriteWaiting()
    {
        while (true)
        {
            string json;

            lock (_saveLock)
            {
                if (_waitingSave == null)
                {
                    _saving = null;
                    return;
                }

                json = _waitingSave;
                _waitingSave = null;
            }

            try
            {
                var newFile = _favoritesPath + ".new";
                File.WriteAllText(newFile, json);
                File.Move(newFile, _favoritesPath, overwrite: true);
            }
            catch (Exception ex)
            {
                _saveProblem = ex.Message;
            }
        }
    }
}
