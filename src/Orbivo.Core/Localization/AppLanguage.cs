using System.Globalization;

namespace Orbivo.Core.Localization;

/// <summary>
/// Language of the operator window and the manual (docs/DECISIONS.md, M11). The recordings stay German in both, so
/// analysis scripts and sessions stay comparable (<see cref="Loc.RecordingCulture"/>).
/// </summary>
public enum AppLanguage
{
    English,
    German,
}

/// <summary>Language codes as stored in settings.json and given with <c>--lang</c>.</summary>
public static class AppLanguages
{
    public const string EnglishCode = "en";

    public const string GermanCode = "de";

    /// <summary>The language at the very first start (no settings.json yet).</summary>
    public const AppLanguage FirstStart = AppLanguage.English;

    /// <summary>Settings written before M11 have no language; those installations keep German, as before.</summary>
    public const AppLanguage BeforeChoice = AppLanguage.German;

    public static string ToCode(AppLanguage language) => language == AppLanguage.German ? GermanCode : EnglishCode;

    /// <summary>"en" or "de" (any case, also "de-DE"); null for anything else.</summary>
    public static AppLanguage? FromCode(string? code)
    {
        string? primary = code?.Trim().Split('-', '_')[0];
        if (string.Equals(primary, EnglishCode, StringComparison.OrdinalIgnoreCase))
        {
            return AppLanguage.English;
        }

        return string.Equals(primary, GermanCode, StringComparison.OrdinalIgnoreCase) ? AppLanguage.German : null;
    }

    /// <summary>Culture for texts and numbers: en-US (decimal point) or de-DE (decimal comma).</summary>
    public static CultureInfo Culture(AppLanguage language) =>
        CultureInfo.GetCultureInfo(language == AppLanguage.German ? "de-DE" : "en-US");
}
