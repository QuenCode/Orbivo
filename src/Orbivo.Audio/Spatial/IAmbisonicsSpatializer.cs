using System.Numerics;

namespace Orbivo.Audio.Spatial;

/// <summary>
/// Turns one block of an AmbiX sound field into headphone sound for a listener with a given orientation in the field
/// (<see cref="SteamAmbisonicsSpatializer"/>; a fake in the tests). Audio output thread only.
/// </summary>
internal interface IAmbisonicsSpatializer : IDisposable
{
    /// <summary>Frames per block; every call processes exactly this many.</summary>
    int BlockFrames { get; }

    /// <summary>Sample rate the spatializer was made for.</summary>
    int SampleRate { get; }

    /// <summary>Highest Ambisonics order it renders.</summary>
    int MaxOrder { get; }

    /// <summary>Forgets all history (filter tails, last orientation), e.g. after a seek. Never allocates.</summary>
    void Reset();

    /// <summary>Renders one block. Never blocks, never allocates (CLAUDE.md rule 2).</summary>
    /// <param name="ambiX">Interleaved AmbiX (ACN/SN3D), <paramref name="channels"/> × <see cref="BlockFrames"/>.</param>
    /// <param name="channels">(order + 1)², at most (<see cref="MaxOrder"/> + 1)².</param>
    /// <param name="stereoOut">Interleaved L/R for the ears, <see cref="BlockFrames"/> frames.</param>
    /// <param name="headInContent">Orientation of the head in the field; identity = looking at the field's front.</param>
    void Process(ReadOnlySpan<float> ambiX, int channels, Span<float> stereoOut, Quaternion headInContent);
}
