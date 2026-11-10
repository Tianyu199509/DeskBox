using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.ViewModels;

namespace DeskBox.Services;

internal enum ItemDropBehavior
{
    None,
    FolderImport,
    StackImport,
    Launch,
    RecycleBinDelete
}

/// <summary>
/// Single classification of what a grid item does when files are dropped on
/// it. The XAML DragOver/Drop predicates, the native OLE hit-test
/// normalization and the shell drop description all resolve drops through
/// this policy, so the three paths cannot disagree. Adding a new item-level
/// drop behavior means changing this type and its tests, never a call site.
/// </summary>
internal static class ItemDropBehaviorPolicy
{
    internal static ItemDropBehavior Resolve(WidgetItem item)
    {
        if (item is WidgetStackItem)
        {
            return ItemDropBehavior.StackImport;
        }

        if (TryGetFolderDropDestinationPath(item, out _))
        {
            return ItemDropBehavior.FolderImport;
        }

        // The Recycle Bin namespace shortcut is the bin's desktop verb in the
        // grid: dropping files on it recycles them. Checked before the launch
        // evaluation because the bin's .lnk has no filesystem target, so no
        // other behavior can apply.
        if (NamespaceShortcutHelper.ResolveKind(item) ==
            NamespaceShortcutKind.RecycleBin)
        {
            return ItemDropBehavior.RecycleBinDelete;
        }

        return ShortcutLaunchPolicy.EvaluateItem(item) == ShortcutLaunchDecision.Launch
            ? ItemDropBehavior.Launch
            : ItemDropBehavior.None;
    }

    internal static bool TryGetFolderDropDestinationPath(
        WidgetItem? item,
        out string destinationPath)
    {
        destinationPath = string.Empty;
        if (item is null || item is WidgetStackItem)
        {
            return false;
        }

        if (item.IsFolder && item.Path.Length > 0)
        {
            destinationPath = item.Path;
            return true;
        }

        if (!item.IsShortcut ||
            string.IsNullOrWhiteSpace(item.Path) ||
            !ShortcutHelper.IsShellLinkPath(item.Path))
        {
            return false;
        }

        string? targetPath = item.TargetPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            targetPath = ShortcutHelper.ReadStoredMetadata(item.Path)?.TargetPath;
        }

        if (string.IsNullOrWhiteSpace(targetPath) ||
            ShortcutTargetProbe.Classify(targetPath) != ShortcutTargetKind.LocalFileSystem)
        {
            return false;
        }

        string expanded = ShortcutLaunchPolicy.ExpandTargetPath(targetPath);
        if (expanded.Length == 0 ||
            DriveMediaInterop.IsProtectedDragOutSource([expanded]) ||
            !ShortcutLaunchPolicy.IsExpandedTargetDirectory(expanded))
        {
            return false;
        }

        destinationPath = expanded;
        return true;
    }
}
