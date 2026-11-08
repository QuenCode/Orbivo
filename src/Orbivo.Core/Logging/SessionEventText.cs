using System.Globalization;
using Orbivo.Core.Formats;

namespace Orbivo.Core.Logging;

/// <summary>German details for the event log (F51), built on the threads that record the events.</summary>
public static class SessionEventText
{
    /// <summary>Video time as <c>0:01:23,500</c>.</summary>
    public static string Time(TimeSpan time)
    {
        long milliseconds = (long)Math.Round(Math.Max(0, time.Ticks) / (double)TimeSpan.TicksPerMillisecond);
        return TimeSpan.FromMilliseconds(milliseconds).ToString(@"h\:mm\:ss\,fff", CultureInfo.InvariantCulture);
    }

    /// <summary>Seconds with a decimal comma, e.g. <c>1,5 s</c>.</summary>
    public static string Seconds(TimeSpan duration) => string.Create(GermanCsv.Numbers, $"{duration.TotalSeconds:0.0##} s");

    /// <summary>What the operator changed in the format of a video, e.g. "Projektion: Cubemap; Augen tauschen: an".</summary>
    public static string FormatChanges(FormatOverrides before, FormatOverrides after)
    {
        var changes = new List<string>();
        if (before.Projection != after.Projection)
        {
            changes.Add("Projektion: " + (after.Projection is { } p ? VideoFormatResolver.Describe(p) : "automatisch"));
        }

        if (before.Layout != after.Layout)
        {
            changes.Add("Stereo-Layout: " + (after.Layout is { } l ? VideoFormatResolver.Describe(l) : "automatisch"));
        }

        if (before.SwapEyes != after.SwapEyes)
        {
            changes.Add("Augen tauschen: " + OnOffAuto(after.SwapEyes));
        }

        if (!Equals(before.Cubemap, after.Cubemap))
        {
            changes.Add("Würfel-Layout: " + (after.Cubemap?.ToString() ?? "automatisch"));
        }

        if (!Equals(before.Fisheye, after.Fisheye))
        {
            changes.Add("Fisheye: " + (after.Fisheye is { } f
                ? string.Create(GermanCsv.Numbers, $"{f.FovDegrees:0.#}°, Mitte {f.CenterX:0.###}/{f.CenterY:0.###}, Radius {f.Radius:0.###}")
                : "automatisch"));
        }

        if (before.Offset != after.Offset)
        {
            changes.Add("Ausrichtung: " + (after.Offset is { } o
                ? string.Create(GermanCsv.Numbers, $"Yaw {o.YawDegrees:0.#}°, Pitch {o.PitchDegrees:0.#}°, Roll {o.RollDegrees:0.#}°")
                : "keine"));
        }

        if (before.AudioFixedToPicture != after.AudioFixedToPicture)
        {
            changes.Add("Ton fest am Bild: " + OnOffAuto(after.AudioFixedToPicture));
        }

        if (before.StereoWidthDegrees != after.StereoWidthDegrees)
        {
            changes.Add("Stereo-Breite: " + (after.StereoWidthDegrees is { } w ? string.Create(GermanCsv.Numbers, $"{w:0.#}°") : "Standard"));
        }

        return changes.Count == 0 ? "Keine Änderung" : string.Join("; ", changes);
    }

    /// <summary>True if only the sound settings changed (logged as "Ton" instead of "Format").</summary>
    public static bool OnlySoundChanged(FormatOverrides before, FormatOverrides after)
    {
        return before with { AudioFixedToPicture = null, StereoWidthDegrees = null } == after with { AudioFixedToPicture = null, StereoWidthDegrees = null }
            && before != after;
    }

    private static string OnOffAuto(bool? value) => value switch
    {
        true => "an",
        false => "aus",
        null => "automatisch",
    };
}
