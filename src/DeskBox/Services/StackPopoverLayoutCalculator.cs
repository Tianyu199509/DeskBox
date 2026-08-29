namespace DeskBox.Services;

internal readonly record struct StackPopoverLayout(
    double Width,
    double Height,
    double ItemsWidth,
    double ItemsHeight,
    double CellWidth,
    double CellHeight,
    int Columns,
    int VisibleRows,
    bool HasVerticalOverflow);

internal readonly record struct StackFolderPreviewMetrics(
    double HostSize,
    double BackdropMargin,
    double CornerRadius,
    double MiniatureScale,
    double MiniatureOffset,
    double BadgeSize,
    double BadgeFontSize,
    double InnerPadding);

internal readonly record struct StackPopoverPosition(
    double Left,
    double Top,
    bool IsHorizontallyClamped,
    bool IsVerticallyClamped);

/// <summary>
/// Centers the popover over the stack-folder icon and only moves it when an
/// edge of the monitor work area would otherwise be crossed.
/// </summary>
internal static class StackPopoverPositionCalculator
{
    private const double WorkAreaEdgeMargin = 8;

    public static StackPopoverPosition Calculate(
        double anchorCenterX,
        double anchorCenterY,
        double popoverWidth,
        double popoverHeight,
        double workAreaLeft,
        double workAreaTop,
        double workAreaWidth,
        double workAreaHeight)
    {
        double width = Math.Max(1, popoverWidth);
        double height = Math.Max(1, popoverHeight);
        double desiredLeft = anchorCenterX - (width / 2);
        double desiredTop = anchorCenterY - (height / 2);
        double left = ClampToWorkArea(
            desiredLeft,
            width,
            workAreaLeft,
            workAreaWidth);
        double top = ClampToWorkArea(
            desiredTop,
            height,
            workAreaTop,
            workAreaHeight);

        return new StackPopoverPosition(
            left,
            top,
            Math.Abs(left - desiredLeft) > 0.01,
            Math.Abs(top - desiredTop) > 0.01);
    }

    private static double ClampToWorkArea(
        double desiredStart,
        double contentLength,
        double workAreaStart,
        double workAreaLength)
    {
        double availableLength = Math.Max(1, workAreaLength);
        double minimum = workAreaStart + WorkAreaEdgeMargin;
        double maximum =
            workAreaStart + availableLength - WorkAreaEdgeMargin - contentLength;
        if (maximum < minimum)
        {
            // The layout calculator normally reserves more than this margin.
            // Centering is still the least destructive fallback on an
            // exceptionally small or transient work area.
            return workAreaStart + ((availableLength - contentLength) / 2);
        }

        return Math.Clamp(desiredStart, minimum, maximum);
    }
}

/// <summary>
/// Keeps the phone-folder-style stack preview proportional to the configured
/// file icon size. The preview stays inside the same layout slot as a normal
/// file icon so its label remains on the shared row baseline.
/// </summary>
internal static class StackFolderPreviewMetricsCalculator
{
    public static StackFolderPreviewMetrics Calculate(
        double previewSize,
        double previewItemSize,
        bool isListMode,
        string? cornerPreference = null)
    {
        double normalizedPreviewSize = Math.Max(1, previewSize);
        double hostSize = normalizedPreviewSize;
        double backdropMargin = Math.Clamp(
            hostSize * 0.025,
            0.75,
            1.5);
        double innerPadding = Math.Max(
            isListMode ? 2.5 : 3.5,
            hostSize * (isListMode ? 0.10 : 0.12));
        double gap = Math.Max(0.75, hostSize * 0.03);
        double miniatureSize = Math.Max(
            5,
            (hostSize - (innerPadding * 2) - gap) / 2);
        double miniatureScale = Math.Clamp(
            miniatureSize / Math.Max(1, previewItemSize),
            0.34,
            0.68);
        double renderedMiniatureSize =
            Math.Max(1, previewItemSize) * miniatureScale;
        double miniatureOffset =
            (hostSize / 2) - innerPadding - (renderedMiniatureSize / 2);
        double badgeSize = Math.Clamp(
            hostSize * 0.29,
            isListMode ? 9 : 10,
            isListMode ? 14 : 20);
        double badgeFontSize = Math.Clamp(
            badgeSize * 0.55,
            7,
            isListMode ? 8.5 : 10);

        return new StackFolderPreviewMetrics(
            hostSize,
            backdropMargin,
            WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(
                cornerPreference),
            miniatureScale,
            miniatureOffset,
            badgeSize,
            badgeFontSize,
            innerPadding);
    }
}

/// <summary>
/// Computes a bounded stack popover size without depending on WinUI. Keeping
/// this policy separate makes small widgets, large displays, and constrained
/// work areas deterministic and testable.
/// </summary>
internal static class StackPopoverLayoutCalculator
{
    internal const double SurfacePadding = 8;
    internal const double TitleHeight = 32;
    internal const double TitleBottomSpacing = 4;
    internal const double TitleMinimumWidth = 120;
    internal const double TitleEditorHeight = 28;
    // Reserved space for the close button docked at the title row's right.
    internal const double TitleTrailingButtonWidth = 30;

    private const double WorkAreaMargin = 40;
    private const double HorizontalPadding = SurfacePadding * 2;
    // Surface padding plus the enlarged title row and its bottom gap.
    private const double BaseChromeHeight =
        (SurfacePadding * 2) + TitleHeight + TitleBottomSpacing;
    private const double IconHorizontalSpacing = 8;
    private const double IconVerticalSpacing = 4;

