using Orbivo.Core.Concurrency;

namespace Orbivo.Core.Playback;

/// <summary>A/V deviation measured by <see cref="AvSyncMeter"/>, in milliseconds (positive = picture after the sound).</summary>
/// <param name="WindowFrames">Frames measured in the last completed window (about one second).</param>
/// <param name="WindowMeanMs">Mean deviation in that window.</param>
/// <param name="WindowMinMs">Smallest deviation in that window.</param>
/// <param name="WindowMaxMs">Largest deviation in that window.</param>
/// <param name="WindowEndTimestamp">Stopwatch timestamp when the window was completed, 0 if none yet.</param>
/// <param name="TotalFrames">Frames measured since <see cref="AvSyncMeter.Reset"/>.</param>
/// <param name="TotalMinMs">Smallest deviation since the reset.</param>
/// <param name="TotalMaxMs">Largest deviation since the reset.</param>
/// <param name="TotalOutsideLimit">Frames outside ±<see cref="AvSyncMeter.LimitMilliseconds"/> since the reset.</param>
public readonly record struct AvSyncStats(
    int WindowFrames,
    double WindowMeanMs,
    double WindowMinMs,
    double WindowMaxMs,
    long WindowEndTimestamp,
    long TotalFrames,
    double TotalMinMs,
    double TotalMaxMs,
    long TotalOutsideLimit)
{
    public static AvSyncStats None => default;
}

/// <summary>
/// Measures the A/V deviation for the status (F40) and the log: for every frame the render thread shows for the first
/// time, the clock at the display time minus the frame's presentation time. On a 90 Hz headset this is between 0 and
/// one refresh (11 ms): a frame appears at the first refresh after it is due. Larger values mean late frames.
/// </summary>
/// <remarks>
/// <see cref="Record"/> belongs to the render thread and neither blocks nor allocates (CLAUDE.md rule 1). It completes
/// a window every <see cref="WindowSeconds"/> and publishes the statistics through a <see cref="SnapshotCell{T}"/>.
/// <see cref="Reset"/> (any thread) asks the render thread to start the totals again.
/// </remarks>
public sealed class AvSyncMeter
{
    /// <summary>N2: the deviation stays within ±20 ms.</summary>
    public const double LimitMilliseconds = 20;

    public const double WindowSeconds = 1;

    private readonly SnapshotCell<AvSyncStats> _published = new(AvSyncStats.None);
    private readonly long _windowTimestampTicks;
    private readonly double _millisecondsPerMediaTick = 1.0 / TimeSpan.TicksPerMillisecond;
    private int _resetRequests;

    // Render thread only.
    private int _resetsSeen;
    private long _windowStart;
    private int _windowFrames;
    private double _windowSum;
    private double _windowMin;
    private double _windowMax;
    private long _totalFrames;
    private double _totalMin;
    private double _totalMax;
    private long _totalOutside;

    /// <param name="timestampFrequency">Stopwatch ticks per second.</param>
    public AvSyncMeter(long timestampFrequency)
    {
        _windowTimestampTicks = (long)(timestampFrequency * WindowSeconds);
        ResetTotals();
        ResetWindow(0);
    }

    /// <summary>The latest statistics. Any thread.</summary>
    public AvSyncStats Current => _published.Read();

    /// <summary>Any thread: the totals start again with the next frame (new video, seek).</summary>
    public void Reset() => Interlocked.Increment(ref _resetRequests);

    /// <summary>Render thread: a frame is shown for the first time at <paramref name="timestamp"/>.</summary>
    /// <param name="timestamp">Display timestamp.</param>
    /// <param name="deviationTicks">Clock at the display time minus the frame's presentation time.</param>
    public void Record(long timestamp, long deviationTicks)
    {
        int resets = Volatile.Read(ref _resetRequests);
        if (resets != _resetsSeen)
        {
            _resetsSeen = resets;
            ResetTotals();
            ResetWindow(timestamp);
        }

        if (_windowFrames == 0 && _windowStart == 0)
        {
            _windowStart = timestamp;
        }

        double milliseconds = deviationTicks * _millisecondsPerMediaTick;
        _windowFrames++;
        _windowSum += milliseconds;
        _windowMin = Math.Min(_windowMin, milliseconds);
        _windowMax = Math.Max(_windowMax, milliseconds);
        _totalFrames++;
        _totalMin = Math.Min(_totalMin, milliseconds);
        _totalMax = Math.Max(_totalMax, milliseconds);
        if (Math.Abs(milliseconds) > LimitMilliseconds)
        {
            _totalOutside++;
        }

        if (timestamp - _windowStart >= _windowTimestampTicks)
        {
            _published.Write(new AvSyncStats(
                _windowFrames,
                _windowSum / _windowFrames,
                _windowMin,
                _windowMax,
                timestamp,
                _totalFrames,
                _totalMin,
                _totalMax,
                _totalOutside));
            ResetWindow(timestamp);
        }
    }

    private void ResetWindow(long timestamp)
    {
        _windowStart = timestamp;
        _windowFrames = 0;
        _windowSum = 0;
        _windowMin = double.MaxValue;
        _windowMax = double.MinValue;
    }

    private void ResetTotals()
    {
        _totalFrames = 0;
        _totalMin = double.MaxValue;
        _totalMax = double.MinValue;
        _totalOutside = 0;
    }
}
