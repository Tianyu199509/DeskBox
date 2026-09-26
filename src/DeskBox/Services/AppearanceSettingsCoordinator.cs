using DeskBox.Contracts;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Sole settings-page writer for the appearance section fields. Numeric
/// sliders only normalize and persist the raw value; the settings shell keeps
/// deciding when to request a live preview and when to schedule the debounced
/// save, so drag-to-preview timing is unchanged. Option fields persist with
/// the same save semantics the settings page used before the migration.
/// </summary>
public sealed class AppearanceSettingsCoordinator : IAppearanceSettings
{
    private const string TrayIconStyleSystem = "System";
    private const string TrayIconStyleColorful = "Colorful";
    private const string TrayIconStyleBlack = "Black";
    private const string TrayIconStyleWhite = "White";

    private readonly SettingsService _settings;
    private bool _stopped;

    internal bool IsStopped => _stopped;

    public AppearanceSettingsCoordinator(SettingsService settings)
    {
        _settings = settings;
    }

    public AppearanceMaterialSettings ReadMaterial()
    {
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        return new(
            shell.WidgetMaterialType,
            shell.WidgetOpacity,
            shell.WidgetMaterialIntensity,
            shell.WidgetCornerPreference,
            shell.WidgetBorderColorMode,
            shell.WidgetBorderStyle);
    }

    public AppearanceDensitySettings ReadDensity()
    {
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        return new(
            shell.LayoutDensity,
            shell.IconSize,
            shell.TextSize,
            shell.LayoutDensityScale,
            shell.HorizontalSpacingScale,
            shell.VerticalSpacingScale,
            fileWidget.FileNameWidthScale,
            fileWidget.FileNameLineCount);
    }

    public AppearanceWindowChromeSettings ReadWindowChrome()
    {
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        return new(
            shell.DefaultWidgetWidth,
            shell.DefaultWidgetHeight,
            shell.DisplayWidgetChromeMode,
            shell.InteractiveWidgetChromeMode,
            shell.WidgetTitleIconMode);
    }

    public AppearanceAnimationSettings ReadAnimation()
    {
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        return new(
            shell.WidgetAnimationEffect,
            shell.WidgetAnimationSpeed,
            shell.WidgetAnimationSlideDirection,
            shell.WidgetAnimationEasingIntensity);
    }

    public AppearanceForegroundSettings ReadForeground()
    {
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        return new(shell.WidgetForegroundMode, shell.WidgetForegroundColor);
    }

    public void SetTrayIconStyle(string? style)
    {
        ThrowIfStopped();
        string normalized = style is TrayIconStyleColorful or TrayIconStyleBlack or TrayIconStyleWhite
            ? style
            : TrayIconStyleSystem;
        CoreSettingsSlice core = _settings.Settings.Core;
        if (core.TrayIconStyle == normalized) return;
        core.TrayIconStyle = normalized;
        _settings.SaveDebounced();
    }

    public AppearanceValueUpdate UpdateWidgetOpacity(double value)
    {
        ThrowIfStopped();
        if (double.IsNaN(value))
        {
            return AppearanceValueUpdate.Rejected(
                _settings.Settings.WidgetShell.WidgetOpacity);
        }

        double normalized = Normalize(value, 0.02d,
            SettingsService.MinWidgetOpacity, SettingsService.MaxWidgetOpacity);
        if (NeedsReentry(normalized, value))
        {
            return AppearanceValueUpdate.NeedsNormalization(normalized);
        }

        _settings.Settings.WidgetShell.WidgetOpacity = normalized;
        return AppearanceValueUpdate.CommittedValue(normalized);
    }

    public AppearanceValueUpdate UpdateWidgetMaterialIntensity(double value)
    {
        ThrowIfStopped();
        if (!double.IsFinite(value))
        {
            return AppearanceValueUpdate.Rejected(
                _settings.Settings.WidgetShell.WidgetMaterialIntensity);
        }

        double normalized = Normalize(value, 0.02d,
            SettingsService.MinWidgetMaterialIntensity,
            SettingsService.MaxWidgetMaterialIntensity);
        if (NeedsReentry(normalized, value))
        {
            return AppearanceValueUpdate.NeedsNormalization(normalized);
        }

        _settings.Settings.WidgetShell.WidgetMaterialIntensity = normalized;
        return AppearanceValueUpdate.CommittedValue(normalized);
    }

