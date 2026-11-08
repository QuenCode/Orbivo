namespace Orbivo.Core.Logging;

/// <summary>Which session logs are kept in the log directory.</summary>
/// <param name="MaxAge">Session logs last written longer ago than this are deleted, unless they are among the newest <paramref name="MinFiles"/>.</param>
/// <param name="MinFiles">The newest this many session logs are always kept, however old they are.</param>
public sealed record LogRetentionPolicy(TimeSpan MaxAge, int MinFiles)
{
    /// <summary>30 days, but at least the last 30 sessions (decision 2026-11-08).</summary>
    public static LogRetentionPolicy Default { get; } = new(TimeSpan.FromDays(30), 30);
}

/// <summary>Outcome of <see cref="LogRetention.Prune"/>.</summary>
/// <param name="DeletedFiles">Number of session logs deleted.</param>
/// <param name="DeletedBytes">Their size together.</param>
/// <param name="Failures">Files that should have gone but could not be deleted, with the reason.</param>
public sealed record LogPruneResult(int DeletedFiles, long DeletedBytes, IReadOnlyList<string> Failures);

/// <summary>
/// Deletes old session logs so the log directory does not grow without bound.
/// Only files named like session logs (<c>orbivo_*.log</c>) are touched.
/// </summary>
public static class LogRetention
{
    public const string SessionLogPattern = "orbivo_*.log";

    /// <summary>
    /// Deletes the session logs in <paramref name="directory"/> that are older than the policy allows and not among
    /// the newest ones it keeps. <paramref name="keepPath"/> (the current session's log) is never deleted.
    /// Files that cannot be deleted (e.g. still open in another Orbivo instance) are reported, not thrown.
    /// </summary>
    public static LogPruneResult Prune(string directory, LogRetentionPolicy policy, DateTime nowUtc, string? keepPath = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!Directory.Exists(directory))
        {
            return new LogPruneResult(0, 0, []);
        }

        string? keepFullPath = keepPath is null ? null : Path.GetFullPath(keepPath);
        FileInfo[] newestFirst = new DirectoryInfo(directory)
            .GetFiles(SessionLogPattern, SearchOption.TopDirectoryOnly)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.Ordinal)
            .ToArray();

        var failures = new List<string>();
        int deletedFiles = 0;
        long deletedBytes = 0;
        for (int i = policy.MinFiles; i < newestFirst.Length; i++)
        {
            FileInfo file = newestFirst[i];
            bool isCurrent = keepFullPath is not null
                && string.Equals(file.FullName, keepFullPath, StringComparison.OrdinalIgnoreCase);
            if (isCurrent || nowUtc - file.LastWriteTimeUtc <= policy.MaxAge)
            {
                continue;
            }

            try
            {
                long length = file.Length;
                file.Delete();
                deletedFiles++;
                deletedBytes += length;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{file.Name}: {exception.Message}");
            }
        }

        return new LogPruneResult(deletedFiles, deletedBytes, failures);
    }
}
