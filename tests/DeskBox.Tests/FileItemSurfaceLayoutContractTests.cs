using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class FileItemSurfaceLayoutContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void InteractionShell_RemainsEagerAndKeepsItsHitTestAndPointerContract()
    {
        XElement surface = LoadSurface().Root!;
        XElement border = surface.Element(Presentation + "Border")!;

        Assert.Equal("SurfaceBorder", (string?)border.Attribute(Xaml + "Name"));
        Assert.Equal("InteractiveSurface", (string?)border.Attribute("Tag"));
        Assert.Equal("Transparent", (string?)border.Attribute("Background"));
        Assert.Null(border.Attribute("DataContext"));
        Assert.Null(border.Attribute(Xaml + "Load"));
        Assert.Null(border.Attribute("Margin"));
        Assert.Null(border.Attribute("Padding"));
        Assert.Equal("Stretch", (string?)border.Attribute("HorizontalAlignment"));
        string codeBehind = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Controls/FileItemSurface.xaml.cs"));
        Assert.Contains("ApplyLayoutMetrics()", codeBehind, StringComparison.Ordinal);

        foreach (string eventName in new[]
        {
            "Loaded", "Unloaded", "PointerEntered", "PointerExited",
            "PointerPressed", "PointerReleased", "PointerCaptureLost"
        })
        {
            Assert.Equal("SurfaceBorder_" + eventName, (string?)border.Attribute(eventName));
        }

        // The outer hit target survives independently of either presentation.
        XElement layoutHost = border.Element(Presentation + "Grid")!;
        Assert.Null(layoutHost.Attribute("DataContext"));
        Assert.Null(layoutHost.Attribute(Xaml + "Load"));
        Assert.Empty(layoutHost.Elements());
    }

    [Theory]
    [InlineData("IconItemLayoutTemplate", "IconItemNameText", "IconLayoutVisibility")]
    [InlineData("ListItemLayoutTemplate", "ListItemNameText", "ListLayoutVisibility")]
    public void Presentations_KeepFileIdentitySeparateFromSurfacePresentationBindings(
        string templateKey,
        string nameElement,
        string visibilityProperty)
    {
        XElement template = GetTemplate(templateKey);
        Assert.Equal("controls:FileItemSurface", (string?)template.Attribute(Xaml + "DataType"));
        XElement layout = template.Elements().Single();
        Assert.Equal("{x:Bind " + visibilityProperty + ", Mode=OneWay}",
            (string?)layout.Attribute("Visibility"));

        // File operations resolve WidgetItem through the inherited DataContext.
        // A presentation must never replace it with a layout wrapper or owner.
        Assert.DoesNotContain(layout.DescendantsAndSelf(), element => element.Attribute("DataContext") is not null);
        XElement image = layout.Descendants(Presentation + "Image").Single();
        Assert.Equal("{Binding Icon}", (string?)image.Attribute("Source"));
        Assert.Equal("{Binding IconVisibility}", (string?)image.Attribute("Visibility"));
        XElement name = layout.Descendants(Presentation + "TextBlock").Single(element =>
            (string?)element.Attribute(Xaml + "Name") == nameElement);
        Assert.Equal("{Binding Name}", (string?)name.Attribute("Text"));
    }

    [Theory]
    [InlineData("IconItemLayoutTemplate")]
    [InlineData("ListItemLayoutTemplate")]
    public void Presentations_DoNotIntroduceInputHandlersAndKeepActivityBadgeNonInteractive(string templateKey)
    {
        XElement layout = GetTemplate(templateKey).Elements().Single();
        string[] inputAttributes =
        [
            "AllowDrop", "DragOver", "DragEnter", "DragLeave", "Drop", "DragStarting",
            "PointerPressed", "PointerReleased", "PointerMoved", "Tapped", "DoubleTapped"
        ];
        Assert.DoesNotContain(layout.DescendantsAndSelf().Attributes(), attribute =>
            inputAttributes.Contains(attribute.Name.LocalName, StringComparer.Ordinal));

        XElement badge = layout.Descendants(Presentation + "Border").Single(element =>
            (string?)element.Attribute("Visibility") == "{x:Bind ActivityBadgeVisibility, Mode=OneWay}");
        Assert.Equal("False", (string?)badge.Attribute("IsHitTestVisible"));
        XElement ring = badge.Element(Presentation + "ProgressRing")!;
        Assert.Equal("{x:Bind IsActivityActive, Mode=OneWay}", (string?)ring.Attribute("IsActive"));
    }

    private static XElement GetTemplate(string key) => LoadSurface()
        .Descendants(Presentation + "DataTemplate")
        .Single(element => (string?)element.Attribute(Xaml + "Key") == key);

    private static XDocument LoadSurface() => XDocument.Load(TestPaths.FromRepository(
        "src/DeskBox/Controls/FileItemSurface.xaml"));
}
