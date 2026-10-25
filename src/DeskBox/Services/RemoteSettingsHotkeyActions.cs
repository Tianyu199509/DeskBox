namespace DeskBox.Services;

using DeskBox.Contracts;
using DeskBox.Models;

/// <summary>
/// Settings-host hotkey facade over the IPC pipe (batch 3c-1): forwards every
/// member to the main process's router. The two synchronous query members
/// block on the pipe round-trip — they run in rare window-construction and
/// refresh paths where a millisecond-scale wait is acceptable, and the reader
/// loop lives on a pool thread so no dispatcher deadlock is possible.
/// </summary>
public sealed class RemoteSettingsHotkeyActions : ISettingsHotkeyActions
{
    private readonly SettingsIpcClient _client;

    public RemoteSettingsHotkeyActions(SettingsIpcClient client)
    {
        _client = client;
        _client.EventReceived += OnEventReceived;
    }

    public event Action? RegistrationChanged;

    private void OnEventReceived(string method, string? payload)
    {
        if (method == SettingsIpcMethods.HotkeyRegistrationChanged)
        {
            RegistrationChanged?.Invoke();
        }
    }

    public GlobalHotkeyRuntimeState? GetRuntimeState()
    {
        (bool ok, string? payload, _) = _client
            .RequestAsync(SettingsIpcMethods.HotkeyState)
            .GetAwaiter().GetResult();
        return ok
            ? SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.GlobalHotkeyRuntimeState)
            : null;
    }

    public bool TryApplyActivation(GlobalHotkeyActivation activation, out string? applyError)
    {
        (bool ok, _, string? error) = _client.RequestAsync(
                SettingsIpcMethods.HotkeyApply,
                SettingsIpcPayload.Encode(activation, SettingsIpcJsonContext.Default.GlobalHotkeyActivation))
            .GetAwaiter().GetResult();
        applyError = error;
        return ok;
    }

    public bool ResetToDefault(out string? error)
    {
        (bool ok, _, string? resetError) = _client.RequestAsync(SettingsIpcMethods.HotkeyReset)
            .GetAwaiter().GetResult();
        error = resetError;
        return ok;
    }

    // Fire-and-forget notifications (batch R10): invoked from settings-page
    // handlers and the restore-defaults chain on the UI thread — a blocking
    // pipe roundtrip there parks the thread in the COM modal loop and the
    // window stops responding to close input entirely (see
    // RemoteSettingsAppActions.NotifyWithoutBlockingAsync for the dump
    // evidence). The bool-returning reads below keep their synchronous
    // bounded-wait contract.
    public void RefreshRegistration() =>
        _ = NotifyWithoutBlockingAsync(
            SettingsIpcMethods.HotkeyRefreshRegistration,
            payload: null,
            nameof(RefreshRegistration));

    public void SetEnabled(bool enabled) =>
        _ = NotifyWithoutBlockingAsync(
            SettingsIpcMethods.HotkeySetEnabled,
            SettingsIpcPayload.EncodeBoolean(enabled),
            nameof(SetEnabled));

    public bool IsGestureOwnedBySearchHotkey(GlobalHotkeyGesture gesture)
    {
        (bool ok, string? payload, _) = _client.RequestAsync(
                SettingsIpcMethods.HotkeyGestureOwned,
                SettingsIpcPayload.Encode(gesture, SettingsIpcJsonContext.Default.GlobalHotkeyGesture))
            .GetAwaiter().GetResult();
        return ok && SettingsIpcPayload.DecodeBoolean(payload);
    }

    public bool IsSearchHotkeyRegistered()
    {
        (bool ok, string? payload, _) = _client.RequestAsync(
                SettingsIpcMethods.SearchHotkeyState)
            .GetAwaiter().GetResult();
        return ok && SettingsIpcPayload.DecodeBoolean(payload);
    }

    public void SetSearchHotkeyEnabled(bool enabled) =>
        _ = NotifyWithoutBlockingAsync(
            SettingsIpcMethods.SearchHotkeySetEnabled,
            SettingsIpcPayload.EncodeBoolean(enabled),
            nameof(SetSearchHotkeyEnabled));

    private Task NotifyWithoutBlockingAsync(string method, string? payload, string caller) =>
        SettingsIpcShell.NotifyWithoutBlockingAsync(_client, method, payload, caller);

    public bool TryApplySearchHotkeyGesture(GlobalHotkeyGesture gesture, out string? error)
    {
        (bool ok, _, string? applyError) = _client.RequestAsync(
                SettingsIpcMethods.SearchHotkeyApplyGesture,
                SettingsIpcPayload.Encode(gesture, SettingsIpcJsonContext.Default.GlobalHotkeyGesture))
            .GetAwaiter().GetResult();
        error = applyError;
        return ok;
    }

    public bool IsGestureOwnedByMainHotkey(GlobalHotkeyGesture gesture)
    {
        (bool ok, string? payload, _) = _client.RequestAsync(
                SettingsIpcMethods.SearchHotkeyGestureOwnedByMain,
                SettingsIpcPayload.Encode(gesture, SettingsIpcJsonContext.Default.GlobalHotkeyGesture))
            .GetAwaiter().GetResult();
        return ok && SettingsIpcPayload.DecodeBoolean(payload);
    }

    // Same fire-and-forget contract as the notifications above (batch R11
    // audit): all three ride UI-thread call sites — the recorder lifecycle
    // pair even runs inside the settings-window close/hide path, where a
    // blocking roundtrip is the exact "X button stops closing the window"
    // signature (see RemoteSettingsAppActions for the dump evidence).
    public void RefreshSearchHotkeyRegistration() =>
        _ = NotifyWithoutBlockingAsync(
            SettingsIpcMethods.SearchHotkeyRefreshRegistration,
            payload: null,
            nameof(RefreshSearchHotkeyRegistration));

    public DesktopDoubleClickToggleResult TrySetDesktopDoubleClickEnabled(bool enabled)
    {
        (bool ok, string? payload, _) = _client.RequestAsync(
                SettingsIpcMethods.HotkeyDoubleClickSet,
                SettingsIpcPayload.EncodeBoolean(enabled))
            .GetAwaiter().GetResult();
        return ok && SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.DesktopDoubleClickToggleResult)
            is { } result
                ? result
                : new DesktopDoubleClickToggleResult(Applied: false, ErrorCode: 0);
    }

    // The recorder's OnOpened follows this with _hook.TryStart(); with the
    // send now asynchronous the two race for a few milliseconds. User input
    // arrives hundreds of milliseconds later, and the pre-R11 synchronous
    // version also proceeded to TryStart on a timed-out suspend, so this
    // widens no real window.
    public void SuspendHotkeysForRecording() =>
        _ = NotifyWithoutBlockingAsync(
            SettingsIpcMethods.HotkeySuspend,
            payload: null,
            nameof(SuspendHotkeysForRecording));

    public void ResumeHotkeysAfterRecording() =>
        _ = NotifyWithoutBlockingAsync(
            SettingsIpcMethods.HotkeyResume,
            payload: null,
            nameof(ResumeHotkeysAfterRecording));
}
