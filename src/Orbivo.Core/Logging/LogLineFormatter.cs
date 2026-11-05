using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Orbivo.Core.Logging;

/// <summary>
/// Formats one log entry as text: <c>2026-11-05T13:03:27.123Z INF Category: message</c>.
/// Exceptions follow on indented lines.
/// </summary>
public static class LogLineFormatter
{
    public static string Format(DateTimeOffset timestamp, LogLevel level, string category, string message, Exception? exception)
    {
        var builder = new StringBuilder(64 + category.Length + message.Length);
        builder.Append(timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
        builder.Append(' ');
        builder.Append(GetLevelTag(level));
        builder.Append(' ');
        builder.Append(category);
        builder.Append(": ");
        builder.Append(message);

        if (exception is not null)
        {
            foreach (string line in exception.ToString().Split('\n'))
            {
                builder.AppendLine();
                builder.Append("    ");
                builder.Append(line.TrimEnd('\r'));
            }
        }

        return builder.ToString();
    }

    public static string GetLevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };
}
