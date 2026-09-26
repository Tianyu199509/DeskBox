using System.IO.Compression;
using System.Net;
using System.Text;
using DeskBox.Models;
using DeskBox.Services;

using DeskBox.Contracts;

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
    public async Task List_ParsesJianguoyunShapedMultistatus()
    {
        // Captured from dav.jianguoyun.com: lowercase prefix, vendor namespace,
        // collection href WITHOUT a trailing slash, empty resourcetype for files.
        const string multistatus =
            """
            <?xml version="1.0" encoding="UTF-8" standalone="no"?><d:multistatus xmlns:d="DAV:" xmlns:s="http://ns.jianguoyun.com"><d:response><d:href>/dav/DeskBox/backups</d:href><d:propstat><d:prop><d:getetag/><d:getcontenttype>httpd/unix-directory</d:getcontenttype><d:displayname>backups</d:displayname><d:getcontentlength>0</d:getcontentlength><d:getlastmodified>Sat, 19 Sep 2026 13:10:00 GMT</d:getlastmodified><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response><d:response><d:href>/dav/DeskBox/backups/DeskBox-CloudBackup-20260920T162455Z-48221e43.zip</d:href><d:propstat><d:prop><d:getetag>rErap3U7trvdPsbTKxpZxg</d:getetag><d:getcontenttype>application/zip</d:getcontenttype><d:displayname>DeskBox-CloudBackup-20260920T162455Z-48221e43.zip</d:displayname><d:getcontentlength>18282</d:getcontentlength><d:getlastmodified>Sun, 20 Sep 2026 16:24:54 GMT</d:getlastmodified><d:resourcetype/></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>
            """;
        var handler = new RecordingHandler(_ => XmlResponse(HttpStatusCode.MultiStatus, multistatus));
        var transport = new WebDavBackupTransport(Options(), handler);

        IReadOnlyList<CloudBackupRemoteEntry> entries = await transport.ListAsync("DeskBox/backups");

        CloudBackupRemoteEntry file = Assert.Single(entries);
        Assert.Equal("DeskBox-CloudBackup-20260920T162455Z-48221e43.zip", file.Name);
        Assert.Equal(18282, file.Length);
        Assert.False(file.IsCollection);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 16, 24, 54, TimeSpan.Zero), file.LastModified);
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
    public async Task RunScheduledIfDue_SkipsWhileRestoreIsPending()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(lastSuccess: DateTimeOffset.MinValue));

        // A pending restore replaces the staged domains on the next
        // restart — uploading the about-to-be-replaced state must not run.
        var markerProbe = new DeskBoxDataBackupService(_appDataRoot);
        File.WriteAllText(markerProbe.PendingRestoreMarkerPath, "{}");

        await service.RunScheduledIfDueAsync();
        Assert.Empty(transport.Files);
    }

    [Fact]
    public async Task RunScheduledIfDue_SkipsWhenCredentialMissing()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport, seedCredential: false);
        service.UpdateOptions(ConfiguredOptions(lastSuccess: DateTimeOffset.MinValue));

        // Configured but no stored credential must not turn every timer
        // tick into an anonymous 401 loop.
        await service.RunScheduledIfDueAsync();
        Assert.Empty(transport.Files);
    }

    [Fact]
    public async Task RunBackupNow_MissingCredential_ReturnsNamedResult()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport, seedCredential: false);
        service.UpdateOptions(ConfiguredOptions());

        CloudBackupRunResult result = await service.RunBackupNowAsync();

        Assert.False(result.Uploaded);
        Assert.True(result.NoCredential);
        Assert.Empty(transport.Files);
    }

    [Fact]
    public async Task RunBackupNow_SkipsWhileRestoreIsPending()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());

        // The manual path gets the same gate as the scheduled one — a
        // user click must not push state a staged restore will replace.
        var markerProbe = new DeskBoxDataBackupService(_appDataRoot);
        File.WriteAllText(markerProbe.PendingRestoreMarkerPath, "{}");

        CloudBackupRunResult result = await service.RunBackupNowAsync();

        Assert.False(result.Uploaded);
        Assert.True(result.RestorePending);
        Assert.Empty(transport.Files);
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
    public async Task RunBackupNow_WhenAnotherRunInFlight_ReturnsAlreadyInProgress()
    {
        // The manual path must refuse to queue behind an in-flight upload —
        // blocking would freeze the settings section for the whole upload.
        var started = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        var transport = new FakeCloudBackupTransport
        {
            UploadHook = async _ =>
            {
                started.TrySetResult();
                await release.Task;
            }
        };
        SeedTodoData();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());

        Task<CloudBackupRunResult> first = service.RunBackupNowAsync();
        await started.Task;

        CloudBackupRunResult second = await service.RunBackupNowAsync();

        Assert.True(second.AlreadyInProgress);
        Assert.False(second.Uploaded);

        release.SetResult();
        Assert.True((await first).Uploaded);
    }

    [Fact]
    public async Task ScheduledRun_Failure_StampsLastFailure_AndFlagsStreakStart()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport { FailOn = op => op.EndsWith(".zip") };
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());
        var events = new List<CloudBackupRunCompletedInfo>();
        service.BackupRunCompleted += events.Add;

        await service.RunScheduledIfDueAsync();

        Assert.True(settings.Settings.CloudBackup.CloudBackupLastFailureUtcTicks > 0);
        CloudBackupRunCompletedInfo first = Assert.Single(events);
        Assert.False(first.Uploaded);
        Assert.True(first.WasScheduled);
        Assert.True(first.IsFirstFailureSinceSuccess);

        // A second failure in the same streak does not re-flag. The
        // spacing floor is cleared between runs — the throttle itself is
        // pinned by RunScheduledIfDue_SecondAttemptWithinSpacingFloor_IsSkipped.
        service.ResetScheduledAttemptSpacingForTesting();
        await service.RunScheduledIfDueAsync();
        Assert.Equal(2, events.Count);
        Assert.False(events[1].IsFirstFailureSinceSuccess);
    }

    [Fact]
    public async Task RunScheduledIfDue_SecondAttemptWithinSpacingFloor_IsSkipped()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport { FailOn = op => op.EndsWith(".zip") };
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(lastSuccess: DateTimeOffset.MinValue));
        var events = new List<CloudBackupRunCompletedInfo>();
        service.BackupRunCompleted += events.Add;

        await service.RunScheduledIfDueAsync();

        // Still interval-due (the failed attempt stamped no success), but
        // the 10-minute attempt-spacing floor must swallow the same-tick
        // second tick — no second run, no second completion event.
        await service.RunScheduledIfDueAsync();

        CloudBackupRunCompletedInfo failure = Assert.Single(events);
        Assert.False(failure.Uploaded);
        Assert.True(settings.Settings.CloudBackup.CloudBackupLastFailureUtcTicks > 0);
    }

    [Fact]
    public async Task RunBackupNow_Failure_StampsLastFailure_AndRethrows()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport { FailOn = op => op.EndsWith(".zip") };
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());
        var events = new List<CloudBackupRunCompletedInfo>();
        service.BackupRunCompleted += events.Add;

        await Assert.ThrowsAsync<CloudBackupTransportException>(() => service.RunBackupNowAsync());

        Assert.True(settings.Settings.CloudBackup.CloudBackupLastFailureUtcTicks > 0);
        CloudBackupRunCompletedInfo info = Assert.Single(events);
        Assert.False(info.Uploaded);
        Assert.False(info.WasScheduled);
    }

    [Fact]
    public async Task SuccessfulRun_ClearsLastFailureTicks()
    {
        SeedTodoData();
        bool failUploads = true;
        var transport = new FakeCloudBackupTransport
        {
            FailOn = op => failUploads && op.EndsWith(".zip")
        };
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());
        await service.RunScheduledIfDueAsync();
        Assert.True(settings.Settings.CloudBackup.CloudBackupLastFailureUtcTicks > 0);

        failUploads = false;
        CloudBackupRunResult result = await service.RunBackupNowAsync();

        Assert.True(result.Uploaded);
        Assert.Equal(0, settings.Settings.CloudBackup.CloudBackupLastFailureUtcTicks);
    }

    [Fact]
    public async Task BackupRunCompleted_OnManualSuccess_CarriesRemotePath()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());
        var events = new List<CloudBackupRunCompletedInfo>();
        service.BackupRunCompleted += events.Add;

        CloudBackupRunResult result = await service.RunBackupNowAsync();

        CloudBackupRunCompletedInfo info = Assert.Single(events);
        Assert.True(info.Uploaded);
        Assert.False(info.WasScheduled);
        Assert.Equal(result.RemoteFilePath, info.RemoteFilePath);
    }

    [Fact]
    public async Task UpdateOptions_DestinationChange_ClearsFailureTick()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport { FailOn = op => op.EndsWith(".zip") };
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());
        await service.RunScheduledIfDueAsync();
        Assert.True(settings.Settings.CloudBackup.CloudBackupLastFailureUtcTicks > 0);

        // A failure stamped on the old endpoint must not follow the user
        // to a destination that has never run.
        service.UpdateOptions(ConfiguredOptions() with { ServerUrl = "https://other.example.com/" });

        Assert.Equal(0, settings.Settings.CloudBackup.CloudBackupLastFailureUtcTicks);
    }

    [Fact]
    public async Task DeleteRemoteSnapshot_RemovesFile()
    {
        var transport = new FakeCloudBackupTransport();
        transport.Files["DeskBox/backups/DeskBox-CloudBackup-20260910T110000Z-abcd1234.zip"] = [1, 2, 3];
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());

        await service.DeleteRemoteSnapshotAsync("DeskBox-CloudBackup-20260910T110000Z-abcd1234.zip");

        Assert.Empty(transport.Files);
    }

    [Fact]
    public async Task DeleteRemoteSnapshot_RejectsUnsafeName()
    {
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.DeleteRemoteSnapshotAsync("../secrets.zip"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => service.DeleteRemoteSnapshotAsync("sub/dir.zip"));
    }

    [Fact]
    public async Task RunBackupNow_VerificationSizeMismatch_SucceedsUnverified()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport { ReportedLengthOverride = 1 };
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());
        service.VerificationRetryDelayMs = [0, 0, 0];

        CloudBackupRunResult result = await service.RunBackupNowAsync();

        // The PUT was accepted — a lying listing must not convert that
        // into a failure (failure stamp + streak toast + re-upload on
        // the next interval). It degrades to success-with-flag instead.
        Assert.True(result.Uploaded);
        Assert.True(result.UploadUnverified);
        Assert.True(settings.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks > 0);
        Assert.Equal(0, settings.Settings.CloudBackup.CloudBackupLastFailureUtcTicks);
    }

    [Fact]
    public async Task RunBackupNow_VerificationMissing_SucceedsUnverified()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport { ExcludeFromListing = _ => true };
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());
        service.VerificationRetryDelayMs = [0, 0, 0];

        CloudBackupRunResult result = await service.RunBackupNowAsync();

        // A never-listed upload still counts: eventually-consistent DAV
        // backends accept the PUT and lag on listing it.
        Assert.True(result.Uploaded);
        Assert.True(result.UploadUnverified);
    }

    [Fact]
    public async Task RunBackupNow_UnverifiedUpload_IsNotAFailureEvent()
    {
        SeedTodoData();
        var transport = new FakeCloudBackupTransport { ExcludeFromListing = _ => true };
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());
        service.VerificationRetryDelayMs = [0, 0, 0];
        var events = new List<CloudBackupRunCompletedInfo>();
        service.BackupRunCompleted += events.Add;

        CloudBackupRunResult result = await service.RunBackupNowAsync();

        // The degraded run must not look like a failure to the event
        // consumers — IsFirstFailureSinceSuccess drives the streak toast.
        Assert.True(result.Uploaded);
        CloudBackupRunCompletedInfo info = Assert.Single(events);
        Assert.True(info.Uploaded);
        Assert.True(info.UploadUnverified);
        Assert.False(info.IsFirstFailureSinceSuccess);
        Assert.Equal(0, settings.Settings.CloudBackup.CloudBackupLastFailureUtcTicks);
    }

    [Fact]
    public async Task ListRemoteSnapshots_OrdersByEmbeddedTimestampNotName()
    {
        // Raw name order puts 'T' ahead of '-', so a UTC name ALWAYS sorts
        // above a legacy local-time name regardless of actual time. The
        // list must order by the embedded timestamp instead: here the
        // legacy file (12:00) is NEWER than the UTC file (11:00).
        var transport = new FakeCloudBackupTransport();
        transport.Files["DeskBox/backups/DeskBox-CloudBackup-20260910T110000Z-abcd1234.zip"] = [1];
        transport.Files["DeskBox/backups/DeskBox-CloudBackup-20260910-120000.zip"] = [1];
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());

        IReadOnlyList<CloudBackupRemoteEntry> list = await service.ListRemoteSnapshotsAsync();

        Assert.Equal(2, list.Count);
        Assert.Equal("DeskBox-CloudBackup-20260910-120000.zip", list[0].Name);
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
    public void UpdateOptions_UnconfiguredDetour_StillResetsLastSuccess()
    {
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        DateTimeOffset success = DateTimeOffset.UtcNow.AddHours(-2);
        service.UpdateOptions(ConfiguredOptions(lastSuccess: success));

        // configured → unconfigured (all domains off) → configured (a
        // different domain): the middle step fails IsConfigured, but the
        // destination identity changed twice — the original success must
        // not survive the detour.
        service.UpdateOptions(ConfiguredOptions(lastSuccess: success, scope: CloudBackupDomain.None));
        service.UpdateOptions(ConfiguredOptions(lastSuccess: success, scope: CloudBackupDomain.QuickCaptureData));

        Assert.Equal(DateTimeOffset.MinValue, service.Options.LastSuccessUtc);
        Assert.Equal(0, settings.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks);
    }

    [Fact]
    public async Task RunBackup_UploadFinishingAfterDestinationChange_DoesNotStampNewDestination()
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
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(lastSuccess: DateTimeOffset.MinValue));

        Task<CloudBackupRunResult> run = service.RunBackupNowAsync();
        await uploadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // While A's upload is in flight the user repoints the backup to B.
        service.UpdateOptions(ConfiguredOptions(lastSuccess: DateTimeOffset.MinValue) with
        {
            ServerUrl = "https://other.example.com/"
        });

        releaseUpload.SetResult();
        CloudBackupRunResult result = await run;

        // The upload succeeded — against A. Stamping that success onto B
        // would mark a destination that has never produced a backup.
        Assert.True(result.Uploaded);
        Assert.Equal(DateTimeOffset.MinValue, service.Options.LastSuccessUtc);
        Assert.Equal(0, settings.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks);
    }

    [Fact]
    public async Task RunBackupNow_NotConfigured_ReturnsSkipped()
    {
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(provider: "none"));

        CloudBackupRunResult result = await service.RunBackupNowAsync();

        Assert.False(result.Uploaded);
        Assert.False(result.NoScopeSelected);
        Assert.Empty(transport.Files);
    }

    /// <summary>
    /// Endpoint set but every domain toggle off — a valid state while the
    /// user is still configuring. Backup-now must name the gap instead of
    /// reporting a generic "not configured".
    /// </summary>
    [Fact]
    public async Task RunBackupNow_EndpointWithoutScope_ReturnsNoScope()
    {
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(scope: CloudBackupDomain.None));

        CloudBackupRunResult result = await service.RunBackupNowAsync();

        Assert.False(result.Uploaded);
        Assert.True(result.NoScopeSelected);
        Assert.Empty(transport.Files);
    }

    /// <summary>
    /// A connection test validates the endpoint, not the backup selection —
    /// it must work before the user has toggled any domain on.
    /// </summary>
    [Fact]
    public async Task ProbeConnection_EndpointWithoutScope_StillProbes()
    {
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(scope: CloudBackupDomain.None));

        await service.ProbeConnectionAsync("just-typed");

        Assert.True(transport.Probed);
    }

    /// <summary>
    /// Restoring a snapshot on a fresh install predates any backup-scope
    /// choice — listing and downloading must only require a reachable
    /// endpoint (restore domains are picked in the restore dialog).
    /// </summary>
    [Fact]
    public async Task RestoreEndpoints_EndpointWithoutScope_ListAndDownload()
    {
        var transport = new FakeCloudBackupTransport();
        transport.Files["DeskBox/backups/DeskBox-CloudBackup-20260101-000000-aaaabbbb.zip"] = [0x1, 0x2];

        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(scope: CloudBackupDomain.None));

        IReadOnlyList<CloudBackupRemoteEntry> snapshots = await service.ListRemoteSnapshotsAsync();
        string path = await service.DownloadSnapshotAsync(snapshots[0].Name, _tempRoot);

        Assert.Single(snapshots);
        Assert.Equal([0x1, 0x2], await File.ReadAllBytesAsync(path));
    }

    /// <summary>The credential belongs to the endpoint, not the backup scope.</summary>
    [Fact]
    public async Task HasCredential_EndpointWithoutScope_Resolves()
    {
        var transport = new FakeCloudBackupTransport();
        (CloudBackupService service, _) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(scope: CloudBackupDomain.None));

        Assert.True(await service.HasCredentialAsync());
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforeUploadAcceptance_DoesNotStampSuccessOrFailure(bool scheduled)
    {
        SeedTodoData();
        using var cancellation = new CancellationTokenSource();
        var transport = new FakeCloudBackupTransport
        {
            UploadHook = _ => { cancellation.Cancel(); return Task.CompletedTask; }
        };
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());
        var events = new List<CloudBackupRunCompletedInfo>();
        service.BackupRunCompleted += events.Add;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scheduled
            ? service.RunScheduledIfDueAsync(cancellation.Token)
            : service.RunBackupNowAsync(cancellation.Token));
        Assert.Empty(transport.Files);
        Assert.Empty(events);
        Assert.Equal(0, settings.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks);
        Assert.Equal(0, settings.Settings.CloudBackup.CloudBackupLastFailureUtcTicks);
    }

    [Fact]
    public async Task SnapshotTarget_RejectsStaleRow_AndFrozenServiceCallUsesOriginalEndpoint()
    {
        const string name = "DeskBox-CloudBackup-20260910T110000Z-abcd1234.zip";
        var a = new FakeCloudBackupTransport();
        var b = new FakeCloudBackupTransport();
        a.Files[$"DeskBox/backups/{name}"] = [1];
        b.Files[$"DeskBox/backups/{name}"] = [2];
        CloudBackupOptions optionsA = ConfiguredOptions() with { ServerUrl = "https://a.example/dav" };
        CloudBackupOptions optionsB = optionsA with { ServerUrl = "https://b.example/dav" };
        var backup = new DeskBoxDataBackupService(_appDataRoot);
        var settings = new SettingsService(Path.Combine(_tempRoot, "target-settings"));
        settings.Settings.CloudBackup.CloudBackupProvider = optionsA.Provider;
        settings.Settings.CloudBackup.CloudBackupServerUrl = optionsA.ServerUrl;
        settings.Settings.CloudBackup.CloudBackupRemotePath = optionsA.RemotePath;
        settings.Settings.CloudBackup.CloudBackupUsername = optionsA.Username;
        var credentials = new InMemoryCredentialStore();
        await credentials.SetSecretAsync(CloudBackupSettingsPolicy.CredentialKey(optionsA), "a-secret");
        await credentials.SetSecretAsync(CloudBackupSettingsPolicy.CredentialKey(optionsB), "b-secret");
        var service = new CloudBackupService(backup, settings, credentials,
            (options, _) => options.Endpoint == optionsA.Endpoint ? a : b);
        service.UpdateOptions(optionsA);
        var actions = new BackupRestoreActions(settings, service, backup, () => Task.CompletedTask);
        Assert.True(actions.IsCurrentEndpoint(optionsA.Endpoint));

        settings.Settings.CloudBackup.CloudBackupServerUrl = optionsB.ServerUrl;
        service.UpdateOptions(optionsB);
        Assert.False(actions.IsCurrentEndpoint(optionsA.Endpoint));
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await actions.DeleteSnapshotAsync(optionsA.Endpoint, name));
        Assert.True(b.Files.ContainsKey($"DeskBox/backups/{name}"));

        await service.DeleteRemoteSnapshotAsync(optionsA, name);
        Assert.False(a.Files.ContainsKey($"DeskBox/backups/{name}"));
        Assert.True(b.Files.ContainsKey($"DeskBox/backups/{name}"));
    }

    [Fact]
    public async Task RemoteSnapshotList_RoutesThroughRestoreActions_WithEndpointFreezeAndStop()
    {
        const string name = "DeskBox-CloudBackup-20260910T110000Z-abcd1234.zip";
        var transport = new FakeCloudBackupTransport();
        transport.Files[$"DeskBox/backups/{name}"] = [1, 2, 3];
        CloudBackupOptions options = ConfiguredOptions();
        var backup = new DeskBoxDataBackupService(_appDataRoot);
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        settings.Settings.CloudBackup.CloudBackupProvider = options.Provider;
        settings.Settings.CloudBackup.CloudBackupServerUrl = options.ServerUrl;
        settings.Settings.CloudBackup.CloudBackupRemotePath = options.RemotePath;
        settings.Settings.CloudBackup.CloudBackupUsername = options.Username;
        var actions = new BackupRestoreActions(settings, service, backup, () => Task.CompletedTask);

        // The read-only list is tracked like the destructive actions: it
        // carries the endpoint captured at call time.
        IReadOnlyList<CloudBackupRemoteEntry> entries = await actions.ListSnapshotsAsync(options.Endpoint);
        CloudBackupRemoteEntry entry = Assert.Single(entries);
        Assert.Equal(name, entry.Name);
        Assert.Equal(3, entry.Length);

        // Endpoint freeze: a stale endpoint rejects before any remote call,
        // exactly like delete/download.
        settings.Settings.CloudBackup.CloudBackupServerUrl = "https://other.example/dav";
        Assert.False(actions.IsCurrentEndpoint(options.Endpoint));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => actions.ListSnapshotsAsync(options.Endpoint));

        // Stop freeze: after StopAsync the list is refused, so a PROPFIND
        // cannot outlive shutdown.
        await actions.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => actions.ListSnapshotsAsync(options.Endpoint));
    }

    [Fact]
    public async Task RestoreActionsStop_CancelsAndDrainsDownload_ThenRemovesTemporaryFiles()
    {
        const string name = "DeskBox-CloudBackup-20260910T110000Z-abcd1234.zip";
        var entered = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeCloudBackupTransport
        {
            DownloadHook = async (_, token) =>
            {
                entered.TrySetResult(token);
                await release.Task;
                token.ThrowIfCancellationRequested();
            }
        };
        transport.Files[$"DeskBox/backups/{name}"] = [1];
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        CloudBackupOptions options = ConfiguredOptions();
        settings.Settings.CloudBackup.CloudBackupProvider = options.Provider;
        settings.Settings.CloudBackup.CloudBackupServerUrl = options.ServerUrl;
        settings.Settings.CloudBackup.CloudBackupRemotePath = options.RemotePath;
        settings.Settings.CloudBackup.CloudBackupUsername = options.Username;
        var actions = new BackupRestoreActions(settings, service,
            new DeskBoxDataBackupService(_appDataRoot), () => Task.CompletedTask);
        string directory = Path.Combine(_tempRoot, "pending-download");

        Task<string> download = actions.DownloadSnapshotAsync(options.Endpoint, name, directory);
        CancellationToken token = await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task stop = actions.StopAsync();
        Assert.Same(stop, actions.StopAsync());
        Assert.True(token.IsCancellationRequested);
        Assert.False(stop.IsCompleted);
        var logs = new List<string>();
        bool laterStepRan = false;
        await new ShutdownSequence(logs.Add).RunAsync(
            ShutdownStep.Bounded("backup-restore-actions", () => stop,
                TimeSpan.FromMilliseconds(50)),
            ShutdownStep.Sync("later-cleanup", () => laterStepRan = true))
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(laterStepRan);
        Assert.Contains("backup-restore-actions", Assert.Single(logs));
        release.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
        await stop;

        Assert.False(Directory.Exists(directory));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            actions.DeleteSnapshotAsync(options.Endpoint, name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestoreActionsStop_CancelsUnconfirmedMarker_ButPreservesScheduledRelaunch(
        bool relaunchScheduled)
    {
        var todoStore = new TodoWidgetStore(
            Path.Combine(_appDataRoot, "data", "widgets"), "todo-widget");
        await todoStore.SaveAsync(new TodoWidgetData
        {
            Items = [new TodoItem { Id = "todo-1", Text = "restore candidate" }]
        });
        var backup = new DeskBoxDataBackupService(_appDataRoot);
        string archivePath = await backup.ExportScopedBackupAsync(
            Path.Combine(_tempRoot, "restore-export"), CloudBackupDomain.TodoData);
        (CloudBackupService cloud, SettingsService settings) =
            CreateService(new FakeCloudBackupTransport());
        var actions = new BackupRestoreActions(settings, cloud, backup,
            () => Task.CompletedTask,
            () => relaunchScheduled
                ? AppRelaunchScheduleResult.StartedSuccessfully
                : AppRelaunchScheduleResult.Failed("simulated restart failure"));

        await actions.PrepareScopedRestoreAsync(archivePath, CloudBackupDomain.TodoData);
        Assert.True(File.Exists(backup.PendingRestoreMarkerPath));
        if (relaunchScheduled) Assert.True(actions.ScheduleRelaunch().Started);
        await actions.StopAsync();

        Assert.Equal(relaunchScheduled, File.Exists(backup.PendingRestoreMarkerPath));
        if (relaunchScheduled) await backup.CancelPendingRestoreAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAfterUploadAcceptance_RecordsUnverifiedSuccess(bool duringRetry)
    {
        SeedTodoData();
        using var cancellation = new CancellationTokenSource();
        var listed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var transport = new FakeCloudBackupTransport
        {
            UploadCompleted = () => { if (!duringRetry) cancellation.Cancel(); },
            ExcludeFromListing = _ => true,
            ListRequested = () => listed.TrySetResult()
        };
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions());
        service.VerificationRetryDelayMs = [60_000];
        var events = new List<CloudBackupRunCompletedInfo>();
        service.BackupRunCompleted += events.Add;
        Task<CloudBackupRunResult> run = service.RunBackupNowAsync(cancellation.Token);
        if (duringRetry)
        {
            await listed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
        }
        CloudBackupRunResult result = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.Uploaded);
        Assert.True(result.UploadUnverified);
        Assert.Single(transport.Files);
        Assert.True(settings.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks > 0);
        Assert.True(settings.Settings.CloudBackup.CloudBackupLastUnverifiedUtcTicks > 0);
        Assert.Equal(0, settings.Settings.CloudBackup.CloudBackupLastFailureUtcTicks);
        Assert.True(Assert.Single(events).Uploaded);
    }

    [Fact]
    public async Task RetentionCancellation_PreservesVerifiedUploadAndDoesNotDeleteOldSnapshots()
    {
        SeedTodoData();
        using var cancellation = new CancellationTokenSource();
        int listings = 0;
        var transport = new FakeCloudBackupTransport
        {
            ListRequested = () => { if (++listings == 2) cancellation.Cancel(); }
        };
        transport.Files["DeskBox/backups/DeskBox-CloudBackup-20000101T000000Z-node.zip"] = [1];
        (CloudBackupService service, SettingsService settings) = CreateService(transport);
        service.UpdateOptions(ConfiguredOptions(retention: 1));
        CloudBackupRunResult result = await service.RunBackupNowAsync(cancellation.Token);
        Assert.True(result.Uploaded);
        Assert.False(result.UploadUnverified);
        Assert.Equal(2, transport.Files.Count);
        Assert.Equal(0, settings.Settings.CloudBackup.CloudBackupLastFailureUtcTicks);
    }

    [Fact]
    public async Task CompletionObserverFailure_CannotReclassifyAnAcceptedUpload()
    {
        SeedTodoData();
        (CloudBackupService service, SettingsService settings) = CreateService(new FakeCloudBackupTransport());
        service.UpdateOptions(ConfiguredOptions());
        var events = new List<CloudBackupRunCompletedInfo>();
        service.BackupRunCompleted += _ => throw new InvalidOperationException("view already closed");
        service.BackupRunCompleted += events.Add;
        Assert.True((await service.RunBackupNowAsync()).Uploaded);
        Assert.True(Assert.Single(events).Uploaded);
        Assert.Equal(ConfiguredOptions().Endpoint, Assert.Single(events).Endpoint);
        Assert.Equal(0, settings.Settings.CloudBackup.CloudBackupLastFailureUtcTicks);
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
        FakeCloudBackupTransport transport,
        bool seedCredential = true)
    {
        var backup = new DeskBoxDataBackupService(_appDataRoot);
        var settings = new SettingsService(Path.Combine(_tempRoot, "settings"));
        var credentials = new InMemoryCredentialStore();
        if (seedCredential)
        {
            // A configured service normally has a stored secret — missing-
            // credential runs are tested explicitly with seedCredential:false.
            credentials.SetSecretAsync(
                CloudBackupSettingsPolicy.CredentialKey(ConfiguredOptions()),
                "s3cret").GetAwaiter().GetResult();
        }

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
        internal Func<string, CancellationToken, Task>? DownloadHook { get; init; }
        internal Action? UploadCompleted { get; init; }
        internal Action? ListRequested { get; init; }
        // Verification-fault knobs: ReportedLengthOverride makes the
        // listing lie about sizes; ExcludeFromListing hides entries
        // entirely — together they simulate silent truncation and
        // never-visible uploads.
        internal long? ReportedLengthOverride { get; init; }
        internal Func<string, bool>? ExcludeFromListing { get; init; }
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
            cancellationToken.ThrowIfCancellationRequested();
            ListRequested?.Invoke();
            ThrowIfFailed(remoteDirectory);
            string prefix = remoteDirectory.TrimEnd('/') + "/";
            IReadOnlyList<CloudBackupRemoteEntry> entries = Files.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                .Where(k => ExcludeFromListing?.Invoke(k) != true)
                .Select(k => new CloudBackupRemoteEntry(
                    k[prefix.Length..],
                    ReportedLengthOverride ?? Files[k].Length,
                    null,
                    false))
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
            UploadCompleted?.Invoke();
        }

        public async Task DownloadAsync(string remoteFilePath, Stream destination, CancellationToken cancellationToken = default)
        {
            ThrowIfFailed(remoteFilePath);
            if (!Files.TryGetValue(remoteFilePath, out byte[]? data))
            {
                throw new CloudBackupTransportException("not found", HttpStatusCode.NotFound);
            }

            if (DownloadHook is not null) await DownloadHook(remoteFilePath, cancellationToken);
            await destination.WriteAsync(data, cancellationToken);
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
