using Orbivo.Core.Concurrency;

namespace Orbivo.Core.Playback;

/// <summary>
/// Hands decoded frames from the decode thread to the render thread and picks the frame to show
/// (docs/ARCHITECTURE.md, "Synchronisation"). Frames live in a fixed set of slots (GPU textures in Orbivo.Media);
/// only slot numbers and times pass through the two lock-free queues.
/// </summary>
/// <remarks>
/// Producer (decode thread): <see cref="TryAcquireFreeSlot"/>, <see cref="WaitForFreeSlot"/>, <see cref="Publish"/>.
/// Consumer (render thread): <see cref="TrySelect"/>. It never blocks and never allocates (CLAUDE.md rule 1).
/// The slot returned by <see cref="TrySelect"/> stays reserved for the render thread until a newer frame replaces it.
/// </remarks>
public sealed class FramePacer : IDisposable
{
    private readonly SpscQueue<QueuedFrame> _ready;
    private readonly SpscQueue<int> _free;
    private readonly AutoResetEvent _slotFreed = new(false);

    // Render thread state.
    private int _currentSlot = -1;
    private long _currentPts;
    private bool _currentShown;

    // Counters: written by one thread each, read anywhere.
    private long _droppedFrames;
    private long _shownFrames;

    public FramePacer(int slotCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slotCount, 2);
        SlotCount = slotCount;
        _ready = new SpscQueue<QueuedFrame>(slotCount);
        _free = new SpscQueue<int>(slotCount);
        for (int slot = 0; slot < slotCount; slot++)
        {
            _free.TryEnqueue(slot);
        }
    }

    public int SlotCount { get; }

    /// <summary>Decoded frames waiting to be shown.</summary>
    public int ReadyCount => _ready.Count;

    /// <summary>Frames that were decoded but replaced by a newer frame before they were shown once.</summary>
    public long DroppedFrames => Volatile.Read(ref _droppedFrames);

    /// <summary>Frames that were shown at least once.</summary>
    public long ShownFrames => Volatile.Read(ref _shownFrames);

    /// <summary>Producer: takes a slot to decode into. Returns false if all slots are in use.</summary>
    public bool TryAcquireFreeSlot(out int slot)
    {
        return _free.TryDequeue(out slot);
    }

    /// <summary>Producer: waits until the render thread frees a slot, at most <paramref name="timeout"/>.</summary>
    public void WaitForFreeSlot(TimeSpan timeout)
    {
        _slotFreed.WaitOne(timeout);
    }

    /// <summary>Producer: the frame in <paramref name="slot"/> is complete and has presentation time <paramref name="ptsTicks"/>.</summary>
    public void Publish(int slot, long ptsTicks)
    {
        if (!_ready.TryEnqueue(new QueuedFrame(slot, ptsTicks)))
        {
            // Cannot happen: there are only SlotCount slots and the ready queue holds at least that many.
            throw new InvalidOperationException("Frame queue overflow.");
        }
    }

    /// <summary>
    /// Consumer: picks the newest frame with a presentation time at or before <paramref name="targetTicks"/>.
    /// Older frames are released and counted as dropped if they were never shown. Keeps the current frame
    /// while no newer one is due (pause, or a video slower than the display).
    /// </summary>
    /// <returns>False until the first frame is due.</returns>
    public bool TrySelect(long targetTicks, out int slot, out long ptsTicks)
    {
        bool freed = false;
        while (_ready.TryPeek(out QueuedFrame next) && next.PtsTicks <= targetTicks)
        {
            _ready.TryDequeue(out _);
            if (_currentSlot >= 0)
            {
                if (!_currentShown)
                {
                    Volatile.Write(ref _droppedFrames, _droppedFrames + 1);
                }

                _free.TryEnqueue(_currentSlot);
                freed = true;
            }

            _currentSlot = next.Slot;
            _currentPts = next.PtsTicks;
            _currentShown = false;
        }

        if (freed)
        {
            _slotFreed.Set();
        }

        slot = _currentSlot;
        ptsTicks = _currentPts;
        if (_currentSlot < 0)
        {
            return false;
        }

        if (!_currentShown)
        {
            _currentShown = true;
            Volatile.Write(ref _shownFrames, _shownFrames + 1);
        }

        return true;
    }

    public void Dispose()
    {
        _slotFreed.Dispose();
    }

    private readonly record struct QueuedFrame(int Slot, long PtsTicks);
}
