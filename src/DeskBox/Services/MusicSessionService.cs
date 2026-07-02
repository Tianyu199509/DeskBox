using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DeskBox.Services;

public enum MusicPlaybackState
{
    Unknown,
    Stopped,
    Playing,
    Paused
}

public enum MusicPlaybackMode
{
    Normal,
    Shuffle,
    Repeat
}

public sealed record MusicSessionInfo(
    string SessionId,
    string SourceAppUserModelId,
    string SourceDisplayName,
    string Title,
    string Artist,
    string Album,
    MusicPlaybackState PlaybackState,
    TimeSpan Position,
    TimeSpan Duration,
    bool CanPlay,
    bool CanPause,
    bool CanGoPrevious,
    bool CanGoNext,
    bool CanSeek,
    bool CanChangeShuffle,
    bool CanChangeRepeat,
    MusicPlaybackMode PlaybackMode,
    IRandomAccessStreamReference? Thumbnail);

public sealed class MusicSessionService : IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private bool _isInitialized;
    private bool _isDisposed;

    public event EventHandler? SessionsChanged;
    public event EventHandler? CurrentSessionChanged;
    public event EventHandler? PlaybackInfoChanged;
    public event EventHandler? MediaPropertiesChanged;
    public event EventHandler? TimelinePropertiesChanged;

    public async Task InitializeAsync()
    {
        if (_isDisposed || _isInitialized)
        {
            return;
        }

        var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        if (_isDisposed)
        {
            return;
        }

        _manager = manager;
        _manager.SessionsChanged += Manager_SessionsChanged;
        _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
        AttachCurrentSession(_manager.GetCurrentSession());
        _isInitialized = true;
    }

    public IReadOnlyList<string> GetSessionIds()
    {
        if (_isDisposed || _manager is null)
        {
            return [];
        }

        return _manager.GetSessions()
            .Select(GetSessionId)
            .ToArray();
    }

    public async Task<MusicSessionInfo?> GetCurrentSessionInfoAsync(string? preferredSessionId = null)
    {
        await InitializeAsync();
        if (_isDisposed)
        {
            return null;
        }

        var session = ResolveSession(preferredSessionId);
        AttachCurrentSession(session);

        return session is null
            ? null
            : await CreateInfoAsync(session);
    }

    public async Task<bool> TrySetPreferredSessionAsync(string sessionId)
    {
        await InitializeAsync();
        if (_isDisposed)
        {
            return false;
        }

        var session = FindSession(sessionId);
        if (session is null)
        {
            return false;
        }

        AttachCurrentSession(session);
        return true;
    }

    public async Task<bool> TryTogglePlayPauseAsync(string? sessionId)
    {
        var session = await GetSessionAsync(sessionId);
        if (session is null)
        {
            return false;
        }

        var playbackInfo = session.GetPlaybackInfo();
        return playbackInfo.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
            ? await session.TryPauseAsync()
            : await session.TryPlayAsync();
    }

    public async Task<bool> TryPlayAsync(string? sessionId)
    {
        var session = await GetSessionAsync(sessionId);
        return session is not null && await session.TryPlayAsync();
    }

    public async Task<bool> TryPauseAsync(string? sessionId)
    {
        var session = await GetSessionAsync(sessionId);
        return session is not null && await session.TryPauseAsync();
    }

    public async Task<bool> TryPreviousAsync(string? sessionId)
    {
        var session = await GetSessionAsync(sessionId);
        return session is not null && await session.TrySkipPreviousAsync();
    }

    public async Task<bool> TryNextAsync(string? sessionId)
    {
        var session = await GetSessionAsync(sessionId);
        return session is not null && await session.TrySkipNextAsync();
    }

    public async Task<bool> TrySeekAsync(string? sessionId, TimeSpan position)
    {
        var session = await GetSessionAsync(sessionId);
        return session is not null && await session.TryChangePlaybackPositionAsync((long)position.TotalMilliseconds * 10_000);
    }

    public async Task<bool> TryChangePlaybackModeAsync(string? sessionId, MusicPlaybackMode playbackMode)
    {
        var session = await GetSessionAsync(sessionId);
        if (session is null)
        {
            return false;
        }

        var playbackInfo = session.GetPlaybackInfo();
        var controls = playbackInfo.Controls;
        bool didChange = false;

        switch (playbackMode)
        {
            case MusicPlaybackMode.Shuffle:
                if (!controls.IsShuffleEnabled)
                {
                    return false;
                }

                if (controls.IsRepeatEnabled && playbackInfo.AutoRepeatMode != MediaPlaybackAutoRepeatMode.None)
                {
                    didChange |= await session.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode.None);
                }

                didChange |= await session.TryChangeShuffleActiveAsync(true);
                return didChange;

            case MusicPlaybackMode.Repeat:
                if (!controls.IsRepeatEnabled)
                {
                    return false;
                }

                if (controls.IsShuffleEnabled && playbackInfo.IsShuffleActive == true)
                {
                    didChange |= await session.TryChangeShuffleActiveAsync(false);
                }

                didChange |= await session.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode.List);
                return didChange;

            default:
                if (controls.IsShuffleEnabled && playbackInfo.IsShuffleActive == true)
                {
                    didChange |= await session.TryChangeShuffleActiveAsync(false);
                }

                if (controls.IsRepeatEnabled && playbackInfo.AutoRepeatMode != MediaPlaybackAutoRepeatMode.None)
                {
                    didChange |= await session.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode.None);
                }

                return didChange;
        }
    }

    public void Dispose()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        if (_manager is not null)
        {
            _manager.SessionsChanged -= Manager_SessionsChanged;
            _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
        }

        DetachSession(_currentSession);
        _manager = null;
        _currentSession = null;
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> GetSessionAsync(string? sessionId)
    {
        await InitializeAsync();
        if (_isDisposed)
        {
            return null;
        }

        return ResolveSession(sessionId);
    }

    private GlobalSystemMediaTransportControlsSession? ResolveSession(string? preferredSessionId)
    {
        if (!string.IsNullOrWhiteSpace(preferredSessionId))
        {
            var preferred = FindSession(preferredSessionId);
            if (preferred is not null)
            {
                return preferred;
            }
        }

        return _manager?.GetCurrentSession() ?? _manager?.GetSessions().FirstOrDefault();
    }

    private GlobalSystemMediaTransportControlsSession? FindSession(string sessionId)
    {
        return _manager?.GetSessions()
            .FirstOrDefault(session => string.Equals(GetSessionId(session), sessionId, StringComparison.Ordinal));
    }

    private async Task<MusicSessionInfo> CreateInfoAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var mediaProperties = await TryGetMediaPropertiesAsync(session);
        var playbackInfo = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var controls = playbackInfo.Controls;

        string sourceApp = session.SourceAppUserModelId ?? string.Empty;
        string title = mediaProperties?.Title ?? string.Empty;
        string artist = mediaProperties?.Artist ?? string.Empty;
        string album = mediaProperties?.AlbumTitle ?? string.Empty;

        return new MusicSessionInfo(
            GetSessionId(session),
            sourceApp,
            GetSourceDisplayName(sourceApp),
            title,
            artist,
            album,
            MapPlaybackState(playbackInfo.PlaybackStatus),
            timeline.Position,
            timeline.EndTime > timeline.StartTime ? timeline.EndTime - timeline.StartTime : TimeSpan.Zero,
            controls.IsPlayEnabled,
            controls.IsPauseEnabled,
            controls.IsPreviousEnabled,
            controls.IsNextEnabled,
            controls.IsPlaybackPositionEnabled,
            controls.IsShuffleEnabled,
            controls.IsRepeatEnabled,
            MapPlaybackMode(playbackInfo),
            mediaProperties?.Thumbnail);
    }

    private static async Task<GlobalSystemMediaTransportControlsSessionMediaProperties?> TryGetMediaPropertiesAsync(
        GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return await session.TryGetMediaPropertiesAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[MusicSession] Failed to read media properties: {ex.Message}");
            return null;
        }
    }

    private void AttachCurrentSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(_currentSession, session))
        {
            return;
        }

        DetachSession(_currentSession);
        _currentSession = session;

        if (_currentSession is not null)
        {
            _currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            _currentSession.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
        }
    }

    private void DetachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null)
        {
            return;
        }

        session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
        session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
        session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
    }

    private void Manager_SessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        if (_isDisposed)
        {
            return;
        }

        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Manager_CurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        if (_isDisposed)
        {
            return;
        }

        AttachCurrentSession(sender.GetCurrentSession());
        CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Session_PlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        if (_isDisposed)
        {
            return;
        }

        PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Session_MediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        MediaPropertiesChangedEventArgs args)
    {
        if (_isDisposed)
        {
            return;
        }

        MediaPropertiesChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Session_TimelinePropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender,
        TimelinePropertiesChangedEventArgs args)
    {
        if (_isDisposed)
        {
            return;
        }

        TimelinePropertiesChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string GetSessionId(GlobalSystemMediaTransportControlsSession session)
    {
        return session.SourceAppUserModelId ?? string.Empty;
    }

    private static string GetSourceDisplayName(string sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return string.Empty;
        }

        var firstSegment = sourceAppUserModelId.Split('!')[0];
        var dottedParts = firstSegment.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return dottedParts.Length > 0 ? dottedParts[^1] : firstSegment;
    }

    private static MusicPlaybackState MapPlaybackState(GlobalSystemMediaTransportControlsSessionPlaybackStatus status)
    {
        return status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MusicPlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MusicPlaybackState.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MusicPlaybackState.Stopped,
            _ => MusicPlaybackState.Unknown
        };
    }

    private static MusicPlaybackMode MapPlaybackMode(GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo)
    {
        if (playbackInfo.IsShuffleActive == true)
        {
            return MusicPlaybackMode.Shuffle;
        }

        return playbackInfo.AutoRepeatMode == MediaPlaybackAutoRepeatMode.None
            ? MusicPlaybackMode.Normal
            : MusicPlaybackMode.Repeat;
    }
}
