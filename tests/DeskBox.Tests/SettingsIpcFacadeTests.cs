using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Batch 3c-1: the remote facade pattern end-to-end — the settings-host
/// proxies (RemoteSettingsHotkeyActions / RemoteSettingsDataActions) over the
/// pipe against the main-side router wired to fake in-process facades, plus
/// the reload-push payload format that carries the echo-suppression host
/// commit id.
/// </summary>
public sealed class SettingsIpcFacadeTests : IDisposable
{
    private readonly string _tempRoot;

    public SettingsIpcFacadeTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    private sealed class FakeHotkeyActions : ISettingsHotkeyActions
    {
        public GlobalHotkeyRuntimeState? StateToReturn;
        public GlobalHotkeyActivation? AppliedActivation;
        public bool ApplyResult = true;
        public bool ResetResult = true;
        public bool RefreshCalled;
        public bool? SetEnabledArg;
        public bool GestureOwnedResult;
        public bool GestureOwnedArg;
        public bool DoubleClickArg;
        public bool SuspendCalled;
        public bool ResumeCalled;

        public event Action? RegistrationChanged;

        public void RaiseRegistrationChanged() => RegistrationChanged?.Invoke();

        public GlobalHotkeyRuntimeState? GetRuntimeState() => StateToReturn;

        public bool TryApplyActivation(GlobalHotkeyActivation activation, out string? applyError)
        {
            AppliedActivation = activation;
            applyError = ApplyResult ? null : "apply failed";
            return ApplyResult;
        }

        public bool ResetToDefault(out string? error)
        {
            error = ResetResult ? null : "reset failed";
            return ResetResult;
        }

        public void RefreshRegistration() => RefreshCalled = true;

        public void SetEnabled(bool enabled) => SetEnabledArg = enabled;

        public bool IsGestureOwnedBySearchHotkey(GlobalHotkeyGesture gesture)
        {
            GestureOwnedArg = true;
            return GestureOwnedResult;
        }

        public DesktopDoubleClickToggleResult TrySetDesktopDoubleClickEnabled(bool enabled)
        {
            DoubleClickArg = enabled;
            return new DesktopDoubleClickToggleResult(!enabled, 42);
        }

        public void SuspendHotkeysForRecording() => SuspendCalled = true;

        public void ResumeHotkeysAfterRecording() => ResumeCalled = true;
    }

    private sealed class FakeDataActions : ISettingsDataActions
    {
        public (int Active, int Recent) Counts = (7, 3);
        public bool Cleared;
        public bool ClearedRecent;
        public bool CleanupCalled;
        public QuickCaptureImageCacheInfo? InfoToReturn = new(4, 1024, 1, 256);

        public Task<(int ActiveCount, int RecentCount)> GetQuickCaptureCountsAsync() =>
            Task.FromResult(Counts);

        public Task ClearQuickCaptureDataAsync()
        {
            Cleared = true;
            return Task.CompletedTask;
        }

        public Task ClearQuickCaptureRecentAsync()
        {
            ClearedRecent = true;
            return Task.CompletedTask;
        }

        public Task<QuickCaptureImageCacheCleanupResult> ClearUnusedQuickCaptureImageCacheAsync()
        {
            CleanupCalled = true;
            return Task.FromResult(new QuickCaptureImageCacheCleanupResult(1, 256));
        }

        public Task<QuickCaptureImageCacheInfo?> GetQuickCaptureImageCacheInfoAsync() =>
            Task.FromResult(InfoToReturn);
    }

    private sealed record Loopback(
        SettingsIpcServer Server,
        FakeHotkeyActions Hotkey,
        FakeDataActions Data,
        FakeRestoreActions Restore,
        FakeWidgetActions Widget,
        FakeAppActions AppActions,
        FakeBackupCommands BackupCommands,
        FakeBackupRestore BackupRestore,
        FakeBackupSettings BackupSettings,
        FakeUpdateService UpdateService,
        SettingsIpcClient Client);

    private static async Task<Loopback> StartLoopbackAsync(string pipeName, SettingsService settings)
    {
        var hotkey = new FakeHotkeyActions();
        var data = new FakeDataActions();
        var restore = new FakeRestoreActions();
        var widget = new FakeWidgetActions();
        var appActions = new FakeAppActions();
        var backupCommands = new FakeBackupCommands();
        var backupRestore = new FakeBackupRestore();
        var backupSettings = new FakeBackupSettings();
        var updateService = new FakeUpdateService();
        SettingsIpcServer? server = null;
        var router = new SettingsIpcMethodRouter(
            settings,
            hotkey,
            data,
            restore,
            widget,
            appActions,
            pushEvent: (method, payload) =>
                server?.SendEventAsync(method, payload) ?? Task.CompletedTask,
            backupCommands: backupCommands,
            backupRestore: backupRestore,
            backupSettings: backupSettings,
            appUpdate: updateService);
        server = SettingsIpcServer.Start(pipeName, router.DispatchAsync);
        var client = await SettingsIpcClient.ConnectAsync(pipeName, TimeSpan.FromMilliseconds(200), attempts: 25);
        (bool ok, string? payload, _) = await client.RequestAsync(SettingsIpcMethods.Ping);
        Assert.True(ok, "loopback ping failed: " + payload);
        return new Loopback(
            server, hotkey, data, restore, widget, appActions,
            backupCommands, backupRestore, backupSettings, updateService, client);
    }

