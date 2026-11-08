using System.Diagnostics;
using System.Numerics;
using Orbivo.Core.Audio;

namespace Orbivo.Audio.Spatial;

/// <summary>
/// Feeds a <see cref="IBlockSpatializer"/> from periods of any length (M7). The output is the input delayed by exactly
/// <see cref="LatencyFrames"/> (one block), so the renderer reads the ring that much ahead and the audio clock stays
/// exact. Each block takes the newest head orientation, so a turn of the head is heard within one block.
/// </summary>
/// <remarks>
/// After <see cref="Reset"/> the stage needs one block of input through <see cref="Prime"/>; from then on, every frame
/// that goes into <see cref="Exchange"/> pushes one processed frame out. Audio output thread only; nothing here blocks
/// or allocates after construction (CLAUDE.md rule 2).
/// </remarks>
internal sealed class SpatialStage : IDisposable
{
    /// <summary>Blocks per load measurement window (about 1 s at 48 kHz with 256 frames).</summary>
    private const int LoadWindowBlocks = 188;

    private readonly IBlockSpatializer _spatializer;
    private readonly ListenerOrientation? _listener;
    private readonly float[] _input;
    private readonly float[] _output;
    private readonly long _blockTicks;
    private int _inputFrames;
    private int _outputRead;
    private bool _primed;

    private long _windowPeakTicks;
    private int _windowBlocks;

    /// <param name="spatializer">Owned by the stage from now on.</param>
    /// <param name="listener">Head orientation from the render thread; null = always looking at the front.</param>
    public SpatialStage(IBlockSpatializer spatializer, ListenerOrientation? listener)
    {
        _spatializer = spatializer;
        _listener = listener;
        _input = new float[2 * spatializer.BlockFrames];
        _output = new float[2 * spatializer.BlockFrames];
        _blockTicks = spatializer.BlockFrames * Stopwatch.Frequency / spatializer.SampleRate;
    }

    public int LatencyFrames => _spatializer.BlockFrames;

    public int SampleRate => _spatializer.SampleRate;

    /// <summary>Angle of the virtual speakers left and right of the picture's front.</summary>
    public double WidthDegrees { get; set; } = StereoPlacement.DefaultWidthDegrees;

    /// <summary>True once <see cref="Prime"/> filled the delay line.</summary>
    public bool IsPrimed => _primed;

    /// <summary>
    /// Highest share of real time one block took to compute in the last measured second (0.1 = 10 %).
    /// </summary>
    public double PeakLoad { get; private set; }

    /// <summary>Blocks rendered since the start.</summary>
    public long Blocks { get; private set; }

    /// <summary>Forgets everything; <see cref="Prime"/> must follow before <see cref="Exchange"/>.</summary>
    public void Reset()
    {
        _spatializer.Reset();
        _inputFrames = 0;
        _outputRead = 0;
        _primed = false;
    }

    /// <summary>Fills the delay line with the first <see cref="LatencyFrames"/> frames after a reset.</summary>
    public void Prime(ReadOnlySpan<float> stereo)
    {
        stereo[..(2 * LatencyFrames)].CopyTo(_input);
        ProcessBlock();
        _primed = true;
    }

    /// <summary>
    /// Replaces <paramref name="frames"/> interleaved stereo frames in place with the processed sound of the frames that
    /// went in <see cref="LatencyFrames"/> earlier.
    /// </summary>
    public void Exchange(Span<float> stereo, int frames)
    {
        int block = LatencyFrames;
        int done = 0;
        while (done < frames)
        {
            // Frames still in the output equal the room left in the input (both are one block apart).
            int count = Math.Min(frames - done, block - _inputFrames);
            Span<float> piece = stereo.Slice(2 * done, 2 * count);
            piece.CopyTo(_input.AsSpan(2 * _inputFrames));
            _output.AsSpan(2 * _outputRead, 2 * count).CopyTo(piece);
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
        _spatializer.Dispose();
    }

    private void ProcessBlock()
    {
        Quaternion head = _listener?.Read().HeadInContent ?? Quaternion.Identity;
        (Vector3 left, Vector3 right) = AudioCoordinates.StereoSpeakers(WidthDegrees, head);

        long start = Stopwatch.GetTimestamp();
        _spatializer.Process(_input, _output, left, right);
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
