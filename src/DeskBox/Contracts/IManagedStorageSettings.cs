namespace DeskBox.Contracts;

/// <summary>
/// Canonical managed-drop action values (what happens when files are dropped
/// onto a file widget that stores content in the managed root), owned here so
/// the managed-storage editor can build its option list without referencing
/// the settings adapter. <see cref="Services.SettingsService"/> keeps its
/// historical constants as aliases of these.
/// </summary>
public static class ManagedDropActions
{
    public const string Copy = "Copy";
    public const string Move = "Move";
    public const string FollowWindows = "FollowWindows";

    public static bool IsValid(string? action) =>
        string.Equals(action, Move, StringComparison.Ordinal) ||
        string.Equals(action, Copy, StringComparison.Ordinal) ||
        string.Equals(action, FollowWindows, StringComparison.Ordinal);
}

/// <summary>
/// Immutable read snapshot of the managed-storage section's persisted
/// presentation: the drop action (already normalized to a canonical value)
/// and the default managed storage root path (already normalized). The
/// managed-storage settings editor binds its XAML surface to a projection of
/// this snapshot; external refresh paths (settings broadcasts, default
/// restores, post-migration commits) re-read it.
/// </summary>
public sealed record ManagedStoragePresentationSettings(
    string DropAction,
    string DragOutAction,
    bool DragOutModifierTipEnabled,
    bool DragOutResultHintEnabled,
    string RootPath);

/// <summary>
/// Shell-computed presentation of the quick-access card (the status line, the
/// pin/unpin button face and its tooltip, the action gate and the unpin
/// intent). The pin-state state machine stays on the settings shell because
/// it queries the Explorer quick-access APIs; the shell pushes the computed
/// presentation onto the managed-storage editor's binding surface.
/// </summary>
public sealed record QuickAccessPresentationSettings(
    bool CanInvoke,
    bool ShouldUnpin,
    string StatusText,
    string ButtonText,
    string ToolTipText);

/// <summary>
/// Settings-page writes for the managed-storage section: the default managed
/// storage root path — the folder that widgets following the default root
/// store their content in — and the managed drop action. The settings shell
/// keeps the folder picker, the migration confirmation/residue dialogs and
/// the quick-access state machine that run around the writes; the file
/// migration itself (moving widget content between roots, rollback, residue
/// cleanup) stays on the host's existing WidgetManager chain and is not part
/// of this port. Every write keeps the original command semantics: normalize
/// the raw value through the shared SettingsService normalizers (blank or
/// unusable paths fall back to the default root; unknown actions fall back
/// to Move), skip unchanged writes, store, and schedule one debounced save
/// with the regular SettingsChanged broadcast. The settings window compares
/// the normalized path against the displayed value before invoking the flow,
/// so no-op writes stay filtered by the caller exactly as before.
/// </summary>
public interface IManagedStorageSettings
{
    /// <summary>
    /// Reads the managed-storage presentation snapshot (normalized drop
    /// action and normalized root path).
    /// </summary>
    ManagedStoragePresentationSettings ReadManagedStoragePresentation();

    string ReadDefaultRootPath();

    string SetDefaultRootPath(string path);

    bool SetManagedDropAction(string? action);

    /// <summary>
    /// Writes the drag-out action (the preferred drop effect advertised to
    /// external targets when dragging files out of a file widget).
    /// </summary>
    bool SetManagedDragOutAction(string? action);

    /// <summary>
    /// Writes whether the in-drag modifier tip is surfaced at drag start.
    /// </summary>
    bool SetDragOutModifierTipEnabled(bool enabled);

    /// <summary>
    /// Writes whether the post-drop receipt hint is shown.
    /// </summary>
    bool SetDragOutResultHintEnabled(bool enabled);
}
