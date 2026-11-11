using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Orbivo.App.Localization;
using Orbivo.Core.Localization;
using Orbivo.Core.Logging;
using Orbivo.Core.Settings;

namespace Orbivo.App.ViewModels;

/// <summary>One entry of the head log rate combo box.</summary>
public sealed record RecordingRateChoice(int RateHz, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The "Aufzeichnung" group of the operator window (F50–F52): head log and event log on or off, target folder,
/// session label, head log rate and "Nur bei laufendem Video". The app starts and stops the recorder on
/// <see cref="EnabledChanged"/>; on, folder, rate and mode are kept in the settings, the label only for this session.
/// </summary>
public sealed class RecordingViewModel : ObservableObject
{
    private bool _enabled;
    private bool _savedEnabled;
    private bool _perVideo;
    private string _folder;
    private string _label = string.Empty;
    private RecordingRateChoice _selectedRate;
    private string _statusText = UiText.Off;
    private StatusLevel _statusLevel = StatusLevel.Neutral;
    private string? _filePath;

    public RecordingViewModel(bool enabled, string folder, int rateHz, bool perVideo)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Rates =
        [
            new RecordingRateChoice(0, UiText.RateEveryFrame),
            new RecordingRateChoice(45, "45 Hz"),
            new RecordingRateChoice(30, "30 Hz (" + UiText.DefaultTag + ")"),
            new RecordingRateChoice(10, "10 Hz"),
        ];
        _enabled = enabled;
        _savedEnabled = enabled;
        _folder = folder;
        _perVideo = perVideo;
        _selectedRate = Rates.FirstOrDefault(choice => choice.RateHz == rateHz)
            ?? Rates.First(choice => choice.RateHz == AppSettings.DefaultHeadSampleRateHz);
    }

    /// <summary>The operator switched the recording on or off; the app starts or stops the recorder.</summary>
    public event EventHandler? EnabledChanged;

    /// <summary>The operator changed the rate; the app applies it to the running recording.</summary>
    public event EventHandler? RateChanged;

    /// <summary>On, folder or rate changed; the app saves the settings.</summary>
    public event EventHandler? SettingsChanged;

    public IReadOnlyList<RecordingRateChoice> Rates { get; }

    /// <summary>Recording on (each switch-on starts a new pair of files).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value))
            {
                return;
            }

            _savedEnabled = value;
            OnPropertyChanged(nameof(CanEdit));
            EnabledChanged?.Invoke(this, EventArgs.Empty);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Folder, label and mode can be changed only while no recording runs.</summary>
    public bool CanEdit => !_enabled;

    /// <summary>
    /// "Nur bei laufendem Video": while switched on, the recorder waits for Play; every video run (Play until Stop, end,
    /// closing or another video) gets its own pair of files. Pause stays in the same files.
    /// </summary>
    public bool PerVideo
    {
        get => _perVideo;
        set
        {
            if (_enabled || !SetProperty(ref _perVideo, value))
            {
                return;
            }

            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string Folder => _folder;

    /// <summary>Viewer or session, part of the file names (not kept).</summary>
    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value ?? string.Empty);
    }

    public RecordingRateChoice SelectedRate
    {
        get => _selectedRate;
        set
        {
            if (value is null || !SetProperty(ref _selectedRate, value))
            {
                return;
            }

            RateChanged?.Invoke(this, EventArgs.Empty);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public int RateHz => _selectedRate.RateHz;

    /// <summary>Duration and rows of the running recording, or why it stopped.</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public StatusLevel StatusLevel
    {
        get => _statusLevel;
        private set => SetProperty(ref _statusLevel, value);
    }

    /// <summary>Head log of the current or last recording, or null.</summary>
    public string? FilePath
    {
        get => _filePath;
        private set
        {
            if (SetProperty(ref _filePath, value))
            {
                OnPropertyChanged(nameof(FileText));
            }
        }
    }

    public string FileText => _filePath is null ? UiText.RecordingNoFileYet : Path.GetFileName(_filePath) + " (+ _ereignisse.csv)";

    /// <summary>On, folder, rate and mode to keep in the settings.</summary>
    public (bool Enabled, string Folder, int RateHz, bool PerVideo) SavedChoice => (_savedEnabled, _folder, _selectedRate.RateHz, _perVideo);

    /// <summary>The operator picked a folder (only while no recording runs).</summary>
    public void ChooseFolder(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (_enabled || string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _folder = folder;
        OnPropertyChanged(nameof(Folder));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>What the recorder needs for the next start.</summary>
    public RecordingOptions CreateOptions(string appVersion) => new(_folder, _label, _selectedRate.RateHz, appVersion) { PerVideo = _perVideo };

    /// <summary>From the recorder, twice a second. A recording that stopped by itself switches the box off (not saved).</summary>
    public void ShowStatus(RecordingStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        FilePath = status.HeadFilePath;
        if (status.Active)
        {
            string text = Loc.Format(
                _perVideo ? UiText.RecordingVideoRunning : UiText.RecordingRunning,
                FormatDuration(status.Duration),
                status.HeadRows,
                status.EventRows,
                status.FilePairs);
            if (status.DroppedRows > 0)
            {
                StatusText = text + " · " + Loc.Format(UiText.RecordingRowsLost, status.DroppedRows);
                StatusLevel = StatusLevel.Warning;
            }
            else
            {
                StatusText = text;
                StatusLevel = StatusLevel.Ok;
            }

            return;
        }

        if (status.Error is not null)
        {
            StatusText = status.Error;
            StatusLevel = StatusLevel.Error;
            if (_enabled)
            {
                // The recorder already stopped; the setting stays as the operator chose it.
                _enabled = false;
                OnPropertyChanged(nameof(Enabled));
                OnPropertyChanged(nameof(CanEdit));
            }

            return;
        }

        if (_enabled && status.Waiting)
        {
            StatusText = status.FilePairs == 0
                ? UiText.RecordingWaitingFirst
                : Loc.Format(
                    status.FilePairs == 1 ? UiText.RecordingWaitingOne : UiText.RecordingWaitingMany,
                    status.FilePairs,
                    FormatDuration(status.Duration),
                    status.HeadRows);
            StatusLevel = StatusLevel.Ok;
            return;
        }

        if (_enabled)
        {
            StatusText = UiText.Starting;
            StatusLevel = StatusLevel.Neutral;
            return;
        }

        StatusText = status.HeadFilePath is null
            ? UiText.Off
            : Loc.Format(UiText.RecordingOffLast, FormatDuration(status.Duration), status.HeadRows, status.EventRows);
        StatusLevel = StatusLevel.Neutral;
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
}
