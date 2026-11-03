using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Services;

public enum DeskBoxFeedbackKind
{
    Suggestion,
    Bug,
}

public sealed record DeskBoxFeedbackSubmissionResult(
    bool Ok,
    long? Id,
    int? RetryAfterMinutes,
    string? Error)
{
    public static DeskBoxFeedbackSubmissionResult Success(long id) => new(true, id, null, null);
    public static DeskBoxFeedbackSubmissionResult RateLimited(int retryAfterMinutes) =>
        new(false, null, retryAfterMinutes, "rate_limited");
    public static DeskBoxFeedbackSubmissionResult Rejected(string error) => new(false, null, null, error);
    public static DeskBoxFeedbackSubmissionResult NetworkFailure() => new(false, null, null, "network");
    public static DeskBoxFeedbackSubmissionResult InvalidContent() => new(false, null, null, "content_too_short");
}

public enum DeskBoxFeedbackDiagnosticsStatus
{
    Uploaded,
    TooLarge,
    Failed,
}

public sealed record DeskBoxFeedbackMessage(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("author_role")] string AuthorRole,
    [property: JsonPropertyName("author_nickname")] string? AuthorNickname,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record DeskBoxFeedbackItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("reply")] string? Reply,
    [property: JsonPropertyName("app_version")] string? AppVersion,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
    [property: JsonPropertyName("messages")] IReadOnlyList<DeskBoxFeedbackMessage>? Messages = null);

public sealed record DeskBoxFeedbackReplyResult(
    bool Ok,
    DeskBoxFeedbackMessage? Message,
    int? RetryAfterMinutes,
    string? Error)
{
    public static DeskBoxFeedbackReplyResult Success(DeskBoxFeedbackMessage message) =>
        new(true, message, null, null);
    public static DeskBoxFeedbackReplyResult RateLimited(int retryAfterMinutes) =>
        new(false, null, retryAfterMinutes, "rate_limited");
    public static DeskBoxFeedbackReplyResult Rejected(string error) =>
        new(false, null, null, error);
    public static DeskBoxFeedbackReplyResult NetworkFailure() =>
        new(false, null, null, "network");
    public static DeskBoxFeedbackReplyResult InvalidContent() =>
        new(false, null, null, "content_too_short");
}

public sealed record DeskBoxFeedbackMineResult(bool Ok, IReadOnlyList<DeskBoxFeedbackItem> Items)
{
    public static DeskBoxFeedbackMineResult Failure() => new(false, []);
}

/// <summary>
/// Anonymous in-app feedback client for the deskbox.fun feedback API.
/// Submit → https://deskbox.fun/stats/api/feedback, then optionally attach the
/// privacy-filtered diagnostics bundle; "my feedback" lists prior submissions
/// plus multi-turn replies keyed by the persisted anonymous client id.
/// The submit cooldown is tracked locally as a UTC unix-seconds timestamp that
/// is written only on a successful submission (#449): it survives restarts
/// unambiguously, never arms on failed/limited attempts, and lets the client
/// detect a server rate-limit state that outlives its own claimed window.
/// </summary>
public sealed partial class FeedbackService
{
    public const string DefaultApiBaseUrl = "https://deskbox.fun/stats/api/";
    private const int MinContentLength = 10;
    private const int MinReplyLength = 2;
    private const int MaxContentLength = 2000;
    private const int MaxDiagnosticsBytes = 5 * 1024 * 1024;

    /// <summary>Intended submit cooldown window, matching the server's 5-minute
    /// per-thread cooldown and the user-facing rate-limit prompt.</summary>
    private const int CooldownWindowMinutes = 5;

    /// <summary>Clock-skew tolerance before the client declares the server's
    /// per-id rate state stale and rotates the anonymous id (#449).</summary>
    private const int StaleServerRateStateGraceMinutes = 5;

    /// <summary>Upper bound on a short-window server claim the client honors
    /// when proving staleness. Note: <c>retry_after_minutes &gt;= 60</c> is the
    /// server's 24-hour rolling daily cap (10 threads / 24h) and is checked
    /// against a full 24-hour window so legitimate active users never have
    /// their anonymous history rotated away.</summary>
    private const int MaxTrustedServerWindowMinutes = 60;
    private const int DailyCapRetryAfterMinutes = 60;
    private const int DailyCapWindowMinutes = 24 * 60;

