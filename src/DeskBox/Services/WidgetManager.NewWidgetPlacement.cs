using DeskBox.Models;
using DeskBox.Platform;
using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// Context for placing a brand-new widget through the unified funnel
/// (spec 5.8). Explicit bounds (organizer-planned rects) win; otherwise the
/// source surface's display (created from inside another widget); otherwise
/// the "新格子出现在" setting.
/// </summary>
public sealed record NewWidgetPlacementContext(
    RectInt32? ExplicitBounds = null,
    string? SourceSurfaceId = null);

public sealed partial class WidgetManager
{
    /// <summary>
    /// Applies the new-widget placement funnel (spec 5.8) BEFORE the config
    /// joins <c>Settings.Widgets</c>: resolves the target display, places the
    /// widget right-aligned with cascade offsets, and sets its home to the
    /// creation display ("放哪属于哪"). Only for brand-new configs —
    /// re-enabling an existing feature widget keeps its stored position.
    /// </summary>
    public void ApplyNewWidgetPlacement(WidgetConfig config, NewWidgetPlacementContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(config);

        context ??= new NewWidgetPlacementContext();
        IReadOnlyList<WidgetScreenInfo> screens = WidgetScreenCatalog.Capture();

        // 1. Organizer-planned rect.
        if (context.ExplicitBounds is { } planned && screens.Count > 0)
        {
            WidgetScreenInfo display = FindDisplayByMaxIntersection(screens, planned);
            var workArea = ToRect(display.WorkArea);
            WidgetPositioningService.CaptureAnchor(config, planned, workArea);
            WidgetPositioningService.UpdateConfigFromPhysicalBounds(config, planned, workArea);
            ApplyNewWidgetHome(config, display);
            return;
        }

        WidgetScreenInfo? target = null;

        // 2. Created from inside another widget surface.
        if (!string.IsNullOrWhiteSpace(context.SourceSurfaceId) &&
            FindConfig(context.SourceSurfaceId) is { } source &&
            screens.Count > 0)
        {
            target = FindDisplayByMaxIntersection(
                screens,
                new RectInt32(
                    (int)Math.Round(source.X),
                    (int)Math.Round(source.Y),
                    Math.Max(1, (int)Math.Round(source.Width)),
                    Math.Max(1, (int)Math.Round(source.Height))));
        }

        // 3. The "新格子出现在" setting.
        string setting = SettingsService.NormalizeWidgetNewPlacementTarget(
            _settingsService.Settings.WidgetNewPlacementTarget);
        if (target is null)
        {
            target = setting switch
            {
                SettingsService.WidgetNewPlacementMainDisplay =>
                    screens.FirstOrDefault(screen => screen.IsPrimary) ?? screens.FirstOrDefault(),
                SettingsService.WidgetNewPlacementSpecificDisplay =>
                    WidgetScreenCatalog.TryFindScreen(
                        screens,
                        _settingsService.Settings.WidgetDefaultBoundScreenId),
                _ => CursorScreen(screens)
            };
        }

        // 4. Nothing usable — keep the deferred initial-placement path.
        if (target is null || screens.Count == 0)
        {
            config.NeedsInitialPlacement = true;
            return;
        }

        var targetWorkArea = ToRect(target.WorkArea);
        double scale = target.EffectiveDpiScale;
        RectInt32 bounds = InitialFileWidgetPlacementPolicy.CalculateRightAlignedBounds(
            targetWorkArea,
            config.Width,
            config.Height,
            scale);
        bounds = CascadeAwayFromExistingWidgets(config.Id, bounds, targetWorkArea, scale, target);
        WidgetPositioningService.CaptureAnchor(config, bounds, targetWorkArea);
        WidgetPositioningService.UpdateConfigFromPhysicalBounds(config, bounds, targetWorkArea);
        ApplyNewWidgetHome(config, target);
    }

    private void ApplyNewWidgetHome(WidgetConfig config, WidgetScreenInfo display)
    {
        if (Win32Helper.GetSystemMetrics(Win32Helper.SM_REMOTESESSION) != 0 ||
            DisplayPlacementResolver.IsDegenerateIdentity(display.StableId))
        {
            config.ScreenBindingMode = WidgetScreenBindingMode.Unbound;
            config.BoundScreenId = null;
            return;
        }

        config.ScreenBindingMode = WidgetScreenBindingMode.Pinned;
        config.BoundScreenId = display.StableId;
    }

    /// <summary>
    /// Nudges the default right-aligned spot per existing widget homed on the
    /// target display so a batch of new widgets cascades instead of stacking
    /// (bounded at 8 levels, spec 5.8).
    /// </summary>
    private RectInt32 CascadeAwayFromExistingWidgets(
        string newWidgetId,
        RectInt32 bounds,
        RectInt32 workArea,
        double scale,
        WidgetScreenInfo target)
    {
        int step = WidgetPositioningService.ToPhysicalPixels(24, scale);
        string stableId = target.StableId.Trim();
        int occupied = _settingsService.Settings.Widgets.Count(widget =>
            !widget.IsDisabled &&
            !string.Equals(widget.Id, newWidgetId, StringComparison.Ordinal) &&
            widget.ScreenBindingMode == WidgetScreenBindingMode.Pinned &&
            string.Equals(widget.BoundScreenId?.Trim(), stableId, StringComparison.OrdinalIgnoreCase));
        int offset = Math.Clamp(occupied, 0, 8) * step;
        if (offset == 0)
        {
            return bounds;
        }

        var moved = new RectInt32(
            Math.Max(workArea.X, bounds.X - offset),
            bounds.Y + offset,
            bounds.Width,
            bounds.Height);
        return WidgetPositioningService.EnsureVisible(moved, workArea);
    }

    private static WidgetScreenInfo? CursorScreen(IReadOnlyList<WidgetScreenInfo> screens)
    {
        if (screens.Count == 0)
        {
            return null;
        }

        PointInt32 cursor = new(0, 0);
        if (Win32Helper.GetCursorPos(out Win32Helper.POINT point))
        {
            cursor = new PointInt32(point.X, point.Y);
        }

        return WidgetScreenCatalog.FindScreenForPoint(screens, cursor) ?? screens.FirstOrDefault();
    }

    private static RectInt32 ToRect(RectInt32 area) => area;
}
