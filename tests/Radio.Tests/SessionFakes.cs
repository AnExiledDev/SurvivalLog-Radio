using System.Collections.Concurrent;
using System.Text.Json;
using Radio.Core;

namespace Radio.Tests;

/// <summary>
/// The game as the record player and music hooks leave it: which record player draws power, what the game's music
/// plays, and the morale given. Music the game asks for goes through the same hold check the real hooks use.
/// </summary>
internal sealed class FakeGame : IRadioGame
{
    public const string HomeMusic = "event:/Music/Home";

    private readonly MusicHold _hold;

    public FakeGame(MusicHold hold) => _hold = hold;

    public bool IsInGame { get; set; } = true;

    public bool HasPower { get; set; } = true;

    public HashSet<long> Players { get; } = new() { 1, 2 };

    /// <summary>The one record player switched on, or null.</summary>
    public long? PlayerOn { get; private set; }

    /// <summary>What the game's music plays; "" when it is quiet.</summary>
    public string Music { get; private set; } = HomeMusic;

    public int Morale { get; private set; }

    public bool IsPowerOff() => !HasPower;

    public bool SwitchOn(long playerId)
    {
        if (!Players.Contains(playerId)) return false;

        PlayerOn = playerId;
        return true;
    }

    public void SwitchOff(long playerId)
    {
        if (PlayerOn == playerId) PlayerOn = null;
    }

    public void StopGameMusic()
    {
        _hold.OnStop();
        Music = "";
    }

    public void PlayGameMusic(HeldMusic music) => Music = music switch
    {
        HeldMusic.Play play => play.Path,
        HeldMusic.FadeIn fadeIn => fadeIn.Path,
        HeldMusic.Silence => "",
        _ => HomeMusic,
    };

    public void GrantMorale() => Morale++;

    /// <summary>The game asks for music, as on a load, a trip or a record. The hook skips it while the radio holds the music.</summary>
    public void AskForMusic(string path)
    {
        if (!_hold.OnPlay(path)) Music = path;
    }

    /// <summary>A save loads or a trip starts or ends: the game turns every record player off.</summary>
    public void TurnAllOff() => PlayerOn = null;
}

/// <summary>The Radio tab as page.js would draw it. It knows only the words page.js knows, so a key it lacks fails the test.</summary>
internal sealed class FakePage : IRadioPage
{
    private static readonly HashSet<string> Words = new(Texts.Page.Select(w => w.Key)) { "" };
    private static readonly HashSet<string> Tones = new() { "", "good", "wait", "bad" };

    public bool IsChinese { get; set; }

    /// <summary>False while the page is busy, like a page still loading; it turns every update away.</summary>
    public bool Accepts { get; set; } = true;

    public JsonElement? NowBar { get; private set; }

    public JsonElement? ListShown { get; private set; }

    public string[] Stars { get; private set; } = Array.Empty<string>();

    public string LinkBox { get; private set; } = "";

    public bool IsTyping { get; private set; }

    public List<(string Text, bool IsProblem)> Toasts { get; } = new();

    public void Install()
    {
    }

    public void ForgetWords()
    {
    }

    public void SetTyping(bool isTyping) => IsTyping = isTyping;

    public bool Now(string json)
    {
        if (!Accepts) return false;

        var now = JsonDocument.Parse(json).RootElement.Clone();
        Known(now.GetProperty("status").GetString());

        if (!Tones.Contains(now.GetProperty("tone").GetString()!)) throw new InvalidOperationException("page.js has no tone " + now.GetProperty("tone"));

        NowBar = now;
        return true;
    }

    public bool List(string json)
    {
        if (!Accepts) return false;

        var list = JsonDocument.Parse(json).RootElement.Clone();
        Known(list.GetProperty("note").GetString());
        ListShown = list;

        return true;
    }

    public bool Favorites(string json)
    {
        if (!Accepts) return false;

        Stars = JsonSerializer.Deserialize<string[]>(json)!;
        return true;
    }

    public void Pasted(string text) => LinkBox = text;

    public void Toast(string text, bool isProblem) => Toasts.Add((text, isProblem));

    private static void Known(string? key)
    {
        if (key == null || !Words.Contains(key)) throw new InvalidOperationException("page.js has no words for " + key);
    }
}

