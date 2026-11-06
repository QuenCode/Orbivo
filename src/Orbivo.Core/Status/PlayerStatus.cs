using Orbivo.Core.Audio;
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

/// <summary>Properties of the sound track being played.</summary>
/// <param name="Codec">Codec name, e.g. "aac".</param>
/// <param name="Conversion">How the track reaches the stereo output.</param>
public sealed record AudioTrackInfo(string Codec, AudioConversionPlan Conversion);

/// <summary>Which clock drives the playback (CLAUDE.md rule 4).</summary>
public enum PlaybackClockSource
{
    /// <summary>System timer: the video has no sound, or no sound output is available.</summary>
    System,

    /// <summary>Audio clock: the heard position of the sound output.</summary>
    Audio,
}

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
    /// <summary>True while the player jumps to a new position; the headset holds the last frame meanwhile.</summary>
    public bool Seeking { get; init; }

    /// <summary>True if the current video plays in a loop (F3).</summary>
    public bool Loop { get; init; }

    /// <summary>Blackout and fade of the headset output (F5).</summary>
    public OutputLevel Output { get; init; } = OutputLevel.Picture;

    /// <summary>Nominal duration of one frame, <see cref="TimeSpan.Zero"/> if no video is open. For frame steps.</summary>
    public TimeSpan FrameDuration { get; init; }

    /// <summary>Sound track of the current video, or null if it has none (or no sound output is available).</summary>
    public AudioTrackInfo? Audio { get; init; }

    /// <summary>State of the sound output device, or null if the player runs without sound output.</summary>
    public AudioOutputStatus? AudioOutput { get; init; }

    /// <summary>Clock that drives the playback now.</summary>
    public PlaybackClockSource ClockSource { get; init; }

    /// <summary>Measured A/V deviation of the shown frames (F40, N2).</summary>
    public AvSyncStats AvSync { get; init; }

    public static PlayerStatus Empty { get; } =
        new(PlaybackState.NoMedia, null, TimeSpan.Zero, TimeSpan.Zero, null, 0, 0, 0, 0, null);
}
