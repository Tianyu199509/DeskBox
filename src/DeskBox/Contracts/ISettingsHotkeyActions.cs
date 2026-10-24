namespace DeskBox.Contracts;

using DeskBox.Models;

/// <summary>
/// Snapshot of the global activation hotkey's registration state so a
/// settings host without the live service (the settings UI running in its
/// own process) renders the same card. Null means the host has no hotkey
/// service at all.
/// </summary>
public sealed record GlobalHotkeyRuntimeState(
    bool IsRegistered,
    string? LastError,
    string CurrentGestureText,
    GlobalHotkeyActivation CurrentActivation);

/// <summary>
/// Outcome of the desktop double-click activation toggle. Applied=false
/// means the host refused the switch; ErrorCode carries the hook failure.
/// </summary>
public readonly record struct DesktopDoubleClickToggleResult(bool Applied, int ErrorCode);

/// <summary>
/// Settings-side surface for the global hotkey and the desktop double-click
/// activation service, which live in the main process once the settings UI
/// runs in its own process. Pure helpers (gesture normalization, formatting,
/// preset gestures) stay static on GlobalHotkeyService and are not mirrored.
/// </summary>
public interface ISettingsHotkeyActions
{
    /// <summary>Forwarded from the live service's RegistrationChanged.</summary>
    event Action? RegistrationChanged;

    /// <summary>Current registration snapshot, or null when no service exists.</summary>
    GlobalHotkeyRuntimeState? GetRuntimeState();

    bool TryApplyActivation(GlobalHotkeyActivation activation, out string? applyError);

    bool ResetToDefault(out string? error);

    /// <summary>Re-runs registration against the persisted settings.</summary>
    void RefreshRegistration();

    /// <summary>Enables or disables the global hotkey host-side.</summary>
    void SetEnabled(bool enabled);

    /// <summary>
    /// Whether the search hotkey currently owns the gesture (registration
    /// state lives with the running services, not in settings.json alone).
    /// </summary>
    bool IsGestureOwnedBySearchHotkey(GlobalHotkeyGesture gesture);

    // ── Search hotkey (batch R9) ────────────────────────────────────
    // SearchSettingsCoordinator consumes ISearchHotkeyController, whose
    // live registration state lives only in the MAIN process. Without these
    // members the settings host's search section rendered the hotkey card
    // permanently unavailable — the toggle/capture/reset controls could
    // never reach the service at all.

    /// <summary>Live registration state of the search hotkey service.</summary>
    bool IsSearchHotkeyRegistered();

    /// <summary>Enables or disables the search hotkey registration.</summary>
    void SetSearchHotkeyEnabled(bool enabled);

    /// <summary>Applies a captured gesture through the live service.</summary>
    bool TryApplySearchHotkeyGesture(GlobalHotkeyGesture gesture, out string? error);

    /// <summary>Whether the main activation hotkey currently owns the gesture.</summary>
    bool IsGestureOwnedByMainHotkey(GlobalHotkeyGesture gesture);

    /// <summary>Re-runs the search hotkey registration against persisted settings.</summary>
    void RefreshSearchHotkeyRegistration();

    /// <summary>
    /// Applies the desktop double-click toggle through the live service,
    /// falling back to persisting the preference when no service exists.
    /// </summary>
    DesktopDoubleClickToggleResult TrySetDesktopDoubleClickEnabled(bool enabled);

    /// <summary>
    /// Suspends both live hotkey registrations while the recorder dialog
    /// captures keys, so neither hotkey fires nor its reserved hook eats the
    /// recorded chord.
    /// </summary>
    void SuspendHotkeysForRecording();

    /// <summary>Counterpart of <see cref="SuspendHotkeysForRecording"/>.</summary>
    void ResumeHotkeysAfterRecording();
}
