using System.Diagnostics;
using Orbivo.Core.Playback;

namespace Orbivo.Core.Tests.Playback;

public sealed class FramePacerTests
{
    private const long FrameTicks = 333_333; // 30 fps

    [Fact]
    public void AllSlotsAreFreeAtStart()
    {
        using var pacer = new FramePacer(4);
        var slots = new List<int>();
        while (pacer.TryAcquireFreeSlot(out int slot))
        {
            slots.Add(slot);
        }

        Assert.Equal([0, 1, 2, 3], slots.Order());
    }

    [Fact]
    public void TrySelect_ReturnsFalseUntilTheFirstFrameIsDue()
    {
        using var pacer = new FramePacer(4);
        Assert.False(pacer.TrySelect(long.MaxValue, out _, out _));

        PublishFrames(pacer, 1, firstPts: 1000);
        Assert.False(pacer.TrySelect(999, out _, out _));
        Assert.True(pacer.TrySelect(1000, out _, out long pts));
        Assert.Equal(1000, pts);
    }

    [Fact]
    public void TrySelect_KeepsTheCurrentFrameUntilTheNextIsDue()
    {
        using var pacer = new FramePacer(4);
        PublishFrames(pacer, 2, firstPts: 0);

        // 90 Hz display, 30 fps video: the same frame three times, then the next.
        Assert.True(pacer.TrySelect(0, out int first, out _));
        Assert.True(pacer.TrySelect(FrameTicks / 3, out int again, out _));
        Assert.True(pacer.TrySelect(2 * FrameTicks / 3, out int third, out _));
        Assert.True(pacer.TrySelect(FrameTicks, out int next, out long pts));

        Assert.Equal(first, again);
        Assert.Equal(first, third);
        Assert.NotEqual(first, next);
        Assert.Equal(FrameTicks, pts);
        Assert.Equal(2, pacer.ShownFrames);
        Assert.Equal(0, pacer.DroppedFrames);
    }

    [Fact]
    public void TrySelect_SkipsFramesThatAreAlreadyLate_AndCountsThem()
    {
        using var pacer = new FramePacer(6);
        PublishFrames(pacer, 5, firstPts: 0);

        // The render thread stalled: frames 0..3 are all due at once, only 3 is shown.
        Assert.True(pacer.TrySelect((3 * FrameTicks) + 10, out _, out long pts));

        Assert.Equal(3 * FrameTicks, pts);
        Assert.Equal(3, pacer.DroppedFrames);
        Assert.Equal(1, pacer.ShownFrames);
        Assert.Equal(1, pacer.ReadyCount);
    }

    [Fact]
    public void ReplacedFramesFreeTheirSlots_TheShownFrameStaysReserved()
    {
        using var pacer = new FramePacer(3);
        PublishFrames(pacer, 3, firstPts: 0);
        Assert.False(pacer.TryAcquireFreeSlot(out _));

        Assert.True(pacer.TrySelect(2 * FrameTicks, out int shown, out _));

        var freed = new List<int>();
        while (pacer.TryAcquireFreeSlot(out int slot))
        {
            freed.Add(slot);
        }

        Assert.Equal(2, freed.Count);
        Assert.DoesNotContain(shown, freed);
    }

    [Fact]
    public void Paused_TheSameFrameStays()
    {
        using var pacer = new FramePacer(4);
        PublishFrames(pacer, 3, firstPts: 0);
        Assert.True(pacer.TrySelect(FrameTicks, out int slot, out _));

        for (int frame = 0; frame < 100; frame++)
        {
            Assert.True(pacer.TrySelect(FrameTicks, out int same, out _));
            Assert.Equal(slot, same);
        }

        Assert.Equal(1, pacer.ReadyCount);
    }

    [Fact]
    public async Task WaitForFreeSlot_WakesUpWhenTheRenderThreadFreesOne()
    {
        using var pacer = new FramePacer(2);
        PublishFrames(pacer, 2, firstPts: 0);
        Assert.True(pacer.TrySelect(0, out _, out _));

        Task<TimeSpan> waiter = Task.Run(
            () =>
        {
            var stopwatch = Stopwatch.StartNew();
            while (!pacer.TryAcquireFreeSlot(out _))
            {
                pacer.WaitForFreeSlot(TimeSpan.FromSeconds(5));
            }

            return stopwatch.Elapsed;
        },
            TestContext.Current.CancellationToken);

        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(pacer.TrySelect(FrameTicks, out _, out _));
        TimeSpan waited = await waiter;

        Assert.True(waited < TimeSpan.FromSeconds(4), $"waited {waited}");
    }

    [Fact]
    public void NewGeneration_HoldsTheCurrentFrameUntilANewOneIsDue()
    {
        using var pacer = new FramePacer(6);
        PublishFrames(pacer, 4, firstPts: 0);
        Assert.True(pacer.TrySelect(0, out int held, out long heldPts));

        // Seek to 10 s: the queued frames 1 to 3 are outdated.
        pacer.BeginGeneration();
        long target = 300 * FrameTicks;
        Assert.True(pacer.TrySelect(target, out int stillHeld, out long stillHeldPts));
        Assert.Equal((held, heldPts), (stillHeld, stillHeldPts));
        Assert.Equal(3, pacer.DiscardedFrames);
        Assert.Equal(0, pacer.DroppedFrames);
        Assert.Equal(0, pacer.ReadyCount);

        PublishFrames(pacer, 2, firstPts: target);
        Assert.True(pacer.TrySelect(target, out _, out long pts));
        Assert.Equal(target, pts);
        Assert.Equal(0, pacer.DroppedFrames);
    }

    [Fact]
    public void NewGeneration_FreesTheOutdatedSlots()
    {
        using var pacer = new FramePacer(4);
        PublishFrames(pacer, 4, firstPts: 0);
        Assert.False(pacer.TryAcquireFreeSlot(out _));

        pacer.BeginGeneration();
        pacer.DiscardStaleFrames();

        int free = 0;
        while (pacer.TryAcquireFreeSlot(out _))
        {
            free++;
        }

        Assert.Equal(4, free);
        Assert.Equal(0, pacer.ShownFrames);
    }

    [Fact]
    public void NewGeneration_FramesOfTheNewGenerationStay()
    {
        using var pacer = new FramePacer(6);
        PublishFrames(pacer, 2, firstPts: 0);
        pacer.BeginGeneration();
        PublishFrames(pacer, 2, firstPts: 100 * FrameTicks);

        pacer.DiscardStaleFrames();
        Assert.Equal(2, pacer.ReadyCount);
        Assert.False(pacer.TrySelect(FrameTicks, out _, out _), "old frames must not be shown");
        Assert.True(pacer.TrySelect(100 * FrameTicks, out _, out long pts));
        Assert.Equal(100 * FrameTicks, pts);
    }

    private static void PublishFrames(FramePacer pacer, int count, long firstPts)
    {
        for (int i = 0; i < count; i++)
        {
            Assert.True(pacer.TryAcquireFreeSlot(out int slot));
            pacer.Publish(slot, firstPts + (i * FrameTicks));
        }
    }
}
