using System.Numerics;
using DeskBox.Helpers;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.Controls;

/// <summary>
/// Drop preview drawn over a widget while another widget is dragged onto it
/// to form a group. Rather than drawing an inner box across the target's icons,
/// the target's content layer dims in place and this overlay presents a crisp,
/// solid pill badge (countdown ring + caption) kept clear of the dragged
/// widget's shrunken thumbnail. All tones draw from the neutral palette.
/// </summary>
public sealed partial class WidgetGroupDropOverlay : UserControl
{
    private const float RingSize = 24f;
    private const float RingStrokeThickness = 2.5f;
    private const double EstimatedBadgeHeight = 40;
    private const double ThumbnailClearanceAbovePointer = 14;
    private const double ThumbnailEstimatedSpanBelowPointer = 152;
    private const double EdgePadding = 10;

    private WidgetGroupDropPreviewState _state = WidgetGroupDropPreviewState.Hidden;
    private ShapeVisual? _ringVisual;
    private CompositionEllipseGeometry? _ringGeometry;
    private CompositionColorBrush? _ringBrush;
    private CompositionColorBrush? _ringTrackBrush;
    private ScalarKeyFrameAnimation? _countdownAnimation;
    private Vector3KeyFrameAnimation? _readyPopAnimation;

    public WidgetGroupDropOverlay()
    {
        InitializeComponent();
    }

    /// <summary>Whether a preview is currently on screen.</summary>
    public bool IsShowing => _state.Visible;

