namespace DeskBox.Tests;

public class FileNameLineCountContractTests
{
    [Fact]
    public void AppearanceDensitySettings_ExposesSingleAndDoubleLineSelection()
    {
        string root = FindRepositoryRoot();
        string settingsXaml = File.ReadAllText(Path.Combine(root, "src/DeskBox/Views/SettingsWindow.xaml"));
        string selectionOptions = File.ReadAllText(Path.Combine(root, "src/DeskBox/ViewModels/SettingsViewModel.SelectionOptions.cs"));

        Assert.Contains("Settings.FileNameLines.Title", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("AvailableFileNameLineCountOptions", settingsXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.FileNameLines.Single", selectionOptions, StringComparison.Ordinal);
        Assert.Contains("Settings.FileNameLines.Double", selectionOptions, StringComparison.Ordinal);
    }

    [Fact]
    public void IconView_FileAndStackNames_BindToConfiguredLineCount()
    {
        string root = FindRepositoryRoot();
        string itemSurface = File.ReadAllText(Path.Combine(root, "src/DeskBox/Controls/FileItemSurface.xaml"));
        string fileSurface = File.ReadAllText(Path.Combine(root, "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));

        Assert.Contains("LayoutContext.IconLabelMaxLines", itemSurface, StringComparison.Ordinal);
        Assert.Contains("DataContext.IconLabelMaxLines", fileSurface, StringComparison.Ordinal);
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
