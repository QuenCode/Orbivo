using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Microsoft.Win32;
using Orbivo.App.ViewModels;
using Orbivo.Core.Playlists;

namespace Orbivo.App;

/// <summary>
/// The operator window. Only view concerns live here: file dialog, drag and drop, keyboard shortcuts (F60) and
/// timeline dragging. Everything else goes through <see cref="MainViewModel"/>.
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // The live view presenter draws into the child window from its own thread: it starts once the window exists
        // and stops before the window goes away.
        LiveViewPanel.WindowCreated += (_, _) => ViewModel.SetLiveViewWindow(LiveViewPanel.WindowHandle);
        LiveViewPanel.WindowDestroying += (_, _) => ViewModel.SetLiveViewWindow(0);
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        string extensions = string.Join(";", Playlist.VideoExtensions.Select(extension => "*" + extension));
        var dialog = new OpenFileDialog
        {
            Title = "Videos zur Playlist hinzufügen",
            Filter = $"Videos ({extensions})|{extensions}|Alle Dateien (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            // Files picked explicitly are added even with an unknown extension.
            ViewModel.AddFiles(dialog.FileNames, explicitFiles: true);
        }
    }

    private void OnChooseIdleImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Standbild für den Leerlauf wählen (Equirect 360°)",
            Filter = "Bilder (*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff|Alle Dateien (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.Idle.ChooseImage(dialog.FileName);
        }
    }

    private void OnChooseRecordingFolderClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Ordner für die Aufzeichnungen wählen",
            InitialDirectory = Directory.Exists(ViewModel.Recording.Folder) ? ViewModel.Recording.Folder : string.Empty,
        };
        if (dialog.ShowDialog(this) == true)
        {
            ViewModel.Recording.ChooseFolder(dialog.FolderName);
        }
    }

    private void OnPlaylistDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPlaylistDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            ViewModel.AddFiles(paths);
        }
    }

    private void OnPlaylistDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source
            && ItemsControl.ContainerFromElement(PlaylistBox, source) is ListBoxItem
            && ViewModel.LoadSelectedCommand.CanExecute(null))
        {
            ViewModel.LoadSelectedCommand.Execute(null);
        }
    }

    private void OnPlaylistKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && ViewModel.RemoveSelectedCommand.CanExecute(null))
        {
            ViewModel.RemoveSelectedCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && ViewModel.LoadSelectedCommand.CanExecute(null))
        {
            ViewModel.LoadSelectedCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>A format number field (offset, fisheye) is taken when it loses the focus, as with Enter.</summary>
    private void OnFormatFieldLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is TextBox { Tag: ICommand command } && command.CanExecute(null))
        {
            command.Execute(null);
        }
    }

    private void OnTimelineDragStarted(object sender, DragStartedEventArgs e)
    {
        ViewModel.SetScrubbing(true);
    }

    private void OnTimelineDragCompleted(object sender, DragCompletedEventArgs e)
    {
        ViewModel.SetScrubbing(false);
    }

    /// <summary>
    /// Keyboard shortcuts for the whole window (F60). They are off while a text field has the focus, so typing a time
    /// or a fade duration works normally. Handled keys do not reach the focused button or list.
    /// </summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            return;
        }

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        MainViewModel viewModel = ViewModel;
        (ICommand? command, object? parameter) = e.Key switch
        {
            Key.Space => (viewModel.PlayPauseCommand, null),
            Key.Left => (viewModel.SeekRelativeCommand, shift ? "-30" : "-5"),
            Key.Right => (viewModel.SeekRelativeCommand, shift ? "30" : "5"),
            Key.OemComma => (viewModel.StepFrameCommand, "-1"),
            Key.OemPeriod => (viewModel.StepFrameCommand, "1"),
            Key.PageUp => (viewModel.PreviousCommand, null),
            Key.PageDown => (viewModel.NextCommand, null),
            Key.B => (viewModel.ToggleBlackoutCommand, null),
            Key.F => (viewModel.ToggleFadeCommand, null),
            Key.M => (viewModel.ToggleMuteCommand, null),
            Key.R => (shift ? viewModel.ResetRecenterCommand : viewModel.RecenterCommand, null),
            _ => ((ICommand?)null, (object?)null),
        };

        if (command is null)
        {
            return;
        }

        e.Handled = true;
        if (command.CanExecute(parameter))
        {
            command.Execute(parameter);
        }
    }
}
