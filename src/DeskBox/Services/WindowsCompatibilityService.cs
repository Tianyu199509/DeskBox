using System.Diagnostics;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace DeskBox.Services;

/// <summary>
/// Centralizes OS and user visual capability checks.  The project is compiled
/// against a current Windows SDK, so every Win11-only call must still be
/// guarded at runtime for the Win10 compatibility floor.
/// </summary>
public static class WindowsCompatibilityService
{
    public const int Windows11Build = 22000;

    private static readonly Lazy<int> s_osBuild = new(GetOsBuild);
    private static readonly object s_animationSettingsCacheGate = new();
    private static readonly long s_animationSettingsCacheDurationTicks =
        Stopwatch.Frequency * 2;
    private static long s_animationSettingsReadTimestamp;
    private static int s_cachedShouldAnimate = -1;

    // WinRT settings objects are process-wide singletons by design; allocating
    // them per read creates a fresh RCW every call on hot paths (stack
    // animations, compact borders, navigation checks). Cache one instance and
    // subscribe to change events to invalidate the composite animation cache
    // immediately instead of waiting for the TTL.
    private static readonly object s_settingsInstanceGate = new();
    private static UISettings? s_uiSettings;
    private static AccessibilitySettings? s_accessibilitySettings;
    private static readonly Lazy<System.Reflection.PropertyInfo?> s_advancedEffectsProperty =
        new(() => typeof(UISettings).GetProperty("AdvancedEffectsEnabled"));

    public static int OsBuild => s_osBuild.Value;

    public static bool IsWindows11OrLater => OsBuild >= Windows11Build;

    public static bool SupportsWin11DwmAttributes => IsWindows11OrLater;

    public static bool SupportsNativeWindowCorners => IsWindows11OrLater;

    public static bool SupportsMica => IsWindows11OrLater && IsSupported(() => MicaController.IsSupported());

    public static bool SupportsDesktopAcrylic =>
        IsSupported(() => DesktopAcrylicController.IsSupported());

    public static bool UsesLegacyWindowAcrylic => !IsWindows11OrLater;

    public static string ResolveWidgetMaterialType(string? requestedMaterialType)
    {
        return ResolveWidgetMaterialTypeForBuild(requestedMaterialType, OsBuild);
    }

    /// <summary>
    /// Resolves the corner preference that can actually be rendered on the
    /// current platform. The persisted setting remains the user's requested
    /// value so a Win10 profile can regain its preference after moving to
    /// Windows 11.
    /// </summary>
    public static string ResolveEffectiveWidgetCornerPreference(string? requestedPreference)
    {
        return ResolveEffectiveWidgetCornerPreferenceForBuild(requestedPreference, OsBuild);
    }

    internal static string ResolveEffectiveWidgetCornerPreferenceForBuild(
        string? requestedPreference,
        int osBuild)
    {
        string normalized = requestedPreference is
            SettingsService.WidgetCornerPreferenceSquare or
            SettingsService.WidgetCornerPreferenceSmall or
            SettingsService.WidgetCornerPreferenceRound
                ? requestedPreference
                : SettingsService.WidgetCornerPreferenceRound;

        return osBuild < Windows11Build
            ? SettingsService.WidgetCornerPreferenceSquare
            : normalized;
    }

    /// <summary>
    /// Resolves the compact media corner mode that can actually be rendered
    /// on the current platform. Win10 keeps all capsule media surfaces square,
    /// while Win11 continues to honor the explicit media preference.
    /// </summary>
    public static string ResolveEffectiveWidgetCompactMediaCornerMode(string? requestedMode)
    {
        return ResolveEffectiveWidgetCompactMediaCornerModeForBuild(requestedMode, OsBuild);
    }

    internal static string ResolveEffectiveWidgetCompactMediaCornerModeForBuild(
        string? requestedMode,
        int osBuild)
    {
        string normalized = SettingsService.NormalizeWidgetCompactMediaCornerMode(requestedMode);
        return osBuild < Windows11Build
            ? SettingsService.WidgetCompactMediaCornerSquare
            : normalized;
    }

    internal static string ResolveWidgetMaterialTypeForBuild(
        string? requestedMaterialType,
        int osBuild)
    {
        string normalized = requestedMaterialType is
            SettingsService.WidgetMaterialTypeMica or
            SettingsService.WidgetMaterialTypeMicaAlt or
            SettingsService.WidgetMaterialTypeAcrylic or
            SettingsService.WidgetMaterialTypeAcrylicBase or
            SettingsService.WidgetMaterialTypeSolid
                ? requestedMaterialType
                : SettingsService.WidgetMaterialTypeAcrylic;

        if (osBuild < Windows11Build && SettingsService.IsMicaMaterial(normalized))
        {
            return SettingsService.WidgetMaterialTypeAcrylic;
        }

        return normalized;
    }

