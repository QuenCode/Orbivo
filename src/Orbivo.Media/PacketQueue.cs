using FFmpeg.AutoGen;

namespace Orbivo.Media;

/// <summary>What a <see cref="PacketQueue"/> entry carries.</summary>
internal enum PacketEntryKind
{
    /// <summary>A compressed packet of the stream.</summary>
    Packet,

    /// <summary>
    /// The demuxer has jumped (seek): everything before is outdated, decode from the target on.
    /// <see cref="PacketEntry.SeekId"/> identifies the seek.
    /// </summary>
    Seek,

    /// <summary>
    /// The demuxer has jumped again within the same seek (video only: the target lay after the last frame, so the
    /// decoder shows the last frame instead).
    /// </summary>
    Reseek,

    /// <summary>End of the file: no more packets until the next seek.</summary>
    End,
}

/// <summary>One entry of a <see cref="PacketQueue"/>. A packet entry owns its <see cref="Packet"/>.</summary>
internal readonly unsafe struct PacketEntry
{
    public PacketEntry(PacketEntryKind kind, AVPacket* packet, int seekId, long targetTicks, long durationTicks = 0)
    {
        Kind = kind;
        Packet = packet;
        SeekId = seekId;
        TargetTicks = targetTicks;
        DurationTicks = durationTicks;
    }

    public PacketEntryKind Kind { get; }

    public AVPacket* Packet { get; }

    /// <summary>Seek the marker belongs to.</summary>
    public int SeekId { get; }

    /// <summary>Seek target of a marker (media time).</summary>
    public long TargetTicks { get; }

    /// <summary>Duration of a packet.</summary>
    public long DurationTicks { get; }
}

/// <summary>
/// Compressed packets of one stream from the demux thread to a decode thread (docs/ARCHITECTURE.md, "Threads").
/// Both threads may block, so a plain lock and <see cref="Monitor"/> are used.
/// </summary>
/// <remarks>
/// The queue is unbounded; the demuxer limits it by looking at <see cref="DurationTicks"/> and <see cref="Bytes"/>.
/// <see cref="Flush"/> frees all queued packets and puts a seek marker in their place.
/// </remarks>
internal sealed unsafe class PacketQueue : IDisposable
{
    private readonly Queue<PacketEntry> _entries = new();
    private readonly object _lock = new();
    private readonly AutoResetEvent _consumed;
    private readonly AVRational _timeBase;
    private long _durationTicks;
    private long _bytes;
    private bool _disposed;

    /// <param name="timeBase">Time base of the packets' durations.</param>
    /// <param name="consumed">Set whenever the decoder takes an entry, so the demuxer can read on.</param>
    public PacketQueue(AVRational timeBase, AutoResetEvent consumed)
    {
        _timeBase = timeBase;
        _consumed = consumed;
    }

    /// <summary>Sum of the durations of the queued packets.</summary>
    public long DurationTicks
    {
        get
        {
            lock (_lock)
            {
                return _durationTicks;
            }
        }
    }

    /// <summary>Sum of the sizes of the queued packets.</summary>
    public long Bytes
    {
        get
        {
            lock (_lock)
            {
                return _bytes;
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>Demux thread: takes over <paramref name="packet"/> (the queue frees it).</summary>
    public void EnqueuePacket(AVPacket* packet)
    {
        long duration = packet->duration > 0
            ? ffmpeg.av_rescale_q(packet->duration, _timeBase, new AVRational { num = 1, den = (int)TimeSpan.TicksPerSecond })
            : 0;
        lock (_lock)
        {
            _entries.Enqueue(new PacketEntry(PacketEntryKind.Packet, packet, 0, 0, duration));
            _durationTicks += duration;
            _bytes += packet->size;
            Monitor.Pulse(_lock);
        }
    }

    /// <summary>Demux thread: a marker (end of file, reseek).</summary>
    public void EnqueueMarker(PacketEntryKind kind, int seekId, long targetTicks)
    {
        lock (_lock)
        {
            _entries.Enqueue(new PacketEntry(kind, null, seekId, targetTicks));
            Monitor.Pulse(_lock);
        }
    }

    /// <summary>Demux thread: frees all queued packets and queues a <paramref name="kind"/> marker (seek or reseek).</summary>
    public void Flush(PacketEntryKind kind, int seekId, long targetTicks)
    {
        lock (_lock)
        {
            FreeAll();
            _entries.Enqueue(new PacketEntry(kind, null, seekId, targetTicks));
            Monitor.Pulse(_lock);
        }
    }

    /// <summary>
    /// Decode thread: the next entry, waiting at most <paramref name="timeout"/>. The caller owns a returned packet
    /// and frees it with <see cref="Free"/>.
    /// </summary>
    public bool TryDequeue(TimeSpan timeout, out PacketEntry entry)
    {
        lock (_lock)
        {
            if (_entries.Count == 0 && !_disposed)
            {
                Monitor.Wait(_lock, timeout);
            }

            if (!_entries.TryDequeue(out entry))
            {
                return false;
            }

            if (entry.Kind == PacketEntryKind.Packet)
            {
                _durationTicks -= entry.DurationTicks;
                _bytes -= entry.Packet->size;
            }
        }

        _consumed.Set();
        return true;
    }

    /// <summary>Frees the packet of an entry taken with <see cref="TryDequeue"/>.</summary>
    public static void Free(in PacketEntry entry)
    {
        if (entry.Packet is not null)
        {
            AVPacket* packet = entry.Packet;
            ffmpeg.av_packet_free(&packet);
        }
    }

    /// <summary>Wakes a waiting decode thread (stopping).</summary>
    public void WakeConsumer()
    {
        lock (_lock)
        {
            Monitor.PulseAll(_lock);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            FreeAll();
            Monitor.PulseAll(_lock);
        }
    }

    private void FreeAll()
    {
        while (_entries.TryDequeue(out PacketEntry entry))
        {
            Free(entry);
        }

        _durationTicks = 0;
        _bytes = 0;
    }
}
