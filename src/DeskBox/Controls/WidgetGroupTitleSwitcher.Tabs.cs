using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using VirtualKey = Windows.System.VirtualKey;

namespace DeskBox.Controls;

public sealed partial class WidgetGroupTitleSwitcher
{
    private readonly Dictionary<string, GroupTab> _tabs = new(StringComparer.Ordinal);
    private readonly WidgetGroupTabSelectionState _tabSelection = new();
    private double _inactiveTabHeaderOpacity = 0.65;
    private bool _isSynchronizingTabs;
    private bool _isSavingTabOrder;
    private bool _tabPointerDown;
    private bool _tabDragCanceled;
    private string? _pressedTabId;
    private string? _pendingTabSelection;
    private TabDragSnapshot? _tabDragSnapshot;

    private bool UsesTabs => WidgetGroupNavigationStyles.Normalize(
        NavigationStyle, allowFollowDefault: false) == WidgetGroupNavigationStyles.Tabs;

    private bool IsTabInteractionBusy =>
        _tabPointerDown || _tabDragSnapshot is not null || _isSavingTabOrder;

    internal void SetTabForegroundColors(
        Windows.UI.Color primary,
        Windows.UI.Color secondary,
        Windows.UI.Color disabled,
        bool highContrast)
    {
        // Mutate the normal-theme instances, leaving the system high-contrast
        // dictionary intact. Existing native tab containers update immediately.
        var resources = (ResourceDictionary)TabsView.Resources.ThemeDictionaries["Default"];
        SetColor(primary, "TabViewItemHeaderForegroundSelected",
            "TabViewItemHeaderForegroundPointerOver", "TabViewScrollButtonForegroundPointerOver");
        SetColor(secondary, "TabViewItemHeaderForeground",
            "TabViewItemHeaderForegroundPressed", "TabViewScrollButtonForeground",
            "TabViewScrollButtonForegroundPressed");
        SetColor(disabled, "TabViewItemHeaderForegroundDisabled", "TabViewScrollButtonForegroundDisabled");
        _inactiveTabHeaderOpacity = highContrast ? 1 : 0.65;
        UpdateTabHeaderOpacity();

        void SetColor(Windows.UI.Color color, params string[] keys)
        {
            foreach (string key in keys)
            {
                ((SolidColorBrush)resources[key]).Color = color;
            }
        }
    }

    private void UpdateTabHeaderOpacity()
    {
        foreach (GroupTab row in _tabs.Values)
        {
            if (row.Tab.Header is UIElement header)
            {
                header.Opacity = ReferenceEquals(TabsView.SelectedItem, row.Tab)
                    ? 1 : _inactiveTabHeaderOpacity;
            }
        }
    }

