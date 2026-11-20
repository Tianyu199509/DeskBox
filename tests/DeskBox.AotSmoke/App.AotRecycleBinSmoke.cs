#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Security.Cryptography;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private async Task CaptureAotManagedUiRecycleBinAsync(
        AotManagedUiSmokeResult result,
        string phase)
    {
        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotLocalFileSurfaceHost host =
            await manager.GetAotLocalFileSurfaceHostAsync(
                AotRecycleBinFixture.OwnedWidgetId);
        RequireAotManagedUi(
            result,
            host.WindowHandle != 0 && host.HasXamlRoot && host.Visible,
            "RecycleBinSurfaceHostReady",
            "The real File Widget HWND or XamlRoot is unavailable.");

        AotRecycleBinFixturePaths paths =
            AotRecycleBinFixture.GetOwnedPaths(DeskBoxDataPathService.Current);
        RequireAotManagedUi(
            result,
            IsAotManagedUiPathEqual(
                host.ViewModel.MappedFolderPath ?? string.Empty,
                paths.WidgetRoot),
            "RecycleBinOwnedRootVerified",
            "The real File Widget is not mapped to the exact owned Recycle Bin fixture.");

        AotManagedUiRecycleBinEvidence evidence = result.RecycleBin ??
            throw new InvalidOperationException(
                "Recycle Bin persistence evidence is unavailable.");
        evidence.RunId = paths.RunId;
        evidence.WindowHandle = host.WindowHandle;
        evidence.HasXamlRoot = host.HasXamlRoot;
        evidence.Visible = host.Visible;

        if (phase == "Compensate")
        {
            evidence.Before = await CaptureAotRecycleBinCompensationStateAsync(
                host,
                paths);
        }
        else
        {
            bool expectOwnedOnDisk = phase != "VerifyRestore";
            uint expectedRecycleMatches = expectOwnedOnDisk ? 0U : 1U;
            evidence.Before = await CaptureAotRecycleBinStateAsync(
                host,
                paths,
                expectOwnedOnDisk,
                expectedRecycleMatches);
            RequireAotRecycleBinState(
                result,
                evidence.Before,
                paths,
                expectOwnedOnDisk,
                expectedRecycleMatches,
                expectOwnedOnDisk
                    ? "RecycleBinOwnedBaselineVerified"
                    : "RecycleBinRestartDeletionVerified");
        }

        switch (phase)
        {
            case "Mutate":
                evidence.Operations = await DeleteAotRecycleBinItemsThroughMenusAsync(
                    result,
                    host,
                    paths);
                evidence.After = await CaptureAotRecycleBinStateAsync(
                    host,
                    paths,
                    expectOwnedOnDisk: false,
                    expectedRecycleMatches: 1);
                RequireAotRecycleBinState(
                    result,
                    evidence.After,
                    paths,
                    expectOwnedOnDisk: false,
                    expectedRecycleMatches: 1,
                    "RecycleBinMenuDeletionApplied");
                break;

            case "VerifyRestore":
                evidence.Operations = RestoreExactAotRecycleBinItems(
                    result,
                    paths,
                    compensation: false);
                await WaitForAotRecycleBinPathsAsync(paths, expectPresent: true);
                evidence.After = await CaptureAotRecycleBinStateAsync(
                    host,
                    paths,
                    expectOwnedOnDisk: true,
                    expectedRecycleMatches: 0);
                RequireAotRecycleBinState(
                    result,
                    evidence.After,
                    paths,
                    expectOwnedOnDisk: true,
                    expectedRecycleMatches: 0,
                    "RecycleBinExactRestoreCompleted");
                break;

            case "Postflight":
                evidence.After = await CaptureAotRecycleBinStateAsync(
                    host,
                    paths,
                    expectOwnedOnDisk: true,
                    expectedRecycleMatches: 0);
                RequireAotRecycleBinState(
                    result,
                    evidence.After,
                    paths,
                    expectOwnedOnDisk: true,
                    expectedRecycleMatches: 0,
                    "RecycleBinPostflightVerified");
                break;

            case "Compensate":
                evidence.Operations = RestoreExactAotRecycleBinItems(
                    result,
                    paths,
                    compensation: true);
                await WaitForAotRecycleBinPathsAsync(paths, expectPresent: true);
                evidence.After = await CaptureAotRecycleBinStateAsync(
                    host,
                    paths,
                    expectOwnedOnDisk: true,
                    expectedRecycleMatches: 0);
                RequireAotRecycleBinState(
                    result,
                    evidence.After,
                    paths,
                    expectOwnedOnDisk: true,
                    expectedRecycleMatches: 0,
                    "RecycleBinCompensationCompleted");
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Recycle Bin phase '{phase}'.");
        }

        SettingsService.SaveDebounced(notifySubscribers: false);
        evidence.FlushSucceeded = await SettingsService.FlushPendingSaveAsync(
            notifySubscribers: false);
        RequireAotManagedUi(
            result,
            evidence.FlushSucceeded,
            "RecycleBinPersistenceFlushed",
            "The Recycle Bin phase did not flush successfully.");
    }

    private async Task<AotManagedUiRecycleBinOperationsEvidence>
        DeleteAotRecycleBinItemsThroughMenusAsync(
            AotManagedUiSmokeResult result,
            AotLocalFileSurfaceHost host,
            AotRecycleBinFixturePaths paths)
    {
        string[] afterSingleNames =
        [
            paths.MultiFolderName,
            AotRecycleBinFixture.BaselineName,
            paths.MultiFileName
        ];
        AotRecycleBinMenuInvocationSnapshot single =
            await host.Surface.InvokeAotRecycleBinMenuDeleteAsync(
                [paths.SingleName],
                expectMultiSelection: false);
        await host.Surface.WaitForAotLocalFileSurfaceAsync(
            paths.WidgetRoot,
            afterSingleNames,
            expectAtMappedRoot: true);
        RequireAotManagedUi(
            result,
            IsValidAotRecycleBinMenu(single, expectedSelectionCount: 1),
            "RecycleBinSingleMenuDeleteCompleted",
            "The single-item File Widget menu did not route its enabled Recycle Bin action.");

        AotRecycleBinMenuInvocationSnapshot multi =
            await host.Surface.InvokeAotRecycleBinMenuDeleteAsync(
                [paths.MultiFileName, paths.MultiFolderName],
                expectMultiSelection: true);
        await host.Surface.WaitForAotLocalFileSurfaceAsync(
            paths.WidgetRoot,
            [AotRecycleBinFixture.BaselineName],
            expectAtMappedRoot: true);
        RequireAotManagedUi(
            result,
            IsValidAotRecycleBinMenu(multi, expectedSelectionCount: 2) &&
            single.MenuItemCount > multi.MenuItemCount &&
            string.Equals(
                single.DeleteText,
                multi.DeleteText,
                StringComparison.Ordinal),
            "RecycleBinMultiMenuDeleteCompleted",
            "The multi-selection File Widget menu did not route its enabled Recycle Bin action.");

        bool diskRemoved = paths.OwnedItems.All(item =>
            !File.Exists(item.Path) && !Directory.Exists(item.Path));
        RequireAotManagedUi(
            result,
            diskRemoved,
            "RecycleBinOwnedPathsRemoved",
            "One or more exact owned paths remained on disk after the product menu actions.");

        return new AotManagedUiRecycleBinOperationsEvidence
        {
            SingleMenu = MapAotRecycleBinMenu(single),
            MultiMenu = MapAotRecycleBinMenu(multi),
            ProductDeletePathCompleted = true,
            OwnedPathsRemoved = diskRemoved
        };
    }

    private static bool IsValidAotRecycleBinMenu(
        AotRecycleBinMenuInvocationSnapshot menu,
        int expectedSelectionCount)
    {
        return menu.SelectedNames.Count == expectedSelectionCount &&
            menu.SelectedPaths.Count == expectedSelectionCount &&
            menu.MultiSelection == (expectedSelectionCount > 1) &&
            menu.MenuItemCount > 0 &&
            menu.DeleteIndex == menu.MenuItemCount - 1 &&
            !string.IsNullOrWhiteSpace(menu.DeleteText) &&
            menu.DeleteEnabled &&
            menu.AutomationInvoked &&
            menu.FeedbackKey == "file-delete" &&
            menu.FeedbackSeverity == nameof(WidgetFeedbackSeverity.Success) &&
            !string.IsNullOrWhiteSpace(menu.FeedbackMessage) &&
            menu.Items.Count == menu.MenuItemCount &&
            menu.Items.Count(item => item.IsDelete) == 1 &&
            menu.Items[menu.DeleteIndex].IsDelete &&
            menu.Items[menu.DeleteIndex].IsEnabled;
    }

    private static AotManagedUiRecycleBinOperationsEvidence
        RestoreExactAotRecycleBinItems(
            AotManagedUiSmokeResult result,
            AotRecycleBinFixturePaths paths,
            bool compensation)
    {
        var nativeCalls = new List<AotManagedUiRecycleBinNativeEvidence>();
        foreach (AotRecycleBinOwnedItem item in paths.OwnedItems)
        {
            bool exists = File.Exists(item.Path) || Directory.Exists(item.Path);
            RecycleBinNativeCallResult query = RecycleBinNativeBackend.Invoke(
                RecycleBinNativeOperation.Query,
                paths.WidgetRoot,
                item.Name);
            nativeCalls.Add(MapAotRecycleBinNative(
                item,
                RecycleBinNativeOperation.Query,
                query));
            RequireAotManagedUi(
                result,
                query.Success && query.MatchedCount == (exists ? 0U : 1U),
                compensation
                    ? "RecycleBinCompensationIdentityQueried"
                    : "RecycleBinExactIdentityQueried",
                $"The exact Recycle Bin identity for '{item.Name}' was ambiguous or unavailable.");

            if (exists)
            {
                continue;
            }

            RecycleBinNativeCallResult restore = RecycleBinNativeBackend.Invoke(
                RecycleBinNativeOperation.Restore,
                paths.WidgetRoot,
                item.Name);
            nativeCalls.Add(MapAotRecycleBinNative(
                item,
                RecycleBinNativeOperation.Restore,
                restore));
            RequireAotManagedUi(
                result,
                restore.Success &&
                restore.MatchedCount == 1 &&
                restore.RestoredCount == 1,
                compensation
                    ? "RecycleBinCompensationIdentityRestored"
                    : "RecycleBinExactIdentityRestored",
                $"The exact Recycle Bin identity for '{item.Name}' was not restored.");
        }

        return new AotManagedUiRecycleBinOperationsEvidence
        {
            NativeCalls = nativeCalls,
            ExactRestoreCompleted = true,
            Compensation = compensation
        };
    }

    private async Task<AotManagedUiRecycleBinStateEvidence>
        CaptureAotRecycleBinStateAsync(
            AotLocalFileSurfaceHost host,
            AotRecycleBinFixturePaths paths,
            bool expectOwnedOnDisk,
            uint expectedRecycleMatches)
    {
        string[] expectedNames = expectOwnedOnDisk
            ?
            [
                paths.MultiFolderName,
                AotRecycleBinFixture.BaselineName,
                paths.MultiFileName,
                paths.SingleName
            ]
            : [AotRecycleBinFixture.BaselineName];
        AotLocalFileSurfaceSnapshot surface =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                expectedNames,
                expectAtMappedRoot: true);

        var native = new List<AotManagedUiRecycleBinNativeEvidence>();
        foreach (AotRecycleBinOwnedItem item in paths.OwnedItems)
        {
            RecycleBinNativeCallResult query = RecycleBinNativeBackend.Invoke(
                RecycleBinNativeOperation.Query,
                paths.WidgetRoot,
                item.Name);
            if (!query.Success || query.MatchedCount != expectedRecycleMatches)
            {
                throw new InvalidOperationException(
                    $"Exact Recycle Bin query mismatch for '{item.Name}': {query.Detail} matches={query.MatchedCount}.");
            }
            native.Add(MapAotRecycleBinNative(
                item,
                RecycleBinNativeOperation.Query,
                query));
        }

        return new AotManagedUiRecycleBinStateEvidence
        {
            FixtureRoot = paths.FixtureRoot,
            MappedFolderPath = host.ViewModel.MappedFolderPath ?? string.Empty,
            Surface = MapAotLocalFileSurface(surface),
            Disk = CaptureAotRecycleBinDisk(paths),
            NativeQueries = native
        };
    }

    private async Task<AotManagedUiRecycleBinStateEvidence>
        CaptureAotRecycleBinCompensationStateAsync(
            AotLocalFileSurfaceHost host,
            AotRecycleBinFixturePaths paths)
    {
        AotRecycleBinOwnedItem[] presentItems = paths.OwnedItems
            .Where(item => File.Exists(item.Path) || Directory.Exists(item.Path))
            .ToArray();
        string[] expectedNames = presentItems
            .Where(item => item.IsFolder)
            .Select(item => item.Name)
            .Concat([AotRecycleBinFixture.BaselineName])
            .Concat(presentItems
                .Where(item => !item.IsFolder)
                .Select(item => item.Name)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        AotLocalFileSurfaceSnapshot surface =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                expectedNames,
                expectAtMappedRoot: true);
        var native = new List<AotManagedUiRecycleBinNativeEvidence>();
        foreach (AotRecycleBinOwnedItem item in paths.OwnedItems)
        {
            RecycleBinNativeCallResult query = RecycleBinNativeBackend.Invoke(
                RecycleBinNativeOperation.Query,
                paths.WidgetRoot,
                item.Name);
            bool exists = File.Exists(item.Path) || Directory.Exists(item.Path);
            if (!query.Success || query.MatchedCount != (exists ? 0U : 1U))
            {
                throw new InvalidOperationException(
                    $"Compensation identity mismatch for '{item.Name}': {query.Detail} matches={query.MatchedCount}.");
            }
            native.Add(MapAotRecycleBinNative(
                item,
                RecycleBinNativeOperation.Query,
                query));
        }

        return new AotManagedUiRecycleBinStateEvidence
        {
            FixtureRoot = paths.FixtureRoot,
            MappedFolderPath = host.ViewModel.MappedFolderPath ?? string.Empty,
            Surface = MapAotLocalFileSurface(surface),
            Disk = CaptureAotRecycleBinDisk(paths),
            NativeQueries = native
        };
    }

    private static AotManagedUiRecycleBinDiskEvidence CaptureAotRecycleBinDisk(
        AotRecycleBinFixturePaths paths)
    {
        return new AotManagedUiRecycleBinDiskEvidence
        {
            Baseline = CaptureAotRecycleBinDiskEntry(
                AotRecycleBinFixture.BaselineName,
                paths.BaselinePath,
                isFolder: false),
            OwnedItems = paths.OwnedItems
                .Select(item => CaptureAotRecycleBinDiskEntry(
                    item.Name,
                    item.Path,
                    item.IsFolder))
                .ToList(),
            FolderPayload = CaptureAotRecycleBinDiskEntry(
                Path.GetFileName(paths.MultiFolderPayloadPath),
                paths.MultiFolderPayloadPath,
                isFolder: false)
        };
    }

    private static AotManagedUiRecycleBinDiskEntryEvidence
        CaptureAotRecycleBinDiskEntry(
            string name,
            string path,
            bool isFolder)
    {
        bool exists = isFolder ? Directory.Exists(path) : File.Exists(path);
        string sha256 = string.Empty;
        long length = 0;
        if (exists && !isFolder)
        {
            using FileStream stream = File.OpenRead(path);
            length = stream.Length;
            sha256 = Convert.ToHexString(SHA256.HashData(stream));
        }

        return new AotManagedUiRecycleBinDiskEntryEvidence
        {
            Name = name,
            Path = path,
            IsFolder = isFolder,
            Exists = exists,
            Length = length,
            Sha256 = sha256
        };
    }

    private static void RequireAotRecycleBinState(
        AotManagedUiSmokeResult result,
        AotManagedUiRecycleBinStateEvidence state,
        AotRecycleBinFixturePaths paths,
        bool expectOwnedOnDisk,
        uint expectedRecycleMatches,
        string step)
    {
        string[] expectedNames = expectOwnedOnDisk
            ?
            [
                paths.MultiFolderName,
                AotRecycleBinFixture.BaselineName,
                paths.MultiFileName,
                paths.SingleName
            ]
            : [AotRecycleBinFixture.BaselineName];
        string[] actualNames = state.Surface.Items
            .Select(item => item.Name)
            .ToArray();
        bool baselineValid =
            state.Disk.Baseline.Exists &&
            !state.Disk.Baseline.IsFolder &&
            state.Disk.Baseline.Length > 0 &&
            state.Disk.Baseline.Sha256.Length == 64;
        bool ownedDiskValid = expectOwnedOnDisk
            ? state.Disk.OwnedItems.All(item =>
                item.Exists &&
                (item.IsFolder ||
                    item.Length > 0 && item.Sha256.Length == 64)) &&
                state.Disk.FolderPayload.Exists &&
                state.Disk.FolderPayload.Length > 0 &&
                state.Disk.FolderPayload.Sha256.Length == 64
            : state.Disk.OwnedItems.All(item => !item.Exists) &&
                !state.Disk.FolderPayload.Exists;
        bool valid =
            IsAotManagedUiPathEqual(state.FixtureRoot, paths.FixtureRoot) &&
            IsAotManagedUiPathEqual(state.MappedFolderPath, paths.WidgetRoot) &&
            baselineValid &&
            ownedDiskValid &&
            state.NativeQueries.Count == paths.OwnedItems.Count &&
            state.NativeQueries.All(call =>
                call.Success &&
                call.Operation == nameof(RecycleBinNativeOperation.Query) &&
                call.MatchedCount == expectedRecycleMatches &&
                call.RestoredCount == 0 &&
                call.AttemptedPhases != 0) &&
            actualNames.SequenceEqual(
                expectedNames,
                StringComparer.OrdinalIgnoreCase) &&
            state.Surface.IsLoaded &&
            state.Surface.HasXamlRoot &&
            state.Surface.DataContextMatchesViewModel &&
            state.Surface.IsAtMappedRoot &&
            state.Surface.ProjectedItemCount == expectedNames.Length &&
            state.Surface.RealizedContainerCount == expectedNames.Length &&
            state.Surface.Items.All(item =>
                item.ContainerRealized &&
                item.DataContextMatches &&
                item.NameProjected &&
                AotLocalFileSurfaceFixture.IsPathEqualOrInside(
                    paths.WidgetRoot,
                    item.Path));

        RequireAotManagedUi(
            result,
            valid,
            step,
            "The owned File Widget, disk tree, or exact Recycle Bin identity did not match the expected state.");
    }

    private static async Task WaitForAotRecycleBinPathsAsync(
        AotRecycleBinFixturePaths paths,
        bool expectPresent)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            bool matches = paths.OwnedItems.All(item =>
                (File.Exists(item.Path) || Directory.Exists(item.Path)) ==
                    expectPresent);
            if (matches)
            {
                return;
            }
            await Task.Delay(50);
        }

        throw new TimeoutException(
            "The exact owned Recycle Bin paths did not reach their expected disk state.");
    }

    private static AotManagedUiRecycleBinMenuEvidence MapAotRecycleBinMenu(
        AotRecycleBinMenuInvocationSnapshot snapshot)
    {
        return new AotManagedUiRecycleBinMenuEvidence
        {
            MultiSelection = snapshot.MultiSelection,
            SelectedNames = snapshot.SelectedNames.ToList(),
            SelectedPaths = snapshot.SelectedPaths.ToList(),
            MenuItemCount = snapshot.MenuItemCount,
            DeleteIndex = snapshot.DeleteIndex,
            DeleteText = snapshot.DeleteText,
            DeleteEnabled = snapshot.DeleteEnabled,
            AutomationInvoked = snapshot.AutomationInvoked,
            FeedbackKey = snapshot.FeedbackKey,
            FeedbackSeverity = snapshot.FeedbackSeverity,
            FeedbackMessage = snapshot.FeedbackMessage,
            Items = snapshot.Items
                .Select(item => new AotManagedUiRecycleBinMenuItemEvidence
                {
                    Index = item.Index,
                    ItemType = item.ItemType,
                    Text = item.Text,
                    IsEnabled = item.IsEnabled,
                    IsDelete = item.IsDelete
                })
                .ToList()
        };
    }

    private static AotManagedUiRecycleBinNativeEvidence MapAotRecycleBinNative(
        AotRecycleBinOwnedItem item,
        RecycleBinNativeOperation operation,
        RecycleBinNativeCallResult call)
    {
        return new AotManagedUiRecycleBinNativeEvidence
        {
            Name = item.Name,
            Path = item.Path,
            IsFolder = item.IsFolder,
            Operation = operation.ToString(),
            Success = call.Success,
            Failure = call.Failure.ToString(),
            Detail = call.Detail,
            Status = call.Status,
            OperationHResult = call.OperationHResult,
            AttemptedPhases = call.AttemptedPhases,
            ComHResult = call.ComHResult,
            CreateHResult = call.CreateHResult,
            NamespaceHResult = call.NamespaceHResult,
            ItemsHResult = call.ItemsHResult,
            EnumerateHResult = call.EnumerateHResult,
            ItemNameHResult = call.ItemNameHResult,
            PropertyHResult = call.PropertyHResult,
            InvokeHResult = call.InvokeHResult,
            MatchedCount = call.MatchedCount,
            RestoredCount = call.RestoredCount
        };
    }
}

