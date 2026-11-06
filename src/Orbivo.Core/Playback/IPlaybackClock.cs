namespace Orbivo.Core.Playback;

/// <summary>
/// The master clock of playback (CLAUDE.md rule 4). From M5 the audio clock, until then and without sound
/// the <see cref="SystemPlaybackClock"/>.
/// </summary>
/// <remarks>
/// Media time is in <see cref="TimeSpan"/> ticks (100 ns) from the start of the video.
/// Timestamps are <see cref="System.Diagnostics.Stopwatch"/> timestamps (QueryPerformanceCounter), the same time base
/// OpenXR display times are converted to.
/// </remarks>
public interface IPlaybackClock
{
    /// <summary>
    /// Media time that is shown at <paramref name="timestamp"/>. Called by the render thread every frame:
    /// never blocks, never allocates.
    /// </summary>
    long GetMediaTicks(long timestamp);
}
