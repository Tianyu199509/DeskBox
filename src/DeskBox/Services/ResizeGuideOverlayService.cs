using DeskBox.Helpers;
using DeskBox.Platform;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;
using Windows.Graphics;

namespace DeskBox.Services;

/// <summary>
/// Provides snap-to-edge alignment detection and visual edge highlights
/// during widget resize operations.  No overlay window is used — highlights
/// are drawn directly on each widget's own root Grid using Border elements,
/// avoiding all transparent-window rendering issues.
/// </summary>
public sealed class ResizeGuideOverlayService
{
    // ── Snap threshold & visual constants ───────────────────────────────

    private const double SnapEngageThresholdDips = 8.0;
    private const double SnapReleaseThresholdDips = 12.0;
    private const double HighlightBandDips = 10.0;      // DIPs – edge glow band width
    private const double HighlightSlideDips = 8.0;      // DIPs – entry slide-out distance
    private const float TipFadeMinFraction = 0.03f;
    private const float TipFadeMaxFraction = 0.45f;
    private const int HighlightZIndex = 100;
    private static readonly TimeSpan HighlightFadeInDuration = TimeSpan.FromMilliseconds(160);
    private static readonly TimeSpan HighlightFadeOutDuration = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan HighlightSlideDuration = TimeSpan.FromMilliseconds(260);

    // ── Active highlight elements (keyed by widget HWND + edge) ─────────

    private readonly Dictionary<(IntPtr Hwnd, SnapEdge Edge), Border> _activeHighlights = new();

    // ── Resize session state ─────────────────────────────────────────────

    private IntPtr _resizingWidgetHwnd;
    private FrameworkElement? _resizingWidgetRoot;
    private Windows.UI.Color _highlightColor;
    private readonly List<WidgetSnapTarget> _resizeSnapTargets = [];
    private readonly List<WidgetSnapTarget> _dragSnapTargets = [];
    private RectInt32? _resizeWorkAreaBounds;
    private int _sessionSnapSpacingPhysical;
    private int _sessionSnapEngageThresholdPhysical;
    private int _sessionSnapReleaseThresholdPhysical;
    private WidgetSnapMatch? _currentDragHorizontalMatch;
    private WidgetSnapMatch? _currentDragVerticalMatch;

    /// <summary>
    /// Whether a resize session is currently active.
    /// </summary>
    public bool IsActive { get; private set; }

    /// <summary>
    /// Whether snap-to-edge behaviour is enabled.  When false,
    /// <see cref="UpdateGuidesAndSnap"/> returns the proposed bounds
    /// unchanged and no highlights are shown.
    /// </summary>
    public bool IsSnapEnabled { get; set; } = true;

    /// <summary>
    /// Desired visual gap, in effective pixels, between two snapped widgets.
    /// </summary>
    public double SnapSpacingDips { get; set; } = SettingsService.DefaultWidgetSnapSpacing;

    // ─────────────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called when a widget resize operation begins.
    /// </summary>
    public void BeginResize(IntPtr resizingWidgetHwnd, FrameworkElement resizingWidgetRoot)
    {
        _resizingWidgetHwnd = resizingWidgetHwnd;
        _resizingWidgetRoot = resizingWidgetRoot;
        _highlightColor = GetHighlightColor();
        _resizeSnapTargets.Clear();
        _resizeSnapTargets.AddRange(GetOtherWidgetBounds(resizingWidgetHwnd));
        _resizeWorkAreaBounds = GetResizeWorkAreaBounds(resizingWidgetHwnd);
        ConfigureSessionSnapMetrics(resizingWidgetHwnd, resizingWidgetRoot);
        IsActive = true;

        App.LogVerbose($"[ResizeGuide] BeginResize hwnd=0x{resizingWidgetHwnd.ToInt64():X}");
    }

