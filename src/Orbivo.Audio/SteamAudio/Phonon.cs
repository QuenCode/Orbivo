using System.Numerics;
using System.Runtime.InteropServices;

namespace Orbivo.Audio.SteamAudio;

/// <summary>
/// P/Invoke declarations for the part of Steam Audio 4.8.1 (<c>phonon.h</c>) that Orbivo uses: context, HRTF and the
/// binaural effect. Written against exactly <see cref="SteamAudioRuntime.ExpectedVersion"/>; an update of phonon.dll
/// needs a check of these structs against the new header (native/README.md).
/// </summary>
/// <remarks>
/// All structs are blittable and all calls go through source-generated stubs without marshalling, so calling the
/// effects on the audio thread does not allocate (CLAUDE.md rule 2). Handles are opaque pointers.
/// </remarks>
internal static unsafe partial class Phonon
{
    private const string Library = SteamAudioRuntime.LibraryFileName;

    /// <summary>STEAMAUDIO_VERSION of phonon_version.h: major &lt;&lt; 16 | minor &lt;&lt; 8 | patch.</summary>
    public static uint ApiVersion =>
        ((uint)SteamAudioRuntime.ExpectedVersion.Major << 16)
        | ((uint)SteamAudioRuntime.ExpectedVersion.Minor << 8)
        | (uint)SteamAudioRuntime.ExpectedVersion.Build;

    [LibraryImport(Library, EntryPoint = "iplContextCreate")]
    public static partial IplError ContextCreate(IplContextSettings* settings, nint* context);

    [LibraryImport(Library, EntryPoint = "iplContextRelease")]
    public static partial void ContextRelease(nint* context);

    [LibraryImport(Library, EntryPoint = "iplHRTFCreate")]
    public static partial IplError HrtfCreate(nint context, IplAudioSettings* audioSettings, IplHrtfSettings* hrtfSettings, nint* hrtf);

    [LibraryImport(Library, EntryPoint = "iplHRTFRelease")]
    public static partial void HrtfRelease(nint* hrtf);

    [LibraryImport(Library, EntryPoint = "iplBinauralEffectCreate")]
    public static partial IplError BinauralEffectCreate(nint context, IplAudioSettings* audioSettings, IplBinauralEffectSettings* effectSettings, nint* effect);

    [LibraryImport(Library, EntryPoint = "iplBinauralEffectRelease")]
    public static partial void BinauralEffectRelease(nint* effect);

    [LibraryImport(Library, EntryPoint = "iplBinauralEffectReset")]
    public static partial void BinauralEffectReset(nint effect);

    [LibraryImport(Library, EntryPoint = "iplBinauralEffectApply")]
    public static partial IplAudioEffectState BinauralEffectApply(nint effect, IplBinauralEffectParams* parameters, IplAudioBuffer* input, IplAudioBuffer* output);
}

internal enum IplError
{
    Success = 0,
    Failure = 1,
    OutOfMemory = 2,
    Initialization = 3,
}

internal enum IplAudioEffectState
{
    TailRemaining = 0,
    TailComplete = 1,
}

internal enum IplSimdLevel
{
    Sse2 = 0,
    Sse4 = 1,
    Avx = 2,
    Avx2 = 3,
    Avx512 = 4,
}

internal enum IplHrtfType
{
    Default = 0,
    Sofa = 1,
}

internal enum IplHrtfNormType
{
    None = 0,
    Rms = 1,
}

internal enum IplHrtfInterpolation
{
    Nearest = 0,
    Bilinear = 1,
}

[StructLayout(LayoutKind.Sequential)]
internal struct IplContextSettings
{
    public uint Version;
    public nint LogCallback;
    public nint AllocateCallback;
    public nint FreeCallback;
    public IplSimdLevel SimdLevel;
    public int Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IplAudioSettings
{
    public int SamplingRate;
    public int FrameSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IplHrtfSettings
{
    public IplHrtfType Type;
    public nint SofaFileName;
    public nint SofaData;
    public int SofaDataSize;
    public float Volume;
    public IplHrtfNormType NormType;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct IplAudioBuffer
{
    public int NumChannels;
    public int NumSamples;
    public float** Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IplBinauralEffectSettings
{
    public nint Hrtf;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct IplBinauralEffectParams
{
    /// <summary>Unit vector from the listener to the source, in the listener's frame (same axes as OpenXR).</summary>
    public Vector3 Direction;
    public IplHrtfInterpolation Interpolation;
    public float SpatialBlend;
    public nint Hrtf;
    public float* PeakDelays;
}
