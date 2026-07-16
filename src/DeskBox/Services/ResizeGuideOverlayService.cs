using DeskBox.Helpers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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

    private const double SnapThreshold = 4.0;       // Physical pixels (halved)
    private const int HighlightThickness = 12;       // DIPs – gradient fade width
    private const int HighlightZIndex = 100;
    private const double BreathingMinOpacity = 0.45;
    private const double BreathingMaxOpacity = 1.0;
    private static readonly TimeSpan BreathingDuration = TimeSpan.FromMilliseconds(1200);

    private static readonly Windows.UI.Color _transparent = Windows.UI.Color.FromArgb(0, 0, 0, 0);

    // ── Active highlight elements (keyed by widget HWND) ────────────────

    private readonly Dictionary<IntPtr, Border> _activeHighlights = new();

    // ── Resize session state ─────────────────────────────────────────────

    private IntPtr _resizingWidgetHwnd;
    private FrameworkElement? _resizingWidgetRoot;
    private Windows.UI.Color _highlightColor;
    private IntPtr _currentTargetHwnd;
    private FrameworkElement? _currentTargetRoot;
    private SnapEdge? _currentResizeEdge;
    private SnapEdge? _currentTargetEdge;
    private string? _lastDragSnapSignature;

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
        _currentTargetHwnd = IntPtr.Zero;
        _currentTargetRoot = null;
        _currentResizeEdge = null;
        _currentTargetEdge = null;
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
            return proposedBounds;
        }

        var otherBounds = GetOtherWidgetBounds();
        var snapped = proposedBounds;
        SnapEdge? snapEdge = null;
        int snapCoordinate = 0;
        IntPtr? targetHwnd = null;

        // ── Horizontal edge snapping (Left / Right) ──────────────────────

        bool checkRight = resizeDirection.Contains("Right", StringComparison.Ordinal);
        bool checkLeft = resizeDirection.Contains("Left", StringComparison.Ordinal);

        if (checkRight || checkLeft)
        {
            int edgeX = checkRight
                ? proposedBounds.X + proposedBounds.Width
                : proposedBounds.X;

            var (snapX, target) = FindSnapEdgeX(edgeX, proposedBounds, otherBounds);
            if (snapX.HasValue)
            {
                int snappedWidth = checkRight
                    ? snapX.Value - snapped.X
                    : snapped.X + snapped.Width - snapX.Value;
                bool widthAllowed = (!minimumWidth.HasValue || snappedWidth >= minimumWidth.Value) &&
                    (!maximumWidth.HasValue || snappedWidth <= maximumWidth.Value);
                if (!widthAllowed)
                {
                    snapX = null;
                }
            }

            if (snapX.HasValue)
            {
                if (checkRight)
                {
                    snapped = new RectInt32(
                        snapped.X, snapped.Y,
                        snapX.Value - snapped.X,
                        snapped.Height);
                    snapEdge = SnapEdge.Right;
                }
                else
                {
                    int rightEdge = snapped.X + snapped.Width;
                    snapped = new RectInt32(
                        snapX.Value, snapped.Y,
                        rightEdge - snapX.Value,
                        snapped.Height);
                    snapEdge = SnapEdge.Left;
                }

                snapCoordinate = snapX.Value;
                targetHwnd = target;
            }
        }

        // ── Vertical edge snapping (Top / Bottom) ────────────────────────

        bool checkBottom = resizeDirection.Contains("Bottom", StringComparison.Ordinal);
        bool checkTop = resizeDirection.Contains("Top", StringComparison.Ordinal);

        if (checkBottom || checkTop)
        {
            int edgeY = checkBottom
                ? proposedBounds.Y + proposedBounds.Height
                : proposedBounds.Y;

            var (snapY, target) = FindSnapEdgeY(edgeY, proposedBounds, otherBounds);
            if (snapY.HasValue)
            {
                if (checkBottom)
                {
                    snapped = new RectInt32(
                        snapped.X, snapped.Y,
                        snapped.Width,
                        snapY.Value - snapped.Y);
                    snapEdge = SnapEdge.Bottom;
                }
                else
                {
                    int bottomEdge = snapped.Y + snapped.Height;
                    snapped = new RectInt32(
                        snapped.X, snapY.Value,
                        snapped.Width,
                        bottomEdge - snapY.Value);
                    snapEdge = SnapEdge.Top;
                }

                snapCoordinate = snapY.Value;
                targetHwnd = target;
            }
        }

        // ── Update highlights ────────────────────────────────────────────

        if (snapEdge.HasValue)
        {
            // Only rebuild the resizing widget's highlight if the edge changed.
            if (_currentResizeEdge != snapEdge.Value)
            {
                ShowHighlight(_resizingWidgetHwnd, _resizingWidgetRoot, snapEdge.Value);
                _currentResizeEdge = snapEdge.Value;
            }

            // Highlight target widget's matched edge
            if (targetHwnd.HasValue && targetHwnd.Value != IntPtr.Zero)
            {
                var targetRoot = App.Current?.WidgetManager
                    ?.GetWidgetRootElementByHandle(targetHwnd.Value);
                if (targetRoot is not null)
                {
                    // Determine which edge of the target was actually matched
                    // by comparing the snap coordinate to the target's bounds.
                    var targetEdge = ResolveTargetEdge(
                        targetHwnd.Value, snapCoordinate, snapEdge.Value);

                    // Clear previous target if it changed
                    if (_currentTargetHwnd != IntPtr.Zero &&
                        _currentTargetHwnd != targetHwnd.Value)
                    {
                        RemoveHighlight(_currentTargetHwnd);
                        _currentTargetEdge = null;
                    }

                    // Only rebuild target highlight if target or edge changed
                    if (_currentTargetHwnd != targetHwnd.Value ||
                        _currentTargetEdge != targetEdge)
                    {
                        ShowHighlight(targetHwnd.Value, targetRoot, targetEdge);
                        _currentTargetEdge = targetEdge;
                    }

                    _currentTargetHwnd = targetHwnd.Value;
                    _currentTargetRoot = targetRoot;
                }
            }
        }
        else
        {
            ClearAllHighlights();
            _currentResizeEdge = null;
            _currentTargetEdge = null;
        }

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
        _currentTargetHwnd = IntPtr.Zero;
        _currentTargetRoot = null;
        _currentResizeEdge = null;
        _currentTargetEdge = null;

        App.LogVerbose("[ResizeGuide] EndResize");
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Snap detection
    // ─────────────────────────────────────────────────────────────────────

    private List<(RectInt32 Bounds, IntPtr Hwnd)> GetOtherWidgetBounds()
    {
        var bounds = new List<(RectInt32, IntPtr)>();
        var manager = App.Current?.WidgetManager;
        if (manager is null)
        {
            return bounds;
        }

        foreach (var hwnd in manager.GetAllWidgetWindowHandles())
        {
            if (hwnd == _resizingWidgetHwnd)
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
                bounds.Add((
                    new RectInt32(rect.Left, rect.Top,
                        rect.Right - rect.Left,
                        rect.Bottom - rect.Top),
                    hwnd));
            }
        }

        return bounds;
    }

    private (int? SnapX, IntPtr? TargetHwnd) FindSnapEdgeX(
        int edgeX,
        RectInt32 sourceBounds,
        List<(RectInt32 Bounds, IntPtr Hwnd)> otherBounds)
    {
        int bestDelta = (int)Math.Ceiling(SnapThreshold);
        int bestGap = int.MaxValue;
        int bestCenterDistance = int.MaxValue;
        int? bestSnap = null;
        IntPtr? bestTarget = null;

        foreach (var (other, hwnd) in otherBounds)
        {
            TrySnapWidget(
                edgeX, other.X, hwnd, sourceBounds, other, alignX: true,
                ref bestDelta, ref bestGap, ref bestCenterDistance, ref bestSnap, ref bestTarget);
            TrySnapWidget(
                edgeX, other.X + other.Width, hwnd, sourceBounds, other, alignX: true,
                ref bestDelta, ref bestGap, ref bestCenterDistance, ref bestSnap, ref bestTarget);
        }

        // Work area edges (no target widget)
        int waLeft = 0;
        int waRight = int.MaxValue;
        if (Win32Helper.GetWindowRect(_resizingWidgetHwnd, out var wr))
        {
            int cx = (wr.Left + wr.Right) / 2;
            int cy = (wr.Top + wr.Bottom) / 2;
            if (Win32Helper.TryGetMonitorWorkArea(cx, cy, out _, out var workArea))
            {
                waLeft = workArea.Left;
                waRight = workArea.Right;
            }
        }

        TrySnap(edgeX, waLeft, IntPtr.Zero, ref bestDelta, ref bestSnap, ref bestTarget);
        TrySnap(edgeX, waRight, IntPtr.Zero, ref bestDelta, ref bestSnap, ref bestTarget);

        return (bestSnap, bestTarget);
    }

    private (int? SnapY, IntPtr? TargetHwnd) FindSnapEdgeY(
        int edgeY,
        RectInt32 sourceBounds,
        List<(RectInt32 Bounds, IntPtr Hwnd)> otherBounds)
    {
        int bestDelta = (int)Math.Ceiling(SnapThreshold);
        int bestGap = int.MaxValue;
        int bestCenterDistance = int.MaxValue;
        int? bestSnap = null;
        IntPtr? bestTarget = null;

        foreach (var (other, hwnd) in otherBounds)
        {
            TrySnapWidget(
                edgeY, other.Y, hwnd, sourceBounds, other, alignX: false,
                ref bestDelta, ref bestGap, ref bestCenterDistance, ref bestSnap, ref bestTarget);
            TrySnapWidget(
                edgeY, other.Y + other.Height, hwnd, sourceBounds, other, alignX: false,
                ref bestDelta, ref bestGap, ref bestCenterDistance, ref bestSnap, ref bestTarget);
        }

        // Work area edges
        int waTop = 0;
        int waBottom = int.MaxValue;
        if (Win32Helper.GetWindowRect(_resizingWidgetHwnd, out var wr))
        {
            int cx = (wr.Left + wr.Right) / 2;
            int cy = (wr.Top + wr.Bottom) / 2;
            if (Win32Helper.TryGetMonitorWorkArea(cx, cy, out _, out var workArea))
            {
                waTop = workArea.Top;
                waBottom = workArea.Bottom;
            }
        }

        TrySnap(edgeY, waTop, IntPtr.Zero, ref bestDelta, ref bestSnap, ref bestTarget);
        TrySnap(edgeY, waBottom, IntPtr.Zero, ref bestDelta, ref bestSnap, ref bestTarget);

        return (bestSnap, bestTarget);
    }

    private static void TrySnap(
        int edge, int candidate, IntPtr hwnd,
        ref int bestDelta, ref int? bestSnap, ref IntPtr? bestTarget)
    {
        int delta = Math.Abs(edge - candidate);
        if (delta < bestDelta)
        {
            bestDelta = delta;
            bestSnap = candidate;
            bestTarget = hwnd;
        }
    }

    private static void TrySnapWidget(
        int edge,
        int candidate,
        IntPtr hwnd,
        RectInt32 source,
        RectInt32 target,
        bool alignX,
        ref int bestDelta,
        ref int bestGap,
        ref int bestCenterDistance,
        ref int? bestSnap,
        ref IntPtr? bestTarget)
    {
        int delta = Math.Abs(edge - candidate);
        if (delta >= (int)Math.Ceiling(SnapThreshold))
        {
            return;
        }

        int sourceStart = alignX ? source.Y : source.X;
        int sourceEnd = sourceStart + (alignX ? source.Height : source.Width);
        int targetStart = alignX ? target.Y : target.X;
        int targetEnd = targetStart + (alignX ? target.Height : target.Width);
        int gap = targetEnd < sourceStart
            ? sourceStart - targetEnd
            : sourceEnd < targetStart
                ? targetStart - sourceEnd
                : 0;
        int sourceCenter = sourceStart + ((sourceEnd - sourceStart) / 2);
        int targetCenter = targetStart + ((targetEnd - targetStart) / 2);
        int centerDistance = Math.Abs(sourceCenter - targetCenter);

        bool isBetter = delta < bestDelta ||
            delta == bestDelta && gap < bestGap ||
            delta == bestDelta && gap == bestGap && centerDistance < bestCenterDistance;
        if (!isBetter)
        {
            return;
        }

        bestDelta = delta;
        bestGap = gap;
        bestCenterDistance = centerDistance;
        bestSnap = candidate;
        bestTarget = hwnd;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Edge highlight management
    // ─────────────────────────────────────────────────────────────────────

    private void ShowHighlight(IntPtr hwnd, FrameworkElement? root, SnapEdge edge)
    {
        if (root is not Grid grid)
        {
            return;
        }

        // Remove existing highlight for this widget if any
        if (_activeHighlights.TryGetValue(hwnd, out var existing))
        {
            StopHighlightAnimation(existing);
            grid.Children.Remove(existing);
            _activeHighlights.Remove(hwnd);
        }

        var c = _highlightColor;

        // Edge glow: brightest at the very edge, fading softly inward.
        // The gradient runs from the edge (opaque) toward the interior (transparent).
        LinearGradientBrush glowBrush;
        double thickness = HighlightThickness;

        // Edge highlight color: bright at edge, slightly translucent
        var edgeColor = Windows.UI.Color.FromArgb(255, c.R, c.G, c.B);

        // Mid-stop: accent color with transparency for both themes.
        // Previously light theme used white here, which created a gray
        // transition band between the accent edge and the white mid-stop.
        // Using accent color with alpha keeps the glow unified and clean.
        var midColor = Windows.UI.Color.FromArgb(100, c.R, c.G, c.B);

        if (edge is SnapEdge.Left)
        {
            // Aligned left, gradient: left=bright → right=transparent
            glowBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0, 0.5),
                EndPoint = new Windows.Foundation.Point(1, 0.5),
            };
            glowBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = edgeColor });
            glowBrush.GradientStops.Add(new GradientStop { Offset = 0.3, Color = midColor });
            glowBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = _transparent });
        }
        else if (edge is SnapEdge.Right)
        {
            // Aligned right, gradient: right=bright → left=transparent
            glowBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(1, 0.5),
                EndPoint = new Windows.Foundation.Point(0, 0.5),
            };
            glowBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = edgeColor });
            glowBrush.GradientStops.Add(new GradientStop { Offset = 0.3, Color = midColor });
            glowBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = _transparent });
        }
        else if (edge is SnapEdge.Top)
        {
            // Aligned top, gradient: top=bright → bottom=transparent
            glowBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.5, 0),
                EndPoint = new Windows.Foundation.Point(0.5, 1),
            };
            glowBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = edgeColor });
            glowBrush.GradientStops.Add(new GradientStop { Offset = 0.3, Color = midColor });
            glowBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = _transparent });
        }
        else // Bottom
        {
            // Aligned bottom, gradient: bottom=bright → top=transparent
            glowBrush = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.5, 1),
                EndPoint = new Windows.Foundation.Point(0.5, 0),
            };
            glowBrush.GradientStops.Add(new GradientStop { Offset = 0.0, Color = edgeColor });
            glowBrush.GradientStops.Add(new GradientStop { Offset = 0.3, Color = midColor });
            glowBrush.GradientStops.Add(new GradientStop { Offset = 1.0, Color = _transparent });
        }

        var border = new Border
        {
            Background = glowBrush,
            IsHitTestVisible = false,
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
            Width = edge is SnapEdge.Left or SnapEdge.Right ? thickness : double.NaN,
            Height = edge is SnapEdge.Top or SnapEdge.Bottom ? thickness : double.NaN,
        };

        Grid.SetRowSpan(border, 20);
        Grid.SetColumnSpan(border, 20);
        Grid.SetRow(border, 0);
        Grid.SetColumn(border, 0);
        border.SetValue(Canvas.ZIndexProperty, HighlightZIndex);

        // Breathing animation: pulse opacity gently
        var breathing = new DoubleAnimation
        {
            From = BreathingMaxOpacity,
            To = BreathingMinOpacity,
            Duration = new Duration(BreathingDuration),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(breathing, border);
        Storyboard.SetTargetProperty(breathing, "Opacity");

        var sb = new Storyboard();
        sb.Children.Add(breathing);
        border.Resources["BreathingStoryboard"] = sb;

        grid.Children.Add(border);
        _activeHighlights[hwnd] = border;

        sb.Begin();
    }

    private void RemoveHighlight(IntPtr hwnd)
    {
        if (!_activeHighlights.TryGetValue(hwnd, out var border))
        {
            return;
        }

        StopHighlightAnimation(border);

        if (border.Parent is Grid grid)
        {
            grid.Children.Remove(border);
        }

        _activeHighlights.Remove(hwnd);
    }

    private void ClearAllHighlights()
    {
        foreach (var kvp in _activeHighlights)
        {
            StopHighlightAnimation(kvp.Value);
            if (kvp.Value.Parent is Grid grid)
            {
                grid.Children.Remove(kvp.Value);
            }
        }
        _activeHighlights.Clear();
        _currentTargetHwnd = IntPtr.Zero;
        _currentTargetRoot = null;
        _currentResizeEdge = null;
        _currentTargetEdge = null;
    }

    private static void StopHighlightAnimation(Border border)
    {
        if (border.Resources.TryGetValue("BreathingStoryboard", out var value) &&
            value is Storyboard sb)
        {
            sb.Stop();
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Determines which edge of the target widget was actually matched by
    /// comparing the snap coordinate to the target's physical bounds.
    /// For example, if A's right edge snaps to B's right edge, this returns
    /// SnapEdge.Right (not Left), so B's right edge is highlighted.
    /// </summary>
    private static SnapEdge ResolveTargetEdge(
        IntPtr targetHwnd, int snapCoordinate, SnapEdge resizingEdge)
    {
        if (!Win32Helper.GetWindowRect(targetHwnd, out var rect))
        {
            // Fallback: use opposite edge
            return resizingEdge switch
            {
                SnapEdge.Left => SnapEdge.Right,
                SnapEdge.Right => SnapEdge.Left,
                SnapEdge.Top => SnapEdge.Bottom,
                SnapEdge.Bottom => SnapEdge.Top,
                _ => resizingEdge
            };
        }

        // Horizontal snap: determine if snap X matches target's left or right
        if (resizingEdge is SnapEdge.Left or SnapEdge.Right)
        {
            int distLeft = Math.Abs(snapCoordinate - rect.Left);
            int distRight = Math.Abs(snapCoordinate - rect.Right);
            return distLeft <= distRight ? SnapEdge.Left : SnapEdge.Right;
        }

        // Vertical snap: determine if snap Y matches target's top or bottom
        int distTop = Math.Abs(snapCoordinate - rect.Top);
        int distBottom = Math.Abs(snapCoordinate - rect.Bottom);
        return distTop <= distBottom ? SnapEdge.Top : SnapEdge.Bottom;
    }

    private static Windows.UI.Color GetHighlightColor()
    {
        return App.Current?.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
    }

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
        _currentTargetHwnd = IntPtr.Zero;
        _currentTargetRoot = null;
        _currentResizeEdge = null;
        _currentTargetEdge = null;
        _lastDragSnapSignature = null;
        IsDragActive = true;

        App.LogVerbose($"[ResizeGuide] BeginDrag hwnd=0x{draggingWidgetHwnd.ToInt64():X}");
    }

    /// <summary>
    /// Called on every PointerMoved during drag-move.  Checks the proposed
    /// bounds against all other widget edges and work-area edges, snaps if
    /// within threshold, and shows edge highlights.
    /// Returns the (possibly snapped) bounds to apply.
    /// </summary>
    public RectInt32 UpdateGuidesAndSnapForDrag(RectInt32 proposedBounds)
    {
        if (!IsDragActive || !IsSnapEnabled)
        {
            return proposedBounds;
        }

        // Reuse the resize session state for highlight management
        _resizingWidgetHwnd = _draggingWidgetHwnd;
        _resizingWidgetRoot = _draggingWidgetRoot;
        IsActive = true;

        var otherBounds = GetOtherWidgetBounds();
        var snapped = proposedBounds;
        var snapInfos = new List<(SnapEdge Edge, int Coordinate, IntPtr? TargetHwnd)>();

        // ── Top edge snapping (vertical alignment of top edge) ──────

        int topEdgeY = proposedBounds.Y;
        var (snapTopY, targetTop) = FindSnapEdgeY(topEdgeY, proposedBounds, otherBounds);
        if (snapTopY.HasValue)
        {
            snapped = new RectInt32(
                snapped.X, snapTopY.Value,
                snapped.Width, snapped.Height);
            snapInfos.Add((SnapEdge.Top, snapTopY.Value, targetTop));
        }

        // ── Left edge snapping (horizontal alignment of left edge) ──

        int leftEdgeX = proposedBounds.X;
        var (snapLeftX, targetLeft) = FindSnapEdgeX(leftEdgeX, proposedBounds, otherBounds);
        if (snapLeftX.HasValue)
        {
            snapped = new RectInt32(
                snapLeftX.Value, snapped.Y,
                snapped.Width, snapped.Height);
            snapInfos.Add((SnapEdge.Left, snapLeftX.Value, targetLeft));
        }

        // ── Right edge snapping (horizontal alignment of right edge) ─

        int rightEdgeX = proposedBounds.X + proposedBounds.Width;
        var (snapRightX, targetRight) = FindSnapEdgeX(rightEdgeX, proposedBounds, otherBounds);
        if (snapRightX.HasValue)
        {
            // Adjust X so right edge snaps; width stays the same
            int newX = snapRightX.Value - proposedBounds.Width;
            snapped = new RectInt32(
                newX, snapped.Y,
                snapped.Width, snapped.Height);
            snapInfos.Add((SnapEdge.Right, snapRightX.Value, targetRight));
        }

        // ── Update highlights for the best snap ───────────────────────

        // Build a lightweight signature to detect whether the snap state
        // has actually changed since the last frame.  If it hasn't, we
        // skip the expensive ClearAll + rebuild cycle entirely.
        string snapSignature = string.Empty;
        if (snapInfos.Count > 0)
        {
            var horizontalSnap = snapInfos.FirstOrDefault(s => s.Edge is SnapEdge.Left or SnapEdge.Right);
            var verticalSnap = snapInfos.FirstOrDefault(s => s.Edge is SnapEdge.Top or SnapEdge.Bottom);
            snapSignature = $"{verticalSnap.Edge},{verticalSnap.TargetHwnd ?? IntPtr.Zero},{horizontalSnap.Edge},{horizontalSnap.TargetHwnd ?? IntPtr.Zero}";
        }

        if (snapSignature == _lastDragSnapSignature)
        {
            return snapped;
        }
        _lastDragSnapSignature = snapSignature;

        if (snapInfos.Count > 0)
        {
            // Show highlights for all snapped edges (up to 2: one horizontal, one vertical)
            var horizontalSnap = snapInfos.FirstOrDefault(s => s.Edge is SnapEdge.Left or SnapEdge.Right);
            var verticalSnap = snapInfos.FirstOrDefault(s => s.Edge is SnapEdge.Top or SnapEdge.Bottom);

            ClearAllHighlights();

            if (verticalSnap != default)
            {
                ShowHighlight(_draggingWidgetHwnd, _draggingWidgetRoot, verticalSnap.Edge);
                if (verticalSnap.TargetHwnd.HasValue && verticalSnap.TargetHwnd.Value != IntPtr.Zero)
                {
                    var targetRoot = App.Current?.WidgetManager
                        ?.GetWidgetRootElementByHandle(verticalSnap.TargetHwnd.Value);
                    if (targetRoot is not null)
                    {
                        var targetEdge = ResolveTargetEdge(
                            verticalSnap.TargetHwnd.Value, verticalSnap.Coordinate, verticalSnap.Edge);
                        ShowHighlight(verticalSnap.TargetHwnd.Value, targetRoot, targetEdge);
                        _currentTargetHwnd = verticalSnap.TargetHwnd.Value;
                        _currentTargetRoot = targetRoot;
                    }
                }
            }

            if (horizontalSnap != default)
            {
                ShowHighlight(_draggingWidgetHwnd, _draggingWidgetRoot, horizontalSnap.Edge);
                if (horizontalSnap.TargetHwnd.HasValue && horizontalSnap.TargetHwnd.Value != IntPtr.Zero)
                {
                    var targetRoot = App.Current?.WidgetManager
                        ?.GetWidgetRootElementByHandle(horizontalSnap.TargetHwnd.Value);
                    if (targetRoot is not null)
                    {
                        var targetEdge = ResolveTargetEdge(
                            horizontalSnap.TargetHwnd.Value, horizontalSnap.Coordinate, horizontalSnap.Edge);
                        ShowHighlight(horizontalSnap.TargetHwnd.Value, targetRoot, targetEdge);
                        _currentTargetHwnd = horizontalSnap.TargetHwnd.Value;
                        _currentTargetRoot = targetRoot;
                    }
                }
            }
        }
        else
        {
            ClearAllHighlights();
        }

        return snapped;
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
        _currentTargetHwnd = IntPtr.Zero;
        _currentTargetRoot = null;
        _currentResizeEdge = null;
        _currentTargetEdge = null;
        _lastDragSnapSignature = null;

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