internal sealed class AotManagedUiRecycleBinEvidence
{
    public string Phase { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public bool NormalShutdownRequested { get; set; }
    public bool FlushSucceeded { get; set; }
    public long WindowHandle { get; set; }
    public bool HasXamlRoot { get; set; }
    public bool Visible { get; set; }
    public AotManagedUiRecycleBinStateEvidence Before { get; set; } = new();
    public AotManagedUiRecycleBinStateEvidence After { get; set; } = new();
    public AotManagedUiRecycleBinOperationsEvidence Operations { get; set; } = new();
}

internal sealed class AotManagedUiRecycleBinStateEvidence
{
    public string FixtureRoot { get; set; } = string.Empty;
    public string MappedFolderPath { get; set; } = string.Empty;
    public AotManagedUiLocalFileSurfaceEvidence Surface { get; set; } = new();
    public AotManagedUiRecycleBinDiskEvidence Disk { get; set; } = new();
    public List<AotManagedUiRecycleBinNativeEvidence> NativeQueries { get; set; } = [];
}

internal sealed class AotManagedUiRecycleBinDiskEvidence
{
    public AotManagedUiRecycleBinDiskEntryEvidence Baseline { get; set; } = new();
    public List<AotManagedUiRecycleBinDiskEntryEvidence> OwnedItems { get; set; } = [];
    public AotManagedUiRecycleBinDiskEntryEvidence FolderPayload { get; set; } = new();
}

internal sealed class AotManagedUiRecycleBinDiskEntryEvidence
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public bool Exists { get; set; }
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
}

