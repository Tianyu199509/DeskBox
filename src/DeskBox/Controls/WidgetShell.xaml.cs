using DeskBox.Contracts;
using DeskBox.Services;
using DeskBox.Helpers;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;
using Windows.UI;

namespace DeskBox.Controls;

public sealed partial class WidgetShell : UserControl
{
    private const double CompactMarqueeGap = 32;
    private const double CompactMarqueeStartDelayMs = 900;
    private const double CompactMarqueeSpeedPixelsPerSecond = 50;
    private const double CompactMarqueeOverflowTolerance = 4;
    private const double CompactActionTrailingPadding = 2;
    private const double CompactReorderHandleWidth = 18;

    /// <summary>
    /// Content hosted below the title area. Future widget kinds should provide their body through this slot.
    /// </summary>
    public static readonly DependencyProperty ShellContentProperty =
        DependencyProperty.Register(
            nameof(ShellContent),
            typeof(object),
            typeof(WidgetShell),
            new PropertyMetadata(null));

    public static readonly DependencyProperty TitleGlyphProperty =
        DependencyProperty.Register(
            nameof(TitleGlyph),
            typeof(string),
            typeof(WidgetShell),
            new PropertyMetadata("\uE8A5", OnTitleIconAppearanceChanged));

    public static readonly DependencyProperty TitleIconModeProperty =
        DependencyProperty.Register(
            nameof(TitleIconMode),
            typeof(string),
            typeof(WidgetShell),
            new PropertyMetadata(WidgetTitleIconModeNames.Color, OnTitleIconAppearanceChanged));

    public static readonly DependencyProperty TitleIconKindProperty =
        DependencyProperty.Register(
            nameof(TitleIconKind),
            typeof(string),
            typeof(WidgetShell),
            new PropertyMetadata(WidgetTitleIconKindNames.Default, OnTitleIconAppearanceChanged));

    public static readonly DependencyProperty TitleIconAccentColorProperty =
        DependencyProperty.Register(
            nameof(TitleIconAccentColor),
            typeof(Color),
            typeof(WidgetShell),
            new PropertyMetadata(AccentColorHelper.DefaultAccentColor, OnTitleIconAppearanceChanged));

    public static readonly DependencyProperty OverlayTitleProperty =
        DependencyProperty.Register(
            nameof(OverlayTitle),
            typeof(string),
            typeof(WidgetShell),
            new PropertyMetadata(string.Empty, OnOverlayTitleChanged));

    /// <summary>
    /// Optional title bar override used by legacy windows while they migrate into the shared shell.
    /// When set, the built-in title and action buttons are hidden.
    /// </summary>
    public static readonly DependencyProperty TitleBarContentProperty =
        DependencyProperty.Register(
            nameof(TitleBarContent),
            typeof(object),
            typeof(WidgetShell),
            new PropertyMetadata(null, OnTitleBarContentChanged));

    public static readonly DependencyProperty ShowHoverButtonsProperty =
        DependencyProperty.Register(
            nameof(ShowHoverButtons),
            typeof(bool),
            typeof(WidgetShell),
            new PropertyMetadata(true, OnShowHoverButtonsChanged));

    public static readonly DependencyProperty ShowAddButtonProperty =
        DependencyProperty.Register(
            nameof(ShowAddButton),
            typeof(bool),
            typeof(WidgetShell),
            new PropertyMetadata(false, OnShowAddButtonChanged));

    public static readonly DependencyProperty ChromeModeProperty =
        DependencyProperty.Register(
            nameof(ChromeMode),
            typeof(WidgetChromeMode),
            typeof(WidgetShell),
            new PropertyMetadata(WidgetChromeMode.Standard, OnChromeModeChanged));

    public static readonly DependencyProperty IsTitleEditableProperty =
        DependencyProperty.Register(
            nameof(IsTitleEditable),
            typeof(bool),
            typeof(WidgetShell),
            new PropertyMetadata(false));

    public static readonly DependencyProperty TitleEditorContentProperty =
        DependencyProperty.Register(
            nameof(TitleEditorContent),
            typeof(object),
            typeof(WidgetShell),
            new PropertyMetadata(null, OnTitleEditorContentChanged));

    private Storyboard? _showButtonsStoryboard;
    private Storyboard? _hideButtonsStoryboard;
    private Storyboard? _overlayHandleVisualStoryboard;
    private Storyboard? _compactLiveStoryboard;
    private Storyboard? _compactReorderHandleStoryboard;
    private DispatcherQueueTimer? _compactMarqueeDelayTimer;
    private DispatcherQueueTimer? _compactMarqueeFrameTimer;
    private TranslateTransform? _rightButtonsTransform;
    private TextBlock? _compactMarqueePrimary;
    private TextBlock? _compactMarqueeClone;
    private Canvas? _compactMarqueeCanvas;
    private FrameworkElement? _compactMarqueeViewport;
    private double _compactMarqueeDistance;
    private DateTimeOffset _compactMarqueeStartedAt;
    private WidgetCompactPresentation? _compactPresentation;
    private IWidgetResponsiveLayoutContent? _responsiveLayoutContent;
    private bool _isResponsiveLayoutTransitionActive;
    private double _responsiveTargetContentWidth;
    private double _responsiveTargetContentHeight;
    private WidgetCompactWidthTier _compactWidthTier = WidgetCompactWidthTier.Standard;
    private bool _isPointerOverShell;
    private bool _isCollapsed;
    private bool _isCollapseActionAvailable;
    private bool _isMinimalCompactStyle;
    private bool _usesStackedCompactText;
    private bool _isCompactKeyboardFocused;
    private bool _usesSmartCompactBehavior;
    private bool _showCompactSummary;
    private bool _isPointerOverCompactIdentity;
    private bool _isPointerOverCompactExpansionZone;
    private bool _isPointerOverCompactActions;
    private bool _isPointerOverCompactActionTrigger;
    private bool _isPointerOverCompactReorderHandle;
    private bool _isCompactActionRegionReported;
    private bool _isCompactTransitionActive;
    private bool _isDragHandlePressed;
    private bool _isCompactMoveHandlePress;
    private bool _isCompactReorderEnabled;
    private Storyboard? _compactVinylRotationStoryboard;
    private bool _isCompactVinylRotating;
    private bool _isPointerOverDragHandle;
    private DragHandleClickAction _pendingDragHandleClickAction;
    private bool _hasDragHandlePressMoved;
    private Windows.Foundation.Point _dragHandlePressPoint;
    private double _compactOuterCornerRadius = 16;
    private double _compactInnerCornerRadius = 8;
    private double _compactMediaCornerRadius = 8;
    private double _expandedOuterCornerRadius = 8;
    private double _transitionOuterCornerRadiusFrom = 8;
    private double _transitionOuterCornerRadiusTo = 8;
    private GridLength _titleBarRowHeight = new(46);
    private Thickness _titleBarPadding = new(14, 7, 12, 5);

    private enum DragHandleClickAction
    {
        None,
        Expand,
        Collapse
    }

    public event EventHandler<RoutedEventArgs>? AddRequested;
    public event EventHandler<RoutedEventArgs>? PositionLockRequested;
    public event EventHandler<RoutedEventArgs>? SizeLockRequested;
    public event EventHandler<RoutedEventArgs>? MoreRequested;
    public event EventHandler<RoutedEventArgs>? CloseRequested;
    public event EventHandler<RoutedEventArgs>? CollapseRequested;
    public event EventHandler<RoutedEventArgs>? ExpandRequested;
    public event EventHandler<RoutedEventArgs>? CompactBodyExpandRequested;
    public event EventHandler<RoutedEventArgs>? CompactPreviousRequested;
    public event EventHandler<RoutedEventArgs>? CompactPrimaryActionRequested;
    public event EventHandler<RoutedEventArgs>? CompactPlayPauseRequested;
    public event EventHandler<RoutedEventArgs>? CompactNextRequested;
    public event EventHandler? CompactPointerEntered;
    public event EventHandler? CompactPointerExited;
    public event EventHandler? CompactExpansionPointerEntered;
    public event EventHandler? CompactExpansionPointerExited;
    public event EventHandler? CompactPointerPressed;
    public event EventHandler? CompactActionPointerEntered;
    public event EventHandler? CompactActionPointerExited;
    public event EventHandler? CompactMoveHandlePointerEntered;
    public event EventHandler? CompactMoveHandlePointerExited;
    public event EventHandler? CompactDragEntered;
    public event EventHandler? CompactDragLeft;
    public event EventHandler? CompactDropCompleted;
    public event EventHandler<DoubleTappedRoutedEventArgs>? TitleDoubleTapped;
    public event EventHandler<RightTappedRoutedEventArgs>? TitleRightTapped;
    public event EventHandler<PointerRoutedEventArgs>? TitlePointerPressed;
    public event EventHandler<PointerRoutedEventArgs>? TitlePointerMoved;
    public event EventHandler<PointerRoutedEventArgs>? TitlePointerReleased;
    public event EventHandler<PointerRoutedEventArgs>? DragHandlePointerPressed;
    public event EventHandler<PointerRoutedEventArgs>? DragHandlePointerMoved;
    public event EventHandler<PointerRoutedEventArgs>? DragHandlePointerReleased;

