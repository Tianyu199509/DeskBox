using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI.Windowing;
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
    private const int PrimaryOriginTolerance = 96;

    private readonly record struct AvailableMonitorWorkArea(RectInt32 WorkArea, string? DeviceName, bool IsPrimary);

    public static RectInt32 ResolveBounds(WidgetConfig config, RectInt32 workArea)
    {
        return ResolveBounds(config, workArea, []);
    }

    public static RectInt32 ResolveBounds(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<RectInt32> availableWorkAreas)
    {
        return ResolveBoundsCore(
            config,
            fallbackWorkArea,
            availableWorkAreas.Select(workArea => new AvailableMonitorWorkArea(workArea, null, false)).ToList());
    }

    internal static RectInt32 ResolveBoundsForTest(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<(RectInt32 WorkArea, string? DeviceName)> availableWorkAreas,
        Func<RectInt32, double> dpiScaleProvider)
    {
        return ResolveBoundsCore(
            config,
            fallbackWorkArea,
            availableWorkAreas
                .Select(area => new AvailableMonitorWorkArea(area.WorkArea, area.DeviceName, false))
                .ToList(),
            dpiScaleProvider);
    }

    internal static RectInt32 ResolveBoundsForTestWithPrimary(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<(RectInt32 WorkArea, string? DeviceName, bool IsPrimary)> availableWorkAreas,
        Func<RectInt32, double> dpiScaleProvider)
    {
        return ResolveBoundsCore(
            config,
            fallbackWorkArea,
            availableWorkAreas
                .Select(area => new AvailableMonitorWorkArea(area.WorkArea, area.DeviceName, area.IsPrimary))
                .ToList(),
            dpiScaleProvider);
    }

    private static RectInt32 ResolveBoundsCore(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<AvailableMonitorWorkArea> availableWorkAreas,
        Func<RectInt32, double>? dpiScaleProvider = null)
    {
        var workArea = SelectWorkAreaCore(config, fallbackWorkArea, availableWorkAreas);
        double scale = GetDpiScale(workArea, dpiScaleProvider);
        int width = ResolvePhysicalWidth(config, scale);
        int height = ResolvePhysicalHeight(config, scale);
        int x = (int)Math.Round(config.X);
        int y = (int)Math.Round(config.Y);

        if (HasValidAnchor(config))
        {
            x = ResolveAnchoredX(config, workArea, width, scale);
            y = ResolveAnchoredY(config, workArea, height, scale);
        }

        return EnsureVisible(new RectInt32(x, y, width, height), workArea);
    }

    public static RectInt32 ResolveBoundsForCurrentTopology(WidgetConfig config)
    {
        int x = (int)Math.Round(double.IsFinite(config.X) ? config.X : 100);
        int y = (int)Math.Round(double.IsFinite(config.Y) ? config.Y : 100);
        var fallbackWorkArea = DisplayArea.GetFromPoint(
            new PointInt32(x, y),
            DisplayAreaFallback.Nearest).WorkArea;

        return ResolveBoundsCore(config, fallbackWorkArea, GetAvailableMonitorWorkAreas());
    }

    public static bool EnsureCurrentBoundsCoordinateVersion(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<RectInt32> availableWorkAreas)
    {
        return EnsureCurrentBoundsCoordinateVersionCore(
            config,
            fallbackWorkArea,
            availableWorkAreas.Select(workArea => new AvailableMonitorWorkArea(workArea, null, false)).ToList());
    }

    public static bool EnsureCurrentBoundsCoordinateVersionForCurrentTopology(
        WidgetConfig config,
        RectInt32 fallbackWorkArea)
    {
        return EnsureCurrentBoundsCoordinateVersionCore(
            config,
            fallbackWorkArea,
            GetAvailableMonitorWorkAreas());
    }

    internal static bool EnsureCurrentBoundsCoordinateVersionForTest(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<(RectInt32 WorkArea, string? DeviceName)> availableWorkAreas,
        Func<RectInt32, double> dpiScaleProvider)
    {
        return EnsureCurrentBoundsCoordinateVersionCore(
            config,
            fallbackWorkArea,
            availableWorkAreas
                .Select(area => new AvailableMonitorWorkArea(area.WorkArea, area.DeviceName, false))
                .ToList(),
            dpiScaleProvider);
    }

    internal static bool EnsureCurrentBoundsCoordinateVersionForTestWithPrimary(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<(RectInt32 WorkArea, string? DeviceName, bool IsPrimary)> availableWorkAreas,
        Func<RectInt32, double> dpiScaleProvider)
    {
        return EnsureCurrentBoundsCoordinateVersionCore(
            config,
            fallbackWorkArea,
            availableWorkAreas
                .Select(area => new AvailableMonitorWorkArea(area.WorkArea, area.DeviceName, area.IsPrimary))
                .ToList(),
            dpiScaleProvider);
    }

    private static bool EnsureCurrentBoundsCoordinateVersionCore(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<AvailableMonitorWorkArea> availableWorkAreas,
        Func<RectInt32, double>? dpiScaleProvider = null)
    {
        if (UsesLogicalBounds(config))
        {
            return false;
        }

        var workArea = SelectWorkAreaCore(
            config,
            fallbackWorkArea,
            availableWorkAreas);
        var legacyBounds = ResolveLegacyPhysicalBounds(config, workArea);
        double scale = GetDpiScale(workArea, dpiScaleProvider);

        config.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
        config.X = legacyBounds.X;
        config.Y = legacyBounds.Y;
        config.Width = ToLogicalPixels(legacyBounds.Width, scale);
        config.Height = ToLogicalPixels(legacyBounds.Height, scale);
        CaptureAnchorCore(config, legacyBounds, workArea, dpiScaleProvider, availableWorkAreas);
        return true;
    }

    public static void CaptureAnchor(WidgetConfig config, RectInt32 bounds, RectInt32 workArea)
    {
        CaptureAnchorCore(config, bounds, workArea);
    }

    private static void CaptureAnchorCore(
        WidgetConfig config,
        RectInt32 bounds,
        RectInt32 workArea,
        Func<RectInt32, double>? dpiScaleProvider = null,
        IReadOnlyList<AvailableMonitorWorkArea>? availableWorkAreas = null)
    {
        double scale = UsesLogicalBounds(config) ? GetDpiScale(workArea, dpiScaleProvider) : 1.0;
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
        config.PositionMarginX = ToLogicalPixels(Math.Max(0, anchorRight ? rightMargin : leftMargin), scale);
        config.PositionMarginY = ToLogicalPixels(Math.Max(0, anchorBottom ? bottomMargin : topMargin), scale);
        config.PositionMonitorKey = CreateMonitorKey(workArea);
        var monitor = FindMonitorForWorkArea(workArea, availableWorkAreas);
        config.PositionMonitorDeviceName = monitor?.DeviceName;
        config.PositionMonitorWasPrimary = monitor?.IsPrimary;
    }

    public static void UpdateConfigFromPhysicalBounds(WidgetConfig config, RectInt32 bounds, RectInt32 workArea)
    {
        UpdateConfigFromPhysicalBoundsCore(config, bounds, workArea);
    }

    internal static void UpdateConfigFromPhysicalBoundsForTest(
        WidgetConfig config,
        RectInt32 bounds,
        RectInt32 workArea,
        Func<RectInt32, double> dpiScaleProvider)
    {
        UpdateConfigFromPhysicalBoundsCore(config, bounds, workArea, dpiScaleProvider);
    }

    private static void UpdateConfigFromPhysicalBoundsCore(
        WidgetConfig config,
        RectInt32 bounds,
        RectInt32 workArea,
        Func<RectInt32, double>? dpiScaleProvider = null)
    {
        double scale = GetDpiScale(workArea, dpiScaleProvider);
        config.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
        config.X = bounds.X;
        config.Y = bounds.Y;
        config.Width = ToLogicalPixels(bounds.Width, scale);
        config.Height = ToLogicalPixels(bounds.Height, scale);
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
        return GetAvailableMonitorWorkAreas()
            .Select(area => area.WorkArea)
            .ToList();
    }

    private static IReadOnlyList<AvailableMonitorWorkArea> GetAvailableMonitorWorkAreas()
    {
        return Win32Helper.GetMonitorWorkAreaInfos()
            .Select(area => new AvailableMonitorWorkArea(
                new RectInt32(
                    area.WorkArea.Left,
                    area.WorkArea.Top,
                    area.WorkArea.Right - area.WorkArea.Left,
                    area.WorkArea.Bottom - area.WorkArea.Top),
                string.IsNullOrWhiteSpace(area.DeviceName) ? null : area.DeviceName,
                area.IsPrimary))
            .ToList();
    }

    public static RectInt32 SelectWorkArea(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<RectInt32> availableWorkAreas)
    {
        return SelectWorkAreaCore(
            config,
            fallbackWorkArea,
            availableWorkAreas.Select(workArea => new AvailableMonitorWorkArea(workArea, null, false)).ToList());
    }

    private static RectInt32 SelectWorkAreaCore(
        WidgetConfig config,
        RectInt32 fallbackWorkArea,
        IReadOnlyList<AvailableMonitorWorkArea> availableWorkAreas)
    {
        var primaryWorkArea = SelectPrimaryWorkAreaForSmartMode(config, availableWorkAreas);
        if (primaryWorkArea.HasValue)
        {
            return primaryWorkArea.Value;
        }

        if (!string.IsNullOrWhiteSpace(config.PositionMonitorDeviceName))
        {
            foreach (var area in availableWorkAreas)
            {
                if (string.Equals(area.DeviceName, config.PositionMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
                {
                    return area.WorkArea;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(config.PositionMonitorKey))
        {
            foreach (var area in availableWorkAreas)
            {
                if (string.Equals(CreateMonitorKey(area.WorkArea), config.PositionMonitorKey, StringComparison.Ordinal))
                {
                    return area.WorkArea;
                }
            }

            if (TryParseMonitorKey(config.PositionMonitorKey, out var savedMonitor))
            {
                AvailableMonitorWorkArea? bestMatch = null;
                long bestDistance = long.MaxValue;
                foreach (var area in availableWorkAreas)
                {
                    if (area.WorkArea.Width == savedMonitor.Width && area.WorkArea.Height == savedMonitor.Height)
                    {
                        long dx = area.WorkArea.X - savedMonitor.X;
                        long dy = area.WorkArea.Y - savedMonitor.Y;
                        long distance = (dx * dx) + (dy * dy);
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestMatch = area;
                        }
                    }
                }

                if (bestMatch.HasValue)
                {
                    return bestMatch.Value.WorkArea;
                }
            }
        }

        return fallbackWorkArea;
    }

    private static RectInt32? SelectPrimaryWorkAreaForSmartMode(
        WidgetConfig config,
        IReadOnlyList<AvailableMonitorWorkArea> availableWorkAreas)
    {
        if (!ShouldFollowPrimaryMonitor(config))
        {
            return null;
        }

        foreach (var area in availableWorkAreas)
        {
            if (area.IsPrimary)
            {
                return area.WorkArea;
            }
        }

        return null;
    }

    private static bool ShouldFollowPrimaryMonitor(WidgetConfig config)
    {
        if (config.PositionMonitorWasPrimary.HasValue)
        {
            return config.PositionMonitorWasPrimary.Value;
        }

        return SavedMonitorLooksLikePrimary(config);
    }

    private static bool SavedMonitorLooksLikePrimary(WidgetConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.PositionMonitorKey) ||
            !TryParseMonitorKey(config.PositionMonitorKey, out var savedMonitor))
        {
            return false;
        }

        return savedMonitor.X >= 0 &&
               savedMonitor.X < PrimaryOriginTolerance &&
               savedMonitor.Y >= 0 &&
               savedMonitor.Y < PrimaryOriginTolerance;
    }

    private static AvailableMonitorWorkArea? FindMonitorForWorkArea(
        RectInt32 workArea,
        IReadOnlyList<AvailableMonitorWorkArea>? availableWorkAreas = null)
    {
        foreach (var area in availableWorkAreas ?? GetAvailableMonitorWorkAreas())
        {
            if (area.WorkArea.X == workArea.X &&
                area.WorkArea.Y == workArea.Y &&
                area.WorkArea.Width == workArea.Width &&
                area.WorkArea.Height == workArea.Height)
            {
                return area;
            }
        }

        return null;
    }

    private static bool HasValidAnchor(WidgetConfig config)
    {
        return config.PositionAnchor is
            WidgetPositionAnchors.LeftTop or
            WidgetPositionAnchors.RightTop or
            WidgetPositionAnchors.LeftBottom or
            WidgetPositionAnchors.RightBottom;
    }

    public static double GetDpiScale(RectInt32 workArea)
    {
        return GetDpiScale(workArea, null);
    }

    private static double GetDpiScale(RectInt32 workArea, Func<RectInt32, double>? dpiScaleProvider)
    {
        int x = workArea.X + Math.Max(0, workArea.Width / 2);
        int y = workArea.Y + Math.Max(0, workArea.Height / 2);
        double scale = dpiScaleProvider?.Invoke(workArea) ?? Win32Helper.GetDpiScaleForPoint(x, y);
        return double.IsFinite(scale) && scale > 0
            ? scale
            : 1.0;
    }

    public static SizeInt32 GetPhysicalMinimumSize(RectInt32 workArea)
    {
        double scale = GetDpiScale(workArea);
        return new SizeInt32(
            ToPhysicalPixels(SettingsService.MinWidgetWidth, scale),
            ToPhysicalPixels(SettingsService.MinWidgetHeight, scale));
    }

    public static SizeInt32 GetPhysicalMinimumSizeForBounds(RectInt32 bounds)
    {
        var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
        return GetPhysicalMinimumSize(workArea);
    }

    public static int ToPhysicalPixels(double logicalPixels, double scale)
    {
        double normalized = double.IsFinite(logicalPixels) ? logicalPixels : 0;
        double normalizedScale = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
        return Math.Max(1, (int)Math.Round(normalized * normalizedScale, MidpointRounding.AwayFromZero));
    }

    public static double ToLogicalPixels(double physicalPixels, double scale)
    {
        double normalized = double.IsFinite(physicalPixels) ? physicalPixels : 0;
        double normalizedScale = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
        return normalized / normalizedScale;
    }

    private static bool UsesLogicalBounds(WidgetConfig config)
    {
        return config.BoundsCoordinateVersion >= WidgetConfig.CurrentBoundsCoordinateVersion;
    }

    private static int ResolvePhysicalWidth(WidgetConfig config, double scale)
    {
        double width = double.IsFinite(config.Width)
            ? Math.Max(SettingsService.MinWidgetWidth, config.Width)
            : SettingsService.DefaultWidgetWidth;
        return UsesLogicalBounds(config)
            ? ToPhysicalPixels(width, scale)
            : (int)Math.Round(width);
    }

    private static int ResolvePhysicalHeight(WidgetConfig config, double scale)
    {
        double height = double.IsFinite(config.Height)
            ? Math.Max(SettingsService.MinWidgetHeight, config.Height)
            : SettingsService.DefaultWidgetHeight;
        return UsesLogicalBounds(config)
            ? ToPhysicalPixels(height, scale)
            : (int)Math.Round(height);
    }

    private static RectInt32 ResolveLegacyPhysicalBounds(WidgetConfig config, RectInt32 workArea)
    {
        int width = ResolvePhysicalWidth(config, 1.0);
        int height = ResolvePhysicalHeight(config, 1.0);
        int x = (int)Math.Round(config.X);
        int y = (int)Math.Round(config.Y);

        if (HasValidAnchor(config))
        {
            x = ResolveLegacyAnchoredX(config, workArea, width);
            y = ResolveLegacyAnchoredY(config, workArea, height);
        }

        return EnsureVisible(new RectInt32(x, y, width, height), workArea);
    }

    private static int ResolveAnchoredX(WidgetConfig config, RectInt32 workArea, int width, double scale)
    {
        int margin = UsesLogicalBounds(config)
            ? ToPhysicalMargin(config.PositionMarginX, scale)
            : NormalizeMargin(config.PositionMarginX);
        return config.PositionAnchor is WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom
            ? workArea.X + workArea.Width - width - margin
            : workArea.X + margin;
    }

    private static int ResolveAnchoredY(WidgetConfig config, RectInt32 workArea, int height, double scale)
    {
        int margin = UsesLogicalBounds(config)
            ? ToPhysicalMargin(config.PositionMarginY, scale)
            : NormalizeMargin(config.PositionMarginY);
        return config.PositionAnchor is WidgetPositionAnchors.LeftBottom or WidgetPositionAnchors.RightBottom
            ? workArea.Y + workArea.Height - height - margin
            : workArea.Y + margin;
    }

    private static int ResolveLegacyAnchoredX(WidgetConfig config, RectInt32 workArea, int width)
    {
        int margin = NormalizeMargin(config.PositionMarginX);
        return config.PositionAnchor is WidgetPositionAnchors.RightTop or WidgetPositionAnchors.RightBottom
            ? workArea.X + workArea.Width - width - margin
            : workArea.X + margin;
    }

    private static int ResolveLegacyAnchoredY(WidgetConfig config, RectInt32 workArea, int height)
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

    private static int ToPhysicalMargin(double logicalPixels, double scale)
    {
        double normalized = double.IsFinite(logicalPixels) ? Math.Max(0, logicalPixels) : 0;
        double normalizedScale = double.IsFinite(scale) && scale > 0 ? scale : 1.0;
        return Math.Max(0, (int)Math.Round(normalized * normalizedScale, MidpointRounding.AwayFromZero));
    }

    private static bool TryParseMonitorKey(string value, out RectInt32 workArea)
    {
        workArea = default;
        var parts = value.Split(':');
        if (parts.Length != 4 ||
            !int.TryParse(parts[0], out int x) ||
            !int.TryParse(parts[1], out int y) ||
            !int.TryParse(parts[2], out int width) ||
            !int.TryParse(parts[3], out int height))
        {
            return false;
        }

        workArea = new RectInt32(x, y, width, height);
        return width > 0 && height > 0;
    }
}
