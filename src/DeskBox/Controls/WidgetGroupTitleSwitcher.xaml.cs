using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.UI;

namespace DeskBox.Controls;

/// <summary>
/// A title-bar-local selector for a widget group. The host owns content commits;
/// native tabs retain the requested selection until it commits or fails, while
/// the stacked title follows the committed <see cref="SetPresentation"/>.
/// </summary>
public sealed partial class WidgetGroupTitleSwitcher : UserControl
{
    private const string DetachDragFormat =
        "DeskBox.WidgetGroup.MemberDetach.v1";
    private const int DetachLongPressMilliseconds = 460;
    private const double DetachLongPressMovementTolerance = 12;
    private const double MaximumTitleWidth = 132;
    private const double IdentitySpacing = 5;
    private double _titleBarContentHeight = WidgetTitleBarMetricsCalculator.MinimumTitleContentHeight;

    private WidgetGroupPresentation? _presentation;
    private IdentitySnapshot? _displayedIdentity;
    private MenuFlyout? _openFlyout;
    private long _tabHoverSwitchGeneration;
    private string? _hoveredTabMemberId;
    private CancellationTokenSource? _tabHoverSwitchCancellation;
    private string? _draggingMemberId;
    private string? _pendingDetachMemberId;
    private UIElement? _detachLongPressSource;
    private Microsoft.UI.Input.PointerPoint? _detachLongPressPoint;
    private Windows.Foundation.Point _detachLongPressStartPosition;
    private CancellationTokenSource? _detachLongPressCancellation;
    private Storyboard? _detachHoldStoryboard;
    private bool _isDetachLongPressArmed;
    private bool _isStartingDetachDrag;
    private DateTimeOffset _suppressGroupTitleClickUntil;

    public static readonly DependencyProperty DisplayModeProperty =
        DependencyProperty.Register(
            nameof(DisplayMode),
            typeof(string),
            typeof(WidgetGroupTitleSwitcher),
            new PropertyMetadata(
                WidgetGroupTitleDisplayModes.IconAndText,
                OnDisplayModeChanged));

    public static readonly DependencyProperty NavigationStyleProperty =
        DependencyProperty.Register(
            nameof(NavigationStyle),
            typeof(string),
            typeof(WidgetGroupTitleSwitcher),
            new PropertyMetadata(
                WidgetGroupNavigationStyles.Stack,
                OnNavigationStyleChanged));

    public static readonly DependencyProperty WheelSwitchEnabledProperty =
        DependencyProperty.Register(
            nameof(WheelSwitchEnabled),
            typeof(bool),
            typeof(WidgetGroupTitleSwitcher),
            new PropertyMetadata(true));

    public static readonly DependencyProperty HoverSwitchEnabledProperty =
        DependencyProperty.Register(
            nameof(HoverSwitchEnabled),
            typeof(bool),
            typeof(WidgetGroupTitleSwitcher),
            new PropertyMetadata(false));

    public static readonly DependencyProperty TitleIconModeProperty =
        DependencyProperty.Register(
            nameof(TitleIconMode),
            typeof(string),
            typeof(WidgetGroupTitleSwitcher),
            new PropertyMetadata(
                WidgetTitleIconModeNames.Color,
                OnTitleIconAppearanceChanged));

    public static readonly DependencyProperty TitleIconAccentColorProperty =
        DependencyProperty.Register(
            nameof(TitleIconAccentColor),
            typeof(Color),
            typeof(WidgetGroupTitleSwitcher),
            new PropertyMetadata(
                AccentColorHelper.DefaultAccentColor,
                OnTitleIconAppearanceChanged));

    public static readonly DependencyProperty IconSizeProperty =
        DependencyProperty.Register(
            nameof(IconSize),
            typeof(double),
            typeof(WidgetGroupTitleSwitcher),
            new PropertyMetadata(14d, OnTitleIconAppearanceChanged));

    public WidgetGroupTitleSwitcher()
    {
        InitializeComponent();
        RegisterSelectorPointerHandlers();
        RegisterTabPointerHandlers();
        RegisterKeyboardAccelerators();
        ApplyWheelFeedbackAccent();
        CurrentTitle.RegisterPropertyChangedCallback(
            TextBlock.FontSizeProperty,
            (_, _) =>
            {
                if (_displayedIdentity is { } identity &&
                    _identityStoryboard is null)
                {
                    UpdateIdentityViewportWidth(identity);
                }
                SynchronizeTabs();
            });
        ApplyDisplayMode();
        ApplyNavigationStyle();
        Unloaded += (_, _) =>
        {
            CancelDetachLongPress();
            CancelTabInteraction();
            CancelAllHoverSwitches();
        };
        Visibility = Visibility.Collapsed;
    }

