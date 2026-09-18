namespace DeskBox.Models;

/// <summary>
/// Performance preset and resource-management preferences (cache budgets, cleanup delays, decorative animation gates).
/// </summary>
public sealed class PerformanceSettingsSlice
{
    /// <summary>Performance state. Selectable presets are <c>Balanced</c> and <c>ResourceSaver</c>; <c>Custom</c> records manual detail changes.</summary>
    public string PerformanceMode { get; set; } = "ResourceSaver";

    /// <summary>Finite delay before releasing idle caches after every widget is fully hidden.</summary>
    public int HiddenCacheCleanupDelaySeconds { get; set; } = 30;

    /// <summary>Hidden cleanup scope. Valid values: <c>Warm</c>, <c>AllRecreatable</c>.</summary>
    public string HiddenCacheCleanupScope { get; set; } = "AllRecreatable";

    /// <summary>Finite delay before shrinking recreatable caches while visible widgets are inactive.</summary>
    public int VisibleIdleCacheCleanupDelaySeconds { get; set; } = 5 * 60;

    /// <summary>
    /// Trim the process working set after the fully-hidden idle deep cleanup
    /// completes. Mirrors the pre-1.4.5 behaviour users remember as low idle
    /// memory: pages are paged out and fault back in on demand, so it never
    /// runs while widgets are visible or the user is interacting.
    /// </summary>
    public bool IdleWorkingSetTrimEnabled { get; set; } = true;

    /// <summary>Experimental working-set trim once all widget hide animations have completed.</summary>
    public bool ImmediateHiddenWorkingSetTrimEnabled { get; set; } = true;

    /// <summary>Finite delay before closing a hidden transient window such as Search.</summary>
    public int TransientWindowReleaseDelaySeconds { get; set; } = 2 * 60;

    /// <summary>Budget for recreatable icon, thumbnail, and decoded-image caches. Valid values: <c>Small</c>, <c>Balanced</c>, <c>Large</c>.</summary>
    public string PerformanceCacheBudget { get; set; } = "Small";

    /// <summary>Legacy compatibility mirror for the former all-or-nothing decorative-effects switch.</summary>
    public bool EnableContinuousDecorativeAnimations { get; set; } = true;

    /// <summary>Whether continuously scrolling title text may run.</summary>
    public bool EnableTextMarqueeAnimations { get; set; } = true;

    /// <summary>Whether music vinyl artwork may rotate while playback is active.</summary>
    public bool EnableVinylRotationAnimations { get; set; } = true;

    /// <summary>Whether Glance widgets may automatically advance their image rotation.</summary>
    public bool EnableGlanceImageAutoRotation { get; set; } = true;

    /// <summary>Whether capsule glow, particle, breathing, and indeterminate ambient effects may run.</summary>
    public bool EnableCompactAmbientAnimations { get; set; } = true;
}
