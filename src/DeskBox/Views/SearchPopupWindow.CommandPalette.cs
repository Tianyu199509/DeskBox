using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace DeskBox.Views;

public sealed partial class SearchPopupWindow
{
    private SearchInlineScope _latchedInlineScope = SearchInlineScope.None;
    private bool _suppressScopeLatchTextChange;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _detailsUpdateCoalesceTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _detailsPreviewDebounceTimer;
    private CancellationTokenSource? _detailsPreviewCancellation;
    private long _lastDetailsPopulateTickMs;
    private long _lastAsyncPreviewStartTickMs;
    private int _detailsImageLoadGeneration;
    private SearchResultItem? _displayedDetailsItem;
    private SearchResultItem? _loadedAsyncPreviewItem;
    private bool _hasShownDetailsInQuerySession;
    private bool _hasExpandedResultsInQuerySession;
    private bool _isCmdBarFlyoutOpen;
    private readonly List<CommandPaletteActionEntry> _allCmdBarActions = [];
    private readonly List<CommandPaletteActionEntry> _filteredCmdBarActions = [];

    private sealed record CommandPaletteActionEntry(
        string Id,
        string Title,
        string Glyph,
        string? ShortcutDisplay,
        string Keywords,
        Func<Task<bool>> ExecuteAsync,
        ImageSource? CustomIcon = null);

    private bool HasActiveSearchOrScope =>
        !string.IsNullOrWhiteSpace(SearchTextBox.Text) ||
        _latchedInlineScope != SearchInlineScope.None;

    private string GetEffectivePopupQuery()
    {
        string raw = SearchTextBox.Text ?? string.Empty;
        if (_latchedInlineScope == SearchInlineScope.None)
        {
            return raw;
        }

        string prefix = GetCanonicalScopePrefix(_latchedInlineScope);
        return string.IsNullOrWhiteSpace(raw) ? prefix : $"{prefix}{raw}";
    }

    private static string GetCanonicalScopePrefix(SearchInlineScope scope) => scope switch
    {
        SearchInlineScope.Apps => "app:",
        SearchInlineScope.Files => "file:",
        SearchInlineScope.Clipboard => "clip:",
        SearchInlineScope.Todos => "todo:",
        SearchInlineScope.Notes => "note:",
        SearchInlineScope.Settings => "set:",
        SearchInlineScope.Commands => ">",
        _ => string.Empty
    };

    private void ResetLatchedScopeState()
    {
        _latchedInlineScope = SearchInlineScope.None;
        SyncScopeHeaderVisuals(SearchTextBox?.Text);
    }

    private bool _searchScopeCompositionEndHooked;

