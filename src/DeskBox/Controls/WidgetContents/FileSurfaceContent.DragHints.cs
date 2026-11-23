using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.DragDrop;
using Windows.ApplicationModel.DataTransfer;

namespace DeskBox.Controls.WidgetContents;

public sealed partial class FileSurfaceContent
{
    internal static DesktopToastRequest CreateDragOutModifierTipRequest(
        string message,
        string actionButtonText,
        Func<Task>? onDismissAsync = null) =>
        new(
            HintId: "drag-out-modifier-tip",
            Message: message,
            Severity: DesktopToastSeverity.Info,
            Priority: DesktopToastPriority.Routine,
            ActionButtonText: actionButtonText,
            OnActionAsync: DesktopToastHudService.OpenDragBehaviorSettingsAsync,
            OnDismissAsync: onDismissAsync,
            DurationMs: 4500,
            KeepVisibleDuringDrag: true);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _outboundModifierTipWatchTimer;
    private long _outboundDragSerial;
    private string? _outboundModifierTipPendingKey;
    private int _outboundModifierTipOutsideTickCount;
    private bool _outboundModifierTipPending;
    private long _outboundVisualRefreshSessionSerial;

    internal static string? ResolveOutboundModifierTipResourceKey(
        DataPackageOperation allowedOperations,
        bool isWindows11OrLater,
        string? managedDragOutAction)
    {
        if (allowedOperations.HasFlag(DataPackageOperation.Copy) &&
            allowedOperations.HasFlag(DataPackageOperation.Move))
        {
            return "Widget.DragOutTip.Modifiers";
        }

        // On Windows 10, Move mode collapses AllowedOperations to single-bit Move
        // to suppress Explorer's #32768 menu, while holding Ctrl BEFORE starting
        // the drag switches the session to Copy (win10CtrlCopyOverride).
        if (!isWindows11OrLater &&
            allowedOperations == DataPackageOperation.Move &&
            string.Equals(
                managedDragOutAction,
                SettingsService.ManagedDragOutActionMove,
                StringComparison.Ordinal))
        {
            return "Widget.DragOutTip.Win10MoveCtrlCopy";
        }

        return null;
    }

    private void ArmOutboundModifierTipWatch(
        DataPackageOperation allowedOperations,
        bool isPlatformDragSession = false)
    {
        StopOutboundModifierTipWatch();

        string? modifierTipKey = _settingsService.Settings.FileWidget.DragOutModifierTipEnabled
            ? ResolveOutboundModifierTipResourceKey(
                allowedOperations,
                WindowsCompatibilityService.IsWindows11OrLater,
                _settingsService.Settings.FileWidget.ManagedDragOutAction)
            : null;
        bool trackModifierTip = modifierTipKey is not null;

        long refreshSerial = 0;
        bool trackVisualRefresh =
            isPlatformDragSession &&
            OutboundDragVisualRefreshCoordinator.Shared.TryBeginDragSession(
                Environment.TickCount64,
                out refreshSerial);

        if (!trackModifierTip && !trackVisualRefresh)
        {
            return;
        }

        if (DispatcherQueue is not { } dispatcher)
        {
            if (refreshSerial != 0)
            {
                OutboundDragVisualRefreshCoordinator.Shared.EndDragSession(refreshSerial);
            }

            return;
        }

        _outboundModifierTipPending = trackModifierTip;
        _outboundModifierTipPendingKey = modifierTipKey;
        _outboundModifierTipOutsideTickCount = 0;
        _outboundVisualRefreshSessionSerial = refreshSerial;

        if (_outboundModifierTipWatchTimer is null)
        {
            _outboundModifierTipWatchTimer = dispatcher.CreateTimer();
            _outboundModifierTipWatchTimer.Interval = TimeSpan.FromMilliseconds(40);
            _outboundModifierTipWatchTimer.IsRepeating = true;
            _outboundModifierTipWatchTimer.Tick += OutboundModifierTipWatchTimer_Tick;
        }

        _outboundModifierTipWatchTimer.Start();
    }

