using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Audio;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Core.Playback;
using Orbivo.Media;
using Orbivo.Xr.Graphics;
using Orbivo.Xr.LiveView;
using Orbivo.Xr.Rendering;
using Silk.NET.OpenXR;

namespace Orbivo.Xr.OpenXr;

/// <summary>
/// Drives the headset: connects to the OpenXR runtime, runs the frame loop on its own thread and reconnects
/// when the runtime or the headset goes away (docs/ARCHITECTURE.md, "OpenXR-Lebenszyklus").
/// The operator UI only hands over images and reads status snapshots (CLAUDE.md rule 3).
/// </summary>
/// <remarks>
/// The thread waits (sleeps) only while no session is running. In the frame loop it blocks only in
/// <c>xrWaitFrame</c>/<c>xrWaitSwapchainImage</c>, the frame clock of the headset.
/// </remarks>
public sealed class XrHost : IDisposable
{
    private const int AllocationCheckWarmUpFrames = 180;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MaxFailureRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan VideoAdvanceInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(2);

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly bool _requestDebugLayer;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stopSignal = new(false);
    private readonly TaskCompletionSource<GraphicsDevice> _deviceReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private GraphicsDevice? _device;
    private StillImageTexture? _pendingImage;
    private IVideoFrameSource? _videoSource;
    private LiveViewFrames? _liveView;
    private ListenerOrientation? _listener;
    private RecenterControl? _recenter;
    private int _idleView = (int)IdleView.Black;
    private volatile bool _stopRequested;

    // Status, written by the render thread, read by the UI.
    private int _state = (int)XrConnectionState.Starting;
    private int _sessionState = (int)XrSessionState.None;
    private XrRuntimeInfo? _runtime;
    private float _framesPerSecond;
    private float _displayRefreshHz;
    private int _headTracked;
    private float _headYawDegrees;
    private float _headPitchDegrees;
    private int _hasImage;
    private int _seeThroughSupport = (int)SeeThroughSupport.Unknown;
    private int _renderLoopAllocates;
    private string? _error;

