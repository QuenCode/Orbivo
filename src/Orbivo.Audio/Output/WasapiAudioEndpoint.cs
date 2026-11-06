using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Orbivo.Audio.Output;

/// <summary>
/// A Windows audio device in shared mode, event-driven, with a buffer of about <see cref="BufferMilliseconds"/>
/// (docs/DECISIONS.md, M5). Plays in the device's mix format; the Windows mixer keeps working for other programs.
/// </summary>
/// <remarks>
/// Built on NAudio's thin COM wrappers (AudioClient, AudioRenderClient, AudioClockClient), not on its player classes:
/// the output thread runs its own loop, so it controls timing and allocations. Positions come from IAudioClock, which
/// reports which frame the device plays at which performance-counter time.
/// </remarks>
internal sealed unsafe class WasapiAudioEndpoint : IAudioEndpoint
{
    public const int BufferMilliseconds = 20;

    private static readonly Guid SubTypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubTypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly ILogger _logger;
    private readonly MMDevice _device;
    private readonly AudioClient _client;
    private readonly AudioRenderClient _render;
    private readonly AudioClockClient _audioClock;
    private readonly AutoResetEvent _period = new(false);
    private readonly double _clockFrequency;
    private readonly int _bytesPerFrame;
    private bool _started;

    private WasapiAudioEndpoint(MMDevice device, AudioClient client, SampleEncoding encoding, ILogger logger)
    {
        _logger = logger;
        _device = device;
        _client = client;
        WaveFormat format = client.MixFormat;
        SampleRate = format.SampleRate;
        Channels = format.Channels;
        Encoding = encoding;
        _bytesPerFrame = format.BlockAlign;
        DeviceId = device.ID;
        DeviceName = device.FriendlyName;

        client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.NoPersist,
            BufferMilliseconds * TimeSpan.TicksPerMillisecond,
            0,
            format,
            Guid.Empty);
        client.SetEventHandle(_period.SafeWaitHandle.DangerousGetHandle());
        BufferFrames = client.BufferSize;
        _render = client.AudioRenderClient;
        _audioClock = client.AudioClockClient;
        _clockFrequency = _audioClock.Frequency;
        StreamLatencyMilliseconds = client.StreamLatency / (double)TimeSpan.TicksPerMillisecond;
    }

    public string? DeviceId { get; }

    public string DeviceName { get; }

    public int SampleRate { get; }

    public int Channels { get; }

    public SampleEncoding Encoding { get; }

    public int BufferFrames { get; }

    public bool IsSilent => false;

    /// <summary>Latency the driver reports on top of the buffer (for the log).</summary>
    public double StreamLatencyMilliseconds { get; }

    /// <summary>Opens <paramref name="device"/>; takes ownership of it (also on failure).</summary>
    public static WasapiAudioEndpoint Open(MMDevice device, ILogger logger)
    {
        AudioClient? client = null;
        try
        {
            client = device.CreateAudioClient();
            SampleEncoding encoding = GetEncoding(client.MixFormat);
            return new WasapiAudioEndpoint(device, client, encoding, logger);
        }
        catch
        {
            client?.Dispose();
            device.Dispose();
            throw;
        }
    }

    /// <summary>The sample format of a mix format; throws for formats the player cannot write.</summary>
    public static SampleEncoding GetEncoding(WaveFormat format)
    {
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible extensible && extensible.SubFormat == SubTypeFloat);
        bool isPcm = format.Encoding == WaveFormatEncoding.Pcm
            || (format is WaveFormatExtensible pcm && pcm.SubFormat == SubTypePcm);
        int bytesPerSample = format.Channels > 0 ? format.BlockAlign / format.Channels : 0;
        if (isFloat && bytesPerSample == 4)
        {
            return SampleEncoding.Float32;
        }

        if (isPcm)
        {
            switch (bytesPerSample)
            {
                case 2:
                    return SampleEncoding.Int16;
                case 3:
                    return SampleEncoding.Int24;
                case 4:
                    return SampleEncoding.Int32;
            }
        }

        throw new NotSupportedException($"Mix format not supported: {format}");
    }

    /// <summary>Volume of this stream only (not the Windows volume); the device test plays at 0.</summary>
    public void SetStreamVolume(float volume)
    {
        AudioStreamVolume streamVolume = _client.AudioStreamVolume;
        var volumes = new float[streamVolume.ChannelCount];
        Array.Fill(volumes, volume);
        streamVolume.SetAllVolumes(volumes);
    }

    public void Start()
    {
        // Pre-fill with silence, so the device has something to play until the first period is written.
        _render.GetBuffer(BufferFrames);
        _render.ReleaseBuffer(BufferFrames, AudioClientBufferFlags.Silent);
        _client.Start();
        _started = true;
    }

    public bool WaitForPeriod(int timeoutMilliseconds) => _period.WaitOne(timeoutMilliseconds);

    public int GetWritableFrames() => BufferFrames - _client.CurrentPadding;

    public Span<byte> GetBuffer(int frames)
    {
        IntPtr buffer = _render.GetBuffer(frames);
        return new Span<byte>((void*)buffer, frames * _bytesPerFrame);
    }

    public void ReleaseBuffer(int frames) => _render.ReleaseBuffer(frames, AudioClientBufferFlags.None);

    public bool TryGetPosition(out double frames, out long timestamp)
    {
        if (!_audioClock.GetPosition(out ulong position, out ulong qpcPosition))
        {
            frames = 0;
            timestamp = 0;
            return false;
        }

        // The position counts the silent pre-fill, which lies before stream frame 0. The QPC position is in 100 ns.
        frames = (position * (double)SampleRate / _clockFrequency) - BufferFrames;
        timestamp = (long)(qpcPosition * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond);
        return true;
    }

    public void Dispose()
    {
        if (_started)
        {
            try
            {
                _client.Stop();
            }
            catch (Exception exception)
            {
                // The device is often already gone when the stream is disposed; the caller has reported why.
                _logger.LogDebug(exception, "Stopping the stream of {Device} failed", DeviceName);
            }
        }

        _audioClock.Dispose();
        _render.Dispose();
        _client.Dispose();
        _device.Dispose();
        _period.Dispose();
    }
}
