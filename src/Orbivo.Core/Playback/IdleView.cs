namespace Orbivo.Core.Playback;

/// <summary>
/// What the headset shows while no video runs (F7): before Play, after Stop, without a video, or when the video failed.
/// Pause and the end of a video keep showing the video's frame. Blackout and fade (F5) apply on top; blackout stays black.
/// </summary>
public enum IdleView
{
    /// <summary>Black (as before M8, the default).</summary>
    Black,

    /// <summary>
    /// The camera picture of the XR-3 (video see-through, OpenXR blend mode <c>ALPHA_BLEND</c>). Black if the runtime
    /// does not offer it. The preview and the live view show a dark stand-in instead.
    /// </summary>
    SeeThrough,

    /// <summary>A still image the operator chose (equirect 360, mono, top-bottom or side-by-side).</summary>
    Image,
}
