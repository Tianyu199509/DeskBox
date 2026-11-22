#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Security.Cryptography;
using DeskBox.Controls.WidgetContents;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private async Task CaptureAotManagedUiPickerClipboardAsync(
        AotManagedUiSmokeResult result,
        string phase)
    {
        AotManagedUiPickerClipboardEvidence evidence =
            result.PickerClipboard ??
            throw new InvalidOperationException(
                "The picker/StorageItems evidence container is unavailable.");
        AotPickerClipboardFixturePaths paths =
            AotPickerClipboardFixture.GetOwnedPaths(
                DeskBoxDataPathService.Current);
        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotLocalFileSurfaceHost host =
            await manager.GetAotLocalFileSurfaceHostAsync(
                AotPickerClipboardFixture.OwnedWidgetId);

        evidence.RunId = paths.RunId;
        evidence.Phase = phase;
        evidence.HostWindowHandle = host.WindowHandle;
        evidence.HostHasXamlRoot = host.HasXamlRoot;
        evidence.HostVisible = host.Visible;
        evidence.WidgetRoot = paths.WidgetRoot;
        evidence.PickerSourceFile = paths.PickerSourceFile;
        evidence.ClipboardSourceFile = paths.ClipboardSourceFile;
        evidence.ClipboardSourceFolder = paths.ClipboardSourceFolder;
        evidence.PickerDestinationFile = paths.PickerDestinationFile;
        evidence.ClipboardDestinationFile = paths.ClipboardDestinationFile;
        evidence.ClipboardDestinationFolder = paths.ClipboardDestinationFolder;
        evidence.SourceHashesBefore = CaptureAotPickerClipboardHashes(paths);

        RequireAotManagedUi(
            result,
            host.WindowHandle != 0 &&
            host.HasXamlRoot &&
            host.Visible &&
            evidence.SourceHashesBefore.Count == 3,
            "PickerClipboardHostReady",
            "The real owned File Widget or exact picker/StorageItems sources are unavailable.");

        switch (phase)
        {
            case "Mutate":
                await CaptureAotPickerClipboardMutationAsync(
                    result,
                    evidence,
                    host,
                    paths);
                break;
            case "VerifyRestore":
                await CaptureAotPickerClipboardRestoreAsync(
                    result,
                    evidence,
                    host,
                    paths);
                break;
            case "Postflight":
                await CaptureAotPickerClipboardPostflightAsync(
                    result,
                    evidence,
                    host,
                    paths);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported picker/StorageItems phase '{phase}'.");
        }

        evidence.SourceHashesAfter = CaptureAotPickerClipboardHashes(paths);
        RequireAotManagedUi(
            result,
            AotPickerClipboardHashesEqual(
                evidence.SourceHashesBefore,
                evidence.SourceHashesAfter),
            "PickerClipboardSourcesPreserved",
            "A picker or StorageItems import changed an owned source instead of copying it.");
    }

    private async Task CaptureAotPickerClipboardMutationAsync(
        AotManagedUiSmokeResult result,
        AotManagedUiPickerClipboardEvidence evidence,
        AotLocalFileSurfaceHost host,
        AotPickerClipboardFixturePaths paths)
    {
        AotLocalFileSurfaceSnapshot baseline =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                [],
                expectAtMappedRoot: true);
        evidence.Before = MapAotLocalFileSurface(baseline);
        RequireAotManagedUi(
            result,
            baseline.Items.Count == 0 &&
            baseline.EmptyStateVisible &&
            Directory.EnumerateFileSystemEntries(paths.WidgetRoot).Any() == false,
            "PickerClipboardOwnedBaselineVerified",
            "The picker/StorageItems widget root was not empty before mutation.");

        evidence.InteractionState = "CancelPending";
        WriteAotManagedUiResult(result.ResultPath, result);
        AotPickerInvocationSnapshot cancel =
            await host.Surface.InvokeAotFilePickerAsync(
                paths.PickerSourceRoot,
                expectCancel: true);
        evidence.CancelPicker = MapAotPickerInvocation(cancel);
        AotLocalFileSurfaceSnapshot afterCancel =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                [],
                expectAtMappedRoot: true);
        evidence.AfterCancel = MapAotLocalFileSurface(afterCancel);
        RequireAotManagedUi(
            result,
            cancel.SelectedPaths.Count == 0 &&
            IsValidAotPickerDialog(cancel.Dialog, host.WindowHandle) &&
            afterCancel.Items.Count == 0 &&
            !File.Exists(paths.PickerDestinationFile),
            "PickerCancelNoChangeVerified",
            "The real picker cancel branch changed the owned widget root or lost its owner/dialog evidence.");

        evidence.InteractionState = "SelectionPending";
        WriteAotManagedUiResult(result.ResultPath, result);
        AotPickerInvocationSnapshot selection =
            await host.Surface.InvokeAotFilePickerAsync(
                paths.PickerSourceRoot,
                expectCancel: false);
        evidence.SelectPicker = MapAotPickerInvocation(selection);
        AotLocalFileSurfaceSnapshot afterPicker =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                [paths.PickerFileName],
                expectAtMappedRoot: true);
        evidence.AfterPicker = MapAotLocalFileSurface(afterPicker);
        RequireAotManagedUi(
            result,
            selection.SelectedPaths.Count == 1 &&
            IsAotManagedUiPathEqual(
                selection.SelectedPaths[0],
                paths.PickerSourceFile) &&
            IsValidAotPickerDialog(selection.Dialog, host.WindowHandle) &&
            File.Exists(paths.PickerDestinationFile) &&
            HashAotPickerClipboardFile(paths.PickerDestinationFile) ==
                evidence.SourceHashesBefore[paths.PickerSourceFile],
            "PickerSelectionImported",
            "The real picker selection did not copy the exact owned file into the real File Widget.");

        AotClipboardStorageItemsSnapshot storageItems =
            await host.Surface.ImportAotClipboardStorageItemsAsync(
            [
                paths.ClipboardSourceFile,
                paths.ClipboardSourceFolder
            ]);
        evidence.StorageItems = MapAotClipboardStorageItems(storageItems);
        string[] expectedNames =
        [
            paths.ClipboardFolderName,
            paths.PickerFileName,
            paths.ClipboardFileName
        ];
        AotLocalFileSurfaceSnapshot afterStorageItems =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                expectedNames,
                expectAtMappedRoot: true);
        evidence.AfterStorageItems = MapAotLocalFileSurface(afterStorageItems);
        evidence.DestinationHashes = CaptureAotPickerClipboardDestinationHashes(
            paths);
        RequireAotManagedUi(
            result,
            storageItems.HostWindowHandle == host.WindowHandle &&
            storageItems.ContainsStorageItems &&
            !storageItems.HasDeskBoxSourcePaths &&
            string.Equals(
                storageItems.RequestedOperation,
                "Copy",
                StringComparison.Ordinal) &&
            storageItems.MaterializedPaths.Count == 2 &&
            storageItems.MaterializedPaths.Any(path =>
                IsAotManagedUiPathEqual(path, paths.ClipboardSourceFile)) &&
            storageItems.MaterializedPaths.Any(path =>
                IsAotManagedUiPathEqual(path, paths.ClipboardSourceFolder)) &&
            storageItems.MaterializedTypes.Contains(
                "StorageFile",
                StringComparer.Ordinal) &&
            storageItems.MaterializedTypes.Contains(
                "StorageFolder",
                StringComparer.Ordinal) &&
            storageItems.ShellFallbackBypassed &&
            storageItems.GlobalClipboardUntouched &&
            string.Equals(
                storageItems.FeedbackKey,
                "file-paste",
                StringComparison.Ordinal) &&
            evidence.DestinationHashes.Count == 3 &&
            string.Equals(
                evidence.DestinationHashes[paths.PickerDestinationFile],
                evidence.SourceHashesBefore[paths.PickerSourceFile],
                StringComparison.Ordinal) &&
            string.Equals(
                evidence.DestinationHashes[paths.ClipboardDestinationFile],
                evidence.SourceHashesBefore[paths.ClipboardSourceFile],
                StringComparison.Ordinal) &&
            string.Equals(
                evidence.DestinationHashes[
                    paths.ClipboardNestedDestinationFile],
                evidence.SourceHashesBefore[
                    paths.ClipboardNestedSourceFile],
                StringComparison.Ordinal),
            "ClipboardStorageItemsImported",
            "The real file/folder StorageItems payload did not enter the product parser and isolated import path.");

        evidence.InteractionState = "Completed";
        RequireAotManagedUi(
            result,
            afterStorageItems.Items.Count == 3,
            "PickerClipboardMutationApplied",
            "The final real File Widget surface does not expose all three imported entries.");
    }

    private async Task CaptureAotPickerClipboardRestoreAsync(
        AotManagedUiSmokeResult result,
        AotManagedUiPickerClipboardEvidence evidence,
        AotLocalFileSurfaceHost host,
        AotPickerClipboardFixturePaths paths)
    {
        string[] expectedNames =
        [
            paths.ClipboardFolderName,
            paths.PickerFileName,
            paths.ClipboardFileName
        ];
        AotLocalFileSurfaceSnapshot restored =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                expectedNames,
                expectAtMappedRoot: true);
        evidence.Before = MapAotLocalFileSurface(restored);
        evidence.AfterStorageItems = evidence.Before;
        evidence.DestinationHashes = CaptureAotPickerClipboardDestinationHashes(
            paths);
        RequireAotManagedUi(
            result,
            restored.Items.Count == 3 &&
            evidence.DestinationHashes.Count == 3 &&
            string.Equals(
                evidence.DestinationHashes[paths.PickerDestinationFile],
                evidence.SourceHashesBefore[paths.PickerSourceFile],
                StringComparison.Ordinal) &&
            string.Equals(
                evidence.DestinationHashes[paths.ClipboardDestinationFile],
                evidence.SourceHashesBefore[paths.ClipboardSourceFile],
                StringComparison.Ordinal) &&
            string.Equals(
                evidence.DestinationHashes[
                    paths.ClipboardNestedDestinationFile],
                evidence.SourceHashesBefore[
                    paths.ClipboardNestedSourceFile],
                StringComparison.Ordinal),
            "PickerClipboardRestartMutationVerified",
            "A fresh AOT process did not restore the picker and StorageItems imports with exact hashes.");
        evidence.InteractionState = "VerifyRestoreCompleted";
    }

    private async Task CaptureAotPickerClipboardPostflightAsync(
        AotManagedUiSmokeResult result,
        AotManagedUiPickerClipboardEvidence evidence,
        AotLocalFileSurfaceHost host,
        AotPickerClipboardFixturePaths paths)
    {
        AotLocalFileSurfaceSnapshot postflight =
            await host.Surface.WaitForAotLocalFileSurfaceAsync(
                paths.WidgetRoot,
                [],
                expectAtMappedRoot: true);
        evidence.Before = MapAotLocalFileSurface(postflight);
        evidence.AfterStorageItems = evidence.Before;
        RequireAotManagedUi(
            result,
            postflight.Items.Count == 0 &&
            postflight.EmptyStateVisible &&
            !Directory.EnumerateFileSystemEntries(paths.WidgetRoot).Any(),
            "PickerClipboardPostflightVerified",
            "A fresh AOT process did not observe the cleaned owned widget baseline.");
        evidence.InteractionState = "PostflightCompleted";
    }

    private static bool IsValidAotPickerDialog(
        AotPickerDialogSnapshot dialog,
        long expectedOwnerWindowHandle)
    {
        return dialog.WindowHandle != 0 &&
            dialog.ExpectedOwnerWindowHandle == expectedOwnerWindowHandle &&
            dialog.ExpectedOwner.WindowHandle == expectedOwnerWindowHandle &&
            dialog.ExpectedOwner.IsWindow &&
            dialog.DirectOwnerWindowHandle != 0 &&
            dialog.RootOwnerWindowHandle != 0 &&
            dialog.WindowThreadId != 0 &&
            dialog.ProcessId != 0 &&
            string.Equals(
                dialog.ClassName,
                "#32770",
                StringComparison.OrdinalIgnoreCase) &&
            dialog.VisibleBeforeAction &&
            dialog.OwnerChainContainsExpected &&
            dialog.OwnerChainHandles.Contains(expectedOwnerWindowHandle) &&
            dialog.WindowDestroyedAfterAction &&
            dialog.ClosedAtUtc >= dialog.ObservedAtUtc;
    }

    private static AotManagedUiPickerInvocationEvidence MapAotPickerInvocation(
        AotPickerInvocationSnapshot invocation)
    {
        AotPickerDialogSnapshot dialog = invocation.Dialog;
        return new AotManagedUiPickerInvocationEvidence
        {
            Action = invocation.Action,
            HostWindowHandle = invocation.HostWindowHandle,
            SuggestedFolder = invocation.SuggestedFolder,
            SelectedPaths = invocation.SelectedPaths.ToList(),
            Dialog = new AotManagedUiPickerDialogEvidence
            {
                Action = dialog.Action,
                WindowHandle = dialog.WindowHandle,
                DirectOwnerWindowHandle = dialog.DirectOwnerWindowHandle,
                RootOwnerWindowHandle = dialog.RootOwnerWindowHandle,
                ExpectedOwnerWindowHandle = dialog.ExpectedOwnerWindowHandle,
                WindowThreadId = dialog.WindowThreadId,
                ProcessId = dialog.ProcessId,
                ClassName = dialog.ClassName,
                Title = dialog.Title,
                VisibleBeforeAction = dialog.VisibleBeforeAction,
                OwnerChainContainsExpected =
                    dialog.OwnerChainContainsExpected,
                WindowDestroyedAfterAction =
                    dialog.WindowDestroyedAfterAction,
                ObservedAtUtc = dialog.ObservedAtUtc,
                ClosedAtUtc = dialog.ClosedAtUtc,
                OwnerChainHandles = dialog.OwnerChainHandles.ToList()
            }
        };
    }

    private static AotManagedUiClipboardStorageItemsEvidence
        MapAotClipboardStorageItems(
            AotClipboardStorageItemsSnapshot snapshot)
    {
        return new AotManagedUiClipboardStorageItemsEvidence
        {
            HostWindowHandle = snapshot.HostWindowHandle,
            ContainsStorageItems = snapshot.ContainsStorageItems,
            HasDeskBoxSourcePaths = snapshot.HasDeskBoxSourcePaths,
            RequestedOperation = snapshot.RequestedOperation,
            MaterializedPaths = snapshot.MaterializedPaths.ToList(),
            MaterializedTypes = snapshot.MaterializedTypes.ToList(),
            FeedbackKey = snapshot.FeedbackKey,
            FeedbackSeverity = snapshot.FeedbackSeverity,
            FeedbackMessage = snapshot.FeedbackMessage,
            ShellFallbackBypassed = snapshot.ShellFallbackBypassed,
            GlobalClipboardUntouched = snapshot.GlobalClipboardUntouched
        };
    }

    private static Dictionary<string, string> CaptureAotPickerClipboardHashes(
        AotPickerClipboardFixturePaths paths)
    {
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [paths.PickerSourceFile] =
                HashAotPickerClipboardFile(paths.PickerSourceFile),
            [paths.ClipboardSourceFile] =
                HashAotPickerClipboardFile(paths.ClipboardSourceFile),
            [paths.ClipboardNestedSourceFile] =
                HashAotPickerClipboardFile(paths.ClipboardNestedSourceFile)
        };
    }

    private static Dictionary<string, string>
        CaptureAotPickerClipboardDestinationHashes(
            AotPickerClipboardFixturePaths paths)
    {
        string[] files =
        [
            paths.PickerDestinationFile,
            paths.ClipboardDestinationFile,
            paths.ClipboardNestedDestinationFile
        ];
        return files
            .Where(File.Exists)
            .ToDictionary(
                path => path,
                HashAotPickerClipboardFile,
                StringComparer.OrdinalIgnoreCase);
    }

    private static bool AotPickerClipboardHashesEqual(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right)
    {
        return left.Count == right.Count &&
            left.All(pair =>
                right.TryGetValue(pair.Key, out string? value) &&
                string.Equals(
                    pair.Value,
                    value,
                    StringComparison.Ordinal));
    }

    private static string HashAotPickerClipboardFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}

