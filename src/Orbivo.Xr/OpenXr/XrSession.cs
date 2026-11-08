using System.Numerics;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Media;
using Orbivo.Xr.Graphics;
using Orbivo.Xr.LiveView;
using Orbivo.Xr.Rendering;
using Silk.NET.OpenXR;
using Vortice.DXGI;

namespace Orbivo.Xr.OpenXr;

/// <summary>Result of one pass through <see cref="XrSession.RenderFrame"/>.</summary>
/// <param name="Submitted">True if a projection layer was submitted (the headset shows our image).</param>
/// <param name="HeadTracked">True if the runtime delivered a valid head orientation for this frame.</param>
/// <param name="Head">Head orientation (average of both eyes), only meaningful if <paramref name="HeadTracked"/>.</param>
/// <param name="PredictedDisplayPeriod">Display period in nanoseconds.</param>
/// <param name="Submitted">A layer was submitted.</param>
/// <param name="HeadTracked">The views had a valid orientation.</param>
/// <param name="Head">Head orientation in the world (between the eyes).</param>
/// <param name="HeadInContent">Head orientation in the shown picture's frame (orientation offset applied), for the sound.</param>
/// <param name="DisplayTimestamp">Predicted display time as Stopwatch timestamp.</param>
/// <param name="PredictedDisplayPeriod">Frame period in nanoseconds.</param>
internal readonly record struct XrFrameResult(bool Submitted, bool HeadTracked, Quaternion Head, Quaternion HeadInContent, long DisplayTimestamp, long PredictedDisplayPeriod);

/// <summary>
/// A session with its LOCAL reference space and one swapchain per eye (docs/ARCHITECTURE.md, "OpenXR-Lebenszyklus").
/// Owned by the XR render thread.
/// </summary>
internal sealed unsafe class XrSession : IDisposable
{
    private readonly XR _xr;
    private readonly XrInstance _instance;
    private readonly GraphicsDevice _device;
    private readonly XrSwapchain[] _swapchains;

    // Pinned so the frame loop can pass pointers without fixed blocks or allocations.
    private readonly View[] _views = GC.AllocateArray<View>(XrInstance.EyeCount, pinned: true);
    private readonly CompositionLayerProjectionView[] _projectionViews =
        GC.AllocateArray<CompositionLayerProjectionView>(XrInstance.EyeCount, pinned: true);

    private bool _hasValidViews;
    private bool _disposed;

    private XrSession(XrInstance instance, GraphicsDevice device, Session handle, Space space, XrSwapchain[] swapchains)
    {
        _xr = instance.Xr;
        _instance = instance;
        _device = device;
        Handle = handle;
        Space = space;
        _swapchains = swapchains;
    }

    public Session Handle { get; }

    public Space Space { get; }

    public Format SwapchainFormat => _swapchains[0].Format;

    public int EyeWidth => _swapchains[0].Width;

    public int EyeHeight => _swapchains[0].Height;

    public static XrSession Create(XrInstance instance, in XrSystemInfo system, GraphicsDevice device, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(logger);
        XR xr = instance.Xr;

        var binding = new GraphicsBindingD3D11KHR(type: StructureType.GraphicsBindingD3D11Khr, device: (void*)device.Device.NativePointer);
        var createInfo = new SessionCreateInfo(type: StructureType.SessionCreateInfo, next: &binding, systemId: system.SystemId);
        Session handle = default;
        xr.CreateSession(instance.Handle, &createInfo, &handle).Check("xrCreateSession");

        Space space = default;
        var swapchains = new List<XrSwapchain>();
        try
        {
            // Identity pose: LOCAL is the space the runtime set up at start (or at the last recenter in Varjo Base).
            var identity = new Posef(new Quaternionf(0f, 0f, 0f, 1f), new Vector3f(0f, 0f, 0f));
            var spaceInfo = new ReferenceSpaceCreateInfo(
                type: StructureType.ReferenceSpaceCreateInfo,
                referenceSpaceType: ReferenceSpaceType.Local,
                poseInReferenceSpace: identity);
            xr.CreateReferenceSpace(handle, &spaceInfo, &space).Check("xrCreateReferenceSpace");

            Format format = ChooseFormat(xr, handle, logger);
            for (int eye = 0; eye < XrInstance.EyeCount; eye++)
            {
                swapchains.Add(XrSwapchain.Create(xr, handle, device, format, system.EyeWidth, system.EyeHeight));
            }

            logger.LogInformation(
                "OpenXR session created: {Width}x{Height} per eye, {Format}, {Images} swapchain images per eye.",
                system.EyeWidth,
                system.EyeHeight,
                format,
                swapchains[0].ImageCount);
            return new XrSession(instance, device, handle, space, [.. swapchains]);
        }
        catch
        {
            foreach (XrSwapchain swapchain in swapchains)
            {
                swapchain.Dispose();
            }

            if (space.Handle != 0)
            {
                xr.DestroySpace(space);
            }

            xr.DestroySession(handle);
            throw;
        }
    }

