using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Orbivo.Audio.Spatial;
using Orbivo.Audio.SteamAudio;
using Orbivo.Core.Audio;
using Orbivo.Core.Concurrency;
using Orbivo.Core.Playback;

namespace Orbivo.Audio.Output;

/// <summary>
/// The sound output (M5, docs/ARCHITECTURE.md "Audio"): its own thread with MMCSS priority writes the
/// <see cref="AudioRenderer"/>'s sound to a WASAPI device and publishes the audio clock.
/// </summary>
/// <remarks>
/// <para>
/// Transport calls (<see cref="Hold"/>, <see cref="Play"/>, <see cref="Pause"/>) come from the player control thread and
/// go through a lock-free queue; the output thread carries them out at its next period. Volume, mute and fader are
/// read once per period.
/// </para>
/// <para>
/// If the selected device cannot be opened or disappears, the output continues on a silent stand-in that runs on the
/// system timer, so the video plays on; the device is tried again every <see cref="RetryInterval"/>. It never switches
/// to another device by itself, except when "Windows default" is selected: then it follows the default.
/// </para>
/// <para>
/// Stereo fixed to the picture (M7) is rendered here, at the last moment before the device, so a head turn is heard as
/// soon as possible: a <see cref="SpatialStage"/> with Steam Audio per device rate, created when a device is opened.
/// Without Steam Audio the sound stays plain stereo and the status says why.
/// </para>
/// <para>
/// An Ambisonics field (M10) goes through an <see cref="AmbisonicsStage"/> per device rate, made at the same time. The
/// ring buffer therefore holds up to <see cref="AmbisonicsLayout.MaxChannels"/> samples per frame.
/// </para>
/// The period loop never blocks except on the device event and never allocates (CLAUDE.md rule 2);
/// <see cref="PeriodAllocatedBytes"/> counts it for the tests. Opening devices and logging happen outside it.
/// </remarks>
public sealed class AudioOutput : IAudioOutput, IDisposable
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    /// <summary>Ring buffer size: 2.7 s at 48 kHz (8 MB with 16 channel slots).</summary>
    public const int RingFrames = 1 << 17;

    private const int FallbackSampleRate = 48_000;
    private const int WaitTimeoutMilliseconds = 100;
    private const int StallLimitMilliseconds = 500;

    private readonly ILogger _logger;
    private readonly Func<IAudioEndpointProvider> _providerFactory;
    private readonly Func<int, IBlockSpatializer>? _spatializerFactory;
    private readonly Func<int, IAmbisonicsSpatializer>? _ambisonicsFactory;
    private readonly ListenerOrientation? _listener;
    private readonly SpscQueue<Command> _commands = new(64);
    private readonly Lock _commandLock = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _stop;

    // Control side → output thread.
    private volatile string? _selectedDeviceId;
    private int _selectionVersion;
    private volatile float _volume;
    private volatile bool _muted;
    private volatile OutputFader? _fader;

    // Output thread → status.
    private volatile int _sampleRate;
    private volatile int _deviceChannels;
    private volatile AudioEndpointState _state = AudioEndpointState.Starting;
    private volatile string _deviceName = "";
    private volatile string? _message;
    private long _underrunFrames;
    private double _latencyMilliseconds;
    private double _driftPpm;
    private long _periodAllocatedBytes;
    private long _periods;
    private volatile bool _spatialAvailable;
    private volatile bool _spatialActive;
    private double _spatialPeakLoad;
    private volatile string? _spatialProblem;
    private volatile bool _fieldBinaural;
    private volatile int _ambisonicsChannels;
    private volatile string? _ambisonicsProblem;
    private StereoPlacement _requestedPlacement = StereoPlacement.HeadLocked;

    // Output thread only.
    private IAudioEndpointProvider? _provider;
    private IAudioEndpoint? _endpoint;
    private AudioRenderer? _renderer;
    private float[] _stereo = [];
    private int _appliedSelection = -1;
    private int _seenChanges;
    private long _retryAt;
    private long _lastProgress;
    private int _spatialFailedRate;
    private int _ambisonicsFailedRate;

    /// <summary>Plays on the Windows device <paramref name="deviceId"/> (null = Windows default).</summary>
    /// <param name="deviceId">Device, or null for the Windows default.</param>
    /// <param name="volume">Volume level 0–1.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="listener">Head orientation for sound fixed to the picture; null = always looking at the front.</param>
    public AudioOutput(string? deviceId, float volume, ILogger logger, ListenerOrientation? listener = null)
        : this(
            () => new WasapiEndpointProvider(logger),
            deviceId,
            volume,
            logger,
            rate => SteamStereoSpatializer.Create(rate),
            listener,
            rate => SteamAmbisonicsSpatializer.Create(rate))
    {
    }

    /// <param name="providerFactory">Device access (simulated in tests).</param>
    /// <param name="deviceId">Device, or null for the default.</param>
    /// <param name="volume">Volume level 0–1.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="spatializerFactory">Makes the spatializer for a device rate; null = plain stereo only.</param>
    /// <param name="listener">Head orientation, or null.</param>
    /// <param name="ambisonicsFactory">Makes the Ambisonics renderer for a device rate; null = fallback pair only.</param>
    internal AudioOutput(
        Func<IAudioEndpointProvider> providerFactory,
        string? deviceId,
        float volume,
        ILogger logger,
        Func<int, IBlockSpatializer>? spatializerFactory = null,
        ListenerOrientation? listener = null,
        Func<int, IAmbisonicsSpatializer>? ambisonicsFactory = null)
    {
        _providerFactory = providerFactory;
        _spatializerFactory = spatializerFactory;
        _ambisonicsFactory = ambisonicsFactory;
        _listener = listener;
        _logger = logger;
        _selectedDeviceId = deviceId;
        _volume = Math.Clamp(volume, 0f, 1f);
        Ring = new AudioRingBuffer(AmbisonicsLayout.MaxChannels, RingFrames);
        _thread = new Thread(Run) { Name = "Orbivo audio output", IsBackground = true, Priority = ThreadPriority.Highest };
        _thread.Start();

        // The decoders need the device rate, so the first device is opened before the constructor returns.
        if (!_ready.Wait(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("The sound output did not start within 5 s");
        }
    }

    public AudioPlaybackClock Clock { get; } = new();

    public AudioRingBuffer Ring { get; }

    public int SampleRate => _sampleRate;

    /// <summary>Bytes allocated by the period loop since the start (must stay 0).</summary>
    internal long PeriodAllocatedBytes => Interlocked.Read(ref _periodAllocatedBytes);

    /// <summary>Periods handled since the start.</summary>
    internal long Periods => Interlocked.Read(ref _periods);

    public void Hold(long mediaTicks)
    {
        lock (_commandLock)
        {
            int sequence = Clock.RequestHold(mediaTicks);
            Enqueue(new Command(CommandKind.Hold, mediaTicks, sequence));
        }
    }

    public void Play()
    {
        lock (_commandLock)
        {
            Enqueue(new Command(CommandKind.Play, 0, 0));
        }
    }

    public void Pause()
    {
        lock (_commandLock)
        {
            Enqueue(new Command(CommandKind.Pause, 0, 0));
        }
    }

    public void SetStereoPlacement(StereoPlacement placement)
    {
        placement = placement with { WidthDegrees = StereoPlacement.ClampWidth(placement.WidthDegrees) };
        lock (_commandLock)
        {
            _requestedPlacement = placement;
            Enqueue(new Command(CommandKind.Placement, BitConverter.DoubleToInt64Bits(placement.WidthDegrees), placement.FixedToPicture ? 1 : 0));
        }
    }

    public void SetVolume(float level) => _volume = Math.Clamp(level, 0f, 1f);

    public void SetMuted(bool muted) => _muted = muted;

    public void SetFader(OutputFader? fader) => _fader = fader;

    public void SelectDevice(string? deviceId)
    {
        _selectedDeviceId = deviceId;
        Interlocked.Increment(ref _selectionVersion);
    }

    public AudioOutputStatus GetStatus() => new(
        _state,
        _selectedDeviceId,
        _deviceName,
        _sampleRate,
        _deviceChannels,
        Volatile.Read(ref _latencyMilliseconds),
        Interlocked.Read(ref _underrunFrames),
        Volatile.Read(ref _driftPpm),
        _volume,
        _muted,
        _message)
    {
        Spatial = new SpatialAudioStatus(
            _spatialAvailable,
            _spatialActive,
            ReadRequestedPlacement(),
            Volatile.Read(ref _spatialPeakLoad),
            _spatialProblem)
        {
            FieldBinaural = _fieldBinaural,
            AmbisonicsChannels = _ambisonicsChannels,
            AmbisonicsProblem = _ambisonicsProblem,
        },
    };

    private StereoPlacement ReadRequestedPlacement()
    {
        lock (_commandLock)
        {
            return _requestedPlacement;
        }
    }

    public void Dispose()
    {
        _stop = true;
        if (!_thread.Join(TimeSpan.FromSeconds(3)))
        {
            _logger.LogWarning("The sound output thread did not end within 3 s");
            return;
        }

        _ready.Dispose();
        Ring.Dispose();
    }

    private void Enqueue(in Command command)
    {
        var wait = Stopwatch.StartNew();
        while (!_commands.TryEnqueue(command))
        {
            // Only if the output thread is stuck: 64 commands are far more than a seek burst produces.
            if (wait.Elapsed > TimeSpan.FromSeconds(1))
            {
                _logger.LogError("The sound output does not take commands; {Kind} dropped", command.Kind);
                return;
            }

            Thread.Sleep(1);
        }
    }

    // ---------------------------------------------------------------- output thread

    private void Run()
    {
        using Mmcss? mmcss = Mmcss.Enter("Pro Audio", out int mmcssError);
        if (mmcss is null)
        {
            _logger.LogWarning("MMCSS registration failed (error {Error}); the sound runs at normal priority", mmcssError);
        }

        try
        {
            _provider = _providerFactory();
            _seenChanges = _provider.ChangeCount;
            UpdateEndpoint();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "The sound output could not start");
            _message = "Tonausgabe konnte nicht starten.";
            SwitchTo(new NullAudioEndpoint(FallbackSampleRate), AudioEndpointState.Unavailable);
        }
        finally
        {
            _ready.Set();
        }

        while (!_stop)
        {
            UpdateEndpoint();
            long before = GC.GetAllocatedBytesForCurrentThread();
            try
            {
                RunPeriod();
            }
            catch (Exception exception) when (_endpoint is { IsSilent: false })
            {
                LoseDevice(exception);
                continue;
            }
            catch (Exception exception)
            {
                // The silent stand-in has no device that could fail: a bug. Keep the clock thread alive and say so.
                _logger.LogError(exception, "The sound output loop failed");
                _message = "Interner Fehler der Tonausgabe (siehe Log).";
                Thread.Sleep(10);
                continue;
            }

            Interlocked.Add(ref _periodAllocatedBytes, GC.GetAllocatedBytesForCurrentThread() - before);
            Interlocked.Increment(ref _periods);
        }

        _endpoint?.Dispose();
        _provider?.Dispose();
        _renderer?.SetStage(null);
        _renderer?.SetAmbisonicsStage(null);
    }

    /// <summary>One device period: commands, sound, clock, status. No allocations.</summary>
    private void RunPeriod()
    {
        IAudioEndpoint endpoint = _endpoint!;
        AudioRenderer renderer = _renderer!;
        bool signalled = endpoint.WaitForPeriod(WaitTimeoutMilliseconds);
        ApplyCommands(renderer);

        long now = Stopwatch.GetTimestamp();
        bool hasPosition = endpoint.TryGetPosition(out double position, out long positionTimestamp);
        if (hasPosition)
        {
            renderer.UpdatePosition(now, position, positionTimestamp);
        }

        int writable = endpoint.GetWritableFrames();
        if (writable > 0)
        {
            long firstHeard = hasPosition
                ? positionTimestamp + (long)((renderer.WrittenFrames - position) * Stopwatch.Frequency / endpoint.SampleRate)
                : now + ((long)endpoint.BufferFrames * Stopwatch.Frequency / endpoint.SampleRate);
            float gain = _muted ? 0f : AudioGain.FromLevel(_volume);
            OutputFader? fader = _fader;
            if (fader is not null)
            {
                gain *= AudioGain.FromLevel(fader.GetLevel(firstHeard));
            }

            renderer.Render(_stereo, writable, firstHeard, gain);
            Span<byte> buffer = endpoint.GetBuffer(writable);
            SampleWriter.Write(_stereo, writable, buffer, endpoint.Channels, endpoint.Encoding);
            endpoint.ReleaseBuffer(writable);
            _lastProgress = now;
        }
        else if (!signalled && (now - _lastProgress) * 1000 / Stopwatch.Frequency > StallLimitMilliseconds && !endpoint.IsSilent)
        {
            throw new TimeoutException("The device stopped asking for data.");
        }

        if (hasPosition)
        {
            double heardNow = position + ((now - positionTimestamp) * (double)endpoint.SampleRate / Stopwatch.Frequency);
            double latency = (renderer.WrittenFrames - heardNow) * 1000.0 / endpoint.SampleRate;
            double previous = Volatile.Read(ref _latencyMilliseconds);
            Volatile.Write(ref _latencyMilliseconds, previous <= 0 ? latency : previous + ((latency - previous) * 0.05));
        }

        Interlocked.Exchange(ref _underrunFrames, renderer.UnderrunFrames);
        Volatile.Write(ref _driftPpm, renderer.Tracker.DriftPpm);
        _spatialActive = renderer.SpatialActive;
        _fieldBinaural = renderer.FieldBinaural;
        _ambisonicsChannels = renderer.AmbisonicsStage is { } field && field.SampleRate == endpoint.SampleRate ? field.MaxChannels : 0;
        double load = renderer.SourceChannels > 2 ? renderer.AmbisonicsStage?.PeakLoad ?? 0 : renderer.Stage?.PeakLoad ?? 0;
        Volatile.Write(ref _spatialPeakLoad, load);
    }

    private void ApplyCommands(AudioRenderer renderer)
    {
        while (_commands.TryDequeue(out Command command))
        {
            switch (command.Kind)
            {
                case CommandKind.Hold:
                    renderer.Hold(command.MediaTicks, command.HoldSequence);
                    break;
                case CommandKind.Play:
                    renderer.Play();
                    break;
                case CommandKind.Pause:
                    renderer.Pause();
                    break;
                case CommandKind.Placement:
                    renderer.SetPlacement(new StereoPlacement(command.HoldSequence != 0, BitConverter.Int64BitsToDouble(command.MediaTicks)));
                    break;
            }
        }
    }

    /// <summary>Opens another device if the selection, the device set or the retry timer asks for it.</summary>
    private void UpdateEndpoint()
    {
        IAudioEndpointProvider provider = _provider!;
        int selection = Volatile.Read(ref _selectionVersion);
        int changes = provider.ChangeCount;
        long now = Stopwatch.GetTimestamp();
        bool selectionChanged = selection != _appliedSelection;
        bool devicesChanged = changes != _seenChanges;
        bool retryDue = _state == AudioEndpointState.Unavailable && (devicesChanged || now >= _retryAt);
        if (!selectionChanged && !devicesChanged && !retryDue)
        {
            return;
        }

        _appliedSelection = selection;
        _seenChanges = changes;
        string? selected = _selectedDeviceId;
        if (!selectionChanged && !retryDue)
        {
            // Some device changed: only the default selection follows it.
            if (selected is not null || _endpoint is null || provider.GetDefaultDeviceId() == _endpoint.DeviceId)
            {
                return;
            }

            _logger.LogInformation("The Windows default sound device changed");
        }

        IAudioEndpoint? opened = null;
        try
        {
            opened = provider.Open(selected);
            opened.Start();
            _message = null;
            SwitchTo(opened, AudioEndpointState.Active);
            _logger.LogInformation(
                "Sound output: {Device} ({Id}), {Rate} Hz, {Channels} channels, {Encoding}, buffer {Buffer} frames{Latency}",
                opened.DeviceName,
                opened.DeviceId,
                opened.SampleRate,
                opened.Channels,
                opened.Encoding,
                opened.BufferFrames,
                opened is WasapiAudioEndpoint wasapi ? $", driver latency {wasapi.StreamLatencyMilliseconds:0.0} ms" : "");
        }
        catch (Exception exception)
        {
            opened?.Dispose();
            _retryAt = now + (long)(RetryInterval.TotalSeconds * Stopwatch.Frequency);
            if (_state != AudioEndpointState.Unavailable || selectionChanged)
            {
                _logger.LogWarning(exception, "The sound device {Device} cannot be opened; playing on without sound", selected ?? "(Windows default)");
            }

            string name = selected is null ? "Windows-Standard" : provider.GetDeviceName(selected) ?? "ausgewähltes Gerät";
            _message = $"Tonausgabe nicht verfügbar ({name}). Wiederholung alle {RetryInterval.TotalSeconds:0} s.";
            if (_endpoint is null || !_endpoint.IsSilent)
            {
                SwitchTo(new NullAudioEndpoint(_renderer?.SampleRate ?? FallbackSampleRate), AudioEndpointState.Unavailable);
            }
            else
            {
                _state = AudioEndpointState.Unavailable;
            }

            if (selected is not null)
            {
                _deviceName = name;
            }
        }
    }

    private void LoseDevice(Exception exception)
    {
        string name = _endpoint?.DeviceName ?? "?";
        _logger.LogWarning(exception, "The sound device {Device} failed; playing on without sound", name);
        _message = $"Tonausgabe ausgefallen ({name}). Wiederholung alle {RetryInterval.TotalSeconds:0} s.";
        _retryAt = Stopwatch.GetTimestamp() + (long)(RetryInterval.TotalSeconds * Stopwatch.Frequency);
        SwitchTo(new NullAudioEndpoint(_renderer?.SampleRate ?? FallbackSampleRate), AudioEndpointState.Unavailable);
        _deviceName = name;
    }

    private void SwitchTo(IAudioEndpoint endpoint, AudioEndpointState state)
    {
        if (_endpoint is not null)
        {
            try
            {
                _endpoint.Dispose();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Closing the sound device {Device} failed", _endpoint.DeviceName);
            }
        }

        if (endpoint.IsSilent)
        {
            endpoint.Start();
        }

        _endpoint = endpoint;
        if (_stereo.Length < endpoint.BufferFrames * 2)
        {
            _stereo = new float[endpoint.BufferFrames * 2];
        }

        long now = Stopwatch.GetTimestamp();
        if (_renderer is null)
        {
            _renderer = new AudioRenderer(Ring, Clock, endpoint.SampleRate, Stopwatch.Frequency);
        }
        else
        {
            if (_renderer.SampleRate != endpoint.SampleRate)
            {
                _logger.LogInformation("Sound output rate changes from {Old} to {New} Hz", _renderer.SampleRate, endpoint.SampleRate);
            }

            _renderer.Attach(endpoint.SampleRate, now);
        }

        UpdateStage(_renderer, endpoint.SampleRate);
        UpdateAmbisonicsStage(_renderer, endpoint.SampleRate);
        _lastProgress = now;
        _sampleRate = endpoint.SampleRate;
        _deviceChannels = endpoint.Channels;
        _deviceName = endpoint.DeviceName;
        _state = state;
    }

    /// <summary>
    /// Gives the renderer a spatial stage for <paramref name="sampleRate"/>: keeps the current one if the rate is the same,
    /// else creates one (loading Steam Audio and its HRTF takes a moment; this runs between periods). A failure is
    /// logged once per rate and leaves plain stereo.
    /// </summary>
    private void UpdateStage(AudioRenderer renderer, int sampleRate)
    {
        if (_spatializerFactory is null)
        {
            return;
        }

        if (renderer.Stage is { } current && current.SampleRate == sampleRate)
        {
            renderer.SetStage(current);
            return;
        }

        if (_spatialFailedRate == sampleRate)
        {
            renderer.SetStage(null);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var stage = new SpatialStage(_spatializerFactory(sampleRate), _listener);
            renderer.SetStage(stage);
            _spatialAvailable = true;
            _spatialProblem = null;
            _logger.LogInformation(
                "Sound fixed to the picture is ready: Steam Audio binaural rendering at {Rate} Hz, blocks of {Block} frames ({Latency:0.0} ms extra delay), prepared in {Milliseconds} ms",
                sampleRate,
                stage.LatencyFrames,
                stage.LatencyFrames * 1000.0 / sampleRate,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SteamAudioException or ArgumentException)
        {
            _spatialFailedRate = sampleRate;
            _spatialAvailable = false;
            _spatialProblem = $"Ton fest am Bild nicht verfügbar: Steam Audio lässt sich nicht laden ({exception.Message}). Der Ton läuft als normales Stereo.";
            _logger.LogError(exception, "Steam Audio could not be prepared for {Rate} Hz; stereo plays without spatial rendering", sampleRate);
            renderer.SetStage(null);
        }
    }

    /// <summary>
    /// Gives the renderer an Ambisonics stage for <paramref name="sampleRate"/>, like <see cref="UpdateStage"/>. A failure
    /// is logged once per rate; Ambisonics then plays through the fallback pair.
    /// </summary>
    private void UpdateAmbisonicsStage(AudioRenderer renderer, int sampleRate)
    {
        if (_ambisonicsFactory is null)
        {
            return;
        }

        if (renderer.AmbisonicsStage is { } current && current.SampleRate == sampleRate)
        {
            renderer.SetAmbisonicsStage(current);
            return;
        }

        if (_ambisonicsFailedRate == sampleRate)
        {
            renderer.SetAmbisonicsStage(null);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var stage = new AmbisonicsStage(_ambisonicsFactory(sampleRate), _listener);
            renderer.SetAmbisonicsStage(stage);
            _ambisonicsProblem = null;
            _logger.LogInformation(
                "Ambisonics is ready: Steam Audio binaural decoding up to order {Order} at {Rate} Hz, blocks of {Block} frames, prepared in {Milliseconds} ms",
                AmbisonicsLayout.GetDegree(stage.MaxChannels - 1),
                sampleRate,
                stage.LatencyFrames,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SteamAudioException or ArgumentException)
        {
            _ambisonicsFailedRate = sampleRate;
            _ambisonicsProblem = $"Ambisonics nicht binaural: Steam Audio lässt sich nicht laden ({exception.Message}). Notbehelf: zwei Richtmikrofone links und rechts, ohne Kopfbewegung.";
            _logger.LogError(exception, "Steam Audio Ambisonics could not be prepared for {Rate} Hz; Ambisonics plays through a cardioid pair", sampleRate);
            renderer.SetAmbisonicsStage(null);
        }
    }

    private enum CommandKind
    {
        Hold,
        Play,
        Pause,
        Placement,
    }

    private readonly record struct Command(CommandKind Kind, long MediaTicks, int HoldSequence);
}
