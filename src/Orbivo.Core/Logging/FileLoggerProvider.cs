using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Orbivo.Core.Logging;

/// <summary>
/// Writes log entries into one file per session.
/// Logging never blocks the caller: entries go into a bounded queue and a background task writes them.
/// If the queue is full, entries are dropped and counted; the count is written when the provider is disposed.
/// </summary>
/// <remarks>
/// Formatting a log entry allocates. Code on the render thread or in the audio callback must therefore not
/// log per frame or per buffer; it reports through counters in the status snapshot instead.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const int QueueCapacity = 10_000;
    private static readonly TimeSpan DisposeTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<string> _queue;
    private readonly StreamWriter _writer;
    private readonly Task _writeLoop;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private long _droppedEntries;
    private int _disposed;

    public FileLoggerProvider(string filePath, LogLevel minimumLevel = LogLevel.Information)
    {
        FilePath = filePath;
        MinimumLevel = minimumLevel;

        var stream = new FileStream(filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        _queue = Channel.CreateBounded<string>(new BoundedChannelOptions(QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });

        _writeLoop = Task.Run(WriteLoopAsync);
    }

    public string FilePath { get; }

    public LogLevel MinimumLevel { get; }

    /// <summary>Number of entries dropped because the queue was full.</summary>
    public long DroppedEntries => Interlocked.Read(ref _droppedEntries);

    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName, static (name, provider) => new FileLogger(name, provider), this);

    internal void Enqueue(string entry)
    {
        if (!_queue.Writer.TryWrite(entry))
        {
            Interlocked.Increment(ref _droppedEntries);
        }
    }

    private async Task WriteLoopAsync()
    {
        ChannelReader<string> reader = _queue.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out string? entry))
                {
                    await _writer.WriteLineAsync(entry).ConfigureAwait(false);
                }

                await _writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (IOException ex)
        {
            // The log file itself is broken, so the failure cannot be logged there.
            Trace.WriteLine($"Orbivo file logger stopped writing '{FilePath}': {ex}");
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.Writer.TryComplete();
        if (!_writeLoop.Wait(DisposeTimeout))
        {
            // The write loop still owns the writer; leave it alone rather than writing concurrently.
            Trace.WriteLine($"Orbivo file logger did not finish writing '{FilePath}' within {DisposeTimeout.TotalSeconds} s.");
            return;
        }

        long dropped = DroppedEntries;
        if (dropped > 0)
        {
            _writer.WriteLine(LogLineFormatter.Format(
                DateTimeOffset.UtcNow,
                LogLevel.Warning,
                typeof(FileLoggerProvider).FullName!,
                $"{dropped} log entries were dropped because the queue was full.",
                exception: null));
        }

        _writer.Dispose();
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= provider.MinimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Enqueue(LogLineFormatter.Format(DateTimeOffset.UtcNow, logLevel, category, formatter(state, exception), exception));
        }
    }
}