    private void StopOutboundModifierTipWatch()
    {
        _outboundModifierTipWatchTimer?.Stop();
        _outboundModifierTipPending = false;
        _outboundModifierTipPendingKey = null;
        _outboundModifierTipOutsideTickCount = 0;
        if (_outboundVisualRefreshSessionSerial != 0)
        {
            long serial = _outboundVisualRefreshSessionSerial;
            _outboundVisualRefreshSessionSerial = 0;
            OutboundDragVisualRefreshCoordinator.Shared.EndDragSession(serial);
        }
    }

    private void OutboundModifierTipWatchTimer_Tick(
        Microsoft.UI.Dispatching.DispatcherQueueTimer sender,
        object args)
    {
        if (_isDisposed ||
            (!ActiveDeskBoxDragRegistry.HasActiveDrag && _elevatedDragSession is null) ||
            !DesktopToastHudInterop.IsAnyMouseButtonDown())
        {
            StopOutboundModifierTipWatch();
            return;
        }

        IntPtr popoverHwnd = _stackPopoverPopupOpen && _stackPopoverHostWindow is { IsVisible: true } host
            ? host.WindowHandle
            : IntPtr.Zero;

        string? modifierTipKeyToShow = null;
        if (_outboundModifierTipPending)
        {
            if (!_settingsService.Settings.FileWidget.DragOutModifierTipEnabled ||
                string.IsNullOrEmpty(_outboundModifierTipPendingKey))
            {
                _outboundModifierTipPending = false;
                _outboundModifierTipPendingKey = null;
                _outboundModifierTipOutsideTickCount = 0;
            }
            else if (!DesktopToastHudInterop.IsCursorOutsideAllDeskBoxWindows(_hostWindowHandle, popoverHwnd))
            {
                _outboundModifierTipOutsideTickCount = 0;
            }
            else if (++_outboundModifierTipOutsideTickCount >= 2)
            {
                modifierTipKeyToShow = _outboundModifierTipPendingKey;
                _outboundModifierTipPending = false;
                _outboundModifierTipPendingKey = null;
                _outboundModifierTipOutsideTickCount = 0;
            }
        }

        if (_outboundVisualRefreshSessionSerial != 0)
        {
            long serial = _outboundVisualRefreshSessionSerial;
            OutboundCursorEnvironmentSnapshot env =
                DesktopToastHudInterop.SampleOutboundDragRefreshEnvironment(
                    _hostWindowHandle,
                    popoverHwnd);
            OutboundDragVisualRefreshDecision decision =
                OutboundDragVisualRefreshCoordinator.Shared.EvaluateTick(
                    serial,
                    Environment.TickCount64,
                    in env);

            switch (decision.Action)
            {
                case OutboundDragVisualRefreshAction.MoveCursor:
                    if (!DesktopToastHudInterop.TryMoveCursorAndPulseDragOver(decision.TargetPoint))
                    {
                        OutboundDragVisualRefreshDecision failDecision =
                            OutboundDragVisualRefreshCoordinator.Shared.NotifyMoveFailed(serial);
                        if (failDecision.Action == OutboundDragVisualRefreshAction.StopWatching)
                        {
                            _outboundVisualRefreshSessionSerial = 0;
                        }
                    }
                    else
                    {
                        if (decision.IsReturnStep)
                        {
                            App.Log(
                                $"[DragProtocol] stage=ColdStartVisualRefresh widget={WidgetId} " +
                                $"pulse={decision.PulseNumber} complete={decision.CompletesWarmup}");
                        }

                        if (decision.CompletesWarmup)
                        {
                            _outboundVisualRefreshSessionSerial = 0;
                        }
                    }

                    break;

                case OutboundDragVisualRefreshAction.StopWatching:
                    _outboundVisualRefreshSessionSerial = 0;
                    break;
            }
        }

