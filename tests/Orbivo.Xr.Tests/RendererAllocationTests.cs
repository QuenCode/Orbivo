using Orbivo.Core.Geometry;
using Orbivo.Xr.Rendering;
using Orbivo.Xr.Tests.Support;
using Vortice.DXGI;

namespace Orbivo.Xr.Tests;

/// <summary>CLAUDE.md rule 1: the per-frame render call must not allocate.</summary>
public sealed class RendererAllocationTests(WarpRendererFixture fixture) : IClassFixture<WarpRendererFixture>
{
    [Fact]
    public void Render_DoesNotAllocate()
    {
        const int size = 32;
        using var target = new OffscreenTarget(fixture.Device, size, size, Format.B8G8R8A8_UNorm_SRgb);
        using StillImageTexture texture = StillImageTexture.Create(fixture.Device, new Bgra32Image(8, 4, new byte[8 * 4 * 4]));
        ViewParameters view = TestViews.Create(30, 10, 75, 1);

        for (int i = 0; i < 10; i++)
        {
            fixture.Renderer.Render(fixture.Device.ImmediateContext, target.RenderTargetView, size, size, view, texture);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 200; i++)
        {
            fixture.Renderer.Render(fixture.Device.ImmediateContext, target.RenderTargetView, size, size, view, texture);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }
}
