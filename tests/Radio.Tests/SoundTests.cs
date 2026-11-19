using Radio.Core;
using Xunit;

namespace Radio.Tests;

public class SampleRingTests
{
    private static float[] Frames(int count, float value) => Enumerable.Repeat(value, count * 2).ToArray();

    private static float[] Ramp(int from, int count) => Enumerable.Range(from * 2, count * 2).Select(i => (float)i).ToArray();

    private static float[] Read(SampleRing ring, int frames)
    {
        var output = new float[frames * 2];
        ring.Read(output);

        return output;
    }

    [Fact]
    public void ANewRingPlaysSilenceUntilEnoughSoundIsWaiting()
    {
        var ring = new SampleRing(1, 0.5);

        ring.Write(Frames(12000, 1));

        Assert.All(Read(ring, 480), s => Assert.Equal(0, s));
        Assert.True(ring.IsBuffering);
        Assert.Equal(0, ring.PlayedFrames);

        ring.Write(Frames(12000, 1));

        Assert.All(Read(ring, 480), s => Assert.Equal(1, s));
        Assert.False(ring.IsBuffering);
        Assert.Equal(480, ring.PlayedFrames);
    }

    [Fact]
    public void SoundComesOutInOrderAcrossTheEndOfTheRing()
    {
        var ring = new SampleRing(0.01, 0);

        ring.Write(Ramp(0, 300));
        Assert.Equal(Ramp(0, 300), Read(ring, 300));

        ring.Write(Ramp(300, 300));
        Assert.Equal(Ramp(300, 300), Read(ring, 300));
    }

    [Fact]
    public void AFullRingTakesOnlyWhatFits()
    {
        var ring = new SampleRing(0.01, 0);

        Assert.Equal(480, ring.Write(Frames(600, 1)));
        Assert.Equal(0, ring.Write(Frames(10, 1)));
        Assert.Equal(480, ring.WaitingFrames);
    }

    /// <summary>
    /// A connection slower than the station runs dry again and again. Waiting twice as long each time makes it stop
    /// less often for the same amount of silence.
    /// </summary>
    [Fact]
    public void EachTimeItRunsDryItWaitsForTwiceAsMuch()
    {
        var ring = new SampleRing(1, 0.1);

        ring.Write(Frames(4800, 1));
        Read(ring, 4800);

        Assert.All(Read(ring, 100), s => Assert.Equal(0, s));
        Assert.True(ring.IsBuffering);

        ring.Write(Frames(9599, 1));

        Assert.All(Read(ring, 100), s => Assert.Equal(0, s));

        ring.Write(Frames(1, 1));

        Assert.All(Read(ring, 100), s => Assert.Equal(1, s));
    }

    [Fact]
    public void TheWaitStopsGrowingAtThreeQuartersOfTheRing()
    {
        var ring = new SampleRing(1, 0.5);

        for (var dry = 0; dry < 3; dry++)
        {
            ring.Write(Frames(dry == 0 ? 24000 : 36000, 1));
            Assert.All(Read(ring, 100), s => Assert.Equal(1, s));
            Read(ring, ring.WaitingFrames + 100);
        }

        ring.Write(Frames(35999, 1));
        Assert.All(Read(ring, 100), s => Assert.Equal(0, s));

        ring.Write(Frames(1, 1));
        Assert.All(Read(ring, 100), s => Assert.Equal(1, s));
    }

    [Fact]
    public void APartlyFilledReadEndsInSilence()
    {
        var ring = new SampleRing(1, 0);

        ring.Write(Frames(50, 1));
        var output = Read(ring, 100);

        Assert.All(output.Take(100), s => Assert.Equal(1, s));
        Assert.All(output.Skip(100), s => Assert.Equal(0, s));
        Assert.Equal(50, ring.PlayedFrames);
    }

    [Fact]
    public void TheLastBitOfASongPlaysOnceTheWriterIsDone()
    {
        var ring = new SampleRing(1, 0.5);

        ring.Write(Frames(100, 1));
        Assert.All(Read(ring, 200), s => Assert.Equal(0, s));

        ring.Finish();
        var output = Read(ring, 200);

        Assert.All(output.Take(200), s => Assert.Equal(1, s));
        Assert.All(output.Skip(200), s => Assert.Equal(0, s));
        Assert.Equal(100, ring.PlayedFrames);
    }

    [Fact]
    public void AWriterWaitingOnAFullRingLetsGoWhenStopped()
    {
        var ring = new SampleRing(0.01, 0);
        using var stop = new CancellationTokenSource(50);

        Assert.False(ring.WriteAll(Frames(1000, 1), stop.Token));
    }

    [Fact]
    public void AWriterWaitingOnAFullRingFinishesOnceTheReaderCatchesUp()
    {
        var ring = new SampleRing(0.01, 0);
        var reader = new Thread(() =>
        {
            var output = new float[200];

            while (ring.WrittenFrames < 1000) ring.Read(output);
        });

        reader.Start();

        Assert.True(ring.WriteAll(Frames(1000, 1), CancellationToken.None));
        reader.Join();
    }
}

public class ResamplerTests
{
    private readonly List<float> _out = new();
    private readonly Resampler _resampler;

    public ResamplerTests() => _resampler = new Resampler(frames => _out.AddRange(frames.ToArray()));

