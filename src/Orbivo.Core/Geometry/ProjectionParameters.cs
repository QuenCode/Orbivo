using System.Numerics;
using System.Runtime.CompilerServices;
using Orbivo.Core.Formats;

namespace Orbivo.Core.Geometry;

/// <summary>
/// Per-face transform for cube projections, two rows per face (<see cref="CubeFace"/> order), as the shader receives them:
/// row 2f = (a00, a01, a10, a11), row 2f+1 = (center u, center v, clamp half width, clamp half height).
/// See <see cref="ProjectionMath.CreateProjectionParameters"/>.
/// </summary>
[InlineArray(Count)]
public struct CubeFaceRows
{
    public const int Count = 12;

    private Vector4 _element0;
}

/// <summary>
/// The projection constants of one eye, made by <see cref="ProjectionMath.CreateProjectionParameters"/> and read
/// by <see cref="ProjectionMath.EyeUv"/> and the shader (<c>ProjectedEyeUv</c> in projection.hlsl). A struct: building
/// it on the render thread does not allocate.
/// </summary>
public struct ProjectionParameters
{
    public ProjectionType Projection;

    /// <summary>Fisheye: xy = circle center, zw = radius along u and v, all in the eye's texture coordinates.</summary>
    public Vector4 Fisheye;

    /// <summary>Fisheye: half the field of view in radians.</summary>
    public float FisheyeHalfFov;

    /// <summary>Cubemap and EAC: where each face lies in the eye's picture.</summary>
    public CubeFaceRows Faces;
}
