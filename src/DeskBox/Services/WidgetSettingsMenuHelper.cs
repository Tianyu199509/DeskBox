// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

/// <summary>
/// Helper for building a per-widget "open settings" menu item and routing
/// to the matching Settings section. Keeps the WidgetKind → section-tag
/// and WidgetKind → localization-key maps in one place so every widget
/// window can share the same logic.
/// </summary>
internal static class WidgetSettingsMenuHelper
{
    /// <summary>
    /// Gets the Settings section tag that best matches the given widget kind.
    /// Returns null when the kind has no dedicated settings page.
    /// </summary>
    public static string? GetSettingsSectionTag(WidgetKind kind) => kind switch
    {
        WidgetKind.File => "FileDisplaySettings",
        WidgetKind.QuickCapture => "QuickCaptureSettings",
        WidgetKind.Todo => "TodoSettings",
        WidgetKind.Music => "MusicSettings",
        WidgetKind.Weather => "WeatherSettings",
        WidgetKind.Glance => "GlanceSettings",
        WidgetKind.Search => "SearchSettings",
        WidgetKind.Pomodoro => "PomodoroSettings",
        _ => null
    };

    /// <summary>
    /// Gets the localization key for the menu item text, per widget kind.
    /// Falls back to a generic "configure" key when the kind is unknown.
    /// </summary>
    public static string GetLocalizationKey(WidgetKind kind) => kind switch
    {
        WidgetKind.File => "Widget.Settings.FileWidget",
        WidgetKind.QuickCapture => "Widget.Settings.QuickCapture",
        WidgetKind.Todo => "Widget.Settings.Todo",
        WidgetKind.Music => "Widget.Settings.Music",
        WidgetKind.Weather => "Widget.Settings.Weather",
        WidgetKind.Glance => "Widget.Settings.Glance",
        WidgetKind.Search => "Widget.Settings.Search",
        WidgetKind.Pomodoro => "Widget.Settings.Pomodoro",
        _ => "Common.Configure"
    };

    /// <summary>
    /// Creates a <see cref="MenuFlyoutItem"/> that opens the matching
    /// Settings section for the given <paramref name="kind"/>.
    /// <paramref name="beforeClick"/> is invoked before navigation so the
    /// caller can hide the current flyout or release interaction layers.
    /// </summary>
    public static MenuFlyoutItem CreateMenuItem(
        WidgetKind kind,
        LocalizationService localization,
        Action? beforeClick = null,
        string? widgetId = null)
    {
        var item = new MenuFlyoutItem
        {
            Text = localization.T(GetLocalizationKey(kind)),
            Icon = new FontIcon { Glyph = "\uE713" }
        };
        string? sectionTag = GetSettingsSectionTag(kind);
        item.Click += (_, _) =>
        {
            beforeClick?.Invoke();
            if (sectionTag is not null)
            {
                if (kind == WidgetKind.Glance && !string.IsNullOrWhiteSpace(widgetId))
                {
                    App.Current.ShowGlanceSettings(widgetId);
                }
                else
                {
                    App.Current.ShowSettings(sectionTag);
                }
            }
        };
        return item;
    }
}