    public AppearanceValueUpdate UpdateIconSize(double value)
    {
        ThrowIfStopped();
        if (double.IsNaN(value))
        {
            return AppearanceValueUpdate.Rejected(
                _settings.Settings.WidgetShell.IconSize);
        }

        double normalized = Normalize(value, 2d,
            SettingsService.MinIconSize, SettingsService.MaxIconSize);
        if (NeedsReentry(normalized, value))
        {
            return AppearanceValueUpdate.NeedsNormalization(normalized);
        }

        _settings.Settings.WidgetShell.IconSize = normalized;
        return AppearanceValueUpdate.CommittedValue(normalized);
    }

    public AppearanceValueUpdate UpdateTextSize(double value)
    {
        ThrowIfStopped();
        if (double.IsNaN(value))
        {
            return AppearanceValueUpdate.Rejected(
                _settings.Settings.WidgetShell.TextSize);
        }

        double normalized = Normalize(value, 0.5d,
            SettingsService.MinTextSize, SettingsService.MaxTextSize);
        if (NeedsReentry(normalized, value))
        {
            return AppearanceValueUpdate.NeedsNormalization(normalized);
        }

        _settings.Settings.WidgetShell.TextSize = normalized;
        return AppearanceValueUpdate.CommittedValue(normalized);
    }

    public AppearanceValueUpdate UpdateLayoutDensityScale(double value)
    {
        ThrowIfStopped();
        if (double.IsNaN(value))
        {
            return AppearanceValueUpdate.Rejected(
                _settings.Settings.WidgetShell.LayoutDensityScale);
        }

        double normalized = Normalize(value, 0.02d,
            SettingsService.MinLayoutDensityScale,
            SettingsService.MaxLayoutDensityScale);
        if (NeedsReentry(normalized, value))
        {
            return AppearanceValueUpdate.NeedsNormalization(normalized);
        }

        _settings.Settings.WidgetShell.LayoutDensityScale = normalized;
        return AppearanceValueUpdate.CommittedValue(normalized);
    }

    public AppearanceValueUpdate UpdateHorizontalSpacingScale(double value) =>
        UpdateSpacingScale(
            value,
            () => _settings.Settings.WidgetShell.HorizontalSpacingScale,
            scale => _settings.Settings.WidgetShell.HorizontalSpacingScale = scale);

    public AppearanceValueUpdate UpdateVerticalSpacingScale(double value) =>
        UpdateSpacingScale(
            value,
            () => _settings.Settings.WidgetShell.VerticalSpacingScale,
            scale => _settings.Settings.WidgetShell.VerticalSpacingScale = scale);

    public AppearanceValueUpdate UpdateFileNameWidthScale(double value) =>
        UpdateSpacingScale(
            value,
            () => _settings.Settings.FileWidget.FileNameWidthScale,
            scale => _settings.Settings.FileWidget.FileNameWidthScale = scale);

    private AppearanceValueUpdate UpdateSpacingScale(
        double value,
        Func<double> readStored,
        Action<double> store)
    {
        ThrowIfStopped();
        if (double.IsNaN(value))
        {
            return AppearanceValueUpdate.Rejected(readStored());
        }

        double normalized = Normalize(value, 0.02d,
            SettingsService.MinSpacingScale, SettingsService.MaxSpacingScale);
        if (NeedsReentry(normalized, value))
        {
            return AppearanceValueUpdate.NeedsNormalization(normalized);
        }

        store(normalized);
        return AppearanceValueUpdate.CommittedValue(normalized);
    }

