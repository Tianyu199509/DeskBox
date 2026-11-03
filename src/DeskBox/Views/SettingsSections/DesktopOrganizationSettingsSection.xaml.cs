using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace DeskBox.Views.SettingsSections;

public sealed partial class DesktopOrganizationSettingsSection : UserControl
{
    private readonly DesktopOrganizationRuleResolver _ruleResolver = new();
    private WidgetConfig? _selectedWidget;
    private DesktopOrganizationRule? _selectedRule;
    private bool _selectedRuleIsDraft;
    private bool _isRefreshing;
    private ISettingsWidgetActions? _widgetActions;
    private ISettingsAppActions? _appActions;
    private nint _ownerWindow;

    public event EventHandler? SubPageChanged;

    public bool IsRuleListOpen => RuleListPanel.Visibility == Visibility.Visible;

    public bool IsDetailSubPageOpen => IsRuleListOpen || IsRuleDetailOpen;

    public bool IsRuleDetailOpen =>
        _selectedWidget is not null && RuleDetailPanel.Visibility == Visibility.Visible;

    public string? ActiveRuleDetailTitle => _selectedWidget?.Name;

    /// <summary>
    /// Injected by the settings window when the deferred section is created,
    /// before the first Loaded (host commands plus this window's owner
    /// handle for main-process dialog parenting).
    /// </summary>
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
        Loaded += (_, _) => Refresh();
        // Code-built rows snapshot their themed brushes at build time, and
        // the theme picker lives in this same window: re-run the row
        // builders when the applied theme flips so they follow along like
        // XAML ThemeResources do.
        ActualThemeChanged += (_, _) =>
        {
            RefreshWidgetCards();
            BuildTypeOverview();
            if (RuleDetailPanel.Visibility == Visibility.Visible)
            {
                BuildCategoryChecks();
                BuildSubtypeChecks();
            }
        };
    }

    public void Refresh()
    {
        if (global::DeskBox.App.Current?.SettingsService is not { } settingsService)
        {
            return;
        }

        _isRefreshing = true;
        WidgetSearchBox.PlaceholderText = T("DesktopOrganization.Rules.SearchPlaceholder");
        ExtensionInput.PlaceholderText = T("DesktopOrganization.Rule.ExtensionPlaceholder");
        ExcludedExtensionInput.PlaceholderText = T(
            "DesktopOrganization.Rule.ExclusionPlaceholder");

        var settings = settingsService.Settings;
        var effectiveRules = settings.DesktopOrganization.DesktopOrganizationRules
            .Where(rule => IsEffectiveRule(rule, settings))
            .ToList();
        int effectiveRuleCount = effectiveRules.Count;
        int configuredWidgetCount = effectiveRules
            .Select(rule => rule.TargetWidgetId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        bool hasEffectiveRules = effectiveRuleCount > 0;
        bool autoOrganizationEnabled =
            hasEffectiveRules && settings.DesktopOrganization.DesktopAutoOrganizationEnabled;

        AutoOrganizationToggle.IsEnabled = hasEffectiveRules;
        AutoOrganizationToggle.IsOn = autoOrganizationEnabled;
        AutoToggleExpander.Description = hasEffectiveRules
            ? T("DesktopOrganization.Auto.Description")
            : T("DesktopOrganization.Auto.NeedRulesHint");
        AutoIntervalCard.IsEnabled = autoOrganizationEnabled;
        OrganizationRulesEntryCard.Description = hasEffectiveRules
            ? Format("DesktopOrganization.Status.Configured", configuredWidgetCount, effectiveRuleCount)
            : T("DesktopOrganization.Rules.Unconfigured");
        RefreshAutoIntervalOptions(settingsService);
        _isRefreshing = false;

        RefreshWidgetCards();
        BuildTypeOverview();
    }

    /// <summary>
    /// Application-scope themed lookups follow the system theme, which
    /// inverts whenever the app's theme override disagrees with it;
    /// resolve against this section's window-scoped theme instead.
    /// </summary>
    private Microsoft.UI.Xaml.Media.Brush SecondaryTextBrush() =>
        NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", this) ??
        new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));

    private void RefreshWidgetCards()
    {
        WidgetRuleCards.Children.Clear();
        if (global::DeskBox.App.Current?.SettingsService is not { } settingsService)
        {
            return;
        }

        var settings = settingsService.Settings;
        var allFileWidgets = settings.WidgetLayout.Widgets
            .Where(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !widget.IsDisabled &&
                !settings.WidgetLayout.DeletedWidgetIds.Contains(widget.Id))
            .OrderBy(widget => widget.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        WidgetSearchBox.Visibility = allFileWidgets.Count > 6
            ? Visibility.Visible
            : Visibility.Collapsed;

        string query = WidgetSearchBox.Visibility == Visibility.Visible
            ? (WidgetSearchBox.Text?.Trim() ?? string.Empty)
            : string.Empty;
        var widgets = query.Length == 0
            ? allFileWidgets
            : allFileWidgets
                .Where(widget => widget.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

        foreach (WidgetConfig widget in widgets)
        {
            DesktopOrganizationRule? rule = settings.DesktopOrganization.DesktopOrganizationRules.FirstOrDefault(candidate =>
                string.Equals(candidate.TargetWidgetId, widget.Id, StringComparison.Ordinal));
            var card = new CommunityToolkit.WinUI.Controls.SettingsCard
            {
                Tag = widget.Id,
                IsClickEnabled = true,
                Header = widget.Name,
                Description = BuildRuleSummary(rule)
            };
            card.HeaderIcon = new FontIcon { Glyph = "\uE8B7" };
            card.Click += WidgetRuleButton_Click;
            WidgetRuleCards.Children.Add(card);
        }

        if (widgets.Count == 0)
        {
            WidgetRuleCards.Children.Add(new TextBlock
            {
                Text = T("DesktopOrganization.Rules.Empty"),
                Margin = new Thickness(8, 12, 8, 4),
                Foreground = SecondaryTextBrush()
            });
        }
    }

    private async void AutoOrganizationToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing)
        {
            return;
        }

        var service = global::DeskBox.App.Current.SettingsService;
        service.Settings.DesktopOrganization.DesktopAutoOrganizationEnabled = AutoOrganizationToggle.IsOn;
        service.Settings.DesktopOrganization.DesktopAutoOrganizationBaselineUtc = AutoOrganizationToggle.IsOn
            ? DateTimeOffset.UtcNow
            : null;
        await service.SaveAsync();
        RuleStatusInfo.ActionButton = null;
        RuleStatusInfo.Severity = InfoBarSeverity.Informational;
        RuleStatusInfo.Title = AutoOrganizationToggle.IsOn
            ? T("DesktopOrganization.Auto.EnabledTitle")
            : T("DesktopOrganization.Auto.DisabledTitle");
        RuleStatusInfo.Message = AutoOrganizationToggle.IsOn
            ? T("DesktopOrganization.Auto.EnabledBody")
            : string.Empty;
        RuleStatusInfo.IsOpen = true;
        Refresh();
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
        AutoIntervalComboBox.IsEnabled = AutoOrganizationToggle.IsOn;
    }

    private static string GetAutoIntervalDisplayName(int seconds) => seconds switch
    {
        DesktopAutoOrganizationPolicy.DefaultDelaySeconds =>
            T("DesktopOrganization.Auto.Interval.Realtime"),
        60 => T("DesktopOrganization.Auto.Interval.Minute"),
        12 * 60 * 60 => Format("DesktopOrganization.Auto.Interval.Hours", 12),
        _ => Format("DesktopOrganization.Auto.Interval.Minutes", seconds / 60)
    };

    private async void AutoIntervalComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_isRefreshing ||
            AutoIntervalComboBox.SelectedItem is not ComboBoxItem { Tag: int seconds })
        {
            return;
        }

        var service = global::DeskBox.App.Current.SettingsService;
        service.Settings.DesktopOrganization.DesktopAutoOrganizationDelaySeconds =
            DesktopAutoOrganizationPolicy.NormalizeDelaySeconds(seconds);
        await service.SaveAsync();
    }

    private void OrganizeNowButton_Click(object sender, RoutedEventArgs e)
    {
        _appActions?.ShowDesktopOrganizationWindow();
    }

    private void OrganizationRulesEntryCard_Click(object sender, RoutedEventArgs e)
    {
        OpenRuleList(animate: true);
    }

    public void OpenRuleList(bool animate = true)
    {
        bool wasAlreadyOpen = RuleListPanel.Visibility == Visibility.Visible &&
                              RuleDetailPanel.Visibility == Visibility.Collapsed;
        _selectedWidget = null;
        _selectedRule = null;
        _selectedRuleIsDraft = false;
        OrganizationOverviewPanel.Visibility = Visibility.Collapsed;
        RuleDetailPanel.Visibility = Visibility.Collapsed;
        RuleListPanel.Visibility = Visibility.Visible;
        RuleStatusInfo.IsOpen = false;
        ResetHostScrollToTop();
        Refresh();
        SubPageChanged?.Invoke(this, EventArgs.Empty);
        if (animate && !wasAlreadyOpen && IsLoaded)
        {
            RuleListPanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(RuleListPanel, 40f);
        }
    }

    public void CloseRuleList(bool animate = true)
    {
        if (RuleListPanel.Visibility == Visibility.Collapsed &&
            RuleDetailPanel.Visibility == Visibility.Collapsed)
        {
            return;
        }

        _selectedWidget = null;
        _selectedRule = null;
        _selectedRuleIsDraft = false;
        RuleDetailPanel.Visibility = Visibility.Collapsed;
        RuleListPanel.Visibility = Visibility.Collapsed;
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
        bool wasSubPageOpen = IsDetailSubPageOpen;
        _selectedWidget = null;
        _selectedRule = null;
        _selectedRuleIsDraft = false;
        RuleDetailPanel.Visibility = Visibility.Collapsed;
        RuleListPanel.Visibility = Visibility.Collapsed;
        OrganizationOverviewPanel.Visibility = Visibility.Visible;
        RuleStatusInfo.IsOpen = false;
        if (wasSubPageOpen)
        {
            Refresh();
            SubPageChanged?.Invoke(this, EventArgs.Empty);
            if (animate && IsLoaded)
            {
                OrganizationOverviewPanel.UpdateLayout();
                DetailPageTransitionHelper.PlayNavigationEnter(OrganizationOverviewPanel, -40f);
            }
        }
    }

    public bool TryNavigateBack()
    {
        if (IsRuleDetailOpen)
        {
            CloseRuleDetail(animate: true);
            return true;
        }

        if (IsRuleListOpen)
        {
            CloseRuleList(animate: true);
            return true;
        }

        return false;
    }

    private void WidgetSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            RefreshWidgetCards();
        }
    }

    private void WidgetRuleButton_Click(object sender, RoutedEventArgs e)
    {
        // The row is a toolkit SettingsCard, whose Click event delivers the
        // card itself as sender — matching on Button silently swallowed every
        // invocation after the v3.1 row conversion.
        if (sender is not CommunityToolkit.WinUI.Controls.SettingsCard { Tag: string widgetId })
        {
            return;
        }

        var settings = global::DeskBox.App.Current.SettingsService.Settings;
        _selectedWidget = settings.WidgetLayout.Widgets.FirstOrDefault(widget =>
            string.Equals(widget.Id, widgetId, StringComparison.Ordinal));
        if (_selectedWidget is null)
        {
            return;
        }

        _selectedRule = settings.DesktopOrganization.DesktopOrganizationRules.FirstOrDefault(rule =>
            string.Equals(rule.TargetWidgetId, widgetId, StringComparison.Ordinal));
        _selectedRuleIsDraft = _selectedRule is null;
        if (_selectedRule is null)
        {
            _selectedRule = new DesktopOrganizationRule
            {
                TargetWidgetId = widgetId,
                IsEnabled = false
            };
        }

        ShowRuleDetail();
    }

    private void ShowRuleDetail()
    {
        if (_selectedWidget is null || _selectedRule is null)
        {
            return;
        }

        bool wasDetailAlreadyOpen = RuleDetailPanel.Visibility == Visibility.Visible;
        OrganizationOverviewPanel.Visibility = Visibility.Collapsed;
        RuleListPanel.Visibility = Visibility.Collapsed;
        RuleDetailPanel.Visibility = Visibility.Visible;
        RuleStatusInfo.IsOpen = false;
        FileTypeExpander.IsExpanded = true;
        ResetHostScrollToTop();
        RuleDetailTitle.Text = _selectedWidget.Name;
        RuleDetailPath.Text = _selectedWidget.MappedFolderPath ?? string.Empty;
        RuleDetailPath.Visibility = string.IsNullOrWhiteSpace(RuleDetailPath.Text)
            ? Visibility.Collapsed
            : Visibility.Visible;
        _isRefreshing = true;
        RuleEnabledToggle.IsOn = _selectedRule.IsEnabled;
        _isRefreshing = false;
        BuildCategoryChecks();
        BuildSubtypeChecks();
        BuildExtensionChips();
        BuildExcludedExtensionChips();
        BuildRuleTokens();
        SubPageChanged?.Invoke(this, EventArgs.Empty);
        if (!wasDetailAlreadyOpen && IsLoaded)
        {
            RuleDetailPanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(RuleDetailPanel, 40f);
        }
    }

    /// <summary>
    /// Drill-down within the section keeps the host page's scroller, which
    /// route navigation normally resets; entering rule detail must do the
    /// same so the header starts in view instead of at the list's offset.
    /// </summary>
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

    private void BuildSubtypeChecks()
    {
        SubtypeChecks.Children.Clear();
        if (_selectedRule is null)
        {
            return;
        }

        foreach (string subtypeId in new[]
                 {
                     DesktopOrganizationSubtypeIds.Pdf,
                     DesktopOrganizationSubtypeIds.Word,
                     DesktopOrganizationSubtypeIds.Excel,
                     DesktopOrganizationSubtypeIds.PowerPoint,
                     DesktopOrganizationSubtypeIds.Text,
                     DesktopOrganizationSubtypeIds.Audio,
                     DesktopOrganizationSubtypeIds.Video
                 })
        {
            var checkBox = new CheckBox
            {
                Content = CreateFileTypeOptionContent(
                    T($"DesktopOrganization.Subtype.{subtypeId}"),
                    DesktopOrganizationClassifier.GetSubtypeExtensions(subtypeId)),
                Tag = subtypeId,
                IsChecked = _selectedRule.SubtypeIds.Contains(subtypeId, StringComparer.Ordinal)
            };
            checkBox.Click += SubtypeCheckBox_Click;
            SubtypeChecks.Children.Add(checkBox);
        }
    }

    private async void SubtypeCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRule is null ||
            sender is not CheckBox { Tag: string subtypeId } checkBox)
        {
            return;
        }

        var settingsService = global::DeskBox.App.Current.SettingsService;
        if (checkBox.IsChecked == true)
        {
            DesktopOrganizationRule? owner = settingsService.Settings.DesktopOrganization.DesktopOrganizationRules
                .FirstOrDefault(rule =>
                    !ReferenceEquals(rule, _selectedRule) &&
                    rule.IsEnabled &&
                    rule.SubtypeIds.Contains(subtypeId, StringComparer.Ordinal));
            if (owner is not null)
            {
                checkBox.IsChecked = false;
                await OfferRuleTransferAsync(
                    T($"DesktopOrganization.Subtype.{subtypeId}"),
                    owner,
                    async () =>
                    {
                        owner.SubtypeIds.RemoveAll(value =>
                            string.Equals(value, subtypeId, StringComparison.Ordinal));
                        EnsureSelectedRuleRegistered();
                        if (!_selectedRule.SubtypeIds.Contains(subtypeId, StringComparer.Ordinal))
                        {
                            _selectedRule.SubtypeIds.Add(subtypeId);
                        }
                        _selectedRule.IsEnabled = true;
                        await settingsService.SaveAsync();
                        ShowRuleDetail();
                        RefreshStatusOnly();
                    });
                return;
            }

            EnsureSelectedRuleRegistered();
            if (!_selectedRule.SubtypeIds.Contains(subtypeId, StringComparer.Ordinal))
            {
                _selectedRule.SubtypeIds.Add(subtypeId);
            }
            _selectedRule.IsEnabled = true;
            _isRefreshing = true;
            RuleEnabledToggle.IsOn = true;
            _isRefreshing = false;
        }
        else
        {
            _selectedRule.SubtypeIds.RemoveAll(value =>
                string.Equals(value, subtypeId, StringComparison.Ordinal));
            DisableEmptySelectedRule();
        }

        await settingsService.SaveAsync();
        BuildRuleTokens();
        RefreshStatusOnly();
    }

    private void BuildCategoryChecks()
    {
        CategoryChecks.Children.Clear();
        if (_selectedRule is null)
        {
            return;
        }

        foreach (string categoryId in DesktopOrganizationCategoryIds.DefaultOrder)
        {
            var checkBox = new CheckBox
            {
                Content = CreateFileTypeOptionContent(
                    T($"DesktopOrganization.Category.{categoryId}"),
                    DesktopOrganizationClassifier.GetCategoryExtensions(categoryId),
                    categoryId == DesktopOrganizationCategoryIds.Other
                        ? T("DesktopOrganization.Rule.OtherExtensions")
                        : null),
                Tag = categoryId,
                IsChecked = _selectedRule.CategoryIds.Contains(categoryId, StringComparer.Ordinal)
            };
            checkBox.Click += CategoryCheckBox_Click;
            CategoryChecks.Children.Add(checkBox);
        }
    }

    private async void CategoryCheckBox_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRule is null ||
            sender is not CheckBox { Tag: string categoryId } checkBox)
        {
            return;
        }

        var settingsService = global::DeskBox.App.Current.SettingsService;
        if (checkBox.IsChecked == true)
        {
            DesktopOrganizationRule? owner = settingsService.Settings.DesktopOrganization.DesktopOrganizationRules
                .FirstOrDefault(rule =>
                    !ReferenceEquals(rule, _selectedRule) &&
                    rule.IsEnabled &&
                    rule.CategoryIds.Contains(categoryId, StringComparer.Ordinal));
            if (owner is not null)
            {
                checkBox.IsChecked = false;
                await OfferRuleTransferAsync(
                    T($"DesktopOrganization.Category.{categoryId}"),
                    owner,
                    async () =>
                    {
                        owner.CategoryIds.RemoveAll(value =>
                            string.Equals(value, categoryId, StringComparison.Ordinal));
                        EnsureSelectedRuleRegistered();
                        if (!_selectedRule.CategoryIds.Contains(categoryId, StringComparer.Ordinal))
                        {
                            _selectedRule.CategoryIds.Add(categoryId);
                        }
                        _selectedRule.IsEnabled = true;
                        await settingsService.SaveAsync();
                        ShowRuleDetail();
                        RefreshStatusOnly();
                    });
                return;
            }

            EnsureSelectedRuleRegistered();
            if (!_selectedRule.CategoryIds.Contains(categoryId, StringComparer.Ordinal))
            {
                _selectedRule.CategoryIds.Add(categoryId);
            }
            _selectedRule.IsEnabled = true;
            _isRefreshing = true;
            RuleEnabledToggle.IsOn = true;
            _isRefreshing = false;
        }
        else
        {
            _selectedRule.CategoryIds.RemoveAll(value =>
                string.Equals(value, categoryId, StringComparison.Ordinal));
            DisableEmptySelectedRule();
        }

        await settingsService.SaveAsync();
        BuildRuleTokens();
        RefreshStatusOnly();
    }

    private FrameworkElement CreateFileTypeOptionContent(
        string title,
        IReadOnlyList<string> extensions,
        string? emptyDescription = null)
    {
        var content = new StackPanel
        {
            Spacing = 2,
            Margin = new Thickness(0, 3, 0, 3)
        };
        content.Children.Add(new TextBlock
        {
            Text = title,
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(new TextBlock
        {
            Text = extensions.Count > 0
                ? string.Join("  ·  ", extensions)
                : emptyDescription ?? string.Empty,
            FontSize = 12,
            Foreground = SecondaryTextBrush(),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 520
        });
        return content;
    }

    private async void RuleEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isRefreshing || _selectedRule is null)
        {
            return;
        }

        if (RuleEnabledToggle.IsOn && !HasAssignments(_selectedRule))
        {
            _isRefreshing = true;
            RuleEnabledToggle.IsOn = false;
            _isRefreshing = false;
            FileTypeExpander.IsExpanded = true;
            return;
        }

        EnsureSelectedRuleRegistered();
        _selectedRule.IsEnabled = RuleEnabledToggle.IsOn;
        await global::DeskBox.App.Current.SettingsService.SaveAsync();
        RefreshStatusOnly();
    }

    private void AddExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        _ = AddExtensionAsync();
    }

    private void ExtensionInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = AddExtensionAsync();
        }
    }

    private async Task AddExtensionAsync()
    {
        if (_selectedRule is null || _selectedWidget is null)
        {
            return;
        }

        string extension = DesktopOrganizationClassifier.NormalizeExtension(ExtensionInput.Text);
        if (string.IsNullOrEmpty(extension))
        {
            return;
        }

        var settingsService = global::DeskBox.App.Current.SettingsService;
        DesktopOrganizationRule? currentOwner = settingsService.Settings.DesktopOrganization.DesktopOrganizationRules
            .FirstOrDefault(rule =>
                !ReferenceEquals(rule, _selectedRule) &&
                rule.IsEnabled &&
                rule.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        if (currentOwner is not null)
        {
            await OfferRuleTransferAsync(
                extension,
                currentOwner,
                async () =>
                {
                    EnsureSelectedRuleRegistered();
                    _ruleResolver.AssignExtensionExclusively(
                        settingsService.Settings.DesktopOrganization.DesktopOrganizationRules,
                        _selectedWidget.Id,
                        extension);
                    _selectedRule.IsEnabled = true;
                    ExtensionInput.Text = string.Empty;
                    await settingsService.SaveAsync();
                    ShowRuleDetail();
                    RefreshStatusOnly();
                });
            return;
        }

        EnsureSelectedRuleRegistered();
        _ruleResolver.AssignExtensionExclusively(
            settingsService.Settings.DesktopOrganization.DesktopOrganizationRules,
            _selectedWidget.Id,
            extension);
        _selectedRule.IsEnabled = true;
        ExtensionInput.Text = string.Empty;
        await settingsService.SaveAsync();
        ShowRuleDetail();
        RefreshStatusOnly();
    }

    private void BuildExtensionChips()
    {
        ExtensionChips.Children.Clear();
        if (_selectedRule is null)
        {
            return;
        }

        foreach (string extension in _selectedRule.Extensions)
        {
            var button = new Button
            {
                Content = $"{extension}  ×",
                Tag = extension,
                Padding = new Thickness(9, 4, 9, 4)
            };
            button.Click += RemoveExtensionButton_Click;
            ExtensionChips.Children.Add(button);
        }
    }

    private void AddExcludedExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        _ = AddExcludedExtensionAsync();
    }

    private void ExcludedExtensionInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (DeskBox.Helpers.ImeCompositionGuard.IsImeCompositionKeyEvent(e))
        {
            return;
        }

        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = AddExcludedExtensionAsync();
        }
    }

    private async Task AddExcludedExtensionAsync()
    {
        if (_selectedRule is null)
        {
            return;
        }

        string extension = DesktopOrganizationClassifier.NormalizeExtension(
            ExcludedExtensionInput.Text);
        if (string.IsNullOrEmpty(extension))
        {
            return;
        }

        if (!_selectedRule.ExcludedExtensions.Contains(
                extension,
                StringComparer.OrdinalIgnoreCase))
        {
            _selectedRule.ExcludedExtensions.Add(extension);
        }

        EnsureSelectedRuleRegistered();
        ExcludedExtensionInput.Text = string.Empty;
        await global::DeskBox.App.Current.SettingsService.SaveAsync();
        BuildExcludedExtensionChips();
    }

    private void BuildExcludedExtensionChips()
    {
        ExcludedExtensionChips.Children.Clear();
        if (_selectedRule is null)
        {
            return;
        }

        foreach (string extension in _selectedRule.ExcludedExtensions)
        {
            var button = new Button
            {
                Content = $"{extension}  ×",
                Tag = extension,
                Padding = new Thickness(9, 4, 9, 4)
            };
            button.Click += RemoveExcludedExtensionButton_Click;
            ExcludedExtensionChips.Children.Add(button);
        }
    }

    private async void RemoveExcludedExtensionButton_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (_selectedRule is null || sender is not Button { Tag: string extension })
        {
            return;
        }

        _selectedRule.ExcludedExtensions.RemoveAll(value =>
            string.Equals(value, extension, StringComparison.OrdinalIgnoreCase));
        await global::DeskBox.App.Current.SettingsService.SaveAsync();
        BuildExcludedExtensionChips();
        BuildRuleTokens();
    }

    private async void RemoveExtensionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRule is null || sender is not Button { Tag: string extension })
        {
            return;
        }

        _selectedRule.Extensions.RemoveAll(value =>
            string.Equals(value, extension, StringComparison.OrdinalIgnoreCase));
        DisableEmptySelectedRule();
        await global::DeskBox.App.Current.SettingsService.SaveAsync();
        BuildExtensionChips();
        BuildRuleTokens();
        RefreshStatusOnly();
    }

    public bool TryNavigateBackToRuleList()
    {
        if (!IsRuleDetailOpen)
        {
            return false;
        }

        CloseRuleDetail(animate: true);
        return true;
    }

    public void CloseRuleDetail(bool animate = true)
    {
        if (_selectedWidget is null && RuleDetailPanel.Visibility == Visibility.Collapsed)
        {
            return;
        }

        _selectedWidget = null;
        _selectedRule = null;
        _selectedRuleIsDraft = false;
        OrganizationOverviewPanel.Visibility = Visibility.Collapsed;
        RuleDetailPanel.Visibility = Visibility.Collapsed;
        RuleListPanel.Visibility = Visibility.Visible;
        RuleStatusInfo.IsOpen = false;
        ResetHostScrollToTop();
        Refresh();
        SubPageChanged?.Invoke(this, EventArgs.Empty);
        if (animate && IsLoaded)
        {
            RuleListPanel.UpdateLayout();
            DetailPageTransitionHelper.PlayNavigationEnter(RuleListPanel, -40f);
        }
    }

    private void BuildTypeOverview()
    {
        TypeOverviewExpander.Items.Clear();
        if (global::DeskBox.App.Current?.SettingsService is not { } settingsService)
        {
            return;
        }

        var settings = settingsService.Settings;
        var titleStyle = Resources.TryGetValue("SettingTitleTextStyle", out object? titleStyleObj)
            ? titleStyleObj as Style
            : null;
        var descriptionStyle = Resources.TryGetValue("SettingDescriptionTextStyle", out object? styleObj)
            ? styleObj as Style
            : null;
        var dividerBrush = NeutralInteractionBrush.ResolveThemedResource("CardStrokeColorDefaultBrush", this) ??
                           new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x20, 0x80, 0x80, 0x80));

        var rowsPanel = new StackPanel { Spacing = 8 };
        string[] categories = DesktopOrganizationCategoryIds.DefaultOrder.ToArray();
        for (int i = 0; i < categories.Length; i++)
        {
            string categoryId = categories[i];
            DesktopOrganizationRule? rule = settings.DesktopOrganization.DesktopOrganizationRules
                .FirstOrDefault(candidate =>
                    candidate.IsEnabled &&
                    candidate.CategoryIds.Contains(categoryId, StringComparer.Ordinal));
            WidgetConfig? widget = rule is null
                ? null
                : settings.WidgetLayout.Widgets.FirstOrDefault(candidate => candidate.Id == rule.TargetWidgetId);

            if (i > 0)
            {
                rowsPanel.Children.Add(new Border
                {
                    Height = 1,
                    Background = dividerBrush
                });
            }

            var rowGrid = new Grid
            {
                ColumnSpacing = 12,
                Padding = new Thickness(0, 2, 0, 2)
            };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var headerText = new TextBlock
            {
                Text = T($"DesktopOrganization.Category.{categoryId}"),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (titleStyle is not null)
            {
                headerText.Style = titleStyle;
            }

            var target = new TextBlock
            {
                Text = widget?.Name ?? T("DesktopOrganization.Rules.KeepOnDesktop"),
                Foreground = SecondaryTextBrush(),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (descriptionStyle is not null)
            {
                target.Style = descriptionStyle;
            }

            Grid.SetColumn(target, 1);
            rowGrid.Children.Add(headerText);
            rowGrid.Children.Add(target);
            rowsPanel.Children.Add(rowGrid);
        }

        TypeOverviewExpander.Items.Add(new CommunityToolkit.WinUI.Controls.SettingsCard
        {
            ContentAlignment = CommunityToolkit.WinUI.Controls.ContentAlignment.Vertical,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = rowsPanel
        });
    }

    private string BuildRuleSummary(DesktopOrganizationRule? rule)
    {
        if (rule is null ||
            (!rule.CategoryIds.Any() && !rule.SubtypeIds.Any() && !rule.Extensions.Any()))
        {
            return T("DesktopOrganization.Rules.Unconfigured");
        }

        var values = rule.CategoryIds
            .Select(category => T($"DesktopOrganization.Category.{category}"))
            .Concat(rule.SubtypeIds.Select(subtype =>
                T($"DesktopOrganization.Subtype.{subtype}")))
            .Concat(rule.Extensions)
            .Take(4)
            .ToList();
        string state = rule.IsEnabled
            ? T("DesktopOrganization.Rules.Enabled")
            : T("DesktopOrganization.Rules.Paused");
        return $"{string.Join("、", values)} · {state}";
    }

    private void BuildRuleTokens()
    {
        if (_selectedRule is null)
        {
            FileTypeExpander.Description = T("DesktopOrganization.Rules.Unconfigured");
            RuleEnabledCard.Description = T("DesktopOrganization.Rule.NoTypesTitle");
            RuleEnabledToggle.IsEnabled = false;
            return;
        }

        var values = _selectedRule.CategoryIds
            .Select(category => T($"DesktopOrganization.Category.{category}"))
            .Concat(_selectedRule.SubtypeIds.Select(subtype =>
                T($"DesktopOrganization.Subtype.{subtype}")))
            .Concat(_selectedRule.Extensions)
            .ToList();
        bool hasAssignments = values.Count > 0;
        FileTypeExpander.Description = hasAssignments
            ? string.Join("、", values.Take(4)) + (values.Count > 4 ? $" +{values.Count - 4}" : string.Empty)
            : T("DesktopOrganization.Rules.Unconfigured");
        RuleEnabledCard.Description = hasAssignments
            ? null!
            : T("DesktopOrganization.Rule.NoTypesTitle");
        RuleEnabledToggle.IsEnabled = hasAssignments;
    }

    private async Task OfferRuleTransferAsync(
        string typeName,
        DesktopOrganizationRule owner,
        Func<Task> accept)
    {
        if (XamlRoot is null)
        {
            return;
        }

        var settings = global::DeskBox.App.Current.SettingsService.Settings;
        WidgetConfig? selectedWidget = _selectedWidget;
        WidgetConfig? ownerWidget = settings.WidgetLayout.Widgets.FirstOrDefault(widget =>
            string.Equals(widget.Id, owner.TargetWidgetId, StringComparison.Ordinal));

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
                    selectedWidget?.Name ?? string.Empty),
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

    private void EnsureSelectedRuleRegistered()
    {
        if (_selectedRule is null || !_selectedRuleIsDraft)
        {
            return;
        }

        global::DeskBox.App.Current.SettingsService.Settings.DesktopOrganization.DesktopOrganizationRules.Add(
            _selectedRule);
        _selectedRuleIsDraft = false;
    }

    private void DisableEmptySelectedRule()
    {
        if (_selectedRule is null || HasAssignments(_selectedRule))
        {
            return;
        }

        _selectedRule.IsEnabled = false;
        _isRefreshing = true;
        RuleEnabledToggle.IsOn = false;
        _isRefreshing = false;
    }

    private void RefreshStatusOnly()
    {
        Refresh();
    }

    private static bool HasAssignments(DesktopOrganizationRule rule) =>
        rule.CategoryIds.Count > 0 ||
        rule.SubtypeIds.Count > 0 ||
        rule.Extensions.Count > 0;

    private static bool IsEffectiveRule(
        DesktopOrganizationRule rule,
        AppSettings settings)
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

    private static string T(string key) =>
        global::DeskBox.App.Current.LocalizationService.T(key);

    private static string Format(string key, params object[] values) =>
        global::DeskBox.App.Current.LocalizationService.Format(key, values);
}
