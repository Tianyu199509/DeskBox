using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Sole settings-page writer for the performance section's fields (batch 50;
/// the section's eleven local-lambda facade writes plus the preset-mode write
/// and the quiescence trim switch moved here from the settings shell). All
/// writes go through the <see cref="PerformanceSettingsSlice"/> paths, so no
/// flat facade passthrough is touched. Normalization and preset resolution
/// stay owned by <see cref="PerformanceSettingsPolicy"/>; a detail edit
/// switches the preset mode to Custom (the legacy shell's
/// UpdateCustomPerformanceSetting semantics) and every write schedules one
/// debounced save with the regular SettingsChanged pass.
/// </summary>
public sealed class PerformanceSettingsCoordinator : IPerformanceSettings
{
    private readonly SettingsService _settings;
    private bool _stopped;

    internal bool IsStopped => _stopped;

    public PerformanceSettingsCoordinator(SettingsService settings)
    {
        _settings = settings;
    }

    public PerformancePresentationSettings ReadPresentation()
    {
        EffectivePerformanceSettings effective =
            PerformanceSettingsPolicy.Resolve(_settings.Settings);
        PerformanceSettingsSlice performance = _settings.Settings.Performance;
        return new(
            effective.Mode,
            effective.HiddenCacheCleanupDelaySeconds,
            effective.VisibleIdleCacheCleanupDelaySeconds,
            effective.CacheBudget,
            effective.HiddenCacheCleanupScope,
            effective.AllowTextMarqueeAnimations,
            effective.AllowVinylRotationAnimations,
            effective.AllowGlanceImageAutoRotation,
            effective.AllowCompactAmbientAnimations,
            performance.IdleWorkingSetTrimEnabled,
            performance.ImmediateHiddenWorkingSetTrimEnabled,
            performance.QuiescenceWorkingSetTrimEnabled,
            performance.KeepCapsuleContentWarm);
    }

    public void SetPerformanceMode(string? mode)
    {
        ThrowIfStopped();
        string normalized = PerformanceSettingsPolicy.NormalizeMode(mode);
        PerformanceSettingsSlice performance = _settings.Settings.Performance;
        if (string.Equals(
                performance.PerformanceMode,
                normalized,
                StringComparison.Ordinal))
        {
            return;
        }

        // The preset write keeps its original shape: the policy writes the
        // mode and the preset's detail fields onto the settings in place;
        // the editor re-projects its surface from the read snapshot after
        // the call (the legacy SynchronizePerformanceDetailSelection).
        PerformanceSettingsPolicy.ApplyPreset(_settings.Settings, normalized);
        _settings.SaveDebounced();
    }

    public void SetHiddenCacheCleanupDelaySeconds(int delaySeconds)
    {
        ThrowIfStopped();
        int normalized =
            PerformanceSettingsPolicy.NormalizeHiddenCacheCleanupDelaySeconds(
                delaySeconds);
        PerformanceSettingsSlice performance = _settings.Settings.Performance;
        if (performance.HiddenCacheCleanupDelaySeconds == normalized)
        {
            return;
        }

        performance.HiddenCacheCleanupDelaySeconds = normalized;
        SwitchPerformanceModeToCustom(performance);
        _settings.SaveDebounced();
    }

    public void SetVisibleIdleCacheCleanupDelaySeconds(int delaySeconds)
    {
        ThrowIfStopped();
        int normalized = PerformanceSettingsPolicy
            .NormalizeVisibleIdleCacheCleanupDelaySeconds(delaySeconds);
        PerformanceSettingsSlice performance = _settings.Settings.Performance;
        if (performance.VisibleIdleCacheCleanupDelaySeconds == normalized)
        {
            return;
        }

        performance.VisibleIdleCacheCleanupDelaySeconds = normalized;
        SwitchPerformanceModeToCustom(performance);
        _settings.SaveDebounced();
    }

