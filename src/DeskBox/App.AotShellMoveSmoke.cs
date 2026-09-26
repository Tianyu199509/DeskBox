#if DESKBOX_NATIVE_AOT
using System.Security.Cryptography;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private async Task CaptureAotManagedUiShellMoveAsync(
        AotManagedUiSmokeResult result,
        string phase)
    {
        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotLocalFileSurfaceHost host =
            await manager.GetAotLocalFileSurfaceHostAsync(
                AotShellMoveFixture.OwnedWidgetId);
        RequireAotManagedUi(
            result,
            host.WindowHandle != 0 && host.HasXamlRoot && host.Visible,
            "ShellMoveSurfaceHostReady",
            "The real Shell move File Widget HWND or XamlRoot is unavailable.");

        AotShellMoveFixturePaths paths =
            AotShellMoveFixture.GetOwnedPaths(DeskBoxDataPathService.Current);
        RequireAotManagedUi(
            result,
            IsAotManagedUiPathEqual(
                host.ViewModel.MappedFolderPath ?? string.Empty,
                paths.WidgetRoot),
            "ShellMoveOwnedRootsVerified",
            "The real File Widget is not mapped to the exact owned Shell move source root.");

        AotManagedUiShellMoveEvidence evidence = result.ShellMove ??
            throw new InvalidOperationException(
                "Shell move persistence evidence is unavailable.");
        evidence.RunId = paths.RunId;
        evidence.WindowHandle = host.WindowHandle;
        evidence.HasXamlRoot = host.HasXamlRoot;
        evidence.Visible = host.Visible;

        switch (phase)
        {
            case "Mutate":
                evidence.Before = await CaptureAotShellMoveStateAsync(
                    host,
                    paths,
                    ShellMoveBaselineSourceNames(paths));
                RequireAotShellMoveState(
                    result,
                    evidence.Before,
                    paths,
                    expectedDestinationNames: [],
                    expectedHistoryItemCounts: [],
                    "ShellMoveOwnedBaselineVerified");
                evidence.Operations = await MoveAotShellItemsThroughMenusAsync(
                    result,
                    host,
                    paths);
                evidence.After = await CaptureAotShellMoveStateAsync(
                    host,
                    paths,
                    ShellMoveMutatedSourceNames(paths));
                RequireAotShellMoveState(
                    result,
                    evidence.After,
                    paths,
                    ShellMoveMutatedDestinationNames(paths),
                    [1, 0, 1, 1],
                    "ShellMoveMutationApplied");
                break;

            case "VerifyRestore":
                evidence.Before = await CaptureAotShellMoveStateAsync(
                    host,
                    paths,
                    ShellMoveMutatedSourceNames(paths));
                RequireAotShellMoveState(
                    result,
                    evidence.Before,
                    paths,
                    ShellMoveMutatedDestinationNames(paths),
                    [1, 0, 1, 1],
                    "ShellMoveRestartMutationVerified");
                evidence.Operations = await RestoreAotShellMoveBaselineAsync(
                    result,
                    paths,
                    compensation: false);
                evidence.After = await CaptureAotShellMoveStateAsync(
                    host,
                    paths,
                    ShellMoveBaselineSourceNames(paths));
                RequireAotShellMoveState(
                    result,
                    evidence.After,
                    paths,
                    expectedDestinationNames: [],
                    expectedHistoryItemCounts: [],
                    "ShellMoveBaselineRestored");
                break;

            case "Postflight":
                evidence.Before = await CaptureAotShellMoveStateAsync(
                    host,
                    paths,
                    ShellMoveBaselineSourceNames(paths));
                RequireAotShellMoveState(
                    result,
                    evidence.Before,
                    paths,
                    expectedDestinationNames: [],
                    expectedHistoryItemCounts: [],
                    "ShellMovePostflightVerified");
                evidence.After = evidence.Before;
                break;

            case "Compensate":
                evidence.Before = CaptureAotShellMoveDiskOnlyState(paths);
                evidence.Operations = await RestoreAotShellMoveBaselineAsync(
                    result,
                    paths,
                    compensation: true);
                evidence.After = await CaptureAotShellMoveStateAsync(
                    host,
                    paths,
                    ShellMoveBaselineSourceNames(paths));
                RequireAotShellMoveState(
                    result,
                    evidence.After,
                    paths,
                    expectedDestinationNames: [],
                    expectedHistoryItemCounts: [],
                    "ShellMoveCompensationCompleted");
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Shell move phase '{phase}'.");
        }

        SettingsService.SaveDebounced(notifySubscribers: false);
        evidence.FlushSucceeded = await SettingsService.FlushPendingSaveAsync(
            notifySubscribers: false);
        RequireAotManagedUi(
            result,
            evidence.FlushSucceeded,
            "ShellMovePersistenceFlushed",
            "The Shell move phase did not flush successfully.");
    }

    private async Task<AotManagedUiShellMoveOperationsEvidence>
        MoveAotShellItemsThroughMenusAsync(
            AotManagedUiSmokeResult result,
            AotLocalFileSurfaceHost host,
            AotShellMoveFixturePaths paths)
    {
        var menus = new List<AotManagedUiShellMoveMenuEvidence>();

        AotShellMoveMenuInvocationSnapshot real =
            await host.Surface.InvokeAotShellMoveBackToDesktopAsync(
                [Path.GetFileNameWithoutExtension(paths.RealName)],
                expectMultiSelection: false);
        menus.Add(MapAotShellMoveMenu(real));
        await host.Surface.WaitForAotLocalFileSurfaceAsync(
            paths.WidgetRoot,
            ShellMoveNamesAfterReal(paths),
            expectAtMappedRoot: true);

        AotShellMoveMenuInvocationSnapshot partial =
            await host.Surface.InvokeAotShellMoveBackToDesktopAsync(
                [
                    Path.GetFileNameWithoutExtension(paths.PartialFirstName),
                    Path.GetFileNameWithoutExtension(paths.PartialSecondName)
                ],
                expectMultiSelection: true);
        menus.Add(MapAotShellMoveMenu(partial));
        await host.Surface.WaitForAotLocalFileSurfaceAsync(
            paths.WidgetRoot,
            ShellMoveNamesAfterPartial(paths),
            expectAtMappedRoot: true);

        AotShellMoveMenuInvocationSnapshot cancel =
            await host.Surface.InvokeAotShellMoveBackToDesktopAsync(
                [Path.GetFileNameWithoutExtension(paths.CancelName)],
                expectMultiSelection: false);
        menus.Add(MapAotShellMoveMenu(cancel));
        await host.Surface.WaitForAotLocalFileSurfaceAsync(
            paths.WidgetRoot,
            ShellMoveNamesAfterPartial(paths),
            expectAtMappedRoot: true);

        AotShellMoveMenuInvocationSnapshot late =
            await host.Surface.InvokeAotShellMoveBackToDesktopAsync(
                [Path.GetFileNameWithoutExtension(paths.LateName)],
                expectMultiSelection: false);
        menus.Add(MapAotShellMoveMenu(late));
        IReadOnlyList<AotShellMoveInvocationSnapshot> pendingInvocations =
            AotShellMoveFixture.CaptureInvocations();
        AotShellMoveInvocationSnapshot pendingLate = pendingInvocations.Single(
            invocation => invocation.Mode == AotShellMoveFixture.LateMode);
        bool lateTaskPendingWhenProductReturned =
            pendingLate.FileServiceOutcome ==
                AotShellMoveFixture.RecoveredPendingOutcome &&
            pendingLate.CompletedCountAtProductReturn == 1 &&
            !pendingLate.NativeTaskReturned;
        await host.Surface.WaitForAotLocalFileSurfaceAsync(
            paths.WidgetRoot,
            ShellMoveMutatedSourceNames(paths),
            expectAtMappedRoot: true);
        await AotShellMoveFixture.WaitForLateTaskReturnAsync();

        IReadOnlyList<AotShellMoveInvocationSnapshot> invocations =
            AotShellMoveFixture.CaptureInvocations();
        bool valid =
            IsValidAotShellMoveMenu(real, 1, "Success", host.WindowHandle) &&
            IsValidAotShellMoveMenu(partial, 2, "Success", host.WindowHandle) &&
            IsValidAotShellMoveMenu(cancel, 1, "Info", host.WindowHandle) &&
            IsValidAotShellMoveMenu(late, 1, "Success", host.WindowHandle) &&
            invocations.Count == 4 &&
            invocations.Select(invocation => invocation.Mode).SequenceEqual(
                [
                    AotShellMoveFixture.RealMode,
                    AotShellMoveFixture.PartialMode,
                    AotShellMoveFixture.CancelMode,
                    AotShellMoveFixture.LateMode
                ],
                StringComparer.Ordinal) &&
            invocations.All(invocation =>
                invocation.OwnerWindowHandle == host.WindowHandle &&
                invocation.OwnerWindowHandle != 0 &&
                invocation.NativeTaskReturned) &&
            IsAotShellMoveInvocation(
                invocations[0],
                plannedCount: 1,
                completedCount: 1,
                actualShell: true,
                simulatedAbort: false,
                AotShellMoveFixture.ReturnedOutcome) &&
            IsAotShellMoveInvocation(
                invocations[1],
                plannedCount: 2,
                completedCount: 1,
                actualShell: false,
                simulatedAbort: true,
                AotShellMoveFixture.ReturnedOutcome) &&
            IsAotShellMoveInvocation(
                invocations[2],
                plannedCount: 1,
                completedCount: 0,
                actualShell: false,
                simulatedAbort: true,
                AotShellMoveFixture.ReturnedOutcome) &&
            IsAotShellMoveInvocation(
                invocations[3],
                plannedCount: 1,
                completedCount: 1,
                actualShell: false,
                simulatedAbort: false,
                AotShellMoveFixture.RecoveredPendingOutcome) &&
            lateTaskPendingWhenProductReturned &&
            invocations[3].ProductReturnedAtUtc <
                invocations[3].NativeTaskReturnedAtUtc;
        RequireAotManagedUi(
            result,
            valid,
            "ShellMoveMenuMatrixCompleted",
            "The owned Shell move menu, owner HWND, partial/cancel, or late-return matrix was incomplete.");

        return new AotManagedUiShellMoveOperationsEvidence
        {
            Menus = menus,
            Invocations = invocations.ToList(),
            ProductMenuPathCompleted = true,
            LateTaskPendingWhenProductReturned = lateTaskPendingWhenProductReturned
        };
    }

    private static bool IsAotShellMoveInvocation(
        AotShellMoveInvocationSnapshot invocation,
        int plannedCount,
        int completedCount,
        bool actualShell,
        bool simulatedAbort,
        string outcome) =>
        invocation.PlannedCount == plannedCount &&
        invocation.SourcePaths.Count == plannedCount &&
        invocation.DestinationPaths.Count == plannedCount &&
        invocation.CompletedCount == completedCount &&
        invocation.CompletedCountAtProductReturn == completedCount &&
        invocation.ActualShellOperation == actualShell &&
        invocation.SimulatedOperationsAborted == simulatedAbort &&
        invocation.FileServiceOutcome == outcome;

    private static bool IsValidAotShellMoveMenu(
        AotShellMoveMenuInvocationSnapshot menu,
        int expectedSelectionCount,
        string expectedSeverity,
        long expectedWindowHandle) =>
        menu.SelectedNames.Count == expectedSelectionCount &&
        menu.SelectedPaths.Count == expectedSelectionCount &&
        menu.MultiSelection == (expectedSelectionCount > 1) &&
        menu.HostWindowHandle == expectedWindowHandle &&
        menu.HostWindowHandle != 0 &&
        menu.MenuItemCount > 0 &&
        menu.MoveIndex >= 0 &&
        !string.IsNullOrWhiteSpace(menu.MoveText) &&
        menu.MoveEnabled &&
        menu.AutomationInvoked &&
        menu.FeedbackKey == "file-move-desktop" &&
        menu.FeedbackSeverity == expectedSeverity &&
        !string.IsNullOrWhiteSpace(menu.FeedbackMessage) &&
        menu.Items.Count == menu.MenuItemCount &&
        menu.Items.Count(item => item.IsMove) == 1 &&
        menu.Items[menu.MoveIndex].IsMove &&
        menu.Items[menu.MoveIndex].IsEnabled;

    private async Task<AotManagedUiShellMoveOperationsEvidence>
        RestoreAotShellMoveBaselineAsync(
            AotManagedUiSmokeResult result,
            AotShellMoveFixturePaths paths,
            bool compensation)
    {
        int restoredCount = 0;
        foreach (AotShellMoveOwnedFile file in paths.OwnedFiles)
        {
            bool sourceExists = File.Exists(file.SourcePath);
            bool destinationExists = File.Exists(file.DestinationPath);
            if (!sourceExists && destinationExists)
            {
                File.Move(file.DestinationPath, file.SourcePath);
                restoredCount++;
            }
            else if (sourceExists && destinationExists)
            {
                string sourceHash = HashAotShellMoveFile(file.SourcePath);
                string destinationHash = HashAotShellMoveFile(file.DestinationPath);
                if (!string.Equals(
                        sourceHash,
                        destinationHash,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Owned Shell move duplicate '{file.Name}' has inconsistent content.");
                }
                File.Delete(file.DestinationPath);
            }
            else if (!sourceExists)
            {
                throw new FileNotFoundException(
                    $"Owned Shell move file '{file.Name}' is missing from both roots.");
            }
        }

        SettingsService.OrganizationHistory.Entries.Clear();
        // The receipt store owns desktop-organization-history.json beside
        // settings.json; SettingsService's debounced save and flush never
        // commit it. Without this durable save the cleared receipts only
        // exist in memory, so the next phase's fresh process reloads the
        // pre-clear entries from disk and the restart persistence proof
        // fails. The store's contract requires persisting every mutation.
        bool historySaved =
            await SettingsService.OrganizationHistory.SaveCheckedAsync();
        RequireAotManagedUi(
            result,
            historySaved &&
            paths.OwnedFiles.All(file =>
                File.Exists(file.SourcePath) &&
                !File.Exists(file.DestinationPath)),
            compensation
                ? "ShellMoveCompensationFilesRestored"
                : "ShellMoveFilesRestoredByHarness",
            "The owned Shell move fixture or its persisted organization history did not return to its source baseline.");
        return new AotManagedUiShellMoveOperationsEvidence
        {
            RestoredByHarness = true,
            RestoredFileCount = restoredCount,
            Compensation = compensation
        };
    }

    private async Task<AotManagedUiShellMoveStateEvidence>
        CaptureAotShellMoveStateAsync(
            AotLocalFileSurfaceHost host,
            AotShellMoveFixturePaths paths,
            IReadOnlyList<string> expectedSourceNames)
    {
        AotLocalFileSurfaceSnapshot surface =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                expectedSourceNames,
                expectAtMappedRoot: true);
        AotManagedUiShellMoveStateEvidence state =
            CaptureAotShellMoveDiskOnlyState(paths);
        state.MappedFolderPath = host.ViewModel.MappedFolderPath ?? string.Empty;
        state.Surface = MapAotLocalFileSurface(surface);
        state.History = SettingsService.OrganizationHistory.Entries
            .Select(entry => new AotManagedUiShellMoveHistoryEvidence
            {
                WidgetId = entry.WidgetId,
                ActionType = entry.ActionType,
                TransferMode = entry.TransferMode,
                CanUndo = entry.CanUndo,
                IsUndone = entry.IsUndone,
                IsFailed = entry.IsFailed,
                ItemCount = entry.Items.Count,
                SourcePaths = entry.Items
                    .Select(item => item.SourcePath)
                    .ToList(),
                DestinationPaths = entry.Items
                    .Select(item => item.DestinationPath)
                    .ToList()
            })
            .ToList();
        return state;
    }

    private static AotManagedUiShellMoveStateEvidence
        CaptureAotShellMoveDiskOnlyState(AotShellMoveFixturePaths paths)
    {
        return new AotManagedUiShellMoveStateEvidence
        {
            FixtureRoot = paths.FixtureRoot,
            WidgetRoot = paths.WidgetRoot,
            DesktopRoot = paths.DesktopRoot,
            Baseline = CaptureAotShellMoveDiskEntry(
                AotShellMoveFixture.BaselineName,
                paths.BaselinePath,
                destinationPath: null),
            OwnedFiles = paths.OwnedFiles
                .Select(file => CaptureAotShellMoveDiskEntry(
                    file.Name,
                    file.SourcePath,
                    file.DestinationPath))
                .ToList()
        };
    }

    private static AotManagedUiShellMoveDiskEntryEvidence
        CaptureAotShellMoveDiskEntry(
            string name,
            string sourcePath,
            string? destinationPath)
    {
        bool sourceExists = File.Exists(sourcePath);
        bool destinationExists =
            !string.IsNullOrWhiteSpace(destinationPath) &&
            File.Exists(destinationPath);
        string existingPath = sourceExists
            ? sourcePath
            : destinationExists
                ? destinationPath!
                : string.Empty;
        long length = 0;
        string sha256 = string.Empty;
        if (!string.IsNullOrEmpty(existingPath))
        {
            using FileStream stream = File.OpenRead(existingPath);
            length = stream.Length;
            sha256 = Convert.ToHexString(SHA256.HashData(stream));
        }

        return new AotManagedUiShellMoveDiskEntryEvidence
        {
            Name = name,
            SourcePath = sourcePath,
            DestinationPath = destinationPath ?? string.Empty,
            SourceExists = sourceExists,
            DestinationExists = destinationExists,
            Length = length,
            Sha256 = sha256
        };
    }

    private static void RequireAotShellMoveState(
        AotManagedUiSmokeResult result,
        AotManagedUiShellMoveStateEvidence state,
        AotShellMoveFixturePaths paths,
        IReadOnlyCollection<string> expectedDestinationNames,
        IReadOnlyList<int> expectedHistoryItemCounts,
        string step)
    {
        string[] expectedSourceNames = paths.OwnedFiles
            .Where(file => !expectedDestinationNames.Contains(
                file.Name,
                StringComparer.Ordinal))
            .Select(file => file.DisplayName)
            .Append(Path.GetFileNameWithoutExtension(
                AotShellMoveFixture.BaselineName))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        string[] actualSurfaceNames = state.Surface.Items
            .Select(item => item.Name)
            .ToArray();
        bool diskValid =
            state.Baseline.SourceExists &&
            !state.Baseline.DestinationExists &&
            state.Baseline.Length > 0 &&
            state.Baseline.Sha256.Length == 64 &&
            state.OwnedFiles.Count == paths.OwnedFiles.Count &&
            state.OwnedFiles.All(file =>
                file.SourceExists != file.DestinationExists &&
                file.Length > 0 &&
                file.Sha256.Length == 64 &&
                file.DestinationExists == expectedDestinationNames.Contains(
                    file.Name,
                    StringComparer.Ordinal));
        bool surfaceValid =
            IsAotManagedUiPathEqual(state.FixtureRoot, paths.FixtureRoot) &&
            IsAotManagedUiPathEqual(state.WidgetRoot, paths.WidgetRoot) &&
            IsAotManagedUiPathEqual(state.DesktopRoot, paths.DesktopRoot) &&
            IsAotManagedUiPathEqual(state.MappedFolderPath, paths.WidgetRoot) &&
            state.Surface.IsLoaded &&
            state.Surface.HasXamlRoot &&
            state.Surface.DataContextMatchesViewModel &&
            state.Surface.ViewModelInitialized &&
            state.Surface.IsAtMappedRoot &&
            !state.Surface.CanNavigateUp &&
            !state.Surface.NavigationBarVisible &&
            state.Surface.ViewMode == nameof(ViewMode.Icon) &&
            state.Surface.ProjectedItemCount == expectedSourceNames.Length &&
            state.Surface.RealizedContainerCount == expectedSourceNames.Length &&
            state.Surface.Items.All(item =>
                !item.IsFolder &&
                item.ContainerRealized &&
                item.DataContextMatches &&
                item.NameProjected &&
                AotLocalFileSurfaceFixture.IsPathEqualOrInside(
                    paths.WidgetRoot,
                    item.Path)) &&
            actualSurfaceNames.SequenceEqual(
                expectedSourceNames,
                StringComparer.OrdinalIgnoreCase);
        bool historyValid =
            state.History.Count == expectedHistoryItemCounts.Count &&
            state.History.Select(history => history.ItemCount).SequenceEqual(
                expectedHistoryItemCounts) &&
            state.History.All(history =>
                history.WidgetId == AotShellMoveFixture.OwnedWidgetId &&
                history.ActionType == OrganizationActionType.MoveBackToDesktop &&
                history.TransferMode == "Move" &&
                history.CanUndo &&
                !history.IsUndone &&
                !history.IsFailed &&
                history.SourcePaths.All(path =>
                    AotLocalFileSurfaceFixture.IsPathEqualOrInside(
                        paths.WidgetRoot,
                        path)) &&
                history.DestinationPaths.All(path =>
                    AotLocalFileSurfaceFixture.IsPathEqualOrInside(
                        paths.DesktopRoot,
                        path)));

        RequireAotManagedUi(
            result,
            diskValid && surfaceValid && historyValid,
            step,
            "The owned Shell move disk, live File Widget, or persisted history state did not match the expected phase.");
    }

    private static string[] ShellMoveBaselineSourceNames(
        AotShellMoveFixturePaths paths) =>
        paths.OwnedFiles
            .Select(file => file.DisplayName)
            .Append("baseline")
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string[] ShellMoveMutatedSourceNames(
        AotShellMoveFixturePaths paths) =>
    [
        "baseline",
        Path.GetFileNameWithoutExtension(paths.CancelName),
        Path.GetFileNameWithoutExtension(paths.PartialSecondName)
    ];

    private static string[] ShellMoveMutatedDestinationNames(
        AotShellMoveFixturePaths paths) =>
    [
        paths.RealName,
        paths.PartialFirstName,
        paths.LateName
    ];

    private static string[] ShellMoveNamesAfterReal(
        AotShellMoveFixturePaths paths) =>
        ShellMoveBaselineSourceNames(paths)
            .Where(name => name != Path.GetFileNameWithoutExtension(paths.RealName))
            .ToArray();

    private static string[] ShellMoveNamesAfterPartial(
        AotShellMoveFixturePaths paths) =>
        ShellMoveNamesAfterReal(paths)
            .Where(name => name !=
                Path.GetFileNameWithoutExtension(paths.PartialFirstName))
            .ToArray();

    private static string HashAotShellMoveFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static AotManagedUiShellMoveMenuEvidence MapAotShellMoveMenu(
        AotShellMoveMenuInvocationSnapshot snapshot)
    {
        return new AotManagedUiShellMoveMenuEvidence
        {
            MultiSelection = snapshot.MultiSelection,
            SelectedNames = snapshot.SelectedNames.ToList(),
            SelectedPaths = snapshot.SelectedPaths.ToList(),
            HostWindowHandle = snapshot.HostWindowHandle,
            MenuItemCount = snapshot.MenuItemCount,
            MoveIndex = snapshot.MoveIndex,
            MoveText = snapshot.MoveText,
            MoveEnabled = snapshot.MoveEnabled,
            AutomationInvoked = snapshot.AutomationInvoked,
            FeedbackKey = snapshot.FeedbackKey,
            FeedbackSeverity = snapshot.FeedbackSeverity,
            FeedbackMessage = snapshot.FeedbackMessage,
            Items = snapshot.Items
                .Select(item => new AotManagedUiShellMoveMenuItemEvidence
                {
                    Index = item.Index,
                    ItemType = item.ItemType,
                    Text = item.Text,
                    IsEnabled = item.IsEnabled,
                    IsMove = item.IsMove
                })
                .ToList()
        };
    }
}

