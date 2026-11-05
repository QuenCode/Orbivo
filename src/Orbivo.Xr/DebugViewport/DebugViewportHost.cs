using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Geometry;
using Orbivo.Xr.Graphics;
using Orbivo.Xr.Rendering;

namespace Orbivo.Xr.DebugViewport;

public enum DebugViewportState
{
    Starting,
    Running,
    Failed,
    Stopped,
}

/// <summary>Snapshot of the debug viewport for the operator UI.</summary>
/// <param name="State">Life cycle state.</param>
/// <param name="FramesPerSecond">Presented frames per second, averaged over about one second.</param>
/// <param name="YawDegrees">View yaw, positive = left.</param>
/// <param name="PitchDegrees">View pitch, positive = up.</param>
/// <param name="VerticalFovDegrees">Vertical field of view.</param>
/// <param name="HasImage">True once an image is shown.</param>
/// <param name="RenderLoopAllocates">True if the render loop allocated after warm-up (CLAUDE.md rule 1, details in the log).</param>
/// <param name="Error">Error message if <paramref name="State"/> is <see cref="DebugViewportState.Failed"/>.</param>
public readonly record struct DebugViewportStatus(
    DebugViewportState State,
    float FramesPerSecond,
    float YawDegrees,
    float PitchDegrees,
    float VerticalFovDegrees,
    bool HasImage,
    bool RenderLoopAllocates,
    string? Error);

/// <summary>
/// Desktop window that stands in for the headset in <c>--no-xr</c> mode (docs/ARCHITECTURE.md).
/// Runs its own render thread with its own window; the operator UI only hands over images and reads status snapshots.
/// </summary>
public sealed class DebugViewportHost : IDisposable
{
    public const string WindowTitle = "Orbivo – Vorschau (Ersatz für die Brille)";

    private const int InitialClientWidth = 1280;
    private const int InitialClientHeight = 720;
    private const uint WaitTimeoutMilliseconds = 100;
    private const int AllocationCheckWarmUpFrames = 120;

    private readonly GraphicsDevice _device;
    private readonly ILogger _logger;
    private readonly Thread _thread;

    private volatile bool _stopRequested;
    private StillImageTexture? _pendingImage;

    // Status, written by the render thread, read by the UI.
    private int _state = (int)DebugViewportState.Starting;
    private float _framesPerSecond;
    private float _yawDegrees;
    private float _pitchDegrees;
    private float _verticalFovDegrees = MouseViewSource.DefaultVerticalFovDegrees;
    private int _hasImage;
    private int _renderLoopAllocates;
    private string? _error;

    public DebugViewportHost(GraphicsDevice device, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(logger);
        _device = device;
        _logger = logger;
        _thread = new Thread(RenderThread)
        {
            Name = "Orbivo debug viewport render",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public void Start()
    {
        _thread.Start();
    }

    /// <summary>
    /// Shows <paramref name="texture"/> from the next frame on. Ownership passes to the viewport. Any thread.
    /// </summary>
    public void ShowImage(StillImageTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        StillImageTexture? notTakenYet = Interlocked.Exchange(ref _pendingImage, texture);
        notTakenYet?.Dispose();
    }

    public DebugViewportStatus GetStatus()
    {
        return new DebugViewportStatus(
            (DebugViewportState)Volatile.Read(ref _state),
            Volatile.Read(ref _framesPerSecond),
            Volatile.Read(ref _yawDegrees),
            Volatile.Read(ref _pitchDegrees),
            Volatile.Read(ref _verticalFovDegrees),
            Volatile.Read(ref _hasImage) != 0,
            Volatile.Read(ref _renderLoopAllocates) != 0,
            Volatile.Read(ref _error));
    }

    public void Dispose()
    {
        _stopRequested = true;
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(3)))
        {
            _logger.LogWarning("Debug viewport render thread did not stop within 3 s.");
        }

        Interlocked.Exchange(ref _pendingImage, null)?.Dispose();
    }

    private void RenderThread()
    {
        StillImageTexture? image = null;
        try
        {
            var view = new MouseViewSource();
            using var window = new DebugViewportWindow(WindowTitle, InitialClientWidth, InitialClientHeight, view);
            using var renderer = new PanoramaRenderer(_device);
            using var swapChain = new ViewportSwapChain(_device, window.Handle, window.ClientWidth, window.ClientHeight);

            _logger.LogInformation(
                "Debug viewport running ({Width}x{Height}). Drag to look around, mouse wheel = field of view, Home = reset.",
                window.ClientWidth,
                window.ClientHeight);
            Volatile.Write(ref _state, (int)DebugViewportState.Running);

            RenderLoop(window, swapChain, renderer, view, ref image);

            Volatile.Write(ref _state, (int)DebugViewportState.Stopped);
            _logger.LogInformation("Debug viewport stopped.");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Debug viewport failed.");
            Volatile.Write(ref _error, exception.Message);
            Volatile.Write(ref _state, (int)DebugViewportState.Failed);
        }
        finally
        {
            image?.Dispose();
        }
    }

    private void RenderLoop(
        DebugViewportWindow window,
        ViewportSwapChain swapChain,
        PanoramaRenderer renderer,
        MouseViewSource view,
        ref StillImageTexture? image)
    {
        Span<ViewParameters> views = stackalloc ViewParameters[1];
        long frame = 0;
        int framesInInterval = 0;
        long intervalStart = Stopwatch.GetTimestamp();

        while (!_stopRequested)
        {
            // Like xrWaitFrame later: wait for the next frame slot, but keep processing input meanwhile.
            nint frameHandle = window.IsMinimized ? 0 : swapChain.FrameLatencyWaitHandle;
            bool frameReady = DebugViewportWindow.WaitForFrameOrMessage(frameHandle, WaitTimeoutMilliseconds);

            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            window.PumpMessages();
            if (!frameReady || _stopRequested)
            {
                continue;
            }

            bool expectedAllocation = false;
            if (window.SizeChanged)
            {
                window.SizeChanged = false;
                swapChain.Resize(window.ClientWidth, window.ClientHeight);
                expectedAllocation = true;
            }

            StillImageTexture? newImage = Interlocked.Exchange(ref _pendingImage, null);
            if (newImage is not null)
            {
                image?.Dispose();
                image = newImage;
                Volatile.Write(ref _hasImage, 1);
                expectedAllocation = true;
            }

            view.GetViews(views);
            renderer.Render(_device.ImmediateContext, swapChain.RenderTargetView, swapChain.Width, swapChain.Height, views[0], image);
            swapChain.Present();

            frame++;
            framesInInterval++;
            PublishView(view);

            long now = Stopwatch.GetTimestamp();
            if (now - intervalStart >= Stopwatch.Frequency)
            {
                Volatile.Write(ref _framesPerSecond, framesInInterval * (float)Stopwatch.Frequency / (now - intervalStart));
                framesInInterval = 0;
                intervalStart = now;
            }

            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            if (allocated > 0 && !expectedAllocation && frame > AllocationCheckWarmUpFrames
                && Interlocked.Exchange(ref _renderLoopAllocates, 1) == 0)
            {
                _logger.LogWarning(
                    "Render loop allocated {Bytes} bytes in frame {Frame} (CLAUDE.md rule 1). Reported once.",
                    allocated,
                    frame);
            }
        }
    }

    private void PublishView(MouseViewSource view)
    {
        Volatile.Write(ref _yawDegrees, float.RadiansToDegrees(view.Yaw));
        Volatile.Write(ref _pitchDegrees, float.RadiansToDegrees(view.Pitch));
        Volatile.Write(ref _verticalFovDegrees, view.VerticalFovDegrees);
    }
}
