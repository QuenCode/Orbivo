using Orbivo.Core.Logging;

namespace Orbivo.Core.Tests.Logging;

public sealed class LogRetentionTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly LogRetentionPolicy Policy = new(TimeSpan.FromDays(30), MinFiles: 3);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OrbivoTests", Guid.NewGuid().ToString("N"));

    public LogRetentionTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void DefaultPolicy_Keeps30DaysAndAtLeast30Sessions()
    {
        Assert.Equal(TimeSpan.FromDays(30), LogRetentionPolicy.Default.MaxAge);
        Assert.Equal(30, LogRetentionPolicy.Default.MinFiles);
    }

    [Fact]
    public void Prune_DeletesOnlyOldFilesBeyondTheMinimumCount()
    {
        // Ages in days: the newest three stay as the minimum; of the rest, only those over 30 days go.
        string[] names = CreateLogs(40, 2, 10, 60, 1, 3);

        LogPruneResult result = LogRetention.Prune(_directory, Policy, Now);

        Assert.Equal(2, result.DeletedFiles);
        Assert.Empty(result.Failures);
        Assert.Equal(
            new[] { names[1], names[2], names[4], names[5] }.Order(StringComparer.Ordinal),
            Directory.GetFiles(_directory).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Prune_KeepsTheNewestSessionsEvenWhenAllAreOld()
    {
        CreateLogs(90, 100, 110, 120, 130);

        LogPruneResult result = LogRetention.Prune(_directory, Policy, Now);

        Assert.Equal(2, result.DeletedFiles);
        Assert.Equal(3, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void Prune_KeepsRecentFilesBeyondTheMinimumCount()
    {
        CreateLogs(1, 2, 3, 4, 5, 29);

        LogPruneResult result = LogRetention.Prune(_directory, Policy, Now);

        Assert.Equal(0, result.DeletedFiles);
        Assert.Equal(6, Directory.GetFiles(_directory).Length);
    }

    [Fact]
    public void Prune_NeverDeletesTheCurrentSessionOrOtherFiles()
    {
        string[] names = CreateLogs(1, 2, 3, 100);
        string current = Path.Combine(_directory, names[3]);
        string foreign = Path.Combine(_directory, "notes.txt");
        File.WriteAllText(foreign, "keep");
        File.SetLastWriteTimeUtc(foreign, Now.AddDays(-365));

        LogPruneResult result = LogRetention.Prune(_directory, Policy, Now, keepPath: current);

        Assert.Equal(0, result.DeletedFiles);
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(foreign));
    }

    [Fact]
    public void Prune_ReportsFilesThatAreStillOpen()
    {
        string[] names = CreateLogs(1, 2, 3, 100);
        string locked = Path.Combine(_directory, names[3]);
        using (new FileStream(locked, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            LogPruneResult result = LogRetention.Prune(_directory, Policy, Now);

            Assert.Equal(0, result.DeletedFiles);
            Assert.Contains(names[3], Assert.Single(result.Failures), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Prune_MissingDirectory_DoesNothing()
    {
        LogPruneResult result = LogRetention.Prune(Path.Combine(_directory, "missing"), Policy, Now);

        Assert.Equal(0, result.DeletedFiles);
        Assert.Empty(result.Failures);
    }

    /// <summary>Creates one session log per age (days before <see cref="Now"/>) and returns the file names in order.</summary>
    private string[] CreateLogs(params int[] ageDays)
    {
        var names = new string[ageDays.Length];
        for (int i = 0; i < ageDays.Length; i++)
        {
            DateTime written = Now.AddDays(-ageDays[i]);
            names[i] = LogPaths.GetSessionFileName(written.ToLocalTime());
            string path = Path.Combine(_directory, names[i]);
            File.WriteAllText(path, "entry");
            File.SetLastWriteTimeUtc(path, written);
        }

        return names;
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
