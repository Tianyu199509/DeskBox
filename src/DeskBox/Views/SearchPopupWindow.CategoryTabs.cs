using System;
using System.Collections.Generic;
using System.Linq;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Views;

public sealed partial class SearchPopupWindow
{
    private bool _isManageTabsFlyoutOpen;

    private bool IsAnyPopupFlyoutOpen => _isCmdBarFlyoutOpen || _isManageTabsFlyoutOpen;

    private void ApplyLocalizedCategoryTabsStrings()
    {
        ToolTipService.SetToolTip(ManageTabsButton, _localizationService.T("Search.Tab.Manage"));
        ManageTabsFlyoutTitle.Text = _localizationService.T("Search.Tab.ManageTitle");
        ManageTabsResetLabel.Text = _localizationService.T("Settings.Clipboard.TabLayout.Reset");
        ToolTipService.SetToolTip(ManageTabsOpenSettingsButton, _localizationService.T("Search.Action.OpenSettings"));
        if (_isManageTabsFlyoutOpen)
        {
            RebuildManageTabsFlyoutList();
        }
    }

    private void TabsScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(TabsScrollViewer).Properties.MouseWheelDelta;
        if (delta == 0 || TabsScrollViewer.ScrollableWidth <= 0)
        {
            return;
        }

