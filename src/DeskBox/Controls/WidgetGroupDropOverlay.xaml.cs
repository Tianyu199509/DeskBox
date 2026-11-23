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
/// Windows 11 Fluent drop preview drawn over a target widget while another
/// widget is dragged onto it to form a group. Preserves the target's header
/// clarity with no full-window wash, outlines the outer surface at its exact
/// corner radius, and presents an 8 DIP acrylic flyout chip (16 DIP countdown
/// ring or status glyph + regular caption) kept clear of the dragged widget's
/// shrunken thumbnail.
/// </summary>
public sealed partial class WidgetGroupDropOverlay : UserControl
{
    private const float RingSize = 16f;
    private const float RingStrokeThickness = 1.6f;
    private const double EstimatedBadgeHeight = 34;
    private const double ThumbnailClearanceAbovePointer = 14;
    private const double ThumbnailEstimatedSpanBelowPointer = 152;
    private const double EdgePadding = 10;

    private WidgetGroupDropPreviewState _state = WidgetGroupDropPreviewState.Hidden;
    private double _lastTitleBottom;
    private ShapeVisual? _ringVisual;
    private CompositionEllipseGeometry? _ringGeometry;
    private CompositionColorBrush? _ringBrush;
    private CompositionColorBrush? _ringTrackBrush;
    private ScalarKeyFrameAnimation? _countdownAnimation;
    private Vector3KeyFrameAnimation? _readyPopAnimation;

    public WidgetGroupDropOverlay()
    {
        InitializeComponent();
        ActualThemeChanged += OnActualThemeChanged;
        SizeChanged += OnLayoutMetricsChanged;
        BadgeBorder.SizeChanged += OnLayoutMetricsChanged;
    }

    /// <summary>Whether a preview is currently on screen.</summary>
    public bool IsShowing => _state.Visible;

    public void SetCornerRadius(CornerRadius cornerRadius)
    {
        if (MaskBorder.CornerRadius != cornerRadius)
        {
            MaskBorder.CornerRadius = cornerRadius;
        }
    }

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
        SetCornerRadius(cornerRadius);

        _lastTitleBottom = ResolveTitleBottom(titleElement);
        PlaceBadge(state.PointerOffsetY, _lastTitleBottom);

        bool visualStateChanged =
            !previous.Visible ||
            previous.Ready != state.Ready ||
            previous.Blocked != state.Blocked ||
            previous.Zone != state.Zone ||
            !string.Equals(previous.Message, state.Message, StringComparison.Ordinal);
        if (visualStateChanged)
        {
            ApplyGroupDropPreviewAppearance(state);
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
        if (countdownChanged || visualStateChanged)
        {
            UpdateRingAndIcon(state, restartCountdown: countdownChanged);
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
        if (_ringVisual is not null)
        {
            _ringVisual.IsVisible = false;
        }

        Visibility = Visibility.Collapsed;
        Opacity = 0;
    }

    /// <summary>Permanent teardown: drops the composition ring and cached animations.</summary>
    public void Release()
    {
        Hide();
        ActualThemeChanged -= OnActualThemeChanged;
        SizeChanged -= OnLayoutMetricsChanged;
        BadgeBorder.SizeChanged -= OnLayoutMetricsChanged;
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

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_state.Visible)
        {
            ApplyGroupDropPreviewAppearance(_state);
        }
    }

