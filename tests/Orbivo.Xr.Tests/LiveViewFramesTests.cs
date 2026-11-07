using System.IO;
using System.Runtime.InteropServices;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Xr.LiveView;
using Orbivo.Xr.Rendering;
using Orbivo.Xr.Tests.Support;
using Vortice.Direct3D11;
using Vortice.DXGI;
using MapFlags = Vortice.Direct3D11.MapFlags;

namespace Orbivo.Xr.Tests;

/// <summary>The live view (F42) as the render thread draws it and the presenter takes it, on WARP.</summary>
public sealed class LiveViewFramesTests(WarpRendererFixture fixture) : IClassFixture<WarpRendererFixture>
{
    private static readonly long Interval = LiveViewFrames.MinFrameInterval;

    [Fact]
    public void LiveView_ShowsTheSameImageAsADirectRender()
    {
        using StillImageTexture image = StillImageTexture.Create(fixture.Device, TestMedia.LoadBgra32(TestMedia.MonoTestImagePath));
        using var frames = new LiveViewFrames(fixture.Device) { Enabled = true };
        frames.SetTargetSize(400, 300);

        // A headset-like combined view: wider than high, looking left and a little up.
        var fov = new FovAngles(TestViews.Radians(-50), TestViews.Radians(50), TestViews.Radians(40), TestViews.Radians(-40));
        var view = new ViewParameters(OrientationMath.FromYawPitch(TestViews.Radians(60), TestViews.Radians(10)), fov);
        PanoramaSource source = PanoramaSource.FromImage(image, VideoFormat.Default);
        frames.RenderIfDue(fixture.Renderer, fixture.Device.ImmediateContext, view, source, 1f, Interval);

        Assert.True(frames.TryGetNewest(out LiveViewFrame frame, out bool isNew));
        Assert.True(isNew);
        (int width, int height) = CombinedView.FitSize(CombinedView.AspectRatio(fov), 400, 300);
        Assert.Equal((width, height), (frame.Width, frame.Height));
        Assert.Equal(400, frame.Width);
        Assert.Equal(Interval, frame.Timestamp);

        using var direct = new OffscreenTarget(fixture.Device, width, height, Format.B8G8R8A8_UNorm_SRgb);
        fixture.Renderer.Render(fixture.Device.ImmediateContext, direct.RenderTargetView, width, height, view, Eye.Left, source, 1f);
        byte[] expected = direct.Read<byte>();
        byte[] actual = ReadImage(frame);
        TestMedia.SavePng(Path.Combine(TestMedia.RenderedViewsDirectory, "live-view-left-up.png"), width, height, actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Off_DrawsNothing()
    {
        using var frames = new LiveViewFrames(fixture.Device);
        frames.SetTargetSize(64, 64);
        frames.RenderIfDue(fixture.Renderer, fixture.Device.ImmediateContext, TestViews.Create(0, 0, 90, 1), PanoramaSource.Black, 1f, Interval);

        Assert.Equal(0, frames.LastFrameTimestamp);
        Assert.False(frames.TryGetNewest(out _, out _));
    }

    [Fact]
    public void WithoutATargetSize_DrawsNothing()
    {
        using var frames = new LiveViewFrames(fixture.Device) { Enabled = true };
        frames.RenderIfDue(fixture.Renderer, fixture.Device.ImmediateContext, TestViews.Create(0, 0, 90, 1), PanoramaSource.Black, 1f, Interval);
        Assert.False(frames.TryGetNewest(out _, out _));
    }

    [Fact]
    public void DrawsAtMostOncePerInterval_AndKeepsTheLastImage()
    {
        using var frames = new LiveViewFrames(fixture.Device) { Enabled = true };
        frames.SetTargetSize(64, 64);
        ViewParameters view = TestViews.Create(0, 0, 90, 1);
        ID3D11DeviceContext context = fixture.Device.ImmediateContext;

        frames.RenderIfDue(fixture.Renderer, context, view, PanoramaSource.Black, 1f, Interval);
        Assert.True(frames.TryGetNewest(out _, out bool first));
        Assert.True(first);

        // One headset frame later (11 ms at 90 Hz): too early.
        frames.RenderIfDue(fixture.Renderer, context, view, PanoramaSource.Black, 1f, Interval + (Interval / 3));
        Assert.True(frames.TryGetNewest(out LiveViewFrame kept, out bool second));
        Assert.False(second);
        Assert.Equal(Interval, kept.Timestamp);

        frames.RenderIfDue(fixture.Renderer, context, view, PanoramaSource.Black, 1f, 2 * Interval);
        Assert.True(frames.TryGetNewest(out LiveViewFrame next, out bool third));
        Assert.True(third);
        Assert.Equal(2 * Interval, next.Timestamp);
    }

    [Fact]
    public void BigPanel_IsLimitedToTheMaximumSize()
    {
        using var frames = new LiveViewFrames(fixture.Device) { Enabled = true };
        frames.SetTargetSize(5000, 5000);
        frames.RenderIfDue(fixture.Renderer, fixture.Device.ImmediateContext, TestViews.Create(0, 0, 90, 2), PanoramaSource.Black, 1f, Interval);

        Assert.True(frames.TryGetNewest(out LiveViewFrame frame, out _));
        Assert.Equal(LiveViewFrames.MaxWidth, frame.Width);
        Assert.Equal(LiveViewFrames.MaxWidth / 2, frame.Height);
    }

    [Fact]
    public void RenderIfDue_DoesNotAllocate()
    {
        using var frames = new LiveViewFrames(fixture.Device) { Enabled = true };
        frames.SetTargetSize(32, 32);
        using StillImageTexture texture = StillImageTexture.Create(fixture.Device, new Bgra32Image(8, 4, new byte[8 * 4 * 4]));
        ViewParameters view = TestViews.Create(30, 10, 75, 1);
        PanoramaSource source = PanoramaSource.FromImage(texture, VideoFormat.Default);
        ID3D11DeviceContext context = fixture.Device.ImmediateContext;
        for (int i = 1; i <= 10; i++)
        {
            frames.RenderIfDue(fixture.Renderer, context, view, source, 1f, i * Interval);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 11; i <= 200; i++)
        {
            frames.RenderIfDue(fixture.Renderer, context, view, source, 1f, i * Interval);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Copies the image part of the slot to the CPU, rows without padding.</summary>
    private byte[] ReadImage(LiveViewFrame frame)
    {
        ID3D11DeviceContext context = fixture.Device.ImmediateContext;
        using ID3D11Texture2D staging = fixture.Device.Device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm,
            (uint)frame.Width,
            (uint)frame.Height,
            arraySize: 1,
            mipLevels: 1,
            bindFlags: BindFlags.None,
            usage: ResourceUsage.Staging,
            cpuAccessFlags: CpuAccessFlags.Read));
        context.CopySubresourceRegion(staging, 0, 0, 0, 0, frame.Texture, 0, new Vortice.Mathematics.Box(0, 0, 0, frame.Width, frame.Height, 1));
        MappedSubresource mapped = context.Map(staging, 0, MapMode.Read, MapFlags.None);
        try
        {
            int rowBytes = frame.Width * 4;
            byte[] result = new byte[rowBytes * frame.Height];
            for (int y = 0; y < frame.Height; y++)
            {
                Marshal.Copy(mapped.DataPointer + (nint)((long)y * mapped.RowPitch), result, y * rowBytes, rowBytes);
            }

            return result;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }
}
