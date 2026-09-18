using System.IO.Compression;
using System.Net;
using System.Text;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// PR-2 transport + orchestration (roadmap §10): WebDAV request shapes via a
/// recording handler, and CloudBackupService end-to-end over a fake
/// transport — scoped export, device-suffixed remote name, retention,
/// scheduling gate, traversal defense. No real network anywhere.
/// </summary>
public sealed class CloudBackupTransportTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _appDataRoot;

    public CloudBackupTransportTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        _appDataRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "app-data")).FullName;
    }

    // ── WebDAV request shapes ───────────────────────────────────────────

    [Fact]
    public async Task Probe_SendsPropfindDepth0_WithBasicAuth()
    {
        var handler = new RecordingHandler(_ => XmlResponse(HttpStatusCode.MultiStatus));
        var transport = new WebDavBackupTransport(Options(), handler);

        await transport.ProbeAsync();

        RecordingHandler.Captured request = Assert.Single(handler.Requests);
        Assert.Equal("PROPFIND", request.Method);
        Assert.Equal("0", request.Headers["Depth"]);
        Assert.StartsWith("Basic ", request.Headers["Authorization"]);
        Assert.Equal("https://dav.example.com/dav/", request.Uri);
    }

    [Fact]
    public async Task List_ParsesMultistatus_SkipsCollectionSelf()
    {
        const string multistatus =
            """
            <?xml version="1.0"?>
            <D:multistatus xmlns:D="DAV:">
              <D:response>
                <D:href>/dav/DeskBox/backups/</D:href>
                <D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop>
                <D:status>HTTP/1.1 200 OK</D:status></D:propstat>
              </D:response>
              <D:response>
                <D:href>/dav/DeskBox/backups/DeskBox-CloudBackup-20260918-2100-abcdef12.zip</D:href>
                <D:propstat><D:prop>
                  <D:displayname>forged-displayname.zip</D:displayname>
                  <D:getcontentlength>4096</D:getcontentlength>
                  <D:getlastmodified>Fri, 18 Sep 2026 13:00:00 GMT</D:getlastmodified>
                  <D:resourcetype/>
                </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
              </D:response>
              <D:response>
                <D:href>/dav/DeskBox/backups/subdir/</D:href>
                <D:propstat><D:prop>
                  <D:resourcetype><D:collection/></D:resourcetype>
                </D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat>
              </D:response>
            </D:multistatus>
            """;
        var handler = new RecordingHandler(_ => XmlResponse(HttpStatusCode.MultiStatus, multistatus));
        var transport = new WebDavBackupTransport(Options(), handler);

        IReadOnlyList<CloudBackupRemoteEntry> entries = await transport.ListAsync("DeskBox/backups");

        Assert.Equal(2, entries.Count);
        CloudBackupRemoteEntry file = entries[0];
        // Name is derived from the href's last segment — the server's
        // displayname is decoration and must never be used for addressing.
        Assert.Equal("DeskBox-CloudBackup-20260918-2100-abcdef12.zip", file.Name);
        Assert.Equal(4096, file.Length);
        Assert.False(file.IsCollection);
        Assert.NotNull(file.LastModified);
        Assert.True(entries[1].IsCollection);

        RecordingHandler.Captured request = Assert.Single(handler.Requests);
        Assert.Equal("PROPFIND", request.Method);
        Assert.Equal("1", request.Headers["Depth"]);
    }

    [Fact]
    public async Task EnsureDirectory_MkcolProgressively_ToleratesExisting()
    {
        var handler = new RecordingHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/backups", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.Created)
                : new HttpResponseMessage(HttpStatusCode.MethodNotAllowed));
        var transport = new WebDavBackupTransport(Options(), handler);

        await transport.EnsureDirectoryAsync("DeskBox/backups");

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("MKCOL", r.Method));
        Assert.Equal("https://dav.example.com/dav/DeskBox", handler.Requests[0].Uri);
        Assert.Equal("https://dav.example.com/dav/DeskBox/backups", handler.Requests[1].Uri);
    }

    [Fact]
    public async Task Upload_PutsStream_EscapesRemoteSegments()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var transport = new WebDavBackupTransport(Options(), handler);

        byte[] payload = Encoding.UTF8.GetBytes("zip-bytes");
        await transport.UploadAsync("DeskBox/backups/my file.zip", new MemoryStream(payload));

        RecordingHandler.Captured request = Assert.Single(handler.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("https://dav.example.com/dav/DeskBox/backups/my%20file.zip", request.Uri);
        Assert.Equal(payload, request.ContentBytes);
    }

    [Fact]
    public async Task Download_WritesToCallerDestination()
    {
        byte[] payload = Encoding.UTF8.GetBytes("remote-zip");
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            });
        var transport = new WebDavBackupTransport(Options(), handler);

        using var destination = new MemoryStream();
        await transport.DownloadAsync("DeskBox/backups/a.zip", destination);

        Assert.Equal(payload, destination.ToArray());
        Assert.Equal("GET", Assert.Single(handler.Requests).Method);
    }

    [Fact]
    public async Task Delete_ToleratesNotFound()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        var transport = new WebDavBackupTransport(Options(), handler);

        await transport.DeleteAsync("DeskBox/backups/gone.zip");
        Assert.Equal("DELETE", Assert.Single(handler.Requests).Method);
    }

    [Fact]
    public async Task HttpFailure_ThrowsTransportException_WithoutLeakingSecret()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var transport = new WebDavBackupTransport(Options(), handler);

        CloudBackupTransportException ex = await Assert.ThrowsAsync<CloudBackupTransportException>(
            () => transport.ListAsync("DeskBox/backups"));

        Assert.Equal(HttpStatusCode.Unauthorized, ex.StatusCode);
        Assert.DoesNotContain("s3cret", ex.Message);
        Assert.DoesNotContain("s3cret", ex.ToString());
    }

    // ── Orchestration ───────────────────────────────────────────────────

    [Fact]
    public async Task RunBackupNow_UploadsScopedArchive_WithDeviceSuffix()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(scope: CloudBackupDomain.TodoData));

        CloudBackupRunResult result = await service.RunBackupNowAsync();

        Assert.True(result.Uploaded);
        string remotePath = Assert.IsType<string>(result.RemoteFilePath);
        string fileName = remotePath.Split('/').Last();
        Assert.StartsWith(CloudBackupService.SnapshotFilePrefix, fileName);
        Assert.EndsWith($"-{DeviceIdentity.Id[..8]}.zip", fileName);

        byte[] uploaded = Assert.Single(transport.Files).Value;
        using var zip = new ZipArchive(new MemoryStream(uploaded), ZipArchiveMode.Read);
        Assert.Contains(zip.Entries, e => e.FullName == "manifest.json");
        Assert.Contains(zip.Entries, e => e.FullName.StartsWith("data/widgets/", StringComparison.Ordinal));
        // Todo only — no style document shipped.
        Assert.DoesNotContain(zip.Entries, e => e.FullName == "widget-style.json");
        Assert.True(settings.Settings.CloudBackupLastSuccessUtcTicks > 0);
    }

    [Fact]
    public async Task RunBackupNow_PrunesRemoteBeyondRetention()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport();
        for (int i = 1; i <= 6; i++)
        {
            transport.Files[$"DeskBox/backups/DeskBox-CloudBackup-2026010{i}-000000-deadbeef.zip"] =
                [0x50, 0x4B];
        }

        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(retention: 3));

        await service.RunBackupNowAsync();

        Assert.Equal(3, transport.Files.Count);
        Assert.All(transport.Files.Keys,
            k => Assert.StartsWith("DeskBox/backups/DeskBox-CloudBackup-", k));
        // Newest three survive: the fresh upload plus the two most recent
        // preexisting snapshots by name order.
        Assert.Contains(transport.Files.Keys,
            k => k.Contains("20260106"));
        Assert.Contains(transport.Files.Keys,
            k => k.Contains("20260105"));
        Assert.DoesNotContain(transport.Files.Keys,
            k => k.Contains("20260101"));
    }

    [Fact]
    public async Task RunScheduledIfDue_SkipsWhenRecentlySucceeded()
    {
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(
            lastSuccess: DateTimeOffset.UtcNow.AddMinutes(-5),
            intervalMinutes: 60));

        await service.RunScheduledIfDueAsync();
        Assert.Empty(transport.Files);
    }

    [Fact]
    public async Task RunScheduledIfDue_RunsWhenNeverSucceeded()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(lastSuccess: DateTimeOffset.MinValue));

        await service.RunScheduledIfDueAsync();
        Assert.Single(transport.Files);
    }

    [Fact]
    public async Task RunScheduledIfDue_SwallowsTransportFailure()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport { FailOn = _ => true };
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());

        await service.RunScheduledIfDueAsync(); // must not throw
        Assert.Empty(transport.Files);
    }

    [Fact]
    public async Task RunScheduledIfDue_SkipsWhileAnotherRunIsInFlight()
    {
        SeedTodoData();
        var uploadStarted = new TaskCompletionSource();
        var releaseUpload = new TaskCompletionSource();
        var transport = new FakeCloudBackupTransport
        {
            UploadHook = _ =>
            {
                uploadStarted.TrySetResult();
                return releaseUpload.Task;
            }
        };
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(lastSuccess: DateTimeOffset.MinValue));

        Task first = service.RunScheduledIfDueAsync();
        await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // A timer tick landing while an upload holds the gate must skip,
        // not queue a second (stale-options) run behind it.
        await service.RunScheduledIfDueAsync();

        releaseUpload.SetResult();
        await first;
        await Task.Delay(50); // any wrongly queued run would surface here
        Assert.Single(transport.Files);
    }

    [Fact]
    public async Task RunBackupNow_NeverDeletesUnsafeListedNames()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport();
        // A hostile/broken server can list names that pass the loose prefix
        // check but would traverse out of the backup directory on DELETE.
        transport.Files["DeskBox/backups/DeskBox-CloudBackup-../../other.zip"] = [0x1];

        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(retention: 1));

        await service.RunBackupNowAsync();

        // The forged entry is filtered before addressing: it is neither
        // deleted nor counted against retention — only the fresh upload
        // is a real snapshot.
        Assert.Equal(2, transport.Files.Count);
        Assert.True(transport.Files.ContainsKey(
            "DeskBox/backups/DeskBox-CloudBackup-../../other.zip"));
    }

    [Fact]
    public async Task ProbeConnection_UsesTypedSecretOverStoredOne()
    {
        var transport = new FakeCloudBackupTransport();
        var backup = new DeskBoxDataBackupService(_appDataRoot);
        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var credentials = new InMemoryCredentialStore();
        string? usedSecret = null;
        var service = new CloudBackupService(
            backup, settings, credentials,
            (options, secret) =>
            {
                usedSecret = secret;
                return transport;
            });
        service.UpdateOptions(ConfiguredOptions());

        await service.ProbeConnectionAsync("just-typed");

        Assert.Equal("just-typed", usedSecret);
        Assert.True(transport.Probed);
    }

    [Fact]
    public async Task RunBackupNow_RemoteNameIsUtcSuffixed()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());

        CloudBackupRunResult result = await service.RunBackupNowAsync();

        string fileName = result.RemoteFilePath!.Split('/').Last();
        Assert.Matches(@"^DeskBox-CloudBackup-\d{8}T\d{6}Z-.{8}\.zip$", fileName);
    }

    [Fact]
    public void UpdateOptions_DestinationChange_ResetsLastSuccess()
    {
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        DateTimeOffset success = DateTimeOffset.UtcNow.AddHours(-2);
        service.UpdateOptions(ConfiguredOptions(lastSuccess: success));
        Assert.Equal(success, service.Options.LastSuccessUtc);

        // Switching the endpoint invalidates the previous success —
        // otherwise the new destination could go unbacked-up for a full
        // interval while the UI claims a recent success.
        service.UpdateOptions(ConfiguredOptions(lastSuccess: success) with
        {
            ServerUrl = "https://other.example.com/"
        });

        Assert.Equal(DateTimeOffset.MinValue, service.Options.LastSuccessUtc);
        Assert.Equal(0, settings.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks);
    }

    [Fact]
    public void UpdateOptions_NonIdentityChange_KeepsLastSuccess()
    {
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        DateTimeOffset success = DateTimeOffset.UtcNow.AddHours(-2);
        service.UpdateOptions(ConfiguredOptions(lastSuccess: success));

        // Interval/retention are preferences, not destination identity.
        service.UpdateOptions(ConfiguredOptions(
            lastSuccess: success,
            intervalMinutes: 360,
            retention: 3));

        Assert.Equal(success, service.Options.LastSuccessUtc);
    }

    [Fact]
    public void UpdateOptions_ScopeExpansion_ResetsLastSuccess()
    {
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        DateTimeOffset success = DateTimeOffset.UtcNow.AddHours(-2);
        service.UpdateOptions(ConfiguredOptions(lastSuccess: success));

        // Turning on a new domain must produce a fresh backup soon —
        // otherwise the new domain can sit unbacked-up for a full interval.
        service.UpdateOptions(ConfiguredOptions(
            lastSuccess: success,
            scope: CloudBackupDomain.TodoData | CloudBackupDomain.QuickCaptureData));

        Assert.Equal(DateTimeOffset.MinValue, service.Options.LastSuccessUtc);
    }

    [Fact]
    public void UpdateOptions_FirstConfiguredPush_KeepsPersistedSuccess()
    {
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        DateTimeOffset success = DateTimeOffset.UtcNow.AddHours(-2);

        // The first configured push after startup must keep the value
        // loaded from settings.json — it is not a destination switch.
        service.UpdateOptions(ConfiguredOptions(lastSuccess: success));

        Assert.Equal(success, service.Options.LastSuccessUtc);
    }

    [Fact]
    public async Task RunBackupNow_NotConfigured_ReturnsSkipped()
    {
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(provider: "none"));

        CloudBackupRunResult result = await service.RunBackupNowAsync();

        Assert.False(result.Uploaded);
        Assert.Empty(transport.Files);
    }

    [Fact]
    public async Task DownloadSnapshot_RejectsTraversal()
    {
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.DownloadSnapshotAsync("../evil.zip", _tempRoot));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.DownloadSnapshotAsync("a/b.zip", _tempRoot));
    }

    [Fact]
    public async Task ListRemoteSnapshots_ReturnsOnlySnapshotZips_NewestFirst()
    {
        var transport = new FakeCloudBackupTransport();
        transport.Files["DeskBox/backups/DeskBox-CloudBackup-20260101-000000-aaaabbbb.zip"] = [0x1];
        transport.Files["DeskBox/backups/DeskBox-CloudBackup-20260102-000000-aaaabbbb.zip"] = [0x1];
        transport.Files["DeskBox/backups/unrelated.txt"] = [0x1];
        transport.Files["DeskBox/backups/DeskBox-Backup-20260101-0000.zip"] = [0x1];

        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());

        IReadOnlyList<CloudBackupRemoteEntry> snapshots = await service.ListRemoteSnapshotsAsync();

        Assert.Equal(2, snapshots.Count);
        Assert.Contains("20260102", snapshots[0].Name);
        Assert.Contains("20260101", snapshots[1].Name);
    }

    // ── Policy ──────────────────────────────────────────────────────────

    [Fact]
    public void GetOptions_ComposesScopeFromToggles()
    {
        var settings = new AppSettings
        {
            CloudBackupProvider = "webdav",
            CloudBackupServerUrl = "https://dav.example.com/dav/",
            CloudBackupTodoDataEnabled = true,
            CloudBackupQuickCaptureDataEnabled = false,
            CloudBackupWidgetStyleEnabled = true
        };

        CloudBackupOptions options = CloudBackupSettingsPolicy.GetOptions(settings);

        Assert.True(options.IsConfigured);
        Assert.Equal(
            CloudBackupDomain.TodoData | CloudBackupDomain.WidgetStyle,
            options.Scope);
        Assert.Equal(5, options.RetentionCount);
        Assert.Equal("DeskBox/backups", options.RemotePath);
    }

    [Fact]
    public void CredentialKey_ScopesByOriginAndUser()
    {
        CloudBackupOptions options = ConfiguredOptions(username: "alice");
        Assert.Equal("webdav:alice@https://dav.example.com",
            CloudBackupSettingsPolicy.CredentialKey(options));
    }

    [Fact]
    public void CredentialKey_DistinguishesSchemeAndPort()
    {
        string https = CloudBackupSettingsPolicy.CredentialKey(
            ConfiguredOptions());
        string http = CloudBackupSettingsPolicy.CredentialKey(
            ConfiguredOptions() with { ServerUrl = "http://dav.example.com/" });
        string port8443 = CloudBackupSettingsPolicy.CredentialKey(
            ConfiguredOptions() with { ServerUrl = "https://dav.example.com:8443/" });

        Assert.Equal("webdav:alice@http://dav.example.com", http);
        Assert.Equal("webdav:alice@https://dav.example.com:8443", port8443);
        Assert.NotEqual(https, http);
        Assert.NotEqual(https, port8443);
    }

    [Fact]
    public void CredentialKey_DistinguishesBasePath()
    {
        // One host can reverse-proxy several DAV services/tenants — the
        // wrong tenant must never receive a stored secret.
        string tenantA = CloudBackupSettingsPolicy.CredentialKey(
            ConfiguredOptions() with { ServerUrl = "https://example.com/dav-a/" });
        string tenantB = CloudBackupSettingsPolicy.CredentialKey(
            ConfiguredOptions() with { ServerUrl = "https://example.com/dav-b/" });

        Assert.Equal("webdav:alice@https://example.com/dav-a", tenantA);
        Assert.Equal("webdav:alice@https://example.com/dav-b", tenantB);
        Assert.NotEqual(tenantA, tenantB);
    }

    [Fact]
    public void UsesPlainHttp_FlagsInsecureEndpoint()
    {
        Assert.False(ConfiguredOptions().UsesPlainHttp);
        Assert.True((ConfiguredOptions() with { ServerUrl = "http://nas.local/" })
            .UsesPlainHttp);
    }

    [Fact]
    public void Settings_NeverSerializesAPasswordField()
    {
        string json = System.Text.Json.JsonSerializer.Serialize(new AppSettings());
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
    }

    // ── Fixture helpers ─────────────────────────────────────────────────

    private static WebDavBackupTransport.Options Options() =>
        // Real providers (Nextcloud, Jianguoyun) mount DAV under a subpath.
        new(new Uri("https://dav.example.com/dav/"), "alice", "s3cret");

    private static HttpResponseMessage XmlResponse(HttpStatusCode status, string body = "<D:multistatus xmlns:D=\"DAV:\"/>") =>
        new(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/xml")
        };

    private void SeedTodoData()
    {
        string widgetDir = Directory.CreateDirectory(
            Path.Combine(_appDataRoot, "data", "widgets", "todo-widget")).FullName;
        File.WriteAllText(Path.Combine(widgetDir, "todo.json"),
            """[{"id":"t1","text":"ship it"}]""");
    }

    private static CloudBackupOptions ConfiguredOptions(
        string provider = "webdav",
        string username = "alice",
        CloudBackupDomain scope = CloudBackupDomain.TodoData,
        int retention = 5,
        int intervalMinutes = 24 * 60,
        DateTimeOffset? lastSuccess = null) =>
        new(
            Provider: provider,
            ServerUrl: "https://dav.example.com/",
            RemotePath: "DeskBox/backups",
            Username: username,
            Scope: scope,
            RetentionCount: retention,
            IntervalMinutes: intervalMinutes,
            LastSuccessUtc: lastSuccess ?? DateTimeOffset.MinValue);

    private (CloudBackupService Service, SettingsService Settings) CreateService(
        FakeCloudBackupTransport transport)
    {
        var backup = new DeskBoxDataBackupService(_appDataRoot);
        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var credentials = new InMemoryCredentialStore();
        var service = new CloudBackupService(
            backup, settings, credentials,
            (options, secret) => transport);
        return (service, settings);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Records every request + captures content for assertion.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal sealed record Captured(
            string Method,
            string Uri,
            Dictionary<string, string> Headers,
            byte[]? ContentBytes);

        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        internal RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        internal List<Captured> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IEnumerable<string>> header in request.Headers)
            {
                headers[header.Key] = string.Join(",", header.Value);
            }

            byte[]? content = request.Content is null
                ? null
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new Captured(
                request.Method.Method,
                request.RequestUri!.AbsoluteUri,
                headers,
                content));
            return _responder(request);
        }
    }

    /// <summary>In-memory remote: file dictionary + call recording.</summary>
    private sealed class FakeCloudBackupTransport : ICloudBackupTransport
    {
        internal Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        internal List<string> Directories { get; } = [];
        internal Func<string, bool>? FailOn { get; init; }
        internal Func<string, Task>? UploadHook { get; init; }
        internal bool Probed { get; private set; }

        public Task ProbeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfFailed("probe");
            Probed = true;
            return Task.CompletedTask;
        }

        public Task EnsureDirectoryAsync(string remoteDirectory, CancellationToken cancellationToken = default)
        {
            ThrowIfFailed(remoteDirectory);
            Directories.Add(remoteDirectory);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<CloudBackupRemoteEntry>> ListAsync(
            string remoteDirectory, CancellationToken cancellationToken = default)
        {
            ThrowIfFailed(remoteDirectory);
            string prefix = remoteDirectory.TrimEnd('/') + "/";
            IReadOnlyList<CloudBackupRemoteEntry> entries = Files.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Select(k => new CloudBackupRemoteEntry(k[prefix.Length..], (long?)Files[k].Length, null, false))
                .ToList();
            return Task.FromResult(entries);
        }

        public async Task UploadAsync(string remoteFilePath, Stream content, CancellationToken cancellationToken = default)
        {
            ThrowIfFailed(remoteFilePath);
            if (UploadHook is not null)
            {
                await UploadHook(remoteFilePath);
            }

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            Files[remoteFilePath] = buffer.ToArray();
        }

        public Task DownloadAsync(string remoteFilePath, Stream destination, CancellationToken cancellationToken = default)
        {
            ThrowIfFailed(remoteFilePath);
            if (!Files.TryGetValue(remoteFilePath, out byte[]? data))
            {
                throw new CloudBackupTransportException("not found", HttpStatusCode.NotFound);
            }

            return destination.WriteAsync(data, cancellationToken).AsTask();
        }

        public Task DeleteAsync(string remoteFilePath, CancellationToken cancellationToken = default)
        {
            ThrowIfFailed(remoteFilePath);
            Files.Remove(remoteFilePath);
            return Task.CompletedTask;
        }

        private void ThrowIfFailed(string operation)
        {
            if (FailOn?.Invoke(operation) == true)
            {
                throw new CloudBackupTransportException($"simulated failure on {operation}");
            }
        }
    }

    private sealed class InMemoryCredentialStore : ICredentialStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public Task<string?> GetSecretAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.TryGetValue(key, out string? value) ? value : null);

        public Task SetSecretAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            _secrets[key] = secret;
            return Task.CompletedTask;
        }

        public Task RemoveSecretAsync(string key, CancellationToken cancellationToken = default)
        {
            _secrets.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<string>> ListKeysAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(_secrets.Keys.ToList());
    }
}
