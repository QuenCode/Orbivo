using System.Numerics;

namespace Orbivo.Core.Audio;

/// <summary>
/// Decoded sound on its way from the audio decode thread to the audio output thread (docs/ARCHITECTURE.md, "Audio"):
/// lock-free, one producer and one consumer, interleaved float32 frames at the output sample rate.
/// </summary>
/// <remarks>
/// The content is addressed by media position: each generation of data starts at a known frame position
/// (<see cref="Start"/>) and continues without gaps, so the consumer always knows which media time it reads
/// (<see cref="ReadPosition"/>). Gaps, late data and the start after a seek are all handled by the consumer comparing
/// <see cref="ReadPosition"/> with the position it has to play.
/// <para>
/// Flushing (seek, new file) is a hand-shake: the producer asks for a new generation with <see cref="RequestFlush"/>
/// and waits until the consumer has dropped the old data (<see cref="AcknowledgeFlush"/>). The consumer does that only
/// once it has faded out, so a seek never cuts the sound off hard.
/// </para>
/// Consumer methods never block and never allocate (CLAUDE.md rule 2). Producer methods may wait.
/// </remarks>
public sealed class AudioRingBuffer : IDisposable
{
    private readonly float[] _samples;
    private readonly long _frameMask;
    private readonly AutoResetEvent _consumerProgress = new(false);

    // Frame counters that only grow. _head: next frame to read (consumer). _tail: next frame to write (producer).
    private long _head;
    private long _tail;

    // Producer → consumer.
    private int _requestedGeneration;
    private int _startedGeneration;
    private long _startFrame;
    private int _sampleRate;
    private int _endedGeneration;

    // Consumer → producer.
    private int _generation;
    private long _generationHead;