    private const string ClientStateFileName = "feedback-client-id.txt";
    private const string CooldownStateFileName = "feedback-last-submit.txt";

    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;
    private readonly string _clientStateFilePath;
    private readonly string _cooldownStateFilePath;
    private readonly string _channel;
    private readonly Func<string> _localeProvider;
    private readonly string _appVersion;
    private readonly Func<DateTimeOffset> _utcClock;
    private string? _clientId;

    public FeedbackService(
        HttpClient? httpClient = null,
        string? apiBaseAddress = null,
        string? clientStateFilePath = null,
        string? channel = null,
        Func<string>? localeProvider = null,
        string? appVersion = null,
        string? cooldownStateFilePath = null,
        Func<DateTimeOffset>? utcClock = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _apiBaseUrl = apiBaseAddress ?? DefaultApiBaseUrl;
        _clientStateFilePath = clientStateFilePath ?? Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            ClientStateFileName);
        _cooldownStateFilePath = cooldownStateFilePath ?? Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            CooldownStateFileName);
        _channel = channel ?? (AppDistributionService.Current.IsMicrosoftStore ? "store" : "direct");
        _localeProvider = localeProvider ?? (() => CultureInfo.CurrentUICulture.Name);
        _appVersion = appVersion ?? ResolveAppVersion();
        _utcClock = utcClock ?? (static () => DateTimeOffset.UtcNow);
    }

    /// <summary>Stable anonymous identifier: SHA-256 of a one-time random GUID,
    /// persisted next to the app data so reinstall keeps a new identity.</summary>
    public string ClientId => _clientId ??= LoadOrCreateClientId();

    public async Task<DeskBoxFeedbackSubmissionResult> SubmitAsync(
        DeskBoxFeedbackKind kind,
        string content,
        bool includeDiagnostics = false,
        CancellationToken cancellationToken = default)
    {
        string trimmed = (content ?? string.Empty).Trim();
        if (trimmed.Length < MinContentLength || trimmed.Length > MaxContentLength)
        {
            return DeskBoxFeedbackSubmissionResult.InvalidContent();
        }

        // The cooldown is a purely local, success-only record (#449): while the
        // intended window is still open, answer locally instead of relying on
        // (or burdening) the server's rate state.
        DateTimeOffset utcNow = _utcClock();
        if (TryGetRemainingCooldown(utcNow) is TimeSpan remaining)
        {
            return DeskBoxFeedbackSubmissionResult.RateLimited(
                Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes)));
        }

        DeskBoxFeedbackSubmissionResult result = await SubmitCoreAsync(
            kind, trimmed, includeDiagnostics, cancellationToken);

        if (!result.Ok &&
            result.RetryAfterMinutes is int serverRetryAfterMinutes &&
            IsServerRateStateProvablyStale(utcNow, serverRetryAfterMinutes))
        {
            // The server rejected a submission even though the client's own
            // success-only record proves the server's claimed window elapsed
            // long ago: its per-id rate bookkeeping is wedged (#449 — users
            // were permanently locked out of in-app feedback). The anonymous
            // id is per-install by design, so rotating it once and retrying is
            // exactly what a reinstall would do. The rotation is gated on the
            // proven staleness above, so a legitimately open window on either
            // side can never trigger it.
            RotateClientId();
            result = await SubmitCoreAsync(kind, trimmed, includeDiagnostics, cancellationToken);
        }

        if (result.Ok)
        {
            // Only a successful submission may start (or restart) the cooldown.
            RecordSuccessfulSubmit(_utcClock());
        }

        return result;
    }

    private async Task<DeskBoxFeedbackSubmissionResult> SubmitCoreAsync(
        DeskBoxFeedbackKind kind,
        string trimmedContent,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        var payload = new DeskBoxFeedbackSubmitPayload(
            kind == DeskBoxFeedbackKind.Bug ? "bug" : "suggestion",
            trimmedContent,
            ClientId,
            _appVersion,
            RuntimeInformation.OSDescription,
            _channel,
            NormalizeLocale(_localeProvider()),
            includeDiagnostics);

        string json = JsonSerializer.Serialize(
            payload,
            FeedbackJsonContext.Default.FeedbackSubmitPayload);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("feedback"));
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                DeskBoxFeedbackSubmitResponse? body = TryParseSubmitResponse(await ReadBodyAsync(response, cancellationToken));
                int retryAfter = body?.RetryAfterMinutes is > 0 ? body.RetryAfterMinutes.Value : CooldownWindowMinutes;
                return DeskBoxFeedbackSubmissionResult.RateLimited(retryAfter);
            }

            if ((int)response.StatusCode is < 200 or >= 300)
            {
                DeskBoxFeedbackSubmitResponse? body = TryParseSubmitResponse(await ReadBodyAsync(response, cancellationToken));
                return DeskBoxFeedbackSubmissionResult.Rejected(body?.Error ?? $"http_{(int)response.StatusCode}");
            }

            DeskBoxFeedbackSubmitResponse? success = TryParseSubmitResponse(await ReadBodyAsync(response, cancellationToken));
            return success?.Ok == true && success.Id is > 0
                ? DeskBoxFeedbackSubmissionResult.Success(success.Id.Value)
                : DeskBoxFeedbackSubmissionResult.Rejected("invalid_response");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // HttpClient's 60-second timeout also surfaces as an
            // OperationCanceledException. It must read as a network failure;
            // rethrowing escapes the dialog's button deferral and wedges the
            // submit button forever (feedback #115).
            return DeskBoxFeedbackSubmissionResult.NetworkFailure();
        }
        catch (Exception)
        {
            return DeskBoxFeedbackSubmissionResult.NetworkFailure();
        }
    }

    public async Task<DeskBoxFeedbackDiagnosticsStatus> UploadDiagnosticsAsync(
        long feedbackId,
        string archivePath,
        CancellationToken cancellationToken = default)
    {
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(archivePath, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return DeskBoxFeedbackDiagnosticsStatus.Failed;
        }

        if (bytes.Length > MaxDiagnosticsBytes)
        {
            return DeskBoxFeedbackDiagnosticsStatus.TooLarge;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                BuildUri($"feedback/{feedbackId}/diagnostics"));
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/zip");
            request.Headers.TryAddWithoutValidation("x-client-id", ClientId);
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            return (int)response.StatusCode is >= 200 and < 300
                ? DeskBoxFeedbackDiagnosticsStatus.Uploaded
                : DeskBoxFeedbackDiagnosticsStatus.Failed;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return DeskBoxFeedbackDiagnosticsStatus.Failed;
        }
    }

    public async Task<DeskBoxFeedbackMineResult> GetMyFeedbackAsync(CancellationToken cancellationToken = default)
    {
        string json = JsonSerializer.Serialize(
            new DeskBoxFeedbackMinePayload(ClientId),
            FeedbackJsonContext.Default.FeedbackMinePayload);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("feedback/mine"));
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return DeskBoxFeedbackMineResult.Failure();
            }

            DeskBoxFeedbackMineResponse? body = JsonSerializer.Deserialize(
                await ReadBodyAsync(response, cancellationToken),
                FeedbackJsonContext.Default.FeedbackMineResponse);
            if (body?.Ok != true || body.Items is null)
            {
                return DeskBoxFeedbackMineResult.Failure();
            }

            return new DeskBoxFeedbackMineResult(true, body.Items);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Same timeout-vs-cancellation split as SubmitAsync: the dialog's
            // loader has no retry path for a thrown exception, so a 60-second
            // timeout must land on the Failure branch.
            return DeskBoxFeedbackMineResult.Failure();
        }
        catch (Exception)
        {
            return DeskBoxFeedbackMineResult.Failure();
        }
    }

    public async Task<DeskBoxFeedbackReplyResult> PostReplyAsync(
        long feedbackId,
        string content,
        CancellationToken cancellationToken = default)
    {
        if (feedbackId <= 0)
        {
            return DeskBoxFeedbackReplyResult.Rejected("invalid_id");
        }

        string trimmed = (content ?? string.Empty).Trim();
        if (trimmed.Length < MinReplyLength || trimmed.Length > MaxContentLength)
        {
            return DeskBoxFeedbackReplyResult.InvalidContent();
        }

        string json = JsonSerializer.Serialize(
            new DeskBoxFeedbackReplyPayload(ClientId, trimmed),
            FeedbackJsonContext.Default.FeedbackReplyPayload);
        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                BuildUri($"feedback/{feedbackId}/messages"));
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await _httpClient.SendAsync(request, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
            {
                DeskBoxFeedbackReplyResponse? body = TryParseReplyResponse(
                    await ReadBodyAsync(response, cancellationToken));
                int retryAfter = body?.RetryAfterMinutes is > 0 ? body.RetryAfterMinutes.Value : 1;
                return DeskBoxFeedbackReplyResult.RateLimited(retryAfter);
            }

            if ((int)response.StatusCode is < 200 or >= 300)
            {
                DeskBoxFeedbackReplyResponse? body = TryParseReplyResponse(
                    await ReadBodyAsync(response, cancellationToken));
                return DeskBoxFeedbackReplyResult.Rejected(body?.Error ?? $"http_{(int)response.StatusCode}");
            }

            DeskBoxFeedbackReplyResponse? success = TryParseReplyResponse(
                await ReadBodyAsync(response, cancellationToken));
            if (success?.Ok != true)
            {
                return DeskBoxFeedbackReplyResult.Rejected("invalid_response");
            }

            DeskBoxFeedbackMessage message = success.Message ??
                new DeskBoxFeedbackMessage(0, "op", null, trimmed, _utcClock());
            return DeskBoxFeedbackReplyResult.Success(message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return DeskBoxFeedbackReplyResult.NetworkFailure();
        }
        catch (Exception)
        {
            return DeskBoxFeedbackReplyResult.NetworkFailure();
        }
    }

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private string BuildUri(string relative)
    {
        return _apiBaseUrl.EndsWith('/') ? _apiBaseUrl + relative : _apiBaseUrl + "/" + relative;
    }

    private string LoadOrCreateClientId()
    {
        try
        {
            string existing = File.ReadAllText(_clientStateFilePath).Trim();
            if (existing.Length == 64 && existing.All(IsHexCharacter))
            {
                return existing.ToLowerInvariant();
            }
        }
        catch (Exception)
        {
            // Missing or unreadable state file: fall through and mint a new id.
        }

        string clientId = MintClientId();
        PersistClientId(clientId);
        return clientId;
    }

    /// <summary>Mints a fresh anonymous id and persists it, dropping the old
    /// one. Used only to recover from a provably stale server-side rate state
    /// (#449); prior "my feedback" history keyed to the old id is forfeited,
    /// which is the same trade-off a reinstall makes.</summary>
    private void RotateClientId()
    {
        string rotated = MintClientId();
        _clientId = rotated;
        PersistClientId(rotated);
    }

    private static string MintClientId() => Convert.ToHexString(
        SHA256.HashData(Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N")))).ToLowerInvariant();

    private void PersistClientId(string clientId)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_clientStateFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = _clientStateFilePath + ".tmp";
            File.WriteAllText(temporaryPath, clientId);
            File.Move(temporaryPath, _clientStateFilePath, overwrite: true);
        }
        catch (Exception)
        {
            // Persistence failures degrade to an in-memory id for this session.
        }
    }

    /// <summary>Remaining cooldown for the current install, or null when the
    /// window has elapsed or no successful submission is on record. The record
    /// is UTC unix seconds, so restarts, locale and DST cannot shift it; a
    /// clock rolled back mid-window is tolerated by re-arming at most one
    /// extra full window.</summary>
    private TimeSpan? TryGetRemainingCooldown(DateTimeOffset utcNow)
    {
        long? lastSuccessUnixSeconds = TryReadLastSuccessfulSubmitUtcUnixSeconds(utcNow);
        if (lastSuccessUnixSeconds is not long stored)
        {
            return null;
        }

        TimeSpan elapsed = utcNow - DateTimeOffset.FromUnixTimeSeconds(stored);
        if (elapsed >= TimeSpan.FromMinutes(CooldownWindowMinutes))
        {
            return null;
        }

        return elapsed > TimeSpan.Zero
            ? TimeSpan.FromMinutes(CooldownWindowMinutes) - elapsed
            : TimeSpan.FromMinutes(CooldownWindowMinutes);
    }

    /// <summary>True when the client's own success-only record proves the
    /// server-claimed rate-limit window elapsed long ago, i.e. the server's
    /// per-id rate state can no longer be legitimate (#449).</summary>
    private bool IsServerRateStateProvablyStale(DateTimeOffset utcNow, int serverRetryAfterMinutes)
    {
        long? lastSuccessUnixSeconds = TryReadLastSuccessfulSubmitUtcUnixSeconds(utcNow);
        if (lastSuccessUnixSeconds is not long stored)
        {
            // No local success on record: nothing contradicts the server.
            return false;
        }

        TimeSpan elapsed = utcNow - DateTimeOffset.FromUnixTimeSeconds(stored);
        int claimedWindowMinutes = serverRetryAfterMinutes >= DailyCapRetryAfterMinutes
            ? DailyCapWindowMinutes
            : Math.Min(
                Math.Max(serverRetryAfterMinutes, CooldownWindowMinutes),
                MaxTrustedServerWindowMinutes);
        return elapsed >= TimeSpan.FromMinutes(claimedWindowMinutes + StaleServerRateStateGraceMinutes);
    }

    /// <summary>Reads the persisted last-successful-submit timestamp. Any
    /// unparseable, non-positive or implausibly future value (beyond the
    /// cooldown window ahead of <paramref name="utcNow"/>) reads as absent —
    /// a corrupt or future-dated record must never lock the user out.</summary>
    private long? TryReadLastSuccessfulSubmitUtcUnixSeconds(DateTimeOffset utcNow)
    {
        string raw;
        try
        {
            raw = File.ReadAllText(_cooldownStateFilePath).Trim();
        }
        catch (Exception)
        {
            return null;
        }

        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long unixSeconds) ||
            unixSeconds <= 0)
        {
            return null;
        }

        long maxPlausibleUnixSeconds = utcNow.ToUnixTimeSeconds() +
            (long)TimeSpan.FromMinutes(CooldownWindowMinutes).TotalSeconds;
        return unixSeconds > maxPlausibleUnixSeconds ? null : unixSeconds;
    }

    private void RecordSuccessfulSubmit(DateTimeOffset utcNow)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_cooldownStateFilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporaryPath = _cooldownStateFilePath + ".tmp";
            File.WriteAllText(
                temporaryPath,
                utcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
            File.Move(temporaryPath, _cooldownStateFilePath, overwrite: true);
        }
        catch (Exception)
        {
            // Persistence failures skip the local cooldown for this install:
            // the server-side limit still applies, so submissions stay safe.
        }
    }

    private static bool IsHexCharacter(char c) =>
        c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static string NormalizeLocale(string locale)
    {
        string trimmed = locale.Trim();
        return trimmed.Length > 16 ? trimmed[..16] : trimmed;
    }

    private static string ResolveAppVersion()
    {
        return Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            .Split('+')[0] ??
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ??
            "1.0.2";
    }

    private static DeskBoxFeedbackSubmitResponse? TryParseSubmitResponse(string json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize(json, FeedbackJsonContext.Default.FeedbackSubmitResponse);

    private static DeskBoxFeedbackReplyResponse? TryParseReplyResponse(string json) =>
        string.IsNullOrEmpty(json)
            ? null
            : JsonSerializer.Deserialize(json, FeedbackJsonContext.Default.FeedbackReplyResponse);

    internal sealed record DeskBoxFeedbackSubmitPayload(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("content")] string Content,
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("app_version")] string AppVersion,
        [property: JsonPropertyName("os")] string Os,
        [property: JsonPropertyName("channel")] string Channel,
        [property: JsonPropertyName("locale")] string Locale,
        [property: JsonPropertyName("has_diagnostics")] bool HasDiagnostics);

    internal sealed record DeskBoxFeedbackMinePayload(
        [property: JsonPropertyName("client_id")] string ClientId);

    internal sealed record DeskBoxFeedbackReplyPayload(
        [property: JsonPropertyName("client_id")] string ClientId,
        [property: JsonPropertyName("content")] string Content);

    internal sealed record DeskBoxFeedbackSubmitResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("retry_after_minutes")] int? RetryAfterMinutes);

    internal sealed record DeskBoxFeedbackReplyResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("message")] DeskBoxFeedbackMessage? Message,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("retry_after_minutes")] int? RetryAfterMinutes);

    internal sealed record DeskBoxFeedbackMineResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("items")] List<DeskBoxFeedbackItem>? Items);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(DeskBoxFeedbackSubmitPayload), TypeInfoPropertyName = "FeedbackSubmitPayload")]
    [JsonSerializable(typeof(DeskBoxFeedbackMinePayload), TypeInfoPropertyName = "FeedbackMinePayload")]
    [JsonSerializable(typeof(DeskBoxFeedbackReplyPayload), TypeInfoPropertyName = "FeedbackReplyPayload")]
    [JsonSerializable(typeof(DeskBoxFeedbackSubmitResponse), TypeInfoPropertyName = "FeedbackSubmitResponse")]
    [JsonSerializable(typeof(DeskBoxFeedbackReplyResponse), TypeInfoPropertyName = "FeedbackReplyResponse")]
    [JsonSerializable(typeof(DeskBoxFeedbackMineResponse), TypeInfoPropertyName = "FeedbackMineResponse")]
    private sealed partial class FeedbackJsonContext : JsonSerializerContext
    {
    }
}