internal sealed class AotManagedUiShellMoveEvidence
{
    public string Phase { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public bool NormalShutdownRequested { get; set; }
    public bool FlushSucceeded { get; set; }
    public long WindowHandle { get; set; }
    public bool HasXamlRoot { get; set; }
    public bool Visible { get; set; }
    public AotManagedUiShellMoveStateEvidence Before { get; set; } = new();
    public AotManagedUiShellMoveStateEvidence After { get; set; } = new();
    public AotManagedUiShellMoveOperationsEvidence Operations { get; set; } = new();
}

internal sealed class AotManagedUiShellMoveStateEvidence
{
    public string FixtureRoot { get; set; } = string.Empty;
    public string WidgetRoot { get; set; } = string.Empty;
    public string DesktopRoot { get; set; } = string.Empty;
    public string MappedFolderPath { get; set; } = string.Empty;
    public AotManagedUiLocalFileSurfaceEvidence Surface { get; set; } = new();
    public AotManagedUiShellMoveDiskEntryEvidence Baseline { get; set; } = new();
    public List<AotManagedUiShellMoveDiskEntryEvidence> OwnedFiles { get; set; } = [];
    public List<AotManagedUiShellMoveHistoryEvidence> History { get; set; } = [];
}

internal sealed class AotManagedUiShellMoveDiskEntryEvidence
{
    public string Name { get; set; } = string.Empty;
    public string SourcePath { get; set; } = string.Empty;
    public string DestinationPath { get; set; } = string.Empty;
    public bool SourceExists { get; set; }
    public bool DestinationExists { get; set; }
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class AotManagedUiShellMoveHistoryEvidence
{
    public string WidgetId { get; set; } = string.Empty;
    public string ActionType { get; set; } = string.Empty;
    public string TransferMode { get; set; } = string.Empty;
    public bool CanUndo { get; set; }
    public bool IsUndone { get; set; }
    public bool IsFailed { get; set; }
    public int ItemCount { get; set; }
    public List<string> SourcePaths { get; set; } = [];
    public List<string> DestinationPaths { get; set; } = [];
}

internal sealed class AotManagedUiShellMoveOperationsEvidence
{
    public List<AotManagedUiShellMoveMenuEvidence> Menus { get; set; } = [];
    public List<AotShellMoveInvocationSnapshot> Invocations { get; set; } = [];
    public bool ProductMenuPathCompleted { get; set; }
    public bool LateTaskPendingWhenProductReturned { get; set; }
    public bool RestoredByHarness { get; set; }
    public int RestoredFileCount { get; set; }
    public bool Compensation { get; set; }
}

internal sealed class AotManagedUiShellMoveMenuEvidence
{
    public bool MultiSelection { get; set; }
    public List<string> SelectedNames { get; set; } = [];
    public List<string> SelectedPaths { get; set; } = [];
    public long HostWindowHandle { get; set; }
    public int MenuItemCount { get; set; }
    public int MoveIndex { get; set; }
    public string MoveText { get; set; } = string.Empty;
    public bool MoveEnabled { get; set; }
    public bool AutomationInvoked { get; set; }
    public string FeedbackKey { get; set; } = string.Empty;
    public string FeedbackSeverity { get; set; } = string.Empty;
    public string FeedbackMessage { get; set; } = string.Empty;
    public List<AotManagedUiShellMoveMenuItemEvidence> Items { get; set; } = [];
}

internal sealed class AotManagedUiShellMoveMenuItemEvidence
{
    public int Index { get; set; }
    public string ItemType { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsMove { get; set; }
}
#endif
