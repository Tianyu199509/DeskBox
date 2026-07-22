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
    protected void ApplyBackdropPreference()
    {
        if (HWnd == IntPtr.Zero || IsClosing)
        {
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

            if (controllerApplied)
            {
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(HWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(HWnd);
            }
            else if (materialType is SettingsService.WidgetMaterialTypeSolid)
            {
                DetachAcrylicControllerTarget();
                DetachMicaControllerTarget();
                backdropType = Win32Helper.DWMSBT_NONE;
                Win32Helper.DwmSetWindowAttribute(HWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                Win32Helper.DisableAccentPolicy(HWnd);
            }
            else
            {
                backdropType = Win32Helper.DWMSBT_TRANSIENTWINDOW;
                Win32Helper.DwmSetWindowAttribute(HWnd, Win32Helper.DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                DetachAcrylicControllerTarget();
                DetachMicaControllerTarget();
                Win32Helper.ApplyAccentBlur(HWnd, tintColor, Math.Min(surfaceOpacity, 0.52), true);
            }

            App.LogVerbose(
                $"[Backdrop] hwnd=0x{HWnd.ToInt64():X} material={materialType} isDark={isDark} " +
                $"opacity={surfaceOpacity:F3} tint=#{tintColor.A:X2}{tintColor.R:X2}{tintColor.G:X2}{tintColor.B:X2} " +
                $"dwmBackdropType={backdropType} " +
                $"acrylicController={AcrylicController is not null} micaController={MicaController is not null}");

            ScheduleInactiveBackdropControllerCleanup(materialType);
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
        string borderStyle = SettingsService.Settings.WidgetBorderStyle;
        string colorMode = SettingsService.Settings.WidgetBorderColorMode;
        var (thickness, alpha) = borderStyle switch
        {
            SettingsService.WidgetBorderStyleMedium => (1.2d, (byte)0x30),
            SettingsService.WidgetBorderStyleThick => (1.6d, (byte)0x48),
            SettingsService.WidgetBorderStyleNone => (0d, (byte)0),
            _ => (0.8d, (byte)0x18)
        };

        if (colorMode == SettingsService.WidgetBorderColorModeNone)
        {
            thickness = 0;
            alpha = 0;
        }

        bool useAccent = colorMode == SettingsService.WidgetBorderColorModeAccent;
        byte borderAlpha = useAccent
            ? (byte)Math.Clamp(Math.Round(alpha * 1.35), 0, 255)
            : alpha;
        byte red = useAccent ? accentColor.R : isDark ? (byte)0xFF : (byte)0x00;
        byte green = useAccent ? accentColor.G : isDark ? (byte)0xFF : (byte)0x00;
        byte blue = useAccent ? accentColor.B : isDark ? (byte)0xFF : (byte)0x00;
        var borderColor = ColorHelper.FromArgb(borderAlpha, red, green, blue);
        var dividerColor = ColorHelper.FromArgb(
            (byte)Math.Clamp(Math.Round(borderAlpha * (isDark ? 0.66 : 0.42)), 0, 255),
            red,
            green,
            blue);
        return (thickness, borderColor, dividerColor);
    }

    private void ApplyCompactBorderVisuals(bool? isDarkOverride = null)
    {
        bool isDark = isDarkOverride ?? RootElement.ActualTheme == ElementTheme.Dark;
        var accentColor = App.Current.ThemeService?.GetEffectiveAccentColor()
            ?? AccentColorHelper.DefaultAccentColor;
        var (borderThickness, borderColor, _) = GetWidgetBorderVisuals(isDark, accentColor);

        try
        {
            if (new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast)
            {
                borderThickness = Math.Max(1, borderThickness);
                borderColor = new Windows.UI.ViewManagement.UISettings().GetColorValue(
                    Windows.UI.ViewManagement.UIColorType.Foreground);
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
        }

        _inactiveBackdropCleanupTimer.Stop();
        _inactiveBackdropCleanupTimer.Interval = InactiveBackdropControllerRetention;
        _inactiveBackdropCleanupTimer.Start();
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

    private static double NormalizeMaterialIntensity(double value) =>
        double.IsFinite(value)
            ? Math.Clamp(
                value,
                SettingsService.MinWidgetMaterialIntensity,
                SettingsService.MaxWidgetMaterialIntensity)
            : SettingsService.DefaultWidgetMaterialIntensity;

    private static double LerpMaterialValue(double start, double end, double progress) =>
        start + ((end - start) * Math.Clamp(progress, 0.0, 1.0));

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
        MicaController.FallbackColor = useAlt
            ? isDark
                ? ColorHelper.FromArgb(0xFF, 0x16, 0x18, 0x1D)
                : ColorHelper.FromArgb(0xFF, 0xE8, 0xEA, 0xEF)
            : isDark
                ? ColorHelper.FromArgb(0xFF, 0x20, 0x22, 0x26)
                : ColorHelper.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        double intensity = NormalizeMaterialIntensity(
            SettingsService.Settings.WidgetMaterialIntensity);
        double tintOpacity = useAlt
            ? LerpMaterialValue(0.28, 0.82, intensity)
            : LerpMaterialValue(0.04, 0.46, intensity);
        double luminosityOpacity = useAlt
            ? LerpMaterialValue(isDark ? 0.34 : 0.42, isDark ? 0.72 : 0.76, intensity)
            : LerpMaterialValue(isDark ? 0.78 : 0.82, isDark ? 0.94 : 0.96, intensity);

        MicaController.TintOpacity = (float)tintOpacity;
        MicaController.LuminosityOpacity = (float)luminosityOpacity;
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

        double intensity = NormalizeMaterialIntensity(
            SettingsService.Settings.WidgetMaterialIntensity);
        double surfaceStrength = LerpMaterialValue(0.08, 1.0, Math.Clamp(surfaceOpacity, 0.0, 1.0));
        double tintOpacity = useBase
            ? LerpMaterialValue(isDark ? 0.18 : 0.12, isDark ? 0.72 : 0.62, intensity)
            : LerpMaterialValue(isDark ? 0.04 : 0.02, isDark ? 0.42 : 0.34, intensity);
        double luminosityOpacity = useBase
            ? LerpMaterialValue(isDark ? 0.38 : 0.46, isDark ? 0.82 : 0.90, intensity)
            : LerpMaterialValue(isDark ? 0.16 : 0.22, isDark ? 0.56 : 0.64, intensity);

        AcrylicController.TintOpacity = (float)Math.Clamp(tintOpacity * surfaceStrength, 0.0, 1.0);
        AcrylicController.LuminosityOpacity = (float)Math.Clamp(
            luminosityOpacity * surfaceStrength,
            0.0,
            1.0);
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

        AcrylicController.Kind = DesktopAcrylicKind.Thin;
        AcrylicController.TintColor = isDark
            ? ColorHelper.FromArgb(0x01, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
        AcrylicController.FallbackColor = isDark
            ? ColorHelper.FromArgb(0x01, 0x20, 0x22, 0x26)
            : ColorHelper.FromArgb(0x01, 0xFF, 0xFF, 0xFF);
        AcrylicController.TintOpacity = 0.0f;
        AcrylicController.LuminosityOpacity = 0.0f;

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
            _backdropRefreshTimer.Tick += (_, _) => OnBackdropRefreshTick(BackdropRefreshGeneration);
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
}
