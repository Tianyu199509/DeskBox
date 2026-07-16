using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using Windows.Graphics;

namespace DeskBox.Tests;

public sealed class WidgetCompactBoundsCalculatorTests
{
    [Theory]
    [InlineData(WidgetPositionAnchors.LeftTop, 100, 200)]
    [InlineData(WidgetPositionAnchors.RightTop, 452, 200)]
    [InlineData(WidgetPositionAnchors.LeftBottom, 100, 558)]
    [InlineData(WidgetPositionAnchors.RightBottom, 452, 558)]
    public void Calculate_PreservesConfiguredAnchor(string anchor, int expectedX, int expectedY)
    {
        var result = WidgetCompactBoundsCalculator.Calculate(
            new RectInt32(100, 200, 600, 400),
            anchor,
            1,
            SettingsService.WidgetCompactContentModeSummary);

        Assert.Equal(expectedX, result.X);
        Assert.Equal(expectedY, result.Y);
        Assert.Equal(248, result.Width);
        Assert.Equal(42, result.Height);
    }

    [Fact]
    public void Calculate_MinimalStyleUsesScaledCompactSize()
    {
        var result = WidgetCompactBoundsCalculator.Calculate(
            new RectInt32(0, 0, 600, 400),
            WidgetPositionAnchors.LeftTop,
            1.5,
            SettingsService.WidgetCompactContentModeMinimal);

        Assert.Equal(258, result.Width);
        Assert.Equal(63, result.Height);
    }

    [Theory]
    [InlineData(SettingsService.WidgetCompactContentModeSmart, WidgetKind.File, 42)]
    [InlineData(SettingsService.WidgetCompactContentModeSmart, WidgetKind.Todo, 42)]
    [InlineData(SettingsService.WidgetCompactContentModeSmart, WidgetKind.Music, 52)]
    [InlineData(SettingsService.WidgetCompactContentModeSmart, WidgetKind.Weather, 52)]
    [InlineData(SettingsService.WidgetCompactContentModeSmart, WidgetKind.QuickCapture, 52)]
    [InlineData(SettingsService.WidgetCompactContentModeSummary, WidgetKind.Music, 42)]
    [InlineData(SettingsService.WidgetCompactContentModeMinimal, WidgetKind.QuickCapture, 42)]
    public void Calculate_UsesContentAppropriateHeight(string contentMode, WidgetKind kind, int expectedHeight)
    {
        RectInt32 result = WidgetCompactBoundsCalculator.Calculate(
            new RectInt32(0, 0, 600, 400),
            WidgetPositionAnchors.LeftTop,
            1,
            contentMode,
            kind);

        Assert.Equal(expectedHeight, result.Height);
    }

    [Theory]
    [InlineData(WidgetKind.File, 272)]
    [InlineData(WidgetKind.Todo, 272)]
    [InlineData(WidgetKind.Music, 320)]
    public void Calculate_SmartStyleUsesWidgetAppropriateWidth(WidgetKind kind, int expectedWidth)
    {
        var result = WidgetCompactBoundsCalculator.Calculate(
            new RectInt32(0, 0, 600, 400),
            WidgetPositionAnchors.LeftTop,
            1,
            SettingsService.WidgetCompactContentModeSmart,
            kind);

        Assert.Equal(expectedWidth, result.Width);
    }

    [Theory]
    [InlineData(100, 144)]
    [InlineData(286, 286)]
    [InlineData(700, 480)]
    public void Calculate_CustomWidthOverridesStyleAndIsClamped(double customWidth, int expectedWidth)
    {
        var result = WidgetCompactBoundsCalculator.Calculate(
            new RectInt32(0, 0, 600, 400),
            WidgetPositionAnchors.LeftTop,
            1,
            SettingsService.WidgetCompactContentModeMinimal,
            WidgetKind.Music,
            customWidth);

        Assert.Equal(expectedWidth, result.Width);
    }

    [Theory]
    [InlineData(144, WidgetCompactWidthTier.Narrow)]
    [InlineData(209.9, WidgetCompactWidthTier.Narrow)]
    [InlineData(210, WidgetCompactWidthTier.Standard)]
    [InlineData(299.9, WidgetCompactWidthTier.Standard)]
    [InlineData(300, WidgetCompactWidthTier.Wide)]
    [InlineData(480, WidgetCompactWidthTier.Wide)]
    [InlineData(double.NaN, WidgetCompactWidthTier.Standard)]
    public void ResolveWidthTier_UsesLogicalWidthBreakpoints(
        double width,
        WidgetCompactWidthTier expected)
    {
        Assert.Equal(expected, WidgetCompactBoundsCalculator.ResolveWidthTier(width));
    }

