namespace Orbivo.Audio.Output;

/// <summary>
/// A device stream the audio output thread writes to: a WASAPI endpoint, the silent stand-in while no device is
/// available, or a simulated device in tests. Every method belongs to the audio output thread.
/// </summary>
/// <remarks>
/// Frames written to the stream are counted from 0 ("stream frames"). <see cref="TryGetPosition"/> tells which stream
/// frame is heard at which time; the audio clock follows it. The silence an endpoint pre-fills at <see cref="Start"/>
/// lies before stream frame 0 (positions are negative while it plays). Methods that talk to a device throw if the device is gone
/// (unplugged, disabled); the output thread then switches to the silent stand-in.
/// </remarks>
internal interface IAudioEndpoint : IDisposable
{
    /// <summary>Windows endpoint id, or null for the silent stand-in.</summary>
    string? DeviceId { get; }

    string DeviceName { get; }

    int SampleRate { get; }

    int Channels { get; }

    SampleEncoding Encoding { get; }

    /// <summary>Size of the device buffer in frames.</summary>
    int BufferFrames { get; }

    /// <summary>True if nothing written here is heard (no device).</summary>
    bool IsSilent { get; }

    /// <summary>Starts the stream (the buffer is pre-filled with silence).</summary>
    void Start();

    /// <summary>Waits until the device wants data (the WASAPI event), at most <paramref name="timeoutMilliseconds"/>.</summary>
    bool WaitForPeriod(int timeoutMilliseconds);

    /// <summary>Frames that fit into the device buffer now.</summary>
    int GetWritableFrames();

    /// <summary>The device memory for the next <paramref name="frames"/> frames; release it with <see cref="ReleaseBuffer"/>.</summary>
    Span<byte> GetBuffer(int frames);

    void ReleaseBuffer(int frames);

    /// <summary>The stream frame heard at <paramref name="timestamp"/> (Stopwatch), with fraction.</summary>
    bool TryGetPosition(out double frames, out long timestamp);
}
