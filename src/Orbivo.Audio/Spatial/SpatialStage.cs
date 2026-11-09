using System.Diagnostics;
using System.Numerics;
using Orbivo.Core.Audio;

namespace Orbivo.Audio.Spatial;

/// <summary>
/// Feeds a block renderer from periods of any length (M7, M10). The output is the input delayed by exactly
/// <see cref="LatencyFrames"/> (one block), so the renderer reads the ring that much ahead and the audio clock stays
/// exact. Each block takes the newest head orientation, so a turn of the head is heard within one block.
/// </summary>
/// <remarks>
/// After <see cref="Reset"/> the stage needs one block of input through <see cref="Prime"/>; from then on, every frame
/// that goes into <see cref="Exchange(ReadOnlySpan{float}, Span{float}, int)"/> pushes one processed stereo frame out.
/// The input has <see cref="InputChannels"/> samples per frame. Audio output thread only; nothing here blocks or
/// allocates after construction (CLAUDE.md rule 2).
/// </remarks>
internal abstract class BlockStage : IDisposable
{
    /// <summary>Blocks per load measurement window (about 1 s at 48 kHz with 256 frames).</summary>
    private const int LoadWindowBlocks = 188;

    private readonly float[] _input;
    private readonly float[] _output;
    private readonly long _blockTicks;
    private int _inputChannels;
    private int _inputFrames;
    private int _outputRead;
    private bool _primed;

    private long _windowPeakTicks;
    private int _windowBlocks;

    protected BlockStage(int blockFrames, int sampleRate, int inputChannels, int maxInputChannels, ListenerOrientation? listener)
    {
        LatencyFrames = blockFrames;
        SampleRate = sampleRate;
        Listener = listener;
        _inputChannels = inputChannels;
        _input = new float[maxInputChannels * blockFrames];
        _output = new float[2 * blockFrames];
        _blockTicks = blockFrames * Stopwatch.Frequency / sampleRate;
    }

    public int LatencyFrames { get; }

    public int SampleRate { get; }

    /// <summary>Samples per input frame.</summary>
    public int InputChannels
    {
        get => _inputChannels;
        protected set => _inputChannels = value;
    }

    /// <summary>True once <see cref="Prime"/> filled the delay line.</summary>
    public bool IsPrimed => _primed;

    /// <summary>Highest share of real time one block took to compute in the last measured second (0.1 = 10 %).</summary>
    public double PeakLoad { get; private set; }

    /// <summary>Blocks rendered since the start.</summary>
    public long Blocks { get; private set; }

    protected ListenerOrientation? Listener { get; }

    /// <summary>Forgets everything; <see cref="Prime"/> must follow before <see cref="Exchange(ReadOnlySpan{float}, Span{float}, int)"/>.</summary>
    public void Reset()
    {
        ResetRenderer();
        _inputFrames = 0;
        _outputRead = 0;
        _primed = false;
    }

    /// <summary>Fills the delay line with the first <see cref="LatencyFrames"/> frames after a reset.</summary>
    public void Prime(ReadOnlySpan<float> input)
    {
        input[..(_inputChannels * LatencyFrames)].CopyTo(_input);
        ProcessBlock();
        _primed = true;
    }

    /// <summary>
    /// Takes <paramref name="frames"/> input frames and writes as many processed stereo frames, those that went in
    /// <see cref="LatencyFrames"/> earlier. <paramref name="input"/> and <paramref name="stereoOut"/> may be the same
    /// memory if the input is stereo.
    /// </summary>
    public void Exchange(ReadOnlySpan<float> input, Span<float> stereoOut, int frames)
    {
        int channels = _inputChannels;
        int block = LatencyFrames;
        int done = 0;
        while (done < frames)
        {
            // Frames still in the output equal the room left in the input (both are one block apart).
            int count = Math.Min(frames - done, block - _inputFrames);
            input.Slice(channels * done, channels * count).CopyTo(_input.AsSpan(channels * _inputFrames));
            _output.AsSpan(2 * _outputRead, 2 * count).CopyTo(stereoOut.Slice(2 * done, 2 * count));
            _inputFrames += count;
            _outputRead += count;
            done += count;
            if (_inputFrames == block)
            {
                ProcessBlock();
            }
        }
    }

    public void Dispose()
    {
        DisposeRenderer();
    }

    protected abstract void ResetRenderer();

    /// <summary>Renders one block of <paramref name="input"/> (<see cref="InputChannels"/> per frame) to stereo.</summary>
    protected abstract void Render(ReadOnlySpan<float> input, Span<float> stereoOut);

