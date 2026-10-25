using System.Collections.ObjectModel;
using System.ComponentModel;
using DeskBox.Contracts;
using DeskBox.Features.Backup;
using DeskBox.Models;

namespace DeskBox.Tests;

/// <summary>
/// Batch-49 coverage for the backup settings editor's binding surface:
/// construction projection, write-through normalization, the busy gates,
/// the message-to-status mapping, the remote-snapshot projection, the
/// diagnostics push ports and the language refresh.
/// </summary>
public sealed class BackupSettingsSurfaceTests
{
    private static readonly BackupEndpoint DavA = new(
        "webdav", "https://a.example/dav", "DeskBox/backups", "simon");
    private static readonly BackupEndpoint DavB = new(
        "webdav", "https://b.example/dav", "DeskBox/backups", "simon");

    private static BackupSettingsViewModel Create(
        FakeBackupSettings fake,
        Action<Exception>? reportError = null) =>
        new(fake,
            action => { action(); return true; },
            reportError ?? (_ => { }),
            localize: key => "[" + key + "]",
            format: (key, args) =>
                "[" + key + ":" + string.Join("|", args.Select(a => a?.ToString() ?? "")) + "]");

    [Fact]
    public void Constructor_ProjectsSnapshotOntoBindingSurface()
    {
        var fake = new FakeBackupSettings(new BackupSettingsSnapshot(
            LocalEnabled: false, LocalIntervalMinutes: 720, LocalRetentionCount: 14,
            LocalDirectory: @"D:\custom", EffectiveLocalDirectory: @"D:\custom",
            LocalOpenDirectory: @"D:\custom", LocalDirectoryFallback: false,
            CloudProvider: "webdav", CloudServerUrl: "https://a.example/dav",
            CloudRemotePath: "DeskBox/backups", CloudUsername: "simon",
            CloudTodoEnabled: true, CloudQuickCaptureEnabled: false, CloudWidgetStyleEnabled: true,
            CloudIntervalMinutes: 360, CloudRetentionCount: 10,
            CloudLastSuccessUtcTicks: 0, CloudLastFailureUtcTicks: 0, CloudLastUnverifiedUtcTicks: 0,
            Endpoint: DavA, HasEndpoint: true));
        using var editor = Create(fake);

        Assert.False(editor.LocalBackupEnabled);
        Assert.Equal(720, editor.SelectedLocalIntervalMinutes);
        Assert.Equal(14, editor.SelectedLocalRetentionCount);
        Assert.Equal(@"D:\custom", editor.LocalDirectoryDisplayText);
        Assert.False(editor.ShowLocalFallbackWarning);
        Assert.Equal("webdav", editor.SelectedProvider);
        Assert.True(editor.ShowWebDavFields);
        Assert.Equal("https://a.example/dav", editor.ServerUrl);
        Assert.False(editor.ShowHttpWarning);
        Assert.Equal("DeskBox/backups", editor.RemotePath);
        Assert.Equal("simon", editor.Username);
        Assert.True(editor.IncludeTodoData);
        Assert.False(editor.IncludeQuickCaptureData);
        Assert.True(editor.IncludeWidgetStyle);
        Assert.Equal(360, editor.SelectedIntervalMinutes);
        Assert.Equal(10, editor.SelectedRetentionCount);
        Assert.True(editor.ActionsEnabled);
    }

    [Fact]
    public void Constructor_FallbackDirectoryShowsWarningAndEffectivePath()
    {
        var fake = new FakeBackupSettings(new BackupSettingsSnapshot(
            true, 1440, 7, string.Empty, @"C:\backup", @"C:\backup", true,
            "none", string.Empty, "DeskBox/backups", string.Empty,
            false, false, false, 1440, 5, 0, 0, 0,
            new("none", "", "", ""), false));
        using var editor = Create(fake);

        Assert.Equal(@"C:\backup", editor.LocalDirectoryDisplayText);
        Assert.Equal(
            "[Settings.DataBackup.AutomaticBackupDirectory.FallbackWarning:C:\\backup]",
            editor.LocalFallbackWarningText);
        Assert.True(editor.ShowLocalFallbackWarning);
    }