    private void Feed(float[] samples, int channels, int rate, int framesPerCall)
    {
        for (var f = 0; f * channels < samples.Length; f += framesPerCall)
        {
            var from = f * channels;

            _resampler.Pcm(samples.AsSpan(from, Math.Min(framesPerCall * channels, samples.Length - from)), channels, rate);
        }
    }

    [Fact]
    public void StereoAt48kHzPassesStraightThrough()
    {
        var input = Enumerable.Range(0, 2000).Select(i => i / 2000f).ToArray();

        Feed(input, 2, 48000, 333);

        Assert.Equal(input, _out);
    }

    [Fact]
    public void MonoPlaysOnBothSides()
    {
        _resampler.Pcm(new[] { 0.1f, 0.2f, 0.3f }, 1, 48000);

        Assert.Equal(new[] { 0.1f, 0.1f, 0.2f, 0.2f, 0.3f, 0.3f }, _out);
    }

    [Fact]
    public void SurroundSoundKeepsTheFrontLeftAndRight()
    {
        _resampler.Pcm(new[] { 1f, 2f, 3f, 4f, 5f, 6f, 11f, 12f, 13f, 14f, 15f, 16f }, 6, 48000);

        Assert.Equal(new[] { 1f, 2f, 11f, 12f }, _out);
    }

    [Fact]
    public void A44kHzStreamComesOutAt48kHz()
    {
        Feed(new float[44100 * 2], 2, 44100, 1152);

        // A few frames stay behind, waiting for the frames after them.
        Assert.InRange(_out.Count / 2, 47990, 48000);
    }

    [Fact]
    public void AToneKeepsItsPitchAndShapeAcrossFrameEdges()
    {
        const double Hz = 1000;
        var input = new float[44100 * 2];

        for (var n = 0; n < 44100; n++) input[n * 2] = input[n * 2 + 1] = (float)Math.Sin(2 * Math.PI * Hz * n / 44100);

        Feed(input, 2, 44100, 1152);

        var worst = 0.0;

        // The first few frames lean on the silence before the start.
        for (var n = 4; n < _out.Count / 2; n++) worst = Math.Max(worst, Math.Abs(_out[n * 2] - Math.Sin(2 * Math.PI * Hz * n / 48000)));

        Assert.True(worst < 0.01, $"the resampled tone is off by {worst}");
    }

    [Fact]
    public void ASteadySoundStaysSteadyAcrossFrameEdges()
    {
        Feed(Enumerable.Repeat(0.5f, 22050 * 2).ToArray(), 2, 22050, 576);

        // The first few frames lean on the silence before the start.
        Assert.All(_out.Skip(8), s => Assert.InRange(s, 0.499999f, 0.500001f));
    }
}

public class BackoffTests
{
    [Fact]
    public void ALinkThatNeverPlayedGetsAFewTriesThenGivesUp()
    {
        var backoff = new Backoff();

        Assert.Equal(new int?[] { 1, 2, null }, new[] { backoff.Next(), backoff.Next(), backoff.Next() });
    }

    [Fact]
    public void AStationThatPlayedKeepsTryingWithLongerWaits()
    {
        var backoff = new Backoff();
        backoff.Played();

        var waits = Enumerable.Range(0, 8).Select(_ => backoff.Next()).ToArray();

        Assert.Equal(new int?[] { 1, 2, 5, 10, 20, 30, 30, 30 }, waits);
    }

    [Fact]
    public void SoundComingBackStartsTheWaitsOver()
    {
        var backoff = new Backoff();
        backoff.Played();
        backoff.Next();
        backoff.Next();
        backoff.Next();

        backoff.Played();

        Assert.Equal(1, backoff.Next());
    }
}

public class MoraleClockTests
{
    private const long Minute = 60L * SampleRing.Rate;

    [Fact]
    public void MoraleComesAfterTheSetMinutesOfSoundAndThenAgain()
    {
        var clock = new MoraleClock();
        clock.Restart(0);

        var grants = Enumerable.Range(1, 6).Select(m => clock.Update(m * Minute, isCounting: true, minutesPerGrant: 3)).ToArray();

        Assert.Equal(new[] { false, false, true, false, false, true }, grants);
    }

    [Fact]
    public void TimeThatDoesNotCountIsLeftOut()
    {
        var clock = new MoraleClock();
        clock.Restart(0);

        Assert.False(clock.Update(5 * Minute, isCounting: false, minutesPerGrant: 3));
        Assert.False(clock.Update(7 * Minute, isCounting: true, minutesPerGrant: 3));
        Assert.True(clock.Update(8 * Minute, isCounting: true, minutesPerGrant: 3));
    }

    [Fact]
    public void ANewRingCountsFromItsOwnStart()
    {
        var clock = new MoraleClock();
        clock.Restart(0);
        clock.Update(2 * Minute, isCounting: true, minutesPerGrant: 3);

        clock.Restart(0);

        Assert.False(clock.Update(2 * Minute, isCounting: true, minutesPerGrant: 3));
        Assert.True(clock.Update(3 * Minute, isCounting: true, minutesPerGrant: 3));
    }

    [Fact]
    public void LessThanAMinuteIsTreatedAsOneMinute()
    {
        var clock = new MoraleClock();
        clock.Restart(0);

        Assert.False(clock.Update(Minute / 2, isCounting: true, minutesPerGrant: 0));
        Assert.True(clock.Update(Minute, isCounting: true, minutesPerGrant: 0));
    }
}
