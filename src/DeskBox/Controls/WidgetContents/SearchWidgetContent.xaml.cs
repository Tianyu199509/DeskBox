using System.Collections.ObjectModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Lightweight search widget content that acts as a desktop entry point
/// for the search popup. Shows a search bar, recent search history, and
/// a set of recommendation cards.
/// </summary>
public sealed partial class SearchWidgetContent : UserControl
{
    private readonly LocalizationService _localizationService;
    private readonly SettingsService? _settingsService;
    private readonly ObservableCollection<SearchRecommendationItem> _widgetRecommendations = [];
    private readonly ObservableCollection<string> _recentQueries = [];

    public SearchWidgetContent(
        LocalizationService localizationService,
        SettingsService? settingsService = null)
    {
        _localizationService = localizationService;
        _settingsService = settingsService;

        InitializeComponent();
        RecommendationsList.ItemsSource = _widgetRecommendations;
        HistoryList.ItemsSource = _recentQueries;

        // The empty-state hint defaults to Visible in XAML so the user sees a
        // prompt the moment the widget mounts, even before any service is ready.
        // The Loaded event below is a second safety net for the case where
        // services weren't available during construction (so UpdateHistoryList
        // / LoadRecommendationsAsync short-circuited without ever calling
        // UpdateEmptyStateHint).
        EmptyStateHintText.Text = _localizationService.T("Widget.Search.EmptyHint");
        Loaded += (_, _) =>
        {
            UpdateContent();
            UpdateEmptyStateHint();
        };

        UpdateContent();

        _localizationService.LanguageChanged += OnLanguageChanged;

        // Live-sync with search history: when the popup records a new query (or clears
        // history), refresh this widget so the user sees it without re-opening.
        var historyService = App.Current.SearchHistoryService;
        if (historyService is not null)
        {
            historyService.RecentQueriesChanged += OnHistoryChanged;
        }

        Unloaded += (_, _) =>
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
            if (historyService is not null)
            {
                historyService.RecentQueriesChanged -= OnHistoryChanged;
            }
        };
    }

    private void OnLanguageChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
            UpdateContent();
        else
            DispatcherQueue.TryEnqueue(UpdateContent);
    }

    /// <summary>
    /// Raised when the user clicks the widget to open the search popup.
    /// </summary>
    public event EventHandler? SearchRequested;

    public void UpdateContent()
    {
        PlaceholderText.Text = _localizationService.T("Search.Placeholder");
        ClearHistoryLabel.Text = _localizationService.T("Widget.Search.Clear");
        UpdateSearchIcon();
        UpdateHotkeyBadge();
        UpdateHistoryList();
        _ = LoadRecommendationsAsync();
    }

    /// <summary>
    /// Loads recent search queries into the history list.
    /// </summary>
    private void UpdateHistoryList()
    {
        var historyService = App.Current.SearchHistoryService;
        if (historyService is null)
        {
            HistoryList.Visibility = Visibility.Collapsed;
            return;
        }

        var queries = historyService.RecentQueries.Take(6).ToList();
        _recentQueries.Clear();
        foreach (var query in queries)
        {
            _recentQueries.Add(query);
        }

        HistoryList.Visibility = queries.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        UpdateClearButtonVisibility();
        UpdateEmptyStateHint();
    }

    /// <summary>
    /// Loads a compact set of recently-opened result cards into the widget body.
    /// </summary>
    public async Task LoadRecommendationsAsync()
    {
        try
        {
            // The widget body shows ONLY items the user actually opened (recent
            // results). Auto-generated Start Menu app shortcuts (.lnk) are NOT shown
            // here: they reappear on every refresh and the user perceived them as
            // un-clearable "garbage". App recommendations remain available in the
            // search popup, which is the right place for discovery.
            var recent = App.Current.SearchHistoryService?.RecentResults
                ?? Array.Empty<SearchRecommendationItem>();
            var preview = recent
                .Where(r => r.Kind != SearchResultKind.Action)
                .GroupBy(r => $"{r.Kind}:{r.DetailPath}:{r.TodoItemId}:{r.QuickCaptureItemId}:{r.Title}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Take(3)
                .ToList();

            _widgetRecommendations.Clear();
            foreach (var item in preview)
            {
                _widgetRecommendations.Add(item);
            }

            RecommendationsList.Visibility = preview.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        catch
        {
            RecommendationsList.Visibility = Visibility.Collapsed;
        }
        UpdateClearButtonVisibility();
        UpdateEmptyStateHint();
    }

    private void UpdateEmptyStateHint()
    {
        // Hide the empty-state prompt whenever there is ANY content to show — either
        // recent search history or result cards. Showing the "type a keyword" hint
        // alongside real entries is confusing.
        bool hasContent = HistoryList.Visibility == Visibility.Visible ||
                          RecommendationsList.Visibility == Visibility.Visible;
        EmptyStateHint.Visibility = hasContent ? Visibility.Collapsed : Visibility.Visible;
        EmptyStateHintText.Text = _localizationService.T("Widget.Search.EmptyHint");
    }

    /// <summary>
    /// Shows the clear-history footer button whenever there is anything to clear:
    /// recent query history OR recommendation/result cards. Hidden only when the
    /// widget body is completely empty, so it never sits alone under the search bar.
    /// </summary>
    private void UpdateClearButtonVisibility()
    {
        ClearHistoryButton.Visibility =
            (HistoryList.Visibility == Visibility.Visible ||
             RecommendationsList.Visibility == Visibility.Visible)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    /// <summary>
    /// Called when SearchHistoryService reports a change (query recorded or history
    /// cleared) — typically because the user searched in the popup. Marshals back to
    /// the UI thread and refreshes history + recommendations so the widget stays live.
    /// </summary>
    private void OnHistoryChanged()
    {
        if (DispatcherQueue.HasThreadAccess)
            UpdateContent();
        else
            DispatcherQueue.TryEnqueue(UpdateContent);
    }

    public void ApplyAppearance()
    {
        UpdateSearchIcon();
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
    }

    // ── Search bar interactions ──

    private void SearchBar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SearchBarButton.Background = ResolveThemeBrush("ControlFillColorSecondaryBrush");
        SearchBarButton.BorderBrush = ResolveThemeBrush("ControlStrokeColorSecondaryBrush");
    }

    private void SearchBar_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        SearchBarButton.Background = ResolveThemeBrush("ControlFillColorDefaultBrush");
        SearchBarButton.BorderBrush = ResolveThemeBrush("ControlStrokeColorDefaultBrush");
    }

    private void SearchBar_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        SearchRequested?.Invoke(this, EventArgs.Empty);
        App.Current.OpenSearchPopup();
        e.Handled = true;
    }

    // ── History item interactions ──

    private void HistoryItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            grid.Background = ResolveThemeBrush("SubtleFillColorSecondaryBrush");
        }
    }

    private void HistoryItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            grid.Background = null;
        }
    }

    private void HistoryItem_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string query } && !string.IsNullOrWhiteSpace(query))
        {
            App.Current.OpenSearchPopupWithQuery(query);
            e.Handled = true;
        }
    }

    private void ClearHistoryButton_Click(object sender, RoutedEventArgs e)
    {
        // Clear both recent queries and the cached recent-result cards shown in the
        // widget body (the "garbage" entries that aren't the user's search text).
        // Favorites are preserved — they are explicitly pinned, not auto-recorded.
        App.Current.SearchHistoryService?.ClearHistoryAndResults();
        // The RecentQueriesChanged subscription refreshes the widget, but call
        // UpdateContent directly too so the UI feels instant even if the event races.
        UpdateContent();
    }

    // ── Recommendation item interactions ──

    private void RecItem_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            grid.Background = ResolveThemeBrush("SubtleFillColorSecondaryBrush");
        }
    }

    private void RecItem_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid)
        {
            grid.Background = null;
        }
    }

    private static Brush? ResolveThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out object? value)
            ? value as Brush
            : null;
}
