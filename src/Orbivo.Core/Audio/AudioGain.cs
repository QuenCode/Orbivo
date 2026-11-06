namespace Orbivo.Core.Audio;

/// <summary>
/// Loudness curve for the volume slider and for fades (F4, F5): a level from 0 to 1 becomes an amplitude factor.
/// </summary>
/// <remarks>
/// The factor is <c>level³</c>. Loudness is heard roughly logarithmically; with a linear factor the slider would do
/// almost nothing in its upper half and everything at the bottom. With the cube, 50 % is about −18 dB and 20 % about
/// −42 dB, close to how volume controls of media players feel. A fade linear in level then also sounds even.
/// </remarks>
public static class AudioGain
{
    /// <summary>Amplitude factor for <paramref name="level"/> (clamped to 0–1).</summary>
    public static float FromLevel(float level)
    {
        float clamped = Math.Clamp(level, 0f, 1f);
        return clamped * clamped * clamped;
    }

    /// <summary>The factor in decibels; −∞ for 0.</summary>
    public static double ToDecibels(float gain) => gain <= 0 ? double.NegativeInfinity : 20 * Math.Log10(gain);
}

/// <summary>
/// Smooths gain changes (volume, mute, blackout) sample by sample, so a jump of the target never clicks.
/// One-pole low-pass with a time constant of <see cref="TimeConstantSeconds"/>. Audio thread; no allocations.
/// </summary>
public struct GainSmoother
{
    public const double TimeConstantSeconds = 0.005;

    private readonly float _coefficient;

    public GainSmoother(int sampleRate, float initial)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        _coefficient = (float)(1 - Math.Exp(-1.0 / (TimeConstantSeconds * sampleRate)));
        Value = initial;
    }

    /// <summary>Gain of the last sample.</summary>
    public float Value { get; private set; }

    /// <summary>Gain for the next sample on the way to <paramref name="target"/>.</summary>
    public float Next(float target)
    {
        float value = Value + ((target - Value) * _coefficient);

        // Settle exactly (the rest is below −80 dB), so silence is really silent and full scale really unchanged.
        if (Math.Abs(target - value) < 1e-4f)
        {
            value = target;
        }

        Value = value;
        return value;
    }
}

/// <summary>
/// Fade of the transport (play, pause, seek, stop) over <see cref="DurationSeconds"/>: the sound never starts or stops
/// mid-wave, which would click. The gain follows a smoothstep curve (no corners at either end). Audio thread.
/// </summary>
public struct TransportRamp
{
    public const double DurationSeconds = 0.005;

    private readonly float _step;
    private int _position;

    public TransportRamp(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        Frames = Math.Max(1, (int)Math.Round(DurationSeconds * sampleRate));
        _step = 1f / Frames;
    }

    /// <summary>Length of a full ramp in frames.</summary>
    public int Frames { get; }

    /// <summary>True: heading to full sound. False: heading to silence.</summary>
    public bool Up { get; private set; }

    /// <summary>Completely silent.</summary>
    public readonly bool IsSilent => _position <= 0;

    /// <summary>At full sound.</summary>
    public readonly bool IsOpen => _position >= Frames;

    /// <summary>Frames left until the ramp reaches its end.</summary>
    public readonly int RemainingFrames => Up ? Frames - _position : _position;

    public void FadeIn() => Up = true;

    public void FadeOut() => Up = false;

    /// <summary>Jumps to silence (only while nothing is heard anyway).</summary>
    public void Close()
    {
        Up = false;
        _position = 0;
    }

    /// <summary>Gain for the next frame.</summary>
    public float Next()
    {
        _position = Up ? Math.Min(Frames, _position + 1) : Math.Max(0, _position - 1);
        float x = _position * _step;
        return x * x * (3f - (2f * x));
    }
}