    public event EventHandler<WidgetGroupMemberEventArgs>? MemberInvoked;

    public event EventHandler<WidgetGroupMemberEventArgs>? RemoveMemberRequested;

    public event EventHandler<WidgetGroupMemberEventArgs>? DetachMemberRequested;

    public event EventHandler<WidgetGroupMemberEventArgs>? DetachDragStarted;

    public event EventHandler<WidgetGroupMemberEventArgs>? DetachDragCompleted;

    public event EventHandler<WidgetGroupReorderEventArgs>? ReorderRequested;

    public event EventHandler? DissolveRequested;

    public event EventHandler? PickerOpened;

    public event EventHandler? PickerClosed;

    public string DisplayMode
    {
        get => (string)GetValue(DisplayModeProperty);
        set => SetValue(DisplayModeProperty, value);
    }

    public string NavigationStyle
    {
        get => (string)GetValue(NavigationStyleProperty);
        set => SetValue(NavigationStyleProperty, value);
    }

    public bool WheelSwitchEnabled
    {
        get => (bool)GetValue(WheelSwitchEnabledProperty);
        set => SetValue(WheelSwitchEnabledProperty, value);
    }

    public bool HoverSwitchEnabled
    {
        get => (bool)GetValue(HoverSwitchEnabledProperty);
        set => SetValue(HoverSwitchEnabledProperty, value);
    }

    public string TitleIconMode
    {
        get => (string)GetValue(TitleIconModeProperty);
        set => SetValue(TitleIconModeProperty, value);
    }

