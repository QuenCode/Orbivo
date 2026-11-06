using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Formats;
using Orbivo.Core.Status;
using Vortice.Direct3D11;
using Vortice.Mathematics;
using DxgiFormat = Vortice.DXGI.Format;
using ID3D11Device = Vortice.Direct3D11.ID3D11Device;
using ID3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using VideoColor = Orbivo.Core.Formats.VideoColor;

namespace Orbivo.Media;

/// <summary>
/// D3D11VA hardware decoding of the video stream on the shared device, on its own decode thread
/// (docs/ARCHITECTURE.md, "Datenfluss Video"). Packets come from the video queue of a <see cref="MediaDemuxer"/>.
/// Each decoded frame is copied into a slot of <see cref="Queue"/> and released at once, so the decoder's own
/// texture pool never runs dry.
/// </summary>
/// <remarks>
/// Disposing stops the thread and frees FFmpeg; the <see cref="Queue"/> is owned by the caller, because the render
/// thread may still be showing one of its frames.
/// <para>
/// Seeking: the demuxer jumps and puts a seek marker into the queue. At the marker the decoder starts a new frame
/// generation in the <see cref="FramePacer"/>, flushes the codec and decodes forward, dropping frames that end before
/// the target without copying them. The first published frame is the one shown at the target. Packets and frames
/// between the seek request and its marker belong to the old position and are dropped. After the end of the file the
/// thread waits for a seek instead of ending.
/// </para>
/// </remarks>
internal sealed unsafe class VideoDecoder : IDisposable
{
    /// <summary>AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX from libavcodec/codec.h (an anonymous enum, not in the bindings).</summary>
    private const int HardwareConfigMethodDeviceContext = 0x01;

    private static readonly AVRational MediaTimeBase = new() { num = 1, den = (int)TimeSpan.TicksPerSecond };
    private static readonly TimeSpan SlotWaitTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// A seek target at most this much before the start of a frame counts as that frame. Covers rounding of frame
    /// steps (30 fps is not a whole number of ticks) and containers with millisecond timestamps (Matroska).
    /// </summary>
    private static readonly long SeekSnapTicks = TimeSpan.TicksPerMillisecond;

    private readonly ILogger _logger;
    private readonly ID3D11DeviceContext _context;
    private readonly string _path;
    private readonly MediaDemuxer _demuxer;
    private readonly bool _ownsDemuxer;
    private readonly AVRational _streamTimeBase;
    private readonly long _streamStartPts;
    private readonly VideoColorFormat _colorHint;
    private readonly Thread _thread;

    private AVCodecContext* _codec;
    private AVFrame* _frame;
    private ID3D11Texture2D? _decoderTexture;

    private volatile bool _stopRequested;
    private bool _disposed;

    // Written by the decode thread, read by the control thread.
    private long _firstPtsTicks = long.MinValue;
    private long _lastPtsTicks = long.MinValue;
    private int _endOfStream;
    private string? _errorMessage;
    private double _decodedFramesPerSecond;
    private VideoStreamInfo _info;
    private int _activeSeekId;
    private long _activeSeekTargetTicks;
    private int _framesInGeneration;
    private long _generationStartTicks = long.MinValue;

    // Decode thread only. Seek id 0 = decode from the start after opening.
    private bool _inputFinished;
    private bool _awaitingReseek;
    private long _skipUntilTicks = long.MinValue;
    private long _lastSkippedPtsTicks = long.MinValue;

