namespace DeskBox.Contracts;

/// <summary>
/// Canonical widget layer-mode values, owned here so the interaction editor
/// can build its option list without referencing the settings adapter.
/// <see cref="Services.SettingsService"/> keeps its historical constants as
/// aliases of these.
/// </summary>
public static class WidgetLayerModes
{
    public const string Dynamic = "Dynamic";
    public const string DesktopPinned = "DesktopPinned";
    public const string QuickReveal = "QuickReveal";
}

/// <summary>
/// Canonical click-raise scope values for Dynamic widget layering: raising only
/// the clicked widget (<see cref="Single"/>) or raising all visible widgets as
/// one group when any part of a widget is clicked (<see cref="Group"/>).
/// </summary>
public static class DynamicClickRaiseScopes
{
    public const string Single = "Single";
    public const string Group = "Group";
}

/// <summary>
/// Canonical file-open method combo values (the page-level projection of the
/// persisted <c>DoubleClickToOpen</c> flag), owned here so the interaction
/// editor can build its option list and map selections without referencing
/// the settings shell.
/// </summary>
public static class FileOpenMethods
{
    public const string SingleClick = "SingleClick";
    public const string DoubleClick = "DoubleClick";
}

/// <summary>
/// Canonical show-desktop behavior combo values (the page-level projection
/// of the persisted <c>KeepWidgetsVisibleOnShowDesktop</c> flag), owned here
/// so the interaction editor can build its option list and map selections
/// without referencing the settings shell.
/// </summary>
public static class ShowDesktopBehaviors
{
    public const string KeepVisible = "KeepVisible";
    public const string HideWithWindows = "HideWithWindows";
}

public readonly record struct InteractionSettingsSnapshot(
    bool AutoStart,
    bool AutoCheckForUpdates,
    bool DoubleClickToOpen,
    bool FileItemSystemContextMenuEnabled,
    bool ResizeSnapEnabled,
    double WidgetSnapSpacing,
    bool KeepWidgetsVisibleOnShowDesktop,
    string WidgetLayerMode,
    string DynamicClickRaiseScope,
    bool ShowHoverButtons,
    string WidgetHoverButtonActions,
    bool IdleWorkingSetTrimEnabled,
    bool ImmediateHiddenWorkingSetTrimEnabled);

/// <summary>
/// Settings-page writes for the interaction section: autostart reflection,
/// update auto-check, silent startup, open-method and file-item context menu, resize snap
/// (enabled plus spacing), show-desktop visibility, widget layer mode, hover
/// buttons (enabled plus the selected action set), and the idle/hidden
/// working-set trims. The settings shell keeps the XAML/AOT binding surface,
/// the startup registration operations (StartupService mode switches, task
/// scheduler / Run-key migration), the update-check trigger timing and every
/// host-side linkage (overlay sync, layer refresh, context-menu prewarm);
/// this port owns only the raw persisted values with their original save
/// semantics: normalize where the page normalized, store, and schedule one
/// debounced save. Hover-action selections keep committing through the
/// shell's appearance-save routine, so their write stores without scheduling
/// its own save.
/// </summary>
/// <summary>
/// Immutable read snapshot of the interaction presentation fields the
/// section editor binds its XAML surface to (layer mode, resize snap enable
/// plus spacing, the two combo projections, and the file-item context-menu
/// toggle the file-widget overview re-binds through its typed editor
/// dependency property). Values arrive already normalized, mirroring the
/// music editor's read-port shape; external refresh paths (settings
/// broadcasts, default restores) re-read it.
/// </summary>
public sealed record InteractionPresentationSettings(
    string LayerMode,
    string ClickRaiseScope,
    bool SnapEnabled,
    double SnapSpacing,
    bool DoubleClickToOpen,
    bool KeepWidgetsVisibleOnShowDesktop,
    bool FileItemContextMenuEnabled);

/// <summary>
/// Immutable presentation snapshot of the global-hotkey card, computed by
/// the settings shell (which owns the hotkey state machine and its
/// host-service queries) and pushed onto the interaction editor's binding
/// surface. The editor never queries the hotkey service itself.
/// </summary>
public sealed record GlobalHotkeyPresentationSettings(
    bool Enabled,
    string Text,
    string StatusText,
    string Description,
    string WarningText,
    bool CanShowWarning);

public interface IInteractionSettings
{
    InteractionSettingsSnapshot ReadAll();

    /// <summary>Reads the normalized interaction presentation snapshot.</summary>
    InteractionPresentationSettings ReadInteractionPresentation();

    // Mirrors the registration-state reflection the settings page performed:
    // unchanged values skip the redundant save.
    void SetAutoStart(bool value);
    void SetAutoCheckForUpdates(bool value);
    void SetShowReleaseNotesAfterUpdate(bool value);
    void SetAutoDownloadUpdates(bool value);
    void SetShowUpdateToastNotification(bool value);
    void SetSilentStartup(bool value);

    void SetDoubleClickToOpen(bool value);
    void SetFileItemSystemContextMenuEnabled(bool value);

    void SetResizeSnapEnabled(bool value);
    void SetWidgetSnapSpacing(double value);

    void SetKeepWidgetsVisibleOnShowDesktop(bool value);
    void SetWidgetLayerMode(string? mode);
    void SetDynamicClickRaiseScope(string? scope);

    void SetShowHoverButtons(bool value);
    // Stores only: the shell schedules the save through its appearance-save
    // routine (drag deferral and notification suppression live there). The
    // value is the shell-built action-set string and is stored verbatim.
    void SetWidgetHoverButtonActions(string value);

    void SetIdleWorkingSetTrimEnabled(bool value);
    void SetImmediateHiddenWorkingSetTrimEnabled(bool value);
}
