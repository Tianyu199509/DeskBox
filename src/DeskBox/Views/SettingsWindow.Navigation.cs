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
    // Sentinel tag for the single-line "no results" suggestion row; it must
    // never reach navigation.
    private const string SettingsSearchNoResultsTag = "__settings-search-no-results";

    private Storyboard? _settingsSearchHighlightStoryboard;
    private EventHandler<object>? _settingsSearchHighlightCompletedHandler;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _settingsSearchHighlightTimer;
    private Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? _settingsSearchHighlightTimerTickHandler;
    private FrameworkElement? _settingsSearchHighlightTarget;
    private double _settingsSearchHighlightOriginalOpacity = 1;
    private object? _settingsSearchHighlightOriginalBorderBrush = DependencyProperty.UnsetValue;
    private object? _settingsSearchHighlightOriginalBorderThickness = DependencyProperty.UnsetValue;
    private readonly List<SettingsExpander> _featureSettingsExpanders = [];
    private readonly Dictionary<SettingsExpander, long> _featureSettingsExpanderCallbacks = [];
    private bool _isSynchronizingFeatureSettingsExpanders;
    private enum MaintenanceSubPageKind
    {
        None,
        AutomaticBackup,
        CloudBackup
    }

    private bool _isSyncingFeatureWidgetsHubTab;
    private bool _isCapsuleModeSubPageOpen;
    private bool _isSyncingCapsuleModeEntry;
    private IReadOnlyList<SettingsOption>? _capsuleEntryOptions;
    private MaintenanceSubPageKind _activeMaintenanceSubPage;
    private bool _isSyncingMaintenanceEntries;
    private string? _activeWidgetGroupId;
    private List<string>? _navigationTagOrder;
    private readonly HashSet<InfoBar> _pinnedNoticeInfoBars = [];

    private const float NavigationEnterOffsetPx = 40f;

    /// <summary>
    /// Drill-down order of the sub-pages that share one navigation entry
    /// (same parent NavTag): the order their entry cards appear in the
    /// owning template. Same-depth sibling navigation uses this table so
    /// switching between children of one nav item slides directionally
    /// instead of always entering from the left.
    /// </summary>
    private static readonly string[] SubSectionTagOrder =
    [
        // General family
        "AiSettings",
        // Interaction family
        "WidgetGroups", "Displays",
        // AppearanceDetail family
        "FileStackSettings",
        // FeatureWidgets family
        "QuickCaptureSettings", "TodoSettings", "MusicSettings",
        "WeatherSettings", "GlanceSettings", "ClipboardSettings"
    ];

    private void InitializeSettingsSectionElements()
    {
        _settingsSectionElements.Add("General", GeneralSection);
        string[] missingRoutes = SectionRoutes.Keys
            .Where(tag => tag != "General" &&
                !ContentHost.Resources.ContainsKey(tag + "SectionTemplate"))
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

    private void SettingsNavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        if (_isSyncingNavigationSelection ||
            args.InvokedItemContainer is not NavigationViewItem { Tag: string invokedTag })
        {
            return;
        }

        if (string.Equals(invokedTag, "FeatureWidgets", StringComparison.Ordinal) &&
            string.Equals(_currentSettingsSection, "CustomWidgetSettings", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("CustomWidgetSettings", out FrameworkElement? customEl) &&
            customEl is CustomWidgetSettingsSection customSection &&
            customSection.TryNavigateBackToStudio())
        {
            CloseTransientNavigationOverlayIfNeeded();
            return;
        }

        if (string.Equals(invokedTag, "Appearance", StringComparison.Ordinal) &&
            string.Equals(_currentSettingsSection, "Appearance", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("Appearance", out FrameworkElement? appearanceEl) &&
            appearanceEl is AppearanceSettingsSection appearanceSection &&
            appearanceSection.TryNavigateBack())
        {
            CloseTransientNavigationOverlayIfNeeded();
            return;
        }

        if (string.Equals(invokedTag, "Interaction", StringComparison.Ordinal) &&
            string.Equals(_currentSettingsSection, "Interaction", StringComparison.Ordinal) &&
            TryCloseCapsuleModeSubPage())
        {
            CloseTransientNavigationOverlayIfNeeded();
            return;
        }

        if (string.Equals(invokedTag, "AppearanceDetail", StringComparison.Ordinal) &&
            string.Equals(_currentSettingsSection, "AppearanceDetail", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? detailEl) &&
            detailEl is FileWidgetSettingsSection detailSection &&
            detailSection.IsDetailSubPageOpen)
        {
            detailSection.CloseAllSubPages(animate: true);
            CloseTransientNavigationOverlayIfNeeded();
            return;
        }

        if (string.Equals(invokedTag, "Maintenance", StringComparison.Ordinal) &&
            string.Equals(_currentSettingsSection, "Maintenance", StringComparison.Ordinal) &&
            TryCloseMaintenanceSubPage())
        {
            CloseTransientNavigationOverlayIfNeeded();
            return;
        }

        if (!string.Equals(invokedTag, _currentSettingsSection, StringComparison.Ordinal))
        {
            ShowSettingsSection(invokedTag, isNestedSection: false);
        }

        CloseTransientNavigationOverlayIfNeeded();
    }

    private void CloseTransientNavigationOverlayIfNeeded()
    {
        if (SettingsNavigationView.PaneDisplayMode != NavigationViewPaneDisplayMode.Left &&
            SettingsNavigationView.IsPaneOpen)
        {
            SettingsNavigationView.IsPaneOpen = false;
        }
    }

    private void RefreshSettingsSearchResults()
    {
        var results = new List<SettingsSearchResult>();
        foreach (SettingsSectionRoute route in SectionRoutes.Values)
        {
            string title = _localizationService.T(route.TitleKey);
            results.Add(new SettingsSearchResult(
                route.Tag,
                title,
                BuildSettingsRouteBreadcrumb(route),
                string.Empty,
                null,
                null));
        }

        results.AddRange(CreateSettingItemSearchResults());

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
        if (matches.Length == 0)
        {
            // AutoSuggestBox guidance: with no matches, show a single-line
            // "no results" row instead of a silently closed flyout. The
            // sentinel SectionTag is guarded in QuerySubmitted/Activate.
            SettingsSearchBox.ItemsSource = new object[]
            {
                new SettingsSearchResult(
                    SettingsSearchNoResultsTag,
                    _localizationService.T("Settings.Search.NoResults"),
                    string.Empty,
                    string.Empty,
                    null,
                    null)
            };
            SettingsSearchBox.IsSuggestionListOpen = true;
            return;
        }

        SettingsSearchBox.ItemsSource = matches.Cast<object>().ToArray();
        SettingsSearchBox.IsSuggestionListOpen = true;
    }

    private IEnumerable<SettingsSearchResult> CreateSettingItemSearchResults()
    {
        bool isStoreBuild = AppDistributionService.Current.IsMicrosoftStore;

        foreach (SettingsSearchCatalogEntry entry in SettingsSearchCatalog.Entries)
        {
            // Store-channel filter (route 1): the entries flagged by the
            // policy only have UI in the direct-installer build, so their
            // search results would dead-end in Store builds. The key set is
            // pinned against the catalog by SettingsSearchChannelPolicyTests.
            if (SettingsSearchChannelPolicy.IsHiddenInStore(entry.HeaderKey) &&
                isStoreBuild)
            {
                continue;
            }

            string destinationTag = NormalizeSettingsSectionTag(entry.SectionTag);
            if (!TryGetSectionRoute(
                    destinationTag,
                    out SettingsSectionRoute route) ||
                string.Equals(entry.HeaderKey, route.TitleKey, StringComparison.Ordinal))
            {
                continue;
            }

            yield return new SettingsSearchResult(
                destinationTag,
                _localizationService.T(entry.HeaderKey),
                BuildSettingsRouteBreadcrumb(route),
                string.IsNullOrWhiteSpace(entry.DescriptionKey)
                    ? string.Empty
                    : _localizationService.T(entry.DescriptionKey),
                entry.SectionTag,
                entry.HeaderKey);
        }
    }

    private string BuildSettingsRouteBreadcrumb(SettingsSectionRoute route)
    {
        if (string.Equals(route.Tag, "CustomWidgetSettings", StringComparison.Ordinal))
        {
            return $"{_localizationService.T("Settings.Section.FeatureWidgets")} / {_localizationService.T(route.TitleKey)}";
        }

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

    private void SettingsSearchBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is AutoSuggestBox searchBox && !string.IsNullOrWhiteSpace(searchBox.Text))
        {
            UpdateSettingsSearchSuggestions(searchBox.Text);
        }
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
        if (args.ChosenSuggestion is null &&
            DeskBox.Helpers.ImeCompositionGuard.IsComposingOrWithinGrace())
        {
            return;
        }

        SettingsSearchResult? result = args.ChosenSuggestion as SettingsSearchResult;
        if (result is null)
        {
            string query = sender.Text.Trim();
            result = FindSettingsSearchMatches(query, 1).FirstOrDefault();
        }

        if (result is null ||
            string.Equals(result.SectionTag, SettingsSearchNoResultsTag, StringComparison.Ordinal))
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
        sender.IsSuggestionListOpen = false;
        CloseTransientNavigationOverlayIfNeeded();
    }

    private void ScheduleSettingsSearchTarget(SettingsSearchResult result)
    {
        if (result.TargetSectionTag is not string targetSectionTag ||
            result.TargetHeaderKey is not string targetHeaderKey)
        {
            return;
        }

        int navigationGeneration = _settingsNavigationGeneration;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_isClosed || navigationGeneration != _settingsNavigationGeneration ||
                !_settingsSectionElements.TryGetValue(targetSectionTag, out FrameworkElement? section))
            {
                return;
            }

            // Resolve only after navigation has created the target section.
            // Expander items may not yet be part of the visual tree.
            section.UpdateLayout();
            FrameworkElement? target = FindSettingsSearchTarget(section, targetHeaderKey, []);
            if (target is null)
            {
                return;
            }
            RevealContainingSettingsSubPage(targetSectionTag, target);
            ExpandSettingsSearchTargetAncestors(target);
            section.UpdateLayout();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_isClosed || navigationGeneration != _settingsNavigationGeneration || section.XamlRoot is null)
                {
                    return;
                }

                section.UpdateLayout();
                FrameworkElement effectiveTarget = ResolveVisibleSettingsSearchTarget(target, section);
                effectiveTarget.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = WindowsCompatibilityService.ShouldAnimate,
                    VerticalAlignmentRatio = 0.18
                });
                HighlightSettingsSearchTarget(effectiveTarget);
                FocusSettingsSearchTarget(effectiveTarget);
            });
        });
    }

    private static FrameworkElement ResolveVisibleSettingsSearchTarget(
        FrameworkElement target,
        FrameworkElement sectionRoot)
    {
        if (IsEffectivelyVisibleWithinSection(target, sectionRoot))
        {
            return target;
        }

        if (string.Equals(target.Name, "EmptyMyWidgetsCard", StringComparison.Ordinal) &&
            sectionRoot.FindName("MyWidgetsListPanel") is Panel listPanel &&
            IsEffectivelyVisibleWithinSection(listPanel, sectionRoot))
        {
            foreach (UIElement child in listPanel.Children)
            {
                if (child is FrameworkElement card && card.Visibility == Visibility.Visible)
                {
                    return card;
                }
            }
        }

        DependencyObject? branch = target;
        DependencyObject? parent = VisualTreeHelper.GetParent(target);
        while (parent is not null && !ReferenceEquals(branch, sectionRoot))
        {
            if (parent is SettingsExpander expander &&
                IsEffectivelyVisibleWithinSection(expander, sectionRoot))
            {
                return expander;
            }

            if (parent is Panel panel &&
                IsEffectivelyVisibleWithinSection(panel, sectionRoot))
            {
                int branchIndex = -1;
                for (int index = 0; index < panel.Children.Count; index++)
                {
                    if (ReferenceEquals(panel.Children[index], branch))
                    {
                        branchIndex = index;
                        break;
                    }
                }

                if (branchIndex >= 0)
                {
                    for (int index = branchIndex - 1; index >= 0; index--)
                    {
                        if (panel.Children[index] is FrameworkElement previous &&
                            previous.Visibility == Visibility.Visible)
                        {
                            return previous;
                        }
                    }

                    for (int index = branchIndex + 1; index < panel.Children.Count; index++)
                    {
                        if (panel.Children[index] is FrameworkElement next &&
                            next.Visibility == Visibility.Visible)
                        {
                            return next;
                        }
                    }
                }

                return panel;
            }

            branch = parent;
            parent = VisualTreeHelper.GetParent(parent);
        }

        return target;
    }

    private static bool IsEffectivelyVisibleWithinSection(
        FrameworkElement element,
        FrameworkElement sectionRoot)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (current is UIElement uiElement && uiElement.Visibility != Visibility.Visible)
            {
                return false;
            }

            if (ReferenceEquals(current, sectionRoot))
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
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

        if (target is Control targetControl)
        {
            _settingsSearchHighlightOriginalBorderBrush = targetControl.ReadLocalValue(Control.BorderBrushProperty);
            _settingsSearchHighlightOriginalBorderThickness = targetControl.ReadLocalValue(Control.BorderThicknessProperty);

            Brush? accentBrush = NeutralInteractionBrush.ResolveThemedResource(
                "AccentFillColorDefaultBrush",
                targetControl);
            if (accentBrush is not null)
            {
                targetControl.BorderBrush = accentBrush;
                targetControl.BorderThickness = new Thickness(2);
            }
        }
        else
        {
            _settingsSearchHighlightOriginalBorderBrush = DependencyProperty.UnsetValue;
            _settingsSearchHighlightOriginalBorderThickness = DependencyProperty.UnsetValue;
        }

        if (WindowsCompatibilityService.ShouldAnimate)
        {
            target.Opacity = Math.Min(0.76, _settingsSearchHighlightOriginalOpacity);
            var animation = new DoubleAnimation
            {
                From = target.Opacity,
                To = _settingsSearchHighlightOriginalOpacity,
                Duration = TimeSpan.FromMilliseconds(450),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));

            var storyboard = new Storyboard();
            storyboard.Children.Add(animation);
            EventHandler<object>? completedHandler = null;
            completedHandler = (_, _) =>
            {
                storyboard.Completed -= completedHandler;
                if (!ReferenceEquals(_settingsSearchHighlightStoryboard, storyboard))
                {
                    return;
                }

                target.Opacity = _settingsSearchHighlightOriginalOpacity;
                _settingsSearchHighlightStoryboard = null;
                _settingsSearchHighlightCompletedHandler = null;
            };
            _settingsSearchHighlightCompletedHandler = completedHandler;
            storyboard.Completed += completedHandler;
            _settingsSearchHighlightStoryboard = storyboard;
            storyboard.Begin();
        }

        Microsoft.UI.Dispatching.DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(1600);
        timer.IsRepeating = false;
        Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? tickHandler = null;
        tickHandler = (senderTimer, _) =>
        {
            senderTimer.Tick -= tickHandler;
            senderTimer.Stop();
            if (!ReferenceEquals(_settingsSearchHighlightTimer, senderTimer))
            {
                return;
            }

            ClearSettingsSearchHighlight();
        };
        _settingsSearchHighlightTimerTickHandler = tickHandler;
        timer.Tick += tickHandler;
        _settingsSearchHighlightTimer = timer;
        timer.Start();
    }

    private void ClearSettingsSearchHighlight()
    {
        Microsoft.UI.Dispatching.DispatcherQueueTimer? timer = _settingsSearchHighlightTimer;
        Windows.Foundation.TypedEventHandler<Microsoft.UI.Dispatching.DispatcherQueueTimer, object>? tickHandler =
            _settingsSearchHighlightTimerTickHandler;
        if (timer is not null && tickHandler is not null)
        {
            timer.Tick -= tickHandler;
        }
        timer?.Stop();
        _settingsSearchHighlightTimer = null;
        _settingsSearchHighlightTimerTickHandler = null;

        Storyboard? storyboard = _settingsSearchHighlightStoryboard;
        EventHandler<object>? completedHandler =
            _settingsSearchHighlightCompletedHandler;
        if (storyboard is not null && completedHandler is not null)
        {
            storyboard.Completed -= completedHandler;
        }

        storyboard?.Stop();
        if (storyboard is not null)
        {
            storyboard.Children.Clear();
        }

        if (_settingsSearchHighlightTarget is not null)
        {
            _settingsSearchHighlightTarget.Opacity = _settingsSearchHighlightOriginalOpacity;
            if (_settingsSearchHighlightTarget is Control targetControl)
            {
                if (ReferenceEquals(_settingsSearchHighlightOriginalBorderBrush, DependencyProperty.UnsetValue))
                {
                    targetControl.ClearValue(Control.BorderBrushProperty);
                }
                else
                {
                    targetControl.BorderBrush = _settingsSearchHighlightOriginalBorderBrush as Brush;
                }

                if (ReferenceEquals(_settingsSearchHighlightOriginalBorderThickness, DependencyProperty.UnsetValue))
                {
                    targetControl.ClearValue(Control.BorderThicknessProperty);
                }
                else if (_settingsSearchHighlightOriginalBorderThickness is Thickness originalThickness)
                {
                    targetControl.BorderThickness = originalThickness;
                }
            }
        }

        _settingsSearchHighlightStoryboard = null;
        _settingsSearchHighlightCompletedHandler = null;
        _settingsSearchHighlightTarget = null;
        _settingsSearchHighlightOriginalOpacity = 1;
        _settingsSearchHighlightOriginalBorderBrush = DependencyProperty.UnsetValue;
        _settingsSearchHighlightOriginalBorderThickness = DependencyProperty.UnsetValue;
    }

    /// <summary>
    /// Explicitly unregisters dependency-property callbacks before the window
    /// tree is detached. WinUI's callback token is native state; leaving it on
    /// a closed expander delays release of the complete settings tree until a
    /// later GC/finalizer pass.
    /// </summary>
    private void ClearFeatureSettingsExpanderCallbacks()
    {
        foreach (var registration in _featureSettingsExpanderCallbacks.ToArray())
        {
            try
            {
                registration.Key.UnregisterPropertyChangedCallback(
                    SettingsExpander.IsExpandedProperty,
                    registration.Value);
            }
            catch (Exception ex)
            {
                App.Log(
                    $"[SettingsLifecycle] Expander callback unregister failed: {ex.Message}");
            }
        }

        _featureSettingsExpanderCallbacks.Clear();
        _featureSettingsExpanders.Clear();
        _isSynchronizingFeatureSettingsExpanders = false;
    }

    private void SettingsNavigationView_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
    {
        _ = TryNavigateBack();
    }

    internal bool TryNavigateBack()
    {
        if (_isClosed)
        {
            return false;
        }

        if (string.Equals(_currentSettingsSection, "CustomWidgetSettings", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("CustomWidgetSettings", out FrameworkElement? customEl) &&
            customEl is CustomWidgetSettingsSection customSection &&
            customSection.TryNavigateBackToStudio())
        {
            return true;
        }

        if (string.Equals(_currentSettingsSection, "Appearance", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("Appearance", out FrameworkElement? appearanceEl) &&
            appearanceEl is AppearanceSettingsSection appearanceSection &&
            appearanceSection.TryNavigateBack())
        {
            return true;
        }

        if (string.Equals(_currentSettingsSection, "Interaction", StringComparison.Ordinal) &&
            TryCloseCapsuleModeSubPage())
        {
            return true;
        }

        if (string.Equals(_currentSettingsSection, "WidgetGroups", StringComparison.Ordinal) &&
            TryCloseWidgetGroupDetailSubPage())
        {
            return true;
        }

        if (string.Equals(_currentSettingsSection, "AppearanceDetail", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? detailEl) &&
            detailEl is FileWidgetSettingsSection detailSection &&
            detailSection.TryNavigateBack())
        {
            return true;
        }

        if (string.Equals(_currentSettingsSection, "Maintenance", StringComparison.Ordinal) &&
            TryCloseMaintenanceSubPage())
        {
            return true;
        }

        if (TryGetSectionRoute(_currentSettingsSection, out var route) &&
            !string.IsNullOrWhiteSpace(route.ParentTag))
        {
            NavigateToSettingsSection(route.ParentTag);
            return true;
        }

        return false;
    }

    public void ShowSection(string sectionTag)
    {
        NavigateToSettingsSection(sectionTag);
    }

    public void ShowGlanceSection(string widgetId)
    {
        EnsureSettingsSectionCreated("GlanceSettings");
        GlanceSettingsSection.SelectWidget(widgetId);
        // Deep-linking a different glance instance while the section is
        // already on screen hits NavigateToSettingsSection's same-section
        // early exit — which also skips the section's enter-time refresh,
        // leaving the previously selected instance's settings on display
        // (batch R12 window-state audit: users could edit the wrong
        // instance). SelectWidget staged the new selection; run the
        // refresh the navigation would have triggered.
        if (SettingsRoot.IsLoaded &&
            string.Equals(_currentSettingsSection, "GlanceSettings", StringComparison.Ordinal))
        {
            _ = GlanceSettingsSection.RefreshFromStoreAsync();
            return;
        }

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
        if (!string.IsNullOrWhiteSpace(sectionTag))
        {
            int atIndex = sectionTag.IndexOf('@');
            if (atIndex > 0 && atIndex + 1 < sectionTag.Length)
            {
                string rawTag = sectionTag[..atIndex];
                string headerKey = sectionTag[(atIndex + 1)..];
                string destinationTag = NormalizeSettingsSectionTag(rawTag);
                NavigateToSettingsSectionCore(destinationTag);
                ScheduleSettingsSearchTarget(new SettingsSearchResult(
                    destinationTag,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    destinationTag,
                    headerKey));
                return;
            }
        }

        NavigateToSettingsSectionCore(sectionTag);
    }

    private void NavigateToSettingsSectionCore(string sectionTag)
    {
        if (string.Equals(sectionTag, "CustomWidgetSettings:ai-settings", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sectionTag, "CustomWidgetSettings:AiSettings", StringComparison.OrdinalIgnoreCase))
        {
            EnsureSettingsSectionCreated("AiSettings");
            if (SettingsRoot.IsLoaded &&
                string.Equals(_currentSettingsSection, "AiSettings", StringComparison.Ordinal))
            {
                AiSettingsSection.RefreshState();
                return;
            }

            sectionTag = "AiSettings";
        }
        else if (string.Equals(sectionTag, "CustomWidgetSettings:agent-hub", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(sectionTag, "AiSettings:agent-hub", StringComparison.OrdinalIgnoreCase))
        {
            EnsureSettingsSectionCreated("AiSettings");
            AiSettingsSection.StageFocusAgentHub();
            if (SettingsRoot.IsLoaded &&
                string.Equals(_currentSettingsSection, "AiSettings", StringComparison.Ordinal))
            {
                AiSettingsSection.RefreshState();
                return;
            }

            sectionTag = "AiSettings";
        }
        else if (!string.IsNullOrWhiteSpace(sectionTag) &&
            sectionTag.StartsWith("CustomWidgetSettings:", StringComparison.OrdinalIgnoreCase))
        {
            string deepLinkPayload = sectionTag["CustomWidgetSettings:".Length..];
            EnsureSettingsSectionCreated("CustomWidgetSettings");
            CustomWidgetSettingsSection.StageDeepLink(deepLinkPayload);
            if (SettingsRoot.IsLoaded &&
                string.Equals(_currentSettingsSection, "CustomWidgetSettings", StringComparison.Ordinal))
            {
                CustomWidgetSettingsSection.RefreshInstances();
                return;
            }

            sectionTag = "CustomWidgetSettings";
        }

        string rawSectionTag = sectionTag ?? string.Empty;
        sectionTag = NormalizeSettingsSectionTag(rawSectionTag);

        ShowSettingsSection(sectionTag);
        ApplyRawSectionSubPageDeepLink(rawSectionTag);
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

        // Re-entering the visible section would re-run every refresh and,
        // with the enter transition wired below, replay a slide-in onto
        // unchanged content. Skip it once the window is live; the
        // construction-time initial navigation (IsLoaded == false) must
        // still run to reveal the section.
        if (SettingsRoot.IsLoaded &&
            string.Equals(sectionTag, _currentSettingsSection, StringComparison.Ordinal))
        {
            return;
        }

        string previousSectionTag = _currentSettingsSection;
        isNestedSection = !string.IsNullOrWhiteSpace(route.ParentTag);
        _currentSettingsSection = sectionTag;
        ViewModel.SetIsOnAboutPage(string.Equals(sectionTag, "About", StringComparison.Ordinal));
        if (string.Equals(previousSectionTag, "CustomWidgetSettings", StringComparison.Ordinal) &&
            !string.Equals(sectionTag, "CustomWidgetSettings", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("CustomWidgetSettings", out FrameworkElement? prevCustomEl) &&
            prevCustomEl is CustomWidgetSettingsSection prevCustomSection)
        {
            prevCustomSection.CloseDetailView(animate: false);
        }
        ResetInSectionSubPagesOnExit(previousSectionTag, sectionTag);
        int navigationGeneration = ++_settingsNavigationGeneration;
        ClearSettingsSearchHighlight();
        string visibleSectionTag = sectionTag switch
        {
            "CustomWidgetSettings" => "FeatureWidgets",
            _ => sectionTag
        };
        EnsureSettingsSectionCreated(visibleSectionTag);
        string? inlineSectionTag = sectionTag switch
        {
            "CustomWidgetSettings" => "CustomWidgetSettings",
            _ => null
        };
        if (inlineSectionTag is not null)
        {
            EnsureSettingsSectionCreated(inlineSectionTag);
        }
        PrepareDeferredNoticeInfoBars();
        foreach ((string tag, FrameworkElement sectionElement) in _settingsSectionElements)
        {
            bool isPrimarySection = string.Equals(
                tag,
                visibleSectionTag,
                StringComparison.Ordinal);
            bool isInlineSection = sectionTag == "CustomWidgetSettings" && tag == "CustomWidgetSettings";
            sectionElement.Visibility = isPrimarySection || isInlineSection
                ? Visibility.Visible : Visibility.Collapsed;
        }

        SyncFeatureWidgetsHubState(sectionTag);

        // Reset scrolling before the enter transition starts: a page sliding
        // in at a stale scroll offset would visibly jump to the top mid-slide.
        // Synchronous (rather than the low-priority dispatch below) so the
        // first animation frame is already rendered at offset zero.
        PageScroller.ChangeView(null, 0, null, disableAnimation: true);

        PlaySettingsSectionEnterTransition(visibleSectionTag, previousSectionTag, sectionTag, inlineSectionTag);

        if (sectionTag == "General")
        {
            RefreshAiSettingsEntrySummary();
        }
        if (sectionTag == "Interaction")
        {
            RefreshDisplaysEntrySummary();
            ViewModel.RefreshGlobalHotkeyState();
            RefreshGlobalHotkeyControls();
        }
        if (sectionTag == "FileStackSettings")
        {
            _ = ViewModel.RefreshFileStackRulePreviewFromDiskAsync();
        }
        if (sectionTag == "AppearanceDetail")
        {
            AppearanceDetailSection.OrganizationSection.Refresh();
            RefreshManagedStoragePathWarning();
            RefreshManagedStorageDesktopShortcutState();
            RefreshDragOutWin10State();
            SyncManagedStorageQuickAccessToggleState();
            _ = ViewModel.RefreshQuickAccessStateAsync();
            _ = RefreshOrphanFoldersSummaryAsync();
        }
        if (sectionTag == "Displays")
        {
            DisplaysSection.Refresh();
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
            _ = ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
        }
        UpdateSearchSettingsActivity();
        UpdateBackupSettingsActivity();
        if (sectionTag == "GlanceSettings")
        {
            _ = GlanceSettingsSection.RefreshFromStoreAsync();
        }
        if (sectionTag == "CustomWidgetSettings")
        {
            CustomWidgetSettingsSection.RefreshInstances();
        }
        if (sectionTag == "AiSettings")
        {
            AiSettingsSection.RefreshState();
        }
        if (sectionTag == "Maintenance")
        {
            _backupSettingsViewModel.RefreshLocalStatus();
            _ = RefreshBackupSnapshotInventoryAsync();
            _ = InitializeCloudBackupSectionAsync();
        }
        if (sectionTag == "About")
        {
            ViewModel.RefreshDragDropPermissionDiagnostic();
            ViewModel.RefreshRuntimeDiagnostics();
        }
        SettingsNavigationView.IsBackButtonVisible = isNestedSection
            ? NavigationViewBackButtonVisible.Visible
            : NavigationViewBackButtonVisible.Collapsed;
        UpdateBreadcrumb(route);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_isClosed || navigationGeneration != _settingsNavigationGeneration)
            {
                return;
            }
            RestartSectionLayoutSettleTimer();
        });
    }

    private void PlaySettingsSectionEnterTransition(
        string visibleSectionTag,
        string previousSectionTag,
        string sectionTag,
        string? inlineSectionTag)
    {
        // The construction-time initial navigation runs before the window is
        // loaded; animating it would flash the freshly built content.
        if (!SettingsRoot.IsLoaded ||
            !_settingsSectionElements.TryGetValue(visibleSectionTag, out FrameworkElement? enteringSection))
        {
            return;
        }

        if (IsFeatureWidgetsHubSiblingSwitch(previousSectionTag, sectionTag))
        {
            FrameworkElement tabContent = string.Equals(sectionTag, "CustomWidgetSettings", StringComparison.Ordinal)
                ? CustomWidgetSettingsSection
                : FeatureWidgetListHost;
            tabContent.UpdateLayout();
            float tabOffsetX = DetermineNavigationEnterOffset(previousSectionTag, sectionTag);
            DetailPageTransitionHelper.PlayNavigationEnter(tabContent, tabOffsetX);
            App.Log($"[SettingsNav] transition tag={sectionTag} offset={tabOffsetX}");
            return;
        }

        // Force layout (notably the first, lazy instantiation of the section)
        // before animating so the page does not visibly grow while sliding.
        enteringSection.UpdateLayout();
        float enterOffsetX = DetermineNavigationEnterOffset(previousSectionTag, sectionTag);
        DetailPageTransitionHelper.PlayNavigationEnter(enteringSection, enterOffsetX);

        // Inline sibling sections (the file-grid page riding along with
        // AppearanceDetail, the hotkey page with Interaction, the reset page
        // with Maintenance) enter together with the primary page in the same
        // direction so the composed page slides as one.
        if (inlineSectionTag is not null &&
            !string.Equals(inlineSectionTag, "CustomWidgetSettings", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue(inlineSectionTag, out FrameworkElement? inlineSection) &&
            !ReferenceEquals(inlineSection, enteringSection))
        {
            inlineSection.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(inlineSection, enterOffsetX);
        }
        App.Log($"[SettingsNav] transition tag={sectionTag} offset={enterOffsetX}");
    }

    private float DetermineNavigationEnterOffset(string previousSectionTag, string sectionTag) =>
        DetermineNavigationEnterOffset(
            previousSectionTag,
            sectionTag,
            GetNavigationTagOrder(),
            SubSectionTagOrder);

    /// <summary>
    /// Pure direction logic for the section enter transition, extracted for
    /// contract testing. Deeper targets enter from the right (drilling in),
    /// returning to shallower pages enters from the left, and same-depth
    /// navigation follows the nav menu order — with sub-pages sharing one
    /// nav entry tie-broken by their drill-down order in
    /// <paramref name="subSectionTagOrder"/>.
    /// </summary>
    internal static float DetermineNavigationEnterOffset(
        string previousSectionTag,
        string sectionTag,
        IReadOnlyList<string> navigationTagOrder,
        IReadOnlyList<string> subSectionTagOrder)
    {
        int previousDepth = GetSectionRouteDepth(previousSectionTag);
        int nextDepth = GetSectionRouteDepth(sectionTag);
        if (nextDepth != previousDepth)
        {
            return nextDepth > previousDepth ? NavigationEnterOffsetPx : -NavigationEnterOffsetPx;
        }

        // Same depth (top-level pages or sibling sub-pages): slide follows
        // the nav menu order — moving down the list enters from the right,
        // moving up from the left.
        (int NavIndex, int SubIndex)? previousKey = GetNavigationOrderKey(
            navigationTagOrder, subSectionTagOrder, previousSectionTag);
        (int NavIndex, int SubIndex)? nextKey = GetNavigationOrderKey(
            navigationTagOrder, subSectionTagOrder, sectionTag);
        if (previousKey is null || nextKey is null)
        {
            return NavigationEnterOffsetPx;
        }
        int navComparison = nextKey.Value.NavIndex.CompareTo(previousKey.Value.NavIndex);
        if (navComparison != 0)
        {
            return navComparison > 0 ? NavigationEnterOffsetPx : -NavigationEnterOffsetPx;
        }

        // Same nav entry (sibling sub-pages under one parent): previously
        // both compared equal here and the slide was always leftward; now
        // the family's drill-down order decides the direction. Untracked
        // tags keep the forward default instead of a bogus rank.
        if (previousKey.Value.SubIndex < 0 || nextKey.Value.SubIndex < 0)
        {
            return NavigationEnterOffsetPx;
        }
        return nextKey.Value.SubIndex > previousKey.Value.SubIndex
            ? NavigationEnterOffsetPx
            : -NavigationEnterOffsetPx;
    }

    private static (int NavIndex, int SubIndex)? GetNavigationOrderKey(
        IReadOnlyList<string> navigationTagOrder,
        IReadOnlyList<string> subSectionTagOrder,
        string sectionTag)
    {
        if (!TryGetSectionRoute(sectionTag, out SettingsSectionRoute route))
        {
            return null;
        }

        int navIndex = IndexOfTag(navigationTagOrder, route.NavTag);
        if (navIndex < 0)
        {
            return null;
        }

        int subIndex = route.ParentTag is null
            ? (string.Equals(sectionTag, "CustomWidgetSettings", StringComparison.Ordinal) ? 1 : 0)
            : IndexOfTag(subSectionTagOrder, sectionTag);
        return (navIndex, subIndex);
    }

    private static int IndexOfTag(IReadOnlyList<string> tags, string tag)
    {
        for (int index = 0; index < tags.Count; index++)
        {
            if (string.Equals(tags[index], tag, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static int GetSectionRouteDepth(string sectionTag)
    {
        int depth = 0;
        string current = sectionTag;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (TryGetSectionRoute(current, out SettingsSectionRoute route) &&
               route.ParentTag is string parentTag &&
               visited.Add(parentTag))
        {
            depth++;
            current = parentTag;
        }
        return depth;
    }

    private List<string> GetNavigationTagOrder()
    {
        _navigationTagOrder ??= BuildNavigationTagOrder(SettingsNavigationView.MenuItems);
        return _navigationTagOrder;
    }

    private static List<string> BuildNavigationTagOrder(IList<object> menuItems)
    {
        var tags = new List<string>();
        CollectNavigationItemTags(menuItems, tags);
        return tags;
    }

    private static void CollectNavigationItemTags(IList<object> menuItems, List<string> tags)
    {
        // Indexer loop on purpose: CsWinRT IVector iteration can throw on
        // projected collections (see the animation-batch foreach incidents).
        for (int index = 0; index < menuItems.Count; index++)
        {
            if (menuItems[index] is NavigationViewItem navItem)
            {
                if (navItem.Tag is string tag)
                {
                    tags.Add(tag);
                }
                CollectNavigationItemTags(navItem.MenuItems, tags);
            }
        }
    }

    private void UpdateBreadcrumb(SettingsSectionRoute route)
    {
        if (string.Equals(route.Tag, "CustomWidgetSettings", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("CustomWidgetSettings", out FrameworkElement? customEl) &&
            customEl is CustomWidgetSettingsSection { IsDetailViewOpen: true, ActiveDetailTitle: { Length: > 0 } detailTitle })
        {
            SettingsBreadcrumbBar.ItemsSource = new object[]
            {
                new SettingsBreadcrumbItem("FeatureWidgets", _localizationService.T("Settings.Section.FeatureWidgets"), 0.62),
                new SettingsBreadcrumbItem(route.Tag, _localizationService.T(route.TitleKey), 0.62),
                new SettingsBreadcrumbItem("CustomWidgetSettings:__detail__", detailTitle, 1.0)
            };
            SettingsBreadcrumbHost.Visibility = Visibility.Visible;
            SettingsBreadcrumbBar.Visibility = Visibility.Visible;
            SettingsNavigationView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;
            return;
        }

        if (string.Equals(route.Tag, "Appearance", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("Appearance", out FrameworkElement? appearanceEl) &&
            appearanceEl is AppearanceSettingsSection { IsMaterialSubPageOpen: true })
        {
            SettingsBreadcrumbBar.ItemsSource = new object[]
            {
                new SettingsBreadcrumbItem(route.Tag, _localizationService.T(route.TitleKey), 0.62),
                new SettingsBreadcrumbItem("Appearance:__material__", _localizationService.T("Settings.Material.Title"), 1.0)
            };
            SettingsBreadcrumbHost.Visibility = Visibility.Visible;
            SettingsBreadcrumbBar.Visibility = Visibility.Visible;
            SettingsNavigationView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;
            return;
        }

        if (string.Equals(route.Tag, "Interaction", StringComparison.Ordinal) &&
            _isCapsuleModeSubPageOpen)
        {
            SettingsBreadcrumbBar.ItemsSource = new object[]
            {
                new SettingsBreadcrumbItem(route.Tag, _localizationService.T(route.TitleKey), 0.62),
                new SettingsBreadcrumbItem("Interaction:__capsule__", _localizationService.T("Settings.Section.CapsuleMode"), 1.0)
            };
            SettingsBreadcrumbHost.Visibility = Visibility.Visible;
            SettingsBreadcrumbBar.Visibility = Visibility.Visible;
            SettingsNavigationView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;
            return;
        }

        if (string.Equals(route.Tag, "WidgetGroups", StringComparison.Ordinal) &&
            _activeWidgetGroupId is not null &&
            WidgetGroupDetailPresenter?.Content is WidgetGroupSettingsItem { DisplayName: { Length: > 0 } groupTitle })
        {
            SettingsBreadcrumbBar.ItemsSource = new object[]
            {
                new SettingsBreadcrumbItem("Interaction", _localizationService.T("Settings.Section.Interaction"), 0.62),
                new SettingsBreadcrumbItem(route.Tag, _localizationService.T(route.TitleKey), 0.62),
                new SettingsBreadcrumbItem("WidgetGroups:__detail__", groupTitle, 1.0)
            };
            SettingsBreadcrumbHost.Visibility = Visibility.Visible;
            SettingsBreadcrumbBar.Visibility = Visibility.Visible;
            SettingsNavigationView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;
            return;
        }

        if (string.Equals(route.Tag, "AppearanceDetail", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? detailEl) &&
            detailEl is FileWidgetSettingsSection detailSection)
        {
            if (detailSection.IsFileDisplaySubPageOpen)
            {
                SettingsBreadcrumbBar.ItemsSource = new object[]
                {
                    new SettingsBreadcrumbItem(route.Tag, _localizationService.T(route.TitleKey), 0.62),
                    new SettingsBreadcrumbItem("AppearanceDetail:__filedisplay__", _localizationService.T("Settings.AppearanceDetail.Title"), 1.0)
                };
                SettingsBreadcrumbHost.Visibility = Visibility.Visible;
                SettingsBreadcrumbBar.Visibility = Visibility.Visible;
                SettingsNavigationView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;
                return;
            }

            if (detailSection.OrganizationSection is { IsRuleDetailOpen: true, ActiveRuleDetailTitle: { Length: > 0 } ruleTitle })
            {
                SettingsBreadcrumbBar.ItemsSource = new object[]
                {
                    new SettingsBreadcrumbItem(route.Tag, _localizationService.T(route.TitleKey), 0.62),
                    new SettingsBreadcrumbItem("AppearanceDetail:__rules__", _localizationService.T("DesktopOrganization.Rules.SectionTitle"), 0.62),
                    new SettingsBreadcrumbItem("AppearanceDetail:__detail__", ruleTitle, 1.0)
                };
                SettingsBreadcrumbHost.Visibility = Visibility.Visible;
                SettingsBreadcrumbBar.Visibility = Visibility.Visible;
                SettingsNavigationView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;
                return;
            }

            if (detailSection.OrganizationSection.IsRuleListOpen)
            {
                SettingsBreadcrumbBar.ItemsSource = new object[]
                {
                    new SettingsBreadcrumbItem(route.Tag, _localizationService.T(route.TitleKey), 0.62),
                    new SettingsBreadcrumbItem("AppearanceDetail:__rules__", _localizationService.T("DesktopOrganization.Rules.SectionTitle"), 1.0)
                };
                SettingsBreadcrumbHost.Visibility = Visibility.Visible;
                SettingsBreadcrumbBar.Visibility = Visibility.Visible;
                SettingsNavigationView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;
                return;
            }
        }

        if (string.Equals(route.Tag, "Maintenance", StringComparison.Ordinal) &&
            _activeMaintenanceSubPage != MaintenanceSubPageKind.None)
        {
            string subPageTitleKey = _activeMaintenanceSubPage == MaintenanceSubPageKind.AutomaticBackup
                ? "Settings.DataBackup.AutomaticBackup.Title"
                : "Settings.CloudBackup.Title";
            SettingsBreadcrumbBar.ItemsSource = new object[]
            {
                new SettingsBreadcrumbItem(route.Tag, _localizationService.T(route.TitleKey), 0.62),
                new SettingsBreadcrumbItem("Maintenance:__subpage__", _localizationService.T(subPageTitleKey), 1.0)
            };
            SettingsBreadcrumbHost.Visibility = Visibility.Visible;
            SettingsBreadcrumbBar.Visibility = Visibility.Visible;
            SettingsNavigationView.IsBackButtonVisible = NavigationViewBackButtonVisible.Visible;
            return;
        }

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
        if (string.Equals(item.SectionTag, "CustomWidgetSettings:__detail__", StringComparison.Ordinal) ||
            string.Equals(item.SectionTag, "Appearance:__material__", StringComparison.Ordinal) ||
            string.Equals(item.SectionTag, "Interaction:__capsule__", StringComparison.Ordinal) ||
            string.Equals(item.SectionTag, "WidgetGroups:__detail__", StringComparison.Ordinal) ||
            string.Equals(item.SectionTag, "AppearanceDetail:__filedisplay__", StringComparison.Ordinal) ||
            string.Equals(item.SectionTag, "AppearanceDetail:__detail__", StringComparison.Ordinal) ||
            string.Equals(item.SectionTag, "Maintenance:__subpage__", StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(item.SectionTag, "CustomWidgetSettings", StringComparison.Ordinal) &&
            string.Equals(_currentSettingsSection, "CustomWidgetSettings", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("CustomWidgetSettings", out FrameworkElement? customEl) &&
            customEl is CustomWidgetSettingsSection customSection &&
            customSection.TryNavigateBackToStudio())
        {
            return;
        }

        if (string.Equals(item.SectionTag, "Appearance", StringComparison.Ordinal) &&
            string.Equals(_currentSettingsSection, "Appearance", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("Appearance", out FrameworkElement? appearanceEl) &&
            appearanceEl is AppearanceSettingsSection appearanceSection &&
            appearanceSection.TryNavigateBack())
        {
            return;
        }

        if (string.Equals(item.SectionTag, "Interaction", StringComparison.Ordinal) &&
            string.Equals(_currentSettingsSection, "Interaction", StringComparison.Ordinal) &&
            TryCloseCapsuleModeSubPage())
        {
            return;
        }

        if (string.Equals(item.SectionTag, "WidgetGroups", StringComparison.Ordinal) &&
            string.Equals(_currentSettingsSection, "WidgetGroups", StringComparison.Ordinal) &&
            TryCloseWidgetGroupDetailSubPage())
        {
            return;
        }

        if (string.Equals(item.SectionTag, "AppearanceDetail:__rules__", StringComparison.Ordinal) &&
            string.Equals(_currentSettingsSection, "AppearanceDetail", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? rulesDetailEl) &&
            rulesDetailEl is FileWidgetSettingsSection rulesDetailSection)
        {
            _ = rulesDetailSection.OrganizationSection.TryNavigateBackToRuleList();
            return;
        }

        if (string.Equals(item.SectionTag, "AppearanceDetail", StringComparison.Ordinal) &&
            string.Equals(_currentSettingsSection, "AppearanceDetail", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? detailEl) &&
            detailEl is FileWidgetSettingsSection detailSection &&
            detailSection.IsDetailSubPageOpen)
        {
            detailSection.CloseAllSubPages(animate: true);
            return;
        }

        if (string.Equals(item.SectionTag, "Maintenance", StringComparison.Ordinal) &&
            string.Equals(_currentSettingsSection, "Maintenance", StringComparison.Ordinal) &&
            TryCloseMaintenanceSubPage())
        {
            return;
        }

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
        if (string.IsNullOrWhiteSpace(sectionTag))
        {
            return "General";
        }

        string trimmed = sectionTag.Trim();
        return trimmed switch
        {
            "PerformanceSettings" => "General",
            "AppearanceMaterialSettings" or "AppearanceDensitySettings" or "AppearanceWindowSettings" or "AppearanceAnimationSettings" => "Appearance",
            "CapsuleMode" or "CapsuleOverridesSettings" or "InteractionWindowSettings" => "Interaction",
            "FileStorageSettings" or "FileDisplaySettings" or "ManagedStorage" or "DesktopOrganizationSettings" => "AppearanceDetail",
            "BackupRestoreSettings" or "CloudBackupSettings" or "ResetSettings" or "DataHealthSettings" => "Maintenance",
            "CompatibilityDiagnosticsSettings" => "About",
            _ when string.Equals(trimmed, "ai", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "ai-settings", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "aisettings", StringComparison.OrdinalIgnoreCase) => "AiSettings",
            _ when string.Equals(trimmed, "custom-widgets", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "customwidgets", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "customwidgetsettings", StringComparison.OrdinalIgnoreCase) => "CustomWidgetSettings",
            _ => trimmed
        };
    }

    private static bool IsFeatureWidgetsHubSiblingSwitch(string previousSectionTag, string sectionTag) =>
        (string.Equals(previousSectionTag, "FeatureWidgets", StringComparison.Ordinal) &&
         string.Equals(sectionTag, "CustomWidgetSettings", StringComparison.Ordinal)) ||
        (string.Equals(previousSectionTag, "CustomWidgetSettings", StringComparison.Ordinal) &&
         string.Equals(sectionTag, "FeatureWidgets", StringComparison.Ordinal));

    private void SyncFeatureWidgetsHubState(string sectionTag)
    {
        if (!_settingsSectionElements.ContainsKey("FeatureWidgets"))
        {
            return;
        }

        if (string.Equals(sectionTag, "FeatureWidgets", StringComparison.Ordinal))
        {
            Localized.SetKey(FeatureWidgetsHubTitleText, "Settings.Section.FeatureWidgets");
            FeatureWidgetsHubHeaderPanel.Visibility = Visibility.Visible;
            CustomWidgetsToolbarPanel.Visibility = Visibility.Collapsed;
            FeatureWidgetListHost.Visibility = Visibility.Visible;
            CustomWidgetHostPanel.Visibility = Visibility.Collapsed;
            SetFeatureWidgetsHubTabIndex(0);
        }
        else if (string.Equals(sectionTag, "CustomWidgetSettings", StringComparison.Ordinal))
        {
            bool isDetailOpen =
                _settingsSectionElements.TryGetValue("CustomWidgetSettings", out FrameworkElement? customEl) &&
                customEl is CustomWidgetSettingsSection { IsDetailViewOpen: true };
            Localized.SetKey(FeatureWidgetsHubTitleText, "CustomWidget.Settings.Title");
            FeatureWidgetsHubHeaderPanel.Visibility = isDetailOpen ? Visibility.Collapsed : Visibility.Visible;
            CustomWidgetsToolbarPanel.Visibility = isDetailOpen ? Visibility.Collapsed : Visibility.Visible;
            FeatureWidgetListHost.Visibility = Visibility.Collapsed;
            CustomWidgetHostPanel.Visibility = Visibility.Visible;
            SetFeatureWidgetsHubTabIndex(1);
        }
    }

    private void SetFeatureWidgetsHubTabIndex(int index)
    {
        Segmented segmented = FeatureWidgetsHubSegmented;
        if (segmented.SelectedIndex == index)
        {
            return;
        }

        _isSyncingFeatureWidgetsHubTab = true;
        try
        {
            segmented.SelectedIndex = index;
        }
        finally
        {
            _isSyncingFeatureWidgetsHubTab = false;
        }
    }

    private void FeatureWidgetsHubSegmented_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingFeatureWidgetsHubTab || _isClosed)
        {
            return;
        }

        string targetSection = FeatureWidgetsHubSegmented.SelectedIndex == 1
            ? "CustomWidgetSettings"
            : "FeatureWidgets";
        if (!string.Equals(_currentSettingsSection, targetSection, StringComparison.Ordinal))
        {
            NavigateToSettingsSection(targetSection);
        }
    }

    private void CustomWidgetsApiDocsButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureSettingsSectionCreated("CustomWidgetSettings");
        CustomWidgetSettingsSection.TriggerOpenApiDocs();
    }

    private void CustomWidgetsImportButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureSettingsSectionCreated("CustomWidgetSettings");
        CustomWidgetSettingsSection.TriggerImportPackage();
    }

    private void CustomWidgetsQuickCreateButton_Click(object sender, RoutedEventArgs e)
    {
        EnsureSettingsSectionCreated("CustomWidgetSettings");
        CustomWidgetSettingsSection.OpenStudio();
    }

    private void ResetInSectionSubPagesOnExit(string previousSectionTag, string nextSectionTag)
    {
        if (string.Equals(previousSectionTag, nextSectionTag, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(previousSectionTag, "Appearance", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("Appearance", out FrameworkElement? prevAppearanceEl) &&
            prevAppearanceEl is AppearanceSettingsSection prevAppearanceSection)
        {
            prevAppearanceSection.CloseMaterialSubPage(animate: false);
        }

        if (string.Equals(previousSectionTag, "Interaction", StringComparison.Ordinal))
        {
            CloseCapsuleModeSubPage(animate: false);
        }

        if (string.Equals(previousSectionTag, "WidgetGroups", StringComparison.Ordinal))
        {
            CloseWidgetGroupDetailSubPage(animate: false);
        }

        if (string.Equals(previousSectionTag, "AppearanceDetail", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? prevDetailEl) &&
            prevDetailEl is FileWidgetSettingsSection prevDetailSection)
        {
            prevDetailSection.CloseAllSubPages(animate: false);
        }

        if (string.Equals(previousSectionTag, "Maintenance", StringComparison.Ordinal))
        {
            CloseMaintenanceSubPage(animate: false);
        }
    }

    private void ApplyRawSectionSubPageDeepLink(string rawSectionTag)
    {
        switch (rawSectionTag)
        {
            case "AppearanceMaterialSettings":
                if (_settingsSectionElements.TryGetValue("Appearance", out FrameworkElement? appearanceEl) &&
                    appearanceEl is AppearanceSettingsSection appearanceSection)
                {
                    appearanceSection.OpenMaterialSubPage(animate: false);
                }
                break;
            case "CapsuleMode":
            case "CapsuleOverridesSettings":
                OpenCapsuleModeSubPage(animate: false);
                break;
            case "FileDisplaySettings":
                if (_settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? fileDisplayEl) &&
                    fileDisplayEl is FileWidgetSettingsSection fileDisplaySection)
                {
                    fileDisplaySection.OpenFileDisplaySubPage(animate: false);
                }
                break;
            case "DesktopOrganizationSettings":
                if (_settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? orgEl) &&
                    orgEl is FileWidgetSettingsSection orgSection)
                {
                    orgSection.OrganizationSection.OpenRuleList(animate: false);
                }
                break;
            case "BackupRestoreSettings":
                OpenMaintenanceSubPage(MaintenanceSubPageKind.AutomaticBackup, animate: false);
                break;
            case "CloudBackupSettings":
                OpenMaintenanceSubPage(MaintenanceSubPageKind.CloudBackup, animate: false);
                break;
        }
    }

    private void RevealContainingSettingsSubPage(string targetSectionTag, FrameworkElement target)
    {
        if (string.Equals(targetSectionTag, "Appearance", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("Appearance", out FrameworkElement? appearanceEl) &&
            appearanceEl is AppearanceSettingsSection appearanceSection &&
            appearanceSection.FindName("MaterialSubPagePanel") is DependencyObject materialPanel &&
            IsDescendantOf(target, materialPanel))
        {
            appearanceSection.OpenMaterialSubPage(animate: false);
            return;
        }

        if (string.Equals(targetSectionTag, "Interaction", StringComparison.Ordinal) &&
            CapsuleModeSubPagePanel is { } capsulePanel &&
            IsDescendantOf(target, capsulePanel))
        {
            OpenCapsuleModeSubPage(animate: false);
            return;
        }

        if (string.Equals(targetSectionTag, "AppearanceDetail", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? detailEl) &&
            detailEl is FileWidgetSettingsSection detailSection)
        {
            if (detailSection.FindName("FileDisplaySubPagePanel") is DependencyObject fileDisplayPanel &&
                IsDescendantOf(target, fileDisplayPanel))
            {
                detailSection.OpenFileDisplaySubPage(animate: false);
                return;
            }

            if (detailSection.OrganizationSection.FindName("RuleListPanel") is DependencyObject ruleListPanel &&
                IsDescendantOf(target, ruleListPanel))
            {
                detailSection.OrganizationSection.OpenRuleList(animate: false);
                return;
            }

            if (detailSection.IsDetailSubPageOpen)
            {
                detailSection.CloseAllSubPages(animate: false);
            }
        }

        if (string.Equals(targetSectionTag, "Maintenance", StringComparison.Ordinal))
        {
            if (AutomaticBackupSubPagePanel is { } autoBackupPanel &&
                IsDescendantOf(target, autoBackupPanel))
            {
                OpenMaintenanceSubPage(MaintenanceSubPageKind.AutomaticBackup, animate: false);
                return;
            }

            if (CloudBackupSubPagePanel is { } cloudBackupPanel &&
                IsDescendantOf(target, cloudBackupPanel))
            {
                OpenMaintenanceSubPage(MaintenanceSubPageKind.CloudBackup, animate: false);
            }
        }

        if (string.Equals(targetSectionTag, "CustomWidgetSettings", StringComparison.Ordinal) &&
            _settingsSectionElements.TryGetValue("CustomWidgetSettings", out FrameworkElement? customEl) &&
            customEl is CustomWidgetSettingsSection { IsDetailViewOpen: true } customSection &&
            customSection.FindName("StudioViewPanel") is DependencyObject studioViewPanel &&
            IsDescendantOf(target, studioViewPanel))
        {
            customSection.CloseDetailView(animate: false);
        }
    }

    private static bool IsDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        DependencyObject? current = element;
        while (current is not null)
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private void RefreshCurrentSectionHeaderAndBreadcrumb()
    {
        if (!TryGetSectionRoute(_currentSettingsSection, out SettingsSectionRoute route))
        {
            return;
        }

        bool isSubPageOpen =
            (string.Equals(_currentSettingsSection, "Appearance", StringComparison.Ordinal) &&
             _settingsSectionElements.TryGetValue("Appearance", out FrameworkElement? appEl) &&
             appEl is AppearanceSettingsSection { IsMaterialSubPageOpen: true }) ||
            (string.Equals(_currentSettingsSection, "Interaction", StringComparison.Ordinal) &&
             _isCapsuleModeSubPageOpen) ||
            (string.Equals(_currentSettingsSection, "WidgetGroups", StringComparison.Ordinal) &&
             _activeWidgetGroupId is not null) ||
            (string.Equals(_currentSettingsSection, "AppearanceDetail", StringComparison.Ordinal) &&
             _settingsSectionElements.TryGetValue("AppearanceDetail", out FrameworkElement? detEl) &&
             detEl is FileWidgetSettingsSection { IsDetailSubPageOpen: true }) ||
            (string.Equals(_currentSettingsSection, "CustomWidgetSettings", StringComparison.Ordinal) &&
             _settingsSectionElements.TryGetValue("CustomWidgetSettings", out FrameworkElement? customEl) &&
             customEl is CustomWidgetSettingsSection { IsDetailViewOpen: true }) ||
            (string.Equals(_currentSettingsSection, "Maintenance", StringComparison.Ordinal) &&
             _activeMaintenanceSubPage != MaintenanceSubPageKind.None);

        bool isNestedSection = !string.IsNullOrWhiteSpace(route.ParentTag) || isSubPageOpen;
        SettingsNavigationView.IsBackButtonVisible = isNestedSection
            ? NavigationViewBackButtonVisible.Visible
            : NavigationViewBackButtonVisible.Collapsed;
        UpdateBreadcrumb(route);
    }

    private void CapsuleSettingsViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isClosed)
        {
            return;
        }

        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName == nameof(DeskBox.Features.Capsule.CapsuleSettingsViewModel.AvailableCollapseBehaviorOptions))
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => SyncCapsuleModeEntryCard(reloadOptions: true));
                return;
            }
            SyncCapsuleModeEntryCard(reloadOptions: true);
        }
        else if (e.PropertyName is
            nameof(DeskBox.Features.Capsule.CapsuleSettingsViewModel.CollapseBehavior) or
            nameof(DeskBox.Features.Capsule.CapsuleSettingsViewModel.CollapseSummaryText))
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(() => SyncCapsuleModeEntryCard());
                return;
            }
            SyncCapsuleModeEntryCard();
        }
    }

    private void SyncCapsuleModeEntryCard(bool reloadOptions = false)
    {
        if (CapsuleModeEntryCard is not { } card ||
            CapsuleModeEntryComboBox is not { } comboBox)
        {
            return;
        }

        _isSyncingCapsuleModeEntry = true;
        try
        {
            card.Description = _capsuleSettingsViewModel.CollapseSummaryText;
            if (reloadOptions || _capsuleEntryOptions is null || !ReferenceEquals(comboBox.ItemsSource, _capsuleEntryOptions))
            {
                _capsuleEntryOptions = _capsuleSettingsViewModel.AvailableCollapseBehaviorOptions;
                comboBox.ItemsSource = _capsuleEntryOptions;
            }

            SettingsOption? selected = _capsuleEntryOptions
                .FirstOrDefault(option => Equals(option.Value, _capsuleSettingsViewModel.CollapseBehavior));
            if (!ReferenceEquals(comboBox.SelectedItem, selected))
            {
                comboBox.SelectedItem = selected;
            }
        }
        finally
        {
            _isSyncingCapsuleModeEntry = false;
        }
    }

    private void CapsuleModeEntryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncingCapsuleModeEntry ||
            CapsuleModeEntryComboBox is not { SelectedItem: SettingsOption { Value: string collapseBehavior } })
        {
            return;
        }

        _capsuleSettingsViewModel.CollapseBehavior = collapseBehavior;
    }

    private void CapsuleModeEntryCard_Click(object sender, RoutedEventArgs e)
    {
        OpenCapsuleModeSubPage(animate: true);
    }

    private void OpenCapsuleModeSubPage(bool animate)
    {
        if (InteractionOverviewPanel is not { } overviewPanel ||
            CapsuleModeSubPagePanel is not { } subPagePanel)
        {
            return;
        }

        bool wasAlreadyOpen = _isCapsuleModeSubPageOpen;
        _isCapsuleModeSubPageOpen = true;
        overviewPanel.Visibility = Visibility.Collapsed;
        subPagePanel.Visibility = Visibility.Visible;
        ApplySegmentedNeutralStates(subPagePanel);
        PageScroller.ChangeView(null, 0d, null, disableAnimation: true);
        RefreshCurrentSectionHeaderAndBreadcrumb();
        if (animate && !wasAlreadyOpen && SettingsRoot.IsLoaded)
        {
            subPagePanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(subPagePanel, NavigationEnterOffsetPx);
        }
    }

    private void CloseCapsuleModeSubPage(bool animate)
    {
        if (!_isCapsuleModeSubPageOpen && CapsuleModeSubPagePanel is not { Visibility: Visibility.Visible })
        {
            return;
        }

        _isCapsuleModeSubPageOpen = false;
        if (CapsuleModeSubPagePanel is { } subPagePanel)
        {
            subPagePanel.Visibility = Visibility.Collapsed;
        }
        if (InteractionOverviewPanel is { } overviewPanel)
        {
            overviewPanel.Visibility = Visibility.Visible;
        }

        SyncCapsuleModeEntryCard();
        if (string.Equals(_currentSettingsSection, "Interaction", StringComparison.Ordinal))
        {
            PageScroller.ChangeView(null, 0d, null, disableAnimation: true);
            RefreshCurrentSectionHeaderAndBreadcrumb();
            if (animate && SettingsRoot.IsLoaded && InteractionOverviewPanel is { } visibleOverview)
            {
                visibleOverview.UpdateLayout();
                DetailPageTransitionHelper.PlayNavigationEnter(visibleOverview, -NavigationEnterOffsetPx);
            }
        }
    }

    private bool TryCloseCapsuleModeSubPage()
    {
        if (!_isCapsuleModeSubPageOpen)
        {
            return false;
        }

        CloseCapsuleModeSubPage(animate: true);
        return true;
    }

    private void BackupSettingsViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isClosed)
        {
            return;
        }

        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName is
                nameof(DeskBox.Features.Backup.BackupSettingsViewModel.LocalBackupEnabled) or
                nameof(DeskBox.Features.Backup.BackupSettingsViewModel.CloudBackupEnabled) or
                nameof(DeskBox.Features.Backup.BackupSettingsViewModel.StatusText))
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(SyncMaintenanceEntryCards);
                return;
            }
            SyncMaintenanceEntryCards();
        }
    }

    private void SyncMaintenanceEntryCards()
    {
        _isSyncingMaintenanceEntries = true;
        try
        {
            if (AutomaticBackupEntryToggle is { } autoToggle)
            {
                autoToggle.IsOn = _backupSettingsViewModel.LocalBackupEnabled;
                ClearToggleSwitchContent(autoToggle);
            }

            if (CloudBackupEntryCard is { } cloudCard)
            {
                cloudCard.Description = _backupSettingsViewModel.StatusText;
            }

            if (CloudBackupEntryToggle is { } cloudToggle)
            {
                cloudToggle.IsOn = _backupSettingsViewModel.CloudBackupEnabled;
                ClearToggleSwitchContent(cloudToggle);
            }
        }
        finally
        {
            _isSyncingMaintenanceEntries = false;
        }
    }

    private void AutomaticBackupEntryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isSyncingMaintenanceEntries || sender is not ToggleSwitch toggle)
        {
            return;
        }

        _backupSettingsViewModel.LocalBackupEnabled = toggle.IsOn;
    }

    private void CloudBackupEntryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isSyncingMaintenanceEntries || sender is not ToggleSwitch toggle)
        {
            return;
        }

        _backupSettingsViewModel.CloudBackupEnabled = toggle.IsOn;
    }

    private void AutomaticBackupEntryCard_Click(object sender, RoutedEventArgs e)
    {
        OpenMaintenanceSubPage(MaintenanceSubPageKind.AutomaticBackup, animate: true);
    }

    private void CloudBackupEntryCard_Click(object sender, RoutedEventArgs e)
    {
        OpenMaintenanceSubPage(MaintenanceSubPageKind.CloudBackup, animate: true);
    }

    private void OpenMaintenanceSubPage(MaintenanceSubPageKind kind, bool animate)
    {
        if (kind == MaintenanceSubPageKind.None ||
            MaintenanceOverviewPanel is not { } overviewPanel ||
            AutomaticBackupSubPagePanel is not { } autoPanel ||
            CloudBackupSubPagePanel is not { } cloudPanel)
        {
            return;
        }

        bool wasAlreadyOpen = _activeMaintenanceSubPage == kind;
        _activeMaintenanceSubPage = kind;
        overviewPanel.Visibility = Visibility.Collapsed;
        autoPanel.Visibility = kind == MaintenanceSubPageKind.AutomaticBackup
            ? Visibility.Visible
            : Visibility.Collapsed;
        cloudPanel.Visibility = kind == MaintenanceSubPageKind.CloudBackup
            ? Visibility.Visible
            : Visibility.Collapsed;
        PageScroller.ChangeView(null, 0d, null, disableAnimation: true);
        RefreshCurrentSectionHeaderAndBreadcrumb();
        if (animate && !wasAlreadyOpen && SettingsRoot.IsLoaded)
        {
            StackPanel targetPanel = kind == MaintenanceSubPageKind.AutomaticBackup ? autoPanel : cloudPanel;
            targetPanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(targetPanel, NavigationEnterOffsetPx);
        }
    }

    private void CloseMaintenanceSubPage(bool animate)
    {
        if (_activeMaintenanceSubPage == MaintenanceSubPageKind.None &&
            AutomaticBackupSubPagePanel is not { Visibility: Visibility.Visible } &&
            CloudBackupSubPagePanel is not { Visibility: Visibility.Visible })
        {
            return;
        }

        _activeMaintenanceSubPage = MaintenanceSubPageKind.None;
        if (AutomaticBackupSubPagePanel is { } autoPanel)
        {
            autoPanel.Visibility = Visibility.Collapsed;
        }
        if (CloudBackupSubPagePanel is { } cloudPanel)
        {
            cloudPanel.Visibility = Visibility.Collapsed;
        }
        if (MaintenanceOverviewPanel is { } overviewPanel)
        {
            overviewPanel.Visibility = Visibility.Visible;
        }

        SyncMaintenanceEntryCards();
        if (string.Equals(_currentSettingsSection, "Maintenance", StringComparison.Ordinal))
        {
            PageScroller.ChangeView(null, 0d, null, disableAnimation: true);
            RefreshCurrentSectionHeaderAndBreadcrumb();
            if (animate && SettingsRoot.IsLoaded && MaintenanceOverviewPanel is { } visibleOverview)
            {
                visibleOverview.UpdateLayout();
                DetailPageTransitionHelper.PlayNavigationEnter(visibleOverview, -NavigationEnterOffsetPx);
            }
        }
    }

    private bool TryCloseMaintenanceSubPage()
    {
        if (_activeMaintenanceSubPage == MaintenanceSubPageKind.None)
        {
            return false;
        }

        CloseMaintenanceSubPage(animate: true);
        return true;
    }

    private void GroupNavigationSettingsViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isClosed)
        {
            return;
        }

        if (string.IsNullOrEmpty(e.PropertyName) ||
            e.PropertyName == nameof(DeskBox.Features.GroupNavigation.GroupNavigationSettingsViewModel.ExistingGroups))
        {
            if (!DispatcherQueue.HasThreadAccess)
            {
                DispatcherQueue.TryEnqueue(SyncActiveWidgetGroupDetail);
                return;
            }
            SyncActiveWidgetGroupDetail();
        }
    }

    private void WidgetGroupItemCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: WidgetGroupSettingsItem groupItem })
        {
            OpenWidgetGroupDetailSubPage(groupItem.GroupId, animate: true);
        }
    }

    private void OpenWidgetGroupDetailSubPage(string groupId, bool animate)
    {
        WidgetGroupSettingsItem? groupItem = _groupNavigationSettingsViewModel.ExistingGroups
            .FirstOrDefault(item => string.Equals(item.GroupId, groupId, StringComparison.Ordinal));
        if (groupItem is null ||
            WidgetGroupsOverviewPanel is not { } overviewPanel ||
            WidgetGroupDetailSubPagePanel is not { } detailPanel ||
            WidgetGroupDetailPresenter is not { } presenter)
        {
            return;
        }

        bool wasAlreadyOpen = string.Equals(_activeWidgetGroupId, groupId, StringComparison.Ordinal) &&
            detailPanel.Visibility == Visibility.Visible;
        _activeWidgetGroupId = groupId;
        presenter.Content = groupItem;
        overviewPanel.Visibility = Visibility.Collapsed;
        detailPanel.Visibility = Visibility.Visible;
        PageScroller.ChangeView(null, 0d, null, disableAnimation: true);
        RefreshCurrentSectionHeaderAndBreadcrumb();
        if (animate && !wasAlreadyOpen && SettingsRoot.IsLoaded)
        {
            detailPanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(detailPanel, NavigationEnterOffsetPx);
        }
    }

    private void CloseWidgetGroupDetailSubPage(bool animate)
    {
        if (_activeWidgetGroupId is null && WidgetGroupDetailSubPagePanel is not { Visibility: Visibility.Visible })
        {
            return;
        }

        _activeWidgetGroupId = null;
        if (WidgetGroupDetailPresenter is { } presenter)
        {
            presenter.Content = null;
        }
        if (WidgetGroupDetailSubPagePanel is { } detailPanel)
        {
            detailPanel.Visibility = Visibility.Collapsed;
        }
        if (WidgetGroupsOverviewPanel is { } overviewPanel)
        {
            overviewPanel.Visibility = Visibility.Visible;
        }

        if (string.Equals(_currentSettingsSection, "WidgetGroups", StringComparison.Ordinal))
        {
            PageScroller.ChangeView(null, 0d, null, disableAnimation: true);
            RefreshCurrentSectionHeaderAndBreadcrumb();
            if (animate && SettingsRoot.IsLoaded && WidgetGroupsOverviewPanel is { } visibleOverview)
            {
                visibleOverview.UpdateLayout();
                DetailPageTransitionHelper.PlayNavigationEnter(visibleOverview, -NavigationEnterOffsetPx);
            }
        }
    }

    private bool TryCloseWidgetGroupDetailSubPage()
    {
        if (_activeWidgetGroupId is null)
        {
            return false;
        }

        CloseWidgetGroupDetailSubPage(animate: true);
        return true;
    }

    private void SyncActiveWidgetGroupDetail()
    {
        if (_activeWidgetGroupId is not string groupId)
        {
            return;
        }

        WidgetGroupSettingsItem? updatedItem = _groupNavigationSettingsViewModel.ExistingGroups
            .FirstOrDefault(item => string.Equals(item.GroupId, groupId, StringComparison.Ordinal));
        if (updatedItem is null)
        {
            CloseWidgetGroupDetailSubPage(animate: string.Equals(_currentSettingsSection, "WidgetGroups", StringComparison.Ordinal));
            return;
        }

        if (WidgetGroupDetailPresenter is { } presenter)
        {
            presenter.Content = updatedItem;
        }

        if (string.Equals(_currentSettingsSection, "WidgetGroups", StringComparison.Ordinal))
        {
            RefreshCurrentSectionHeaderAndBreadcrumb();
        }
    }

    private void NestedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sectionTag })
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

    private void WidgetGroupRenameTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (e.Key == VirtualKey.Enter &&
            sender is TextBox { Tag: string groupId } textBox)
        {
            ViewModel.RenameWidgetGroup(groupId, textBox.Text);
            e.Handled = true;
        }
    }

    private void SaveWidgetGroupRenameButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string groupId, Parent: Panel parent } &&
            parent.Children.OfType<TextBox>().FirstOrDefault() is { } textBox)
        {
            ViewModel.RenameWidgetGroup(groupId, textBox.Text);
        }
    }

    private void ResetWidgetGroupOverrideButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string groupId })
        {
            ViewModel.ResetWidgetGroupOverrides(groupId);
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
            })
        {
            return;
        }

        if (await _widgetActions.ReorderWidgetGroupMemberAsync(
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
            })
        {
            return;
        }

        if (await _widgetActions.ReorderWidgetGroupMemberAsync(
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
            })
        {
            return;
        }

        if (await _widgetActions.RemoveWidgetFromGroupAsync(member.WidgetId))
        {
            ViewModel.RefreshWidgetGroupSettings();
        }
    }

    private async void DissolveWidgetGroupButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string memberId } ||
            SettingsRoot.XamlRoot is null)
        {
            return;
        }

        ContentDialogResult choice = await ShowDialogWhenFreeAsync(() =>
            SettingsRoot.XamlRoot is null
                ? null
                : new ContentDialog
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
                });

        if (choice != ContentDialogResult.Primary)
        {
            return;
        }

        if (await _widgetActions.DissolveWidgetGroupContainingAsync(memberId))
        {
            ViewModel.RefreshWidgetGroupSettings();
        }
    }

    private void AddFileStackRuleButton_Click(object sender, RoutedEventArgs e)
    {
        _fileStackSettingsViewModel.AddRule();
    }

    private void RemoveFileStackRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FileStackCustomRuleEditor editor })
        {
            _fileStackSettingsViewModel.RemoveRule(editor);
        }
    }

    private void MoveFileStackRuleUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FileStackCustomRuleEditor editor })
        {
            _fileStackSettingsViewModel.MoveRule(editor, -1);
        }
    }

    private void MoveFileStackRuleDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: FileStackCustomRuleEditor editor })
        {
            _fileStackSettingsViewModel.MoveRule(editor, 1);
        }
    }

    private void FileStackRulesListView_Loaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is ListView listView &&
            DragDropPermissionService.IsCurrentProcessElevatedCached())
        {
            // WinUI's ListView drag/reorder throws inside an elevated process;
            // Up/Down rule buttons remain available.
            listView.CanDragItems = false;
            listView.CanReorderItems = false;
        }
    }

    private void FileStackRulesListView_DragItemsCompleted(
        ListViewBase sender,
        DragItemsCompletedEventArgs args)
    {
        _fileStackSettingsViewModel.CommitRuleOrder();
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

    // Music / Weather in-page master switches (batch 1 gating fix). These
    // widgets' editors have no Enabled surface (unlike Quick Capture/Todo),
    // so the switch state is owned here: optimistic write through the shell
    // facade, reconciled on the settings-reload push.
    private bool _isSyncingMusicWeatherFeatureEnable;



    private void MusicWeatherFeatureEnableToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isSyncingMusicWeatherFeatureEnable ||
            sender is not ToggleSwitch { Tag: string kindTag } toggle ||
            !Enum.TryParse(kindTag, out WidgetKind kind) ||
            kind is not (WidgetKind.Music or WidgetKind.Weather))
        {
            return;
        }

        ViewModel.SetWidgetEnabled(kind, toggle.IsOn);
        string sectionTag = kind == WidgetKind.Music ? "MusicSettings" : "WeatherSettings";
        if (ResolveMusicWeatherFeatureElements(sectionTag).Body is { } body)
        {
            SetPanelChildrenEnabled(body, toggle.IsOn);
        }
    }

    private void SyncMusicWeatherFeatureEnableState(string sectionTag)
    {
        (ToggleSwitch? toggle, StackPanel? body) = ResolveMusicWeatherFeatureElements(sectionTag);
        if (toggle is null || body is null)
        {
            return;
        }

        WidgetKind kind = sectionTag == "MusicSettings" ? WidgetKind.Music : WidgetKind.Weather;
        bool enabled = ViewModel.IsWidgetEnabled(kind);
        _isSyncingMusicWeatherFeatureEnable = true;
        try
        {
            toggle.IsOn = enabled;
        }
        finally
        {
            _isSyncingMusicWeatherFeatureEnable = false;
        }

        SetPanelChildrenEnabled(body, enabled);
    }

    // Panels are not Controls (no IsEnabled of their own); gate the direct
    // card children instead — same semantics as the Quick Capture per-card
    // bindings.
    private static void SetPanelChildrenEnabled(Panel panel, bool enabled)
    {
        foreach (UIElement child in panel.Children)
        {
            if (child is Control control)
            {
                control.IsEnabled = enabled;
            }
            else
            {
                child.Opacity = enabled ? 1.0 : 0.45;
            }
        }
    }

    private (ToggleSwitch? Toggle, StackPanel? Body) ResolveMusicWeatherFeatureElements(string sectionTag)
    {
        if (!_settingsSectionElements.TryGetValue(sectionTag, out FrameworkElement? section))
        {
            return (null, null);
        }

        bool isMusic = sectionTag == "MusicSettings";
        return (
            section.FindName(isMusic ? "MusicFeatureEnableToggle" : "WeatherFeatureEnableToggle") as ToggleSwitch,
            section.FindName(isMusic ? "MusicSettingsBodyPanel" : "WeatherSettingsBodyPanel") as StackPanel);
    }

    private void QuickCaptureTabsDropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        // The tab-visibility flyout state machine lives on the Quick Capture
        // section editor (batch 46); the section reaches it through its
        // DataContext.
        var quickCaptureSettings = _quickCaptureSettingsViewModel;
        SettingsMultiSelectMenu.Show(
            button,
            quickCaptureSettings.AvailableDefaultViews,
            quickCaptureSettings.GetDefaultViewDisplayName,
            quickCaptureSettings.IsTabSelected,
            quickCaptureSettings.CanToggleTab,
            quickCaptureSettings.ToggleTab);
    }

    private void TodoTabsDropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        // The tab-visibility flyout state machine lives on the Todo section
        // editor (batch 47); the section reaches it through its DataContext.
        var todoSettings = _todoSettingsViewModel;
        SettingsMultiSelectMenu.Show(
            button,
            todoSettings.AvailableDefaultFilters,
            todoSettings.GetTabDisplayName,
            todoSettings.IsTabSelected,
            todoSettings.CanToggleTab,
            todoSettings.ToggleTab);
    }

    private void TodoFooterDisplayDropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        var todoSettings = _todoSettingsViewModel;
        SettingsMultiSelectMenu.Show(
            button,
            todoSettings.AvailableFooterDisplayOptions,
            todoSettings.GetFooterDisplayOptionName,
            todoSettings.IsFooterDisplayOptionSelected,
            _ => true,
            todoSettings.ToggleFooterDisplayOption);
    }

    private void QuickCaptureResetTextSizeButton_Click(object sender, RoutedEventArgs e) =>
        _quickCaptureSettingsViewModel.ResetTextSizesToGlobal();

    private void TodoResetTextSizeButton_Click(object sender, RoutedEventArgs e) =>
        _todoSettingsViewModel.ResetTextSizesToGlobal();

    // ── Deferred-notice InfoBar severity brushes ────────────────

    private global::Microsoft.UI.Xaml.Controls.InfoBar? Windows10CompatibilityInfoBar =>
        _settingsSectionElements.TryGetValue("Appearance", out FrameworkElement? el)
            ? el.FindName("Windows10CompatibilityInfoBar") as global::Microsoft.UI.Xaml.Controls.InfoBar
            : null;

    private global::Microsoft.UI.Xaml.Controls.InfoBar? CloudBackupSyncNoticeInfoBar =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.InfoBar>(
            "Maintenance", "CloudBackupSyncNoticeInfoBar");

    private global::Microsoft.UI.Xaml.Controls.InfoBar? StoreUninstallDataNoticeInfoBar =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.InfoBar>(
            "Maintenance", "StoreUninstallDataNoticeInfoBar");

    private void PrepareDeferredNoticeInfoBars()
    {
        // ThemeResource severity brushes inside the InfoBar template fail to
        // resolve for sections realized through DataTemplate.LoadContent
        // (the same delayed-creation pitfall as the card brushes), leaving
        // the notices painted exactly like the card behind them. Pin the
        // informational severity brushes from the application resources;
        // null-guarded because each template only populates its field once
        // its section has been created.
        PinInformationalSeverityBrushes(Windows10CompatibilityInfoBar);
        PinInformationalSeverityBrushes(StoreUninstallDataNoticeInfoBar);
        ApplyNoticeMessageTypography(CloudBackupSyncNoticeInfoBar);
        ApplyNoticeMessageTypography(Windows10CompatibilityInfoBar);
        ApplyNoticeMessageTypography(AboutStoreNoticeInfoBar);
        ApplyNoticeMessageTypography(StoreUninstallDataNoticeInfoBar);
    }

    private void PinInformationalSeverityBrushes(InfoBar? infoBar)
    {
        if (infoBar is null)
        {
            return;
        }

        ApplyInformationalSeverityBrushes(infoBar);
        if (_pinnedNoticeInfoBars.Add(infoBar))
        {
            // Local brush values do not follow theme swaps; re-pin when the
            // realized section's theme flips.
            infoBar.ActualThemeChanged += (_, _) => ApplyInformationalSeverityBrushes(infoBar);
        }
    }

    // Mirrors the library's InfoBarInformationalSeverity background
    // (Microsoft.WinUI generic.xaml): Light = SystemFillColorAttentionBackground
    // #80F6F6F6, Default/Dark = #08FFFFFF, HighContrast = SystemColorWindow.
    // WinRT activations stay inside the method (never static initializers):
    // headless test hosts touch this type without WinAppSDK activation.
    private static Windows.UI.Color NoticeInfoBackgroundLight =>
        Windows.UI.Color.FromArgb(0x80, 0xF6, 0xF6, 0xF6);
    private static Windows.UI.Color NoticeInfoBackgroundDark =>
        Windows.UI.Color.FromArgb(0x08, 0xFF, 0xFF, 0xFF);

    internal static void ApplyInformationalSeverityBrushes(InfoBar infoBar)
    {
        // Resolve per the notice's ActualTheme, never from
        // Application.Current.Resources: that lookup serves themed keys from
        // a stale dictionary on Windows 10 after a live theme switch, which
        // painted the light informational wash over the dark theme (the
        // Windows-11-only-correct mismatch). The template's default text
        // colors already follow the theme, so only the background is pinned.
        Windows.UI.Color background = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast
            ? new Windows.UI.ViewManagement.UISettings().GetColorValue(
                Windows.UI.ViewManagement.UIColorType.Background)
            : infoBar.ActualTheme == ElementTheme.Dark
                ? NoticeInfoBackgroundDark
                : NoticeInfoBackgroundLight;
        infoBar.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(background);
    }

    // ── Notice InfoBar message typography ───────────────────────

    // The InfoBar control template reads its Title/Message font sizes
    // through StaticResource lookups that resolve inside the control
    // library's generic.xaml, so neither element-local resource overrides
    // nor implicit text styles can reach those two text blocks (unlike
    // the severity brushes, which are ThemeResource values pinned above).
    // Size the message text block down to the settings-card description
    // spec directly once the template has been applied; the title keeps
    // the template default (14px semi-bold). Shared with the search
    // section's notices.
    internal static void ApplyNoticeMessageTypography(InfoBar? infoBar)
    {
        if (infoBar is null)
        {
            return;
        }

        if (infoBar.IsLoaded)
        {
            SetNoticeMessageTypography(infoBar);
            return;
        }

        // Deferred sections are created before they join the visual tree;
        // wait for the first Loaded (template applied) and detach after.
        infoBar.Loaded -= OnNoticeLoadedForTypography;
        infoBar.Loaded += OnNoticeLoadedForTypography;
    }

    private static void OnNoticeLoadedForTypography(object sender, RoutedEventArgs e)
    {
        if (sender is not InfoBar infoBar)
        {
            return;
        }

        infoBar.Loaded -= OnNoticeLoadedForTypography;
        SetNoticeMessageTypography(infoBar);
    }

    private static void SetNoticeMessageTypography(InfoBar infoBar)
    {
        if (TryApplyNoticeMessageTypography(infoBar))
        {
            return;
        }

        // Loaded fires before the first layout pass, so the template parts
        // (Title/Message text blocks) may not exist in the visual tree yet;
        // retry from the first LayoutUpdated, which runs after ApplyTemplate.
        EventHandler<object> retry = null!;
        retry = (_, _) =>
        {
            if (TryApplyNoticeMessageTypography(infoBar))
            {
                infoBar.LayoutUpdated -= retry;
            }
        };
        infoBar.LayoutUpdated += retry;
    }

    private static bool TryApplyNoticeMessageTypography(InfoBar infoBar)
    {
        if (FindNamedDescendant(infoBar, "Message") is not TextBlock message)
        {
            return false;
        }

        message.FontSize = 12;
        return true;
    }

    private static FrameworkElement? FindNamedDescendant(DependencyObject root, string name)
    {
        int childCount = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < childCount; index++)
        {
            if (VisualTreeHelper.GetChild(root, index) is not FrameworkElement child)
            {
                continue;
            }

            if (child.Name == name)
            {
                return child;
            }

            if (FindNamedDescendant(child, name) is { } match)
            {
                return match;
            }
        }

        return null;
    }
}
