using Orbivo.Core.Audio;

namespace Orbivo.Core.Tests.Audio;

public sealed class AudioPlaybackClockTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    [Fact]
    public void Publish_RunsTheClock()
    {
        var clock = new AudioPlaybackClock();
        clock.Publish(new ClockSnapshot(1000, 5 * Second, 1.0, 5 * Second, long.MaxValue), 0);
        Assert.Equal(5 * Second, clock.GetMediaTicks(500));
        Assert.Equal((5 * Second) + 2000, clock.GetMediaTicks(3000));
    }

    [Fact]
    public void RequestHold_StandsAtOnce_UntilTheAudioThreadTookItOver()
    {
        var clock = new AudioPlaybackClock();
        var running = new ClockSnapshot(0, 0, 1.0, 0, long.MaxValue);
        clock.Publish(running, 0);

        int hold = clock.RequestHold(42 * Second);
        Assert.Equal(42 * Second, clock.GetMediaTicks(Second));
        Assert.Equal(ClockSnapshot.Standing(42 * Second), clock.Snapshot);

        // The audio thread publishes once more before it sees the request: the hold still wins.
        clock.Publish(running, 0);
        Assert.Equal(42 * Second, clock.GetMediaTicks(Second));

        // Taken over: from now on the published clock counts again.
        clock.Publish(new ClockSnapshot(0, 42 * Second, 1.0, 42 * Second, long.MaxValue), hold);
        Assert.Equal((42 * Second) + 10, clock.GetMediaTicks(10));
    }

    [Fact]
    public void ReadsAndWrites_DoNotAllocate()
    {
        var clock = new AudioPlaybackClock();
        clock.Publish(ClockSnapshot.Standing(1), 0);
        _ = clock.GetMediaTicks(0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            clock.Publish(new ClockSnapshot(i, i, 1.0, 0, long.MaxValue), 0);
            _ = clock.GetMediaTicks(i);
            _ = clock.Snapshot;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
