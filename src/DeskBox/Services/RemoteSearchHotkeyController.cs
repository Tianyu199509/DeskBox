namespace DeskBox.Services;

using DeskBox.Contracts;
using DeskBox.Models;

/// <summary>
/// Settings-host adapter (batch R9): the search section's hotkey card
/// consumes <see cref="ISearchHotkeyController"/>, whose live registration
/// state lives only in the MAIN process. Every mutating member rides the
/// search-hotkey IPC commands; <see cref="CurrentActivation"/> reads the local
/// settings mirror exactly like the real service does (the activation is
/// persisted in settings.json, both processes hold the same copy).
/// </summary>
public sealed class RemoteSearchHotkeyController : ISearchHotkeyController
{
    private readonly RemoteSettingsHotkeyActions _actions;
    private readonly Func<GlobalHotkeyGesture> _readPersistedGesture;
    private readonly Func<GlobalHotkeyActivation> _readPersistedActivation;
    private readonly Func<GlobalHotkeyActivation> _readMainActivation;
    private readonly Func<bool> _readMainEnabled;
    private readonly Action<bool>? _mirrorEnabledLocally;
    private readonly Action<GlobalHotkeyActivation>? _mirrorActivationLocally;
    private volatile bool _registered;

    public RemoteSearchHotkeyController(
        RemoteSettingsHotkeyActions actions,
        Func<GlobalHotkeyGesture> readPersistedGesture,
        Func<GlobalHotkeyActivation>? readMainActivation = null,
        Action<bool>? mirrorEnabledLocally = null,
        Func<GlobalHotkeyActivation>? readPersistedActivation = null,
        Func<bool>? readMainEnabled = null,
        Action<GlobalHotkeyActivation>? mirrorActivationLocally = null)
    {
        _actions = actions;
        _readPersistedGesture = readPersistedGesture;
        _readPersistedActivation = readPersistedActivation ??
            (() => GlobalHotkeyActivation.FromChord(_readPersistedGesture()));
        // Default = a non-conflicting invalid activation when no mirror provider is supplied.
        _readMainActivation = readMainActivation ?? (() => default);
        _readMainEnabled = readMainEnabled ?? (() => true);
        _mirrorEnabledLocally = mirrorEnabledLocally;
        _mirrorActivationLocally = mirrorActivationLocally;
        _actions.RegistrationChanged += RefreshRegistrationCache;
        RefreshRegistrationCache();
    }

    public event Action? StateChanged;

    private void RefreshRegistrationCache()
    {
        // Fire-and-forget: the answer lands in the cache a round trip later;
        // until then the boot optimistic value (unregistered) stands and
        // every push corrects it.
        _ = Task.Run(async () =>
        {
            try
            {
                bool registered = await _actions.IsSearchHotkeyRegisteredAsync();
                if (_registered != registered)
                {
                    _registered = registered;
                    StateChanged?.Invoke();
                }
            }
            catch (Exception ex)
            {
                App.Log($"[SearchHotkey] Registration cache refresh failed: {ex.Message}");
            }
        });
    }

    public bool IsRegistered => _registered;

    public GlobalHotkeyActivation CurrentActivation => _readPersistedActivation();

    public GlobalHotkeyGesture CurrentGesture => CurrentActivation.Gesture;

    public void SetEnabled(bool enabled)
    {
        _mirrorEnabledLocally?.Invoke(enabled);
        _actions.SetSearchHotkeyEnabled(enabled);
        RefreshRegistrationCache();
    }

    public bool TryApplyGesture(GlobalHotkeyGesture gesture, out string? error)
    {
        return TryApplyActivation(GlobalHotkeyActivation.FromChord(gesture), out error);
    }

    public bool TryApplyActivation(GlobalHotkeyActivation activation, out string? error)
    {
        bool applied = _actions.TryApplySearchHotkeyActivation(activation, out error);
        if (applied)
        {
            _mirrorActivationLocally?.Invoke(activation);
            RefreshRegistrationCache();
        }

        return applied;
    }

    public bool IsGestureOwnedByMainHotkey(GlobalHotkeyGesture gesture)
    {
        return IsActivationOwnedByMainHotkey(GlobalHotkeyActivation.FromChord(gesture));
    }

    public bool IsActivationOwnedByMainHotkey(GlobalHotkeyActivation activation)
    {
        if (!_readMainEnabled())
        {
            return false;
        }

        return GlobalHotkeyService.DoActivationsConflict(activation, _readMainActivation());
    }
}
