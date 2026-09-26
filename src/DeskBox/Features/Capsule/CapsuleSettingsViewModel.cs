using DeskBox.Contracts;

namespace DeskBox.Features.Capsule;

/// <summary>
/// Capsule/compact-mode section editor. The legacy settings shell keeps every
/// XAML/AOT binding (preset selections and the derived Custom flags live on
/// the shell so the view-state dances are untouched) and forwards its writes
/// here; this editor is the feature seam over <see cref="ICapsuleSettings"/>
/// and owns no duplicated state. All normalization and persistence rules live
/// in the coordinator.
/// </summary>
public sealed class CapsuleSettingsViewModel
{
    private readonly ICapsuleSettings _settings;

    public CapsuleSettingsViewModel(ICapsuleSettings settings)
    {
        _settings = settings;
    }

    public CapsuleBehaviorSettings ReadBehavior() => _settings.ReadBehavior();

    public CapsuleGeometrySettings ReadGeometry() => _settings.ReadGeometry();

    public CapsuleArrangementSettings ReadArrangement() => _settings.ReadArrangement();

    public CapsuleAnimationSettings ReadAnimation() => _settings.ReadAnimation();

    public CapsuleTimingSettings ReadTiming() => _settings.ReadTiming();

    public void SetWidgetCollapseBehavior(string? behavior) =>
        _settings.SetWidgetCollapseBehavior(behavior);

    public void SetWidgetCompactContentMode(string? mode) =>
        _settings.SetWidgetCompactContentMode(mode);

    public void SetWidgetCompactHideSensitiveContent(bool value) =>
        _settings.SetWidgetCompactHideSensitiveContent(value);

    public void SetWidgetCompactWidthMode(string? mode) =>
        _settings.SetWidgetCompactWidthMode(mode);

    public void SetWidgetCompactExpansionDirection(string? direction) =>
        _settings.SetWidgetCompactExpansionDirection(direction);

    public void SetWidgetCapsuleArrangementMode(string? mode) =>
        _settings.SetWidgetCapsuleArrangementMode(mode);

    public void SetWidgetCapsuleBarPlacement(string? placement) =>
        _settings.SetWidgetCapsuleBarPlacement(placement);

    public void SetWidgetCapsuleBarDirection(string? direction) =>
        _settings.SetWidgetCapsuleBarDirection(direction);

    public void SetWidgetCapsuleBarSpacing(double spacing) =>
        _settings.SetWidgetCapsuleBarSpacing(spacing);

    public void SetWidgetCompactAnimationEffect(string? effect) =>
        _settings.SetWidgetCompactAnimationEffect(effect);

    public void SetWidgetCompactAnimationDurationMs(double value) =>
        _settings.SetWidgetCompactAnimationDurationMs(value);

    public void SetWidgetCompactExpandDelayMs(double value) =>
        _settings.SetWidgetCompactExpandDelayMs(value);

    public void SetWidgetCompactCollapseDelayMs(double value) =>
        _settings.SetWidgetCompactCollapseDelayMs(value);

    public void SetWidgetCompactMediaCornerMode(string? mode) =>
        _settings.SetWidgetCompactMediaCornerMode(mode);
}
