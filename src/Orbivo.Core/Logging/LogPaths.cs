using System.Globalization;

namespace Orbivo.Core.Logging;

/// <summary>
/// Locations and names of the per-session diagnostic log files.
/// </summary>
public static class LogPaths
{
    /// <summary>
    /// Default directory: <c>%LOCALAPPDATA%\Orbivo\logs</c>.
    /// </summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Orbivo",
        "logs");

    /// <summary>
    /// File name for a session that started at <paramref name="localStartTime"/>,
    /// e.g. <c>orbivo_2026-11-05_14-03-27.log</c>. Names sort chronologically.
    /// </summary>
    public static string GetSessionFileName(DateTime localStartTime, int attempt = 0)
    {
        string stamp = localStartTime.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
        return attempt == 0 ? $"orbivo_{stamp}.log" : $"orbivo_{stamp}_{attempt + 1}.log";
    }

    /// <summary>
    /// Creates <paramref name="directory"/> if needed and returns a path for a new session log
    /// that does not exist yet.
    /// </summary>
    public static string CreateSessionFilePath(string directory, DateTime localStartTime)
    {
        Directory.CreateDirectory(directory);
        for (int attempt = 0; ; attempt++)
        {
            string path = Path.Combine(directory, GetSessionFileName(localStartTime, attempt));
            if (!File.Exists(path))
            {
                return path;
            }
        }
    }
}
