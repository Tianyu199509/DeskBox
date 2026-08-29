// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using System.Diagnostics;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

/// <summary>
/// Shared base class for all desktop widget windows (file, content, quick-capture).
/// Consolidates window setup, backdrop management, layer/Z-order control,
/// drag/resize logic, and display-change restoration that was previously
/// duplicated across ContentWidgetWindow and QuickCaptureWidgetWindow.
/// </summary>
public abstract partial class WidgetWindowBase : Window
{
    private const int MinWidth = (int)SettingsService.MinWidgetWidth;
    private const int MinHeight = (int)SettingsService.MinWidgetHeight;
    private static readonly UIntPtr DesktopPinnedActivationSubclassId = new(0xDDB5);

    private static readonly int[] BackdropRefreshDelays = [80, 240, 580];
    private static readonly TimeSpan InactiveBackdropControllerRetention = TimeSpan.FromSeconds(3);
    private static readonly ConditionalWeakTable<SolidColorBrush, object> MutableBrushes = new();
    private static readonly object MutableBrushMarker = new();

    // ── Protected state: window identity & services ────────────
    // Set by derived classes in their constructors before calling ConfigureWindowCore().
    protected SettingsService SettingsService = null!;
    protected IntPtr HWnd;
    protected AppWindow AppWindow = null!;
    protected WidgetWindowDiagnostics Diagnostics = null!;
    protected WidgetTrayAnimationController TrayAnimation = null!;
    
    // Smart Animation Adapter - for future enhancement
    private static SmartAnimationAdapter? _smartAdapter;
    
    /// <summary>
    /// 智能动画适配器（静态访问点）
    /// </summary>
    public static SmartAnimationAdapter? SmartAnimationAdapter => _smartAdapter;
    internal WidgetDisplayChangeWatcher? DisplayChangeWatcher;

    // ── Protected state: backdrop controllers ──────────────────
    protected DesktopAcrylicController? AcrylicController;
    protected MicaController? MicaController;
    protected bool AcrylicControllerAttached;
    protected bool MicaControllerAttached;
    protected bool LegacyAccentBackdropActive;
    private bool _isInteractionBackdropDowngraded;
    private Windows.UI.Color _lastLegacyAccentTintColor;
    private double _lastLegacyAccentOpacity;
    private bool? _acrylicControllerUsesBase;
    private bool? _micaControllerUsesAlt;
    private BackdropSignature? _lastAppliedBackdropSignature;
    private WinUIEx.TransparentTintBackdrop? _solidColorBackdrop;
    protected SystemBackdropConfiguration? BackdropConfiguration;
    protected ICompositionSupportsSystemBackdrop? BackdropTarget;
    protected bool IsSolidColorBackdropActive { get; private set; }

    // ── Protected state: backdrop refresh ──────────────────────
    protected long BackdropRefreshGeneration;
    private DispatcherQueueTimer? _backdropRefreshTimer;
    private DispatcherQueueTimer? _inactiveBackdropCleanupTimer;
    private int _backdropRefreshStage;
    private bool _isTrackedForDiagnostics;

    // ── Protected state: drag & resize ─────────────────────────
    protected bool IsDragging;
    protected bool HasMovedTitleBarDrag;
    protected bool IsResizing;
    protected bool IsApplyingBounds;
    protected string ResizeDirection = string.Empty;
    protected Win32Helper.POINT InitialCursorPt;
    protected PointInt32 InitialWindowPos;
    protected SizeInt32 InitialWindowSize;
    protected FrameworkElement? DragCaptureElement;
    private bool _isCoordinatedMoveDrag;
    private bool _deferTitleBarDragConfigUpdates;
    private bool _deferInteractiveResizeConfigUpdates;
    private PendingTitleBarDragFrame? _pendingTitleBarDragFrame;
    private IDisposable? _titleBarDragFrameRegistration;
    private RectInt32? _pendingInteractiveResizeBounds;
    private IDisposable? _interactiveResizeFrameRegistration;
    private IDisposable? _interactiveResizeClockBoostLease;
    private SizeInt32 _interactiveResizeMinimumSize;
    private bool _isDisplayTopologyTransitionActive;
    private long _displayTopologyTransitionGeneration;
    private XamlRoot? _observedXamlRoot;
    private double _observedRasterizationScale;

