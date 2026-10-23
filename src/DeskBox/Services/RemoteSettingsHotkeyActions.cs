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

    public void RefreshRegistration() =>
        _client.RequestAsync(SettingsIpcMethods.HotkeyRefreshRegistration)
            .GetAwaiter().GetResult();

    public void SetEnabled(bool enabled) =>
        _client.RequestAsync(
                SettingsIpcMethods.HotkeySetEnabled,
                SettingsIpcPayload.EncodeBoolean(enabled))
            .GetAwaiter().GetResult();

    public bool IsGestureOwnedBySearchHotkey(GlobalHotkeyGesture gesture)
    {
        (bool ok, string? payload, _) = _client.RequestAsync(
                SettingsIpcMethods.HotkeyGestureOwned,
                SettingsIpcPayload.Encode(gesture, SettingsIpcJsonContext.Default.GlobalHotkeyGesture))
            .GetAwaiter().GetResult();
        return ok && SettingsIpcPayload.DecodeBoolean(payload);
    }

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
