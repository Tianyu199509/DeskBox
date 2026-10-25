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

    private async Task NotifyWithoutBlockingAsync(string method, string? payload, string caller)
    {
        try
        {
            (bool ok, _, string? error) = await _client.RequestAsync(method, payload);
            if (!ok)
            {
                App.Log($"[SettingsIpc] {caller} failed: {error ?? "unknown error"}");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[SettingsIpc] {caller} failed: {ex.Message}");
        }
    }

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

    public void RefreshSearchHotkeyRegistration() =>
        _client.RequestAsync(SettingsIpcMethods.SearchHotkeyRefreshRegistration)
            .GetAwaiter().GetResult();

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

    public void SuspendHotkeysForRecording() =>
        _client.RequestAsync(SettingsIpcMethods.HotkeySuspend)
            .GetAwaiter().GetResult();

    public void ResumeHotkeysAfterRecording() =>
        _client.RequestAsync(SettingsIpcMethods.HotkeyResume)
            .GetAwaiter().GetResult();
}
