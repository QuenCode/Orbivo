using System.Globalization;

namespace Orbivo.Core.Playback;

/// <summary>Media times as the operator reads and types them.</summary>
public static class MediaTime
{
    /// <summary>"hh:mm:ss.fff", e.g. "00:01:05.250", rounded to the millisecond.</summary>
    public static string Format(TimeSpan time)
    {
        long milliseconds = (long)Math.Round(Math.Max(0, time.Ticks) / (double)TimeSpan.TicksPerMillisecond);
        time = TimeSpan.FromMilliseconds(milliseconds);
        return time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads a time typed by the operator: seconds ("75", "7.5", "7,5"), "mm:ss" or "hh:mm:ss", each with optional
    /// fraction of a second (point or comma). The first part may exceed 59 ("90:00" = 90 minutes); later parts may not.
    /// </summary>
    public static bool TryParse(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Trim().Split(':');
        if (parts.Length > 3)
        {
            return false;
        }

        // Only the last part (seconds) may have a fraction.
        if (!TryParseSeconds(parts[^1], out double seconds) || (parts.Length > 1 && seconds >= 60))
        {
            return false;
        }

        double totalSeconds = seconds;
        double factor = 60;
        for (int i = parts.Length - 2; i >= 0; i--)
        {
            if (!int.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value)
                || (i > 0 && value >= 60))
            {
                return false;
            }

            totalSeconds += value * factor;
            factor *= 60;
        }

        if (totalSeconds > TimeSpan.MaxValue.TotalSeconds / 2)
        {
            return false;
        }

        time = TimeSpan.FromTicks((long)Math.Round(totalSeconds * TimeSpan.TicksPerSecond));
        return true;
    }

    private static bool TryParseSeconds(string text, out double seconds)
    {
        return double.TryParse(
            text.Trim().Replace(',', '.'),
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out seconds);
    }
}