        double targetOffset = Math.Clamp(
            TabsScrollViewer.HorizontalOffset - (delta * 0.65),
            0,
            TabsScrollViewer.ScrollableWidth);
        _ = TabsScrollViewer.ChangeView(targetOffset, null, null, disableAnimation: true);
        e.Handled = true;
    }

    private void ScrollSelectedTabIntoView()
    {
        if (TabsList.SelectedItem is { } selected)
        {
            TabsList.ScrollIntoView(selected);
        }
    }

    private void ManageTabsFlyout_Opening(object sender, object e)
    {
        _isManageTabsFlyoutOpen = true;
        RebuildManageTabsFlyoutList();
    }

    private void ManageTabsFlyout_Opened(object sender, object e)
    {
    }

    private void ManageTabsFlyout_Closed(object sender, object e)
    {
        _isManageTabsFlyoutOpen = false;
        if (IsPopupVisible && !_isWindowClosed && _isPopupInputActive)
        {
            SearchTextBox.Focus(FocusState.Programmatic);
        }
    }

    private void ManageTabsResetButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyCategoryLayoutMutation(SearchSettingsSlice.DefaultCategoryLayout, rerunSearch: true);
        RebuildManageTabsFlyoutList();
    }

    private void ManageTabsOpenSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        ManageTabsFlyout.Hide();
        HidePopup();
        ActionRequested?.Invoke(this, "open-settings");
    }

    private void ManageTabsListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        var orderedIds = ManageTabsListView.Items
            .OfType<ListViewItem>()
            .Select(static item => item.Tag as string)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Select(static id => id!)
            .ToList();
        if (orderedIds.Count == 0)
        {
            return;
        }

        string currentLayout = _settingsService.Settings.Search.SearchCategoryLayout;
        string updatedLayout = SearchSettingsSlice.ReorderCategories(currentLayout, orderedIds);
        ApplyCategoryLayoutMutation(updatedLayout, rerunSearch: false);
        RebuildManageTabsFlyoutList();
    }

    private void ApplyCategoryLayoutMutation(string updatedLayout, bool rerunSearch)
    {
        string normalized = SearchSettingsSlice.NormalizeCategoryLayout(updatedLayout);
        var search = _settingsService.Settings.Search;
        if (string.Equals(search.SearchCategoryLayout, normalized, StringComparison.Ordinal))
        {
            return;
        }

        bool previousDeskBoxContent = search.SearchIncludeDeskBoxContent;
        IReadOnlyList<SearchCategoryEntry> parsed = SearchSettingsSlice.ParseCategoryLayout(normalized);
        search.SearchCategoryLayout = normalized;
        search.SearchIncludeDeskBoxContent = parsed.Any(static entry =>
            entry.Id is SearchSettingsSlice.CategoryTodo or SearchSettingsSlice.CategoryNote or SearchSettingsSlice.CategoryWidget &&
            entry.SearchEnabled);
        search.SearchIncludeClipboard = parsed.Any(static entry =>
            entry.Id == SearchSettingsSlice.CategoryClipboard && entry.SearchEnabled);
        if (!string.Equals(search.SearchDefaultTab, SearchSettingsSlice.CategoryAll, StringComparison.Ordinal) &&
            !parsed.Any(entry => string.Equals(entry.Id, search.SearchDefaultTab, StringComparison.Ordinal) && entry.TabShown))
        {
            search.SearchDefaultTab = SearchSettingsSlice.CategoryAll;
        }

        if (previousDeskBoxContent != search.SearchIncludeDeskBoxContent)
        {
            _viewModel.EnsureDeskBoxContentSearchSynced(search.SearchIncludeDeskBoxContent);
        }

        _settingsService.SaveDebounced(notifySubscribers: false);
        _viewModel.RefreshActiveSearchAndTabs(rerunSearch);
        UpdatePanelVisibility();
    }

    private void RebuildManageTabsFlyoutList()
    {
        AppSettings appSettings = _settingsService.Settings;
        string currentLayout = SearchSettingsSlice.NormalizeCategoryLayout(
            appSettings.Search.SearchCategoryLayout);
        IReadOnlyList<SearchCategoryEntry> entries = SearchSettingsSlice.ParseCategoryLayout(currentLayout);
        ManageTabsResetButton.IsEnabled = !SearchSettingsSlice.IsDefaultCategoryLayout(currentLayout);

        bool allowDragReorder = !DragDropPermissionService.IsCurrentProcessElevatedCached();
        ManageTabsListView.AllowDrop = allowDragReorder;
        ManageTabsListView.CanDragItems = allowDragReorder;
        ManageTabsListView.CanReorderItems = allowDragReorder;
        ManageTabsListView.ReorderMode = allowDragReorder
            ? ListViewReorderMode.Enabled
            : ListViewReorderMode.Disabled;

        string scopeChipLabel = _localizationService.T("Search.Tab.ScopeChip");
        string tabChipLabel = _localizationService.T("Search.Tab.TabChip");
        string featureOffLabel = _localizationService.T("Search.Tab.FeatureDisabled");
        string moveUpText = _localizationService.T("Settings.Clipboard.TabLayout.MoveUp");
        string moveDownText = _localizationService.T("Settings.Clipboard.TabLayout.MoveDown");

        Brush transparentBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Brush primaryText = NeutralInteractionBrush.ResolveThemedResource("TextFillColorPrimaryBrush", RootGrid) ?? transparentBrush;
        Brush secondaryText = NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", RootGrid) ?? transparentBrush;
        Brush tertiaryText = NeutralInteractionBrush.ResolveThemedResource("TextFillColorTertiaryBrush", RootGrid) ?? transparentBrush;
        Brush onAccentText = NeutralInteractionBrush.ResolveThemedResource("TextOnAccentFillColorPrimaryBrush", RootGrid) ?? transparentBrush;
        Brush accentFill = NeutralInteractionBrush.ResolveThemedResource("AccentFillColorDefaultBrush", RootGrid) ?? transparentBrush;
        Brush activeSearchFill = NeutralInteractionBrush.ResolveThemedResource("ControlFillColorSecondaryBrush", RootGrid) ?? transparentBrush;
        Brush subtleStroke = NeutralInteractionBrush.ResolveThemedResource("ControlStrokeColorDefaultBrush", RootGrid) ?? transparentBrush;

        ManageTabsListView.Items.Clear();

        for (int index = 0; index < entries.Count; index++)
        {
            SearchCategoryEntry entry = entries[index];
            string categoryId = entry.Id;
            string categoryLabel = _localizationService.T(SearchSettingsSlice.GetCategoryNameKey(categoryId));
            bool featureEnabled = SearchSettingsSlice.IsCategoryFeatureEnabled(appSettings, categoryId);
            bool effectiveSearchOn = featureEnabled && entry.SearchEnabled;
            bool effectiveTabOn = featureEnabled && entry.SearchEnabled && entry.TabShown;

            var rowGrid = new Grid
            {
                ColumnSpacing = 6,
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
                FontSize = 12,
                Opacity = 0.42,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = allowDragReorder ? Visibility.Visible : Visibility.Collapsed
            };
            Grid.SetColumn(gripIcon, 0);
            rowGrid.Children.Add(gripIcon);

            FrameworkElement iconElement = CreateCategoryIconElement(categoryId, effectiveSearchOn ? 0.92 : 0.42);
            Grid.SetColumn(iconElement, 1);
            rowGrid.Children.Add(iconElement);

            var titlePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };
            titlePanel.Children.Add(new TextBlock
            {
                Text = categoryLabel,
                FontSize = 12.5,
                Foreground = effectiveSearchOn ? primaryText : secondaryText,
                Opacity = featureEnabled ? 1.0 : 0.52,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (!featureEnabled)
            {
                titlePanel.Children.Add(new TextBlock
                {
                    Text = featureOffLabel,
                    FontSize = 10.5,
                    Foreground = tertiaryText,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            Grid.SetColumn(titlePanel, 2);
            rowGrid.Children.Add(titlePanel);

            var movePanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 1,
                Opacity = allowDragReorder ? 0.45 : 0.85,
                VerticalAlignment = VerticalAlignment.Center
            };
            var upBtn = CreateTinyMoveButton(categoryId, -1, "\uE70E", index > 0, $"{categoryLabel} - {moveUpText}");
            var downBtn = CreateTinyMoveButton(categoryId, +1, "\uE70D", index < entries.Count - 1, $"{categoryLabel} - {moveDownText}");
            movePanel.Children.Add(upBtn);
            movePanel.Children.Add(downBtn);
            rowGrid.PointerEntered += (_, _) =>
            {
                gripIcon.Opacity = 0.85;
                movePanel.Opacity = 1.0;
            };
            rowGrid.PointerExited += (_, _) =>
            {
                gripIcon.Opacity = 0.42;
                movePanel.Opacity = allowDragReorder ? 0.45 : 0.85;
            };
            Grid.SetColumn(movePanel, 3);
            rowGrid.Children.Add(movePanel);

            var chipsPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                VerticalAlignment = VerticalAlignment.Center
            };

            var searchChip = CreateCompactStateChipButton(
                glyph: "\uE721",
                text: scopeChipLabel,
                isActive: effectiveSearchOn,
                isEnabled: featureEnabled,
                activeBackground: activeSearchFill,
                activeBorder: subtleStroke,
                activeForeground: primaryText,
                inactiveBorder: subtleStroke,
                inactiveForeground: tertiaryText,
                transparentBrush: transparentBrush,
                automationName: $"{categoryLabel} - {scopeChipLabel}");
            searchChip.Click += (_, _) =>
            {
                string latest = _settingsService.Settings.Search.SearchCategoryLayout;
                string next = SearchSettingsSlice.SetCategorySearchEnabled(latest, categoryId, !effectiveSearchOn);
                ApplyCategoryLayoutMutation(next, rerunSearch: true);
                RebuildManageTabsFlyoutList();
            };

            var tabChip = CreateCompactStateChipButton(
                glyph: "\uE8EC",
                text: tabChipLabel,
                isActive: effectiveTabOn,
                isEnabled: featureEnabled,
                activeBackground: accentFill,
                activeBorder: accentFill,
                activeForeground: onAccentText,
                inactiveBorder: subtleStroke,
                inactiveForeground: tertiaryText,
                transparentBrush: transparentBrush,
                automationName: $"{categoryLabel} - {tabChipLabel}");
            tabChip.Click += (_, _) =>
            {
                string latest = _settingsService.Settings.Search.SearchCategoryLayout;
                bool targetTabShown = !effectiveTabOn;
                bool wasSearchOff = !effectiveSearchOn && targetTabShown;
                string next = SearchSettingsSlice.SetCategoryTabShown(latest, categoryId, targetTabShown);
                ApplyCategoryLayoutMutation(next, rerunSearch: wasSearchOff);
                RebuildManageTabsFlyoutList();
            };

            chipsPanel.Children.Add(searchChip);
            chipsPanel.Children.Add(tabChip);
            Grid.SetColumn(chipsPanel, 4);
            rowGrid.Children.Add(chipsPanel);

            ManageTabsListView.Items.Add(new ListViewItem
            {
                Tag = categoryId,
                Content = rowGrid,
                MinHeight = 32,
                Padding = new Thickness(6, 0, 6, 0),
                Margin = new Thickness(0, 1, 0, 1),
                CornerRadius = new CornerRadius(6),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
                AllowFocusOnInteraction = false,
                IsTabStop = false
            });
        }
    }

    private FrameworkElement CreateCategoryIconElement(string categoryId, double opacity)
    {
        ImageSource? svgSource = categoryId switch
        {
            SearchSettingsSlice.CategoryTodo => Controls.SearchResultRowControl.GetTodoSvgIcon(),
            SearchSettingsSlice.CategoryNote => Controls.SearchResultRowControl.GetQuickCaptureSvgIcon(),
            _ => null
        };
        if (svgSource is not null)
        {
            return new Image
            {
                Source = svgSource,
                Width = 14,
                Height = 14,
                Opacity = opacity,
                Stretch = Stretch.Uniform,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return new FontIcon
        {
            Glyph = SearchSettingsSlice.GetCategoryGlyph(categoryId),
            FontSize = 13,
            Opacity = opacity,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private Button CreateTinyMoveButton(string categoryId, int delta, string glyph, bool isEnabled, string automationName)
    {
        var btn = new Button
        {
            Style = (Style)RootGrid.Resources["ClearInputButtonStyle"],
            Width = 20,
            Height = 22,
            CornerRadius = new CornerRadius(4),
            IsEnabled = isEnabled,
            Opacity = isEnabled ? 0.78 : 0.25,
            Content = new FontIcon { Glyph = glyph, FontSize = 9.5 }
        };
        AutomationProperties.SetName(btn, automationName);
        ToolTipService.SetToolTip(btn, automationName);
        btn.Click += (_, _) =>
        {
            string latest = _settingsService.Settings.Search.SearchCategoryLayout;
            string next = SearchSettingsSlice.MoveCategory(latest, categoryId, delta);
            ApplyCategoryLayoutMutation(next, rerunSearch: false);
            RebuildManageTabsFlyoutList();
        };
        return btn;
    }

    private static Button CreateCompactStateChipButton(
        string glyph,
        string text,
        bool isActive,
        bool isEnabled,
        Brush activeBackground,
        Brush activeBorder,
        Brush activeForeground,
        Brush inactiveBorder,
        Brush inactiveForeground,
        Brush transparentBrush,
        string automationName)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center
        };
        content.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontSize = 10,
            Foreground = isActive ? activeForeground : inactiveForeground,
            VerticalAlignment = VerticalAlignment.Center
        });
        content.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = isActive ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            Foreground = isActive ? activeForeground : inactiveForeground,
            VerticalAlignment = VerticalAlignment.Center
        });

        var btn = new Button
        {
            Height = 24,
            MinHeight = 24,
            MinWidth = 0,
            Padding = new Thickness(6, 0, 6, 0),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Background = isActive ? activeBackground : transparentBrush,
            BorderBrush = isActive ? activeBorder : inactiveBorder,
            IsEnabled = isEnabled,
            Opacity = isEnabled ? 1.0 : 0.42,
            IsTabStop = false,
            AllowFocusOnInteraction = false,
            VerticalAlignment = VerticalAlignment.Center,
            Content = content
        };
        AutomationProperties.SetName(btn, automationName);
        ToolTipService.SetToolTip(btn, automationName);
        return btn;
    }
}
