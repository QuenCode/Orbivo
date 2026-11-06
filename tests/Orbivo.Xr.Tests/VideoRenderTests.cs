using System.Numerics;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Xr.Rendering;
using Orbivo.Xr.Tests.Support;
using Vortice.DXGI;

namespace Orbivo.Xr.Tests;

/// <summary>
/// Video frames on WARP: the YUV conversion in color.hlsl against VideoColor (C# reference), the stereo layout in
/// projection.hlsl against ProjectionMath (CLAUDE.md rule 8), and the allocation-free render path (rule 1).
/// </summary>
public sealed class VideoRenderTests(WarpRendererFixture fixture) : IClassFixture<WarpRendererFixture>
{
    private const int Size = 32;

    public static TheoryData<int, YuvMatrix, YuvRange, ColorPrimaries, int, int, int> ColorCases => new()
    {
        // bit depth, matrix, range, primaries, Y, Cb, Cr
        { 8, YuvMatrix.Bt709, YuvRange.Limited, ColorPrimaries.Bt709, 16, 128, 128 },
        { 8, YuvMatrix.Bt709, YuvRange.Limited, ColorPrimaries.Bt709, 235, 128, 128 },
        { 8, YuvMatrix.Bt709, YuvRange.Limited, ColorPrimaries.Bt709, 126, 128, 128 },
        { 8, YuvMatrix.Bt709, YuvRange.Limited, ColorPrimaries.Bt709, 63, 102, 240 },
        { 8, YuvMatrix.Bt709, YuvRange.Limited, ColorPrimaries.Bt709, 173, 42, 26 },
        { 8, YuvMatrix.Bt601, YuvRange.Limited, ColorPrimaries.Bt709, 100, 150, 90 },
        { 8, YuvMatrix.Bt709, YuvRange.Full, ColorPrimaries.Bt709, 200, 60, 180 },
        { 10, YuvMatrix.Bt709, YuvRange.Limited, ColorPrimaries.Bt709, 502, 400, 600 },
        { 10, YuvMatrix.Bt2020, YuvRange.Limited, ColorPrimaries.Bt2020, 502, 512, 512 },
        { 10, YuvMatrix.Bt2020, YuvRange.Limited, ColorPrimaries.Bt2020, 600, 450, 700 },
    };

    [Theory]
    [MemberData(nameof(ColorCases))]
    public void VideoColor_MatchesTheCSharpReference(int bitDepth, YuvMatrix matrix, YuvRange range, ColorPrimaries primaries, int y, int cb, int cr)
    {
        var color = new VideoColorFormat(matrix, range, primaries, bitDepth);
        using var frame = new TestVideoFrame(fixture.Device, 64, 32, bitDepth, (y, cb, cr), (y, cb, cr), StereoLayout.Mono, color);

        byte[] bgra = RenderSrgb(PanoramaSource.FromVideo(frame.Frame), Eye.Left, TestViews.Create(0, 0, 60, 1));

        Vector3 linear = VideoColor.SampleToLinear(TestVideoFrame.Samples((y, cb, cr), bitDepth), frame.Constants);
        var expected = new Vector3(VideoColor.LinearToSrgb(linear.X), VideoColor.LinearToSrgb(linear.Y), VideoColor.LinearToSrgb(linear.Z)) * 255f;
        Vector3 actual = CenterPixel(bgra);

        Assert.True(
            MathF.Abs(expected.X - actual.X) <= 1.5f && MathF.Abs(expected.Y - actual.Y) <= 1.5f && MathF.Abs(expected.Z - actual.Z) <= 1.5f,
            $"expected {expected}, shader {actual}");
    }

    [Theory]
    [InlineData(StereoLayout.TopBottom, Eye.Left, true)]
    [InlineData(StereoLayout.TopBottom, Eye.Right, false)]
    [InlineData(StereoLayout.Mono, Eye.Left, true)]
    [InlineData(StereoLayout.Mono, Eye.Right, true)]
    public void TopBottom_LeftEyeSeesTheUpperHalf(StereoLayout layout, Eye eye, bool expectWhite)
    {
        var color = new VideoColorFormat(YuvMatrix.Bt709, YuvRange.Limited, ColorPrimaries.Bt709, 8);

        // Upper half white, lower half black. In mono, looking up sees the upper half from both eyes.
        using var frame = new TestVideoFrame(fixture.Device, 64, 64, 8, (235, 128, 128), (16, 128, 128), layout, color);
        double pitch = layout == StereoLayout.Mono ? 45 : 0;

        byte[] bgra = RenderSrgb(PanoramaSource.FromVideo(frame.Frame), eye, TestViews.Create(0, pitch, 30, 1));
        Vector3 center = CenterPixel(bgra);

        Assert.True(expectWhite ? center.X > 250 : center.X < 5, $"{layout} {eye}: center {center}");
    }

