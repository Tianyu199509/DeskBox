namespace DeskBox.Tests;

/// <summary>
/// Pins the material section's system visual-effect cards: they are read-only
/// projections of the Windows-wide shadow/transparency switches. The buttons
/// must deep-link into the OS settings surfaces — an in-app write (registry or
/// SPIF_UPDATEINIFILE) lands in the MSIX copy-on-write private hive and
/// silently reverts after the next sign-in, so no write path may return.
/// </summary>
public sealed class WindowShadowSettingsContractTests
{
    [Fact]
    public void MaterialSection_HostsBothSystemEffectCardsWithStatusAndDeepLinks()
    {
        string slice = Read(
            "src/DeskBox/Views/SettingsWindow.xaml");
        string material = Slice(
            slice,
            "x:Key=\"AppearanceMaterialSettingsSectionTemplate\"",
            "x:Key=\"AppearanceDensitySettingsSectionTemplate\"");

        // Transparency card: status projection + ms-settings deep link.
        Assert.Contains("HeaderKey=\"Settings.SystemTransparency.Title\"", material, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SystemTransparencyStatusText}\"", material, StringComparison.Ordinal);
        Assert.Contains("Click=\"OpenSystemTransparencySettingsButton_Click\"", material, StringComparison.Ordinal);
        Assert.Contains("svc:Localized.Key=\"Settings.SystemEffect.OpenSystemSettings\"", material, StringComparison.Ordinal);

        // Shadow card: same shape, mirroring SPI_GETDROPSHADOW.
        Assert.Contains("HeaderKey=\"Settings.WindowShadow.Title\"", material, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SystemShadowStatusText}\"", material, StringComparison.Ordinal);
        Assert.Contains("Click=\"OpenWindowShadowSettingsButton_Click\"", material, StringComparison.Ordinal);
    }

    [Fact]
    public void AppearanceSection_NoLongerExposesTheShadowToggle()
    {
        string xaml = Read(
            "src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");

        Assert.DoesNotContain("WindowShadowToggle", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Settings.WindowShadow.Title", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Win32Helper_ExposesReadOnlyProbes_WithoutAnyWritePath()
    {
        string source = Read("src/DeskBox/Platform/Win32Helper.cs");

        // Live session state via SystemParametersInfo (GET only).
        Assert.Contains("SpiGetDropShadow = 0x1024", source, StringComparison.Ordinal);
        Assert.Contains("TryGetWindowDropShadowEnabled", source, StringComparison.Ordinal);

        // Registry source of truth for the transparency switch.
        Assert.Contains("TryGetSystemTransparencyEffectsEnabled", source, StringComparison.Ordinal);
        Assert.Contains("EnableTransparency", source, StringComparison.Ordinal);

        // The write path is gone: SPIF_UPDATEINIFILE persistence would be
        // swallowed by MSIX HKCU copy-on-write virtualization and revert
        // after the next sign-in.
        Assert.DoesNotContain("TrySetWindowDropShadowEnabled", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SpiSetDropShadow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SpifUpdateIniFile", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SpifSendChange", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsShell_DeepLinksIntoTheOsSettingsSurfaces()
    {
        string source = Read(
            "src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs");

        Assert.Contains("ms-settings:colors", source, StringComparison.Ordinal);
        Assert.Contains("SystemPropertiesPerformance.exe", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemEffectStates_LiveOnTheAppearanceEditorAndRefreshViaTheShell()
    {
        // The material section's DataContext is the appearance editor, not
        // SettingsViewModel — the status properties must live on the editor
        // or the {Binding}s resolve to nothing and the cards show no state.
        string startup = Read(
            "src/DeskBox/Views/SettingsWindow.Startup.cs");
        string shell = Read(
            "src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs");
        string deferred = Read(
            "src/DeskBox/Views/SettingsWindow.DeferredSections.cs");
        string editor = Read(
            "src/DeskBox/Features/Appearance/AppearanceSettingsViewModel.cs");
        string editorBridge = Read(
            "src/DeskBox/Features/Appearance/AppearanceSettingsViewModel.AotBindableProperties.cs");

        Assert.Contains("RefreshSystemAppearanceStates()", startup, StringComparison.Ordinal);
        Assert.Contains("public void RefreshSystemAppearanceStates()", shell, StringComparison.Ordinal);
        Assert.Contains("TryGetWindowDropShadowEnabled(out bool shadowsEnabled)", shell, StringComparison.Ordinal);
        Assert.Contains("TryGetSystemTransparencyEffectsEnabled(out bool transparencyEnabled)", shell, StringComparison.Ordinal);
        Assert.Contains("UpdateSystemEffectStates(shadowOn, transparencyOn)", shell, StringComparison.Ordinal);

        // Creation-time refresh covers changes made while the window was closed.
        Assert.Contains("RefreshSystemAppearanceStates()", deferred, StringComparison.Ordinal);

        Assert.Contains("public void UpdateSystemEffectStates(", editor, StringComparison.Ordinal);
        Assert.Contains("nameof(SystemShadowStatusText)", editorBridge, StringComparison.Ordinal);
        Assert.Contains("nameof(SystemTransparencyStatusText)", editorBridge, StringComparison.Ordinal);

        // Language switch regression: the status texts are computed getters
        // over the pushed raw states and RefreshLocalization must re-raise
        // them, or the cards keep showing the previous language (they store
        // no localized strings of their own).
        string refresh = Slice(
            editor,
            "public void RefreshLocalization()",
            "/// <summary>");
        Assert.Contains("OnPropertyChanged(nameof(SystemShadowStatusText))", refresh, StringComparison.Ordinal);
        Assert.Contains("OnPropertyChanged(nameof(SystemTransparencyStatusText))", refresh, StringComparison.Ordinal);
    }

    [Fact]
    public void SystemEffectStrings_PresentInEveryShippedLocale()
    {
        string[] requiredKeys =
        [
            "Settings.SystemTransparency.Title",
            "Settings.SystemTransparency.Description",
            "Settings.SystemEffect.OpenSystemSettings",
            "Settings.SystemEffect.StateOn",
            "Settings.SystemEffect.StateOff",
            "Settings.WindowShadow.Title",
            "Settings.WindowShadow.Description"
        ];
        string[] removedKeys =
        [
            "Settings.WindowShadow.Confirm.Title",
            "Settings.WindowShadow.Confirm.Body",
            "Settings.WindowShadow.ApplyFailed",
            "Settings.WindowShadow.ApplyFailedNoEffect"
        ];
        string stringsDirectory = TestPaths.FromRepository("src/DeskBox/Strings");

        foreach (string path in Directory.EnumerateFiles(stringsDirectory, "*.json"))
        {
            string content = File.ReadAllText(path);
            foreach (string key in requiredKeys)
            {
                Assert.Contains("\"" + key + "\"", content, StringComparison.Ordinal);
            }

            foreach (string key in removedKeys)
            {
                Assert.DoesNotContain("\"" + key + "\"", content, StringComparison.Ordinal);
            }
        }
    }

    private static string Read(string path)
    {
        return File.ReadAllText(TestPaths.FromRepository(path));
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing start marker: {startMarker}");
        Assert.True(end > start, $"Missing end marker: {endMarker}");
        return source[start..end];
    }
}
