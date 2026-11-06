using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Orbivo.App.Imaging;
using Orbivo.App.ViewModels;
using Orbivo.Audio;
using Orbivo.Core.Commands;
using Orbivo.Core.Formats;
using Orbivo.Core.Logging;
using Orbivo.Core.Status;
using Orbivo.Media;
using Orbivo.Xr.DebugViewport;
using Orbivo.Xr.Graphics;
using Orbivo.Xr.OpenXr;
using Orbivo.Xr.Rendering;

namespace Orbivo.App;

/// <summary>
/// Composition root: sets up logging, checks native components and shows the main window.
/// Then it starts the headset output (OpenXR), or with <c>--no-xr</c> the desktop preview window that stands in for it,
/// and the video player on the same Direct3D 11 device.
/// </summary>
public partial class App : Application
{
    private FileLoggerProvider? _fileLoggerProvider;
    private ILoggerFactory? _loggerFactory;
    private ILogger? _logger;
    private MainViewModel? _viewModel;
    private GraphicsDevice? _graphicsDevice;
    private DebugViewportHost? _debugViewport;
    private XrHost? _xrHost;
    private VideoPlayer? _player;
    private StartupOptions? _options;

    /// <summary>Operator UI → player (CLAUDE.md rule 3).</summary>
    private readonly Channel<PlayerCommand> _playerCommands =
        Channel.CreateUnbounded<PlayerCommand>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

#if DEBUG
    private const bool RequestDebugLayer = true;
#else
    private const bool RequestDebugLayer = false;
#endif

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        StartupOptions options = StartupOptions.Parse(e.Args);
        _options = options;

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
            OpenXrLoader.Check(nativeDirectory, _loggerFactory.CreateLogger(typeof(OpenXrLoader).FullName!)),
        ];

        _viewModel = new MainViewModel(options, nativeComponents, logFilePath, _playerCommands.Writer);
        var window = new MainWindow
        {
            DataContext = _viewModel,
        };
        MainWindow = window;
        window.Show();

        if (options.VideoPath is not null)
        {
            // First playlist entry. The open command waits in the channel until the player runs; the headset stays
            // black until the operator presses Play (F6).
            _viewModel.AddAndLoad(options.VideoPath);
        }

        bool ffmpegAvailable = nativeComponents[0].IsAvailable;
        if (options.NoXr)
        {
            StartDebugViewport(options, ffmpegAvailable, _viewModel, _loggerFactory, _logger);
        }
        else
        {
            StartHeadset(options, ffmpegAvailable, _viewModel, _loggerFactory, _logger);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _viewModel?.StopStatusPolling();
        _playerCommands.Writer.TryComplete();

        // Render threads first: the player frees the frames they read. Disposing the XR host releases its reference
        // to the device; the player's textures and FFmpeg keep their own references until the player is disposed.
        _xrHost?.Dispose();
        _debugViewport?.Dispose();
        _player?.Dispose();
        _graphicsDevice?.Dispose();
        _logger?.LogInformation("Orbivo exiting with code {ExitCode}.", e.ApplicationExitCode);
        ShutDownLogging();
        base.OnExit(e);
    }

    /// <summary>
    /// Starts the XR render thread. It connects to the runtime on its own and keeps retrying;
    /// the image is loaded once the device on the GPU of the headset exists.
    /// </summary>
    private void StartHeadset(StartupOptions options, bool ffmpegAvailable, MainViewModel viewModel, ILoggerFactory loggerFactory, ILogger logger)
    {
        _xrHost = new XrHost(loggerFactory, RequestDebugLayer);
        _xrHost.SetImageLayout(options.StereoLayout ?? StereoLayout.Mono);
        _xrHost.Start();
        viewModel.StartXrPolling(_xrHost.GetStatus);
        _ = StartWhenDeviceReadyAsync(options, ffmpegAvailable, _xrHost, viewModel, loggerFactory, logger);
    }

    /// <summary>The device exists once the headset is found; image and player need it.</summary>
    private async Task StartWhenDeviceReadyAsync(
        StartupOptions options,
        bool ffmpegAvailable,
        XrHost host,
        MainViewModel viewModel,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        GraphicsDevice device = await host.GraphicsDeviceReady;
        StartPlayer(device, ffmpegAvailable, host.SetVideoSource, viewModel, loggerFactory, logger);
        if (options.ImagePath is not null)
        {
            await LoadImageAsync(options.ImagePath, options.StereoLayout, device, host.ShowImage, viewModel, logger);
        }
    }

    /// <summary>
    /// Starts the player on the renderer's device and hands it to the render thread as video source.
    /// Without FFmpeg the player does not start; the status says why.
    /// </summary>
    private void StartPlayer(
        GraphicsDevice device,
        bool ffmpegAvailable,
        Action<IVideoFrameSource> attach,
        MainViewModel viewModel,
        ILoggerFactory loggerFactory,
        ILogger logger)
    {
        if (!ffmpegAvailable)
        {
            logger.LogError("The video player is not started because FFmpeg is missing or unusable.");
            viewModel.ShowPlayerUnavailable("Kein Video möglich: FFmpeg fehlt (siehe Statuszeile FFmpeg)");
            return;
        }

        _player = new VideoPlayer(device.Device, _playerCommands.Reader, new VideoPlayerOptions(_options?.StereoLayout), loggerFactory);
        attach(_player);
        _player.Start();
        viewModel.StartPlayerPolling(_player.GetStatus);
    }

    private void StartDebugViewport(StartupOptions options, bool ffmpegAvailable, MainViewModel viewModel, ILoggerFactory loggerFactory, ILogger logger)
    {
        try
        {
            _graphicsDevice = GraphicsDevice.CreateOnHighPerformanceGpu(loggerFactory.CreateLogger<GraphicsDevice>(), RequestDebugLayer);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Creating the Direct3D 11 device failed.");
            viewModel.ShowGraphicsDeviceFailed();
            return;
        }

        viewModel.ShowGraphicsDevice(_graphicsDevice.AdapterName, _graphicsDevice.DedicatedVideoMemoryBytes);
        _debugViewport = new DebugViewportHost(_graphicsDevice, loggerFactory.CreateLogger<DebugViewportHost>());
        _debugViewport.SetImageLayout(options.StereoLayout ?? StereoLayout.Mono);
        StartPlayer(_graphicsDevice, ffmpegAvailable, _debugViewport.SetVideoSource, viewModel, loggerFactory, logger);
        _debugViewport.Start();
        viewModel.StartViewportPolling(_debugViewport.GetStatus);

        if (options.ImagePath is not null)
        {
            _ = LoadImageAsync(options.ImagePath, options.StereoLayout, _graphicsDevice, _debugViewport.ShowImage, viewModel, logger);
        }
    }

    /// <summary>
    /// Decodes the image and creates the texture on a worker thread, then hands it to the render thread
    /// (headset or preview window) through <paramref name="show"/>.
    /// </summary>
    private static async Task LoadImageAsync(
        string path,
        StereoLayout? layoutOverride,
        GraphicsDevice device,
        Action<StillImageTexture> show,
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
                show(StillImageTexture.Create(device, image));
                return (image.Width, image.Height);
            });

            logger.LogInformation(
                "Image {Path} ({Width}x{Height}) loaded in {Milliseconds} ms.",
                fullPath,
                width,
                height,
                stopwatch.ElapsedMilliseconds);
            // The hosts were told the layout before; the guess here only labels it for the operator when none was given.
            StereoLayout layout = layoutOverride ?? StereoLayout.Mono;
            if (layoutOverride is null && StereoLayoutDetection.FromSize(width, height) != StereoLayout.Mono)
            {
                logger.LogWarning(
                    "Image {Path} is square and probably top-bottom stereo; it is shown as mono. Start with --stereo tb.",
                    fullPath);
            }

            viewModel.ShowImageLoaded(fileName, width, height, layout, layoutOverride is not null);
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
