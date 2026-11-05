using CommunityToolkit.Mvvm.ComponentModel;

namespace Orbivo.App.ViewModels;

public enum StatusLevel
{
    Neutral,
    Ok,
    Warning,
    Error,
}

/// <summary>
/// One line in the status panel (label and value are German UI text). Value and level can change at run time.
/// </summary>
public sealed class StatusEntry(string label, string value, StatusLevel level) : ObservableObject
{
    private string _value = value;
    private StatusLevel _level = level;

    public string Label { get; } = label;

    public string Value
    {
        get => _value;
        private set => SetProperty(ref _value, value);
    }

    public StatusLevel Level
    {
        get => _level;
        private set => SetProperty(ref _level, value);
    }

    public void Update(string value, StatusLevel level)
    {
        Value = value;
        Level = level;
    }
}
