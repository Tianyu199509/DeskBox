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

    public Task RefreshAsync(int? mediaPropertiesGeneration = null)
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        if (_isRefreshing && _activeRefreshTask is { IsCompleted: false } inFlightTask)
        {
            _pendingFullRefreshMediaGeneration = _fullRefreshPending
                ? MergePendingMediaGeneration(_pendingFullRefreshMediaGeneration, mediaPropertiesGeneration)
                : mediaPropertiesGeneration;
            _fullRefreshPending = true;
            return inFlightTask;
        }

        Task refreshTask = RunRefreshLoopAsync(mediaPropertiesGeneration);
        if (!refreshTask.IsCompleted)
        {
            _activeRefreshTask = refreshTask;
        }

        return refreshTask;
    }

    private async Task RunRefreshLoopAsync(int? mediaPropertiesGeneration)
    {
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
                    else if (!CanRunWindowMediaRefresh())
                    {
                        Interlocked.Exchange(ref _hiddenMediaStateDirty, 1);
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
                DateTimeOffset completedUtc = DateTimeOffset.UtcNow;
                _lastLiveReconcileUtc = completedUtc;
                _lastLiveTimelineProbeUtc = completedUtc;
                _ = ConsumeMemoryEpochChanges();
                Interlocked.Exchange(
                    ref _lastFullRefreshCompletedUtcTicks,
                    completedUtc.UtcDateTime.Ticks);
            }

            _isRefreshing = false;
            _activeRefreshTask = null;
        }
    }

    private static int? MergePendingMediaGeneration(int? existing, int? incoming) =>
        existing is null || incoming is null
            ? null
            : Math.Max(existing.Value, incoming.Value);

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

        ++_playPauseCommandGeneration;
        ClearPendingPlayPauseIntent();

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

        int commandGeneration = ++_playPauseCommandGeneration;
        bool wantPlay = !IsPlaying;
        MusicPlaybackState targetState = wantPlay
            ? MusicPlaybackState.Playing
            : MusicPlaybackState.Paused;

        bool accepted = wantPlay
            ? await _musicSessionService.TryPlayAsync(_preferredSessionId)
            : await _musicSessionService.TryPauseAsync(_preferredSessionId);
        if (_isDisposed || commandGeneration != _playPauseCommandGeneration)
        {
            return;
        }

        if (!accepted)
        {
            ClearPendingPlayPauseIntent();
            await RefreshAsync();
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        _pendingCommandTargetState = targetState;
        _pendingCommandTargetUntilUtc = nowUtc + PendingPlayPauseSettleWindow;

        TimeSpan anchoredPos = _timelineExtrapolator.ApplyPlaybackStateChange(
            targetState,
            playbackRate: 1.0,
            nowUtc);
        if (wantPlay)
        {
            CanPause = true;
        }
        else
        {
            CanPlay = true;
        }

        PlaybackState = targetState;
        if (ShouldExposeTimelinePosition() && !_isSeeking)
        {
            Position = anchoredPos;
        }

        await RefreshPlaybackAsync(isUserInitiated: true);
        await RefreshTimelineAsync(isUserInitiated: true);

        await Task.Delay(PostCommandVerificationDelayMs);
        if (_isDisposed || commandGeneration != _playPauseCommandGeneration)
        {
            return;
        }

        await RefreshPlaybackAsync(isUserInitiated: true);
        await RefreshTimelineAsync(isUserInitiated: true);

        if (_pendingCommandTargetState is not null)
        {
            await Task.Delay(PostCommandFinalReconcileDelayMs);
            if (_isDisposed || commandGeneration != _playPauseCommandGeneration)
            {
                return;
            }

            ClearPendingPlayPauseIntent();
            await RefreshPlaybackAsync(isUserInitiated: true);
            await RefreshTimelineAsync(isUserInitiated: true);
        }
    }

    public async Task PreviousAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        ++_playPauseCommandGeneration;
        ClearPendingPlayPauseIntent();
        await _musicSessionService.TryPreviousAsync(_preferredSessionId);
        ScheduleDebouncedMediaRefresh(isUserInitiated: true);
    }

    public async Task NextAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        ++_playPauseCommandGeneration;
        ClearPendingPlayPauseIntent();
        await _musicSessionService.TryNextAsync(_preferredSessionId);
        ScheduleDebouncedMediaRefresh(isUserInitiated: true);
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

            await RefreshPlaybackAsync(isUserInitiated: true);
            await Task.Delay(180);
            if (!_isDisposed)
            {
                await RefreshPlaybackAsync(isUserInitiated: true);
            }
        }
        finally
        {
            _isChangingPlaybackMode = false;
        }
    }

    public Task RefreshSystemVolumeAsync() => RefreshVolumeAsync();

    internal bool HasReadSystemVolume => _hasReadSystemVolume;

    public Task<bool> RefreshVolumeAsync()
    {
        if (_isDisposed)
        {
            return Task.FromResult(false);
        }

        if (_activeVolumeRefreshTask is { IsCompleted: false } activeTask)
        {
            return activeTask;
        }

        Task<bool> refreshTask = RefreshVolumeCoreAsync();
        if (!refreshTask.IsCompleted)
        {
            _activeVolumeRefreshTask = refreshTask;
        }

        return refreshTask;
    }

    private async Task<bool> RefreshVolumeCoreAsync()
    {
        try
        {
            MusicVolumeSnapshot snapshot = await _volumeController.GetVolumeAsync(
                _sourceAppUserModelId,
                _sourceDisplayName);

            if (_isDisposed)
            {
                return false;
            }

            if (snapshot.HasSystemVolume)
            {
                SystemVolume = snapshot.SystemVolume;
                _hasReadSystemVolume = true;
            }

            HasSessionVolume = snapshot.HasSessionVolume;
            if (snapshot.HasSessionVolume)
            {
                SessionVolume = snapshot.SessionVolume;
            }

            return snapshot.HasSystemVolume;
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Refresh volume failed: {ex.Message}");
            return false;
        }
        finally
        {
            _activeVolumeRefreshTask = null;
        }
    }

    public async Task SetSystemVolumeAsync(double volume)
    {
        if (_isDisposed)
        {
            return;
        }

        SystemVolume = volume;
        _hasReadSystemVolume = true;
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

                bool didChange = await _volumeController.TrySetSystemMasterVolumeAsync(requestedVolume);
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

                bool didChange = await _volumeController.TrySetSessionVolumeAsync(
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

    public Task ToggleSystemMuteAsync()
    {
        if (_isDisposed)
        {
            return Task.CompletedTask;
        }

        if (SystemVolume <= 0.005)
        {
            double restore = _preMuteSystemVolume > 0.01 ? _preMuteSystemVolume : 0.35;
            return SetSystemVolumeAsync(restore);
        }

        _preMuteSystemVolume = SystemVolume;
        return SetSystemVolumeAsync(0.0);
    }

    public Task ToggleSessionMuteAsync()
    {
        if (_isDisposed || !HasSessionVolume)
        {
            return Task.CompletedTask;
        }

        if (SessionVolume <= 0.005)
        {
            double restore = _preMuteSessionVolume > 0.01 ? _preMuteSessionVolume : 0.8;
            return SetSessionVolumeAsync(restore);
        }

        _preMuteSessionVolume = SessionVolume;
        return SetSessionVolumeAsync(0.0);
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
            await RefreshTimelineAsync(isUserInitiated: true);
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        _timelineExtrapolator.ApplyManualSeek(targetPosition, nowUtc);
        Position = targetPosition;
        UpdateLyricActiveLineAndScheduleTimer();
        await Task.Delay(240);
        if (!_isDisposed)
        {
            await RefreshTimelineAsync(isUserInitiated: true);
        }
    }
}
