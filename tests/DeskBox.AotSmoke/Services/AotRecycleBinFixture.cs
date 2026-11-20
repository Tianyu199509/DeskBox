#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
namespace DeskBox.Services;

internal static class AotRecycleBinFixture
{
    internal const string Scenario = "RecycleBinMenuPersistenceRestart";
    internal const string PhaseEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_RECYCLE_BIN_PHASE";
    internal const string RunIdEnvironmentVariable =
        "DESKBOX_AOT_MANAGED_UI_RECYCLE_BIN_RUN_ID";
    internal const string OwnedWidgetId = "aot-5b4c1b1-file";
    internal const string FixtureDirectoryName = "recycle-bin-menu";
    internal const string WidgetRootDirectoryName = "widget-root";
    internal const string BaselineName = "baseline";

    internal static AotRecycleBinFixturePaths GetOwnedPaths(
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
            phase is not "Mutate" and
                not "VerifyRestore" and
                not "Postflight" and
                not "Compensate" ||
            !IsValidRunId(runId))
        {
            throw new InvalidOperationException(
                "The owned Recycle Bin fixture is unavailable outside its exact AOT scenario, phase, and run identity.");
        }

        string fixtureRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            "fixtures",
            FixtureDirectoryName));
        string widgetRoot = Path.GetFullPath(Path.Combine(
            fixtureRoot,
            WidgetRootDirectoryName));
        if (!Directory.Exists(fixtureRoot) ||
            !Directory.Exists(widgetRoot) ||
            !AotLocalFileSurfaceFixture.IsPathEqualOrInside(
                dataPaths.RootPath,
                fixtureRoot) ||
            !AotLocalFileSurfaceFixture.IsPathEqualOrInside(
                fixtureRoot,
                widgetRoot))
        {
            throw new InvalidOperationException(
                "The owned Recycle Bin fixture escaped or is missing from the isolated preview root.");
        }

        string singleName = $"single-{runId}";
        string multiFileName = $"multi-file-{runId}";
        string multiFolderName = $"multi-folder-{runId}";
        string folderPayloadName = $"payload-{runId}";
        return new AotRecycleBinFixturePaths(
            runId!,
            fixtureRoot,
            widgetRoot,
            Path.Combine(widgetRoot, BaselineName),
            singleName,
            Path.Combine(widgetRoot, singleName),
            multiFileName,
            Path.Combine(widgetRoot, multiFileName),
            multiFolderName,
            Path.Combine(widgetRoot, multiFolderName),
            Path.Combine(widgetRoot, multiFolderName, folderPayloadName));
    }

    private static bool IsValidRunId(string? value)
    {
        return value is { Length: 32 } &&
            value.All(character =>
                character is >= '0' and <= '9' or
                    >= 'a' and <= 'f');
    }
}

internal sealed record AotRecycleBinFixturePaths(
    string RunId,
    string FixtureRoot,
    string WidgetRoot,
    string BaselinePath,
    string SingleName,
    string SinglePath,
    string MultiFileName,
    string MultiFilePath,
    string MultiFolderName,
    string MultiFolderPath,
    string MultiFolderPayloadPath)
{
    internal IReadOnlyList<AotRecycleBinOwnedItem> OwnedItems =>
    [
        new(SingleName, SinglePath, IsFolder: false),
        new(MultiFileName, MultiFilePath, IsFolder: false),
        new(MultiFolderName, MultiFolderPath, IsFolder: true)
    ];
}

internal sealed record AotRecycleBinOwnedItem(
    string Name,
    string Path,
    bool IsFolder);
#endif