    [Fact]
    public async Task UserEdits_WriteThroughWithoutEchoLoops()
    {
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA) with { CloudWidgetStyleEnabled = true });
        using var editor = Create(fake);

        editor.LocalBackupEnabled = false;
        editor.SelectedLocalIntervalMinutes = 30; // valid preset, differs from default
        editor.SelectedLocalIntervalMinutes = 61; // unknown preset normalizes back
        editor.SelectedLocalRetentionCount = 14; // valid preset, differs from default
        editor.SelectedLocalRetentionCount = 99; // unknown preset normalizes back
        editor.SelectedProvider = "bogus";
        editor.ServerUrl = "http://nas.local/dav";
        editor.RemotePath = "  other/path  ";
        editor.Username = "ada";
        editor.IncludeTodoData = true;
        editor.IncludeQuickCaptureData = true;
        editor.IncludeWidgetStyle = false;
        editor.SelectedIntervalMinutes = 360; // valid preset, differs from default
        editor.SelectedIntervalMinutes = 61; // unknown preset normalizes back
        editor.SelectedRetentionCount = 10; // valid preset, differs from default
        editor.SelectedRetentionCount = 99; // unknown preset normalizes back
        // Updates ride a pool thread since batch R11 (the blocking pipe
        // pair used to run on the UI thread); drain before asserting.
        await editor.PendingReads;

        Assert.Contains(fake.Changes, c => c.LocalEnabled == false);
        Assert.Contains(fake.Changes, c => c.LocalIntervalMinutes == 30);
        Assert.Contains(fake.Changes, c => c.LocalIntervalMinutes == 1440);
        Assert.Contains(fake.Changes, c => c.LocalRetentionCount == 14);
        Assert.Contains(fake.Changes, c => c.LocalRetentionCount == 7);
        Assert.Contains(fake.Changes, c => c.CloudProvider == "none");
        Assert.Contains(fake.Changes, c => c.CloudServerUrl == "http://nas.local/dav");
        Assert.Contains(fake.Changes, c => c.CloudRemotePath == "  other/path  ");
        Assert.Contains(fake.Changes, c => c.CloudUsername == "ada");
        Assert.Contains(fake.Changes, c => c.CloudTodoEnabled == true);
        Assert.Contains(fake.Changes, c => c.CloudQuickCaptureEnabled == true);
        Assert.Contains(fake.Changes, c => c.CloudWidgetStyleEnabled == false);
        Assert.Contains(fake.Changes, c => c.CloudIntervalMinutes == 360);
        Assert.Contains(fake.Changes, c => c.CloudIntervalMinutes == 1440);
        Assert.Contains(fake.Changes, c => c.CloudRetentionCount == 10);
        Assert.Contains(fake.Changes, c => c.CloudRetentionCount == 5);

        // The setters normalized the projected values in place.
        Assert.Equal(1440, editor.SelectedLocalIntervalMinutes);
        Assert.Equal(7, editor.SelectedLocalRetentionCount);
        Assert.Equal("none", editor.SelectedProvider);
        Assert.False(editor.ShowWebDavFields);
        Assert.True(editor.ShowHttpWarning);
        Assert.Equal(1440, editor.SelectedIntervalMinutes);
        Assert.Equal(5, editor.SelectedRetentionCount);
        // Each accepted control edit produced exactly one change record
        // (normalized-back no-ops are skipped, like the legacy shell).
        Assert.Equal(16, fake.Changes.Count);
    }

    [Fact]
    public async Task TurningProviderOff_SwitchesEveryCloudDomainOff()
    {
        // Batch R12 product decision: the provider dropdown is the cloud
        // master switch — WebDAV off means cloud backup is fully off. The
        // page must flip all three domain toggles immediately and send the
        // off writes (the coordinator clears the persisted domains in the
        // same write; these are the idempotent follow-ups).
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA) with
        {
            CloudTodoEnabled = true,
            CloudQuickCaptureEnabled = true,
            CloudWidgetStyleEnabled = true,
        });
        using var editor = Create(fake);

        Assert.True(editor.IncludeTodoData);
        editor.SelectedProvider = "none";
        await editor.PendingReads;

        Assert.False(editor.IncludeTodoData);
        Assert.False(editor.IncludeQuickCaptureData);
        Assert.False(editor.IncludeWidgetStyle);
        Assert.False(editor.ShowWebDavFields);
        Assert.Contains(fake.Changes, c => c.CloudProvider == "none");
        Assert.Contains(fake.Changes, c => c.CloudTodoEnabled == false);
        Assert.Contains(fake.Changes, c => c.CloudQuickCaptureEnabled == false);
        Assert.Contains(fake.Changes, c => c.CloudWidgetStyleEnabled == false);
    }

    [Fact]
    public void ProjectionRefresh_DoesNotWriteBack()
    {
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA));
        using var editor = Create(fake);
        fake.Changes.Clear();

        fake.Snapshot = DefaultSnapshot(DavB) with
        {
            LocalEnabled = false,
            CloudProvider = "none",
            CloudServerUrl = "http://b.example/dav"
        };
        editor.RefreshState();

        Assert.Empty(fake.Changes);
        Assert.False(editor.LocalBackupEnabled);
        Assert.False(editor.ShowWebDavFields);
        Assert.True(editor.ShowHttpWarning);
    }

    [Fact]
    public async Task UpdateLocalDirectory_NormalizesAndResets()
    {
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA));
        using var editor = Create(fake);

        editor.UpdateLocalDirectory(@"  D:\custom  ");
        await editor.PendingReads;
        Assert.Contains(fake.Changes, c => c.LocalDirectory == @"D:\custom");

        editor.UpdateLocalDirectory("   ");
        await editor.PendingReads;
        Assert.Contains(fake.Changes, c => c.LocalDirectory == "");
        Assert.True(editor.IsValidLocalDirectory(@"C:\any", out _));
    }

    [Fact]
    public async Task CommandBusyGate_DisablesAndReenablesActions()
    {
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA));
        using var editor = Create(fake);

        Assert.True(editor.ActionsEnabled);
        editor.SetCommandBusy(true);
        Assert.False(editor.ActionsEnabled);
        editor.SetCommandBusy(true); // idempotent
        Assert.False(editor.ActionsEnabled);
        editor.SetCommandBusy(false);
        Assert.True(editor.ActionsEnabled);

        // The visit state machine's own busy flag gates actions as well.
        TaskCompletionSource probeGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fake.Probe = async (_, _, token) =>
        {
            Assert.False(editor.ActionsEnabled);
            await probeGate.Task.WaitAsync(token);
        };
        editor.Activate();
        Task<bool> probe = editor.ProbeAsync(null);
        Assert.False(editor.ActionsEnabled);
        probeGate.SetResult();
        Assert.True(await probe);
        Assert.True(editor.ActionsEnabled);
    }

    [Fact]
    public void MessageChanges_MapOntoConnectionStatusText()
    {
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA));
        using var editor = Create(fake);

        Assert.Equal(string.Empty, editor.ConnectionStatusText);
        editor.SetSurfaceMessageForTest(new(BackupPageMessageKind.ProbeFailed, "offline"));
        Assert.Equal("[Settings.CloudBackup.TestConnection.Failed:offline]", editor.ConnectionStatusText);
        editor.SetSurfaceMessageForTest(new(BackupPageMessageKind.PasswordMissing));
        Assert.Equal("[Settings.CloudBackup.Password.NotSaved]", editor.ConnectionStatusText);
        editor.SetSurfaceMessageForTest(new(BackupPageMessageKind.PasswordEmpty));
        Assert.Equal("[Settings.CloudBackup.Password.EmptyBody]", editor.ConnectionStatusText);
        editor.SetSurfaceMessageForTest(BackupPageMessage.Empty);
        Assert.Equal(string.Empty, editor.ConnectionStatusText);
    }

    [Fact]
    public void EndpointChange_ClearsConnectionStatusAndSnapshotProjection()
    {
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA));
        using var editor = Create(fake);
        editor.Activate();
        editor.ConnectionStatusText = "stale from the old endpoint";

        fake.Snapshot = DefaultSnapshot(DavB);
        editor.RefreshState();

        Assert.Equal(string.Empty, editor.ConnectionStatusText);
    }

    [Fact]
    public void RemoteSnapshots_ProjectIntoBindableItems()
    {
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA));
        using var editor = Create(fake);
        Assert.Empty(editor.RemoteSnapshotItems);

        DateTimeOffset created = new(2026, 9, 28, 8, 30, 0, TimeSpan.Zero);
        editor.SetSurfaceSnapshotsForTest(
        [
            new("deskbox-20260928-abcd1234.zip", 2048, created),
            new("legacy.zip", null, null)
        ]);

        ObservableCollection<CloudBackupRemoteSnapshotItem> items = editor.RemoteSnapshotItems;
        Assert.Equal(2, items.Count);
        Assert.Equal("deskbox-20260928-abcd1234.zip", items[0].Name);
        Assert.Contains("abcd1234", items[0].Details, StringComparison.Ordinal);
        Assert.Contains("2 [Size.Unit.KB]", items[0].Details, StringComparison.Ordinal);
        Assert.Equal("legacy.zip", items[1].Title);
        Assert.Equal("legacy.zip", items[1].Details);
        Assert.Equal(DavA, items[0].Endpoint);
    }

    [Fact]
    public void StatusText_ComposesSuccessFailureAndUnverified()
    {
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA) with
        {
            CloudLastSuccessUtcTicks = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero).UtcTicks,
            CloudLastFailureUtcTicks = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero).UtcTicks,
            CloudLastUnverifiedUtcTicks = new DateTimeOffset(2026, 9, 26, 12, 0, 0, TimeSpan.Zero).UtcTicks
        });
        using var editor = Create(fake);

        string status = editor.StatusText;
        Assert.Contains("Settings.CloudBackup.LastSuccess", status, StringComparison.Ordinal);
        Assert.Contains("Settings.CloudBackup.LastFailure", status, StringComparison.Ordinal);
        Assert.Contains("Settings.CloudBackup.LastUnverified", status, StringComparison.Ordinal);

        using var never = Create(new FakeBackupSettings(DefaultSnapshot(DavA)));
        Assert.Equal("[Settings.CloudBackup.LastSuccess.Never]", never.StatusText);
    }

    [Fact]
    public void CredentialStatusText_FollowsCredentialReads()
    {
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA))
        {
            Credential = (_, _) => Task.FromResult(true)
        };
        using var editor = Create(fake);
        Assert.Equal("[Settings.CloudBackup.Password.NotSaved]", editor.CredentialStatusText);

        editor.Activate();
        Assert.Equal("[Settings.CloudBackup.Password.Saved]", editor.CredentialStatusText);
    }

    [Fact]
    public void HttpWarningText_IsLocalizedPerRead()
    {
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA));
        using var editor = Create(fake);
        Assert.Equal("[Settings.CloudBackup.HttpWarning]", editor.HttpWarningText);
    }

    [Fact]
    public void DiagnosticsPorts_PushTextsAndBusyGates()
    {
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA));
        using var editor = Create(fake);
        Assert.Equal(string.Empty, editor.DragDropSummaryText);
        Assert.False(editor.CanRepairDragDrop);
        Assert.True(editor.CanResyncRuntime);

        editor.SetDragDropDiagnostic(new(
            "summary", "detail", "process", "explorer", "uac", "appCompat", "startup", "shortcut",
            CanRepair: true));
        Assert.Equal("summary", editor.DragDropSummaryText);
        Assert.Equal("detail", editor.DragDropDetailText);
        Assert.Equal("shortcut", editor.DragDropShortcutText);
        Assert.True(editor.CanRepairDragDrop);

        editor.SetDragDropRepairBusy(true);
        Assert.False(editor.CanRepairDragDrop);
        editor.SetDragDropRepairBusy(false);
        Assert.True(editor.CanRepairDragDrop);

        editor.SetDragDropRepairStatus("repaired 3");
        Assert.Equal("repaired 3", editor.DragDropRepairStatusText);

        editor.SetRuntimeHealth("healthy", "detail-line");
        Assert.Equal("healthy", editor.RuntimeSummaryText);
        Assert.Equal("detail-line", editor.RuntimeDetailText);
        editor.SetRuntimeResyncBusy(true);
        Assert.False(editor.CanResyncRuntime);
        editor.SetRuntimeResyncBusy(false);
        Assert.True(editor.CanResyncRuntime);
    }

    [Fact]
    public void RefreshLocalization_ReprojectsOptionTablesAndStatusTexts()
    {
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA) with
        {
            CloudLastSuccessUtcTicks = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero).UtcTicks
        });
        using var editor = Create(fake);
        _ = editor.AvailableLocalIntervalOptions;
        _ = editor.AvailableProviderOptions;
        _ = editor.AvailableIntervalOptions;
        _ = editor.AvailableRetentionOptions;
        _ = editor.AvailableLocalRetentionOptions;

        var names = new HashSet<string>();
        editor.PropertyChanged += (_, e) => names.Add(e.PropertyName!);
        editor.RefreshLocalization();

        foreach (string name in new[]
        {
            nameof(BackupSettingsViewModel.AvailableLocalIntervalOptions),
            nameof(BackupSettingsViewModel.AvailableLocalRetentionOptions),
            nameof(BackupSettingsViewModel.AvailableProviderOptions),
            nameof(BackupSettingsViewModel.AvailableIntervalOptions),
            nameof(BackupSettingsViewModel.AvailableRetentionOptions),
            nameof(BackupSettingsViewModel.SelectedProvider),
            nameof(BackupSettingsViewModel.StatusText),
            nameof(BackupSettingsViewModel.CredentialStatusText),
            nameof(BackupSettingsViewModel.HttpWarningText),
            nameof(BackupSettingsViewModel.LocalDirectoryDisplayText)
        })
        {
            Assert.Contains(name, names);
        }
    }

    [Fact]
    public void OptionTables_UseRealArraysAndCanonicalValues()
    {
        var fake = new FakeBackupSettings(DefaultSnapshot(DavA));
        using var editor = Create(fake);

        SettingsOption[] localIntervals = (SettingsOption[])editor.AvailableLocalIntervalOptions;
        SettingsOption[] providers = (SettingsOption[])editor.AvailableProviderOptions;
        Assert.Equal(6, localIntervals.Length);
        Assert.Equal(2, providers.Length);
        Assert.Equal(5, editor.AvailableIntervalOptions.Count);
        Assert.Equal(5, editor.AvailableRetentionOptions.Count);
        Assert.Equal(5, editor.AvailableLocalRetentionOptions.Count);
        Assert.Equal("[Settings.DataBackup.Interval.Hour]", localIntervals[2].DisplayName);
        Assert.Equal(60, localIntervals[2].Value);
        Assert.Equal("[Settings.CloudBackup.Provider.WebDav]", providers[1].DisplayName);
        Assert.Equal("webdav", providers[1].Value);
    }

    [Fact]
    public void ShellSettingsViewModel_HasNoBackupFamilySurfaceLeft()
    {
        string[] retired =
        [
            "AutomaticBackupEnabled",
            "SelectedAutomaticBackupIntervalMinutes",
            "SelectedAutomaticBackupRetentionCount",
            "AvailableAutomaticBackupIntervalOptions",
            "AvailableAutomaticBackupRetentionOptions",
            "AutomaticBackupDirectoryDisplayText",
            "AutomaticBackupFallbackWarningText",
            "AutomaticBackupFallbackWarningVisibility",
            "CloudBackupActionsEnabled",
            "SelectedCloudBackupProvider",
            "AvailableCloudBackupProviderOptions",
            "CloudBackupWebDavVisibility",
            "CloudBackupServerUrl",
            "CloudBackupHttpWarningText",
            "CloudBackupHttpWarningVisibility",
            "CloudBackupRemotePath",
            "CloudBackupUsername",
            "CloudBackupCredentialStatusText",
            "CloudBackupCredentialSaved",
            "CloudBackupConnectionStatusText",
            "CloudBackupConnectionStatusVisibility",
            "CloudBackupTodoDataEnabled",
            "CloudBackupQuickCaptureDataEnabled",
            "CloudBackupWidgetStyleEnabled",
            "SelectedCloudBackupIntervalMinutes",
            "AvailableCloudBackupIntervalOptions",
            "SelectedCloudBackupRetentionCount",
            "AvailableCloudBackupRetentionOptions",
            "CloudBackupStatusText",
            "CloudBackupBusy",
            "CloudBackupEndpointGeneration",
            "CloudBackupRemoteSnapshots",
            "DragDropPermissionSummaryText",
            "DragDropPermissionDetailText",
            "DragDropPermissionSeverityKind",
            "DragDropPermissionProcessText",
            "DragDropPermissionExplorerText",
            "DragDropPermissionUacText",
            "DragDropPermissionAppCompatText",
            "DragDropPermissionStartupText",
            "DragDropPermissionShortcutText",
            "DragDropPermissionRepairStatusText",
            "IsDragDropPermissionRepairing",
            "CanRepairDragDropPermission",
            "RuntimeHealthSummary",
            "RuntimeHealthDetail",
            "IsRuntimeResyncing",
            "CanResyncRuntimeState"
        ];
        System.Reflection.PropertyInfo[] properties = typeof(DeskBox.ViewModels.SettingsViewModel)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
        Assert.Empty(properties.Select(p => p.Name).Where(retired.Contains));
    }

    private static BackupSettingsSnapshot DefaultSnapshot(BackupEndpoint endpoint) => new(
        true, 1440, 7, string.Empty, @"C:\backup", @"C:\backup", false,
        endpoint.Provider, endpoint.ServerUrl, endpoint.RemotePath, endpoint.Username,
        false, false, false, 1440, 5, 0, 0, 0, endpoint, true);

    private sealed class FakeBackupSettings(BackupSettingsSnapshot snapshot) : IBackupSettings
    {
        private Action<BackupUploadNotification>? _uploadCompleted;

        public BackupSettingsSnapshot Snapshot { get; set; } = snapshot;
        public List<BackupSettingsChange> Changes { get; } = [];
        public Func<BackupEndpoint, CancellationToken, Task<bool>> Credential { get; set; } =
            (_, _) => Task.FromResult(false);
        public Func<BackupEndpoint, string?, CancellationToken, Task> Probe { get; set; } =
            (_, _, _) => Task.CompletedTask;

        public event Action<BackupUploadNotification>? UploadCompleted
        {
            add => _uploadCompleted += value;
            remove => _uploadCompleted -= value;
        }

        public BackupSettingsSnapshot Read() => Snapshot;

        public void Update(BackupSettingsChange change) => Changes.Add(change);

        public bool IsValidLocalDirectory(string path, out string? rejectionReasonKey)
        {
            rejectionReasonKey = null;
            return true;
        }

        public Task SaveAsync() => Task.CompletedTask;

        public Task<bool> HasCredentialAsync(BackupEndpoint endpoint, CancellationToken cancellationToken) =>
            Credential(endpoint, cancellationToken);

        public Task SaveCredentialAsync(BackupEndpoint endpoint, string secret, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task ProbeAsync(BackupEndpoint endpoint, string? secret, CancellationToken cancellationToken) =>
            Probe(endpoint, secret, cancellationToken);

        public Task<IReadOnlyList<BackupRemoteSnapshot>> ListAsync(
            BackupEndpoint endpoint,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<BackupRemoteSnapshot>>([]);
    }
}

file static class BackupSettingsSurfaceTestExtensions
{
    /// <summary>Drives the private message projection path from tests.</summary>
    public static void SetSurfaceMessageForTest(
        this BackupSettingsViewModel editor,
        BackupPageMessage message)
    {
        System.Reflection.PropertyInfo property = typeof(BackupSettingsViewModel)
            .GetProperty("Message", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!;
        property.SetValue(editor, message);
    }

    /// <summary>Drives the private snapshot projection path from tests.</summary>
    public static void SetSurfaceSnapshotsForTest(
        this BackupSettingsViewModel editor,
        IReadOnlyList<BackupRemoteSnapshot> snapshots)
    {
        System.Reflection.PropertyInfo property = typeof(BackupSettingsViewModel)
            .GetProperty("RemoteSnapshots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!;
        property.SetValue(editor, snapshots);
    }
}
