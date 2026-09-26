using DeskBox.Contracts;
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

        ViewModel = viewModel;
    }

    public QuickCaptureWidgetViewModel ViewModel { get; }

    public bool IsReadyForReuse =>
        MaterializedView is QuickCaptureSurfaceContent content &&
        content.IsReadyForReuse;

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    protected override void OnViewMaterialized(FrameworkElement view)
    {
        if (view is QuickCaptureSurfaceContent content)
        {
            content.FeedbackRequested += Content_FeedbackRequested;
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
        return AsContent(View).InitializeContentAsync();
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

    public override void OnWindowVisibilityChanged(bool visible)
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.OnWindowVisibilityChanged(visible);
        }
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
        return MaterializedView is QuickCaptureSurfaceContent content
            ? content.CaptureSwitchTransientState()
            : null;
    }

    void IWidgetTransientStateContent.RestoreTransientState(object? state)
    {
        if (MaterializedView is QuickCaptureSurfaceContent content)
        {
            content.RestoreSwitchTransientState(state);
        }
    }

    internal Task RevealItemAsync(string? itemId)
    {
        return MaterializedView is QuickCaptureSurfaceContent content
            ? content.RevealItemAsync(itemId)
            : Task.CompletedTask;
    }

    internal Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<DroppedFilePath> files,
        QuickCaptureItemViewModel? targetItem)
    {
        return MaterializedView is QuickCaptureSurfaceContent content
            ? content.ImportNativeDroppedFilesAsync(files, targetItem)
            : Task.FromResult(false);
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
            // The leaf's dispose chain releases view subscriptions and
            // disposes the view model, matching the pre-adapter ownership.
            content.FeedbackRequested -= Content_FeedbackRequested;
            content.Dispose();
        }
        else
        {
            ViewModel.Dispose();
        }
    }
}
