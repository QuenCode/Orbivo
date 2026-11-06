using System.Globalization;
using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbivo.Core.Commands;
using Orbivo.Core.Formats;
using Orbivo.Core.Playback;
using Orbivo.Core.Status;
using Orbivo.Xr.DebugViewport;
using Orbivo.Xr.OpenXr;

namespace Orbivo.App.ViewModels;

/// <summary>
/// View model of the operator main window. Play, Pause and Stop send commands to the player (CLAUDE.md rule 3);
/// playlist, seek and format controls are still placeholders (M4, M6).
/// The status panel shows the headset connection (F40, F41), or in <c>--no-xr</c> mode the preview window, and the player.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ChannelWriter<PlayerCommand> _commands;
    private readonly List<Action> _refreshers = [];
    private readonly StatusEntry _gpuEntry;
    private readonly StatusEntry? _imageEntry;
    private readonly StatusEntry _playbackEntry;
    private readonly StatusEntry _videoEntry;
    private readonly StatusEntry _decoderEntry;
    private readonly StatusEntry _viewEntry;
    private readonly StatusEntry? _viewportEntry;
    private readonly StatusEntry? _headsetEntry;
    private readonly StatusEntry? _sessionEntry;
    private readonly StatusEntry? _xrRenderEntry;
    private DispatcherTimer? _statusTimer;
    private PlaybackState _playbackState = PlaybackState.NoMedia;
    private string _timeText = FormatTime(TimeSpan.Zero) + " / " + FormatTime(TimeSpan.Zero);
    private double _progress;
    private string _stereoLayoutText = "–";
    private string _projectionText = "–";

    public MainViewModel(
        StartupOptions options,
        IEnumerable<NativeComponentStatus> nativeComponents,
        string logFilePath,
        ChannelWriter<PlayerCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(nativeComponents);
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands;
        PlayCommand = new RelayCommand(
            () => Send(new Core.Commands.PlayCommand()),
            () => _playbackState is PlaybackState.Ready or PlaybackState.Paused or PlaybackState.Ended or PlaybackState.Opening);
        PauseCommand = new RelayCommand(
            () => Send(new Core.Commands.PauseCommand()),
            () => _playbackState == PlaybackState.Playing);
        StopCommand = new RelayCommand(
            () => Send(new Core.Commands.StopCommand()),
            () => _playbackState is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ended);

        Title = options.NoXr ? "Orbivo – Operator (ohne Brille)" : "Orbivo – Operator";

        var entries = new List<StatusEntry>
        {
            new("Modus", options.NoXr ? "Desktop-Vorschau (--no-xr)" : "Brille (OpenXR)", StatusLevel.Neutral),
        };

        _gpuEntry = new StatusEntry("GPU", options.NoXr ? "Wird eingerichtet …" : "Wartet auf die Brille …", StatusLevel.Neutral);
        _imageEntry = options.ImagePath is null ? null : new StatusEntry("Bild", "Wird geladen …", StatusLevel.Neutral);
        _playbackEntry = options.VideoPath is null
            ? new StatusEntry(
                "Wiedergabe",
                $"Kein Video (Schwarz). Start mit {StartupOptions.VideoOption} <Pfad>",
                options.ImagePath is null ? StatusLevel.Warning : StatusLevel.Neutral)
            : new StatusEntry("Wiedergabe", "Wartet auf die Grafikkarte …", StatusLevel.Neutral);
        _videoEntry = new StatusEntry("Video", "–", StatusLevel.Neutral);
        _decoderEntry = new StatusEntry("Decoder", "–", StatusLevel.Neutral);

        if (options.NoXr)
        {
            _viewportEntry = new StatusEntry("Vorschaufenster", "Startet …", StatusLevel.Neutral);
            _viewEntry = new StatusEntry("Blick (Maus)", "–", StatusLevel.Neutral);
            entries.AddRange(
            [
                new StatusEntry("Brille", "Nicht verwendet (--no-xr)", StatusLevel.Neutral),
                _gpuEntry,
                _viewportEntry,
                _playbackEntry,
                _videoEntry,
                _decoderEntry,
                _viewEntry,
            ]);
        }
        else
        {
            _headsetEntry = new StatusEntry("Brille", "Startet …", StatusLevel.Neutral);
            _sessionEntry = new StatusEntry("Session", "–", StatusLevel.Neutral);
            _xrRenderEntry = new StatusEntry("Bildausgabe", "–", StatusLevel.Neutral);
            _viewEntry = new StatusEntry("Blick (Proband)", "–", StatusLevel.Neutral);
            entries.AddRange([_headsetEntry, _sessionEntry, _xrRenderEntry, _gpuEntry, _playbackEntry, _videoEntry, _decoderEntry, _viewEntry]);
        }

        if (_imageEntry is not null)
        {
            entries.Insert(entries.IndexOf(_playbackEntry), _imageEntry);
        }

        entries.AddRange(nativeComponents.Select(component => new StatusEntry(
            component.Component,
            component.Detail,
            component.IsAvailable ? StatusLevel.Ok : StatusLevel.Error)));
        entries.Add(new StatusEntry("Log-Datei", logFilePath, StatusLevel.Neutral));

        StatusEntries = entries;
    }

    public string Title { get; }

    public IReadOnlyList<StatusEntry> StatusEntries { get; }

    public IRelayCommand PlayCommand { get; }

    public IRelayCommand PauseCommand { get; }

    public IRelayCommand StopCommand { get; }

    /// <summary>"position / duration" for the transport bar.</summary>
    public string TimeText
    {
        get => _timeText;
        private set => SetProperty(ref _timeText, value);
    }

    /// <summary>Position as a fraction of the duration (0–1), shown on the timeline. Seek follows in M4.</summary>
    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public string ProjectionText
    {
        get => _projectionText;
        private set => SetProperty(ref _projectionText, value);
    }

    public string StereoLayoutText
    {
        get => _stereoLayoutText;
        private set => SetProperty(ref _stereoLayoutText, value);
    }

    public void ShowGraphicsDevice(string adapterName, ulong dedicatedVideoMemoryBytes)
    {
        double gigabytes = dedicatedVideoMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        _gpuEntry.Update(string.Create(CultureInfo.CurrentCulture, $"{adapterName} ({gigabytes:0} GB)"), StatusLevel.Ok);
    }

    public void ShowGraphicsDeviceFailed()
    {
        _gpuEntry.Update("Grafikkarte konnte nicht eingerichtet werden (Details im Log)", StatusLevel.Error);
        _viewportEntry?.Update("Nicht gestartet", StatusLevel.Error);
        _imageEntry?.Update("–", StatusLevel.Neutral);
        _playbackEntry.Update("Kein Video möglich ohne Grafikkarte", StatusLevel.Error);
    }

    public void ShowImageLoaded(string fileName, int width, int height, StereoLayout layout, bool layoutChosenExplicitly)
    {
        string text = string.Create(
            CultureInfo.CurrentCulture,
            $"{fileName} ({width} × {height}), {DescribeLayout(layout)}{(layoutChosenExplicitly ? string.Empty : " (aus der Größe geschätzt)")}");
        int eyeWidth = layout == StereoLayout.SideBySide ? width / 2 : width;
        int eyeHeight = layout == StereoLayout.TopBottom ? height / 2 : height;
        bool expected = layout == StereoLayout.SideBySide ? eyeWidth == eyeHeight : eyeWidth == 2 * eyeHeight;
        _imageEntry?.Update(
            expected ? text : text + ". Ungewöhnliches Seitenverhältnis für Equirect 360°",
            expected ? StatusLevel.Ok : StatusLevel.Warning);
    }

    public void ShowImageFailed(string fileName, string reason)
    {
        _imageEntry?.Update($"{fileName}: {reason}", StatusLevel.Error);
    }

    /// <summary>Refreshes playback, video and decoder ten times per second.</summary>
    public void StartPlayerPolling(Func<PlayerStatus> getStatus)
    {
        ArgumentNullException.ThrowIfNull(getStatus);
        AddRefresher(() => ShowPlayerStatus(getStatus()));
    }

    public void ShowPlayerUnavailable(string reason)
    {
        _playbackEntry.Update(reason, StatusLevel.Error);
    }

    /// <summary>Refreshes the preview window and view direction four times per second.</summary>
    public void StartViewportPolling(Func<DebugViewportStatus> getStatus)
    {
        ArgumentNullException.ThrowIfNull(getStatus);
        AddRefresher(() => ShowViewportStatus(getStatus()));
    }

    /// <summary>Refreshes headset, session, frame rate and view direction four times per second.</summary>
    public void StartXrPolling(Func<XrStatus> getStatus)
    {
        ArgumentNullException.ThrowIfNull(getStatus);
        AddRefresher(() => ShowXrStatus(getStatus()));
    }

    public void StopStatusPolling()
    {
        _statusTimer?.Stop();
        _statusTimer = null;
    }

    private void AddRefresher(Action refresh)
    {
        _refreshers.Add(refresh);
        if (_statusTimer is null)
        {
            _statusTimer = new DispatcherTimer { Interval = StatusPollInterval };
            _statusTimer.Tick += (_, _) =>
            {
                foreach (Action refresher in _refreshers)
                {
                    refresher();
                }
            };
            _statusTimer.Start();
        }

        refresh();
    }

    private void Send(PlayerCommand command)
    {
        if (!_commands.TryWrite(command))
        {
            _playbackEntry.Update("Befehl konnte nicht an den Player übergeben werden", StatusLevel.Error);
        }
    }

    private void ShowPlayerStatus(PlayerStatus status)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        if (status.State != _playbackState)
        {
            _playbackState = status.State;
            PlayCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            StopCommand.NotifyCanExecuteChanged();
        }

        TimeText = FormatTime(status.Position) + " / " + FormatTime(status.Duration);
        Progress = status.Duration > TimeSpan.Zero ? Math.Clamp(status.Position / status.Duration, 0.0, 1.0) : 0.0;

        string file = status.FilePath is null ? string.Empty : Path.GetFileName(status.FilePath);
        (string stateText, StatusLevel stateLevel) = status.State switch
        {
            PlaybackState.NoMedia => ("Kein Video", StatusLevel.Neutral),
            PlaybackState.Opening => ($"{file}: wird geöffnet …", StatusLevel.Neutral),
            PlaybackState.Ready => ($"{file}: bereit, Brille schwarz bis Play", StatusLevel.Ok),
            PlaybackState.Playing => ($"{file}: läuft", StatusLevel.Ok),
            PlaybackState.Paused => ($"{file}: Pause", StatusLevel.Ok),
            PlaybackState.Ended => ($"{file}: Ende erreicht, letztes Bild steht", StatusLevel.Ok),
            _ => ($"{file}: {status.Error ?? "Fehler"}", StatusLevel.Error),
        };
        _playbackEntry.Update(stateText, stateLevel);

        VideoStreamInfo? video = status.Video;
        if (video is null)
        {
            _videoEntry.Update("–", StatusLevel.Neutral);
            _decoderEntry.Update("–", StatusLevel.Neutral);
            ProjectionText = "–";
            StereoLayoutText = "–";
            return;
        }

        ProjectionText = "Equirect 360° (bis M6 fest)";
        StereoLayoutText = DescribeLayout(video.Layout) + (video.LayoutChosenExplicitly ? " (Kommandozeile)" : " (aus der Größe geschätzt)");
        _videoEntry.Update(
            string.Create(
                culture,
                $"{video.Codec} {video.Profile}, {video.Width} × {video.Height}, {video.FrameRate:0.##} fps, {video.Color.BitDepth} Bit, {DescribeColor(video.Color)}, {DescribeLayout(video.Layout)}"),
            StatusLevel.Ok);

        string decoderText = string.Create(
            culture,
            $"{(video.HardwareDecoding ? "Hardware (D3D11VA)" : "Software")}, {status.DecodedFramesPerSecond:0} fps dekodiert, Puffer {status.QueuedFrames}/{status.QueueCapacity}, verworfen {status.DroppedFrames}");

        // While playing, the buffer stays nearly full if the decoder keeps up. Running empty means stutter.
        bool starving = status.State == PlaybackState.Playing && status.QueuedFrames <= 1 && status.Position < status.Duration - TimeSpan.FromSeconds(1);
        _decoderEntry.Update(
            starving ? decoderText + ". Puffer läuft leer, Decoder zu langsam" : decoderText,
            starving || status.DroppedFrames > 0 || !video.HardwareDecoding ? StatusLevel.Warning : StatusLevel.Ok);
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.ToString(@"hh\:mm\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private static string DescribeLayout(StereoLayout layout) => layout switch
    {
        StereoLayout.TopBottom => "Stereo Top-Bottom",
        StereoLayout.SideBySide => "Stereo Side-by-Side",
        _ => "Mono",
    };

    private static string DescribeColor(VideoColorFormat color)
    {
        string matrix = color.Matrix switch
        {
            YuvMatrix.Bt601 => "BT.601",
            YuvMatrix.Bt2020 => "BT.2020",
            _ => "BT.709",
        };
        return matrix + (color.Range == YuvRange.Full ? " Full Range" : " Limited Range");
    }

    private void ShowXrStatus(XrStatus status)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        XrRuntimeInfo? runtime = status.Runtime;
        string runtimeText = runtime is null ? "OpenXR-Runtime" : $"{runtime.RuntimeName} {runtime.RuntimeVersion}";

        (string headsetText, StatusLevel headsetLevel) = status.State switch
        {
            XrConnectionState.Starting => ("Startet …", StatusLevel.Neutral),
            XrConnectionState.LoaderMissing => ("OpenXR-Loader fehlt (siehe native/README.md)", StatusLevel.Error),
            XrConnectionState.RuntimeUnavailable => ("Kein OpenXR-Runtime erreichbar. Läuft Varjo Base? Neuer Versuch alle 2 s", StatusLevel.Error),
            XrConnectionState.HeadsetUnavailable => ($"{runtimeText}: keine Brille gefunden. Brille anschließen und einschalten", StatusLevel.Warning),
            XrConnectionState.Connecting => ($"{runtimeText}: verbinde …", StatusLevel.Neutral),
            XrConnectionState.Connected when runtime is not null => (
                string.Create(culture, $"{runtime.SystemName} über {runtimeText}, {runtime.EyeWidth} × {runtime.EyeHeight} je Auge"),
                StatusLevel.Ok),
            XrConnectionState.Connected => ("Verbunden", StatusLevel.Ok),
            XrConnectionState.Failed => ($"Fehler: {status.Error} (Details im Log). Neuer Versuch läuft", StatusLevel.Error),
            _ => ("Beendet", StatusLevel.Neutral),
        };
        _headsetEntry?.Update(headsetText, headsetLevel);

        (string sessionText, StatusLevel sessionLevel) = status.SessionState switch
        {
            XrSessionState.Idle => ("Wartet (IDLE)", StatusLevel.Neutral),
            XrSessionState.Ready => ("Startet (READY)", StatusLevel.Neutral),
            XrSessionState.Synchronized => ("Läuft, Brille zeigt das Bild noch nicht (SYNCHRONIZED)", StatusLevel.Warning),
            XrSessionState.Visible => ("Sichtbar (VISIBLE)", StatusLevel.Ok),
            XrSessionState.Focused => ("Aktiv (FOCUSED)", StatusLevel.Ok),
            XrSessionState.Stopping => ("Wird angehalten (STOPPING)", StatusLevel.Warning),
            XrSessionState.LossPending => ("Verbindung geht verloren (LOSS_PENDING)", StatusLevel.Error),
            XrSessionState.Exiting => ("Wird beendet (EXITING)", StatusLevel.Warning),
            _ => ("–", StatusLevel.Neutral),
        };
        _sessionEntry?.Update(sessionText, sessionLevel);

        bool rendering = status.State == XrConnectionState.Connected && status.FramesPerSecond > 0f;
        if (!rendering)
        {
            _xrRenderEntry?.Update("–", StatusLevel.Neutral);
        }
        else
        {
            string text = string.Create(culture, $"{status.FramesPerSecond:0} fps, Brille {status.DisplayRefreshHz:0} Hz");
            if (status.RenderLoopAllocates)
            {
                _xrRenderEntry?.Update(text + ". Render-Schleife allokiert (siehe Log)", StatusLevel.Warning);
            }
            else if (status.DisplayRefreshHz > 0f && status.FramesPerSecond < status.DisplayRefreshHz * 0.95f)
            {
                _xrRenderEntry?.Update(text + ". Unter der Bildrate der Brille", StatusLevel.Warning);
            }
            else
            {
                _xrRenderEntry?.Update(text, StatusLevel.Ok);
            }
        }

        if (runtime is not null && runtime.AdapterName.Length > 0)
        {
            _gpuEntry.Update(runtime.AdapterName + " (von OpenXR vorgegeben)", StatusLevel.Ok);
        }

        if (!rendering)
        {
            _viewEntry.Update("–", StatusLevel.Neutral);
        }
        else if (!status.HeadTracked)
        {
            _viewEntry.Update("Kein Tracking", StatusLevel.Warning);
        }
        else
        {
            _viewEntry.Update(
                string.Create(
                    culture,
                    $"{DescribeDirection(status.HeadYawDegrees, status.HeadPitchDegrees)}: Yaw {status.HeadYawDegrees:+0;-0;0}°, Pitch {status.HeadPitchDegrees:+0;-0;0}°"),
                StatusLevel.Ok);
        }
    }

    private void ShowViewportStatus(DebugViewportStatus status)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        switch (status.State)
        {
            case DebugViewportState.Starting:
                _viewportEntry?.Update("Startet …", StatusLevel.Neutral);
                break;
            case DebugViewportState.Running when status.RenderLoopAllocates:
                _viewportEntry?.Update(
                    string.Create(culture, $"Läuft, {status.FramesPerSecond:0} fps. Render-Schleife allokiert (siehe Log)"),
                    StatusLevel.Warning);
                break;
            case DebugViewportState.Running:
                _viewportEntry?.Update(string.Create(culture, $"Läuft, {status.FramesPerSecond:0} fps"), StatusLevel.Ok);
                break;
            case DebugViewportState.Failed:
                _viewportEntry?.Update($"Fehler: {status.Error} (Details im Log)", StatusLevel.Error);
                break;
            default:
                _viewportEntry?.Update("Beendet", StatusLevel.Neutral);
                break;
        }

        if (status.State == DebugViewportState.Running)
        {
            _viewEntry.Update(
                string.Create(
                    culture,
                    $"{DescribeDirection(status.YawDegrees, status.PitchDegrees)}: Yaw {status.YawDegrees:+0;-0;0}°, Pitch {status.PitchDegrees:+0;-0;0}°, Sichtfeld {status.VerticalFovDegrees:0}°"),
                StatusLevel.Neutral);
        }
    }

    /// <summary>Rough direction in words. Yaw positive = left (docs/DECISIONS.md).</summary>
    private static string DescribeDirection(float yawDegrees, float pitchDegrees)
    {
        if (pitchDegrees > 45f)
        {
            return "oben";
        }

        if (pitchDegrees < -45f)
        {
            return "unten";
        }

        return yawDegrees switch
        {
            >= -45f and <= 45f => "vorne",
            > 45f and <= 135f => "links",
            < -45f and >= -135f => "rechts",
            _ => "hinten",
        };
    }
}
