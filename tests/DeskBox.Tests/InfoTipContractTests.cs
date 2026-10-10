namespace DeskBox.Tests;

/// <summary>
/// Contracts for the InfoTip explanation-flyout pattern (long explanatory
/// copy behind a small ghost button):
/// 1. One shared Flyout instance serves every anchor — per-topic popups
///    would grow the deferred-created settings sections' resident cost,
///    and a TeachingTip alternative pins whole pages in memory
///    (microsoft-ui-xaml #2849).
/// 2. Text resolves from localization keys at open time, so language
///    switches need no re-registration and late-created sections stay cheap.
/// 3. XamlRoot is taken from the anchor on every show — anchors can live in
///    different windows across the app lifetime.
/// 4. Anchors must not carry Localized.HeaderKey/DescriptionKey: those feed
///    the settings search catalog and the AOT frozen-count contracts, which
///    this pattern must leave untouched.
/// </summary>
public sealed class InfoTipContractTests
{
    [Fact]
    public void InfoTipService_UsesOneSharedFlyoutAndResolvesTextAtOpen()
    {
        string code = ReadRepositoryFile("src/DeskBox/Services/InfoTip.cs");

        Assert.Contains("private static Flyout? s_sharedFlyout;", code, StringComparison.Ordinal);
        Assert.Contains("EnsureSharedFlyout()", code, StringComparison.Ordinal);
        // Shared instance is returned, never recreated once built.
        Assert.Contains("if (s_sharedFlyout is { } existing)", code, StringComparison.Ordinal);
        // Open-time resolution through the sanctioned localization facade
        // (never App.Current directly — module-boundary ratchet).
        Assert.Contains("Localized.T(titleKey!)", code, StringComparison.Ordinal);
        Assert.Contains("Localized.T(bodyKey!)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("App.Current", code, StringComparison.Ordinal);
        // Readability contract: bodies render as newline-separated paragraphs
        // with block line height, and **bold** spans become bold runs.
        Assert.Contains("FillBody", code, StringComparison.Ordinal);
        Assert.Contains("Split('\\n')", code.Replace("\\\\", "\\"), StringComparison.Ordinal);
        Assert.Contains("AppendMarkdownLiteInlines", code, StringComparison.Ordinal);
        Assert.Contains("LineStackingStrategy.BlockLineHeight", code, StringComparison.Ordinal);
        // Header-side anchors: card-attached keys compose the header via
        // Localized.SetLocalizedHeader; the badge button is manufactured in
        // code. The Segoe Fluent "?" glyph (E897) renders pixel-hinted
        // inside the style's translucent neutral circle — bare-glyph XAML
        // anchors stay rejected (see the anchor test below).
        Assert.Contains("TryCreateHeaderContent", code, StringComparison.Ordinal);
        Assert.Contains("CreateAnchorButton", code, StringComparison.Ordinal);
        Assert.Contains("Glyph = \"\\uE897\"", code, StringComparison.Ordinal);

        // XamlRoot follows the anchor on every show.
        Assert.Contains("flyout.XamlRoot = button.XamlRoot;", code, StringComparison.Ordinal);
        Assert.Contains("flyout.ShowAt(button);", code, StringComparison.Ordinal);
        // Ghost anchors get a localized accessible name.
        Assert.Contains("AutomationProperties.SetName", code, StringComparison.Ordinal);
    }

    [Fact]
    public void AppDefinesTheGhostAnchorButtonStyle()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml");

        Assert.Contains("InfoTipButtonStyle", app, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/Views/SettingsSections/DisplaySettingsSection.xaml")]
    [InlineData("src/DeskBox/Views/SettingsWindow.xaml")]
    public void InfoTipAnchors_AreWiredWithoutLocalizedHeaderKeys(string path)
    {
        string xaml = ReadRepositoryFile(path);

        Assert.Contains("svc:InfoTip.TitleKey=", xaml, StringComparison.Ordinal);
        Assert.Contains("svc:InfoTip.BodyKey=", xaml, StringComparison.Ordinal);
        // Card-side anchors: the keys attach to the card and the badge is
        // manufactured in code (CreateAnchorButton picks up App.xaml's
        // InfoTipButtonStyle), so the section XAML holds no direct
        // InfoTipButtonStyle references anymore.
        Assert.DoesNotContain("InfoTipButtonStyle", xaml, StringComparison.Ordinal);
        // The anchor is a plain text "?" label: font-icon routes failed
        // (MDL2/Fluent E897 are narrow bare "?" outlines, not circled;
        // E946 circled-"i" read poorly at this size) and a hand-baked
        // fontTools path never rasterized in WinUI 3. Each anchor picks a
        // FontSize matching its context, so sizes intentionally differ.
        // (The "?" badge is manufactured in code by InfoTip.CreateAnchorButton —
        // card-side anchors carry keys only, so XAML holds no literal Text="?".)
        Assert.DoesNotContain("Glyph=\"&#xE946;\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Glyph=\"&#xE897;\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ScaleY=\"-0.0078125\"", xaml, StringComparison.Ordinal);

        // The anchor Button elements must stay out of the HeaderKey/
        // DescriptionKey economies (search catalog + 4D1B frozen counts).
        foreach (string line in xaml.Split('\n'))
        {
            if (!line.Contains("svc:InfoTip.TitleKey", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.False(
                line.Contains("svc:Localized.HeaderKey", StringComparison.Ordinal) ||
                line.Contains("svc:Localized.DescriptionKey", StringComparison.Ordinal),
                $"{path}: InfoTip anchors must not carry Localized Header/Description keys.");
        }
    }

    [Fact]
    public void InfoTipKeysExistInEveryLocale()
    {
        string[] required =
        [
            "Common.Help",
            "Settings.Displays.BindingInfoTip.Title",
            "Settings.Displays.BindingInfoTip.Description",
            "Settings.WidgetLayer.InfoTip.Title",
            "Settings.WidgetLayer.InfoTip.Description",
        ];

        foreach (string locale in EnumerateLocales())
        {
            string json = ReadRepositoryFile($"src/DeskBox/Strings/{locale}.json");
            foreach (string key in required)
            {
                Assert.Contains($"\"{key}\"", json, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void SmtcNoticeCopy_StaysGlobal_NoNamedPlayers()
    {
        // The banner copy must never name regional players: DeskBox ships
        // worldwide and every market uses different software.
        string[] banned =
        [
            "NetEase",
            "网易云",
            "網易雲",
            "QQ音乐",
            "QQ音樂",
            "QQ Music",
        ];

        foreach (string locale in EnumerateLocales())
        {
            string json = ReadRepositoryFile($"src/DeskBox/Strings/{locale}.json");
            int start = json.IndexOf("\"Settings.Music.SmtcNotice.Title\"", StringComparison.Ordinal);
            Assert.True(start >= 0, $"{locale}: SmtcNotice.Title missing");
            int descriptionAt = json.IndexOf("\"Settings.Music.SmtcNotice.Description\"", StringComparison.Ordinal);
            Assert.True(descriptionAt > start, $"{locale}: SmtcNotice.Description missing");
            string segment = json.Substring(start, descriptionAt - start + 2500);
            foreach (string needle in banned)
            {
                Assert.DoesNotContain(needle, segment, StringComparison.Ordinal);
            }
        }
    }

    private static IEnumerable<string> EnumerateLocales()
    {
        string stringsDirectory = TestPaths.FromRepository("src/DeskBox/Strings");
        foreach (string file in Directory.EnumerateFiles(stringsDirectory, "*.json"))
        {
            yield return Path.GetFileNameWithoutExtension(file);
        }
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
