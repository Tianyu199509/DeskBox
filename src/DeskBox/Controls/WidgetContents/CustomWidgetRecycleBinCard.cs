using System.Numerics;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.UI;
using WinUIPath = Microsoft.UI.Xaml.Shapes.Path;

namespace DeskBox.Controls.WidgetContents;

/// <summary>
/// First-class Windows 11 Fluent 2 Recycle Bin micro-surface for Custom Widgets.
/// Combines event-driven Shell empty/full artwork and item count with compositor-driven
/// tactile drag-to-delete animations (hinged lid opening on hover, elastic swallow rebound
/// on drop, and badge count pop) plus hover-to-reveal confirmed Empty Bin action.
/// </summary>
internal sealed partial class CustomWidgetRecycleBinCard : Grid, IDisposable
{
    private readonly Func<IntPtr> _ownerHwndResolver;
    private readonly Func<string> _cultureResolver;
    private readonly bool _isInteractiveAllowed;

    private readonly Border _cardSurface;
    private readonly Border _dangerGlowOverlay;
    private readonly Grid _iconStage;
    private readonly Grid _restIconLayer;
    private readonly Image _shellIconImage;
    private readonly WinUIPath _fallbackGlyphPath;
    private readonly Canvas _articulatedTrashCanvas;
    private readonly WinUIPath _articulatedLidPath;
    private readonly WinUIPath _articulatedBodyPath;
    private readonly TextBlock _titleTextBlock;
    private readonly Border _countBadge;
    private readonly TextBlock _countTextBlock;
    private readonly Button _clearButton;
    private readonly FontIcon _clearButtonIcon;

    private IDisposable? _serviceSubscription;
    private BitmapImage? _decodedEmptyBitmap;
    private BitmapImage? _decodedFullBitmap;
    private byte[]? _lastEmptyPngRef;
    private byte[]? _lastFullPngRef;
    private int _iconDecodeGeneration;

    private long _itemCount = -1;
    private string _restLabelText;
    private string _dropHintText;
    private string _clearTooltipText;
    private bool _isPointerHovered;
    private bool _dropHoverActive;
    private bool _clearDialogOpen;
    private bool _isDisposed;

    private Brush? _hostRestBackground;
    private Brush? _hostHoverBackground;
    private Brush? _hostBorderBrush;

