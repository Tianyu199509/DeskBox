using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Atomically rebases all persisted filesystem paths owned by a <see cref="WidgetConfig"/>
/// (<see cref="WidgetConfig.MappedFolderPath"/>, <see cref="WidgetConfig.Items"/>,
/// manual stack member overrides, loose-item stack display order keys, and
/// <see cref="WidgetConfig.FileAddedAtByPath"/>) when a folder tree moves, is renamed,
/// or is restored onto another user profile.
/// </summary>
public static class WidgetPathRebaseHelper
{
    public static bool RebaseFolderTree(
        WidgetConfig config,
        string? oldFolderPath,
        string? newFolderPath,
        bool rebaseMappedFolderPath = true)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (string.IsNullOrWhiteSpace(oldFolderPath) ||
            string.IsNullOrWhiteSpace(newFolderPath))
        {
            return false;
        }

        string oldRoot;
        string newRoot;
        try
        {
            oldRoot = NormalizeRootPath(oldFolderPath);
            newRoot = NormalizeRootPath(newFolderPath);
        }
        catch
        {
            return false;
        }

        if (string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? currentMappedPath = config.MappedFolderPath;
        string? rebasedMappedPath = currentMappedPath;
        bool mappedPathChanged = false;
        if (rebaseMappedFolderPath && !string.IsNullOrWhiteSpace(currentMappedPath))
        {
            rebasedMappedPath = RebasePath(currentMappedPath, oldRoot, newRoot);
            mappedPathChanged = !string.Equals(
                currentMappedPath,
                rebasedMappedPath,
                StringComparison.Ordinal);
        }

        List<WidgetItemConfig> currentItems = config.Items ?? [];
        var rebasedItemPaths = new string[currentItems.Count];
        bool itemsChanged = false;
        for (int index = 0; index < currentItems.Count; index++)
        {
            string currentItemPath = currentItems[index].Path;
            string rebasedItemPath = RebasePath(currentItemPath, oldRoot, newRoot);
            rebasedItemPaths[index] = rebasedItemPath;
            if (!string.Equals(currentItemPath, rebasedItemPath, StringComparison.Ordinal))
            {
                itemsChanged = true;
            }
        }

        bool stackOrAddedAtChanged = WidgetFileStackSettings.RebaseManagedFolderPaths(
            config,
            oldRoot,
            newRoot);

        if (mappedPathChanged)
        {
            config.MappedFolderPath = rebasedMappedPath;
        }

        if (itemsChanged)
        {
            for (int index = 0; index < currentItems.Count; index++)
            {
                currentItems[index].Path = rebasedItemPaths[index];
            }
        }

        return mappedPathChanged || itemsChanged || stackOrAddedAtChanged;
    }

    internal static string RebasePath(
        string path,
        string oldRoot,
        string newRoot)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }

        string trimmedFullPath = fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        if (string.Equals(trimmedFullPath, oldRoot, StringComparison.OrdinalIgnoreCase))
        {
            return newRoot;
        }

        string oldPrefix = oldRoot + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        return Path.Combine(newRoot, fullPath[oldPrefix.Length..]);
    }

    private static string NormalizeRootPath(string path) =>
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
