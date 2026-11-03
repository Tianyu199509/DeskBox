using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public class FileNameLineCountContractTests
{
    [Fact]
    public void AppearanceDensitySettings_ExposesHiddenSingleAndDoubleSelection()
    {
        string root = FindRepositoryRoot();
        string fileWidgetXaml = File.ReadAllText(Path.Combine(root, "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml"));
        string editor = File.ReadAllText(Path.Combine(root, "src/DeskBox/Features/Appearance/AppearanceSettingsViewModel.cs"));

        Assert.Contains("Settings.FileNameLines.Title", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("Appearance.AvailableFileNameLineCountOptions", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("{x:Bind Appearance.FileNameLineCount, Mode=TwoWay}", fileWidgetXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.FileNameLines.Hidden", editor, StringComparison.Ordinal);
        Assert.Contains("Settings.FileNameLines.Single", editor, StringComparison.Ordinal);
        Assert.Contains("Settings.FileNameLines.Double", editor, StringComparison.Ordinal);
    }

    [Fact]
    public void IconView_FileAndStackNames_BindToConfiguredLineCount()
    {
        string root = FindRepositoryRoot();
        string itemSurface = File.ReadAllText(Path.Combine(root, "src/DeskBox/Controls/FileItemSurface.xaml"));
        string fileSurface = File.ReadAllText(Path.Combine(root, "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));

        Assert.Contains("LayoutContext.IconLabelMaxLines", itemSurface, StringComparison.Ordinal);
        Assert.Contains("DataContext.IconLabelMaxLines", fileSurface, StringComparison.Ordinal);
        Assert.Contains("LayoutContext.IconLabelVisibility", itemSurface, StringComparison.Ordinal);
        Assert.Contains("DataContext.IconLabelVisibility", fileSurface, StringComparison.Ordinal);
    }

    [Fact]
    public void IconTileHeight_ReservesConfiguredLinesAtMaximumSystemTextScale()
    {
        double oneLineHeight = WidgetViewModel.ResolveIconTileHeight(
            iconSize: SettingsService.DefaultIconSize,
            textSize: SettingsService.DefaultTextSize,
            fileNameLineCount: SettingsService.MinFileNameLineCount,
            verticalScale: SettingsService.DefaultVerticalSpacingScale,
            systemTextScaleFactor:
                WindowsCompatibilityService.MaxSystemTextScaleFactor);
        double twoLineHeight = WidgetViewModel.ResolveIconTileHeight(
            iconSize: SettingsService.DefaultIconSize,
            textSize: SettingsService.DefaultTextSize,
            fileNameLineCount: SettingsService.DefaultFileNameLineCount,
            verticalScale: SettingsService.DefaultVerticalSpacingScale,
            systemTextScaleFactor:
                WindowsCompatibilityService.MaxSystemTextScaleFactor);
        double defaultScaleTwoLineHeight = WidgetViewModel.ResolveIconTileHeight(
            iconSize: SettingsService.DefaultIconSize,
            textSize: SettingsService.DefaultTextSize,
            fileNameLineCount: SettingsService.DefaultFileNameLineCount,
            verticalScale: SettingsService.DefaultVerticalSpacingScale,
            systemTextScaleFactor:
                WindowsCompatibilityService.MinSystemTextScaleFactor);

        Assert.True(twoLineHeight > oneLineHeight);
        Assert.True(twoLineHeight > defaultScaleTwoLineHeight);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "DeskBox")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
