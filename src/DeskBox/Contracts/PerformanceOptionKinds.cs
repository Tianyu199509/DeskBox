namespace DeskBox.Contracts;

/// <summary>
/// Canonical performance-section option values, owned here so the
/// performance editor can build its binding surface without referencing the
/// settings adapter. <see cref="Services.PerformanceSettingsPolicy"/> keeps
/// its historical constants as aliases of these; the policy stays the single
/// normalization source (preset resolution and load-time normalization).
/// </summary>
public static class PerformanceOptionKinds
{
    public const string ModeBalanced = "Balanced";
    public const string ModeResourceSaver = "ResourceSaver";
    public const string ModeCustom = "Custom";

    public const string CacheBudgetSmall = "Small";
    public const string CacheBudgetBalanced = "Balanced";
    public const string CacheBudgetLarge = "Large";

    public const string HiddenCacheCleanupScopeWarm = "Warm";
    public const string HiddenCacheCleanupScopeAllRecreatable = "AllRecreatable";

    public const int CleanupAfter30Seconds = 30;
    public const int CleanupAfter1Minute = 60;
    public const int CleanupAfter2Minutes = 2 * 60;
    public const int CleanupAfter5Minutes = 5 * 60;
    public const int CleanupAfter10Minutes = 10 * 60;
    public const int CleanupAfter15Minutes = 15 * 60;

    public const string DecorativeAnimationTextMarquee = "TextMarquee";
    public const string DecorativeAnimationVinylRotation = "VinylRotation";
    public const string DecorativeAnimationGlanceRotation = "GlanceRotation";
    public const string DecorativeAnimationCompactAmbient = "CompactAmbient";

    public static IReadOnlyList<string> SupportedDecorativeAnimationOptions { get; } =
        Array.AsReadOnly(new[]
        {
            DecorativeAnimationTextMarquee,
            DecorativeAnimationVinylRotation,
            DecorativeAnimationGlanceRotation,
            DecorativeAnimationCompactAmbient
        });

    public static string NormalizeMode(string? mode) =>
        string.Equals(mode, ModeResourceSaver, StringComparison.OrdinalIgnoreCase)
            ? ModeResourceSaver
            : string.Equals(mode, ModeCustom, StringComparison.OrdinalIgnoreCase)
                ? ModeCustom
                : ModeBalanced;

    public static string NormalizeCacheBudget(string? cacheBudget) =>
        string.Equals(cacheBudget, CacheBudgetSmall, StringComparison.OrdinalIgnoreCase)
            ? CacheBudgetSmall
            : string.Equals(cacheBudget, CacheBudgetLarge, StringComparison.OrdinalIgnoreCase)
                ? CacheBudgetLarge
                : CacheBudgetBalanced;

    public static string NormalizeHiddenCacheCleanupScope(string? scope) =>
        string.Equals(scope, HiddenCacheCleanupScopeAllRecreatable, StringComparison.OrdinalIgnoreCase)
            ? HiddenCacheCleanupScopeAllRecreatable
            : HiddenCacheCleanupScopeWarm;
}

/// <summary>
/// Read snapshot for the performance section's binding surface: the preset
/// mode with its policy-resolved detail selections, the four decorative
/// animation switches and the three working-set trim switches. Built by the
/// performance coordinator from <see cref="Services.PerformanceSettingsPolicy"/>
/// resolution plus the performance slice; the editor projects it without
/// writing back.
/// </summary>
public sealed record PerformancePresentationSettings(
    string Mode,
    int HiddenCacheCleanupDelaySeconds,
    int VisibleIdleCacheCleanupDelaySeconds,
    string CacheBudget,
    string HiddenCacheCleanupScope,
    bool AllowTextMarqueeAnimations,
    bool AllowVinylRotationAnimations,
    bool AllowGlanceImageAutoRotation,
    bool AllowCompactAmbientAnimations,
    bool IdleWorkingSetTrimEnabled,
    bool ImmediateHiddenWorkingSetTrimEnabled,
    bool QuiescenceWorkingSetTrimEnabled,
    bool KeepCapsuleContentWarm = true);
