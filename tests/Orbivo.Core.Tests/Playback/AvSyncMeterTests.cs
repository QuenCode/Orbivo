using Orbivo.Core.Playback;

namespace Orbivo.Core.Tests.Playback;

public sealed class AvSyncMeterTests
{
    private const long Frequency = 1_000_000;

    [Fact]
    public void CompletesAWindowEverySecond_WithMeanMinAndMax()
    {
        var meter = new AvSyncMeter(Frequency);
        Assert.Equal(0, meter.Current.WindowFrames);

        long t = 10 * Frequency;
        for (int i = 0; i < 30; i++)
        {
            // Deviations 0, 5, 10, 0, 5, 10 … ms
            meter.Record(t, (i % 3) * 5 * TimeSpan.TicksPerMillisecond);
            t += Frequency / 30;
        }

        meter.Record(10 * Frequency + Frequency, 0); // one second after the first frame: completes the window
        AvSyncStats stats = meter.Current;
        Assert.Equal(31, stats.WindowFrames);
        Assert.Equal(0, stats.WindowMinMs, 6);
        Assert.Equal(10, stats.WindowMaxMs, 6);
        Assert.Equal(150.0 / 31, stats.WindowMeanMs, 6);
        Assert.Equal(31, stats.TotalFrames);
        Assert.Equal(0, stats.TotalOutsideLimit);
    }

    [Fact]
    public void CountsFramesOutsideTheLimit_AndResetStartsTheTotalsAgain()
    {
        var meter = new AvSyncMeter(Frequency);
        long t = Frequency;
        meter.Record(t, 25 * TimeSpan.TicksPerMillisecond);
        meter.Record(t + Frequency, -21 * TimeSpan.TicksPerMillisecond);
        AvSyncStats stats = meter.Current;
        Assert.Equal(2, stats.TotalOutsideLimit);
        Assert.Equal(-21, stats.TotalMinMs, 6);
        Assert.Equal(25, stats.TotalMaxMs, 6);

        meter.Reset();
        meter.Record(t + (2 * Frequency), 0);
        meter.Record(t + (3 * Frequency) + 1, 0);
        stats = meter.Current;
        Assert.Equal(2, stats.TotalFrames);
        Assert.Equal(0, stats.TotalOutsideLimit);
    }

    [Fact]
    public void Record_DoesNotAllocate()
    {
        var meter = new AvSyncMeter(Frequency);
        meter.Record(1, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            meter.Record(i * (Frequency / 90), i % 7);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
