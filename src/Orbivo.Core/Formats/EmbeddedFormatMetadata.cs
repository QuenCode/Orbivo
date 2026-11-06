namespace Orbivo.Core.Formats;

/// <summary>
/// Projection from the Spherical Video metadata of a stream (sv3d in MP4, ProjectionType in Matroska, Apple vexu),
/// as FFmpeg reports it (<c>enum AVSphericalProjection</c>; numbers as in libavutil/spherical.h).
/// </summary>
public enum SphericalProjectionKind
{
    Equirectangular = 0,
    Cubemap = 1,
    EquirectangularTile = 2,
    HalfEquirectangular = 3,
    Rectilinear = 4,
    Fisheye = 5,
    ParametricImmersive = 6,
}

/// <summary>
/// Stereo packing from the metadata (st3d in MP4, StereoMode in Matroska, frame packing SEI), as FFmpeg reports it
/// (<c>enum AVStereo3DType</c>; numbers as in libavutil/stereo3d.h).
/// </summary>
public enum Stereo3DKind
{
    TwoD = 0,
    SideBySide = 1,
    TopBottom = 2,
    FrameSequence = 3,
    Checkerboard = 4,
    SideBySideQuincunx = 5,
    Lines = 6,
    Columns = 7,
    Unspecified = 8,
}

/// <summary>Spherical mapping of a stream (sv3d).</summary>
/// <param name="Kind">Projection; a number outside the enum means FFmpeg knows a projection Orbivo does not.</param>
/// <param name="YawDegrees">Initial view (prhd), as stored; not applied (docs/DECISIONS.md, M6).</param>
/// <param name="PitchDegrees">Initial view (prhd).</param>
/// <param name="RollDegrees">Initial view (prhd).</param>
/// <param name="BoundLeft">Equirect tile: part of the full sphere cut off on the left, 0–1.</param>
/// <param name="BoundTop">Equirect tile: cut off at the top, 0–1.</param>
/// <param name="BoundRight">Equirect tile: cut off on the right, 0–1.</param>
/// <param name="BoundBottom">Equirect tile: cut off at the bottom, 0–1.</param>
/// <param name="Padding">Cubemap: unused pixels around each face.</param>
public sealed record SphericalMapping(
    SphericalProjectionKind Kind,
    double YawDegrees = 0,
    double PitchDegrees = 0,
    double RollDegrees = 0,
    double BoundLeft = 0,
    double BoundTop = 0,
    double BoundRight = 0,
    double BoundBottom = 0,
    int Padding = 0);

/// <summary>Stereo packing of a stream (st3d).</summary>
/// <param name="Kind">Packing; a number outside the enum means a packing Orbivo does not know.</param>
/// <param name="Inverted">The right eye comes first (top or left).</param>
public sealed record Stereo3DInfo(Stereo3DKind Kind, bool Inverted = false);

/// <summary>What the video file itself says about its format (F12). Null parts are missing from the file.</summary>
public sealed record EmbeddedFormatMetadata(SphericalMapping? Spherical, Stereo3DInfo? Stereo)
{
    public static EmbeddedFormatMetadata None { get; } = new(null, null);

    /// <summary>Short description for log and status, e.g. "sv3d Cubemap, st3d TopBottom".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Spherical is { } spherical)
        {
            string bounds = spherical.Kind == SphericalProjectionKind.EquirectangularTile
                ? FormattableString.Invariant($" (bounds l {spherical.BoundLeft:0.###} t {spherical.BoundTop:0.###} r {spherical.BoundRight:0.###} b {spherical.BoundBottom:0.###})")
                : string.Empty;
            parts.Add($"sv3d {spherical.Kind}{bounds}");
        }

        if (Stereo is { } stereo)
        {
            parts.Add($"st3d {stereo.Kind}" + (stereo.Inverted ? " inverted" : string.Empty));
        }

        return parts.Count == 0 ? "none" : string.Join(", ", parts);
    }
}
