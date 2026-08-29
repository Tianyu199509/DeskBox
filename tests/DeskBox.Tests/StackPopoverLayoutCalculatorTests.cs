using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class StackPopoverLayoutCalculatorTests
{
    [Fact]
    public void Activation_ItemClickBeforePointerReleaseRunsOnlyOnce()
    {
        var arbiter = new StackInputActivationArbiter();

        arbiter.BeginPointer("kind:folder");

        Assert.True(arbiter.ShouldActivateFromItemClick("kind:folder"));
        Assert.False(arbiter.ShouldActivateFromPointerRelease(
            "kind:folder",
            isValidRelease: true));
        arbiter.EndPointer();
    }

    [Fact]
    public void Activation_PointerReleaseBeforeItemClickRunsOnlyOnce()
    {
        var arbiter = new StackInputActivationArbiter();

        arbiter.BeginPointer("kind:folder");

        Assert.True(arbiter.ShouldActivateFromPointerRelease(
            "kind:folder",
            isValidRelease: true));
        arbiter.EndPointer();
        Assert.False(arbiter.ShouldActivateFromItemClick("kind:folder"));
    }

    [Fact]
    public void Activation_NewPointerGestureClearsAnUnconsumedReleaseMarker()
    {
        var arbiter = new StackInputActivationArbiter();

        arbiter.BeginPointer("kind:folder");
        Assert.True(arbiter.ShouldActivateFromPointerRelease(
            "kind:folder",
            isValidRelease: true));
        arbiter.EndPointer();

        arbiter.BeginPointer("kind:folder");
        Assert.True(arbiter.ShouldActivateFromItemClick("kind:folder"));
    }

    [Fact]
    public void Position_CentersPopoverDirectlyOverFolderIconWhenSpaceFits()
    {
        StackPopoverPosition position =
            StackPopoverPositionCalculator.Calculate(
                anchorCenterX: 260,
                anchorCenterY: 594,
                popoverWidth: 220,
                popoverHeight: 214,
                workAreaLeft: 0,
                workAreaTop: 0,
                workAreaWidth: 1920,
                workAreaHeight: 1040);

        Assert.Equal(150, position.Left);
        Assert.Equal(487, position.Top);
        Assert.False(position.IsHorizontallyClamped);
        Assert.False(position.IsVerticallyClamped);
    }

    [Fact]
    public void Position_ClampsOnlyTheAxesThatCrossWorkAreaEdges()
    {
        StackPopoverPosition position =
            StackPopoverPositionCalculator.Calculate(
                anchorCenterX: 30,
                anchorCenterY: 400,
                popoverWidth: 220,
                popoverHeight: 214,
                workAreaLeft: 0,
                workAreaTop: 0,
                workAreaWidth: 1920,
                workAreaHeight: 1040);

        Assert.Equal(8, position.Left);
        Assert.Equal(293, position.Top);
        Assert.True(position.IsHorizontallyClamped);
        Assert.False(position.IsVerticallyClamped);
    }

    [Fact]
    public void Position_ClampsAtBottomRightWithWorkAreaMargin()
    {
        StackPopoverPosition position =
            StackPopoverPositionCalculator.Calculate(
                anchorCenterX: 980,
                anchorCenterY: 760,
                popoverWidth: 240,
                popoverHeight: 220,
                workAreaLeft: 100,
                workAreaTop: 50,
                workAreaWidth: 900,
                workAreaHeight: 720);

        Assert.Equal(752, position.Left);
        Assert.Equal(542, position.Top);
        Assert.True(position.IsHorizontallyClamped);
        Assert.True(position.IsVerticallyClamped);
    }

    [Fact]
    public void FolderPreview_UsesNormalIconSlotAndScalesMiniaturesWithIconSize()
    {
        StackFolderPreviewMetrics small =
            StackFolderPreviewMetricsCalculator.Calculate(
                previewSize: 24,
                previewItemSize: 18,
                isListMode: false);
        StackFolderPreviewMetrics large =
            StackFolderPreviewMetricsCalculator.Calculate(
                previewSize: 56,
                previewItemSize: 43,
                isListMode: false);

        Assert.Equal(24, small.HostSize);
        Assert.Equal(56, large.HostSize);
        Assert.True(large.HostSize > small.HostSize);
        Assert.True(
            43 * large.MiniatureScale >
            18 * small.MiniatureScale);
        Assert.True(
            small.InnerPadding - small.BackdropMargin >= 2.5);
        Assert.True(
            large.InnerPadding > small.InnerPadding);
    }

    [Theory]
    [InlineData(SettingsService.WidgetCornerPreferenceSquare, 0)]
    [InlineData(SettingsService.WidgetCornerPreferenceSmall, 4)]
    [InlineData(SettingsService.WidgetCornerPreferenceRound, 8)]
    public void FolderPreview_CornerRadiusFollowsWidgetPreference(
        string cornerPreference,
        double expectedRadius)
    {
        StackFolderPreviewMetrics metrics =
            StackFolderPreviewMetricsCalculator.Calculate(
                previewSize: 30,
                previewItemSize: 24,
                isListMode: false,
                cornerPreference);

        Assert.Equal(expectedRadius, metrics.CornerRadius);
    }

    [Fact]
    public void IconLayout_ExpandsBeyondVerySmallWidgetWithoutExceedingWorkArea()
    {
        StackPopoverLayout layout = StackPopoverLayoutCalculator.Calculate(
            isListMode: false,
            itemCount: 18,
            widgetWidth: 150,
            workAreaWidth: 1366,
            workAreaHeight: 768,
            itemWidth: 96,
            itemHeight: 112);

        Assert.InRange(layout.Width, 280, 720);
        Assert.InRange(layout.Columns, 2, 6);
        Assert.InRange(layout.VisibleRows, 1, 5);
        Assert.True(layout.Height <= 736);
    }

    [Fact]
    public void IconLayout_ClampsLargeWidgetToSixColumnsAndMaximumWidth()
    {
        StackPopoverLayout layout = StackPopoverLayoutCalculator.Calculate(
            isListMode: false,
            itemCount: 80,
            widgetWidth: 1400,
            workAreaWidth: 1920,
            workAreaHeight: 1080,
            itemWidth: 100,
            itemHeight: 110);

        Assert.Equal(6, layout.Columns);
        Assert.True(layout.Width <= 720);
        Assert.Equal(5, layout.VisibleRows);
    }

    [Fact]
    public void PopoverLayout_Grid3PinsColumnsAndVisibleRowsAndScrollsBeyond()
    {
        StackPopoverLayout layout = StackPopoverLayoutCalculator.Calculate(
            isListMode: false,
            itemCount: 69,
            widgetWidth: 280,
            workAreaWidth: 1920,
            workAreaHeight: 1080,
            itemWidth: 96,
            itemHeight: 112,
            layoutMode: SettingsService.FileStackPopoverLayoutGrid3);

        Assert.Equal(3, layout.Columns);
        Assert.Equal(3, layout.VisibleRows);
        Assert.True(layout.HasVerticalOverflow);
    }

    [Fact]
    public void PopoverLayout_Grid5PinsFiveColumnsAndVisibleRows()
    {
        StackPopoverLayout layout = StackPopoverLayoutCalculator.Calculate(
            isListMode: false,
            itemCount: 69,
            widgetWidth: 280,
            workAreaWidth: 1920,
            workAreaHeight: 1080,
            itemWidth: 96,
            itemHeight: 112,
            layoutMode: SettingsService.FileStackPopoverLayoutGrid5);

        Assert.Equal(5, layout.Columns);
        Assert.Equal(5, layout.VisibleRows);
        Assert.True(layout.HasVerticalOverflow);
    }

    [Fact]
    public void PopoverLayout_GridModeShrinksColumnsToItemCountWhenStackIsSmall()
    {
        StackPopoverLayout layout = StackPopoverLayoutCalculator.Calculate(
            isListMode: false,
            itemCount: 4,
            widgetWidth: 280,
            workAreaWidth: 1920,
            workAreaHeight: 1080,
            itemWidth: 96,
            itemHeight: 112,
            layoutMode: SettingsService.FileStackPopoverLayoutGrid5);

        Assert.Equal(4, layout.Columns);
        // Fixed grid modes cap the shape; rows shrink to the content that
        // actually exists (4 files / 4 columns = 1 row, not 5 empty rows).
        Assert.Equal(1, layout.VisibleRows);
        Assert.False(layout.HasVerticalOverflow);
    }

    [Fact]
    public void PopoverLayout_AdaptiveModeMatchesLegacyBehavior()
    {
        StackPopoverLayout adaptive = StackPopoverLayoutCalculator.Calculate(
            isListMode: false,
            itemCount: 7,
            widgetWidth: 900,
            workAreaWidth: 1920,
            workAreaHeight: 1080,
            itemWidth: 72,
            itemHeight: 82,
            layoutMode: SettingsService.FileStackPopoverLayoutAdaptive);
        StackPopoverLayout unspecified = StackPopoverLayoutCalculator.Calculate(
            isListMode: false,
            itemCount: 7,
            widgetWidth: 900,
            workAreaWidth: 1920,
            workAreaHeight: 1080,
            itemWidth: 72,
            itemHeight: 82);

        Assert.Equal(3, adaptive.Columns);
        Assert.Equal(3, adaptive.VisibleRows);
        Assert.Equal(unspecified, adaptive);
    }

    [Fact]
    public void PopoverLayout_ListModeGrid3LimitsVisibleRowsToThree()
    {
        StackPopoverLayout layout = StackPopoverLayoutCalculator.Calculate(
            isListMode: true,
            itemCount: 40,
            widgetWidth: 280,
            workAreaWidth: 1920,
            workAreaHeight: 1080,
            itemWidth: 32,
            itemHeight: 48,
            layoutMode: SettingsService.FileStackPopoverLayoutGrid3);

        Assert.Equal(1, layout.Columns);
        Assert.Equal(3, layout.VisibleRows);
        Assert.True(layout.HasVerticalOverflow);
    }

    [Fact]
    public void PopoverLayout_GridModesFallBackToAdaptiveForUnknownValues()
    {
        Assert.Null(StackPopoverLayoutCalculator.ResolveFixedColumns("Bogus"));
        Assert.Null(
            StackPopoverLayoutCalculator.ResolveFixedVisibleRows("Bogus"));
        Assert.Equal(
            SettingsService.FileStackPopoverLayoutAdaptive,
            SettingsService.NormalizeFileStackPopoverLayout("Bogus"));
    }

    [Fact]
    public void PopoverStyle_NormalizesToNeutralAndKeepsMaterialOptIn()
    {
        Assert.Equal(
            SettingsService.FileStackPopoverStyleFollowMaterial,
            SettingsService.NormalizeFileStackPopoverStyle(
                SettingsService.FileStackPopoverStyleFollowMaterial));
        Assert.Equal(
            SettingsService.FileStackPopoverStyleNeutral,
            SettingsService.NormalizeFileStackPopoverStyle("Bogus"));
        Assert.Equal(
            SettingsService.FileStackPopoverStyleNeutral,
            SettingsService.NormalizeFileStackPopoverStyle(null));
    }

    [Fact]
    public void PopoverLayout_DefaultsToGrid3ForNewSettings()
    {
        var fresh = new Models.AppSettings();

        Assert.Equal(
            SettingsService.FileStackPopoverLayoutGrid3,
            fresh.FileStackPopoverLayout);
        Assert.Equal(
            SettingsService.FileStackPopoverStyleNeutral,
            fresh.FileStackPopoverStyle);
    }

    [Fact]
    public void IconLayout_SevenItemsUsesACompactCenteredThreeByThreeGrid()
    {
        StackPopoverLayout layout = StackPopoverLayoutCalculator.Calculate(
            isListMode: false,
            itemCount: 7,
            widgetWidth: 900,
            workAreaWidth: 1920,
            workAreaHeight: 1080,
            itemWidth: 72,
            itemHeight: 82);

        Assert.Equal(3, layout.Columns);
        Assert.Equal(3, layout.VisibleRows);
        Assert.Equal(240, layout.ItemsWidth);
        Assert.Equal(258, layout.ItemsHeight);
        Assert.Equal(256, layout.Width);
        Assert.Equal(310, layout.Height);
    }

    [Fact]
    public void IconLayout_FourItemsUsesACompactTwoByTwoSurface()
    {
        StackPopoverLayout layout = StackPopoverLayoutCalculator.Calculate(
            isListMode: false,
            itemCount: 4,
            widgetWidth: 420,
            workAreaWidth: 1920,
            workAreaHeight: 1080,
            itemWidth: 72,
            itemHeight: 82);

        Assert.Equal(2, layout.Columns);
        Assert.Equal(2, layout.VisibleRows);
        Assert.Equal(160, layout.ItemsWidth);
        Assert.Equal(172, layout.ItemsHeight);
        Assert.Equal(184, layout.Width);
        Assert.Equal(224, layout.Height);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(9, 3)]
    [InlineData(10, 4)]
    public void IconLayout_ColumnCountFollowsFolderItemCount(
        int itemCount,
        int expectedColumns)
    {
        StackPopoverLayout layout = StackPopoverLayoutCalculator.Calculate(
            isListMode: false,
            itemCount,
            widgetWidth: 1200,
            workAreaWidth: 1920,
            workAreaHeight: 1080,
            itemWidth: 72,
            itemHeight: 82);

        Assert.Equal(expectedColumns, layout.Columns);
    }

    [Fact]
    public void IconLayout_DoesNotGrowMerelyBecauseTheWidgetIsWide()
    {
        StackPopoverLayout narrow = StackPopoverLayoutCalculator.Calculate(
            false, 7, 180, 1920, 1080, 72, 82);
        StackPopoverLayout wide = StackPopoverLayoutCalculator.Calculate(
            false, 7, 1400, 1920, 1080, 72, 82);

        Assert.Equal(narrow.Width, wide.Width);
        Assert.Equal(narrow.Height, wide.Height);
        Assert.Equal(narrow.Columns, wide.Columns);
    }

    [Theory]
    [InlineData(4, false)]
    [InlineData(30, false)]
    [InlineData(31, true)]
    public void IconLayout_EnablesVerticalScrollingOnlyForHiddenRows(
        int itemCount,
        bool expectedOverflow)
    {
        StackPopoverLayout layout = StackPopoverLayoutCalculator.Calculate(
            false, itemCount, 420, 1920, 1080, 72, 82);

        Assert.Equal(expectedOverflow, layout.HasVerticalOverflow);
    }

    [Fact]
    public void ListLayout_UsesOneColumnAndAtMostEightRows()
    {
        StackPopoverLayout layout = StackPopoverLayoutCalculator.Calculate(
            isListMode: true,
            itemCount: 30,
            widgetWidth: 480,
            workAreaWidth: 1920,
            workAreaHeight: 1080,
            itemWidth: 100,
            itemHeight: 52);

        Assert.Equal(1, layout.Columns);
        Assert.Equal(8, layout.VisibleRows);
        Assert.InRange(layout.Width, 280, 560);
        Assert.True(layout.HasVerticalOverflow);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(9, true)]
    public void ListLayout_EnablesVerticalScrollingOnlyPastVisibleCapacity(
        int itemCount,
        bool expectedOverflow)
    {
        StackPopoverLayout layout = StackPopoverLayoutCalculator.Calculate(
            true, itemCount, 420, 1920, 1080, 100, 52);

        Assert.Equal(expectedOverflow, layout.HasVerticalOverflow);
    }

    [Fact]
    public void ConstrainedWorkArea_AlwaysProducesUsableBoundedLayout()
    {
        StackPopoverLayout layout = StackPopoverLayoutCalculator.Calculate(
            isListMode: false,
            itemCount: 24,
            widgetWidth: 160,
            workAreaWidth: 260,
            workAreaHeight: 240,
            itemWidth: 110,
            itemHeight: 130);

        Assert.InRange(layout.Columns, 1, 2);
        Assert.Equal(1, layout.VisibleRows);
        Assert.True(layout.Width <= 220);
        Assert.True(layout.Height <= 200);
    }
}
