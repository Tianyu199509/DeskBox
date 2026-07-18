using Windows.Graphics;

namespace DeskBox.Services;

public readonly record struct WidgetCapsuleArrangementItem(
    string Id,
    int Width,
    int Height);

public static class WidgetCapsuleArrangementCalculator
{
    private const int HorizontalMinimumWidth = 96;
    private const int VerticalMinimumHeight = 36;

    public static IReadOnlyDictionary<string, RectInt32> Calculate(
        IReadOnlyList<WidgetCapsuleArrangementItem> items,
        RectInt32 workArea,
        PointInt32 anchorPoint,
        string? positionAnchor,
        string? direction,
        int spacing)
    {
        var results = new Dictionary<string, RectInt32>(StringComparer.Ordinal);
        if (items.Count == 0 || workArea.Width <= 0 || workArea.Height <= 0)
        {
            return results;
        }

        bool vertical = SettingsService.NormalizeWidgetCapsuleBarDirection(direction) ==
            SettingsService.WidgetCapsuleBarDirectionVertical;
        bool anchorRight = positionAnchor is
            WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom;
        bool anchorBottom = positionAnchor is
            WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom;
        int workRight = workArea.X + workArea.Width;
        int workBottom = workArea.Y + workArea.Height;
        var safeAnchor = new PointInt32(
            Math.Clamp(anchorPoint.X, workArea.X, workRight),
            Math.Clamp(anchorPoint.Y, workArea.Y, workBottom));

        if (vertical)
        {
            ArrangeVerticalSingleColumn(
                items,
                workArea,
                safeAnchor,
                anchorRight,
                anchorBottom,
                spacing,
                results);
        }
        else
        {
            ArrangeHorizontalSingleRow(
                items,
                workArea,
                safeAnchor,
                anchorRight,
                anchorBottom,
                spacing,
                results);
        }

        ClampGroupIntoWorkArea(results, workArea);
        return results;
    }

    private static void ArrangeHorizontalSingleRow(
        IReadOnlyList<WidgetCapsuleArrangementItem> items,
        RectInt32 workArea,
        PointInt32 anchorPoint,
        bool anchorRight,
        bool anchorBottom,
        int spacing,
        IDictionary<string, RectInt32> results)
    {
        int commonHeight = Math.Min(
            workArea.Height,
            items.Max(item => Math.Max(1, item.Height)));
        (int safeSpacing, int[] widths) = FitPrimarySizes(
            items.Select(item => item.Width).ToArray(),
            workArea.Width,
            spacing,
            HorizontalMinimumWidth);
        int cursorX = anchorPoint.X;

        for (int index = 0; index < items.Count; index++)
        {
            WidgetCapsuleArrangementItem item = items[index];
            int width = widths[index];
            int x = anchorRight ? cursorX - width : cursorX;
            int y = anchorBottom ? anchorPoint.Y - commonHeight : anchorPoint.Y;
            results[item.Id] = new RectInt32(x, y, width, commonHeight);
            cursorX = anchorRight
                ? x - safeSpacing
                : x + width + safeSpacing;
        }
    }

    private static void ArrangeVerticalSingleColumn(
        IReadOnlyList<WidgetCapsuleArrangementItem> items,
        RectInt32 workArea,
        PointInt32 anchorPoint,
        bool anchorRight,
        bool anchorBottom,
        int spacing,
        IDictionary<string, RectInt32> results)
    {
        int commonWidth = Math.Min(
            workArea.Width,
            items.Max(item => Math.Max(1, item.Width)));
        (int safeSpacing, int[] heights) = FitPrimarySizes(
            items.Select(item => item.Height).ToArray(),
            workArea.Height,
            spacing,
            VerticalMinimumHeight);
        int cursorY = anchorPoint.Y;

        for (int index = 0; index < items.Count; index++)
        {
            WidgetCapsuleArrangementItem item = items[index];
            int height = heights[index];
            int x = anchorRight ? anchorPoint.X - commonWidth : anchorPoint.X;
            int y = anchorBottom ? cursorY - height : cursorY;
            results[item.Id] = new RectInt32(x, y, commonWidth, height);
            cursorY = anchorBottom
                ? y - safeSpacing
                : y + height + safeSpacing;
        }
    }

    private static (int Spacing, int[] Sizes) FitPrimarySizes(
        IReadOnlyList<int> requestedSizes,
        int availableLength,
        int requestedSpacing,
        int preferredMinimum)
    {
        int count = requestedSizes.Count;
        int safeAvailable = Math.Max(1, availableLength);
        int safeSpacing = Math.Max(0, requestedSpacing);
        if (count > 1)
        {
            safeSpacing = Math.Min(safeSpacing, safeAvailable / (count - 1));
        }

        int[] sizes = requestedSizes
            .Select(size => Math.Max(1, size))
            .ToArray();
        int availableForItems = Math.Max(1, safeAvailable - safeSpacing * Math.Max(0, count - 1));
        if (sizes.Sum() <= availableForItems)
        {
            return (safeSpacing, sizes);
        }

        int effectiveMinimum = Math.Min(
            preferredMinimum,
            Math.Max(1, availableForItems / count));
        double scale = availableForItems / (double)Math.Max(1, sizes.Sum());
        for (int index = 0; index < sizes.Length; index++)
        {
            sizes[index] = Math.Max(effectiveMinimum, (int)Math.Floor(sizes[index] * scale));
        }

        int excess = sizes.Sum() - availableForItems;
        while (excess > 0)
        {
            bool reduced = false;
            for (int index = sizes.Length - 1; index >= 0 && excess > 0; index--)
            {
                if (sizes[index] <= 1)
                {
                    continue;
                }

                sizes[index]--;
                excess--;
                reduced = true;
            }

            if (!reduced)
            {
                break;
            }
        }

        int remaining = availableForItems - sizes.Sum();
        for (int index = 0; remaining > 0; index = (index + 1) % sizes.Length)
        {
            sizes[index]++;
            remaining--;
        }

        return (safeSpacing, sizes);
    }

    private static void ClampGroupIntoWorkArea(
        IDictionary<string, RectInt32> results,
        RectInt32 workArea)
    {
        int minX = results.Values.Min(bounds => bounds.X);
        int minY = results.Values.Min(bounds => bounds.Y);
        int maxX = results.Values.Max(bounds => bounds.X + bounds.Width);
        int maxY = results.Values.Max(bounds => bounds.Y + bounds.Height);
        int workRight = workArea.X + workArea.Width;
        int workBottom = workArea.Y + workArea.Height;
        int offsetX = minX < workArea.X
            ? workArea.X - minX
            : maxX > workRight
                ? workRight - maxX
                : 0;
        int offsetY = minY < workArea.Y
            ? workArea.Y - minY
            : maxY > workBottom
                ? workBottom - maxY
                : 0;
        if (offsetX == 0 && offsetY == 0)
        {
            return;
        }

        foreach (string id in results.Keys.ToList())
        {
            RectInt32 bounds = results[id];
            results[id] = new RectInt32(
                bounds.X + offsetX,
                bounds.Y + offsetY,
                bounds.Width,
                bounds.Height);
        }
    }
}
