using System.Diagnostics;
using System.Numerics;
using Orbivo.Core.Concurrency;
using Orbivo.Core.Playback;

namespace Orbivo.Core.Logging;

/// <summary>One row of the head log (F50), filled by the render thread every frame. Copied, never shared.</summary>
/// <param name="Timestamp">Stopwatch timestamp the pose belongs to (predicted display time in the headset).</param>
/// <param name="Tracked">True if the head orientation of this frame is valid.</param>
/// <param name="TrackedHead">Head orientation as tracked (LOCAL space of the runtime, or the mouse view in the preview).</param>
/// <param name="HeadInContent">Viewing direction in the video's own frame (after recenter and orientation offset).</param>
/// <param name="RecenterYawDegrees">Recenter turn at that moment (F30).</param>
/// <param name="Content">What was shown, before blackout and fade.</param>
/// <param name="Brightness">Blackout and fade factor (1 = full picture, 0 = black).</param>
/// <param name="State">Transport state of the player.</param>
/// <param name="VideoFile">Loaded video (full path) or null. A reference copy, no allocation.</param>
/// <param name="VideoPtsTicks">Presentation time of the shown video frame, or <see cref="long.MinValue"/>.</param>
public readonly record struct HeadSample(
    long Timestamp,
    bool Tracked,
    Quaternion TrackedHead,
    Quaternion HeadInContent,
    float RecenterYawDegrees,
    HeadsetContent Content,
    float Brightness,
    PlaybackState State,
    string? VideoFile,
    long VideoPtsTicks);

/// <summary>
/// The render thread's entry to the head log: lets samples through at the chosen rate into a lock-free queue while a
/// recording runs, and costs nothing otherwise. Never blocks, never allocates (CLAUDE.md rule 1). Only one render
/// thread may record at a time (headset or preview).
/// </summary>
public sealed class HeadSampleGate
{
    /// <summary>Rows of 90 s at 90 Hz fit, so the writer may stall for a while without losing any.</summary>
    public const int Capacity = 8192;

    private readonly SpscQueue<HeadSample> _queue = new(Capacity);
    private int _active;
    private long _periodTicks;
    private long _toleranceTicks;
    private long _nextDue = long.MinValue;
    private long _dropped;

    /// <summary>Samples waiting for the writer. Consumer side: <see cref="TryTake"/>.</summary>
    public int Pending => _queue.Count;

    /// <summary>Samples lost because the queue was full, since the last <see cref="Activate"/>.</summary>
    public long Dropped => Volatile.Read(ref _dropped);

    /// <summary>True while samples are let through.</summary>
    public bool IsActive => Volatile.Read(ref _active) != 0;

    /// <summary>
    /// Writer thread. Starts letting samples through at <paramref name="rateHz"/> (0 = every frame). Samples from
    /// before are not in the queue (the writer drained it at the last stop).
    /// </summary>
    public void Activate(int rateHz)
    {
        SetRate(rateHz);
        Volatile.Write(ref _dropped, 0);
        Volatile.Write(ref _active, 1);
    }

    /// <summary>Any thread. Changes the rate of the running recording (0 = every frame).</summary>
    public void SetRate(int rateHz)
    {
        long period = rateHz <= 0 ? 0 : Stopwatch.Frequency / rateHz;

        // A frame up to 2 ms early still counts, so 45 Hz at 90 fps takes every second frame despite jitter.
        Volatile.Write(ref _toleranceTicks, Math.Min(period / 4, Stopwatch.Frequency / 500));
        Volatile.Write(ref _periodTicks, period);
    }

    /// <summary>Writer thread. Stops letting samples through; the writer drains what is queued.</summary>
    public void Deactivate()
    {
        Volatile.Write(ref _active, 0);
    }

    /// <summary>Render thread, once per frame. Queues the sample if a recording runs and it is due.</summary>
    public void Offer(in HeadSample sample)
    {
        if (Volatile.Read(ref _active) == 0)
        {
            _nextDue = long.MinValue;
            return;
        }

        long period = Volatile.Read(ref _periodTicks);
        if (period > 0)
        {
            if (_nextDue != long.MinValue && sample.Timestamp + Volatile.Read(ref _toleranceTicks) < _nextDue)
            {
                return;
            }

            // Keep the grid, unless the render thread fell behind by more than one period.
            _nextDue = _nextDue == long.MinValue || sample.Timestamp - _nextDue > period
                ? sample.Timestamp + period
                : _nextDue + period;
        }

        if (!_queue.TryEnqueue(sample))
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    /// <summary>Writer thread only.</summary>
    public bool TryTake(out HeadSample sample) => _queue.TryDequeue(out sample);
}
