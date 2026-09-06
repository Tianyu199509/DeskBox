using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Services;
using Windows.Graphics;

namespace DeskBox.Views;

public abstract partial class WidgetWindowBase
{
    private static readonly UIntPtr GroupWheelSubclassId = new(0xDDB4);
    private bool _groupingInitialized;
    private bool _groupPickerInteractionOpen;
    private string? _groupDetachDragMemberId;
    private int _groupDetachDragOffsetX;
    private int _groupDetachDragOffsetY;
    private CancellationTokenSource? _groupDetachPreviewCancellation;
    private WidgetDetachPlacementPreviewWindow? _groupDetachPlacementPreview;
    private bool _groupDetachCommitInProgress;
    private volatile bool _groupDetachDragCanceled;
    private bool _groupDetachPreviewWarmupQueued;
    private Win32Helper.SubclassProc? _groupWheelSubclassProc;
    private bool _isGroupWheelSubclassInstalled;
    private bool _nativeGroupWheelEnabled;

    protected void InitializeWidgetGrouping()
    {
        if (_groupingInitialized)
        {
            return;
        }

        _groupingInitialized = true;
        InstallGroupWheelSubclass();
        WidgetShellControl.GroupMemberInvoked += WidgetShellControl_GroupMemberInvoked;
        WidgetShellControl.GroupMemberRemoveRequested += WidgetShellControl_GroupMemberRemoveRequested;
        WidgetShellControl.GroupMemberDetachRequested += WidgetShellControl_GroupMemberDetachRequested;
        WidgetShellControl.GroupMemberDetachDragStarted += WidgetShellControl_GroupMemberDetachDragStarted;
        WidgetShellControl.GroupMemberDetachDragCompleted += WidgetShellControl_GroupMemberDetachDragCompleted;
        WidgetShellControl.GroupMemberReorderRequested += WidgetShellControl_GroupMemberReorderRequested;
        WidgetShellControl.GroupDissolveRequested += WidgetShellControl_GroupDissolveRequested;
        WidgetShellControl.GroupPickerOpened += WidgetShellControl_GroupPickerOpened;
        WidgetShellControl.GroupPickerClosed += WidgetShellControl_GroupPickerClosed;

        if (App.Current.WidgetManager is { } manager)
        {
            manager.WidgetGroupsChanged += WidgetManager_WidgetGroupsChanged;
        }

        RefreshWidgetGroupPresentation();
    }

    public void SetGroupDropPreview(
        bool visible,
        bool ready,
        string? messageKey = null)
    {
        WidgetShellControl.SetGroupDropPreview(visible, ready, messageKey);
    }

    public RectInt32? GetGroupMergeTitleScreenBounds()
    {
        Microsoft.UI.Xaml.FrameworkElement? titleTarget =
            WidgetShellControl.GroupMergeTitleTargetElement;
        if (titleTarget?.XamlRoot is null ||
            titleTarget.ActualWidth <= 0 ||
            titleTarget.ActualHeight <= 0)
        {
            return null;
        }

        try
        {
            Windows.Foundation.Point topLeft = titleTarget.TransformToVisual(null)
                .TransformPoint(new Windows.Foundation.Point(0, 0));
            double scale = titleTarget.XamlRoot.RasterizationScale;
            PointInt32 windowPosition = AppWindow.Position;
            int left = windowPosition.X + (int)Math.Floor(topLeft.X * scale);
            int top = windowPosition.Y + (int)Math.Floor(topLeft.Y * scale);
            int right = windowPosition.X +
                (int)Math.Ceiling((topLeft.X + titleTarget.ActualWidth) * scale);
            int bottom = windowPosition.Y +
                (int)Math.Ceiling((topLeft.Y + titleTarget.ActualHeight) * scale);
            return new RectInt32(
                left,
                top,
                Math.Max(1, right - left),
                Math.Max(1, bottom - top));
        }
        catch
        {
            return null;
        }
    }

