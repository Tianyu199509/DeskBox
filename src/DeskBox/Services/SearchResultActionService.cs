using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Performs secondary actions on search results: attaching a file to a todo,
/// saving a file as a quick-capture note, and copying paths to the clipboard.
/// These actions are surfaced through the result context menu in the search popup.
/// </summary>
public sealed class SearchResultActionService
{
    private readonly SettingsService _settingsService;

    public SearchResultActionService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Creates a todo in the first available Todo widget with the file attached.
    /// Returns a human-readable outcome for display in a tooltip/toast.
    /// </summary>
    public async Task<bool> AttachFileToTodoAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var todoWidget = _settingsService.Settings.Widgets
                .FirstOrDefault(w => w.WidgetKind == WidgetKind.Todo && !w.IsDisabled);

            if (todoWidget is null)
            {
                App.Log("[SearchAction] No Todo widget available to attach to.");
                return false;
            }

            var store = new TodoWidgetStore(todoWidget.Id);
            var data = await store.LoadAsync();

            string fileName = Path.GetFileName(path);
            var item = new TodoItem
            {
                Text = fileName,
                Notes = path,
                SortOrder = data.Items.Count,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            item.Attachments.Add(new TodoAttachment
            {
                FilePath = path,
                DisplayName = fileName,
                Type = "file",
                StorageMode = TodoAttachment.LinkedStorageMode,
                AddedAt = DateTimeOffset.UtcNow
            });

            data.Items.Add(item);
            await store.SaveAsync(data);

            App.Log($"[SearchAction] Attached '{fileName}' to todo widget '{todoWidget.Id}'.");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[SearchAction] Failed to attach file to todo: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Saves a file as a quick-capture note with the file attached.
    /// </summary>
    public async Task<bool> SaveFileToNoteAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return false;
        }

        try
        {
            var store = new QuickCaptureStore();
            var data = await store.LoadAsync();

            string fileName = Path.GetFileName(path);
            var item = new QuickCaptureItem
            {
                Type = QuickCaptureItemType.Text,
                Title = fileName,
                Body = path,
                SourceKind = QuickCaptureSourceKind.DragDrop,
                SortOrder = data.Items.Count,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            item.Attachments.Add(new TodoAttachment
            {
                FilePath = path,
                DisplayName = fileName,
                Type = "file",
                StorageMode = TodoAttachment.LinkedStorageMode,
                AddedAt = DateTimeOffset.UtcNow
            });

            data.Items.Insert(0, item);
            await store.SaveAsync(data);

            App.Log($"[SearchAction] Saved '{fileName}' to quick capture.");
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[SearchAction] Failed to save file to note: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Whether the given result can be attached to a todo (requires an existing file
    /// and at least one enabled Todo widget).
    /// </summary>
    public bool CanAttachToTodo(SearchResultItem? item)
    {
        return item is not null &&
               item.Kind == SearchResultKind.File &&
               !string.IsNullOrWhiteSpace(item.DetailPath) &&
               File.Exists(item.DetailPath) &&
               _settingsService.Settings.Widgets
                   .Any(w => w.WidgetKind == WidgetKind.Todo && !w.IsDisabled);
    }

    /// <summary>
    /// Whether the given result can be saved as a note (requires an existing file).
    /// </summary>
    public bool CanSaveToNote(SearchResultItem? item)
    {
        return item is not null &&
               item.Kind == SearchResultKind.File &&
               !string.IsNullOrWhiteSpace(item.DetailPath) &&
               File.Exists(item.DetailPath);
    }
}
