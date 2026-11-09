using System.Numerics;
using System.Runtime.InteropServices;
using Orbivo.Audio.SteamAudio;
using Orbivo.Core.Audio;

namespace Orbivo.Audio.Spatial;

/// <summary>
/// Renders an AmbiX sound field binaurally with Steam Audio's Ambisonics decode effect and its built-in HRTF (M10). The
/// effect rotates the field against the listener's orientation and decodes it for the two ears.
/// </summary>
/// <remarks>
/// Steam Audio's channels lie like AmbiX (ACN order, the same axes) but are orthonormal: N3D / √(4π). Each AmbiX
/// channel of degree l is therefore scaled by √((2l + 1) / 4π) on the way in; a source of level 1 in AmbiX then comes
/// out as loud as a mono source of level 1 through Steam Audio's binaural effect.
/// <para>
/// Owns a Steam Audio context, the HRTF, the effect and the native sample buffers. Created and disposed on the audio
/// output thread between periods; <see cref="Process"/> neither blocks nor allocates.
/// </para>
/// </remarks>
internal sealed unsafe class SteamAmbisonicsSpatializer : IAmbisonicsSpatializer
{
    /// <summary>
    /// Gain per order (index 1–3) after decoding, so a single source in front is about as loud as the mono signal
    /// played as plain stereo (measured with white noise in <c>SteamAmbisonicsSpatializerTests</c>: without it, order 1
    /// came out 8.1 dB, order 2 6.4 dB, order 3 5.1 dB lower).
    /// </summary>
    internal static readonly float[] OutputGain = [1f, 2.5f, 2.1f, 1.8f];

    private readonly float[] _channelScale;

    private nint _context;
    private nint _hrtf;
    private nint _effect;

    // Native memory: planar field channels, the two ears, and the pointer arrays IPLAudioBuffer points to.
    private float* _field;
    private float* _ears;
    private float** _pointers;
    private int _lastChannels;

    private SteamAmbisonicsSpatializer(int sampleRate, int maxOrder, int blockFrames)
    {
        SampleRate = sampleRate;
        MaxOrder = maxOrder;
        BlockFrames = blockFrames;
        int channels = AmbisonicsLayout.GetChannelCount(maxOrder);
        _channelScale = new float[channels];
        for (int acn = 0; acn < channels; acn++)
        {
            int degree = AmbisonicsLayout.GetDegree(acn);
            _channelScale[acn] = MathF.Sqrt(((2 * degree) + 1) / (4 * MathF.PI));
        }
    }

    public int BlockFrames { get; }

    public int SampleRate { get; }

    public int MaxOrder { get; }

    private int MaxChannels => _channelScale.Length;

    /// <summary>
    /// Loads Steam Audio and prepares the effect. Throws <see cref="SteamAudioException"/> if Steam Audio refuses, or
    /// the loader's exceptions if phonon.dll is missing or of another version.
    /// </summary>
    public static SteamAmbisonicsSpatializer Create(
        int sampleRate,
        int maxOrder = AmbisonicsLayout.MaxSupportedOrder,
        int blockFrames = SteamStereoSpatializer.DefaultBlockFrames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOrder, AmbisonicsLayout.MinSupportedOrder);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxOrder, AmbisonicsLayout.MaxSupportedOrder);
        ArgumentOutOfRangeException.ThrowIfLessThan(blockFrames, 16);
        var spatializer = new SteamAmbisonicsSpatializer(sampleRate, maxOrder, blockFrames);
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
        Phonon.AmbisonicsDecodeEffectReset(_effect);
    }

    public void Process(ReadOnlySpan<float> ambiX, int channels, Span<float> stereoOut, Quaternion headInContent)
    {
        int frames = BlockFrames;
        if (!AmbisonicsLayout.TryGetOrder(channels, out int order) || order > MaxOrder)
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Not a supported Ambisonics channel count.");
        }

        if (ambiX.Length < channels * frames || stereoOut.Length < 2 * frames)
        {
            throw new ArgumentException("A block needs BlockFrames frames.");
        }

        if (channels != _lastChannels)
        {
            // Channels above the order of this track stay silent.
            new Span<float>(_field, MaxChannels * frames).Clear();
            _lastChannels = channels;
        }

        for (int c = 0; c < channels; c++)
        {
            float* plane = _field + (c * frames);
            float scale = _channelScale[c];
            for (int i = 0; i < frames; i++)
            {
                plane[i] = ambiX[(i * channels) + c] * scale;
            }
        }

        (Vector3 right, Vector3 up, Vector3 ahead) = AudioCoordinates.ListenerAxes(headInContent);
        var parameters = new IplAmbisonicsDecodeEffectParams
        {
            Order = order,
            Hrtf = _hrtf,
            Orientation = new IplCoordinateSpace3 { Right = right, Up = up, Ahead = ahead, Origin = Vector3.Zero },
            Binaural = IplBool.True,
        };
        var inBuffer = new IplAudioBuffer { NumChannels = MaxChannels, NumSamples = frames, Data = _pointers };
        var outBuffer = new IplAudioBuffer { NumChannels = 2, NumSamples = frames, Data = _pointers + MaxChannels };
        Phonon.AmbisonicsDecodeEffectApply(_effect, &parameters, &inBuffer, &outBuffer);

        float* left = _ears;
        float* rightEar = _ears + frames;
        float gain = OutputGain[order];
        for (int i = 0; i < frames; i++)
        {
            stereoOut[2 * i] = left[i] * gain;
            stereoOut[(2 * i) + 1] = rightEar[i] * gain;
        }
    }

    public void Dispose()
    {
        if (_effect != 0)
        {
            nint effect = _effect;
            Phonon.AmbisonicsDecodeEffectRelease(&effect);
            _effect = 0;
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

        if (_field != null)
        {
            NativeMemory.AlignedFree(_field);
            _field = null;
        }

        if (_ears != null)
        {
            NativeMemory.AlignedFree(_ears);
            _ears = null;
        }

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

        var effectSettings = new IplAmbisonicsDecodeEffectSettings
        {
            SpeakerLayout = new IplSpeakerLayout { Type = IplSpeakerLayoutType.Stereo },
            Hrtf = _hrtf,
            MaxOrder = MaxOrder,
        };
        nint effect;
        Check(Phonon.AmbisonicsDecodeEffectCreate(_context, &audioSettings, &effectSettings, &effect), "iplAmbisonicsDecodeEffectCreate");
        _effect = effect;

        _field = Allocate(MaxChannels * BlockFrames);
        _ears = Allocate(2 * BlockFrames);

        // [0 .. MaxChannels) field channels, then the left and the right ear.
        _pointers = (float**)NativeMemory.Alloc((nuint)(MaxChannels + 2), (nuint)sizeof(float*));
        for (int c = 0; c < MaxChannels; c++)
        {
            _pointers[c] = _field + (c * BlockFrames);
        }

        _pointers[MaxChannels] = _ears;
        _pointers[MaxChannels + 1] = _ears + BlockFrames;
    }

    private static float* Allocate(int floats)
    {
        // 64-byte alignment suits the SIMD code in phonon.dll.
        var memory = (float*)NativeMemory.AlignedAlloc((nuint)(floats * sizeof(float)), 64);
        new Span<float>(memory, floats).Clear();
        return memory;
    }

    private static void Check(IplError error, string function)
    {
        if (error != IplError.Success)
        {
            throw new SteamAudioException(function, error);
        }
    }
}
