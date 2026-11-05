using Microsoft.Extensions.Logging;
using Orbivo.Core.Logging;

namespace Orbivo.Core.Tests.Logging;

public sealed class LogLineFormatterTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 11, 5, 14, 3, 27, 123, TimeSpan.FromHours(1));

    [Fact]
    public void Format_WritesUtcTimestampLevelCategoryAndMessage()
    {
        string line = LogLineFormatter.Format(Timestamp, LogLevel.Warning, "Orbivo.Test", "Hello", exception: null);

        Assert.Equal("2026-11-05T13:03:27.123Z WRN Orbivo.Test: Hello", line);
    }

    [Fact]
    public void Format_AppendsExceptionOnIndentedLines()
    {
        var exception = new InvalidOperationException("Boom");

        string text = LogLineFormatter.Format(Timestamp, LogLevel.Error, "Cat", "Failed", exception);
        string[] lines = text.Split(Environment.NewLine);

        Assert.Equal("2026-11-05T13:03:27.123Z ERR Cat: Failed", lines[0]);
        Assert.Equal("    System.InvalidOperationException: Boom", lines[1]);
        Assert.All(lines.Skip(1), line => Assert.StartsWith("    ", line, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(LogLevel.Trace, "TRC")]
    [InlineData(LogLevel.Debug, "DBG")]
    [InlineData(LogLevel.Information, "INF")]
    [InlineData(LogLevel.Warning, "WRN")]
    [InlineData(LogLevel.Error, "ERR")]
    [InlineData(LogLevel.Critical, "CRT")]
    public void GetLevelTag_UsesThreeLetterTags(LogLevel level, string expected)
    {
        Assert.Equal(expected, LogLineFormatter.GetLevelTag(level));
    }
}
