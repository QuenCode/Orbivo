using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Orbivo.Core.Logging;

namespace Orbivo.App.ViewModels;

/// <summary>One entry of the head log rate combo box.</summary>
public sealed record RecordingRateChoice(int RateHz, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The "Aufzeichnung" group of the operator window (F50–F52): head log and event log on or off, target folder,
/// participant/session label and head log rate. The app starts and stops the recorder on <see cref="EnabledChanged"/>;
/// on, folder and rate are kept in the settings, the label only for this session.
/// </summary>
public sealed class RecordingViewModel : ObservableObject
{
    private bool _enabled;
    private bool _savedEnabled;
    private string _folder;
    private string _label = string.Empty;
    private RecordingRateChoice _selectedRate;
    private string _statusText = "Aus";
    private StatusLevel _statusLevel = StatusLevel.Neutral;
    private string? _filePath;

    public RecordingViewModel(bool enabled, string folder, int rateHz)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        Rates =
        [
            new RecordingRateChoice(0, "Jedes Bild (90 Hz in der Brille)"),
            new RecordingRateChoice(45, "45 Hz"),
            new RecordingRateChoice(30, "30 Hz"),
            new RecordingRateChoice(10, "10 Hz"),
        ];
        _enabled = enabled;
        _savedEnabled = enabled;
        _folder = folder;
        _selectedRate = Rates.FirstOrDefault(choice => choice.RateHz == rateHz) ?? Rates[0];
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

    /// <summary>Folder and label can be changed only while no recording runs.</summary>
    public bool CanEdit => !_enabled;

    public string Folder => _folder;

    /// <summary>Participant or session, part of the file names (not kept).</summary>
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

    public string FileText => _filePath is null ? "Noch keine Datei" : Path.GetFileName(_filePath) + " (+ _ereignisse.csv)";

    /// <summary>On, folder and rate to keep in the settings.</summary>
    public (bool Enabled, string Folder, int RateHz) SavedChoice => (_savedEnabled, _folder, _selectedRate.RateHz);

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
    public RecordingOptions CreateOptions(string appVersion) => new(_folder, _label, _selectedRate.RateHz, appVersion);

    /// <summary>From the recorder, twice a second. A recording that stopped by itself switches the box off (not saved).</summary>
    public void ShowStatus(RecordingStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        FilePath = status.HeadFilePath;
        if (status.Active)
        {
            string text = string.Create(
                CultureInfo.GetCultureInfo("de-DE"),
                $"Läuft seit {FormatDuration(status.Duration)} · {status.HeadRows:N0} Kopf-Zeilen · {status.EventRows:N0} Ereignisse");
            if (status.DroppedRows > 0)
            {
                StatusText = text + string.Create(CultureInfo.GetCultureInfo("de-DE"), $" · {status.DroppedRows:N0} Zeilen verloren");
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

        if (_enabled)
        {
            StatusText = "Startet …";
            StatusLevel = StatusLevel.Neutral;
            return;
        }

        StatusText = status.HeadFilePath is null ? "Aus" : string.Create(
            CultureInfo.GetCultureInfo("de-DE"),
            $"Aus · letzte Aufzeichnung {FormatDuration(status.Duration)}, {status.HeadRows:N0} Kopf-Zeilen, {status.EventRows:N0} Ereignisse");
        StatusLevel = StatusLevel.Neutral;
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.ToString(duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss", CultureInfo.InvariantCulture);
}
