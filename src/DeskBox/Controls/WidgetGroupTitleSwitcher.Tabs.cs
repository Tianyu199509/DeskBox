using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using VirtualKey = Windows.System.VirtualKey;

namespace DeskBox.Controls;

public sealed partial class WidgetGroupTitleSwitcher
{
    private readonly Dictionary<string, GroupTab> _tabs = new(StringComparer.Ordinal);
    private readonly WidgetGroupTabSelectionState _tabSelection = new();
    private double _inactiveTabHeaderOpacity = 0.65;
    private Windows.UI.Color _tabPrimaryColor = Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private Windows.UI.Color _tabSecondaryColor = Windows.UI.Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF);
    private Windows.UI.Color _tabDisabledColor = Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
    private bool _tabHighContrast;
    private bool _hasExplicitTabColors;
    private bool _isTabDragDisabledByElevation;
    private bool _isSavingTabOrder;
    private bool _tabPointerDown;
    private bool _tabDragCanceled;
    private bool _isTabDragLongPressArmed;
    private bool _allowWindowDragPassthrough;
    private string? _pressedTabId;
    private string? _pointerOverTabId;
    private string? _selectedTabId;
    private string? _pendingTabSelection;
    private string? _reorderTargetMemberId;
    private Windows.Foundation.Point _tabPressStartPoint;
    private Windows.Foundation.Point _lastTabPointerInRoot;
    private long _tabPressTimestampTick64;
    private CancellationTokenSource? _tabLongPressCancellation;
    private LiveTabReorderState? _liveReorderState;
    private DispatcherQueueTimer? _dragEdgeScrollTimer;
    private int _dragEdgeScrollDirection;
    private DispatcherQueueTimer? _ctrlWheelCommitTimer;
    private List<string>? _ctrlWheelOrder;
    private string[]? _ctrlWheelOriginalOrder;
    private string? _ctrlWheelGroupId;
    private double _ctrlWheelAccumulator;
    private DateTimeOffset _ctrlWheelLastInputAt;
    private int _ctrlWheelGestureDirection;
    private bool _ctrlWheelGestureCommitted;
    private DispatcherQueueTimer? _dragHoverSwitchTimer;
    private string? _dragHoverSwitchTargetId;
    private bool _isFileDragStripFreeze;
    private int _fileDragFreezeExitGeneration;

    // Chrome/Edge and VS Code both dwell 500ms before a drag-hover tab
    // switch; Win11 Explorer takes 1-2s. 400ms keeps DeskBox snappier than
    // the browsers while giving the frozen strip a stable, deliberate
    // targeting rhythm instead of a hair trigger.
    private const int DragHoverSwitchDelayMilliseconds = 400;
    private const int TabHoldPreLiftMilliseconds = 80;
    private const int TabHoldReorderArmMilliseconds = 260;
    private const double TabClickSlipToleranceDips = 6.0;
    private const double TabWindowDragDistanceThreshold = 8.0;
    private const double TabHoldHorizontalCreepToleranceDips = 10.0;
    private const int NeighborSlideDurationMilliseconds = 160;
    private const int DropSettleDurationMilliseconds = 140;

    private bool UsesTabs => WidgetGroupNavigationStyles.UsesTabStrip(
        WidgetGroupNavigationStyles.Normalize(NavigationStyle, allowFollowDefault: false));

    private bool UsesEqualTabs => WidgetGroupNavigationStyles.UsesEqualWidthTabs(
        WidgetGroupNavigationStyles.Normalize(NavigationStyle, allowFollowDefault: false));

    private bool IsTabInteractionBusy =>
        _tabPointerDown ||
        _liveReorderState is not null ||
        _isSavingTabOrder ||
        _ctrlWheelOrder is not null;

    internal bool IsInteractiveSource(DependencyObject? source)
    {
        if (source is null || _allowWindowDragPassthrough)
        {
            return false;
        }

        if (UsesTabs)
        {
            if (FindGroupTabBorder(source) is not null)
            {
                return true;
            }

            for (DependencyObject? current = source; current is not null;
                 current = VisualTreeHelper.GetParent(current))
            {
                if (ReferenceEquals(current, ScrollLeftButton) ||
                    ReferenceEquals(current, ScrollRightButton))
                {
                    return true;
                }
                if (ReferenceEquals(current, this))
                {
                    break;
                }
            }

            return false;
        }

        for (DependencyObject? current = source; current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, SelectorButton))
            {
                return true;
            }
        }

        return false;
    }

    internal void SetTabForegroundColors(
        Windows.UI.Color primary,
        Windows.UI.Color secondary,
        Windows.UI.Color disabled,
        bool highContrast)
    {
        _tabPrimaryColor = primary;
        _tabSecondaryColor = secondary;
        _tabDisabledColor = disabled;
        _tabHighContrast = highContrast;
        _hasExplicitTabColors = true;
        _inactiveTabHeaderOpacity = highContrast ? 1 : 0.65;
        UpdateTabVisuals();
    }

    private (Windows.UI.Color Primary, Windows.UI.Color Secondary) ResolveEffectiveTabColors()
    {
        if (_hasExplicitTabColors)
        {
            return (_tabPrimaryColor, _tabSecondaryColor);
        }

        bool isLight = ActualTheme == ElementTheme.Light;
        Windows.UI.Color primary = isLight
            ? Windows.UI.Color.FromArgb(0xE4, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        Windows.UI.Color secondary = isLight
            ? Windows.UI.Color.FromArgb(0x9E, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF);
        return (primary, secondary);
    }

    private void UpdateTabVisuals()
    {
        var (primary, secondary) = ResolveEffectiveTabColors();
        bool equalTabs = UsesEqualTabs;
        if (ScrollLeftButtonIcon is not null)
        {
            ScrollLeftButtonIcon.Foreground = SharedBrushCache.GetOrCreate(primary);
        }
        if (ScrollRightButtonIcon is not null)
        {
            ScrollRightButtonIcon.Foreground = SharedBrushCache.GetOrCreate(primary);
        }

        foreach (var (memberId, row) in _tabs)
        {
            bool isSelected = string.Equals(_selectedTabId, memberId, StringComparison.Ordinal);
            bool isHovered = string.Equals(_pointerOverTabId, memberId, StringComparison.Ordinal);
            bool isPressed = _tabPointerDown && string.Equals(_pressedTabId, memberId, StringComparison.Ordinal);
            bool isLiveDragged = _liveReorderState is { } live &&
                string.Equals(live.DraggedMemberId, memberId, StringComparison.Ordinal);
            bool isLongPressArmed = isPressed && _isTabDragLongPressArmed;

            double topRadius = equalTabs ? 0 : ComputeTabsConcentricCornerRadius();
            row.Tab.BorderThickness = new Thickness(0);
            row.Tab.BorderBrush = SharedBrushCache.GetOrCreate(Colors.Transparent);
            row.Tab.CornerRadius = equalTabs
                ? new CornerRadius(0)
                : new CornerRadius(topRadius, topRadius, 0, 0);

            if (_tabHighContrast)
            {
                row.Tab.Background = SharedBrushCache.GetOrCreate(Colors.Transparent);
                row.Title.Foreground = SharedBrushCache.GetOrCreate(
                    isSelected || isHovered || isLiveDragged ? primary : secondary);
                row.Title.FontWeight = isSelected
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
                row.Title.Opacity = 1;
                row.Icon.Opacity = 1;
                row.Header.Opacity = 1;
            }
            else if (equalTabs)
            {
                // EqualTabs ("等宽并列"): No rounded rectangle pill background.
                // Active state is communicated by highlighted text/icon + vertical cell dividers.
                byte bgAlpha = isLiveDragged
                    ? (byte)0x18
                    : isPressed
                        ? (byte)0x0E
                        : isHovered
                            ? (byte)0x08
                            : (byte)0x00;
                row.Tab.Background = bgAlpha == 0
                    ? SharedBrushCache.GetOrCreate(Colors.Transparent)
                    : SharedBrushCache.GetOrCreate(
                        Windows.UI.Color.FromArgb(bgAlpha, primary.R, primary.G, primary.B));
                row.Title.Foreground = SharedBrushCache.GetOrCreate(
                    isSelected || isHovered || isPressed || isLiveDragged ? primary : secondary);
                row.Title.FontWeight = isSelected
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
                row.Title.Opacity = isSelected || isLiveDragged
                    ? 1.0
                    : isHovered || isPressed
                        ? 0.88
                        : 0.56;
                row.Icon.Opacity = isSelected || isLiveDragged
                    ? 1.0
                    : isHovered || isPressed
                        ? 0.90
                        : 0.65;
                row.Header.Opacity = 1.0;
            }
            else
            {
                // Tabs ("并排显示"): Win11 connected tab look.
                // Selected tab fill and contour are rendered by ConnectedTabFillPath & ConnectedTabContourPath.
                byte bgAlpha = isSelected
                    ? (byte)0x00
                    : isLiveDragged
                        ? (byte)0x1C
                        : isPressed
                            ? (byte)0x14
                            : isHovered
                                ? (byte)0x0E
                                : (byte)0x00;
                row.Tab.Background = bgAlpha == 0
                    ? SharedBrushCache.GetOrCreate(Colors.Transparent)
                    : SharedBrushCache.GetOrCreate(
                        Windows.UI.Color.FromArgb(bgAlpha, primary.R, primary.G, primary.B));
                row.Title.Foreground = SharedBrushCache.GetOrCreate(
                    isSelected || isHovered || isPressed || isLiveDragged ? primary : secondary);
                row.Title.FontWeight = isSelected
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
                row.Title.Opacity = isSelected || isLiveDragged
                    ? 1.0
                    : isHovered || isPressed
                        ? 0.92
                        : 0.64;
                row.Icon.Opacity = isSelected || isLiveDragged
                    ? 1.0
                    : isHovered || isPressed
                        ? 0.92
                        : _inactiveTabHeaderOpacity;
                row.Header.Opacity = 1.0;
            }

            if (!isLiveDragged && row.ActiveScaleStoryboard is null)
            {
                double scale = isLongPressArmed
                    ? 1.02
                    : isPressed
                        ? 0.97
                        : 1.0;
                row.ScaleTransform.ScaleX = scale;
                row.ScaleTransform.ScaleY = scale;
            }
        }

        UpdateOverflowScrollButtons();
        UpdateConnectedTabContour();
    }

    private PointerEventHandler? _tabsPointerPressedHandler;
    private PointerEventHandler? _tabsPointerMovedHandler;
    private PointerEventHandler? _tabsPointerReleasedHandler;
    private PointerEventHandler? _tabsPointerCanceledHandler;
    private PointerEventHandler? _tabsPointerCaptureLostHandler;
    private KeyEventHandler? _tabsKeyDownHandler;
    private DragEventHandler? _tabsDragEnterHandler;
    private DragEventHandler? _tabsDragOverHandler;
    private DragEventHandler? _tabsDragLeaveHandler;
    private DragEventHandler? _tabsDropHandler;

    private void RegisterTabPointerHandlers()
    {
        _tabsPointerPressedHandler = TabsView_PointerPressed;
        _tabsPointerMovedHandler = TabsView_PointerMoved;
        _tabsPointerReleasedHandler = TabsView_PointerReleased;
        _tabsPointerCanceledHandler = TabsView_PointerCanceled;
        _tabsPointerCaptureLostHandler = TabsView_PointerCaptureLost;
        _tabsKeyDownHandler = TabsView_KeyDown;
        _tabsDragEnterHandler = TabsView_DragEnter;
        _tabsDragOverHandler = TabsView_DragOver;
        _tabsDragLeaveHandler = TabsView_DragLeave;
        _tabsDropHandler = TabsView_Drop;
        TabsView.AddHandler(UIElement.PointerPressedEvent, _tabsPointerPressedHandler, handledEventsToo: true);
        TabsView.AddHandler(UIElement.PointerMovedEvent, _tabsPointerMovedHandler, handledEventsToo: true);
        TabsView.AddHandler(UIElement.PointerReleasedEvent, _tabsPointerReleasedHandler, handledEventsToo: true);
        TabsView.AddHandler(UIElement.PointerCanceledEvent, _tabsPointerCanceledHandler, handledEventsToo: true);
        TabsView.AddHandler(UIElement.PointerCaptureLostEvent, _tabsPointerCaptureLostHandler, handledEventsToo: true);
        TabsView.AddHandler(UIElement.KeyDownEvent, _tabsKeyDownHandler, handledEventsToo: true);
        TabsView.AddHandler(UIElement.DragEnterEvent, _tabsDragEnterHandler, handledEventsToo: true);
        TabsView.AddHandler(UIElement.DragOverEvent, _tabsDragOverHandler, handledEventsToo: true);
        TabsView.AddHandler(UIElement.DragLeaveEvent, _tabsDragLeaveHandler, handledEventsToo: true);
        TabsView.AddHandler(UIElement.DropEvent, _tabsDropHandler, handledEventsToo: true);
    }

    private void UnregisterTabPointerHandlers()
    {
        StopDragEdgeScrollTimer();
        StopCtrlWheelCommitTimer();
        if (_tabsPointerPressedHandler is not null)
        {
            TabsView.RemoveHandler(UIElement.PointerPressedEvent, _tabsPointerPressedHandler);
            _tabsPointerPressedHandler = null;
        }
        if (_tabsPointerMovedHandler is not null)
        {
            TabsView.RemoveHandler(UIElement.PointerMovedEvent, _tabsPointerMovedHandler);
            _tabsPointerMovedHandler = null;
        }
        if (_tabsPointerReleasedHandler is not null)
        {
            TabsView.RemoveHandler(UIElement.PointerReleasedEvent, _tabsPointerReleasedHandler);
            _tabsPointerReleasedHandler = null;
        }
        if (_tabsPointerCanceledHandler is not null)
        {
            TabsView.RemoveHandler(UIElement.PointerCanceledEvent, _tabsPointerCanceledHandler);
            _tabsPointerCanceledHandler = null;
        }
        if (_tabsPointerCaptureLostHandler is not null)
        {
            TabsView.RemoveHandler(UIElement.PointerCaptureLostEvent, _tabsPointerCaptureLostHandler);
            _tabsPointerCaptureLostHandler = null;
        }
        if (_tabsKeyDownHandler is not null)
        {
            TabsView.RemoveHandler(UIElement.KeyDownEvent, _tabsKeyDownHandler);
            _tabsKeyDownHandler = null;
        }
        if (_tabsDragEnterHandler is not null)
        {
            TabsView.RemoveHandler(UIElement.DragEnterEvent, _tabsDragEnterHandler);
            _tabsDragEnterHandler = null;
        }
        if (_tabsDragOverHandler is not null)
        {
            TabsView.RemoveHandler(UIElement.DragOverEvent, _tabsDragOverHandler);
            _tabsDragOverHandler = null;
        }
        if (_tabsDragLeaveHandler is not null)
        {
            TabsView.RemoveHandler(UIElement.DragLeaveEvent, _tabsDragLeaveHandler);
            _tabsDragLeaveHandler = null;
        }
        if (_tabsDropHandler is not null)
        {
            TabsView.RemoveHandler(UIElement.DropEvent, _tabsDropHandler);
            _tabsDropHandler = null;
        }
    }

    private void SynchronizeTabs()
    {
        if (TabsView is null || IsTabInteractionBusy)
        {
            return;
        }
        if (_presentation is null)
        {
            ClearTabs();
            return;
        }
        if (!UsesTabs)
        {
            return;
        }

        bool equalTabs = UsesEqualTabs;
        TabsScrollViewer.HorizontalScrollMode = equalTabs
            ? ScrollMode.Disabled
            : ScrollMode.Enabled;
        TabsView.HorizontalAlignment = equalTabs
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;
        TabsView.ColumnSpacing = equalTabs ? 0 : 2;
        if (equalTabs && TabsScrollViewer.HorizontalOffset > 0)
        {
            TabsScrollViewer.ChangeView(0, null, null, disableAnimation: true);
        }

        var memberIds = _presentation.Members.Select(member => member.WidgetId)
            .ToHashSet(StringComparer.Ordinal);
        if (!_isFileDragStripFreeze)
        {
            foreach (string removedId in _tabs.Keys.Where(id => !memberIds.Contains(id)).ToArray())
            {
                StopTabAnimations(_tabs[removedId]);
                DetachGroupTabHandlers(_tabs[removedId].Tab);
                TabsView.Children.Remove(_tabs[removedId].Tab);
                _tabs.Remove(removedId);
            }

            int memberCount = _presentation.Members.Count;
            while (TabsView.ColumnDefinitions.Count > memberCount)
            {
                TabsView.ColumnDefinitions.RemoveAt(TabsView.ColumnDefinitions.Count - 1);
            }
            while (TabsView.ColumnDefinitions.Count < memberCount)
            {
                TabsView.ColumnDefinitions.Add(new ColumnDefinition());
            }

            GridLength targetColumnWidth = equalTabs
                ? new GridLength(1, GridUnitType.Star)
                : GridLength.Auto;
            for (int colIndex = 0; colIndex < TabsView.ColumnDefinitions.Count; colIndex++)
            {
                if (TabsView.ColumnDefinitions[colIndex].Width != targetColumnWidth)
                {
                    TabsView.ColumnDefinitions[colIndex].Width = targetColumnWidth;
                }
            }
        }

        string displayMode = WidgetGroupTitleDisplayModes.Normalize(
            DisplayMode, allowFollowDefault: false);
        bool showIcon = displayMode != WidgetGroupTitleDisplayModes.TextOnly &&
            WidgetTitleIconModeNames.NormalizeMode(TitleIconMode) != WidgetTitleIconMode.Hidden;
        bool showText = displayMode != WidgetGroupTitleDisplayModes.IconOnly || !showIcon;

        for (int index = 0; index < _presentation.Members.Count; index++)
        {
            WidgetGroupMemberPresentation member = _presentation.Members[index];
            if (!_tabs.TryGetValue(member.WidgetId, out GroupTab? row))
            {
                row = CreateGroupTab(member.WidgetId);
                _tabs.Add(member.WidgetId, row);
            }

            if (!_isFileDragStripFreeze)
            {
                if (!TabsView.Children.Contains(row.Tab))
                {
                    TabsView.Children.Add(row.Tab);
                }
                Grid.SetColumn(row.Tab, index);
                Canvas.SetZIndex(row.Tab, 1);
                row.DragTranslateTransform.X = 0;
                row.DragTranslateTransform.Y = 0;
                row.Tab.Opacity = 1;
            }

            ApplyGroupTabLayoutMode(row, equalTabs);
            row.Icon.Glyph = member.Glyph;
            row.Icon.IconKind = member.IconKind;
            row.Icon.CustomEmoji = member.CustomTitleIconEmoji;
            row.Icon.CustomImageSource = WidgetTitleIconImageSourceFactory
                .TryCreate(member.CustomTitleIconImagePath);
            row.Icon.Mode = TitleIconMode;
            row.Icon.AccentColor = TitleIconAccentColor;
            row.Icon.IconSize = IconSize;
            row.Icon.Visibility = showIcon ? Visibility.Visible : Visibility.Collapsed;
            row.Icon.Margin = showIcon && showText
                ? new Thickness(0, 0, 6, 0)
                : new Thickness(0);
            row.Title.Text = member.Name;
            row.Title.FontSize = CurrentTitle.FontSize;
            row.Title.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
            ToolTipService.SetToolTip(row.Tab, member.Name);
            AutomationProperties.SetName(row.Tab, member.Name);
            AutomationProperties.SetPositionInSet(row.Tab, index + 1);
            AutomationProperties.SetSizeOfSet(row.Tab, _presentation.Members.Count);
        }

        string? previousSelectedId = _selectedTabId;
        _selectedTabId = _tabSelection.ResolveSelection(
            _presentation.GroupId, _presentation.ActiveMemberId, memberIds);
        UpdateTabVisuals();
        if (!equalTabs &&
            _selectedTabId is not null &&
            !string.Equals(previousSelectedId, _selectedTabId, StringComparison.Ordinal))
        {
            ScrollTabIntoView(_selectedTabId, animate: true);
        }
    }

    private void ApplyGroupTabLayoutMode(GroupTab row, bool equalTabs)
    {
        double pillHeight = equalTabs
            ? Math.Max(28, _titleBarContentHeight)
            : Math.Max(24, _titleBarContentHeight - TabsModeOuterInset);
        row.Tab.MinHeight = pillHeight;
        row.Tab.VerticalAlignment = VerticalAlignment.Stretch;
        if (equalTabs)
        {
            row.Tab.HorizontalAlignment = HorizontalAlignment.Stretch;
            row.Tab.Margin = new Thickness(0);
            row.Tab.MinWidth = 0;
            row.Tab.MaxWidth = double.PositiveInfinity;
            row.Tab.Padding = new Thickness(8, 0, 8, 0);
            row.Header.HorizontalAlignment = HorizontalAlignment.Center;
            row.Title.MaxWidth = double.PositiveInfinity;
        }
        else
        {
            row.Tab.HorizontalAlignment = HorizontalAlignment.Left;
            row.Tab.Margin = new Thickness(0, TabsModeOuterInset, 0, 0);
            row.Tab.MinWidth = 64;
            row.Tab.MaxWidth = 160;
            row.Tab.Padding = new Thickness(11, 0, 11, 0);
            row.Header.HorizontalAlignment = HorizontalAlignment.Center;
            row.Title.MaxWidth = MaximumTitleWidth;
        }
    }

    private GroupTab CreateGroupTab(string memberId)
    {
        var icon = new WidgetTitleIcon { IsHitTestVisible = false };
        var title = new TextBlock
        {
            MaxWidth = MaximumTitleWidth,
            FontWeight = Microsoft.UI.Text.FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var loading = new ProgressRing
        {
            Width = 10,
            Height = 10,
            Margin = new Thickness(6, 0, 0, 0),
            IsActive = false,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        var header = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            MinHeight = 22
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 1);
        Grid.SetColumn(loading, 2);
        header.Children.Add(icon);
        header.Children.Add(title);
        header.Children.Add(loading);

        var scaleTransform = new ScaleTransform { ScaleX = 1, ScaleY = 1 };
        var translateTransform = new TranslateTransform { X = 0, Y = 0 };
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(scaleTransform);
        transformGroup.Children.Add(translateTransform);

        double pillHeight = Math.Max(24, _titleBarContentHeight - TabsModeOuterInset);
        double topRadius = ComputeTabsConcentricCornerRadius();
        var tab = new Border
        {
            Child = header,
            Tag = memberId,
            MinHeight = pillHeight,
            CornerRadius = new CornerRadius(topRadius, topRadius, 0, 0),
            BorderThickness = new Thickness(0),
            Background = SharedBrushCache.GetOrCreate(Colors.Transparent),
            BorderBrush = SharedBrushCache.GetOrCreate(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Stretch,
            AllowDrop = true,
            CanDrag = false,
            RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            RenderTransform = transformGroup
        };
        tab.PointerEntered += GroupTab_PointerEntered;
        tab.PointerExited += GroupTab_PointerExited;
        return new GroupTab(tab, header, scaleTransform, translateTransform, icon, title, loading);
    }

    private void DetachGroupTabHandlers(Border tab)
    {
        tab.PointerEntered -= GroupTab_PointerEntered;
        tab.PointerExited -= GroupTab_PointerExited;
    }

    private void GroupTab_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string memberId })
        {
            return;
        }

        _pointerOverTabId = memberId;
        UpdateTabVisuals();
        if (HoverSwitchEnabled && !IsTabInteractionBusy &&
            _presentation?.ActiveMemberId != memberId)
        {
            BeginTabHoverSwitch(memberId);
        }
    }

    private void GroupTab_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border { Tag: string memberId })
        {
            return;
        }

        if (string.Equals(_pointerOverTabId, memberId, StringComparison.Ordinal))
        {
            _pointerOverTabId = null;
            UpdateTabVisuals();
        }
        CancelTabHoverSwitch(memberId);
    }

    private void ClearTabs()
    {
        if (_liveReorderState is not null || _isSavingTabOrder)
        {
            _tabDragCanceled = true;
            return;
        }
        CancelTabPress(releasePointerCapture: true);
        StopDragEdgeScrollTimer();
        StopCtrlWheelCommitTimer();
        foreach (GroupTab row in _tabs.Values)
        {
            StopTabAnimations(row);
            DetachGroupTabHandlers(row.Tab);
        }
        TabsView.Children.Clear();
        TabsView.ColumnDefinitions.Clear();
        _tabs.Clear();
        _tabSelection.Reset();
        _selectedTabId = null;
        _pointerOverTabId = null;
        _pendingTabSelection = null;
        _reorderTargetMemberId = null;
        if (ConnectedTabContourPath is not null)
        {
            ConnectedTabContourPath.Data = null;
        }
        if (ConnectedTabFillPath is not null)
        {
            ConnectedTabFillPath.Data = null;
        }
        if (InactiveSeparatorsPath is not null)
        {
            InactiveSeparatorsPath.Data = null;
        }
        if (ScrollLeftButton is not null)
        {
            ScrollLeftButton.Visibility = Visibility.Collapsed;
        }
        if (ScrollRightButton is not null)
        {
            ScrollRightButton.Visibility = Visibility.Collapsed;
        }
    }

    private void CompleteTabInvocation(string memberId, bool succeeded, long requestVersion)
    {
        if (_tabSelection.Complete(requestVersion, memberId, succeeded, _presentation?.ActiveMemberId))
        {
            SynchronizeTabs();
        }
    }

    private void SetTabLoading(string? memberId, bool isLoading)
    {
        foreach (var (id, row) in _tabs)
        {
            if (isLoading || memberId is null || memberId == id)
            {
                bool show = isLoading && memberId == id;
                row.Loading.IsActive = show;
                row.Loading.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private void CommitPendingTabSelection()
    {
        if (IsTabInteractionBusy || _pendingTabSelection is not { } memberId)
        {
            return;
        }
        _pendingTabSelection = null;
        if (_tabSelection.PendingMemberId == memberId)
        {
            return;
        }
        if (UsesTabs && _presentation is { } presentation &&
            (presentation.ActiveMemberId != memberId || _tabSelection.PendingMemberId is not null) &&
            presentation.Members.Any(member => member.WidgetId == memberId))
        {
            long requestVersion = _tabSelection.Request(presentation.GroupId, memberId);
            _selectedTabId = memberId;
            UpdateTabVisuals();
            if (!UsesEqualTabs)
            {
                ScrollTabIntoView(memberId, animate: true);
            }
            MemberInvoked?.Invoke(this, new WidgetGroupMemberEventArgs(memberId)
            {
                TabSelectionRequestVersion = requestVersion
            });
        }
    }

    private Border? FindGroupTabBorder(object? source)
    {
        for (DependencyObject? current = source as DependencyObject; current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is Border { Tag: string memberId } border &&
                _tabs.TryGetValue(memberId, out GroupTab? row) &&
                ReferenceEquals(row.Tab, border))
            {
                return border;
            }
            if (ReferenceEquals(current, TabsView) || ReferenceEquals(current, this))
            {
                break;
            }
        }
        return null;
    }

    // =========================================================================
    // Overflow Left/Right Scroll Buttons & Connected Contour Rendering
    // =========================================================================

    private void TabsRoot_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateOverflowScrollButtons();
        UpdateConnectedTabContour();
    }

    private void TabsScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateOverflowScrollButtons();
        UpdateConnectedTabContour();
    }

    private void TabsScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateOverflowScrollButtons();
        UpdateConnectedTabContour();
    }

    private void TabsView_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateOverflowScrollButtons();
        UpdateConnectedTabContour();
    }

    private void ScrollLeftButton_Click(object sender, RoutedEventArgs e)
    {
        ScrollTabStripByStep(-1);
    }

    private void ScrollRightButton_Click(object sender, RoutedEventArgs e)
    {
        ScrollTabStripByStep(1);
    }

    private void ScrollTabStripByStep(int direction)
    {
        if (UsesEqualTabs || TabsScrollViewer is null)
        {
            return;
        }

        double step = Math.Max(76, TabsScrollViewer.ViewportWidth * 0.55);
        double target = Math.Clamp(
            TabsScrollViewer.HorizontalOffset + (direction * step),
            0,
            Math.Max(0, TabsScrollViewer.ScrollableWidth));
        TabsScrollViewer.ChangeView(target, null, null, disableAnimation: false);
    }

    internal bool TryHandleOverflowWheelScroll(double delta)
    {
        if (!UsesTabs || UsesEqualTabs || TabsScrollViewer is null ||
            TabsScrollViewer.ScrollableWidth <= 0.5 || Math.Abs(delta) < 0.01)
        {
            return false;
        }

        double step = (-delta / 120.0) * 64.0;
        double target = Math.Clamp(
            TabsScrollViewer.HorizontalOffset + step,
            0,
            TabsScrollViewer.ScrollableWidth);
        TabsScrollViewer.ChangeView(target, null, null, disableAnimation: false);
        return true;
    }

    private void ScrollTabIntoView(string memberId, bool animate)
    {
        if (UsesEqualTabs || TabsScrollViewer is null || !_tabs.TryGetValue(memberId, out GroupTab? row))
        {
            return;
        }

        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (UsesEqualTabs ||
                TabsScrollViewer is null ||
                TabsScrollViewer.ScrollableWidth <= 0.5 ||
                !_tabs.TryGetValue(memberId, out GroupTab? targetRow) ||
                targetRow.Tab.ActualWidth <= 0)
            {
                return;
            }

            try
            {
                GeneralTransform transform = targetRow.Tab.TransformToVisual(TabsView);
                Windows.Foundation.Point topLeft = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                double tabLeft = topLeft.X;
                double tabRight = tabLeft + targetRow.Tab.ActualWidth;
                double viewportWidth = TabsScrollViewer.ViewportWidth;
                if (viewportWidth <= 0)
                {
                    return;
                }

                const double breathingMargin = 14.0;
                double currentOffset = TabsScrollViewer.HorizontalOffset;
                double desiredOffset = currentOffset;
                if (tabLeft - breathingMargin < currentOffset)
                {
                    desiredOffset = Math.Max(0, tabLeft - breathingMargin);
                }
                else if (tabRight + breathingMargin > currentOffset + viewportWidth)
                {
                    desiredOffset = Math.Min(
                        TabsScrollViewer.ScrollableWidth,
                        tabRight + breathingMargin - viewportWidth);
                }

                if (Math.Abs(desiredOffset - currentOffset) > 0.5)
                {
                    TabsScrollViewer.ChangeView(
                        desiredOffset,
                        null,
                        null,
                        disableAnimation: !animate || !AreSystemAnimationsEnabled());
                }
            }
            catch (InvalidOperationException)
            {
            }
        });
    }

    private void UpdateOverflowScrollButtons()
    {
        if (ScrollLeftButton is null || ScrollRightButton is null || TabsScrollViewer is null || TabsRoot is null)
        {
            return;
        }

        if (!UsesTabs || UsesEqualTabs || _presentation is null || _presentation.Members.Count < 2 || TabsRoot.ActualWidth <= 0)
        {
            ScrollLeftButton.Visibility = Visibility.Collapsed;
            ScrollRightButton.Visibility = Visibility.Collapsed;
            return;
        }

        double contentWidth = Math.Max(TabsView.ActualWidth, TabsView.DesiredSize.Width);
        bool overflows = contentWidth > TabsRoot.ActualWidth + 1.5 ||
            TabsScrollViewer.ScrollableWidth > 0.5;
        Visibility targetVisibility = overflows ? Visibility.Visible : Visibility.Collapsed;
        if (ScrollLeftButton.Visibility != targetVisibility)
        {
            ScrollLeftButton.Visibility = targetVisibility;
        }
        if (ScrollRightButton.Visibility != targetVisibility)
        {
            ScrollRightButton.Visibility = targetVisibility;
        }

        if (!overflows)
        {
            return;
        }

        double offset = TabsScrollViewer.HorizontalOffset;
        double maxOffset = Math.Max(0, TabsScrollViewer.ScrollableWidth);
        bool canScrollLeft = offset > 0.5;
        bool canScrollRight = maxOffset <= 0.5
            ? contentWidth > TabsScrollViewer.ActualWidth + 1.5
            : offset < maxOffset - 0.5;

        ScrollLeftButton.IsEnabled = canScrollLeft;
        ScrollRightButton.IsEnabled = canScrollRight;
        ScrollLeftButton.Opacity = canScrollLeft ? 0.88 : 0.28;
        ScrollRightButton.Opacity = canScrollRight ? 0.88 : 0.28;
    }

    private static double SnapToDevicePixelCenter(double coordinate, double scale)
    {
        return (Math.Round((coordinate * scale) - 0.5) + 0.5) / scale;
    }

    private FrameworkElement? FindAncestorTitleBarGrid()
    {
        for (DependencyObject? current = this; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement element &&
                string.Equals(element.Name, "TitleBarGrid", StringComparison.Ordinal))
            {
                return element;
            }
        }

        return null;
    }

    private void UpdateConnectedTabContour()
    {
        if (ConnectedTabContourPath is null ||
            ConnectedTabFillPath is null ||
            InactiveSeparatorsPath is null ||
            TabsRoot is null)
        {
            return;
        }

        if (!UsesTabs || _presentation is null || _presentation.Members.Count == 0 ||
            TabsRoot.ActualWidth <= 0 || TabsRoot.ActualHeight <= 0)
        {
            ConnectedTabContourPath.Data = null;
            ConnectedTabFillPath.Data = null;
            InactiveSeparatorsPath.Data = null;
            return;
        }

        double scale = XamlRoot?.RasterizationScale is > 0 ? XamlRoot.RasterizationScale : 1.0;
        var (primary, _) = ResolveEffectiveTabColors();
        byte contourAlpha = _tabHighContrast ? (byte)0xFF : (byte)0x2E;
        byte separatorAlpha = _tabHighContrast ? (byte)0xFF : (byte)0x22;
        ConnectedTabContourPath.Stroke = SharedBrushCache.GetOrCreate(
            Windows.UI.Color.FromArgb(contourAlpha, primary.R, primary.G, primary.B));
        InactiveSeparatorsPath.Stroke = SharedBrushCache.GetOrCreate(
            Windows.UI.Color.FromArgb(separatorAlpha, primary.R, primary.G, primary.B));

        double lineStartX = 0;
        double lineEndX = TabsRoot.ActualWidth;
        if (FindAncestorTitleBarGrid() is { ActualWidth: > 0 } titleBarGrid)
        {
            try
            {
                GeneralTransform titleTransform = titleBarGrid.TransformToVisual(TabsRoot);
                lineStartX = titleTransform.TransformPoint(new Windows.Foundation.Point(0, 0)).X;
                lineEndX = titleTransform.TransformPoint(new Windows.Foundation.Point(titleBarGrid.ActualWidth, 0)).X;
            }
            catch (InvalidOperationException)
            {
            }
        }

        double bottomY = SnapToDevicePixelCenter(Math.Max(1, TabsRoot.ActualHeight - 0.5), scale);

        if (UsesEqualTabs)
        {
            ConnectedTabFillPath.Data = null;
            InactiveSeparatorsPath.Data = null;

            var equalGeometry = new PathGeometry();
            var baselineFigure = new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(lineStartX, bottomY),
                IsClosed = false,
                IsFilled = false
            };
            baselineFigure.Segments.Add(new LineSegment
            {
                Point = new Windows.Foundation.Point(lineEndX, bottomY)
            });
            equalGeometry.Figures.Add(baselineFigure);

            // Centered half-height vertical dividers matching the text/icon height
            double textHeight = Math.Clamp(Math.Max(CurrentTitle.FontSize, 13.0) * 1.08, 14.0, 17.0);
            double midY = bottomY * 0.5;
            double divTop = SnapToDevicePixelCenter(Math.Max(4.0, midY - (textHeight * 0.5)), scale);
            double divBottom = SnapToDevicePixelCenter(Math.Min(bottomY - 4.0, midY + (textHeight * 0.5)), scale);

            int count = _presentation.Members.Count;
            if (count > 1 && TabsView.ActualWidth > 0)
            {
                double slotWidth = TabsView.ActualWidth / count;
                for (int i = 1; i < count; i++)
                {
                    double x = SnapToDevicePixelCenter(i * slotWidth, scale);
                    var divider = new PathFigure
                    {
                        StartPoint = new Windows.Foundation.Point(x, divTop),
                        IsClosed = false,
                        IsFilled = false
                    };
                    divider.Segments.Add(new LineSegment
                    {
                        Point = new Windows.Foundation.Point(x, divBottom)
                    });
                    equalGeometry.Figures.Add(divider);
                }
            }

            // When right-side action buttons are visible, draw the vertical divider
            // between the last equal tab and the action button area with the same half-height.
            if (lineEndX > TabsRoot.ActualWidth + 4)
            {
                double rightBoundaryX = SnapToDevicePixelCenter(TabsRoot.ActualWidth, scale);
                var rightDivider = new PathFigure
                {
                    StartPoint = new Windows.Foundation.Point(rightBoundaryX, divTop),
                    IsClosed = false,
                    IsFilled = false
                };
                rightDivider.Segments.Add(new LineSegment
                {
                    Point = new Windows.Foundation.Point(rightBoundaryX, divBottom)
                });
                equalGeometry.Figures.Add(rightDivider);
            }

            ConnectedTabContourPath.Data = equalGeometry;
            return;
        }

        // "并排显示" (Tabs mode): Win11 connected tab contour + inactive short separators.
        double viewportLeft = 0;
        double viewportRight = TabsRoot.ActualWidth;
        if (TabsScrollViewer.ActualWidth > 0)
        {
            try
            {
                GeneralTransform vpTransform = TabsScrollViewer.TransformToVisual(TabsRoot);
                viewportLeft = vpTransform.TransformPoint(new Windows.Foundation.Point(0, 0)).X;
                viewportRight = vpTransform.TransformPoint(new Windows.Foundation.Point(TabsScrollViewer.ActualWidth, 0)).X;
            }
            catch (InvalidOperationException)
            {
            }
        }

        bool selectedDetachingOutside = _liveReorderState is { IsDetachingOutside: true } detach &&
            string.Equals(detach.DraggedMemberId, _selectedTabId, StringComparison.Ordinal);

        bool drewConnectedArch = false;
        if (!selectedDetachingOutside &&
            _selectedTabId is not null &&
            _tabs.TryGetValue(_selectedTabId, out GroupTab? selectedRow) &&
            selectedRow.Tab.ActualWidth > 0)
        {
            try
            {
                GeneralTransform tabTransform = selectedRow.Tab.TransformToVisual(TabsRoot);
                double rawLeft = tabTransform.TransformPoint(new Windows.Foundation.Point(0, 0)).X;
                double rawRight = tabTransform.TransformPoint(new Windows.Foundation.Point(selectedRow.Tab.ActualWidth, 0)).X;

                double clipLeft = ScrollLeftButton.Visibility == Visibility.Visible ? viewportLeft + 1 : lineStartX + 1;
                double clipRight = ScrollRightButton.Visibility == Visibility.Visible ? viewportRight - 1 : lineEndX - 1;
                double archLeft = SnapToDevicePixelCenter(Math.Clamp(rawLeft, clipLeft, clipRight), scale);
                double archRight = SnapToDevicePixelCenter(Math.Clamp(rawRight, clipLeft, clipRight), scale);

                if (archRight - archLeft >= 18)
                {
                    drewConnectedArch = true;
                    double topY = SnapToDevicePixelCenter(TabsModeOuterInset, scale);
                    double tabWidth = archRight - archLeft;
                    double desiredTopR = ComputeTabsConcentricCornerRadius();
                    double topR = Math.Min(desiredTopR, Math.Max(3.0, (tabWidth - 6.0) * 0.5));
                    double leftFootR = Math.Min(4.0, Math.Max(0.0, archLeft - lineStartX));
                    double rightFootR = Math.Min(4.0, Math.Max(0.0, lineEndX - archRight));

                    var contourGeometry = new PathGeometry();
                    var contourFigure = new PathFigure
                    {
                        StartPoint = new Windows.Foundation.Point(lineStartX, bottomY),
                        IsClosed = false,
                        IsFilled = false
                    };
                    contourFigure.Segments.Add(new LineSegment
                    {
                        Point = new Windows.Foundation.Point(archLeft - leftFootR, bottomY)
                    });
                    if (leftFootR > 0.5)
                    {
                        contourFigure.Segments.Add(new ArcSegment
                        {
                            Point = new Windows.Foundation.Point(archLeft, bottomY - leftFootR),
                            Size = new Windows.Foundation.Size(leftFootR, leftFootR),
                            SweepDirection = SweepDirection.Counterclockwise,
                            IsLargeArc = false
                        });
                    }
                    contourFigure.Segments.Add(new LineSegment
                    {
                        Point = new Windows.Foundation.Point(archLeft, topY + topR)
                    });
                    contourFigure.Segments.Add(new ArcSegment
                    {
                        Point = new Windows.Foundation.Point(archLeft + topR, topY),
                        Size = new Windows.Foundation.Size(topR, topR),
                        SweepDirection = SweepDirection.Clockwise,
                        IsLargeArc = false
                    });
                    contourFigure.Segments.Add(new LineSegment
                    {
                        Point = new Windows.Foundation.Point(archRight - topR, topY)
                    });
                    contourFigure.Segments.Add(new ArcSegment
                    {
                        Point = new Windows.Foundation.Point(archRight, topY + topR),
                        Size = new Windows.Foundation.Size(topR, topR),
                        SweepDirection = SweepDirection.Clockwise,
                        IsLargeArc = false
                    });
                    contourFigure.Segments.Add(new LineSegment
                    {
                        Point = new Windows.Foundation.Point(archRight, bottomY - rightFootR)
                    });
                    if (rightFootR > 0.5)
                    {
                        contourFigure.Segments.Add(new ArcSegment
                        {
                            Point = new Windows.Foundation.Point(archRight + rightFootR, bottomY),
                            Size = new Windows.Foundation.Size(rightFootR, rightFootR),
                            SweepDirection = SweepDirection.Counterclockwise,
                            IsLargeArc = false
                        });
                    }
                    contourFigure.Segments.Add(new LineSegment
                    {
                        Point = new Windows.Foundation.Point(lineEndX, bottomY)
                    });
                    contourGeometry.Figures.Add(contourFigure);
                    ConnectedTabContourPath.Data = contourGeometry;

                    if (_tabHighContrast)
                    {
                        ConnectedTabFillPath.Data = null;
                    }
                    else
                    {
                        bool selectedHovered = string.Equals(_pointerOverTabId, _selectedTabId, StringComparison.Ordinal);
                        byte fillAlpha = selectedHovered ? (byte)0x1A : (byte)0x12;
                        ConnectedTabFillPath.Fill = SharedBrushCache.GetOrCreate(
                            Windows.UI.Color.FromArgb(fillAlpha, primary.R, primary.G, primary.B));

                        var fillGeometry = new PathGeometry();
                        var fillFigure = new PathFigure
                        {
                            StartPoint = new Windows.Foundation.Point(archLeft - leftFootR, bottomY),
                            IsClosed = true,
                            IsFilled = true
                        };
                        if (leftFootR > 0.5)
                        {
                            fillFigure.Segments.Add(new ArcSegment
                            {
                                Point = new Windows.Foundation.Point(archLeft, bottomY - leftFootR),
                                Size = new Windows.Foundation.Size(leftFootR, leftFootR),
                                SweepDirection = SweepDirection.Counterclockwise,
                                IsLargeArc = false
                            });
                        }
                        fillFigure.Segments.Add(new LineSegment
                        {
                            Point = new Windows.Foundation.Point(archLeft, topY + topR)
                        });
                        fillFigure.Segments.Add(new ArcSegment
                        {
                            Point = new Windows.Foundation.Point(archLeft + topR, topY),
                            Size = new Windows.Foundation.Size(topR, topR),
                            SweepDirection = SweepDirection.Clockwise,
                            IsLargeArc = false
                        });
                        fillFigure.Segments.Add(new LineSegment
                        {
                            Point = new Windows.Foundation.Point(archRight - topR, topY)
                        });
                        fillFigure.Segments.Add(new ArcSegment
                        {
                            Point = new Windows.Foundation.Point(archRight, topY + topR),
                            Size = new Windows.Foundation.Size(topR, topR),
                            SweepDirection = SweepDirection.Clockwise,
                            IsLargeArc = false
                        });
                        fillFigure.Segments.Add(new LineSegment
                        {
                            Point = new Windows.Foundation.Point(archRight, bottomY - rightFootR)
                        });
                        if (rightFootR > 0.5)
                        {
                            fillFigure.Segments.Add(new ArcSegment
                            {
                                Point = new Windows.Foundation.Point(archRight + rightFootR, bottomY),
                                Size = new Windows.Foundation.Size(rightFootR, rightFootR),
                                SweepDirection = SweepDirection.Counterclockwise,
                                IsLargeArc = false
                            });
                        }
                        fillGeometry.Figures.Add(fillFigure);
                        ConnectedTabFillPath.Data = fillGeometry;
                    }
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        if (!drewConnectedArch)
        {
            ConnectedTabFillPath.Data = null;
            var fallbackGeometry = new PathGeometry();
            var fallbackFigure = new PathFigure
            {
                StartPoint = new Windows.Foundation.Point(lineStartX, bottomY),
                IsClosed = false,
                IsFilled = false
            };
            fallbackFigure.Segments.Add(new LineSegment
            {
                Point = new Windows.Foundation.Point(lineEndX, bottomY)
            });
            fallbackGeometry.Figures.Add(fallbackFigure);
            ConnectedTabContourPath.Data = fallbackGeometry;
        }

        // Short vertical separators between adjacent inactive, non-hovered tabs
        IReadOnlyList<string> orderedIds =
            (IReadOnlyList<string>?)_liveReorderState?.CurrentOrder ??
            (IReadOnlyList<string>?)_ctrlWheelOrder ??
            _presentation.Members.Select(static m => m.WidgetId).ToList();

        var sepGeometry = new PathGeometry();
        double centerY = (TabsModeOuterInset + bottomY) * 0.5;
        double sepHalfH = Math.Clamp(Math.Max(CurrentTitle.FontSize, 13.0) * 0.52, 6.5, 8.0);
        double sepTop = SnapToDevicePixelCenter(Math.Max(TabsModeOuterInset + 2.0, centerY - sepHalfH), scale);
        double sepBottom = SnapToDevicePixelCenter(Math.Min(bottomY - 2.0, centerY + sepHalfH), scale);

        for (int i = 0; i < orderedIds.Count - 1; i++)
        {
            string leftId = orderedIds[i];
            string rightId = orderedIds[i + 1];
            if (string.Equals(leftId, _selectedTabId, StringComparison.Ordinal) ||
                string.Equals(rightId, _selectedTabId, StringComparison.Ordinal) ||
                string.Equals(leftId, _pointerOverTabId, StringComparison.Ordinal) ||
                string.Equals(rightId, _pointerOverTabId, StringComparison.Ordinal) ||
                string.Equals(leftId, _liveReorderState?.DraggedMemberId, StringComparison.Ordinal) ||
                string.Equals(rightId, _liveReorderState?.DraggedMemberId, StringComparison.Ordinal))
            {
                continue;
            }

            if (!_tabs.TryGetValue(leftId, out GroupTab? leftRow) ||
                !_tabs.TryGetValue(rightId, out GroupTab? rightRow) ||
                leftRow.Tab.ActualWidth <= 0 ||
                rightRow.Tab.ActualWidth <= 0)
            {
                continue;
            }

            try
            {
                double leftRightEdge = leftRow.Tab
                    .TransformToVisual(TabsRoot)
                    .TransformPoint(new Windows.Foundation.Point(leftRow.Tab.ActualWidth, 0)).X;
                double rightLeftEdge = rightRow.Tab
                    .TransformToVisual(TabsRoot)
                    .TransformPoint(new Windows.Foundation.Point(0, 0)).X;
                double sepX = SnapToDevicePixelCenter((leftRightEdge + rightLeftEdge) * 0.5, scale);
                if (sepX >= viewportLeft + 2 && sepX <= viewportRight - 2)
                {
                    var sepFigure = new PathFigure
                    {
                        StartPoint = new Windows.Foundation.Point(sepX, sepTop),
                        IsClosed = false,
                        IsFilled = false
                    };
                    sepFigure.Segments.Add(new LineSegment
                    {
                        Point = new Windows.Foundation.Point(sepX, sepBottom)
                    });
                    sepGeometry.Figures.Add(sepFigure);
                }
            }
            catch (InvalidOperationException)
            {
            }
        }

        InactiveSeparatorsPath.Data = sepGeometry.Figures.Count > 0 ? sepGeometry : null;
    }

    // =========================================================================
    // Pointer-Captured Live Reorder, Neighbor Slide-Aside & Tear-Out Detach
    // =========================================================================

    private void TabsView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        ExitFileDragStripFreeze();
        if (_liveReorderState is not null || _isSavingTabOrder)
        {
            return;
        }

        if (!e.GetCurrentPoint(TabsView).Properties.IsLeftButtonPressed ||
            FindGroupTabBorder(e.OriginalSource) is not { Tag: string memberId })
        {
            return;
        }

        FlushPendingCtrlWheelReorderNow();
        CancelTabPress(releasePointerCapture: false);
        _tabPointerDown = true;
        _pressedTabId = memberId;
        _tabPressStartPoint = e.GetCurrentPoint(TabsView).Position;
        _lastTabPointerInRoot = e.GetCurrentPoint(TabsRoot).Position;
        _tabPressTimestampTick64 = Environment.TickCount64;
        _isTabDragLongPressArmed = false;
        CancelAllHoverSwitches();
        CancelWheelFeedback();
        TabsView.CapturePointer(e.Pointer);
        if (_tabs.TryGetValue(memberId, out GroupTab? row))
        {
            AnimateTabScale(row, 0.965, 90);
        }
        StartTabDragLongPressTimer(memberId);
        UpdateTabVisuals();
    }

    private void StartTabDragLongPressTimer(string memberId)
    {
        _tabLongPressCancellation?.Cancel();
        _tabLongPressCancellation?.Dispose();
        _tabLongPressCancellation = null;
        if (_isTabDragDisabledByElevation ||
            DragDropPermissionService.IsCurrentProcessElevatedCached() ||
            _presentation is not { Members.Count: >= 2 })
        {
            return;
        }

        var cts = new CancellationTokenSource();
        _tabLongPressCancellation = cts;
        _ = ArmTabDragAfterLongPressAsync(memberId, cts.Token);
    }

    private async Task ArmTabDragAfterLongPressAsync(string memberId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TabHoldPreLiftMilliseconds, cancellationToken);
            if (!cancellationToken.IsCancellationRequested &&
                _tabPointerDown &&
                string.Equals(_pressedTabId, memberId, StringComparison.Ordinal) &&
                _tabs.TryGetValue(memberId, out GroupTab? preLiftRow))
            {
                AnimateTabScale(preLiftRow, 0.985, TabHoldReorderArmMilliseconds - TabHoldPreLiftMilliseconds);
            }

            await Task.Delay(TabHoldReorderArmMilliseconds - TabHoldPreLiftMilliseconds, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cancellationToken.IsCancellationRequested ||
            !_tabPointerDown ||
            !string.Equals(_pressedTabId, memberId, StringComparison.Ordinal))
        {
            return;
        }

        _isTabDragLongPressArmed = true;
        BeginLiveTabReorder(memberId, _lastTabPointerInRoot);
    }

    private void BeginLiveTabReorder(string memberId, Windows.Foundation.Point pointerInRoot)
    {
        if (_liveReorderState is not null ||
            _isSavingTabOrder ||
            _presentation is not { Members.Count: >= 2 } presentation ||
            !_tabs.TryGetValue(memberId, out GroupTab? draggedRow))
        {
            return;
        }

        string[] originalOrder = presentation.Members.Select(static m => m.WidgetId).ToArray();
        int originalIndex = Array.IndexOf(originalOrder, memberId);
        if (originalIndex < 0)
        {
            return;
        }

        var slots = new SlotMetric[originalOrder.Length];
        double runningLeft = 0;
        double spacing = TabsView.ColumnSpacing;
        for (int i = 0; i < originalOrder.Length; i++)
        {
            string id = originalOrder[i];
            double width = 72;
            if (_tabs.TryGetValue(id, out GroupTab? row) && row.Tab.ActualWidth > 0)
            {
                width = row.Tab.ActualWidth;
                try
                {
                    runningLeft = row.Tab
                        .TransformToVisual(TabsView)
                        .TransformPoint(new Windows.Foundation.Point(0, 0)).X -
                        row.DragTranslateTransform.X;
                }
                catch (InvalidOperationException)
                {
                }
            }

            slots[i] = new SlotMetric(runningLeft, width);
            runningLeft += width + spacing;
        }

        double draggedWidth = slots[originalIndex].Width;
        double draggedStep = draggedWidth + spacing;

        _liveReorderState = new LiveTabReorderState(
            presentation.GroupId,
            memberId,
            draggedRow,
            originalOrder,
            originalOrder.ToList(),
            originalIndex,
            slots,
            draggedWidth,
            draggedStep,
            pointerInRoot.X,
            pointerInRoot.Y,
            TabsScrollViewer?.HorizontalOffset ?? 0);

        _draggingMemberId = memberId;
        _reorderTargetMemberId = memberId;
        _tabDragCanceled = false;
        _pendingTabSelection = null;
        _tabSelection.Reset();
        CancelAllHoverSwitches();
        CancelDragHoverSwitch();
        CancelWheelFeedback();

        foreach (var (id, row) in _tabs)
        {
            Canvas.SetZIndex(row.Tab, string.Equals(id, memberId, StringComparison.Ordinal) ? 20 : 1);
        }

        _liveReorderState.DetachDragStartedRaised = true;
        DetachDragStarted?.Invoke(this, new WidgetGroupMemberEventArgs(memberId));
        AnimateTabScale(draggedRow, 1.03, 110);
        UpdateTabVisuals();
    }

    private void TabsView_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _lastTabPointerInRoot = e.GetCurrentPoint(TabsRoot).Position;

        if (_liveReorderState is { } liveState)
        {
            Microsoft.UI.Input.PointerPoint pointInRoot = e.GetCurrentPoint(TabsRoot);
            if (!pointInRoot.Properties.IsLeftButtonPressed)
            {
                _ = CompleteLiveTabReorderAsync(canceled: _tabDragCanceled);
                return;
            }

            e.Handled = true;
            UpdateLiveTabReorder(liveState, pointInRoot.Position);
            return;
        }

        if (!_tabPointerDown ||
            _pressedTabId is not { } pressedId ||
            !_tabs.TryGetValue(pressedId, out GroupTab? row))
        {
            return;
        }

        Microsoft.UI.Input.PointerPoint currentPointInStrip = e.GetCurrentPoint(TabsView);
        if (!currentPointInStrip.Properties.IsLeftButtonPressed)
        {
            CancelTabPress(releasePointerCapture: true);
            UpdateTabVisuals();
            return;
        }

        if (_isTabDragLongPressArmed)
        {
            e.Handled = true;
            BeginLiveTabReorder(pressedId, _lastTabPointerInRoot);
            if (_liveReorderState is { } startedState)
            {
                UpdateLiveTabReorder(startedState, _lastTabPointerInRoot);
            }
            return;
        }

        double dx = currentPointInStrip.Position.X - _tabPressStartPoint.X;
        double dy = currentPointInStrip.Position.Y - _tabPressStartPoint.Y;
        double absDx = Math.Abs(dx);
        double absDy = Math.Abs(dy);

        // Micro-slip tolerance so fast clicks never turn into accidental drags.
        if (absDx < TabClickSlipToleranceDips && absDy < TabClickSlipToleranceDips)
        {
            return;
        }

        long elapsedMs = Environment.TickCount64 - _tabPressTimestampTick64;
        if (elapsedMs >= TabHoldPreLiftMilliseconds &&
            absDx <= TabHoldHorizontalCreepToleranceDips &&
            absDy <= TabClickSlipToleranceDips)
        {
            // Finger is holding horizontally in place preparing to reorder; arm early if near threshold.
            if (elapsedMs >= 170 &&
                !_isTabDragDisabledByElevation &&
                !DragDropPermissionService.IsCurrentProcessElevatedCached() &&
                _presentation is { Members.Count: >= 2 })
            {
                _isTabDragLongPressArmed = true;
                e.Handled = true;
                BeginLiveTabReorder(pressedId, _lastTabPointerInRoot);
                if (_liveReorderState is { } earlyState)
                {
                    UpdateLiveTabReorder(earlyState, _lastTabPointerInRoot);
                }
            }
            return;
        }

        if ((dx * dx) + (dy * dy) >= TabWindowDragDistanceThreshold * TabWindowDragDistanceThreshold)
        {
            // Immediate drag before long-press threshold moves the widget window.
            CancelTabPress(releasePointerCapture: true);
            AnimateTabScale(row, 1.0, 90);
            UpdateTabVisuals();
            _allowWindowDragPassthrough = true;
            try
            {
                WindowDragRequested?.Invoke(this, e);
            }
            finally
            {
                _allowWindowDragPassthrough = false;
            }
        }
    }

    private void UpdateLiveTabReorder(LiveTabReorderState state, Windows.Foundation.Point pointInRoot)
    {
        state.LastPointerInRoot = pointInRoot;
        double rootHeight = Math.Max(32, TabsRoot.ActualHeight);
        double exitMarginY = state.IsDetachingOutside ? 18.0 : 30.0;
        bool outsideVertically = pointInRoot.Y < -exitMarginY || pointInRoot.Y > rootHeight + exitMarginY;
        bool outsideHorizontally = pointInRoot.X < -160 || pointInRoot.X > Math.Max(TabsRoot.ActualWidth, 120) + 160;
        bool shouldDetachOutside = outsideVertically || outsideHorizontally;

        if (shouldDetachOutside)
        {
            StopDragEdgeScrollTimer();
            if (!state.DetachDragStartedRaised)
            {
                state.DetachDragStartedRaised = true;
                DetachDragStarted?.Invoke(this, new WidgetGroupMemberEventArgs(state.DraggedMemberId));
            }

            if (!state.IsDetachingOutside)
            {
                state.IsDetachingOutside = true;
                state.DraggedRow.Tab.Opacity = 0.28;
                AnimateNeighborsForTearOut(state);
                UpdateTabVisuals();
            }
            return;
        }

        if (state.IsDetachingOutside)
        {
            state.IsDetachingOutside = false;
            state.DraggedRow.Tab.Opacity = 1.0;
            AnimateNeighborsToCurrentOrder(state, animate: true);
        }

        double currentScrollOffset = TabsScrollViewer?.HorizontalOffset ?? 0;
        double scrollDelta = currentScrollOffset - state.StartScrollOffset;
        double rawDeltaX = (pointInRoot.X - state.StartPointerInRootX) + scrollDelta;

        double minDeltaX = -state.OriginalSlots[state.OriginalIndex].Left;
        SlotMetric lastSlot = state.OriginalSlots[^1];
        double totalStripWidth = lastSlot.Left + lastSlot.Width;
        double maxDeltaX = totalStripWidth - (state.OriginalSlots[state.OriginalIndex].Left + state.DraggedWidth);
        double clampedDeltaX = Math.Clamp(rawDeltaX, minDeltaX, Math.Max(minDeltaX, maxDeltaX));

        StopTabTranslateAnimation(state.DraggedRow);
        state.DraggedRow.DragTranslateTransform.X = clampedDeltaX;
        state.DraggedRow.DragTranslateTransform.Y = 0;

        UpdateDragEdgeAutoScroll(pointInRoot);
        EvaluateLiveReorderTargetSlot(state, clampedDeltaX);
        UpdateConnectedTabContour();
    }

    private void EvaluateLiveReorderTargetSlot(LiveTabReorderState state, double draggedDeltaX)
    {
        double draggedLeft = state.OriginalSlots[state.OriginalIndex].Left + draggedDeltaX;
        double spacing = TabsView.ColumnSpacing;
        int count = state.CurrentOrder.Count;
        bool changed = false;

        // Moving right: compare draggedLeft against the transition midpoint of swapping with the right neighbor.
        // This is invariant to tab width differences between the dragged tab and its neighbors.
        while (state.CurrentTargetIndex < count - 1)
        {
            double currentSlotLeft = ComputeSlotLeftAt(state, state.CurrentTargetIndex, spacing);
            string rightNeighborId = state.CurrentOrder[state.CurrentTargetIndex + 1];
            double rightNeighborWidth = GetTabSlotWidth(rightNeighborId, state.DraggedWidth);
            double nextTriggerLeft = currentSlotLeft + ((rightNeighborWidth + spacing) * 0.48);
            if (draggedLeft > nextTriggerLeft)
            {
                state.CurrentOrder.RemoveAt(state.CurrentTargetIndex);
                state.CurrentTargetIndex++;
                state.CurrentOrder.Insert(state.CurrentTargetIndex, state.DraggedMemberId);
                changed = true;
            }
            else
            {
                break;
            }
        }

        // Moving left: compare draggedLeft against the transition midpoint of swapping with the left neighbor.
        while (state.CurrentTargetIndex > 0)
        {
            double prevSlotLeft = ComputeSlotLeftAt(state, state.CurrentTargetIndex - 1, spacing);
            string leftNeighborId = state.CurrentOrder[state.CurrentTargetIndex - 1];
            double leftNeighborWidth = GetTabSlotWidth(leftNeighborId, state.DraggedWidth);
            double prevTriggerLeft = prevSlotLeft + ((leftNeighborWidth + spacing) * 0.52);
            if (draggedLeft < prevTriggerLeft)
            {
                state.CurrentOrder.RemoveAt(state.CurrentTargetIndex);
                state.CurrentTargetIndex--;
                state.CurrentOrder.Insert(state.CurrentTargetIndex, state.DraggedMemberId);
                changed = true;
            }
            else
            {
                break;
            }
        }

        if (!changed)
        {
            return;
        }

        _reorderTargetMemberId = state.OriginalOrder[state.CurrentTargetIndex];
        AnimateNeighborsToCurrentOrder(state, animate: true);
    }

    private double ComputeSlotLeftAt(LiveTabReorderState state, int targetSlotIndex, double spacing)
    {
        double cursorX = state.OriginalSlots[0].Left;
        for (int i = 0; i < targetSlotIndex; i++)
        {
            cursorX += GetTabSlotWidth(state.CurrentOrder[i], state.DraggedWidth) + spacing;
        }
        return cursorX;
    }

    private double GetTabSlotWidth(string memberId, double fallbackWidth) =>
        _tabs.TryGetValue(memberId, out GroupTab? row) && row.Tab.ActualWidth > 0
            ? row.Tab.ActualWidth
            : fallbackWidth;

    private void AnimateNeighborsToCurrentOrder(LiveTabReorderState state, bool animate)
    {
        for (int origIdx = 0; origIdx < state.OriginalOrder.Length; origIdx++)
        {
            string memberId = state.OriginalOrder[origIdx];
            if (string.Equals(memberId, state.DraggedMemberId, StringComparison.Ordinal) ||
                !_tabs.TryGetValue(memberId, out GroupTab? row))
            {
                continue;
            }

            int currentIdx = state.CurrentOrder.IndexOf(memberId);
            double targetOffset = 0;
            if (origIdx < state.OriginalIndex && currentIdx > origIdx)
            {
                targetOffset = state.DraggedSlotStep;
            }
            else if (origIdx > state.OriginalIndex && currentIdx < origIdx)
            {
                targetOffset = -state.DraggedSlotStep;
            }

            AnimateTabTranslateX(row, targetOffset, animate ? NeighborSlideDurationMilliseconds : 0);
        }
    }

    private void AnimateNeighborsForTearOut(LiveTabReorderState state)
    {
        for (int origIdx = 0; origIdx < state.OriginalOrder.Length; origIdx++)
        {
            string memberId = state.OriginalOrder[origIdx];
            if (string.Equals(memberId, state.DraggedMemberId, StringComparison.Ordinal) ||
                !_tabs.TryGetValue(memberId, out GroupTab? row))
            {
                continue;
            }

            double targetOffset = origIdx > state.OriginalIndex ? -state.DraggedSlotStep : 0;
            AnimateTabTranslateX(row, targetOffset, NeighborSlideDurationMilliseconds);
        }
    }

    private void UpdateDragEdgeAutoScroll(Windows.Foundation.Point pointInRoot)
    {
        if (UsesEqualTabs || TabsScrollViewer is null || TabsScrollViewer.ScrollableWidth <= 0.5)
        {
            StopDragEdgeScrollTimer();
            return;
        }

        double viewportLeft = 0;
        double viewportRight = TabsRoot.ActualWidth;
        try
        {
            GeneralTransform vpTransform = TabsScrollViewer.TransformToVisual(TabsRoot);
            viewportLeft = vpTransform.TransformPoint(new Windows.Foundation.Point(0, 0)).X;
            viewportRight = vpTransform.TransformPoint(new Windows.Foundation.Point(TabsScrollViewer.ActualWidth, 0)).X;
        }
        catch (InvalidOperationException)
        {
        }

        const double edgeHotZone = 26.0;
        int direction = 0;
        if (pointInRoot.X < viewportLeft + edgeHotZone && TabsScrollViewer.HorizontalOffset > 0.5)
        {
            direction = -1;
        }
        else if (pointInRoot.X > viewportRight - edgeHotZone &&
                 TabsScrollViewer.HorizontalOffset < TabsScrollViewer.ScrollableWidth - 0.5)
        {
            direction = 1;
        }

        if (direction == 0)
        {
            StopDragEdgeScrollTimer();
            return;
        }

        _dragEdgeScrollDirection = direction;
        if (_dragEdgeScrollTimer is null)
        {
            DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.IsRepeating = true;
            timer.Tick += DragEdgeScrollTimer_Tick;
            _dragEdgeScrollTimer = timer;
            timer.Start();
        }
    }

    private void DragEdgeScrollTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_liveReorderState is not { } state ||
            UsesEqualTabs ||
            TabsScrollViewer is null ||
            _dragEdgeScrollDirection == 0)
        {
            StopDragEdgeScrollTimer();
            return;
        }

        double nextOffset = Math.Clamp(
            TabsScrollViewer.HorizontalOffset + (_dragEdgeScrollDirection * 5.0),
            0,
            TabsScrollViewer.ScrollableWidth);
        if (Math.Abs(nextOffset - TabsScrollViewer.HorizontalOffset) < 0.1)
        {
            StopDragEdgeScrollTimer();
            return;
        }

        TabsScrollViewer.ChangeView(nextOffset, null, null, disableAnimation: true);
        UpdateLiveTabReorder(state, state.LastPointerInRoot);
    }

    private void StopDragEdgeScrollTimer()
    {
        _dragEdgeScrollDirection = 0;
        if (_dragEdgeScrollTimer is not null)
        {
            _dragEdgeScrollTimer.Stop();
            _dragEdgeScrollTimer.Tick -= DragEdgeScrollTimer_Tick;
            _dragEdgeScrollTimer = null;
        }
    }

    private void TabsView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_liveReorderState is not null)
        {
            e.Handled = true;
            _ = CompleteLiveTabReorderAsync(canceled: _tabDragCanceled);
            return;
        }

        if (!_tabPointerDown)
        {
            return;
        }

        string? pressedId = _pressedTabId;
        CancelTabPress(releasePointerCapture: true);
        if (pressedId is not null && _tabs.TryGetValue(pressedId, out GroupTab? bounceRow))
        {
            AnimateTabScale(bounceRow, 1.0, 120);
        }
        UpdateTabVisuals();
        if (_isSavingTabOrder)
        {
            return;
        }

        _pendingTabSelection = null;
        if (pressedId is not null && _tabs.TryGetValue(pressedId, out GroupTab? row))
        {
            var point = e.GetCurrentPoint(row.Tab).Position;
            if (point.X >= -6 && point.Y >= -6 &&
                point.X <= row.Tab.ActualWidth + 6 && point.Y <= row.Tab.ActualHeight + 6)
            {
                _pendingTabSelection = pressedId;
            }
        }
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            CommitPendingTabSelection();
            if (!IsTabInteractionBusy)
            {
                SynchronizeTabs();
            }
        });
    }

    private void TabsView_PointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (_liveReorderState is not null)
        {
            _ = CompleteLiveTabReorderAsync(canceled: true);
            return;
        }

        CancelTabPress(releasePointerCapture: false);
        UpdateTabVisuals();
        SynchronizeTabs();
    }

    private void TabsView_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_liveReorderState is not null)
        {
            _ = CompleteLiveTabReorderAsync(canceled: _tabDragCanceled);
            return;
        }

        CancelTabPress(releasePointerCapture: false);
        UpdateTabVisuals();
    }

    private async Task CompleteLiveTabReorderAsync(bool canceled)
    {
        if (_liveReorderState is not { } state)
        {
            return;
        }

        StopDragEdgeScrollTimer();
        CancelTabPress(releasePointerCapture: true);

        bool escapePressed = Win32Helper.IsKeyDown((int)VirtualKey.Escape);
        bool effectiveCanceled = canceled || _tabDragCanceled || escapePressed;

        if (!effectiveCanceled && state.IsDetachingOutside)
        {
            _liveReorderState = null;
            _reorderTargetMemberId = null;
            try
            {
                ResetAllTabTransforms();
                DetachMemberRequested?.Invoke(this, new WidgetGroupMemberEventArgs(state.DraggedMemberId));
            }
            finally
            {
                _draggingMemberId = null;
                if (state.DetachDragStartedRaised)
                {
                    DetachDragCompleted?.Invoke(this, new WidgetGroupMemberEventArgs(state.DraggedMemberId));
                }
                SynchronizeTabs();
            }
            return;
        }

        // If detach preview was shown earlier during the drag but the user brought
        // the tab back into the rail before releasing, close the detach preview first.
        if (state.DetachDragStartedRaised)
        {
            state.DetachDragStartedRaised = false;
            DetachDragCompleted?.Invoke(this, new WidgetGroupMemberEventArgs(state.DraggedMemberId));
        }

        int finalIndex = effectiveCanceled ? state.OriginalIndex : state.CurrentTargetIndex;
        if (effectiveCanceled)
        {
            state.CurrentOrder.Clear();
            state.CurrentOrder.AddRange(state.OriginalOrder);
            state.CurrentTargetIndex = state.OriginalIndex;
            AnimateNeighborsToCurrentOrder(state, animate: true);
        }

        // Calculate exact landing X for the dragged tab so it settles smoothly into the target slot.
        double landingLeft = state.OriginalSlots[0].Left;
        double spacing = TabsView.ColumnSpacing;
        for (int i = 0; i < finalIndex; i++)
        {
            string id = state.CurrentOrder[i];
            double w = _tabs.TryGetValue(id, out GroupTab? r) && r.Tab.ActualWidth > 0
                ? r.Tab.ActualWidth
                : state.DraggedWidth;
            landingLeft += w + spacing;
        }
        double landingTranslateX = landingLeft - state.OriginalSlots[state.OriginalIndex].Left;

        state.DraggedRow.Tab.Opacity = 1.0;
        AnimateTabScale(state.DraggedRow, 1.0, DropSettleDurationMilliseconds);
        AnimateTabTranslateX(state.DraggedRow, landingTranslateX, DropSettleDurationMilliseconds);

        if (AreSystemAnimationsEnabled() && XamlRoot is not null)
        {
            await Task.Delay(DropSettleDurationMilliseconds);
        }

        _liveReorderState = null;
        _reorderTargetMemberId = null;
        _isSavingTabOrder = true;
        try
        {
            // Commit visual column positions and clear transforms in the same frame so there is zero jump.
            for (int idx = 0; idx < state.CurrentOrder.Count; idx++)
            {
                string id = state.CurrentOrder[idx];
                if (_tabs.TryGetValue(id, out GroupTab? r))
                {
                    StopTabAnimations(r);
                    r.DragTranslateTransform.X = 0;
                    r.DragTranslateTransform.Y = 0;
                    r.ScaleTransform.ScaleX = 1;
                    r.ScaleTransform.ScaleY = 1;
                    r.Tab.Opacity = 1;
                    Canvas.SetZIndex(r.Tab, 1);
                    Grid.SetColumn(r.Tab, idx);
                }
            }
            UpdateConnectedTabContour();

            bool stillSameGroup = UsesTabs &&
                _presentation?.GroupId == state.GroupId &&
                _presentation.Members.Select(static m => m.WidgetId)
                    .SequenceEqual(state.OriginalOrder, StringComparer.Ordinal);

            if (!effectiveCanceled && stillSameGroup && finalIndex != state.OriginalIndex)
            {
                string targetWidgetId = state.OriginalOrder[finalIndex];
                var request = new WidgetGroupReorderEventArgs(
                    state.DraggedMemberId,
                    targetWidgetId,
                    state.GroupId,
                    state.OriginalOrder);
                if (ReorderRequested is { } handler)
                {
                    handler(this, request);
                    await request.Completion;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetGroup] Live tab reorder commit failed member={state.DraggedMemberId}: {ex}");
        }
        finally
        {
            _isSavingTabOrder = false;
            _draggingMemberId = null;
            SynchronizeTabs();
        }
    }

    private void ResetAllTabTransforms()
    {
        foreach (GroupTab row in _tabs.Values)
        {
            StopTabAnimations(row);
            row.DragTranslateTransform.X = 0;
            row.DragTranslateTransform.Y = 0;
            row.ScaleTransform.ScaleX = 1;
            row.ScaleTransform.ScaleY = 1;
            row.Tab.Opacity = 1;
            Canvas.SetZIndex(row.Tab, 1);
        }
    }

    // =========================================================================
    // Ctrl + Mouse Wheel Quick Position Adjustment
    // =========================================================================

    internal bool TryHandleCtrlWheelReorder(double delta)
    {
        if (!UsesTabs ||
            _isTabDragDisabledByElevation ||
            DragDropPermissionService.IsCurrentProcessElevatedCached() ||
            _liveReorderState is not null ||
            _isSavingTabOrder ||
            _pickerOpen ||
            _presentation is not { Members.Count: >= 2 } presentation ||
            Math.Abs(delta) < 0.01)
        {
            return false;
        }

        bool consumedStep = WidgetGroupNavigationInteractionPolicy.TryConsumeWheelGestureStep(
            ref _ctrlWheelAccumulator,
            ref _ctrlWheelLastInputAt,
            ref _ctrlWheelGestureDirection,
            ref _ctrlWheelGestureCommitted,
            delta,
            DateTimeOffset.UtcNow,
            out int direction);
        if (!consumedStep)
        {
            return true;
        }

        string activeId = _selectedTabId ?? presentation.ActiveMemberId;
        if (_ctrlWheelOrder is null ||
            !string.Equals(_ctrlWheelGroupId, presentation.GroupId, StringComparison.Ordinal))
        {
            _ctrlWheelGroupId = presentation.GroupId;
            _ctrlWheelOriginalOrder = presentation.Members.Select(static m => m.WidgetId).ToArray();
            _ctrlWheelOrder = _ctrlWheelOriginalOrder.ToList();
        }

        int currentIndex = _ctrlWheelOrder.IndexOf(activeId);
        if (currentIndex < 0)
        {
            return false;
        }

        int targetIndex = currentIndex + direction;
        if (targetIndex < 0 || targetIndex >= _ctrlWheelOrder.Count)
        {
            if (_tabs.TryGetValue(activeId, out GroupTab? edgeRow))
            {
                AnimateBoundaryTabNudge(edgeRow, direction);
            }
            RestartCtrlWheelCommitTimer();
            return true;
        }

        string neighborId = _ctrlWheelOrder[targetIndex];
        _ctrlWheelOrder[currentIndex] = neighborId;
        _ctrlWheelOrder[targetIndex] = activeId;

        double activeWidth = _tabs.TryGetValue(activeId, out GroupTab? activeRow) && activeRow.Tab.ActualWidth > 0
            ? activeRow.Tab.ActualWidth
            : 72;
        double neighborWidth = _tabs.TryGetValue(neighborId, out GroupTab? neighborRow) && neighborRow.Tab.ActualWidth > 0
            ? neighborRow.Tab.ActualWidth
            : 72;
        double spacing = TabsView.ColumnSpacing;

        for (int i = 0; i < _ctrlWheelOrder.Count; i++)
        {
            if (_tabs.TryGetValue(_ctrlWheelOrder[i], out GroupTab? r))
            {
                Grid.SetColumn(r.Tab, i);
            }
        }

        if (activeRow is not null)
        {
            StopTabTranslateAnimation(activeRow);
            activeRow.DragTranslateTransform.X = -direction * (neighborWidth + spacing);
            AnimateTabTranslateX(activeRow, 0, NeighborSlideDurationMilliseconds);
        }
        if (neighborRow is not null)
        {
            StopTabTranslateAnimation(neighborRow);
            neighborRow.DragTranslateTransform.X = direction * (activeWidth + spacing);
            AnimateTabTranslateX(neighborRow, 0, NeighborSlideDurationMilliseconds);
        }

        UpdateConnectedTabContour();
        if (!UsesEqualTabs)
        {
            ScrollTabIntoView(activeId, animate: true);
        }

        RestartCtrlWheelCommitTimer();
        return true;
    }

    private void RestartCtrlWheelCommitTimer()
    {
        if (_ctrlWheelCommitTimer is null)
        {
            DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(220);
            timer.IsRepeating = false;
            timer.Tick += CtrlWheelCommitTimer_Tick;
            _ctrlWheelCommitTimer = timer;
        }
        _ctrlWheelCommitTimer.Stop();
        _ctrlWheelCommitTimer.Start();
    }

    private void StopCtrlWheelCommitTimer()
    {
        if (_ctrlWheelCommitTimer is not null)
        {
            _ctrlWheelCommitTimer.Stop();
            _ctrlWheelCommitTimer.Tick -= CtrlWheelCommitTimer_Tick;
            _ctrlWheelCommitTimer = null;
        }
        _ctrlWheelOrder = null;
        _ctrlWheelOriginalOrder = null;
        _ctrlWheelGroupId = null;
    }

    private void CtrlWheelCommitTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        FlushPendingCtrlWheelReorderNow();
    }

    private async void FlushPendingCtrlWheelReorderNow()
    {
        if (_ctrlWheelCommitTimer is not null)
        {
            _ctrlWheelCommitTimer.Stop();
        }

        List<string>? workingOrder = _ctrlWheelOrder;
        string[]? originalOrder = _ctrlWheelOriginalOrder;
        string? groupId = _ctrlWheelGroupId;
        _ctrlWheelOrder = null;
        _ctrlWheelOriginalOrder = null;
        _ctrlWheelGroupId = null;

        if (workingOrder is null || originalOrder is null || groupId is null || _presentation is null)
        {
            return;
        }

        string activeId = _selectedTabId ?? _presentation.ActiveMemberId;
        int origIndex = Array.IndexOf(originalOrder, activeId);
        int finalIndex = workingOrder.IndexOf(activeId);
        if (origIndex < 0 || finalIndex < 0 || origIndex == finalIndex)
        {
            SynchronizeTabs();
            return;
        }

        string targetWidgetId = originalOrder[finalIndex];
        _isSavingTabOrder = true;
        try
        {
            var request = new WidgetGroupReorderEventArgs(
                activeId,
                targetWidgetId,
                groupId,
                originalOrder);
            if (ReorderRequested is { } handler)
            {
                handler(this, request);
                await request.Completion;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetGroup] Ctrl+Wheel tab reorder failed member={activeId}: {ex}");
        }
        finally
        {
            _isSavingTabOrder = false;
            SynchronizeTabs();
        }
    }

    // =========================================================================
    // Transform & Micro-Animation Helpers
    // =========================================================================

    private void AnimateTabTranslateX(GroupTab row, double targetX, int durationMs)
    {
        StopTabTranslateAnimation(row);
        if (durationMs <= 0 || !AreSystemAnimationsEnabled() || XamlRoot is null ||
            Math.Abs(row.DragTranslateTransform.X - targetX) < 0.25)
        {
            row.DragTranslateTransform.X = targetX;
            UpdateConnectedTabContour();
            return;
        }

        var animation = new DoubleAnimation
        {
            From = row.DragTranslateTransform.X,
            To = targetX,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animation, row.DragTranslateTransform);
        Storyboard.SetTargetProperty(animation, nameof(TranslateTransform.X));
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(row.ActiveTranslateStoryboard, storyboard))
            {
                row.ActiveTranslateStoryboard = null;
                row.DragTranslateTransform.X = targetX;
                UpdateConnectedTabContour();
            }
        };
        row.ActiveTranslateStoryboard = storyboard;
        storyboard.Begin();
    }

    private void AnimateTabScale(GroupTab row, double targetScale, int durationMs)
    {
        StopTabScaleAnimation(row);
        if (durationMs <= 0 || !AreSystemAnimationsEnabled() || XamlRoot is null ||
            (Math.Abs(row.ScaleTransform.ScaleX - targetScale) < 0.002 &&
             Math.Abs(row.ScaleTransform.ScaleY - targetScale) < 0.002))
        {
            row.ScaleTransform.ScaleX = targetScale;
            row.ScaleTransform.ScaleY = targetScale;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var animX = new DoubleAnimation
        {
            From = row.ScaleTransform.ScaleX,
            To = targetScale,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = ease
        };
        var animY = new DoubleAnimation
        {
            From = row.ScaleTransform.ScaleY,
            To = targetScale,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = ease
        };
        var storyboard = new Storyboard();
        Storyboard.SetTarget(animX, row.ScaleTransform);
        Storyboard.SetTargetProperty(animX, nameof(ScaleTransform.ScaleX));
        Storyboard.SetTarget(animY, row.ScaleTransform);
        Storyboard.SetTargetProperty(animY, nameof(ScaleTransform.ScaleY));
        storyboard.Children.Add(animX);
        storyboard.Children.Add(animY);
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(row.ActiveScaleStoryboard, storyboard))
            {
                row.ActiveScaleStoryboard = null;
                row.ScaleTransform.ScaleX = targetScale;
                row.ScaleTransform.ScaleY = targetScale;
            }
        };
        row.ActiveScaleStoryboard = storyboard;
        storyboard.Begin();
    }

    private void AnimateBoundaryTabNudge(GroupTab row, int direction)
    {
        StopTabTranslateAnimation(row);
        if (!AreSystemAnimationsEnabled() || XamlRoot is null)
        {
            return;
        }

        double offset = Math.Sign(direction) * 4.0;
        var motion = new DoubleAnimationUsingKeyFrames();
        motion.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(65)),
            Value = offset,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        motion.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(210)),
            Value = 0,
            EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
        });
        var storyboard = new Storyboard();
        Storyboard.SetTarget(motion, row.DragTranslateTransform);
        Storyboard.SetTargetProperty(motion, nameof(TranslateTransform.X));
        storyboard.Children.Add(motion);
        storyboard.Completed += (_, _) =>
        {
            if (ReferenceEquals(row.ActiveTranslateStoryboard, storyboard))
            {
                row.ActiveTranslateStoryboard = null;
                row.DragTranslateTransform.X = 0;
                UpdateConnectedTabContour();
            }
        };
        row.ActiveTranslateStoryboard = storyboard;
        storyboard.Begin();
    }

    private static void StopTabTranslateAnimation(GroupTab row)
    {
        if (row.ActiveTranslateStoryboard is { } sb)
        {
            row.ActiveTranslateStoryboard = null;
            sb.Stop();
            sb.Children.Clear();
        }
    }

    private static void StopTabScaleAnimation(GroupTab row)
    {
        if (row.ActiveScaleStoryboard is { } sb)
        {
            row.ActiveScaleStoryboard = null;
            sb.Stop();
            sb.Children.Clear();
        }
    }

    private static void StopTabAnimations(GroupTab row)
    {
        StopTabTranslateAnimation(row);
        StopTabScaleAnimation(row);
    }

    private void CancelTabPress(bool releasePointerCapture)
    {
        _tabLongPressCancellation?.Cancel();
        _tabLongPressCancellation?.Dispose();
        _tabLongPressCancellation = null;
        _isTabDragLongPressArmed = false;
        _tabPointerDown = false;
        _pressedTabId = null;
        if (releasePointerCapture)
        {
            TabsView.ReleasePointerCaptures();
        }
    }

    private void TabsView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _liveReorderState is not null)
        {
            _tabDragCanceled = true;
            e.Handled = true;
            _ = CompleteLiveTabReorderAsync(canceled: true);
        }
    }

    private void CancelTabInteraction()
    {
        CancelTabPress(releasePointerCapture: true);
        StopDragEdgeScrollTimer();
        StopCtrlWheelCommitTimer();
        _tabSelection.Reset();
        _pendingTabSelection = null;
        _reorderTargetMemberId = null;
        _tabDragCanceled = true;
        if (_liveReorderState is { } state)
        {
            _liveReorderState = null;
            _draggingMemberId = null;
            if (state.DetachDragStartedRaised)
            {
                DetachDragCompleted?.Invoke(this, new WidgetGroupMemberEventArgs(state.DraggedMemberId));
            }
        }
        ResetAllTabTransforms();
        UpdateTabVisuals();
    }

    // =========================================================================
    // External File Drag-Hover Switch Support
    // =========================================================================

    private void TabsView_DragEnter(object sender, DragEventArgs args)
    {
        if (args.DataView.Contains(DetachDragFormat))
        {
            args.AcceptedOperation = DataPackageOperation.None;
            args.Handled = true;
            return;
        }

        EnterFileDragStripFreeze();
    }

    private void TabsView_DragOver(object sender, DragEventArgs args)
    {
        if (args.DataView.Contains(DetachDragFormat))
        {
            args.AcceptedOperation = DataPackageOperation.None;
            args.Handled = true;
            return;
        }

        EnterFileDragStripFreeze();
        ObserveDragHoverSwitch(FindTabMemberUnderDrag(args), native: false);
    }

    private void TabsView_DragLeave(object sender, DragEventArgs args)
    {
        CancelDragHoverSwitch();
        ScheduleFileDragStripFreezeExit();
    }

    private const int StackDragSwitchCooldownMilliseconds = 900;
    private string? _lastDroppedTabMemberId;
    private long _lastStackDragSwitchTick64;

    private void SelectorButton_DragEnter(object sender, DragEventArgs args)
    {
        if (args.DataView.Contains(DetachDragFormat) || UsesTabs)
        {
            return;
        }

        ObserveDragHoverSwitch(ResolveStackModeDragTargetMemberId(respectCooldown: true), native: false);
    }

    private void SelectorButton_DragOver(object sender, DragEventArgs args)
    {
        if (args.DataView.Contains(DetachDragFormat) || UsesTabs)
        {
            CancelDragHoverSwitch();
            return;
        }

        ObserveDragHoverSwitch(ResolveStackModeDragTargetMemberId(respectCooldown: true), native: false);
    }

    private void SelectorButton_DragLeave(object sender, DragEventArgs args)
    {
        if (UsesTabs)
        {
            return;
        }

        try
        {
            if (SelectorButton.ActualWidth > 0 && SelectorButton.ActualHeight > 0)
            {
                Windows.Foundation.Point pt = args.GetPosition(SelectorButton);
                if (pt.X >= 0 && pt.Y >= 0 &&
                    pt.X <= SelectorButton.ActualWidth &&
                    pt.Y <= SelectorButton.ActualHeight)
                {
                    return;
                }
            }
        }
        catch (InvalidOperationException)
        {
        }

        CancelDragHoverSwitch();
    }

    private void SelectorButton_Drop(object sender, DragEventArgs args)
    {
        if (UsesTabs)
        {
            return;
        }

        _lastDroppedTabMemberId = args.DataView.Contains(DetachDragFormat)
            ? null
            : _presentation?.ActiveMemberId;
        CancelDragHoverSwitch();
    }

    internal static string? ResolveNextGroupMemberId(
        IReadOnlyList<string> memberIds,
        string? activeMemberId)
    {
        if (memberIds.Count <= 1)
        {
            return null;
        }

        int activeIndex = -1;
        for (int index = 0; index < memberIds.Count; index++)
        {
            if (string.Equals(memberIds[index], activeMemberId, StringComparison.Ordinal))
            {
                activeIndex = index;
                break;
            }
        }

        int nextIndex = activeIndex < 0
            ? 0
            : (activeIndex + 1) % memberIds.Count;
        return memberIds[nextIndex];
    }

    private string? ResolveStackModeDragTargetMemberId(bool respectCooldown)
    {
        if (_presentation is not { Members.Count: > 1 } presentation)
        {
            return null;
        }

        if (respectCooldown &&
            _lastStackDragSwitchTick64 != 0 &&
            Environment.TickCount64 - _lastStackDragSwitchTick64 < StackDragSwitchCooldownMilliseconds)
        {
            return null;
        }

        string[] memberIds = presentation.Members
            .Select(static member => member.WidgetId)
            .ToArray();
        return ResolveNextGroupMemberId(memberIds, presentation.ActiveMemberId);
    }

    internal string? HitTestDropTargetMemberId(
        FrameworkElement coordinateSpace,
        Windows.Foundation.Point point)
    {
        try
        {
            if (UsesTabs)
            {
                foreach (GroupTab row in _tabs.Values)
                {
                    if (row.Tab.ActualWidth <= 0 || row.Tab.ActualHeight <= 0)
                    {
                        continue;
                    }

                    Windows.Foundation.Point tabPoint = coordinateSpace
                        .TransformToVisual(row.Tab)
                        .TransformPoint(point);
                    if (tabPoint.X >= 0 && tabPoint.Y >= 0 &&
                        tabPoint.X <= row.Tab.ActualWidth &&
                        tabPoint.Y <= row.Tab.ActualHeight)
                    {
                        return row.Tab.Tag as string;
                    }
                }

                return null;
            }

            if (SelectorButton.Visibility == Visibility.Visible &&
                SelectorButton.ActualWidth > 0 &&
                SelectorButton.ActualHeight > 0)
            {
                Windows.Foundation.Point buttonPoint = coordinateSpace
                    .TransformToVisual(SelectorButton)
                    .TransformPoint(point);
                if (buttonPoint.X >= 0 && buttonPoint.Y >= 0 &&
                    buttonPoint.X <= SelectorButton.ActualWidth &&
                    buttonPoint.Y <= SelectorButton.ActualHeight)
                {
                    return _presentation?.ActiveMemberId;
                }
            }
        }
        catch (InvalidOperationException)
        {
        }

        return null;
    }

    internal string? HitTestDragSwitchMemberId(
        FrameworkElement coordinateSpace,
        Windows.Foundation.Point point,
        bool respectStackCooldown = false)
    {
        try
        {
            if (UsesTabs)
            {
                foreach (GroupTab row in _tabs.Values)
                {
                    if (row.Tab.ActualWidth <= 0 || row.Tab.ActualHeight <= 0)
                    {
                        continue;
                    }

                    Windows.Foundation.Point tabPoint = coordinateSpace
                        .TransformToVisual(row.Tab)
                        .TransformPoint(point);
                    if (tabPoint.X >= 0 && tabPoint.Y >= 0 &&
                        tabPoint.X <= row.Tab.ActualWidth &&
                        tabPoint.Y <= row.Tab.ActualHeight)
                    {
                        return row.Tab.Tag as string;
                    }
                }

                return null;
            }

            if (SelectorButton.Visibility == Visibility.Visible &&
                SelectorButton.ActualWidth > 0 &&
                SelectorButton.ActualHeight > 0)
            {
                Windows.Foundation.Point buttonPoint = coordinateSpace
                    .TransformToVisual(SelectorButton)
                    .TransformPoint(point);
                if (buttonPoint.X >= 0 && buttonPoint.Y >= 0 &&
                    buttonPoint.X <= SelectorButton.ActualWidth &&
                    buttonPoint.Y <= SelectorButton.ActualHeight)
                {
                    return ResolveStackModeDragTargetMemberId(respectCooldown: respectStackCooldown);
                }
            }
        }
        catch (InvalidOperationException)
        {
        }

        return null;
    }

    private void TabsView_Drop(object sender, DragEventArgs args)
    {
        if (args.DataView.Contains(DetachDragFormat))
        {
            _lastDroppedTabMemberId = null;
            CancelDragHoverSwitch();
            ExitFileDragStripFreeze();
            return;
        }

        _lastDroppedTabMemberId = FindTabMemberUnderDrag(args) ?? _dragHoverSwitchTargetId;
        CancelDragHoverSwitch();
        ExitFileDragStripFreeze();
    }

    internal string? ConsumePendingOrDroppedTabMemberId()
    {
        string? targetId = _lastDroppedTabMemberId ?? _dragHoverSwitchTargetId;
        _lastDroppedTabMemberId = null;
        CancelDragHoverSwitch();
        ExitFileDragStripFreeze();
        return targetId;
    }

    private void EnterFileDragStripFreeze()
    {
        _fileDragFreezeExitGeneration++;
        if (_isFileDragStripFreeze || !UsesTabs)
        {
            return;
        }

        _isFileDragStripFreeze = true;
    }

    private void ScheduleFileDragStripFreezeExit()
    {
        if (!_isFileDragStripFreeze)
        {
            return;
        }

        int generation = ++_fileDragFreezeExitGeneration;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (_fileDragFreezeExitGeneration == generation)
            {
                ExitFileDragStripFreeze();
            }
        });
    }

    private void ExitFileDragStripFreeze()
    {
        _fileDragFreezeExitGeneration++;
        if (!_isFileDragStripFreeze)
        {
            return;
        }

        _isFileDragStripFreeze = false;
        SynchronizeTabs();
    }

    internal void SetFileDragStripFreeze(bool active)
    {
        if (active)
        {
            EnterFileDragStripFreeze();
        }
        else
        {
            ExitFileDragStripFreeze();
        }
    }

    private void ObserveDragHoverSwitch(string? targetId, bool native)
    {
        if (targetId is null ||
            _presentation is null ||
            string.Equals(_presentation.ActiveMemberId, targetId, StringComparison.Ordinal))
        {
            CancelDragHoverSwitch();
            return;
        }

        if (string.Equals(_dragHoverSwitchTargetId, targetId, StringComparison.Ordinal) &&
            _dragHoverSwitchTimer is { IsRunning: true })
        {
            return;
        }

        CancelDragHoverSwitch();
        _dragHoverSwitchTargetId = targetId;
        DispatcherQueueTimer timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(DragHoverSwitchDelayMilliseconds);
        timer.IsRepeating = false;
        timer.Tick += DragHoverSwitchTimer_Tick;
        _dragHoverSwitchTimer = timer;
        timer.Start();
        App.Log(
            $"[WidgetGroup] Drag-hover dwell start member={targetId} " +
            $"native={native} group={_presentation.GroupId}");
    }

    internal void ObserveNativeDragHoverMember(string? memberId)
    {
        ObserveDragHoverSwitch(memberId, native: true);
    }

    private string? FindTabMemberUnderDrag(DragEventArgs args)
    {
        try
        {
            foreach (GroupTab row in _tabs.Values)
            {
                if (row.Tab.ActualWidth <= 0 || row.Tab.ActualHeight <= 0)
                {
                    continue;
                }
                Windows.Foundation.Point point = args.GetPosition(row.Tab);
                if (point.X >= 0 && point.Y >= 0 &&
                    point.X <= row.Tab.ActualWidth &&
                    point.Y <= row.Tab.ActualHeight)
                {
                    return (string)row.Tab.Tag;
                }
            }
        }
        catch (InvalidOperationException)
        {
        }
        return null;
    }

    private void CommitDragHoverSwitch()
    {
        string? targetId = _dragHoverSwitchTargetId;
        CancelDragHoverSwitch();
        if (targetId is null)
        {
            return;
        }

        string? reason = ResolveDragHoverSwitchBlockReason(targetId);
        if (reason is not null)
        {
            App.Log(
                $"[WidgetGroup] Drag-hover dwell blocked member={targetId} " +
                $"reason={reason}");
            return;
        }

        if (!UsesTabs)
        {
            _lastStackDragSwitchTick64 = Environment.TickCount64;
        }
        App.Log(
            $"[WidgetGroup] Drag-hover dwell switch member={targetId} " +
            $"group={_presentation!.GroupId}");
        MemberInvoked?.Invoke(
            this,
            new WidgetGroupMemberEventArgs(targetId, WidgetGroupSwitchOrigin.DragHover));
    }

    private string? ResolveDragHoverSwitchBlockReason(string targetId)
    {
        if (_draggingMemberId is not null)
        {
            return "detach";
        }
        if (IsTabInteractionBusy)
        {
            return "busy";
        }
        if (_presentation is not { Members.Count: > 1 } presentation)
        {
            return "members";
        }
        if (string.Equals(presentation.ActiveMemberId, targetId, StringComparison.Ordinal))
        {
            return "active";
        }
        if (!presentation.Members.Any(member => member.WidgetId == targetId))
        {
            return "missing";
        }
        if (!Win32Helper.IsAnyMouseButtonDown())
        {
            return "button";
        }
        return null;
    }

    private void DragHoverSwitchTimer_Tick(DispatcherQueueTimer sender, object args) =>
        CommitDragHoverSwitch();

    internal void CancelDragHoverSwitch()
    {
        if (_dragHoverSwitchTimer is not null)
        {
            _dragHoverSwitchTimer.Stop();
            _dragHoverSwitchTimer.Tick -= DragHoverSwitchTimer_Tick;
            _dragHoverSwitchTimer = null;
        }
        _dragHoverSwitchTargetId = null;
    }

    private readonly record struct SlotMetric(double Left, double Width)
    {
        public double Center => Left + (Width * 0.5);
    }

    private sealed class GroupTab
    {
        public GroupTab(
            Border tab,
            Grid header,
            ScaleTransform scaleTransform,
            TranslateTransform dragTranslateTransform,
            WidgetTitleIcon icon,
            TextBlock title,
            ProgressRing loading)
        {
            Tab = tab;
            Header = header;
            ScaleTransform = scaleTransform;
            DragTranslateTransform = dragTranslateTransform;
            Icon = icon;
            Title = title;
            Loading = loading;
        }

        public Border Tab { get; }
        public Grid Header { get; }
        public ScaleTransform ScaleTransform { get; }
        public TranslateTransform DragTranslateTransform { get; }
        public WidgetTitleIcon Icon { get; }
        public TextBlock Title { get; }
        public ProgressRing Loading { get; }
        public Storyboard? ActiveTranslateStoryboard { get; set; }
        public Storyboard? ActiveScaleStoryboard { get; set; }
    }

    private sealed class LiveTabReorderState
    {
        public LiveTabReorderState(
            string groupId,
            string draggedMemberId,
            GroupTab draggedRow,
            string[] originalOrder,
            List<string> currentOrder,
            int originalIndex,
            SlotMetric[] originalSlots,
            double draggedWidth,
            double draggedSlotStep,
            double startPointerInRootX,
            double startPointerInRootY,
            double startScrollOffset)
        {
            GroupId = groupId;
            DraggedMemberId = draggedMemberId;
            DraggedRow = draggedRow;
            OriginalOrder = originalOrder;
            CurrentOrder = currentOrder;
            OriginalIndex = originalIndex;
            CurrentTargetIndex = originalIndex;
            OriginalSlots = originalSlots;
            DraggedWidth = draggedWidth;
            DraggedSlotStep = draggedSlotStep;
            StartPointerInRootX = startPointerInRootX;
            StartPointerInRootY = startPointerInRootY;
            StartScrollOffset = startScrollOffset;
            LastPointerInRoot = new Windows.Foundation.Point(startPointerInRootX, startPointerInRootY);
        }

        public string GroupId { get; }
        public string DraggedMemberId { get; }
        public GroupTab DraggedRow { get; }
        public string[] OriginalOrder { get; }
        public List<string> CurrentOrder { get; }
        public int OriginalIndex { get; }
        public int CurrentTargetIndex { get; set; }
        public SlotMetric[] OriginalSlots { get; }
        public double DraggedWidth { get; }
        public double DraggedSlotStep { get; }
        public double StartPointerInRootX { get; }
        public double StartPointerInRootY { get; }
        public double StartScrollOffset { get; }
        public Windows.Foundation.Point LastPointerInRoot { get; set; }
        public bool IsDetachingOutside { get; set; }
        public bool DetachDragStartedRaised { get; set; }
    }
}
