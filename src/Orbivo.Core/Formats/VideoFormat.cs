using Orbivo.Core.Geometry;

namespace Orbivo.Core.Formats;

/// <summary>
/// Everything the renderer needs to know about how a picture is laid out (F10, F11, F14). Immutable: the render
/// thread holds a reference and reads it without locks; a change means a new instance.
/// </summary>
/// <param name="Projection">How each eye's picture covers the sphere.</param>
/// <param name="Layout">How the two eyes are packed into the picture.</param>
/// <param name="SwapEyes">Show the left half to the right eye and vice versa (F11, stereo only).</param>
/// <param name="Cubemap">Face layout for <see cref="ProjectionType.Cubemap"/> and <see cref="ProjectionType.Eac"/>.</param>
/// <param name="Fisheye">Circle for <see cref="ProjectionType.Fisheye"/>.</param>
/// <param name="Offset">Turns the picture (F14).</param>
public sealed record VideoFormat(
    ProjectionType Projection,
    StereoLayout Layout,
    bool SwapEyes,
    CubemapLayout Cubemap,
    FisheyeParameters Fisheye,
    OrientationOffset Offset)
{
    /// <summary>Equirect 360 mono, nothing turned.</summary>
    public static VideoFormat Default { get; } = new(
        ProjectionType.Equirect360,
        StereoLayout.Mono,
        SwapEyes: false,
        CubemapLayout.Grid3x2,
        FisheyeParameters.Default,
        OrientationOffset.Zero);

    /// <summary>Equirect 360 with the given stereo layout (still images, tests).</summary>
    public static VideoFormat Equirect360(StereoLayout layout) => Default with { Layout = layout };

    /// <summary>
    /// The eye whose half of the picture <paramref name="eye"/> sees: swapped for stereo with <see cref="SwapEyes"/>.
    /// </summary>
    public Eye SourceEye(Eye eye)
    {
        return SwapEyes && Layout != StereoLayout.Mono ? (eye == Eye.Left ? Eye.Right : Eye.Left) : eye;
    }
}
