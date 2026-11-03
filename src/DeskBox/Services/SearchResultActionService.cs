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
    private readonly QuickCaptureService? _quickCaptureService;
    private readonly Func<string, Task>? _refreshTodoWidgetAsync;

    public SearchResultActionService(
        SettingsService settingsService,
        QuickCaptureService? quickCaptureService = null,
        Func<string, Task>? refreshTodoWidgetAsync = null)
    {
        _settingsService = settingsService;
        _quickCaptureService = quickCaptureService;
        _refreshTodoWidgetAsync = refreshTodoWidgetAsync;
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
                Type = AttachmentStorageService.GetAttachmentType(path),
                StorageMode = TodoAttachment.LinkedStorageMode,
                AddedAt = DateTimeOffset.UtcNow
            });

            data.Items.Add(item);
            await store.SaveAsync(data);
            if (_refreshTodoWidgetAsync is not null)
            {
                await _refreshTodoWidgetAsync(todoWidget.Id);
            }

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
            string fileName = Path.GetFileName(path);
            if (_quickCaptureService is not null)
            {
                QuickCaptureItem? created = await _quickCaptureService.AddLinkedFileNoteAsync(path);
                if (created is null)
                {
                    return false;
                }

                App.Log($"[SearchAction] Saved '{fileName}' to quick capture via live service.");
                return true;
            }

            var store = new QuickCaptureStore();
            var data = await store.LoadAsync();

            var item = new QuickCaptureItem
            {
                Type = QuickCaptureItemType.Text,
                Title = fileName,
                Body = path,
                SourceKind = QuickCaptureSourceKind.DragDrop,
                SortOrder = 0,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            item.Attachments.Add(new TodoAttachment
            {
                FilePath = path,
                DisplayName = fileName,
                Type = AttachmentStorageService.GetAttachmentType(path),
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
}
