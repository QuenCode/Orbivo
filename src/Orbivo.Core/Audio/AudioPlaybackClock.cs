using Orbivo.Core.Concurrency;
using Orbivo.Core.Playback;

namespace Orbivo.Core.Audio;

/// <summary>
/// The playback clock as a function of time: a line through an anchor, limited to a range. One value describes a
/// running clock, a clock that stands still, a clock that starts at a known moment (stands at
/// <see cref="MinMediaTicks"/> until then) and one that stops at a known media time (<see cref="MaxMediaTicks"/>).
/// </summary>
/// <param name="AnchorTimestamp">Stopwatch timestamp of the anchor.</param>
/// <param name="AnchorMediaTicks">Media time at the anchor (100 ns ticks).</param>
/// <param name="MediaTicksPerTimestampTick">Speed: media ticks per Stopwatch tick; 0 = standing.</param>
/// <param name="MinMediaTicks">The clock never shows less.</param>
/// <param name="MaxMediaTicks">The clock never shows more.</param>
public readonly record struct ClockSnapshot(
    long AnchorTimestamp,
    long AnchorMediaTicks,
    double MediaTicksPerTimestampTick,
    long MinMediaTicks,
    long MaxMediaTicks)
{
    /// <summary>A clock that stands at <paramref name="mediaTicks"/>.</summary>
    public static ClockSnapshot Standing(long mediaTicks) => new(0, mediaTicks, 0, mediaTicks, mediaTicks);

    /// <summary>Media time at <paramref name="timestamp"/> without the limits.</summary>
    public long Unlimited(long timestamp) =>
        AnchorMediaTicks + (long)((timestamp - AnchorTimestamp) * MediaTicksPerTimestampTick);

    /// <summary>Media time at <paramref name="timestamp"/>.</summary>
    public long At(long timestamp) => Math.Clamp(Unlimited(timestamp), MinMediaTicks, MaxMediaTicks);

    /// <summary>True if the clock moves at <paramref name="timestamp"/> (not held by a limit, not standing).</summary>
    public bool IsAdvancingAt(long timestamp)
    {
        if (MediaTicksPerTimestampTick <= 0)
        {
            return false;
        }

        long value = Unlimited(timestamp);
        return value > MinMediaTicks && value < MaxMediaTicks;
    }
}

/// <summary>
/// The audio clock (CLAUDE.md rule 4): the media time that is heard now. The audio output thread publishes it every
/// period (<see cref="Publish"/>); the render thread reads it every frame. Neither side blocks or allocates.
/// </summary>
public sealed class AudioPlaybackClock : IPlaybackClock
{
    private readonly SnapshotCell<ClockSnapshot> _cell = new(ClockSnapshot.Standing(0));

    /// <summary>The current clock function. Any thread.</summary>
    public ClockSnapshot Snapshot => _cell.Read();

    /// <inheritdoc />
    public long GetMediaTicks(long timestamp) => _cell.Read().At(timestamp);

    /// <summary>Audio output thread only.</summary>
    public void Publish(in ClockSnapshot snapshot) => _cell.Write(snapshot);
}
