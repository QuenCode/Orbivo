using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;

namespace Orbivo.Media;

/// <summary>
/// The demux thread of one opened file (docs/ARCHITECTURE.md, "Threads"): reads packets and hands them to the video
/// and the audio decode thread through two <see cref="PacketQueue"/>s. Seeks run here: the demuxer jumps to the
/// keyframe before the target and puts a seek marker into both queues, so each decoder knows where the new data begins.
/// </summary>
/// <remarks>
/// It reads on while one of the queues holds less than <see cref="QueueTargetTicks"/> of packets and stops at
/// <see cref="MaxQueuedBytes"/>, so a full video queue never starves the sound and the other way round.
/// <para>
/// Media time is common to both streams: ticks from the start of the video stream (<see cref="OriginTicks"/>).
/// </para>
/// </remarks>
internal sealed unsafe class MediaDemuxer : IDisposable
{
    /// <summary>Each queue is filled to about this much playing time.</summary>
    public static readonly long QueueTargetTicks = TimeSpan.TicksPerSecond;

    /// <summary>Upper limit for both queues together (8K video has about 12 MB per second).</summary>
    public const long MaxQueuedBytes = 64L * 1024 * 1024;

    /// <summary>Packets without a duration: at most this many per queue.</summary>
    private const int MaxQueuedPackets = 240;

    private static readonly AVRational MediaTimeBase = new() { num = 1, den = (int)TimeSpan.TicksPerSecond };
    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(50);

    private readonly ILogger _logger;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly AVRational _videoTimeBase;
    private readonly long _videoStartPts;

    private AVFormatContext* _format;
    private AVPacket* _packet;
    private volatile bool _stopRequested;
    private bool _disposed;
    private string? _errorMessage;

    // Control thread → demux thread.
    private int _requestedSeekId;
    private long _requestedSeekTicks;
    private int _reseekPending;
    private int _reseekId;
    private long _reseekTicks;

    // Demux thread only.
    private int _handledSeekId;
    private bool _ended;

    private MediaDemuxer(string path, AVFormatContext* format, int videoStreamIndex, int audioStreamIndex, ILogger logger)
    {
        Path = path;
        _format = format;
        _logger = logger;
        VideoStreamIndex = videoStreamIndex;
        AudioStreamIndex = audioStreamIndex;
        AVStream* video = format->streams[videoStreamIndex];
        _videoTimeBase = video->time_base;
        _videoStartPts = video->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : video->start_time;
        OriginTicks = ffmpeg.av_rescale_q(_videoStartPts, _videoTimeBase, MediaTimeBase);
        VideoQueue = new PacketQueue(video->time_base, _wake);
        if (audioStreamIndex >= 0)
        {
            AudioQueue = new PacketQueue(format->streams[audioStreamIndex]->time_base, _wake);
        }

        _packet = ffmpeg.av_packet_alloc();
        _thread = new Thread(DemuxThread)
        {
            Name = "Orbivo demux",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public string Path { get; }

    /// <summary>The opened file. Decoders read stream parameters from it before <see cref="Start"/>.</summary>
    public AVFormatContext* Format => _format;

    public int VideoStreamIndex { get; }

    /// <summary>Audio stream that is demuxed, or −1 (no sound track, or sound not wanted).</summary>
    public int AudioStreamIndex { get; }

    public PacketQueue VideoQueue { get; }

    public PacketQueue? AudioQueue { get; }

    /// <summary>Start of the video stream in container time; media time 0 of both streams.</summary>
    public long OriginTicks { get; }

    /// <summary>Id of the latest seek asked for (0 = the start after opening).</summary>
    public int RequestedSeekId => Volatile.Read(ref _requestedSeekId);

    /// <summary>German error text if reading failed, else null.</summary>
    public string? ErrorMessage => Volatile.Read(ref _errorMessage);

    /// <summary>
    /// Opens <paramref name="path"/>. Throws <see cref="MediaOpenException"/> if it is not a video file.
    /// </summary>
    /// <param name="path">Video file.</param>
    /// <param name="withAudio">Demux the best sound track as well (only if a sound output exists).</param>
    /// <param name="logger">Receives errors.</param>
    public static MediaDemuxer Open(string path, bool withAudio, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(logger);
        if (!File.Exists(path))
        {
            throw new MediaOpenException($"Video file {path} not found.", "Datei nicht gefunden");
        }

        AVFormatContext* format = null;
        try
        {
            int result = ffmpeg.avformat_open_input(&format, path, null, null);
            if (result < 0)
            {
                throw new MediaOpenException(
                    $"avformat_open_input({path}) failed: {FFmpegError.Describe(result)}.",
                    "Datei kann nicht geöffnet werden (kein bekanntes Videoformat?)");
            }

            FFmpegError.Check(ffmpeg.avformat_find_stream_info(format, null), "avformat_find_stream_info");

            int video = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (video < 0)
            {
                throw new MediaOpenException($"{path} contains no video stream.", "Datei enthält kein Video");
            }

            int audio = withAudio ? ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, video, null, 0) : -1;
            if (audio < 0)
            {
                audio = -1;
            }

            for (int i = 0; i < format->nb_streams; i++)
            {
                format->streams[i]->discard = i == video || i == audio ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
            }

            var demuxer = new MediaDemuxer(path, format, video, audio, logger);
            format = null;
            return demuxer;
        }
        catch (FFmpegException exception)
        {
            throw new MediaOpenException(exception.Message, "Video kann nicht gelesen werden (Details im Log)", exception);
        }
        finally
        {
            if (format is not null)
            {
                ffmpeg.avformat_close_input(&format);
            }
        }
    }

    public void Start()
    {
        _thread.Start();
    }

    /// <summary>
    /// Control thread: jump to <paramref name="targetTicks"/> (media time). Returns the id of the seek; both decoders
    /// see it in their seek markers. Replaces a seek that is still running.
    /// </summary>
    public int RequestSeek(long targetTicks)
    {
        Volatile.Write(ref _requestedSeekTicks, targetTicks);
        int id = Interlocked.Increment(ref _requestedSeekId);
        _wake.Set();
        return id;
    }

    /// <summary>
    /// Video decode thread: within seek <paramref name="seekId"/>, read the video again from
    /// <paramref name="targetTicks"/> (the target lay after the last frame). The audio queue is not flushed;
    /// the audio decoder drops the packets it already had by their time.
    /// </summary>
    public void RequestVideoReseek(int seekId, long targetTicks)
    {
        Volatile.Write(ref _reseekId, seekId);
        Volatile.Write(ref _reseekTicks, targetTicks);
        Volatile.Write(ref _reseekPending, 1);
        _wake.Set();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopRequested = true;
        _wake.Set();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogError("Demux thread for {Path} did not stop within 5 s; its resources are not freed.", Path);
            return;
        }

        VideoQueue.Dispose();
        AudioQueue?.Dispose();
        AVPacket* packet = _packet;
        ffmpeg.av_packet_free(&packet);
        _packet = null;
        AVFormatContext* format = _format;
        ffmpeg.avformat_close_input(&format);
        _format = null;
        _wake.Dispose();
    }

