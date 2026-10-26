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

    // Deep review R18 (A-#3): GetRuntimeState used to take a blocking pipe
    // roundtrip on EVERY settings push (the host's ApplySettingsSnapshot →
    // RefreshGlobalHotkeyState chain) — a warm path mislabeled as cold.
    // Cache fed by the registration push + mutation-triggered refreshes,
    // same shape as the R13-3 registration cache; the sync signature stays
    // (callers are property refresh chains), only the transport disappears.
    private volatile GlobalHotkeyRuntimeState? _cachedRuntimeState;

    public RemoteSettingsHotkeyActions(SettingsIpcClient client)
    {
        _client = client;
        _client.EventReceived += OnEventReceived;
        RefreshRuntimeStateCache();
    }

    public event Action? RegistrationChanged;

    private void OnEventReceived(string method, string? payload)
    {
        if (method == SettingsIpcMethods.HotkeyRegistrationChanged)
        {
            RefreshRuntimeStateCache();
            RegistrationChanged?.Invoke();
        }
    }

    private void RefreshRuntimeStateCache()
    {
        // Fire-and-forget: the answer lands a roundtrip later; until then
        // callers see the previous state (or null before the first refresh
        // completes — matching the pre-split no-service behavior).
        _ = Task.Run(async () =>
        {
            try
            {
                (bool ok, string? payload, _) = await _client.RequestAsync(
                    SettingsIpcMethods.HotkeyState);
                if (ok &&
                    SettingsIpcPayload.Decode(payload, SettingsIpcJsonContext.Default.GlobalHotkeyRuntimeState)
                        is { } state)
                {
                    _cachedRuntimeState = state;
                }
            }
            catch (Exception ex)
            {
                App.Log($"[SearchHotkey] Runtime state cache refresh failed: {ex.Message}");
            }
        });
    }

    public GlobalHotkeyRuntimeState? GetRuntimeState() => _cachedRuntimeState;

    public bool TryApplyActivation(GlobalHotkeyActivation activation, out string? applyError)
    {
        (bool ok, _, string? error) = _client.RequestAsync(
                SettingsIpcMethods.HotkeyApply,
                SettingsIpcPayload.Encode(activation, SettingsIpcJsonContext.Default.GlobalHotkeyActivation))
            .GetAwaiter().GetResult();
        applyError = error;
        if (ok)
        {
            RefreshRuntimeStateCache();
        }

        return ok;
    }

    public bool ResetToDefault(out string? error)
    {
        (bool ok, _, string? resetError) = _client.RequestAsync(SettingsIpcMethods.HotkeyReset)
            .GetAwaiter().GetResult();
        error = resetError;
        if (ok)
        {
            RefreshRuntimeStateCache();
        }

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

    // IsGestureOwnedBySearchHotkey / IsGestureOwnedByMainHotkey removed
    // with the interface members (deep review R13-3): both predicates are
    // settings-mirror computations the settings host performs locally —
    // the per-keystroke synchronous round trips were the R10/R11
    // modal-loop wedge family.

    public async Task<bool> IsSearchHotkeyRegisteredAsync()
    {
        // Deep review R13-3: async — the host's controller caches the
        // answer off the registration push instead of blocking a caller.
        (bool ok, string? payload, _) = await _client.RequestAsync(
                SettingsIpcMethods.SearchHotkeyState);
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
