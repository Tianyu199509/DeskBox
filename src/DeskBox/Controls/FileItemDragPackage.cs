using DeskBox.Models;
using DeskBox.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DeskBox.Controls;

public readonly record struct FileItemDragPackageResult(
    IReadOnlyList<string> SourcePaths,
    bool HasStorageItems,
    bool UsesNativeShellDataObject);

/// <summary>
/// Creates the common file-item drag payload. Hosts remain responsible for
/// deciding which items are dragged and how the completed drop is reconciled.
/// </summary>
public static class FileItemDragPackage
{
    // The transport permission advertised through DragStartingEventArgs
    // .AllowedOperations, for internal and external drags alike. Copy|Move
    // lets Explorer complete the drop natively (same-volume move, cross-
    // volume copy) while third-party receivers can still fall back to a copy.
    // The completion firewall in FileDragSourceGuardDataObject keeps a Move
    // reply from ever reaching the Shell source object, and DeskBox itself
    // never deletes a source based on the reported drop result.
    internal const DataPackageOperation SupportedOperations =
        DataPackageOperation.Copy | DataPackageOperation.Move;

    // The user's drag-out preference becomes the OLE preferred drop effect:
    // receivers honoring it take it as the default, receivers ignoring it
    // fall back to their own rules. FollowWindows maps to None so the
    // target applies its native volume/modifier defaults unchanged. The
    // preference only reaches Windows 11 targets; Windows 10 decides solely
    // from the advertised effect set (see ResolveDragOutAllowedOperations).
    internal static DataPackageOperation ResolveDragOutPreferredOperation(
        string? action) => action switch
    {
        SettingsService.ManagedDragOutActionMove => DataPackageOperation.Move,
        SettingsService.ManagedDragOutActionCopy => DataPackageOperation.Copy,
        _ => DataPackageOperation.None
    };

    // Windows 10's Explorer shows its copy/move picker on EVERY drop whose
    // brokered drag advertises more than one effect: the drop arrives
    // without mouse-button state and the shell cannot confirm a unique
    // default, so it asks (verified on a Win10 19045 VM across all three
    // settings tiers; a single-effect offer is the only silent shape — see
    // docs/articles/win10-drag-out-incident-20260930.md §7). The advertised
    // set therefore stays collapsed to one bit on Windows 10. Which bit:
    // an explicit Move/Copy setting performs exactly what it says, while
    // the default (FollowWindows) and unknown values resolve to Copy. The
    // silent single-bit shape cannot carry Windows' volume-dependent
    // default, and Move-only forces IM-style receivers (the WeChat send
    // box, feedback 500) to resolve the drop to Move and delete the
    // original into the Recycle Bin themselves — announcing Copy is the
    // safe side of the forced choice: originals stay, copy-only receivers
    // (VS Code, Chromium, WinForms) accept the drag, and users who want
    // moves select the Move tier explicitly. The OS flag is injectable so
    // tests pin both branches on any host.
    internal static DataPackageOperation ResolveDragOutAllowedOperations(
        string? action,
        bool? isWindows11OrLater = null)
    {
        if (isWindows11OrLater ??
            Services.WindowsCompatibilityService.IsWindows11OrLater)
        {
            return SupportedOperations;
        }

        return action switch
        {
            SettingsService.ManagedDragOutActionMove => DataPackageOperation.Move,
            SettingsService.ManagedDragOutActionCopy => DataPackageOperation.Copy,
            _ => DataPackageOperation.Copy,
        };
    }

