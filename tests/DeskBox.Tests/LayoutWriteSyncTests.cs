using DeskBox.Contracts;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Batch R2: the layout write commands and the external layout merge —
/// the write-drop family (group presentation, nav defaults, capsule
/// resets, placement preferences) and the stale-mirror family.
/// </summary>
public sealed class LayoutWriteSyncTests : IDisposable
{
    private readonly string _tempRoot;

    public LayoutWriteSyncTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    [Fact]
    public void ApplyExternalLayout_MergesSliceAndBroadcasts()
    {
        var service = new SettingsService(_tempRoot);
        var authoritative = new SettingsService(_tempRoot);
        authoritative.LoadAsync().GetAwaiter().GetResult();
        authoritative.Settings.Widgets.Add(new Models.WidgetConfig
        {
            Id = "w-main",
            WidgetKind = Models.WidgetKind.Music,
        });
        authoritative.Settings.WidgetLayout.FeatureWidgetEnabledStates["Music"] = true;
        string slice = authoritative.SerializeCurrentLayout();

        int changed = 0;
        service.LoadAsync().GetAwaiter().GetResult();
        service.SettingsChanged += () => changed++;
        Assert.DoesNotContain(service.Settings.Widgets, w => w.Id == "w-main");

        Assert.True(service.ApplyExternalLayout(slice));
        Assert.Contains(service.Settings.Widgets, w => w.Id == "w-main");
        Assert.True(service.Settings.WidgetLayout.FeatureWidgetEnabledStates["Music"]);
        Assert.Equal(1, changed);

        // Garbage and empty refuse; nothing changes.
        Assert.False(service.ApplyExternalLayout("{ broken"));
        Assert.False(service.ApplyExternalLayout(string.Empty));
        Assert.Single(service.Settings.Widgets);
    }

    [Fact]
    public async Task LayoutWrites_RoundTripThroughThePipe()
    {
        var main = new SettingsService(_tempRoot);
        await main.LoadAsync();
        main.Settings.WidgetGroups.Add(new Models.WidgetGroupConfig
        {
            Id = "g1",
            Name = "One",
        });
        await main.SaveAsync();

        var widget = new FakeWidgetFacade();
        var router = new SettingsIpcMethodRouter(main, null, null, restore: null, widget: widget);

        // Direct router dispatch covers the wire contract for the writes +
        // the slice responses; the pipe layer is already covered by the
        // facade loopback family.
        var presentation = new GroupPresentationChange(
            "g1",
            NavigationStyle: "Capsule",
            WheelSwitch: GroupPresentationTriState.Null);
        var (ok1, slice1, _) = await router.DispatchAsync(
            SettingsIpcMethods.WidgetApplyGroupPresentation,
            SettingsIpcPayload.Encode(presentation, SettingsIpcJsonContext.Default.GroupPresentationChange),
            CancellationToken.None);
        Assert.True(ok1, "presentation write failed");
        Assert.NotNull(slice1);
        Assert.Contains(widget.Presentations, p => p.GroupId == "g1" && p.NavigationStyle == "Capsule");

        var (okRead, sliceRead, _) = await router.DispatchAsync(
            SettingsIpcMethods.WidgetLayoutRead, null, CancellationToken.None);
        Assert.True(okRead);
        Assert.False(string.IsNullOrEmpty(sliceRead));

        var (okCapsule, _, _) = await router.DispatchAsync(
            SettingsIpcMethods.WidgetResetCapsuleOverrides,
            SettingsIpcPayload.Encode(
                new CapsuleOverrideReset(CapsuleOverrideResetScope.All),
                SettingsIpcJsonContext.Default.CapsuleOverrideReset),
            CancellationToken.None);
        Assert.True(okCapsule);
        Assert.Contains(widget.Resets, r => r.Scope == CapsuleOverrideResetScope.All);
    }

    [Fact]
    public async Task AppearanceClear_RoutesThroughThePipeAsTheFifthLayoutWrite()
    {
        var main = new SettingsService(_tempRoot);
        await main.LoadAsync();

        var widget = new FakeWidgetFacade();
        var router = new SettingsIpcMethodRouter(main, null, null, restore: null, widget: widget);

        var (ok, slice, _) = await router.DispatchAsync(
            SettingsIpcMethods.AppearanceClearPerWidgetBackgrounds,
            string.Empty,
            CancellationToken.None);
        Assert.True(ok, "clear write failed");
        Assert.False(string.IsNullOrEmpty(slice),
            "the response must carry the layout slice for the mirror merge");
        Assert.Equal(1, widget.ClearPerWidgetBackgroundsCalls);
    }

    [Fact]
    public void AdoptExternalSnapshot_MirrorsTheMainGraphWithoutADiskLoad()
    {
        var main = new SettingsService(_tempRoot);
        main.LoadAsync().GetAwaiter().GetResult();
        main.Settings.LastRunVersion = "adopt-roundtrip";
        main.Settings.Widgets.Add(new Models.WidgetConfig
        {
            Id = "w-adopt",
            WidgetKind = Models.WidgetKind.Glance,
        });
        main.Settings.WidgetLayout.FeatureWidgetEnabledStates["Todo"] = true;
        string json = main.SerializeCurrentSettings();

        // Host boot shape: a fresh service that never disk-loads. Adoption
        // alone must produce the complete mirror — settings, main-owned
        // runtime markers (verbatim here; the host never authors them), and
        // the layout domain riding inside the graph.
        var host = new SettingsService(_tempRoot);
        host.AdoptExternalSnapshot(json);
        Assert.Equal("adopt-roundtrip", host.Settings.LastRunVersion);
        Assert.Contains(host.Settings.Widgets, w => w.Id == "w-adopt");
        Assert.True(host.Settings.WidgetLayout.FeatureWidgetEnabledStates["Todo"]);

        // Malformed payloads throw so the host boot fails loudly instead of
        // running on a fabricated graph.
        Assert.Throws<InvalidOperationException>(() => host.AdoptExternalSnapshot("{ broken"));
    }

