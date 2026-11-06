using System.Diagnostics;
using System.Threading.Channels;
using Orbivo.Core.Commands;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Core.Playback;
using Orbivo.Core.Status;
using Orbivo.Media.Tests.Support;
using Orbivo.Xr.Graphics;

namespace Orbivo.Media.Tests;

/// <summary>
/// Format detection of the generated test videos (F12, F13): the sv3d/st3d metadata as FFmpeg reads it, the sidecar
/// next to the video, and the format the player resolves from both. Plus a format change through the command channel.
/// </summary>
[Collection(HardwareDecodeFixture.Collection)]
public sealed class VideoFormatDetectionTests(HardwareDecodeFixture fixture)
{
    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("equirect360_mono_4096x2048_hevc_8bit_30fps.mp4", ProjectionType.Equirect360, StereoLayout.Mono, null, "sv3d Equirectangular, st3d TwoD")]
    [InlineData("equirect360_tb_4096x4096_hevc_8bit_30fps.mp4", ProjectionType.Equirect360, StereoLayout.TopBottom, null, "sv3d Equirectangular, st3d TopBottom")]
    [InlineData("equirect360_tb_4096x4096_hevc_8bit_30fps_nometa.mp4", ProjectionType.Equirect360, StereoLayout.TopBottom, null, "none")]
    [InlineData("equirect360_tb_4096x4096_hevc_8bit_30fps_sv3d_only.mp4", ProjectionType.Equirect360, StereoLayout.TopBottom, null, "sv3d Equirectangular")]
    [InlineData("equirect360_sbs_4096x2048_hevc_8bit_30fps.mp4", ProjectionType.Equirect360, StereoLayout.SideBySide, null, "st3d SideBySide")]
    [InlineData("equirect180_sbs_4096x2048_hevc_8bit_30fps.mp4", ProjectionType.Equirect180, StereoLayout.SideBySide, null, "sv3d EquirectangularTile (bounds l 0.25 t 0 r 0.25 b 0)")]
    [InlineData("cubemap3x2_mono_3072x2048_hevc_8bit_30fps.mp4", ProjectionType.Cubemap, StereoLayout.Mono, "3x2", "sv3d Cubemap, st3d TwoD")]
    [InlineData("cubemap6x1_mono_6144x1024_hevc_8bit_30fps.mp4", ProjectionType.Cubemap, StereoLayout.Mono, "6x1", "sv3d Cubemap")]
    [InlineData("cubemap1x6_mono_1024x6144_hevc_8bit_30fps.mp4", ProjectionType.Cubemap, StereoLayout.Mono, "1x6", "none")]
    [InlineData("cubemap3x2_tb_3072x4096_hevc_8bit_30fps.mp4", ProjectionType.Cubemap, StereoLayout.TopBottom, "3x2", "st3d TopBottom")]
    [InlineData("eac_mono_3072x2048_hevc_8bit_30fps.mp4", ProjectionType.Eac, StereoLayout.Mono, "eac", "st3d TwoD")]
    [InlineData("eac_tb_3072x4096_hevc_8bit_30fps.mp4", ProjectionType.Eac, StereoLayout.TopBottom, "eac", "st3d TopBottom")]
    [InlineData("fisheye180_mono_4096x4096_hevc_8bit_30fps.mp4", ProjectionType.Fisheye, StereoLayout.Mono, null, "st3d TwoD")]
    [InlineData("fisheye200_sbs_4096x2048_hevc_8bit_30fps.mp4", ProjectionType.Fisheye, StereoLayout.SideBySide, null, "st3d SideBySide")]
    public void TestVideo_HasTheExpectedFormat(string fileName, ProjectionType projection, StereoLayout layout, string? cubemap, string metadata)
    {
        fixture.RequireHardware();
        string path = Require(fileName);

        using MediaDemuxer demuxer = MediaDemuxer.Open(path, withAudio: false, TestOutputLoggerFactory.Instance.CreateLogger("Demuxer"));
        EmbeddedFormatMetadata embedded = demuxer.ReadFormatMetadata();
        SidecarFile sidecar = SidecarFile.Load(path);
        (int width, int height) = demuxer.VideoSize;
        ResolvedVideoFormat resolved = VideoFormatResolver.Resolve(embedded, width, height, sidecar.Overrides);
        TestContext.Current.TestOutputHelper?.WriteLine($"{fileName}: {embedded.Describe()} -> {resolved.Format}; {string.Join(" | ", resolved.Warnings)}");

        Assert.Contains(metadata, embedded.Describe(), StringComparison.Ordinal);
        Assert.Empty(sidecar.Warnings);
        Assert.Equal(projection, resolved.Format.Projection);
        Assert.Equal(layout, resolved.Format.Layout);
        Assert.False(resolved.Format.SwapEyes);
        Assert.True(resolved.Format.Offset.IsZero);
        if (cubemap is not null)
        {
            Assert.Equal(cubemap, resolved.Format.Cubemap.PresetName);
        }

        if (fileName.Contains("sv3d_only", StringComparison.Ordinal))
        {
            Assert.Contains(resolved.Warnings, w => w.Contains("st3d", StringComparison.Ordinal));
        }
        else
        {
            Assert.Empty(resolved.Warnings);
        }
    }

    [Fact]
    public void Fisheye200_ReadsTheCircleFromTheSidecar()
    {
        string path = Require("fisheye200_sbs_4096x2048_hevc_8bit_30fps.mp4");

        Assert.Equal(new FisheyeParameters(200, 0.48, 0.51, 0.46), SidecarFile.Load(path).Overrides.Fisheye);
    }

    [Fact]
    public void Player_AppliesAndStoresAFormatChange()
    {
        GraphicsDevice device = fixture.RequireHardware();
        string directory = Path.Combine(Path.GetTempPath(), "orbivo-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "cube.mp4");
        File.Copy(Require("cubemap3x2_mono_3072x2048_hevc_8bit_30fps.mp4"), path);
        var commands = Channel.CreateUnbounded<PlayerCommand>();
        var player = new VideoPlayer(device.Device, commands.Reader, new VideoPlayerOptions(StereoLayout: null), TestOutputLoggerFactory.Instance);
        player.Start();

        try
        {
            Send(commands, new OpenMediaCommand(path));
            WaitFor(player, s => s.State == PlaybackState.Ready && s.Format is not null);
            VideoFormatStatus opened = player.GetStatus().Format!;
            Assert.Equal(ProjectionType.Cubemap, opened.Format.Projection);
            Assert.Equal(FormatSource.Metadata, opened.Sources.Projection);
            Assert.False(opened.SidecarExists);

            var overrides = new FormatOverrides(Cubemap: CubemapLayout.Strip6x1, SwapEyes: true, Offset: new OrientationOffset(45, 0, 0));
            Send(commands, new SetVideoFormatCommand(overrides));
            WaitFor(player, s => s.Format?.Overrides == overrides);
            VideoFormatStatus changed = player.GetStatus().Format!;
            Assert.True(changed.SidecarExists);
            Assert.Null(changed.SidecarError);
            Assert.Equal(CubemapLayout.Strip6x1, changed.Format.Cubemap);
            Assert.Equal(FormatSource.Sidecar, changed.Sources.Cubemap);
            Assert.Equal(CubemapLayout.Grid3x2, changed.Automatic.Cubemap);
            Assert.Equal(overrides, SidecarFile.Load(path).Overrides);

            // The render thread sees the new format with the next frame, without reopening.
            Send(commands, new PlayCommand());
            WaitFor(player, s => s.State == PlaybackState.Playing);
            VideoFrame frame = WaitForFrame(player);
            Assert.Equal(changed.Format, frame.Format);

            // Stop reopens the decoder; the choices stay.
            Send(commands, new StopCommand());
            WaitFor(player, s => s.State == PlaybackState.Ready && s.Position == TimeSpan.Zero);
            Assert.Equal(overrides, player.GetStatus().Format!.Overrides);

            // Opening again reads the sidecar; "Alles automatisch" deletes it.
            Send(commands, new OpenMediaCommand(path));
            WaitFor(player, s => s.State == PlaybackState.Ready && s.Format is { SidecarExists: true });
            Assert.Equal(overrides, player.GetStatus().Format!.Overrides);
            Send(commands, new SetVideoFormatCommand(FormatOverrides.None));
            WaitFor(player, s => s.Format is { SidecarExists: false });
            Assert.False(File.Exists(SidecarFile.PathFor(path)));
            Assert.Equal(opened.Format, player.GetStatus().Format!.Format);
        }
        finally
        {
            player.ReleaseRenderThread();
            commands.Writer.Complete();
            player.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Require(string fileName)
    {
        string path = Path.Combine(TestVideos.OutputDirectory, fileName);
        if (!File.Exists(path))
        {
            Assert.Skip($"{fileName} missing, run python tools/testmedia/make_testmedia.py.");
        }

        return path;
    }

    private static VideoFrame WaitForFrame(VideoPlayer player)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < StateTimeout)
        {
            if (player.TryGetFrame(Stopwatch.GetTimestamp(), out VideoFrame frame))
            {
                return frame;
            }

            Thread.Sleep(10);
        }

        Assert.Fail("No frame shown.");
        return default;
    }

    private static void Send(Channel<PlayerCommand> commands, PlayerCommand command)
    {
        Assert.True(commands.Writer.TryWrite(command));
    }

    private static void WaitFor(VideoPlayer player, Func<PlayerStatus, bool> condition)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < StateTimeout)
        {
            PlayerStatus status = player.GetStatus();
            if (condition(status))
            {
                return;
            }

            if (status.State == PlaybackState.Failed)
            {
                Assert.Fail($"Player failed: {status.Error}");
            }

            Thread.Sleep(10);
        }

        Assert.Fail($"Timeout, state {player.GetStatus().State}.");
    }
}
