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

    public async Task<(bool Succeeded, string? WidgetId, string? ItemId, string FileName)> AttachFileToTodoWithResultAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path) ||
            !SearchSettingsSlice.IsCategoryFeatureEnabled(_settingsService.Settings, SearchSettingsSlice.CategoryTodo))
        {
            return (false, null, null, string.Empty);
        }

        try
        {
            var todoWidget = _settingsService.Settings.WidgetLayout.Widgets
                .FirstOrDefault(w => w.WidgetKind == WidgetKind.Todo && !w.IsDisabled);

            string targetWidgetId = todoWidget?.Id ?? CustomWidgetHostSnapshotService.FallbackTodoWidgetId;
            string todoWidgetsRoot = CustomWidgetHostSnapshotService.ResolveWidgetsRootForSettings(_settingsService);
            var store = new TodoWidgetStore(todoWidgetsRoot, targetWidgetId);
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
            if (_refreshTodoWidgetAsync is not null && todoWidget is not null)
            {
                await _refreshTodoWidgetAsync(todoWidget.Id);
            }

            App.Log($"[SearchAction] Attached '{fileName}' to todo widget '{targetWidgetId}'.");
            return (true, targetWidgetId, item.Id, fileName);
        }
        catch (Exception ex)
        {
            App.Log($"[SearchAction] Failed to attach file to todo: {ex.Message}");
            return (false, null, null, string.Empty);
        }
    }

    /// <summary>
    /// Saves a file as a quick-capture note with the file attached.
    /// </summary>
    public async Task<bool> SaveFileToNoteAsync(string? path)
    {
        var result = await SaveFileToNoteWithResultAsync(path);
        return result.Succeeded;
    }

    public async Task<(bool Succeeded, string? ItemId, string FileName)> SaveFileToNoteWithResultAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path) ||
            !SearchSettingsSlice.IsCategoryFeatureEnabled(_settingsService.Settings, SearchSettingsSlice.CategoryNote))
        {
            return (false, null, string.Empty);
        }

        try
        {
            string fileName = Path.GetFileName(path);
            if (_quickCaptureService is not null)
            {
                QuickCaptureItem? created = await _quickCaptureService.AddLinkedFileNoteAsync(path);
                if (created is null)
                {
                    return (false, null, string.Empty);
                }

                App.Log($"[SearchAction] Saved '{fileName}' to quick capture via live service.");
                return (true, created.Id, fileName);
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
            return (true, item.Id, fileName);
        }
        catch (Exception ex)
        {
            App.Log($"[SearchAction] Failed to save file to note: {ex.Message}");
            return (false, null, string.Empty);
        }
    }
}
