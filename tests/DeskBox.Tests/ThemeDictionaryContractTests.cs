using System.Text.RegularExpressions;

namespace DeskBox.Tests;

/// <summary>
/// App-level theme dictionaries must stay closed across all four theme keys.
/// RequestedTheme is ignored by the framework under high contrast (official
/// FrameworkElement.RequestedTheme documentation), so widget windows and the
/// search popup resolve their custom keys against HighContrast/Default when
/// the system runs a contrast theme — with only Light/Dark authored, those
/// lookups failed to resolve at XAML parse time. Keep the key sets identical
/// across Light/Dark/Default/HighContrast, and keep the HighContrast brushes
/// system-owned (SystemColor* pairs, no hardcoded palette) per the
/// contrast-themes guidance, mirroring the WidgetGroupTitleSwitcher TabView
/// sample.
/// </summary>
public sealed class ThemeDictionaryContractTests
{
    private const string AppXamlPath = "src/DeskBox/App.xaml";
    private const string FeedbackPresenterPath = "src/DeskBox/Controls/WidgetFeedbackPresenter.xaml";

    [Fact]
    public void AppThemeDictionaries_CoverAllFourThemesWithIdenticalKeys()
    {
        var dictionaries = ExtractThemeDictionaries(ReadRepositoryFile(AppXamlPath));

        Assert.True(dictionaries.ContainsKey("Light"), "App.xaml must author a Light theme dictionary.");
        Assert.True(dictionaries.ContainsKey("Dark"), "App.xaml must author a Dark theme dictionary.");
        Assert.True(dictionaries.ContainsKey("Default"), "App.xaml must author a Default fallback dictionary.");
        Assert.True(dictionaries.ContainsKey("HighContrast"), "App.xaml must author a HighContrast dictionary: under high contrast RequestedTheme is ignored and these keys resolve against it.");

        AssertIdenticalKeySets(dictionaries, AppXamlPath);
    }

    [Fact]
    public void AppHighContrastBrushes_AreSystemOwned()
    {
        var dictionaries = ExtractThemeDictionaries(ReadRepositoryFile(AppXamlPath));
        string highContrast = dictionaries["HighContrast"];

        foreach (var brush in ExtractBrushEntries(highContrast))
        {
            // Transparent is the mapping for decorative washes; every other
            // brush must reference the system contrast palette instead of a
            // hardcoded color that can clash with the user's theme.
            Assert.True(brush.Value.Contains("SystemColor", StringComparison.Ordinal) ||
                        brush.Value.Contains("Transparent", StringComparison.Ordinal),
                $"App.xaml HighContrast brush '{brush.Key}' must map to a SystemColor* resource or Transparent, found '{brush.Value}'.");
        }
    }

    [Fact]
    public void FeedbackPresenterThemeDictionaries_CoverAllFourThemes()
    {
        var dictionaries = ExtractThemeDictionaries(ReadRepositoryFile(FeedbackPresenterPath));

        Assert.True(dictionaries.ContainsKey("Light"), "WidgetFeedbackPresenter must author a Light theme dictionary.");
        Assert.True(dictionaries.ContainsKey("Dark"), "WidgetFeedbackPresenter must author a Dark theme dictionary.");
        Assert.True(dictionaries.ContainsKey("Default"), "WidgetFeedbackPresenter must author a Default fallback dictionary.");
        Assert.True(dictionaries.ContainsKey("HighContrast"),
            "WidgetFeedbackPresenter must author a HighContrast dictionary: the all-white Default (Dark mirror) is unreadable on the Desert-style light contrast plate.");

        AssertIdenticalKeySets(dictionaries, FeedbackPresenterPath);
        Assert.Contains("SystemColorWindowTextColor", dictionaries["HighContrast"], StringComparison.Ordinal);
    }

    private static Dictionary<string, string> ExtractThemeDictionaries(string xaml)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var themeSection = xaml.Substring(xaml.IndexOf("<ResourceDictionary.ThemeDictionaries>", StringComparison.Ordinal));
        themeSection = themeSection.Substring(0, themeSection.IndexOf("</ResourceDictionary.ThemeDictionaries>", StringComparison.Ordinal));

        foreach (Match match in Regex.Matches(
                     themeSection,
                     @"<ResourceDictionary x:Key=""(\w+)"">([\s\S]*?)</ResourceDictionary>"))
        {
            result[match.Groups[1].Value] = match.Groups[2].Value;
        }

        return result;
    }

    private static IEnumerable<KeyValuePair<string, string>> ExtractBrushEntries(string dictionaryContent)
    {
        foreach (Match match in Regex.Matches(
                     dictionaryContent,
                     @"<SolidColorBrush x:Key=""(\w+)"" Color=""([^""]+)"""))
        {
            yield return new KeyValuePair<string, string>(match.Groups[1].Value, match.Groups[2].Value);
        }
    }

    private static void AssertIdenticalKeySets(Dictionary<string, string> dictionaries, string path)
    {
        var reference = ExtractKeys(dictionaries["Light"]).Order().ToArray();
        foreach (var (theme, content) in dictionaries)
        {
            var keys = ExtractKeys(content).Order().ToArray();
            Assert.True(keys.SequenceEqual(reference),
                $"{path}: theme dictionary '{theme}' key set differs from Light ({keys.Length} vs {reference.Length} keys). " +
                "All theme dictionaries must carry the same keys so ThemeResource resolution cannot miss under any theme.");
        }
    }

    private static IEnumerable<string> ExtractKeys(string dictionaryContent)
    {
        return Regex.Matches(dictionaryContent, @"x:Key=""(\w+)""")
            .Select(m => m.Groups[1].Value);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
