using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Orbivo.Core.Localization;

namespace Orbivo.Core.Tests.Localization;

/// <summary>
/// Both languages of the operator texts (M11): every resx file in src has a German twin with the same names and the
/// same placeholders, and texts that go to the operator and the event log come out in the right language each.
/// </summary>
public sealed partial class LocalizationTests
{
    public static TheoryData<string> ResxFiles()
    {
        var data = new TheoryData<string>();
        foreach (string path in Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src"), "*.resx", SearchOption.AllDirectories)
                     .Where(path => !path.EndsWith(".de.resx", StringComparison.OrdinalIgnoreCase))
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            data.Add(Path.GetRelativePath(RepositoryRoot(), path));
        }

        return data;
    }

    [Fact]
    public void ThereAreResxFiles()
    {
        Assert.Contains(ResxFiles(), row => row.Data.EndsWith("CoreText.resx", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(ResxFiles))]
    public void GermanTwin_HasTheSameTextsAndPlaceholders(string relativePath)
    {
        string english = Path.Combine(RepositoryRoot(), relativePath);
        string german = Path.ChangeExtension(english, ".de.resx");
        Assert.True(File.Exists(german), $"{relativePath} has no German twin {Path.GetFileName(german)}.");

        Dictionary<string, string> englishTexts = ReadTexts(english);
        Dictionary<string, string> germanTexts = ReadTexts(german);
        Assert.Empty(englishTexts.Keys.Except(germanTexts.Keys));
        Assert.Empty(germanTexts.Keys.Except(englishTexts.Keys));
        foreach ((string name, string text) in englishTexts)
        {
            Assert.False(string.IsNullOrWhiteSpace(text), $"{name} is empty in English.");
            Assert.False(string.IsNullOrWhiteSpace(germanTexts[name]), $"{name} is empty in German.");

            // A line break is a real one in the resx; "\n" as two characters would show up as such in a message box.
            Assert.DoesNotContain(@"\n", text, StringComparison.Ordinal);
            Assert.DoesNotContain(@"\n", germanTexts[name], StringComparison.Ordinal);
            Assert.True(
                Placeholders(text).SetEquals(Placeholders(germanTexts[name])),
                $"{name}: placeholders differ between \"{text}\" and \"{germanTexts[name]}\".");
            _ = string.Format(CultureInfo.InvariantCulture, text, new object[10]);
            _ = string.Format(CultureInfo.InvariantCulture, germanTexts[name], new object[10]);
        }
    }

    [Fact]
    public void OperatorText_IsShownInTheUiLanguage_RecordedInGerman_AndLoggedInEnglish()
    {
        var text = new OperatorText(nameof(CoreText.AudioNoDecoder), "opus");
        CultureInfo before = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("There is no decoder for the sound codec opus", text.ToString());
            Assert.Equal("Für den Ton-Codec opus gibt es keinen Decoder", text.ForRecording());
            Assert.Equal("There is no decoder for the sound codec opus", text.ForLog());

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("Für den Ton-Codec opus gibt es keinen Decoder", text.ToString());
            Assert.Equal("There is no decoder for the sound codec opus", text.ForLog());
        }
        finally
        {
            CultureInfo.CurrentUICulture = before;
        }
    }

    [Fact]
    public void NestedOperatorTexts_UseTheSameLanguage_AndNumbersItsFormat()
    {
        var reason = new OperatorText(nameof(CoreText.HardwareNoProfile), "hevc", 10);
        var text = new OperatorText(nameof(CoreText.SoftwareOnly420), reason, "yuv444p");
        Assert.Equal(
            "Die GPU hat keinen Hardware-Decoder für hevc mit 10 Bit. Software-Dekodierung kann nur 4:2:0 mit 8 oder 10 Bit (dieses Video: yuv444p)",
            text.ForRecording());
        Assert.StartsWith("The GPU has no hardware decoder for hevc with 10 bit. Software decoding", text.ForLog(), StringComparison.Ordinal);

        var width = new OperatorText(nameof(CoreText.SidecarInvalidStereoWidth), "7", 30.0, 90.0);
        Assert.Contains("30–90°", width.ToString(CultureInfo.GetCultureInfo("en-US")), StringComparison.Ordinal);
        Assert.Equal("abc", OperatorText.Literal("abc").ForRecording());
    }

    [Theory]
    [InlineData("en", AppLanguage.English)]
    [InlineData("EN-us", AppLanguage.English)]
    [InlineData("de", AppLanguage.German)]
    [InlineData("de-AT", AppLanguage.German)]
    public void LanguageCodes_AreRead(string code, AppLanguage expected)
    {
        Assert.Equal(expected, AppLanguages.FromCode(code));
        Assert.Equal(code[..2].ToLowerInvariant(), AppLanguages.ToCode(expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fr")]
    [InlineData("deutsch")]
    public void UnknownLanguageCodes_GiveNull(string? code)
    {
        Assert.Null(AppLanguages.FromCode(code));
    }

    [Fact]
    public void Cultures_GiveTheDecimalSeparatorOfTheLanguage()
    {
        Assert.Equal(".", AppLanguages.Culture(AppLanguage.English).NumberFormat.NumberDecimalSeparator);
        Assert.Equal(",", AppLanguages.Culture(AppLanguage.German).NumberFormat.NumberDecimalSeparator);
        Assert.Equal(",", Loc.RecordingCulture.NumberFormat.NumberDecimalSeparator);
    }

    private static Dictionary<string, string> ReadTexts(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .ToDictionary(data => (string)data.Attribute("name")!, data => (string?)data.Element("value") ?? string.Empty);

    private static HashSet<string> Placeholders(string text) =>
        [.. PlaceholderPattern().Matches(text).Select(match => match.Groups[1].Value)];

    private static string RepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "Orbivo.sln")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        return directory ?? throw new InvalidOperationException("Orbivo.sln not found above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderPattern();
}
