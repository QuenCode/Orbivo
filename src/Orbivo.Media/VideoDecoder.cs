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
/// One opened video file: FFmpeg demux and D3D11VA hardware decoding on the shared device, on its own decode thread
/// (docs/ARCHITECTURE.md, "Datenfluss Video"). Each decoded frame is copied into a slot of <see cref="Queue"/>
/// and released at once, so the decoder's own texture pool never runs dry.
/// </summary>
/// <remarks>
/// Video only until M5: demux and decode share one thread. Other streams are discarded by the demuxer.
/// Disposing stops the thread and frees FFmpeg; the <see cref="Queue"/> is owned by the caller, because the render
/// thread may still be showing one of its frames.
/// </remarks>
internal sealed unsafe class VideoDecoder : IDisposable
{
    /// <summary>AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX from libavcodec/codec.h (an anonymous enum, not in the bindings).</summary>
    private const int HardwareConfigMethodDeviceContext = 0x01;

    private static readonly AVRational MediaTimeBase = new() { num = 1, den = (int)TimeSpan.TicksPerSecond };
    private static readonly TimeSpan SlotWaitTimeout = TimeSpan.FromMilliseconds(50);

    private readonly ILogger _logger;
    private readonly ID3D11DeviceContext _context;
    private readonly string _path;
    private readonly int _streamIndex;
    private readonly AVRational _streamTimeBase;
    private readonly long _streamStartPts;
    private readonly VideoColorFormat _colorHint;
    private readonly Thread _thread;

    private AVFormatContext* _format;
    private AVCodecContext* _codec;
    private AVPacket* _packet;
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

    private VideoDecoder(
        string path,
        VideoFrameQueue queue,
        ID3D11DeviceContext context,
        AVFormatContext* format,
        AVCodecContext* codec,
        int streamIndex,
        VideoStreamInfo info,
        TimeSpan duration,
        long frameDurationTicks,
        ILogger logger)
    {
        _path = path;
        Queue = queue;
        _context = context;
        _format = format;
        _codec = codec;
        _streamIndex = streamIndex;
        AVStream* stream = format->streams[streamIndex];
        _streamTimeBase = stream->time_base;
        _streamStartPts = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time;
        _colorHint = info.Color;
        _info = info;
        Duration = duration;
        FrameDurationTicks = frameDurationTicks;
        _logger = logger;
        _packet = ffmpeg.av_packet_alloc();
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

    /// <summary>German error text for the operator if decoding failed, else null.</summary>
    public string? ErrorMessage => Volatile.Read(ref _errorMessage);

    /// <summary>
    /// Frames decoded per second, measured over about one second of wall-clock time. While the buffer is full this
    /// equals the playback rate; the decoder's spare capacity shows in a buffer that stays full.
    /// </summary>
    public double DecodedFramesPerSecond => Volatile.Read(ref _decodedFramesPerSecond);

    /// <summary>True once enough frames are buffered to start playback smoothly (or the video is shorter).</summary>
    public bool IsPrerolled => EndOfStream || (HasFirstFrame && Queue.Pacer.ReadyCount >= Math.Max(1, Queue.Pacer.SlotCount - 2));

    /// <summary>
    /// Opens <paramref name="path"/> and prepares hardware decoding on <paramref name="device"/>. Throws
    /// <see cref="MediaOpenException"/> if the file cannot be played. Call <see cref="Start"/> afterwards.
    /// </summary>
    /// <param name="path">Video file.</param>
    /// <param name="device">The shared, multithread-protected D3D11 device of the renderer.</param>
    /// <param name="context">Its immediate context (used for the copy into the frame ring).</param>
    /// <param name="layoutOverride">Stereo layout chosen by the operator, or null to guess from the size.</param>
    /// <param name="slotCount">Number of frame textures.</param>
    /// <param name="logger">Receives open details and errors.</param>
    public static VideoDecoder Open(
        string path,
        ID3D11Device device,
        ID3D11DeviceContext context,
        StereoLayout? layoutOverride,
        int slotCount,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(logger);

        if (!File.Exists(path))
        {
            throw new MediaOpenException($"Video file {path} not found.", "Datei nicht gefunden");
        }

        AVFormatContext* format = null;
        AVCodecContext* codec = null;
        VideoFrameQueue? queue = null;
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

            int streamIndex = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (streamIndex < 0)
            {
                throw new MediaOpenException($"{path} contains no video stream.", "Datei enthält kein Video");
            }

            for (int i = 0; i < format->nb_streams; i++)
            {
                format->streams[i]->discard = i == streamIndex ? AVDiscard.AVDISCARD_DEFAULT : AVDiscard.AVDISCARD_ALL;
            }

            AVStream* stream = format->streams[streamIndex];
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
            var result2 = new VideoDecoder(path, queue, context, format, codec, streamIndex, info, duration, frameDurationTicks, logger);
            format = null;
            codec = null;
            return result2;
        }
        catch (FFmpegException exception)
        {
            throw new MediaOpenException(exception.Message, "Video kann nicht dekodiert werden (Details im Log)", exception);
        }
        catch (Exception) when (FreeOnFailure(ref format, ref codec, queue))
        {
            throw;
        }
        finally
        {
            FreeOnFailure(ref format, ref codec, null);
        }
    }

