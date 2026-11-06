using Orbivo.Core.Audio;

namespace Orbivo.Core.Tests.Audio;

/// <summary>
/// The clock logic against simulated devices: the heard position of a device that runs faster or slower than the
/// system timer, reported with jitter, as WASAPI does every period.
/// </summary>
public sealed class AudioClockTrackerTests
{
    private const int SampleRate = 48_000;
    private const long Frequency = 10_000_000; // Stopwatch ticks per second, as QueryPerformanceCounter on Windows 10/11.
    private const long Start = 5 * Frequency;

    [Fact]
    public void StandingClock_ShowsItsTimeAtAnyMoment()
    {
        var tracker = new AudioClockTracker(SampleRate, Frequency);
        tracker.Stand(TimeSpan.FromSeconds(3).Ticks);
        Assert.Equal(TimeSpan.FromSeconds(3).Ticks, tracker.Current.At(0));
        Assert.Equal(TimeSpan.FromSeconds(3).Ticks, tracker.Current.At(long.MaxValue / 2));
        Assert.False(tracker.Current.IsAdvancingAt(Start));
    }

    [Fact]
    public void Segment_StandsAtItsStartUntilTheFirstFrameIsHeard()
    {
        var tracker = new AudioClockTracker(SampleRate, Frequency);
        long media = TimeSpan.FromSeconds(10).Ticks;
        long heard = Start + (Frequency / 50); // 20 ms latency
        tracker.BeginSegment(0, media, heard);

        Assert.Equal(media, tracker.Current.At(Start));
        Assert.Equal(media, tracker.Current.At(heard));
        Assert.False(tracker.Current.IsAdvancingAt(Start));
        Assert.Equal(media + TimeSpan.FromMilliseconds(100).Ticks, tracker.Current.At(heard + (Frequency / 10)));
        Assert.True(tracker.Current.IsAdvancingAt(heard + (Frequency / 10)));
    }

    [Fact]
    public void EndSegment_StopsTheClockAtTheMediaTimeOfTheEndFrame()
    {
        var tracker = new AudioClockTracker(SampleRate, Frequency);
        tracker.BeginSegment(0, 0, Start);
        tracker.EndSegment(SampleRate); // ends after one second of frames

        Assert.Equal(TimeSpan.FromSeconds(0.5).Ticks, tracker.Current.At(Start + (Frequency / 2)));
        Assert.Equal(TimeSpan.FromSeconds(1).Ticks, tracker.Current.At(Start + (5 * Frequency)));

        tracker.CancelEnd();
        Assert.Equal(TimeSpan.FromSeconds(5).Ticks, tracker.Current.At(Start + (5 * Frequency)));
    }

    [Fact]
    public void FirstHeardPosition_AnchorsTheClockExactly()
    {
        var tracker = new AudioClockTracker(SampleRate, Frequency);

        // The estimate of when the first frame is heard was 7 ms too early.
        tracker.BeginSegment(1000, 0, Start);
        long trueStart = Start + (7 * Frequency / 1000);
        long positionTime = trueStart + (Frequency / 100);
        tracker.Update(positionTime, 1000 + (SampleRate / 100), positionTime);

        Assert.Equal(TimeSpan.FromMilliseconds(10).Ticks, tracker.Current.At(positionTime));
        Assert.Equal(TimeSpan.FromMilliseconds(110).Ticks, tracker.Current.At(positionTime + (Frequency / 10)), TimeSpan.TicksPerMillisecond / 10);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(200, 0.5)]
    [InlineData(-300, 1.0)]
    [InlineData(1000, 1.0)]
    public void TenMinutes_WithDriftAndJitter_StayWithinOneMillisecond(double driftPpm, double jitterMs)
    {
        Simulation result = Simulate(TimeSpan.FromMinutes(10), driftPpm, jitterMs, initialEstimateErrorMs: 5);

        // After settling (2 s), the clock follows the heard position within 1 ms, far inside N2 (±20 ms).
        Assert.True(result.MaxErrorAfterSettleMs < 1.0, $"max error {result.MaxErrorAfterSettleMs:0.000} ms");
        Assert.True(result.MaxErrorMs < 20, $"max error at the start {result.MaxErrorMs:0.000} ms");
        Assert.True(result.Monotonic, "the clock ran backwards between two render frames");
        Assert.Equal(0, result.Resyncs);

        // The drift shown to the operator is an estimate; timestamp jitter moves it a bit.
        double tolerance = 20 + (100 * jitterMs);
        Assert.InRange(result.EstimatedDriftPpm, driftPpm - tolerance, driftPpm + tolerance);
    }

    [Fact]
    public void LargeJump_ReanchorsTheClock()
    {
        var tracker = new AudioClockTracker(SampleRate, Frequency);
        tracker.BeginSegment(0, 0, Start);
        long now = Start + Frequency;
        tracker.Update(now, SampleRate, now);

        // The device suddenly reports 100 ms later (e.g. a glitch in the driver).
        now += Frequency / 100;
        tracker.Update(now, SampleRate + (SampleRate / 100) + (SampleRate / 10), now);
        Assert.Equal(1, tracker.Resyncs);
        Assert.Equal(TimeSpan.FromMilliseconds(1110).Ticks, tracker.Current.At(now));
    }

    private static Simulation Simulate(TimeSpan duration, double driftPpm, double jitterMs, double initialEstimateErrorMs)
    {
        var random = new Random(4711);
        var tracker = new AudioClockTracker(SampleRate, Frequency);
        double speed = 1 + (driftPpm * 1e-6);

        // Segment: stream frame 0 = media 0, truly heard at Start; the estimate is a bit off.
        tracker.BeginSegment(0, 0, Start + (long)(initialEstimateErrorMs * Frequency / 1000));
        long end = Start + (long)(duration.TotalSeconds * Frequency);
        long period = Frequency / 100;
        long renderPeriod = Frequency / 90;
        long nextRender = Start;
        long lastClock = long.MinValue;
        double maxError = 0;
        double maxErrorAfterSettle = 0;
        bool monotonic = true;

        for (long now = Start + period; now < end; now += period)
        {
            // The device reports a position measured shortly before "now", with a jittered timestamp.
            long measuredAt = now - (period / 3);
            double heardFrames = (measuredAt - Start) / (double)Frequency * SampleRate * speed;
            long reportedTimestamp = measuredAt + (long)((random.NextDouble() - 0.5) * 2 * jitterMs * Frequency / 1000);
            tracker.Update(now, heardFrames, reportedTimestamp);
            ClockSnapshot clock = tracker.Current;

            for (; nextRender < now + period; nextRender += renderPeriod)
            {
                long value = clock.At(nextRender);
                long truth = (long)((nextRender - Start) / (double)Frequency * speed * TimeSpan.TicksPerSecond);
                double errorMs = Math.Abs(value - Math.Max(0, truth)) / (double)TimeSpan.TicksPerMillisecond;
                maxError = Math.Max(maxError, errorMs);
                if (nextRender - Start > 2 * Frequency)
                {
                    maxErrorAfterSettle = Math.Max(maxErrorAfterSettle, errorMs);
                }

                monotonic &= value >= lastClock;
                lastClock = value;
            }
        }

        return new Simulation(maxError, maxErrorAfterSettle, monotonic, tracker.Resyncs, tracker.DriftPpm);
    }

    private readonly record struct Simulation(double MaxErrorMs, double MaxErrorAfterSettleMs, bool Monotonic, int Resyncs, double EstimatedDriftPpm);
}