internal sealed class AotManagedUiPickerClipboardEvidence
{
    public string Phase { get; set; } = string.Empty;
    public bool NormalShutdownRequested { get; set; }
    public string RunId { get; set; } = string.Empty;
    public string InteractionState { get; set; } = string.Empty;
    public long HostWindowHandle { get; set; }
    public bool HostHasXamlRoot { get; set; }
    public bool HostVisible { get; set; }
    public string WidgetRoot { get; set; } = string.Empty;
    public string PickerSourceFile { get; set; } = string.Empty;
    public string ClipboardSourceFile { get; set; } = string.Empty;
    public string ClipboardSourceFolder { get; set; } = string.Empty;
    public string PickerDestinationFile { get; set; } = string.Empty;
    public string ClipboardDestinationFile { get; set; } = string.Empty;
    public string ClipboardDestinationFolder { get; set; } = string.Empty;
    public Dictionary<string, string> SourceHashesBefore { get; set; } = [];
    public Dictionary<string, string> SourceHashesAfter { get; set; } = [];
    public Dictionary<string, string> DestinationHashes { get; set; } = [];
    public AotManagedUiLocalFileSurfaceEvidence Before { get; set; } = new();
    public AotManagedUiLocalFileSurfaceEvidence AfterCancel { get; set; } = new();
    public AotManagedUiLocalFileSurfaceEvidence AfterPicker { get; set; } = new();
    public AotManagedUiLocalFileSurfaceEvidence AfterStorageItems { get; set; } = new();
    public AotManagedUiPickerInvocationEvidence CancelPicker { get; set; } = new();
    public AotManagedUiPickerInvocationEvidence SelectPicker { get; set; } = new();
    public AotManagedUiClipboardStorageItemsEvidence StorageItems { get; set; } = new();
}

