namespace Orbivo.Core.Formats;

/// <summary>
/// How the picture of one eye covers the sphere (F10). The numbers are the values the shader receives
/// (<c>projection.hlsl</c>, <c>ProjectionType*</c> constants); do not reorder.
/// </summary>
public enum ProjectionType
{
    /// <summary>Equirectangular, 360° × 180°. Image center = forward, left in the image = left.</summary>
    Equirect360 = 0,

    /// <summary>Equirectangular, 180° × 180° (VR180). Only the front half; the back stays black.</summary>
    Equirect180 = 1,

    /// <summary>Six cube faces in a grid (<see cref="CubemapLayout"/>), linear within each face.</summary>
    Cubemap = 2,

    /// <summary>Equi-angular cubemap: like <see cref="Cubemap"/>, but equal angles take equal space on a face (YouTube).</summary>
    Eac = 3,

    /// <summary>Equidistant fisheye circle (<see cref="FisheyeParameters"/>), looking forward. Outside the circle stays black.</summary>
    Fisheye = 4,
}

/// <summary>
/// The six faces of a cube, named from the viewer in the middle (OpenXR axes: +X right, +Y up, −Z forward).
/// The letters r, l, u, d, f, b in <see cref="CubemapLayout.FaceOrder"/> follow this order, as in ffmpeg's v360 filter.
/// </summary>
public enum CubeFace
{
    Right = 0,
    Left = 1,
    Up = 2,
    Down = 3,
    Front = 4,
    Back = 5,
}
