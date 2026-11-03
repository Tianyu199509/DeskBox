using System.Collections.ObjectModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Minimal desktop entry point for search: launch, reopen a recent query, or
/// remove recent-query history.
/// </summary>
public sealed partial class SearchWidgetContent : UserControl, IDisposable
{
    private readonly LocalizationService _localizationService;
    private readonly SettingsService? _settingsService;
    private readonly ObservableCollection<SearchHistoryEntry> _recentQueries = [];
    private SearchHistoryService? _subscribedHistoryService;
    private bool _externalSubscriptionsAttached;
    private bool _historyRefreshQueued;
    private bool _hasHotkeyBadge;
    private bool _isResponsiveLayoutTransitionActive;
    private double _responsiveTargetWidth;
    private double _responsiveTargetHeight;
    private bool _isDisposed;

    public SearchWidgetContent(
        LocalizationService localizationService,
        SettingsService? settingsService = null)
    {
        _localizationService = localizationService;
        _settingsService = settingsService;

        InitializeComponent();
        HistoryList.ItemsSource = _recentQueries;
        Loaded += SearchWidgetContent_Loaded;
        Unloaded += SearchWidgetContent_Unloaded;

        UpdateContent();
        AttachExternalSubscriptions();
    }

    public event EventHandler? SearchRequested;

    private void SearchWidgetContent_Loaded(object sender, RoutedEventArgs e)
    {
        AttachExternalSubscriptions();
        UpdateContent();
    }

    private void SearchWidgetContent_Unloaded(object sender, RoutedEventArgs e)
    {
        DetachExternalSubscriptions();
    }

    private void AttachExternalSubscriptions()
    {
        if (_isDisposed || _externalSubscriptionsAttached)
        {
            return;
        }

        _localizationService.LanguageChanged += OnLanguageChanged;
        _externalSubscriptionsAttached = true;
        SynchronizeHistoryServiceSubscription();
    }

    private void DetachExternalSubscriptions()
    {
        if (!_externalSubscriptionsAttached)
        {
            return;
        }

        _localizationService.LanguageChanged -= OnLanguageChanged;
        if (_subscribedHistoryService is not null)
        {
            _subscribedHistoryService.RecentQueriesChanged -= OnHistoryChanged;
            _subscribedHistoryService = null;
        }

        _externalSubscriptionsAttached = false;
    }

    private SearchHistoryService? SynchronizeHistoryServiceSubscription()
    {
        SearchHistoryService? currentService = App.Current.SearchHistoryService;
        if (currentService is null ||
            ReferenceEquals(currentService, _subscribedHistoryService))
        {
            return currentService ?? _subscribedHistoryService;
        }

        if (_subscribedHistoryService is not null && _externalSubscriptionsAttached)
        {
            _subscribedHistoryService.RecentQueriesChanged -= OnHistoryChanged;
        }

        _subscribedHistoryService = currentService;
        if (_externalSubscriptionsAttached)
        {
            _subscribedHistoryService.RecentQueriesChanged += OnHistoryChanged;
        }

        return _subscribedHistoryService;
    }