    public void SetWidgetMaterialType(string? materialType)
    {
        ThrowIfStopped();
        string normalized = materialType is
            SettingsService.WidgetMaterialTypeMica or
            SettingsService.WidgetMaterialTypeMicaAlt or
            SettingsService.WidgetMaterialTypeAcrylic or
            SettingsService.WidgetMaterialTypeAcrylicBase or
            SettingsService.WidgetMaterialTypeSolid
                ? materialType
                : SettingsService.WidgetMaterialTypeAcrylic;
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetMaterialType == normalized) return;
        shell.WidgetMaterialType = normalized;
        // Same order the settings page used: preview the new backdrop, then
        // schedule the debounced save.
        _settings.RequestAppearancePreview();
        _settings.SaveDebounced();
    }

    public void SetWidgetCornerPreference(string? preference)
    {
        ThrowIfStopped();
        string normalized = preference is
            SettingsService.WidgetCornerPreferenceSquare or
            SettingsService.WidgetCornerPreferenceSmall or
            SettingsService.WidgetCornerPreferenceRound
                ? preference
                : SettingsService.WidgetCornerPreferenceRound;
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetCornerPreference == normalized) return;
        shell.WidgetCornerPreference = normalized;
        _settings.SaveDebounced();
    }

    public void SetWidgetBorderColorMode(string? mode)
    {
        ThrowIfStopped();
        string normalized = mode is
            SettingsService.WidgetBorderColorModeNeutral or
            SettingsService.WidgetBorderColorModeAccent or
            SettingsService.WidgetBorderColorModeNone
                ? mode
                : SettingsService.WidgetBorderColorModeNeutral;
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetBorderColorMode == normalized) return;
        shell.WidgetBorderColorMode = normalized;
        _settings.SaveDebounced();
    }

    public void SetWidgetBorderStyle(string? style)
    {
        ThrowIfStopped();
        string normalized = style is
            SettingsService.WidgetBorderStyleThin or
            SettingsService.WidgetBorderStyleMedium or
            SettingsService.WidgetBorderStyleThick
                ? style
                : SettingsService.WidgetBorderStyleThin;
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetBorderStyle == normalized) return;
        shell.WidgetBorderStyle = normalized;
        _settings.SaveDebounced();
    }

    public void SetFileNameLineCount(int lineCount)
    {
        ThrowIfStopped();
        int normalized = SettingsService.NormalizeFileNameLineCount(lineCount);
        FileWidgetSettingsSlice fileWidget = _settings.Settings.FileWidget;
        if (fileWidget.FileNameLineCount == normalized) return;
        fileWidget.FileNameLineCount = normalized;
    }

    public void MarkLayoutDensityCustom()
    {
        ThrowIfStopped();
        _settings.Settings.WidgetShell.LayoutDensity =
            SettingsService.LayoutDensityCustom;
    }

    public void ApplyLayoutDensityPreset(string preset)
    {
        ThrowIfStopped();
        // Writes the seven preset fields without scheduling a save; the shell
        // runs its single preview+save pass once the preset application and
        // its slider bindings settle.
        SettingsService.ApplyLayoutDensityPreset(_settings.Settings, preset);
    }

    public AppearanceValueUpdate UpdateDefaultWidgetWidth(double value)
    {
        ThrowIfStopped();
        if (double.IsNaN(value))
        {
            return AppearanceValueUpdate.Rejected(
                _settings.Settings.WidgetShell.DefaultWidgetWidth);
        }

        double normalized = Normalize(value, 10d,
            SettingsService.MinWidgetWidth, 1200d);
        if (NeedsReentry(normalized, value))
        {
            return AppearanceValueUpdate.NeedsNormalization(normalized);
        }

        _settings.Settings.WidgetShell.DefaultWidgetWidth = normalized;
        _settings.SaveDebounced();
        return AppearanceValueUpdate.CommittedValue(normalized);
    }

    public AppearanceValueUpdate UpdateDefaultWidgetHeight(double value)
    {
        ThrowIfStopped();
        if (double.IsNaN(value))
        {
            return AppearanceValueUpdate.Rejected(
                _settings.Settings.WidgetShell.DefaultWidgetHeight);
        }

        double normalized = Normalize(value, 10d,
            SettingsService.MinWidgetHeight, 1200d);
        if (NeedsReentry(normalized, value))
        {
            return AppearanceValueUpdate.NeedsNormalization(normalized);
        }

        _settings.Settings.WidgetShell.DefaultWidgetHeight = normalized;
        _settings.SaveDebounced();
        return AppearanceValueUpdate.CommittedValue(normalized);
    }

    public void SetDisplayWidgetChromeMode(string? mode)
    {
        ThrowIfStopped();
        SetChromeMode(
            mode,
            WidgetChromeMode.Overlay,
            value => _settings.Settings.WidgetShell.DisplayWidgetChromeMode = value);
    }

    public void SetInteractiveWidgetChromeMode(string? mode)
    {
        ThrowIfStopped();
        SetChromeMode(
            mode,
            WidgetChromeMode.Standard,
            value => _settings.Settings.WidgetShell.InteractiveWidgetChromeMode = value);
    }

    private void SetChromeMode(
        string? mode,
        WidgetChromeMode fallback,
        Action<string> store)
    {
        string normalized = SettingsService.NormalizeWidgetChromeModeSetting(mode, fallback);
        store(normalized);
        _settings.SaveDebounced();
    }

    public void SetWidgetTitleIconMode(string? mode)
    {
        ThrowIfStopped();
        string normalized = SettingsService.NormalizeWidgetTitleIconModeSetting(mode);
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetTitleIconMode == normalized) return;
        shell.WidgetTitleIconMode = normalized;
    }

    public void SetAnimationEffect(string? effect, bool scheduleSave = true)
    {
        ThrowIfStopped();
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        string normalized = SettingsService.NormalizeWidgetAnimationEffect(effect);
        if (shell.WidgetAnimationEffect == normalized)
        {
            return;
        }

        shell.WidgetAnimationEffect = normalized;
        if (scheduleSave) _settings.SaveDebounced();
    }

    public void SetAnimationSpeed(string? speed, bool scheduleSave = true)
    {
        ThrowIfStopped();
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        string normalized = SettingsService.NormalizeWidgetAnimationSpeed(speed);
        if (shell.WidgetAnimationSpeed == normalized)
        {
            return;
        }

        shell.WidgetAnimationSpeed = normalized;
        if (scheduleSave) _settings.SaveDebounced();
    }

    public void SetAnimationSlideDirection(string? direction, bool scheduleSave = true)
    {
        ThrowIfStopped();
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        string normalized = SettingsService.NormalizeWidgetAnimationSlideDirection(direction);
        if (shell.WidgetAnimationSlideDirection == normalized)
        {
            return;
        }

        shell.WidgetAnimationSlideDirection = normalized;
        if (scheduleSave) _settings.SaveDebounced();
    }

    public void SetAnimationEasingIntensity(string? intensity, bool scheduleSave = true)
    {
        ThrowIfStopped();
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        string normalized = SettingsService.NormalizeWidgetAnimationEasingIntensity(intensity);
        if (shell.WidgetAnimationEasingIntensity == normalized)
        {
            return;
        }

        shell.WidgetAnimationEasingIntensity = normalized;
        if (scheduleSave) _settings.SaveDebounced();
    }

    public void SetWidgetForegroundMode(string? mode)
    {
        ThrowIfStopped();
        string normalized = WidgetForegroundSettings.NormalizeMode(mode);
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetForegroundMode == normalized) return;
        shell.WidgetForegroundMode = normalized;
    }

    public void SetWidgetForegroundColor(string colorHex)
    {
        ThrowIfStopped();
        WidgetShellSettingsSlice shell = _settings.Settings.WidgetShell;
        if (shell.WidgetForegroundColor == colorHex) return;
        shell.WidgetForegroundColor = colorHex;
    }

    private static double Normalize(double value, double step, double min, double max) =>
        Math.Clamp(
            Math.Round(value / step, MidpointRounding.AwayFromZero) * step,
            min,
            max);

    private static bool NeedsReentry(double normalized, double value) =>
        Math.Abs(normalized - value) > 0.0001;

    private void ThrowIfStopped() => ObjectDisposedException.ThrowIf(_stopped, this);

    internal void Stop() => _stopped = true;
}
