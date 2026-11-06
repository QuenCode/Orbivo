using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Orbivo.Audio.Output;
using Orbivo.Core.Audio;
using Orbivo.Core.Commands;
using Orbivo.Core.Playback;
using Orbivo.Core.Status;
using Orbivo.Media.Tests.Support;
using Orbivo.Xr.Graphics;

namespace Orbivo.Media.Tests;

/// <summary>
/// Picture and sound together (M5): real video and sound decoding, the real sound output thread, a simulated device
/// (nothing reaches a speaker). The test videos flash a white field and beep at every full second; the flash must
/// appear when the beep is heard.
/// </summary>
[Collection(HardwareDecodeFixture.Collection)]
public sealed class AudioSyncTests(HardwareDecodeFixture fixture) : IDisposable
{
    private const int Rate = 48_000;
    private const float OnsetThreshold = 0.15f;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly long FrameTicks = TimeSpan.TicksPerSecond / 30;

    private readonly Channel<PlayerCommand> _commands = Channel.CreateUnbounded<PlayerCommand>();
    private readonly ConcurrentQueue<(long PtsTicks, long Timestamp)> _flashes = new();
    private readonly CancellationTokenSource _stopRender = new();
    private SimulatedEndpointProvider? _provider;
    private AudioOutput? _output;
    private VideoPlayer? _player;
    private Task? _render;

    public void Dispose()
    {
        _stopRender.Cancel();
        _render?.Wait(TimeSpan.FromSeconds(5));
        _player?.ReleaseRenderThread();
        _commands.Writer.TryComplete();
        _player?.Dispose();
        _output?.Dispose();
        _stopRender.Dispose();
    }

