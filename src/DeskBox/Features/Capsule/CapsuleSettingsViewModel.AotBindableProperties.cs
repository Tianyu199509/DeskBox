#if DESKBOX_NATIVE_AOT
namespace DeskBox.Features.Capsule;

// The capsule family sections (main capsule section plus the behavior,
// arrangement, animation and overrides subsections) keep their runtime
// {Binding} surface and bind through a section-level DataContext switch.
// Expose only the properties used by that XAML surface in NativeAOT builds,
// mirroring the GlanceWidgetViewModel / AppearanceSettingsViewModel bridge
// pattern.
[WinRT.GeneratedBindableCustomProperty([
    nameof(AnimationDurationMs),
    nameof(AnimationDurationText),
    nameof(AnimationEffect),
    nameof(ArrangementDetailsSummary),
    nameof(ArrangementMode),
    nameof(AvailableAnimationEffectOptions),
    nameof(AvailableArrangementOptions),
    nameof(AvailableBarDirectionOptions),
    nameof(AvailableBarPlacementOptions),
    nameof(AvailableCollapseBehaviorOptions),
    nameof(AvailableContentModeOptions),
    nameof(AvailableExpansionDirectionOptions),
    nameof(AvailableHoverResponseOptions),
    nameof(AvailableWidthModeOptions),
    nameof(BarDirection),
    nameof(BarPlacement),
    nameof(BarSpacing),
    nameof(BarSpacingText),
    nameof(CanOpenAnimationDetails),
    nameof(CanOpenHoverResponseDetails),
    nameof(CollapseBehavior),
    nameof(CollapseDelayMs),
    nameof(CollapseDelayText),
    nameof(CollapseSummaryText),
    nameof(ContentMode),
    nameof(ExpandDelayMs),
    nameof(ExpandDelayText),
    nameof(ExpansionDirection),
    nameof(HasWidthOverrides),
    nameof(HideSensitiveContent),
    nameof(HoverResponse),
    nameof(IsBarEnabled),
    nameof(IsBarSpacingEnabled),
    nameof(IsCapsuleActive),
    nameof(OverrideItems),
    nameof(OverrideSummaryText),
    nameof(ShowAnimationCustom),
    nameof(ShowArrangementEntry),
    nameof(ShowHoverResponseCustom),
    nameof(ShowHoverResponseEntry),
    nameof(ShowOverridesEmpty),
    nameof(ShowOverridesEntry),
    nameof(ShowOverridesList),
    nameof(WidthMode)
], [])]
public sealed partial class CapsuleSettingsViewModel
{
}
#endif
