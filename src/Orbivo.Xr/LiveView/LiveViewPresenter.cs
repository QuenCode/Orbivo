using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orbivo.Xr.Graphics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Orbivo.Xr.DebugViewport.NativeMethods;

namespace Orbivo.Xr.LiveView;

public enum LiveViewState
{
    Starting,
    Running,
    Failed,
    Stopped,
}

/// <summary>State of the live view for the operator UI.</summary>
/// <param name="State">Presenter state.</param>
/// <param name="FramesPerSecond">New images shown per second, averaged over about one second.</param>
/// <param name="LastFrameTimestamp">Stopwatch timestamp of the newest image from the render thread, 0 if none yet.</param>
/// <param name="Error">Error text (English, details in the log), or null.</param>
public readonly record struct LiveViewStatus(LiveViewState State, float FramesPerSecond, long LastFrameTimestamp, string? Error);

/// <summary>
/// Shows the live view (F42) in a child window of the operator window, on a thread of its own: copies the newest
/// image of <see cref="LiveViewFrames"/> into a swap chain of the window and presents it. While it runs, the render
/// thread draws the live view; once it is disposed, it draws nothing for it.
/// </summary>
/// <remarks>
/// The device's immediate context is shared with the render thread. The presenter therefore only uses calls that do not
/// change the pipeline state (clear, copy, present, flush); a draw would change the bound shaders or targets of the
/// render thread in the middle of its frame.
/// </remarks>
public sealed class LiveViewPresenter : IDisposable
{
    private const uint FrameWaitTimeoutMilliseconds = 100;
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(4);
    private static readonly TimeSpan HiddenWait = TimeSpan.FromMilliseconds(50);

    private readonly GraphicsDevice _device;
    private readonly LiveViewFrames _frames;
    private readonly nint _window;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private volatile bool _stopRequested;
    private int _state = (int)LiveViewState.Starting;
    private float _framesPerSecond;
    private string? _error;
    private bool _disposed;

    /// <param name="device">The device the render thread draws the live view with.</param>
    /// <param name="frames">Images from the render thread.</param>
    /// <param name="window">Child window from <see cref="LiveViewWindow.CreateChild"/>; must outlive the presenter.</param>
    /// <param name="logger">Logger.</param>
    public LiveViewPresenter(GraphicsDevice device, LiveViewFrames frames, nint window, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(frames);
        ArgumentNullException.ThrowIfNull(logger);
        _device = device;
        _frames = frames;
        _window = window;
        _logger = logger;
        _thread = new Thread(PresentThread)
        {
            Name = "Orbivo live view",
            IsBackground = true,
        };
    }

    public void Start()
    {
        _frames.Enabled = true;
        _thread.Start();
    }

    public LiveViewStatus GetStatus()
    {
        return new LiveViewStatus(
            (LiveViewState)Volatile.Read(ref _state),
            Volatile.Read(ref _framesPerSecond),
            _frames.LastFrameTimestamp,
            Volatile.Read(ref _error));
    }

