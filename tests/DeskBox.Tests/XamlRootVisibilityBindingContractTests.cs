using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class XamlRootVisibilityBindingContractTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void AllXamlClassRoots_DoNotBindSelfVisibility()
    {
        string srcRoot = TestPaths.FromRepository("src");
        string[] xamlFiles = Directory.GetFiles(srcRoot, "*.xaml", SearchOption.AllDirectories)
            .Where(static path =>
                !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.NotEmpty(xamlFiles);

        List<string> violations = [];
        foreach (string xamlFile in xamlFiles)
        {
            XDocument document = XDocument.Load(xamlFile);
            XElement? root = document.Root;
            if (root is null || root.Attribute(XamlNamespace + "Class") is null)
            {
                continue;
            }

            string? rootVisibility = root.Attribute("Visibility")?.Value;
            if (!string.IsNullOrWhiteSpace(rootVisibility) &&
                (rootVisibility.Contains("{x:Bind", StringComparison.Ordinal) ||
                 rootVisibility.Contains("{Binding", StringComparison.Ordinal)))
            {
                string relativePath = Path.GetRelativePath(srcRoot, xamlFile);
                violations.Add($"src/{relativePath.Replace('\\', '/')}: Visibility=\"{rootVisibility}\"");
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void TodoOverdueSectionBar_SynchronizesVisibilityBeforeFirstMeasureAndBindsInnerGrid()
    {
        string xamlPath = TestPaths.FromRepository("src/DeskBox/Controls/Todo/TodoOverdueSectionBar.xaml");
        string codePath = TestPaths.FromRepository("src/DeskBox/Controls/Todo/TodoOverdueSectionBar.xaml.cs");

        XDocument document = XDocument.Load(xamlPath);
        XElement root = Assert.IsType<XElement>(document.Root);
        Assert.Null(root.Attribute("Visibility"));

        XElement innerGrid = Assert.Single(root.Elements());
        Assert.Equal("Grid", innerGrid.Name.LocalName);
        Assert.Equal("{x:Bind BarVisibility, Mode=OneWay}", innerGrid.Attribute("Visibility")?.Value);

        string code = File.ReadAllText(codePath);
        Assert.Contains("SyncBarVisibility();", code, StringComparison.Ordinal);
        Assert.Contains("Visibility = BarVisibility;", code, StringComparison.Ordinal);
    }
}