    [Theory]
    [InlineData(WidgetPositionAnchors.LeftTop, 100, 200)]
    [InlineData(WidgetPositionAnchors.RightTop, 452, 200)]
    [InlineData(WidgetPositionAnchors.LeftBottom, 100, 308)]
    [InlineData(WidgetPositionAnchors.RightBottom, 452, 308)]
    public void ApplyCompactSizeToResolvedPlacement_RemovesExpandedMinimumHeight(
        string anchor,
        int expectedX,
        int expectedY)
    {
        var result = WidgetCompactBoundsCalculator.ApplyCompactSizeToResolvedPlacement(
            new RectInt32(100, 200, 600, 150),
            anchor,
            1,
            WidgetCompactBoundsCalculator.SummaryWidth);

        Assert.Equal(expectedX, result.X);
        Assert.Equal(expectedY, result.Y);
        Assert.Equal(248, result.Width);
        Assert.Equal(42, result.Height);
    }

    [Theory]
    [InlineData(WidgetPositionAnchors.LeftTop, 100, 20)]
    [InlineData(WidgetPositionAnchors.RightTop, 0, 20)]
    [InlineData(WidgetPositionAnchors.LeftBottom, 100, 0)]
    [InlineData(WidgetPositionAnchors.RightBottom, 0, 0)]
    public void AnchorExpandedBoundsToCompact_PreservesCompactAnchorAndClampsToWorkArea(
        string anchor,
        int expectedX,
        int expectedY)
    {
        var result = WidgetCompactBoundsCalculator.AnchorExpandedBoundsToCompact(
            new RectInt32(100, 20, 172, 48),
            new RectInt32(900, 700, 360, 300),
            anchor,
            new RectInt32(0, 0, 1000, 800));

        Assert.Equal(expectedX, result.X);
        Assert.Equal(expectedY, result.Y);
        Assert.Equal(360, result.Width);
        Assert.Equal(300, result.Height);
    }

    [Theory]
    [InlineData(SettingsService.WidgetCornerPreferenceSquare, 0, 0)]
    [InlineData(SettingsService.WidgetCornerPreferenceSmall, 4, 2)]
    [InlineData(SettingsService.WidgetCornerPreferenceRound, 8, 4)]
    [InlineData(SettingsService.WidgetCornerPreferenceDefault, 8, 4)]
    public void CompactCornerRadii_FollowWindowCornerPreference(
        string preference,
        double expectedOuter,
        double expectedInner)
    {
        Assert.Equal(expectedOuter, WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(preference));
        Assert.Equal(expectedInner, WidgetCompactBoundsCalculator.ResolveInnerCornerRadius(preference));
    }

    [Theory]
    [InlineData(SettingsService.WidgetCompactMediaCornerFollowWidget, SettingsService.WidgetCornerPreferenceSquare, 0)]
    [InlineData(SettingsService.WidgetCompactMediaCornerFollowWidget, SettingsService.WidgetCornerPreferenceRound, 4)]
    [InlineData(SettingsService.WidgetCompactMediaCornerSquare, SettingsService.WidgetCornerPreferenceRound, 0)]
    [InlineData(SettingsService.WidgetCompactMediaCornerSmall, SettingsService.WidgetCornerPreferenceSquare, 4)]
    [InlineData(SettingsService.WidgetCompactMediaCornerRound, SettingsService.WidgetCornerPreferenceSquare, 21)]
    public void MediaCornerRadius_UsesConfiguredMode(
        string mode,
        string cornerPreference,
        double expected)
    {
        Assert.Equal(
            expected,
            WidgetCompactBoundsCalculator.ResolveMediaCornerRadius(mode, cornerPreference));
    }

    [Theory]
    [InlineData(WidgetPositionAnchors.LeftTop, 100, 200)]
    [InlineData(WidgetPositionAnchors.RightTop, 140, 200)]
    [InlineData(WidgetPositionAnchors.LeftBottom, 100, 212)]
    [InlineData(WidgetPositionAnchors.RightBottom, 140, 212)]
    public void ApplySizeToStablePlacement_PreservesSelectedEdges(
        string anchor,
        int expectedX,
        int expectedY)
    {
        RectInt32 result = WidgetCompactBoundsCalculator.ApplySizeToStablePlacement(
            new RectInt32(100, 200, 280, 60),
            240,
            48,
            anchor);

        Assert.Equal(new RectInt32(expectedX, expectedY, 240, 48), result);
    }

    [Theory]
    [InlineData(null, SettingsService.WidgetCollapseBehaviorClick)]
    [InlineData("unexpected", SettingsService.WidgetCollapseBehaviorClick)]
    [InlineData("manual", SettingsService.WidgetCollapseBehaviorClick)]
    [InlineData("auto", SettingsService.WidgetCollapseBehaviorSmart)]
    [InlineData("expanded", SettingsService.WidgetCollapseBehaviorExpanded)]
    [InlineData("smart", SettingsService.WidgetCollapseBehaviorSmart)]
    public void NormalizeCollapseBehavior_ConstrainsValue(string? value, string expected)
    {
        Assert.Equal(expected, SettingsService.NormalizeWidgetCollapseBehavior(value));
    }