    private sealed class FakeWidgetFacade : ISettingsWidgetActions
    {
        public List<GroupPresentationChange> Presentations { get; } = [];
        public List<CapsuleOverrideReset> Resets { get; } = [];

        public bool IsHostAvailable => true;
        public bool IsFeatureWidgetEnabled(Models.WidgetKind kind) => true;
        public Task<bool> SetFeatureWidgetEnabled(Models.WidgetKind kind, bool enabled) => Task.FromResult(true);
        public Task ApplyGroupPresentation(GroupPresentationChange change)
        {
            Presentations.Add(change);
            return Task.CompletedTask;
        }

        public Task ApplyGroupNavigationDefaults(GroupNavigationDefaultsChange change) => Task.CompletedTask;
        public Task ApplyLayoutPreferences(LayoutPreferencesChange change) => Task.CompletedTask;
        public Task ResetCapsuleOverrides(CapsuleOverrideReset reset)
        {
            Resets.Add(reset);
            return Task.CompletedTask;
        }

        public int ClearPerWidgetBackgroundsCalls { get; set; }
        public Task ClearPerWidgetBackgrounds()
        {
            ClearPerWidgetBackgroundsCalls++;
            return Task.CompletedTask;
        }

        public Task ResetFeatureWidgetAsync(Models.WidgetKind kind) => Task.CompletedTask;
        public void RefreshVisibleWidgetDesktopLayers(string reason) { }
        public void NotifyWidgetGroupPresentationSettingsChanged() { }
        public Task<bool> ReorderWidgetGroupMemberAsync(string sourceWidgetId, string targetWidgetId) => Task.FromResult(true);
        public Task<bool> RemoveWidgetFromGroupAsync(string widgetId) => Task.FromResult(true);
        public Task<bool> DissolveWidgetGroupContainingAsync(string widgetId) => Task.FromResult(true);
        public IReadOnlyList<Services.ManagedStorageFolderCleanupCandidate>? GetOrphanManagedStorageFolders() => null;
        public Task<int> RestoreOrphanManagedStorageFoldersAsync(IReadOnlyList<string> folderPaths) => Task.FromResult(0);
        public Task MoveOrphanManagedStorageFolderContentsToDesktopAsync(string folderPath) => Task.CompletedTask;
        public Task DeleteOrphanManagedStorageFolderAsync(string folderPath, nint ownerWindowHandle) => Task.CompletedTask;
        public int GetDefaultManagedStorageWidgetCount() => 0;
        public Task<Services.ManagedStorageMigrationPreview> PreviewManagedStorageMigrationAsync(string newRootPath, CancellationToken token) => throw new NotImplementedException();
        public Task<Services.ManagedStorageMigrationResult> UpdateDefaultManagedStorageRootAsync(string newRootPath, Services.ManagedStorageMigrationOptions? options) => throw new NotImplementedException();
        public IReadOnlyList<Services.GlanceWidgetInstanceInfo> GetGlanceWidgetInstances() => [];
        public bool IsGlanceFeatureEnabled => false;
        public Task<Services.GlanceWidgetInstanceInfo> CreateGlanceWidgetAsync(string? sourceWidgetId) => throw new NotImplementedException();
        public Task SetGlanceWidgetInstanceEnabledAsync(string widgetId, bool enabled) => Task.CompletedTask;
        public Task<bool> LocateGlanceWidgetAsync(string widgetId) => Task.FromResult(true);
        public Task RemoveGlanceWidgetAsync(string widgetId) => Task.CompletedTask;
        public Task RenameWidgetAsync(string widgetId, string newName) => Task.CompletedTask;
        public Task<(int MovedCount, Services.WidgetMoveAllUndoToken Token)> MoveAllWidgetSurfacesToDisplayAsync(string displayStableId) => throw new NotImplementedException();
        public Task<bool> UndoMoveAllWidgetSurfacesAsync(Services.WidgetMoveAllUndoToken token) => Task.FromResult(true);
        public Task ApplyScreenBindingAsync(string widgetId, Models.WidgetScreenBindingMode mode, string? boundScreenId, bool persistImmediately = true) => Task.CompletedTask;
        public IReadOnlyList<Services.FolderWatcherHealthSnapshot>? GetFolderWatcherHealthSnapshots() => null;
        public Models.OrganizationHistoryEntry? GetLatestUndoableDesktopOrganizationEntry() => null;
        public Models.OrganizationHistoryEntry? GetDesktopOrganizationHistoryEntry(string historyId) => null;
        public Task<DesktopOrganizationUndoResult> UndoDesktopOrganizationAsync(string historyId, nint ownerWindowHandle) => throw new NotImplementedException();
        public Task<DesktopOrganizationUndoResult> AbandonDesktopOrganizationUndoAsync(string historyId) => throw new NotImplementedException();
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
