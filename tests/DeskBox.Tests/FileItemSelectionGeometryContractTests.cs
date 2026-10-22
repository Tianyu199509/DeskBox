using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class FileItemSelectionGeometryContractTests
{
    [Fact]
    public void IconSelectionSurface_FillsItsColumnAndKeepsANarrowGutter()
    {
        string root = FindRepositoryRoot();
        string source = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/FileItemSurface.xaml.cs"));
        XDocument document = XDocument.Load(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace controls = "using:DeskBox.Controls";

        Assert.Contains(
            "public HorizontalAlignment SurfaceHorizontalAlignment =>",
            source,
            StringComparison.Ordinal);
        int alignmentStart = source.IndexOf(
            "public HorizontalAlignment SurfaceHorizontalAlignment",
            StringComparison.Ordinal);
        int alignmentEnd = source.IndexOf(
            "public double SurfaceMaxWidth",
            StringComparison.Ordinal);
        string alignmentProperty = source[alignmentStart..alignmentEnd];
        Assert.Contains(
            "HorizontalAlignment.Stretch",
            alignmentProperty,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HorizontalAlignment.Left",
            alignmentProperty,
            StringComparison.Ordinal);
        Assert.Contains(
            "return new Thickness(1, 0, 1, 0);",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public double SurfaceMaxWidth => double.PositiveInfinity;",
            source,
            StringComparison.Ordinal);

        foreach (string templateKey in new[]
                 {
                     "SurfaceFileIconTemplate",
                     "StackPopoverFileIconTemplate"
                 })
        {
            XElement template = document
                .Descendants(presentation + "DataTemplate")
                .Single(element =>
                    string.Equals(
                        (string?)element.Attribute(x + "Key"),
                        templateKey,
                        StringComparison.Ordinal));
            XElement surface = template
                .Descendants(controls + "FileItemSurface")
                .Single();

            Assert.Equal("Icon", (string?)surface.Attribute("Mode"));
            Assert.Equal(
                "Stretch",
                (string?)surface.Attribute("HorizontalAlignment"));
            Assert.Equal(
                "Top",
                (string?)surface.Attribute("VerticalAlignment"));
        }
    }

    [Fact]
    public void ListSelectionSurface_CoversTheFullRowLikeExplorer()
    {
        string root = FindRepositoryRoot();
        XDocument document = XDocument.Load(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace controls = "using:DeskBox.Controls";

        // Hover and selection paint on the interactive surface, so both
        // list hosts must stretch it across the row instead of letting it
        // hug the icon plus label width.
        foreach (string templateKey in new[]
                 {
                     "SurfaceFileListTemplate",
                     "StackPopoverFileListTemplate"
                 })
        {
            XElement template = document
                .Descendants(presentation + "DataTemplate")
                .Single(element =>
                    string.Equals(
                        (string?)element.Attribute(x + "Key"),
                        templateKey,
                        StringComparison.Ordinal));
            XElement surface = template
                .Descendants(controls + "FileItemSurface")
                .Single();

            Assert.Equal("List", (string?)surface.Attribute("Mode"));
            Assert.Equal(
                "Stretch",
                (string?)surface.Attribute("HorizontalAlignment"));
        }
    }

    [Fact]
    public void BuildPrimaryContentZone_UnionsBlocksAndInflatesEdges()
    {
        var iconBounds = new Windows.Foundation.Rect(10, 5, 20, 20);
        var labelBounds = new Windows.Foundation.Rect(35, 8, 100, 30);

        Windows.Foundation.Rect zone = DeskBox.Controls
            .FileItemSelectionGeometry.BuildPrimaryContentZone(
                iconBounds,
                labelBounds,
                inflation: 4);

        // Deep inside either block.
        Assert.True(zone.Contains(new Windows.Foundation.Point(15, 15)));
        Assert.True(zone.Contains(new Windows.Foundation.Point(100, 20)));
        // Within the 4px slack around the union's edges.
        Assert.True(zone.Contains(new Windows.Foundation.Point(138, 20)));
        Assert.True(zone.Contains(new Windows.Foundation.Point(15, 2)));
        // Beyond the slack.
        Assert.False(zone.Contains(new Windows.Foundation.Point(145, 20)));
        Assert.False(zone.Contains(new Windows.Foundation.Point(15, 0)));
    }

    [Fact]
    public void ListRightClickZone_OnlyCoversIconAndLabelBlocks()
    {
        string root = FindRepositoryRoot();
        string surfaceSource = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/FileItemSurface.xaml.cs"));
        string surfaceXaml = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/FileItemSurface.xaml"));
        string routingSource = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));

        // The label block is named so the surface can measure it next to
        // the icon host, and the zone check keeps its slack pinned.
        Assert.Contains(
            "x:Name=\"ListItemTextHost\"",
            surfaceXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "PrimaryContentZoneInflation = 4",
            surfaceSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "internal bool IsPointWithinListPrimaryContent",
            surfaceSource,
            StringComparison.Ordinal);

        // The right-tap divert must run before the item-menu branch and
        // share the blank path (selection clears, content-area menu).
        int divertIndex = routingSource.IndexOf(
            "IsPointWithinListPrimaryContent",
            StringComparison.Ordinal);
        int blankHelperIndex = routingSource.IndexOf(
            "ShowContentAreaFlyoutForBlankTap",
            StringComparison.Ordinal);
        Assert.True(divertIndex >= 0, "right-tap zone divert is missing");
        Assert.True(blankHelperIndex >= 0, "blank-tap helper is missing");
        Assert.Contains(
            "ClearSelection();",
            routingSource[blankHelperIndex..],
            StringComparison.Ordinal);
    }

    [Fact]
    public void RightButtonMarquee_ShowsSelectionMenuOnRelease()
    {
        string root = FindRepositoryRoot();
        string selectionSource = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs"));
        string routingSource = File.ReadAllText(Path.Combine(
            root,
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));

        // Explorer's right-button marquee: the blank-area press filter must
        // admit the right button, and the marquee release must open the
        // menu for the swept selection (blank-area menu when it caught
        // nothing) while a duplicate RightTapped is swallowed.
        Assert.Contains(
            "IsRightButtonPressed",
            selectionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_selectionStartedWithRightButton",
            selectionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "completedRightMarquee",
            selectionSource,
            StringComparison.Ordinal);
        // The marquee must also START from a list row's blank remainder —
        // a full list has no true blank left, which read as "feature
        // missing" before this gate existed. Explorer admits BOTH buttons
        // there, so the gate carries no right-button restriction; instead,
        // the item-container drag is cancelled while the marquee is
        // pending (see the DragStarting cancel pin below).
        Assert.Contains(
            "_selectionPressOnRowBlank",
            selectionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_selectionPressOnRowBlank)",
            routingSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "e.Cancel = true;",
            routingSource,
            StringComparison.Ordinal);

        // The surface must be found by TYPE walk. The DataContext-based
        // FindItemElement returns the innermost element (everything inside
        // a row inherits the item DataContext), so an "is FileItemSurface"
        // check on its result is dead code — the exact bug that made the
        // zone and marquee silently no-ops in the field.
        Assert.Contains(
            "private static FileItemSurface? FindItemSurface",
            selectionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "FindItemSurface(e.OriginalSource)",
            routingSource,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "FindItemElement(e.OriginalSource) is FileItemSurface",
            routingSource + selectionSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "_suppressNextRightTap",
            routingSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShowItemContextMenuForSelection",
            routingSource,
            StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "src",
                    "DeskBox",
                    "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