    /// <summary>
    /// Called on every PointerMoved during resize.  Checks the proposed bounds
    /// against all other widget edges and work-area edges, snaps if within
    /// threshold, and shows edge highlights on both the resizing widget and
    /// the nearest target widget.
    /// Returns the (possibly snapped) bounds to apply.
    /// </summary>
    public RectInt32 UpdateGuidesAndSnap(
        RectInt32 proposedBounds,
        string resizeDirection,
        int? minimumWidth = null,
        int? maximumWidth = null)
    {
        if (!IsActive || !IsSnapEnabled)
        {
            // Snap toggled off mid-session must still converge the bands.
            if (IsActive)
            {
                ClearAllHighlights();
            }

            return proposedBounds;
        }

        var snapped = proposedBounds;
        WidgetSnapMatch? horizontalMatch = null;
        WidgetSnapMatch? verticalMatch = null;

        // ── Horizontal edge snapping (Left / Right) ──────────────────────

        bool checkRight = resizeDirection.Contains("Right", StringComparison.Ordinal);
        bool checkLeft = resizeDirection.Contains("Left", StringComparison.Ordinal);

        if (checkRight || checkLeft)
        {
            WidgetSnapEdge sourceEdge = checkRight
                ? WidgetSnapEdge.Right
                : WidgetSnapEdge.Left;
            horizontalMatch = WidgetSnapCalculator.ResolveResizeEdge(
                proposedBounds,
                sourceEdge,
                _resizeSnapTargets,
                _resizeWorkAreaBounds,
                _sessionSnapSpacingPhysical,
                _sessionSnapEngageThresholdPhysical);
            if (horizontalMatch is { } match)
            {
                int snappedWidth = checkRight
                    ? match.Coordinate - snapped.X
                    : snapped.X + snapped.Width - match.Coordinate;
                bool widthAllowed = (!minimumWidth.HasValue || snappedWidth >= minimumWidth.Value) &&
                    (!maximumWidth.HasValue || snappedWidth <= maximumWidth.Value);
                if (!widthAllowed)
                {
                    horizontalMatch = null;
                }
            }

            if (horizontalMatch is { } matchToApply)
            {
                if (checkRight)
                {
                    snapped = new RectInt32(
                        snapped.X, snapped.Y,
                        matchToApply.Coordinate - snapped.X,
                        snapped.Height);
                }
                else
                {
                    int rightEdge = snapped.X + snapped.Width;
                    snapped = new RectInt32(
                        matchToApply.Coordinate, snapped.Y,
                        rightEdge - matchToApply.Coordinate,
                        snapped.Height);
                }
            }
        }

        // ── Vertical edge snapping (Top / Bottom) ────────────────────────

        bool checkBottom = resizeDirection.Contains("Bottom", StringComparison.Ordinal);
        bool checkTop = resizeDirection.Contains("Top", StringComparison.Ordinal);

        if (checkBottom || checkTop)
        {
            WidgetSnapEdge sourceEdge = checkBottom
                ? WidgetSnapEdge.Bottom
                : WidgetSnapEdge.Top;
            verticalMatch = WidgetSnapCalculator.ResolveResizeEdge(
                proposedBounds,
                sourceEdge,
                _resizeSnapTargets,
                _resizeWorkAreaBounds,
                _sessionSnapSpacingPhysical,
                _sessionSnapEngageThresholdPhysical);
            if (verticalMatch is { } matchToApply)
            {
                if (checkBottom)
                {
                    snapped = new RectInt32(
                        snapped.X, snapped.Y,
                        snapped.Width,
                        matchToApply.Coordinate - snapped.Y);
                }
                else
                {
                    int bottomEdge = snapped.Y + snapped.Height;
                    snapped = new RectInt32(
                        snapped.X, matchToApply.Coordinate,
                        snapped.Width,
                        bottomEdge - matchToApply.Coordinate);
                }
            }
        }

        // ── Update highlights ────────────────────────────────────────────

        var desired = new HashSet<(IntPtr Hwnd, SnapEdge Edge)>();
        // A corner resize can snap on both axes — highlight both.
        AddMatchHighlights(desired, _resizingWidgetHwnd, horizontalMatch);
        AddMatchHighlights(desired, _resizingWidgetHwnd, verticalMatch);
        SyncHighlights(desired);

        return snapped;
    }

