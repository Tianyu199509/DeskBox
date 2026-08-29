using DeskBox.Models;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls;

/// <summary>
/// Host operations used by the shared file-item context menus.
/// The menu shape lives here; each surface only supplies its host-specific
/// actions and selection state.
/// </summary>
public sealed record FileItemMenuActions(
    Func<string, string, MenuFlyoutItem> CreateMenuItem,
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
    Func<WidgetItem, Task>? RunAsAdministratorAsync = null);

public static class FileItemMenuBuilder
{
    public static MenuFlyout CreateItemFlyout(
        WidgetItem item,
        FileItemMenuActions actions)
    {
        var flyout = new MenuFlyout();

        MenuFlyoutItem open = actions.CreateMenuItem(
            "Widget.Open",
            "\uE8E5");
        open.KeyboardAcceleratorTextOverride = "Enter";
        open.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.OpenItemAsync(item);
        };
        flyout.Items.Add(open);

        if (actions.CanRunAsAdministrator?.Invoke(item) == true &&
            actions.RunAsAdministratorAsync is not null)
        {
            MenuFlyoutItem runAsAdministrator = actions.CreateMenuItem(
                "Widget.RunAsAdministrator",
                "\uE7EF");
            runAsAdministrator.Click += async (_, _) =>
            {
                flyout.Hide();
                await actions.RunAsAdministratorAsync(item);
            };
            flyout.Items.Add(runAsAdministrator);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem cut = actions.CreateMenuItem(
            "Common.Cut",
            "\uE8C6");
        cut.KeyboardAcceleratorTextOverride = "Ctrl+X";
        cut.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.CopySelectionToClipboardAsync(true);
        };
        flyout.Items.Add(cut);

        MenuFlyoutItem copy = actions.CreateMenuItem(
            "Common.Copy",
            "\uE8C8");
        copy.KeyboardAcceleratorTextOverride = "Ctrl+C";
        copy.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.CopySelectionToClipboardAsync(false);
        };
        flyout.Items.Add(copy);

        MenuFlyoutItem rename = actions.CreateMenuItem(
            "Common.Rename",
            "\uE8AC");
        rename.KeyboardAcceleratorTextOverride = "F2";
        rename.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.RenameItemAsync(item);
        };
        flyout.Items.Add(rename);
        flyout.Items.Add(new MenuFlyoutSeparator());

        MenuFlyoutItem copyPath = actions.CreateMenuItem(
            "Widget.CopyPath",
            "\uE8C8");
        copyPath.KeyboardAcceleratorTextOverride = "Ctrl+Shift+C";
        copyPath.Click += (_, _) =>
        {
            flyout.Hide();
            actions.CopySelectedPathsToClipboard();
        };
        flyout.Items.Add(copyPath);

        MenuFlyoutItem properties = actions.CreateMenuItem(
            "Common.Properties",
            "\uE946");
        properties.KeyboardAcceleratorTextOverride = "Alt+Enter";
        properties.Click += (_, _) =>
        {
            flyout.Hide();
            actions.ShowProperties(item);
        };
        flyout.Items.Add(properties);

        if (actions.ShowSystemContextMenuAsync is not null)
        {
            MenuFlyoutItem moreSystemOperations = actions.CreateMenuItem(
                "Widget.MoreSystemOperations",
                "\uE712");
            moreSystemOperations.Click += async (_, _) =>
            {
                flyout.Hide();
                await actions.ShowSystemContextMenuAsync(item);
            };
            flyout.Items.Add(moreSystemOperations);
        }

        if (actions.CanRemoveFromStack(item))
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            MenuFlyoutItem removeFromStack = actions.CreateMenuItem(
                "Widget.Stack.RemoveItem",
                "\uE8FB");
            removeFromStack.Click += (_, _) =>
            {
                flyout.Hide();
                actions.RemoveFromStack(item);
            };
            flyout.Items.Add(removeFromStack);
        }

        MenuFlyoutItem showInExplorer = actions.CreateMenuItem(
            "Widget.ShowInExplorer",
            "\uE838");
        showInExplorer.Click += (_, _) =>
        {
            flyout.Hide();
            actions.ShowInExplorer(item);
        };
        flyout.Items.Add(showInExplorer);

        if (actions.CanMoveItemsBackToDesktop())
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            MenuFlyoutItem moveBack = actions.CreateMenuItem(
                "Widget.MoveBackToDesktop",
                "\uE74A");
            moveBack.Click += async (_, _) =>
            {
                flyout.Hide();
                await actions.MoveItemsBackToDesktopAsync(
                    actions.GetSelectedItems());
            };
            flyout.Items.Add(moveBack);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem delete = actions.CreateMenuItem(
            "Widget.MoveToRecycleBin",
            "\uE74D");
        delete.KeyboardAcceleratorTextOverride = "Delete";
        delete.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.DeleteItemsAsync(actions.GetSelectedItems());
        };
        flyout.Items.Add(delete);

        return flyout;
    }

    public static MenuFlyout CreateMultiSelectionFlyout(
        FileItemMenuActions actions)
    {
        var flyout = new MenuFlyout();

        if (actions.CanCreateManualStack)
        {
            MenuFlyoutItem startStack = actions.CreateMenuItem(
                "Widget.Stack.Start",
                "\uE8B7");
            startStack.Click += (_, _) =>
            {
                flyout.Hide();
                actions.CreateManualStack(actions.GetSelectedItems());
                actions.ClearSelection();
            };
            flyout.Items.Add(startStack);
            flyout.Items.Add(new MenuFlyoutSeparator());
        }

        MenuFlyoutItem cut = actions.CreateMenuItem(
            "Common.Cut",
            "\uE8C6");
        cut.KeyboardAcceleratorTextOverride = "Ctrl+X";
        cut.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.CopySelectionToClipboardAsync(true);
        };
        flyout.Items.Add(cut);

        MenuFlyoutItem copy = actions.CreateMenuItem(
            "Common.Copy",
            "\uE8C8");
        copy.KeyboardAcceleratorTextOverride = "Ctrl+C";
        copy.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.CopySelectionToClipboardAsync(false);
        };
        flyout.Items.Add(copy);

        flyout.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem copyPath = actions.CreateMenuItem(
            "Widget.CopyPath",
            "\uE8C8");
        copyPath.KeyboardAcceleratorTextOverride = "Ctrl+Shift+C";
        copyPath.Click += (_, _) =>
        {
            flyout.Hide();
            actions.CopySelectedPathsToClipboard();
        };
        flyout.Items.Add(copyPath);

        if (actions.CanMoveItemsBackToDesktop())
        {
            MenuFlyoutItem moveBack = actions.CreateMenuItem(
                "Widget.MoveBackToDesktop",
                "\uE74A");
            moveBack.Click += async (_, _) =>
            {
                flyout.Hide();
                await actions.MoveItemsBackToDesktopAsync(
                    actions.GetSelectedItems());
            };
            flyout.Items.Add(moveBack);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        MenuFlyoutItem delete = actions.CreateMenuItem(
            "Widget.MoveToRecycleBin",
            "\uE74D");
        delete.KeyboardAcceleratorTextOverride = "Delete";
        delete.Click += async (_, _) =>
        {
            flyout.Hide();
            await actions.DeleteItemsAsync(actions.GetSelectedItems());
        };
        flyout.Items.Add(delete);

        return flyout;
    }
}
