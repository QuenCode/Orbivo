namespace Orbivo.Core.Geometry;

/// <summary>
/// Field of view of one view as four angles in radians, same meaning as OpenXR <c>XrFovf</c>.
/// For a view that looks along its forward axis, <see cref="AngleLeft"/> and <see cref="AngleDown"/> are negative.
/// The angles may be asymmetric (headset eyes).
/// </summary>
public readonly record struct FovAngles(float AngleLeft, float AngleRight, float AngleUp, float AngleDown)
{
    /// <summary>
    /// Symmetric field of view for a desktop window: the vertical angle is given,
    /// the horizontal angle follows from the aspect ratio (width / height) with square pixels.
    /// </summary>
    public static FovAngles FromVertical(float verticalFovRadians, float aspectRatio)
    {
        if (!(verticalFovRadians > 0f && verticalFovRadians < MathF.PI))
        {
            throw new ArgumentOutOfRangeException(nameof(verticalFovRadians), verticalFovRadians, "Must be between 0 and pi.");
        }

        if (!(aspectRatio > 0f) || float.IsInfinity(aspectRatio))
        {
            throw new ArgumentOutOfRangeException(nameof(aspectRatio), aspectRatio, "Must be positive and finite.");
        }

        float halfVertical = verticalFovRadians * 0.5f;
        float halfHorizontal = MathF.Atan(MathF.Tan(halfVertical) * aspectRatio);
        return new FovAngles(-halfHorizontal, halfHorizontal, halfVertical, -halfVertical);
    }

    /// <summary>Total horizontal angle in radians.</summary>
    public float Horizontal => AngleRight - AngleLeft;

    /// <summary>Total vertical angle in radians.</summary>
    public float Vertical => AngleUp - AngleDown;
}
