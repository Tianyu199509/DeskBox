using DeskBox.Helpers;
using DeskBox.Models;
using Windows.Graphics;

namespace DeskBox.Services;

public static class WidgetPositionAnchors
{
    public const string LeftTop = "LeftTop";
    public const string RightTop = "RightTop";
    public const string LeftBottom = "LeftBottom";
    public const string RightBottom = "RightBottom";
}

public static class WidgetPositioningService
{
    private const int MinimumVisibleExtent = 48;
    private const int FallbackOffset = 32;

    public static RectInt32 ResolveBounds(WidgetConfig config, RectInt32 workArea)
    {
        return ResolveBounds(config, workArea, []);
    }

    public static RectInt32 ResolveBounds(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<RectInt32> availableWorkAreas)
    {
        int width = (int)Math.Round(Math.Max(SettingsService.MinWidgetWidth, config.Width));
        int height = (int)Math.Round(Math.Max(SettingsService.MinWidgetHeight, config.Height));
        int x = (int)Math.Round(config.X);
        int y = (int)Math.Round(config.Y);
        var workArea = SelectWorkArea(config, fallbackWorkArea, availableWorkAreas);

        if (HasValidAnchor(config))
        {
            x = ResolveAnchoredX(config, workArea, width);
            y = ResolveAnchoredY(config, workArea, height);
        }

        return EnsureVisible(new RectInt32(x, y, width, height), workArea);
    }

    public static void CaptureAnchor(WidgetConfig config, RectInt32 bounds, RectInt32 workArea)
    {
        int leftMargin = bounds.X - workArea.X;
        int rightMargin = (workArea.X + workArea.Width) - (bounds.X + bounds.Width);
        int topMargin = bounds.Y - workArea.Y;
        int bottomMargin = (workArea.Y + workArea.Height) - (bounds.Y + bounds.Height);

        bool anchorRight = rightMargin < leftMargin;
        bool anchorBottom = bottomMargin < topMargin;

        config.PositionAnchor = (anchorRight, anchorBottom) switch
        {
            (true, true) => WidgetPositionAnchors.RightBottom,
            (true, false) => WidgetPositionAnchors.RightTop,
            (false, true) => WidgetPositionAnchors.LeftBottom,
            _ => WidgetPositionAnchors.LeftTop
        };
        config.PositionMarginX = Math.Max(0, anchorRight ? rightMargin : leftMargin);
        config.PositionMarginY = Math.Max(0, anchorBottom ? bottomMargin : topMargin);
        config.PositionMonitorKey = CreateMonitorKey(workArea);
    }

    public static RectInt32 EnsureVisible(RectInt32 bounds, RectInt32 workArea)
    {
        bool isWildlyOffscreen =
            bounds.X + bounds.Width < workArea.X + MinimumVisibleExtent ||
            bounds.Y + bounds.Height < workArea.Y + MinimumVisibleExtent ||
            bounds.X > workArea.X + workArea.Width - MinimumVisibleExtent ||
            bounds.Y > workArea.Y + workArea.Height - MinimumVisibleExtent;

        if (isWildlyOffscreen)
        {
            return new RectInt32(
                workArea.X + FallbackOffset,
                workArea.Y + FallbackOffset,
                bounds.Width,
                bounds.Height);
        }

        int maxX = Math.Max(workArea.X, workArea.X + workArea.Width - bounds.Width);
        int maxY = Math.Max(workArea.Y, workArea.Y + workArea.Height - bounds.Height);
        return new RectInt32(
            Math.Clamp(bounds.X, workArea.X, maxX),
            Math.Clamp(bounds.Y, workArea.Y, maxY),
            bounds.Width,
            bounds.Height);
    }

    public static string CreateMonitorKey(RectInt32 workArea)
    {
        return $"{workArea.X}:{workArea.Y}:{workArea.Width}:{workArea.Height}";
    }

    public static IReadOnlyList<RectInt32> GetAvailableWorkAreas()
    {
        return Win32Helper.GetMonitorWorkAreas()
            .Select(area => new RectInt32(
                area.WorkArea.Left,
                area.WorkArea.Top,
                area.WorkArea.Right - area.WorkArea.Left,
                area.WorkArea.Bottom - area.WorkArea.Top))
            .ToList();
    }

    public static RectInt32 SelectWorkArea(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<RectInt32> availableWorkAreas)
    {
        if (!string.IsNullOrWhiteSpace(config.PositionMonitorKey))
        {
            foreach (var workArea in availableWorkAreas)
            {
                if (string.Equals(CreateMonitorKey(workArea), config.PositionMonitorKey, StringComparison.Ordinal))
                {
                    return workArea;
                }
            }
        }

        return fallbackWorkArea;
    }

    private static bool HasValidAnchor(WidgetConfig config)
    {
        return config.PositionAnchor is
            WidgetPositionAnchors.LeftTop or
            WidgetPositionAnchors.RightTop or
            WidgetPositionAnchors.LeftBottom or
            WidgetPositionAnchors.RightBottom;
    }

    private static int ResolveAnchoredX(WidgetConfig config, RectInt32 workArea, int width)
    {
        int margin = NormalizeMargin(config.PositionMarginX);
        return config.PositionAnchor is WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom
            ? workArea.X + workArea.Width - width - margin
            : workArea.X + margin;
    }

    private static int ResolveAnchoredY(WidgetConfig config, RectInt32 workArea, int height)
    {
        int margin = NormalizeMargin(config.PositionMarginY);
        return config.PositionAnchor is WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom
            ? workArea.Y + workArea.Height - height - margin
            : workArea.Y + margin;
    }

    private static int NormalizeMargin(double value)
    {
        return double.IsFinite(value)
            ? (int)Math.Round(Math.Max(0, value))
            : 0;
    }
}
