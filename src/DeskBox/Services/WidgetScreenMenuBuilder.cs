using DeskBox.Models;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

/// <summary>
/// Builds the per-widget "移到显示器" submenu (spec 6.1): a radio group over
/// the online displays with the check on the widget's ACTUAL display, a
/// "始终在主显示器" toggle, and — for fallback placements — a disabled row
/// naming the disconnected home display. Hidden entirely on single-display
/// systems; null must never reach Items.Add.
/// </summary>
internal static class WidgetScreenMenuBuilder
{
    public static MenuFlyoutSubItem? TryCreate(
        LocalizationService localizationService,
        WidgetConfig config,
        Action<WidgetScreenBindingMode, string?> applyBinding)
    {
        IReadOnlyList<WidgetScreenInfo> screens = WidgetScreenCatalog.Capture();
        if (screens.Count <= 1)
        {
            return null;
        }

        var menu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Widget.ScreenBinding.Title"),
            Icon = new FontIcon { Glyph = "\uE7F4" }
        };

        WidgetScreenInfo? actual = ResolveActualDisplay(config, screens);
        WidgetScreenInfo? home = config.ScreenBindingMode == WidgetScreenBindingMode.Pinned
            ? WidgetScreenCatalog.TryFindScreen(screens, config.BoundScreenId)
            : null;

        // Idempotence guard (menu side): re-clicking the entry that mirrors
        // the persisted state must not re-run the binding pipeline. The
        // manager guards again; this keeps the menu itself from even asking.
        void ApplyIfChanged(WidgetScreenBindingMode mode, string? boundId)
        {
            if (config.ScreenBindingMode == mode &&
                string.Equals(
                    config.BoundScreenId?.Trim(),
                    boundId?.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            applyBinding(mode, boundId);
        }

        foreach (WidgetScreenInfo screen in screens)
        {
            // Follow-primary surfaces have no pinned home, so no radio is
            // checked — the toggle below carries the selection instead.
            bool isActual = config.ScreenBindingMode == WidgetScreenBindingMode.Pinned &&
                actual is not null &&
                string.Equals(actual.StableId, screen.StableId, StringComparison.OrdinalIgnoreCase);
            string text = localizationService.Format(
                "Widget.ScreenBinding.MonitorFormat",
                screen.Number,
                screen.DisplayName);
            if (screen.IsPrimary)
            {
                text += localizationService.T("Widget.ScreenBinding.PrimarySuffix");
            }

            string stableId = screen.StableId;
            menu.Items.Add(CreateRadioItem(
                text,
                isActual,
                () => ApplyIfChanged(WidgetScreenBindingMode.Pinned, stableId)));
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var followPrimary = new ToggleMenuFlyoutItem
        {
            Text = localizationService.T("Widget.ScreenBinding.FollowPrimary"),
            Icon = new FontIcon { Glyph = "\uE718" },
            IsChecked = config.ScreenBindingMode == WidgetScreenBindingMode.FollowPrimary
        };
        followPrimary.Click += (_, _) =>
        {
            if (followPrimary.IsChecked)
            {
                ApplyIfChanged(WidgetScreenBindingMode.FollowPrimary, null);
            }
            else
            {
                // Turning the toggle off keeps the widget where it is: home
                // becomes the current actual display (spec 5.10).
                ApplyIfChanged(
                    WidgetScreenBindingMode.Pinned,
                    actual?.StableId ?? home?.StableId);
            }
        };
        menu.Items.Add(followPrimary);

        // Fallback placement: name the disconnected home display compactly
        // ("DELL U2720Q（未连接）") — menu rows must stay short; the full
        // "returns when reconnected" sentence lives in the settings page row
        // description, not here.
        if (config.ScreenBindingMode == WidgetScreenBindingMode.Pinned && home is null)
        {
            string labelText = string.IsNullOrWhiteSpace(config.BoundScreenLabel)
                ? localizationService.T("Widget.ScreenBinding.HomeOfflineFallbackName")
                : config.BoundScreenLabel!;
            var offline = new MenuFlyoutItem
            {
                Text = localizationService.Format(
                    "Widget.ScreenBinding.HomeOfflineCompact",
                    labelText),
                IsEnabled = false
            };
            menu.Items.Add(offline);
        }

        return menu;
    }

    private static WidgetScreenInfo? ResolveActualDisplay(
        WidgetConfig config,
        IReadOnlyList<WidgetScreenInfo> screens)
    {
        Windows.Graphics.PointInt32 point = new(
            (int)Math.Round(double.IsFinite(config.X) ? config.X : 0),
            (int)Math.Round(double.IsFinite(config.Y) ? config.Y : 0));
        return WidgetScreenCatalog.FindScreenForPoint(screens, point);
    }

    private static RadioMenuFlyoutItem CreateRadioItem(
        string text,
        bool isChecked,
        Action apply)
    {
        var item = new RadioMenuFlyoutItem
        {
            Text = text,
            GroupName = "widget-screen-home",
            IsChecked = isChecked
        };
        item.Click += (_, _) => apply();
        return item;
    }
}