/// <summary>The game's music channel. The speakers read whatever ring it was last given.</summary>
internal sealed class FakeAudio : IAudioOut
{
    private float[] _buffer = Array.Empty<float>();

    /// <summary>The ring the speakers play, or null when the radio is quiet.</summary>
    public SampleRing? Ring { get; private set; }

    public float Volume { get; private set; }

    public void Play(SampleRing ring) => Ring = ring;

    public void SetVolume(float volume) => Volume = volume;

    public void Stop() => Ring = null;

    /// <summary>The speakers take this many frames, as FMOD's callback would.</summary>
    public void Speakers(int frames)
    {
        if (Ring == null) return;

        if (_buffer.Length != frames * SampleRing.Channels) _buffer = new float[frames * SampleRing.Channels];

        Ring.Read(_buffer);
    }
}

/// <summary>A reader the test drives: it sends events and sound when told to.</summary>
internal sealed class FakePlayback : IPlayback
{
    private readonly ConcurrentQueue<PlayerEvent> _events = new();

    public FakePlayback(Uri? link, IReadOnlyList<string> paths, int first)
    {
        Link = link;
        Paths = paths;
        First = first;
    }

    public Uri? Link { get; }

    public IReadOnlyList<string> Paths { get; }

    public int First { get; }

    public SampleRing Ring { get; } = new(2, 0.5);

    public bool IsStopped { get; private set; }

    public void Send(PlayerEvent playerEvent) => _events.Enqueue(playerEvent);

    public bool TryTakeEvent(out PlayerEvent? playerEvent)
    {
        var isTaken = _events.TryDequeue(out var taken);
        playerEvent = taken;

        return isTaken;
    }

    public void Stop() => IsStopped = true;

    /// <summary>This many frames of sound come in from the station or the file.</summary>
    public void Arrives(int frames) => Ring.Write(Enumerable.Repeat(0.25f, frames * SampleRing.Channels).ToArray());
}

internal sealed class FakeMaker : IPlaybackMaker
{
    public List<FakePlayback> Made { get; } = new();

    public FakePlayback Last => Made[^1];

    public IPlayback Stream(Uri link) => Make(new FakePlayback(link, Array.Empty<string>(), 0));

    public IPlayback Songs(IReadOnlyList<string> paths, int first) => Make(new FakePlayback(null, paths, first));

    private FakePlayback Make(FakePlayback playback)
    {
        Made.Add(playback);
        return playback;
    }
}

/// <summary>The player's computer: folders of files, the clipboard and the favorites file.</summary>
internal sealed class FakeFiles : IRadioFiles
{
    public Dictionary<string, List<string>> Folders { get; } = new();

    /// <summary>Set to make the next scans wait until the test finishes them.</summary>
    public TaskCompletionSource<MusicList>? SlowScan { get; set; }

    public string Clipboard { get; set; } = "";

    public string? OpenProblem { get; set; }

    public string? OpenedFolder { get; private set; }

    public string? FavoritesFile { get; set; }

    public Task<MusicList> ScanMusic(string folder, CancellationToken cancel)
    {
        if (SlowScan != null) return SlowScan.Task;

        if (!Folders.TryGetValue(folder, out var files)) return Task.FromException<MusicList>(new DirectoryNotFoundException("no folder " + folder));

        return Task.FromResult(MusicFolder.Scan(files, folder));
    }

    public string? OpenMusicFolder(string folder)
    {
        if (OpenProblem == null) OpenedFolder = folder;

        return OpenProblem;
    }

    public string ReadClipboard() => Clipboard;

    public string? LoadFavorites() => FavoritesFile;

    public void SaveFavorites(string json) => FavoritesFile = json;
}

internal static class PageJson
{
    public static string Text(this JsonElement? element, string name) => element!.Value.GetProperty(name).GetString()!;

    public static bool Flag(this JsonElement? element, string name) => element!.Value.GetProperty(name).GetBoolean();

    public static int Number(this JsonElement? element, string name) => element!.Value.GetProperty(name).GetInt32();

    public static string[] RowIds(this JsonElement? list) => list!.Value.GetProperty("rows").EnumerateArray().Select(r => r.ValueKind == JsonValueKind.String ? r.GetString()! : r.GetProperty("id").GetString()!).ToArray();
}
