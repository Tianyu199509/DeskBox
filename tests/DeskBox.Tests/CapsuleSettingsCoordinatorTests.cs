using DeskBox.Contracts;
using DeskBox.Features.Capsule;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class CapsuleSettingsCoordinatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void OptionSetters_NormalizeInvalidValues_SkipNoOpWrites()
    {
        var settings = new SettingsService(_root);
        var coordinator = new CapsuleSettingsCoordinator(settings);
        var editor = new CapsuleSettingsViewModel(coordinator);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        editor.SetWidgetCompactWidthMode("NotAMode");
        Assert.Equal(
            SettingsService.WidgetCompactWidthModeAligned,
            settings.Settings.WidgetShell.WidgetCompactWidthMode);

        editor.SetWidgetCompactExpansionDirection("Sideways");
        Assert.Equal(
            SettingsService.WidgetCompactExpansionDirectionAuto,
            settings.Settings.WidgetShell.WidgetCompactExpansionDirection);

        editor.SetWidgetCompactContentMode("Verbose");
        Assert.Equal(
            SettingsService.WidgetCompactContentModeSmart,
            settings.Settings.WidgetShell.WidgetCompactContentMode);

        editor.SetWidgetCollapseBehavior(null);
        Assert.Equal(
            SettingsService.WidgetCollapseBehaviorClick,
            settings.Settings.WidgetShell.WidgetCollapseBehavior);

        editor.SetWidgetCapsuleArrangementMode("Diagonal");
        Assert.Equal(
            SettingsService.WidgetCapsuleArrangementFree,
            settings.Settings.WidgetShell.WidgetCapsuleArrangementMode);

        editor.SetWidgetCompactMediaCornerMode("Hexagon");
        Assert.Equal(
            SettingsService.WidgetCompactMediaCornerFollowWidget,
            settings.Settings.WidgetShell.WidgetCompactMediaCornerMode);

        // Unchanged option writes must not notify or save again.
        editor.SetWidgetCollapseBehavior(SettingsService.WidgetCollapseBehaviorClick);
        int notifiedAfterRepeat = notified;
        editor.SetWidgetCollapseBehavior(SettingsService.WidgetCollapseBehaviorClick);
        Assert.Equal(notifiedAfterRepeat, notified);
    }

    [Fact]
    public void AnimationEffectPreset_PairWritesDuration_WithASingleSave()
    {
        var settings = new SettingsService(_root);
        var coordinator = new CapsuleSettingsCoordinator(settings);
        int notified = 0;
        settings.SettingsChanged += () => notified++;

        coordinator.SetWidgetCompactAnimationEffect(SettingsService.WidgetCompactAnimationSnappy);
        Assert.Equal(SettingsService.WidgetCompactAnimationSnappy,
            settings.Settings.WidgetShell.WidgetCompactAnimationEffect);
        Assert.Equal(SettingsService.SnappyWidgetCompactAnimationDurationMs,
            settings.Settings.WidgetShell.WidgetCompactAnimationDurationMs);
        Assert.Equal(1, notified);

        // Custom and None carry no preset: the stored duration must not move.
        coordinator.SetWidgetCompactAnimationEffect(SettingsService.WidgetCompactAnimationCustom);
        Assert.Equal(SettingsService.SnappyWidgetCompactAnimationDurationMs,
            settings.Settings.WidgetShell.WidgetCompactAnimationDurationMs);

        CapsuleAnimationSettings snapshot = coordinator.ReadAnimation();
        Assert.Equal(SettingsService.WidgetCompactAnimationCustom, snapshot.CompactAnimationEffect);
        Assert.Equal(
            SettingsService.SnappyWidgetCompactAnimationDurationMs,
            snapshot.CompactAnimationDurationMs);
    }

    [Fact]
    public void CustomDurationWrite_FlipsPresetEffectToCustom_ButKeepsCustomOrNone()
    {
        var settings = new SettingsService(_root);
        var coordinator = new CapsuleSettingsCoordinator(settings);

        coordinator.SetWidgetCompactAnimationEffect(SettingsService.WidgetCompactAnimationSmooth);
        coordinator.SetWidgetCompactAnimationDurationMs(301.6d);
        Assert.Equal(SettingsService.WidgetCompactAnimationCustom,
            settings.Settings.WidgetShell.WidgetCompactAnimationEffect);
        Assert.Equal(302, settings.Settings.WidgetShell.WidgetCompactAnimationDurationMs);

        // An explicit Custom effect stays Custom; None never flips to Custom.
        coordinator.SetWidgetCompactAnimationEffect(SettingsService.WidgetCompactAnimationNone);
        coordinator.SetWidgetCompactAnimationDurationMs(280);
        Assert.Equal(SettingsService.WidgetCompactAnimationNone,
            settings.Settings.WidgetShell.WidgetCompactAnimationEffect);

        coordinator.SetWidgetCompactAnimationEffect(SettingsService.WidgetCompactAnimationCustom);
        coordinator.SetWidgetCompactAnimationDurationMs(330);
        Assert.Equal(SettingsService.WidgetCompactAnimationCustom,
            settings.Settings.WidgetShell.WidgetCompactAnimationEffect);
    }

    [Fact]
    public void NumericWrites_NormalizeAndClampBeforePersisting()
    {
        var settings = new SettingsService(_root);
        var coordinator = new CapsuleSettingsCoordinator(settings);

        coordinator.SetWidgetCapsuleBarSpacing(double.NaN);
        Assert.Equal(
            SettingsService.DefaultWidgetCapsuleBarSpacing,
            settings.Settings.WidgetShell.WidgetCapsuleBarSpacing);

        coordinator.SetWidgetCapsuleBarSpacing(9_999d);
        Assert.Equal(
            SettingsService.MaxWidgetCapsuleBarSpacing,
            settings.Settings.WidgetShell.WidgetCapsuleBarSpacing);

        coordinator.SetWidgetCompactAnimationDurationMs(9_999);
        Assert.Equal(
            SettingsService.MaxWidgetCompactAnimationDurationMs,
            settings.Settings.WidgetShell.WidgetCompactAnimationDurationMs);

        coordinator.SetWidgetCompactExpandDelayMs(1);
        Assert.Equal(
            SettingsService.MinWidgetCompactExpandDelayMs,
            settings.Settings.WidgetShell.WidgetCompactExpandDelayMs);

        coordinator.SetWidgetCompactCollapseDelayMs(99_999);
        Assert.Equal(
            SettingsService.MaxWidgetCompactCollapseDelayMs,
            settings.Settings.WidgetShell.WidgetCompactCollapseDelayMs);

        CapsuleTimingSettings timing = coordinator.ReadTiming();
        Assert.Equal(SettingsService.MinWidgetCompactExpandDelayMs, timing.ExpandDelayMs);
        Assert.Equal(SettingsService.MaxWidgetCompactCollapseDelayMs, timing.CollapseDelayMs);
    }

    [Fact]
    public void HoverDelayAndBehaviorWrites_PersistThroughThePort()
    {
        var settings = new SettingsService(_root);
        var coordinator = new CapsuleSettingsCoordinator(settings);

        coordinator.SetWidgetCompactExpandDelayMs(240);
        coordinator.SetWidgetCompactCollapseDelayMs(480);
        coordinator.SetWidgetCompactHideSensitiveContent(true);
        coordinator.SetWidgetCollapseBehavior(SettingsService.WidgetCollapseBehaviorSmart);
        coordinator.SetWidgetCompactContentMode(SettingsService.WidgetCompactContentModeMinimal);
        coordinator.SetWidgetCapsuleBarPlacement(SettingsService.WidgetCapsuleBarPlacementTop);
        coordinator.SetWidgetCapsuleBarDirection(SettingsService.WidgetCapsuleBarDirectionVertical);

        CapsuleBehaviorSettings behavior = coordinator.ReadBehavior();
        Assert.Equal(SettingsService.WidgetCollapseBehaviorSmart, behavior.CollapseBehavior);
        Assert.Equal(SettingsService.WidgetCompactContentModeMinimal, behavior.CompactContentMode);
        Assert.True(behavior.HideSensitiveContent);

        CapsuleArrangementSettings arrangement = coordinator.ReadArrangement();
        Assert.Equal(SettingsService.WidgetCapsuleBarPlacementTop, arrangement.BarPlacement);
        Assert.Equal(SettingsService.WidgetCapsuleBarDirectionVertical, arrangement.BarDirection);
    }

    [Fact]
    public async Task CapsuleWrites_RoundTripThroughDisk()
    {
        var settings = new SettingsService(_root);
        var coordinator = new CapsuleSettingsCoordinator(settings);

        coordinator.SetWidgetCompactWidthMode(SettingsService.WidgetCompactWidthModeIndependent);
        coordinator.SetWidgetCompactExpansionDirection(
            SettingsService.WidgetCompactExpansionDirectionUp);
        coordinator.SetWidgetCompactAnimationEffect(SettingsService.WidgetCompactAnimationSlow);
        coordinator.SetWidgetCompactExpandDelayMs(500);
        coordinator.SetWidgetCompactCollapseDelayMs(900);
        coordinator.SetWidgetCompactMediaCornerMode(SettingsService.WidgetCompactMediaCornerRound);
        coordinator.SetWidgetCapsuleBarSpacing(21);

        await settings.SaveAsync();
        var reloaded = new SettingsService(_root);
        await reloaded.LoadAsync();
        WidgetShellSettingsSlice shell = reloaded.Settings.WidgetShell;
        Assert.Equal(SettingsService.WidgetCompactWidthModeIndependent, shell.WidgetCompactWidthMode);
        Assert.Equal(SettingsService.WidgetCompactExpansionDirectionUp, shell.WidgetCompactExpansionDirection);
        Assert.Equal(SettingsService.WidgetCompactAnimationSlow, shell.WidgetCompactAnimationEffect);
        Assert.Equal(SettingsService.SlowWidgetCompactAnimationDurationMs, shell.WidgetCompactAnimationDurationMs);
        Assert.Equal(500, shell.WidgetCompactExpandDelayMs);
        Assert.Equal(900, shell.WidgetCompactCollapseDelayMs);
        Assert.Equal(SettingsService.WidgetCompactMediaCornerRound, shell.WidgetCompactMediaCornerMode);
        Assert.Equal(21, shell.WidgetCapsuleBarSpacing);
    }

    [Fact]
    public void PresetHelpers_MatchThePersistedPresetFields()
    {
        // The shell's view state and the coordinator share these mappings;
        // drift would split the duration a user sees from the one on disk.
        Assert.Equal(
            SettingsService.SnappyWidgetCompactAnimationDurationMs,
            SettingsService.WidgetCompactAnimationPresetDurationMs(
                SettingsService.WidgetCompactAnimationSnappy));
        Assert.Null(SettingsService.WidgetCompactAnimationPresetDurationMs(
            SettingsService.WidgetCompactAnimationCustom));
        Assert.Null(SettingsService.WidgetCompactAnimationPresetDurationMs(
            SettingsService.WidgetCompactAnimationNone));

        Assert.Equal(
            (SettingsService.SensitiveWidgetCompactExpandDelayMs,
             SettingsService.SensitiveWidgetCompactCollapseDelayMs),
            SettingsService.WidgetCompactHoverResponsePresetDelays(
                SettingsService.WidgetCompactHoverResponseSensitive));
        Assert.Equal(
            (SettingsService.DefaultWidgetCompactExpandDelayMs,
             SettingsService.DefaultWidgetCompactCollapseDelayMs),
            SettingsService.WidgetCompactHoverResponsePresetDelays(
                SettingsService.WidgetCompactHoverResponseBalanced));
        Assert.Null(SettingsService.WidgetCompactHoverResponsePresetDelays(
            SettingsService.WidgetCompactHoverResponseCustom));
    }

    [Fact]
    public async Task StoppedCoordinator_RejectsFurtherWrites()
    {
        var settings = new SettingsService(_root);
        var coordinator = new CapsuleSettingsCoordinator(settings);
        coordinator.Stop();

        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetWidgetCollapseBehavior(SettingsService.WidgetCollapseBehaviorSmart));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetWidgetCompactAnimationEffect(SettingsService.WidgetCompactAnimationSnappy));
        Assert.Throws<ObjectDisposedException>(
            () => coordinator.SetWidgetCompactAnimationDurationMs(250));
        Assert.True(coordinator.IsStopped);

        await settings.SaveAsync();
        Assert.Equal(
            SettingsService.WidgetCollapseBehaviorExpanded,
            settings.Settings.WidgetShell.WidgetCollapseBehavior);
        Assert.Equal(
            SettingsService.WidgetCompactAnimationSlow,
            settings.Settings.WidgetShell.WidgetCompactAnimationEffect);
    }
}
