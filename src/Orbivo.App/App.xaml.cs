using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Orbivo.App.Imaging;
using Orbivo.App.LiveView;
using Orbivo.App.ViewModels;
using Orbivo.Audio;
using Orbivo.Audio.Output;
using Orbivo.Core.Audio;
using Orbivo.Core.Commands;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Core.Logging;
using Orbivo.Core.Settings;
using Orbivo.Core.Status;
using Orbivo.Media;
using Orbivo.Xr.DebugViewport;
using Orbivo.Xr.Graphics;
using Orbivo.Xr.LiveView;
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
    private AudioOutput? _audioOutput;
    private LiveViewController? _liveView;
    private LiveViewFrames? _liveViewFrames;
    private StartupOptions? _options;
    private SessionRecorder? _recorder;
    private string _appVersion = "unbekannt";

    /// <summary>Device and hand-over of the render thread for the idle image, once the device exists (UI thread).</summary>
    private (GraphicsDevice Device, Action<StillImageTexture> Show)? _imageTarget;

    /// <summary>Applies the idle view to the render thread (headset or preview), once it exists.</summary>
    private Action<Core.Playback.IdleView>? _setIdleView;

    /// <summary>The idle image that was last loaded or is being loaded, to load each file only once.</summary>
    private string? _loadedIdleImage;

    /// <summary>Operator UI → player (CLAUDE.md rule 3).</summary>
    private readonly Channel<PlayerCommand> _playerCommands =
        Channel.CreateUnbounded<PlayerCommand>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>Head direction in the picture's frame, render thread → audio thread (sound fixed to the picture, M7).</summary>
    private readonly ListenerOrientation _listener = new();

    /// <summary>Recenter (F30): requested by the player, carried out by the render thread. Not saved (docs/DECISIONS.md, M8).</summary>
    private readonly RecenterControl _recenter = new();

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

        _appVersion = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unbekannt";
        _logger.LogInformation(
            "Orbivo {Version} starting. OS: {Os}, runtime: {Runtime}, XR: {XrMode}, video decoder: {Decoder}.",
            _appVersion,
            RuntimeInformation.OSDescription,
            RuntimeInformation.FrameworkDescription,
            options.NoXr ? "disabled (--no-xr)" : "enabled",
            options.Decoder);
        if (options.UnknownArguments.Count > 0)
        {
            _logger.LogWarning("Ignoring unknown command line arguments: {Arguments}", string.Join(" ", options.UnknownArguments));
        }

        PruneOldLogs(logFilePath, _logger);

        string nativeDirectory = AppContext.BaseDirectory;
        NativeComponentStatus[] nativeComponents =
        [
            FFmpegRuntime.Initialize(nativeDirectory, _loggerFactory.CreateLogger(typeof(FFmpegRuntime).FullName!)),
            SteamAudioRuntime.Check(nativeDirectory, _loggerFactory.CreateLogger(typeof(SteamAudioRuntime).FullName!)),
            OpenXrLoader.Check(nativeDirectory, _loggerFactory.CreateLogger(typeof(OpenXrLoader).FullName!)),
        ];

        AppSettings settings = AppSettings.Load(AppSettings.DefaultPath, out string? settingsProblem);
        if (settingsProblem is not null)
        {
            _logger.LogWarning("{Path}: {Problem}", AppSettings.DefaultPath, settingsProblem);
        }

        // The sound output runs from the start (silence until a video plays), so the decoders know the device rate.
        _audioOutput = new AudioOutput(settings.AudioDeviceId, settings.Volume, _loggerFactory.CreateLogger<AudioOutput>(), _listener);
        ILogger deviceLogger = _loggerFactory.CreateLogger(typeof(AudioDeviceCatalog).FullName!);
        _viewModel = new MainViewModel(
            options,
            nativeComponents,
            logFilePath,
            _playerCommands.Writer,
            settings,
            () => AudioDeviceCatalog.ListOutputs(deviceLogger));
        _viewModel.SettingsChanged += (_, _) => SaveSettings();
        if (options.ImagePath is not null)
        {
            // --image: the idle image for this session; the stored idle view stays as it is.
            _viewModel.Idle.UseSessionImage(Path.GetFullPath(options.ImagePath));
        }

        _viewModel.Idle.Changed += (_, _) =>
        {
            ApplyIdleView();
            _recorder?.Events.Record(SessionEventKind.IdleView, DescribeIdleView(_viewModel.Idle));
        };

        // The recorder starts before the render thread and the player, so their first events are in the log.
        _recorder = new SessionRecorder(_loggerFactory.CreateLogger<SessionRecorder>(), _recenter);
        _viewModel.Recording.EnabledChanged += (_, _) => ApplyRecording();
        _viewModel.Recording.RateChanged += (_, _) => _recorder.SetHeadSampleRate(_viewModel.Recording.RateHz);
        _viewModel.StartRecordingPolling(() => _recorder.Status);
        ApplyRecording();
        _liveView = new LiveViewController(_loggerFactory.CreateLogger<LiveViewPresenter>());
        _viewModel.AttachLiveView(_liveView);
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
        SaveSettings();
        _playerCommands.Writer.TryComplete();

        // Render threads first: the player frees the frames they read. Disposing the XR host releases its reference
        // to the device; the player's textures and FFmpeg keep their own references until the player is disposed.
        // The sound output goes after the player, whose decoders write into its ring buffer.
        // The live view presenter stopped with its window; its slot textures go once no render thread draws into them.
        _liveView?.Dispose();
        _xrHost?.Dispose();
        _debugViewport?.Dispose();
        _liveViewFrames?.Dispose();
        _player?.Dispose();
        _recorder?.Dispose();
        _audioOutput?.Dispose();
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
        _xrHost.SetListener(_listener);
        _xrHost.SetRecenter(_recenter);
        _xrHost.SetSessionLog(_recorder?.Samples, _recorder?.Events);
        _setIdleView = _xrHost.SetIdleView;
        ApplyIdleView();
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
        AttachLiveView(device, host.SetLiveView, logger);
        StartPlayer(device, ffmpegAvailable, host.SetVideoSource, viewModel, loggerFactory, logger);
        _imageTarget = (device, host.ShowImage);
        ApplyIdleView();
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

        _player = new VideoPlayer(device.Device, _playerCommands.Reader, new VideoPlayerOptions(_options?.StereoLayout, Decoder: _options?.Decoder ?? DecoderPreference.Auto) { Recenter = _recenter, Events = _recorder?.Events }, loggerFactory, _audioOutput);
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
        _debugViewport.SetListener(_listener);
        _debugViewport.SetRecenter(_recenter);
        _debugViewport.SetSessionLog(_recorder?.Samples);
        _setIdleView = _debugViewport.SetIdleView;
        _imageTarget = (_graphicsDevice, _debugViewport.ShowImage);
        ApplyIdleView();
        AttachLiveView(_graphicsDevice, _debugViewport.SetLiveView, logger);
        StartPlayer(_graphicsDevice, ffmpegAvailable, _debugViewport.SetVideoSource, viewModel, loggerFactory, logger);
        _debugViewport.Start();
        viewModel.StartViewportPolling(_debugViewport.GetStatus);
    }

    /// <summary>Starts a new recording or ends the running one, as the operator switched it (F52). UI thread.</summary>
    private void ApplyRecording()
    {
        if (_viewModel is null || _recorder is null)
        {
            return;
        }

        RecordingViewModel recording = _viewModel.Recording;
        if (recording.Enabled)
        {
            _recorder.Start(recording.CreateOptions(_appVersion) with { IdleView = DescribeIdleView(_viewModel.Idle) });
        }
        else
        {
            _recorder.Stop();
        }
    }

    private static string DescribeIdleView(IdleViewModel idle) => idle.View switch
    {
        Core.Playback.IdleView.SeeThrough => "See-through",
        Core.Playback.IdleView.Image => "Standbild: " + (idle.ImagePath ?? "kein Bild gewählt"),
        _ => "Schwarz",
    };

    /// <summary>
    /// Hands the operator's idle view (F7) to the render thread and loads a newly chosen idle image once the device
    /// exists. UI thread.
    /// </summary>
    private void ApplyIdleView()
    {
        if (_viewModel is null || _logger is null)
        {
            return;
        }

        IdleViewModel idle = _viewModel.Idle;
        _setIdleView?.Invoke(idle.View);
        if (idle.ImagePath is { } path && _imageTarget is { } target
            && !string.Equals(path, _loadedIdleImage, StringComparison.OrdinalIgnoreCase))
        {
            _loadedIdleImage = path;
            _ = LoadImageAsync(path, _options?.ImagePath is not null ? _options.StereoLayout : null, target.Device, target.Show, _viewModel, _logger);
        }
    }

    /// <summary>
    /// Creates the live view textures (F42) on the render thread's device and hands them to the render thread and the
    /// live view controller. Without them the app runs on; the live view then says it is unavailable.
    /// </summary>
    private void AttachLiveView(GraphicsDevice device, Action<LiveViewFrames> attach, ILogger logger)
    {
        try
        {
            _liveViewFrames = new LiveViewFrames(device);
        }
        catch (Exception exception) when (exception is SharpGen.Runtime.SharpGenException or OutOfMemoryException)
        {
            logger.LogError(exception, "Creating the live view textures failed; the live view is not available.");
            return;
        }

        attach(_liveViewFrames);
        _liveView?.Attach(device, _liveViewFrames);
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
            (int width, int height, StereoLayout layout) = await Task.Run(() =>
            {
                Bgra32Image image = StillImageLoader.LoadBgra32(fullPath);
                StereoLayout layout = layoutOverride ?? StereoLayoutDetection.FromSize(image.Width, image.Height);
                show(StillImageTexture.Create(device, image, layout));
                return (image.Width, image.Height, layout);
            });

            logger.LogInformation(
                "Image {Path} ({Width}x{Height}) loaded in {Milliseconds} ms.",
                fullPath,
                width,
                height,
                stopwatch.ElapsedMilliseconds);
            logger.LogInformation("Idle image layout: {Layout} ({Source}).", layout, layoutOverride is null ? "guessed from the size" : "--stereo");
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

    /// <summary>Deletes session logs older than 30 days, keeping at least the last 30 sessions (decision 2026-11-08).</summary>
    private static void PruneOldLogs(string currentLogPath, ILogger logger)
    {
        LogRetentionPolicy policy = LogRetentionPolicy.Default;
        LogPruneResult result;
        try
        {
            result = LogRetention.Prune(LogPaths.DefaultDirectory, policy, DateTime.UtcNow, currentLogPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Cleaning up old session logs in {Directory} failed.", LogPaths.DefaultDirectory);
            return;
        }

        if (result.DeletedFiles > 0)
        {
            logger.LogInformation(
                "Deleted {Count} session logs older than {Days} days ({Kilobytes} KB); the last {MinFiles} sessions are always kept.",
                result.DeletedFiles,
                policy.MaxAge.TotalDays,
                result.DeletedBytes / 1024,
                policy.MinFiles);
        }

        foreach (string failure in result.Failures)
        {
            logger.LogWarning("Old session log could not be deleted: {Failure}", failure);
        }
    }

    /// <summary>Keeps output device and volume for the next start (decision M5).</summary>
    private void SaveSettings()
    {
        if (_viewModel is null)
        {
            return;
        }

        try
        {
            _viewModel.CurrentSettings.Save(AppSettings.DefaultPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(exception, "Saving the settings to {Path} failed.", AppSettings.DefaultPath);
        }
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
