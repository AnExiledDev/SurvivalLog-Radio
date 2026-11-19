using System;

namespace Radio.Core;

/// <summary>Where decoded sound goes: interleaved floats at any rate and channel count.</summary>
public interface IPcmSink
{
    void Pcm(ReadOnlySpan<float> samples, int channels, int rate);
}

/// <summary>Where sound goes once it is 48 kHz stereo.</summary>
public delegate void StereoWriter(ReadOnlySpan<float> frames);

/// <summary>
/// Turns decoded sound into 48 kHz stereo for the ring. Mono plays on both sides; with more than two channels it
/// keeps the front left and right. Other rates are resampled with 4-point Hermite curves, which is cheap and sounds
/// clean for music. The last few input frames carry over between calls, so a stream decoded one frame at a time
/// has no clicks at the frame edges.
/// </summary>
public sealed class Resampler : IPcmSink
{
    private const int History = 3;

    private readonly StereoWriter _output;
    private float[] _work = new float[8192];
    private float[] _out = new float[8192];
    private int _workFrames;
    private double _position;
    private int _rate;

    public Resampler(StereoWriter output) => _output = output;

    /// <summary>Forget the carried frames: the next sound doesn't follow on from the last.</summary>
    public void Reset()
    {
        _workFrames = 0;
        _rate = 0;
    }

    public void Pcm(ReadOnlySpan<float> samples, int channels, int rate)
    {
        if (channels <= 0 || rate <= 0) return;

        var frames = samples.Length / channels;

        if (frames == 0) return;

        if (rate != _rate)
        {
            _rate = rate;
            _workFrames = 0;
            _position = 1;
        }

        if (rate == SampleRing.Rate)
        {
            WriteStereo(samples, channels, frames);
            return;
        }

        AppendStereo(samples, channels, frames);
        Resample();
    }

    private void WriteStereo(ReadOnlySpan<float> samples, int channels, int frames)
    {
        if (channels == 2)
        {
            _output(samples.Slice(0, frames * 2));
            return;
        }

        Grow(ref _out, frames * 2);
        ToStereo(samples, channels, frames, _out);
        _output(_out.AsSpan(0, frames * 2));
    }

    private void AppendStereo(ReadOnlySpan<float> samples, int channels, int frames)
    {
        if (_workFrames == 0)
        {
            // Start with one frame of silence so the first real frame has a frame before it.
            Grow(ref _work, 2);
            _work[0] = 0;
            _work[1] = 0;
            _workFrames = 1;
        }

        Grow(ref _work, (_workFrames + frames) * 2);
        ToStereo(samples, channels, frames, _work.AsSpan(_workFrames * 2));
        _workFrames += frames;
    }

    private void Resample()
    {
        var step = (double)_rate / SampleRing.Rate;
        var most = (int)((_workFrames - _position) / step) + 2;
        Grow(ref _out, most * 2);
        var made = 0;

        while (true)
        {
            var i = (int)_position;

            if (i + 2 >= _workFrames) break;

            var t = (float)(_position - i);
            _out[made * 2] = Hermite(_work, i, 0, t);
            _out[made * 2 + 1] = Hermite(_work, i, 1, t);
            made++;
            _position += step;
        }

        if (made > 0) _output(_out.AsSpan(0, made * 2));

        // Keep the frame before the next position and everything after it.
        var keepFrom = Math.Max(0, (int)_position - 1);
        var keep = _workFrames - keepFrom;
        Array.Copy(_work, keepFrom * 2, _work, 0, keep * 2);
        _workFrames = keep;
        _position -= keepFrom;
    }

    /// <summary>The curve through frames i-1, i, i+1 and i+2, at t between frames i and i+1.</summary>
    private static float Hermite(float[] w, int i, int side, float t)
    {
        var y0 = w[(i - 1) * 2 + side];
        var y1 = w[i * 2 + side];
        var y2 = w[(i + 1) * 2 + side];
        var y3 = w[(i + 2) * 2 + side];
        var c1 = 0.5f * (y2 - y0);
        var c2 = y0 - 2.5f * y1 + 2f * y2 - 0.5f * y3;
        var c3 = 0.5f * (y3 - y0) + 1.5f * (y1 - y2);

        return ((c3 * t + c2) * t + c1) * t + y1;
    }

    private static void ToStereo(ReadOnlySpan<float> samples, int channels, int frames, Span<float> output)
    {
        for (var f = 0; f < frames; f++)
        {
            var left = samples[f * channels];
            var right = channels == 1 ? left : samples[f * channels + 1];
            output[f * 2] = left;
            output[f * 2 + 1] = right;
        }
    }

    private static void Grow(ref float[] buffer, int length)
    {
        if (buffer.Length < length) Array.Resize(ref buffer, Math.Max(length, buffer.Length * 2));
    }
}
