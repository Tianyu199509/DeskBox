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
using System.Runtime.CompilerServices;
using Windows.Graphics;
using WinRT;
using WinRT.Interop;

namespace DeskBox.Views;

public abstract partial class WidgetWindowBase
{
    protected void ConfigureWindowCore()
    {
        if (!_isTrackedForDiagnostics)
        {
            PerformanceLogger.TrackWindowOpen(LogPrefix);
            _isTrackedForDiagnostics = true;
        }

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
}
