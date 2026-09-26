namespace DeskBox.Contracts;

public readonly record struct CapsuleBehaviorSettings(
    string CollapseBehavior,
    string CompactContentMode,
    bool HideSensitiveContent);

public readonly record struct CapsuleGeometrySettings(
    string CompactWidthMode,
    string CompactExpansionDirection);

public readonly record struct CapsuleArrangementSettings(
    string ArrangementMode,
    string BarPlacement,
    string BarDirection,
    double BarSpacing);

public readonly record struct CapsuleAnimationSettings(
    string CompactAnimationEffect,
    int CompactAnimationDurationMs);

public readonly record struct CapsuleTimingSettings(
    int ExpandDelayMs,
    int CollapseDelayMs);

/// <summary>
/// Settings-page writes for the capsule/compact-mode section: collapse
/// behavior, compact content mode, sensitive-content hiding, capsule geometry,
/// capsule bar arrangement, compact animation (effect plus duration presets),
/// hover expand/collapse delays and compact media corner style. The settings
/// shell keeps the XAML/AOT binding surface and the view-state dances (preset
/// selections, derived Custom flags); this port owns the raw persisted values.
/// Every write keeps the section's original save semantics: normalize, store,
/// and schedule the debounced save. The hover-response selection itself is a
/// derived view state and is not persisted through this port.
/// </summary>
public interface ICapsuleSettings
{
    CapsuleBehaviorSettings ReadBehavior();
    CapsuleGeometrySettings ReadGeometry();
    CapsuleArrangementSettings ReadArrangement();
    CapsuleAnimationSettings ReadAnimation();
    CapsuleTimingSettings ReadTiming();

    void SetWidgetCollapseBehavior(string? behavior);
    void SetWidgetCompactContentMode(string? mode);
    void SetWidgetCompactHideSensitiveContent(bool value);

    void SetWidgetCompactWidthMode(string? mode);
    void SetWidgetCompactExpansionDirection(string? direction);

    void SetWidgetCapsuleArrangementMode(string? mode);
    void SetWidgetCapsuleBarPlacement(string? placement);
    void SetWidgetCapsuleBarDirection(string? direction);
    void SetWidgetCapsuleBarSpacing(double spacing);

    // Preset effects pair-write the matching duration before a single save;
    // a custom duration write flips a non-custom/non-none effect to Custom,
    // exactly like the settings page did before the migration.
    void SetWidgetCompactAnimationEffect(string? effect);
    void SetWidgetCompactAnimationDurationMs(double value);

    void SetWidgetCompactExpandDelayMs(double value);
    void SetWidgetCompactCollapseDelayMs(double value);

    void SetWidgetCompactMediaCornerMode(string? mode);
}
