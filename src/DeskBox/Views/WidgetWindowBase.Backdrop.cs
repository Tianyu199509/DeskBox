// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
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

    private readonly record struct SharedMicaKey(
        bool IsDark,
        bool UseAlt,
        Windows.UI.Color TintColor,
        int RoundedTintOpacity,
        int RoundedLuminosityOpacity);

    private readonly record struct SharedAcrylicKey(
        bool IsDark,
        bool UseBase,
        Windows.UI.Color TintColor,
        int RoundedTintOpacity,
        int RoundedLuminosityOpacity);

    private sealed class SharedMicaSlot(MicaController controller, SystemBackdropConfiguration configuration)
    {
        public MicaController Controller { get; } = controller;
        public SystemBackdropConfiguration Configuration { get; } = configuration;
        public SharedMicaKey Key { get; set; }
        public HashSet<ICompositionSupportsSystemBackdrop> Targets { get; } = [];
    }

    private sealed class SharedAcrylicSlot(DesktopAcrylicController controller, SystemBackdropConfiguration configuration)
    {
        public DesktopAcrylicController Controller { get; } = controller;
        public SystemBackdropConfiguration Configuration { get; } = configuration;
        public SharedAcrylicKey Key { get; set; }
        public HashSet<ICompositionSupportsSystemBackdrop> Targets { get; } = [];
    }

    private const int MaxSharedBackdropSlotsPerFamily = 2;

    [ThreadStatic]
    private static List<SharedMicaSlot>? s_sharedMicaSlots;

    [ThreadStatic]
    private static List<SharedAcrylicSlot>? s_sharedAcrylicSlots;

    private SharedMicaSlot? _attachedMicaSlot;
    private SharedAcrylicSlot? _attachedAcrylicSlot;

    protected void ApplyBackdropPreference()
    {
        if (HWnd == IntPtr.Zero || IsClosing)
        {
            return;
        }

        if (!IsWindowVisibleForBackdrop)
        {
            SuspendBackdropTargetsForHiddenWindow();
            ApplySurfaceStyle();
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
            // The immersive-dark-mode attribute, sheet-of-glass frame and DWM
            // border depend only on the theme, not the material. Re-issuing
            // them on every material change churns DWM state natively, so they
            // only run when the theme flipped or when no signature was applied
            // yet (first apply / post-dispose / after a failed apply).
            bool dwmThemeChanged = _lastAppliedBackdropSignature is not { } lastSignature ||
                lastSignature.IsDark != isDark;
            if (dwmThemeChanged)
            {
                Win32Helper.SetWindowTheme(HWnd, isDark);
                Win32Helper.ApplyFullWindowFrame(HWnd);
                ApplyDwmBorderStyle(isDark);
            }

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
                LegacyAccentBackdropActive = ApplyLegacyAccentBackdrop(
                    tintColor,
                    accentOpacity);
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
                LegacyAccentBackdropActive = ApplyLegacyAccentBackdrop(
                    tintColor,
                    fallbackOpacity);
            }
        }

        ApplySurfaceStyle();
    }

    private bool ApplyLegacyAccentBackdrop(Windows.UI.Color tintColor, double opacity)
    {
        _lastLegacyAccentTintColor = tintColor;
        _lastLegacyAccentOpacity = opacity;
        // A full preference application always restores the blurred accent
        // policy, so any interaction-time simplification ends here.
        _isInteractionBackdropDowngraded = false;
        return Win32Helper.ApplyAccentBlur(HWnd, tintColor, opacity, enabled: true);
    }

    /// <summary>
    /// Win10-only: temporarily switches the legacy acrylic accent policy to a
    /// tint without DWM blur while the window is being moved/resized/animated,
    /// so DWM stops re-sampling the blur on every frame. Keeps the frosted
    /// glass by default — the simplification only runs after recent frame
    /// ticks kept missing their budget (or in ResourceSaver mode).
    /// </summary>
    public void SimplifyBackdropForInteraction()
    {
        if (_isInteractionBackdropDowngraded ||
            !LegacyAccentBackdropActive ||
            !WindowsCompatibilityService.UsesLegacyWindowAcrylic ||
            HWnd == IntPtr.Zero)
        {
            return;
        }

        if (!InteractionBackdropSimplificationPolicy.ShouldSimplify(
                WidgetCompactAnimationCoordinator.RecentFrameOverrunMask,
                SettingsService.Settings.PerformanceMode))
        {
            return;
        }

        bool applied = Win32Helper.ApplyAccentBlur(
            HWnd,
            _lastLegacyAccentTintColor,
            _lastLegacyAccentOpacity,
            enabled: true,
            blurEnabled: false);
        if (!applied)
        {
            return;
        }

        _isInteractionBackdropDowngraded = true;
        App.LogVerbose(
            $"[Backdrop.Win10] hwnd=0x{HWnd.ToInt64():X} interaction tint-only accent applied " +
            $"(recent overrun bits={System.Numerics.BitOperations.PopCount((ulong)WidgetCompactAnimationCoordinator.RecentFrameOverrunMask)})");
    }

    /// <summary>
    /// Restores the full blurred legacy acrylic after an interaction ends.
    /// Deliberately does not go through the signature reuse check — the
    /// signature never changed, so ApplyBackdropPreference would short-circuit
    /// and leave the tint-only policy in place.
    /// </summary>
    public void RestoreBackdropAfterInteraction()
    {
        if (!_isInteractionBackdropDowngraded)
        {
            return;
        }

        _isInteractionBackdropDowngraded = false;
        if (!LegacyAccentBackdropActive || HWnd == IntPtr.Zero)
        {
            return;
        }

        Win32Helper.ApplyAccentBlur(
            HWnd,
            _lastLegacyAccentTintColor,
            _lastLegacyAccentOpacity,
            enabled: true,
            blurEnabled: true);
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
            _solidColorBackdrop = new SolidTintSystemBackdrop(tintColor);
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

        _solidColorBackdrop.Dispose();
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
        // The per-widget border override resolves before the calculator:
        // FollowGlobal drops the border for widgets with any image background
        // (their own or the global unified/panorama one), and an explicit
        // accent/neutral/none choice always wins.
        (string style, string colorMode) = WidgetBorderCustomization.ApplyOverride(
            WidgetBorderCustomization.GetModeOverride(Config),
            SettingsService.Settings.WidgetBorderStyle,
            SettingsService.Settings.WidgetBorderColorMode,
            WidgetBackgroundCustomization.HasCustomBackground(Config) ||
            WidgetBackgroundCustomization.IsGlobalImageBackgroundActive(
                SettingsService.Settings));
        WidgetBorderVisuals visuals = WidgetBorderVisualCalculator.Resolve(
            style,
            colorMode,
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
        DetachAcrylicControllerTarget();

        WidgetMaterialOpacityProfile profile = WidgetMaterialVisualCalculator.CalculateMica(
            isDark,
            useAlt,
            SettingsService.Settings.WidgetMaterialIntensity);
        var desiredKey = new SharedMicaKey(
            isDark,
            useAlt,
            tintColor,
            (int)Math.Round(profile.TintOpacity * 1000.0),
            (int)Math.Round(profile.LuminosityOpacity * 1000.0));

        if (MicaControllerAttached &&
            _attachedMicaSlot is { } existingSlot &&
            existingSlot.Key == desiredKey &&
            ReferenceEquals(MicaController, existingSlot.Controller))
        {
            return true;
        }

        DetachMicaControllerTarget();
        SharedMicaSlot slot = AcquireOrConfigureSharedMicaSlot(
            desiredKey,
            isDark,
            tintColor,
            useAlt,
            profile);
        if (!slot.Targets.Contains(BackdropTarget))
        {
            if (!slot.Controller.AddSystemBackdropTarget(BackdropTarget))
            {
                return false;
            }

            slot.Targets.Add(BackdropTarget);
        }

        _attachedMicaSlot = slot;
        MicaController = slot.Controller;
        BackdropConfiguration = slot.Configuration;
        MicaControllerAttached = true;
        return true;
    }

    private static SharedMicaSlot AcquireOrConfigureSharedMicaSlot(
        SharedMicaKey desiredKey,
        bool isDark,
        Windows.UI.Color tintColor,
        bool useAlt,
        WidgetMaterialOpacityProfile profile)
    {
        List<SharedMicaSlot> slots = s_sharedMicaSlots ??= [];
        SharedMicaSlot? matching = null;
        SharedMicaSlot? reusableIdle = null;
        foreach (SharedMicaSlot candidate in slots)
        {
            if (candidate.Key == desiredKey)
            {
                matching = candidate;
                break;
            }

            if (reusableIdle is null && candidate.Targets.Count == 0)
            {
                reusableIdle = candidate;
            }
        }

        SharedMicaSlot slot = matching ?? reusableIdle ?? CreateSharedMicaSlot(slots, useAlt);
        ConfigureSharedMicaSlot(slot, desiredKey, isDark, tintColor, useAlt, profile);
        TrimIdleSharedMicaSlots(slots, preserve: slot);
        return slot;
    }

    private static SharedMicaSlot CreateSharedMicaSlot(List<SharedMicaSlot> slots, bool useAlt)
    {
        var configuration = new SystemBackdropConfiguration { IsInputActive = true };
        var controller = new MicaController
        {
            Kind = useAlt ? MicaKind.BaseAlt : MicaKind.Base
        };
        controller.SetSystemBackdropConfiguration(configuration);
        var created = new SharedMicaSlot(controller, configuration);
        slots.Add(created);
        return created;
    }

    private static void ConfigureSharedMicaSlot(
        SharedMicaSlot slot,
        SharedMicaKey key,
        bool isDark,
        Windows.UI.Color tintColor,
        bool useAlt,
        WidgetMaterialOpacityProfile profile)
    {
        slot.Configuration.IsInputActive = true;
        slot.Configuration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        slot.Controller.Kind = useAlt ? MicaKind.BaseAlt : MicaKind.Base;
        slot.Controller.TintColor = tintColor;
        slot.Controller.FallbackColor = WidgetMaterialVisualCalculator.BuildMicaFallbackColor(
            isDark,
            useAlt);
        slot.Controller.TintOpacity = (float)profile.TintOpacity;
        slot.Controller.LuminosityOpacity = (float)profile.LuminosityOpacity;
        slot.Key = key;
    }

    private static void TrimIdleSharedMicaSlots(List<SharedMicaSlot> slots, SharedMicaSlot preserve)
    {
        for (int i = slots.Count - 1; i >= 0 && slots.Count > MaxSharedBackdropSlotsPerFamily; i--)
        {
            SharedMicaSlot candidate = slots[i];
            if (ReferenceEquals(candidate, preserve) || candidate.Targets.Count > 0)
            {
                continue;
            }

            slots.RemoveAt(i);
            try
            {
                candidate.Controller.RemoveAllSystemBackdropTargets();
                candidate.Controller.Dispose();
            }
            catch
            {
            }
        }
    }

    protected void DisposeMicaController()
    {
        _lastAppliedBackdropSignature = null;
        DetachMicaControllerTarget();
        MicaController = null;
    }

    internal static void TrimAllIdleSharedBackdropControllers()
    {
        if (s_sharedMicaSlots is { Count: > 0 } micaSlots)
        {
            for (int i = micaSlots.Count - 1; i >= 0; i--)
            {
                SharedMicaSlot candidate = micaSlots[i];
                if (candidate.Targets.Count > 0)
                {
                    continue;
                }

                micaSlots.RemoveAt(i);
                try
                {
                    candidate.Controller.RemoveAllSystemBackdropTargets();
                    candidate.Controller.Dispose();
                }
                catch
                {
                }
            }
        }

        if (s_sharedAcrylicSlots is { Count: > 0 } acrylicSlots)
        {
            for (int i = acrylicSlots.Count - 1; i >= 0; i--)
            {
                SharedAcrylicSlot candidate = acrylicSlots[i];
                if (candidate.Targets.Count > 0)
                {
                    continue;
                }

                acrylicSlots.RemoveAt(i);
                try
                {
                    candidate.Controller.RemoveAllSystemBackdropTargets();
                    candidate.Controller.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    protected void SuspendBackdropTargetsForHiddenWindow()
    {
        if (IsClosing)
        {
            return;
        }

        ClearSolidColorBackdrop();
        DetachAcrylicControllerTarget();
        DetachMicaControllerTarget();
    }

    protected void DetachMicaControllerTarget()
    {
        if (!MicaControllerAttached)
        {
            _attachedMicaSlot = null;
            return;
        }

        try
        {
            if (BackdropTarget is not null && _attachedMicaSlot is { } slot)
            {
                slot.Targets.Remove(BackdropTarget);
                slot.Controller.RemoveSystemBackdropTarget(BackdropTarget);
            }
            else if (BackdropTarget is not null && MicaController is not null)
            {
                MicaController.RemoveSystemBackdropTarget(BackdropTarget);
            }
        }
        catch
        {
        }
        finally
        {
            _attachedMicaSlot = null;
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
        DetachMicaControllerTarget();

        WidgetMaterialOpacityProfile profile = WidgetMaterialVisualCalculator.CalculateAcrylic(
            isDark,
            useBase,
            surfaceOpacity,
            SettingsService.Settings.WidgetMaterialIntensity);
        var desiredKey = new SharedAcrylicKey(
            isDark,
            useBase,
            tintColor,
            (int)Math.Round(profile.TintOpacity * 1000.0),
            (int)Math.Round(profile.LuminosityOpacity * 1000.0));

        if (AcrylicControllerAttached &&
            _attachedAcrylicSlot is { Controller.IsClosed: false } existingSlot &&
            existingSlot.Key == desiredKey &&
            ReferenceEquals(AcrylicController, existingSlot.Controller))
        {
            return true;
        }

        DetachAcrylicControllerTarget();
        SharedAcrylicSlot slot = AcquireOrConfigureSharedAcrylicSlot(
            desiredKey,
            isDark,
            tintColor,
            useBase,
            profile);
        if (!slot.Targets.Contains(BackdropTarget))
        {
            if (!slot.Controller.AddSystemBackdropTarget(BackdropTarget))
            {
                return false;
            }

            slot.Targets.Add(BackdropTarget);
        }

        _attachedAcrylicSlot = slot;
        AcrylicController = slot.Controller;
        BackdropConfiguration = slot.Configuration;
        AcrylicControllerAttached = true;
        return true;
    }

    private static SharedAcrylicSlot AcquireOrConfigureSharedAcrylicSlot(
        SharedAcrylicKey desiredKey,
        bool isDark,
        Windows.UI.Color tintColor,
        bool useBase,
        WidgetMaterialOpacityProfile profile)
    {
        List<SharedAcrylicSlot> slots = s_sharedAcrylicSlots ??= [];
        slots.RemoveAll(static s => s.Controller.IsClosed);

        SharedAcrylicSlot? matching = null;
        SharedAcrylicSlot? reusableIdle = null;
        foreach (SharedAcrylicSlot candidate in slots)
        {
            if (candidate.Key == desiredKey)
            {
                matching = candidate;
                break;
            }

            if (reusableIdle is null && candidate.Targets.Count == 0)
            {
                reusableIdle = candidate;
            }
        }

        SharedAcrylicSlot slot = matching ?? reusableIdle ?? CreateSharedAcrylicSlot(slots, useBase);
        ConfigureSharedAcrylicSlot(slot, desiredKey, isDark, tintColor, useBase, profile);
        TrimIdleSharedAcrylicSlots(slots, preserve: slot);
        return slot;
    }

    private static SharedAcrylicSlot CreateSharedAcrylicSlot(List<SharedAcrylicSlot> slots, bool useBase)
    {
        var configuration = new SystemBackdropConfiguration { IsInputActive = true };
        var controller = new DesktopAcrylicController
        {
            Kind = useBase ? DesktopAcrylicKind.Base : DesktopAcrylicKind.Thin
        };
        controller.SetSystemBackdropConfiguration(configuration);
        var created = new SharedAcrylicSlot(controller, configuration);
        slots.Add(created);
        return created;
    }

    private static void ConfigureSharedAcrylicSlot(
        SharedAcrylicSlot slot,
        SharedAcrylicKey key,
        bool isDark,
        Windows.UI.Color tintColor,
        bool useBase,
        WidgetMaterialOpacityProfile profile)
    {
        slot.Configuration.IsInputActive = true;
        slot.Configuration.Theme = isDark ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;
        slot.Configuration.HighContrastBackgroundColor = isDark
            ? ColorHelper.FromArgb(0xFF, 0x20, 0x20, 0x20)
            : ColorHelper.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);
        slot.Controller.Kind = useBase ? DesktopAcrylicKind.Base : DesktopAcrylicKind.Thin;
        slot.Controller.TintColor = tintColor;
        slot.Controller.FallbackColor = tintColor;
        slot.Controller.TintOpacity = (float)profile.TintOpacity;
        slot.Controller.LuminosityOpacity = (float)profile.LuminosityOpacity;
        slot.Key = key;
    }

    private static void TrimIdleSharedAcrylicSlots(List<SharedAcrylicSlot> slots, SharedAcrylicSlot preserve)
    {
        for (int i = slots.Count - 1; i >= 0 && slots.Count > MaxSharedBackdropSlotsPerFamily; i--)
        {
            SharedAcrylicSlot candidate = slots[i];
            if (ReferenceEquals(candidate, preserve) || candidate.Targets.Count > 0)
            {
                continue;
            }

            slots.RemoveAt(i);
            try
            {
                candidate.Controller.RemoveAllSystemBackdropTargets();
                candidate.Controller.Dispose();
            }
            catch
            {
            }
        }
    }

    protected void DisposeAcrylicController()
    {
        _lastAppliedBackdropSignature = null;
        DetachAcrylicControllerTarget();
        AcrylicController = null;
    }

    protected void DetachAcrylicControllerTarget()
    {
        if (!AcrylicControllerAttached)
        {
            _attachedAcrylicSlot = null;
            return;
        }

        try
        {
            if (BackdropTarget is not null && _attachedAcrylicSlot is { } slot)
            {
                slot.Targets.Remove(BackdropTarget);
                if (!slot.Controller.IsClosed)
                {
                    slot.Controller.RemoveSystemBackdropTarget(BackdropTarget);
                }
            }
            else if (BackdropTarget is not null && AcrylicController is { IsClosed: false })
            {
                AcrylicController.RemoveSystemBackdropTarget(BackdropTarget);
            }
        }
        catch
        {
        }
        finally
        {
            _attachedAcrylicSlot = null;
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