    public void Apply(
        WidgetGroupDropPreviewState state,
        FrameworkElement? titleElement,
        CornerRadius cornerRadius)
    {
        WidgetGroupDropPreviewState previous = _state;
        if (!state.Visible)
        {
            Hide();
            return;
        }

        _state = state;
        Visibility = Visibility.Visible;
        Opacity = 1;
        MaskBorder.CornerRadius = cornerRadius;

        double titleBottom = PlaceTitleHighlight(
            state.Zone == WidgetGroupDropPreviewZone.Title ? titleElement : null,
            titleElement);
        PlaceBadge(state.PointerOffsetY, titleBottom);

        bool visualStateChanged =
            !previous.Visible ||
            previous.Ready != state.Ready ||
            previous.Blocked != state.Blocked ||
            previous.Zone != state.Zone ||
            !string.Equals(previous.Message, state.Message, StringComparison.Ordinal);
        if (visualStateChanged)
        {
            ApplyGroupDropPreviewAppearance(state);
            StateIcon.Glyph = state.Blocked
                ? "\uE7BA"
                : state.Ready
                    ? "\uE73E"
                    : "\uE8F1";
            StateLabel.Text = state.Message ?? string.Empty;
            StateLabel.Visibility = string.IsNullOrWhiteSpace(state.Message)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        bool countdownChanged =
            !previous.Visible ||
            previous.Zone != state.Zone ||
            previous.Ready != state.Ready ||
            previous.Blocked != state.Blocked ||
            previous.Dwell != state.Dwell;
        if (countdownChanged)
        {
            UpdateRing(state);
        }

        if (state.Ready && !previous.Ready)
        {
            PlayReadyPop();
        }
    }

    public void Hide()
    {
        if (!_state.Visible && Visibility == Visibility.Collapsed)
        {
            return;
        }

        _state = WidgetGroupDropPreviewState.Hidden;
        _ringGeometry?.StopAnimation("TrimEnd");
        Visibility = Visibility.Collapsed;
        Opacity = 0;
    }

    /// <summary>Permanent teardown: drops the composition ring and cached animations.</summary>
    public void Release()
    {
        Hide();
        try
        {
            ElementCompositionPreview.SetElementChildVisual(RingHost, null);
        }
        catch
        {
            // The host may already be detached during window teardown.
        }

        _countdownAnimation?.Dispose();
        _readyPopAnimation?.Dispose();
        _ringVisual?.Dispose();
        _ringGeometry?.Dispose();
        _ringBrush?.Dispose();
        _ringTrackBrush?.Dispose();
        _countdownAnimation = null;
        _readyPopAnimation = null;
        _ringVisual = null;
        _ringGeometry = null;
        _ringBrush = null;
        _ringTrackBrush = null;
    }

    private void PlaceBadge(double pointerOffsetY, double titleBottom)
    {
        double height = ActualHeight;
        double width = ActualWidth;
        if (height < EstimatedBadgeHeight + 8 || width < 64)
        {
            BadgeBorder.Visibility = Visibility.Collapsed;
            return;
        }

        BadgeBorder.Visibility = Visibility.Visible;
        StateLabel.MaxWidth = Math.Max(72, width - 84);

        double badgeHeight = BadgeBorder.ActualHeight > 0
            ? BadgeBorder.ActualHeight
            : EstimatedBadgeHeight;
        double minTop = Math.Max(EdgePadding, titleBottom + 6);
        double maxTop = Math.Max(minTop, height - badgeHeight - EdgePadding);
        double centeredTop = Math.Clamp(
            ((minTop + height) - badgeHeight) * 0.5,
            minTop,
            maxTop);

        double targetTop = centeredTop;
        if (double.IsFinite(pointerOffsetY))
        {
            // The dragged widget is grabbed by its top bar and hangs downward
            // from the cursor: keep the pill clear of [pointerY - 12, pointerY + 152].
            double occludedTop = pointerOffsetY - 12;
            double occludedBottom = pointerOffsetY + ThumbnailEstimatedSpanBelowPointer;
            bool overlapsCentered =
                centeredTop + badgeHeight > occludedTop &&
                centeredTop < occludedBottom;
            if (overlapsCentered)
            {
                double aboveCursorTop = pointerOffsetY - badgeHeight - ThumbnailClearanceAbovePointer;
                if (aboveCursorTop >= minTop)
                {
                    targetTop = Math.Min(maxTop, aboveCursorTop);
                }
                else
                {
                    double belowThumbnailTop = occludedBottom + 8;
                    if (belowThumbnailTop <= maxTop)
                    {
                        targetTop = belowThumbnailTop;
                    }
                }
            }
        }

        BadgeTranslate.Y = Math.Round(targetTop);
    }

    private double PlaceTitleHighlight(
        FrameworkElement? highlightTarget,
        FrameworkElement? anyTitleElement)
    {
        double titleBottom = 0;
        if (anyTitleElement is { ActualWidth: > 0, ActualHeight: > 0 })
        {
            try
            {
                Windows.Foundation.Point origin = anyTitleElement.TransformToVisual(this)
                    .TransformPoint(new Windows.Foundation.Point(0, 0));
                titleBottom = Math.Max(0, origin.Y + anyTitleElement.ActualHeight);
                if (highlightTarget is not null)
                {
                    TitleHighlight.Margin = new Thickness(origin.X, origin.Y, 0, 0);
                    TitleHighlight.Width = highlightTarget.ActualWidth;
                    TitleHighlight.Height = highlightTarget.ActualHeight;
                    TitleHighlight.Visibility = Visibility.Visible;
                    return titleBottom;
                }
            }
            catch
            {
                // Detached subtree; fall through to hide highlight.
            }
        }

        TitleHighlight.Visibility = Visibility.Collapsed;
        return titleBottom;
    }

    private void ApplyGroupDropPreviewAppearance(WidgetGroupDropPreviewState state)
    {
        // Where a dragged widget would land is a drag state, so the wash,
        // outline, pill stroke, ring and caption draw from the neutral palette.
        Color line = NeutralInteractionBrush.Line(this);
        Color fill = NeutralInteractionBrush.Fill(this);
        bool isDark = NeutralInteractionBrush.IsDarkTheme(this);
        bool highContrast = WindowsCompatibilityService.IsHighContrast;

        byte washAlpha = highContrast
            ? (byte)0
            : state.Ready ? (byte)0x42 : state.Blocked ? (byte)0x1E : (byte)0x2C;
        MaskBorder.Background = SharedBrushCache.GetOrCreate(
            Color.FromArgb(washAlpha, fill.R, fill.G, fill.B));
        MaskBorder.BorderBrush = SharedBrushCache.GetOrCreate(
            WithAlpha(line, state.Ready ? (byte)0xF0 : state.Blocked ? (byte)0x70 : (byte)0x98));
        MaskBorder.BorderThickness = new Thickness(state.Ready ? 2 : 1.5);

        TitleHighlight.BorderBrush = SharedBrushCache.GetOrCreate(WithAlpha(line, 0xEC));

        Color pillFill = highContrast
            ? (isDark ? Colors.Black : Colors.White)
            : isDark
                ? Color.FromArgb(state.Ready ? (byte)0xF4 : (byte)0xE8, 28, 30, 36)
                : Color.FromArgb(state.Ready ? (byte)0xF6 : (byte)0xEE, 250, 251, 253);
        BadgeBorder.Background = SharedBrushCache.GetOrCreate(pillFill);
        BadgeBorder.BorderBrush = SharedBrushCache.GetOrCreate(
            WithAlpha(line, state.Ready ? (byte)0xD8 : (byte)0x68));

        Color foregroundColor = highContrast
            ? WithAlpha(line, 0xFF)
            : isDark
                ? Color.FromArgb(state.Ready ? (byte)0xFF : (byte)0xEA, 255, 255, 255)
                : Color.FromArgb(state.Ready ? (byte)0xF4 : (byte)0xDC, 20, 22, 26);
        SolidColorBrush contentBrush = SharedBrushCache.GetOrCreate(foregroundColor);
        StateIcon.Foreground = contentBrush;
        StateLabel.Foreground = contentBrush;

        if (_ringBrush is not null)
        {
            _ringBrush.Color = foregroundColor;
        }

        if (_ringTrackBrush is not null)
        {
            _ringTrackBrush.Color = WithAlpha(line, 0x3C);
        }
    }

    private void UpdateRing(WidgetGroupDropPreviewState state)
    {
        if (!EnsureRing())
        {
            return;
        }

        _ringGeometry!.StopAnimation("TrimEnd");
        if (state.Blocked)
        {
            _ringVisual!.IsVisible = false;
            return;
        }

        _ringVisual!.IsVisible = true;
        if (state.Ready || state.Dwell <= TimeSpan.Zero)
        {
            _ringGeometry.TrimEnd = 1f;
            return;
        }

        if (!WindowsCompatibilityService.ShouldAnimate)
        {
            _ringGeometry.TrimEnd = 0f;
            return;
        }

        Compositor compositor = _ringGeometry.Compositor;
        if (_countdownAnimation is null)
        {
            ScalarKeyFrameAnimation countdown = compositor.CreateScalarKeyFrameAnimation();
            countdown.InsertKeyFrame(0f, 0f);
            countdown.InsertKeyFrame(1f, 1f, compositor.CreateLinearEasingFunction());
            _countdownAnimation = countdown;
        }

        _countdownAnimation.Duration = state.Dwell;
        _ringGeometry.TrimEnd = 0f;
        _ringGeometry.StartAnimation("TrimEnd", _countdownAnimation);
    }

    private bool EnsureRing()
    {
        if (_ringVisual is not null)
        {
            return true;
        }

        try
        {
            Compositor compositor = ElementCompositionPreview.GetElementVisual(RingHost).Compositor;
            Color line = NeutralInteractionBrush.Line(this);
            bool isDark = NeutralInteractionBrush.IsDarkTheme(this);
            Color fg = isDark ? Colors.White : Color.FromArgb(0xEC, 20, 22, 26);
            float center = RingSize / 2f;
            float radius = center - (RingStrokeThickness / 2f) - 0.5f;

            CompositionEllipseGeometry trackGeometry = compositor.CreateEllipseGeometry();
            trackGeometry.Center = new Vector2(center, center);
            trackGeometry.Radius = new Vector2(radius, radius);
            _ringTrackBrush = compositor.CreateColorBrush(WithAlpha(line, 0x3C));
            CompositionSpriteShape track = compositor.CreateSpriteShape(trackGeometry);
            track.StrokeBrush = _ringTrackBrush;
            track.StrokeThickness = RingStrokeThickness;

            _ringGeometry = compositor.CreateEllipseGeometry();
            _ringGeometry.Center = new Vector2(center, center);
            _ringGeometry.Radius = new Vector2(radius, radius);
            _ringBrush = compositor.CreateColorBrush(fg);
            CompositionSpriteShape progress = compositor.CreateSpriteShape(_ringGeometry);
            progress.StrokeBrush = _ringBrush;
            progress.StrokeThickness = RingStrokeThickness;
            progress.StrokeStartCap = CompositionStrokeCap.Round;
            progress.StrokeEndCap = CompositionStrokeCap.Round;

            _ringVisual = compositor.CreateShapeVisual();
            _ringVisual.Size = new Vector2(RingSize, RingSize);
            _ringVisual.CenterPoint = new Vector3(center, center, 0f);
            _ringVisual.RotationAngleInDegrees = -90f;
            _ringVisual.Shapes.Add(track);
            _ringVisual.Shapes.Add(progress);
            ElementCompositionPreview.SetElementChildVisual(RingHost, _ringVisual);
            return true;
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[WidgetGroupDrop] Countdown ring unavailable: {ex.Message}");
            return false;
        }
    }

    private void PlayReadyPop()
    {
        if (!WindowsCompatibilityService.ShouldAnimate ||
            BadgeBorder.ActualWidth <= 0 ||
            BadgeBorder.ActualHeight <= 0)
        {
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(BadgeBorder);
        Compositor compositor = visual.Compositor;
        visual.CenterPoint = new Vector3(
            (float)BadgeBorder.ActualWidth * 0.5f,
            (float)BadgeBorder.ActualHeight * 0.5f,
            0f);
        if (_readyPopAnimation is null)
        {
            Vector3KeyFrameAnimation pop = compositor.CreateVector3KeyFrameAnimation();
            pop.InsertKeyFrame(0f, new Vector3(0.92f, 0.92f, 1f));
            pop.InsertKeyFrame(
                1f,
                Vector3.One,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1.18f)));
            pop.Duration = TimeSpan.FromMilliseconds(165);
            _readyPopAnimation = pop;
        }

        visual.StartAnimation("Scale", _readyPopAnimation);
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb((byte)(color.A * alpha / 255), color.R, color.G, color.B);
}