    public CustomWidgetRecycleBinCard(
        string initialTitle,
        string initialDropHint,
        string initialClearTooltip,
        double bodyFontSize,
        double captionFontSize,
        bool isDark,
        Func<IntPtr> ownerHwndResolver,
        Func<string> cultureResolver,
        bool isInteractiveAllowed = true,
        CornerRadius? hostCornerRadius = null,
        Thickness? hostBorderThickness = null,
        Brush? hostRestBackground = null,
        Brush? hostHoverBackground = null,
        Brush? hostBorderBrush = null)
    {
        _ownerHwndResolver = ownerHwndResolver ?? (static () => IntPtr.Zero);
        _cultureResolver = cultureResolver ?? LocalizationService.ResolveAmbientCultureName;
        _isInteractiveAllowed = isInteractiveAllowed;
        _restLabelText = initialTitle;
        _dropHintText = initialDropHint;
        _clearTooltipText = initialClearTooltip;
        _hostRestBackground = hostRestBackground;
        _hostHoverBackground = hostHoverBackground;
        _hostBorderBrush = hostBorderBrush;
        CornerRadius effectiveCorner = hostCornerRadius ?? new CornerRadius(8);
        Thickness effectiveBorderThickness = hostBorderThickness ?? new Thickness(1);

        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        // 1. Icon Stage: Rest Shell Icon + Articulated Lid/Body Vector Trash Can
        _fallbackGlyphPath = new WinUIPath
        {
            Data = BuildTrashBodyAndLidCombinedGeometry(),
            StrokeThickness = 1.35,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _shellIconImage = new Image
        {
            Width = 16,
            Height = 16,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _restIconLayer = new Grid
        {
            Width = 18,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _restIconLayer.Children.Add(_fallbackGlyphPath);
        _restIconLayer.Children.Add(_shellIconImage);

        _articulatedLidPath = new WinUIPath
        {
            Data = BuildArticulatedLidGeometry(),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };
        _articulatedBodyPath = new WinUIPath
        {
            Data = BuildArticulatedBodyGeometry(),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };

        _articulatedTrashCanvas = new Canvas
        {
            Width = 18,
            Height = 18,
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _articulatedTrashCanvas.Children.Add(_articulatedBodyPath);
        _articulatedTrashCanvas.Children.Add(_articulatedLidPath);

        _iconStage = new Grid
        {
            Width = 20,
            Height = 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        _iconStage.Children.Add(_restIconLayer);
        _iconStage.Children.Add(_articulatedTrashCanvas);

        // 2. Title / Drop Hint Label
        _titleTextBlock = new TextBlock
        {
            Text = _restLabelText,
            FontSize = bodyFontSize,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            MaxLines = 1,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        // 3. Right Slot: Count Pill Badge & Hover-Reveal Clear Button
        _countTextBlock = new TextBlock
        {
            FontSize = Math.Max(10.5, captionFontSize - 0.5),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Typography.SetNumeralAlignment(_countTextBlock, FontNumeralAlignment.Tabular);

        _countBadge = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(7, 1.5, 7, 1.5),
            MinWidth = 20,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            Child = _countTextBlock
        };

        _clearButtonIcon = new FontIcon
        {
            Glyph = "\uEA99", // Broom / Clean glyph (clear recycle bin)
            FontSize = 12.5
        };
        _clearButton = new Button
        {
            Width = 26,
            Height = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
            IsHitTestVisible = false,
            IsTabStop = false,
            Content = _clearButtonIcon
        };
        ToolTipService.SetToolTip(_clearButton, _clearTooltipText);
        AutomationProperties.SetName(_clearButton, _clearTooltipText);

        var rightActionHost = new Grid
        {
            MinWidth = 28,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        rightActionHost.Children.Add(_countBadge);
        rightActionHost.Children.Add(_clearButton);

        var contentRow = new Grid
        {
            Padding = new Thickness(11, 6, 8, 6),
            ColumnSpacing = 8,
            MinHeight = 36
        };
        contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        contentRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_iconStage, 0);
        Grid.SetColumn(_titleTextBlock, 1);
        Grid.SetColumn(rightActionHost, 2);
        contentRow.Children.Add(_iconStage);
        contentRow.Children.Add(_titleTextBlock);
        contentRow.Children.Add(rightActionHost);

        // Soft danger glow layer (translucent coral tint + border during file drag hover)
        _dangerGlowOverlay = new Border
        {
            CornerRadius = effectiveCorner,
            BorderThickness = new Thickness(1.5),
            Opacity = 0,
            IsHitTestVisible = false
        };

        var layeredGrid = new Grid();
        layeredGrid.Children.Add(_dangerGlowOverlay);
        layeredGrid.Children.Add(contentRow);

        _cardSurface = new Border
        {
            CornerRadius = effectiveCorner,
            BorderThickness = effectiveBorderThickness,
            MinHeight = 36,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            AllowDrop = _isInteractiveAllowed,
            Child = layeredGrid
        };
        AutomationProperties.SetName(_cardSurface, _restLabelText);
        Children.Add(_cardSurface);

        ApplyTheme(isDark);

        if (_isInteractiveAllowed)
        {
            UiElementCursorHelper.SetCursor(_cardSurface, Microsoft.UI.Input.InputSystemCursorShape.Hand);
            UiElementCursorHelper.SetCursor(_clearButton, Microsoft.UI.Input.InputSystemCursorShape.Hand);

            _cardSurface.PointerEntered += OnCardPointerEntered;
            _cardSurface.PointerExited += OnCardPointerExited;
            _cardSurface.Tapped += OnCardTapped;
            _cardSurface.DragOver += OnCardXamlDragOver;
            _cardSurface.DragLeave += OnCardXamlDragLeave;
            _cardSurface.Drop += OnCardXamlDrop;
            _clearButton.Click += OnClearButtonClick;
        }

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += OnActualThemeChanged;
    }

    public long CurrentItemCount => Math.Max(0, _itemCount);

    public bool IsDropHoverActive => _dropHoverActive;

    public void UpdateLocalizedCopy(string title, string dropHint, string clearTooltip)
    {
        _restLabelText = title;
        _dropHintText = dropHint;
        _clearTooltipText = clearTooltip;
        ToolTipService.SetToolTip(_clearButton, _clearTooltipText);
        AutomationProperties.SetName(_clearButton, _clearTooltipText);
        AutomationProperties.SetName(_cardSurface, _restLabelText);
        if (!_dropHoverActive)
        {
            _titleTextBlock.Text = _restLabelText;
        }
        else
        {
            _titleTextBlock.Text = _dropHintText;
        }
    }

    public void ApplyTheme(bool isDark)
    {
        Color restBg = isDark ? Color.FromArgb(18, 255, 255, 255) : Color.FromArgb(12, 255, 255, 255);
        Color hoverBg = isDark ? Color.FromArgb(30, 255, 255, 255) : Color.FromArgb(24, 0, 0, 0);
        Color restStroke = isDark ? Color.FromArgb(22, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0);

        Brush restSurfaceBrush = _hostRestBackground ?? new SolidColorBrush(restBg);
        Brush hoverSurfaceBrush = _hostHoverBackground ?? new SolidColorBrush(hoverBg);
        _cardSurface.Background = _isPointerHovered && !_dropHoverActive ? hoverSurfaceBrush : restSurfaceBrush;
        _cardSurface.BorderBrush = _hostBorderBrush ?? new SolidColorBrush(restStroke);

        Color coralStroke = isDark ? Color.FromArgb(235, 248, 113, 113) : Color.FromArgb(230, 220, 38, 38);
        Color coralFill = isDark ? Color.FromArgb(46, 239, 68, 68) : Color.FromArgb(28, 239, 68, 68);
        _dangerGlowOverlay.Background = new SolidColorBrush(coralFill);
        _dangerGlowOverlay.BorderBrush = new SolidColorBrush(coralStroke);

        Brush dangerFg = new SolidColorBrush(isDark ? Color.FromArgb(255, 252, 165, 165) : Color.FromArgb(255, 185, 28, 28));
        Brush restFg = new SolidColorBrush(isDark ? Color.FromArgb(232, 255, 255, 255) : Color.FromArgb(228, 15, 23, 42));
        Brush mutedFg = new SolidColorBrush(isDark ? Color.FromArgb(170, 255, 255, 255) : Color.FromArgb(156, 51, 65, 85));

        _titleTextBlock.Foreground = _dropHoverActive ? dangerFg : restFg;
        _fallbackGlyphPath.Stroke = mutedFg;
        _articulatedLidPath.Stroke = new SolidColorBrush(coralStroke);
        _articulatedBodyPath.Stroke = new SolidColorBrush(coralStroke);
        _clearButtonIcon.Foreground = mutedFg;

        Color badgeBg = isDark ? Color.FromArgb(42, 96, 165, 250) : Color.FromArgb(30, 37, 99, 235);
        Color badgeFg = isDark ? Color.FromArgb(255, 147, 197, 253) : Color.FromArgb(255, 29, 78, 216);
        _countBadge.Background = new SolidColorBrush(badgeBg);
        _countTextBlock.Foreground = new SolidColorBrush(badgeFg);
    }

    /// <summary>
    /// DPI-accurate screen-space hit-test used by the hosting window's native Win32 OLE
    /// <c>IDropTarget</c> pipeline so Explorer/Desktop file drags only activate deletion
    /// when hovering directly over this Recycle Bin card.
    /// </summary>
    public bool ContainsScreenPoint(int screenX, int screenY, IntPtr hostHwnd)
    {
        if (_isDisposed ||
            !IsLoaded ||
            Visibility != Visibility.Visible ||
            _cardSurface.ActualWidth <= 0 ||
            _cardSurface.ActualHeight <= 0 ||
            hostHwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            GeneralTransform transform = _cardSurface.TransformToVisual(null);
            Rect bounds = transform.TransformBounds(
                new Rect(0, 0, _cardSurface.ActualWidth, _cardSurface.ActualHeight));

            var clientPt = new Win32Helper.POINT { X = screenX, Y = screenY };
            if (!Win32Helper.ScreenToClient(hostHwnd, ref clientPt))
            {
                return false;
            }

            double scale = XamlRoot?.RasterizationScale ?? 1.0;
            if (scale <= 0)
            {
                scale = 1.0;
            }

            double dipX = clientPt.X / scale;
            double dipY = clientPt.Y / scale;
            return dipX >= bounds.Left && dipX <= bounds.Right &&
                   dipY >= bounds.Top && dipY <= bounds.Bottom;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Logical-state gated drag hover setter safe for 120Hz OLE <c>DragOver</c> streams.
    /// </summary>
    public void SetDragHover(bool active)
    {
        if (_isDisposed || active == _dropHoverActive)
        {
            return;
        }

        _dropHoverActive = active;
        bool isDark = ResolveIsDark();

        if (active)
        {
            _titleTextBlock.Text = _dropHintText;
            _clearButton.Opacity = 0;
            _clearButton.IsHitTestVisible = false;
            _countBadge.Opacity = 0.35;
        }
        else
        {
            _titleTextBlock.Text = _restLabelText;
            SyncHoverAffordances();
        }

        ApplyTheme(isDark);
        AnimateDragHoverVisuals(active, durationMs: active ? 167 : 120);
    }

    internal bool IsDragHoverActive => _dropHoverActive;

    public async Task<int> HandleDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        PlaySwallowAnimation();
        if (paths is not { Count: > 0 })
        {
            return 0;
        }

        CustomWidgetRecycleBatchOutcome outcome = await CustomWidgetRecycleBinService.Shared
            .RecycleDroppedFilesWithUndoAsync(
                paths,
                _ownerHwndResolver(),
                _cultureResolver());
        return outcome.RecycledCount;
    }

    public void PlaySwallowAnimation()
    {
        _dropHoverActive = false;
        _titleTextBlock.Text = _restLabelText;
        ApplyTheme(ResolveIsDark());
        SyncHoverAffordances();

        if (!FluentMotionHelper.AreAnimationsEnabled || !IsLoaded)
        {
            ResetVisualTransformsImmediate();
            return;
        }

        try
        {
            Visual cardVis = ElementCompositionPreview.GetElementVisual(_cardSurface);
            Visual glowVis = ElementCompositionPreview.GetElementVisual(_dangerGlowOverlay);
            Visual restIconVis = ElementCompositionPreview.GetElementVisual(_restIconLayer);
            Visual artVis = ElementCompositionPreview.GetElementVisual(_articulatedTrashCanvas);
            Visual lidVis = ElementCompositionPreview.GetElementVisual(_articulatedLidPath);
            Visual bodyVis = ElementCompositionPreview.GetElementVisual(_articulatedBodyPath);
            Compositor comp = cardVis.Compositor;

            EnsureVisualCenters(cardVis, lidVis, bodyVis);

            // 1. Snap lid shut quickly (75ms)
            ScalarKeyFrameAnimation lidSnap = comp.CreateScalarKeyFrameAnimation();
            lidSnap.InsertKeyFrame(1f, 0f);
            lidSnap.Duration = TimeSpan.FromMilliseconds(75);
            lidVis.StartAnimation("RotationAngleInDegrees", lidSnap);

            Vector3KeyFrameAnimation lidOffsetSnap = comp.CreateVector3KeyFrameAnimation();
            lidOffsetSnap.InsertKeyFrame(1f, Vector3.Zero);
            lidOffsetSnap.Duration = TimeSpan.FromMilliseconds(75);
            lidVis.StartAnimation("Offset", lidOffsetSnap);

            // 2. Elastic swallow compress & rebound on trash body + card (240ms)
            CubicBezierEasingFunction springEase = comp.CreateCubicBezierEasingFunction(
                new Vector2(0.22f, 1.0f),
                new Vector2(0.36f, 1.0f));
            Vector3KeyFrameAnimation swallowScale = comp.CreateVector3KeyFrameAnimation();
            swallowScale.InsertKeyFrame(0.32f, new Vector3(1.10f, 0.84f, 1f));
            swallowScale.InsertKeyFrame(0.68f, new Vector3(0.96f, 1.06f, 1f), springEase);
            swallowScale.InsertKeyFrame(1.0f, Vector3.One, springEase);
            swallowScale.Duration = TimeSpan.FromMilliseconds(240);
            bodyVis.StartAnimation("Scale", swallowScale);

            Vector3KeyFrameAnimation cardBounce = comp.CreateVector3KeyFrameAnimation();
            cardBounce.InsertKeyFrame(0.30f, new Vector3(0.985f, 0.96f, 1f));
            cardBounce.InsertKeyFrame(0.70f, new Vector3(1.008f, 1.015f, 1f), springEase);
            cardBounce.InsertKeyFrame(1.0f, Vector3.One, springEase);
            cardBounce.Duration = TimeSpan.FromMilliseconds(230);
            cardVis.StartAnimation("Scale", cardBounce);

            // 3. Glow flash & release + crossfade back to filled Shell icon
            ScalarKeyFrameAnimation glowRelease = comp.CreateScalarKeyFrameAnimation();
            glowRelease.InsertKeyFrame(0.18f, 1.0f);
            glowRelease.InsertKeyFrame(1.0f, 0.0f);
            glowRelease.Duration = TimeSpan.FromMilliseconds(260);
            glowVis.StartAnimation("Opacity", glowRelease);

            ScalarKeyFrameAnimation artFadeOut = comp.CreateScalarKeyFrameAnimation();
            artFadeOut.InsertKeyFrame(0.65f, 1.0f);
            artFadeOut.InsertKeyFrame(1.0f, 0.0f);
            artFadeOut.Duration = TimeSpan.FromMilliseconds(260);
            artVis.StartAnimation("Opacity", artFadeOut);

            ScalarKeyFrameAnimation restFadeIn = comp.CreateScalarKeyFrameAnimation();
            restFadeIn.InsertKeyFrame(0.55f, 0.0f);
            restFadeIn.InsertKeyFrame(1.0f, 1.0f);
            restFadeIn.Duration = TimeSpan.FromMilliseconds(260);
            restIconVis.StartAnimation("Opacity", restFadeIn);
        }
        catch
        {
            ResetVisualTransformsImmediate();
        }
    }

    private void AnimateDragHoverVisuals(bool active, int durationMs)
    {
        if (!FluentMotionHelper.AreAnimationsEnabled || !IsLoaded)
        {
            _dangerGlowOverlay.Opacity = active ? 1 : 0;
            _articulatedTrashCanvas.Opacity = active ? 1 : 0;
            _restIconLayer.Opacity = active ? 0 : 1;
            return;
        }

        try
        {
            Visual cardVis = ElementCompositionPreview.GetElementVisual(_cardSurface);
            Visual glowVis = ElementCompositionPreview.GetElementVisual(_dangerGlowOverlay);
            Visual restIconVis = ElementCompositionPreview.GetElementVisual(_restIconLayer);
            Visual artVis = ElementCompositionPreview.GetElementVisual(_articulatedTrashCanvas);
            Visual lidVis = ElementCompositionPreview.GetElementVisual(_articulatedLidPath);
            Visual bodyVis = ElementCompositionPreview.GetElementVisual(_articulatedBodyPath);
            Compositor comp = cardVis.Compositor;

            EnsureVisualCenters(cardVis, lidVis, bodyVis);
            TimeSpan dur = TimeSpan.FromMilliseconds(durationMs);
            CubicBezierEasingFunction ease = comp.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 1.0f),
                new Vector2(0.30f, 1.0f));

            Vector3KeyFrameAnimation cardScale = comp.CreateVector3KeyFrameAnimation();
            cardScale.InsertKeyFrame(1f, active ? new Vector3(1.022f, 1.022f, 1f) : Vector3.One, ease);
            cardScale.Duration = dur;
            cardVis.StartAnimation("Scale", cardScale);

            ScalarKeyFrameAnimation glowAnim = comp.CreateScalarKeyFrameAnimation();
            glowAnim.InsertKeyFrame(1f, active ? 1f : 0f, ease);
            glowAnim.Duration = dur;
            glowVis.StartAnimation("Opacity", glowAnim);

            ScalarKeyFrameAnimation artOpacity = comp.CreateScalarKeyFrameAnimation();
            artOpacity.InsertKeyFrame(1f, active ? 1f : 0f, ease);
            artOpacity.Duration = dur;
            artVis.StartAnimation("Opacity", artOpacity);

            ScalarKeyFrameAnimation restOpacity = comp.CreateScalarKeyFrameAnimation();
            restOpacity.InsertKeyFrame(1f, active ? 0f : 1f, ease);
            restOpacity.Duration = dur;
            restIconVis.StartAnimation("Opacity", restOpacity);

            ScalarKeyFrameAnimation lidTilt = comp.CreateScalarKeyFrameAnimation();
            lidTilt.InsertKeyFrame(1f, active ? -20f : 0f, ease);
            lidTilt.Duration = dur;
            lidVis.StartAnimation("RotationAngleInDegrees", lidTilt);

            Vector3KeyFrameAnimation lidLift = comp.CreateVector3KeyFrameAnimation();
            lidLift.InsertKeyFrame(1f, active ? new Vector3(-0.6f, -1.4f, 0f) : Vector3.Zero, ease);
            lidLift.Duration = dur;
            lidVis.StartAnimation("Offset", lidLift);
        }
        catch
        {
            _dangerGlowOverlay.Opacity = active ? 1 : 0;
            _articulatedTrashCanvas.Opacity = active ? 1 : 0;
            _restIconLayer.Opacity = active ? 0 : 1;
        }
    }

    private void PlayBadgePopAnimation()
    {
        if (!FluentMotionHelper.AreAnimationsEnabled ||
            !IsLoaded ||
            _countBadge.Visibility != Visibility.Visible)
        {
            return;
        }

        try
        {
            Visual badgeVis = ElementCompositionPreview.GetElementVisual(_countBadge);
            Compositor comp = badgeVis.Compositor;
            float w = (float)Math.Max(20, _countBadge.ActualWidth);
            float h = (float)Math.Max(18, _countBadge.ActualHeight);
            badgeVis.CenterPoint = new Vector3(w * 0.5f, h * 0.5f, 0f);

            CubicBezierEasingFunction ease = comp.CreateCubicBezierEasingFunction(
                new Vector2(0.22f, 1.0f),
                new Vector2(0.36f, 1.0f));
            Vector3KeyFrameAnimation pop = comp.CreateVector3KeyFrameAnimation();
            pop.InsertKeyFrame(0.0f, new Vector3(0.86f, 0.86f, 1f));
            pop.InsertKeyFrame(0.45f, new Vector3(1.24f, 1.24f, 1f), ease);
            pop.InsertKeyFrame(1.0f, Vector3.One, ease);
            pop.Duration = TimeSpan.FromMilliseconds(200);
            badgeVis.StartAnimation("Scale", pop);
        }
        catch
        {
        }
    }

    private void EnsureVisualCenters(Visual cardVis, Visual lidVis, Visual bodyVis)
    {
        float cw = (float)Math.Max(48, _cardSurface.ActualWidth);
        float ch = (float)Math.Max(36, _cardSurface.ActualHeight);
        cardVis.CenterPoint = new Vector3(cw * 0.5f, ch * 0.5f, 0f);
        // Left hinge of lid (x=3, y=5 in 18x18 canvas)
        lidVis.CenterPoint = new Vector3(3.0f, 5.0f, 0f);
        // Bottom center base of trash body (x=9, y=15.5 in 18x18 canvas)
        bodyVis.CenterPoint = new Vector3(9.0f, 15.5f, 0f);
    }

    private void ResetVisualTransformsImmediate()
    {
        _dangerGlowOverlay.Opacity = 0;
        _articulatedTrashCanvas.Opacity = 0;
        _restIconLayer.Opacity = 1;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_isDisposed)
        {
            return;
        }

        _serviceSubscription?.Dispose();
        _serviceSubscription = CustomWidgetRecycleBinService.Shared.Subscribe(
            ApplyServiceState,
            DispatcherQueue);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isPointerHovered = false;
        SetDragHover(false);
        _serviceSubscription?.Dispose();
        _serviceSubscription = null;
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args) =>
        ApplyTheme(ResolveIsDark());

    private bool ResolveIsDark() => NeutralInteractionBrush.IsDarkTheme(this);

    internal void ApplyServiceState(CustomWidgetRecycleBinState state)
    {
        if (_isDisposed)
        {
            return;
        }

        long previousCount = _itemCount;
        _itemCount = Math.Max(0, state.ItemCount);
        string badgeText = state.CountBadgeText;
        _countTextBlock.Text = badgeText;
        _countBadge.Visibility = _itemCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (!_dropHoverActive)
        {
            SyncHoverAffordances();
        }

        if (previousCount >= 0 && _itemCount > previousCount)
        {
            PlayBadgePopAnimation();
        }

        _ = RefreshShellArtworkIfNeededAsync(state);
    }

    private async Task RefreshShellArtworkIfNeededAsync(CustomWidgetRecycleBinState state)
    {
        if (DispatcherQueue is not { } dq)
        {
            return;
        }

        int gen = ++_iconDecodeGeneration;
        if (state.EmptyIconPng is { Length: > 0 } emptyBytes && !ReferenceEquals(emptyBytes, _lastEmptyPngRef))
        {
            _lastEmptyPngRef = emptyBytes;
            BitmapImage? decodedEmpty = await IconHelper.CreateBitmapImageAsync(dq, emptyBytes, 32);
            if (_isDisposed || gen != _iconDecodeGeneration)
            {
                return;
            }
            _decodedEmptyBitmap = decodedEmpty;
        }

        if (state.FullIconPng is { Length: > 0 } fullBytes && !ReferenceEquals(fullBytes, _lastFullPngRef))
        {
            _lastFullPngRef = fullBytes;
            BitmapImage? decodedFull = await IconHelper.CreateBitmapImageAsync(dq, fullBytes, 32);
            if (_isDisposed || gen != _iconDecodeGeneration)
            {
                return;
            }
            _decodedFullBitmap = decodedFull;
        }

        BitmapImage? desired = _itemCount > 0 ? _decodedFullBitmap : _decodedEmptyBitmap;
        if (desired is not null)
        {
            _shellIconImage.Source = desired;
            _fallbackGlyphPath.Visibility = Visibility.Collapsed;
        }
        else
        {
            _fallbackGlyphPath.Visibility = Visibility.Visible;
        }
    }

    private void SyncHoverAffordances()
    {
        bool showClear = _isPointerHovered && !_dropHoverActive && _itemCount > 0;
        _clearButton.Opacity = showClear ? 1 : 0;
        _clearButton.IsHitTestVisible = showClear;
        _countBadge.Opacity = showClear ? 0 : 1;
    }

    private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerHovered = true;
        if (!_dropHoverActive)
        {
            SyncHoverAffordances();
            ApplyTheme(ResolveIsDark());
        }
    }

    private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerHovered = false;
        if (!_dropHoverActive)
        {
            SyncHoverAffordances();
            ApplyTheme(ResolveIsDark());
        }
    }

    private void OnCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (!_isInteractiveAllowed || _clearDialogOpen)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject src && IsDescendantOfClearButton(src))
        {
            return;
        }

        e.Handled = true;
        if (DispatcherQueue is { } dq)
        {
            _ = dq.TryEnqueue(ShellRecycleBinInterop.OpenRecycleBinInExplorer);
        }
        else
        {
            ShellRecycleBinInterop.OpenRecycleBinInExplorer();
        }
    }

