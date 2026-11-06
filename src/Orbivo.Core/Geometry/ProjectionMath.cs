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
    /// Texture coordinates in an equirectangular 180° image (VR180) for a normalized direction: the front half,
    /// left edge = 90° left, right edge = 90° right. <paramref name="valid"/> is false for the back half (black).
    /// HLSL: <c>Equirect180Uv</c>.
    /// </summary>
    public static Vector2 Equirect180Uv(Vector3 direction, out bool valid)
    {
        float longitude = MathF.Atan2(direction.X, -direction.Z);
        float latitude = MathF.Asin(Math.Clamp(direction.Y, -1f, 1f));
        valid = MathF.Abs(longitude) <= 0.5f * MathF.PI;
        return new Vector2((longitude / MathF.PI) + 0.5f, 0.5f - (latitude / MathF.PI));
    }

    /// <summary>
    /// The cube face a normalized direction hits, and the position on it: s to the right, t upwards, both in [−1, 1],
    /// as seen from the middle of the cube (unturned faces, see <see cref="CubemapLayout"/>). HLSL: <c>CubeFaceSt</c>.
    /// </summary>
    /// <remarks>Ties on a cube edge go to X before Y before Z, the same in HLSL.</remarks>
    public static CubeFace CubeFaceSt(Vector3 direction, out Vector2 st)
    {
        Vector3 a = Vector3.Abs(direction);
        if (a.X >= a.Y && a.X >= a.Z)
        {
            if (direction.X > 0f)
            {
                st = new Vector2(direction.Z, direction.Y) / a.X;
                return CubeFace.Right;
            }

            st = new Vector2(-direction.Z, direction.Y) / a.X;
            return CubeFace.Left;
        }

        if (a.Y >= a.Z)
        {
            if (direction.Y > 0f)
            {
                st = new Vector2(direction.X, direction.Z) / a.Y;
                return CubeFace.Up;
            }

            st = new Vector2(direction.X, -direction.Z) / a.Y;
            return CubeFace.Down;
        }

        if (direction.Z < 0f)
        {
            st = new Vector2(direction.X, direction.Y) / a.Z;
            return CubeFace.Front;
        }

        st = new Vector2(-direction.X, direction.Y) / a.Z;
        return CubeFace.Back;
    }

    /// <summary>
    /// The equi-angular correction of EAC: equal angles take equal space on a face. Maps a face coordinate
    /// tan(angle) in [−1, 1] to angle / 45° in [−1, 1]. HLSL: <c>EquiAngular</c>.
    /// </summary>
    public static Vector2 EquiAngular(Vector2 st)
    {
        const float Scale = 4f / MathF.PI;
        return new Vector2(Scale * MathF.Atan(st.X), Scale * MathF.Atan(st.Y));
    }

    /// <summary>
    /// Builds the projection constants of one eye. Runs on the render thread every frame: no allocations.
    /// </summary>
    /// <param name="format">Projection, cube layout and fisheye circle.</param>
    /// <param name="eyeWidth">Width of one eye's picture in texels.</param>
    /// <param name="eyeHeight">Height of one eye's picture in texels.</param>
    public static ProjectionParameters CreateProjectionParameters(VideoFormat format, int eyeWidth, int eyeHeight)
    {
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfLessThan(eyeWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(eyeHeight, 1);

        var parameters = new ProjectionParameters { Projection = format.Projection };

        FisheyeParameters fisheye = format.Fisheye;
        parameters.Fisheye = new Vector4(
            (float)fisheye.CenterX,
            (float)fisheye.CenterY,
            (float)(fisheye.Radius * eyeHeight / eyeWidth),
            (float)fisheye.Radius);
        parameters.FisheyeHalfFov = (float)(fisheye.FovDegrees * Math.PI / 360.0);

        CubemapLayout layout = format.Cubemap;
        float cellWidth = 1f / layout.Columns;
        float cellHeight = 1f / layout.Rows;
        float faceWidth = cellWidth - (2f * layout.PaddingPixels / eyeWidth);
        float faceHeight = cellHeight - (2f * layout.PaddingPixels / eyeHeight);

        // Stay half a texel inside the face: bilinear filtering must not mix in the neighboring cell.
        float clampWidth = MathF.Max(0f, (0.5f * faceWidth) - (0.5f / eyeWidth));
        float clampHeight = MathF.Max(0f, (0.5f * faceHeight) - (0.5f / eyeHeight));
        for (int face = 0; face < 6; face++)
        {
            (int column, int row, int rotation) = layout.GetCell((CubeFace)face);

            // q = (s, -t) / 2 is the position on the unturned face (x right, y down, in [-0.5, 0.5]).
            // A clockwise quarter turn moves the top of the face to the right: (x, y) -> (-y, x).
            parameters.Faces[2 * face] = rotation switch
            {
                1 => new Vector4(0f, -faceWidth, faceHeight, 0f),
                2 => new Vector4(-faceWidth, 0f, 0f, -faceHeight),
                3 => new Vector4(0f, faceWidth, -faceHeight, 0f),
                _ => new Vector4(faceWidth, 0f, 0f, faceHeight),
            };
            parameters.Faces[(2 * face) + 1] = new Vector4(
                (column + 0.5f) * cellWidth,
                (row + 0.5f) * cellHeight,
                clampWidth,
                clampHeight);
        }

        return parameters;
    }

    /// <summary>
    /// Texture coordinates within one eye's picture for a normalized direction, for every projection (F10).
    /// <paramref name="valid"/> is false where the picture has nothing (back of VR180, outside the fisheye circle);
    /// the shader shows black there. HLSL: <c>ProjectedEyeUv</c>.
    /// </summary>
    public static Vector2 EyeUv(Vector3 direction, in ProjectionParameters parameters, out bool valid)
    {
        switch (parameters.Projection)
        {
            case ProjectionType.Equirect180:
                return Equirect180Uv(direction, out valid);

            case ProjectionType.Cubemap:
            case ProjectionType.Eac:
            {
                valid = true;
                CubeFace face = CubeFaceSt(direction, out Vector2 st);
                if (parameters.Projection == ProjectionType.Eac)
                {
                    st = EquiAngular(st);
                }

                var q = new Vector2(0.5f * st.X, -0.5f * st.Y);
                Vector4 transform = parameters.Faces[2 * (int)face];
                Vector4 placement = parameters.Faces[(2 * (int)face) + 1];
                var center = new Vector2(placement.X, placement.Y);
                var half = new Vector2(placement.Z, placement.W);
                var uv = new Vector2(
                    center.X + (transform.X * q.X) + (transform.Y * q.Y),
                    center.Y + (transform.Z * q.X) + (transform.W * q.Y));
                return Vector2.Clamp(uv, center - half, center + half);
            }

            case ProjectionType.Fisheye:
            {
                // Equidistant: the angle from the optical axis (forward, -Z) grows linearly with the distance from the center.
                var lateral = new Vector2(direction.X, direction.Y);
                float length = lateral.Length();
                float angle = MathF.Atan2(length, -direction.Z);
                valid = angle <= parameters.FisheyeHalfFov;

                // lateral / length * angle / halfFov, written without dividing by zero straight ahead (angle ~ length there).
                float scale = (length > 1e-6f ? angle / length : 1f) / parameters.FisheyeHalfFov;
                Vector4 circle = parameters.Fisheye;
                return new Vector2(
                    circle.X + (lateral.X * scale * circle.Z),
                    circle.Y - (lateral.Y * scale * circle.W));
            }

            default:
                valid = true;
                return Equirect360Uv(direction);
        }
    }

    /// <summary>
    /// Where one eye lies in the source image (F11). Mono uses the whole image; top-bottom puts the left eye on top,
    /// side-by-side on the left. HLSL: the constants <c>SourceScaleOffset</c> and <c>SourceClamp</c>.
    /// </summary>
    /// <remarks>
    /// Sampling stays half a texel inside the eye, so bilinear filtering never mixes in the other eye.
    /// Along a wrapping axis (u of a full-width equirect 360 eye) nothing is clamped, so there is no seam at the back.
    /// </remarks>
    /// <param name="layout">Stereo layout of the image.</param>
    /// <param name="eye">The eye whose half to use (after swapping, see <see cref="VideoFormat.SourceEye"/>).</param>
    /// <param name="width">Image width in texels.</param>
    /// <param name="height">Image height in texels.</param>
    /// <param name="wrapsAround">True if u of a full-width eye wraps around (equirect 360); false clamps it too.</param>
    public static EyeSourceRect CreateEyeSourceRect(StereoLayout layout, Eye eye, int width, int height, bool wrapsAround = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);

        // No clamp along a wrapping axis: any value outside [0, 1] works, the sampler wraps u.
        const float Open = 2f;
        float uMin = wrapsAround ? -Open : 0.5f / width;
        float uMax = wrapsAround ? Open : 1f - (0.5f / width);
        bool second = eye == Eye.Right;
        return layout switch
        {
            StereoLayout.TopBottom => new EyeSourceRect(
                new Vector2(1f, 0.5f),
                new Vector2(0f, second ? 0.5f : 0f),
                new Vector2(uMin, 1f / height),
                new Vector2(uMax, 1f - (1f / height))),
            StereoLayout.SideBySide => new EyeSourceRect(
                new Vector2(0.5f, 1f),
                new Vector2(second ? 0.5f : 0f, 0f),
                new Vector2(1f / width, 0f),
                new Vector2(1f - (1f / width), 1f)),
            _ => new EyeSourceRect(Vector2.One, Vector2.Zero, new Vector2(uMin, 0f), new Vector2(uMax, 1f)),
        };
    }

    /// <summary>Size of one eye's picture in texels.</summary>
    public static (int Width, int Height) EyeSize(StereoLayout layout, int width, int height)
    {
        return layout switch
        {
            StereoLayout.TopBottom => (width, Math.Max(1, height / 2)),
            StereoLayout.SideBySide => (Math.Max(1, width / 2), height),
            _ => (width, height),
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
