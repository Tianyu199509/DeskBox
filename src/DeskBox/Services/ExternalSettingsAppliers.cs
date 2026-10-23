namespace DeskBox.Services;

using DeskBox.Models;

/// <summary>
/// Diff-driven appliers for value-shaped settings that have no coordinator
/// subscription on the main side (batch 3a of the settings-process split):
/// when an external snapshot (the settings host's commit) changes one of
/// these fields, the live services whose state derives from it must re-read
/// it. Pure and delegate-driven so the diff logic is unit-testable without
/// a live host.
/// </summary>
internal static class ExternalSettingsAppliers
{
    /// <summary>Host actions invoked for the field groups that changed.</summary>
    internal readonly record struct Delegates(
        Action RefreshHotkeyRegistration,
        Action RefreshDesktopDoubleClickRegistration,
        Action<bool, double> ApplySnapOptions,
        Action RefreshTrayIcon);

    internal static void ApplyValueAppliers(
        AppSettings before,
        AppSettings after,
        Delegates delegates)
    {
        if (before.GlobalHotkeyActivationKind != after.GlobalHotkeyActivationKind ||
            before.GlobalHotkeyModifiers != after.GlobalHotkeyModifiers ||
            before.GlobalHotkeyKey != after.GlobalHotkeyKey ||
            before.GlobalHotkeyEnabled != after.GlobalHotkeyEnabled)
        {
            delegates.RefreshHotkeyRegistration();
        }

        if (before.DesktopDoubleClickEnabled != after.DesktopDoubleClickEnabled)
        {
            delegates.RefreshDesktopDoubleClickRegistration();
        }

        if (before.ResizeSnapEnabled != after.ResizeSnapEnabled ||
            before.WidgetSnapSpacing != after.WidgetSnapSpacing)
        {
            delegates.ApplySnapOptions(after.ResizeSnapEnabled, after.WidgetSnapSpacing);
        }

        if (!string.Equals(before.TrayIconStyle, after.TrayIconStyle, StringComparison.Ordinal))
        {
            delegates.RefreshTrayIcon();
        }
    }
}