    private bool IsDescendantOfClearButton(DependencyObject? current)
    {
        while (current is not null)
        {
            if (ReferenceEquals(current, _clearButton))
            {
                return true;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return false;
    }

    private async void OnClearButtonClick(object sender, RoutedEventArgs e)
    {
        if (!_isInteractiveAllowed || _itemCount <= 0 || _clearDialogOpen || XamlRoot is null)
        {
            return;
        }

        _clearDialogOpen = true;
        try
        {
            string culture = _cultureResolver();
            var dialog = new ContentDialog
            {
                Title = LocalizationService.TranslateForCulture(culture, "CustomWidget.RecycleBin.ConfirmEmptyTitle"),
                Content = LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.RecycleBin.ConfirmEmptyBody",
                    _itemCount),
                PrimaryButtonText = LocalizationService.TranslateForCulture(culture, "CustomWidget.RecycleBin.ConfirmEmptyPrimary"),
                CloseButtonText = LocalizationService.TranslateForCulture(culture, "Common.Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(this)
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            await CustomWidgetRecycleBinService.Shared.EmptyAllBinsConfirmedAsync(_ownerHwndResolver());
        }
        catch
        {
            return;
        }
        finally
        {
            _clearDialogOpen = false;
        }
    }

    private void OnCardXamlDragOver(object sender, DragEventArgs e)
    {
        if (!_isInteractiveAllowed)
        {
            e.AcceptedOperation = DataPackageOperation.None;
            return;
        }

        bool hasFiles = e.DataView.Contains(StandardDataFormats.StorageItems) ||
                        DeskBoxDragData.IsInternalFileDrag(e.DataView);
        if (hasFiles)
        {
            e.AcceptedOperation = (e.AllowedOperations & DataPackageOperation.Move) != 0
                ? DataPackageOperation.Move
                : DataPackageOperation.Copy;
            e.DragUIOverride.Caption = LocalizationService.TranslateForCulture(
                _cultureResolver(),
                "Widget.DropOnRecycleBinHint");
            SetDragHover(true);
            e.Handled = true;
        }
        else
        {
            e.AcceptedOperation = DataPackageOperation.None;
            SetDragHover(false);
        }
    }

    private void OnCardXamlDragLeave(object sender, DragEventArgs e)
    {
        SetDragHover(false);
    }

    private async void OnCardXamlDrop(object sender, DragEventArgs e)
    {
        if (!_isInteractiveAllowed)
        {
            return;
        }

        e.Handled = true;
        DragOperationDeferral deferral = e.GetDeferral();
        try
        {
            var paths = DeskBoxDragData.GetInternalDroppedFiles(e.DataView)
                .Select(static f => f.Path)
                .Where(static p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            if (paths.Count == 0 && e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
                foreach (IStorageItem item in items)
                {
                    if (!string.IsNullOrWhiteSpace(item.Path))
                    {
                        paths.Add(item.Path);
                    }
                }
            }

            if (paths.Count == 0)
            {
                SetDragHover(false);
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }

            e.AcceptedOperation = (e.AllowedOperations & DataPackageOperation.Move) != 0
                ? DataPackageOperation.Move
                : DataPackageOperation.Copy;
            await HandleDroppedPathsAsync(paths);
        }
        catch (Exception ex)
        {
            SetDragHover(false);
            App.Log($"[CustomWidget.RecycleBin] XAML drop failed: {ex.Message}");
        }
        finally
        {
            deferral.Complete();
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _serviceSubscription?.Dispose();
        _serviceSubscription = null;
    }

    private static Geometry BuildTrashBodyAndLidCombinedGeometry()
    {
        var group = new GeometryGroup();
        group.Children.Add(BuildArticulatedLidGeometry());
        group.Children.Add(BuildArticulatedBodyGeometry());
        return group;
    }

    private static PathGeometry BuildArticulatedLidGeometry()
    {
        var geom = new PathGeometry();
        // Lid bar: (3.0, 5.0) -> (15.0, 5.0)
        var bar = new PathFigure { StartPoint = new Point(3.0, 5.0), IsClosed = false, IsFilled = false };
        bar.Segments.Add(new LineSegment { Point = new Point(15.0, 5.0) });
        geom.Figures.Add(bar);

        // Lid handle arch: (6.6, 5.0) -> (6.6, 3.2) -> (11.4, 3.2) -> (11.4, 5.0)
        var handle = new PathFigure { StartPoint = new Point(6.6, 5.0), IsClosed = false, IsFilled = false };
        handle.Segments.Add(new LineSegment { Point = new Point(6.6, 3.3) });
        handle.Segments.Add(new LineSegment { Point = new Point(11.4, 3.3) });
        handle.Segments.Add(new LineSegment { Point = new Point(11.4, 5.0) });
        geom.Figures.Add(handle);
        return geom;
    }

    private static PathGeometry BuildArticulatedBodyGeometry()
    {
        var geom = new PathGeometry();
        // Tapered bin body: (4.4, 6.4) -> (5.2, 15.2) -> (12.8, 15.2) -> (13.6, 6.4)
        var bucket = new PathFigure { StartPoint = new Point(4.4, 6.4), IsClosed = false, IsFilled = false };
        bucket.Segments.Add(new LineSegment { Point = new Point(5.2, 15.2) });
        bucket.Segments.Add(new LineSegment { Point = new Point(12.8, 15.2) });
        bucket.Segments.Add(new LineSegment { Point = new Point(13.6, 6.4) });
        geom.Figures.Add(bucket);

        // Inner vertical ribs
        var ribLeft = new PathFigure { StartPoint = new Point(7.5, 8.2), IsClosed = false, IsFilled = false };
        ribLeft.Segments.Add(new LineSegment { Point = new Point(7.8, 13.2) });
        geom.Figures.Add(ribLeft);

        var ribRight = new PathFigure { StartPoint = new Point(10.5, 8.2), IsClosed = false, IsFilled = false };
        ribRight.Segments.Add(new LineSegment { Point = new Point(10.2, 13.2) });
        geom.Figures.Add(ribRight);
        return geom;
    }
}
