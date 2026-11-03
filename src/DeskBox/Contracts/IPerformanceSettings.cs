namespace DeskBox.Contracts;

/// <summary>
/// Performance-section settings writes. The performance preset mode, the
/// custom-mode detail fields (hidden-cache cleanup delay, visible-idle
/// cleanup delay, cache budget, hidden-cleanup scope), the four decorative
/// animation switches and the quiescence working-set trim switch all write
/// through this contract; the idle and immediate-hidden working-set trim
/// switches stay on <see cref="IInteractionSettings"/> (their write owner
/// since the interaction-section batch). Normalization and preset resolution
/// remain owned by <see cref="Services.PerformanceSettingsPolicy"/>; every
/// write applies the same custom-mode switch the legacy shell performed
/// (a detail edit flips the preset mode to Custom) and schedules one
/// debounced save with the regular SettingsChanged pass.
/// </summary>
public interface IPerformanceSettings
{
    /// <summary>Reads the section's presentation snapshot (policy-resolved).</summary>
    PerformancePresentationSettings ReadPresentation();

    /// <summary>
    /// Applies a preset mode: the policy writes the preset's detail fields
    /// onto the settings and the caller re-projects its surface. Selecting
    /// the already-selected mode is a no-op.
    /// </summary>
    void SetPerformanceMode(string? mode);

    void SetHiddenCacheCleanupDelaySeconds(int delaySeconds);

    void SetVisibleIdleCacheCleanupDelaySeconds(int delaySeconds);

    void SetPerformanceCacheBudget(string? cacheBudget);

    void SetHiddenCacheCleanupScope(string? scope);

    /// <summary>
    /// Writes one decorative animation switch and re-derives the legacy
    /// combined flag from all four switches; the edit switches the preset
    /// mode to Custom, exactly as the legacy shell's custom-setting path did.
    /// Unknown options are ignored.
    /// </summary>
    void SetDecorativeAnimationEnabled(string option, bool enabled);

    void SetQuiescenceWorkingSetTrimEnabled(bool enabled);

    void SetKeepCapsuleContentWarm(bool enabled);
}
