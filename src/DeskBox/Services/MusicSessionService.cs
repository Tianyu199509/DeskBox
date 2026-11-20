using System.Globalization;
using DeskBox.Services.Media;

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
    byte[]? ThumbnailBytes = null,
    ulong ThumbnailFingerprint = 0,
    bool ThumbnailUnchanged = false,
    string? ThumbnailContentType = null);

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
    event EventHandler? ConnectionStateChanged;

    MediaSessionConnectionState ConnectionState { get; }

    Task InitializeAsync();
    Task<bool> RetryConnectionAsync();
    IReadOnlyList<MusicSessionOption> GetSessionOptions();
    Task<MusicSessionInfo?> GetCurrentSessionInfoAsync(
        string? preferredSessionId = null,
        ulong knownCoverFingerprint = 0);
    Task<MusicThumbnailSnapshot?> GetCurrentThumbnailAsync(
        string? preferredSessionId = null,
        ulong knownCoverFingerprint = 0);
    Task<MusicTimelineSnapshot?> GetCurrentTimelineAsync(string? preferredSessionId = null);
    Task<MusicPlaybackSnapshot?> GetCurrentPlaybackAsync(string? preferredSessionId = null);
    Task<bool> TrySetPreferredSessionAsync(string sessionId);
    Task<bool> TryPlayAsync(string? sessionId);
    Task<bool> TryPauseAsync(string? sessionId);
    Task<bool> TryTogglePlayPauseAsync(string? sessionId);
    Task<bool> TryPreviousAsync(string? sessionId);
    Task<bool> TryNextAsync(string? sessionId);
    Task<bool> TrySeekAsync(string? sessionId, TimeSpan position);
    Task<bool> TryChangePlaybackModeAsync(string? sessionId, MusicPlaybackMode playbackMode);
}

/// <summary>
/// Lease-backed facade over <see cref="MediaSessionProxyClient"/> so all
/// Windows System Media Transport Controls (SMTC) interaction runs strictly
/// out-of-process in <c>deskbox-thumbnail-proxy.exe --media-session-server</c>
/// with zero in-process <c>Windows.Media.MediaControl.dll</c> activation.
/// </summary>
public sealed class MusicSessionService : IMusicSessionController, IDisposable
{
    internal static readonly TimeSpan MediaPropertiesIpcTimeout = TimeSpan.FromMilliseconds(2500);

    private readonly MediaSessionProxyClient _client;
    private readonly object _lifecycleGate = new();
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _holdsLease;
    private bool _isInitialized;
    private bool _isDisposed;

    public MusicSessionService()
        : this(MediaSessionProxyClient.Shared)
    {
    }

    internal MusicSessionService(MediaSessionProxyClient client)
    {
        _client = client;
    }

    internal static int SharedManagerReferenceCount => MediaSessionProxyClient.Shared.ActiveLeaseCount;

    public MediaSessionConnectionState ConnectionState => _client.ConnectionState;

    public event EventHandler? SessionsChanged;
    public event EventHandler? CurrentSessionChanged;
    public event EventHandler? PlaybackInfoChanged;
    public event EventHandler? MediaPropertiesChanged;
    public event EventHandler? TimelinePropertiesChanged;
    public event EventHandler? ConnectionStateChanged;