    public void Begin()
    {
        var beginInfo = new SessionBeginInfo(
            type: StructureType.SessionBeginInfo,
            primaryViewConfigurationType: XrInstance.ViewConfiguration);
        _xr.BeginSession(Handle, &beginInfo).Check("xrBeginSession");
    }

    public void End()
    {
        _xr.EndSession(Handle).Check("xrEndSession");
    }

    public void RequestExit()
    {
        _xr.RequestExitSession(Handle).Check("xrRequestExitSession");
    }

    /// <summary>
    /// One frame: wait for the frame slot, locate the eyes, render both and submit them.
    /// Blocks only in <c>xrWaitFrame</c> and <c>xrWaitSwapchainImage</c> (the frame clock, like VSync).
    /// Does not allocate (CLAUDE.md rule 1).
    /// </summary>
    /// <param name="renderer">Draws the panorama.</param>
    /// <param name="video">Video frames; a due frame wins over <paramref name="image"/>. Null without a player.</param>
    /// <param name="image">Image to show if there is no video frame, or null for black.</param>
    /// <param name="imageFormat">Format of <paramref name="image"/>.</param>
    /// <param name="liveView">Live view for the operator (F42), or null.</param>
    public XrFrameResult RenderFrame(
        PanoramaRenderer renderer,
        IVideoFrameSource? video,
        StillImageTexture? image,
        VideoFormat imageFormat,
        LiveViewFrames? liveView)
    {
        var waitInfo = new FrameWaitInfo(type: StructureType.FrameWaitInfo);
        var frameState = new FrameState(type: StructureType.FrameState);
        _xr.WaitFrame(Handle, &waitInfo, &frameState).Check("xrWaitFrame");

        var beginInfo = new FrameBeginInfo(type: StructureType.FrameBeginInfo);
        _xr.BeginFrame(Handle, &beginInfo).Check("xrBeginFrame");

        // The frame is chosen for the moment the headset shows it, not for now (docs/ARCHITECTURE.md, "Synchronisation").
        // Taken even when nothing is rendered, so the decoder keeps pace with the playback clock.
        long displayTimestamp = _instance.ToStopwatchTimestamp(frameState.PredictedDisplayTime);
        VideoFrame frame = default;
        bool hasVideoFrame = video is not null && video.TryGetFrame(displayTimestamp, out frame);

        bool headTracked = false;
        Quaternion head = Quaternion.Identity;
        OrientationOffset offset = OrientationOffset.Zero;
        uint layerCount = 0;
        if (frameState.ShouldRender != 0)
        {
            headTracked = LocateViews(frameState.PredictedDisplayTime);
            if (_hasValidViews)
            {
                PanoramaSource source = hasVideoFrame ? PanoramaSource.FromVideo(frame) : PanoramaSource.FromImage(image, imageFormat);
                offset = source.Format.Offset;
                // Blackout and fade (F5) also cover the still image; without a player there is nothing to dim.
                float brightness = video?.GetOutputBrightness(displayTimestamp) ?? 1f;
                RenderEyes(renderer, source, brightness);
                layerCount = 1;
                if (liveView is { Enabled: true })
                {
                    ViewParameters combined = CombinedView.Create(
                        XrConversions.ToViewParameters(_views[0], orientationValid: true, Quaternion.Identity),
                        XrConversions.ToViewParameters(_views[1], orientationValid: true, Quaternion.Identity));
                    liveView.RenderIfDue(renderer, _device.ImmediateContext, combined, source, brightness, displayTimestamp);
                }

                head = Quaternion.Slerp(
                    XrConversions.ToQuaternion(_views[0].Pose.Orientation),
                    XrConversions.ToQuaternion(_views[1].Pose.Orientation),
                    0.5f);
            }
        }

        var layer = new CompositionLayerProjection(
            type: StructureType.CompositionLayerProjection,
            space: Space,
            viewCount: XrInstance.EyeCount,
            views: (CompositionLayerProjectionView*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref _projectionViews[0]));
        var layerHeader = (CompositionLayerBaseHeader*)&layer;
        var endInfo = new FrameEndInfo(
            type: StructureType.FrameEndInfo,
            displayTime: frameState.PredictedDisplayTime,
            environmentBlendMode: EnvironmentBlendMode.Opaque,
            layerCount: layerCount,
            layers: &layerHeader);
        _xr.EndFrame(Handle, &endInfo).Check("xrEndFrame");

        // The sound looks into the video exactly like the eyes do (CLAUDE.md rule 6: rotation only).
        return new XrFrameResult(layerCount > 0, headTracked, head, offset.ApplyToView(head), displayTimestamp, frameState.PredictedDisplayPeriod);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (XrSwapchain swapchain in _swapchains)
        {
            swapchain.Dispose();
        }

        _xr.DestroySpace(Space);
        _xr.DestroySession(Handle);
    }

