using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed class TodoWidgetStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _storePath;

    public TodoWidgetStore(string widgetId)
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeskBox",
                "data",
                "widgets"),
            widgetId)
    {
    }

    internal TodoWidgetStore(string widgetsDataRoot, string widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            throw new ArgumentException("Widget id cannot be empty.", nameof(widgetId));
        }

        string safeWidgetId = SanitizeWidgetId(widgetId);
        string dataDir = Path.Combine(widgetsDataRoot, safeWidgetId);
        Directory.CreateDirectory(dataDir);
        _storePath = Path.Combine(dataDir, "todo.json");
    }

    internal string StorePath => _storePath;

    public async Task<TodoWidgetData> LoadAsync()
    {
        if (!File.Exists(_storePath))
        {
            return new TodoWidgetData();
        }

        try
        {
            string json = await File.ReadAllTextAsync(_storePath);
            var data = JsonSerializer.Deserialize<TodoWidgetData>(json, s_jsonOptions);
            return Normalize(data);
        }
        catch (Exception ex)
        {
            App.Log($"[TodoWidgetStore] Failed to load store: {ex}");
            return new TodoWidgetData();
        }
    }

    public async Task SaveAsync(TodoWidgetData data)
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

    private static TodoWidgetData Normalize(TodoWidgetData? data)
    {
        data ??= new TodoWidgetData();
        data.Version = Math.Max(1, data.Version);
        data.Items ??= [];

        int fallbackSortOrder = 0;
        foreach (var item in data.Items.Where(item => item is not null))
        {
            if (string.IsNullOrWhiteSpace(item.Id))
            {
                item.Id = Guid.NewGuid().ToString("N");
            }

            item.Text = item.Text?.Trim() ?? string.Empty;
            item.ColorMarker = TodoItem.NormalizeColorMarker(item.ColorMarker);
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
                item.SortOrder = fallbackSortOrder;
            }

            fallbackSortOrder++;
        }

        data.Items = data.Items
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Text))
            .GroupBy(item => item.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(item => item.SortOrder)
            .ThenByDescending(item => item.UpdatedAt)
            .ToList();

        NormalizeSortOrders(data.Items);
        return data;
    }

    private static void NormalizeSortOrders(List<TodoItem> items)
    {
        for (int index = 0; index < items.Count; index++)
        {
            items[index].SortOrder = index;
        }
    }

    private static string SanitizeWidgetId(string widgetId)
    {
        string trimmed = widgetId.Trim();
        char[] invalidChars = Path.GetInvalidFileNameChars();
        var safeChars = trimmed.Select(ch => invalidChars.Contains(ch) ? '_' : ch).ToArray();
        string safe = new(safeChars);
        return string.IsNullOrWhiteSpace(safe) ? Guid.NewGuid().ToString("N") : safe;
    }

}
