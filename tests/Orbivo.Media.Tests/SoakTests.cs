using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Orbivo.Audio.Output;
using Orbivo.Core.Commands;
using Orbivo.Core.Geometry;
using Orbivo.Core.Logging;
using Orbivo.Core.Playback;
using Orbivo.Core.Status;
using Orbivo.Media.Tests.Support;
using Orbivo.Xr.Graphics;

namespace Orbivo.Media.Tests;

/// <summary>
/// The M8 soak test (docs/ROADMAP.md): several videos one after another for 30 minutes, driven through the command
/// channel like the operator does, while the recording runs. Opt-in: <c>ORBIVO_LONG_TESTS=1</c>; the duration can be
/// shortened with <c>ORBIVO_SOAK_MINUTES</c>.
/// </summary>
[Collection(HardwareDecodeFixture.Collection)]
public sealed class SoakTests(HardwareDecodeFixture fixture) : IDisposable
{
    private const double RenderRateHz = 90;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "orbivo-soak-" + Guid.NewGuid().ToString("N"));
    private readonly Channel<PlayerCommand> _commands = Channel.CreateUnbounded<PlayerCommand>();
    private readonly CancellationTokenSource _stopRender = new();

    public void Dispose()
    {
        _stopRender.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void SeveralVideos_ForThirtyMinutes_WithRecording()
    {
        if (Environment.GetEnvironmentVariable("ORBIVO_LONG_TESTS") != "1")
        {
            Assert.Skip("Opt-in: set ORBIVO_LONG_TESTS=1 (runs 30 minutes, ORBIVO_SOAK_MINUTES shortens it).");
        }

        TimeSpan duration = TimeSpan.FromMinutes(
            double.TryParse(Environment.GetEnvironmentVariable("ORBIVO_SOAK_MINUTES"), CultureInfo.InvariantCulture, out double minutes) ? minutes : 30);
        string[] playlist =
        [
            .. new[]
            {
                "equirect360_mono_4096x2048_hevc_8bit_30fps.mp4",
                "equirect360_tb_8192x8192_hevc_8bit_30fps.mp4",
                "eac_mono_6144x4096_hevc_8bit_30fps.mp4",
                "equirect180_sbs_8192x4096_hevc_8bit_30fps.mp4",
            }
            .Select(name => Path.Combine(TestVideos.OutputDirectory, name))
            .Where(File.Exists),
        ];
        Assert.True(playlist.Length >= 2, "Test videos missing: python tools/testmedia/make_testmedia.py");

        GraphicsDevice graphics = fixture.RequireHardware();
        ITestOutputHelper? output = TestContext.Current.TestOutputHelper;
        var recenter = new RecenterControl();
        using var recorder = new SessionRecorder(TestOutputLoggerFactory.Instance.CreateLogger("SessionRecorder"), recenter);
        var provider = new SimulatedEndpointProvider();
        using var audio = new AudioOutput(() => provider, "sim", 1f, TestOutputLoggerFactory.Instance.CreateLogger("AudioOutput"));
        using var player = new VideoPlayer(
            graphics.Device,
            _commands.Reader,
            new VideoPlayerOptions(StereoLayout: null) { Recenter = recenter, Events = recorder.Events },
            TestOutputLoggerFactory.Instance,
            audio);
        player.Start();
        recorder.Start(new RecordingOptions(_folder, "Dauertest", 0, "test"));
        WaitUntil(() => recorder.Status.Active, TimeSpan.FromSeconds(10));
        long offered = 0;
        Thread render = StartRenderLoop(player, recorder.Samples, recenter, () => Interlocked.Increment(ref offered));

        var sent = new Dictionary<string, int>();
        void Send(PlayerCommand command, string label)
        {
            Assert.True(_commands.Writer.TryWrite(command));
            sent[label] = sent.GetValueOrDefault(label) + 1;
        }

        var random = new Random(8);
        var run = Stopwatch.StartNew();
        long baselineMemory = 0;
        int baselineHandles = 0;
        long baselinePrivate = 0;
        int videosPlayed = 0;
        TimeSpan nextReport = TimeSpan.FromMinutes(1);
        var errors = new List<string>();
        try
        {
            while (run.Elapsed < duration)
            {
                string path = playlist[videosPlayed % playlist.Length];
                Send(new OpenMediaCommand(path, Play: true), "Laden");
                WaitUntil(() => player.GetStatus() is { State: PlaybackState.Playing or PlaybackState.Failed } status && status.FilePath == Path.GetFullPath(path), TimeSpan.FromSeconds(20));
                videosPlayed++;

                // Operator actions during the video, then let it run to its end.
                int actions = random.Next(1, 4);
                for (int i = 0; i < actions && player.GetStatus().State == PlaybackState.Playing; i++)
                {
                    Thread.Sleep(random.Next(1000, 3000));
                    switch (random.Next(5))
                    {
                        case 0:
                            Send(new SeekCommand(TimeSpan.FromSeconds(random.Next(0, 15))), "Seek");
                            break;
                        case 1:
                            Send(new PauseCommand(), "Pause");
                            Thread.Sleep(500);
                            Send(new PlayCommand(), "Play");
                            break;
                        case 2:
                            Send(new FadeCommand(ToBlack: true, TimeSpan.FromSeconds(0.5)), "Fade");
                            Thread.Sleep(800);
                            Send(new FadeCommand(ToBlack: false, TimeSpan.FromSeconds(0.5)), "Fade");
                            break;
                        case 3:
                            Send(new SetVolumeCommand(random.Next(30, 100) / 100f), "Lautstärke");
                            break;
                        default:
                            Send(random.Next(2) == 0 ? new RecenterCommand() : new ResetRecenterCommand(), "Recenter");
                            break;
                    }
                }

                WaitUntil(() => player.GetStatus().State is PlaybackState.Ended or PlaybackState.Failed, TimeSpan.FromSeconds(60));
                PlayerStatus ended = player.GetStatus();
                if (ended.Error is not null)
                {
                    errors.Add($"{Path.GetFileName(path)}: {ended.Error}");
                }

                if (baselineMemory == 0 && run.Elapsed > TimeSpan.FromMinutes(Math.Min(5, duration.TotalMinutes / 3)))
                {
                    (baselineMemory, baselineHandles, baselinePrivate) = Measure();
                }

                if (run.Elapsed > nextReport)
                {
                    RecordingStatus status = recorder.Status;
                    (long memory, int handles, long privateBytes) = Measure();
                    output?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{run.Elapsed:hh\\:mm\\:ss} {videosPlayed} videos, {status.HeadRows} head rows, {status.EventRows} events, dropped {status.DroppedRows}, managed {memory / 1048576} MB, private {privateBytes / 1048576} MB, handles {handles}"));
                    nextReport += TimeSpan.FromMinutes(1);
                }
            }

            Send(new CloseMediaCommand(), "Entladen");
            WaitUntil(() => player.GetStatus().State == PlaybackState.NoMedia, TimeSpan.FromSeconds(10));
        }
        finally
        {
            _stopRender.Cancel();
            render.Join(TimeSpan.FromSeconds(5));
            _commands.Writer.TryComplete();
        }

        (long endMemory, int endHandles, long endPrivate) = Measure();
        Thread.Sleep(300);
        RecordingStatus running = recorder.Status;
        recorder.Stop();
        WaitUntil(() => !recorder.Status.Active, TimeSpan.FromSeconds(10));
        RecordingStatus final = recorder.Status;

        string[] headLines = File.ReadAllLines(final.HeadFilePath!, Encoding.UTF8);
        string[] eventLines = File.ReadAllLines(final.EventFilePath!, Encoding.UTF8);
        (int gaps, double maxGapMs) = FindGaps(headLines);
        output?.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"{run.Elapsed:hh\\:mm\\:ss}: {videosPlayed} videos, {headLines.Length - 1} head rows ({Interlocked.Read(ref offered)} offered), {eventLines.Length - 1} events, " +
            $"largest gap {maxGapMs:0.0} ms, gaps > 100 ms: {gaps}; managed {baselineMemory / 1048576} → {endMemory / 1048576} MB, private {baselinePrivate / 1048576} → {endPrivate / 1048576} MB, handles {baselineHandles} → {endHandles}"));
        output?.WriteLine($"Files: {final.HeadFilePath}, {final.EventFilePath}");

        Assert.Empty(errors);
        Assert.Equal(0, final.DroppedRows);
        Assert.Null(final.Error);
        Assert.Equal(Interlocked.Read(ref offered), headLines.Length - 1);
        Assert.True(running.HeadRows <= headLines.Length - 1);
        Assert.Equal(0, gaps);

        // Every command the operator gave is in the event log, plus the start and end of the recording.
        foreach ((string label, int count) in sent)
        {
            int logged = eventLines.Count(line => line.Split(';')[2] == label);
            Assert.True(logged >= count, $"{label}: sent {count}, logged {logged}");
        }

        Assert.DoesNotContain(eventLines, line => line.Split(';')[2] == "Fehler");
        Assert.StartsWith("Start", eventLines[1].Split(';')[5], StringComparison.Ordinal);
        Assert.Contains("Ende (Vom Operator beendet)", eventLines[^1], StringComparison.Ordinal);

        // Nothing grows without bound: compared with the state after the first minutes.
        Assert.InRange(endMemory - baselineMemory, long.MinValue, 64L * 1048576);
        Assert.InRange(endPrivate - baselinePrivate, long.MinValue, 512L * 1048576);
        Assert.InRange(endHandles - baselineHandles, int.MinValue, 200);
    }

    /// <summary>Stands in for the headset render thread: about 90 frames per second, each offered to the head log.</summary>
    private Thread StartRenderLoop(VideoPlayer player, HeadSampleGate samples, RecenterControl recenter, Action onOffer)
    {
        CancellationToken stop = _stopRender.Token;
        var thread = new Thread(
            () =>
            {
                long period = (long)(Stopwatch.Frequency / RenderRateHz);
                long next = Stopwatch.GetTimestamp();
                double angle = 0;
                while (!stop.IsCancellationRequested)
                {
                    long now = Stopwatch.GetTimestamp();
                    if (now < next)
                    {
                        Thread.Sleep(1);
                        continue;
                    }

                    next = Math.Max(next + period, now - period);
                    angle += 0.01;
                    Quaternion head = Quaternion.CreateFromYawPitchRoll((float)Math.Sin(angle), (float)Math.Sin(angle * 0.7) * 0.3f, 0f);
                    Quaternion correction = recenter.Update(head, headTracked: true);
                    bool hasFrame = player.TryGetFrame(now, out VideoFrame frame);
                    float brightness = player.GetOutputBrightness(now);
                    samples.Offer(new HeadSample(
                        now,
                        Tracked: true,
                        head,
                        correction * head,
                        recenter.YawDegrees,
                        hasFrame ? HeadsetContent.Video : HeadsetContent.Black,
                        brightness,
                        player.State,
                        player.FilePath,
                        hasFrame ? frame.PtsTicks : long.MinValue));
                    onOffer();
                }

                player.ReleaseRenderThread();
            })
        {
            Name = "Soak render stand-in",
            IsBackground = true,
        };
        thread.Start();
        return thread;
    }

    /// <summary>Gaps in the "Sekunden" column larger than 100 ms (the render stand-in runs at 90 Hz).</summary>
    private static (int Gaps, double MaxGapMs) FindGaps(string[] headLines)
    {
        int gaps = 0;
        double max = 0;
        double previous = double.NaN;
        foreach (string line in headLines.Skip(1))
        {
            int first = line.IndexOf(';', StringComparison.Ordinal);
            int second = line.IndexOf(';', first + 1);
            double seconds = double.Parse(line.AsSpan(first + 1, second - first - 1), NumberStyles.Float, GermanCsv.Numbers);
            if (!double.IsNaN(previous))
            {
                double gapMs = (seconds - previous) * 1000;
                max = Math.Max(max, gapMs);
                if (gapMs > 100)
                {
                    gaps++;
                }
            }

            previous = seconds;
        }

        return (gaps, max);
    }

    private static (long Managed, int Handles, long PrivateBytes) Measure()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using Process process = Process.GetCurrentProcess();
        return (GC.GetTotalMemory(forceFullCollection: true), process.HandleCount, process.PrivateMemorySize64);
    }

    private static void WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(stopwatch.Elapsed < timeout, "Timed out.");
            Thread.Sleep(20);
        }
    }
}
