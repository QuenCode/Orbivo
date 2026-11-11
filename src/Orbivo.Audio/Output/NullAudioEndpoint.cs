using System.Diagnostics;
using Orbivo.Core.Localization;

namespace Orbivo.Audio.Output;

/// <summary>
/// The silent stand-in while no device is available: consumes frames at the nominal rate of the system timer, so the
/// clock runs and the video plays on without sound (decision M5). Nothing written here is heard.
/// </summary>
internal sealed class NullAudioEndpoint : IAudioEndpoint
{
    private const int BufferMilliseconds = 40;
    private const int PeriodMilliseconds = 10;

    private readonly byte[] _scratch;
    private long _startTimestamp;
    private long _written;
    private long _starvedFrames;

    public NullAudioEndpoint(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        SampleRate = sampleRate;
        BufferFrames = sampleRate * BufferMilliseconds / 1000;
        _scratch = new byte[BufferFrames * Channels * sizeof(float)];
    }

    public string? DeviceId => null;

    public string DeviceName => CoreText.SoundNoDevice;

    public int SampleRate { get; }

    public int Channels => 2;

    public SampleEncoding Encoding => SampleEncoding.Float32;

    public int BufferFrames { get; }

    public bool IsSilent => true;

    public void Start()
    {
        _startTimestamp = Stopwatch.GetTimestamp();
    }

    public bool WaitForPeriod(int timeoutMilliseconds)
    {
        Thread.Sleep(Math.Min(PeriodMilliseconds, Math.Max(1, timeoutMilliseconds)));
        return true;
    }

    public int GetWritableFrames() => BufferFrames - (int)(BufferFrames + _written - Played(Stopwatch.GetTimestamp()));

    public Span<byte> GetBuffer(int frames) => _scratch.AsSpan(0, frames * Channels * sizeof(float));

    public void ReleaseBuffer(int frames) => _written += frames;

    public bool TryGetPosition(out double frames, out long timestamp)
    {
        timestamp = Stopwatch.GetTimestamp();
        frames = Played(timestamp) - BufferFrames;
        return true;
    }

    public void Dispose()
    {
    }

    /// <summary>Frames played since the start, the silent pre-fill of <see cref="BufferFrames"/> included.</summary>
    private long Played(long now)
    {
        long played = ((now - _startTimestamp) * SampleRate / Stopwatch.Frequency) - _starvedFrames;
        long available = BufferFrames + _written;
        if (played > available)
        {
            // Nothing was written in time (the thread was held up): the stand-in waits, as a device would play silence.
            _starvedFrames += played - available;
            played = available;
        }

        return played;
    }
}