    /// <summary>
    /// Called when the resize operation ends.  Clears all highlights.
    /// </summary>
    public void EndResize()
    {
        if (!IsActive)
        {
            return;
        }

        ClearAllHighlights();
        IsActive = false;
        _resizingWidgetHwnd = IntPtr.Zero;
        _resizingWidgetRoot = null;
        _resizeSnapTargets.Clear();
        _resizeWorkAreaBounds = null;

        App.LogVerbose("[ResizeGuide] EndResize");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Snap detection
    // ─────────────────────────────────────────────────────────────────────

    private List<WidgetSnapTarget> GetOtherWidgetBounds(
        IntPtr excludedHwnd)
    {
        var bounds = new List<WidgetSnapTarget>();
        var manager = App.Current?.WidgetManager;
        if (manager is null)
        {
            return bounds;
        }

        foreach (var hwnd in manager.GetAllWidgetWindowHandles())
        {
            if (hwnd == excludedHwnd)
            {
                continue;
            }

            // Skip hidden windows so alignment guides don't snap to invisible widgets
            if (!Win32Helper.IsWindowVisible(hwnd))
            {
                continue;
            }

            if (Win32Helper.GetWindowRect(hwnd, out var rect) &&
                rect.Right > rect.Left && rect.Bottom > rect.Top)
            {
                bounds.Add(new WidgetSnapTarget(
                    new RectInt32(rect.Left, rect.Top,
                        rect.Right - rect.Left,
                        rect.Bottom - rect.Top),
                    hwnd));
            }
        }

        return bounds;
    }

    private static RectInt32? GetResizeWorkAreaBounds(IntPtr hwnd)
    {
        if (!Win32Helper.GetWindowRect(hwnd, out var windowRect))
        {
            return null;
        }

        int centerX = (windowRect.Left + windowRect.Right) / 2;
        int centerY = (windowRect.Top + windowRect.Bottom) / 2;
        if (!Win32Helper.TryGetMonitorWorkArea(centerX, centerY, out _, out var workArea))
        {
            return null;
        }

        return new RectInt32(
            workArea.Left,
            workArea.Top,
            workArea.Right - workArea.Left,
            workArea.Bottom - workArea.Top);
    }

    private void ConfigureSessionSnapMetrics(IntPtr hwnd, FrameworkElement root)
    {
        double scale = Win32Helper.GetDpiScaleForWindow(hwnd, root.XamlRoot);
        _sessionSnapSpacingPhysical = Math.Max(0, (int)Math.Round(
            SettingsService.NormalizeWidgetSnapSpacing(SnapSpacingDips) * scale));
        _sessionSnapEngageThresholdPhysical = Math.Max(1, (int)Math.Round(
            SnapEngageThresholdDips * scale));
        _sessionSnapReleaseThresholdPhysical = Math.Max(
            _sessionSnapEngageThresholdPhysical,
            (int)Math.Round(SnapReleaseThresholdDips * scale));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Edge highlight management
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Brings the on-screen edge lines in line with <paramref name="desired"/>:
    /// lines for edges that are no longer snapped fade out, newly snapped
    /// edges get a fresh line that plays the one-shot settle animation, and
    /// unchanged edges keep their existing line untouched.
    /// </summary>
    private void SyncHighlights(HashSet<(IntPtr Hwnd, SnapEdge Edge)> desired)
    {
        var stale = new List<(IntPtr Hwnd, SnapEdge Edge)>();
        foreach (var kvp in _activeHighlights)
        {
            if (!desired.Contains(kvp.Key))
            {
                stale.Add(kvp.Key);
            }
        }

        foreach (var key in stale)
        {
            FadeOutHighlight(_activeHighlights[key]);
            _activeHighlights.Remove(key);
        }

        foreach (var key in desired)
        {
            if (_activeHighlights.ContainsKey(key))
            {
                continue;
            }

            FrameworkElement? root = key.Hwnd == _resizingWidgetHwnd
                ? _resizingWidgetRoot
                : App.Current?.WidgetManager?.GetWidgetRootElementByHandle(key.Hwnd);
            ShowHighlight(key.Hwnd, root, key.Edge);
        }
    }

    private static void AddMatchHighlights(
        HashSet<(IntPtr Hwnd, SnapEdge Edge)> desired,
        IntPtr sourceHwnd,
        WidgetSnapMatch? match)
    {
        if (match is not { } snapMatch)
        {
            return;
        }

        desired.Add((sourceHwnd, ToOverlayEdge(snapMatch.SourceEdge)));
        if (snapMatch.TargetWindowHandle != IntPtr.Zero)
        {
            desired.Add((snapMatch.TargetWindowHandle, ToOverlayEdge(snapMatch.TargetEdge)));
        }
    }

    private void ShowHighlight(IntPtr hwnd, FrameworkElement? root, SnapEdge edge)
    {
        if (root is not Grid grid)
        {
            return;
        }

        var key = (hwnd, edge);
        bool isVertical = edge is SnapEdge.Left or SnapEdge.Right;

        var host = new Border
        {
            IsHitTestVisible = false,
            Opacity = 0,
            HorizontalAlignment = edge switch
            {
                SnapEdge.Left => HorizontalAlignment.Left,
                SnapEdge.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Stretch
            },
            VerticalAlignment = edge switch
            {
                SnapEdge.Top => VerticalAlignment.Top,
                SnapEdge.Bottom => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Stretch
            },
            Width = isVertical ? HighlightBandDips : double.NaN,
            Height = isVertical ? double.NaN : HighlightBandDips,
        };

        Grid.SetRowSpan(host, 20);
        Grid.SetColumnSpan(host, 20);
        Grid.SetRow(host, 0);
        Grid.SetColumn(host, 0);
        host.SetValue(Canvas.ZIndexProperty, HighlightZIndex);

        // True 2D falloff via a composition mask brush: a perpendicular
        // gradient (opaque accent at the boundary → transparent inward)
        // multiplied by a longitudinal alpha mask whose tips fade out before
        // the corner zone.  Band ends dissolve instead of being sheared by
        // the DWM-rounded window silhouette.
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        var (glow, tipFadeIn, tipFadeOut, resources) =
            CreateEdgeGlowVisual(compositor, _highlightColor, edge, isVertical);
        ElementCompositionPreview.SetElementChildVisual(host, glow);
        host.Tag = resources;

        double tipFadeDips = GetTipFadeDips();
        host.SizeChanged += (_, e) =>
        {
            glow.Size = new Vector2(
                (float)e.NewSize.Width, (float)e.NewSize.Height);
            float length = Math.Max(
                1.0f,
                isVertical ? (float)e.NewSize.Height : (float)e.NewSize.Width);
            float fade = Math.Clamp(
                (float)(tipFadeDips / length),
                TipFadeMinFraction,
                TipFadeMaxFraction);
            tipFadeIn.Offset = fade;
            tipFadeOut.Offset = 1.0f - fade;
        };

        // One-shot entry: the glow fades in while sliding outward onto the
        // boundary — the closest "extending out of the edge" a
        // window-clipped surface can produce.
        var slide = new TranslateTransform();
        switch (edge)
        {
            case SnapEdge.Left: slide.X = HighlightSlideDips; break;
            case SnapEdge.Right: slide.X = -HighlightSlideDips; break;
            case SnapEdge.Top: slide.Y = HighlightSlideDips; break;
            default: slide.Y = -HighlightSlideDips; break;
        }
        host.RenderTransform = slide;

        var fadeIn = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = new Duration(HighlightFadeInDuration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fadeIn, host);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");

        var slideIn = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(HighlightSlideDuration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(slideIn, slide);
        Storyboard.SetTargetProperty(slideIn, isVertical ? "X" : "Y");

        var sb = new Storyboard();
        sb.Children.Add(fadeIn);
        sb.Children.Add(slideIn);
        host.Resources["EnterStoryboard"] = sb;

        grid.Children.Add(host);
        _activeHighlights[key] = host;
        sb.Begin();
    }

    /// <summary>
    /// Builds the composition sprite for one edge-glow band.  The brush is a
    /// mask brush: source = perpendicular color gradient, mask =
    /// longitudinal alpha fade, so the rendered falloff is the product of
    /// the two axes.
    /// </summary>
    private static (SpriteVisual Glow, CompositionColorGradientStop TipFadeIn,
        CompositionColorGradientStop TipFadeOut, IDisposable[] Resources)
        CreateEdgeGlowVisual(
            Compositor compositor,
            Windows.UI.Color accent,
            SnapEdge edge,
            bool isVertical)
    {
        var (start, end) = edge switch
        {
            SnapEdge.Left => (new Vector2(0, 0.5f), new Vector2(1, 0.5f)),
            SnapEdge.Right => (new Vector2(1, 0.5f), new Vector2(0, 0.5f)),
            SnapEdge.Top => (new Vector2(0.5f, 0), new Vector2(0.5f, 1)),
            _ => (new Vector2(0.5f, 1), new Vector2(0.5f, 0)),
        };

        var colorGradient = compositor.CreateLinearGradientBrush();
        colorGradient.StartPoint = start;
        colorGradient.EndPoint = end;
        colorGradient.ColorStops.Add(
            compositor.CreateColorGradientStop(0.0f, accent));
        colorGradient.ColorStops.Add(
            compositor.CreateColorGradientStop(0.22f, WithAlpha(accent, 180)));
        colorGradient.ColorStops.Add(
            compositor.CreateColorGradientStop(0.55f, WithAlpha(accent, 70)));
        colorGradient.ColorStops.Add(
            compositor.CreateColorGradientStop(1.0f, WithAlpha(accent, 0)));

        var transparent = Windows.UI.Color.FromArgb(0, 255, 255, 255);
        var opaque = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        var tipFade = compositor.CreateLinearGradientBrush();
        tipFade.StartPoint = isVertical
            ? new Vector2(0.5f, 0)
            : new Vector2(0, 0.5f);
        tipFade.EndPoint = isVertical
            ? new Vector2(0.5f, 1)
            : new Vector2(1, 0.5f);
        var tipStart = compositor.CreateColorGradientStop(0.0f, transparent);
        var tipFadeIn = compositor.CreateColorGradientStop(0.12f, opaque);
        var tipFadeOut = compositor.CreateColorGradientStop(0.88f, opaque);
        var tipEnd = compositor.CreateColorGradientStop(1.0f, transparent);
        tipFade.ColorStops.Add(tipStart);
        tipFade.ColorStops.Add(tipFadeIn);
        tipFade.ColorStops.Add(tipFadeOut);
        tipFade.ColorStops.Add(tipEnd);

        var mask = compositor.CreateMaskBrush();
        mask.Source = colorGradient;
        mask.Mask = tipFade;

        var glow = compositor.CreateSpriteVisual();
        glow.Brush = mask;

        return (glow, tipFadeIn, tipFadeOut,
            new IDisposable[]
            {
                glow, mask, colorGradient, tipFade,
                tipStart, tipFadeIn, tipFadeOut, tipEnd
            });
    }

    private static Windows.UI.Color WithAlpha(Windows.UI.Color color, byte alpha) =>
        Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>
    /// Length of the fade that dissolves each band end: the widget's outer
    /// corner radius plus a small margin, so the glow is fully gone before
    /// the DWM-rounded silhouette starts curving.
    /// </summary>
    private static double GetTipFadeDips()
    {
        string preference = WindowsCompatibilityService.ResolveEffectiveWidgetCornerPreference(
            App.Current?.SettingsService?.Settings.WidgetShell.WidgetCornerPreference
            ?? SettingsService.WidgetCornerPreferenceRound);
        double radius = WidgetCompactBoundsCalculator.ResolveOuterCornerRadius(preference);
        return Math.Max(10.0, radius + 4.0);
    }

    private void ClearAllHighlights()
    {
        foreach (var kvp in _activeHighlights)
        {
            FadeOutHighlight(kvp.Value);
        }

        _activeHighlights.Clear();
    }

    private static void StopHighlightAnimation(Border border)
    {
        if (border.Resources.TryGetValue("EnterStoryboard", out var value) &&
            value is Storyboard sb)
        {
            sb.Stop();
        }
    }

    /// <summary>
    /// Quickly fades a highlight band out, then removes it from its parent
    /// grid and disposes its composition resources.
    /// </summary>
    private static void FadeOutHighlight(Border border)
    {
        double startOpacity = border.Opacity;
        var slide = border.RenderTransform as TranslateTransform;
        double startX = slide?.X ?? 0;
        double startY = slide?.Y ?? 0;
        StopHighlightAnimation(border);
        border.Opacity = startOpacity;
        if (slide is not null)
        {
            // Stopping the enter animation reverts the slide to its initial
            // offset; keep the band where it was so the fade does not jump.
            slide.X = startX;
            slide.Y = startY;
        }

        var fadeOut = new DoubleAnimation
        {
            From = startOpacity,
            To = 0,
            Duration = new Duration(HighlightFadeOutDuration),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        Storyboard.SetTarget(fadeOut, border);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(fadeOut);
        sb.Completed += (_, _) => RemoveHighlightElement(border);
        // Fallback: if the host leaves the tree before the storyboard
        // finishes (widget window closed mid-fade, grid rebuilt), Completed
        // may never fire — Unloaded guarantees the composition objects are
        // still released.  Removal is idempotent.
        border.Unloaded += (_, _) => RemoveHighlightElement(border);
        try
        {
            sb.Begin();
        }
        catch (Exception)
        {
            RemoveHighlightElement(border);
        }
    }

    /// <summary>
    /// Detaches the host from its grid and releases the composition objects
    /// it owns.  Safe to call more than once and on detached elements.
    /// </summary>
    private static void RemoveHighlightElement(Border border)
    {
        if (border.Parent is Grid grid)
        {
            grid.Children.Remove(border);
        }

        if (border.Tag is IDisposable[] resources)
        {
            ElementCompositionPreview.SetElementChildVisual(border, null);
            foreach (var resource in resources)
            {
                resource.Dispose();
            }

            border.Tag = null;
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────

    private static SnapEdge ToOverlayEdge(WidgetSnapEdge edge) =>
        edge switch
        {
            WidgetSnapEdge.Left => SnapEdge.Left,
            WidgetSnapEdge.Right => SnapEdge.Right,
            WidgetSnapEdge.Top => SnapEdge.Top,
            WidgetSnapEdge.Bottom => SnapEdge.Bottom,
            _ => SnapEdge.Left
        };

    /// <summary>
    /// The tone a snap guide edge line draws with: the effective DeskBox
    /// accent. Resizing/drag-snap feedback is deliberately the one drag-time
    /// visual that keeps the theme color (Simon, 2026-09-13).
    /// </summary>
    private static Windows.UI.Color GetHighlightColor() =>
        App.Current?.ThemeService?.GetEffectiveAccentColor()
        ?? AccentColorHelper.DefaultAccentColor;

    // ── Drag session state ──────────────────────────────────────────────

    private IntPtr _draggingWidgetHwnd;
    private FrameworkElement? _draggingWidgetRoot;

    /// <summary>
    /// Whether a drag-move session is currently active.
    /// </summary>
    public bool IsDragActive { get; private set; }

    // ─────────────────────────────────────────────────────────────────────
    //  Drag-Move snap API
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called when a widget drag-move operation begins.
    /// </summary>
    public void BeginDrag(IntPtr draggingWidgetHwnd, FrameworkElement draggingWidgetRoot)
    {
        _draggingWidgetHwnd = draggingWidgetHwnd;
        _draggingWidgetRoot = draggingWidgetRoot;
        _highlightColor = GetHighlightColor();
        _currentDragHorizontalMatch = null;
        _currentDragVerticalMatch = null;
        _dragSnapTargets.Clear();
        _dragSnapTargets.AddRange(GetOtherWidgetBounds(draggingWidgetHwnd));
        _resizeWorkAreaBounds = GetResizeWorkAreaBounds(draggingWidgetHwnd);
        ConfigureSessionSnapMetrics(draggingWidgetHwnd, draggingWidgetRoot);
        IsDragActive = true;

        App.LogVerbose($"[ResizeGuide] BeginDrag hwnd=0x{draggingWidgetHwnd.ToInt64():X}");
    }

    /// <summary>
    /// Called on every PointerMoved during drag-move.  Checks the proposed
    /// bounds against all other widget edges and work-area edges, snaps if
    /// within threshold, and shows edge highlights.
    /// Returns the (possibly snapped) bounds to apply.
    /// </summary>
    public RectInt32 UpdateGuidesAndSnapForDrag(RectInt32 proposedBounds, bool suspendSnap = false)
    {
        if (suspendSnap && IsDragActive)
        {
            // A merge target is engaged: edge snapping would drag the window
            // away from the drop zone, so hide the bands and drop the
            // hysteresis lock to start fresh once the pointer leaves it.
            ClearAllHighlights();
            _currentDragHorizontalMatch = null;
            _currentDragVerticalMatch = null;
            return proposedBounds;
        }

        if (!IsDragActive || !IsSnapEnabled)
        {
            // Snap toggled off mid-drag must still converge the bands.
            if (IsDragActive)
            {
                ClearAllHighlights();
            }

            return proposedBounds;
        }

        // Reuse the resize session state for highlight management
        _resizingWidgetHwnd = _draggingWidgetHwnd;
        _resizingWidgetRoot = _draggingWidgetRoot;
        IsActive = true;

        WidgetMoveSnapResult result = WidgetSnapCalculator.ResolveMove(
            proposedBounds,
            _dragSnapTargets,
            _resizeWorkAreaBounds,
            _sessionSnapSpacingPhysical,
            _sessionSnapEngageThresholdPhysical,
            _sessionSnapReleaseThresholdPhysical,
            _currentDragHorizontalMatch,
            _currentDragVerticalMatch);
        _currentDragHorizontalMatch = result.HorizontalMatch;
        _currentDragVerticalMatch = result.VerticalMatch;

        // ── Update highlights for the active snap matches ────────────────

        var desired = new HashSet<(IntPtr Hwnd, SnapEdge Edge)>();
        AddMatchHighlights(desired, _draggingWidgetHwnd, result.VerticalMatch);
        AddMatchHighlights(desired, _draggingWidgetHwnd, result.HorizontalMatch);
        SyncHighlights(desired);

        return result.Bounds;
    }

    /// <summary>
    /// Called when the drag-move operation ends.  Clears all highlights.
    /// </summary>
    public void EndDrag()
    {
        if (!IsDragActive)
        {
            return;
        }

        ClearAllHighlights();
        IsDragActive = false;
        IsActive = false;
        _draggingWidgetHwnd = IntPtr.Zero;
        _draggingWidgetRoot = null;
        _currentDragHorizontalMatch = null;
        _currentDragVerticalMatch = null;
        _dragSnapTargets.Clear();
        _resizeWorkAreaBounds = null;
        // The drag path aliases the resize session fields — drop them too
        // so a closed widget's root element isn't retained until the next
        // session.
        _resizingWidgetHwnd = IntPtr.Zero;
        _resizingWidgetRoot = null;
        _resizeSnapTargets.Clear();

        App.LogVerbose("[ResizeGuide] EndDrag");
    }

    private enum SnapEdge
    {
        Left,
        Right,
        Top,
        Bottom
    }
}
