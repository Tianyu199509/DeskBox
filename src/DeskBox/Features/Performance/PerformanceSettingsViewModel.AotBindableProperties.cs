#if DESKBOX_NATIVE_AOT
namespace DeskBox.Features.Performance;

// The performance expander keeps its runtime {Binding} surface and binds
// through an element-level DataContext switch on GeneralSection.
[WinRT.GeneratedBindableCustomProperty([
    nameof(AllowCompactAmbient),
    nameof(AllowGlanceRotation),
    nameof(AllowTextMarquee),
    nameof(AllowVinylRotation),
    nameof(AvailableHiddenCacheCleanupDelayOptions),
    nameof(AvailableHiddenCacheCleanupScopeOptions),
    nameof(AvailablePerformanceCacheBudgetOptions),
    nameof(AvailablePerformanceModeOptions),
    nameof(AvailableVisibleIdleCacheCleanupDelayOptions),
    nameof(ContinuousDecorativeAnimationsSummaryText),
    nameof(IdleWorkingSetTrimEnabled),
    nameof(KeepCapsuleContentWarm),
    nameof(KeepWarmCacheWhenHidden),
    nameof(SelectedHiddenCacheCleanupDelaySeconds),
    nameof(SelectedHiddenCacheCleanupScope),
    nameof(SelectedPerformanceCacheBudget),
    nameof(SelectedPerformanceMode),
    nameof(SelectedVisibleIdleCacheCleanupDelaySeconds)
], [])]
public sealed partial class PerformanceSettingsViewModel
{
}
#endif
