using CommunityToolkit.Mvvm.ComponentModel;
using Orbivo.Core.Status;

namespace Orbivo.App.ViewModels;

/// <summary>
/// View model of the operator main window. In M0 it only shows startup information;
/// playlist, transport and format controls are placeholders.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
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
        entries.AddRange(nativeComponents.Select(component => new StatusEntry(
            component.Component,
            component.Detail,
            component.IsAvailable ? StatusLevel.Ok : StatusLevel.Error)));
        entries.Add(new StatusEntry("Log-Datei", logFilePath, StatusLevel.Neutral));

        StatusEntries = entries;
    }

    public string Title { get; }

    public IReadOnlyList<StatusEntry> StatusEntries { get; }
}
