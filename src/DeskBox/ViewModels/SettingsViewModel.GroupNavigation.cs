using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private const string GroupBooleanFollowDefault = "FollowDefault";
    private const string GroupBooleanOn = "On";
    private const string GroupBooleanOff = "Off";

    public string WidgetGroupOverviewSummaryText
    {
        get
        {
            int count = _settingsService.Settings.WidgetGroups.Count(group =>
                group.MemberIds.Count >= 2);
            string layout = GetWidgetGroupNavigationDisplayName(
                SelectedWidgetGroupDefaultNavigationStyle);
            return count == 0
                ? _localizationService.Format(
                    "Settings.WidgetGroups.Overview.Ready",
                    layout)
                : _localizationService.Format(
                    "Settings.WidgetGroups.Overview.Summary",
                    count,
                    layout);
        }
    }

    public string SelectedWidgetGroupDefaultNavigationStyle
    {
        get => WidgetGroupNavigationStyles.Normalize(
            _settingsService.Settings.WidgetGroupDefaultNavigationStyle,
            allowFollowDefault: false);
        set
        {
            string normalized = WidgetGroupNavigationStyles.Normalize(
                value,
                allowFollowDefault: false);
            if (string.Equals(
                    _settingsService.Settings.WidgetGroupDefaultNavigationStyle,
                    normalized,
                    StringComparison.Ordinal))
            {
                return;
            }

            _settingsService.Settings.WidgetGroupDefaultNavigationStyle = normalized;
            SaveWidgetGroupPresentationChange();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableWidgetGroupNavigationStyleOptions =>
        WrapOptions(
        [
            new(WidgetGroupNavigationStyles.Tabs, T("Settings.WidgetGroupNavigation.Tabs")),
            new(WidgetGroupNavigationStyles.Stack, T("Settings.WidgetGroupNavigation.Stack"))
        ]);

    public string SelectedWidgetGroupDefaultTitleDisplayMode
    {
        get => WidgetGroupTitleDisplayModes.Normalize(
            _settingsService.Settings.WidgetGroupDefaultTitleDisplayMode,
            allowFollowDefault: false);
        set
        {
            string normalized = WidgetGroupTitleDisplayModes.Normalize(
                value,
                allowFollowDefault: false);
            if (string.Equals(
                    _settingsService.Settings.WidgetGroupDefaultTitleDisplayMode,
                    normalized,
                    StringComparison.Ordinal))
            {
                return;
            }

            _settingsService.Settings.WidgetGroupDefaultTitleDisplayMode = normalized;
            SaveWidgetGroupPresentationChange();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<SettingsOption> AvailableWidgetGroupTitleDisplayModeOptions =>
        WrapOptions(
        [
            new(WidgetGroupTitleDisplayModes.IconAndText, T("Settings.WidgetGroupTitle.IconAndText")),
            new(WidgetGroupTitleDisplayModes.IconOnly, T("Settings.WidgetGroupTitle.IconOnly")),
            new(WidgetGroupTitleDisplayModes.TextOnly, T("Settings.WidgetGroupTitle.TextOnly"))
        ]);

    public bool IsWidgetGroupWheelSwitchEnabled
    {
        get => _settingsService.Settings.WidgetGroupWheelSwitchEnabled;
        set
        {
            if (_settingsService.Settings.WidgetGroupWheelSwitchEnabled == value)
            {
                return;
            }

            _settingsService.Settings.WidgetGroupWheelSwitchEnabled = value;
            SaveWidgetGroupPresentationChange();
            OnPropertyChanged();
        }
    }

    public bool IsWidgetGroupHoverSwitchEnabled
    {
        get => _settingsService.Settings.WidgetGroupHoverSwitchEnabled;
        set
        {
            if (_settingsService.Settings.WidgetGroupHoverSwitchEnabled == value)
            {
                return;
            }

            _settingsService.Settings.WidgetGroupHoverSwitchEnabled = value;
            SaveWidgetGroupPresentationChange();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<WidgetGroupSettingsItem> ExistingWidgetGroupItems =>
        _settingsService.Settings.WidgetGroups
            .Where(group => group.MemberIds.Count >= 2)
            .Select(CreateWidgetGroupSettingsItem)
            .ToArray();

    public Visibility ExistingWidgetGroupsVisibility =>
        ExistingWidgetGroupItems.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public Visibility ExistingWidgetGroupsEmptyVisibility =>
        ExistingWidgetGroupItems.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    public void RefreshWidgetGroupSettings()
    {
        OnPropertyChanged(nameof(SelectedWidgetGroupDefaultNavigationStyle));
        OnPropertyChanged(nameof(SelectedWidgetGroupDefaultTitleDisplayMode));
        OnPropertyChanged(nameof(IsWidgetGroupWheelSwitchEnabled));
        OnPropertyChanged(nameof(IsWidgetGroupHoverSwitchEnabled));
        OnPropertyChanged(nameof(WidgetGroupOverviewSummaryText));
        NotifyExistingWidgetGroupPropertiesChanged();
    }

    public bool RenameWidgetGroup(string groupId, string? name)
    {
        WidgetGroupConfig? group = FindWidgetGroup(groupId);
        if (group is null)
        {
            return false;
        }

        string normalized = name?.Trim() ?? string.Empty;
        if (string.Equals(group.Name, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        group.Name = normalized;
        CompleteWidgetGroupSettingsChange();
        NotifyExistingWidgetGroupPropertiesChanged();
        return true;
    }

    public bool SetWidgetGroupNavigationStyle(string groupId, string? value)
    {
        WidgetGroupConfig? group = FindWidgetGroup(groupId);
        if (group is null)
        {
            return false;
        }

        string normalized = WidgetGroupNavigationStyles.Normalize(
            value,
            allowFollowDefault: true);
        if (string.Equals(group.NavigationStyle, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        string previous = WidgetGroupNavigationStyles.Normalize(
            group.NavigationStyle,
            allowFollowDefault: true);
        group.NavigationStyle = normalized;
        if (previous == WidgetGroupNavigationStyles.Tabs &&
            normalized != WidgetGroupNavigationStyles.Tabs &&
            group.WheelSwitchEnabled == false)
        {
            group.WheelSwitchEnabled = null;
        }

        CompleteWidgetGroupSettingsChange();
        return true;
    }

    public bool SetWidgetGroupTitleDisplayMode(string groupId, string? value)
    {
        WidgetGroupConfig? group = FindWidgetGroup(groupId);
        if (group is null)
        {
            return false;
        }

        string normalized = WidgetGroupTitleDisplayModes.Normalize(
            value,
            allowFollowDefault: true);
        if (string.Equals(group.TitleDisplayMode, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        group.TitleDisplayMode = normalized;
        CompleteWidgetGroupSettingsChange();
        return true;
    }

    public bool SetWidgetGroupWheelSetting(string groupId, string? value)
    {
        WidgetGroupConfig? group = FindWidgetGroup(groupId);
        if (group is null)
        {
            return false;
        }

        bool? normalized = ParseGroupBooleanSetting(value);
        if (group.WheelSwitchEnabled == normalized)
        {
            return false;
        }

        group.WheelSwitchEnabled = normalized;
        CompleteWidgetGroupSettingsChange();
        return true;
    }

    public bool SetWidgetGroupHoverSetting(string groupId, string? value)
    {
        WidgetGroupConfig? group = FindWidgetGroup(groupId);
        if (group is null)
        {
            return false;
        }

        bool? normalized = ParseGroupBooleanSetting(value);
        if (group.HoverSwitchEnabled == normalized)
        {
            return false;
        }

        group.HoverSwitchEnabled = normalized;
        CompleteWidgetGroupSettingsChange();
        return true;
    }

    public bool SetWidgetGroupCollapseBehavior(string groupId, string? value)
    {
        WidgetGroupConfig? group = FindWidgetGroup(groupId);
        if (group is null)
        {
            return false;
        }

        WidgetCollapseBehavior normalized = WidgetCollapseBehaviorNames.Normalize(
            value,
            WidgetCollapseBehavior.System,
            allowSystem: true);
        string settingValue = WidgetCollapseBehaviorNames.ToSettingValue(normalized);
        if (string.Equals(group.CollapseBehavior, settingValue, StringComparison.Ordinal))
        {
            return false;
        }

        group.CollapseBehavior = settingValue;
        CompleteWidgetGroupSettingsChange();
        return true;
    }

    public bool SetWidgetGroupChromeMode(string groupId, string? value)
    {
        WidgetGroupConfig? group = FindWidgetGroup(groupId);
        if (group is null)
        {
            return false;
        }

        WidgetChromeMode normalized = WidgetChromeModeNames.NormalizeMode(
            value,
            WidgetChromeMode.Standard);
        if (!WidgetGroupChromePolicy.IsSupportedGroupMode(normalized))
        {
            normalized = WidgetChromeMode.Standard;
        }

        string settingValue = WidgetChromeModeNames.ToSettingValue(normalized);
        if (string.Equals(group.ChromeMode, settingValue, StringComparison.Ordinal))
        {
            return false;
        }

        group.ChromeMode = settingValue;
        CompleteWidgetGroupSettingsChange();
        return true;
    }

    public bool ResetWidgetGroupOverrides(string groupId)
    {
        WidgetGroupConfig? group = FindWidgetGroup(groupId);
        if (group is null)
        {
            return false;
        }

        bool changed =
            group.NavigationStyle != WidgetGroupNavigationStyles.FollowDefault ||
            group.TitleDisplayMode != WidgetGroupTitleDisplayModes.FollowDefault ||
            group.WheelSwitchEnabled is not null ||
            group.HoverSwitchEnabled is not null ||
            WidgetCollapseBehaviorNames.Normalize(
                group.CollapseBehavior,
                WidgetCollapseBehavior.System,
                allowSystem: true) != WidgetCollapseBehavior.System ||
            WidgetGroupChromePolicy.NormalizePersistedMode(group.ChromeMode) !=
                WidgetChromeMode.Standard;
        if (!changed)
        {
            return false;
        }

        group.NavigationStyle = WidgetGroupNavigationStyles.FollowDefault;
        group.TitleDisplayMode = WidgetGroupTitleDisplayModes.FollowDefault;
        group.WheelSwitchEnabled = null;
        group.HoverSwitchEnabled = null;
        group.CollapseBehavior = WidgetCollapseBehaviorNames.System;
        group.ChromeMode = WidgetChromeModeNames.Standard;
        CompleteWidgetGroupSettingsChange();
        NotifyExistingWidgetGroupPropertiesChanged();
        return true;
    }

    private void SaveWidgetGroupPresentationChange()
    {
        _settingsService.SaveDebounced();
        App.Current?.WidgetManager?.NotifyWidgetGroupPresentationSettingsChanged();
        OnPropertyChanged(nameof(WidgetGroupOverviewSummaryText));
        NotifyExistingWidgetGroupPropertiesChanged();
    }

    private void CompleteWidgetGroupSettingsChange()
    {
        _settingsService.SaveDebounced(notifySubscribers: false);
        App.Current?.WidgetManager?.NotifyWidgetGroupPresentationSettingsChanged();
        OnPropertyChanged(nameof(WidgetGroupOverviewSummaryText));
    }

    private WidgetGroupConfig? FindWidgetGroup(string groupId) =>
        _settingsService.Settings.WidgetGroups.FirstOrDefault(candidate =>
            string.Equals(candidate.Id, groupId, StringComparison.Ordinal));

    private WidgetGroupSettingsItem CreateWidgetGroupSettingsItem(
        WidgetGroupConfig group)
    {
        string configuredNavigation = WidgetGroupNavigationStyles.Normalize(
            group.NavigationStyle,
            allowFollowDefault: true);
        string effectiveNavigation = WidgetGroupNavigationStyles.Resolve(
            configuredNavigation,
            _settingsService.Settings.WidgetGroupDefaultNavigationStyle);
        string configuredTitleMode = WidgetGroupTitleDisplayModes.Normalize(
            group.TitleDisplayMode,
            allowFollowDefault: true);
        WidgetCollapseBehavior configuredCollapse =
            WidgetCollapseBehaviorNames.Normalize(
                group.CollapseBehavior,
                WidgetCollapseBehavior.System,
                allowSystem: true);
        WidgetChromeMode configuredChrome =
            WidgetGroupChromePolicy.NormalizePersistedMode(group.ChromeMode);

        bool hasOverrides =
            configuredNavigation != WidgetGroupNavigationStyles.FollowDefault ||
            configuredTitleMode != WidgetGroupTitleDisplayModes.FollowDefault ||
            group.WheelSwitchEnabled is not null ||
            group.HoverSwitchEnabled is not null ||
            configuredCollapse != WidgetCollapseBehavior.System ||
            configuredChrome != WidgetChromeMode.Standard;

        WidgetConfig? activeMember = _settingsService.Settings.Widgets
            .FirstOrDefault(widget => string.Equals(
                widget.Id,
                group.ActiveMemberId,
                StringComparison.Ordinal));
        string displayName = !string.IsNullOrWhiteSpace(group.Name)
            ? group.Name.Trim()
            : activeMember is not null
                ? GetWidgetDisplayName(activeMember)
                : T("Settings.WidgetGroups.Existing.Unnamed");
        string memberCount = _localizationService.Format(
            "Settings.WidgetGroups.Existing.MemberCount",
            group.MemberIds.Count);
        string summary = _localizationService.Format(
            hasOverrides
                ? "Settings.WidgetGroups.Existing.SummaryCustom"
                : "Settings.WidgetGroups.Existing.SummaryDefault",
            memberCount,
            GetWidgetGroupNavigationDisplayName(effectiveNavigation));

        var members = new List<WidgetGroupMemberSettingsItem>(group.MemberIds.Count);
        for (int index = 0; index < group.MemberIds.Count; index++)
        {
            string widgetId = group.MemberIds[index];
            WidgetConfig? widget = _settingsService.Settings.Widgets
                .FirstOrDefault(candidate => string.Equals(
                    candidate.Id,
                    widgetId,
                    StringComparison.Ordinal));
            members.Add(new WidgetGroupMemberSettingsItem(
                group.Id,
                widgetId,
                widget is null ? widgetId : GetWidgetDisplayName(widget),
                index > 0 ? group.MemberIds[index - 1] : null,
                index + 1 < group.MemberIds.Count ? group.MemberIds[index + 1] : null));
        }

        return new WidgetGroupSettingsItem(
            group.Id,
            group.MemberIds.FirstOrDefault(),
            displayName,
            summary,
            hasOverrides,
            configuredNavigation,
            CreateGroupNavigationOptions(),
            configuredTitleMode,
            CreateGroupTitleOptions(),
            FormatGroupBooleanSetting(group.WheelSwitchEnabled),
            CreateGroupBooleanOptions(_settingsService.Settings.WidgetGroupWheelSwitchEnabled),
            FormatGroupBooleanSetting(group.HoverSwitchEnabled),
            CreateGroupBooleanOptions(_settingsService.Settings.WidgetGroupHoverSwitchEnabled),
            WidgetCollapseBehaviorNames.ToSettingValue(configuredCollapse),
            CreateGroupCollapseOptions(),
            WidgetChromeModeNames.ToSettingValue(configuredChrome),
            CreateGroupChromeOptions(),
            members);
    }

    private IReadOnlyList<SettingsOption> CreateGroupNavigationOptions() =>
        WrapOptions(
        [
            new(
                WidgetGroupNavigationStyles.FollowDefault,
                FormatFollowDefault(GetWidgetGroupNavigationDisplayName(
                    SelectedWidgetGroupDefaultNavigationStyle))),
            new(WidgetGroupNavigationStyles.Tabs, T("Settings.WidgetGroupNavigation.Tabs")),
            new(WidgetGroupNavigationStyles.Stack, T("Settings.WidgetGroupNavigation.Stack"))
        ]);

    private IReadOnlyList<SettingsOption> CreateGroupTitleOptions() =>
        WrapOptions(
        [
            new(
                WidgetGroupTitleDisplayModes.FollowDefault,
                FormatFollowDefault(GetWidgetGroupTitleDisplayName(
                    SelectedWidgetGroupDefaultTitleDisplayMode))),
            new(WidgetGroupTitleDisplayModes.IconAndText, T("Settings.WidgetGroupTitle.IconAndText")),
            new(WidgetGroupTitleDisplayModes.IconOnly, T("Settings.WidgetGroupTitle.IconOnly")),
            new(WidgetGroupTitleDisplayModes.TextOnly, T("Settings.WidgetGroupTitle.TextOnly"))
        ]);

    private IReadOnlyList<SettingsOption> CreateGroupBooleanOptions(bool defaultValue) =>
        WrapOptions(
        [
            new(
                GroupBooleanFollowDefault,
                FormatFollowDefault(T(defaultValue ? "Common.On" : "Common.Off"))),
            new(GroupBooleanOn, T("Common.On")),
            new(GroupBooleanOff, T("Common.Off"))
        ]);

    private IReadOnlyList<SettingsOption> CreateGroupCollapseOptions()
    {
        string defaultBehavior = WidgetCollapseBehaviorNames.ToSettingValue(
            WidgetCollapseBehaviorNames.Normalize(
                _settingsService.Settings.WidgetCollapseBehavior));
        return WrapOptions(
        [
            new(
                WidgetCollapseBehaviorNames.System,
                FormatFollowDefault(GetWidgetCollapseBehaviorDisplayName(defaultBehavior))),
            new(
                WidgetCollapseBehaviorNames.Expanded,
                GetWidgetCollapseBehaviorDisplayName(WidgetCollapseBehaviorNames.Expanded)),
            new(
                WidgetCollapseBehaviorNames.Click,
                GetWidgetCollapseBehaviorDisplayName(WidgetCollapseBehaviorNames.Click)),
            new(
                WidgetCollapseBehaviorNames.Smart,
                GetWidgetCollapseBehaviorDisplayName(WidgetCollapseBehaviorNames.Smart))
        ]);
    }

    private IReadOnlyList<SettingsOption> CreateGroupChromeOptions() =>
        WrapOptions(
        [
            new(WidgetChromeModeNames.Standard, T("Settings.WidgetChrome.Standard")),
            new(WidgetChromeModeNames.Compact, T("Settings.WidgetChrome.Compact"))
        ]);

    private string FormatFollowDefault(string currentValue) =>
        _localizationService.Format(
            "Settings.WidgetGroups.FollowDefaultWithValue",
            currentValue);

    private string GetWidgetDisplayName(WidgetConfig widget)
    {
        if (!widget.IsDefaultTitle && !string.IsNullOrWhiteSpace(widget.Name))
        {
            return widget.Name.Trim();
        }

        string localized = GetWidgetKindDisplayName(widget.WidgetKind);
        return !string.IsNullOrWhiteSpace(localized)
            ? localized
            : !string.IsNullOrWhiteSpace(widget.Name)
                ? widget.Name.Trim()
                : widget.WidgetKind.ToString();
    }

    private static bool? ParseGroupBooleanSetting(string? value) => value switch
    {
        GroupBooleanOn => true,
        GroupBooleanOff => false,
        _ => null
    };

    private static string FormatGroupBooleanSetting(bool? value) => value switch
    {
        true => GroupBooleanOn,
        false => GroupBooleanOff,
        _ => GroupBooleanFollowDefault
    };

    private string GetWidgetGroupNavigationDisplayName(string style) =>
        WidgetGroupNavigationStyles.Normalize(
            style,
            allowFollowDefault: false) switch
        {
            WidgetGroupNavigationStyles.Tabs =>
                T("Settings.WidgetGroupNavigation.Tabs"),
            WidgetGroupNavigationStyles.Stack =>
                T("Settings.WidgetGroupNavigation.Stack"),
            _ => T("Settings.WidgetGroupNavigation.Stack")
        };

    private string GetWidgetGroupTitleDisplayName(string style) =>
        WidgetGroupTitleDisplayModes.Normalize(
            style,
            allowFollowDefault: false) switch
        {
            WidgetGroupTitleDisplayModes.IconOnly =>
                T("Settings.WidgetGroupTitle.IconOnly"),
            WidgetGroupTitleDisplayModes.TextOnly =>
                T("Settings.WidgetGroupTitle.TextOnly"),
            _ => T("Settings.WidgetGroupTitle.IconAndText")
        };

    private void NotifyExistingWidgetGroupPropertiesChanged()
    {
        OnPropertyChanged(nameof(ExistingWidgetGroupItems));
        OnPropertyChanged(nameof(ExistingWidgetGroupsVisibility));
        OnPropertyChanged(nameof(ExistingWidgetGroupsEmptyVisibility));
    }

    private string T(string key) => _localizationService.T(key);
}

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record WidgetGroupSettingsItem(
    string GroupId,
    string? FirstMemberId,
    string DisplayName,
    string Summary,
    bool HasOverrides,
    string NavigationStyle,
    IReadOnlyList<SettingsOption> NavigationOptions,
    string TitleDisplayMode,
    IReadOnlyList<SettingsOption> TitleOptions,
    string WheelSetting,
    IReadOnlyList<SettingsOption> WheelOptions,
    string HoverSetting,
    IReadOnlyList<SettingsOption> HoverOptions,
    string CollapseBehavior,
    IReadOnlyList<SettingsOption> CollapseOptions,
    string ChromeMode,
    IReadOnlyList<SettingsOption> ChromeOptions,
    IReadOnlyList<WidgetGroupMemberSettingsItem> Members);

[WinRT.GeneratedBindableCustomProperty]
public sealed partial record WidgetGroupMemberSettingsItem(
    string GroupId,
    string WidgetId,
    string DisplayName,
    string? MoveUpTargetWidgetId,
    string? MoveDownTargetWidgetId)
{
    public bool CanMoveUp => MoveUpTargetWidgetId is not null;
    public bool CanMoveDown => MoveDownTargetWidgetId is not null;
}
