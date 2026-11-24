using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Central policy deciding whether the Clipboard capture daemon and
/// quick-paste shortcut should run. Either the desktop Clipboard widget
/// or standalone Quick-Paste shortcut mode keeps background monitoring alive.
/// </summary>
internal static class ClipboardRuntimePolicy
{
    public static bool IsMonitoringActive(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Clipboard) ||
               settings.Clipboard.ClipboardHotkeyEnabled;
    }

    public static bool IsQuickPasteHotkeyActive(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Clipboard.ClipboardHotkeyEnabled;
    }
}
