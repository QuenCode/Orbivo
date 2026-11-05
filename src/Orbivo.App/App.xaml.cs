using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Orbivo.App.ViewModels;
using Orbivo.Audio;
using Orbivo.Core.Logging;
using Orbivo.Core.Status;
using Orbivo.Media;

namespace Orbivo.App;

/// <summary>
/// Composition root: sets up logging, checks native components and shows the main window.
/// </summary>
public partial class App : Application
{
    private FileLoggerProvider? _fileLoggerProvider;
    private ILoggerFactory? _loggerFactory;
    private ILogger? _logger;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        StartupOptions options = StartupOptions.Parse(e.Args);

        string logFilePath = LogPaths.CreateSessionFilePath(LogPaths.DefaultDirectory, DateTime.Now);
        _fileLoggerProvider = new FileLoggerProvider(logFilePath);
        _loggerFactory = new LoggerFactory([_fileLoggerProvider]);
        _logger = _loggerFactory.CreateLogger<App>();

        RegisterGlobalExceptionHandlers();

        _logger.LogInformation(
            "Orbivo {Version} starting. OS: {Os}, runtime: {Runtime}, XR: {XrMode}.",
            Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            options.NoXr ? "disabled (--no-xr)" : "enabled");
        if (options.UnknownArguments.Count > 0)
        {
            _logger.LogWarning("Ignoring unknown command line arguments: {Arguments}", string.Join(" ", options.UnknownArguments));
        }

        string nativeDirectory = AppContext.BaseDirectory;
        NativeComponentStatus[] nativeComponents =
        [
            FFmpegRuntime.Initialize(nativeDirectory, _loggerFactory.CreateLogger(typeof(FFmpegRuntime).FullName!)),
            SteamAudioRuntime.Check(nativeDirectory, _loggerFactory.CreateLogger(typeof(SteamAudioRuntime).FullName!)),
        ];

        var window = new MainWindow
        {
            DataContext = new MainViewModel(options, nativeComponents, logFilePath),
        };
        MainWindow = window;
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _logger?.LogInformation("Orbivo exiting with code {ExitCode}.", e.ApplicationExitCode);
        ShutDownLogging();
        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.Exception, "Unhandled exception on the UI thread. Shutting down.");
        MessageBox.Show(
            $"Unerwarteter Fehler: {e.Exception.Message}\n\nOrbivo wird beendet. Details stehen in der Log-Datei:\n{_fileLoggerProvider?.FilePath}",
            "Orbivo – Fehler",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        _logger?.LogCritical(e.ExceptionObject as Exception, "Unhandled exception on a background thread. The process terminates.");
        // The process ends without OnExit; flush the log file now.
        ShutDownLogging();
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception.");
        e.SetObserved();
    }

    private void ShutDownLogging()
    {
        // LoggerFactory does not dispose providers passed in through its constructor.
        _loggerFactory?.Dispose();
        _fileLoggerProvider?.Dispose();
    }
}
