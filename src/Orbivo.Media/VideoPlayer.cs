using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Audio;
using Orbivo.Core.Commands;
using Orbivo.Core.Formats;
using Orbivo.Core.Playback;
using Orbivo.Core.Status;
using Vortice.Direct3D11;

namespace Orbivo.Media;

/// <summary>Settings of the player that do not change while it runs.</summary>
/// <param name="StereoLayout">
/// Stereo layout from the command line (<c>--stereo</c>): wins over sidecar and metadata, this session only. Null = automatic.
/// </param>
/// <param name="FrameSlots">Number of decoded frames on the GPU (docs/ARCHITECTURE.md: 6–8).</param>
/// <param name="Decoder">
/// <c>--decoder auto|hw|sw</c>. Auto: hardware if the GPU can decode the video, else software; software as well if the
/// hardware fails before the first frame.
/// </param>
public sealed record VideoPlayerOptions(StereoLayout? StereoLayout, int FrameSlots = 6, DecoderPreference Decoder = DecoderPreference.Auto)
{
    /// <summary>False skips the hardware check before opening, so the fallback after a hardware failure can be tested.</summary>
    internal bool CheckHardware { get; init; } = true;
}

/// <summary>
/// The player control thread (docs/ARCHITECTURE.md, "Threads"): reads <see cref="PlayerCommand"/>s from the UI,
/// drives the <see cref="PlaybackStateMachine"/>, opens and closes decoders, seeks, runs the clock, the loop and the
/// output fader (blackout and fade, F5), and publishes <see cref="PlayerStatus"/> snapshots. The render thread gets
/// its frames and the output brightness through <see cref="IVideoFrameSource"/>.
/// </summary>
/// <remarks>
/// With a sound output (M5), a file is opened with one <see cref="MediaDemuxer"/> for a <see cref="VideoDecoder"/> and
/// an <see cref="AudioDecoder"/>; the audio clock is the master clock then, and blackout and fade apply to the sound
/// as well (docs/DECISIONS.md, M5).
/// </remarks>
/// <remarks>
/// Every opened video has its own <see cref="VideoFrameQueue"/>. A new queue is handed to the render thread through
/// <see cref="_publishedQueue"/>; the render thread announces the queue it uses in <see cref="_renderHazard"/>
/// (a hazard pointer). An old queue is disposed only once the render thread no longer uses it. No locks on the
/// render thread (CLAUDE.md rule 1).
/// </remarks>
public sealed class VideoPlayer : IVideoFrameSource, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>A sound track that is not ready this long after the picture does not hold up the start.</summary>
    private static readonly TimeSpan AudioPrerollTimeout = TimeSpan.FromSeconds(2);

    /// <summary>The A/V deviation goes to the log this often while playing.</summary>
    private static readonly TimeSpan AvSyncLogInterval = TimeSpan.FromSeconds(10);

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ChannelReader<PlayerCommand> _commands;
    private readonly VideoPlayerOptions _options;
    private readonly ILogger _logger;
    private readonly DisplayKeepAwake _keepAwake;
    private readonly Thread _thread;
    private readonly TransportClock _clock;
    private readonly IAudioOutput? _audio;
    private readonly PlaybackStateMachine _machine = new();
    private readonly OutputFader _fader = new();
    private readonly AvSyncMeter _avSync = new(Stopwatch.Frequency);
    private readonly List<VideoFrameQueue> _retiredQueues = [];

    private MediaDemuxer? _demuxer;
    private VideoDecoder? _decoder;
    private AudioDecoder? _audioDecoder;
    private long _prerollStartTimestamp;
    private bool _audioErrorLogged;
    private long _avSyncLoggedWindow;
    private long _avSyncLogTimestamp;
    private string? _filePath;

    /// <summary>Why the loaded file is decoded in software after its hardware decoder failed; null otherwise.</summary>
    private string? _softwareFallbackReason;

    // Format of the loaded file (F10–F14), control thread only.
    private SidecarFile? _sidecar;
    private FormatOverrides _overrides = FormatOverrides.None;
    private EmbeddedFormatMetadata? _metadata;
    private (int Width, int Height) _videoSize;
    private ResolvedVideoFormat? _resolvedFormat;
    private string? _sidecarError;
    private VideoFormatStatus? _formatStatus;
    private bool _loop;
    private TimeSpan _seekPosition;
    private volatile bool _stopRequested;
    private bool _disposed;

    // Shared with the render thread.
    private VideoFrameQueue? _publishedQueue;
    private VideoFrameQueue? _renderHazard;
    private volatile bool _videoVisible;

    // Render thread only.
    private VideoFrameQueue? _renderQueue;
    private long _lastShownPts = long.MinValue;

    // Status, written by the control thread, read by the UI.
    private PlayerStatus _status = PlayerStatus.Empty;
    private VideoDecoder? _statusDecoder;

    /// <param name="device">The shared, multithread-protected D3D11 device of the renderer.</param>
    /// <param name="commands">Commands from the operator UI.</param>
    /// <param name="options">Player settings.</param>
    /// <param name="loggerFactory">Creates the player logger.</param>
    /// <param name="audio">Sound output, or null to play without sound (system timer as clock).</param>
    public VideoPlayer(
        ID3D11Device device,
        ChannelReader<PlayerCommand> commands,
        VideoPlayerOptions options,
        ILoggerFactory loggerFactory,
        IAudioOutput? audio = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _device = device;
        _context = device.ImmediateContext; // Borrowed, see Dispose.
        _commands = commands;
        _options = options;
        _logger = loggerFactory.CreateLogger<VideoPlayer>();
        _keepAwake = new DisplayKeepAwake(_logger);
        _audio = audio;
        _clock = new TransportClock(audio);
        audio?.SetFader(_fader);
        _thread = new Thread(ControlThread)
        {
            Name = "Orbivo player control",
            IsBackground = true,
        };
    }

    public void Start()
    {
        _thread.Start();
    }

    /// <summary>Current state for the operator UI. Any thread.</summary>
    public PlayerStatus GetStatus()
    {
        PlayerStatus status = Volatile.Read(ref _status) with
        {
            AudioOutput = _audio?.GetStatus(),
            ClockSource = _clock.Source,
            AvSync = _avSync.Current,
        };
        VideoDecoder? decoder = Volatile.Read(ref _statusDecoder);
        if (decoder is null)
        {
            return status with { Output = _fader.Current };
        }

        long position = Math.Max(0, _clock.MediaTicks - Math.Max(0, decoder.HasFirstFrame ? decoder.FirstPtsTicks : 0));
        if (status.Duration > TimeSpan.Zero)
        {
            position = Math.Min(position, status.Duration.Ticks);
        }

        FramePacer pacer = decoder.Queue.Pacer;
        return status with
        {
            Position = TimeSpan.FromTicks(position),
            Video = decoder.Info,
            DecodedFramesPerSecond = decoder.DecodedFramesPerSecond,
            QueuedFrames = pacer.ReadyCount,
            QueueCapacity = pacer.SlotCount,
            DroppedFrames = pacer.DroppedFrames,
            Output = _fader.Current,
            FrameDuration = TimeSpan.FromTicks(decoder.FrameDurationTicks),
        };
    }

    /// <inheritdoc />
    public bool TryGetFrame(long displayTimestamp, out VideoFrame frame)
    {
        VideoFrameQueue? queue = AcquireRenderQueue();
        if (queue is null)
        {
            frame = default;
            return false;
        }

        if (!_videoVisible)
        {
            // Black until Play, but a seek in that state needs the outdated frames' slots.
            queue.Pacer.DiscardStaleFrames();
            frame = default;
            return false;
        }

        long target = _clock.GetMediaTicks(displayTimestamp, out bool advancing);
        if (!queue.TryGetFrame(target, out frame))
        {
            return false;
        }

        if (frame.PtsTicks != _lastShownPts)
        {
            // First display of this frame: how far the heard sound (the clock) is ahead of the picture.
            _lastShownPts = frame.PtsTicks;
            if (advancing)
            {
                _avSync.Record(displayTimestamp, target - frame.PtsTicks);
            }
        }

        return true;
    }

    /// <inheritdoc />
    public float GetOutputBrightness(long displayTimestamp)
    {
        return _fader.GetBrightness(displayTimestamp);
    }

    /// <inheritdoc />
    public void ReleaseRenderThread()
    {
        _renderQueue = null;
        Interlocked.Exchange(ref _renderHazard, null);
    }

    /// <summary>
    /// Stops the control thread and frees all frames. The render threads that read frames must be stopped before.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopRequested = true;
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogWarning("Player control thread did not stop within 5 s.");
        }

        CloseDecoder();
        Interlocked.Exchange(ref _publishedQueue, null)?.Dispose();
        foreach (VideoFrameQueue queue in _retiredQueues)
        {
            queue.Dispose();
        }

        _retiredQueues.Clear();

        // _context is not disposed: Vortice hands out one shared wrapper per device, owned by the device's creator.
    }

    /// <summary>
    /// Render thread: switches to the newest published queue. The hazard pointer is set before the queue is used,
    /// and the published queue is read again afterwards, so the control thread never frees a queue in use.
    /// </summary>
    private VideoFrameQueue? AcquireRenderQueue()
    {
        VideoFrameQueue? published = Volatile.Read(ref _publishedQueue);
        if (published == _renderQueue)
        {
            return published;
        }

        while (true)
        {
            Interlocked.Exchange(ref _renderHazard, published);
            VideoFrameQueue? again = Volatile.Read(ref _publishedQueue);
            if (again == published)
            {
                break;
            }

            published = again;
        }

        _renderQueue = published;
        return published;
    }

    private void ControlThread()
    {
        _logger.LogInformation("Player control thread started.");
        Task<bool>? waitForCommand = null;
        while (!_stopRequested)
        {
            try
            {
                while (_commands.TryRead(out PlayerCommand? command))
                {
                    Handle(command);
                }

                PollDecoder();
                DisposeUnusedQueues();

                // A monitor switching off or on stalls the video decoding for seconds (DECISIONS.md, M5).
                _keepAwake.Update(_machine.State is PlaybackState.Playing or PlaybackState.Paused);
            }
            catch (Exception exception)
            {
                // Keep the player alive: report, show black, wait for the next command.
                _logger.LogError(exception, "Player control failed.");
                Fail("Interner Fehler im Player (Details im Log)");
            }

            // Wait for the next command, but look at the decoder at least every PollInterval.
            waitForCommand ??= _commands.WaitToReadAsync().AsTask();
            if (waitForCommand.Wait(PollInterval))
            {
                if (!waitForCommand.Result)
                {
                    // The UI completed the channel: no more commands, the app is closing.
                    Thread.Sleep(PollInterval);
                    continue;
                }

                waitForCommand = null;
            }
        }

        _keepAwake.Update(false);
        _logger.LogInformation("Player control thread stopped.");
    }

    private void Handle(PlayerCommand command)
    {
        _logger.LogInformation("Command {Command} in state {State}.", command.GetType().Name, _machine.State);
        switch (command)
        {
            case OpenMediaCommand open:
                _filePath = Path.GetFullPath(open.Path);
                _softwareFallbackReason = null;
                LoadSidecar(_filePath);
                Apply(_machine.Open());
                if (open.Play)
                {
                    Apply(_machine.Play());
                }

                break;
            case PlayCommand:
                Apply(_machine.Play());
                break;
            case PauseCommand:
                Apply(_machine.Pause());
                break;
            case StopCommand when _machine.State == PlaybackState.Ready:
                // Already black; Stop still means "back to the beginning" after a seek.
                SeekTo(TimeSpan.Zero);
                break;
            case StopCommand:
                Apply(_machine.Stop());
                break;
            case CloseMediaCommand:
                Apply(_machine.Close());
                break;
            case SeekCommand seek:
                SeekTo(seek.Position);
                break;
            case SetLoopCommand loop:
                _loop = loop.Enabled;
                PublishStatus(error: null);
                break;
            case SetBlackoutCommand blackout:
                _fader.SetBlackout(blackout.Black);
                break;
            case FadeCommand fade:
                _fader.FadeTo(fade.ToBlack, fade.Duration);
                break;
            case SetVolumeCommand volume:
                _audio?.SetVolume(volume.Level);
                break;
            case SetMuteCommand mute:
                _audio?.SetMuted(mute.Muted);
                break;
            case SetAudioDeviceCommand device:
                _audio?.SelectDevice(device.DeviceId);
                break;
            case SetVideoFormatCommand format:
                SetFormat(format.Overrides);
                break;
            default:
                _logger.LogWarning("Unknown command {Command} ignored.", command);
                break;
        }
    }

    private void PollDecoder()
    {
        VideoDecoder? decoder = _decoder;
        if (decoder is null)
        {
            return;
        }

        if (decoder.ErrorMessage is { } error && _machine.State != PlaybackState.Failed)
        {
            if (decoder.HardwareFailedBeforeFirstFrame && _options.Decoder == DecoderPreference.Auto && _softwareFallbackReason is null)
            {
                FallBackToSoftware(error);
                return;
            }

            Fail(error);
            return;
        }

        PollAudio();
        bool prerolled = decoder.IsPrerolled && IsAudioPrerolled();
        if (_machine.State == PlaybackState.Opening && prerolled)
        {
            if (!decoder.HasFirstFrame)
            {
                Fail("Das Video enthält keine Bilder");
                return;
            }

            _logger.LogInformation(
                "Video ready at {Start}: {Frames} frames buffered.",
                TimeSpan.FromTicks(decoder.StartTicks - decoder.FirstPtsTicks),
                decoder.Queue.Pacer.ReadyCount);
            _clock.Stop(decoder.StartTicks);
            Apply(_machine.MediaReady());
            return;
        }

        if (_machine.Seeking && prerolled)
        {
            _logger.LogInformation(
                "Seek done at {Start} (requested {Target}).",
                TimeSpan.FromTicks(decoder.StartTicks - decoder.FirstPtsTicks),
                _seekPosition);
            _clock.Stop(decoder.StartTicks);
            Apply(_machine.SeekCompleted());
            return;
        }

        if (_machine.State == PlaybackState.Playing
            && !_machine.Seeking
            && decoder.EndOfStream
            && _clock.MediaTicks >= decoder.LastPtsTicks + decoder.FrameDurationTicks)
        {
            _logger.LogInformation(
                "End of video reached. Frames shown: {Shown}, dropped: {Dropped}. Loop: {Loop}.",
                decoder.Queue.Pacer.ShownFrames,
                decoder.Queue.Pacer.DroppedFrames,
                _loop);
            if (_loop)
            {
                SeekTo(TimeSpan.Zero);
            }
            else
            {
                Apply(_machine.EndReached());
            }
        }
    }

    /// <summary>
    /// The hardware decoder rejected the video before its first frame (the check before opening did not catch it):
    /// open it again with software decoding, for the rest of this file. A seek that was waiting starts again.
    /// </summary>
    private void FallBackToSoftware(string hardwareError)
    {
        _logger.LogWarning("Hardware decoding of {Path} failed before the first frame ({Error}); opening it again with software decoding.", _filePath, hardwareError);
        _softwareFallbackReason = "Hardware-Decoder hat das Video abgelehnt";
        OpenDecoder();
        if (_machine.Seeking && _decoder is { } decoder)
        {
            decoder.RequestSeek(_seekPosition.Ticks);
        }

        PublishStatus(error: null);
    }

    /// <summary>
    /// True when the sound of the current position is buffered too (or there is none). A sound track that is late by
    /// more than <see cref="AudioPrerollTimeout"/> after the picture does not hold up the start; it is a dropout then.
    /// </summary>
    private bool IsAudioPrerolled()
    {
        AudioDecoder? audio = _audioDecoder;
        if (audio is null || audio.IsPrerolled)
        {
            return true;
        }

        if (Stopwatch.GetElapsedTime(_prerollStartTimestamp) < AudioPrerollTimeout)
        {
            return false;
        }

        _logger.LogWarning("The sound is not ready {Timeout} after the start or seek; the picture starts without it.", AudioPrerollTimeout);
        _prerollStartTimestamp = Stopwatch.GetTimestamp();
        return true;
    }

    /// <summary>Sound decoder errors, a new device rate, and the A/V log.</summary>
    private void PollAudio()
    {
        AudioDecoder? audio = _audioDecoder;
        IAudioOutput? output = _audio;
        if (audio is null || output is null || _demuxer is null)
        {
            return;
        }

        if (audio.ErrorMessage is { } error && !_audioErrorLogged)
        {
            // The picture plays on; the clock keeps following the device, which plays silence.
            _audioErrorLogged = true;
            _logger.LogError("Sound decoding failed, the video plays on without sound: {Error}", error);
        }

        int rate = output.SampleRate;
        if (rate > 0 && rate != audio.OutputSampleRate && _machine.State is not (PlaybackState.Failed or PlaybackState.NoMedia))
        {
            // The device changed its rate (other device, or Windows settings): decode for the new rate from here on.
            long origin = _decoder is { HasFirstFrame: true } video ? video.FirstPtsTicks : 0;
            TimeSpan position = TimeSpan.FromTicks(Math.Max(0, _clock.MediaTicks - origin));
            _logger.LogInformation(
                "Sound output rate changed from {Old} to {New} Hz; the sound decoder starts again at {Position}.",
                audio.OutputSampleRate,
                rate,
                position);
            audio.Dispose();
            _audioDecoder = null;
            try
            {
                AudioDecoder reopened = AudioDecoder.Open(_demuxer, output.Ring, rate, _logger);
                _audioDecoder = reopened;
                reopened.Start();
            }
            catch (MediaOpenException exception)
            {
                _logger.LogError(exception, "The sound decoder cannot start again; the video plays on without sound.");
            }

            _clock.UseAudio(_audioDecoder is not null);
            SeekTo(position);
        }

        LogAvSync();
    }

    private void LogAvSync()
    {
        AvSyncStats stats = _avSync.Current;
        if (stats.WindowEndTimestamp == 0 || stats.WindowEndTimestamp == _avSyncLoggedWindow)
        {
            return;
        }

        _avSyncLoggedWindow = stats.WindowEndTimestamp;
        bool outside = stats.WindowMinMs < -AvSyncMeter.LimitMilliseconds || stats.WindowMaxMs > AvSyncMeter.LimitMilliseconds;
        if (!outside && Stopwatch.GetElapsedTime(_avSyncLogTimestamp) < AvSyncLogInterval)
        {
            return;
        }

        _avSyncLogTimestamp = Stopwatch.GetTimestamp();
        AudioOutputStatus? output = _audio?.GetStatus();
        _logger.Log(
            outside ? LogLevel.Warning : LogLevel.Information,
            "A/V {Mean:+0.0;-0.0} ms ({Min:+0.0;-0.0} to {Max:+0.0;-0.0}) over {Frames} frames; since start {TotalMin:+0.0;-0.0} to {TotalMax:+0.0;-0.0} ms, {Outside} of {Total} outside +-{Limit} ms. Clock {Clock}, sound latency {Latency:0.0} ms, drift {Drift:+0;-0} ppm, dropouts {Underruns} frames.",
            stats.WindowMeanMs,
            stats.WindowMinMs,
            stats.WindowMaxMs,
            stats.WindowFrames,
            stats.TotalMinMs,
            stats.TotalMaxMs,
            stats.TotalOutsideLimit,
            stats.TotalFrames,
            AvSyncMeter.LimitMilliseconds,
            _clock.Source,
            output?.LatencyMilliseconds ?? 0,
            output?.DriftPpm ?? 0,
            output?.UnderrunFrames ?? 0);
    }

    /// <summary>Jumps to <paramref name="position"/> (from the start of the video), clamped to the video.</summary>
    private void SeekTo(TimeSpan position)
    {
        VideoDecoder? decoder = _decoder;
        if (decoder is null)
        {
            return;
        }

        long frame = decoder.FrameDurationTicks;
        long latest = decoder.Duration > TimeSpan.Zero ? Math.Max(0, decoder.Duration.Ticks - frame) : long.MaxValue;
        _seekPosition = TimeSpan.FromTicks(Math.Clamp(position.Ticks, 0, latest));
        Apply(_machine.Seek());
    }

    private void Apply(PlaybackActions actions)
    {
        if (actions.HasFlag(PlaybackActions.HideVideo))
        {
            _videoVisible = false;
        }

        if (actions.HasFlag(PlaybackActions.StopClock))
        {
            _clock.Stop();
        }

        if (actions.HasFlag(PlaybackActions.CloseMedia))
        {
            CloseMedia();
        }

        if (actions.HasFlag(PlaybackActions.OpenMedia) || actions.HasFlag(PlaybackActions.RestartMedia))
        {
            OpenDecoder();
        }

        if (actions.HasFlag(PlaybackActions.SeekMedia) && _decoder is { } decoder)
        {
            // "Seeking" must be visible before the clock stands at the target, or a reader of the status could take the
            // seek for done (clock at the target, not seeking) while the old frames are still shown.
            PublishStatus(error: null);
            // The clock stands at the target until the decoder has the frames there (SeekCompleted / MediaReady).
            long origin = decoder.HasFirstFrame ? decoder.FirstPtsTicks : 0;
            long target = origin + _seekPosition.Ticks;
            _clock.Stop(target);
            decoder.RequestSeek(target);
            _prerollStartTimestamp = Stopwatch.GetTimestamp();
            _avSync.Reset();
        }

        if (actions.HasFlag(PlaybackActions.StartClock))
        {
            // The clock stands at the first frame (or the seek target) since the video became ready.
            _clock.Resume();
        }

        if (actions.HasFlag(PlaybackActions.PauseClock))
        {
            _clock.Pause();
        }

        if (actions.HasFlag(PlaybackActions.ResumeClock))
        {
            _clock.Resume();
        }

        if (actions.HasFlag(PlaybackActions.ShowVideo))
        {
            _videoVisible = true;
        }

        PublishStatus(error: null);
    }

    private void OpenDecoder()
    {
        CloseDecoder();
        string path = _filePath ?? throw new InvalidOperationException("No file to open.");
        try
        {
            if (!TryOpenParts(path, withAudio: _audio is not null))
            {
                // The sound track cannot be decoded: without sound, so its packets do not pile up in the demuxer.
                TryOpenParts(path, withAudio: false);
            }

            VideoDecoder decoder = _decoder!;
            PublishQueue(decoder.Queue);
            _clock.UseAudio(_audioDecoder is not null);
            _audioErrorLogged = false;
            _prerollStartTimestamp = Stopwatch.GetTimestamp();
            _avSync.Reset();
            decoder.Start();
            _audioDecoder?.Start();
            _demuxer!.Start();
            Volatile.Write(ref _statusDecoder, decoder);
            if (_audio is not null && _audioDecoder is null)
            {
                _logger.LogInformation("{Path} plays without sound; the system timer is the clock.", path);
            }
        }
        catch (MediaOpenException exception)
        {
            _logger.LogError(exception, "Cannot play {Path}.", path);
            Fail(exception.OperatorMessage);
        }
    }

    /// <summary>
    /// Opens the demuxer and the decoders (not started). False if the sound decoder could not be opened (nothing is
    /// kept open then). Throws <see cref="MediaOpenException"/> if the video cannot be played.
    /// </summary>
    private bool TryOpenParts(string path, bool withAudio)
    {
        MediaDemuxer demuxer = MediaDemuxer.Open(path, withAudio, _logger);
        VideoDecoder? video = null;
        try
        {
            _metadata = demuxer.ReadFormatMetadata();
            _videoSize = demuxer.VideoSize;
            ResolveFormat();
            video = VideoDecoder.Open(
                demuxer,
                ownsDemuxer: false,
                _device,
                _context,
                _resolvedFormat!.Format,
                _options.FrameSlots,
                _logger,
                _softwareFallbackReason is null ? _options.Decoder : DecoderPreference.Software,
                _softwareFallbackReason,
                _options.CheckHardware);
            AudioDecoder? audio = null;
            if (withAudio && demuxer.AudioStreamIndex >= 0)
            {
                try
                {
                    audio = AudioDecoder.Open(demuxer, _audio!.Ring, _audio.SampleRate, _logger);
                }
                catch (MediaOpenException exception)
                {
                    _logger.LogWarning(exception, "The sound track of {Path} cannot be decoded; the video plays without sound.", path);
                    video.Dispose();
                    demuxer.Dispose();
                    return false;
                }
            }

            _demuxer = demuxer;
            _decoder = video;
            _audioDecoder = audio;
            return true;
        }
        catch
        {
            video?.Dispose();
            demuxer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Closes the file and forgets everything about it: the render thread gets no queue (black), the status has no
    /// video, format or A/V values. Blackout, fade and loop stay; they belong to the session, not to the video.
    /// </summary>
    private void CloseMedia()
    {
        _logger.LogInformation("Closing {Path}.", _filePath);
        CloseDecoder();
        PublishQueue(null);
        _filePath = null;
        _softwareFallbackReason = null;
        _sidecar = null;
        _overrides = FormatOverrides.None;
        _metadata = null;
        _videoSize = default;
        _resolvedFormat = null;
        _sidecarError = null;
        _formatStatus = null;
        _avSync.Reset();
    }

    private void CloseDecoder()
    {
        // Decoders first: they read the demuxer's queues.
        AudioDecoder? audio = _audioDecoder;
        _audioDecoder = null;
        audio?.Dispose();

        VideoDecoder? decoder = _decoder;
        _decoder = null;
        decoder?.Dispose();

        MediaDemuxer? demuxer = _demuxer;
        _demuxer = null;
        demuxer?.Dispose();
        _clock.UseAudio(false);
    }

    /// <summary>Hands a new queue to the render thread; the old one is disposed once the render thread lets go of it.</summary>
    private void PublishQueue(VideoFrameQueue? queue)
    {
        VideoFrameQueue? old = Interlocked.Exchange(ref _publishedQueue, queue);
        if (old is not null)
        {
            _retiredQueues.Add(old);
        }

        DisposeUnusedQueues();
    }

    private void DisposeUnusedQueues()
    {
        if (_retiredQueues.Count == 0)
        {
            return;
        }

        VideoFrameQueue? inUse = Volatile.Read(ref _renderHazard);
        for (int i = _retiredQueues.Count - 1; i >= 0; i--)
        {
            if (_retiredQueues[i] != inUse)
            {
                _retiredQueues[i].Dispose();
                _retiredQueues.RemoveAt(i);
            }
        }
    }

    private void Fail(string message)
    {
        _logger.LogWarning("Player failed: {Message}", message);
        _videoVisible = false;
        _clock.Stop();
        _machine.MediaFailed();
        CloseDecoder();
        _avSync.Reset();
        PublishStatus(message);
    }

    private void PublishStatus(string? error)
    {
        VideoDecoder? decoder = _decoder;
        Volatile.Write(ref _statusDecoder, decoder);
        Volatile.Write(ref _status, new PlayerStatus(
            _machine.State,
            _filePath,
            TimeSpan.Zero,
            decoder?.Duration ?? TimeSpan.Zero,
            decoder?.Info,
            0,
            0,
            0,
            0,
            error ?? (_machine.State == PlaybackState.Failed ? Volatile.Read(ref _status).Error : null))
        {
            Seeking = _machine.Seeking,
            Loop = _loop,
            Audio = _audioDecoder?.Info,
            Format = _formatStatus,
        });
    }

    /// <summary>Reads the sidecar of a newly loaded file; its choices hold until the next file (also across Stop).</summary>
    private void LoadSidecar(string path)
    {
        _sidecar = SidecarFile.Load(path);
        _overrides = _sidecar.Overrides;
        _metadata = null;
        _videoSize = default;
        _resolvedFormat = null;
        _sidecarError = null;
        _formatStatus = null;
        foreach (string warning in _sidecar.Warnings)
        {
            _logger.LogWarning("Sidecar {Path}: {Warning}", _sidecar.Path, warning);
        }

        if (_sidecar.Exists)
        {
            _logger.LogInformation("Sidecar {Path} read: {Overrides}.", _sidecar.Path, _overrides);
        }
    }

    /// <summary>Decides the format from metadata, sidecar and command line (needs the opened file's metadata and size).</summary>
    private void ResolveFormat()
    {
        if (_metadata is not { } metadata)
        {
            return;
        }

        _resolvedFormat = VideoFormatResolver.Resolve(metadata, _videoSize.Width, _videoSize.Height, _overrides, _options.StereoLayout);
        ResolvedVideoFormat resolved = _resolvedFormat;
        UpdateFormatStatus();
        _logger.LogInformation(
            "Format of {Path}: {Format} (sources {Sources}); metadata: {Metadata}.",
            _filePath,
            resolved.Format,
            resolved.Sources,
            metadata.Describe());
        foreach (string warning in resolved.Warnings)
        {
            _logger.LogWarning("Format of {Path}: {Warning}", _filePath, warning);
        }
    }

    /// <summary>The operator changed the format: show it at once and store it in the sidecar.</summary>
    private void SetFormat(FormatOverrides overrides)
    {
        if (_filePath is null || _sidecar is null)
        {
            _logger.LogWarning("Format change ignored: no video loaded.");
            return;
        }

        _overrides = overrides;
        ResolveFormat();
        if (_decoder is { } decoder && _resolvedFormat is { } resolved)
        {
            decoder.Queue.PictureFormat = resolved.Format;
        }

        try
        {
            _sidecar.Save(overrides);
            _sidecarError = null;
            _logger.LogInformation("Sidecar {Path} {Action}: {Overrides}.", _sidecar.Path, _sidecar.Exists ? "saved" : "removed", overrides);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogError(exception, "Cannot save the sidecar {Path}.", _sidecar.Path);
            _sidecarError = $"Sidecar nicht gespeichert: {exception.Message}";
        }

        UpdateFormatStatus();

        PublishStatus(error: null);
    }

    /// <summary>Builds the format part of the status once per change, so publishing the status stays cheap.</summary>
    private void UpdateFormatStatus()
    {
        _formatStatus = CreateFormatStatus();
    }

    private VideoFormatStatus? CreateFormatStatus()
    {
        if (_resolvedFormat is not { } resolved || _sidecar is not { } sidecar)
        {
            return null;
        }

        IReadOnlyList<string> warnings = sidecar.Warnings.Count == 0 ? resolved.Warnings : [.. sidecar.Warnings, .. resolved.Warnings];
        return new VideoFormatStatus(
            resolved.Format,
            resolved.Automatic,
            resolved.Sources,
            resolved.AutomaticSources,
            _overrides,
            _metadata?.Describe() ?? "none",
            warnings,
            sidecar.Path,
            sidecar.Exists,
            _sidecarError);
    }
}
