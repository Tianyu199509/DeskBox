using System.Globalization;
using System.Collections.ObjectModel;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskBox.Contracts;
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
            OnPropertyChanged(nameof(WidgetTransparency));
            return;
        }

        AppearanceValueUpdate update = _appearanceSettings.UpdateWidgetOpacity(value);
        if (!update.Committed)
        {
            WidgetOpacity = update.Value;
            return;
        }

        SaveAppearanceChange();
        OnPropertyChanged(nameof(WidgetOpacityValueText));
        OnPropertyChanged(nameof(WidgetOpacityPercent));
        OnPropertyChanged(nameof(WidgetOpacityPercentInput));
        OnPropertyChanged(nameof(WidgetTransparency));
    }

    partial void OnWidgetMaterialIntensityChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(WidgetMaterialIntensityValueText));
            return;
        }

        AppearanceValueUpdate update = _appearanceSettings.UpdateWidgetMaterialIntensity(value);
        if (!update.Committed)
        {
            WidgetMaterialIntensity = update.Value;
            return;
        }

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

        AppearanceValueUpdate update = _appearanceSettings.UpdateIconSize(value);
        if (!update.Committed)
        {
            IconSize = update.Value;
            return;
        }

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

        AppearanceValueUpdate update = _appearanceSettings.UpdateTextSize(value);
        if (!update.Committed)
        {
            TextSize = update.Value;
            return;
        }

        SyncLayoutDensitySelection();
        SaveAppearanceChange();
        // Global text size also drives the effective inherited font sizes of
        // the Todo and Quick Capture sections; keep them in sync immediately
        // (batch 22 regression) while their raw override values stay as-is.
        _todoSettings.Refresh();
        _quickCaptureSettings.RefreshFromSettings();
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

        AppearanceValueUpdate update = _appearanceSettings.UpdateLayoutDensityScale(value);
        if (!update.Committed)
        {
            LayoutDensityScale = update.Value;
            return;
        }

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
            _appearanceSettings.UpdateHorizontalSpacingScale(value),
            next => HorizontalSpacingScale = next,
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
            _appearanceSettings.UpdateVerticalSpacingScale(value),
            next => VerticalSpacingScale = next,
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
            _appearanceSettings.UpdateFileNameWidthScale(value),
            next => FileNameWidthScale = next,
            nameof(FileNameWidthValueText),
            nameof(FileNameWidthPercent),
            nameof(FileNameWidthPercentInput));
    }

}
