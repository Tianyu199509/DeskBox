namespace DeskBox.Models;

/// <summary>
/// Application-level preferences: theme, language, accent, autostart, updates, the global hotkey, and one-time lifecycle markers.
/// </summary>
public sealed class CoreSettingsSlice
{
    /// <summary>
    /// Application theme. Valid values: <c>"System"</c>, <c>"Light"</c>, <c>"Dark"</c>.
    /// </summary>
    public string Theme { get; set; } = "System";

    /// <summary>
    /// Tray icon style. Valid values: <c>"System"</c>, <c>"Colorful"</c>, <c>"Black"</c>, <c>"White"</c>.
    /// </summary>
    public string TrayIconStyle { get; set; } = "Colorful";

/// <summary>
/// Display language. Valid values: <c>"System"</c>, <c>"zh-CN"</c>, <c>"zh-TW"</c>, <c>"en-US"</c>, <c>"ja-JP"</c>, <c>"de-DE"</c>, <c>"pt-BR"</c>, <c>"hi-IN"</c>, <c>"es-ES"</c>, <c>"fr-FR"</c>, <c>"ar-SA"</c>, <c>"bn-BD"</c>, <c>"ru-RU"</c>.
/// </summary>
    public string Language { get; set; } = "System";

    /// <summary>
    /// Accent color source. Valid values: <c>"System"</c>, <c>"Custom"</c>.
    /// </summary>
    public string AccentColorMode { get; set; } = "System";

    /// <summary>
    /// Custom accent color in hex format such as <c>#0078D4</c>.
    /// </summary>
    public string CustomAccentColor { get; set; } = "#0078D4";

    /// <summary>Whether DeskBox should launch automatically at Windows startup.</summary>
    public bool AutoStart { get; set; } = true;

    /// <summary>
    /// Whether the one-time autostart default has already been applied.
    /// <see cref="AutoStart"/> is only a mirror of the system state, so it
    /// cannot tell "never decided" from "the user turned startup off"; this
    /// marker makes the default apply exactly once and never again.
    /// </summary>
    public bool AutoStartDefaultApplied { get; set; }

    /// <summary>Null adopts the existing registration on upgrade; new installs use Standard.</summary>
    public StartupMode? AutoStartMode { get; set; }

    /// <summary>Whether DeskBox should check for updates in the background.</summary>
    public bool AutoCheckForUpdates { get; set; } = true;

    /// <summary>Last time DeskBox successfully attempted an update check.</summary>
    public DateTimeOffset? LastUpdateCheckAt { get; set; }

    /// <summary>Whether the global hotkey is enabled.</summary>
    public bool GlobalHotkeyEnabled { get; set; } = true;

    /// <summary>The activation shape used by the global DeskBox shortcut.</summary>
    public HotkeyActivationKind GlobalHotkeyActivationKind { get; set; } =
        HotkeyActivationKind.Chord;

    /// <summary>Global hotkey modifier bit flags.</summary>
    public int GlobalHotkeyModifiers { get; set; } = (int)HotkeyModifierKeys.None;

    /// <summary>Global hotkey virtual key code.</summary>
    public int GlobalHotkeyKey { get; set; } = (int)Windows.System.VirtualKey.F7;

    /// <summary>Whether double-clicking blank desktop space toggles all widgets.</summary>
    public bool DesktopDoubleClickEnabled { get; set; }

    /// <summary>Whether the first-run onboarding has been completed or skipped.</summary>
    public bool HasCompletedOnboarding { get; set; }

    /// <summary>The last onboarding step reached before completion.</summary>
    public int OnboardingStepIndex { get; set; }

    /// <summary>The onboarding experience version most recently completed or skipped.</summary>
    public int CompletedOnboardingVersion { get; set; }

    /// <summary>
    /// Whether the one-time default file-widget setup has already been resolved.
    /// This remains true after the user deletes or disables every file widget.
    /// </summary>
    public bool HasResolvedInitialFileWidgetSetup { get; set; }
}
