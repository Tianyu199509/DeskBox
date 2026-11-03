namespace DeskBox.Tests;

/// <summary>
/// Notice cards in the settings surfaces must be native InfoBars driven
/// through the Title/Message properties (HeaderKey/DescriptionKey). Three
/// historical traps are pinned here:
/// 1. Rich content placed inside the InfoBar content region collided with
///    the template action-button slot (a button-shaped outline across the
///    text).
/// 2. The elevated-hotkey notice must sit OUTSIDE the hotkey expander
///    entirely (a sibling below it, unified across the interaction and
///    search pages): long wrapped text fails to lay out inside the nested
///    item card and renders as an empty strip.
/// 3. ThemeResource severity brushes fail to resolve inside the
///    late-created section templates (DataTemplate.LoadContent), painting
///    the notice exactly like the card behind it with near-invisible text;
///    the severity brushes must be pinned from the application resources
///    in code-behind, and each notice needs an x:Name so the shell can
///    reach it.
/// </summary>
public sealed class SettingsNoticeCardContractTests
{
    [Fact]
    public void SettingsSurfaces_DoNotPlaceRichContentInsideInfoBarContent()
    {
        string window = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.xaml");
        Assert.DoesNotContain("InfoBar.Content", window, StringComparison.Ordinal);

        foreach (string sectionPath in Directory.EnumerateFiles(
                     TestPaths.FromRepository("src/DeskBox/Views/SettingsSections"),
                     "*.xaml"))
        {
            string sectionXaml = File.ReadAllText(sectionPath);
            Assert.DoesNotContain("InfoBar.Content", sectionXaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Notices_UseNativeInfoBarsOrCompactInfoTipsWithoutPermanentHotkeyBanners()
    {
        string window = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.xaml");
        string search = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml");

        int storeNotice = CountOccurrences(window,
            "svc:Localized.HeaderKey=\"Settings.About.Channel.Store\"");
        int cloudNoticeInfoBar = CountOccurrences(window,
            "svc:Localized.HeaderKey=\"Settings.CloudBackup.SyncNotice.Title\"");
        int cloudNoticeInfoTip = CountOccurrences(window,
            "svc:InfoTip.TitleKey=\"Settings.CloudBackup.SyncNotice.Title\"");
        Assert.Equal(1, storeNotice);
        Assert.Equal(1, cloudNoticeInfoBar);
        Assert.Equal(1, cloudNoticeInfoTip);

        // Elevated hotkey and SMTC background explanations live in compact InfoTips on their host cards.
        Assert.Contains(
            "svc:InfoTip.TitleKey=\"Settings.GlobalHotkey.ElevatedNotice.Title\"",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "svc:InfoTip.TitleKey=\"Settings.GlobalHotkey.ElevatedNotice.Title\"",
            search,
            StringComparison.Ordinal);
        Assert.Contains(
            "svc:InfoTip.TitleKey=\"Settings.Music.SmtcNotice.Title\"",
            window,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalHotkeyElevatedNoticeInfoBar", window, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchHotkeyElevatedNoticeInfoBar", search, StringComparison.Ordinal);
        Assert.DoesNotContain("MusicSmtcNoticeInfoBar", window, StringComparison.Ordinal);
    }

    [Fact]
    public void NoticeInfoBarSeverityBrushes_ResolvePerActualTheme()
    {
        // The informational wash is pinned per ActualTheme with the library's
        // own per-theme values. The previous pin read
        // Application.Current.Resources, whose themed lookup served the stale
        // dictionary on Windows 10 after a live theme switch, painting the
        // light wash over the dark theme.
        string code = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.Navigation.cs");

        Assert.Contains("NoticeInfoBackgroundLight", code, StringComparison.Ordinal);
        Assert.Contains("0x80, 0xF6, 0xF6, 0xF6", code, StringComparison.Ordinal);
        Assert.Contains("NoticeInfoBackgroundDark", code, StringComparison.Ordinal);
        Assert.Contains("0x08, 0xFF, 0xFF, 0xFF", code, StringComparison.Ordinal);
        Assert.Contains("infoBar.ActualTheme == ElementTheme.Dark", code, StringComparison.Ordinal);
        Assert.Contains("AccessibilitySettings().HighContrast", code, StringComparison.Ordinal);
        // WinRT activation must stay lazy: static initializers would run on
        // headless test hosts without WinAppSDK activation.
        Assert.DoesNotContain("static readonly Windows.UI.ViewManagement", code, StringComparison.Ordinal);
        Assert.DoesNotContain("InfoBarInformationalSeverityBackgroundBrush\" out", code, StringComparison.Ordinal);

        // The template's default text colors already follow the theme; the
        // foreground pin (whose key never existed in the library) is gone.
        Assert.DoesNotContain("InfoBarInformationalSeverityForegroundBrush", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchSection_DoesNotReadApplicationResourcesDirectly()
    {
        string code = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/SearchSettingsSection.xaml.cs");

        Assert.DoesNotContain("SearchHotkeyElevatedNoticeInfoBar", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Application.Current.Resources", code, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalizedService_MapsInfoBarHeaderAndDescriptionKeys()
    {
        string code = ReadRepositoryFile("src/DeskBox/Services/Localized.cs");

        Assert.Contains("case InfoBar infoBar:", code, StringComparison.Ordinal);
        Assert.Contains("infoBar.Title = value;", code, StringComparison.Ordinal);
        Assert.Contains("infoBar.Message = value;", code, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string content, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = content.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
