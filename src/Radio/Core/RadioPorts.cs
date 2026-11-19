using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Radio.Core;

/// <summary>The game, as the radio sees it. Main thread only.</summary>
public interface IRadioGame
{
    /// <summary>A save is loaded and its base can be read.</summary>
    bool IsInGame { get; }

    /// <summary>The base has no power, so a record would refuse to play.</summary>
    bool IsPowerOff();

    /// <summary>
    /// Turns the record player on the way a record does: every other record player stops first, then it draws power.
    /// False when that record player isn't there.
    /// </summary>
    bool SwitchOn(long playerId);

    /// <summary>Turns the record player off without touching the music.</summary>
    void SwitchOff(long playerId);

    void StopGameMusic();

    void PlayGameMusic(HeldMusic music);

    /// <summary>The morale a finished record gives. The game allows it once a day, shared with records.</summary>
    void GrantMorale();
}

/// <summary>The Radio tab in the game's web UI, and the toasts. Main thread only.</summary>
public interface IRadioPage
{
    bool IsChinese { get; }

    void Install();

    /// <summary>The page lost its script or loaded it again, so its words go with the next call.</summary>
    void ForgetWords();

    /// <summary>A text box on the page has focus, so the game's keys should wait.</summary>
    void SetTyping(bool isTyping);

    /// <summary>False when the page couldn't take it; the session tries again later.</summary>
    bool Now(string json);

    bool List(string json);

    bool Favorites(string json);

    /// <summary>Puts text in the link box.</summary>
    void Pasted(string text);

    void Toast(string text, bool isProblem);
}

/// <summary>Plays a ring into the game's music. The audio thread reads the ring; everything else is main thread.</summary>
public interface IAudioOut
{
    /// <summary>Starts playing the ring, or switches to it when another one plays.</summary>
    void Play(SampleRing ring);

    /// <summary>0 to 1, on top of the game's music slider.</summary>
    void SetVolume(float volume);

    void Stop();
}

/// <summary>Makes and starts the reader for a station or the music folder.</summary>
public interface IPlaybackMaker
{
    IPlayback Stream(Uri link);

    IPlayback Songs(IReadOnlyList<string> paths, int first);
}

/// <summary>The player's own computer: the music folder, the clipboard and the favorites file.</summary>
public interface IRadioFiles
{
    /// <summary>Finds the songs in the folder off the game thread.</summary>
    Task<MusicList> ScanMusic(string folder, CancellationToken cancel);

    /// <summary>Opens the folder in Windows Explorer, making it first when it's missing. Null when it opened, otherwise why it didn't.</summary>
    string? OpenMusicFolder(string folder);

    /// <summary>The clipboard's text, or "" when it holds none.</summary>
    string ReadClipboard();

    /// <summary>The saved favorites, or null when there are none yet.</summary>
    string? LoadFavorites();

    /// <summary>Writes the favorites off the game thread.</summary>
    void SaveFavorites(string json);
}

/// <summary>The settings the radio reads. The music folder is already a full path.</summary>
public sealed record RadioSettings(int Volume, bool PlayWhileExploring, string MusicFolder, bool GiveMorale, double StreamMoraleMinutes);