    public Color TitleIconAccentColor
    {
        get => (Color)GetValue(TitleIconAccentColorProperty);
        set => SetValue(TitleIconAccentColorProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public string? ActiveMemberId => _presentation?.ActiveMemberId;

    private void RegisterSelectorPointerHandlers()
    {
        SelectorButton.AddHandler(
            UIElement.PointerPressedEvent,
            new PointerEventHandler(SelectorButton_PointerPressed),
            handledEventsToo: true);
        SelectorButton.AddHandler(
            UIElement.PointerMovedEvent,
            new PointerEventHandler(GroupTitle_PointerMoved),
            handledEventsToo: true);
        SelectorButton.AddHandler(
            UIElement.PointerReleasedEvent,
            new PointerEventHandler(SelectorButton_PointerReleased),
            handledEventsToo: true);
        SelectorButton.AddHandler(
            UIElement.PointerCanceledEvent,
            new PointerEventHandler(GroupTitle_PointerCanceled),
            handledEventsToo: true);
        SelectorButton.AddHandler(
            UIElement.PointerCaptureLostEvent,
            new PointerEventHandler(GroupTitle_PointerCaptureLost),
            handledEventsToo: true);
    }

    /// <summary>
    /// Applies an already-committed group presentation. Pass
    /// <paramref name="animateIdentity"/> only when the host has atomically
    /// committed the corresponding content.
    /// </summary>
    internal void ApplyTitleBarMetrics(double contentHeight, double titleFontSize)
    {
        CurrentTitle.FontSize = titleFontSize;
        OutgoingTitle.FontSize = titleFontSize;
        foreach (GroupTab row in _tabs.Values)
        {
            row.Title.FontSize = titleFontSize;
        }
        if (_titleBarContentHeight == contentHeight)
        {
            return;
        }

        _titleBarContentHeight = contentHeight;
        Root.MinHeight = contentHeight;
        SelectorButton.MinHeight = contentHeight;
        TabsView.MinHeight = contentHeight;
        foreach (GroupTab row in _tabs.Values)
        {
            row.Tab.MinHeight = contentHeight;
        }
    }

    internal void SetTabStripMargin(Thickness margin)
    {
        TabsView.Margin = margin;
    }

    public void SetPresentation(
        WidgetGroupPresentation? presentation,
        bool animateIdentity = false,
        WidgetGroupSwitchOrigin origin = WidgetGroupSwitchOrigin.Programmatic,
        bool forward = true)
    {
        IdentitySnapshot? previous = _displayedIdentity;
        _presentation = presentation;
        ReconcilePendingWheelTarget(presentation);
        IdentitySnapshot? next = ResolveActiveIdentity(presentation);

        if (next is null)
        {
            ClosePicker();
            CancelIdentityAnimation();
            CancelWheelFeedback();
            _displayedIdentity = null;
            SetIdentity(
                CurrentIcon,
                CurrentTitle,
                null);
            SetIdentity(
                OutgoingIcon,
                OutgoingTitle,
                null);
            ClearTabs();
            SetPositionRail(CurrentPositionRailLayer, null);
            SetPositionRail(OutgoingPositionRailLayer, null);
            PositionRailViewport.Visibility = Visibility.Collapsed;
            LoadingRing.IsActive = false;
            LoadingRing.Opacity = 0;
            _isPointerOverSelector = false;
            _isSelectorPressed = false;
            CancelAllHoverSwitches();
            UpdateInteractionChrome(animate: false);
            Visibility = Visibility.Collapsed;
            return;
        }

        Visibility = Visibility.Visible;
        IsEnabled = presentation!.Members.Count > 1;
        _displayedIdentity = next;
        SelectorButton.Tag = next.WidgetId;
        PositionRailViewport.Visibility = next.Count > 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        ApplyNavigationStyle();

        bool identityChanged =
            previous is not null &&
            !string.Equals(
                previous.WidgetId,
                next.WidgetId,
                StringComparison.Ordinal);
        if (animateIdentity && identityChanged && CanAnimateIdentity())
        {
            AnimateIdentityTransition(previous!, next, origin, forward);
        }
        else
        {
            CancelIdentityAnimation();
            SetIdentity(
                CurrentIcon,
                CurrentTitle,
                next);
            SetIdentity(
                OutgoingIcon,
                OutgoingTitle,
                null);
            SetPositionRail(CurrentPositionRailLayer, next);
            SetPositionRail(OutgoingPositionRailLayer, null);
            CurrentPositionRailLayer.Opacity = 1;
            OutgoingPositionRailLayer.Opacity = 0;
            CurrentIdentityLayer.Opacity = 1;
            OutgoingIdentityLayer.Opacity = 0;
            ApplyDisplayMode();
            UpdateIdentityViewportWidth(next);
        }

        UpdateAccessibility(next);
    }

    public void OpenPicker(FrameworkElement? anchor = null)
    {
        if (_presentation is null ||
            _presentation.Members.Count == 0 ||
            _openFlyout is not null)
        {
            return;
        }

        MenuFlyout flyout = CreateMembersFlyout();
        _openFlyout = flyout;
        flyout.ShowAt(anchor ?? (UsesTabs ? TabsView : SelectorButton));
    }

    public void SetMemberLoading(string? widgetId, bool isLoading)
    {
        SetTabLoading(widgetId, isLoading);
        if (!isLoading ||
            string.IsNullOrWhiteSpace(widgetId) ||
            _presentation is null)
        {
            LoadingRing.IsActive = false;
            LoadingRing.Opacity = 0;
            return;
        }

        WidgetGroupMemberPresentation? member =
            _presentation.Members.FirstOrDefault(item =>
                string.Equals(
                    item.WidgetId,
                    widgetId,
                    StringComparison.Ordinal));
        if (member is null)
        {
            return;
        }

        LoadingRing.IsActive = true;
        LoadingRing.Opacity = 1;
        AutomationProperties.SetName(
            LoadingRing,
            T("Widget.Group.Switching").Replace(
                "{0}",
                member.Name,
                StringComparison.Ordinal));
    }

    private static void OnDisplayModeChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is WidgetGroupTitleSwitcher switcher)
        {
            switcher.ApplyDisplayMode();
            switcher.SynchronizeTabs();
        }
    }

