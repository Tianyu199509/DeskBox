#if DESKBOX_NATIVE_AOT
namespace DeskBox.Features.GroupNavigation;

// The WidgetGroups settings section keeps its runtime {Binding} surface and
// binds through a section-level DataContext switch. Expose only the
// properties used by that XAML surface in NativeAOT builds, mirroring the
// GlanceWidgetViewModel / AppearanceSettingsViewModel bridge pattern.
[WinRT.GeneratedBindableCustomProperty([
    nameof(AvailableNavigationStyleOptions),
    nameof(AvailableSwitchAnimationOptions),
    nameof(AvailableTitleDisplayModeOptions),
    nameof(DefaultNavigationStyle),
    nameof(DefaultSwitchAnimationStyle),
    nameof(DefaultTitleDisplayMode),
    nameof(ExistingGroups),
    nameof(HasExistingGroups),
    nameof(HoverSwitchEnabled),
    nameof(ShowExistingGroupsEmpty),
    nameof(WheelSwitchEnabled)
], [])]
public sealed partial class GroupNavigationSettingsViewModel
{
}
#endif
