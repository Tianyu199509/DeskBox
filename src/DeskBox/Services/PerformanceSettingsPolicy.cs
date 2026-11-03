using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

public readonly record struct EffectivePerformanceSettings(
    string Mode,
    int HiddenCacheCleanupDelaySeconds,
    int HiddenDeepCleanupDelaySeconds,
    bool AllowTextMarqueeAnimations,
    bool AllowVinylRotationAnimations,
    bool AllowGlanceImageAutoRotation,
    bool AllowCompactAmbientAnimations,
    int VisibleIdleCacheCleanupDelaySeconds,
    int TransientWindowReleaseDelaySeconds,
    string CacheBudget,
    string HiddenCacheCleanupScope)
{
    public bool AllowContinuousDecorativeAnimations =>
        AllowTextMarqueeAnimations ||
        AllowVinylRotationAnimations ||
        AllowGlanceImageAutoRotation ||
        AllowCompactAmbientAnimations;
}

/// <summary>
/// Resolves the user-facing performance preset into narrowly scoped runtime
/// behavior. Interaction, capsule expansion, and widget show/hide animations
/// are intentionally outside this policy.
/// </summary>
public static class PerformanceSettingsPolicy
{
    public const string ModeBestVisual = "BestVisual";
    public const string ModeBalanced = PerformanceOptionKinds.ModeBalanced;
    public const string ModeResourceSaver = PerformanceOptionKinds.ModeResourceSaver;
    public const string ModeCustom = PerformanceOptionKinds.ModeCustom;

    public const string DecorativeAnimationTextMarquee =
        PerformanceOptionKinds.DecorativeAnimationTextMarquee;
    public const string DecorativeAnimationVinylRotation =
        PerformanceOptionKinds.DecorativeAnimationVinylRotation;
    public const string DecorativeAnimationGlanceRotation =
        PerformanceOptionKinds.DecorativeAnimationGlanceRotation;
    public const string DecorativeAnimationCompactAmbient =
        PerformanceOptionKinds.DecorativeAnimationCompactAmbient;

    public static IReadOnlyList<string> SupportedDecorativeAnimationOptions =>
        PerformanceOptionKinds.SupportedDecorativeAnimationOptions;

    public const string CacheBudgetSmall = PerformanceOptionKinds.CacheBudgetSmall;
    public const string CacheBudgetBalanced = PerformanceOptionKinds.CacheBudgetBalanced;
    public const string CacheBudgetLarge = PerformanceOptionKinds.CacheBudgetLarge;

    public const string HiddenCacheCleanupScopeWarm =
        PerformanceOptionKinds.HiddenCacheCleanupScopeWarm;
    public const string HiddenCacheCleanupScopeAllRecreatable =
        PerformanceOptionKinds.HiddenCacheCleanupScopeAllRecreatable;

    public const int CleanupNever = -1;
    public const int CleanupAfter30Seconds = PerformanceOptionKinds.CleanupAfter30Seconds;
    public const int CleanupAfter1Minute = PerformanceOptionKinds.CleanupAfter1Minute;
    public const int CleanupAfter2Minutes = PerformanceOptionKinds.CleanupAfter2Minutes;
    public const int CleanupAfter5Minutes = PerformanceOptionKinds.CleanupAfter5Minutes;
    public const int CleanupAfter10Minutes = PerformanceOptionKinds.CleanupAfter10Minutes;
    public const int CleanupAfter15Minutes = PerformanceOptionKinds.CleanupAfter15Minutes;

    public const string DefaultMode = ModeResourceSaver;
    public const int DefaultHiddenCacheCleanupDelaySeconds = CleanupAfter30Seconds;
    public const int DefaultVisibleIdleCacheCleanupDelaySeconds = CleanupAfter5Minutes;
    public const int DefaultTransientWindowReleaseDelaySeconds = CleanupAfter2Minutes;
    public const string DefaultCacheBudget = CacheBudgetSmall;
    public const string DefaultHiddenCacheCleanupScope =
        HiddenCacheCleanupScopeAllRecreatable;
    public const bool DefaultIdleWorkingSetTrimEnabled = true;
    public const bool DefaultImmediateHiddenWorkingSetTrimEnabled = true;
    public const bool DefaultQuiescenceWorkingSetTrimEnabled = true;
    public const bool DefaultContinuousDecorativeAnimationsEnabled = true;
    public const bool DefaultTextMarqueeAnimationsEnabled = true;
    public const bool DefaultVinylRotationAnimationsEnabled = true;
    public const bool DefaultGlanceImageAutoRotationEnabled = true;
    public const bool DefaultCompactAmbientAnimationsEnabled = true;

