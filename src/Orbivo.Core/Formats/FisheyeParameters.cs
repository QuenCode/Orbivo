using System.Diagnostics.CodeAnalysis;

namespace Orbivo.Core.Formats;

/// <summary>
/// An equidistant fisheye circle in the picture of one eye (F10): the angle from the optical axis (forward) grows
/// linearly with the distance from the circle's center and reaches half the field of view at the radius.
/// </summary>
/// <param name="FovDegrees">Field of view across the circle in degrees (180 = hemisphere).</param>
/// <param name="CenterX">Center of the circle, 0 = left edge, 1 = right edge of the eye's picture.</param>
/// <param name="CenterY">Center of the circle, 0 = top edge, 1 = bottom edge of the eye's picture.</param>
/// <param name="Radius">Radius of the circle as a fraction of the eye picture's height (0.5 = touches top and bottom).</param>
public readonly record struct FisheyeParameters(double FovDegrees, double CenterX, double CenterY, double Radius)
{
    public const double MinFovDegrees = 60;
    public const double MaxFovDegrees = 360;
    public const double MinRadius = 0.05;
    public const double MaxRadius = 2;

    /// <summary>180°, centered, touching the top and bottom edge.</summary>
    public static FisheyeParameters Default { get; } = new(180, 0.5, 0.5, 0.5);

    /// <summary>Checks the ranges; <paramref name="error"/> explains the first problem (English, for the log).</summary>
    public bool TryValidate([NotNullWhen(false)] out string? error)
    {
        if (!double.IsFinite(FovDegrees) || FovDegrees is < MinFovDegrees or > MaxFovDegrees)
        {
            error = $"Fisheye field of view {FovDegrees} is outside {MinFovDegrees}-{MaxFovDegrees} degrees.";
            return false;
        }

        if (!double.IsFinite(CenterX) || !double.IsFinite(CenterY) || CenterX is < 0 or > 1 || CenterY is < 0 or > 1)
        {
            error = $"Fisheye center ({CenterX}, {CenterY}) is outside 0-1.";
            return false;
        }

        if (!double.IsFinite(Radius) || Radius is < MinRadius or > MaxRadius)
        {
            error = $"Fisheye radius {Radius} is outside {MinRadius}-{MaxRadius}.";
            return false;
        }

        error = null;
        return true;
    }
}
