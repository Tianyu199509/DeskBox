using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeskBox.Views.SettingsSections;

public sealed partial class DesktopOrganizationSettingsSection : UserControl
{
    private static readonly string[] SubtypeOrder =
    [
        DesktopOrganizationSubtypeIds.Pdf,
        DesktopOrganizationSubtypeIds.Word,
        DesktopOrganizationSubtypeIds.Excel,
        DesktopOrganizationSubtypeIds.PowerPoint,
        DesktopOrganizationSubtypeIds.Text,
        DesktopOrganizationSubtypeIds.Audio,
        DesktopOrganizationSubtypeIds.Video
    ];

    private readonly DesktopOrganizationRuleResolver _ruleResolver = new();
    private readonly HashSet<string> _expandedWidgetIds = new(StringComparer.Ordinal);
    private bool _isRefreshing;
    private bool _showByWidgetView;
    private ISettingsWidgetActions? _widgetActions;
    private ISettingsAppActions? _appActions;
    private nint _ownerWindow;

    public event EventHandler? SubPageChanged;

    public bool IsAutoOrganizationSubPageOpen =>
        AutoOrganizationSubPagePanel.Visibility == Visibility.Visible;

    public bool IsDetailSubPageOpen => IsAutoOrganizationSubPageOpen;

    public void Configure(
        ISettingsWidgetActions widgetActions,
        ISettingsAppActions appActions,
        nint ownerWindow)
    {
        _widgetActions = widgetActions;
        _appActions = appActions;
        _ownerWindow = ownerWindow;
    }