    private static Format ChooseFormat(XR xr, Session session, ILogger logger)
    {
        uint count = 0;
        xr.EnumerateSwapchainFormats(session, 0, &count, (long*)null).Check("xrEnumerateSwapchainFormats");
        var formats = new long[count];
        fixed (long* pointer = formats)
        {
            xr.EnumerateSwapchainFormats(session, count, &count, pointer).Check("xrEnumerateSwapchainFormats");
        }

        Format chosen = XrConversions.ChooseSwapchainFormat(formats.AsSpan(0, (int)count));
        if (chosen == Format.Unknown)
        {
            throw new OpenXrException(
                $"The runtime offers no sRGB swapchain format Orbivo can use. Offered: {string.Join(", ", formats.Select(f => (Format)f))}.");
        }

        logger.LogInformation("Swapchain format {Format} (runtime offers {Formats}).", chosen, string.Join(", ", formats.Select(f => (Format)f)));
        return chosen;
    }

    /// <summary>
    /// Locates both eyes. Keeps the last valid views if the orientation is not valid in this frame,
    /// so a short tracking loss does not blank the headset. Returns true if this frame's orientation is valid.
    /// </summary>
    private bool LocateViews(long displayTime)
    {
        var locateInfo = new ViewLocateInfo(
            type: StructureType.ViewLocateInfo,
            viewConfigurationType: XrInstance.ViewConfiguration,
            displayTime: displayTime,
            space: Space);
        var viewState = new ViewState(type: StructureType.ViewState);
        View* located = stackalloc View[XrInstance.EyeCount];
        for (int eye = 0; eye < XrInstance.EyeCount; eye++)
        {
            located[eye] = new View(type: StructureType.View);
        }

        uint count = 0;
        _xr.LocateView(Handle, &locateInfo, &viewState, XrInstance.EyeCount, &count, located).Check("xrLocateViews");

        bool valid = count == XrInstance.EyeCount && (viewState.ViewStateFlags & ViewStateFlags.OrientationValidBit) != 0;
        if (valid)
        {
            for (int eye = 0; eye < XrInstance.EyeCount; eye++)
            {
                _views[eye] = located[eye];
            }

            _hasValidViews = true;
        }

        return valid;
    }

    private void RenderEyes(PanoramaRenderer renderer, in PanoramaSource source, float brightness)
    {
        for (int eye = 0; eye < XrInstance.EyeCount; eye++)
        {
            ref readonly View view = ref _views[eye];
            ViewParameters parameters = XrConversions.ToViewParameters(view, orientationValid: true, Quaternion.Identity);

            XrSwapchain swapchain = _swapchains[eye];
            var target = swapchain.AcquireImage();
            renderer.Render(_device.ImmediateContext, target, swapchain.Width, swapchain.Height, parameters, (Eye)eye, source, brightness);
            swapchain.ReleaseImage();

            // The submitted pose is the located one: the compositor reprojects from it. Content is at infinity,
            // so the rendered image depends on the orientation only (CLAUDE.md rule 6).
            _projectionViews[eye] = new CompositionLayerProjectionView(
                type: StructureType.CompositionLayerProjectionView,
                pose: view.Pose,
                fov: view.Fov,
                subImage: new SwapchainSubImage(
                    swapchain: swapchain.Handle,
                    imageRect: new Rect2Di(new Offset2Di(0, 0), new Extent2Di(swapchain.Width, swapchain.Height)),
                    imageArrayIndex: 0));
        }
    }
}
