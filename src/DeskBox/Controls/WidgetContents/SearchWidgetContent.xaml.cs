using System.Collections.ObjectModel;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// Lightweight search widget content that acts as a desktop entry point
/// for the search popup. Shows a search bar and a set of recommendation cards.
/// </summary>
public sealed partial class SearchWidgetContent : UserControl
{
    private readonly LocalizationService _localizationService;
    private readonly SettingsService? _settingsService;
    private readonly ObservableCollection<SearchRecommendationItem> _widgetRecommendations = [];

    public SearchWidgetContent(
        LocalizationService localizationService,
        SettingsService? settingsService = null)
    {
        _localizationService = localizationService;
        _settingsService = settingsService;

        InitializeComponent();
        RecommendationsList.ItemsSource = _widgetRecommendations;
        UpdateContent();
    }

    /// <summary>
    /// Raised when the user clicks the widget to open the search popup.
    /// </summary>
    public event EventHandler? SearchRequested;

    public void UpdateContent()
    {
        PlaceholderText.Text = _localizationService.T("Search.Placeholder");
        UpdateSearchIcon();
        UpdateHotkeyBadge();
        _ = LoadRecommendationsAsync();
    }

    /// <summary>
    /// Loads a compact set of recommendation cards into the widget body.
    /// </summary>
    public async Task LoadRecommendationsAsync()
    {
        try
        {
            var engine = App.Current.SearchEngineService;
            if (engine is null)
            {
                RecommendationsList.Visibility = Visibility.Collapsed;
                return;
            }

            var recommendations = await engine.GetRecommendationsAsync();

            // Actual usage comes first; generated suggestions only fill the remaining
            // compact preview slots.
            var recent = App.Current.SearchHistoryService?.RecentResults
                ?? Array.Empty<SearchRecommendationItem>();
            var preview = recent
                .Concat(recommendations)
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

    private void RootGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        SearchRequested?.Invoke(this, EventArgs.Empty);
        App.Current.OpenSearchPopup();
        e.Handled = true;
    }
}
