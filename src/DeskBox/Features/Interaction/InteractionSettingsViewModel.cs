using DeskBox.Contracts;

namespace DeskBox.Features.Interaction;

/// <summary>
/// Interaction section editor. The legacy settings shell keeps every
/// XAML/AOT binding, the startup registration operations, the update-check
/// trigger timing and the host-side linkages (overlay sync, layer refresh,
/// context-menu prewarm), and forwards its persisted writes here; this
/// editor is the feature seam over <see cref="IInteractionSettings"/> and
/// owns no duplicated state. All normalization and persistence rules live in
/// the coordinator.
/// </summary>
public sealed class InteractionSettingsViewModel
{
    private readonly IInteractionSettings _settings;

    public InteractionSettingsViewModel(IInteractionSettings settings)
    {
        _settings = settings;
    }

    public InteractionSettingsSnapshot ReadAll() => _settings.ReadAll();

    public void SetAutoStart(bool value) => _settings.SetAutoStart(value);
    public void SetAutoCheckForUpdates(bool value) => _settings.SetAutoCheckForUpdates(value);

    public void SetDoubleClickToOpen(bool value) => _settings.SetDoubleClickToOpen(value);
    public void SetFileItemSystemContextMenuEnabled(bool value) =>
        _settings.SetFileItemSystemContextMenuEnabled(value);

    public void SetResizeSnapEnabled(bool value) => _settings.SetResizeSnapEnabled(value);
    public void SetWidgetSnapSpacing(double value) => _settings.SetWidgetSnapSpacing(value);

    public void SetKeepWidgetsVisibleOnShowDesktop(bool value) =>
        _settings.SetKeepWidgetsVisibleOnShowDesktop(value);
    public void SetWidgetLayerMode(string? mode) => _settings.SetWidgetLayerMode(mode);

    public void SetShowHoverButtons(bool value) => _settings.SetShowHoverButtons(value);
    public void SetWidgetHoverButtonActions(string value) =>
        _settings.SetWidgetHoverButtonActions(value);

    public void SetIdleWorkingSetTrimEnabled(bool value) =>
        _settings.SetIdleWorkingSetTrimEnabled(value);
    public void SetImmediateHiddenWorkingSetTrimEnabled(bool value) =>
        _settings.SetImmediateHiddenWorkingSetTrimEnabled(value);
}