    private void OnLanguageChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateContent();
        }
        else
        {
            DispatcherQueue.TryEnqueue(UpdateContent);
        }
    }

    public void UpdateContent()
    {
        if (_isDisposed)
        {
            return;
        }

        PlaceholderText.Text = _localizationService.T("Search.Placeholder");
        HistorySectionTitle.Text = _localizationService.T("Search.Section.RecentSearches");
        ClearHistoryLabel.Text = _localizationService.T("Widget.Search.Clear");
        EmptyStateHintText.Text = _localizationService.T("Widget.Search.EmptyHint");
        ToolTipService.SetToolTip(
            ClearHistoryButton,
            _localizationService.T("Search.Section.ClearHistory"));

        UpdateSearchIcon();
        UpdateHotkeyBadge();
        UpdateHistoryList();
        UpdateResponsiveLayout();
    }

    internal void BeginResponsiveLayoutTransition(
        double targetContentWidth,
        double targetContentHeight,
        bool isCollapsing)
    {
        _isResponsiveLayoutTransitionActive = true;
        _responsiveTargetWidth = Math.Max(0, targetContentWidth);
        _responsiveTargetHeight = Math.Max(0, targetContentHeight);

        // On expand the live body is revealed during the transition, so select
        // its final responsive state once before intermediate HWND sizes arrive.
        if (!isCollapsing)
        {
            ApplyResponsiveLayout(_responsiveTargetWidth, _responsiveTargetHeight);
        }
    }

    internal void CompleteResponsiveLayoutTransition(
        double finalContentWidth,
        double finalContentHeight)
    {
        _isResponsiveLayoutTransitionActive = false;
        _responsiveTargetWidth = 0;
        _responsiveTargetHeight = 0;
        ApplyResponsiveLayout(finalContentWidth, finalContentHeight);
    }

    internal void CancelResponsiveLayoutTransition()
    {
        _isResponsiveLayoutTransitionActive = false;
        _responsiveTargetWidth = 0;
        _responsiveTargetHeight = 0;
        UpdateResponsiveLayout();
    }

    private void UpdateHistoryList()
    {
        SearchHistoryService? historyService = SynchronizeHistoryServiceSubscription();
        if (historyService is null)
        {
            _recentQueries.Clear();
            ApplyHistoryVisibility(hasHistory: false);
            return;
        }

        string deleteLabel = _localizationService.T("Common.Delete");
        _recentQueries.Clear();
        foreach (string query in historyService.RecentQueries)
        {
            _recentQueries.Add(new SearchHistoryEntry
            {
                Query = query,
                DeleteLabel = deleteLabel
            });
        }

        ApplyHistoryVisibility(_recentQueries.Count > 0);
    }

    private void ApplyHistoryVisibility(bool hasHistory)
    {
        HistoryList.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        ClearHistoryButton.Visibility = hasHistory ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateHint.Visibility = hasHistory ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnHistoryChanged()
    {
        if (_isDisposed)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            UpdateHistoryList();
            return;
        }

        if (_historyRefreshQueued)
        {
            return;
        }

        _historyRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                _historyRefreshQueued = false;
                if (!_isDisposed)
                {
                    UpdateHistoryList();
                }
            }))
        {
            _historyRefreshQueued = false;
        }
    }

    public void ApplyAppearance()
    {
        if (_isDisposed)
        {
            return;
        }

        UpdateSearchIcon();
        UpdateHotkeyBadge();
    }

    private void UpdateSearchIcon()
    {
        SearchIcon.Mode = _settingsService?.Settings.WidgetTitleIconMode
                          ?? WidgetTitleIconModeNames.Color;
    }

    private void UpdateHotkeyBadge()
    {
        if (_settingsService is null || !_settingsService.Settings.SearchHotkeyEnabled)
        {
            HotkeyBadge.Text = string.Empty;
            _hasHotkeyBadge = false;
            UpdateResponsiveLayout();
            return;
        }

        var modifiers = (HotkeyModifierKeys)_settingsService.Settings.SearchHotkeyModifiers;
        var parts = new List<string>();
        if (modifiers.HasFlag(HotkeyModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        string keyName = _settingsService.Settings.SearchHotkeyKey switch
        {
            0x20 => "Space",
            >= 0x41 and <= 0x5A => ((char)_settingsService.Settings.SearchHotkeyKey).ToString(),
            >= 0x30 and <= 0x39 => ((char)_settingsService.Settings.SearchHotkeyKey).ToString(),
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(keyName))
        {
            parts.Add(keyName);
        }

        HotkeyBadge.Text = string.Join("+", parts);
        _hasHotkeyBadge = !string.IsNullOrWhiteSpace(HotkeyBadge.Text);
        UpdateResponsiveLayout();
    }

    private void SearchBar_Click(object sender, RoutedEventArgs e)
    {
        SearchRequested?.Invoke(this, EventArgs.Empty);
        App.Current.OpenSearchPopup();
    }

    private void HistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string query } && !string.IsNullOrWhiteSpace(query))
        {
            App.Current.OpenSearchPopupWithQuery(query.Trim());
        }
    }

    private void DeleteHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string query } && !string.IsNullOrWhiteSpace(query))
        {
            SearchHistoryService? historyService = SynchronizeHistoryServiceSubscription();
            if (historyService?.RemoveRecentQuery(query) == true)
            {
                UpdateHistoryList();
            }
        }
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        SearchHistoryService? historyService = SynchronizeHistoryServiceSubscription();
        historyService?.ClearRecentHistory();
        UpdateHistoryList();
    }

    private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateResponsiveLayout();
    }

    private void UpdateResponsiveLayout()
    {
        if (RootGrid is null)
        {
            return;
        }

        if (_isResponsiveLayoutTransitionActive)
        {
            return;
        }

        ApplyResponsiveLayout(RootGrid.ActualWidth, RootGrid.ActualHeight);
    }

    private void ApplyResponsiveLayout(double width, double height)
    {
        Visibility historyVisibility = width >= 180 && height >= 112
            ? Visibility.Visible
            : Visibility.Collapsed;
        Visibility hotkeyVisibility = _hasHotkeyBadge && width >= 220
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (HistoryArea.Visibility != historyVisibility)
        {
            HistoryArea.Visibility = historyVisibility;
        }

        if (HotkeyBadge.Visibility != hotkeyVisibility)
        {
            HotkeyBadge.Visibility = hotkeyVisibility;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        DetachExternalSubscriptions();
        Loaded -= SearchWidgetContent_Loaded;
        Unloaded -= SearchWidgetContent_Unloaded;
        HistoryList.ItemsSource = null;
        _recentQueries.Clear();
        SearchRequested = null;
        // SearchWidgetContent.xaml only uses {x:Bind} inside DataTemplates, so
        // the generated root Bindings field stays null unless root {x:Bind} is added.
        Bindings?.StopTracking();
        DataContext = null;
        Content = null;
    }
}
