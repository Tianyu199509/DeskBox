using DeskBox.Services;
using DeskBox.Views;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace DeskBox.Tests;

public sealed class WidgetForegroundContractTests
{
    [Fact]
    public void SettingsSurface_ExposesPaletteAndColorControlsWithoutTextEdge()
    {
        string appearanceXaml = Read("src/DeskBox/Views/SettingsSections/AppearanceSettingsSection.xaml");
        string settingsWindowXaml = Read("src/DeskBox/Views/SettingsWindow.xaml");
        string bridge = Read(
            "src/DeskBox/Features/Appearance/AppearanceSettingsViewModel.AotBindableProperties.cs");

        Assert.Contains("AvailableForegroundModeOptions", appearanceXaml, StringComparison.Ordinal);
        Assert.Contains("ForegroundColorHex", appearanceXaml, StringComparison.Ordinal);
        Assert.Contains("nameof(ForegroundColorHex)", bridge, StringComparison.Ordinal);
        // The text edge experiment was removed in 1.5.1 (too many platform
        // traps for the value it added); nothing may quietly reintroduce a
        // shadow surface, host or setting.
        Assert.DoesNotContain("WidgetTextEdge", appearanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetTextEdge", settingsWindowXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetTextEdge", bridge, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/Views/ContentWidgetWindow.xaml")]
    public void WidgetRoots_ProvideLocalSemanticBrushesWithoutDetachedShadowHost(string path)
    {
        string xaml = Read(path);

        Assert.Contains("x:Key=\"TextFillColorPrimaryBrush\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TextFillColorSecondaryBrush\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetTextShadowHost", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void TextEdgeFeature_LeavesNoManagerHostOrSettingBehind()
    {
        // The text edge (composition shadow behind widget text) was dropped in
        // 1.5.1: GetAlphaMask snapshots, per-surface budgets and host lifetime
        // kept producing widget freezes, so the whole feature goes — manager,
        // window and capsule mounts, the popover mount, and the setting.
        string foreground = Read(
            "src/DeskBox/Views/WidgetWindowBase.Foreground.cs");
        string shell = Read("src/DeskBox/Controls/WidgetShell.xaml.cs");
        string popover = Read(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs");
        string settings = Read("src/DeskBox/Models/AppSettings.cs");
        string foregroundSettings = Read(
            "src/DeskBox/Services/WidgetForegroundSettings.cs");
        string menu = Read("src/DeskBox/Services/WidgetForegroundMenuBuilder.cs");

        Assert.Contains("CleanupWidgetForegroundAppearance", foreground, StringComparison.Ordinal);
        Assert.DoesNotContain("TextEdge", foreground, StringComparison.Ordinal);
        Assert.DoesNotContain("TextEdge", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("TextShadow", popover, StringComparison.Ordinal);
        Assert.DoesNotContain("WidgetTextEdgeMode", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("EdgeMode", foregroundSettings, StringComparison.Ordinal);
        Assert.DoesNotContain("TextEdge", menu, StringComparison.Ordinal);
        Assert.False(File.Exists(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetTextShadowManager.cs")));
        Assert.False(File.Exists(TestPaths.FromRepository(
            "src/DeskBox/Views/WidgetWindowBase.TextEdge.cs")));
    }

    [Fact]
    public void TheWidgetMenu_ExposesPerWidgetForegroundOverrides()
    {
        Assert.Contains(
            "WidgetForegroundMenuBuilder.Create",
            Read("src/DeskBox/Views/ContentWidgetWindow.Commands.cs"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void WeatherContent_UsesWidgetPaletteWithoutDetachedThemeOverrides()
    {
        string weatherCode = Read(
            "src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.xaml.cs");
        string windowCode = Read("src/DeskBox/Views/ContentWidgetWindow.xaml.cs");

        // A weather-local RequestedTheme makes ThemeResource labels resolve
        // against framework brushes instead of the widget's mutable palette.
        Assert.Contains(
            "RequestedTheme = ElementTheme.Default;",
            weatherCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "RootGrid.RequestedTheme = ElementTheme.Default;",
            weatherCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "RootGrid.RequestedTheme = !_viewModel.UsesRichSkin",
            weatherCode,
            StringComparison.Ordinal);

        // The collapsed weather capsule must inherit the same palette as the
        // expanded content instead of forcing a second light/dark theme.
        Assert.DoesNotContain(
            "UseLightForeground: usesRichSkin",
            windowCode,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/Controls/WidgetShell.xaml")]
    [InlineData("src/DeskBox/Controls/FileItemSurface.xaml")]
    [InlineData("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml")]
    [InlineData("src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml")]
    [InlineData("src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml")]
    [InlineData("src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml")]
    [InlineData("src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml")]
    [InlineData("src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.xaml")]
    public void WidgetContentRoots_InheritTheWidgetPrimaryForeground(string path)
    {
        Assert.Contains(
            "Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\"",
            Read(path),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/Views/ContentWidgetWindow.xaml")]
    public void WidgetRoots_RedirectDefaultNativeTextStatesToLocalSemanticBrushes(
        string path)
    {
        string xaml = Read(path);

        Assert.Contains("x:Key=\"DefaultTextForegroundThemeBrush\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TextControlForeground\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"TextControlPlaceholderForeground\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"GridViewItemForeground\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ListViewItemForeground\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SegmentedItemForeground\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ButtonItemForeground\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"PivotItemForeground\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CodeGeneratedText_ResolvesTheHostingWidgetResourceScope()
    {
        string markdown = Read("src/DeskBox/Controls/MarkdownDocumentView.cs");
        string stackPopover = Read(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs");
        string quickCapture = Read(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs");
        string todo = Read(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.EditingAndUndo.cs");
        string clipboard = Read(
            "src/DeskBox/Controls/WidgetContents/ClipboardWidgetContent.xaml.cs");
        string clipboardAdapter = Read(
            "src/DeskBox/Controls/WidgetContents/ClipboardWidgetContentAdapter.cs");
        string foregroundWindow = Read(
            "src/DeskBox/Views/WidgetWindowBase.Foreground.cs");
        string contentWindow = Read(
            "src/DeskBox/Views/ContentWidgetWindow.xaml.cs");
        string groupSwitcherXaml = Read(
            "src/DeskBox/Controls/WidgetGroupTitleSwitcher.xaml");
        string groupSwitcherTabs = Read(
            "src/DeskBox/Controls/WidgetGroupTitleSwitcher.Tabs.cs");
        string neutralBrush = Read("src/DeskBox/Helpers/NeutralInteractionBrush.cs");

        Assert.Contains("_contentForeground = Foreground ??", markdown, StringComparison.Ordinal);
        Assert.Contains("SolidColorBrush.ColorProperty", markdown, StringComparison.Ordinal);
        Assert.Contains(
            "NeutralInteractionBrush.ResolveThemedResource",
            markdown,
            StringComparison.Ordinal);
        Assert.Contains("ApplyStackPopoverForegroundResources(content)", stackPopover, StringComparison.Ordinal);
        Assert.Contains(
            "NeutralInteractionBrush.ResolveThemedResource",
            quickCapture,
            StringComparison.Ordinal);
        Assert.Contains(
            "NeutralInteractionBrush.ResolveThemedResource",
            todo,
            StringComparison.Ordinal);
        Assert.Contains(
            "NeutralInteractionBrush.ResolveThemedResource(key, this)",
            clipboard,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Application.Current.Resources.ThemeDictionaries",
            clipboard,
            StringComparison.Ordinal);
        Assert.Contains(
            "content.ApplyAppearanceVisuals();",
            clipboardAdapter,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnWidgetForegroundChanged();",
            foregroundWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "protected override void OnWidgetForegroundChanged()",
            contentWindow,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Setter Property=\"Foreground\" Value=\"{ThemeResource TextFillColorPrimaryBrush}\" />",
            groupSwitcherXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "CurrentTitle.Foreground = primaryBrush;",
            groupSwitcherTabs,
            StringComparison.Ordinal);
        Assert.Contains(
            "OutgoingTitle.Foreground = primaryBrush;",
            groupSwitcherTabs,
            StringComparison.Ordinal);
        Assert.Contains("VisualTreeHelper.GetParent(current)", neutralBrush, StringComparison.Ordinal);
        Assert.Contains("candidate.Resources.TryGetValue(key", neutralBrush, StringComparison.Ordinal);
    }

    [Fact]
    public void OverlayPolarity_AdaptsToForegroundLuminance_AcrossLightDarkCustomAndHighContrast()
    {
        var lightOnLight = WidgetWindowBase.ResolveForegroundPalette(
            WidgetForegroundSettings.ModeLight,
            default,
            ElementTheme.Light,
            highContrast: false);
        Assert.True(lightOnLight.UseDarkOverlay);
        Assert.Equal(Color.FromArgb(0xFF, 0x2C, 0x2C, 0x2C), lightOnLight.OverlaySurface);
        Assert.Equal(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF), lightOnLight.SubtleButtonPointerOver);

        var darkOnDark = WidgetWindowBase.ResolveForegroundPalette(
            WidgetForegroundSettings.ModeDark,
            default,
            ElementTheme.Dark,
            highContrast: false);
        Assert.False(darkOnDark.UseDarkOverlay);
        Assert.Equal(Color.FromArgb(0xFF, 0xF9, 0xF9, 0xF9), darkOnDark.OverlaySurface);
        Assert.Equal(Color.FromArgb(0x0D, 0x00, 0x00, 0x00), darkOnDark.SubtleButtonPointerOver);

        var followLight = WidgetWindowBase.ResolveForegroundPalette(
            WidgetForegroundSettings.ModeFollowTheme,
            default,
            ElementTheme.Light,
            highContrast: false);
        Assert.False(followLight.UseDarkOverlay);
        Assert.Equal(WidgetWindowBase.LightOverlaySurfaceColor, followLight.OverlaySurface);

        var followDark = WidgetWindowBase.ResolveForegroundPalette(
            WidgetForegroundSettings.ModeFollowTheme,
            default,
            ElementTheme.Dark,
            highContrast: false);
        Assert.True(followDark.UseDarkOverlay);
        Assert.Equal(WidgetWindowBase.DarkOverlaySurfaceColor, followDark.OverlaySurface);

        Color brightYellow = Color.FromArgb(0xFF, 0xFF, 0xE0, 0x66);
        var customLight = WidgetWindowBase.ResolveForegroundPalette(
            WidgetForegroundSettings.ModeCustom,
            brightYellow,
            ElementTheme.Light,
            highContrast: false);
        Assert.True(customLight.UseDarkOverlay);
        Assert.Equal(brightYellow, customLight.Primary);
        Assert.Equal(WidgetWindowBase.DarkOverlaySurfaceColor, customLight.OverlaySurface);

        Color deepNavy = Color.FromArgb(0xFF, 0x10, 0x2A, 0x43);
        var customDark = WidgetWindowBase.ResolveForegroundPalette(
            WidgetForegroundSettings.ModeCustom,
            deepNavy,
            ElementTheme.Dark,
            highContrast: false);
        Assert.False(customDark.UseDarkOverlay);
        Assert.Equal(deepNavy, customDark.Primary);
        Assert.Equal(WidgetWindowBase.LightOverlaySurfaceColor, customDark.OverlaySurface);

        Color hcFg = Color.FromArgb(0xFF, 0xFF, 0xFF, 0x00);
        Color hcBg = Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        var highContrast = WidgetWindowBase.ResolveForegroundPalette(
            WidgetForegroundSettings.ModeLight,
            default,
            ElementTheme.Light,
            highContrast: true,
            highContrastForeground: hcFg,
            highContrastBackground: hcBg);
        Assert.Equal(hcFg, highContrast.Primary);
        Assert.Equal(hcBg, highContrast.OverlaySurface);
        Assert.Equal(Color.FromArgb(0x00, 0x00, 0x00, 0x00), highContrast.SubtleButtonPointerOver);
    }

    [Fact]
    public void WidgetLocalOverlayBrushes_ClipboardStripPolish_AndPopoverThemeIsolation_AreWired()
    {
        string contentWindowXaml = Read("src/DeskBox/Views/ContentWidgetWindow.xaml");
        string[] expectedLocalKeys =
        [
            "WidgetOverlaySurfaceBrush",
            "WidgetOverlayInputSurfaceBrush",
            "WidgetOverlayStrokeBrush",
            "WidgetSubtleButtonBackgroundPointerOver",
            "WidgetSubtleButtonBackgroundPressed",
            "ButtonBackground",
            "ButtonBackgroundPointerOver",
            "ButtonBackgroundPressed",
            "ButtonBackgroundDisabled",
            "ButtonBorderBrush",
            "ButtonBorderBrushPointerOver",
            "ButtonBorderBrushPressed"
        ];
        foreach (string key in expectedLocalKeys)
        {
            Assert.Contains($"x:Key=\"{key}\"", contentWindowXaml, StringComparison.Ordinal);
        }

        string clipboardXaml = Read("src/DeskBox/Controls/WidgetContents/ClipboardWidgetContent.xaml");
        int overlayStart = clipboardXaml.IndexOf("x:Name=\"ActionButtonsOverlay\"", StringComparison.Ordinal);
        Assert.True(overlayStart >= 0);
        int overlayEnd = clipboardXaml.IndexOf("</Border>", overlayStart, StringComparison.Ordinal);
        string overlayBlock = clipboardXaml.Substring(overlayStart, overlayEnd - overlayStart);
        Assert.Contains("BorderBrush=\"{ThemeResource WidgetOverlayStrokeBrush}\"", overlayBlock, StringComparison.Ordinal);
        Assert.Contains("BorderThickness=\"1\"", overlayBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Opacity=\"0.84\"", overlayBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("Opacity=\"0.86\"", overlayBlock, StringComparison.Ordinal);

        string popoverCode = Read("src/DeskBox/Views/WidgetEditorPopoverWindow.cs");
        Assert.Contains("ElementTheme effectiveTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(themeScope);", popoverCode, StringComparison.Ordinal);
        Assert.Contains("NeutralInteractionBrush.ResolveThemedResource(\"TextFillColorSecondaryBrush\", effectiveTheme)", popoverCode, StringComparison.Ordinal);

        string clipboardCode = Read("src/DeskBox/Controls/WidgetContents/ClipboardWidgetContent.xaml.cs");
        Assert.Contains("ElementTheme popoverTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(this);", clipboardCode, StringComparison.Ordinal);
        Assert.Contains("NeutralInteractionBrush.ResolveThemedResource(\"TextFillColorSecondaryBrush\", popoverTheme)", clipboardCode, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
