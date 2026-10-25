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
        Action RefreshTrayIcon,
        Action RefreshThemeAppearance,
        Action ReplayLanguageChange,
        Action<bool> ApplySearchContentToggle);

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

        // Snap replayed unconditionally (batch R12, sequence audit defect
        // F): the set-snap fire-and-forget commands can be applied out of
        // order under the server's bounded-concurrent dispatch, and when
        // the commit's before/after happen to match (rapid double-toggle
        // netting to zero) the diff below would skip the replay — leaving
        // the live snap overlay stuck on the older value with no
        // convergence mechanism. The replay is two property writes.
        delegates.ApplySnapOptions(after.ResizeSnapEnabled, after.WidgetSnapSpacing);

        if (!string.Equals(before.TrayIconStyle, after.TrayIconStyle, StringComparison.Ordinal))
        {
            delegates.RefreshTrayIcon();
        }

        // ThemeService does not subscribe to SettingsChanged (it follows the
        // system color stream), and LanguageChanged only raises from
        // SetLanguage — external commits must drive both explicitly.
        if (!string.Equals(before.Theme, after.Theme, StringComparison.Ordinal) ||
            !string.Equals(before.AccentColorMode, after.AccentColorMode, StringComparison.Ordinal) ||
            !string.Equals(before.CustomAccentColor, after.CustomAccentColor, StringComparison.Ordinal))
        {
            delegates.RefreshThemeAppearance();
        }

        if (!string.Equals(before.Language, after.Language, StringComparison.Ordinal))
        {
            delegates.ReplayLanguageChange();
        }

        if (before.SearchIncludeDeskBoxContent != after.SearchIncludeDeskBoxContent)
        {
            delegates.ApplySearchContentToggle(after.SearchIncludeDeskBoxContent);
        }
    }
}
