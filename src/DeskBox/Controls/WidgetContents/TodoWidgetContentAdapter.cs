using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Content adapter for the future Todo widget. This keeps Todo in the shared
/// content pipeline without making the widget kind user-creatable yet.
/// </summary>
public sealed class TodoWidgetContentAdapter :
    WidgetContentAdapterBase,
    IWidgetAddActionContent,
    IWidgetFeedbackSource,
    IWidgetTransientStateContent,
    IWidgetResponsiveLayoutContent,
    IWidgetInteractiveResizeContent,
    IWidgetGroupContentCacheable,
    IWidgetPerformanceAwareContent
{
    public TodoWidgetContentAdapter(WidgetConfig config, LocalizationService localizationService)
        : this(config, new TodoWidgetStore(config.Id), localizationService)
    {
    }

    public TodoWidgetContentAdapter(WidgetConfig config, TodoWidgetStore store, LocalizationService localizationService)
        : this(config, new TodoWidgetViewModel(
            store,
            localizationService,
            config,
            recycleAttachmentFileAsync: CreateDefaultRecycleKernel()))
    {
    }

    public TodoWidgetContentAdapter(WidgetConfig config, TodoWidgetStore store, LocalizationService localizationService, SettingsService settingsService)
        : this(config, new TodoWidgetViewModel(
            store,
            localizationService,
            config,
            settingsService,
            recycleAttachmentFileAsync: CreateDefaultRecycleKernel()))
    {
    }

    private static Func<string, Task<bool>>? CreateDefaultRecycleKernel() =>
        (App.Current as App)?.FileService is { } fileService
            ? path => fileService.DeleteEntryAsync(path, recycle: true)
            : null;

    internal TodoWidgetContentAdapter(
        WidgetConfig config,
        TodoWidgetViewModel viewModel,
        Func<TodoWidgetViewModel, FrameworkElement>? viewFactory = null)
        : base(
            config,
            () => (viewFactory ?? (vm => new TodoWidgetContent(vm)))(viewModel))
    {
        if (config.WidgetKind != WidgetKind.Todo)
        {
            throw new ArgumentException("Todo content requires a Todo widget config.", nameof(config));
        }

        ViewModel = viewModel;
    }

    public TodoWidgetViewModel ViewModel { get; }

    public bool IsReadyForReuse => ViewModel.IsInitialized && !IsDisposed;

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    private bool _pendingWideSelectionWasAutomatic;
    private bool _isCompactCollapsed;
    private bool _isWindowVisible = true;
    private bool _isHostActivated;

    private bool ShouldKeepCollapsedViewWarm =>
        (ViewModel.SettingsService ?? App.Current?.SettingsService)?.Settings.Performance.KeepCapsuleContentWarm == true;

    private bool ShouldKeepHiddenViewWarm
    {
        get
        {
            SettingsService? settingsService = ViewModel.SettingsService ?? App.Current?.SettingsService;
            if (settingsService is null)
            {
                return true;
            }

            EffectivePerformanceSettings performance =
                PerformanceSettingsPolicy.Resolve(settingsService.Settings);
            return string.Equals(
                performance.HiddenCacheCleanupScope,
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeWarm,
                StringComparison.Ordinal);
        }
    }

    protected override void OnViewMaterialized(FrameworkElement view)
    {
        if (view is TodoWidgetContent todoContent)
        {
            todoContent.WideSelectionWasAutomatic = _pendingWideSelectionWasAutomatic;
            todoContent.SetHostWindowActivated(_isHostActivated);
            todoContent.FeedbackRequested += TodoContent_FeedbackRequested;
        }
    }

    private void TodoContent_FeedbackRequested(
        object? sender,
        WidgetFeedbackRequestedEventArgs e)
    {
        FeedbackRequested?.Invoke(this, e);
    }

    public override Task InitializeAsync()
    {
        return ViewModel.InitializeAsync();
    }

    public override Task RefreshAsync()
    {
        return ViewModel.InitializeAsync();
    }

    public override void ApplyAppearance()
    {
        ViewModel.ApplyAppearance();
    }

    public override void OnActivated()
    {
        _isHostActivated = true;
        if (MaterializedView is TodoWidgetContent todoContent)
        {
            todoContent.SetHostWindowActivated(true);
        }
    }

    public override void OnDeactivated()
    {
        _isHostActivated = false;
        if (MaterializedView is TodoWidgetContent todoContent)
        {
            todoContent.SetHostWindowActivated(false);
        }
    }

    internal void FlushPendingEditsForShutdown()
    {
        if (!IsDisposed && MaterializedView is TodoWidgetContent todoContent)
        {
            todoContent.FlushPendingEditsBeforeTeardown();
        }
    }

    internal void RefreshForDateRollover()
    {
        if (IsDisposed)
        {
            return;
        }

        ViewModel.RefreshIfDateChanged();
        if (MaterializedView is TodoWidgetContent todoContent && todoContent.IsLoaded)
        {
            todoContent.EnsureMidnightRolloverTimerAndRefresh();
        }
    }

    public override void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        if (visible && !_isCompactCollapsed)
        {
            ViewModel.RefreshIfDateChanged();
            if (MaterializedView is TodoWidgetContent todoContent && todoContent.IsLoaded)
            {
                todoContent.EnsureMidnightRolloverTimerAndRefresh();
            }
        }
    }

    public override void OnCompactStateChanged(bool collapsed)
    {
        _isCompactCollapsed = collapsed;
        if (collapsed)
        {
            if (MaterializedView is TodoWidgetContent todoContent)
            {
                todoContent.ReleaseTransientRenderingSubscriptions();
            }

            if (!ShouldKeepCollapsedViewWarm)
            {
                ViewModel.ReleaseAttachmentThumbnails();
                IconHelper.ClearCacheScope(ViewModel.PreviewCacheScope);
            }
        }
        else if (MaterializedView is not null)
        {
            ViewModel.RefreshIfDateChanged();
            if (MaterializedView is TodoWidgetContent todoContent && todoContent.IsLoaded)
            {
                todoContent.EnsureMidnightRolloverTimerAndRefresh();
            }
            ViewModel.EnsureSelectedDetailThumbnailsLoaded();
        }
    }

    public override void OnWindowLongHidden()
    {
        if (MaterializedView is TodoWidgetContent todoContent)
        {
            todoContent.ReleaseTransientRenderingSubscriptions();
        }

        // Keep any expanded live view intact; collapsed leaf hibernation is owned
        // exclusively by WidgetShell.HibernateHostedContentView().
        if (MaterializedView is null || (!_isWindowVisible && !ShouldKeepHiddenViewWarm))
        {
            ViewModel.ReleaseAttachmentThumbnails(
                includeSelectedDetail: MaterializedView is null || _isCompactCollapsed);
            IconHelper.ClearCacheScope(ViewModel.PreviewCacheScope);
        }
    }

    public void ApplyPerformanceSettings()
    {
        if (IsDisposed)
        {
            return;
        }

        if (_isCompactCollapsed && !ShouldKeepCollapsedViewWarm)
        {
            ViewModel.ReleaseAttachmentThumbnails();
            IconHelper.ClearCacheScope(ViewModel.PreviewCacheScope);
        }
    }

    public override void OnWindowRevealCompleted()
    {
        if (!_isCompactCollapsed && MaterializedView is not null)
        {
            ViewModel.RefreshIfDateChanged();
            if (MaterializedView is TodoWidgetContent todoContent && todoContent.IsLoaded)
            {
                todoContent.EnsureMidnightRolloverTimerAndRefresh();
            }
            ViewModel.EnsureSelectedDetailThumbnailsLoaded();
        }
    }

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        if (MaterializedView is TodoWidgetContent todoContent)
        {
            todoContent.BeginResponsiveLayoutTransition(
                targetContentWidth,
                targetContentHeight,
                isCollapsing);
        }
    }

    public void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        if (MaterializedView is TodoWidgetContent todoContent)
        {
            todoContent.CompleteResponsiveLayoutTransition(
                finalContentWidth,
                finalContentHeight);
        }
    }

    public void CancelResponsiveLayoutTransition()
    {
        if (MaterializedView is TodoWidgetContent todoContent)
        {
            todoContent.CancelResponsiveLayoutTransition();
        }
    }

    public void BeginInteractiveResize(double contentWidth, double contentHeight)
    {
        if (MaterializedView is TodoWidgetContent todoContent)
        {
            todoContent.BeginInteractiveResize();
        }
    }

    public void CompleteInteractiveResize(double contentWidth, double contentHeight)
    {
        if (MaterializedView is TodoWidgetContent todoContent)
        {
            todoContent.CompleteInteractiveResize(contentWidth);
        }
    }

    public object? CaptureTransientState()
    {
        bool wideSelectionWasAutomatic = MaterializedView is TodoWidgetContent todoContent
            ? todoContent.WideSelectionWasAutomatic
            : _pendingWideSelectionWasAutomatic;
        return new TodoTransientState(wideSelectionWasAutomatic);
    }

    public void RestoreTransientState(object? state)
    {
        if (state is not TodoTransientState todoState)
        {
            return;
        }

        _pendingWideSelectionWasAutomatic = todoState.WideSelectionWasAutomatic;
        if (MaterializedView is TodoWidgetContent todoContent)
        {
            todoContent.WideSelectionWasAutomatic = todoState.WideSelectionWasAutomatic;
        }
    }

    public Task AddFromTitleButtonAsync()
    {
        if (View is TodoWidgetContent todoContent)
        {
            todoContent.FocusQuickAdd();
        }

        return Task.CompletedTask;
    }

    private sealed record TodoTransientState(
        bool WideSelectionWasAutomatic);

    internal bool CanImportExternalDrop(DataPackageView dataView)
    {
        return MaterializedView is TodoWidgetContent todoContent
            ? todoContent.CanImportExternalDrop(dataView)
            : TodoWidgetContent.CanImportExternalDropData(dataView);
    }

    internal Task<bool> ImportExternalDropAsync(DataPackageView dataView)
    {
        return MaterializedView is TodoWidgetContent todoContent
            ? todoContent.ImportExternalDropAsync(dataView)
            : ImportHibernatedExternalDropAsync(dataView);
    }

    internal Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<DroppedFilePath> files,
        TodoItemViewModel? targetItem)
    {
        return MaterializedView is TodoWidgetContent todoContent
            ? todoContent.ImportNativeDroppedFilesAsync(files, targetItem)
            : ImportHibernatedNativeDroppedFilesAsync(files, targetItem);
    }

    private async Task<bool> ImportHibernatedExternalDropAsync(DataPackageView dataView)
    {
        if (IsDisposed)
        {
            return false;
        }

        try
        {
            if (DeskBoxDragData.TryGetQuickCaptureDragItems(dataView) is { Count: > 0 } qcItems)
            {
                int importedCount = await ViewModel.ImportQuickCaptureItemsAsync(qcItems);
                if (importedCount > 0)
                {
                    RaiseHibernatedTodoFeedback("Todo.Dropped", WidgetFeedbackSeverity.Success);
                    return true;
                }
            }

            if (DeskBoxDragData.HasDroppedFiles(dataView))
            {
                using DroppedFileBatch batch = await DeskBoxDragData.TryGetDroppedFilesAsync(dataView);
                if (batch.Files.Count == 0)
                {
                    string? fallbackText = await TodoWidgetContent.TryGetDroppedTodoTextAsync(dataView);
                    if (string.IsNullOrWhiteSpace(fallbackText))
                    {
                        return false;
                    }

                    await ViewModel.AddItemAsync(fallbackText);
                    RaiseHibernatedTodoFeedback("Todo.Dropped", WidgetFeedbackSeverity.Success);
                    return true;
                }

                return await ImportHibernatedNativeDroppedFilesCoreAsync(batch.Files, targetItem: null);
            }

            string? text = await TodoWidgetContent.TryGetDroppedTodoTextAsync(dataView);
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            await ViewModel.AddItemAsync(text);
            RaiseHibernatedTodoFeedback("Todo.Dropped", WidgetFeedbackSeverity.Success);
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to import dropped content while hibernated: {ex}");
            RaiseHibernatedTodoFeedback("Todo.DropFailed", WidgetFeedbackSeverity.Error);
            return false;
        }
    }

    private async Task<bool> ImportHibernatedNativeDroppedFilesAsync(
        IReadOnlyList<DroppedFilePath> files,
        TodoItemViewModel? targetItem)
    {
        if (files.Count == 0 || IsDisposed)
        {
            return false;
        }

        try
        {
            return await ImportHibernatedNativeDroppedFilesCoreAsync(files, targetItem);
        }
        catch (Exception ex)
        {
            App.Log($"[Todo] Failed to import native dropped files while hibernated: {ex}");
            RaiseHibernatedTodoFeedback("Todo.DropFailed", WidgetFeedbackSeverity.Error);
            return false;
        }
    }

    private async Task<bool> ImportHibernatedNativeDroppedFilesCoreAsync(
        IReadOnlyList<DroppedFilePath> files,
        TodoItemViewModel? targetItem)
    {
        TodoItemViewModel? item = targetItem ??
            (ViewModel.IsDetailPageOpen
                ? ViewModel.SelectedDetailItem
                : await ViewModel.AddItemAsync(TodoWidgetContent.BuildDroppedTodoTitle(files)));
        if (item is null)
        {
            return false;
        }

        int addedCount = await ViewModel.AddDroppedAttachmentsAsync(item.Id, files);
        if (addedCount <= 0)
        {
            return false;
        }

        RaiseHibernatedTodoFeedback("Todo.Dropped", WidgetFeedbackSeverity.Success);
        return true;
    }

    private void RaiseHibernatedTodoFeedback(string resourceKey, WidgetFeedbackSeverity severity) => RaiseHibernatedTodoFeedbackCore(resourceKey, severity);

    internal async Task<(IReadOnlyList<TodoItem> Items, TodoExportStatusScope InitialScope)> PrepareExportSnapshotAsync()
    {
        if (!ViewModel.IsInitialized)
        {
            await ViewModel.InitializeAsync();
        }

        if (MaterializedView is TodoWidgetContent todoContent)
        {
            await todoContent.FlushPendingEditsForExportAsync();
        }

        TodoItem[] snapshot = ViewModel.Items
            .Select(vm => vm.Item)
            .Where(item => !item.IsDeleted && !string.IsNullOrWhiteSpace(item.Text))
            .ToArray();

        TodoExportStatusScope initialScope = ViewModel.SelectedFilter switch
        {
            TodoFilter.Completed => TodoExportStatusScope.CompletedOnly,
            TodoFilter.Active => TodoExportStatusScope.ActiveOnly,
            _ => TodoExportStatusScope.All
        };

        return (snapshot, initialScope);
    }

    private void RaiseHibernatedTodoFeedbackCore(
        string resourceKey,
        WidgetFeedbackSeverity severity)
    {
        LocalizationService? localization = App.Current?.LocalizationService;
        string text = localization is not null ? localization.T(resourceKey) : resourceKey;
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(
                new WidgetFeedbackRequest(
                    text,
                    severity,
                    "todo-status")));
    }

    protected override void OnHibernatingView(FrameworkElement view)
    {
        if (view is TodoWidgetContent todoContent)
        {
            todoContent.FlushPendingEditsBeforeTeardown();
            todoContent.FeedbackRequested -= TodoContent_FeedbackRequested;
            todoContent.Dispose();
        }

        ViewModel.ReleaseAttachmentThumbnails();
        IconHelper.ClearCacheScope(ViewModel.PreviewCacheScope);
    }

    protected override void Dispose(bool disposing)
    {
        if (MaterializedView is TodoWidgetContent todoContent)
        {
            todoContent.FlushPendingEditsBeforeTeardown();
            todoContent.ReleaseTransientRenderingSubscriptions();
            todoContent.FeedbackRequested -= TodoContent_FeedbackRequested;
            todoContent.Dispose();
        }

        ViewModel.ReleaseAttachmentThumbnails();
        IconHelper.ClearCacheScope(ViewModel.PreviewCacheScope);

        if (ViewModel is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
