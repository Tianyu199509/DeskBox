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

internal readonly record struct SystemSessionCandidate(
    MusicPlaybackState State,
    bool IsClosed,
    bool IsSystemCurrent,
    bool IsPreviousCurrent);

internal interface IMusicSessionController : IDisposable
{
    event EventHandler? SessionsChanged;
    event EventHandler? CurrentSessionChanged;
    event EventHandler? PlaybackInfoChanged;
    event EventHandler? MediaPropertiesChanged;
    event EventHandler? TimelinePropertiesChanged;

    Task InitializeAsync();
    IReadOnlyList<MusicSessionOption> GetSessionOptions();
    Task<MusicSessionInfo?> GetCurrentSessionInfoAsync(string? preferredSessionId = null);
    Task<IRandomAccessStreamReference?> GetCurrentThumbnailAsync(string? preferredSessionId = null);
    Task<MusicTimelineSnapshot?> GetCurrentTimelineAsync(string? preferredSessionId = null);
    Task<MusicPlaybackSnapshot?> GetCurrentPlaybackAsync(string? preferredSessionId = null);
    Task<bool> TrySetPreferredSessionAsync(string sessionId);
    Task<bool> TryPlayAsync(string? sessionId);
    Task<bool> TryPauseAsync(string? sessionId);
    Task<bool> TryPreviousAsync(string? sessionId);
    Task<bool> TryNextAsync(string? sessionId);
    Task<bool> TrySeekAsync(string? sessionId, TimeSpan position);
    Task<bool> TryChangePlaybackModeAsync(string? sessionId, MusicPlaybackMode playbackMode);
}

public sealed class MusicSessionService : IMusicSessionController, IDisposable
{
    private static readonly object s_sharedManagerGate = new();
    private static GlobalSystemMediaTransportControlsSessionManager? s_sharedManager;
    private static Task<GlobalSystemMediaTransportControlsSessionManager>? s_sharedManagerRequestTask;
    private static int s_sharedManagerRefCount;

    private readonly object _sessionObservationGate = new();
    private readonly List<GlobalSystemMediaTransportControlsSession> _observedSessions = [];
    private readonly Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> _sessionsChangedHandler;
    private readonly Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> _currentSessionChangedHandler;
    private readonly Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _sessionPlaybackInfoChangedHandler;
    private readonly Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _sessionMediaPropertiesChangedHandler;
    private readonly Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> _sessionTimelinePropertiesChangedHandler;
    private readonly Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _observedPlaybackInfoChangedHandler;
    private readonly Action _memoryReclaimCompletedHandler;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private string? _currentSessionId;
    private string? _preferredSessionId;
    private bool _holdsSharedManagerReference;
    private bool _isInitialized;
    private bool _isDisposed;

    public MusicSessionService()
    {
        _sessionsChangedHandler = Manager_SessionsChanged;
        _currentSessionChangedHandler = Manager_CurrentSessionChanged;
        _sessionPlaybackInfoChangedHandler = Session_PlaybackInfoChanged;
        _sessionMediaPropertiesChangedHandler = Session_MediaPropertiesChanged;
        _sessionTimelinePropertiesChangedHandler = Session_TimelinePropertiesChanged;
        _observedPlaybackInfoChangedHandler = ObservedSession_PlaybackInfoChanged;
        _memoryReclaimCompletedHandler = OnMemoryReclaimCompleted;
    }

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

    private readonly SemaphoreSlim _initializeGate = new(1, 1);

    public async Task InitializeAsync()
    {
        if (_isDisposed || _isInitialized)
        {
            return;
        }

        await _initializeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed || _isInitialized)
            {
                return;
            }

            var manager = await AcquireSharedManagerAsync().ConfigureAwait(false);
            lock (_sessionObservationGate)
            {
                if (_isDisposed)
                {
                    ReleaseSharedManager();
                    return;
                }

                _holdsSharedManagerReference = true;
                _manager = manager;
                _manager.SessionsChanged += _sessionsChangedHandler;
                _manager.CurrentSessionChanged += _currentSessionChangedHandler;
                MemoryReclaimer.ReclaimCompleted += _memoryReclaimCompletedHandler;
            }

            SyncObservedSessions();
            if (_isDisposed)
            {
                TearDownCore();
                return;
            }

