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
        data.Version = Math.Max(2, data.Version);
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
            item.Recurrence = TodoRecurrence.Normalize(item.Recurrence, item.DueDate);
            item.RecurrenceSeriesId = TodoRecurrenceService.NormalizeSeriesId(item.RecurrenceSeriesId);
            item.ReminderOffsetMinutes = TodoReminderOptions.NormalizeOffsetMinutes(item.ReminderOffsetMinutes);
            item.GeneratedNextItemId = string.IsNullOrWhiteSpace(item.GeneratedNextItemId)
                ? null
                : item.GeneratedNextItemId.Trim();
            if (item.CreatedAt == default)
            {
                item.CreatedAt = DateTimeOffset.UtcNow;
            }

            if (item.UpdatedAt == default)
            {
                item.UpdatedAt = item.CreatedAt;
            }

            if (item.IsCompleted)
            {
                item.CompletedAt ??= item.UpdatedAt == default ? item.CreatedAt : item.UpdatedAt;
            }
            else
            {
                item.CompletedAt = null;
            }

            if (item.DueDate is null)
            {
                item.Recurrence = null;
                item.RecurrenceSeriesId = null;
                item.ReminderLastNotifiedAt = null;
                item.ReminderDismissedForDueDate = null;
                item.SnoozedUntil = null;
                item.SnoozeLastNotifiedAt = null;
            }
            else if (item.IsCompleted)
            {
                item.SnoozedUntil = null;
                item.SnoozeLastNotifiedAt = null;
            }
            else if (item.ReminderDismissedForDueDate is { } dismissedForDueDate &&
                     !DateTimeOffset.Equals(dismissedForDueDate, item.DueDate.Value))
            {
                item.ReminderLastNotifiedAt = null;
                item.ReminderDismissedForDueDate = null;
                item.SnoozedUntil = null;
                item.SnoozeLastNotifiedAt = null;
            }

            if (!item.IsCompleted || item.Recurrence is null)
            {
                item.GeneratedNextItemId = null;
            }

            if (item.Recurrence is null)
            {
                item.RecurrenceSeriesId = null;
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

        NormalizeRecurrenceSeriesIds(data.Items);
        NormalizeSortOrders(data.Items);
        return data;
    }

    private static void NormalizeRecurrenceSeriesIds(List<TodoItem> items)
    {
        var recurringItems = items
            .Where(item => item.Recurrence is not null)
            .ToList();
        if (recurringItems.Count == 0)
        {
            return;
        }

        var itemsById = recurringItems.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var visitedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in recurringItems)
        {
            if (!visitedIds.Add(item.Id))
            {
                continue;
            }

            var component = new List<TodoItem>();
            var queue = new Queue<TodoItem>();
            queue.Enqueue(item);

            while (queue.Count > 0)
            {
                TodoItem current = queue.Dequeue();
                component.Add(current);

                if (!string.IsNullOrWhiteSpace(current.GeneratedNextItemId) &&
                    itemsById.TryGetValue(current.GeneratedNextItemId, out TodoItem? nextItem) &&
                    visitedIds.Add(nextItem.Id))
                {
                    queue.Enqueue(nextItem);
                }

                foreach (var previousItem in recurringItems.Where(entry =>
                             string.Equals(entry.GeneratedNextItemId, current.Id, StringComparison.Ordinal)))
                {
                    if (visitedIds.Add(previousItem.Id))
                    {
                        queue.Enqueue(previousItem);
                    }
                }
            }

            string seriesId = component
                .Select(entry => TodoRecurrenceService.NormalizeSeriesId(entry.RecurrenceSeriesId))
                .FirstOrDefault(seriesId => !string.IsNullOrWhiteSpace(seriesId))
                ?? Guid.NewGuid().ToString("N");

            foreach (var componentItem in component)
            {
                componentItem.RecurrenceSeriesId = seriesId;
            }
        }
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
