using DeskBox.Controls;
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
using CommunityToolkit.WinUI.Controls;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private Storyboard? _settingsSearchHighlightStoryboard;
    private FrameworkElement? _settingsSearchHighlightTarget;
    private double _settingsSearchHighlightOriginalOpacity = 1;
    private readonly List<SettingsExpander> _featureSettingsExpanders = [];
    private readonly Dictionary<SettingsExpander, long> _featureSettingsExpanderCallbacks = [];
    private bool _isSynchronizingFeatureSettingsExpanders;

    private void InitializeSettingsSectionElements()
    {
        _settingsSectionElements = new Dictionary<string, FrameworkElement>(StringComparer.Ordinal)
        {
            ["General"] = GeneralSection,
            ["PerformanceSettings"] = PerformanceSettingsSection,
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
            ["WidgetGroups"] = WidgetGroupsSection,
            ["AppearanceDetail"] = AppearanceDetailSection,
            ["FileDisplaySettings"] = FileDisplaySettingsSection,
            ["FileStorageSettings"] = FileStorageSettingsSection,
            ["FileStackSettings"] = FileStackSettingsSection,
            ["DesktopOrganizationSettings"] = DesktopOrganizationSettingsSection,
            ["FeatureWidgets"] = FeatureWidgetsSection,
            ["QuickCaptureSettings"] = QuickCaptureSettingsSection,
            ["TodoSettings"] = TodoSettingsSection,
            ["MusicSettings"] = MusicSettingsSection,
            ["WeatherSettings"] = WeatherSettingsSection,
            ["GlanceSettings"] = GlanceSettingsSection,
            ["SearchSettings"] = SearchSettingsSection,
            ["CommandApiSettings"] = CommandApiSettingsSection,
            ["Interaction"] = InteractionSection,
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
            // NativeAOT cannot project an empty array of this private managed
            // suggestion type through the WinRT object-valued ItemsSource ABI.
            // Null is the native empty-state contract and is behaviorally
            // identical while avoiding construction-time E_INVALIDARG.
            SettingsSearchBox.ItemsSource = null;
            SettingsSearchBox.IsSuggestionListOpen = false;
            return;
        }

        SettingsSearchResult[] matches = FindSettingsSearchMatches(normalizedQuery, 10);
        SettingsSearchBox.ItemsSource = matches.Cast<object>().ToArray();
        SettingsSearchBox.IsSuggestionListOpen = matches.Length > 0;
    }

    private IEnumerable<SettingsSearchResult> CreateSettingItemSearchResults()
    {
        foreach ((string sectionTag, FrameworkElement section) in _settingsSectionElements)
        {
            string destinationTag = sectionTag switch
            {
                "FileStorageSettings" => "AppearanceDetail",
                "InteractionWindowSettings" => "Interaction",
                "ResetSettings" => "Maintenance",
                _ => sectionTag
            };
            if (!TryGetSectionRoute(
                    destinationTag,
                    out SettingsSectionRoute route))
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
                    destinationTag,
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

        ActivateSettingsSearchResult(result, sender);
    }

    private void ActivateSettingsSearchResult(
        SettingsSearchResult result,
        AutoSuggestBox sender)
    {
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

    public void ShowGlanceSection(string widgetId)
    {
        GlanceSettingsSection.SelectWidget(widgetId);
        NavigateToSettingsSection("GlanceSettings");
    }

    public void RefreshUpdateStateFromService()
    {
        ViewModel.RefreshCachedUpdateState();
    }

    private NavigationViewItem? FindNavItemByTag(string tag)
    {
        foreach (var item in SettingsNavigationView.MenuItems)
        {
            if (item is NavigationViewItem navItem &&
                FindNavItemByTag(navItem, tag) is { } match)
            {
                return match;
            }
        }
        return null;
    }

    private static NavigationViewItem? FindNavItemByTag(
        NavigationViewItem item,
        string tag)
    {
        if (item.Tag is string itemTag &&
            string.Equals(itemTag, tag, StringComparison.Ordinal))
        {
            return item;
        }

        foreach (object child in item.MenuItems)
        {
            if (child is NavigationViewItem childItem &&
                FindNavItemByTag(childItem, tag) is { } match)
            {
                return match;
            }
        }

        return null;
    }

    private void NavigateToSettingsSection(string sectionTag)
    {
        sectionTag = NormalizeSettingsSectionTag(sectionTag);

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
        sectionTag = NormalizeSettingsSectionTag(sectionTag);

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
            bool isPrimarySection = string.Equals(
                tag,
                visibleSectionTag,
                StringComparison.Ordinal);
            bool isInlineSection = sectionTag switch
            {
                "AppearanceDetail" => tag == "FileStorageSettings",
                "Interaction" or "Advanced" => tag == "InteractionWindowSettings",
                "Maintenance" => tag == "ResetSettings",
                _ => false
            };
            sectionElement.Visibility = isPrimarySection || isInlineSection
                ? Visibility.Visible : Visibility.Collapsed;
        }

        if (sectionTag == "FileStackSettings")
        {
            _ = ViewModel.RefreshFileStackRulePreviewFromDiskAsync();
        }
        if (sectionTag == "DesktopOrganizationSettings")
        {
            DesktopOrganizationSettingsSection.Refresh();
        }
        if (sectionTag == "WidgetGroups")
        {
            ViewModel.RefreshWidgetGroupSettings();
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
        if (sectionTag == "CommandApiSettings")
        {
            CommandApiSettingsSection.RefreshFromSettings();
        }
        if (sectionTag == "GlanceSettings")
        {
            _ = GlanceSettingsSection.RefreshFromStoreAsync();
        }
        if (sectionTag == "ManagedStorage")
        {
            RefreshManagedStorageFolderList();
        }
        else if (sectionTag == "AppearanceDetail")
        {
            _ = ViewModel.RefreshQuickAccessStateAsync();
        }
        if (sectionTag == "CompatibilityDiagnosticsSettings")
        {
            ViewModel.RefreshDragDropPermissionDiagnostic();
            ViewModel.RefreshRuntimeDiagnostics();
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

        SettingsBreadcrumbBar.ItemsSource = new object[]
        {
            new SettingsBreadcrumbItem(parentRoute.Tag, _localizationService.T(parentRoute.TitleKey), 0.62),
            new SettingsBreadcrumbItem(route.Tag, _localizationService.T(route.TitleKey), 1.0)
        };
        SettingsBreadcrumbHost.Visibility = Visibility.Visible;
        SettingsBreadcrumbBar.Visibility = Visibility.Visible;
    }

    private void SettingsBreadcrumbBar_ItemClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs args)
    {
        if (args.Item is SettingsBreadcrumbItem item)
        {
            NavigateFromSettingsBreadcrumbItem(item);
        }
    }

    private void NavigateFromSettingsBreadcrumbItem(SettingsBreadcrumbItem item)
    {
        if (string.Equals(item.SectionTag, _currentSettingsSection, StringComparison.Ordinal))
        {
            return;
        }

        NavigateToSettingsSection(item.SectionTag);
    }

    private static bool TryGetSectionRoute(string sectionTag, out SettingsSectionRoute route)
    {
        return SectionRoutes.TryGetValue(sectionTag, out route!);
    }

    private static string NormalizeSettingsSectionTag(string sectionTag)
    {
        return sectionTag switch
        {
            "FileStorageSettings" => "AppearanceDetail",
            "InteractionWindowSettings" => "Interaction",
            "ResetSettings" => "Maintenance",
            _ => sectionTag
        };
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

    private void ResetWidgetGroupOverrideButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string groupId })
        {
            ViewModel.ResetWidgetGroupOverrides(groupId);
        }
    }

    private void WidgetGroupNameTextBox_LostFocus(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: string groupId } textBox)
        {
            ViewModel.RenameWidgetGroup(groupId, textBox.Text);
        }
    }

    private void WidgetGroupNavigationComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ApplyWidgetGroupOption(
            sender,
            ViewModel.SetWidgetGroupNavigationStyle);

    private void WidgetGroupTitleComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ApplyWidgetGroupOption(
            sender,
            ViewModel.SetWidgetGroupTitleDisplayMode);

    private void WidgetGroupChromeComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ApplyWidgetGroupOption(
            sender,
            ViewModel.SetWidgetGroupChromeMode);

    private void WidgetGroupCollapseComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ApplyWidgetGroupOption(
            sender,
            ViewModel.SetWidgetGroupCollapseBehavior);

    private void WidgetGroupWheelComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ApplyWidgetGroupOption(
            sender,
            ViewModel.SetWidgetGroupWheelSetting);

    private void WidgetGroupHoverComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e) =>
        ApplyWidgetGroupOption(
            sender,
            ViewModel.SetWidgetGroupHoverSetting);

    private static void ApplyWidgetGroupOption(
        object sender,
        Func<string, string?, bool> apply)
    {
        if (sender is ComboBox
            {
                Tag: string groupId,
                SelectedItem: SettingsOption option
            })
        {
            apply(groupId, option.Value?.ToString());
        }
    }

    private async void MoveWidgetGroupMemberUpButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button
            {
                Tag: WidgetGroupMemberSettingsItem
                {
                    MoveUpTargetWidgetId: string targetId
                } member
            } ||
            App.Current.WidgetManager is not { } manager)
        {
            return;
        }

        if (await manager.ReorderWidgetGroupMemberAsync(
                member.WidgetId,
                targetId))
        {
            ViewModel.RefreshWidgetGroupSettings();
        }
    }

    private async void MoveWidgetGroupMemberDownButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button
            {
                Tag: WidgetGroupMemberSettingsItem
                {
                    MoveDownTargetWidgetId: string targetId
                } member
            } ||
            App.Current.WidgetManager is not { } manager)
        {
            return;
        }

        if (await manager.ReorderWidgetGroupMemberAsync(
                member.WidgetId,
                targetId))
        {
            ViewModel.RefreshWidgetGroupSettings();
        }
    }

    private async void RemoveWidgetGroupMemberButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button
            {
                Tag: WidgetGroupMemberSettingsItem member
            } ||
            App.Current.WidgetManager is not { } manager)
        {
            return;
        }

        if (await manager.RemoveWidgetFromGroupAsync(
                member.WidgetId,
                revealStandalone: true))
        {
            ViewModel.RefreshWidgetGroupSettings();
        }
    }

    private async void DissolveWidgetGroupButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string memberId } ||
            App.Current.WidgetManager is not { } manager ||
            SettingsRoot.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = _localizationService.T(
                "Settings.WidgetGroups.DissolveDialog.Title"),
            PrimaryButtonText = _localizationService.T(
                "Settings.WidgetGroups.DissolveDialog.Confirm"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.T(
                    "Settings.WidgetGroups.DissolveDialog.Description"),
                TextWrapping = TextWrapping.Wrap
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (await manager.DissolveWidgetGroupContainingAsync(memberId))
        {
            ViewModel.RefreshWidgetGroupSettings();
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

    private void FeatureSettingsExpander_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not SettingsExpander expander ||
            _featureSettingsExpanderCallbacks.ContainsKey(expander))
        {
            return;
        }

        _featureSettingsExpanders.Add(expander);
        long callback = expander.RegisterPropertyChangedCallback(
            SettingsExpander.IsExpandedProperty,
            (dependencyObject, _) =>
            {
                if (_isSynchronizingFeatureSettingsExpanders ||
                    dependencyObject is not SettingsExpander current ||
                    !current.IsExpanded ||
                    current.Tag is not string groupTag)
                {
                    return;
                }

                _isSynchronizingFeatureSettingsExpanders = true;
                try
                {
                    foreach (SettingsExpander peer in _featureSettingsExpanders)
                    {
                        if (!ReferenceEquals(peer, current) &&
                            peer.IsExpanded &&
                            peer.Tag is string peerTag &&
                            string.Equals(peerTag, groupTag, StringComparison.Ordinal))
                        {
                            peer.IsExpanded = false;
                        }
                    }
                }
                finally
                {
                    _isSynchronizingFeatureSettingsExpanders = false;
                }
            });
        _featureSettingsExpanderCallbacks.Add(expander, callback);
    }

    private void FeatureSettingsEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { IsOn: false, Tag: string groupTag })
        {
            return;
        }

        foreach (SettingsExpander expander in _featureSettingsExpanders)
        {
            if (expander.Tag is string expanderGroup &&
                string.Equals(expanderGroup, groupTag, StringComparison.Ordinal))
            {
                expander.IsExpanded = false;
            }
        }
    }

    private void QuickCaptureTabsDropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        SettingsMultiSelectMenu.Show(
            button,
            ViewModel.AvailableQuickCaptureDefaultViews,
            ViewModel.GetQuickCaptureTabDisplayName,
            ViewModel.IsQuickCaptureTabSelected,
            ViewModel.CanToggleQuickCaptureTab,
            ViewModel.ToggleQuickCaptureTab);
    }

    private void TodoTabsDropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        SettingsMultiSelectMenu.Show(
            button,
            ViewModel.AvailableTodoDefaultFilters,
            ViewModel.GetTodoTabDisplayName,
            ViewModel.IsTodoTabSelected,
            ViewModel.CanToggleTodoTab,
            ViewModel.ToggleTodoTab);
    }

    private void TodoFooterDisplayDropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        SettingsMultiSelectMenu.Show(
            button,
            ["Stats", "ClearCompleted"],
            ViewModel.GetTodoFooterDisplayOptionName,
            ViewModel.IsTodoFooterDisplayOptionSelected,
            _ => true,
            ViewModel.ToggleTodoFooterDisplayOption);
    }

    private void WeatherDisplayOptionsDropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        SettingsMultiSelectMenu.Show(
            button,
            ViewModel.AvailableWeatherDisplayOptions,
            ViewModel.GetWeatherDisplayOptionName,
            ViewModel.IsWeatherDisplayOptionSelected,
            _ => true,
            ViewModel.ToggleWeatherDisplayOption);
    }

    private void ContinuousDecorativeAnimationsDropDown_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        SettingsMultiSelectMenu.Show(
            button,
            ViewModel.AvailableContinuousDecorativeAnimationOptions,
            ViewModel.GetContinuousDecorativeAnimationDisplayName,
            ViewModel.IsContinuousDecorativeAnimationSelected,
            _ => true,
            ViewModel.ToggleContinuousDecorativeAnimation);
    }

    private void HoverButtonActionsDropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        double flyoutWidth = Math.Max(220, Math.Max(button.ActualWidth, button.MinWidth));
        var flyout = new MenuFlyout
        {
            ShouldConstrainToRootBounds = false
        };

        const string noneAction = "__None";
        var noneItem = new ToggleMenuFlyoutItem
        {
            Tag = noneAction,
            Text = _localizationService.T("Settings.HoverButtonActions.None"),
            IsChecked = !ViewModel.ShowHoverButtons,
            MinWidth = flyoutWidth
        };
        noneItem.Click += (_, _) =>
        {
            ViewModel.ShowHoverButtons = false;
            RefreshHoverButtonActionsMenu(flyout);
        };
        flyout.Items.Add(noneItem);
        flyout.Items.Add(new MenuFlyoutSeparator());

        foreach (string action in ViewModel.AvailableWidgetHoverButtonActions)
        {
            var item = new ToggleMenuFlyoutItem
            {
                Tag = action,
                Text = ViewModel.GetHoverButtonActionDisplayName(action),
                IsChecked = ViewModel.IsHoverButtonActionSelected(action),
                IsEnabled = ViewModel.CanToggleHoverButtonAction(action),
                MinWidth = flyoutWidth
            };
            item.Click += (_, _) =>
            {
                ViewModel.ToggleHoverButtonAction(action);
                ViewModel.ShowHoverButtons = true;
                item.IsChecked = ViewModel.IsHoverButtonActionSelected(action);
                RefreshHoverButtonActionsMenu(flyout);
            };
            flyout.Items.Add(item);
        }

        flyout.ShowAt(button);
    }

    private void RefreshHoverButtonActionsMenu(MenuFlyout flyout)
    {
        foreach (var item in flyout.Items.OfType<ToggleMenuFlyoutItem>())
        {
            if (item.Tag is not string action)
            {
                continue;
            }

            if (string.Equals(action, "__None", StringComparison.Ordinal))
            {
                item.IsChecked = !ViewModel.ShowHoverButtons;
                item.IsEnabled = true;
                continue;
            }

            item.IsChecked = ViewModel.ShowHoverButtons &&
                ViewModel.IsHoverButtonActionSelected(action);
            item.IsEnabled = ViewModel.CanToggleHoverButtonAction(action);
        }
    }
}