    /// <param name="channels">Samples per frame.</param>
    /// <param name="minimumCapacityFrames">At least this many frames fit; rounded up to a power of two.</param>
    public AudioRingBuffer(int channels, int minimumCapacityFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumCapacityFrames, 2);
        Channels = channels;
        CapacityFrames = (int)BitOperations.RoundUpToPowerOf2((uint)minimumCapacityFrames);
        _frameMask = CapacityFrames - 1;
        _samples = new float[CapacityFrames * channels];
    }

    public int Channels { get; }

    public int CapacityFrames { get; }

    /// <summary>Frames written and not yet read. Exact on the producer and consumer threads, a snapshot elsewhere.</summary>
    public int BufferedFrames => (int)(Volatile.Read(ref _tail) - Volatile.Read(ref _head));

    /// <summary>Generation the consumer reads (the latest acknowledged flush).</summary>
    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Generation the producer asked for last.</summary>
    public int RequestedGeneration => Volatile.Read(ref _requestedGeneration);

    // ---------------------------------------------------------------- producer

    /// <summary>
    /// Producer: all data written so far is outdated. Returns the new generation; wait with <see cref="WaitForFlush"/>
    /// until the consumer has dropped the old data, then call <see cref="Start"/> and write.
    /// </summary>
    public int RequestFlush()
    {
        int generation = _requestedGeneration + 1;
        Volatile.Write(ref _requestedGeneration, generation);
        return generation;
    }

    /// <summary>True once the consumer reads <paramref name="generation"/> (or a later one).</summary>
    public bool IsFlushed(int generation) => Volatile.Read(ref _generation) >= generation;

    /// <summary>Producer: waits at most <paramref name="timeout"/> for <see cref="IsFlushed"/>.</summary>
    public bool WaitForFlush(int generation, TimeSpan timeout)
    {
        if (IsFlushed(generation))
        {
            return true;
        }

        _consumerProgress.WaitOne(timeout);
        return IsFlushed(generation);
    }

    /// <summary>
    /// Producer, after the flush: the first frame written from now on has media position <paramref name="startFrame"/>
    /// (in frames at the output rate). Later frames follow without gaps.
    /// </summary>
    /// <param name="startFrame">Media position of the first frame.</param>
    /// <param name="sampleRate">Sample rate of the data; the consumer plays it only on a device stream of the same rate.</param>
    public void Start(long startFrame, int sampleRate)
    {
        Volatile.Write(ref _startFrame, startFrame);
        Volatile.Write(ref _sampleRate, sampleRate);
        Volatile.Write(ref _startedGeneration, _requestedGeneration);
    }

    /// <summary>Sample rate of the current data (from <see cref="Start"/>), 0 before the first start.</summary>
    public int SampleRate => Volatile.Read(ref _sampleRate);

    /// <summary>Producer: true if <see cref="Start"/> was called for the current generation.</summary>
    public bool IsStarted => Volatile.Read(ref _startedGeneration) == _requestedGeneration;

    /// <summary>Producer: room for this many frames.</summary>
    public int FreeFrames => CapacityFrames - (int)(_tail - Volatile.Read(ref _head));

    /// <summary>Producer: media position of the next frame to write. Valid after <see cref="Start"/>.</summary>
    public long WritePosition => Volatile.Read(ref _startFrame) + (_tail - Volatile.Read(ref _generationHead));

    /// <summary>Producer: copies as many whole frames of <paramref name="interleaved"/> as fit; returns the number of frames.</summary>
    public int Write(ReadOnlySpan<float> interleaved)
    {
        int frames = Math.Min(interleaved.Length / Channels, FreeFrames);
        CopyIn(interleaved[..(frames * Channels)]);
        return frames;
    }

    /// <summary>Producer: writes up to <paramref name="frames"/> frames of silence; returns the number written.</summary>
    public int WriteSilence(int frames)
    {
        frames = Math.Min(frames, FreeFrames);
        long tail = _tail;
        for (int i = 0; i < frames; i++)
        {
            int offset = (int)((tail + i) & _frameMask) * Channels;
            _samples.AsSpan(offset, Channels).Clear();
        }

        Volatile.Write(ref _tail, tail + frames);
        return frames;
    }

    /// <summary>Producer: waits at most <paramref name="timeout"/> until the consumer has read or flushed something.</summary>
    public void WaitForConsumer(TimeSpan timeout)
    {
        _consumerProgress.WaitOne(timeout);
    }

    /// <summary>Producer: no more data follows in this generation (end of the file).</summary>
    public void MarkEnd()
    {
        Volatile.Write(ref _endedGeneration, _requestedGeneration);
    }

    // ---------------------------------------------------------------- consumer

    /// <summary>Consumer: the producer waits for <see cref="AcknowledgeFlush"/>.</summary>
    public bool FlushPending => Volatile.Read(ref _requestedGeneration) != _generation;

    /// <summary>Consumer: drops all data and switches to the requested generation.</summary>
    public void AcknowledgeFlush()
    {
        int requested = Volatile.Read(ref _requestedGeneration);
        if (requested == _generation)
        {
            return;
        }

        // The producer does not write while it waits for this, so the tail is stable.
        long tail = Volatile.Read(ref _tail);
        Volatile.Write(ref _head, tail);
        Volatile.Write(ref _generationHead, tail);
        Volatile.Write(ref _generation, requested);
        _consumerProgress.Set();
    }

    /// <summary>
    /// Consumer: the producer has said where the data of the current generation starts. Stays true while a flush is
    /// pending: the old data can still be read (to fade it out) until <see cref="AcknowledgeFlush"/>.
    /// </summary>
    public bool HasStart => Volatile.Read(ref _startedGeneration) == _generation;

    /// <summary>Consumer: media position (frames) of the next frame to read. Valid if <see cref="HasStart"/>.</summary>
    public long ReadPosition => Volatile.Read(ref _startFrame) + (_head - _generationHead);

    /// <summary>Consumer: frames ready to read (of the current generation, also while a flush is pending).</summary>
    public int AvailableFrames => (int)(Volatile.Read(ref _tail) - _head);

    /// <summary>Consumer: the current generation is complete and fully read.</summary>
    public bool EndOfData => Volatile.Read(ref _endedGeneration) == _generation && AvailableFrames == 0;

    /// <summary>Consumer: copies up to <paramref name="frames"/> frames into <paramref name="destination"/>; returns the number read.</summary>
    public int Read(Span<float> destination, int frames)
    {
        frames = Math.Min(Math.Min(frames, AvailableFrames), destination.Length / Channels);
        long head = _head;
        int first = (int)Math.Min(frames, CapacityFrames - (head & _frameMask));
        int offset = (int)(head & _frameMask) * Channels;
        _samples.AsSpan(offset, first * Channels).CopyTo(destination);
        if (first < frames)
        {
            _samples.AsSpan(0, (frames - first) * Channels).CopyTo(destination[(first * Channels)..]);
        }

        Volatile.Write(ref _head, head + frames);
        _consumerProgress.Set();
        return frames;
    }

    /// <summary>Consumer: drops up to <paramref name="frames"/> frames; returns the number dropped.</summary>
    public int Skip(long frames)
    {
        int skipped = (int)Math.Min(frames, AvailableFrames);
        Volatile.Write(ref _head, _head + skipped);
        _consumerProgress.Set();
        return skipped;
    }

    public void Dispose()
    {
        _consumerProgress.Dispose();
    }

    private void CopyIn(ReadOnlySpan<float> interleaved)
    {
        int frames = interleaved.Length / Channels;
        long tail = _tail;
        int first = (int)Math.Min(frames, CapacityFrames - (tail & _frameMask));
        int offset = (int)(tail & _frameMask) * Channels;
        interleaved[..(first * Channels)].CopyTo(_samples.AsSpan(offset));
        if (first < frames)
        {
            interleaved[(first * Channels)..].CopyTo(_samples);
        }

        Volatile.Write(ref _tail, tail + frames);
    }
}
