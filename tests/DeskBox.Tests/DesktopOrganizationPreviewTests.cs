using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class DesktopOrganizationPreviewTests
{
    [Fact]
    public void SummaryOnlyCountsSelectedSourcesAndIncludesManualExclusions()
    {
        var personal = Item("personal.txt");
        var publicItem = Item("public.lnk") with { SourceScope = DesktopOrganizationSourceScope.Public };
        var folder = Item("folder") with { IsDirectory = true, ExclusionReason = DesktopOrganizationExclusionReason.Folder };
        var plan = new DesktopOrganizationPlan
        {
            SourceItems = [personal, publicItem, folder],
            Targets = [Target("docs", [personal])],
            ExcludedItems = [publicItem with { ExclusionReason = DesktopOrganizationExclusionReason.SourceNotSelected }, folder]
        };
        var selections = new Dictionary<string, DesktopOrganizationTargetSelection>();
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var summary = DesktopOrganizationPreviewSummary.Calculate(plan, selections, excluded);
        Assert.Equal(2, summary.TotalCount);
        Assert.Equal(1, summary.SelectedCount);
        Assert.Equal(1, summary.RetainedCount);
        excluded.Add(personal.SourcePath.ToUpperInvariant());
        summary = DesktopOrganizationPreviewSummary.Calculate(plan, selections, excluded);
        Assert.Equal(0, summary.SelectedCount);
        Assert.Equal(2, summary.RetainedCount);
        Assert.Equal(0, summary.DestinationCount);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    [InlineData(false, true, 1)]
    [InlineData(true, true, 2)]
    public void SummaryScopeAndSelectionAlwaysUseTheSamePopulation(bool personal, bool shared, int count)
    {
        var items = new List<DesktopOrganizationFileSnapshot>
        { Item("a.txt"), Item("b.txt") with { SourceScope = DesktopOrganizationSourceScope.Public } };
        var plan = new DesktopOrganizationPlan
        {
            IncludePersonalDesktop = personal, IncludePublicDesktop = shared,
            SourceItems = items, Targets = [Target("all", items)]
        };
        var summary = DesktopOrganizationPreviewSummary.Calculate(plan, new Dictionary<string, DesktopOrganizationTargetSelection>(), new HashSet<string>());
        Assert.Equal(count, summary.TotalCount);
        Assert.Equal(count, summary.SelectedCount);
        Assert.Equal(0, summary.RetainedCount);
    }

    [Fact]
    public void RescanKeepsChoicesRemovesMissingPathsAndRequiresReviewForNewFiles()
    {
        var excluded = Item("excluded.txt");
        var folder = Item("folder") with { ExclusionReason = DesktopOrganizationExclusionReason.Folder, IsDirectory = true };
        var removed = Item("removed.txt");
        var added = Item("new.txt");
        var result = DesktopOrganizationPreviewReconciliation.Calculate(
            [excluded, folder, removed], [excluded, folder, added],
            [excluded.SourcePath.ToUpperInvariant(), removed.SourcePath], [folder.SourcePath], []);
        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.RemovedCount);
        Assert.True(result.ExcludedPaths.SetEquals([excluded.SourcePath, added.SourcePath]));
        Assert.True(result.OptionalPaths.SetEquals([folder.SourcePath]));
        Assert.True(result.NewPaths.SetEquals([added.SourcePath]));
        var again = DesktopOrganizationPreviewReconciliation.Calculate(
            [excluded, folder, added], [excluded, folder, added],
            result.ExcludedPaths, result.OptionalPaths, result.NewPaths);
        Assert.Equal(0, again.AddedCount);
        Assert.Contains(added.SourcePath, again.NewPaths);
        Assert.Contains(added.SourcePath, again.ExcludedPaths);
    }

    [Fact]
    public void RescanDoesNotCarryAnOptInAcrossAFileBecomingProtected()
    {
        var folder = Item("folder") with { IsDirectory = true, ExclusionReason = DesktopOrganizationExclusionReason.Folder };
        var protectedFolder = folder with { ExclusionReason = DesktopOrganizationExclusionReason.ReparsePoint };
        var result = DesktopOrganizationPreviewReconciliation.Calculate([folder], [protectedFolder], [], [folder.SourcePath], []);
        Assert.Empty(result.OptionalPaths);
        Assert.Empty(result.NewPaths);
    }

    [Fact]
    public void TwoCategoriesSentToSameExistingWidgetCountAsOneDestination()
    {
        var plan = new DesktopOrganizationPlan
        {
            Targets = [Target("docs", [Item("a.txt")]), Target("apps", [Item("b.lnk")])]
        };
        var selections = plan.Targets.ToDictionary(target => target.SourceBucketId, target => new DesktopOrganizationTargetSelection
        {
            SourceBucketId = target.SourceBucketId, IsSelected = true,
            DestinationMode = DesktopOrganizationDestinationMode.ExistingWidget, ExistingWidgetId = "work"
        });
        var summary = DesktopOrganizationPreviewSummary.Calculate(plan, selections, new HashSet<string>());
        Assert.Equal(2, summary.SelectedCount);
        Assert.Equal(1, summary.DestinationCount);
        Assert.Equal(0, summary.NewDestinationCount);
        selections["apps"].DestinationMode = DesktopOrganizationDestinationMode.Dynamic;
        summary = DesktopOrganizationPreviewSummary.Calculate(plan, selections, new HashSet<string>());
        Assert.Equal(2, summary.DestinationCount);
        Assert.Equal(1, summary.NewDestinationCount);
        selections["apps"].IsSelected = false;
        summary = DesktopOrganizationPreviewSummary.Calculate(plan, selections, new HashSet<string>());
        Assert.Equal(1, summary.SelectedCount);
        Assert.Equal(1, summary.RetainedCount);
    }

    [Fact]
    public void PreviewGeometryUsesConfiguredIconTextSpacingAndDestinationOverride()
    {
        var settings = new AppSettings { IconSize = 30, TextSize = 13, FileNameLineCount = 2 };
        var original = FileWidgetIconLayout.Calculate(settings);
        Assert.Equal(30, original.ImageSize);
        Assert.Equal(13, original.LabelFontSize);
        Assert.Equal(2, original.LabelMaxLines);
        Assert.True(original.ShowLabel);
        var overridden = FileWidgetIconLayout.Calculate(settings, iconSizeOverride: 48);
        Assert.Equal(48, overridden.ImageSize);
        Assert.Equal(30, settings.IconSize);
        Assert.True(overridden.TileWidth >= original.TileWidth);
        settings.HorizontalSpacingScale = SettingsService.MaxSpacingScale;
        settings.VerticalSpacingScale = SettingsService.MaxSpacingScale;
        settings.FileNameWidthScale = SettingsService.MaxSpacingScale;
        var spacious = FileWidgetIconLayout.Calculate(settings);
        Assert.True(spacious.CellWidth > original.CellWidth);
        Assert.True(spacious.CellHeight > original.CellHeight);
        Assert.True(spacious.LabelMaxWidth > original.LabelMaxWidth);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2.25)]
    public void ConfiguredNameLinesFitAtSystemTextScale(double textScale)
    {
        var settings = new AppSettings { IconSize = 48, TextSize = 14, FileNameLineCount = 2, VerticalSpacingScale = 0 };
        var twoLines = FileWidgetIconLayout.Calculate(settings, systemTextScaleFactor: textScale);
        double contentHeight = twoLines.TilePadding.Top + twoLines.TilePadding.Bottom + twoLines.ImageSize +
            twoLines.ContentSpacing + Math.Ceiling(14 * 1.4 * textScale) * 2;
        Assert.True(twoLines.TileHeight >= contentHeight);
        settings.FileNameLineCount = 1;
        var oneLine = FileWidgetIconLayout.Calculate(settings, systemTextScaleFactor: textScale);
        Assert.True(oneLine.TileHeight < twoLines.TileHeight);
        settings.FileNameLineCount = SettingsService.HiddenFileNameLineCount;
        var hidden = FileWidgetIconLayout.Calculate(settings, systemTextScaleFactor: textScale);
        Assert.False(hidden.ShowLabel);
        Assert.True(hidden.TileHeight < oneLine.TileHeight);
    }

    private static DesktopOrganizationFileSnapshot Item(string name) => new(
        "C:\\Desktop\\" + name, name, Path.GetExtension(name), 12, DateTime.UnixEpoch,
        DesktopOrganizationCategoryIds.Documents, null, DesktopOrganizationExclusionReason.None);

    private static DesktopOrganizationTargetPlan Target(string id, List<DesktopOrganizationFileSnapshot> items) => new()
    {
        SourceBucketId = id, TargetWidgetId = id, CreatesWidget = true, Items = items
    };
}
