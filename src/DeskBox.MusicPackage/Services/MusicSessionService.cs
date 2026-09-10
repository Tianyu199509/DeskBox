using Windows.Media;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace DeskBox.MusicPackage.Services;

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

public sealed record MusicSessionOption(
    string SessionId,
    string SourceAppUserModelId,
    string SourceDisplayName,
    MusicPlaybackState PlaybackState,
    bool IsSystemCurrent);

public sealed record MusicTimelineSnapshot(
    TimeSpan Position,
    TimeSpan Duration);

public sealed record MusicPlaybackSnapshot(
    MusicPlaybackState PlaybackState,
    bool CanPlay,
    bool CanPause,
    bool CanGoPrevious,
    bool CanGoNext,
    bool CanSeek,
    bool CanChangeShuffle,
    bool CanChangeRepeat,
    MusicPlaybackMode PlaybackMode);

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
        return GetSessionOptions()
            .Select(option => option.SessionId)
            .ToArray();
    }

    public IReadOnlyList<MusicSessionOption> GetSessionOptions()
    {
        if (_isDisposed || _manager is null)
        {
            return [];
        }

        GlobalSystemMediaTransportControlsSession? systemCurrent = _manager.GetCurrentSession();
        var options = new List<MusicSessionOption>();
        foreach (SessionEntry entry in EnumerateSessionEntries())
        {
            try
            {
                var playbackInfo = entry.Session.GetPlaybackInfo();
                string sourceApp = entry.Session.SourceAppUserModelId ?? string.Empty;
                options.Add(new MusicSessionOption(
                    entry.SessionId,
                    sourceApp,
                    GetSourceDisplayName(sourceApp),
                    MapPlaybackState(playbackInfo.PlaybackStatus),
                    IsSameSession(entry.Session, systemCurrent)));
            }
            catch (Exception ex)
            {
                // A source can disappear between GetSessions() and reading its
                // state. Skip that stale entry; SessionsChanged will repopulate
                // the picker without turning an ordinary player exit into an
                // application error.
                PackageLog.Write($"[MusicSession] Skipped stale source: {ex.Message}");
            }
        }

        return options;
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

    public async Task<MusicTimelineSnapshot?> GetCurrentTimelineAsync(string? preferredSessionId = null)
    {
        await InitializeAsync();
        if (_isDisposed)
        {
            return null;
        }

        var session = ResolveSession(preferredSessionId);
        AttachCurrentSession(session);
        if (session is null)
        {
            return null;
        }

        var timeline = session.GetTimelineProperties();
        return new MusicTimelineSnapshot(
            timeline.Position,
            timeline.EndTime > timeline.StartTime
                ? timeline.EndTime - timeline.StartTime
                : TimeSpan.Zero);
    }

    public async Task<MusicPlaybackSnapshot?> GetCurrentPlaybackAsync(string? preferredSessionId = null)
    {
        await InitializeAsync();
        if (_isDisposed)
        {
            return null;
        }

        var session = ResolveSession(preferredSessionId);
        AttachCurrentSession(session);
        if (session is null)
        {
            return null;
        }

        var playbackInfo = session.GetPlaybackInfo();
        var controls = playbackInfo.Controls;
        return new MusicPlaybackSnapshot(
            MapPlaybackState(playbackInfo.PlaybackStatus),
            controls.IsPlayEnabled,
            controls.IsPauseEnabled,
            controls.IsPreviousEnabled,
            controls.IsNextEnabled,
            controls.IsPlaybackPositionEnabled,
            controls.IsShuffleEnabled,
            controls.IsRepeatEnabled,
            MapPlaybackMode(playbackInfo));
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
        try
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
        catch { return false; } // stale session mid-call (audit 21: SMTC transport)
    }

    public async Task<bool> TryPlayAsync(string? sessionId)
    {
        try
        {
            var session = await GetSessionAsync(sessionId);
            return session is not null && await session.TryPlayAsync();
        }
        catch { return false; }
    }

    public async Task<bool> TryPauseAsync(string? sessionId)
    {
        try
        {
            var session = await GetSessionAsync(sessionId);
            return session is not null && await session.TryPauseAsync();
        }
        catch { return false; }
    }

    public async Task<bool> TryPreviousAsync(string? sessionId)
    {
        try
        {
            var session = await GetSessionAsync(sessionId);
            return session is not null && await session.TrySkipPreviousAsync();
        }
        catch { return false; }
    }

    public async Task<bool> TryNextAsync(string? sessionId)
    {
        try
        {
            var session = await GetSessionAsync(sessionId);
            return session is not null && await session.TrySkipNextAsync();
        }
        catch { return false; }
    }

    public async Task<bool> TrySeekAsync(string? sessionId, TimeSpan position)
    {
        try
        {
            var session = await GetSessionAsync(sessionId);
            return session is not null && await session.TryChangePlaybackPositionAsync((long)position.TotalMilliseconds * 10_000);
        }
        catch { return false; }
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
        return EnumerateSessionEntries()
            .FirstOrDefault(entry => string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal))
            ?.Session;
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
            PackageLog.Write($"[MusicSession] Failed to read media properties: {ex.Message}");
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

    private string GetSessionId(GlobalSystemMediaTransportControlsSession session)
    {
        IReadOnlyList<SessionEntry> entries = EnumerateSessionEntries();
        SessionEntry? identityMatch = entries.FirstOrDefault(entry => IsSameSession(entry.Session, session));
        if (identityMatch is not null)
        {
            return identityMatch.SessionId;
        }

        string sourceApp = session.SourceAppUserModelId ?? string.Empty;
        SessionEntry[] sourceMatches = entries
            .Where(entry => string.Equals(
                entry.Session.SourceAppUserModelId,
                sourceApp,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return sourceMatches.Length == 1
            ? sourceMatches[0].SessionId
            : CreateSessionId(sourceApp, 0);
    }

    internal static string CreateSessionId(string sourceAppUserModelId, int sourceOrdinal)
    {
        return string.Concat(
            sourceAppUserModelId,
            "\u001F",
            Math.Max(0, sourceOrdinal).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    internal static string GetSourceDisplayName(string sourceAppUserModelId)
    {
        if (string.IsNullOrWhiteSpace(sourceAppUserModelId))
        {
            return string.Empty;
        }

        string normalized = sourceAppUserModelId.Trim();
        string lower = normalized.ToLowerInvariant();
        if (lower.Contains("qqmusic", StringComparison.Ordinal))
        {
            return "QQ音乐";
        }

        if (lower.Contains("cloudmusic", StringComparison.Ordinal) ||
            lower.Contains("netease", StringComparison.Ordinal))
        {
            return "网易云音乐";
        }

        if (lower.Contains("msedge", StringComparison.Ordinal))
        {
            return "Microsoft Edge";
        }

        if (lower.Contains("chrome", StringComparison.Ordinal))
        {
            return "Google Chrome";
        }

        if (lower.Contains("firefox", StringComparison.Ordinal))
        {
            return "Mozilla Firefox";
        }

        if (lower.Contains("spotify", StringComparison.Ordinal))
        {
            return "Spotify";
        }

        if (lower.Contains("zunemusic", StringComparison.Ordinal) ||
            lower.Contains("media.player", StringComparison.Ordinal))
        {
            return "Windows Media Player";
        }

        string firstSegment = normalized.Split('!')[0];
        if (firstSegment.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(firstSegment);
        }

        string[] dottedParts = firstSegment.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return dottedParts.Length > 0 ? dottedParts[^1] : firstSegment;
    }

    internal static IReadOnlyList<string> DisambiguateSourceDisplayNames(
        IReadOnlyList<string> displayNames)
    {
        var totals = displayNames
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var ordinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new string[displayNames.Count];

        for (int index = 0; index < displayNames.Count; index++)
        {
            string displayName = displayNames[index];
            if (totals[displayName] <= 1)
            {
                result[index] = displayName;
                continue;
            }

            int ordinal = ordinals.TryGetValue(displayName, out int current) ? current + 1 : 1;
            ordinals[displayName] = ordinal;
            result[index] = $"{displayName} ({ordinal})";
        }

        return result;
    }

    private IReadOnlyList<SessionEntry> EnumerateSessionEntries()
    {
        if (_manager is null)
        {
            return [];
        }

        var sourceOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var entries = new List<SessionEntry>();
        foreach (GlobalSystemMediaTransportControlsSession session in _manager.GetSessions())
        {
            string sourceApp = session.SourceAppUserModelId ?? string.Empty;
            int ordinal = sourceOrdinals.TryGetValue(sourceApp, out int nextOrdinal)
                ? nextOrdinal
                : 0;
            sourceOrdinals[sourceApp] = ordinal + 1;
            entries.Add(new SessionEntry(CreateSessionId(sourceApp, ordinal), session));
        }

        return entries;
    }

    private static bool IsSameSession(
        GlobalSystemMediaTransportControlsSession? left,
        GlobalSystemMediaTransportControlsSession? right)
    {
        return left is not null &&
            right is not null &&
            (ReferenceEquals(left, right) || left.Equals(right));
    }

    private sealed record SessionEntry(
        string SessionId,
        GlobalSystemMediaTransportControlsSession Session);

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


