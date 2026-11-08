using Orbivo.Core.Geometry;

namespace Orbivo.Xr.OpenXr;

/// <summary>Connection to the headset as the operator UI shows it.</summary>
public enum XrConnectionState
{
    /// <summary>The render thread is starting.</summary>
    Starting,

    /// <summary>openxr_loader.dll could not be loaded. No retry; see native/README.md.</summary>
    LoaderMissing,

    /// <summary>No OpenXR runtime reachable (Varjo Base not running or not installed). Retrying.</summary>
    RuntimeUnavailable,

    /// <summary>Runtime is there, but no headset (unplugged or switched off). Retrying.</summary>
    HeadsetUnavailable,

    /// <summary>Runtime answered; looking for the headset or creating the session.</summary>
    Connecting,

    /// <summary>A session exists; <see cref="XrStatus.SessionState"/> tells how far it is.</summary>
    Connected,

    /// <summary>Unexpected error; details in <see cref="XrStatus.Error"/> and the log. Retrying after a longer pause.</summary>
    Failed,

    /// <summary>The render thread has ended (app is closing).</summary>
    Stopped,
}

/// <summary>OpenXR session state (<c>XrSessionState</c>), so the UI does not depend on Silk.NET.</summary>
public enum XrSessionState
{
    None,
    Idle,
    Ready,
    Synchronized,
    Visible,
    Focused,
    Stopping,
    LossPending,
    Exiting,
}

/// <summary>Details of the connected runtime and headset; changes only when a connection is made.</summary>
/// <param name="RuntimeName">Runtime name, e.g. "Varjo OpenXR Runtime".</param>
/// <param name="RuntimeVersion">Runtime version as text.</param>
/// <param name="SystemName">Headset name reported by the runtime, empty until a headset is found.</param>
/// <param name="EyeWidth">Swapchain width per eye in pixels (recommended size), 0 until a session exists.</param>
/// <param name="EyeHeight">Swapchain height per eye in pixels.</param>
/// <param name="SwapchainFormat">DXGI format of the swapchains.</param>
/// <param name="AdapterName">GPU the runtime requires.</param>
public sealed record XrRuntimeInfo(
    string RuntimeName,
    string RuntimeVersion,
    string SystemName,
    int EyeWidth,
    int EyeHeight,
    string SwapchainFormat,
    string AdapterName);

/// <summary>Snapshot of the headset output for the operator UI (F40, F41).</summary>
/// <param name="State">Connection state.</param>
/// <param name="SessionState">OpenXR session state while <see cref="XrConnectionState.Connected"/>.</param>
/// <param name="Runtime">Runtime and headset details, null before the runtime answered.</param>
/// <param name="FramesPerSecond">Submitted frames per second, averaged over about one second.</param>
/// <param name="DisplayRefreshHz">Refresh rate of the headset from the predicted display period.</param>
/// <param name="HeadTracked">True if the last frame had a valid head orientation.</param>
/// <param name="HeadYawDegrees">Head yaw of the viewer relative to the front after recenter, positive = left (docs/DECISIONS.md).</param>
/// <param name="HeadPitchDegrees">Head pitch of the viewer, positive = up.</param>
/// <param name="HasImage">True once an image is shown in the headset.</param>
/// <param name="RenderLoopAllocates">True if the frame loop allocated after warm-up (CLAUDE.md rule 1, details in the log).</param>
/// <param name="Error">Last error text (English, from the exception) for <see cref="XrConnectionState.Failed"/>.</param>
/// <param name="Recenter">Recenter state (F30).</param>
/// <param name="SeeThrough">Whether the runtime offers the camera picture (F7); known once a session was created.</param>
/// <param name="StateSinceUtc">When <paramref name="State"/> began (UTC).</param>
/// <param name="WasConnected">True once a headset was connected in this run: a later loss is a disconnect (N3).</param>
public readonly record struct XrStatus(
    XrConnectionState State,
    XrSessionState SessionState,
    XrRuntimeInfo? Runtime,
    float FramesPerSecond,
    float DisplayRefreshHz,
    bool HeadTracked,
    float HeadYawDegrees,
    float HeadPitchDegrees,
    bool HasImage,
    bool RenderLoopAllocates,
    string? Error,
    RecenterStatus Recenter = default,
    SeeThroughSupport SeeThrough = SeeThroughSupport.Unknown,
    DateTime StateSinceUtc = default,
    bool WasConnected = false);

/// <summary>Whether the runtime offers the blend mode <c>ALPHA_BLEND</c> for the camera picture (see-through, F7).</summary>
public enum SeeThroughSupport
{
    /// <summary>No session was created yet.</summary>
    Unknown,

    Available,

    /// <summary>The headset shows black instead of the camera picture.</summary>
    NotAvailable,
}
