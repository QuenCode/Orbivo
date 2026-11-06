using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbivo.Core.Commands;
using Orbivo.Core.Formats;
using Orbivo.Core.Geometry;
using Orbivo.Core.Status;

namespace Orbivo.App.ViewModels;

/// <summary>One entry of a format combo box; <see cref="Value"/> null = automatic.</summary>
public sealed class FormatChoice(object? value, string label) : ObservableObject
{
    private string _label = label;

    public object? Value { get; } = value;

    public string Label
    {
        get => _label;
        set => SetProperty(ref _label, value);
    }

    public override string ToString() => Label;
}

/// <summary>
/// The "Format" group of the operator window (F10–F14): projection, stereo layout, eye swap, cube layout, fisheye
/// circle and orientation offset of the loaded video. Every change goes to the player as a
/// <see cref="SetVideoFormatCommand"/>, which shows it at once and stores it in the video's sidecar.
/// </summary>
/// <remarks>
/// The view model keeps its own copy of the choices while the same file is loaded, so quick changes in a row never
/// build on a status that is not back yet. Text fields are only refreshed when their value changed in the player,
/// so a status update never overwrites what the operator is typing.
/// </remarks>
public sealed class FormatViewModel : ObservableObject
{
    private readonly Action<PlayerCommand> _send;
    private readonly FormatChoice _autoProjection = new(null, "Automatisch");
    private readonly FormatChoice _autoLayout = new(null, "Automatisch");
    private readonly FormatChoice _autoCubemap = new(null, "Automatisch");
    private VideoFormatStatus? _status;
    private string? _sidecarPath;
    private FormatOverrides _overrides = FormatOverrides.None;
    private bool _updating;
    private FormatChoice? _selectedProjection;
    private FormatChoice? _selectedLayout;
    private FormatChoice? _selectedCubemap;
    private FormatChoice? _customCubemap;
    private OrientationOffset? _shownOffset;
    private FisheyeParameters? _shownFisheye;
    private string _yawText = "0";
    private string _pitchText = "0";
    private string _rollText = "0";
    private string _fovText = string.Empty;
    private string _centerXText = string.Empty;
    private string _centerYText = string.Empty;
    private string _radiusText = string.Empty;
    private string _inputError = string.Empty;
    private string _metadataText = "–";
    private string _sidecarText = "–";
    private string _warningText = string.Empty;

    public FormatViewModel(Action<PlayerCommand> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        _send = send;
        Projections = [_autoProjection, .. Enum.GetValues<ProjectionType>().Select(p => new FormatChoice(p, VideoFormatResolver.Describe(p)))];
        Layouts = [_autoLayout, .. Enum.GetValues<StereoLayout>().Select(l => new FormatChoice(l, VideoFormatResolver.Describe(l)))];
        CubemapLayouts = [_autoCubemap, .. CubemapLayout.Presets.Select(p => new FormatChoice(p.Layout, DescribeCubemap(p.Layout)))];
        ApplyOffsetCommand = new RelayCommand(ApplyOffset, () => IsAvailable);
        ResetOffsetCommand = new RelayCommand(() => Change(_overrides with { Offset = null }, refreshOffset: true), () => IsAvailable);
        ApplyFisheyeCommand = new RelayCommand(ApplyFisheye, () => IsAvailable);
        ResetFisheyeCommand = new RelayCommand(() => Change(_overrides with { Fisheye = null }, refreshFisheye: true), () => IsAvailable);
        ResetAllCommand = new RelayCommand(
            () => Change(FormatOverrides.None, refreshOffset: true, refreshFisheye: true),
            () => IsAvailable && !_overrides.IsEmpty);
    }

    public ObservableCollection<FormatChoice> Projections { get; }

    public ObservableCollection<FormatChoice> Layouts { get; }

    public ObservableCollection<FormatChoice> CubemapLayouts { get; }