    private static void OnNavigationStyleChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is WidgetGroupTitleSwitcher switcher)
        {
            switcher.ApplyNavigationStyle();
        }
    }

    private static void OnTitleIconAppearanceChanged(
        DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is WidgetGroupTitleSwitcher switcher)
        {
            switcher.ApplyWheelFeedbackAccent();
            switcher.ApplyDisplayMode();
            switcher.SynchronizeTabs();
        }
    }

    private void ApplyWheelFeedbackAccent()
    {
        Color accent = TitleIconAccentColor;
        UpWheelFeedbackAccentStart.Color = accent;
        UpWheelFeedbackAccentEnd.Color = accent;
        DownWheelFeedbackAccentStart.Color = accent;
        DownWheelFeedbackAccentEnd.Color = accent;
    }

    private void ApplyNavigationStyle()
    {
        bool useTabs = string.Equals(
            WidgetGroupNavigationStyles.Normalize(
                NavigationStyle,
                allowFollowDefault: false),
            WidgetGroupNavigationStyles.Tabs,
            StringComparison.Ordinal);
        SelectorButton.Visibility = useTabs ? Visibility.Collapsed : Visibility.Visible;
        // Detach drags are started explicitly after the long-press threshold;
        // leaving native CanDrag enabled would reintroduce eager drag starts.
        SelectorButton.CanDrag = false;
        CapsuleSurface.Visibility = useTabs
            ? Visibility.Collapsed
            : Visibility.Visible;
        TabsView.Visibility = useTabs && _presentation is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        TitleInteractionChrome.Visibility = useTabs
            ? Visibility.Collapsed
            : Visibility.Visible;
        UpdateInteractionChrome(animate: false);
        if (useTabs)
        {
            CancelDetachLongPress();
            SynchronizeTabs();
        }
        else
        {
            CancelTabInteraction();
        }
    }

    private void ApplyDisplayMode()
    {
        string displayMode = WidgetGroupTitleDisplayModes.Normalize(
            DisplayMode,
            allowFollowDefault: false);
        bool wantsIcon = displayMode is
            WidgetGroupTitleDisplayModes.IconAndText or
            WidgetGroupTitleDisplayModes.IconOnly;
        bool iconModeVisible =
            WidgetTitleIconModeNames.NormalizeMode(TitleIconMode) is not
                WidgetTitleIconMode.Hidden;
        bool showIcon = wantsIcon && iconModeVisible;
        bool showText = displayMode is
            WidgetGroupTitleDisplayModes.IconAndText or
            WidgetGroupTitleDisplayModes.TextOnly ||
            (displayMode == WidgetGroupTitleDisplayModes.IconOnly &&
             !showIcon);

        string effectiveIconMode = showIcon
            ? TitleIconMode
            : WidgetTitleIconModeNames.Hidden;
        CurrentIcon.Mode = effectiveIconMode;
        OutgoingIcon.Mode = effectiveIconMode;
        CurrentIcon.AccentColor = TitleIconAccentColor;
        OutgoingIcon.AccentColor = TitleIconAccentColor;
        CurrentIcon.IconSize = IconSize;
        OutgoingIcon.IconSize = IconSize;
        CurrentTitle.Visibility =
            showText ? Visibility.Visible : Visibility.Collapsed;
        OutgoingTitle.Visibility =
            showText ? Visibility.Visible : Visibility.Collapsed;

        if (_displayedIdentity is { } identity)
        {
            SetIdentity(
                CurrentIcon,
                CurrentTitle,
                identity);
            UpdateIdentityViewportWidth(identity);
        }
    }

    private double MeasureIdentityWidth(IdentitySnapshot identity)
    {
        string displayMode = WidgetGroupTitleDisplayModes.Normalize(
            DisplayMode,
            allowFollowDefault: false);
        bool wantsIcon = displayMode is
            WidgetGroupTitleDisplayModes.IconAndText or
            WidgetGroupTitleDisplayModes.IconOnly;
        bool showIcon =
            wantsIcon &&
            WidgetTitleIconModeNames.NormalizeMode(TitleIconMode) is not
                WidgetTitleIconMode.Hidden;
        bool showText = displayMode is
            WidgetGroupTitleDisplayModes.IconAndText or
            WidgetGroupTitleDisplayModes.TextOnly ||
            (displayMode == WidgetGroupTitleDisplayModes.IconOnly &&
             !showIcon);

        double iconWidth = 0;
        if (showIcon)
        {
            CurrentIcon.Measure(
                new Windows.Foundation.Size(72, 28));
            iconWidth = Math.Clamp(
                Math.Ceiling(CurrentIcon.DesiredSize.Width),
                15,
                70);
        }

        double textWidth = 0;
        if (showText)
        {
            CurrentTitle.Text = identity.Name;
            CurrentTitle.Measure(
                new Windows.Foundation.Size(MaximumTitleWidth, 28));
            textWidth = Math.Clamp(
                Math.Ceiling(CurrentTitle.DesiredSize.Width),
                8,
                MaximumTitleWidth);
        }

        return Math.Max(
            12,
            iconWidth +
            textWidth +
            (showIcon && showText ? IdentitySpacing : 0));
    }

    private void UpdateIdentityViewportWidth(IdentitySnapshot identity)
    {
        IdentityViewport.Width = MeasureIdentityWidth(identity);
    }

    private void SetPositionRail(
        StackPanel host,
        IdentitySnapshot? identity)
    {
        host.Children.Clear();
        if (identity is null)
        {
            return;
        }

        IReadOnlyList<WidgetGroupPositionRailSlot> slots =
            WidgetGroupNavigationInteractionPolicy.ResolvePositionRailSlots(
                identity.Index,
                identity.Count);
        foreach (WidgetGroupPositionRailSlot slot in slots)
        {
            bool active = slot.IsActive;
            host.Children.Add(new Border
            {
                Width = 3,
                Height = active ? 7 : 3,
                HorizontalAlignment = HorizontalAlignment.Center,
                Background = CreateAccentBrush(),
                CornerRadius = new CornerRadius(1.5),
                IsHitTestVisible = false,
                Opacity = active ? 0.94 : 0.3
            });
        }
    }

    private async void BeginTabHoverSwitch(string memberId)
    {
        if (_draggingMemberId is not null || IsTabInteractionBusy)
        {
            return;
        }

        _hoveredTabMemberId = memberId;
        _tabHoverSwitchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _tabHoverSwitchCancellation = cancellation;
        long generation = ++_tabHoverSwitchGeneration;
        try
        {
            await Task.Delay(80, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (generation != _tabHoverSwitchGeneration ||
            IsTabInteractionBusy ||
            !HoverSwitchEnabled ||
            !string.Equals(
                _hoveredTabMemberId,
                memberId,
                StringComparison.Ordinal) ||
            _presentation is null ||
            string.Equals(
                _presentation.ActiveMemberId,
                memberId,
                StringComparison.Ordinal))
        {
            return;
        }

        DispatchHoverSwitch(memberId);
    }

    private void DispatchHoverSwitch(string memberId)
    {
        MemberInvoked?.Invoke(
            this,
            new WidgetGroupMemberEventArgs(
                memberId,
                WidgetGroupSwitchOrigin.Picker));
    }

    private void CancelTabHoverSwitch(string memberId)
    {
        if (!string.Equals(
                _hoveredTabMemberId,
                memberId,
                StringComparison.Ordinal))
        {
            return;
        }

        _hoveredTabMemberId = null;
        _tabHoverSwitchCancellation?.Cancel();
        _tabHoverSwitchCancellation = null;
        _tabHoverSwitchGeneration++;
    }

    private void CancelAllHoverSwitches()
    {
        _tabHoverSwitchCancellation?.Cancel();
        _tabHoverSwitchCancellation = null;
        _hoveredTabMemberId = null;
        _tabHoverSwitchGeneration++;
    }

    private void GroupTitle_DragStarting(
        UIElement sender,
        DragStartingEventArgs e)
    {
        string? memberId =
            (sender as FrameworkElement)?.Tag as string ??
            _presentation?.ActiveMemberId;
        if (!_isStartingDetachDrag ||
            string.IsNullOrWhiteSpace(memberId) ||
            _presentation is null ||
            _presentation.Members.Count < 2 ||
            !string.Equals(
                memberId,
                _presentation.ActiveMemberId,
                StringComparison.Ordinal))
        {
            e.Cancel = true;
            return;
        }

        _draggingMemberId = memberId;
        DetachDragStarted?.Invoke(
            this,
            new WidgetGroupMemberEventArgs(memberId));
        _suppressGroupTitleClickUntil =
            DateTimeOffset.UtcNow.AddMilliseconds(500);
        CancelAllHoverSwitches();
        e.Data.SetData(DetachDragFormat, memberId);
        e.Data.RequestedOperation = DataPackageOperation.Move;
        string detachHint = T("Widget.Group.DetachDragHint");
        e.Data.Properties.Title = detachHint;
        e.AllowedOperations = DataPackageOperation.Move;
        Root.Opacity = 0.72;
        DetachScaleTransform.ScaleX = 0.985;
        DetachScaleTransform.ScaleY = 0.985;
    }

    private void GroupTitle_PointerPressed(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (sender is UIElement source)
        {
            BeginDetachLongPress(source, e);
        }
    }

    private void GroupTitle_PointerMoved(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (_isStartingDetachDrag ||
            sender is not UIElement source ||
            !ReferenceEquals(source, _detachLongPressSource))
        {
            return;
        }

        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(source);
        if (!point.Properties.IsLeftButtonPressed)
        {
            CancelDetachLongPress();
            return;
        }

        _detachLongPressPoint = point;
        if (_isDetachLongPressArmed)
        {
            e.Handled = true;
            _ = StartDetachDragAsync(source, point);
            return;
        }

        double deltaX =
            point.Position.X - _detachLongPressStartPosition.X;
        double deltaY =
            point.Position.Y - _detachLongPressStartPosition.Y;
        double toleranceSquared =
            DetachLongPressMovementTolerance *
            DetachLongPressMovementTolerance;
        if ((deltaX * deltaX) + (deltaY * deltaY) > toleranceSquared)
        {
            CancelDetachLongPress();
        }
    }

    private void GroupTitle_PointerReleased(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!_isStartingDetachDrag)
        {
            CancelDetachLongPress();
        }
    }

    private void GroupTitle_PointerCanceled(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!_isStartingDetachDrag)
        {
            CancelDetachLongPress();
        }
    }

    private void GroupTitle_PointerCaptureLost(
        object sender,
        PointerRoutedEventArgs e)
    {
        if (!_isStartingDetachDrag)
        {
            CancelDetachLongPress(releasePointerCapture: false);
        }
    }

    private void BeginDetachLongPress(
        UIElement source,
        PointerRoutedEventArgs e)
    {
        Microsoft.UI.Input.PointerPoint point = e.GetCurrentPoint(source);
        string? memberId =
            (source as FrameworkElement)?.Tag as string ??
            _presentation?.ActiveMemberId;
        if (UsesTabs || !point.Properties.IsLeftButtonPressed ||
            _isStartingDetachDrag ||
            _presentation is null ||
            _presentation.Members.Count < 2 ||
            string.IsNullOrWhiteSpace(memberId) ||
            !string.Equals(
                memberId,
                _presentation.ActiveMemberId,
                StringComparison.Ordinal))
        {
            return;
        }

        CancelDetachLongPress();
        CancelWheelFeedback();
        _pendingDetachMemberId = memberId;
        _detachLongPressSource = source;
        _detachLongPressPoint = point;
        _detachLongPressStartPosition = point.Position;
        _detachLongPressCancellation = new CancellationTokenSource();
        source.CapturePointer(e.Pointer);
        StartDetachHoldVisual();
        _ = ArmDetachDragAfterLongPressAsync(
            source,
            memberId,
            _detachLongPressCancellation.Token);
    }

    private async Task ArmDetachDragAfterLongPressAsync(
        UIElement source,
        string memberId,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(
                DetachLongPressMilliseconds,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested ||
            !ReferenceEquals(source, _detachLongPressSource) ||
            !string.Equals(
                memberId,
                _pendingDetachMemberId,
                StringComparison.Ordinal) ||
            _detachLongPressPoint is null)
        {
            return;
        }

        _isDetachLongPressArmed = true;
        _detachHoldStoryboard?.Stop();
        _detachHoldStoryboard = null;
        Root.Opacity = 0.78;
        DetachScaleTransform.ScaleX = 0.98;
        DetachScaleTransform.ScaleY = 0.98;
    }

    private async Task StartDetachDragAsync(
        UIElement source,
        Microsoft.UI.Input.PointerPoint pointerPoint)
    {
        if (_isStartingDetachDrag ||
            !_isDetachLongPressArmed ||
            !ReferenceEquals(source, _detachLongPressSource) ||
            string.IsNullOrWhiteSpace(_pendingDetachMemberId))
        {
            return;
        }

        string memberId = _pendingDetachMemberId;
        _isDetachLongPressArmed = false;
        _isStartingDetachDrag = true;
        _suppressGroupTitleClickUntil =
            DateTimeOffset.UtcNow.AddMilliseconds(750);
        _detachHoldStoryboard?.Stop();
        _detachHoldStoryboard = null;
        try
        {
            await source.StartDragAsync(pointerPoint);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetGroup] Failed to start long-press detach drag " +
                $"id={memberId}: {ex}");
        }
        finally
        {
            _isStartingDetachDrag = false;
            ClearDetachLongPressState();
            RestoreDetachDragVisual();
        }
    }

    private void StartDetachHoldVisual()
    {
        _detachHoldStoryboard?.Stop();
        _detachHoldStoryboard = null;
        if (!AreSystemAnimationsEnabled() || XamlRoot is null)
        {
            return;
        }

        var storyboard = new Storyboard();
        AddAnimation(Root, nameof(UIElement.Opacity), 1, 0.86);
        AddAnimation(
            DetachScaleTransform,
            nameof(ScaleTransform.ScaleX),
            1,
            0.985);
        AddAnimation(
            DetachScaleTransform,
            nameof(ScaleTransform.ScaleY),
            1,
            0.985);
        _detachHoldStoryboard = storyboard;
        storyboard.Begin();

        void AddAnimation(
            DependencyObject target,
            string property,
            double from,
            double to)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(
                    DetachLongPressMilliseconds),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseInOut
                }
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, property);
            storyboard.Children.Add(animation);
        }
    }

    private void CancelDetachLongPress(bool releasePointerCapture = true)
    {
        if (_isStartingDetachDrag)
        {
            return;
        }

        UIElement? source = _detachLongPressSource;
        _detachLongPressCancellation?.Cancel();
        ClearDetachLongPressState();
        if (releasePointerCapture)
        {
            source?.ReleasePointerCaptures();
        }
        RestoreDetachDragVisual();
    }

    private void ClearDetachLongPressState()
    {
        _detachLongPressCancellation?.Dispose();
        _detachLongPressCancellation = null;
        _detachLongPressSource = null;
        _detachLongPressPoint = null;
        _pendingDetachMemberId = null;
        _isDetachLongPressArmed = false;
        _detachHoldStoryboard?.Stop();
        _detachHoldStoryboard = null;
    }

    private void GroupTitle_DropCompleted(
        UIElement sender,
        DropCompletedEventArgs e)
    {
        string? memberId = _draggingMemberId;
        _draggingMemberId = null;
        RestoreDetachDragVisual();
        if (e.DropResult == DataPackageOperation.None &&
            !string.IsNullOrWhiteSpace(memberId))
        {
            DetachMemberRequested?.Invoke(
                this,
                new WidgetGroupMemberEventArgs(memberId));
        }

        if (!string.IsNullOrWhiteSpace(memberId))
        {
            DetachDragCompleted?.Invoke(
                this,
                new WidgetGroupMemberEventArgs(memberId));
        }
    }

    private void RestoreDetachDragVisual()
    {
        _detachHoldStoryboard?.Stop();
        _detachHoldStoryboard = null;
        Root.Opacity = 1;
        DetachScaleTransform.ScaleX = 1;
        DetachScaleTransform.ScaleY = 1;
    }

    private Brush CreateAccentBrush() => new SolidColorBrush(TitleIconAccentColor);

    private static Brush ResolveThemeBrush(
        string resourceKey,
        Brush fallback)
    {
        return Application.Current.Resources.TryGetValue(
                   resourceKey,
                   out object? resource) &&
               resource is Brush brush
            ? brush
            : fallback;
    }

    private void UpdateAccessibility(IdentitySnapshot identity)
    {
        string automationName = T("Widget.Group.Position")
            .Replace("{0}", identity.Name, StringComparison.Ordinal)
            .Replace(
                "{1}",
                (identity.Index + 1).ToString(),
                StringComparison.Ordinal)
            .Replace(
                "{2}",
                identity.Count.ToString(),
                StringComparison.Ordinal);

        AutomationProperties.SetName(this, automationName);
        AutomationProperties.SetName(SelectorButton, automationName);
        AutomationProperties.SetHelpText(
            SelectorButton,
            T("Widget.Group.OpenMembers"));
        AutomationProperties.SetPositionInSet(
            SelectorButton,
            identity.Index + 1);
        AutomationProperties.SetSizeOfSet(SelectorButton, identity.Count);
        ToolTipService.SetToolTip(SelectorButton, automationName);
    }

    private MenuFlyout CreateMembersFlyout()
    {
        var flyout = new MenuFlyout();
        flyout.Opened += MembersFlyout_Opened;
        flyout.Closed += MembersFlyout_Closed;

        if (_presentation is null)
        {
            return flyout;
        }

        foreach (WidgetGroupMemberPresentation member in _presentation.Members)
        {
            string memberId = member.WidgetId;
            bool isCurrent = string.Equals(
                member.WidgetId,
                _displayedIdentity?.WidgetId,
                StringComparison.Ordinal);
            var item = new MenuFlyoutItem
            {
                Text = member.Name,
                FontWeight = Microsoft.UI.Text.FontWeights.Normal,
                Icon = new FontIcon
                {
                    Glyph = isCurrent ? "\uE73E" : member.Glyph
                }
            };
            int index = FindMemberIndex(
                _presentation.Members,
                member.WidgetId);
            AutomationProperties.SetPositionInSet(item, index + 1);
            AutomationProperties.SetSizeOfSet(
                item,
                _presentation.Members.Count);
            item.Click += (_, _) =>
                MemberInvoked?.Invoke(
                    this,
                    new WidgetGroupMemberEventArgs(
                        memberId,
                        WidgetGroupSwitchOrigin.Picker));
            flyout.Items.Add(item);
        }

        AddGroupCommands(flyout);
        return flyout;
    }

    private void AddGroupCommands(MenuFlyout flyout)
    {
        if (_presentation is null)
        {
            return;
        }

        var reorder = new MenuFlyoutSubItem
        {
            Text = T("Common.Move"),
            Icon = new FontIcon { Glyph = "\uE8AB" }
        };
        for (int index = 0; index < _presentation.Members.Count; index++)
        {
            WidgetGroupMemberPresentation member =
                _presentation.Members[index];
            var memberOrder = new MenuFlyoutSubItem
            {
                Text = member.Name,
                Icon = new FontIcon { Glyph = member.Glyph }
            };

            if (index > 0)
            {
                string previousId =
                    _presentation.Members[index - 1].WidgetId;
                var moveUp = new MenuFlyoutItem
                {
                    Text = T("Widget.Stack.MoveUp"),
                    Icon = new FontIcon { Glyph = "\uE74A" }
                };
                moveUp.Click += (_, _) =>
                    ReorderRequested?.Invoke(
                        this,
                        new WidgetGroupReorderEventArgs(
                            member.WidgetId,
                            previousId));
                memberOrder.Items.Add(moveUp);
            }

            if (index < _presentation.Members.Count - 1)
            {
                string nextId =
                    _presentation.Members[index + 1].WidgetId;
                var moveDown = new MenuFlyoutItem
                {
                    Text = T("Widget.Stack.MoveDown"),
                    Icon = new FontIcon { Glyph = "\uE74B" }
                };
                moveDown.Click += (_, _) =>
                    ReorderRequested?.Invoke(
                        this,
                        new WidgetGroupReorderEventArgs(
                            member.WidgetId,
                            nextId));
                memberOrder.Items.Add(moveDown);
            }

            reorder.Items.Add(memberOrder);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        flyout.Items.Add(reorder);

        string activeMemberId = _presentation.ActiveMemberId;
        var remove = new MenuFlyoutItem
        {
            Text = T("Widget.Group.RemoveCurrent"),
            Icon = new FontIcon { Glyph = "\uE7E8" }
        };
        remove.Click += (_, _) =>
            RemoveMemberRequested?.Invoke(
                this,
                new WidgetGroupMemberEventArgs(activeMemberId));
        flyout.Items.Add(remove);

        var dissolve = new MenuFlyoutItem
        {
            Text = T("Widget.Group.Dissolve"),
            Icon = new FontIcon { Glyph = "\uE711" }
        };
        dissolve.Click += (_, _) =>
            DissolveRequested?.Invoke(this, EventArgs.Empty);
        flyout.Items.Add(dissolve);
    }

    private void MembersFlyout_Opened(object? sender, object e)
    {
        _pickerOpen = true;
        _isSelectorPressed = false;
        UpdateInteractionChrome();
        PickerOpened?.Invoke(this, EventArgs.Empty);
    }

    private void MembersFlyout_Closed(object? sender, object e)
    {
        _pickerOpen = false;
        _isSelectorPressed = false;
        _wheelAccumulator = 0;
        UpdateInteractionChrome();
        if (sender is MenuFlyout flyout)
        {
            flyout.Opened -= MembersFlyout_Opened;
            flyout.Closed -= MembersFlyout_Closed;
        }

        _openFlyout = null;
        PickerClosed?.Invoke(this, EventArgs.Empty);
    }

    private void ClosePicker()
    {
        _openFlyout?.Hide();
        _openFlyout = null;
        _pickerOpen = false;
        _isSelectorPressed = false;
        UpdateInteractionChrome();
    }

    private void SelectorButton_Click(object sender, RoutedEventArgs e)
    {
        if (DateTimeOffset.UtcNow <= _suppressGroupTitleClickUntil)
        {
            return;
        }

        OpenPicker(SelectorButton);
    }

    private static IdentitySnapshot? ResolveActiveIdentity(
        WidgetGroupPresentation? presentation)
    {
        if (presentation is null || presentation.Members.Count == 0)
        {
            return null;
        }

        int activeIndex = FindMemberIndex(
            presentation.Members,
            presentation.ActiveMemberId);
        if (activeIndex < 0)
        {
            activeIndex = FindActiveFlagIndex(presentation.Members);
        }
        if (activeIndex < 0)
        {
            activeIndex = 0;
        }

        WidgetGroupMemberPresentation active =
            presentation.Members[activeIndex];
        return new IdentitySnapshot(
            active.WidgetId,
            active.Name,
            active.Glyph,
            active.IconKind,
            activeIndex,
            presentation.Members.Count);
    }

    private static int FindActiveFlagIndex(
        IReadOnlyList<WidgetGroupMemberPresentation> members)
    {
        for (int index = 0; index < members.Count; index++)
        {
            if (members[index].IsActive)
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindMemberIndex(
        IReadOnlyList<WidgetGroupMemberPresentation> members,
        string widgetId)
    {
        for (int index = 0; index < members.Count; index++)
        {
            if (string.Equals(
                    members[index].WidgetId,
                    widgetId,
                    StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static void SetIdentity(
        WidgetTitleIcon icon,
        TextBlock title,
        IdentitySnapshot? identity)
    {
        icon.Glyph = identity?.Glyph ?? string.Empty;
        icon.IconKind = identity?.IconKind ??
            WidgetTitleIconKindNames.Default;
        icon.LabelText = identity?.Name ?? string.Empty;
        title.Text = identity?.Name ?? string.Empty;
    }

    private static string T(string key)
    {
        return App.Current?.LocalizationService.T(key) ??
               LocalizationService.DefaultText(key);
    }

    private sealed record IdentitySnapshot(
        string WidgetId,
        string Name,
        string Glyph,
        string IconKind,
        int Index,
        int Count);
}
