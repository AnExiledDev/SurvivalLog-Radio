namespace Radio.Core;

/// <summary>The game music that was asked for while the radio played, to start when it stops.</summary>
public abstract record HeldMusic
{
    private HeldMusic()
    {
    }

    /// <summary>Nothing was asked for: the home music comes back.</summary>
    public sealed record Home : HeldMusic;

    /// <summary>The game stopped its music, so it stays quiet.</summary>
    public sealed record Silence : HeldMusic;

    public sealed record Play(string Path) : HeldMusic;

    public sealed record FadeIn(string Path, float Seconds, float Volume) : HeldMusic;
}

/// <summary>
/// Keeps the game music off while the radio plays. The game asks for music on every load, trip and cutscene; while
/// holding, the music hooks skip those requests and only remember the last one, so the right music starts when the
/// radio stops.
/// </summary>
public sealed class MusicHold
{
    private HeldMusic _held = new HeldMusic.Home();

    public bool IsHolding { get; private set; }

    public HeldMusic Held => _held;

    public void Hold()
    {
        IsHolding = true;
        _held = new HeldMusic.Home();
    }

    /// <summary>Stops holding and says what music to start now.</summary>
    public HeldMusic Release()
    {
        IsHolding = false;
        return _held;
    }

    /// <summary>The game asked to play a track. True when the hook should skip it.</summary>
    public bool OnPlay(string path)
    {
        if (!IsHolding) return false;

        _held = new HeldMusic.Play(path);
        return true;
    }

    /// <summary>The game asked to fade a track in. True when the hook should skip it.</summary>
    public bool OnFadeIn(string path, float seconds, float volume)
    {
        if (!IsHolding) return false;

        _held = new HeldMusic.FadeIn(path, seconds, volume);
        return true;
    }

    /// <summary>The game stopped its music. Stopping is harmless, so it always runs.</summary>
    public void OnStop()
    {
        if (IsHolding) _held = new HeldMusic.Silence();
    }
}
