using Orbivo.Core.Logging;

namespace Orbivo.Core.Tests.Logging;

public sealed class LogPathsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OrbivoTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultDirectory_IsLogsNextToTheExecutable()
    {
        Assert.Equal(Path.Combine(AppContext.BaseDirectory, "Logs"), LogPaths.DefaultDirectory);
    }

    [Fact]
    public void GetSessionFileName_ContainsSortableStartTime()
    {
        var start = new DateTime(2026, 11, 5, 14, 3, 27);

        Assert.Equal("orbivo_2026-11-05_14-03-27.log", LogPaths.GetSessionFileName(start));
        Assert.Equal("orbivo_2026-11-05_14-03-27_2.log", LogPaths.GetSessionFileName(start, attempt: 1));
    }

    [Fact]
    public void CreateSessionFilePath_CreatesDirectoryAndAvoidsExistingFiles()
    {
        var start = new DateTime(2026, 11, 5, 14, 3, 27);

        string first = LogPaths.CreateSessionFilePath(_directory, start);
        File.WriteAllText(first, "taken");
        string second = LogPaths.CreateSessionFilePath(_directory, start);

        Assert.True(Directory.Exists(_directory));
        Assert.EndsWith("orbivo_2026-11-05_14-03-27.log", first, StringComparison.Ordinal);
        Assert.EndsWith("orbivo_2026-11-05_14-03-27_2.log", second, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
