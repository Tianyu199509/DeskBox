namespace DeskBox.Tests;

internal static class TestPaths
{
    public static string FromRepository(string relativePath) =>
        Path.Combine(FindRepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Resolves a production source file path through the central relocation map.
    /// When a production file moves between projects (pluginization Step 1+), add a
    /// single entry to <see cref="SourceRelocations"/> instead of updating every
    /// contract test that pins the file by path.
    /// </summary>
    public static string SourceFile(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        if (SourceRelocations.TryGetValue(normalized, out string? relocated))
        {
            string relocatedPath = FromRepository(relocated);
            if (File.Exists(relocatedPath))
            {
                return relocatedPath;
            }

            throw new FileNotFoundException(
                $"Source relocation '{normalized}' -> '{relocated}' points to a missing file. " +
                "Remove or update the stale entry in TestPaths.SourceRelocations.",
                relocatedPath);
        }

        return FromRepository(normalized);
    }

    /// <summary>
    /// Central relocation map for production sources that have moved between
    /// projects. Keys and values are repository-relative paths with forward
    /// slashes. Entries are only added when files physically move; stale entries
    /// fail fast in <see cref="SourceFile"/> and in TestPathsContractTests.
    /// </summary>
    private static readonly Dictionary<string, string> SourceRelocations =
        new(StringComparer.Ordinal)
        {
            // Step 1 (Abstractions): widget contract files moved from the host
            // assembly; namespaces are unchanged, only paths moved.
            ["src/DeskBox/Contracts/IWidgetContent.cs"] = "src/DeskBox.Abstractions/Contracts/IWidgetContent.cs",
            ["src/DeskBox/Contracts/IWidgetAddActionContent.cs"] = "src/DeskBox.Abstractions/Contracts/IWidgetAddActionContent.cs",
            ["src/DeskBox/Contracts/IWidgetFeedbackSource.cs"] = "src/DeskBox.Abstractions/Contracts/IWidgetFeedbackSource.cs",
            ["src/DeskBox/Contracts/IWidgetHostContextMenuSource.cs"] = "src/DeskBox.Abstractions/Contracts/IWidgetHostContextMenuSource.cs",
            ["src/DeskBox/Contracts/IWidgetTransientStateContent.cs"] = "src/DeskBox.Abstractions/Contracts/IWidgetTransientStateContent.cs",
            ["src/DeskBox/Contracts/ICalendarPresentationSource.cs"] = "src/DeskBox.Abstractions/Contracts/ICalendarPresentationSource.cs",
            ["src/DeskBox/Models/WidgetConfig.cs"] = "src/DeskBox.Abstractions/Models/WidgetConfig.cs",
            ["src/DeskBox/Models/WidgetFeedback.cs"] = "src/DeskBox.Abstractions/Models/WidgetFeedback.cs",
            ["src/DeskBox/Services/WidgetContentDescriptor.cs"] = "src/DeskBox.Abstractions/Services/WidgetContentDescriptor.cs",
            ["src/DeskBox/Services/WidgetChromeMode.cs"] = "src/DeskBox.Abstractions/Services/WidgetChromeMode.cs",
        };

    /// <summary>Read-only view for TestPathsContractTests invariants.</summary>
    internal static IReadOnlyDictionary<string, string> SourceRelocationMap => SourceRelocations;

    /// <summary>
    /// Production source roots scanned by repository-wide contract tests
    /// (JSON serialization baseline, retail smoke isolation, ...). Every project
    /// added under src/ must be registered here so the contract tests keep
    /// seeing its sources; TestPathsContractTests fails when a src/* project
    /// is missing from this list.
    /// </summary>
    public static IReadOnlyList<string> ProductionSourceRoots()
    {
        string repositoryRoot = FindRepositoryRoot();
        return
        [
            "src/DeskBox",
            "src/DeskBox.Abstractions",
            "src/DeskBox.Updater",
        ];
    }

    /// <summary>
    /// Enumerates production .cs files across all <see cref="ProductionSourceRoots"/>,
    /// excluding build outputs (bin/obj/AppPackages) inside each root.
    /// </summary>
    public static IEnumerable<string> EnumerateProductionSourceFiles()
    {
        foreach (string root in ProductionSourceRoots())
        {
            string rootPath = FromRepository(root);
            foreach (string path in Directory.EnumerateFiles(
                         rootPath,
                         "*.cs",
                         SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(rootPath, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("AppPackages/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return path;
            }
        }
    }

    /// <summary>
    /// Enumerates production .xaml files across all <see cref="ProductionSourceRoots"/>,
    /// excluding build outputs (bin/obj/AppPackages) inside each root.
    /// </summary>
    public static IEnumerable<string> EnumerateProductionXamlFiles()
    {
        foreach (string root in ProductionSourceRoots())
        {
            string rootPath = FromRepository(root);
            foreach (string path in Directory.EnumerateFiles(
                         rootPath,
                         "*.xaml",
                         SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(rootPath, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("AppPackages/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return path;
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "DeskBox", "DeskBox.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the DeskBox repository root.");
    }
}
