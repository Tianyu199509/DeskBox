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
    private readonly record struct BackdropSignature(
        bool IsDark,
        string MaterialType,
        double SurfaceOpacity,
        Windows.UI.Color TintColor,
        double MaterialIntensity);

    protected void ApplyBackdropPreference()
    {
        if (HWnd == IntPtr.Zero || IsClosing)
        {
            return;
        }

        bool isDark = RootElement.ActualTheme == ElementTheme.Dark;
        double surfaceOpacity = Math.Clamp(WidgetOpacity, 0.0, 1.0);
        string requestedMaterialType = SettingsService.Settings.WidgetMaterialType;
        string materialType = WindowsCompatibilityService.ResolveWidgetMaterialType(
            requestedMaterialType);
        var tintColor = materialType == SettingsService.WidgetMaterialTypeSolid
            ? BuildSolidColorBackdropTintColor(isDark, surfaceOpacity)
            : BuildNativeBackdropTintColor(isDark);
        var signature = new BackdropSignature(
            isDark,
            materialType,
            Math.Round(surfaceOpacity, 3),
            tintColor,
            Math.Round(SettingsService.Settings.WidgetMaterialIntensity, 3));

        if (CanReuseAppliedBackdrop(signature))
        {
            ApplySurfaceStyle();
            return;
        }

        try
        {
            Win32Helper.SetWindowTheme(HWnd, isDark);
            Win32Helper.ApplyFullWindowFrame(HWnd);
            ApplyDwmBorderStyle(isDark);

            int backdropType;
            bool controllerApplied = false;

            if (materialType == SettingsService.WidgetMaterialTypeSolid)
            {
                controllerApplied = ApplySolidColorBackdrop(tintColor);
            }
            else
            {
                ClearSolidColorBackdrop();

                if (SettingsService.IsMicaMaterial(materialType))
                {
                    controllerApplied = ApplyMicaController(
                        isDark,
                        tintColor,
                        materialType == SettingsService.WidgetMaterialTypeMicaAlt);
                }

                if (!controllerApplied && SettingsService.IsAcrylicMaterial(materialType))
                {
                    controllerApplied = ApplyAcrylicController(
                        isDark,
                        tintColor,
                        surfaceOpacity,
                        materialType == SettingsService.WidgetMaterialTypeAcrylicBase);
                }
            }

            if (controllerApplied)
            {
                LegacyAccentBackdropActive = false;
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.TrySetDwmWindowAttribute(HWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType);
                Win32Helper.DisableAccentPolicy(HWnd);
            }
            else
            {
                backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                Win32Helper.TrySetDwmWindowAttribute(HWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType);
                DetachAcrylicControllerTarget();
                DetachMicaControllerTarget();
                double accentOpacity = WindowsCompatibilityService.UsesLegacyWindowAcrylic &&
                    SettingsService.IsAcrylicMaterial(materialType)
                        ? WidgetMaterialVisualCalculator.CalculateLegacyAcrylicOpacity(
                            materialType == SettingsService.WidgetMaterialTypeAcrylicBase,
                            surfaceOpacity,
                            SettingsService.Settings.WidgetMaterialIntensity)
                        : Math.Min(surfaceOpacity, 0.52);
                if (WindowsCompatibilityService.UsesLegacyWindowAcrylic)
                {
                    DisposeAcrylicController();
                    DisposeMicaController();
                    SystemBackdrop = null;
                }
                LegacyAccentBackdropActive = Win32Helper.ApplyAccentBlur(
                    HWnd,
                    tintColor,
                    accentOpacity,
                    true);
                if (!LegacyAccentBackdropActive)
                {
                    App.Log(
                        $"[Backdrop] Legacy accent application failed hwnd=0x{HWnd.ToInt64():X} " +
                        $"material={materialType} opacity={accentOpacity:F3}");
                }
            }

            App.LogVerbose(
                $"[Backdrop] hwnd=0x{HWnd.ToInt64():X} requested={requestedMaterialType} " +
                $"material={materialType} isDark={isDark} " +
                $"opacity={surfaceOpacity:F3} tint=#{tintColor.A:X2}{tintColor.R:X2}{tintColor.G:X2}{tintColor.B:X2} " +
                $"dwmBackdropType={backdropType} " +
                $"acrylicController={AcrylicController is { IsClosed: false } && AcrylicControllerAttached} " +
                $"micaController={MicaController is not null && MicaControllerAttached} " +
                $"solidColorBackdrop={IsSolidColorBackdropActive} " +
                $"legacyAccent={LegacyAccentBackdropActive}");
            if (WindowsCompatibilityService.UsesLegacyWindowAcrylic)
            {
                App.Log(
                    $"[Backdrop.Win10] hwnd=0x{HWnd.ToInt64():X} requested={requestedMaterialType} " +
                    $"resolved={materialType} desktopAcrylicSupported={WindowsCompatibilityService.SupportsDesktopAcrylic} " +
                    $"controllerApplied={controllerApplied} legacyAccent={LegacyAccentBackdropActive} " +
                    $"opacity={surfaceOpacity:F3} intensity={SettingsService.Settings.WidgetMaterialIntensity:F3}");
            }

            _lastAppliedBackdropSignature = signature;
            ScheduleInactiveBackdropControllerCleanup(materialType);
        }
        catch (Exception ex)
        {
            App.Log($"ApplyBackdropPreference fallback: {ex}");
            _lastAppliedBackdropSignature = null;
            ClearSolidColorBackdrop();
            DisposeAcrylicController();
            DisposeMicaController();
            if (materialType == SettingsService.WidgetMaterialTypeSolid)
            {
                int backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.TrySetDwmWindowAttribute(HWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType);
                Win32Helper.DisableAccentPolicy(HWnd);
                LegacyAccentBackdropActive = false;
            }
            else
            {
                double fallbackOpacity = WindowsCompatibilityService.UsesLegacyWindowAcrylic
                    ? WidgetMaterialVisualCalculator.CalculateLegacyAcrylicOpacity(
                        materialType == SettingsService.WidgetMaterialTypeAcrylicBase,
                        surfaceOpacity,
                        SettingsService.Settings.WidgetMaterialIntensity)
                    : Math.Min(surfaceOpacity, 0.52);
                LegacyAccentBackdropActive = Win32Helper.ApplyAccentBlur(
                    HWnd,
                    tintColor,
                    fallbackOpacity,
                    true);
            }
        }

        ApplySurfaceStyle();
    }

    private bool CanReuseAppliedBackdrop(BackdropSignature signature)
    {
        if (_lastAppliedBackdropSignature != signature)
        {
            return false;
        }

        return SettingsService.IsMicaMaterial(signature.MaterialType)
            ? MicaController is not null && MicaControllerAttached
            : SettingsService.IsAcrylicMaterial(signature.MaterialType)
                ? (AcrylicController is { IsClosed: false } && AcrylicControllerAttached) ||
                  LegacyAccentBackdropActive
                : signature.MaterialType == SettingsService.WidgetMaterialTypeSolid &&
                  IsSolidColorBackdropActive &&
                  _solidColorBackdrop is not null &&
                  ReferenceEquals(SystemBackdrop, _solidColorBackdrop);
    }

    protected virtual Windows.UI.Color BuildSolidColorBackdropTintColor(
        bool isDark,
        double surfaceOpacity)
    {
        Windows.UI.Color tintColor = BuildNativeBackdropTintColor(isDark);
        return Windows.UI.Color.FromArgb(
            (byte)Math.Clamp(Math.Round(surfaceOpacity * 255), 0, 255),
            tintColor.R,
            tintColor.G,
            tintColor.B);
    }

    private bool ApplySolidColorBackdrop(Windows.UI.Color tintColor)
    {
        DetachAcrylicControllerTarget();
        DetachMicaControllerTarget();

        if (_solidColorBackdrop is null)
        {
            _solidColorBackdrop = new WinUIEx.TransparentTintBackdrop(tintColor);
            SystemBackdrop = _solidColorBackdrop;
        }
        else
        {
            _solidColorBackdrop.TintColor = tintColor;
            if (!ReferenceEquals(SystemBackdrop, _solidColorBackdrop))
            {
                SystemBackdrop = _solidColorBackdrop;
            }
        }

        IsSolidColorBackdropActive = true;
        return true;
    }

    protected void ClearSolidColorBackdrop()
    {
        IsSolidColorBackdropActive = false;
        if (_solidColorBackdrop is null)
        {
            return;
        }

        if (ReferenceEquals(SystemBackdrop, _solidColorBackdrop))
        {
            SystemBackdrop = null;
        }

        _solidColorBackdrop = null;
    }

    protected static SolidColorBrush GetOrUpdateSolidColorBrush(Brush? current, Windows.UI.Color color)
    {
        if (current is SolidColorBrush brush && MutableBrushes.TryGetValue(brush, out _))
        {
            try
            {
                if (!brush.Color.Equals(color))
                {
                    brush.Color = color;
                }

                return brush;
            }
            catch (Exception)
            {
                MutableBrushes.Remove(brush);
            }
        }

        var replacement = new SolidColorBrush(color);
        MutableBrushes.Add(replacement, MutableBrushMarker);
        return replacement;
    }

    protected (double Thickness, Windows.UI.Color BorderColor, Windows.UI.Color DividerColor)
        GetWidgetBorderVisuals(bool isDark, Windows.UI.Color accentColor)
    {
        WidgetBorderVisuals visuals = WidgetBorderVisualCalculator.Resolve(
            SettingsService.Settings.WidgetBorderStyle,
            SettingsService.Settings.WidgetBorderColorMode,
            isDark,
            accentColor);
        return (
            visuals.Thickness,
            visuals.BorderColor,
            visuals.DividerColor);
    }

    private void ApplyCompactBorderVisuals(bool? isDarkOverride = null)
    {
        bool isDark = isDarkOverride ?? RootElement.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        var (borderThickness, borderColor, _) = GetWidgetBorderVisuals(isDark, accentColor);

        try
        {
            if (WindowsCompatibilityService.IsHighContrast)
            {
                borderThickness = Math.Max(1, borderThickness);
                if (WindowsCompatibilityService.TryGetUiColor(
                        Windows.UI.ViewManagement.UIColorType.Foreground) is
                    { } highContrastForeground)
                {
                    borderColor = highContrastForeground;
                }
            }
        }
        catch
        {
            // Keep the configured border when accessibility APIs are unavailable.
        }

        var surface = WidgetShellControl.BackgroundSurface;
        surface.BorderThickness = new Thickness(borderThickness);
        surface.BorderBrush = GetOrUpdateSolidColorBrush(surface.BorderBrush, borderColor);
    }

    protected void ScheduleInactiveBackdropControllerCleanup(string materialType)
    {
        bool hasInactiveController = materialType switch
        {
            SettingsService.WidgetMaterialTypeMica or SettingsService.WidgetMaterialTypeMicaAlt =>
                AcrylicController is not null,
            SettingsService.WidgetMaterialTypeAcrylic or SettingsService.WidgetMaterialTypeAcrylicBase =>
                MicaController is not null,
            _ => AcrylicController is not null || MicaController is not null
        };

        if (!hasInactiveController)
        {
            _inactiveBackdropCleanupTimer?.Stop();
            return;
        }

        if (_inactiveBackdropCleanupTimer is null)
        {
            _inactiveBackdropCleanupTimer = DispatcherQueue.CreateTimer();
            _inactiveBackdropCleanupTimer.IsRepeating = false;
            _inactiveBackdropCleanupTimer.Tick += InactiveBackdropCleanupTimer_Tick;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }

        _inactiveBackdropCleanupTimer.Stop();
        _inactiveBackdropCleanupTimer.Interval = InactiveBackdropControllerRetention;
        _inactiveBackdropCleanupTimer.Start();
    }

    protected void StopInactiveBackdropCleanupTimer()
    {
        DispatcherQueueTimer? timer = _inactiveBackdropCleanupTimer;
        if (timer is null)
        {
            return;
        }

        _inactiveBackdropCleanupTimer = null;
        timer.Stop();
        timer.Tick -= InactiveBackdropCleanupTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    private void InactiveBackdropCleanupTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        string materialType = SettingsService.Settings.WidgetMaterialType;
        bool releasedController = false;

        if (!SettingsService.IsAcrylicMaterial(materialType) && AcrylicController is not null)
        {
            DisposeAcrylicController();
            releasedController = true;
        }

        if (!SettingsService.IsMicaMaterial(materialType) && MicaController is not null)
        {
            DisposeMicaController();
            releasedController = true;
        }

        if (releasedController)
        {
            App.ScheduleLightMemoryCleanup();
        }
    }

    protected bool ApplyMicaController(
        bool isDark,
        Windows.UI.Color tintColor,
        bool useAlt)
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

        if (MicaController is not null && _micaControllerUsesAlt != useAlt)
        {
            DisposeMicaController();
        }

        if (MicaController is null)
        {
            DetachAcrylicControllerTarget();
            MicaController = new MicaController
            {
                Kind = useAlt ? MicaKind.BaseAlt : MicaKind.Base
            };
            _micaControllerUsesAlt = useAlt;
        }

        DetachAcrylicControllerTarget();
        if (!MicaControllerAttached)
        {
            if (!MicaController.AddSystemBackdropTarget(BackdropTarget))
            {
                DisposeMicaController();
                return false;
            }

            MicaControllerAttached = true;
            MicaController.SetSystemBackdropConfiguration(BackdropConfiguration);
        }

        MicaController.Kind = useAlt ? MicaKind.BaseAlt : MicaKind.Base;
        MicaController.TintColor = tintColor;
        MicaController.FallbackColor = WidgetMaterialVisualCalculator.BuildMicaFallbackColor(
            isDark,
            useAlt);

        WidgetMaterialOpacityProfile profile = WidgetMaterialVisualCalculator.CalculateMica(
            isDark,
            useAlt,
            SettingsService.Settings.WidgetMaterialIntensity);
        MicaController.TintOpacity = (float)profile.TintOpacity;
        MicaController.LuminosityOpacity = (float)profile.LuminosityOpacity;
        return true;
    }

    protected void DisposeMicaController()
    {
        _lastAppliedBackdropSignature = null;
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
            MicaControllerAttached = false;
            _micaControllerUsesAlt = null;
        }
    }

    protected void DetachMicaControllerTarget()
    {
        if (MicaController is null || !MicaControllerAttached)
        {
            return;
        }

        try
        {
            MicaController.RemoveAllSystemBackdropTargets();
        }
        catch
        {
        }
        finally
        {
            MicaControllerAttached = false;
        }
    }

    protected bool ApplyAcrylicController(
        bool isDark,
        Windows.UI.Color tintColor,
        double surfaceOpacity,
        bool useBase)
    {
        if (!WindowsCompatibilityService.SupportsDesktopAcrylic)
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

        if (AcrylicController is not null &&
            !AcrylicController.IsClosed &&
            _acrylicControllerUsesBase != useBase)
        {
            DisposeAcrylicController();
        }

        if (AcrylicController is null || AcrylicController.IsClosed)
        {
            DetachMicaControllerTarget();
            AcrylicController = new DesktopAcrylicController
            {
                Kind = useBase ? DesktopAcrylicKind.Base : DesktopAcrylicKind.Thin
            };
            _acrylicControllerUsesBase = useBase;
        }

        DetachMicaControllerTarget();
        if (!AcrylicControllerAttached)
        {
            if (!AcrylicController.AddSystemBackdropTarget(BackdropTarget))
            {
                DisposeAcrylicController();
                return false;
            }

            AcrylicControllerAttached = true;
            AcrylicController.SetSystemBackdropConfiguration(BackdropConfiguration);
        }

        AcrylicController.Kind = useBase ? DesktopAcrylicKind.Base : DesktopAcrylicKind.Thin;
        AcrylicController.TintColor = tintColor;
        AcrylicController.FallbackColor = tintColor;

        WidgetMaterialOpacityProfile profile = WidgetMaterialVisualCalculator.CalculateAcrylic(
            isDark,
            useBase,
            surfaceOpacity,
            SettingsService.Settings.WidgetMaterialIntensity);
        AcrylicController.TintOpacity = (float)profile.TintOpacity;
        AcrylicController.LuminosityOpacity = (float)profile.LuminosityOpacity;
        return true;
    }

    protected void DisposeAcrylicController()
    {
        _lastAppliedBackdropSignature = null;
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
            AcrylicControllerAttached = false;
            _acrylicControllerUsesBase = null;
        }
    }

    protected void DetachAcrylicControllerTarget()
    {
        if (AcrylicController is null || !AcrylicControllerAttached)
        {
            return;
        }

        try
        {
            AcrylicController.RemoveAllSystemBackdropTargets();
        }
        catch
        {
        }
        finally
        {
            AcrylicControllerAttached = false;
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

        ++BackdropRefreshGeneration;
        _backdropRefreshStage = 0;

        if (_backdropRefreshTimer is null)
        {
            _backdropRefreshTimer = DispatcherQueue.CreateTimer();
            _backdropRefreshTimer.Tick += BackdropRefreshTimer_Tick;
            PerformanceLogger.RecordTransientUiTimerCreated();
        }
        else
        {
            _backdropRefreshTimer.Stop();
        }

        _backdropRefreshTimer.Interval = TimeSpan.FromMilliseconds(BackdropRefreshDelays[0]);
        _backdropRefreshTimer.Start();
    }

    private void BackdropRefreshTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        OnBackdropRefreshTick(BackdropRefreshGeneration);
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
        DispatcherQueueTimer? timer = _backdropRefreshTimer;
        if (timer is null)
        {
            return;
        }

        _backdropRefreshTimer = null;
        timer.Stop();
        timer.Tick -= BackdropRefreshTimer_Tick;
        PerformanceLogger.RecordTransientUiTimerReleased();
    }

    // ── Layer / Z-order management ─────────────────────────────
}
