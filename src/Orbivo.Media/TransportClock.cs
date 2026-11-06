using System.Diagnostics;
using Orbivo.Core.Audio;
using Orbivo.Core.Playback;
using Orbivo.Core.Status;

namespace Orbivo.Media;

/// <summary>
/// The master clock of the player (CLAUDE.md rule 4): the audio clock while a video with sound plays, the system
/// timer otherwise. The player control thread drives it; the render thread reads it.
/// </summary>
/// <remarks>
/// Transport calls always go to both: the system timer, and the sound output (hold, play, pause), so the sound
/// stops when a video without sound follows one with sound. Only the reading switches.
/// With the audio clock, <see cref="Pause"/> and <see cref="Resume"/> take effect when the sound fades out and in is
/// heard (some 10 ms later); <see cref="Stop"/> holds at once.
/// </remarks>
internal sealed class TransportClock : IPlaybackClock
{
    private readonly SystemPlaybackClock _system = new();
    private readonly IAudioOutput? _audio;
    private volatile bool _useAudio;

    public TransportClock(IAudioOutput? audio)
    {
        _audio = audio;
    }

    public PlaybackClockSource Source => _useAudio ? PlaybackClockSource.Audio : PlaybackClockSource.System;

    /// <summary>Media time now.</summary>
    public long MediaTicks => GetMediaTicks(Stopwatch.GetTimestamp());

    /// <summary>Control thread: the audio clock drives from now on (a video with sound), or the system timer.</summary>
    public void UseAudio(bool useAudio)
    {
        _useAudio = useAudio && _audio is not null;
    }

    /// <inheritdoc />
    public long GetMediaTicks(long timestamp)
    {
        return _useAudio ? _audio!.Clock.GetMediaTicks(timestamp) : _system.GetMediaTicks(timestamp);
    }

    /// <summary>
    /// Render thread: media time at <paramref name="timestamp"/>, and whether the clock moves there (for the A/V
    /// measurement, which skips standing frames). Never blocks, never allocates.
    /// </summary>
    public long GetMediaTicks(long timestamp, out bool advancing)
    {
        if (_useAudio)
        {
            ClockSnapshot snapshot = _audio!.Clock.Snapshot;
            advancing = snapshot.IsAdvancingAt(timestamp);
            return snapshot.At(timestamp);
        }

        advancing = _system.IsRunning;
        return _system.GetMediaTicks(timestamp);
    }

    /// <summary>Stands at <paramref name="mediaTicks"/> at once (open, seek, stop).</summary>
    public void Stop(long mediaTicks = 0)
    {
        _system.Stop(mediaTicks);
        _audio?.Hold(mediaTicks);
    }

    public void Pause()
    {
        _system.Pause();
        if (_useAudio)
        {
            _audio!.Pause();
        }
    }

    public void Resume()
    {
        _system.Resume();
        if (_useAudio)
        {
            _audio!.Play();
        }
    }
}
