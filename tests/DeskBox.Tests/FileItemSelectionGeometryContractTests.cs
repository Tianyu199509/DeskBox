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
