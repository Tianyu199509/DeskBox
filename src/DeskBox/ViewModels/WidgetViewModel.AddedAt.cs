using DeskBox.Models;

namespace DeskBox.ViewModels;

public partial class WidgetViewModel
{
    private string[]? _activeAddedAtDirectories;

    private static string NormalizeAddedAtDirectoryKey(string? directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directoryPath));
        }
        catch
        {
            return Path.TrimEndingDirectorySeparator(directoryPath);
        }
    }

    private void ApplyPersistedAddedTimes(IReadOnlyList<WidgetItem> items)
    {
        HashSet<string>? scopedDirectories = _activeAddedAtDirectories is { Length: > 0 } dirs
            ? dirs.Where(static dir => dir.Length > 0)
                  .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;
        _activeAddedAtDirectories = null;

        EnsureAddedAtDictionaryComparer();
        bool isLegacySeed = !Config.FileAddedAtTrackingInitialized;
        bool changed = false;
        var currentPaths = items
            .Select(item => item.Path)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in items)
        {
            if (Config.FileAddedAtByPath.TryGetValue(item.Path, out DateTimeOffset addedAt))
            {
                item.AddedAt = addedAt;
                continue;
            }

            addedAt = isLegacySeed && item.CreatedAt != default
                ? new DateTimeOffset(DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Local))
                : DateTimeOffset.Now;
            Config.FileAddedAtByPath[item.Path] = addedAt;
            item.AddedAt = addedAt;
            changed = true;
        }

        foreach (string stalePath in Config.FileAddedAtByPath.Keys
                     .Where(path => !currentPaths.Contains(path) &&
                                    (scopedDirectories is null ||
                                     scopedDirectories.Contains(
                                         NormalizeAddedAtDirectoryKey(
                                             Path.GetDirectoryName(path)))))
                     .ToArray())
        {
            Config.FileAddedAtByPath.Remove(stalePath);
            changed = true;
        }

        if (!Config.FileAddedAtTrackingInitialized)
        {
            Config.FileAddedAtTrackingInitialized = true;
            changed = true;
        }

        if (changed)
        {
            PersistAddedAtTracking();
        }
    }

    private void AssignAddedAt(WidgetItem item, DateTimeOffset? preferred = null)
    {
        EnsureAddedAtDictionaryComparer();
        if (!Config.FileAddedAtByPath.TryGetValue(item.Path, out DateTimeOffset addedAt))
        {
            addedAt = preferred ?? DateTimeOffset.Now;
            Config.FileAddedAtByPath[item.Path] = addedAt;
            PersistAddedAtTrackingIfNeeded();
        }

        item.AddedAt = addedAt;
    }

    private void RecordFileAddedAt(string path, DateTimeOffset addedAt)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        EnsureAddedAtDictionaryComparer();
        Config.FileAddedAtByPath[path] = addedAt;
        Config.FileAddedAtTrackingInitialized = true;
        PersistAddedAtTrackingIfNeeded();
    }

    private void TransferFileAddedAt(string oldPath, string newPath)
    {
        EnsureAddedAtDictionaryComparer();
        DateTimeOffset addedAt = Config.FileAddedAtByPath.TryGetValue(oldPath, out var existing)
            ? existing
            : DateTimeOffset.Now;
        Config.FileAddedAtByPath.Remove(oldPath);
        Config.FileAddedAtByPath[newPath] = addedAt;

        try
        {
            string normalizedOldDir = Path.GetFullPath(oldPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string normalizedNewDir = Path.GetFullPath(newPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string oldPrefix = normalizedOldDir + Path.DirectorySeparatorChar;
            List<KeyValuePair<string, DateTimeOffset>> descendants = Config.FileAddedAtByPath
                .Where(entry => entry.Key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach ((string descendantOldPath, DateTimeOffset descendantAddedAt) in descendants)
            {
                Config.FileAddedAtByPath.Remove(descendantOldPath);
                string rebasedDescendant = Path.Combine(
                    normalizedNewDir,
                    descendantOldPath[oldPrefix.Length..]);
                Config.FileAddedAtByPath[rebasedDescendant] = descendantAddedAt;
            }
        }
        catch
        {
        }

        PersistAddedAtTrackingIfNeeded();
    }

    private void RemoveFileAddedAt(string path)
    {
        EnsureAddedAtDictionaryComparer();
        if (Config.FileAddedAtByPath.Remove(path))
        {
            PersistAddedAtTrackingIfNeeded();
        }
    }

    /// <summary>
    /// Every AddedAt mutation used to hit the settings debounce pipeline
    /// immediately — a 2000-file import therefore rescheduled the save once
    /// per file (plus once more per upsert through AssignAddedAt). Inside a
    /// batch mutation scope the dictionary mutations are free and the persist
    /// happens once, at the scope finalization.
    /// </summary>
    private void PersistAddedAtTrackingIfNeeded()
    {
        if (_itemMutationBatchDepth > 0)
        {
            _addedAtPersistPending = true;
            MarkItemMutationBatchDirty();
            return;
        }

        PersistAddedAtTracking();
    }

    private void ResetAddedAtTracking()
    {
        Config.FileAddedAtByPath = new Dictionary<string, DateTimeOffset>(
            StringComparer.OrdinalIgnoreCase);
        Config.FileAddedAtTrackingInitialized = false;
    }

    private void EnsureAddedAtDictionaryComparer()
    {
        if (Config.FileAddedAtByPath is null)
        {
            Config.FileAddedAtByPath = new Dictionary<string, DateTimeOffset>(
                StringComparer.OrdinalIgnoreCase);
            return;
        }

        if (Config.FileAddedAtByPath.Comparer.Equals(StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        Config.FileAddedAtByPath = new Dictionary<string, DateTimeOffset>(
            Config.FileAddedAtByPath,
            StringComparer.OrdinalIgnoreCase);
    }

    private void PersistAddedAtTracking()
    {
        _settingsService.UpdateWidget(Config, notifySubscribers: false);
    }
}
