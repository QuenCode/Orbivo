namespace Orbivo.App.ViewModels;

/// <summary>An entry of the output device list (F24).</summary>
/// <param name="Id">Windows endpoint id, null = Windows default (follows changes of the default).</param>
/// <param name="Name">Name shown to the operator.</param>
public sealed record AudioDeviceChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}
