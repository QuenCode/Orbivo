namespace Orbivo.App.ViewModels;

public enum StatusLevel
{
    Neutral,
    Ok,
    Warning,
    Error,
}

/// <summary>
/// One line in the status panel (label and value are German UI text).
/// </summary>
public sealed record StatusEntry(string Label, string Value, StatusLevel Level);
