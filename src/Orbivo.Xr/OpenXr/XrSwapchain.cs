using System.Runtime.InteropServices;
using Orbivo.Xr.Graphics;
using Silk.NET.OpenXR;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Orbivo.Xr.OpenXr;

/// <summary>
/// The swapchain of one eye. Render target views for all images are created up front,
/// so acquiring an image in the frame loop does not allocate (CLAUDE.md rule 1).
/// </summary>
internal sealed unsafe class XrSwapchain : IDisposable
{
    private const long InfiniteDuration = long.MaxValue;

    private readonly XR _xr;
    private readonly ID3D11Texture2D[] _textures;
    private readonly ID3D11RenderTargetView[] _renderTargetViews;
    private bool _disposed;

    private XrSwapchain(XR xr, Swapchain handle, int width, int height, Format format, ID3D11Texture2D[] textures, ID3D11RenderTargetView[] views)
    {
        _xr = xr;
        Handle = handle;
        Width = width;
        Height = height;
        Format = format;
        _textures = textures;
        _renderTargetViews = views;
    }

    public Swapchain Handle { get; }

    public int Width { get; }

    public int Height { get; }

    public Format Format { get; }

    public int ImageCount => _textures.Length;

    public static XrSwapchain Create(XR xr, Session session, GraphicsDevice device, Format format, int width, int height)
    {
        var createInfo = new SwapchainCreateInfo(
            type: StructureType.SwapchainCreateInfo,
            usageFlags: SwapchainUsageFlags.ColorAttachmentBit,
            format: (long)format,
            sampleCount: 1,
            width: (uint)width,
            height: (uint)height,
            faceCount: 1,
            arraySize: 1,
            mipCount: 1);
        Swapchain handle = default;
        xr.CreateSwapchain(session, &createInfo, &handle).Check("xrCreateSwapchain");

        var textures = new List<ID3D11Texture2D>();
        var views = new List<ID3D11RenderTargetView>();
        try
        {
            uint count = 0;
            xr.EnumerateSwapchainImages(handle, 0, &count, (SwapchainImageBaseHeader*)null).Check("xrEnumerateSwapchainImages");
            var images = new SwapchainImageD3D11KHR[count];
            for (int i = 0; i < images.Length; i++)
            {
                images[i] = new SwapchainImageD3D11KHR(type: StructureType.SwapchainImageD3D11Khr);
            }

            fixed (SwapchainImageD3D11KHR* pointer = images)
            {
                xr.EnumerateSwapchainImages(handle, count, &count, (SwapchainImageBaseHeader*)pointer).Check("xrEnumerateSwapchainImages");
            }

            // The runtime may create the textures typeless, so the view names the format explicitly.
            var viewDescription = new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, format);
            foreach (SwapchainImageD3D11KHR image in images)
            {
                // The runtime owns the texture; our wrapper holds its own reference and releases only that one.
                Marshal.AddRef((nint)image.Texture);
                var texture = new ID3D11Texture2D((nint)image.Texture);
                textures.Add(texture);
                views.Add(device.Device.CreateRenderTargetView(texture, viewDescription));
            }

            return new XrSwapchain(xr, handle, width, height, format, [.. textures], [.. views]);
        }
        catch
        {
            foreach (ID3D11RenderTargetView view in views)
            {
                view.Dispose();
            }

            foreach (ID3D11Texture2D texture in textures)
            {
                texture.Dispose();
            }

            xr.DestroySwapchain(handle);
            throw;
        }
    }

    /// <summary>Acquires the next image and waits until it can be rendered to. Frame loop; does not allocate.</summary>
    public ID3D11RenderTargetView AcquireImage()
    {
        uint index = 0;
        var acquireInfo = new SwapchainImageAcquireInfo(type: StructureType.SwapchainImageAcquireInfo);
        _xr.AcquireSwapchainImage(Handle, &acquireInfo, &index).Check("xrAcquireSwapchainImage");

        var waitInfo = new SwapchainImageWaitInfo(type: StructureType.SwapchainImageWaitInfo, timeout: InfiniteDuration);
        _xr.WaitSwapchainImage(Handle, &waitInfo).Check("xrWaitSwapchainImage");
        return _renderTargetViews[index];
    }

    /// <summary>Hands the rendered image back to the runtime. Frame loop; does not allocate.</summary>
    public void ReleaseImage()
    {
        var releaseInfo = new SwapchainImageReleaseInfo(type: StructureType.SwapchainImageReleaseInfo);
        _xr.ReleaseSwapchainImage(Handle, &releaseInfo).Check("xrReleaseSwapchainImage");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (ID3D11RenderTargetView view in _renderTargetViews)
        {
            view.Dispose();
        }

        foreach (ID3D11Texture2D texture in _textures)
        {
            texture.Dispose();
        }

        _xr.DestroySwapchain(Handle);
    }
}
