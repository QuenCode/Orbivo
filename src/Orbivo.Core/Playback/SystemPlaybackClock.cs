namespace Orbivo.Core.Playback;

/// <summary>
/// Playback clock driven by the system timer (QueryPerformanceCounter), used while there is no sound (M3, CLAUDE.md rule 4).
/// </summary>
/// <remarks>
/// <see cref="Start"/>, <see cref="Pause"/>, <see cref="Resume"/> and <see cref="Stop"/> belong to the player control thread.
/// Each of them publishes a new immutable state; <see cref="GetMediaTicks"/> reads it lock-free from any thread.
/// </remarks>
public sealed class SystemPlaybackClock : IPlaybackClock
{
    private readonly TimeProvider _timeProvider;
    private readonly double _mediaTicksPerTimestampTick;
    private State _state = new(false, 0, 0);

    /// <param name="timeProvider">Source of timestamps; <see cref="TimeProvider.System"/> if null (tests pass a fake).</param>
    public SystemPlaybackClock(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _mediaTicksPerTimestampTick = (double)TimeSpan.TicksPerSecond / _timeProvider.TimestampFrequency;
    }

    public bool IsRunning => Volatile.Read(ref _state).Running;

    /// <summary>Media time now.</summary>
    public long MediaTicks => GetMediaTicks(_timeProvider.GetTimestamp());

    public long GetMediaTicks(long timestamp)
    {
        State state = Volatile.Read(ref _state);
        if (!state.Running)
        {
            return state.AnchorMediaTicks;
        }

        // Never runs backwards: a display timestamp slightly before the anchor still maps to the anchor.
        long elapsed = Math.Max(0, timestamp - state.AnchorTimestamp);
        return state.AnchorMediaTicks + (long)(elapsed * _mediaTicksPerTimestampTick);
    }

    /// <summary>Runs from <paramref name="mediaTicks"/> on, starting now.</summary>
    public void Start(long mediaTicks)
    {
        Volatile.Write(ref _state, new State(true, _timeProvider.GetTimestamp(), mediaTicks));
    }

    /// <summary>Stands still at the current media time.</summary>
    public void Pause()
    {
        long now = _timeProvider.GetTimestamp();
        Volatile.Write(ref _state, new State(false, now, GetMediaTicks(now)));
    }

    /// <summary>Runs again from the time it stood still at.</summary>
    public void Resume()
    {
        State state = Volatile.Read(ref _state);
        if (!state.Running)
        {
            Start(state.AnchorMediaTicks);
        }
    }

    /// <summary>Stands still at <paramref name="mediaTicks"/>.</summary>
    public void Stop(long mediaTicks = 0)
    {
        Volatile.Write(ref _state, new State(false, _timeProvider.GetTimestamp(), mediaTicks));
    }

    private sealed record State(bool Running, long AnchorTimestamp, long AnchorMediaTicks);
}
