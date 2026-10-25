namespace DeskBox.Services;

using DeskBox.Contracts;
using DeskBox.Models;

/// <summary>
/// Settings-host adapter (batch R9): the search section's hotkey card
/// consumes <see cref="ISearchHotkeyController"/>, whose live registration
/// state lives only in the MAIN process. Every mutating member rides the
/// search-hotkey IPC commands; <see cref="CurrentGesture"/> reads the local
/// settings mirror exactly like the real service does (the gesture is
/// persisted in settings.json, both processes hold the same copy).
/// Deep review R13-3 (R14 hot-path pass): the synchronous round trips this
/// adapter used to make per keystroke are gone —
/// <see cref="IsGestureOwnedByMainHotkey"/> is computed from the settings
/// mirror with the service's own normalization (the service's
/// CurrentActivation derives from the same settings fields), and
/// <see cref="IsRegistered"/> reads a registration cache fed by the
/// registration-changed push plus explicit refreshes.
/// </summary>
public sealed class RemoteSearchHotkeyController : ISearchHotkeyController
{
    private readonly RemoteSettingsHotkeyActions _actions;
    private readonly Func<GlobalHotkeyGesture> _readPersistedGesture;
    private readonly Func<GlobalHotkeyActivation> _readMainActivation;
    private volatile bool _registered;

    public RemoteSearchHotkeyController(
        RemoteSettingsHotkeyActions actions,
        Func<GlobalHotkeyGesture> readPersistedGesture,
        Func<GlobalHotkeyActivation>? readMainActivation = null)
    {
        _actions = actions;
        _readPersistedGesture = readPersistedGesture;
        // Default = a non-chord activation, so the ownership predicate is
        // conservatively false without a mirror provider (a default record
        // struct would carry Kind=Chord(0) and could false-positive).
        _readMainActivation = readMainActivation ??
            (() => new GlobalHotkeyActivation(HotkeyActivationKind.WindowsTap, default));
        _actions.RegistrationChanged += RefreshRegistrationCache;
        RefreshRegistrationCache();
    }

    private void RefreshRegistrationCache()
    {
        // Fire-and-forget: the answer lands in the cache a round trip later;
        // until then the boot optimistic value (unregistered) stands and
        // every push corrects it.
        _ = Task.Run(async () =>
        {
            try
            {
                _registered = await _actions.IsSearchHotkeyRegisteredAsync();
            }
            catch (Exception ex)
            {
                App.Log($"[SearchHotkey] Registration cache refresh failed: {ex.Message}");
            }
        });
    }

    public bool IsRegistered => _registered;

    public GlobalHotkeyGesture CurrentGesture => _readPersistedGesture();

    public void SetEnabled(bool enabled)
    {
        _actions.SetSearchHotkeyEnabled(enabled);
        RefreshRegistrationCache();
    }

    public bool TryApplyGesture(GlobalHotkeyGesture gesture, out string? error)
    {
        bool applied = _actions.TryApplySearchHotkeyGesture(gesture, out error);
        if (applied)
        {
            RefreshRegistrationCache();
        }

        return applied;
    }

    public bool IsGestureOwnedByMainHotkey(GlobalHotkeyGesture gesture)
    {
        // Local mirror computation — identical inputs to
        // SearchHotkeyService.IsGestureOwnedByMainHotkey (the service reads
        // GlobalHotkeyService.CurrentActivation, which normalizes the same
        // three settings fields). No round trip, no modal-loop wedge.
        return _readMainActivation() is { Kind: HotkeyActivationKind.Chord } activation &&
            activation.Gesture.Equals(gesture);
    }
}