    public void SetPerformanceCacheBudget(string? cacheBudget)
    {
        ThrowIfStopped();
        string normalized =
            PerformanceSettingsPolicy.NormalizeCacheBudget(cacheBudget);
        PerformanceSettingsSlice performance = _settings.Settings.Performance;
        if (string.Equals(
                performance.PerformanceCacheBudget,
                normalized,
                StringComparison.Ordinal))
        {
            return;
        }

        performance.PerformanceCacheBudget = normalized;
        SwitchPerformanceModeToCustom(performance);
        _settings.SaveDebounced();
    }

    public void SetHiddenCacheCleanupScope(string? scope)
    {
        ThrowIfStopped();
        string normalized =
            PerformanceSettingsPolicy.NormalizeHiddenCacheCleanupScope(scope);
        PerformanceSettingsSlice performance = _settings.Settings.Performance;
        if (string.Equals(
                performance.HiddenCacheCleanupScope,
                normalized,
                StringComparison.Ordinal))
        {
            return;
        }

        performance.HiddenCacheCleanupScope = normalized;
        SwitchPerformanceModeToCustom(performance);
        _settings.SaveDebounced();
    }

    public void SetDecorativeAnimationEnabled(string option, bool enabled)
    {
        ThrowIfStopped();
        PerformanceSettingsSlice performance = _settings.Settings.Performance;
        bool changed;
        switch (option)
        {
            case PerformanceOptionKinds.DecorativeAnimationTextMarquee:
                changed = performance.EnableTextMarqueeAnimations != enabled;
                if (changed)
                {
                    performance.EnableTextMarqueeAnimations = enabled;
                }
                break;
            case PerformanceOptionKinds.DecorativeAnimationVinylRotation:
                changed = performance.EnableVinylRotationAnimations != enabled;
                if (changed)
                {
                    performance.EnableVinylRotationAnimations = enabled;
                }
                break;
            case PerformanceOptionKinds.DecorativeAnimationGlanceRotation:
                changed = performance.EnableGlanceImageAutoRotation != enabled;
                if (changed)
                {
                    performance.EnableGlanceImageAutoRotation = enabled;
                }
                break;
            case PerformanceOptionKinds.DecorativeAnimationCompactAmbient:
                changed = performance.EnableCompactAmbientAnimations != enabled;
                if (changed)
                {
                    performance.EnableCompactAmbientAnimations = enabled;
                }
                break;
            default:
                return;
        }

        if (!changed)
        {
            return;
        }

        // The legacy combined flag is derived from all four switches on
        // every decorative write (the old shell's toggle lambda).
        performance.EnableContinuousDecorativeAnimations =
            performance.EnableTextMarqueeAnimations &&
            performance.EnableVinylRotationAnimations &&
            performance.EnableGlanceImageAutoRotation &&
            performance.EnableCompactAmbientAnimations;
        _settings.SaveDebounced();
    }

    public void SetQuiescenceWorkingSetTrimEnabled(bool enabled)
    {
        ThrowIfStopped();
        PerformanceSettingsSlice performance = _settings.Settings.Performance;
        if (performance.QuiescenceWorkingSetTrimEnabled == enabled)
        {
            return;
        }

        performance.QuiescenceWorkingSetTrimEnabled = enabled;
        _settings.SaveDebounced();
    }

    public void SetKeepCapsuleContentWarm(bool enabled)
    {
        ThrowIfStopped();
        PerformanceSettingsSlice performance = _settings.Settings.Performance;
        if (performance.KeepCapsuleContentWarm == enabled)
        {
            return;
        }

        performance.KeepCapsuleContentWarm = enabled;
        _settings.SaveDebounced();
    }

    private void SwitchPerformanceModeToCustom(PerformanceSettingsSlice performance)
    {
        if (string.Equals(
                performance.PerformanceMode,
                PerformanceOptionKinds.ModeCustom,
                StringComparison.Ordinal))
        {
            return;
        }

        performance.PerformanceMode = PerformanceOptionKinds.ModeCustom;
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopped, this);

    internal void Stop() => _stopped = true;
}
