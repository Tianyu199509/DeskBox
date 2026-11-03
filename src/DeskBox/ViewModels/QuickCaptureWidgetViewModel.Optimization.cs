using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.ViewModels;

public sealed record QuickCaptureFilterChipDescriptor(
    string Key,
    string Label,
    int Count,
    bool IsSelected,
    bool IsArchiveChip = false);

public sealed partial class QuickCaptureWidgetViewModel
{
    private string? _activeTagFilter;
    private bool _showArchived;
    private int _archivedCount;
    private string? _mirrorFolderPath;
    private IReadOnlyList<QuickCaptureFilterChipDescriptor> _filterChips = [];

    public QuickCaptureService CaptureService => _quickCaptureService;

    public string? ActiveTagFilter
    {
        get => _activeTagFilter;
        set
        {
            string? normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('#');
            if (SetProperty(ref _activeTagFilter, normalized))
            {
                OnPropertyChanged(nameof(HasActiveFilter));
                ScheduleVisibleItemsRefresh();
            }
        }
    }

    public bool ShowArchived
    {
        get => _showArchived;
        set
        {
            if (SetProperty(ref _showArchived, value))
            {
                OnPropertyChanged(nameof(HasActiveFilter));
                ScheduleVisibleItemsRefresh();
            }
        }
    }

    public bool HasActiveFilter => !string.IsNullOrWhiteSpace(_activeTagFilter) || _showArchived;

    public int ArchivedCount
    {
        get => _archivedCount;
        internal set => SetProperty(ref _archivedCount, value);
    }

    public string? MirrorFolderPath
    {
        get => _mirrorFolderPath;
        internal set
        {
            if (SetProperty(ref _mirrorFolderPath, value))
            {
                OnPropertyChanged(nameof(HasMirrorFolder));
            }
        }
    }

    public bool HasMirrorFolder => !string.IsNullOrWhiteSpace(_mirrorFolderPath);

    public IReadOnlyList<QuickCaptureFilterChipDescriptor> FilterChips
    {
        get => _filterChips;
        internal set
        {
            _filterChips = value;
            OnPropertyChanged(nameof(FilterChips));
        }
    }

    public void ToggleTagFilter(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return;
        }

        string normalized = tag.Trim().TrimStart('#');
        ActiveTagFilter = string.Equals(_activeTagFilter, normalized, StringComparison.CurrentCultureIgnoreCase)
            ? null
            : normalized;
    }

    public void ToggleShowArchived()
    {
        ShowArchived = !ShowArchived;
    }

    public void ClearFilters()
    {
        bool changed = false;
        if (_activeTagFilter is not null)
        {
            _activeTagFilter = null;
            OnPropertyChanged(nameof(ActiveTagFilter));
            changed = true;
        }

        if (_showArchived)
        {
            _showArchived = false;
            OnPropertyChanged(nameof(ShowArchived));
            changed = true;
        }

        if (changed)
        {
            OnPropertyChanged(nameof(HasActiveFilter));
            ScheduleVisibleItemsRefresh();
        }
    }

    public void RefreshVisibleRelativeTimestamps()
    {
        foreach (QuickCaptureItemViewModel item in Items)
        {
            item.RefreshRelativeTimestamps();
        }
    }

    public Task<int> SetArchivedAsync(string itemId, bool isArchived)
    {
        return _quickCaptureService.SetArchivedAsync([itemId], isArchived);
    }

    public Task<int> SetArchivedAsync(IEnumerable<string> itemIds, bool isArchived)
    {
        return _quickCaptureService.SetArchivedAsync(itemIds, isArchived);
    }

    public async Task<QuickCaptureItem?> MergeItemsAsync(IReadOnlyList<string> itemIds)
    {
        QuickCaptureMergeResult result = await _quickCaptureService.MergeItemsAsync(itemIds);
        return result.MergedItem;
    }

    public Task<string?> GetMirrorFolderPathAsync()
    {
        return _quickCaptureService.GetMirrorFolderPathAsync();
    }

    public Task SetMirrorFolderPathAsync(string? folderPath)
    {
        return _quickCaptureService.SetMirrorFolderPathAsync(folderPath);
    }

    public async Task<QuickCaptureItemViewModel?> AddClipboardImageBytesAsync(
        byte[] pngBytes,
        string? body = null)
    {
        QuickCaptureItem? created = await _quickCaptureService.AddImageBytesItemAsync(
            pngBytes,
            extension: ".png",
            pin: IsPinnedView);
        if (created is not null && !string.IsNullOrWhiteSpace(body))
        {
            QuickCaptureWriteResult updated = await _quickCaptureService.UpdateItemDetailsWithResultAsync(
                created.Id,
                created.Title,
                body,
                created.AppearancePreset,
                EditorContentFormat);
            if (updated.Saved && updated.Item is not null)
            {
                created = updated.Item;
            }
        }
        if (created is null)
        {
            return null;
        }

        await RefreshVisibleItemsAsync();
        return Items.FirstOrDefault(entry => string.Equals(entry.Id, created.Id, StringComparison.Ordinal));
    }

    public async Task<QuickCaptureItemViewModel?> AddClipboardImageBytesAttachmentAsync(
        QuickCaptureItemViewModel item,
        byte[] pngBytes)
    {
        string displayName = $"Screenshot_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.png";
        QuickCaptureItem? updated = await _quickCaptureService.AddImageBytesAttachmentAsync(
            item.Id,
            pngBytes,
            displayName);
        if (updated is null)
        {
            return null;
        }

        await RefreshVisibleItemsAsync();
        return Items.FirstOrDefault(entry => string.Equals(entry.Id, item.Id, StringComparison.Ordinal));
    }

    public static async Task<DroppedFilePath?> SaveClipboardImageBytesToTempFileAsync(byte[] pngBytes)
    {
        if (pngBytes is null || pngBytes.Length == 0)
        {
            return null;
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "DeskBox", "QuickCapturePaste");
        Directory.CreateDirectory(tempDir);
        string fileName = $"Screenshot_{DateTimeOffset.Now:yyyyMMdd_HHmmss_fff}.png";
        string tempPath = Path.Combine(tempDir, fileName);
        await File.WriteAllBytesAsync(tempPath, pngBytes);
        return new DroppedFilePath(tempPath, fileName, ForceManagedCopy: true);
    }

    internal static bool ItemMatchesTagFilter(QuickCaptureItem item, string? tagFilter)
    {
        if (string.IsNullOrWhiteSpace(tagFilter))
        {
            return true;
        }

        string normalized = tagFilter.Trim().TrimStart('#');
        string prefix = normalized + "/";
        foreach (string tag in item.Tags ?? [])
        {
            if (string.Equals(tag, normalized, StringComparison.CurrentCultureIgnoreCase) ||
                tag.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
