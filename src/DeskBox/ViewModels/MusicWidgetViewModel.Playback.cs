using DeskBox.Services;
using DeskBox.Services.Lyrics;

namespace DeskBox.ViewModels;

public sealed partial class MusicWidgetViewModel
{
    public async Task InitializeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await RefreshAsync();
        if (_isDisposed)
        {
            return;
        }

        UpdateProgressTimerState();
        UpdateLyricActiveLineAndScheduleTimer();
    }

    public async Task RefreshAsync(int? mediaPropertiesGeneration = null)
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isRefreshing)
        {
            _fullRefreshPending = true;
            _pendingFullRefreshMediaGeneration = mediaPropertiesGeneration;
            return;
        }

        _isRefreshing = true;
        bool refreshCompleted = false;
        try
        {
            do
            {
                _fullRefreshPending = false;
                int? refreshGeneration = mediaPropertiesGeneration;
                try
                {
                    await _musicSessionService.InitializeAsync();
                    if (_isDisposed)
                    {
                        return;
                    }

                    RefreshSessionList();
                    MusicSessionInfo? info = await _musicSessionService.GetCurrentSessionInfoAsync(_preferredSessionId);
                    if (_isDisposed)
                    {
                        return;
                    }

                    bool isStaleMediaRefresh = refreshGeneration is { } expectedGeneration &&
                        expectedGeneration != Volatile.Read(ref _mediaPropertiesPendingGeneration);
                    if (!isStaleMediaRefresh)
                    {
                        await ApplyInfoAsync(info);
                        refreshCompleted = true;
                    }
                }
                catch (Exception ex)
                {
                    if (_isDisposed)
                    {
                        return;
                    }

                    App.Log($"[MusicWidget] Refresh failed: {ex}");
                    await ApplyInfoAsync(null);
                }

                if (_fullRefreshPending)
                {
                    mediaPropertiesGeneration = _pendingFullRefreshMediaGeneration;
                    _pendingFullRefreshMediaGeneration = null;
                }
            }
            while (_fullRefreshPending && !_isDisposed);
        }
        finally
        {
            if (refreshCompleted)
            {
                Interlocked.Exchange(
                    ref _lastFullRefreshCompletedUtcTicks,
                    DateTime.UtcNow.Ticks);
            }

            _isRefreshing = false;
        }
    }

    public async Task SelectSessionAsync(int index)
    {
        if (_isDisposed)
        {
            return;
        }

        if (index < 0 || index >= SessionIds.Count)
        {
            return;
        }

        await SelectSessionAsync(SessionIds[index]);
    }

    public async Task<IReadOnlyList<MusicSessionOption>> GetAvailableSessionOptionsAsync()
    {
        if (_isDisposed)
        {
            return [];
        }

        await _musicSessionService.InitializeAsync();
        if (_isDisposed)
        {
            return [];
        }

        return RefreshSessionList();
    }

    public async Task<bool> SelectSessionAsync(string? sessionId)
    {
        if (_isDisposed)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sessionId))
        {
            _preferredSessionId = null;
            RaiseSelectedSessionChanged();
            await RefreshAsync();
            return true;
        }

        bool selected = await _musicSessionService.TrySetPreferredSessionAsync(sessionId);
        if (!selected)
        {
            // The source may have closed after the picker was populated. Return
            // to the system session instead of retaining an invalid selection.
            _preferredSessionId = null;
            RaiseSelectedSessionChanged();
            await RefreshAsync();
            return false;
        }

        _preferredSessionId = sessionId;
        RaiseSelectedSessionChanged();
        await RefreshAsync();
        return true;
    }

    private void RaiseSelectedSessionChanged()
    {
        OnPropertyChanged(nameof(IsFollowingSystemSession));
        OnPropertyChanged(nameof(PreferredSessionId));
        OnPropertyChanged(nameof(SelectedSessionIndex));
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        if (IsPlaying)
        {
            await _musicSessionService.TryPauseAsync(_preferredSessionId);
        }
        else
        {
            await _musicSessionService.TryPlayAsync(_preferredSessionId);
        }

        await RefreshPlaybackAsync();
        await RefreshTimelineAsync();
        await Task.Delay(180);
        if (!_isDisposed)
        {
            await RefreshPlaybackAsync();
            await RefreshTimelineAsync();
        }
    }

    public async Task PreviousAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await _musicSessionService.TryPreviousAsync(_preferredSessionId);
        ScheduleDebouncedMediaRefresh();
    }

    public async Task NextAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await _musicSessionService.TryNextAsync(_preferredSessionId);
        ScheduleDebouncedMediaRefresh();
    }

    public async Task CyclePlaybackModeAsync()
    {
        if (_isDisposed || _isChangingPlaybackMode || !CanChangePlaybackMode)
        {
            return;
        }

        _isChangingPlaybackMode = true;
        try
        {
            MusicPlaybackMode requestedMode = GetNextPlaybackMode();
            bool didChange = await _musicSessionService.TryChangePlaybackModeAsync(_preferredSessionId, requestedMode);
            if (!didChange)
            {
                App.Log($"[MusicWidget] Playback mode request was rejected. requested={requestedMode}");
            }

            await RefreshPlaybackAsync();
            await Task.Delay(180);
            if (!_isDisposed)
            {
                await RefreshPlaybackAsync();
            }
        }
        finally
        {
            _isChangingPlaybackMode = false;
        }
    }

    public Task RefreshSystemVolumeAsync() => RefreshVolumeAsync();

    public async Task RefreshVolumeAsync()
    {
        if (_isDisposed || _isRefreshingVolume)
        {
            return;
        }

        _isRefreshingVolume = true;
        try
        {
            MusicVolumeSnapshot snapshot = await _musicVolumeService.GetVolumeAsync(
                _sourceAppUserModelId,
                _sourceDisplayName);

            if (_isDisposed)
            {
                return;
            }

            SystemVolume = snapshot.SystemVolume;
            HasSessionVolume = snapshot.HasSessionVolume;
            if (snapshot.HasSessionVolume)
            {
                SessionVolume = snapshot.SessionVolume;
            }
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Refresh volume failed: {ex.Message}");
        }
        finally
        {
            _isRefreshingVolume = false;
        }
    }

    public async Task SetSystemVolumeAsync(double volume)
    {
        if (_isDisposed)
        {
            return;
        }

        SystemVolume = volume;
        _pendingSystemVolume = SystemVolume;
        if (_isChangingSystemVolume)
        {
            return;
        }

        _isChangingSystemVolume = true;
        try
        {
            while (_pendingSystemVolume.HasValue)
            {
                double requestedVolume = _pendingSystemVolume.Value;
                _pendingSystemVolume = null;

                bool didChange = await _musicVolumeService.TrySetSystemMasterVolumeAsync(requestedVolume);
                if (!didChange)
                {
                    App.Log("[MusicWidget] System volume request was rejected.");
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Set system volume failed: {ex.Message}");
        }
        finally
        {
            _isChangingSystemVolume = false;
        }
    }

    public async Task SetSessionVolumeAsync(double volume)
    {
        if (_isDisposed || !HasSessionVolume)
        {
            return;
        }

        SessionVolume = volume;
        _pendingSessionVolume = SessionVolume;
        if (_isChangingSessionVolume)
        {
            return;
        }

        _isChangingSessionVolume = true;
        try
        {
            while (_pendingSessionVolume.HasValue)
            {
                double requestedVolume = _pendingSessionVolume.Value;
                _pendingSessionVolume = null;

                bool didChange = await _musicVolumeService.TrySetSessionVolumeAsync(
                    _sourceAppUserModelId,
                    _sourceDisplayName,
                    requestedVolume);
                if (!didChange)
                {
                    App.Log("[MusicWidget] Session volume request was rejected.");
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Set session volume failed: {ex.Message}");
        }
        finally
        {
            _isChangingSessionVolume = false;
        }
    }

    public void ToggleLyricsView()
    {
        if (!ShowLyrics || !HasSession)
        {
            return;
        }

        IsLyricsViewOpen = !IsLyricsViewOpen;
    }

    public void CloseLyricsView()
    {
        IsLyricsViewOpen = false;
    }

    public async Task SeekToLyricLineAsync(LrcLine? line)
    {
        if (_isDisposed || line is null || !CanInteractWithProgress)
        {
            return;
        }

        double shiftedMs = line.Timestamp.TotalMilliseconds + _currentLyrics.TotalOffsetMs;
        TimeSpan target = TimeSpan.FromMilliseconds(Math.Clamp(shiftedMs, 0, Duration.TotalMilliseconds));

        bool didSeek = await _musicSessionService.TrySeekAsync(_preferredSessionId, target);
        if (!didSeek)
        {
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        _timelineExtrapolator.ApplyManualSeek(target, nowUtc);
        Position = target;
        UpdateLyricActiveLineAndScheduleTimer();
    }

    public async Task AdjustLyricsOffsetAsync(int deltaMs, bool reset = false)
    {
        if (_isDisposed || !_hasCurrentMediaInfo || string.IsNullOrWhiteSpace(_title))
        {
            return;
        }

        var query = new LyricsQuery(_title, _artist, _album, Duration);
        int updatedOffsetMs = await _lyricsService.AdjustUserOffsetAsync(query, deltaMs, reset);
        if (_isDisposed)
        {
            return;
        }

        _currentLyrics = _currentLyrics.WithUserOffset(updatedOffsetMs);
        OnPropertyChanged(nameof(LyricsOffsetText));
        OnPropertyChanged(nameof(HasNonZeroLyricsOffset));
        UpdateLyricActiveLineAndScheduleTimer();
    }

    public void BeginSeek()
    {
        _isSeeking = true;
    }

    public async Task CommitSeekAsync()
    {
        if (_isDisposed || !_isSeeking)
        {
            return;
        }

        _isSeeking = false;
        if (!CanInteractWithProgress)
        {
            SeekValue = Math.Clamp(Position.TotalSeconds, 0, SeekMaximum);
            return;
        }

        var targetPosition = TimeSpan.FromSeconds(SeekValue);
        bool didSeek = await _musicSessionService.TrySeekAsync(_preferredSessionId, targetPosition);
        if (!didSeek)
        {
            App.Log("[MusicWidget] Seek request was rejected by the active media session.");
            await RefreshTimelineAsync();
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        _timelineExtrapolator.ApplyManualSeek(targetPosition, nowUtc);
        Position = targetPosition;
        UpdateLyricActiveLineAndScheduleTimer();
        await Task.Delay(240);
        if (!_isDisposed)
        {
            await RefreshTimelineAsync();
        }
    }
}
