using DeskBox.Services;

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

        if (_isWindowRevealCompleted)
        {
            UpdateProgressTimerState();
            UpdateLyricActiveLineAndScheduleTimer();
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
                bool hadInFlightWork =
                    _isRefreshing ||
                    _fullRefreshPending ||
                    Volatile.Read(ref _debouncedMediaRefreshInFlightCount) > 0 ||
                    Volatile.Read(ref _timelineRefreshInFlightCount) > 0 ||
                    Volatile.Read(ref _playbackRefreshInFlightCount) > 0;
                Interlocked.Increment(ref _mediaPropertiesPendingGeneration);
                _timelineRefreshGeneration++;
                _playbackRefreshGeneration++;
                ++_lateCoverProbeGeneration;
                if (hadInFlightWork ||
                    (_hasCurrentMediaInfo &&
                     (!_hasAppliedCoverForCurrentTrack ||
                      _coverRetryInFlight ||
                      DateTimeOffset.UtcNow - _currentTrackCoverStartedUtc < CoverSettleWindow)))
                {
                    Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
                }
            }
        }

        UpdateProgressTimerState();
        UpdateLyricActiveLineAndScheduleTimer();
    }

    public void OnWindowRevealCompleted()
    {
        if (_isDisposed || !_isWindowVisible || _isWindowRevealCompleted)
        {
            return;
        }

        _isWindowRevealCompleted = true;
        DateTimeOffset nowOffsetUtc = DateTimeOffset.UtcNow;
        if (!_isSeeking && _hasCurrentMediaInfo && ShouldExposeTimelinePosition())
        {
            Position = _timelineExtrapolator.GetPosition(nowOffsetUtc);
        }

        UpdateProgressTimerState();
        UpdateLyricActiveLineAndScheduleTimer();

        bool hasHiddenChanges =
            Interlocked.Exchange(ref _hiddenMediaStateDirty, 0) != 0 ||
            ConsumeMemoryEpochChanges() ||
            (_hasCurrentMediaInfo &&
             _timelineExtrapolator.HasExceededNativeDuration(nowOffsetUtc, TrackEndReconcileGrace));
        long lastRefreshTicks =
            Interlocked.Read(ref _lastFullRefreshCompletedUtcTicks);
        DateTime lastRefreshUtc = lastRefreshTicks > 0
            ? new DateTime(lastRefreshTicks, DateTimeKind.Utc)
            : DateTime.MinValue;
        if (ShouldRefreshAfterReveal(
                nowOffsetUtc.UtcDateTime,
                lastRefreshUtc,
                hasHiddenChanges))
        {
            _ = RefreshAsync();
        }
        else if (_hasCurrentMediaInfo &&
                 _currentTrackCoverSignature is { Length: > 0 } coverSig &&
                 (!_hasAppliedCoverForCurrentTrack ||
                  nowOffsetUtc - _currentTrackCoverStartedUtc < CoverSettleWindow))
        {
            ScheduleLateCoverProbes(coverSig);
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
    /// expanded surface's refresh cost when it is fully covered.
    /// </summary>
    public void OnCompactStateChanged(bool collapsed)
    {
        if (_isDisposed || _isCompactCollapsed == collapsed)
        {
            return;
        }

        _isCompactCollapsed = collapsed;
        UpdateProgressTimerState();
        UpdateLyricActiveLineAndScheduleTimer();
        if (!collapsed && _isWindowVisible && _isWindowRevealCompleted)
        {
            _ = RefreshTimelineAsync();
        }
    }
}
