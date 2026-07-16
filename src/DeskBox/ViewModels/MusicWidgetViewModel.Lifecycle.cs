using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace DeskBox.ViewModels;

public sealed partial class MusicWidgetViewModel
{
    public void ApplyAppearance()
    {
        if (_settingsService is null)
        {
            return;
        }

        TextSize = SettingsService.NormalizeTextSize(_settingsService.Settings.TextSize);
        ApplyMusicSettings(_settingsService.Settings);
        RaiseMusicAccentPropertiesChanged();
    }

    public void OnActivated()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isWindowVisible)
        {
            _progressTimer?.Start();
            UpdateMusicTimerDiagnostics();
        }
        _ = RefreshAsync();
    }

    public void OnDeactivated()
    {
    }

    /// <summary>
    /// Called when the host window becomes visible or hidden.
    /// Stops all timers when hidden to avoid unnecessary CPU/GPU usage,
    /// and restarts them when the window becomes visible again.
    /// </summary>
    public void OnWindowVisibilityChanged(bool visible)
    {
        if (_isDisposed)
        {
            return;
        }

        _isWindowVisible = visible;
        if (visible)
        {
            _progressTimer?.Start();
            _ = RefreshAsync();
        }
        else
        {
            _progressTimer?.Stop();
        }
        UpdateMusicTimerDiagnostics();
    }
}
