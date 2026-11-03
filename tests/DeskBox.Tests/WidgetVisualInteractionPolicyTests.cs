using DeskBox.Models;

namespace DeskBox.Tests;

public sealed class WidgetVisualInteractionPolicyTests
{
    [Theory]
    [InlineData(WidgetFeedbackSeverity.Info, 1800)]
    [InlineData(WidgetFeedbackSeverity.Success, 1800)]
    [InlineData(WidgetFeedbackSeverity.Warning, 3000)]
    [InlineData(WidgetFeedbackSeverity.Error, 4500)]
    public void FeedbackDuration_FollowsSeverity(
        WidgetFeedbackSeverity severity,
        int expectedMilliseconds)
    {
        var request = new WidgetFeedbackRequest("message", severity);

        Assert.Equal(
            expectedMilliseconds,
            request.DisplayDuration.TotalMilliseconds);
    }

    [Fact]
    public void FeedbackWithAction_UsesFiveSecondWindow()
    {
        var request = new WidgetFeedbackRequest(
            "deleted",
            WidgetFeedbackSeverity.Info,
            "delete",
            "Undo",
            () => Task.CompletedTask);

        Assert.Equal(5000, request.DisplayDuration.TotalMilliseconds);
    }

    [Fact]
    public void FeedbackWithDismiss_UsesFiveSecondWindow()
    {
        var request = new WidgetFeedbackRequest(
            "copied",
            WidgetFeedbackSeverity.Info,
            "drag-out-result-tip",
            DismissAction: () => Task.CompletedTask);

        Assert.Equal(5000, request.DisplayDuration.TotalMilliseconds);
    }

    [Theory]
    [InlineData(0, SearchAppMotionKind.FadeScale, 167, 0, 0)]
    [InlineData(1, SearchAppMotionKind.Rise, 167, 4, 0)]
    [InlineData(2, SearchAppMotionKind.Wave, 167, 4, 120)]
    [InlineData(3, SearchAppMotionKind.SoftScale, 200, 0, 0)]
    public void SearchMotion_IsRestrained(
        int persistedStyle,
        SearchAppMotionKind expectedKind,
        int expectedDuration,
        double expectedTranslation,
        int expectedStagger)
    {
        SearchMotionProfile profile =
            SearchMotionProfile.Resolve(persistedStyle, true);

        Assert.Equal(expectedKind, profile.Kind);
        Assert.Equal(expectedDuration, profile.DurationMilliseconds);
        Assert.Equal(expectedTranslation, profile.TranslationY);
        Assert.Equal(expectedStagger, profile.MaximumStaggerMilliseconds);
        Assert.InRange(profile.DurationMilliseconds, 0, 250);
        Assert.InRange(profile.TranslationY, 0, 4);
    }

    [Fact]
    public void SearchMotion_ReducedMotionIsStatic()
    {
        SearchMotionProfile profile = SearchMotionProfile.Resolve(2, false);

        Assert.False(profile.IsAnimated);
        Assert.Equal(0, profile.DurationMilliseconds);
        Assert.Equal(0, profile.TranslationY);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(1, true, true)]
    [InlineData(2, false, false)]
    public void FileSelectionCommands_RespectSingleSelection(
        int count,
        bool canOpen,
        bool canRename)
    {
        FileSelectionCommandState state =
            FileSelectionCommandState.Resolve(count);

        Assert.Equal(canOpen, state.CanOpen);
        Assert.Equal(canRename, state.CanRename);
        Assert.Equal(count > 0, state.CanDelete);
    }

    [Theory]
    [InlineData("Smooth", 280, 280, true)]
    [InlineData("Slow", 220, 360, true)]
    [InlineData("Snappy", 220, 160, true)]
    [InlineData("Custom", 800, 400, true)]
    [InlineData("None", 220, 0, false)]
    public void CompactProfile_ResolvesPreset(
        string preset,
        int configuredDuration,
        int expectedDuration,
        bool expectedAnimated)
    {
        WidgetCompactTransitionVisualProfile profile =
            WidgetCompactTransitionVisualProfile.Resolve(
                preset,
                configuredDuration,
                true);

        Assert.Equal(expectedDuration, profile.DurationMilliseconds);
        Assert.Equal(expectedAnimated, profile.IsAnimated);
        Assert.InRange(profile.IdentityTranslation, 0, 4);
    }

