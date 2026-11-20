using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Services.Media;

public enum MediaSessionConnectionState
{
    Healthy,
    Reconnecting,
    Unavailable
}

public sealed record MusicThumbnailSnapshot(
    byte[]? Bytes,
    ulong Fingerprint,
    bool Unchanged,
    string? ContentType);

internal sealed class MediaSessionProxyCommand
{
    public long Id { get; set; }
    public string Op { get; set; } = string.Empty;
    public string? PreferredSessionId { get; set; }
    public string? SessionId { get; set; }
    public string? KnownCoverFingerprint { get; set; }
    public string? Action { get; set; }
    public long? PositionTicks { get; set; }
    public string? PlaybackMode { get; set; }
}

internal sealed class MediaSessionOptionDto
{
    public string SessionId { get; set; } = string.Empty;
    public string SourceAppUserModelId { get; set; } = string.Empty;
    public string PlaybackState { get; set; } = "unknown";
    public bool IsSystemCurrent { get; set; }
}

internal sealed class MediaSessionInfoDto
{
    public string SessionId { get; set; } = string.Empty;
    public string SourceAppUserModelId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string PlaybackState { get; set; } = "unknown";
    public long PositionTicks { get; set; }
    public long DurationTicks { get; set; }
    public long PositionUpdatedFileTimeTicks { get; set; }
    public double PlaybackRate { get; set; } = 1.0;
    public bool CanPlay { get; set; }
    public bool CanPause { get; set; }
    public bool CanGoPrevious { get; set; }
    public bool CanGoNext { get; set; }
    public bool CanSeek { get; set; }
    public bool CanChangeShuffle { get; set; }
    public bool CanChangeRepeat { get; set; }
    public string PlaybackMode { get; set; } = "normal";
    public bool HasThumbnail { get; set; }
    public string? ThumbnailFingerprint { get; set; }
    public bool ThumbnailUnchanged { get; set; }
    public string? ThumbnailContentType { get; set; }
    public string? ThumbnailBase64 { get; set; }
}

internal sealed class MediaSessionThumbnailDto
{
    public string? Fingerprint { get; set; }
    public bool Unchanged { get; set; }
    public string? ContentType { get; set; }
    public string? Base64 { get; set; }
}

internal sealed class MediaSessionTimelineDto
{
    public long PositionTicks { get; set; }
    public long DurationTicks { get; set; }
    public long PositionUpdatedFileTimeTicks { get; set; }
    public double PlaybackRate { get; set; } = 1.0;
    public string PlaybackState { get; set; } = "unknown";
}

internal sealed class MediaSessionPlaybackDto
{
    public string PlaybackState { get; set; } = "unknown";
    public double PlaybackRate { get; set; } = 1.0;
    public bool CanPlay { get; set; }
    public bool CanPause { get; set; }
    public bool CanGoPrevious { get; set; }
    public bool CanGoNext { get; set; }
    public bool CanSeek { get; set; }
    public bool CanChangeShuffle { get; set; }
    public bool CanChangeRepeat { get; set; }
    public string PlaybackMode { get; set; } = "normal";
}

internal sealed class MediaSessionProxyMessage
{
    public string Type { get; set; } = string.Empty;
    public long Id { get; set; }
    public bool Ok { get; set; }
    public string? Name { get; set; }
    public string? Error { get; set; }
    public bool Changed { get; set; }
    public List<MediaSessionOptionDto>? Sessions { get; set; }
    public MediaSessionInfoDto? Info { get; set; }
    public MediaSessionThumbnailDto? Thumbnail { get; set; }
    public MediaSessionTimelineDto? Timeline { get; set; }
    public MediaSessionPlaybackDto? Playback { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(
    typeof(MediaSessionProxyCommand),
    TypeInfoPropertyName = "MediaSessionProxyCommand")]
[JsonSerializable(
    typeof(MediaSessionProxyMessage),
    TypeInfoPropertyName = "MediaSessionProxyMessage")]
internal sealed partial class MediaSessionProxyJsonContext : JsonSerializerContext
{
}

internal static class MediaSessionProxyProtocol
{
    internal static string SerializeCommand(MediaSessionProxyCommand command) =>
        JsonSerializer.Serialize(
            command,
            MediaSessionProxyJsonContext.Default.MediaSessionProxyCommand);

    internal static MediaSessionProxyMessage? DeserializeMessage(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(
                line,
                MediaSessionProxyJsonContext.Default.MediaSessionProxyMessage);
        }
        catch
        {
            return null;
        }
    }

    internal static MusicPlaybackState ParsePlaybackState(string? raw) => raw switch
    {
        "playing" => MusicPlaybackState.Playing,
        "paused" => MusicPlaybackState.Paused,
        "stopped" => MusicPlaybackState.Stopped,
        _ => MusicPlaybackState.Unknown
    };

