using System.Diagnostics;
using System.Threading.Channels;
using Orbivo.Core.Commands;
using Orbivo.Core.Playback;
using Orbivo.Core.Status;
using Orbivo.Media.Tests.Support;
using Orbivo.Xr.Graphics;

namespace Orbivo.Media.Tests;

/// <summary>
/// Seek, loop, blackout and fade (M4) with real decoding. Seek must land on the frame shown at the target
/// (roadmap: ±1 frame; the decoder aims for the exact frame), keep the last picture while it runs and keep the
/// transport state.
/// </summary>
[Collection(HardwareDecodeFixture.Collection)]
public sealed class VideoPlayerSeekTests(HardwareDecodeFixture fixture) : IDisposable
{
    private const double FramesPerSecond = 30;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly long FrameTicks = (long)(TimeSpan.TicksPerSecond / FramesPerSecond);

    private readonly Channel<PlayerCommand> _commands = Channel.CreateUnbounded<PlayerCommand>();
    private VideoPlayer? _player;

    public static TheoryData<double> PausedTargets => [7.5, 0.5, 19.95, 3.0, 12.345, 0.0, 1.0 / 30, 10.0 - 0.001, 6.4];

    public void Dispose()
    {
        _player?.ReleaseRenderThread();
        _commands.Writer.TryComplete();
        _player?.Dispose();
    }

    [Theory]
    [MemberData(nameof(PausedTargets))]
    public void SeekWhilePaused_ShowsTheFrameAtTheTarget(double targetSeconds)
    {
        VideoPlayer player = OpenPlaying(TestVideos.Mono4K);
        RenderFor(player, TimeSpan.FromMilliseconds(300));
        Send(new PauseCommand());
        WaitFor(player, status => status.State == PlaybackState.Paused);

        TimeSpan target = TimeSpan.FromSeconds(targetSeconds);
        (TimeSpan elapsed, VideoFrame frame) = SeekAndWait(player, target, PlaybackState.Paused);

        AssertFrameAt(frame, target);
        Assert.Equal(target, player.GetStatus().Position);
        TestContext.Current.TestOutputHelper?.WriteLine($"Seek to {target} took {elapsed.TotalMilliseconds:0} ms, frame {frame.PtsTicks / (double)FrameTicks:0.0}.");
    }

    [Fact]
    public void SeekBehindTheEnd_ShowsTheLastFrame()
    {
        VideoPlayer player = OpenPlaying(TestVideos.Mono4K);
        Send(new PauseCommand());
        WaitFor(player, status => status.State == PlaybackState.Paused);

        (_, VideoFrame frame) = SeekAndWait(player, TimeSpan.FromSeconds(25), PlaybackState.Paused);

        Assert.Equal(TestVideos.FrameCount - 1, FrameIndex(frame));
    }

    [Fact]
    public void SeekWhilePlaying_ContinuesFromTheTarget()
    {
        VideoPlayer player = OpenPlaying(TestVideos.Mono4K);
        RenderFor(player, TimeSpan.FromMilliseconds(300));

        TimeSpan target = TimeSpan.FromSeconds(10);
        (TimeSpan elapsed, _) = SeekAndWait(player, target, PlaybackState.Playing);
        (VideoFrame last, int distinct) = RenderFor(player, TimeSpan.FromMilliseconds(500));

        Assert.InRange(last.PtsTicks, (target + TimeSpan.FromSeconds(0.35)).Ticks, (target + TimeSpan.FromSeconds(0.7)).Ticks);
        Assert.InRange(distinct, 12, 17);
        TestContext.Current.TestOutputHelper?.WriteLine($"Seek while playing took {elapsed.TotalMilliseconds:0} ms.");
    }

    [Fact]
    public void SeekWhenReady_StaysBlack_AndPlayStartsAtTheTarget()
    {
        VideoPlayer player = StartPlayer();
        Send(new OpenMediaCommand(TestVideos.Mono4K));
        WaitFor(player, status => status.State == PlaybackState.Ready);

        Send(new SeekCommand(TimeSpan.FromSeconds(5)));
        WaitFor(player, status => !status.Seeking && status.Position == TimeSpan.FromSeconds(5), pump: true);
        Assert.False(player.TryGetFrame(Stopwatch.GetTimestamp(), out _), "Ready stays black (F6)");
        Assert.Equal(PlaybackState.Ready, player.GetStatus().State);

        Send(new PlayCommand());
        WaitFor(player, status => status.State == PlaybackState.Playing);
        (VideoFrame first, _) = RenderFor(player, TimeSpan.FromMilliseconds(50));
        Assert.InRange(first.PtsTicks, TimeSpan.FromSeconds(5).Ticks, TimeSpan.FromSeconds(5.2).Ticks);
    }

