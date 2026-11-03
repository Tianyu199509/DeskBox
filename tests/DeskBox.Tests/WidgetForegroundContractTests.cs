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
        string neutralBrush = Read("src/DeskBox/Helpers/NeutralInteractionBrush.cs");

        Assert.Contains("_contentForeground = Foreground ??", markdown, StringComparison.Ordinal);
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
        Assert.Contains("VisualTreeHelper.GetParent(current)", neutralBrush, StringComparison.Ordinal);
        Assert.Contains("candidate.Resources.TryGetValue(key", neutralBrush, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
