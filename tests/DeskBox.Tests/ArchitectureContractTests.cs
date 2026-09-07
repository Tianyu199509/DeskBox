namespace DeskBox.Tests;

/// <summary>
/// Architecture boundary guards (pluginization roadmap section 7, stage 1.5).
///
/// Zero-assembly-split posture: feature code stays inside the host project, so
/// the boundary is enforced by ratcheted snapshots instead of compiler
/// assemblies. Three surfaces are frozen per feature:
///   1. the file inventory (additions/renames require a deliberate snapshot
///      update here);
///   2. the set of host namespaces feature sources may use (new host-internal
///      dependencies, including any DeskBox.Views usage, are rejected);
///   3. ambient App.Current.WidgetManager access counts (the planned broker
///      migration may only shrink them).
/// The DeskBox.Abstractions contract assembly gets its own purity guard.
/// Aot smoke/fixture partials, SettingsViewModel partials, host window
/// partials (ContentWidgetWindow/QuickCaptureWidgetWindow/WidgetManager) are
/// excluded: they are host-owned until the roadmap moves them.
/// </summary>
public sealed class ArchitectureContractTests
{
    private static readonly string[] SearchDirectories =
    [
        "src/DeskBox/ViewModels",
        "src/DeskBox/Controls/WidgetContents",
        "src/DeskBox/Controls",
        "src/DeskBox/Views/SettingsSections",
        "src/DeskBox/Views",
        "src/DeskBox/Services",
        "src/DeskBox/Helpers",
        "src/DeskBox/Models",
    ];

    private static readonly string[] HostNamespaceAllowList =
    [
        "DeskBox.Contracts",
        "DeskBox.Controls",
        "DeskBox.Controls.WidgetContents",
        "DeskBox.Helpers",
        "DeskBox.Models",
        "DeskBox.Services",
        "DeskBox.ViewModels",
    ];

    private sealed record FeatureSpec(
        string Name,
        string[] NameTokens,
        string[] NameTokenExclusions,
        string[] ExtraFiles);

    private static readonly FeatureSpec[] Features =
    [
        new("Weather", ["Weather", "CitySearch"], [], []),
        new("Todo", ["Todo"], [], []),
        new("Music", ["Music"], [], []),
        new("Glance", ["Glance"], [],
        [
            "src/DeskBox/Services/LocalCalendarPresentationSource.cs",
            "src/DeskBox/Services/SystemFontCatalogService.cs",
        ]),
        new("Search", ["Search"], ["CitySearch"], []),
        new("QuickCapture", ["QuickCapture"], [], []),
    ];

    private static readonly Dictionary<string, int> FrozenFeatureFileCounts =
        new(StringComparer.Ordinal)
        {
            ["Weather"] = 16,
            ["Todo"] = 36,
            ["Music"] = 13,
            ["Glance"] = 19,
            ["Search"] = 20,
            ["QuickCapture"] = 22,
        };

    private static readonly Dictionary<string, int> FrozenAmbientWidgetManagerAccess =
        new(StringComparer.Ordinal)
        {
            ["src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs"] = 9,
            ["src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.Operations.cs"] = 1,
        };

    [Fact]
    public void FeatureFileInventory_MatchesFrozenCounts()
    {
        foreach (FeatureSpec feature in Features)
        {
            string[] files = EnumerateFeatureFiles(feature).Order().ToArray();
            int expected = FrozenFeatureFileCounts[feature.Name];
            Assert.True(
                files.Length == expected,
                $"{feature.Name}: expected {expected} feature source files, found {files.Length}. " +
                "If this change is intentional, update FrozenFeatureFileCounts deliberately. Files:\n" +
                string.Join('\n', files));
        }
    }

    [Fact]
    public void FeatureSources_StayWithinFrozenHostNamespaceSet()
    {
        foreach (FeatureSpec feature in Features)
        {
            foreach (string file in EnumerateFeatureFiles(feature))
            {
                foreach (string usingDirective in ExtractDeskBoxUsings(file))
                {
                    Assert.True(
                        HostNamespaceAllowList.Contains(usingDirective, StringComparer.Ordinal),
                        $"{file}: feature code may not depend on host namespace '{usingDirective}'. " +
                        "Feature sources must stay within " + string.Join(", ", HostNamespaceAllowList) +
                        " (DeskBox.Views is host-owned; new host namespaces require a roadmap decision " +
                        "and a deliberate allow-list update).");
                }
            }
        }
    }

