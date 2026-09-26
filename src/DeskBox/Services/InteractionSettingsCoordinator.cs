using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Sole settings-page writer for the interaction section fields: autostart
/// reflection, update auto-check, open-method and file-item context menu,
/// resize snap (enabled plus spacing), show-desktop visibility, widget layer
/// mode, hover buttons (enabled plus the selected action set) and the
/// idle/hidden working-set trims. The section has no live-preview coupling
/// with the appearance machinery: every edit keeps the original semantics of
/// normalize (where the page normalized), store, and schedule one debounced
/// save, so interaction changes still reach the host only through the regular
/// SettingsChanged/refresh chain and the shell-side linkages that run after
/// each write. The startup registration operations themselves (mode switches,
/// task scheduler / Run-key fallback) stay in StartupService untouched; this
/// coordinator only persists the registration-state reflection.
/// </summary>
public sealed class InteractionSettingsCoordinator : IInteractionSettings
{
    private readonly SettingsService _settings;
    private bool _stopped;

    internal bool IsStopped => _stopped;

    public InteractionSettingsCoordinator(SettingsService settings)
    {
        _settings = settings;
    }

    public InteractionSettingsSnapshot ReadAll()
    {
        CoreSettingsSlice core = _settings.Settings.Core;
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        PerformanceSettingsSlice performance = _settings.Settings.Performance;
        return new(
            core.AutoStart,
            core.AutoCheckForUpdates,
            fileWidget.DoubleClickToOpen,
            fileWidget.FileItemSystemContextMenuEnabled,
            shell.ResizeSnapEnabled,
            shell.WidgetSnapSpacing,
            shell.KeepWidgetsVisibleOnShowDesktop,
            shell.WidgetLayerMode,
            shell.ShowHoverButtons,
            shell.WidgetHoverButtonActions,
            performance.IdleWorkingSetTrimEnabled,
            performance.ImmediateHiddenWorkingSetTrimEnabled);
    }

    public void SetAutoStart(bool value)
    {
        ThrowIfStopped();
        CoreSettingsSlice core = _settings.Settings.Core;
        if (core.AutoStart == value) return;
        core.AutoStart = value;
        _settings.SaveDebounced();
    }

    public void SetAutoCheckForUpdates(bool value)
    {
        ThrowIfStopped();
        CoreSettingsSlice core = _settings.Settings.Core;
        if (core.AutoCheckForUpdates == value) return;
        core.AutoCheckForUpdates = value;
        _settings.SaveDebounced();
    }

    public void SetDoubleClickToOpen(bool value)
    {
        ThrowIfStopped();
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (fileWidget.DoubleClickToOpen == value) return;
        fileWidget.DoubleClickToOpen = value;
        _settings.SaveDebounced();
    }

    public void SetFileItemSystemContextMenuEnabled(bool value)
    {
        ThrowIfStopped();
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (fileWidget.FileItemSystemContextMenuEnabled == value) return;
        fileWidget.FileItemSystemContextMenuEnabled = value;
        _settings.SaveDebounced();
    }

    public void SetResizeSnapEnabled(bool value)
    {
        ThrowIfStopped();
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.ResizeSnapEnabled == value) return;
        shell.ResizeSnapEnabled = value;
        _settings.SaveDebounced();
    }

    public void SetWidgetSnapSpacing(double value)
    {
        ThrowIfStopped();
        double normalized = SettingsService.NormalizeWidgetSnapSpacing(value);
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetSnapSpacing == normalized) return;
        shell.WidgetSnapSpacing = normalized;
        _settings.SaveDebounced();
    }

    public void SetKeepWidgetsVisibleOnShowDesktop(bool value)
    {
        ThrowIfStopped();
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.KeepWidgetsVisibleOnShowDesktop == value) return;
        shell.KeepWidgetsVisibleOnShowDesktop = value;
        _settings.SaveDebounced();
    }

    public void SetWidgetLayerMode(string? mode)
    {
        ThrowIfStopped();
        string normalized = SettingsService.NormalizeWidgetLayerModeSetting(mode);
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetLayerMode == normalized) return;
        shell.WidgetLayerMode = normalized;
        _settings.SaveDebounced();
    }

    public void SetShowHoverButtons(bool value)
    {
        ThrowIfStopped();
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.ShowHoverButtons == value) return;
        shell.ShowHoverButtons = value;
        _settings.SaveDebounced();
    }

    public void SetWidgetHoverButtonActions(string value)
    {
        ThrowIfStopped();
        // Store-only: the settings shell commits this field through its
        // appearance-save routine, which owns the drag deferral and the
        // notification-suppression flags; scheduling a save here would
        // duplicate the commit the shell already performs.
        _settings.Settings.WidgetShell.WidgetHoverButtonActions = value;
    }

    public void SetIdleWorkingSetTrimEnabled(bool value)
    {
        ThrowIfStopped();
        PerformanceSettingsSlice performance = _settings.Settings.Performance;
        if (performance.IdleWorkingSetTrimEnabled == value) return;
        performance.IdleWorkingSetTrimEnabled = value;
        _settings.SaveDebounced();
    }

    public void SetImmediateHiddenWorkingSetTrimEnabled(bool value)
    {
        ThrowIfStopped();
        PerformanceSettingsSlice performance = _settings.Settings.Performance;
        if (performance.ImmediateHiddenWorkingSetTrimEnabled == value) return;
        performance.ImmediateHiddenWorkingSetTrimEnabled = value;
        _settings.SaveDebounced();
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopped, this);

    internal void Stop() => _stopped = true;
}
