using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Batch 3a of the settings-process split: the persistence sink seam, the
/// external-snapshot apply path (with main-owned runtime marker
/// preservation), the disk-reload path (settings-host side of the reload
/// push), the commit-id echo-suppression token, and the diff-driven
/// external appliers.
/// </summary>
public sealed class SettingsExternalCommitTests : IDisposable
{
    private readonly string _tempRoot;

    public SettingsExternalCommitTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
    }

    private string SettingsRoot => _tempRoot;

    /// <summary>
    /// A forwarding sink standing in for the batch-3d IPC bridge. It captures
    /// commits without touching disk; the test then pumps them into the
    /// "main" service explicitly — the real bridge applies cross-process, and
    /// a same-process synchronous forward would deadlock on the shared
    /// per-directory commit gate the caller holds.
    /// </summary>
    private sealed class ForwardingSink : ISettingsPersistenceSink
    {
        public List<SettingsPersistenceCommit> Commits { get; } = [];

        public Task<bool> SaveAsync(SettingsService owner, SettingsPersistenceCommit commit)
        {
            Commits.Add(commit);
            return Task.FromResult(true);
        }

        public async Task<bool> PumpAsync(SettingsService main)
        {
            bool allApplied = true;
            foreach (SettingsPersistenceCommit commit in Commits)
            {
                string json = JsonSerializer.Serialize(
                    commit.Snapshot,
                    SettingsJsonContext.Default.AppSettings);
                allApplied &= await main.ApplyExternalSnapshotAsync(json);
            }

            return allApplied;
        }
    }

    [Fact]
    public async Task ForwardingSink_CapturesCommitAndTracksOutboundId()
    {
        var sink = new ForwardingSink();
        var service = new SettingsService(SettingsRoot, sink);

        Assert.True(await service.SaveCheckedAsync());
        Assert.True(await service.SaveCheckedAsync());

        Assert.Equal(2, sink.Commits.Count);
        long secondCommit = sink.Commits[1].CommitId;
        Assert.True(service.IsLatestOutboundCommit(secondCommit));
        Assert.False(service.IsLatestOutboundCommit(sink.Commits[0].CommitId));
        Assert.False(service.IsLatestOutboundCommit(0));
        Assert.False(File.Exists(Path.Combine(SettingsRoot, "settings.json")));
    }

    [Fact]
    public async Task ExternalSnapshot_AppliesBroadcastsAndPersists()
    {
        var main = new SettingsService(SettingsRoot);
        await main.LoadAsync();
        main.Settings.GlobalHotkeyEnabled = false;
        await main.SaveAsync();

        var sink = new ForwardingSink();
        var host = new SettingsService(SettingsRoot, sink);
        host.Settings.GlobalHotkeyEnabled = true;
        Assert.True(await host.SaveCheckedAsync());

        int changedCount = 0;
        host.SettingsChanged += () => changedCount++;
        object? appliedArgs = null;
        main.ExternalSnapshotApplied += (before, after) => appliedArgs = (before, after);
        int mainChanged = 0;
        main.SettingsChanged += () => mainChanged++;

        Assert.True(await sink.PumpAsync(main));
        Assert.True(main.Settings.GlobalHotkeyEnabled);
        // The host itself is not notified by its own outbound commit — only
        // the applying service broadcasts.
        Assert.Equal(0, changedCount);
        Assert.Equal(1, mainChanged);
        Assert.NotNull(appliedArgs);

        // Durable: a fresh service reading the same directory sees the host's
        // commit.
        var fresh = new SettingsService(SettingsRoot);
        await fresh.LoadAsync();
        Assert.True(fresh.Settings.GlobalHotkeyEnabled);
    }

    [Fact]
    public async Task ExternalSnapshot_PreservesMainOwnedRuntimeMarkers()
    {
        var main = new SettingsService(SettingsRoot);
        await main.LoadAsync();
        var updateCheckAt = DateTimeOffset.UtcNow.AddDays(-1);
        main.Settings.LastUpdateCheckAt = updateCheckAt;
        main.Settings.HasResolvedInitialFileWidgetSetup = true;
        await main.SaveAsync();

        // The host loaded before those markers flipped, so its snapshot
        // carries the stale values.
        var sink = new ForwardingSink();
        var host = new SettingsService(SettingsRoot, sink);
        Assert.Null(host.Settings.LastUpdateCheckAt);
        Assert.False(host.Settings.HasResolvedInitialFileWidgetSetup);
        host.Settings.FileStacksEnabled = !host.Settings.FileStacksEnabled;
        Assert.True(await host.SaveCheckedAsync());

        Assert.True(await sink.PumpAsync(main));
        Assert.Equal(updateCheckAt, main.Settings.LastUpdateCheckAt);
        Assert.True(main.Settings.HasResolvedInitialFileWidgetSetup);
    }

    [Fact]
    public async Task ExternalSnapshot_PreservesMainOwnedWidgetLayoutDomain()
    {
        var main = new SettingsService(SettingsRoot);
        await main.LoadAsync();
        // Live main-process layout state: the host snapshot was serialized
        // before this state existed, exactly like a real settings-host
        // session loaded before the user moved widgets around. The enabled
        // dictionary itself is HOST-owned (its only writer is the settings
        // UI), so the main process never mutates it out from under a host.
        main.Settings.Widgets.Add(new WidgetConfig { Id = "w-live", WidgetKind = WidgetKind.Music });

        // The host's dictionary drifted (stale false values from an older
        // load): the snapshot must NOT carry it — feature switches travel as
        // their own command now.
        main.Settings.WidgetLayout.FeatureWidgetEnabledStates["Music"] = true;
        var host = new SettingsService(SettingsRoot);
        await host.LoadAsync();
        host.Settings.WidgetLayout.FeatureWidgetEnabledStates["Music"] = false;
        Assert.DoesNotContain(host.Settings.Widgets, w => w.Id == "w-live");
        host.Settings.Theme = "Light";

        string payload = System.Text.Json.JsonSerializer.Serialize(
            host.Settings,
            SettingsJsonContext.Default.AppSettings);
        Assert.True(await main.ApplyExternalSnapshotAsync(payload));

        // The whole widget-layout domain — including the enabled dictionary —
        // keeps the main process's authoritative values.
        Assert.True(FeatureWidgetSettings.IsEnabled(main.Settings, WidgetKind.Music));
        Assert.Contains(main.Settings.Widgets, w => w.Id == "w-live");
        // The host's actual settings change landed.
        Assert.Equal("Light", main.Settings.Theme);
    }

    [Fact]
    public async Task ExternalSnapshot_PreservesMainOwnedBackupDomainsAndCapsuleState()
    {
        var main = new SettingsService(SettingsRoot);
        await main.LoadAsync();
        // Main-side writes that happen while the host session is live: the
        // backup-settings coordinator applying a router command, the cloud
        // runtime recording a successful run, and a capsule drag reorder.
        main.Settings.AutomaticBackupIntervalMinutes = 720;
        main.Settings.CloudBackup.CloudBackupProvider = "webdav";
        main.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks = 123456789L;
        main.Settings.WidgetCapsuleBarOrder = ["b-first", "a-second"];
        main.Settings.WidgetCapsuleFreePlacements["capsule-1"] = new WidgetCompactPlacement();

        // The host loaded before any of that existed, so its snapshot
        // carries the stale defaults.
        var host = new SettingsService(SettingsRoot);
        await host.LoadAsync();
        Assert.Equal(24 * 60, host.Settings.AutomaticBackupIntervalMinutes);
        Assert.Equal("none", host.Settings.CloudBackup.CloudBackupProvider);
        Assert.Equal(0L, host.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks);
        Assert.Empty(host.Settings.WidgetCapsuleBarOrder);
        host.Settings.Theme = "Light";

        string payload = JsonSerializer.Serialize(
            host.Settings,
            SettingsJsonContext.Default.AppSettings);
        Assert.True(await main.ApplyExternalSnapshotAsync(payload));

        // Every main-owned domain and collection survives the apply, while
        // the host's actual settings change still lands (deep review R13:
        // without the preserve, a host commit racing a backup run or a
        // just-commanded backup setting change silently reverted them).
        Assert.Equal(720, main.Settings.AutomaticBackupIntervalMinutes);
        Assert.Equal("webdav", main.Settings.CloudBackup.CloudBackupProvider);
        Assert.Equal(123456789L, main.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks);
        Assert.Equal(["b-first", "a-second"], main.Settings.WidgetCapsuleBarOrder);
        Assert.Contains("capsule-1", main.Settings.WidgetCapsuleFreePlacements.Keys);
        Assert.Equal("Light", main.Settings.Theme);
    }

    [Fact]
    public async Task AppearancePreview_PreservesMainOwnedBackupDomains()
    {
        var main = new SettingsService(SettingsRoot);
        await main.LoadAsync();
        main.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks = 42L;
        main.Settings.AutomaticBackupEnabled = false;

        var host = new SettingsService(SettingsRoot);
        await host.LoadAsync();
        host.Settings.Theme = "Dark";
        string payload = JsonSerializer.Serialize(
            host.Settings,
            SettingsJsonContext.Default.AppSettings);

        // The preview swap makes the stale graph live in the main process —
        // the preserve keeps main-owned state intact so the next durable
        // save from any source cannot persist a rollback (deep review R13).
        Assert.True(main.ApplyExternalAppearancePreview(payload));
        Assert.Equal(42L, main.Settings.CloudBackup.CloudBackupLastSuccessUtcTicks);
        Assert.False(main.Settings.AutomaticBackupEnabled);
        Assert.Equal("Dark", main.Settings.Theme);
    }

    [Fact]
    public async Task ExternalSnapshot_RefusesNewerSchemaAndGarbage()
    {
        var main = new SettingsService(SettingsRoot);
        await main.LoadAsync();

        string newerSchema = JsonSerializer.Serialize(
            new AppSettings { SchemaVersion = SettingsMigrationPipeline.CurrentSchemaVersion + 5 },
            SettingsJsonContext.Default.AppSettings);
        Assert.False(await main.ApplyExternalSnapshotAsync(newerSchema));

        Assert.False(await main.ApplyExternalSnapshotAsync("{ not json"));
        Assert.False(await main.ApplyExternalSnapshotAsync(string.Empty));
    }

    [Fact]
    public async Task TryReloadFromDisk_PicksUpMainCommitsAndFailsClosed()
    {
        var main = new SettingsService(SettingsRoot);
        await main.LoadAsync();
        var host = new SettingsService(SettingsRoot, new ForwardingSink());

        // SearchMaxResults is a discrete 50/100/200 setting — anything else
        // normalizes back to the default on save.
        main.Settings.SearchMaxResults = 50;
        await main.SaveAsync();

        int hostChanged = 0;
        host.SettingsChanged += () => hostChanged++;
        Assert.True(await host.TryReloadFromDiskAsync());
        Assert.Equal(50, host.Settings.SearchMaxResults);
        Assert.Equal(1, hostChanged);

        // Corrupt file: reload refuses and keeps the current state.
        await File.WriteAllTextAsync(
            Path.Combine(SettingsRoot, "settings.json"),
            "{ broken");
        int beforeChange = host.Settings.SearchMaxResults;
        Assert.False(await host.TryReloadFromDiskAsync());
        Assert.Equal(beforeChange, host.Settings.SearchMaxResults);
    }

    [Fact]
    public void ApplyValueAppliers_DiffsOnlyChangedGroups()
    {
        var before = new AppSettings();
        var after = new AppSettings
        {
            GlobalHotkeyEnabled = !before.GlobalHotkeyEnabled,
            ResizeSnapEnabled = !before.ResizeSnapEnabled,
            WidgetSnapSpacing = before.WidgetSnapSpacing + 4,
            TrayIconStyle = "Hidden",
            Theme = "Dark",
            Language = "en-US",
            SearchIncludeDeskBoxContent = !before.SearchIncludeDeskBoxContent,
        };

        int hotkey = 0, doubleClick = 0, tray = 0, theme = 0, language = 0;
        var searchToggleCalls = new List<bool>();
        var snapCalls = new List<(bool Enabled, double Spacing)>();
        ExternalSettingsAppliers.ApplyValueAppliers(
            before,
            after,
            new ExternalSettingsAppliers.Delegates(
                () => hotkey++,
                () => doubleClick++,
                (enabled, spacing) => snapCalls.Add((enabled, spacing)),
                () => tray++,
                () => theme++,
                () => language++,
                enabled => searchToggleCalls.Add(enabled)));

        Assert.Equal(1, hotkey);
        Assert.Equal(0, doubleClick);
        Assert.Equal(1, tray);
        Assert.Equal(1, theme);
        Assert.Equal(1, language);
        Assert.Equal([after.SearchIncludeDeskBoxContent], searchToggleCalls);
        var snap = Assert.Single(snapCalls);
        Assert.Equal(after.ResizeSnapEnabled, snap.Enabled);
        Assert.Equal(after.WidgetSnapSpacing, snap.Spacing);

        // Identical states: no applier runs — except snap, which is
        // replayed unconditionally (batch R12: the convergence point for
        // out-of-order fire-and-forget set-snap commands).
        int any = 0;
        int identicalSnapCalls = 0;
        ExternalSettingsAppliers.ApplyValueAppliers(
            before,
            new AppSettings(),
            new ExternalSettingsAppliers.Delegates(
                () => any++,
                () => any++,
                (_, _) => identicalSnapCalls++,
                () => any++,
                () => any++,
                () => any++,
                _ => any++));
        Assert.Equal(0, any);
        Assert.Equal(1, identicalSnapCalls);

        // The desktop double-click group trips only its own delegate
        // (snap is the unconditional-replay exception, batch R12).
        int doubleClickOnly = 0;
        int toggledSnapCalls = 0;
        var toggled = new AppSettings { DesktopDoubleClickEnabled = !before.DesktopDoubleClickEnabled };
        ExternalSettingsAppliers.ApplyValueAppliers(
            before,
            toggled,
            new ExternalSettingsAppliers.Delegates(
                () => Assert.Fail("hotkey applier must not run"),
                () => doubleClickOnly++,
                (_, _) => toggledSnapCalls++,
                () => Assert.Fail("tray applier must not run"),
                () => Assert.Fail("theme applier must not run"),
                () => Assert.Fail("language applier must not run"),
                _ => Assert.Fail("search applier must not run")));
        Assert.Equal(1, doubleClickOnly);
        Assert.Equal(1, toggledSnapCalls);
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
