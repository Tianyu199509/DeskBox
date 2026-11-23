using System.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Features.Search;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// Settings section for the global search feature: hotkey, Everything connection,
/// local/web scopes, recommendations, and results view. The injected editor owns
/// settings operations and visit cancellation.
/// </summary>
public sealed partial class SearchSettingsSection : UserControl
{
    private bool _isLoading;
    private SearchSettingsViewModel? _viewModel;
    private LocalizationService _localization = null!;
    private ISettingsHotkeyActions _hotkeyActions = null!;
    private nint _ownerWindow;
    private bool _hostActive;
    private bool _observingModel;
    private bool _everythingExpansionInitialized;
    private bool _lastEverythingUnhealthy;

    public SearchSettingsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private LocalizationService Localization => _localization;

    public void Configure(
        SearchSettingsViewModel viewModel,
        LocalizationService localization,
        ISettingsHotkeyActions hotkeyActions,
        nint ownerWindow)
    {
        _viewModel = viewModel;
        _localization = localization;
        _hotkeyActions = hotkeyActions;
        _ownerWindow = ownerWindow;
        RefreshFromSettings();
    }

    public void SetActive(bool active)
    {
        _hostActive = active;
        SynchronizeActivity();
    }

    private void SynchronizeActivity()
    {
        if (_viewModel is null)
        {
            return;
        }

        bool active = _hostActive && IsLoaded;
        if (_observingModel != active)
        {
            if (active)
            {
                _viewModel.PropertyChanged += OnEditorChanged;
            }
            else
            {
                _viewModel.PropertyChanged -= OnEditorChanged;
            }

            _observingModel = active;
        }

        if (active)
        {
            _everythingExpansionInitialized = false;
            _lastEverythingUnhealthy = false;
            _viewModel.Activate();
            RefreshFromSettings();
        }
        else
        {
            _viewModel.Deactivate();
        }
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_hostActive && IsLoaded)
        {
            RenderEditor();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Keep the template-default title (14px semi-bold) with the message
        // sized down to the settings-card description spec; see
        // SettingsWindow.ApplyNoticeMessageTypography for why the template
        // text block must be retargeted from code-behind.
        SettingsWindow.ApplyNoticeMessageTypography(SearchHotkeyReservedWarning);
        SettingsWindow.ApplyNoticeMessageTypography(EverythingStatusInfoBar);
        SettingsWindow.EnsureInformationalSeverityPinned(EverythingStatusInfoBar);
        SynchronizeActivity();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _everythingExpansionInitialized = false;
        _lastEverythingUnhealthy = false;
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.PropertyChanged -= OnEditorChanged;
        _observingModel = false;
        _viewModel.Deactivate();
    }

