using Orbivo.Core.Localization;

namespace Orbivo.App.ViewModels;

/// <summary>An entry of the language list (M11), named in its own language so everyone finds theirs.</summary>
public sealed record LanguageChoice(AppLanguage Language, string Name)
{
    public static IReadOnlyList<LanguageChoice> All { get; } =
    [
        new(AppLanguage.English, "English"),
        new(AppLanguage.German, "Deutsch"),
    ];

    public override string ToString() => Name;
}
