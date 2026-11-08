using System.Numerics;
using System.Runtime.InteropServices;
using Orbivo.Audio.SteamAudio;

namespace Orbivo.Audio.Spatial;

/// <summary>
/// Plays the two stereo channels from two virtual speakers with Steam Audio's binaural effect and its built-in HRTF
/// (M7). Each channel goes through its own effect with its own direction; the two ear signals are added.
/// </summary>
/// <remarks>
/// Owns a Steam Audio context, the HRTF, two effects and the native sample buffers. Created and disposed on the audio
/// output thread between periods (opening a device); <see cref="Process"/> runs inside the period and neither blocks
/// nor allocates. Bilinear HRTF interpolation keeps a turning head free of steps between the measured directions.
/// </remarks>
internal sealed unsafe class SteamStereoSpatializer : IBlockSpatializer
{
    /// <summary>256 frames: 5.3 ms at 48 kHz. Also the extra delay of the sound when fixed to the picture.</summary>
    public const int DefaultBlockFrames = 256;

    /// <summary>
    /// Gain after the two binaural renders are added, so a mix sounds about as loud as plain stereo
    /// (measured with centered white noise in <c>SteamStereoSpatializerTests</c>: 0.75 gave +2 dB).
    /// </summary>
    internal const float OutputGain = 0.6f;

    private nint _context;
    private nint _hrtf;
    private nint _leftEffect;
    private nint _rightEffect;

    // Native memory: per channel buffers and the pointer arrays IPLAudioBuffer points to.
    private float* _monoLeft;
    private float* _monoRight;
    private float* _earsLeftSpeaker;
    private float* _earsRightSpeaker;
    private float** _pointers;

    private SteamStereoSpatializer(int sampleRate, int blockFrames)
    {
        SampleRate = sampleRate;
        BlockFrames = blockFrames;
    }

    public int BlockFrames { get; }

    public int SampleRate { get; }

