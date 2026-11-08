using System.Numerics;

namespace Orbivo.Audio.Spatial;

/// <summary>
/// Turns one block of stereo into headphone sound with the two channels coming from two directions
/// (<see cref="SteamStereoSpatializer"/>; a fake in the tests). Audio output thread only.
/// </summary>
internal interface IBlockSpatializer : IDisposable
{
    /// <summary>Frames per block; every call processes exactly this many.</summary>
    int BlockFrames { get; }

    /// <summary>Sample rate the spatializer was made for.</summary>
    int SampleRate { get; }

    /// <summary>Forgets all history (filter tails, last directions), e.g. after a seek. Never allocates.</summary>
    void Reset();

    /// <summary>
    /// Renders one block. Never blocks, never allocates (CLAUDE.md rule 2).
    /// </summary>
    /// <param name="stereoIn">Interleaved L/R, <see cref="BlockFrames"/> frames.</param>
    /// <param name="stereoOut">Interleaved L/R for the ears, <see cref="BlockFrames"/> frames.</param>
    /// <param name="leftDirection">Direction of the left channel's speaker relative to the head (OpenXR axes).</param>
    /// <param name="rightDirection">Direction of the right channel's speaker relative to the head.</param>
    void Process(ReadOnlySpan<float> stereoIn, Span<float> stereoOut, Vector3 leftDirection, Vector3 rightDirection);
}
