using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Core.Persistence;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Distribution;

namespace DeskBox.Tests;

public sealed class UpgradeAndStoreCompatibilityTests : IDisposable
{
    private readonly string _tempRoot;

    public UpgradeAndStoreCompatibilityTests()
    {
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"DeskBoxUpgradeStoreCompatTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
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
        catch
        {
        }
    }

    [Fact]
    public async Task ProxyHandoffValidation_BlocksDnsRebindingLiteralTunAndDirectProxyAdmin()
    {
        // Direct request targeting the proxy's own host:port must never be treated as a proxied handoff.
        var corpProxy = new WebProxy("http://10.20.30.40:8080", BypassOnLocal: false);
        Assert.False(CustomWidgetNetworkSecurity.IsConfiguredSystemProxyEndpoint(
            new Uri("http://10.20.30.40:8080/admin"),
            new DnsEndPoint("10.20.30.40", 8080),
            corpProxy));

        // Literal 198.18.x.x is blocked for public-only proxied requests.
        Assert.Throws<InvalidOperationException>(() =>
            CustomWidgetNetworkSecurity.ValidateTargetUriForProxyHandoff(
                new Uri("https://198.18.0.10/api"),
                requirePublicIpOnly: true));

        // DNS resolving to cloud metadata or loopback must be rejected before proxy handoff.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CustomWidgetNetworkSecurity.ValidateTargetUriForProxyHandoffAsync(
                new Uri("https://169.254.169.254.nip.io/latest/meta-data/"),
                requirePublicIpOnly: true,
                CancellationToken.None,
                dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") })));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CustomWidgetNetworkSecurity.ValidateTargetUriForProxyHandoffAsync(
                new Uri("https://127.0.0.1.nip.io:8080/secret"),
                requirePublicIpOnly: true,
                CancellationToken.None,
                dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Loopback })));

        // Clash/mihomo TUN Fake-IP (198.18.x.x) returned by DNS for a public domain remains
        // allowed while a TUN adapter owns a fake-IP-range address (topology injected so the
        // assertion does not depend on the machine's actual proxy software).
        await CustomWidgetNetworkSecurity.ValidateTargetUriForProxyHandoffAsync(
            new Uri("https://api.github.com/repos/example/demo"),
            requirePublicIpOnly: true,
            CancellationToken.None,
            dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("198.18.0.42") }),
            tunAddressProvider: () => new[] { IPAddress.Parse("198.18.0.1") });

        // Without an active TUN adapter the same fake-IP answer is reserved-range probing.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CustomWidgetNetworkSecurity.ValidateTargetUriForProxyHandoffAsync(
                new Uri("https://api.github.com/repos/example/demo"),
                requirePublicIpOnly: true,
                CancellationToken.None,
                dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("198.18.0.42") }),
                tunAddressProvider: () => Array.Empty<IPAddress>()));
    }

    [Fact]
    public void FakeIpDnsGating_DependsOnActiveTunTopology_AndBlocksStackSpecials()
    {
        IPAddress[] noTun = Array.Empty<IPAddress>();
        IPAddress[] tunActive = { IPAddress.Parse("198.18.0.1") };

        // No TUN adapter: the whole 198.18.0.0/15 range is reserved and forbidden.
        Assert.True(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.18.0.42"), noTun));
        Assert.True(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.19.200.7"), noTun));

        // Active TUN: the fake-IP pool is legitimate, but the adapter's own address and the
        // stack's gateway/DNS specials stay forbidden.
        Assert.False(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.18.0.42"), tunActive));
        Assert.False(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.19.200.7"), tunActive));
        Assert.True(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.18.0.1"), tunActive));
        Assert.True(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.18.0.0"), tunActive));
        Assert.True(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.18.0.2"), tunActive));
        Assert.True(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.19.255.255"), tunActive));

        // Public and private addresses are unaffected by this gate (other rules handle them).
        Assert.False(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("8.8.8.8"), noTun));
        Assert.False(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("192.168.1.1"), noTun));

        // IPv4-mapped fake-IP answers normalize to the same verdict.
        Assert.True(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("::ffff:198.18.0.42"), noTun));
        Assert.False(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("::ffff:198.18.0.42"), tunActive));
    }

    [Fact]
    public void StoreUserAssetMigration_RebasesClipboardJsonAndBackupDroppedFilePaths()
    {
        string legacyData = Directory.CreateDirectory(Path.Combine(_tempRoot, "LegacyData")).FullName;
        string durableData = Directory.CreateDirectory(Path.Combine(_tempRoot, "DurableData")).FullName;
        string clipboardDir = Directory.CreateDirectory(Path.Combine(durableData, "clipboard")).FullName;
        string droppedOldPath = Path.Combine(legacyData, "clipboard", "dropped", "item1", "report.pdf");
        string droppedNewPath = Path.Combine(durableData, "clipboard", "dropped", "item1", "report.pdf");

        string payload = JsonSerializer.Serialize(new { filePaths = new[] { droppedOldPath } });
        string primaryPath = Path.Combine(clipboardDir, "clipboard.json");
        string backupPath = primaryPath + ".bak";
        File.WriteAllText(primaryPath, payload, Encoding.UTF8);
        File.WriteAllText(backupPath, payload, Encoding.UTF8);

        StoreUserAssetMigrationFileOps.RebaseMigratedJsonStorePaths(legacyData, durableData);

        JsonNode? primaryNode = JsonNode.Parse(File.ReadAllText(primaryPath, Encoding.UTF8));
        JsonNode? backupNode = JsonNode.Parse(File.ReadAllText(backupPath, Encoding.UTF8));
        Assert.Equal(droppedNewPath, primaryNode?["filePaths"]?[0]?.GetValue<string>());
        Assert.Equal(droppedNewPath, backupNode?["filePaths"]?[0]?.GetValue<string>());
    }

    [Fact]
    public async Task FutureSchemaWithUnknownEnums_LoadsReadOnlyWithoutQuarantiningLayoutOrSettings()
    {
        string profileDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "FutureDowngrade")).FullName;
        string layoutPath = Path.Combine(profileDir, "widget-layout.json");
        string futureLayoutJson = """{"schemaVersion":99,"layout":{"widgets":[{"id":"w1","widgetKind":"FutureKindV2"}]}}""";
        await File.WriteAllTextAsync(layoutPath, futureLayoutJson, Encoding.UTF8);

        var layoutStore = new WidgetLayoutStore(layoutPath);
        WidgetLayoutLoadResult layoutResult = await layoutStore.LoadAsync(new WidgetLayoutSettingsSlice());
        Assert.True(layoutResult.Authoritative);
        Assert.False(layoutStore.CanWrite);
        Assert.Equal(99, layoutStore.LoadedSchemaVersion);
        Assert.Empty(Directory.GetFiles(profileDir, "*.corrupt-*"));
        Assert.Equal(futureLayoutJson, await File.ReadAllTextAsync(layoutPath, Encoding.UTF8));

        string settingsDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "FutureSettingsProfile")).FullName;
        string settingsDataDir = Directory.CreateDirectory(Path.Combine(settingsDir, "data")).FullName;
        string settingsPath = Path.Combine(settingsDataDir, "settings.json");
        string futureSettingsJson = """{"schemaVersion":999,"searchHotkeyGesture":{"activationKind":"TripleTapFuture"}}""";
        await File.WriteAllTextAsync(settingsPath, futureSettingsJson, Encoding.UTF8);

        var settingsService = new SettingsService(settingsDir);
        await settingsService.LoadAsync();
        await settingsService.SaveAsync();

        Assert.Empty(Directory.GetFiles(settingsDataDir, "*.corrupt-*"));
        Assert.Equal(futureSettingsJson, await File.ReadAllTextAsync(settingsPath, Encoding.UTF8));
    }

    [Fact]
    public async Task TodoWidgetStore_LegacyV3AllDayMigrationPreservesDismissedAndSnoozedReminderState()
    {
        string widgetsRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "TodoMigration")).FullName;
        var store = new TodoWidgetStore(widgetsRoot, "w-todo");
        DateTimeOffset legacyAllDay = new(2026, 11, 20, 23, 59, 0, DateTimeOffset.Now.Offset);
        DateTimeOffset snoozedUntil = DateTimeOffset.UtcNow.AddHours(2);
        var legacyDoc = new TodoWidgetData
        {
            Version = 3,
            Items =
            [
                new TodoItem
                {
                    Id = "t1",
                    Text = "All-day task",
                    DueDate = legacyAllDay,
                    HasDueTime = false,
                    ReminderDismissedForDueDate = legacyAllDay,
                    SnoozedUntil = snoozedUntil
                }
            ]
        };

        await File.WriteAllTextAsync(
            store.StorePath,
            JsonSerializer.Serialize(legacyDoc, TodoJsonContext.Default.StoreData),
            Encoding.UTF8);

        TodoWidgetData loaded = await store.LoadAsync();
        TodoItem item = Assert.Single(loaded.Items);

        Assert.Equal(4, loaded.Version);
        Assert.False(item.HasDueTime);
        Assert.NotNull(item.DueDate);
        Assert.Equal(0, item.DueDate!.Value.ToLocalTime().Hour);
        Assert.Equal(0, item.DueDate.Value.ToLocalTime().Minute);
        Assert.Equal(item.DueDate, item.ReminderDismissedForDueDate);
        Assert.Equal(snoozedUntil, item.SnoozedUntil);
    }

    [Fact]
    public async Task ScopedCloudBackup_StampsSchema3ForCustomWidgetsAndExcludesOrphansWhenZeroLiveCustomWidgets()
    {
        string sourceRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "BackupSource")).FullName;
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        string customExportDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "BackupExportCustom")).FullName;
        string todoExportDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "BackupExportTodo")).FullName;

        // Live layout has zero custom widgets, but an orphan custom-widgets folder remains on disk.
        await File.WriteAllTextAsync(
            Path.Combine(sourceData, "widget-layout.json"),
            """{"schemaVersion":2,"layout":{"widgets":[]}}""",
            Encoding.UTF8);
        string orphanDir = Directory.CreateDirectory(Path.Combine(sourceData, "custom-widgets", "deleted-custom-1")).FullName;
        await File.WriteAllTextAsync(Path.Combine(orphanDir, "manifest.json"), """{"id":"deleted-custom-1"}""", Encoding.UTF8);

        var backupService = new DeskBoxDataBackupService(sourceRoot);
        string customArchive = await backupService.ExportScopedBackupAsync(
            customExportDir,
            CloudBackupDomain.CustomWidgets);

        using (ZipArchive zip = ZipFile.OpenRead(customArchive))
        {
            ZipArchiveEntry? manifestEntry = zip.GetEntry("manifest.json");
            Assert.NotNull(manifestEntry);
            using var reader = new StreamReader(manifestEntry!.Open(), Encoding.UTF8);
            JsonNode? manifest = JsonNode.Parse(await reader.ReadToEndAsync());
            Assert.Equal(3, manifest?["schemaVersion"]?.GetValue<int>());
            // Orphan custom-widgets folder must NOT be exported when layout cleanly lists 0 custom widgets.
            Assert.DoesNotContain(zip.Entries, e => e.FullName.Contains("deleted-custom-1", StringComparison.OrdinalIgnoreCase));
        }

        // Scoped backup without CustomWidgets stays schemaVersion 2 for v1.5.9 compatibility.
        string todoArchive = await backupService.ExportScopedBackupAsync(
            todoExportDir,
            CloudBackupDomain.TodoData);
        using (ZipArchive zip = ZipFile.OpenRead(todoArchive))
        {
            using var reader = new StreamReader(zip.GetEntry("manifest.json")!.Open(), Encoding.UTF8);
            JsonNode? manifest = JsonNode.Parse(await reader.ReadToEndAsync());
            Assert.Equal(2, manifest?["schemaVersion"]?.GetValue<int>());
        }
    }

    [Fact]
    public void ChannelMigration_DeferredDirectImportSurvivesUnattendedLaunchAndClearsStaleLayoutOnReimport()
    {
        // 1. Direct unattended launch writes deferred marker so next interactive launch still prompts.
        string directRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "DirectDeferred")).FullName;
        string storeRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "StoreSource")).FullName;
        string storeData = Directory.CreateDirectory(Path.Combine(storeRoot, "data")).FullName;
        File.WriteAllText(
            Path.Combine(storeData, "settings.json"),
            """{"schemaVersion":18,"marker":"from-store"}""",
            Encoding.UTF8);

        ChannelMigrationOutcome unattended = ChannelMigrationService.RunDirectStartupImportFromStore(
            directRoot,
            storeRoot,
            isInteractiveLaunch: false,
            cultureName: "zh-CN",
            confirmPrompt: (_, _) => true,
            otherChannelInstalledProbe: () => true);
        Assert.Equal(ChannelMigrationOutcomeKind.None, unattended.Kind);
        Assert.True(File.Exists(Path.Combine(directRoot, ChannelMigrationService.StoreImportDeferredMarkerFileName)));

        // Simulate SettingsService writing a blank settings.json during that unattended boot.
        string directData = Directory.CreateDirectory(Path.Combine(directRoot, "data")).FullName;
        File.WriteAllText(Path.Combine(directData, "settings.json"), """{"schemaVersion":18,"marker":"blank"}""", Encoding.UTF8);

        int prompts = 0;
        ChannelMigrationOutcome interactive = ChannelMigrationService.RunDirectStartupImportFromStore(
            directRoot,
            storeRoot,
            isInteractiveLaunch: true,
            cultureName: "zh-CN",
            confirmPrompt: (_, _) => { prompts++; return true; },
            otherChannelInstalledProbe: () => true);
        Assert.Equal(1, prompts);
        Assert.Equal(ChannelMigrationOutcomeKind.ImportedFromStoreToDirect, interactive.Kind);
        Assert.Contains("from-store", File.ReadAllText(Path.Combine(directData, "settings.json"), Encoding.UTF8));
        Assert.False(File.Exists(Path.Combine(directRoot, ChannelMigrationService.StoreImportDeferredMarkerFileName)));

        // 2. Direct->Store newer re-import clears stale Store widget-layout.json when Direct predates it.
        string oldDirectRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "DirectLegacyNoLayout")).FullName;
        string oldDirectData = Directory.CreateDirectory(Path.Combine(oldDirectRoot, "data")).FullName;
        string existingStoreRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "StoreWithLayout")).FullName;
        string existingStoreData = Directory.CreateDirectory(Path.Combine(existingStoreRoot, "data")).FullName;
        string storeSettings = Path.Combine(existingStoreData, "settings.json");
        string storeLayout = Path.Combine(existingStoreData, "widget-layout.json");
        File.WriteAllText(storeSettings, """{"schemaVersion":18}""", Encoding.UTF8);
        File.WriteAllText(storeLayout, """{"schemaVersion":2,"layout":{"widgets":[]}}""", Encoding.UTF8);
        File.SetLastWriteTimeUtc(storeSettings, DateTime.UtcNow.AddMinutes(-30));
        File.SetLastWriteTimeUtc(storeLayout, DateTime.UtcNow.AddMinutes(-30));

        string newerDirectSettings = Path.Combine(oldDirectData, "settings.json");
        File.WriteAllText(newerDirectSettings, """{"schemaVersion":18,"widgets":[{"id":"legacy"}]}""", Encoding.UTF8);
        File.SetLastWriteTimeUtc(newerDirectSettings, DateTime.UtcNow);

        ChannelMigrationOutcome reimport = ChannelMigrationService.RunStoreStartupMigration(
            oldDirectRoot,
            existingStoreRoot,
            Path.Combine(_tempRoot, "DirectRec"),
            Path.Combine(_tempRoot, "StoreRec"),
            isInteractiveLaunch: true,
            cultureName: "en-US",
            confirmPrompt: (_, _) => true,
            otherChannelInstalledProbe: () => true,
            resolveFinalPath: path => path);
        Assert.Equal(ChannelMigrationOutcomeKind.MigratedFromDirectToStoreNewer, reimport.Kind);
        Assert.False(File.Exists(storeLayout));
    }

    [Fact]
    public void DragDropPermissionService_AwaitParentExitFallbackMarkerRoundTripsOnce()
    {
        string markerRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "ParentMarker")).FullName;
        int syntheticParentPid = Environment.ProcessId + 1000;

        DragDropPermissionService.WriteAwaitParentExitFallbackMarker(
            $"--await-parent-exit {syntheticParentPid} --startup",
            markerRoot);

        int? consumedFirst = DragDropPermissionService.TryConsumeAwaitParentExitFallbackMarker(markerRoot);
        int? consumedSecond = DragDropPermissionService.TryConsumeAwaitParentExitFallbackMarker(markerRoot);

        Assert.Equal(syntheticParentPid, consumedFirst);
        Assert.Null(consumedSecond);
    }

    [Theory]
    [InlineData("Move", "Copy", false, "Copy")]
    [InlineData("Move", "Copy", true, "Copy")]
    [InlineData("Move", "FollowWindows", false, "FollowWindows")]
    [InlineData("Move", "FollowWindows", true, "Move")]
    [InlineData("Move", "Move", false, "Move")]
    [InlineData("Move", "Move", true, "Move")]
    [InlineData("Copy", "FollowWindows", false, "FollowWindows")]
    [InlineData("Copy", "FollowWindows", true, "FollowWindows")]
    [InlineData("FollowWindows", "FollowWindows", true, "FollowWindows")]
    [InlineData("Move", "", false, "Move")]
    public void V159To160Upgrade_DragOutMigrationMatrix_PreservesCopyAndWin10FollowWindows(
        string dropAction,
        string dragOutAction,
        bool isWindows11OrLater,
        string expectedDragOutAction)
    {
        var slice = new FileWidgetSettingsSlice
        {
            ManagedDropAction = dropAction,
            ManagedDragOutAction = dragOutAction
        };

        Migration_15_To_16.UpgradeLegacyDragOutAction(slice, isWindows11OrLater);

        Assert.Equal(dropAction, slice.ManagedDropAction);
        Assert.Equal(expectedDragOutAction, slice.ManagedDragOutAction);
    }

    [Fact]
    public async Task SettingsService_LoadAsync_PreservesIndependentDragOutActionAcrossReloadAndV159CopyUpgrade()
    {
        // 1. v1.5.9 (schema 15) user who explicitly set drag-out to Copy must retain Copy after upgrading to 1.6.0.
        string upgradeDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "UpgradeV159Copy")).FullName;
        File.WriteAllText(
            Path.Combine(upgradeDir, "settings.json"),
            """{"schemaVersion":15,"managedDropAction":"Move","managedDragOutAction":"Copy"}""",
            Encoding.UTF8);

        var upgraded = new SettingsService(upgradeDir);
        await upgraded.LoadAsync();
        Assert.Equal(SettingsMigrationPipeline.CurrentSchemaVersion, upgraded.Settings.SchemaVersion);
        Assert.Equal(SettingsService.ManagedDropActionMove, upgraded.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(SettingsService.ManagedDragOutActionCopy, upgraded.Settings.FileWidget.ManagedDragOutAction);

        // 2. Schema 18 profile with independent Drop=Move and DragOut=FollowWindows survives save + reload intact.
        string customDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "CustomDragOutReload")).FullName;
        File.WriteAllText(
            Path.Combine(customDir, "settings.json"),
            """{"schemaVersion":18,"managedDropAction":"Move","managedDragOutAction":"FollowWindows"}""",
            Encoding.UTF8);

        var loaded = new SettingsService(customDir);
        await loaded.LoadAsync();
        Assert.Equal(SettingsService.ManagedDropActionMove, loaded.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(SettingsService.ManagedDragOutActionFollowWindows, loaded.Settings.FileWidget.ManagedDragOutAction);

        await loaded.SaveAsync();
        var reloaded = new SettingsService(customDir);
        await reloaded.LoadAsync();
        Assert.Equal(SettingsService.ManagedDropActionMove, reloaded.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(SettingsService.ManagedDragOutActionFollowWindows, reloaded.Settings.FileWidget.ManagedDragOutAction);
    }

    [Fact]
    public async Task DesktopToastHud_SwitchActions_UpdateDropAndDragOutIndependently()
    {
        string hudDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "HudActionIndependence")).FullName;
        var settings = new SettingsService(hudDir);
        await settings.LoadAsync();

        // Fresh install defaults to Move/Move (Plan A2).
        Assert.Equal(SettingsService.ManagedDropActionMove, settings.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(SettingsService.ManagedDragOutActionMove, settings.Settings.FileWidget.ManagedDragOutAction);

        // Clicking "Use Windows default" on Win10 drag-out rejection HUD updates ONLY ManagedDragOutAction.
        await DesktopToastHudService.SwitchDragOutActionAsync(
            settings,
            SettingsService.ManagedDragOutActionFollowWindows);
        Assert.Equal(SettingsService.ManagedDropActionMove, settings.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(SettingsService.ManagedDragOutActionFollowWindows, settings.Settings.FileWidget.ManagedDragOutAction);

        // Clicking inbound cross-volume "Default to Move" HUD updates ONLY ManagedDropAction without overwriting Copy drag-out.
        settings.Settings.FileWidget.ManagedDropAction = SettingsService.ManagedDropActionFollowWindows;
        settings.Settings.FileWidget.ManagedDragOutAction = SettingsService.ManagedDragOutActionCopy;
        await DesktopToastHudService.SwitchDropActionAsync(
            settings,
            SettingsService.ManagedDropActionMove);
        Assert.Equal(SettingsService.ManagedDropActionMove, settings.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(SettingsService.ManagedDragOutActionCopy, settings.Settings.FileWidget.ManagedDragOutAction);
    }
}
