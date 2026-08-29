using DeskBox.Controls;
using DeskBox.Models;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

internal static class WidgetGroupMenuBuilder
{
    public static void Append(
        MenuFlyout flyout,
        WidgetConfig config,
        WidgetManager? widgetManager,
        LocalizationService localizationService)
    {
        if (widgetManager is null)
        {
            return;
        }

        WidgetGroupPresentation? group =
            widgetManager.GetWidgetGroupPresentation(config.Id);
        IReadOnlyList<WidgetGroupJoinTarget> targets =
            widgetManager.GetWidgetGroupJoinTargets(config.Id);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var joinItem = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.Group.Join"),
            Icon = new FontIcon { Glyph = "\uE8A1" },
            IsEnabled = targets.Count > 0
        };
        foreach (WidgetGroupJoinTarget target in targets)
        {
            string targetText = target.MemberCount > 1
                ? localizationService.Format(
                    "Widget.Group.TargetWithCount",
                    target.DisplayName,
                    target.MemberCount)
                : target.DisplayName;
            if (!target.CanJoin &&
                !string.IsNullOrWhiteSpace(target.RejectionReasonKey))
            {
                targetText +=
                    $" · {localizationService.T(target.RejectionReasonKey)}";
            }

            var targetItem = new MenuFlyoutItem
            {
                Text = targetText,
                IsEnabled = target.CanJoin
            };
            targetItem.Click += async (_, _) => await TryExecuteAsync(
                () => widgetManager.MergeWidgetsAsync(
                    config.Id,
                    target.TargetWidgetId),
                $"merge source={config.Id} target={target.TargetWidgetId}");
            joinItem.Items.Add(targetItem);
        }
        flyout.Items.Add(joinItem);

        if (group is null)
        {
            return;
        }

