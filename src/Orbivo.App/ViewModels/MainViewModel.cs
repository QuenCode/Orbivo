using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Channels;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbivo.App.LiveView;
using Orbivo.Audio.Output;
using Orbivo.Core.Audio;
using Orbivo.Core.Commands;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Core.Logging;
using Orbivo.Core.Playback;
using Orbivo.Core.Playlists;
using Orbivo.Core.Settings;
using Orbivo.Core.Status;
using Orbivo.Xr.DebugViewport;
using Orbivo.Xr.LiveView;
using Orbivo.Xr.OpenXr;

namespace Orbivo.App.ViewModels;

/// <summary>
/// View model of the operator main window (M4): playlist (F1–F3), transport with timeline, time input and
/// keyboard shortcuts (F2, F60), blackout and fade (F5). Everything reaches the player as a command
/// (CLAUDE.md rule 3); what the player does comes back through status snapshots.
/// The status panel shows the headset connection (F40, F41), or in <c>--no-xr</c> mode the preview window, and the player.
/// Format (M6, F10–F14): see <see cref="FormatViewModel"/>. Sound (M5): output device (F24), volume and mute (F4), sound and
/// A/V status.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private static readonly TimeSpan StatusPollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultFrameDuration = TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30);

    /// <summary>How long a sent seek target counts as the current position for relative jumps.</summary>
    private static readonly TimeSpan SeekTargetHold = TimeSpan.FromMilliseconds(400);

    private readonly ChannelWriter<PlayerCommand> _commands;
    private readonly List<Action> _refreshers = [];
    private readonly StatusEntry _gpuEntry;
    private readonly AppSettings _loadedSettings;
    private readonly StatusEntry _playbackEntry;
    private readonly StatusEntry _videoEntry;
    private readonly StatusEntry _decoderEntry;
    private readonly StatusEntry _viewEntry;
    private readonly StatusEntry? _viewportEntry;
    private readonly StatusEntry? _headsetEntry;
    private readonly StatusEntry? _sessionEntry;
    private readonly StatusEntry? _xrRenderEntry;
    private readonly StatusEntry _outputEntry;
    private readonly StatusEntry _playlistEntry;
    private readonly StatusEntry _soundEntry;
    private readonly StatusEntry _soundOutputEntry;
    private readonly StatusEntry _avSyncEntry;
    private readonly Func<IReadOnlyList<AudioDeviceInfo>> _listAudioDevices;
    private AudioDeviceChoice? _selectedAudioDevice;
    private double _volumePercent;
    private bool _muted;
    private bool _soundLocked;
    private readonly bool _noXr;

    /// <summary>False with <c>--video</c>: that session's playlist is neither restored nor saved (M9).</summary>
    private readonly bool _keepsPlaylist;
    private LiveViewController? _liveView;
    private bool _liveViewEnabled;
    private string _liveViewStatusText = "Aus";
    private bool _updatingDevices;
    private string _audioModeText = "–";
    private readonly Stopwatch _sinceSeekSent = new();
    private DispatcherTimer? _statusTimer;
    private PlayerStatus _lastStatus = PlayerStatus.Empty;
    private PlaybackState _playbackState = PlaybackState.NoMedia;
    private bool _playerRunning;
    private RecenterStatus _recenter;
    private readonly StatusEntry _recenterEntry = new("Recenter", "Aus (Vorderseite des Videos)", StatusLevel.Neutral);
    private string _timeText = MediaTime.Format(TimeSpan.Zero) + " / " + MediaTime.Format(TimeSpan.Zero);
    private double _timelineSeconds;
    private double _durationSeconds;
    private bool _scrubbing;
    private bool _updatingTimeline;
    private TimeSpan _lastSeekTarget;
    private string _timeInput = string.Empty;
    private string _fadeSecondsText = "1,0";
    private TimeSpan _fadeDuration = TimeSpan.FromSeconds(1);
    private bool _autoAdvance = true;
    private bool _loop;
    private PlaylistEntry? _selectedEntry;

    /// <summary>Files of the last playlist that were gone at the start; shown until the playlist changes.</summary>
    private string? _restoreProblem;
    private string _outputText = "Brille: Leerlauf";
    private StatusLevel _outputLevel = StatusLevel.Neutral;

    public MainViewModel(
        StartupOptions options,
        IEnumerable<NativeComponentStatus> nativeComponents,
        string logFilePath,
        ChannelWriter<PlayerCommand> commands,
        AppSettings settings,
        Func<IReadOnlyList<AudioDeviceInfo>> listAudioDevices,
        string appVersion = "")
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(nativeComponents);
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(listAudioDevices);
        _commands = commands;
        _noXr = options.NoXr;
        _listAudioDevices = listAudioDevices;
        Format = new FormatViewModel(Send);
        _loadedSettings = settings;
        Idle = new IdleViewModel(settings.IdleView, settings.IdleImagePath, options.NoXr);
        Idle.SettingsChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        Idle.Changed += (_, _) => ShowOutput(_lastStatus);
        Recording = new RecordingViewModel(settings.RecordingEnabled, settings.RecordingFolder ?? AppSettings.DefaultRecordingFolder, settings.HeadSampleRateHz, settings.RecordingPerVideo);
        Recording.SettingsChanged += (_, _) => SettingsChanged?.Invoke(this, EventArgs.Empty);
        _volumePercent = Math.Round(settings.Volume * 100);
        _soundLocked = settings.SoundLocked;
        _autoAdvance = settings.AutoAdvance;
        _fadeDuration = TimeSpan.FromSeconds(settings.FadeSeconds);
        _fadeSecondsText = settings.FadeSeconds.ToString("0.0#", CultureInfo.CurrentCulture);
        _keepsPlaylist = options.VideoPath is null;
        Playlist = new Playlist();
        ((System.Collections.Specialized.INotifyCollectionChanged)Playlist.Entries).CollectionChanged += (_, _) => OnPlaylistChanged();

        PlayCommand = new RelayCommand(
            () => Send(new Core.Commands.PlayCommand()),
            () => _playbackState is PlaybackState.Ready or PlaybackState.Paused or PlaybackState.Ended or PlaybackState.Opening);
        PauseCommand = new RelayCommand(
            () => Send(new Core.Commands.PauseCommand()),
            () => _playbackState == PlaybackState.Playing);
        StopCommand = new RelayCommand(
            () => Send(new Core.Commands.StopCommand()),
            () => _playbackState is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ended
                || (_playbackState == PlaybackState.Ready && _lastStatus.Position > TimeSpan.Zero));
        PlayPauseCommand = new RelayCommand(TogglePlayPause);
        NextCommand = new RelayCommand(() => LoadEntry(Playlist.MoveNext()), () => Playlist.HasNext);
        PreviousCommand = new RelayCommand(() => LoadEntry(Playlist.MovePrevious()), () => Playlist.HasPrevious);
        LoadSelectedCommand = new RelayCommand(LoadSelected, () => SelectedEntry is not null);
        RemoveSelectedCommand = new RelayCommand(RemoveSelected, () => SelectedEntry is { } entry && CanRemove(entry));
        MoveSelectedUpCommand = new RelayCommand(() => MoveSelected(-1), () => SelectedEntry is not null && Playlist.Entries.IndexOf(SelectedEntry) > 0);
        MoveSelectedDownCommand = new RelayCommand(
            () => MoveSelected(+1),
            () => SelectedEntry is not null && Playlist.Entries.IndexOf(SelectedEntry) < Playlist.Entries.Count - 1);
        SeekRelativeCommand = new RelayCommand<string>(SeekRelative, _ => CanSeek);
        StepFrameCommand = new RelayCommand<string>(StepFrame, _ => CanSeek);
        GoToTimeCommand = new RelayCommand(GoToTime, () => CanSeek);
        ToggleBlackoutCommand = new RelayCommand(ToggleBlackout, () => _playerRunning);
        ToggleFadeCommand = new RelayCommand(ToggleFade, () => _playerRunning);
        RecenterCommand = new RelayCommand(() => Send(new Core.Commands.RecenterCommand()), () => _playerRunning && _recenter.Available);
        ResetRecenterCommand = new RelayCommand(
            () => Send(new Core.Commands.ResetRecenterCommand()),
            () => _playerRunning && _recenter.Available && (_recenter.IsActive || _recenter.Pending));
        ToggleMuteCommand = new RelayCommand(() => Muted = !Muted);
        RefreshAudioDevicesCommand = new RelayCommand(RefreshAudioDevices, () => !SoundLocked);

        OpenManualCommand = new RelayCommand(() => ManualRequested?.Invoke(this, EventArgs.Empty));

        // "1.0.0+<commit>" → "1.0.0"; the full version is in the log.
        string shortVersion = appVersion.Split('+')[0];
        string name = shortVersion.Length == 0 ? "Orbivo" : "Orbivo " + shortVersion;
        Title = options.NoXr ? name + " – Operator (ohne Brille)" : name + " – Operator";

        var entries = new List<StatusEntry>
        {
            new("Modus", options.NoXr ? "Desktop-Vorschau (--no-xr)" : "Brille (OpenXR)", StatusLevel.Neutral),
        };

        _gpuEntry = new StatusEntry("GPU", options.NoXr ? "Wird eingerichtet …" : "Wartet auf die Brille …", StatusLevel.Neutral);
        _playbackEntry = options.VideoPath is null
            ? new StatusEntry(
                "Wiedergabe",
                "Kein Video geladen. Videos über „Hinzufügen …“ oder per Drag & Drop in die Playlist",
                StatusLevel.Neutral)
            : new StatusEntry("Wiedergabe", "Wartet auf die Grafikkarte …", StatusLevel.Neutral);
        _videoEntry = new StatusEntry("Video", "–", StatusLevel.Neutral);
        _decoderEntry = new StatusEntry("Decoder", "–", StatusLevel.Neutral);
        _outputEntry = new StatusEntry("Ausgabe", "Bild (kein Schwarzbild)", StatusLevel.Neutral);
        _playlistEntry = new StatusEntry("Playlist", "Leer", StatusLevel.Neutral);
        _soundEntry = new StatusEntry("Ton", "–", StatusLevel.Neutral);
        _soundOutputEntry = new StatusEntry("Tonausgabe", "Startet …", StatusLevel.Neutral);
        _avSyncEntry = new StatusEntry("A/V", "–", StatusLevel.Neutral);

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
                _outputEntry,
                _playlistEntry,
                _videoEntry,
                _decoderEntry,
                _soundEntry,
                _soundOutputEntry,
                _avSyncEntry,
                _viewEntry,
                _recenterEntry,
            ]);
        }
        else
        {
            _headsetEntry = new StatusEntry("Brille", "Startet …", StatusLevel.Neutral);
            _sessionEntry = new StatusEntry("Session", "–", StatusLevel.Neutral);
            _xrRenderEntry = new StatusEntry("Bildausgabe", "–", StatusLevel.Neutral);
            _viewEntry = new StatusEntry("Blick (Proband)", "–", StatusLevel.Neutral);
            entries.AddRange(
            [
                _headsetEntry, _sessionEntry, _xrRenderEntry, _gpuEntry, _playbackEntry, _outputEntry, _playlistEntry,
                _videoEntry, _decoderEntry, _soundEntry, _soundOutputEntry, _avSyncEntry, _viewEntry, _recenterEntry,
            ]);
        }

        entries.AddRange(nativeComponents.Select(component => new StatusEntry(
            component.Component,
            component.Detail,
            component.IsAvailable ? StatusLevel.Ok : StatusLevel.Error)));
        entries.Add(new StatusEntry("Log-Datei", logFilePath, StatusLevel.Neutral));

        StatusEntries = entries;
        FillAudioDevices(settings.AudioDeviceId);

        // Loop goes to the player like a click; the command waits in the channel until the player runs.
        Loop = settings.Loop;
        if (_keepsPlaylist)
        {
            RestorePlaylist(settings.Playlist, settings.PlaylistSelected);
        }
    }

    /// <summary>The operator changed the output device; the app saves the settings.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>The operator asked for the manual (link or F1); the app opens Anleitung.html.</summary>
    public event EventHandler? ManualRequested;

    /// <summary>Opens the operator manual (F1).</summary>
    public IRelayCommand OpenManualCommand { get; }

    /// <summary>
    /// Settings to keep: output device, volume and their lock (not mute), idle view, recording, playlist with its
    /// switches and the fade duration.
    /// </summary>
    public AppSettings CurrentSettings => _loadedSettings with
    {
        AudioDeviceId = SelectedAudioDevice?.Id,
        Volume = (float)(VolumePercent / 100),
        SoundLocked = SoundLocked,
        IdleView = Idle.SavedChoice.View,
        IdleImagePath = Idle.SavedChoice.ImagePath,
        RecordingEnabled = Recording.SavedChoice.Enabled,
        RecordingFolder = string.Equals(Recording.SavedChoice.Folder, AppSettings.DefaultRecordingFolder, StringComparison.OrdinalIgnoreCase) ? null : Recording.SavedChoice.Folder,
        HeadSampleRateHz = Recording.SavedChoice.RateHz,
        RecordingPerVideo = Recording.SavedChoice.PerVideo,
        Playlist = _keepsPlaylist ? (Playlist.Entries.Count == 0 ? null : Playlist.Entries.Select(entry => entry.Path).ToArray()) : _loadedSettings.Playlist,
        PlaylistSelected = _keepsPlaylist ? SavedPlaylistIndex() : _loadedSettings.PlaylistSelected,
        AutoAdvance = AutoAdvance,
        Loop = Loop,
        FadeSeconds = _fadeDuration.TotalSeconds,
    };

    /// <summary>The "Brille im Leerlauf" group (F7).</summary>
    public IdleViewModel Idle { get; }

    /// <summary>The "Aufzeichnung" group (F50–F52).</summary>
    public RecordingViewModel Recording { get; }

    /// <summary>Shows the recorder's status with every status refresh.</summary>
    public void StartRecordingPolling(Func<RecordingStatus> getStatus)
    {
        ArgumentNullException.ThrowIfNull(getStatus);
        AddRefresher(() => Recording.ShowStatus(getStatus()));
    }

    /// <summary>
    /// Locks output device and volume against a slip (kept across restarts). Mute stays usable, as an emergency stop.
    /// </summary>
    public bool SoundLocked
    {
        get => _soundLocked;
        set
        {
            if (SetProperty(ref _soundLocked, value))
            {
                OnPropertyChanged(nameof(SoundSettingsEditable));
                RefreshAudioDevicesCommand.NotifyCanExecuteChanged();
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>True while device and volume may be changed (not <see cref="SoundLocked"/>).</summary>
    public bool SoundSettingsEditable => !SoundLocked;

    /// <summary>"Windows-Standard" and the active output devices (F24).</summary>
    public ObservableCollection<AudioDeviceChoice> AudioDevices { get; } = [];

    public AudioDeviceChoice? SelectedAudioDevice
    {
        get => _selectedAudioDevice;
        set
        {
            if (value is null)
            {
                return;
            }

            if (SoundLocked && !_updatingDevices)
            {
                // Locked: the list goes back to the kept device.
                OnPropertyChanged(nameof(SelectedAudioDevice));
                return;
            }

            if (!SetProperty(ref _selectedAudioDevice, value) || _updatingDevices)
            {
                return;
            }

            Send(new SetAudioDeviceCommand(value.Id));
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Volume in percent (F4); the player applies a loudness curve.</summary>
    public double VolumePercent
    {
        get => _volumePercent;
        set
        {
            if (SoundLocked)
            {
                // Locked: the slider goes back to the kept volume.
                OnPropertyChanged(nameof(VolumePercent));
                return;
            }

            if (SetProperty(ref _volumePercent, Math.Clamp(Math.Round(value), 0, 100)))
            {
                Send(new SetVolumeCommand((float)(_volumePercent / 100)));
                OnPropertyChanged(nameof(VolumeText));
            }
        }
    }

    public string VolumeText => string.Create(CultureInfo.CurrentCulture, $"{VolumePercent:0} %");

    /// <summary>Mute (F4, key M). Not kept across restarts.</summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            if (SetProperty(ref _muted, value))
            {
                Send(new SetMuteCommand(value));
                OnPropertyChanged(nameof(MuteButtonText));
            }
        }
    }

    public string MuteButtonText => Muted ? "Ton an (M)" : "Stumm (M)";

    public IRelayCommand ToggleMuteCommand { get; }

    public IRelayCommand RefreshAudioDevicesCommand { get; }

    /// <summary>How the sound of the current video reaches the headphones.</summary>
    public string AudioModeText
    {
        get => _audioModeText;
        private set => SetProperty(ref _audioModeText, value);
    }

    public string Title { get; }

    /// <summary>Live view (F42): what the participant sees, in the operator window. Off at every start.</summary>
    public bool LiveViewEnabled
    {
        get => _liveViewEnabled;
        set
        {
            if (SetProperty(ref _liveViewEnabled, value))
            {
                _liveView?.SetEnabled(value);
                ShowLiveViewStatus();
            }
        }
    }

    public string LiveViewStatusText
    {
        get => _liveViewStatusText;
        private set => SetProperty(ref _liveViewStatusText, value);
    }

    public IReadOnlyList<StatusEntry> StatusEntries { get; }

    public IRelayCommand PlayCommand { get; }

    public IRelayCommand PauseCommand { get; }

    public IRelayCommand StopCommand { get; }

    /// <summary>Space bar: Pause while playing, otherwise Play.</summary>
    public IRelayCommand PlayPauseCommand { get; }

    public IRelayCommand NextCommand { get; }

    public IRelayCommand PreviousCommand { get; }

    public IRelayCommand LoadSelectedCommand { get; }

    public IRelayCommand RemoveSelectedCommand { get; }

    public IRelayCommand MoveSelectedUpCommand { get; }

    public IRelayCommand MoveSelectedDownCommand { get; }

    /// <summary>Parameter: seconds to jump, e.g. "-5" or "30".</summary>
    public IRelayCommand<string> SeekRelativeCommand { get; }

    /// <summary>Parameter: "-1" or "1" frames.</summary>
    public IRelayCommand<string> StepFrameCommand { get; }

    public IRelayCommand GoToTimeCommand { get; }

    public IRelayCommand ToggleBlackoutCommand { get; }

    public IRelayCommand ToggleFadeCommand { get; }

    /// <summary>The test person's viewing direction becomes the front of the video (F30, key R).</summary>
    public IRelayCommand RecenterCommand { get; }

    /// <summary>The video's own front applies again (Shift+R).</summary>
    public IRelayCommand ResetRecenterCommand { get; }

    public Playlist Playlist { get; }

    /// <summary>The entry selected in the list (not necessarily the loaded one).</summary>
    public PlaylistEntry? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (SetProperty(ref _selectedEntry, value))
            {
                RefreshPlaylistCommands();
            }
        }
    }

    /// <summary>At the end of a video, open the next one and play it (F3).</summary>
    public bool AutoAdvance
    {
        get => _autoAdvance;
        set
        {
            if (SetProperty(ref _autoAdvance, value))
            {
                ShowPlaylistStatus();
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Repeat the current video (F3). Wins over <see cref="AutoAdvance"/>.</summary>
    public bool Loop
    {
        get => _loop;
        set
        {
            if (SetProperty(ref _loop, value))
            {
                Send(new SetLoopCommand(value));
                ShowPlaylistStatus();
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>"position / duration" for the transport bar.</summary>
    public string TimeText
    {
        get => _timeText;
        private set => SetProperty(ref _timeText, value);
    }

    /// <summary>Length of the video in seconds (timeline maximum), at least 1 so the slider has a range.</summary>
    public double DurationSeconds
    {
        get => _durationSeconds;
        private set => SetProperty(ref _durationSeconds, value);
    }

    /// <summary>
    /// Timeline position in seconds. The status updates it; when the operator moves the slider (click or drag),
    /// the new value is sent as a seek.
    /// </summary>
    public double TimelineSeconds
    {
        get => _timelineSeconds;
        set
        {
            if (SetProperty(ref _timelineSeconds, value) && !_updatingTimeline && CanSeek)
            {
                SeekTo(TimeSpan.FromSeconds(value));
            }
        }
    }

    /// <summary>Time typed by the operator ("1:23.4", "83,4").</summary>
    public string TimeInput
    {
        get => _timeInput;
        set => SetProperty(ref _timeInput, value);
    }

    /// <summary>Fade duration in seconds as typed (comma or point).</summary>
    public string FadeSecondsText
    {
        get => _fadeSecondsText;
        set
        {
            if (SetProperty(ref _fadeSecondsText, value)
                && double.TryParse(value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds))
            {
                _fadeDuration = TimeSpan.FromSeconds(Math.Clamp(seconds, AppSettings.MinFadeSeconds, AppSettings.MaxFadeSeconds));
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Big indicator: what the participant sees right now.</summary>
    public string OutputText
    {
        get => _outputText;
        private set => SetProperty(ref _outputText, value);
    }

    public StatusLevel OutputStatusLevel
    {
        get => _outputLevel;
        private set => SetProperty(ref _outputLevel, value);
    }

    /// <summary>"Schwarzbild" or "Schwarzbild aus", depending on the output.</summary>
    public string BlackoutButtonText => _lastStatus.Output.TowardsBlack ? "Schwarzbild aus (B)" : "Schwarzbild (B)";

    /// <summary>"Ausblenden" or "Einblenden", depending on the output.</summary>
    public string FadeButtonText => _lastStatus.Output.TowardsBlack ? "Einblenden (F)" : "Ausblenden (F)";

    /// <summary>True if the timeline and time input can be used (a video is open).</summary>
    public bool IsSeekable => CanSeek;

    private bool CanSeek => _playbackState is PlaybackState.Ready or PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ended;

    /// <summary>The "Format" group (F10–F14).</summary>
    public FormatViewModel Format { get; }

    public void ShowGraphicsDevice(string adapterName, ulong dedicatedVideoMemoryBytes)
    {
        double gigabytes = dedicatedVideoMemoryBytes / (1024.0 * 1024.0 * 1024.0);
        _gpuEntry.Update(string.Create(CultureInfo.CurrentCulture, $"{adapterName} ({gigabytes:0} GB)"), StatusLevel.Ok);
    }

    public void ShowGraphicsDeviceFailed()
    {
        _gpuEntry.Update("Grafikkarte konnte nicht eingerichtet werden (Details im Log)", StatusLevel.Error);
        _viewportEntry?.Update("Nicht gestartet", StatusLevel.Error);
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
        if (expected)
        {
            Idle.ShowImageLoaded(text);
        }
        else
        {
            Idle.ShowImageFailed(text + ". Ungewöhnliches Seitenverhältnis für Equirect 360°");
        }
    }

    public void ShowImageFailed(string fileName, string reason)
    {
        Idle.ShowImageFailed($"{fileName}: {reason}");
    }

    /// <summary>Refreshes playback, video and decoder ten times per second.</summary>
    public void StartPlayerPolling(Func<PlayerStatus> getStatus)
    {
        ArgumentNullException.ThrowIfNull(getStatus);
        _playerRunning = true;
        ToggleBlackoutCommand.NotifyCanExecuteChanged();
        ToggleFadeCommand.NotifyCanExecuteChanged();
        RecenterCommand.NotifyCanExecuteChanged();
        ResetRecenterCommand.NotifyCanExecuteChanged();
        AddRefresher(() => ShowPlayerStatus(getStatus()));
    }

    /// <summary>
    /// Adds video files to the playlist. Dropped paths: folders contribute the videos in them, files without a video
    /// extension are left out. Files picked in the file dialog (<paramref name="explicitFiles"/>) are all added.
    /// Returns the number of videos added.
    /// </summary>
    public int AddFiles(IEnumerable<string> paths, bool explicitFiles = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        IReadOnlyList<string> videos = explicitFiles ? paths.Select(Path.GetFullPath).ToList() : Playlist.ExpandPaths(paths);
        IReadOnlyList<PlaylistEntry> added = Playlist.Add(videos);
        if (added.Count > 0)
        {
            SelectedEntry = added[0];
        }
        else
        {
            _playlistEntry.Update("Keine Videodateien gefunden (" + string.Join(", ", Playlist.VideoExtensions) + ")", StatusLevel.Warning);
        }

        return added.Count;
    }

    /// <summary>Adds <paramref name="path"/> and loads it (black until Play), for <c>--video</c>.</summary>
    public void AddAndLoad(string path)
    {
        // Not filtered by extension: the operator asked for exactly this file.
        PlaylistEntry entry = Playlist.Add([Path.GetFullPath(path)])[0];
        SelectedEntry = entry;
        Playlist.SetCurrent(entry);
        LoadEntry(entry);
    }

    public void ShowPlayerUnavailable(string reason)
    {
        _playbackEntry.Update(reason, StatusLevel.Error);
    }

    /// <summary>The live view controller; its state is shown ten times per second.</summary>
    public void AttachLiveView(LiveViewController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        _liveView = controller;
        controller.SetEnabled(LiveViewEnabled);
        AddRefresher(ShowLiveViewStatus);
    }

    /// <summary>The child window of the live view was created (handle) or is about to be destroyed (0).</summary>
    public void SetLiveViewWindow(nint window)
    {
        _liveView?.SetWindow(window);
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

    /// <summary>
    /// Scrubbing: while the operator drags the timeline thumb, the status does not move it, and every new
    /// position is sent as a seek (the player keeps only the latest).
    /// </summary>
    public void SetScrubbing(bool scrubbing)
    {
        _scrubbing = scrubbing;
    }

    private void Send(PlayerCommand command)
    {
        if (!_commands.TryWrite(command))
        {
            _playbackEntry.Update("Befehl konnte nicht an den Player übergeben werden", StatusLevel.Error);
        }
    }

    private void TogglePlayPause()
    {
        if (_playbackState == PlaybackState.Playing)
        {
            Send(new Core.Commands.PauseCommand());
        }
        else if (PlayCommand.CanExecute(null))
        {
            Send(new Core.Commands.PlayCommand());
        }
    }

    /// <summary>
    /// Opens <paramref name="entry"/>. While a video plays the new one plays at once; otherwise it waits
    /// black until Play (F6).
    /// </summary>
    private void LoadEntry(PlaylistEntry? entry)
    {
        if (entry is null)
        {
            return;
        }

        bool play = _playbackState == PlaybackState.Playing;
        Send(new OpenMediaCommand(entry.Path, play));
        SelectedEntry = entry;
        RefreshPlaylistCommands();
    }

    private void LoadSelected()
    {
        if (SelectedEntry is { } entry)
        {
            Playlist.SetCurrent(entry);
            LoadEntry(entry);
        }
    }

    private void RemoveSelected()
    {
        if (SelectedEntry is not { } entry)
        {
            return;
        }

        if (!CanRemove(entry))
        {
            return;
        }

        bool loaded = entry == Playlist.Current;
        int index = Playlist.Entries.IndexOf(entry);
        Playlist.Remove(entry);
        if (loaded)
        {
            // Close the file, so it can be moved or deleted, and empty the status.
            Send(new CloseMediaCommand());
        }

        SelectedEntry = Playlist.Entries.Count == 0 ? null : Playlist.Entries[Math.Min(index, Playlist.Entries.Count - 1)];
    }

    /// <summary>
    /// The loaded video cannot be removed while it plays, pauses, opens or shows its last frame: the participant
    /// would lose the picture by a slip. After Stop (black, ready) it can.
    /// </summary>
    private bool CanRemove(PlaylistEntry entry)
    {
        return entry != Playlist.Current
            || _playbackState is not (PlaybackState.Opening or PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ended);
    }

    private void MoveSelected(int direction)
    {
        if (SelectedEntry is { } entry)
        {
            Playlist.Move(entry, direction);
            RefreshPlaylistCommands();
        }
    }

    private void OnPlaylistChanged()
    {
        _restoreProblem = null;
        RefreshPlaylistCommands();
        ShowPlaylistStatus();
        if (_keepsPlaylist)
        {
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Puts the playlist of the last session back (M9). Nothing is loaded: the entry that was loaded last is selected,
    /// "Laden" or a double click opens it. Files that are gone are left out and named in the playlist status.
    /// </summary>
    private void RestorePlaylist(IReadOnlyList<string>? paths, int selected)
    {
        if (paths is null || paths.Count == 0)
        {
            return;
        }

        var present = new List<string>();
        var missing = new List<string>();
        int selectedPresent = -1;
        for (int index = 0; index < paths.Count; index++)
        {
            if (File.Exists(paths[index]))
            {
                if (index == selected)
                {
                    selectedPresent = present.Count;
                }

                present.Add(paths[index]);
            }
            else
            {
                missing.Add(Path.GetFileName(paths[index]));
            }
        }

        IReadOnlyList<PlaylistEntry> added = Playlist.Add(present);
        if (added.Count > 0)
        {
            SelectedEntry = added[selectedPresent >= 0 ? selectedPresent : 0];
        }

        if (missing.Count > 0)
        {
            _restoreProblem = missing.Count == 1
                ? $"1 Video der letzten Playlist fehlt: {missing[0]}"
                : $"{missing.Count} Videos der letzten Playlist fehlen: {string.Join(", ", missing)}";
            ShowPlaylistStatus();
        }
    }

    /// <summary>The loaded entry (or, if none, the selected one), to select it after a restart.</summary>
    private int SavedPlaylistIndex()
    {
        PlaylistEntry? entry = Playlist.Current ?? SelectedEntry;
        return entry is null ? -1 : Playlist.Entries.IndexOf(entry);
    }

    private void RefreshPlaylistCommands()
    {
        NextCommand.NotifyCanExecuteChanged();
        PreviousCommand.NotifyCanExecuteChanged();
        LoadSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        MoveSelectedUpCommand.NotifyCanExecuteChanged();
        MoveSelectedDownCommand.NotifyCanExecuteChanged();
        ShowPlaylistStatus();
    }

    private void ShowPlaylistStatus()
    {
        int count = Playlist.Entries.Count;
        int current = Playlist.CurrentIndex;
        string text = count == 0
            ? "Leer"
            : current < 0
                ? $"{count} Videos, keins geladen"
                : $"Video {current + 1} von {count}";
        if (count > 0)
        {
            text += Loop ? ", Wiederholen an" : AutoAdvance ? ", automatisch weiter" : ", hält am Ende";
        }

        if (_restoreProblem is not null)
        {
            _playlistEntry.Update(text + ". " + _restoreProblem, StatusLevel.Warning);
            return;
        }

        _playlistEntry.Update(text, StatusLevel.Neutral);
    }

    /// <summary>Where relative jumps start: the last sent target while the player is still on its way there.</summary>
    private TimeSpan SeekBase =>
        _lastStatus.Seeking || (_sinceSeekSent.IsRunning && _sinceSeekSent.Elapsed < SeekTargetHold)
            ? _lastSeekTarget
            : _lastStatus.Position;

    private void SeekTo(TimeSpan position)
    {
        TimeSpan duration = _lastStatus.Duration;
        if (position < TimeSpan.Zero)
        {
            position = TimeSpan.Zero;
        }

        if (duration > TimeSpan.Zero && position > duration)
        {
            position = duration;
        }

        _lastSeekTarget = position;
        _sinceSeekSent.Restart();
        Send(new SeekCommand(position));
        if (!_scrubbing)
        {
            ShowTimeline(position, duration);
        }
    }

    private void SeekRelative(string? seconds)
    {
        if (double.TryParse(seconds, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value))
        {
            SeekTo(SeekBase + TimeSpan.FromSeconds(value));
        }
    }

    private void StepFrame(string? frames)
    {
        if (int.TryParse(frames, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int count))
        {
            TimeSpan frame = _lastStatus.FrameDuration > TimeSpan.Zero ? _lastStatus.FrameDuration : DefaultFrameDuration;
            SeekTo(SeekBase + (frame * count));
        }
    }

    private void GoToTime()
    {
        if (MediaTime.TryParse(TimeInput, out TimeSpan time))
        {
            SeekTo(time);
            TimeInput = MediaTime.Format(time);
        }
        else
        {
            _playbackEntry.Update($"Zeit „{TimeInput}“ nicht lesbar. Beispiele: 83, 1:23, 1:23.5, 0:01:23", StatusLevel.Warning);
        }
    }

    private void ToggleBlackout()
    {
        Send(new SetBlackoutCommand(!_lastStatus.Output.TowardsBlack));
    }

    private void RefreshAudioDevices()
    {
        FillAudioDevices(SelectedAudioDevice?.Id);
    }

    /// <summary>Lists the devices again; the selection stays, also if that device is unplugged right now.</summary>
    private void FillAudioDevices(string? selectedId)
    {
        _updatingDevices = true;
        try
        {
            AudioDevices.Clear();
            AudioDevices.Add(new AudioDeviceChoice(null, "Windows-Standard"));
            foreach (AudioDeviceInfo device in _listAudioDevices())
            {
                AudioDevices.Add(new AudioDeviceChoice(device.Id, device.Name));
            }

            AudioDeviceChoice? selected = AudioDevices.FirstOrDefault(choice => choice.Id == selectedId);
            if (selected is null)
            {
                selected = new AudioDeviceChoice(selectedId, "Gespeichertes Gerät (nicht verbunden)");
                AudioDevices.Add(selected);
            }

            _selectedAudioDevice = selected;
            OnPropertyChanged(nameof(SelectedAudioDevice));
        }
        finally
        {
            _updatingDevices = false;
        }
    }

    private void ToggleFade()
    {
        Send(new FadeCommand(!_lastStatus.Output.TowardsBlack, _fadeDuration));
    }

    private void ShowTimeline(TimeSpan position, TimeSpan duration)
    {
        TimeText = MediaTime.Format(position) + " / " + MediaTime.Format(duration);
        _updatingTimeline = true;
        try
        {
            DurationSeconds = Math.Max(1, duration.TotalSeconds);
            TimelineSeconds = Math.Clamp(position.TotalSeconds, 0, DurationSeconds);
        }
        finally
        {
            _updatingTimeline = false;
        }
    }

    private void ShowPlayerStatus(PlayerStatus status)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        PlayerStatus previous = _lastStatus;
        _lastStatus = status;
        if (status.State != _playbackState)
        {
            PlaybackState before = _playbackState;
            _playbackState = status.State;
            PlayCommand.NotifyCanExecuteChanged();
            PauseCommand.NotifyCanExecuteChanged();
            SeekRelativeCommand.NotifyCanExecuteChanged();
            StepFrameCommand.NotifyCanExecuteChanged();
            GoToTimeCommand.NotifyCanExecuteChanged();
            RemoveSelectedCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsSeekable));
            if (status.State == PlaybackState.Ended && before == PlaybackState.Playing)
            {
                AdvanceAfterEnd(status);
            }
        }

        StopCommand.NotifyCanExecuteChanged();
        if (status.Output.TowardsBlack != previous.Output.TowardsBlack)
        {
            OnPropertyChanged(nameof(BlackoutButtonText));
            OnPropertyChanged(nameof(FadeButtonText));
        }

        if (!_scrubbing)
        {
            ShowTimeline(status.Position, status.Duration);
        }

        string file = status.FilePath is null ? string.Empty : Path.GetFileName(status.FilePath);
        string seeking = status.Seeking ? ", springt …" : string.Empty;
        (string stateText, StatusLevel stateLevel) = status.State switch
        {
            PlaybackState.NoMedia => ("Kein Video", StatusLevel.Neutral),
            PlaybackState.Opening => ($"{file}: wird geöffnet …", StatusLevel.Neutral),
            PlaybackState.Ready => ($"{file}: bereit, Brille schwarz bis Play{seeking}", StatusLevel.Ok),
            PlaybackState.Playing => ($"{file}: läuft{seeking}", StatusLevel.Ok),
            PlaybackState.Paused => ($"{file}: Pause{seeking}", StatusLevel.Ok),
            PlaybackState.Ended => ($"{file}: Ende erreicht, letztes Bild steht", StatusLevel.Ok),
            _ => ($"{file}: {status.Error ?? "Fehler"}", StatusLevel.Error),
        };
        _playbackEntry.Update(stateText, stateLevel);
        ShowOutput(status);
        ShowSound(status);

        Format.Update(status.Format);
        VideoStreamInfo? video = status.Video;
        if (video is null)
        {
            _videoEntry.Update("–", StatusLevel.Neutral);
            _decoderEntry.Update("–", StatusLevel.Neutral);
            return;
        }

        VideoFormatStatus? format = status.Format;
        string formatText = format is null
            ? string.Empty
            : $", {VideoFormatResolver.Describe(format.Format.Projection)} {DescribeLayout(format.Format.Layout)}{(format.Format.SwapEyes ? ", Augen getauscht" : string.Empty)}";
        bool formatWarning = format is not null && (format.Warnings.Count > 0 || format.SidecarError is not null);
        _videoEntry.Update(
            string.Create(
                culture,
                $"{video.Codec} {video.Profile}, {video.Width} × {video.Height}, {video.FrameRate:0.##} fps, {video.Color.BitDepth} Bit, {DescribeColor(video.Color)}{formatText}")
                + (formatWarning ? ". Formathinweise im Bereich Format" : string.Empty),
            formatWarning ? StatusLevel.Warning : StatusLevel.Ok);

        string decoderText = string.Create(
            culture,
            $"{(video.HardwareDecoding ? "Hardware (D3D11VA)" : $"Software-Dekodierung ({video.SoftwareReason ?? "ohne Angabe"})")}, {status.DecodedFramesPerSecond:0} fps dekodiert, Puffer {status.QueuedFrames}/{status.QueueCapacity}, verworfen {status.DroppedFrames}");

        // While playing, the buffer stays nearly full if the decoder keeps up. Running empty means stutter.
        bool starving = status.State == PlaybackState.Playing && status.QueuedFrames <= 1 && status.Position < status.Duration - TimeSpan.FromSeconds(1);
        _decoderEntry.Update(
            starving ? decoderText + ". Puffer läuft leer, Decoder zu langsam" : decoderText,
            starving || status.DroppedFrames > 0 || !video.HardwareDecoding ? StatusLevel.Warning : StatusLevel.Ok);
    }

    /// <summary>Auto-advance (F3): the player reached the end of the current video and does not loop.</summary>
    private void AdvanceAfterEnd(PlayerStatus status)
    {
        PlaylistEntry? current = Playlist.Current;
        if (!AutoAdvance || current is null || !string.Equals(current.Path, status.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PlaylistEntry? next = Playlist.MoveNext();
        if (next is not null)
        {
            Send(new OpenMediaCommand(next.Path, Play: true));
            SelectedEntry = next;
            RefreshPlaylistCommands();
        }
    }

    /// <summary>What the participant sees (F40): video, still frame, or black, and blackout or fade on top.</summary>
    private void ShowOutput(PlayerStatus status)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        OutputLevel output = status.Output;
        bool videoVisible = status.State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Ended;
        string idle = Idle.View switch
        {
            IdleView.SeeThrough => "See-through",
            IdleView.Image => "Standbild",
            _ => "Schwarz",
        };
        string content = status.State switch
        {
            PlaybackState.Playing => "Video läuft",
            PlaybackState.Paused => "Videobild steht (Pause)",
            PlaybackState.Ended => "Letztes Bild (Ende)",
            PlaybackState.NoMedia => $"Leerlauf: {idle} (kein Video geladen)",
            _ => $"Leerlauf: {idle} (Video noch nicht gestartet)",
        };

        string text;
        StatusLevel level;
        if (output.Fading)
        {
            text = string.Create(
                culture,
                $"{(output.TowardsBlack ? "Blendet aus" : "Blendet ein")} ({output.Level * 100:0} %): {content}");
            level = StatusLevel.Warning;
        }
        else if (output.Level <= 0f)
        {
            text = "SCHWARZBILD" + (videoVisible ? $" (dahinter: {content})" : string.Empty);
            level = StatusLevel.Neutral;
        }
        else
        {
            text = content;
            level = videoVisible ? StatusLevel.Ok : StatusLevel.Neutral;
        }

        OutputText = "Brille: " + text;
        OutputStatusLevel = level;
        _outputEntry.Update(
            output.Fading ? text : output.Level <= 0f ? "Schwarzbild aktiv" : "Bild (kein Schwarzbild)",
            output.Fading ? StatusLevel.Warning : output.Level <= 0f ? StatusLevel.Warning : StatusLevel.Ok);
    }

    /// <summary>Sound track, output device and A/V deviation (M5).</summary>
    private void ShowSound(PlayerStatus status)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        AudioTrackInfo? track = status.Audio;
        if (track is null)
        {
            _soundEntry.Update(status.Video is null ? "–" : "Kein Ton im Video (Systemuhr als Takt)", StatusLevel.Neutral);
            AudioModeText = status.Video is null ? "–" : "Kein Ton";
        }
        else
        {
            AudioConversionPlan plan = track.Conversion;
            (string mode, string? problem) = DescribeSoundMode(plan, status.AudioOutput?.Spatial);
            string text = string.Create(culture, $"{track.Codec}, {plan.Describe()}, {mode}");
            if (plan.IsDownmix)
            {
                text += ". Mehrkanal wird auf Stereo gemischt";
            }

            _soundEntry.Update(
                problem is null ? text : $"{text}. {problem}",
                plan.IsDownmix || problem is not null ? StatusLevel.Warning : StatusLevel.Ok);
            string modeText = plan.Channels == ChannelHandling.Ambisonics ? $"{plan.Mode!.Describe()}, {mode}" : mode;
            AudioModeText = problem is null ? modeText : $"{modeText} – {problem}";
        }

        AudioOutputStatus? output = status.AudioOutput;
        if (output is null)
        {
            _soundOutputEntry.Update("Keine Tonausgabe", StatusLevel.Error);
        }
        else
        {
            string extras = output.Muted ? ", STUMM" : string.Empty;
            if (output.Spatial is { Active: true } spatial)
            {
                extras = string.Create(culture, $", Raumklang-Last {spatial.PeakLoad:0.#%}") + extras;
            }

            double dropoutMs = output.SampleRate > 0 ? output.UnderrunFrames * 1000.0 / output.SampleRate : 0;
            switch (output.State)
            {
                case AudioEndpointState.Active:
                    _soundOutputEntry.Update(
                        string.Create(
                            culture,
                            $"{output.DeviceName}, {output.SampleRate / 1000.0:0.#} kHz, {output.DeviceChannels} Kanäle, Latenz {output.LatencyMilliseconds:0} ms, Drift {output.DriftPpm:+0;-0;0} ppm, Aussetzer {dropoutMs:0} ms{extras}"),
                        output.UnderrunFrames > 0 || output.Muted ? StatusLevel.Warning : StatusLevel.Ok);
                    break;
                case AudioEndpointState.Unavailable:
                    _soundOutputEntry.Update(output.Message ?? "Nicht verfügbar", StatusLevel.Error);
                    break;
                default:
                    _soundOutputEntry.Update("Startet …", StatusLevel.Neutral);
                    break;
            }
        }

        AvSyncStats sync = status.AvSync;
        string clock = status.ClockSource == PlaybackClockSource.Audio ? "Takt: Ton" : "Takt: Systemuhr";
        if (sync.WindowFrames == 0)
        {
            _avSyncEntry.Update(status.Video is null ? "–" : $"– ({clock})", StatusLevel.Neutral);
        }
        else
        {
            bool inside = sync.WindowMinMs >= -AvSyncMeter.LimitMilliseconds && sync.WindowMaxMs <= AvSyncMeter.LimitMilliseconds;
            _avSyncEntry.Update(
                string.Create(
                    culture,
                    $"{sync.WindowMeanMs:+0.0;-0.0;0.0} ms ({sync.WindowMinMs:+0.0;-0.0;0.0} … {sync.WindowMaxMs:+0.0;-0.0;0.0}), seit Start {sync.TotalOutsideLimit} von {sync.TotalFrames} Bildern außerhalb ±{AvSyncMeter.LimitMilliseconds:0} ms, {clock}"),
                inside && sync.TotalOutsideLimit == 0 ? StatusLevel.Ok : StatusLevel.Warning);
        }
    }

    /// <summary>How the sound is heard right now (M7), and why sound fixed to the picture does not work, if it does not.</summary>
    private static (string Mode, string? Problem) DescribeSoundMode(AudioConversionPlan plan, SpatialAudioStatus? spatial)
    {
        if (plan.Channels == ChannelHandling.MonoOnBoth)
        {
            return ("Mono auf beiden Ohren (ohne Richtung)", null);
        }

        if (plan.Channels == ChannelHandling.Ambisonics)
        {
            // Without the field itself: plan.Describe() names it already.
            bool fixedToPicture = spatial?.Placement.FixedToPicture ?? false;

            // Before Play no field flows yet; a ready stage that can render it says what will be heard, as for stereo.
            if (spatial?.FieldBinaural == true || (spatial is not null && spatial.AmbisonicsProblem is null && spatial.AmbisonicsChannels >= plan.RingChannels))
            {
                return (fixedToPicture ? "binaural, Klangfeld fest am Bild" : "binaural, Klangfeld dreht mit dem Kopf", plan.Mode!.Warning);
            }

            if (spatial?.AmbisonicsProblem is { } problem)
            {
                return ("Notbehelf links/rechts (dreht mit dem Kopf)", problem);
            }

            return ("wird vorbereitet …", plan.Mode!.Warning);
        }

        if (spatial is null || !spatial.Placement.FixedToPicture)
        {
            return ("Stereo direkt (dreht mit dem Kopf)", null);
        }

        string placement = string.Create(CultureInfo.CurrentCulture, $"Ton fest am Bild, Lautsprecher bei ±{spatial.Placement.WidthDegrees:0.#}°");
        if (spatial.Active)
        {
            return (placement, null);
        }

        return spatial.Problem is null
            ? (placement + " (wird vorbereitet …)", null)
            : ("Stereo direkt (dreht mit dem Kopf)", spatial.Problem);
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
        string since = XrEventText.Since(status.StateSinceUtc, DateTime.UtcNow);

        (string headsetText, StatusLevel headsetLevel) = status.State switch
        {
            XrConnectionState.Starting => ("Startet …", StatusLevel.Neutral),
            XrConnectionState.LoaderMissing => ("OpenXR-Loader fehlt (siehe native/README.md)", StatusLevel.Error),
            XrConnectionState.RuntimeUnavailable when status.WasConnected => ($"Brille getrennt{since}: Varjo Base läuft nicht mehr. Varjo Base starten, Orbivo verbindet sich von selbst neu", StatusLevel.Error),
            XrConnectionState.RuntimeUnavailable => ("Kein OpenXR-Runtime erreichbar. Läuft Varjo Base? Neuer Versuch alle 2 s", StatusLevel.Error),
            XrConnectionState.HeadsetUnavailable when status.WasConnected => ($"Brille getrennt{since}. Brille anschließen und einschalten, Orbivo verbindet sich von selbst neu", StatusLevel.Error),
            XrConnectionState.HeadsetUnavailable => ($"{runtimeText}: keine Brille gefunden. Brille anschließen und einschalten", StatusLevel.Warning),
            XrConnectionState.Connecting when status.WasConnected => ($"{runtimeText}: verbinde neu{since} …", StatusLevel.Warning),
            XrConnectionState.Connecting => ($"{runtimeText}: verbinde …", StatusLevel.Neutral),
            XrConnectionState.Connected when runtime is not null => (
                string.Create(culture, $"{runtime.SystemName} über {runtimeText}, {runtime.EyeWidth} × {runtime.EyeHeight} je Auge"),
                StatusLevel.Ok),
            XrConnectionState.Connected => ("Verbunden", StatusLevel.Ok),
            XrConnectionState.Failed => ($"Fehler{since}: {status.Error} (Details im Log). Neuer Versuch läuft", StatusLevel.Error),
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

        ShowRecenter(status.Recenter, waitingText: "wartet auf Kopf-Tracking");
        Idle.ShowSeeThroughSupport(status.SeeThrough switch
        {
            SeeThroughSupport.Available => true,
            SeeThroughSupport.NotAvailable => false,
            _ => null,
        });
    }

    /// <summary>Recenter line and buttons (F30). The yaw is the turn of the video's front, positive = to the left.</summary>
    private void ShowRecenter(RecenterStatus recenter, string waitingText)
    {
        bool changed = recenter != _recenter;
        _recenter = recenter;
        if (!changed)
        {
            return;
        }

        CultureInfo culture = CultureInfo.CurrentCulture;
        if (recenter.Pending)
        {
            _recenterEntry.Update($"Angefordert, {waitingText}", StatusLevel.Warning);
        }
        else if (!recenter.IsActive)
        {
            _recenterEntry.Update("Aus (Vorderseite des Videos)", StatusLevel.Neutral);
        }
        else
        {
            float yaw = recenter.YawDegrees;
            string side = yaw > 0f ? "links" : "rechts";
            _recenterEntry.Update(
                string.Create(culture, $"Vorne liegt {Math.Abs(yaw):0}° {side} der Vorderseite des Videos (Yaw {yaw:+0;-0;0}°)"),
                StatusLevel.Ok);
        }

        RecenterCommand.NotifyCanExecuteChanged();
        ResetRecenterCommand.NotifyCanExecuteChanged();
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

        ShowRecenter(status.Recenter, waitingText: "wartet auf das Vorschaufenster");
    }

    private void ShowLiveViewStatus()
    {
        if (!LiveViewEnabled)
        {
            LiveViewStatusText = "Aus";
            return;
        }

        if (_liveView is not { IsAvailable: true } controller)
        {
            LiveViewStatusText = _noXr ? "Wartet auf die Grafikkarte …" : "Wartet auf die Brille …";
            return;
        }

        LiveViewStatus? status = controller.GetStatus();
        if (status is not { } current || current.State == LiveViewState.Starting)
        {
            LiveViewStatusText = "Startet …";
            return;
        }

        if (current.State == LiveViewState.Failed)
        {
            LiveViewStatusText = $"Fehler: {current.Error} (Details im Log)";
            return;
        }

        double age = current.LastFrameTimestamp == 0
            ? double.MaxValue
            : (Stopwatch.GetTimestamp() - current.LastFrameTimestamp) / (double)Stopwatch.Frequency;
        if (age > 1.0)
        {
            LiveViewStatusText = _noXr
                ? "Kein Bild: Vorschaufenster ist minimiert"
                : "Kein Bild: Die Brille zeigt gerade nichts (Session nicht sichtbar)";
            return;
        }

        LiveViewStatusText = string.Create(CultureInfo.CurrentCulture, $"{current.FramesPerSecond:0} Bilder/s");
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