    [Fact]
    public void StopWhenReady_GoesBackToTheBeginning()
    {
        VideoPlayer player = StartPlayer();
        Send(new OpenMediaCommand(TestVideos.Mono4K));
        WaitFor(player, status => status.State == PlaybackState.Ready);
        Send(new SeekCommand(TimeSpan.FromSeconds(5)));
        WaitFor(player, status => !status.Seeking && status.Position == TimeSpan.FromSeconds(5), pump: true);

        Send(new StopCommand());
        WaitFor(player, status => !status.Seeking && status.Position == TimeSpan.Zero, pump: true);
        Assert.Equal(PlaybackState.Ready, player.GetStatus().State);
    }

    [Fact]
    public void SeekAfterTheEnd_PausesAtTheTarget()
    {
        VideoPlayer player = OpenPlaying(TestVideos.Mono4K);
        SeekAndWait(player, TimeSpan.FromSeconds(19.5), PlaybackState.Playing);
        WaitFor(player, status => status.State == PlaybackState.Ended, pump: true);

        (_, VideoFrame frame) = SeekAndWait(player, TimeSpan.FromSeconds(2), PlaybackState.Paused);
        AssertFrameAt(frame, TimeSpan.FromSeconds(2));

        Send(new PlayCommand());
        WaitFor(player, status => status.State == PlaybackState.Playing);
        (VideoFrame later, _) = RenderFor(player, TimeSpan.FromMilliseconds(300));
        Assert.True(later.PtsTicks > frame.PtsTicks, "plays on from the target");
    }

    [Fact]
    public void ManySeeksInARow_EndAtTheLastTarget()
    {
        VideoPlayer player = OpenPlaying(TestVideos.Mono4K);
        Send(new PauseCommand());
        WaitFor(player, status => status.State == PlaybackState.Paused);

        var random = new Random(4);
        for (int i = 0; i < 25; i++)
        {
            Send(new SeekCommand(TimeSpan.FromSeconds(random.NextDouble() * 19)));
            player.TryGetFrame(Stopwatch.GetTimestamp(), out _);
            Thread.Sleep(random.Next(0, 15));
        }

        (_, VideoFrame frame) = SeekAndWait(player, TimeSpan.FromSeconds(8), PlaybackState.Paused);
        AssertFrameAt(frame, TimeSpan.FromSeconds(8));
        Assert.Null(player.GetStatus().Error);
    }

    [Fact]
    public void Loop_StartsAgainAtTheBeginning()
    {
        VideoPlayer player = OpenPlaying(TestVideos.Mono4K);
        Send(new SetLoopCommand(true));
        SeekAndWait(player, TimeSpan.FromSeconds(19.4), PlaybackState.Playing);

        (VideoFrame last, _) = RenderFor(player, TimeSpan.FromSeconds(1.5));

        PlayerStatus status = player.GetStatus();
        Assert.True(status.Loop);
        Assert.Equal(PlaybackState.Playing, status.State);
        Assert.InRange(last.PtsTicks, 0, TimeSpan.FromSeconds(1.2).Ticks);
        Assert.InRange(status.Position, TimeSpan.Zero, TimeSpan.FromSeconds(1.2));
    }

    [Fact]
    public void BlackoutAndFade_SetTheOutputBrightness()
    {
        VideoPlayer player = StartPlayer();
        Assert.Equal(1f, player.GetOutputBrightness(Stopwatch.GetTimestamp()));

        Send(new SetBlackoutCommand(true));
        WaitFor(player, status => status.Output.TowardsBlack);
        Assert.Equal(0f, player.GetOutputBrightness(Stopwatch.GetTimestamp()));

        Send(new FadeCommand(ToBlack: false, TimeSpan.FromMilliseconds(400)));
        WaitFor(player, status => status.Output.Fading);
        float during = player.GetOutputBrightness(Stopwatch.GetTimestamp());
        Assert.InRange(during, 0f, 0.999f);
        WaitFor(player, status => !status.Output.Fading && status.Output.Level == 1f);
        Assert.Equal(1f, player.GetOutputBrightness(Stopwatch.GetTimestamp()));
    }

    [Fact]
    public void Seek8K_IsFastEnoughToScrub()
    {
        VideoPlayer player = OpenPlaying(TestVideos.TopBottom8K);
        Send(new PauseCommand());
        WaitFor(player, status => status.State == PlaybackState.Paused);

        var times = new List<double>();
        foreach (double seconds in new[] { 15.5, 3.2, 9.9, 0.0, 18.0 })
        {
            TimeSpan target = TimeSpan.FromSeconds(seconds);
            (TimeSpan elapsed, VideoFrame frame) = SeekAndWait(player, target, PlaybackState.Paused);
            AssertFrameAt(frame, target);
            times.Add(elapsed.TotalMilliseconds);
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"8K TB seek times (ms): {string.Join(", ", times.Select(t => t.ToString("0")))}");
        Assert.True(times.Max() < 3000, $"slowest seek {times.Max():0} ms");
    }

