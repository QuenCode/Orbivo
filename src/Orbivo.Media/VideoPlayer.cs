using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Commands;
using Orbivo.Core.Formats;
using Orbivo.Core.Playback;
using Orbivo.Core.Status;
using Vortice.Direct3D11;

namespace Orbivo.Media;

/// <summary>Settings of the player that do not change while it runs.</summary>
/// <param name="StereoLayout">Stereo layout chosen on the command line, or null to guess it from the video size.</param>
/// <param name="FrameSlots">Number of decoded frames on the GPU (docs/ARCHITECTURE.md: 6–8).</param>
public sealed record VideoPlayerOptions(StereoLayout? StereoLayout, int FrameSlots = 6);

/// <summary>
/// The player control thread (docs/ARCHITECTURE.md, "Threads"): reads <see cref="PlayerCommand"/>s from the UI,
/// drives the <see cref="PlaybackStateMachine"/>, opens and closes decoders, seeks, runs the clock, the loop and the
/// output fader (blackout and fade, F5), and publishes <see cref="PlayerStatus"/> snapshots. The render thread gets
/// its frames and the output brightness through <see cref="IVideoFrameSource"/>.
/// </summary>
/// <remarks>
/// Every opened video has its own <see cref="VideoFrameQueue"/>. A new queue is handed to the render thread through
/// <see cref="_publishedQueue"/>; the render thread announces the queue it uses in <see cref="_renderHazard"/>
/// (a hazard pointer). An old queue is disposed only once the render thread no longer uses it. No locks on the
/// render thread (CLAUDE.md rule 1).
/// </remarks>
public sealed class VideoPlayer : IVideoFrameSource, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(20);

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ChannelReader<PlayerCommand> _commands;
    private readonly VideoPlayerOptions _options;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly SystemPlaybackClock _clock = new();
    private readonly PlaybackStateMachine _machine = new();
    private readonly OutputFader _fader = new();
    private readonly List<VideoFrameQueue> _retiredQueues = [];

    private VideoDecoder? _decoder;
    private string? _filePath;
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

    // Status, written by the control thread, read by the UI.
    private PlayerStatus _status = PlayerStatus.Empty;
    private VideoDecoder? _statusDecoder;

    /// <param name="device">The shared, multithread-protected D3D11 device of the renderer.</param>
    /// <param name="commands">Commands from the operator UI.</param>
    /// <param name="options">Player settings.</param>
    /// <param name="loggerFactory">Creates the player logger.</param>
    public VideoPlayer(ID3D11Device device, ChannelReader<PlayerCommand> commands, VideoPlayerOptions options, ILoggerFactory loggerFactory)
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
        PlayerStatus status = Volatile.Read(ref _status);
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

        return queue.TryGetFrame(_clock.GetMediaTicks(displayTimestamp), out frame);
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

        _logger.LogInformation("Player control thread stopped.");
    }

    private void Handle(PlayerCommand command)
    {
        _logger.LogInformation("Command {Command} in state {State}.", command.GetType().Name, _machine.State);
        switch (command)
        {
            case OpenMediaCommand open:
                _filePath = Path.GetFullPath(open.Path);
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
            Fail(error);
            return;
        }

        if (_machine.State == PlaybackState.Opening && decoder.IsPrerolled)
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

        if (_machine.Seeking && decoder.IsPrerolled)
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

        if (actions.HasFlag(PlaybackActions.OpenMedia) || actions.HasFlag(PlaybackActions.RestartMedia))
        {
            OpenDecoder();
        }

        if (actions.HasFlag(PlaybackActions.SeekMedia) && _decoder is { } decoder)
        {
            // The clock stands at the target until the decoder has the frames there (SeekCompleted / MediaReady).
            long origin = decoder.HasFirstFrame ? decoder.FirstPtsTicks : 0;
            long target = origin + _seekPosition.Ticks;
            _clock.Stop(target);
            decoder.RequestSeek(target);
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
            VideoDecoder decoder = VideoDecoder.Open(path, _device, _context, _options.StereoLayout, _options.FrameSlots, _logger);
            _decoder = decoder;
            PublishQueue(decoder.Queue);
            decoder.Start();
            Volatile.Write(ref _statusDecoder, decoder);
        }
        catch (MediaOpenException exception)
        {
            _logger.LogError(exception, "Cannot play {Path}.", path);
            Fail(exception.OperatorMessage);
        }
    }

    private void CloseDecoder()
    {
        VideoDecoder? decoder = _decoder;
        if (decoder is null)
        {
            return;
        }

        _decoder = null;
        decoder.Dispose();
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
        });
    }
}
