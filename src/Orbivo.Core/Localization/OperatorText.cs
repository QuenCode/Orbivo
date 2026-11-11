using System.Globalization;

namespace Orbivo.Core.Localization;

/// <summary>
/// A text for the operator, kept as the name of a <see cref="CoreText"/> resource and its arguments. The operator window
/// shows it in the UI language, the event log writes it in German (<see cref="Loc.RecordingCulture"/>) and the
/// diagnostic log in English. Used where one text goes to both, e.g. why a video cannot be played.
/// </summary>
public sealed class OperatorText
{
    private readonly string? _name;
    private readonly string? _literal;
    private readonly object?[] _arguments;

    /// <param name="name">Name of the resource, e.g. <c>nameof(CoreText.FileNotFound)</c>.</param>
    /// <param name="arguments">Values for {0}, {1}, … Nested <see cref="OperatorText"/>s are written in the same language.</param>
    public OperatorText(string name, params object?[] arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _name = name;
        _arguments = arguments ?? [];
    }

    private OperatorText(string literal)
    {
        _literal = literal;
        _arguments = [];
    }

    /// <summary>A text that is the same in every language (e.g. a message from a library).</summary>
    public static OperatorText Literal(string text) => new(text ?? string.Empty);

    /// <summary>The text in <paramref name="culture"/>: its resources and its number format.</summary>
    public string ToString(CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(culture);
        if (_name is null)
        {
            return _literal!;
        }

        string format = CoreText.ResourceManager.GetString(_name, culture) ?? _name;
        if (_arguments.Length == 0)
        {
            return format;
        }

        object?[] arguments = [.. _arguments.Select(argument => argument is OperatorText text ? text.ToString(culture) : argument)];
        return string.Format(culture, format, arguments);
    }

    /// <summary>The text in the UI language.</summary>
    public override string ToString() => ToString(CultureInfo.CurrentUICulture);

    /// <summary>The text in English, for the diagnostic log.</summary>
    public string ForLog() => ToString(CultureInfo.InvariantCulture);

    /// <summary>The text in German, for the event log.</summary>
    public string ForRecording() => ToString(Loc.RecordingCulture);
}
