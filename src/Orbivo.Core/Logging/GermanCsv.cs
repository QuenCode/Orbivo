using System.Globalization;
using System.Text;

namespace Orbivo.Core.Logging;

/// <summary>
/// CSV as a German Excel opens it with a double click (docs/DECISIONS.md, M8): semicolon between fields, decimal comma,
/// UTF-8 with byte order mark. Fields with a semicolon, quote or line break are quoted.
/// </summary>
public static class GermanCsv
{
    public const char Separator = ';';

    /// <summary>German number format: decimal comma, no thousands separator.</summary>
    public static readonly NumberFormatInfo Numbers = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = string.Empty,
        NegativeSign = "-",
    };

    /// <summary>UTF-8 with byte order mark (Excel needs it to read umlauts correctly).</summary>
    public static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    /// <summary>Appends <paramref name="value"/> as one field, quoted if needed.</summary>
    public static void AppendText(StringBuilder line, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        if (value.AsSpan().IndexOfAny(";\"\r\n") < 0)
        {
            line.Append(value);
            return;
        }

        line.Append('"').Append(value.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
    }

    /// <summary>Appends a number with a fixed count of decimals and a decimal comma (e.g. <c>-12,345</c>).</summary>
    public static void AppendNumber(StringBuilder line, double value, int decimals)
    {
        if (!double.IsFinite(value))
        {
            return;
        }

        line.Append(value.ToString(DecimalFormats[decimals], Numbers));
    }

    /// <summary>Appends an integer.</summary>
    public static void AppendInteger(StringBuilder line, long value)
    {
        line.Append(value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>UTC time as <c>2026-11-08 14:12:03,123</c> (Excel reads it as date and time).</summary>
    public static void AppendUtc(StringBuilder line, DateTime utc)
    {
        line.Append(utc.ToString("yyyy-MM-dd HH:mm:ss,fff", CultureInfo.InvariantCulture));
    }

    public static void AppendSeparator(StringBuilder line) => line.Append(Separator);

    private static readonly string[] DecimalFormats = ["0", "0.0", "0.00", "0.000", "0.0000", "0.00000", "0.000000"];
}
