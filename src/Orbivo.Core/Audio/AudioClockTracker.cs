namespace Orbivo.Core.Audio;

/// <summary>
/// Turns the position of the audio device into a smooth playback clock (docs/ARCHITECTURE.md, "Synchronisation").
/// Runs on the audio output thread, which publishes <see cref="Current"/> through <see cref="AudioPlaybackClock"/>.
/// No I/O, no allocations, no locks; deterministic, so it is tested with simulated devices.
/// </summary>
/// <remarks>
/// Frames written to the device are numbered from the start of the device stream ("stream frames"). A playing segment
/// maps stream frames to media time: <see cref="BeginSegment"/> says which stream frame starts at which media time.
/// The device reports which stream frame is heard at which timestamp (<see cref="Update"/>).
/// <para>
/// The clock follows that position with a PI controller: the proportional part corrects an error within about
/// <see cref="CorrectionTimeSeconds"/>, the integral part learns the drift of the device clock against the system timer
/// (typically 10–300 ppm). Jitter in the reported positions therefore does not reach the frame selection. An error
/// larger than <see cref="ResyncThresholdTicks"/> re-anchors the clock at once.
/// </para>
/// Before the first frame of a segment is heard, the clock stands at the segment's media time; after the end frame of
/// a segment (pause) it stands at the end. Both are limits of the published <see cref="ClockSnapshot"/>, so the clock
/// starts and stops exactly when the sound does, not when the audio thread handles it.
/// </remarks>
public sealed class AudioClockTracker
{
    /// <summary>Time constant of the proportional correction.</summary>
    public const double CorrectionTimeSeconds = 0.5;

    /// <summary>Time constant of the drift estimate (critically damped with the correction time).</summary>
    public const double DriftTimeSeconds = 1.0;

    /// <summary>Averaging time of <see cref="DriftPpm"/>.</summary>
    public const double DriftDisplayTimeSeconds = 30.0;

    /// <summary>Errors larger than this re-anchor the clock (30 ms).</summary>
    public const long ResyncThresholdTicks = 30 * TimeSpan.TicksPerMillisecond;

    private const double MaxDrift = 0.005;
    private const double MaxCorrection = 0.01;

    private readonly double _nominalRate;
    private readonly double _mediaTicksPerFrame;
    private readonly double _timestampFrequency;

    private bool _inSegment;
    private long _segmentStartFrame;
    private long _segmentStartMediaTicks;
    private long _segmentEndFrame = long.MaxValue;
    private bool _locked;
    private double _drift;
    private double _driftAverage;
    private long _lastUpdateTimestamp;

