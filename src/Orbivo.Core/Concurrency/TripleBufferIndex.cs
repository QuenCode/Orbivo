namespace Orbivo.Core.Concurrency;

/// <summary>
/// Lock-free triple buffering of three slots (index 0 to 2) between one writer and one reader, "newest wins":
/// the writer always has a slot of its own to fill, the reader always keeps the slot it shows, and the third slot
/// holds the newest finished one. Neither side ever waits (CLAUDE.md rule 1). The slots themselves (textures,
/// buffers) belong to the caller; this class only hands out their indices.
/// </summary>
/// <remarks>
/// The writer fills <see cref="WriteIndex"/> and calls <see cref="Publish"/>: its slot becomes the newest one and it gets
/// the slot that held the newest before. The reader calls <see cref="TryAcquireNewest"/>: if a newer slot was published
/// since its last call, it swaps its slot for that one. Writer and reader can therefore never hold the same slot.
/// The interlocked exchange orders the memory accesses: what the writer stored in a slot before publishing it is
/// visible to the reader after acquiring it.
/// </remarks>
public sealed class TripleBufferIndex
{
    public const int SlotCount = 3;

    private const int IndexMask = 0b011;
    private const int NewBit = 0b100;

    // Index of the middle slot; NewBit set if the writer published it after the reader's last acquisition.
    private int _middle = 1;

    // Writer thread only.
    private int _write;

    // Reader thread only.
    private int _read = 2;

    /// <summary>Writer: the slot to fill now. Never the reader's slot.</summary>
    public int WriteIndex => _write;

    /// <summary>Reader: the slot acquired last (initially one that was never written).</summary>
    public int ReadIndex => _read;

    /// <summary>Writer: the slot at <see cref="WriteIndex"/> is complete; <see cref="WriteIndex"/> moves to another slot.</summary>
    public void Publish()
    {
        _write = Interlocked.Exchange(ref _middle, _write | NewBit) & IndexMask;
    }

    /// <summary>
    /// Reader: takes the newest published slot, if there is one the reader has not seen. Returns false and keeps
    /// <see cref="ReadIndex"/> otherwise.
    /// </summary>
    public bool TryAcquireNewest()
    {
        if ((Volatile.Read(ref _middle) & NewBit) == 0)
        {
            return false;
        }

        _read = Interlocked.Exchange(ref _middle, _read) & IndexMask;
        return true;
    }
}
