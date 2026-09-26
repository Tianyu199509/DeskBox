using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Sole settings-page writer for the capsule/compact-mode section fields.
/// The section has no live-preview coupling with the appearance machinery:
/// every edit keeps the original semantics of normalize, store, and schedule
/// one debounced save, so capsule behavior still reaches the widget host only
/// through the regular SettingsChanged/refresh chain. Preset pair-writes
/// (animation effect plus duration, hover-response delays via the shell's own
/// delay bindings) commit with the same single debounced save the settings
/// page used before the migration.
/// </summary>
public sealed class CapsuleSettingsCoordinator : ICapsuleSettings
{
    private readonly SettingsService _settings;
    private bool _stopped;

    internal bool IsStopped => _stopped;

    public CapsuleSettingsCoordinator(SettingsService settings)
    {
        _settings = settings;
    }

    public CapsuleBehaviorSettings ReadBehavior()
    {
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        return new(
            shell.WidgetCollapseBehavior,
            shell.WidgetCompactContentMode,
            shell.WidgetCompactHideSensitiveContent);
    }

    public CapsuleGeometrySettings ReadGeometry()
    {
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        return new(
            shell.WidgetCompactWidthMode,
            shell.WidgetCompactExpansionDirection);
    }

    public CapsuleArrangementSettings ReadArrangement()
    {
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        return new(
            shell.WidgetCapsuleArrangementMode,
            shell.WidgetCapsuleBarPlacement,
            shell.WidgetCapsuleBarDirection,
            shell.WidgetCapsuleBarSpacing);
    }

    public CapsuleAnimationSettings ReadAnimation()
    {
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        return new(
            shell.WidgetCompactAnimationEffect,
            shell.WidgetCompactAnimationDurationMs);
    }

    public CapsuleTimingSettings ReadTiming()
    {
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        return new(
            shell.WidgetCompactExpandDelayMs,
            shell.WidgetCompactCollapseDelayMs);
    }

    public void SetWidgetCollapseBehavior(string? behavior)
    {
        ThrowIfStopped();
        SetOption(
            SettingsService.NormalizeWidgetCollapseBehavior(behavior),
            () => _settings.Settings.WidgetShell.WidgetCollapseBehavior,
            value => _settings.Settings.WidgetShell.WidgetCollapseBehavior = value);
    }

    public void SetWidgetCompactContentMode(string? mode)
    {
        ThrowIfStopped();
        SetOption(
            SettingsService.NormalizeWidgetCompactContentMode(mode),
            () => _settings.Settings.WidgetShell.WidgetCompactContentMode,
            value => _settings.Settings.WidgetShell.WidgetCompactContentMode = value);
    }