    private void OnLayoutMetricsChanged(object sender, SizeChangedEventArgs e)
    {
        if (_state.Visible)
        {
            PlaceBadge(_state.PointerOffsetY, _lastTitleBottom);
        }
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
        StateLabel.MaxWidth = Math.Max(72, width - 76);

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
            // from the cursor: keep the chip clear of [pointerY - 12, pointerY + 152].
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

    private double ResolveTitleBottom(FrameworkElement? titleElement)
    {
        if (titleElement is not { ActualWidth: > 0, ActualHeight: > 0 })
        {
            return 0;
        }

        try
        {
            Windows.Foundation.Point origin = titleElement.TransformToVisual(this)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            return Math.Max(0, origin.Y + titleElement.ActualHeight);
        }
        catch
        {
            return 0;
        }
    }

    private void ApplyGroupDropPreviewAppearance(WidgetGroupDropPreviewState state)
    {
        // Where a dragged widget would land is a transient drag state: the
        // single outer frame and countdown ring draw from the neutral palette,
        // while the chip plate uses WinUI's scoped in-app acrylic resource.
        Color line = NeutralInteractionBrush.Line(this);
        bool isDark = NeutralInteractionBrush.IsDarkTheme(this);
        bool highContrast = WindowsCompatibilityService.IsHighContrast;

        Color frameColor = highContrast
            ? Color.FromArgb(0xFF, line.R, line.G, line.B)
            : WithAlpha(line, state.Ready ? (byte)0xD8 : state.Blocked ? (byte)0x58 : (byte)0x78);
        MaskBorder.BorderBrush = SharedBrushCache.GetOrCreate(frameColor);
        MaskBorder.BorderThickness = new Thickness(highContrast ? 2 : state.Ready ? 1.5 : 1);

        Color fallbackPrimary = highContrast
            ? Color.FromArgb(0xFF, line.R, line.G, line.B)
            : isDark
                ? Colors.White
                : Color.FromArgb(0xE4, 0, 0, 0);
        Color fallbackSecondary = highContrast
            ? fallbackPrimary
            : isDark
                ? Color.FromArgb(0xC5, 255, 255, 255)
                : Color.FromArgb(0x93, 0, 0, 0);

        Brush primaryBrush = NeutralInteractionBrush.ResolveThemedResource("TextFillColorPrimaryBrush", this)
            ?? SharedBrushCache.GetOrCreate(fallbackPrimary);
        Brush secondaryBrush = NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", this)
            ?? SharedBrushCache.GetOrCreate(fallbackSecondary);

        StateLabel.Foreground = state.Ready || state.Blocked
            ? primaryBrush
            : secondaryBrush;

        if (state.Blocked && !highContrast)
        {
            StateIcon.Foreground = NeutralInteractionBrush.ResolveThemedResource("SystemFillColorCautionBrush", this)
                ?? SharedBrushCache.GetOrCreate(Color.FromArgb(0xFF, 157, 93, 0));
        }
        else
        {
            StateIcon.Foreground = primaryBrush;
        }

        Color ringColor = (primaryBrush as SolidColorBrush)?.Color ?? fallbackPrimary;
        if (_ringBrush is not null)
        {
            _ringBrush.Color = highContrast ? ringColor : WithAlpha(ringColor, 0xD8);
        }

        if (_ringTrackBrush is not null)
        {
            _ringTrackBrush.Color = WithAlpha(line, 0x38);
        }
    }

    private void UpdateRingAndIcon(WidgetGroupDropPreviewState state, bool restartCountdown)
    {
        if (state.Blocked || state.Ready || state.Dwell <= TimeSpan.Zero)
        {
            _ringGeometry?.StopAnimation("TrimEnd");
            if (_ringVisual is not null)
            {
                _ringVisual.IsVisible = false;
            }

            StateIcon.Glyph = state.Blocked ? "\uE7BA" : "\uE73E";
            StateIcon.Visibility = Visibility.Visible;
            return;
        }

        if (!EnsureRing())
        {
            StateIcon.Glyph = "\uE8F1";
            StateIcon.Visibility = Visibility.Visible;
            return;
        }

        StateIcon.Visibility = Visibility.Collapsed;
        _ringVisual!.IsVisible = true;
        if (!restartCountdown)
        {
            return;
        }

        _ringGeometry!.StopAnimation("TrimEnd");
        if (!WindowsCompatibilityService.ShouldAnimate)
        {
            _ringGeometry.TrimEnd = 0.65f;
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
            bool highContrast = WindowsCompatibilityService.IsHighContrast;
            Color fg = highContrast
                ? Color.FromArgb(0xFF, line.R, line.G, line.B)
                : isDark
                    ? Color.FromArgb(0xD8, 255, 255, 255)
                    : Color.FromArgb(0xC2, 0, 0, 0);
            float center = RingSize / 2f;
            float radius = center - (RingStrokeThickness / 2f) - 0.5f;

            CompositionEllipseGeometry trackGeometry = compositor.CreateEllipseGeometry();
            trackGeometry.Center = new Vector2(center, center);
            trackGeometry.Radius = new Vector2(radius, radius);
            _ringTrackBrush = compositor.CreateColorBrush(WithAlpha(line, 0x38));
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
            pop.InsertKeyFrame(0f, new Vector3(0.94f, 0.94f, 1f));
            pop.InsertKeyFrame(
                1f,
                Vector3.One,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1.12f)));
            pop.Duration = TimeSpan.FromMilliseconds(165);
            _readyPopAnimation = pop;
        }

        visual.StartAnimation("Scale", _readyPopAnimation);
    }

    private static Color WithAlpha(Color color, byte alpha) =>
        Color.FromArgb((byte)(color.A * alpha / 255), color.R, color.G, color.B);
}
