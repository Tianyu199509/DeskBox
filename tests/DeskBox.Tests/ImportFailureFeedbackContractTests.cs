namespace DeskBox.Tests;

/// <summary>
/// Source contracts for the import-failure feedback wiring: the dedicated
/// drop-preparation causes (mapped folder unavailable / destination outside
/// the mapped root) map to their localized keys, and the stack-drop legs
/// report the localized description instead of leaking the raw English
/// exception message.
/// </summary>
public sealed class ImportFailureFeedbackContractTests
{
    private const string SurfaceSource =
        "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs";
    private const string ItemVisualsSource =
        "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.ItemVisuals.cs";
    private const string PolicySource =
        "src/DeskBox/Controls/WidgetContents/ImportFailureMessagePolicy.cs";

    [Fact]
    public void Policy_MapsDropPreparationFailuresToDedicatedKeys()
    {
        string policy = File.ReadAllText(TestPaths.FromRepository(PolicySource));

        Assert.Contains("Widget.Import.MappedFolderUnavailable", policy);
        Assert.Contains("Widget.Import.DestinationOutsideMappedRoot", policy);
    }

    [Fact]
    public void SurfaceAndPasteLegs_RouteThroughTheLocalizedDescription()
    {
        string surface = File.ReadAllText(TestPaths.FromRepository(SurfaceSource));

        // Paste / picker leg and WinUI drop leg surface dedicated causes
        // instead of the generic file-action / import-failed wording.
        int legs = surface
            .Split("DescribeImportFailure(ex, requestedCount: 0)")
            .Length - 1;
        Assert.Equal(2, legs);
    }

    [Fact]
    public void StackDropLegs_ReportLocalizedFailureInsteadOfRawExceptionMessage()
    {
        string itemVisuals = File.ReadAllText(
            TestPaths.FromRepository(ItemVisualsSource));

        int stackLegs = itemVisuals
            .Split("DescribeImportFailure(ex, requestedCount: 0)")
            .Length - 1;
        Assert.Equal(2, stackLegs);
    }
}