    public static IReadOnlyList<WidgetItem> ResolveDraggedItems(
        IReadOnlyList<WidgetItem> eventItems,
        IReadOnlyList<WidgetItem> selectedItems)
    {
        WidgetItem[] distinctEventItems = eventItems.Distinct().ToArray();
        WidgetItem[] distinctSelectedItems = selectedItems.Distinct().ToArray();
        if (distinctSelectedItems.Length <= 1 || distinctEventItems.Length == 0)
        {
            return distinctEventItems;
        }

        // Some WinUI ListView input paths report only the pointer anchor in
        // DragItemsStarting even though it belongs to a larger selection. The
        // visible selection is authoritative whenever the event anchor is one
        // of its members.
        return distinctEventItems.Any(distinctSelectedItems.Contains)
            ? distinctSelectedItems
            : distinctEventItems;
    }

    public static bool TryPrepare(
        DataPackage dataPackage,
        IReadOnlyList<WidgetItem> draggedItems,
        string sourceWidgetId,
        Func<IEnumerable<string>, IReadOnlyList<IStorageItem>> getStorageItems,
        Func<IReadOnlyList<string>, string> getTitle,
        out FileItemDragPackageResult result)
    {
        result = default;
        if (draggedItems.Count == 0)
        {
            return false;
        }

        string[] sourcePaths = draggedItems
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (sourcePaths.Length == 0)
        {
            return false;
        }

        // Prefer a native Shell IDataObject for every file drag. It is what
        // Explorer itself produces, so external targets see the same formats
        // and Explorer owns the desktop drop position. It also sidesteps the
        // WinRT StorageFile broker, which can reject .lnk files (including
        // ones whose filesystem attributes look normal) and which this UI-STA
        // event would otherwise have to wait on synchronously. The guard
        // wrapper consumes completion receipts so a receiver's Move or Paste
        // Succeeded reply can never ask the Shell object to clean its source.
        bool usesNativeShellDataObject =
            NativeShellFileDragProvider.TryAttach(
                dataPackage,
                sourcePaths);
        IReadOnlyList<IStorageItem> storageItems = [];
        if (!usesNativeShellDataObject)
        {
            if (NativeShellFileDragProvider.RequiresStorageBrokerBypass(
                    sourcePaths))
            {
                App.Log(
                    $"[DragStart] Canceled broker-blocked file drag because " +
                    $"a native Shell payload could not be created paths=" +
                    $"{sourcePaths.Length}");
                return false;
            }

            // Never advertise a partial selection or fall back to a
            // coordinate-free filesystem move after Drop.
            storageItems = getStorageItems(sourcePaths);
            if (storageItems.Count != sourcePaths.Length)
            {
                App.Log(
                    $"[DragStart] Canceled file drag because only a " +
                    $"partial StorageItems payload was available " +
                    $"paths={sourcePaths.Length}");
                return false;
            }

            dataPackage.SetStorageItems(storageItems, readOnly: true);
        }

        // No preferred operation: Shell file sources advertise none, so a
        // receiver resolves its own default instead of inheriting one from us.
        // For Explorer that means the native volume rules (same-volume move,
        // cross-volume copy, modifiers); for third parties it means whatever
        // the receiver considers natural for a file payload. The allowed set
        // lives on DragStartingEventArgs.AllowedOperations and is written by
        // the caller — the two must never be merged into one value again.
        dataPackage.RequestedOperation = DataPackageOperation.None;

        dataPackage.Properties[DeskBoxDragData.SourceWidgetIdProperty] =
            sourceWidgetId;
        dataPackage.Properties[DeskBoxDragData.SourcePathsProperty] =
            sourcePaths;
        dataPackage.Properties[
            DeskBoxDragData.InternalFileDragTokenProperty] =
            DeskBoxDragData.InternalFileDragToken;
        // No text representation: Chromium turns CF_UNICODETEXT paths into
        // text/plain plus text/uri-list, and Electron drop zones then treat
        // the drag as text or a link instead of files. Explorer offers none.
        dataPackage.Properties.Title = getTitle(sourcePaths);

        result = new FileItemDragPackageResult(
            sourcePaths,
            storageItems.Count > 0 || usesNativeShellDataObject,
            usesNativeShellDataObject);
        return true;
    }
}
