using System.Diagnostics;
using System.Text;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.UI.ViewManagement;
using WinRT.Interop;
using static DeskBox.Platform.MonitorAndBackdropInterop;

namespace DeskBox.Views;

internal enum WidgetPopoverPlacement
{
    Side = 0,
    Bottom = 1,
    BottomRight = 2
}

/// <summary>
/// Standalone borderless Acrylic popover window for out-of-widget editors and
/// previews (Clipboard Peek/Edit/OCR, Countdown event editor, AI credentials,
/// Custom Widget secrets, Todo export).
///
/// <para>Hosting text editors in a real <see cref="Window"/> with its own
/// <see cref="XamlRoot"/> and <c>ContentIsland</c> eliminates both WinUI 3
/// <c>PopupWindowSiteBridge</c> defects seen on unconstrained <see cref="Flyout"/>s:
/// (1) intermittent CJK/TSF composition loss caused by <c>WS_EX_NOACTIVATE</c>
/// popup bridges, and (2) Windows IME candidate bar coordinate offsets caused
/// by popup content sharing the owner widget's <see cref="XamlRoot"/> across
/// separate top-level HWNDs at non-100% DPI scales.</para>
/// </summary>
internal sealed class WidgetEditorPopoverWindow : Window
{
    private const uint GwChild = 5;
    private const int VkProcessKey = 229;
    private const long ImeCompositionGraceMs = 95;
    private const long TriggerLightDismissSuppressionMs = 480;
    private const double BorderStrokeHorizontalDip = 2;
    private const double BorderStrokeVerticalDip = 2;
    private const double AnchorGapDip = 6;
    private const double ScreenMarginDip = 12;
    private const double VerticalScrollbarAllowanceDip = 14;

    private static readonly HashSet<WidgetEditorPopoverWindow> s_activePopovers = [];
    private static long s_globalLastCompositionEndedTickMs = -10_000;
    private static int s_globalActiveCompositionCount;

    private static IntPtr s_lastLightDismissOwnerHwnd;
    private static string? s_lastLightDismissLogicalKey;
    private static WeakReference<FrameworkElement>? s_lastLightDismissTrigger;
    private static Win32Helper.POINT s_lastLightDismissCursor;
    private static bool s_lastLightDismissHasCursor;
    private static long s_lastLightDismissTickMs;

    private readonly AppWindow _appWindow;
    private readonly IntPtr _ownerWindowHandle;
    private readonly Grid _hostRoot;
    private readonly Border _surfaceBorder;
    private readonly ScrollViewer _scrollViewer;
    private readonly string _backdropKind;
    private readonly KeyEventHandler _previewKeyDownHandler;
    private readonly KeyEventHandler _keyDownHandler;
    private readonly PointerEventHandler _pointerPressedHandler;
    private readonly RightTappedEventHandler _rightTappedHandler;
    private readonly HashSet<Popup> _watchedChildPopups = [];
    private readonly HashSet<DependencyObject> _imeTrackedControls = [];

    private FlyoutBridgeBandPromoter? _bridgeBandPromoter;
    private DispatcherQueueTimer? _textInputHostWatchTimer;
    private AppWindow? _ownerAppWindow;
    private IDisposable? _compactInteractionLease;
    private bool _holdsWidgetInteraction;
    private string _interactionScope = "WidgetPopover";
    private string? _logicalKey;
    private FrameworkElement? _anchor;
    private FrameworkElement? _triggerElement;
    private FrameworkElement? _content;
    private Control? _initialFocusTarget;
    private WidgetPopoverPlacement _placement;
    private double _minWidthDip = 264;
    private double _maxWidthDip = 404;
    private bool _isShown;
    private bool _isClosing;
    private bool _isClosed;
    private bool _notifiedClosed;
    private bool _handlersDetached;
    private long _showSessionId;
    private long _shownAtTickMs;
    private int _activeCompositionCount;
    private long _lastCompositionEndedTickMs = -10_000;

    public WidgetEditorPopoverWindow(IntPtr ownerWindowHandle, string? title = null)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "DeskBox Popover" : title;
        _ownerWindowHandle = ownerWindowHandle;

        WindowHandle = WindowNative.GetWindowHandle(this);
        WindowId windowId = Win32Interop.GetWindowIdFromWindow(WindowHandle);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        WindowShellState.TryHideFromSwitchers(_appWindow);
        WindowShellState.TryApplyBorderlessOverlappedPresenter(_appWindow);

        ConfigureWindowStyles();
        _backdropKind = WindowsCompatibilityService.ApplySafeBackdrop(this, preferMica: false);

