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

public sealed record DeskBoxFeedbackItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("reply")] string? Reply,
    [property: JsonPropertyName("app_version")] string? AppVersion,
    [property: JsonPropertyName("channel")] string? Channel,
    [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt);

public sealed record DeskBoxFeedbackMineResult(bool Ok, IReadOnlyList<DeskBoxFeedbackItem> Items)
{
    public static DeskBoxFeedbackMineResult Failure() => new(false, []);
}

/// <summary>
/// Anonymous in-app feedback client for the deskbox.fun feedback API.
/// Submit → https://deskbox.fun/stats/api/feedback, then optionally attach the
/// privacy-filtered diagnostics bundle; "my feedback" lists prior submissions
/// plus official replies keyed by the persisted anonymous client id.
/// </summary>
public sealed partial class FeedbackService
{
    public const string DefaultApiBaseUrl = "https://deskbox.fun/stats/api/";
    private const int MinContentLength = 10;
    private const int MaxContentLength = 2000;
    private const int MaxDiagnosticsBytes = 5 * 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly string _apiBaseUrl;
    private readonly string _clientStateFilePath;
    private readonly string _channel;
    private readonly Func<string> _localeProvider;
    private readonly string _appVersion;
    private string? _clientId;

    public FeedbackService(
        HttpClient? httpClient = null,
        string? apiBaseAddress = null,
        string? clientStateFilePath = null,
        string? channel = null,
        Func<string>? localeProvider = null,
        string? appVersion = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _apiBaseUrl = apiBaseAddress ?? DefaultApiBaseUrl;
        _clientStateFilePath = clientStateFilePath ?? Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "feedback-client-id.txt");
        _channel = channel ?? (AppDistributionService.Current.IsMicrosoftStore ? "store" : "direct");
        _localeProvider = localeProvider ?? (() => CultureInfo.CurrentUICulture.Name);
        _appVersion = appVersion ?? ResolveAppVersion();
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

        var payload = new DeskBoxFeedbackSubmitPayload(
            kind == DeskBoxFeedbackKind.Bug ? "bug" : "suggestion",
            trimmed,
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
                int retryAfter = body?.RetryAfterMinutes is > 0 ? body.RetryAfterMinutes.Value : 10;
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

        string clientId = Convert.ToHexString(
            SHA256.HashData(Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N")))).ToLowerInvariant();
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

        return clientId;
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

    internal sealed record DeskBoxFeedbackSubmitResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("retry_after_minutes")] int? RetryAfterMinutes);

    internal sealed record DeskBoxFeedbackMineResponse(
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("items")] List<DeskBoxFeedbackItem>? Items);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(DeskBoxFeedbackSubmitPayload), TypeInfoPropertyName = "FeedbackSubmitPayload")]
    [JsonSerializable(typeof(DeskBoxFeedbackMinePayload), TypeInfoPropertyName = "FeedbackMinePayload")]
    [JsonSerializable(typeof(DeskBoxFeedbackSubmitResponse), TypeInfoPropertyName = "FeedbackSubmitResponse")]
    [JsonSerializable(typeof(DeskBoxFeedbackMineResponse), TypeInfoPropertyName = "FeedbackMineResponse")]
    private sealed partial class FeedbackJsonContext : JsonSerializerContext
    {
    }
}
