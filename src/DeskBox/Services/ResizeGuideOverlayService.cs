using DeskBox.Helpers;
using DeskBox.Platform;
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

    private const double SnapEngageThresholdDips = 8.0;
    private const double SnapReleaseThresholdDips = 12.0;
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
        _currentTargetHwnd = IntPtr.Zero;
        _currentTargetRoot = null;
        _currentResizeEdge = null;
        _currentTargetEdge = null;
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

        WidgetSnapMatch? visibleMatch = verticalMatch ?? horizontalMatch;
        if (visibleMatch is { } snapMatch)
        {
            SnapEdge snapEdge = ToOverlayEdge(snapMatch.SourceEdge);
            // Only rebuild the resizing widget's highlight if the edge changed.
            if (_currentResizeEdge != snapEdge)
            {
                ShowHighlight(_resizingWidgetHwnd, _resizingWidgetRoot, snapEdge);
                _currentResizeEdge = snapEdge;
            }

            // Highlight target widget's matched edge
            if (snapMatch.TargetWindowHandle != IntPtr.Zero)
            {
                var targetRoot = App.Current?.WidgetManager
                    ?.GetWidgetRootElementByHandle(snapMatch.TargetWindowHandle);
                if (targetRoot is not null)
                {
                    SnapEdge targetEdge = ToOverlayEdge(snapMatch.TargetEdge);

                    // Clear previous target if it changed
                    if (_currentTargetHwnd != IntPtr.Zero &&
                        _currentTargetHwnd != snapMatch.TargetWindowHandle)
                    {
                        RemoveHighlight(_currentTargetHwnd);
                        _currentTargetEdge = null;
                    }

                    // Only rebuild target highlight if target or edge changed
                    if (_currentTargetHwnd != snapMatch.TargetWindowHandle ||
                        _currentTargetEdge != targetEdge)
                    {
                        ShowHighlight(snapMatch.TargetWindowHandle, targetRoot, targetEdge);
                        _currentTargetEdge = targetEdge;
                    }

                    _currentTargetHwnd = snapMatch.TargetWindowHandle;
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

        // Mid-stop: the accent with transparency for both themes.
        // Previously light theme used white here, which created a grey
        // transition band between the accent edge and the white mid-stop.
        // Using the accent with alpha keeps the glow unified and clean.
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

        if (WindowsCompatibilityService.IsWindows11OrLater)
        {
            // Win11 keeps the original breathing effect. Win10 uses the same
            // edge gradient as a static glow so live resize does not add a
            // second continuously animated XAML workload.
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
        }

        grid.Children.Add(border);
        _activeHighlights[hwnd] = border;

        if (border.Resources.TryGetValue("BreathingStoryboard", out var value) &&
            value is Storyboard storyboard)
        {
            storyboard.Begin();
        }
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
    /// The tone a snap guide and its edge glow draw with: the effective DeskBox
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
        _currentTargetHwnd = IntPtr.Zero;
        _currentTargetRoot = null;
        _currentResizeEdge = null;
        _currentTargetEdge = null;
        _lastDragSnapSignature = null;
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

        // ── Update highlights for the best snap ───────────────────────

        // Build a lightweight signature to detect whether the snap state
        // has actually changed since the last frame.  If it hasn't, we
        // skip the expensive ClearAll + rebuild cycle entirely.
        string snapSignature = string.Empty;
        if (result.HorizontalMatch is not null || result.VerticalMatch is not null)
        {
            WidgetSnapMatch horizontal = result.HorizontalMatch.GetValueOrDefault();
            WidgetSnapMatch vertical = result.VerticalMatch.GetValueOrDefault();
            snapSignature =
                $"{vertical.SourceEdge},{vertical.TargetEdge},{vertical.TargetWindowHandle}," +
                $"{horizontal.SourceEdge},{horizontal.TargetEdge},{horizontal.TargetWindowHandle}";
        }

        if (snapSignature == _lastDragSnapSignature)
        {
            return result.Bounds;
        }
        _lastDragSnapSignature = snapSignature;

        if (result.HorizontalMatch is not null || result.VerticalMatch is not null)
        {
            ClearAllHighlights();
            if (result.VerticalMatch is { } verticalMatch)
            {
                ShowDragMatch(verticalMatch);
            }

            if (result.HorizontalMatch is { } horizontalMatch)
            {
                ShowDragMatch(horizontalMatch);
            }
        }
        else
        {
            ClearAllHighlights();
        }

        return result.Bounds;
    }

    private void ShowDragMatch(WidgetSnapMatch match)
    {
        ShowHighlight(
            _draggingWidgetHwnd,
            _draggingWidgetRoot,
            ToOverlayEdge(match.SourceEdge));
        if (match.TargetWindowHandle == IntPtr.Zero)
        {
            return;
        }

        FrameworkElement? targetRoot = App.Current?.WidgetManager
            ?.GetWidgetRootElementByHandle(match.TargetWindowHandle);
        if (targetRoot is null)
        {
            return;
        }

        ShowHighlight(
            match.TargetWindowHandle,
            targetRoot,
            ToOverlayEdge(match.TargetEdge));
        _currentTargetHwnd = match.TargetWindowHandle;
        _currentTargetRoot = targetRoot;
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
        _currentDragHorizontalMatch = null;
        _currentDragVerticalMatch = null;
        _dragSnapTargets.Clear();
        _resizeWorkAreaBounds = null;

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
