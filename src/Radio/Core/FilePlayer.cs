using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Radio.Core;

/// <summary>
/// Plays the songs in the music folder in order on its own thread, starting at one and going round. It reads ahead
/// into the ring, so it tells the game thread where in the ring each song starts; the game thread shows a song once
/// playback reaches that point.
/// </summary>
public sealed class FilePlayer : IPlayback
{
    public const double BufferSeconds = 8;
    public const double PrebufferSeconds = 0.3;

    private readonly IReadOnlyList<string> _files;
    private readonly int _first;
    private readonly FileOpener _open;
    private readonly CancellationTokenSource _stop = new();
    private readonly EventQueue _events = new();
    private readonly Resampler _resampler;

    public FilePlayer(IReadOnlyList<string> files, int first, FileOpener open)
    {
        _files = files;
        _first = first;
        _open = open;
        Ring = new SampleRing(BufferSeconds, PrebufferSeconds);
        _resampler = new Resampler(WriteToRing);
    }

    public SampleRing Ring { get; }

    private CancellationToken Cancel => _stop.Token;

    public void Start() => new Thread(Run) { IsBackground = true, Name = "Radio music folder" }.Start();

    public bool TryTakeEvent(out PlayerEvent? playerEvent) => _events.TryTake(out playerEvent);

    public void Stop()
    {
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Run()
    {
        if (_files.Count == 0)
        {
            _events.Add(new PlayerEvent.Failed(Trouble.NoSongs, "the folder is empty"));
            return;
        }

        var index = _first >= 0 && _first < _files.Count ? _first : 0;
        var failedInARow = 0;

        while (!Cancel.IsCancellationRequested)
        {
            try
            {
                // A song that decodes to nothing would have the loop spin through the folder at full speed.
                if (!PlaySong(index)) throw new InvalidDataException("the song has no sound");

                failedInARow = 0;
            }
            catch (Exception) when (Cancel.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _events.Add(new PlayerEvent.SongSkipped(index, e is UnsupportedAudio u ? u.Format : e.GetType().Name + ": " + e.Message));
                failedInARow++;

                if (failedInARow >= _files.Count)
                {
                    _events.Add(new PlayerEvent.Failed(Trouble.NoSongs, "none of the songs could be read"));
                    return;
                }
            }

            index = (index + 1) % _files.Count;
            _resampler.Reset();
        }
    }

    /// <summary>False when the song gave no sound at all.</summary>
    private bool PlaySong(int index)
    {
        // Nothing is written between reading this and telling the game thread, so it is exactly where the song starts.
        var start = Ring.WrittenFrames;
        using var song = _open(_files[index]);
        _events.Add(new PlayerEvent.SongStarts(index, start));

        while (song.Read(_resampler))
        {
            if (Cancel.IsCancellationRequested) return true;
        }

        return Ring.WrittenFrames > start;
    }

    private void WriteToRing(ReadOnlySpan<float> frames)
    {
        if (!Ring.WriteAll(frames, Cancel)) throw new OperationCanceledException(Cancel);
    }
}
