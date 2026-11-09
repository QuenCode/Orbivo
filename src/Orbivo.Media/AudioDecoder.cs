using FFmpeg.AutoGen;
using Microsoft.Extensions.Logging;
using Orbivo.Core.Audio;
using Orbivo.Core.Status;

namespace Orbivo.Media;

/// <summary>
/// The audio decode thread of one opened file (docs/ARCHITECTURE.md, "Threads"): decodes the sound track from the
/// demuxer's audio queue, converts it with swresample to interleaved float32 at the output sample rate (stereo, or an
/// AmbiX sound field for Ambisonics, M10) and writes it into the <see cref="AudioRingBuffer"/> with its media position.
/// </summary>
/// <remarks>
/// Media time is the same as for the video: ticks from the start of the video stream (<see cref="MediaDemuxer.OriginTicks"/>).
/// Sound before 0 (encoder priming, a track that starts earlier) is dropped. At a seek marker the decoder flushes the
/// ring buffer (and waits until the output has faded out), then drops the decoded sound before the target to the sample.
/// Gaps in the timestamps become silence, overlaps are dropped, so the ring buffer always holds continuous media time.
/// </remarks>
internal sealed unsafe class AudioDecoder : IDisposable
{
    /// <summary>A seek is ready once this much sound is buffered (or the track ended).</summary>
    public static readonly TimeSpan PrerollDuration = TimeSpan.FromMilliseconds(200);

    /// <summary>Timestamp jumps up to this size are smoothed over (no silence, no dropping).</summary>
    private static readonly long DiscontinuityToleranceTicks = 20 * TimeSpan.TicksPerMillisecond;

    private static readonly AVRational MediaTimeBase = new() { num = 1, den = (int)TimeSpan.TicksPerSecond };
    private static readonly TimeSpan QueueWait = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan RingWait = TimeSpan.FromMilliseconds(20);

    private readonly MediaDemuxer _demuxer;
    private readonly AudioRingBuffer _ring;
    private readonly ILogger _logger;
    private readonly Thread _thread;
    private readonly AVRational _streamTimeBase;
    private readonly int _swrChannels;
    private readonly int _ringChannels;

    private AVCodecContext* _codec;
    private SwrContext* _swr;
    private AVFrame* _frame;
    private float[] _converted = new float[8192];
    private float[] _ringFrames = new float[8192];

    private volatile bool _stopRequested;
    private bool _disposed;

    // Written by the decode thread, read by the control thread.
    private int _activeSeekId = -1;
    private int _endOfStream;
    private string? _errorMessage;

    // Decode thread only.
    private bool _inputFinished;
    private long _skipUntilFrame;
    private bool _positionKnown;
    private long _nextOutputFrame;
    private int _decodeErrors;

    private AudioDecoder(MediaDemuxer demuxer, AudioRingBuffer ring, AVCodecContext* codec, SwrContext* swr, AudioConversionPlan plan, string codecName, ILogger logger)
    {
        _demuxer = demuxer;
        _ring = ring;
        _codec = codec;
        _swr = swr;
        _logger = logger;
        Plan = plan;
        Info = new AudioTrackInfo(codecName, plan);
        _swrChannels = SwrChannels(plan);
        _ringChannels = plan.RingChannels;
        _streamTimeBase = demuxer.Format->streams[demuxer.AudioStreamIndex]->time_base;
        _frame = ffmpeg.av_frame_alloc();
        _thread = new Thread(DecodeThread)
        {
            Name = "Orbivo audio decode",
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
        };
    }

    public AudioConversionPlan Plan { get; }

    public AudioTrackInfo Info { get; }

    /// <summary>Sample rate of the sound written to the ring buffer.</summary>
    public int OutputSampleRate => Plan.OutputSampleRate;

    /// <summary>All sound of the track (from the latest seek on) is in the ring buffer.</summary>
    public bool EndOfStream => Volatile.Read(ref _endOfStream) != 0;

    /// <summary>German error text if decoding failed for good (the video plays on without sound), else null.</summary>
    public string? ErrorMessage => Volatile.Read(ref _errorMessage);

    /// <summary>
    /// True once the latest seek (or the start) is handled: the ring buffer holds sound of the new position worth
    /// <see cref="PrerollDuration"/>, or the track ended, or decoding failed.
    /// </summary>
    public bool IsPrerolled
    {
        get
        {
            if (ErrorMessage is not null)
            {
                return true;
            }

            if (Volatile.Read(ref _activeSeekId) != _demuxer.RequestedSeekId)
            {
                return false;
            }

            return EndOfStream || _ring.BufferedFrames >= PrerollDuration.TotalSeconds * OutputSampleRate;
        }
    }