    private VideoDecoder(
        MediaDemuxer demuxer,
        bool ownsDemuxer,
        VideoFrameQueue queue,
        ID3D11DeviceContext context,
        AVCodecContext* codec,
        VideoStreamInfo info,
        TimeSpan duration,
        long frameDurationTicks,
        ILogger logger)
    {
        _demuxer = demuxer;
        _ownsDemuxer = ownsDemuxer;
        _path = demuxer.Path;
        Queue = queue;
        _context = context;
        _codec = codec;
        AVStream* stream = demuxer.Format->streams[demuxer.VideoStreamIndex];
        _streamTimeBase = stream->time_base;
        _streamStartPts = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time;
        _colorHint = info.Color;
        _info = info;
        Duration = duration;
        FrameDurationTicks = frameDurationTicks;
        _logger = logger;
        _frame = ffmpeg.av_frame_alloc();
        _thread = new Thread(DecodeThread)
        {
            Name = "Orbivo video decode",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public VideoFrameQueue Queue { get; }

    public TimeSpan Duration { get; }

    /// <summary>Nominal duration of one frame (from the frame rate).</summary>
    public long FrameDurationTicks { get; }

    /// <summary>Stream properties. The color description is final once the first frame is decoded.</summary>
    public VideoStreamInfo Info => Volatile.Read(ref _info);

    public bool HasFirstFrame => Volatile.Read(ref _firstPtsTicks) != long.MinValue;

    /// <summary>Presentation time of the first frame; valid once <see cref="HasFirstFrame"/>.</summary>
    public long FirstPtsTicks => Volatile.Read(ref _firstPtsTicks);

    /// <summary>Presentation time of the newest decoded frame.</summary>
    public long LastPtsTicks => Volatile.Read(ref _lastPtsTicks);

    /// <summary>All frames are decoded and published.</summary>
    public bool EndOfStream => Volatile.Read(ref _endOfStream) != 0;

    /// <summary>German error text for the operator if decoding (or reading the file) failed, else null.</summary>
    public string? ErrorMessage => Volatile.Read(ref _errorMessage) ?? _demuxer.ErrorMessage;

    /// <summary>
    /// Frames decoded per second, measured over about one second of wall-clock time. While the buffer is full this
    /// equals the playback rate; the decoder's spare capacity shows in a buffer that stays full.
    /// </summary>
    public double DecodedFramesPerSecond => Volatile.Read(ref _decodedFramesPerSecond);

    /// <summary>
    /// True once the latest seek (or the start after opening) is handled and enough of its frames are buffered to play
    /// smoothly, or the file ended. Then <see cref="StartTicks"/> is valid.
    /// </summary>
    public bool IsPrerolled
    {
        get
        {
            // The decode thread resets the generation counters before it publishes the seek id it handles.
            if (Volatile.Read(ref _activeSeekId) != _demuxer.RequestedSeekId)
            {
                return false;
            }

            return EndOfStream || Volatile.Read(ref _framesInGeneration) >= PrerollFrames;
        }
    }

    /// <summary>
    /// Media time the clock starts from after opening or seeking: the seek target, or the first frame if that comes later
    /// (start of the file, gaps in variable frame rate video). Valid once <see cref="IsPrerolled"/>.
    /// </summary>
    public long StartTicks
    {
        get
        {
            long start = Volatile.Read(ref _generationStartTicks);
            long target = Volatile.Read(ref _activeSeekTargetTicks);
            return start == long.MinValue ? Math.Max(0, target) : Math.Max(start, target);
        }
    }

    private int PrerollFrames => Math.Max(1, Queue.Pacer.SlotCount - 2);

    /// <summary>
    /// Control thread: decode from <paramref name="targetTicks"/> (media time, like <see cref="FirstPtsTicks"/>) on.
    /// Replaces a seek that is still running. <see cref="IsPrerolled"/> tells when it is done. The seek runs in the
    /// demuxer, so a sound decoder on the same demuxer follows it as well.
    /// </summary>
    public void RequestSeek(long targetTicks)
    {
        _demuxer.RequestSeek(targetTicks);
        Wake();
    }

    /// <summary>Wakes the decode thread if it waits for a free slot (a seek was requested at the demuxer).</summary>
    public void Wake()
    {
        Queue.Pacer.WakeProducer();
    }

    /// <summary>
    /// Opens <paramref name="path"/> with a demuxer of its own (video only) and prepares hardware decoding.
    /// Throws <see cref="MediaOpenException"/> if the file cannot be played. Call <see cref="Start"/> afterwards.
    /// </summary>
    public static VideoDecoder Open(
        string path,
        ID3D11Device device,
        ID3D11DeviceContext context,
        StereoLayout? layoutOverride,
        int slotCount,
        ILogger logger)
    {
        MediaDemuxer demuxer = MediaDemuxer.Open(path, withAudio: false, logger);
        try
        {
            return Open(demuxer, ownsDemuxer: true, device, context, layoutOverride, slotCount, logger);
        }
        catch
        {
            demuxer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Prepares hardware decoding of the video stream of <paramref name="demuxer"/> on <paramref name="device"/>.
    /// Throws <see cref="MediaOpenException"/> if the video cannot be played. Call <see cref="Start"/> afterwards.
    /// </summary>
    /// <param name="demuxer">The opened file, not started yet.</param>
    /// <param name="ownsDemuxer">True: this decoder starts and disposes the demuxer.</param>
    /// <param name="device">The shared, multithread-protected D3D11 device of the renderer.</param>
    /// <param name="context">Its immediate context (used for the copy into the frame ring).</param>
    /// <param name="layoutOverride">Stereo layout chosen by the operator, or null to guess from the size.</param>
    /// <param name="slotCount">Number of frame textures.</param>
    /// <param name="logger">Receives open details and errors.</param>
    public static VideoDecoder Open(
        MediaDemuxer demuxer,
        bool ownsDemuxer,
        ID3D11Device device,
        ID3D11DeviceContext context,
        StereoLayout? layoutOverride,
        int slotCount,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        string path = demuxer.Path;
        AVFormatContext* format = demuxer.Format;
        AVCodecContext* codec = null;
        VideoFrameQueue? queue = null;
        try
        {
            AVStream* stream = format->streams[demuxer.VideoStreamIndex];
            AVCodecParameters* parameters = stream->codecpar;
            string codecName = ffmpeg.avcodec_get_name(parameters->codec_id) ?? "unknown";
            string profile = ffmpeg.avcodec_profile_name(parameters->codec_id, parameters->profile) ?? string.Empty;

            AVCodec* decoder = FindHardwareDecoder(parameters->codec_id);
            if (decoder is null)
            {
                throw new MediaOpenException(
                    $"No FFmpeg decoder with D3D11VA support for codec {codecName}.",
                    $"Für den Codec {codecName} gibt es keinen Hardware-Decoder");
            }

            codec = ffmpeg.avcodec_alloc_context3(decoder);
            FFmpegError.Check(ffmpeg.avcodec_parameters_to_context(codec, parameters), "avcodec_parameters_to_context");
            codec->pkt_timebase = stream->time_base;
            codec->hw_device_ctx = CreateHardwareDevice(device);
            codec->get_format = new AVCodecContext_get_format_func
            {
                Pointer = (nint)(delegate* unmanaged[Cdecl]<AVCodecContext*, AVPixelFormat*, AVPixelFormat>)&ChooseHardwareFormat,
            };

            // Hardware decoding does its work on the GPU; frame threading would only add latency and surfaces.
            codec->thread_count = 1;
            FFmpegError.Check(ffmpeg.avcodec_open2(codec, decoder, null), "avcodec_open2");

            int width = parameters->width;
            int height = parameters->height;
            AVPixFmtDescriptor* pixelFormat = ffmpeg.av_pix_fmt_desc_get((AVPixelFormat)parameters->format);
            int bitDepth = pixelFormat is null ? 8 : pixelFormat->comp[0].depth;
            VideoColorFormat color = VideoColor.ResolveUnspecified(
                ToMatrix(parameters->color_space),
                ToRange(parameters->color_range),
                ToPrimaries(parameters->color_primaries),
                height,
                bitDepth > 8 ? 10 : 8);

            AVRational frameRate = ffmpeg.av_guess_frame_rate(format, stream, null);
            double framesPerSecond = frameRate.num > 0 && frameRate.den > 0 ? ffmpeg.av_q2d(frameRate) : 0;
            long frameDurationTicks = framesPerSecond > 0
                ? (long)(TimeSpan.TicksPerSecond / framesPerSecond)
                : TimeSpan.TicksPerSecond / 30;

            TimeSpan duration = stream->duration != ffmpeg.AV_NOPTS_VALUE
                ? TimeSpan.FromTicks(ffmpeg.av_rescale_q(stream->duration, stream->time_base, MediaTimeBase))
                : format->duration > 0 ? TimeSpan.FromTicks(format->duration * (TimeSpan.TicksPerSecond / ffmpeg.AV_TIME_BASE)) : TimeSpan.Zero;

            StereoLayout layout = layoutOverride ?? StereoLayoutDetection.FromSize(width, height);
            var info = new VideoStreamInfo(
                codecName,
                profile,
                width,
                height,
                framesPerSecond,
                color,
                layout,
                layoutOverride is not null,
                HardwareDecoding: true);

            logger.LogInformation(
                "Opened {Path}: {Codec} {Profile} {Width}x{Height}, {Fps:0.###} fps, {PixelFormat}, {Duration}, color {Matrix}/{Range}/{Primaries}, stereo {Layout} ({LayoutSource}). Using hardware decode (D3D11VA) with FFmpeg decoder '{Decoder}'.",
                path,
                codecName,
                profile,
                width,
                height,
                framesPerSecond,
                pixelFormat is null ? "?" : Marshal.PtrToStringUTF8((nint)pixelFormat->name),
                duration,
                color.Matrix,
                color.Range,
                color.Primaries,
                layout,
                layoutOverride is null ? "guessed from size" : "command line",
                Marshal.PtrToStringUTF8((nint)decoder->name));

            queue = new VideoFrameQueue(device, slotCount, layout);
            var result = new VideoDecoder(demuxer, ownsDemuxer, queue, context, codec, info, duration, frameDurationTicks, logger);
            codec = null;
            return result;
        }
        catch (FFmpegException exception)
        {
            throw new MediaOpenException(exception.Message, "Video kann nicht dekodiert werden (Details im Log)", exception);
        }
        catch (Exception) when (FreeOnFailure(ref codec, queue))
        {
            throw;
        }
        finally
        {
            FreeOnFailure(ref codec, null);
        }
    }

    /// <summary>Starts the decode thread (and the demuxer, if this decoder owns it).</summary>
    public void Start()
    {
        _thread.Start();
        if (_ownsDemuxer)
        {
            _demuxer.Start();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopRequested = true;
        _demuxer.VideoQueue.WakeConsumer();
        Queue.Pacer.WakeProducer();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            // Freeing FFmpeg under a running decode call would crash; leak instead and say so.
            _logger.LogError("Video decode thread for {Path} did not stop within 5 s; its resources are not freed.", _path);
            return;
        }

        _decoderTexture?.Dispose();
        _decoderTexture = null;
        AVFrame* frame = _frame;
        ffmpeg.av_frame_free(&frame);
        _frame = null;
        AVCodecContext* codec = _codec;
        ffmpeg.avcodec_free_context(&codec);
        _codec = null;
        if (_ownsDemuxer)
        {
            _demuxer.Dispose();
        }
    }

    private void DecodeThread()
    {
        try
        {
            DecodeLoop();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Decoding {Path} failed.", _path);
            Volatile.Write(ref _errorMessage, DescribeDecodeError(exception));
        }
    }

    private void DecodeLoop()
    {
        long windowStart = Stopwatch.GetTimestamp();
        int framesInWindow = 0;
        Volatile.Write(ref _activeSeekTargetTicks, long.MinValue);

        while (!_stopRequested)
        {
            bool seekPending = _demuxer.RequestedSeekId != _activeSeekId;
            if (seekPending || _awaitingReseek || EndOfStream)
            {
                // Only the queue matters now: the marker of the next seek. Packets before it are dropped.
                TakeInput(seekPending);
                continue;
            }

            int result = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (result == 0)
            {
                long pts = PresentationTicks();
                if (_skipUntilTicks != long.MinValue && pts + FrameDurationTicks <= _skipUntilTicks + SeekSnapTicks)
                {
                    // Seeking: this frame ends before the target and is never shown.
                    _lastSkippedPtsTicks = pts;
                    ffmpeg.av_frame_unref(_frame);
                    continue;
                }

                if (!DeliverFrame(pts))
                {
                    return;
                }

                framesInWindow++;
                long now = Stopwatch.GetTimestamp();
                if (now - windowStart >= Stopwatch.Frequency)
                {
                    Volatile.Write(ref _decodedFramesPerSecond, framesInWindow * (double)Stopwatch.Frequency / (now - windowStart));
                    windowStart = now;
                    framesInWindow = 0;
                }

                continue;
            }

            if (result == ffmpeg.AVERROR_EOF)
            {
                if (Volatile.Read(ref _framesInGeneration) == 0 && _lastSkippedPtsTicks != long.MinValue)
                {
                    // The seek target lies after the last frame: read again and show the last frame instead.
                    _logger.LogInformation(
                        "Seek target {Target} is after the last frame of {Path}; showing the last frame ({Last}).",
                        TimeSpan.FromTicks(_skipUntilTicks),
                        _path,
                        TimeSpan.FromTicks(_lastSkippedPtsTicks));
                    _awaitingReseek = true;
                    _demuxer.RequestVideoReseek(_activeSeekId, _lastSkippedPtsTicks);
                    continue;
                }

                Volatile.Write(ref _endOfStream, 1);
                _logger.LogInformation("Decoded all frames of {Path} (last at {Last}).", _path, TimeSpan.FromTicks(LastPtsTicks));
                continue;
            }

            if (result != FFmpegError.TryAgain)
            {
                FFmpegError.Check(result, "avcodec_receive_frame");
            }

            if (_inputFinished)
            {
                throw new InvalidOperationException("The decoder wants more input after the end of the file.");
            }

            TakeInput(seekPending: false);
        }
    }

    /// <summary>
    /// Takes the next entry of the video queue (waits a little if there is none) and acts on it. While
    /// <paramref name="seekPending"/>, packets and end markers belong to the old position and are dropped.
    /// </summary>
    private void TakeInput(bool seekPending)
    {
        if (!_demuxer.VideoQueue.TryDequeue(SlotWaitTimeout, out PacketEntry entry))
        {
            return;
        }

        try
        {
            bool current = !seekPending && !_awaitingReseek && !EndOfStream;
            switch (entry.Kind)
            {
                case PacketEntryKind.Packet when current && !_inputFinished:
                    FFmpegError.Check(ffmpeg.avcodec_send_packet(_codec, entry.Packet), "avcodec_send_packet");
                    break;
                case PacketEntryKind.Seek:
                    BeginSeek(entry.SeekId, entry.TargetTicks);
                    break;
                case PacketEntryKind.Reseek when _awaitingReseek && entry.SeekId == _activeSeekId:
                    _awaitingReseek = false;
                    FlushCodec(entry.TargetTicks);
                    break;
                case PacketEntryKind.End when current && !_inputFinished:
                    // Drain: the decoder returns the remaining frames, then AVERROR_EOF.
                    FFmpegError.Check(ffmpeg.avcodec_send_packet(_codec, null), "avcodec_send_packet (drain)");
                    _inputFinished = true;
                    break;
            }
        }
        finally
        {
            PacketQueue.Free(entry);
        }
    }

    /// <summary>
    /// Starts seek <paramref name="seekId"/> at its marker: outdates all queued frames, resets the counters of the
    /// generation and flushes the codec. The counters are reset before the id is published, so
    /// <see cref="IsPrerolled"/> never sees old values.
    /// </summary>
    private void BeginSeek(int seekId, long targetTicks)
    {
        long target = Math.Max(0, targetTicks);
        Queue.Pacer.BeginGeneration();
        Volatile.Write(ref _framesInGeneration, 0);
        Volatile.Write(ref _generationStartTicks, long.MinValue);
        Volatile.Write(ref _endOfStream, 0);
        Volatile.Write(ref _activeSeekTargetTicks, target);
        _logger.LogInformation("Seek {Id} in {Path} to {Target}.", seekId, _path, TimeSpan.FromTicks(target));
        _awaitingReseek = false;
        FlushCodec(target);
        Volatile.Write(ref _activeSeekId, seekId);
    }

    /// <summary>The demuxer has jumped to the keyframe at or before <paramref name="targetTicks"/>: skip up to the target.</summary>
    private void FlushCodec(long targetTicks)
    {
        ffmpeg.avcodec_flush_buffers(_codec);
        _inputFinished = false;
        _skipUntilTicks = targetTicks;
        _lastSkippedPtsTicks = long.MinValue;
    }

    /// <summary>
    /// Waits for a free slot, copies the decoded frame into it and publishes it. Returns false when stopping.
    /// A seek requested meanwhile drops the frame (returns true without publishing).
    /// </summary>
    private bool DeliverFrame(long pts)
    {
        try
        {
            if (_frame->format != (int)AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                throw new InvalidOperationException($"The decoder returned {(AVPixelFormat)_frame->format} instead of D3D11 frames.");
            }

            int slot;
            while (!Queue.Pacer.TryAcquireFreeSlot(out slot))
            {
                if (_stopRequested)
                {
                    return false;
                }

                if (_demuxer.RequestedSeekId != _activeSeekId)
                {
                    return true;
                }

                Queue.Pacer.WaitForFreeSlot(SlotWaitTimeout);
            }

            if (!Queue.HasTextures)
            {
                CreateQueueTextures();
            }

            CopyToSlot(slot);
            if (!HasFirstFrame)
            {
                Volatile.Write(ref _firstPtsTicks, pts);
            }

            if (Volatile.Read(ref _generationStartTicks) == long.MinValue)
            {
                Volatile.Write(ref _generationStartTicks, pts);
            }

            // Skipping ends with the first published frame; later frames are all shown, even after a gap.
            _skipUntilTicks = long.MinValue;
            Volatile.Write(ref _lastPtsTicks, pts);
            Queue.Pacer.Publish(slot, pts);
            Volatile.Write(ref _framesInGeneration, _framesInGeneration + 1);
            return true;
        }
        finally
        {
            ffmpeg.av_frame_unref(_frame);
        }
    }

    private void CreateQueueTextures()
    {
        var framesContext = (AVHWFramesContext*)_frame->hw_frames_ctx->data;
        AVPixelFormat softwareFormat = framesContext->sw_format;
        (DxgiFormat format, int bitDepth) = softwareFormat switch
        {
            AVPixelFormat.AV_PIX_FMT_NV12 => (DxgiFormat.NV12, 8),
            AVPixelFormat.AV_PIX_FMT_P010LE => (DxgiFormat.P010, 10),
            _ => throw new NotSupportedException($"Decoded surface format {softwareFormat} is not supported (only NV12 and P010)."),
        };

        // NV12/P010 copies need even sizes.
        int width = _frame->width & ~1;
        int height = _frame->height & ~1;
        VideoColorFormat color = _colorHint with { BitDepth = bitDepth };
        Queue.CreateTextures(format, width, height, VideoColor.CreateConstants(color));
        Volatile.Write(ref _info, _info with { Width = width, Height = height, Color = color });

        _logger.LogInformation(
            "First frame of {Path}: {Width}x{Height} {Format} surfaces (decoder pool {PoolWidth}x{PoolHeight}). {Slots} frame textures, about {MiB} MiB of video memory.",
            _path,
            width,
            height,
            format,
            framesContext->width,
            framesContext->height,
            Queue.Pacer.SlotCount,
            Queue.TextureBytes(format) / (1024 * 1024));
    }

    /// <summary>
    /// Copies the decoder surface into the slot texture. AV_PIX_FMT_D3D11 frames carry the texture array in data[0]
    /// and the array index in data[1]. The copy runs on the shared immediate context (multithread protected), so it is
    /// ordered before every later draw that reads the slot.
    /// </summary>
    private void CopyToSlot(int slot)
    {
        nint texturePointer = (nint)_frame->data[0];
        uint arrayIndex = (uint)(nint)_frame->data[1];
        if (_decoderTexture is null || _decoderTexture.NativePointer != texturePointer)
        {
            // Wrap the pool texture once and keep our own reference, so the wrapper can be disposed safely.
            _decoderTexture?.Dispose();
            _decoderTexture = new ID3D11Texture2D(texturePointer);
            _decoderTexture.AddRef();
        }

        var box = new Box(0, 0, 0, Queue.Width, Queue.Height, 1);
        _context.CopySubresourceRegion(Queue.GetTexture(slot), 0, 0, 0, 0, _decoderTexture, arrayIndex, box);
    }

    /// <summary>Media time of the frame: ticks from the start of the video stream (<see cref="MediaDemuxer.OriginTicks"/>).</summary>
    private long PresentationTicks()
    {
        long pts = _frame->best_effort_timestamp;
        if (pts == ffmpeg.AV_NOPTS_VALUE)
        {
            return HasFirstFrame ? LastPtsTicks + FrameDurationTicks : 0;
        }

        return ffmpeg.av_rescale_q(pts - _streamStartPts, _streamTimeBase, MediaTimeBase);
    }

    /// <summary>
    /// FFmpeg offers the pixel formats it can decode to; only the D3D11 surfaces are accepted (no CPU copies, CLAUDE.md rule 5).
    /// If the GPU cannot decode this profile, D3D11 is not offered and decoding fails with an error.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static AVPixelFormat ChooseHardwareFormat(AVCodecContext* context, AVPixelFormat* formats)
    {
        for (AVPixelFormat* format = formats; *format != AVPixelFormat.AV_PIX_FMT_NONE; format++)
        {
            if (*format == AVPixelFormat.AV_PIX_FMT_D3D11)
            {
                return *format;
            }
        }

        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    /// <summary>
    /// The first FFmpeg decoder for <paramref name="codecId"/> that can decode through D3D11VA. Matters for AV1, where
    /// the LGPL build also contains libdav1d (software only) and FFmpeg would pick it by default.
    /// </summary>
    private static AVCodec* FindHardwareDecoder(AVCodecID codecId)
    {
        void* iterator = null;
        for (AVCodec* codec = ffmpeg.av_codec_iterate(&iterator); codec is not null; codec = ffmpeg.av_codec_iterate(&iterator))
        {
            if (codec->id != codecId || ffmpeg.av_codec_is_decoder(codec) == 0)
            {
                continue;
            }

            for (int i = 0; ; i++)
            {
                AVCodecHWConfig* config = ffmpeg.avcodec_get_hw_config(codec, i);
                if (config is null)
                {
                    break;
                }

                if ((config->methods & HardwareConfigMethodDeviceContext) != 0
                    && config->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA
                    && config->pix_fmt == AVPixelFormat.AV_PIX_FMT_D3D11)
                {
                    return codec;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Wraps the renderer's device for FFmpeg (docs/ARCHITECTURE.md, "Datenfluss Video", step 1).
    /// FFmpeg releases the device when the context is freed, so it gets its own reference.
    /// </summary>
    private static AVBufferRef* CreateHardwareDevice(ID3D11Device device)
    {
        AVBufferRef* reference = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (reference is null)
        {
            throw new InvalidOperationException("av_hwdevice_ctx_alloc(D3D11VA) failed.");
        }

        var deviceContext = (AVHWDeviceContext*)reference->data;
        var d3d11Context = (AVD3D11VADeviceContext*)deviceContext->hwctx;
        device.AddRef();
        d3d11Context->device = (FFmpeg.AutoGen.ID3D11Device*)device.NativePointer;
        int result = ffmpeg.av_hwdevice_ctx_init(reference);
        if (result < 0)
        {
            ffmpeg.av_buffer_unref(&reference);
            FFmpegError.Check(result, "av_hwdevice_ctx_init");
        }

        return reference;
    }

    private static bool FreeOnFailure(ref AVCodecContext* codec, VideoFrameQueue? queue)
    {
        if (codec is not null)
        {
            AVCodecContext* codecToFree = codec;
            ffmpeg.avcodec_free_context(&codecToFree);
            codec = null;
        }

        queue?.Dispose();
        return false;
    }

    private static string DescribeDecodeError(Exception exception)
    {
        return exception switch
        {
            FFmpegException ffmpegException when ffmpegException.Message.Contains("avcodec", StringComparison.Ordinal) =>
                "Hardware-Decoder lehnt das Video ab (Profil oder Auflösung zu groß für die GPU?). Details im Log",
            NotSupportedException => "Bildformat des Decoders wird nicht unterstützt (Details im Log)",
            _ => "Dekodieren fehlgeschlagen (Details im Log)",
        };
    }

    private static YuvMatrix? ToMatrix(AVColorSpace space) => space switch
    {
        AVColorSpace.AVCOL_SPC_BT709 => YuvMatrix.Bt709,
        AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M => YuvMatrix.Bt601,
        AVColorSpace.AVCOL_SPC_BT2020_NCL or AVColorSpace.AVCOL_SPC_BT2020_CL => YuvMatrix.Bt2020,
        _ => null,
    };

    private static YuvRange? ToRange(AVColorRange range) => range switch
    {
        AVColorRange.AVCOL_RANGE_MPEG => YuvRange.Limited,
        AVColorRange.AVCOL_RANGE_JPEG => YuvRange.Full,
        _ => null,
    };

    private static ColorPrimaries? ToPrimaries(AVColorPrimaries primaries) => primaries switch
    {
        AVColorPrimaries.AVCOL_PRI_BT709 or AVColorPrimaries.AVCOL_PRI_BT470BG or AVColorPrimaries.AVCOL_PRI_SMPTE170M => ColorPrimaries.Bt709,
        AVColorPrimaries.AVCOL_PRI_BT2020 => ColorPrimaries.Bt2020,
        _ => null,
    };
}
