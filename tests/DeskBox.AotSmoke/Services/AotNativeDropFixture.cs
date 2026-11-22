#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
namespace DeskBox.Services;

internal static class AotNativeDropFixture
{
    internal const string Scenario = "NativeDropPersistenceRestart";
    internal const string PhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_NATIVE_DROP_PHASE";
    internal const string RunIdEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_NATIVE_DROP_RUN_ID";
    internal const string OwnedWidgetId = "aot-5b4c1c2a-file";
    internal const string FixtureDirectoryName = "native-drop";
    internal const string WidgetRootDirectoryName = "widget-root";
    internal const string SourceDirectoryName = "sources";
    internal const string BaselineFileName = "baseline.txt";
    internal const string TargetFolderName = "target-folder";
    internal const string CopyLargeFileName = "copy-large.bin";
    internal const string CopyFolderName = "copy-folder";
    internal const string MoveFileName = "move-small.txt";
    internal const string MoveFolderName = "move-folder";
    internal const string NestedPayloadName = "payload.txt";

    internal static AotNativeDropFixturePaths GetOwnedPaths(
        DeskBoxDataPathService dataPaths)
    {
        ArgumentNullException.ThrowIfNull(dataPaths);

        string? scenario = Environment.GetEnvironmentVariable(
            "DESKBOX_AOT_MANAGED_UI_SMOKE");
        string? phase = Environment.GetEnvironmentVariable(
            PhaseEnvironmentVariable);
        string? runId = Environment.GetEnvironmentVariable(
            RunIdEnvironmentVariable);
        if (!string.Equals(scenario, Scenario, StringComparison.Ordinal) ||
            phase is not "Mutate" and not "VerifyRestore" and not "Postflight" ||
            runId is not { Length: 32 } ||
            runId.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            throw new InvalidOperationException(
                "The native-drop fixture requires its exact AOT scenario, phase and lowercase run ID.");
        }

        string? configuredPreviewRoot = Environment.GetEnvironmentVariable(
            DeskBoxDataPathService.AotPreviewRootEnvironmentVariable);
        string fixtureRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            "fixtures",
            FixtureDirectoryName,
            runId));
        string widgetRoot = Path.GetFullPath(Path.Combine(
            fixtureRoot,
            WidgetRootDirectoryName));
        string sourceRoot = Path.GetFullPath(Path.Combine(
            fixtureRoot,
            SourceDirectoryName));

        if (!dataPaths.IsDevelopmentRoot ||
            string.IsNullOrWhiteSpace(configuredPreviewRoot) ||
            !IsPathEqual(dataPaths.RootPath, configuredPreviewRoot) ||
            !Directory.Exists(fixtureRoot) ||
            !Directory.Exists(widgetRoot) ||
            !Directory.Exists(sourceRoot) ||
            !IsPathEqualOrInside(dataPaths.RootPath, fixtureRoot) ||
            !IsPathEqualOrInside(fixtureRoot, widgetRoot) ||
            !IsPathEqualOrInside(fixtureRoot, sourceRoot))
        {
            throw new InvalidOperationException(
                "The native-drop fixture escaped or is missing from the isolated preview root.");
        }

        return new AotNativeDropFixturePaths(
            runId,
            phase,
            fixtureRoot,
            widgetRoot,
            sourceRoot,
            Path.Combine(widgetRoot, BaselineFileName),
            Path.Combine(widgetRoot, TargetFolderName),
            Path.Combine(sourceRoot, CopyLargeFileName),
            Path.Combine(sourceRoot, CopyFolderName),
            Path.Combine(sourceRoot, CopyFolderName, NestedPayloadName),
            Path.Combine(sourceRoot, MoveFileName),
            Path.Combine(sourceRoot, MoveFolderName),
            Path.Combine(sourceRoot, MoveFolderName, NestedPayloadName),
            Path.Combine(widgetRoot, CopyLargeFileName),
            Path.Combine(widgetRoot, CopyFolderName),
            Path.Combine(widgetRoot, CopyFolderName, NestedPayloadName),
            Path.Combine(widgetRoot, MoveFileName),
            Path.Combine(widgetRoot, MoveFolderName),
            Path.Combine(widgetRoot, MoveFolderName, NestedPayloadName));
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

    private static bool IsPathEqual(string left, string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed record AotNativeDropFixturePaths(
    string RunId,
    string Phase,
    string FixtureRoot,
    string WidgetRoot,
    string SourceRoot,
    string BaselineFile,
    string TargetFolder,
    string CopyLargeSourceFile,
    string CopySourceFolder,
    string CopySourceNestedFile,
    string MoveSourceFile,
    string MoveSourceFolder,
    string MoveSourceNestedFile,
    string CopyDestinationFile,
    string CopyDestinationFolder,
    string CopyDestinationNestedFile,
    string MoveDestinationFile,
    string MoveDestinationFolder,
    string MoveDestinationNestedFile);
#endif