    private static int FrameIndex(VideoFrame frame) => (int)Math.Round(frame.PtsTicks / (double)FrameTicks);

    /// <summary>The frame shown at <paramref name="target"/> is the newest one starting at or before it.</summary>
    private static void AssertFrameAt(VideoFrame frame, TimeSpan target)
    {
        int expected = (int)Math.Floor((target.Ticks + 1) / (double)FrameTicks);
        expected = Math.Min(expected, TestVideos.FrameCount - 1);
        Assert.True(
            FrameIndex(frame) == expected,
            $"target {target}: expected frame {expected}, shown frame {FrameIndex(frame)} (pts {TimeSpan.FromTicks(frame.PtsTicks)})");
    }

    /// <summary>
    /// Sends a seek and takes frames like a render loop until it is done. Asserts that the headset never goes black
    /// meanwhile (it holds the last frame) unless it was black before.
    /// </summary>
    private (TimeSpan Elapsed, VideoFrame Frame) SeekAndWait(VideoPlayer player, TimeSpan target, PlaybackState expectedState)
    {
        bool hadFrame = player.TryGetFrame(Stopwatch.GetTimestamp(), out _);
        var stopwatch = Stopwatch.StartNew();
        Send(new SeekCommand(target));

        // The command is handled asynchronously: wait for the clock to stand at the target first.
        long expectedPosition = Math.Min(target.Ticks, TimeSpan.FromSeconds(20).Ticks - FrameTicks);
        bool sawBlack = false;
        while (true)
        {
            Assert.True(stopwatch.Elapsed < Timeout, $"seek to {target} did not finish, status {player.GetStatus()}");
            if (!player.TryGetFrame(Stopwatch.GetTimestamp(), out _))
            {
                sawBlack = true;
            }

            PlayerStatus status = player.GetStatus();
            Assert.Null(status.Error);
            bool atTarget = expectedState == PlaybackState.Playing
                ? status.Position.Ticks >= expectedPosition && status.Position.Ticks < expectedPosition + TimeSpan.FromSeconds(0.5).Ticks
                : status.Position.Ticks == expectedPosition;
            if (!status.Seeking && status.State == expectedState && atTarget)
            {
                break;
            }

            Thread.Sleep(5);
        }

        TimeSpan elapsed = stopwatch.Elapsed;
        Assert.True(player.TryGetFrame(Stopwatch.GetTimestamp(), out VideoFrame frame), "a frame after the seek");
        Assert.False(hadFrame && sawBlack, "the headset went black during the seek instead of holding the last frame");
        return (elapsed, frame);
    }

    private VideoPlayer StartPlayer()
    {
        GraphicsDevice device = fixture.RequireHardware();
        _player = new VideoPlayer(device.Device, _commands.Reader, new VideoPlayerOptions(StereoLayout: null), TestOutputLoggerFactory.Instance);
        _player.Start();
        return _player;
    }

    private VideoPlayer OpenPlaying(string path)
    {
        VideoPlayer player = StartPlayer();
        Send(new OpenMediaCommand(path, Play: true));
        WaitFor(player, status => status.State == PlaybackState.Playing);
        return player;
    }

    private void Send(PlayerCommand command)
    {
        Assert.True(_commands.Writer.TryWrite(command));
    }

    private static (VideoFrame Last, int DistinctFrames) RenderFor(VideoPlayer player, TimeSpan duration)
    {
        VideoFrame last = default;
        long lastPts = long.MinValue;
        int distinct = 0;
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < duration)
        {
            if (player.TryGetFrame(Stopwatch.GetTimestamp(), out VideoFrame frame))
            {
                last = frame;
                if (frame.PtsTicks != lastPts)
                {
                    lastPts = frame.PtsTicks;
                    distinct++;
                }
            }

            Thread.Sleep(10);
        }

        Assert.True(lastPts != long.MinValue, "no frame shown");
        return (last, distinct);
    }

    /// <summary>Waits for a status condition. With <paramref name="pump"/> it takes frames like a render loop meanwhile.</summary>
    private static void WaitFor(VideoPlayer player, Func<PlayerStatus, bool> condition, bool pump = false)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < Timeout)
        {
            PlayerStatus status = player.GetStatus();
            if (condition(status))
            {
                return;
            }

            Assert.False(status.State == PlaybackState.Failed, $"player failed: {status.Error}");
            if (pump)
            {
                player.TryGetFrame(Stopwatch.GetTimestamp(), out _);
            }

            Thread.Sleep(5);
        }

        Assert.Fail($"Timeout, status {player.GetStatus()}.");
    }
}
