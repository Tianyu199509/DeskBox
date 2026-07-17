using DeskBox.Models;

namespace DeskBox.Services;

public static class WidgetCompactPrivacyPolicy
{
    public static bool HidesSensitiveContent(bool enabled, WidgetKind widgetKind) =>
        enabled && widgetKind is
            WidgetKind.File or
            WidgetKind.Todo or
            WidgetKind.QuickCapture or
            WidgetKind.Music;

    public static string ResolveContentMode(
        string? contentMode,
        bool enabled,
        WidgetKind widgetKind)
    {
        string normalized = SettingsService.NormalizeWidgetCompactContentMode(contentMode);
        bool hidesSmartDetail =
            HidesSensitiveContent(enabled, widgetKind) &&
            normalized == SettingsService.WidgetCompactContentModeSmart &&
            widgetKind is WidgetKind.File or WidgetKind.Todo or WidgetKind.QuickCapture;
        return hidesSmartDetail
            ? SettingsService.WidgetCompactContentModeSummary
            : normalized;
    }
}