    private void DemuxThread()
    {
        try
        {
            DemuxLoop();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Reading {Path} failed.", Path);
            Volatile.Write(ref _errorMessage, "Lesen der Datei fehlgeschlagen (Details im Log)");
        }
    }

    private void DemuxLoop()
    {
        while (!_stopRequested)
        {
            int requested = Volatile.Read(ref _requestedSeekId);
            if (requested != _handledSeekId)
            {
                long target = Volatile.Read(ref _requestedSeekTicks);
                SeekTo(target);
                VideoQueue.Flush(PacketEntryKind.Seek, requested, target);
                AudioQueue?.Flush(PacketEntryKind.Seek, requested, target);
                _handledSeekId = requested;
                continue;
            }

            if (Interlocked.Exchange(ref _reseekPending, 0) == 1 && Volatile.Read(ref _reseekId) == _handledSeekId)
            {
                long target = Volatile.Read(ref _reseekTicks);
                SeekTo(target);
                VideoQueue.Flush(PacketEntryKind.Reseek, _handledSeekId, target);
                continue;
            }

            if (_ended || !NeedsMorePackets())
            {
                _wake.WaitOne(IdleWait);
                continue;
            }

            int result = ffmpeg.av_read_frame(_format, _packet);
            if (result == ffmpeg.AVERROR_EOF)
            {
                VideoQueue.EnqueueMarker(PacketEntryKind.End, _handledSeekId, 0);
                AudioQueue?.EnqueueMarker(PacketEntryKind.End, _handledSeekId, 0);
                _ended = true;
                continue;
            }

            FFmpegError.Check(result, "av_read_frame");
            PacketQueue? queue = _packet->stream_index == VideoStreamIndex ? VideoQueue
                : _packet->stream_index == AudioStreamIndex ? AudioQueue
                : null;
            if (queue is null)
            {
                ffmpeg.av_packet_unref(_packet);
                continue;
            }

            AVPacket* owned = ffmpeg.av_packet_alloc();
            ffmpeg.av_packet_move_ref(owned, _packet);
            queue.EnqueuePacket(owned);
        }

        VideoQueue.WakeConsumer();
        AudioQueue?.WakeConsumer();
    }

    private bool NeedsMorePackets()
    {
        long bytes = VideoQueue.Bytes + (AudioQueue?.Bytes ?? 0);
        if (bytes >= MaxQueuedBytes)
        {
            return false;
        }

        return NeedsMore(VideoQueue) || (AudioQueue is not null && NeedsMore(AudioQueue));
    }

    private static bool NeedsMore(PacketQueue queue) =>
        queue.DurationTicks < QueueTargetTicks && queue.Count < MaxQueuedPackets;

    /// <summary>Jumps to the keyframe at or before <paramref name="targetTicks"/> in the video stream.</summary>
    private void SeekTo(long targetTicks)
    {
        long timestamp = ffmpeg.av_rescale_q(targetTicks, MediaTimeBase, _videoTimeBase) + _videoStartPts;
        FFmpegError.Check(
            ffmpeg.av_seek_frame(_format, VideoStreamIndex, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD),
            "av_seek_frame");
        _ended = false;
    }
}
