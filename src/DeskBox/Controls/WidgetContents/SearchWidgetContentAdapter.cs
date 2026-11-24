using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Residency adapter for the first-party desktop Search entry widget.
/// Owns <see cref="SearchWidgetViewModel"/> while letting the XAML leaf view
/// hibernate cleanly when collapsed to a compact capsule.
/// </summary>
public sealed class SearchWidgetContentAdapter :
    WidgetContentAdapterBase,
    IWidgetFeedbackSource,
    IWidgetGroupContentCacheable,
    IWidgetPerformanceAwareContent
{
    private readonly SettingsService _settingsService;
    private readonly SearchWidgetHostCallbacks _hostCallbacks;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private IDisposable? _preferencesSubscription;
    private IDisposable? _everythingSubscription;
    private bool _isWindowVisible = true;
    private bool _isCompactCollapsed;

    public SearchWidgetContentAdapter(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService? settingsService = null,
        SearchWidgetHostCallbacks? hostCallbacks = null,
        Func<SearchWidgetViewModel, FrameworkElement>? viewFactory = null)
        : this(
            config,
            settingsService ?? new SettingsService(),
            localizationService,
            hostCallbacks ?? new SearchWidgetHostCallbacks(),
            viewFactory)
    {
    }

    private SearchWidgetContentAdapter(
        WidgetConfig config,
        SettingsService settingsService,
        LocalizationService localizationService,
        SearchWidgetHostCallbacks hostCallbacks,
        Func<SearchWidgetViewModel, FrameworkElement>? viewFactory)
        : this(
            config,
            CreateViewModel(config, localizationService, settingsService, hostCallbacks),
            settingsService,
            localizationService,
            hostCallbacks,
            viewFactory)
    {
    }

    private SearchWidgetContentAdapter(
        WidgetConfig config,
        SearchWidgetViewModel viewModel,
        SettingsService settingsService,
        LocalizationService localizationService,
        SearchWidgetHostCallbacks hostCallbacks,
        Func<SearchWidgetViewModel, FrameworkElement>? viewFactory)
        : base(
            config,
            () => (viewFactory ?? (vm => new SearchWidgetContent(vm, localizationService)))(viewModel))
    {
        if (config.WidgetKind != WidgetKind.Search)
        {
            throw new ArgumentException("Search content requires a Search widget config.", nameof(config));
        }

        _settingsService = settingsService;
        _hostCallbacks = hostCallbacks;
        ViewModel = viewModel;
        ViewModel.FeedbackRequested += ViewModel_FeedbackRequested;
        _preferencesSubscription = hostCallbacks.SubscribePreferencesChanged?.Invoke(ViewModel.RequestRefresh);
        _everythingSubscription = hostCallbacks.SubscribeEverythingConnectionChanged?.Invoke(ViewModel.RequestRefresh);
    }

    public SearchWidgetViewModel ViewModel { get; }

    public bool IsReadyForReuse => !IsDisposed;

    public event EventHandler<WidgetFeedbackRequestedEventArgs>? FeedbackRequested;

    protected override void OnViewMaterialized(FrameworkElement view)
    {
        if (view is SearchWidgetContent content)
        {
            content.FeedbackRequested += Content_FeedbackRequested;
            content.OnCompactStateChanged(_isCompactCollapsed);
            content.OnWindowVisibilityChanged(_isWindowVisible);
            content.SyncFromResidentViewModel();
        }
    }

    protected override void OnHibernatingView(FrameworkElement view)
    {
        if (view is SearchWidgetContent content)
        {
            content.FeedbackRequested -= Content_FeedbackRequested;
            content.DisposeLeafOnly();
        }
    }

    public override Task InitializeAsync()
    {
        EnsureEverythingSubscription();
        ViewModel.RefreshState();
        TriggerBackgroundEverythingProbe();
        return Task.CompletedTask;
    }

    public override Task RefreshAsync()
    {
        EnsureEverythingSubscription();
        ViewModel.RefreshState();
        TriggerBackgroundEverythingProbe();
        return Task.CompletedTask;
    }

    public override void ApplyAppearance()
    {
        ViewModel.RefreshState();
        if (MaterializedView is SearchWidgetContent content)
        {
            content.ApplyAppearanceVisuals();
        }
    }

    public override void OnCompactStateChanged(bool collapsed)
    {
        _isCompactCollapsed = collapsed;
        if (MaterializedView is SearchWidgetContent content)
        {
            content.OnCompactStateChanged(collapsed);
        }
    }

    public override void OnWindowVisibilityChanged(bool visible)
    {
        _isWindowVisible = visible;
        if (MaterializedView is SearchWidgetContent content)
        {
            content.OnWindowVisibilityChanged(visible);
        }
    }

    public void ApplyPerformanceSettings()
    {
        _ = _settingsService;
    }

    private void EnsureEverythingSubscription()
    {
        _everythingSubscription ??=
            _hostCallbacks.SubscribeEverythingConnectionChanged?.Invoke(ViewModel.RequestRefresh);
    }

    private void TriggerBackgroundEverythingProbe()
    {
        if (_hostCallbacks.ProbeEverythingConnectionAsync is not { } probe ||
            !_settingsService.Settings.Search.SearchEverythingEnabled)
        {
            return;
        }

        _ = RunEverythingProbeSafeAsync(probe);
    }

    private async Task RunEverythingProbeSafeAsync(Func<CancellationToken, Task> probe)
    {
        try
        {
            await probe(_lifetimeCts.Token).ConfigureAwait(false);
            if (IsDisposed)
            {
                return;
            }

            if (_hostCallbacks.UiDispatch is { } dispatch)
            {
                dispatch(() =>
                {
                    if (!IsDisposed)
                    {
                        EnsureEverythingSubscription();
                        ViewModel.RefreshState();
                    }
                });
            }
            else if (!IsDisposed)
            {
                EnsureEverythingSubscription();
                ViewModel.RefreshState();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Log($"[SearchWidget] Everything probe failed: {ex.Message}");
        }
    }

    private void ViewModel_FeedbackRequested(object? sender, WidgetFeedbackRequestedEventArgs e)
    {
        FeedbackRequested?.Invoke(this, e);
    }

    private void Content_FeedbackRequested(object? sender, WidgetFeedbackRequestedEventArgs e)
    {
        FeedbackRequested?.Invoke(this, e);
    }

    protected override void Dispose(bool disposing)
    {
        _lifetimeCts.Cancel();
        _lifetimeCts.Dispose();

        _preferencesSubscription?.Dispose();
        _preferencesSubscription = null;
        _everythingSubscription?.Dispose();
        _everythingSubscription = null;

        if (MaterializedView is SearchWidgetContent content)
        {
            content.FeedbackRequested -= Content_FeedbackRequested;
            content.DisposeLeafOnly();
        }

        ViewModel.FeedbackRequested -= ViewModel_FeedbackRequested;
        ViewModel.Dispose();
    }

    private static SearchWidgetViewModel CreateViewModel(
        WidgetConfig config,
        LocalizationService localizationService,
        SettingsService settingsService,
        SearchWidgetHostCallbacks callbacks)
    {
        return new SearchWidgetViewModel(
            config,
            localizationService,
            settingsService,
            callbacks.HistoryService,
            isEverythingConnected: callbacks.IsEverythingConnected,
            openSearchPopup: callbacks.OpenSearchPopup,
            openSearchPopupWithQuery: callbacks.OpenSearchPopupWithQuery,
            executeSearchAction: callbacks.ExecuteSearchAction,
            openRecommendation: callbacks.OpenRecommendation,
            openSettings: callbacks.OpenSettings,
            copyText: callbacks.CopyText,
            setPrivacyMaskPreference: callbacks.SetPrivacyMaskPreference,
            uiDispatch: callbacks.UiDispatch);
    }
}
