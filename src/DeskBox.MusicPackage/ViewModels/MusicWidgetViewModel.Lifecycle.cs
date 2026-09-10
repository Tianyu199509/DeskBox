using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskBox.MusicPackage.Services;
using DeskBox.MusicPackage.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace DeskBox.MusicPackage.ViewModels;

public sealed partial class MusicWidgetViewModel
{
    public void ApplyAppearance()
    {
        if (_settingsService is null)
        {
            return;
        }

        TextSize = MusicEnvironmentContext.NormalizeTextSize(_settingsService.Settings.TextSize);
        ApplyMusicSettings(_settingsService.Settings);
        RaiseMusicAccentPropertiesChanged();
    }

    public void OnActivated()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isWindowRevealCompleted)
        {
            UpdateProgressTimerState();
            _ = RefreshAsync();
        }
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

        bool visibilityChanged = _isWindowVisible != visible;
        _isWindowVisible = visible;
        if (!visible)
        {
            _isWindowRevealCompleted = false;
            if (visibilityChanged)
            {
                Interlocked.Increment(ref _mediaPropertiesPendingGeneration);
                _timelineRefreshGeneration++;
                _playbackRefreshGeneration++;
            }
        }
        UpdateProgressTimerState();
    }

    public void OnWindowRevealCompleted()
    {
        if (_isDisposed || !_isWindowVisible || _isWindowRevealCompleted)
        {
            return;
        }

        _isWindowRevealCompleted = true;
        UpdateProgressTimerState();
        bool hasHiddenChanges =
            Interlocked.Exchange(ref _hiddenMediaStateDirty, 0) != 0;
        long lastRefreshTicks =
            Interlocked.Read(ref _lastFullRefreshCompletedUtcTicks);
        DateTime lastRefreshUtc = lastRefreshTicks > 0
            ? new DateTime(lastRefreshTicks, DateTimeKind.Utc)
            : DateTime.MinValue;
        if (ShouldRefreshAfterReveal(
                DateTime.UtcNow,
                lastRefreshUtc,
                hasHiddenChanges))
        {
            _ = RefreshAsync();
        }
    }

    internal static bool ShouldRefreshAfterReveal(
        DateTime utcNow,
        DateTime lastRefreshUtc,
        bool hasHiddenChanges)
    {
        return hasHiddenChanges ||
            lastRefreshUtc == DateTime.MinValue ||
            utcNow - lastRefreshUtc >= RevealFullRefreshFreshness;
    }

    /// <summary>
    /// Keeps capsule progress live at a lower cadence while avoiding the
    /// expanded surface's 500 ms refresh cost when it is fully covered.
    /// </summary>
    public void OnCompactStateChanged(bool collapsed)
    {
        if (_isDisposed || _isCompactCollapsed == collapsed)
        {
            return;
        }

        _isCompactCollapsed = collapsed;
        UpdateProgressTimerState();
        if (!collapsed && _isWindowVisible && _isWindowRevealCompleted)
        {
            _ = RefreshTimelineAsync();
        }
    }
}