    private static readonly EffectivePerformanceSettings s_resourceSaverPreset = new(
        ModeResourceSaver,
        CleanupAfter30Seconds,
        CleanupAfter1Minute,
        DefaultTextMarqueeAnimationsEnabled,
        DefaultVinylRotationAnimationsEnabled,
        DefaultGlanceImageAutoRotationEnabled,
        DefaultCompactAmbientAnimationsEnabled,
        CleanupAfter5Minutes,
        CleanupAfter2Minutes,
        CacheBudgetSmall,
        HiddenCacheCleanupScopeAllRecreatable);

    private static readonly EffectivePerformanceSettings s_balancedPreset = new(
        ModeBalanced,
        CleanupAfter30Seconds,
        CleanupAfter5Minutes,
        DefaultTextMarqueeAnimationsEnabled,
        DefaultVinylRotationAnimationsEnabled,
        DefaultGlanceImageAutoRotationEnabled,
        DefaultCompactAmbientAnimationsEnabled,
        CleanupAfter10Minutes,
        CleanupAfter10Minutes,
        CacheBudgetBalanced,
        HiddenCacheCleanupScopeAllRecreatable);

    public static string NormalizeMode(string? mode) =>
        PerformanceOptionKinds.NormalizeMode(mode);

    public static string NormalizeCacheBudget(string? cacheBudget) =>
        PerformanceOptionKinds.NormalizeCacheBudget(cacheBudget);

    public static string NormalizeHiddenCacheCleanupScope(string? scope) =>
        PerformanceOptionKinds.NormalizeHiddenCacheCleanupScope(scope);

    public static int ResolveInactiveGroupContentCacheCapacity(
        string? cacheBudget)
    {
        return NormalizeCacheBudget(cacheBudget) switch
        {
            CacheBudgetSmall => 1,
            CacheBudgetLarge => 2,
            _ => 1
        };
    }

    public static int NormalizeHiddenCacheCleanupDelaySeconds(int delaySeconds) =>
        delaySeconds == CleanupNever
            ? CleanupAfter5Minutes
            : delaySeconds is
            CleanupAfter30Seconds or
            CleanupAfter1Minute or
            CleanupAfter5Minutes
                ? delaySeconds
                : DefaultHiddenCacheCleanupDelaySeconds;

    public static int NormalizeVisibleIdleCacheCleanupDelaySeconds(
        int delaySeconds) =>
        delaySeconds == CleanupNever
            ? CleanupAfter15Minutes
            : delaySeconds is
            CleanupAfter30Seconds or
            CleanupAfter1Minute or
            CleanupAfter5Minutes or
            CleanupAfter10Minutes or
            CleanupAfter15Minutes
                ? delaySeconds
                : DefaultVisibleIdleCacheCleanupDelaySeconds;

    public static int NormalizeTransientWindowReleaseDelaySeconds(
        int delaySeconds) =>
        delaySeconds == CleanupNever
            ? CleanupAfter10Minutes
            : delaySeconds is
            CleanupAfter30Seconds or
            CleanupAfter1Minute or
            CleanupAfter2Minutes or
            CleanupAfter10Minutes
                ? delaySeconds
                : DefaultTransientWindowReleaseDelaySeconds;

    public static EffectivePerformanceSettings Resolve(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string mode = NormalizeMode(settings.PerformanceMode);
        return mode switch
        {
            ModeResourceSaver => s_resourceSaverPreset with
            {
                AllowTextMarqueeAnimations = settings.EnableTextMarqueeAnimations,
                AllowVinylRotationAnimations = settings.EnableVinylRotationAnimations,
                AllowGlanceImageAutoRotation = settings.EnableGlanceImageAutoRotation,
                AllowCompactAmbientAnimations = settings.EnableCompactAmbientAnimations
            },
            ModeCustom => ResolveCustom(settings),
            _ => s_balancedPreset with
            {
                AllowTextMarqueeAnimations = settings.EnableTextMarqueeAnimations,
                AllowVinylRotationAnimations = settings.EnableVinylRotationAnimations,
                AllowGlanceImageAutoRotation = settings.EnableGlanceImageAutoRotation,
                AllowCompactAmbientAnimations = settings.EnableCompactAmbientAnimations
            }
        };
    }

