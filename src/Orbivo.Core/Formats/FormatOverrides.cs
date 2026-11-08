using Orbivo.Core.Geometry;

namespace Orbivo.Core.Formats;

/// <summary>
/// The operator's choices for one video (F13, F14), as stored in the sidecar. Null = automatic (metadata or guess).
/// </summary>
/// <param name="Projection">Projection, or null for automatic.</param>
/// <param name="Layout">Stereo layout, or null for automatic.</param>
/// <param name="SwapEyes">Swap the eyes, or null for automatic (metadata can say the eyes are swapped).</param>
/// <param name="Cubemap">Cube face layout, or null for automatic (3 × 2, EAC: YouTube layout).</param>
/// <param name="Fisheye">Fisheye circle, or null for automatic (180°, centered).</param>
/// <param name="Offset">Orientation offset, or null for none.</param>
/// <param name="AudioFixedToPicture">Stereo sound fixed to the picture (M7), or null for the default (on).</param>
/// <param name="StereoWidthDegrees">Angle of the virtual stereo speakers, or null for the default (75°).</param>
public sealed record FormatOverrides(
    ProjectionType? Projection = null,
    StereoLayout? Layout = null,
    bool? SwapEyes = null,
    CubemapLayout? Cubemap = null,
    FisheyeParameters? Fisheye = null,
    OrientationOffset? Offset = null,
    bool? AudioFixedToPicture = null,
    double? StereoWidthDegrees = null)
{
    public static FormatOverrides None { get; } = new();

    /// <summary>True if nothing is overridden; then no sidecar is needed.</summary>
    public bool IsEmpty =>
        Projection is null && Layout is null && SwapEyes is null && Cubemap is null && Fisheye is null
        && (Offset is null || Offset.Value.IsZero) && AudioFixedToPicture is null && StereoWidthDegrees is null;
}