    /// <summary>
    /// Applies a secondary-window backdrop without invoking Win11-only APIs on
    /// the Windows 10 compatibility floor. If no system material is available,
    /// SystemBackdrop is cleared and the window's opaque XAML surface remains
    /// the solid-color fallback.
    /// </summary>
    public static string ApplySafeBackdrop(Window window, bool preferMica = true)
    {
        ArgumentNullException.ThrowIfNull(window);
        try
        {
            if (preferMica && SupportsMica)
            {
                window.SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
                return "Mica";
            }

            if (SupportsDesktopAcrylic)
            {
                window.SystemBackdrop = new DesktopAcrylicBackdrop();
                return "Acrylic";
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[Backdrop] System material unavailable: {ex.Message}");
        }

        window.SystemBackdrop = null;
        return "Solid";
    }

    public static bool AreAnimationsEnabled => ReadUiSetting(
        static settings => settings.AnimationsEnabled,
        fallback: true);

    /// <summary>
    /// Advanced effects is not available on every supported Windows contract;
    /// reflection keeps the Win10 fallback safe while still honoring the user
    /// setting on newer systems.
    /// </summary>
    public static bool AreAdvancedEffectsEnabled
    {
        get
        {
            UISettings? settings = UiSettingsInstance;
            if (settings is null)
            {
                return true;
            }

            try
            {
                return s_advancedEffectsProperty.Value?.GetValue(settings) as bool? ?? true;
            }
            catch
            {
                return true;
            }
        }
    }

    public static bool IsHighContrast => ReadAccessibilitySetting(
        static settings => settings.HighContrast,
        fallback: false);

    /// <summary>
    /// Reads a system UI color from the shared <see cref="UISettings"/>
    /// instance. Returns null when the settings object is unavailable so
    /// callers can keep their configured fallback.
    /// </summary>
    public static Windows.UI.Color? TryGetUiColor(
        Windows.UI.ViewManagement.UIColorType colorType)
    {
        UISettings? settings = UiSettingsInstance;
        if (settings is null)
        {
            return null;
        }

        try
        {
            return settings.GetColorValue(colorType);
        }
        catch
        {
            return null;
        }
    }

    public static bool ShouldAnimate
    {
        get
        {
            long now = Stopwatch.GetTimestamp();
            int cached = Volatile.Read(ref s_cachedShouldAnimate);
            long readAt = Volatile.Read(ref s_animationSettingsReadTimestamp);
            if (cached >= 0 &&
                now - readAt <= s_animationSettingsCacheDurationTicks)
            {
                return cached == 1;
            }

            lock (s_animationSettingsCacheGate)
            {
                now = Stopwatch.GetTimestamp();
                cached = Volatile.Read(ref s_cachedShouldAnimate);
                readAt = Volatile.Read(ref s_animationSettingsReadTimestamp);
                if (cached >= 0 &&
                    now - readAt <= s_animationSettingsCacheDurationTicks)
                {
                    return cached == 1;
                }

                bool shouldAnimate = ResolveShouldAnimate(
                    AreAnimationsEnabled,
                    AreAdvancedEffectsEnabled,
                    IsHighContrast);
                Volatile.Write(
                    ref s_cachedShouldAnimate,
                    shouldAnimate ? 1 : 0);
                Volatile.Write(
                    ref s_animationSettingsReadTimestamp,
                    now);
                return shouldAnimate;
            }
        }
    }

    internal static bool ResolveShouldAnimate(
        bool animationsEnabled,
        bool advancedEffectsEnabled,
        bool highContrast)
    {
        return animationsEnabled && advancedEffectsEnabled && !highContrast;
    }

    private static int GetOsBuild()
    {
        try
        {
            return Environment.OSVersion.Version.Build;
        }
        catch
        {
            // Capability detection must fail closed. Assuming Windows 11 here
            // could invoke unsupported DWM/backdrop APIs on the Win10 floor.
            return 0;
        }
    }

    private static bool IsSupported(Func<bool> probe)
    {
        try
        {
            return probe();
        }
        catch
        {
            return false;
        }
    }

    private static UISettings? UiSettingsInstance
    {
        get
        {
            if (s_uiSettings is not null)
            {
                return s_uiSettings;
            }

            lock (s_settingsInstanceGate)
            {
                if (s_uiSettings is not null)
                {
                    return s_uiSettings;
                }

                try
                {
                    var settings = new UISettings();
                    settings.AnimationsEnabledChanged +=
                        UiSettings_Changed;
                    s_uiSettings = settings;
                }
                catch
                {
                    // Leave null; the fallback values apply and the next
                    // probe retries creation.
                }

                return s_uiSettings;
            }
        }
    }

    private static AccessibilitySettings? AccessibilitySettingsInstance
    {
        get
        {
            if (s_accessibilitySettings is not null)
            {
                return s_accessibilitySettings;
            }

            lock (s_settingsInstanceGate)
            {
                if (s_accessibilitySettings is not null)
                {
                    return s_accessibilitySettings;
                }

                try
                {
                    var settings = new AccessibilitySettings();
                    settings.HighContrastChanged +=
                        AccessibilitySettings_Changed;
                    s_accessibilitySettings = settings;
                }
                catch
                {
                    // Leave null; the fallback value applies.
                }

                return s_accessibilitySettings;
            }
        }
    }

    private static void UiSettings_Changed(UISettings sender, object args) =>
        InvalidateAnimationCapabilityCache();

    private static void AccessibilitySettings_Changed(
        AccessibilitySettings sender,
        object args) =>
        InvalidateAnimationCapabilityCache();

    private static void InvalidateAnimationCapabilityCache() =>
        Volatile.Write(ref s_cachedShouldAnimate, -1);

    private static bool ReadUiSetting(Func<UISettings, bool> read, bool fallback)
    {
        UISettings? settings = UiSettingsInstance;
        if (settings is null)
        {
            return fallback;
        }

        try
        {
            return read(settings);
        }
        catch
        {
            return fallback;
        }
    }

    private static bool ReadAccessibilitySetting(
        Func<AccessibilitySettings, bool> read,
        bool fallback)
    {
        AccessibilitySettings? settings = AccessibilitySettingsInstance;
        if (settings is null)
        {
            return fallback;
        }

        try
        {
            return read(settings);
        }
        catch
        {
            return fallback;
        }
    }
}
