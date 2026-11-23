using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml.Linq;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Dispatching;

namespace DeskBox.Tests;

public sealed class NativeAotAndCapsuleHydrationOptimizationTests
{
    [Fact]
    public void DeskBoxServiceContainer_ResolvesSingletonsLazilyWithoutReflectionAndEnforcesDisposal()
    {
        var container = ServiceRegistry.CreateContainer();

        SettingsService settings = container.SettingsService;
        Assert.Same(settings, container.SettingsService);

        LocalizationService localization = container.LocalizationService;
        Assert.Same(localization, container.LocalizationService);

        ThemeService theme = container.ThemeService;
        Assert.Same(theme, container.ThemeService);

        FileService fileService = container.FileService;
        Assert.Same(fileService, container.FileService);

        OrganizerService organizer = container.OrganizerService;
        Assert.Same(organizer, container.OrganizerService);

        Assert.Same(container.AttachmentHealthService, container.AttachmentHealthService);
        Assert.Same(container.DiagnosticsBundleService, container.DiagnosticsBundleService);
        Assert.Same(container.ResizeGuideOverlayService, container.ResizeGuideOverlayService);
        Assert.Same(
            container.ManagedStorageDesktopShortcutService,
            container.ManagedStorageDesktopShortcutService);
        Assert.Same(container.AppUpdateService, container.AppUpdateService);

        container.Dispose();
        // Idempotent second Dispose must not throw.
        container.Dispose();

        Assert.Throws<ObjectDisposedException>(() => _ = container.SettingsService);
        Assert.Throws<ObjectDisposedException>(() => _ = container.FileService);
    }