    [Fact]
    public void Mono4K_TheFlashIsShownWhenTheBeepIsHeard()
    {
        VideoPlayer player = Start(TestVideos.Mono4K, new SimulatedEndpointOptions(DriftPpm: 200, LatencyFrames: 960, OnsetThreshold: OnsetThreshold));
        Send(new OpenMediaCommand(TestVideos.Mono4K, Play: true));
        WaitFor(status => status.State == PlaybackState.Playing);
        Thread.Sleep(8000);

        PlayerStatus status = player.GetStatus();
        Assert.Equal(PlaybackClockSource.Audio, status.ClockSource);
        Assert.Equal("aac", status.Audio?.Codec);
        Assert.Equal(AudioEndpointState.Active, status.AudioOutput?.State);
        double[] deviations = MatchFlashesToBeeps(_provider!.Latest!);
        Assert.True(deviations.Length >= 6, $"only {deviations.Length} flash/beep pairs");
        Assert.All(deviations, deviation => Assert.InRange(deviation, -AvSyncMeter.LimitMilliseconds, AvSyncMeter.LimitMilliseconds));
        Assert.InRange(deviations.Average(), -5, 5);

        AvSyncStats sync = status.AvSync;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"meter: {sync.TotalFrames} frames, {sync.TotalMinMs:+0.0;-0.0} … {sync.TotalMaxMs:+0.0;-0.0} ms, outside {sync.TotalOutsideLimit}; " +
            $"latency {status.AudioOutput?.LatencyMilliseconds:0.0} ms, underruns {status.AudioOutput?.UnderrunFrames}");
        Assert.True(sync.TotalFrames > 150);
        Assert.Equal(0, sync.TotalOutsideLimit);
        Assert.Equal(0, _provider.Latest!.GlitchFrames);
        Assert.Equal(0, status.AudioOutput?.UnderrunFrames);
    }

    [Fact]
    public void PauseSeekAndLoop_DuringBeeps_NeverClick()
    {
        VideoPlayer player = Start(TestVideos.Mono4K, new SimulatedEndpointOptions(RecordFrames: 40 * Rate));
        Send(new OpenMediaCommand(TestVideos.Mono4K, Play: true));
        WaitFor(status => status.State == PlaybackState.Playing);

        // Each change is sent while a beep is being written, so its fade runs through a beep.
        WaitUntilWrittenInBeep();
        Send(new PauseCommand());
        WaitFor(status => status.State == PlaybackState.Paused);
        Thread.Sleep(300);
        Send(new PlayCommand());
        Thread.Sleep(700);
        WaitUntilWrittenInBeep();
        Send(new SeekCommand(TimeSpan.FromSeconds(5.0333))); // lands in a beep, a third into a 1 kHz period
        WaitFor(status => !status.Seeking && status.State == PlaybackState.Playing && status.Position > TimeSpan.FromSeconds(5.1));
        WaitUntilWrittenInBeep();
        Send(new PauseCommand());
        WaitFor(status => status.State == PlaybackState.Paused);
        Send(new SeekCommand(TimeSpan.FromSeconds(9.0333)));
        WaitFor(status => !status.Seeking && status.State == PlaybackState.Paused);
        Send(new PlayCommand());
        Send(new SetLoopCommand(true));
        Thread.Sleep(500);
        Send(new SeekCommand(TimeSpan.FromSeconds(19.4)));
        WaitFor(status => !status.Seeking && status.Position > TimeSpan.FromSeconds(19.5));
        WaitFor(status => !status.Seeking && status.State == PlaybackState.Playing && status.Position < TimeSpan.FromSeconds(2));
        Thread.Sleep(1200);

        SimulatedAudioEndpoint device = _provider!.Latest!;
        ReadOnlySpan<float> recorded = device.Recorded;
        float loudest = 0;
        float steepest = 0;
        for (int i = 2; i < recorded.Length; i += 2)
        {
            loudest = Math.Max(loudest, Math.Abs(recorded[i]));
            steepest = Math.Max(steepest, Math.Abs(recorded[i] - recorded[i - 2]));
        }

        int[] beepLengths = BeepLengthsMs(recorded);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"loudest {loudest:0.000}, steepest step {steepest:0.0000}, beeps (ms): {string.Join(" ", beepLengths)}, underruns {_output!.GetStatus().UnderrunFrames}");

        // The beep is 0.5·sin(2π·1 kHz): its steepest step is 0.065; a cut would jump by up to 0.5.
        Assert.InRange(loudest, 0.4f, 0.6f);
        Assert.InRange(steepest, 0.03f, 0.09f);
        Assert.True(beepLengths.Count(length => length < 55) >= 3, "the changes fell into beeps (shortened beeps in the output)");
        Assert.Equal(0, device.GlitchFrames);
    }

    [Fact]
    public void NoSoundDevice_TheVideoPlaysOnTheSilentStandIn()
    {
        GraphicsDevice device = fixture.RequireHardware();
        _provider = new SimulatedEndpointProvider();
        _output = new AudioOutput(() => _provider, "gone", 1f, TestOutputLoggerFactory.Instance.CreateLogger("AudioOutput"));
        _player = new VideoPlayer(device.Device, _commands.Reader, new VideoPlayerOptions(StereoLayout: null), TestOutputLoggerFactory.Instance, _output);
        _player.Start();
        StartRenderLoop(_player);
        Send(new OpenMediaCommand(TestVideos.Mono4K, Play: true));
        WaitFor(status => status.State == PlaybackState.Playing);
        TimeSpan before = _player.GetStatus().Position;
        Thread.Sleep(1000);

        PlayerStatus status = _player.GetStatus();
        Assert.Equal(AudioEndpointState.Unavailable, status.AudioOutput?.State);
        Assert.NotNull(status.AudioOutput?.Message);
        Assert.InRange((status.Position - before).TotalSeconds, 0.9, 1.1);
        Assert.Equal(0, status.AvSync.TotalOutsideLimit);
    }

    /// <summary>
    /// 10 minutes of real time (opt-in: ORBIVO_LONG_TESTS=1, video from <c>make_testmedia.py --long</c>) on a device
    /// whose clock runs 200 ppm fast: every flash within ±20 ms of its beep (roadmap M5).
    /// </summary>
    [Fact]
    public void LongRun_TenMinutes_StaysInSync()
    {
        if (Environment.GetEnvironmentVariable("ORBIVO_LONG_TESTS") != "1")
        {
            Assert.Skip("Opt-in: set ORBIVO_LONG_TESTS=1 (runs 10 minutes).");
        }

        string path = TestVideos.Mono4KLong;
        VideoPlayer player = Start(path, new SimulatedEndpointOptions(DriftPpm: 200, LatencyFrames: 960, OnsetThreshold: OnsetThreshold));
        Send(new OpenMediaCommand(path, Play: true));
        WaitFor(status => status.State == PlaybackState.Playing);
        var run = Stopwatch.StartNew();
        while (player.GetStatus().State == PlaybackState.Playing && run.Elapsed < TimeSpan.FromMinutes(11))
        {
            Thread.Sleep(1000);
        }

        PlayerStatus status = player.GetStatus();
        Assert.Equal(PlaybackState.Ended, status.State);
        double[] deviations = MatchFlashesToBeeps(_provider!.Latest!);
        AvSyncStats sync = status.AvSync;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"{deviations.Length} pairs over {run.Elapsed.TotalMinutes:0.0} min: {deviations.Min():+0.0;-0.0} … {deviations.Max():+0.0;-0.0} ms, " +
            $"mean {deviations.Average():+0.00;-0.00} ms; first minute {deviations.Take(60).Average():+0.00;-0.00}, last minute {deviations.TakeLast(60).Average():+0.00;-0.00}; " +
            $"meter {sync.TotalMinMs:+0.0;-0.0} … {sync.TotalMaxMs:+0.0;-0.0} ms, outside {sync.TotalOutsideLimit}; drift {status.AudioOutput?.DriftPpm:0} ppm, " +
            $"underruns {status.AudioOutput?.UnderrunFrames}, glitches {_provider.Latest!.GlitchFrames}");

        Assert.True(deviations.Length >= 590, $"only {deviations.Length} flash/beep pairs");
        Assert.All(deviations, deviation => Assert.InRange(deviation, -AvSyncMeter.LimitMilliseconds, AvSyncMeter.LimitMilliseconds));
        Assert.Equal(0, sync.TotalOutsideLimit);
        Assert.Equal(0, _provider.Latest!.GlitchFrames);
        Assert.Equal(0, status.AudioOutput?.UnderrunFrames);
        Assert.InRange(status.AudioOutput?.DriftPpm ?? 0, 150, 250);
    }

    private VideoPlayer Start(string path, SimulatedEndpointOptions device)
    {
        Assert.True(File.Exists(path));
        GraphicsDevice graphics = fixture.RequireHardware();
        _provider = new SimulatedEndpointProvider(device);
        _output = new AudioOutput(() => _provider, device.DeviceId, 1f, TestOutputLoggerFactory.Instance.CreateLogger("AudioOutput"));
        _player = new VideoPlayer(graphics.Device, _commands.Reader, new VideoPlayerOptions(StereoLayout: null), TestOutputLoggerFactory.Instance, _output);
        _player.Start();
        StartRenderLoop(_player);
        return _player;
    }

    /// <summary>
    /// Stands in for the render thread: asks for a frame about every millisecond and notes when each flash frame
    /// (a frame at a full second) is shown for the first time.
    /// </summary>
    private void StartRenderLoop(VideoPlayer player)
    {
        CancellationToken stop = _stopRender.Token;
        _render = Task.Factory.StartNew(
            () =>
            {
                long lastPts = long.MinValue;
                while (!stop.IsCancellationRequested)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (player.TryGetFrame(now, out VideoFrame frame) && frame.PtsTicks != lastPts)
                    {
                        lastPts = frame.PtsTicks;
                        if (frame.PtsTicks % TimeSpan.TicksPerSecond < FrameTicks / 2)
                        {
                            _flashes.Enqueue((frame.PtsTicks, now));
                        }
                    }

                    Thread.SpinWait(2000);
                    Thread.Yield();
                }
            },
            stop,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>For every flash shown, its time minus the time the nearest beep onset was heard, in milliseconds.</summary>
    private double[] MatchFlashesToBeeps(SimulatedAudioEndpoint device)
    {
        long[] heard = device.Onsets.Select(device.HeardTimestamp).ToArray();
        Assert.NotEmpty(heard);
        var deviations = new List<double>();
        foreach ((long pts, long shown) in _flashes)
        {
            long nearest = heard.MinBy(timestamp => Math.Abs(timestamp - shown));
            double deviation = (shown - nearest) * 1000.0 / Stopwatch.Frequency;
            if (Math.Abs(deviation) < 300)
            {
                deviations.Add(deviation);
            }
            else
            {
                TestContext.Current.TestOutputHelper?.WriteLine($"flash at {TimeSpan.FromTicks(pts)} has no beep within 300 ms");
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine(
            $"flash − beep (ms): {string.Join(" ", deviations.Take(20).Select(d => d.ToString("+0.0;-0.0")))}{(deviations.Count > 20 ? " …" : "")}");
        return [.. deviations];
    }

    /// <summary>Waits until the frame being written now carries a beep (10–25 ms into a full second of media time).</summary>
    private void WaitUntilWrittenInBeep()
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < Timeout)
        {
            AudioOutputStatus status = _output!.GetStatus();
            long written = _output.Clock.GetMediaTicks(Stopwatch.GetTimestamp()) + (long)(status.LatencyMilliseconds * TimeSpan.TicksPerMillisecond);
            long intoSecond = written % TimeSpan.TicksPerSecond;
            if (intoSecond is > 10 * TimeSpan.TicksPerMillisecond and < 25 * TimeSpan.TicksPerMillisecond)
            {
                return;
            }

            Thread.Sleep(1);
        }

        Assert.Fail("no beep came");
    }

    /// <summary>Lengths of the beeps in the output (runs above a small level), in milliseconds.</summary>
    private static int[] BeepLengthsMs(ReadOnlySpan<float> recorded)
    {
        var lengths = new List<int>();
        int start = -1;
        int lastLoud = -1;
        for (int frame = 0; frame < recorded.Length / 2; frame++)
        {
            if (Math.Abs(recorded[2 * frame]) > 0.05f)
            {
                if (start < 0 || frame - lastLoud > Rate / 1000)
                {
                    if (start >= 0)
                    {
                        lengths.Add((lastLoud - start) * 1000 / Rate);
                    }

                    start = frame;
                }

                lastLoud = frame;
            }
        }

        if (start >= 0)
        {
            lengths.Add((lastLoud - start) * 1000 / Rate);
        }

        return [.. lengths];
    }

    private void Send(PlayerCommand command)
    {
        Assert.True(_commands.Writer.TryWrite(command));
    }

    private void WaitFor(Func<PlayerStatus, bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < Timeout)
        {
            PlayerStatus status = _player!.GetStatus();
            if (condition(status))
            {
                return;
            }

            Assert.False(status.State == PlaybackState.Failed, $"player failed: {status.Error}");
            Thread.Sleep(5);
        }

        Assert.Fail($"timed out, status {_player!.GetStatus()}");
    }
}