    public async Task InitializeAsync()
    {
        if (Volatile.Read(ref _isDisposed) || Volatile.Read(ref _isInitialized))
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

            lock (_lifecycleGate)
            {
                if (_isDisposed)
                {
                    return;
                }

                if (!_holdsLease)
                {
                    _client.SessionsChanged += Client_SessionsChanged;
                    _client.CurrentSessionChanged += Client_CurrentSessionChanged;
                    _client.PlaybackInfoChanged += Client_PlaybackInfoChanged;
                    _client.MediaPropertiesChanged += Client_MediaPropertiesChanged;
                    _client.TimelinePropertiesChanged += Client_TimelinePropertiesChanged;
                    _client.ConnectionStateChanged += Client_ConnectionStateChanged;
                    _client.AcquireLease();
                    _holdsLease = true;
                }
            }

            _ = await _client.EnsureConnectedAsync().ConfigureAwait(false);
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

    public async Task<bool> RetryConnectionAsync()
    {
        if (_isDisposed)
        {
            return false;
        }

        await InitializeAsync().ConfigureAwait(false);
        if (_isDisposed)
        {
            return false;
        }

        return await _client.RetryConnectionAsync().ConfigureAwait(false);
    }

    public IReadOnlyList<MusicSessionOption> GetSessionOptions()
    {
        if (_isDisposed || !_holdsLease)
        {
            return [];
        }

        return _client.GetCachedSessionOptions();
    }

    public async Task<MusicSessionInfo?> GetCurrentSessionInfoAsync(
        string? preferredSessionId = null,
        ulong knownCoverFingerprint = 0)
    {
        await InitializeAsync().ConfigureAwait(false);
        if (_isDisposed)
        {
            return null;
        }

        return await _client
            .GetCurrentSessionInfoAsync(preferredSessionId, knownCoverFingerprint)
            .ConfigureAwait(false);
    }

    public async Task<MusicThumbnailSnapshot?> GetCurrentThumbnailAsync(
        string? preferredSessionId = null,
        ulong knownCoverFingerprint = 0)
    {
        await InitializeAsync().ConfigureAwait(false);
        if (_isDisposed)
        {
            return null;
        }

        return await _client
            .GetCurrentThumbnailAsync(preferredSessionId, knownCoverFingerprint)
            .ConfigureAwait(false);
    }

    public async Task<MusicTimelineSnapshot?> GetCurrentTimelineAsync(string? preferredSessionId = null)
    {
        await InitializeAsync().ConfigureAwait(false);
        if (_isDisposed)
        {
            return null;
        }

        return await _client.GetCurrentTimelineAsync(preferredSessionId).ConfigureAwait(false);
    }

    public async Task<MusicPlaybackSnapshot?> GetCurrentPlaybackAsync(string? preferredSessionId = null)
    {
        await InitializeAsync().ConfigureAwait(false);
        if (_isDisposed)
        {
            return null;
        }

        return await _client.GetCurrentPlaybackAsync(preferredSessionId).ConfigureAwait(false);
    }

    public async Task<bool> TrySetPreferredSessionAsync(string sessionId)
    {
        await InitializeAsync().ConfigureAwait(false);
        if (_isDisposed)
        {
            return false;
        }

        return await _client.TrySetPreferredSessionAsync(sessionId).ConfigureAwait(false);
    }

    public Task<bool> TryPlayAsync(string? sessionId) =>
        RunControlAsync(sessionId, "play");

    public Task<bool> TryPauseAsync(string? sessionId) =>
        RunControlAsync(sessionId, "pause");

    public Task<bool> TryTogglePlayPauseAsync(string? sessionId) =>
        RunControlAsync(sessionId, "toggle_play_pause");

    public Task<bool> TryPreviousAsync(string? sessionId) =>
        RunControlAsync(sessionId, "previous");

    public Task<bool> TryNextAsync(string? sessionId) =>
        RunControlAsync(sessionId, "next");

    public async Task<bool> TrySeekAsync(string? sessionId, TimeSpan position)
    {
        await InitializeAsync().ConfigureAwait(false);
        if (_isDisposed)
        {
            return false;
        }

        long positionHundredNanoseconds = Math.Max(0L, (long)position.TotalMilliseconds * 10_000L);
        return await _client
            .TryControlAsync(sessionId, "seek", positionTicks: positionHundredNanoseconds)
            .ConfigureAwait(false);
    }

    public async Task<bool> TryChangePlaybackModeAsync(string? sessionId, MusicPlaybackMode playbackMode)
    {
        await InitializeAsync().ConfigureAwait(false);
        if (_isDisposed)
        {
            return false;
        }

        return await _client
            .TryControlAsync(sessionId, "playback_mode", playbackMode: playbackMode)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_lifecycleGate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
        }

        TearDownCore();
    }

    private async Task<bool> RunControlAsync(string? sessionId, string action)
    {
        await InitializeAsync().ConfigureAwait(false);
        if (_isDisposed)
        {
            return false;
        }

        return await _client.TryControlAsync(sessionId, action).ConfigureAwait(false);
    }

    private void TearDownCore()
    {
        bool shouldReleaseLease;
        lock (_lifecycleGate)
        {
            shouldReleaseLease = _holdsLease;
            _holdsLease = false;
        }

        if (!shouldReleaseLease)
        {
            return;
        }

        _client.SessionsChanged -= Client_SessionsChanged;
        _client.CurrentSessionChanged -= Client_CurrentSessionChanged;
        _client.PlaybackInfoChanged -= Client_PlaybackInfoChanged;
        _client.MediaPropertiesChanged -= Client_MediaPropertiesChanged;
        _client.TimelinePropertiesChanged -= Client_TimelinePropertiesChanged;
        _client.ConnectionStateChanged -= Client_ConnectionStateChanged;
        _client.ReleaseLease();
    }

    private void Client_SessionsChanged(object? sender, EventArgs e)
    {
        if (!_isDisposed)
        {
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Client_CurrentSessionChanged(object? sender, EventArgs e)
    {
        if (!_isDisposed)
        {
            CurrentSessionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Client_PlaybackInfoChanged(object? sender, EventArgs e)
    {
        if (!_isDisposed)
        {
            PlaybackInfoChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Client_MediaPropertiesChanged(object? sender, EventArgs e)
    {
        if (!_isDisposed)
        {
            MediaPropertiesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Client_TimelinePropertiesChanged(object? sender, EventArgs e)
    {
        if (!_isDisposed)
        {
            TimelinePropertiesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Client_ConnectionStateChanged(object? sender, EventArgs e)
    {
        if (!_isDisposed)
        {
            ConnectionStateChanged?.Invoke(this, EventArgs.Empty);
        }
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

    internal static async Task<T?> TryAwaitWithTimeoutAsync<T>(Task<T> operationTask, TimeSpan timeout)
    {
        try
        {
            return await operationTask.WaitAsync(timeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            App.Log($"[MusicSession] Timed out waiting {timeout.TotalMilliseconds:F0}ms for cross-process SMTC async operation.");
            return default;
        }
        catch (Exception ex)
        {
            App.Log($"[MusicSession] Failed to read media properties: {ex.Message}");
            return default;
        }
    }

    internal static string CreateSessionId(string sourceAppUserModelId, int sourceOrdinal)
    {
        return string.Concat(
            sourceAppUserModelId,
            "\u001F",
            Math.Max(0, sourceOrdinal).ToString(CultureInfo.InvariantCulture));
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
}