    /// <param name="sampleRate">Frames per second of the device stream.</param>
    /// <param name="timestampFrequency">Stopwatch ticks per second.</param>
    public AudioClockTracker(int sampleRate, long timestampFrequency)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(timestampFrequency, 1);
        SampleRate = sampleRate;
        _timestampFrequency = timestampFrequency;
        _nominalRate = (double)TimeSpan.TicksPerSecond / timestampFrequency;
        _mediaTicksPerFrame = (double)TimeSpan.TicksPerSecond / sampleRate;
        Current = ClockSnapshot.Standing(0);
    }

    public int SampleRate { get; }

    /// <summary>The clock function to publish.</summary>
    public ClockSnapshot Current { get; private set; }

    /// <summary>
    /// Estimated drift of the device clock against the system timer, in parts per million, averaged over about
    /// <see cref="DriftDisplayTimeSeconds"/> (the controller's own estimate moves with the jitter).
    /// </summary>
    public double DriftPpm => _driftAverage * 1e6;

    /// <summary>Last measured difference between the heard position and the clock (heard − clock).</summary>
    public long LastErrorTicks { get; private set; }

    /// <summary>Number of times the clock was re-anchored because the error was too large.</summary>
    public int Resyncs { get; private set; }

    /// <summary>True while a segment plays (between <see cref="BeginSegment"/> and <see cref="Stand"/>).</summary>
    public bool InSegment => _inSegment;

    /// <summary>The clock stands at <paramref name="mediaTicks"/> (no sound plays, or a seek holds it).</summary>
    public void Stand(long mediaTicks)
    {
        _inSegment = false;
        _segmentEndFrame = long.MaxValue;
        Current = ClockSnapshot.Standing(mediaTicks);
    }

    /// <summary>
    /// Stream frame <paramref name="streamFrame"/> carries media time <paramref name="mediaTicks"/>, and the following
    /// frames continue from there. <paramref name="heardTimestamp"/> is the estimate when that frame will be heard;
    /// until then the clock stands at <paramref name="mediaTicks"/>.
    /// </summary>
    public void BeginSegment(long streamFrame, long mediaTicks, long heardTimestamp)
    {
        _inSegment = true;
        _segmentStartFrame = streamFrame;
        _segmentStartMediaTicks = mediaTicks;
        _segmentEndFrame = long.MaxValue;
        _locked = false;
        Current = new ClockSnapshot(heardTimestamp, mediaTicks, _nominalRate * (1 + _drift), mediaTicks, long.MaxValue);
    }

    /// <summary>The segment ends before stream frame <paramref name="streamFrame"/>; the clock stops there.</summary>
    public void EndSegment(long streamFrame)
    {
        if (!_inSegment)
        {
            return;
        }

        _segmentEndFrame = streamFrame;
        Current = Current with { MaxMediaTicks = MediaTicksAt(streamFrame) };
    }

    /// <summary>Takes back <see cref="EndSegment"/>: the segment plays on.</summary>
    public void CancelEnd()
    {
        if (!_inSegment)
        {
            return;
        }

        _segmentEndFrame = long.MaxValue;
        Current = Current with { MaxMediaTicks = long.MaxValue };
    }

    /// <summary>Media time of stream frame <paramref name="streamFrame"/> in the current segment.</summary>
    public long MediaTicksAt(double streamFrame) =>
        _segmentStartMediaTicks + (long)((streamFrame - _segmentStartFrame) * _mediaTicksPerFrame);

    /// <summary>
    /// The device reports that stream frame <paramref name="positionFrames"/> is heard at <paramref name="positionTimestamp"/>.
    /// <paramref name="now"/> is the current timestamp; the clock stays continuous there.
    /// </summary>
    public void Update(long now, double positionFrames, long positionTimestamp)
    {
        if (!_inSegment || positionFrames < _segmentStartFrame || positionFrames >= _segmentEndFrame)
        {
            // Not heard yet (the clock stands at the start) or already over (it stands at the end).
            return;
        }

        long measured = MediaTicksAt(positionFrames);
        if (!_locked)
        {
            Lock(positionTimestamp, measured);
            return;
        }

        long error = measured - Current.Unlimited(positionTimestamp);
        LastErrorTicks = error;
        if (Math.Abs(error) > ResyncThresholdTicks)
        {
            Resyncs++;
            Lock(positionTimestamp, measured);
            return;
        }

        double errorSeconds = (double)error / TimeSpan.TicksPerSecond;
        double elapsedSeconds = Math.Clamp((positionTimestamp - _lastUpdateTimestamp) / _timestampFrequency, 0, 0.1);
        _lastUpdateTimestamp = positionTimestamp;
        _drift = Math.Clamp(_drift + (errorSeconds * elapsedSeconds / (DriftTimeSeconds * DriftTimeSeconds)), -MaxDrift, MaxDrift);
        _driftAverage += (_drift - _driftAverage) * Math.Min(1, elapsedSeconds / DriftDisplayTimeSeconds);
        double correction = Math.Clamp(errorSeconds / CorrectionTimeSeconds, -MaxCorrection, MaxCorrection);

        // Continuous at "now": only the speed changes.
        long anchorMedia = Current.Unlimited(now);
        Current = Current with
        {
            AnchorTimestamp = now,
            AnchorMediaTicks = anchorMedia,
            MediaTicksPerTimestampTick = _nominalRate * (1 + _drift + correction),
        };
    }

    private void Lock(long timestamp, long mediaTicks)
    {
        _locked = true;
        _lastUpdateTimestamp = timestamp;
        LastErrorTicks = 0;
        Current = Current with
        {
            AnchorTimestamp = timestamp,
            AnchorMediaTicks = mediaTicks,
            MediaTicksPerTimestampTick = _nominalRate * (1 + _drift),
        };
    }
}
