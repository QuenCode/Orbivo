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
    string? Message);

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

    /// <summary>Switches to the device <paramref name="deviceId"/>; null = Windows default (follows changes of it).</summary>
    void SelectDevice(string? deviceId);

    /// <summary>Current state. Any thread.</summary>
    AudioOutputStatus GetStatus();
}
