using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox;

public partial class App
{
    private QuickCaptureWindowCoordinator? _quickCaptureWindowCoordinator;

    internal QuickCaptureWindowCoordinator EnsureQuickCaptureWindowCoordinator()
    {
        return _quickCaptureWindowCoordinator ??= new QuickCaptureWindowCoordinator(
            QuickCaptureService,
            SettingsService,
            ThemeService,
            LocalizationService);
    }

    public void OpenQuickCapturePopoutWindow(
        string itemId,
        bool startAlwaysOnTop = false,
        bool startEditing = false)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        EnsureQuickCaptureWindowCoordinator().OpenOrActivatePopout(
            itemId,
            startAlwaysOnTop,
            startEditing);
    }

    public void ShowQuickJotWindow()
    {
        IntPtr prev = Win32Helper.GetForegroundWindow();
        EnsureQuickCaptureWindowCoordinator().ShowQuickJot(prev);
    }

    private void AppendQuickCapturePopoutWindowHandles(List<IntPtr> handles)
    {
        _quickCaptureWindowCoordinator?.AppendNonTopmostHandles(handles);
    }

    private SearchEngineService CreateConfiguredSearchEngineService()
    {
        return new SearchEngineService(
            SettingsService,
            LocalizationService,
            _everythingSearchService!,
            QuickCaptureService,
            clipboardItemsProvider: GetClipboardItemsForSearchAsync,
            frecencyBoostProvider: _searchHistoryService is { } history
                ? history.GetFrecencyBoost
                : null,
            clipboardPreviewImageResolver: clip =>
                FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.Clipboard)
                    ? EnsureClipboardHistoryService().ResolveThumbnailPath(clip)
                    : null);
    }

    private async Task<IReadOnlyList<ClipboardHistoryItem>> GetClipboardItemsForSearchAsync(
        CancellationToken cancellationToken)
    {
        if (IsShuttingDown ||
            !FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.Clipboard))
        {
            return [];
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await EnsureClipboardHistoryService().GetItemsAsync().ConfigureAwait(false);
    }

    private async Task HandleQuickCaptureSearchActionAsync(string actionId)
    {
        if (string.IsNullOrWhiteSpace(actionId))
        {
            return;
        }

        if (string.Equals(actionId, "quick-jot", StringComparison.Ordinal))
        {
            ShowQuickJotWindow();
            return;
        }

        const string openSettingsPrefix = "open-settings:";
        if (actionId.StartsWith(openSettingsPrefix, StringComparison.Ordinal))
        {
            string target = actionId[openSettingsPrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(target))
            {
                ShowSettings(target);
            }
            return;
        }

        if (actionId.StartsWith(SearchWebAndUrlEvaluator.OpenUrlActionPrefix, StringComparison.Ordinal))
        {
            string targetUrl = actionId[SearchWebAndUrlEvaluator.OpenUrlActionPrefix.Length..].Trim();
            SearchWebAndUrlEvaluator.TryLaunchSafeExternalUri(targetUrl);
            return;
        }

        if (actionId.StartsWith(SearchWebAndUrlEvaluator.WebSearchActionPrefix, StringComparison.Ordinal))
        {
            string searchUrl = actionId[SearchWebAndUrlEvaluator.WebSearchActionPrefix.Length..].Trim();
            SearchWebAndUrlEvaluator.TryLaunchSafeExternalUri(searchUrl);
            return;
        }

        const string copyCalcPrefix = "copy-calc:";
        if (actionId.StartsWith(copyCalcPrefix, StringComparison.Ordinal))
        {
            string calcText = actionId[copyCalcPrefix.Length..];
            if (!string.IsNullOrEmpty(calcText))
            {
                CopyPlainTextToSystemClipboard(calcText);
            }
            return;
        }

        const string clipboardCopyPrefix = "clipboard-copy:";
        if (actionId.StartsWith(clipboardCopyPrefix, StringComparison.Ordinal))
        {
            string clipId = actionId[clipboardCopyPrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(clipId) &&
                FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.Clipboard))
            {
                ClipboardHistoryService clipService = EnsureClipboardHistoryService();
                IReadOnlyList<ClipboardHistoryItem> items = await clipService.GetItemsAsync();
                ClipboardHistoryItem? matched = items.FirstOrDefault(
                    x => string.Equals(x.Id, clipId, StringComparison.Ordinal));
                if (matched is not null)
                {
                    await clipService.CopyItemToClipboardAsync(matched);
                }
            }
            return;
        }

        const string quickTodoOpenPrefix = "quick-todo-open:";
        if (actionId.StartsWith(quickTodoOpenPrefix, StringComparison.Ordinal))
        {
            string todoText = actionId[quickTodoOpenPrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(todoText))
            {
                await QuickCreateTodoFromSearchAsync(todoText, openAfterSave: true);
            }
            return;
        }

        const string quickTodoPrefix = "quick-todo:";
        if (actionId.StartsWith(quickTodoPrefix, StringComparison.Ordinal))
        {
            string todoText = actionId[quickTodoPrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(todoText))
            {
                await QuickCreateTodoFromSearchAsync(todoText, openAfterSave: false);
            }
            return;
        }

        const string quickNoteOpenPrefix = "quick-note-open:";
        if (actionId.StartsWith(quickNoteOpenPrefix, StringComparison.Ordinal))
        {
            string body = actionId[quickNoteOpenPrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(body))
            {
                await QuickCreateNoteFromSearchAsync(body, openAfterSave: true);
            }
            return;
        }

        const string quickNotePrefix = "quick-note:";
        if (actionId.StartsWith(quickNotePrefix, StringComparison.Ordinal))
        {
            string body = actionId[quickNotePrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(body))
            {
                await QuickCreateNoteFromSearchAsync(body, openAfterSave: false);
            }
        }
    }

    private static void CopyPlainTextToSystemClipboard(string value)
    {
        try
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText(value);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);
            Windows.ApplicationModel.DataTransfer.Clipboard.Flush();
        }
        catch (Exception ex)
        {
            Log($"[Search] Failed to copy calculation result to clipboard: {ex.Message}");
        }
    }

    private async Task QuickCreateTodoFromSearchAsync(string todoText, bool openAfterSave = false)
    {
        var result = await CreateTodoItemWithResultFromTextAsync(todoText);
        if (!result.Succeeded)
        {
            return;
        }

        if (openAfterSave)
        {
            if (WidgetManager is not null && !string.IsNullOrWhiteSpace(result.WidgetId))
            {
                await WidgetManager.ShowTodoReminderTargetAsync(
                    result.WidgetId,
                    result.ItemId,
                    preferTodayFilter: false);
            }
            return;
        }

        TryShowSearchQuickCreateHudForTodo(result.NormalizedText, result.WidgetId, result.ItemId);
    }

    private async Task QuickCreateNoteFromSearchAsync(string body, bool openAfterSave = false)
    {
        if (QuickCaptureService is null ||
            string.IsNullOrWhiteSpace(body) ||
            !FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.QuickCapture))
        {
            return;
        }

        TextContentFormat format = string.Equals(
            SettingsService.NormalizeQuickCaptureFormat(
                SettingsService.Settings.QuickCapture.QuickCaptureDefaultFormat),
            SettingsService.QuickCaptureFormatPlainText,
            StringComparison.Ordinal)
            ? TextContentFormat.PlainText
            : TextContentFormat.Markdown;
        var writeResult = await QuickCaptureService.AddDetailedItemWithResultAsync(
            null,
            body,
            QuickCaptureAppearancePreset.Default,
            pin: false,
            contentFormat: format);
        if (!writeResult.Saved || writeResult.Item is null)
        {
            return;
        }

        string createdItemId = writeResult.Item.Id;
        if (openAfterSave)
        {
            if (WidgetManager is not null)
            {
                var window = await WidgetManager.CreateOrShowQuickCaptureWidgetAsync();
                await window.RevealItemAsync(createdItemId);
            }
            return;
        }

        TryShowSearchQuickCreateHudForNote(body, createdItemId);
    }

    internal void TryShowSearchQuickCreateHudForTodo(
        string? rawText,
        string? widgetId,
        string? itemId)
    {
        if (!SettingsService.Settings.Search.SearchQuickCreateShowHud)
        {
            return;
        }

        string snippet = FormatHudSnippet(rawText);
        string message = string.IsNullOrWhiteSpace(snippet)
            ? LocalizationService.T("Search.Action.AttachedToTodo")
            : string.Format(LocalizationService.T("Search.QuickCreate.HudSavedTodoFormat"), snippet);
        string actionLabel = LocalizationService.T("Search.QuickCreate.HudViewTodo");
        Func<Task>? onAction = !string.IsNullOrWhiteSpace(widgetId)
            ? async () =>
            {
                if (WidgetManager is not null)
                {
                    await WidgetManager.ShowTodoReminderTargetAsync(
                        widgetId,
                        itemId,
                        preferTodayFilter: false);
                }
            }
            : null;

        DesktopToastHudService.Shared.TryShow(
            new DesktopToastRequest(
                HintId: string.Empty,
                Message: message,
                Severity: DesktopToastSeverity.Success,
                Priority: DesktopToastPriority.ContextualGuide,
                ActionButtonText: onAction is not null ? actionLabel : null,
                OnActionAsync: onAction,
                ShowDismissButton: true,
                DurationMs: 4200),
            SettingsService);
    }

    internal void TryShowSearchQuickCreateHudForNote(
        string? rawText,
        string? itemId)
    {
        if (!SettingsService.Settings.Search.SearchQuickCreateShowHud)
        {
            return;
        }

        string snippet = FormatHudSnippet(rawText);
        string message = string.IsNullOrWhiteSpace(snippet)
            ? LocalizationService.T("Search.Action.SavedToNote")
            : string.Format(LocalizationService.T("Search.QuickCreate.HudSavedNoteFormat"), snippet);
        string actionLabel = LocalizationService.T("Search.QuickCreate.HudViewNote");
        Func<Task>? onAction = !string.IsNullOrWhiteSpace(itemId)
            ? async () =>
            {
                if (WidgetManager is not null)
                {
                    var window = await WidgetManager.CreateOrShowQuickCaptureWidgetAsync();
                    await window.RevealItemAsync(itemId);
                }
            }
            : null;

        DesktopToastHudService.Shared.TryShow(
            new DesktopToastRequest(
                HintId: string.Empty,
                Message: message,
                Severity: DesktopToastSeverity.Success,
                Priority: DesktopToastPriority.ContextualGuide,
                ActionButtonText: onAction is not null ? actionLabel : null,
                OnActionAsync: onAction,
                ShowDismissButton: true,
                DurationMs: 4200),
            SettingsService);
    }

    private static string FormatHudSnippet(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        string singleLine = rawText
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return singleLine.Length > 28
            ? string.Concat(singleLine.AsSpan(0, 28), "…")
            : singleLine;
    }

    public async Task<bool> CreateTodoItemFromTextAsync(string todoText)
    {
        var result = await CreateTodoItemWithResultFromTextAsync(todoText);
        return result.Succeeded;
    }

    internal async Task<(bool Succeeded, string? WidgetId, string? ItemId, string NormalizedText)> CreateTodoItemWithResultFromTextAsync(string todoText)
    {
        if (WidgetManager is null ||
            string.IsNullOrWhiteSpace(todoText) ||
            !FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.Todo))
        {
            return (false, null, null, string.Empty);
        }

        string normalized = todoText
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (normalized.Length == 0)
        {
            return (false, null, null, string.Empty);
        }

        if (normalized.Length > 240)
        {
            normalized = string.Concat(normalized.AsSpan(0, 240), "…");
        }

        WidgetConfig? todoConfig = SettingsService.Settings.WidgetLayout.Widgets
            .FirstOrDefault(w => w.WidgetKind == WidgetKind.Todo && !w.IsDisabled);
        if (todoConfig is null)
        {
            ContentWidgetWindow createdWindow = await WidgetManager.CreateTodoWidgetAsync();
            todoConfig = SettingsService.Settings.WidgetLayout.Widgets
                .FirstOrDefault(w => string.Equals(w.Id, createdWindow.Identity.WidgetId, StringComparison.Ordinal));
            if (todoConfig is null)
            {
                return (false, null, null, string.Empty);
            }
        }

        var store = new TodoWidgetStore(todoConfig.Id);
        TodoWidgetData data = await store.LoadAsync();
        bool addToBottom = SettingsService.NormalizeTodoNewTaskPosition(
                               SettingsService.Settings.Todo.TodoNewTaskPosition) ==
                           SettingsService.TodoNewTaskPositionBottom;
        int sortOrder = addToBottom
            ? (data.Items.Count == 0 ? 0 : data.Items.Max(i => i.SortOrder) + 1)
            : (data.Items.Count == 0 ? 0 : data.Items.Min(i => i.SortOrder) - 1);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var item = new TodoItem
        {
            Text = normalized,
            SortOrder = sortOrder,
            CreatedAt = now,
            UpdatedAt = now
        };
        if (addToBottom)
        {
            data.Items.Add(item);
        }
        else
        {
            data.Items.Insert(0, item);
        }

        await store.SaveAsync(data);
        await WidgetManager.RefreshTodoWidgetAsync(todoConfig.Id);
        return (true, todoConfig.Id, item.Id, normalized);
    }

    public async Task<bool> SaveClipboardHistoryItemToQuickCaptureAsync(ClipboardHistoryItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (QuickCaptureService is null ||
            !FeatureWidgetSettings.IsEnabled(SettingsService.Settings, WidgetKind.QuickCapture))
        {
            return false;
        }

        ClipboardHistoryService clipService = EnsureClipboardHistoryService();
        switch (item.Kind)
        {
            case ClipboardItemKind.Image:
            {
                string? imagePath = clipService.GetAbsolutePath(item.ImageRelativePath);
                if (!string.IsNullOrWhiteSpace(imagePath) && File.Exists(imagePath))
                {
                    return await QuickCaptureService.AddImageFileItemAsync(imagePath) is not null;
                }

                return false;
            }

            case ClipboardItemKind.Files:
            {
                string[] existingFiles = item.FilePaths.Where(File.Exists).ToArray();
                if (existingFiles.Length > 0)
                {
                    return await QuickCaptureService.AddItemWithAttachmentsAsync(
                        existingFiles,
                        copyToManagedStorage: false) is not null;
                }

                if (item.FilePaths.Count > 0)
                {
                    await QuickCaptureService.AddItemAsync(string.Join(Environment.NewLine, item.FilePaths));
                    return true;
                }

                return false;
            }

            default:
            {
                string text = await clipService.GetFullTextAsync(item);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    await QuickCaptureService.AddItemAsync(text);
                    return true;
                }

                return false;
            }
        }
    }
}