    public void SetWidgetCompactHideSensitiveContent(bool value)
    {
        ThrowIfStopped();
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetCompactHideSensitiveContent == value) return;
        shell.WidgetCompactHideSensitiveContent = value;
        _settings.SaveDebounced();
    }

    public void SetWidgetCompactWidthMode(string? mode)
    {
        ThrowIfStopped();
        SetOption(
            SettingsService.NormalizeWidgetCompactWidthMode(mode),
            () => _settings.Settings.WidgetShell.WidgetCompactWidthMode,
            value => _settings.Settings.WidgetShell.WidgetCompactWidthMode = value);
    }

    public void SetWidgetCompactExpansionDirection(string? direction)
    {
        ThrowIfStopped();
        SetOption(
            SettingsService.NormalizeWidgetCompactExpansionDirection(direction),
            () => _settings.Settings.WidgetShell.WidgetCompactExpansionDirection,
            value => _settings.Settings.WidgetShell.WidgetCompactExpansionDirection = value);
    }

    public void SetWidgetCapsuleArrangementMode(string? mode)
    {
        ThrowIfStopped();
        SetOption(
            SettingsService.NormalizeWidgetCapsuleArrangementMode(mode),
            () => _settings.Settings.WidgetShell.WidgetCapsuleArrangementMode,
            value => _settings.Settings.WidgetShell.WidgetCapsuleArrangementMode = value);
    }

    public void SetWidgetCapsuleBarPlacement(string? placement)
    {
        ThrowIfStopped();
        SetOption(
            SettingsService.NormalizeWidgetCapsuleBarPlacement(placement),
            () => _settings.Settings.WidgetShell.WidgetCapsuleBarPlacement,
            value => _settings.Settings.WidgetShell.WidgetCapsuleBarPlacement = value);
    }

    public void SetWidgetCapsuleBarDirection(string? direction)
    {
        ThrowIfStopped();
        SetOption(
            SettingsService.NormalizeWidgetCapsuleBarDirection(direction),
            () => _settings.Settings.WidgetShell.WidgetCapsuleBarDirection,
            value => _settings.Settings.WidgetShell.WidgetCapsuleBarDirection = value);
    }

    public void SetWidgetCapsuleBarSpacing(double spacing)
    {
        ThrowIfStopped();
        double normalized = SettingsService.NormalizeWidgetCapsuleBarSpacing(spacing);
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetCapsuleBarSpacing == normalized) return;
        shell.WidgetCapsuleBarSpacing = normalized;
        _settings.SaveDebounced();
    }

    public void SetWidgetCompactAnimationEffect(string? effect)
    {
        ThrowIfStopped();
        string normalized = SettingsService.NormalizeWidgetCompactAnimationEffect(effect);
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        shell.WidgetCompactAnimationEffect = normalized;
        if (SettingsService.WidgetCompactAnimationPresetDurationMs(normalized) is { } duration)
        {
            shell.WidgetCompactAnimationDurationMs = duration;
        }

        _settings.SaveDebounced();
    }

    public void SetWidgetCompactAnimationDurationMs(double value)
    {
        ThrowIfStopped();
        int normalized = SettingsService.NormalizeWidgetCompactAnimationDurationMs((int)Math.Round(value));
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetCompactAnimationEffect is not
            (SettingsService.WidgetCompactAnimationCustom or
             SettingsService.WidgetCompactAnimationNone))
        {
            shell.WidgetCompactAnimationEffect = SettingsService.WidgetCompactAnimationCustom;
        }

        shell.WidgetCompactAnimationDurationMs = normalized;
        _settings.SaveDebounced();
    }

    public void SetWidgetCompactExpandDelayMs(double value)
    {
        ThrowIfStopped();
        int normalized = SettingsService.NormalizeWidgetCompactExpandDelayMs((int)Math.Round(value));
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetCompactExpandDelayMs == normalized) return;
        shell.WidgetCompactExpandDelayMs = normalized;
        _settings.SaveDebounced();
    }

    public void SetWidgetCompactCollapseDelayMs(double value)
    {
        ThrowIfStopped();
        int normalized = SettingsService.NormalizeWidgetCompactCollapseDelayMs((int)Math.Round(value));
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetCompactCollapseDelayMs == normalized) return;
        shell.WidgetCompactCollapseDelayMs = normalized;
        _settings.SaveDebounced();
    }

    public void SetWidgetCompactMediaCornerMode(string? mode)
    {
        ThrowIfStopped();
        SetOption(
            SettingsService.NormalizeWidgetCompactMediaCornerMode(mode),
            () => _settings.Settings.WidgetShell.WidgetCompactMediaCornerMode,
            value => _settings.Settings.WidgetShell.WidgetCompactMediaCornerMode = value);
    }

    // Option edits keep the section's original immediate-save semantics;
    // unchanged values skip the redundant save and SettingsChanged pass.
    private void SetOption(string normalized, Func<string> readStored, Action<string> store)
    {
        if (readStored() == normalized) return;
        store(normalized);
        _settings.SaveDebounced();
    }

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopped, this);

    internal void Stop() => _stopped = true;
}
