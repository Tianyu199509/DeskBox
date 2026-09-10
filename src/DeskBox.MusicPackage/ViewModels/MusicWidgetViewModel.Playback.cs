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
                    var info = await _musicSessionService.GetCurrentSessionInfoAsync(_preferredSessionId);
                    if (_isDisposed)
                    {
                        return;
                    }

                    bool isStaleMediaRefresh = refreshGeneration is { } expectedGeneration &&
                        expectedGeneration != Volatile.Read(ref _mediaPropertiesPendingGeneration);
                    if (!isStaleMediaRefresh)
                    {
                        await ApplyInfoAsync(info, refreshGeneration);
                        refreshCompleted = true;
                    }
                }
                catch (Exception ex)
                {
                    if (_isDisposed)
                    {
                        return;
                    }

                    PackageLog.Write($"[MusicWidget] Refresh failed: {ex}");
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

        await RefreshAsync();
        await Task.Delay(180);
        await RefreshAsync();
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
                PackageLog.Write($"[MusicWidget] Playback mode request was rejected. requested={requestedMode}");
            }

            await RefreshAsync();
            await Task.Delay(180);
            await RefreshAsync();
        }
        finally
        {
            _isChangingPlaybackMode = false;
        }
    }

    public async Task RefreshVolumeAsync()
    {
        if (_isDisposed || _isRefreshingVolume)
        {
            return;
        }

        _isRefreshingVolume = true;
        try
        {
            var snapshot = await _musicVolumeService.GetVolumeAsync(_sourceAppUserModelId, SourceDisplayName);
            if (_isDisposed)
            {
                return;
            }

            SystemVolume = snapshot.SystemVolume;
            SessionVolume = snapshot.SessionVolume;
            HasSessionVolume = snapshot.HasSessionVolume;
        }
        catch (Exception ex)
        {
            PackageLog.Write($"[MusicWidget] Refresh volume failed: {ex.Message}");
            HasSessionVolume = false;
        }
        finally
        {
            _isRefreshingVolume = false;
        }
    }

    public async Task RefreshSystemVolumeAsync()
    {
        if (_isDisposed || _isRefreshingVolume)
        {
            return;
        }

        _isRefreshingVolume = true;
        try
        {
            double systemVolume = await _musicVolumeService.GetSystemMasterVolumeAsync();
            if (!_isDisposed)
            {
                SystemVolume = systemVolume;
            }
        }
        catch (Exception ex)
        {
            PackageLog.Write($"[MusicWidget] Refresh system volume failed: {ex.Message}");
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
                    PackageLog.Write("[MusicWidget] System volume request was rejected.");
                }
            }
        }
        catch (Exception ex)
        {
            PackageLog.Write($"[MusicWidget] Set system volume failed: {ex.Message}");
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

                bool didChange = await _musicVolumeService.TrySetSessionVolumeAsync(_sourceAppUserModelId, SourceDisplayName, requestedVolume);
                if (!didChange)
                {
                    PackageLog.Write("[MusicWidget] Session volume request was rejected.");
                    HasSessionVolume = false;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            PackageLog.Write($"[MusicWidget] Set session volume failed: {ex.Message}");
            HasSessionVolume = false;
        }
        finally
        {
            _isChangingSessionVolume = false;
        }
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
            PackageLog.Write("[MusicWidget] Seek request was rejected by the active media session.");
            await RefreshAsync();
            return;
        }

        Position = targetPosition;
        _lastSyncedPosition = targetPosition;
        _lastPositionSyncAt = DateTimeOffset.UtcNow;
        await Task.Delay(260);
        await RefreshAsync();
    }
}

