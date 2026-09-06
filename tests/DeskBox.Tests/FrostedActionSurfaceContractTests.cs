namespace DeskBox.Tests;

public sealed class FrostedActionSurfaceContractTests
{
    [Fact]
    public void RecommendedActionAreas_UseIndependentAcrylicLayers()
    {
        string root = FindRepositoryRoot();
        string onboarding = Read(root, "src/DeskBox/Views/OnboardingWindow.xaml");
        string quickCaptureSurface = Read(root, "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml");
        string quickCaptureWindow = Read(root, "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml");
        string todo = Read(root, "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml");
        string releaseNotes = Read(root, "src/DeskBox/Views/ReleaseNotesWindow.xaml");

        AssertAcrylicLayer(onboarding, "x:Name=\"FooterAcrylicSurface\"", "Opacity=\"0.46\"");
        AssertAcrylicLayer(quickCaptureSurface, "x:Name=\"DetailHeaderAcrylicSurface\"", "Opacity=\"0.42\"");
        AssertAcrylicLayer(quickCaptureWindow, "x:Name=\"DetailHeaderAcrylicSurface\"", "Opacity=\"0.42\"");
        AssertAcrylicLayer(todo, "x:Name=\"DetailHeaderAcrylicSurface\"", "Opacity=\"0.42\"");
        AssertAcrylicLayer(releaseNotes, "x:Name=\"FooterAcrylicSurface\"", "Opacity=\"0.5\"");
    }

    [Fact]
    public void DesktopOrganizationDynamicText_UsesThemeAwareStyles()
    {
        string root = FindRepositoryRoot();
        string xaml = Read(root, "src/DeskBox/Controls/DesktopOrganizationTaskView.xaml");
        string presentation = Read(root, "src/DeskBox/Controls/DesktopOrganizationTaskView.Presentation.cs");

        Assert.Contains("x:Key=\"DesktopOrganizationSecondaryTextStyle\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Value=\"{ThemeResource TextFillColorSecondaryBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Style = (Style)Resources[\"DesktopOrganizationSecondaryTextStyle\"]",
            presentation,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Application.Current.Resources[\"TextFillColorSecondaryBrush\"]",
            presentation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TodoAndQuickCaptureDetailHeaders_ShareGeometryAndIconMetrics()
    {
        string root = FindRepositoryRoot();
        string quickCapture = Read(root, "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml");
        string quickCaptureWindow = Read(root, "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml");
        string todo = Read(root, "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml");
        string quickCaptureViewModel = Read(root, "src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.cs");
        string todoViewModel = Read(root, "src/DeskBox/ViewModels/TodoWidgetViewModel.cs");

        Assert.Contains("MinHeight=\"40\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"40\"", quickCaptureWindow, StringComparison.Ordinal);
        Assert.Contains("Padding=\"4,2\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("Padding=\"4,2\"", quickCaptureWindow, StringComparison.Ordinal);
        Assert.Contains("Padding=\"4,2\"", todo, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"36\" Padding=\"2,0\" ColumnSpacing=\"2\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"36\" Padding=\"2,0\" ColumnSpacing=\"2\"", quickCaptureWindow, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"36\" Padding=\"2,0\" ColumnSpacing=\"2\"", todo, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"QuickCaptureDetailActionIconSize\">13</x:Double>", quickCapture, StringComparison.Ordinal);
        Assert.Contains("<x:Double x:Key=\"TodoDetailActionIconSize\">13</x:Double>", todo, StringComparison.Ordinal);
        Assert.Contains("Margin=\"{Binding DetailPageMargin}\"", quickCapture, StringComparison.Ordinal);
        Assert.DoesNotContain("Padding=\"4,0,0,0\"", quickCapture, StringComparison.Ordinal);
        Assert.Contains("Path=DataContext.DetailPageMargin", todo, StringComparison.Ordinal);
        Assert.Contains("DetailPageMargin => new(0, 6 - RootPadding.Top, 0, 0)", quickCaptureViewModel, StringComparison.Ordinal);
        Assert.Contains("DetailPageMargin => new(0, 6 - RootPadding.Top, 0, 0)", todoViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("ScaleTransform ScaleX=\"1.3\" ScaleY=\"1.3\"", quickCaptureWindow, StringComparison.Ordinal);
    }

    private static void AssertAcrylicLayer(string xaml, string name, string opacity)
    {
        Assert.Contains(name, xaml, StringComparison.Ordinal);
        Assert.Contains(
            "Background=\"{ThemeResource SystemControlAcrylicElementBrush}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(opacity, xaml, StringComparison.Ordinal);
        Assert.Contains("IsHitTestVisible=\"False\"", xaml, StringComparison.Ordinal);
    }

    private static string Read(string root, string relativePath) =>
        File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "DeskBox", "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("DeskBox repository root was not found.");
    }
}
