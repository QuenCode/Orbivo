using System.Diagnostics;
using System.Threading.Channels;
using Orbivo.Audio.Output;
using Orbivo.Core.Audio;
using Orbivo.Core.Commands;
using Orbivo.Core.Formats;
using Orbivo.Core.Playback;
using Orbivo.Core.Status;
using Orbivo.Media.Tests.Support;
using Orbivo.Xr.Graphics;

namespace Orbivo.Media.Tests;

/// <summary>
/// An Ambisonics video through the whole player (M10): real decoding, the real sound output thread with Steam Audio
/// (if phonon.dll is there), a simulated device. The operator's choice of the audio mode reopens the sound decoder.
/// The video is copied to a temporary folder, because the choice is stored in a sidecar next to it.
/// </summary>
[Collection(HardwareDecodeFixture.Collection)]
public sealed class AmbisonicsPlaybackTests(HardwareDecodeFixture fixture) : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private readonly Channel<PlayerCommand> _commands = Channel.CreateUnbounded<PlayerCommand>();
    private readonly CancellationTokenSource _stopRender = new();
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "orbivo-ambisonics-" + Guid.NewGuid().ToString("N"));
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
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void Sa3dVideo_PlaysTheField_AndTheChoiceOfChannelsMixesItDown()
    {
        string path = CopyToTemp(TestVideos.AmbisonicsSa3d);
        VideoPlayer player = Start();
        Send(new OpenMediaCommand(path, Play: true));
        WaitFor(status => status.State == PlaybackState.Playing && status.Position > TimeSpan.FromSeconds(1));

        PlayerStatus status = player.GetStatus();
        Assert.Equal(PlaybackClockSource.Audio, status.ClockSource);
        Assert.Equal(ChannelHandling.Ambisonics, status.Audio?.Conversion.Channels);
        Assert.Equal(AudioModeSource.Metadata, status.Audio?.Conversion.Mode?.Source);
        WaitFor(status => status.AudioOutput?.Spatial?.FieldBinaural == true || status.AudioOutput?.Spatial?.AmbisonicsProblem is not null);
        float peak = 0;
        foreach (float sample in _provider!.Latest!.Recorded)
        {
            peak = Math.Max(peak, Math.Abs(sample));
        }

        Assert.True(peak > 0.01f, $"the field is heard (peak {peak})");

        // The operator chooses "Kanäle": the decoder starts again at the same place and mixes the field down.
        TimeSpan before = player.GetStatus().Position;
        FormatOverrides overrides = status.Format!.Overrides with { AudioMode = AudioModeChoice.Channels };
        Send(new SetVideoFormatCommand(overrides));
        WaitFor(status => status.Audio?.Conversion.Channels == ChannelHandling.Downmix && status.State == PlaybackState.Playing);
        status = player.GetStatus();
        Assert.Equal(AudioModeSource.Sidecar, status.Audio?.Conversion.Mode?.Source);
        Assert.InRange((status.Position - before).TotalSeconds, -0.2, 2.0);
        Assert.True(File.Exists(path + ".orbivo.json"), "the choice is stored in the sidecar");

        // Back to automatic: Ambisonics again, the sidecar goes away.
        Send(new SetVideoFormatCommand(overrides with { AudioMode = null }));
        WaitFor(status => status.Audio?.Conversion.Channels == ChannelHandling.Ambisonics && status.State == PlaybackState.Playing);
        Assert.False(File.Exists(path + ".orbivo.json"));
        Assert.Null(player.GetStatus().Error);
    }

    [Fact]
    public void ChoosingTheAudioModeAfterTheEnd_KeepsTheEnd_AndPlayStartsAgainCleanly()
    {
        string path = CopyToTemp(TestVideos.AmbisonicsSa3d);
        VideoPlayer player = Start();
        Send(new OpenMediaCommand(path, Play: false));
        WaitFor(status => status.State == PlaybackState.Ready);
        TimeSpan duration = player.GetStatus().Duration;
        Send(new SeekCommand(duration - TimeSpan.FromSeconds(1)));
        Send(new PlayCommand());
        WaitFor(status => status.State == PlaybackState.Ended);

        FormatOverrides overrides = player.GetStatus().Format!.Overrides with { AudioMode = AudioModeChoice.Channels };
        Send(new SetVideoFormatCommand(overrides));
        WaitFor(status => status.Audio?.Conversion.Channels == ChannelHandling.Downmix);
        Thread.Sleep(300);
        Assert.Equal(PlaybackState.Ended, player.GetStatus().State);

        long underrunsBefore = player.GetStatus().AudioOutput?.UnderrunFrames ?? 0;
        Send(new PlayCommand());
        WaitFor(status => status.State == PlaybackState.Playing && status.Position > TimeSpan.FromSeconds(1));
        PlayerStatus playing = player.GetStatus();
        Assert.Equal(ChannelHandling.Downmix, playing.Audio?.Conversion.Channels);
        Assert.Equal(PlaybackClockSource.Audio, playing.ClockSource);
        Assert.Equal(underrunsBefore, playing.AudioOutput?.UnderrunFrames ?? 0);
        Assert.Null(playing.Error);
    }

    private string CopyToTemp(string source)
    {
        Directory.CreateDirectory(_folder);
        string target = Path.Combine(_folder, Path.GetFileName(source));
        File.Copy(source, target);
        return target;
    }

    private VideoPlayer Start()
    {
        GraphicsDevice graphics = fixture.RequireHardware();
        var device = new SimulatedEndpointOptions(LatencyFrames: 960, RecordFrames: 10 * 48_000);
        _provider = new SimulatedEndpointProvider(device);
        var logger = TestOutputLoggerFactory.Instance.CreateLogger("AudioOutput");
        _output = new AudioOutput(
            () => _provider,
            device.DeviceId,
            1f,
            logger,
            rate => Audio.Spatial.SteamStereoSpatializer.Create(rate),
            listener: null,
            ambisonicsFactory: rate => Audio.Spatial.SteamAmbisonicsSpatializer.Create(rate));
        _player = new VideoPlayer(graphics.Device, _commands.Reader, new VideoPlayerOptions(StereoLayout: null), TestOutputLoggerFactory.Instance, _output);
        _player.Start();
        StartRenderLoop(_player);
        return _player;
    }

    private void StartRenderLoop(VideoPlayer player)
    {
        CancellationToken stop = _stopRender.Token;
        _render = Task.Factory.StartNew(
            () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    player.TryGetFrame(Stopwatch.GetTimestamp(), out _);
                    Thread.Sleep(1);
                }
            },
            stop,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
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
