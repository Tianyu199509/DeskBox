#if DESKBOX_NATIVE_AOT
namespace DeskBox.Features.Performance;

// The performance section keeps its runtime {Binding} surface and binds
// through a section-level DataContext switch (batch 50); the General
// section's inline preset combo resolves against this editor as well
// through an element-level DataContext. Expose only the properties used by
// those XAML surfaces in NativeAOT builds, mirroring the
// GlanceWidgetViewModel bridge pattern.
[WinRT.GeneratedBindableCustomProperty([
    nameof(AvailableHiddenCacheCleanupDelayOptions),
    nameof(AvailableHiddenCacheCleanupScopeOptions),
    nameof(AvailablePerformanceCacheBudgetOptions),
    nameof(AvailablePerformanceModeOptions),
    nameof(AvailableVisibleIdleCacheCleanupDelayOptions),
    nameof(ContinuousDecorativeAnimationsSummaryText),
    nameof(IdleWorkingSetTrimEnabled),
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