    [Fact]
    public void FeatureSources_AmbientWidgetManagerAccessStaysAtFrozenCounts()
    {
        var actual = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (FeatureSpec feature in Features)
        {
            foreach (string file in EnumerateFeatureFiles(feature))
            {
                int count = CountOccurrences(file, "App.Current.WidgetManager") +
                            CountOccurrences(file, "App.Current?.WidgetManager");
                if (count > 0)
                {
                    actual[RepositoryRelative(file)] = count;
                }
            }
        }

        Assert.Equal(
            FrozenAmbientWidgetManagerAccess.Keys.Order(),
            actual.Keys.Order());
        foreach ((string file, int expected) in FrozenAmbientWidgetManagerAccess)
        {
            Assert.Equal(expected, actual[file]);
        }
    }

    [Fact]
    public void AbstractionsAssembly_StaysWithinContractNamespaces()
    {
        string[] allowedPrefixes =
        [
            "System",
            "Microsoft",
            "WinRT",
            "DeskBox.Models",
            "DeskBox.Contracts",
            "DeskBox.Services",
        ];

        string root = TestPaths.FromRepository("src/DeskBox.Abstractions");
        foreach (string file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            foreach (string usingDirective in ExtractDeskBoxUsings(file))
            {
                Assert.True(
                    allowedPrefixes.Contains(usingDirective, StringComparer.Ordinal),
                    $"{file}: the Abstractions contract assembly may not depend on '{usingDirective}'. " +
                    "Host namespaces would make the contract assembly circular.");
            }
        }
    }

    private static IEnumerable<string> EnumerateFeatureFiles(FeatureSpec feature)
    {
        foreach (string directory in SearchDirectories)
        {
            string directoryPath = TestPaths.FromRepository(directory);
            if (!Directory.Exists(directoryPath))
            {
                continue;
            }

            foreach (string path in Directory.EnumerateFiles(directoryPath, "*.cs"))
            {
                string fileName = Path.GetFileName(path);
                if (IsExcludedFileName(fileName))
                {
                    continue;
                }

                bool matches = feature.NameTokens.Any(token =>
                    fileName.Contains(token, StringComparison.Ordinal));
                bool excluded = feature.NameTokenExclusions.Any(token =>
                    fileName.Contains(token, StringComparison.Ordinal));
                if (matches && !excluded)
                {
                    yield return path;
                }
            }
        }

        foreach (string extra in feature.ExtraFiles)
        {
            string path = TestPaths.FromRepository(extra);
            Assert.True(File.Exists(path), $"Missing declared feature file: {extra}");
            yield return path;
        }
    }

    private static bool IsExcludedFileName(string fileName) =>
        (fileName.Contains(".Aot", StringComparison.Ordinal) &&
            fileName.EndsWith("Smoke.cs", StringComparison.Ordinal)) ||
        fileName.StartsWith("Aot", StringComparison.Ordinal) &&
            fileName.EndsWith("Fixture.cs", StringComparison.Ordinal) ||
        fileName.StartsWith("SettingsViewModel.", StringComparison.Ordinal) ||
        fileName.StartsWith("ContentWidgetWindow.", StringComparison.Ordinal) ||
        fileName.StartsWith("QuickCaptureWidgetWindow.", StringComparison.Ordinal) ||
        fileName.StartsWith("WidgetManager.", StringComparison.Ordinal);

    private static IEnumerable<string> ExtractDeskBoxUsings(string file)
    {
        foreach (string line in File.ReadLines(file))
        {
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith("using DeskBox.", StringComparison.Ordinal))
            {
                continue;
            }

            int end = trimmed.IndexOf(';');
            if (end > 0)
            {
                yield return trimmed["using ".Length..end];
            }
        }
    }

    private static int CountOccurrences(string file, string token)
    {
        int count = 0;
        int index = 0;
        string content = File.ReadAllText(file);
        while ((index = content.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string RepositoryRelative(string path) =>
        Path.GetRelativePath(TestPaths.FromRepository("."), path)
            .Replace(Path.DirectorySeparatorChar, '/');
}
