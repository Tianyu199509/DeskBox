using DeskBox.Contracts;
using DeskBox.Controls.WidgetContents;
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
    private const double CompactMarqueeGap = 32;
    private const double CompactMarqueeStartDelayMs = 900;
    private const double CompactMarqueeSpeedPixelsPerSecond = 50;
    private const double CompactMarqueeOverflowTolerance = 4;
    private const double CompactActionTrailingPadding = 2;
    private const double CompactReorderHandleWidth = 18;
    private const double CompactParticleCanvasWidth = 400;
    private const double CompactParticleCanvasHeight = 40;
    internal const double CompactLiveIndeterminateDurationSeconds = 1.0 / 0.875;
    internal const double EdgeGlowPulseDurationSeconds = Math.PI * 2 / 0.8;
    private WidgetCompactTransitionVisualProfile _compactTransitionProfile =
        WidgetCompactTransitionVisualProfile.Resolve(
            SettingsService.WidgetCompactAnimationSmooth,
            SettingsService.DefaultWidgetCompactAnimationDurationMs,
            true);
    private bool _isShellDragActive;
    private bool _isCompactCompositionTransitionActive;
    private double _lastCompactTransitionCornerRadius = double.NaN;
    private bool _isPersistentAttentionActive;
    private Color _persistentAttentionColor = Colors.Transparent;

    public void ShowFeedback(WidgetFeedbackRequest request)
    {
        FeedbackPresenter.IsCompact = _isCollapsed;
        FeedbackPresenter.Show(request);
    }

    public void ClearFeedback(string? deduplicationKey = null) =>
        FeedbackPresenter.Clear(deduplicationKey);

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
    private readonly StoryboardSlot _overlayHandleVisualStoryboard = new();
    private readonly StoryboardSlot _compactLiveStoryboard = new();
    private readonly StoryboardSlot _compactUpdateStoryboard = new();
    private readonly StoryboardSlot _compactReorderHandleStoryboard = new();
    private readonly StoryboardSlot _compactFullBleedVisibilityStoryboard = new();
    private readonly StoryboardSlot _compactEdgeGlowFlashStoryboard = new();
    private readonly StoryboardSlot _compactActionVisibilityStoryboard = new();
    private readonly StoryboardSlot _compactDragGripStoryboard = new();
    private readonly StoryboardSlot _compactTextHoverStoryboard = new();
    private readonly StoryboardSlot _compactIdentityHighlightStoryboard = new();
    private readonly StoryboardSlot _compactActionHighlightStoryboard = new();
    private bool _compactTextViewportUpdateQueued;
    private ScalarKeyFrameAnimation? _groupDropBreathingAnimation;
    private DispatcherQueueTimer? _compactMarqueeDelayTimer;
    private Storyboard? _compactMarqueeStoryboard;
    private TranslateTransform? _compactMarqueeTransform;
    private TranslateTransform? _rightButtonsTransform;
    private TextBlock? _compactMarqueePrimary;
    private TextBlock? _compactMarqueeClone;
    private Canvas? _compactMarqueeCanvas;
    private FrameworkElement? _compactMarqueeViewport;
    private WidgetCompactPresentation? _compactPresentation;
    private WidgetGroupPresentation? _groupPresentation;
    private IWidgetContent? _hostedContent;
    private IWidgetResponsiveLayoutContent? _responsiveLayoutContent;
    private readonly InsetClip _contentTransitionClip;
    private FileSurfaceContent? _transitionOutgoingFileSurface;
    private FileSurfaceContent? _transitionIncomingFileSurface;
    private bool _isContentSnapshotTransitionActive;
    private bool _isResponsiveLayoutTransitionActive;
    private double _responsiveTargetContentWidth;
    private double _responsiveTargetContentHeight;
    private bool _isTransitionContentLayoutFrozen;
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
    private Windows.Foundation.Point? _pendingMoreMenuPointerPosition;
    private long _pendingMoreMenuPointerCapturedAt;
    private int _moreMenuPointerCaptureVersion;
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
        GroupTitleSwitcher.MemberInvoked += (_, e) => GroupMemberInvoked?.Invoke(this, e);
        GroupTitleSwitcher.RemoveMemberRequested += (_, e) => GroupMemberRemoveRequested?.Invoke(this, e);
        GroupTitleSwitcher.DetachMemberRequested += (_, e) => GroupMemberDetachRequested?.Invoke(this, e);
        GroupTitleSwitcher.DetachDragStarted += (_, e) => GroupMemberDetachDragStarted?.Invoke(this, e);
        GroupTitleSwitcher.DetachDragCompleted += (_, e) => GroupMemberDetachDragCompleted?.Invoke(this, e);
        GroupTitleSwitcher.ReorderRequested += (_, e) => GroupMemberReorderRequested?.Invoke(this, e);
        GroupTitleSwitcher.DissolveRequested += (_, _) => GroupDissolveRequested?.Invoke(this, EventArgs.Empty);
        GroupTitleSwitcher.PickerOpened += (_, _) => GroupPickerOpened?.Invoke(this, EventArgs.Empty);
        GroupTitleSwitcher.PickerClosed += (_, _) => GroupPickerClosed?.Invoke(this, EventArgs.Empty);
        CompactTitleIcon.SetCompactPresentationMode(true);
        SetProtectedCursor(CompactIdentityHost, InputSystemCursorShape.SizeAll);
        SetProtectedCursor(CompactReorderHandle, InputSystemCursorShape.SizeAll);
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
        CompactLiveTrack.SizeChanged += (_, _) => RestartCompactLiveIndeterminateForTrackSize();
        Loaded += (_, _) =>
        {
            ApplyChromeMode();
            ApplyCompactAdaptiveLayout();
            ApplyFullBleedOverlayTheme();
            UpdatePersistentAttentionVisual();
            if (_isHostVisualActivityEnabled)
            {
                QueueCompactMarquee();
                RestartCompactVisualTimers();
            }
        };
        ActualThemeChanged += (_, _) => ApplyFullBleedOverlayTheme();
        Unloaded += (_, _) =>
        {
            StopCompactMarquee();
            ReleaseCompactMarqueeDelayTimer();
            StopCompactVisualTimers();
            StopCompactVinylRotation();
            StopTransientCompactStoryboards();
            StopPersistentAttentionAnimation();
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
        UpdatePersistentAttentionVisual();
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
        StopPersistentAttentionAnimation();
    }

    internal void ApplyPerformanceSettings()
    {
        StopCompactMarquee();
        StopCompactVisualTimers();
        StopCompactVinylRotation();
        StopTransientCompactStoryboards();
        UpdatePersistentAttentionVisual();
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
        _compactDragGripStoryboard.StopAndClear();
        _compactTextHoverStoryboard.StopAndClear();
        _compactIdentityHighlightStoryboard.StopAndClear();
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

    /// <summary>
    /// 显示需要用户明确处理的持续提醒。动画可暂停以节省后台资源，
    /// 但提醒状态会保留，并在格子重新显示时继续。
    /// </summary>
    public void SetPersistentAttention(bool active, Color color)
    {
        bool becameActive = active && !_isPersistentAttentionActive;
        _isPersistentAttentionActive = active;
        _persistentAttentionColor = color;
        if (PersistentAttentionBorder.BorderBrush is SolidColorBrush brush)
        {
            brush.Color = color;
        }
        else
        {
            PersistentAttentionBorder.BorderBrush = new SolidColorBrush(color);
        }

        UpdatePersistentAttentionVisual();
        if (becameActive && IsLoaded)
        {
            // Composition 对刚从 Collapsed 实例化的视觉有时不会立即接收
            // 永久动画。边框常驻视觉树，并在下一帧重挂一次动画，确保
            // 番茄钟在已经折叠时完成也能立刻闪烁。
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isPersistentAttentionActive)
                {
                    UpdatePersistentAttentionVisual();
                }
            });
        }
    }

    private void UpdatePersistentAttentionVisual()
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(
            PersistentAttentionBorder);
        visual.StopAnimation(nameof(Visual.Opacity));

        if (!_isPersistentAttentionActive)
        {
            visual.Opacity = 0;
            return;
        }

        if (PersistentAttentionBorder.BorderBrush is SolidColorBrush brush)
        {
            brush.Color = _persistentAttentionColor;
        }
        else
        {
            PersistentAttentionBorder.BorderBrush =
                new SolidColorBrush(_persistentAttentionColor);
        }

        PersistentAttentionBorder.CornerRadius = BackgroundPlate.CornerRadius;
        visual.Opacity = 1;
        if (!_isHostVisualActivityEnabled ||
            !IsLoaded ||
            !WindowsCompatibilityService.ShouldAnimate)
        {
            return;
        }

        ScalarKeyFrameAnimation animation =
            visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(0, 1);
        animation.InsertKeyFrame(0.5f, 0.22f);
        animation.InsertKeyFrame(1, 1);
        animation.Duration = TimeSpan.FromMilliseconds(820);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation(nameof(Visual.Opacity), animation);
    }

    private void StopPersistentAttentionAnimation()
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(
            PersistentAttentionBorder);
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.Opacity = _isPersistentAttentionActive ? 1 : 0;
    }

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
    public FrameworkElement GroupNavigationElement => GroupTitleSwitcher;

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
            CompactIdentityRegionHighlight.Opacity <= 0.001 &&
            CompactActionRegionHighlight.Opacity <= 0.001 &&
            CompactTextHoverBackground.Opacity <= 0.001 &&
            CompactDragGripIndicator.Opacity <= 0.001)
        {
            return;
        }

        bool shellWasActive = _isPointerOverShell;
        _isPointerOverShell = false;
        _isPointerOverDragHandle = false;
        _isCompactKeyboardFocused = false;
        ResetCompactInteractionRegions();

        // Detach any active hover clocks before assigning the resting values.
        // Direct property writes avoid creating a zero-duration storyboard for
        // every native hide/show cycle.
        _compactIdentityHighlightStoryboard.StopAndClear();
        _compactActionHighlightStoryboard.StopAndClear();
        _compactTextHoverStoryboard.StopAndClear();
        _compactDragGripStoryboard.StopAndClear();
        CompactIdentityRegionHighlight.Opacity = 0;
        CompactActionRegionHighlight.Opacity = 0;
        CompactTextHoverBackground.Opacity = 0;
        CompactDragGripIndicator.Opacity = 0;
        CompactReorderGlyph.Opacity = 0.58;
        ApplyCompactActionVisibility(animate: false);
        UpdateCompactReorderHandleVisual(animate: false);
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
                return CompactIdentityHost.Visibility == Visibility.Visible &&
                       CompactIdentityHost.ActualWidth > 0 &&
                       CompactIdentityHost.ActualHeight > 0
                    ? CompactIdentityHost
                    : null;
            }

            if (ChromeMode == WidgetChromeMode.Overlay)
            {
                return OverlayDragHandle.Visibility == Visibility.Visible &&
                       OverlayDragHandle.Opacity > 0.01 &&
                       OverlayDragHandle.ActualWidth > 0 &&
                       OverlayDragHandle.ActualHeight > 0
                    ? OverlayDragHandle
                    : null;
            }

            return TitleBarGrid.Visibility == Visibility.Visible &&
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
        _isCompactTransitionActive ||
        _isContentSnapshotTransitionActive ||
        _isResponsiveLayoutTransitionActive ||
        OutgoingContentPresenter.Content is not null ||
        _groupDropBreathingAnimation is not null;

    public bool HasWidgetGroup => _groupPresentation is not null;

    public void SetGroupPresentation(
        WidgetGroupPresentation? presentation,
        bool animateIdentity = false,
        WidgetGroupSwitchOrigin origin = WidgetGroupSwitchOrigin.Programmatic,
        bool forward = true)
    {
        _groupPresentation = presentation;
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
        UpdateCompactGroupPositionRail(presentation);
        ApplyCompactAdaptiveLayout();
        UpdateTitleBarContentVisibility();
        ApplyChromeMode();
    }

    private void UpdateCompactGroupPositionRail(
        WidgetGroupPresentation? presentation)
    {
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
        var accentBrush = SharedBrushCache.GetOrCreate(TitleIconAccentColor);
        foreach (WidgetGroupPositionRailSlot slot in slots)
        {
            bool active = slot.IsActive;
            CompactGroupPositionRail.Children.Add(new Border
            {
                Width = 3,
                Height = active ? 7 : 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = accentBrush,
                CornerRadius = new CornerRadius(1.5),
                IsHitTestVisible = false,
                Opacity = active ? 0.94 : 0.3
            });
        }

        // The position rail already communicates that this is the group move
        // region, so do not stack the generic six-dot drag affordance on it.
        CompactDragGripIndicator.Opacity = 0;
    }

    internal bool TryHandleGroupKeyboardNavigation(KeyRoutedEventArgs e)
    {
        return GroupTitleSwitcher.TryHandleKeyboardNavigation(e);
    }

    public void OpenGroupPicker(FrameworkElement? anchor = null)
    {
        if (_groupPresentation is null)
        {
            return;
        }

        GroupTitleSwitcher.OpenPicker(anchor ?? GroupTitleSwitcher);
    }

    public void SetGroupMemberLoading(string? widgetId, bool isLoading)
    {
        GroupTitleSwitcher.SetMemberLoading(widgetId, isLoading);
    }

    public void SetGroupDropPreview(
        bool visible,
        bool ready,
        string? messageKey = null)
    {
        StopGroupDropPreviewBreathing();
        if (!visible || !TryPlaceGroupDropPreviewOverTitle())
        {
            GroupDropPreview.Visibility = Visibility.Collapsed;
            GroupDropPreview.Opacity = 0;
            GroupDropPreviewTransform.Y = 0;
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
        Color accent =
            App.Current?.ThemeService?.GetEffectiveAccentColor() ??
            AccentColorHelper.DefaultAccentColor;
        byte borderAlpha = ready ? (byte)0xF0 : blocked ? (byte)0xA8 : (byte)0xD0;
        GroupDropPreview.Background = SharedBrushCache.GetOrCreate(Colors.Transparent);
        GroupDropPreview.BorderBrush = SharedBrushCache.GetOrCreate(Color.FromArgb(
            borderAlpha,
            accent.R,
            accent.G,
            accent.B));
        GroupDropPreviewIcon.Foreground = SharedBrushCache.GetOrCreate(Color.FromArgb(
            0xFF,
            accent.R,
            accent.G,
            accent.B));

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
            visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(1500);
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        CubicBezierEasingFunction easing =
            visual.Compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.42f, 0),
                new Vector2(0.58f, 1));
        animation.InsertKeyFrame(0, ready ? 0.42f : 0.3f);
        animation.InsertKeyFrame(0.5f, ready ? 1 : 0.78f, easing);
        animation.InsertKeyFrame(1, ready ? 0.42f : 0.3f, easing);
        _groupDropBreathingAnimation = animation;
        visual.StartAnimation("Opacity", animation);
    }

    private void StopGroupDropPreviewBreathing()
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(
            GroupDropPreview);
        visual.StopAnimation("Opacity");
        visual.Opacity = 0;
        _groupDropBreathingAnimation = null;
    }

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
        DetachHostedContentEvents();
        _hostedContent = content;
        AttachHostedContentEvents();
        _responsiveLayoutContent = content as IWidgetResponsiveLayoutContent;
        ShellContent = content.View;
        content.OnCompactStateChanged(_isCollapsed);
        NotifyHostedContentViewportSize(
            ContentTransitionViewport.ActualWidth,
            ContentTransitionViewport.ActualHeight);
        DispatcherQueue.TryEnqueue(() => NotifyHostedContentViewportSize(
            ContentTransitionViewport.ActualWidth,
            ContentTransitionViewport.ActualHeight));
        HostedContentChanged?.Invoke(this, EventArgs.Empty);
    }

    internal bool HasPresentableContentFrame =>
        (ShellContent is not null &&
         ShellContentPresenter.Visibility == Visibility.Visible &&
         ShellContentPresenter.Opacity > 0) ||
        (OutgoingContentPresenter.Content is not null &&
         OutgoingContentPresenter.Visibility == Visibility.Visible &&
         OutgoingContentPresenter.Opacity > 0);

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

        SuspendFileSurfaceItemTransitions(outgoingContent, incomingContent);
        EnsureContentTransitionViewportClip();
        EndInteractiveNeighborPreview();
        ShellContent = null;
        OutgoingContentPresenter.Content = outgoingContent.View;
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
        _transitionOutgoingFileSurface = outgoingContent as FileSurfaceContent;
        _transitionIncomingFileSurface = incomingContent as FileSurfaceContent;

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
        FileSurfaceContent? outgoing = _transitionOutgoingFileSurface;
        FileSurfaceContent? incoming = _transitionIncomingFileSurface;
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

        WidgetContentTransitionProfile profile =
            WidgetContentTransitionProfile.Create(
                animationsEnabled,
                directional);
        if (profile.DurationMilliseconds <= 0)
        {
            OutgoingContentPresenter.Opacity = 0;
            ShellContentPresenter.Opacity = 1;
            ShellContentPresenter.IsHitTestVisible = true;
            return Task.CompletedTask;
        }

        double distance = profile.TranslationDistance;
        double sign = forward ? 1 : -1;
        var incomingTransform = new CompositeTransform
        {
            ScaleX = profile.MinimumScale,
            ScaleY = profile.MinimumScale,
            TranslateY = profile.UsesMotion
                ? distance * sign
                : 0
        };
        var outgoingTransform = new CompositeTransform();
        ShellContentPresenter.RenderTransformOrigin =
            new Windows.Foundation.Point(0.5, 0.5);
        OutgoingContentPresenter.RenderTransformOrigin =
            new Windows.Foundation.Point(0.5, 0.5);
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
                nameof(CompositeTransform.TranslateY),
                -distance * sign,
                outgoingDurationMs,
                easingMode: EasingMode.EaseIn);
            AddTransitionAnimation(
                storyboard,
                incomingTransform,
                nameof(CompositeTransform.TranslateY),
                0,
                incomingDurationMs,
                incomingBeginTimeMs);
        }
        AddTransitionAnimation(
            storyboard,
            outgoingTransform,
            nameof(CompositeTransform.ScaleX),
            profile.MinimumScale,
            outgoingDurationMs,
            easingMode: EasingMode.EaseIn);
        AddTransitionAnimation(
            storyboard,
            outgoingTransform,
            nameof(CompositeTransform.ScaleY),
            profile.MinimumScale,
            outgoingDurationMs,
            easingMode: EasingMode.EaseIn);
        AddTransitionAnimation(
            storyboard,
            incomingTransform,
            nameof(CompositeTransform.ScaleX),
            1,
            incomingDurationMs,
            incomingBeginTimeMs);
        AddTransitionAnimation(
            storyboard,
            incomingTransform,
            nameof(CompositeTransform.ScaleY),
            1,
            incomingDurationMs,
            incomingBeginTimeMs);
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
        ShellContentPresenter.RenderTransformOrigin =
            new Windows.Foundation.Point(0, 0);
        OutgoingContentPresenter.RenderTransformOrigin =
            new Windows.Foundation.Point(0, 0);
    }

    private void ContentTransitionViewport_SizeChanged(
        object sender,
        SizeChangedEventArgs e)
    {
        EnsureContentTransitionViewportClip();

        if (!_isResponsiveLayoutTransitionActive)
        {
            NotifyHostedContentViewportSize(e.NewSize.Width, e.NewSize.Height);
        }
    }

    private void EnsureContentTransitionViewportClip()
    {
        Visual contentTransitionVisual =
            ElementCompositionPreview.GetElementVisual(
                ContentTransitionViewport);
        if (!ReferenceEquals(
                contentTransitionVisual.Clip,
                _contentTransitionClip))
        {
            contentTransitionVisual.Clip = _contentTransitionClip;
        }
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

    public void ClearContent()
    {
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
        if (_hostedContent is FileSurfaceContent fileSurface)
        {
            fileSurface.ImportBusyChanged += HostedFileSurface_ImportBusyChanged;
            TitleBarGrid.IsHitTestVisible = !fileSurface.IsImportBusy;
        }
    }

    private void DetachHostedContentEvents()
    {
        if (_hostedContent is FileSurfaceContent fileSurface)
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
        double? frozenWindowHeight = null)
    {
        double titleHeight = IsOverlayChromeMode
            ? 0
            : ResolveTitleBarLayoutHeight();
        _responsiveTargetContentWidth = Math.Max(0, targetWindowWidth);
        _responsiveTargetContentHeight = Math.Max(0, targetWindowHeight - titleHeight);
        _isResponsiveLayoutTransitionActive = true;
        FreezeTransitionContentLayout(
            Math.Max(0, frozenWindowWidth ?? targetWindowWidth),
            Math.Max(0, (frozenWindowHeight ?? targetWindowHeight) - titleHeight),
            expansionAnchor);
        _responsiveLayoutContent?.BeginResponsiveLayoutTransition(
                _responsiveTargetContentWidth,
                _responsiveTargetContentHeight,
                isCollapsing);
    }

    public void CompleteResponsiveLayoutTransition()
    {
        if (!_isResponsiveLayoutTransitionActive)
        {
            return;
        }

        RestoreTransitionContentLayout();
        _responsiveLayoutContent?.CompleteResponsiveLayoutTransition(
                _responsiveTargetContentWidth,
                _responsiveTargetContentHeight);
        NotifyHostedContentViewportSize(
            _responsiveTargetContentWidth,
            _responsiveTargetContentHeight);
        _isResponsiveLayoutTransitionActive = false;
    }

    public void CancelResponsiveLayoutTransition()
    {
        if (!_isResponsiveLayoutTransitionActive)
        {
            return;
        }

        RestoreTransitionContentLayout();
        _responsiveLayoutContent?.CancelResponsiveLayoutTransition();
        _isResponsiveLayoutTransitionActive = false;
    }

    private void FreezeTransitionContentLayout(
        double contentWidth,
        double contentHeight,
        WidgetCompactExpansionAnchor expansionAnchor)
    {
        if (contentWidth <= 0 || contentHeight <= 0)
        {
            return;
        }

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

    private void RestoreTransitionContentLayout()
    {
        if (!_isTransitionContentLayoutFrozen)
        {
            return;
        }

        ShellContentPresenter.Width = double.NaN;
        ShellContentPresenter.Height = double.NaN;
        ShellContentPresenter.HorizontalAlignment = HorizontalAlignment.Stretch;
        ShellContentPresenter.VerticalAlignment = VerticalAlignment.Stretch;
        _isTransitionContentLayoutFrozen = false;
    }

    public void SetCollapsed(bool collapsed, string contentMode)
    {
        ResetCompactTransitionVisuals();
        StopTransientCompactStoryboards();
        bool stateChanged = _isCollapsed != collapsed;
        _isCollapsed = collapsed;
        _hostedContent?.OnCompactStateChanged(collapsed);
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
            RestartCompactVisualTimers();
        }
        else
        {
            StopCompactMarquee();
            StopCompactVisualTimers();
            StopCompactVinylRotation();
        }
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
                compactMediaRadius);
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
        WidgetCompactTransitionVisualProfile? visualProfile = null)
    {
        if (_isCollapsed == collapsed)
        {
            return false;
        }

        _expandedOuterCornerRadius = Math.Max(0, expandedOuterRadius);
        _compactOuterCornerRadius = Math.Max(0, compactOuterRadius);
        _compactInnerCornerRadius = Math.Max(0, compactInnerRadius);
        _compactMediaCornerRadius = Math.Max(0, compactMediaRadius);
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
            _isCollapsed = false;
            ApplyChromeMode();
        }

        CollapsedChromeLayer.Visibility = Visibility.Visible;
        CollapsedChromeLayer.IsHitTestVisible = false;
        CollapsedChromeLayer.Opacity = 1;
        // Keep the real expanded tree visible while the HWND grows. The compact
        // layer sits above it and fades away, so newly revealed pixels already
        // contain live content instead of an empty placeholder that only appears
        // when the transition completes.
        TitleBarGrid.Opacity = 1;
        ShellContentPresenter.Opacity = 1;
        CompactIdentityHost.Opacity = collapsed ? 0 : 1;
        CompactTextContainer.Opacity = collapsed ? 0 : 1;
        CompactBadge.Opacity = collapsed ? 0 : 1;
        CompactLiveIndicatorHost.Opacity = collapsed ? 0 : 1;
        ElementCompositionPreview.SetIsTranslationEnabled(TitleBarGrid, true);
        ElementCompositionPreview.SetIsTranslationEnabled(ShellContentPresenter, true);
        ElementCompositionPreview.SetIsTranslationEnabled(CompactTextContainer, true);
        TitleBarGrid.Translation = Vector3.Zero;
        ShellContentPresenter.Translation = Vector3.Zero;
        CompactTextContainer.Translation = collapsed
            ? new Vector3(0, 3, 0)
            : Vector3.Zero;
        ApplyCompactInnerCornerRadii();
        SetBackgroundCornerRadius(_transitionOuterCornerRadiusFrom);
        _lastCompactTransitionCornerRadius = _transitionOuterCornerRadiusFrom;
        _isCompactCompositionTransitionActive =
            StartCompactCompositionTransition(collapsed);
        return true;
    }

    public void SetCompactTransitionProgress(bool collapsed, double progress)
    {
        if (!_isCompactTransitionActive)
        {
            return;
        }

        double value = Math.Clamp(progress, 0, 1);
        bool compositionOwnsWin10Visuals =
            _isCompactCompositionTransitionActive &&
            !WindowsCompatibilityService.IsWindows11OrLater;
        if (!compositionOwnsWin10Visuals)
        {
            double compactOpacity =
                _compactTransitionProfile.GetCompactSurfaceOpacity(collapsed, value);
            CollapsedChromeLayer.Opacity = compactOpacity;

            // Expansion reveals the already-laid-out live tree as the physical
            // window grows. Collapse may still fade that tree beneath the incoming
            // compact layer, but the expand path must never hold it at opacity zero.
            double liveContentOpacity =
                _compactTransitionProfile.GetLiveContentOpacity(collapsed, value);
            TitleBarGrid.Opacity = liveContentOpacity;
            ShellContentPresenter.Opacity = liveContentOpacity;
            float liveContentTranslationY = (float)
                _compactTransitionProfile.GetLiveContentTranslationY(collapsed, value);
            TitleBarGrid.Translation = new Vector3(0, liveContentTranslationY, 0);
            ShellContentPresenter.Translation = new Vector3(0, liveContentTranslationY, 0);

            double compactIdentityOpacity =
                _compactTransitionProfile.GetCompactIdentityOpacity(collapsed, value);
            double compactTextOpacity =
                _compactTransitionProfile.GetCompactTextOpacity(collapsed, value);
            CompactIdentityHost.Opacity = compactIdentityOpacity;
            CompactTextContainer.Opacity = compactTextOpacity;
            CompactBadge.Opacity = compactTextOpacity;
            CompactLiveIndicatorHost.Opacity = compactTextOpacity;
            CompactTextContainer.Translation = new Vector3(
                0,
                (float)(3 * (1 - compactTextOpacity)),
                0);

            double cornerRadius = Lerp(
                _transitionOuterCornerRadiusFrom,
                _transitionOuterCornerRadiusTo,
                value);
            if (double.IsNaN(_lastCompactTransitionCornerRadius) ||
                Math.Abs(cornerRadius - _lastCompactTransitionCornerRadius) >= 0.75 ||
                value >= 1)
            {
                SetBackgroundCornerRadius(cornerRadius);
                _lastCompactTransitionCornerRadius = cornerRadius;
            }
        }

        // Full-bleed background: fade out earlier during expand, fade in later during collapse
        bool hasFullBleed = _compactPresentation?.UseFullBleedBackground == true &&
            _compactPresentation.Thumbnail is not null;
        if (hasFullBleed && !compositionOwnsWin10Visuals)
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

        try
        {
            bool started = false;
            if (!WindowsCompatibilityService.IsWindows11OrLater)
            {
                // Win10 pays heavily when XAML dependency properties are
                // rewritten alongside a real HWND resize. Keep the physical
                // bounds transition on the UI thread, while independent fades
                // run on the compositor clock.
                StartCompactOpacityAnimation(
                    CollapsedChromeLayer,
                    progress => _compactTransitionProfile.GetCompactSurfaceOpacity(collapsed, progress));
                StartCompactOpacityAnimation(
                    TitleBarGrid,
                    progress => _compactTransitionProfile.GetLiveContentOpacity(collapsed, progress));
                StartCompactOpacityAnimation(
                    ShellContentPresenter,
                    progress => _compactTransitionProfile.GetLiveContentOpacity(collapsed, progress));
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
                started = true;
            }

            bool hasFullBleed = _compactPresentation?.UseFullBleedBackground == true &&
                _compactPresentation.Thumbnail is not null;
            if (hasFullBleed)
            {
                StartCompactScaleAnimation(
                    CompactFullBleedBackground,
                    progress => ResolveFullBleedTransition(collapsed, progress).Scale);
                if (!WindowsCompatibilityService.IsWindows11OrLater)
                {
                    StartCompactOpacityAnimation(
                        CompactFullBleedBackground,
                        progress => ResolveFullBleedTransition(collapsed, progress).Opacity);
                    StartCompactOpacityAnimation(
                        CompactFullBleedOverlay,
                        progress => ResolveFullBleedTransition(collapsed, progress).Opacity *
                            ResolveFullBleedOverlayOpacity());
                }

                started = true;
            }

            return started;
        }
        catch (Exception ex)
        {
            StopCompactCompositionTransitionAnimations(force: true);
            App.LogVerbose($"[Compact] Composition visual fallback: {ex.Message}");
            return false;
        }
    }

    private void StartCompactOpacityAnimation(
        FrameworkElement element,
        Func<double, double> valueSelector)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        ScalarKeyFrameAnimation animation = visual.Compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(
            _compactTransitionProfile.DurationMilliseconds);
        const int sampleCount = 12;
        for (int step = 0; step <= sampleCount; step++)
        {
            double timeProgress = step / (double)sampleCount;
            double easedProgress = _compactTransitionProfile.EaseProgress(timeProgress);
            animation.InsertKeyFrame(
                (float)timeProgress,
                (float)Math.Clamp(valueSelector(easedProgress), 0, 1));
        }

        visual.StartAnimation("Opacity", animation);
    }

    private void StartCompactScaleAnimation(
        FrameworkElement element,
        Func<double, double> valueSelector)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3(visual.Size.X / 2, visual.Size.Y / 2, 0);
        Vector3KeyFrameAnimation animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.Duration = TimeSpan.FromMilliseconds(
            _compactTransitionProfile.DurationMilliseconds);
        const int sampleCount = 24;
        for (int step = 0; step <= sampleCount; step++)
        {
            double timeProgress = step / (double)sampleCount;
            double easedProgress = _compactTransitionProfile.EaseProgress(timeProgress);
            float scale = (float)valueSelector(easedProgress);
            animation.InsertKeyFrame((float)timeProgress, new Vector3(scale, scale, 1));
        }

        visual.StartAnimation("Scale", animation);
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

        foreach (FrameworkElement element in new FrameworkElement[]
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
            Visual visual = ElementCompositionPreview.GetElementVisual(element);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
            visual.Scale = Vector3.One;
        }

        _isCompactCompositionTransitionActive = false;
    }

    public void CompleteCompactTransition(bool collapsed, string contentMode)
    {
        _isCompactTransitionActive = false;
        SetBackgroundCornerRadius(collapsed
            ? _compactOuterCornerRadius
            : _expandedOuterCornerRadius);
        // SetCollapsed performs the single final visual reset. Calling it both
        // here and there repeatedly resolves/stops the same Composition visuals
        // at the end of every capsule transition.
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
        StopCompactCompositionTransitionAnimations();
        _lastCompactTransitionCornerRadius = double.NaN;
        TitleBarGrid.Opacity = 1;
        ShellContentPresenter.Opacity = 1;
        CollapsedChromeLayer.Opacity = 1;
        CompactIdentityHost.Opacity = 1;
        CompactTextContainer.Opacity = 1;
        CompactBadge.Opacity = 1;
        CompactLiveIndicatorHost.Opacity = 1;
        TitleBarGrid.Translation = Vector3.Zero;
        ShellContentPresenter.Translation = Vector3.Zero;
        CompactTextContainer.Translation = Vector3.Zero;
        CollapsedChromeLayer.IsHitTestVisible = true;
        FullBleedScaleTransform.ScaleX = 1;
        FullBleedScaleTransform.ScaleY = 1;
        TitleIdentityHost.Opacity = 1;
        GroupTitleSwitcher.Opacity = 1;
    }

    public void SetCompactPresentation(WidgetCompactPresentation presentation)
    {
        WidgetCompactPresentation? previous = _compactPresentation;
        if (previous == presentation)
        {
            return;
        }

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

            WidgetCompactPresentation previousWithCurrentLiveText = previous with
            {
                Title = presentation.Title,
                Summary = presentation.Summary,
                Progress = presentation.Progress,
                IsProgressIndeterminate = presentation.IsProgressIndeterminate,
                MusicProgress = presentation.MusicProgress
            };
            if (previous.IsLiveTextUpdate &&
                presentation.IsLiveTextUpdate &&
                string.Equals(
                    previous.LiveStateKey,
                    presentation.LiveStateKey,
                    StringComparison.Ordinal) &&
                previousWithCurrentLiveText == presentation)
            {
                // 倒计时每秒变化时仅替换文本与进度。完整的呈现刷新会重置
                // marquee、动画和操作区，导致文字左右移动，甚至让悬停按钮闪退。
                _compactPresentation = presentation;
                CompactTitleText.Text = presentation.Title;
                CompactTitleMarqueeClone.Text = presentation.Title;
                CompactSummaryText.Text = presentation.Summary;
                CompactSummaryMarqueeClone.Text = presentation.Summary;
                CompactTitleIcon.LabelText = presentation.Title;
                ApplyCompactLiveState();
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
            !string.IsNullOrWhiteSpace(previous.EmojiIcon) != !string.IsNullOrWhiteSpace(presentation.EmojiIcon) ||
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
        CompactTitleMarqueeClone.Text = presentation.Title;
        CompactSummaryText.Text = presentation.Summary;
        CompactSummaryMarqueeClone.Text = presentation.Summary;
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
        CompactPlayPauseIcon.Kind = presentation.IsPlaying
            ? MusicTransportIconKind.Pause
            : MusicTransportIconKind.Play;
        ApplyCompactActionLabels(presentation);
        UpdateCompactVinylRotation(showVinyl && presentation.IsPlaying);
        ApplyCompactForegroundTheme(presentation);

        // Badge
        bool hasBadge = !string.IsNullOrWhiteSpace(presentation.BadgeText);
        CompactBadge.Visibility = hasBadge ? Visibility.Visible : Visibility.Collapsed;
        if (hasBadge)
        {
            CompactBadgeText.Text = presentation.BadgeText;
            CompactBadge.Background = presentation.BadgeIsWarning
                ? new SolidColorBrush(Windows.UI.Color.FromArgb(0xE6, 0xD8, 0x3B, 0x01))
                : new SolidColorBrush(
                    App.Current.ThemeService?.GetEffectiveAccentColor() ??
                    AccentColorHelper.DefaultAccentColor);
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

        if (textChanged || musicPlaybackChanged)
        {
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
        bool shouldRotate = _isHostVisualActivityEnabled &&
            IsLoaded &&
            _isCollapsed &&
            CompactVinylHost.Visibility == Visibility.Visible &&
            isPlaying &&
            VinylRotationAnimationsEnabled() &&
            SystemAnimationsEnabled();
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

    private void ApplyCompactActionLabels(WidgetCompactPresentation presentation)
    {
        var localization = App.Current.LocalizationService;
        string primaryActionLabel = string.IsNullOrWhiteSpace(presentation.PrimaryActionLabel)
            ? localization.T("Todo.Menu.MarkCompleted")
            : presentation.PrimaryActionLabel;
        SetAccessibleLabel(CompactPrimaryActionButton, primaryActionLabel);
        SetAccessibleLabel(CompactPreviousButton, localization.T("Music.Control.Previous"));
        SetAccessibleLabel(
            CompactPlayPauseButton,
            localization.T(presentation.IsPlaying ? "Music.Control.Pause" : "Music.Control.Play"));
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

    private void SetBackgroundCornerRadius(double radius)
    {
        var cornerRadius = new CornerRadius(Math.Max(0, radius));
        BackgroundPlate.CornerRadius = cornerRadius;
        PersistentAttentionBorder.CornerRadius = cornerRadius;
    }

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
                    var accent = App.Current.ThemeService?.GetEffectiveAccentColor()
                        ?? AccentColorHelper.DefaultAccentColor;
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
        ScalarKeyFrameAnimation translation = visual.Compositor.CreateScalarKeyFrameAnimation();
        translation.InsertKeyFrame(0, 0);
        translation.InsertKeyFrame(1, (float)maxTranslate);
        translation.Duration = TimeSpan.FromSeconds(CompactLiveIndeterminateDurationSeconds);
        translation.IterationBehavior = AnimationIterationBehavior.Forever;

        ScalarKeyFrameAnimation opacity = visual.Compositor.CreateScalarKeyFrameAnimation();
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

        Visual visual = ElementCompositionPreview.GetElementVisual(CompactLiveProgress);
        visual.StopAnimation("Translation.X");
        visual.StopAnimation(nameof(Visual.Opacity));
        CompactLiveProgress.Translation = Vector3.Zero;
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

    private DispatcherQueueTimer? _compactLiveBreathingTimer;
    private ScalarKeyFrameAnimation? _compactLiveTranslationAnimation;
    private ScalarKeyFrameAnimation? _compactLiveOpacityAnimation;
    private double _compactLiveIndeterminateSegment;
    private double _compactLiveBreathingPhase;

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

        if (_compactLiveBreathingTimer is not null)
        {
            return;
        }

        _compactLiveBreathingPhase = 0;
        _compactLiveBreathingTimer = DispatcherQueue.CreateTimer();
        _compactLiveBreathingTimer.Interval = TimeSpan.FromMilliseconds(50);
        _compactLiveBreathingTimer.Tick += CompactLiveBreathingTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerCreated();
        _compactLiveBreathingTimer.Start();
    }

    private void StopCompactLiveBreathing()
    {
        if (_compactLiveBreathingTimer is not { } timer)
        {
            return;
        }

        _compactLiveBreathingTimer = null;
        timer.Stop();
        timer.Tick -= CompactLiveBreathingTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    private void CompactLiveBreathingTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _compactLiveBreathingPhase += 0.06;
        CompactLiveProgress.Opacity =
            0.6 + 0.2 * Math.Sin(_compactLiveBreathingPhase);
    }

    private void ApplyFullBleedVisibility(bool visible)
    {
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
        CollapsedChromeLayer.RequestedTheme = presentation.UseLightForeground switch
        {
            true => ElementTheme.Dark,
            false => ElementTheme.Light,
            null => ElementTheme.Default
        };
    }

    private void ApplyEdgeGlow(WidgetCompactPresentation p)
    {
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
        ScalarKeyFrameAnimation animation = visual.Compositor.CreateScalarKeyFrameAnimation();
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

        ElementCompositionPreview
            .GetElementVisual(CompactEdgeGlow)
            .StopAnimation(nameof(Visual.Opacity));
        _edgeGlowPulseAnimation = null;
    }

    // ── Particles (rain / snow) ────────────────────────────────

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

    private sealed class CompactParticleAnimationState
    {
        internal CompactParticleAnimationState(
            Microsoft.UI.Xaml.Shapes.Shape shape,
            double speed,
            double drift)
        {
            Shape = shape;
            Speed = speed;
            Drift = drift;
        }

        internal Microsoft.UI.Xaml.Shapes.Shape Shape { get; }
        internal double Speed { get; }
        internal double Drift { get; }
        internal CompositionScopedBatch? Batch { get; set; }
        internal Vector3KeyFrameAnimation? Animation { get; set; }
        internal int Generation { get; set; }
    }

    private readonly List<CompactParticleAnimationState> _particles = [];
    private CompactParticleKind _activeParticleKind = CompactParticleKind.None;
    private int _particleAnimationGeneration;

    private void ApplyParticles(WidgetCompactPresentation p)
    {
        if (!_isHostVisualActivityEnabled ||
            !IsLoaded ||
            !_isCollapsed ||
            !CompactAmbientAnimationsEnabled())
        {
            StopParticles();
            return;
        }

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
                Canvas.SetLeft(line, rng.NextDouble() * CompactParticleCanvasWidth);
                Canvas.SetTop(line, rng.NextDouble() * CompactParticleCanvasHeight);
                CompactParticleCanvas.Children.Add(line);
                _particles.Add(new CompactParticleAnimationState(
                    line,
                    0.5 + rng.NextDouble() * 0.4,
                    -0.2));
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
                Canvas.SetLeft(dot, rng.NextDouble() * CompactParticleCanvasWidth);
                Canvas.SetTop(dot, rng.NextDouble() * CompactParticleCanvasHeight);
                CompactParticleCanvas.Children.Add(dot);
                _particles.Add(new CompactParticleAnimationState(
                    dot,
                    0.15 + rng.NextDouble() * 0.15,
                    0.08 + rng.NextDouble() * 0.06));
            }
        }

        int generation = ++_particleAnimationGeneration;
        foreach (CompactParticleAnimationState particle in _particles)
        {
            StartParticleAnimation(particle, generation);
        }
    }

    private void StopParticles()
    {
        ++_particleAnimationGeneration;
        foreach (CompactParticleAnimationState particle in _particles)
        {
            if (particle.Batch is { } batch)
            {
                batch.Completed -= ParticleAnimationBatch_Completed;
                particle.Batch = null;
            }

            Visual visual = ElementCompositionPreview.GetElementVisual(particle.Shape);
            visual.StopAnimation("Translation");
            particle.Shape.Translation = Vector3.Zero;
            particle.Animation = null;
        }

        CompactParticleCanvas.Children.Clear();
        _particles.Clear();
        _activeParticleKind = CompactParticleKind.None;
    }

    private void StartParticleAnimation(CompactParticleAnimationState particle, int generation)
    {
        if (!_isHostVisualActivityEnabled ||
            !IsLoaded ||
            !_isCollapsed ||
            !CompactAmbientAnimationsEnabled() ||
            !SystemAnimationsEnabled() ||
            generation != _particleAnimationGeneration)
        {
            return;
        }

        double startTop = Canvas.GetTop(particle.Shape);
        if (!double.IsFinite(startTop))
        {
            startTop = -10;
            Canvas.SetTop(particle.Shape, startTop);
        }

        double travel = Math.Max(1, CompactParticleCanvasHeight + 10 - startTop);
        double durationSeconds = Math.Max(0.05, travel / particle.Speed * 0.05);
        double horizontalTravel = particle.Drift * durationSeconds / 0.05;
        double startLeft = Canvas.GetLeft(particle.Shape);
        if (!double.IsFinite(startLeft))
        {
            startLeft = Random.Shared.NextDouble() * CompactParticleCanvasWidth;
            Canvas.SetLeft(particle.Shape, startLeft);
        }

        ElementCompositionPreview.SetIsTranslationEnabled(particle.Shape, true);
        Visual visual = ElementCompositionPreview.GetElementVisual(particle.Shape);
        visual.StopAnimation("Translation");
        particle.Shape.Translation = Vector3.Zero;

        Vector3KeyFrameAnimation animation = visual.Compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(0, Vector3.Zero);
        if (horizontalTravel < 0 && startLeft + horizontalTravel < -10)
        {
            InsertParticleWrapKeyFrames(
                animation,
                startLeft,
                horizontalTravel,
                travel,
                boundary: -10,
                wrappedPosition: CompactParticleCanvasWidth + 5);
            horizontalTravel =
                CompactParticleCanvasWidth + 5 - startLeft +
                horizontalTravel * (1 - (-10 - startLeft) / horizontalTravel);
        }
        else if (horizontalTravel > 0 &&
                 startLeft + horizontalTravel > CompactParticleCanvasWidth + 10)
        {
            InsertParticleWrapKeyFrames(
                animation,
                startLeft,
                horizontalTravel,
                travel,
                boundary: CompactParticleCanvasWidth + 10,
                wrappedPosition: -5);
            horizontalTravel =
                -5 - startLeft +
                horizontalTravel *
                (1 - (CompactParticleCanvasWidth + 10 - startLeft) / horizontalTravel);
        }
        animation.InsertKeyFrame(
            1,
            new Vector3((float)horizontalTravel, (float)travel, 0));
        animation.Duration = TimeSpan.FromSeconds(durationSeconds);

        CompositionScopedBatch batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        particle.Generation = generation;
        particle.Animation = animation;
        particle.Batch = batch;
        batch.Completed += ParticleAnimationBatch_Completed;
        visual.StartAnimation("Translation", animation);
        batch.End();
    }

    private static void InsertParticleWrapKeyFrames(
        Vector3KeyFrameAnimation animation,
        double startLeft,
        double horizontalTravel,
        double verticalTravel,
        double boundary,
        double wrappedPosition)
    {
        float wrapProgress = (float)Math.Clamp(
            (boundary - startLeft) / horizontalTravel,
            0,
            1);
        float afterWrapProgress = Math.Min(1, wrapProgress + 0.0001f);
        animation.InsertKeyFrame(
            wrapProgress,
            new Vector3(
                (float)(boundary - startLeft),
                (float)(verticalTravel * wrapProgress),
                0));
        animation.InsertKeyFrame(
            afterWrapProgress,
            new Vector3(
                (float)(wrappedPosition - startLeft +
                    horizontalTravel * (afterWrapProgress - wrapProgress)),
                (float)(verticalTravel * afterWrapProgress),
                0));
    }

    private void ParticleAnimationBatch_Completed(
        object sender,
        CompositionBatchCompletedEventArgs args)
    {
        if (sender is not CompositionScopedBatch completedBatch)
        {
            return;
        }

        CompactParticleAnimationState? particle = _particles.FirstOrDefault(
            candidate => ReferenceEquals(candidate.Batch, completedBatch));
        completedBatch.Completed -= ParticleAnimationBatch_Completed;
        if (particle is null)
        {
            return;
        }

        particle.Batch = null;
        particle.Animation = null;
        if (!_isHostVisualActivityEnabled ||
            !CompactAmbientAnimationsEnabled() ||
            !SystemAnimationsEnabled() ||
            particle.Generation != _particleAnimationGeneration ||
            _activeParticleKind == CompactParticleKind.None)
        {
            return;
        }

        Visual visual = ElementCompositionPreview.GetElementVisual(particle.Shape);
        visual.StopAnimation("Translation");
        particle.Shape.Translation = Vector3.Zero;
        Canvas.SetTop(particle.Shape, -10);
        Canvas.SetLeft(
            particle.Shape,
            Random.Shared.NextDouble() * CompactParticleCanvasWidth);
        StartParticleAnimation(particle, particle.Generation);
    }

    // ── Bottom glow (music playback) ─────────────────────────

    private DispatcherQueueTimer? _bottomGlowTimer;
    private double _bottomGlowPhase;

    private void ApplySpectrum(WidgetCompactPresentation p)
    {
        // Bottom glow removed - progress bar is sufficient
        CompactBottomGlow.Visibility = Visibility.Collapsed;
        StopBottomGlow();
    }

    private void StartBottomGlow()
    {
        if (!_isHostVisualActivityEnabled ||
            !IsLoaded ||
            !_isCollapsed ||
            !CompactAmbientAnimationsEnabled() ||
            !SystemAnimationsEnabled())
        {
            StopBottomGlow();
            CompactBottomGlow.Opacity = 0.6;
            return;
        }

        if (_bottomGlowTimer is not null)
        {
            return;
        }

        _bottomGlowPhase = 0;
        _bottomGlowTimer = DispatcherQueue.CreateTimer();
        _bottomGlowTimer.Interval = TimeSpan.FromMilliseconds(50);
        _bottomGlowTimer.Tick += BottomGlowTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerCreated();
        _bottomGlowTimer.Start();
    }

    private void StopBottomGlow()
    {
        if (_bottomGlowTimer is not { } timer)
        {
            return;
        }

        _bottomGlowTimer = null;
        timer.Stop();
        timer.Tick -= BottomGlowTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    private void BottomGlowTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _bottomGlowPhase += 0.035;
        CompactBottomGlow.Opacity =
            0.35 + 0.3 * Math.Sin(_bottomGlowPhase);
    }

    // ── Breathing border (search) ──────────────────────────────

    private DispatcherQueueTimer? _breathBorderTimer;
    private double _breathBorderPhase;

    private void ApplyShimmer(WidgetCompactPresentation p)
    {
        CompactShimmer.Opacity = 0;
        StopBreathBorder();
    }

    private void StartBreathBorder()
    {
        if (!_isHostVisualActivityEnabled ||
            !IsLoaded ||
            !_isCollapsed ||
            !CompactAmbientAnimationsEnabled() ||
            !SystemAnimationsEnabled())
        {
            StopBreathBorder();
            CompactEdgeGlow.Opacity = 0.35;
            return;
        }

        if (_breathBorderTimer is not null)
        {
            return;
        }

        _breathBorderPhase = 0;
        _breathBorderTimer = DispatcherQueue.CreateTimer();
        _breathBorderTimer.Interval = TimeSpan.FromMilliseconds(50);
        _breathBorderTimer.Tick += BreathBorderTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerCreated();
        _breathBorderTimer.Start();
    }

    private void StopBreathBorder()
    {
        if (_breathBorderTimer is not { } timer)
        {
            return;
        }

        _breathBorderTimer = null;
        timer.Stop();
        timer.Tick -= BreathBorderTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    private void BreathBorderTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        _breathBorderPhase += 0.03;
        CompactEdgeGlow.Opacity =
            0.35 + 0.15 * Math.Sin(_breathBorderPhase);
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
        if (!SystemAnimationsEnabled()) return;
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
        if (!SystemAnimationsEnabled())
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
        Canvas.SetLeft(marquee.Primary, 0);
        Canvas.SetLeft(marquee.Clone, marquee.NaturalWidth + CompactMarqueeGap);

        var transform = new TranslateTransform();
        marquee.Canvas.RenderTransform = transform;
        double distance = marquee.NaturalWidth + CompactMarqueeGap;
        TimeSpan startDelay = TimeSpan.FromMilliseconds(CompactMarqueeStartDelayMs);
        TimeSpan travelDuration = TimeSpan.FromSeconds(
            distance / CompactMarqueeSpeedPixelsPerSecond);
        TimeSpan cycleDuration = startDelay + travelDuration;
        var translation = new DoubleAnimationUsingKeyFrames
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        translation.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 0
        });
        translation.KeyFrames.Add(new DiscreteDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(startDelay),
            Value = 0
        });
        translation.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(cycleDuration),
            Value = -distance
        });
        Storyboard.SetTarget(translation, transform);
        Storyboard.SetTargetProperty(translation, "X");

        var storyboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever
        };
        storyboard.Children.Add(translation);

        _compactMarqueePrimary = marquee.Primary;
        _compactMarqueeClone = marquee.Clone;
        _compactMarqueeCanvas = marquee.Canvas;
        _compactMarqueeViewport = marquee.Viewport;
        _compactMarqueeTransform = transform;
        _compactMarqueeStoryboard = storyboard;
        storyboard.Begin();
    }

    private bool ShouldSuspendCompactMarquee() =>
        _compactPresentation is { ShowVinyl: true, IsPlaying: false };

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
        Storyboard? storyboard = _compactMarqueeStoryboard;
        _compactMarqueeStoryboard = null;
        if (storyboard is not null)
        {
            storyboard.Stop();
            storyboard.Children.Clear();
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
            _compactPresentation is not { } presentation)
        {
            return;
        }

        ApplyEdgeGlow(presentation);
        ApplyParticles(presentation);
        ApplyCompactLiveState();
        bool showVinyl = presentation.ShowVinyl && presentation.Thumbnail is not null;
        UpdateCompactVinylRotation(showVinyl && presentation.IsPlaying);
    }

    private void StopCompactVisualTimers()
    {
        StopCompactLiveIndeterminate();
        StopCompactLiveBreathing();
        StopParticles();
        StopBottomGlow();
        StopBreathBorder();
        StopEdgeGlowPulse();
    }

    private void StopCompactVinylRotation()
    {
        if (!_isCompactVinylRotating && _compactVinylRotationStoryboard is null)
        {
            return;
        }

        _isCompactVinylRotating = false;
        _compactVinylRotationStoryboard?.Stop();
    }

    private void ResetCompactMarqueeTarget()
    {
        if (_compactMarqueeTransform is not null)
        {
            _compactMarqueeTransform.X = 0;
        }
        if (_compactMarqueeCanvas is not null)
        {
            _compactMarqueeCanvas.RenderTransform = null;
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
        _compactMarqueeTransform = null;
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

    private double ResolveTitleBarMinimumHeight() =>
        _titleBarRowHeight.GridUnitType == GridUnitType.Pixel
            ? Math.Max(0, _titleBarRowHeight.Value)
            : 0;

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
            shell.UpdateCompactGroupPositionRail(shell._groupPresentation);
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
        bool hasGroupNavigation = _groupPresentation is not null;
        bool isEditingTitle = TitleEditorContent is not null;
        bool hasCustomTitleBar = TitleBarContent is not null && !hasGroupNavigation;
        CustomTitleBarContentPresenter.Visibility = hasCustomTitleBar ? Visibility.Visible : Visibility.Collapsed;
        DefaultTitleBarContentHost.Visibility = hasCustomTitleBar ? Visibility.Collapsed : Visibility.Visible;
        TitleIdentityHost.Visibility = hasGroupNavigation && !isEditingTitle
            ? Visibility.Collapsed
            : Visibility.Visible;
        GroupTitleSwitcher.Visibility = hasGroupNavigation && !isEditingTitle
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ApplyChromeMode()
    {
        if (ShellRoot.RowDefinitions.Count < 2)
        {
            return;
        }

        if (_isCollapsed)
        {
            ShellRoot.RowDefinitions[0].MinHeight = 0;
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
        TitleBarGrid.Padding = usesOverlay ? new Thickness(2, 0, 0, 0) : _titleBarPadding;
        RightActionButtons.VerticalAlignment = usesOverlay ? VerticalAlignment.Top : VerticalAlignment.Center;
        TitleBarGrid.Visibility = usesOverlay && !isEditingTitle ? Visibility.Collapsed : Visibility.Visible;

        HeaderDivider.Visibility = usesOverlay ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetRow(ShellContentPresenter, usesOverlay ? 0 : 1);
        Grid.SetRowSpan(ShellContentPresenter, usesOverlay ? 2 : 1);
        ShellContentPresenter.Margin = new Thickness(0);
        TitleIdentityHost.Visibility = !isEditingTitle &&
                                       (_groupPresentation is not null || usesOverlay)
            ? Visibility.Collapsed
            : Visibility.Visible;
        GroupTitleSwitcher.Visibility =
            _groupPresentation is not null &&
            !usesOverlay &&
            !isEditingTitle
            ? Visibility.Visible
            : Visibility.Collapsed;
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
        show = show && _groupPresentation is null;
        if (SystemAnimationsEnabled() &&
            _compactDragGripStoryboard.IsActiveFor(show))
        {
            return;
        }

        if (!_isHostVisualActivityEnabled || !SystemAnimationsEnabled())
        {
            _compactDragGripStoryboard.StopAndClear();
            CompactDragGripIndicator.Opacity = show ? 0.7 : 0;
            return;
        }

        var animation = new DoubleAnimation
        {
            To = show ? 0.7 : 0,
            Duration = TimeSpan.FromMilliseconds(
                show
                    ? WidgetMotion.TransitionMilliseconds
                    : WidgetMotion.FeedbackMilliseconds),
            EasingFunction = new CubicEase
            {
                EasingMode = show ? EasingMode.EaseOut : EasingMode.EaseIn
            }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, CompactDragGripIndicator);
        Storyboard.SetTargetProperty(animation, "Opacity");
        storyboard.Children.Add(animation);
        _compactDragGripStoryboard.Begin(storyboard, show);
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
        double identityTarget = _isCollapsed && _isPointerOverCompactIdentity ? 1 : 0;
        double actionTarget = _isCollapsed && IsPointerOverCompactActionRegion() ? 1 : 0;
        AnimateOpacity(
            CompactIdentityRegionHighlight,
            identityTarget,
            _compactIdentityHighlightStoryboard);
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
        if (wasActive && _hostedContent is FileSurfaceContent fileSurface)
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

        // WinUI can miss the final Drop/DragItemsCompleted callback even when
        // the pointer is released over the file surface. Let that surface
        // commit its last confirmed internal insertion before clearing state.
        // Do not raise CompactDragLeft here: its delayed restore belongs to a
        // real drag leave and can race the hover request repairing this stale
        // session.
        _isShellDragActive = false;
        if (_hostedContent is FileSurfaceContent fileSurface)
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
        bool isDark = ActualTheme == ElementTheme.Dark ||
            ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark;
        return SharedBrushCache.GetOrCreate(isDark
            ? Color.FromArgb(0xFF, 0x2C, 0x2F, 0x36)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    }

    private Brush CreateOpaqueOverlayButtonBorder()
    {
        bool isDark = ActualTheme == ElementTheme.Dark ||
            ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark;
        return SharedBrushCache.GetOrCreate(isDark
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
        GroupTitleSwitcher.HandleTitleBarPointerWheel(TitleBarGrid, e);
    }

    internal bool IsPointOverGroupTitleBar(
        FrameworkElement coordinateSpace,
        Windows.Foundation.Point point)
    {
        if (_groupPresentation is null ||
            GroupTitleSwitcher.Visibility != Visibility.Visible ||
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
        return GroupTitleSwitcher.HandleNativeWheel(delta);
    }

    internal void NotifyGroupMemberInvocationCompleted(
        string widgetId,
        bool succeeded)
    {
        GroupTitleSwitcher.NotifyMemberInvocationCompleted(
            widgetId,
            succeeded);
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
            _pendingDragHandleClickAction = pressedMoveHandle && _groupPresentation is not null
                ? DragHandleClickAction.OpenGroup
                : startsWindowDrag
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
        _overlayHandleVisualStoryboard.Begin(storyboard, showCollapseCue);
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
