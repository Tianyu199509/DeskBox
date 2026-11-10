using DeskBox.Models;
using DeskBox.Platform;
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
    // IMPORTANT (WinUI 3 C++ contract §4.1 & §8.1.1b): CUIElement::OnDragStarting
    // initializes DragStartingEventArgs.AllowedOperations to the 3-bit mask
    // Copy | Move | Link (0x7) and checks `if (allowedOperations != 0x7)` to
    // detect whether the app customized AllowedOperations. Setting 0x7 when
    // RequestedOperation == None causes WinUI 3 to skip put_AllowedOperations
    // and fall back to RequestedOperation (0x0), collapsing AllowedOperations
    // to None for both XAML intra-widget arrangement and external DoDragDrop!
    internal const DataPackageOperation SupportedOperations =
        DataPackageOperation.Copy | DataPackageOperation.Move;

    // Retained as an alias for SupportedOperations (0x3 != 0x7) so callers and
    // tests referencing FollowWindows operations on Windows 11 never hit the
    // WinUI 3 0x7 sentinel trap while RequestedOperation == None.
    internal const DataPackageOperation FullFollowWindowsOperations =
        SupportedOperations;

    // Protected sources (Start Menu, Public Desktop, Program Files, Windows)
    // must never advertise Move, but on Windows 11 advertising Copy | Link (0x5 != 0x7,
    // with PreferredOperation = Copy) keeps drops safe while allowing Taskbar / Start
    // Menu pinning and Alt+Drag shortcut creation.
    internal const DataPackageOperation ProtectedWin11Operations =
        DataPackageOperation.Copy | DataPackageOperation.Link;

    // The user's drag-out preference becomes the OLE preferred drop effect:
    // receivers honoring it take it as the default, receivers ignoring it
    // fall back to their own rules. FollowWindows maps to None so the
    // target applies its native volume/modifier defaults unchanged. The
    // preference only reaches Windows 11 targets; Windows 10 decides solely
    // from the advertised effect set (see ResolveDragOutAllowedOperations).
    internal static DataPackageOperation ResolveDragOutPreferredOperation(
        string? action,
        IEnumerable<string>? sourcePaths = null,
        bool? isProtectedCopyOnly = null)
    {
        if (isProtectedCopyOnly ?? IsProtectedCopyOnlySource(sourcePaths))
        {
            return DataPackageOperation.Copy;
        }

        return action switch
        {
            SettingsService.ManagedDragOutActionMove => DataPackageOperation.Move,
            SettingsService.ManagedDragOutActionCopy => DataPackageOperation.Copy,
            _ => DataPackageOperation.None
        };
    }

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
    private static readonly Lazy<string[]> s_startMenuRoots = new(static () =>
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.StartMenu,
            Environment.SpecialFolder.CommonStartMenu,
            Environment.SpecialFolder.Programs,
            Environment.SpecialFolder.CommonPrograms
        ];

        return folders
            .Select(Environment.GetFolderPath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path =>
            {
                try
                {
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) +
                           Path.DirectorySeparatorChar;
                }
                catch
                {
                    return string.Empty;
                }
            })
            .Where(static path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    });

    internal static bool IsStartMenuShortcutSource(IEnumerable<string>? sourcePaths)
    {
        if (sourcePaths is null)
        {
            return false;
        }

        string[] roots = s_startMenuRoots.Value;
        if (roots.Length == 0)
        {
            return false;
        }

        foreach (string rawPath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string fullPath;
            try
            {
                string substResolved = PathOriginClassifier.ResolveSubstPath(rawPath);
                fullPath = Path.GetFullPath(
                    string.IsNullOrWhiteSpace(substResolved) ? rawPath : substResolved);
            }
            catch
            {
                continue;
            }

            foreach (string root in roots)
            {
                if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool IsProtectedCopyOnlySource(IEnumerable<string>? sourcePaths) =>
        IsStartMenuShortcutSource(sourcePaths) ||
        DriveMediaInterop.IsProtectedDragOutSource(sourcePaths);

    internal static DataPackageOperation ResolveDragOutAllowedOperations(
        string? action,
        bool? isWindows11OrLater = null,
        IEnumerable<string>? sourcePaths = null,
        bool? isProtectedCopyOnly = null)
    {
        bool win11OrLater = isWindows11OrLater ??
            Services.WindowsCompatibilityService.IsWindows11OrLater;

        // Explicit Copy-only mode strictly advertises single-bit Copy on both
        // Win11 and Win10. Protected launcher/system directories (Start Menu,
        // Public Desktop, Program Files, Windows) forbid Move on every OS,
        // while offering Copy | Link (0x5 != 0x7) on Win11 so Taskbar / Start Menu
        // pinning and Alt+Drag shortcut creation work seamlessly.
        if (string.Equals(action, SettingsService.ManagedDragOutActionCopy, StringComparison.Ordinal))
        {
            return DataPackageOperation.Copy;
        }

        if (isProtectedCopyOnly ?? IsProtectedCopyOnlySource(sourcePaths))
        {
            return win11OrLater
                ? ProtectedWin11Operations
                : DataPackageOperation.Copy;
        }

        if (string.Equals(action, SettingsService.ManagedDragOutActionMove, StringComparison.Ordinal))
        {
            // Win11 honors Preferred DropEffect = Move while keeping Copy as a
            // fallback for copy-only drop targets (browsers, editors); Win10
            // requires single-bit Move to avoid Explorer's multi-effect menu.
            return win11OrLater
                ? SupportedOperations
                : DataPackageOperation.Move;
        }

        // FollowWindows (default):
        // - Windows 11 advertises Copy | Move (0x3 != 0x7) with no preferred
        //   operation: WinUI 3 C++ writes AllowedOperations into CDragOperation
        //   without hitting the 0x7 unset-sentinel bug, internal XAML arrangement
        //   routes receive Copy | Move, Explorer applies native same-volume Move /
        //   cross-volume Copy, and third-party apps (WeChat, browsers, Electron,
        //   editors) pick Copy without ever deleting originals.
        // - Windows 10 collapses to single-bit Copy so Explorer completes
        //   silently without its multi-effect (#32768) picker while keeping
        //   source files 100% safe across WeChat/browsers; a one-time bottom
        //   HUD pill offers a one-click switch to Move when dropping onto
        //   Desktop or Explorer.
        return win11OrLater
            ? SupportedOperations
            : DataPackageOperation.Copy;
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
        out FileItemDragPackageResult result,
        bool allowNativeShellDataObject = true)
    {
        result = default;
        if (draggedItems.Count == 0)
        {
            return false;
        }

        if (!TryPreparePaths(
                dataPackage,
                draggedItems.Select(item => item.Path),
                getStorageItems,
                getTitle,
                "widget",
                out result,
                allowNativeShellDataObject))
        {
            return false;
        }

        // Widget drags are internal: origin markers let in-app drop targets
        // treat them as associations instead of file transfers (see
        // DeskBoxDragData.IsInternalFileDrag). Externally-facing hosts such
        // as search must call TryPreparePaths directly and stay unstamped.
        dataPackage.Properties[DeskBoxDragData.SourceWidgetIdProperty] =
            sourceWidgetId;
        dataPackage.Properties[DeskBoxDragData.SourcePathsProperty] =
            result.SourcePaths;
        dataPackage.Properties[
            DeskBoxDragData.InternalFileDragTokenProperty] =
            DeskBoxDragData.InternalFileDragToken;
        return true;
    }

    /// <summary>
    /// Path-driven core shared by every file drag host. Produces a native
    /// Shell IDataObject when attachable, a complete StorageItems payload
    /// otherwise, and never a partial one. Hosts decide whether to stamp
    /// DeskBox-origin markers on top (TryPrepare does; search does not).
    /// </summary>
    public static bool TryPreparePaths(
        DataPackage dataPackage,
        IEnumerable<string> paths,
        Func<IEnumerable<string>, IReadOnlyList<IStorageItem>> getStorageItems,
        Func<IReadOnlyList<string>, string> getTitle,
        string logSource,
        out FileItemDragPackageResult result,
        bool allowNativeShellDataObject = true)
    {
        result = default;
        string[] sourcePaths = paths
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
            allowNativeShellDataObject && NativeShellFileDragProvider.TryAttach(
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
                    $"{sourcePaths.Length} source={logSource}");
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
                    $"paths={sourcePaths.Length} source={logSource}");
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