    protected void CleanupWidgetGrouping()
    {
        if (!_groupingInitialized)
        {
            return;
        }

        _groupingInitialized = false;
        RemoveGroupWheelSubclass();
        WidgetShellControl.GroupMemberInvoked -= WidgetShellControl_GroupMemberInvoked;
        WidgetShellControl.GroupMemberRemoveRequested -= WidgetShellControl_GroupMemberRemoveRequested;
        WidgetShellControl.GroupMemberDetachRequested -= WidgetShellControl_GroupMemberDetachRequested;
        WidgetShellControl.GroupMemberDetachDragStarted -= WidgetShellControl_GroupMemberDetachDragStarted;
        WidgetShellControl.GroupMemberDetachDragCompleted -= WidgetShellControl_GroupMemberDetachDragCompleted;
        WidgetShellControl.GroupMemberReorderRequested -= WidgetShellControl_GroupMemberReorderRequested;
        WidgetShellControl.GroupDissolveRequested -= WidgetShellControl_GroupDissolveRequested;
        WidgetShellControl.GroupPickerOpened -= WidgetShellControl_GroupPickerOpened;
        WidgetShellControl.GroupPickerClosed -= WidgetShellControl_GroupPickerClosed;

        if (App.Current?.WidgetManager is { } manager)
        {
            manager.WidgetGroupsChanged -= WidgetManager_WidgetGroupsChanged;
        }

        if (_groupPickerInteractionOpen)
        {
            _groupPickerInteractionOpen = false;
            ReleaseInteractionLayer("widget-group-picker-cleanup");
        }

        ClearGroupDetachDragAnchor();
        StopGroupDetachPreviewPolling();
        if (!_groupDetachCommitInProgress)
        {
            CloseGroupDetachPlacementPreview();
        }
        _nativeGroupWheelEnabled = false;
    }

    private void InstallGroupWheelSubclass()
    {
        if (_isGroupWheelSubclassInstalled || HWnd == IntPtr.Zero)
        {
            return;
        }

        _groupWheelSubclassProc ??= GroupWheelSubclassProc;
        _isGroupWheelSubclassInstalled = Win32Helper.SetWindowSubclass(
            HWnd,
            _groupWheelSubclassProc,
            GroupWheelSubclassId,
            UIntPtr.Zero);
        App.LogVerbose(
            $"[WidgetGroup] Native wheel hook hwnd=0x{HWnd.ToInt64():X} " +
            $"installed={_isGroupWheelSubclassInstalled}");
    }

    private void RemoveGroupWheelSubclass()
    {
        if (!_isGroupWheelSubclassInstalled ||
            _groupWheelSubclassProc is null ||
            HWnd == IntPtr.Zero)
        {
            return;
        }

        Win32Helper.RemoveWindowSubclass(
            HWnd,
            _groupWheelSubclassProc,
            GroupWheelSubclassId);
        _isGroupWheelSubclassInstalled = false;
    }

