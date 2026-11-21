using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Helpers;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Projection of zero-input application recommendations after applying manual order,
/// Windows 11-style folder grouping, and hidden-item filtering.
/// </summary>
public sealed record SearchRecommendationProjection(
    IReadOnlyList<RecommendedGridEntry> VisibleEntries,
    IReadOnlyList<SearchResultItem> VisibleFlatItems,
    IReadOnlyList<SearchResultItem> HiddenItems);

/// <summary>
/// Persists user customization of the search popup's application recommendation grid:
/// manual sort order, folder groups, hidden items, and Everything prompt dismissal.
/// Uses source-generated JSON and atomic file replacement for Native AOT safety.
/// </summary>
public sealed partial class SearchRecommendationLayoutService
{
    public const string EntryKindApp = "app";
    public const string EntryKindFolder = "folder";

    private readonly string _storePath;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _saveVersion;
    private long _flushedVersion;
    private PersistedLayoutData _data = new();

    public SearchRecommendationLayoutService()
        : this(Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "search-recommendations.json"))
    {
    }

    internal SearchRecommendationLayoutService(string storePath)
    {
        _storePath = storePath;
        Load();
    }

    public event Action? LayoutChanged;

    public SearchRecommendationProjection BuildProjection(
        IReadOnlyList<SearchResultItem> deduplicatedApps,
        string defaultFolderName)
    {
        if (deduplicatedApps.Count == 0)
        {
            return new SearchRecommendationProjection([], [], []);
        }

        string fallbackFolderName = string.IsNullOrWhiteSpace(defaultFolderName)
            ? "Folder"
            : defaultFolderName.Trim();

        lock (_gate)
        {
            var hiddenSet = new HashSet<string>(_data.HiddenAppKeys, StringComparer.OrdinalIgnoreCase);
            var visibleByKey = new Dictionary<string, SearchResultItem>(StringComparer.OrdinalIgnoreCase);
            var orderedVisibleKeys = new List<string>(deduplicatedApps.Count);
            var hiddenByKey = new Dictionary<string, SearchResultItem>(StringComparer.OrdinalIgnoreCase);

            foreach (SearchResultItem item in deduplicatedApps)
            {
                string key = AppRecommendationIdentity.GetCanonicalKey(item);
                if (hiddenSet.Contains(key))
                {
                    hiddenByKey.TryAdd(key, item);
                }
                else if (visibleByKey.TryAdd(key, item))
                {
                    orderedVisibleKeys.Add(key);
                }
            }

            // Preserve the order in which items were hidden (most recently hidden last or saved order).
            var hiddenItems = new List<SearchResultItem>(hiddenByKey.Count);
            foreach (string hiddenKey in _data.HiddenAppKeys)
            {
                if (hiddenByKey.Remove(hiddenKey, out SearchResultItem? matchedHidden))
                {
                    hiddenItems.Add(matchedHidden);
                }
            }
            foreach (SearchResultItem remainingHidden in hiddenByKey.Values)
            {
                hiddenItems.Add(remainingHidden);
            }

            var consumedVisible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visibleEntries = new List<RecommendedGridEntry>();
            var visibleFlatItems = new List<SearchResultItem>(visibleByKey.Count);

            foreach (PersistedLayoutNode node in _data.Entries)
            {
                if (string.Equals(node.Kind, EntryKindFolder, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(node.FolderId))
                {
                    var folderItems = new List<SearchResultItem>();
                    foreach (string memberKey in node.FolderAppKeys)
                    {
                        if (visibleByKey.TryGetValue(memberKey, out SearchResultItem? member) &&
                            consumedVisible.Add(memberKey))
                        {
                            folderItems.Add(member);
                            visibleFlatItems.Add(member);
                        }
                    }

                    if (folderItems.Count > 0)
                    {
                        string folderTitle = string.IsNullOrWhiteSpace(node.FolderName)
                            ? fallbackFolderName
                            : node.FolderName.Trim();
                        visibleEntries.Add(RecommendedGridEntry.CreateFolder(
                            node.FolderId,
                            folderTitle,
                            folderItems));
                    }
                }
                else if (string.Equals(node.Kind, EntryKindApp, StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(node.AppKey))
                {
                    if (visibleByKey.TryGetValue(node.AppKey, out SearchResultItem? app) &&
                        consumedVisible.Add(node.AppKey))
                    {
                        visibleEntries.Add(RecommendedGridEntry.CreateApp(node.AppKey, app));
                        visibleFlatItems.Add(app);
                    }
                }
            }

            foreach (string key in orderedVisibleKeys)
            {
                if (consumedVisible.Add(key) &&
                    visibleByKey.TryGetValue(key, out SearchResultItem? newApp))
                {
                    visibleEntries.Add(RecommendedGridEntry.CreateApp(key, newApp));
                    visibleFlatItems.Add(newApp);
                }
            }

            return new SearchRecommendationProjection(
                visibleEntries,
                visibleFlatItems,
                hiddenItems);
        }
    }

    public IReadOnlyList<RecommendationFolderSummary> GetFolders(string defaultFolderName)
    {
        string fallback = string.IsNullOrWhiteSpace(defaultFolderName)
            ? "Folder"
            : defaultFolderName.Trim();

        lock (_gate)
        {
            var result = new List<RecommendationFolderSummary>();
            foreach (PersistedLayoutNode node in _data.Entries)
            {
                if (string.Equals(node.Kind, EntryKindFolder, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(node.FolderId) &&
                    node.FolderAppKeys.Count > 0)
                {
                    string name = string.IsNullOrWhiteSpace(node.FolderName)
                        ? fallback
                        : node.FolderName.Trim();
                    result.Add(new RecommendationFolderSummary(node.FolderId, name));
                }
            }

            return result;
        }
    }

    public bool HideApp(string appKey, IReadOnlyList<string> currentVisibleAppKeys)
    {
        if (string.IsNullOrWhiteSpace(appKey))
        {
            return false;
        }

        bool changed = false;
        lock (_gate)
        {
            MaterializeVisibleOrderUnderLock(currentVisibleAppKeys);

            if (!_data.HiddenAppKeys.Any(k => string.Equals(k, appKey, StringComparison.OrdinalIgnoreCase)))
            {
                _data.HiddenAppKeys.Add(appKey);
                changed = true;
            }

            if (RemoveAppFromNodesUnderLock(appKey, dissolveSingleItemFolders: true))
            {
                changed = true;
            }
        }

        if (changed)
        {
            Save();
            LayoutChanged?.Invoke();
        }

        return changed;
    }

    public int HideApps(IReadOnlyList<string> appKeys, IReadOnlyList<string> currentVisibleAppKeys)
    {
        if (appKeys is null || appKeys.Count == 0)
        {
            return 0;
        }

        int hiddenCount = 0;
        bool changed = false;
        lock (_gate)
        {
            MaterializeVisibleOrderUnderLock(currentVisibleAppKeys);

            foreach (string rawKey in appKeys)
            {
                if (string.IsNullOrWhiteSpace(rawKey))
                {
                    continue;
                }

                string appKey = rawKey.Trim();
                if (!_data.HiddenAppKeys.Any(k => string.Equals(k, appKey, StringComparison.OrdinalIgnoreCase)))
                {
                    _data.HiddenAppKeys.Add(appKey);
                    hiddenCount++;
                    changed = true;
                }

                if (RemoveAppFromNodesUnderLock(appKey, dissolveSingleItemFolders: true))
                {
                    changed = true;
                }
            }
        }

        if (changed)
        {
            Save();
            LayoutChanged?.Invoke();
        }

        return hiddenCount;
    }

    public bool UnhideApp(string appKey)
    {
        if (string.IsNullOrWhiteSpace(appKey))
        {
            return false;
        }

        bool changed;
        lock (_gate)
        {
            changed = _data.HiddenAppKeys.RemoveAll(
                existing => string.Equals(existing, appKey, StringComparison.OrdinalIgnoreCase)) > 0;
            if (changed && !ContainsAppKeyUnderLock(appKey))
            {
                _data.Entries.Add(new PersistedLayoutNode
                {
                    Kind = EntryKindApp,
                    AppKey = appKey
                });
            }
        }

        if (changed)
        {
            Save();
            LayoutChanged?.Invoke();
        }

        return changed;
    }

    public int UnhideAllApps()
    {
        int restoredCount;
        lock (_gate)
        {
            restoredCount = _data.HiddenAppKeys.Count;
            if (restoredCount == 0)
            {
                return 0;
            }

            foreach (string key in _data.HiddenAppKeys)
            {
                if (!ContainsAppKeyUnderLock(key))
                {
                    _data.Entries.Add(new PersistedLayoutNode
                    {
                        Kind = EntryKindApp,
                        AppKey = key
                    });
                }
            }

            _data.HiddenAppKeys.Clear();
        }

        Save();
        LayoutChanged?.Invoke();
        return restoredCount;
    }

    public bool MoveTopLevelEntry(
        string sourceEntryKey,
        int targetVisibleIndex,
        IReadOnlyList<string> currentVisibleAppKeys)
    {
        if (string.IsNullOrWhiteSpace(sourceEntryKey))
        {
            return false;
        }

        bool changed = false;
        lock (_gate)
        {
            MaterializeVisibleOrderUnderLock(currentVisibleAppKeys);
            PruneInvisibleTopLevelNodesUnderLock(currentVisibleAppKeys);

            int currentIndex = FindTopLevelNodeIndexUnderLock(sourceEntryKey);
            if (currentIndex < 0)
            {
                return false;
            }

            int clampedTarget = Math.Clamp(targetVisibleIndex, 0, Math.Max(0, _data.Entries.Count - 1));
            if (currentIndex == clampedTarget)
            {
                return false;
            }

            PersistedLayoutNode moving = _data.Entries[currentIndex];
            _data.Entries.RemoveAt(currentIndex);
            clampedTarget = Math.Clamp(clampedTarget, 0, _data.Entries.Count);
            _data.Entries.Insert(clampedTarget, moving);
            changed = true;
        }

        if (changed)
        {
            Save();
            LayoutChanged?.Invoke();
        }

        return changed;
    }

    /// <summary>
    /// Merges <paramref name="draggedAppKey"/> onto <paramref name="targetEntryKey"/>
    /// ("app:&lt;key&gt;" creates a new 2-app folder; "folder:&lt;id&gt;" appends into that folder).
    /// Returns the resulting folder ID, or null if no merge took place.
    /// </summary>
    public string? MergeIntoFolder(
        string draggedAppKey,
        string targetEntryKey,
        string defaultFolderName,
        IReadOnlyList<string> currentVisibleAppKeys)
    {
        if (string.IsNullOrWhiteSpace(draggedAppKey) ||
            string.IsNullOrWhiteSpace(targetEntryKey))
        {
            return null;
        }

        string fallbackName = string.IsNullOrWhiteSpace(defaultFolderName)
            ? "Folder"
            : defaultFolderName.Trim();

        string? resultFolderId = null;
        lock (_gate)
        {
            MaterializeVisibleOrderUnderLock(currentVisibleAppKeys);
            PruneInvisibleTopLevelNodesUnderLock(currentVisibleAppKeys);

            if (targetEntryKey.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
            {
                string folderId = targetEntryKey["folder:".Length..];
                PersistedLayoutNode? targetFolder = _data.Entries.FirstOrDefault(node =>
                    string.Equals(node.Kind, EntryKindFolder, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(node.FolderId, folderId, StringComparison.OrdinalIgnoreCase));
                if (targetFolder is null)
                {
                    return null;
                }

                if (targetFolder.FolderAppKeys.Any(k => string.Equals(k, draggedAppKey, StringComparison.OrdinalIgnoreCase)))
                {
                    return targetFolder.FolderId;
                }

                RemoveAppFromNodesUnderLock(draggedAppKey, dissolveSingleItemFolders: true);
                targetFolder.FolderAppKeys.Add(draggedAppKey);
                resultFolderId = targetFolder.FolderId;
            }
            else if (targetEntryKey.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
            {
                string targetAppKey = targetEntryKey["app:".Length..];
                if (string.Equals(draggedAppKey, targetAppKey, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                int targetCheckIndex = FindTopLevelNodeIndexUnderLock(targetEntryKey);
                if (targetCheckIndex < 0)
                {
                    return null;
                }

                RemoveAppFromNodesUnderLock(draggedAppKey, dissolveSingleItemFolders: true);

                int updatedTargetIndex = FindTopLevelNodeIndexUnderLock(targetEntryKey);
                if (updatedTargetIndex < 0)
                {
                    return null;
                }

                string newFolderId = Guid.NewGuid().ToString("N")[..12];
                _data.Entries[updatedTargetIndex] = new PersistedLayoutNode
                {
                    Kind = EntryKindFolder,
                    FolderId = newFolderId,
                    FolderName = fallbackName,
                    FolderAppKeys = [targetAppKey, draggedAppKey]
                };
                resultFolderId = newFolderId;
            }
        }

        if (resultFolderId is not null)
        {
            Save();
            LayoutChanged?.Invoke();
        }

        return resultFolderId;
    }

    /// <summary>
    /// Merges one or more selected application keys onto <paramref name="targetEntryKey"/>
    /// ("app:&lt;key&gt;" creates a new folder containing the target app plus all dragged apps;
    /// "folder:&lt;id&gt;" appends all dragged apps into that folder) in a single atomic save.
    /// </summary>
    public string? MergeAppsIntoFolder(
        IReadOnlyList<string> draggedAppKeys,
        string targetEntryKey,
        string defaultFolderName,
        IReadOnlyList<string> currentVisibleAppKeys)
    {
        if (draggedAppKeys is null ||
            draggedAppKeys.Count == 0 ||
            string.IsNullOrWhiteSpace(targetEntryKey))
        {
            return null;
        }

        if (draggedAppKeys.Count == 1)
        {
            return MergeIntoFolder(
                draggedAppKeys[0],
                targetEntryKey,
                defaultFolderName,
                currentVisibleAppKeys);
        }

        var normalizedDragged = new List<string>(draggedAppKeys.Count);
        var seenDragged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in draggedAppKeys)
        {
            if (!string.IsNullOrWhiteSpace(raw))
            {
                string trimmed = raw.Trim();
                if (seenDragged.Add(trimmed))
                {
                    normalizedDragged.Add(trimmed);
                }
            }
        }

        if (normalizedDragged.Count == 0)
        {
            return null;
        }

        string fallbackName = string.IsNullOrWhiteSpace(defaultFolderName)
            ? "Folder"
            : defaultFolderName.Trim();

        string? resultFolderId = null;
        bool changed = false;
        lock (_gate)
        {
            MaterializeVisibleOrderUnderLock(currentVisibleAppKeys);
            PruneInvisibleTopLevelNodesUnderLock(currentVisibleAppKeys);

            if (targetEntryKey.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
            {
                string folderId = targetEntryKey["folder:".Length..];
                PersistedLayoutNode? targetFolder = _data.Entries.FirstOrDefault(node =>
                    string.Equals(node.Kind, EntryKindFolder, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(node.FolderId, folderId, StringComparison.OrdinalIgnoreCase));
                if (targetFolder is null)
                {
                    return null;
                }

                foreach (string draggedKey in normalizedDragged)
                {
                    if (targetFolder.FolderAppKeys.Any(
                            k => string.Equals(k, draggedKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    RemoveAppFromNodesUnderLock(draggedKey, dissolveSingleItemFolders: true);
                    targetFolder.FolderAppKeys.Add(draggedKey);
                    changed = true;
                }

                resultFolderId = targetFolder.FolderId;
            }
            else if (targetEntryKey.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
            {
                string targetAppKey = targetEntryKey["app:".Length..];
                var keysToMerge = normalizedDragged
                    .Where(k => !string.Equals(k, targetAppKey, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (keysToMerge.Count == 0)
                {
                    return null;
                }

                if (FindTopLevelNodeIndexUnderLock(targetEntryKey) < 0)
                {
                    return null;
                }

                foreach (string draggedKey in keysToMerge)
                {
                    RemoveAppFromNodesUnderLock(draggedKey, dissolveSingleItemFolders: true);
                }

                int updatedTargetIndex = FindTopLevelNodeIndexUnderLock(targetEntryKey);
                if (updatedTargetIndex < 0)
                {
                    return null;
                }

                string newFolderId = Guid.NewGuid().ToString("N")[..12];
                var folderMembers = new List<string>(1 + keysToMerge.Count) { targetAppKey };
                folderMembers.AddRange(keysToMerge);
                _data.Entries[updatedTargetIndex] = new PersistedLayoutNode
                {
                    Kind = EntryKindFolder,
                    FolderId = newFolderId,
                    FolderName = fallbackName,
                    FolderAppKeys = folderMembers
                };
                resultFolderId = newFolderId;
                changed = true;
            }
        }

        if (changed && resultFolderId is not null)
        {
            Save();
            LayoutChanged?.Invoke();
        }

        return resultFolderId;
    }

    public bool MoveTopLevelEntries(
        IReadOnlyList<string> sourceEntryKeys,
        int targetVisibleIndex,
        IReadOnlyList<string> currentVisibleAppKeys)
    {
        if (sourceEntryKeys is null || sourceEntryKeys.Count == 0)
        {
            return false;
        }

        if (sourceEntryKeys.Count == 1)
        {
            return MoveTopLevelEntry(sourceEntryKeys[0], targetVisibleIndex, currentVisibleAppKeys);
        }

        bool changed = false;
        lock (_gate)
        {
            MaterializeVisibleOrderUnderLock(currentVisibleAppKeys);
            PruneInvisibleTopLevelNodesUnderLock(currentVisibleAppKeys);

            var movingSet = new HashSet<string>(sourceEntryKeys, StringComparer.OrdinalIgnoreCase);
            var movingNodes = new List<PersistedLayoutNode>();
            int adjustedInsertIndex = Math.Clamp(targetVisibleIndex, 0, _data.Entries.Count);

            for (int i = 0; i < _data.Entries.Count; i++)
            {
                string nodeEntryKey = GetEntryKey(_data.Entries[i]);
                if (movingSet.Contains(nodeEntryKey))
                {
                    movingNodes.Add(_data.Entries[i]);
                    if (i < targetVisibleIndex)
                    {
                        adjustedInsertIndex--;
                    }
                }
            }

            if (movingNodes.Count == 0)
            {
                return false;
            }

            var beforeKeys = new List<string>(_data.Entries.Count);
            foreach (PersistedLayoutNode node in _data.Entries)
            {
                beforeKeys.Add(GetEntryKey(node));
            }

            _data.Entries.RemoveAll(node => movingSet.Contains(GetEntryKey(node)));
            adjustedInsertIndex = Math.Clamp(adjustedInsertIndex, 0, _data.Entries.Count);
            _data.Entries.InsertRange(adjustedInsertIndex, movingNodes);

            if (_data.Entries.Count != beforeKeys.Count)
            {
                changed = true;
            }
            else
            {
                for (int i = 0; i < beforeKeys.Count; i++)
                {
                    if (!string.Equals(GetEntryKey(_data.Entries[i]), beforeKeys[i], StringComparison.OrdinalIgnoreCase))
                    {
                        changed = true;
                        break;
                    }
                }
            }
        }

        if (changed)
        {
            Save();
            LayoutChanged?.Invoke();
        }

        return changed;
    }

    private static string GetEntryKey(PersistedLayoutNode node) =>
        string.Equals(node.Kind, EntryKindFolder, StringComparison.OrdinalIgnoreCase)
            ? $"folder:{node.FolderId}"
            : $"app:{node.AppKey}";

    public string? CreateFolderFromApp(
        string appKey,
        string defaultFolderName,
        IReadOnlyList<string> currentVisibleAppKeys)
    {
        if (string.IsNullOrWhiteSpace(appKey))
        {
            return null;
        }

        string fallbackName = string.IsNullOrWhiteSpace(defaultFolderName)
            ? "Folder"
            : defaultFolderName.Trim();

        string? createdFolderId = null;
        lock (_gate)
        {
            MaterializeVisibleOrderUnderLock(currentVisibleAppKeys);
            PruneInvisibleTopLevelNodesUnderLock(currentVisibleAppKeys);

            int topIndex = FindTopLevelNodeIndexUnderLock($"app:{appKey}");
            RemoveAppFromNodesUnderLock(appKey, dissolveSingleItemFolders: true);

            string folderId = Guid.NewGuid().ToString("N")[..12];
            var folderNode = new PersistedLayoutNode
            {
                Kind = EntryKindFolder,
                FolderId = folderId,
                FolderName = fallbackName,
                FolderAppKeys = [appKey]
            };

            int insertIndex = topIndex >= 0
                ? Math.Clamp(topIndex, 0, _data.Entries.Count)
                : _data.Entries.Count;
            _data.Entries.Insert(insertIndex, folderNode);
            createdFolderId = folderId;
        }

        Save();
        LayoutChanged?.Invoke();
        return createdFolderId;
    }

    public bool ReorderInsideFolder(string folderId, string draggedAppKey, int targetIndex)
    {
        if (string.IsNullOrWhiteSpace(folderId) || string.IsNullOrWhiteSpace(draggedAppKey))
        {
            return false;
        }

        bool changed = false;
        lock (_gate)
        {
            PersistedLayoutNode? folder = FindFolderNodeUnderLock(folderId);
            if (folder is null)
            {
                return false;
            }

            int currentIndex = folder.FolderAppKeys.FindIndex(
                k => string.Equals(k, draggedAppKey, StringComparison.OrdinalIgnoreCase));
            if (currentIndex < 0)
            {
                return false;
            }

            int clampedTarget = Math.Clamp(targetIndex, 0, Math.Max(0, folder.FolderAppKeys.Count - 1));
            if (currentIndex == clampedTarget)
            {
                return false;
            }

            string key = folder.FolderAppKeys[currentIndex];
            folder.FolderAppKeys.RemoveAt(currentIndex);
            clampedTarget = Math.Clamp(clampedTarget, 0, folder.FolderAppKeys.Count);
            folder.FolderAppKeys.Insert(clampedTarget, key);
            changed = true;
        }

        if (changed)
        {
            Save();
            LayoutChanged?.Invoke();
        }

        return changed;
    }

    /// <summary>
    /// Removes an application from a folder and places it back onto the top-level grid
    /// immediately after the folder. If the folder had multiple apps and now has 1 (or 0)
    /// left, the folder automatically dissolves in Windows 11 Start Menu fashion.
    /// Returns true if the folder still exists after removal; false if it dissolved.
    /// </summary>
    public bool RemoveFromFolder(
        string folderId,
        string appKey,
        IReadOnlyList<string> currentVisibleAppKeys,
        out bool folderStillExists)
    {
        folderStillExists = false;
        if (string.IsNullOrWhiteSpace(folderId) || string.IsNullOrWhiteSpace(appKey))
        {
            return false;
        }

        bool changed = false;
        lock (_gate)
        {
            MaterializeVisibleOrderUnderLock(currentVisibleAppKeys);

            int folderIndex = _data.Entries.FindIndex(node =>
                string.Equals(node.Kind, EntryKindFolder, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(node.FolderId, folderId, StringComparison.OrdinalIgnoreCase));
            if (folderIndex < 0)
            {
                return false;
            }

            PersistedLayoutNode folder = _data.Entries[folderIndex];
            int removed = folder.FolderAppKeys.RemoveAll(
                k => string.Equals(k, appKey, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                folderStillExists = true;
                return false;
            }

            changed = true;
            if (folder.FolderAppKeys.Count == 0)
            {
                _data.Entries[folderIndex] = new PersistedLayoutNode
                {
                    Kind = EntryKindApp,
                    AppKey = appKey
                };
                folderStillExists = false;
            }
            else if (folder.FolderAppKeys.Count == 1)
            {
                // Dissolve 1-item folder: remaining app stays at folder's slot, extracted app follows it.
                string remainingKey = folder.FolderAppKeys[0];
                _data.Entries[folderIndex] = new PersistedLayoutNode
                {
                    Kind = EntryKindApp,
                    AppKey = remainingKey
                };
                _data.Entries.Insert(folderIndex + 1, new PersistedLayoutNode
                {
                    Kind = EntryKindApp,
                    AppKey = appKey
                });
                folderStillExists = false;
            }
            else
            {
                _data.Entries.Insert(folderIndex + 1, new PersistedLayoutNode
                {
                    Kind = EntryKindApp,
                    AppKey = appKey
                });
                folderStillExists = true;
            }
        }

        if (changed)
        {
            Save();
            LayoutChanged?.Invoke();
        }

        return changed;
    }

    public bool DissolveFolder(string folderId, IReadOnlyList<string> currentVisibleAppKeys)
    {
        if (string.IsNullOrWhiteSpace(folderId))
        {
            return false;
        }

        bool changed = false;
        lock (_gate)
        {
            MaterializeVisibleOrderUnderLock(currentVisibleAppKeys);

            int folderIndex = _data.Entries.FindIndex(node =>
                string.Equals(node.Kind, EntryKindFolder, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(node.FolderId, folderId, StringComparison.OrdinalIgnoreCase));
            if (folderIndex < 0)
            {
                return false;
            }

            PersistedLayoutNode folder = _data.Entries[folderIndex];
            _data.Entries.RemoveAt(folderIndex);
            int insertAt = folderIndex;
            foreach (string memberKey in folder.FolderAppKeys)
            {
                _data.Entries.Insert(insertAt++, new PersistedLayoutNode
                {
                    Kind = EntryKindApp,
                    AppKey = memberKey
                });
            }

            changed = true;
        }

        if (changed)
        {
            Save();
            LayoutChanged?.Invoke();
        }

        return changed;
    }

    public bool RenameFolder(string folderId, string? newName, string defaultFolderName)
    {
        if (string.IsNullOrWhiteSpace(folderId))
        {
            return false;
        }

        string normalizedName = string.IsNullOrWhiteSpace(newName)
            ? (string.IsNullOrWhiteSpace(defaultFolderName) ? "Folder" : defaultFolderName.Trim())
            : newName.Trim();

        bool changed = false;
        lock (_gate)
        {
            PersistedLayoutNode? folder = FindFolderNodeUnderLock(folderId);
            if (folder is null)
            {
                return false;
            }

            if (!string.Equals(folder.FolderName, normalizedName, StringComparison.Ordinal))
            {
                folder.FolderName = normalizedName;
                changed = true;
            }
        }

        if (changed)
        {
            Save();
            LayoutChanged?.Invoke();
        }

        return changed;
    }

    public bool IsEverythingPromptDismissed(bool currentEverythingEnabled)
    {
        lock (_gate)
        {
            if (!_data.EverythingPromptDismissed)
            {
                return false;
            }

            // If the user went to Settings and changed the Everything toggle since dismissing,
            // re-arm the prompt so new status feedback is not permanently suppressed.
            if (_data.EverythingEnabledAtDismiss != currentEverythingEnabled)
            {
                return false;
            }

            return true;
        }
    }

    public void DismissEverythingPrompt(bool currentEverythingEnabled)
    {
        bool changed = false;
        lock (_gate)
        {
            if (!_data.EverythingPromptDismissed ||
                _data.EverythingEnabledAtDismiss != currentEverythingEnabled)
            {
                _data.EverythingPromptDismissed = true;
                _data.EverythingEnabledAtDismiss = currentEverythingEnabled;
                changed = true;
            }
        }

        if (changed)
        {
            Save();
            LayoutChanged?.Invoke();
        }
    }

    public void ResetEverythingPromptDismissal()
    {
        bool changed = false;
        lock (_gate)
        {
            if (_data.EverythingPromptDismissed)
            {
                _data.EverythingPromptDismissed = false;
                changed = true;
            }
        }

        if (changed)
        {
            Save();
        }
    }

    private void MaterializeVisibleOrderUnderLock(IReadOnlyList<string> currentVisibleAppKeys)
    {
        if (currentVisibleAppKeys.Count == 0)
        {
            return;
        }

        var existingKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PersistedLayoutNode node in _data.Entries)
        {
            if (string.Equals(node.Kind, EntryKindFolder, StringComparison.OrdinalIgnoreCase))
            {
                foreach (string member in node.FolderAppKeys)
                {
                    existingKeys.Add(member);
                }
            }
            else if (!string.IsNullOrWhiteSpace(node.AppKey))
            {
                existingKeys.Add(node.AppKey);
            }
        }

        foreach (string visibleKey in currentVisibleAppKeys)
        {
            if (existingKeys.Add(visibleKey))
            {
                _data.Entries.Add(new PersistedLayoutNode
                {
                    Kind = EntryKindApp,
                    AppKey = visibleKey
                });
            }
        }
    }

    private void PruneInvisibleTopLevelNodesUnderLock(IReadOnlyList<string> currentVisibleAppKeys)
    {
        if (currentVisibleAppKeys.Count == 0)
        {
            return;
        }

        var visibleSet = new HashSet<string>(currentVisibleAppKeys, StringComparer.OrdinalIgnoreCase);
        var consumedVisible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenFolderIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalizedEntries = new List<PersistedLayoutNode>(_data.Entries.Count);

        foreach (PersistedLayoutNode node in _data.Entries)
        {
            if (string.Equals(node.Kind, EntryKindFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(node.FolderId) || !seenFolderIds.Add(node.FolderId))
                {
                    continue;
                }

                var keptMembers = new List<string>(node.FolderAppKeys.Count);
                foreach (string memberKey in node.FolderAppKeys)
                {
                    if (!string.IsNullOrWhiteSpace(memberKey) &&
                        visibleSet.Contains(memberKey) &&
                        consumedVisible.Add(memberKey))
                    {
                        keptMembers.Add(memberKey);
                    }
                }

                if (keptMembers.Count > 0)
                {
                    node.FolderAppKeys = keptMembers;
                    normalizedEntries.Add(node);
                }
            }
            else if (string.Equals(node.Kind, EntryKindApp, StringComparison.OrdinalIgnoreCase) &&
                     !string.IsNullOrWhiteSpace(node.AppKey) &&
                     visibleSet.Contains(node.AppKey) &&
                     consumedVisible.Add(node.AppKey))
            {
                normalizedEntries.Add(node);
            }
        }

        _data.Entries.Clear();
        _data.Entries.AddRange(normalizedEntries);
    }

    private int FindTopLevelNodeIndexUnderLock(string entryKey)
    {
        if (entryKey.StartsWith("folder:", StringComparison.OrdinalIgnoreCase))
        {
            string folderId = entryKey["folder:".Length..];
            return _data.Entries.FindIndex(node =>
                string.Equals(node.Kind, EntryKindFolder, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(node.FolderId, folderId, StringComparison.OrdinalIgnoreCase));
        }

        if (entryKey.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
        {
            string appKey = entryKey["app:".Length..];
            return _data.Entries.FindIndex(node =>
                string.Equals(node.Kind, EntryKindApp, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(node.AppKey, appKey, StringComparison.OrdinalIgnoreCase));
        }

        return -1;
    }

    private PersistedLayoutNode? FindFolderNodeUnderLock(string folderId) =>
        _data.Entries.FirstOrDefault(node =>
            string.Equals(node.Kind, EntryKindFolder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(node.FolderId, folderId, StringComparison.OrdinalIgnoreCase));

    private bool ContainsAppKeyUnderLock(string appKey)
    {
        foreach (PersistedLayoutNode node in _data.Entries)
        {
            if (string.Equals(node.Kind, EntryKindFolder, StringComparison.OrdinalIgnoreCase))
            {
                if (node.FolderAppKeys.Any(k => string.Equals(k, appKey, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            else if (string.Equals(node.AppKey, appKey, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private bool RemoveAppFromNodesUnderLock(string appKey, bool dissolveSingleItemFolders)
    {
        bool changed = false;
        for (int i = _data.Entries.Count - 1; i >= 0; i--)
        {
            PersistedLayoutNode node = _data.Entries[i];
            if (string.Equals(node.Kind, EntryKindFolder, StringComparison.OrdinalIgnoreCase))
            {
                int beforeCount = node.FolderAppKeys.Count;
                int removed = node.FolderAppKeys.RemoveAll(
                    k => string.Equals(k, appKey, StringComparison.OrdinalIgnoreCase));
                if (removed > 0)
                {
                    changed = true;
                    if (node.FolderAppKeys.Count == 0)
                    {
                        _data.Entries.RemoveAt(i);
                    }
                    else if (dissolveSingleItemFolders &&
                             beforeCount >= 2 &&
                             node.FolderAppKeys.Count == 1)
                    {
                        _data.Entries[i] = new PersistedLayoutNode
                        {
                            Kind = EntryKindApp,
                            AppKey = node.FolderAppKeys[0]
                        };
                    }
                }
            }
            else if (string.Equals(node.AppKey, appKey, StringComparison.OrdinalIgnoreCase))
            {
                _data.Entries.RemoveAt(i);
                changed = true;
            }
        }

        return changed;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath))
            {
                return;
            }

            using var stream = new FileStream(
                _storePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            PersistedLayoutData? loaded = JsonSerializer.Deserialize(
                stream,
                SearchRecommendationLayoutJsonContext.Default.PersistedLayoutData);
            if (loaded is not null)
            {
                loaded.Entries ??= [];
                loaded.HiddenAppKeys ??= [];
                foreach (PersistedLayoutNode node in loaded.Entries)
                {
                    node.FolderAppKeys ??= [];
                }

                _data = loaded;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SearchRecommendations] Failed to load layout: {ex.Message}");
        }
    }

    private void Save()
    {
        long version = Interlocked.Increment(ref _saveVersion);
        if (TryShouldOffloadSaveFromUiThread())
        {
            _ = Task.Run(() => FlushPendingSaveAsync(version));
            return;
        }

        _writeGate.Wait();
        try
        {
            if (version <= Volatile.Read(ref _flushedVersion))
            {
                return;
            }

            long latestVersion = Volatile.Read(ref _saveVersion);
            PersistToDisk(CaptureSnapshot());
            Volatile.Write(ref _flushedVersion, latestVersion);
        }
        catch (Exception ex)
        {
            App.Log($"[SearchRecommendations] Failed to save layout: {ex.Message}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static bool TryShouldOffloadSaveFromUiThread()
    {
        try
        {
            return Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is not null;
        }
        catch
        {
            return false;
        }
    }

    private async Task FlushPendingSaveAsync(long requestedVersion)
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (requestedVersion <= Volatile.Read(ref _flushedVersion))
            {
                return;
            }

            long latestVersion = Volatile.Read(ref _saveVersion);
            PersistToDisk(CaptureSnapshot());
            Volatile.Write(ref _flushedVersion, latestVersion);
        }
        catch (Exception ex)
        {
            App.Log($"[SearchRecommendations] Failed to save layout: {ex.Message}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private PersistedLayoutData CaptureSnapshot()
    {
        lock (_gate)
        {
            return new PersistedLayoutData
            {
                Version = _data.Version,
                HiddenAppKeys = [.. _data.HiddenAppKeys],
                EverythingPromptDismissed = _data.EverythingPromptDismissed,
                EverythingEnabledAtDismiss = _data.EverythingEnabledAtDismiss,
                Entries = _data.Entries
                    .Select(node => new PersistedLayoutNode
                    {
                        Kind = node.Kind,
                        AppKey = node.AppKey,
                        FolderId = node.FolderId,
                        FolderName = node.FolderName,
                        FolderAppKeys = [.. node.FolderAppKeys]
                    })
                    .ToList()
            };
        }
    }

    private void PersistToDisk(PersistedLayoutData snapshot)
    {
        string? directory = Path.GetDirectoryName(_storePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = $"{_storePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.Create,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(
                    stream,
                    snapshot,
                    SearchRecommendationLayoutJsonContext.Default.PersistedLayoutData);
            }

            DeskBox.Platform.SearchHistoryFileOps.ReplaceStoreFileAtomically(tempPath, _storePath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    DeskBox.Platform.SearchHistoryFileOps.TryDeleteTempFile(tempPath);
                }
            }
            catch
            {
            }
        }
    }

    private sealed class PersistedLayoutData
    {
        public int Version { get; set; } = 1;
        public List<PersistedLayoutNode> Entries { get; set; } = [];
        public List<string> HiddenAppKeys { get; set; } = [];
        public bool EverythingPromptDismissed { get; set; }
        public bool EverythingEnabledAtDismiss { get; set; }
    }

    private sealed class PersistedLayoutNode
    {
        public string Kind { get; set; } = EntryKindApp;
        public string? AppKey { get; set; }
        public string? FolderId { get; set; }
        public string? FolderName { get; set; }
        public List<string> FolderAppKeys { get; set; } = [];
    }

    [JsonSourceGenerationOptions(
        GenerationMode = JsonSourceGenerationMode.Metadata,
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        WriteIndented = true)]
    [JsonSerializable(typeof(PersistedLayoutData), TypeInfoPropertyName = "PersistedLayoutData")]
    private sealed partial class SearchRecommendationLayoutJsonContext : JsonSerializerContext
    {
    }
}
