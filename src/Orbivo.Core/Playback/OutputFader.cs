namespace Orbivo.Core.Playback;

/// <summary>What the headset output does with the picture right now (F5).</summary>
/// <param name="Level">Perceived brightness now: 0 = black, 1 = full picture.</param>
/// <param name="TargetLevel">Where the output is heading: 0 (black) or 1 (picture).</param>
/// <param name="Fading">True while a fade runs.</param>
public readonly record struct OutputLevel(float Level, float TargetLevel, bool Fading)
{
    public static OutputLevel Picture => new(1f, 1f, false);

    /// <summary>The output is black or heading there (blackout, fade out).</summary>
    public bool TowardsBlack => TargetLevel <= 0f;
}

/// <summary>
/// Blackout and fade of the headset output (F5), independent of the transport state. The render thread reads the
/// brightness for each frame through <see cref="GetBrightness"/>; the player control thread changes it.
/// </summary>
/// <remarks>
/// The fade runs linearly in perceived brightness. The shader multiplies linear light, so the factor is
/// <c>level^2.2</c>: a linear ramp in linear light would look like a sudden jump at the dark end.
/// Each change publishes a new immutable state; reading it is lock-free and does not allocate (CLAUDE.md rule 1).
/// </remarks>
public sealed class OutputFader
{
    /// <summary>Exponent from perceived level to linear-light factor (sRGB-like display gamma).</summary>
    public const float DisplayGamma = 2.2f;

    private readonly TimeProvider _timeProvider;
    private State _state;

    /// <param name="timeProvider">Source of timestamps; <see cref="TimeProvider.System"/> if null (tests pass a fake).</param>
    public OutputFader(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _state = new State(1f, 1f, 0, 0);
    }

    /// <summary>Output level now. Any thread.</summary>
    public OutputLevel Current
    {
        get
        {
            State state = Volatile.Read(ref _state);
            long now = _timeProvider.GetTimestamp();
            float level = state.LevelAt(now);
            return new OutputLevel(level, state.ToLevel, level != state.ToLevel);
        }
    }

    /// <summary>
    /// Factor for the shader at <paramref name="timestamp"/> (a <see cref="System.Diagnostics.Stopwatch"/> timestamp,
    /// in the headset the predicted display time): 0 = black, 1 = full picture. Render thread: never blocks, never allocates.
    /// </summary>
    public float GetBrightness(long timestamp)
    {
        return ToLinear(Volatile.Read(ref _state).LevelAt(timestamp));
    }

    /// <summary>Switches to black or back to the picture at once.</summary>
    public void SetBlackout(bool black)
    {
        float level = black ? 0f : 1f;
        Volatile.Write(ref _state, new State(level, level, _timeProvider.GetTimestamp(), 0));
    }

    /// <summary>
    /// Fades from the current level to black or to the picture. A fade from halfway takes half of
    /// <paramref name="fullDuration"/>, so the speed is always the same.
    /// </summary>
    public void FadeTo(bool black, TimeSpan fullDuration)
    {
        long now = _timeProvider.GetTimestamp();
        float from = Volatile.Read(ref _state).LevelAt(now);
        float to = black ? 0f : 1f;
        double seconds = Math.Max(0, fullDuration.TotalSeconds) * Math.Abs(to - from);
        long durationTicks = (long)(seconds * _timeProvider.TimestampFrequency);
        Volatile.Write(ref _state, new State(from, to, now, durationTicks));
    }

    /// <summary>Perceived level (0–1) to the linear-light factor the shader multiplies with.</summary>
    public static float ToLinear(float level)
    {
        return level <= 0f ? 0f : level >= 1f ? 1f : MathF.Pow(level, DisplayGamma);
    }

    private sealed record State(float FromLevel, float ToLevel, long StartTimestamp, long DurationTimestampTicks)
    {
        public float LevelAt(long timestamp)
        {
            if (DurationTimestampTicks <= 0 || timestamp >= StartTimestamp + DurationTimestampTicks)
            {
                return ToLevel;
            }

            if (timestamp <= StartTimestamp)
            {
                return FromLevel;
            }

            float progress = (float)((double)(timestamp - StartTimestamp) / DurationTimestampTicks);
            return FromLevel + ((ToLevel - FromLevel) * progress);
        }
    }
}
