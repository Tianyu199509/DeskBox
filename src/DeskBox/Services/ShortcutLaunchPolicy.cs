using DeskBox.Helpers;
using DeskBox.Models;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Services;

internal enum ShortcutLaunchDecision
{
    None,
    Launch
}

/// <summary>
/// Shared predicate deciding whether a grid item acts as a launch drop target
/// ("drop files on it to open them with the linked application"). The XAML
/// DragOver/Drop handlers, the native OLE hit-test normalization and the shell
/// drop description all evaluate this so the three paths can never disagree.
/// Whether the shortcut's target actually accepts files is decided by the
/// shell's own drop handler at delegation time, not here.
/// </summary>
internal static class ShortcutLaunchPolicy
{
    internal static ShortcutLaunchDecision Evaluate(
        bool isShortcut,
        string? path,
        string? targetPath,
        bool targetIsDirectory)
    {
        if (!isShortcut ||
            string.IsNullOrWhiteSpace(path) ||
            !ShortcutHelper.IsShellLinkPath(path))
        {
            return ShortcutLaunchDecision.None;
        }

        if (ShortcutTargetProbe.Classify(ExpandTargetPath(targetPath)) !=
            ShortcutTargetKind.LocalFileSystem)
        {
            return ShortcutLaunchDecision.None;
        }

        // A folder shortcut keeps its import semantics: delegating a drop to it
        // would let the shell move or copy user files into the target folder.
        // A missing stored target stays eligible on purpose - Windows link
        // tracking can still resolve it, exactly like a drop in Explorer.
        if (targetIsDirectory)
        {
            return ShortcutLaunchDecision.None;
        }

        return ShortcutLaunchDecision.Launch;
    }

    /// <summary>
    /// Decides whether a drag that started inside a DeskBox file surface may
    /// also resolve as a launch. Internal drags normally keep their reorder
    /// semantics, but a drop on an application-shortcut tile means the same
    /// thing it means for an external drag: open these files with the linked
    /// application. Stack-popover member drags stay excluded because their
    /// paths address stack membership, not the files to open. A drag whose
    /// paths are all virtual (a browser drop carrying no filesystem path)
    /// stays excluded too: there is nothing on disk to hand the application.
    ///
    /// The dragged shortcut itself is excluded: a shortcut tile being dragged
    /// onto its own position means "put it back here", not "open it with
    /// itself". Without this the release opened the .lnk as a file and the
    /// linked application reported a broken image.
    /// </summary>
    internal static ShortcutLaunchDecision EvaluateInternalDrag(
        bool isDeskBoxFileDrag,
        bool isStackPopoverMemberDrag,
        IReadOnlyList<string> paths,
        string shortcutPath,
        bool targetHostedInStackPopover = false)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return isDeskBoxFileDrag &&
            !(isStackPopoverMemberDrag && targetHostedInStackPopover) &&
            paths.Count > 0 &&
            !ContainsSameFile(paths, shortcutPath)
                ? ShortcutLaunchDecision.Launch
                : ShortcutLaunchDecision.None;
    }

    /// <summary>
    /// True when <paramref name="shortcutPath"/> is one of the dragged files.
    /// Compared by full path with Windows case rules, the same comparison the
    /// transfer and reorder checks use.
    /// </summary>
    internal static bool ContainsSameFile(
        IReadOnlyList<string> paths,
        string shortcutPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath))
        {
            return false;
        }

        string shortcutFullPath;
        try
        {
            shortcutFullPath = Path.GetFullPath(shortcutPath);
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        foreach (string path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                if (string.Equals(
                        Path.GetFullPath(path),
                        shortcutFullPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception ex) when (
                ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }
        }

        return false;
    }

    /// <summary>
    /// Decides whether a completed internal drag should still be resolved as a
    /// launch. WinUI does not deliver the routed <c>Drop</c> event to the item
    /// surface for a drag that started from the same ListView - measured
    /// 2026-09-12: the pointer hovers a shortcut tile with the drop accepted,
    /// then the release only raises <c>DropCompleted</c> on the source.
    /// The release therefore falls back to that completion: the drop stays a
    /// launch when the surface did not take the gesture and the pointer was
    /// still inside the shortcut tile's icon (live geometry, not a recorded
    /// point - the reorder preview can recycle containers under the pointer).
    ///
    /// <paramref name="dropResult"/> is deliberately not compared against a
    /// single value: an in-app report for an internal file drag is normalized
    /// to Copy (<c>ResolveFileDragFeedbackOperation</c>) while a surface that
    /// took the drag itself reports its own reorder operation. The
    /// hovered-tile and pointer facts are what decide, because the hover is
    /// only ever recorded while a shortcut tile is the active child drop
    /// target.
    /// </summary>
    internal static bool ShouldLaunchFromCompletedInternalDrag(
        DataPackageOperation dropResult,
        bool hoveredLaunchTarget,
        bool cursorInsideLaunchTarget)
    {
        _ = dropResult;
        return hoveredLaunchTarget && cursorInsideLaunchTarget;
    }

    /// <summary>
    /// A point containment test for launch hit-testing: the icon rectangle
    /// expanded by a small slack that absorbs pointer jitter between drag
    /// samples without reaching into the neighbouring tile.
    /// </summary>
    internal static bool IsPointInsideRectWithSlack(
        double x,
        double y,
        double width,
        double height,
        double slack,
        double pointX,
        double pointY) =>
        width > 0 &&
        height > 0 &&
        pointX >= x - slack &&
        pointY >= y - slack &&
        pointX <= x + width + slack &&
        pointY <= y + height + slack;

    internal static ShortcutLaunchDecision EvaluateItem(WidgetItem item)
    {
        string? targetPath = item.TargetPath;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            // FileService.OpenItem resolves the stored metadata on click, so a
            // shortcut tile whose TargetPath was not materialized still needs a
            // stat against the stored link data here.
            targetPath = ShortcutHelper.ReadStoredMetadata(item.Path)?.TargetPath;
        }

        // Classification is a pure string check; the directory stat below can
        // probe the network for UNC/mapped-drive targets. DragOver fires on
        // every pointer move, so reject non-local targets before any I/O.
        if (string.IsNullOrWhiteSpace(targetPath) ||
            ShortcutTargetProbe.Classify(targetPath) != ShortcutTargetKind.LocalFileSystem)
        {
            return ShortcutLaunchDecision.None;
        }

        // Deliberately not cached: a stale "not a directory" would let a
        // folder-shortcut drop be delegated to the shell, which moves user
        // files into the target - the one red line this policy enforces.
        return Evaluate(
            item.IsShortcut,
            item.Path,
            targetPath,
            IsExpandedTargetDirectory(targetPath));
    }

    internal static string ExpandTargetPath(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return string.Empty;
        }

        try
        {
            return Environment.ExpandEnvironmentVariables(targetPath.Trim());
        }
        catch
        {
            return targetPath.Trim();
        }
    }

    internal static bool IsExpandedTargetDirectory(string? targetPath)
    {
        string expanded = ExpandTargetPath(targetPath);
        if (expanded.Length == 0)
        {
            return false;
        }

        try
        {
            return Directory.Exists(expanded);
        }
        catch
        {
            return false;
        }
    }
}