internal sealed class AotManagedUiPickerInvocationEvidence
{
    public string Action { get; set; } = string.Empty;
    public long HostWindowHandle { get; set; }
    public string SuggestedFolder { get; set; } = string.Empty;
    public List<string> SelectedPaths { get; set; } = [];
    public AotManagedUiPickerDialogEvidence Dialog { get; set; } = new();
}

internal sealed class AotManagedUiPickerDialogEvidence
{
    public string Action { get; set; } = string.Empty;
    public long WindowHandle { get; set; }
    public long DirectOwnerWindowHandle { get; set; }
    public long RootOwnerWindowHandle { get; set; }
    public long ExpectedOwnerWindowHandle { get; set; }
    public uint WindowThreadId { get; set; }
    public uint ProcessId { get; set; }
    public string ClassName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public bool VisibleBeforeAction { get; set; }
    public bool OwnerChainContainsExpected { get; set; }
    public bool WindowDestroyedAfterAction { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public List<long> OwnerChainHandles { get; set; } = [];
}

internal sealed class AotManagedUiClipboardStorageItemsEvidence
{
    public long HostWindowHandle { get; set; }
    public bool ContainsStorageItems { get; set; }
    public bool HasDeskBoxSourcePaths { get; set; }
    public string RequestedOperation { get; set; } = string.Empty;
    public List<string> MaterializedPaths { get; set; } = [];
    public List<string> MaterializedTypes { get; set; } = [];
    public string FeedbackKey { get; set; } = string.Empty;
    public string FeedbackSeverity { get; set; } = string.Empty;
    public string FeedbackMessage { get; set; } = string.Empty;
    public bool ShellFallbackBypassed { get; set; }
    public bool GlobalClipboardUntouched { get; set; }
}
#endif
