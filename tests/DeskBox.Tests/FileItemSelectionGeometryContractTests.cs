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
            "Mode == FileItemSurfaceMode.List\n            ? HorizontalAlignment.Left\n            : HorizontalAlignment.Stretch",
            source.ReplaceLineEndings("\n"),
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
