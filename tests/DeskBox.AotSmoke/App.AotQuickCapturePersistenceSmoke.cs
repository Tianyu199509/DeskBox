#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private const string AotQuickCapturePendingSaveBody =
        "AOT Quick Capture meaningful pending-save draft";
    private const string AotQuickCaptureAutoSaveBody =
        "AOT Quick Capture real 600 ms auto-save edit";
    private const string AotQuickCaptureExplicitFlushBody =
        "AOT Quick Capture explicit restart flush edit";

    private async Task CaptureAotManagedUiQuickCapturePersistenceAsync(
        AotManagedUiSmokeResult result,
        string phase)
    {
        WidgetManager manager = WidgetManager ??
            throw new InvalidOperationException("WidgetManager is unavailable.");
        AotQuickCapturePersistenceHost host =
            await manager.GetAotQuickCapturePersistenceHostAsync(
                AotManagedUiQuickCaptureWidgetId);
        RequireAotManagedUi(
            result,
            host.WindowHandle != 0 && host.HasXamlRoot && host.Visible,
            "QuickCaptureLiveHost",
            "The owned Quick Capture HWND or XamlRoot is unavailable.");

        QuickCaptureSurfaceContent surface = host.Surface;
        AotManagedUiQuickCapturePersistenceEvidence evidence =
            result.QuickCapturePersistence ??
            throw new InvalidOperationException(
                "The Quick Capture persistence evidence was not initialized.");

        await surface.ViewModel.RefreshItemsAsync();
        if (phase == AotManagedUiQuickCaptureVerifyDeletePhase)
        {
            QuickCaptureStoreData reloaded = await QuickCaptureService.GetDataAsync();
            QuickCaptureItem item = reloaded.Items.Single(entry => !entry.IsDeleted);
            await surface.OpenAotQuickCaptureItemAsync(item.Id);
        }
        evidence.Before = await CaptureAotManagedUiQuickCaptureStateAsync(surface);

        switch (phase)
        {
            case AotManagedUiQuickCaptureMutatePhase:
            {
                RequireAotManagedUiQuickCaptureEmpty(evidence.Before);
                string attachmentFixturePath = Path.Combine(
                    DeskBoxDataPathService.Current.RootPath,
                    "fixtures",
                    "quick-capture-attachment.txt");
                AotQuickCaptureMutationResult mutation =
                    await surface.RunAotQuickCaptureMutationAsync(
                        AotQuickCapturePendingSaveBody,
                        AotQuickCaptureAutoSaveBody,
                        attachmentFixturePath);
                evidence.PendingSaveFlushed = mutation.PendingSaveFlushed;
                evidence.AutoSaveObserved = mutation.AutoSaveObserved;
                evidence.ManagedAttachmentPath = mutation.ManagedAttachmentPath;
                evidence.After = await CaptureAotManagedUiQuickCaptureStateAsync(surface);
                RequireAotManagedUi(
                    result,
                    evidence.PendingSaveFlushed && evidence.AutoSaveObserved,
                    "QuickCaptureDraftAndAutoSaveObserved",
                    "The meaningful draft flush or real 600 ms auto-save did not complete.");
                RequireAotManagedUiQuickCapturePopulated(
                    evidence.After,
                    AotQuickCaptureAutoSaveBody,
                    expectedAttachmentCount: 1);
                RequireAotManagedUi(
                    result,
                    string.Equals(
                        evidence.After.Items.Single().Id,
                        mutation.ItemId,
                        StringComparison.Ordinal),
                    "QuickCaptureManagedAttachmentPersisted",
                    "The Quick Capture item or managed attachment was not persisted.");
                break;
            }

            case AotManagedUiQuickCaptureVerifyDeletePhase:
            {
                RequireAotManagedUiQuickCapturePopulated(
                    evidence.Before,
                    AotQuickCaptureAutoSaveBody,
                    expectedAttachmentCount: 1);
                string itemId = evidence.Before.Items.Single().Id;
                evidence.PendingSaveFlushed =
                    await surface.FlushAotQuickCaptureExistingItemAsync(
                        itemId,
                        AotQuickCaptureExplicitFlushBody);
                evidence.AfterExplicitFlush =
                    await CaptureAotManagedUiQuickCaptureStateAsync(surface);
                RequireAotManagedUiQuickCapturePopulated(
                    evidence.AfterExplicitFlush,
                    AotQuickCaptureExplicitFlushBody,
                    expectedAttachmentCount: 1);
                RequireAotManagedUi(
                    result,
                    evidence.PendingSaveFlushed,
                    "QuickCaptureRestartAndExplicitFlushVerified",
                    "The reloaded Quick Capture item did not complete its explicit flush.");

                evidence.ManagedAttachmentPath =
                    await surface.DeleteAotQuickCaptureManagedAttachmentAsync(itemId);
                evidence.AfterAttachmentDelete =
                    await CaptureAotManagedUiQuickCaptureStateAsync(surface);
                RequireAotManagedUiQuickCapturePopulated(
                    evidence.AfterAttachmentDelete,
                    AotQuickCaptureExplicitFlushBody,
                    expectedAttachmentCount: 0);
                RequireAotManagedUi(
                    result,
                    !File.Exists(evidence.ManagedAttachmentPath),
                    "QuickCaptureManagedAttachmentDeleted",
                    "The product attachment deletion path left its managed file behind.");

                await surface.DeleteAotQuickCaptureItemAsync(itemId);
                evidence.After = await CaptureAotManagedUiQuickCaptureStateAsync(surface);
                RequireAotManagedUiQuickCaptureEmpty(evidence.After);
                RequireAotManagedUi(
                    result,
                    true,
                    "QuickCaptureItemDeleted",
                    "The product Quick Capture item deletion path did not complete.");
                break;
            }

            case AotManagedUiQuickCapturePostflightPhase:
                RequireAotManagedUiQuickCaptureEmpty(evidence.Before);
                evidence.After = await CaptureAotManagedUiQuickCaptureStateAsync(surface);
                RequireAotManagedUiQuickCaptureEmpty(evidence.After);
                RequireAotManagedUi(
                    result,
                    true,
                    "QuickCaptureDeletePostflightVerified",
                    "The Quick Capture delete postflight was not clean.");
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Quick Capture persistence phase '{phase}'.");
        }
    }

    private async Task<AotManagedUiQuickCaptureStateEvidence>
        CaptureAotManagedUiQuickCaptureStateAsync(
            QuickCaptureSurfaceContent surface)
    {
        await surface.ViewModel.RefreshItemsAsync();
        QuickCaptureStoreData data = await QuickCaptureService.GetDataAsync();
        AotQuickCaptureSurfaceSnapshot surfaceSnapshot =
            surface.CaptureAotQuickCaptureSurfaceSnapshot();
        string attachmentRoot = Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "quick-capture",
            "attachments");
        string[] managedAttachmentRelativePaths = Directory.Exists(attachmentRoot)
            ? Directory.EnumerateFiles(
                    attachmentRoot,
                    "*",
                    SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(attachmentRoot, path)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .OrderBy(path => path, StringComparer.Ordinal)
                .ToArray()
            : [];

        return new AotManagedUiQuickCaptureStateEvidence
        {
            StoreVersion = data.Version,
            Items = data.Items
                .Where(item => !item.IsDeleted)
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new AotManagedUiQuickCaptureItemEvidence
                {
                    Id = item.Id,
                    Body = item.Body,
                    ContentFormat = item.ContentFormat.ToString(),
                    Type = item.Type.ToString(),
                    SourceKind = item.SourceKind.ToString(),
                    Attachments = item.Attachments
                        .OrderBy(attachment => attachment.Id, StringComparer.Ordinal)
                        .Select(attachment =>
                            new AotManagedUiQuickCaptureAttachmentEvidence
                            {
                                Id = attachment.Id,
                                FilePath = attachment.FilePath,
                                DisplayName = attachment.DisplayName,
                                Type = attachment.Type,
                                StorageMode = attachment.StorageMode,
                                Exists = File.Exists(attachment.FilePath)
                            })
                        .ToList()
                })
                .ToList(),
            ManagedAttachmentFileCount = managedAttachmentRelativePaths.Length,
            ManagedAttachmentRelativePaths = managedAttachmentRelativePaths.ToList(),
            SurfaceInitialized = surfaceSnapshot.IsInitialized,
            SurfaceLoaded = surfaceSnapshot.IsLoaded,
            SurfaceHasXamlRoot = surfaceSnapshot.HasXamlRoot,
            SurfaceItemCount = surfaceSnapshot.SurfaceItemCount,
            DetailItemId = surfaceSnapshot.DetailItemId,
            DetailBody = surfaceSnapshot.DetailBody,
            DetailIsCreating = surfaceSnapshot.IsCreatingDetail,
            DetailIsEditing = surfaceSnapshot.IsDetailEditing,
            DetailHasUnsavedChanges = surfaceSnapshot.DetailHasUnsavedChanges,
            PendingAttachmentCount = surfaceSnapshot.PendingAttachmentCount
        };
    }

    private static void RequireAotManagedUiQuickCaptureEmpty(
        AotManagedUiQuickCaptureStateEvidence state)
    {
        if (state.StoreVersion != 4 ||
            state.Items.Count != 0 ||
            state.ManagedAttachmentFileCount != 0 ||
            state.ManagedAttachmentRelativePaths.Count != 0 ||
            !state.SurfaceInitialized ||
            !state.SurfaceLoaded ||
            !state.SurfaceHasXamlRoot ||
            state.SurfaceItemCount != 0 ||
            state.DetailItemId is not null ||
            state.DetailHasUnsavedChanges ||
            state.PendingAttachmentCount != 0)
        {
            throw new InvalidOperationException(
                "The Quick Capture store, surface, or managed attachment baseline is not empty.");
        }
    }

    private static void RequireAotManagedUiQuickCapturePopulated(
        AotManagedUiQuickCaptureStateEvidence state,
        string expectedBody,
        int expectedAttachmentCount)
    {
        AotManagedUiQuickCaptureItemEvidence item = state.Items.Single();
        if (state.StoreVersion != 4 ||
            !string.Equals(item.Body, expectedBody, StringComparison.Ordinal) ||
            item.Attachments.Count != expectedAttachmentCount ||
            state.ManagedAttachmentFileCount != expectedAttachmentCount ||
            state.ManagedAttachmentRelativePaths.Count != expectedAttachmentCount ||
            item.Attachments.Any(attachment =>
                !string.Equals(
                    attachment.StorageMode,
                    TodoAttachment.ManagedStorageMode,
                    StringComparison.Ordinal) ||
                !attachment.Exists) ||
            !state.SurfaceInitialized ||
            !state.SurfaceLoaded ||
            !state.SurfaceHasXamlRoot ||
            state.SurfaceItemCount != 1 ||
            !string.Equals(state.DetailItemId, item.Id, StringComparison.Ordinal) ||
            !string.Equals(state.DetailBody, expectedBody, StringComparison.Ordinal) ||
            state.DetailHasUnsavedChanges ||
            state.PendingAttachmentCount != 0)
        {
            throw new InvalidOperationException(
                "The Quick Capture store, UI detail, or managed attachment state is incomplete.");
        }
    }
}

