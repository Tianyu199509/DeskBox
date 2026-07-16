// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
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
/// duplicated across WidgetWindow, ContentWidgetWindow, and QuickCaptureWidgetWindow.
/// </summary>
public abstract partial class WidgetWindowBase : Window
{
    private const int MinWidth = (int)SettingsService.MinWidgetWidth;
    private const int MinHeight = (int)SettingsService.MinWidgetHeight;

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
    internal WidgetDisplayChangeWatcher? DisplayChangeWatcher;

    // ── Protected state: backdrop controllers ──────────────────
    protected DesktopAcrylicController? AcrylicController;
    protected MicaController? MicaController;
    protected bool AcrylicControllerAttached;
    protected bool MicaControllerAttached;
    private bool? _acrylicControllerUsesBase;
    private bool? _micaControllerUsesAlt;
    protected SystemBackdropConfiguration? BackdropConfiguration;
    protected ICompositionSupportsSystemBackdrop? BackdropTarget;

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

    // ── Protected state: layer / Z-order ───────────────────────
    protected bool IsAtDesktopLayer;
    protected bool KeepRaisedUntilDeactivate;
    protected bool RestoreDesktopLayerWhenIdle;
    protected bool IsHideAnimationRunning;
    protected DateTime LastElevateForInteractionUtc = DateTime.MinValue;
    protected DispatcherQueueTimer? TopMostSafetyTimer;

    // ── Protected state: closing ───────────────────────────────
    protected bool IsClosing;

    /// <summary>
    /// Parameterless constructor required by the WinUI 3 XAML compiler.
    /// Derived classes must set the protected fields (SettingsService, HWnd, etc.)
    /// in their own constructors before calling ConfigureWindowCore().
    /// </summary>
    protected WidgetWindowBase() { }

    // ── Abstract members: each subclass must provide ───────────

    /// <summary>The widget configuration for this window.</summary>
    public abstract WidgetConfig Config { get; }

    /// <summary>The opacity value (0–1) used for backdrop tinting.</summary>
    protected abstract double WidgetOpacity { get; }

    /// <summary>The root XAML element (typically RootGrid).</summary>
    protected abstract FrameworkElement RootElement { get; }

    /// <summary>The shared chrome used to render expanded and compact widget states.</summary>
    protected abstract WidgetShell WidgetShellControl { get; }

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

    // ── Virtual hooks: subclasses can override for specific behavior ──

    /// <summary>Apply XAML-level surface styling (border brush, plate color, etc.).</summary>
    protected virtual void ApplySurfaceStyle() { }

    /// <summary>Extra guards that block RestoreDesktopLayer (e.g. open flyouts).</summary>
    protected virtual bool HasBlockingFlyoutOpen() => false;

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

    /// <summary>Whether to queue backdrop refresh after loading.</summary>
    protected virtual bool SupportsBackdropRefresh => true;

    /// <summary>Whether the native backdrop is temporarily suppressed for tray reveal animation.</summary>
    protected virtual bool IsBackdropSuppressedForTrayReveal => false;

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

    /// <summary>Called during ConfigureWindow to install subclass-specific hooks (e.g. file drop subclass).</summary>
    protected virtual void ConfigureWindowExtra() { }

    /// <summary>Called during ConfigureWindow's RootGrid.Loaded handler.</summary>
    protected virtual void OnRootElementLoaded() { }

    /// <summary>Called during ConfigureWindow's RootGrid.ActualThemeChanged handler.</summary>
    protected virtual void OnRootElementThemeChanged() { }

    // ── Window configuration ───────────────────────────────────

    protected void CleanupBase()
    {
        CleanupWidgetCollapse();
        StopBackdropRefreshTimer();
        if (_inactiveBackdropCleanupTimer is not null)
        {
            _inactiveBackdropCleanupTimer.Stop();
            _inactiveBackdropCleanupTimer.Tick -= InactiveBackdropCleanupTimer_Tick;
            _inactiveBackdropCleanupTimer = null;
        }
        TopMostSafetyTimer?.Stop();
        TopMostSafetyTimer = null;
        DisplayChangeWatcher?.Dispose();
        DisplayChangeWatcher = null;
        DisposeAcrylicController();
        DisposeMicaController();
        WidgetLayerService.ReleaseWindow(HWnd);
        TrackWindowClosedForDiagnostics();
    }

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
