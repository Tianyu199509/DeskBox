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
    IWidgetContent,
    IWidgetFeedbackSource,
    IWidgetTransientStateContent,
    IWidgetResponsiveLayoutContent,
    IWidgetHostViewportContent,
    IWidgetInteractiveResizeContent,
    IWidgetAddActionContent,
    IWidgetGroupContentCacheable,
    IDisposable
{
    private readonly Func<QuickCaptureWidgetViewModel, FrameworkElement> _viewFactory;
    private readonly QuickCaptureWidgetViewModel _viewModel;
    private FrameworkElement? _view;
    private bool _isDisposed;

    public QuickCaptureWidgetContentAdapter(
        WidgetConfig config,
        QuickCaptureService quickCaptureService,
        SettingsService settingsService,
        LocalizationService localizationService,
        DispatcherQueue dispatcherQueue,
        Func<QuickCaptureWidgetViewModel, FrameworkElement>? viewFactory = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(quickCaptureService);

        Config = config;
        _viewModel = new QuickCaptureWidgetViewModel(
            config,
            quickCaptureService,
            settingsService,
            localizationService,
            dispatcherQueue);
        _viewFactory = viewFactory ?? (viewModel => new QuickCaptureSurfaceContent(
            viewModel,
            settingsService,
            localizationService,
            dispatcherQueue));
    }

    public WidgetConfig Config { get; }

    public string WidgetId => Config.Id;

    public WidgetKind WidgetKind => Config.WidgetKind;

    public QuickCaptureWidgetViewModel ViewModel => _viewModel;

    public FrameworkElement View
    {
        get
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_view is null)
            {
                _view = _viewFactory(_viewModel);
                if (_view is QuickCaptureSurfaceContent content)
                {
                    content.FeedbackRequested += Content_FeedbackRequested;
                }
            }

            return _view;
        }
    }

    public bool IsReadyForReuse =>
        _view is QuickCaptureSurfaceContent content &&
        content.IsReadyForReuse;

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    private void Content_FeedbackRequested(
        object? sender,
        WidgetFeedbackRequestedEventArgs e)
    {
        FeedbackRequested?.Invoke(this, e);
    }

    public Task InitializeAsync()
    {
        return AsContent(View).InitializeContentAsync();
    }

    public Task RefreshAsync()
    {
        return _viewModel.RefreshItemsAsync();
    }

    public Task AddFromTitleButtonAsync()
    {
        return AsContent(View).AddFromTitleButtonAsync();
    }

    public void ApplyAppearance()
    {
        if (_view is QuickCaptureSurfaceContent content)
        {
            content.ApplyAppearance();
        }
    }

    public void OnActivated()
    {
        if (_view is QuickCaptureSurfaceContent content)
        {
            content.OnActivated();
        }
    }

    public void OnDeactivated()
    {
        if (_view is QuickCaptureSurfaceContent content)
        {
            content.OnDeactivated();
        }
    }

    public void OnWindowVisibilityChanged(bool visible)
    {
        if (_view is QuickCaptureSurfaceContent content)
        {
            content.OnWindowVisibilityChanged(visible);
        }
    }

    public void OnWindowRevealCompleted()
    {
        if (_view is QuickCaptureSurfaceContent content)
        {
            content.OnWindowRevealCompleted();
        }
    }

    public void OnHostViewportSizeChanged(double width, double height)
    {
        if (_view is QuickCaptureSurfaceContent content)
        {
            content.OnHostViewportSizeChanged(width, height);
        }
    }

    public void BeginInteractiveResize(double contentWidth, double contentHeight)
    {
        if (_view is QuickCaptureSurfaceContent content)
        {
            content.BeginInteractiveResize(contentWidth, contentHeight);
        }
    }

    public void CompleteInteractiveResize(double contentWidth, double contentHeight)
    {
        if (_view is QuickCaptureSurfaceContent content)
        {
            content.CompleteInteractiveResize(contentWidth, contentHeight);
        }
    }

    public void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        if (_view is QuickCaptureSurfaceContent content)
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
        if (_view is QuickCaptureSurfaceContent content)
        {
            content.CompleteResponsiveLayoutTransition(
                finalContentWidth,
                finalContentHeight);
        }
    }

    public void CancelResponsiveLayoutTransition()
    {
        if (_view is QuickCaptureSurfaceContent content)
        {
            content.CancelResponsiveLayoutTransition();
        }
    }

    object? IWidgetTransientStateContent.CaptureTransientState()
    {
        return _view is QuickCaptureSurfaceContent content
            ? content.CaptureSwitchTransientState()
            : null;
    }

    void IWidgetTransientStateContent.RestoreTransientState(object? state)
    {
        if (_view is QuickCaptureSurfaceContent content)
        {
            content.RestoreSwitchTransientState(state);
        }
    }

    internal Task RevealItemAsync(string? itemId)
    {
        return _view is QuickCaptureSurfaceContent content
            ? content.RevealItemAsync(itemId)
            : Task.CompletedTask;
    }

    internal Task<bool> ImportNativeDroppedFilesAsync(
        IReadOnlyList<DroppedFilePath> files,
        QuickCaptureItemViewModel? targetItem)
    {
        return _view is QuickCaptureSurfaceContent content
            ? content.ImportNativeDroppedFilesAsync(files, targetItem)
            : Task.FromResult(false);
    }

    private QuickCaptureSurfaceContent AsContent(FrameworkElement view)
    {
        return view as QuickCaptureSurfaceContent ??
            throw new InvalidOperationException(
                "Quick Capture content requires the surface leaf view.");
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_view is QuickCaptureSurfaceContent content)
        {
            // The leaf's dispose chain releases view subscriptions and
            // disposes the view model, matching the pre-adapter ownership.
            content.FeedbackRequested -= Content_FeedbackRequested;
            content.Dispose();
        }
        else
        {
            _viewModel.Dispose();
        }
    }
}