internal sealed class AotManagedUiRecycleBinOperationsEvidence
{
    public AotManagedUiRecycleBinMenuEvidence SingleMenu { get; set; } = new();
    public AotManagedUiRecycleBinMenuEvidence MultiMenu { get; set; } = new();
    public List<AotManagedUiRecycleBinNativeEvidence> NativeCalls { get; set; } = [];
    public bool ProductDeletePathCompleted { get; set; }
    public bool OwnedPathsRemoved { get; set; }
    public bool ExactRestoreCompleted { get; set; }
    public bool Compensation { get; set; }
}

internal sealed class AotManagedUiRecycleBinMenuEvidence
{
    public bool MultiSelection { get; set; }
    public List<string> SelectedNames { get; set; } = [];
    public List<string> SelectedPaths { get; set; } = [];
    public int MenuItemCount { get; set; }
    public int DeleteIndex { get; set; }
    public string DeleteText { get; set; } = string.Empty;
    public bool DeleteEnabled { get; set; }
    public bool AutomationInvoked { get; set; }
    public string FeedbackKey { get; set; } = string.Empty;
    public string FeedbackSeverity { get; set; } = string.Empty;
    public string FeedbackMessage { get; set; } = string.Empty;
    public List<AotManagedUiRecycleBinMenuItemEvidence> Items { get; set; } = [];
}

internal sealed class AotManagedUiRecycleBinMenuItemEvidence
{
    public int Index { get; set; }
    public string ItemType { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public bool IsDelete { get; set; }
}

internal sealed class AotManagedUiRecycleBinNativeEvidence
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public string Operation { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Failure { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public uint Status { get; set; }
    public int OperationHResult { get; set; }
    public uint AttemptedPhases { get; set; }
    public int ComHResult { get; set; }
    public int CreateHResult { get; set; }
    public int NamespaceHResult { get; set; }
    public int ItemsHResult { get; set; }
    public int EnumerateHResult { get; set; }
    public int ItemNameHResult { get; set; }
    public int PropertyHResult { get; set; }
    public int InvokeHResult { get; set; }
    public uint MatchedCount { get; set; }
    public uint RestoredCount { get; set; }
}
#endif