    [Theory]
    [InlineData(null, SettingsService.WidgetCollapsedStyleSummary)]
    [InlineData("unexpected", SettingsService.WidgetCollapsedStyleSummary)]
    [InlineData("minimal", SettingsService.WidgetCollapsedStyleMinimal)]
    [InlineData("pill", SettingsService.WidgetCollapsedStylePill)]
    public void NormalizeCollapsedStyle_ConstrainsValue(string? value, string expected)
    {
        Assert.Equal(expected, SettingsService.NormalizeWidgetCollapsedStyle(value));
    }

    [Theory]
    [InlineData(null, SettingsService.WidgetCompactContentModeSmart)]
    [InlineData("unexpected", SettingsService.WidgetCompactContentModeSmart)]
    [InlineData("minimal", SettingsService.WidgetCompactContentModeMinimal)]
    [InlineData("summary", SettingsService.WidgetCompactContentModeSummary)]
    public void NormalizeCompactContentMode_ConstrainsValue(string? value, string expected)
    {
        Assert.Equal(expected, SettingsService.NormalizeWidgetCompactContentMode(value));
    }

    [Theory]
    [InlineData(null, SettingsService.WidgetCompactAnimationSmooth)]
    [InlineData("slow", SettingsService.WidgetCompactAnimationSlow)]
    [InlineData("snappy", SettingsService.WidgetCompactAnimationSnappy)]
    [InlineData("none", SettingsService.WidgetCompactAnimationNone)]
    [InlineData("unexpected", SettingsService.WidgetCompactAnimationSmooth)]
    public void NormalizeCompactAnimation_ConstrainsValue(string? value, string expected)
    {
        Assert.Equal(expected, SettingsService.NormalizeWidgetCompactAnimationEffect(value));
    }

    [Theory]
    [InlineData(10, SettingsService.MinWidgetCompactAnimationDurationMs)]
    [InlineData(220, 220)]
    [InlineData(900, SettingsService.MaxWidgetCompactAnimationDurationMs)]
    public void NormalizeCompactAnimationDuration_ClampsValue(int value, int expected)
    {
        Assert.Equal(expected, SettingsService.NormalizeWidgetCompactAnimationDurationMs(value));
    }

    [Fact]
    public void WidgetConfig_CollapsedStateRoundTripsThroughJson()
    {
        var config = new WidgetConfig { IsCollapsed = true };

        string json = JsonSerializer.Serialize(config);
        WidgetConfig? restored = JsonSerializer.Deserialize<WidgetConfig>(json);

        Assert.NotNull(restored);
        Assert.True(restored.IsCollapsed);
    }

    [Fact]
    public void CollapseBehaviorOverride_ResolvesPerWidgetBeforeGlobalDefault()
    {
        var config = new WidgetConfig();
        WidgetCollapseBehaviorNames.SetOverride(config, WidgetCollapseBehavior.Expanded);

        WidgetCollapseBehavior resolved = WidgetCollapseBehaviorNames.Resolve(
            config,
            SettingsService.WidgetCollapseBehaviorSmart);

        Assert.Equal(WidgetCollapseBehavior.Expanded, resolved);
    }

    [Fact]
    public void CompactPlacement_RoundTripsThroughJson()
    {
        var config = new WidgetConfig
        {
            CompactPlacement = new WidgetCompactPlacement
            {
                X = 120,
                Y = 48,
                PositionAnchor = WidgetPositionAnchors.LeftTop,
                PositionMarginX = 120,
                PositionMarginY = 48,
                PositionMonitorKey = "0:0:1920:1040"
            }
        };

        string json = JsonSerializer.Serialize(config);
        WidgetConfig? restored = JsonSerializer.Deserialize<WidgetConfig>(json);

        Assert.NotNull(restored?.CompactPlacement);
        Assert.Equal(WidgetPositionAnchors.LeftTop, restored.CompactPlacement.PositionAnchor);
        Assert.Equal(120, restored.CompactPlacement.PositionMarginX);
        Assert.Equal(48, restored.CompactPlacement.PositionMarginY);
    }

    [Fact]
    public void CompactWidth_RoundTripsThroughJson()
    {
        var config = new WidgetConfig { CompactWidth = 316 };

        string json = JsonSerializer.Serialize(config);
        WidgetConfig? restored = JsonSerializer.Deserialize<WidgetConfig>(json);

        Assert.Equal(316, restored?.CompactWidth);
    }
}
