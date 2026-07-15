using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class QuickCaptureStore
{
    private const int CurrentVersion = 3;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _storePath;

    public QuickCaptureStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeskBox",
            "data",
            "quick-capture"))
    {
    }

    internal QuickCaptureStore(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _storePath = Path.Combine(dataDir, "quick-capture.json");
    }

    internal string StorePath => _storePath;

    internal string ImageDirectory => Path.Combine(Path.GetDirectoryName(_storePath)!, "images");

    internal string ThumbnailDirectory => Path.Combine(Path.GetDirectoryName(_storePath)!, "thumbnails");

    internal string ExportDirectory => Path.Combine(Path.GetDirectoryName(_storePath)!, "exports");

    internal string AttachmentDirectory => Path.Combine(Path.GetDirectoryName(_storePath)!, "attachments");

    public async Task<QuickCaptureStoreData> LoadAsync()
    {
        if (!File.Exists(_storePath))
        {
            return new QuickCaptureStoreData();
        }

        try
        {
            string json = await File.ReadAllTextAsync(_storePath);
            var data = JsonSerializer.Deserialize<QuickCaptureStoreData>(json, s_jsonOptions);
            return Normalize(data);
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureStore] Failed to load store: {ex}");
            return new QuickCaptureStoreData();
        }
    }

    public async Task SaveAsync(QuickCaptureStoreData data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_storePath)!);
        data = Normalize(data);

        string tempPath = $"{_storePath}.{Guid.NewGuid():N}.tmp";
        string json = JsonSerializer.Serialize(data, s_jsonOptions);
        await File.WriteAllTextAsync(tempPath, json);

        if (File.Exists(_storePath))
        {
            File.Replace(tempPath, _storePath, null);
        }
        else
        {
            File.Move(tempPath, _storePath);
        }
    }

    private static QuickCaptureStoreData Normalize(QuickCaptureStoreData? data)
    {
        data ??= new QuickCaptureStoreData();
        data.Version = CurrentVersion;
        data.Items ??= [];
        data.RecentItems ??= [];

        NormalizeItems(data.Items, isRecent: false);
        NormalizeItems(data.RecentItems, isRecent: true);

        data.Items = data.Items
            .Where(IsValidItem)
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.SortOrder)
            .ThenByDescending(item => item.UpdatedAt)
            .ToList();
        NormalizePinnedSortOrders(data.Items);

        data.RecentItems = data.RecentItems
            .Where(IsValidItem)
            .GroupBy(GetDeduplicationKey, StringComparer.Ordinal)
            .Select(group => group.OrderBy(item => item.SortOrder).ThenByDescending(item => item.UpdatedAt).First())
            .OrderBy(item => item.SortOrder)
            .ThenByDescending(item => item.UpdatedAt)
            .Take(QuickCaptureService.MaxRecentLimit)
            .ToList();

        NormalizeSortOrders(data.Items);
        NormalizeSortOrders(data.RecentItems);
        return data;
    }

    private static void NormalizeItems(List<QuickCaptureItem> items, bool isRecent)
    {
        int sortOrder = 0;
        foreach (var item in items.Where(item => item is not null))
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                item.Id = Guid.NewGuid().ToString("N");
            }

            item.Body = item.Body?.Trim() ?? string.Empty;
            item.Title = string.IsNullOrWhiteSpace(item.Title) ? null : item.Title.Trim();
            item.Url = string.IsNullOrWhiteSpace(item.Url) ? null : item.Url.Trim();
            item.ImagePath = string.IsNullOrWhiteSpace(item.ImagePath) ? null : item.ImagePath.Trim();
            item.ContentHash = string.IsNullOrWhiteSpace(item.ContentHash) ? null : item.ContentHash.Trim();
            item.Attachments ??= [];
            if (!string.IsNullOrWhiteSpace(item.ImagePath) &&
                !item.Attachments.Any(attachment =>
                    string.Equals(attachment.FilePath, item.ImagePath, StringComparison.OrdinalIgnoreCase)))
            {
                item.Attachments.Insert(0, new TodoAttachment
                {
                    FilePath = item.ImagePath,
                    DisplayName = Path.GetFileName(item.ImagePath),
                    Type = "image",
                    StorageMode = TodoAttachment.ManagedStorageMode,
                    AddedAt = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt
                });
            }
            NormalizeAttachments(item.Attachments);
            item.ImagePath ??= item.Attachments
                .FirstOrDefault(attachment => string.Equals(attachment.Type, "image", StringComparison.OrdinalIgnoreCase))
                ?.FilePath;
            if (!string.IsNullOrWhiteSpace(item.ImagePath))
            {
                item.Type = QuickCaptureItemType.Image;
            }
            item.IsRecent = isRecent;
            item.Tags = (item.Tags ?? [])
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();
            if (isRecent)
            {
                item.AppearancePreset = QuickCaptureAppearancePreset.Default;
                item.SourceKind = QuickCaptureSourceKind.Clipboard;
            }
            if (item.CreatedAt == default)
            {
                item.CreatedAt = DateTimeOffset.UtcNow;
            }

            if (item.UpdatedAt == default)
            {
                item.UpdatedAt = item.CreatedAt;
            }

            if (item.SortOrder < 0)
            {
                item.SortOrder = sortOrder;
            }

            if (!item.IsPinned)
            {
                item.PinnedSortOrder = -1;
            }

            sortOrder++;
        }
    }

    private static bool IsValidItem(QuickCaptureItem? item)
    {
        return item is not null &&
               (!string.IsNullOrWhiteSpace(item.Title) ||
                !string.IsNullOrWhiteSpace(item.Body) ||
                item.Attachments.Count > 0 ||
                (item.Type == QuickCaptureItemType.Image && !string.IsNullOrWhiteSpace(item.ImagePath)));
    }

    private static void NormalizeAttachments(List<TodoAttachment> attachments)
    {
        foreach (TodoAttachment attachment in attachments.Where(attachment => attachment is not null))
        {
            attachment.Id = string.IsNullOrWhiteSpace(attachment.Id)
                ? Guid.NewGuid().ToString("N")
                : attachment.Id.Trim();
            attachment.FilePath = attachment.FilePath?.Trim() ?? string.Empty;
            attachment.DisplayName = string.IsNullOrWhiteSpace(attachment.DisplayName)
                ? Path.GetFileName(attachment.FilePath)
                : attachment.DisplayName.Trim();
            attachment.Type = string.IsNullOrWhiteSpace(attachment.Type) ? "file" : attachment.Type.Trim();
            attachment.StorageMode = TodoAttachment.NormalizeStorageMode(attachment.StorageMode);
            attachment.AddedAt = attachment.AddedAt == default ? DateTimeOffset.UtcNow : attachment.AddedAt;
        }

        attachments.RemoveAll(attachment => attachment is null || string.IsNullOrWhiteSpace(attachment.FilePath));
    }

    private static string GetDeduplicationKey(QuickCaptureItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.ContentHash))
        {
            return item.ContentHash;
        }

        if (item.Type == QuickCaptureItemType.Image && !string.IsNullOrWhiteSpace(item.ImagePath))
        {
            return $"image:{item.ImagePath}";
        }

        return item.Body;
    }

    private static void NormalizeSortOrders(List<QuickCaptureItem> items)
    {
        for (int index = 0; index < items.Count; index++)
        {
            items[index].SortOrder = index;
        }
    }

    private static void NormalizePinnedSortOrders(List<QuickCaptureItem> items)
    {
        var pinnedItems = items
            .Where(item => item is not null && item.IsPinned)
            .OrderBy(item => item.PinnedSortOrder < 0 ? int.MaxValue : item.PinnedSortOrder)
            .ThenBy(item => item.SortOrder)
            .ThenByDescending(item => item.UpdatedAt)
            .ToList();

        for (int index = 0; index < pinnedItems.Count; index++)
        {
            pinnedItems[index].PinnedSortOrder = index;
        }

        foreach (var item in items.Where(item => item is not null && !item.IsPinned))
        {
            item.PinnedSortOrder = -1;
        }
    }
}
