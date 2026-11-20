using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

/// <summary>
/// The Open With app list handed to the menu builder. The entries ride in a
/// dedicated MenuFlyout opened beside the row; Dispose releases the live
/// Shell association handlers once the submenu round is over.
/// </summary>
public sealed record OpenWithMenuPayload(
    IReadOnlyList<MenuFlyoutItemBase> Items,
    Action Dispose);

/// <summary>
/// Host operations used by the shared file-item context menus.
/// The menu shape lives here; each surface only supplies its host-specific
/// actions and selection state.
/// </summary>
public sealed record FileItemMenuActions(
    Func<string, string, MenuFlyoutItem> CreateMenuItem,
    Func<string, string> Localize,
    Func<WidgetItem, Task> OpenItemAsync,
    Func<bool, Task> CopySelectionToClipboardAsync,
    Func<WidgetItem, Task> RenameItemAsync,
    Action CopySelectedPathsToClipboard,
    Action<WidgetItem> ShowInExplorer,
    Action<WidgetItem> ShowProperties,
    Func<bool> CanMoveItemsBackToDesktop,
    Func<IReadOnlyList<WidgetItem>, Task> MoveItemsBackToDesktopAsync,
    Func<IReadOnlyList<WidgetItem>, Task> DeleteItemsAsync,
    Func<IReadOnlyList<WidgetItem>> GetSelectedItems,
    bool CanCreateManualStack,
    Action<IReadOnlyList<WidgetItem>> CreateManualStack,
    Func<WidgetItem, bool> CanRemoveFromStack,
    Action<WidgetItem> RemoveFromStack,
    Action ClearSelection,
    Func<WidgetItem, Task>? ShowSystemContextMenuAsync = null,
    Func<WidgetItem, bool>? CanRunAsAdministrator = null,
    Func<WidgetItem, Task>? RunAsAdministratorAsync = null,
    Func<WidgetItem, OpenWithMenuPayload?>? GetOpenWithMenuItems = null,
    Func<WidgetItem, bool>? CanExtractArchive = null,
    Func<WidgetItem, Task>? ExtractArchiveAsync = null,
    Func<IReadOnlyList<WidgetItem>, bool>? CanCompressSelection = null,
    Func<IReadOnlyList<WidgetItem>, ArchiveCompressionFormat, Task>? CompressSelectionAsync = null,
    Func<WidgetItem, Task>? CreateItemShortcutAsync = null,
    Func<Task>? EmptyRecycleBinAsync = null);

