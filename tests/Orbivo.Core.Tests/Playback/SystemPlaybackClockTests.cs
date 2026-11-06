using Orbivo.Core.Playback;

namespace Orbivo.Core.Tests.Playback;

public sealed class SystemPlaybackClockTests
{
    [Fact]
    public void NewClock_StandsAtZero()
    {
        var time = new FakeTimeProvider();
        var clock = new SystemPlaybackClock(time);
        time.AdvanceMilliseconds(500);

        Assert.False(clock.IsRunning);
        Assert.Equal(0, clock.MediaTicks);
    }

    [Fact]
    public void Start_RunsFromTheGivenMediaTime()
    {
        var time = new FakeTimeProvider();
        var clock = new SystemPlaybackClock(time);
        clock.Start(Ms(1000));
        time.AdvanceMilliseconds(250);

        Assert.True(clock.IsRunning);
        Assert.Equal(Ms(1250), clock.MediaTicks);
    }

    [Fact]
    public void GetMediaTicks_MapsFutureDisplayTimestamps()
    {
        var time = new FakeTimeProvider();
        var clock = new SystemPlaybackClock(time);
        clock.Start(0);

        // The headset shows the frame about 20 ms from now.
        Assert.Equal(Ms(20), clock.GetMediaTicks(time.Timestamp + 20_000));
    }

    [Fact]
    public void GetMediaTicks_NeverGoesBeforeTheStart()
    {
        var time = new FakeTimeProvider();
        var clock = new SystemPlaybackClock(time);
        clock.Start(Ms(100));

        Assert.Equal(Ms(100), clock.GetMediaTicks(time.Timestamp - 50_000));
    }

    [Fact]
    public void Pause_StandsStill_AndResumeContinuesFromThere()
    {
        var time = new FakeTimeProvider();
        var clock = new SystemPlaybackClock(time);
        clock.Start(0);
        time.AdvanceMilliseconds(300);
        clock.Pause();
        time.AdvanceMilliseconds(5000);

        Assert.Equal(Ms(300), clock.MediaTicks);

        clock.Resume();
        time.AdvanceMilliseconds(100);
        Assert.Equal(Ms(400), clock.MediaTicks);
    }

    [Fact]
    public void Resume_WhileRunning_ChangesNothing()
    {
        var time = new FakeTimeProvider();
        var clock = new SystemPlaybackClock(time);
        clock.Start(0);
        time.AdvanceMilliseconds(100);
        clock.Resume();
        time.AdvanceMilliseconds(100);

        Assert.Equal(Ms(200), clock.MediaTicks);
    }

    [Fact]
    public void Stop_StandsAtTheGivenTime()
    {
        var time = new FakeTimeProvider();
        var clock = new SystemPlaybackClock(time);
        clock.Start(Ms(5000));
        time.AdvanceMilliseconds(100);
        clock.Stop();
        time.AdvanceMilliseconds(100);

        Assert.False(clock.IsRunning);
        Assert.Equal(0, clock.MediaTicks);
    }

    private static long Ms(double milliseconds) => (long)(milliseconds * TimeSpan.TicksPerMillisecond);
}
