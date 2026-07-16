using System.Globalization;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    partial void OnWidgetOpacityChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(WidgetOpacityValueText));
            OnPropertyChanged(nameof(WidgetOpacityPercent));
            OnPropertyChanged(nameof(WidgetOpacityPercentInput));
            return;
        }

        if (double.IsNaN(value))
        {
            WidgetOpacity = _settingsService.Settings.WidgetOpacity;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value / 0.02d, MidpointRounding.AwayFromZero) * 0.02d,
            SettingsService.MinWidgetOpacity,
            SettingsService.MaxWidgetOpacity);

        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            WidgetOpacity = normalizedValue;
            return;
        }

        _settingsService.Settings.WidgetOpacity = normalizedValue;
        SaveAppearanceChange();
        OnPropertyChanged(nameof(WidgetOpacityValueText));
        OnPropertyChanged(nameof(WidgetOpacityPercent));
        OnPropertyChanged(nameof(WidgetOpacityPercentInput));
    }

    partial void OnWidgetMaterialIntensityChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(WidgetMaterialIntensityValueText));
            return;
        }

        if (!double.IsFinite(value))
        {
            WidgetMaterialIntensity = _settingsService.Settings.WidgetMaterialIntensity;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value / 0.02d, MidpointRounding.AwayFromZero) * 0.02d,
            SettingsService.MinWidgetMaterialIntensity,
            SettingsService.MaxWidgetMaterialIntensity);
        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            WidgetMaterialIntensity = normalizedValue;
            return;
        }

        _settingsService.Settings.WidgetMaterialIntensity = normalizedValue;
        SaveAppearanceChange();
        OnPropertyChanged(nameof(WidgetMaterialIntensityValueText));
    }

    partial void OnIconSizeChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(IconSizeValueText));
            OnPropertyChanged(nameof(IconSizeInput));
            return;
        }

        if (double.IsNaN(value))
        {
            IconSize = _settingsService.Settings.IconSize;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value / 2d, MidpointRounding.AwayFromZero) * 2d,
            SettingsService.MinIconSize,
            SettingsService.MaxIconSize);

        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            IconSize = normalizedValue;
            return;
        }

        _settingsService.Settings.IconSize = normalizedValue;
        SyncLayoutDensitySelection();
        SaveAppearanceChange();
        OnPropertyChanged(nameof(IconSizeValueText));
        OnPropertyChanged(nameof(IconSizeInput));
    }

    partial void OnTextSizeChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(TextSizeValueText));
            OnPropertyChanged(nameof(TextSizeInput));
            return;
        }

        if (double.IsNaN(value))
        {
            TextSize = _settingsService.Settings.TextSize;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value * 2d, MidpointRounding.AwayFromZero) / 2d,
            SettingsService.MinTextSize,
            SettingsService.MaxTextSize);

        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            TextSize = normalizedValue;
            return;
        }

        _settingsService.Settings.TextSize = normalizedValue;
        SyncLayoutDensitySelection();
        SaveAppearanceChange();
        OnPropertyChanged(nameof(TextSizeValueText));
        OnPropertyChanged(nameof(TextSizeInput));
    }

    partial void OnLayoutDensityScaleChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(LayoutDensityValueText));
            OnPropertyChanged(nameof(LayoutDensityPercent));
            OnPropertyChanged(nameof(LayoutDensityPercentInput));
            return;
        }

        if (double.IsNaN(value))
        {
            LayoutDensityScale = _settingsService.Settings.LayoutDensityScale;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value / 0.02d, MidpointRounding.AwayFromZero) * 0.02d,
            SettingsService.MinLayoutDensityScale,
            SettingsService.MaxLayoutDensityScale);

        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            LayoutDensityScale = normalizedValue;
            return;
        }

        _settingsService.Settings.LayoutDensityScale = normalizedValue;
        SyncLayoutDensitySelection();
        SaveAppearanceChange();
        OnPropertyChanged(nameof(LayoutDensityValueText));
        OnPropertyChanged(nameof(LayoutDensityPercent));
        OnPropertyChanged(nameof(LayoutDensityPercentInput));
    }

    partial void OnHorizontalSpacingScaleChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(HorizontalSpacingValueText));
            OnPropertyChanged(nameof(HorizontalSpacingPercent));
            OnPropertyChanged(nameof(HorizontalSpacingPercentInput));
            return;
        }

        ApplySpacingScaleChange(
            value,
            _settingsService.Settings.HorizontalSpacingScale,
            next => HorizontalSpacingScale = next,
            next => _settingsService.Settings.HorizontalSpacingScale = next,
            nameof(HorizontalSpacingValueText),
            nameof(HorizontalSpacingPercent),
            nameof(HorizontalSpacingPercentInput));
    }

    partial void OnVerticalSpacingScaleChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(VerticalSpacingValueText));
            OnPropertyChanged(nameof(VerticalSpacingPercent));
            OnPropertyChanged(nameof(VerticalSpacingPercentInput));
            return;
        }

        ApplySpacingScaleChange(
            value,
            _settingsService.Settings.VerticalSpacingScale,
            next => VerticalSpacingScale = next,
            next => _settingsService.Settings.VerticalSpacingScale = next,
            nameof(VerticalSpacingValueText),
            nameof(VerticalSpacingPercent),
            nameof(VerticalSpacingPercentInput));
    }

    partial void OnFileNameWidthScaleChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(FileNameWidthValueText));
            OnPropertyChanged(nameof(FileNameWidthPercent));
            OnPropertyChanged(nameof(FileNameWidthPercentInput));
            return;
        }

        ApplySpacingScaleChange(
            value,
            _settingsService.Settings.FileNameWidthScale,
            next => FileNameWidthScale = next,
            next => _settingsService.Settings.FileNameWidthScale = next,
            nameof(FileNameWidthValueText),
            nameof(FileNameWidthPercent),
            nameof(FileNameWidthPercentInput));
    }
}