    internal static MusicPlaybackMode ParsePlaybackMode(string? raw) => raw switch
    {
        "shuffle" => MusicPlaybackMode.Shuffle,
        "repeat" => MusicPlaybackMode.Repeat,
        "repeat_one" => MusicPlaybackMode.RepeatOne,
        _ => MusicPlaybackMode.Normal
    };

    internal static string FormatPlaybackMode(MusicPlaybackMode mode) => mode switch
    {
        MusicPlaybackMode.Shuffle => "shuffle",
        MusicPlaybackMode.Repeat => "repeat",
        MusicPlaybackMode.RepeatOne => "repeat_one",
        _ => "normal"
    };

    internal static ulong ParseFingerprint(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) &&
        ulong.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value)
            ? value
            : 0;

    internal static DateTimeOffset ParseFileTimeUtc(long fileTimeTicks)
    {
        if (fileTimeTicks <= 0)
        {
            return default;
        }

        try
        {
            return DateTimeOffset.FromFileTime(fileTimeTicks).ToUniversalTime();
        }
        catch
        {
            return default;
        }
    }

    internal static byte[]? TryDecodeBase64(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(base64);
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>
/// Pure backoff and circuit-breaker policy governing restarts of the
/// out-of-process SMTC child. Keeps transient Windows media-service restarts
/// self-healing while stopping crash storms and surfacing a manual retry state.
/// </summary>
internal sealed class MediaSessionCircuitBreaker
{
    internal static readonly TimeSpan DefaultCrashWindow = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan DefaultHealthyUptimeReset = TimeSpan.FromMinutes(2);
    internal const int DefaultCircuitOpenThreshold = 5;
    internal static readonly TimeSpan[] DefaultBackoffSchedule =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30)
    ];

    private readonly object _gate = new();
    private readonly Queue<DateTimeOffset> _crashTimestamps = new();
    private readonly TimeSpan _crashWindow;
    private readonly TimeSpan _healthyUptimeReset;
    private readonly int _circuitOpenThreshold;
    private readonly TimeSpan[] _backoffSchedule;
    private DateTimeOffset? _lastStartedUtc;
    private DateTimeOffset? _nextAllowedStartUtc;
    private MediaSessionConnectionState _state = MediaSessionConnectionState.Healthy;

    internal MediaSessionCircuitBreaker(
        TimeSpan? crashWindow = null,
        TimeSpan? healthyUptimeReset = null,
        int circuitOpenThreshold = DefaultCircuitOpenThreshold,
        TimeSpan[]? backoffSchedule = null)
    {
        _crashWindow = crashWindow ?? DefaultCrashWindow;
        _healthyUptimeReset = healthyUptimeReset ?? DefaultHealthyUptimeReset;
        _circuitOpenThreshold = Math.Max(1, circuitOpenThreshold);
        _backoffSchedule = backoffSchedule is { Length: > 0 } ? backoffSchedule : DefaultBackoffSchedule;
    }

    internal MediaSessionConnectionState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    internal bool CanStartNow(DateTimeOffset nowUtc, out TimeSpan remainingDelay)
    {
        lock (_gate)
        {
            if (_state == MediaSessionConnectionState.Unavailable)
            {
                remainingDelay = Timeout.InfiniteTimeSpan;
                return false;
            }

            if (_nextAllowedStartUtc is { } allowedAt && nowUtc < allowedAt)
            {
                remainingDelay = allowedAt - nowUtc;
                return false;
            }

            remainingDelay = TimeSpan.Zero;
            return true;
        }
    }

    internal void RecordStarted(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            _lastStartedUtc = nowUtc;
            _nextAllowedStartUtc = null;
            _state = MediaSessionConnectionState.Healthy;
        }
    }

    internal (MediaSessionConnectionState State, TimeSpan? RetryDelay, int CrashesInWindow) RecordUnexpectedExit(
        DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            if (_lastStartedUtc is { } started && nowUtc - started >= _healthyUptimeReset)
            {
                _crashTimestamps.Clear();
            }

            _lastStartedUtc = null;
            while (_crashTimestamps.Count > 0 && nowUtc - _crashTimestamps.Peek() > _crashWindow)
            {
                _crashTimestamps.Dequeue();
            }

            _crashTimestamps.Enqueue(nowUtc);
            int count = _crashTimestamps.Count;
            if (count >= _circuitOpenThreshold)
            {
                _state = MediaSessionConnectionState.Unavailable;
                _nextAllowedStartUtc = null;
                return (_state, null, count);
            }

            int delayIndex = Math.Min(count - 1, _backoffSchedule.Length - 1);
            TimeSpan delay = _backoffSchedule[delayIndex];
            _state = MediaSessionConnectionState.Reconnecting;
            _nextAllowedStartUtc = nowUtc + delay;
            return (_state, delay, count);
        }
    }

    internal void ResetForManualRetry()
    {
        lock (_gate)
        {
            _crashTimestamps.Clear();
            _lastStartedUtc = null;
            _nextAllowedStartUtc = null;
            _state = MediaSessionConnectionState.Healthy;
        }
    }
}
