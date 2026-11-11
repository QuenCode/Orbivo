using CommunityToolkit.Mvvm.ComponentModel;
using Orbivo.App.Localization;
using Orbivo.Core.Playback;

namespace Orbivo.App.ViewModels;

/// <summary>One entry of the idle view combo box.</summary>
public sealed record IdleViewChoice(IdleView Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The "Brille im Leerlauf" group of the operator window (F7): black, see-through or a still image while no video runs.
/// The app applies every change to the render thread (<see cref="Changed"/>) and keeps it in the settings; a still
/// image from the command line (<c>--image</c>) applies to this session only, until the operator changes the choice.
/// </summary>
public sealed class IdleViewModel : ObservableObject
{
    private IdleViewChoice _selected;
    private string? _imagePath;
    private string _imageText = UiText.IdleNoImage;
    private StatusLevel _imageLevel = StatusLevel.Neutral;
    private string _seeThroughText = string.Empty;
    private IdleView _savedView;
    private string? _savedImagePath;

    public IdleViewModel(IdleView view, string? imagePath, bool noXr)
    {
        Choices =
        [
            new IdleViewChoice(IdleView.Black, UiText.IdleNameBlack),
            new IdleViewChoice(IdleView.SeeThrough, UiText.IdleNameSeeThrough),
            new IdleViewChoice(IdleView.Image, UiText.IdleNameImage),
        ];
        _selected = Choices.First(choice => choice.Value == view);
        _imagePath = imagePath;
        _savedView = view;
        _savedImagePath = imagePath;
        _seeThroughText = noXr ? UiText.SeeThroughPreview : UiText.SeeThroughNotChecked;
    }

    /// <summary>Idle view or image changed; the app applies it (render thread, image loading, event log).</summary>
    public event EventHandler? Changed;

    /// <summary>The operator changed the choice; the app saves the settings.</summary>
    public event EventHandler? SettingsChanged;

    public IReadOnlyList<IdleViewChoice> Choices { get; }

    public IdleView View => _selected.Value;

    public IdleViewChoice Selected
    {
        get => _selected;
        set
        {
            if (value is null || !SetProperty(ref _selected, value))
            {
                return;
            }

            OnPropertyChanged(nameof(View));
            OnPropertyChanged(nameof(ShowsImageRow));
            _savedView = value.Value;
            _savedImagePath = _imagePath;
            Changed?.Invoke(this, EventArgs.Empty);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Full path of the still image, or null.</summary>
    public string? ImagePath => _imagePath;

    public bool ShowsImageRow => _selected.Value == IdleView.Image || _imagePath is not null;

    /// <summary>File name, size and layout of the image, or why it could not be loaded.</summary>
    public string ImageText
    {
        get => _imageText;
        private set => SetProperty(ref _imageText, value);
    }

    public StatusLevel ImageLevel
    {
        get => _imageLevel;
        private set => SetProperty(ref _imageLevel, value);
    }

    /// <summary>Whether the headset can show the camera picture.</summary>
    public string SeeThroughText
    {
        get => _seeThroughText;
        private set => SetProperty(ref _seeThroughText, value);
    }

    /// <summary>The choices to keep in the settings (a session image from the command line is not kept).</summary>
    public (IdleView View, string? ImagePath) SavedChoice => (_savedView, _savedImagePath);

    /// <summary>The operator picked an image file: it becomes the idle image and the idle view switches to it.</summary>
    public void ChooseImage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _imagePath = path;
        _savedImagePath = path;
        ImageText = UiText.Loading;
        ImageLevel = StatusLevel.Neutral;
        OnPropertyChanged(nameof(ImagePath));
        OnPropertyChanged(nameof(ShowsImageRow));
        if (_selected.Value != IdleView.Image)
        {
            // Raises Changed and SettingsChanged.
            Selected = Choices.First(choice => choice.Value == IdleView.Image);
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary><c>--image</c>: shows this image while idle, for this session only (the settings stay as they were).</summary>
    public void UseSessionImage(string path)
    {
        _imagePath = path;
        _selected = Choices.First(choice => choice.Value == IdleView.Image);
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(View));
        OnPropertyChanged(nameof(ImagePath));
        OnPropertyChanged(nameof(ShowsImageRow));
    }

    public void ShowImageLoaded(string text)
    {
        ImageText = text;
        ImageLevel = StatusLevel.Ok;
    }

    public void ShowImageFailed(string text)
    {
        ImageText = text;
        ImageLevel = StatusLevel.Error;
    }

    /// <summary>From the headset status: the runtime offers the camera picture or not.</summary>
    public void ShowSeeThroughSupport(bool? available)
    {
        SeeThroughText = available switch
        {
            true => UiText.SeeThroughAvailable,
            false => UiText.SeeThroughMissing,
            null => SeeThroughText,
        };
    }
}
