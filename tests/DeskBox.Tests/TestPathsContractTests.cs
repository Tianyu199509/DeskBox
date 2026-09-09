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
    public void EverySrcProject_IsReachableFromTheHostOrTestBuildGraph()
    {
        // ci.yml compiles DeskBox.csproj (RID-pinned solution builds are
        // rejected by NETSDK1134), so compilation coverage for new projects
        // comes from the ProjectReference graph. Any project not reachable
        // from the host or the test project is never compiled by CI and must
        // be wired up (or given its own pipeline) deliberately.
        string repositoryRoot = TestPaths.FromRepository(".");
        string[] srcProjects = Directory
            .GetDirectories(Path.Combine(repositoryRoot, "src"))
            .SelectMany(directory => Directory.GetFiles(directory, "*.csproj"))
            .Select(path => Path.GetFullPath(path))
            .ToArray();

        var reachable = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(Path.GetFullPath(Path.Combine(repositoryRoot, "src/DeskBox/DeskBox.csproj")));
        queue.Enqueue(Path.GetFullPath(Path.Combine(repositoryRoot, "tests/DeskBox.Tests/DeskBox.Tests.csproj")));
        while (queue.Count > 0)
        {
            string project = queue.Dequeue();
            if (!reachable.Add(project) || !File.Exists(project))
            {
                continue;
            }

            foreach (string line in File.ReadLines(project))
            {
                const string marker = "ProjectReference Include=\"";
                int index = line.IndexOf(marker, StringComparison.Ordinal);
                if (index < 0)
                {
                    continue;
                }

                int closing = line.IndexOf('"', index + marker.Length);
                if (closing < 0)
                {
                    continue;
                }

                string relative = line[(index + marker.Length)..closing];
                queue.Enqueue(Path.GetFullPath(
                    Path.Combine(Path.GetDirectoryName(project)!, relative)));
            }
        }

        // Native package projects are deliberately NOT referenced by the host
        // (loaded at runtime via the native ABI, not compiled in). They have
        // their own build pipeline (scripts/spike/build-official-glance.ps1).
        string[] deliberatelyUnreachable =
        [
            Path.GetFullPath(Path.Combine(repositoryRoot, "src/DeskBox.GlancePackage/DeskBox.GlancePackage.csproj")),
        ];

        foreach (string project in srcProjects)
        {
            Assert.True(
                reachable.Contains(project) || deliberatelyUnreachable.Contains(project, StringComparer.OrdinalIgnoreCase),
                $"{project} is not reachable from the host or test build graph; CI never compiles it. " +
                "Add a ProjectReference or a dedicated pipeline step deliberately.");
        }
    }

    [Fact]
    public void PinnedFeatureWidgetsReads_GoThroughTheRelocationMap()
    {
        const string pinnedPath = "src/DeskBox/Services/WidgetManager.FeatureWidgets.cs";
        string testsRoot = TestPaths.FromRepository("tests/DeskBox.Tests");
        string[] adoptingFiles = Directory
            .EnumerateFiles(testsRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => path.Replace(Path.DirectorySeparatorChar, '/')
                .Contains("/obj/", StringComparison.OrdinalIgnoreCase) is false)
            .Where(file =>
                File.ReadAllText(file).Contains(pinnedPath, StringComparison.Ordinal) &&
                File.ReadAllText(file).Contains("TestPaths.SourceFile", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        // Five contract tests pin FeatureWidgets source content; they must read
        // it through TestPaths.SourceFile so a future move needs one relocation
        // entry instead of five test edits.
        Assert.True(
            adoptingFiles.Length >= 5,
            $"Expected at least 5 test files reading '{pinnedPath}' via TestPaths.SourceFile, " +
            $"found {adoptingFiles.Length}: {string.Join(", ", adoptingFiles)}");
    }

    [Fact]
    public void SolutionFile_ContainsEveryProjectUnderSrc()
    {
        string repositoryRoot = TestPaths.FromRepository(".");
        string solution = File.ReadAllText(Path.Combine(repositoryRoot, "DeskBox.sln"));
        string[] srcProjects = Directory
            .GetDirectories(Path.Combine(repositoryRoot, "src"))
            .SelectMany(directory => Directory.GetFiles(directory, "*.csproj"))
            .Select(path => Path.GetRelativePath(repositoryRoot, path)
                .Replace(Path.DirectorySeparatorChar, '\\'))
            .ToArray();

        Assert.NotEmpty(srcProjects);
        foreach (string projectPath in srcProjects)
        {
            Assert.Contains(projectPath, solution, StringComparison.OrdinalIgnoreCase);
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
