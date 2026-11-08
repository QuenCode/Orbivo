using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Core.Playback;
using Orbivo.Xr.Rendering;
using Orbivo.Xr.Tests.Support;
using Vortice.DXGI;

namespace Orbivo.Xr.Tests;

/// <summary>Idle view (F7): what the render threads draw while no video frame is due.</summary>
public sealed class IdleViewRenderTests(WarpRendererFixture fixture) : IClassFixture<WarpRendererFixture>
{
    private const int Size = 16;

    [Fact]
    public void ForIdle_PicksTheOperatorsChoice()
    {
        using StillImageTexture image = StillImageTexture.Create(fixture.Device, new Bgra32Image(8, 4, new byte[8 * 4 * 4]));

        Assert.True(PanoramaSource.ForIdle(IdleView.Black, image).IsBlack);
        Assert.True(PanoramaSource.ForIdle(IdleView.SeeThrough, image).IsSeeThrough);
        Assert.False(PanoramaSource.ForIdle(IdleView.Image, image).IsBlack);
        Assert.True(PanoramaSource.ForIdle(IdleView.Image, null).IsBlack);
    }

    [Theory]
    [InlineData(8192, 4096, StereoLayout.Mono)]
    [InlineData(4096, 4096, StereoLayout.TopBottom)]
    [InlineData(8192, 2048, StereoLayout.SideBySide)]
    public void IdleImage_KeepsTheLayoutItWasCreatedWith(int width, int height, StereoLayout layout)
    {
        // The texture carries its format, so image and layout always change together.
        using StillImageTexture image = StillImageTexture.Create(fixture.Device, new Bgra32Image(8, 4, new byte[8 * 4 * 4]), StereoLayoutDetection.FromSize(width, height));

        Assert.Equal(layout, image.PictureFormat.Layout);
        Assert.Equal(ProjectionType.Equirect360, image.PictureFormat.Projection);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(0.5f)]
    [InlineData(0f)]
    public void SeeThrough_DrawsTheStandIn_DimmedByFadeAndBlackout(float brightness)
    {
        using var target = new OffscreenTarget(fixture.Device, Size, Size, Format.R8G8B8A8_UNorm);

        fixture.Renderer.Render(
            fixture.Device.ImmediateContext,
            target.RenderTargetView,
            Size,
            Size,
            TestViews.Create(0, 0, 75, 1),
            Eye.Left,
            PanoramaSource.SeeThrough,
            brightness);
        byte[] rgba = target.Read<byte>();

        // Linear target: the stored value is the linear stand-in color times the brightness.
        Assert.InRange(rgba[0], Expected(PanoramaRenderer.SeeThroughStandIn.R * brightness) - 1, Expected(PanoramaRenderer.SeeThroughStandIn.R * brightness) + 1);
        Assert.InRange(rgba[1], Expected(PanoramaRenderer.SeeThroughStandIn.G * brightness) - 1, Expected(PanoramaRenderer.SeeThroughStandIn.G * brightness) + 1);
        Assert.InRange(rgba[2], Expected(PanoramaRenderer.SeeThroughStandIn.B * brightness) - 1, Expected(PanoramaRenderer.SeeThroughStandIn.B * brightness) + 1);
        Assert.Equal(255, rgba[3]);
    }

    [Fact]
    public void SeeThroughStandIn_IsVisiblyNotBlack()
    {
        // About sRGB 48 or more on every channel, so the operator tells it apart from black in the live view.
        Assert.All(
            [PanoramaRenderer.SeeThroughStandIn.R, PanoramaRenderer.SeeThroughStandIn.G, PanoramaRenderer.SeeThroughStandIn.B],
            linear => Assert.InRange(LinearToSrgb(linear) * 255, 45, 80));
    }

    private static int Expected(float linear) => (int)Math.Round(linear * 255);

    private static double LinearToSrgb(double linear)
    {
        return linear <= 0.0031308 ? linear * 12.92 : (1.055 * Math.Pow(linear, 1 / 2.4)) - 0.055;
    }
}