    private IntPtr GroupWheelSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr subclassId,
        UIntPtr refData)
    {
        try
        {
            if (message == Win32Helper.WM_MOUSEWHEEL &&
                TryQueueNativeGroupWheel(wParam, lParam))
            {
                return IntPtr.Zero;
            }

            if (message == Win32Helper.WM_NCDESTROY)
            {
                RemoveGroupWheelSubclass();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetGroup] Native wheel hook failed: {ex}");
        }

        return Win32Helper.DefSubclassProc(
            hWnd,
            message,
            wParam,
            lParam);
    }

    private bool TryQueueNativeGroupWheel(UIntPtr wParam, IntPtr lParam)
    {
        int delta = unchecked((short)(wParam.ToUInt64() >> 16));
        long packedPoint = lParam.ToInt64();
        var clientPoint = new Win32Helper.POINT
        {
            X = unchecked((short)(packedPoint & 0xFFFF)),
            Y = unchecked((short)((packedPoint >> 16) & 0xFFFF))
        };
        if (!_nativeGroupWheelEnabled ||
            delta == 0 ||
            !Win32Helper.ScreenToClient(HWnd, ref clientPoint))
        {
            return false;
        }

        double scale = Win32Helper.GetDpiScaleForWindow(
            HWnd,
            xamlRoot: null);
        if (!double.IsFinite(scale) || scale <= 0)
        {
            scale = 1;
        }

        int broadTitleHeight = Math.Max(
            1,
            (int)Math.Ceiling(72 * scale));
        if (clientPoint.X < 0 ||
            clientPoint.Y < 0 ||
            clientPoint.X > AppWindow.Size.Width ||
            clientPoint.Y > broadTitleHeight)
        {
            return false;
        }

        int capturedX = clientPoint.X;
        int capturedY = clientPoint.Y;
        return DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var logicalPoint = new Windows.Foundation.Point(
                    capturedX / scale,
                    capturedY / scale);
                if (WidgetShellControl.IsPointOverGroupTitleBar(
                        RootElement,
                        logicalPoint))
                {
                    WidgetShellControl.HandleNativeGroupTitleWheel(delta);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetGroup] Queued native wheel failed: {ex}");
            }
        });
    }

    protected void RefreshWidgetGroupPresentation(
        bool animateIdentity = false,
        WidgetGroupSwitchOrigin origin = WidgetGroupSwitchOrigin.Programmatic,
        bool forward = true)
    {
        WidgetGroupPresentation? presentation =
            App.Current?.WidgetManager?.GetWidgetGroupPresentation(Config.Id);
        _nativeGroupWheelEnabled =
            presentation is { WheelSwitchEnabled: true } &&
            presentation.Members.Count > 1;
        Diagnostics.SetSurfaceId(presentation?.SurfaceId);
        WidgetShellControl.SetGroupPresentation(
            presentation,
            animateIdentity,
            origin,
            forward);
        QueueGroupDetachPreviewWarmup(presentation);
        OnWidgetGroupPresentationChanged(presentation);
    }

    protected virtual void OnWidgetGroupPresentationChanged(
        WidgetGroupPresentation? presentation)
    {
    }

    protected void SynchronizeWidgetGroupLayout()
    {
        WidgetManager? manager = App.Current?.WidgetManager;
        manager?.SynchronizeGroupLayoutFromMember(Config);
        manager?.CaptureCurrentTopologyLayout(Config);
    }

    private void WidgetManager_WidgetGroupsChanged()
    {
        if (IsClosing)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshWidgetGroupState();
        }
        else
        {
            DispatcherQueue.TryEnqueue(RefreshWidgetGroupState);
        }
    }

    private void RefreshWidgetGroupState()
    {
        RefreshWidgetGroupPresentation();
        if (!_collapseInitialized || IsClosing)
        {
            return;
        }

        // Group members share one persistent window. A behavior change or an
        // in-place member switch replaces Config without recreating that host,
        // so refresh the effective behavior and its hover recovery explicitly.
        if (_lastEffectiveCollapseBehavior != EffectiveCollapseBehavior)
        {
            ApplyCompactTooltips();
            ApplyEffectiveCollapseBehavior(animate: true);
            return;
        }

        StartCompactHoverRecoveryProbe();
        SynchronizeCompactHoverFromCurrentCursor();
    }

    private async void WidgetShellControl_GroupMemberInvoked(
        object? sender,
        WidgetGroupMemberEventArgs e)
    {
        if (App.Current?.WidgetManager is not { } manager)
        {
            return;
        }

        bool succeeded = false;
        try
        {
            // Selecting the currently visible member while another one is
            // still loading is a valid request to cancel that pending switch.
            succeeded = await manager.SwitchWidgetGroupMemberAsync(
                e.WidgetId,
                e.Origin);
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetGroup] Switch failed target={e.WidgetId}: {ex}");
        }
        finally
        {
            WidgetShellControl.NotifyGroupMemberInvocationCompleted(
                e.WidgetId,
                succeeded,
                e.TabSelectionRequestVersion);
        }
    }

    private async void WidgetShellControl_GroupMemberRemoveRequested(
        object? sender,
        WidgetGroupMemberEventArgs e)
    {
        if (App.Current?.WidgetManager is { } manager)
        {
            try
            {
                await manager.RemoveWidgetFromGroupAsync(e.WidgetId, revealStandalone: true);
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetGroup] Remove member failed id={e.WidgetId}: {ex}");
            }
        }
    }

    private async void WidgetShellControl_GroupMemberDetachRequested(
        object? sender,
        WidgetGroupMemberEventArgs e)
    {
        // None is also the native result for Esc/canceled drags. Only a
        // normal release outside the whole window may separate a member.
        if (_groupDetachDragCanceled || Win32Helper.IsKeyDown(0x1B) ||
            Win32Helper.IsAnyMouseButtonDown())
        {
            ClearGroupDetachDragAnchor();
            CloseGroupDetachPlacementPreview();
            return;
        }
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            ClearGroupDetachDragAnchor();
            return;
        }

        bool isOutside = IsCursorOutsideGroupWindow(cursor);
        PointInt32? detachedPosition = TryResolveDetachedPosition(
            e.WidgetId,
            cursor);
        if (!isOutside ||
            detachedPosition is null ||
            App.Current?.WidgetManager is not { } manager)
        {
            ClearGroupDetachDragAnchor();
            CloseGroupDetachPlacementPreview();
            return;
        }

        _groupDetachCommitInProgress = true;
        StopGroupDetachPreviewPolling();
        var detachedBounds = new RectInt32(
            detachedPosition.Value.X,
            detachedPosition.Value.Y,
            AppWindow.Size.Width,
            AppWindow.Size.Height);
        _groupDetachPlacementPreview?.MarkCommitted(detachedBounds);
        ClearGroupDetachDragAnchor();
        try
        {
            await manager.RemoveWidgetFromGroupAsync(
                e.WidgetId,
                revealStandalone: true,
                detachedPosition: detachedPosition);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetGroup] Drag detach failed id={e.WidgetId}: {ex}");
        }
        finally
        {
            _groupDetachCommitInProgress = false;
            WidgetDetachPlacementPreviewWindow? preview =
                _groupDetachPlacementPreview;
            _groupDetachPlacementPreview = null;
            if (preview is not null)
            {
                await preview.FadeOutAndHideAsync();
            }
        }
    }

    private void WidgetShellControl_GroupMemberDetachDragStarted(
        object? sender,
        WidgetGroupMemberEventArgs e)
    {
        _groupDetachDragCanceled = false;
        App.Current?.WidgetManager?.CancelWidgetSurfaceSwitch(Config.Id);
        if (!Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            ClearGroupDetachDragAnchor();
            return;
        }

        var position = AppWindow.Position;
        _groupDetachDragMemberId = e.WidgetId;
        _groupDetachDragOffsetX = cursor.X - position.X;
        _groupDetachDragOffsetY = cursor.Y - position.Y;
        StartGroupDetachPlacementPreview(cursor);
    }

    private void WidgetShellControl_GroupMemberDetachDragCompleted(
        object? sender,
        WidgetGroupMemberEventArgs e)
    {
        if (!_groupDetachCommitInProgress)
        {
            ClearGroupDetachDragAnchor();
            CloseGroupDetachPlacementPreview();
        }
    }

    private void StartGroupDetachPlacementPreview(Win32Helper.POINT cursor)
    {
        CloseGroupDetachPlacementPreview();
        _groupDetachCommitInProgress = false;

        WidgetDetachPlacementPreviewWindow preview;
        try
        {
            App app = App.Current
                ?? throw new InvalidOperationException("DeskBox application is unavailable.");
            WidgetManager manager = app.WidgetManager
                ?? throw new InvalidOperationException("Widget manager is unavailable.");
            string caption = app.LocalizationService.T(
                "Widget.Group.DetachDragCaption");
            preview = manager
                .AcquireWidgetDetachPlacementPreview(
                    caption,
                    GetCurrentSurfaceCornerRadius());
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetGroup] Failed to create detach preview: {ex}");
            return;
        }

        _groupDetachPlacementPreview = preview;
        var sourcePosition = AppWindow.Position;
        var sourceSize = AppWindow.Size;
        int offsetX = _groupDetachDragOffsetX;
        int offsetY = _groupDetachDragOffsetY;
        preview.Update(
            new RectInt32(
                cursor.X - offsetX,
                cursor.Y - offsetY,
                sourceSize.Width,
                sourceSize.Height),
            visible: false);

        var cancellation = new CancellationTokenSource();
        _groupDetachPreviewCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (Win32Helper.IsKeyDown(0x1B))
                    {
                        _groupDetachDragCanceled = true;
                    }
                    if (Win32Helper.GetCursorPos(out Win32Helper.POINT point))
                    {
                        const int releaseMargin = 8;
                        bool outside =
                            point.X < sourcePosition.X - releaseMargin ||
                            point.Y < sourcePosition.Y - releaseMargin ||
                            point.X > sourcePosition.X +
                                sourceSize.Width + releaseMargin ||
                            point.Y > sourcePosition.Y +
                                sourceSize.Height + releaseMargin;
                        preview.Update(
                            new RectInt32(
                                point.X - offsetX,
                                point.Y - offsetY,
                                sourceSize.Width,
                                sourceSize.Height),
                            outside && !_groupDetachDragCanceled);
                    }

                    await Task.Delay(16, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetGroup] Detach preview tracking failed: {ex}");
            }
        }, token);
    }

    private void StopGroupDetachPreviewPolling()
    {
        CancellationTokenSource? cancellation =
            _groupDetachPreviewCancellation;
        _groupDetachPreviewCancellation = null;
        if (cancellation is null)
        {
            return;
        }

        cancellation.Cancel();
        cancellation.Dispose();
    }

    private void CloseGroupDetachPlacementPreview()
    {
        StopGroupDetachPreviewPolling();
        WidgetDetachPlacementPreviewWindow? preview =
            _groupDetachPlacementPreview;
        _groupDetachPlacementPreview = null;
        preview?.Hide();
    }

    private void QueueGroupDetachPreviewWarmup(
        WidgetGroupPresentation? presentation)
    {
        if (presentation is null || _groupDetachPreviewWarmupQueued)
        {
            return;
        }

        _groupDetachPreviewWarmupQueued = true;
        DispatcherQueue.TryEnqueue(
            Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () =>
            {
                App? app = App.Current;
                WidgetManager? manager = app?.WidgetManager;
                if (!_groupingInitialized ||
                    WidgetShellControl.HasWidgetGroup == false ||
                    app is null ||
                    manager is null)
                {
                    return;
                }

                string caption = app.LocalizationService.T(
                    "Widget.Group.DetachDragCaption");
                manager.PrewarmWidgetDetachPlacementPreview(
                    caption,
                    GetCurrentSurfaceCornerRadius());
            });
    }

    private PointInt32? TryResolveDetachedPosition(
        string widgetId,
        Win32Helper.POINT cursor)
    {
        if (!string.Equals(
                widgetId,
                _groupDetachDragMemberId,
                StringComparison.Ordinal))
        {
            return null;
        }

        return new PointInt32(
            cursor.X - _groupDetachDragOffsetX,
            cursor.Y - _groupDetachDragOffsetY);
    }

    private void ClearGroupDetachDragAnchor()
    {
        _groupDetachDragMemberId = null;
        _groupDetachDragOffsetX = 0;
        _groupDetachDragOffsetY = 0;
    }

    private bool IsCursorOutsideGroupWindow(Win32Helper.POINT cursor)
    {
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        const int releaseMargin = 8;
        return cursor.X < position.X - releaseMargin ||
               cursor.Y < position.Y - releaseMargin ||
               cursor.X > position.X + size.Width + releaseMargin ||
               cursor.Y > position.Y + size.Height + releaseMargin;
    }

    private async void WidgetShellControl_GroupMemberReorderRequested(
        object? sender,
        WidgetGroupReorderEventArgs e)
    {
        bool succeeded = false;
        try
        {
            if (App.Current?.WidgetManager is { } manager)
            {
                succeeded = await manager.ReorderWidgetGroupMemberAsync(
                    e.SourceWidgetId,
                    e.TargetWidgetId,
                    e.ExpectedGroupId,
                    e.ExpectedMemberIds);
                if (!IsClosing)
                {
                    RefreshWidgetGroupPresentation();
                }
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[WidgetGroup] Reorder failed source={e.SourceWidgetId} " +
                $"target={e.TargetWidgetId}: {ex}");
        }
        finally
        {
            e.Complete(succeeded);
        }
    }

    private async void WidgetShellControl_GroupDissolveRequested(object? sender, EventArgs e)
    {
        if (App.Current?.WidgetManager is { } manager)
        {
            try
            {
                await manager.DissolveWidgetGroupContainingAsync(Config.Id);
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetGroup] Dissolve failed member={Config.Id}: {ex}");
            }
        }
    }

    private void WidgetShellControl_GroupPickerOpened(object? sender, EventArgs e)
    {
        if (_groupPickerInteractionOpen)
        {
            return;
        }

        _groupPickerInteractionOpen = true;
        BeginInteractionLayer("widget-group-picker");
    }

    private void WidgetShellControl_GroupPickerClosed(object? sender, EventArgs e)
    {
        if (!_groupPickerInteractionOpen)
        {
            return;
        }

        _groupPickerInteractionOpen = false;
        ReleaseInteractionLayer("widget-group-picker");
    }
}
