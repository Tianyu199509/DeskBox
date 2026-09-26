using DeskBox.Contracts;
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
    IWidgetGroupContentCacheable
{
    public TodoWidgetContentAdapter(WidgetConfig config, LocalizationService localizationService)
        : this(config, new TodoWidgetStore(config.Id), localizationService)
    {
    }

    public TodoWidgetContentAdapter(WidgetConfig config, TodoWidgetStore store, LocalizationService localizationService)
        : this(config, new TodoWidgetViewModel(store, localizationService, config))
    {
    }

    public TodoWidgetContentAdapter(WidgetConfig config, TodoWidgetStore store, LocalizationService localizationService, SettingsService settingsService)
        : this(config, new TodoWidgetViewModel(store, localizationService, config, settingsService))
    {
    }

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

    protected override void OnViewMaterialized(FrameworkElement view)
    {
        if (view is TodoWidgetContent todoContent)
        {
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

    public override void OnWindowLongHidden()
    {
        if (MaterializedView is TodoWidgetContent todoContent)
        {
            todoContent.ReleaseTransientRenderingSubscriptions();
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
        return new TodoTransientState(
            ViewModel.InputText,
            ViewModel.DraftImportant,
            ViewModel.DraftDueDate);
    }

    public void RestoreTransientState(object? state)
    {
        if (state is not TodoTransientState todoState)
        {
            return;
        }

        ViewModel.InputText = todoState.InputText;
        ViewModel.DraftImportant = todoState.DraftImportant;
        ViewModel.DraftDueDate = todoState.DraftDueDate;
    }

    public Task AddFromTitleButtonAsync()
    {
        if (View is TodoWidgetContent todoContent)
        {
            todoContent.OpenAddEditor();
        }

        return Task.CompletedTask;
    }

    private sealed record TodoTransientState(
        string InputText,
        bool DraftImportant,
        DateTimeOffset? DraftDueDate);

    internal bool CanImportExternalDrop(DataPackageView dataView)
    {
        return View is TodoWidgetContent todoContent
            ? todoContent.CanImportExternalDrop(dataView)
            : false;
    }

    internal Task<bool> ImportExternalDropAsync(DataPackageView dataView)
    {
        return View is TodoWidgetContent todoContent
            ? todoContent.ImportExternalDropAsync(dataView)
            : Task.FromResult(false);
    }

    internal Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<DroppedFilePath> files,
        TodoItemViewModel? targetItem)
    {
        return View is TodoWidgetContent todoContent
            ? todoContent.ImportNativeDroppedFilesAsync(files, targetItem)
            : Task.FromResult(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (MaterializedView is TodoWidgetContent todoContent)
        {
            todoContent.ReleaseTransientRenderingSubscriptions();
            todoContent.FeedbackRequested -= TodoContent_FeedbackRequested;
        }

        if (ViewModel is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
