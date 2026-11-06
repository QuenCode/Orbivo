using System.Numerics;
using Orbivo.Core.Formats;

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
    /// Where one eye lies in the source image (F11). Mono uses the whole image; top-bottom puts the left eye on top,
    /// side-by-side on the left. HLSL: the constants <c>SourceScaleOffset</c> and <c>SourceClamp</c>.
    /// </summary>
    /// <remarks>
    /// Sampling stays half a texel inside the eye, so bilinear filtering never mixes in the other eye.
    /// Along a wrapping axis (u of a full-width eye) nothing is clamped, so there is no seam at the back.
    /// </remarks>
    /// <param name="layout">Stereo layout of the image.</param>
    /// <param name="eye">The eye to render.</param>
    /// <param name="width">Image width in texels.</param>
    /// <param name="height">Image height in texels.</param>
    public static EyeSourceRect CreateEyeSourceRect(StereoLayout layout, Eye eye, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);

        // No clamp along a wrapping axis: any value outside [0, 1] works, the sampler wraps u.
        const float Open = 2f;
        bool second = eye == Eye.Right;
        return layout switch
        {
            StereoLayout.TopBottom => new EyeSourceRect(
                new Vector2(1f, 0.5f),
                new Vector2(0f, second ? 0.5f : 0f),
                new Vector2(-Open, 1f / height),
                new Vector2(Open, 1f - (1f / height))),
            StereoLayout.SideBySide => new EyeSourceRect(
                new Vector2(0.5f, 1f),
                new Vector2(second ? 0.5f : 0f, 0f),
                new Vector2(1f / width, 0f),
                new Vector2(1f - (1f / width), 1f)),
            _ => new EyeSourceRect(Vector2.One, Vector2.Zero, new Vector2(-Open, 0f), new Vector2(Open, 1f)),
        };
    }

    /// <summary>
    /// Texture coordinates in the whole source image for coordinates within one eye's image. HLSL: <c>SourceUv</c>.
    /// </summary>
    public static Vector2 SourceUv(Vector2 eyeUv, in EyeSourceRect rect)
    {
        return (Vector2.Clamp(eyeUv, rect.ClampMin, rect.ClampMax) * rect.Scale) + rect.Offset;
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
