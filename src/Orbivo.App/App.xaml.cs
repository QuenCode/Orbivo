using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Orbivo.App.Imaging;
using Orbivo.App.ViewModels;
using Orbivo.Audio;
using Orbivo.Core.Logging;
using Orbivo.Core.Status;
using Orbivo.Media;
using Orbivo.Xr.DebugViewport;
using Orbivo.Xr.Graphics;
using Orbivo.Xr.Rendering;

namespace Orbivo.App;

/// <summary>
/// Composition root: sets up logging, checks native components and shows the main window.
/// With <c>--no-xr</c> it also starts the desktop preview window that stands in for the headset.
/// </summary>
public partial class App : Application
{
    private FileLoggerProvider? _fileLoggerProvider;
    private ILoggerFactory? _loggerFactory;
    private ILogger? _logger;
    private MainViewModel? _viewModel;
    private GraphicsDevice? _graphicsDevice;
    private DebugViewportHost? _debugViewport;

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

        _viewModel = new MainViewModel(options, nativeComponents, logFilePath);
        var window = new MainWindow
        {
            DataContext = _viewModel,
        };
        MainWindow = window;
        window.Show();

        if (options.NoXr)
        {
            StartDebugViewport(options.ImagePath, _viewModel, _loggerFactory, _logger);
        }
        else if (options.ImagePath is not null)
        {
            _logger.LogWarning("{Option} is only used together with {NoXr}.", StartupOptions.ImageOption, StartupOptions.NoXrSwitch);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.StopViewportPolling();
        _debugViewport?.Dispose();
        _graphicsDevice?.Dispose();
        _logger?.LogInformation("Orbivo exiting with code {ExitCode}.", e.ApplicationExitCode);
        ShutDownLogging();
        base.OnExit(e);
    }

    private void StartDebugViewport(string? imagePath, MainViewModel viewModel, ILoggerFactory loggerFactory, ILogger logger)
    {
#if DEBUG
        const bool requestDebugLayer = true;
#else
        const bool requestDebugLayer = false;
#endif
        try
        {
            _graphicsDevice = GraphicsDevice.CreateOnHighPerformanceGpu(loggerFactory.CreateLogger<GraphicsDevice>(), requestDebugLayer);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Creating the Direct3D 11 device failed.");
            viewModel.ShowGraphicsDeviceFailed();
            return;
        }

        viewModel.ShowGraphicsDevice(_graphicsDevice.AdapterName, _graphicsDevice.DedicatedVideoMemoryBytes);
        _debugViewport = new DebugViewportHost(_graphicsDevice, loggerFactory.CreateLogger<DebugViewportHost>());
        _debugViewport.Start();
        viewModel.StartViewportPolling(_debugViewport.GetStatus);

        if (imagePath is not null)
        {
            _ = LoadImageAsync(imagePath, _graphicsDevice, _debugViewport, viewModel, logger);
        }
    }

    /// <summary>Decodes the image and creates the texture on a worker thread, then hands it to the viewport.</summary>
    private static async Task LoadImageAsync(
        string path,
        GraphicsDevice device,
        DebugViewportHost viewport,
        MainViewModel viewModel,
        ILogger logger)
    {
        string fullPath = Path.GetFullPath(path);
        string fileName = Path.GetFileName(fullPath);
        try
        {
            var stopwatch = Stopwatch.StartNew();
            (int width, int height) = await Task.Run(() =>
            {
                Bgra32Image image = StillImageLoader.LoadBgra32(fullPath);
                viewport.ShowImage(StillImageTexture.Create(device, image));
                return (image.Width, image.Height);
            });

            logger.LogInformation(
                "Image {Path} ({Width}x{Height}) loaded in {Milliseconds} ms.",
                fullPath,
                width,
                height,
                stopwatch.ElapsedMilliseconds);
            if (width != 2 * height)
            {
                logger.LogWarning("Image {Path} is not 2:1; M1 shows every image as equirect 360 mono.", fullPath);
            }

            viewModel.ShowImageLoaded(fileName, width, height);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Loading image {Path} failed.", fullPath);
            viewModel.ShowImageFailed(fileName, DescribeImageError(exception));
        }
    }

    private static string DescribeImageError(Exception exception)
    {
        string reason = exception switch
        {
            FileNotFoundException => "Datei nicht gefunden",
            DirectoryNotFoundException => "Ordner nicht gefunden",
            UnauthorizedAccessException => "Kein Zugriff auf die Datei",
            FileFormatException or NotSupportedException => "Kein lesbares Bildformat",
            ArgumentException => $"Bild zu groß (höchstens {StillImageTexture.MaxDimension} Pixel je Seite)",
            _ => "Laden fehlgeschlagen",
        };
        return reason + " (Details im Log)";
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
