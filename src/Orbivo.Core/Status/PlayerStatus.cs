using Orbivo.Core.Formats;
using Orbivo.Core.Playback;

namespace Orbivo.Core.Status;

/// <summary>Properties of the video stream being played.</summary>
/// <param name="Codec">Codec name, e.g. "hevc".</param>
/// <param name="Profile">Codec profile, e.g. "Main 10", or empty.</param>
/// <param name="Width">Decoded width in pixels.</param>
/// <param name="Height">Decoded height in pixels.</param>
/// <param name="FrameRate">Nominal frames per second, 0 if unknown.</param>
/// <param name="Color">Color description used for the conversion.</param>
/// <param name="Layout">Stereo layout used for rendering.</param>
/// <param name="LayoutChosenExplicitly">True if the layout comes from the command line, false if it was guessed from the size.</param>
/// <param name="HardwareDecoding">True for D3D11VA hardware decoding.</param>
public sealed record VideoStreamInfo(
    string Codec,
    string Profile,
    int Width,
    int Height,
    double FrameRate,
    VideoColorFormat Color,
    StereoLayout Layout,
    bool LayoutChosenExplicitly,
    bool HardwareDecoding);

/// <summary>
/// Immutable snapshot of the player for the operator UI (CLAUDE.md rule 3: player → UI only through snapshots).
/// </summary>
/// <param name="State">Transport state.</param>
/// <param name="FilePath">Loaded file, or null.</param>
/// <param name="Position">Current media time.</param>
/// <param name="Duration">Length of the video, <see cref="TimeSpan.Zero"/> if unknown.</param>
/// <param name="Video">Stream properties once the file is open, else null.</param>
/// <param name="DecodedFramesPerSecond">Decoder throughput over the last second; equals the frame rate while the buffer is full.</param>
/// <param name="QueuedFrames">Decoded frames waiting for display.</param>
/// <param name="QueueCapacity">Number of frame slots.</param>
/// <param name="DroppedFrames">Frames decoded but never shown, since the video was opened.</param>
/// <param name="Error">Error text for the operator (German), or null.</param>
public sealed record PlayerStatus(
    PlaybackState State,
    string? FilePath,
    TimeSpan Position,
    TimeSpan Duration,
    VideoStreamInfo? Video,
    double DecodedFramesPerSecond,
    int QueuedFrames,
    int QueueCapacity,
    long DroppedFrames,
    string? Error)
{
    public static PlayerStatus Empty { get; } =
        new(PlaybackState.NoMedia, null, TimeSpan.Zero, TimeSpan.Zero, null, 0, 0, 0, 0, null);
}
