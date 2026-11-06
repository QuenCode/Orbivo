using Microsoft.Extensions.Logging;

namespace Orbivo.Media.Tests.Support;

/// <summary>Writes log messages into the output of the running test, so failures come with the decoder log.</summary>
internal sealed class TestOutputLoggerFactory : ILoggerFactory
{
    public static TestOutputLoggerFactory Instance { get; } = new();

    public ILogger CreateLogger(string categoryName) => new TestOutputLogger(categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class TestOutputLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string shortCategory = category[(category.LastIndexOf('.') + 1)..];
            TestContext.Current.TestOutputHelper?.WriteLine($"[{logLevel}] {shortCategory}: {formatter(state, exception)}{(exception is null ? string.Empty : " " + exception)}");
        }
    }
}