            var (initialSession, initialId) = ResolveSession(_preferredSessionId);
            if (_isDisposed)
            {
                TearDownCore();
                return;
            }

            AttachCurrentSession(initialSession, initialId);
            if (_isDisposed)
            {
                TearDownCore();
                return;
            }

            _isInitialized = true;
        }
        finally
        {
            _initializeGate.Release();
        }
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

    public async Task<IRandomAccessStreamReference?> GetCurrentThumbnailAsync(string? preferredSessionId = null)
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
            var mediaProperties = await TryGetMediaPropertiesAsync(session);
            return mediaProperties?.Thumbnail;
        }
        catch (Exception ex)
        {
            InvalidateCurrentSessionIfMatch(session);
            App.LogVerbose($"[MusicSession] Skipped stale thumbnail read: {ex.Message}");
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
        lock (_sessionObservationGate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        TearDownCore();
    }

    private void TearDownCore()
    {
        GlobalSystemMediaTransportControlsSessionManager? managerToDetach;
        bool releaseSharedManager;
        lock (_sessionObservationGate)
        {
            MemoryReclaimer.ReclaimCompleted -= _memoryReclaimCompletedHandler;
            managerToDetach = _manager;
            _manager = null;
            DetachSession(_currentSession);
            _currentSession = null;
            _currentSessionId = null;
            _preferredSessionId = null;
            releaseSharedManager = _holdsSharedManagerReference;
            _holdsSharedManagerReference = false;
        }

        if (managerToDetach is not null)
        {
            managerToDetach.SessionsChanged -= _sessionsChangedHandler;
            managerToDetach.CurrentSessionChanged -= _currentSessionChangedHandler;
        }

        ClearObservedSessions();
        if (releaseSharedManager)
        {
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

        var (session, resolvedId) = ResolveSession(sessionId);
        AttachCurrentSession(session, resolvedId);
        return session;
    }

    internal static bool ShouldReuseCachedPreferredSession(
        bool hasPreviousSession,
        string? previousSessionId,
        string preferredSessionId,
        bool matchesLiveEntryIdentity,
        bool isPreviousSessionClosed)
    {
        return hasPreviousSession &&
            !isPreviousSessionClosed &&
            matchesLiveEntryIdentity &&
            string.Equals(previousSessionId, preferredSessionId, StringComparison.Ordinal);
    }

    private (GlobalSystemMediaTransportControlsSession? Session, string? SessionId) ResolveSession(string? preferredSessionId)
    {
        GlobalSystemMediaTransportControlsSession? previousCurrent;
        string? previousCurrentId;
        lock (_sessionObservationGate)
        {
            previousCurrent = _currentSession;
            previousCurrentId = _currentSessionId;
        }

        if (!string.IsNullOrWhiteSpace(preferredSessionId))
        {
            _preferredSessionId = preferredSessionId;
            var preferred = FindSessionEntry(preferredSessionId);
            bool matchesLiveIdentity = preferred is not null &&
                IsSameSession(previousCurrent, preferred.Session);
            if (ShouldReuseCachedPreferredSession(
                    previousCurrent is not null,
                    previousCurrentId,
                    preferredSessionId,
                    matchesLiveIdentity,
                    IsSessionClosed(previousCurrent)))
            {
                return (previousCurrent, previousCurrentId);
            }

            if (preferred is not null && !IsSessionClosed(preferred.Session))
            {
                return (preferred.Session, preferred.SessionId);
            }

            _preferredSessionId = null;
        }
        else
        {
            _preferredSessionId = null;
        }

        if (_manager is null)
        {
            return (null, null);
        }

        GlobalSystemMediaTransportControlsSession? systemCurrent = null;
        try
        {
            systemCurrent = _manager.GetCurrentSession();
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MusicSession] Skipped stale GetCurrentSession: {ex.Message}");
        }

        IReadOnlyList<SessionEntry> entries = EnumerateSessionEntries();
        if (entries.Count == 0)
        {
            if (systemCurrent is not null && IsSameSession(previousCurrent, systemCurrent))
            {
                return (previousCurrent, previousCurrentId);
            }

            return (systemCurrent, null);
        }

        bool systemCurrentListed = systemCurrent is not null &&
            entries.Any(entry => IsSameSession(entry.Session, systemCurrent));
        int totalCount = entries.Count + (systemCurrent is not null && !systemCurrentListed ? 1 : 0);
        var candidateEntries = new SessionEntry[totalCount];
        var candidates = new SystemSessionCandidate[totalCount];

        int count = 0;
        if (systemCurrent is not null && !systemCurrentListed)
        {
            candidateEntries[count] = new SessionEntry(GetSessionId(systemCurrent), systemCurrent);
            candidates[count] = BuildSystemSessionCandidate(
                systemCurrent,
                isSystemCurrent: true,
                isPreviousCurrent: IsSameSession(previousCurrent, systemCurrent));
            count++;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            SessionEntry entry = entries[i];
            candidateEntries[count] = entry;
            candidates[count] = BuildSystemSessionCandidate(
                entry.Session,
                isSystemCurrent: IsSameSession(entry.Session, systemCurrent),
                isPreviousCurrent: IsSameSession(entry.Session, previousCurrent));
            count++;
        }

        int selectedIndex = SelectSystemSessionCandidateIndex(candidates.AsSpan(0, count));
        if (selectedIndex < 0)
        {
            return (null, null);
        }

        SessionEntry selected = candidateEntries[selectedIndex];
        return (selected.Session, selected.SessionId);
    }

    private static SystemSessionCandidate BuildSystemSessionCandidate(
        GlobalSystemMediaTransportControlsSession session,
        bool isSystemCurrent,
        bool isPreviousCurrent)
    {
        try
        {
            var playbackInfo = session.GetPlaybackInfo();
            bool isClosed = playbackInfo.PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            return new SystemSessionCandidate(
                MapPlaybackState(playbackInfo.PlaybackStatus),
                isClosed,
                isSystemCurrent,
                isPreviousCurrent);
        }
        catch
        {
            return new SystemSessionCandidate(
                MusicPlaybackState.Unknown,
                IsClosed: true,
                isSystemCurrent,
                isPreviousCurrent);
        }
    }

    internal static int SelectSystemSessionCandidateIndex(ReadOnlySpan<SystemSessionCandidate> candidates)
    {
        if (candidates.IsEmpty)
        {
            return -1;
        }

        for (int i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].State == MusicPlaybackState.Playing && candidates[i].IsSystemCurrent)
            {
                return i;
            }
        }

        for (int i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].State == MusicPlaybackState.Playing)
            {
                return i;
            }
        }

        for (int i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].IsPreviousCurrent &&
                !candidates[i].IsClosed &&
                candidates[i].State == MusicPlaybackState.Unknown)
            {
                return i;
            }
        }

        for (int i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].IsSystemCurrent && !candidates[i].IsClosed)
            {
                return i;
            }
        }

        for (int i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].IsPreviousCurrent && !candidates[i].IsClosed)
            {
                return i;
            }
        }

        for (int i = 0; i < candidates.Length; i++)
        {
            if (candidates[i].State == MusicPlaybackState.Paused)
            {
                return i;
            }
        }

        for (int i = 0; i < candidates.Length; i++)
        {
            if (!candidates[i].IsClosed)
            {
                return i;
            }
        }

        return 0;
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
        lock (_sessionObservationGate)
        {
            if (_isDisposed)
            {
                return;
            }

            if (session is null && _currentSession is null)
            {
                _currentSessionId = null;
                return;
            }

            if (IsSameSession(_currentSession, session))
            {
                if (IsSessionClosed(_currentSession))
                {
                    DetachSession(_currentSession);
                    _currentSession = null;
                    _currentSessionId = null;
                    return;
                }

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
                _currentSession.PlaybackInfoChanged += _sessionPlaybackInfoChangedHandler;
                _currentSession.MediaPropertiesChanged += _sessionMediaPropertiesChangedHandler;
                _currentSession.TimelinePropertiesChanged += _sessionTimelinePropertiesChangedHandler;
            }
        }
    }

    private void InvalidateCurrentSessionIfMatch(GlobalSystemMediaTransportControlsSession session)
    {
        lock (_sessionObservationGate)
        {
            if (IsSameSession(_currentSession, session))
            {
                DetachSession(_currentSession);
                _currentSession = null;
                _currentSessionId = null;
            }
        }
    }

    private void SyncObservedSessions()
    {
        if (_isDisposed || _manager is null)
        {
            return;
        }

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> snapshot;
        try
        {
            snapshot = _manager.GetSessions();
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MusicSession] Skipped GetSessions while syncing observers: {ex.Message}");
            return;
        }

        lock (_sessionObservationGate)
        {
            if (_isDisposed)
            {
                return;
            }

            for (int i = _observedSessions.Count - 1; i >= 0; i--)
            {
                GlobalSystemMediaTransportControlsSession existing = _observedSessions[i];
                bool stillPresent = false;
                for (int j = 0; j < snapshot.Count; j++)
                {
                    if (IsSameSession(existing, snapshot[j]))
                    {
                        stillPresent = true;
                        break;
                    }
                }

                if (!stillPresent || IsSessionClosed(existing))
                {
                    try
                    {
                        existing.PlaybackInfoChanged -= _observedPlaybackInfoChangedHandler;
                    }
                    catch (Exception ex)
                    {
                        App.LogVerbose($"[MusicSession] Skipped stale observed session detach: {ex.Message}");
                    }

                    _observedSessions.RemoveAt(i);
                }
            }

            foreach (GlobalSystemMediaTransportControlsSession session in snapshot)
            {
                bool alreadyObserved = false;
                for (int i = 0; i < _observedSessions.Count; i++)
                {
                    if (IsSameSession(_observedSessions[i], session))
                    {
                        alreadyObserved = true;
                        break;
                    }
                }

                if (alreadyObserved || IsSessionClosed(session))
                {
                    continue;
                }

                GlobalSystemMediaTransportControlsSession canonical =
                    _currentSession is not null && IsSameSession(_currentSession, session)
                        ? _currentSession
                        : session;
                try
                {
                    canonical.PlaybackInfoChanged += _observedPlaybackInfoChangedHandler;
                    _observedSessions.Add(canonical);
                }
                catch (Exception ex)
                {
                    App.LogVerbose($"[MusicSession] Skipped observing stale session: {ex.Message}");
                }
            }
        }
    }

    private void ClearObservedSessions()
    {
        lock (_sessionObservationGate)
        {
            DetachObservedSessionsCore();
        }
    }

    private void DetachObservedSessionsCore()
    {
        foreach (GlobalSystemMediaTransportControlsSession session in _observedSessions)
        {
            try
            {
                session.PlaybackInfoChanged -= _observedPlaybackInfoChangedHandler;
            }
            catch (Exception ex)
            {
                App.LogVerbose($"[MusicSession] Skipped stale observed session detach: {ex.Message}");
            }
        }

        _observedSessions.Clear();
    }

    private void DetachSession(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            session.PlaybackInfoChanged -= _sessionPlaybackInfoChangedHandler;
            session.MediaPropertiesChanged -= _sessionMediaPropertiesChangedHandler;
            session.TimelinePropertiesChanged -= _sessionTimelinePropertiesChangedHandler;
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MusicSession] Skipped stale session detach: {ex.Message}");
        }
    }

    private void OnMemoryReclaimCompleted()
    {
        if (_isDisposed || !_isInitialized || _manager is null)
        {
            return;
        }

        SyncObservedSessions();
        GlobalSystemMediaTransportControlsSession? previousSession;
        lock (_sessionObservationGate)
        {
            previousSession = _currentSession;
            if (previousSession is not null &&
                (!IsSessionListedAndOpen(_manager, previousSession) || IsSessionClosed(previousSession)))
            {
                DetachSession(previousSession);
                _currentSession = null;
                _currentSessionId = null;
                previousSession = null;
            }
        }

        var (resolvedSession, resolvedId) = ResolveSession(_preferredSessionId);
        AttachCurrentSession(resolvedSession, resolvedId);
        if (!IsSameSession(previousSession, resolvedSession))
        {
            CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
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

        SyncObservedSessions();
        lock (_sessionObservationGate)
        {
            if (_currentSession is not null &&
                (!IsSessionListedAndOpen(sender, _currentSession) || IsSessionClosed(_currentSession)))
            {
                DetachSession(_currentSession);
                _currentSession = null;
                _currentSessionId = null;
            }
            else if (string.IsNullOrWhiteSpace(_preferredSessionId) ||
                     !string.Equals(_currentSessionId, _preferredSessionId, StringComparison.Ordinal) ||
                     !IsPreferredSessionMatchingInstance(sender, _preferredSessionId, _currentSession))
            {
                _currentSessionId = null;
            }
        }

        var (resolvedSession, resolvedId) = ResolveSession(_preferredSessionId);
        AttachCurrentSession(resolvedSession, resolvedId);
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

        lock (_sessionObservationGate)
        {
            if (!string.IsNullOrWhiteSpace(_preferredSessionId) &&
                string.Equals(_currentSessionId, _preferredSessionId, StringComparison.Ordinal) &&
                IsPreferredSessionMatchingInstance(sender, _preferredSessionId, _currentSession) &&
                !IsSessionClosed(_currentSession))
            {
                return;
            }
        }

        var (resolvedSession, resolvedId) = ResolveSession(_preferredSessionId);
        AttachCurrentSession(resolvedSession, resolvedId);
        CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ObservedSession_PlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender,
        PlaybackInfoChangedEventArgs args)
    {
        if (_isDisposed)
        {
            return;
        }

        GlobalSystemMediaTransportControlsSession? current;
        string? preferredId;
        lock (_sessionObservationGate)
        {
            current = _currentSession;
            preferredId = _preferredSessionId;
        }

        if (IsSameSession(current, sender))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(preferredId))
        {
            if (IsSessionClosed(current) ||
                (FindSessionEntry(preferredId) is { } livePreferred &&
                 !IsSameSession(current, livePreferred.Session)))
            {
                var (preferredResolved, preferredResolvedId) = ResolveSession(preferredId);
                AttachCurrentSession(preferredResolved, preferredResolvedId);
                CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        try
        {
            if (sender.GetPlaybackInfo().PlaybackStatus !=
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                return;
            }

            if (current is not null && !IsSessionClosed(current))
            {
                bool currentStillPlaying = current.GetPlaybackInfo().PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                if (currentStillPlaying)
                {
                    GlobalSystemMediaTransportControlsSession? systemCurrent = SafeGetSystemCurrentSession();
                    bool senderPromotedToSystemCurrent =
                        systemCurrent is not null &&
                        IsSameSession(sender, systemCurrent) &&
                        !IsSameSession(current, systemCurrent);
                    if (!senderPromotedToSystemCurrent)
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MusicSession] Skipped observed playback probe: {ex.Message}");
            return;
        }

        var (resolvedSession, resolvedId) = ResolveSession(null);
        AttachCurrentSession(resolvedSession, resolvedId);
        CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private GlobalSystemMediaTransportControlsSession? SafeGetSystemCurrentSession()
    {
        try
        {
            return _manager?.GetCurrentSession();
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MusicSession] Skipped GetCurrentSession probe: {ex.Message}");
            return null;
        }
    }

    private static bool IsSessionClosed(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null)
        {
            return true;
        }

        try
        {
            return session.GetPlaybackInfo().PlaybackStatus ==
                GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsSessionListedAndOpen(
        GlobalSystemMediaTransportControlsSessionManager manager,
        GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            foreach (GlobalSystemMediaTransportControlsSession candidate in manager.GetSessions())
            {
                if (IsSameSession(candidate, session))
                {
                    return !IsSessionClosed(candidate);
                }
            }
        }
        catch (Exception ex)
        {
            App.LogVerbose($"[MusicSession] Skipped session presence check: {ex.Message}");
        }

        return false;
    }

    private static bool IsPreferredSessionMatchingInstance(
        GlobalSystemMediaTransportControlsSessionManager manager,
        string preferredSessionId,
        GlobalSystemMediaTransportControlsSession? currentSession)
    {
        if (currentSession is null)
        {
            return false;
        }

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
                    return IsSameSession(session, currentSession) && !IsSessionClosed(session);
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

        if (IsSessionClosed(sender))
        {
            InvalidateCurrentSessionIfMatch(sender);
            SyncObservedSessions();
            var (resolvedSession, resolvedId) = ResolveSession(_preferredSessionId);
            AttachCurrentSession(resolvedSession, resolvedId);
            CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
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
