using Microsoft.Extensions.Logging;
using Orbivo.Core.Logging;

namespace Orbivo.Core.Tests.Logging;

public sealed class FileLoggerProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "OrbivoTests", Guid.NewGuid().ToString("N"));

    public FileLoggerProviderTests()
    {
        Directory.CreateDirectory(_directory);
    }

    [Fact]
    public void Dispose_FlushesAllEntriesInOrder()
    {
        string path = Path.Combine(_directory, "session.log");
        using (var provider = new FileLoggerProvider(path))
        {
            ILogger logger = provider.CreateLogger("Orbivo.Test");
            for (int i = 0; i < 100; i++)
            {
                logger.LogInformation("Entry {Index}", i);
            }
        }

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(100, lines.Length);
        Assert.EndsWith("INF Orbivo.Test: Entry 0", lines[0], StringComparison.Ordinal);
        Assert.EndsWith("INF Orbivo.Test: Entry 99", lines[99], StringComparison.Ordinal);
    }

    [Fact]
    public void EntriesBelowMinimumLevel_AreNotWritten()
    {
        string path = Path.Combine(_directory, "filtered.log");
        using (var provider = new FileLoggerProvider(path, LogLevel.Warning))
        {
            ILogger logger = provider.CreateLogger("Cat");
            logger.LogInformation("hidden");
            logger.LogWarning("shown");
        }

        string line = Assert.Single(File.ReadAllLines(path));
        Assert.EndsWith("WRN Cat: shown", line, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateLogger_ReturnsSameInstancePerCategory()
    {
        using var provider = new FileLoggerProvider(Path.Combine(_directory, "same.log"));

        Assert.Same(provider.CreateLogger("A"), provider.CreateLogger("A"));
        Assert.NotSame(provider.CreateLogger("A"), provider.CreateLogger("B"));
    }

    public void Dispose()
    {
        Directory.Delete(_directory, recursive: true);
    }
}
