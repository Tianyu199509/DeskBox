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
    Repeat,
    RepeatOne
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
    DateTimeOffset PositionUpdatedUtc,
    double PlaybackRate,
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
    TimeSpan Duration,
    DateTimeOffset PositionUpdatedUtc,
    double PlaybackRate,
    MusicPlaybackState PlaybackState);

public sealed record MusicPlaybackSnapshot(
    MusicPlaybackState PlaybackState,
    double PlaybackRate,
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
    private static readonly object s_sharedManagerGate = new();
    private static GlobalSystemMediaTransportControlsSessionManager? s_sharedManager;
    private static Task<GlobalSystemMediaTransportControlsSessionManager>? s_sharedManagerRequestTask;
    private static int s_sharedManagerRefCount;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private string? _currentSessionId;
    private string? _preferredSessionId;
    private bool _holdsSharedManagerReference;
    private bool _isInitialized;
    private bool _isDisposed;

    internal static int SharedManagerReferenceCount
    {
        get
        {
            lock (s_sharedManagerGate)
            {
                return s_sharedManagerRefCount;
            }
        }
    }

    private static async Task<GlobalSystemMediaTransportControlsSessionManager> AcquireSharedManagerAsync()
    {
        Task<GlobalSystemMediaTransportControlsSessionManager> requestTask;
        lock (s_sharedManagerGate)
        {
            s_sharedManagerRefCount++;
            if (s_sharedManager is not null)
            {
                return s_sharedManager;
            }

            s_sharedManagerRequestTask ??= GlobalSystemMediaTransportControlsSessionManager
                .RequestAsync()
                .AsTask();
            requestTask = s_sharedManagerRequestTask;
        }

        try
        {
            var resolved = await requestTask;
            lock (s_sharedManagerGate)
            {
                if (s_sharedManagerRefCount > 0)
                {
                    s_sharedManager = resolved;
                }
                else
                {
                    s_sharedManager = null;
                    s_sharedManagerRequestTask = null;
                }
            }

            return resolved;
        }
        catch
        {
            lock (s_sharedManagerGate)
            {
                s_sharedManagerRefCount = Math.Max(0, s_sharedManagerRefCount - 1);
                if (ReferenceEquals(s_sharedManagerRequestTask, requestTask))
                {
                    s_sharedManagerRequestTask = null;
                }
            }

            throw;
        }
    }

    private static void ReleaseSharedManager()
    {
        lock (s_sharedManagerGate)
        {
            if (s_sharedManagerRefCount <= 0)
            {
                return;
            }

            s_sharedManagerRefCount--;
            if (s_sharedManagerRefCount == 0)
            {
                s_sharedManager = null;
                s_sharedManagerRequestTask = null;
            }
        }
    }

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

        var manager = await AcquireSharedManagerAsync();
        if (_isDisposed)
        {
            ReleaseSharedManager();
            return;
        }

        _holdsSharedManagerReference = true;
        _manager = manager;
        _manager.SessionsChanged += Manager_SessionsChanged;
        _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
        AttachCurrentSession(_manager.GetCurrentSession());
        _isInitialized = true;
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
                App.LogVerbose($"[MusicSession] Skipped stale source: {ex.Message}");
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

        var (session, resolvedId) = ResolveSession(preferredSessionId);
        AttachCurrentSession(session, resolvedId);
        if (session is null)
        {
            return null;
        }

        try
        {
            return await CreateInfoAsync(session, resolvedId);
        }
        catch (Exception ex)
        {
            // The full-info read races the player's exit on its synchronous
            // halves too; callers treat null as "no update" and the next
            // refresh or SessionsChanged reattaches — same contract as the
            // timeline and playback snapshot reads.
            InvalidateCurrentSessionIfMatch(session);
            App.LogVerbose($"[MusicSession] Skipped stale session info read: {ex.Message}");
            return null;
        }
    }

    public async Task<MusicTimelineSnapshot?> GetCurrentTimelineAsync(string? preferredSessionId = null)
    {
        await InitializeAsync();
        if (_isDisposed)
        {
            return null;
        }

        var (session, resolvedId) = ResolveSession(preferredSessionId);
        AttachCurrentSession(session, resolvedId);
        if (session is null)
        {
            return null;
        }

        try
        {
            var timeline = session.GetTimelineProperties();
            var playbackInfo = session.GetPlaybackInfo();
            return new MusicTimelineSnapshot(
                ExtractRawPosition(timeline),
                ExtractDuration(timeline),
                timeline.LastUpdatedTime,
                ExtractPlaybackRate(playbackInfo),
                MapPlaybackState(playbackInfo.PlaybackStatus));
        }
        catch (Exception ex)
        {
            // The session wrapper can go stale between resolution and this
            // read (player exit, per-track session churn); callers treat null
            // as "no update" and the next full refresh or SessionsChanged
            // reattaches. Throwing here turned into the recurring timeline
            // NRE spam seen in feedback diagnostics.
            InvalidateCurrentSessionIfMatch(session);
            App.LogVerbose($"[MusicSession] Skipped stale timeline read: {ex.Message}");
            return null;
        }
    }

    public async Task<MusicPlaybackSnapshot?> GetCurrentPlaybackAsync(string? preferredSessionId = null)
    {
        await InitializeAsync();
        if (_isDisposed)
        {
            return null;
        }

        var (session, resolvedId) = ResolveSession(preferredSessionId);
        AttachCurrentSession(session, resolvedId);
        if (session is null)
        {
            return null;
        }

        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            var controls = playbackInfo.Controls;
            return new MusicPlaybackSnapshot(
                MapPlaybackState(playbackInfo.PlaybackStatus),
                ExtractPlaybackRate(playbackInfo),
                controls.IsPlayEnabled,
                controls.IsPauseEnabled,
                controls.IsPreviousEnabled,
                controls.IsNextEnabled,
                controls.IsPlaybackPositionEnabled,
                controls.IsShuffleEnabled,
                controls.IsRepeatEnabled,
                MapPlaybackMode(playbackInfo));
        }
        catch (Exception ex)
        {
            // Same stale-session window as the timeline read above.
            InvalidateCurrentSessionIfMatch(session);
            App.LogVerbose($"[MusicSession] Skipped stale playback read: {ex.Message}");
            return null;
        }
    }

    public async Task<bool> TrySetPreferredSessionAsync(string sessionId)
    {
        await InitializeAsync();
        if (_isDisposed)
        {
            return false;
        }

        var entry = FindSessionEntry(sessionId);
        if (entry is null)
        {
            _preferredSessionId = null;
            return false;
        }

        _preferredSessionId = entry.SessionId;
        AttachCurrentSession(entry.Session, entry.SessionId);
        return true;
    }

    public async Task<bool> TryPlayAsync(string? sessionId)
    {
        return await TryControlAsync(sessionId, "play", session => session.TryPlayAsync());
    }

    public async Task<bool> TryPauseAsync(string? sessionId)
    {
        return await TryControlAsync(sessionId, "pause", session => session.TryPauseAsync());
    }

    public async Task<bool> TryPreviousAsync(string? sessionId)
    {
        return await TryControlAsync(sessionId, "previous", session => session.TrySkipPreviousAsync());
    }

    public async Task<bool> TryNextAsync(string? sessionId)
    {
        return await TryControlAsync(sessionId, "next", session => session.TrySkipNextAsync());
    }

    public async Task<bool> TrySeekAsync(string? sessionId, TimeSpan position)
    {
        long positionHundredNanoseconds = (long)position.TotalMilliseconds * 10_000;
        return await TryControlAsync(
            sessionId,
            "seek",
            session => session.TryChangePlaybackPositionAsync(positionHundredNanoseconds));
    }

    /// <summary>
    /// Media commands target a session whose backing player can vanish at
    /// any moment; the resulting COMException (0x800706BA) surfaced through
    /// async-void click handlers as recurring unhandled exceptions in
    /// feedback diagnostics. Reads already degraded silently for the same
    /// reason — controls now do too.
    /// </summary>
    private async Task<bool> TryControlAsync(
        string? sessionId,
        string operation,
        Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> control)
    {
        // Resolution itself can race the player's exit, so it shares the
        // guard instead of running in GetSessionAsync.
        try
        {
            var session = await GetSessionAsync(sessionId);
            return session is not null && await control(session);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MusicSession] Skipped stale control '{operation}': {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TryChangePlaybackModeAsync(string? sessionId, MusicPlaybackMode playbackMode)
    {
        try
        {
            var session = await GetSessionAsync(sessionId);
            return session is not null && await ApplyPlaybackModeAsync(session, playbackMode);
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MusicSession] Skipped stale control 'mode': {ex.Message}");
            return false;
        }
    }

    private static async Task<bool> ApplyPlaybackModeAsync(
        GlobalSystemMediaTransportControlsSession session,
        MusicPlaybackMode playbackMode)
    {

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

            case MusicPlaybackMode.RepeatOne:
                if (!controls.IsRepeatEnabled)
                {
                    return false;
                }

                if (controls.IsShuffleEnabled && playbackInfo.IsShuffleActive == true)
                {
                    didChange |= await session.TryChangeShuffleActiveAsync(false);
                }

                didChange |= await session.TryChangeAutoRepeatModeAsync(MediaPlaybackAutoRepeatMode.Track);
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
        _currentSessionId = null;
        _preferredSessionId = null;
        if (_holdsSharedManagerReference)
        {
            _holdsSharedManagerReference = false;
            ReleaseSharedManager();
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> GetSessionAsync(string? sessionId)
    {
        await InitializeAsync();
        if (_isDisposed)
        {
            return null;
        }

        return ResolveSession(sessionId).Session;
    }

    private (GlobalSystemMediaTransportControlsSession? Session, string? SessionId) ResolveSession(string? preferredSessionId)
    {
        if (!string.IsNullOrWhiteSpace(preferredSessionId))
        {
            _preferredSessionId = preferredSessionId;
            if (_currentSession is not null &&
                string.Equals(_currentSessionId, preferredSessionId, StringComparison.Ordinal))
            {
                return (_currentSession, _currentSessionId);
            }

            var preferred = FindSessionEntry(preferredSessionId);
            if (preferred is not null)
            {
                return (preferred.Session, preferred.SessionId);
            }

            _preferredSessionId = null;
        }
        else
        {
            _preferredSessionId = null;
        }

        var systemSession = _manager?.GetCurrentSession() ?? _manager?.GetSessions().FirstOrDefault();
        if (systemSession is not null && IsSameSession(_currentSession, systemSession))
        {
            return (_currentSession, _currentSessionId);
        }

        return (systemSession, null);
    }

    private SessionEntry? FindSessionEntry(string sessionId)
    {
        return EnumerateSessionEntries()
            .FirstOrDefault(entry => string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal));
    }

    private async Task<MusicSessionInfo> CreateInfoAsync(
        GlobalSystemMediaTransportControlsSession session,
        string? knownSessionId = null)
    {
        var mediaProperties = await TryGetMediaPropertiesAsync(session);
        var playbackInfo = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var controls = playbackInfo.Controls;

        string sourceApp = session.SourceAppUserModelId ?? string.Empty;
        string title = mediaProperties?.Title ?? string.Empty;
        string artist = mediaProperties?.Artist ?? string.Empty;
        string album = mediaProperties?.AlbumTitle ?? string.Empty;
        string sessionId = !string.IsNullOrWhiteSpace(knownSessionId)
            ? knownSessionId
            : GetSessionId(session);
        if (IsSameSession(_currentSession, session))
        {
            _currentSessionId = sessionId;
        }

        return new MusicSessionInfo(
            sessionId,
            sourceApp,
            GetSourceDisplayName(sourceApp),
            title,
            artist,
            album,
            MapPlaybackState(playbackInfo.PlaybackStatus),
            ExtractRawPosition(timeline),
            ExtractDuration(timeline),
            timeline.LastUpdatedTime,
            ExtractPlaybackRate(playbackInfo),
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

    private static TimeSpan ExtractRawPosition(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
    {
        TimeSpan raw = timeline.StartTime > TimeSpan.Zero && timeline.Position >= timeline.StartTime
            ? timeline.Position - timeline.StartTime
            : timeline.Position;
        return raw < TimeSpan.Zero ? TimeSpan.Zero : raw;
    }

    private static TimeSpan ExtractDuration(
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline) =>
        timeline.EndTime > timeline.StartTime
            ? timeline.EndTime - timeline.StartTime
            : TimeSpan.Zero;

    private static double ExtractPlaybackRate(
        GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo) =>
        playbackInfo.PlaybackRate is { } rate &&
        double.IsFinite(rate) &&
        rate is >= 0.05 and <= 4.0
            ? rate
            : 1.0;

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

    private void AttachCurrentSession(
        GlobalSystemMediaTransportControlsSession? session,
        string? resolvedSessionId = null)
    {
        if (session is null && _currentSession is null)
        {
            _currentSessionId = null;
            return;
        }

        if (IsSameSession(_currentSession, session))
        {
            if (!string.IsNullOrWhiteSpace(resolvedSessionId))
            {
                _currentSessionId = resolvedSessionId;
            }

            return;
        }

        DetachSession(_currentSession);
        _currentSession = session;
        _currentSessionId = resolvedSessionId;

        if (_currentSession is not null)
        {
            _currentSession.PlaybackInfoChanged += Session_PlaybackInfoChanged;
            _currentSession.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            _currentSession.TimelinePropertiesChanged += Session_TimelinePropertiesChanged;
        }
    }

    private void InvalidateCurrentSessionIfMatch(GlobalSystemMediaTransportControlsSession session)
    {
        if (IsSameSession(_currentSession, session))
        {
            DetachSession(_currentSession);
            _currentSession = null;
            _currentSessionId = null;
        }
    }

    private void DetachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
            session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            session.TimelinePropertiesChanged -= Session_TimelinePropertiesChanged;
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MusicSession] Skipped stale session detach: {ex.Message}");
        }
    }

    private void Manager_SessionsChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        SessionsChangedEventArgs args)
    {
        if (_isDisposed)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_preferredSessionId) ||
            !string.Equals(_currentSessionId, _preferredSessionId, StringComparison.Ordinal) ||
            !IsPreferredSessionPresent(sender, _preferredSessionId))
        {
            _currentSessionId = null;
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

        if (!string.IsNullOrWhiteSpace(_preferredSessionId) &&
            string.Equals(_currentSessionId, _preferredSessionId, StringComparison.Ordinal) &&
            IsPreferredSessionPresent(sender, _preferredSessionId))
        {
            return;
        }

        AttachCurrentSession(sender.GetCurrentSession());
        CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsPreferredSessionPresent(
        GlobalSystemMediaTransportControlsSessionManager manager,
        string preferredSessionId)
    {
        try
        {
            var sourceOrdinals = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (GlobalSystemMediaTransportControlsSession session in manager.GetSessions())
            {
                string sourceApp = session.SourceAppUserModelId ?? string.Empty;
                int ordinal = sourceOrdinals.TryGetValue(sourceApp, out int nextOrdinal)
                    ? nextOrdinal
                    : 0;
                sourceOrdinals[sourceApp] = ordinal + 1;
                if (string.Equals(CreateSessionId(sourceApp, ordinal), preferredSessionId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MusicSession] Skipped preferred session check: {ex.Message}");
        }

        return false;
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

    internal static MusicPlaybackMode MapPlaybackMode(
        bool isShuffleActive,
        MediaPlaybackAutoRepeatMode? autoRepeatMode)
    {
        if (isShuffleActive)
        {
            return MusicPlaybackMode.Shuffle;
        }

        return autoRepeatMode switch
        {
            MediaPlaybackAutoRepeatMode.Track => MusicPlaybackMode.RepeatOne,
            MediaPlaybackAutoRepeatMode.List => MusicPlaybackMode.Repeat,
            _ => MusicPlaybackMode.Normal
        };
    }

    private static MusicPlaybackMode MapPlaybackMode(GlobalSystemMediaTransportControlsSessionPlaybackInfo playbackInfo) =>
        MapPlaybackMode(playbackInfo.IsShuffleActive == true, playbackInfo.AutoRepeatMode);
}
