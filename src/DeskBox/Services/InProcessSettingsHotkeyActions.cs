namespace DeskBox.Services;

using DeskBox.Contracts;
using DeskBox.Models;

/// <summary>
/// In-process default of <see cref="ISettingsHotkeyActions"/>: a thin wrapper
/// over the App-owned hotkey services, used while the settings UI still runs
/// inside the main process. The services may legitimately be null in headless
/// hosts; every member degrades exactly like the previous inline null checks.
/// </summary>
public sealed class InProcessSettingsHotkeyActions : ISettingsHotkeyActions
{
    private readonly GlobalHotkeyService? _hotkeyService;
    private readonly SearchHotkeyService? _searchHotkeyService;
    private readonly Func<SearchHotkeyService?>? _searchHotkeyProvider;
    private readonly DesktopDoubleClickActivationService? _doubleClickService;
    private readonly SettingsService _settingsService;

    public InProcessSettingsHotkeyActions(
        GlobalHotkeyService? hotkeyService,
        SearchHotkeyService? searchHotkeyService,
        DesktopDoubleClickActivationService? doubleClickService,
        SettingsService settingsService,
        Func<SearchHotkeyService?>? searchHotkeyProvider = null)
    {
        _hotkeyService = hotkeyService;
        _searchHotkeyService = searchHotkeyService;
        _searchHotkeyProvider = searchHotkeyProvider;
        _doubleClickService = doubleClickService;
        _settingsService = settingsService;
        // Subscribed eagerly for the facade's lifetime: the settings window
        // is the only consumer and outlives every registration change, so
        // per-subscriber bookkeeping would only add removal races.
        if (_hotkeyService is not null)
        {
            _hotkeyService.RegistrationChanged += OnRegistrationChanged;
        }
    }

    public event Action? RegistrationChanged;

    private void OnRegistrationChanged() => RegistrationChanged?.Invoke();

    /// <summary>
    /// Live resolution (batch R10): the router is constructed at MAIN boot,
    /// when the search feature is usually still disabled and the service
    /// does not exist yet (it is created lazily at first enable). The
    /// constructor snapshot was null forever in that state, and every
    /// search-hotkey command silently no-opped — the settings page showed
    /// "hotkey failed / combination in use" for a key nobody held.
    /// </summary>
    private SearchHotkeyService? SearchHotkey =>
        _searchHotkeyProvider?.Invoke() ?? _searchHotkeyService;

    public Task<bool> IsSearchHotkeyRegisteredAsync() =>
        Task.FromResult(SearchHotkey?.IsRegistered == true);

    public void SetSearchHotkeyEnabled(bool enabled) =>
        SearchHotkey?.SetEnabled(enabled);

    public bool TryApplySearchHotkeyGesture(GlobalHotkeyGesture gesture, out string? error)
    {
        if (SearchHotkey is not { } search)
        {
            error = "search hotkey service unavailable";
            return false;
        }

        return search.TryApplyGesture(gesture, out error);
    }

    public void RefreshSearchHotkeyRegistration() =>
        SearchHotkey?.RefreshRegistration();

    public GlobalHotkeyRuntimeState? GetRuntimeState()
    {
        if (_hotkeyService is not { } hotkey)
        {
            return null;
        }

        return new GlobalHotkeyRuntimeState(
            hotkey.IsRegistered,
            hotkey.LastError,
            hotkey.CurrentGestureText,
            hotkey.CurrentActivation);
    }

    public bool TryApplyActivation(GlobalHotkeyActivation activation, out string? applyError)
    {
        if (_hotkeyService is not { } hotkey)
        {
            applyError = null;
            return false;
        }

        return hotkey.TryApplyActivation(activation, out applyError);
    }

    public bool ResetToDefault(out string? error)
    {
        if (_hotkeyService is not { } hotkey)
        {
            error = null;
            return false;
        }

        return hotkey.ResetToDefault(out error);
    }

    public void RefreshRegistration() => _hotkeyService?.RefreshRegistration();

    public void SetEnabled(bool enabled) => _hotkeyService?.SetEnabled(enabled);

    // IsGestureOwnedBySearchHotkey/ByMainHotkey left the interface with
    // their round trips (deep review R13-3): both are settings-mirror
    // computations the settings host now performs locally.

    public DesktopDoubleClickToggleResult TrySetDesktopDoubleClickEnabled(bool enabled)
    {
        if (_doubleClickService is not { } service)
        {
            // Same fallback the settings window used inline: without the
            // live service the preference is only persisted.
            _settingsService.Settings.DesktopDoubleClickEnabled = enabled;
            _settingsService.SaveDebounced();
            return new DesktopDoubleClickToggleResult(Applied: true, ErrorCode: 0);
        }

        bool applied = service.TrySetEnabled(enabled, out int errorCode);
        return new DesktopDoubleClickToggleResult(applied, errorCode);
    }

    public void SuspendHotkeysForRecording()
    {
        _hotkeyService?.SuspendForRecording();
        _searchHotkeyService?.SuspendForRecording();
    }

    public void ResumeHotkeysAfterRecording()
    {
        _searchHotkeyService?.ResumeAfterRecording();
        _hotkeyService?.ResumeAfterRecording();
    }
}
