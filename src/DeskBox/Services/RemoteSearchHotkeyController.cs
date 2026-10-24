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
/// </summary>
public sealed class RemoteSearchHotkeyController : ISearchHotkeyController
{
    private readonly RemoteSettingsHotkeyActions _actions;
    private readonly Func<GlobalHotkeyGesture> _readPersistedGesture;

    public RemoteSearchHotkeyController(
        RemoteSettingsHotkeyActions actions,
        Func<GlobalHotkeyGesture> readPersistedGesture)
    {
        _actions = actions;
        _readPersistedGesture = readPersistedGesture;
    }

    public bool IsRegistered => _actions.IsSearchHotkeyRegistered();

    public GlobalHotkeyGesture CurrentGesture => _readPersistedGesture();

    public void SetEnabled(bool enabled) => _actions.SetSearchHotkeyEnabled(enabled);

    public bool TryApplyGesture(GlobalHotkeyGesture gesture, out string? error) =>
        _actions.TryApplySearchHotkeyGesture(gesture, out error);

    public bool IsGestureOwnedByMainHotkey(GlobalHotkeyGesture gesture) =>
        _actions.IsGestureOwnedByMainHotkey(gesture);
}