    /// <summary>
    /// Prepares decoding of the audio stream of <paramref name="demuxer"/> into <paramref name="ring"/> at
    /// <paramref name="outputSampleRate"/>. Throws <see cref="MediaOpenException"/> if the track cannot be decoded.
    /// </summary>
    /// <param name="demuxer">The opened file.</param>
    /// <param name="ring">Ring buffer of the sound output.</param>
    /// <param name="outputSampleRate">Rate of the device stream.</param>
    /// <param name="audioMode">The operator's choice (sidecar <c>audioMode</c>), null = automatic (F23).</param>
    /// <param name="logger">Logger.</param>
    public static AudioDecoder Open(MediaDemuxer demuxer, AudioRingBuffer ring, int outputSampleRate, AudioModeChoice? audioMode, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        ArgumentNullException.ThrowIfNull(ring);
        ArgumentNullException.ThrowIfNull(logger);
        if (demuxer.AudioStreamIndex < 0)
        {
            throw new InvalidOperationException("The demuxer has no audio stream.");
        }

        AVStream* stream = demuxer.Format->streams[demuxer.AudioStreamIndex];
        AVCodecParameters* parameters = stream->codecpar;
        string codecName = ffmpeg.avcodec_get_name(parameters->codec_id) ?? "unknown";
        AVCodecContext* codec = null;
        SwrContext* swr = null;
        try
        {
            AVCodec* decoder = ffmpeg.avcodec_find_decoder(parameters->codec_id);
            if (decoder is null)
            {
                throw new MediaOpenException($"No FFmpeg decoder for audio codec {codecName}.", $"Für den Ton-Codec {codecName} gibt es keinen Decoder");
            }

            codec = ffmpeg.avcodec_alloc_context3(decoder);
            FFmpegError.Check(ffmpeg.avcodec_parameters_to_context(codec, parameters), "avcodec_parameters_to_context (audio)");
            codec->pkt_timebase = stream->time_base;
            FFmpegError.Check(ffmpeg.avcodec_open2(codec, decoder, null), "avcodec_open2 (audio)");

            int channels = Math.Max(1, codec->ch_layout.nb_channels);
            EmbeddedAudioLayout layout = demuxer.AudioLayout ?? new EmbeddedAudioLayout(channels, null, LayoutKnown: false);
            if (layout.Channels != channels)
            {
                logger.LogWarning(
                    "The container of {Path} states {ContainerChannels} sound channels, the decoder gives {Channels}; using {Channels}.",
                    demuxer.Path,
                    layout.Channels,
                    channels,
                    channels);
                layout = layout with { Channels = channels };
            }

            ResolvedAudioMode mode = AudioModeResolver.Resolve(layout, audioMode);
            AudioConversionPlan plan = AudioConversionPlan.Create(channels, codec->sample_rate, outputSampleRate, mode);
            if (plan.RingChannels > ring.Channels)
            {
                throw new MediaOpenException(
                    $"The sound track of {demuxer.Path} needs {plan.RingChannels} channels, the ring buffer holds {ring.Channels}.",
                    "Tonformat wird nicht unterstützt (Details im Log)");
            }

            swr = CreateResampler(codec, plan);

            logger.LogInformation(
                "Sound track of {Path}: {Codec}, {Channels} channels ({Layout}), {Rate} Hz, {Format}. Output: {Plan} (float32, {RingChannels} channels, {OutputRate} Hz).",
                demuxer.Path,
                codecName,
                channels,
                layout.Describe(),
                codec->sample_rate,
                ffmpeg.av_get_sample_fmt_name(codec->sample_fmt),
                plan.Describe(),
                plan.RingChannels,
                outputSampleRate);
            if (mode.Warning is { } warning)
            {
                logger.LogWarning("Sound track of {Path}: {Warning}", demuxer.Path, warning);
            }

            if (plan.IsDownmix)
            {
                logger.LogWarning("The sound track of {Path} has {Channels} channels; they are mixed down to stereo.", demuxer.Path, channels);
            }

            var result = new AudioDecoder(demuxer, ring, codec, swr, plan, codecName, logger);
            codec = null;
            swr = null;
            return result;
        }
        catch (FFmpegException exception)
        {
            throw new MediaOpenException(exception.Message, "Ton kann nicht dekodiert werden (Details im Log)", exception);
        }
        finally
        {
            if (swr is not null)
            {
                ffmpeg.swr_free(&swr);
            }

            if (codec is not null)
            {
                ffmpeg.avcodec_free_context(&codec);
            }
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
        _demuxer.AudioQueue?.WakeConsumer();
        if (_thread.IsAlive && !_thread.Join(TimeSpan.FromSeconds(5)))
        {
            _logger.LogError("Audio decode thread for {Path} did not stop within 5 s; its resources are not freed.", _demuxer.Path);
            return;
        }

        AVFrame* frame = _frame;
        ffmpeg.av_frame_free(&frame);
        _frame = null;
        SwrContext* swr = _swr;
        ffmpeg.swr_free(&swr);
        _swr = null;
        AVCodecContext* codec = _codec;
        ffmpeg.avcodec_free_context(&codec);
        _codec = null;
    }

    /// <summary>Channels swresample puts out: one for mono, all of the track for Ambisonics, else two.</summary>
    private static int SwrChannels(AudioConversionPlan plan) => plan.Channels switch
    {
        ChannelHandling.MonoOnBoth => 1,
        ChannelHandling.Ambisonics => plan.SourceChannels,
        _ => AudioConversionPlan.StereoChannels,
    };

    private static SwrContext* CreateResampler(AVCodecContext* codec, AudioConversionPlan plan)
    {
        AVChannelLayout input;
        AVChannelLayout output;
        if (plan.Channels == ChannelHandling.Ambisonics)
        {
            // Every channel as it is, only resampled: the same unnamed layout on both sides, so swresample never mixes.
            // (It does not accept Ambisonics layouts, and a named one like "4.0" would invite a remix.)
            input = new AVChannelLayout { order = AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC, nb_channels = plan.SourceChannels };
            output = input;
        }
        else
        {
            if (codec->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC || ffmpeg.av_channel_layout_check(&codec->ch_layout) == 0)
            {
                ffmpeg.av_channel_layout_default(&input, Math.Max(1, codec->ch_layout.nb_channels));
            }
            else
            {
                FFmpegError.Check(ffmpeg.av_channel_layout_copy(&input, &codec->ch_layout), "av_channel_layout_copy");
            }

            // Mono stays one channel here and is copied to both sides afterwards: swresample would lower it by 3 dB.
            ffmpeg.av_channel_layout_default(&output, plan.Channels == ChannelHandling.MonoOnBoth ? 1 : AudioConversionPlan.StereoChannels);
        }

        SwrContext* swr = null;
        try
        {
            FFmpegError.Check(
                ffmpeg.swr_alloc_set_opts2(
                    &swr,
                    &output,
                    AVSampleFormat.AV_SAMPLE_FMT_FLT,
                    plan.OutputSampleRate,
                    &input,
                    codec->sample_fmt,
                    codec->sample_rate,
                    0,
                    null),
                "swr_alloc_set_opts2");
            FFmpegError.Check(ffmpeg.swr_init(swr), "swr_init");
            return swr;
        }
        catch
        {
            ffmpeg.swr_free(&swr);
            throw;
        }
        finally
        {
            ffmpeg.av_channel_layout_uninit(&input);
            ffmpeg.av_channel_layout_uninit(&output);
        }
    }

    private void DecodeThread()
    {
        try
        {
            // The ring buffer may still hold the end of the previous video: start a fresh generation.
            if (!FlushRing())
            {
                return;
            }

            _skipUntilFrame = 0;
            Volatile.Write(ref _activeSeekId, 0);
            DecodeLoop();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Decoding the sound of {Path} failed; the video plays on without sound.", _demuxer.Path);
            Volatile.Write(ref _errorMessage, "Ton kann nicht dekodiert werden (Details im Log)");
        }
    }

    private void DecodeLoop()
    {
        PacketQueue queue = _demuxer.AudioQueue!;
        while (!_stopRequested)
        {
            bool seekPending = _demuxer.RequestedSeekId != _activeSeekId;
            if (seekPending || EndOfStream)
            {
                TakeInput(queue, seekPending);
                continue;
            }

            int result = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (result == 0)
            {
                try
                {
                    WriteFrame();
                }
                finally
                {
                    ffmpeg.av_frame_unref(_frame);
                }

                continue;
            }

            if (result == ffmpeg.AVERROR_EOF)
            {
                FlushResampler();
                _ring.MarkEnd();
                Volatile.Write(ref _endOfStream, 1);
                _logger.LogInformation("Decoded all sound of {Path}.", _demuxer.Path);
                continue;
            }

            if (result != FFmpegError.TryAgain)
            {
                ReportDecodeError(result, "avcodec_receive_frame");
            }

            if (_inputFinished)
            {
                // A decoder that wants input after draining: treat the track as ended.
                _ring.MarkEnd();
                Volatile.Write(ref _endOfStream, 1);
                continue;
            }

            TakeInput(queue, seekPending: false);
        }
    }

    private void TakeInput(PacketQueue queue, bool seekPending)
    {
        if (!queue.TryDequeue(QueueWait, out PacketEntry entry))
        {
            return;
        }

        try
        {
            bool current = !seekPending && !EndOfStream && !_inputFinished;
            switch (entry.Kind)
            {
                case PacketEntryKind.Packet when current:
                    int result = ffmpeg.avcodec_send_packet(_codec, entry.Packet);
                    if (result < 0 && result != FFmpegError.TryAgain)
                    {
                        ReportDecodeError(result, "avcodec_send_packet");
                    }

                    break;
                case PacketEntryKind.Seek:
                    BeginSeek(entry.SeekId, entry.TargetTicks);
                    break;
                case PacketEntryKind.End when current:
                    FFmpegError.Check(ffmpeg.avcodec_send_packet(_codec, null), "avcodec_send_packet (audio drain)");
                    _inputFinished = true;
                    break;
            }
        }
        finally
        {
            PacketQueue.Free(entry);
        }
    }

    /// <summary>A broken packet costs a moment of sound, not the playback. Logged a few times, then counted.</summary>
    private void ReportDecodeError(int error, string operation)
    {
        _decodeErrors++;
        if (_decodeErrors <= 5)
        {
            _logger.LogWarning("Audio {Operation} failed for {Path}: {Error}. Skipping.", operation, _demuxer.Path, FFmpegError.Describe(error));
        }
    }

    private void BeginSeek(int seekId, long targetTicks)
    {
        ffmpeg.avcodec_flush_buffers(_codec);
        ResetResampler();
        if (!FlushRing())
        {
            return;
        }

        _inputFinished = false;
        _positionKnown = false;
        _skipUntilFrame = TicksToFrames(Math.Max(0, targetTicks));
        Volatile.Write(ref _endOfStream, 0);
        Volatile.Write(ref _activeSeekId, seekId);
    }

    /// <summary>Asks for a new ring generation and waits until the output has faded out and dropped the old sound.</summary>
    private bool FlushRing()
    {
        int generation = _ring.RequestFlush();
        while (!_ring.WaitForFlush(generation, RingWait))
        {
            if (_stopRequested)
            {
                return false;
            }
        }

        return true;
    }

    private void ResetResampler()
    {
        // Drops the samples buffered inside swresample.
        ffmpeg.swr_close(_swr);
        FFmpegError.Check(ffmpeg.swr_init(_swr), "swr_init");
    }

    private void FlushResampler()
    {
        int frames = Convert(null, 0);
        if (frames > 0)
        {
            WriteConverted(frames);
        }
    }

    private void WriteFrame()
    {
        long pts = _frame->best_effort_timestamp;
        if (pts != ffmpeg.AV_NOPTS_VALUE)
        {
            long ticks = ffmpeg.av_rescale_q(pts, _streamTimeBase, MediaTimeBase) - _demuxer.OriginTicks;
            long delay = ffmpeg.swr_get_delay(_swr, OutputSampleRate);
            long position = TicksToFrames(ticks) - delay;
            if (!_positionKnown)
            {
                _nextOutputFrame = position;
                _positionKnown = true;
            }
            else if (Math.Abs(position - _nextOutputFrame) > TicksToFrames(DiscontinuityToleranceTicks))
            {
                _logger.LogInformation(
                    "Sound timestamps of {Path} jump by {Jump:0.0} ms at {Time}; continuing at the new time.",
                    _demuxer.Path,
                    (position - _nextOutputFrame) * 1000.0 / OutputSampleRate,
                    TimeSpan.FromTicks(ticks));
                _nextOutputFrame = position;
            }
        }
        else if (!_positionKnown)
        {
            _nextOutputFrame = _skipUntilFrame;
            _positionKnown = true;
        }

        int frames = Convert(_frame->extended_data, _frame->nb_samples);
        if (frames > 0)
        {
            WriteConverted(frames);
        }
    }

    /// <summary>Runs swresample into <see cref="_converted"/>; returns the number of output frames.</summary>
    private int Convert(byte** input, int inputFrames)
    {
        int capacity = ffmpeg.swr_get_out_samples(_swr, inputFrames);
        if (capacity <= 0)
        {
            return 0;
        }

        if (_converted.Length < capacity * _swrChannels)
        {
            _converted = new float[capacity * _swrChannels * 2];
        }

        fixed (float* output = _converted)
        {
            byte* outputPlane = (byte*)output;
            int frames = ffmpeg.swr_convert(_swr, &outputPlane, capacity, input, inputFrames);
            FFmpegError.Check(frames, "swr_convert");
            return frames;
        }
    }

    /// <summary>Writes <paramref name="frames"/> converted frames at <see cref="_nextOutputFrame"/> into the ring buffer.</summary>
    private void WriteConverted(int frames)
    {
        ReadOnlySpan<float> samples = ToRingFrames(frames);
        long position = _nextOutputFrame;
        _nextOutputFrame += frames;

        // Before the seek target (or before media time 0): never heard.
        long skip = Math.Max(0, Math.Max(_skipUntilFrame, 0) - position);
        if (skip >= frames)
        {
            return;
        }

        position += skip;
        samples = samples[(int)(skip * _ringChannels)..];

        if (!_ring.IsStarted)
        {
            _ring.Start(position, OutputSampleRate, _ringChannels);
        }
        else
        {
            long writePosition = _ring.WritePosition;
            if (position > writePosition)
            {
                // A gap in the track: fill it with silence, so the media time stays continuous.
                if (!WriteBlocking(ReadOnlySpan<float>.Empty, position - writePosition))
                {
                    return;
                }
            }
            else if (position < writePosition)
            {
                // Overlap (sound read again after a video reseek): drop what is already there.
                long overlap = Math.Min(writePosition - position, samples.Length / _ringChannels);
                samples = samples[(int)(overlap * _ringChannels)..];
            }
        }

        WriteBlocking(samples, 0);
    }

    /// <summary>
    /// Writes <paramref name="silenceFrames"/> of silence, then <paramref name="samples"/>, waiting for room.
    /// Returns false if a seek or the end of the decoder interrupts.
    /// </summary>
    private bool WriteBlocking(ReadOnlySpan<float> samples, long silenceFrames)
    {
        while (silenceFrames > 0 || !samples.IsEmpty)
        {
            if (_stopRequested || _demuxer.RequestedSeekId != _activeSeekId)
            {
                return false;
            }

            int written;
            if (silenceFrames > 0)
            {
                written = _ring.WriteSilence((int)Math.Min(silenceFrames, int.MaxValue));
                silenceFrames -= written;
            }
            else
            {
                written = _ring.Write(samples);
                samples = samples[(written * _ringChannels)..];
            }

            if (written == 0)
            {
                _ring.WaitForConsumer(RingWait);
            }
        }

        return true;
    }

    /// <summary>The converted frames as the ring buffer takes them: stereo, or AmbiX with (order + 1)² channels.</summary>
    private ReadOnlySpan<float> ToRingFrames(int frames)
    {
        if (_swrChannels == _ringChannels && !Plan.ConvertsFuMa)
        {
            return _converted.AsSpan(0, frames * _ringChannels);
        }

        if (_ringFrames.Length < frames * _ringChannels)
        {
            _ringFrames = new float[frames * _ringChannels * 2];
        }

        if (Plan.Channels == ChannelHandling.Ambisonics)
        {
            AmbisonicsFrames.ToAmbiX(_converted, _swrChannels, _ringFrames, _ringChannels, frames, Plan.ConvertsFuMa);
        }
        else
        {
            // Mono on both sides.
            for (int i = 0; i < frames; i++)
            {
                float sample = _converted[i];
                _ringFrames[2 * i] = sample;
                _ringFrames[(2 * i) + 1] = sample;
            }
        }

        return _ringFrames.AsSpan(0, frames * _ringChannels);
    }

    private long TicksToFrames(long ticks) => (long)Math.Round(ticks * (double)OutputSampleRate / TimeSpan.TicksPerSecond);
}
