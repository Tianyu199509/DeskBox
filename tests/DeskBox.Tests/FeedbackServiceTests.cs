using System.Net;
using System.Text;
using System.Text.Json;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class FeedbackServiceTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));

    public FeedbackServiceTests()
    {
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public async Task Submit_SendsAnonymousPayloadAndReturnsFeedbackId()
    {
        var handler = new RecordingHandler(_ =>
            Json(HttpStatusCode.Created, """{"ok":true,"id":4242}"""));
        FeedbackService service = CreateService(handler);

        DeskBoxFeedbackSubmissionResult result = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "The widget disappears after unlocking the session.",
            includeDiagnostics: true);

        Assert.True(result.Ok);
        Assert.Equal(4242, result.Id);

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://example.test/stats/api/feedback", request.Uri);
        using JsonDocument payload = JsonDocument.Parse(request.Body!);
        JsonElement root = payload.RootElement;
        Assert.Equal("bug", root.GetProperty("type").GetString());
        Assert.Equal(
            "The widget disappears after unlocking the session.",
            root.GetProperty("content").GetString());
        Assert.Equal("store", root.GetProperty("channel").GetString());
        Assert.Equal("zh-CN", root.GetProperty("locale").GetString());
        Assert.Equal("1.5.1", root.GetProperty("app_version").GetString());
        Assert.True(root.GetProperty("has_diagnostics").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("os").GetString()));
        string clientId = root.GetProperty("client_id").GetString()!;
        Assert.Equal(64, clientId.Length);
        Assert.Matches("^[0-9a-f]{64}$", clientId);
    }

    [Fact]
    public async Task Submit_MapsSuggestionKindAndAbsentDiagnostics()
    {
        var handler = new RecordingHandler(_ =>
            Json(HttpStatusCode.Created, """{"ok":true,"id":7}"""));
        FeedbackService service = CreateService(handler);

        await service.SubmitAsync(DeskBoxFeedbackKind.Suggestion, "Please add a calendar widget.", includeDiagnostics: false);

        using JsonDocument payload = JsonDocument.Parse(
            Assert.Single(handler.Requests).Body!);
        Assert.Equal("suggestion", payload.RootElement.GetProperty("type").GetString());
        Assert.False(payload.RootElement.GetProperty("has_diagnostics").GetBoolean());
    }

    [Fact]
    public async Task Submit_SurfacesRateLimitRetryWindow()
    {
        var handler = new RecordingHandler(_ => Json(
            HttpStatusCode.TooManyRequests,
            """{"ok":false,"error":"rate_limited","retry_after_minutes":37}"""));
        FeedbackService service = CreateService(handler);

        DeskBoxFeedbackSubmissionResult result = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "A sufficiently long report body.");

        Assert.False(result.Ok);
        Assert.Equal("rate_limited", result.Error);
        Assert.Equal(37, result.RetryAfterMinutes);
    }

    [Fact]
    public async Task Submit_RejectsContentShorterThanTenCharactersWithoutCallingTheApi()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Created, """{"ok":true,"id":1}"""));
        FeedbackService service = CreateService(handler);

        DeskBoxFeedbackSubmissionResult result = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "too short");

        Assert.False(result.Ok);
        Assert.Equal("content_too_short", result.Error);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Submit_ReportsNetworkFailureInsteadOfThrowing()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("offline"));
        FeedbackService service = CreateService(handler);

        DeskBoxFeedbackSubmissionResult result = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "Network should fail for this submission.");

        Assert.False(result.Ok);
        Assert.Equal("network", result.Error);
    }

    [Fact]
    public async Task Submit_HttpTimeoutReportsNetworkFailureInsteadOfThrowing()
    {
        // HttpClient's 60-second timeout surfaces as TaskCanceledException (an
        // OperationCanceledException subclass). It must land on the network
        // failure branch so the dialog's deferral always completes (#115).
        var handler = new RecordingHandler(_ => throw new TaskCanceledException("timeout"));
        FeedbackService service = CreateService(handler);

        DeskBoxFeedbackSubmissionResult result = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "A timeout should read as a network failure.");

        Assert.False(result.Ok);
        Assert.Equal("network", result.Error);
    }

    [Fact]
    public async Task Submit_UserCancellationStillThrows()
    {
        var handler = new RecordingHandler(_ => throw new OperationCanceledException("caller cancelled"));
        FeedbackService service = CreateService(handler);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.SubmitAsync(
                DeskBoxFeedbackKind.Bug,
                "A caller cancellation must stay observable.",
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task ClientId_IsPersistedAndStableAcrossServiceInstances()
    {
        var handler = new RecordingHandler(_ =>
            Json(HttpStatusCode.Created, """{"ok":true,"id":9}"""));
        FeedbackService first = CreateService(handler);
        await first.SubmitAsync(DeskBoxFeedbackKind.Bug, "First submission body text.");

        FeedbackService second = CreateService(handler);
        await second.SubmitAsync(DeskBoxFeedbackKind.Bug, "Second submission body text.");

        string[] clientIds = handler.Requests
            .Select(request => JsonDocument.Parse(request.Body!)
                .RootElement.GetProperty("client_id").GetString()!)
            .ToArray();
        Assert.Equal(2, clientIds.Length);
        Assert.Equal(clientIds[0], clientIds[1]);
        Assert.Equal(first.ClientId, second.ClientId);
        Assert.True(File.Exists(Path.Combine(_tempRoot, "feedback-client-id.txt")));
    }

    [Fact]
    public async Task UploadDiagnostics_PostsZipWithClientIdHeader()
    {
        byte[] archive = [0x50, 0x4b, 0x03, 0x04, 0x01, 0x02, 0x03, 0x04];
        string archivePath = Path.Combine(_tempRoot, "diagnostics.zip");
        await File.WriteAllBytesAsync(archivePath, archive);

        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"ok":true}"""));
        FeedbackService service = CreateService(handler);

        DeskBoxFeedbackDiagnosticsStatus status = await service.UploadDiagnosticsAsync(4242, archivePath);

        Assert.Equal(DeskBoxFeedbackDiagnosticsStatus.Uploaded, status);
        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("https://example.test/stats/api/feedback/4242/diagnostics", request.Uri);
        Assert.Equal("application/zip", request.ContentType);
        Assert.Equal(service.ClientId, request.ClientIdHeader);
        Assert.Equal(archive, request.BodyBytes);
    }

    [Fact]
    public async Task UploadDiagnostics_RejectsArchiveLargerThanFiveMegabytesWithoutCallingTheApi()
    {
        string archivePath = Path.Combine(_tempRoot, "oversized.zip");
        await File.WriteAllBytesAsync(archivePath, new byte[5 * 1024 * 1024 + 1]);

        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"ok":true}"""));
        FeedbackService service = CreateService(handler);

        DeskBoxFeedbackDiagnosticsStatus status = await service.UploadDiagnosticsAsync(1, archivePath);

        Assert.Equal(DeskBoxFeedbackDiagnosticsStatus.TooLarge, status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UploadDiagnostics_MissingArchiveReportsFailure()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, """{"ok":true}"""));
        FeedbackService service = CreateService(handler);

        DeskBoxFeedbackDiagnosticsStatus status = await service.UploadDiagnosticsAsync(
            1,
            Path.Combine(_tempRoot, "does-not-exist.zip"));

        Assert.Equal(DeskBoxFeedbackDiagnosticsStatus.Failed, status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task GetMyFeedback_ParsesStatusReplyAndVersion()
    {
        const string body = """
            {"ok":true,"items":[
              {"id":12,"type":"bug","status":"in_progress","content":"Tiles overlap on 125% scaling.",
               "reply":"Fixed in the next build.","app_version":"1.5.0","channel":"direct",
               "created_at":"2026-09-10T08:30:00Z"}
            ]}
            """;
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.OK, body));
        FeedbackService service = CreateService(handler);

        DeskBoxFeedbackMineResult result = await service.GetMyFeedbackAsync();

        Assert.True(result.Ok);
        DeskBoxFeedbackItem item = Assert.Single(result.Items);
        Assert.Equal(12, item.Id);
        Assert.Equal("in_progress", item.Status);
        Assert.Equal("Tiles overlap on 125% scaling.", item.Content);
        Assert.Equal("Fixed in the next build.", item.Reply);
        Assert.Equal("1.5.0", item.AppVersion);
        Assert.Equal(2026, item.CreatedAt.Year);

        RecordedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("https://example.test/stats/api/feedback/mine", request.Uri);
        Assert.Equal(
            service.ClientId,
            JsonDocument.Parse(request.Body!).RootElement.GetProperty("client_id").GetString());
    }

    [Fact]
    public async Task GetMyFeedback_RequestFailureReportsNotOk()
    {
        var handler = new RecordingHandler(_ => Json(HttpStatusCode.Forbidden, """{"ok":false}"""));
        FeedbackService service = CreateService(handler);

        DeskBoxFeedbackMineResult result = await service.GetMyFeedbackAsync();

        Assert.False(result.Ok);
        Assert.Empty(result.Items);
    }

    private FeedbackService CreateService(HttpMessageHandler handler) => new(
        new HttpClient(handler),
        apiBaseAddress: "https://example.test/stats/api/",
        clientStateFilePath: Path.Combine(_tempRoot, "feedback-client-id.txt"),
        channel: "store",
        localeProvider: () => "zh-CN",
        appVersion: "1.5.1");

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string? Body,
        byte[]? BodyBytes,
        string? ContentType,
        string? ClientIdHeader);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            byte[]? bytes = null;
            if (request.Content is not null)
            {
                bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            }

            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                bytes is null ? null : Encoding.UTF8.GetString(bytes),
                bytes,
                request.Content?.Headers.ContentType?.MediaType,
                request.Headers.TryGetValues("x-client-id", out IEnumerable<string>? values)
                    ? values.FirstOrDefault()
                    : null));

            return _responder(request);
        }
    }
}
