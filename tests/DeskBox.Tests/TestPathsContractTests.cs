namespace DeskBox.Tests;

/// <summary>
/// Guards for the Phase 0 guardrail infrastructure itself (pluginization
/// roadmap section 7, stage 0). These tests fail when a new project is added
/// under src/ without being registered in <see cref="TestPaths.ProductionSourceRoots"/>,
/// or when a relocation entry goes stale after a file move.
/// </summary>
public sealed class TestPathsContractTests
{
    [Fact]
    public void ProductionSourceRoots_CoverEveryProjectUnderSrc()
    {
        string repositoryRoot = TestPaths.FromRepository(".");
        string[] srcProjectDirectories = Directory
            .GetDirectories(Path.Combine(repositoryRoot, "src"))
            .SelectMany(directory => Directory.GetFiles(directory, "*.csproj"))
            .Select(path => Path.GetDirectoryName(path)!)
            .Select(directory => Path.GetRelativePath(repositoryRoot, directory)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToArray();

        Assert.NotEmpty(srcProjectDirectories);
        Assert.Contains("src/DeskBox", srcProjectDirectories);

        foreach (string projectDirectory in srcProjectDirectories)
        {
            Assert.Contains(
                TestPaths.ProductionSourceRoots(),
                root => string.Equals(root, projectDirectory, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void ProductionSourceRoots_ExistAndContainSources()
    {
        foreach (string root in TestPaths.ProductionSourceRoots())
        {
            string rootPath = TestPaths.FromRepository(root);
            Assert.True(Directory.Exists(rootPath), $"Missing production source root: {root}");
            Assert.True(
                Directory.EnumerateFiles(rootPath, "*.cs", SearchOption.AllDirectories).Any(),
                $"Production source root has no .cs files: {root}");
        }
    }

    [Fact]
    public void SourceRelocations_PointToExistingFilesAndLeaveNoStaleOriginals()
    {
        foreach ((string original, string relocated) in TestPaths.SourceRelocationMap)
        {
            Assert.True(
                File.Exists(TestPaths.FromRepository(relocated)),
                $"Relocation target is missing: {original} -> {relocated}");
            Assert.False(
                File.Exists(TestPaths.FromRepository(original)),
                $"Stale relocation: original still exists after move ({original} -> {relocated}). " +
                "Remove or update the entry.");
        }
    }

    [Fact]
    public void EnumerateProductionSourceFiles_ExcludesBuildOutputsAndIncludesAppSources()
    {
        string[] files = TestPaths.EnumerateProductionSourceFiles().ToArray();
        string Normalize(string path) => path.Replace(Path.DirectorySeparatorChar, '/');

        Assert.Contains(
            files,
            path => Normalize(path).EndsWith("src/DeskBox/App.xaml.cs", StringComparison.Ordinal));
        Assert.DoesNotContain(
            files,
            path => Normalize(path).Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                    Normalize(path).Contains("/obj/", StringComparison.OrdinalIgnoreCase));
    }
}