    public static StackPopoverLayout Calculate(
        bool isListMode,
        int itemCount,
        double widgetWidth,
        double workAreaWidth,
        double workAreaHeight,
        double itemWidth,
        double itemHeight,
        string? layoutMode = null)
    {
        int count = Math.Max(1, itemCount);
        double availableWidth = Math.Max(180, workAreaWidth - WorkAreaMargin);
        double availableHeight = Math.Max(160, workAreaHeight - WorkAreaMargin);
        double chromeHeight = BaseChromeHeight;
        int? fixedColumns = ResolveFixedColumns(layoutMode);
        int? fixedVisibleRows = ResolveFixedVisibleRows(layoutMode);

        return isListMode
            ? CalculateList(
                count,
                widgetWidth,
                availableWidth,
                availableHeight,
                itemHeight,
                chromeHeight,
                fixedVisibleRows)
            : CalculateIcons(
                count,
                widgetWidth,
                availableWidth,
                availableHeight,
                itemWidth,
                itemHeight,
                chromeHeight,
                fixedColumns,
                fixedVisibleRows);
    }

    internal static int? ResolveFixedColumns(string? layoutMode) =>
        layoutMode == SettingsService.FileStackPopoverLayoutGrid3
            ? 3
            : layoutMode == SettingsService.FileStackPopoverLayoutGrid5
                ? 5
                : null;

    internal static int? ResolveFixedVisibleRows(string? layoutMode) =>
        layoutMode == SettingsService.FileStackPopoverLayoutGrid3
            ? 3
            : layoutMode == SettingsService.FileStackPopoverLayoutGrid5
                ? 5
                : null;

    private static StackPopoverLayout CalculateIcons(
        int count,
        double widgetWidth,
        double availableWidth,
        double availableHeight,
        double itemWidth,
        double itemHeight,
        double chromeHeight,
        int? fixedColumns = null,
        int? fixedVisibleRows = null)
    {
        double cellWidth =
            Math.Clamp(itemWidth, 64, 196) + IconHorizontalSpacing;
        double cellHeight =
            Math.Clamp(itemHeight, 56, 212) + IconVerticalSpacing;
        double maximumWidth = Math.Min(720, availableWidth);
        double minimumWidth = Math.Min(184, maximumWidth);
        int maximumColumns = Math.Clamp(
            (int)Math.Floor(
                Math.Max(cellWidth, maximumWidth - HorizontalPadding) /
                cellWidth),
            1,
            6);
        int contentColumns = count switch
        {
            <= 3 => count,
            4 => 2,
            <= 9 => 3,
            <= 16 => 4,
            <= 25 => 5,
            _ => 6
        };
        int columns = fixedColumns is { } fixedColumnCount
            ? Math.Min(count, Math.Min(fixedColumnCount, maximumColumns))
            : Math.Min(
                count,
                Math.Clamp(contentColumns, 1, maximumColumns));

        int totalRows = (int)Math.Ceiling(count / (double)columns);
        int desiredRows = fixedVisibleRows is { } fixedRowCount
            ? fixedRowCount
            : Math.Min(5, totalRows);
        double maximumHeight = Math.Min(720, availableHeight);
        int rowsThatFit = Math.Max(
            1,
            (int)Math.Floor(
                Math.Max(cellHeight, maximumHeight - chromeHeight) /
                cellHeight));
        int visibleRows = Math.Min(desiredRows, rowsThatFit);
        double itemsWidth = columns * cellWidth;
        double width = ClampToAvailable(
            Math.Max(minimumWidth, itemsWidth + HorizontalPadding),
            minimumWidth,
            maximumWidth);
        itemsWidth = Math.Min(itemsWidth, Math.Max(cellWidth, width - HorizontalPadding));
        double itemsHeight = visibleRows * cellHeight;
        double height = Math.Min(
            maximumHeight,
            chromeHeight + itemsHeight);

        return new StackPopoverLayout(
            width,
            height,
            itemsWidth,
            itemsHeight,
            cellWidth,
            cellHeight,
            columns,
            visibleRows,
            totalRows > visibleRows);
    }

    private static StackPopoverLayout CalculateList(
        int count,
        double widgetWidth,
        double availableWidth,
        double availableHeight,
        double itemHeight,
        double chromeHeight,
        int? fixedVisibleRows = null)
    {
        double rowHeight = Math.Clamp(itemHeight, 40, 96);
        double maximumWidth = Math.Min(560, availableWidth);
        double minimumWidth = Math.Min(280, maximumWidth);
        double width = ClampToAvailable(
            Math.Max(
                count <= 4 ? 300 : count <= 8 ? 340 : 380,
                Math.Min(widgetWidth, 520)),
            minimumWidth,
            maximumWidth);
        double maximumHeight = Math.Min(720, availableHeight);
        int rowsThatFit = Math.Max(
            1,
            (int)Math.Floor(
                Math.Max(rowHeight, maximumHeight - chromeHeight) /
                rowHeight));
        int desiredRows = fixedVisibleRows is { } fixedRowCount
            ? fixedRowCount
            : Math.Min(8, count);
        int visibleRows = Math.Min(desiredRows, rowsThatFit);
        double itemsWidth = Math.Max(1, width - HorizontalPadding);
        double itemsHeight = visibleRows * rowHeight;
        double height = Math.Min(
            maximumHeight,
            chromeHeight + itemsHeight);

        return new StackPopoverLayout(
            width,
            height,
            itemsWidth,
            itemsHeight,
            itemsWidth,
            rowHeight,
            1,
            visibleRows,
            count > visibleRows);
    }

    private static double ClampToAvailable(
        double value,
        double minimum,
        double maximum) =>
        maximum <= minimum
            ? maximum
            : Math.Clamp(value, minimum, maximum);
}
