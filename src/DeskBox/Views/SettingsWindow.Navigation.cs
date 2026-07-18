using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private void InitializeSettingsSectionElements()
    {
        _settingsSectionElements = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal)
        {
            ["General"] = GeneralSection,
            ["Appearance"] = AppearanceSection,
            ["AppearanceMaterialSettings"] = AppearanceMaterialSettingsSection,
            ["AppearanceDensitySettings"] = AppearanceDensitySettingsSection,
            ["AppearanceWindowSettings"] = AppearanceWindowSettingsSection,
            ["AppearanceAnimationSettings"] = AppearanceAnimationSettingsSection,
            ["CapsuleMode"] = CapsuleModeSection,
            ["CapsuleBehaviorSettings"] = CapsuleBehaviorSettingsSection,
            ["CapsuleContentSettings"] = CapsuleContentSettingsSection,
            ["CapsuleAnimationSettings"] = CapsuleAnimationSettingsSection,
            ["CapsuleOverridesSettings"] = CapsuleOverridesSettingsSection,
            ["AppearanceDetail"] = AppearanceDetailSection,
            ["FileDisplaySettings"] = FileDisplaySettingsSection,
            ["FileStorageSettings"] = FileStorageSettingsSection,
            ["FileStackSettings"] = FileStackSettingsSection,
            ["FeatureWidgets"] = FeatureWidgetsSection,
            ["QuickCaptureSettings"] = QuickCaptureSettingsSection,
            ["TodoSettings"] = TodoSettingsSection,
            ["MusicSettings"] = MusicSettingsSection,
            ["WeatherSettings"] = WeatherSettingsSection,
            ["Interaction"] = InteractionSection,
            ["ManagedStorage"] = ManagedStorageSection,
            ["Maintenance"] = MaintenanceSection,
            ["BackupRestoreSettings"] = BackupRestoreSettingsSection,
            ["DataHealthSettings"] = DataHealthSettingsSection,
            ["CompatibilityDiagnosticsSettings"] = CompatibilityDiagnosticsSettingsSection,
            ["ResetSettings"] = ResetSettingsSection,
            ["About"] = AboutSection
        };

        string[] missingRoutes = SectionRoutes.Keys
            .Where(tag => tag != "Advanced" && !_settingsSectionElements.ContainsKey(tag))
            .ToArray();
        if (missingRoutes.Length > 0)
        {
            throw new InvalidOperationException(
                $"Settings sections are not registered: {string.Join(", ", missingRoutes)}");
        }
    }

    private void SettingsNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_isSyncingNavigationSelection)
        {
            return;
        }

        if (args.SelectedItem is NavigationViewItem { Tag: string sectionTag })
        {
            ShowSettingsSection(sectionTag, isNestedSection: false);
        }
    }

    private void RefreshSettingsSearchResults()
    {
        _settingsSearchResults = SectionRoutes.Values
            .Where(route => route.Tag != "Advanced")
            .Select(route =>
            {
                string title = _localizationService.T(route.TitleKey);
                string breadcrumb = route.ParentTag is not null &&
                    TryGetSectionRoute(route.ParentTag, out var parentRoute)
                    ? $"{_localizationService.T(parentRoute.TitleKey)} / {title}"
                    : title;
                return new SettingsSearchResult(route.Tag, title, breadcrumb);
            })
            .ToArray();

        if (SettingsSearchBox is null)
        {
            return;
        }

        SettingsSearchBox.PlaceholderText = _localizationService.T("Settings.Search.Placeholder");
        UpdateSettingsSearchSuggestions(SettingsSearchBox.Text);
    }

    private void UpdateSettingsSearchSuggestions(string? query)
    {
        if (SettingsSearchBox is null)
        {
            return;
        }

        string normalizedQuery = query?.Trim() ?? string.Empty;
        if (normalizedQuery.Length == 0)
        {
            SettingsSearchBox.ItemsSource = Array.Empty<SettingsSearchResult>();
            SettingsSearchBox.IsSuggestionListOpen = false;
            return;
        }

        SettingsSearchResult[] matches = _settingsSearchResults
            .Where(result =>
                result.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ||
                result.Breadcrumb.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Take(8)
            .ToArray();
        SettingsSearchBox.ItemsSource = matches;
        SettingsSearchBox.IsSuggestionListOpen = matches.Length > 0;
    }

    private void SettingsSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            UpdateSettingsSearchSuggestions(sender.Text);
        }
    }

    private void SettingsSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        SettingsSearchResult? result = args.ChosenSuggestion as SettingsSearchResult;
        if (result is null)
        {
            string query = sender.Text.Trim();
            result = _settingsSearchResults.FirstOrDefault(item =>
                item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                item.Breadcrumb.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (result is null)
        {
            return;
        }

        NavigateToSettingsSection(result.SectionTag);
        sender.Text = string.Empty;
        UpdateSettingsSearchSuggestions(string.Empty);
    }

    private void SettingsNavigationView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        if (TryGetSectionRoute(_currentSettingsSection, out var route) &&
            !string.IsNullOrWhiteSpace(route.ParentTag))
        {
            NavigateToSettingsSection(route.ParentTag);
        }
    }

    public void ShowSection(string sectionTag)
    {
        NavigateToSettingsSection(sectionTag);
    }

    public void RefreshUpdateStateFromService()
    {
        ViewModel.RefreshCachedUpdateState();
    }

    private NavigationViewItem? FindNavItemByTag(string tag)
    {
        foreach (var item in SettingsNavigationView.MenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag is string navTag && navTag == tag)
            {
                return navItem;
            }
        }
        return null;
    }

    private void NavigateToSettingsSection(string sectionTag)
    {
        ShowSettingsSection(sectionTag);
        var navItem = GetNavItemForSection(sectionTag);
        if (navItem is not null && !ReferenceEquals(SettingsNavigationView.SelectedItem, navItem))
        {
            _isSyncingNavigationSelection = true;
            try
            {
                SettingsNavigationView.SelectedItem = navItem;
            }
            finally
            {
                _isSyncingNavigationSelection = false;
            }
        }
    }

    private NavigationViewItem? GetNavItemForSection(string sectionTag)
    {
        return TryGetSectionRoute(sectionTag, out var route)
            ? FindNavItemByTag(route.NavTag) ?? GeneralNavItem
            : GeneralNavItem;
    }

    private void ShowSettingsSection(string sectionTag, bool isNestedSection = false)
    {
        if (!TryGetSectionRoute(sectionTag, out var route))
        {
            sectionTag = "General";
            route = SectionRoutes[sectionTag];
        }

        isNestedSection = !string.IsNullOrWhiteSpace(route.ParentTag);
        _currentSettingsSection = sectionTag;
        string visibleSectionTag = sectionTag == "Advanced" ? "Interaction" : sectionTag;
        foreach ((string tag, FrameworkElement sectionElement) in _settingsSectionElements)
        {
            sectionElement.Visibility = string.Equals(tag, visibleSectionTag, StringComparison.Ordinal)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (sectionTag == "FileStackSettings")
        {
            _ = ViewModel.RefreshFileStackRulePreviewFromDiskAsync();
        }
        if (sectionTag == "FeatureWidgets")
        {
            RefreshFeatureWidgetList();
        }
        if (sectionTag == "QuickCaptureSettings")
        {
            ViewModel.RefreshQuickCaptureClipboardDiagnostics();
            _ = ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
        }
        if (sectionTag == "ManagedStorage")
        {
            RefreshManagedStorageFolderList();
        }
        else if (sectionTag == "FileStorageSettings")
        {
            _ = ViewModel.RefreshQuickAccessStateAsync();
        }
        if (sectionTag == "CompatibilityDiagnosticsSettings")
        {
            ViewModel.RefreshDragDropPermissionDiagnostic();
        }
        SettingsNavigationView.IsBackButtonVisible = isNestedSection
            ? NavigationViewBackButtonVisible.Visible
            : NavigationViewBackButtonVisible.Collapsed;
        UpdateBreadcrumb(route);

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            PageScroller.ChangeView(null, 0, null, disableAnimation: true);
            RestartSectionLayoutSettleTimer();
        });
    }

    private void UpdateBreadcrumb(SettingsSectionRoute route)
    {
        if (string.IsNullOrWhiteSpace(route.ParentTag) ||
            !TryGetSectionRoute(route.ParentTag, out var parentRoute))
        {
            SettingsBreadcrumbBar.Visibility = Visibility.Collapsed;
            SettingsBreadcrumbBar.ItemsSource = null;
            return;
        }

        SettingsBreadcrumbBar.ItemsSource = new[]
        {
            new SettingsBreadcrumbItem(parentRoute.Tag, _localizationService.T(parentRoute.TitleKey), 0.62),
            new SettingsBreadcrumbItem(route.Tag, _localizationService.T(route.TitleKey), 1.0)
        };
        SettingsBreadcrumbBar.Visibility = Visibility.Visible;
    }

    private void SettingsBreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is not SettingsBreadcrumbItem item ||
            string.Equals(item.SectionTag, _currentSettingsSection, StringComparison.Ordinal))
        {
            return;
        }

        NavigateToSettingsSection(item.SectionTag);
    }

    private static bool TryGetSectionRoute(string sectionTag, out SettingsSectionRoute route)
    {
        return SectionRoutes.TryGetValue(sectionTag, out route!);
    }

    private void AccentPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex } ||
            !AccentColorHelper.TryParseHex(hex, out var color))
        {
            return;
        }

        ViewModel.SetCustomAccentColor(color);
    }

    private void SettingsComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ComboBox combo ||
            combo.Tag is not string menuKind ||
            combo.SelectedIndex < 0 ||
            e.AddedItems.Count == 0 ||
            e.AddedItems[0] is not string displayName)
        {
            return;
        }

        switch (menuKind)
        {
            case "Theme":
                ViewModel.SelectedTheme = ViewModel.AvailableThemes[combo.SelectedIndex];
                break;
            case "Language":
                ViewModel.SelectedLanguage = ViewModel.AvailableLanguages[combo.SelectedIndex];
                break;
            case "MusicDisplayMode":
                ViewModel.SelectedMusicDisplayMode = ViewModel.AvailableMusicDisplayModes[combo.SelectedIndex];
                break;
            case "FileStackGroupBy":
                ViewModel.SelectedFileStackGroupBy = ViewModel.AvailableFileStackGroupBys[combo.SelectedIndex];
                break;
            case "FileStackThreshold":
                ViewModel.SelectedFileStackThreshold = ViewModel.AvailableFileStackThresholds[combo.SelectedIndex];
                break;
            case "FileStackOrderBy":
                ViewModel.SelectedFileStackOrderBy = ViewModel.AvailableFileStackOrderBys[combo.SelectedIndex];
                break;
            case "FileStackUnmatchedBehavior":
                ViewModel.SelectedFileStackUnmatchedBehavior =
                    ViewModel.AvailableFileStackUnmatchedBehaviors[combo.SelectedIndex];
                break;
            case "WidgetCorner":
                ViewModel.SelectedWidgetCornerPreference = ViewModel.AvailableWidgetCornerPreferences[combo.SelectedIndex];
                break;
            case "WidgetMaterial":
                ViewModel.SelectedWidgetMaterialType = ViewModel.AvailableWidgetMaterialTypes[combo.SelectedIndex];
                break;
            case "WidgetBorderColor":
                ViewModel.SelectedWidgetBorderColorMode = ViewModel.AvailableWidgetBorderColorModes[combo.SelectedIndex];
                break;
            case "WidgetBorder":
                ViewModel.SelectedWidgetBorderStyle = ViewModel.AvailableWidgetBorderStyles[combo.SelectedIndex];
                break;
            case "WidgetCollapseBehavior":
                ViewModel.SelectedWidgetCollapseBehavior = ViewModel.AvailableWidgetCollapseBehaviors[combo.SelectedIndex];
                break;
            case "WidgetCompactContentMode":
                ViewModel.SelectedWidgetCompactContentMode =
                    ViewModel.AvailableWidgetCompactContentModes[combo.SelectedIndex];
                break;
            case "WidgetCompactAnimationEffect":
                ViewModel.SelectedWidgetCompactAnimationEffect = ViewModel.AvailableWidgetCompactAnimationEffects[combo.SelectedIndex];
                break;
            case "WidgetCompactMediaCornerMode":
                ViewModel.SelectedWidgetCompactMediaCornerMode = ViewModel.AvailableWidgetCompactMediaCornerModes[combo.SelectedIndex];
                break;
            case "LayoutDensity":
                ViewModel.SelectedLayoutDensity = ViewModel.AvailableLayoutDensities[combo.SelectedIndex];
                break;
            case "AnimationPreset":
                ViewModel.SelectedAnimationPreset = ViewModel.AvailableAnimationPresets[combo.SelectedIndex];
                break;
            case "WidgetAnimationEffect":
                ViewModel.SelectedWidgetAnimationEffect = ViewModel.AvailableWidgetAnimationEffects[combo.SelectedIndex];
                break;
            case "WidgetAnimationSpeed":
                ViewModel.SelectedWidgetAnimationSpeed = ViewModel.AvailableWidgetAnimationSpeeds[combo.SelectedIndex];
                break;
            case "WidgetAnimationSlideDirection":
                ViewModel.SelectedWidgetAnimationSlideDirection = ViewModel.AvailableWidgetAnimationSlideDirections[combo.SelectedIndex];
                break;
            case "WidgetAnimationEasingIntensity":
                ViewModel.SelectedWidgetAnimationEasingIntensity = ViewModel.AvailableWidgetAnimationEasingIntensities[combo.SelectedIndex];
                break;
            case "DisplayWidgetChromeMode":
                ViewModel.SelectedDisplayWidgetChromeMode = ViewModel.AvailableDisplayWidgetChromeModes[combo.SelectedIndex];
                break;
            case "InteractiveWidgetChromeMode":
                ViewModel.SelectedInteractiveWidgetChromeMode = ViewModel.AvailableInteractiveWidgetChromeModes[combo.SelectedIndex];
                break;
            case "WidgetTitleIconMode":
                ViewModel.SelectedWidgetTitleIconMode = ViewModel.AvailableWidgetTitleIconModes[combo.SelectedIndex];
                break;
            case "WidgetLayerMode":
                if (!_isSettingsRootLoaded)
                {
                    return;
                }

                ViewModel.SelectedWidgetLayerMode = ViewModel.AvailableWidgetLayerModes[combo.SelectedIndex];
                break;
            case "QuickCaptureDefaultView":
                ViewModel.SelectedQuickCaptureDefaultView = ViewModel.AvailableQuickCaptureDefaultViews[combo.SelectedIndex];
                break;
            case "QuickCaptureTabStyle":
                ViewModel.SelectedQuickCaptureTabStyle = ViewModel.AvailableWidgetTabStyles[combo.SelectedIndex];
                break;
            case "QuickCapturePreviewLines":
                ViewModel.QuickCaptureItemPreviewLineCount = ViewModel.AvailableItemPreviewLineCounts[combo.SelectedIndex];
                break;
            case "QuickCaptureEnterBehavior":
                ViewModel.QuickCaptureEditorEnterBehavior = ViewModel.AvailableEditorEnterBehaviors[combo.SelectedIndex];
                break;
            case "TodoNewTaskPosition":
                ViewModel.SelectedTodoNewTaskPosition = ViewModel.AvailableTodoNewTaskPositions[combo.SelectedIndex];
                break;
            case "AttachmentStorageMode":
                ViewModel.SelectedAttachmentStorageMode = ViewModel.AvailableAttachmentStorageModes[combo.SelectedIndex];
                break;
            case "TodoDefaultFilter":
                ViewModel.SelectedTodoDefaultFilter = ViewModel.AvailableTodoDefaultFilters[combo.SelectedIndex];
                break;
            case "TodoTabStyle":
                ViewModel.SelectedTodoTabStyle = ViewModel.AvailableWidgetTabStyles[combo.SelectedIndex];
                break;
            case "TodoPreviewLines":
                ViewModel.TodoItemPreviewLineCount = ViewModel.AvailableItemPreviewLineCounts[combo.SelectedIndex];
                break;
            case "TodoEnterBehavior":
                ViewModel.TodoEditorEnterBehavior = ViewModel.AvailableEditorEnterBehaviors[combo.SelectedIndex];
                break;
            case "TodoReminderOffset":
                ViewModel.SelectedTodoReminderOffsetMinutes = ViewModel.AvailableTodoReminderOffsetMinutes[combo.SelectedIndex];
                break;
case "WeatherTemperatureUnit":
ViewModel.SelectedWeatherTemperatureUnit = ViewModel.AvailableWeatherTemperatureUnits[combo.SelectedIndex];
break;
case "WeatherWindSpeedUnit":
ViewModel.SelectedWeatherWindSpeedUnit = ViewModel.AvailableWeatherWindSpeedUnits[combo.SelectedIndex];
break;
case "WeatherDefaultView":
ViewModel.SelectedWeatherDefaultView = ViewModel.AvailableWeatherDefaultViews[combo.SelectedIndex];
break;
case "WeatherSkin":
ViewModel.SelectedWeatherSkin = ViewModel.AvailableWeatherSkins[combo.SelectedIndex];
break;
case "WeatherRefreshInterval":
ViewModel.SelectedWeatherRefreshInterval = ViewModel.AvailableWeatherRefreshIntervals[combo.SelectedIndex];
break;
            case "TrayIconStyle":
                ViewModel.SelectedTrayIconStyle = ViewModel.AvailableTrayIconStyles[combo.SelectedIndex];
                break;
        }
    }

    private void NestedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sectionTag })
        {
            NavigateToSettingsSection(sectionTag);
        }
    }

    private void AddFileStackRuleButton_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.AddFileStackCustomRule();
    }

    private void RemoveFileStackRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FileStackCustomRuleEditor editor })
        {
            ViewModel.RemoveFileStackCustomRule(editor);
        }
    }

    private void MoveFileStackRuleUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FileStackCustomRuleEditor editor })
        {
            ViewModel.MoveFileStackCustomRule(editor, -1);
        }
    }

    private void MoveFileStackRuleDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FileStackCustomRuleEditor editor })
        {
            ViewModel.MoveFileStackCustomRule(editor, 1);
        }
    }

    private void FileStackRulesListView_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        ViewModel.CommitFileStackCustomRuleOrder();
    }

    private void SettingsDropDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button || button.Tag is not string menuKind)
        {
            return;
        }

        string selectedValue;
        IReadOnlyList<string> values;
        Action<string> applyValue;
        Func<string, string> displayValue;

        switch (menuKind)
        {
            case "Theme":
                selectedValue = ViewModel.SelectedTheme;
                values = ViewModel.AvailableThemes;
                applyValue = value => ViewModel.SelectedTheme = value;
                displayValue = ViewModel.GetThemeDisplayName;
                break;

            case "Language":
                selectedValue = ViewModel.SelectedLanguage;
                values = ViewModel.AvailableLanguages;
                applyValue = value => ViewModel.SelectedLanguage = value;
                displayValue = ViewModel.GetLanguageDisplayName;
                break;

            case "WidgetCorner":
                selectedValue = ViewModel.SelectedWidgetCornerPreference;
                values = ViewModel.AvailableWidgetCornerPreferences;
                applyValue = value => ViewModel.SelectedWidgetCornerPreference = value;
                displayValue = ViewModel.GetCornerDisplayName;
                break;

            case "WidgetAnimationEffect":
                selectedValue = ViewModel.SelectedWidgetAnimationEffect;
                values = ViewModel.AvailableWidgetAnimationEffects;
                applyValue = value => ViewModel.SelectedWidgetAnimationEffect = value;
                displayValue = ViewModel.GetWidgetAnimationEffectDisplayName;
                break;

            case "WidgetAnimationSpeed":
                selectedValue = ViewModel.SelectedWidgetAnimationSpeed;
                values = ViewModel.AvailableWidgetAnimationSpeeds;
                applyValue = value => ViewModel.SelectedWidgetAnimationSpeed = value;
                displayValue = ViewModel.GetWidgetAnimationSpeedDisplayName;
                break;

            case "WidgetAnimationSlideDirection":
                selectedValue = ViewModel.SelectedWidgetAnimationSlideDirection;
                values = ViewModel.AvailableWidgetAnimationSlideDirections;
                applyValue = value => ViewModel.SelectedWidgetAnimationSlideDirection = value;
                displayValue = ViewModel.GetWidgetAnimationSlideDirectionDisplayName;
                break;

            case "WidgetAnimationEasingIntensity":
                selectedValue = ViewModel.SelectedWidgetAnimationEasingIntensity;
                values = ViewModel.AvailableWidgetAnimationEasingIntensities;
                applyValue = value => ViewModel.SelectedWidgetAnimationEasingIntensity = value;
                displayValue = ViewModel.GetWidgetAnimationEasingIntensityDisplayName;
                break;

            default:
                return;
        }

        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };

        foreach (string value in values)
        {
            var item = new MenuFlyoutItem
            {
                Text = displayValue(value),
                MinWidth = button.ActualWidth > 0 ? button.ActualWidth : button.MinWidth,
                Icon = string.Equals(value, selectedValue, StringComparison.Ordinal)
                    ? new FontIcon { Glyph = "\uE73E" }
                    : null
            };
            item.Click += (_, _) => applyValue(value);
            flyout.Items.Add(item);
        }

        flyout.ShowAt(button);
    }

    private void HoverButtonActionsDropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        double flyoutWidth = Math.Max(220, Math.Max(button.ActualWidth, button.MinWidth));
        var panel = new StackPanel
        {
            Width = flyoutWidth,
            Padding = new Thickness(6),
            Spacing = 2
        };

        foreach (string action in ViewModel.AvailableWidgetHoverButtonActions)
        {
            panel.Children.Add(CreateHoverButtonActionFlyoutRow(action, flyoutWidth));
        }

        var flyout = new Flyout
        {
            Content = panel,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = false,
            FlyoutPresenterStyle = new Style(typeof(FlyoutPresenter))
            {
                Setters =
                {
                    new Setter(Control.PaddingProperty, new Thickness(0)),
                    new Setter(FrameworkElement.MinWidthProperty, flyoutWidth)
                }
            }
        };
        flyout.ShowAt(button);
    }

    private Button CreateHoverButtonActionFlyoutRow(string action, double flyoutWidth)
    {
        var checkIcon = new FontIcon
        {
            Width = 18,
            FontSize = 13,
            Glyph = "\uE73E",
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = ViewModel.IsHoverButtonActionSelected(action)
                ? Visibility.Visible
                : Visibility.Collapsed
        };
        var textBlock = new TextBlock
        {
            Text = ViewModel.GetHoverButtonActionDisplayName(action),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        var rowContent = new Grid
        {
            ColumnSpacing = 8
        };
        rowContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        rowContent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(checkIcon, 0);
        Grid.SetColumn(textBlock, 1);
        rowContent.Children.Add(checkIcon);
        rowContent.Children.Add(textBlock);

        var rowButton = new Button
        {
            Tag = action,
            Content = rowContent,
            Width = flyoutWidth - 12,
            MinHeight = 34,
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.Resources["SubtleButtonStyle"],
            Opacity = ViewModel.CanToggleHoverButtonAction(action) ? 1.0 : 0.48
        };
        rowButton.Click += (_, _) =>
        {
            if (!ViewModel.CanToggleHoverButtonAction(action))
            {
                return;
            }

            ViewModel.ToggleHoverButtonAction(action);
            RefreshHoverButtonActionsFlyout((StackPanel)rowButton.Parent);
        };
        return rowButton;
    }

    private void RefreshHoverButtonActionsFlyout(StackPanel panel)
    {
        foreach (var child in panel.Children.OfType<Button>())
        {
            if (child.Tag is not string action)
            {
                continue;
            }

            child.Opacity = ViewModel.CanToggleHoverButtonAction(action) ? 1.0 : 0.48;
            if (child.Content is Grid rowContent &&
                rowContent.Children.OfType<FontIcon>().FirstOrDefault() is FontIcon checkIcon)
            {
                checkIcon.Visibility = ViewModel.IsHoverButtonActionSelected(action)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }
    }
}