    private bool TryLatchInlineScopeFromInput(string? rawInput)
    {
        if (!_searchScopeCompositionEndHooked && SearchTextBox is not null)
        {
            _searchScopeCompositionEndHooked = true;
            SearchTextBox.TextCompositionEnded += (_, _) =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (SearchTextBox is not null && !DeskBox.Helpers.ImeCompositionGuard.IsActivelyComposing(SearchTextBox))
                    {
                        _ = TryLatchInlineScopeFromInput(SearchTextBox.Text);
                    }
                });
            };
        }

        if (_suppressScopeLatchTextChange ||
            _latchedInlineScope != SearchInlineScope.None ||
            string.IsNullOrEmpty(rawInput) ||
            SearchTextBox is null ||
            DeskBox.Helpers.ImeCompositionGuard.IsActivelyComposing(SearchTextBox))
        {
            return false;
        }

        ParsedSearchQuery parsed = SearchQuerySyntaxParser.Parse(rawInput);
        if (!parsed.HasScopeFilter || string.IsNullOrEmpty(parsed.ScopeBadgeText))
        {
            return false;
        }

        // Single-char prefixes ("f:", "t:", "n:", ">") require a trailing space so typing
        // Windows drive paths like "F:\Projects" never misfires on the colon keystroke.
        bool isMultiCharPrefix = parsed.ScopeBadgeText.Length >= 3;
        bool hasSpaceAfterPrefix = rawInput.TrimStart().Length > parsed.ScopeBadgeText.Length &&
                                   char.IsWhiteSpace(rawInput.TrimStart()[parsed.ScopeBadgeText.Length]);
        if (!isMultiCharPrefix && !hasSpaceAfterPrefix)
        {
            return false;
        }

        _latchedInlineScope = parsed.Scope;
        _suppressScopeLatchTextChange = true;
        try
        {
            string remainder = parsed.EffectiveQuery ?? string.Empty;
            SearchTextBox.Text = remainder;
            SearchTextBox.SelectionStart = remainder.Length;
        }
        finally
        {
            _suppressScopeLatchTextChange = false;
        }

        SyncScopeHeaderVisuals(SearchTextBox.Text);
        return true;
    }

    private void ExitLatchedScope()
    {
        if (_latchedInlineScope == SearchInlineScope.None)
        {
            return;
        }

        _latchedInlineScope = SearchInlineScope.None;
        SyncScopeHeaderVisuals(SearchTextBox.Text);
        _searchDebounceTimer?.Stop();
        _viewModel.Query = GetEffectivePopupQuery();
        UpdatePanelVisibility();
        SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void ScopeBackButton_Click(object sender, RoutedEventArgs e)
    {
        ExitLatchedScope();
    }

    private void SyncScopeHeaderVisuals(string? currentBoxText)
    {
        if (SearchTextBox is null)
        {
            return;
        }

        SearchInlineScope activeScope = _latchedInlineScope;
        if (activeScope != SearchInlineScope.None)
        {
            DefaultSearchIconGrid.Visibility = Visibility.Collapsed;
            ScopeHeaderCluster.Visibility = Visibility.Visible;
            ScopePrefixBadge.Visibility = Visibility.Collapsed;
            ImageSource? scopeSvg = activeScope switch
            {
                SearchInlineScope.Todos => Controls.SearchResultRowControl.GetTodoSvgIcon(),
                SearchInlineScope.Notes => Controls.SearchResultRowControl.GetQuickCaptureSvgIcon(),
                _ => null
            };
            if (scopeSvg is not null)
            {
                ScopeHeaderImage.Source = scopeSvg;
                ScopeHeaderImage.Visibility = Visibility.Visible;
                ScopeHeaderIcon.Visibility = Visibility.Collapsed;
            }
            else
            {
                ScopeHeaderImage.Source = null;
                ScopeHeaderImage.Visibility = Visibility.Collapsed;
                ScopeHeaderIcon.Visibility = Visibility.Visible;
                ScopeHeaderIcon.Glyph = GetScopeGlyph(activeScope);
            }

            SearchTextBox.PlaceholderText = GetScopePlaceholderText(activeScope);
            return;
        }

        DefaultSearchIconGrid.Visibility = Visibility.Visible;
        ScopeHeaderCluster.Visibility = Visibility.Collapsed;
        SearchTextBox.PlaceholderText = _localizationService.T("Search.Placeholder");

        ParsedSearchQuery parsed = !string.IsNullOrWhiteSpace(currentBoxText)
            ? SearchQuerySyntaxParser.Parse(currentBoxText)
            : default;
        string badge = !string.IsNullOrWhiteSpace(currentBoxText)
            ? (parsed.ScopeBadgeText ?? string.Empty)
            : _viewModel.ActiveScopeBadgeText;

        ImageSource? badgeSvg = null;
        if (parsed.IsQuickTodoIntent || parsed.Scope == SearchInlineScope.Todos)
        {
            badgeSvg = Controls.SearchResultRowControl.GetTodoSvgIcon();
            if (parsed.IsQuickTodoIntent)
            {
                badge = _localizationService.T("Search.QuickCreate.BadgeTodo");
            }
        }
        else if (parsed.IsQuickNoteIntent || parsed.Scope == SearchInlineScope.Notes)
        {
            badgeSvg = Controls.SearchResultRowControl.GetQuickCaptureSvgIcon();
            if (parsed.IsQuickNoteIntent)
            {
                badge = _localizationService.T("Search.QuickCreate.BadgeNote");
            }
        }

        if (badgeSvg is not null)
        {
            ScopePrefixBadgeIcon.Source = badgeSvg;
            ScopePrefixBadgeIcon.Visibility = Visibility.Visible;
            ScopePrefixBadge.Background = NeutralInteractionBrush.ResolveThemedResource(
                "ControlFillColorSecondaryBrush",
                Content);
            ScopePrefixBadge.BorderBrush = NeutralInteractionBrush.ResolveThemedResource(
                "CardStrokeColorDefaultBrush",
                Content);
            ScopePrefixBadgeText.Foreground = NeutralInteractionBrush.ResolveThemedResource(
                "TextFillColorPrimaryBrush",
                Content);
        }
        else
        {
            ScopePrefixBadgeIcon.Source = null;
            ScopePrefixBadgeIcon.Visibility = Visibility.Collapsed;
            ScopePrefixBadge.Background = NeutralInteractionBrush.ResolveThemedResource(
                "AccentFillColorDefaultBrush",
                Content);
            ScopePrefixBadge.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            ScopePrefixBadgeText.Foreground = NeutralInteractionBrush.ResolveThemedResource(
                "TextOnAccentFillColorPrimaryBrush",
                Content);
        }

        ScopePrefixBadgeText.Text = badge;
        ScopePrefixBadge.Visibility = string.IsNullOrWhiteSpace(badge)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private static string GetScopeGlyph(SearchInlineScope scope) => scope switch
    {
        SearchInlineScope.Apps => "\uE71D",
        SearchInlineScope.Files => "\uE8B7",
        SearchInlineScope.Clipboard => "\uE77F",
        SearchInlineScope.Todos => "\uE9D5",
        SearchInlineScope.Notes => "\uE70F",
        SearchInlineScope.Settings => "\uE713",
        SearchInlineScope.Commands => "\uE756",
        _ => "\uE721"
    };

    private string GetScopePlaceholderText(SearchInlineScope scope) => scope switch
    {
        SearchInlineScope.Apps => _localizationService.T("Search.Scope.Placeholder.Apps"),
        SearchInlineScope.Files => _localizationService.T("Search.Scope.Placeholder.Files"),
        SearchInlineScope.Clipboard => _localizationService.T("Search.Scope.Placeholder.Clipboard"),
        SearchInlineScope.Todos => _localizationService.T("Search.Scope.Placeholder.Todos"),
        SearchInlineScope.Notes => _localizationService.T("Search.Scope.Placeholder.Notes"),
        SearchInlineScope.Settings => _localizationService.T("Search.Scope.Placeholder.Settings"),
        SearchInlineScope.Commands => _localizationService.T("Search.Scope.Placeholder.Commands"),
        _ => _localizationService.T("Search.Placeholder")
    };

    private string GetScopeContextDisplayName(SearchInlineScope scope) => scope switch
    {
        SearchInlineScope.Apps => TrimEllipsis(_localizationService.T("Search.Scope.Placeholder.Apps")),
        SearchInlineScope.Files => TrimEllipsis(_localizationService.T("Search.Scope.Placeholder.Files")),
        SearchInlineScope.Clipboard => TrimEllipsis(_localizationService.T("Search.Scope.Placeholder.Clipboard")),
        SearchInlineScope.Todos => TrimEllipsis(_localizationService.T("Search.Scope.Placeholder.Todos")),
        SearchInlineScope.Notes => TrimEllipsis(_localizationService.T("Search.Scope.Placeholder.Notes")),
        SearchInlineScope.Settings => TrimEllipsis(_localizationService.T("Search.Scope.Placeholder.Settings")),
        SearchInlineScope.Commands => TrimEllipsis(_localizationService.T("Search.Scope.Placeholder.Commands")),
        _ => _localizationService.T("Search.Scope.GlobalTitle")
    };

    private static string TrimEllipsis(string text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.TrimEnd('.', '…', ' ');

    private void ApplyLocalizedCommandPaletteStrings()
    {
        DetailsTypeLabel.Text = _localizationService.T("Search.Details.Type");
        CmdBarMoreLabel.Text = _localizationService.T("Search.CmdBar.More");
        CmdBarActionsFilterBox.PlaceholderText = _localizationService.T("Search.Actions.FilterPlaceholder");
        CmdBarActionsEmptyText.Text = _localizationService.T("Search.Actions.NoMatch");
        ToolTipService.SetToolTip(ScopeBackButton, _localizationService.T("Search.Scope.GlobalTitle"));
        SyncDetailsToggleTooltip();
        SyncScopeHeaderVisuals(SearchTextBox?.Text);
        if (DetailsPaneCard?.Visibility == Visibility.Visible)
        {
            PopulateDetailsPane(_viewModel.SelectedItem, immediate: true);
        }
        UpdateCommandBarForSelectedItem();
    }

    private void SyncDetailsToggleTooltip()
    {
        bool detailsEnabled = _settingsService.Settings.Search.SearchShowDetailsPane;
        string tip = _localizationService.T(
            detailsEnabled ? "Search.Details.ToggleHide" : "Search.Details.ToggleShow");
        ToolTipService.SetToolTip(ToggleDetailsPaneButton, tip);
        ToggleDetailsPaneIcon.Opacity = detailsEnabled ? 1.0 : 0.62;
    }

    private void ResetSearchSessionVisualFlags()
    {
        _hasShownDetailsInQuerySession = false;
        _hasExpandedResultsInQuerySession = false;
        CancelPendingDetailsFilePreview();
        _loadedAsyncPreviewItem = null;
    }

    private void CancelPendingDetailsFilePreview()
    {
        _detailsPreviewDebounceTimer?.Stop();
        ++_detailsImageLoadGeneration;
        if (_detailsPreviewCancellation is not null)
        {
            try
            {
                _detailsPreviewCancellation.Cancel();
            }
            catch
            {
            }
            _detailsPreviewCancellation.Dispose();
            _detailsPreviewCancellation = null;
        }
    }

    private void ToggleDetailsPanePreference()
    {
        bool next = !_settingsService.Settings.Search.SearchShowDetailsPane;
        _settingsService.Settings.Search.SearchShowDetailsPane = next;
        _settingsService.SaveDebounced();
        if (next)
        {
            _hasShownDetailsInQuerySession = false;
        }
        SyncDetailsToggleTooltip();
        UpdatePanelVisibility();
    }

    private void ToggleDetailsPaneButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleDetailsPanePreference();
        SearchTextBox.Focus(FocusState.Programmatic);
    }

    private void UpdateDetailsSplitLayout(bool showResults)
    {
        ResultsSplitGrid.Visibility = showResults ? Visibility.Visible : Visibility.Collapsed;
        bool showDetails = showResults &&
                           _settingsService.Settings.Search.SearchShowDetailsPane &&
                           _viewModel.SelectedItem is not null;

        bool wasCollapsed = DetailsPaneCard.Visibility != Visibility.Visible;
        if (showDetails)
        {
            ResultsListColumn.Width = new GridLength(3, GridUnitType.Star);
            DetailsPaneColumn.Width = new GridLength(2, GridUnitType.Star);
            DetailsPaneCard.Visibility = Visibility.Visible;
            PopulateDetailsPane(_viewModel.SelectedItem, immediate: true);
            if (wasCollapsed &&
                !_hasShownDetailsInQuerySession &&
                AreSystemAnimationsEnabled() &&
                !_viewModel.IsApplyingBackgroundResultRefresh)
            {
                _hasShownDetailsInQuerySession = true;
                AnimateDetailsPaneEntrance();
            }
            else
            {
                _hasShownDetailsInQuerySession = true;
                DetailsPaneCard.Opacity = 1.0;
                DetailsTranslateTransform.X = 0;
            }
        }
        else
        {
            _detailsUpdateCoalesceTimer?.Stop();
            CancelPendingDetailsFilePreview();
            _displayedDetailsItem = null;
            _loadedAsyncPreviewItem = null;
            DetailsPaneCard.Visibility = Visibility.Collapsed;
            ResultsListColumn.Width = new GridLength(1, GridUnitType.Star);
            DetailsPaneColumn.Width = new GridLength(0);
        }

        SyncDetailsToggleTooltip();
    }

    private void AnimateDetailsPaneEntrance()
    {
        DetailsPaneCard.Opacity = 0;
        DetailsTranslateTransform.X = 8;

        var fade = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(140)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, DetailsPaneCard);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var slide = new DoubleAnimation
        {
            From = 8,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, DetailsTranslateTransform);
        Storyboard.SetTargetProperty(slide, "X");

        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Children.Add(slide);
        sb.Begin();
    }

    private void ScheduleSelectedItemDetailsRefresh()
    {
        UpdateCommandBarForSelectedItem();

        if (DetailsPaneCard.Visibility != Visibility.Visible)
        {
            if (ResultsPanel.Visibility == Visibility.Visible &&
                _settingsService.Settings.Search.SearchShowDetailsPane &&
                _viewModel.SelectedItem is not null)
            {
                UpdateDetailsSplitLayout(showResults: true);
                UpdatePopupDynamicBounds();
            }

            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastDetailsPopulateTickMs >= 55)
        {
            PopulateDetailsPane(_viewModel.SelectedItem, immediate: true);
            return;
        }

        if (_detailsUpdateCoalesceTimer is null)
        {
            _detailsUpdateCoalesceTimer = DispatcherQueue.CreateTimer();
            _detailsUpdateCoalesceTimer.Interval = TimeSpan.FromMilliseconds(55);
            _detailsUpdateCoalesceTimer.IsRepeating = false;
            _detailsUpdateCoalesceTimer.Tick += (_, _) =>
            {
                if (!_isWindowClosed && DetailsPaneCard.Visibility == Visibility.Visible)
                {
                    PopulateDetailsPane(_viewModel.SelectedItem, immediate: true);
                }
            };
        }

        _detailsUpdateCoalesceTimer.Stop();
        _detailsUpdateCoalesceTimer.Start();
    }

    private void PopulateDetailsPane(SearchResultItem? item, bool immediate = false)
    {
        _ = immediate;
        if (item is null)
        {
            return;
        }

        _lastDetailsPopulateTickMs = Environment.TickCount64;
        _displayedDetailsItem = item;

        // 1. Hero Icon
        ImageSource? heroIcon = item.Icon ?? Controls.SearchResultRowControl.ResolveNativeWidgetSvgIcon(item);
        if (heroIcon is not null)
        {
            DetailsHeroImage.Source = heroIcon;
            DetailsHeroImagePlate.Visibility = Visibility.Visible;
            DetailsHeroGlyphPlate.Visibility = Visibility.Collapsed;
        }
        else
        {
            DetailsHeroImage.Source = null;
            DetailsHeroImagePlate.Visibility = Visibility.Collapsed;
            DetailsHeroGlyphPlate.Visibility = Visibility.Visible;
            DetailsHeroGlyph.Glyph = item.DisplayGlyph;
        }

        // Kick off lazy shell metadata enrichment if needed for file/folder items.
        if (item.Kind is SearchResultKind.File or SearchResultKind.Folder && !item.IconResolved)
        {
            _ = EnsureSelectedDetailsMetadataAsync(item);
        }

        // 2. Clean Hero Title
        DetailsTitleText.Text = ResolveCleanDetailsTitle(item);

        // 3. Synchronous Body Preview (Notes, Todos, Clipboard text/code/files, Calculator, Settings)
        bool hasSyncBodyPreview = false;
        if (!string.IsNullOrWhiteSpace(item.PreviewText))
        {
            DetailsBodyPreviewText.Text = item.PreviewText;
            DetailsBodyPreviewBorder.Visibility = Visibility.Visible;
            hasSyncBodyPreview = true;
        }
        else if (item.Kind == SearchResultKind.Action &&
                 item.ActionId?.StartsWith("open-settings:", StringComparison.Ordinal) == true &&
                 !string.IsNullOrWhiteSpace(item.Subtitle))
        {
            DetailsBodyPreviewText.Text = item.Subtitle;
            DetailsBodyPreviewBorder.Visibility = Visibility.Visible;
            hasSyncBodyPreview = true;
        }

        // 4. Lightweight Static Preview (Images, Video Poster Frames, PDF Page-1, Word .docx & Text/Code Snippets)
        if (SearchDetailsPreviewLoader.CanAttemptAsyncFilePreview(item))
        {
            if (!ReferenceEquals(_loadedAsyncPreviewItem, item))
            {
                DetailsImagePreview.Source = null;
                DetailsImagePreviewBorder.Visibility = Visibility.Collapsed;
                DetailsVideoBadge.Visibility = Visibility.Collapsed;
                if (!hasSyncBodyPreview)
                {
                    DetailsBodyPreviewText.Text = string.Empty;
                    DetailsBodyPreviewBorder.Visibility = Visibility.Collapsed;
                }

                ScheduleDetailsFilePreviewLoad(item);
            }
        }
        else
        {
            CancelPendingDetailsFilePreview();
            _loadedAsyncPreviewItem = null;
            DetailsImagePreview.Source = null;
            DetailsImagePreviewBorder.Visibility = Visibility.Collapsed;
            DetailsVideoBadge.Visibility = Visibility.Collapsed;
            if (!hasSyncBodyPreview)
            {
                DetailsBodyPreviewText.Text = string.Empty;
                DetailsBodyPreviewBorder.Visibility = Visibility.Collapsed;
            }
        }

        // 5. Type Badge
        DetailsTypeLabel.Text = _localizationService.T("Search.Details.Type");
        DetailsTypeBadgeText.Text = ResolveDetailsTypeLabel(item);

        // 6. Path / Target URL
        if (!string.IsNullOrWhiteSpace(item.DetailPath))
        {
            bool isUrlTarget = item.Kind != SearchResultKind.File &&
                               item.Kind != SearchResultKind.Folder &&
                               SearchWebAndUrlEvaluator.TryNormalizeSafeExternalUri(item.DetailPath, out _);
            DetailsPathLabel.Text = _localizationService.T(
                isUrlTarget ? "Search.Details.Target" : "Search.Details.Path");
            DetailsPathValue.Text = item.DetailPath;
            DetailsPathSection.Visibility = Visibility.Visible;
        }
        else
        {
            DetailsPathSection.Visibility = Visibility.Collapsed;
        }

        // 7. Secondary Metadata (Size + Modified OR SourceApp + Stats)
        (string? m1Label, string? m1Val, string? m2Label, string? m2Val) = ResolveExtraMetadataPairs(item);
        bool showM1 = !string.IsNullOrWhiteSpace(m1Label) && !string.IsNullOrWhiteSpace(m1Val);
        bool showM2 = !string.IsNullOrWhiteSpace(m2Label) && !string.IsNullOrWhiteSpace(m2Val);

        if (showM1)
        {
            DetailsMeta1Label.Text = m1Label!;
            DetailsMeta1Value.Text = m1Val!;
            DetailsMeta1Section.Visibility = Visibility.Visible;
        }
        else
        {
            DetailsMeta1Section.Visibility = Visibility.Collapsed;
        }

        if (showM2)
        {
            DetailsMeta2Label.Text = m2Label!;
            DetailsMeta2Value.Text = m2Val!;
            DetailsMeta2Section.Visibility = Visibility.Visible;
        }
        else
        {
            DetailsMeta2Section.Visibility = Visibility.Collapsed;
        }

        DetailsExtraGrid.Visibility = (showM1 || showM2) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task EnsureSelectedDetailsMetadataAsync(SearchResultItem item)
    {
        await _viewModel.EnsureResultMetadataAsync(item);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_isWindowClosed && ReferenceEquals(_displayedDetailsItem, item))
            {
                PopulateDetailsPane(item, immediate: true);
            }
        });
    }

    private void ScheduleDetailsFilePreviewLoad(SearchResultItem item)
    {
        CancelPendingDetailsFilePreview();
        long now = Environment.TickCount64;
        if (now - _lastAsyncPreviewStartTickMs >= 90)
        {
            _lastAsyncPreviewStartTickMs = now;
            StartDetailsFilePreviewLoad(item);
            return;
        }

        if (_detailsPreviewDebounceTimer is null)
        {
            _detailsPreviewDebounceTimer = DispatcherQueue.CreateTimer();
            _detailsPreviewDebounceTimer.Interval = TimeSpan.FromMilliseconds(85);
            _detailsPreviewDebounceTimer.IsRepeating = false;
            _detailsPreviewDebounceTimer.Tick += (_, _) =>
            {
                if (!_isWindowClosed &&
                    DetailsPaneCard.Visibility == Visibility.Visible &&
                    _displayedDetailsItem is { } target)
                {
                    _lastAsyncPreviewStartTickMs = Environment.TickCount64;
                    StartDetailsFilePreviewLoad(target);
                }
            };
        }

        _detailsPreviewDebounceTimer.Stop();
        _detailsPreviewDebounceTimer.Start();
    }

    private void StartDetailsFilePreviewLoad(SearchResultItem item)
    {
        var cts = new CancellationTokenSource();
        _detailsPreviewCancellation = cts;
        int generation = ++_detailsImageLoadGeneration;
        _ = LoadDetailsRichPreviewAsync(item, generation, cts.Token);
    }

    private async Task LoadDetailsRichPreviewAsync(
        SearchResultItem item,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            SearchDetailsLoadedPreview loaded = await SearchDetailsPreviewLoader
                .LoadAsync(item, cancellationToken)
                .ConfigureAwait(true);

            if (_isWindowClosed ||
                cancellationToken.IsCancellationRequested ||
                generation != _detailsImageLoadGeneration ||
                !ReferenceEquals(_displayedDetailsItem, item))
            {
                return;
            }

            _loadedAsyncPreviewItem = item;
            if (loaded.Image is not null)
            {
                DetailsImagePreview.Source = loaded.Image;
                DetailsImagePreviewBorder.Visibility = Visibility.Visible;
                DetailsVideoBadge.Visibility = loaded.IsVideoPoster
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
            else
            {
                DetailsImagePreview.Source = null;
                DetailsImagePreviewBorder.Visibility = Visibility.Collapsed;
                DetailsVideoBadge.Visibility = Visibility.Collapsed;
            }

            if (!string.IsNullOrWhiteSpace(loaded.TextSnippet) &&
                string.IsNullOrWhiteSpace(item.PreviewText))
            {
                DetailsBodyPreviewText.Text = loaded.TextSnippet;
                DetailsBodyPreviewBorder.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            if (generation == _detailsImageLoadGeneration)
            {
                DetailsImagePreview.Source = null;
                DetailsImagePreviewBorder.Visibility = Visibility.Collapsed;
                DetailsVideoBadge.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static string ResolveCleanDetailsTitle(SearchResultItem item)
    {
        if (item.Kind == SearchResultKind.File &&
            (item.Title.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ||
             item.Title.EndsWith(".url", StringComparison.OrdinalIgnoreCase)))
        {
            string stripped = Path.GetFileNameWithoutExtension(item.Title);
            if (!string.IsNullOrWhiteSpace(stripped))
            {
                return stripped;
            }
        }

        return item.Title;
    }

    private string ResolveDetailsTypeLabel(SearchResultItem item)
    {
        if (Controls.SearchResultRowControl.IsTodoSearchResultOrAction(item))
        {
            return _localizationService.T("Search.Type.Todo");
        }

        if (Controls.SearchResultRowControl.IsQuickCaptureSearchResultOrAction(item))
        {
            return _localizationService.T("Search.Type.Note");
        }

        if (!string.IsNullOrWhiteSpace(item.TypeDisplay))
        {
            return item.TypeDisplay!;
        }

        return item.Kind switch
        {
            SearchResultKind.Folder => _localizationService.T("Search.Type.Folder"),
            SearchResultKind.File => FileCategoryHelper.Categorize(item.Title) == FileCategory.App
                ? _localizationService.T("Search.Type.App")
                : (!string.IsNullOrWhiteSpace(Path.GetExtension(item.Title))
                    ? Path.GetExtension(item.Title).TrimStart('.').ToUpperInvariant()
                    : _localizationService.T("Search.Type.File")),
            SearchResultKind.Clipboard => _localizationService.T("Search.Type.Clipboard"),
            SearchResultKind.Todo => _localizationService.T("Search.Type.Todo"),
            SearchResultKind.QuickCapture => _localizationService.T("Search.Type.Note"),
            SearchResultKind.Action when item.ActionId?.StartsWith(SearchWebAndUrlEvaluator.OpenUrlActionPrefix, StringComparison.Ordinal) == true
                => _localizationService.T("Search.Type.Url"),
            SearchResultKind.Action when item.ActionId?.StartsWith(SearchWebAndUrlEvaluator.WebSearchActionPrefix, StringComparison.Ordinal) == true
                => _localizationService.T("Search.Type.WebSearch"),
            SearchResultKind.Action when item.ActionId?.StartsWith("open-settings:", StringComparison.Ordinal) == true
                => _localizationService.T("Search.Action.OpenSettings"),
            _ => _localizationService.T("Search.Group.Actions")
        };
    }

    private (string? Label1, string? Value1, string? Label2, string? Value2) ResolveExtraMetadataPairs(
        SearchResultItem item)
    {
        if (item.Kind is SearchResultKind.File or SearchResultKind.Folder)
        {
            string? sizeVal = !string.IsNullOrWhiteSpace(item.SizeDisplay) && item.SizeDisplay != "—"
                ? item.SizeDisplay
                : null;
            string? dateVal = !string.IsNullOrWhiteSpace(item.DateDisplay)
                ? item.DateDisplay
                : (item.ModifiedAt.HasValue ? item.ModifiedAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : null);
            return (
                sizeVal is not null ? _localizationService.T("Search.Details.Size") : null,
                sizeVal,
                dateVal is not null ? _localizationService.T("Search.Details.Modified") : null,
                dateVal);
        }

        if (item.Kind == SearchResultKind.Clipboard)
        {
            string? appVal = !string.IsNullOrWhiteSpace(item.PreviewSourceApp) ? item.PreviewSourceApp : null;
            string? statsVal = item.PreviewCharCount is > 0
                ? string.Format(
                    _localizationService.T("Search.Details.StatsFormat"),
                    item.PreviewCharCount.Value,
                    Math.Max(1, item.PreviewLineCount ?? 1))
                : (item.ModifiedAt.HasValue ? item.ModifiedAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm") : null);
            string? statsLabel = item.PreviewCharCount is > 0
                ? _localizationService.T("Search.Details.Stats")
                : (statsVal is not null ? _localizationService.T("Search.Details.Modified") : null);
            return (
                appVal is not null ? _localizationService.T("Search.Details.SourceApp") : null,
                appVal,
                statsLabel,
                statsVal);
        }

        if (item.Kind is SearchResultKind.QuickCapture or SearchResultKind.Todo)
        {
            string? modVal = item.ModifiedAt.HasValue
                ? item.ModifiedAt.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm")
                : null;
            string? statsVal = item.PreviewCharCount is > 0
                ? string.Format(
                    _localizationService.T("Search.Details.StatsFormat"),
                    item.PreviewCharCount.Value,
                    Math.Max(1, item.PreviewLineCount ?? 1))
                : null;
            return (
                modVal is not null ? _localizationService.T("Search.Details.Modified") : null,
                modVal,
                statsVal is not null ? _localizationService.T("Search.Details.Stats") : null,
                statsVal);
        }

        return (null, null, null, null);
    }

    private void UpdateCommandBarForSelectedItem()
    {
        if (CmdPalResultFooterGrid is null)
        {
            return;
        }

        SearchInlineScope effectiveScope = _latchedInlineScope != SearchInlineScope.None
            ? _latchedInlineScope
            : SearchQuerySyntaxParser.Parse(SearchTextBox?.Text).Scope;
        CmdBarContextTitle.Text = GetScopeContextDisplayName(effectiveScope);
        CmdBarMoreLabel.Text = _localizationService.T("Search.CmdBar.More");

        SearchResultItem? selected = _viewModel.SelectedItem;
        if (selected is null)
        {
            CmdBarPrimaryButton.Visibility = Visibility.Collapsed;
            CmdBarPrimaryDivider.Visibility = Visibility.Collapsed;
            CmdBarSecondaryButton.Visibility = Visibility.Collapsed;
            return;
        }

        CmdBarPrimaryButton.Visibility = Visibility.Visible;
        CmdBarPrimaryLabel.Text = ResolvePrimaryActionVerb(selected);

        if (IsQuickTodoCreateAction(selected, out _))
        {
            CmdBarSecondaryLabel.Text = _localizationService.T("Search.QuickCreate.CmdSaveAndOpenTodo");
            CmdBarSecondaryButton.Visibility = Visibility.Visible;
            CmdBarPrimaryDivider.Visibility = Visibility.Visible;
        }
        else if (IsQuickNoteCreateAction(selected, out _))
        {
            CmdBarSecondaryLabel.Text = _localizationService.T("Search.QuickCreate.CmdSaveAndOpenNote");
            CmdBarSecondaryButton.Visibility = Visibility.Visible;
            CmdBarPrimaryDivider.Visibility = Visibility.Visible;
        }
        else if (CanRunItemAsAdmin(selected))
        {
            CmdBarSecondaryLabel.Text = _localizationService.T("Widget.RunAsAdministrator");
            CmdBarSecondaryButton.Visibility = Visibility.Visible;
            CmdBarPrimaryDivider.Visibility = Visibility.Visible;
        }
        else if (CanOpenItemLocation(selected))
        {
            CmdBarSecondaryLabel.Text = _localizationService.T("Search.Menu.OpenLocation");
            CmdBarSecondaryButton.Visibility = Visibility.Visible;
            CmdBarPrimaryDivider.Visibility = Visibility.Visible;
        }
        else
        {
            CmdBarSecondaryButton.Visibility = Visibility.Collapsed;
            CmdBarPrimaryDivider.Visibility = Visibility.Visible;
        }
    }

    private string ResolvePrimaryActionVerb(SearchResultItem item)
    {
        if (IsQuickTodoCreateAction(item, out _))
        {
            return _localizationService.T("Search.QuickCreate.CmdSaveTodo");
        }

        if (IsQuickNoteCreateAction(item, out _))
        {
            return _localizationService.T("Search.QuickCreate.CmdSaveNote");
        }

        if (item.Kind == SearchResultKind.Clipboard ||
            (item.Kind == SearchResultKind.Action &&
             item.ActionId?.StartsWith("copy-calc:", StringComparison.Ordinal) == true))
        {
            return _localizationService.T("Search.CmdBar.Copy");
        }

        if (item.Kind == SearchResultKind.Action &&
            item.ActionId?.StartsWith(SearchWebAndUrlEvaluator.OpenUrlActionPrefix, StringComparison.Ordinal) == true)
        {
            return _localizationService.T("Search.CmdBar.OpenUrl");
        }

        if (item.Kind == SearchResultKind.Action &&
            item.ActionId?.StartsWith(SearchWebAndUrlEvaluator.WebSearchActionPrefix, StringComparison.Ordinal) == true)
        {
            return _localizationService.T("Search.CmdBar.SearchWeb");
        }

        if (item.Kind == SearchResultKind.File &&
            FileCategoryHelper.Categorize(item.Title) == FileCategory.App)
        {
            return _localizationService.T("Search.CmdBar.Run");
        }

        return _localizationService.T("Search.CmdBar.Open");
    }

    private static bool IsQuickTodoCreateAction(SearchResultItem? item, out string todoText)
    {
        const string prefix = "quick-todo:";
        if (item?.Kind == SearchResultKind.Action &&
            item.ActionId is { } id &&
            id.StartsWith(prefix, StringComparison.Ordinal))
        {
            todoText = id[prefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(todoText);
        }

        todoText = string.Empty;
        return false;
    }

    private static bool IsQuickNoteCreateAction(SearchResultItem? item, out string noteText)
    {
        const string prefix = "quick-note:";
        if (item?.Kind == SearchResultKind.Action &&
            item.ActionId is { } id &&
            id.StartsWith(prefix, StringComparison.Ordinal))
        {
            noteText = id[prefix.Length..].Trim();
            return !string.IsNullOrWhiteSpace(noteText);
        }

        noteText = string.Empty;
        return false;
    }

    private string GetEffectiveQuickCreateText()
    {
        string raw = (SearchTextBox?.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        ParsedSearchQuery parsed = SearchQuerySyntaxParser.Parse(raw);
        return (parsed.EffectiveQuery ?? string.Empty).Trim();
    }

    private bool CanOfferShortcutQuickTodo() =>
        _settingsService.Settings.Search.SearchQuickCreateTodoEnabled &&
        FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Todo);

    private bool CanOfferShortcutQuickNote() =>
        _settingsService.Settings.Search.SearchQuickCreateNoteEnabled &&
        FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.QuickCapture);

    private bool TryExecuteShortcutQuickCreate(bool isTodo, bool openAfterSave)
    {
        if (isTodo && !CanOfferShortcutQuickTodo())
        {
            return false;
        }

        if (!isTodo && !CanOfferShortcutQuickNote())
        {
            return false;
        }

        string text = GetEffectiveQuickCreateText();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string prefix = isTodo
            ? (openAfterSave ? "quick-todo-open:" : "quick-todo:")
            : (openAfterSave ? "quick-note-open:" : "quick-note:");
        _viewModel.InvokeAction($"{prefix}{text}");
        HidePopup();
        return true;
    }

    private static bool CanRunItemAsAdmin(SearchResultItem? item) =>
        item?.Kind == SearchResultKind.File &&
        !string.IsNullOrWhiteSpace(item.DetailPath) &&
        ElevatedFileLauncher.CanRunAsAdministrator(item.DetailPath);

    private static bool CanOpenItemLocation(SearchResultItem? item) =>
        item?.Kind is SearchResultKind.File or SearchResultKind.Folder &&
        !string.IsNullOrWhiteSpace(item.DetailPath) &&
        (File.Exists(item.DetailPath) || Directory.Exists(item.DetailPath));

    private bool ExecuteSecondaryCommandForSelectedItem()
    {
        SearchResultItem? selected = _viewModel.SelectedItem;
        if (selected is null)
        {
            return false;
        }

        if (IsQuickTodoCreateAction(selected, out string todoText))
        {
            _viewModel.InvokeAction($"quick-todo-open:{todoText}");
            return true;
        }

        if (IsQuickNoteCreateAction(selected, out string noteText))
        {
            _viewModel.InvokeAction($"quick-note-open:{noteText}");
            return true;
        }

        if (CanRunItemAsAdmin(selected))
        {
            return TryRunItemAsAdministrator(selected);
        }

        return _viewModel.OpenSelectedLocation();
    }

    private bool TryRunItemAsAdministrator(SearchResultItem item)
    {
        if (string.IsNullOrWhiteSpace(item.DetailPath))
        {
            return false;
        }

        ElevatedFileLaunchResult result = ElevatedFileLauncher.RunAsAdministrator(
            _hwnd,
            item.DetailPath);
        if (result.Status == ElevatedFileLaunchStatus.Launched)
        {
            return true;
        }

        if (result.Status != ElevatedFileLaunchStatus.Cancelled)
        {
            ShowTransientStatus(_localizationService.T(
                result.Status == ElevatedFileLaunchStatus.AlreadyRunningUnelevated
                    ? "Widget.RunAsAdministrator.AlreadyRunning"
                    : "Widget.Error.OperationIncomplete"));
        }

        return false;
    }

    private void CmdBarPrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.ExecuteSelectedItem())
        {
            HidePopup();
        }
    }

    private void CmdBarSecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (ExecuteSecondaryCommandForSelectedItem())
        {
            HidePopup();
        }
    }

    private bool HandleCommandPaletteShortcutKeys(KeyRoutedEventArgs e)
    {
        bool ctrl = Win32Helper.IsKeyPressed(VirtualKey.Control);
        bool shift = Win32Helper.IsKeyPressed(VirtualKey.Shift);
        bool alt = Win32Helper.IsKeyPressed(VirtualKey.Menu);

        // Backspace on empty SearchTextBox exits latched inline scope back to Global Search.
        if (e.Key == VirtualKey.Back &&
            !ctrl && !shift && !alt &&
            _latchedInlineScope != SearchInlineScope.None &&
            string.IsNullOrEmpty(SearchTextBox.Text))
        {
            ExitLatchedScope();
            return true;
        }

        // Ctrl+T saves current query text directly as a Todo (Ctrl+Shift+T saves and opens).
        if (ctrl && !alt && e.Key == VirtualKey.T)
        {
            if (TryExecuteShortcutQuickCreate(isTodo: true, openAfterSave: shift))
            {
                return true;
            }
        }

        // Ctrl+N saves current query text directly to QuickCapture (Ctrl+Shift+N saves and opens).
        if (ctrl && !alt && e.Key == VirtualKey.N)
        {
            if (TryExecuteShortcutQuickCreate(isTodo: false, openAfterSave: shift))
            {
                return true;
            }
        }

        // Ctrl+D toggles the right-side Details sidebar live.
        if (ctrl && !shift && !alt && e.Key == VirtualKey.D)
        {
            ToggleDetailsPanePreference();
            return true;
        }

        // Ctrl+K opens the searchable secondary action menu when results are visible.
        if (ctrl && !shift && !alt && e.Key == VirtualKey.K)
        {
            if (CmdPalResultFooterGrid.Visibility == Visibility.Visible)
            {
                if (_isCmdBarFlyoutOpen)
                {
                    CmdBarActionsFlyout.Hide();
                }
                else
                {
                    CmdBarActionsFlyout.ShowAt(CmdBarMoreButton);
                }

                return true;
            }
        }

        // Ctrl+Shift+C copies the selected item's path/target when available.
        if (ctrl && shift && !alt && e.Key == VirtualKey.C &&
            _viewModel.SelectedItem is { } selectedForPath &&
            !string.IsNullOrWhiteSpace(selectedForPath.DetailPath))
        {
            CopyPathToClipboard(selectedForPath);
            return true;
        }

        // Ctrl+Shift+E opens file location even for admin-capable apps where Ctrl+Enter runs as admin.
        if (ctrl && shift && !alt && e.Key == VirtualKey.E &&
            CanOpenItemLocation(_viewModel.SelectedItem))
        {
            if (_viewModel.OpenSelectedLocation())
            {
                HidePopup();
            }

            return true;
        }

        // Alt+Enter shows Windows File Properties dialog.
        if (alt && !ctrl && !shift && e.Key == VirtualKey.Enter &&
            CanOpenItemLocation(_viewModel.SelectedItem))
        {
            Win32Helper.ShowFileProperties(_hwnd, _viewModel.SelectedItem!.DetailPath!);
            return true;
        }

        return false;
    }

    private void CmdBarActionsFlyout_Opening(object sender, object e)
    {
        _isCmdBarFlyoutOpen = true;
        BuildCmdBarActionEntries(_viewModel.SelectedItem);
        CmdBarActionsFilterBox.Text = string.Empty;
        ApplyCmdBarActionsFilter(string.Empty);
    }

    private void CmdBarActionsFlyout_Opened(object sender, object e)
    {
        CmdBarActionsFilterBox.Focus(FocusState.Programmatic);
    }

    private void CmdBarActionsFlyout_Closed(object sender, object e)
    {
        _isCmdBarFlyoutOpen = false;
        if (IsPopupVisible && !_isWindowClosed)
        {
            SearchTextBox.Focus(FocusState.Programmatic);
        }
    }

    private void BuildCmdBarActionEntries(SearchResultItem? item)
    {
        _allCmdBarActions.Clear();

        if (item is not null)
        {
            string primaryVerb = ResolvePrimaryActionVerb(item);
            _allCmdBarActions.Add(new CommandPaletteActionEntry(
                Id: "primary",
                Title: primaryVerb,
                Glyph: "\uE8E5",
                ShortcutDisplay: "↵",
                Keywords: "open run execute launch copy primary save",
                ExecuteAsync: () =>
                {
                    if (_viewModel.ExecuteItem(item))
                    {
                        HidePopup();
                    }
                    return Task.FromResult(true);
                },
                CustomIcon: Controls.SearchResultRowControl.ResolveNativeWidgetSvgIcon(item)));

            if (IsQuickTodoCreateAction(item, out string itemTodoText))
            {
                _allCmdBarActions.Add(new CommandPaletteActionEntry(
                    Id: "save-open-todo",
                    Title: _localizationService.T("Search.QuickCreate.CmdSaveAndOpenTodo"),
                    Glyph: "\uE9D5",
                    ShortcutDisplay: "Ctrl+↵",
                    Keywords: "todo task save open view 待办 查看 打开",
                    ExecuteAsync: () =>
                    {
                        _viewModel.InvokeAction($"quick-todo-open:{itemTodoText}");
                        HidePopup();
                        return Task.FromResult(true);
                    },
                    CustomIcon: Controls.SearchResultRowControl.GetTodoSvgIcon()));
            }
            else if (IsQuickNoteCreateAction(item, out string itemNoteText))
            {
                _allCmdBarActions.Add(new CommandPaletteActionEntry(
                    Id: "save-open-note",
                    Title: _localizationService.T("Search.QuickCreate.CmdSaveAndOpenNote"),
                    Glyph: "\uE70B",
                    ShortcutDisplay: "Ctrl+↵",
                    Keywords: "note jot capture save open view 随记 便签 打开 查看",
                    ExecuteAsync: () =>
                    {
                        _viewModel.InvokeAction($"quick-note-open:{itemNoteText}");
                        HidePopup();
                        return Task.FromResult(true);
                    },
                    CustomIcon: Controls.SearchResultRowControl.GetQuickCaptureSvgIcon()));
            }

            if (CanRunItemAsAdmin(item))
            {
                _allCmdBarActions.Add(new CommandPaletteActionEntry(
                    Id: "run-admin",
                    Title: _localizationService.T("Widget.RunAsAdministrator"),
                    Glyph: "\uE7EF",
                    ShortcutDisplay: "Ctrl+↵",
                    Keywords: "admin administrator elevate uac 管理员",
                    ExecuteAsync: () =>
                    {
                        if (TryRunItemAsAdministrator(item))
                        {
                            HidePopup();
                        }
                        return Task.FromResult(true);
                    }));
            }

            if (CanOpenItemLocation(item))
            {
                string locShortcut = CanRunItemAsAdmin(item) ? "Ctrl+Shift+E" : "Ctrl+↵";
                _allCmdBarActions.Add(new CommandPaletteActionEntry(
                    Id: "open-location",
                    Title: _localizationService.T("Search.Menu.OpenLocation"),
                    Glyph: "\uE838",
                    ShortcutDisplay: locShortcut,
                    Keywords: "folder explorer location reveal 目录 位置 文件夹",
                    ExecuteAsync: () =>
                    {
                        Win32Helper.ShowInExplorer(item.DetailPath!);
                        HidePopup();
                        return Task.FromResult(true);
                    }));
            }

            if (!string.IsNullOrWhiteSpace(item.DetailPath))
            {
                _allCmdBarActions.Add(new CommandPaletteActionEntry(
                    Id: "copy-path",
                    Title: _localizationService.T("Search.Menu.CopyPath"),
                    Glyph: "\uE8C8",
                    ShortcutDisplay: "Ctrl+Shift+C",
                    Keywords: "copy path url link 路径 复制",
                    ExecuteAsync: () =>
                    {
                        CopyPathToClipboard(item);
                        return Task.FromResult(true);
                    }));
            }

            if (CanOpenItemLocation(item))
            {
                if (CanAttachItem(item))
                {
                    _allCmdBarActions.Add(new CommandPaletteActionEntry(
                        Id: "attach-todo",
                        Title: _localizationService.T("Search.Menu.AttachToTodo"),
                        Glyph: "\uE723",
                        ShortcutDisplay: null,
                        Keywords: "todo task attach 待办",
                        ExecuteAsync: async () =>
                        {
                            await AttachItemToTodoAsync(item);
                            return true;
                        },
                        CustomIcon: Controls.SearchResultRowControl.GetTodoSvgIcon()));
                }

                if (CanSaveItem(item))
                {
                    _allCmdBarActions.Add(new CommandPaletteActionEntry(
                        Id: "save-note",
                        Title: _localizationService.T("Search.Menu.SaveToNote"),
                        Glyph: "\uE70B",
                        ShortcutDisplay: null,
                        Keywords: "note capture save 随记 笔记",
                        ExecuteAsync: async () =>
                        {
                            await SaveItemToNoteAsync(item);
                            return true;
                        },
                        CustomIcon: Controls.SearchResultRowControl.GetQuickCaptureSvgIcon()));
                }

                _allCmdBarActions.Add(new CommandPaletteActionEntry(
                    Id: "copy-file",
                    Title: _localizationService.T("Common.Copy"),
                    Glyph: "\uE8C8",
                    ShortcutDisplay: null,
                    Keywords: "copy file 复制文件",
                    ExecuteAsync: async () =>
                    {
                        await CopyFileSystemItemAsync(item, DataPackageOperation.Copy);
                        return true;
                    }));

                _allCmdBarActions.Add(new CommandPaletteActionEntry(
                    Id: "properties",
                    Title: _localizationService.T("Common.Properties"),
                    Glyph: "\uE946",
                    ShortcutDisplay: "Alt+↵",
                    Keywords: "properties info details 属性",
                    ExecuteAsync: () =>
                    {
                        Win32Helper.ShowFileProperties(_hwnd, item.DetailPath!);
                        return Task.FromResult(true);
                    }));
            }
        }

        string quickText = GetEffectiveQuickCreateText();
        if (!string.IsNullOrWhiteSpace(quickText))
        {
            string previewText = quickText.Length > 24
                ? string.Concat(quickText.AsSpan(0, 24), "…")
                : quickText;

            if (CanOfferShortcutQuickTodo() && !IsQuickTodoCreateAction(item, out _))
            {
                _allCmdBarActions.Add(new CommandPaletteActionEntry(
                    Id: "quick-create-todo-now",
                    Title: string.Format(_localizationService.T("Search.QuickCreate.TodoTitleFormat"), previewText),
                    Glyph: "\uE9D5",
                    ShortcutDisplay: "Ctrl+T",
                    Keywords: "todo task create add new 待办 新建 添加",
                    ExecuteAsync: () =>
                    {
                        _viewModel.InvokeAction($"quick-todo:{quickText}");
                        HidePopup();
                        return Task.FromResult(true);
                    },
                    CustomIcon: Controls.SearchResultRowControl.GetTodoSvgIcon()));
            }

            if (CanOfferShortcutQuickNote() && !IsQuickNoteCreateAction(item, out _))
            {
                _allCmdBarActions.Add(new CommandPaletteActionEntry(
                    Id: "quick-create-note-now",
                    Title: string.Format(_localizationService.T("Search.QuickCreate.NoteTitleFormat"), previewText),
                    Glyph: "\uE70B",
                    ShortcutDisplay: "Ctrl+N",
                    Keywords: "note jot capture save new 随记 速记 便签 保存",
                    ExecuteAsync: () =>
                    {
                        _viewModel.InvokeAction($"quick-note:{quickText}");
                        HidePopup();
                        return Task.FromResult(true);
                    },
                    CustomIcon: Controls.SearchResultRowControl.GetQuickCaptureSvgIcon()));
            }
        }

        // Offer direct browser web search for current query from Ctrl+K anytime!
        string activeText = (SearchTextBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(activeText) &&
            item?.ActionId?.StartsWith(SearchWebAndUrlEvaluator.WebSearchActionPrefix, StringComparison.Ordinal) != true)
        {
            var searchSettings = _settingsService.Settings.Search;
            string webUri = SearchWebAndUrlEvaluator.BuildFallbackWebSearchUri(
                activeText,
                searchSettings.SearchDefaultWebEngine,
                searchSettings.SearchCustomWebUrl);
            string webTitle = string.Format(
                _localizationService.T("Search.Web.FallbackFormat"),
                activeText.Length > 28 ? string.Concat(activeText.AsSpan(0, 28), "…") : activeText);
            _allCmdBarActions.Add(new CommandPaletteActionEntry(
                Id: "web-search-now",
                Title: webTitle,
                Glyph: "\uE774",
                ShortcutDisplay: null,
                Keywords: "web browser search bing google baidu 网页 浏览器 搜索",
                ExecuteAsync: () =>
                {
                    if (SearchWebAndUrlEvaluator.TryLaunchSafeExternalUri(webUri))
                    {
                        HidePopup();
                    }
                    return Task.FromResult(true);
                }));
        }

        bool detailsVisible = _settingsService.Settings.Search.SearchShowDetailsPane;
        _allCmdBarActions.Add(new CommandPaletteActionEntry(
            Id: "toggle-details",
            Title: _localizationService.T(
                detailsVisible ? "Search.Actions.ToggleDetailsHide" : "Search.Actions.ToggleDetailsShow"),
            Glyph: "\uE8A0",
            ShortcutDisplay: "Ctrl+D",
            Keywords: "sidebar details pane preview 侧栏 详情 预览",
            ExecuteAsync: () =>
            {
                ToggleDetailsPanePreference();
                return Task.FromResult(true);
            }));

        _allCmdBarActions.Add(new CommandPaletteActionEntry(
            Id: "open-search-settings",
            Title: _localizationService.T("Search.Action.OpenSettings"),
            Glyph: "\uE713",
            ShortcutDisplay: null,
            Keywords: "settings preferences config 设置",
            ExecuteAsync: () =>
            {
                HidePopup();
                ActionRequested?.Invoke(this, "open-settings");
                return Task.FromResult(true);
            }));
    }

    private void ApplyCmdBarActionsFilter(string? rawFilter)
    {
        string filter = (rawFilter ?? string.Empty).Trim();
        _filteredCmdBarActions.Clear();

        if (string.IsNullOrEmpty(filter))
        {
            _filteredCmdBarActions.AddRange(_allCmdBarActions);
        }
        else
        {
            foreach (var entry in _allCmdBarActions)
            {
                if (entry.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    entry.Keywords.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    SettingsSearchMatcher.GetScore(filter, entry.Title, entry.Keywords, entry.Id) != SettingsSearchMatcher.NoMatch)
                {
                    _filteredCmdBarActions.Add(entry);
                }
            }
        }

        CmdBarActionsListView.Items.Clear();
        foreach (CommandPaletteActionEntry entry in _filteredCmdBarActions)
        {
            CmdBarActionsListView.Items.Add(CreateCmdBarActionListViewItem(entry));
        }

        bool hasAny = _filteredCmdBarActions.Count > 0;
        CmdBarActionsListView.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        CmdBarActionsEmptyText.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
        if (hasAny)
        {
            CmdBarActionsListView.SelectedIndex = 0;
        }
    }

    private ListViewItem CreateCmdBarActionListViewItem(CommandPaletteActionEntry entry)
    {
        var rowGrid = new Grid
        {
            Tag = entry,
            MinHeight = 32,
            ColumnSpacing = 10,
            VerticalAlignment = VerticalAlignment.Center
        };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (entry.CustomIcon is not null)
        {
            var customImg = new Image
            {
                Source = entry.CustomIcon,
                Width = 16,
                Height = 16,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(customImg, 0);
            rowGrid.Children.Add(customImg);
        }
        else
        {
            var icon = new FontIcon
            {
                Glyph = entry.Glyph,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", Content)
            };
            Grid.SetColumn(icon, 0);
            rowGrid.Children.Add(icon);
        }

        var title = new TextBlock
        {
            Text = entry.Title,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1
        };
        Grid.SetColumn(title, 1);
        rowGrid.Children.Add(title);

        if (!string.IsNullOrWhiteSpace(entry.ShortcutDisplay))
        {
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 2),
                VerticalAlignment = VerticalAlignment.Center,
                Background = NeutralInteractionBrush.ResolveThemedResource("ControlFillColorSecondaryBrush", Content),
                BorderBrush = NeutralInteractionBrush.ResolveThemedResource("DividerStrokeColorDefaultBrush", Content),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = entry.ShortcutDisplay,
                    FontSize = 10.5,
                    Foreground = NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", Content)
                }
            };
            Grid.SetColumn(badge, 2);
            rowGrid.Children.Add(badge);
        }

        return new ListViewItem
        {
            Tag = entry,
            Content = rowGrid,
            MinHeight = 34,
            Padding = new Thickness(10, 2, 10, 2),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            AllowFocusOnInteraction = false,
            IsTabStop = false
        };
    }

    private void CmdBarActionsFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyCmdBarActionsFilter(CmdBarActionsFilterBox.Text);
    }

    private async void CmdBarActionsListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is FrameworkElement { Tag: CommandPaletteActionEntry tagged })
        {
            CmdBarActionsFlyout.Hide();
            await tagged.ExecuteAsync();
            return;
        }

        int idx = CmdBarActionsListView.SelectedIndex;
        if (idx >= 0 && idx < _filteredCmdBarActions.Count)
        {
            CommandPaletteActionEntry entry = _filteredCmdBarActions[idx];
            CmdBarActionsFlyout.Hide();
            await entry.ExecuteAsync();
        }
    }

    private async void CmdBarActionsFilterBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            CmdBarActionsFlyout.Hide();
            return;
        }

        if (e.Key == VirtualKey.Down)
        {
            e.Handled = true;
            if (_filteredCmdBarActions.Count > 0)
            {
                int next = (CmdBarActionsListView.SelectedIndex + 1) % _filteredCmdBarActions.Count;
                CmdBarActionsListView.SelectedIndex = next;
                CmdBarActionsListView.ScrollIntoView(CmdBarActionsListView.SelectedItem);
            }
            return;
        }

        if (e.Key == VirtualKey.Up)
        {
            e.Handled = true;
            if (_filteredCmdBarActions.Count > 0)
            {
                int prev = CmdBarActionsListView.SelectedIndex <= 0
                    ? _filteredCmdBarActions.Count - 1
                    : CmdBarActionsListView.SelectedIndex - 1;
                CmdBarActionsListView.SelectedIndex = prev;
                CmdBarActionsListView.ScrollIntoView(CmdBarActionsListView.SelectedItem);
            }
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            int idx = CmdBarActionsListView.SelectedIndex;
            if (idx >= 0 && idx < _filteredCmdBarActions.Count)
            {
                CommandPaletteActionEntry selected = _filteredCmdBarActions[idx];
                CmdBarActionsFlyout.Hide();
                await selected.ExecuteAsync();
            }
        }
    }
}
