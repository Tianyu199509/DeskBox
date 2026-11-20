#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
namespace DeskBox.Services;

internal static class AotLocalFileSurfaceFixture
{
    internal const string Scenario = "LocalFileSurfacePersistenceRestart";
    internal const string PhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_LOCAL_FILE_PHASE";
    internal const string OwnedWidgetId = "aot-5b4c1a-file";
    internal const string FixtureDirectoryName = "local-file-surface";
    internal const string WidgetRootDirectoryName = "widget-root";
    internal const string SourceDirectoryName = "sources";
    internal const string BaselineFileName = "baseline.txt";
    internal const string NestedDirectoryName = "nested";
    internal const string NestedFileName = "nested.txt";
    internal const string CopySourceFileName = "copy-source.txt";
    internal const string MoveSourceFileName = "move-source.txt";
    internal const string RenamedCopyFileName = "copied-renamed.txt";
    internal const string WatcherCreatedFileName = "watcher-created.txt";

    internal static AotLocalFileFixturePaths GetOwnedPaths(
        DeskBoxDataPathService dataPaths)
    {
        ArgumentNullException.ThrowIfNull(dataPaths);

        string? scenario = Environment.GetEnvironmentVariable(
            "DESKBOX_AOT_MANAGED_UI_SMOKE");
        string? phase = Environment.GetEnvironmentVariable(
            PhaseEnvironmentVariable);
        if (!string.Equals(scenario, Scenario, StringComparison.Ordinal) ||
            phase is not "Mutate" and not "VerifyRestore" and not "Postflight")
        {
            throw new InvalidOperationException(
                "The owned local-file fixture is unavailable outside its exact AOT scenario and phase.");
        }

        string fixtureRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            "fixtures",
            FixtureDirectoryName));
        string widgetRoot = Path.GetFullPath(Path.Combine(
            fixtureRoot,
            WidgetRootDirectoryName));
        string sourceRoot = Path.GetFullPath(Path.Combine(
            fixtureRoot,
            SourceDirectoryName));

        if (!Directory.Exists(fixtureRoot) ||
            !Directory.Exists(widgetRoot) ||
            !Directory.Exists(sourceRoot) ||
            !IsPathEqualOrInside(dataPaths.RootPath, fixtureRoot) ||
            !IsPathEqualOrInside(fixtureRoot, widgetRoot) ||
            !IsPathEqualOrInside(fixtureRoot, sourceRoot))
        {
            throw new InvalidOperationException(
                "The owned local-file fixture escaped or is missing from the isolated preview root.");
        }

        return new AotLocalFileFixturePaths(
            fixtureRoot,
            widgetRoot,
            sourceRoot,
            Path.Combine(widgetRoot, BaselineFileName),
            Path.Combine(widgetRoot, NestedDirectoryName),
            Path.Combine(widgetRoot, NestedDirectoryName, NestedFileName),
            Path.Combine(sourceRoot, CopySourceFileName),
            Path.Combine(sourceRoot, MoveSourceFileName),
            Path.Combine(widgetRoot, CopySourceFileName),
            Path.Combine(widgetRoot, MoveSourceFileName),
            Path.Combine(widgetRoot, RenamedCopyFileName),
            Path.Combine(widgetRoot, WatcherCreatedFileName));
    }

    internal static bool IsPathEqualOrInside(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return normalizedCandidate.Equals(
                normalizedRoot,
                StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record AotLocalFileFixturePaths(
    string FixtureRoot,
    string WidgetRoot,
    string SourceRoot,
    string BaselineFile,
    string NestedDirectory,
    string NestedFile,
    string CopySourceFile,
    string MoveSourceFile,
    string CopiedFile,
    string MovedFile,
    string RenamedCopyFile,
    string WatcherCreatedFile);
#endif