    /// <summary>Stops the thread; the render threads stop drawing the live view. Call before destroying the window.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _frames.Enabled = false;
        _stopRequested = true;
        _stopSignal.Set();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _logger.LogWarning("Live view thread did not stop within 2 s.");
            return;
        }

        _stopSignal.Dispose();
    }

    private void PresentThread()
    {
        _logger.LogInformation("Live view started.");
        try
        {
            GetClientRect(_window, out RECT rect);
            using var swapChain = new LiveViewSwapChain(_device, _window, rect.Right - rect.Left, rect.Bottom - rect.Top, _logger);
            Volatile.Write(ref _state, (int)LiveViewState.Running);
            Run(swapChain);
            Volatile.Write(ref _state, (int)LiveViewState.Stopped);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Live view failed.");
            Volatile.Write(ref _error, exception.Message);
            Volatile.Write(ref _state, (int)LiveViewState.Failed);
        }
        finally
        {
            _frames.Enabled = false;
            Volatile.Write(ref _framesPerSecond, 0f);
            _logger.LogInformation("Live view stopped.");
        }
    }

    private void Run(LiveViewSwapChain swapChain)
    {
        ID3D11DeviceContext context = _device.ImmediateContext;
        bool dirty = true;
        int framesInInterval = 0;
        long intervalStart = Stopwatch.GetTimestamp();
        while (!_stopRequested)
        {
            GetClientRect(_window, out RECT rect);
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0)
            {
                // Hidden or collapsed: nothing to show, nothing for the render thread to draw.
                _frames.SetTargetSize(0, 0);
                _stopSignal.Wait(HiddenWait);
                continue;
            }

            if (width != swapChain.Width || height != swapChain.Height)
            {
                swapChain.Resize(width, height);
                dirty = true;
            }

            _frames.SetTargetSize(width, height);
            bool hasFrame = _frames.TryGetNewest(out LiveViewFrame frame, out bool isNew);
            if (!isNew && !dirty)
            {
                UpdateRate(ref framesInInterval, ref intervalStart);
                _stopSignal.Wait(IdleWait);
                continue;
            }

            swapChain.WaitForFrame(FrameWaitTimeoutMilliseconds);
            swapChain.Clear();
            if (hasFrame)
            {
                // Centered, black around it; cut if the panel became smaller than the image.
                int copyWidth = Math.Min(frame.Width, width);
                int copyHeight = Math.Min(frame.Height, height);
                int x = (width - copyWidth) / 2;
                int y = (height - copyHeight) / 2;
                context.CopySubresourceRegion(
                    swapChain.BackBuffer,
                    0,
                    (uint)x,
                    (uint)y,
                    0,
                    frame.Texture,
                    0,
                    new Box(0, 0, 0, copyWidth, copyHeight, 1));
            }

            swapChain.Present();
            dirty = false;
            if (isNew)
            {
                framesInInterval++;
            }

            UpdateRate(ref framesInInterval, ref intervalStart);
        }
    }

    private void UpdateRate(ref int framesInInterval, ref long intervalStart)
    {
        long now = Stopwatch.GetTimestamp();
        if (now - intervalStart >= Stopwatch.Frequency)
        {
            Volatile.Write(ref _framesPerSecond, framesInInterval * (float)Stopwatch.Frequency / (now - intervalStart));
            framesInInterval = 0;
            intervalStart = now;
        }
    }

    /// <summary>
    /// Swap chain of the live view window. Flip model with a frame latency wait handle; if the system does not allow
    /// that for a child window, the older copy model (presenting waits for the vertical blank itself).
    /// </summary>
    private sealed class LiveViewSwapChain : IDisposable
    {
        private const Format BufferFormat = Format.B8G8R8A8_UNorm;
        private static readonly Color4 Black = new(0f, 0f, 0f, 1f);

        private readonly GraphicsDevice _device;
        private readonly IDXGISwapChain1 _swapChain;
        private readonly SwapChainFlags _flags;
        private readonly nint _frameWaitHandle;
        private ID3D11Texture2D? _backBuffer;
        private ID3D11RenderTargetView? _backBufferView;

        public LiveViewSwapChain(GraphicsDevice device, nint window, int width, int height, ILogger logger)
        {
            _device = device;
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            using IDXGIDevice dxgiDevice = device.Device.QueryInterface<IDXGIDevice>();
            using IDXGIAdapter adapter = dxgiDevice.GetAdapter();
            using IDXGIFactory2 factory = adapter.GetParent<IDXGIFactory2>();

            try
            {
                _flags = SwapChainFlags.FrameLatencyWaitableObject;
                _swapChain = factory.CreateSwapChainForHwnd(
                    device.Device,
                    window,
                    new SwapChainDescription1((uint)width, (uint)height, BufferFormat, bufferCount: 2, swapEffect: SwapEffect.FlipDiscard, flags: _flags),
                    null,
                    null);
                using IDXGISwapChain2 swapChain2 = _swapChain.QueryInterface<IDXGISwapChain2>();
                swapChain2.MaximumFrameLatency = 1;
                _frameWaitHandle = swapChain2.FrameLatencyWaitableObject;
            }
            catch (SharpGen.Runtime.SharpGenException exception)
            {
                logger.LogWarning(exception, "Flip-model swap chain for the live view window not available; using the copy model.");
                _flags = SwapChainFlags.None;
                _swapChain = factory.CreateSwapChainForHwnd(
                    device.Device,
                    window,
                    new SwapChainDescription1((uint)width, (uint)height, BufferFormat, bufferCount: 1, swapEffect: SwapEffect.Discard, flags: _flags),
                    null,
                    null);
            }

            factory.MakeWindowAssociation(window, WindowAssociationFlags.IgnoreAll).CheckError();
            Width = width;
            Height = height;
            AcquireBackBuffer();
        }

        public int Width { get; private set; }

        public int Height { get; private set; }

        public ID3D11Texture2D BackBuffer => _backBuffer ?? throw new ObjectDisposedException(nameof(LiveViewSwapChain));

        public void WaitForFrame(uint timeoutMilliseconds)
        {
            if (_frameWaitHandle != 0)
            {
                WaitForSingleObject(_frameWaitHandle, timeoutMilliseconds);
            }
        }

        public void Clear()
        {
            _device.ImmediateContext.ClearRenderTargetView(_backBufferView, Black);
        }

        public void Present()
        {
            _swapChain.Present(1, PresentFlags.None).CheckError();
        }

        public void Resize(int width, int height)
        {
            // The back buffer is never bound to the pipeline, so no ClearState (which would hit the render thread).
            ReleaseBackBuffer();
            _device.ImmediateContext.Flush();
            _swapChain.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, _flags).CheckError();
            Width = width;
            Height = height;
            AcquireBackBuffer();
        }

        public void Dispose()
        {
            ReleaseBackBuffer();
            if (_frameWaitHandle != 0)
            {
                CloseHandle(_frameWaitHandle);
            }

            _swapChain.Dispose();
        }

        private void AcquireBackBuffer()
        {
            _backBuffer = _swapChain.GetBuffer<ID3D11Texture2D>(0);
            _backBufferView = _device.Device.CreateRenderTargetView(_backBuffer, null);
        }

        private void ReleaseBackBuffer()
        {
            _backBufferView?.Dispose();
            _backBufferView = null;
            _backBuffer?.Dispose();
            _backBuffer = null;
        }
    }
}
