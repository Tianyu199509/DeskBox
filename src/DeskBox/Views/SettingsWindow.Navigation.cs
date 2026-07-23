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
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT.Interop;
using DeskBox.Views.SettingsSections;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private Storyboard? _settingsSearchHighlightStoryboard;
    private FrameworkElement? _settingsSearchHighlightTarget;
    private double _settingsSearchHighlightOriginalOpacity = 1;

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
            ["CapsuleArrangementSettings"] = CapsuleArrangementSettingsSection,
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
            ["SearchSettings"] = SearchSettingsSection,
            ["Interaction"] = InteractionSection,
            ["InteractionHotkeySettings"] = InteractionHotkeySettingsSection,
            ["InteractionHoverSettings"] = InteractionHoverSettingsSection,
            ["InteractionWindowSettings"] = InteractionWindowSettingsSection,
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
        var results = new List<SettingsSearchResult>();
        foreach (SettingsSectionRoute route in SectionRoutes.Values.Where(route => route.Tag != "Advanced"))
        {
            string title = _localizationService.T(route.TitleKey);
            results.Add(new SettingsSearchResult(
                route.Tag,
                title,
                BuildSettingsRouteBreadcrumb(route),
                string.Empty,
                null));
        }

        if (_isSettingsRootLoaded)
        {
            results.AddRange(CreateSettingItemSearchResults());
        }

        _settingsSearchResults = results;

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

        SettingsSearchResult[] matches = FindSettingsSearchMatches(normalizedQuery, 10);
        SettingsSearchBox.ItemsSource = matches;
        SettingsSearchBox.IsSuggestionListOpen = matches.Length > 0;
    }

    private IEnumerable<SettingsSearchResult> CreateSettingItemSearchResults()
    {
        foreach ((string sectionTag, FrameworkElement section) in _settingsSectionElements)
        {
            if (!TryGetSectionRoute(sectionTag, out SettingsSectionRoute route))
            {
                continue;
            }

            var indexedHeaderKeys = new HashSet<string>(StringComparer.Ordinal);
            string breadcrumb = BuildSettingsRouteBreadcrumb(route);
            foreach (FrameworkElement element in FindDescendants<FrameworkElement>(section))
            {
                string? headerKey = Localized.GetHeaderKey(element);
                if (string.IsNullOrWhiteSpace(headerKey) ||
                    string.Equals(headerKey, route.TitleKey, StringComparison.Ordinal) ||
                    !indexedHeaderKeys.Add(headerKey))
                {
                    continue;
                }

                string? descriptionKey = Localized.GetDescriptionKey(element);
                yield return new SettingsSearchResult(
                    sectionTag,
                    _localizationService.T(headerKey),
                    breadcrumb,
                    string.IsNullOrWhiteSpace(descriptionKey)
                        ? string.Empty
                        : _localizationService.T(descriptionKey),
                    element);
            }
        }
    }

    private string BuildSettingsRouteBreadcrumb(SettingsSectionRoute route)
    {
        var titles = new Stack<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        SettingsSectionRoute? current = route;
        while (current is not null && visited.Add(current.Tag))
        {
            titles.Push(_localizationService.T(current.TitleKey));
            current = current.ParentTag is not null && TryGetSectionRoute(current.ParentTag, out var parent)
                ? parent
                : null;
        }

        return string.Join(" / ", titles);
    }

    private SettingsSearchResult[] FindSettingsSearchMatches(string query, int limit)
    {
        return _settingsSearchResults
            .Select(result => new
            {
                Result = result,
                Score = SettingsSearchMatcher.GetScore(
                    query,
                    result.Title,
                    result.Breadcrumb,
                    result.Description)
            })
            .Where(match => match.Score != SettingsSearchMatcher.NoMatch)
            .OrderBy(match => match.Score)
            .ThenBy(match => match.Result.IsPage ? 0 : 1)
            .ThenBy(match => match.Result.Title.Length)
            .Take(limit)
            .Select(match => match.Result)
            .ToArray();
    }

    private void SettingsSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            UpdateSettingsSearchSuggestions(sender.Text);
        }
    }

    private void SettingsSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        // Dim the search icon when typing
        SettingsSearchIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)
            Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorTertiaryBrush"];
    }

    private void SettingsSearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Restore icon color
        SettingsSearchIcon.Foreground = (Microsoft.UI.Xaml.Media.Brush)
            Microsoft.UI.Xaml.Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    private void SettingsSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        SettingsSearchResult? result = args.ChosenSuggestion as SettingsSearchResult;
        if (result is null)
        {
            string query = sender.Text.Trim();
            result = FindSettingsSearchMatches(query, 1).FirstOrDefault();
        }

        if (result is null)
        {
            return;
        }

        NavigateToSettingsSection(result.SectionTag);
        ScheduleSettingsSearchTarget(result);
        sender.Text = string.Empty;
        UpdateSettingsSearchSuggestions(string.Empty);
    }

    private void ScheduleSettingsSearchTarget(SettingsSearchResult result)
    {
        if (result.TargetElement is not FrameworkElement target)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_isClosed || target.XamlRoot is null)
            {
                return;
            }

            ExpandSettingsSearchTargetAncestors(target);
            target.UpdateLayout();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_isClosed || target.XamlRoot is null)
                {
                    return;
                }

                target.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = true,
                    VerticalAlignmentRatio = 0.18
                });
                HighlightSettingsSearchTarget(target);
                FocusSettingsSearchTarget(target);
            });
        });
    }

    private static void ExpandSettingsSearchTargetAncestors(DependencyObject target)
    {
        DependencyObject? current = target;
        while (current is not null)
        {
            if (current is CommunityToolkit.WinUI.Controls.SettingsExpander expander)
            {
                expander.IsExpanded = true;
            }

            current = VisualTreeHelper.GetParent(current);
        }
    }

    private static void FocusSettingsSearchTarget(FrameworkElement target)
    {
        Control? focusTarget = FindDescendants<Control>(target)
            .FirstOrDefault(control =>
                control.IsEnabled &&
                control.IsTabStop &&
                control.Visibility == Visibility.Visible);
        if (focusTarget is null &&
            target is Control targetControl &&
            targetControl.IsEnabled &&
            targetControl.IsTabStop)
        {
            focusTarget = targetControl;
        }

        focusTarget?.Focus(FocusState.Programmatic);
    }

    private void HighlightSettingsSearchTarget(FrameworkElement target)
    {
        ClearSettingsSearchHighlight();

        _settingsSearchHighlightTarget = target;
        _settingsSearchHighlightOriginalOpacity = target.Opacity;
        target.Opacity = Math.Min(0.68, _settingsSearchHighlightOriginalOpacity);

        var animation = new DoubleAnimation
        {
            From = target.Opacity,
            To = _settingsSearchHighlightOriginalOpacity,
            Duration = TimeSpan.FromMilliseconds(650),
            EnableDependentAnimation = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));

        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_settingsSearchHighlightStoryboard, storyboard))
            {
                return;
            }

            target.Opacity = _settingsSearchHighlightOriginalOpacity;
            _settingsSearchHighlightStoryboard = null;
            _settingsSearchHighlightTarget = null;
        };
        _settingsSearchHighlightStoryboard = storyboard;
        storyboard.Begin();
    }

    private void ClearSettingsSearchHighlight()
    {
        _settingsSearchHighlightStoryboard?.Stop();
        if (_settingsSearchHighlightTarget is not null)
        {
            _settingsSearchHighlightTarget.Opacity = _settingsSearchHighlightOriginalOpacity;
        }

        _settingsSearchHighlightStoryboard = null;
        _settingsSearchHighlightTarget = null;
        _settingsSearchHighlightOriginalOpacity = 1;
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
        if (sectionTag == "SearchSettings")
        {
            SearchSettingsSection.RefreshFromSettings();
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
        if (sectionTag == "BackupRestoreSettings")
        {
            _ = RefreshBackupSnapshotInventoryAsync();
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
            SettingsBreadcrumbHost.Visibility = Visibility.Collapsed;
            SettingsBreadcrumbBar.Visibility = Visibility.Collapsed;
            SettingsBreadcrumbBar.ItemsSource = null;
            return;
        }

        SettingsBreadcrumbBar.ItemsSource = new[]
        {
            new SettingsBreadcrumbItem(parentRoute.Tag, _localizationService.T(parentRoute.TitleKey), 0.62),
            new SettingsBreadcrumbItem(route.Tag, _localizationService.T(route.TitleKey), 1.0)
        };
        SettingsBreadcrumbHost.Visibility = Visibility.Visible;
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

    private void NestedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string sectionTag })
        {
            NavigateToSettingsSection(sectionTag);
        }
    }

    private void SettingsSection_NavigationRequested(
        object? sender,
        SettingsSectionNavigationRequestedEventArgs e)
    {
        NavigateToSettingsSection(e.SectionTag);
    }

    private void ResetCapsuleWidgetOverrideButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string widgetId })
        {
            ViewModel.ResetCapsuleOverridesForWidget(widgetId);
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
