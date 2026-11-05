using System.Globalization;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Orbivo.Core.Status;
using Orbivo.Xr.DebugViewport;

namespace Orbivo.App.ViewModels;

/// <summary>
/// View model of the operator main window. Playlist, transport and format controls are still placeholders;
/// in <c>--no-xr</c> mode the status panel shows GPU, preview window, image and view direction.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private static readonly TimeSpan ViewportPollInterval = TimeSpan.FromMilliseconds(250);

    private readonly StatusEntry? _gpuEntry;
    private readonly StatusEntry? _viewportEntry;
    private readonly StatusEntry? _imageEntry;
    private readonly StatusEntry? _viewEntry;
    private DispatcherTimer? _viewportTimer;

    public MainViewModel(StartupOptions options, IEnumerable<NativeComponentStatus> nativeComponents, string logFilePath)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(nativeComponents);

        Title = options.NoXr ? "Orbivo – Operator (ohne Brille)" : "Orbivo – Operator";

        var entries = new List<StatusEntry>
        {
            new("Modus", options.NoXr ? "Desktop-Vorschau (--no-xr)" : "Brille (OpenXR)", StatusLevel.Neutral),
            new("Brille", "Noch nicht verfügbar (ab M2)", StatusLevel.Neutral),
        };

        if (options.NoXr)
        {
            _gpuEntry = new StatusEntry("GPU", "Wird eingerichtet …", StatusLevel.Neutral);
            _viewportEntry = new StatusEntry("Vorschaufenster", "Startet …", StatusLevel.Neutral);
            _imageEntry = options.ImagePath is null
                ? new StatusEntry("Bild", $"Kein Bild. Start mit {StartupOptions.ImageOption} <Pfad>", StatusLevel.Warning)
                : new StatusEntry("Bild", "Wird geladen …", StatusLevel.Neutral);
            _viewEntry = new StatusEntry("Blick (Maus)", "–", StatusLevel.Neutral);
            entries.AddRange([_gpuEntry, _viewportEntry, _imageEntry, _viewEntry]);
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
        _gpuEntry?.Update(string.Create(CultureInfo.CurrentCulture, $"{adapterName} ({gigabytes:0} GB)"), StatusLevel.Ok);
    }

    public void ShowGraphicsDeviceFailed()
    {
        _gpuEntry?.Update("Grafikkarte konnte nicht eingerichtet werden (Details im Log)", StatusLevel.Error);
        _viewportEntry?.Update("Nicht gestartet", StatusLevel.Error);
        _imageEntry?.Update("–", StatusLevel.Neutral);
    }

    public void ShowImageLoaded(string fileName, int width, int height)
    {
        string text = string.Create(CultureInfo.CurrentCulture, $"{fileName} ({width} × {height})");
        if (width == 2 * height)
        {
            _imageEntry?.Update(text, StatusLevel.Ok);
        }
        else
        {
            _imageEntry?.Update(text + ". Nicht 2:1, wird trotzdem als Equirect 360° Mono gezeigt", StatusLevel.Warning);
        }
    }

    public void ShowImageFailed(string fileName, string reason)
    {
        _imageEntry?.Update($"{fileName}: {reason}", StatusLevel.Error);
    }

    /// <summary>Refreshes the preview window and view direction four times per second.</summary>
    public void StartViewportPolling(Func<DebugViewportStatus> getStatus)
    {
        ArgumentNullException.ThrowIfNull(getStatus);
        _viewportTimer?.Stop();
        _viewportTimer = new DispatcherTimer { Interval = ViewportPollInterval };
        _viewportTimer.Tick += (_, _) => ShowViewportStatus(getStatus());
        _viewportTimer.Start();
        ShowViewportStatus(getStatus());
    }

    public void StopViewportPolling()
    {
        _viewportTimer?.Stop();
        _viewportTimer = null;
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
            _viewEntry?.Update(
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
