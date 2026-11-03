using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Media.Animation;
using System.Numerics;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace DeskBox.Controls;

public sealed class WidgetMenuRequestedEventArgs : EventArgs
{
    public WidgetMenuRequestedEventArgs(
        FrameworkElement anchor,
        Windows.Foundation.Point? pointerPosition)
    {
        Anchor = anchor ?? throw new ArgumentNullException(nameof(anchor));
        PointerPosition = pointerPosition;
    }

    public FrameworkElement Anchor { get; }

    public Windows.Foundation.Point? PointerPosition { get; }
}

public sealed partial class WidgetShell : UserControl
{
    // WinUI keeps the animation clocks associated with a storyboard until the
    // storyboard is stopped and its children are detached.  Compact pointer
    // feedback is triggered frequently, so every short-lived storyboard gets
    // one reusable slot with explicit completion cleanup.
    private sealed class StoryboardSlot
    {
        private EventHandler<object>? _completed;
        private object? _key;

        public Storyboard? Active { get; private set; }

        public bool IsActiveFor(object? key) =>
            Active is not null && Equals(_key, key);

        public void Begin(
            Storyboard storyboard,
            object? key = null,
            Action? onCompleted = null)
        {
            StopAndClear();

            EventHandler<object>? completed = null;
            completed = (_, _) =>
            {
                storyboard.Completed -= completed;
                storyboard.Stop();
                storyboard.Children.Clear();
                if (!ReferenceEquals(Active, storyboard))
                {
                    return;
                }

                Active = null;
                _completed = null;
                _key = null;
                onCompleted?.Invoke();
            };

            Active = storyboard;
            _completed = completed;
            _key = key;
            storyboard.Completed += completed;
            try
            {
                storyboard.Begin();
            }
            catch
            {
                StopAndClear();
                throw;
            }
        }

        public void StopAndClear()
        {
            Storyboard? storyboard = Active;
            EventHandler<object>? completed = _completed;
            Active = null;
            _completed = null;
            _key = null;
            if (storyboard is null)
            {
                return;
            }

            if (completed is not null)
            {
                storyboard.Completed -= completed;
            }

            storyboard.Stop();
            storyboard.Children.Clear();
        }
    }

    private const double MoreMenuPointerOffsetDips = 4;
    private const long MoreMenuPointerMaximumAgeMilliseconds = 1000;
    private const double TitleBarClickDragThresholdSquared = 25;
    private const double CompactMarqueeGap = 32;
    private const double CompactMarqueeStartDelayMs = 900;
    private const double CompactMarqueeSpeedPixelsPerSecond = 50;
    private const double CompactMarqueeOverflowTolerance = 4;
    private const double CompactActionTrailingPadding = 2;
    private const double CompactReorderHandleWidth = 18;
    private const int DeferredExpandedContentRevealDurationMs = 150;
    internal const double CompactLiveIndeterminateDurationSeconds = 1.0 / 0.875;
    internal const double EdgeGlowPulseDurationSeconds = Math.PI * 2 / 0.8;
    private WidgetCompactTransitionVisualProfile _compactTransitionProfile =
        WidgetCompactTransitionVisualProfile.Resolve(
            SettingsService.WidgetCompactAnimationSmooth,
            SettingsService.DefaultWidgetCompactAnimationDurationMs,
            true);
    private bool _isShellDragActive;
    private bool _isCompactCompositionTransitionActive;
    private bool _isCompactDeferredContentPresentationActive;
    private bool _isDeferredContentRevealAnimationActive;
    private double _lastCompactTransitionCornerRadius = double.NaN;

    internal bool IsCompactDeferredContentPresentationActive =>
        _isCompactDeferredContentPresentationActive;

    private bool _hasPendingGroupTitleForeground;
    private Color _groupTitlePrimaryColor;
    private Color _groupTitleSecondaryColor;
    private Color _groupTitleDisabledColor;
    private bool _groupTitleHighContrast;
    private double _lastGroupTitleIconSize = 16;

    private WidgetFeedbackPresenter EnsureFeedbackPresenter()
    {
        if (FeedbackPresenter is null)
        {
            _ = FindName(nameof(FeedbackPresenter));
        }

        return FeedbackPresenter!;
    }

    private WidgetGroupTitleSwitcher EnsureGroupTitleSwitcher()
    {
        if (GroupTitleSwitcher is not null)
        {
            return GroupTitleSwitcher;
        }

        _ = FindName(nameof(GroupTitleSwitcher));
        WidgetGroupTitleSwitcher switcher = GroupTitleSwitcher!;
        switcher.IconSize = _lastGroupTitleIconSize;
        if (_hasPendingGroupTitleForeground)
        {
            switcher.SetTabForegroundColors(
                _groupTitlePrimaryColor,
                _groupTitleSecondaryColor,
                _groupTitleDisabledColor,
                _groupTitleHighContrast);
        }

        switcher.MemberInvoked += (_, e) => GroupMemberInvoked?.Invoke(this, e);
        switcher.RemoveMemberRequested += (_, e) => GroupMemberRemoveRequested?.Invoke(this, e);
        switcher.DetachMemberRequested += (_, e) => GroupMemberDetachRequested?.Invoke(this, e);
        switcher.DetachDragStarted += (_, e) => GroupMemberDetachDragStarted?.Invoke(this, e);
        switcher.DetachDragCompleted += (_, e) => GroupMemberDetachDragCompleted?.Invoke(this, e);
        switcher.ReorderRequested += (_, e) =>
        {
            if (GroupMemberReorderRequested is { } handler)
            {
                handler(this, e);
            }
            else
            {
                e.Complete(false);
            }
        };
        switcher.DissolveRequested += (_, _) => GroupDissolveRequested?.Invoke(this, EventArgs.Empty);
        switcher.PickerOpened += (_, _) => GroupPickerOpened?.Invoke(this, EventArgs.Empty);
        switcher.PickerClosed += (_, _) => GroupPickerClosed?.Invoke(this, EventArgs.Empty);
        Bindings.Update();
        return switcher;
    }

    public void ShowFeedback(WidgetFeedbackRequest request)
    {
        WidgetFeedbackPresenter presenter = EnsureFeedbackPresenter();
        presenter.IsCompact = _isCollapsed;
        presenter.Show(request);
    }

    public void ClearFeedback(string? deduplicationKey = null) =>
        FeedbackPresenter?.Clear(deduplicationKey);

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

    public static readonly DependencyProperty TitleIconCustomEmojiProperty =
        DependencyProperty.Register(
            nameof(TitleIconCustomEmoji),
            typeof(string),
            typeof(WidgetShell),
            new PropertyMetadata(string.Empty, OnTitleIconAppearanceChanged));

    public static readonly DependencyProperty TitleIconCustomSourceProperty =
        DependencyProperty.Register(
            nameof(TitleIconCustomSource),
            typeof(ImageSource),
            typeof(WidgetShell),
            new PropertyMetadata(null, OnTitleIconAppearanceChanged));

    public static readonly DependencyProperty CustomBackgroundSourceProperty =
        DependencyProperty.Register(
            nameof(CustomBackgroundSource),
            typeof(ImageSource),
            typeof(WidgetShell),
            new PropertyMetadata(null, OnCustomBackgroundChanged));

    public static readonly DependencyProperty CustomBackgroundFitProperty =
        DependencyProperty.Register(
            nameof(CustomBackgroundFit),
            typeof(string),
            typeof(WidgetShell),
            new PropertyMetadata(
                Services.WidgetBackgroundCustomization.FitFill,
                OnCustomBackgroundChanged));

    public static readonly DependencyProperty CustomBackgroundDimProperty =
        DependencyProperty.Register(
            nameof(CustomBackgroundDim),
            typeof(double),
            typeof(WidgetShell),
            new PropertyMetadata(
                Services.WidgetBackgroundCustomization.DefaultDimPercent / 100d,
                OnCustomBackgroundChanged));

    public static readonly DependencyProperty CustomBackgroundPanoramaSourceProperty =
        DependencyProperty.Register(
            nameof(CustomBackgroundPanoramaSource),
            typeof(ImageSource),
            typeof(WidgetShell),
            new PropertyMetadata(null, OnCustomBackgroundChanged));

    /// <summary>
    /// Per-widget solid background color as an opaque #RRGGBB hex string.
    /// Stored as a string (the same shape as the foreground-color override)
    /// rather than a nullable struct so the dependency property stays a plain
    /// reference type; null or unparsable means "no color override".
    /// </summary>
    public static readonly DependencyProperty CustomBackgroundColorHexProperty =
        DependencyProperty.Register(
            nameof(CustomBackgroundColorHex),
            typeof(string),
            typeof(WidgetShell),
            new PropertyMetadata(null, OnCustomBackgroundChanged));

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

    private readonly StoryboardSlot _overlayHandleVisualStoryboard = new();
    private readonly StoryboardSlot _compactLiveStoryboard = new();
    private readonly StoryboardSlot _compactUpdateStoryboard = new();
    private readonly StoryboardSlot _compactReorderHandleStoryboard = new();
    private readonly StoryboardSlot _compactFullBleedVisibilityStoryboard = new();
    private readonly StoryboardSlot _compactEdgeGlowFlashStoryboard = new();
    private readonly StoryboardSlot _compactActionVisibilityStoryboard = new();
    private readonly StoryboardSlot _compactTextHoverStoryboard = new();
    private readonly StoryboardSlot _compactActionHighlightStoryboard = new();
    private bool _compactTextViewportUpdateQueued;
    private ScalarKeyFrameAnimation? _groupDropBreathingAnimation;
    private DispatcherQueueTimer? _compactMarqueeDelayTimer;
    private ScalarKeyFrameAnimation? _compactMarqueeAnimation;
    private TranslateTransform? _rightButtonsTransform;
    private TextBlock? _compactMarqueePrimary;
    private TextBlock? _compactMarqueeClone;
    private Grid? _compactMarqueeTrack;
    private FrameworkElement? _compactMarqueeViewport;
    private WidgetCompactPresentation? _compactPresentation;
    private WidgetGroupPresentation? _groupPresentation;
    private IWidgetContent? _hostedContent;
    private IWidgetResponsiveLayoutContent? _responsiveLayoutContent;
    private readonly InsetClip _contentTransitionClip;
    private FileWidgetContentAdapter? _transitionOutgoingFileSurface;
    private FileWidgetContentAdapter? _transitionIncomingFileSurface;
    private bool _isContentSnapshotTransitionActive;
    private bool _isContentTransitionClipAttached;
    private bool _isCompactRenderWarmupActive;
    private bool _isResponsiveLayoutTransitionActive;
    private double _responsiveTargetContentWidth;
    private double _responsiveTargetContentHeight;
    private WidgetCompactExpansionAnchor _transitionExpansionAnchor = WidgetCompactExpansionAnchor.LeftTop;
    private double _transitionFrozenWindowWidth;
    private bool _transitionHasHorizontalMorph = true;
    private double _transitionFrozenContentHeight;
    private bool _isTransitionContentLayoutFrozen;
    private bool _isTransitionTitleBarLayoutFrozen;
    private WidgetCompactWidthTier _compactWidthTier = WidgetCompactWidthTier.Standard;
    private bool _isPointerOverShell;
    private bool _isHostVisualActivityEnabled;
    private bool _isCollapsed;
    private bool _isCollapseActionAvailable;
    private bool _isMinimalCompactStyle;
    private bool _usesStackedCompactText;
    private bool _isCompactKeyboardFocused;
    private bool _usesSmartCompactBehavior;
    private bool _showCompactSummary;
    private bool _isPointerOverCompactIdentity;
    private bool _isPointerOverCompactExpansionZone;
    private bool _isCompactMoveHintLatched;
    private string _compactMoveHintText = string.Empty;
    private string _compactExpandHintText = string.Empty;
    private bool _isPointerOverCompactActions;
    private bool _isPointerOverCompactActionTrigger;
    private bool _isPointerOverCompactReorderHandle;
    private bool _isCompactActionRegionReported;
    private bool _isCompactTransitionActive;
    private bool _isDragHandlePressed;
    private bool _isCompactMoveHandlePress;
    private bool _isCompactReorderEnabled;
    private bool _isCompactVinylRotating;
    private float _compactVinylBaseAngleDegrees;
    private long _compactVinylRotationStartedTimestamp;
    private bool _isPointerOverDragHandle;
    private Windows.Foundation.Point? _pendingMoreMenuPointerPosition;
    private long _pendingMoreMenuPointerCapturedAt;
    private int _moreMenuPointerCaptureVersion;
    private DragHandleClickAction _pendingDragHandleClickAction;
    private bool _hasDragHandlePressMoved;
    private Windows.Foundation.Point _dragHandlePressPoint;
    private bool _isTitleBarPressTrackingClick;
    private bool _hasTitleBarPressMoved;
    private bool _suppressNextTitleBarClickCollapse;
    private Windows.Foundation.Point _titleBarPressPoint;
    private DispatcherQueueTimer? _titleBarClickCollapseTimer;
    private double _compactOuterCornerRadius = 16;
    private double _compactInnerCornerRadius = 8;
    private double _compactMediaCornerRadius = 8;
    private double _expandedOuterCornerRadius = 8;
    private double _transitionOuterCornerRadiusFrom = 8;
    private double _transitionOuterCornerRadiusTo = 8;
    private GridLength _titleBarRowHeight = new(40);
    private Thickness _titleBarPadding = new(14, 4, 12, 4);

    private enum DragHandleClickAction
    {
        None,
        Expand,
        Collapse,
        OpenGroup
    }

    public event EventHandler<RoutedEventArgs>? AddRequested;
    public event EventHandler<RoutedEventArgs>? PositionLockRequested;
    public event EventHandler<RoutedEventArgs>? SizeLockRequested;
    public event EventHandler<WidgetMenuRequestedEventArgs>? MoreRequested;
    public event EventHandler<RoutedEventArgs>? CloseRequested;
    public event EventHandler<RoutedEventArgs>? CollapseRequested;
    public event EventHandler<RoutedEventArgs>? ExpandRequested;
    public event EventHandler<RoutedEventArgs>? CompactBodyExpandRequested;
    public event EventHandler<RoutedEventArgs>? CompactPreviousRequested;
    public event EventHandler<RoutedEventArgs>? CompactPrimaryActionRequested;
    public event EventHandler<RoutedEventArgs>? CompactPlayPauseRequested;
    public event EventHandler<RoutedEventArgs>? CompactNextRequested;
    public event EventHandler? CompactPointerEntered;
    public event EventHandler? CompactPointerMoved;
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
    public event EventHandler<WidgetGroupMemberEventArgs>? GroupMemberInvoked;
    public event EventHandler<WidgetGroupMemberEventArgs>? GroupMemberRemoveRequested;
    public event EventHandler<WidgetGroupMemberEventArgs>? GroupMemberDetachRequested;
    public event EventHandler<WidgetGroupMemberEventArgs>? GroupMemberDetachDragStarted;
    public event EventHandler<WidgetGroupMemberEventArgs>? GroupMemberDetachDragCompleted;
    public event EventHandler<WidgetGroupReorderEventArgs>? GroupMemberReorderRequested;
    public event EventHandler? GroupDissolveRequested;
    public event EventHandler? GroupPickerOpened;
    public event EventHandler? GroupPickerClosed;
    public event EventHandler? HostedContentChanged;