    public static bool Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        string mode = NormalizeMode(settings.PerformanceMode);
        int hiddenDelay = NormalizeHiddenCacheCleanupDelaySeconds(
            settings.HiddenCacheCleanupDelaySeconds);
        int visibleIdleDelay = NormalizeVisibleIdleCacheCleanupDelaySeconds(
            settings.VisibleIdleCacheCleanupDelaySeconds);
        int transientWindowDelay = NormalizeTransientWindowReleaseDelaySeconds(
            settings.TransientWindowReleaseDelaySeconds);
        string cacheBudget = NormalizeCacheBudget(settings.PerformanceCacheBudget);
        string hiddenCleanupScope = NormalizeHiddenCacheCleanupScope(
            settings.HiddenCacheCleanupScope);
        bool allowTextMarqueeAnimations =
            settings.EnableTextMarqueeAnimations;
        bool allowVinylRotationAnimations =
            settings.EnableVinylRotationAnimations;
        bool allowGlanceImageAutoRotation =
            settings.EnableGlanceImageAutoRotation;
        bool allowCompactAmbientAnimations =
            settings.EnableCompactAmbientAnimations;

        if (!string.Equals(mode, ModeCustom, StringComparison.Ordinal))
        {
            EffectivePerformanceSettings preset = ResolvePreset(mode);
            hiddenDelay = preset.HiddenCacheCleanupDelaySeconds;
            visibleIdleDelay = preset.VisibleIdleCacheCleanupDelaySeconds;
            transientWindowDelay = preset.TransientWindowReleaseDelaySeconds;
            cacheBudget = preset.CacheBudget;
            hiddenCleanupScope = preset.HiddenCacheCleanupScope;
        }

