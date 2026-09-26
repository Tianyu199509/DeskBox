namespace DeskBox.Contracts;

public readonly record struct InteractionSettingsSnapshot(
    bool AutoStart,
    bool AutoCheckForUpdates,
    bool DoubleClickToOpen,
    bool FileItemSystemContextMenuEnabled,
    bool ResizeSnapEnabled,
    double WidgetSnapSpacing,
    bool KeepWidgetsVisibleOnShowDesktop,
    string WidgetLayerMode,
    bool ShowHoverButtons,
    string WidgetHoverButtonActions,
    bool IdleWorkingSetTrimEnabled,
    bool ImmediateHiddenWorkingSetTrimEnabled);

/// <summary>
/// Settings-page writes for the interaction section: autostart reflection,
/// update auto-check, open-method and file-item context menu, resize snap
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
public interface IInteractionSettings
{
    InteractionSettingsSnapshot ReadAll();

    // Mirrors the registration-state reflection the settings page performed:
    // unchanged values skip the redundant save.
    void SetAutoStart(bool value);
    void SetAutoCheckForUpdates(bool value);

    void SetDoubleClickToOpen(bool value);
    void SetFileItemSystemContextMenuEnabled(bool value);

    void SetResizeSnapEnabled(bool value);
    void SetWidgetSnapSpacing(double value);

    void SetKeepWidgetsVisibleOnShowDesktop(bool value);
    void SetWidgetLayerMode(string? mode);

    void SetShowHoverButtons(bool value);
    // Stores only: the shell schedules the save through its appearance-save
    // routine (drag deferral and notification suppression live there). The
    // value is the shell-built action-set string and is stored verbatim.
    void SetWidgetHoverButtonActions(string value);

    void SetIdleWorkingSetTrimEnabled(bool value);
    void SetImmediateHiddenWorkingSetTrimEnabled(bool value);
}
