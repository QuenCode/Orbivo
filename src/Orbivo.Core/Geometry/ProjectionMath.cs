using System.Numerics;

namespace Orbivo.Core.Geometry;

/// <summary>
/// C# reference of the projection math in <c>src/Orbivo.Xr/Shaders/projection.hlsl</c>.
/// CLAUDE.md rule 8: always change both together. The GPU tests in Orbivo.Xr.Tests compare them pixel by pixel.
/// </summary>
/// <remarks>
/// Coordinates (CLAUDE.md rule 9): OpenXR convention, right-handed, +X right, +Y up, −Z forward.
/// NDC: x = −1 left edge, +1 right edge; y = −1 bottom edge, +1 top edge.
/// Texture coordinates: u = 0 left edge, v = 0 top edge of the image.
/// </remarks>
public static class ProjectionMath
{
    /// <summary>
    /// Builds the per-pixel ray basis for a view. The shader receives exactly these three vectors.
    /// </summary>
    public static ViewRayBasis CreateViewRayBasis(Quaternion orientation, FovAngles fov)
    {
        float tanLeft = MathF.Tan(fov.AngleLeft);
        float tanRight = MathF.Tan(fov.AngleRight);
        float tanUp = MathF.Tan(fov.AngleUp);
        float tanDown = MathF.Tan(fov.AngleDown);

        // View space: the image plane at z = -1 spans [tanLeft, tanRight] x [tanDown, tanUp].
        var right = new Vector3((tanRight - tanLeft) * 0.5f, 0f, 0f);
        var up = new Vector3(0f, (tanUp - tanDown) * 0.5f, 0f);
        var center = new Vector3((tanRight + tanLeft) * 0.5f, (tanUp + tanDown) * 0.5f, -1f);

        return new ViewRayBasis(
            Vector3.Transform(right, orientation),
            Vector3.Transform(up, orientation),
            Vector3.Transform(center, orientation));
    }

    /// <summary>
    /// Normalized world-space viewing direction for a position in normalized device coordinates.
    /// HLSL: <c>DirectionFromNdc</c>.
    /// </summary>
    public static Vector3 DirectionFromNdc(in ViewRayBasis basis, Vector2 ndc)
    {
        return Vector3.Normalize((ndc.X * basis.Right) + (ndc.Y * basis.Up) + basis.Center);
    }

    /// <summary>
    /// Texture coordinates in an equirectangular 360° image for a normalized direction.
    /// Image center = forward, left in the image = left, top of the image = up.
    /// u wraps around at the back (0 and 1 are the same column). HLSL: <c>Equirect360Uv</c>.
    /// </summary>
    public static Vector2 Equirect360Uv(Vector3 direction)
    {
        float longitude = MathF.Atan2(direction.X, -direction.Z);
        float latitude = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        return new Vector2((longitude / (2f * MathF.PI)) + 0.5f, 0.5f - (latitude / MathF.PI));
    }

    /// <summary>
    /// NDC of the center of a pixel in a render target of the given size (pixel row 0 is the top row).
    /// </summary>
    public static Vector2 PixelCenterToNdc(int x, int y, int width, int height)
    {
        return new Vector2(
            (((x + 0.5f) / width) * 2f) - 1f,
            1f - (((y + 0.5f) / height) * 2f));
    }
}
