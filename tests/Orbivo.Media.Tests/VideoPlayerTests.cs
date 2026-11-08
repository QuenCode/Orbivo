using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Orbivo.Core.Commands;
using Orbivo.Core.Logging;
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
    public void Close_ReleasesTheFile_AndEmptiesTheStatus()
    {
        GraphicsDevice device = fixture.RequireHardware();
        string copy = Path.Combine(Path.GetTempPath(), $"orbivo-close-{Guid.NewGuid():N}.mp4");
        File.Copy(TestVideos.Mono4K, copy);
        var commands = Channel.CreateUnbounded<PlayerCommand>();
        using var player = new VideoPlayer(device.Device, commands.Reader, new VideoPlayerOptions(StereoLayout: null), TestOutputLoggerFactory.Instance);
        player.Start();

        try
        {
            Send(commands, new OpenMediaCommand(copy));
            WaitForState(player, PlaybackState.Ready);
            Send(commands, new PlayCommand());
            WaitForState(player, PlaybackState.Playing);
            RenderFor(player, TimeSpan.FromSeconds(1.5));
            Assert.NotEqual(0, player.GetStatus().AvSync.WindowFrames);

            Send(commands, new CloseMediaCommand());
            WaitForState(player, PlaybackState.NoMedia);
            PlayerStatus closed = player.GetStatus();
            Assert.Null(closed.FilePath);
            Assert.Null(closed.Video);
            Assert.Null(closed.Format);
            Assert.Null(closed.Audio);
            Assert.Null(closed.Error);
            Assert.Equal(TimeSpan.Zero, closed.Position);
            Assert.Equal(TimeSpan.Zero, closed.Duration);
            Assert.Equal(0, closed.AvSync.WindowFrames);
            Assert.False(player.TryGetFrame(Stopwatch.GetTimestamp(), out _), "black after closing");

            // The file is free: Explorer could delete it now.
            File.Delete(copy);
            Assert.False(File.Exists(copy));

            // Play without a video does nothing.
            Send(commands, new PlayCommand());
            Thread.Sleep(100);
            Assert.Equal(PlaybackState.NoMedia, player.GetStatus().State);
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

    [Fact]
    public void HardwareFailureBeforeTheFirstFrame_FallsBackToSoftware()
    {
        GraphicsDevice device = fixture.RequireHardware();
        string path = TestVideos.H264AboveGpuLimit;
        var commands = Channel.CreateUnbounded<PlayerCommand>();

        // Without the check the hardware decoder gets the video and rejects it, as if the check had missed it.
        var options = new VideoPlayerOptions(StereoLayout: null) { CheckHardware = false };
        using var player = new VideoPlayer(device.Device, commands.Reader, options, TestOutputLoggerFactory.Instance);
        player.Start();

        try
        {
            Send(commands, new OpenMediaCommand(path));
            WaitForState(player, PlaybackState.Ready);
            PlayerStatus ready = player.GetStatus();
            Assert.False(ready.Video?.HardwareDecoding);
            Assert.Equal("Hardware-Decoder hat das Video abgelehnt", ready.Video?.SoftwareReason);
            Assert.Null(ready.Error);

            Send(commands, new PlayCommand());
            WaitForState(player, PlaybackState.Playing);
            (VideoFrame playing, int distinctFrames) = RenderFor(player, TimeSpan.FromSeconds(1));
            Assert.InRange(playing.PtsTicks, TimeSpan.FromSeconds(0.8).Ticks, TimeSpan.FromSeconds(1.2).Ticks);
            Assert.InRange(distinctFrames, 25, 31);
            Assert.Equal((4320, 2160), (playing.Width, playing.Height));

            Send(commands, new SeekCommand(TimeSpan.FromSeconds(3)));
            var stopwatch = Stopwatch.StartNew();
            while (player.GetStatus() is { Seeking: true } or { Position.TotalSeconds: < 2.9 })
            {
                // Like the render thread: its calls free the frames of the old position.
                player.TryGetFrame(Stopwatch.GetTimestamp(), out _);
                Assert.True(stopwatch.Elapsed < StateTimeout, "seek not done");
                Thread.Sleep(10);
            }

            (VideoFrame afterSeek, _) = RenderFor(player, TimeSpan.FromMilliseconds(300));
            Assert.InRange(afterSeek.PtsTicks, TimeSpan.FromSeconds(2.95).Ticks, TimeSpan.FromSeconds(3.6).Ticks);

            // Stop opens the file again: in software at once, without trying the hardware again.
            Send(commands, new StopCommand());
            WaitForState(player, PlaybackState.Ready);
            Assert.False(player.GetStatus().Video?.HardwareDecoding);
            Assert.Null(player.GetStatus().Error);
        }
        finally
        {
            player.ReleaseRenderThread();
            commands.Writer.Complete();
        }
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

    [Fact]
    public void CommandsStatesAndErrors_GoToTheEventLog()
    {
        GraphicsDevice device = fixture.RequireHardware();
        string path = Path.GetFullPath(TestVideos.Mono4K);
        var events = new CollectingEventLog();
        var commands = Channel.CreateUnbounded<PlayerCommand>();
        using var player = new VideoPlayer(device.Device, commands.Reader, new VideoPlayerOptions(StereoLayout: null) { Events = events }, TestOutputLoggerFactory.Instance);
        player.Start();

        try
        {
            Assert.Equal(PlaybackState.NoMedia, player.State);
            Send(commands, new OpenMediaCommand(path));
            WaitForState(player, PlaybackState.Ready);
            Assert.Equal(PlaybackState.Ready, player.State);
            Assert.Equal(path, player.FilePath);

            Send(commands, new PlayCommand());
            WaitForState(player, PlaybackState.Playing);
            Send(commands, new SeekCommand(TimeSpan.FromSeconds(5)));
            Send(commands, new SetLoopCommand(true));
            Send(commands, new FadeCommand(ToBlack: true, TimeSpan.FromSeconds(1.5)));
            Send(commands, new SetVolumeCommand(0.5f));
            Send(commands, new RecenterCommand());
            Send(commands, new PauseCommand());
            WaitForState(player, PlaybackState.Paused);
            Send(commands, new CloseMediaCommand());
            WaitForState(player, PlaybackState.NoMedia);
            Assert.Null(player.FilePath);

            Send(commands, new OpenMediaCommand(Path.Combine(TestVideos.OutputDirectory, "does-not-exist.mp4")));
            WaitForState(player, PlaybackState.Failed);
        }
        finally
        {
            player.ReleaseRenderThread();
            commands.Writer.Complete();
        }

        SessionEvent[] log = events.Snapshot();
        string Line(SessionEvent e) => $"{SessionEventQueue.Label(e.Kind)}|{e.Details}";
        string[] lines = log.Select(Line).ToArray();
        int Index(string line)
        {
            int index = Array.FindIndex(lines, l => l.StartsWith(line, StringComparison.Ordinal));
            Assert.True(index >= 0, $"missing '{line}' in:\n{string.Join("\n", lines)}");
            return index;
        }

        // In order, as the operator gave the commands and the player reacted.
        int[] order =
        [
            Index("Laden|" + path),
            Index("Zustand|Bereit"),
            Index("Play|"),
            Index("Zustand|Läuft"),
            Index("Seek|von 0:00:0"),
            Index("Loop|an"),
            Index("Fade|nach Schwarz, 1,5 s"),
            Index("Lautstärke|50 %"),
            Index("Recenter|Angefordert"),
            Index("Pause|"),
            Index("Zustand|Pause"),
            Index("Entladen|"),
            Index("Zustand|Kein Video"),
            Index("Fehler|"),
        ];
        Assert.Equal(order.Order(), order);
        Assert.Contains("nach 0:00:05,000", lines[order[4]], StringComparison.Ordinal);

        SessionEvent seek = log[order[4]];
        Assert.Equal(path, seek.VideoFile);
        Assert.NotNull(seek.VideoTime);
        Assert.Contains("Datei nicht gefunden", log[order[^1]].Details, StringComparison.Ordinal);
    }

    private sealed class CollectingEventLog : ISessionEventLog
    {
        private readonly ConcurrentQueue<SessionEvent> _events = new();

        public void Record(SessionEventKind kind, string details, string? videoFile = null, TimeSpan? videoTime = null)
        {
            _events.Enqueue(new SessionEvent(Stopwatch.GetTimestamp(), kind, details, videoFile, videoTime));
        }

        public SessionEvent[] Snapshot() => [.. _events];
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
