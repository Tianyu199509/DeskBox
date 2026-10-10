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
    private FrameworkElement? _settingsSearchHighlightTarget;
    private double _settingsSearchHighlightOriginalOpacity = 1;
    private readonly List<SettingsExpander> _featureSettingsExpanders = [];
    private readonly Dictionary<SettingsExpander, long> _featureSettingsExpanderCallbacks = [];
    private bool _isSynchronizingFeatureSettingsExpanders;
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
        // FeatureWidgets family
        "QuickCaptureSettings", "TodoSettings", "MusicSettings",
        "WeatherSettings", "GlanceSettings", "SearchSettings",
        // Appearance family
        "AppearanceMaterialSettings", "AppearanceDensitySettings",
        "AppearanceWindowSettings", "AppearanceAnimationSettings",
        // AppearanceDetail family
        "FileDisplaySettings", "ManagedStorage", "FileStackSettings",
        "DesktopOrganizationSettings",
        // Maintenance family
        "BackupRestoreSettings", "CloudBackupSettings", "DataHealthSettings",
        "CompatibilityDiagnosticsSettings",
        // CapsuleMode family
        "CapsuleOverridesSettings",
        // General family
        "PerformanceSettings", "Displays"
    ];

    private void InitializeSettingsSectionElements()
    {
        _settingsSectionElements.Add("General", GeneralSection);
        string[] missingRoutes = SectionRoutes.Keys
            .Where(tag => tag is not "Advanced" and not "General" &&
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
        foreach (SettingsSearchCatalogEntry entry in SettingsSearchCatalog.Entries)
        {
            // Store-channel filter (route 1): the entries flagged by the
            // policy only have UI in the direct-installer build, so their
            // search results would dead-end in Store builds. The key set is
            // pinned against the catalog by SettingsSearchChannelPolicyTests.
            if (SettingsSearchChannelPolicy.IsHiddenInStore(entry.HeaderKey) &&
                AppDistributionService.Current.IsMicrosoftStore)
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

    private void SettingsSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
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
        sender.Text = string.Empty;
        UpdateSettingsSearchSuggestions(string.Empty);
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
            ExpandSettingsSearchTargetAncestors(target);
            section.UpdateLayout();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_isClosed || navigationGeneration != _settingsNavigationGeneration || target.XamlRoot is null)
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
            _settingsSearchHighlightTarget = null;
        };
        _settingsSearchHighlightCompletedHandler = completedHandler;
        storyboard.Completed += completedHandler;
        _settingsSearchHighlightStoryboard = storyboard;
        storyboard.Begin();
    }

    private void ClearSettingsSearchHighlight()
    {
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
        }

        _settingsSearchHighlightStoryboard = null;
        _settingsSearchHighlightCompletedHandler = null;
        _settingsSearchHighlightTarget = null;
        _settingsSearchHighlightOriginalOpacity = 1;
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
        EnsureSettingsSectionCreated("GlanceSettings");
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
        int navigationGeneration = ++_settingsNavigationGeneration;
        ClearSettingsSearchHighlight();
        string visibleSectionTag = sectionTag == "Advanced" ? "Interaction" : sectionTag;
        EnsureSettingsSectionCreated(visibleSectionTag);
        string? inlineSectionTag = sectionTag switch
        {
            "AppearanceDetail" => "FileStorageSettings",
            "Interaction" or "Advanced" => "InteractionWindowSettings",
            "Maintenance" => "ResetSettings",
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

        // Reset scrolling before the enter transition starts: a page sliding
        // in at a stale scroll offset would visibly jump to the top mid-slide.
        // Synchronous (rather than the low-priority dispatch below) so the
        // first animation frame is already rendered at offset zero.
        PageScroller.ChangeView(null, 0, null, disableAnimation: true);

        PlaySettingsSectionEnterTransition(visibleSectionTag, previousSectionTag, sectionTag, inlineSectionTag);

        if (sectionTag == "FileStackSettings")
        {
            _ = ViewModel.RefreshFileStackRulePreviewFromDiskAsync();
        }
        if (sectionTag == "DesktopOrganizationSettings")
        {
            DesktopOrganizationSettingsSection.Refresh();
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
            ViewModel.RefreshQuickCaptureClipboardDiagnostics();
            _ = ViewModel.RefreshQuickCaptureImageCacheInfoAsync();
        }
        UpdateSearchSettingsActivity();
        UpdateBackupSettingsActivity();
        if (sectionTag == "GlanceSettings")
        {
            _ = GlanceSettingsSection.RefreshFromStoreAsync();
        }
        if (sectionTag == "CloudBackupSettings")
        {
            _ = InitializeCloudBackupSectionAsync();
        }
        if (sectionTag == "ManagedStorage")
        {
            RefreshManagedStorageFolderList();
        }
        else if (sectionTag == "FileStorageSettings")
        {
            RefreshManagedStorageDesktopShortcutState();
            _ = ViewModel.RefreshQuickAccessStateAsync();
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
            _backupSettingsViewModel.RefreshLocalStatus();
            _ = RefreshBackupSnapshotInventoryAsync();
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

        int subIndex = route.ParentTag is null ? 0 : IndexOfTag(subSectionTagOrder, sectionTag);
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

    private void ContinuousDecorativeAnimationsDropDown_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        // The decorative-animation flyout's selection surface lives on the
        // performance editor (batch 50); the section reaches it through its
        // DataContext.
        var performanceSettings = _performanceSettingsViewModel;
        SettingsMultiSelectMenu.Show(
            button,
            performanceSettings.AvailableContinuousDecorativeAnimationOptions,
            performanceSettings.GetContinuousDecorativeAnimationDisplayName,
            performanceSettings.IsContinuousDecorativeAnimationSelected,
            _ => true,
            performanceSettings.ToggleContinuousDecorativeAnimation);
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

    // ── Deferred-notice InfoBar severity brushes ────────────────

    // Accessors live here rather than SectionElements.cs so the notice
    // pinning stays self-contained; both lookups follow the established
    // FindCreatedSectionElement deferred-name pattern (never forces
    // section creation, returns null before the section exists).
    private global::Microsoft.UI.Xaml.Controls.InfoBar? GlobalHotkeyElevatedNoticeInfoBar =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.InfoBar>(
            "InteractionWindowSettings", "GlobalHotkeyElevatedNoticeInfoBar");

    private global::Microsoft.UI.Xaml.Controls.InfoBar? Windows10CompatibilityInfoBar =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.InfoBar>(
            "AppearanceMaterialSettings", "Windows10CompatibilityInfoBar");

    private global::Microsoft.UI.Xaml.Controls.InfoBar? CloudBackupSyncNoticeInfoBar =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.InfoBar>(
            "CloudBackupSettings", "CloudBackupSyncNoticeInfoBar");

    private global::Microsoft.UI.Xaml.Controls.InfoBar? MusicSmtcNoticeInfoBar =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.InfoBar>(
            "MusicSettings", "MusicSmtcNoticeInfoBar");

    private global::Microsoft.UI.Xaml.Controls.InfoBar? StoreUninstallDataNoticeInfoBar =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.InfoBar>(
            "CloudBackupSettings", "StoreUninstallDataNoticeInfoBar");

    private void PrepareDeferredNoticeInfoBars()
    {
        // ThemeResource severity brushes inside the InfoBar template fail to
        // resolve for sections realized through DataTemplate.LoadContent
        // (the same delayed-creation pitfall as the card brushes), leaving
        // the notices painted exactly like the card behind them. Pin the
        // informational severity brushes from the application resources;
        // null-guarded because each template only populates its field once
        // its section has been created.
        PinInformationalSeverityBrushes(GlobalHotkeyElevatedNoticeInfoBar);
        PinInformationalSeverityBrushes(Windows10CompatibilityInfoBar);
        PinInformationalSeverityBrushes(MusicSmtcNoticeInfoBar);
        PinInformationalSeverityBrushes(StoreUninstallDataNoticeInfoBar);
        ApplyNoticeMessageTypography(CloudBackupSyncNoticeInfoBar);
        ApplyNoticeMessageTypography(GlobalHotkeyElevatedNoticeInfoBar);
        ApplyNoticeMessageTypography(Windows10CompatibilityInfoBar);
        ApplyNoticeMessageTypography(AboutStoreNoticeInfoBar);
        ApplyNoticeMessageTypography(MusicSmtcNoticeInfoBar);
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

    internal static void ApplyInformationalSeverityBrushes(InfoBar infoBar)
    {
        if (Application.Current.Resources.TryGetValue(
                "InfoBarInformationalSeverityBackgroundBrush", out object? background) &&
            background is Brush backgroundBrush)
        {
            infoBar.Background = backgroundBrush;
        }
        if (Application.Current.Resources.TryGetValue(
                "InfoBarInformationalSeverityForegroundBrush", out object? foreground) &&
            foreground is Brush foregroundBrush)
        {
            infoBar.Foreground = foregroundBrush;
        }
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
