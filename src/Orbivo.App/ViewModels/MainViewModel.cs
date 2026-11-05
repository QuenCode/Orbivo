using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Orbivo.Core.Status;
using Orbivo.Xr.DebugViewport;
using Orbivo.Xr.OpenXr;

namespace Orbivo.App.ViewModels;

/// <summary>
/// View model of the operator main window. Playlist, transport and format controls are still placeholders.
/// The status panel shows the headset connection (F40, F41), or in <c>--no-xr</c> mode the preview window.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly StatusEntry _gpuEntry;
    private readonly StatusEntry _imageEntry;
    private readonly StatusEntry _viewEntry;
    private readonly StatusEntry? _viewportEntry;
    private readonly StatusEntry? _headsetEntry;
    private readonly StatusEntry? _sessionEntry;
    private readonly StatusEntry? _xrRenderEntry;
    private DispatcherTimer? _statusTimer;

    public MainViewModel(StartupOptions options, IEnumerable<NativeComponentStatus> nativeComponents, string logFilePath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(nativeComponents);

        Title = options.NoXr ? "Orbivo – Operator (ohne Brille)" : "Orbivo – Operator";

        var entries = new List<StatusEntry>
        {
            new("Modus", options.NoXr ? "Desktop-Vorschau (--no-xr)" : "Brille (OpenXR)", StatusLevel.Neutral),
        };

        _gpuEntry = new StatusEntry("GPU", options.NoXr ? "Wird eingerichtet …" : "Wartet auf die Brille …", StatusLevel.Neutral);
        _imageEntry = options.ImagePath is null
            ? new StatusEntry("Bild", $"Kein Bild (Schwarz). Start mit {StartupOptions.ImageOption} <Pfad>", StatusLevel.Warning)
            : new StatusEntry("Bild", "Wird geladen …", StatusLevel.Neutral);

        if (options.NoXr)
        {
            _viewportEntry = new StatusEntry("Vorschaufenster", "Startet …", StatusLevel.Neutral);
            _viewEntry = new StatusEntry("Blick (Maus)", "–", StatusLevel.Neutral);
            entries.AddRange(
            [
                new StatusEntry("Brille", "Nicht verwendet (--no-xr)", StatusLevel.Neutral),
                _gpuEntry,
                _viewportEntry,
                _imageEntry,
                _viewEntry,
            ]);
        }
        else
        {
            _headsetEntry = new StatusEntry("Brille", "Startet …", StatusLevel.Neutral);
            _sessionEntry = new StatusEntry("Session", "–", StatusLevel.Neutral);
            _xrRenderEntry = new StatusEntry("Bildausgabe", "–", StatusLevel.Neutral);
            _viewEntry = new StatusEntry("Blick (Proband)", "–", StatusLevel.Neutral);
            entries.AddRange([_headsetEntry, _sessionEntry, _xrRenderEntry, _gpuEntry, _imageEntry, _viewEntry]);
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

    public void ShowGraphicsDevice(string adapterName, ulong dedicatedVideoMemoryBytes)
    {
        double gigabytes = dedicatedVideoMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        _gpuEntry.Update(string.Create(CultureInfo.CurrentCulture, $"{adapterName} ({gigabytes:0} GB)"), StatusLevel.Ok);
    }

    public void ShowGraphicsDeviceFailed()
    {
        _gpuEntry.Update("Grafikkarte konnte nicht eingerichtet werden (Details im Log)", StatusLevel.Error);
        _viewportEntry?.Update("Nicht gestartet", StatusLevel.Error);
        _imageEntry.Update("–", StatusLevel.Neutral);
    }

    public void ShowImageLoaded(string fileName, int width, int height)
    {
        string text = string.Create(CultureInfo.CurrentCulture, $"{fileName} ({width} × {height})");
        if (width == 2 * height)
        {
            _imageEntry.Update(text, StatusLevel.Ok);
        }
        else
        {
            _imageEntry.Update(text + ". Nicht 2:1, wird trotzdem als Equirect 360° Mono gezeigt", StatusLevel.Warning);
        }
    }

    public void ShowImageFailed(string fileName, string reason)
    {
        _imageEntry.Update($"{fileName}: {reason}", StatusLevel.Error);
    }

    /// <summary>Refreshes the preview window and view direction four times per second.</summary>
    public void StartViewportPolling(Func<DebugViewportStatus> getStatus)
    {
        ArgumentNullException.ThrowIfNull(getStatus);
        StartStatusPolling(() => ShowViewportStatus(getStatus()));
    }

    /// <summary>Refreshes headset, session, frame rate and view direction four times per second.</summary>
    public void StartXrPolling(Func<XrStatus> getStatus)
    {
        ArgumentNullException.ThrowIfNull(getStatus);
        StartStatusPolling(() => ShowXrStatus(getStatus()));
    }

    public void StopStatusPolling()
    {
        _statusTimer?.Stop();
        _statusTimer = null;
    }

    private void StartStatusPolling(Action refresh)
    {
        _statusTimer?.Stop();
        _statusTimer = new DispatcherTimer { Interval = StatusPollInterval };
        _statusTimer.Tick += (_, _) => refresh();
        _statusTimer.Start();
        refresh();
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
