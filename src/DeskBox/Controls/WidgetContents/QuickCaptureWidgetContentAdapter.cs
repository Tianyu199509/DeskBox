using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Residency adapter for Quick Capture. The adapter owns the view model and
/// the lazy view, so a cached group member keeps its data projection while the
/// leaf control stays a disposable view (roadmap P1-c).
/// </summary>
public sealed class QuickCaptureWidgetContentAdapter :
    WidgetContentAdapterBase,
    IWidgetFeedbackSource,
    IWidgetTransientStateContent,
    IWidgetResponsiveLayoutContent,
    IWidgetHostViewportContent,
    IWidgetInteractiveResizeContent,
    IWidgetAddActionContent,
    IWidgetGroupContentCacheable
{
    private readonly LocalizationService _localizationService;
    private bool _isWindowVisible = true;
    private bool _isCompactCollapsed;

    public QuickCaptureWidgetContentAdapter(
        WidgetConfig config,
        QuickCaptureService quickCaptureService,
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue,
        Func<QuickCaptureWidgetViewModel, FrameworkElement>? viewFactory = null)
        : this(
            config,
            new QuickCaptureWidgetViewModel(
                config,
                quickCaptureService,
                settingsService,
                localizationService,
                dispatcherQueue),
            settingsService,
            localizationService,
            dispatcherQueue,
            viewFactory)
    {
    }

    private QuickCaptureWidgetContentAdapter(
        WidgetConfig config,
        QuickCaptureWidgetViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue,
        Func<QuickCaptureWidgetViewModel, FrameworkElement>? viewFactory)
        : base(
            config,
            () => (viewFactory ?? (vm => new QuickCaptureSurfaceContent(
                vm,
                settingsService,
                localizationService,
                dispatcherQueue)))(viewModel))
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        _localizationService = localizationService;
        ViewModel = viewModel;
    }

    public QuickCaptureWidgetViewModel ViewModel { get; }

    public bool IsReadyForReuse =>
        !IsDisposed &&
        (MaterializedView is QuickCaptureSurfaceContent content
            ? content.IsReadyForReuse
            : ViewModel.IsInitialized);

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    protected override void OnViewMaterialized(FrameworkElement view)
    {
        if (view is QuickCaptureSurfaceContent content)
        {
            content.FeedbackRequested += Content_FeedbackRequested;
            content.OnCompactStateChanged(_isCompactCollapsed);
            content.OnWindowVisibilityChanged(_isWindowVisible);
            content.SyncFromResidentViewModel();
        }
    }

    protected override void OnHibernatingView(FrameworkElement view)
    {
        QuickCaptureSurfaceContent.QuickCaptureHibernatingWork? pendingWork = null;
        if (view is QuickCaptureSurfaceContent content)
        {
            pendingWork = content.ExtractAndReleasePendingWorkBeforeTeardown();
            content.FeedbackRequested -= Content_FeedbackRequested;
            content.DisposeLeafOnly();
        }

        if (pendingWork is not null)
        {
            _ = PersistHibernatingWorkAsync(pendingWork);
        }

        ViewModel.ReleaseAttachmentThumbnails();
        IconHelper.ClearCacheScope(TodoAttachmentViewModel.QuickCapturePreviewCacheScope);
    }

    private async Task PersistHibernatingWorkAsync(
        QuickCaptureSurfaceContent.QuickCaptureHibernatingWork work)
    {
        try
        {
            if (work.IsCreatingDetail)
            {
                string? createdId = null;
                if (work.PendingAttachments.Length > 0)
                {
                    QuickCaptureItemViewModel? attached =
                        await ViewModel.AddItemWithAttachmentsAsync(work.PendingAttachments);
                    if (attached is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(work.Body) ||
                            work.Appearance != QuickCaptureAppearancePreset.Default ||
                            work.ContentFormat != ViewModel.EditorContentFormat)
                        {
                            QuickCaptureWriteResult updateResult =
                                await ViewModel.EditItemDetailsWithResultAsync(
                                    attached,
                                    null,
                                    work.Body,
                                    work.Appearance,
                                    work.ContentFormat);
                            createdId = updateResult.Item?.Id ?? attached.Id;
                        }
                        else
                        {
                            createdId = attached.Id;
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(work.Body))
                {
                    QuickCaptureWriteResult addResult =
                        await ViewModel.AddDetailedItemWithResultAsync(
                            null,
                            work.Body,
                            work.Appearance,
                            work.ContentFormat);
                    createdId = addResult.Item?.Id;
                }

                if (!string.IsNullOrWhiteSpace(createdId) && !IsDisposed)
                {
                    await ViewModel.RefreshItemsAsync();
                    if (MaterializedView is QuickCaptureSurfaceContent liveContent)
                    {
                        liveContent.PromoteHibernatedDraftToPersistedItem(createdId);
                    }
                    else if (HasHibernatedTransientState &&
                             HibernatedTransientState is QuickCaptureExtendedTransientState savedState &&
                             savedState.IsCreatingDetail)
                    {
                        SaveHibernatedTransientState(savedState with
                        {
                            BaseState = savedState.BaseState with
                            {
                                SelectedDetailItemId = createdId,
                                IsDetailEditing = true,
                                DetailDraft = null
                            },
                            IsCreatingDetail = false,
                            PendingDetailAttachments = null
                        });
                    }
                }

                return;
            }

            if (work.ExistingItem is { } existingItem && !string.IsNullOrWhiteSpace(work.Body))
            {
                await ViewModel.EditItemDetailsWithResultAsync(
                    existingItem,
                    null,
                    work.Body,
                    work.Appearance,
                    work.ContentFormat);
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetSurface] Quick Capture hibernate save failed id={WidgetId}: {ex}");
        }
    }

    private void Content_FeedbackRequested(
        object? sender,
        WidgetFeedbackRequestedEventArgs e)
    {
        FeedbackRequested?.Invoke(this, e);
    }

    public override Task InitializeAsync()
    {
        return MaterializedView is QuickCaptureSurfaceContent content
            ? content.InitializeContentAsync()
            : ViewModel.InitializeAsync();
    }

    public override Task RefreshAsync()
    {
        return ViewModel.RefreshItemsAsync();
    }

    public Task AddFromTitleButtonAsync()
    {
        return AsContent(View).AddFromTitleButtonAsync();
    }

    public override void ApplyAppearance()
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.ApplyAppearance();
        }
        else
        {
            ViewModel.ApplyAppearancePreview();
        }
    }

    public override void OnActivated()
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.OnActivated();
        }
    }

    public override void OnDeactivated()
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.OnDeactivated();
        }
    }

    public override void OnCompactStateChanged(bool collapsed)
    {
        _isCompactCollapsed = collapsed;
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.OnCompactStateChanged(collapsed);
        }

        if (collapsed)
        {
            ViewModel.ReleaseAttachmentThumbnails();
            IconHelper.ClearCacheScope(TodoAttachmentViewModel.QuickCapturePreviewCacheScope);
        }
    }

    public override void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.OnWindowVisibilityChanged(visible);
        }
        else if (!visible)
        {
            ViewModel.SuspendWindowRefresh();
        }
    }

    public override void OnWindowLongHidden()
    {
        // Keep any expanded live view intact; collapsed leaf hibernation is owned
        // exclusively by WidgetShell.HibernateHostedContentView().
        ViewModel.ReleaseAttachmentThumbnails();
        IconHelper.ClearCacheScope(TodoAttachmentViewModel.QuickCapturePreviewCacheScope);
    }

    public override void OnWindowRevealCompleted()
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.OnWindowRevealCompleted();
        }
    }

    public void OnHostViewportSizeChanged(double width, double height)
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.OnHostViewportSizeChanged(width, height);
        }
    }

    public void BeginInteractiveResize(double contentWidth, double contentHeight)
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.BeginInteractiveResize(contentWidth, contentHeight);
        }
    }

    public void CompleteInteractiveResize(double contentWidth, double contentHeight)
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.CompleteInteractiveResize(contentWidth, contentHeight);
        }
    }

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.BeginResponsiveLayoutTransition(
                targetContentWidth,
                targetContentHeight,
                isCollapsing);
        }
    }

    public void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.CompleteResponsiveLayoutTransition(
                finalContentWidth,
                finalContentHeight);
        }
    }

    public void CancelResponsiveLayoutTransition()
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.CancelResponsiveLayoutTransition();
        }
    }

    object? IWidgetTransientStateContent.CaptureTransientState()
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            return content.CaptureSwitchTransientState();
        }

        return HasHibernatedTransientState ? HibernatedTransientState : null;
    }

    void IWidgetTransientStateContent.RestoreTransientState(object? state)
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.RestoreSwitchTransientState(state);
            return;
        }

        SaveHibernatedTransientState(state);
    }

    internal Task RevealItemAsync(string? itemId)
    {
        return View is QuickCaptureSurfaceContent content
            ? content.RevealItemAsync(itemId)
            : Task.CompletedTask;
    }

    internal Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<DroppedFilePath> files,
        QuickCaptureItemViewModel? targetItem)
    {
        return MaterializedView is QuickCaptureSurfaceContent content
            ? content.ImportNativeDroppedFilesAsync(files, targetItem)
            : ImportHibernatedNativeDroppedFilesAsync(files, targetItem);
    }

    private async Task<bool> ImportHibernatedNativeDroppedFilesAsync(
        IReadOnlyList<DroppedFilePath> files,
        QuickCaptureItemViewModel? targetItem)
    {
        if (files.Count == 0 || IsDisposed)
        {
            return false;
        }

        try
        {
            QuickCaptureItemViewModel? imported = targetItem is null
                ? await ViewModel.AddItemWithAttachmentsAsync(files)
                : await ViewModel.AddAttachmentsAsync(targetItem, files);
            if (imported is null)
            {
                return false;
            }

            RaiseHibernatedFeedback(
                _localizationService.T("QuickCapture.Dropped"),
                WidgetFeedbackSeverity.Success,
                targetItem is null ? "quick-native-drop" : "quick-native-attach");
            return true;
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetSurface] Quick Capture native file drop failed " +
                $"id={WidgetId}: {ex}");
            RaiseHibernatedFeedback(
                _localizationService.T("Common.OperationFailedRetry"),
                WidgetFeedbackSeverity.Error,
                "quick-native-drop-error");
            return false;
        }
    }

    private void RaiseHibernatedFeedback(
        string message,
        WidgetFeedbackSeverity severity,
        string? deduplicationKey)
    {
        FeedbackRequested?.Invoke(
            this,
            new WidgetFeedbackRequestedEventArgs(
                new WidgetFeedbackRequest(
                    message,
                    severity,
                    deduplicationKey)));
    }

    private QuickCaptureSurfaceContent AsContent(FrameworkElement view)
    {
        return view as QuickCaptureSurfaceContent ??
            throw new InvalidOperationException(
                "Quick Capture content requires the surface leaf view.");
    }

    protected override void Dispose(bool disposing)
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            QuickCaptureSurfaceContent.QuickCaptureHibernatingWork? pendingWork =
                content.ExtractAndReleasePendingWorkBeforeTeardown();
            if (pendingWork is not null)
            {
                _ = PersistHibernatingWorkAsync(pendingWork);
            }

            // The leaf's dispose chain releases view subscriptions and
            // disposes the view model, matching the pre-adapter ownership.
            content.FeedbackRequested -= Content_FeedbackRequested;
            content.Dispose();
        }
        else
        {
            ViewModel.Dispose();
        }

        IconHelper.ClearCacheScope(TodoAttachmentViewModel.QuickCapturePreviewCacheScope);
    }
}
