using System.Numerics;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Core.Playback;
using Orbivo.Media;
using Orbivo.Xr.Graphics;
using Orbivo.Xr.LiveView;
using Orbivo.Xr.Rendering;
using Silk.NET.OpenXR;
using Vortice.DXGI;
using Vortice.Mathematics;

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
/// <param name="Submitted">True if a layer was submitted.</param>
/// <param name="HeadTracked">True if this frame's head orientation is valid.</param>
/// <param name="TrackedHead">Head orientation as tracked in the LOCAL space.</param>
/// <param name="Head">Head orientation after recenter (F30): identity = looking at the front.</param>
/// <param name="HeadInContent">Head in the video's own frame (recenter and orientation offset), as the renderer looks into it.</param>
/// <param name="DisplayTimestamp">Stopwatch timestamp of the predicted display time.</param>
/// <param name="PredictedDisplayPeriod">Frame period of the headset in nanoseconds.</param>
/// <param name="Content">What the eyes showed, before blackout and fade.</param>
/// <param name="Brightness">Blackout and fade factor (1 = full picture, 0 = black).</param>
/// <param name="VideoPtsTicks">Presentation time of the shown video frame, or <see cref="long.MinValue"/>.</param>
internal readonly record struct XrFrameResult(
    bool Submitted,
    bool HeadTracked,
    Quaternion TrackedHead,
    Quaternion Head,
    Quaternion HeadInContent,
    long DisplayTimestamp,
    long PredictedDisplayPeriod,
    HeadsetContent Content,
    float Brightness,
    long VideoPtsTicks);

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

    private XrSession(XrInstance instance, GraphicsDevice device, Session handle, Space space, XrSwapchain[] swapchains, bool supportsSeeThrough)
    {
        SupportsSeeThrough = supportsSeeThrough;
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

    /// <summary>True if the runtime offers the blend mode <c>ALPHA_BLEND</c> (camera picture of the XR-3, F7).</summary>
    public bool SupportsSeeThrough { get; }

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
            bool seeThrough = SupportsAlphaBlend(instance, system.SystemId, logger);
            return new XrSession(instance, device, handle, space, [.. swapchains], seeThrough);
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
    /// <param name="image">Idle image (F7) with its format, or null.</param>
    /// <param name="liveView">Live view for the operator (F42), or null.</param>
    /// <param name="recenter">Recenter (F30), or null.</param>
    /// <param name="idleView">What to show while no video frame is due (F7).</param>
    public XrFrameResult RenderFrame(
        PanoramaRenderer renderer,
        IVideoFrameSource? video,
        StillImageTexture? image,
        LiveViewFrames? liveView,
        RecenterControl? recenter,
        IdleView idleView)
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
        Quaternion trackedHead = Quaternion.Identity;
        Quaternion correction = Quaternion.Identity;
        OrientationOffset offset = OrientationOffset.Zero;
        HeadsetContent content = HeadsetContent.None;
        float brightness = 1f;
        bool seeThrough = false;
        uint layerCount = 0;
        if (frameState.ShouldRender != 0)
        {
            headTracked = LocateViews(frameState.PredictedDisplayTime);
            if (_hasValidViews)
            {
                trackedHead = Quaternion.Normalize(Quaternion.Slerp(
                    XrConversions.ToQuaternion(_views[0].Pose.Orientation),
                    XrConversions.ToQuaternion(_views[1].Pose.Orientation),
                    0.5f));

                // Recenter turns every view the same way (F31: picture, live view and sound).
                correction = recenter?.Update(trackedHead, headTracked) ?? Quaternion.Identity;
                PanoramaSource source = hasVideoFrame ? PanoramaSource.FromVideo(frame) : PanoramaSource.ForIdle(idleView, image);
                if (source.IsSeeThrough && !SupportsSeeThrough)
                {
                    source = PanoramaSource.Black;
                }

                offset = source.Format.Offset;
                content = source.Content;
                seeThrough = source.IsSeeThrough;

                // Blackout and fade (F5) also cover the idle view; without a player there is nothing to dim.
                brightness = video?.GetOutputBrightness(displayTimestamp) ?? 1f;
                RenderEyes(renderer, source, brightness, correction);
                layerCount = 1;
                if (liveView is { Enabled: true })
                {
                    ViewParameters combined = CombinedView.Create(
                        RecenterControl.Apply(XrConversions.ToViewParameters(_views[0], orientationValid: true, Quaternion.Identity), correction),
                        RecenterControl.Apply(XrConversions.ToViewParameters(_views[1], orientationValid: true, Quaternion.Identity), correction));
                    liveView.RenderIfDue(renderer, _device.ImmediateContext, combined, source, brightness, displayTimestamp);
                }
            }
        }

        // See-through: the compositor blends the layer over the camera picture by its (premultiplied) alpha.
        var layer = new CompositionLayerProjection(
            type: StructureType.CompositionLayerProjection,
            layerFlags: seeThrough ? CompositionLayerFlags.BlendTextureSourceAlphaBit : 0,
            space: Space,
            viewCount: XrInstance.EyeCount,
            views: (CompositionLayerProjectionView*)System.Runtime.CompilerServices.Unsafe.AsPointer(ref _projectionViews[0]));
        var layerHeader = (CompositionLayerBaseHeader*)&layer;
        var endInfo = new FrameEndInfo(
            type: StructureType.FrameEndInfo,
            displayTime: frameState.PredictedDisplayTime,
            environmentBlendMode: seeThrough ? EnvironmentBlendMode.AlphaBlend : EnvironmentBlendMode.Opaque,
            layerCount: layerCount,
            layers: &layerHeader);
        _xr.EndFrame(Handle, &endInfo).Check("xrEndFrame");

        // The sound looks into the video exactly like the eyes do (CLAUDE.md rule 6: rotation only).
        Quaternion head = Quaternion.Normalize(correction * trackedHead);
        return new XrFrameResult(
            layerCount > 0,
            headTracked,
            trackedHead,
            head,
            offset.ApplyToView(head),
            displayTimestamp,
            frameState.PredictedDisplayPeriod,
            content,
            brightness,
            content == HeadsetContent.Video ? frame.PtsTicks : long.MinValue);
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

    /// <summary>Asks the runtime for its environment blend modes and logs them (decision 2026-11-07: write to the log).</summary>
    private static bool SupportsAlphaBlend(XrInstance instance, ulong systemId, ILogger logger)
    {
        XR xr = instance.Xr;
        uint count = 0;
        xr.EnumerateEnvironmentBlendModes(instance.Handle, systemId, XrInstance.ViewConfiguration, 0, &count, (EnvironmentBlendMode*)null)
            .Check("xrEnumerateEnvironmentBlendModes");
        var modes = new EnvironmentBlendMode[count];
        fixed (EnvironmentBlendMode* pointer = modes)
        {
            xr.EnumerateEnvironmentBlendModes(instance.Handle, systemId, XrInstance.ViewConfiguration, count, &count, pointer)
                .Check("xrEnumerateEnvironmentBlendModes");
        }

        bool alphaBlend = modes.AsSpan(0, (int)count).Contains(EnvironmentBlendMode.AlphaBlend);
        logger.LogInformation(
            "Environment blend modes: {Modes}. See-through (camera picture) is {Availability}.",
            string.Join(", ", modes.Take((int)count)),
            alphaBlend ? "available" : "not available");
        return alphaBlend;
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

    private void RenderEyes(PanoramaRenderer renderer, in PanoramaSource source, float brightness, Quaternion correction)
    {
        for (int eye = 0; eye < XrInstance.EyeCount; eye++)
        {
            ref readonly View view = ref _views[eye];
            ViewParameters parameters = RecenterControl.Apply(XrConversions.ToViewParameters(view, orientationValid: true, Quaternion.Identity), correction);

            XrSwapchain swapchain = _swapchains[eye];
            var target = swapchain.AcquireImage();
            if (source.IsSeeThrough)
            {
                // Transparent shows the camera; blackout and fade lay black over it (premultiplied: color 0, alpha = darkness).
                _device.ImmediateContext.ClearRenderTargetView(target, new Color4(0f, 0f, 0f, 1f - Math.Clamp(brightness, 0f, 1f)));
            }
            else
            {
                renderer.Render(_device.ImmediateContext, target, swapchain.Width, swapchain.Height, parameters, (Eye)eye, source, brightness);
            }

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
