// Copyright (c) DeskBox. All rights reserved.

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
public abstract class WidgetWindowBase : Window
{
    private const int MinWidth = (int)SettingsService.MinWidgetWidth;
    private const int MinHeight = (int)SettingsService.MinWidgetHeight;

    private static readonly int[] BackdropRefreshDelays = [80, 240, 580];

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
    protected SystemBackdropConfiguration? BackdropConfiguration;
    protected ICompositionSupportsSystemBackdrop? BackdropTarget;

    // ── Protected state: backdrop refresh ──────────────────────
    protected long BackdropRefreshGeneration;
    private DispatcherQueueTimer? _backdropRefreshTimer;
    private int _backdropRefreshStage;

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

    /// <summary>Called during ConfigureWindow to install subclass-specific hooks (e.g. file drop subclass).</summary>
    protected virtual void ConfigureWindowExtra() { }

    /// <summary>Called during ConfigureWindow's RootGrid.Loaded handler.</summary>
    protected virtual void OnRootElementLoaded() { }

    /// <summary>Called during ConfigureWindow's RootGrid.ActualThemeChanged handler.</summary>
    protected virtual void OnRootElementThemeChanged() { }

    // ── Window configuration ───────────────────────────────────

    protected void ConfigureWindowCore()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        int exStyle = Win32Helper.GetWindowLong(HWnd, Win32Helper.GWL_EXSTYLE);
        exStyle |= Win32Helper.WS_EX_TOOLWINDOW;
        Win32Helper.SetWindowLong(HWnd, Win32Helper.GWL_EXSTYLE, exStyle);

        ConfigureWindowExtra();

        int style = Win32Helper.GetWindowLong(HWnd, Win32Helper.GWL_STYLE);
        style &= ~(Win32Helper.WS_CAPTION | Win32Helper.WS_BORDER | Win32Helper.WS_DLGFRAME | Win32Helper.WS_THICKFRAME);
        Win32Helper.SetWindowLong(HWnd, Win32Helper.GWL_STYLE, style);
        Win32Helper.SetWindowPos(
            HWnd,
            IntPtr.Zero,
            0, 0, 0, 0,
            Win32Helper.SWP_NOMOVE | Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOACTIVATE | Win32Helper.SWP_FRAMECHANGED);

        AppWindow.IsShownInSwitchers = false;
        ExtendsContentIntoTitleBar = false;