    // ── Protected state: layer / Z-order ───────────────────────
    protected bool IsAtDesktopLayer;
    // Manager-initiated raises represent a shared presentation state (startup,
    // group topology changes), not an individual pointer interaction. Content
    // hosts must not independently undo that state on their own deactivation.
    protected bool IsRaisedFromManager;
    protected bool KeepRaisedUntilDeactivate;
    protected bool RestoreDesktopLayerWhenIdle;
    protected bool IsHideAnimationRunning;
    protected DateTime LastElevateForInteractionUtc = DateTime.MinValue;
    protected DispatcherQueueTimer? TopMostSafetyTimer;
    private Win32Helper.SubclassProc? _desktopPinnedActivationSubclassProc;
    private bool _isDesktopPinnedActivationSubclassInstalled;
    private PointerEventHandler? _desktopPinnedPointerPressedHandler;

    // ── Protected state: closing ───────────────────────────────
    protected bool IsClosing;

    /// <summary>
    /// Parameterless constructor required by the WinUI 3 XAML compiler.
    /// Derived classes must set the protected fields (SettingsService, HWnd, etc.)
    /// in their own constructors before calling ConfigureWindowCore().
    /// </summary>
    protected WidgetWindowBase()
    {
        // 初始化智能动画适配器（只在第一次）
        if (_smartAdapter is null)
        {
            try
            {
                _smartAdapter = new SmartAnimationAdapter(
                    DispatcherQueue.GetForCurrentThread(),
                    msg => Debug.WriteLine($"[SmartAnimation] {msg}"));
                
                var level = _smartAdapter.GetCurrentHardwareLevel();
                Debug.WriteLine($"[SmartAnimation] Hardware Level: {level}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SmartAnimation] Failed to initialize: {ex.Message}");
                // 回退到默认模式
                _smartAdapter = null;
            }
        }
    }

    // ── Abstract members: each subclass must provide ───────────

    /// <summary>The widget configuration for this window.</summary>
    public abstract WidgetConfig Config { get; }

    /// <summary>The public XAML root exposed through the host-neutral manager contract.</summary>
    public FrameworkElement? WindowContentRoot => Content as FrameworkElement;

    /// <summary>
    /// Whether the window currently sits above its resting desktop layer.
    /// This is intentionally logical state rather than the Win32 TOPMOST flag:
    /// DeskBox temporarily raises normal-band windows without leaving them
    /// permanently topmost.
    /// </summary>
    public bool IsRaisedAboveDesktopLayer =>
        !IsAtDesktopLayer || _isRaisedForExpandedState;

    /// <summary>The opacity value (0–1) used for backdrop tinting.</summary>
    protected abstract double WidgetOpacity { get; }

    /// <summary>The root XAML element (typically RootGrid).</summary>
    protected abstract FrameworkElement RootElement { get; }

    /// <summary>The shared chrome used to render expanded and compact widget states.</summary>
    protected abstract WidgetShell WidgetShellControl { get; }

    internal bool HasActiveVisualWork =>
        IsDragging ||
        IsResizing ||
        IsHideAnimationRunning ||
        TrayAnimation.IsPositionTransitionActive ||
        WidgetShellControl.HasActiveVisualWork;

    /// <summary>Log prefix used in Z-order and backdrop log messages.</summary>
    protected abstract string LogPrefix { get; }

    /// <summary>Whether the window size is locked by the user.</summary>
    protected abstract bool IsSizeLocked { get; }

    /// <summary>Whether the window position is locked by the user.</summary>
    protected abstract bool IsPositionLocked { get; }