    private static string PipeName() => "deskbox-tests-facade-ipc-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task HotkeyFacade_RoundTripsEveryCommand()
    {
        var settings = new SettingsService(_tempRoot);
        var loop = await StartLoopbackAsync(PipeName(), settings);
        var server = loop.Server;
        var hotkey = loop.Hotkey;
        var client = loop.Client;
        var remote = new RemoteSettingsHotkeyActions(client);
        try
        {
            var expectedState = new GlobalHotkeyRuntimeState(
                true,
                null,
                "F7",
                // VK_F7 = 0x78
                GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(HotkeyModifierKeys.None, 0x78)));
            hotkey.StateToReturn = expectedState;
            Assert.Equal(expectedState, remote.GetRuntimeState());

            var activation = GlobalHotkeyActivation.FromChord(
                new GlobalHotkeyGesture(HotkeyModifierKeys.Alt | HotkeyModifierKeys.Control, 0x20));
            Assert.True(remote.TryApplyActivation(activation, out string? applyError));
            Assert.Null(applyError);
            Assert.Equal(activation, hotkey.AppliedActivation);

            hotkey.ResetResult = false;
            Assert.False(remote.ResetToDefault(out string? resetError));
            Assert.Equal("reset failed", resetError);

            remote.RefreshRegistration();
            Assert.True(hotkey.RefreshCalled);

            remote.SetEnabled(false);
            Assert.False(hotkey.SetEnabledArg);

            hotkey.GestureOwnedResult = true;
            Assert.True(remote.IsGestureOwnedBySearchHotkey(activation.Gesture));

            DesktopDoubleClickToggleResult toggle = remote.TrySetDesktopDoubleClickEnabled(true);
            Assert.True(hotkey.DoubleClickArg);
            Assert.False(toggle.Applied);
            Assert.Equal(42, toggle.ErrorCode);

            remote.SuspendHotkeysForRecording();
            remote.ResumeHotkeysAfterRecording();
            Assert.True(hotkey.SuspendCalled);
            Assert.True(hotkey.ResumeCalled);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task DataFacade_RoundTripsEveryCommand()
    {
        var settings = new SettingsService(_tempRoot);
        var loop = await StartLoopbackAsync(PipeName(), settings);
        var server = loop.Server;
        var data = loop.Data;
        var client = loop.Client;
        var remote = new RemoteSettingsDataActions(client);
        try
        {
            (int active, int recent) = await remote.GetQuickCaptureCountsAsync();
            Assert.Equal((7, 3), (active, recent));

            await remote.ClearQuickCaptureDataAsync();
            Assert.True(data.Cleared);

            await remote.ClearQuickCaptureRecentAsync();
            Assert.True(data.ClearedRecent);

            QuickCaptureImageCacheCleanupResult cleanup = await remote.ClearUnusedQuickCaptureImageCacheAsync();
            Assert.True(data.CleanupCalled);
            Assert.Equal(1, cleanup.DeletedFileCount);
            Assert.Equal(256, cleanup.DeletedBytes);

            QuickCaptureImageCacheInfo? info = await remote.GetQuickCaptureImageCacheInfoAsync();
            Assert.NotNull(info);
            Assert.Equal(4, info!.TotalFileCount);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task HotkeyRegistrationEvent_ReachesRemoteSubscribers()
    {
        var settings = new SettingsService(_tempRoot);
        var loop = await StartLoopbackAsync(PipeName(), settings);
        var server = loop.Server;
        var client = loop.Client;
        var remote = new RemoteSettingsHotkeyActions(client);
        var raised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remote.RegistrationChanged += () => raised.TrySetResult();
        try
        {
            await server.SendEventAsync(SettingsIpcMethods.HotkeyRegistrationChanged, null);
            await raised.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
        }
    }

    [Fact]
    public void ReloadPayload_ComposesAndParsesWithEchoSuppression()
    {
        string withHost = SettingsIpcReloadPayload.Compose(11, 7);
        Assert.True(SettingsIpcReloadPayload.TryParse(withHost, out long mainId, out long? hostId));
        Assert.Equal(11, mainId);
        Assert.Equal(7, hostId);

        string withoutHost = SettingsIpcReloadPayload.Compose(12, null);
        Assert.True(SettingsIpcReloadPayload.TryParse(withoutHost, out long mainId2, out long? hostId2));
        Assert.Equal(12, mainId2);
        Assert.Null(hostId2);

        Assert.False(SettingsIpcReloadPayload.TryParse(null, out _, out _));
        Assert.False(SettingsIpcReloadPayload.TryParse("garbage", out _, out _));
    }

    private sealed class FakeRestoreActions : ISettingsRestoreActions
    {
        public DeskBoxRestorePreparation Preparation = new(
            DateTimeOffset.UtcNow,
            "1.6.0",
            3,
            1024,
            15,
            true);
        public bool ConfirmResult = true;
        public bool Cancelled;
        public string? DeletedSnapshot;
        public bool Scanned;

        public Task<DeskBoxRestorePreparation> PrepareRestoreAsync(string archivePath) =>
            Task.FromResult(Preparation);

        public Task<bool> ConfirmPendingFullRestoreAsync() => Task.FromResult(ConfirmResult);

        public Task CancelPendingRestoreAsync()
        {
            Cancelled = true;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DeskBoxBackupSnapshotInfo>> GetSnapshotInventoryAsync() => Task.FromResult<IReadOnlyList<DeskBoxBackupSnapshotInfo>>(
        [
            new("c:\\snap.zip", "pre-restore", DateTimeOffset.UtcNow, 2048, true, null, 15),
        ]);

        public Task DeleteSnapshotAsync(string snapshotPath)
        {
            DeletedSnapshot = snapshotPath;
            return Task.CompletedTask;
        }

        public Task<DeskBoxAttachmentHealthReport> ScanAttachmentHealthAsync()
        {
            Scanned = true;
            return Task.FromResult(new DeskBoxAttachmentHealthReport(
                5, [], [], [], 0));
        }
    }

    private sealed class FakeWidgetActions : ISettingsWidgetActions
    {
        public bool HostAvailable = true;
        public WidgetKind EnabledArg;
        public string? LayerReason;
        public bool NotifiedGroup;
        public IReadOnlyList<ManagedStorageFolderCleanupCandidate>? Orphans =
        [
            new("folder", "C:\\managed\\folder", 4),
        ];
        public string? MovedOrphan;
        public (string Path, nint Hwnd)? DeletedOrphan;
        public string? MigrationRoot;
        public ManagedStorageMigrationResult MigrationResult =
            new(2, "C:\\old", "C:\\new", 7, 4096, false);
        public List<GlanceWidgetInstanceInfo> Glance =
        [
            new("g1", "First", true),
            new("g2", "Second", false),
        ];
        public bool GlanceFeatureEnabled = true;
        public string? RenamedGlance;
        public (string WidgetId, WidgetScreenBindingMode Mode, string? ScreenId)? Binding;
        public OrganizationHistoryEntry? LatestEntry = new() { Id = "h1", CanUndo = true };
        public (string Id, nint Hwnd)? OrgUndo;
        private WidgetMoveAllUndoToken? _lastMoveAllToken;

        public bool IsHostAvailable => HostAvailable;

        public bool IsFeatureWidgetEnabled(WidgetKind kind)
        {
            EnabledArg = kind;
            return true;
        }

        public Task SyncFeatureWidgetEnabledAsync(WidgetKind kind, bool enabled) => Task.CompletedTask;

        public List<Contracts.GroupPresentationChange> GroupPresentations { get; } = [];
        public List<Contracts.GroupNavigationDefaultsChange> NavDefaults { get; } = [];
        public List<Contracts.LayoutPreferencesChange> LayoutPreferences { get; } = [];
        public List<Contracts.CapsuleOverrideReset> CapsuleResets { get; } = [];

        public Task ApplyGroupPresentation(Contracts.GroupPresentationChange change)
        {
            GroupPresentations.Add(change);
            return Task.CompletedTask;
        }

        public Task ApplyGroupNavigationDefaults(Contracts.GroupNavigationDefaultsChange change)
        {
            NavDefaults.Add(change);
            return Task.CompletedTask;
        }

        public Task ApplyLayoutPreferences(Contracts.LayoutPreferencesChange change)
        {
            LayoutPreferences.Add(change);
            return Task.CompletedTask;
        }

        public Task ResetCapsuleOverrides(Contracts.CapsuleOverrideReset reset)
        {
            CapsuleResets.Add(reset);
            return Task.CompletedTask;
        }

        public Task<bool> SetFeatureWidgetEnabled(WidgetKind kind, bool enabled)
        {
            SyncToggles.Add((kind, enabled));
            return Task.FromResult(enabled);
        }

        public List<(WidgetKind Kind, bool Enabled)> SyncToggles { get; } = [];

        public Task ResetFeatureWidgetAsync(WidgetKind kind) => Task.CompletedTask;

        public void RefreshVisibleWidgetDesktopLayers(string reason) => LayerReason = reason;

        public void NotifyWidgetGroupPresentationSettingsChanged() => NotifiedGroup = true;

        public Task<bool> ReorderWidgetGroupMemberAsync(string sourceWidgetId, string targetWidgetId) =>
            Task.FromResult(true);

        public Task<bool> RemoveWidgetFromGroupAsync(string widgetId) => Task.FromResult(true);

        public Task<bool> DissolveWidgetGroupContainingAsync(string widgetId) => Task.FromResult(true);

        public IReadOnlyList<ManagedStorageFolderCleanupCandidate>? GetOrphanManagedStorageFolders() => Orphans;

        public Task<int> RestoreOrphanManagedStorageFoldersAsync(IReadOnlyList<string> folderPaths) =>
            Task.FromResult(folderPaths.Count);

        public Task MoveOrphanManagedStorageFolderContentsToDesktopAsync(string folderPath)
        {
            MovedOrphan = folderPath;
            return Task.CompletedTask;
        }

        public Task DeleteOrphanManagedStorageFolderAsync(string folderPath, nint ownerWindowHandle)
        {
            DeletedOrphan = (folderPath, ownerWindowHandle);
            return Task.CompletedTask;
        }

        public int GetDefaultManagedStorageWidgetCount() => 6;

        public Task<ManagedStorageMigrationPreview> PreviewManagedStorageMigrationAsync(
            string newRootPath,
            CancellationToken token) => Task.FromResult(
            new ManagedStorageMigrationPreview(3, 9, 2048, false, null));

        public async Task<ManagedStorageMigrationResult> UpdateDefaultManagedStorageRootAsync(
            string newRootPath,
            ManagedStorageMigrationOptions? options)
        {
            MigrationRoot = newRootPath;
            options?.Progress?.Report(new ManagedStorageMigrationProgress(
                ManagedStorageMigrationPhase.Copying,
                1,
                2,
                0,
                0,
                null));
            await Task.Delay(10);
            return MigrationResult;
        }

        public IReadOnlyList<GlanceWidgetInstanceInfo> GetGlanceWidgetInstances() => Glance;

        public bool IsGlanceFeatureEnabled => GlanceFeatureEnabled;

        public Task<GlanceWidgetInstanceInfo> CreateGlanceWidgetAsync(string? sourceWidgetId) =>
            Task.FromResult(new GlanceWidgetInstanceInfo("g3", sourceWidgetId ?? "fresh", true));

        public Task SetGlanceWidgetInstanceEnabledAsync(string widgetId, bool enabled) => Task.CompletedTask;

        public Task<bool> LocateGlanceWidgetAsync(string widgetId) => Task.FromResult(true);

        public Task RemoveGlanceWidgetAsync(string widgetId) => Task.CompletedTask;

        public Task RenameWidgetAsync(string widgetId, string newName)
        {
            RenamedGlance = widgetId + ":" + newName;
            return Task.CompletedTask;
        }

        public async Task<(int MovedCount, WidgetMoveAllUndoToken Token)> MoveAllWidgetSurfacesToDisplayAsync(
            string displayStableId)
        {
            await Task.Yield();
            _lastMoveAllToken = new WidgetMoveAllUndoToken();
            return (4, _lastMoveAllToken);
        }

        public Task<bool> UndoMoveAllWidgetSurfacesAsync(WidgetMoveAllUndoToken token) =>
            Task.FromResult(ReferenceEquals(token, _lastMoveAllToken));

        public Task ApplyScreenBindingAsync(
            string widgetId,
            WidgetScreenBindingMode mode,
            string? boundScreenId,
            bool persistImmediately = true)
        {
            Binding = (widgetId, mode, boundScreenId);
            return Task.CompletedTask;
        }

        public IReadOnlyList<FolderWatcherHealthSnapshot>? GetFolderWatcherHealthSnapshots() =>
        [
            new FolderWatcherHealthSnapshot("C:\\watch", FolderWatcherHealth.Watching, true, true, false, 0, null, null),
        ];

        public OrganizationHistoryEntry? GetLatestUndoableDesktopOrganizationEntry() => LatestEntry;

        public OrganizationHistoryEntry? GetDesktopOrganizationHistoryEntry(string historyId) =>
            historyId == "h1" ? LatestEntry : null;

        public Task<DesktopOrganizationUndoResult> UndoDesktopOrganizationAsync(
            string historyId,
            nint ownerWindowHandle)
        {
            OrgUndo = (historyId, ownerWindowHandle);
            return Task.FromResult(new DesktopOrganizationUndoResult(
                DesktopOrganizationUndoOutcome.Incomplete,
                RestoredCount: 2,
                RemainingCount: 1));
        }

        public Task<DesktopOrganizationUndoResult> AbandonDesktopOrganizationUndoAsync(string historyId) =>
            Task.FromResult(new DesktopOrganizationUndoResult(DesktopOrganizationUndoOutcome.Completed));
    }

    private sealed class FakeAppActions : ISettingsAppActions
    {
        public bool ShutdownForRestartCalled;
        public bool ShutdownForUpdateCalled;
        public string? ExplorerRelaunchArgs;
        public bool OnboardingShown;
        public nint DesktopOrgOwnerHwnd;
        public bool TrayIconRefreshed;
        public bool? SnapEnabledArg;
        public double SnapSpacingArg;
        public (nint Hwnd, string Reason)? RaiseBand;
        public (nint Hwnd, string Reason)? ReleaseBand;
        public bool HasShortcut = true;
        public bool CreateShortcutResult = true;
        public string? SyncedShortcutRoot;
        public string ExportResult = "C:\\diag.zip";
        public bool UploadResult = true;
        public AppRuntimeHealthSnapshot? RuntimeHealth = null;
        public DragDropPermissionDiagnostic Diagnostic = new(
            DragDropDiagnosticSeverity.Warning,
            DragDropDiagnosticIssue.None,
            "summary",
            "detail",
            "Medium",
            "Medium",
            "On",
            "None",
            "On",
            "Ok",
            false,
            false,
            false,
            false,
            false,
            false,
            false);

        public event Action? DisplayTopologyChanged;

        public event Action? ScreenHomeChanged;

        public void RaiseDisplayTopologyChanged() => DisplayTopologyChanged?.Invoke();

        public Task ShutdownForRestartAsync()
        {
            ShutdownForRestartCalled = true;
            return Task.CompletedTask;
        }

        public Task ShutdownForUpdateAsync()
        {
            ShutdownForUpdateCalled = true;
            return Task.CompletedTask;
        }

        public AppRelaunchScheduleResult ScheduleRestartAfterCurrentProcessExit() =>
            new(true, null);

        public bool TryRelaunchAsExplorerUser(string arguments)
        {
            ExplorerRelaunchArgs = arguments;
            return true;
        }

        public void ShowOnboarding(bool resumeProgress = false) => OnboardingShown = true;

        public void ShowDesktopOrganizationWindow(nint ownerWindowHandle = 0) =>
            DesktopOrgOwnerHwnd = ownerWindowHandle;

        public void OpenReleaseNotesWindow(
            AppUpdateManifest? manifest,
            string version,
            Func<CancellationToken, Task<AppUpdateManifest?>>? manifestLoader,
            nint ownerWindowHandle = 0)
        {
        }

        public void UpdateTrayIcon() => TrayIconRefreshed = true;

        public void SetSnapEnabled(bool value) => SnapEnabledArg = value;

        public void SetSnapSpacingDips(double value) => SnapSpacingArg = value;

        public void ScheduleLightMemoryCleanup()
        {
        }

        public void ScheduleSettingsHiddenCleanup()
        {
        }

        public void BringSettingsWindowToRaisedBand(nint windowHandle, string reason) =>
            RaiseBand = (windowHandle, reason);

        public void ReleaseRaisedBandGuest(nint windowHandle, string reason) =>
            ReleaseBand = (windowHandle, reason);

        public bool HasManagedStorageDesktopShortcut() => HasShortcut;

        public Task<bool> CreateManagedStorageDesktopShortcutAsync() => Task.FromResult(CreateShortcutResult);

        public Task<bool> RemoveManagedStorageDesktopShortcutAsync() => Task.FromResult(true);

        public Task SyncManagedStorageDesktopShortcutAsync(string oldRootPath)
        {
            SyncedShortcutRoot = oldRootPath;
            return Task.CompletedTask;
        }

        public Task<string> ExportDiagnosticBundleAsync(string destinationDirectory) =>
            Task.FromResult(ExportResult);

        public Task<bool> UploadDiagnosticsForFeedbackAsync(long feedbackId, string tempDirectory) =>
            Task.FromResult(UploadResult);

        public AppRuntimeHealthSnapshot? GetRuntimeHealthSnapshot() => RuntimeHealth;

        public Task ForceExternalStateRecoveryAsync() => Task.CompletedTask;

        public DragDropPermissionDiagnostic DiagnoseDragDropPermissions() => Diagnostic;

        public DragDropPermissionRepairResult RepairDragDropPermissions() =>
            new(true, false, 2, string.Empty, false);
    }

    [Fact]
    public async Task RestoreFacade_RoundTripsInventoryAndPreparation()
    {
        var settings = new SettingsService(_tempRoot);
        var loop = await StartLoopbackAsync(PipeName(), settings);
        try
        {
            var remote = new RemoteSettingsRestoreActions(loop.Client);

            DeskBoxRestorePreparation preparation = await remote.PrepareRestoreAsync("archive.zip");
            Assert.Equal("1.6.0", preparation.AppVersion);
            Assert.Equal(3, preparation.FileCount);

            Assert.True(await remote.ConfirmPendingFullRestoreAsync());
            await remote.CancelPendingRestoreAsync();
            Assert.True(loop.Restore.Cancelled);

            IReadOnlyList<DeskBoxBackupSnapshotInfo> inventory = await remote.GetSnapshotInventoryAsync();
            Assert.Single(inventory);
            Assert.True(inventory[0].IsReadable);

            await remote.DeleteSnapshotAsync("snap.zip");
            Assert.Equal("snap.zip", loop.Restore.DeletedSnapshot);

            DeskBoxAttachmentHealthReport report = await remote.ScanAttachmentHealthAsync();
            Assert.True(loop.Restore.Scanned);
            Assert.Equal(5, report.ReferencedFileCount);
        }
        finally
        {
            await loop.Client.DisposeAsync();
            await loop.Server.DisposeAsync();
        }
    }

    [Fact]
    public async Task WidgetFacade_RoundTripsRecordsListsAndUndoHandle()
    {
        var settings = new SettingsService(_tempRoot);
        var loop = await StartLoopbackAsync(PipeName(), settings);
        try
        {
            var hostMirror = new SettingsService(Path.Combine(_tempRoot, "widget-host"));
            await hostMirror.LoadAsync();
            var remote = new RemoteSettingsWidgetActions(loop.Client, hostMirror);

            Assert.True(remote.IsHostAvailable);
            Assert.True(remote.IsFeatureWidgetEnabled(WidgetKind.Todo));
            Assert.Equal(WidgetKind.Todo, loop.Widget.EnabledArg);

            remote.RefreshVisibleWidgetDesktopLayers("test-layer");
            Assert.Equal("test-layer", loop.Widget.LayerReason);
            remote.NotifyWidgetGroupPresentationSettingsChanged();
            Assert.True(loop.Widget.NotifiedGroup);

            Assert.NotNull(remote.GetOrphanManagedStorageFolders());
            Assert.Single(remote.GetOrphanManagedStorageFolders()!);

            Assert.Equal(2, await remote.RestoreOrphanManagedStorageFoldersAsync(["a", "b"]));

            await remote.MoveOrphanManagedStorageFolderContentsToDesktopAsync("C:\\orphan");
            Assert.Equal("C:\\orphan", loop.Widget.MovedOrphan);

            await remote.DeleteOrphanManagedStorageFolderAsync("C:\\orphan", 0x1234);
            Assert.Equal(("C:\\orphan", (nint)0x1234), loop.Widget.DeletedOrphan);

            Assert.Equal(6, remote.GetDefaultManagedStorageWidgetCount());

            // Migration with progress: the pushed event must land in the
            // caller's IProgress while the request is in flight.
            var progressReports = new List<ManagedStorageMigrationProgress>();
            ManagedStorageMigrationResult migration = await remote.UpdateDefaultManagedStorageRootAsync(
                "C:\\new-root",
                new ManagedStorageMigrationOptions(
                    Progress: new Progress<ManagedStorageMigrationProgress>(progressReports.Add)));
            Assert.Equal("C:\\new-root", loop.Widget.MigrationRoot);
            Assert.Equal(2, migration.AffectedWidgetCount);
            await Task.Delay(300);
            Assert.Contains(progressReports, report => report.Phase == ManagedStorageMigrationPhase.Copying);

            IReadOnlyList<GlanceWidgetInstanceInfo> glance = remote.GetGlanceWidgetInstances();
            Assert.Equal(2, glance.Count);
            Assert.True(remote.IsGlanceFeatureEnabled);
            GlanceWidgetInstanceInfo created = await remote.CreateGlanceWidgetAsync("g1");
            Assert.Equal("g1", created.Name);
            await remote.SetGlanceWidgetInstanceEnabledAsync("g1", false);
            Assert.True(await remote.LocateGlanceWidgetAsync("g1"));
            await remote.RemoveGlanceWidgetAsync("g1");
            await remote.RenameWidgetAsync("g2", "Renamed");
            Assert.Equal("g2:Renamed", loop.Widget.RenamedGlance);

            // Move-all + undo across the handle registry.
            (int moved, WidgetMoveAllUndoToken token) = await remote.MoveAllWidgetSurfacesToDisplayAsync("screen-1");
            Assert.Equal(4, moved);
            Assert.NotNull(token.IpcHandleId);
            Assert.True(await remote.UndoMoveAllWidgetSurfacesAsync(token));

            await remote.ApplyScreenBindingAsync("w1", WidgetScreenBindingMode.Pinned, "screen-2");
            Assert.Equal(("w1", WidgetScreenBindingMode.Pinned, "screen-2"), loop.Widget.Binding);

            Assert.NotNull(remote.GetFolderWatcherHealthSnapshots());

            OrganizationHistoryEntry? latest = remote.GetLatestUndoableDesktopOrganizationEntry();
            Assert.NotNull(latest);
            Assert.Equal("h1", latest!.Id);
            Assert.NotNull(remote.GetDesktopOrganizationHistoryEntry("h1"));
            Assert.Null(remote.GetDesktopOrganizationHistoryEntry("nope"));

            DesktopOrganizationUndoResult undo = await remote.UndoDesktopOrganizationAsync("h1", 0x77);
            Assert.Equal(("h1", (nint)0x77), loop.Widget.OrgUndo);
            Assert.Equal(DesktopOrganizationUndoOutcome.Incomplete, undo.Outcome);
            Assert.Equal(2, undo.RestoredCount);
            Assert.Equal(1, undo.RemainingCount);

            DesktopOrganizationUndoResult abandon = await remote.AbandonDesktopOrganizationUndoAsync("h1");
            Assert.Equal(DesktopOrganizationUndoOutcome.Completed, abandon.Outcome);
        }
        finally
        {
            await loop.Client.DisposeAsync();
            await loop.Server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AppFacade_RoundTripsCommandsAndEvents()
    {
        var settings = new SettingsService(_tempRoot);
        var loop = await StartLoopbackAsync(PipeName(), settings);
        try
        {
            var remote = new RemoteSettingsAppActions(loop.Client);

            var topologyChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            remote.DisplayTopologyChanged += () => topologyChanged.TrySetResult();

            await remote.ShutdownForRestartAsync();
            Assert.True(loop.AppActions.ShutdownForRestartCalled);
            await remote.ShutdownForUpdateAsync();
            Assert.True(loop.AppActions.ShutdownForUpdateCalled);

            Assert.True(remote.TryRelaunchAsExplorerUser("--args"));
            Assert.Equal("--args", loop.AppActions.ExplorerRelaunchArgs);

            remote.ShowOnboarding();
            Assert.True(loop.AppActions.OnboardingShown);

            remote.ShowDesktopOrganizationWindow(0x42);
            // Fire-and-forget since the window build must not block the host
            // UI thread; the owner lands after the UI-thread marshal.
            var ownerDeadline = System.Diagnostics.Stopwatch.StartNew();
            while (loop.AppActions.DesktopOrgOwnerHwnd != (nint)0x42 &&
                   ownerDeadline.ElapsedMilliseconds < 5000)
            {
                await Task.Delay(50);
            }
            Assert.Equal((nint)0x42, loop.AppActions.DesktopOrgOwnerHwnd);

            remote.UpdateTrayIcon();
            Assert.True(loop.AppActions.TrayIconRefreshed);

            remote.SetSnapEnabled(false);
            Assert.False(loop.AppActions.SnapEnabledArg);
            remote.SetSnapSpacingDips(12.5);
            Assert.Equal(12.5, loop.AppActions.SnapSpacingArg);

            remote.BringSettingsWindowToRaisedBand(0x99, "shown");
            Assert.Equal(((nint)0x99, "shown"), loop.AppActions.RaiseBand);
            remote.ReleaseRaisedBandGuest(0x99, "hidden");
            Assert.Equal(((nint)0x99, "hidden"), loop.AppActions.ReleaseBand);

            Assert.True(remote.HasManagedStorageDesktopShortcut());
            Assert.True(await remote.CreateManagedStorageDesktopShortcutAsync());
            await remote.SyncManagedStorageDesktopShortcutAsync("C:\\old");
            Assert.Equal("C:\\old", loop.AppActions.SyncedShortcutRoot);

            Assert.Equal("C:\\diag.zip", await remote.ExportDiagnosticBundleAsync("C:\\out"));
            Assert.True(await remote.UploadDiagnosticsForFeedbackAsync(55, "ignored"));

            Assert.Null(remote.GetRuntimeHealthSnapshot());

            DragDropPermissionDiagnostic diagnostic = remote.DiagnoseDragDropPermissions();
            Assert.Equal("summary", diagnostic.Summary);
            Assert.Equal("Medium", diagnostic.CurrentProcessIntegrity);

            DragDropPermissionRepairResult repair = remote.RepairDragDropPermissions();
            Assert.True(repair.Success);
            Assert.Equal(2, repair.RepairedCount);

            await loop.Server.SendEventAsync(SettingsIpcMethods.AppDisplayTopologyChanged, null);
            await topologyChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await loop.Client.DisposeAsync();
            await loop.Server.DisposeAsync();
        }
    }


    private sealed class FakeBackupCommands : IBackupCommands
    {
        public bool Stopping;
        public string? ExportDir;

        public LocalBackupResult SnapshotResult = new(BackupOutcome.Succeeded);

        public CloudBackupRunResult UploadResult = new(true, "remote.zip", 1);

        public bool IsStopping => Stopping;

        public Task<LocalBackupResult> CreateSnapshotNowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(SnapshotResult);

        public Task<string> ExportAsync(string destinationDirectory, CancellationToken cancellationToken = default)
        {
            ExportDir = destinationDirectory;
            return Task.FromResult("C:\\backup.zip");
        }

        public Task<CloudBackupRunResult> UploadNowAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(UploadResult);
    }

    private sealed class FakeBackupRestore : IBackupRestoreActions
    {
        public bool Stopped;
        public BackupEndpoint? EndpointArg;
        public string? DeletedSnapshotName;
        public (string Endpoint, string Name, string Directory)? DownloadTarget;
        public (string Archive, CloudBackupDomain Scope)? ScopedRestore;
        public bool Cancelled;
        public bool? ReplaceMode;
        public string? DeletedTempDownload;

        public Task StopAsync()
        {
            Stopped = true;
            return Task.CompletedTask;
        }

        public bool IsCurrentEndpoint(BackupEndpoint endpoint)
        {
            EndpointArg = endpoint;
            return true;
        }

        public Task DeleteSnapshotAsync(BackupEndpoint endpoint, string name, CancellationToken cancellationToken = default)
        {
            DeletedSnapshotName = name;
            return Task.CompletedTask;
        }

        public Task<string> DownloadSnapshotAsync(BackupEndpoint endpoint, string name, string directory, CancellationToken cancellationToken = default)
        {
            DownloadTarget = (endpoint.Provider, name, directory);
            return Task.FromResult("C:\\dl\\snap.zip");
        }

        public Task<DeskBoxRestorePreparation> PrepareScopedRestoreAsync(string archivePath, CloudBackupDomain scope, CancellationToken cancellationToken = default)
        {
            ScopedRestore = (archivePath, scope);
            return Task.FromResult(new DeskBoxRestorePreparation(
                DateTimeOffset.UtcNow, "1.6.0", 1, 10, 15, true));
        }

        public Task CancelPendingRestoreAsync(CancellationToken cancellationToken = default)
        {
            Cancelled = true;
            return Task.CompletedTask;
        }

        public Task<bool> SetPendingRestoreItemReplaceModeAsync(bool replace, CancellationToken cancellationToken = default)
        {
            ReplaceMode = replace;
            return Task.FromResult(true);
        }

        public AppRelaunchScheduleResult ScheduleRelaunch() => new(true, null);

        public Task ShutdownForRestartAsync() => Task.CompletedTask;

        public void DeleteTemporaryDownload(string? directory) => DeletedTempDownload = directory;
    }

    private sealed class FakeBackupSettings : IBackupSettings
    {
        public BackupSettingsSnapshot Snapshot = new(
            true, 30, 3, "C:\\backups", "C:\\backups", "C:\\backups", false,
            "webdav", "https://cloud", "/deskbox", "user",
            true, false, true, 60, 5, 100, 0, 0,
            new BackupEndpoint("webdav", "https://cloud", "/deskbox", "user"),
            true);
        public BackupSettingsChange? LastChange;
        public string? LastValidDir;
        public BackupEndpoint? CredentialEndpoint;
        public BackupEndpoint? ProbedEndpoint;
        public BackupEndpoint? ListedEndpoint;

        public event Action<BackupUploadNotification>? UploadCompleted;

        public BackupSettingsSnapshot Read() => Snapshot;

        public void Update(BackupSettingsChange change) => LastChange = change;

        public bool IsValidLocalDirectory(string path, out string? rejectionReasonKey)
        {
            LastValidDir = path;
            rejectionReasonKey = path == "C:\\bad" ? "Settings.Backup.DirectoryInvalid" : null;
            return path != "C:\\bad";
        }

        public Task SaveAsync() => Task.CompletedTask;

        public Task<bool> HasCredentialAsync(BackupEndpoint endpoint, CancellationToken cancellationToken)
        {
            CredentialEndpoint = endpoint;
            return Task.FromResult(true);
        }

        public Task SaveCredentialAsync(BackupEndpoint endpoint, string secret, CancellationToken cancellationToken)
        {
            CredentialEndpoint = endpoint;
            return Task.CompletedTask;
        }

        public Task ProbeAsync(BackupEndpoint endpoint, string? secretOverride, CancellationToken cancellationToken)
        {
            ProbedEndpoint = endpoint;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<BackupRemoteSnapshot>> ListAsync(BackupEndpoint endpoint, CancellationToken cancellationToken)
        {
            ListedEndpoint = endpoint;
            return Task.FromResult<IReadOnlyList<BackupRemoteSnapshot>>(
            [
                new("snap-1.zip", 2048, DateTimeOffset.UtcNow),
            ]);
        }
    }

    private sealed class FakeUpdateService : IAppUpdateService
    {
        public AppUpdateCheckResult CheckResult = new(
            DeskBox.Models.AppUpdateCheckStatus.UpdateAvailable,
            "1.5.9",
            new DeskBox.Models.AppUpdateManifest());
        public string? CheckVersionArg;
        public DeskBox.Models.AppUpdateManifest? DownloadManifestArg;
        public string? InstallerPathArg;
        public string InstallerUrlResult = "https://update/next";

        public event Action<DeskBox.Models.AppUpdateCheckResult>? CheckCompleted;

        public event Action<DeskBox.Models.AppUpdateDownloadProgress>? DownloadProgressChanged;

        public event Action<DeskBox.Models.AppUpdateDownloadResult>? DownloadCompleted;

        public AppUpdateCheckResult? LastCheckResult => CheckResult;

        public DateTime? LastCheckTimeUtc => new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

        public string ManifestUrl => InstallerUrlResult;

        public AppUpdateDeliveryKind DeliveryKind => AppUpdateDeliveryKind.DirectInstaller;

        public string? LastDownloadedInstallerPath => "C:\\downloads\\setup.exe";

        public bool IsDownloadInProgress => false;

        public Task<AppUpdateCheckResult> CheckForUpdatesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CheckResult);

        public Task<AppUpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken = default)
        {
            CheckVersionArg = currentVersion;
            return Task.FromResult(CheckResult);
        }

        public Task<DeskBox.Models.AppUpdateDownloadResult> DownloadUpdateAsync(
            DeskBox.Models.AppUpdateManifest manifest,
            IProgress<DeskBox.Models.AppUpdateDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            DownloadManifestArg = manifest;
            return Task.FromResult(DeskBox.Models.AppUpdateDownloadResult.Completed("C:/downloads/setup.exe"));
        }

        public void ClearLastDownloadedInstaller()
        {
        }

        public void PruneStaleInstallerDirectories()
        {
        }

        public AppUpdateInstallResult StartInstallerHelper(string installerPath, bool silent = true, string? appCultureName = null)
        {
            InstallerPathArg = installerPath;
            return AppUpdateInstallResult.Started();
        }
    }

    [Fact]
    public async Task BackupSurfaces_RoundTripCommandsSettingsAndCloudActions()
    {
        var settings = new SettingsService(_tempRoot);
        var loop = await StartLoopbackAsync(PipeName(), settings);
        try
        {
            var commands = new RemoteBackupCommands(loop.Client);
            var backupSettings = new RemoteBackupSettings(loop.Client);
            var cloud = new RemoteBackupRestoreActions(loop.Client);

            // IBackupCommands
            Assert.False(commands.IsStopping);
            LocalBackupResult snapshot = await commands.CreateSnapshotNowAsync();
            Assert.Equal(BackupOutcome.Succeeded, snapshot.Outcome);
            Assert.Equal("C:\\backup.zip", await commands.ExportAsync("C:\\out"));
            Assert.Equal("C:\\out", loop.BackupCommands.ExportDir);
            CloudBackupRunResult upload = await commands.UploadNowAsync();
            Assert.True(upload.Uploaded);

            // IBackupSettings
            BackupSettingsSnapshot read = backupSettings.Read();
            Assert.True(read.LocalEnabled);
            Assert.Equal("webdav", read.CloudProvider);
            Assert.True(read.HasEndpoint);

            backupSettings.Update(new BackupSettingsChange(LocalEnabled: false));
            Assert.False(loop.BackupSettings.LastChange!.LocalEnabled);

            Assert.True(backupSettings.IsValidLocalDirectory("C:\\good", out string? goodReason));
            Assert.Null(goodReason);
            Assert.False(backupSettings.IsValidLocalDirectory("C:\\bad", out string? badReason));
            Assert.NotNull(badReason);
            Assert.Equal("C:\\bad", loop.BackupSettings.LastValidDir);

            var endpoint = new BackupEndpoint("webdav", "https://cloud", "/deskbox", "user");
            Assert.True(await backupSettings.HasCredentialAsync(endpoint, CancellationToken.None));
            await backupSettings.SaveCredentialAsync(endpoint, "secret", CancellationToken.None);
            Assert.Equal(endpoint, loop.BackupSettings.CredentialEndpoint);
            await backupSettings.ProbeAsync(endpoint, "override", CancellationToken.None);
            Assert.Equal(endpoint, loop.BackupSettings.ProbedEndpoint);
            IReadOnlyList<BackupRemoteSnapshot> snapshots = await backupSettings.ListAsync(endpoint, CancellationToken.None);
            var remoteSnapshot = Assert.Single(snapshots);
            Assert.Equal("snap-1.zip", remoteSnapshot.Name);

            // IBackupRestoreActions
            await cloud.StopAsync();
            Assert.True(loop.BackupRestore.Stopped);
            Assert.True(cloud.IsCurrentEndpoint(endpoint));
            await cloud.DeleteSnapshotAsync(endpoint, "snap-1.zip");
            Assert.Equal("snap-1.zip", loop.BackupRestore.DeletedSnapshotName);
            Assert.Equal("C:\\dl\\snap.zip", await cloud.DownloadSnapshotAsync(endpoint, "snap-1.zip", "C:\\dl"));
            Assert.Equal(("webdav", "snap-1.zip", "C:\\dl"), loop.BackupRestore.DownloadTarget);
            DeskBoxRestorePreparation preparation = await cloud.PrepareScopedRestoreAsync("C:\\dl\\snap.zip", CloudBackupDomain.TodoData);
            Assert.Equal("1.6.0", preparation.AppVersion);
            Assert.Equal(("C:\\dl\\snap.zip", CloudBackupDomain.TodoData), loop.BackupRestore.ScopedRestore);
            await cloud.CancelPendingRestoreAsync();
            Assert.True(loop.BackupRestore.Cancelled);
            Assert.True(await cloud.SetPendingRestoreItemReplaceModeAsync(true));
            Assert.True(loop.BackupRestore.ReplaceMode);
            Assert.True(cloud.ScheduleRelaunch().Started);
            await cloud.ShutdownForRestartAsync();
            cloud.DeleteTemporaryDownload("C:\\dl");
            Assert.Equal("C:\\dl", loop.BackupRestore.DeletedTempDownload);
        }
        finally
        {
            await loop.Client.DisposeAsync();
            await loop.Server.DisposeAsync();
        }
    }

    [Fact]
    public async Task UpdateService_RoundTripsStateCheckDownloadAndEvents()
    {
        var settings = new SettingsService(_tempRoot);
        var loop = await StartLoopbackAsync(PipeName(), settings);
        try
        {
            var remote = new RemoteAppUpdateService(loop.Client);

            var checkCompleted = new TaskCompletionSource<DeskBox.Models.AppUpdateCheckResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            remote.CheckCompleted += result => checkCompleted.TrySetResult(result);

            Assert.NotNull(remote.LastCheckResult);
            Assert.Equal(new DateTime(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc), remote.LastCheckTimeUtc);
            Assert.Equal("https://update/next", remote.ManifestUrl);
            Assert.Equal(AppUpdateDeliveryKind.DirectInstaller, remote.DeliveryKind);
            Assert.Equal("C:\\downloads\\setup.exe", remote.LastDownloadedInstallerPath);
            Assert.False(remote.IsDownloadInProgress);

            DeskBox.Models.AppUpdateCheckResult check = await remote.CheckForUpdatesAsync("1.5.9");
            Assert.Equal("1.5.9", check.CurrentVersion);
            Assert.Equal("1.5.9", loop.UpdateService.CheckVersionArg);

            DeskBox.Models.AppUpdateDownloadResult download =
                await remote.DownloadUpdateAsync(new DeskBox.Models.AppUpdateManifest());
            Assert.NotNull(download);
            Assert.NotNull(loop.UpdateService.DownloadManifestArg);

            AppUpdateInstallResult install = remote.StartInstallerHelper("C:\\downloads\\setup.exe");
            Assert.Equal("C:\\downloads\\setup.exe", loop.UpdateService.InstallerPathArg);

            // Event push reaches the remote subscriber.
            await loop.Server.SendEventAsync(
                SettingsIpcMethods.UpdateCheckCompleted,
                SettingsIpcPayload.Encode(
                    loop.UpdateService.CheckResult,
                    SettingsIpcJsonContext.Default.AppUpdateCheckResult));
            DeskBox.Models.AppUpdateCheckResult pushed = await checkCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("1.5.9", pushed.CurrentVersion);
        }
        finally
        {
            await loop.Client.DisposeAsync();
            await loop.Server.DisposeAsync();
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
