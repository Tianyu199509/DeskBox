namespace DeskBox.Helpers;

internal enum FileDropIntent
{
    None,
    Copy,
    Move,
    Shortcut,
    Reference,
    Reorder,
    Organize
}

/// <summary>
/// Resolves the semantic intent of a file drop before individual XAML and OLE
/// adapters translate it into their platform-specific operation flags.
/// </summary>
internal static class FileDropIntentPolicy
{
    public static FileDropIntent ResolveMappedTransfer(
        bool hasMappedFolder,
        bool forceCopy,
        bool controlDown,
        bool shiftDown,
        bool defaultMove,
        bool canCopy = true,
        bool canMove = true,
        bool altDown = false,
        bool followWindows = false,
        bool sameVolume = true,
        bool canLink = true,
        bool shortcutOutsideDesktop = false,
        bool sourcesOnDesktop = true)
    {
        if (!hasMappedFolder)
        {
            return FileDropIntent.Reference;
        }

        // Temporary and virtual payloads must outlive their provider-owned
        // staging directory, so they can never be moved from the source.
        if (forceCopy)
        {
            return canCopy ? FileDropIntent.Copy : FileDropIntent.None;
        }

        // Windows documents Ctrl+Shift as the link gesture. DeskBox also
        // accepts Alt as a discoverable shortcut gesture because it is the
        // modifier users commonly use when dragging from Explorer. Keep this
        // decision in the shared policy so XAML and native OLE paths agree.
        if ((altDown || (controlDown && shiftDown)) && canLink)
        {
            return FileDropIntent.Shortcut;
        }

        if (controlDown)
        {
            return canCopy ? FileDropIntent.Copy : FileDropIntent.None;
        }

        if (shiftDown)
        {
            return canMove ? FileDropIntent.Move : FileDropIntent.None;
        }

        // The "shortcut outside the desktop" default keeps files that already
        // live on the desktop moving into managed storage, while every other
        // source becomes a link so the original never leaves its location.
        // Explicit modifier gestures above always take precedence. A caller
        // that cannot extract the source paths treats the payload as
        // desktop-resident to preserve the previous behaviour.
        if (shortcutOutsideDesktop)
        {
            if (sourcesOnDesktop && canMove)
            {
                return FileDropIntent.Move;
            }

            if (!sourcesOnDesktop && canLink)
            {
                return FileDropIntent.Shortcut;
            }
        }

        // Explorer chooses Move for a same-volume drop and Copy when the
        // source and destination are on different volumes. A caller that has
        // enough path information can opt into that exact behaviour.
        if (followWindows)
        {
            if (sameVolume && canMove)
            {
                return FileDropIntent.Move;
            }

            if (!sameVolume && canCopy)
            {
                return FileDropIntent.Copy;
            }
        }

        if (defaultMove && canMove)
        {
            return FileDropIntent.Move;
        }

        return canCopy
            ? FileDropIntent.Copy
            : canMove
                ? FileDropIntent.Move
                : FileDropIntent.None;
    }

    public static bool AreSameVolume(string sourcePath, string destinationPath)
    {
        try
        {
            string sourceRoot = Path.GetPathRoot(Path.GetFullPath(sourcePath)) ??
                string.Empty;
            string destinationRoot = Path.GetPathRoot(
                Path.GetFullPath(destinationPath)) ?? string.Empty;
            return sourceRoot.Length > 0 &&
                   destinationRoot.Length > 0 &&
                   string.Equals(
                       sourceRoot,
                       destinationRoot,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Unknown roots are treated as cross-volume. That is the safe
            // choice because a move may otherwise delete the source after a
            // provider has copied only part of a large payload.
            return false;
        }
    }

    public static bool AreAllOnSameVolume(
        IEnumerable<string> sourcePaths,
        string destinationPath)
    {
        return sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .All(path => AreSameVolume(path, destinationPath));
    }

    /// <summary>
    /// Whether <paramref name="path"/> is <paramref name="directory"/> itself
    /// or one of its descendants. Comparison is case-insensitive and guarded
    /// against sibling prefixes such as "DesktopBackup".
    /// </summary>
    public static bool IsUnderDirectory(string path, string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) ||
                string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            string fullPath = Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            string fullRoot = Path.GetFullPath(directory).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (fullRoot.Length == 0)
            {
                return false;
            }

            return string.Equals(
                    fullPath,
                    fullRoot,
                    StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(
                    fullRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // Invalid or inaccessible paths are never treated as resident.
            return false;
        }
    }

    /// <summary>
    /// Whether every non-empty <paramref name="sourcePaths"/> entry lives under
    /// at least one of the given <paramref name="directories"/>. An unknown
    /// payload (no extractable root) resolves as desktop-resident so the
    /// default move behaviour is preserved rather than surprising the user.
    /// </summary>
    public static bool AreAllUnderDirectories(
        IEnumerable<string> sourcePaths,
        IEnumerable<string> directories)
    {
        try
        {
            string[] roots = directories
                .Where(directory => !string.IsNullOrWhiteSpace(directory))
                .Select(directory => Path.GetFullPath(directory))
                .ToArray();
            if (roots.Length == 0)
            {
                return true;
            }

            return sourcePaths
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .All(path => roots.Any(root => IsUnderDirectory(path, root)));
        }
        catch
        {
            // Invalid or inaccessible roots fall back to desktop-resident so
            // the existing move behaviour is preserved.
            return true;
        }
    }
}
