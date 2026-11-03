#if DESKBOX_NATIVE_AOT
namespace DeskBox.Features.ManagedStorage;

// The managed-storage settings section keeps its runtime {Binding} surface
// and binds through a section-level DataContext switch. Expose only the
// properties used by that XAML surface in NativeAOT builds, mirroring the
// GlanceWidgetViewModel bridge pattern.
[WinRT.GeneratedBindableCustomProperty([
    nameof(AvailableDragInBehaviorOptionItems),
    nameof(AvailableDragOutActionOptions),
    nameof(AvailableDragOutBehaviorOptionItems),
    nameof(AvailableDropActionOptions),
    nameof(CanInvokeQuickAccessAction),
    nameof(DragBehaviorSummaryText),
    nameof(DragOutAction),
    nameof(DragOutModifierTipEnabled),
    nameof(DragOutResultHintEnabled),
    nameof(DropAction),
    nameof(PinQuickAccessButtonText),
    nameof(PinQuickAccessToolTipText),
    nameof(QuickAccessStatusText),
    nameof(RootPath)
], [])]
public sealed partial class ManagedStorageSettingsViewModel
{
}
#endif