    public DesktopOrganizationSettingsSection()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            SettingsWindow.EnsureInformationalSeverityPinned(NoRulesGuidanceInfoBar);
            SettingsWindow.EnsureInformationalSeverityPinned(RuleStatusInfo);
            SettingsWindow.ApplyNoticeMessageTypography(NoRulesGuidanceInfoBar);
            SettingsWindow.ApplyNoticeMessageTypography(RuleStatusInfo);
            Refresh();
        };
        ActualThemeChanged += (_, _) =>
        {
            if (IsLoaded)
            {
                RebuildRuleViews();
            }
        };
    }

    public void Refresh()
    {
        if (TryGetSettingsService() is not { } settingsService)
        {
            return;
        }

        _isRefreshing = true;
        try
        {
            WidgetSearchBox.PlaceholderText = T("DesktopOrganization.Rules.SearchPlaceholder");
            ByTypeExtensionInput.PlaceholderText = T("DesktopOrganization.Rule.ExtensionPlaceholder");
            ByTypeExcludedExtensionInput.PlaceholderText = T("DesktopOrganization.Rule.ExclusionPlaceholder");
            ByTypeExtensionTargetComboBox.PlaceholderText = T("DesktopOrganization.Rules.TargetWidgetPlaceholder");
            ByTypeExcludedTargetComboBox.PlaceholderText = T("DesktopOrganization.Rules.TargetWidgetPlaceholder");

            AppSettings settings = settingsService.Settings;
            List<DesktopOrganizationRule> effectiveRules = settings.DesktopOrganization.DesktopOrganizationRules
                .Where(rule => IsEffectiveRule(rule, settings))
                .ToList();
            int effectiveRuleCount = effectiveRules.Count;
            int configuredWidgetCount = effectiveRules
                .Select(rule => rule.TargetWidgetId)
                .Distinct(StringComparer.Ordinal)
                .Count();
            bool hasEffectiveRules = effectiveRuleCount > 0;
            bool autoEnabled = settings.DesktopOrganization.DesktopAutoOrganizationEnabled;

            // Never lock or gray out the master switch: users can turn it on first
            // and configure rules directly in the 2nd-level page.
            AutoOrganizationEntryToggle.IsEnabled = true;
            AutoOrganizationEntryToggle.IsOn = autoEnabled;
            AutoOrganizationToggle.IsEnabled = true;
            AutoOrganizationToggle.IsOn = autoEnabled;

            AutoOrganizationEntryCard.Description = (autoEnabled, hasEffectiveRules) switch
            {
                (true, true) => Format("DesktopOrganization.Auto.Entry.OnConfigured", configuredWidgetCount, effectiveRuleCount),
                (true, false) => T("DesktopOrganization.Auto.Entry.OnNoRules"),
                (false, true) => Format("DesktopOrganization.Status.Configured", configuredWidgetCount, effectiveRuleCount),
                (false, false) => T("DesktopOrganization.Auto.Entry.OffNoRules")
            };

            bool showNoRulesGuidance = autoEnabled && !hasEffectiveRules;
            NoRulesGuidanceInfoBar.IsOpen = showNoRulesGuidance;
            NoRulesGuidanceInfoBar.Visibility = showNoRulesGuidance
                ? Visibility.Visible
                : Visibility.Collapsed;

            AutoIntervalCard.IsEnabled = autoEnabled;
            RefreshAutoIntervalOptions(settingsService);
            SyncViewTabs();
        }
        finally
        {
            _isRefreshing = false;
        }

        RebuildRuleViews();
    }

    public void OpenAutoOrganizationSubPage(bool animate = true)
    {
        bool wasAlreadyOpen = IsAutoOrganizationSubPageOpen;
        OrganizationOverviewPanel.Visibility = Visibility.Collapsed;
        AutoOrganizationSubPagePanel.Visibility = Visibility.Visible;
        RuleStatusInfo.IsOpen = false;
        ResetHostScrollToTop();
        Refresh();
        SubPageChanged?.Invoke(this, EventArgs.Empty);
        if (animate && !wasAlreadyOpen && IsLoaded)
        {
            AutoOrganizationSubPagePanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(AutoOrganizationSubPagePanel, 40f);
        }
    }

    public void CloseAutoOrganizationSubPage(bool animate = true)
    {
        if (!IsAutoOrganizationSubPageOpen)
        {
            return;
        }

        AutoOrganizationSubPagePanel.Visibility = Visibility.Collapsed;
        OrganizationOverviewPanel.Visibility = Visibility.Visible;
        RuleStatusInfo.IsOpen = false;
        ResetHostScrollToTop();
        Refresh();
        SubPageChanged?.Invoke(this, EventArgs.Empty);
        if (animate && IsLoaded)
        {
            OrganizationOverviewPanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(OrganizationOverviewPanel, -40f);
        }
    }

    public void CloseAllSubPages(bool animate = false)
    {
        if (!IsAutoOrganizationSubPageOpen)
        {
            return;
        }

        AutoOrganizationSubPagePanel.Visibility = Visibility.Collapsed;
        OrganizationOverviewPanel.Visibility = Visibility.Visible;
        RuleStatusInfo.IsOpen = false;
        Refresh();
        SubPageChanged?.Invoke(this, EventArgs.Empty);
        if (animate && IsLoaded)
        {
            OrganizationOverviewPanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(OrganizationOverviewPanel, -40f);
        }
    }

    public bool TryNavigateBack()
    {
        if (!IsAutoOrganizationSubPageOpen)
        {
            return false;
        }

        CloseAutoOrganizationSubPage(animate: true);
        return true;
    }

    private void OrganizeNowButton_Click(object sender, RoutedEventArgs e)
    {
        _appActions?.ShowDesktopOrganizationWindow(_ownerWindow);
    }

    private void AutoOrganizationEntryCard_Click(object sender, RoutedEventArgs e)
    {
        OpenAutoOrganizationSubPage(animate: true);
    }

    private async void AutoOrganizationEntryToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || TryGetSettingsService() is not { } service)
        {
            return;
        }

        bool turningOn = AutoOrganizationEntryToggle.IsOn;
        await ApplyAutoOrganizationToggleAsync(service, turningOn);

        bool hasEffectiveRules = service.Settings.DesktopOrganization.DesktopOrganizationRules
            .Any(rule => IsEffectiveRule(rule, service.Settings));
        if (turningOn && !hasEffectiveRules)
        {
            // Guide the user directly into the 2nd-level page when turned on with 0 rules.
            OpenAutoOrganizationSubPage(animate: true);
            return;
        }

        Refresh();
    }

    private async void AutoOrganizationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || TryGetSettingsService() is not { } service)
        {
            return;
        }

        bool turningOn = AutoOrganizationToggle.IsOn;
        await ApplyAutoOrganizationToggleAsync(service, turningOn);
        Refresh();
    }

    private async Task ApplyAutoOrganizationToggleAsync(SettingsService service, bool enabled)
    {
        service.Settings.DesktopOrganization.DesktopAutoOrganizationEnabled = enabled;
        service.Settings.DesktopOrganization.DesktopAutoOrganizationBaselineUtc = enabled
            ? DateTimeOffset.UtcNow
            : null;
        await service.SaveAsync();
    }

    private void RulesViewSelectorBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_isRefreshing)
        {
            return;
        }

        bool byWidget = sender.SelectedItem is SelectorBarItem { Tag: "ByWidget" };
        if (_showByWidgetView == byWidget)
        {
            return;
        }

        _showByWidgetView = byWidget;
        SyncViewTabs();
        RebuildRuleViews();
    }

    private void SyncViewTabs()
    {
        ByTypeViewTab.IsSelected = !_showByWidgetView;
        ByWidgetViewTab.IsSelected = _showByWidgetView;
    }

    private void RefreshAutoIntervalOptions(SettingsService settingsService)
    {
        AutoIntervalComboBox.Items.Clear();
        int selected = DesktopAutoOrganizationPolicy.NormalizeDelaySeconds(
            settingsService.Settings.DesktopOrganization.DesktopAutoOrganizationDelaySeconds);
        int selectedIndex = 0;
        for (int index = 0;
             index < DesktopAutoOrganizationPolicy.SupportedDelaySeconds.Length;
             index++)
        {
            int seconds = DesktopAutoOrganizationPolicy.SupportedDelaySeconds[index];
            AutoIntervalComboBox.Items.Add(new ComboBoxItem
            {
                Content = GetAutoIntervalDisplayName(seconds),
                Tag = seconds
            });
            if (seconds == selected)
            {
                selectedIndex = index;
            }
        }

        AutoIntervalComboBox.SelectedIndex = selectedIndex;
        AutoIntervalComboBox.IsEnabled = settingsService.Settings.DesktopOrganization.DesktopAutoOrganizationEnabled;
    }

    private static string GetAutoIntervalDisplayName(int seconds) => seconds switch
    {
        DesktopAutoOrganizationPolicy.DefaultDelaySeconds =>
            T("DesktopOrganization.Auto.Interval.Realtime"),
        60 => T("DesktopOrganization.Auto.Interval.Minute"),
        12 * 60 * 60 => Format("DesktopOrganization.Auto.Interval.Hours", 12),
        _ => Format("DesktopOrganization.Auto.Interval.Minutes", seconds / 60)
    };

    private async void AutoIntervalComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshing ||
            TryGetSettingsService() is not { } service ||
            AutoIntervalComboBox.SelectedItem is not ComboBoxItem { Tag: int seconds })
        {
            return;
        }

        service.Settings.DesktopOrganization.DesktopAutoOrganizationDelaySeconds =
            DesktopAutoOrganizationPolicy.NormalizeDelaySeconds(seconds);
        await service.SaveAsync();
    }

    private void WidgetSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            RebuildByWidgetView();
        }
    }

    private void RebuildRuleViews()
    {
        if (TryGetSettingsService() is not { } settingsService)
        {
            return;
        }

        List<WidgetConfig> allFileWidgets = GetActiveFileWidgets(settingsService.Settings);
        bool hasWidgets = allFileWidgets.Count > 0;
        NoFileWidgetsCard.Visibility = hasWidgets ? Visibility.Collapsed : Visibility.Visible;
        ByTypeRulesPanel.Visibility = hasWidgets && !_showByWidgetView ? Visibility.Visible : Visibility.Collapsed;
        ByWidgetRulesPanel.Visibility = hasWidgets && _showByWidgetView ? Visibility.Visible : Visibility.Collapsed;

        if (!hasWidgets)
        {
            return;
        }

        if (_showByWidgetView)
        {
            RebuildByWidgetView();
        }
        else
        {
            RebuildByTypeView();
        }
    }

    // -------------------------------------------------------------------------
    // View 1: By File Type (7 clean category mapping cards + hover ToolTip)
    // -------------------------------------------------------------------------
    private void RebuildByTypeView()
    {
        if (TryGetSettingsService() is not { } settingsService)
        {
            return;
        }

        AppSettings settings = settingsService.Settings;
        List<WidgetConfig> widgets = GetActiveFileWidgets(settings);
        double minWidth = ResolveResourceDouble("SettingsControlMinWidth", 176d);
        double maxWidth = ResolveResourceDouble("SettingsControlMaxWidth", 260d);

        _isRefreshing = true;
        try
        {
            CategoryMappingCards.Children.Clear();
            foreach (string categoryId in DesktopOrganizationCategoryIds.DefaultOrder)
            {
                string capturedCategoryId = categoryId;
                DesktopOrganizationRule? ownerRule = settings.DesktopOrganization.DesktopOrganizationRules
                    .FirstOrDefault(rule =>
                        rule.IsEnabled &&
                        rule.CategoryIds.Contains(capturedCategoryId, StringComparer.Ordinal) &&
                        widgets.Any(w => string.Equals(w.Id, rule.TargetWidgetId, StringComparison.Ordinal)));

                IReadOnlyList<string> extensions = DesktopOrganizationClassifier.GetCategoryExtensions(capturedCategoryId);
                string title = T($"DesktopOrganization.Category.{capturedCategoryId}");
                string subtitle = extensions.Count > 0
                    ? Format("DesktopOrganization.Rules.ExtensionCountHint", extensions.Count)
                    : T("DesktopOrganization.Rules.OtherHint");
                string tooltipText = BuildFormatTooltip(
                    extensions,
                    capturedCategoryId == DesktopOrganizationCategoryIds.Other
                        ? T("DesktopOrganization.Rule.OtherExtensions")
                        : null);

                var combo = BuildWidgetTargetComboBox(
                    widgets,
                    emptyLabel: T("DesktopOrganization.Rules.KeepOnDesktop"),
                    selectedWidgetId: ownerRule?.TargetWidgetId,
                    minWidth: minWidth,
                    maxWidth: maxWidth);
                combo.SelectionChanged += async (s, _) =>
                {
                    if (_isRefreshing || s is not ComboBox { SelectedItem: ComboBoxItem { Tag: string targetId } })
                    {
                        return;
                    }

                    await AssignCategoryToWidgetAsync(capturedCategoryId, targetId);
                };

                var card = new CommunityToolkit.WinUI.Controls.SettingsCard
                {
                    Header = title,
                    Description = subtitle,
                    HeaderIcon = new FontIcon { Glyph = GetCategoryGlyph(capturedCategoryId) },
                    HorizontalContentAlignment = HorizontalAlignment.Right,
                    Content = combo
                };
                ToolTipService.SetToolTip(card, tooltipText);
                CategoryMappingCards.Children.Add(card);
            }

            // Subtype mapping rows inside collapsed ByTypeAdvancedExpander
            SubtypeMappingRows.Children.Clear();
            var titleStyle = TryGetResourceStyle("SettingTitleTextStyle");
            var descStyle = TryGetResourceStyle("SettingDescriptionTextStyle");
            var dividerBrush = NeutralInteractionBrush.ResolveThemedResource("CardStrokeColorDefaultBrush", this) ??
                               new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x20, 0x80, 0x80, 0x80));

            for (int i = 0; i < SubtypeOrder.Length; i++)
            {
                string subtypeId = SubtypeOrder[i];
                if (i > 0)
                {
                    SubtypeMappingRows.Children.Add(new Border
                    {
                        Height = 1,
                        Background = dividerBrush
                    });
                }

                DesktopOrganizationRule? ownerRule = settings.DesktopOrganization.DesktopOrganizationRules
                    .FirstOrDefault(rule =>
                        rule.IsEnabled &&
                        rule.SubtypeIds.Contains(subtypeId, StringComparer.Ordinal) &&
                        widgets.Any(w => string.Equals(w.Id, rule.TargetWidgetId, StringComparison.Ordinal)));

                IReadOnlyList<string> extensions = DesktopOrganizationClassifier.GetSubtypeExtensions(subtypeId);
                string tooltipText = BuildFormatTooltip(extensions, null);

                var row = new Grid
                {
                    ColumnSpacing = 12,
                    Padding = new Thickness(0, 2, 0, 2)
                };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var textStack = new StackPanel
                {
                    Spacing = 2,
                    VerticalAlignment = VerticalAlignment.Center
                };
                var nameBlock = new TextBlock { Text = T($"DesktopOrganization.Subtype.{subtypeId}") };
                if (titleStyle is not null) nameBlock.Style = titleStyle;
                var hintBlock = new TextBlock
                {
                    Text = Format("DesktopOrganization.Rules.ExtensionCountHint", extensions.Count),
                    Foreground = SecondaryTextBrush()
                };
                if (descStyle is not null) hintBlock.Style = descStyle;
                textStack.Children.Add(nameBlock);
                textStack.Children.Add(hintBlock);

                var combo = BuildWidgetTargetComboBox(
                    widgets,
                    emptyLabel: T("DesktopOrganization.Rules.FollowCategory"),
                    selectedWidgetId: ownerRule?.TargetWidgetId,
                    minWidth: minWidth,
                    maxWidth: maxWidth);
                combo.SelectionChanged += async (s, _) =>
                {
                    if (_isRefreshing || s is not ComboBox { SelectedItem: ComboBoxItem { Tag: string targetId } })
                    {
                        return;
                    }

                    await AssignSubtypeToWidgetAsync(subtypeId, targetId);
                };
                Grid.SetColumn(combo, 1);

                row.Children.Add(textStack);
                row.Children.Add(combo);
                ToolTipService.SetToolTip(row, tooltipText);
                SubtypeMappingRows.Children.Add(row);
            }

            // Target widget pickers + chips for custom & excluded extensions
            PopulateTargetPickerCombo(ByTypeExtensionTargetComboBox, widgets);
            PopulateTargetPickerCombo(ByTypeExcludedTargetComboBox, widgets);
            RebuildByTypeExtensionChips(settings, widgets);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    private void RebuildByTypeExtensionChips(AppSettings settings, List<WidgetConfig> widgets)
    {
        ByTypeExtensionChips.Children.Clear();
        ByTypeExcludedExtensionChips.Children.Clear();

        foreach (DesktopOrganizationRule rule in settings.DesktopOrganization.DesktopOrganizationRules)
        {
            WidgetConfig? widget = widgets.FirstOrDefault(w =>
                string.Equals(w.Id, rule.TargetWidgetId, StringComparison.Ordinal));
            if (widget is null)
            {
                continue;
            }

            string widgetId = widget.Id;
            foreach (string ext in rule.Extensions)
            {
                string capturedExt = ext;
                var chip = new Button
                {
                    Content = $"{capturedExt} → {widget.Name}  ×",
                    Padding = new Thickness(9, 4, 9, 4)
                };
                chip.Click += async (_, _) => await RemoveExtensionFromWidgetAsync(widgetId, capturedExt);
                ByTypeExtensionChips.Children.Add(chip);
            }

            foreach (string ext in rule.ExcludedExtensions)
            {
                string capturedExt = ext;
                var chip = new Button
                {
                    Content = $"{capturedExt} ({widget.Name})  ×",
                    Padding = new Thickness(9, 4, 9, 4)
                };
                chip.Click += async (_, _) => await RemoveExcludedExtensionFromWidgetAsync(widgetId, capturedExt);
                ByTypeExcludedExtensionChips.Children.Add(chip);
            }
        }
    }

    private void ByTypeExtensionInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = AddExtensionFromByTypeAsync();
        }
    }

    private void ByTypeAddExtensionButton_Click(object sender, RoutedEventArgs e) =>
        _ = AddExtensionFromByTypeAsync();

    private async Task AddExtensionFromByTypeAsync()
    {
        if (ByTypeExtensionTargetComboBox.SelectedItem is not ComboBoxItem { Tag: string widgetId })
        {
            return;
        }

        if (await AddExtensionToWidgetAsync(widgetId, ByTypeExtensionInput.Text, confirmTransfer: false))
        {
            ByTypeExtensionInput.Text = string.Empty;
        }
    }

    private void ByTypeExcludedExtensionInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = AddExcludedExtensionFromByTypeAsync();
        }
    }

    private void ByTypeAddExcludedExtensionButton_Click(object sender, RoutedEventArgs e) =>
        _ = AddExcludedExtensionFromByTypeAsync();

    private async Task AddExcludedExtensionFromByTypeAsync()
    {
        if (ByTypeExcludedTargetComboBox.SelectedItem is not ComboBoxItem { Tag: string widgetId })
        {
            return;
        }

        if (await AddExcludedExtensionToWidgetAsync(widgetId, ByTypeExcludedExtensionInput.Text))
        {
            ByTypeExcludedExtensionInput.Text = string.Empty;
        }
    }

    // -------------------------------------------------------------------------
    // View 2: By Widget (expandable card per File Widget + WrapFlowPanel ToggleButton pills + ToolTip)
    // -------------------------------------------------------------------------
    private void RebuildByWidgetView()
    {
        if (TryGetSettingsService() is not { } settingsService)
        {
            return;
        }

        AppSettings settings = settingsService.Settings;
        List<WidgetConfig> allWidgets = GetActiveFileWidgets(settings);

        WidgetSearchBox.Visibility = allWidgets.Count > 6
            ? Visibility.Visible
            : Visibility.Collapsed;

        string query = WidgetSearchBox.Visibility == Visibility.Visible
            ? (WidgetSearchBox.Text?.Trim() ?? string.Empty)
            : string.Empty;
        List<WidgetConfig> widgets = query.Length == 0
            ? allWidgets
            : allWidgets
                .Where(w => w.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

        bool hasAnyEffectiveRules = settings.DesktopOrganization.DesktopOrganizationRules
            .Any(rule => IsEffectiveRule(rule, settings));
        if (!hasAnyEffectiveRules && _expandedWidgetIds.Count == 0 && widgets.Count > 0)
        {
            // Auto-expand the first widget when 0 rules are configured so the user can click pills immediately.
            _expandedWidgetIds.Add(widgets[0].Id);
        }

        WidgetRuleCards.Children.Clear();
        var titleStyle = TryGetResourceStyle("SettingTitleTextStyle");
        var descStyle = TryGetResourceStyle("SettingDescriptionTextStyle");

        foreach (WidgetConfig widget in widgets)
        {
            string widgetId = widget.Id;
            DesktopOrganizationRule? rule = settings.DesktopOrganization.DesktopOrganizationRules
                .FirstOrDefault(r => string.Equals(r.TargetWidgetId, widgetId, StringComparison.Ordinal));

            var expander = new CommunityToolkit.WinUI.Controls.SettingsExpander
            {
                Header = widget.Name,
                Description = BuildRuleSummary(rule),
                HeaderIcon = new FontIcon { Glyph = "\uE8B7" },
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsExpanded = _expandedWidgetIds.Contains(widgetId)
            };
            expander.Expanded += (_, _) => _expandedWidgetIds.Add(widgetId);
            expander.Collapsed += (_, _) => _expandedWidgetIds.Remove(widgetId);

            bool hasAssignments = rule is not null && HasAssignments(rule);
            var ruleToggle = new ToggleSwitch
            {
                MinWidth = 0,
                OnContent = string.Empty,
                OffContent = string.Empty,
                IsOn = rule?.IsEnabled == true && hasAssignments,
                IsEnabled = hasAssignments
            };
            ruleToggle.Toggled += async (s, _) =>
            {
                if (_isRefreshing || s is not ToggleSwitch toggle)
                {
                    return;
                }

                await SetWidgetRuleEnabledAsync(widgetId, toggle.IsOn);
            };
            expander.Content = ruleToggle;

            // Card 1: 7 broad categories as compact pill ToggleButtons with hover ToolTips
            var categoriesPanel = new WrapFlowPanel
            {
                HorizontalSpacing = 8,
                VerticalSpacing = 8
            };
            foreach (string categoryId in DesktopOrganizationCategoryIds.DefaultOrder)
            {
                string capturedCategoryId = categoryId;
                bool isAssignedHere = rule is not null &&
                    rule.CategoryIds.Contains(capturedCategoryId, StringComparer.Ordinal);
                DesktopOrganizationRule? otherOwner = FindCategoryOwner(settings, allWidgets, capturedCategoryId, excludeWidgetId: widgetId);
                WidgetConfig? otherOwnerWidget = otherOwner is null
                    ? null
                    : allWidgets.FirstOrDefault(w => string.Equals(w.Id, otherOwner.TargetWidgetId, StringComparison.Ordinal));

                IReadOnlyList<string> extensions = DesktopOrganizationClassifier.GetCategoryExtensions(capturedCategoryId);
                string tooltip = BuildFormatTooltip(
                    extensions,
                    capturedCategoryId == DesktopOrganizationCategoryIds.Other
                        ? T("DesktopOrganization.Rule.OtherExtensions")
                        : null);
                if (otherOwnerWidget is not null)
                {
                    tooltip = $"{Format("DesktopOrganization.Rules.OwnedByHint", otherOwnerWidget.Name)}\n\n{tooltip}";
                }

                var pill = new ToggleButton
                {
                    Content = T($"DesktopOrganization.Category.{capturedCategoryId}"),
                    IsChecked = isAssignedHere,
                    Padding = new Thickness(12, 6, 12, 6),
                    CornerRadius = new CornerRadius(16),
                    Opacity = !isAssignedHere && otherOwnerWidget is not null ? 0.65 : 1.0
                };
                ToolTipService.SetToolTip(pill, tooltip);
                pill.Click += async (s, _) =>
                {
                    if (_isRefreshing || s is not ToggleButton button)
                    {
                        return;
                    }

                    await ToggleCategoryForWidgetAsync(widgetId, capturedCategoryId, button.IsChecked == true);
                };
                categoriesPanel.Children.Add(pill);
            }

            var categoriesCard = new CommunityToolkit.WinUI.Controls.SettingsCard
            {
                Header = T("DesktopOrganization.Rule.Categories"),
                Description = T("DesktopOrganization.Rule.CategoriesDescription"),
                ContentAlignment = CommunityToolkit.WinUI.Controls.ContentAlignment.Vertical,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = categoriesPanel
            };
            expander.Items.Add(categoriesCard);

            // Card 2: 7 subtype pill ToggleButtons + custom & excluded extensions
            var advancedStack = new StackPanel { Spacing = 10 };
            var subtypesTitle = new TextBlock { Text = T("DesktopOrganization.Rule.SpecificTypes") };
            if (titleStyle is not null) subtypesTitle.Style = titleStyle;
            advancedStack.Children.Add(subtypesTitle);

            var subtypesPanel = new WrapFlowPanel
            {
                HorizontalSpacing = 8,
                VerticalSpacing = 8
            };
            foreach (string subtypeId in SubtypeOrder)
            {
                string capturedSubtypeId = subtypeId;
                bool isAssignedHere = rule is not null &&
                    rule.SubtypeIds.Contains(capturedSubtypeId, StringComparer.Ordinal);
                DesktopOrganizationRule? otherOwner = FindSubtypeOwner(settings, allWidgets, capturedSubtypeId, excludeWidgetId: widgetId);
                WidgetConfig? otherOwnerWidget = otherOwner is null
                    ? null
                    : allWidgets.FirstOrDefault(w => string.Equals(w.Id, otherOwner.TargetWidgetId, StringComparison.Ordinal));

                IReadOnlyList<string> extensions = DesktopOrganizationClassifier.GetSubtypeExtensions(capturedSubtypeId);
                string tooltip = BuildFormatTooltip(extensions, null);
                if (otherOwnerWidget is not null)
                {
                    tooltip = $"{Format("DesktopOrganization.Rules.OwnedByHint", otherOwnerWidget.Name)}\n\n{tooltip}";
                }

                var pill = new ToggleButton
                {
                    Content = T($"DesktopOrganization.Subtype.{capturedSubtypeId}"),
                    IsChecked = isAssignedHere,
                    Padding = new Thickness(12, 5, 12, 5),
                    CornerRadius = new CornerRadius(16),
                    Opacity = !isAssignedHere && otherOwnerWidget is not null ? 0.65 : 1.0
                };
                ToolTipService.SetToolTip(pill, tooltip);
                pill.Click += async (s, _) =>
                {
                    if (_isRefreshing || s is not ToggleButton button)
                    {
                        return;
                    }

                    await ToggleSubtypeForWidgetAsync(widgetId, capturedSubtypeId, button.IsChecked == true);
                };
                subtypesPanel.Children.Add(pill);
            }
            advancedStack.Children.Add(subtypesPanel);

            // Custom extension row for this widget
            var extTitle = new TextBlock { Text = T("DesktopOrganization.Rule.Extensions"), Margin = new Thickness(0, 4, 0, 0) };
            if (titleStyle is not null) extTitle.Style = titleStyle;
            advancedStack.Children.Add(extTitle);

            var extInput = new TextBox { PlaceholderText = T("DesktopOrganization.Rule.ExtensionPlaceholder") };
            var extAddBtn = new Button { Content = T("DesktopOrganization.Common.Add") };
            extInput.KeyDown += (s, args) =>
            {
                if (!ImeCompositionGuard.IsImeCompositionKeyEvent(args) && args.Key == VirtualKey.Enter)
                {
                    args.Handled = true;
                    _ = AddExtensionToWidgetAsync(widgetId, extInput.Text, confirmTransfer: true);
                }
            };
            extAddBtn.Click += (_, _) => _ = AddExtensionToWidgetAsync(widgetId, extInput.Text, confirmTransfer: true);
            advancedStack.Children.Add(BuildInputActionRow(extInput, extAddBtn));

            if (rule is { Extensions.Count: > 0 })
            {
                var chips = new WrapFlowPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
                foreach (string ext in rule.Extensions)
                {
                    string capturedExt = ext;
                    var chip = new Button
                    {
                        Content = $"{capturedExt}  ×",
                        Padding = new Thickness(9, 4, 9, 4)
                    };
                    chip.Click += async (_, _) => await RemoveExtensionFromWidgetAsync(widgetId, capturedExt);
                    chips.Children.Add(chip);
                }
                advancedStack.Children.Add(chips);
            }

            // Excluded extension row for this widget
            var exclTitle = new TextBlock { Text = T("DesktopOrganization.Rule.Exclusions"), Margin = new Thickness(0, 4, 0, 0) };
            if (titleStyle is not null) exclTitle.Style = titleStyle;
            advancedStack.Children.Add(exclTitle);

            var exclInput = new TextBox { PlaceholderText = T("DesktopOrganization.Rule.ExclusionPlaceholder") };
            var exclAddBtn = new Button { Content = T("DesktopOrganization.Common.Add") };
            exclInput.KeyDown += (s, args) =>
            {
                if (!ImeCompositionGuard.IsImeCompositionKeyEvent(args) && args.Key == VirtualKey.Enter)
                {
                    args.Handled = true;
                    _ = AddExcludedExtensionToWidgetAsync(widgetId, exclInput.Text);
                }
            };
            exclAddBtn.Click += (_, _) => _ = AddExcludedExtensionToWidgetAsync(widgetId, exclInput.Text);
            advancedStack.Children.Add(BuildInputActionRow(exclInput, exclAddBtn));

            if (rule is { ExcludedExtensions.Count: > 0 })
            {
                var exclChips = new WrapFlowPanel { HorizontalSpacing = 6, VerticalSpacing = 6 };
                foreach (string ext in rule.ExcludedExtensions)
                {
                    string capturedExt = ext;
                    var chip = new Button
                    {
                        Content = $"{capturedExt}  ×",
                        Padding = new Thickness(9, 4, 9, 4)
                    };
                    chip.Click += async (_, _) => await RemoveExcludedExtensionFromWidgetAsync(widgetId, capturedExt);
                    exclChips.Children.Add(chip);
                }
                advancedStack.Children.Add(exclChips);
            }

            if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath))
            {
                var pathBlock = new TextBlock
                {
                    Text = widget.MappedFolderPath,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 4, 0, 0),
                    Foreground = SecondaryTextBrush()
                };
                if (descStyle is not null) pathBlock.Style = descStyle;
                ToolTipService.SetToolTip(pathBlock, widget.MappedFolderPath);
                advancedStack.Children.Add(pathBlock);
            }

            var advancedCard = new CommunityToolkit.WinUI.Controls.SettingsCard
            {
                Header = T("DesktopOrganization.Rules.SubtypesAndCustom.Title"),
                ContentAlignment = CommunityToolkit.WinUI.Controls.ContentAlignment.Vertical,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = advancedStack
            };
            expander.Items.Add(advancedCard);

            WidgetRuleCards.Children.Add(expander);
        }
    }

    // -------------------------------------------------------------------------
    // Rule Mutation Operations
    // -------------------------------------------------------------------------
    private async Task AssignCategoryToWidgetAsync(string categoryId, string targetWidgetId)
    {
        if (TryGetSettingsService() is not { } service)
        {
            return;
        }

        List<DesktopOrganizationRule> rules = service.Settings.DesktopOrganization.DesktopOrganizationRules;
        foreach (DesktopOrganizationRule existing in rules)
        {
            if (existing.CategoryIds.RemoveAll(v => string.Equals(v, categoryId, StringComparison.Ordinal)) > 0 &&
                !HasAssignments(existing))
            {
                existing.IsEnabled = false;
            }
        }

        if (!string.IsNullOrEmpty(targetWidgetId))
        {
            DesktopOrganizationRule targetRule = GetOrCreateRule(rules, targetWidgetId);
            if (!targetRule.CategoryIds.Contains(categoryId, StringComparer.Ordinal))
            {
                targetRule.CategoryIds.Add(categoryId);
            }
            targetRule.IsEnabled = true;
        }

        await service.SaveAsync();
        Refresh();
    }

    private async Task AssignSubtypeToWidgetAsync(string subtypeId, string targetWidgetId)
    {
        if (TryGetSettingsService() is not { } service)
        {
            return;
        }

        List<DesktopOrganizationRule> rules = service.Settings.DesktopOrganization.DesktopOrganizationRules;
        foreach (DesktopOrganizationRule existing in rules)
        {
            if (existing.SubtypeIds.RemoveAll(v => string.Equals(v, subtypeId, StringComparison.Ordinal)) > 0 &&
                !HasAssignments(existing))
            {
                existing.IsEnabled = false;
            }
        }

        if (!string.IsNullOrEmpty(targetWidgetId))
        {
            DesktopOrganizationRule targetRule = GetOrCreateRule(rules, targetWidgetId);
            if (!targetRule.SubtypeIds.Contains(subtypeId, StringComparer.Ordinal))
            {
                targetRule.SubtypeIds.Add(subtypeId);
            }
            targetRule.IsEnabled = true;
        }

        await service.SaveAsync();
        Refresh();
    }

    private async Task ToggleCategoryForWidgetAsync(string widgetId, string categoryId, bool assign)
    {
        if (TryGetSettingsService() is not { } service)
        {
            return;
        }

        AppSettings settings = service.Settings;
        List<WidgetConfig> widgets = GetActiveFileWidgets(settings);
        if (assign)
        {
            DesktopOrganizationRule? owner = FindCategoryOwner(settings, widgets, categoryId, excludeWidgetId: widgetId);
            if (owner is not null)
            {
                await OfferRuleTransferAsync(
                    T($"DesktopOrganization.Category.{categoryId}"),
                    owner,
                    widgetId,
                    () => AssignCategoryToWidgetAsync(categoryId, widgetId));
                Refresh();
                return;
            }

            await AssignCategoryToWidgetAsync(categoryId, widgetId);
            return;
        }

        DesktopOrganizationRule? rule = settings.DesktopOrganization.DesktopOrganizationRules
            .FirstOrDefault(r => string.Equals(r.TargetWidgetId, widgetId, StringComparison.Ordinal));
        if (rule is not null)
        {
            rule.CategoryIds.RemoveAll(v => string.Equals(v, categoryId, StringComparison.Ordinal));
            if (!HasAssignments(rule))
            {
                rule.IsEnabled = false;
            }
            await service.SaveAsync();
        }

        Refresh();
    }

    private async Task ToggleSubtypeForWidgetAsync(string widgetId, string subtypeId, bool assign)
    {
        if (TryGetSettingsService() is not { } service)
        {
            return;
        }

        AppSettings settings = service.Settings;
        List<WidgetConfig> widgets = GetActiveFileWidgets(settings);
        if (assign)
        {
            DesktopOrganizationRule? owner = FindSubtypeOwner(settings, widgets, subtypeId, excludeWidgetId: widgetId);
            if (owner is not null)
            {
                await OfferRuleTransferAsync(
                    T($"DesktopOrganization.Subtype.{subtypeId}"),
                    owner,
                    widgetId,
                    () => AssignSubtypeToWidgetAsync(subtypeId, widgetId));
                Refresh();
                return;
            }

            await AssignSubtypeToWidgetAsync(subtypeId, widgetId);
            return;
        }

        DesktopOrganizationRule? rule = settings.DesktopOrganization.DesktopOrganizationRules
            .FirstOrDefault(r => string.Equals(r.TargetWidgetId, widgetId, StringComparison.Ordinal));
        if (rule is not null)
        {
            rule.SubtypeIds.RemoveAll(v => string.Equals(v, subtypeId, StringComparison.Ordinal));
            if (!HasAssignments(rule))
            {
                rule.IsEnabled = false;
            }
            await service.SaveAsync();
        }

        Refresh();
    }

    private async Task SetWidgetRuleEnabledAsync(string widgetId, bool enabled)
    {
        if (TryGetSettingsService() is not { } service)
        {
            return;
        }

        DesktopOrganizationRule? rule = service.Settings.DesktopOrganization.DesktopOrganizationRules
            .FirstOrDefault(r => string.Equals(r.TargetWidgetId, widgetId, StringComparison.Ordinal));
        if (rule is null || !HasAssignments(rule))
        {
            Refresh();
            return;
        }

        rule.IsEnabled = enabled;
        await service.SaveAsync();
        Refresh();
    }

    private async Task<bool> AddExtensionToWidgetAsync(string widgetId, string rawText, bool confirmTransfer)
    {
        if (TryGetSettingsService() is not { } service)
        {
            return false;
        }

        string extension = DesktopOrganizationClassifier.NormalizeExtension(rawText);
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        List<DesktopOrganizationRule> rules = service.Settings.DesktopOrganization.DesktopOrganizationRules;
        DesktopOrganizationRule? currentOwner = rules.FirstOrDefault(rule =>
            !string.Equals(rule.TargetWidgetId, widgetId, StringComparison.Ordinal) &&
            rule.IsEnabled &&
            rule.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        if (confirmTransfer && currentOwner is not null)
        {
            bool accepted = false;
            await OfferRuleTransferAsync(
                extension,
                currentOwner,
                widgetId,
                async () =>
                {
                    _ruleResolver.AssignExtensionExclusively(rules, widgetId, extension);
                    GetOrCreateRule(rules, widgetId).IsEnabled = true;
                    await service.SaveAsync();
                    accepted = true;
                });
            Refresh();
            return accepted;
        }

        _ruleResolver.AssignExtensionExclusively(rules, widgetId, extension);
        GetOrCreateRule(rules, widgetId).IsEnabled = true;
        await service.SaveAsync();
        Refresh();
        return true;
    }

    private async Task RemoveExtensionFromWidgetAsync(string widgetId, string extension)
    {
        if (TryGetSettingsService() is not { } service)
        {
            return;
        }

        DesktopOrganizationRule? rule = service.Settings.DesktopOrganization.DesktopOrganizationRules
            .FirstOrDefault(r => string.Equals(r.TargetWidgetId, widgetId, StringComparison.Ordinal));
        if (rule is null)
        {
            return;
        }

        rule.Extensions.RemoveAll(v => string.Equals(v, extension, StringComparison.OrdinalIgnoreCase));
        if (!HasAssignments(rule))
        {
            rule.IsEnabled = false;
        }

        await service.SaveAsync();
        Refresh();
    }

    private async Task<bool> AddExcludedExtensionToWidgetAsync(string widgetId, string rawText)
    {
        if (TryGetSettingsService() is not { } service)
        {
            return false;
        }

        string extension = DesktopOrganizationClassifier.NormalizeExtension(rawText);
        if (string.IsNullOrEmpty(extension))
        {
            return false;
        }

        DesktopOrganizationRule rule = GetOrCreateRule(
            service.Settings.DesktopOrganization.DesktopOrganizationRules,
            widgetId);
        if (!rule.ExcludedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            rule.ExcludedExtensions.Add(extension);
        }

        await service.SaveAsync();
        Refresh();
        return true;
    }

    private async Task RemoveExcludedExtensionFromWidgetAsync(string widgetId, string extension)
    {
        if (TryGetSettingsService() is not { } service)
        {
            return;
        }

        DesktopOrganizationRule? rule = service.Settings.DesktopOrganization.DesktopOrganizationRules
            .FirstOrDefault(r => string.Equals(r.TargetWidgetId, widgetId, StringComparison.Ordinal));
        if (rule is null)
        {
            return;
        }

        rule.ExcludedExtensions.RemoveAll(v => string.Equals(v, extension, StringComparison.OrdinalIgnoreCase));
        await service.SaveAsync();
        Refresh();
    }

    private async Task OfferRuleTransferAsync(
        string typeName,
        DesktopOrganizationRule owner,
        string destinationWidgetId,
        Func<Task> accept)
    {
        if (XamlRoot is null || TryGetSettingsService() is not { } service)
        {
            return;
        }

        AppSettings settings = service.Settings;
        WidgetConfig? destinationWidget = settings.WidgetLayout.Widgets.FirstOrDefault(w =>
            string.Equals(w.Id, destinationWidgetId, StringComparison.Ordinal));
        WidgetConfig? ownerWidget = settings.WidgetLayout.Widgets.FirstOrDefault(w =>
            string.Equals(w.Id, owner.TargetWidgetId, StringComparison.Ordinal));

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = T("DesktopOrganization.Rule.TransferTitle"),
            Content = new TextBlock
            {
                Text = Format(
                    "DesktopOrganization.Rule.TransferBody",
                    typeName,
                    ownerWidget?.Name ?? owner.TargetWidgetId,
                    destinationWidget?.Name ?? destinationWidgetId),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = T("DesktopOrganization.Rule.TransferAction"),
            CloseButtonText = T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };

        ContentDialogResult result;
        try
        {
            result = await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopOrganization] Rule transfer dialog failed: {ex}");
            return;
        }

        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            await accept();
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopOrganization] Rule transfer failed: {ex}");
            RuleStatusInfo.ActionButton = null;
            RuleStatusInfo.Severity = InfoBarSeverity.Error;
            RuleStatusInfo.Title = T("DesktopOrganization.Result.FailedTitle");
            RuleStatusInfo.Message = T("DesktopOrganization.Result.FailedBody");
            RuleStatusInfo.IsOpen = true;
        }
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------
    private static ComboBox BuildWidgetTargetComboBox(
        List<WidgetConfig> widgets,
        string emptyLabel,
        string? selectedWidgetId,
        double minWidth,
        double maxWidth)
    {
        var combo = new ComboBox
        {
            MinWidth = minWidth,
            MaxWidth = maxWidth
        };
        combo.Items.Add(new ComboBoxItem { Content = emptyLabel, Tag = string.Empty });
        int selectedIndex = 0;
        for (int i = 0; i < widgets.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = widgets[i].Name, Tag = widgets[i].Id });
            if (string.Equals(widgets[i].Id, selectedWidgetId, StringComparison.Ordinal))
            {
                selectedIndex = i + 1;
            }
        }
        combo.SelectedIndex = selectedIndex;
        return combo;
    }

    private static void PopulateTargetPickerCombo(ComboBox combo, List<WidgetConfig> widgets)
    {
        string? previousSelectedId = (combo.SelectedItem as ComboBoxItem)?.Tag as string;
        combo.Items.Clear();
        int selectedIndex = 0;
        for (int i = 0; i < widgets.Count; i++)
        {
            combo.Items.Add(new ComboBoxItem { Content = widgets[i].Name, Tag = widgets[i].Id });
            if (string.Equals(widgets[i].Id, previousSelectedId, StringComparison.Ordinal))
            {
                selectedIndex = i;
            }
        }
        if (widgets.Count > 0)
        {
            combo.SelectedIndex = selectedIndex;
        }
    }

    private static Grid BuildInputActionRow(TextBox input, Button button)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(input, 0);
        Grid.SetColumn(button, 1);
        grid.Children.Add(input);
        grid.Children.Add(button);
        return grid;
    }

    private static string BuildFormatTooltip(IReadOnlyList<string> extensions, string? fallbackDescription)
    {
        if (extensions.Count > 0)
        {
            return Format("DesktopOrganization.Rules.SupportedFormatsTooltip", string.Join("  ·  ", extensions));
        }

        return fallbackDescription ?? string.Empty;
    }

    private static string GetCategoryGlyph(string categoryId) => categoryId switch
    {
        DesktopOrganizationCategoryIds.Shortcuts => "\uE71B",
        DesktopOrganizationCategoryIds.Documents => "\uE8A5",
        DesktopOrganizationCategoryIds.Images => "\uEB9F",
        DesktopOrganizationCategoryIds.Media => "\uE8B2",
        DesktopOrganizationCategoryIds.Design => "\uE790",
        DesktopOrganizationCategoryIds.Packages => "\uE7B8",
        _ => "\uE8B7"
    };

    private static List<WidgetConfig> GetActiveFileWidgets(AppSettings settings) =>
        settings.WidgetLayout.Widgets
            .Where(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !widget.IsDisabled &&
                !settings.WidgetLayout.DeletedWidgetIds.Contains(widget.Id))
            .OrderBy(widget => widget.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    private static DesktopOrganizationRule GetOrCreateRule(
        List<DesktopOrganizationRule> rules,
        string widgetId)
    {
        DesktopOrganizationRule? rule = rules.FirstOrDefault(r =>
            string.Equals(r.TargetWidgetId, widgetId, StringComparison.Ordinal));
        if (rule is null)
        {
            rule = new DesktopOrganizationRule
            {
                TargetWidgetId = widgetId,
                IsEnabled = true
            };
            rules.Add(rule);
        }
        return rule;
    }

    private static DesktopOrganizationRule? FindCategoryOwner(
        AppSettings settings,
        List<WidgetConfig> activeWidgets,
        string categoryId,
        string excludeWidgetId) =>
        settings.DesktopOrganization.DesktopOrganizationRules.FirstOrDefault(rule =>
            !string.Equals(rule.TargetWidgetId, excludeWidgetId, StringComparison.Ordinal) &&
            rule.IsEnabled &&
            rule.CategoryIds.Contains(categoryId, StringComparer.Ordinal) &&
            activeWidgets.Any(w => string.Equals(w.Id, rule.TargetWidgetId, StringComparison.Ordinal)));

    private static DesktopOrganizationRule? FindSubtypeOwner(
        AppSettings settings,
        List<WidgetConfig> activeWidgets,
        string subtypeId,
        string excludeWidgetId) =>
        settings.DesktopOrganization.DesktopOrganizationRules.FirstOrDefault(rule =>
            !string.Equals(rule.TargetWidgetId, excludeWidgetId, StringComparison.Ordinal) &&
            rule.IsEnabled &&
            rule.SubtypeIds.Contains(subtypeId, StringComparer.Ordinal) &&
            activeWidgets.Any(w => string.Equals(w.Id, rule.TargetWidgetId, StringComparison.Ordinal)));

    private string BuildRuleSummary(DesktopOrganizationRule? rule)
    {
        if (rule is null || !HasAssignments(rule))
        {
            return T("DesktopOrganization.Rules.Unconfigured");
        }

        List<string> values = rule.CategoryIds
            .Select(category => T($"DesktopOrganization.Category.{category}"))
            .Concat(rule.SubtypeIds.Select(subtype => T($"DesktopOrganization.Subtype.{subtype}")))
            .Concat(rule.Extensions)
            .Take(4)
            .ToList();
        string state = rule.IsEnabled
            ? T("DesktopOrganization.Rules.Enabled")
            : T("DesktopOrganization.Rules.Paused");
        return $"{string.Join("、", values)} · {state}";
    }

    private void ResetHostScrollToTop()
    {
        DependencyObject ancestor = this;
        while (ancestor is not null)
        {
            if (ancestor is ScrollViewer scroller)
            {
                scroller.ChangeView(null, 0, null, disableAnimation: true);
                return;
            }
            ancestor = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(ancestor);
        }
    }

    private Microsoft.UI.Xaml.Media.Brush SecondaryTextBrush() =>
        NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", this) ??
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));

    private Style? TryGetResourceStyle(string key) =>
        Resources.TryGetValue(key, out object? obj) ? obj as Style : null;

    private double ResolveResourceDouble(string key, double fallback) =>
        Resources.TryGetValue(key, out object? obj) && obj is double value ? value : fallback;

    private static bool HasAssignments(DesktopOrganizationRule rule) =>
        rule.CategoryIds.Count > 0 ||
        rule.SubtypeIds.Count > 0 ||
        rule.Extensions.Count > 0;

    private static bool IsEffectiveRule(DesktopOrganizationRule rule, AppSettings settings)
    {
        if (!rule.IsEnabled || !HasAssignments(rule))
        {
            return false;
        }

        return settings.WidgetLayout.Widgets.Any(widget =>
            string.Equals(widget.Id, rule.TargetWidgetId, StringComparison.Ordinal) &&
            widget.WidgetKind == WidgetKind.File &&
            !widget.IsDisabled &&
            !settings.WidgetLayout.DeletedWidgetIds.Contains(widget.Id) &&
            !string.IsNullOrWhiteSpace(widget.MappedFolderPath));
    }

    private static SettingsService? TryGetSettingsService() =>
        global::DeskBox.App.Current?.SettingsService;

    private static string T(string key) =>
        global::DeskBox.App.Current.LocalizationService.T(key);

    private static string Format(string key, params object[] values) =>
        global::DeskBox.App.Current.LocalizationService.Format(key, values);
}
