namespace DeskBox.Tests;

public sealed class Windows10CornerCompatibilityTests
{
    [Fact]
    public void EffectiveCornerPolicy_IsUsedByWindowCapsuleAndPopupSurfaces()
    {
        string widgetBounds = Read("src/DeskBox/Views/WidgetWindowBase.Bounds.cs");
        string widgetCollapse = Read("src/DeskBox/Views/WidgetWindowBase.Collapse.cs");
        string searchPopup = Read("src/DeskBox/Views/SearchPopupWindow.xaml.cs");
        string stackPopover = Read(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopover.cs");

        Assert.Contains(
            "ResolveEffectiveWidgetCornerPreference",
            widgetBounds,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveEffectiveWidgetCornerPreference",
            widgetCollapse,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveEffectiveWidgetCompactMediaCornerMode",
            widgetCollapse,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveEffectiveWidgetCornerPreference",
            searchPopup,
            StringComparison.Ordinal);
        Assert.Contains(
            "ResolveEffectiveWidgetCornerPreference",
            stackPopover,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EffectiveCornerPolicy_IsUsedByMusicWeatherAndGlanceInnerSurfaces()
    {
        string musicViewModel = Read("src/DeskBox/ViewModels/MusicWidgetViewModel.cs");
        string musicContent = Read(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContent.xaml.cs");
        string weatherViewModel = Read("src/DeskBox/ViewModels/WeatherWidgetViewModel.cs");
        string weatherContent = Read(
            "src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.xaml");
        string weatherCodeBehind = Read(
            "src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.xaml.cs");
        string weatherAot = Read(
            "src/DeskBox/ViewModels/WeatherViewModels.AotBindableProperties.cs");
        string glanceViewModel = Read("src/DeskBox/ViewModels/GlanceWidgetViewModel.cs");

        Assert.Contains(
            "ResolveEffectiveWidgetCornerPreference",
            musicViewModel,
            StringComparison.Ordinal);
        Assert.Contains("ApplyAlbumArtCornerRadius", musicContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Math.Max(8, size * 0.12)", musicContent, StringComparison.Ordinal);
        Assert.Contains(
            "ResolveEffectiveWidgetCornerPreference",
            weatherViewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CornerRadius=\"{Binding WidgetCornerRadius}\"",
            weatherContent,
            StringComparison.Ordinal);
        Assert.Contains(
            "ApplyRichSkinCornerRadius",
            weatherCodeBehind,
            StringComparison.Ordinal);
        Assert.Contains("RichBackdrop.CornerRadius", weatherCodeBehind, StringComparison.Ordinal);
        Assert.Contains("LoadingOverlay.CornerRadius", weatherCodeBehind, StringComparison.Ordinal);
        Assert.Contains("nameof(WidgetCornerRadius)", weatherAot, StringComparison.Ordinal);
        Assert.Contains(
            "ResolveEffectiveWidgetCornerPreference",
            glanceViewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Win10Compatibility_PreservesPersistedRequestedCornerPreference()
    {
        string compatibility = Read(
            "src/DeskBox/Services/WindowsCompatibilityService.cs");
        string settings = Read("src/DeskBox/Services/SettingsService.cs");

        Assert.Contains(
            "ResolveEffectiveWidgetCornerPreferenceForBuild",
            compatibility,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ResolveEffectiveWidgetCornerPreference",
            settings,
            StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