/// <summary>
/// Builds the item context menus as a single-column MenuFlyout — one popup
/// window with the platform backdrop. The retired strip-plus-rows flyout
/// shape opened as two separate HWNDs and the row window had
/// no backdrop to show a material through (five failed rounds, see
/// artifacts/context-menu-enhancement-20261015); MenuFlyout restores the
/// material and the native submenu/hover/entrance behaviors.
/// The single-item order mirrors the native classic menu (the desktop
/// right-click the reference screenshots capture): the open family leads,
/// extension-style verbs follow, then the stable tail — copy-as-path,
/// cut/copy, create-shortcut/delete/rename, properties — with the
/// system-menu escape hatch last. Namespace shortcuts (This PC, Recycle
/// Bin, Control Panel links) additionally inherit their target's desktop
/// verbs: Empty Recycle Bin rides right after Open, matching the native
/// desktop icon menu, while rows that need a filesystem target (Open file
/// location, Compress) are hidden the way Explorer hides them.
/// </summary>
public static class FileItemMenuBuilder
{
    public static MenuFlyout CreateItemFlyout(
        WidgetItem item,
        FileItemMenuActions actions)
    {
        var flyout = CreateMenu();

        NamespaceShortcutKind namespaceKind =
            NamespaceShortcutHelper.ResolveKind(item);
        bool isNamespaceShortcut = namespaceKind != NamespaceShortcutKind.None;

        AddOpenRow(flyout, item, actions);
        AddOpenWithRow(flyout, item, actions);

        // Explorer's classic menu pins Open file location as the second row
        // for links; a PIDL-only namespace link has no filesystem target, so
        // the native menu omits the row entirely.
        if (!isNamespaceShortcut)
        {
            AddShowInExplorerRow(flyout, item, actions);
        }

        if (actions.CanRunAsAdministrator?.Invoke(item) == true &&
            actions.RunAsAdministratorAsync is not null)
        {
            flyout.Items.Add(CreateRow(
                actions,
                "Widget.RunAsAdministrator",
                "\uE7EF",
                null,
                async () => await actions.RunAsAdministratorAsync!(item)));
        }

        if (actions.CanExtractArchive?.Invoke(item) == true &&
            actions.ExtractArchiveAsync is not null)
        {
            flyout.Items.Add(CreateRow(
                actions,
                "Widget.ExtractAll",
                "\uE7F8",
                null,
                async () => await actions.ExtractArchiveAsync!(item)));
        }

        if (namespaceKind == NamespaceShortcutKind.RecycleBin &&
            actions.EmptyRecycleBinAsync is not null)
        {
            AddEmptyRecycleBinRow(flyout, actions);
        }

        // DeskBox's own archive verbs occupy the extension-verb zone of the
        // native layout (between the open family and the file-group tail).
        // The namespace targets have no desktop counterpart, so they skip it.
        if (!isNamespaceShortcut)
        {
            AddCompressToZipRow(flyout, actions);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(CreateRow(
            actions,
            "Widget.CopyPath",
            "\uE8C8",
            "Ctrl+Shift+C",
            () =>
            {
                actions.CopySelectedPathsToClipboard();
                return Task.CompletedTask;
            }));

        flyout.Items.Add(new MenuFlyoutSeparator());

        AddCutCopyRows(flyout, actions);

        flyout.Items.Add(new MenuFlyoutSeparator());

        AddCreateShortcutRow(flyout, item, actions);
        AddDeleteRow(flyout, actions);
        AddRenameRow(flyout, item, actions);

        bool hasSurfaceRows = actions.CanRemoveFromStack(item) ||
            actions.CanMoveItemsBackToDesktop();
        if (hasSurfaceRows)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        if (actions.CanRemoveFromStack(item))
        {
            flyout.Items.Add(CreateRow(
                actions,
                "Widget.Stack.RemoveItem",
                "\uE8FB",
                null,
                () =>
                {
                    actions.RemoveFromStack(item);
                    return Task.CompletedTask;
                }));
        }

        if (actions.CanMoveItemsBackToDesktop())
        {
            flyout.Items.Add(CreateRow(
                actions,
                "Widget.MoveBackToDesktop",
                "\uE74A",
                null,
                async () => await actions.MoveItemsBackToDesktopAsync(
                    actions.GetSelectedItems())));
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        flyout.Items.Add(CreateRow(
            actions,
            "Common.Properties",
            "\uE946",
            "Alt+Enter",
            () =>
            {
                actions.ShowProperties(item);
                return Task.CompletedTask;
            }));

        AddShowMoreOptionsRow(flyout, item, actions);

        return flyout;
    }

    public static MenuFlyout CreateMultiSelectionFlyout(
        FileItemMenuActions actions)
    {
        var flyout = CreateMenu();

        flyout.Items.Add(CreateRow(
            actions,
            "Widget.Open",
            "\uE8E5",
            null,
            async () =>
            {
                foreach (WidgetItem item in actions.GetSelectedItems())
                {
                    await actions.OpenItemAsync(item);
                }
            }));

        flyout.Items.Add(new MenuFlyoutSeparator());

        AddCutCopyRows(flyout, actions);
        AddDeleteRow(flyout, actions);

        flyout.Items.Add(new MenuFlyoutSeparator());

        AddCompressToZipRow(flyout, actions);

        flyout.Items.Add(CreateRow(
            actions,
            "Widget.CopyPath",
            "\uE8C8",
            "Ctrl+Shift+C",
            () =>
            {
                actions.CopySelectedPathsToClipboard();
                return Task.CompletedTask;
            }));

        if (actions.CanCreateManualStack)
        {
            flyout.Items.Add(CreateRow(
                actions,
                "Widget.Stack.Start",
                "\uE8B7",
                null,
                () =>
                {
                    actions.CreateManualStack(actions.GetSelectedItems());
                    actions.ClearSelection();
                    return Task.CompletedTask;
                }));
        }

        if (actions.CanMoveItemsBackToDesktop())
        {
            flyout.Items.Add(CreateRow(
                actions,
                "Widget.MoveBackToDesktop",
                "\uE74A",
                null,
                async () => await actions.MoveItemsBackToDesktopAsync(
                    actions.GetSelectedItems())));
        }

        return flyout;
    }

    /// <summary>
    /// Cut / copy, the first of the native classic file groups; rename moved
    /// to the second group (create shortcut / delete / rename) where the
    /// classic menu keeps it.
    /// </summary>
    private static void AddCutCopyRows(
        MenuFlyout flyout,
        FileItemMenuActions actions)
    {
        flyout.Items.Add(CreateRow(
            actions,
            "Common.Cut",
            "\uE8C6",
            "Ctrl+X",
            async () => await actions.CopySelectionToClipboardAsync(true)));

        flyout.Items.Add(CreateRow(
            actions,
            "Common.Copy",
            "\uE8C8",
            "Ctrl+C",
            async () => await actions.CopySelectionToClipboardAsync(false)));
    }

    private static void AddDeleteRow(
        MenuFlyout flyout,
        FileItemMenuActions actions)
    {
        flyout.Items.Add(CreateRow(
            actions,
            "Widget.MoveToRecycleBin",
            "\uE74D",
            "Delete",
            async () => await actions.DeleteItemsAsync(
                actions.GetSelectedItems())));
    }

    /// <summary>
    /// Create shortcut — the first row of the native classic tail group
    /// (create shortcut / delete / rename), offered for every item.
    /// </summary>
    private static void AddCreateShortcutRow(
        MenuFlyout flyout,
        WidgetItem item,
        FileItemMenuActions actions)
    {
        if (actions.CreateItemShortcutAsync is null)
        {
            return;
        }

        flyout.Items.Add(CreateRow(
            actions,
            "Widget.CreateShortcut",
            "\uE71B",
            null,
            async () => await actions.CreateItemShortcutAsync!(item)));
    }

    private static void AddRenameRow(
        MenuFlyout flyout,
        WidgetItem item,
        FileItemMenuActions actions)
    {
        flyout.Items.Add(CreateRow(
            actions,
            "Common.Rename",
            "\uE8AC",
            "F2",
            async () => await actions.RenameItemAsync(item)));
    }

    private static void AddOpenRow(
        MenuFlyout flyout,
        WidgetItem item,
        FileItemMenuActions actions)
    {
        flyout.Items.Add(CreateRow(
            actions,
            "Widget.Open",
            "\uE8E5",
            "Enter",
            async () => await actions.OpenItemAsync(item)));
    }

    private static void AddShowInExplorerRow(
        MenuFlyout flyout,
        WidgetItem item,
        FileItemMenuActions actions)
    {
        flyout.Items.Add(CreateRow(
            actions,
            "Widget.ShowInExplorer",
            "\uE838",
            null,
            () =>
            {
                actions.ShowInExplorer(item);
                return Task.CompletedTask;
            }));
    }

    /// <summary>
    /// Empty Recycle Bin for the bin's namespace shortcut — the target
    /// desktop icon's own verb, kept in its native slot right after the
    /// open family. Native grays the verb for an empty bin; the aggregate
    /// query is Shell work, so it runs on a low-priority UI pass after the
    /// menu is up, never in the right-tap callback. Until it answers, the
    /// row stays enabled and the click path re-checks before invoking.
    /// </summary>
    private static void AddEmptyRecycleBinRow(
        MenuFlyout flyout,
        FileItemMenuActions actions)
    {
        MenuFlyoutItem row = CreateRow(
            actions,
            "Widget.EmptyRecycleBin",
            "\uE74D",
            null,
            async () => await actions.EmptyRecycleBinAsync!());
        flyout.Opened += (_, _) => ScheduleRecycleBinEmptyStateCheck(row);
        flyout.Items.Add(row);
    }

    private static void ScheduleRecycleBinEmptyStateCheck(MenuFlyoutItem row)
    {
        DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            return;
        }

        _ = queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (ShellRecycleBinInterop.TryQueryTotalItemCount(out long totalItems) &&
                totalItems == 0)
            {
                row.IsEnabled = false;
            }
        });
    }

    private static void AddOpenWithRow(
        MenuFlyout flyout,
        WidgetItem item,
        FileItemMenuActions actions)
    {
        if (actions.GetOpenWithMenuItems is null)
        {
            return;
        }

        OpenWithMenuPayload? payload;
        try
        {
            payload = actions.GetOpenWithMenuItems(item);
        }
        catch
        {
            payload = null;
        }

        if (payload is null || payload.Items.Count == 0)
        {
            return;
        }

        AddSubmenuRow(
            flyout,
            actions,
            "Widget.OpenWith",
            "\uE8AF",
            payload.Items,
            payload.Dispose);
    }

    /// <summary>
    /// A row with a native MenuFlyoutSubItem submenu: the platform draws the
    /// chevron, opens on hover with its own timing, and dismisses the whole
    /// menu round on selection. The retired two-window flyout shape needed
    /// a container-wrapper hack, a chevron resource hack, and a manual
    /// hover debounce here — all gone with the migration.
    /// </summary>
    private static void AddSubmenuRow(
        MenuFlyout flyout,
        FileItemMenuActions actions,
        string localizationKey,
        string glyph,
        IReadOnlyList<MenuFlyoutItemBase> submenuItems,
        Action? submenuClosed = null)
    {
        var row = new MenuFlyoutSubItem
        {
            Text = actions.Localize(localizationKey),
            Icon = new FontIcon { Glyph = glyph }
        };

        foreach (MenuFlyoutItemBase entry in submenuItems)
        {
            row.Items.Add(entry);
        }

        if (submenuClosed is not null)
        {
            flyout.Closed += (_, _) => submenuClosed();
        }

        flyout.Items.Add(row);
    }

    private static MenuFlyout CreateMenu() => new()
    {
        // Grid widgets are small windows; the default root-bounds
        // constraint shoves the menu far away when the item sits near the
        // widget edge. Like the native Shell menu, only the monitor bounds
        // should matter (flip at screen edges, glued to the click point).
        ShouldConstrainToRootBounds = false
    };

    private static void AddCompressToZipRow(
        MenuFlyout flyout,
        FileItemMenuActions actions)
    {
        if (actions.CanCompressSelection?.Invoke(
                actions.GetSelectedItems()) != true ||
            actions.CompressSelectionAsync is null)
        {
            return;
        }

        var zipItem = new MenuFlyoutItem
        {
            Text = actions.Localize("Widget.CompressTo.Zip"),
            Icon = new FontIcon { Glyph = "\uE7F8" }
        };
        zipItem.Click += async (_, _) =>
        {
            await actions.CompressSelectionAsync(
                actions.GetSelectedItems(),
                ArchiveCompressionFormat.Zip);
        };

        var tarItem = new MenuFlyoutItem
        {
            Text = actions.Localize("Widget.CompressTo.Tar"),
            Icon = new FontIcon { Glyph = "\uE7F8" }
        };
        tarItem.Click += async (_, _) =>
        {
            await actions.CompressSelectionAsync(
                actions.GetSelectedItems(),
                ArchiveCompressionFormat.Tar);
        };

        // 7z needs the LZMA SDK (no BCL writer) — registered as batch debt
        // pending a go; the native format dialog has no public API.
        AddSubmenuRow(
            flyout,
            actions,
            "Widget.CompressTo",
            "\uE7F8",
            [zipItem, tarItem]);
    }

    private static void AddShowMoreOptionsRow(
        MenuFlyout flyout,
        WidgetItem item,
        FileItemMenuActions actions)
    {
        if (actions.ShowSystemContextMenuAsync is null)
        {
            return;
        }

        flyout.Items.Add(CreateRow(
            actions,
            "Widget.MoreSystemOperations",
            "\uE712",
            null,
            async () => await actions.ShowSystemContextMenuAsync(item)));
    }

    private static MenuFlyoutItem CreateRow(
        FileItemMenuActions actions,
        string localizationKey,
        string glyph,
        string? acceleratorText,
        Func<Task> onClick)
    {
        var row = new MenuFlyoutItem
        {
            Text = actions.Localize(localizationKey),
            Icon = new FontIcon { Glyph = glyph }
        };
        if (!string.IsNullOrEmpty(acceleratorText))
        {
            row.KeyboardAcceleratorTextOverride = acceleratorText;
        }

        // MenuFlyout closes itself on selection; the retired strip-plus-rows
        // rows needed an explicit Hide on every command.
        row.Click += async (_, _) =>
        {
            try
            {
                await onClick();
            }
            catch (Exception ex)
            {
                App.Log($"[FileItemMenu] Menu action '{localizationKey}' failed: {ex}");
            }
        };
        return row;
    }
}
