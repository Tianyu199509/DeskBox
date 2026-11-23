using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
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

        // Fake-IP (198.18.x.x) returned by DNS for a public domain is allowed for both local TUN
        // and transparent router gateways (OpenWrt/Surge where PC has no local TUN NIC), while
        // stack specials and any 198.18.x.x IP bound to a local NIC on this PC stay blocked.
        await CustomWidgetNetworkSecurity.ValidateTargetUriForProxyHandoffAsync(
            new Uri("https://api.github.com/repos/example/demo"),
            requirePublicIpOnly: true,
            CancellationToken.None,
            dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("198.18.0.42") }),
            tunAddressProvider: () => new[] { IPAddress.Parse("198.18.0.1") });

        await CustomWidgetNetworkSecurity.ValidateTargetUriForProxyHandoffAsync(
            new Uri("https://api.github.com/repos/example/demo"),
            requirePublicIpOnly: true,
            CancellationToken.None,
            dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("198.18.0.42") }),
            tunAddressProvider: () => Array.Empty<IPAddress>());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CustomWidgetNetworkSecurity.ValidateTargetUriForProxyHandoffAsync(
                new Uri("https://api.github.com/repos/example/demo"),
                requirePublicIpOnly: true,
                CancellationToken.None,
                dnsResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse("198.18.0.42") }),
                tunAddressProvider: () => new[] { IPAddress.Parse("198.18.0.42") }));
    }

    [Fact]
    public void FakeIpDnsGating_AllowsRouterAndLocalFakeIpPool_WhileBlockingLiteralsSpecialsAndOwnNicIps()
    {
        IPAddress[] noLocalNicIp = Array.Empty<IPAddress>();
        IPAddress[] localTunNic = { IPAddress.Parse("198.18.0.1"), IPAddress.Parse("198.18.0.99") };

        // URL literals in 198.18.0.0/15 are always treated as local/non-public.
        Assert.False(CustomWidgetNetworkSecurity.TryValidatePublicHttpsUrl("https://198.18.0.42/api", out _, out _));
        Assert.False(CustomWidgetNetworkSecurity.TryValidateWidgetNetworkUrl(
            "https://198.18.0.42/api", allowLocalAndHttp: false, out _, out bool reqLocal, out _));
        Assert.True(reqLocal);

        // Transparent router Fake-IP gateway (no local TUN NIC) and local TUN both allow normal pool answers.
        Assert.False(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.18.0.42"), noLocalNicIp));
        Assert.False(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.19.200.7"), noLocalNicIp));
        Assert.False(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.18.0.42"), localTunNic));
        Assert.False(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("::ffff:198.18.0.42"), noLocalNicIp));

        // Stack specials and any IP owned by a local NIC remain forbidden.
        Assert.True(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.18.0.0"), noLocalNicIp));
        Assert.True(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.18.0.1"), noLocalNicIp));
        Assert.True(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.18.0.2"), noLocalNicIp));
        Assert.True(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.19.255.255"), noLocalNicIp));
        Assert.True(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("198.18.0.99"), localTunNic));
        Assert.True(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("::ffff:198.18.0.1"), noLocalNicIp));

        // Public and RFC1918 addresses are unaffected by Fake-IP check (IsBlockedIpAddress handles RFC1918).
        Assert.False(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("8.8.8.8"), noLocalNicIp));
        Assert.False(CustomWidgetNetworkSecurity.IsForbiddenFakeIpDnsResult(IPAddress.Parse("192.168.1.1"), noLocalNicIp));
    }

    [Fact]
    public void StoreUserAssetMigration_RebasesRealPre160StorePaths_QuickCaptureAndTodo()
    {
        string legacyData = Directory.CreateDirectory(Path.Combine(_tempRoot, "LegacyData")).FullName;
        string durableData = Directory.CreateDirectory(Path.Combine(_tempRoot, "DurableData")).FullName;
        string qcDir = Directory.CreateDirectory(Path.Combine(durableData, "quick-capture")).FullName;
        string oldAttachPath = Path.Combine(legacyData, "quick-capture", "attachments", "note1.png");
        string newAttachPath = Path.Combine(durableData, "quick-capture", "attachments", "note1.png");

        string payload = JsonSerializer.Serialize(new { imagePath = oldAttachPath });
        string primaryPath = Path.Combine(qcDir, "quick-capture.json");
        string backupPath = primaryPath + ".bak";
        File.WriteAllText(primaryPath, payload, Encoding.UTF8);
        File.WriteAllText(backupPath, payload, Encoding.UTF8);

        StoreUserAssetMigrationFileOps.RebaseMigratedJsonStorePaths(legacyData, durableData);

        JsonNode? primaryNode = JsonNode.Parse(File.ReadAllText(primaryPath, Encoding.UTF8));
        JsonNode? backupNode = JsonNode.Parse(File.ReadAllText(backupPath, Encoding.UTF8));
        Assert.Equal(newAttachPath, primaryNode?["imagePath"]?.GetValue<string>());
        Assert.Equal(newAttachPath, backupNode?["imagePath"]?.GetValue<string>());
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
        string futureSettingsJson = """{"schemaVersion":999,"searchHotkeyActivationKind":"TripleTapFuture"}""";
        await File.WriteAllTextAsync(settingsPath, futureSettingsJson, Encoding.UTF8);

        var settingsService = new SettingsService(settingsDataDir);
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
    public async Task DesktopToastHud_SwitchActions_KeepDropAndDragOutUnifiedWithSingleSettingsDropdown()
    {
        string hudDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "HudActionUnified")).FullName;
        var settings = new SettingsService(hudDir);
        await settings.LoadAsync();

        // Fresh install defaults to Move/Move.
        Assert.Equal(SettingsService.ManagedDropActionMove, settings.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(SettingsService.ManagedDragOutActionMove, settings.Settings.FileWidget.ManagedDragOutAction);

        // Clicking "Use Windows default" on Win10 drag-out rejection HUD updates both fields so the single Settings dropdown reflects it.
        await DesktopToastHudService.SwitchDragOutActionAsync(
            settings,
            SettingsService.ManagedDragOutActionFollowWindows);
        Assert.Equal(SettingsService.ManagedDropActionFollowWindows, settings.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(SettingsService.ManagedDragOutActionFollowWindows, settings.Settings.FileWidget.ManagedDragOutAction);

        // Clicking inbound cross-volume "Default to Move" HUD updates both fields in lockstep.
        settings.Settings.FileWidget.ManagedDropAction = SettingsService.ManagedDropActionFollowWindows;
        settings.Settings.FileWidget.ManagedDragOutAction = SettingsService.ManagedDragOutActionCopy;
        await DesktopToastHudService.SwitchDropActionAsync(
            settings,
            SettingsService.ManagedDropActionMove);
        Assert.Equal(SettingsService.ManagedDropActionMove, settings.Settings.FileWidget.ManagedDropAction);
        Assert.Equal(SettingsService.ManagedDragOutActionMove, settings.Settings.FileWidget.ManagedDragOutAction);
    }

    [Fact]
    public async Task ManualBackupWithClipboard_MaterializesPortablePlaintextSnapshot_AndRestoresAcrossKeyRotation()
    {
        byte[] onePixelPng = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        string sourceRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "PortableBackupSrc")).FullName;
        string sourceData = Directory.CreateDirectory(Path.Combine(sourceRoot, "data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sourceData, "settings.json"), """{"schemaVersion":18}""", Encoding.UTF8);

        var clipStore = new ClipboardStore(Path.Combine(sourceData, "clipboard"));
        byte[] keyA = clipStore.GetOrCreateContentHashKey();
        string imgHashA = DeskBox.Features.Clipboard.ClipboardContentHashing.ComputeImageContentHash(onePixelPng, keyA);
        string imgLeafA = DeskBox.Features.Clipboard.ClipboardContentHashing.MediaFileNameFromContentHash(imgHashA);
        Directory.CreateDirectory(clipStore.ImageDirectory);
        await File.WriteAllBytesAsync(Path.Combine(clipStore.ImageDirectory, imgLeafA), onePixelPng);

        string secretToken = "ghp_1234567890abcdefghijklmnopqrstuvwxyzABCD";
        string longSecret = secretToken + new string('X', 2300);
        await clipStore.SaveAsync(new ClipboardStoreDocument
        {
            Items =
            {
                new ClipboardHistoryItem
                {
                    Id = "sec-text",
                    Kind = ClipboardItemKind.Text,
                    ContentHash = DeskBox.Features.Clipboard.ClipboardContentHashing.ComputeNormalizedTextContentHash(longSecret, keyA),
                    TextContent = longSecret,
                    Summary = ClipboardClassifier.BuildSummary(longSecret),
                    IsSensitive = true
                },
                new ClipboardHistoryItem
                {
                    Id = "sec-img",
                    Kind = ClipboardItemKind.Image,
                    ContentHash = imgHashA,
                    ImageFileName = imgLeafA,
                    ThumbnailFileName = imgLeafA,
                    ImageWidth = 1,
                    ImageHeight = 1,
                    Summary = "1 × 1",
                    OcrText = secretToken,
                    IsSensitive = true
                }
            }
        });

        await File.WriteAllTextAsync(Path.Combine(sourceData, "widget-secrets.json"), """{"w1":{"apiKey":"cipher"}}""", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(sourceData, "ai-credentials.json"), """{"customEndpoint":"https://priv.local"}""", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(sourceData, "widget-permissions.json"), """{"version":1,"widgets":{"w1":{"trustedLocalAuthoring":true}}}""", Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(sourceData, "widget-permissions.key"), "machine-bound", Encoding.UTF8);

        // Snapshot simulating manual export with includeClipboard = true
        string snapshotDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "PortableSnapshot")).FullName;
        foreach (string srcFile in Directory.EnumerateFiles(sourceData, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceData, srcFile);
            string dst = Path.Combine(snapshotDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(srcFile, dst, overwrite: true);
        }

        await DeskBox.Services.Backup.PortableBackupProcessor.MaterializePortableClipboardSnapshotAsync(snapshotDir);
        Assert.False(File.Exists(Path.Combine(snapshotDir, "clipboard", "hmac-key.bin")));

        // Simulate restoring onto a brand-new machine (new directory with no hmac-key.bin -> fresh HMAC key)
        string restoredClipRoot = Path.Combine(_tempRoot, "RestoredOnMachineB", "clipboard");
        foreach (string srcFile in Directory.EnumerateFiles(Path.Combine(snapshotDir, "clipboard"), "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(Path.Combine(snapshotDir, "clipboard"), srcFile);
            string dst = Path.Combine(restoredClipRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(srcFile, dst, overwrite: true);
        }

        var machineBStore = new ClipboardStore(restoredClipRoot);
        ClipboardStoreDocument restoredDoc = await machineBStore.LoadAsync();
        Assert.Equal(2, restoredDoc.Items.Count);

        ClipboardHistoryItem restoredText = Assert.Single(restoredDoc.Items, x => x.Id == "sec-text");
        string? fullRestored = await machineBStore.ReadPayloadTextAsync(restoredText.TextPayloadFileName);
        Assert.Equal(longSecret, fullRestored);
        Assert.StartsWith("txt:h1:", restoredText.ContentHash, StringComparison.Ordinal);

        ClipboardHistoryItem restoredImg = Assert.Single(restoredDoc.Items, x => x.Id == "sec-img");
        Assert.Equal("1 × 1", restoredImg.Summary);
        Assert.Equal(secretToken, restoredImg.OcrText);
        Assert.Null(restoredImg.ThumbnailFileName);
        string restoredImgPath = Path.Combine(machineBStore.ImageDirectory, restoredImg.ImageFileName!);
        Assert.True(DeskBox.Platform.ClipboardStorageFileOps.IsDpapiProtectedMediaFile(restoredImgPath));
        Assert.True(DeskBox.Platform.ClipboardStorageFileOps.TryReadMediaBytes(restoredImgPath, out byte[] plainRestoredPng));
        Assert.Equal(onePixelPng, plainRestoredPng);

        // Verify clipboard.json.bak on Machine B does NOT retain cleartext secrets after migration resave.
        string bakPath = ResilientJsonStore.GetBackupPath(Path.Combine(restoredClipRoot, "clipboard.json"));
        if (File.Exists(bakPath))
        {
            string bakText = await File.ReadAllTextAsync(bakPath, Encoding.UTF8);
            Assert.DoesNotContain(secretToken, bakText, StringComparison.Ordinal);
        }

        // Verify full ExportBackupAsync(includeClipboard: true) omits non-portable DPAPI vaults while exporting decrypted portable clipboard.
        string exportRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "BackupSvcRoot")).FullName;
        string exportData = Path.Combine(exportRoot, "data");
        foreach (string srcFile in Directory.EnumerateFiles(sourceData, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceData, srcFile);
            string dst = Path.Combine(exportData, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(srcFile, dst, overwrite: true);
        }
        await File.WriteAllTextAsync(Path.Combine(exportData, "settings.json"), """{"schemaVersion":16}""", Encoding.UTF8);
        var backupSvc = new DeskBoxDataBackupService(exportRoot);
        string outDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "ExportOut")).FullName;
        string exportedArchivePath = await backupSvc.ExportBackupAsync(outDir, includeClipboard: true);
        using (System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(exportedArchivePath))
        {
            Assert.Contains(zip.Entries, e => e.FullName == "data/clipboard/clipboard.json");
            Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith("ai-credentials.json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith("widget-secrets.json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith("widget-permissions.json", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith("widget-permissions.key", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(zip.Entries, e => e.FullName.EndsWith("hmac-key.bin", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ForeignRestoreStripsPermissions_AndChannelScopedWritesPreserveIntendedTrust()
    {
        // 1. Foreign / external backup archive restore strips machine-bound permission & secret vaults,
        // even if manifestKind claims "automatic", while local recovery root snapshots keep them.
        string stagedForeign = Directory.CreateDirectory(Path.Combine(_tempRoot, "StagedForeign")).FullName;
        string recoveryRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "RecoveryRoot")).FullName;
        File.WriteAllText(Path.Combine(stagedForeign, "widget-permissions.json"), """{"version":1,"widgets":{"evil":{"trustedLocalAuthoring":true,"overrides":{"commandExecution":true}}}}""");
        File.WriteAllText(Path.Combine(stagedForeign, "ai-credentials.json"), """{"providers":{}}""");
        DeskBox.Services.Backup.PortableBackupProcessor.StripMachineBoundSecretsIfUntrustedRestore(
            stagedForeign,
            Path.Combine(_tempRoot, "Downloads", "forged-auto.zip"),
            recoveryRoot,
            manifestKind: "automatic");
        Assert.False(File.Exists(Path.Combine(stagedForeign, "widget-permissions.json")));
        Assert.False(File.Exists(Path.Combine(stagedForeign, "ai-credentials.json")));

        string stagedLocalAuto = Directory.CreateDirectory(Path.Combine(_tempRoot, "StagedLocalAuto")).FullName;
        File.WriteAllText(Path.Combine(stagedLocalAuto, "widget-permissions.json"), """{"version":1,"widgets":{}}""");
        DeskBox.Services.Backup.PortableBackupProcessor.StripMachineBoundSecretsIfUntrustedRestore(
            stagedLocalAuto,
            Path.Combine(recoveryRoot, "auto-snapshot.zip"),
            recoveryRoot,
            manifestKind: "automatic");
        Assert.True(File.Exists(Path.Combine(stagedLocalAuto, "widget-permissions.json")));

        // 2. Signed Official OTA keeps official baseline trust while revoking explicit CommandExecution override;
        // Studio save on an Agent-origin widget keeps user-granted overrides without elevating ungranted permissions.
        string trustRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "ChannelTrust")).FullName;
        var trustStore = new CustomWidgetTrustStore(trustRoot);
        const string m1 = """{"id":"deskbox.pomodoro","schemaVersion":1,"root":"r","elements":{"r":{"type":"Text","text":"v1"}}}""";
        const string m2 = """{"id":"deskbox.pomodoro","schemaVersion":1,"root":"r","elements":{"r":{"type":"Text","text":"v2"}}}""";
        trustStore.SetPermissionOverride("w-official", CustomWidgetPermissionKind.CommandExecution, enabled: true, defaultTrustedIfNew: true);
        trustStore.RecordExecutableSurfaceAfterWrite("w-official", m1, "export function onInit(){}", trustedLocalAuthoring: true);
        trustStore.RecordExecutableSurfaceAfterWrite("w-official", m2, "export function onInit(){ return 2; }", trustedLocalAuthoring: false, preserveTrustedAuthoringOnExternalWrite: true);
        CustomWidgetTrustProfile afterOta = trustStore.GetProfile("w-official", manifestOriginFallback: "official", manifestTemplateId: "deskbox.pomodoro");
        Assert.True(afterOta.TrustedLocalAuthoring);
        Assert.False(afterOta.IsPermissionGranted(CustomWidgetPermissionKind.CommandExecution));

        trustStore.RegisterWidgetProvenance("w-agent", trustedLocalAuthoring: false);
        trustStore.RecordExecutableSurfaceAfterWrite("w-agent", m1, "export function a(){}", trustedLocalAuthoring: false);
        trustStore.SetPermissionOverride("w-agent", CustomWidgetPermissionKind.LocalFileAccess, enabled: true);
        trustStore.RecordExecutableSurfaceAfterWrite("w-agent", m2, "export function b(){}", trustedLocalAuthoring: true, preserveExistingTrustedStateOnHostWrite: true);
        CustomWidgetTrustProfile afterStudioEdit = trustStore.GetProfile("w-agent", manifestOriginFallback: "agent");
        Assert.False(afterStudioEdit.TrustedLocalAuthoring);
        Assert.True(afterStudioEdit.IsPermissionGranted(CustomWidgetPermissionKind.LocalFileAccess));
        Assert.False(afterStudioEdit.IsPermissionGranted(CustomWidgetPermissionKind.LocalNetwork));

        // 3. Public URL containing "10." in version path does NOT trigger LocalNetwork permission.
        CustomWidgetManifest pubVerManifest = CustomWidgetManifestParser.Parse(
            """{"id":"custom.ver","schemaVersion":1,"root":"b","elements":{"b":{"type":"Button","text":"Go","onClick":{"type":"http.request","url":"https://api.github.com/releases/v1.10.0"}}}}""").Manifest!;
        IReadOnlySet<CustomWidgetPermissionKind> perms = CustomWidgetTrustStore.DetectRequestedPermissions(pubVerManifest);
        Assert.Contains(CustomWidgetPermissionKind.PublicNetwork, perms);
        Assert.DoesNotContain(CustomWidgetPermissionKind.LocalNetwork, perms);
    }

    [Fact]
    public async Task RebaseClipboardPaths_UpdatesDroppedFilePathsAcrossProfiles_AndUnknownWidgetKindNeverBecomesFile()
    {
        string stagedData = Directory.CreateDirectory(Path.Combine(_tempRoot, "StagedRestoreData")).FullName;
        string targetData = Directory.CreateDirectory(Path.Combine(_tempRoot, "TargetLiveData")).FullName;
        string droppedFile = Path.Combine(stagedData, "clipboard", "dropped", "b1", "report.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(droppedFile)!);
        await File.WriteAllBytesAsync(droppedFile, [1, 2, 3]);

        string clipJsonPath = Path.Combine(stagedData, "clipboard", "clipboard.json");
        await File.WriteAllTextAsync(
            clipJsonPath,
            """{"version":1,"items":[{"id":"f1","kind":"Files","filePaths":["C:\\Users\\OldUser\\AppData\\Local\\DeskBox\\data\\clipboard\\dropped\\b1\\report.pdf"],"textContent":"C:\\Users\\OldUser\\AppData\\Local\\DeskBox\\data\\clipboard\\dropped\\b1\\report.pdf"}]}""",
            Encoding.UTF8);

        await DeskBox.Services.Backup.PortableBackupProcessor.RebaseClipboardPathsAsync(
            stagedData,
            @"C:\Users\OldUser\AppData\Local\DeskBox\data",
            targetData);

        string expectedPath = Path.Combine(targetData, "clipboard", "dropped", "b1", "report.pdf");
        JsonNode? rebasedNode = JsonNode.Parse(await File.ReadAllTextAsync(clipJsonPath, Encoding.UTF8));
        Assert.Equal(expectedPath, rebasedNode?["items"]?[0]?["filePaths"]?[0]?.GetValue<string>());
        Assert.Equal(expectedPath, rebasedNode?["items"]?[0]?["textContent"]?.GetValue<string>());

        // Unknown future WidgetKind round-trips its original kind name through SettingsService without becoming WidgetKind.File.
        string futureSettingsDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "FutureKindRoundTrip")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(futureSettingsDir, "settings.json"),
            """{"schemaVersion":16}""",
            Encoding.UTF8);
        await File.WriteAllTextAsync(
            Path.Combine(futureSettingsDir, "widget-layout.json"),
            """{"schemaVersion":2,"layout":{"widgets":[{"id":"future-1","name":"Future","widgetKind":"HologramV9","isDisabled":true}]}}""",
            Encoding.UTF8);
        var futureSettings = new SettingsService(futureSettingsDir);
        await futureSettings.LoadAsync();
        WidgetConfig loadedFuture = Assert.Single(futureSettings.Settings.Widgets, w => w.Id == "future-1");
        Assert.True(WidgetKindJsonConverter.IsUnknownFutureKind(loadedFuture.WidgetKind));
        Assert.True(loadedFuture.IsDisabled);
        Assert.False(WidgetRegistry.Default.IsAvailableForSession(loadedFuture, futureSettings.Settings));
        await futureSettings.SaveAsync();
        string savedLayoutJson = await File.ReadAllTextAsync(Path.Combine(futureSettingsDir, "widget-layout.json"), Encoding.UTF8);
        Assert.Contains("\"HologramV9\"", savedLayoutJson, StringComparison.Ordinal);

        // 1. Unparseable breaking future-schema stores back up original JSON and block destructive overwrite on SaveAsync.
        string unparseableQcDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "UnparseableFutureQc")).FullName;
        string unparseableQcJson = """{"version":99,"items":"breaking-non-array-shape"}""";
        await File.WriteAllTextAsync(Path.Combine(unparseableQcDir, "quick-capture.json"), unparseableQcJson, Encoding.UTF8);
        var unparseableQcStore = new QuickCaptureStore(unparseableQcDir);
        QuickCaptureStoreData unparseableLoaded = await unparseableQcStore.LoadAsync();
        Assert.Equal(99, unparseableLoaded.Version);
        await unparseableQcStore.SaveAsync(new QuickCaptureStoreData { Items = [] });
        Assert.Contains("breaking-non-array-shape", await File.ReadAllTextAsync(Path.Combine(unparseableQcDir, "quick-capture.json"), Encoding.UTF8), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(unparseableQcDir, "quick-capture.json.future-v99.bak")));

        // 2. Compatible additive future-schema stores archive .future-v99.bak once AND allow normal user saves (no silent data loss).
        string additiveQcDir = Directory.CreateDirectory(Path.Combine(_tempRoot, "AdditiveFutureQc")).FullName;
        string additiveQcJson = """{"version":99,"futureBanner":"v99","items":[{"id":"n1","body":"future note"}]}""";
        await File.WriteAllTextAsync(Path.Combine(additiveQcDir, "quick-capture.json"), additiveQcJson, Encoding.UTF8);
        var additiveQcStore = new QuickCaptureStore(additiveQcDir);
        QuickCaptureStoreData additiveLoaded = await additiveQcStore.LoadAsync();
        Assert.Single(additiveLoaded.Items);
        Assert.True(File.Exists(Path.Combine(additiveQcDir, "quick-capture.json.future-v99.bak")));
        additiveLoaded.Items.Add(new QuickCaptureItem { Id = "n2", Body = "newly added note" });
        await additiveQcStore.SaveAsync(additiveLoaded);
        QuickCaptureStoreData reloadedAfterSave = await new QuickCaptureStore(additiveQcDir).LoadAsync();
        Assert.Equal(2, reloadedAfterSave.Items.Count);

        // Store background wakeup protocol URI parses cleanly and activates startup mode.
        Assert.True(DeskBoxProtocolActivationService.TryParseProtocolUri("deskbox://startup", out DeskBoxProtocolIntent? wakeIntent, out _));
        Assert.Equal(DeskBoxProtocolAction.BackgroundStartup, wakeIntent!.Action);
        Assert.True(StartupLaunchPolicy.IsStartupLaunch(["deskbox://startup"]));

        // Legacy v15 image-clipboard opt-out is preserved on upgrade to v16+.
        string v15Dir = Directory.CreateDirectory(Path.Combine(_tempRoot, "LegacyV15ClipPrivacy")).FullName;
        await File.WriteAllTextAsync(
            Path.Combine(v15Dir, "settings.json"),
            """{"schemaVersion":15,"quickCaptureClipboardEnabled":true,"quickCaptureImageClipboardEnabled":false}""",
            Encoding.UTF8);
        var migratedSettings = new SettingsService(v15Dir);
        await migratedSettings.LoadAsync();
        Assert.False(migratedSettings.Settings.Clipboard.ClipboardCaptureImages);

        // Win10 glyph normalization covers Win11-only transport bold icons.
        Assert.Equal("\uE892", CustomWidgetSemanticIcons.NormalizeFluentGlyphForOs("\uF8AC", isWindows11OrLater: false));
        Assert.Equal("\uE893", CustomWidgetSemanticIcons.NormalizeFluentGlyphForOs("\uF8AD", isWindows11OrLater: false));
        Assert.Equal("\uE769", CustomWidgetSemanticIcons.NormalizeFluentGlyphForOs("\uF8AE", isWindows11OrLater: false));
    }

    [Fact]
    public async Task StoreUserAssetMigration_RebasesClipboardDroppedFilesAndCustomWidgetStores_PreservingDroppedAttachments()
    {
        string legacyData = Directory.CreateDirectory(Path.Combine(_tempRoot, "LegacyClipData")).FullName;
        string durableData = Directory.CreateDirectory(Path.Combine(_tempRoot, "DurableClipData")).FullName;

        string oldDroppedFile = Path.Combine(legacyData, "clipboard", "dropped", "entry-1", "invoice.pdf");
        string newDroppedFile = Path.Combine(durableData, "clipboard", "dropped", "entry-1", "invoice.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(newDroppedFile)!);
        await File.WriteAllTextAsync(newDroppedFile, "pdf-bytes", Encoding.UTF8);

        var durableClipStore = new ClipboardStore(Path.Combine(durableData, "clipboard"));
        byte[] hmacKey = RandomNumberGenerator.GetBytes(32);
        await File.WriteAllTextAsync(
            Path.Combine(durableClipStore.RootDirectory, "hmac-key.bin"),
            DpapiSecretProtector.ProtectUtf8ToBase64(Convert.ToBase64String(hmacKey)),
            Encoding.UTF8);

        string oldHash = DeskBox.Features.Clipboard.ClipboardContentHashing.ComputeFilesContentHash([oldDroppedFile], hmacKey);
        var clipDoc = new ClipboardStoreDocument
        {
            Items =
            [
                new ClipboardHistoryItem
                {
                    Id = "c1",
                    Kind = ClipboardItemKind.Files,
                    TextContent = oldDroppedFile,
                    FilePaths = [oldDroppedFile],
                    ContentHash = oldHash
                }
            ]
        };
        string clipJson = JsonSerializer.Serialize(clipDoc, ClipboardJsonContext.Default.ClipboardStoreDocument);
        await File.WriteAllTextAsync(durableClipStore.StorePath, clipJson, Encoding.UTF8);
        await File.WriteAllTextAsync(durableClipStore.StorePath + ".bak", clipJson, Encoding.UTF8);

        string cwDir = Directory.CreateDirectory(Path.Combine(durableData, "custom-widgets", "cw-1")).FullName;
        string oldCwAttachment = Path.Combine(legacyData, "custom-widgets", "cw-1", "attachments", "photo.png");
        string newCwAttachment = Path.Combine(durableData, "custom-widgets", "cw-1", "attachments", "photo.png");
        await File.WriteAllTextAsync(
            Path.Combine(cwDir, "store.json"),
            JsonSerializer.Serialize(new { cover = oldCwAttachment }),
            Encoding.UTF8);

        StoreUserAssetMigrationFileOps.RebaseMigratedJsonStorePaths(legacyData, durableData);

        ClipboardStoreDocument migratedDoc = await durableClipStore.LoadAsync();
        ClipboardHistoryItem migratedItem = Assert.Single(migratedDoc.Items);
        Assert.Equal(newDroppedFile, Assert.Single(migratedItem.FilePaths));
        Assert.Equal(newDroppedFile, migratedItem.TextContent);
        Assert.Equal(
            DeskBox.Features.Clipboard.ClipboardContentHashing.ComputeFilesContentHash([newDroppedFile], hmacKey),
            migratedItem.ContentHash);

        ClipboardStore.CleanupOrphanedDroppedStorage(durableClipStore, migratedDoc.Items);
        Assert.True(File.Exists(newDroppedFile), "Rebased dropped attachment must survive orphan cleanup.");

        JsonNode? cwNode = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(cwDir, "store.json"), Encoding.UTF8));
        Assert.Equal(newCwAttachment, cwNode?["cover"]?.GetValue<string>());
    }

    [Fact]
    public async Task CustomWidgetDataStore_CompanionSaveRelay_InvalidatesCacheWhenMainProcessUpdatesDisk()
    {
        string sharedDataRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "CompanionRelayData")).FullName;
        var mainStore = new CustomWidgetDataStore("w-live", sharedDataRoot);
        var initialPayload = new CustomWidgetStorePayload();
        initialPayload.Settings["city"] = "Beijing";
        await mainStore.SaveAsync(initialPayload);

        var studioStore = new CustomWidgetDataStore("w-live", sharedDataRoot)
        {
            SaveRelayOverride = async (widgetId, jsonPayload, clearAttachments) =>
            {
                string wire = CustomWidgetDataStore.ComposeRelayPayload(widgetId, jsonPayload, clearAttachments);
                using JsonDocument doc = JsonDocument.Parse(wire[(wire.IndexOf('\n') + 1)..]);
                CustomWidgetStorePayload parsed = JsonSerializer.Deserialize(
                    doc.RootElement.GetRawText(),
                    CustomWidgetStoreJsonContext.Default.CustomWidgetStorePayload)!;
                if (clearAttachments)
                {
                    await mainStore.ClearAllDataAsync();
                }

                await mainStore.SaveAsync(parsed);
            }
        };

        CustomWidgetStorePayload firstStudioLoad = await studioStore.LoadAsync();
        Assert.Equal("Beijing", firstStudioLoad.Settings["city"]);

        // Simulate main process / MCP updating store.json while Studio remains open.
        var externalPayload = await mainStore.LoadAsync();
        externalPayload.Settings["city"] = "Shanghai";
        externalPayload.Collections["tasks"] =
        [
            CustomWidgetRecord.CreateProjected("r1", ("title", "Added from desktop"))
        ];
        await mainStore.SaveAsync(externalPayload);

        CustomWidgetStorePayload secondStudioLoad = await studioStore.LoadAsync();
        Assert.Equal("Shanghai", secondStudioLoad.Settings["city"]);
        Assert.True(secondStudioLoad.Collections.ContainsKey("tasks"));
        Assert.Single(secondStudioLoad.Collections["tasks"]);
    }

    [Fact]
    public void ChannelMigration_StoreToDirectImport_ResolvesUtf8BomSourceLanguageWhenExplicitCultureOmitted()
    {
        string directRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "DirectEmpty")).FullName;
        string storeRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "StoreConfigured")).FullName;
        string storeData = Directory.CreateDirectory(Path.Combine(storeRoot, "data")).FullName;

        // Encoding.UTF8 writes a 3-byte UTF-8 BOM; pre-UI culture resolution must strip it cleanly.
        File.WriteAllText(
            Path.Combine(storeData, "settings.json"),
            """{"schemaVersion":18,"language":"ja-JP"}""",
            Encoding.UTF8);

        string? capturedPromptTitle = null;
        ChannelMigrationOutcome outcome = ChannelMigrationService.RunDirectStartupImportFromStore(
            directRoot,
            storeRoot,
            isInteractiveLaunch: true,
            cultureName: null,
            confirmPrompt: (title, _) =>
            {
                capturedPromptTitle = title;
                return true;
            },
            otherChannelInstalledProbe: () => true);

        Assert.Equal(ChannelMigrationOutcomeKind.ImportedFromStoreToDirect, outcome.Kind);
        Assert.Equal(
            LocalizationService.TranslateForCulture("ja-JP", "ChannelCoexistence.StoreImportPrompt.Title"),
            capturedPromptTitle);
    }

    [Fact]
    public void StripMachineBoundSecrets_PreservesSecretsForPreRestoreAndCustomAutoBackupRoots()
    {
        string recoveryRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "DefaultRecovery")).FullName;
        string preRestoreRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "DataRoot", "backups", "pre-restore")).FullName;
        string customAutoRoot = Directory.CreateDirectory(Path.Combine(_tempRoot, "CustomAutoDir")).FullName;

        string stagedPreRestore = Directory.CreateDirectory(Path.Combine(_tempRoot, "StagedPreRestore")).FullName;
        Directory.CreateDirectory(Path.Combine(stagedPreRestore, "clipboard"));
        File.WriteAllText(Path.Combine(stagedPreRestore, "ai-credentials.json"), """{"providers":{"openai":{"apiKey":"enc"}}}""");
        File.WriteAllBytes(Path.Combine(stagedPreRestore, "clipboard", "hmac-key.bin"), [1, 2, 3, 4]);

        DeskBox.Services.Backup.PortableBackupProcessor.StripMachineBoundSecretsIfUntrustedRestore(
            stagedPreRestore,
            Path.Combine(preRestoreRoot, "pre-restore-20261122.zip"),
            recoveryRoot,
            manifestKind: "pre-restore",
            preRestoreRoot,
            customAutoRoot);
        Assert.True(File.Exists(Path.Combine(stagedPreRestore, "ai-credentials.json")));
        Assert.True(File.Exists(Path.Combine(stagedPreRestore, "clipboard", "hmac-key.bin")));

        string stagedCustomAuto = Directory.CreateDirectory(Path.Combine(_tempRoot, "StagedCustomAuto")).FullName;
        File.WriteAllText(Path.Combine(stagedCustomAuto, "widget-secrets.json"), """{"secrets":{}}""");
        DeskBox.Services.Backup.PortableBackupProcessor.StripMachineBoundSecretsIfUntrustedRestore(
            stagedCustomAuto,
            Path.Combine(customAutoRoot, "auto-20261122.zip"),
            recoveryRoot,
            manifestKind: "automatic",
            preRestoreRoot,
            customAutoRoot);
        Assert.True(File.Exists(Path.Combine(stagedCustomAuto, "widget-secrets.json")));
    }
}
