using System;
using System.Threading;

namespace Radio.Core;

/// <summary>
/// The sound waiting to be played: 48 kHz stereo floats. One thread writes (the stream or file reader) and one reads
/// (FMOD's mixer, through the stream callback). Reading never blocks and never allocates. When it runs dry it plays
/// silence until enough has built up again, and each time that happens it waits for twice as much, so a connection
/// slower than the station stops now and then instead of every few seconds.
/// </summary>
public sealed class SampleRing
{
    public const int Rate = 48000;
    public const int Channels = 2;

    private readonly float[] _samples;
    private readonly int _maxPrebufferFrames;

    /// <summary>Only the reader changes it.</summary>
    private int _prebufferFrames;
    private long _written;
    private long _read;
    private int _isBuffering = 1;
    private int _isFinished;

    /// <param name="seconds">How much sound it holds.</param>
    /// <param name="prebufferSeconds">How much has to be waiting before it starts. It doubles each time it runs dry.</param>
    public SampleRing(double seconds, double prebufferSeconds)
    {
        _samples = new float[(int)(seconds * Rate) * Channels];
        _prebufferFrames = (int)(prebufferSeconds * Rate);
        _maxPrebufferFrames = CapacityFrames * 3 / 4;
    }

    public int CapacityFrames => _samples.Length / Channels;

    /// <summary>Frames the reader has played so far, counting only real sound, not the silence while buffering.</summary>
    public long PlayedFrames => Volatile.Read(ref _read);

    /// <summary>Frames the writer has put in so far.</summary>
    public long WrittenFrames => Volatile.Read(ref _written);

    public int WaitingFrames => (int)(WrittenFrames - PlayedFrames);

    /// <summary>True while it plays silence because not enough sound is waiting.</summary>
    public bool IsBuffering => Volatile.Read(ref _isBuffering) == 1;

    /// <summary>The writer is done, so what is left plays out even when it is less than the prebuffer.</summary>
    public void Finish() => Volatile.Write(ref _isFinished, 1);

    /// <summary>Puts in as much as fits and says how many frames that was.</summary>
    public int Write(ReadOnlySpan<float> frames)
    {
        var count = Math.Min(frames.Length / Channels, CapacityFrames - WaitingFrames);

        if (count <= 0) return 0;

        var start = (int)(_written % CapacityFrames) * Channels;
        var first = Math.Min(count * Channels, _samples.Length - start);
        frames.Slice(0, first).CopyTo(_samples.AsSpan(start));
        frames.Slice(first, count * Channels - first).CopyTo(_samples);
        Volatile.Write(ref _written, _written + count);

        return count;
    }

    /// <summary>Puts in all of it, waiting while the ring is full. False when cancelled first.</summary>
    public bool WriteAll(ReadOnlySpan<float> frames, CancellationToken cancel)
    {
        while (frames.Length >= Channels)
        {
            if (cancel.IsCancellationRequested) return false;

            var count = Write(frames);
            frames = frames.Slice(count * Channels);

            if (count == 0 && cancel.WaitHandle.WaitOne(20)) return false;
        }

        return true;
    }

    /// <summary>Fills the output with sound, or with silence where there is none yet.</summary>
    public void Read(Span<float> output)
    {
        var wanted = output.Length / Channels;
        var waiting = WaitingFrames;

        if (Volatile.Read(ref _isBuffering) == 1)
        {
            var isEnough = waiting >= Math.Min(_prebufferFrames, CapacityFrames) || (waiting > 0 && Volatile.Read(ref _isFinished) == 1);

            if (!isEnough)
            {
                output.Clear();
                return;
            }

            Volatile.Write(ref _isBuffering, 0);
        }

        var count = Math.Min(wanted, waiting);
        var start = (int)(_read % CapacityFrames) * Channels;
        var first = Math.Min(count * Channels, _samples.Length - start);
        _samples.AsSpan(start, first).CopyTo(output);
        _samples.AsSpan(0, count * Channels - first).CopyTo(output.Slice(first));
        output.Slice(count * Channels).Clear();
        Volatile.Write(ref _read, _read + count);

        if (count < wanted) RunDry();
    }

    private void RunDry()
    {
        Volatile.Write(ref _isBuffering, 1);

        if (_prebufferFrames < _maxPrebufferFrames) _prebufferFrames = Math.Min(_prebufferFrames * 2, _maxPrebufferFrames);
    }
}