    public WidgetShell()
    {
        InitializeComponent();
        CompactTitleIcon.SetCompactPresentationMode(true);
        SetProtectedCursor(CompactIdentityHost, InputSystemCursorShape.SizeAll);
        SetProtectedCursor(CompactReorderHandle, InputSystemCursorShape.SizeAll);
        ShellRoot.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(ShellRoot_DragEnter), true);
        ShellRoot.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(ShellRoot_DragLeave), true);
        ShellRoot.AddHandler(UIElement.DropEvent, new DragEventHandler(ShellRoot_Drop), true);
        RightActionButtons.SizeChanged += (_, _) =>
        {
            _rightButtonsTransform = RightActionButtons.RenderTransform as TranslateTransform;
        };
        Loaded += (_, _) =>
        {
            ApplyChromeMode();
            ApplyCompactAdaptiveLayout();
            ApplyFullBleedOverlayTheme();
            QueueCompactMarquee();
        };
        ActualThemeChanged += (_, _) => ApplyFullBleedOverlayTheme();
        Unloaded += (_, _) =>
        {
            StopCompactMarquee();
            _compactLiveStoryboard?.Stop();
        };
    }

    public bool ShowHoverButtons
    {
        get => (bool)GetValue(ShowHoverButtonsProperty);
        set => SetValue(ShowHoverButtonsProperty, value);
    }

    public object? ShellContent
    {
        get => GetValue(ShellContentProperty);
        set => SetValue(ShellContentProperty, value);
    }

    public string TitleGlyph
    {
        get => (string)GetValue(TitleGlyphProperty);
        set => SetValue(TitleGlyphProperty, value);
    }

    public string TitleIconMode
    {
        get => (string)GetValue(TitleIconModeProperty);
        set => SetValue(TitleIconModeProperty, value);
    }

    public string TitleIconKind
    {
        get => (string)GetValue(TitleIconKindProperty);
        set => SetValue(TitleIconKindProperty, value);
    }

    public Color TitleIconAccentColor
    {
        get => (Color)GetValue(TitleIconAccentColorProperty);
        set => SetValue(TitleIconAccentColorProperty, value);
    }

    public string OverlayTitle
    {
        get => (string)GetValue(OverlayTitleProperty);
        set => SetValue(OverlayTitleProperty, value);
    }

    public bool ShowAddButton
    {
        get => (bool)GetValue(ShowAddButtonProperty);
        set => SetValue(ShowAddButtonProperty, value);
    }

    public WidgetChromeMode ChromeMode
    {
        get => (WidgetChromeMode)GetValue(ChromeModeProperty);
        set => SetValue(ChromeModeProperty, value);
    }

    public bool IsTitleEditable
    {
        get => (bool)GetValue(IsTitleEditableProperty);
        set => SetValue(IsTitleEditableProperty, value);
    }

    public object? TitleEditorContent
    {
        get => GetValue(TitleEditorContentProperty);
        set => SetValue(TitleEditorContentProperty, value);
    }

    public Visibility AddButtonVisibility => ShowAddButton ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Custom title bar content for migrated legacy widgets that still own title interactions.
    /// New simple widget kinds should prefer the default title bar.
    /// </summary>
    public object? TitleBarContent
    {
        get => GetValue(TitleBarContentProperty);
        set => SetValue(TitleBarContentProperty, value);
    }

    public Grid TitleBar => TitleBarGrid;
    public Border BackgroundSurface => BackgroundPlate;
    public Border Divider => HeaderDivider;
    public WidgetTitleIcon TitleIconElement => TitleIcon;
    public TextBlock TitleTextElement => TitleText;
    public ContentPresenter TitleEditorPresenterElement => TitleEditorPresenter;
    public StackPanel RightActionButtonHost => RightActionButtons;
    public StackPanel TitleIdentityHostElement => TitleIdentityHost;
    public ContentPresenter ShellContentPresenterElement => ShellContentPresenter;
    public Button PositionLockActionButton => PositionLockButton;
    public Button SizeLockActionButton => SizeLockButton;
    public Button AddActionButton => AddButton;
    public Button CollapseActionButton => CollapseButton;
    public Button CompactExpandActionButton => CompactExpandButton;
    public FrameworkElement OverlayDragHandleElement => OverlayDragHandle;

    public FrameworkElement CompactMoveHandleElement => CompactIdentityHost;
    public FrameworkElement CompactBodyElement => CompactTextContainer;
    public FrameworkElement CompactReorderHandleElement => CompactReorderHandle;
    public Button MoreActionButton => MoreButton;
    public Button CloseActionButton => CloseButton;
    public FrameworkElement PositionLockActionIcon => PositionLockButtonIcon;
    public FrameworkElement PositionLockFilledActionIcon => PositionLockButtonFilledIcon;
    public FrameworkElement SizeLockActionIcon => SizeLockButtonIcon;
    public FrameworkElement SizeLockFilledActionIcon => SizeLockButtonFilledIcon;
    public FrameworkElement AddActionIcon => AddButtonIcon;
    public FrameworkElement MoreActionIcon => MoreButtonIcon;
    public FrameworkElement CloseActionIcon => CloseButtonIcon;
    public FrameworkElement DragHandleElement => _isCollapsed ? CollapsedChromeLayer : OverlayDragHandle;

    public bool IsOverlayChromeMode => ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden;

    public bool IsCollapsed => _isCollapsed;

    public bool IsCompactMoveHandlePress => _isCompactMoveHandlePress;

    public void SetCompactReorderEnabled(bool enabled)
    {
        _isCompactReorderEnabled = enabled;
        CompactReorderHandle.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        CompactReorderHandle.IsHitTestVisible = enabled && _isCollapsed;
        UpdateCompactActionRegionWidth();
        UpdateCompactReorderHandleVisual(animate: false);
    }

    public void SetCompactInteractionMode(bool usesSmartBehavior)
    {
        _usesSmartCompactBehavior = usesSmartBehavior;
        ApplyCompactAdaptiveLayout();
        ApplyCompactActionVisibility(animate: false);
    }

    public void SetContent(IWidgetContent content)
    {
        _responsiveLayoutContent = content as IWidgetResponsiveLayoutContent;
        ShellContent = content.View;
    }

    public void BeginResponsiveLayoutTransition(
        bool isCollapsing,
        double targetWindowWidth,
        double targetWindowHeight)
    {
        if (_responsiveLayoutContent is null)
        {
            _isResponsiveLayoutTransitionActive = false;
            return;
        }

        double titleHeight = IsOverlayChromeMode
            ? 0
            : _titleBarRowHeight.GridUnitType == GridUnitType.Pixel
                ? _titleBarRowHeight.Value
                : Math.Max(0, TitleBarGrid.ActualHeight);
        _responsiveTargetContentWidth = Math.Max(0, targetWindowWidth);
        _responsiveTargetContentHeight = Math.Max(0, targetWindowHeight - titleHeight);
        _isResponsiveLayoutTransitionActive = true;
        _responsiveLayoutContent.BeginResponsiveLayoutTransition(
            _responsiveTargetContentWidth,
            _responsiveTargetContentHeight,
            isCollapsing);
    }

    public void CompleteResponsiveLayoutTransition()
    {
        if (!_isResponsiveLayoutTransitionActive || _responsiveLayoutContent is null)
        {
            return;
        }

        _responsiveLayoutContent.CompleteResponsiveLayoutTransition(
            _responsiveTargetContentWidth,
            _responsiveTargetContentHeight);
        _isResponsiveLayoutTransitionActive = false;
    }

    public void CancelResponsiveLayoutTransition()
    {
        if (!_isResponsiveLayoutTransitionActive || _responsiveLayoutContent is null)
        {
            return;
        }

        _responsiveLayoutContent.CancelResponsiveLayoutTransition();
        _isResponsiveLayoutTransitionActive = false;
    }

    public void SetCollapsed(bool collapsed, string contentMode)
    {
        ResetCompactTransitionVisuals();
        bool stateChanged = _isCollapsed != collapsed;
        _isCollapsed = collapsed;
        if (stateChanged)
        {
            ResetCompactInteractionRegions();
        }
        UpdateCompactInteractionRegionHighlights();
        if (collapsed)
        {
            _isPointerOverDragHandle = false;
        }
        _isMinimalCompactStyle = string.Equals(
            contentMode,
            SettingsService.WidgetCompactContentModeMinimal,
            StringComparison.Ordinal);
        ApplyCompactAdaptiveLayout();
        ApplyChromeMode();
        UpdateOverlayDragHandleVisual(animate: false);
        ApplyCompactActionVisibility(animate: false);
        UpdateCompactReorderHandleVisual(animate: false);
        if (collapsed)
        {
            QueueCompactMarquee();
        }
        else
        {
            StopCompactMarquee();
            StopParticles();
            StopBottomGlow();
            StopBreathBorder();
            StopEdgeGlowPulse();
        }
    }

    public bool PrepareCompactTransition(
        bool collapsed,
        double expandedOuterRadius,
        double compactOuterRadius,
        double compactInnerRadius,
        double compactMediaRadius)
    {
        if (_isCollapsed == collapsed)
        {
            return false;
        }

        _expandedOuterCornerRadius = Math.Max(0, expandedOuterRadius);
        _compactOuterCornerRadius = Math.Max(0, compactOuterRadius);
        _compactInnerCornerRadius = Math.Max(0, compactInnerRadius);
        _compactMediaCornerRadius = Math.Max(0, compactMediaRadius);
        _transitionOuterCornerRadiusFrom = collapsed
            ? _expandedOuterCornerRadius
            : _compactOuterCornerRadius;
        _transitionOuterCornerRadiusTo = collapsed
            ? _compactOuterCornerRadius
            : _expandedOuterCornerRadius;
        _isCompactTransitionActive = true;
        if (!collapsed)
        {
            _isCollapsed = false;
            ApplyChromeMode();
        }

        CollapsedChromeLayer.Visibility = Visibility.Visible;
        CollapsedChromeLayer.IsHitTestVisible = false;
        CollapsedChromeLayer.Opacity = collapsed ? 0 : 1;
        TitleBarGrid.Opacity = collapsed ? 1 : 0;
        ShellContentPresenter.Opacity = collapsed ? 1 : 0;
        ApplyCompactInnerCornerRadii();
        SetBackgroundCornerRadius(_transitionOuterCornerRadiusFrom);
        return true;
    }

    public void SetCompactTransitionProgress(bool collapsed, double progress)
    {
        if (!_isCompactTransitionActive)
        {
            return;
        }

        double value = Math.Clamp(progress, 0, 1);
        double compactOpacity;
        double expandedOpacity;
        if (collapsed)
        {
            expandedOpacity = 1 - SmoothStep(Math.Clamp(value / 0.62, 0, 1));
            compactOpacity = SmoothStep(Math.Clamp((value - 0.36) / 0.64, 0, 1));
        }
        else
        {
            compactOpacity = 1 - SmoothStep(Math.Clamp(value / 0.46, 0, 1));
            double expandedRevealStart = _isResponsiveLayoutTransitionActive ? 0.42 : 0.28;
            expandedOpacity = SmoothStep(Math.Clamp(
                (value - expandedRevealStart) / (1 - expandedRevealStart),
                0,
                1));
        }

        CollapsedChromeLayer.Opacity = compactOpacity;
        TitleBarGrid.Opacity = expandedOpacity;
        ShellContentPresenter.Opacity = expandedOpacity;
        SetBackgroundCornerRadius(Lerp(
            _transitionOuterCornerRadiusFrom,
            _transitionOuterCornerRadiusTo,
            value));

        // Full-bleed background: fade out earlier during expand, fade in later during collapse
        bool hasFullBleed = _compactPresentation?.UseFullBleedBackground == true &&
            _compactPresentation.Thumbnail is not null;
        if (hasFullBleed)
        {
            double fullBleedOpacity;
            double fullBleedScale;
            if (collapsed)
            {
                // Collapsing: fade in after compact layer is partially visible
                fullBleedOpacity = SmoothStep(Math.Clamp((value - 0.5) / 0.5, 0, 1));
                fullBleedScale = Lerp(0.9, 1.0, fullBleedOpacity);
            }
            else
            {
                // Expanding: fade out faster than compact layer, shrink slightly
                fullBleedOpacity = 1 - SmoothStep(Math.Clamp(value / 0.35, 0, 1));
                fullBleedScale = Lerp(1.0, 1.15, 1 - fullBleedOpacity);
            }

            CompactFullBleedBackground.Opacity = fullBleedOpacity;
            CompactFullBleedOverlay.Opacity = fullBleedOpacity;
            FullBleedScaleTransform.ScaleX = fullBleedScale;
            FullBleedScaleTransform.ScaleY = fullBleedScale;
        }
    }

    private static double SmoothStep(double value) => value * value * (3 - (2 * value));

    private static double Lerp(double start, double end, double progress) =>
        start + ((end - start) * Math.Clamp(progress, 0, 1));

    public void CompleteCompactTransition(bool collapsed, string contentMode)
    {
        _isCompactTransitionActive = false;
        SetBackgroundCornerRadius(collapsed
            ? _compactOuterCornerRadius
            : _expandedOuterCornerRadius);
        ResetCompactTransitionVisuals();
        SetCollapsed(collapsed, contentMode);
    }

    public void CancelCompactTransition()
    {
        _isCompactTransitionActive = false;
        SetBackgroundCornerRadius(_isCollapsed
            ? _compactOuterCornerRadius
            : _expandedOuterCornerRadius);
        ResetCompactTransitionVisuals();
        ApplyChromeMode();
    }

    private void ResetCompactTransitionVisuals()
    {
        TitleBarGrid.Opacity = 1;
        ShellContentPresenter.Opacity = 1;
        CollapsedChromeLayer.Opacity = 1;
        CollapsedChromeLayer.IsHitTestVisible = true;
        FullBleedScaleTransform.ScaleX = 1;
        FullBleedScaleTransform.ScaleY = 1;
    }

    public void SetCompactPresentation(WidgetCompactPresentation presentation)
    {
        WidgetCompactPresentation? previous = _compactPresentation;
        bool textChanged = previous is not null &&
            (!string.Equals(previous.Title, presentation.Title, StringComparison.Ordinal) ||
             !string.Equals(previous.Summary, presentation.Summary, StringComparison.Ordinal));
        bool liveStateChanged = previous is not null &&
            !string.IsNullOrWhiteSpace(presentation.LiveStateKey) &&
            !string.Equals(previous.LiveStateKey, presentation.LiveStateKey, StringComparison.Ordinal);
        bool structureChanged = previous is null ||
            previous.ShowPrimaryAction != presentation.ShowPrimaryAction ||
            previous.ShowMediaControls != presentation.ShowMediaControls ||
            previous.UseStackedText != presentation.UseStackedText ||
            previous.UseFullBleedBackground != presentation.UseFullBleedBackground ||
            !string.IsNullOrWhiteSpace(previous.EmojiIcon) != !string.IsNullOrWhiteSpace(presentation.EmojiIcon) ||
            string.IsNullOrWhiteSpace(previous.Summary) != string.IsNullOrWhiteSpace(presentation.Summary);

        _compactPresentation = presentation;
        CompactTitleText.Text = presentation.Title;
        CompactTitleMarqueeClone.Text = presentation.Title;
        CompactSummaryText.Text = presentation.Summary;
        CompactSummaryMarqueeClone.Text = presentation.Summary;
        CompactTitleIcon.Glyph = presentation.Glyph;
        CompactTitleIcon.LabelText = presentation.Title;

        bool useFullBleed = presentation.UseFullBleedBackground && presentation.Thumbnail is not null;
        CompactFullBleedBackground.Source = useFullBleed ? presentation.Thumbnail : null;
        ApplyFullBleedOverlayTheme();
        ApplyFullBleedVisibility(useFullBleed);

        // Paused dim overlay: show when full-bleed but not playing
        CompactPausedDim.Visibility = useFullBleed && !presentation.IsPlaying
            ? Visibility.Visible
            : Visibility.Collapsed;

        CompactThumbnail.Source = useFullBleed ? null : presentation.Thumbnail;
        CompactThumbnailHost.Visibility = !useFullBleed && presentation.Thumbnail is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        // White border for thumbnail (quick capture)
        if (!useFullBleed && presentation.Thumbnail is not null)
        {
            CompactThumbnailHost.BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            CompactThumbnailHost.BorderThickness = new Thickness(1);
        }
        else
        {
            CompactThumbnailHost.ClearValue(Border.BorderBrushProperty);
            CompactThumbnailHost.ClearValue(Border.BorderThicknessProperty);
        }

        // Rotating vinyl disc (music capsule). Label reuses the album cover.
        bool showVinyl = presentation.ShowVinyl && presentation.Thumbnail is not null;
        CompactVinylHost.Visibility = showVinyl ? Visibility.Visible : Visibility.Collapsed;
        CompactVinylLabelBrush.ImageSource = showVinyl ? presentation.Thumbnail : null;

        bool hasEmoji = !string.IsNullOrWhiteSpace(presentation.EmojiIcon);
        CompactEmojiIcon.Text = presentation.EmojiIcon;
        CompactEmojiIcon.Visibility = hasEmoji ? Visibility.Visible : Visibility.Collapsed;

        // When an emoji (e.g. weather) is shown, the default widget glyph must be
        // hidden. We cannot just set CompactTitleIcon.Visibility = Collapsed, because
        // WidgetTitleIcon re-applies Visibility = Visible on its own Loaded /
        // ActualThemeChanged and whenever an appearance property changes. So the hide
        // has to go through the control's own Mode = Hidden, which makes it keep
        // itself Collapsed and survive its internal re-show logic.
        //
        // The Mode is driven DIRECTLY here (not via the former x:Bind to the
        // WidgetShell.TitleIconMode DP): the content-widget appearance path
        // (ContentWidgetWindow) resets that DP to the global setting on every
        // theme/appearance refresh, which would re-show the glyph on top of the
        // emoji. Setting the target property directly is immune to that reset.
        // When the icon should be visible we honor the user's global icon-mode
        // preference so the compact glyph still respects their setting.
        bool showTitleIcon = !hasEmoji && !useFullBleed && presentation.Thumbnail is null && !showVinyl;
        CompactTitleIcon.Visibility = showTitleIcon ? Visibility.Visible : Visibility.Collapsed;
        // When the icon is visible we honor the user's global icon-mode
        // preference so the compact glyph still respects their setting; fall back
        // to Color if the service isn't ready yet.
        string visibleIconMode = WidgetTitleIconModeNames.Color;
        if (App.Current?.SettingsService?.Settings is { } settings &&
            !string.IsNullOrWhiteSpace(settings.WidgetTitleIconMode))
        {
            visibleIconMode = settings.WidgetTitleIconMode;
        }
        CompactTitleIcon.Mode = showTitleIcon
            ? visibleIconMode
            : WidgetTitleIconModeNames.Hidden;

        // Per-widget icon color override (e.g. file type color)
        if (presentation.IconColor is not null)
        {
            CompactTitleIcon.AccentColor = presentation.IconColor.Value;
        }
        else
        {
            CompactTitleIcon.ClearValue(WidgetTitleIcon.AccentColorProperty);
        }

        CompactPrimaryActionIcon.Glyph = presentation.PrimaryActionGlyph;
        CompactPreviousButton.IsEnabled = presentation.CanGoPrevious;
        CompactNextButton.IsEnabled = presentation.CanGoNext;
        CompactPlayPauseIcon.Glyph = presentation.IsPlaying ? "\uE769" : "\uE102";
        ApplyCompactActionLabels(presentation.IsPlaying);
        UpdateCompactVinylRotation(showVinyl && presentation.IsPlaying);

        // Badge
        bool hasBadge = !string.IsNullOrWhiteSpace(presentation.BadgeText);
        CompactBadge.Visibility = hasBadge ? Visibility.Visible : Visibility.Collapsed;
        if (hasBadge)
        {
            CompactBadgeText.Text = presentation.BadgeText;
            CompactBadge.Background = presentation.BadgeIsWarning
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xD8, 0x3B, 0x01))
                : (Brush)Microsoft.UI.Xaml.Application.Current.Resources["AccentFillColorDefaultBrush"];
        }

        // Visual effects
        ApplyColorField(presentation);
        ApplyEdgeGlow(presentation);
        ApplyParticles(presentation);
        ApplySpectrum(presentation);
        ApplyShimmer(presentation);
        ApplyConditionalAnimations(presentation);

        ApplyCompactLiveState();
        ApplyCompactMusicProgress();

        if (structureChanged)
        {
            ApplyCompactAdaptiveLayout();
        }

        if (textChanged)
        {
            StopCompactMarquee();
            QueueCompactMarquee();
        }

        if (liveStateChanged && _isCollapsed)
        {
            AnimateCompactLiveChange();
        }
    }

    private void ApplyCompactAdaptiveLayout()
    {
        if (_compactPresentation is null)
        {
            return;
        }

        double logicalWidth = CollapsedChromeLayer.ActualWidth > 0
            ? CollapsedChromeLayer.ActualWidth
            : ActualWidth;
        _compactWidthTier = WidgetCompactBoundsCalculator.ResolveWidthTier(logicalWidth);
        _showCompactSummary = !_isMinimalCompactStyle &&
            _compactWidthTier != WidgetCompactWidthTier.Narrow &&
            !string.IsNullOrWhiteSpace(_compactPresentation.Summary);
        _usesStackedCompactText = _showCompactSummary &&
            _compactPresentation.UseStackedText;

        CompactTextHost.Orientation = _usesStackedCompactText
            ? Orientation.Vertical
            : Orientation.Horizontal;
        CompactTextHost.Spacing = _showCompactSummary
            ? (_usesStackedCompactText ? 1 : 6)
            : 0;

        bool useFullBleed = _compactPresentation.UseFullBleedBackground &&
            _compactPresentation.Thumbnail is not null;
        bool showVinyl = _compactPresentation.ShowVinyl &&
            _compactPresentation.Thumbnail is not null;
        double identityVisualSize;
        double identityHitSize;
        if (showVinyl)
        {
            identityVisualSize = _compactWidthTier == WidgetCompactWidthTier.Narrow ? 22 : 26;
            identityHitSize = identityVisualSize;
        }
        else if (useFullBleed)
        {
            identityVisualSize = 10;
            identityHitSize = 10;
        }
        else
        {
            identityVisualSize = _compactWidthTier switch
            {
                WidgetCompactWidthTier.Narrow => 24,
                WidgetCompactWidthTier.Wide when _usesStackedCompactText => 36,
                _ when _usesStackedCompactText => 34,
                _ => 28
            };
            identityHitSize = _compactWidthTier == WidgetCompactWidthTier.Narrow ? 34 : 40;
        }
        CompactIdentityHost.Width = Math.Max(identityVisualSize, identityHitSize);
        CompactIdentityHost.Height = Math.Max(identityVisualSize, identityHitSize);
        CompactIdentityHost.Opacity = (showVinyl || !useFullBleed) ? 1 : 0;
        CompactThumbnailHost.Width = identityVisualSize;
        CompactThumbnailHost.Height = identityVisualSize;
        CompactTitleIcon.IconSize = _compactWidthTier == WidgetCompactWidthTier.Narrow
            ? 13
            : _usesStackedCompactText ? 16 : 14;

        bool showPrimaryAction = _compactPresentation.ShowPrimaryAction &&
            _compactWidthTier != WidgetCompactWidthTier.Narrow;
        bool showMediaPlayPause = _compactPresentation.ShowMediaControls &&
            _compactWidthTier != WidgetCompactWidthTier.Narrow;
        bool showExtendedMedia = showMediaPlayPause &&
            _compactWidthTier == WidgetCompactWidthTier.Wide;
        CompactPrimaryActionButton.Visibility = showPrimaryAction
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactPreviousButton.Visibility = showExtendedMedia
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactPlayPauseButton.Visibility = showMediaPlayPause
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactNextButton.Visibility = showExtendedMedia
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactExpandButton.Visibility = _usesSmartCompactBehavior
            ? Visibility.Collapsed
            : Visibility.Visible;

        UpdateCompactActionRegionWidth();
        ApplyCompactTextVisibility();
        DispatcherQueue.TryEnqueue(UpdateCompactTextViewportWidths);
    }

    private void EnsureCompactVinylRotationStoryboard()
    {
        if (_compactVinylRotationStoryboard is not null)
        {
            return;
        }

        _compactVinylRotationStoryboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        var rotate = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = new Duration(TimeSpan.FromSeconds(4.0)),
            RepeatBehavior = RepeatBehavior.Forever
        };
        Storyboard.SetTarget(rotate, CompactVinylRotateTransform);
        Storyboard.SetTargetProperty(rotate, "(RotateTransform.Angle)");
        _compactVinylRotationStoryboard.Children.Add(rotate);
    }

    private void UpdateCompactVinylRotation(bool isPlaying)
    {
        // Idempotent: ApplyCompactPresentation is invoked on every progress/state tick,
        // so calling Begin() each time would restart the rotation from 0 and make it
        // stutter. Only start/stop when the desired rotating state actually changes.
        bool shouldRotate = CompactVinylHost.Visibility == Visibility.Visible && isPlaying;
        if (shouldRotate == _isCompactVinylRotating)
        {
            return;
        }

        _isCompactVinylRotating = shouldRotate;
        EnsureCompactVinylRotationStoryboard();
        if (shouldRotate)
        {
            _compactVinylRotationStoryboard.Begin();
        }
        else
        {
            _compactVinylRotationStoryboard.Stop();
        }
    }

    private void UpdateCompactActionRegionWidth()
    {
        Button[] actionButtons =
        [
            CompactPrimaryActionButton,
            CompactPreviousButton,
            CompactPlayPauseButton,
            CompactNextButton,
            CompactExpandButton
        ];
        Button[] visibleButtons = actionButtons
            .Where(button => button.Visibility == Visibility.Visible)
            .ToArray();

        double buttonWidth = visibleButtons.Sum(button =>
            double.IsNaN(button.Width)
                ? Math.Max(0, button.MinWidth)
                : Math.Max(0, button.Width));
        double buttonSpacing = Math.Max(0, visibleButtons.Length - 1) * CompactActionHost.Spacing;
        double trailingWidth = _isCompactReorderEnabled
            ? CompactReorderHandleWidth + CompactActionTrailingPadding
            : visibleButtons.Length > 0
                ? CompactActionTrailingPadding
                : 0;
        double actionRegionWidth = buttonWidth + buttonSpacing + trailingWidth;

        CompactActionHost.Margin = new Thickness(0, 0, trailingWidth, 0);
        CompactActionRegionHighlight.Width = actionRegionWidth;
        CompactActionRegionTrigger.Width = actionRegionWidth;
    }

    private void UpdateCompactTextViewportWidths()
    {
        if (_compactPresentation is null || CompactTextContainer.ActualWidth <= 0)
        {
            return;
        }

        StopCompactMarquee();
        double availableWidth = Math.Max(24, CompactTextContainer.ActualWidth);
        double titleWidth;
        double summaryWidth = 0;
        if (!_showCompactSummary)
        {
            titleWidth = availableWidth;
        }
        else if (_usesStackedCompactText)
        {
            titleWidth = availableWidth;
            summaryWidth = availableWidth;
        }
        else
        {
            double contentWidth = Math.Max(24, availableWidth - 15);
            double titleRatio = _compactWidthTier == WidgetCompactWidthTier.Wide ? 0.62 : 0.56;
            titleWidth = Math.Max(24, contentWidth * titleRatio);
            summaryWidth = Math.Max(20, contentWidth - titleWidth);
        }

        SetCompactTextViewportWidth(CompactTitleViewport, CompactTitleText, titleWidth);
        if (_showCompactSummary)
        {
            SetCompactTextViewportWidth(CompactSummaryViewport, CompactSummaryText, summaryWidth);
        }
        QueueCompactMarquee();
    }

    private static void SetCompactTextViewportWidth(
        FrameworkElement viewport,
        TextBlock textBlock,
        double width)
    {
        double safeWidth = Math.Max(1, width);
        viewport.Width = safeWidth;
        viewport.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, safeWidth, Math.Max(1, viewport.Height))
        };
        textBlock.Width = safeWidth;
        textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private void ApplyCompactActionLabels(bool isPlaying)
    {
        var localization = App.Current.LocalizationService;
        SetAccessibleLabel(CompactPrimaryActionButton, localization.T("Todo.Menu.MarkCompleted"));
        SetAccessibleLabel(CompactPreviousButton, localization.T("Music.Control.Previous"));
        SetAccessibleLabel(
            CompactPlayPauseButton,
            localization.T(isPlaying ? "Music.Control.Pause" : "Music.Control.Play"));
        SetAccessibleLabel(CompactNextButton, localization.T("Music.Control.Next"));
        SetAccessibleLabel(CompactExpandButton, localization.T("Widget.Compact.Expand"));
    }

    private static void SetAccessibleLabel(FrameworkElement element, string label)
    {
        AutomationProperties.SetName(element, label);
        ToolTipService.SetToolTip(element, label);
    }

    public void NotifyCompactDragMoved()
    {
        StopCompactMarquee();
        if (_pendingDragHandleClickAction != DragHandleClickAction.None)
        {
            _hasDragHandlePressMoved = true;
        }
    }

    public void SetCompactCornerRadii(double outerRadius, double innerRadius, double mediaRadius)
    {
        _compactOuterCornerRadius = Math.Max(0, outerRadius);
        _compactInnerCornerRadius = Math.Max(0, innerRadius);
        _compactMediaCornerRadius = Math.Max(0, mediaRadius);
        ApplyCompactCornerRadii();
    }

    private void ApplyCompactCornerRadii()
    {
        SetBackgroundCornerRadius(_compactOuterCornerRadius);
        ApplyCompactInnerCornerRadii();
    }

    private void ApplyCompactInnerCornerRadii()
    {
        CompactThumbnailHost.CornerRadius = new CornerRadius(_compactMediaCornerRadius);
        CompactTitleIcon.SetSurfaceCornerRadiusOverride(_compactMediaCornerRadius);
        CompactIdentityRegionHighlight.CornerRadius = new CornerRadius(_compactInnerCornerRadius);
        CompactActionRegionHighlight.CornerRadius = new CornerRadius(_compactInnerCornerRadius);

        // Full-bleed layers follow outer radius
        var outerCR = new CornerRadius(_compactOuterCornerRadius);
        CompactFullBleedClip.CornerRadius = outerCR;
        CompactPausedDim.CornerRadius = outerCR;
        CompactEdgeGlow.CornerRadius = outerCR;
        CompactColorField.CornerRadius = outerCR;
        CompactFullBleedOverlay.CornerRadius = outerCR;
        CompactShimmer.CornerRadius = outerCR;

        foreach (var button in new[]
        {
            CompactPrimaryActionButton,
            CompactPreviousButton,
            CompactPlayPauseButton,
            CompactNextButton,
            CompactExpandButton
        })
        {
            button.CornerRadius = new CornerRadius(_compactInnerCornerRadius);
        }
    }

    private void SetBackgroundCornerRadius(double radius) =>
        BackgroundPlate.CornerRadius = new CornerRadius(Math.Max(0, radius));

    private void ApplyCompactTextVisibility()
    {
        CompactSummaryViewport.Visibility = _showCompactSummary
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactTextSeparator.Visibility = _showCompactSummary && !_usesStackedCompactText
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyCompactLiveState()
    {
        // Determinate progress (duration known): show the real fraction bar.
        if (_compactPresentation?.Progress is { } progress && double.IsFinite(progress))
        {
            StopCompactLiveIndeterminate();
            CompactLiveProgressTransform.TranslateX = 0;

            double value = Math.Clamp(progress, 0, 1);
            CompactLiveTrack.Visibility = Visibility.Visible;
            CompactLiveProgress.Visibility = Visibility.Visible;
            bool isPlaying = _compactPresentation?.IsPlaying == true;
            bool isAttention = _compactPresentation?.IsAttention == true;
            bool isFullBleed = _compactPresentation?.UseFullBleedBackground == true;

            // Full-bleed mode: white progress bar, thicker, more visible
            if (isFullBleed)
            {
                CompactLiveIndicatorHost.Height = 3;
                CompactLiveTrack.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                CompactLiveTrack.Opacity = 1;
                CompactLiveProgress.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            }
            else
            {
                CompactLiveIndicatorHost.Height = 2;
                CompactLiveTrack.ClearValue(Border.BackgroundProperty);
                CompactLiveTrack.Opacity = isAttention ? 0.3 : 0.16;
                CompactLiveProgress.ClearValue(Border.BackgroundProperty);
            }

            CompactLiveProgressTransform.ScaleX = value;

            // Todo progress: overdue=orange, normal=accent→green gradient
            if (!isPlaying && !isFullBleed)
            {
                if (isAttention)
                {
                    // Overdue: orange
                    CompactLiveProgress.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0xF9, 0x73, 0x16));
                }
                else
                {
                    // Normal: accent → green based on completion
                    var accent = (Windows.UI.Color)Microsoft.UI.Xaml.Application.Current.Resources["SystemAccentColor"];
                    byte r = (byte)(accent.R + (0x22 - accent.R) * value);
                    byte g = (byte)(accent.G + (0xC5 - accent.G) * value);
                    byte b = (byte)(accent.B + (0x5E - accent.B) * value);
                    CompactLiveProgress.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, r, g, b));
                }
            }
            else if (!isFullBleed)
            {
                CompactLiveProgress.ClearValue(Border.BackgroundProperty);
            }

            if (isPlaying)
            {
                CompactLiveProgress.Opacity = isFullBleed ? 0.95 : 0.7;
            }
            else
            {
                CompactLiveProgress.Opacity = isAttention ? 1 : (isPlaying ? (isFullBleed ? 0.95 : 0.7) : 0.4);
            }

            return;
        }

        // Duration unknown but playing: many media apps (esp. streaming/live or
        // at the very start of a track) report EndTime only tens of seconds into
        // playback. Rather than hiding the bar entirely, show a sweeping
        // indeterminate segment so the capsule still reflects live activity.
        if (_compactPresentation is { IsProgressIndeterminate: true })
        {
            CompactLiveTrack.Visibility = Visibility.Visible;
            CompactLiveProgress.Visibility = Visibility.Visible;
            bool isFullBleed = _compactPresentation?.UseFullBleedBackground == true;
            if (isFullBleed)
            {
                CompactLiveIndicatorHost.Height = 3;
                CompactLiveTrack.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                CompactLiveTrack.Opacity = 1;
                CompactLiveProgress.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
                CompactLiveProgress.Opacity = 0.9;
            }
            else
            {
                CompactLiveIndicatorHost.Height = 2;
                CompactLiveTrack.ClearValue(Border.BackgroundProperty);
                CompactLiveTrack.Opacity = 0.18;
                CompactLiveProgress.ClearValue(Border.BackgroundProperty);
                CompactLiveProgress.Opacity = 0.7;
            }

            if (_compactLiveIndeterminateTimer is null)
            {
                StartCompactLiveIndeterminate(isFullBleed);
            }

            return;
        }

        // No progress and not indeterminate: hide.
        StopCompactLiveIndeterminate();
        CompactLiveTrack.Visibility = Visibility.Collapsed;
        CompactLiveProgress.Visibility = Visibility.Collapsed;
        CompactLiveTrack.Opacity = 0;
        CompactLiveProgress.Opacity = 0;
        CompactLiveProgressTransform.ScaleX = 0;
        CompactLiveProgressTransform.TranslateX = 0;
    }

    /// <summary>
    /// Shows a plain, determinate progress bar inside the music capsule, below the
    /// artist name. Driven by the presentation's MusicProgress ratio (0–1). The
    /// fill is the theme accent color (white when the capsule uses a full-bleed
    /// cover background), so it matches the rest of the UI. No timestamps.
    /// </summary>
    private void ApplyCompactMusicProgress()
    {
        WidgetCompactPresentation? p = _compactPresentation;
        if (p is null || !p.MusicProgress.HasValue)
        {
            CompactMusicProgressHost.Visibility = Visibility.Collapsed;
            return;
        }

        CompactMusicProgressHost.Visibility = Visibility.Visible;
        double value = Math.Clamp(p.MusicProgress.Value, 0, 1);
        CompactMusicProgressTransform.ScaleX = value;

        bool isFullBleed = p.UseFullBleedBackground && p.Thumbnail is not null;
        if (isFullBleed)
        {
            // On a cover-art background, invert to white so the bar stays visible.
            CompactMusicProgressFill.Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            CompactMusicProgressTrack.Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
        }
        else
        {
            CompactMusicProgressFill.ClearValue(Border.BackgroundProperty);
            CompactMusicProgressTrack.ClearValue(Border.BackgroundProperty);
        }
    }

    private void StartCompactLiveIndeterminate(bool isFullBleed)
    {
        StopCompactLiveIndeterminate();

        _compactLiveIndeterminatePhase = 0;
        double segment = isFullBleed ? 0.22 : 0.30;

        // Show the segment right away so the bar is visible from the very first
        // frame, even before the timer fires its first tick.
        CompactLiveProgressTransform.ScaleX = segment;
        CompactLiveProgressTransform.TranslateX = 0;

        // Drive the sweep directly with a DispatcherQueue timer. This is not a
        // XAML Storyboard, so it is not affected by the OS "Show animations"
        // setting — it keeps running whether or not the user has system
        // animations enabled. This fixes the symptom where, with animations
        // disabled, the bar would freeze at a static semi-transparent segment
        // for the entire Duration-unknown window (e.g. music first ~40 s).
        _compactLiveIndeterminateTimer = DispatcherQueue.CreateTimer();
        _compactLiveIndeterminateTimer.Interval = TimeSpan.FromMilliseconds(40);
        _compactLiveIndeterminateTimer.Tick += (_, _) =>
        {
            _compactLiveIndeterminatePhase += 0.035;
            if (_compactLiveIndeterminatePhase > 1)
            {
                _compactLiveIndeterminatePhase -= 1;
            }

            double trackWidth = CompactLiveTrack.ActualWidth;
            double maxTranslate = Math.Max(0, trackWidth * (1 - segment));
            CompactLiveProgressTransform.ScaleX = segment;
            CompactLiveProgressTransform.TranslateX = _compactLiveIndeterminatePhase * maxTranslate;

            // Opacity pulse guarantees visible "live" motion even if the track has
            // not been laid out yet (ActualWidth == 0 → TranslateX stays 0). The
            // pulse is layout-independent, so the bar never looks frozen during the
            // Duration-unknown window.
            CompactLiveProgress.Opacity = 0.4 + 0.4 * (0.5 + 0.5 * Math.Sin(_compactLiveIndeterminatePhase * Math.PI * 2));
        };
        _compactLiveIndeterminateTimer.Start();
    }

    private void StopCompactLiveIndeterminate()
    {
        if (_compactLiveIndeterminateTimer is not null)
        {
            _compactLiveIndeterminateTimer.Stop();
            _compactLiveIndeterminateTimer = null;
        }
    }

    private DispatcherQueueTimer? _compactLiveBreathingTimer;
    private DispatcherQueueTimer? _compactLiveIndeterminateTimer;
    private double _compactLiveIndeterminatePhase;

    private void StartCompactLiveBreathing()
    {
        StopCompactLiveBreathing();
        if (!SystemAnimationsEnabled())
        {
            return;
        }

        _compactLiveBreathingTimer = DispatcherQueue.CreateTimer();
        _compactLiveBreathingTimer.Interval = TimeSpan.FromMilliseconds(50);
        double phase = 0;
        _compactLiveBreathingTimer.Tick += (_, _) =>
        {
            phase += 0.06;
            CompactLiveProgress.Opacity = 0.6 + 0.2 * Math.Sin(phase);
        };
        _compactLiveBreathingTimer.Start();
    }

    private void StopCompactLiveBreathing()
    {
        _compactLiveBreathingTimer?.Stop();
        _compactLiveBreathingTimer = null;
    }

    private void ApplyFullBleedVisibility(bool visible)
    {
        double targetBgOpacity = visible ? 1 : 0;
        double targetOverlayOpacity = visible ? 1 : 0;

        if (!SystemAnimationsEnabled())
        {
            CompactFullBleedBackground.Opacity = targetBgOpacity;
            CompactFullBleedOverlay.Opacity = targetOverlayOpacity;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(250);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();

        var bgAnim = new DoubleAnimation
        {
            To = targetBgOpacity,
            Duration = new Duration(duration),
            EasingFunction = easing
        };
        Storyboard.SetTarget(bgAnim, CompactFullBleedBackground);
        Storyboard.SetTargetProperty(bgAnim, "Opacity");
        storyboard.Children.Add(bgAnim);

        var overlayAnim = new DoubleAnimation
        {
            To = targetOverlayOpacity,
            Duration = new Duration(duration),
            EasingFunction = easing
        };
        Storyboard.SetTarget(overlayAnim, CompactFullBleedOverlay);
        Storyboard.SetTargetProperty(overlayAnim, "Opacity");
        storyboard.Children.Add(overlayAnim);

        storyboard.Begin();
    }

    private static readonly Brush s_fullBleedTitleBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
    private static readonly Brush s_fullBleedSummaryBrush = new SolidColorBrush(
        Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

    // Full-bleed capsule text uses theme-adaptive foreground colors (dark text in
    // light theme, light text in dark theme). Match the readability scrim to the
    // theme so the text always stays legible: dark scrim in dark theme, white
    // scrim in light theme.
    private void ApplyFullBleedOverlayTheme()
    {
        bool isDark = ActualTheme == ElementTheme.Dark ||
            (ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        byte channel = isDark ? (byte)0x00 : (byte)0xFF;
        CompactFullBleedStop0.Color = Color.FromArgb(0xD9, channel, channel, channel);
        CompactFullBleedStop1.Color = Color.FromArgb(0x8C, channel, channel, channel);
        CompactFullBleedStop2.Color = Color.FromArgb(0x40, channel, channel, channel);

        // Paused dim overlay: darken in dark theme, lighten (brighten) in light theme.
        CompactPausedDim.Background = new SolidColorBrush(isDark
            ? Color.FromArgb(0x1A, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    }

    // ── Visual Effects ─────────────────────────────────────────

    private void ApplyColorField(WidgetCompactPresentation p)
    {
        bool hasColorField = p.BackgroundColorStart is not null && p.BackgroundColorEnd is not null;
        if (hasColorField)
        {
            ColorFieldStop1.Color = p.BackgroundColorStart!.Value;
            ColorFieldStop2.Color = p.BackgroundColorEnd!.Value;
            CompactColorField.Opacity = 1;
        }
        else
        {
            CompactColorField.Opacity = 0;
        }
    }

    private void ApplyEdgeGlow(WidgetCompactPresentation p)
    {
        if (p.EdgeGlowColor is not null)
        {
            CompactEdgeGlowBrush.Color = p.EdgeGlowColor.Value;
            CompactEdgeGlow.BorderThickness = new Thickness(1.5);
            CompactEdgeGlow.Opacity = 0.7;
            StartEdgeGlowPulse();
        }
        else
        {
            StopEdgeGlowPulse();
            CompactEdgeGlow.Opacity = 0;
        }
    }

    private DispatcherQueueTimer? _edgeGlowPulseTimer;

    private void StartEdgeGlowPulse()
    {
        StopEdgeGlowPulse();
        if (!SystemAnimationsEnabled()) return;
        _edgeGlowPulseTimer = DispatcherQueue.CreateTimer();
        _edgeGlowPulseTimer.Interval = TimeSpan.FromMilliseconds(50);
        double phase = 0;
        _edgeGlowPulseTimer.Tick += (_, _) =>
        {
            phase += 0.04;
            CompactEdgeGlow.Opacity = 0.5 + 0.25 * Math.Sin(phase);
        };
        _edgeGlowPulseTimer.Start();
    }

    private void StopEdgeGlowPulse()
    {
        _edgeGlowPulseTimer?.Stop();
        _edgeGlowPulseTimer = null;
    }

    // ── Particles (rain / snow) ────────────────────────────────

    private DispatcherQueueTimer? _particleTimer;
    private readonly List<(Microsoft.UI.Xaml.Shapes.Shape Shape, double Speed, double Drift)> _particles = [];
    private CompactParticleKind _activeParticleKind = CompactParticleKind.None;

    private void ApplyParticles(WidgetCompactPresentation p)
    {
        if (p.ParticleKind == _activeParticleKind && p.ParticleKind != CompactParticleKind.None)
        {
            return; // already running same kind
        }

        StopParticles();
        _activeParticleKind = p.ParticleKind;
        if (p.ParticleKind == CompactParticleKind.None || !SystemAnimationsEnabled())
        {
            return;
        }

        double canvasW = 400, canvasH = 40;
        var rng = new Random();

        if (p.ParticleKind == CompactParticleKind.Rain)
        {
            for (int i = 0; i < 10; i++)
            {
                var line = new Microsoft.UI.Xaml.Shapes.Line
                {
                    X1 = 0, Y1 = 0, X2 = -2, Y2 = 6,
                    Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0x44, 0xFF, 0xFF, 0xFF)),
                    StrokeThickness = 1
                };
                Canvas.SetLeft(line, rng.NextDouble() * canvasW);
                Canvas.SetTop(line, rng.NextDouble() * canvasH);
                CompactParticleCanvas.Children.Add(line);
                _particles.Add((line, 0.5 + rng.NextDouble() * 0.4, -0.2));
            }
        }
        else // Snow
        {
            for (int i = 0; i < 10; i++)
            {
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = 2, Height = 2,
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
                };
                Canvas.SetLeft(dot, rng.NextDouble() * canvasW);
                Canvas.SetTop(dot, rng.NextDouble() * canvasH);
                CompactParticleCanvas.Children.Add(dot);
                _particles.Add((dot, 0.15 + rng.NextDouble() * 0.15, 0.08 + rng.NextDouble() * 0.06));
            }
        }

        _particleTimer = DispatcherQueue.CreateTimer();
        _particleTimer.Interval = TimeSpan.FromMilliseconds(50);
        _particleTimer.Tick += (_, _) =>
        {
            foreach (var (shape, speed, drift) in _particles)
            {
                double top = Canvas.GetTop(shape) + speed;
                double left = Canvas.GetLeft(shape) + drift;
                if (top > canvasH + 10)
                {
                    top = -10;
                    left = new Random().NextDouble() * canvasW;
                }
                if (left < -10) left = canvasW + 5;
                if (left > canvasW + 10) left = -5;
                Canvas.SetTop(shape, top);
                Canvas.SetLeft(shape, left);
            }
        };
        _particleTimer.Start();
    }

    private void StopParticles()
    {
        _particleTimer?.Stop();
        _particleTimer = null;
        CompactParticleCanvas.Children.Clear();
        _particles.Clear();
        _activeParticleKind = CompactParticleKind.None;
    }

    // ── Bottom glow (music playback) ─────────────────────────

    private DispatcherQueueTimer? _bottomGlowTimer;

    private void ApplySpectrum(WidgetCompactPresentation p)
    {
        // Bottom glow removed - progress bar is sufficient
        CompactBottomGlow.Visibility = Visibility.Collapsed;
        StopBottomGlow();
    }

    private void StartBottomGlow()
    {
        StopBottomGlow();
        if (!SystemAnimationsEnabled()) { CompactBottomGlow.Opacity = 0.6; return; }
        _bottomGlowTimer = DispatcherQueue.CreateTimer();
        _bottomGlowTimer.Interval = TimeSpan.FromMilliseconds(50);
        double phase = 0;
        _bottomGlowTimer.Tick += (_, _) =>
        {
            phase += 0.035;
            CompactBottomGlow.Opacity = 0.35 + 0.3 * Math.Sin(phase);
        };
        _bottomGlowTimer.Start();
    }

    private void StopBottomGlow()
    {
        _bottomGlowTimer?.Stop();
        _bottomGlowTimer = null;
    }

    // ── Breathing border (search) ──────────────────────────────

    private DispatcherQueueTimer? _breathBorderTimer;

    private void ApplyShimmer(WidgetCompactPresentation p)
    {
        CompactShimmer.Opacity = 0;
        StopBreathBorder();
    }

    private void StartBreathBorder()
    {
        StopBreathBorder();
        if (!SystemAnimationsEnabled()) { CompactEdgeGlow.Opacity = 0.35; return; }
        _breathBorderTimer = DispatcherQueue.CreateTimer();
        _breathBorderTimer.Interval = TimeSpan.FromMilliseconds(50);
        double phase = 0;
        _breathBorderTimer.Tick += (_, _) =>
        {
            phase += 0.03;
            CompactEdgeGlow.Opacity = 0.35 + 0.15 * Math.Sin(phase);
        };
        _breathBorderTimer.Start();
    }

    private void StopBreathBorder()
    {
        _breathBorderTimer?.Stop();
        _breathBorderTimer = null;
    }

    // ── Conditional animations (todo flash, capture bounce) ────

    private string? _lastTodoLiveKey;
    private string? _lastCaptureLiveKey;

    private void ApplyConditionalAnimations(WidgetCompactPresentation p)
    {
        // Todo: all-complete flash
        if (p.EdgeGlowColor is not null && p.EdgeGlowColor.Value.G > 150 && p.EdgeGlowColor.Value.R < 100)
        {
            // Green glow = all complete
            if (_lastTodoLiveKey is not null && _lastTodoLiveKey != p.LiveStateKey)
            {
                TriggerEdgeGlowFlash();
            }
            _lastTodoLiveKey = p.LiveStateKey;
        }
        else
        {
            _lastTodoLiveKey = null;
        }

        // Quick capture: new record bounce
        if (p.EnableBounceOnUpdate && _lastCaptureLiveKey is not null && _lastCaptureLiveKey != p.LiveStateKey)
        {
            TriggerCapsuleBounce();
        }
        if (p.EnableBounceOnUpdate)
        {
            _lastCaptureLiveKey = p.LiveStateKey;
        }
        else
        {
            _lastCaptureLiveKey = null;
        }
    }

    private void TriggerEdgeGlowFlash()
    {
        if (!SystemAnimationsEnabled()) return;
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            From = 1.0, To = 0.5, Duration = TimeSpan.FromMilliseconds(150),
            AutoReverse = true, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, CompactEdgeGlow);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void TriggerCapsuleBounce()
    {
        if (!SystemAnimationsEnabled()) return;
        var sb = new Storyboard();
        var animX = new DoubleAnimation
        {
            From = 1.0, To = 1.03, Duration = TimeSpan.FromMilliseconds(100),
            AutoReverse = true, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        var animY = new DoubleAnimation
        {
            From = 1.0, To = 1.03, Duration = TimeSpan.FromMilliseconds(100),
            AutoReverse = true, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animX, ShellRootScale);
        Storyboard.SetTargetProperty(animX, "ScaleX");
        Storyboard.SetTarget(animY, ShellRootScale);
        Storyboard.SetTargetProperty(animY, "ScaleY");
        sb.Children.Add(animX);
        sb.Children.Add(animY);
        sb.Begin();
    }

    private void AnimateCompactLiveChange()
    {
        if (!SystemAnimationsEnabled())
        {
            return;
        }

        _compactLiveStoryboard?.Stop();
        CompactLiveEventIndicator.Opacity = 0;
        CompactTextHost.Opacity = 1;
        CompactTextTransform.X = 0;

        var indicatorAnimation = new DoubleAnimationUsingKeyFrames();
        indicatorAnimation.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0
        });
        indicatorAnimation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)),
            Value = 0.9,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        indicatorAnimation.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(380)),
            Value = 0.9
        });
        indicatorAnimation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(860)),
            Value = 0,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        Storyboard.SetTarget(indicatorAnimation, CompactLiveEventIndicator);
        Storyboard.SetTargetProperty(indicatorAnimation, "Opacity");

        var textOpacityAnimation = new DoubleAnimation
        {
            From = 0.58,
            To = 1,
            Duration = new Duration(TimeSpan.FromMilliseconds(220)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(textOpacityAnimation, CompactTextHost);
        Storyboard.SetTargetProperty(textOpacityAnimation, "Opacity");

        var textOffsetAnimation = new DoubleAnimation
        {
            From = 5,
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(260)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(textOffsetAnimation, CompactTextTransform);
        Storyboard.SetTargetProperty(textOffsetAnimation, "X");

        var storyboard = new Storyboard();
        storyboard.Children.Add(indicatorAnimation);
        storyboard.Children.Add(textOpacityAnimation);
        storyboard.Children.Add(textOffsetAnimation);
        storyboard.Completed += (_, _) =>
        {
            CompactLiveEventIndicator.Opacity = 0;
            CompactTextHost.Opacity = 1;
            CompactTextTransform.X = 0;
        };
        _compactLiveStoryboard = storyboard;
        storyboard.Begin();
    }

    private void QueueCompactMarquee(int delayMs = 300)
    {
        _compactMarqueeDelayTimer?.Stop();
        if (!_isCollapsed ||
            _compactPresentation?.EnableMarquee != true ||
            IsPointerOverCompactActionRegion() ||
            !SystemAnimationsEnabled())
        {
            return;
        }

        if (_compactMarqueeDelayTimer is null)
        {
            _compactMarqueeDelayTimer = DispatcherQueue.CreateTimer();
            _compactMarqueeDelayTimer.IsRepeating = false;
            _compactMarqueeDelayTimer.Tick += (_, _) =>
            {
                _compactMarqueeDelayTimer?.Stop();
                StartCompactMarqueeIfNeeded();
            };
        }

        _compactMarqueeDelayTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, delayMs));
        _compactMarqueeDelayTimer.Start();
    }

    private void EnsureCompactMarqueeFrameTimer()
    {
        if (_compactMarqueeFrameTimer is not null)
        {
            return;
        }

        _compactMarqueeFrameTimer = DispatcherQueue.CreateTimer();
        _compactMarqueeFrameTimer.Interval = TimeSpan.FromMilliseconds(16);
        _compactMarqueeFrameTimer.IsRepeating = true;
        _compactMarqueeFrameTimer.Tick += CompactMarqueeFrameTimer_Tick;
    }

    private void StartCompactMarqueeIfNeeded()
    {
        StopCompactMarquee(resetDelayTimer: false);
        if (!_isCollapsed ||
            _compactPresentation?.EnableMarquee != true ||
            IsPointerOverCompactActionRegion() ||
            !SystemAnimationsEnabled())
        {
            return;
        }

        var elements = ResolveCompactMarqueeElements();
        if (elements is not { } marquee)
        {
            return;
        }

        marquee.Primary.Width = marquee.NaturalWidth;
        marquee.Clone.Width = marquee.NaturalWidth;
        marquee.Primary.TextTrimming = TextTrimming.Clip;
        marquee.Clone.Text = marquee.Primary.Text;
        marquee.Clone.Visibility = Visibility.Visible;
        Canvas.SetLeft(marquee.Primary, 0);
        Canvas.SetLeft(marquee.Clone, marquee.NaturalWidth + CompactMarqueeGap);
        marquee.Canvas.Translation = Vector3.Zero;

        _compactMarqueePrimary = marquee.Primary;
        _compactMarqueeClone = marquee.Clone;
        _compactMarqueeCanvas = marquee.Canvas;
        _compactMarqueeViewport = marquee.Viewport;
        _compactMarqueeDistance = marquee.NaturalWidth + CompactMarqueeGap;
        _compactMarqueeStartedAt = DateTimeOffset.UtcNow;
        EnsureCompactMarqueeFrameTimer();
        _compactMarqueeFrameTimer?.Start();
    }

    private void CompactMarqueeFrameTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (!_isCollapsed ||
            _isPointerOverCompactActions ||
            _compactMarqueeCanvas is null ||
            _compactMarqueeViewport is null ||
            _compactMarqueeDistance <= 0)
        {
            StopCompactMarquee();
            return;
        }

        double elapsedMs = (DateTimeOffset.UtcNow - _compactMarqueeStartedAt).TotalMilliseconds;
        double movingMs = Math.Max(0, elapsedMs - CompactMarqueeStartDelayMs);
        double offset = movingMs * CompactMarqueeSpeedPixelsPerSecond / 1000;
        if (offset >= _compactMarqueeDistance)
        {
            _compactMarqueeStartedAt = DateTimeOffset.UtcNow;
            offset = 0;
        }

        _compactMarqueeCanvas.Translation = new Vector3((float)-offset, 0, 0);
    }

    private (TextBlock Primary, TextBlock Clone, Canvas Canvas, FrameworkElement Viewport, double NaturalWidth)?
        ResolveCompactMarqueeElements()
    {
        double titleWidth = MeasureCompactTextWidth(CompactTitleText);
        if (CanUseCompactMarqueeTarget(CompactTitleViewport, titleWidth))
        {
            return (
                CompactTitleText,
                CompactTitleMarqueeClone,
                CompactTitleMarqueeCanvas,
                CompactTitleViewport,
                titleWidth);
        }

        double summaryWidth = MeasureCompactTextWidth(CompactSummaryText);
        if (_showCompactSummary && CanUseCompactMarqueeTarget(CompactSummaryViewport, summaryWidth))
        {
            return (
                CompactSummaryText,
                CompactSummaryMarqueeClone,
                CompactSummaryMarqueeCanvas,
                CompactSummaryViewport,
                summaryWidth);
        }

        return null;
    }

    private static bool CanUseCompactMarqueeTarget(FrameworkElement viewport, double naturalWidth) =>
        viewport.Visibility == Visibility.Visible &&
        viewport.Width > 0 &&
        naturalWidth > viewport.Width + CompactMarqueeOverflowTolerance;

    private static double MeasureCompactTextWidth(TextBlock source)
    {
        var probe = new TextBlock
        {
            Text = source.Text,
            FontFamily = source.FontFamily,
            FontSize = source.FontSize,
            FontStretch = source.FontStretch,
            FontStyle = source.FontStyle,
            FontWeight = source.FontWeight,
            CharacterSpacing = source.CharacterSpacing,
            TextWrapping = TextWrapping.NoWrap
        };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private void StopCompactMarquee(bool resetDelayTimer = true)
    {
        if (resetDelayTimer)
        {
            _compactMarqueeDelayTimer?.Stop();
        }
        _compactMarqueeFrameTimer?.Stop();
        _compactMarqueeDistance = 0;
        ResetCompactMarqueeTarget();
    }

    private void ResetCompactMarqueeTarget()
    {
        if (_compactMarqueeCanvas is not null)
        {
            _compactMarqueeCanvas.Translation = Vector3.Zero;
        }
        if (_compactMarqueePrimary is not null && _compactMarqueeViewport is not null)
        {
            _compactMarqueePrimary.Width = Math.Max(1, _compactMarqueeViewport.Width);
            _compactMarqueePrimary.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        if (_compactMarqueeClone is not null)
        {
            _compactMarqueeClone.ClearValue(WidthProperty);
            _compactMarqueeClone.Visibility = Visibility.Collapsed;
        }

        _compactMarqueePrimary = null;
        _compactMarqueeClone = null;
        _compactMarqueeCanvas = null;
        _compactMarqueeViewport = null;
    }

    private void CollapsedChromeLayer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_isCollapsed)
        {
            return;
        }

        StopCompactMarquee();
        ApplyCompactAdaptiveLayout();
        QueueCompactMarquee(650);
    }

    public void SetCollapseActionAvailable(bool available)
    {
        _isCollapseActionAvailable = available;
        CollapseButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        UpdateOverlayDragHandleVisual(animate: false);
        ApplyChromeMode();
    }

    /// <summary>
    /// Keeps legacy dynamic title sizing centralized on the shell while host windows are migrated.
    /// </summary>
    public void SetTitleBarRowHeight(GridLength height)
    {
        _titleBarRowHeight = height;
        ApplyChromeMode();
    }

    public void SetTitleBarPadding(Thickness padding)
    {
        _titleBarPadding = padding;
        ApplyChromeMode();
    }

    /// <summary>
    /// Allows migrated windows to preserve their existing divider alignment during the transition.
    /// </summary>
    public void SetDividerMargin(Thickness margin)
    {
        HeaderDivider.Margin = margin;
    }

    private void ShellRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverShell = true;
        CompactPointerEntered?.Invoke(this, EventArgs.Empty);
        if (_isCollapsed)
        {
            ApplyCompactActionVisibility();
            UpdateCompactReorderHandleVisual();
            QueueCompactMarquee(500);
            return;
        }
        bool usesOverlay = ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden;

        if (usesOverlay)
        {
            SetOverlayChromeVisible(true);
            return;
        }

        ApplyActionButtonVisibility();
    }

    private void ShellRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverShell = false;
        CompactPointerExited?.Invoke(this, EventArgs.Empty);
        if (_isCollapsed)
        {
            ResetCompactInteractionRegions();
            UpdateCompactInteractionRegionHighlights();
            ApplyCompactActionVisibility();
            UpdateCompactReorderHandleVisual();
            return;
        }
        bool usesOverlay = ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden;

        if (usesOverlay)
        {
            SetOverlayChromeVisible(false);
            return;
        }

        ApplyActionButtonVisibility();
    }

    private void EnsureStoryboards()
    {
        if (_showButtonsStoryboard is not null)
        {
            return;
        }

        _rightButtonsTransform = new TranslateTransform { X = 12 };
        RightActionButtons.RenderTransform = _rightButtonsTransform;

        _showButtonsStoryboard = new Storyboard();

        var showOpacity = new DoubleAnimation
        {
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(showOpacity, RightActionButtons);
        Storyboard.SetTargetProperty(showOpacity, "Opacity");
        _showButtonsStoryboard.Children.Add(showOpacity);

        var showX = new DoubleAnimation
        {
            To = 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(showX, _rightButtonsTransform);
        Storyboard.SetTargetProperty(showX, "X");
        _showButtonsStoryboard.Children.Add(showX);

        _hideButtonsStoryboard = new Storyboard();

        var hideOpacity = new DoubleAnimation
        {
            To = 0.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(hideOpacity, RightActionButtons);
        Storyboard.SetTargetProperty(hideOpacity, "Opacity");
        _hideButtonsStoryboard.Children.Add(hideOpacity);

        var hideX = new DoubleAnimation
        {
            To = 12,
            Duration = new Duration(TimeSpan.FromMilliseconds(200)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        Storyboard.SetTarget(hideX, _rightButtonsTransform);
        Storyboard.SetTargetProperty(hideX, "X");
        _hideButtonsStoryboard.Children.Add(hideX);
    }

    private static void OnTitleBarContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.UpdateTitleBarContentVisibility();
        }
    }

    private static void OnOverlayTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.Bindings.Update();
        }
    }

    private static void OnTitleIconAppearanceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.Bindings.Update();
        }
    }

    private static void OnShowAddButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.Bindings.Update();
        }
    }

    private static void OnShowHoverButtonsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.ApplyChromeMode();
        }
    }

    private static void OnChromeModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.ApplyChromeMode();
        }
    }

    private static void OnTitleEditorContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.UpdateTitleEditorVisibility();
        }
    }

    private void UpdateTitleBarContentVisibility()
    {
        bool hasCustomTitleBar = TitleBarContent is not null;
        CustomTitleBarContentPresenter.Visibility = hasCustomTitleBar ? Visibility.Visible : Visibility.Collapsed;
        DefaultTitleBarContentHost.Visibility = hasCustomTitleBar ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ApplyChromeMode()
    {
        if (ShellRoot.RowDefinitions.Count < 2)
        {
            return;
        }

        if (_isCollapsed)
        {
            ShellRoot.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            ShellRoot.RowDefinitions[1].Height = new GridLength(0);
            TitleBarGrid.Visibility = Visibility.Collapsed;
            HeaderDivider.Visibility = Visibility.Collapsed;
            ShellContentPresenter.Visibility = Visibility.Collapsed;
            OverlayChromeLayer.Visibility = Visibility.Collapsed;
            CollapsedChromeLayer.Visibility = Visibility.Visible;
            return;
        }

        CollapsedChromeLayer.Visibility = Visibility.Collapsed;
        ShellContentPresenter.Visibility = Visibility.Visible;
        ShellRoot.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        bool usesOverlay = ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden;
        bool isEditingTitle = TitleEditorContent is not null;

        ShellRoot.RowDefinitions[0].Height = usesOverlay
            ? new GridLength(0)
            : _titleBarRowHeight;
        BackgroundPlate.Margin = new Thickness(0);
        Grid.SetRow(TitleBarGrid, usesOverlay ? 1 : 0);
        Canvas.SetZIndex(TitleBarGrid, usesOverlay ? 40 : 2);
        Canvas.SetZIndex(ShellContentPresenter, 1);
        TitleBarGrid.HorizontalAlignment = usesOverlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        TitleBarGrid.VerticalAlignment = usesOverlay ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        TitleBarGrid.Margin = usesOverlay ? new Thickness(0, -2, 6, 0) : new Thickness(0);
        TitleBarGrid.Padding = usesOverlay ? new Thickness(2, 0, 0, 0) : _titleBarPadding;
        RightActionButtons.VerticalAlignment = usesOverlay ? VerticalAlignment.Top : VerticalAlignment.Center;
        TitleBarGrid.Visibility = usesOverlay && !isEditingTitle ? Visibility.Collapsed : Visibility.Visible;

        HeaderDivider.Visibility = usesOverlay ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(ShellContentPresenter, usesOverlay ? 0 : 1);
        Grid.SetRowSpan(ShellContentPresenter, usesOverlay ? 2 : 1);
        ShellContentPresenter.Margin = new Thickness(0);
        TitleIdentityHost.Visibility = usesOverlay && !isEditingTitle ? Visibility.Collapsed : Visibility.Visible;
        OverlayChromeLayer.Visibility = usesOverlay && !isEditingTitle ? Visibility.Visible : Visibility.Collapsed;
        OverlayIdentityHost.Visibility = Visibility.Collapsed;
        OverlayDragHandle.Visibility = usesOverlay && !isEditingTitle ? Visibility.Visible : Visibility.Collapsed;

        if (usesOverlay)
        {
            RightActionButtons.Opacity = 0;
            RightActionButtons.IsHitTestVisible = false;
        }
        else
        {
            ApplyActionButtonVisibility();
        }

        SetOverlayChromeVisible(_isPointerOverShell, animateButtons: false);
        ApplyActionButtonSurface(false);
    }

    private void ApplyActionButtonVisibility()
    {
        _showButtonsStoryboard?.Stop();
        _hideButtonsStoryboard?.Stop();
        HoverActionButtons.Visibility = ShowHoverButtons ? Visibility.Visible : Visibility.Collapsed;
        RightActionButtons.Opacity = 1;
        RightActionButtons.IsHitTestVisible = ShowHoverButtons || _isCollapseActionAvailable;
        if (_rightButtonsTransform is not null)
        {
            _rightButtonsTransform.X = 0;
        }
    }

    private void SetOverlayChromeVisible(bool isVisible, bool animateButtons = true)
    {
        bool isEditingTitle = TitleEditorContent is not null;
        bool usesOverlay = ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden;
        bool showHandle = usesOverlay && !isEditingTitle && (isVisible || _isDragHandlePressed);

        OverlayIdentityHost.Opacity = 0;
        OverlayDragHandle.Opacity = showHandle ? 1 : 0;
        OverlayDragHandle.IsHitTestVisible = showHandle;

        if (!animateButtons)
        {
            if (ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden)
            {
                RightActionButtons.Opacity = 0;
            }
        }
    }

    private void ApplyActionButtonSurface(bool isOverlay)
    {
        var background = isOverlay ? CreateOpaqueOverlayButtonBackground() : new SolidColorBrush(Colors.Transparent);
        var border = isOverlay ? CreateOpaqueOverlayButtonBorder() : new SolidColorBrush(Colors.Transparent);
        var thickness = isOverlay ? new Thickness(0.8) : new Thickness(0);

        foreach (var button in new[] { PositionLockButton, SizeLockButton, AddButton, CollapseButton, MoreButton, CloseButton })
        {
            button.Background = background;
            button.BorderBrush = border;
            button.BorderThickness = thickness;
        }
    }

    private void ApplyCompactActionVisibility(bool animate = true)
    {
        bool visible = _isCollapsed &&
            (IsPointerOverCompactActionRegion() || _isCompactKeyboardFocused);
        CompactActionHost.IsHitTestVisible = visible;
        if (!animate || !SystemAnimationsEnabled())
        {
            CompactActionHost.Opacity = visible ? 1 : 0;
            CompactActionHostTransform.X = visible ? 0 : 16;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(visible ? 200 : 150);
        var easing = new CubicEase
        {
            EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn
        };

        var opacityAnim = new DoubleAnimation
        {
            To = visible ? 1 : 0,
            Duration = new Duration(duration),
            EasingFunction = easing
        };
        var slideAnim = new DoubleAnimation
        {
            To = visible ? 0 : 16,
            Duration = new Duration(duration),
            EasingFunction = easing
        };

        var storyboard = new Storyboard();
        storyboard.Children.Add(opacityAnim);
        storyboard.Children.Add(slideAnim);
        Storyboard.SetTarget(opacityAnim, CompactActionHost);
        Storyboard.SetTargetProperty(opacityAnim, "Opacity");
        Storyboard.SetTarget(slideAnim, CompactActionHostTransform);
        Storyboard.SetTargetProperty(slideAnim, "X");
        storyboard.Begin();
    }

    private void UpdateCompactReorderHandleVisual(bool animate = true)
    {
        bool visible = _isCompactReorderEnabled &&
            _isCollapsed &&
            (_isPointerOverShell || _isDragHandlePressed || _isCompactKeyboardFocused);
        CompactReorderHandle.IsHitTestVisible = _isCompactReorderEnabled && _isCollapsed;
        double targetOpacity = visible ? 1 : 0;

        _compactReorderHandleStoryboard?.Stop();
        if (!animate || !SystemAnimationsEnabled())
        {
            CompactReorderHandle.Opacity = targetOpacity;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(visible ? 140 : 100)),
            EasingFunction = new CubicEase
            {
                EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, CompactReorderHandle);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        _compactReorderHandleStoryboard = storyboard;
        storyboard.Begin();
    }

    private static bool SystemAnimationsEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
        }
    }

    private void CollapseButton_Click(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke(this, e);

    private void CompactExpandButton_Click(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke(this, e);

    private void CompactPreviousButton_Click(object sender, RoutedEventArgs e) => CompactPreviousRequested?.Invoke(this, e);

    private void CompactPrimaryActionButton_Click(object sender, RoutedEventArgs e) =>
        CompactPrimaryActionRequested?.Invoke(this, e);

    private void CompactPlayPauseButton_Click(object sender, RoutedEventArgs e) => CompactPlayPauseRequested?.Invoke(this, e);

    private void CompactNextButton_Click(object sender, RoutedEventArgs e) => CompactNextRequested?.Invoke(this, e);

    private void CompactActionHost_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverCompactActions = true;
        UpdateCompactInteractionRegionHighlights();
        StopCompactMarquee();
        UpdateCompactActionRegionState();
        ApplyCompactActionVisibility();
    }

    private void CompactActionHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverCompactActions = false;
        QueueCompactActionRegionRefreshAfterExit();
    }

    private void CompactIdentityHost_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_isCollapsed || _isPointerOverCompactIdentity)
        {
            return;
        }

        _isPointerOverCompactIdentity = true;
        UpdateCompactInteractionRegionHighlights();
        StopCompactMarquee();
        AnimateDragGripIndicator(true);
        CompactMoveHandlePointerEntered?.Invoke(this, EventArgs.Empty);
    }

    private void CompactIdentityHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerOverCompactIdentity)
        {
            return;
        }

        _isPointerOverCompactIdentity = false;
        UpdateCompactInteractionRegionHighlights();
        AnimateDragGripIndicator(false);
        CompactMoveHandlePointerExited?.Invoke(this, EventArgs.Empty);
        if (_isPointerOverShell)
        {
            QueueCompactMarquee(650);
        }
    }

    private void AnimateDragGripIndicator(bool show)
    {
        if (!SystemAnimationsEnabled())
        {
            CompactDragGripIndicator.Opacity = show ? 0.7 : 0;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = show ? 0.7 : 0,
            Duration = new Duration(TimeSpan.FromMilliseconds(show ? 150 : 100)),
            EasingFunction = new CubicEase
            {
                EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, CompactDragGripIndicator);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void CompactReorderHandle_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_isCollapsed || !_isCompactReorderEnabled)
        {
            return;
        }

        _isPointerOverCompactReorderHandle = true;
        UpdateCompactInteractionRegionHighlights();
        CompactReorderGlyph.Opacity = 0.92;
        StopCompactMarquee();
        UpdateCompactActionRegionState();
        ApplyCompactActionVisibility();
    }

    private void CompactReorderHandle_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverCompactReorderHandle = false;
        CompactReorderGlyph.Opacity = 0.58;
        if (!_isCollapsed || !_isCompactReorderEnabled)
        {
            return;
        }

        QueueCompactActionRegionRefreshAfterExit();
    }

    private void CompactTextContainer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_isCollapsed)
        {
            return;
        }

        bool identityWasActive = _isPointerOverCompactIdentity;
        bool actionRegionWasActive = IsPointerOverCompactActionRegion();
        _isPointerOverCompactIdentity = false;
        _isPointerOverCompactActions = false;
        _isPointerOverCompactActionTrigger = false;
        _isPointerOverCompactReorderHandle = false;
        CompactReorderGlyph.Opacity = 0.58;
        AnimateDragGripIndicator(false);
        if (identityWasActive)
        {
            CompactMoveHandlePointerExited?.Invoke(this, EventArgs.Empty);
        }
        if (actionRegionWasActive)
        {
            UpdateCompactActionRegionState();
        }

        _isPointerOverCompactExpansionZone = true;
        UpdateCompactInteractionRegionHighlights();
        AnimateTextHoverBackground(true);
        ApplyCompactActionVisibility();
        CompactExpansionPointerEntered?.Invoke(this, EventArgs.Empty);
    }

    private void CompactActionRegionTrigger_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (!_isCollapsed)
        {
            return;
        }

        _isPointerOverCompactActionTrigger = true;
        StopCompactMarquee();
        UpdateCompactInteractionRegionHighlights();
        UpdateCompactActionRegionState();
        ApplyCompactActionVisibility();
    }

    private void CompactActionRegionTrigger_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverCompactActionTrigger = false;
        QueueCompactActionRegionRefreshAfterExit();
    }

    private void CompactTextContainer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        AnimateTextHoverBackground(false);
        if (!_isPointerOverCompactExpansionZone)
        {
            return;
        }

        _isPointerOverCompactExpansionZone = false;
        CompactExpansionPointerExited?.Invoke(this, EventArgs.Empty);
    }

    private void AnimateTextHoverBackground(bool show)
    {
        double targetOpacity = show ? 0.5 : 0;
        if (!SystemAnimationsEnabled())
        {
            CompactTextHoverBackground.Opacity = targetOpacity;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(show ? 120 : 150)),
            EasingFunction = new CubicEase
            {
                EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, CompactTextHoverBackground);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        storyboard.Begin();
    }

    private void UpdateCompactInteractionRegionHighlights()
    {
        double identityTarget = _isCollapsed && _isPointerOverCompactIdentity ? 1 : 0;
        double actionTarget = _isCollapsed && IsPointerOverCompactActionRegion() ? 1 : 0;
        AnimateOpacity(CompactIdentityRegionHighlight, identityTarget);
        AnimateOpacity(CompactActionRegionHighlight, actionTarget);
    }

    private void AnimateOpacity(UIElement element, double target)
    {
        if (Math.Abs(element.Opacity - target) < 0.01) return;
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(100),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, element);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
    }

    private void ResetCompactInteractionRegions()
    {
        bool identityWasActive = _isPointerOverCompactIdentity;
        bool expansionWasActive = _isPointerOverCompactExpansionZone;
        _isPointerOverCompactIdentity = false;
        _isPointerOverCompactExpansionZone = false;
        _isPointerOverCompactActions = false;
        _isPointerOverCompactActionTrigger = false;
        _isPointerOverCompactReorderHandle = false;

        if (identityWasActive)
        {
            CompactMoveHandlePointerExited?.Invoke(this, EventArgs.Empty);
        }

        if (expansionWasActive)
        {
            CompactExpansionPointerExited?.Invoke(this, EventArgs.Empty);
        }

        UpdateCompactActionRegionState();
    }

    private bool IsPointerOverCompactActionRegion() =>
        _isPointerOverCompactActionTrigger ||
        _isPointerOverCompactActions ||
        _isPointerOverCompactReorderHandle;

    private void UpdateCompactActionRegionState()
    {
        bool active = _isCollapsed && IsPointerOverCompactActionRegion();
        if (_isCompactActionRegionReported == active)
        {
            return;
        }

        _isCompactActionRegionReported = active;
        if (active)
        {
            CompactActionPointerEntered?.Invoke(this, EventArgs.Empty);
        }
        else
        {
            CompactActionPointerExited?.Invoke(this, EventArgs.Empty);
        }
    }

    private void QueueCompactActionRegionRefreshAfterExit()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateCompactInteractionRegionHighlights();
            UpdateCompactActionRegionState();
            ApplyCompactActionVisibility();
            if (!IsPointerOverCompactActionRegion())
            {
                QueueCompactMarquee(650);
            }
        });
    }

    private void CollapsedChromeLayer_GotFocus(object sender, RoutedEventArgs e)
    {
        _isCompactKeyboardFocused = true;
        ApplyCompactActionVisibility();
    }

    private void CollapsedChromeLayer_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        // Forward to the same event as title bar right-click → widget windows show their context menu
        TitleRightTapped?.Invoke(this, e);
    }

    private void CollapsedChromeLayer_LostFocus(object sender, RoutedEventArgs e)
    {
        _isCompactKeyboardFocused = false;
        ApplyCompactActionVisibility();
    }

    private void ShellRoot_DragEnter(object sender, DragEventArgs e) => CompactDragEntered?.Invoke(this, EventArgs.Empty);

    private void ShellRoot_DragLeave(object sender, DragEventArgs e) => CompactDragLeft?.Invoke(this, EventArgs.Empty);

    private void ShellRoot_Drop(object sender, DragEventArgs e) => CompactDropCompleted?.Invoke(this, EventArgs.Empty);

    private Brush CreateOpaqueOverlayButtonBackground()
    {
        bool isDark = ActualTheme == ElementTheme.Dark ||
            ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark;
        return new SolidColorBrush(isDark
            ? Color.FromArgb(0xFF, 0x2C, 0x2F, 0x36)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    private Brush CreateOpaqueOverlayButtonBorder()
    {
        bool isDark = ActualTheme == ElementTheme.Dark ||
            ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark;
        return new SolidColorBrush(isDark
            ? Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x2E, 0x00, 0x00, 0x00));
    }

    private static Brush GetBrushResourceOrFallback(string resourceKey, Color fallbackColor)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out object? resource))
        {
            return resource switch
            {
                Brush brush => brush,
                Color color => new SolidColorBrush(color),
                _ => new SolidColorBrush(fallbackColor)
            };
        }

        return new SolidColorBrush(fallbackColor);
    }

    private void UpdateTitleEditorVisibility()
    {
        bool isEditingTitle = TitleEditorContent is not null;
        TitleEditorPresenter.Visibility = isEditingTitle ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Visibility = isEditingTitle ? Visibility.Collapsed : Visibility.Visible;
        ApplyChromeMode();
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        AddRequested?.Invoke(this, e);
    }

    private void PositionLockButton_Click(object sender, RoutedEventArgs e)
    {
        PositionLockRequested?.Invoke(this, e);
    }

    private void SizeLockButton_Click(object sender, RoutedEventArgs e)
    {
        SizeLockRequested?.Invoke(this, e);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        MoreRequested?.Invoke(this, e);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke(this, e);
    }

    private void TitleText_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        TitleDoubleTapped?.Invoke(this, e);
    }

    private void TitleBarGrid_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        TitleRightTapped?.Invoke(this, e);
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        TitlePointerPressed?.Invoke(this, e);
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        TitlePointerMoved?.Invoke(this, e);
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        TitlePointerReleased?.Invoke(this, e);
    }

    private void TitleBarGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // When pointer capture is lost mid-drag (e.g., alt-tab, UAC),
        // notify the parent window so it can call EndWindowDragCore.
        TitlePointerReleased?.Invoke(this, e);
    }

    private void OverlayDragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isCollapsed && e.OriginalSource is DependencyObject source && IsWithin(source, CompactActionHost))
        {
            return;
        }

        if (!e.GetCurrentPoint(DragHandleElement).Properties.IsLeftButtonPressed)
        {
            return;
        }

        bool startsWindowDrag = true;
        if (_isCollapsed)
        {
            StopCompactMarquee();
            CompactPointerPressed?.Invoke(this, EventArgs.Empty);
            // Pressed state: reduce hover mask opacity
            CompactIdentityRegionHighlight.Opacity *= 0.5;
            CompactActionRegionHighlight.Opacity *= 0.5;
            bool pressedMoveHandle = e.OriginalSource is DependencyObject moveSource &&
                IsWithin(moveSource, CompactIdentityHost);
            bool pressedReorderHandle = _isCompactReorderEnabled &&
                e.OriginalSource is DependencyObject reorderSource &&
                IsWithin(reorderSource, CompactReorderHandle);
            _isCompactMoveHandlePress = pressedMoveHandle;
            startsWindowDrag = pressedMoveHandle || pressedReorderHandle;
            _pendingDragHandleClickAction = startsWindowDrag
                ? DragHandleClickAction.None
                : DragHandleClickAction.Expand;
        }
        else if (_isCollapseActionAvailable && IsOverlayChromeMode)
        {
            _isCompactMoveHandlePress = false;
            _pendingDragHandleClickAction = DragHandleClickAction.Collapse;
        }
        else
        {
            _isCompactMoveHandlePress = false;
            _pendingDragHandleClickAction = DragHandleClickAction.None;
        }

        _hasDragHandlePressMoved = false;
        _dragHandlePressPoint = e.GetCurrentPoint(DragHandleElement).Position;
        _isDragHandlePressed = true;
        DragHandleElement.CapturePointer(e.Pointer);
        UpdateOverlayDragHandleVisual();
        UpdateCompactReorderHandleVisual();
        SetOverlayChromeVisible(true);
        if (startsWindowDrag)
        {
            DragHandlePointerPressed?.Invoke(this, e);
        }
    }

    private void OverlayDragHandle_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_pendingDragHandleClickAction != DragHandleClickAction.None && !_hasDragHandlePressMoved)
        {
            Windows.Foundation.Point current = e.GetCurrentPoint(DragHandleElement).Position;
            double deltaX = current.X - _dragHandlePressPoint.X;
            double deltaY = current.Y - _dragHandlePressPoint.Y;
            _hasDragHandlePressMoved = (deltaX * deltaX) + (deltaY * deltaY) >= 25;
            if (_hasDragHandlePressMoved)
            {
                UpdateOverlayDragHandleVisual();
            }
        }

        DragHandlePointerMoved?.Invoke(this, e);
    }

    private void OverlayDragHandle_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        // Restore pressed state
        if (_isCollapsed)
        {
            UpdateCompactInteractionRegionHighlights();
        }
        DragHandleClickAction clickAction = _hasDragHandlePressMoved
            ? DragHandleClickAction.None
            : _pendingDragHandleClickAction;
        DragHandlePointerReleased?.Invoke(this, e);
        EndDragHandlePress(e.Pointer);
        if (clickAction == DragHandleClickAction.Expand)
        {
            CompactBodyExpandRequested?.Invoke(this, e);
        }
        else if (clickAction == DragHandleClickAction.Collapse)
        {
            CollapseRequested?.Invoke(this, e);
        }
    }

    private void OverlayDragHandle_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // When pointer capture is lost mid-drag (e.g., alt-tab, UAC),
        // notify the parent window so it can call EndWindowDragCore.
        DragHandlePointerReleased?.Invoke(this, e);
        EndDragHandlePress(e.Pointer);
    }

    private void EndDragHandlePress(Pointer pointer)
    {
        if (!_isDragHandlePressed)
        {
            return;
        }

        _isDragHandlePressed = false;
        _pendingDragHandleClickAction = DragHandleClickAction.None;
        _hasDragHandlePressMoved = false;
        _isCompactMoveHandlePress = false;
        DragHandleElement.ReleasePointerCapture(pointer);
        UpdateOverlayDragHandleVisual();
        UpdateCompactReorderHandleVisual();
        SetOverlayChromeVisible(_isPointerOverShell);
    }

    private void OverlayDragHandle_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverDragHandle = true;
        UpdateOverlayDragHandleVisual();
    }

    private void OverlayDragHandle_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverDragHandle = false;
        UpdateOverlayDragHandleVisual();
    }

    private void UpdateOverlayDragHandleVisual(bool animate = true)
    {
        bool showCollapseCue = !_isCollapsed &&
            _isCollapseActionAvailable &&
            _isPointerOverDragHandle &&
            !_isDragHandlePressed;
        double gripOpacity = showCollapseCue ? 0 : 0.76;
        double chevronOpacity = showCollapseCue ? 0.9 : 0;

        _overlayHandleVisualStoryboard?.Stop();
        if (!animate || !SystemAnimationsEnabled())
        {
            OverlayDragGrip.Opacity = gripOpacity;
            OverlayCollapseChevron.Opacity = chevronOpacity;
            return;
        }

        var storyboard = new Storyboard();
        AddOpacityAnimation(storyboard, OverlayDragGrip, gripOpacity);
        AddOpacityAnimation(storyboard, OverlayCollapseChevron, chevronOpacity);
        _overlayHandleVisualStoryboard = storyboard;
        storyboard.Begin();
    }

    private static void AddOpacityAnimation(
        Storyboard storyboard,
        FrameworkElement target,
        double opacity)
    {
        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = new Duration(TimeSpan.FromMilliseconds(100)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
    }

    private static void SetProtectedCursor(UIElement element, InputSystemCursorShape shape)
    {
        var property = typeof(UIElement).GetProperty(
            "ProtectedCursor",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        property?.SetValue(element, InputSystemCursor.Create(shape));
    }

    private static bool IsWithin(DependencyObject source, DependencyObject target)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, target))
            {
                return true;
            }
        }

        return false;
    }
}
