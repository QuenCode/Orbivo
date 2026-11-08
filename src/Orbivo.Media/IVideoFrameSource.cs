using Orbivo.Core.Formats;
using Orbivo.Core.Playback;
using Vortice.Direct3D11;

namespace Orbivo.Media;

/// <summary>
/// One decoded video frame on the GPU, as the render thread binds it (docs/ARCHITECTURE.md, "Datenfluss Video").
/// Valid until the next call of <see cref="IVideoFrameSource.TryGetFrame"/>.
/// </summary>
/// <param name="Luma">Y plane (R8 or R16 view of the NV12/P010 texture).</param>
/// <param name="Chroma">Interleaved Cb/Cr plane (R8G8 or R16G16 view).</param>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="Format">Projection, stereo layout and orientation offset the frame is shown with (may change between frames).</param>
/// <param name="Color">YUV → linear RGB constants for the shader.</param>
/// <param name="PtsTicks">Presentation time of the frame (100 ns ticks from the start of the video).</param>
public readonly record struct VideoFrame(
    ID3D11ShaderResourceView Luma,
    ID3D11ShaderResourceView Chroma,
    int Width,
    int Height,
    VideoFormat Format,
    VideoColorConstants Color,
    long PtsTicks);

/// <summary>
/// Supplies the video frame to show. Implemented by the player, called by the render thread (headset or preview window).
/// </summary>
public interface IVideoFrameSource
{
    /// <summary>
    /// The frame to show at <paramref name="displayTimestamp"/> (a <see cref="System.Diagnostics.Stopwatch"/> timestamp,
    /// in the headset the predicted display time). Returns false if black should be shown.
    /// Render thread only: never blocks, never allocates (CLAUDE.md rule 1).
    /// </summary>
    bool TryGetFrame(long displayTimestamp, out VideoFrame frame);

    /// <summary>
    /// Brightness of the headset output at <paramref name="displayTimestamp"/> for blackout and fade (F5):
    /// 0 = black, 1 = full picture, as a linear-light factor for the shader. Applies to video and still image.
    /// Render thread only: never blocks, never allocates.
    /// </summary>
    float GetOutputBrightness(long displayTimestamp);

    /// <summary>Transport state for the head log (F50). Any thread; never blocks, never allocates.</summary>
    PlaybackState State { get; }

    /// <summary>Full path of the loaded video, or null, for the head log (F50). Any thread; never blocks, never allocates.</summary>
    string? FilePath { get; }

    /// <summary>
    /// The render thread stops rendering for a while (headset session ended). Lets the player free GPU memory
    /// of frames it no longer needs. Render thread only.
    /// </summary>
    void ReleaseRenderThread();
}