        if (!_outboundModifierTipPending && _outboundVisualRefreshSessionSerial == 0)
        {
            StopOutboundModifierTipWatch();
        }

        if (modifierTipKeyToShow is not null)
        {
            MaybeShowDragOutModifierTip(modifierTipKeyToShow);
        }
    }

    // Teaches the modifier vocabulary only after the drag actually crosses
    // outside DeskBox-owned window boundaries. On by default; settings (or the
    // HUD ✕ button) can switch it off.
    private void MaybeShowDragOutModifierTip(
        string resourceKey)
    {
        if (!_settingsService.Settings.FileWidget.DragOutModifierTipEnabled ||
            string.IsNullOrWhiteSpace(resourceKey))
        {
            return;
        }

        // Contract §9.23: never create or activate a top-level HUD window
        // synchronously inside the DragStarting callback frame.
        long dragSerial = ++_outboundDragSerial;
        if (DispatcherQueue is { } dispatcher)
        {
            _ = dispatcher.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    // Serial + live-registry check kills the late tip: a
                    // Low-priority enqueue can land after the user already
                    // finished the drag, and "hold Shift" advice after the
                    // fact is noise. HasActiveDrag alone is not enough —
                    // starting the NEXT drag would also pass that check.
                    if (_isDisposed ||
                        dragSerial != _outboundDragSerial ||
                        !ActiveDeskBoxDragRegistry.HasActiveDrag ||
                        !_settingsService.Settings.FileWidget.DragOutModifierTipEnabled)
                    {
                        return;
                    }

                    DesktopToastHudService.Shared.TryShow(
                        CreateDragOutModifierTipRequest(
                            T(resourceKey),
                            T("Widget.DragHud.Action.OpenSettings"),
                            DisableDragOutModifierTipAsync),
                        _settingsService);
                });
            return;
        }

        DesktopToastHudService.Shared.TryShow(
            CreateDragOutModifierTipRequest(
                T(resourceKey),
                T("Widget.DragHud.Action.OpenSettings"),
                DisableDragOutModifierTipAsync),
            _settingsService);
    }

    internal static bool ShouldSuppressDragOutResultHint(
        bool handledAsStackMembership,
        ExternalDragObservation observation,
        DropTargetWindowKind targetKind,
        bool win10MoveRejectedByNonExplorer = false) =>
        handledAsStackMembership ||
        observation == ExternalDragObservation.None ||
        targetKind == DropTargetWindowKind.DeskBoxOwnWindow ||
        win10MoveRejectedByNonExplorer;

    private void MaybeShowDragOutResultHint(
        DataPackageOperation dropResult,
        DataPackageOperation allowedOperations,
        ExternalDragObservation observation,
        bool handledAsStackMembership,
        DropTargetWindowKind targetKind = DropTargetWindowKind.NonExplorerWindow,
        bool win10MoveRejectedByNonExplorer = false)
    {
        if (!_settingsService.Settings.FileWidget.DragOutResultHintEnabled ||
            ShouldSuppressDragOutResultHint(
                handledAsStackMembership,
                observation,
                targetKind,
                win10MoveRejectedByNonExplorer))
        {
            return;
        }

        string? key = GetDragOutResultHintKey(dropResult, allowedOperations);
        if (key is null)
        {
            return;
        }

        DesktopToastHudService.Shared.TryShow(
            new DesktopToastRequest(
                HintId: "drag-out-result-tip",
                Message: T(key),
                Severity: DesktopToastSeverity.Info,
                Priority: DesktopToastPriority.Routine,
                ActionButtonText: T("Widget.DragHud.Action.OpenSettings"),
                OnActionAsync: DesktopToastHudService.OpenDragBehaviorSettingsAsync,
                OnDismissAsync: DisableDragOutResultHintAsync,
                DurationMs: 4500),
            _settingsService);
    }
}