    public WidgetShell()
    {
        InitializeComponent();
        Visual contentTransitionVisual =
            ElementCompositionPreview.GetElementVisual(
                ContentTransitionViewport);
        _contentTransitionClip =
            contentTransitionVisual.Compositor.CreateInsetClip();
        contentTransitionVisual.Clip = _contentTransitionClip;
        _isContentTransitionClipAttached = true;
        ShellRoot.AddHandler(UIElement.DragEnterEvent, new DragEventHandler(ShellRoot_DragEnter), true);
        ShellRoot.AddHandler(UIElement.DragLeaveEvent, new DragEventHandler(ShellRoot_DragLeave), true);
        ShellRoot.AddHandler(UIElement.DropEvent, new DragEventHandler(ShellRoot_Drop), true);
        TitleBarGrid.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(TitleBarGrid_PointerWheelChanged),
            true);
        MoreButton.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(MoreButton_PointerPressed),
            true);
        MoreButton.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(MoreButton_PointerReleased),
            true);
        MoreButton.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(MoreButton_PointerCanceled),
            true);
        MoreButton.AddHandler(
            UIElement.KeyDownEvent,
            new KeyEventHandler(MoreButton_KeyDown),
            true);
        RightActionButtons.SizeChanged += (_, _) =>
        {
            _rightButtonsTransform = RightActionButtons.RenderTransform as TranslateTransform;
        };
        Loaded += (_, _) =>
        {
            ApplyChromeMode();
            ApplyCompactAdaptiveLayout();
            ApplyFullBleedOverlayTheme();
            if (_isHostVisualActivityEnabled)
            {
                QueueCompactMarquee();
                RestartCompactVisualTimers();
            }
        };
        ActualThemeChanged += (_, _) =>
        {
            ApplyFullBleedOverlayTheme();
            UpdateCustomBackgroundVisual();
        };
        // The clip must track the HOST's size, not the shell's: the panorama
        // host flips Collapsed→Visible without the shell itself resizing, and
        // a zero RenderSize at that moment would clip the image away forever.
        // Wired lazily in EnsureCustomBackgroundPanoramaHost when x:Load realizes it.
        ActualThemeChanged += (_, _) =>
            UpdateCompactGroupPositionRail(_groupPresentation);
        Unloaded += (_, _) =>
        {
            StopTitleBarClickCollapse();
            StopCompactMarquee();
            ReleaseCompactMarqueeDelayTimer();
            StopCompactVisualTimers();
            StopCompactVinylRotation();
            StopTransientCompactStoryboards();
            StopDeferredExpandedContentRevealAnimation();
            _compactTextViewportUpdateQueued = false;
            StopGroupDropPreviewBreathing();
            EndShellDragSession(notifyCompact: true);
            ClearPendingMoreMenuPointerPosition();
        };
    }

    internal void ResumeVisualActivity()
    {
        if (_isHostVisualActivityEnabled)
        {
            return;
        }

        _isHostVisualActivityEnabled = true;
        if (!IsLoaded)
        {
            return;
        }

        QueueCompactMarquee();
        RestartCompactVisualTimers();
    }

    internal void SuspendVisualActivity()
    {
        if (!_isHostVisualActivityEnabled)
        {
            return;
        }

        _isHostVisualActivityEnabled = false;
        StopCompactMarquee();
        StopCompactVisualTimers();
        StopCompactVinylRotation();
        StopTransientCompactStoryboards();
    }

    internal void ApplyPerformanceSettings()
    {
        StopCompactMarquee();
        StopCompactVisualTimers();
        StopCompactVinylRotation();
        StopTransientCompactStoryboards();
        if (_hostedContent is IWidgetPerformanceAwareContent performanceAware)
        {
            performanceAware.ApplyPerformanceSettings();
        }

        if (!_isHostVisualActivityEnabled || !IsLoaded)
        {
            return;
        }

        QueueCompactMarquee();
        RestartCompactVisualTimers();
    }

    private void StopTransientCompactStoryboards()
    {
        _overlayHandleVisualStoryboard.StopAndClear();
        _compactLiveStoryboard.StopAndClear();
        _compactUpdateStoryboard.StopAndClear();
        _compactReorderHandleStoryboard.StopAndClear();
        _compactFullBleedVisibilityStoryboard.StopAndClear();
        _compactEdgeGlowFlashStoryboard.StopAndClear();
        _compactActionVisibilityStoryboard.StopAndClear();
        _compactTextHoverStoryboard.StopAndClear();
        _compactActionHighlightStoryboard.StopAndClear();
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

    public string TitleIconCustomEmoji
    {
        get => (string)GetValue(TitleIconCustomEmojiProperty);
        set => SetValue(TitleIconCustomEmojiProperty, value);
    }

    public ImageSource? TitleIconCustomSource
    {
        get => (ImageSource?)GetValue(TitleIconCustomSourceProperty);
        set => SetValue(TitleIconCustomSourceProperty, value);
    }

    /// <summary>Per-widget background image; null follows the global material.</summary>
    public ImageSource? CustomBackgroundSource
    {
        get => (ImageSource?)GetValue(CustomBackgroundSourceProperty);
        set => SetValue(CustomBackgroundSourceProperty, value);
    }

    /// <summary>Fill (cover) or Contain (fit) for the custom background.</summary>
    public string CustomBackgroundFit
    {
        get => (string)GetValue(CustomBackgroundFitProperty);
        set => SetValue(CustomBackgroundFitProperty, value);
    }

    /// <summary>Scrim strength over the background image, 0 (clear) to 1 (solid).</summary>
    public double CustomBackgroundDim
    {
        get => (double)GetValue(CustomBackgroundDimProperty);
        set => SetValue(CustomBackgroundDimProperty, value);
    }

    /// <summary>
    /// Shared panorama bitmap; takes precedence over
    /// <see cref="CustomBackgroundSource"/> when set. WinUI has no
    /// ImageBrush.Viewbox, so the shell positions an oversized image with
    /// <see cref="SetCustomPanoramaPlacement"/> and clips it to the plate.
    /// </summary>
    public ImageSource? CustomBackgroundPanoramaSource
    {
        get => (ImageSource?)GetValue(CustomBackgroundPanoramaSourceProperty);
        set => SetValue(CustomBackgroundPanoramaSourceProperty, value);
    }

    /// <summary>Per-widget solid background color hex; null follows the image chain.</summary>
    public string? CustomBackgroundColorHex
    {
        get => (string?)GetValue(CustomBackgroundColorHexProperty);
        set => SetValue(CustomBackgroundColorHexProperty, value);
    }

    private bool _customPanoramaSizeHooked;
    private bool _collapsedChromeInitialized;
    private string? _overlayDragHandleLabel;
    private string? _compactMoveAccessibleName;
    private string? _compactExpandAccessibleName;
    private string? _compactReorderAccessibleName;
    private string? _compactExpandButtonTooltip;

    public bool IsCompactChromeLoaded => CollapsedChromeLayer is not null;

    private Grid EnsureCollapsedChromeLayer()
    {
        if (CollapsedChromeLayer is null)
        {
            _ = FindName(nameof(CollapsedChromeLayer));
        }

        if (!_collapsedChromeInitialized && CollapsedChromeLayer is not null)
        {
            _collapsedChromeInitialized = true;
            CompactTitleIcon.SetCompactPresentationMode(true);
            SetProtectedCursor(CompactMoveHitRegion, InputSystemCursorShape.SizeAll);
            SetProtectedCursor(CompactReorderHandle, InputSystemCursorShape.SizeAll);
            CompactLiveTrack.SizeChanged += (_, _) => RestartCompactLiveIndeterminateForTrackSize();
            ApplyCompactHostAccessibilityIfLoaded();
            ApplyFullBleedOverlayTheme();
            ApplyCompactInnerCornerRadii();
            UpdateCompactGroupPositionRail(_groupPresentation);
            CompactReorderHandle.Visibility = _isCompactReorderEnabled
                ? Visibility.Visible
                : Visibility.Collapsed;
            CompactReorderHandle.IsHitTestVisible = _isCompactReorderEnabled && _isCollapsed;
            Bindings.Update();
            if (_compactPresentation is { } pendingPresentation)
            {
                _compactPresentation = null;
                ApplyCompactPresentationCore(pendingPresentation, previous: null);
            }
            else
            {
                UpdateCompactActionRegionWidth();
            }

            UpdateCompactHint();
            ApplyCompactActionVisibility(animate: false);
            UpdateCompactReorderHandleVisual(animate: false);
        }

        return CollapsedChromeLayer!;
    }

    private TextBlock EnsureCompactMarqueeClone(bool isTitle)
    {
        _ = EnsureCollapsedChromeLayer();
        if (isTitle)
        {
            if (CompactTitleMarqueeClone is null)
            {
                _ = FindName(nameof(CompactTitleMarqueeClone));
            }

            return CompactTitleMarqueeClone!;
        }

        if (CompactSummaryMarqueeClone is null)
        {
            _ = FindName(nameof(CompactSummaryMarqueeClone));
        }

        return CompactSummaryMarqueeClone!;
    }

    public void SetCompactHostAccessibility(
        string moveAccessibleName,
        string expandAccessibleName,
        string reorderAccessibleName,
        string expandButtonTooltip)
    {
        _compactMoveAccessibleName = moveAccessibleName;
        _compactExpandAccessibleName = expandAccessibleName;
        _compactReorderAccessibleName = reorderAccessibleName;
        _compactExpandButtonTooltip = expandButtonTooltip;
        ApplyCompactHostAccessibilityIfLoaded();
    }

    private void ApplyCompactHostAccessibilityIfLoaded()
    {
        if (CollapsedChromeLayer is null)
        {
            return;
        }

        if (_compactMoveAccessibleName is not null)
        {
            AutomationProperties.SetName(CompactMoveHitRegion, _compactMoveAccessibleName);
        }

        if (_compactExpandAccessibleName is not null)
        {
            AutomationProperties.SetName(CompactTextContainer, _compactExpandAccessibleName);
        }

        if (_compactReorderAccessibleName is not null)
        {
            ToolTipService.SetToolTip(CompactReorderHandle, _compactReorderAccessibleName);
            AutomationProperties.SetName(CompactReorderHandle, _compactReorderAccessibleName);
        }

        if (_compactExpandButtonTooltip is not null)
        {
            ToolTipService.SetToolTip(CompactExpandButton, _compactExpandButtonTooltip);
        }
    }

    private void EnsureCustomBackgroundPanoramaHost()
    {
        if (CustomBackgroundPanoramaHost is null)
        {
            _ = FindName(nameof(CustomBackgroundPanoramaHost));
        }

        if (!_customPanoramaSizeHooked && CustomBackgroundPanoramaHost is not null)
        {
            _customPanoramaSizeHooked = true;
            CustomBackgroundPanoramaHost.SizeChanged += (_, _) => UpdateCustomPanoramaClip();
        }
    }

    private void EnsureCompactVinylHost()
    {
        _ = EnsureCollapsedChromeLayer();
        if (CompactVinylHost is null)
        {
            _ = FindName(nameof(CompactVinylHost));
        }
    }

    private void EnsureCompactMediaControls()
    {
        _ = EnsureCollapsedChromeLayer();
        if (CompactPreviousButton is not null &&
            CompactPlayPauseButton is not null &&
            CompactNextButton is not null)
        {
            return;
        }

        _ = FindName(nameof(CompactPreviousButton));
        _ = FindName(nameof(CompactPlayPauseButton));
        _ = FindName(nameof(CompactNextButton));
        var cornerRadius = new CornerRadius(_compactInnerCornerRadius);
        if (CompactPreviousButton is not null)
        {
            CompactPreviousButton.CornerRadius = cornerRadius;
        }
        if (CompactPlayPauseButton is not null)
        {
            CompactPlayPauseButton.CornerRadius = cornerRadius;
        }
        if (CompactNextButton is not null)
        {
            CompactNextButton.CornerRadius = cornerRadius;
        }
    }

    private void EnsureOverlayChromeLayer()
    {
        if (OverlayChromeLayer is not null)
        {
            return;
        }

        _ = FindName(nameof(OverlayChromeLayer));
        ApplyOverlayDragHandleLabelIfLoaded();
    }

    private FrameworkElement EnsureOverlayDragHandle()
    {
        EnsureOverlayChromeLayer();
        return OverlayDragHandle!;
    }

    private void EnsureGroupDropPreview()
    {
        if (GroupDropPreview is null)
        {
            _ = FindName(nameof(GroupDropPreview));
        }
    }

    public void SetOverlayDragHandleLabel(string? label)
    {
        _overlayDragHandleLabel = label;
        ApplyOverlayDragHandleLabelIfLoaded();
    }

    private void ApplyOverlayDragHandleLabelIfLoaded()
    {
        if (OverlayDragHandle is null || string.IsNullOrEmpty(_overlayDragHandleLabel))
        {
            return;
        }

        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(
            OverlayDragHandle,
            _overlayDragHandleLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            OverlayDragHandle,
            _overlayDragHandleLabel);
    }

    /// <summary>
    /// Places the panorama image so the slice under the widget's desktop
    /// position lands inside the plate; values are DIPs relative to the
    /// plate's top-left corner.
    /// </summary>
    public void SetCustomPanoramaPlacement(
        double width,
        double height,
        double left,
        double top)
    {
        EnsureCustomBackgroundPanoramaHost();
        if (CustomBackgroundPanoramaImage is null)
        {
            return;
        }

        CustomBackgroundPanoramaImage.Width = width;
        CustomBackgroundPanoramaImage.Height = height;
        CustomBackgroundPanoramaImage.Margin = new Thickness(left, top, 0, 0);
    }

    public bool IsCustomBackgroundActive =>
        CustomBackgroundSource is not null ||
        CustomBackgroundColorHex is not null;

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
    public double ActualTitleBarHeight => Math.Max(0, TitleBarGrid.ActualHeight);
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
    public Button? CompactExpandActionButton => CompactExpandButton;
    public FrameworkElement? OverlayDragHandleElement => OverlayDragHandle;

    public FrameworkElement? CompactMoveHandleElement => CompactMoveHitRegion;
    public FrameworkElement? CompactBodyElement => CompactTextContainer;
    public FrameworkElement? CompactReorderHandleElement => CompactReorderHandle;

    public void SetCompactHintTexts(string moveText, string expandText)
    {
        _compactMoveHintText = moveText ?? string.Empty;
        _compactExpandHintText = expandText ?? string.Empty;
        UpdateCompactHint();
    }

    public void HideCompactHint()
    {
        _isCompactMoveHintLatched = false;
        if (CollapsedChromeLayer is not null)
        {
            CompactHintPresenter.Visibility = Visibility.Collapsed;
        }
    }

    private void UpdateCompactHint()
    {
        if (CollapsedChromeLayer is null)
        {
            return;
        }

        string? text = _isPointerOverCompactExpansionZone
                ? _compactExpandHintText
                : _isCompactMoveHintLatched || _isPointerOverCompactIdentity
                    ? _compactMoveHintText
                : null;
        if (string.IsNullOrWhiteSpace(text))
        {
            CompactHintPresenter.Visibility = Visibility.Collapsed;
            return;
        }

        CompactHintText.Text = text;
        CompactHintPresenter.Visibility = Visibility.Visible;
    }
    public Button MoreActionButton => MoreButton;
    public Button CloseActionButton => CloseButton;
    public FrameworkElement PositionLockActionIcon => PositionLockButtonIcon;
    public FrameworkElement PositionLockFilledActionIcon => PositionLockButtonFilledIcon;
    public FrameworkElement SizeLockActionIcon => SizeLockButtonIcon;
    public FrameworkElement SizeLockFilledActionIcon => SizeLockButtonFilledIcon;
    public FrameworkElement AddActionIcon => AddButtonIcon;
    public FrameworkElement MoreActionIcon => MoreButtonIcon;
    public FrameworkElement CloseActionIcon => CloseButtonIcon;
    public FrameworkElement DragHandleElement => _isCollapsed
        ? EnsureCollapsedChromeLayer()
        : (OverlayDragHandle ?? EnsureOverlayDragHandle());
    public FrameworkElement? GroupNavigationElement => GroupTitleSwitcher;

    /// <summary>
    /// Clears hover state that WinUI can leave behind when the native host is
    /// hidden without delivering the matching routed PointerExited events.
    /// </summary>
    public void ResetTransientCompactPointerState()
    {
        if (!_isPointerOverShell &&
            !_isPointerOverDragHandle &&
            !_isCompactKeyboardFocused &&
            !_isPointerOverCompactIdentity &&
            !_isPointerOverCompactExpansionZone &&
            !IsPointerOverCompactActionRegion() &&
            (CompactActionRegionHighlight is null || CompactActionRegionHighlight.Opacity <= 0.001) &&
            (CompactTextHoverBackground is null || CompactTextHoverBackground.Opacity <= 0.001))
        {
            return;
        }

        bool shellWasActive = _isPointerOverShell;
        if (_isPointerOverDragHandle)
        {
            App.LogVerbose("[DragCue] Cleared by transient-state reset (host visibility).");
        }
        _isPointerOverShell = false;
        ForceClearDragCue();
        _isCompactKeyboardFocused = false;
        ResetCompactInteractionRegions();

        // Detach any active hover clocks before assigning the resting values.
        // Direct property writes avoid creating a zero-duration storyboard for
        // every native hide/show cycle.
        _compactActionHighlightStoryboard.StopAndClear();
        _compactTextHoverStoryboard.StopAndClear();
        if (CollapsedChromeLayer is not null)
        {
            CompactActionRegionHighlight.Opacity = 0;
            CompactTextHoverBackground.Opacity = 0;
            CompactReorderGlyph.Opacity = 0.58;
            ApplyCompactActionVisibility(animate: false);
            UpdateCompactReorderHandleVisual(animate: false);
        }
        UpdateOverlayDragHandleVisual(animate: false);

        if (shellWasActive)
        {
            CompactPointerExited?.Invoke(this, EventArgs.Empty);
        }
    }

    public FrameworkElement? GroupMergeTitleTargetElement
    {
        get
        {
            if (ChromeMode == WidgetChromeMode.Hidden)
            {
                return null;
            }

            if (_isCollapsed)
            {
                return CompactMoveHitRegion is
                    {
                        Visibility: Visibility.Visible,
                        ActualWidth: > 0,
                        ActualHeight: > 0
                    }
                    ? CompactMoveHitRegion
                    : null;
            }

            if (ChromeMode == WidgetChromeMode.Overlay)
            {
                return OverlayDragHandle is
                    {
                        Visibility: Visibility.Visible,
                        Opacity: > 0.01,
                        ActualWidth: > 0,
                        ActualHeight: > 0
                    }
                    ? OverlayDragHandle
                    : null;
            }

            return !_isCompactRenderWarmupActive &&
                   TitleBarGrid.Visibility == Visibility.Visible &&
                   TitleBarGrid.ActualWidth > 0 &&
                   TitleBarGrid.ActualHeight > 0
                ? TitleBarGrid
                : null;
        }
    }

    public bool IsOverlayChromeMode => ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden;

    public bool IsCollapsed => _isCollapsed;

    public bool IsCompactMoveHandlePress => _isCompactMoveHandlePress;

    internal bool HasActiveVisualWork =>
        _isCompactRenderWarmupActive ||
        _isCompactTransitionActive ||
        _isContentSnapshotTransitionActive ||
        _isResponsiveLayoutTransitionActive ||
        OutgoingContentPresenter.Content is not null ||
        _groupDropBreathingAnimation is not null;

    // Looping ambient decoration (compact marquee, vinyl rotation) is
    // steady-state, not transient work — it is excluded from
    // HasActiveVisualWork on purpose, and the quiescence trim only
    // lengthens its required quiet period in response.
    internal bool HasAmbientVisualWork =>
        _compactMarqueeAnimation is not null ||
        _isCompactVinylRotating;

    public bool HasWidgetGroup => _groupPresentation is not null;

    internal void SetGroupTitleForegroundColors(Color primary, Color secondary, Color disabled, bool highContrast)
    {
        _hasPendingGroupTitleForeground = true;
        _groupTitlePrimaryColor = primary;
        _groupTitleSecondaryColor = secondary;
        _groupTitleDisabledColor = disabled;
        _groupTitleHighContrast = highContrast;
        GroupTitleSwitcher?.SetTabForegroundColors(primary, secondary, disabled, highContrast);
    }

    public void SetGroupPresentation(
        WidgetGroupPresentation? presentation,
        bool animateIdentity = false,
        WidgetGroupSwitchOrigin origin = WidgetGroupSwitchOrigin.Programmatic,
        bool forward = true)
    {
        _groupPresentation = presentation;
        if (presentation is not null || GroupTitleSwitcher is not null)
        {
            EnsureGroupTitleSwitcher();
            GroupTitleSwitcher.NavigationStyle =
                presentation?.NavigationStyle ??
                WidgetGroupNavigationStyles.Stack;
            GroupTitleSwitcher.DisplayMode = presentation?.TitleDisplayMode ??
                WidgetGroupTitleDisplayModes.IconAndText;
            GroupTitleSwitcher.WheelSwitchEnabled =
                presentation?.WheelSwitchEnabled ?? true;
            GroupTitleSwitcher.HoverSwitchEnabled =
                presentation?.HoverSwitchEnabled ?? false;
            GroupTitleSwitcher.SetPresentation(
                presentation,
                animateIdentity,
                origin,
                forward);
        }

        UpdateCompactGroupPositionRail(presentation);
        ApplyCompactAdaptiveLayout();
        UpdateTitleBarContentVisibility();
        ApplyChromeMode();
    }

    private void UpdateCompactGroupPositionRail(
        WidgetGroupPresentation? presentation)
    {
        if (CollapsedChromeLayer is null)
        {
            return;
        }

        CompactGroupPositionRail.Children.Clear();
        bool showRail = presentation is not null &&
            presentation.Members.Count > 1;
        CompactGroupPositionRail.Visibility = showRail
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactGroupPositionRailColumn.Width = new GridLength(
            showRail ? 10 : 0);
        if (!showRail)
        {
            return;
        }

        int activeIndex = presentation!.Members
            .Select((member, index) => (member, index))
            .Where(item => string.Equals(
                item.member.WidgetId,
                presentation.ActiveMemberId,
                StringComparison.Ordinal))
            .Select(item => item.index)
            .DefaultIfEmpty(0)
            .First();
        IReadOnlyList<WidgetGroupPositionRailSlot> slots =
            WidgetGroupNavigationInteractionPolicy.ResolvePositionRailSlots(
                activeIndex,
                presentation.Members.Count);
        // The compact pager dots are a selected-position indicator, so they
        // draw the neutral interaction tone; the theme-flip subscription
        // re-resolves it because the color is copied at paint time.
        var railBrush = SharedBrushCache.GetOrCreate(
            NeutralInteractionBrush.Line(this));
        foreach (WidgetGroupPositionRailSlot slot in slots)
        {
            bool active = slot.IsActive;
            CompactGroupPositionRail.Children.Add(new Border
            {
                Width = 3,
                Height = active ? 7 : 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = railBrush,
                CornerRadius = new CornerRadius(1.5),
                IsHitTestVisible = false,
                Opacity = active ? 0.94 : 0.3
            });
        }
    }

    internal bool TryHandleGroupKeyboardNavigation(KeyRoutedEventArgs e)
    {
        return GroupTitleSwitcher?.TryHandleKeyboardNavigation(e) == true;
    }

    public void OpenGroupPicker(FrameworkElement? anchor = null)
    {
        if (_groupPresentation is null)
        {
            return;
        }

        WidgetGroupTitleSwitcher switcher = EnsureGroupTitleSwitcher();
        switcher.OpenPicker(anchor ?? switcher);
    }

    public void SetGroupMemberLoading(string? widgetId, bool isLoading)
    {
        GroupTitleSwitcher?.SetMemberLoading(widgetId, isLoading);
    }

    internal void ObserveGroupTabDragHover(TabViewItem? tab)
    {
        GroupTitleSwitcher?.ObserveNativeDragHoverTab(tab);
    }

    internal void CancelGroupTabDragHover()
    {
        GroupTitleSwitcher?.CancelDragHoverSwitch();
    }

    internal void SetGroupTabFileDragActive(bool active)
    {
        GroupTitleSwitcher?.SetFileDragStripFreeze(active);
    }

    public void SetGroupDropPreview(
        bool visible,
        bool ready,
        string? messageKey = null)
    {
        StopGroupDropPreviewBreathing();
        if (!visible)
        {
            if (GroupDropPreview is not null)
            {
                GroupDropPreview.Visibility = Visibility.Collapsed;
                GroupDropPreview.Opacity = 0;
                GroupDropPreviewTransform.Y = 0;
            }

            return;
        }

        EnsureGroupDropPreview();
        if (GroupDropPreview is null || !TryPlaceGroupDropPreviewOverTitle())
        {
            if (GroupDropPreview is not null)
            {
                GroupDropPreview.Visibility = Visibility.Collapsed;
                GroupDropPreview.Opacity = 0;
                GroupDropPreviewTransform.Y = 0;
            }

            return;
        }

        bool blocked =
            !ready &&
            !string.IsNullOrWhiteSpace(messageKey);
        GroupDropPreviewIcon.Glyph = blocked ? "\uE7BA" : "\uE8F1";
        ApplyGroupDropPreviewAppearance(ready, blocked);
        GroupDropPreview.Visibility = Visibility.Visible;
        GroupDropPreview.Opacity = 1;
        GroupDropPreviewTransform.Y = 0;
        StartGroupDropPreviewBreathing(ready);
    }

    private bool TryPlaceGroupDropPreviewOverTitle()
    {
        FrameworkElement? target = GroupMergeTitleTargetElement;
        if (target is null)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Point topLeft = target.TransformToVisual(ShellRoot)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            GroupDropPreview.Margin = new Thickness(topLeft.X, topLeft.Y, 0, 0);
            GroupDropPreview.Width = target.ActualWidth;
            GroupDropPreview.Height = target.ActualHeight;
            return GroupDropPreview.Width > 0 && GroupDropPreview.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyGroupDropPreviewAppearance(bool ready, bool blocked)
    {
        // Where a dragged widget group would land is a drag state, so the
        // preview outline and its icon draw the neutral interaction tone; ready
        // and blocked still read differently through opacity.
        Color neutral = NeutralInteractionBrush.Line(this);
        byte toneAlpha = ready ? (byte)0xF0 : blocked ? (byte)0xA8 : (byte)0xD0;
        GroupDropPreview.Background = SharedBrushCache.GetOrCreate(Colors.Transparent);
        GroupDropPreview.BorderBrush = SharedBrushCache.GetOrCreate(Color.FromArgb(
            (byte)(neutral.A * toneAlpha / 255),
            neutral.R,
            neutral.G,
            neutral.B));
        GroupDropPreviewIcon.Foreground = SharedBrushCache.GetOrCreate(Color.FromArgb(
            0xFF,
            neutral.R,
            neutral.G,
            neutral.B));

        if (WindowsCompatibilityService.IsHighContrast)
        {
            GroupDropPreview.Background = SharedBrushCache.GetOrCreate(Colors.Transparent);
        }
    }

    private void StartGroupDropPreviewBreathing(bool ready)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(
            GroupDropPreview);
        visual.StopAnimation("Opacity");
        if (!SystemAnimationsEnabled())
        {
            visual.Opacity = WindowsCompatibilityService.IsHighContrast
                ? 1
                : ready
                    ? 1
                    : 0.72f;
            return;
        }

        ScalarKeyFrameAnimation animation =
            _compositionResources.GetScalar(visual.Compositor, WidgetAnimationTemplate.GroupDropBreathing);
        animation.Duration = TimeSpan.FromMilliseconds(1500);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        CubicBezierEasingFunction easing =
            _compositionResources.GetBreathingEasing(visual.Compositor);
        animation.InsertKeyFrame(0, ready ? 0.42f : 0.3f);
        animation.InsertKeyFrame(0.5f, ready ? 1 : 0.78f, easing);
        animation.InsertKeyFrame(1, ready ? 0.42f : 0.3f, easing);
        _groupDropBreathingAnimation = animation;
        visual.StartAnimation("Opacity", animation);
    }

    private void StopGroupDropPreviewBreathing()
    {
        if (GroupDropPreview is null)
        {
            _groupDropBreathingAnimation = null;
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(
            GroupDropPreview);
        visual.StopAnimation("Opacity");
        visual.Opacity = 0;
        _groupDropBreathingAnimation = null;
    }

    public void SetCompactReorderEnabled(bool enabled)
    {
        _isCompactReorderEnabled = enabled;
        if (CollapsedChromeLayer is null)
        {
            return;
        }

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
        AbortCompactExpansionRenderWarmup();
        StopDeferredExpandedContentRevealAnimation();
        bool hadPreviousHostedContent = _hostedContent is not null;
        DetachHostedContentEvents();
        _hostedContent = content;
        AttachHostedContentEvents();
        _responsiveLayoutContent = content as IWidgetResponsiveLayoutContent;
        bool deferCollapsedViewAttachment =
            ((_isCollapsed && !_isCompactTransitionActive) ||
             _isCompactDeferredContentPresentationActive) &&
            !_isContentSnapshotTransitionActive &&
            OutgoingContentPresenter.Content is null &&
            content is DeskBox.Controls.WidgetContents.WidgetContentAdapterBase
            {
                HasMaterializedView: false
            };
        ShellContent = deferCollapsedViewAttachment ? null : content.View;
        if (!_isCompactDeferredContentPresentationActive)
        {
            content.OnCompactStateChanged(_isCollapsed);
            NotifyHostedContentViewportSize(
                ContentTransitionViewport.ActualWidth,
                ContentTransitionViewport.ActualHeight);
            DispatcherQueue.TryEnqueue(() => NotifyHostedContentViewportSize(
                ContentTransitionViewport.ActualWidth,
                ContentTransitionViewport.ActualHeight));
            if (!hadPreviousHostedContent &&
                !_isCollapsed &&
                !_isCompactTransitionActive &&
                !_isContentSnapshotTransitionActive &&
                OutgoingContentPresenter.Content is null &&
                ShellContent is not null &&
                IsLoaded &&
                SystemAnimationsEnabled())
            {
                StartDeferredExpandedContentRevealAnimation(
                    anchorsBottom: _transitionExpansionAnchor is
                        WidgetCompactExpansionAnchor.LeftBottom or
                        WidgetCompactExpansionAnchor.RightBottom);
            }
        }

        HostedContentChanged?.Invoke(this, EventArgs.Empty);
    }

    internal bool HasPresentableContentFrame =>
        !_isCompactRenderWarmupActive &&
        ((ShellContent is not null &&
         ShellContentPresenter.Visibility == Visibility.Visible &&
         ShellContentPresenter.Opacity > 0) ||
        (OutgoingContentPresenter.Content is not null &&
         OutgoingContentPresenter.Visibility == Visibility.Visible &&
         OutgoingContentPresenter.Opacity > 0));

    /// <summary>
    /// Keeps a bitmap of the currently rendered body above the live presenter
    /// while a persistent legacy host rebinds to another member context.
    /// </summary>
    public async Task<bool> BeginContentSnapshotTransitionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_isContentSnapshotTransitionActive ||
            OutgoingContentPresenter.Content is not null ||
            !IsLoaded ||
            _isCollapsed ||
            ShellContentPresenter.Visibility != Visibility.Visible ||
            ShellContentPresenter.ActualWidth < 1 ||
            ShellContentPresenter.ActualHeight < 1)
        {
            return false;
        }

        var snapshot = new RenderTargetBitmap();
        try
        {
            await snapshot.RenderAsync(ShellContentPresenter);
        }
        catch (Exception ex)
        {
            App.LogVerbose(
                $"[WidgetGroup] Content snapshot capture unavailable: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return false;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot.PixelWidth < 1 || snapshot.PixelHeight < 1)
        {
            return false;
        }

        OutgoingContentPresenter.Content = new Image
        {
            Source = snapshot,
            Stretch = Stretch.Fill,
            IsHitTestVisible = false
        };
        Grid.SetRow(OutgoingContentPresenter, Grid.GetRow(ShellContentPresenter));
        Grid.SetRowSpan(
            OutgoingContentPresenter,
            Grid.GetRowSpan(ShellContentPresenter));
        Canvas.SetZIndex(
            OutgoingContentPresenter,
            Canvas.GetZIndex(ShellContentPresenter) + 1);
        OutgoingContentPresenter.Margin = ShellContentPresenter.Margin;
        OutgoingContentPresenter.Opacity = 1;
        OutgoingContentPresenter.Visibility = ShellContentPresenter.Visibility;
        _isContentSnapshotTransitionActive = true;
        return true;
    }

    public void CompleteContentSnapshotTransition()
    {
        if (!_isContentSnapshotTransitionActive)
        {
            return;
        }

        _isContentSnapshotTransitionActive = false;
        OutgoingContentPresenter.Content = null;
        OutgoingContentPresenter.Visibility = Visibility.Collapsed;
    }

    public void BeginContentTransition(
        IWidgetContent outgoingContent,
        IWidgetContent incomingContent)
    {
        ArgumentNullException.ThrowIfNull(outgoingContent);
        ArgumentNullException.ThrowIfNull(incomingContent);

        if (_isContentSnapshotTransitionActive)
        {
            throw new InvalidOperationException(
                "A snapshot transition must finish before starting a live content transition.");
        }

        StopDeferredExpandedContentRevealAnimation();
        SuspendFileSurfaceItemTransitions(outgoingContent, incomingContent);
        EnsureContentTransitionViewportClip();
        EndInteractiveNeighborPreview();
        ShellContent = null;
        if (_isCollapsed)
        {
            OutgoingContentPresenter.Content = null;
        }
        else
        {
            OutgoingContentPresenter.Content = outgoingContent.View;
        }
        Grid.SetRow(OutgoingContentPresenter, Grid.GetRow(ShellContentPresenter));
        Grid.SetRowSpan(
            OutgoingContentPresenter,
            Grid.GetRowSpan(ShellContentPresenter));
        Canvas.SetZIndex(
            OutgoingContentPresenter,
            Canvas.GetZIndex(ShellContentPresenter) + 1);
        OutgoingContentPresenter.Margin = ShellContentPresenter.Margin;
        OutgoingContentPresenter.Opacity = 1;
        OutgoingContentPresenter.Visibility = ShellContentPresenter.Visibility;
        // Stage the initialized incoming member in the live presenter without
        // exposing it. The manager may wait for a presented frame and persist
        // the group transaction before animation begins; keeping opacity at
        // zero here guarantees that interval has exactly one visible member.
        ShellContentPresenter.Opacity = 0;
        ShellContentPresenter.IsHitTestVisible = false;
        SetContent(incomingContent);
    }

    public void CompleteContentTransition()
    {
        try
        {
            ResetContentTransitionVisuals();
            _isContentSnapshotTransitionActive = false;
            OutgoingContentPresenter.Content = null;
            OutgoingContentPresenter.Visibility = Visibility.Collapsed;
        }
        finally
        {
            ResumeFileSurfaceItemTransitions();
        }
    }

    private void SuspendFileSurfaceItemTransitions(
        IWidgetContent outgoingContent,
        IWidgetContent incomingContent)
    {
        ResumeFileSurfaceItemTransitions();
        _transitionOutgoingFileSurface = outgoingContent as FileWidgetContentAdapter;
        _transitionIncomingFileSurface = incomingContent as FileWidgetContentAdapter;

        _transitionOutgoingFileSurface?
            .SuspendItemContainerTransitionsForHostSwitch();
        if (!ReferenceEquals(
                _transitionIncomingFileSurface,
                _transitionOutgoingFileSurface))
        {
            _transitionIncomingFileSurface?
                .SuspendItemContainerTransitionsForHostSwitch();
        }
    }

    private void ResumeFileSurfaceItemTransitions()
    {
        FileWidgetContentAdapter? outgoing = _transitionOutgoingFileSurface;
        FileWidgetContentAdapter? incoming = _transitionIncomingFileSurface;
        _transitionOutgoingFileSurface = null;
        _transitionIncomingFileSurface = null;

        outgoing?.ResumeItemContainerTransitionsAfterHostSwitch();
        if (!ReferenceEquals(incoming, outgoing))
        {
            incoming?.ResumeItemContainerTransitionsAfterHostSwitch();
        }
    }

    public async Task<RenderTargetBitmap?> CaptureOutgoingContentSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (OutgoingContentPresenter.Content is null ||
            !IsLoaded ||
            OutgoingContentPresenter.ActualWidth < 1 ||
            OutgoingContentPresenter.ActualHeight < 1)
        {
            return null;
        }

        var snapshot = new RenderTargetBitmap();
        try
        {
            await snapshot.RenderAsync(OutgoingContentPresenter);
        }
        catch (Exception ex)
        {
            App.LogVerbose(
                $"[WidgetSurface] Snapshot capture unavailable: " +
                $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return snapshot.PixelWidth > 0 && snapshot.PixelHeight > 0
            ? snapshot
            : null;
    }

    private bool _isInteractiveNeighborPreviewActive;
    private RenderTargetBitmap? _interactiveNeighborSnapshot;

    public void UpdateInteractiveNeighborPreview(
        RenderTargetBitmap snapshot,
        double translation,
        double progress)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        StopDeferredExpandedContentRevealAnimation();
        EnsureContentTransitionViewportClip();
        if (_isInteractiveNeighborPreviewActive &&
            !ReferenceEquals(_interactiveNeighborSnapshot, snapshot))
        {
            EndInteractiveNeighborPreview();
        }

        if (!_isInteractiveNeighborPreviewActive)
        {
            OutgoingContentPresenter.Content = new Image
            {
                Source = snapshot,
                Stretch = Stretch.Fill,
                IsHitTestVisible = false
            };
            Grid.SetRow(
                OutgoingContentPresenter,
                Grid.GetRow(ShellContentPresenter));
            Grid.SetRowSpan(
                OutgoingContentPresenter,
                Grid.GetRowSpan(ShellContentPresenter));
            OutgoingContentPresenter.Margin = ShellContentPresenter.Margin;
            Canvas.SetZIndex(
                OutgoingContentPresenter,
                Canvas.GetZIndex(ShellContentPresenter) - 1);
            OutgoingContentPresenter.Visibility =
                ShellContentPresenter.Visibility;
            _isInteractiveNeighborPreviewActive = true;
            _interactiveNeighborSnapshot = snapshot;
        }

        double extent = Math.Max(
            1,
            ShellContentPresenter.ActualHeight);
        var liveTransform = new TranslateTransform
        {
            Y = translation
        };
        var previewTransform = new TranslateTransform
        {
            Y = translation < 0
                ? extent + translation
                : -extent + translation
        };
        ShellContentPresenter.RenderTransform = liveTransform;
        OutgoingContentPresenter.RenderTransform = previewTransform;
        ShellContentPresenter.Opacity =
            1 - (Math.Clamp(progress, 0, 1) * 0.08);
        OutgoingContentPresenter.Opacity =
            0.84 + (Math.Clamp(progress, 0, 1) * 0.16);
    }

    public void EndInteractiveNeighborPreview()
    {
        if (!_isInteractiveNeighborPreviewActive)
        {
            return;
        }

        _isInteractiveNeighborPreviewActive = false;
        _interactiveNeighborSnapshot = null;
        ShellContentPresenter.RenderTransform = null;
        ShellContentPresenter.Opacity = 1;
        OutgoingContentPresenter.RenderTransform = null;
        OutgoingContentPresenter.Opacity = 1;
        OutgoingContentPresenter.Content = null;
        OutgoingContentPresenter.Visibility = Visibility.Collapsed;
    }

    public Task AnimateContentTransitionAsync(
        bool directional,
        bool forward,
        CancellationToken cancellationToken = default)
    {
        EnsureContentTransitionViewportClip();
        if (OutgoingContentPresenter.Content is null)
        {
            return Task.CompletedTask;
        }

        bool animationsEnabled = SystemAnimationsEnabled();

        WidgetGroupSwitchAnimationEffect effect =
            WidgetGroupSwitchAnimationPolicy.ResolveEffect(
                App.Current?.SettingsService?.Settings.WidgetLayout
                    .WidgetGroupSwitchAnimationStyle,
                _groupPresentation?.NavigationStyle);
        WidgetContentTransitionProfile profile =
            WidgetContentTransitionProfile.Create(
                animationsEnabled,
                directional,
                effect);
        if (profile.DurationMilliseconds <= 0)
        {
            OutgoingContentPresenter.Opacity = 0;
            ShellContentPresenter.Opacity = 1;
            ShellContentPresenter.IsHitTestVisible = true;
            return Task.CompletedTask;
        }

        double distance = profile.TranslationDistance;
        double sign = forward ? 1 : -1;
        bool horizontalMotion =
            effect == WidgetGroupSwitchAnimationEffect.Horizontal;
        string motionProperty = horizontalMotion
            ? nameof(CompositeTransform.TranslateX)
            : nameof(CompositeTransform.TranslateY);
        var incomingTransform = new CompositeTransform();
        if (profile.UsesMotion)
        {
            if (horizontalMotion)
            {
                incomingTransform.TranslateX = distance * sign;
            }
            else
            {
                incomingTransform.TranslateY = distance * sign;
            }
        }
        var outgoingTransform = new CompositeTransform();
        ShellContentPresenter.RenderTransform = incomingTransform;
        OutgoingContentPresenter.RenderTransform = outgoingTransform;

        ShellContentPresenter.Opacity = 0;
        OutgoingContentPresenter.Opacity = 1;
        int outgoingDurationMs = profile.OutgoingDurationMilliseconds;
        int incomingBeginTimeMs =
            outgoingDurationMs + profile.SwapGapMilliseconds;
        int incomingDurationMs = profile.IncomingDurationMilliseconds;
        var storyboard = new Storyboard();
        if (profile.UsesMotion)
        {
            AddTransitionAnimation(
                storyboard,
                outgoingTransform,
                motionProperty,
                -distance * sign,
                outgoingDurationMs,
                easingMode: EasingMode.EaseIn);
            AddTransitionAnimation(
                storyboard,
                incomingTransform,
                motionProperty,
                0,
                incomingDurationMs,
                incomingBeginTimeMs);
        }
        AddTransitionAnimation(
            storyboard,
            OutgoingContentPresenter,
            "Opacity",
            0,
            outgoingDurationMs,
            easingMode: EasingMode.EaseIn);
        AddTransitionAnimation(
            storyboard,
            ShellContentPresenter,
            "Opacity",
            1,
            incomingDurationMs,
            incomingBeginTimeMs);

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan completionFallbackDelay = TimeSpan.FromMilliseconds(
            Math.Max(250, profile.DurationMilliseconds + 250));
        EventHandler<object>? storyboardCompleted = null;
        bool settled = false;
        void Settle(bool cancelled)
        {
            if (settled)
            {
                return;
            }

            settled = true;
            if (storyboardCompleted is not null)
            {
                storyboard.Completed -= storyboardCompleted;
            }
            storyboard.Stop();
            storyboard.Children.Clear();
            SetContentTransitionVisuals(incomingVisible: !cancelled);
            if (cancelled)
            {
                completion.TrySetCanceled(cancellationToken);
            }
            else
            {
                completion.TrySetResult();
            }
        }

        async Task RunCompletionFallbackAsync()
        {
            await Task.Delay(completionFallbackDelay).ConfigureAwait(false);
            DispatcherQueue.TryEnqueue(() => Settle(cancelled: false));
        }

        storyboardCompleted = (_, _) => Settle(cancelled: false);
        storyboard.Completed += storyboardCompleted;
        CancellationTokenRegistration registration = cancellationToken.Register(
            () => DispatcherQueue.TryEnqueue(() => Settle(cancelled: true)));
        _ = completion.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        storyboard.Begin();
        _ = RunCompletionFallbackAsync();
        return completion.Task;
    }

    private static void AddTransitionAnimation(
        Storyboard storyboard,
        DependencyObject target,
        string property,
        double to,
        int durationMs,
        int beginTimeMs = 0,
        EasingMode easingMode = EasingMode.EaseOut)
    {
        var animation = new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            BeginTime = TimeSpan.FromMilliseconds(beginTimeMs),
            EasingFunction = new CubicEase { EasingMode = easingMode }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void ResetContentTransitionVisuals()
    {
        SetContentTransitionVisuals(incomingVisible: true);
    }

    private void SetContentTransitionVisuals(bool incomingVisible)
    {
        _isInteractiveNeighborPreviewActive = false;
        _interactiveNeighborSnapshot = null;
        ShellContentPresenter.Opacity = incomingVisible ? 1 : 0;
        ShellContentPresenter.IsHitTestVisible = incomingVisible;
        OutgoingContentPresenter.Opacity = incomingVisible ? 0 : 1;
        ShellContentPresenter.RenderTransform = null;
        OutgoingContentPresenter.RenderTransform = null;
    }

    private void ContentTransitionViewport_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        EnsureContentTransitionViewportClip();

        if (!_isResponsiveLayoutTransitionActive &&
            !_isCompactDeferredContentPresentationActive)
        {
            NotifyHostedContentViewportSize(e.NewSize.Width, e.NewSize.Height);
        }
    }

    private void EnsureContentTransitionViewportClip()
    {
        if (_isResponsiveLayoutTransitionActive && _isContentTransitionClipAttached)
        {
            return;
        }

        Visual contentTransitionVisual =
            ElementCompositionPreview.GetElementVisual(
                ContentTransitionViewport);
        if (!ReferenceEquals(
                contentTransitionVisual.Clip,
                _contentTransitionClip))
        {
            contentTransitionVisual.Clip = _contentTransitionClip;
        }

        _isContentTransitionClipAttached = true;
    }

    private void NotifyHostedContentViewportSize(double width, double height)
    {
        if (_hostedContent is not IWidgetHostViewportContent viewportContent ||
            !double.IsFinite(width) ||
            !double.IsFinite(height) ||
            width <= 0 ||
            height <= 0)
        {
            return;
        }

        viewportContent.OnHostViewportSizeChanged(width, height);
    }

    public void RollbackContentTransition(IWidgetContent outgoingContent)
    {
        ArgumentNullException.ThrowIfNull(outgoingContent);
        ShellContent = null;
        CompleteContentTransition();
        SetContent(outgoingContent);
    }

    internal bool IsHostedContentHibernated =>
        _hostedContent is not null && ShellContent is null;

    internal void TrimCollapsedTransientVisuals()
    {
        if (!_isCollapsed || _isCompactTransitionActive || _isContentSnapshotTransitionActive)
        {
            return;
        }

        if (_isInteractiveNeighborPreviewActive)
        {
            EndInteractiveNeighborPreview();
        }
        else
        {
            _interactiveNeighborSnapshot = null;
        }
    }

    internal bool HibernateHostedContentView()
    {
        if (!_isCollapsed ||
            _isCompactTransitionActive ||
            _isContentSnapshotTransitionActive ||
            OutgoingContentPresenter.Content is not null ||
            _hostedContent is not DeskBox.Controls.WidgetContents.WidgetContentAdapterBase adapter ||
            !adapter.HasMaterializedView)
        {
            return false;
        }

        AbortCompactExpansionRenderWarmup();
        EndShellDragSession(notifyCompact: true);
        ShellContent = null;
        return adapter.HibernateCollapsedView();
    }

    internal bool EnsureHostedContentViewAttached()
    {
        if (_hostedContent is null || ShellContent is not null)
        {
            return false;
        }

        ShellContent = _hostedContent.View;
        _hostedContent.OnCompactStateChanged(_isCollapsed);
        NotifyHostedContentViewportSize(
            ContentTransitionViewport.ActualWidth,
            ContentTransitionViewport.ActualHeight);
        DispatcherQueue.TryEnqueue(() => NotifyHostedContentViewportSize(
            ContentTransitionViewport.ActualWidth,
            ContentTransitionViewport.ActualHeight));
        return true;
    }

    public void ClearContent()
    {
        AbortCompactExpansionRenderWarmup();
        StopDeferredExpandedContentRevealAnimation();
        _isCompactDeferredContentPresentationActive = false;
        bool hadContent = _hostedContent is not null || ShellContent is not null;
        DetachHostedContentEvents();
        _hostedContent = null;
        _responsiveLayoutContent = null;
        ShellContent = null;
        EndShellDragSession(notifyCompact: true);
        CompleteContentTransition();
        if (hadContent)
        {
            HostedContentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void AttachHostedContentEvents()
    {
        if (_hostedContent is FileWidgetContentAdapter fileSurface)
        {
            fileSurface.ImportBusyChanged += HostedFileSurface_ImportBusyChanged;
            TitleBarGrid.IsHitTestVisible = !fileSurface.IsImportBusy;
        }
    }

    private void DetachHostedContentEvents()
    {
        if (_hostedContent is FileWidgetContentAdapter fileSurface)
        {
            fileSurface.ImportBusyChanged -= HostedFileSurface_ImportBusyChanged;
        }

        TitleBarGrid.IsHitTestVisible = true;
    }

    private void HostedFileSurface_ImportBusyChanged(bool isBusy)
    {
        TitleBarGrid.IsHitTestVisible = !isBusy;
    }

    public void BeginResponsiveLayoutTransition(
        bool isCollapsing,
        double targetWindowWidth,
        double targetWindowHeight,
        WidgetCompactExpansionAnchor expansionAnchor = WidgetCompactExpansionAnchor.LeftTop,
        double? frozenWindowWidth = null,
        double? frozenWindowHeight = null,
        bool hasHorizontalMorph = true,
        bool deferContentPresentation = false)
    {
        if (!isCollapsing)
        {
            _isCompactDeferredContentPresentationActive = deferContentPresentation;
        }

        double titleHeight = IsOverlayChromeMode
            ? 0
            : ResolveTitleBarLayoutHeight();
        _responsiveTargetContentWidth = Math.Max(0, targetWindowWidth);
        _responsiveTargetContentHeight = Math.Max(0, targetWindowHeight - titleHeight);
        _transitionExpansionAnchor = expansionAnchor;
        _transitionHasHorizontalMorph = hasHorizontalMorph;
        _transitionFrozenWindowWidth = Math.Max(0, frozenWindowWidth ?? targetWindowWidth);
        _transitionFrozenContentHeight = Math.Max(0, (frozenWindowHeight ?? targetWindowHeight) - titleHeight);
        _isResponsiveLayoutTransitionActive = true;
        FreezeTransitionContentLayout(
            _transitionFrozenWindowWidth,
            _transitionFrozenContentHeight,
            expansionAnchor,
            _transitionFrozenWindowWidth);
        if (!_isCompactDeferredContentPresentationActive)
        {
            _responsiveLayoutContent?.BeginResponsiveLayoutTransition(
                _responsiveTargetContentWidth,
                _responsiveTargetContentHeight,
                isCollapsing);
        }
    }

    public void CompleteResponsiveLayoutTransition()
    {
        if (!_isResponsiveLayoutTransitionActive)
        {
            return;
        }

        RestoreTransitionContentLayout();
        if (!_isCompactDeferredContentPresentationActive)
        {
            _responsiveLayoutContent?.CompleteResponsiveLayoutTransition(
                _responsiveTargetContentWidth,
                _responsiveTargetContentHeight);
            NotifyHostedContentViewportSize(
                _responsiveTargetContentWidth,
                _responsiveTargetContentHeight);
        }

        _isResponsiveLayoutTransitionActive = false;
    }

    public void CancelResponsiveLayoutTransition()
    {
        if (!_isResponsiveLayoutTransitionActive)
        {
            return;
        }

        RestoreTransitionContentLayout();
        if (!_isCompactDeferredContentPresentationActive)
        {
            _responsiveLayoutContent?.CancelResponsiveLayoutTransition();
        }

        _isResponsiveLayoutTransitionActive = false;
    }

    private void FreezeTransitionContentLayout(
        double contentWidth,
        double contentHeight,
        WidgetCompactExpansionAnchor expansionAnchor,
        double stableWindowWidth = double.NaN)
    {
        if (!_isCompactDeferredContentPresentationActive &&
            contentWidth > 0 &&
            contentHeight > 0)
        {
            bool anchorsRight = expansionAnchor is
                WidgetCompactExpansionAnchor.RightTop or
                WidgetCompactExpansionAnchor.RightBottom;
            bool anchorsBottom = expansionAnchor is
                WidgetCompactExpansionAnchor.LeftBottom or
                WidgetCompactExpansionAnchor.RightBottom;
            ShellContentPresenter.Width = contentWidth;
            ShellContentPresenter.Height = contentHeight;
            ShellContentPresenter.HorizontalAlignment = anchorsRight
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;
            ShellContentPresenter.VerticalAlignment = anchorsBottom
                ? VerticalAlignment.Bottom
                : VerticalAlignment.Top;
            _isTransitionContentLayoutFrozen = true;
        }

        // The title bar must not re-wrap at every intermediate window width
        // during the bounds animation: each re-layout lands on the UI thread
        // alongside the frame-paced window move and turns into visible
        // stutter on the title (group tab strips above all). Freezing it at
        // the transition's stable width follows the same convention the
        // content presenter already uses — the moving HWND edge reveals or
        // clips it, no reflow, no new animation semantics.
        WidgetTitleBarTransitionFreeze titleFreeze =
            WidgetTitleBarTransitionFreezePolicy.Evaluate(
                IsOverlayChromeMode,
                TitleBarGrid.Visibility == Visibility.Visible,
                stableWindowWidth,
                expansionAnchor);
        if (titleFreeze.ShouldFreeze)
        {
            TitleBarGrid.Width = titleFreeze.Width;
            TitleBarGrid.HorizontalAlignment = titleFreeze.AlignRight
                ? HorizontalAlignment.Right
                : HorizontalAlignment.Left;
            _isTransitionTitleBarLayoutFrozen = true;
        }
    }

    private void RestoreTransitionContentLayout()
    {
        if (_isTransitionContentLayoutFrozen)
        {
            ShellContentPresenter.Width = double.NaN;
            ShellContentPresenter.Height = double.NaN;
            ShellContentPresenter.HorizontalAlignment = HorizontalAlignment.Stretch;
            ShellContentPresenter.VerticalAlignment = VerticalAlignment.Stretch;
            _isTransitionContentLayoutFrozen = false;
        }

        if (_isTransitionTitleBarLayoutFrozen)
        {
            TitleBarGrid.Width = double.NaN;
            TitleBarGrid.HorizontalAlignment = HorizontalAlignment.Stretch;
            _isTransitionTitleBarLayoutFrozen = false;
        }
    }

    public void SetCollapsed(bool collapsed, string contentMode)
    {
        if (collapsed)
        {
            _isCompactDeferredContentPresentationActive = false;
            _ = EnsureCollapsedChromeLayer();
        }
        AbortCompactExpansionRenderWarmup();
        StopDeferredExpandedContentRevealAnimation();
        ResetCompactTransitionVisuals();
        StopTransientCompactStoryboards();
        bool stateChanged = _isCollapsed != collapsed;
        _isCollapsed = collapsed;
        if (FeedbackPresenter is { } feedbackPresenter)
        {
            feedbackPresenter.IsCompact = collapsed;
        }
        if (collapsed)
        {
            UpdateCompactHint();
        }
        else
        {
            HideCompactHint();
        }
        if (collapsed || !_isCompactDeferredContentPresentationActive)
        {
            _hostedContent?.OnCompactStateChanged(collapsed);
        }
        if (stateChanged)
        {
            ResetCompactInteractionRegions();
        }
        UpdateCompactInteractionRegionHighlights();
        if (collapsed)
        {
            if (_isPointerOverDragHandle)
            {
                App.LogVerbose("[DragCue] Cleared by collapse transition.");
            }
            ForceClearDragCue();
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
            RestartCompactVisualTimers();
        }
        else
        {
            StopCompactMarquee();
            StopCompactVisualTimers();
            StopCompactVinylRotation();
        }
    }

    /// <summary>
    /// Forwards the host window's bounds-transition activity to the hosted
    /// content so incremental visual work can hold during the animation.
    /// </summary>
    internal void NotifyCompactBoundsTransitionActive(bool isActive)
    {
        _hostedContent?.OnCompactBoundsTransitionActiveChanged(isActive);
    }

    public bool BeginCompactExpansionRenderWarmup(
        double targetWindowWidth,
        double targetWindowHeight,
        WidgetCompactExpansionAnchor expansionAnchor = WidgetCompactExpansionAnchor.LeftTop)
    {
        if (!_isCollapsed ||
            _isCompactTransitionActive ||
            _isCompactRenderWarmupActive ||
            ShellContent is null ||
            !double.IsFinite(targetWindowWidth) ||
            !double.IsFinite(targetWindowHeight) ||
            targetWindowWidth <= 0 ||
            targetWindowHeight <= 0)
        {
            return false;
        }

        _ = EnsureCollapsedChromeLayer();
        _isCompactRenderWarmupActive = true;
        try
        {
            SetExpandedChromeCompositionHidden(true);
            ApplyChromeMode();
            BeginResponsiveLayoutTransition(
                isCollapsing: false,
                targetWindowWidth,
                targetWindowHeight,
                expansionAnchor,
                frozenWindowWidth: targetWindowWidth,
                frozenWindowHeight: targetWindowHeight);
            UpdateLayout();
            PrewarmCompactTransitionCompositionResources();
            return true;
        }
        catch
        {
            AbortCompactExpansionRenderWarmup();
            throw;
        }
    }

    public bool EndCompactExpansionRenderWarmup()
    {
        if (!_isCompactRenderWarmupActive)
        {
            return false;
        }

        _isCompactRenderWarmupActive = false;
        CompleteResponsiveLayoutTransition();
        SetExpandedChromeCompositionHidden(false);
        TitleBarGrid.IsHitTestVisible =
            _hostedContent is not FileWidgetContentAdapter { IsImportBusy: true };
        ShellContentPresenter.IsHitTestVisible = true;
        _hostedContent?.OnCompactStateChanged(true);
        ApplyChromeMode();
        return true;
    }

    internal void AbortCompactExpansionRenderWarmup()
    {
        if (!_isCompactRenderWarmupActive)
        {
            return;
        }

        _isCompactRenderWarmupActive = false;
        CancelResponsiveLayoutTransition();
        SetExpandedChromeCompositionHidden(false);
        TitleBarGrid.IsHitTestVisible =
            _hostedContent is not FileWidgetContentAdapter { IsImportBusy: true };
        ShellContentPresenter.IsHitTestVisible = true;
        _hostedContent?.OnCompactStateChanged(_isCollapsed);
        ApplyChromeMode();
    }

    private void SetExpandedChromeCompositionHidden(bool hidden)
    {
        float opacity = hidden ? 0f : 1f;
        ElementCompositionPreview.GetElementVisual(TitleBarGrid).Opacity = opacity;
        ElementCompositionPreview.GetElementVisual(HeaderDivider).Opacity = opacity;
        ElementCompositionPreview.GetElementVisual(ShellContentPresenter).Opacity = opacity;
    }

    public bool WarmCompactExpansionLayout(
        double targetWindowWidth,
        double targetWindowHeight,
        double expandedOuterRadius,
        double compactOuterRadius,
        double compactInnerRadius,
        double compactMediaRadius,
        string contentMode,
        WidgetCompactExpansionAnchor expansionAnchor = WidgetCompactExpansionAnchor.LeftTop)
    {
        if (!_isCollapsed ||
            _isCompactTransitionActive ||
            ShellContent is null ||
            !double.IsFinite(targetWindowWidth) ||
            !double.IsFinite(targetWindowHeight) ||
            targetWindowWidth <= 0 ||
            targetWindowHeight <= 0)
        {
            return false;
        }

        bool prepared = false;
        try
        {
            BeginResponsiveLayoutTransition(
                isCollapsing: false,
                targetWindowWidth,
                targetWindowHeight,
                expansionAnchor,
                frozenWindowWidth: targetWindowWidth,
                frozenWindowHeight: targetWindowHeight);
            prepared = PrepareCompactTransition(
                collapsed: false,
                expandedOuterRadius,
                compactOuterRadius,
                compactInnerRadius,
                compactMediaRadius,
                WidgetCompactTransitionVisualProfile.Resolve(
                    SettingsService.WidgetCompactAnimationNone, 0, false));
            if (!prepared)
            {
                return false;
            }

            // The compact layer remains fully opaque while the expanded title and
            // body are measured at their real target size behind it. This realizes
            // templates and performs the first expensive XAML layout without moving
            // or resizing the native window.
            double titleHeight = 0;
            if (!IsOverlayChromeMode)
            {
                TitleBarGrid.Measure(new Windows.Foundation.Size(
                    targetWindowWidth,
                    double.PositiveInfinity));
                titleHeight = Math.Max(
                    ResolveTitleBarLayoutHeight(),
                    TitleBarGrid.DesiredSize.Height);
            }
            double contentHeight = Math.Max(1, targetWindowHeight - titleHeight);
            var titleSize = new Windows.Foundation.Size(targetWindowWidth, titleHeight);
            var contentSize = new Windows.Foundation.Size(targetWindowWidth, contentHeight);

            if (titleHeight > 0 && TitleBarGrid.Visibility == Visibility.Visible)
            {
                TitleBarGrid.Measure(titleSize);
                TitleBarGrid.Arrange(new Windows.Foundation.Rect(0, 0, titleSize.Width, titleSize.Height));
            }

            ShellContentPresenter.Measure(contentSize);
            ShellContentPresenter.Arrange(
                new Windows.Foundation.Rect(0, 0, contentSize.Width, contentSize.Height));

            // ActualWidth/ActualHeight are constrained by the still-compact
            // native host and can remain zero even though the expanded visual
            // tree completed Measure/Arrange at the requested target size.
            // Reaching this point without an exception is the readiness signal;
            // treating host-clipped ActualSize as failure caused the same hidden
            // layout to repeat every retry interval until the user expanded it.
            PrewarmCompactTransitionCompositionResources();
            return true;
        }
        finally
        {
            CompleteResponsiveLayoutTransition();
            if (prepared || _isCompactTransitionActive || !_isCollapsed)
            {
                CompleteCompactTransition(collapsed: true, contentMode);
            }
        }
    }

    public bool PrepareCompactTransition(
        bool collapsed,
        double expandedOuterRadius,
        double compactOuterRadius,
        double compactInnerRadius,
        double compactMediaRadius,
        WidgetCompactTransitionVisualProfile? visualProfile = null,
        bool deferContentPresentation = false)
    {
        AbortCompactExpansionRenderWarmup();
        StopDeferredExpandedContentRevealAnimation();
        if (_isCollapsed == collapsed)
        {
            return false;
        }

        _expandedOuterCornerRadius = Math.Max(0, expandedOuterRadius);
        _compactOuterCornerRadius = Math.Max(0, compactOuterRadius);
        _compactInnerCornerRadius = Math.Max(0, compactInnerRadius);
        _compactMediaCornerRadius = Math.Max(0, compactMediaRadius);
        _ = EnsureCollapsedChromeLayer();
        _compactTransitionProfile = visualProfile ??
            WidgetCompactTransitionVisualProfile.Resolve(
                SettingsService.WidgetCompactAnimationSmooth,
                SettingsService.DefaultWidgetCompactAnimationDurationMs,
                SystemAnimationsEnabled());
        _transitionOuterCornerRadiusFrom = collapsed
            ? _expandedOuterCornerRadius
            : _compactOuterCornerRadius;
        _transitionOuterCornerRadiusTo = collapsed
            ? _compactOuterCornerRadius
            : _expandedOuterCornerRadius;
        _isCompactTransitionActive = true;
        if (!collapsed)
        {
            _isCompactDeferredContentPresentationActive = deferContentPresentation;
            _isCollapsed = false;
            if (!deferContentPresentation)
            {
                _hostedContent?.OnCompactStateChanged(false);
            }

            ApplyChromeMode();
            if (_isResponsiveLayoutTransitionActive)
            {
                FreezeTransitionContentLayout(
                    _transitionFrozenWindowWidth,
                    _transitionFrozenContentHeight,
                    _transitionExpansionAnchor,
                    _transitionFrozenWindowWidth);
            }

            if (_compactTransitionProfile.IsAnimated &&
                _compactTransitionProfile.DurationMilliseconds > 0)
            {
                UpdateLayout();
            }
        }

        CollapsedChromeLayer.Visibility = Visibility.Visible;
        CollapsedChromeLayer.IsHitTestVisible = false;
        CollapsedChromeLayer.Opacity = 1;
        // Keep the real expanded tree visible while the HWND grows when warm.
        // When cold/hibernated/paged-out, deferContentPresentation keeps only
        // the lightweight title chrome live during the morph and reveals the
        // leaf content right after the panel settles at full size.
        TitleBarGrid.Opacity = 1;
        if (!_isCompactDeferredContentPresentationActive)
        {
            ShellContentPresenter.Opacity = 1;
        }
        ApplyCompactInnerCornerRadii();
        SetBackgroundCornerRadius(_transitionOuterCornerRadiusFrom);
        _lastCompactTransitionCornerRadius = _transitionOuterCornerRadiusFrom;
        _isCompactCompositionTransitionActive =
            StartCompactCompositionTransition(collapsed);
        if (_isCompactCompositionTransitionActive)
        {
            CompactIdentityHost.Opacity = 1;
            CompactTextContainer.Opacity = 1;
            CompactBadge.Opacity = 1;
            CompactLiveIndicatorHost.Opacity = 1;
        }
        else
        {
            CompactIdentityHost.Opacity = _compactTransitionProfile.GetCompactIdentityOpacity(collapsed, 0);
            double initialCompactTextOpacity = _compactTransitionProfile.GetCompactTextOpacity(collapsed, 0);
            CompactTextContainer.Opacity = initialCompactTextOpacity;
            CompactBadge.Opacity = initialCompactTextOpacity;
            CompactLiveIndicatorHost.Opacity = initialCompactTextOpacity;
        }

        return true;
    }

    public void SetCompactTransitionProgress(
        bool collapsed,
        double progress,
        double? surfaceProgress = null)
    {
        if (!_isCompactTransitionActive || CollapsedChromeLayer is null)
        {
            return;
        }

        double value = Math.Clamp(progress, 0, 1);
        double effectiveSurfaceProgress = Math.Clamp(
            surfaceProgress ?? _compactTransitionProfile.EaseSurfaceProgress(collapsed, value),
            0,
            1);
        bool compositionOwnsVisuals = _isCompactCompositionTransitionActive;
        if (!compositionOwnsVisuals)
        {
            double compactOpacity =
                _compactTransitionProfile.GetCompactSurfaceOpacity(collapsed, value);
            CollapsedChromeLayer.Opacity = compactOpacity;

            double liveContentOpacity =
                _compactTransitionProfile.GetLiveContentOpacity(collapsed, value);
            TitleBarGrid.Opacity = liveContentOpacity;
            if (!_isCompactDeferredContentPresentationActive)
            {
                ShellContentPresenter.Opacity = liveContentOpacity;
            }
            double compactIdentityOpacity =
                _compactTransitionProfile.GetCompactIdentityOpacity(collapsed, value);
            double compactTextOpacity =
                _compactTransitionProfile.GetCompactTextOpacity(collapsed, value);
            CompactIdentityHost.Opacity = compactIdentityOpacity;
            CompactTextContainer.Opacity = compactTextOpacity;
            CompactBadge.Opacity = compactTextOpacity;
            CompactLiveIndicatorHost.Opacity = compactTextOpacity;
        }

        // The XAML/background corner geometry still follows the real HWND
        // boundary. Independent fades/translations keep their compositor clock
        // on both Windows versions while this small geometry update is paced.
        double cornerRadius = Lerp(
            _transitionOuterCornerRadiusFrom,
            _transitionOuterCornerRadiusTo,
            effectiveSurfaceProgress);
        if (double.IsNaN(_lastCompactTransitionCornerRadius) ||
            Math.Abs(cornerRadius - _lastCompactTransitionCornerRadius) >= 0.75 ||
            effectiveSurfaceProgress >= 1)
        {
            SetBackgroundCornerRadius(cornerRadius);
            _lastCompactTransitionCornerRadius = cornerRadius;
        }

        // Full-bleed background: fade out earlier during expand, fade in later during collapse
        bool hasFullBleed = _compactPresentation?.UseFullBleedBackground == true &&
            _compactPresentation.Thumbnail is not null;
        if (hasFullBleed && !compositionOwnsVisuals)
        {
            double fullBleedOpacity;
            double fullBleedScale;
            if (collapsed)
            {
                // Collapsing: fade in after compact layer is partially visible
                fullBleedOpacity = SmoothStep(Math.Clamp((value - 0.5) / 0.5, 0, 1));
                fullBleedScale = Lerp(0.98, 1.0, fullBleedOpacity);
            }
            else
            {
                // Expanding: fade out faster than compact layer, shrink slightly
                fullBleedOpacity = 1 - SmoothStep(Math.Clamp(value / 0.35, 0, 1));
                fullBleedScale = Lerp(1.0, 1.02, 1 - fullBleedOpacity);
            }

            CompactFullBleedBackground.Opacity = fullBleedOpacity;
            CompactFullBleedOverlay.Opacity =
                fullBleedOpacity * ResolveFullBleedOverlayOpacity();
            if (!_isCompactCompositionTransitionActive)
            {
                FullBleedScaleTransform.ScaleX = fullBleedScale;
                FullBleedScaleTransform.ScaleY = fullBleedScale;
            }
        }
    }

    private static double SmoothStep(double value) => value * value * (3 - (2 * value));

    private static double Lerp(double start, double end, double progress) =>
        start + ((end - start) * Math.Clamp(progress, 0, 1));

    private bool StartCompactCompositionTransition(bool collapsed)
    {
        if (!_compactTransitionProfile.IsAnimated ||
            _compactTransitionProfile.DurationMilliseconds <= 0)
        {
            return false;
        }

        bool animateContentPresenter = !_isCompactDeferredContentPresentationActive;
        bool anchorsBottom = _transitionExpansionAnchor is
            WidgetCompactExpansionAnchor.LeftBottom or
            WidgetCompactExpansionAnchor.RightBottom;
        try
        {
            StartCompactScaleAnimation(
                TitleBarGrid,
                progress => _compactTransitionProfile.GetLiveContentScale(collapsed, progress),
                useExpansionAnchor: true);
            StartCompactTranslationAnimation(
                TitleBarGrid,
                progress => _compactTransitionProfile.GetLiveContentTranslationY(collapsed, progress, anchorsBottom));
            if (animateContentPresenter)
            {
                StartCompactScaleAnimation(
                    ShellContentPresenter,
                    progress => _compactTransitionProfile.GetLiveContentScale(collapsed, progress),
                    useExpansionAnchor: true);
                StartCompactTranslationAnimation(
                    ShellContentPresenter,
                    progress => _compactTransitionProfile.GetLiveContentTranslationY(collapsed, progress, anchorsBottom));
            }

            StartCompactScaleAnimation(
                CollapsedChromeLayer,
                progress => _compactTransitionProfile.GetCompactContentScale(collapsed, progress),
                useExpansionAnchor: true);

            if (collapsed)
            {
                StartCompactOpacityAnimation(
                    TitleBarGrid,
                    progress => _compactTransitionProfile.GetLiveContentOpacity(collapsed, progress));
                if (animateContentPresenter)
                {
                    StartCompactOpacityAnimation(
                        ShellContentPresenter,
                        progress => _compactTransitionProfile.GetLiveContentOpacity(collapsed, progress));
                }

                StartCompactOpacityAnimation(
                    CompactIdentityHost,
                    progress => _compactTransitionProfile.GetCompactIdentityOpacity(collapsed, progress));
                StartCompactOpacityAnimation(
                    CompactTextContainer,
                    progress => _compactTransitionProfile.GetCompactTextOpacity(collapsed, progress));
                StartCompactOpacityAnimation(
                    CompactBadge,
                    progress => _compactTransitionProfile.GetCompactTextOpacity(collapsed, progress));
                StartCompactOpacityAnimation(
                    CompactLiveIndicatorHost,
                    progress => _compactTransitionProfile.GetCompactTextOpacity(collapsed, progress));
            }
            else
            {
                Visual titleVisual = ElementCompositionPreview.GetElementVisual(TitleBarGrid);
                titleVisual.Opacity = 1f;
                if (animateContentPresenter)
                {
                    Visual contentVisual = ElementCompositionPreview.GetElementVisual(ShellContentPresenter);
                    contentVisual.Opacity = 1f;
                }

                StartCompactOpacityAnimation(
                    CollapsedChromeLayer,
                    progress => _compactTransitionProfile.GetCompactSurfaceOpacity(collapsed, progress));
            }

            bool hasFullBleed = _compactPresentation?.UseFullBleedBackground == true &&
                _compactPresentation.Thumbnail is not null;
            if (hasFullBleed)
            {
                StartCompactScaleAnimation(
                    CompactFullBleedBackground,
                    progress => ResolveFullBleedTransition(collapsed, progress).Scale);
                StartCompactOpacityAnimation(
                    CompactFullBleedBackground,
                    progress => ResolveFullBleedTransition(collapsed, progress).Opacity);
                StartCompactOpacityAnimation(
                    CompactFullBleedOverlay,
                    progress => ResolveFullBleedTransition(collapsed, progress).Opacity *
                        ResolveFullBleedOverlayOpacity());
            }

            return true;
        }
        catch (Exception ex)
        {
            StopCompactCompositionTransitionAnimations(force: true);
            App.LogVerbose($"[Compact] Composition visual fallback: {ex.Message}");
            return false;
        }
    }

    private void StopCompactGeometricClipAnimation()
    {
        try
        {
            Visual shellVisual = ElementCompositionPreview.GetElementVisual(ShellRoot);
            if (shellVisual.Clip is not null)
            {
                var (geometry, _, sizeExpression) = _compositionResources.GetCompactClip(
                    shellVisual.Compositor);
                geometry.StopAnimation("Size");
                geometry.StopAnimation("CornerRadius");
                sizeExpression.ClearAllParameters();
                shellVisual.Clip = null;
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[Compact] Geometric clip cleanup skipped: {ex.Message}");
        }
    }

    private void StartCompactOpacityAnimation(
        FrameworkElement element,
        Func<double, double> valueSelector)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        visual.Opacity = (float)Math.Clamp(valueSelector(0), 0, 1);
        ScalarKeyFrameAnimation animation = _compositionResources.GetScalar(
            visual.Compositor, WidgetAnimationTemplate.CompactOpacity);
        animation.Duration = TimeSpan.FromMilliseconds(
            _compactTransitionProfile.DurationMilliseconds);
        const int sampleCount = 24;
        for (int step = 0; step <= sampleCount; step++)
        {
            double timeProgress = step / (double)sampleCount;
            animation.InsertKeyFrame(
                (float)timeProgress,
                (float)Math.Clamp(valueSelector(timeProgress), 0, 1));
        }

        visual.StartAnimation("Opacity", animation);
    }

    private void StartCompactScaleAnimation(
        FrameworkElement element,
        Func<double, double> valueSelector,
        bool useExpansionAnchor = false)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = ResolveCompactScaleCenterPoint(element, visual, useExpansionAnchor);
        Vector3KeyFrameAnimation animation = _compositionResources.GetVector3(
            visual.Compositor, WidgetAnimationTemplate.CompactScale);
        animation.Duration = TimeSpan.FromMilliseconds(
            _compactTransitionProfile.DurationMilliseconds);
        const int sampleCount = 24;
        for (int step = 0; step <= sampleCount; step++)
        {
            double timeProgress = step / (double)sampleCount;
            float scale = (float)valueSelector(timeProgress);
            float scaleX = useExpansionAnchor && !_transitionHasHorizontalMorph ? 1f : scale;
            animation.InsertKeyFrame((float)timeProgress, new Vector3(scaleX, scale, 1));
        }

        visual.StartAnimation("Scale", animation);
    }

    private Vector3 ResolveCompactScaleCenterPoint(
        FrameworkElement element,
        Visual visual,
        bool useExpansionAnchor)
    {
        float width = visual.Size.X > 0
            ? visual.Size.X
            : (float)(double.IsFinite(element.Width) && element.Width > 0
                ? element.Width
                : element.ActualWidth > 0
                    ? element.ActualWidth
                    : _transitionFrozenWindowWidth);
        float height = visual.Size.Y > 0
            ? visual.Size.Y
            : (float)(double.IsFinite(element.Height) && element.Height > 0
                ? element.Height
                : element.ActualHeight > 0
                    ? element.ActualHeight
                    : element == ShellContentPresenter
                        ? _transitionFrozenContentHeight
                        : 40);
        if (!useExpansionAnchor)
        {
            return new Vector3(width * 0.5f, height * 0.5f, 0);
        }

        bool anchorsRight = _transitionExpansionAnchor is
            WidgetCompactExpansionAnchor.RightTop or
            WidgetCompactExpansionAnchor.RightBottom;
        bool anchorsBottom = _transitionExpansionAnchor is
            WidgetCompactExpansionAnchor.LeftBottom or
            WidgetCompactExpansionAnchor.RightBottom;
        float centerX = !_transitionHasHorizontalMorph
            ? width * 0.5f
            : (anchorsRight ? width : 0f);
        float centerY = element == CollapsedChromeLayer
            ? height * 0.5f
            : (anchorsBottom ? height : 0f);
        return new Vector3(centerX, centerY, 0);
    }

    private void StartCompactTranslationAnimation(
        FrameworkElement element,
        Func<double, double> valueSelector)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Vector3KeyFrameAnimation animation = _compositionResources.GetVector3(
            visual.Compositor, WidgetAnimationTemplate.CompactTranslation);
        animation.Duration = TimeSpan.FromMilliseconds(
            _compactTransitionProfile.DurationMilliseconds);
        const int sampleCount = 24;
        for (int step = 0; step <= sampleCount; step++)
        {
            double timeProgress = step / (double)sampleCount;
            animation.InsertKeyFrame((float)timeProgress,
                new Vector3(0, (float)valueSelector(timeProgress), 0));
        }

        visual.StartAnimation("Translation", animation);
    }

    public void PlayLayerRaiseMicroImpulse()
    {
        if (!SystemAnimationsEnabled() ||
            _isCompactTransitionActive ||
            _isCompactCompositionTransitionActive)
        {
            return;
        }

        try
        {
            Visual shellVisual = ElementCompositionPreview.GetElementVisual(ShellRoot);
            float width = (float)Math.Max(1, ShellRoot.ActualWidth);
            float height = (float)Math.Max(1, ShellRoot.ActualHeight);
            shellVisual.CenterPoint = new Vector3(width * 0.5f, height * 0.5f, 0);
            Vector3KeyFrameAnimation scaleImpulse = _compositionResources.GetVector3(
                shellVisual.Compositor,
                WidgetAnimationTemplate.LayerRaiseScale);
            CubicBezierEasingFunction easing = _compositionResources.GetSoftOutEasing(
                shellVisual.Compositor);
            scaleImpulse.Duration = TimeSpan.FromMilliseconds(360);
            scaleImpulse.InsertKeyFrame(0f, Vector3.One);
            scaleImpulse.InsertKeyFrame(0.4f, new Vector3(1.012f, 1.012f, 1f), easing);
            scaleImpulse.InsertKeyFrame(1f, Vector3.One, _compositionResources.GetBreathingEasing(shellVisual.Compositor));
            shellVisual.StartAnimation("Scale", scaleImpulse);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[Composition] Layer raise impulse skipped: {ex.Message}");
        }
    }

    private static (double Opacity, double Scale) ResolveFullBleedTransition(
        bool collapsed,
        double progress)
    {
        if (collapsed)
        {
            double opacity = SmoothStep(Math.Clamp((progress - 0.5) / 0.5, 0, 1));
            return (opacity, Lerp(0.98, 1, opacity));
        }

        double expandingOpacity = 1 - SmoothStep(Math.Clamp(progress / 0.35, 0, 1));
        return (expandingOpacity, Lerp(1, 1.02, 1 - expandingOpacity));
    }

    private void StopCompactCompositionTransitionAnimations(bool force = false)
    {
        if (!force && !_isCompactCompositionTransitionActive)
        {
            return;
        }

        StopCompactGeometricClipAnimation();

        // A visibility storyboard must not overwrite the restored base values.
        _compactFullBleedVisibilityStoryboard.StopAndClear();
        bool hasTranslationAnimation = _isCompactCompositionTransitionActive;
        bool hasFullBleed = _compactPresentation?.UseFullBleedBackground == true &&
            _compactPresentation.Thumbnail is not null;

        foreach (FrameworkElement? candidate in new FrameworkElement?[]
        {
            CollapsedChromeLayer,
            TitleBarGrid,
            ShellContentPresenter,
            CompactIdentityHost,
            CompactTextContainer,
            CompactBadge,
            CompactLiveIndicatorHost,
            CompactFullBleedBackground,
            CompactFullBleedOverlay
        })
        {
            if (candidate is not { } element)
            {
                continue;
            }

            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            if (hasTranslationAnimation &&
                (element == TitleBarGrid || element == ShellContentPresenter))
            {
                // StopAnimation throws ArgumentException for a property that
                // was never created, which happens when an expansion completes
                // without any animated collapse ever having run (a capsule
                // restored straight from launch). Enabling translation is
                // idempotent and guarantees the property exists; the crash
                // otherwise aborts the completion chain and leaves the capsule
                // layer visible over the expanded body.
                ElementCompositionPreview.SetIsTranslationEnabled(element, true);
                visual.StopAnimation("Translation");
                // The collapse curve ends at a non-zero rise (-4), and stopping
                // a Composition animation leaves the property at its last key
                // frame. Without an explicit reset the next expansion keeps
                // the offset and the title renders clipped at the window top.
                visual.Properties.InsertVector3(
                    "Translation",
                    System.Numerics.Vector3.Zero);
            }
            // The image scrim is a separate sibling of the image. Making every
            // visual opaque exposes its black gradient even on non-image capsules.
            // Synchronize both layers: an unchanged XAML value alone does not undo
            // an opacity written directly to the backing Composition visual.
            double restoredOpacity = 1;
            if (element == CompactFullBleedBackground)
            {
                restoredOpacity = hasFullBleed ? 1 : 0;
                element.Opacity = restoredOpacity;
            }
            else if (element == CompactFullBleedOverlay)
            {
                restoredOpacity = hasFullBleed ? ResolveFullBleedOverlayOpacity() : 0;
                element.Opacity = restoredOpacity;
            }
            visual.Opacity = (float)restoredOpacity;
            visual.Scale = Vector3.One;
        }

        _isCompactCompositionTransitionActive = false;
    }

    public void CompleteCompactTransition(
        bool collapsed,
        string contentMode,
        bool keepContentDeferred = false)
    {
        _isCompactTransitionActive = false;
        if (collapsed || !keepContentDeferred)
        {
            _isCompactDeferredContentPresentationActive = false;
        }

        SetBackgroundCornerRadius(collapsed
            ? _compactOuterCornerRadius
            : _expandedOuterCornerRadius);
        // SetCollapsed performs the single final visual reset. Calling it both
        // here and there repeatedly resolves/stops the same Composition visuals
        // at the end of every capsule transition.
        SetCollapsed(collapsed, contentMode);
    }

    public void RevealDeferredExpandedContent(bool animate, bool anchorsBottom = false)
    {
        _isCompactDeferredContentPresentationActive = false;
        StopDeferredExpandedContentRevealAnimation();
        if (_isCollapsed || _isCompactTransitionActive)
        {
            return;
        }

        bool shouldAnimateReveal = animate &&
            SystemAnimationsEnabled() &&
            ShellContent is not null &&
            OutgoingContentPresenter.Content is null &&
            !_isContentSnapshotTransitionActive;
        if (shouldAnimateReveal)
        {
            Visual contentVisual = ElementCompositionPreview.GetElementVisual(ShellContentPresenter);
            contentVisual.Opacity = 0f;
        }

        ShellContentPresenter.Visibility = Visibility.Visible;
        ShellContentPresenter.Opacity = 1;
        ShellContentPresenter.IsHitTestVisible = true;
        _hostedContent?.OnCompactStateChanged(false);

        double viewportWidth = ContentTransitionViewport.ActualWidth;
        double viewportHeight = ContentTransitionViewport.ActualHeight;
        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            viewportWidth = _responsiveTargetContentWidth;
            viewportHeight = _responsiveTargetContentHeight;
        }

        _responsiveLayoutContent?.CompleteResponsiveLayoutTransition(
            viewportWidth,
            viewportHeight);
        NotifyHostedContentViewportSize(viewportWidth, viewportHeight);
        ShellContentPresenter.UpdateLayout();

        if (shouldAnimateReveal)
        {
            StartDeferredExpandedContentRevealAnimation(anchorsBottom);
        }
    }

    private void StartDeferredExpandedContentRevealAnimation(bool anchorsBottom)
    {
        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(ShellContentPresenter, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(ShellContentPresenter);
            CubicBezierEasingFunction easing = _compositionResources.GetSoftOutEasing(
                visual.Compositor);
            TimeSpan duration = TimeSpan.FromMilliseconds(
                DeferredExpandedContentRevealDurationMs);

            ScalarKeyFrameAnimation opacity = _compositionResources.GetScalar(
                visual.Compositor,
                WidgetAnimationTemplate.DeferredContentRevealOpacity);
            opacity.Duration = duration;
            opacity.InsertKeyFrame(0f, 0f);
            opacity.InsertKeyFrame(1f, 1f, easing);

            float startOffsetY = anchorsBottom ? 4f : -4f;
            Vector3KeyFrameAnimation translation = _compositionResources.GetVector3(
                visual.Compositor,
                WidgetAnimationTemplate.DeferredContentRevealTranslation);
            translation.Duration = duration;
            translation.InsertKeyFrame(0f, new Vector3(0f, startOffsetY, 0f));
            translation.InsertKeyFrame(1f, Vector3.Zero, easing);

            _isDeferredContentRevealAnimationActive = true;
            visual.StartAnimation("Opacity", opacity);
            visual.StartAnimation("Translation", translation);
        }
        catch (Exception ex)
        {
            StopDeferredExpandedContentRevealAnimation();
            App.LogVerbose($"[Compact] Deferred content reveal fallback: {ex.Message}");
        }
    }

    private void StopDeferredExpandedContentRevealAnimation()
    {
        if (!_isDeferredContentRevealAnimationActive)
        {
            return;
        }

        _isDeferredContentRevealAnimationActive = false;
        if (ShellContentPresenter is null)
        {
            return;
        }

        try
        {
            ElementCompositionPreview.SetIsTranslationEnabled(ShellContentPresenter, true);
            Visual visual = ElementCompositionPreview.GetElementVisual(ShellContentPresenter);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
            visual.Opacity = 1f;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[Compact] Deferred content reveal cleanup skipped: {ex.Message}");
        }
    }

    public void CancelCompactTransition()
    {
        AbortCompactExpansionRenderWarmup();
        StopDeferredExpandedContentRevealAnimation();
        _isCompactDeferredContentPresentationActive = false;
        _isCompactTransitionActive = false;
        SetBackgroundCornerRadius(_isCollapsed
            ? _compactOuterCornerRadius
            : _expandedOuterCornerRadius);
        ResetCompactTransitionVisuals();
        _hostedContent?.OnCompactStateChanged(_isCollapsed);
        ApplyChromeMode();
    }

    private void ResetCompactTransitionVisuals()
    {
        StopCompactCompositionTransitionAnimations();
        _lastCompactTransitionCornerRadius = double.NaN;
        TitleBarGrid.Opacity = 1;
        ShellContentPresenter.Opacity = 1;
        if (CollapsedChromeLayer is not null)
        {
            CollapsedChromeLayer.Opacity = 1;
            CompactIdentityHost.Opacity = 1;
            CompactTextContainer.Opacity = 1;
            CompactBadge.Opacity = 1;
            CompactLiveIndicatorHost.Opacity = 1;
            CollapsedChromeLayer.IsHitTestVisible = true;
            FullBleedScaleTransform.ScaleX = 1;
            FullBleedScaleTransform.ScaleY = 1;
        }
        TitleIdentityHost.Opacity = 1;
        if (GroupTitleSwitcher is not null)
        {
            GroupTitleSwitcher.Opacity = 1;
        }
    }

    public void SetCompactPresentation(WidgetCompactPresentation presentation)
    {
        WidgetCompactPresentation? previous = _compactPresentation;
        if (previous == presentation)
        {
            return;
        }

        if (CollapsedChromeLayer is null)
        {
            _compactPresentation = presentation;
            return;
        }

        ApplyCompactPresentationCore(presentation, previous);
    }

    private void ApplyCompactPresentationCore(
        WidgetCompactPresentation presentation,
        WidgetCompactPresentation? previous)
    {
        if (previous is not null)
        {
            WidgetCompactPresentation previousWithCurrentProgress = previous with
            {
                Progress = presentation.Progress,
                IsProgressIndeterminate = presentation.IsProgressIndeterminate,
                MusicProgress = presentation.MusicProgress
            };
            if (previousWithCurrentProgress == presentation)
            {
                bool liveProgressChanged =
                    previous.Progress != presentation.Progress ||
                    previous.IsProgressIndeterminate != presentation.IsProgressIndeterminate;
                bool musicProgressChanged = previous.MusicProgress != presentation.MusicProgress;

                _compactPresentation = presentation;
                if (liveProgressChanged)
                {
                    ApplyCompactLiveState();
                }

                if (musicProgressChanged)
                {
                    // The music ViewModel advances every 500 ms. Once the bar has
                    // been initialized, a progress-only update needs one transform
                    // write instead of rebuilding text, brushes, icons and effects.
                    if (previous.MusicProgress.HasValue &&
                        presentation.MusicProgress is { } musicProgress)
                    {
                        CompactMusicProgressTransform.ScaleX =
                            Math.Clamp(musicProgress, 0, 1);
                    }
                    else
                    {
                        ApplyCompactMusicProgress();
                    }
                }

                return;
            }
        }

        bool textChanged = previous is null ||
            !string.Equals(previous.Title, presentation.Title, StringComparison.Ordinal) ||
            !string.Equals(previous.Summary, presentation.Summary, StringComparison.Ordinal);
        bool musicPlaybackChanged = previous is not null &&
            presentation.ShowVinyl &&
            previous.IsPlaying != presentation.IsPlaying;
        bool liveStateChanged = previous is not null &&
            !string.IsNullOrWhiteSpace(presentation.LiveStateKey) &&
            !string.Equals(previous.LiveStateKey, presentation.LiveStateKey, StringComparison.Ordinal);
        bool structureChanged = previous is null ||
            previous.ShowPrimaryAction != presentation.ShowPrimaryAction ||
            previous.ShowMediaControls != presentation.ShowMediaControls ||
            previous.UseStackedText != presentation.UseStackedText ||
            previous.UseFullBleedBackground != presentation.UseFullBleedBackground ||
            (previous.IconImage is not null) != (presentation.IconImage is not null) ||
            string.IsNullOrWhiteSpace(previous.Summary) != string.IsNullOrWhiteSpace(presentation.Summary);

        // Tear down the previous moving canvas before replacing either text.
        // Otherwise one frame can render the new string with the old string's
        // width/translation, which is visible as a flash and small position jump.
        if (textChanged || musicPlaybackChanged && !presentation.IsPlaying)
        {
            StopCompactMarquee();
        }

        _compactPresentation = presentation;
        CompactTitleText.Text = presentation.Title;
        if (CompactTitleMarqueeClone is not null)
        {
            CompactTitleMarqueeClone.Text = presentation.Title;
        }
        CompactSummaryText.Text = presentation.Summary;
        if (CompactSummaryMarqueeClone is not null)
        {
            CompactSummaryMarqueeClone.Text = presentation.Summary;
        }
        CompactTitleIcon.Glyph = presentation.Glyph;
        CompactTitleIcon.LabelText = presentation.Title;

        bool useFullBleed = presentation.UseFullBleedBackground && presentation.Thumbnail is not null;
        CompactFullBleedBackground.Source = useFullBleed ? presentation.Thumbnail : null;
        CompactFullBleedClip.Opacity = useFullBleed
            ? ResolveFullBleedBackgroundOpacity()
            : 1.0;
        ApplyFullBleedOverlayTheme();
        ApplyFullBleedVisibility(useFullBleed);

        // Paused dim overlay: show when full-bleed but not playing
        bool showPausedDim = useFullBleed &&
            (presentation.ShowMediaControls || presentation.ShowVinyl) &&
            !presentation.IsPlaying;
        CompactPausedDim.Visibility = showPausedDim
            ? Visibility.Visible
            : Visibility.Collapsed;

        CompactThumbnail.Source = useFullBleed ? null : presentation.Thumbnail;
        CompactThumbnailHost.Visibility = !useFullBleed && presentation.Thumbnail is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        // White border for thumbnail (quick capture)
        if (!useFullBleed && presentation.Thumbnail is not null)
        {
            CompactThumbnailHost.BorderBrush = SharedBrushCache.GetOrCreate(Windows.UI.Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF));
            CompactThumbnailHost.BorderThickness = new Thickness(1);
        }
        else
        {
            CompactThumbnailHost.ClearValue(Border.BorderBrushProperty);
            CompactThumbnailHost.ClearValue(Border.BorderThicknessProperty);
        }

        // Rotating vinyl disc (music capsule). Label reuses the album cover.
        bool showVinyl = presentation.ShowVinyl && presentation.Thumbnail is not null;
        if (showVinyl)
        {
            EnsureCompactVinylHost();
            if (CompactVinylHost is not null && CompactVinylLabelBrush is not null)
            {
                CompactVinylHost.Visibility = Visibility.Visible;
                CompactVinylLabelBrush.ImageSource = presentation.Thumbnail;
            }
        }
        else if (CompactVinylHost is not null)
        {
            CompactVinylHost.Visibility = Visibility.Collapsed;
            if (CompactVinylLabelBrush is not null)
            {
                CompactVinylLabelBrush.ImageSource = null;
            }
        }

        bool hasIconImage = presentation.IconImage is not null;
        CompactIconImage.Source = presentation.IconImage;
        CompactIconImage.Visibility = hasIconImage
            ? Visibility.Visible
            : Visibility.Collapsed;

        // When an icon image (e.g. weather) is shown, the default widget glyph must be
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
        // icon image. Setting the target property directly is immune to that reset.
        // When the icon should be visible we honor the user's global icon-mode
        // preference so the compact glyph still respects their setting.
        bool showTitleIcon = !hasIconImage && !useFullBleed && presentation.Thumbnail is null && !showVinyl;
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
        if (presentation.ShowMediaControls)
        {
            EnsureCompactMediaControls();
        }
        if (CompactPreviousButton is not null)
        {
            CompactPreviousButton.IsEnabled = presentation.CanGoPrevious;
        }
        if (CompactNextButton is not null)
        {
            CompactNextButton.IsEnabled = presentation.CanGoNext;
        }
        if (CompactPlayPauseIcon is not null)
        {
            CompactPlayPauseIcon.Kind = presentation.IsPlaying
                ? MusicTransportIconKind.Pause
                : MusicTransportIconKind.Play;
        }
        ApplyCompactActionLabels(presentation.IsPlaying);
        UpdateCompactVinylRotation(showVinyl && presentation.IsPlaying);
        ApplyCompactForegroundTheme(presentation);

        // Badge
        bool hasBadge = !string.IsNullOrWhiteSpace(presentation.BadgeText);
        CompactBadge.Visibility = hasBadge ? Visibility.Visible : Visibility.Collapsed;
        if (hasBadge)
        {
            CompactBadgeText.Text = presentation.BadgeText;
            CompactBadge.Background = presentation.BadgeIsWarning
                ? ResolveCriticalBadgeBrush()
                : SharedBrushCache.GetOrCreate(
                    App.Current?.ThemeService?.GetEffectiveAccentColor() ??
                    AccentColorHelper.DefaultAccentColor);
        }

        // Visual effects
        ApplyColorField(presentation);
        ApplyEdgeGlow(presentation);
        ApplyConditionalAnimations(presentation);

        ApplyCompactLiveState();
        ApplyCompactMusicProgress();

        if (structureChanged)
        {
            ApplyCompactAdaptiveLayout();
        }

        if (textChanged || musicPlaybackChanged)
        {
            QueueCompactMarquee();
        }

        if (liveStateChanged && _isCollapsed)
        {
            AnimateCompactLiveChange();
        }
    }

    internal void ClearCompactBitmaps()
    {
        if (_compactPresentation is { } current &&
            (current.Thumbnail is not null || current.IconImage is not null))
        {
            _compactPresentation = current with
            {
                Thumbnail = null,
                IconImage = null,
                UseFullBleedBackground = false
            };
        }

        if (CollapsedChromeLayer is null)
        {
            return;
        }

        CompactFullBleedBackground.Source = null;
        ApplyFullBleedVisibility(false);
        CompactThumbnail.Source = null;
        CompactThumbnailHost.Visibility = Visibility.Collapsed;
        CompactThumbnailHost.ClearValue(Border.BorderBrushProperty);
        CompactThumbnailHost.ClearValue(Border.BorderThicknessProperty);
        if (CompactVinylLabelBrush is not null)
        {
            CompactVinylLabelBrush.ImageSource = null;
        }
        CompactIconImage.Source = null;
        CompactIconImage.Visibility = Visibility.Collapsed;
    }

    private void ApplyCompactAdaptiveLayout()
    {
        if (_compactPresentation is null || CollapsedChromeLayer is null)
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
        double identityBaseSize = Math.Max(identityVisualSize, identityHitSize);
        double groupRailWidth = _groupPresentation is not null
            ? CompactGroupPositionRailColumn.Width.Value
            : 0;
        CompactIdentityHost.Width = identityBaseSize + groupRailWidth;
        CompactIdentityHost.Height = Math.Max(identityVisualSize, identityHitSize);
        CompactIdentityVisualHost.Opacity = (showVinyl || !useFullBleed) ? 1 : 0;
        if (!_isCompactTransitionActive)
        {
            CompactIdentityHost.Opacity = 1;
        }
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
        if (showMediaPlayPause)
        {
            EnsureCompactMediaControls();
        }
        if (CompactPreviousButton is not null)
        {
            CompactPreviousButton.Visibility = showExtendedMedia
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (CompactPlayPauseButton is not null)
        {
            CompactPlayPauseButton.Visibility = showMediaPlayPause
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (CompactNextButton is not null)
        {
            CompactNextButton.Visibility = showExtendedMedia
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        CompactExpandButton.Visibility = _usesSmartCompactBehavior
            ? Visibility.Collapsed
            : Visibility.Visible;

        UpdateCompactActionRegionWidth();
        ApplyCompactTextVisibility();
        QueueCompactTextViewportWidths();
    }

    private void QueueCompactTextViewportWidths()
    {
        if (_compactTextViewportUpdateQueued)
        {
            return;
        }

        _compactTextViewportUpdateQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _compactTextViewportUpdateQueued = false;
            if (IsLoaded)
            {
                UpdateCompactTextViewportWidths();
            }
        }))
        {
            _compactTextViewportUpdateQueued = false;
        }
    }

    private const double CompactVinylSecondsPerRevolution = 9.0;

    private void UpdateCompactVinylRotation(bool isPlaying)
    {
        // Idempotent: ApplyCompactPresentation is invoked on every progress/state tick,
        // so only start/stop when the desired rotating state actually changes.
        bool shouldRotate = _isHostVisualActivityEnabled &&
            IsLoaded &&
            _isCollapsed &&
            CompactVinylHost is { Visibility: Visibility.Visible } &&
            isPlaying &&
            VinylRotationAnimationsEnabled() &&
            SystemAnimationsEnabled();
        if (shouldRotate == _isCompactVinylRotating)
        {
            return;
        }

        if (shouldRotate)
        {
            EnsureCompactVinylHost();
            if (CompactVinylLabelHost is null)
            {
                return;
            }

            _isCompactVinylRotating = true;
            _compactVinylRotationStartedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
            Visual visual = ElementCompositionPreview.GetElementVisual(CompactVinylLabelHost);
            visual.CenterPoint = new Vector3(6.5f, 6.5f, 0f);
            Compositor compositor = visual.Compositor;
            using ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
            using LinearEasingFunction linear = compositor.CreateLinearEasingFunction();
            animation.InsertKeyFrame(0f, _compactVinylBaseAngleDegrees);
            animation.InsertKeyFrame(1f, _compactVinylBaseAngleDegrees + 360f, linear);
            animation.Duration = TimeSpan.FromSeconds(CompactVinylSecondsPerRevolution);
            animation.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("RotationAngleInDegrees", animation);
        }
        else
        {
            StopCompactVinylRotation();
        }
    }

    private void UpdateCompactActionRegionWidth()
    {
        if (CollapsedChromeLayer is null)
        {
            return;
        }

        Button?[] actionButtons =
        [
            CompactPrimaryActionButton,
            CompactPreviousButton,
            CompactPlayPauseButton,
            CompactNextButton,
            CompactExpandButton
        ];
        Button[] visibleButtons = actionButtons
            .OfType<Button>()
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
        if (_compactPresentation is null ||
            CollapsedChromeLayer is null ||
            CompactTextContainer.ActualWidth <= 0)
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
        var rect = new Windows.Foundation.Rect(0, 0, safeWidth, Math.Max(1, viewport.Height));
        if (viewport.Clip is RectangleGeometry existingClip)
        {
            existingClip.Rect = rect;
        }
        else
        {
            viewport.Clip = new RectangleGeometry
            {
                Rect = rect
            };
        }
        textBlock.Width = safeWidth;
        textBlock.TextTrimming = TextTrimming.CharacterEllipsis;
    }

    private void ApplyCompactActionLabels(bool isPlaying)
    {
        var localization = App.Current.LocalizationService;
        SetAccessibleLabel(CompactPrimaryActionButton, localization.T("Todo.Menu.MarkCompleted"));
        if (CompactPreviousButton is not null)
        {
            SetAccessibleLabel(CompactPreviousButton, localization.T("Music.Control.Previous"));
        }
        if (CompactPlayPauseButton is not null)
        {
            SetAccessibleLabel(
                CompactPlayPauseButton,
                localization.T(isPlaying ? "Music.Control.Pause" : "Music.Control.Play"));
        }
        if (CompactNextButton is not null)
        {
            SetAccessibleLabel(CompactNextButton, localization.T("Music.Control.Next"));
        }
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
        if (CollapsedChromeLayer is null)
        {
            return;
        }

        CompactThumbnailHost.CornerRadius = new CornerRadius(_compactMediaCornerRadius);
        CompactTitleIcon.SetSurfaceCornerRadiusOverride(_compactMediaCornerRadius);
        CompactActionRegionHighlight.CornerRadius = new CornerRadius(_compactInnerCornerRadius);

        // Full-bleed layers follow outer radius
        var outerCR = new CornerRadius(_compactOuterCornerRadius);
        CompactFullBleedClip.CornerRadius = outerCR;
        CompactPausedDim.CornerRadius = outerCR;
        CompactEdgeGlow.CornerRadius = outerCR;
        CompactColorField.CornerRadius = outerCR;
        CompactFullBleedOverlay.CornerRadius = outerCR;

        foreach (var button in new Button?[]
        {
            CompactPrimaryActionButton,
            CompactPreviousButton,
            CompactPlayPauseButton,
            CompactNextButton,
            CompactExpandButton
        })
        {
            if (button is not null)
            {
                button.CornerRadius = new CornerRadius(_compactInnerCornerRadius);
            }
        }
    }

    private void SetBackgroundCornerRadius(double radius) =>
        BackgroundPlate.CornerRadius = new CornerRadius(Math.Max(0, radius));

    private void ApplyCompactTextVisibility()
    {
        if (CollapsedChromeLayer is null)
        {
            return;
        }

        CompactSummaryViewport.Visibility = _showCompactSummary
            ? Visibility.Visible
            : Visibility.Collapsed;
        CompactTextSeparator.Visibility = _showCompactSummary && !_usesStackedCompactText
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyCompactLiveState()
    {
        if (CollapsedChromeLayer is null)
        {
            return;
        }

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
                CompactLiveTrack.Background = SharedBrushCache.GetOrCreate(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                CompactLiveTrack.Opacity = 1;
                CompactLiveProgress.Background = SharedBrushCache.GetOrCreate(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
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
                    CompactLiveProgress.Background = SharedBrushCache.GetOrCreate(Windows.UI.Color.FromArgb(0xFF, 0xF9, 0x73, 0x16));
                }
                else
                {
                    // Normal: accent → green based on completion
                    var accent = App.Current.ThemeService?.GetEffectiveAccentColor()
                        ?? AccentColorHelper.DefaultAccentColor;
                    byte r = (byte)(accent.R + (0x22 - accent.R) * value);
                    byte g = (byte)(accent.G + (0xC5 - accent.G) * value);
                    byte b = (byte)(accent.B + (0x5E - accent.B) * value);
                    CompactLiveProgress.Background = SharedBrushCache.GetOrCreate(Windows.UI.Color.FromArgb(0xFF, r, g, b));
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
                CompactLiveTrack.Background = SharedBrushCache.GetOrCreate(Windows.UI.Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
                CompactLiveTrack.Opacity = 1;
                CompactLiveProgress.Background = SharedBrushCache.GetOrCreate(Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
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

            if (_compactLiveTranslationAnimation is null)
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
        if (CollapsedChromeLayer is null)
        {
            return;
        }

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
            CompactMusicProgressFill.Background = SharedBrushCache.GetOrCreate(
                Windows.UI.Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
            CompactMusicProgressTrack.Background = SharedBrushCache.GetOrCreate(
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
        _compactLiveIndeterminateSegment = isFullBleed ? 0.22 : 0.30;

        // Show the segment right away so the bar is visible from the very first
        // frame, even before the timer fires its first tick.
        CompactLiveProgressTransform.ScaleX = _compactLiveIndeterminateSegment;
        CompactLiveProgressTransform.TranslateX = 0;

        if (!_isHostVisualActivityEnabled ||
            !IsLoaded ||
            !_isCollapsed ||
            !CompactAmbientAnimationsEnabled() ||
            !SystemAnimationsEnabled())
        {
            StopCompactLiveIndeterminate();
            CompactLiveProgressTransform.ScaleX = 1;
            CompactLiveProgressTransform.TranslateX = 0;
            CompactLiveProgress.Opacity = isFullBleed ? 0.72 : 0.5;
            return;
        }

        if (_compactLiveTranslationAnimation is not null)
        {
            return;
        }

        double maxTranslate = Math.Max(
            0,
            CompactLiveTrack.ActualWidth * (1 - _compactLiveIndeterminateSegment));
        ElementCompositionPreview.SetIsTranslationEnabled(CompactLiveProgress, true);
        Visual visual = ElementCompositionPreview.GetElementVisual(CompactLiveProgress);
        ScalarKeyFrameAnimation translation = _compositionResources.GetScalar(
            visual.Compositor, WidgetAnimationTemplate.CompactLiveTranslation);
        translation.InsertKeyFrame(0, 0);
        translation.InsertKeyFrame(1, (float)maxTranslate);
        translation.Duration = TimeSpan.FromSeconds(CompactLiveIndeterminateDurationSeconds);
        translation.IterationBehavior = AnimationIterationBehavior.Forever;

        ScalarKeyFrameAnimation opacity = _compositionResources.GetScalar(
            visual.Compositor, WidgetAnimationTemplate.CompactLiveOpacity);
        InsertSineKeyFrames(opacity, midpoint: 0.6f, amplitude: 0.2f);
        opacity.Duration = translation.Duration;
        opacity.IterationBehavior = AnimationIterationBehavior.Forever;

        _compactLiveTranslationAnimation = translation;
        _compactLiveOpacityAnimation = opacity;
        visual.StartAnimation("Translation.X", translation);
        visual.StartAnimation(nameof(Visual.Opacity), opacity);
    }

    private void StopCompactLiveIndeterminate()
    {
        if (_compactLiveTranslationAnimation is null && _compactLiveOpacityAnimation is null)
        {
            return;
        }

        if (CompactLiveProgress is not null)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(CompactLiveProgress);
            visual.StopAnimation("Translation.X");
            visual.StopAnimation(nameof(Visual.Opacity));
        }
        _compactLiveTranslationAnimation = null;
        _compactLiveOpacityAnimation = null;
    }



    private void RestartCompactLiveIndeterminateForTrackSize()
    {
        if (_compactLiveTranslationAnimation is null ||
            _compactPresentation?.IsProgressIndeterminate != true)
        {
            return;
        }

        bool isFullBleed = _compactPresentation.UseFullBleedBackground &&
            _compactPresentation.Thumbnail is not null;
        StopCompactLiveIndeterminate();
        StartCompactLiveIndeterminate(isFullBleed);
    }

    private ScalarKeyFrameAnimation? _compactLiveBreathingAnimation;
    private ScalarKeyFrameAnimation? _compactLiveTranslationAnimation;
    private ScalarKeyFrameAnimation? _compactLiveOpacityAnimation;
    private double _compactLiveIndeterminateSegment;

    private void StartCompactLiveBreathing()
    {
        if (!_isHostVisualActivityEnabled ||
            !IsLoaded ||
            !_isCollapsed ||
            !CompactAmbientAnimationsEnabled() ||
            !SystemAnimationsEnabled())
        {
            StopCompactLiveBreathing();
            return;
        }

        if (_compactLiveBreathingAnimation is not null)
        {
            return;
        }

        _compactLiveBreathingAnimation = StartSineOpacityBreathing(
            CompactLiveProgress,
            baseOpacity: 0.6,
            amplitude: 0.2,
            phaseStepPerTick: 0.06);
    }

    private void StopCompactLiveBreathing()
    {
        if (CompactLiveProgress is null)
        {
            if (_compactLiveBreathingAnimation is not null)
            {
                WidgetCompositionResources.Release(_compactLiveBreathingAnimation);
                _compactLiveBreathingAnimation = null;
            }
            return;
        }

        StopSineOpacityBreathing(
            _compactLiveBreathingAnimation,
            CompactLiveProgress,
            resetOpacity: 0.6,
            out _compactLiveBreathingAnimation);
    }

    private void ApplyFullBleedVisibility(bool visible)
    {
        if (CollapsedChromeLayer is null)
        {
            return;
        }

        double targetBgOpacity = visible ? 1 : 0;
        double targetOverlayOpacity = visible ? ResolveFullBleedOverlayOpacity() : 0;

        if (SystemAnimationsEnabled() &&
            _compactFullBleedVisibilityStoryboard.IsActiveFor(visible))
        {
            return;
        }

        if (!SystemAnimationsEnabled())
        {
            _compactFullBleedVisibilityStoryboard.StopAndClear();
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

        _compactFullBleedVisibilityStoryboard.Begin(storyboard, visible);
    }

    private double ResolveFullBleedOverlayOpacity() => Math.Clamp(
        _compactPresentation?.FullBleedOverlayOpacity ?? 1.0,
        0.0,
        1.0);

    private double ResolveFullBleedBackgroundOpacity() => Math.Clamp(
        _compactPresentation?.FullBleedBackgroundOpacity ?? 1.0,
        0.0,
        1.0);

    // Full-bleed capsule text uses theme-adaptive foreground colors (dark text in
    // light theme, light text in dark theme). Match the readability scrim to the
    // theme so the text always stays legible: dark scrim in dark theme, white
    // scrim in light theme.
    private void ApplyFullBleedOverlayTheme()
    {
        if (CollapsedChromeLayer is null)
        {
            return;
        }

        bool isDark = NeutralInteractionBrush.IsDarkTheme(this);

        byte channel = isDark ? (byte)0x00 : (byte)0xFF;
        bool useUniformOverlay = _compactPresentation?.UseUniformFullBleedOverlay == true;
        CompactFullBleedStop0.Color = Color.FromArgb(
            useUniformOverlay ? (byte)0xFF : (byte)0xD9,
            channel,
            channel,
            channel);
        CompactFullBleedStop1.Color = Color.FromArgb(
            useUniformOverlay ? (byte)0xFF : (byte)0x8C,
            channel,
            channel,
            channel);
        CompactFullBleedStop2.Color = Color.FromArgb(
            useUniformOverlay ? (byte)0xFF : (byte)0x40,
            channel,
            channel,
            channel);

        // Paused dim overlay: darken in dark theme, lighten (brighten) in light theme.
        CompactPausedDim.Background = SharedBrushCache.GetOrCreate(isDark
            ? Color.FromArgb(0x1A, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    }

    // ── Visual Effects ─────────────────────────────────────────

    private void ApplyColorField(WidgetCompactPresentation p)
    {
        if (CollapsedChromeLayer is null)
        {
            return;
        }

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

    private void ApplyCompactForegroundTheme(WidgetCompactPresentation presentation)
    {
        if (CollapsedChromeLayer is null)
        {
            return;
        }

        CollapsedChromeLayer.RequestedTheme = presentation.UseLightForeground switch
        {
            true => ElementTheme.Dark,
            false => ElementTheme.Light,
            null => ElementTheme.Default
        };
    }

    private void ApplyEdgeGlow(WidgetCompactPresentation p)
    {
        if (CollapsedChromeLayer is null)
        {
            return;
        }

        if (p.EdgeGlowColor is not null)
        {
            CompactEdgeGlowBrush.Color = p.EdgeGlowColor.Value;
            CompactEdgeGlow.BorderThickness = new Thickness(1.5);
            CompactEdgeGlow.Opacity = 0.56;
            StartEdgeGlowPulse();
        }
        else
        {
            StopEdgeGlowPulse();
            CompactEdgeGlow.Opacity = 0;
        }
    }

    private ScalarKeyFrameAnimation? _edgeGlowPulseAnimation;

    private void StartEdgeGlowPulse()
    {
        if (!_isHostVisualActivityEnabled ||
            !IsLoaded ||
            !_isCollapsed ||
            !CompactAmbientAnimationsEnabled() ||
            !SystemAnimationsEnabled())
        {
            StopEdgeGlowPulse();
            return;
        }

        if (_edgeGlowPulseAnimation is not null)
        {
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(CompactEdgeGlow);
        ScalarKeyFrameAnimation animation = _compositionResources.GetScalar(
            visual.Compositor, WidgetAnimationTemplate.EdgeGlow);
        InsertSineKeyFrames(animation, midpoint: 0.48f, amplitude: 0.1f);
        animation.Duration = TimeSpan.FromSeconds(EdgeGlowPulseDurationSeconds);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        _edgeGlowPulseAnimation = animation;
        visual.StartAnimation(nameof(Visual.Opacity), animation);
    }

    private void StopEdgeGlowPulse()
    {
        if (_edgeGlowPulseAnimation is null)
        {
            return;
        }

        if (CompactEdgeGlow is not null)
        {
            ElementCompositionPreview
                .GetElementVisual(CompactEdgeGlow)
                .StopAnimation(nameof(Visual.Opacity));
        }
        _edgeGlowPulseAnimation = null;
    }

    private static void InsertSineKeyFrames(
        ScalarKeyFrameAnimation animation,
        float midpoint,
        float amplitude)
    {
        const int sampleCount = 16;
        for (int i = 0; i <= sampleCount; i++)
        {
            float progress = (float)i / sampleCount;
            float value = midpoint + amplitude * MathF.Sin(progress * MathF.PI * 2);
            animation.InsertKeyFrame(progress, value);
        }
    }

    /// <summary>
    /// Compositor-owned replacement for the retired 50 ms UI-thread ambient
    /// timers: each collapsed widget used to wake the dispatcher at 20 Hz
    /// just to oscillate one Opacity sine. The keyframes reproduce the old
    /// base + amplitude * sin curve at eighth-cycle resolution, with the
    /// period derived from the legacy per-tick phase step.
    /// </summary>
    private const int AmbientBreathingTickMilliseconds = 50;
    private const int AmbientBreathingKeyFrameSteps = 8;

    private static ScalarKeyFrameAnimation StartSineOpacityBreathing(
        FrameworkElement element,
        double baseOpacity,
        double amplitude,
        double phaseStepPerTick)
    {
        double periodMilliseconds =
            (2 * Math.PI / phaseStepPerTick) * AmbientBreathingTickMilliseconds;
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(periodMilliseconds);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        for (int step = 0; step <= AmbientBreathingKeyFrameSteps; step++)
        {
            double value = baseOpacity +
                amplitude * Math.Sin(
                    step * 2 * Math.PI / AmbientBreathingKeyFrameSteps);
            animation.InsertKeyFrame(
                (float)step / AmbientBreathingKeyFrameSteps,
                (float)value);
        }

        visual.StartAnimation("Opacity", animation);
        return animation;
    }

    private static void StopSineOpacityBreathing(
        ScalarKeyFrameAnimation? animation,
        FrameworkElement element,
        double resetOpacity,
        out ScalarKeyFrameAnimation? cleared)
    {
        cleared = null;
        if (animation is null)
        {
            return;
        }

        ElementCompositionPreview.GetElementVisual(element)
            .StopAnimation("Opacity");
        WidgetCompositionResources.Release(animation);
        // StopAnimation reverts the visual to its XAML property value; park
        // it at the caller's resting opacity instead.
        element.Opacity = resetOpacity;
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
        if (CollapsedChromeLayer is null || !SystemAnimationsEnabled()) return;
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            From = 0.9,
            To = 0.45,
            Duration = TimeSpan.FromMilliseconds(
                WidgetMotion.FeedbackMilliseconds),
            AutoReverse = true, EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, CompactEdgeGlow);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        _compactEdgeGlowFlashStoryboard.Begin(sb);
    }

    private void TriggerCapsuleBounce()
    {
        if (CollapsedChromeLayer is null || !SystemAnimationsEnabled()) return;
        CompactIdentityPulseTransform.ScaleX = 0.92;
        CompactIdentityPulseTransform.ScaleY = 0.92;
        var animX = new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(
                WidgetMotion.TransitionMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var animY = new DoubleAnimation
        {
            To = 1,
            Duration = TimeSpan.FromMilliseconds(
                WidgetMotion.TransitionMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animX, CompactIdentityPulseTransform);
        Storyboard.SetTargetProperty(animX, "ScaleX");
        Storyboard.SetTarget(animY, CompactIdentityPulseTransform);
        Storyboard.SetTargetProperty(animY, "ScaleY");
        var sb = new Storyboard();
        sb.Children.Add(animX);
        sb.Children.Add(animY);
        _compactUpdateStoryboard.Begin(sb);
    }

    private void AnimateCompactLiveChange()
    {
        if (CollapsedChromeLayer is null || !SystemAnimationsEnabled())
        {
            return;
        }

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
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(
                WidgetMotion.FeedbackMilliseconds)),
            Value = 0.82,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        indicatorAnimation.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(
                WidgetMotion.SpatialMilliseconds)),
            Value = 0.82
        });
        indicatorAnimation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(667)),
            Value = 0,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
        Storyboard.SetTarget(indicatorAnimation, CompactLiveEventIndicator);
        Storyboard.SetTargetProperty(indicatorAnimation, "Opacity");

        var textOpacityAnimation = new DoubleAnimation
        {
            From = 0.72,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(
                WidgetMotion.TransitionMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(textOpacityAnimation, CompactTextHost);
        Storyboard.SetTargetProperty(textOpacityAnimation, "Opacity");

        var textOffsetAnimation = new DoubleAnimation
        {
            From = WidgetMotion.TranslationDistance,
            To = 0,
            Duration = TimeSpan.FromMilliseconds(
                WidgetMotion.TransitionMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(textOffsetAnimation, CompactTextTransform);
        Storyboard.SetTargetProperty(textOffsetAnimation, "X");

        var storyboard = new Storyboard();
        storyboard.Children.Add(indicatorAnimation);
        storyboard.Children.Add(textOpacityAnimation);
        storyboard.Children.Add(textOffsetAnimation);
        _compactLiveStoryboard.Begin(
            storyboard,
            onCompleted: () =>
        {
            CompactLiveEventIndicator.Opacity = 0;
            CompactTextHost.Opacity = 1;
            CompactTextTransform.X = 0;
        });
    }

    private void QueueCompactMarquee(int delayMs = 300)
    {
        _compactMarqueeDelayTimer?.Stop();
        if (!_isHostVisualActivityEnabled ||
            !IsLoaded ||
            !_isCollapsed ||
            CollapsedChromeLayer is null ||
            _compactPresentation?.EnableMarquee != true ||
            ShouldSuspendCompactMarquee() ||
            IsPointerOverCompactActionRegion() ||
            !TextMarqueeAnimationsEnabled() ||
            !SystemAnimationsEnabled())
        {
            return;
        }

        if (_compactMarqueeDelayTimer is null)
        {
            _compactMarqueeDelayTimer = DispatcherQueue.CreateTimer();
            _compactMarqueeDelayTimer.IsRepeating = false;
            _compactMarqueeDelayTimer.Tick += CompactMarqueeDelayTimer_Tick;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }

        _compactMarqueeDelayTimer.Interval = TimeSpan.FromMilliseconds(Math.Max(100, delayMs));
        _compactMarqueeDelayTimer.Start();
    }

    private void CompactMarqueeDelayTimer_Tick(
        DispatcherQueueTimer sender,
        object args)
    {
        sender.Stop();
        StartCompactMarqueeIfNeeded();
    }

    private void StartCompactMarqueeIfNeeded()
    {
        StopCompactMarquee(resetDelayTimer: false);
        if (!_isHostVisualActivityEnabled ||
            !IsLoaded ||
            !_isCollapsed ||
            CollapsedChromeLayer is null ||
            _compactPresentation?.EnableMarquee != true ||
            ShouldSuspendCompactMarquee() ||
            IsPointerOverCompactActionRegion() ||
            !TextMarqueeAnimationsEnabled() ||
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
        // A Grid keeps both copies vertically centered, including while the
        // track scrolls. Canvas children ignore VerticalAlignment.
        marquee.Clone.Margin = new Thickness(marquee.NaturalWidth + CompactMarqueeGap, 0, 0, 0);

        double distance = marquee.NaturalWidth + CompactMarqueeGap;
        // Grid children can never exceed the track's arrange slot: a
        // viewport-sized track would clip the primary to the visible
        // width and collapse the offset clone to zero, so the marquee
        // would only ever show the first part of the text. The track
        // must span both copies; the viewport clip does the cropping.
        marquee.Track.HorizontalAlignment = HorizontalAlignment.Left;
        marquee.Track.Width = distance + marquee.NaturalWidth;
        TimeSpan startDelay = TimeSpan.FromMilliseconds(CompactMarqueeStartDelayMs);
        TimeSpan travelDuration = TimeSpan.FromSeconds(
            distance / CompactMarqueeSpeedPixelsPerSecond);
        TimeSpan cycleDuration = startDelay + travelDuration;

        ElementCompositionPreview.SetIsTranslationEnabled(marquee.Track, true);
        Visual trackVisual = ElementCompositionPreview.GetElementVisual(marquee.Track);
        Compositor compositor = trackVisual.Compositor;
        ScalarKeyFrameAnimation animation = WidgetCompositionResources.Track(
            compositor.CreateScalarKeyFrameAnimation());
        float holdFraction = (float)Math.Clamp(
            startDelay.TotalMilliseconds / cycleDuration.TotalMilliseconds,
            0.01,
            0.95);
        using StepEasingFunction holdStep = compositor.CreateStepEasingFunction();
        holdStep.IsFinalStepSingleFrame = true;
        using LinearEasingFunction linear = compositor.CreateLinearEasingFunction();
        animation.InsertKeyFrame(0f, 0f);
        animation.InsertKeyFrame(holdFraction, 0f, holdStep);
        animation.InsertKeyFrame(1f, (float)-distance, linear);
        animation.Duration = cycleDuration;
        animation.IterationBehavior = AnimationIterationBehavior.Forever;

        _compactMarqueePrimary = marquee.Primary;
        _compactMarqueeClone = marquee.Clone;
        _compactMarqueeTrack = marquee.Track;
        _compactMarqueeViewport = marquee.Viewport;
        _compactMarqueeAnimation = animation;
        trackVisual.StartAnimation("Translation.X", animation);
    }

    private bool ShouldSuspendCompactMarquee() =>
        _compactPresentation is { ShowVinyl: true, IsPlaying: false };

    private (TextBlock Primary, TextBlock Clone, Grid Track, FrameworkElement Viewport, double NaturalWidth)?
        ResolveCompactMarqueeElements()
    {
        double titleWidth = MeasureCompactTextWidth(CompactTitleText);
        if (CanUseCompactMarqueeTarget(CompactTitleViewport, titleWidth))
        {
            return (
                CompactTitleText,
                EnsureCompactMarqueeClone(isTitle: true),
                CompactTitleMarqueeTrack,
                CompactTitleViewport,
                titleWidth);
        }

        if (_showCompactSummary)
        {
            double summaryWidth = MeasureCompactTextWidth(CompactSummaryText);
            if (CanUseCompactMarqueeTarget(CompactSummaryViewport, summaryWidth))
            {
                return (
                    CompactSummaryText,
                    EnsureCompactMarqueeClone(isTitle: false),
                    CompactSummaryMarqueeTrack,
                    CompactSummaryViewport,
                    summaryWidth);
            }
        }

        return null;
    }

    private static bool CanUseCompactMarqueeTarget(FrameworkElement viewport, double naturalWidth) =>
        viewport.Visibility == Visibility.Visible &&
        viewport.Width > 0 &&
        naturalWidth > viewport.Width + CompactMarqueeOverflowTolerance;

    private static double MeasureCompactTextWidth(TextBlock source)
    {
        if (string.IsNullOrEmpty(source.Text))
        {
            return 0;
        }

        double savedWidth = source.Width;
        TextTrimming savedTrimming = source.TextTrimming;
        source.Width = double.NaN;
        source.TextTrimming = TextTrimming.None;
        source.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = source.DesiredSize.Width;
        source.Width = savedWidth;
        source.TextTrimming = savedTrimming;
        if (!double.IsNaN(savedWidth) && savedWidth > 0)
        {
            source.Measure(new Windows.Foundation.Size(savedWidth, double.PositiveInfinity));
        }

        return width;
    }

    private void StopCompactMarquee(bool resetDelayTimer = true)
    {
        if (resetDelayTimer)
        {
            _compactMarqueeDelayTimer?.Stop();
        }
        ScalarKeyFrameAnimation? animation = _compactMarqueeAnimation;
        _compactMarqueeAnimation = null;
        if (_compactMarqueeTrack is not null)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(_compactMarqueeTrack, true);
            Visual trackVisual = ElementCompositionPreview.GetElementVisual(_compactMarqueeTrack);
            trackVisual.StopAnimation("Translation.X");
            trackVisual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        if (animation is not null)
        {
            WidgetCompositionResources.Release(animation);
        }
        ResetCompactMarqueeTarget();
    }

    private void ReleaseCompactMarqueeDelayTimer()
    {
        if (_compactMarqueeDelayTimer is not { } timer)
        {
            return;
        }

        _compactMarqueeDelayTimer = null;
        timer.Stop();
        timer.Tick -= CompactMarqueeDelayTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    private void RestartCompactVisualTimers()
    {
        if (!_isHostVisualActivityEnabled ||
            !IsLoaded ||
            !_isCollapsed ||
            CollapsedChromeLayer is null ||
            _compactPresentation is not { } presentation)
        {
            return;
        }

        ApplyEdgeGlow(presentation);
        ApplyCompactLiveState();
        bool showVinyl = presentation.ShowVinyl && presentation.Thumbnail is not null;
        UpdateCompactVinylRotation(showVinyl && presentation.IsPlaying);
    }

    private void StopCompactVisualTimers()
    {
        StopCompactLiveIndeterminate();
        StopCompactLiveBreathing();
        StopEdgeGlowPulse();
    }

    private void StopCompactVinylRotation()
    {
        if (!_isCompactVinylRotating)
        {
            return;
        }

        _isCompactVinylRotating = false;
        if (_compactVinylRotationStartedTimestamp != 0)
        {
            double elapsedSeconds = System.Diagnostics.Stopwatch
                .GetElapsedTime(_compactVinylRotationStartedTimestamp)
                .TotalSeconds;
            float nextAngle = (_compactVinylBaseAngleDegrees +
                (float)((elapsedSeconds / CompactVinylSecondsPerRevolution) * 360.0)) % 360f;
            _compactVinylBaseAngleDegrees = nextAngle < 0f ? nextAngle + 360f : nextAngle;
            _compactVinylRotationStartedTimestamp = 0;
        }

        if (CompactVinylLabelHost is not null)
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(CompactVinylLabelHost);
            visual.StopAnimation("RotationAngleInDegrees");
            visual.RotationAngleInDegrees = _compactVinylBaseAngleDegrees;
        }
    }

    private void ResetCompactMarqueeTarget()
    {
        if (_compactMarqueeTrack is not null)
        {
            _compactMarqueeTrack.RenderTransform = null;
            _compactMarqueeTrack.ClearValue(WidthProperty);
            _compactMarqueeTrack.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        if (_compactMarqueePrimary is not null && _compactMarqueeViewport is not null)
        {
            _compactMarqueePrimary.Width = Math.Max(1, _compactMarqueeViewport.Width);
            _compactMarqueePrimary.TextTrimming = TextTrimming.CharacterEllipsis;
        }
        if (_compactMarqueeClone is not null)
        {
            _compactMarqueeClone.ClearValue(WidthProperty);
            _compactMarqueeClone.Margin = new Thickness(0);
            _compactMarqueeClone.Visibility = Visibility.Collapsed;
        }

        _compactMarqueePrimary = null;
        _compactMarqueeClone = null;
        _compactMarqueeTrack = null;
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
    /// Applies the resolved title mode consistently to title content and actions.
    /// </summary>
    public void ApplyTitleBarMetrics(WidgetTitleBarMetrics metrics, WidgetChromeMode chromeMode)
    {
        // Apply one resolved mode to the ordinary title, group navigation and
        // every action before laying out the header.
        _titleBarRowHeight = metrics.RowHeight;
        _titleBarPadding = WidgetTitleBarMetricsCalculator.CreateOuterPadding(chromeMode);
        TitleIcon.IconSize = metrics.TitleIconSize;
        TitleText.FontSize = metrics.TitleTextSize;
        _lastGroupTitleIconSize = metrics.TitleIconSize;
        if (GroupTitleSwitcher is not null)
        {
            GroupTitleSwitcher.IconSize = metrics.TitleIconSize;
        }

        WidgetTitleBarMetricsCalculator.ApplyActionButton(PositionLockButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(SizeLockButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(AddButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(MoreButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(CloseButton, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionButton(CollapseButton, metrics);
        WidgetActionIconHelper.ApplyPairSize(PositionLockButtonIcon, PositionLockButtonFilledIcon, metrics);
        WidgetActionIconHelper.ApplyPairSize(SizeLockButtonIcon, SizeLockButtonFilledIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(AddButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(MoreButtonIcon, metrics);
        WidgetTitleBarMetricsCalculator.ApplyActionIcon(CloseButtonIcon, metrics);
        if (CollapseButton.Content is FrameworkElement collapseIcon)
        {
            WidgetTitleBarMetricsCalculator.ApplyActionIcon(collapseIcon, metrics);
        }

        if (ChromeMode != chromeMode)
        {
            ChromeMode = chromeMode;
        }
        else
        {
            ApplyChromeMode();
        }
    }

    public void SetTitleBarRowHeight(GridLength height)
    {
        _titleBarRowHeight = height;
        ApplyChromeMode();
    }

    private double ResolveTitleBarMinimumHeight() =>
        Math.Max(
            WidgetTitleBarMetricsCalculator.MinimumTitleContentHeight +
                _titleBarPadding.Top + _titleBarPadding.Bottom,
            _titleBarRowHeight.GridUnitType == GridUnitType.Pixel
                ? _titleBarRowHeight.Value
                : 0);

    private double ResolveTitleBarLayoutHeight() =>
        Math.Max(ResolveTitleBarMinimumHeight(), Math.Max(0, TitleBarGrid.ActualHeight));

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

    /// <summary>
    /// Raised when the pointer enters the drag bar. The hosting window
    /// verifies the claim against the physical cursor before showing the
    /// arrow (spurious WM_MOUSEMOVE arrive while the widget morphs its
    /// bounds, so event coordinates are not trusted in either direction) and
    /// keeps it armed until the cursor physically leaves the bar's zone.
    /// </summary>
    public event EventHandler? OverlayDragCueArmRequested;

    public bool IsOverlayDragCueArmed => _isPointerOverDragHandle;

    /// <summary>
    /// The window's physical-cursor verdict. This is the only path (short of
    /// collapse/host-hide clears) that arms or restores the cue.
    /// </summary>
    public void SetOverlayDragCueActive(bool active)
    {
        if (active)
        {
            if (!_isPointerOverDragHandle)
            {
                _isPointerOverDragHandle = true;
                App.LogVerbose("[DragCue] Armed.");
                UpdateOverlayDragHandleVisual();
            }

            return;
        }

        if (_isPointerOverDragHandle)
        {
            _isPointerOverDragHandle = false;
            App.LogVerbose("[DragCue] Restored bar.");
            UpdateOverlayDragHandleVisual();
        }
    }

    /// <summary>
    /// Immediate, unconditional clear used by state transitions (collapse,
    /// host hide) where the cue must not linger at all.
    /// </summary>
    private void ForceClearDragCue() => SetOverlayDragCueActive(false);

    private void ShellRoot_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOverShell = false;
        // Deliberately no drag-cue handling here: routed exits are frequently
        // synthetic in this app (layer reasserts, bounds animations) and must
        // not restore the cue — only the physical-cursor poll may.
        HideCompactHint();
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
            shell.UpdateCompactGroupPositionRail(shell._groupPresentation);
        }
    }

    /// <summary>
    /// Clips the oversized panorama image to the plate bounds; a plain grid
    /// does not clip children, so the rectangle geometry does it explicitly.
    /// </summary>
    private void UpdateCustomPanoramaClip()
    {
        if (CustomBackgroundPanoramaHost is null)
        {
            return;
        }

        Windows.Foundation.Size size = CustomBackgroundPanoramaHost.RenderSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            // Not laid out yet (e.g. the host just became visible): the
            // host's SizeChanged re-runs this with real bounds. A zero rect
            // here would permanently clip the image away.
            return;
        }

        var clipRect = new Windows.Foundation.Rect(0, 0, size.Width, size.Height);
        if (CustomBackgroundPanoramaHost.Clip is RectangleGeometry existingClip)
        {
            existingClip.Rect = clipRect;
        }
        else
        {
            CustomBackgroundPanoramaHost.Clip = new RectangleGeometry
            {
                Rect = clipRect
            };
        }
    }

    private static void OnCustomBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is WidgetShell shell)
        {
            shell.UpdateCustomBackgroundVisual();
        }
    }

    private void UpdateCustomBackgroundVisual()
    {
        bool imageActive = CustomBackgroundSource is not null ||
            CustomBackgroundPanoramaSource is not null;

        // Solid per-widget color: parses here so an unparsable stored value
        // self-heals to "no override" instead of throwing in measure passes.
        Color? solidColor = null;
        if (CustomBackgroundColorHex is { } colorHex &&
            AccentColorHelper.TryParseHex(colorHex, out Color parsedColor))
        {
            solidColor = Color.FromArgb(0xFF, parsedColor.R, parsedColor.G, parsedColor.B);
        }

        CustomBackgroundColorLayer.Visibility =
            solidColor is not null && !imageActive
                ? Visibility.Visible
                : Visibility.Collapsed;
        CustomBackgroundColorLayer.Background = solidColor is { } layerColor
            ? SharedBrushCache.GetOrCreate(layerColor)
            : null;

        // Panorama rendering positions an oversized image and clips it to
        // the plate; the plain Image path covers per-widget and unified
        // backgrounds.
        if (CustomBackgroundPanoramaSource is not null)
        {
            EnsureCustomBackgroundPanoramaHost();
            if (CustomBackgroundPanoramaImage is not null && CustomBackgroundPanoramaHost is not null)
            {
                CustomBackgroundPanoramaImage.Source = CustomBackgroundPanoramaSource;
                CustomBackgroundPanoramaHost.Visibility = Visibility.Visible;
                UpdateCustomPanoramaClip();
            }
        }
        else if (CustomBackgroundPanoramaHost is not null)
        {
            if (CustomBackgroundPanoramaImage is not null)
            {
                CustomBackgroundPanoramaImage.Source = null;
            }

            CustomBackgroundPanoramaHost.Visibility = Visibility.Collapsed;
        }
        CustomBackgroundImage.Source = CustomBackgroundPanoramaSource is null
            ? CustomBackgroundSource
            : null;
        CustomBackgroundImage.Stretch =
            Services.WidgetBackgroundCustomization.NormalizeFit(CustomBackgroundFit) ==
                Services.WidgetBackgroundCustomization.FitContain
                ? Stretch.Uniform
                : Stretch.UniformToFill;
        CustomBackgroundImage.Visibility =
            CustomBackgroundSource is not null && CustomBackgroundPanoramaSource is null
                ? Visibility.Visible
                : Visibility.Collapsed;

        // Theme-aware scrim: dark theme dims with black, light theme with
        // white, so foreground text keeps its contrast in both themes. High
        // contrast forces a stronger floor — text contrast wins over the
        // photo there. The scrim tames photos only: it never washes out a
        // solid color the user picked by hand.
        double dim = Math.Clamp(CustomBackgroundDim, 0d, 1d);
        if (WindowsCompatibilityService.IsHighContrast)
        {
            dim = Math.Max(dim, 0.6d);
        }

        CustomBackgroundScrim.Background = SharedBrushCache.GetOrCreate(
            NeutralInteractionBrush.IsDarkTheme(this)
                ? Windows.UI.Color.FromArgb(0xFF, 0x00, 0x00, 0x00)
                : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        CustomBackgroundScrim.Opacity = dim;
        CustomBackgroundScrim.Visibility = imageActive ? Visibility.Visible : Visibility.Collapsed;
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
        bool hasGroupNavigation = _groupPresentation is not null;
        bool isEditingTitle = TitleEditorContent is not null;
        bool hasCustomTitleBar = TitleBarContent is not null && !hasGroupNavigation;
        CustomTitleBarContentPresenter.Visibility = hasCustomTitleBar ? Visibility.Visible : Visibility.Collapsed;
        DefaultTitleBarContentHost.Visibility = hasCustomTitleBar ? Visibility.Collapsed : Visibility.Visible;
        TitleIdentityHost.Visibility = hasGroupNavigation && !isEditingTitle
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (hasGroupNavigation)
        {
            EnsureGroupTitleSwitcher();
        }

        if (GroupTitleSwitcher is not null)
        {
            GroupTitleSwitcher.Visibility = hasGroupNavigation && !isEditingTitle
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void ApplyChromeMode()
    {
        if (ShellRoot.RowDefinitions.Count < 2)
        {
            return;
        }

        if (_isCollapsed && !_isCompactRenderWarmupActive)
        {
            ShellRoot.RowDefinitions[0].MinHeight = 0;
            ShellRoot.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Star);
            ShellRoot.RowDefinitions[1].Height = new GridLength(0);
            TitleBarGrid.Visibility = Visibility.Collapsed;
            HeaderDivider.Visibility = Visibility.Collapsed;
            ShellContentPresenter.Visibility = Visibility.Collapsed;
            if (OverlayChromeLayer is not null)
            {
                OverlayChromeLayer.Visibility = Visibility.Collapsed;
            }
            EnsureCollapsedChromeLayer().Visibility = Visibility.Visible;
            return;
        }

        if (CollapsedChromeLayer is not null && !_isCompactTransitionActive)
        {
            CollapsedChromeLayer.Visibility = Visibility.Collapsed;
        }
        ShellContentPresenter.Visibility = _isCompactDeferredContentPresentationActive
            ? Visibility.Collapsed
            : Visibility.Visible;
        ShellRoot.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Star);
        bool usesOverlay = ChromeMode is WidgetChromeMode.Overlay or WidgetChromeMode.Hidden;
        bool isEditingTitle = TitleEditorContent is not null;

        bool usesGroupTabs = !usesOverlay && !isEditingTitle &&
            _groupPresentation?.NavigationStyle == WidgetGroupNavigationStyles.Tabs;
        double titleContentHeight = ResolveTitleBarMinimumHeight() -
            _titleBarPadding.Top - _titleBarPadding.Bottom;
        if (_groupPresentation is not null)
        {
            EnsureGroupTitleSwitcher();
        }

        if (GroupTitleSwitcher is not null)
        {
            GroupTitleSwitcher.ApplyTitleBarMetrics(titleContentHeight, TitleText.FontSize);
        }

        ShellRoot.RowDefinitions[0].MinHeight = usesOverlay
            ? 0
            : ResolveTitleBarMinimumHeight();
        ShellRoot.RowDefinitions[0].Height = usesOverlay
            ? new GridLength(0)
            : GridLength.Auto;
        BackgroundPlate.Margin = new Thickness(0);
        Grid.SetRow(TitleBarGrid, usesOverlay ? 1 : 0);
        Canvas.SetZIndex(TitleBarGrid, usesOverlay ? 40 : 2);
        Canvas.SetZIndex(ShellContentPresenter, 1);
        TitleBarGrid.HorizontalAlignment = usesOverlay ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        TitleBarGrid.VerticalAlignment = usesOverlay ? VerticalAlignment.Top : VerticalAlignment.Stretch;
        TitleBarGrid.Margin = usesOverlay ? new Thickness(0, -2, 6, 0) : new Thickness(0);
        TitleBarGrid.Padding = usesOverlay
            ? new Thickness(2, 0, 0, 0)
            : usesGroupTabs
                // Native TabView supplies an 8 DIP leading slot. Pull the
                // strip back by the 4 DIP top inset so the first tab's outer
                // edge aligns with the title bar's top spacing.
                ? new Thickness(0, _titleBarPadding.Top, 8, _titleBarPadding.Bottom)
                : _titleBarPadding;
        if (GroupTitleSwitcher is not null)
        {
            GroupTitleSwitcher.SetTabStripMargin(usesGroupTabs
                ? new Thickness(-4, 0, 0, 0)
                : new Thickness(0));
            GroupTitleSwitcher.Margin = usesGroupTabs ? new Thickness(0) : new Thickness(-6, 0, 6, 0);
            GroupTitleSwitcher.VerticalAlignment = VerticalAlignment.Center;
        }
        TitleIdentityHost.MinHeight = titleContentHeight;
        RightActionButtonsHost.MinHeight = usesOverlay ? 0 : titleContentHeight;
        RightActionButtonsHost.Margin = usesGroupTabs ? new Thickness(8, 0, 0, 0) : new Thickness(0);
        RightActionButtonsHost.VerticalAlignment = usesOverlay ? VerticalAlignment.Top : VerticalAlignment.Center;
        RightActionButtons.VerticalAlignment = usesOverlay ? VerticalAlignment.Top : VerticalAlignment.Center;
        TitleBarGrid.Visibility = usesOverlay && !isEditingTitle ? Visibility.Collapsed : Visibility.Visible;

        HeaderDivider.Margin = new Thickness(12, 0, 12, _titleBarPadding.Bottom);
        HeaderDivider.Visibility = usesOverlay || usesGroupTabs ? Visibility.Collapsed : Visibility.Visible;
        TabActionsDivider.Visibility = usesGroupTabs ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(ShellContentPresenter, usesOverlay ? 0 : 1);
        Grid.SetRowSpan(ShellContentPresenter, usesOverlay ? 2 : 1);
        ShellContentPresenter.Margin = new Thickness(0);
        TitleIdentityHost.Visibility = !isEditingTitle &&
                                       (_groupPresentation is not null || usesOverlay)
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (GroupTitleSwitcher is not null)
        {
            GroupTitleSwitcher.Visibility =
                _groupPresentation is not null &&
                !usesOverlay &&
                !isEditingTitle
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        if (usesOverlay)
        {
            EnsureOverlayChromeLayer();
        }

        if (OverlayChromeLayer is not null)
        {
            OverlayChromeLayer.Visibility = usesOverlay && !isEditingTitle ? Visibility.Visible : Visibility.Collapsed;
            OverlayIdentityHost.Visibility = Visibility.Collapsed;
            OverlayDragHandle.Visibility = usesOverlay && !isEditingTitle ? Visibility.Visible : Visibility.Collapsed;
        }

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
        if (_isCompactRenderWarmupActive)
        {
            Grid collapsedLayer = EnsureCollapsedChromeLayer();
            collapsedLayer.Visibility = Visibility.Visible;
            collapsedLayer.Opacity = 1;
            collapsedLayer.IsHitTestVisible = true;
            if (OverlayChromeLayer is not null)
            {
                OverlayChromeLayer.Visibility = Visibility.Collapsed;
            }
            TitleBarGrid.IsHitTestVisible = false;
            ShellContentPresenter.IsHitTestVisible = false;
        }
    }

    private void ApplyActionButtonVisibility()
    {
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

        if (usesOverlay)
        {
            EnsureOverlayChromeLayer();
        }

        if (OverlayChromeLayer is not null)
        {
            OverlayIdentityHost.Opacity = 0;
            OverlayDragHandle.Opacity = showHandle ? 1 : 0;
            OverlayDragHandle.IsHitTestVisible = showHandle;
        }
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
        var transparent = SharedBrushCache.GetOrCreate(Colors.Transparent);
        var background = isOverlay ? CreateOpaqueOverlayButtonBackground() : transparent;
        var border = isOverlay ? CreateOpaqueOverlayButtonBorder() : transparent;
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
        if (CollapsedChromeLayer is null)
        {
            _compactActionVisibilityStoryboard.StopAndClear();
            return;
        }

        bool visible = _isCollapsed &&
            (IsPointerOverCompactActionRegion() || _isCompactKeyboardFocused);
        CompactActionHost.IsHitTestVisible = visible;
        if (animate &&
            SystemAnimationsEnabled() &&
            _compactActionVisibilityStoryboard.IsActiveFor(visible))
        {
            return;
        }

        if (!animate || !SystemAnimationsEnabled())
        {
            _compactActionVisibilityStoryboard.StopAndClear();
            CompactActionHost.Opacity = visible ? 1 : 0;
            CompactActionHostTransform.X =
                visible ? 0 : WidgetMotion.TranslationDistance;
            return;
        }

        var duration = TimeSpan.FromMilliseconds(
            visible
                ? WidgetMotion.TransitionMilliseconds
                : WidgetMotion.FeedbackMilliseconds);
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
            To = visible ? 0 : WidgetMotion.TranslationDistance,
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
        _compactActionVisibilityStoryboard.Begin(storyboard, visible);
    }

    private void UpdateCompactReorderHandleVisual(bool animate = true)
    {
        if (CollapsedChromeLayer is null)
        {
            _compactReorderHandleStoryboard.StopAndClear();
            return;
        }

        bool visible = _isCompactReorderEnabled &&
            _isCollapsed &&
            (_isPointerOverShell || _isDragHandlePressed || _isCompactKeyboardFocused);
        CompactReorderHandle.IsHitTestVisible = _isCompactReorderEnabled && _isCollapsed;
        double targetOpacity = visible ? 1 : 0;

        if (animate &&
            SystemAnimationsEnabled() &&
            _compactReorderHandleStoryboard.IsActiveFor(visible))
        {
            return;
        }

        if (!animate || !SystemAnimationsEnabled())
        {
            _compactReorderHandleStoryboard.StopAndClear();
            CompactReorderHandle.Opacity = targetOpacity;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(
                visible
                    ? WidgetMotion.TransitionMilliseconds
                    : WidgetMotion.FeedbackMilliseconds),
            EasingFunction = new CubicEase
            {
                EasingMode = visible ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, CompactReorderHandle);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        _compactReorderHandleStoryboard.Begin(storyboard, visible);
    }

    private static bool SystemAnimationsEnabled()
    {
        return WindowsCompatibilityService.ShouldAnimate;
    }

    private static bool TextMarqueeAnimationsEnabled()
    {
        return Application.Current is not App app ||
            app.SettingsService is null ||
            PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings)
                .AllowTextMarqueeAnimations;
    }

    private static bool VinylRotationAnimationsEnabled()
    {
        return Application.Current is not App app ||
            app.SettingsService is null ||
            PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings)
                .AllowVinylRotationAnimations;
    }

    private static bool CompactAmbientAnimationsEnabled()
    {
        return Application.Current is not App app ||
            app.SettingsService is null ||
            PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings)
                .AllowCompactAmbientAnimations;
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
        _isCompactMoveHintLatched = true;
        UpdateCompactHint();
        UpdateCompactInteractionRegionHighlights();
        StopCompactMarquee();
        CompactMoveHandlePointerEntered?.Invoke(this, EventArgs.Empty);
    }

    private void CompactIdentityHost_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (!_isPointerOverCompactIdentity)
        {
            return;
        }

        _isPointerOverCompactIdentity = false;
        UpdateCompactHint();
        UpdateCompactInteractionRegionHighlights();
        CompactMoveHandlePointerExited?.Invoke(this, EventArgs.Empty);
        if (_isPointerOverShell)
        {
            QueueCompactMarquee(650);
        }
    }

    private void CompactMoveHitRegion_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        CompactIdentityHost_PointerEntered(sender, e);

    private void CompactMoveHitRegion_PointerExited(object sender, PointerRoutedEventArgs e) =>
        CompactIdentityHost_PointerExited(sender, e);

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
        if (identityWasActive)
        {
            CompactMoveHandlePointerExited?.Invoke(this, EventArgs.Empty);
        }
        if (actionRegionWasActive)
        {
            UpdateCompactActionRegionState();
        }

        _isPointerOverCompactExpansionZone = true;
        _isCompactMoveHintLatched = false;
        UpdateCompactHint();
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
        UpdateCompactHint();
        CompactExpansionPointerExited?.Invoke(this, EventArgs.Empty);
    }

    private void AnimateTextHoverBackground(bool show)
    {
        if (CompactTextHoverBackground is null)
        {
            _compactTextHoverStoryboard.StopAndClear();
            return;
        }

        double targetOpacity = show ? 0.5 : 0;
        if (SystemAnimationsEnabled() &&
            _compactTextHoverStoryboard.IsActiveFor(show))
        {
            return;
        }

        if (!SystemAnimationsEnabled())
        {
            _compactTextHoverStoryboard.StopAndClear();
            CompactTextHoverBackground.Opacity = targetOpacity;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = targetOpacity,
            Duration = TimeSpan.FromMilliseconds(
                WidgetMotion.FeedbackMilliseconds),
            EasingFunction = new CubicEase
            {
                EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, CompactTextHoverBackground);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        _compactTextHoverStoryboard.Begin(storyboard, show);
    }

    private void UpdateCompactInteractionRegionHighlights()
    {
        if (CompactActionRegionHighlight is null)
        {
            _compactActionHighlightStoryboard.StopAndClear();
            return;
        }

        double actionTarget = _isCollapsed && IsPointerOverCompactActionRegion() ? 1 : 0;
        AnimateOpacity(
            CompactActionRegionHighlight,
            actionTarget,
            _compactActionHighlightStoryboard);
    }

    private static void AnimateOpacity(
        UIElement element,
        double target,
        StoryboardSlot storyboardSlot)
    {
        if (Math.Abs(element.Opacity - target) < 0.01) return;
        if (SystemAnimationsEnabled() &&
            storyboardSlot.IsActiveFor(target))
        {
            return;
        }

        if (!SystemAnimationsEnabled())
        {
            storyboardSlot.StopAndClear();
            element.Opacity = target;
            return;
        }
        var sb = new Storyboard();
        var anim = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(
                WidgetMotion.FeedbackMilliseconds),
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(anim, element);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        storyboardSlot.Begin(sb, target);
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

    private void ShellRoot_DragEnter(object sender, DragEventArgs e)
    {
        if (_isShellDragActive)
        {
            return;
        }

        _isShellDragActive = true;
        CompactDragEntered?.Invoke(this, EventArgs.Empty);
    }

    private void ShellRoot_DragLeave(object sender, DragEventArgs e)
    {
        if (IsPointerInsideShell(e))
        {
            return;
        }

        EndShellDragSession(notifyCompact: true);
    }

    private void ShellRoot_Drop(object sender, DragEventArgs e)
    {
        EndShellDragSession(notifyCompact: false);
        CompactDropCompleted?.Invoke(this, EventArgs.Empty);
    }

    private bool IsPointerInsideShell(DragEventArgs e)
    {
        if (ShellRoot.ActualWidth <= 0 || ShellRoot.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Point point = e.GetPosition(ShellRoot);
            return point.X >= 0 &&
                   point.Y >= 0 &&
                   point.X <= ShellRoot.ActualWidth &&
                   point.Y <= ShellRoot.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void EndShellDragSession(bool notifyCompact)
    {
        bool wasActive = _isShellDragActive;
        _isShellDragActive = false;
        if (wasActive && _hostedContent is FileWidgetContentAdapter fileSurface)
        {
            fileSurface.ClearDragSessionVisualState();
        }
        if (notifyCompact && wasActive)
        {
            CompactDragLeft?.Invoke(this, EventArgs.Empty);
        }
    }

    internal bool TryClearStaleShellDragSessionAfterPointerRelease()
    {
        if (!_isShellDragActive || Win32Helper.IsAnyMouseButtonDown())
        {
            return false;
        }

        // Button-up can precede native Drop completion. Rebuilding the file
        // projection here can remove the active target while its last feedback
        // is still Move, authorizing Shell to delete the source shortcut.
        if (_hostedContent is FileWidgetContentAdapter activeFileSurface &&
            activeFileSurface.ShouldDeferReleasedDragSessionRecovery())
        {
            return false;
        }

        // Once the source has completed, recover any remaining visual state.
        // Do not raise CompactDragLeft here: its delayed restore belongs to a
        // real drag leave and can race the hover request repairing this stale
        // session.
        _isShellDragActive = false;
        if (_hostedContent is FileWidgetContentAdapter fileSurface)
        {
            fileSurface.CompleteReleasedDragSession();
        }
        return true;
    }

    internal bool TryEndShellDragSessionAfterNativePointerExit()
    {
        if (!_isShellDragActive)
        {
            return false;
        }

        EndShellDragSession(notifyCompact: true);
        return true;
    }

    private Brush CreateOpaqueOverlayButtonBackground()
    {
        return SharedBrushCache.GetOrCreate(NeutralInteractionBrush.IsDarkTheme(this)
            ? Color.FromArgb(0xFF, 0x2C, 0x2F, 0x36)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    private Brush CreateOpaqueOverlayButtonBorder()
    {
        return SharedBrushCache.GetOrCreate(NeutralInteractionBrush.IsDarkTheme(this)
            ? Color.FromArgb(0x52, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x2E, 0x00, 0x00, 0x00));
    }

    // Element-scoped resolution follows the shell's theme override
    // (NeutralInteractionBrush), not the system theme; the fallback is the
    // Fluent light-theme critical fill.
    private Brush ResolveCriticalBadgeBrush() =>
        NeutralInteractionBrush.ResolveThemedBrush("SystemFillColorCriticalBrush", this) ??
        SharedBrushCache.GetOrCreate(Windows.UI.Color.FromArgb(0xE6, 0xD8, 0x3B, 0x01));

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
        MoreRequested?.Invoke(
            this,
            new WidgetMenuRequestedEventArgs(
                MoreButton,
                ConsumePendingMoreMenuPointerPosition()));
    }

    private void MoreButton_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(MoreButton);
        if ((point.PointerDeviceType != PointerDeviceType.Mouse &&
             point.PointerDeviceType != PointerDeviceType.Touchpad) ||
            !point.Properties.IsLeftButtonPressed)
        {
            ClearPendingMoreMenuPointerPosition();
            return;
        }

        _pendingMoreMenuPointerPosition = new Windows.Foundation.Point(
            point.Position.X + MoreMenuPointerOffsetDips,
            point.Position.Y + MoreMenuPointerOffsetDips);
        _pendingMoreMenuPointerCapturedAt = Environment.TickCount64;
        unchecked
        {
            _moreMenuPointerCaptureVersion++;
        }
    }

    private void MoreButton_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_pendingMoreMenuPointerPosition is null)
        {
            return;
        }

        int captureVersion = _moreMenuPointerCaptureVersion;
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_moreMenuPointerCaptureVersion == captureVersion)
            {
                ClearPendingMoreMenuPointerPosition();
            }
        });
    }

    private void MoreButton_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        ClearPendingMoreMenuPointerPosition();
    }

    private void MoreButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        ClearPendingMoreMenuPointerPosition();
    }

    private Windows.Foundation.Point? ConsumePendingMoreMenuPointerPosition()
    {
        Windows.Foundation.Point? pointerPosition = _pendingMoreMenuPointerPosition;
        long capturedAt = _pendingMoreMenuPointerCapturedAt;
        ClearPendingMoreMenuPointerPosition();

        if (pointerPosition is null ||
            Environment.TickCount64 - capturedAt > MoreMenuPointerMaximumAgeMilliseconds)
        {
            return null;
        }

        return pointerPosition;
    }

    private void ClearPendingMoreMenuPointerPosition()
    {
        _pendingMoreMenuPointerPosition = null;
        _pendingMoreMenuPointerCapturedAt = 0;
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

    private void TitleBarGrid_PointerWheelChanged(
        object sender,
        PointerRoutedEventArgs e)
    {
        GroupTitleSwitcher?.HandleTitleBarPointerWheel(TitleBarGrid, e);
    }

    internal bool IsPointOverGroupTitleBar(
        FrameworkElement coordinateSpace,
        Windows.Foundation.Point point)
    {
        if (_groupPresentation is null ||
            GroupTitleSwitcher is not { Visibility: Visibility.Visible } ||
            TitleBarGrid.ActualWidth <= 0 ||
            TitleBarGrid.ActualHeight <= 0)
        {
            return false;
        }

        try
        {
            Windows.Foundation.Rect bounds = TitleBarGrid
                .TransformToVisual(coordinateSpace)
                .TransformBounds(new Windows.Foundation.Rect(
                    0,
                    0,
                    TitleBarGrid.ActualWidth,
                    TitleBarGrid.ActualHeight));
            return bounds.Contains(point);
        }
        catch
        {
            return false;
        }
    }

    internal bool HandleNativeGroupTitleWheel(int delta)
    {
        return GroupTitleSwitcher?.HandleNativeWheel(delta) == true;
    }

    internal void NotifyGroupMemberInvocationCompleted(
        string widgetId,
        bool succeeded,
        long? tabSelectionRequestVersion = null)
    {
        GroupTitleSwitcher?.NotifyMemberInvocationCompleted(
            widgetId,
            succeeded,
            tabSelectionRequestVersion);
    }

    internal void NotifyGroupInteractionInterrupted()
    {
        GroupTitleSwitcher?.NotifyInteractionInterrupted();
    }

    private void TitleBarGrid_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (CanCollapseFromTitleBarClick &&
            e.GetCurrentPoint(TitleBarGrid).Properties.IsLeftButtonPressed)
        {
            if (_titleBarClickCollapseTimer is { IsRunning: true })
            {
                // A second press inside the double-click window opens the
                // rename editor, not another collapse.
                StopTitleBarClickCollapse();
                _suppressNextTitleBarClickCollapse = true;
            }
            else
            {
                _suppressNextTitleBarClickCollapse = false;
            }

            _isTitleBarPressTrackingClick = true;
            _hasTitleBarPressMoved = false;
            _titleBarPressPoint = e.GetCurrentPoint(TitleBarGrid).Position;
        }
        else
        {
            _isTitleBarPressTrackingClick = false;
        }

        TitlePointerPressed?.Invoke(this, e);
    }

    private void TitleBarGrid_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_isTitleBarPressTrackingClick && !_hasTitleBarPressMoved)
        {
            Windows.Foundation.Point current = e.GetCurrentPoint(TitleBarGrid).Position;
            double deltaX = current.X - _titleBarPressPoint.X;
            double deltaY = current.Y - _titleBarPressPoint.Y;
            _hasTitleBarPressMoved =
                (deltaX * deltaX) + (deltaY * deltaY) >= TitleBarClickDragThresholdSquared;
        }

        TitlePointerMoved?.Invoke(this, e);
    }

    private void TitleBarGrid_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        bool isTitleBarClick = _isTitleBarPressTrackingClick && !_hasTitleBarPressMoved;
        _isTitleBarPressTrackingClick = false;
        TitlePointerReleased?.Invoke(this, e);
        if (isTitleBarClick && CanCollapseFromTitleBarClick)
        {
            RaiseTitleBarClickCollapse(e);
        }
    }

    private void TitleBarGrid_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        // A press whose capture is lost (alt-tab, UAC) is a drag abort, not
        // a click. The deferred collapse timer intentionally keeps running:
        // it is only armed after a completed release.
        _isTitleBarPressTrackingClick = false;
        // When pointer capture is lost mid-drag (e.g., alt-tab, UAC),
        // notify the parent window so it can call EndWindowDragCore.
        TitlePointerReleased?.Invoke(this, e);
    }

    private bool CanCollapseFromTitleBarClick =>
        !_isCollapsed &&
        _isCollapseActionAvailable &&
        !_usesSmartCompactBehavior &&
        // A click that lands outside the rename editor while editing commits
        // the rename; it must not also collapse the widget.
        TitleEditorContent is null;

    private void RaiseTitleBarClickCollapse(PointerRoutedEventArgs e)
    {
        if (_suppressNextTitleBarClickCollapse)
        {
            _suppressNextTitleBarClickCollapse = false;
            return;
        }

        if (e.OriginalSource is DependencyObject source && IsWithin(source, TitleIdentityHost))
        {
            // The identity strip also owns double-click rename. Wait out the
            // system double-click window so the first tap of a rename cannot
            // collapse the widget out from under the second tap.
            uint delayMs = Win32Helper.GetDoubleClickTime();
            if (delayMs == 0)
            {
                delayMs = 500;
            }

            _titleBarClickCollapseTimer ??= DispatcherQueue.CreateTimer();
            _titleBarClickCollapseTimer.Tick -= TitleBarClickCollapseTimer_Tick;
            _titleBarClickCollapseTimer.Tick += TitleBarClickCollapseTimer_Tick;
            _titleBarClickCollapseTimer.Interval = TimeSpan.FromMilliseconds(delayMs);
            _titleBarClickCollapseTimer.Start();
            return;
        }

        CollapseRequested?.Invoke(this, e);
    }

    private void TitleBarClickCollapseTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        StopTitleBarClickCollapse();
        if (CanCollapseFromTitleBarClick)
        {
            CollapseRequested?.Invoke(this, new RoutedEventArgs());
        }
    }

    private void StopTitleBarClickCollapse()
    {
        _titleBarClickCollapseTimer?.Stop();
    }

    private void ReleaseTitleBarClickCollapseTimer()
    {
        if (_titleBarClickCollapseTimer is not { } timer)
        {
            return;
        }

        _titleBarClickCollapseTimer = null;
        timer.Stop();
        timer.Tick -= TitleBarClickCollapseTimer_Tick;
    }

    private void OverlayDragHandle_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_isCollapsed &&
            CompactActionHost is not null &&
            e.OriginalSource is DependencyObject source &&
            IsWithin(source, CompactActionHost))
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
            CompactActionRegionHighlight.Opacity *= 0.5;
            bool pressedMoveHandle = e.OriginalSource is DependencyObject moveSource &&
                (IsWithin(moveSource, CompactMoveHitRegion) ||
                 IsWithin(moveSource, CompactIdentityHost));
            bool pressedReorderHandle = _isCompactReorderEnabled &&
                e.OriginalSource is DependencyObject reorderSource &&
                IsWithin(reorderSource, CompactReorderHandle);
            _isCompactMoveHandlePress = pressedMoveHandle;
            startsWindowDrag = pressedMoveHandle || pressedReorderHandle;
            _pendingDragHandleClickAction = pressedMoveHandle && _groupPresentation is not null
                ? DragHandleClickAction.OpenGroup
                : startsWindowDrag
                    ? DragHandleClickAction.None
                    : DragHandleClickAction.Expand;
        }
        else if (_isCollapseActionAvailable &&
                 !_usesSmartCompactBehavior &&
                 IsOverlayChromeMode)
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
        if (_isCollapsed && ReferenceEquals(sender, CollapsedChromeLayer))
        {
            // CollapsedChromeLayer receives bubbled moves from the whole capsule.
            // Keep this separate from PointerEntered because swapping grouped
            // content can leave WinUI's entry state attached to the outgoing tree.
            CompactPointerMoved?.Invoke(this, EventArgs.Empty);
        }

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
        else if (clickAction == DragHandleClickAction.OpenGroup)
        {
            OpenGroupPicker(CompactIdentityHost);
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
        // A boundary crossing is the only trigger. The window verifies the
        // physical cursor before the arrow shows and restores the flat bar
        // only after the cursor physically leaves; no exit tracking exists on
        // the shell side.
        OverlayDragCueArmRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateOverlayDragHandleVisual(bool animate = true)
    {
        if (OverlayDragGrip is null)
        {
            _overlayHandleVisualStoryboard.StopAndClear();
            return;
        }

        bool showCollapseCue = !_isCollapsed &&
            _isCollapseActionAvailable &&
            _isPointerOverDragHandle &&
            !_isDragHandlePressed;
        const double gripOpacity = 1;
        double leftAngle = showCollapseCue ? -13 : 0;
        double rightAngle = showCollapseCue ? 13 : 0;

        if (SystemAnimationsEnabled() &&
            _overlayHandleVisualStoryboard.IsActiveFor(showCollapseCue))
        {
            return;
        }

        if (!animate || !SystemAnimationsEnabled())
        {
            _overlayHandleVisualStoryboard.StopAndClear();
            OverlayDragGrip.Opacity = gripOpacity;
            OverlayDragGripLeftRotation.Angle = leftAngle;
            OverlayDragGripRightRotation.Angle = rightAngle;
            return;
        }

        var storyboard = new Storyboard();
        AddOpacityAnimation(storyboard, OverlayDragGrip, gripOpacity);
        AddHandleAngleAnimation(
            storyboard,
            OverlayDragGripLeftRotation,
            leftAngle);
        AddHandleAngleAnimation(
            storyboard,
            OverlayDragGripRightRotation,
            rightAngle);
        _overlayHandleVisualStoryboard.Begin(
            storyboard,
            showCollapseCue,
            onCompleted: () =>
            {
                // StoryboardSlot's completion stops the storyboard and
                // detaches its children; on WinUI that releases the held
                // values back to their base (flat bar). The clocks are gone
                // by the time this callback runs, so re-applying the resting
                // values directly makes the morph persist until the cue
                // state actually changes.
                OverlayDragGrip.Opacity = gripOpacity;
                OverlayDragGripLeftRotation.Angle = leftAngle;
                OverlayDragGripRightRotation.Angle = rightAngle;
            });
    }

    private static void AddHandleAngleAnimation(
        Storyboard storyboard,
        RotateTransform target,
        double angle)
    {
        var animation = new DoubleAnimation
        {
            To = angle,
            Duration = TimeSpan.FromMilliseconds(
                WidgetMotion.TransitionMilliseconds),
            EasingFunction = new CubicEase
            {
                EasingMode = EasingMode.EaseOut
            }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, nameof(RotateTransform.Angle));
        storyboard.Children.Add(animation);
    }

    private static void AddOpacityAnimation(
        Storyboard storyboard,
        FrameworkElement target,
        double opacity)
    {
        var animation = new DoubleAnimation
        {
            To = opacity,
            Duration = TimeSpan.FromMilliseconds(
                WidgetMotion.FeedbackMilliseconds),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
    }

    private static void SetProtectedCursor(UIElement element, InputSystemCursorShape shape)
    {
        UiElementCursorHelper.SetCursor(element, shape);
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
