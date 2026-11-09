using System.Numerics;
using System.Runtime.InteropServices;
using Orbivo.Audio.SteamAudio;

namespace Orbivo.Audio.Spatial;

/// <summary>
/// Steam Audio's own Ambisonics encoder, for the tests: it shows which N3D channel Steam Audio expects for a direction,
/// so the tests can pin how AmbiX maps onto Steam Audio's axes (<see cref="SteamAmbisonicsSpatializer"/>).
/// Not used for playback.
/// </summary>
internal static unsafe class SteamAmbisonicsEncoder
{
    /// <summary>
    /// Gains of the (order + 1)² N3D channels Steam Audio gives a source in <paramref name="direction"/> (Steam Audio
    /// axes, the same as OpenXR: +X right, +Y up, −Z ahead).
    /// </summary>
    public static float[] Gains(Vector3 direction, int order)
    {
        const int Frames = 64;
        int channels = (order + 1) * (order + 1);
        nint context = 0;
        nint effect = 0;
        float* input = (float*)NativeMemory.AlignedAlloc(Frames * sizeof(float), 64);
        float* output = (float*)NativeMemory.AlignedAlloc((nuint)(channels * Frames * sizeof(float)), 64);
        float** pointers = (float**)NativeMemory.Alloc((nuint)(channels + 1), (nuint)sizeof(float*));
        try
        {
            var contextSettings = new IplContextSettings { Version = Phonon.ApiVersion, SimdLevel = IplSimdLevel.Avx2 };
            Check(Phonon.ContextCreate(&contextSettings, &context), "iplContextCreate");
            var audioSettings = new IplAudioSettings { SamplingRate = 48_000, FrameSize = Frames };
            var settings = new IplAmbisonicsEncodeEffectSettings { MaxOrder = order };
            Check(Phonon.AmbisonicsEncodeEffectCreate(context, &audioSettings, &settings, &effect), "iplAmbisonicsEncodeEffectCreate");

            for (int i = 0; i < Frames; i++)
            {
                input[i] = 1f;
            }

            pointers[0] = input;
            for (int c = 0; c < channels; c++)
            {
                pointers[c + 1] = output + (c * Frames);
            }

            var parameters = new IplAmbisonicsEncodeEffectParams { Direction = direction, Order = order };
            var inBuffer = new IplAudioBuffer { NumChannels = 1, NumSamples = Frames, Data = pointers };
            var outBuffer = new IplAudioBuffer { NumChannels = channels, NumSamples = Frames, Data = pointers + 1 };

            // The encoder fades from the previous direction over a block; the second block has the steady gains.
            Phonon.AmbisonicsEncodeEffectApply(effect, &parameters, &inBuffer, &outBuffer);
            Phonon.AmbisonicsEncodeEffectApply(effect, &parameters, &inBuffer, &outBuffer);

            var gains = new float[channels];
            for (int c = 0; c < channels; c++)
            {
                gains[c] = output[(c * Frames) + Frames - 1];
            }

            return gains;
        }
        finally
        {
            if (effect != 0)
            {
                Phonon.AmbisonicsEncodeEffectRelease(&effect);
            }

            if (context != 0)
            {
                Phonon.ContextRelease(&context);
            }

            NativeMemory.AlignedFree(input);
            NativeMemory.AlignedFree(output);
            NativeMemory.Free(pointers);
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