        _scrollViewer = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            HorizontalScrollMode = ScrollMode.Disabled
        };

        _surfaceBorder = new Border
        {
            CornerRadius = WindowsCompatibilityService.SupportsNativeWindowCorners
                ? new CornerRadius(8)
                : new CornerRadius(0),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 12, 14, 14),
            Child = _scrollViewer
        };

        _hostRoot = new Grid();
        _hostRoot.Children.Add(_surfaceBorder);

        _previewKeyDownHandler = new KeyEventHandler(HostRoot_PreviewKeyDown);
        _keyDownHandler = new KeyEventHandler(HostRoot_KeyDown);
        _pointerPressedHandler = new PointerEventHandler(HostRoot_PointerPressed);
        _rightTappedHandler = new RightTappedEventHandler(HostRoot_RightTapped);

        _hostRoot.AddHandler(UIElement.PreviewKeyDownEvent, _previewKeyDownHandler, handledEventsToo: true);
        _hostRoot.AddHandler(UIElement.KeyDownEvent, _keyDownHandler, handledEventsToo: false);
        _hostRoot.AddHandler(UIElement.PointerPressedEvent, _pointerPressedHandler, handledEventsToo: true);
        _hostRoot.AddHandler(UIElement.RightTappedEvent, _rightTappedHandler, handledEventsToo: true);
        _hostRoot.ContextRequested += HostRoot_ContextRequested;
        _hostRoot.GotFocus += HostRoot_GotFocus;

        Content = _hostRoot;

        Activated += OnWindowActivated;
        Closed += OnWindowClosed;
    }

    internal event EventHandler? PopoverClosed;

    internal IntPtr WindowHandle { get; }

    internal IntPtr OwnerWindowHandle => _ownerWindowHandle;

    internal string? LogicalKey => _logicalKey;

    internal bool IsShown => !_isClosed && !_isClosing && _isShown;

    internal static bool IsActivePopoverWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero || s_activePopovers.Count == 0)
        {
            return false;
        }

        foreach (WidgetEditorPopoverWindow popover in s_activePopovers)
        {
            if (popover.IsShown && popover.WindowHandle == windowHandle)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool HasOpenPopoverForOwner(IntPtr ownerWindowHandle)
    {
        if (ownerWindowHandle == IntPtr.Zero || s_activePopovers.Count == 0)
        {
            return false;
        }

        foreach (WidgetEditorPopoverWindow popover in s_activePopovers)
        {
            if (!popover._isClosed &&
                (popover._isShown || popover._isClosing) &&
                popover._ownerWindowHandle == ownerWindowHandle)
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsPopoverOwnedBy(IntPtr popoverWindowHandle, IntPtr ownerWindowHandle)
    {
        if (popoverWindowHandle == IntPtr.Zero ||
            ownerWindowHandle == IntPtr.Zero ||
            s_activePopovers.Count == 0)
        {
            return false;
        }

        IntPtr current = popoverWindowHandle;
        for (int depth = 0; depth < 4 && current != IntPtr.Zero; depth++)
        {
            IntPtr nextOwner = IntPtr.Zero;
            foreach (WidgetEditorPopoverWindow popover in s_activePopovers)
            {
                if (popover.IsShown && popover.WindowHandle == current)
                {
                    if (popover._ownerWindowHandle == ownerWindowHandle)
                    {
                        return true;
                    }

                    nextOwner = popover._ownerWindowHandle;
                    break;
                }
            }

            current = nextOwner;
        }

        return false;
    }

    internal static void ClosePopoversForOwner(
        IntPtr ownerWindowHandle,
        bool restoreOwnerForeground = false)
    {
        if (ownerWindowHandle == IntPtr.Zero || s_activePopovers.Count == 0)
        {
            return;
        }

        WidgetEditorPopoverWindow[] snapshot = [.. s_activePopovers];
        foreach (WidgetEditorPopoverWindow popover in snapshot)
        {
            if (popover.IsShown && popover._ownerWindowHandle == ownerWindowHandle)
            {
                popover.ClosePopover(restoreOwnerForeground);
            }
        }
    }

    /// <summary>
    /// Handles toggle-on-trigger-click across Desktop-Pinned, Dynamic, Normal,
    /// Quick Reveal, and QuickPaste modes. Closes any matching active popover
    /// (or suppresses immediate re-open when mouse-down on the trigger already
    /// light-dismissed that same popover) and closes any sibling popovers on
    /// the same owner HWND so popovers never stack.
    /// </summary>
    internal static bool TryConsumeToggleOrCloseExisting(
        IntPtr ownerWindowHandle,
        string? logicalKey,
        FrameworkElement? triggerElement = null)
    {
        bool consumedToggle = false;
        if (s_activePopovers.Count > 0)
        {
            WidgetEditorPopoverWindow[] snapshot = [.. s_activePopovers];
            foreach (WidgetEditorPopoverWindow active in snapshot)
            {
                if (!active.IsShown)
                {
                    continue;
                }

                bool sameOwner = ownerWindowHandle != IntPtr.Zero &&
                                 active._ownerWindowHandle == ownerWindowHandle;
                bool sameLogicalKey = !string.IsNullOrEmpty(logicalKey) &&
                                      string.Equals(active._logicalKey, logicalKey, StringComparison.Ordinal);
                bool sameTrigger = triggerElement is not null &&
                                   ReferenceEquals(active._triggerElement, triggerElement);

                if ((sameOwner || ownerWindowHandle == IntPtr.Zero) && (sameLogicalKey || sameTrigger))
                {
                    active.ClosePopover(restoreOwnerForeground: true);
                    consumedToggle = true;
                }
                else if (sameOwner)
                {
                    active.ClosePopover(restoreOwnerForeground: false);
                }
            }
        }

        if (consumedToggle)
        {
            ClearLightDismissStamp();
            return true;
        }

        long elapsed = Environment.TickCount64 - s_lastLightDismissTickMs;
        if (elapsed >= 0 &&
            elapsed <= TriggerLightDismissSuppressionMs &&
            ownerWindowHandle != IntPtr.Zero &&
            s_lastLightDismissOwnerHwnd == ownerWindowHandle)
        {
            bool keyMatch = !string.IsNullOrEmpty(logicalKey) &&
                            string.Equals(s_lastLightDismissLogicalKey, logicalKey, StringComparison.Ordinal);
            bool triggerMatch = triggerElement is not null &&
                                s_lastLightDismissTrigger is { } weakTrigger &&
                                weakTrigger.TryGetTarget(out FrameworkElement? prevTrigger) &&
                                ReferenceEquals(prevTrigger, triggerElement);

            if ((keyMatch || triggerMatch) &&
                s_lastLightDismissHasCursor &&
                triggerElement is not null &&
                IsScreenPointInsideElement(ownerWindowHandle, triggerElement, s_lastLightDismissCursor))
            {
                ClearLightDismissStamp();
                return true;
            }
        }

        return false;
    }

    internal static bool IsImeCompositionKeyEvent(KeyRoutedEventArgs args)
    {
        if (ImeCompositionGuard.IsImeCompositionKeyEvent(args))
        {
            return true;
        }

        if (s_globalActiveCompositionCount > 0 ||
            Environment.TickCount64 - s_globalLastCompositionEndedTickMs < ImeCompositionGraceMs)
        {
            return true;
        }

        return false;
    }

    internal static void DismissActiveFromDesktopPinnedClick(
        IntPtr clickedWidgetHandle,
        object? originalSource)
    {
        if (s_activePopovers.Count == 0)
        {
            return;
        }

        DependencyObject? sourceDep = originalSource as DependencyObject;
        bool hasCursor = Win32Helper.GetCursorPos(out Win32Helper.POINT cursor);
        WidgetEditorPopoverWindow[] snapshot = [.. s_activePopovers];
        foreach (WidgetEditorPopoverWindow popover in snapshot)
        {
            if (!popover.IsShown)
            {
                continue;
            }

            if (clickedWidgetHandle == popover._ownerWindowHandle &&
                popover._triggerElement is not null &&
                IsDescendantOf(sourceDep, popover._triggerElement))
            {
                continue;
            }

            popover.RecordLightDismissStamp(hasCursor ? cursor : null);
            WidgetEditorPopoverWindow target = popover;
            UiDispatch.PostAfterCallback(() =>
            {
                if (target.IsShown)
                {
                    target.ClosePopover(restoreOwnerForeground: false);
                }
            });
        }
    }

    internal static IntPtr ResolveOwnerWindowHandle(FrameworkElement? element)
    {
        if (element?.XamlRoot?.ContentIslandEnvironment is { } env &&
            env.AppWindowId.Value != 0)
        {
            return Win32Interop.GetWindowFromWindowId(env.AppWindowId);
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// Creates a consistent Fluent 2 top-right close button (<c>\uE711</c>) for
    /// out-of-widget popovers and flyouts.
    /// </summary>
    internal static Button CreateHeaderCloseButton(
        string closeLabel,
        Action onClick,
        DependencyObject? themeScope = null)
    {
        var icon = new FontIcon
        {
            Glyph = "\uE711",
            FontSize = 10
        };
        if (NeutralInteractionBrush.ResolveThemedResource("TextFillColorSecondaryBrush", themeScope) is { } iconBrush)
        {
            icon.Foreground = iconBrush;
        }

        var button = new Button
        {
            Width = 26,
            Height = 26,
            MinWidth = 26,
            MinHeight = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Content = icon
        };
        if (Application.Current?.Resources.TryGetValue("SubtleButtonStyle", out object? styleObj) == true &&
            styleObj is Style subtleStyle)
        {
            button.Style = subtleStyle;
        }

        string accessibleLabel = string.IsNullOrWhiteSpace(closeLabel) ? "Close" : closeLabel;
        AutomationProperties.SetName(button, accessibleLabel);
        ToolTipService.SetToolTip(button, accessibleLabel);
        button.Click += (_, _) => onClick();
        return button;
    }

    internal void SetSurfacePadding(Thickness padding)
    {
        _surfaceBorder.Padding = padding;
    }

    internal void ShowAnchored(
        FrameworkElement anchor,
        FrameworkElement content,
        WidgetPopoverPlacement placement,
        double minWidthDip = 264,
        double maxWidthDip = 404,
        Control? initialFocusTarget = null,
        Action<TextBox>? applyInitialSelection = null,
        FrameworkElement? triggerElement = null,
        string logScope = "WidgetPopover",
        string? logicalKey = null)
    {
        if (_isClosed || _isClosing)
        {
            return;
        }

        ArgumentNullException.ThrowIfNull(anchor);
        ArgumentNullException.ThrowIfNull(content);

        DetachAnchorAndContentHandlers();

        _anchor = anchor;
        _triggerElement = triggerElement;
        _initialFocusTarget = initialFocusTarget;
        _placement = placement;
        _minWidthDip = Math.Max(180, minWidthDip);
        _maxWidthDip = Math.Max(_minWidthDip, maxWidthDip);
        _interactionScope = string.IsNullOrWhiteSpace(logScope) ? "WidgetPopover" : logScope;
        _logicalKey = logicalKey;

        _anchor.Unloaded += Anchor_Unloaded;
        AttachOwnerAppWindowHandler();
        _content = content;
        _content.SizeChanged += Content_SizeChanged;
        _scrollViewer.Content = content;
        AttachImeCompositionTrackingRecursive(content);

        bool isDark = ResolveIsDarkTheme(anchor);
        _hostRoot.FlowDirection = anchor.FlowDirection;
        _hostRoot.RequestedTheme = isDark ? ElementTheme.Dark : ElementTheme.Light;
        Win32Helper.SetWindowTheme(WindowHandle, isDark);
        Win32Helper.DisableWindowFrameShadow(WindowHandle);
        ApplySurfaceThemeBrushes(isDark);

        ++_showSessionId;
        RectInt32 targetBounds = MeasureAndComputeTargetBounds();

        _appWindow.MoveAndResize(targetBounds);
        if (_appWindow.Size.Width != targetBounds.Width ||
            _appWindow.Size.Height != targetBounds.Height)
        {
            _appWindow.MoveAndResize(targetBounds);
        }

        _isShown = true;
        _shownAtTickMs = Environment.TickCount64;
        s_activePopovers.Add(this);
        AcquireInteractionLeasesOnce();

        if (!_appWindow.IsVisible)
        {
            _appWindow.Show();
        }

        _ = Win32Helper.SetWindowPos(
            WindowHandle,
            Win32Helper.HWND_TOPMOST,
            targetBounds.X,
            targetBounds.Y,
            targetBounds.Width,
            targetBounds.Height,
            Win32Helper.SWP_SHOWWINDOW | Win32Helper.SWP_NOOWNERZORDER);

        SetCloaked(false);
        ActivateInput(initialFocusTarget);
        App.Current?.WidgetManager?.ReassertRaisedWidgetGroupAfterDeskBoxActivation(
            _ownerWindowHandle,
            "widget-editor-popover-open");

        if (initialFocusTarget is TextBox textBox && DispatcherQueue is { } dq)
        {
            InlineEditorFocus.FocusWhenLoaded(
                textBox,
                applyInitialSelection ?? (static tb => tb.SelectionStart = tb.Text.Length),
                dq,
                _interactionScope,
                canContinue: () => IsShown,
                onFocused: EnsureIslandKeyboardFocus);
        }
        else if (initialFocusTarget is not null)
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (IsShown)
                {
                    EnsureIslandKeyboardFocus();
                    _ = initialFocusTarget.Focus(FocusState.Programmatic);
                }
            });
        }

        // Re-sync once the window's own XamlRoot finishes its first realized pass.
        DispatcherQueue?.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                if (IsShown)
                {
                    AttachImeCompositionTrackingRecursive(_content);
                    RefreshBoundsFromContent();
                }
            });
    }

    internal void RefreshBoundsFromContent()
    {
        if (!IsShown || _content is null)
        {
            return;
        }

        RectInt32 updated = MeasureAndComputeTargetBounds();
        PointInt32 pos = _appWindow.Position;
        SizeInt32 size = _appWindow.Size;
        if (Math.Abs(size.Width - updated.Width) > 1 ||
            Math.Abs(size.Height - updated.Height) > 1 ||
            Math.Abs(pos.X - updated.X) > 2 ||
            Math.Abs(pos.Y - updated.Y) > 2)
        {
            _appWindow.MoveAndResize(updated);
            if (_appWindow.Size.Width != updated.Width ||
                _appWindow.Size.Height != updated.Height)
            {
                _appWindow.MoveAndResize(updated);
            }
        }
    }

    internal void ActivateInput(UIElement? focusTarget = null)
    {
        if (_isClosed || _isClosing)
        {
            return;
        }

        int extendedStyle = Win32Helper.GetWindowLong(WindowHandle, Win32Helper.GWL_EXSTYLE);
        if ((extendedStyle & Win32Helper.WS_EX_NOACTIVATE) != 0)
        {
            extendedStyle &= ~Win32Helper.WS_EX_NOACTIVATE;
            _ = Win32Helper.SetWindowLong(WindowHandle, Win32Helper.GWL_EXSTYLE, extendedStyle);
        }

        _ = Win32Helper.AllowSetForegroundWindow((uint)Environment.ProcessId);
        _ = SetForegroundWindow(WindowHandle);
        _ = SetActiveWindow(WindowHandle);
        Activate();
        EnsureIslandKeyboardFocus();

        if (focusTarget is Control control)
        {
            _ = control.Focus(FocusState.Programmatic);
        }
        else
        {
            focusTarget?.Focus(FocusState.Programmatic);
        }
    }

    internal void ClosePopover(bool restoreOwnerForeground = true)
    {
        if (_isClosed || _isClosing)
        {
            return;
        }

        _isClosing = true;
        _isShown = false;
        ++_showSessionId;

        bool childPopoverHeldForeground = IsPopoverOwnedBy(Win32Helper.GetForegroundWindow(), WindowHandle);
        ClosePopoversForOwner(WindowHandle, restoreOwnerForeground: false);

        StopTextInputHostWatcher();
        UnwatchAllChildPopups();
        StopBridgeBandPromoter();
        DetachAnchorAndContentHandlers();
        CloseOpenChildPopupsOnXamlRoot();

        SetCloaked(true);

        IntPtr foregroundBeforeHide = Win32Helper.GetForegroundWindow();
        bool ownedForeground = foregroundBeforeHide == WindowHandle ||
                               childPopoverHeldForeground ||
                               IsCurrentProcessPopupBridge(foregroundBeforeHide);

        if (restoreOwnerForeground && ownedForeground)
        {
            if (!WidgetLayerService.UsesDesktopPinnedMode() &&
                _ownerWindowHandle != IntPtr.Zero &&
                Win32Helper.IsWindow(_ownerWindowHandle) &&
                Win32Helper.IsWindowVisible(_ownerWindowHandle) &&
                !WidgetLayerService.IsWindowNoActivate(_ownerWindowHandle))
            {
                _ = SetForegroundWindow(_ownerWindowHandle);
                _ = SetActiveWindow(_ownerWindowHandle);
                foregroundBeforeHide = _ownerWindowHandle;
            }
            else
            {
                App.Current?.WidgetManager?.RestoreForegroundToRaisedWidgetBeforeAuxiliaryHide(WindowHandle);
                foregroundBeforeHide = Win32Helper.GetForegroundWindow();
            }
        }

        try
        {
            if (_appWindow.IsVisible)
            {
                _appWindow.Hide();
            }
        }
        catch
        {
        }

        if (foregroundBeforeHide != IntPtr.Zero &&
            foregroundBeforeHide != WindowHandle &&
            !WidgetLayerService.UsesDesktopPinnedMode() &&
            App.Current?.IsDeskBoxWindow(foregroundBeforeHide) == true &&
            Win32Helper.IsWindow(foregroundBeforeHide) &&
            Win32Helper.IsWindowVisible(foregroundBeforeHide) &&
            !WidgetLayerService.IsWindowNoActivate(foregroundBeforeHide) &&
            Win32Helper.GetForegroundWindow() != foregroundBeforeHide)
        {
            _ = SetForegroundWindow(foregroundBeforeHide);
            _ = SetActiveWindow(foregroundBeforeHide);
        }

        s_activePopovers.Remove(this);
        ReleaseInteractionLeasesOnce();

        if (!restoreOwnerForeground && _ownerWindowHandle != IntPtr.Zero)
        {
            App.Current?.WidgetManager?.NotifyTransientPopupDismissedFromWidget(_ownerWindowHandle);
        }

        NotifyPopoverClosedOnce();

        SystemBackdrop = null;
        _scrollViewer.Content = null;
        _content = null;
        _anchor = null;
        _triggerElement = null;
        _initialFocusTarget = null;

        UiDispatch.PostAfterCallback(() =>
        {
            if (!_isClosed)
            {
                try
                {
                    Close();
                }
                catch
                {
                }
            }
        });
    }

    internal static RectInt32 ComputeAnchoredBounds(
        WidgetPopoverPlacement placement,
        Win32Helper.RECT ownerWindowRect,
        Win32Helper.RECT anchorScreenRect,
        Win32Helper.RECT workArea,
        int widthPx,
        int heightPx,
        int gapPx,
        int screenMarginPx,
        bool isRightToLeft = false)
    {
        int safeWidth = Math.Max(120, Math.Min(widthPx, Math.Max(120, (workArea.Right - workArea.Left) - (screenMarginPx * 2))));
        int safeHeight = Math.Max(72, Math.Min(heightPx, Math.Max(72, (workArea.Bottom - workArea.Top) - (screenMarginPx * 2))));

        int minX = workArea.Left + screenMarginPx;
        int maxX = Math.Max(minX, workArea.Right - safeWidth - screenMarginPx);
        int minY = workArea.Top + screenMarginPx;
        int maxY = Math.Max(minY, workArea.Bottom - safeHeight - screenMarginPx);

        int x;
        int y;

        if (placement == WidgetPopoverPlacement.Side)
        {
            int rightCandidate = ownerWindowRect.Right + gapPx;
            int leftCandidate = ownerWindowRect.Left - safeWidth - gapPx;
            bool rightFits = rightCandidate + safeWidth <= workArea.Right - screenMarginPx;
            bool leftFits = leftCandidate >= workArea.Left + screenMarginPx;

            if (isRightToLeft)
            {
                if (leftFits)
                {
                    x = leftCandidate;
                }
                else if (rightFits)
                {
                    x = rightCandidate;
                }
                else
                {
                    int rightRoom = workArea.Right - ownerWindowRect.Right;
                    int leftRoom = ownerWindowRect.Left - workArea.Left;
                    x = leftRoom >= rightRoom ? leftCandidate : rightCandidate;
                }
            }
            else
            {
                if (rightFits)
                {
                    x = rightCandidate;
                }
                else if (leftFits)
                {
                    x = leftCandidate;
                }
                else
                {
                    int rightRoom = workArea.Right - ownerWindowRect.Right;
                    int leftRoom = ownerWindowRect.Left - workArea.Left;
                    x = rightRoom >= leftRoom ? rightCandidate : leftCandidate;
                }
            }

            y = anchorScreenRect.Top;
        }
        else
        {
            int belowCandidate = anchorScreenRect.Bottom + gapPx;
            int aboveCandidate = anchorScreenRect.Top - safeHeight - gapPx;

            if (belowCandidate + safeHeight <= workArea.Bottom - screenMarginPx)
            {
                y = belowCandidate;
            }
            else if (aboveCandidate >= workArea.Top + screenMarginPx)
            {
                y = aboveCandidate;
            }
            else
            {
                y = belowCandidate;
            }

            bool alignRightEdge = placement == WidgetPopoverPlacement.BottomRight
                ? !isRightToLeft
                : isRightToLeft;
            x = alignRightEdge
                ? anchorScreenRect.Right - safeWidth
                : anchorScreenRect.Left;
        }

        x = Math.Clamp(x, minX, maxX);
        y = Math.Clamp(y, minY, maxY);

        return new RectInt32(x, y, safeWidth, safeHeight);
    }

    private RectInt32 MeasureAndComputeTargetBounds()
    {
        Win32Helper.RECT ownerRect = default;
        bool hasOwnerRect = _ownerWindowHandle != IntPtr.Zero &&
                            Win32Helper.GetWindowRect(_ownerWindowHandle, out ownerRect);

        Win32Helper.RECT anchorRect;
        double hostScale = Math.Max(
            0.5,
            _anchor?.XamlRoot?.RasterizationScale ??
            (_ownerWindowHandle != IntPtr.Zero
                ? Win32Helper.GetDpiScaleForWindow(_ownerWindowHandle, _anchor?.XamlRoot)
                : 1.0));

        if (_anchor is { IsLoaded: true } loadedAnchor && hasOwnerRect)
        {
            Point originDip = loadedAnchor.TransformToVisual(null).TransformPoint(new Point(0, 0));
            var clientOrigin = new Win32Helper.POINT { X = 0, Y = 0 };
            int baseX = Win32Helper.ClientToScreen(_ownerWindowHandle, ref clientOrigin)
                ? clientOrigin.X
                : ownerRect.Left;
            int baseY = clientOrigin.Y != 0 || baseX == clientOrigin.X
                ? clientOrigin.Y
                : ownerRect.Top;

            int left = baseX + (int)Math.Round(originDip.X * hostScale);
            int top = baseY + (int)Math.Round(originDip.Y * hostScale);
            int width = Math.Max(1, (int)Math.Round(loadedAnchor.ActualWidth * hostScale));
            int height = Math.Max(1, (int)Math.Round(loadedAnchor.ActualHeight * hostScale));
            anchorRect = new Win32Helper.RECT
            {
                Left = left,
                Top = top,
                Right = left + width,
                Bottom = top + height
            };
        }
        else if (hasOwnerRect)
        {
            anchorRect = ownerRect;
        }
        else if (Win32Helper.GetCursorPos(out Win32Helper.POINT cursor))
        {
            anchorRect = new Win32Helper.RECT
            {
                Left = cursor.X,
                Top = cursor.Y,
                Right = cursor.X + 1,
                Bottom = cursor.Y + 1
            };
            ownerRect = anchorRect;
        }
        else
        {
            anchorRect = new Win32Helper.RECT { Left = 200, Top = 200, Right = 400, Bottom = 240 };
            ownerRect = anchorRect;
        }

        int probeX = (anchorRect.Left + anchorRect.Right) / 2;
        int probeY = (anchorRect.Top + anchorRect.Bottom) / 2;
        double targetScale = Math.Max(0.5, Win32Helper.GetDpiScaleForPoint(probeX, probeY));

        if (!Win32Helper.TryGetMonitorWorkArea(probeX, probeY, out _, out Win32Helper.RECT workArea))
        {
            workArea = new Win32Helper.RECT
            {
                Left = 0,
                Top = 0,
                Right = 1920,
                Bottom = 1080
            };
        }

        Thickness pad = _surfaceBorder.Padding;
        double chromeHorzDip = pad.Left + pad.Right + BorderStrokeHorizontalDip;
        double chromeVertDip = pad.Top + pad.Bottom + BorderStrokeVerticalDip;

        double maxWindowHeightDip = Math.Max(
            160,
            ((workArea.Bottom - workArea.Top) / targetScale) - (ScreenMarginDip * 2));
        double maxContentWidthDip = Math.Max(140, _maxWidthDip - chromeHorzDip);
        double minContentWidthDip = Math.Max(120, _minWidthDip - chromeHorzDip);

        double contentWidthDip = minContentWidthDip;
        double contentHeightDip = 96;

        if (_content is not null)
        {
            double explicitWidth = !double.IsNaN(_content.Width) && _content.Width > 0
                ? _content.Width
                : 0;
            double probeWidth = explicitWidth > 0
                ? Math.Clamp(explicitWidth, minContentWidthDip, maxContentWidthDip)
                : maxContentWidthDip;

            _content.Measure(new Size(probeWidth, double.PositiveInfinity));
            contentWidthDip = explicitWidth > 0
                ? probeWidth
                : Math.Clamp(Math.Ceiling(_content.DesiredSize.Width), minContentWidthDip, maxContentWidthDip);

            _content.Measure(new Size(contentWidthDip, double.PositiveInfinity));
            contentHeightDip = Math.Max(48, Math.Ceiling(_content.DesiredSize.Height));
        }

        bool needsVerticalScroll = contentHeightDip + chromeVertDip > maxWindowHeightDip + 0.5;
        _scrollViewer.Padding = needsVerticalScroll
            ? new Thickness(0, 0, 10, 0)
            : new Thickness(0);

        double scrollExtraWidthDip = needsVerticalScroll ? VerticalScrollbarAllowanceDip : 0;
        double effectiveMaxWidthDip = _maxWidthDip + scrollExtraWidthDip;
        double totalWidthDip = Math.Clamp(
            contentWidthDip + chromeHorzDip + scrollExtraWidthDip,
            _minWidthDip,
            effectiveMaxWidthDip);
        double totalHeightDip = Math.Clamp(contentHeightDip + chromeVertDip, 76, maxWindowHeightDip);

        int widthPx = StackPopoverPixelCalculator.ToCoveringPhysicalPixels(totalWidthDip, targetScale);
        int heightPx = StackPopoverPixelCalculator.ToCoveringPhysicalPixels(totalHeightDip, targetScale);
        int gapPx = Math.Max(4, (int)Math.Round(AnchorGapDip * targetScale));
        int marginPx = Math.Max(8, (int)Math.Round(ScreenMarginDip * targetScale));
        bool isRtl = _hostRoot.FlowDirection == FlowDirection.RightToLeft;

        return ComputeAnchoredBounds(
            _placement,
            ownerRect,
            anchorRect,
            workArea,
            widthPx,
            heightPx,
            gapPx,
            marginPx,
            isRightToLeft: isRtl);
    }

    private void EnsureIslandKeyboardFocus()
    {
        IntPtr childIslandHwnd = GetWindow(WindowHandle, GwChild);
        IntPtr focusTargetHwnd = childIslandHwnd != IntPtr.Zero ? childIslandHwnd : WindowHandle;
        _ = SetFocus(focusTargetHwnd);
    }

    private void AcquireInteractionLeasesOnce()
    {
        if (!_holdsWidgetInteraction)
        {
            _holdsWidgetInteraction = true;
            App.Current?.WidgetManager?.BeginWidgetInteraction($"popover:{_interactionScope}");
        }

        if (_compactInteractionLease is null && _ownerWindowHandle != IntPtr.Zero)
        {
            _compactInteractionLease = App.Current?.WidgetManager
                ?.FindWidgetWindowByHandle(_ownerWindowHandle)
                ?.AcquirePopoverInteraction($"popover:{_interactionScope}");
        }
    }

    private void ReleaseInteractionLeasesOnce()
    {
        if (_compactInteractionLease is { } lease)
        {
            _compactInteractionLease = null;
            lease.Dispose();
        }

        if (_holdsWidgetInteraction)
        {
            _holdsWidgetInteraction = false;
            App.Current?.WidgetManager?.EndWidgetInteraction($"popover:{_interactionScope}");
        }
    }

    private void NotifyPopoverClosedOnce()
    {
        if (_notifiedClosed)
        {
            return;
        }

        _notifiedClosed = true;
        try
        {
            PopoverClosed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetPopover] PopoverClosed handler error: {ex.Message}");
        }
    }

    private void Anchor_Unloaded(object sender, RoutedEventArgs e)
    {
        if (IsShown)
        {
            ClosePopover(restoreOwnerForeground: false);
        }
    }

    private void Content_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsShown)
        {
            return;
        }

        DispatcherQueue?.TryEnqueue(
            DispatcherQueuePriority.Low,
            RefreshBoundsFromContent);
    }

    private void HostRoot_GotFocus(object sender, RoutedEventArgs e)
    {
        if (!IsShown)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject sourceDep)
        {
            AttachImeCompositionTrackingSingle(sourceDep);
        }

        if (FindTextInputControl(e.OriginalSource as DependencyObject) is { } inputControl &&
            Win32Helper.GetForegroundWindow() != WindowHandle)
        {
            ActivateInput(inputControl);
        }
    }

    private void HostRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!IsShown)
        {
            return;
        }

        ShellContextMenuProxy.CancelOpenMenu();

        if (FindAncestorComboBoxOrFlyoutButton(e.OriginalSource as DependencyObject))
        {
            EnsureBridgeBandPromoterStarted();
            ScheduleChildPopupWatchPass();
        }

        if (FindTextInputControl(e.OriginalSource as DependencyObject) is { } inputControl &&
            Win32Helper.GetForegroundWindow() != WindowHandle)
        {
            ActivateInput(inputControl);
        }
    }

    private void HostRoot_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (!IsShown)
        {
            return;
        }

        EnsureBridgeBandPromoterStarted();
        ScheduleChildPopupWatchPass();
    }

    private void HostRoot_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (!IsShown)
        {
            return;
        }

        EnsureBridgeBandPromoterStarted();
        ScheduleChildPopupWatchPass();
    }

    private void HostRoot_PreviewKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (!IsShown)
        {
            return;
        }

        if (IsImeCompositionActiveOrRecent(args))
        {
            return;
        }

        if (args.Key != Windows.System.VirtualKey.Escape)
        {
            return;
        }

        // Allow focused text editors to process Escape first when needed;
        // unhandled Escape bubbles to HostRoot_KeyDown and closes the popover.
        if (FindTextInputControl(args.OriginalSource as DependencyObject) is not null)
        {
            return;
        }

        args.Handled = true;
        ClosePopover(restoreOwnerForeground: true);
    }

    private void HostRoot_KeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key != Windows.System.VirtualKey.Escape || args.Handled || !IsShown)
        {
            return;
        }

        if (IsImeCompositionActiveOrRecent(args))
        {
            return;
        }

        args.Handled = true;
        ClosePopover(restoreOwnerForeground: true);
    }

    private bool IsImeCompositionActiveOrRecent(KeyRoutedEventArgs args)
    {
        if ((int)args.OriginalKey == VkProcessKey || (int)args.Key == VkProcessKey)
        {
            return true;
        }

        long now = Environment.TickCount64;
        return _activeCompositionCount > 0 ||
               now - _lastCompositionEndedTickMs < ImeCompositionGraceMs ||
               s_globalActiveCompositionCount > 0 ||
               now - s_globalLastCompositionEndedTickMs < ImeCompositionGraceMs;
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (!IsShown)
        {
            return;
        }

        if (args.WindowActivationState != WindowActivationState.Deactivated)
        {
            StopTextInputHostWatcher();
            return;
        }

        bool hasCursor = Win32Helper.GetCursorPos(out Win32Helper.POINT deactivationCursor);
        long sessionId = _showSessionId;

        if (_hostRoot.XamlRoot is { } immediateRoot)
        {
            IReadOnlyList<Popup> immediatePopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(immediateRoot);
            if (immediatePopups.Count > 0)
            {
                EnsureBridgeBandPromoterStarted();
                WatchChildPopups(immediatePopups, sessionId);
            }
        }

        UiDispatch.PostAfterCallback(() =>
            ReconcilePostDeactivation(sessionId, hasCursor ? deactivationCursor : null));
    }

    private void ReconcilePostDeactivation(long sessionId, Win32Helper.POINT? deactivationCursor)
    {
        if (!IsShown || sessionId != _showSessionId)
        {
            return;
        }

        IntPtr foreground = Win32Helper.GetForegroundWindow();
        if (foreground == WindowHandle || IsPopoverOwnedBy(foreground, WindowHandle))
        {
            StopTextInputHostWatcher();
            return;
        }

        if (_hostRoot.XamlRoot is { } currentRoot)
        {
            IReadOnlyList<Popup> openPopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(currentRoot);
            if (openPopups.Count > 0)
            {
                EnsureBridgeBandPromoterStarted();
                WatchChildPopups(openPopups, sessionId);
                if (foreground == IntPtr.Zero || IsCurrentProcessPopupBridge(foreground))
                {
                    return;
                }
            }
            else
            {
                StopBridgeBandPromoter();
            }
        }

        if (IsCurrentProcessPopupBridge(foreground))
        {
            EnsureBridgeBandPromoterStarted();
            ScheduleChildPopupWatchPass();
            return;
        }

        // Preserve open drafts when the user invokes Win+. / Win+; Emoji Picker or Touch Keyboard.
        if (IsWindowsTextInputExperienceWindow(foreground))
        {
            StartTextInputHostWatcher(sessionId);
            return;
        }

        // When opened from a closing MenuFlyout on the owner widget, the menu's
        // native popup bridge teardown can briefly bounce foreground back to the
        // owner HWND a tick after ShowAnchored when no mouse button is pressed.
        if (_ownerWindowHandle != IntPtr.Zero &&
            foreground == _ownerWindowHandle &&
            Environment.TickCount64 - _shownAtTickMs < 180 &&
            !IsAnyMouseButtonDown())
        {
            ActivateInput(_initialFocusTarget);
            return;
        }

        RecordLightDismissStamp(deactivationCursor);
        ClosePopover(restoreOwnerForeground: false);
    }

    private void ScheduleChildPopupWatchPass()
    {
        long sessionId = _showSessionId;
        DispatcherQueue?.TryEnqueue(
            DispatcherQueuePriority.Low,
            () =>
            {
                if (!IsShown || sessionId != _showSessionId || _hostRoot.XamlRoot is not { } root)
                {
                    return;
                }

                IReadOnlyList<Popup> openPopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(root);
                if (openPopups.Count > 0)
                {
                    WatchChildPopups(openPopups, sessionId);
                }
                else if (!IsCurrentProcessPopupBridge(Win32Helper.GetForegroundWindow()))
                {
                    StopBridgeBandPromoter();
                }
            });
    }

    private void WatchChildPopups(IReadOnlyList<Popup> popups, long sessionId)
    {
        foreach (Popup popup in popups)
        {
            if (!_watchedChildPopups.Add(popup))
            {
                continue;
            }

            Popup captured = popup;
            void OnChildPopupClosed(object? s, object e)
            {
                captured.Closed -= OnChildPopupClosed;
                _watchedChildPopups.Remove(captured);

                UiDispatch.PostAfterCallback(() =>
                {
                    if (!IsShown || sessionId != _showSessionId)
                    {
                        return;
                    }

                    if (_hostRoot.XamlRoot is { } root &&
                        VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count == 0)
                    {
                        StopBridgeBandPromoter();
                    }

                    bool hasCursor = Win32Helper.GetCursorPos(out Win32Helper.POINT cursor);
                    ReconcilePostDeactivation(sessionId, hasCursor ? cursor : null);
                });
            }

            captured.Closed += OnChildPopupClosed;
        }
    }

    private void UnwatchAllChildPopups()
    {
        _watchedChildPopups.Clear();
    }

    private void CloseOpenChildPopupsOnXamlRoot()
    {
        try
        {
            if (_hostRoot.XamlRoot is not { } root)
            {
                return;
            }

            IReadOnlyList<Popup> openPopups = VisualTreeHelper.GetOpenPopupsForXamlRoot(root);
            for (int i = 0; i < openPopups.Count; i++)
            {
                openPopups[i].IsOpen = false;
            }
        }
        catch
        {
        }
    }

    private void EnsureBridgeBandPromoterStarted()
    {
        _bridgeBandPromoter ??= FlyoutBridgeBandPromoter.TryBegin();
    }

    private void StopBridgeBandPromoter()
    {
        _bridgeBandPromoter?.Dispose();
        _bridgeBandPromoter = null;
    }

    private void StartTextInputHostWatcher(long sessionId)
    {
        if (_textInputHostWatchTimer is not null || DispatcherQueue is not { } dq)
        {
            return;
        }

        DispatcherQueueTimer timer = dq.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(220);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (!IsShown || sessionId != _showSessionId)
            {
                StopTextInputHostWatcher();
                return;
            }

            IntPtr fg = Win32Helper.GetForegroundWindow();
            if (fg == WindowHandle || IsPopoverOwnedBy(fg, WindowHandle) || IsCurrentProcessPopupBridge(fg))
            {
                StopTextInputHostWatcher();
                return;
            }

            if (fg == IntPtr.Zero || IsWindowsTextInputExperienceWindow(fg))
            {
                return;
            }

            StopTextInputHostWatcher();
            ClosePopover(restoreOwnerForeground: false);
        };
        _textInputHostWatchTimer = timer;
        timer.Start();
    }

    private void StopTextInputHostWatcher()
    {
        if (_textInputHostWatchTimer is { } timer)
        {
            _textInputHostWatchTimer = null;
            timer.Stop();
        }
    }

    private void AttachImeCompositionTrackingRecursive(DependencyObject? root)
    {
        if (root is null)
        {
            return;
        }

        AttachImeCompositionTrackingSingle(root);
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            AttachImeCompositionTrackingRecursive(VisualTreeHelper.GetChild(root, i));
        }

        if (root is Panel panel)
        {
            foreach (UIElement child in panel.Children)
            {
                AttachImeCompositionTrackingRecursive(child);
            }
        }
        else if (root is Border { Child: { } borderChild })
        {
            AttachImeCompositionTrackingRecursive(borderChild);
        }
        else if (root is ContentControl { Content: DependencyObject contentDep })
        {
            AttachImeCompositionTrackingRecursive(contentDep);
        }
        else if (root is ScrollViewer { Content: DependencyObject scrollDep })
        {
            AttachImeCompositionTrackingRecursive(scrollDep);
        }
    }

    private void AttachImeCompositionTrackingSingle(DependencyObject node)
    {
        for (DependencyObject? cur = node; cur is not null; cur = VisualTreeHelper.GetParent(cur))
        {
            if (cur is TextBox tb && _imeTrackedControls.Add(tb))
            {
                tb.TextCompositionStarted += TextBox_TextCompositionStarted;
                tb.TextCompositionEnded += TextBox_TextCompositionEnded;
            }
            else if (cur is RichEditBox reb && _imeTrackedControls.Add(reb))
            {
                reb.TextCompositionStarted += RichEditBox_TextCompositionStarted;
                reb.TextCompositionEnded += RichEditBox_TextCompositionEnded;
            }
            else if (cur is AutoSuggestBox asb && _imeTrackedControls.Add(asb))
            {
                asb.Loaded += AutoSuggestBox_LoadedAttachIme;
            }
        }
    }

    private void AutoSuggestBox_LoadedAttachIme(object sender, RoutedEventArgs e)
    {
        if (sender is DependencyObject dep)
        {
            AttachImeCompositionTrackingRecursive(dep);
        }
    }

    private void DetachImeCompositionTracking()
    {
        foreach (DependencyObject node in _imeTrackedControls)
        {
            if (node is TextBox tb)
            {
                tb.TextCompositionStarted -= TextBox_TextCompositionStarted;
                tb.TextCompositionEnded -= TextBox_TextCompositionEnded;
            }
            else if (node is RichEditBox reb)
            {
                reb.TextCompositionStarted -= RichEditBox_TextCompositionStarted;
                reb.TextCompositionEnded -= RichEditBox_TextCompositionEnded;
            }
            else if (node is AutoSuggestBox asb)
            {
                asb.Loaded -= AutoSuggestBox_LoadedAttachIme;
            }
        }

        if (_activeCompositionCount > 0)
        {
            s_globalActiveCompositionCount = Math.Max(0, s_globalActiveCompositionCount - _activeCompositionCount);
            _activeCompositionCount = 0;
        }

        _imeTrackedControls.Clear();
    }

    private void TextBox_TextCompositionStarted(TextBox sender, TextCompositionStartedEventArgs args)
    {
        _activeCompositionCount++;
        s_globalActiveCompositionCount++;
    }

    private void TextBox_TextCompositionEnded(TextBox sender, TextCompositionEndedEventArgs args)
    {
        _activeCompositionCount = Math.Max(0, _activeCompositionCount - 1);
        s_globalActiveCompositionCount = Math.Max(0, s_globalActiveCompositionCount - 1);
        long now = Environment.TickCount64;
        _lastCompositionEndedTickMs = now;
        s_globalLastCompositionEndedTickMs = now;
    }

    private void RichEditBox_TextCompositionStarted(RichEditBox sender, TextCompositionStartedEventArgs args)
    {
        _activeCompositionCount++;
        s_globalActiveCompositionCount++;
    }

    private void RichEditBox_TextCompositionEnded(RichEditBox sender, TextCompositionEndedEventArgs args)
    {
        _activeCompositionCount = Math.Max(0, _activeCompositionCount - 1);
        s_globalActiveCompositionCount = Math.Max(0, s_globalActiveCompositionCount - 1);
        long now = Environment.TickCount64;
        _lastCompositionEndedTickMs = now;
        s_globalLastCompositionEndedTickMs = now;
    }

    private void AttachOwnerAppWindowHandler()
    {
        if (_ownerWindowHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            WindowId ownerWinId = Win32Interop.GetWindowIdFromWindow(_ownerWindowHandle);
            if (ownerWinId.Value != 0)
            {
                _ownerAppWindow = AppWindow.GetFromWindowId(ownerWinId);
                if (_ownerAppWindow is not null)
                {
                    _ownerAppWindow.Changed += OwnerAppWindow_Changed;
                }
            }
        }
        catch
        {
            _ownerAppWindow = null;
        }
    }

    private void OwnerAppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!IsShown)
        {
            return;
        }

        try
        {
            if (args.DidVisibilityChange && !sender.IsVisible)
            {
                DispatcherQueue?.TryEnqueue(() => ClosePopover(restoreOwnerForeground: false));
                return;
            }
        }
        catch
        {
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            DispatcherQueue?.TryEnqueue(RefreshBoundsFromContent);
        }
    }

    private void DetachAnchorAndContentHandlers()
    {
        if (_ownerAppWindow is not null)
        {
            try
            {
                _ownerAppWindow.Changed -= OwnerAppWindow_Changed;
            }
            catch
            {
            }
            _ownerAppWindow = null;
        }

        if (_anchor is not null)
        {
            _anchor.Unloaded -= Anchor_Unloaded;
        }

        if (_content is not null)
        {
            _content.SizeChanged -= Content_SizeChanged;
        }

        DetachImeCompositionTracking();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        _isClosed = true;
        _isShown = false;
        _isClosing = false;
        ++_showSessionId;

        ClosePopoversForOwner(WindowHandle, restoreOwnerForeground: false);
        s_activePopovers.Remove(this);
        StopTextInputHostWatcher();
        UnwatchAllChildPopups();
        StopBridgeBandPromoter();
        DetachAnchorAndContentHandlers();
        ReleaseInteractionLeasesOnce();
        NotifyPopoverClosedOnce();

        if (!_handlersDetached)
        {
            _handlersDetached = true;
            Activated -= OnWindowActivated;
            Closed -= OnWindowClosed;
            _hostRoot.RemoveHandler(UIElement.PreviewKeyDownEvent, _previewKeyDownHandler);
            _hostRoot.RemoveHandler(UIElement.KeyDownEvent, _keyDownHandler);
            _hostRoot.RemoveHandler(UIElement.PointerPressedEvent, _pointerPressedHandler);
            _hostRoot.RemoveHandler(UIElement.RightTappedEvent, _rightTappedHandler);
            _hostRoot.ContextRequested -= HostRoot_ContextRequested;
            _hostRoot.GotFocus -= HostRoot_GotFocus;
        }

        SystemBackdrop = null;
        _scrollViewer.Content = null;
        _content = null;
        _anchor = null;
        _triggerElement = null;
        _initialFocusTarget = null;
        Content = null;
    }

    private void RecordLightDismissStamp(Win32Helper.POINT? cursor)
    {
        s_lastLightDismissOwnerHwnd = _ownerWindowHandle;
        s_lastLightDismissLogicalKey = _logicalKey;
        s_lastLightDismissTrigger = _triggerElement is not null
            ? new WeakReference<FrameworkElement>(_triggerElement)
            : null;
        if (cursor is { } pt)
        {
            s_lastLightDismissCursor = pt;
            s_lastLightDismissHasCursor = true;
        }
        else
        {
            s_lastLightDismissHasCursor = Win32Helper.GetCursorPos(out s_lastLightDismissCursor);
        }

        s_lastLightDismissTickMs = Environment.TickCount64;
    }

    private static void ClearLightDismissStamp()
    {
        s_lastLightDismissTickMs = 0;
        s_lastLightDismissOwnerHwnd = IntPtr.Zero;
        s_lastLightDismissLogicalKey = null;
        s_lastLightDismissTrigger = null;
        s_lastLightDismissHasCursor = false;
    }

    private static bool IsScreenPointInsideElement(
        IntPtr ownerWindowHandle,
        FrameworkElement element,
        Win32Helper.POINT screenPoint)
    {
        if (!element.IsLoaded ||
            element.XamlRoot is null ||
            element.ActualWidth <= 0 ||
            element.ActualHeight <= 0 ||
            ownerWindowHandle == IntPtr.Zero)
        {
            return false;
        }

        var clientOrigin = new Win32Helper.POINT { X = 0, Y = 0 };
        if (!Win32Helper.ClientToScreen(ownerWindowHandle, ref clientOrigin))
        {
            if (!Win32Helper.GetWindowRect(ownerWindowHandle, out Win32Helper.RECT hostRect))
            {
                return false;
            }

            clientOrigin.X = hostRect.Left;
            clientOrigin.Y = hostRect.Top;
        }

        double scale = Math.Max(0.5, element.XamlRoot.RasterizationScale);
        Point originDip = element.TransformToVisual(null).TransformPoint(default);
        double left = clientOrigin.X + (originDip.X * scale);
        double top = clientOrigin.Y + (originDip.Y * scale);
        double right = left + (element.ActualWidth * scale);
        double bottom = top + (element.ActualHeight * scale);

        return screenPoint.X >= left - 2 &&
               screenPoint.X <= right + 2 &&
               screenPoint.Y >= top - 2 &&
               screenPoint.Y <= bottom + 2;
    }

    private void ConfigureWindowStyles()
    {
        int extendedStyle = Win32Helper.GetWindowLong(WindowHandle, Win32Helper.GWL_EXSTYLE);
        extendedStyle |= Win32Helper.WS_EX_TOOLWINDOW | Win32Helper.WS_EX_TOPMOST;
        extendedStyle &= ~Win32Helper.WS_EX_NOACTIVATE;
        _ = Win32Helper.SetWindowLong(WindowHandle, Win32Helper.GWL_EXSTYLE, extendedStyle);

        int style = Win32Helper.GetWindowLong(WindowHandle, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION |
                   Win32Helper.WS_BORDER |
                   Win32Helper.WS_DLGFRAME |
                   Win32Helper.WS_THICKFRAME);
        _ = Win32Helper.SetWindowLong(WindowHandle, Win32Helper.GWL_STYLE, style);

        _ = Win32Helper.SetWindowPos(
            WindowHandle,
            IntPtr.Zero,
            0,
            0,
            0,
            0,
            Win32Helper.SWP_NOMOVE |
            Win32Helper.SWP_NOSIZE |
            Win32Helper.SWP_NOZORDER |
            Win32Helper.SWP_NOACTIVATE |
            Win32Helper.SWP_FRAMECHANGED);

        if (WindowsCompatibilityService.SupportsNativeWindowCorners)
        {
            int cornerPreference = Win32Helper.DWMWCP_ROUND;
            Win32Helper.TrySetDwmWindowAttribute(
                WindowHandle,
                Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE,
                ref cornerPreference);
        }

        Win32Helper.DisableWindowFrameShadow(WindowHandle);
        SetCloaked(true);
    }

    private void ApplySurfaceThemeBrushes(bool isDark)
    {
        bool highContrast = false;
        try
        {
            highContrast = new AccessibilitySettings().HighContrast;
        }
        catch
        {
        }

        if (highContrast)
        {
            var uiSettings = new UISettings();
            _surfaceBorder.Background = new SolidColorBrush(uiSettings.UIElementColor(UIElementType.Window));
            _surfaceBorder.BorderBrush = new SolidColorBrush(uiSettings.UIElementColor(UIElementType.WindowText));
            return;
        }

        _surfaceBorder.BorderBrush =
            NeutralInteractionBrush.ResolveThemedResource("SurfaceStrokeColorFlyoutBrush", _hostRoot)
            ?? NeutralInteractionBrush.ResolveThemedResource("CardStrokeColorDefaultBrush", _hostRoot)
            ?? new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(96, 255, 255, 255)
                : Windows.UI.Color.FromArgb(64, 0, 0, 0));

        if (string.Equals(_backdropKind, "Solid", StringComparison.Ordinal) ||
            !WindowsCompatibilityService.SupportsNativeWindowCorners)
        {
            _surfaceBorder.Background = new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(255, 36, 36, 36)
                : Windows.UI.Color.FromArgb(255, 249, 249, 249));
        }
        else
        {
            // Subtle tint over DesktopAcrylic so text and input controls match Fluent 2 Flyout contrast.
            _surfaceBorder.Background = new SolidColorBrush(isDark
                ? Windows.UI.Color.FromArgb(148, 32, 32, 36)
                : Windows.UI.Color.FromArgb(168, 250, 250, 252));
        }
    }

    private void SetCloaked(bool cloaked)
    {
        int value = cloaked ? 1 : 0;
        Win32Helper.TrySetDwmWindowAttribute(WindowHandle, Win32Helper.DWMWA_CLOAK, ref value);
    }

    private static bool ResolveIsDarkTheme(FrameworkElement anchor)
    {
        return anchor.ActualTheme switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => NeutralInteractionBrush.IsDarkTheme(anchor)
        };
    }

    private static bool IsCurrentProcessPopupBridge(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        IntPtr root = Win32Helper.GetAncestor(hwnd, Win32Helper.GA_ROOT);
        IntPtr candidate = root != IntPtr.Zero ? root : hwnd;
        foreach (IntPtr bridge in Win32Helper.GetVisiblePopupBridgeWindows())
        {
            if (bridge == candidate || bridge == hwnd)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsWindowsTextInputExperienceWindow(IntPtr hwnd) =>
        TransientInputFocusGuard.IsWindowsTextInputExperienceWindow(hwnd);

    private static bool IsAnyMouseButtonDown() =>
        (Win32Helper.GetAsyncKeyState(0x01) & 0x8000) != 0 ||
        (Win32Helper.GetAsyncKeyState(0x02) & 0x8000) != 0 ||
        (Win32Helper.GetAsyncKeyState(0x04) & 0x8000) != 0;

    private static Control? FindTextInputControl(DependencyObject? source)
    {
        DependencyObject? current = source;
        for (int depth = 0; depth < 12 && current is not null; depth++)
        {
            if (current is TextBox or RichEditBox or PasswordBox or AutoSuggestBox or NumberBox)
            {
                return current as Control;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private static bool FindAncestorComboBoxOrFlyoutButton(DependencyObject? source)
    {
        DependencyObject? current = source;
        for (int depth = 0; depth < 10 && current is not null; depth++)
        {
            if (current is ComboBox or DropDownButton or SplitButton or
                Button { Flyout: not null })
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsDescendantOf(DependencyObject? source, DependencyObject ancestor)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }
}
