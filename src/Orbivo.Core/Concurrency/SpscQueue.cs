using System.Numerics;

namespace Orbivo.Core.Concurrency;

/// <summary>
/// Bounded lock-free queue for exactly one producer thread and one consumer thread
/// (docs/ARCHITECTURE.md, "Threads": shared data between non-blocking threads).
/// </summary>
/// <remarks>
/// Neither side blocks or allocates. <see cref="TryEnqueue"/> may only be called by the producer,
/// <see cref="TryDequeue"/> and <see cref="TryPeek"/> only by the consumer. <see cref="Count"/> may be read anywhere.
/// </remarks>
/// <typeparam name="T">Element type; a struct, so that elements are copied and never shared.</typeparam>
public sealed class SpscQueue<T>
    where T : struct
{
    private readonly T[] _items;
    private readonly long _mask;

    // _head: next element to read (written by the consumer). _tail: next free position (written by the producer).
    private long _head;
    private long _tail;

    /// <param name="minimumCapacity">At least this many elements fit; rounded up to a power of two.</param>
    public SpscQueue(int minimumCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumCapacity, 1);
        int capacity = (int)BitOperations.RoundUpToPowerOf2((uint)minimumCapacity);
        _items = new T[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _items.Length;

    /// <summary>Number of queued elements. Exact on the producer and consumer threads, a snapshot elsewhere.</summary>
    public int Count => (int)(Volatile.Read(ref _tail) - Volatile.Read(ref _head));

    /// <summary>Producer only. Returns false if the queue is full.</summary>
    public bool TryEnqueue(in T item)
    {
        long tail = _tail;
        if (tail - Volatile.Read(ref _head) >= _items.Length)
        {
            return false;
        }

        _items[tail & _mask] = item;

        // Publishes the element: the consumer sees the new tail only after the element is written.
        Volatile.Write(ref _tail, tail + 1);
        return true;
    }

    /// <summary>Consumer only. Returns false if the queue is empty.</summary>
    public bool TryDequeue(out T item)
    {
        long head = _head;
        if (head == Volatile.Read(ref _tail))
        {
            item = default;
            return false;
        }

        item = _items[head & _mask];
        _items[head & _mask] = default;

        // Frees the position: the producer reuses it only after the element was read.
        Volatile.Write(ref _head, head + 1);
        return true;
    }

    /// <summary>Consumer only. Reads the oldest element without removing it.</summary>
    public bool TryPeek(out T item)
    {
        long head = _head;
        if (head == Volatile.Read(ref _tail))
        {
            item = default;
            return false;
        }

        item = _items[head & _mask];
        return true;
    }
}
