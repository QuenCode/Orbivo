using System.Diagnostics;
using Orbivo.Core.Formats;
using Orbivo.Media.Tests.Support;
using Orbivo.Xr.Graphics;

namespace Orbivo.Media.Tests;

/// <summary>Hardware decoding (D3D11VA) of the generated test videos into the frame ring.</summary>
[Collection(HardwareDecodeFixture.Collection)]
public sealed class VideoDecoderTests(HardwareDecodeFixture fixture)
{
    [Fact]
    public void Mono4K_DecodesEveryFrameInOrderOnTheGpu()
    {
        GraphicsDevice device = fixture.RequireHardware();
        string path = TestVideos.Mono4K;

        using DecoderRun run = DecoderRun.Open(device, path);
        List<long> pts = run.ConsumeAll(TimeSpan.FromSeconds(60));

        Assert.True(run.Decoder.Info.HardwareDecoding);
        Assert.Equal("hevc", run.Decoder.Info.Codec);
        Assert.Equal((4096, 2048), (run.Decoder.Info.Width, run.Decoder.Info.Height));
        Assert.Equal((4096, 2048), (run.Decoder.Queue.Width, run.Decoder.Queue.Height));
        Assert.Null(run.Decoder.ErrorMessage);

        Assert.Equal(TestVideos.FrameCount, pts.Count);
        Assert.Equal(0, pts[0]);
        for (int i = 1; i < pts.Count; i++)
        {
            Assert.True(pts[i] > pts[i - 1], $"pts not increasing at frame {i}: {pts[i - 1]} -> {pts[i]}");
        }

        // 30 fps: frame 599 at 19.967 s.
        Assert.InRange(pts[^1], TimeSpan.FromSeconds(599 / 30.0).Ticks - TimeSpan.TicksPerMillisecond, TimeSpan.FromSeconds(599 / 30.0).Ticks + TimeSpan.TicksPerMillisecond);
        Assert.Equal(0, run.Decoder.Queue.Pacer.DroppedFrames);
    }

    [Fact]
    public void TopBottom8K_DecodesFasterThanRealTime()
    {
        GraphicsDevice device = fixture.RequireHardware();
        string path = TestVideos.TopBottom8K;

        using DecoderRun run = DecoderRun.Open(device, path);
        var stopwatch = Stopwatch.StartNew();
        List<long> pts = run.ConsumeAll(TimeSpan.FromSeconds(120));
        stopwatch.Stop();

        double framesPerSecond = pts.Count / stopwatch.Elapsed.TotalSeconds;
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"8K TB HEVC on {device.AdapterName}: {pts.Count} frames in {stopwatch.Elapsed.TotalSeconds:0.00} s = {framesPerSecond:0.0} fps");

        Assert.Equal(TestVideos.FrameCount, pts.Count);
        Assert.True(framesPerSecond > 30, $"only {framesPerSecond:0.0} fps, the video needs 30");
    }

    [Fact]
    public void Format_IsHandedToTheQueue()
    {
        GraphicsDevice device = fixture.RequireHardware();
        VideoFormat format = VideoFormat.Equirect360(StereoLayout.SideBySide) with { SwapEyes = true };

        using DecoderRun run = DecoderRun.Open(device, TestVideos.Mono4K, format);

        Assert.Same(format, run.Decoder.Queue.PictureFormat);
    }

    [Fact]
    public void MissingFile_GivesAnOperatorMessage()
    {
        GraphicsDevice device = fixture.RequireHardware();

        MediaOpenException exception = Assert.Throws<MediaOpenException>(
            () => DecoderRun.Open(device, Path.Combine(TestVideos.OutputDirectory, "does-not-exist.mp4")));

        Assert.Equal("Datei nicht gefunden", exception.OperatorMessage);
    }

    [Fact]
    public void StillImage_HasNoHardwareDecoder()
    {
        GraphicsDevice device = fixture.RequireHardware();

        MediaOpenException exception = Assert.Throws<MediaOpenException>(() => DecoderRun.Open(device, TestVideos.MonoImage));

        Assert.Contains("keinen Hardware-Decoder", exception.OperatorMessage, StringComparison.Ordinal);
    }

    /// <summary>A started decoder plus a consumer that takes every frame exactly once, as fast as it comes.</summary>
    private sealed class DecoderRun : IDisposable
    {
        private DecoderRun(VideoDecoder decoder)
        {
            Decoder = decoder;
        }

        public VideoDecoder Decoder { get; }

        public static DecoderRun Open(GraphicsDevice device, string path, VideoFormat? format = null)
        {
            VideoDecoder decoder = VideoDecoder.Open(path, device.Device, device.ImmediateContext, format ?? VideoFormat.Default, slotCount: 6, TestOutputLoggerFactory.Instance.CreateLogger("VideoDecoder"));
            decoder.Start();
            return new DecoderRun(decoder);
        }

        public List<long> ConsumeAll(TimeSpan timeout)
        {
            var pts = new List<long>(TestVideos.FrameCount);
            var stopwatch = Stopwatch.StartNew();
            long target = long.MinValue;
            while (stopwatch.Elapsed < timeout)
            {
                Assert.Null(Decoder.ErrorMessage);
                if (!Decoder.HasFirstFrame)
                {
                    Thread.Yield();
                    continue;
                }

                if (target == long.MinValue)
                {
                    target = Decoder.FirstPtsTicks;
                }

                // Target 1.5 frames after the current one: takes exactly the next frame, never skips one.
                if (Decoder.Queue.Pacer.TrySelect(target, out _, out long current) && (pts.Count == 0 || current != pts[^1]))
                {
                    pts.Add(current);
                    target = current + (Decoder.FrameDurationTicks * 3 / 2);
                    continue;
                }

                if (Decoder.EndOfStream && Decoder.Queue.Pacer.ReadyCount == 0)
                {
                    return pts;
                }

                Thread.Yield();
            }

            Assert.Fail($"Timeout after {pts.Count} frames.");
            return pts;
        }

        public void Dispose()
        {
            Decoder.Dispose();
            Decoder.Queue.Dispose();
        }
    }
}
