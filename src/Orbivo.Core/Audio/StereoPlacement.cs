using Orbivo.Core.Formats;

namespace Orbivo.Core.Audio;

/// <summary>
/// How the stereo sound of a video is heard (M7, docs/DECISIONS.md 2026-11-08). Fixed to the picture, the two channels
/// play from two virtual speakers in the video, at <see cref="WidthDegrees"/> left and right of the picture's front;
/// turning the head moves them like the picture. Otherwise the channels go to the ears unchanged (head-locked).
/// </summary>
/// <param name="FixedToPicture">True: virtual speakers in the video. False: plain stereo on the headphones.</param>
/// <param name="WidthDegrees">Angle of each speaker from the front of the picture.</param>
public readonly record struct StereoPlacement(bool FixedToPicture, double WidthDegrees)
{
    public const double MinWidthDegrees = 30;
    public const double MaxWidthDegrees = 90;
    public const double DefaultWidthDegrees = 60;

    /// <summary>Fixed to the picture, 60°: the setting of a video without sidecar choices.</summary>
    public static StereoPlacement Default => new(true, DefaultWidthDegrees);

    /// <summary>Plain stereo on the headphones (also used for mono tracks, which have no direction).</summary>
    public static StereoPlacement HeadLocked => new(false, DefaultWidthDegrees);

    /// <summary>The placement the operator chose for a video (sidecar), the defaults for what is not chosen.</summary>
    public static StereoPlacement From(FormatOverrides overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        return new StereoPlacement(
            overrides.AudioFixedToPicture ?? true,
            overrides.StereoWidthDegrees is { } width ? ClampWidth(width) : DefaultWidthDegrees);
    }

    /// <summary>True if <paramref name="degrees"/> is an allowed width.</summary>
    public static bool IsValidWidth(double degrees) =>
        double.IsFinite(degrees) && degrees >= MinWidthDegrees && degrees <= MaxWidthDegrees;

    public static double ClampWidth(double degrees) =>
        double.IsFinite(degrees) ? Math.Clamp(degrees, MinWidthDegrees, MaxWidthDegrees) : DefaultWidthDegrees;

    /// <summary>German text for the operator, e.g. "fest am Bild, Lautsprecher bei ±60°".</summary>
    public string Describe() => FixedToPicture
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"fest am Bild, Lautsprecher bei ±{WidthDegrees:0}°")
        : "Stereo direkt (dreht mit dem Kopf)";
}