    /// <param name="loggerFactory">Creates the loggers for host and graphics device.</param>
    /// <param name="requestDebugLayer">Enables the D3D11 debug layer if installed (debug builds).</param>
    public XrHost(ILoggerFactory loggerFactory, bool requestDebugLayer)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<XrHost>();
        _requestDebugLayer = requestDebugLayer;
        _thread = new Thread(RenderThread)
        {
            Name = "Orbivo XR render",
            IsBackground = true,
            Priority = ThreadPriority.Highest,
        };
    }

    /// <summary>
    /// Completes once the D3D11 device exists on the GPU the runtime requires. Never completes if no runtime
    /// or headset is ever found. Images for <see cref="ShowImage"/> must be created on this device.
    /// </summary>
    public Task<GraphicsDevice> GraphicsDeviceReady => _deviceReady.Task;

    public void Start()
    {
        _thread.Start();
    }

    /// <summary>
    /// Shows video frames from <paramref name="source"/> whenever it has one; otherwise the image or black. Any thread,
    /// call before the device is ready. The source must outlive the host.
    /// </summary>
    public void SetVideoSource(IVideoFrameSource? source)
    {
        Volatile.Write(ref _videoSource, source);
    }

    /// <summary>Draws the live view for the operator (F42) into <paramref name="frames"/> while it is on. Any thread.</summary>
    public void SetLiveView(LiveViewFrames? frames)
    {
        Volatile.Write(ref _liveView, frames);
    }

    /// <summary>
    /// Publishes the head orientation in the picture's frame every rendered frame, for sound fixed to the picture (M7).
    /// Any thread.
    /// </summary>
    public void SetListener(ListenerOrientation? listener)
    {
        Volatile.Write(ref _listener, listener);
    }

    /// <summary>What the headset shows while no video runs (F7), from the next frame on. Any thread.</summary>
    public void SetIdleView(IdleView view)
    {
        Volatile.Write(ref _idleView, (int)view);
    }

    /// <summary>Carries out recenter requests (F30) with the head pose of the next frame. Any thread.</summary>
    public void SetRecenter(RecenterControl? recenter)
    {
        Volatile.Write(ref _recenter, recenter);
    }

    /// <summary>
    /// The idle image (F7, shown while the idle view is <see cref="IdleView.Image"/>), from the next frame on, in the
    /// layout it was created with. Ownership passes to the host. Any thread.
    /// </summary>
    public void ShowImage(StillImageTexture texture)
    {
        ArgumentNullException.ThrowIfNull(texture);
        Interlocked.Exchange(ref _pendingImage, texture)?.Dispose();
    }

    public XrStatus GetStatus()
    {
        return new XrStatus(
            (XrConnectionState)Volatile.Read(ref _state),
            (XrSessionState)Volatile.Read(ref _sessionState),
            Volatile.Read(ref _runtime),
            Volatile.Read(ref _framesPerSecond),
            Volatile.Read(ref _displayRefreshHz),
            Volatile.Read(ref _headTracked) != 0,
            Volatile.Read(ref _headYawDegrees),
            Volatile.Read(ref _headPitchDegrees),
            Volatile.Read(ref _hasImage) != 0,
            Volatile.Read(ref _renderLoopAllocates) != 0,
            Volatile.Read(ref _error),
            RecenterStatus.Of(Volatile.Read(ref _recenter)),
            (SeeThroughSupport)Volatile.Read(ref _seeThroughSupport));
    }

    public void Dispose()
    {
        _stopRequested = true;
        _stopSignal.Set();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("XR render thread did not stop within 5 s.");
            return;
        }

        Interlocked.Exchange(ref _pendingImage, null)?.Dispose();
        _device?.Dispose();
        _stopSignal.Dispose();
    }

    private void RenderThread()
    {
        XR xr;
        try
        {
            xr = XR.GetApi();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            _logger.LogError(exception, "The OpenXR loader could not be loaded. See native/README.md.");
            Volatile.Write(ref _error, exception.Message);
            SetState(XrConnectionState.LoaderMissing);
            return;
        }

        StillImageTexture? image = null;
        try
        {
            TimeSpan failureDelay = RetryDelay;
            while (!_stopRequested)
            {
                try
                {
                    RunInstance(xr, ref image);
                    failureDelay = RetryDelay;
                }
                catch (OpenXrException exception) when (exception.IsTransient)
                {
                    if (exception.Result == Result.ErrorRuntimeUnavailable)
                    {
                        if (SetState(XrConnectionState.RuntimeUnavailable))
                        {
                            _logger.LogWarning("No OpenXR runtime available ({Result}). Is Varjo Base running? Retrying every {Seconds} s.", exception.Result, RetryDelay.TotalSeconds);
                        }
                    }
                    else
                    {
                        SetState(XrConnectionState.Connecting);
                        _logger.LogWarning("OpenXR connection lost: {Message} Reconnecting.", exception.Message);
                    }

                    failureDelay = RetryDelay;
                }
                catch (Exception exception)
                {
                    // Keep the app alive (N3): report, wait longer each time, try again. Log each new error once.
                    bool newError = Volatile.Read(ref _error) != exception.Message;
                    Volatile.Write(ref _error, exception.Message);
                    if (SetState(XrConnectionState.Failed) || newError)
                    {
                        _logger.LogError(exception, "Headset output failed. Retrying in {Seconds} s.", failureDelay.TotalSeconds);
                    }

                    SetSessionState(XrSessionState.None);
                    Wait(failureDelay);
                    failureDelay = TimeSpan.FromTicks(Math.Min(failureDelay.Ticks * 2, MaxFailureRetryDelay.Ticks));
                    continue;
                }

                SetSessionState(XrSessionState.None);
                Wait(RetryDelay);
            }
        }
        finally
        {
            image?.Dispose();
            xr.Dispose();
            SetState(XrConnectionState.Stopped);
            _logger.LogInformation("XR render thread stopped.");
        }
    }

    /// <summary>One OpenXR instance: waits for a headset, runs sessions until the instance is lost or the app closes.</summary>
    private void RunInstance(XR xr, ref StillImageTexture? image)
    {
        using XrInstance instance = XrInstance.Create(xr, _logger);
        SetState(XrConnectionState.Connecting);
        Volatile.Write(ref _runtime, new XrRuntimeInfo(instance.RuntimeName, instance.RuntimeVersion, string.Empty, 0, 0, string.Empty, _device?.AdapterName ?? string.Empty));

        var events = default(EventDataBuffer);
        while (!_stopRequested)
        {
            if (!instance.TryGetSystem(out XrSystemInfo system))
            {
                if (SetState(XrConnectionState.HeadsetUnavailable))
                {
                    _logger.LogWarning("OpenXR runtime {Runtime} reports no headset. Retrying every {Seconds} s.", instance.RuntimeName, RetryDelay.TotalSeconds);
                }

                if (DrainInstanceEvents(instance, ref events))
                {
                    return;
                }

                Wait(RetryDelay);
                continue;
            }

            GraphicsDevice device = EnsureDevice(system);
            using XrSession session = XrSession.Create(instance, system, device, _logger);
            Volatile.Write(ref _runtime, new XrRuntimeInfo(
                instance.RuntimeName,
                instance.RuntimeVersion,
                system.SystemName,
                session.EyeWidth,
                session.EyeHeight,
                session.SwapchainFormat.ToString(),
                device.AdapterName));
            Volatile.Write(ref _error, null);
            Volatile.Write(ref _seeThroughSupport, (int)(session.SupportsSeeThrough ? SeeThroughSupport.Available : SeeThroughSupport.NotAvailable));
            SetState(XrConnectionState.Connected);
            _logger.LogInformation("Headset {System} connected through {Runtime}.", system.SystemName, instance.RuntimeName);

            XrLifecycleAction outcome;
            try
            {
                outcome = RunSession(instance, session, device, ref image, ref events);
            }
            finally
            {
                // Lets the player free frames of videos it closed while no session runs.
                Volatile.Read(ref _videoSource)?.ReleaseRenderThread();
            }

            if (outcome == XrLifecycleAction.Finish || _stopRequested)
            {
                return;
            }

            // Reconnect: session closed or lost by the runtime. A lost instance surfaces as an exception.
            _logger.LogInformation("OpenXR session ended by the runtime. Reconnecting in {Seconds} s.", RetryDelay.TotalSeconds);
            SetSessionState(XrSessionState.None);
            SetState(XrConnectionState.Connecting);
            Wait(RetryDelay);
        }
    }

    private GraphicsDevice EnsureDevice(in XrSystemInfo system)
    {
        if (_device is null)
        {
            _device = GraphicsDevice.CreateOnAdapter(
                system.AdapterLuid,
                system.MinimumFeatureLevel,
                _loggerFactory.CreateLogger<GraphicsDevice>(),
                _requestDebugLayer);
            _deviceReady.TrySetResult(_device);
            return _device;
        }

        if (_device.AdapterLuid != system.AdapterLuid)
        {
            // Textures and (from M3) the decoder live on the old device; switching GPUs at run time is not supported.
            throw new InvalidOperationException(
                $"The headset now requires a different GPU (LUID 0x{system.AdapterLuid:X}) than at start ({_device.AdapterName}). Restart Orbivo.");
        }

        return _device;
    }

    /// <summary>
    /// Runs the event loop and, while the session is running, the frame loop. Returns <see cref="XrLifecycleAction.Reconnect"/>
    /// or <see cref="XrLifecycleAction.Finish"/>.
    /// </summary>
    private XrLifecycleAction RunSession(
        XrInstance instance,
        XrSession session,
        GraphicsDevice device,
        ref StillImageTexture? image,
        ref EventDataBuffer events)
    {
        var lifecycle = new XrSessionLifecycle();
        using var renderer = new PanoramaRenderer(device);
        long exitDeadline = 0;
        long frame = 0;
        int framesInInterval = 0;
        long intervalStart = Stopwatch.GetTimestamp();

        while (true)
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            bool expectedAllocation = false;

            if (_stopRequested && !lifecycle.ExitRequested)
            {
                if (!lifecycle.IsRunning)
                {
                    return XrLifecycleAction.Finish;
                }

                _logger.LogInformation("Requesting the end of the OpenXR session.");
                lifecycle.RequestExit();
                session.RequestExit();
                exitDeadline = Stopwatch.GetTimestamp() + (long)(ExitGracePeriod.TotalSeconds * Stopwatch.Frequency);
            }

            if (lifecycle.ExitRequested && Stopwatch.GetTimestamp() > exitDeadline)
            {
                _logger.LogWarning("The runtime did not end the session within {Seconds} s; destroying it.", ExitGracePeriod.TotalSeconds);
                return XrLifecycleAction.Finish;
            }

            while (instance.PollEvent(ref events))
            {
                expectedAllocation = true;
                XrLifecycleAction action = HandleEvent(session, lifecycle, ref events);
                if (action is XrLifecycleAction.Reconnect or XrLifecycleAction.Finish)
                {
                    return action;
                }
            }

            if (!lifecycle.IsRunning)
            {
                // IDLE: no frames allowed yet. Sleeping is fine here, no frame is pending.
                Wait(IdlePollInterval);
                continue;
            }

            StillImageTexture? newImage = Interlocked.Exchange(ref _pendingImage, null);
            if (newImage is not null)
            {
                image?.Dispose();
                image = newImage;
                Volatile.Write(ref _hasImage, 1);
                expectedAllocation = true;
                _logger.LogInformation("Showing a {Width}x{Height} image in the headset.", image.Width, image.Height);
            }

            XrFrameResult result = session.RenderFrame(
                renderer,
                Volatile.Read(ref _videoSource),
                image,
                Volatile.Read(ref _liveView),
                Volatile.Read(ref _recenter),
                (IdleView)Volatile.Read(ref _idleView));
            frame++;
            framesInInterval++;
            PublishFrame(result);

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
                _logger.LogWarning("XR frame loop allocated {Bytes} bytes in frame {Frame} (CLAUDE.md rule 1). Reported once.", allocated, frame);
            }
        }
    }

    private unsafe XrLifecycleAction HandleEvent(XrSession session, XrSessionLifecycle lifecycle, ref EventDataBuffer events)
    {
        switch (events.Type)
        {
            case StructureType.EventDataSessionStateChanged:
            {
                EventDataSessionStateChanged changed;
                fixed (EventDataBuffer* pointer = &events)
                {
                    changed = *(EventDataSessionStateChanged*)pointer;
                }

                if (changed.Session.Handle != session.Handle.Handle)
                {
                    return XrLifecycleAction.None;
                }

                XrLifecycleAction action = lifecycle.OnStateChanged(changed.State);
                SetSessionState(ToPublic(changed.State));
                _logger.LogInformation("OpenXR session state {State}.", changed.State);
                switch (action)
                {
                    case XrLifecycleAction.BeginSession:
                        session.Begin();
                        break;
                    case XrLifecycleAction.EndSession:
                        session.End();
                        Volatile.Write(ref _framesPerSecond, 0f);
                        break;
                }

                return action;
            }

            case StructureType.EventDataInstanceLossPending:
                _logger.LogWarning("OpenXR instance loss pending (runtime is shutting down).");
                throw new OpenXrException("xrPollEvent", Result.ErrorInstanceLost);

            case StructureType.EventDataEventsLost:
                _logger.LogWarning("OpenXR event queue overflowed; some events were lost.");
                return XrLifecycleAction.None;

            default:
                return XrLifecycleAction.None;
        }
    }

    /// <summary>Handles events while no session exists. Returns true if the instance is lost.</summary>
    private bool DrainInstanceEvents(XrInstance instance, ref EventDataBuffer events)
    {
        while (instance.PollEvent(ref events))
        {
            if (events.Type == StructureType.EventDataInstanceLossPending)
            {
                _logger.LogWarning("OpenXR instance loss pending (runtime is shutting down).");
                return true;
            }
        }

        return false;
    }

    private void PublishFrame(in XrFrameResult result)
    {
        Volatile.Write(ref _displayRefreshHz, XrConversions.RefreshRateHz(result.PredictedDisplayPeriod));
        Volatile.Write(ref _headTracked, result.HeadTracked ? 1 : 0);
        if (result.HeadTracked)
        {
            Volatile.Read(ref _listener)?.Publish(result.HeadInContent, result.DisplayTimestamp);
            (float yaw, float pitch) = OrientationMath.ToYawPitch(Quaternion.Normalize(result.Head));
            Volatile.Write(ref _headYawDegrees, float.RadiansToDegrees(yaw));
            Volatile.Write(ref _headPitchDegrees, float.RadiansToDegrees(pitch));
        }
    }

    /// <summary>Sets the state; returns true if it changed (used to log a state only once).</summary>
    private bool SetState(XrConnectionState state)
    {
        return Interlocked.Exchange(ref _state, (int)state) != (int)state;
    }

    private void SetSessionState(XrSessionState state)
    {
        Volatile.Write(ref _sessionState, (int)state);
        if (state == XrSessionState.None)
        {
            Volatile.Write(ref _framesPerSecond, 0f);
            Volatile.Write(ref _headTracked, 0);
        }
    }

    /// <summary>
    /// Waits outside the frame loop. Meanwhile it keeps taking video frames that are due, so the decoder keeps pace
    /// with the playback clock even while the headset shows nothing; otherwise the video would have to catch up
    /// by dropping frames once the session runs again.
    /// </summary>
    private void Wait(TimeSpan delay)
    {
        long end = Stopwatch.GetTimestamp() + (long)(delay.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            IVideoFrameSource? video = Volatile.Read(ref _videoSource);
            if (video is not null)
            {
                video.TryGetFrame(Stopwatch.GetTimestamp(), out _);
                video.ReleaseRenderThread();
            }

            TimeSpan remaining = TimeSpan.FromSeconds((end - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
            if (remaining <= TimeSpan.Zero || _stopSignal.Wait(remaining < VideoAdvanceInterval ? remaining : VideoAdvanceInterval))
            {
                return;
            }
        }
    }

    private static XrSessionState ToPublic(SessionState state)
    {
        return state switch
        {
            SessionState.Idle => XrSessionState.Idle,
            SessionState.Ready => XrSessionState.Ready,
            SessionState.Synchronized => XrSessionState.Synchronized,
            SessionState.Visible => XrSessionState.Visible,
            SessionState.Focused => XrSessionState.Focused,
            SessionState.Stopping => XrSessionState.Stopping,
            SessionState.LossPending => XrSessionState.LossPending,
            SessionState.Exiting => XrSessionState.Exiting,
            _ => XrSessionState.None,
        };
    }
}