    public void Start()
    {
        _thread.Start();
    }

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
            // Freeing FFmpeg under a running decode call would crash; leak instead and say so.
            _logger.LogError("Video decode thread for {Path} did not stop within 5 s; its resources are not freed.", _path);
            return;
        }

        _decoderTexture?.Dispose();
        _decoderTexture = null;
        AVFrame* frame = _frame;
        ffmpeg.av_frame_free(&frame);
        _frame = null;
        AVPacket* packet = _packet;
        ffmpeg.av_packet_free(&packet);
        _packet = null;
        AVCodecContext* codec = _codec;
        ffmpeg.avcodec_free_context(&codec);
        _codec = null;
        AVFormatContext* format = _format;
        ffmpeg.avformat_close_input(&format);
        _format = null;
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
        bool inputFinished = false;
        long windowStart = Stopwatch.GetTimestamp();
        int framesInWindow = 0;

        while (!_stopRequested)
        {
            int result = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (result == 0)
            {
                if (!DeliverFrame())
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
                Volatile.Write(ref _endOfStream, 1);
                _logger.LogInformation("Decoded all frames of {Path} (last at {Last}).", _path, TimeSpan.FromTicks(LastPtsTicks));
                return;
            }

            if (result != FFmpegError.TryAgain)
            {
                FFmpegError.Check(result, "avcodec_receive_frame");
            }

            if (inputFinished)
            {
                throw new InvalidOperationException("The decoder wants more input after the end of the file.");
            }

            result = ffmpeg.av_read_frame(_format, _packet);
            if (result == ffmpeg.AVERROR_EOF)
            {
                // Drain: the decoder returns the remaining frames, then AVERROR_EOF.
                FFmpegError.Check(ffmpeg.avcodec_send_packet(_codec, null), "avcodec_send_packet (drain)");
                inputFinished = true;
                continue;
            }

            FFmpegError.Check(result, "av_read_frame");
            try
            {
                if (_packet->stream_index == _streamIndex)
                {
                    FFmpegError.Check(ffmpeg.avcodec_send_packet(_codec, _packet), "avcodec_send_packet");
                }
            }
            finally
            {
                ffmpeg.av_packet_unref(_packet);
            }
        }
    }

    /// <summary>Waits for a free slot, copies the decoded frame into it and publishes it. Returns false when stopping.</summary>
    private bool DeliverFrame()
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

                Queue.Pacer.WaitForFreeSlot(SlotWaitTimeout);
            }

            if (!Queue.HasTextures)
            {
                CreateQueueTextures();
            }

            CopyToSlot(slot);
            long pts = PresentationTicks();
            if (!HasFirstFrame)
            {
                Volatile.Write(ref _firstPtsTicks, pts);
            }

            Volatile.Write(ref _lastPtsTicks, pts);
            Queue.Pacer.Publish(slot, pts);
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

    private static bool FreeOnFailure(ref AVFormatContext* format, ref AVCodecContext* codec, VideoFrameQueue? queue)
    {
        if (codec is not null)
        {
            AVCodecContext* codecToFree = codec;
            ffmpeg.avcodec_free_context(&codecToFree);
            codec = null;
        }

        if (format is not null)
        {
            AVFormatContext* formatToClose = format;
            ffmpeg.avformat_close_input(&formatToClose);
            format = null;
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
