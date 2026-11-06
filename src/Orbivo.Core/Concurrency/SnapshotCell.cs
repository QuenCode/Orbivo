namespace Orbivo.Core.Concurrency;

/// <summary>
/// Holds the latest value of a small struct that one thread writes often and other threads read, without locks and
/// without allocating (CLAUDE.md rules 1 and 2). Used for the audio clock: the audio thread writes it every period,
/// the render thread reads it every frame.
/// </summary>
/// <remarks>
/// The writer fills one of <see cref="SlotCount"/> slots in turn, each guarded by a sequence number (a seqlock per slot),
/// and then publishes the slot index. A reader copies the published slot and checks that its sequence number did not
/// change meanwhile. The writer reaches a slot again only after <see cref="SlotCount"/> − 1 further writes, so a reader
/// practically never has to retry. Only one thread may write.
/// </remarks>
/// <typeparam name="T">Value type; copied in and out.</typeparam>
public sealed class SnapshotCell<T>
    where T : struct
{
    public const int SlotCount = 8;

    private readonly T[] _slots = new T[SlotCount];
    private readonly int[] _sequences = new int[SlotCount];
    private int _index;

    public SnapshotCell(T initial)
    {
        _slots[0] = initial;
    }

    /// <summary>Writer thread only. Publishes <paramref name="value"/>; never blocks, never allocates.</summary>
    public void Write(in T value)
    {
        int next = (_index + 1) & (SlotCount - 1);

        // Odd sequence = being written. Interlocked is a full fence: the slot writes cannot move before it.
        Interlocked.Increment(ref _sequences[next]);
        _slots[next] = value;
        Volatile.Write(ref _sequences[next], _sequences[next] + 1);
        Volatile.Write(ref _index, next);
    }

    /// <summary>Any thread. The latest published value; never blocks, never allocates.</summary>
    public T Read()
    {
        while (true)
        {
            int index = Volatile.Read(ref _index);
            int before = Volatile.Read(ref _sequences[index]);
            if ((before & 1) == 0)
            {
                T value = _slots[index];

                // The copy above must be complete before the sequence is read again.
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _sequences[index]) == before)
                {
                    return value;
                }
            }

            // Only reached if the writer lapped all slots during the copy (reader preempted for many periods).
            Thread.SpinWait(1);
        }
    }
}