    private void RegisterTabPointerHandlers()
    {
        TabsView.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler(TabsView_PointerPressed), handledEventsToo: true);
        TabsView.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler(TabsView_PointerReleased), handledEventsToo: true);
        TabsView.AddHandler(UIElement.PointerCanceledEvent,
            new PointerEventHandler(TabsView_PointerCanceled), handledEventsToo: true);
        TabsView.AddHandler(UIElement.KeyDownEvent,
            new KeyEventHandler(TabsView_KeyDown), handledEventsToo: true);
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

        _isSynchronizingTabs = true;
        try
        {
            var memberIds = _presentation.Members.Select(member => member.WidgetId)
                .ToHashSet(StringComparer.Ordinal);
            foreach (string removedId in _tabs.Keys.Where(id => !memberIds.Contains(id)).ToArray())
            {
                TabsView.TabItems.Remove(_tabs[removedId].Tab);
                _tabs.Remove(removedId);
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
                // Keep the native containers alive across content commits, title
                // changes and settings notifications. Never rebuild during a drag.
                if (index >= TabsView.TabItems.Count || !ReferenceEquals(TabsView.TabItems[index], row.Tab))
                {
                    TabsView.TabItems.Remove(row.Tab);
                    TabsView.TabItems.Insert(index, row.Tab);
                }
                row.Icon.Glyph = member.Glyph;
                row.Icon.IconKind = member.IconKind;
                row.Icon.Mode = TitleIconMode;
                row.Icon.AccentColor = TitleIconAccentColor;
                row.Icon.IconSize = IconSize;
                row.Icon.Visibility = showIcon ? Visibility.Visible : Visibility.Collapsed;
                row.Icon.Margin = showIcon && showText
                    ? new Thickness(0, 0, 8, 0)
                    : new Thickness(0);
                row.Title.Text = member.Name;
                row.Title.FontSize = CurrentTitle.FontSize;
                row.Title.Visibility = showText ? Visibility.Visible : Visibility.Collapsed;
                ToolTipService.SetToolTip(row.Tab, member.Name);
                AutomationProperties.SetName(row.Tab, member.Name);
                AutomationProperties.SetPositionInSet(row.Tab, index + 1);
                AutomationProperties.SetSizeOfSet(row.Tab, _presentation.Members.Count);
            }
            string selectedId = _tabSelection.ResolveSelection(
                _presentation.GroupId, _presentation.ActiveMemberId, memberIds);
            TabsView.SelectedItem = _tabs.TryGetValue(selectedId, out GroupTab? selected)
                ? selected.Tab : null;
            UpdateTabHeaderOpacity();
        }
        finally
        {
            _isSynchronizingTabs = false;
        }
    }

    private GroupTab CreateGroupTab(string memberId)
    {
        var icon = new WidgetTitleIcon { IsHitTestVisible = false };
        var title = new TextBlock
        {
            MaxWidth = MaximumTitleWidth,
            // Override the native selected ContentPresenter's inherited SemiBold.
            // Stable text weight also prevents tab widths from shifting on selection.
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
            MinHeight = 24
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 1);
        Grid.SetColumn(loading, 2);
        header.Children.Add(icon);
        header.Children.Add(title);
        header.Children.Add(loading);
        var tab = new TabViewItem
        {
            Header = header,
            Tag = memberId,
            IsClosable = false,
            MinHeight = _titleBarContentHeight,
            VerticalContentAlignment = VerticalAlignment.Center,
            // Native TabView drag lookup falls back to the item's Content.
            // Null content makes every header resolve to the first tab. Keep
            // a distinct, zero-height placeholder; DeskBox owns real content.
            Content = new Border { Visibility = Visibility.Collapsed }
        };
        tab.PointerEntered += (_, _) =>
        {
            if (HoverSwitchEnabled && !IsTabInteractionBusy &&
                _presentation?.ActiveMemberId != memberId)
            {
                BeginTabHoverSwitch(memberId);
            }
        };
        tab.PointerExited += (_, _) => CancelTabHoverSwitch(memberId);
        return new GroupTab(tab, icon, title, loading);
    }

    private void ClearTabs()
    {
        if (_tabDragSnapshot is not null || _isSavingTabOrder)
        {
            _tabDragCanceled = true;
            return;
        }
        _isSynchronizingTabs = true;
        try
        {
            TabsView.TabItems.Clear();
            _tabs.Clear();
            _tabSelection.Reset();
            _pendingTabSelection = null;
        }
        finally
        {
            _isSynchronizingTabs = false;
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

    private void TabsView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTabHeaderOpacity();
        if (_isSynchronizingTabs || _tabDragSnapshot is not null || _isSavingTabOrder ||
            TabsView.SelectedItem is not TabViewItem { Tag: string memberId })
        {
            return;
        }
        _pendingTabSelection = memberId;
        // Native selection can occur before PointerPressed bubbles to us. Wait
        // until the input event is routed to distinguish a click from a drag.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CommitPendingTabSelection);
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
            // Native selection already shows the user's target. Keep it across
            // the queued release and host refreshes, rolling back only on failure.
            long requestVersion = _tabSelection.Request(presentation.GroupId, memberId);
            MemberInvoked?.Invoke(this, new WidgetGroupMemberEventArgs(memberId)
            {
                TabSelectionRequestVersion = requestVersion
            });
        }
    }

    private static TabViewItem? FindGroupTab(object source)
    {
        for (DependencyObject? current = source as DependencyObject; current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (current is TabViewItem tab)
            {
                return tab;
            }
        }
        return null;
    }

    private void TabsView_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (e.GetCurrentPoint(TabsView).Properties.IsLeftButtonPressed &&
            FindGroupTab(e.OriginalSource) is { Tag: string memberId })
        {
            _tabPointerDown = true;
            _pressedTabId = memberId;
            CancelAllHoverSwitches();
        }
    }

    private void TabsView_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _tabPointerDown = false;
        string? pressedId = _pressedTabId;
        _pressedTabId = null;
        if (_tabDragSnapshot is not null || _isSavingTabOrder)
        {
            return;
        }
        _pendingTabSelection = null;
        if (pressedId is not null && _tabs.TryGetValue(pressedId, out GroupTab? row))
        {
            var point = e.GetCurrentPoint(row.Tab).Position;
            if (point.X >= 0 && point.Y >= 0 &&
                point.X <= row.Tab.ActualWidth && point.Y <= row.Tab.ActualHeight)
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
        _tabPointerDown = false;
        _pressedTabId = null;
        _pendingTabSelection = null;
        // The native drag loop itself can cancel XAML pointer capture. The
        // drag completion result, not capture loss, decides whether to commit.
        if (_tabDragSnapshot is null)
        {
            SynchronizeTabs();
        }
    }

    private void TabsView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && _tabDragSnapshot is not null)
        {
            _tabDragCanceled = true;
        }
    }

    private void CancelTabInteraction()
    {
        _tabSelection.Reset();
        _tabPointerDown = false;
        _pressedTabId = null;
        _pendingTabSelection = null;
        _tabDragCanceled = true;
    }

    private void TabsView_TabDragStarting(TabView sender, TabViewTabDragStartingEventArgs args)
    {
        if (!UsesTabs || _isSavingTabOrder || _tabDragSnapshot is not null ||
            _presentation is null || _presentation.Members.Count < 2 ||
            args.Tab?.Tag is not string memberId ||
            !_presentation.Members.Any(member => member.WidgetId == memberId))
        {
            args.Cancel = true;
            CancelTabInteraction();
            return;
        }
        _tabDragSnapshot = new TabDragSnapshot(_presentation.GroupId, memberId,
            _presentation.Members.Select(member => member.WidgetId).ToArray());
        _tabDragCanceled = false;
        _tabPointerDown = false;
        _pressedTabId = null;
        _pendingTabSelection = null;
        _draggingMemberId = memberId;
        _tabSelection.Reset();
        CancelAllHoverSwitches();
        CancelWheelFeedback();
        args.Data.SetData(DetachDragFormat, memberId);
        args.Data.RequestedOperation = DataPackageOperation.Move;
        Win32Helper.GetCursorPos(out var dragStartCursor);
        App.LogVerbose($"[WidgetGroup] Tab drag started group={_presentation.GroupId} member={memberId} " +
            $"mouseDown={Win32Helper.IsAnyMouseButtonDown()} cursor={dragStartCursor.X},{dragStartCursor.Y}");
        DetachDragStarted?.Invoke(this, new WidgetGroupMemberEventArgs(memberId));
    }

    private void TabsView_TabStripDragOver(object sender, DragEventArgs args)
    {
        // The native list owns in-strip reordering. Other groups are not a
        // supported transfer target; ordinary file drops can still bubble to
        // the existing file-content drop handler.
        if (args.DataView.Contains(DetachDragFormat) && _tabDragSnapshot is null)
        {
            args.AcceptedOperation = DataPackageOperation.None;
            args.Handled = true;
        }
    }

    private async void TabsView_TabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args)
    {
        if (_tabDragSnapshot is not { } snapshot)
        {
            return;
        }
        bool canceled = _tabDragCanceled || Win32Helper.IsKeyDown((int)VirtualKey.Escape) ||
            Win32Helper.IsAnyMouseButtonDown();
        string[] visualOrder = TabsView.TabItems.OfType<TabViewItem>()
            .Select(tab => (string)tab.Tag).ToArray();
        App.LogVerbose($"[WidgetGroup] Tab drag completed group={snapshot.GroupId} member={snapshot.MemberId} " +
            $"result={args.DropResult} canceled={canceled} order={string.Join(",", visualOrder)}");
        _tabDragSnapshot = null;
        _tabPointerDown = false;
        _pressedTabId = null;
        _pendingTabSelection = null;
        try
        {
            bool stillSameGroup = UsesTabs && _presentation?.GroupId == snapshot.GroupId &&
                _presentation.Members.Select(member => member.WidgetId)
                    .SequenceEqual(snapshot.MemberIds, StringComparer.Ordinal);
            if (canceled || !stillSameGroup)
            {
                return;
            }
            if (args.DropResult == DataPackageOperation.Move &&
                WidgetGroupOrder.TryResolveDragTarget(snapshot.MemberIds, visualOrder,
                    snapshot.MemberId, out string? targetId))
            {
                _isSavingTabOrder = true;
                var request = new WidgetGroupReorderEventArgs(snapshot.MemberId, targetId!,
                    snapshot.GroupId, snapshot.MemberIds);
                if (ReorderRequested is { } handler)
                {
                    handler(this, request);
                    await request.Completion;
                }
            }
            else if (args.DropResult == DataPackageOperation.None)
            {
                // The host also checks the entire window bounds, the release
                // margin and cancellation observed during the native drag loop.
                DetachMemberRequested?.Invoke(this, new WidgetGroupMemberEventArgs(snapshot.MemberId));
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetGroup] Tab drag commit failed member={snapshot.MemberId}: {ex}");
        }
        finally
        {
            _isSavingTabOrder = false;
            _draggingMemberId = null;
            DetachDragCompleted?.Invoke(this, new WidgetGroupMemberEventArgs(snapshot.MemberId));
            SynchronizeTabs();
        }
    }

    private sealed record GroupTab(TabViewItem Tab, WidgetTitleIcon Icon, TextBlock Title, ProgressRing Loading);
    private sealed record TabDragSnapshot(string GroupId, string MemberId, string[] MemberIds);
}
