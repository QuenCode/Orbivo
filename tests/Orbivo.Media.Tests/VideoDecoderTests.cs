using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging.Abstractions;
using Orbivo.Core.Formats;
using Orbivo.Core.Localization;
using Orbivo.Media.Tests.Support;
using Orbivo.Xr.Graphics;
using Vortice.Direct3D11;
using DxgiFormat = Vortice.DXGI.Format;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace Orbivo.Media.Tests;

/// <summary>
/// Decoding of the generated test videos into the frame ring: hardware (D3D11VA), the check of the GPU before opening,
/// and the software fallback with its upload as NV12/P010.
/// </summary>
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

        Assert.Equal("Datei nicht gefunden", exception.OperatorMessage.ToString());
        Assert.Equal("File not found", exception.OperatorMessage.ForLog());
    }

    [Fact]
    public void StillImage_HasNoHardwareDecoder()
    {
        GraphicsDevice device = fixture.RequireHardware();

        MediaOpenException exception = Assert.Throws<MediaOpenException>(() => DecoderRun.Open(device, TestVideos.MonoImage));

        Assert.Contains("keinen Hardware-Decoder", exception.OperatorMessage.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AVCodecID.AV_CODEC_ID_HEVC, 1, 8, 8192, 8192, true)]
    [InlineData(AVCodecID.AV_CODEC_ID_HEVC, 2, 10, 4096, 2048, true)]
    [InlineData(AVCodecID.AV_CODEC_ID_H264, 100, 8, 4096, 2160, true)]
    [InlineData(AVCodecID.AV_CODEC_ID_H264, 100, 8, 4320, 2160, false)]
    [InlineData(AVCodecID.AV_CODEC_ID_H264, 110, 10, 3840, 2160, false)]
    [InlineData(AVCodecID.AV_CODEC_ID_VC1, 0, 8, 1920, 1080, null)]
    public void HardwareCheck_KnowsTheLimitsOfTheGpu(AVCodecID codec, int profile, int bitDepth, int width, int height, bool? expected)
    {
        GraphicsDevice device = fixture.RequireHardware();

        bool? result = HardwareDecodeSupport.CanDecode(
            device.Device, codec, profile, bitDepth, chroma420: true, width, height, TestOutputLoggerFactory.Instance.CreateLogger("Check"), out OperatorText? reason);
        TestContext.Current.TestOutputHelper?.WriteLine($"{codec} {bitDepth} bit {width}x{height} on {device.AdapterName}: {result} {reason}");

        Assert.Equal(expected, result);
        Assert.Equal(result == false, reason is not null);
    }

    [Fact]
    public void H264AboveTheGpuLimit_DecodesEveryFrameInSoftware()
    {
        GraphicsDevice device = fixture.RequireHardware();

        using DecoderRun run = DecoderRun.Open(device, TestVideos.H264AboveGpuLimit);
        var stopwatch = Stopwatch.StartNew();
        List<long> pts = run.ConsumeAll(TimeSpan.FromSeconds(60));
        stopwatch.Stop();
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"H.264 4320x2160 in software: {pts.Count} frames in {stopwatch.Elapsed.TotalSeconds:0.00} s = {pts.Count / stopwatch.Elapsed.TotalSeconds:0.0} fps ({Environment.ProcessorCount} logical processors)");

        Assert.False(run.Decoder.Info.HardwareDecoding);
        Assert.Contains("4320 × 2160", run.Decoder.Info.SoftwareReason?.ToString(), StringComparison.Ordinal);
        Assert.Equal((4320, 2160), (run.Decoder.Queue.Width, run.Decoder.Queue.Height));
        Assert.Equal(TestVideos.DecoderVideoFrameCount, pts.Count);
        Assert.Equal(0, pts[0]);
        for (int i = 1; i < pts.Count; i++)
        {
            Assert.True(pts[i] > pts[i - 1], $"pts not increasing at frame {i}: {pts[i - 1]} -> {pts[i]}");
        }

        Assert.Equal(0, run.Decoder.Queue.Pacer.DroppedFrames);
    }

    [Theory]
    [InlineData(DecoderPreference.Auto)]
    [InlineData(DecoderPreference.Software)]
    public void SoftwareDecoding_Seeks(DecoderPreference preference)
    {
        GraphicsDevice device = fixture.RequireHardware();
        string path = preference == DecoderPreference.Auto ? TestVideos.H264AboveGpuLimit : TestVideos.Mono4K;

        using DecoderRun run = DecoderRun.Open(device, path, preference: preference);
        Assert.False(run.Decoder.Info.HardwareDecoding);
        var stopwatch = Stopwatch.StartNew();
        while (!run.Decoder.IsPrerolled)
        {
            Assert.Null(run.Decoder.ErrorMessage);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "not prerolled after opening");
            Thread.Sleep(5);
        }

        long target = TimeSpan.FromSeconds(3).Ticks;
        run.Decoder.RequestSeek(target);
        stopwatch.Restart();
        while (!run.Decoder.IsPrerolled)
        {
            // Like the render thread: its calls free the outdated frames (nothing is due before the start).
            run.Decoder.Queue.Pacer.TrySelect(long.MinValue, out _, out _);
            Assert.Null(run.Decoder.ErrorMessage);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "seek not done");
            Thread.Sleep(5);
        }

        Assert.True(run.Decoder.Queue.Pacer.TrySelect(target, out _, out long shown));
        Assert.InRange(shown, target - TimeSpan.TicksPerMillisecond, target + TimeSpan.TicksPerMillisecond);
    }

    [Fact]
    public void H264AboveTheGpuLimit_FailsWithHardwareOnly()
    {
        GraphicsDevice device = fixture.RequireHardware();

        MediaOpenException exception = Assert.Throws<MediaOpenException>(
            () => DecoderRun.Open(device, TestVideos.H264AboveGpuLimit, preference: DecoderPreference.Hardware));

        Assert.Contains("4320 × 2160", exception.OperatorMessage.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void HardwareFailure_BeforeTheFirstFrame_IsReported()
    {
        GraphicsDevice device = fixture.RequireHardware();

        // Without the check the GPU gets the H.264 video it cannot decode, as if the check had missed it.
        MediaDemuxer demuxer = MediaDemuxer.Open(TestVideos.H264AboveGpuLimit, withAudio: false, NullLogger.Instance);
        VideoDecoder decoder = VideoDecoder.Open(
            demuxer, ownsDemuxer: true, device.Device, device.ImmediateContext, VideoFormat.Default, slotCount: 6,
            TestOutputLoggerFactory.Instance.CreateLogger("VideoDecoder"), checkHardware: false);
        try
        {
            Assert.True(decoder.Info.HardwareDecoding);
            decoder.Start();
            var stopwatch = Stopwatch.StartNew();
            while (decoder.ErrorMessage is null && stopwatch.Elapsed < TimeSpan.FromSeconds(15))
            {
                Thread.Sleep(10);
            }

            Assert.NotNull(decoder.ErrorMessage);
            Assert.True(decoder.HardwareFailedBeforeFirstFrame);
            Assert.False(decoder.HasFirstFrame);
        }
        finally
        {
            decoder.Dispose();
            decoder.Queue.Dispose();
        }
    }

    [Theory]
    [InlineData("equirect360_mono_4096x2048_hevc_8bit_30fps.mp4", "NV12")]
    [InlineData("equirect360_mono_4096x2048_hevc_10bit_30fps_4s.mp4", "P010")]
    public void SoftwareDecoding_GivesTheSamePictureAsTheHardware(string fileName, string textureFormat)
    {
        GraphicsDevice device = fixture.RequireHardware();
        string path = Path.Combine(TestVideos.OutputDirectory, fileName);
        Assert.SkipUnless(File.Exists(path), $"{fileName} missing, run python tools/testmedia/make_testmedia.py.");

        (ushort[] hardware, DxgiFormat hardwareFormat) = FirstPicture(device, path, DecoderPreference.Hardware);
        (ushort[] software, DxgiFormat softwareFormat) = FirstPicture(device, path, DecoderPreference.Software);

        Assert.Equal(textureFormat, hardwareFormat.ToString());
        Assert.Equal(hardwareFormat, softwareFormat);
        Assert.Equal(hardware.Length, software.Length);
        int different = 0;
        int largest = 0;
        for (int i = 0; i < hardware.Length; i++)
        {
            int difference = Math.Abs(hardware[i] - software[i]);
            if (difference != 0)
            {
                different++;
                largest = Math.Max(largest, difference);
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{fileName}: {different} of {hardware.Length} samples differ, at most by {largest}.");

        // HEVC decoding is exact: both decoders give the same samples, so luma, chroma and their order must match.
        Assert.Equal(0, different);
    }

    /// <summary>Luma and interleaved chroma samples of the first frame, as the frame ring holds them.</summary>
    private static (ushort[] Samples, DxgiFormat Format) FirstPicture(GraphicsDevice device, string path, DecoderPreference preference)
    {
        using DecoderRun run = DecoderRun.Open(device, path, preference: preference);
        Assert.Equal(preference == DecoderPreference.Hardware, run.Decoder.Info.HardwareDecoding);
        var stopwatch = Stopwatch.StartNew();
        int slot;
        while (!run.Decoder.Queue.Pacer.TrySelect(0, out slot, out _))
        {
            Assert.Null(run.Decoder.ErrorMessage);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(30), "no first frame");
            Thread.Sleep(5);
        }

        // The selected slot stays untouched until the next selection.
        ID3D11Texture2D texture = run.Decoder.Queue.GetTexture(slot);
        Texture2DDescription description = texture.Description;
        using ID3D11Texture2D staging = device.Device.CreateTexture2D(description with
        {
            BindFlags = BindFlags.None,
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });
        device.ImmediateContext.CopyResource(staging, texture);
        device.ImmediateContext.Map(staging, 0, MapMode.Read, MapFlags.None, out MappedSubresource mapped).CheckError();
        try
        {
            bool wide = description.Format == DxgiFormat.P010;
            int width = (int)description.Width;
            int rows = (int)description.Height * 3 / 2;
            var samples = new ushort[width * rows];
            var row = new byte[width * (wide ? 2 : 1)];
            for (int y = 0; y < rows; y++)
            {
                Marshal.Copy(mapped.DataPointer + (y * (nint)mapped.RowPitch), row, 0, row.Length);
                for (int x = 0; x < width; x++)
                {
                    samples[(y * width) + x] = wide ? BitConverter.ToUInt16(row, x * 2) : row[x];
                }
            }

            return (samples, description.Format);
        }
        finally
        {
            device.ImmediateContext.Unmap(staging, 0);
        }
    }

    /// <summary>A started decoder plus a consumer that takes every frame exactly once, as fast as it comes.</summary>
    private sealed class DecoderRun : IDisposable
    {
        private DecoderRun(VideoDecoder decoder)
        {
            Decoder = decoder;
        }

        public VideoDecoder Decoder { get; }

        public static DecoderRun Open(GraphicsDevice device, string path, VideoFormat? format = null, DecoderPreference preference = DecoderPreference.Auto)
        {
            VideoDecoder decoder = VideoDecoder.Open(
                path, device.Device, device.ImmediateContext, format ?? VideoFormat.Default, slotCount: 6, TestOutputLoggerFactory.Instance.CreateLogger("VideoDecoder"), preference);
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
