using System.Globalization;

namespace Orbivo.Core.Localization;

/// <summary>
/// Language of the running app. It is set once at the start, before any window or thread exists; a change of the
/// language in the operator window takes effect after a restart (docs/DECISIONS.md, M11).
/// </summary>
public static class Loc
{
    /// <summary>
    /// German: recordings (head log, event log) are written in German whatever the UI language is
    /// (docs/DECISIONS.md, M11).
    /// </summary>
    public static CultureInfo RecordingCulture { get; } = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>Makes <paramref name="language"/> the culture of this thread and of every thread started later.</summary>
    public static void Apply(AppLanguage language)
    {
        CultureInfo culture = AppLanguages.Culture(language);
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>The language the texts are shown in right now.</summary>
    public static AppLanguage Current =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == AppLanguages.GermanCode ? AppLanguage.German : AppLanguage.English;

    /// <summary><see cref="string.Format(IFormatProvider, string, object?[])"/> with the current culture.</summary>
    public static string Format(string format, params object?[] args) => string.Format(CultureInfo.CurrentCulture, format, args);
}