    protected abstract void DisposeRenderer();

    private void ProcessBlock()
    {
        long start = Stopwatch.GetTimestamp();
        Render(_input.AsSpan(0, _inputChannels * LatencyFrames), _output);
        long elapsed = Stopwatch.GetTimestamp() - start;

        _inputFrames = 0;
        _outputRead = 0;
        Blocks++;
        _windowPeakTicks = Math.Max(_windowPeakTicks, elapsed);
        if (++_windowBlocks >= LoadWindowBlocks)
        {
            PeakLoad = (double)_windowPeakTicks / _blockTicks;
            _windowPeakTicks = 0;
            _windowBlocks = 0;
        }
    }
}

/// <summary>Stereo fixed to the picture (M7): two virtual speakers at ±<see cref="WidthDegrees"/> of the picture's front.</summary>
internal sealed class SpatialStage : BlockStage
{
    private readonly IBlockSpatializer _spatializer;

    /// <param name="spatializer">Owned by the stage from now on.</param>
    /// <param name="listener">Head orientation from the render thread; null = always looking at the front.</param>
    public SpatialStage(IBlockSpatializer spatializer, ListenerOrientation? listener)
        : base(spatializer.BlockFrames, spatializer.SampleRate, 2, 2, listener)
    {
        _spatializer = spatializer;
    }

    /// <summary>Angle of the virtual speakers left and right of the picture's front.</summary>
    public double WidthDegrees { get; set; } = StereoPlacement.DefaultWidthDegrees;

    /// <summary>Stereo in place: replaces <paramref name="frames"/> frames with the processed ones of one block earlier.</summary>
    public void Exchange(Span<float> stereo, int frames) => Exchange(stereo, stereo, frames);

    protected override void ResetRenderer() => _spatializer.Reset();

    protected override void Render(ReadOnlySpan<float> input, Span<float> stereoOut)
    {
        Quaternion head = Listener?.Read().HeadInContent ?? Quaternion.Identity;
        (Vector3 left, Vector3 right) = AudioCoordinates.StereoSpeakers(WidthDegrees, head);
        _spatializer.Process(input, stereoOut, left, right);
    }

    protected override void DisposeRenderer() => _spatializer.Dispose();
}

/// <summary>
/// An Ambisonics sound field rendered for the ears (M10). Fixed to the picture, the field stays in place while the head
/// turns; otherwise it turns with the head (the listener always looks at the field's front).
/// </summary>
internal sealed class AmbisonicsStage : BlockStage
{
    private readonly IAmbisonicsSpatializer _spatializer;

    /// <param name="spatializer">Owned by the stage from now on.</param>
    /// <param name="listener">Head orientation from the render thread; null = always looking at the front.</param>
    public AmbisonicsStage(IAmbisonicsSpatializer spatializer, ListenerOrientation? listener)
        : base(
            spatializer.BlockFrames,
            spatializer.SampleRate,
            AmbisonicsLayout.GetChannelCount(AmbisonicsLayout.MinSupportedOrder),
            AmbisonicsLayout.GetChannelCount(spatializer.MaxOrder),
            listener)
    {
        _spatializer = spatializer;
    }

    /// <summary>Most channels per frame (the highest order the spatializer renders).</summary>
    public int MaxChannels => AmbisonicsLayout.GetChannelCount(_spatializer.MaxOrder);

    /// <summary>The field stays with the picture (head orientation applied), or turns with the head.</summary>
    public bool FixedToPicture { get; set; } = true;

    /// <summary>True if the stage renders fields with <paramref name="channels"/> per frame.</summary>
    public bool Supports(int channels) =>
        AmbisonicsLayout.TryGetOrder(channels, out int order) && order <= _spatializer.MaxOrder;

    /// <summary>Switches to fields with <paramref name="channels"/> per frame; <see cref="BlockStage.Reset"/> and a prime follow.</summary>
    public void SetChannels(int channels)
    {
        if (!Supports(channels))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Not a supported Ambisonics channel count.");
        }

        InputChannels = channels;
        Reset();
    }

    protected override void ResetRenderer() => _spatializer.Reset();

    protected override void Render(ReadOnlySpan<float> input, Span<float> stereoOut)
    {
        Quaternion head = FixedToPicture ? Listener?.Read().HeadInContent ?? Quaternion.Identity : Quaternion.Identity;
        _spatializer.Process(input, InputChannels, stereoOut, head);
    }

    protected override void DisposeRenderer() => _spatializer.Dispose();
}
