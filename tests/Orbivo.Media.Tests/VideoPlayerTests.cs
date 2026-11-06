using System.Diagnostics;
using System.Threading.Channels;
using Orbivo.Core.Commands;
using Orbivo.Core.Playback;
using Orbivo.Core.Status;
using Orbivo.Media.Tests.Support;
using Orbivo.Xr.Graphics;

namespace Orbivo.Media.Tests;

/// <summary>
/// The player as the app drives it: commands through the channel (CLAUDE.md rule 3), status snapshots back,
/// frames for the render thread chosen by the system clock.
/// </summary>
[Collection(HardwareDecodeFixture.Collection)]
public sealed class VideoPlayerTests(HardwareDecodeFixture fixture)
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(15);

    [Fact]
    public void OpenPlayPauseStop()
    {
        GraphicsDevice device = fixture.RequireHardware();
        string path = TestVideos.Mono4K;
        var commands = Channel.CreateUnbounded<PlayerCommand>();
        using var player = new VideoPlayer(device.Device, commands.Reader, new VideoPlayerOptions(StereoLayout: null), TestOutputLoggerFactory.Instance);
        player.Start();

        try
        {
            Send(commands, new OpenMediaCommand(path));
            WaitForState(player, PlaybackState.Ready);
            PlayerStatus ready = player.GetStatus();
            Assert.True(ready.Video?.HardwareDecoding);
            Assert.Equal(TimeSpan.Zero, ready.Position);
            Assert.InRange(ready.Duration.TotalSeconds, 19.9, 20.1);
            Assert.False(player.TryGetFrame(Stopwatch.GetTimestamp(), out _), "black until Play");

            Send(commands, new PlayCommand());
            WaitForState(player, PlaybackState.Playing);
            (VideoFrame playing, int distinctFrames) = RenderFor(player, TimeSpan.FromSeconds(1));
            Assert.InRange(playing.PtsTicks, TimeSpan.FromSeconds(0.8).Ticks, TimeSpan.FromSeconds(1.2).Ticks);
            Assert.InRange(distinctFrames, 25, 31);
            Assert.Equal((4096, 2048), (playing.Width, playing.Height));
            Assert.Equal(0, player.GetStatus().DroppedFrames);

            Send(commands, new PauseCommand());
            WaitForState(player, PlaybackState.Paused);
            TimeSpan pausedAt = player.GetStatus().Position;
            Assert.True(player.TryGetFrame(Stopwatch.GetTimestamp(), out VideoFrame held), "pause holds the frame");
            (VideoFrame stillHeld, int framesWhilePaused) = RenderFor(player, TimeSpan.FromMilliseconds(300));
            Assert.Equal(pausedAt, player.GetStatus().Position);
            Assert.Equal(held.PtsTicks, stillHeld.PtsTicks);
            Assert.Equal(1, framesWhilePaused);

            Send(commands, new PlayCommand());
            WaitForState(player, PlaybackState.Playing);
            (VideoFrame resumed, _) = RenderFor(player, TimeSpan.FromMilliseconds(300));
            Assert.True(player.GetStatus().Position > pausedAt, "resumes after pause");
            Assert.True(resumed.PtsTicks > held.PtsTicks, "frames advance after pause");

            Send(commands, new StopCommand());
            WaitForState(player, PlaybackState.Ready);
            PlayerStatus stopped = player.GetStatus();
            Assert.Equal(TimeSpan.Zero, stopped.Position);
            Assert.False(player.TryGetFrame(Stopwatch.GetTimestamp(), out _), "black after Stop");
            Assert.Null(stopped.Error);
        }
        finally
        {
            player.ReleaseRenderThread();
            commands.Writer.Complete();
        }
    }

    [Fact]
    public void MissingFile_FailsWithAnOperatorMessage()
    {
        GraphicsDevice device = fixture.RequireHardware();
        var commands = Channel.CreateUnbounded<PlayerCommand>();
        using var player = new VideoPlayer(device.Device, commands.Reader, new VideoPlayerOptions(StereoLayout: null), TestOutputLoggerFactory.Instance);
        player.Start();

        Send(commands, new OpenMediaCommand(Path.Combine(TestVideos.OutputDirectory, "does-not-exist.mp4")));
        WaitForState(player, PlaybackState.Failed);

        Assert.Contains("Datei nicht gefunden", player.GetStatus().Error, StringComparison.Ordinal);
        commands.Writer.Complete();
    }

    /// <summary>Takes frames like a render loop at about 100 Hz. Returns the last frame and how many different frames were shown.</summary>
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

    private static void Send(Channel<PlayerCommand> commands, PlayerCommand command)
    {
        Assert.True(commands.Writer.TryWrite(command));
    }

    private static void WaitForState(VideoPlayer player, PlaybackState expected)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < StateTimeout)
        {
            PlayerStatus status = player.GetStatus();
            if (status.State == expected)
            {
                return;
            }

            if (status.State == PlaybackState.Failed && expected != PlaybackState.Failed)
            {
                Assert.Fail($"Player failed while waiting for {expected}: {status.Error}");
            }

            Thread.Sleep(10);
        }

        Assert.Fail($"Timeout waiting for {expected}, state {player.GetStatus().State}.");
    }
}