    /// <summary>
    /// Re-reads settings and updates the controls. Called when the section becomes visible.
    /// </summary>
    public void RefreshFromSettings()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.RefreshState();
        RenderEditor();
    }

    private void RenderEditor()
    {
        if (_viewModel is null)
        {
            return;
        }

        _isLoading = true;
        try
        {
            bool featureEnabled = _viewModel.State.FeatureEnabled;
            SearchPreferences settings = _viewModel.State.Preferences;

            EverythingEnableToggle.IsOn = settings.EverythingEnabled;
            EverythingEnableToggle.IsEnabled = featureEnabled;
            EverythingAdvancedSyntaxToggle.IsOn = settings.AdvancedSyntax;
            bool syntaxEnabled = featureEnabled && settings.EverythingEnabled;
            EverythingAdvancedSyntaxToggle.IsEnabled = syntaxEnabled;
            EverythingAdvancedSyntaxCard.IsEnabled = syntaxEnabled;

            SearchCalculatorToggle.IsOn = settings.IncludeCalculator;
            SearchLocalScopeExpander.IsEnabled = featureEnabled;
            RebuildSearchCategoryLayoutList(_viewModel.State);
            UpdateLocalScopeSummary(_viewModel.State);

            bool todoFeatureEnabled = _viewModel.State.TodoFeatureEnabled;
            bool noteFeatureEnabled = _viewModel.State.QuickCaptureFeatureEnabled;
            SearchQuickCreateExpander.IsEnabled = featureEnabled;
            SearchQuickCreateTodoCard.IsEnabled = featureEnabled && todoFeatureEnabled;
            SearchQuickCreateTodoToggle.IsEnabled = featureEnabled && todoFeatureEnabled;
            SearchQuickCreateTodoToggle.IsOn = todoFeatureEnabled && settings.QuickCreateTodoEnabled;
            SearchQuickCreateTodoCard.Description = todoFeatureEnabled
                ? Localization.T("Settings.Search.QuickCreate.Todo.Description")
                : Localization.T("Settings.Search.QuickCreate.Todo.DisabledByFeature");

            SearchQuickCreateNoteCard.IsEnabled = featureEnabled && noteFeatureEnabled;
            SearchQuickCreateNoteToggle.IsEnabled = featureEnabled && noteFeatureEnabled;
            SearchQuickCreateNoteToggle.IsOn = noteFeatureEnabled && settings.QuickCreateNoteEnabled;
            SearchQuickCreateNoteCard.Description = noteFeatureEnabled
                ? Localization.T("Settings.Search.QuickCreate.Note.Description")
                : Localization.T("Settings.Search.QuickCreate.Note.DisabledByFeature");

            bool anyQuickCreateEnabled = featureEnabled &&
                ((todoFeatureEnabled && settings.QuickCreateTodoEnabled) ||
                 (noteFeatureEnabled && settings.QuickCreateNoteEnabled));
            SearchQuickCreateDisplayModeCard.IsEnabled = anyQuickCreateEnabled;
            SearchQuickCreateDisplayModeComboBox.IsEnabled = anyQuickCreateEnabled;
            SearchQuickCreateDisplayModeComboBox.SelectedItem = SearchQuickCreateDisplayModeComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.QuickCreateDisplayMode,
                    StringComparison.OrdinalIgnoreCase))
                ?? SearchQuickCreateDisplayModeComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();

            bool bothQuickCreateEnabled = featureEnabled &&
                todoFeatureEnabled && settings.QuickCreateTodoEnabled &&
                noteFeatureEnabled && settings.QuickCreateNoteEnabled;
            SearchQuickCreateDefaultTargetCard.IsEnabled = bothQuickCreateEnabled;
            SearchQuickCreateDefaultTargetComboBox.IsEnabled = bothQuickCreateEnabled;
            SearchQuickCreateDefaultTargetComboBox.SelectedItem = SearchQuickCreateDefaultTargetComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.QuickCreateDefaultTarget,
                    StringComparison.OrdinalIgnoreCase))
                ?? SearchQuickCreateDefaultTargetComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();

            SearchQuickCreateHudCard.IsEnabled = anyQuickCreateEnabled;
            SearchQuickCreateHudToggle.IsEnabled = anyQuickCreateEnabled;
            SearchQuickCreateHudToggle.IsOn = settings.QuickCreateShowHud;
            UpdateQuickCreateSummary(settings, todoFeatureEnabled, noteFeatureEnabled);

            SearchWebFallbackToggle.IsOn = settings.IncludeWebFallback;
            SearchWebFallbackExpander.IsEnabled = featureEnabled;
            bool webEnabled = featureEnabled && settings.IncludeWebFallback;
            SearchWebEngineCard.IsEnabled = webEnabled;
            SearchWebEngineComboBox.IsEnabled = webEnabled;
            SearchWebEngineComboBox.SelectedItem = SearchWebEngineComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.DefaultWebEngine,
                    StringComparison.OrdinalIgnoreCase))
                ?? SearchWebEngineComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
            SearchCustomWebUrlTextBox.Text = settings.CustomWebUrl ?? string.Empty;
            SearchCustomWebUrlCard.IsEnabled = webEnabled;
            SearchCustomWebUrlTextBox.IsEnabled = webEnabled;
            SearchCustomWebUrlCard.Visibility = string.Equals(
                    settings.DefaultWebEngine,
                    "custom",
                    StringComparison.OrdinalIgnoreCase)
                ? Visibility.Visible
                : Visibility.Collapsed;

            SearchRecommendationsToggle.IsOn = settings.ShowRecommendations;
            SearchRecommendationsCard.IsEnabled = featureEnabled;
            bool recommendationsEnabled = featureEnabled && settings.ShowRecommendations;
            SearchIconAnimationCard.IsEnabled = recommendationsEnabled;
            SearchIconAnimationComboBox.IsEnabled = recommendationsEnabled;
            SearchIconAnimationComboBox.SelectedItem = SearchIconAnimationComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.IconAnimation.ToString(),
                    StringComparison.Ordinal));

            SearchResultsViewExpander.IsEnabled = featureEnabled;
            RebuildDefaultTabComboBox(_viewModel.State);
            SearchCategoryBarModeComboBox.SelectedItem = SearchCategoryBarModeComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.CategoryBarMode,
                    StringComparison.OrdinalIgnoreCase))
                ?? SearchCategoryBarModeComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
            SearchShowDetailsPaneToggle.IsOn = settings.ShowDetailsPane;
            SearchShowSortHeaderToggle.IsOn = settings.ShowSortHeader;
            SearchDefaultSortComboBox.SelectedItem = SearchDefaultSortComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    settings.DefaultSortColumn,
                    StringComparison.OrdinalIgnoreCase))
                ?? SearchDefaultSortComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();

            RefreshSearchHotkeyControls();
            UpdateEverythingDashboard(_viewModel.Connection);

            bool canOperate = featureEnabled && !_viewModel.IsBusy;
            EverythingDetectButton.IsEnabled = canOperate;
            EverythingBrowseButton.IsEnabled = canOperate;
            EverythingLaunchButton.IsEnabled = canOperate;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private bool IsCategoryFeatureEnabled(string categoryId, SearchSettingsSnapshot state) =>
        SearchSettingsSlice.GetLinkedFeatureWidgetKind(categoryId) switch
        {
            WidgetKind.Todo => state.TodoFeatureEnabled,
            WidgetKind.QuickCapture => state.QuickCaptureFeatureEnabled,
            WidgetKind.Clipboard => state.ClipboardFeatureEnabled,
            _ => true
        };

    private void UpdateLocalScopeSummary(SearchSettingsSnapshot state)
    {
        SearchPreferences settings = state.Preferences;
        IReadOnlyList<SearchCategoryEntry> entries = SearchSettingsSlice.ParseCategoryLayout(settings.CategoryLayout);
        int enabledSearchCount = 0;
        int shownTabCount = 0;
        foreach (SearchCategoryEntry entry in entries)
        {
            if (!IsCategoryFeatureEnabled(entry.Id, state))
            {
                continue;
            }

            if (entry.SearchEnabled)
            {
                enabledSearchCount++;
                if (entry.TabShown)
                {
                    shownTabCount++;
                }
            }
        }

        if (enabledSearchCount == 0 && !settings.IncludeCalculator)
        {
            SearchLocalScopeExpander.Description = Localization.T("Settings.Search.LocalScope.None");
            return;
        }

        SearchLocalScopeExpander.Description = string.Format(
            Localization.T("Settings.Search.CategoryLayout.SummaryFormat"),
            enabledSearchCount,
            shownTabCount + 1);
    }

    private void RebuildDefaultTabComboBox(SearchSettingsSnapshot state)
    {
        SearchPreferences settings = state.Preferences;
        IReadOnlyList<SearchCategoryEntry> entries = SearchSettingsSlice.ParseCategoryLayout(settings.CategoryLayout);
        string desiredDefault = SearchSettingsSlice.NormalizeDefaultTabId(settings.DefaultTab);

        SearchDefaultTabComboBox.Items.Clear();
        SearchDefaultTabComboBox.Items.Add(new ComboBoxItem
        {
            Tag = SearchSettingsSlice.CategoryAll,
            Content = Localization.T("Search.Tab.All")
        });

        foreach (SearchCategoryEntry entry in entries)
        {
            if (!entry.SearchEnabled || !entry.TabShown || !IsCategoryFeatureEnabled(entry.Id, state))
            {
                continue;
            }

            SearchDefaultTabComboBox.Items.Add(new ComboBoxItem
            {
                Tag = entry.Id,
                Content = Localization.T(SearchSettingsSlice.GetCategoryNameKey(entry.Id))
            });
        }

        SearchDefaultTabComboBox.SelectedItem = SearchDefaultTabComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, desiredDefault, StringComparison.OrdinalIgnoreCase))
            ?? SearchDefaultTabComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void RebuildSearchCategoryLayoutList(SearchSettingsSnapshot state)
    {
        string currentLayout = SearchSettingsSlice.NormalizeCategoryLayout(state.Preferences.CategoryLayout);
        IReadOnlyList<SearchCategoryEntry> entries = SearchSettingsSlice.ParseCategoryLayout(currentLayout);
        ResetSearchCategoryLayoutButton.IsEnabled =
            state.FeatureEnabled && !SearchSettingsSlice.IsDefaultCategoryLayout(currentLayout);

        bool allowDragReorder = state.FeatureEnabled && !DragDropPermissionService.IsCurrentProcessElevatedCached();
        SearchCategoryLayoutListView.AllowDrop = allowDragReorder;
        SearchCategoryLayoutListView.CanDragItems = allowDragReorder;
        SearchCategoryLayoutListView.CanReorderItems = allowDragReorder;
        SearchCategoryLayoutListView.ReorderMode = allowDragReorder
            ? ListViewReorderMode.Enabled
            : ListViewReorderMode.Disabled;

        var itemContainerStyle = new Style(typeof(ListViewItem));
        if (Application.Current?.Resources is { } appRes &&
            appRes.TryGetValue("DefaultListViewItemStyle", out object? baseObj) &&
            baseObj is Style baseItemStyle)
        {
            itemContainerStyle.BasedOn = baseItemStyle;
        }
        itemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        itemContainerStyle.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34d));
        itemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(6, 1, 6, 1)));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1)));
        SearchCategoryLayoutListView.ItemContainerStyle = itemContainerStyle;

        string scopeChipLabel = Localization.T("Search.Tab.ScopeChip");
        string tabChipLabel = Localization.T("Search.Tab.TabChip");
        string featureOffLabel = Localization.T("Search.Tab.FeatureDisabled");
        string moveUpText = Localization.T("Settings.Clipboard.TabLayout.MoveUp");
        string moveDownText = Localization.T("Settings.Clipboard.TabLayout.MoveDown");

        var transparentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Microsoft.UI.Xaml.Media.Brush primaryText = NeutralInteractionBrush.ResolveThemedResource("TextFillColorPrimaryBrush", this) ?? transparentBrush;
        Microsoft.UI.Xaml.Media.Brush secondaryText = NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", this) ?? transparentBrush;
        Microsoft.UI.Xaml.Media.Brush tertiaryText = NeutralInteractionBrush.ResolveThemedResource("TextFillColorTertiaryBrush", this) ?? transparentBrush;
        Microsoft.UI.Xaml.Media.Brush onAccentText = NeutralInteractionBrush.ResolveThemedResource("TextOnAccentFillColorPrimaryBrush", this) ?? transparentBrush;
        Microsoft.UI.Xaml.Media.Brush accentFill = NeutralInteractionBrush.ResolveThemedResource("AccentFillColorDefaultBrush", this) ?? transparentBrush;
        Microsoft.UI.Xaml.Media.Brush activeSearchFill = NeutralInteractionBrush.ResolveThemedResource("ControlFillColorSecondaryBrush", this) ?? transparentBrush;
        Microsoft.UI.Xaml.Media.Brush subtleStroke = NeutralInteractionBrush.ResolveThemedResource("ControlStrokeColorDefaultBrush", this) ?? transparentBrush;

        SearchCategoryLayoutListView.Items.Clear();

        for (int index = 0; index < entries.Count; index++)
        {
            SearchCategoryEntry entry = entries[index];
            string categoryId = entry.Id;
            string categoryLabel = Localization.T(SearchSettingsSlice.GetCategoryNameKey(categoryId));
            bool widgetEnabled = IsCategoryFeatureEnabled(categoryId, state);
            bool rowInteractive = state.FeatureEnabled && widgetEnabled;
            bool effectiveSearchOn = widgetEnabled && entry.SearchEnabled;
            bool effectiveTabOn = widgetEnabled && entry.SearchEnabled && entry.TabShown;

            var rowGrid = new Grid
            {
                ColumnSpacing = 8,
                MinHeight = 32,
                VerticalAlignment = VerticalAlignment.Center
            };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var gripIcon = new FontIcon
            {
                Glyph = "\uE784",
                FontSize = 13,
                Opacity = 0.45,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = allowDragReorder ? Visibility.Visible : Visibility.Collapsed
            };
            ToolTipService.SetToolTip(gripIcon, Localization.T("CustomWidget.Module.DragHint"));
            Grid.SetColumn(gripIcon, 0);
            rowGrid.Children.Add(gripIcon);

            FrameworkElement iconElement = CreateSettingsCategoryIconElement(categoryId, effectiveSearchOn ? 0.92 : 0.42);
            Grid.SetColumn(iconElement, 1);
            rowGrid.Children.Add(iconElement);

            var titleStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center
            };
            titleStack.Children.Add(new TextBlock
            {
                Text = categoryLabel,
                FontSize = 13,
                Foreground = effectiveSearchOn ? primaryText : secondaryText,
                Opacity = widgetEnabled ? 1.0 : 0.52,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!widgetEnabled)
            {
                titleStack.Children.Add(new TextBlock
                {
                    Text = featureOffLabel,
                    FontSize = 11.5,
                    Foreground = tertiaryText,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            Grid.SetColumn(titleStack, 2);
            rowGrid.Children.Add(titleStack);

            var moveButtonsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 2,
                Opacity = allowDragReorder ? 0.55 : 0.90,
                VerticalAlignment = VerticalAlignment.Center
            };
            moveButtonsPanel.Children.Add(CreateSettingsMoveCategoryButton(categoryId, -1, "\uE70E", state.FeatureEnabled && index > 0, $"{categoryLabel} - {moveUpText}"));
            moveButtonsPanel.Children.Add(CreateSettingsMoveCategoryButton(categoryId, +1, "\uE70D", state.FeatureEnabled && index < entries.Count - 1, $"{categoryLabel} - {moveDownText}"));
            rowGrid.PointerEntered += (_, _) =>
            {
                gripIcon.Opacity = 0.88;
                moveButtonsPanel.Opacity = 1.0;
            };
            rowGrid.PointerExited += (_, _) =>
            {
                gripIcon.Opacity = 0.45;
                moveButtonsPanel.Opacity = allowDragReorder ? 0.55 : 0.90;
            };
            Grid.SetColumn(moveButtonsPanel, 3);
            rowGrid.Children.Add(moveButtonsPanel);

            var chipsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                VerticalAlignment = VerticalAlignment.Center
            };

            var searchChip = CreateSettingsStateChipButton(
                glyph: "\uE721",
                text: scopeChipLabel,
                isActive: effectiveSearchOn,
                isEnabled: rowInteractive,
                activeBackground: activeSearchFill,
                activeBorder: subtleStroke,
                activeForeground: primaryText,
                inactiveBorder: subtleStroke,
                inactiveForeground: tertiaryText,
                transparentBrush: transparentBrush,
                automationName: $"{categoryLabel} - {scopeChipLabel}");
            searchChip.Click += (_, _) =>
            {
                if (_isLoading || _viewModel is null) return;
                string latest = _viewModel.State.Preferences.CategoryLayout;
                string next = SearchSettingsSlice.SetCategorySearchEnabled(latest, categoryId, !effectiveSearchOn);
                _viewModel.UpdatePreferences(new(CategoryLayout: next));
            };

            var tabChip = CreateSettingsStateChipButton(
                glyph: "\uE8EC",
                text: tabChipLabel,
                isActive: effectiveTabOn,
                isEnabled: rowInteractive,
                activeBackground: accentFill,
                activeBorder: accentFill,
                activeForeground: onAccentText,
                inactiveBorder: subtleStroke,
                inactiveForeground: tertiaryText,
                transparentBrush: transparentBrush,
                automationName: $"{categoryLabel} - {tabChipLabel}");
            tabChip.Click += (_, _) =>
            {
                if (_isLoading || _viewModel is null) return;
                string latest = _viewModel.State.Preferences.CategoryLayout;
                string next = SearchSettingsSlice.SetCategoryTabShown(latest, categoryId, !effectiveTabOn);
                _viewModel.UpdatePreferences(new(CategoryLayout: next));
            };

            chipsPanel.Children.Add(searchChip);
            chipsPanel.Children.Add(tabChip);
            Grid.SetColumn(chipsPanel, 4);
            rowGrid.Children.Add(chipsPanel);

            SearchCategoryLayoutListView.Items.Add(new ListViewItem
            {
                Tag = categoryId,
                Content = rowGrid,
                IsTabStop = false
            });
        }
    }

    private static FrameworkElement CreateSettingsCategoryIconElement(string categoryId, double opacity)
    {
        Microsoft.UI.Xaml.Media.ImageSource? svgSource = categoryId switch
        {
            SearchSettingsSlice.CategoryTodo => SearchResultRowControl.GetTodoSvgIcon(),
            SearchSettingsSlice.CategoryNote => SearchResultRowControl.GetQuickCaptureSvgIcon(),
            _ => null
        };
        if (svgSource is not null)
        {
            return new Image
            {
                Source = svgSource,
                Width = 15,
                Height = 15,
                Opacity = opacity,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return new FontIcon
        {
            Glyph = SearchSettingsSlice.GetCategoryGlyph(categoryId),
            FontSize = 13.5,
            Opacity = opacity,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private Button CreateSettingsMoveCategoryButton(string categoryId, int delta, string glyph, bool isEnabled, string automationName)
    {
        var btn = new Button
        {
            Width = 24,
            Height = 24,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(5),
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            IsEnabled = isEnabled,
            Opacity = isEnabled ? 0.85 : 0.28,
            Content = new FontIcon { Glyph = glyph, FontSize = 10 }
        };
        AutomationProperties.SetName(btn, automationName);
        ToolTipService.SetToolTip(btn, automationName);
        btn.Click += (_, _) =>
        {
            if (_isLoading || _viewModel is null) return;
            string latest = _viewModel.State.Preferences.CategoryLayout;
            string next = SearchSettingsSlice.MoveCategory(latest, categoryId, delta);
            _viewModel.UpdatePreferences(new(CategoryLayout: next));
        };
        return btn;
    }

    private static Button CreateSettingsStateChipButton(
        string glyph,
        string text,
        bool isActive,
        bool isEnabled,
        Microsoft.UI.Xaml.Media.Brush activeBackground,
        Microsoft.UI.Xaml.Media.Brush activeBorder,
        Microsoft.UI.Xaml.Media.Brush activeForeground,
        Microsoft.UI.Xaml.Media.Brush inactiveBorder,
        Microsoft.UI.Xaml.Media.Brush inactiveForeground,
        Microsoft.UI.Xaml.Media.Brush transparentBrush,
        string automationName)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 10.5,
            Foreground = isActive ? activeForeground : inactiveForeground,
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = isActive ? activeForeground : inactiveForeground,
            VerticalAlignment = VerticalAlignment.Center
        });

        var btn = new Button
        {
            Height = 26,
            MinHeight = 26,
            MinWidth = 0,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Background = isActive ? activeBackground : transparentBrush,
            BorderBrush = isActive ? activeBorder : inactiveBorder,
            IsEnabled = isEnabled,
            Opacity = isEnabled ? 1.0 : 0.42,
            VerticalAlignment = VerticalAlignment.Center,
            Content = content
        };
        AutomationProperties.SetName(btn, automationName);
        ToolTipService.SetToolTip(btn, automationName);
        return btn;
    }

    private void ResetSearchCategoryLayoutButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _viewModel is null)
        {
            return;
        }

        _viewModel.UpdatePreferences(new(CategoryLayout: SearchSettingsSlice.DefaultCategoryLayout));
    }

    private void SearchCategoryLayoutListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (_isLoading || _viewModel is null)
        {
            return;
        }

        var orderedIds = SearchCategoryLayoutListView.Items
            .OfType<ListViewItem>()
            .Select(static item => item.Tag as string)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .ToList();
        if (orderedIds.Count == 0)
        {
            return;
        }

        string next = SearchSettingsSlice.ReorderCategories(_viewModel.State.Preferences.CategoryLayout, orderedIds);
        _viewModel.UpdatePreferences(new(CategoryLayout: next));
    }

    private void UpdateQuickCreateSummary(
        SearchPreferences settings,
        bool todoFeatureEnabled,
        bool noteFeatureEnabled)
    {
        var activeTargets = new List<string>(2);
        if (todoFeatureEnabled && settings.QuickCreateTodoEnabled)
        {
            activeTargets.Add(Localization.T("Widget.Type.Todo"));
        }

        if (noteFeatureEnabled && settings.QuickCreateNoteEnabled)
        {
            activeTargets.Add(Localization.T("Widget.Type.QuickCapture"));
        }

        SearchQuickCreateExpander.Description = activeTargets.Count == 0
            ? Localization.T("Settings.Search.LocalScope.None")
            : string.Join(Localization.T("Glance.Display.Separator"), activeTargets);
    }

    private void SearchQuickCreateTodoToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(QuickCreateTodoEnabled: SearchQuickCreateTodoToggle.IsOn));
    }

    private void SearchQuickCreateNoteToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(QuickCreateNoteEnabled: SearchQuickCreateNoteToggle.IsOn));
    }

    private void SearchQuickCreateDisplayModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SearchQuickCreateDisplayModeComboBox.SelectedItem is not ComboBoxItem { Tag: string mode })
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(QuickCreateDisplayMode: mode));
    }

    private void SearchQuickCreateDefaultTargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SearchQuickCreateDefaultTargetComboBox.SelectedItem is not ComboBoxItem { Tag: string target })
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(QuickCreateDefaultTarget: target));
    }

    private void SearchQuickCreateHudToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(QuickCreateShowHud: SearchQuickCreateHudToggle.IsOn));
    }

    private void SearchRecommendationsToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(ShowRecommendations: SearchRecommendationsToggle.IsOn));
    }

    private void SearchDefaultTabComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SearchDefaultTabComboBox.SelectedItem is not ComboBoxItem { Tag: string tabId })
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(DefaultTab: tabId));
    }

    private void SearchCalculatorToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(IncludeCalculator: SearchCalculatorToggle.IsOn));
    }

    private void SearchWebFallbackToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(IncludeWebFallback: SearchWebFallbackToggle.IsOn));
    }

    private void SearchWebEngineComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SearchWebEngineComboBox.SelectedItem is not ComboBoxItem { Tag: string engineId })
        {
            return;
        }

        SearchCustomWebUrlCard.Visibility = string.Equals(engineId, "custom", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
        _viewModel?.UpdatePreferences(new(DefaultWebEngine: engineId));
    }

    private void SearchCustomWebUrlTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitCustomWebUrl();
    }

    private void SearchCustomWebUrlTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            CommitCustomWebUrl();
        }
    }

    private void CommitCustomWebUrl()
    {
        if (_isLoading)
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(CustomWebUrl: SearchCustomWebUrlTextBox.Text ?? string.Empty));
    }

    private void SearchCategoryBarModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SearchCategoryBarModeComboBox.SelectedItem is not ComboBoxItem { Tag: string mode })
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(CategoryBarMode: mode));
    }

    private void SearchShowDetailsPaneToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(ShowDetailsPane: SearchShowDetailsPaneToggle.IsOn));
    }

    private void SearchShowSortHeaderToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(ShowSortHeader: SearchShowSortHeaderToggle.IsOn));
    }

    private void SearchDefaultSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SearchDefaultSortComboBox.SelectedItem is not ComboBoxItem { Tag: string sortCol })
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(DefaultSortColumn: sortCol));
    }

    private void SearchIconAnimationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || SearchIconAnimationComboBox.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !int.TryParse(value, out int style))
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(IconAnimation: style));
    }

    private void UpdateEverythingDashboard(EverythingConnectionSnapshot snapshot)
    {
        if (_viewModel is null)
        {
            return;
        }

        bool featureEnabled = _viewModel.State.FeatureEnabled;
        bool enabled = _viewModel.State.Preferences.EverythingEnabled;

        EverythingStatusInfoBar.Title =
            Localization.T("Settings.Search.Everything.StatusTitle");
        EverythingStatusInfoBar.Severity = snapshot.State switch
        {
            EverythingConnectionState.Connected => InfoBarSeverity.Success,
            EverythingConnectionState.Checking or EverythingConnectionState.NotConfirmed =>
                InfoBarSeverity.Informational,
            EverythingConnectionState.NotInstalled or EverythingConnectionState.NotRunning =>
                InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Error
        };
        EverythingStatusInfoBar.Message = snapshot.State switch
        {
            EverythingConnectionState.Unknown =>
                Localization.T("Settings.Search.Everything.Status.Unknown"),
            EverythingConnectionState.Checking =>
                Localization.T("Settings.Search.Everything.Status.Checking"),
            EverythingConnectionState.NotConfirmed =>
                Localization.T("Settings.Search.Everything.Status.NotConfirmed"),
            EverythingConnectionState.NotInstalled =>
                Localization.T("Settings.Search.Everything.Status.NotInstalled"),
            EverythingConnectionState.NotRunning =>
                Localization.T("Settings.Search.Everything.Status.NotRunning"),
            EverythingConnectionState.PermissionMismatch =>
                Localization.T("Settings.Search.Everything.Status.PermissionMismatch"),
            EverythingConnectionState.IpcUnavailable =>
                Localization.T("Settings.Search.Everything.Status.IpcUnavailable"),
            EverythingConnectionState.SdkUnavailable =>
                Localization.T("Settings.Search.Everything.Status.SdkUnavailable"),
            EverythingConnectionState.Connected => Localization.Format(
                "Settings.Search.Everything.Status.Connected",
                snapshot.Version ?? Localization.T("Settings.Search.Everything.VersionUnknown")),
            _ => Localization.T("Settings.Search.Everything.Status.Error")
        };

        EverythingPathText.Text = string.IsNullOrWhiteSpace(snapshot.ExecutablePath)
            ? Localization.T("Settings.Search.Everything.Path.NotFound")
            : Localization.Format(
                snapshot.UsesManualPath
                    ? "Settings.Search.Everything.Path.Manual"
                    : "Settings.Search.Everything.Path.Detected",
                snapshot.ExecutablePath);

        EverythingLaunchButton.Visibility =
            !string.IsNullOrWhiteSpace(snapshot.ExecutablePath) && !snapshot.IsRunning
                ? Visibility.Visible
                : Visibility.Collapsed;
        bool isStoreBuild = AppDistributionService.Current.IsMicrosoftStore;
        EverythingDownloadButton.Visibility =
            !isStoreBuild && snapshot.State == EverythingConnectionState.NotInstalled
                ? Visibility.Visible
                : Visibility.Collapsed;

        if (!featureEnabled)
        {
            EverythingStatusInfoBar.Severity = InfoBarSeverity.Informational;
            EverythingStatusInfoBar.Message = Localization.T("Settings.Search.Index.Status.Disabled");
        }
        else if (_viewModel.Failure != SearchSettingsFailure.None)
        {
            EverythingStatusInfoBar.Severity = InfoBarSeverity.Error;
            EverythingStatusInfoBar.Message = Localization.T(
                _viewModel.Failure == SearchSettingsFailure.InvalidExecutable
                    ? "Settings.Search.Everything.Status.InvalidExecutable"
                    : "Settings.Search.Everything.Status.Error");
        }

        EverythingConnectionExpander.Description = ResolveEverythingSummaryText(
            featureEnabled,
            enabled,
            snapshot.State,
            _viewModel.Failure);

        bool isUnhealthy = featureEnabled && enabled &&
            (_viewModel.Failure != SearchSettingsFailure.None ||
             snapshot.State is
                 EverythingConnectionState.NotInstalled or
                 EverythingConnectionState.NotRunning or
                 EverythingConnectionState.PermissionMismatch or
                 EverythingConnectionState.IpcUnavailable or
                 EverythingConnectionState.SdkUnavailable);

        if (!_everythingExpansionInitialized)
        {
            _everythingExpansionInitialized = true;
            EverythingConnectionExpander.IsExpanded = isUnhealthy;
        }
        else if (isUnhealthy && !_lastEverythingUnhealthy)
        {
            EverythingConnectionExpander.IsExpanded = true;
        }

        _lastEverythingUnhealthy = isUnhealthy;
    }

    private string ResolveEverythingSummaryText(
        bool featureEnabled,
        bool enabled,
        EverythingConnectionState state,
        SearchSettingsFailure failure)
    {
        if (!featureEnabled || !enabled || state == EverythingConnectionState.NotConfirmed)
        {
            return Localization.T("Settings.Search.Everything.Summary.Disabled");
        }

        if (failure != SearchSettingsFailure.None)
        {
            return Localization.T("Settings.Search.Everything.Summary.Error");
        }

        return state switch
        {
            EverythingConnectionState.Connected =>
                Localization.T("Settings.Search.Everything.Summary.Connected"),
            EverythingConnectionState.Checking or EverythingConnectionState.Unknown =>
                Localization.T("Settings.Search.Everything.Summary.Checking"),
            EverythingConnectionState.NotInstalled =>
                Localization.T("Settings.Search.Everything.Summary.NotInstalled"),
            EverythingConnectionState.NotRunning =>
                Localization.T("Settings.Search.Everything.Summary.NotRunning"),
            _ => Localization.T("Settings.Search.Everything.Summary.Error")
        };
    }

    private void EverythingEnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded)
        {
            return;
        }

        bool enabled = EverythingEnableToggle.IsOn;
        _viewModel?.UpdatePreferences(new(EverythingEnabled: enabled));
    }

    private void EverythingAdvancedSyntaxToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading || !IsLoaded)
        {
            return;
        }

        _viewModel?.UpdatePreferences(new(AdvancedSyntax: EverythingAdvancedSyntaxToggle.IsOn));
    }

    private async void EverythingDetectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { } editor)
        {
            await editor.DetectAutomaticallyAsync();
        }
    }

    private async void EverythingLaunchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is { } editor)
        {
            await editor.LaunchEverythingAsync();
        }
    }

    private async void EverythingBrowseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not { IsActive: true } editor || _ownerWindow == 0)
        {
            return;
        }

        CancellationToken visit = editor.VisitToken;
        try
        {
            string? executablePath = await FileOpenPickerService.PickSingleFileAsync(
                _ownerWindow,
                [".exe"],
                PickerLocationId.ComputerFolder);
            if (executablePath is null || visit.IsCancellationRequested)
            {
                return;
            }

            await editor.SelectExecutableAsync(executablePath);
        }
        catch (Exception ex)
        {
            if (!visit.IsCancellationRequested)
            {
                editor.ReportViewError(ex);
            }
        }
    }

    private async void EverythingDownloadButton_Click(object sender, RoutedEventArgs e)
    {
        if (AppDistributionService.Current.IsMicrosoftStore)
        {
            return;
        }

        _ = await Launcher.LaunchUriAsync(new Uri("https://www.voidtools.com/downloads/"));
    }

    private async void EverythingHelpButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            Title = Localization.T("Settings.Search.Everything.About.Title"),
            CloseButtonText = Localization.T("Settings.Dialog.SupportClose"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(this)
        };

        var body = new StackPanel { Spacing = 12, MaxWidth = 420 };
        body.Children.Add(new TextBlock
        {
            Text = Localization.T("Settings.Search.Everything.About.P1"),
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(new TextBlock
        {
            Text = Localization.T("Settings.Search.Everything.SharingNotice"),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8
        });
        body.Children.Add(new TextBlock
        {
            Text = Localization.T("Settings.Search.Everything.About.P3"),
            TextWrapping = TextWrapping.Wrap
        });

        if (!AppDistributionService.Current.IsMicrosoftStore)
        {
            var linksRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
            linksRow.Children.Add(new HyperlinkButton
            {
                NavigateUri = new Uri("https://www.voidtools.com/downloads/"),
                Content = new TextBlock
                {
                    Text = Localization.T("Settings.Search.Everything.Download"),
                    TextWrapping = TextWrapping.Wrap
                },
                Padding = new Thickness(0)
            });
            linksRow.Children.Add(new HyperlinkButton
            {
                NavigateUri = new Uri("https://www.voidtools.com/support/everything/installing-everything/"),
                Content = new TextBlock
                {
                    Text = Localization.T("Settings.Search.Everything.SetupHelp"),
                    TextWrapping = TextWrapping.Wrap
                },
                Padding = new Thickness(0)
            });
            body.Children.Add(linksRow);
        }

        dialog.Content = body;
        await dialog.ShowAsync();
    }

    private void RefreshSearchHotkeyControls()
    {
        if (_viewModel is null)
        {
            return;
        }

        SearchHotkeyState hotkey = _viewModel.State.Hotkey;
        bool hotkeyAvailable = _viewModel.State.FeatureEnabled && hotkey.Available;

        // Content-level gating on purpose: the card itself stays enabled
        // so the InfoTip about the unavailable hotkey can always be opened;
        // only the controls that would mutate the hotkey go gray.
        SearchHotkeyToggle.IsEnabled = hotkeyAvailable;
        SearchHotkeyCaptureButton.IsEnabled = hotkeyAvailable;
        SearchHotkeyToggle.IsOn = hotkey.Enabled && hotkeyAvailable;

        KeyVisualState visualState = hotkeyAvailable && hotkey.Enabled && !hotkey.Registered
            ? KeyVisualState.Warning
            : KeyVisualState.Normal;
        SearchHotkeyCaptureButton.Content = KeyVisualControl.CreateInlineButtonContent(
            hotkey.EffectiveActivation,
            hotkey.DisplayText,
            showEditIcon: true,
            state: visualState,
            doubleTapBadgeText: Localization.T("Settings.HotkeyRecorder.DoubleTapBadge"));
        AutomationProperties.SetName(SearchHotkeyCaptureButton, hotkey.DisplayText);

        SearchHotkeyCard.Description = !hotkeyAvailable
            ? Localization.T("Settings.Search.Hotkey.Status.Disabled")
            : _viewModel.HotkeyError ?? Localization.T(
                !hotkey.Enabled ? "Settings.Search.Hotkey.Status.Disabled" :
                hotkey.Registered ? "Settings.Search.Hotkey.Status.Active" : "Settings.Search.Hotkey.Status.Failed");

        UpdateSearchHotkeyReservedWarning(
            hotkey.EffectiveActivation,
            hotkey.DisplayText,
            hotkeyAvailable && hotkey.Enabled);
    }

    private void UpdateSearchHotkeyReservedWarning(
        GlobalHotkeyActivation activation,
        string displayText,
        bool active)
    {
        bool isReserved = active &&
            (activation.Kind == HotkeyActivationKind.WindowsTap ||
             activation.IsStandaloneSpaceDoubleTap ||
             (activation.Kind is HotkeyActivationKind.Chord or HotkeyActivationKind.DoubleTap &&
              !activation.IsModifierOnlyDoubleTap &&
              GlobalHotkeyService.IsReservedSystemGesture(activation.Gesture)));

        if (!isReserved)
        {
            SearchHotkeyReservedWarning.IsOpen = false;
            SearchHotkeyReservedWarning.Visibility = Visibility.Collapsed;
            return;
        }

        string warningMessage = activation.Kind == HotkeyActivationKind.WindowsTap
            ? Localization.T("Settings.GlobalHotkey.WindowsTapWarning")
            : activation.IsStandaloneSpaceDoubleTap
                ? Localization.T("Settings.GlobalHotkey.DoubleSpaceWarning")
                : activation.Kind is HotkeyActivationKind.Chord or HotkeyActivationKind.DoubleTap &&
                  !activation.IsModifierOnlyDoubleTap &&
                  activation.Gesture.Modifiers == HotkeyModifierKeys.Alt &&
                  activation.Gesture.VirtualKey == (int)VirtualKey.Space
                    ? Localization.T("Settings.GlobalHotkey.AltSpaceWarning")
                    : Localization.T("Settings.GlobalHotkey.ReservedWarning");

        SearchHotkeyReservedWarning.Title = displayText;
        SearchHotkeyReservedWarning.Message = warningMessage;
        SearchHotkeyReservedWarning.Visibility = Visibility.Visible;
        SearchHotkeyReservedWarning.IsOpen = true;
    }

    private void SearchHotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _viewModel?.SetHotkeyEnabled(SearchHotkeyToggle.IsOn);
    }

    private async void SearchHotkeyCaptureButton_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null || _viewModel is not { IsActive: true } editor)
        {
            return;
        }

        GlobalHotkeyActivation current = editor.State.Hotkey.EffectiveActivation;
        HotkeyRecorderDialog.Preset[] presets =
        [
            new(
                "Alt + D",
                GlobalHotkeyActivation.FromChord(SearchSettingsViewModel.DefaultGesture)),
            new(
                Localization.T("Settings.GlobalHotkey.Preset.AltSpace"),
                GlobalHotkeyActivation.FromChord(SearchSettingsViewModel.AltSpaceGesture)),
            new(
                Localization.T("Settings.GlobalHotkey.Preset.DoubleSpace"),
                GlobalHotkeyService.DoubleSpaceActivation),
            new(
                Localization.T("Settings.GlobalHotkey.Preset.DoubleControl"),
                new GlobalHotkeyActivation(
                    HotkeyActivationKind.DoubleControl,
                    new GlobalHotkeyGesture(HotkeyModifierKeys.Control, 0))),
            new(
                Localization.T("Settings.GlobalHotkey.Preset.DoubleShift"),
                GlobalHotkeyActivation.FromDoubleTapModifier(HotkeyModifierKeys.Shift)),
            new(
                Localization.T("Settings.GlobalHotkey.Preset.WinSpace"),
                GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                    HotkeyModifierKeys.Windows,
                    (int)VirtualKey.Space)))
        ];

        var dialog = new HotkeyRecorderDialog(
            XamlRoot,
            Localization,
            _hotkeyActions,
            HotkeyRecorderDialog.Scope.Search,
            current,
            presets,
            activation => ApplySearchHotkeyFromRecorderAsync(editor, activation),
            GetSearchHotkeyRecorderConflict);
        await dialog.ShowAsync();
    }

    private Task<string?> ApplySearchHotkeyFromRecorderAsync(
        SearchSettingsViewModel editor,
        GlobalHotkeyActivation activation)
    {
        try
        {
            editor.ApplyHotkeyActivation(activation);
            return Task.FromResult(editor.HotkeyError);
        }
        catch (Exception ex)
        {
            editor.ReportViewError(ex);
            return Task.FromResult<string?>(
                Localization.T("Settings.Search.Hotkey.Status.Failed"));
        }
    }

    private string? GetSearchHotkeyRecorderConflict(GlobalHotkeyActivation activation)
    {
        if (_viewModel is { IsActive: true } editor)
        {
            if (editor.IsActivationOwnedByMainHotkey(activation))
            {
                return Localization.T("Settings.Search.Hotkey.Status.GlobalHotkeyConflict");
            }

            if (editor.IsActivationOwnedByClipboardHotkey(activation))
            {
                return Localization.T("Settings.Clipboard.Hotkey.Status.ClipboardHotkeyConflict");
            }
        }

        return null;
    }
}
