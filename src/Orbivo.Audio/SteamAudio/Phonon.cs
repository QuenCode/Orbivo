using System.Numerics;
using System.Runtime.InteropServices;

namespace Orbivo.Audio.SteamAudio;

/// <summary>
/// P/Invoke declarations for the part of Steam Audio 4.8.1 (<c>phonon.h</c>) that Orbivo uses: context, HRTF, the
/// binaural effect and the Ambisonics decode effect (M10); the Ambisonics encode effect serves the tests. Written against exactly <see cref="SteamAudioRuntime.ExpectedVersion"/>; an update of phonon.dll
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

    [LibraryImport(Library, EntryPoint = "iplAmbisonicsDecodeEffectCreate")]
    public static partial IplError AmbisonicsDecodeEffectCreate(nint context, IplAudioSettings* audioSettings, IplAmbisonicsDecodeEffectSettings* effectSettings, nint* effect);

    [LibraryImport(Library, EntryPoint = "iplAmbisonicsDecodeEffectRelease")]
    public static partial void AmbisonicsDecodeEffectRelease(nint* effect);

    [LibraryImport(Library, EntryPoint = "iplAmbisonicsDecodeEffectReset")]
    public static partial void AmbisonicsDecodeEffectReset(nint effect);

    /// <summary>Rotates an N3D sound field and renders it; cannot work in place.</summary>
    [LibraryImport(Library, EntryPoint = "iplAmbisonicsDecodeEffectApply")]
    public static partial IplAudioEffectState AmbisonicsDecodeEffectApply(nint effect, IplAmbisonicsDecodeEffectParams* parameters, IplAudioBuffer* input, IplAudioBuffer* output);

    [LibraryImport(Library, EntryPoint = "iplAmbisonicsEncodeEffectCreate")]
    public static partial IplError AmbisonicsEncodeEffectCreate(nint context, IplAudioSettings* audioSettings, IplAmbisonicsEncodeEffectSettings* effectSettings, nint* effect);

    [LibraryImport(Library, EntryPoint = "iplAmbisonicsEncodeEffectRelease")]
    public static partial void AmbisonicsEncodeEffectRelease(nint* effect);

    [LibraryImport(Library, EntryPoint = "iplAmbisonicsEncodeEffectApply")]
    public static partial IplAudioEffectState AmbisonicsEncodeEffectApply(nint effect, IplAmbisonicsEncodeEffectParams* parameters, IplAudioBuffer* input, IplAudioBuffer* output);
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

internal enum IplSpeakerLayoutType
{
    Mono = 0,
    Stereo = 1,
    Quadraphonic = 2,
    Surround51 = 3,
    Surround71 = 4,
    Custom = 5,
}

/// <summary>IPLbool.</summary>
internal enum IplBool
{
    False = 0,
    True = 1,
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
internal unsafe struct IplSpeakerLayout
{
    public IplSpeakerLayoutType Type;
    public int NumSpeakers;
    public Vector3* Speakers;
}

/// <summary>A coordinate system in the canonical one: +x right, +y up, −z ahead (the same axes as OpenXR).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IplCoordinateSpace3
{
    public Vector3 Right;
    public Vector3 Up;
    public Vector3 Ahead;
    public Vector3 Origin;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IplAmbisonicsDecodeEffectSettings
{
    public IplSpeakerLayout SpeakerLayout;
    public nint Hrtf;
    public int MaxOrder;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IplAmbisonicsDecodeEffectParams
{
    public int Order;
    public nint Hrtf;

    /// <summary>The listener's orientation in the frame of the sound field.</summary>
    public IplCoordinateSpace3 Orientation;
    public IplBool Binaural;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IplAmbisonicsEncodeEffectSettings
{
    public int MaxOrder;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IplAmbisonicsEncodeEffectParams
{
    public Vector3 Direction;
    public int Order;
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
