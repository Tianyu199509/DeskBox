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
    partial void OnAutoStartChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        StartupService.SetEnabled(value);
        _settingsService.Settings.AutoStart = value;
        _settingsService.SaveDebounced();
    }

    partial void OnAutoCheckForUpdatesChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.AutoCheckForUpdates = value;
        _settingsService.SaveDebounced();
    }

    partial void OnDoubleClickToOpenChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.DoubleClickToOpen = value;
        _settingsService.SaveDebounced();
    }

    partial void OnResizeSnapEnabledChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.ResizeSnapEnabled = value;
        _settingsService.SaveDebounced();

        // Sync to the live overlay service
        if (App.Current is { } app)
        {
            app.ResizeGuideOverlay.IsSnapEnabled = value;
        }
    }

    partial void OnDefaultWidthChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(DefaultWidthInput));
            return;
        }

        if (double.IsNaN(value))
        {
            DefaultWidth = _settingsService.Settings.DefaultWidgetWidth;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value / 10d, MidpointRounding.AwayFromZero) * 10d,
            SettingsService.MinWidgetWidth,
            1200d);

        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            DefaultWidth = normalizedValue;
            return;
        }

        _settingsService.Settings.DefaultWidgetWidth = normalizedValue;
        _settingsService.SaveDebounced();
        OnPropertyChanged(nameof(DefaultWidthInput));
    }

    partial void OnDefaultHeightChanged(double value)
    {
        if (_isRestoringDefaults)
        {
            OnPropertyChanged(nameof(DefaultHeightInput));
            return;
        }

        if (double.IsNaN(value))
        {
            DefaultHeight = _settingsService.Settings.DefaultWidgetHeight;
            return;
        }

        double normalizedValue = Math.Clamp(
            Math.Round(value / 10d, MidpointRounding.AwayFromZero) * 10d,
            SettingsService.MinWidgetHeight,
            1200d);

        if (Math.Abs(normalizedValue - value) > 0.0001)
        {
            DefaultHeight = normalizedValue;
            return;
        }

        _settingsService.Settings.DefaultWidgetHeight = normalizedValue;
        _settingsService.SaveDebounced();
        OnPropertyChanged(nameof(DefaultHeightInput));
    }

    partial void OnHideShortcutArrowOverlayChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.HideShortcutArrowOverlay = value;
        _settingsService.SaveDebounced();
    }

    partial void OnShowImageFilesAsIconsChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.ShowImageFilesAsIcons = value;
        _settingsService.SaveDebounced();
    }

    partial void OnShowHoverButtonsChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.ShowHoverButtons = value;
        _settingsService.SaveDebounced();
    }

    partial void OnShowHoverActionLockPositionChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionLockPosition, value);
    }

    partial void OnShowHoverActionLockSizeChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionLockSize, value);
    }

    partial void OnShowHoverActionAddChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionAdd, value);
    }

    partial void OnShowHoverActionMoreChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionMore, value);
    }

    partial void OnShowHoverActionDeleteChanged(bool value)
    {
        OnHoverButtonActionSelectionChanged(SettingsService.WidgetHoverActionDelete, value);
    }

    partial void OnShowListItemDetailsChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.ShowListItemDetails = value;
        _settingsService.SaveDebounced();
    }

    partial void OnShowFileItemPathTooltipsChanged(bool value)
    {
        if (_isRestoringDefaults || _isApplyingSettingsSnapshot)
        {
            return;
        }

        _settingsService.Settings.ShowFileItemPathTooltips = value;
        _settingsService.SaveDebounced();
    }

    partial void OnShowFileExtensionsChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.ShowFileExtensions = value;
        _settingsService.SaveDebounced();
    }

    partial void OnHideShortcutExtensionWhenShowingFileExtensionsChanged(bool value)
    {
        if (_isRestoringDefaults)
        {
            return;
        }

        _settingsService.Settings.HideShortcutExtensionWhenShowingFileExtensions = value;
        _settingsService.SaveDebounced();
    }
}