        bool changed = false;
        changed |= SetIfChanged(settings.PerformanceMode, mode, value =>
            settings.PerformanceMode = value);
        changed |= SetIfChanged(
            settings.HiddenCacheCleanupDelaySeconds,
            hiddenDelay,
            value => settings.HiddenCacheCleanupDelaySeconds = value);
        changed |= SetIfChanged(
            settings.VisibleIdleCacheCleanupDelaySeconds,
            visibleIdleDelay,
            value => settings.VisibleIdleCacheCleanupDelaySeconds = value);
        changed |= SetIfChanged(
            settings.TransientWindowReleaseDelaySeconds,
            transientWindowDelay,
            value => settings.TransientWindowReleaseDelaySeconds = value);
        changed |= SetIfChanged(
            settings.PerformanceCacheBudget,
            cacheBudget,
            value => settings.PerformanceCacheBudget = value);
        changed |= SetIfChanged(
            settings.HiddenCacheCleanupScope,
            hiddenCleanupScope,
            value => settings.HiddenCacheCleanupScope = value);
        changed |= SetIfChanged(
            settings.EnableTextMarqueeAnimations,
            allowTextMarqueeAnimations,
            value => settings.EnableTextMarqueeAnimations = value);
        changed |= SetIfChanged(
            settings.EnableVinylRotationAnimations,
            allowVinylRotationAnimations,
            value => settings.EnableVinylRotationAnimations = value);
        changed |= SetIfChanged(
            settings.EnableGlanceImageAutoRotation,
            allowGlanceImageAutoRotation,
            value => settings.EnableGlanceImageAutoRotation = value);
        changed |= SetIfChanged(
            settings.EnableCompactAmbientAnimations,
            allowCompactAmbientAnimations,
            value => settings.EnableCompactAmbientAnimations = value);
        bool legacyDecorativeAnimationsEnabled =
            allowTextMarqueeAnimations &&
            allowVinylRotationAnimations &&
            allowGlanceImageAutoRotation &&
            allowCompactAmbientAnimations;
        changed |= SetIfChanged(
            settings.EnableContinuousDecorativeAnimations,
            legacyDecorativeAnimationsEnabled,
            value => settings.EnableContinuousDecorativeAnimations = value);
        return changed;
    }

    public static void ApplyPreset(AppSettings settings, string? mode)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.PerformanceMode = NormalizeMode(mode);
        if (string.Equals(
                settings.PerformanceMode,
                ModeCustom,
                StringComparison.Ordinal))
        {
            settings.HiddenCacheCleanupDelaySeconds =
                NormalizeHiddenCacheCleanupDelaySeconds(
                    settings.HiddenCacheCleanupDelaySeconds);
            settings.VisibleIdleCacheCleanupDelaySeconds =
                NormalizeVisibleIdleCacheCleanupDelaySeconds(
                    settings.VisibleIdleCacheCleanupDelaySeconds);
            settings.TransientWindowReleaseDelaySeconds =
                NormalizeTransientWindowReleaseDelaySeconds(
                    settings.TransientWindowReleaseDelaySeconds);
            settings.PerformanceCacheBudget =
                NormalizeCacheBudget(settings.PerformanceCacheBudget);
            settings.HiddenCacheCleanupScope =
                NormalizeHiddenCacheCleanupScope(
                    settings.HiddenCacheCleanupScope);
            settings.EnableContinuousDecorativeAnimations =
                settings.EnableTextMarqueeAnimations &&
                settings.EnableVinylRotationAnimations &&
                settings.EnableGlanceImageAutoRotation &&
                settings.EnableCompactAmbientAnimations;
            return;
        }

        EffectivePerformanceSettings preset = ResolvePreset(
            settings.PerformanceMode);
        settings.HiddenCacheCleanupDelaySeconds =
            preset.HiddenCacheCleanupDelaySeconds;
        settings.VisibleIdleCacheCleanupDelaySeconds =
            preset.VisibleIdleCacheCleanupDelaySeconds;
        settings.TransientWindowReleaseDelaySeconds =
            preset.TransientWindowReleaseDelaySeconds;
        settings.PerformanceCacheBudget = preset.CacheBudget;
        settings.HiddenCacheCleanupScope = preset.HiddenCacheCleanupScope;
        settings.EnableContinuousDecorativeAnimations =
            settings.EnableTextMarqueeAnimations &&
            settings.EnableVinylRotationAnimations &&
            settings.EnableGlanceImageAutoRotation &&
            settings.EnableCompactAmbientAnimations;
    }

    private static EffectivePerformanceSettings ResolvePreset(string mode) =>
        string.Equals(NormalizeMode(mode), ModeResourceSaver, StringComparison.Ordinal)
            ? s_resourceSaverPreset
            : s_balancedPreset;

    private static EffectivePerformanceSettings ResolveCustom(
        AppSettings settings)
    {
        int hiddenDelay = NormalizeHiddenCacheCleanupDelaySeconds(
            settings.HiddenCacheCleanupDelaySeconds);
        int deepDelay = hiddenDelay switch
        {
            CleanupAfter5Minutes => CleanupAfter10Minutes,
            _ => CleanupAfter5Minutes
        };
        string cacheBudget = NormalizeCacheBudget(
            settings.PerformanceCacheBudget);
        return new(
            ModeCustom,
            hiddenDelay,
            deepDelay,
            settings.EnableTextMarqueeAnimations,
            settings.EnableVinylRotationAnimations,
            settings.EnableGlanceImageAutoRotation,
            settings.EnableCompactAmbientAnimations,
            NormalizeVisibleIdleCacheCleanupDelaySeconds(
                settings.VisibleIdleCacheCleanupDelaySeconds),
            NormalizeTransientWindowReleaseDelaySeconds(
                settings.TransientWindowReleaseDelaySeconds),
            cacheBudget,
            NormalizeHiddenCacheCleanupScope(
                settings.HiddenCacheCleanupScope));
    }

    private static bool SetIfChanged<T>(
        T current,
        T value,
        Action<T> setter)
    {
        if (EqualityComparer<T>.Default.Equals(current, value))
        {
            return false;
        }

        setter(value);
        return true;
    }
}
