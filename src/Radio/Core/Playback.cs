using System;
using System.Collections.Concurrent;

namespace Radio.Core;

/// <summary>Turns compressed frames into sound. Made and used on the reader's own thread.</summary>
public interface IFrameDecoder : IDisposable
{
    /// <summary>Decodes one frame. A format it hasn't seen sets it up again. Throws <see cref="UnsupportedAudio"/> when it can't.</summary>
    void Decode(FrameFormat format, ReadOnlySpan<byte> frame, IPcmSink output);
}

/// <summary>Reads a whole song file, or a link to one, with the system's decoders. Made and used on the reader's thread.</summary>
public interface IFileDecoder : IDisposable
{
    /// <summary>Sends the next block of sound to the output. False at the end.</summary>
    bool Read(IPcmSink output);
}

/// <summary>Opens a song file or link with the system's decoders. Throws <see cref="UnsupportedAudio"/> when it can't.</summary>
public delegate IFileDecoder FileOpener(string pathOrLink);

/// <summary>The audio is in a format this computer can't decode.</summary>
public sealed class UnsupportedAudio : Exception
{
    public UnsupportedAudio(string format) : base(format + " isn't supported") => Format = format;

    /// <summary>A short name of the format, like "Ogg" or "AC-3", for the status line.</summary>
    public string Format { get; }
}

/// <summary>Why a station couldn't be played, for the status line.</summary>
public enum Trouble
{
    /// <summary>No connection, or it dropped and didn't come back.</summary>
    CannotConnect,

    /// <summary>The link leads to a web page, or to something that isn't sound.</summary>
    NotAStream,

    /// <summary>The sound is in a format this mod can't play.</summary>
    Unsupported,

    /// <summary>The server said no, like 403 or 404.</summary>
    Refused,

    /// <summary>The music folder has no songs this computer can play.</summary>
    NoSongs,
}

/// <summary>Something a reader thread tells the game thread. Readers never touch the game or the log themselves.</summary>
public abstract record PlayerEvent
{
    private PlayerEvent()
    {
    }

    /// <summary>Connecting, or connecting again after a drop.</summary>
    public sealed record Connecting : PlayerEvent;

    /// <summary>Sound is coming in.</summary>
    public sealed record Playing(string Format) : PlayerEvent;

    /// <summary>The station sent a new song title.</summary>
    public sealed record Title(string Text) : PlayerEvent;

    /// <summary>It dropped and tries again in a few seconds.</summary>
    public sealed record Retrying(int Seconds, Trouble Why, string Detail) : PlayerEvent;

    /// <summary>It gave up.</summary>
    public sealed record Failed(Trouble Why, string Detail) : PlayerEvent;

    /// <summary>A recording with an end reached it.</summary>
    public sealed record Ended : PlayerEvent;

    /// <summary>A song from the music folder starts once the ring has played up to this frame.</summary>
    public sealed record SongStarts(int Index, long AtFrame) : PlayerEvent;

    /// <summary>A song file couldn't be read and was skipped.</summary>
    public sealed record SongSkipped(int Index, string Detail) : PlayerEvent;

    /// <summary>A line for the mod's log file.</summary>
    public sealed record Note(string Text) : PlayerEvent;
}

/// <summary>One thing playing: a station or the music folder, read on its own thread into its own ring.</summary>
public interface IPlayback
{
    SampleRing Ring { get; }

    bool TryTakeEvent(out PlayerEvent? playerEvent);

    /// <summary>Stops reading. Safe to call more than once and from the game thread.</summary>
    void Stop();
}

/// <summary>The events a reader thread leaves for the game thread.</summary>
public sealed class EventQueue
{
    private const int MaxWaiting = 256;

    private readonly ConcurrentQueue<PlayerEvent> _events = new();

    public void Add(PlayerEvent playerEvent)
    {
        // A game thread that stopped reading must not let this grow forever.
        if (_events.Count < MaxWaiting) _events.Enqueue(playerEvent);
    }

    public bool TryTake(out PlayerEvent? playerEvent) => _events.TryDequeue(out playerEvent);
}

/// <summary>How long to wait before connecting again: longer each time, and a few tries at most for a link that never played.</summary>
public sealed class Backoff
{
    public const int TriesBeforePlaying = 3;
    private static readonly int[] Steps = { 1, 2, 5, 10, 20, 30 };

    private int _step;
    private bool _hasPlayed;
    private int _failures;

    /// <summary>Sound came through, so a later drop starts the waits over.</summary>
    public void Played()
    {
        _hasPlayed = true;
        _step = 0;
        _failures = 0;
    }

    /// <summary>Seconds to wait before the next try, or null to give up.</summary>
    public int? Next()
    {
        _failures++;

        if (!_hasPlayed && _failures >= TriesBeforePlaying) return null;

        var seconds = Steps[Math.Min(_step, Steps.Length - 1)];
        _step++;

        return seconds;
    }
}

/// <summary>
/// Counts listening time toward morale. A record grants morale when it finishes; a station never finishes, so it
/// grants morale after this many minutes of sound instead. The game still caps it at once a day, the same cap the
/// records share.
/// </summary>
public sealed class MoraleClock
{
    private long _lastPlayed;
    private long _counted;

    /// <summary>A new ring: its count starts at zero.</summary>
    public void Restart(long played)
    {
        _lastPlayed = played;
        _counted = 0;
    }

    /// <summary>True when enough time has played since the last grant. Only time while counting is true adds up.</summary>
    public bool Update(long played, bool isCounting, double minutesPerGrant)
    {
        var delta = played - _lastPlayed;
        _lastPlayed = played;

        if (!isCounting || delta <= 0) return false;

        _counted += delta;
        var needed = (long)(Math.Max(1, minutesPerGrant) * 60 * SampleRing.Rate);

        if (_counted < needed) return false;

        _counted = 0;
        return true;
    }
}
