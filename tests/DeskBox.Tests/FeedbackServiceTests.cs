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
    public async Task Submit_SuccessArmsLocalCooldownThatBlocksNextAttemptWithoutCallingTheApi()
    {
        // Regression for #449: the cooldown must start only on a successful
        // submission and be answered locally while the window is open.
        var handler = new RecordingHandler(_ =>
            Json(HttpStatusCode.Created, """{"ok":true,"id":33}"""));
        DateTimeOffset utcNow = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        FeedbackService service = CreateService(handler, () => utcNow);

        Assert.True((await service.SubmitAsync(DeskBoxFeedbackKind.Bug, "First successful feedback body.")).Ok);

        DeskBoxFeedbackSubmissionResult blocked = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "Second attempt inside the cooldown window.");

        Assert.False(blocked.Ok);
        Assert.Equal("rate_limited", blocked.Error);
        Assert.Equal(5, blocked.RetryAfterMinutes);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Submit_ServerRateLimitResponseAloneDoesNotArmTheLocalCooldown()
    {
        // A 429 is a rejected attempt, never a successful one: it must not
        // write the local cooldown state, and without a local success record
        // the client must not rotate its anonymous id either.
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.TooManyRequests, """{"ok":false,"error":"rate_limited","retry_after_minutes":10}"""),
            Json(HttpStatusCode.Created, """{"ok":true,"id":44}"""),
        });
        var handler = new RecordingHandler(_ => responses.Dequeue());
        DateTimeOffset utcNow = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        FeedbackService service = CreateService(handler, () => utcNow);

        DeskBoxFeedbackSubmissionResult limited = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "Rate limited by the server on first try.");
        Assert.False(limited.Ok);
        Assert.Equal(10, limited.RetryAfterMinutes);

        // Still the same instant: only a success could have armed a cooldown.
        DeskBoxFeedbackSubmissionResult allowed = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "The retry goes through immediately.");

        Assert.True(allowed.Ok);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(BodyClientId(handler.Requests[0]), BodyClientId(handler.Requests[1]));
    }

    [Fact]
    public async Task Submit_NetworkFailureDoesNotArmTheLocalCooldown()
    {
        int calls = 0;
        var handler = new RecordingHandler(_ =>
            calls++ == 0
                ? throw new HttpRequestException("offline")
                : Json(HttpStatusCode.Created, """{"ok":true,"id":45}"""));
        DateTimeOffset utcNow = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        FeedbackService service = CreateService(handler, () => utcNow);

        DeskBoxFeedbackSubmissionResult failed = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "Network is down for this attempt.");
        Assert.False(failed.Ok);
        Assert.Equal("network", failed.Error);

        DeskBoxFeedbackSubmissionResult retried = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "Network is back for this attempt.");
        Assert.True(retried.Ok);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Cooldown_ExpiresAfterTheWindowAcrossRestart()
    {
        // The cooldown must survive a restart (fresh service instance, same
        // state file) and expire after the intended window.
        var handler = new RecordingHandler(_ =>
            Json(HttpStatusCode.Created, """{"ok":true,"id":71}"""));
        DateTimeOffset startTime = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        DateTimeOffset utcNow = startTime;
        FeedbackService first = CreateService(handler, () => utcNow);
        Assert.True((await first.SubmitAsync(DeskBoxFeedbackKind.Bug, "Submitted before restarting.")).Ok);

        // The on-disk contract is a culture-invariant UTC unix-seconds stamp.
        string cooldownPath = Path.Combine(_tempRoot, "feedback-last-submit.txt");
        Assert.True(File.Exists(cooldownPath));
        Assert.Equal(
            startTime.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            File.ReadAllText(cooldownPath).Trim());

        utcNow = startTime.AddMinutes(5).AddSeconds(1);
        FeedbackService restarted = CreateService(handler, () => utcNow);
        DeskBoxFeedbackSubmissionResult result = await restarted.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "Submitted right after the window elapsed.");

        Assert.True(result.Ok);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Submit_RecoversByRotatingClientIdWhenServerRateStateIsStale()
    {
        // Regression for #449: a wedged server-side rate state keyed on the
        // permanent anonymous id must not lock the user out forever. Once the
        // client's own success-only record proves the server-claimed window
        // elapsed long ago, the id rotates once and the submission retries.
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.Created, """{"ok":true,"id":100}"""),
            Json(HttpStatusCode.TooManyRequests, """{"ok":false,"error":"rate_limited","retry_after_minutes":10}"""),
            Json(HttpStatusCode.Created, """{"ok":true,"id":101}"""),
        });
        var handler = new RecordingHandler(_ => responses.Dequeue());
        DateTimeOffset utcNow = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        FeedbackService service = CreateService(handler, () => utcNow);

        Assert.True((await service.SubmitAsync(DeskBoxFeedbackKind.Bug, "The first report goes through.")).Ok);

        // Days later the server still claims a 10-minute window: provably stale.
        utcNow = utcNow.AddDays(2);
        DeskBoxFeedbackSubmissionResult recovered = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "The widget loses its pinned corner.");

        Assert.True(recovered.Ok);
        Assert.Equal(101, recovered.Id);
        Assert.Equal(3, handler.Requests.Count);
        string originalId = BodyClientId(handler.Requests[0]);
        Assert.Equal(originalId, BodyClientId(handler.Requests[1]));
        Assert.NotEqual(originalId, BodyClientId(handler.Requests[2]));

        // The recovered success re-arms the cooldown, again success-only.
        DeskBoxFeedbackSubmissionResult blocked = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "An immediate follow-up is blocked.");
        Assert.False(blocked.Ok);
        Assert.Equal("rate_limited", blocked.Error);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Submit_HonorsServerWindowWhileClaimIsStillPlausible()
    {
        // No rotation while the server's claimed window has not provably
        // elapsed: a longer legitimate window (e.g. 37 minutes) is surfaced,
        // the anonymous id is kept, and exactly one request is made.
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.Created, """{"ok":true,"id":5}"""),
            Json(HttpStatusCode.TooManyRequests, """{"ok":false,"error":"rate_limited","retry_after_minutes":37}"""),
        });
        var handler = new RecordingHandler(_ => responses.Dequeue());
        DateTimeOffset utcNow = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        FeedbackService service = CreateService(handler, () => utcNow);

        Assert.True((await service.SubmitAsync(DeskBoxFeedbackKind.Bug, "Earlier successful report.")).Ok);

        utcNow = utcNow.AddMinutes(20);
        DeskBoxFeedbackSubmissionResult limited = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "Still inside the server's claimed window.");

        Assert.False(limited.Ok);
        Assert.Equal(37, limited.RetryAfterMinutes);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(BodyClientId(handler.Requests[0]), BodyClientId(handler.Requests[1]));
    }

    [Fact]
    public async Task Cooldown_UnreadableOrImplausibleRecordNeverLocksOut()
    {
        // A corrupt or far-future cooldown record must read as absent, never
        // as "just submitted" — parse failures cannot wedge the user (#449).
        var handler = new RecordingHandler(_ =>
            Json(HttpStatusCode.Created, """{"ok":true,"id":81}"""));
        DateTimeOffset utcNow = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        string cooldownPath = Path.Combine(_tempRoot, "feedback-last-submit.txt");
        await File.WriteAllTextAsync(cooldownPath, "not-a-timestamp");

        FeedbackService service = CreateService(handler, () => utcNow);
        Assert.True((await service.SubmitAsync(DeskBoxFeedbackKind.Bug, "Submitted with corrupt state.")).Ok);

        long farFuture = utcNow.AddYears(1).ToUnixTimeSeconds();
        await File.WriteAllTextAsync(cooldownPath, farFuture.ToString());

        FeedbackService futureRecord = CreateService(handler, () => utcNow);
        DeskBoxFeedbackSubmissionResult result = await futureRecord.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "Submitted despite an implausible record.");

        Assert.True(result.Ok);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static string BodyClientId(RecordedRequest request) =>
        JsonDocument.Parse(request.Body!).RootElement.GetProperty("client_id").GetString()!;

    [Fact]
    public async Task ClientId_IsPersistedAndStableAcrossServiceInstances()
    {
        var handler = new RecordingHandler(_ =>
            Json(HttpStatusCode.Created, """{"ok":true,"id":9}"""));
        DateTimeOffset utcNow = new(2026, 9, 1, 8, 0, 0, TimeSpan.Zero);
        FeedbackService first = CreateService(handler, () => utcNow);
        await first.SubmitAsync(DeskBoxFeedbackKind.Bug, "First submission body text.");

        // A fresh instance simulates a restart; the clock advances past the
        // cooldown window so the second submission is legitimately allowed.
        utcNow = utcNow.AddMinutes(11);
        FeedbackService second = CreateService(handler, () => utcNow);
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

    [Fact]
    public async Task Submit_DailyCapRateLimitDoesNotRotateClientIdWithinTwentyFourHours()
    {
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.Created, """{"ok":true,"id":200}"""),
            Json(HttpStatusCode.TooManyRequests, """{"ok":false,"error":"rate_limited","retry_after_minutes":60}"""),
            Json(HttpStatusCode.TooManyRequests, """{"ok":false,"error":"rate_limited","retry_after_minutes":60}"""),
            Json(HttpStatusCode.Created, """{"ok":true,"id":201}"""),
        });
        var handler = new RecordingHandler(_ => responses.Dequeue());
        DateTimeOffset utcNow = new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);
        FeedbackService service = CreateService(handler, () => utcNow);

        Assert.True((await service.SubmitAsync(DeskBoxFeedbackKind.Bug, "Tenth daily submission body.")).Ok);
        string originalClientId = service.ClientId;

        // 90 minutes later: inside the 24-hour daily cap window, so ClientId must NOT rotate.
        utcNow = utcNow.AddMinutes(90);
        DeskBoxFeedbackSubmissionResult dailyCapped = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "Eleventh submission inside 24h daily cap.");
        Assert.False(dailyCapped.Ok);
        Assert.Equal(60, dailyCapped.RetryAfterMinutes);
        Assert.Equal(originalClientId, service.ClientId);
        Assert.Equal(2, handler.Requests.Count);

        // 25 hours after the last recorded success: server claiming 60m is now provably stale -> rotates and recovers.
        utcNow = utcNow.AddHours(24);
        DeskBoxFeedbackSubmissionResult recovered = await service.SubmitAsync(
            DeskBoxFeedbackKind.Bug,
            "Submission after 25h recovers from wedged server state.");
        Assert.True(recovered.Ok);
        Assert.Equal(201, recovered.Id);
        Assert.NotEqual(originalClientId, service.ClientId);
    }

    [Fact]
    public async Task GetMyFeedback_ParsesMultiTurnMessagesAndPostReplyAppendsFollowUp()
    {
        const string mineBody = """
            {"ok":true,"items":[
              {"id":88,"type":"suggestion","status":"in_progress","content":"Support multi-turn feedback thread.",
               "reply":"Shipped in 1.6.0","app_version":"1.6.0","channel":"direct",
               "created_at":"2026-11-04T08:00:00Z",
               "messages":[
                 {"id":501,"author_role":"official","author_nickname":"DeskBox Team","content":"Can you share your scaling setting?","created_at":"2026-11-04T08:05:00Z"},
                 {"id":502,"author_role":"op","author_nickname":null,"content":"150% on primary 4K display.","created_at":"2026-11-04T08:10:00Z"}
               ]}
            ]}
            """;
        var responses = new Queue<HttpResponseMessage>(new[]
        {
            Json(HttpStatusCode.OK, mineBody),
            Json(HttpStatusCode.Created, """{"ok":true,"message":{"id":503,"author_role":"op","author_nickname":null,"content":"Tested and confirmed!","created_at":"2026-11-04T08:15:00Z"}}"""),
            Json(HttpStatusCode.TooManyRequests, """{"ok":false,"error":"rate_limited","retry_after_minutes":1}"""),
        });
        var handler = new RecordingHandler(_ => responses.Dequeue());
        FeedbackService service = CreateService(handler);

        DeskBoxFeedbackMineResult mine = await service.GetMyFeedbackAsync();
        Assert.True(mine.Ok);
        DeskBoxFeedbackItem item = Assert.Single(mine.Items);
        Assert.NotNull(item.Messages);
        Assert.Equal(2, item.Messages!.Count);
        Assert.Equal("official", item.Messages[0].AuthorRole);
        Assert.Equal("op", item.Messages[1].AuthorRole);

        // Short reply (< 2 chars) is rejected locally without calling the API.
        DeskBoxFeedbackReplyResult tooShort = await service.PostReplyAsync(88, "a");
        Assert.False(tooShort.Ok);
        Assert.Equal("content_too_short", tooShort.Error);
        Assert.Single(handler.Requests);

        // Valid follow-up reply posts to /feedback/88/messages.
        DeskBoxFeedbackReplyResult posted = await service.PostReplyAsync(88, "  Tested and confirmed!  ");
        Assert.True(posted.Ok);
        Assert.NotNull(posted.Message);
        Assert.Equal(503, posted.Message!.Id);
        Assert.Equal("Tested and confirmed!", posted.Message.Content);
        Assert.Equal("https://example.test/stats/api/feedback/88/messages", handler.Requests[1].Uri);
        using (JsonDocument replyDoc = JsonDocument.Parse(handler.Requests[1].Body!))
        {
            Assert.Equal(service.ClientId, replyDoc.RootElement.GetProperty("client_id").GetString());
            Assert.Equal("Tested and confirmed!", replyDoc.RootElement.GetProperty("content").GetString());
        }

        // 429 rate-limit on rapid follow-up surfaces retry_after_minutes.
        DeskBoxFeedbackReplyResult rateLimited = await service.PostReplyAsync(88, "Another rapid reply");
        Assert.False(rateLimited.Ok);
        Assert.Equal("rate_limited", rateLimited.Error);
        Assert.Equal(1, rateLimited.RetryAfterMinutes);
    }

    private FeedbackService CreateService(
        HttpMessageHandler handler,
        Func<DateTimeOffset>? utcClock = null,
        string? cooldownStateFilePath = null) => new(
        new HttpClient(handler),
        apiBaseAddress: "https://example.test/stats/api/",
        clientStateFilePath: Path.Combine(_tempRoot, "feedback-client-id.txt"),
        channel: "store",
        localeProvider: () => "zh-CN",
        appVersion: "1.5.1",
        cooldownStateFilePath: cooldownStateFilePath ?? Path.Combine(_tempRoot, "feedback-last-submit.txt"),
        utcClock: utcClock);

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