    /// <summary>True once a video is open and its format known.</summary>
    public bool IsAvailable => _status is not null;

    /// <summary>False if <c>--stereo</c> fixes the layout for this session.</summary>
    public bool CanChooseLayout => IsAvailable && _status!.Sources.Layout != FormatSource.CommandLine;

    public bool ShowsCubemap => _status?.Format.Projection is ProjectionType.Cubemap or ProjectionType.Eac;

    public bool ShowsFisheye => _status?.Format.Projection == ProjectionType.Fisheye;

    public bool CanSwapEyes => IsAvailable && _status!.Format.Layout != StereoLayout.Mono;

    public FormatChoice? SelectedProjection
    {
        get => _selectedProjection;
        set
        {
            if (SetProperty(ref _selectedProjection, value) && !_updating && value is not null)
            {
                Change(_overrides with { Projection = (ProjectionType?)value.Value });
            }
        }
    }

    public FormatChoice? SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (SetProperty(ref _selectedLayout, value) && !_updating && value is not null)
            {
                Change(_overrides with { Layout = (StereoLayout?)value.Value });
            }
        }
    }

    public FormatChoice? SelectedCubemap
    {
        get => _selectedCubemap;
        set
        {
            if (SetProperty(ref _selectedCubemap, value) && !_updating && value is not null)
            {
                Change(_overrides with { Cubemap = (CubemapLayout?)value.Value });
            }
        }
    }

    /// <summary>Augen tauschen (F11). Choosing what the metadata says makes it automatic again.</summary>
    public bool SwapEyes
    {
        get => _status?.Format.SwapEyes ?? false;
        set
        {
            if (_updating || _status is null || value == SwapEyes)
            {
                return;
            }

            Change(_overrides with { SwapEyes = value == _status.Automatic.SwapEyes ? null : value });
            OnPropertyChanged();
        }
    }

    public string YawText
    {
        get => _yawText;
        set => SetProperty(ref _yawText, value);
    }

    public string PitchText
    {
        get => _pitchText;
        set => SetProperty(ref _pitchText, value);
    }

    public string RollText
    {
        get => _rollText;
        set => SetProperty(ref _rollText, value);
    }

    public string FovText
    {
        get => _fovText;
        set => SetProperty(ref _fovText, value);
    }

    public string CenterXText
    {
        get => _centerXText;
        set => SetProperty(ref _centerXText, value);
    }

    public string CenterYText
    {
        get => _centerYText;
        set => SetProperty(ref _centerYText, value);
    }

    public string RadiusText
    {
        get => _radiusText;
        set => SetProperty(ref _radiusText, value);
    }

    /// <summary>Why the last typed value was not taken, or empty.</summary>
    public string InputError
    {
        get => _inputError;
        private set => SetProperty(ref _inputError, value);
    }

    public string MetadataText
    {
        get => _metadataText;
        private set => SetProperty(ref _metadataText, value);
    }

    public string SidecarText
    {
        get => _sidecarText;
        private set => SetProperty(ref _sidecarText, value);
    }

    /// <summary>Hints about the format (German), one per line, or empty.</summary>
    public string WarningText
    {
        get => _warningText;
        private set => SetProperty(ref _warningText, value);
    }

    public IRelayCommand ApplyOffsetCommand { get; }

    public IRelayCommand ResetOffsetCommand { get; }

    public IRelayCommand ApplyFisheyeCommand { get; }

    public IRelayCommand ResetFisheyeCommand { get; }

    public IRelayCommand ResetAllCommand { get; }

    /// <summary>Takes a new player status (UI thread).</summary>
    public void Update(VideoFormatStatus? status)
    {
        if (ReferenceEquals(status, _status))
        {
            return;
        }

        bool newFile = status is not null && !string.Equals(status.SidecarPath, _sidecarPath, StringComparison.OrdinalIgnoreCase);
        _status = status;
        if (status is null)
        {
            MetadataText = "–";
            SidecarText = "–";
            WarningText = string.Empty;
            RaiseAll();
            return;
        }

        if (newFile)
        {
            // The player read the sidecar of a newly loaded file.
            _sidecarPath = status.SidecarPath;
            _overrides = status.Overrides;
            _shownOffset = null;
            _shownFisheye = null;
            InputError = string.Empty;
        }

        _updating = true;
        try
        {
            ShowChoices(status);
        }
        finally
        {
            _updating = false;
        }

        MetadataText = status.Metadata == "none" ? "keine (sv3d/st3d fehlen)" : status.Metadata;
        string sidecar = status.SidecarExists ? $"gespeichert in {Path.GetFileName(status.SidecarPath)}" : "keins (alles automatisch)";
        SidecarText = status.SidecarError is null ? sidecar : status.SidecarError;
        WarningText = string.Join(Environment.NewLine, status.Warnings);
        RaiseAll();
    }

    private void ShowChoices(VideoFormatStatus status)
    {
        VideoFormat automatic = status.Automatic;
        _autoProjection.Label = $"Automatisch ({VideoFormatResolver.Describe(automatic.Projection)}, {DescribeSource(status.AutomaticSources.Projection)})";
        _autoLayout.Label = status.Sources.Layout == FormatSource.CommandLine
            ? $"Kommandozeile ({VideoFormatResolver.Describe(automatic.Layout)})"
            : $"Automatisch ({VideoFormatResolver.Describe(automatic.Layout)}, {DescribeSource(status.AutomaticSources.Layout)})";
        _autoCubemap.Label = $"Automatisch ({DescribeCubemap(automatic.Cubemap)})";

        SelectedProjection = Projections.First(c => Equals(c.Value, _overrides.Projection));
        SelectedLayout = status.Sources.Layout == FormatSource.CommandLine
            ? _autoLayout
            : Layouts.First(c => Equals(c.Value, _overrides.Layout));
        SelectedCubemap = FindCubemap(_overrides.Cubemap);

        OrientationOffset offset = status.Format.Offset;
        if (_shownOffset != offset)
        {
            _shownOffset = offset;
            YawText = FormatNumber(offset.YawDegrees);
            PitchText = FormatNumber(offset.PitchDegrees);
            RollText = FormatNumber(offset.RollDegrees);
        }

        FisheyeParameters fisheye = status.Format.Fisheye;
        if (_shownFisheye != fisheye)
        {
            _shownFisheye = fisheye;
            FovText = FormatNumber(fisheye.FovDegrees);
            CenterXText = FormatNumber(fisheye.CenterX);
            CenterYText = FormatNumber(fisheye.CenterY);
            RadiusText = FormatNumber(fisheye.Radius);
        }
    }

    private FormatChoice FindCubemap(CubemapLayout? layout)
    {
        FormatChoice? preset = CubemapLayouts.FirstOrDefault(c => c != _customCubemap && Equals(c.Value, layout));
        if (preset is not null)
        {
            return preset;
        }

        // A layout from the sidecar that is no preset (other order, rotation or padding).
        if (_customCubemap is not null)
        {
            CubemapLayouts.Remove(_customCubemap);
        }

        _customCubemap = new FormatChoice(layout, $"Aus dem Sidecar: {layout}");
        CubemapLayouts.Add(_customCubemap);
        return _customCubemap;
    }

    private static string DescribeSource(FormatSource source)
    {
        return source switch
        {
            FormatSource.Metadata => "Metadaten",
            FormatSource.Guess => "aus der Größe geschätzt",
            FormatSource.CommandLine => "Kommandozeile",
            _ => "Standard",
        };
    }

    private static string DescribeCubemap(CubemapLayout layout)
    {
        string name = layout.PresetName switch
        {
            "3x2" => "3 × 2",
            "6x1" => "6 × 1",
            "1x6" => "1 × 6",
            "eac" => "EAC (YouTube)",
            _ => layout.ToString(),
        };
        return layout.PaddingPixels > 0 && layout.PresetName is not null ? $"{name}, Rand {layout.PaddingPixels} px" : name;
    }

    private void ApplyOffset()
    {
        if (!TryParse(YawText, -OrientationOffset.MaxDegrees, OrientationOffset.MaxDegrees, out double yaw)
            || !TryParse(PitchText, -OrientationOffset.MaxDegrees, OrientationOffset.MaxDegrees, out double pitch)
            || !TryParse(RollText, -OrientationOffset.MaxDegrees, OrientationOffset.MaxDegrees, out double roll))
        {
            InputError = $"Ausrichtung: Zahlen von −{OrientationOffset.MaxDegrees:0} bis {OrientationOffset.MaxDegrees:0} Grad eingeben.";
            return;
        }

        InputError = string.Empty;
        var offset = new OrientationOffset(yaw, pitch, roll);
        if (offset != (_overrides.Offset ?? OrientationOffset.Zero))
        {
            Change(_overrides with { Offset = offset.IsZero ? null : offset });
        }
    }

    private void ApplyFisheye()
    {
        if (!TryParse(FovText, FisheyeParameters.MinFovDegrees, FisheyeParameters.MaxFovDegrees, out double fov)
            || !TryParse(CenterXText, 0, 1, out double centerX)
            || !TryParse(CenterYText, 0, 1, out double centerY)
            || !TryParse(RadiusText, FisheyeParameters.MinRadius, FisheyeParameters.MaxRadius, out double radius))
        {
            InputError = string.Create(
                CultureInfo.CurrentCulture,
                $"Fisheye: Sichtfeld {FisheyeParameters.MinFovDegrees:0}–{FisheyeParameters.MaxFovDegrees:0}°, Mitte 0–1, Radius {FisheyeParameters.MinRadius}–{FisheyeParameters.MaxRadius}.");
            return;
        }

        InputError = string.Empty;
        var fisheye = new FisheyeParameters(fov, centerX, centerY, radius);
        if (fisheye != (_overrides.Fisheye ?? FisheyeParameters.Default))
        {
            Change(_overrides with { Fisheye = fisheye == FisheyeParameters.Default ? null : fisheye });
        }
    }

    private void Change(FormatOverrides overrides, bool refreshOffset = false, bool refreshFisheye = false)
    {
        if (_status is null)
        {
            return;
        }

        if (refreshOffset)
        {
            _shownOffset = null;
            InputError = string.Empty;
        }

        if (refreshFisheye)
        {
            _shownFisheye = null;
            InputError = string.Empty;
        }

        _overrides = overrides;
        _send(new SetVideoFormatCommand(overrides));
        ResetAllCommand.NotifyCanExecuteChanged();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(CanChooseLayout));
        OnPropertyChanged(nameof(ShowsCubemap));
        OnPropertyChanged(nameof(ShowsFisheye));
        OnPropertyChanged(nameof(CanSwapEyes));
        OnPropertyChanged(nameof(SwapEyes));
        ApplyOffsetCommand.NotifyCanExecuteChanged();
        ResetOffsetCommand.NotifyCanExecuteChanged();
        ApplyFisheyeCommand.NotifyCanExecuteChanged();
        ResetFisheyeCommand.NotifyCanExecuteChanged();
        ResetAllCommand.NotifyCanExecuteChanged();
    }

    private static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);

    /// <summary>Accepts the operator's culture (German comma) and a point.</summary>
    private static bool TryParse(string text, double min, double max, out double value)
    {
        string trimmed = text.Trim().Replace('−', '-');
        bool parsed = double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value)
            || double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        if (string.IsNullOrEmpty(trimmed))
        {
            value = 0;
            parsed = min <= 0;
        }

        return parsed && double.IsFinite(value) && value >= min && value <= max;
    }
}
