using System.Diagnostics;

namespace Orbivo.Audio.Output;

/// <summary>Settings of a <see cref="SimulatedAudioEndpoint"/>.</summary>
/// <param name="SampleRate">Nominal rate.</param>
/// <param name="DriftPpm">The device clock runs this much faster than the system timer.</param>
/// <param name="LatencyFrames">Frames between leaving the buffer and being heard (converter, cable).</param>
/// <param name="BufferFrames">Device buffer (50 ms: Thread.Sleep may take a whole 15.6 ms timer tick).</param>
/// <param name="RecordFrames">Stream frames to record (float stereo only), 0 = none.</param>
/// <param name="DeviceId">Id the endpoint reports.</param>
/// <param name="Channels">Channels of the mix format.</param>
/// <param name="Encoding">Sample format.</param>
/// <param name="OnsetThreshold">
/// Records the stream frame where the left channel first exceeds this after at least half a second below it (beep
/// onsets for the sync tests, without recording minutes of sound); 0 = off.
/// </param>
internal sealed record SimulatedEndpointOptions(
    int SampleRate = 48_000,
    double DriftPpm = 0,
    int LatencyFrames = 0,
    int BufferFrames = 2400,
    int RecordFrames = 0,
    string DeviceId = "sim",
    int Channels = 2,
    SampleEncoding Encoding = SampleEncoding.Float32,
    float OnsetThreshold = 0);

/// <summary>
/// A device for tests: plays in real time with a chosen drift and latency, records what it plays and counts glitches
/// (moments it had nothing to play). Nothing reaches a speaker. Can be made to fail like an unplugged device.
/// </summary>
internal sealed class SimulatedAudioEndpoint : IAudioEndpoint
{
    private readonly SimulatedEndpointOptions _options;
    private readonly byte[] _scratch;
    private readonly float[] _record;
    private readonly double _framesPerTimestampTick;
    private readonly long[] _onsets = new long[8192];
    private int _onsetCount;
    private long _quietFrames;
    private long _startTimestamp;
    private long _written;
    private long _starvedFrames;
    private volatile bool _fail;

    public SimulatedAudioEndpoint(SimulatedEndpointOptions options)
    {
        _options = options;
        _scratch = new byte[options.BufferFrames * options.Channels * SampleWriter.BytesPerSample(options.Encoding)];
        _record = new float[options.RecordFrames * 2];
        _framesPerTimestampTick = options.SampleRate * (1 + (options.DriftPpm * 1e-6)) / Stopwatch.Frequency;
    }

    public string? DeviceId => _options.DeviceId;

    public string DeviceName => $"Simuliert ({_options.DeviceId})";

    public int SampleRate => _options.SampleRate;

    public int Channels => _options.Channels;

    public SampleEncoding Encoding => _options.Encoding;

    public int BufferFrames => _options.BufferFrames;

    public bool IsSilent => false;

    public bool Started { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Frames the device had to play but nothing was written (after the start).</summary>
    public long GlitchFrames => Interlocked.Read(ref _starvedFrames);

    /// <summary>Stream frames written so far.</summary>
    public long WrittenFrames => Interlocked.Read(ref _written);

    /// <summary>Recorded stream frames (interleaved L/R, from stream frame 0).</summary>
    public ReadOnlySpan<float> Recorded => _record.AsSpan(0, (int)Math.Min(WrittenFrames, _options.RecordFrames) * 2);

    /// <summary>Stream frames of the onsets found so far (see <see cref="SimulatedEndpointOptions.OnsetThreshold"/>).</summary>
    public long[] Onsets => _onsets.AsSpan(0, Volatile.Read(ref _onsetCount)).ToArray();

    /// <summary>From now on every call throws, as with an unplugged device.</summary>
    public void Fail() => _fail = true;

    /// <summary>When stream frame <paramref name="streamFrame"/> is heard (valid while there were no glitches).</summary>
    public long HeardTimestamp(long streamFrame) =>
        _startTimestamp + (long)((streamFrame + BufferFrames + _options.LatencyFrames + GlitchFrames) / _framesPerTimestampTick);

    public void Start()
    {
        ThrowIfFailed();
        _startTimestamp = Stopwatch.GetTimestamp();
        Started = true;
    }

    public bool WaitForPeriod(int timeoutMilliseconds)
    {
        ThrowIfFailed();
        Thread.Sleep(Math.Min(2, Math.Max(1, timeoutMilliseconds)));
        return true;
    }

    public int GetWritableFrames()
    {
        ThrowIfFailed();
        return BufferFrames - (int)(BufferFrames + _written - (long)Played(Stopwatch.GetTimestamp()));
    }

    public Span<byte> GetBuffer(int frames)
    {
        ThrowIfFailed();
        return _scratch.AsSpan(0, frames * Channels * SampleWriter.BytesPerSample(Encoding));
    }

    public void ReleaseBuffer(int frames)
    {
        ThrowIfFailed();
        if (Encoding == SampleEncoding.Float32 && Channels == 2 && _written < _options.RecordFrames)
        {
            int count = (int)Math.Min(frames, _options.RecordFrames - _written);
            System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_scratch.AsSpan(0, count * 8))
                .CopyTo(_record.AsSpan((int)(_written * 2)));
        }

        if (_options.OnsetThreshold > 0 && Encoding == SampleEncoding.Float32 && Channels == 2)
        {
            DetectOnsets(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(_scratch.AsSpan(0, frames * 8)));
        }

        Interlocked.Add(ref _written, frames);
    }

    public bool TryGetPosition(out double frames, out long timestamp)
    {
        ThrowIfFailed();
        timestamp = Stopwatch.GetTimestamp();
        frames = Played(timestamp) - BufferFrames - _options.LatencyFrames;
        return true;
    }

    public void Dispose()
    {
        Disposed = true;
    }

    /// <summary>Frames played since the start, the silent pre-fill of <see cref="BufferFrames"/> included.</summary>
    private double Played(long now)
    {
        double played = ((now - _startTimestamp) * _framesPerTimestampTick) - _starvedFrames;
        long available = BufferFrames + _written;
        if (played > available)
        {
            Interlocked.Add(ref _starvedFrames, (long)Math.Ceiling(played - available));
            played = available;
        }

        return played;
    }

    private void DetectOnsets(ReadOnlySpan<float> stereo)
    {
        long quietNeeded = SampleRate / 2;
        for (int i = 0; i < stereo.Length / 2; i++)
        {
            if (Math.Abs(stereo[2 * i]) <= _options.OnsetThreshold)
            {
                _quietFrames++;
                continue;
            }

            if (_quietFrames >= quietNeeded && _onsetCount < _onsets.Length)
            {
                _onsets[_onsetCount] = _written + i;
                Volatile.Write(ref _onsetCount, _onsetCount + 1);
            }

            _quietFrames = 0;
        }
    }

    private void ThrowIfFailed()
    {
        if (_fail)
        {
            throw new InvalidOperationException("Simulated device lost.");
        }
    }
}
