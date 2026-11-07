using System.Numerics;

namespace Orbivo.Core.Geometry;

/// <summary>
/// One view that covers what both eyes see (live view for the operator, F42): it looks along the head direction
/// (between the eyes) and its field of view reaches from the left edge of the left eye to the right edge of the right
/// eye, and from the lowest to the highest edge of either eye.
/// </summary>
public static class CombinedView
{
    /// <summary>Edges are kept within ±80° of the view axis, so the flat image stays finite.</summary>
    public const float MaxEdgeAngle = 80f * MathF.PI / 180f;

    /// <param name="left">View of the left eye.</param>
    /// <param name="right">View of the right eye.</param>
    /// <remarks>
    /// Eyes that are turned against each other (canted displays) are taken into account by shifting their edges by the
    /// eye's yaw and pitch relative to the head direction. That is exact for yaw alone and close enough for the few
    /// degrees headsets use.
    /// </remarks>
    public static ViewParameters Create(in ViewParameters left, in ViewParameters right)
    {
        Quaternion leftOrientation = Quaternion.Normalize(left.Orientation);
        Quaternion rightOrientation = Quaternion.Normalize(right.Orientation);
        if (Quaternion.Dot(leftOrientation, rightOrientation) < 0f)
        {
            rightOrientation = -rightOrientation;
        }

        Quaternion center = Quaternion.Normalize(Quaternion.Slerp(leftOrientation, rightOrientation, 0.5f));
        (float leftYaw, float leftPitch) = RelativeYawPitch(center, leftOrientation);
        (float rightYaw, float rightPitch) = RelativeYawPitch(center, rightOrientation);

        // Yaw positive = turned left (docs/DECISIONS.md); FOV angles are positive to the right and up.
        float angleLeft = MathF.Min(left.Fov.AngleLeft - leftYaw, right.Fov.AngleLeft - rightYaw);
        float angleRight = MathF.Max(left.Fov.AngleRight - leftYaw, right.Fov.AngleRight - rightYaw);
        float angleUp = MathF.Max(left.Fov.AngleUp + leftPitch, right.Fov.AngleUp + rightPitch);
        float angleDown = MathF.Min(left.Fov.AngleDown + leftPitch, right.Fov.AngleDown + rightPitch);

        var fov = new FovAngles(
            Math.Clamp(angleLeft, -MaxEdgeAngle, MaxEdgeAngle),
            Math.Clamp(angleRight, -MaxEdgeAngle, MaxEdgeAngle),
            Math.Clamp(angleUp, -MaxEdgeAngle, MaxEdgeAngle),
            Math.Clamp(angleDown, -MaxEdgeAngle, MaxEdgeAngle));
        return new ViewParameters(center, fov);
    }

    /// <summary>Width / height of the flat image of <paramref name="fov"/> with square pixels.</summary>
    public static float AspectRatio(in FovAngles fov)
    {
        float width = MathF.Tan(fov.AngleRight) - MathF.Tan(fov.AngleLeft);
        float height = MathF.Tan(fov.AngleUp) - MathF.Tan(fov.AngleDown);
        return width > 0f && height > 0f ? width / height : 1f;
    }

    /// <summary>
    /// The largest image size with <paramref name="aspectRatio"/> that fits into <paramref name="maxWidth"/> ×
    /// <paramref name="maxHeight"/> (at least 1 × 1).
    /// </summary>
    public static (int Width, int Height) FitSize(float aspectRatio, int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0 || !(aspectRatio > 0f) || !float.IsFinite(aspectRatio))
        {
            return (Math.Max(1, maxWidth), Math.Max(1, maxHeight));
        }

        int width = maxWidth;
        int height = (int)MathF.Round(maxWidth / aspectRatio);
        if (height > maxHeight)
        {
            height = maxHeight;
            width = (int)MathF.Round(maxHeight * aspectRatio);
        }

        return (Math.Clamp(width, 1, maxWidth), Math.Clamp(height, 1, maxHeight));
    }

    private static (float Yaw, float Pitch) RelativeYawPitch(Quaternion center, Quaternion eye)
    {
        return OrientationMath.ToYawPitch(Quaternion.Normalize(Quaternion.Conjugate(center) * eye));
    }
}