    /// <summary>Build the native backdrop tint color for the current theme.</summary>
    protected abstract Windows.UI.Color BuildNativeBackdropTintColor(bool isDark);

    /// <summary>Update the config object from physical bounds.</summary>
    protected abstract void UpdateConfigBoundsFromPhysical(
        int x, int y, int width, int height, bool persist);

    public void ApplyPerformanceSettings()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(ApplyPerformanceSettings);
            return;
        }

        if (!IsClosing)
        {
            WidgetShellControl.ApplyPerformanceSettings();
        }
    }

    // ── Virtual hooks: subclasses can override for specific behavior ──

    /// <summary>Apply XAML-level surface styling (border brush, plate color, etc.).</summary>
    protected virtual void ApplySurfaceStyle() { }

    /// <summary>Extra guards that block RestoreDesktopLayer (e.g. open flyouts).</summary>
    protected virtual bool HasBlockingFlyoutOpen()
    {
        XamlRoot? xamlRoot = RootElement.XamlRoot;
        return xamlRoot is not null &&
            VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot).Count > 0;
    }

    /// <summary>Allows hosts with custom title bars to update collapse actions.</summary>
    protected virtual void OnCollapseBehaviorChanged(WidgetCollapseBehavior behavior) { }

    /// <summary>Called after elevation for interaction (e.g. set focus).</summary>
    protected virtual void OnElevated() { }

    /// <summary>Called when a drag has moved beyond the threshold.</summary>
    protected virtual void OnDragMoved() { }

    /// <summary>Called when drag ends with whether it actually moved.</summary>
    protected virtual void OnDragEnd(bool hasMoved) { }

    /// <summary>Called when resize ends.</summary>
    protected virtual void OnResizeEnd() { }

    /// <summary>Called when resize starts (after elevate).</summary>
    protected virtual void OnResizeStart() { }

    /// <summary>Called whenever the compact/capsule visual state changes.</summary>
    protected virtual void OnCompactVisualStateChanged(bool collapsed) { }

    /// <summary>Whether to queue backdrop refresh after loading.</summary>
    protected virtual bool SupportsBackdropRefresh => true;

    /// <summary>
    /// Converts persisted content-card bounds into the physical host bounds.
    /// Group surfaces use this to reserve a same-HWND navigation region.
    /// </summary>
    protected virtual RectInt32 ExpandContentBoundsToHost(RectInt32 contentBounds) =>
        contentBounds;

    /// <summary>
    /// Converts physical host bounds back to persisted content-card bounds.
    /// </summary>
    protected virtual RectInt32 CollapseHostBoundsToContent(RectInt32 hostBounds) =>
        hostBounds;

    /// <summary>
    /// Reports whether the window's expanded content has completed its initial
    /// data load and can be measured safely for compact expansion warm-up.
    /// </summary>
    protected virtual bool IsCompactExpansionWarmupContentReady => true;

    protected Windows.Foundation.Rect GetCurrentAnimationBounds()
    {
        RectInt32 bounds = GetActualWindowBounds();
        return new Windows.Foundation.Rect(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height);
    }

    protected RectInt32 GetActualWindowBounds()
    {
        if (HWnd != IntPtr.Zero && Win32Helper.GetWindowRect(HWnd, out var rect))
        {
            return new RectInt32(
                rect.Left,
                rect.Top,
                Math.Max(1, rect.Right - rect.Left),
                Math.Max(1, rect.Bottom - rect.Top));
        }

        PointInt32 position = AppWindow.Position;
        SizeInt32 size = AppWindow.Size;
        return new RectInt32(
            position.X,
            position.Y,
            Math.Max(1, size.Width),
            Math.Max(1, size.Height));
    }

    public Task WaitForFirstPresentedFrameAsync(CancellationToken cancellationToken)
    {
        return TrayAnimation.WaitForContentReadyAsync(cancellationToken);
    }

    /// <summary>Called during ConfigureWindow to install subclass-specific hooks (e.g. file drop subclass).</summary>
    protected virtual void ConfigureWindowExtra() { }

    /// <summary>Called during ConfigureWindow's RootGrid.Loaded handler.</summary>
    protected virtual void OnRootElementLoaded() { }

    /// <summary>Called during ConfigureWindow's RootGrid.ActualThemeChanged handler.</summary>
    protected virtual void OnRootElementThemeChanged() { }

    // ── Window configuration ───────────────────────────────────

    protected void CleanupBase()
    {
        CancelPendingTitleBarDragFrame();
        CancelPendingInteractiveResizeFrame();
        EndInteractiveResizePerformanceSession();
        RemoveDesktopPinnedPointerRouting();
        RemoveDesktopPinnedActivationGuard();
        WidgetShellControl.HostedContentChanged -= WidgetShellControl_HostedContentChanged;
        CleanupWidgetGrouping();
        CleanupWidgetCollapse();
        StopBackdropRefreshTimer();
        StopInactiveBackdropCleanupTimer();
        ReleaseTopMostSafetyTimer();
        DetachXamlRootScaleWatcher();
        CleanupWidgetForegroundAppearance();
        DisplayChangeWatcher?.Dispose();
        DisplayChangeWatcher = null;
        ClearSolidColorBackdrop();
        DisposeAcrylicController();
        DisposeMicaController();
        WidgetLayerService.ReleaseWindow(HWnd);
        TrackWindowClosedForDiagnostics();
    }

    private void QueueInteractiveResizeBounds(RectInt32 bounds)
    {
        _pendingInteractiveResizeBounds = bounds;
        // Both Win11 and Win10 commit through the shared frame coordinator so
        // resize follows the same present-aligned cadence as drag and capsule
        // transitions. On Win10 this replaces the legacy 8ms burst commits,
        // which could reach ~125Hz of full XAML re-layout on pointer-move
        // bursts; the coordinator consumes only the newest pending bounds per
        // tick, keeping first-commit latency within a single frame.
        _interactiveResizeFrameRegistration ??=
            WidgetCompactAnimationCoordinator.Register(ApplyPendingInteractiveResizeBounds);
    }

    private void ApplyPendingInteractiveResizeBounds()
    {
        if (!IsResizing || _pendingInteractiveResizeBounds is not { } bounds)
        {
            CancelPendingInteractiveResizeFrame();
            return;
        }

        _pendingInteractiveResizeBounds = null;
        ApplyWindowBounds(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            persist: false,
            updateConfig: false);
    }

    private void FlushPendingInteractiveResizeBounds()
    {
        if (_pendingInteractiveResizeBounds is { } bounds)
        {
            _pendingInteractiveResizeBounds = null;
            ApplyWindowBounds(
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                persist: false,
                updateConfig: false);
        }

        CancelPendingInteractiveResizeFrame();
    }

    private void CancelPendingInteractiveResizeFrame()
    {
        _pendingInteractiveResizeBounds = null;
        _interactiveResizeFrameRegistration?.Dispose();
        _interactiveResizeFrameRegistration = null;
    }

    private void BeginInteractiveResizePerformanceSession()
    {
        _interactiveResizeClockBoostLease ??= CompositorClockBoostCoordinator.Acquire();
    }

    private void EndInteractiveResizePerformanceSession()
    {
        _interactiveResizeClockBoostLease?.Dispose();
        _interactiveResizeClockBoostLease = null;
        _interactiveResizeMinimumSize = default;
    }

    private readonly record struct PendingTitleBarDragFrame(
        RectInt32 ProposedBounds,
        int DeltaX,
        int DeltaY);

    protected void TrackWindowClosedForDiagnostics()
    {
        if (!_isTrackedForDiagnostics)
        {
            return;
        }

        PerformanceLogger.TrackWindowClose(LogPrefix);
        _isTrackedForDiagnostics = false;
    }
}
