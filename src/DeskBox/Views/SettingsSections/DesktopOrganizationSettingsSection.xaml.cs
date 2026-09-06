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

    public DesktopOrganizationSettingsSection()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        if (global::DeskBox.App.Current?.SettingsService is not { } settingsService)
        {
            return;
        }

        _isRefreshing = true;
        AutoOrganizationToggle.IsOn = settingsService.Settings.DesktopAutoOrganizationEnabled;
        WidgetSearchBox.PlaceholderText = T("DesktopOrganization.Rules.SearchPlaceholder");
        ExtensionInput.PlaceholderText = T("DesktopOrganization.Rule.ExtensionPlaceholder");
        ExcludedExtensionInput.PlaceholderText = T(
            "DesktopOrganization.Rule.ExclusionPlaceholder");
        OrganizationHistoryEntry? latest = global::DeskBox.App.Current.OrganizerService
            .GetLatestUndoableEntry();
        UndoOrganizationButton.Visibility = latest is null
            ? Visibility.Collapsed
            : Visibility.Visible;
        UndoOrganizationButton.IsEnabled = latest is not null;
        UndoOrganizationButton.Tag = latest?.Id;
        int effectiveRuleCount = settingsService.Settings.DesktopOrganizationRules.Count(
            rule => IsEffectiveRule(rule, settingsService.Settings));
        int effectiveWidgetCount = settingsService.Settings.DesktopOrganizationRules
            .Where(rule => IsEffectiveRule(rule, settingsService.Settings))
            .Select(rule => rule.TargetWidgetId)
            .Distinct(StringComparer.Ordinal)
            .Count();
        bool hasEffectiveRules = effectiveRuleCount > 0;
        AutoSetupCard.Visibility = hasEffectiveRules
            ? Visibility.Collapsed
            : Visibility.Visible;
        AutoToggleCard.Visibility = hasEffectiveRules
            ? Visibility.Visible
            : Visibility.Collapsed;
        OrganizationSummaryCard.Header = settingsService.Settings.DesktopAutoOrganizationEnabled
            ? T("DesktopOrganization.Status.Running")
            : T("DesktopOrganization.Status.Ready");
        OrganizationSummaryCard.Description = hasEffectiveRules
            ? Format(
                "DesktopOrganization.Status.Configured",
                effectiveWidgetCount,
                effectiveRuleCount)
            : T("DesktopOrganization.Status.NoRules");
        _isRefreshing = false;
        RefreshWidgetCards();
        BuildTypeOverview();
    }

    private void RefreshWidgetCards()
    {
        WidgetRuleCards.Children.Clear();
        var settings = global::DeskBox.App.Current.SettingsService.Settings;
        string query = WidgetSearchBox.Text?.Trim() ?? string.Empty;
        var widgets = settings.Widgets
            .Where(widget =>
                widget.WidgetKind == WidgetKind.File &&
                !widget.IsDisabled &&
                !settings.DeletedWidgetIds.Contains(widget.Id) &&
                (query.Length == 0 ||
                 widget.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
            .OrderBy(widget => widget.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        WidgetSearchBox.Visibility = widgets.Count > 6
            ? Visibility.Visible
            : Visibility.Collapsed;

        foreach (WidgetConfig widget in widgets)
        {
            DesktopOrganizationRule? rule = settings.DesktopOrganizationRules.FirstOrDefault(candidate =>
                string.Equals(candidate.TargetWidgetId, widget.Id, StringComparison.Ordinal));
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Tag = widget.Id,
                Padding = new Thickness(16, 11, 16, 11)
            };
            button.Click += WidgetRuleButton_Click;

            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new FontIcon { Glyph = "\uE8B7", FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
            var text = new StackPanel { Spacing = 3 };
            text.Children.Add(new TextBlock
            {
                Text = widget.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            text.Children.Add(new TextBlock
            {
                Text = BuildRuleSummary(rule),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                TextWrapping = TextWrapping.Wrap
            });
            var chevron = new FontIcon
            {
                Glyph = "\uE76C",
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 1);
            Grid.SetColumn(chevron, 2);
            grid.Children.Add(icon);
            grid.Children.Add(text);
            grid.Children.Add(chevron);
            button.Content = grid;
            WidgetRuleCards.Children.Add(button);
        }

        if (widgets.Count == 0)
        {
            WidgetRuleCards.Children.Add(new TextBlock
            {
                Text = T("DesktopOrganization.Rules.Empty"),
                Margin = new Thickness(8, 12, 8, 4),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
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
        service.Settings.DesktopAutoOrganizationEnabled = AutoOrganizationToggle.IsOn;
        service.Settings.DesktopAutoOrganizationBaselineUtc = AutoOrganizationToggle.IsOn
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

    private void OrganizeNowButton_Click(object sender, RoutedEventArgs e)
    {
        global::DeskBox.App.Current.ShowDesktopOrganizationWindow();
    }

    private void AutoSetupButton_Click(object sender, RoutedEventArgs e)
    {
        RuleStatusInfo.ActionButton = null;
        RuleStatusInfo.Severity = InfoBarSeverity.Informational;
        RuleStatusInfo.Title = T("DesktopOrganization.Auto.SetupInfoTitle");
        RuleStatusInfo.Message = T("DesktopOrganization.Auto.SetupInfoBody");
        RuleStatusInfo.IsOpen = true;
        WidgetRuleCards.StartBringIntoView(new BringIntoViewOptions
        {
            AnimationDesired = true,
            VerticalAlignmentRatio = 0.2
        });
    }

    private async void UndoOrganizationButton_Click(object sender, RoutedEventArgs e)
    {
        if (UndoOrganizationButton.Tag is not string historyId ||
            global::DeskBox.App.Current.WidgetManager is not { } widgetManager)
        {
            return;
        }

        UndoOrganizationButton.IsEnabled = false;
        try
        {
            var app = global::DeskBox.App.Current;
            var coordinator = new DesktopOrganizationCoordinator(
                app.SettingsService,
                app.FileService,
                widgetManager,
                app.OrganizerService,
                app.LocalizationService);
            nint owner = app.SettingsWindowInstance is { } window
                ? WinRT.Interop.WindowNative.GetWindowHandle(window) : 0;
            await coordinator.UndoAsync(historyId, owner);
            RuleStatusInfo.ActionButton = null;
            RuleStatusInfo.Severity = InfoBarSeverity.Success;
            RuleStatusInfo.Title = T("DesktopOrganization.Undo.Success");
            RuleStatusInfo.Message = string.Empty;
            RuleStatusInfo.IsOpen = true;
            Refresh();
        }
        catch (DesktopOrganizationIncompleteUndoException ex)
        {
            RuleStatusInfo.ActionButton = null;
            RuleStatusInfo.Severity = InfoBarSeverity.Warning;
            RuleStatusInfo.Title = T("DesktopOrganization.Public.UndoPendingTitle");
            RuleStatusInfo.Message = Format("DesktopOrganization.Public.UndoPending", ex.RestoredCount, ex.RemainingCount);
            RuleStatusInfo.IsOpen = true;
            Refresh();
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopOrganization] Undo from settings failed: {ex}");
            RuleStatusInfo.ActionButton = null;
            RuleStatusInfo.Severity = InfoBarSeverity.Error;
            RuleStatusInfo.Title = T("DesktopOrganization.Undo.Failed");
            RuleStatusInfo.Message = T("DesktopOrganization.Undo.FailedBody");
            RuleStatusInfo.IsOpen = true;
            UndoOrganizationButton.IsEnabled = true;
        }
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
        if (sender is not Button { Tag: string widgetId })
        {
            return;
        }

        var settings = global::DeskBox.App.Current.SettingsService.Settings;
        _selectedWidget = settings.Widgets.FirstOrDefault(widget =>
            string.Equals(widget.Id, widgetId, StringComparison.Ordinal));
        if (_selectedWidget is null)
        {
            return;
        }

        _selectedRule = settings.DesktopOrganizationRules.FirstOrDefault(rule =>
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

        RuleListPanel.Visibility = Visibility.Collapsed;
        RuleDetailPanel.Visibility = Visibility.Visible;
        RuleStatusInfo.IsOpen = false;
        RuleDetailTitle.Text = _selectedWidget.Name;
        RuleDetailPath.Text = _selectedWidget.MappedFolderPath ?? string.Empty;
        _isRefreshing = true;
        RuleEnabledToggle.IsOn = _selectedRule.IsEnabled;
        _isRefreshing = false;
        BuildCategoryChecks();
        BuildSubtypeChecks();
        BuildExtensionChips();
        BuildExcludedExtensionChips();
        BuildRuleTokens();
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
            DesktopOrganizationRule? owner = settingsService.Settings.DesktopOrganizationRules
                .FirstOrDefault(rule =>
                    !ReferenceEquals(rule, _selectedRule) &&
                    rule.IsEnabled &&
                    rule.SubtypeIds.Contains(subtypeId, StringComparer.Ordinal));
            if (owner is not null)
            {
                checkBox.IsChecked = false;
                OfferRuleTransfer(
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
            DesktopOrganizationRule? owner = settingsService.Settings.DesktopOrganizationRules
                .FirstOrDefault(rule =>
                    !ReferenceEquals(rule, _selectedRule) &&
                    rule.IsEnabled &&
                    rule.CategoryIds.Contains(categoryId, StringComparer.Ordinal));
            if (owner is not null)
            {
                checkBox.IsChecked = false;
                OfferRuleTransfer(
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

    private static FrameworkElement CreateFileTypeOptionContent(
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
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
                "TextFillColorSecondaryBrush"],
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
        DesktopOrganizationRule? currentOwner = settingsService.Settings.DesktopOrganizationRules
            .FirstOrDefault(rule =>
                !ReferenceEquals(rule, _selectedRule) &&
                rule.IsEnabled &&
                rule.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase));
        if (currentOwner is not null)
        {
            OfferRuleTransfer(
                extension,
                currentOwner,
                async () =>
                {
                    EnsureSelectedRuleRegistered();
                    _ruleResolver.AssignExtensionExclusively(
                        settingsService.Settings.DesktopOrganizationRules,
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
            settingsService.Settings.DesktopOrganizationRules,
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

    private void CloseRuleDetailButton_Click(object sender, RoutedEventArgs e)
    {
        _selectedWidget = null;
        _selectedRule = null;
        _selectedRuleIsDraft = false;
        RuleDetailPanel.Visibility = Visibility.Collapsed;
        RuleListPanel.Visibility = Visibility.Visible;
        RuleStatusInfo.IsOpen = false;
        Refresh();
    }

    private void BuildTypeOverview()
    {
        TypeOverviewRows.Children.Clear();
        var settings = global::DeskBox.App.Current.SettingsService.Settings;
        foreach (string categoryId in DesktopOrganizationCategoryIds.DefaultOrder)
        {
            DesktopOrganizationRule? rule = settings.DesktopOrganizationRules
                .FirstOrDefault(candidate =>
                    candidate.IsEnabled &&
                    candidate.CategoryIds.Contains(categoryId, StringComparer.Ordinal));
            WidgetConfig? widget = rule is null
                ? null
                : settings.Widgets.FirstOrDefault(candidate => candidate.Id == rule.TargetWidgetId);

            var border = new Border
            {
                Style = (Style)Resources["SettingsGroupStyle"]
            };
            var grid = new Grid
            {
                Padding = new Thickness(16, 11, 16, 11),
                ColumnSpacing = 12
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.Children.Add(new TextBlock
            {
                Text = T($"DesktopOrganization.Category.{categoryId}"),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            var target = new TextBlock
            {
                Text = widget?.Name ?? T("DesktopOrganization.Rules.KeepOnDesktop"),
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
            };
            Grid.SetColumn(target, 1);
            grid.Children.Add(target);
            border.Child = grid;
            TypeOverviewRows.Children.Add(border);
        }
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
            ? null
            : T("DesktopOrganization.Rule.NoTypesTitle");
        RuleEnabledToggle.IsEnabled = hasAssignments;
    }

    private void OfferRuleTransfer(
        string typeName,
        DesktopOrganizationRule owner,
        Func<Task> accept)
    {
        var settings = global::DeskBox.App.Current.SettingsService.Settings;
        WidgetConfig? selectedWidget = _selectedWidget;
        WidgetConfig? ownerWidget = settings.Widgets.FirstOrDefault(widget =>
            string.Equals(widget.Id, owner.TargetWidgetId, StringComparison.Ordinal));
        RuleStatusInfo.Severity = InfoBarSeverity.Warning;
        RuleStatusInfo.Title = T("DesktopOrganization.Rule.TransferTitle");
        RuleStatusInfo.Message = Format(
            "DesktopOrganization.Rule.TransferBody",
            typeName,
            ownerWidget?.Name ?? owner.TargetWidgetId,
            selectedWidget?.Name ?? string.Empty);
        var action = new Button
        {
            Content = T("DesktopOrganization.Rule.TransferAction")
        };
        action.Click += async (_, _) =>
        {
            action.IsEnabled = false;
            try
            {
                await accept();
                RuleStatusInfo.Severity = InfoBarSeverity.Success;
                RuleStatusInfo.Title = T("DesktopOrganization.Rule.TransferSuccess");
                RuleStatusInfo.Message = Format(
                    "DesktopOrganization.Rule.TransferSuccessBody",
                    typeName,
                    selectedWidget?.Name ?? string.Empty);
                RuleStatusInfo.ActionButton = null;
            }
            catch (Exception ex)
            {
                App.Log($"[DesktopOrganization] Rule transfer failed: {ex}");
                RuleStatusInfo.Severity = InfoBarSeverity.Error;
                RuleStatusInfo.Title = T("DesktopOrganization.Result.FailedTitle");
                RuleStatusInfo.Message = T("DesktopOrganization.Result.FailedBody");
                action.IsEnabled = true;
            }
        };
        RuleStatusInfo.ActionButton = action;
        RuleStatusInfo.IsOpen = true;
    }

    private void EnsureSelectedRuleRegistered()
    {
        if (_selectedRule is null || !_selectedRuleIsDraft)
        {
            return;
        }

        global::DeskBox.App.Current.SettingsService.Settings.DesktopOrganizationRules.Add(
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

        return settings.Widgets.Any(widget =>
            string.Equals(widget.Id, rule.TargetWidgetId, StringComparison.Ordinal) &&
            widget.WidgetKind == WidgetKind.File &&
            !widget.IsDisabled &&
            !settings.DeletedWidgetIds.Contains(widget.Id) &&
            !string.IsNullOrWhiteSpace(widget.MappedFolderPath));
    }

    private static string T(string key) =>
        global::DeskBox.App.Current.LocalizationService.T(key);

    private static string Format(string key, params object[] values) =>
        global::DeskBox.App.Current.LocalizationService.Format(key, values);
}
