using Microsoft.Extensions.Logging.Abstractions;
using Orbivo.Xr.Graphics;
using Orbivo.Xr.Rendering;

namespace Orbivo.Xr.Tests.Support;

/// <summary>One WARP device and renderer per test class.</summary>
public sealed class WarpRendererFixture : IDisposable
{
    public WarpRendererFixture()
    {
        Device = GraphicsDevice.CreateWarp(NullLogger.Instance);
        Renderer = new PanoramaRenderer(Device);
    }

    public GraphicsDevice Device { get; }

    public PanoramaRenderer Renderer { get; }

    public void Dispose()
    {
        Renderer.Dispose();
        Device.Dispose();
    }
}
