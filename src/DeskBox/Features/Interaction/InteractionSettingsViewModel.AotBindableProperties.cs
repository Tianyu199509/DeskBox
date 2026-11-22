#if DESKBOX_NATIVE_AOT
namespace DeskBox.Features.Interaction;

// The interaction settings sections keep their runtime {Binding} surface and
// bind through a section-level DataContext switch. Expose only the properties
// used by that XAML surface in NativeAOT builds, mirroring the
// GlanceWidgetViewModel / MusicSettingsViewModel bridge pattern.
[WinRT.GeneratedBindableCustomProperty([
    nameof(AvailableClickRaiseScopeOptions),
    nameof(AvailableFileOpenMethodOptions),
    nameof(AvailableLayerModeOptions),
    nameof(AvailableShowDesktopBehaviorOptions),
    nameof(CanShowHotkeyWarning),
    nameof(ClickRaiseScope),
    nameof(FileOpenMethod),
    nameof(HoverButtonActionsSummary),
    nameof(HotkeyDescription),
    nameof(HotkeyEnabled),
    nameof(HotkeyStatusText),
    nameof(HotkeyText),
    nameof(HotkeyWarningText),
    nameof(IsDynamicLayerMode),
    nameof(LayerMode),
    nameof(ShowDesktopBehavior),
    nameof(SnapEnabled),
    nameof(SnapSpacing),
    nameof(SnapSpacingText)
], [])]
public sealed partial class InteractionSettingsViewModel
{
}
#endif