internal sealed class AotManagedUiQuickCapturePersistenceEvidence
{
    public string Phase { get; set; } = string.Empty;
    public bool PendingSaveFlushed { get; set; }
    public bool AutoSaveObserved { get; set; }
    public bool NormalShutdownRequested { get; set; }
    public string? ManagedAttachmentPath { get; set; }
    public AotManagedUiQuickCaptureStateEvidence Before { get; set; } = new();
    public AotManagedUiQuickCaptureStateEvidence? AfterExplicitFlush { get; set; }
    public AotManagedUiQuickCaptureStateEvidence? AfterAttachmentDelete { get; set; }
    public AotManagedUiQuickCaptureStateEvidence After { get; set; } = new();
}

internal sealed class AotManagedUiQuickCaptureStateEvidence
{
    public int StoreVersion { get; set; }
    public List<AotManagedUiQuickCaptureItemEvidence> Items { get; set; } = [];
    public int ManagedAttachmentFileCount { get; set; }
    public List<string> ManagedAttachmentRelativePaths { get; set; } = [];
    public bool SurfaceInitialized { get; set; }
    public bool SurfaceLoaded { get; set; }
    public bool SurfaceHasXamlRoot { get; set; }
    public int SurfaceItemCount { get; set; }
    public string? DetailItemId { get; set; }
    public string DetailBody { get; set; } = string.Empty;
    public bool DetailIsCreating { get; set; }
    public bool DetailIsEditing { get; set; }
    public bool DetailHasUnsavedChanges { get; set; }
    public int PendingAttachmentCount { get; set; }
}

internal sealed class AotManagedUiQuickCaptureItemEvidence
{
    public string Id { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string ContentFormat { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string SourceKind { get; set; } = string.Empty;
    public List<AotManagedUiQuickCaptureAttachmentEvidence> Attachments { get; set; } = [];
}

internal sealed class AotManagedUiQuickCaptureAttachmentEvidence
{
    public string Id { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string StorageMode { get; set; } = string.Empty;
    public bool Exists { get; set; }
}
#endif
