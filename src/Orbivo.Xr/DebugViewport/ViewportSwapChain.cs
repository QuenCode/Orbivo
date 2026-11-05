using Orbivo.Xr.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace Orbivo.Xr.DebugViewport;

/// <summary>
/// Flip-model swap chain of the debug viewport with a frame latency wait handle, so that the loop runs like the
/// headset loop later: wait for the frame, read input, render, present.
/// </summary>
/// <remarks>
/// Gamma (docs/DECISIONS.md): flip-model swap chains do not accept *_SRGB formats. The buffers are
/// B8G8R8A8_UNORM and the render target view is B8G8R8A8_UNORM_SRGB, which encodes the linear shader output.
/// </remarks>
internal sealed class ViewportSwapChain : IDisposable
{
    private const Format BufferFormat = Format.B8G8R8A8_UNorm;
    private const Format ViewFormat = Format.B8G8R8A8_UNorm_SRgb;
    private const SwapChainFlags Flags = SwapChainFlags.FrameLatencyWaitableObject;

    private readonly GraphicsDevice _device;
    private readonly IDXGISwapChain2 _swapChain;
    private ID3D11RenderTargetView? _renderTargetView;

    public ViewportSwapChain(GraphicsDevice device, nint window, int width, int height)
    {
        _device = device;

        using IDXGIDevice dxgiDevice = device.Device.QueryInterface<IDXGIDevice>();
        using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
        using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

        var description = new SwapChainDescription1(
            (uint)width,
            (uint)height,
            BufferFormat,
            bufferCount: 2,
            swapEffect: SwapEffect.FlipDiscard,
            flags: Flags);

        using (IDXGISwapChain1 swapChain1 = factory.CreateSwapChainForHwnd(device.Device, window, description, null, null))
        {
            _swapChain = swapChain1.QueryInterface<IDXGISwapChain2>();
        }

        factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAltEnter).CheckError();
        _swapChain.MaximumFrameLatency = 1;
        FrameLatencyWaitHandle = _swapChain.FrameLatencyWaitableObject;
        Width = width;
        Height = height;
        CreateRenderTargetView();
    }

    /// <summary>Signaled when the next frame may be rendered. Owned by this object.</summary>
    public nint FrameLatencyWaitHandle { get; }

    public int Width { get; private set; }

    public int Height { get; private set; }

    public ID3D11RenderTargetView RenderTargetView =>
        _renderTargetView ?? throw new ObjectDisposedException(nameof(ViewportSwapChain));

    public void Resize(int width, int height)
    {
        // All references to the back buffers must be gone before ResizeBuffers. The renderer binds its
        // state again every frame, so clearing the context state is safe.
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        _device.ImmediateContext.ClearState();
        _device.ImmediateContext.Flush();

        _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, Flags).CheckError();
        Width = width;
        Height = height;
        CreateRenderTargetView();
    }

    /// <summary>Presents with vertical sync. Throws if the device was lost.</summary>
    public void Present()
    {
        _swapChain.Present(1, PresentFlags.None).CheckError();
    }

    public void Dispose()
    {
        _renderTargetView?.Dispose();
        _renderTargetView = null;
        NativeMethods.CloseHandle(FrameLatencyWaitHandle);
        _swapChain.Dispose();
    }

    private void CreateRenderTargetView()
    {
        using ID3D11Texture2D backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
        _renderTargetView = _device.Device.CreateRenderTargetView(
            backBuffer,
            new RenderTargetViewDescription(RenderTargetViewDimension.Texture2D, ViewFormat));
    }
}