    /// <summary>
    /// Loads Steam Audio and prepares the effects. Throws <see cref="SteamAudioException"/> if Steam Audio refuses, or
    /// the loader's exceptions (<see cref="DllNotFoundException"/>, <see cref="EntryPointNotFoundException"/>) if
    /// phonon.dll is missing or of another version.
    /// </summary>
    public static SteamStereoSpatializer Create(int sampleRate, int blockFrames = DefaultBlockFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockFrames, 16);
        var spatializer = new SteamStereoSpatializer(sampleRate, blockFrames);
        try
        {
            spatializer.Initialize();
            return spatializer;
        }
        catch
        {
            spatializer.Dispose();
            throw;
        }
    }

    public void Reset()
    {
        Phonon.BinauralEffectReset(_leftEffect);
        Phonon.BinauralEffectReset(_rightEffect);
    }

    public void Process(ReadOnlySpan<float> stereoIn, Span<float> stereoOut, Vector3 leftDirection, Vector3 rightDirection)
    {
        int frames = BlockFrames;
        if (stereoIn.Length < 2 * frames || stereoOut.Length < 2 * frames)
        {
            throw new ArgumentException("A block needs BlockFrames stereo frames.");
        }

        for (int i = 0; i < frames; i++)
        {
            _monoLeft[i] = stereoIn[2 * i];
            _monoRight[i] = stereoIn[(2 * i) + 1];
        }

        Apply(_leftEffect, leftDirection, _pointers, _pointers + 2);
        Apply(_rightEffect, rightDirection, _pointers + 1, _pointers + 4);

        float* leftSpeakerLeftEar = _pointers[2];
        float* leftSpeakerRightEar = _pointers[3];
        float* rightSpeakerLeftEar = _pointers[4];
        float* rightSpeakerRightEar = _pointers[5];
        for (int i = 0; i < frames; i++)
        {
            stereoOut[2 * i] = (leftSpeakerLeftEar[i] + rightSpeakerLeftEar[i]) * OutputGain;
            stereoOut[(2 * i) + 1] = (leftSpeakerRightEar[i] + rightSpeakerRightEar[i]) * OutputGain;
        }
    }

    public void Dispose()
    {
        if (_leftEffect != 0)
        {
            nint effect = _leftEffect;
            Phonon.BinauralEffectRelease(&effect);
            _leftEffect = 0;
        }

        if (_rightEffect != 0)
        {
            nint effect = _rightEffect;
            Phonon.BinauralEffectRelease(&effect);
            _rightEffect = 0;
        }

        if (_hrtf != 0)
        {
            nint hrtf = _hrtf;
            Phonon.HrtfRelease(&hrtf);
            _hrtf = 0;
        }

        if (_context != 0)
        {
            nint context = _context;
            Phonon.ContextRelease(&context);
            _context = 0;
        }

        Free(ref _monoLeft);
        Free(ref _monoRight);
        Free(ref _earsLeftSpeaker);
        Free(ref _earsRightSpeaker);
        if (_pointers != null)
        {
            NativeMemory.Free(_pointers);
            _pointers = null;
        }
    }

    private void Initialize()
    {
        var contextSettings = new IplContextSettings { Version = Phonon.ApiVersion, SimdLevel = IplSimdLevel.Avx2 };
        nint context;
        Check(Phonon.ContextCreate(&contextSettings, &context), "iplContextCreate");
        _context = context;

        var audioSettings = new IplAudioSettings { SamplingRate = SampleRate, FrameSize = BlockFrames };
        var hrtfSettings = new IplHrtfSettings { Type = IplHrtfType.Default, Volume = 1f, NormType = IplHrtfNormType.None };
        nint hrtf;
        Check(Phonon.HrtfCreate(_context, &audioSettings, &hrtfSettings, &hrtf), "iplHRTFCreate");
        _hrtf = hrtf;

        var effectSettings = new IplBinauralEffectSettings { Hrtf = _hrtf };
        nint left;
        Check(Phonon.BinauralEffectCreate(_context, &audioSettings, &effectSettings, &left), "iplBinauralEffectCreate");
        _leftEffect = left;
        nint right;
        Check(Phonon.BinauralEffectCreate(_context, &audioSettings, &effectSettings, &right), "iplBinauralEffectCreate");
        _rightEffect = right;

        _monoLeft = Allocate(BlockFrames);
        _monoRight = Allocate(BlockFrames);
        _earsLeftSpeaker = Allocate(2 * BlockFrames);
        _earsRightSpeaker = Allocate(2 * BlockFrames);

        // [0] left input, [1] right input, [2..3] ears of the left speaker, [4..5] ears of the right speaker.
        _pointers = (float**)NativeMemory.Alloc(6, (nuint)sizeof(float*));
        _pointers[0] = _monoLeft;
        _pointers[1] = _monoRight;
        _pointers[2] = _earsLeftSpeaker;
        _pointers[3] = _earsLeftSpeaker + BlockFrames;
        _pointers[4] = _earsRightSpeaker;
        _pointers[5] = _earsRightSpeaker + BlockFrames;
    }

    private void Apply(nint effect, Vector3 direction, float** input, float** output)
    {
        var parameters = new IplBinauralEffectParams
        {
            Direction = direction,
            Interpolation = IplHrtfInterpolation.Bilinear,
            SpatialBlend = 1f,
            Hrtf = _hrtf,
            PeakDelays = null,
        };
        var inBuffer = new IplAudioBuffer { NumChannels = 1, NumSamples = BlockFrames, Data = input };
        var outBuffer = new IplAudioBuffer { NumChannels = 2, NumSamples = BlockFrames, Data = output };
        Phonon.BinauralEffectApply(effect, &parameters, &inBuffer, &outBuffer);
    }

    private static float* Allocate(int floats)
    {
        // 64-byte alignment suits the SIMD code in phonon.dll.
        var memory = (float*)NativeMemory.AlignedAlloc((nuint)(floats * sizeof(float)), 64);
        new Span<float>(memory, floats).Clear();
        return memory;
    }

    private static void Free(ref float* memory)
    {
        if (memory != null)
        {
            NativeMemory.AlignedFree(memory);
            memory = null;
        }
    }

    private static void Check(IplError error, string function)
    {
        if (error != IplError.Success)
        {
            throw new SteamAudioException(function, error);
        }
    }
}

/// <summary>A Steam Audio call failed.</summary>
public sealed class SteamAudioException : Exception
{
    public SteamAudioException()
    {
    }

    public SteamAudioException(string message)
        : base(message)
    {
    }

    public SteamAudioException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal SteamAudioException(string function, IplError error)
        : base($"{function} failed: {error}")
    {
    }
}