        var groupControlMenu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.Group.Control"),
            Icon = new FontIcon { Glyph = "\uE713" }
        };

        var navigationMenu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.Group.NavigationStyle"),
            Icon = new FontIcon { Glyph = "\uE8A1" }
        };
        AddNavigationItem(
            WidgetGroupNavigationStyles.FollowDefault,
            FormatFollowDefault(GetNavigationName(
                widgetManager.GetWidgetGroupDefaultNavigationStyle())));
        AddNavigationItem(
            WidgetGroupNavigationStyles.Tabs,
            GetNavigationName(WidgetGroupNavigationStyles.Tabs));
        AddNavigationItem(
            WidgetGroupNavigationStyles.Stack,
            GetNavigationName(WidgetGroupNavigationStyles.Stack));
        groupControlMenu.Items.Add(navigationMenu);

        void AddNavigationItem(string style, string text)
        {
            string configured = WidgetGroupNavigationStyles.Normalize(
                widgetManager.GetWidgetGroupNavigationStyle(config.Id),
                allowFollowDefault: true);
            var item = new ToggleMenuFlyoutItem
            {
                Text = text,
                IsChecked = string.Equals(
                    configured,
                    style,
                    StringComparison.Ordinal)
            };
            item.Click += async (_, _) => await TryExecuteAsync(
                () => widgetManager.SetWidgetGroupNavigationStyleAsync(
                    config.Id,
                    style),
                $"navigation-style member={config.Id} style={style}");
            navigationMenu.Items.Add(item);
        }

        var titleStyleMenu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.Group.TitleDisplayMode"),
            Icon = new FontIcon { Glyph = "\uE8AB" }
        };
        AddTitleStyleItem(
            WidgetGroupTitleDisplayModes.FollowDefault,
            FormatFollowDefault(GetTitleName(
                widgetManager.GetWidgetGroupDefaultTitleDisplayMode())));
        AddTitleStyleItem(
            WidgetGroupTitleDisplayModes.IconAndText,
            GetTitleName(WidgetGroupTitleDisplayModes.IconAndText));
        AddTitleStyleItem(
            WidgetGroupTitleDisplayModes.IconOnly,
            GetTitleName(WidgetGroupTitleDisplayModes.IconOnly));
        AddTitleStyleItem(
            WidgetGroupTitleDisplayModes.TextOnly,
            GetTitleName(WidgetGroupTitleDisplayModes.TextOnly));
        groupControlMenu.Items.Add(titleStyleMenu);

        void AddTitleStyleItem(string style, string text)
        {
            string configured = WidgetGroupTitleDisplayModes.Normalize(
                widgetManager.GetWidgetGroupTitleDisplayMode(config.Id),
                allowFollowDefault: true);
            var item = new ToggleMenuFlyoutItem
            {
                Text = text,
                IsChecked = string.Equals(
                    configured,
                    style,
                    StringComparison.Ordinal)
            };
            item.Click += async (_, _) => await TryExecuteAsync(
                () => widgetManager.SetWidgetGroupTitleDisplayModeAsync(
                    config.Id,
                    style),
                $"title-style member={config.Id} style={style}");
            titleStyleMenu.Items.Add(item);
        }

        var wheelMenu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.Group.WheelSwitch"),
            Icon = new FontIcon { Glyph = "\uE7C2" }
        };
        AddWheelItem(
            value: null,
            FormatFollowDefault(GetBooleanName(
                widgetManager.GetWidgetGroupDefaultWheelSwitchEnabled())));
        AddWheelItem(value: true, GetBooleanName(true));
        AddWheelItem(value: false, GetBooleanName(false));
        groupControlMenu.Items.Add(wheelMenu);

        void AddWheelItem(bool? value, string text)
        {
            bool? configured =
                widgetManager.GetWidgetGroupWheelSwitchEnabled(config.Id);
            var item = new ToggleMenuFlyoutItem
            {
                Text = text,
                IsChecked = configured == value
            };
            item.Click += async (_, _) => await TryExecuteAsync(
                () => widgetManager.SetWidgetGroupWheelSwitchEnabledAsync(
                    config.Id,
                    value),
                $"wheel-switch member={config.Id} value={value}");
            wheelMenu.Items.Add(item);
        }

        var hoverMenu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.Group.HoverSwitch"),
            Icon = new FontIcon { Glyph = "\uE7C9" }
        };
        AddHoverItem(
            value: null,
            FormatFollowDefault(GetBooleanName(
                widgetManager.GetWidgetGroupDefaultHoverSwitchEnabled())));
        AddHoverItem(value: true, GetBooleanName(true));
        AddHoverItem(value: false, GetBooleanName(false));
        groupControlMenu.Items.Add(hoverMenu);

        void AddHoverItem(bool? value, string text)
        {
            bool? configured =
                widgetManager.GetWidgetGroupHoverSwitchEnabled(config.Id);
            var item = new ToggleMenuFlyoutItem
            {
                Text = text,
                IsChecked = configured == value
            };
            item.Click += async (_, _) => await TryExecuteAsync(
                () => widgetManager.SetWidgetGroupHoverSwitchEnabledAsync(
                    config.Id,
                    value),
                $"hover-switch member={config.Id} value={value}");
            hoverMenu.Items.Add(item);
        }

        var dissolveItem = new MenuFlyoutItem
        {
            Text = localizationService.T("Widget.Group.Dissolve"),
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        dissolveItem.Click += async (_, _) => await TryExecuteAsync(
            () => widgetManager.DissolveWidgetGroupContainingAsync(config.Id),
            $"dissolve member={config.Id}");
        groupControlMenu.Items.Add(new MenuFlyoutSeparator());
        groupControlMenu.Items.Add(dissolveItem);

        var removeItem = new MenuFlyoutItem
        {
            Text = localizationService.T("Widget.Group.RemoveCurrent"),
            Icon = new FontIcon { Glyph = "\uE8D9" }
        };
        removeItem.Click += async (_, _) => await TryExecuteAsync(
            () => widgetManager.RemoveWidgetFromGroupAsync(
                config.Id,
                revealStandalone: true),
            $"remove member={config.Id}");
        groupControlMenu.Items.Add(removeItem);
        flyout.Items.Add(groupControlMenu);

        string FormatFollowDefault(string value) =>
            localizationService.Format(
                "Settings.WidgetGroups.FollowDefaultWithValue",
                value);

        string GetBooleanName(bool value) =>
            localizationService.T(value ? "Common.On" : "Common.Off");

        string GetNavigationName(string style) =>
            WidgetGroupNavigationStyles.Normalize(
                style,
                allowFollowDefault: false) switch
            {
                WidgetGroupNavigationStyles.Tabs =>
                    localizationService.T("Widget.Group.Navigation.Tabs"),
                WidgetGroupNavigationStyles.Stack =>
                    localizationService.T("Widget.Group.Navigation.Stack"),
                _ => localizationService.T("Widget.Group.Navigation.Stack")
            };

        string GetTitleName(string style) =>
            WidgetGroupTitleDisplayModes.Normalize(
                style,
                allowFollowDefault: false) switch
            {
                WidgetGroupTitleDisplayModes.IconOnly =>
                    localizationService.T("Widget.Group.TitleDisplay.IconOnly"),
                WidgetGroupTitleDisplayModes.TextOnly =>
                    localizationService.T("Widget.Group.TitleDisplay.TextOnly"),
                _ => localizationService.T(
                    "Widget.Group.TitleDisplay.IconAndText")
            };
    }

    private static async Task TryExecuteAsync(
        Func<Task<bool>> operation,
        string description)
    {
        try
        {
            await operation();
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetGroup] Menu operation failed {description}: {ex}");
        }
    }
}