        var config = Config;
        var workArea = DisplayArea.GetFromRect(
            new RectInt32(
                (int)Math.Round(config.X),
                (int)Math.Round(config.Y),
                (int)Math.Round(config.Width),
                (int)Math.Round(config.Height)),
            DisplayAreaFallback.Nearest).WorkArea;
        var bounds = WidgetPositioningService.ResolveBounds(
            config,
            workArea,
            WidgetPositioningService.GetAvailableWorkAreas());
        ApplyWindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, persist: false);

        ApplyDwmBorderStyle(RootElement.ActualTheme == ElementTheme.Dark);
        ApplyWindowCornerPreference();
        Win32Helper.EnsureSystemDispatcherQueue();
        Win32Helper.ApplyFullWindowFrame(HWnd);
        ApplyBackdropPreference();

        RootElement.Loaded += (_, _) =>
        {
            OnRootElementLoaded();
            ApplyBackdropPreference();
            Win32Helper.ApplyFullWindowFrame(HWnd);
            if (SupportsBackdropRefresh)
            {
                QueueBackdropRefresh();
            }
        };
        RootElement.ActualThemeChanged += (_, _) =>
        {
            ApplyBackdropPreference();
            OnRootElementThemeChanged();
        };
    }

    // ── Bounds management ──────────────────────────────────────

    protected void ApplyWindowBounds(int x, int y, int width, int height, bool persist, bool updateConfig = true)
    {
        var minSize = GetPhysicalMinimumWindowSize(x, y, width, height);
        width = Math.Max(minSize.Width, width);
        height = Math.Max(minSize.Height, height);

        IsApplyingBounds = true;
        try
        {
            AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        }
        finally
        {
            IsApplyingBounds = false;
        }

        if (persist)
        {
            CapturePositionAnchor(x, y, width, height);
            UpdateConfigBoundsFromPhysical(x, y, width, height, persist: true);
            return;
        }

        if (updateConfig)
        {
            UpdateConfigBoundsFromPhysical(x, y, width, height, persist: false);
        }
    }

    protected SizeInt32 GetPhysicalMinimumWindowSize(int x, int y, int width, int height)
    {
        return WidgetPositioningService.GetPhysicalMinimumSizeForBounds(
            new RectInt32(x, y, Math.Max(1, width), Math.Max(1, height)));
    }

    protected void CapturePositionAnchor(int x, int y, int width, int height)
    {
        var bounds = new RectInt32(x, y, width, height);
        // Use the window center point to determine the owning display.
        // This prevents incorrect anchor capture when the window straddles
        // two monitors during a cross-screen drag.
        var center = new PointInt32(
            x + Math.Max(1, width) / 2,
            y + Math.Max(1, height) / 2);
        var workArea = DisplayArea.GetFromPoint(center, DisplayAreaFallback.Nearest).WorkArea;
        Config.BoundsCoordinateVersion = WidgetConfig.CurrentBoundsCoordinateVersion;
        WidgetPositioningService.CaptureAnchor(Config, bounds, workArea);
    }

    // ── Display change restoration ─────────────────────────────

    protected bool TryRestoreBoundsForCurrentTopology(bool allowHidden)
    {
        if (IsClosing || IsHideAnimationRunning)
        {
            return true;
        }

        if (!allowHidden && !Visible)
        {
            return true;
        }

        if (IsDragging || IsResizing || TrayAnimation.IsApplyingBounds)
        {
            return false;
        }

        var bounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(Config);
        var position = AppWindow.Position;
        var size = AppWindow.Size;
        if (position.X == bounds.X &&
            position.Y == bounds.Y &&
            size.Width == bounds.Width &&
            size.Height == bounds.Height)
        {
            return true;
        }

        ApplyWindowBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height, persist: false, updateConfig: true);
        return true;
    }

    protected bool RestoreBoundsAfterDisplayChange()
    {
        if (!Visible)
        {
            var bounds = WidgetPositioningService.ResolveBoundsForCurrentTopology(Config);
            var workArea = DisplayArea.GetFromRect(bounds, DisplayAreaFallback.Nearest).WorkArea;
            WidgetPositioningService.CaptureAnchor(Config, bounds, workArea);
            WidgetPositioningService.UpdateConfigFromPhysicalBounds(Config, bounds, workArea);
            SettingsService.SaveDebounced();
            return true;
        }

        bool restored = TryRestoreBoundsForCurrentTopology(allowHidden: false);
        if (restored)
        {
            RestoreDesktopLayer(force: true);
        }

        return restored;
    }

    public void RestoreBoundsForCurrentTopology()
    {
        _ = TryRestoreBoundsForCurrentTopology(allowHidden: true);
    }

    // ── AppWindow change handling ──────────────────────────────

    protected void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (IsApplyingBounds || TrayAnimation.IsApplyingBounds || (!IsDragging && !IsResizing))
        {
            return;
        }

        if (args.DidPositionChange || args.DidSizeChange)
        {
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            UpdateConfigBoundsFromPhysical(pos.X, pos.Y, size.Width, size.Height, persist: false);
        }
    }

    // ── Window corner & DWM border ─────────────────────────────

    protected void ApplyWindowCornerPreference()
    {
        int cornerPreference = SettingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceDefault => Win32Helper.DWMWCP_DEFAULT,
            SettingsService.WidgetCornerPreferenceSquare => Win32Helper.DWMWCP_DONOTROUND,
            SettingsService.WidgetCornerPreferenceSmall => Win32Helper.DWMWCP_ROUNDSMALL,
            _ => Win32Helper.DWMWCP_ROUND
        };

        Win32Helper.DwmSetWindowAttribute(HWnd, Win32Helper.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));
    }

    protected void ApplyDwmBorderStyle(bool isDark)
    {
        // Always set DWM border to transparent — the visual border is drawn by XAML
        // BackgroundPlate.BorderBrush which correctly follows the XAML CornerRadius.
        int borderNone = unchecked((int)0xFFFFFFFE);
        Win32Helper.SetWindowBorderColor(HWnd, borderNone);
    }

    protected double GetCornerRadiusFromPreference()
    {
        return SettingsService.Settings.WidgetCornerPreference switch
        {
            SettingsService.WidgetCornerPreferenceSquare => 0,
            SettingsService.WidgetCornerPreferenceSmall => 4,
            SettingsService.WidgetCornerPreferenceRound => 8,
            _ => 8
        };
    }

    // ── Backdrop preference ────────────────────────────────────

    protected void ApplyBackdropPreference()
    {
        if (HWnd == IntPtr.Zero || IsClosing)
        {
            return;
        }

        if (IsBackdropSuppressedForTrayReveal)
        {
            ApplySurfaceStyle();
            return;
        }

        bool isDark = RootElement.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(WidgetOpacity, 0.0, 1.0);
        var tintColor = BuildNativeBackdropTintColor(isDark);
        string materialType = SettingsService.Settings.WidgetMaterialType;

        try
        {
            Win32Helper.SetWindowTheme(HWnd, isDark);
            Win32Helper.ApplyFullWindowFrame(HWnd);
            ApplyDwmBorderStyle(isDark);

            int backdropType;
            bool controllerApplied = false;

            if (materialType is SettingsService.WidgetMaterialTypeMica)
            {
                controllerApplied = ApplyMicaController(isDark, tintColor, surfaceOpacity);
            }

            if (!controllerApplied && materialType is SettingsService.WidgetMaterialTypeAcrylic)
            {
                controllerApplied = ApplyAcrylicController(isDark, tintColor, surfaceOpacity);
            }

            if (controllerApplied)
            {
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(HWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(HWnd);
            }
            else if (materialType is SettingsService.WidgetMaterialTypeSolid)
            {
                DisposeAcrylicController();
                DisposeMicaController();
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(HWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(HWnd);
            }
            else
            {
                backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                Win32Helper.DwmSetWindowAttribute(HWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                DisposeAcrylicController();
                DisposeMicaController();
                Win32Helper.ApplyAccentBlur(HWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
            }

            App.LogVerbose(
                $"[Backdrop] hwnd=0x{HWnd.ToInt64():X} material={materialType} isDark={isDark} " +
                $"opacity={surfaceOpacity:F3} tint=#{tintColor.A:X2}{tintColor.R:X2}{tintColor.G:X2}{tintColor.B:X2} " +
                $"dwmBackdropType={backdropType} " +
                $"acrylicController={AcrylicController is not null} micaController={MicaController is not null}");
        }
        catch (Exception ex)
        {
            App.Log($"ApplyBackdropPreference fallback: {ex}");
            DisposeAcrylicController();
            DisposeMicaController();
            Win32Helper.ApplyAccentBlur(HWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
        }

        ApplySurfaceStyle();
    }

    protected bool ApplyMicaController(
        bool isDark,
        Windows.UI.Color tintColor,
        double surfaceOpacity)
    {
        if (!MicaController.IsSupported())
        {
            DisposeMicaController();
            return false;
        }

        BackdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        BackdropConfiguration ??= new SystemBackdropConfiguration();
        BackdropConfiguration.IsInputActive = true;
        BackdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        if (MicaController is null)
        {
            DisposeAcrylicController();
            MicaController = new MicaController { Kind = MicaKind.Base };

            if (!MicaController.AddSystemBackdropTarget(BackdropTarget))
            {
                DisposeMicaController();
                return false;
            }
        }
        else
        {
            DisposeAcrylicController();
        }

        MicaController.SetSystemBackdropConfiguration(BackdropConfiguration);
        MicaController.Kind = MicaKind.Base;
        MicaController.TintColor = tintColor;
        MicaController.FallbackColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        MicaController.TintOpacity = (float)Math.Clamp(surfaceOpacity * 0.5, 0.0, 1.0);
        return true;
    }

    protected void DisposeMicaController()
    {
        if (MicaController is null)
        {
            return;
        }

        try
        {
            MicaController.RemoveAllSystemBackdropTargets();
            MicaController.Dispose();
        }
        catch
        {
        }
        finally
        {
            MicaController = null;
        }
    }

    protected bool ApplyAcrylicController(
        bool isDark,
        Windows.UI.Color tintColor,
        double surfaceOpacity)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            DisposeAcrylicController();
            return false;
        }

        BackdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        BackdropConfiguration ??= new SystemBackdropConfiguration();
        BackdropConfiguration.IsInputActive = true;
        BackdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        BackdropConfiguration.HighContrastBackgroundColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : ColorHelper.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);

        if (AcrylicController is null || AcrylicController.IsClosed)
        {
            DisposeMicaController();
            AcrylicController = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Thin
            };

            if (!AcrylicController.AddSystemBackdropTarget(BackdropTarget))
            {
                DisposeAcrylicController();
                return false;
            }
        }
        else
        {
            DisposeMicaController();
        }

        AcrylicController.SetSystemBackdropConfiguration(BackdropConfiguration);
        AcrylicController.Kind = DesktopAcrylicKind.Thin;
        AcrylicController.TintColor = tintColor;
        AcrylicController.FallbackColor = tintColor;
        double tintOpacity = isDark
            ? Math.Clamp(0.12 + surfaceOpacity * 0.34, 0.0, 0.52)
            : Math.Clamp(0.00 + surfaceOpacity * 0.40, 0.0, 0.44);
        double luminosityOpacity = isDark
            ? Math.Clamp(0.34 + surfaceOpacity * 0.36, 0.0, 0.82)
            : Math.Clamp(0.22 + surfaceOpacity * 0.58, 0.0, 0.86);
        AcrylicController.TintOpacity = (float)tintOpacity;
        AcrylicController.LuminosityOpacity = (float)luminosityOpacity;
        return true;
    }

    protected bool ApplyTransparentAcrylicController(bool isDark)
    {
        if (!DesktopAcrylicController.IsSupported())
        {
            DisposeAcrylicController();
            return false;
        }

        BackdropTarget ??= this.As<ICompositionSupportsSystemBackdrop>();
        BackdropConfiguration ??= new SystemBackdropConfiguration();
        BackdropConfiguration.IsInputActive = true;
        BackdropConfiguration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        if (AcrylicController is null || AcrylicController.IsClosed)
        {
            AcrylicController = new DesktopAcrylicController
            {
                Kind = DesktopAcrylicKind.Thin
            };

            if (!AcrylicController.AddSystemBackdropTarget(BackdropTarget))
            {
                DisposeAcrylicController();
                return false;
            }
        }

        AcrylicController.SetSystemBackdropConfiguration(BackdropConfiguration);
        AcrylicController.Kind = DesktopAcrylicKind.Thin;
        AcrylicController.TintColor = isDark
            ? ColorHelper.FromArgb(0x01, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
        AcrylicController.FallbackColor = isDark
            ? ColorHelper.FromArgb(0x01, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
        AcrylicController.TintOpacity = 0.0f;
        AcrylicController.LuminosityOpacity = 0.0f;

        DisposeMicaController();
        return true;
    }

    protected void DisposeAcrylicController()
    {
        if (AcrylicController is null)
        {
            return;
        }

        try
        {
            AcrylicController.RemoveAllSystemBackdropTargets();
            AcrylicController.Dispose();
        }
        catch
        {
        }
        finally
        {
            AcrylicController = null;
        }
    }

    // ── Backdrop refresh timer ─────────────────────────────────

    protected void QueueBackdropRefresh()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(QueueBackdropRefresh);
            return;
        }

        long generation = ++BackdropRefreshGeneration;
        _backdropRefreshStage = 0;

        if (_backdropRefreshTimer is null)
        {
            _backdropRefreshTimer = DispatcherQueue.CreateTimer();
            _backdropRefreshTimer.Tick += (_, _) => OnBackdropRefreshTick(generation);
        }
        else
        {
            _backdropRefreshTimer.Stop();
        }

        _backdropRefreshTimer.Interval = TimeSpan.FromMilliseconds(BackdropRefreshDelays[0]);
        _backdropRefreshTimer.Start();
    }

    private void OnBackdropRefreshTick(long generation)
    {
        if (generation != BackdropRefreshGeneration)
        {
            _backdropRefreshTimer?.Stop();
            return;
        }

        RefreshBackdropIfCurrent(generation);

        int nextStage = _backdropRefreshStage + 1;
        _backdropRefreshStage = nextStage;

        if (nextStage < BackdropRefreshDelays.Length)
        {
            _backdropRefreshTimer!.Interval = TimeSpan.FromMilliseconds(BackdropRefreshDelays[nextStage]);
        }
        else
        {
            _backdropRefreshTimer!.Stop();
        }
    }

    private void RefreshBackdropIfCurrent(long generation)
    {
        if (generation != BackdropRefreshGeneration)
        {
            return;
        }

        if (!Visible || IsHideAnimationRunning)
        {
            return;
        }

        // Skip backdrop refresh during drag/resize — the window is moving
        // and the backdrop will be refreshed once when the operation ends.
        if (IsDragging || IsResizing)
        {
            return;
        }

        ApplyBackdropPreference();
    }

    protected void StopBackdropRefreshTimer()
    {
        _backdropRefreshTimer?.Stop();
        _backdropRefreshTimer = null;
    }

    // ── Layer / Z-order management ─────────────────────────────

    protected void ElevateForInteraction()
    {
        if (App.Current.WidgetManager is { WidgetsRaisedFromTray: true })
        {
            return;
        }

        LastElevateForInteractionUtc = DateTime.UtcNow;
        HoldTemporaryTopMost();
        App.Current.WidgetManager?.BringAllVisibleWidgetsToFront(HWnd);
        OnElevated();
    }

    protected void HoldTemporaryTopMost()
    {
        if (WidgetLayerService.UsesDesktopPinnedMode())
        {
            IsAtDesktopLayer = true;
            KeepRaisedUntilDeactivate = false;
            RestoreDesktopLayerWhenIdle = false;
            WidgetLayerService.MoveToDesktopBottom(HWnd);
            App.LogVerbose($"[ZOrder] {LogPrefix} HoldTemporaryTopMost skipped pinned hwnd=0x{HWnd.ToInt64():X}");
            return;
        }

        IsAtDesktopLayer = false;
        KeepRaisedUntilDeactivate = true;
        RestoreDesktopLayerWhenIdle = false;
        WidgetLayerService.HoldTemporaryTopMost(HWnd);
        App.LogVerbose($"[ZOrder] {LogPrefix} HoldTemporaryTopMost hwnd=0x{HWnd.ToInt64():X}");
        StartTopMostSafetyTimer();
    }

    protected void StartTopMostSafetyTimer()
    {
        if (TopMostSafetyTimer is null)
        {
            TopMostSafetyTimer = DispatcherQueue.CreateTimer();
            TopMostSafetyTimer.IsRepeating = false;
            TopMostSafetyTimer.Interval = TimeSpan.FromSeconds(2);
            TopMostSafetyTimer.Tick += (_, _) =>
            {
                TopMostSafetyTimer?.Stop();
                if (!IsAtDesktopLayer && !IsDragging && !IsResizing &&
                    App.Current.WidgetManager is not { WidgetsRaisedFromTray: true })
                {
                    App.Log($"[ZOrder] {LogPrefix} safety timer: force restore hwnd=0x{HWnd.ToInt64():X}");
                    RestoreDesktopLayer(force: true);
                }
            };
        }
        else
        {
            TopMostSafetyTimer.Stop();
        }
        TopMostSafetyTimer.Start();
    }

    protected void RestoreDesktopLayer(bool force = false)
    {
        if (!force && !RestoreDesktopLayerWhenIdle && KeepRaisedUntilDeactivate)
        {
            return;
        }

        if (!force && (IsDragging || IsResizing || HasBlockingFlyoutOpen()))
        {
            if (force || RestoreDesktopLayerWhenIdle)
            {
                RestoreDesktopLayerWhenIdle = true;
            }

            return;
        }

        TopMostSafetyTimer?.Stop();
        TopMostSafetyTimer = null;
        KeepRaisedUntilDeactivate = false;
        RestoreDesktopLayerWhenIdle = false;
        ClearTopMostOnly();
        ApplyBackdropPreference();
    }

    protected void ClearTopMostOnly()
    {
        IsAtDesktopLayer = true;
        IntPtr foreground = WidgetLayerService.ClearTopMostPreservingForeground(HWnd);
        App.LogVerbose($"[ZOrder] {LogPrefix} ClearTopMostOnly hwnd=0x{HWnd.ToInt64():X} fg=0x{foreground.ToInt64():X}");
    }

    // ── Drag logic ─────────────────────────────────────────────

    protected void BeginWindowDragCore(PointerRoutedEventArgs e, FrameworkElement captureElement)
    {
        IsDragging = true;
        HasMovedTitleBarDrag = false;
        DisplayChangeWatcher?.SuppressRestore();
        ElevateForInteraction();
        Win32Helper.GetCursorPos(out InitialCursorPt);
        InitialWindowPos = AppWindow.Position;
        InitialWindowSize = AppWindow.Size;
        DragCaptureElement = captureElement;
        captureElement.CapturePointer(e.Pointer);
        e.Handled = true;

        App.Current?.ResizeGuideOverlay.BeginDrag(HWnd, RootElement);
    }

    protected void ContinueWindowDragCore(PointerRoutedEventArgs e)
    {
        if (!IsDragging)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - InitialCursorPt.X;
        int deltaY = currentPt.Y - InitialCursorPt.Y;
        int dragDistanceSquared = (deltaX * deltaX) + (deltaY * deltaY);

        if (!HasMovedTitleBarDrag)
        {
            if (dragDistanceSquared < 16)
            {
                e.Handled = true;
                return;
            }

            HasMovedTitleBarDrag = true;
        }

        int newX = InitialWindowPos.X + deltaX;
        int newY = InitialWindowPos.Y + deltaY;

        var proposedBounds = new RectInt32(newX, newY, InitialWindowSize.Width, InitialWindowSize.Height);
        var snappedBounds = App.Current?.ResizeGuideOverlay.UpdateGuidesAndSnapForDrag(proposedBounds)
            ?? proposedBounds;

        ApplyWindowBounds(snappedBounds.X, snappedBounds.Y, snappedBounds.Width, snappedBounds.Height, persist: false);
        e.Handled = true;
    }

    protected void EndWindowDragCore(PointerRoutedEventArgs e)
    {
        if (!IsDragging)
        {
            return;
        }

        IsDragging = false;
        bool hasMoved = HasMovedTitleBarDrag;
        DragCaptureElement?.ReleasePointerCapture(e.Pointer);
        DragCaptureElement = null;

        App.Current?.ResizeGuideOverlay.EndDrag();

        var finalPosition = AppWindow.Position;
        var finalSize = AppWindow.Size;
        CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
        UpdateConfigBoundsFromPhysical(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        OnDragEnd(hasMoved);

        DisplayChangeWatcher?.ResumeRestore();
        HasMovedTitleBarDrag = false;
        QueueBackdropRefresh();
        e.Handled = true;
    }

    // ── Resize logic ───────────────────────────────────────────

    protected void ResizeBorder_PointerPressedCore(object sender, PointerRoutedEventArgs e)
    {
        if (IsSizeLocked || sender is not FrameworkElement element)
        {
            return;
        }

        var properties = e.GetCurrentPoint(element).Properties;
        if (!properties.IsLeftButtonPressed)
        {
            return;
        }

        IsResizing = true;
        ResizeDirection = element.Tag as string ?? string.Empty;
        DisplayChangeWatcher?.SuppressRestore();
        OnResizeStart();
        Win32Helper.GetCursorPos(out InitialCursorPt);
        InitialWindowPos = AppWindow.Position;
        InitialWindowSize = AppWindow.Size;
        element.CapturePointer(e.Pointer);
        App.Current.ResizeGuideOverlay.BeginResize(HWnd, RootElement);
        e.Handled = true;
    }

    protected void ResizeBorder_PointerMovedCore(object sender, PointerRoutedEventArgs e)
    {
        if (!IsResizing)
        {
            return;
        }

        Win32Helper.GetCursorPos(out var currentPt);
        int deltaX = currentPt.X - InitialCursorPt.X;
        int deltaY = currentPt.Y - InitialCursorPt.Y;

        int newWidth = InitialWindowSize.Width;
        int newHeight = InitialWindowSize.Height;
        int newX = InitialWindowPos.X;
        int newY = InitialWindowPos.Y;
        var minSize = GetPhysicalMinimumWindowSize(
            InitialWindowPos.X,
            InitialWindowPos.Y,
            InitialWindowSize.Width,
            InitialWindowSize.Height);

        if (ResizeDirection.Contains("Right"))
        {
            newWidth = Math.Max(minSize.Width, InitialWindowSize.Width + deltaX);
        }
        else if (ResizeDirection.Contains("Left"))
        {
            int rightEdge = InitialWindowPos.X + InitialWindowSize.Width;
            newWidth = Math.Max(minSize.Width, InitialWindowSize.Width - deltaX);
            newX = rightEdge - newWidth;
        }

        if (ResizeDirection.Contains("Bottom"))
        {
            newHeight = Math.Max(minSize.Height, InitialWindowSize.Height + deltaY);
        }
        else if (ResizeDirection.Contains("Top"))
        {
            int bottomEdge = InitialWindowPos.Y + InitialWindowSize.Height;
            newHeight = Math.Max(minSize.Height, InitialWindowSize.Height - deltaY);
            newY = bottomEdge - newHeight;
        }

        var proposed = new RectInt32(newX, newY, newWidth, newHeight);
        var snapped = App.Current.ResizeGuideOverlay.UpdateGuidesAndSnap(proposed, ResizeDirection);
        ApplyWindowBounds(snapped.X, snapped.Y, snapped.Width, snapped.Height, persist: false);
        e.Handled = true;
    }

    protected void ResizeBorder_PointerReleasedCore(object sender, PointerRoutedEventArgs e)
    {
        if (!IsResizing || sender is not FrameworkElement element)
        {
            return;
        }

        IsResizing = false;
        ResizeDirection = string.Empty;
        element.ReleasePointerCapture(e.Pointer);
        App.Current.ResizeGuideOverlay.EndResize();
        var finalPosition = AppWindow.Position;
        var finalSize = AppWindow.Size;
        CapturePositionAnchor(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height);
        UpdateConfigBoundsFromPhysical(finalPosition.X, finalPosition.Y, finalSize.Width, finalSize.Height, persist: true);
        OnResizeEnd();
        DisplayChangeWatcher?.ResumeRestore();
        QueueBackdropRefresh();
        e.Handled = true;
    }

    // ── Interaction layer helpers ──────────────────────────────

    protected void BeginInteractionLayer(string reason, bool elevate = true)
    {
        App.Current.WidgetManager?.BeginWidgetInteraction(reason);
        if (elevate)
        {
            ElevateForInteraction();
        }
    }

    protected void ReleaseInteractionLayer(string reason)
    {
        App.Current.WidgetManager?.EndWidgetInteraction(reason);
        if (App.Current.WidgetManager?.RequestRestoreRaisedWidgetsToDesktopLayer(reason) == true)
        {
            return;
        }

        RestoreDesktopLayer();
    }

    // ── Tray animation helpers ─────────────────────────────────

    protected WidgetTrayAnimationProfile GetTrayAnimationProfile()
    {
        return TrayAnimation.CreateProfile(WidgetAnimationSettings.From(SettingsService.Settings));
    }

    protected void LogTrayWindow(string message)
    {
        App.LogVerbose(Diagnostics.FormatTrayWindowMessage(message));
    }

    // ── Cleanup ────────────────────────────────────────────────

    protected void CleanupBase()
    {
        StopBackdropRefreshTimer();
        TopMostSafetyTimer?.Stop();
        TopMostSafetyTimer = null;
        DisplayChangeWatcher?.Dispose();
        DisplayChangeWatcher = null;
        DisposeAcrylicController();
        DisposeMicaController();
        WidgetLayerService.ReleaseWindow(HWnd);
    }
}
