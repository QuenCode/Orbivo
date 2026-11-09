using Orbivo.Core.Playback;

namespace Orbivo.Core.Audio;

/// <summary>State of the sound output device as the operator sees it.</summary>
public enum AudioEndpointState
{
    /// <summary>The device is being opened.</summary>
    Starting,

    /// <summary>The device plays (silence while no video sounds).</summary>
    Active,

    /// <summary>
    /// The selected device is gone or cannot be opened. Playback continues on the system timer without sound;
    /// the device is tried again every few seconds.
    /// </summary>
    Unavailable,
}

/// <summary>State of the sound fixed to the picture (M7) for the operator UI.</summary>
/// <param name="Available">Steam Audio is loaded and ready for the device rate.</param>
/// <param name="Active">The sound is fixed to the picture right now.</param>
/// <param name="Placement">What the player asked for (from the video's sidecar).</param>
/// <param name="PeakLoad">Highest share of real time one block took to compute in the last second (0.05 = 5 %).</param>
/// <param name="Problem">Why it is not available (German), or null.</param>
public sealed record SpatialAudioStatus(bool Available, bool Active, StereoPlacement Placement, double PeakLoad, string? Problem)
{
    /// <summary>An Ambisonics field plays and is rendered binaurally with Steam Audio (M10).</summary>
    public bool FieldBinaural { get; init; }

    /// <summary>German text if Ambisonics cannot be rendered binaurally (it then plays through a fallback pair), or null.</summary>
    public string? AmbisonicsProblem { get; init; }
}

/// <summary>Snapshot of the sound output for the operator UI.</summary>
/// <param name="State">Device state.</param>
/// <param name="DeviceId">Selected device, null = Windows default.</param>
/// <param name="DeviceName">Name of the device in use or of the selected one.</param>
/// <param name="SampleRate">Sample rate of the device stream (also of the decoded sound), 0 if unknown.</param>
/// <param name="DeviceChannels">Channels of the device mix format.</param>
/// <param name="LatencyMilliseconds">Time from writing a frame until it is heard, as measured.</param>
/// <param name="UnderrunFrames">Frames of silence played because decoded sound was missing (dropouts).</param>
/// <param name="DriftPpm">Drift of the device clock against the system timer.</param>
/// <param name="Volume">Volume level 0–1 (before the curve).</param>
/// <param name="Muted">Mute switch.</param>
/// <param name="Message">German text for the operator (reason when unavailable), or null.</param>
public sealed record AudioOutputStatus(
    AudioEndpointState State,
    string? DeviceId,
    string DeviceName,
    int SampleRate,
    int DeviceChannels,
    double LatencyMilliseconds,
    long UnderrunFrames,
    double DriftPpm,
    float Volume,
    bool Muted,
    string? Message)
{
    /// <summary>Sound fixed to the picture (M7), or null if the output does not support it.</summary>
    public SpatialAudioStatus? Spatial { get; init; }
}

/// <summary>
/// The sound output as the player sees it (implemented in Orbivo.Audio). The player control thread calls the
/// transport methods; the audio output thread carries them out at its next period with short fades, so nothing clicks.
/// </summary>
public interface IAudioOutput
{
    /// <summary>The audio clock: the media time that is heard. Master clock while a video with sound plays.</summary>
    AudioPlaybackClock Clock { get; }

    /// <summary>Decoded sound goes here (producer: the audio decode thread).</summary>
    AudioRingBuffer Ring { get; }

    /// <summary>Sample rate the decoder must deliver: the rate of the device stream.</summary>
    int SampleRate { get; }

    /// <summary>
    /// Fades out (if sound plays) and holds the clock at <paramref name="mediaTicks"/> at once. Reading continues at
    /// that media time with <see cref="Play"/>. Used for opening, seeking, stopping.
    /// </summary>
    void Hold(long mediaTicks);

    /// <summary>Starts the sound at the media time the clock stands at, with a fade-in. The clock starts with the sound.</summary>
    void Play();

    /// <summary>Fades out; the clock stops at the last heard media time.</summary>
    void Pause();

    /// <summary>Volume level 0–1 (F4).</summary>
    void SetVolume(float level);

    /// <summary>Mute (F4).</summary>
    void SetMuted(bool muted);

    /// <summary>Blackout and fade of the picture also apply to the sound (decision M5).</summary>
    void SetFader(OutputFader? fader);

    /// <summary>
    /// How the stereo of the current video is heard (M7): fixed to the picture or plain. Applies at the next period; a
    /// switch while sound plays dips through a few milliseconds of silence.
    /// </summary>
    void SetStereoPlacement(StereoPlacement placement);

    /// <summary>Switches to the device <paramref name="deviceId"/>; null = Windows default (follows changes of it).</summary>
    void SelectDevice(string? deviceId);

    /// <summary>Current state. Any thread.</summary>
    AudioOutputStatus GetStatus();
}