    public static TheoryData<StereoLayout, Eye> LayoutCases => new()
    {
        { StereoLayout.Mono, Eye.Left },
        { StereoLayout.TopBottom, Eye.Left },
        { StereoLayout.TopBottom, Eye.Right },
        { StereoLayout.SideBySide, Eye.Left },
        { StereoLayout.SideBySide, Eye.Right },
    };

    [Theory]
    [MemberData(nameof(LayoutCases))]
    public void SourceUv_MatchesProjectionMath(StereoLayout layout, Eye eye)
    {
        const int TextureWidth = 64;
        const int TextureHeight = 32;
        var color = new VideoColorFormat(YuvMatrix.Bt709, YuvRange.Limited, ColorPrimaries.Bt709, 8);
        using var frame = new TestVideoFrame(fixture.Device, TextureWidth, TextureHeight, 8, (16, 128, 128), (16, 128, 128), layout, color);
        ViewParameters view = TestViews.Create(150, -30, 100, 1.5);

        using var target = new OffscreenTarget(fixture.Device, Size, Size, Format.R32G32B32A32_Float);
        fixture.Renderer.Render(
            fixture.Device.ImmediateContext, target.RenderTargetView, Size, Size, view, eye, PanoramaSource.FromVideo(frame.Frame), 1f, PanoramaOutput.DebugSourceUv);
        float[] gpu = target.Read<float>();

        ViewRayBasis basis = ProjectionMath.CreateViewRayBasis(view.Orientation, view.Fov);
        EyeSourceRect rect = ProjectionMath.CreateEyeSourceRect(layout, eye, TextureWidth, TextureHeight);
        var mismatches = new List<string>();
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Vector3 direction = ProjectionMath.DirectionFromNdc(basis, ProjectionMath.PixelCenterToNdc(x, y, Size, Size));
                Vector2 expected = ProjectionMath.SourceUv(ProjectionMath.Equirect360Uv(direction), rect);
                int i = ((y * Size) + x) * 4;
                var actual = new Vector2(gpu[i], gpu[i + 1]);

                // u of a full-width eye wraps at the back seam: 0 and 1 are the same column.
                float du = MathF.Abs(expected.X - actual.X);
                if (layout != StereoLayout.SideBySide)
                {
                    du = MathF.Min(du, 1f - du);
                }

                if (du > 1e-4f || MathF.Abs(expected.Y - actual.Y) > 1e-4f)
                {
                    mismatches.Add($"({x},{y}): expected {expected}, shader {actual}");
                }
            }
        }

        Assert.True(mismatches.Count == 0, $"{mismatches.Count} pixels differ, first: {string.Join("; ", mismatches.Take(5))}");
    }

    [Fact]
    public void RenderVideo_DoesNotAllocate()
    {
        var color = new VideoColorFormat(YuvMatrix.Bt709, YuvRange.Limited, ColorPrimaries.Bt709, 8);
        using var frame = new TestVideoFrame(fixture.Device, 64, 64, 8, (100, 120, 140), (50, 130, 110), StereoLayout.TopBottom, color);
        using var target = new OffscreenTarget(fixture.Device, Size, Size, Format.B8G8R8A8_UNorm_SRgb);
        ViewParameters view = TestViews.Create(30, 10, 75, 1);

        for (int i = 0; i < 10; i++)
        {
            RenderBothEyes(frame, target, view);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            RenderBothEyes(frame, target, view);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private void RenderBothEyes(TestVideoFrame frame, OffscreenTarget target, in ViewParameters view)
    {
        PanoramaSource source = PanoramaSource.FromVideo(frame.Frame);
        fixture.Renderer.Render(fixture.Device.ImmediateContext, target.RenderTargetView, Size, Size, view, Eye.Left, source);
        fixture.Renderer.Render(fixture.Device.ImmediateContext, target.RenderTargetView, Size, Size, view, Eye.Right, source);
    }

    private byte[] RenderSrgb(in PanoramaSource source, Eye eye, ViewParameters view)
    {
        using var target = new OffscreenTarget(fixture.Device, Size, Size, Format.B8G8R8A8_UNorm_SRgb);
        fixture.Renderer.Render(fixture.Device.ImmediateContext, target.RenderTargetView, Size, Size, view, eye, source);
        return target.Read<byte>();
    }

    private static Vector3 CenterPixel(byte[] bgra)
    {
        int i = (((Size / 2) * Size) + (Size / 2)) * 4;
        return new Vector3(bgra[i + 2], bgra[i + 1], bgra[i]);
    }
}
