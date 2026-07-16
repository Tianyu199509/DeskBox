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

        if (_isWindowVisible)
        {
            _progressTimer?.Start();
        }
        UpdateMusicTimerDiagnostics();
    }

    public async Task RefreshAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        if (_isRefreshing)
        {
            _fullRefreshPending = true;
            return;
        }

        _isRefreshing = true;
        try
        {
            do
            {
                _fullRefreshPending = false;
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

                    await ApplyInfoAsync(info);
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
            }
            while (_fullRefreshPending && !_isDisposed);
        }
        finally
        {
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

        _preferredSessionId = SessionIds[index];
        await _musicSessionService.TrySetPreferredSessionAsync(_preferredSessionId);
        OnPropertyChanged(nameof(SelectedSessionIndex));
        await RefreshAsync();
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
        await RefreshAsync();
    }

    public async Task NextAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        await _musicSessionService.TryNextAsync(_preferredSessionId);
        await RefreshAsync();
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
            App.Log($"[MusicWidget] Refresh volume failed: {ex.Message}");
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
            App.Log($"[MusicWidget] Refresh system volume failed: {ex.Message}");
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

                bool didChange = await _musicVolumeService.TrySetSessionVolumeAsync(_sourceAppUserModelId, SourceDisplayName, requestedVolume);
                if (!didChange)
                {
                    App.Log("[MusicWidget] Session volume request was rejected.");
                    HasSessionVolume = false;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[MusicWidget] Set session volume failed: {ex.Message}");
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
            App.Log("[MusicWidget] Seek request was rejected by the active media session.");
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