    [Fact]
    public void CompactProfile_ReducedMotionIsImmediate()
    {
        WidgetCompactTransitionVisualProfile profile =
            WidgetCompactTransitionVisualProfile.Resolve("Slow", 220, false);

        Assert.False(profile.IsAnimated);
        Assert.Equal(0, profile.DurationMilliseconds);
        Assert.Equal((1d, 0d), profile.GetOpacity(true, 0.5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactProfile_NeverShowsCompactAndExpandedIdentityTogether(
        bool collapsing)
    {
        WidgetCompactTransitionVisualProfile profile =
            WidgetCompactTransitionVisualProfile.Resolve("Smooth", 220, true);

        for (int step = 0; step <= 100; step++)
        {
            (double compact, double expanded) =
                profile.GetOpacity(collapsing, step / 100d);

            Assert.False(
                compact > 0.001 && expanded > 0.001,
                $"Both identity layers were visible at step {step}.");
        }
    }

    [Fact]
    public void CompactProfile_ExpansionKeepsLiveContentVisible()
    {
        WidgetCompactTransitionVisualProfile profile =
            WidgetCompactTransitionVisualProfile.Resolve("Smooth", 220, true);

        Assert.Equal(0, profile.GetCompactSurfaceOpacity(collapsing: false, progress: 0.25));

        for (int step = 0; step <= 100; step++)
        {
            Assert.Equal(
                1,
                profile.GetLiveContentOpacity(
                    collapsing: false,
                    progress: step / 100d));
        }
    }

    [Fact]
    public void CompactProfile_MicroScaleAndAsymmetricDurationFollowBrimDeckChoreography()
    {
        WidgetCompactTransitionVisualProfile profile =
            WidgetCompactTransitionVisualProfile.Resolve("Smooth", 220, true);

        Assert.Equal(0.96, profile.GetLiveContentScale(collapsing: false, progress: 0), 3);
        Assert.Equal(1.0, profile.GetLiveContentScale(collapsing: false, progress: 1), 3);
        Assert.Equal(1.0, profile.GetLiveContentScale(collapsing: true, progress: 0), 3);
        Assert.Equal(0.96, profile.GetLiveContentScale(collapsing: true, progress: 1), 3);

        Assert.Equal(1.0, profile.GetCompactContentScale(collapsing: false, progress: 0), 3);
        Assert.Equal(0.96, profile.GetCompactContentScale(collapsing: false, progress: 1), 3);
        Assert.Equal(0.96, profile.GetCompactContentScale(collapsing: true, progress: 0), 3);
        Assert.Equal(1.0, profile.GetCompactContentScale(collapsing: true, progress: 1), 3);

        int expandDuration = profile.ResolveEffectiveDurationMilliseconds(collapsing: false, remainingShare: 1.0);
        int collapseDuration = profile.ResolveEffectiveDurationMilliseconds(collapsing: true, remainingShare: 1.0);
        int redirectedDuration = profile.ResolveEffectiveDurationMilliseconds(collapsing: false, remainingShare: 0.2);

        Assert.Equal(220, expandDuration);
        Assert.True(collapseDuration < expandDuration);
        Assert.Equal((int)Math.Round(220 * 0.40), redirectedDuration);

        Assert.Equal(0, profile.EaseSurfaceProgress(collapsing: true, progress: 0.08, redirectedMidFlight: false));
        Assert.True(profile.EaseSurfaceProgress(collapsing: true, progress: 0.08, redirectedMidFlight: true) > 0);
    }

    [Fact]
    public void CompactProfile_CollapseKeepsLiveContentAndRevealsCapsuleEarly()
    {
        WidgetCompactTransitionVisualProfile profile =
            WidgetCompactTransitionVisualProfile.Resolve("Smooth", 220, true);

        Assert.Equal(1, profile.GetLiveContentOpacity(collapsing: true, progress: 0));
        Assert.Equal(1, profile.GetCompactSurfaceOpacity(collapsing: true, progress: 0));
        Assert.Equal(1, profile.GetCompactSurfaceOpacity(collapsing: true, progress: 1));
        Assert.Equal(0, profile.GetCompactTextOpacity(collapsing: true, progress: 0.68));
        Assert.True(profile.GetCompactIdentityOpacity(collapsing: true, progress: 0.68) > 0);
        Assert.True(profile.GetCompactTextOpacity(collapsing: true, progress: 0.8) > 0);
    }

    [Fact]
    public void CompactProfile_CollapseNeverOverlapsLiveAndCompactText()
    {
        WidgetCompactTransitionVisualProfile profile =
            WidgetCompactTransitionVisualProfile.Resolve("Smooth", 220, true);

        for (int step = 0; step <= 100; step++)
        {
            double progress = step / 100d;
            double live = profile.GetLiveContentOpacity(collapsing: true, progress);
            double compactText = profile.GetCompactTextOpacity(collapsing: true, progress);

            Assert.False(
                live > 0.01 && compactText > 0.01,
                $"Live and compact text overlap at step {step}.");
        }
    }

    [Fact]
    public void ActiveUndoFeedback_IsNotClobberedByPassiveInfoOrSuccessToasts()
    {
        var activeUndo = new WidgetFeedbackRequest(
            "Deleted note",
            WidgetFeedbackSeverity.Info,
            "quick-delete",
            "Undo",
            () => Task.CompletedTask);
        var passiveCopy = new WidgetFeedbackRequest(
            "Copied",
            WidgetFeedbackSeverity.Success,
            "quick-copied");
        var newerUndo = new WidgetFeedbackRequest(
            "Deleted another note",
            WidgetFeedbackSeverity.Info,
            "quick-delete-2",
            "Undo",
            () => Task.CompletedTask);
        var errorAlert = new WidgetFeedbackRequest(
            "Save failed",
            WidgetFeedbackSeverity.Error,
            "quick-save-error");

        Assert.True(WidgetFeedbackPolicy.ShouldPreserveActiveAction(activeUndo, passiveCopy));
        Assert.False(WidgetFeedbackPolicy.ShouldPreserveActiveAction(activeUndo, newerUndo));
        Assert.False(WidgetFeedbackPolicy.ShouldPreserveActiveAction(activeUndo, errorAlert));
        Assert.False(WidgetFeedbackPolicy.ShouldPreserveActiveAction(null, passiveCopy));
    }
}