    [Fact]
    public void UiElementCursorHelper_UsesCompiledUnsafeAccessorWithoutReflectionPropertyLookup()
    {
        Type? helperType = typeof(App).Assembly.GetType("DeskBox.Helpers.UiElementCursorHelper");
        Assert.NotNull(helperType);

        MethodInfo? accessor = helperType!.GetMethod(
            "SetProtectedCursor",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(accessor);

        var unsafeAttr = accessor!.GetCustomAttribute<UnsafeAccessorAttribute>();
        Assert.NotNull(unsafeAttr);
        Assert.Equal(UnsafeAccessorKind.Method, unsafeAttr!.Kind);
        Assert.Equal("set_ProtectedCursor", unsafeAttr.Name);
    }

    [Fact]
    public async Task StartupMetadataPin_PreservesPreNormalizationJsonBytesInAutomaticBackupArchive()
    {
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "deskbox-startup-pin-test-" + Guid.NewGuid().ToString("N"));
        string appRoot = Path.Combine(tempRoot, "DeskBox");
        string dataDir = Path.Combine(appRoot, "data");
        string attachmentsDir = Path.Combine(dataDir, "Attachments");
        string recoveryRoot = Path.Combine(tempRoot, "DeskBox-Recovery");
        Directory.CreateDirectory(attachmentsDir);

        try
        {
            const string preMutationSettings = "{\"schemaVersion\":1,\"marker\":\"pre-restore-settings\"}";
            const string preMutationLayout = "{\"layout\":{\"widgets\":[{\"id\":\"pre-restore-widget\"}]}}";
            const string preMutationTodo = "{\"items\":[{\"id\":\"pre-restore-todo\"}]}";

            string settingsPath = Path.Combine(dataDir, "settings.json");
            string layoutPath = Path.Combine(dataDir, "widget-layout.json");
            string todoPath = Path.Combine(dataDir, "todo.json");
            string attachmentPath = Path.Combine(attachmentsDir, "payload.bin");

            await File.WriteAllTextAsync(settingsPath, preMutationSettings, Encoding.UTF8);
            await File.WriteAllTextAsync(layoutPath, preMutationLayout, Encoding.UTF8);
            await File.WriteAllTextAsync(todoPath, preMutationTodo, Encoding.UTF8);
            byte[] attachmentBytes = [1, 2, 3, 4, 5, 6, 7, 8];
            await File.WriteAllBytesAsync(attachmentPath, attachmentBytes);

            var backupService = new DeskBoxDataBackupService(appRoot, recoveryRoot);
            backupService.UpdateAutomaticBackupOptions(new AutomaticBackupOptions(
                IsEnabled: true,
                IntervalMinutes: 60,
                RetentionCount: 5,
                CustomDirectory: null));

            // 1. Capture millisecond-scale metadata pin before widget restoration mutates JSON files.
            await backupService.CaptureStartupMetadataPinIfDueAsync();

            // 2. Simulate Phase 3 widget restoration overwriting settings/layout/todo on disk.
            await File.WriteAllTextAsync(
                settingsPath,
                "{\"schemaVersion\":1,\"marker\":\"mutated-after-restore\"}",
                Encoding.UTF8);
            await File.WriteAllTextAsync(
                layoutPath,
                "{\"layout\":{\"widgets\":[]}}",
                Encoding.UTF8);
            await File.WriteAllTextAsync(
                todoPath,
                "{\"items\":[]}",
                Encoding.UTF8);
            await File.WriteAllTextAsync(
                Path.Combine(dataDir, "desktop-organization-recovery.json"),
                "{\"postRestoreCreated\":true}",
                Encoding.UTF8);

            // 3. Run the scheduled automatic snapshot in the background and verify it archives
            //    the exact pre-mutation JSON bytes alongside the on-disk attachment payload.
            LocalBackupResult snapshotResult = await backupService.CreateAutomaticSnapshotResultAsync(force: false);
            Assert.Equal(BackupOutcome.Succeeded, snapshotResult.Outcome);
            Assert.False(string.IsNullOrWhiteSpace(snapshotResult.ArchivePath));
            Assert.True(File.Exists(snapshotResult.ArchivePath));

            using ZipArchive archive = ZipFile.OpenRead(snapshotResult.ArchivePath!);
            Assert.Equal(preMutationSettings, ReadUtf8ZipEntry(archive, "data/settings.json"));
            Assert.Equal(preMutationLayout, ReadUtf8ZipEntry(archive, "data/widget-layout.json"));
            Assert.Equal(preMutationTodo, ReadUtf8ZipEntry(archive, "data/todo.json"));
            Assert.Null(archive.GetEntry("data/desktop-organization-recovery.json"));

            ZipArchiveEntry? attachmentEntry = archive.GetEntry("data/Attachments/payload.bin");
            Assert.NotNull(attachmentEntry);
            using var ms = new MemoryStream();
            await using (Stream s = attachmentEntry!.Open())
            {
                await s.CopyToAsync(ms);
            }
            Assert.Equal(attachmentBytes, ms.ToArray());
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task ProgressiveRematerialization_CapsFirstSyncFrameAt24AndStreamsRemainingSlices()
    {
        string tempDir = Path.Combine(
            Path.GetTempPath(),
            "deskbox-prog-remat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        for (int i = 0; i < 80; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, $"item-{i:D3}.txt"), "x");
        }

        DispatcherQueueController controller = DispatcherQueueController.CreateOnDedicatedThread();
        try
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Assert.True(controller.DispatcherQueue.TryEnqueue(async () =>
            {
                // The headless host installs no dispatcher sync context, and WinUI's built-in
                // DispatcherQueueSynchronizationContext.Post does not re-install itself on the
                // resumed callback, so a second await would resume on a thread-pool thread and
                // race low-priority slice callbacks mutating RenderedItems. Keep a sticky sync
                // context installed across every await so all ViewModel calls stay serialized on
                // the DispatcherQueue thread just like the real WinUI host.
                SynchronizationContext.SetSynchronizationContext(
                    new StickyDispatcherQueueSynchronizationContext(controller.DispatcherQueue));
                try
                {
                    var settings = new SettingsService();
                    var fileService = new FileService();
                    var organizer = TestOrganizerServices.Create(settings, fileService);
                    var config = new WidgetConfig
                    {
                        Id = "prog-remat-test",
                        WidgetKind = WidgetKind.File,
                        Name = "Files",
                        MappedFolderPath = tempDir,
                        FollowsDefaultStoragePath = false,
                        SortMode = WidgetSortMode.Name
                    };

                    using var vm = new WidgetViewModel(
                        config,
                        fileService,
                        organizer,
                        settings,
                        dispatcherQueue: controller.DispatcherQueue);
                    await vm.InitializeAsync();
                    Assert.True(controller.DispatcherQueue.HasThreadAccess);
                    vm.EnsureProjectionAndRenderWindowSynchronizedForHydration();
                    Assert.Equal(80, vm.RenderedItems.Count);

                    // Simulate post-hibernation rematerialization: initial synchronous layout pass
                    // must only materialize the first 24 items.
                    vm.BeginProgressiveRematerialization();
                    Assert.Equal(24, vm.RenderedItems.Count);

                    // Allow low-priority slices to drain on the real DispatcherQueue.
                    var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void WaitUntilFull()
                    {
                        if (vm.RenderedItems.Count >= 80)
                        {
                            drained.TrySetResult();
                            return;
                        }

                        _ = controller.DispatcherQueue.TryEnqueue(
                            DispatcherQueuePriority.Low,
                            WaitUntilFull);
                    }
                    _ = controller.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, WaitUntilFull);
                    await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    Assert.True(controller.DispatcherQueue.HasThreadAccess);
                    Assert.Equal(80, vm.RenderedItems.Count);

                    // Verify selection restoration fast-forwards immediately without waiting for slices.
                    vm.BeginProgressiveRematerialization();
                    Assert.Equal(24, vm.RenderedItems.Count);
                    vm.CompleteProgressiveRematerializationNow();
                    Assert.Equal(80, vm.RenderedItems.Count);

                    vm.Dispose();
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }));

            await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            await controller.ShutdownQueueAsync();
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    public void NativeAotProjects_ConfigureLeftShiftedAnalyzersFeatureSwitchesPgoAndMstat()
    {
        XDocument sharedProps = XDocument.Load(TestPaths.FromRepository("src/Directory.Build.props"));
        XDocument mainProj = XDocument.Load(TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"));
        Assert.Equal("true", mainProj.Descendants("EnableTrimAnalyzer").Single().Value);
        Assert.Equal("true", mainProj.Descendants("EnableAotAnalyzer").Single().Value);
        Assert.Equal("false", sharedProps.Descendants("MetricsSupport").Single().Value);
        Assert.Equal("false", sharedProps.Descendants("HttpActivityPropagationSupport").Single().Value);
        Assert.Equal("false", sharedProps.Descendants("XmlResolverIsNetworkingEnabledByDefault").Single().Value);
        Assert.Equal("false", sharedProps.Descendants("AutoreleasePoolSupport").Single().Value);

        XElement sharedAotGroup = sharedProps
            .Descendants("PropertyGroup")
            .Single(g => (string?)g.Attribute("Condition") == "'$(PublishAot)' == 'true'");
        Assert.Equal("Size", sharedAotGroup.Element("OptimizationPreference")?.Value);
        Assert.Equal("true", sharedAotGroup.Element("IlcPgoOptimize")?.Value);
        Assert.Equal("true", sharedAotGroup.Element("IlcFoldIdenticalMethodBodies")?.Value);
        Assert.Equal("true", sharedAotGroup.Element("IlcGenerateMstatFile")?.Value);

        XElement mainAotGroup = mainProj
            .Descendants("PropertyGroup")
            .Single(g => (string?)g.Attribute("Condition") == "'$(PublishAot)' == 'true'");
        Assert.Equal("true", mainAotGroup.Element("StackTraceSupport")?.Value);

        XDocument updaterProj = XDocument.Load(TestPaths.FromRepository("src/DeskBox.Updater/DeskBox.Updater.csproj"));
        Assert.Equal("true", updaterProj.Descendants("IsAotCompatible").Single().Value);
        XElement updaterAotGroup = updaterProj
            .Descendants("PropertyGroup")
            .Single(g => (string?)g.Attribute("Condition") == "'$(PublishAot)' == 'true'");
        Assert.Equal("false", updaterAotGroup.Element("StackTraceSupport")?.Value);

        string mibcPath = TestPaths.FromRepository("src/DeskBox/Profiles/DeskBox.Startup.mibc");
        Assert.True(File.Exists(mibcPath));
        Assert.True(new FileInfo(mibcPath).Length > 1024);
    }

    private sealed class StickyDispatcherQueueSynchronizationContext(DispatcherQueue queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
            if (!queue.TryEnqueue(() =>
                {
                    SynchronizationContext? previous = Current;
                    SetSynchronizationContext(this);
                    try
                    {
                        d(state);
                    }
                    finally
                    {
                        SetSynchronizationContext(previous);
                    }
                }))
            {
                throw new InvalidOperationException("Failed to enqueue continuation on DispatcherQueue.");
            }
        }

        public override SynchronizationContext CreateCopy() => this;
    }

    private static string ReadUtf8ZipEntry(ZipArchive archive, string entryName)
    {
        ZipArchiveEntry? entry = archive.GetEntry(entryName);
        Assert.NotNull(entry);
        using var reader = new StreamReader(entry!.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
