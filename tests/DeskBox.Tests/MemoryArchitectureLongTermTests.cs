using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Views;
using Microsoft.UI.Dispatching;

namespace DeskBox.Tests;

public sealed class MemoryArchitectureLongTermTests
{
    [Fact]
    public void Phase2_PureWin32TrayAndPngEncoder_EliminatesNotifyIconAndSystemDrawing()
    {
        string csproj = Read("src/DeskBox/DeskBox.csproj");

        Assert.DoesNotContain("H.NotifyIcon", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("System.Drawing.Common", csproj, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(TestPaths.FromRepository("src/DeskBox/Platform/NativeTrayIconHost.cs")));

        // Verify pure BGRA -> PNG encoder emits valid PNG signature + IHDR + IDAT + IEND.
        byte[] bgra = new byte[4 * 4 * 4];
        for (int i = 0; i < bgra.Length; i += 4)
        {
            bgra[i + 0] = 0x40;
            bgra[i + 1] = 0x80;
            bgra[i + 2] = 0xC0;
            bgra[i + 3] = 0xFF;
        }

        byte[] png = IconPngEncoder.EncodeRegionAsPng(
            new IconPngEncoder.IconPixelBuffer(4, 4, bgra),
            0,
            0,
            4,
            4);
        Assert.True(png.Length > 32);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)));
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }

    [Fact]
    public void Phase2_PhysicalSettingsProject_StripsSettingsPayloadAndPriFromResidentHost()
    {
        string residentCsproj = Read("src/DeskBox/DeskBox.csproj");
        string settingsCsproj = Read("src/DeskBox.Settings/DeskBox.Settings.csproj");

        Assert.Contains("ExcludeSettingsOnlyToolkitPriFromResidentHost", residentCsproj, StringComparison.Ordinal);
        Assert.Contains("'%(_PriFile.Filename)' == 'CommunityToolkit.WinUI.Controls.ColorPicker'", residentCsproj, StringComparison.Ordinal);
        Assert.Contains("'%(_PriFile.Filename)' == 'CommunityToolkit.WinUI.Controls.SettingsControls'", residentCsproj, StringComparison.Ordinal);
        Assert.Contains("Views\\SettingsWindow.xaml", residentCsproj, StringComparison.Ordinal);
        Assert.Contains("Views\\SettingsSections\\**\\*.xaml", residentCsproj, StringComparison.Ordinal);

        Assert.Contains("DESKBOX_SETTINGS_HOST_BINARY", settingsCsproj, StringComparison.Ordinal);
        Assert.Contains("<AssemblyName>DeskBox.Settings</AssemblyName>", settingsCsproj, StringComparison.Ordinal);

        // Native AOT size/trim optimizations enabled on resident host.
        Assert.Contains("<OptimizationPreference>Size</OptimizationPreference>", residentCsproj, StringComparison.Ordinal);
        Assert.Contains("<IlcOptimizationPreference>Size</IlcOptimizationPreference>", residentCsproj, StringComparison.Ordinal);
        Assert.Contains("<IlcFoldIdenticalMethodBodies>true</IlcFoldIdenticalMethodBodies>", residentCsproj, StringComparison.Ordinal);
        Assert.Contains("<UseSystemResourceKeys Condition=\"'$(DeskBoxAotSmokeHarness)' != 'true'\">true</UseSystemResourceKeys>", residentCsproj, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase3_LeafControlsAndCollapsedCapsuleHibernation_PreserveViewModelAndContracts()
    {
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(GlanceWidgetContent)));
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(WeatherWidgetContent)));
        Assert.True(typeof(IDisposable).IsAssignableFrom(typeof(TodoWidgetContent)));

        Assert.Equal(15_000, WidgetWindowBase.PostCollapseSoftTrimDelayMs);
        Assert.Equal(90_000, WidgetWindowBase.PostCollapseHibernationDelayMs);

        string collapseSource = Read("src/DeskBox/Views/WidgetWindowBase.Collapse.cs");
        Assert.Contains("OnCollapsedSoftTrimTimerElapsed", collapseSource, StringComparison.Ordinal);
        Assert.Contains("OnCollapsedContentHibernationTimerElapsed", collapseSource, StringComparison.Ordinal);
        Assert.Contains("if (_collapsedContentHibernationTimer is not null)", collapseSource, StringComparison.Ordinal);
        Assert.Contains("HasActiveCompactHoverOrDragProbeTarget()", collapseSource, StringComparison.Ordinal);
        Assert.Contains("CreateObservedCompactSubclassMessage(", collapseSource, StringComparison.Ordinal);
        Assert.Contains("TryHibernateCollapsedContent()", collapseSource, StringComparison.Ordinal);
        Assert.Contains("EnsureCollapsedContentViewAttached()", collapseSource, StringComparison.Ordinal);

        string shellSource = Read("src/DeskBox/Controls/WidgetShell.xaml.cs");
        Assert.Contains("bool deferCollapsedViewAttachment =", shellSource, StringComparison.Ordinal);

        string adapterBaseSource = Read("src/DeskBox/Controls/WidgetContents/WidgetContentAdapterBase.cs");
        Assert.Contains("internal bool HibernateCollapsedView()", adapterBaseSource, StringComparison.Ordinal);
        Assert.Contains("OnHibernatingView(view);", adapterBaseSource, StringComparison.Ordinal);
        Assert.Contains("transientContent.RestoreTransientState(savedState);", adapterBaseSource, StringComparison.Ordinal);

        string fileSurfaceSource = Read("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        Assert.Contains("internal void DisposeLeafOnly()", fileSurfaceSource, StringComparison.Ordinal);
        Assert.Contains("internal void SyncFromResidentViewModel()", fileSurfaceSource, StringComparison.Ordinal);
        Assert.DoesNotContain("OnWindowLongHidden()", fileSurfaceSource, StringComparison.Ordinal);

        string quickCaptureSource = Read("src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs");
        Assert.Contains("internal void DisposeLeafOnly()", quickCaptureSource, StringComparison.Ordinal);
        Assert.Contains("internal void SyncFromResidentViewModel()", quickCaptureSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Phase3_ContentWidgetWindowPool_EnforcesWarmCapacityLifelineFloorAndWatcherObligations()
    {
        // Warm cleanup scope preserves all hidden hosts.
        Assert.Equal(
            int.MaxValue,
            ContentWidgetWindowPool.ResolveWarmHiddenHostCapacity(
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeWarm,
                PerformanceSettingsPolicy.CacheBudgetSmall));

        // AllRecreatable respects Small=1 vs Balanced/Large=2.
        Assert.Equal(
            1,
            ContentWidgetWindowPool.ResolveWarmHiddenHostCapacity(
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
                PerformanceSettingsPolicy.CacheBudgetSmall));
        Assert.Equal(
            2,
            ContentWidgetWindowPool.ResolveWarmHiddenHostCapacity(
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
                PerformanceSettingsPolicy.CacheBudgetBalanced));

        HiddenContentHostCandidate[] candidates =
        [
            new("file-1", WidgetKind.File, IsVisible: false, IsNativeVisible: false, IsClosing: false, IsTransitionActive: false, IsGroupMember: false, HasFolderWatcherObligation: true),
            new("group-todo", WidgetKind.Todo, IsVisible: false, IsNativeVisible: false, IsClosing: false, IsTransitionActive: false, IsGroupMember: true, HasFolderWatcherObligation: false),
            new("clipboard-1", WidgetKind.Clipboard, IsVisible: false, IsNativeVisible: false, IsClosing: false, IsTransitionActive: false, IsGroupMember: false, HasFolderWatcherObligation: false),
            new("glance-1", WidgetKind.Glance, IsVisible: false, IsNativeVisible: false, IsClosing: false, IsTransitionActive: false, IsGroupMember: false, HasFolderWatcherObligation: false),
            new("weather-1", WidgetKind.Weather, IsVisible: false, IsNativeVisible: false, IsClosing: false, IsTransitionActive: false, IsGroupMember: false, HasFolderWatcherObligation: false),
            new("music-1", WidgetKind.Music, IsVisible: false, IsNativeVisible: false, IsClosing: false, IsTransitionActive: false, IsGroupMember: false, HasFolderWatcherObligation: false)
        ];

        // Eligible recreatable hosts = clipboard-1, glance-1, weather-1, music-1 (4 total).
        // Under Small budget (keep 1 warm), 3 heaviest evict in priority order: glance-1, weather-1, music-1; clipboard-1 remains warm, and file-1 + group-todo are untouched.
        IReadOnlyList<string> evictedSmall =
            ContentWidgetWindowPool.SelectExcessHiddenHostsToEvict(
                candidates,
                totalLoadedDesktopWindows: 6,
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
                PerformanceSettingsPolicy.CacheBudgetSmall);
        Assert.Equal(["glance-1", "weather-1", "music-1"], evictedSmall);

        // Under Balanced budget (keep 2 warm), only 2 heaviest evict: glance-1, weather-1.
        IReadOnlyList<string> evictedBalanced =
            ContentWidgetWindowPool.SelectExcessHiddenHostsToEvict(
                candidates,
                totalLoadedDesktopWindows: 6,
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
                PerformanceSettingsPolicy.CacheBudgetBalanced);
        Assert.Equal(["glance-1", "weather-1"], evictedBalanced);

        // Lifeline floor: if only 1 desktop window is loaded total, never evict it so DXamlCore never drops to 0 windows.
        HiddenContentHostCandidate[] singleHost =
        [
            new("glance-only", WidgetKind.Glance, IsVisible: false, IsNativeVisible: false, IsClosing: false, IsTransitionActive: false, IsGroupMember: false, HasFolderWatcherObligation: false)
        ];
        Assert.Empty(
            ContentWidgetWindowPool.SelectExcessHiddenHostsToEvict(
                singleHost,
                totalLoadedDesktopWindows: 1,
                PerformanceSettingsPolicy.HiddenCacheCleanupScopeAllRecreatable,
                PerformanceSettingsPolicy.CacheBudgetSmall));
    }

    [Fact]
    public void Phase3_WarmShellPool_EnforcesCapacityAndDeterministicTrim()
    {
        List<string> destroyed = [];
        var pool = new WarmShellPool<string>(maxCapacity: 2, destroyed.Add);

        Assert.True(pool.ParkOrDestroy("shell-a"));
        Assert.True(pool.ParkOrDestroy("shell-b"));
        Assert.False(pool.ParkOrDestroy("shell-overflow"));
        Assert.Equal(["shell-overflow"], destroyed);
        Assert.Equal(2, pool.Count);

        // LIFO acquire returns most recently warmed shell.
        Assert.True(pool.TryAcquire(out string? acquired));
        Assert.Equal("shell-b", acquired);
        Assert.Equal(1, pool.Count);

        Assert.True(pool.ParkOrDestroy("shell-c"));
        Assert.Equal(2, pool.Count);

        // TrimToCapacity(1) evicts oldest ("shell-a") and keeps newest ("shell-c").
        int trimmed = pool.TrimToCapacity(1);
        Assert.Equal(1, trimmed);
        Assert.Equal(["shell-overflow", "shell-a"], destroyed);
        Assert.True(pool.TryAcquire(out string? remaining));
        Assert.Equal("shell-c", remaining);
    }

    [Fact]
    public void Wave1_OneShotTimerPool_ReusesTimersWithZeroChurnAndBalancesDrain()
    {
        int nextId = 0;
        List<string> released = [];
        var pool = new OneShotTimerPool<string>(
            createTimer: () => $"timer-{++nextId}",
            releaseTimer: released.Add,
            maxRetained: 2);

        // Simulate 500 cancel + re-arm cycles on a single timer slot: must allocate only 1 timer.
        for (int i = 0; i < 500; i++)
        {
            string rented = pool.Rent();
            Assert.Equal("timer-1", rented);
            pool.Return(rented);
        }

        Assert.Equal(1, pool.TotalCreatedCount);
        Assert.Equal(0, pool.TotalReleasedCount);
        Assert.Equal(1, pool.RetainedCount);

        // Rent 3 concurrent timers and return all 3: 2 retained, 1 overflow released immediately.
        string t1 = pool.Rent();
        string t2 = pool.Rent();
        string t3 = pool.Rent();
        Assert.Equal(3, pool.TotalCreatedCount);

        pool.Return(t1);
        pool.Return(t2);
        pool.Return(t3);
        Assert.Equal(2, pool.RetainedCount);
        Assert.Equal(1, pool.TotalReleasedCount);
        Assert.Equal([t3], released);

        // DrainAll releases all retained timers so created == released.
        pool.DrainAll();
        Assert.Equal(0, pool.RetainedCount);
        Assert.Equal(pool.TotalCreatedCount, pool.TotalReleasedCount);
    }

    [Fact]
    public async Task Wave1_HeadlessAdaptersAndDrops_DoNotMaterializeLeafViews()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Wave1Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string widgetsRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "widgets")).FullName;
            string sampleFile = Path.Combine(tempRoot, "wave1-note.md");
            await File.WriteAllTextAsync(sampleFile, "# Wave 1 headless drop");

            var config = new WidgetConfig
            {
                Id = "todo-wave1-hibernated",
                WidgetKind = WidgetKind.Todo,
                Name = "Todo"
            };
            var store = new TodoWidgetStore(config.Id, widgetsRoot);
            var localization = TestServices.CreateLocalizationService();
            var viewModel = new DeskBox.ViewModels.TodoWidgetViewModel(store, localization, config);
            int viewFactoryCalls = 0;
            using var adapter = new TodoWidgetContentAdapter(
                config,
                viewModel,
                _ =>
                {
                    viewFactoryCalls++;
                    throw new InvalidOperationException("Hibernated Todo adapter must not materialize its XAML view.");
                });

            await adapter.InitializeAsync();
            Assert.False(adapter.HasMaterializedView);
            Assert.Equal(0, viewFactoryCalls);

            int feedbackCount = 0;
            adapter.FeedbackRequested += (_, _) => feedbackCount++;

            bool imported = await adapter.ImportNativeDroppedFilesAsync(
                [new DroppedFilePath(sampleFile, "wave1-note.md", ForceManagedCopy: false)],
                targetItem: null);

            Assert.True(imported);
            Assert.False(adapter.HasMaterializedView);
            Assert.Equal(0, viewFactoryCalls);
            Assert.Equal(1, feedbackCount);
            DeskBox.ViewModels.TodoItemViewModel created = Assert.Single(adapter.ViewModel.Items);
            Assert.Equal("wave1-note", created.Text);
            Assert.Single(created.Attachments);
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

        // Source contracts for Born-Hibernated File/QuickCapture adapters and Todo row unsubscription.
        string fileAdapter = Read("src/DeskBox/Controls/WidgetContents/FileWidgetContentAdapter.cs");
        Assert.Contains("? content.InitializeAsync(cancellationToken)\n            : ViewModel.InitializeAsync(cancellationToken);", fileAdapter.ReplaceLineEndings("\n"), StringComparison.Ordinal);

        string quickAdapter = Read("src/DeskBox/Controls/WidgetContents/QuickCaptureWidgetContentAdapter.cs");
        Assert.Contains("? content.InitializeContentAsync()\n            : ViewModel.InitializeAsync();", quickAdapter.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("ImportHibernatedNativeDroppedFilesAsync(files, targetItem)", quickAdapter, StringComparison.Ordinal);

        string todoDragDrop = Read("src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DragDrop.cs");
        Assert.Contains("_rowVisuals.AttachRow(sender, TodoItem_PropertyChanged", todoDragDrop, StringComparison.Ordinal);
        Assert.Contains("_rowVisuals.ClearSubscribedRows(TodoItem_PropertyChanged)", todoDragDrop, StringComparison.Ordinal);

        string todoRowVisuals = Read("src/DeskBox/Controls/Todo/TodoRowVisualController.cs");
        Assert.Contains("_subscribedRowItems.Remove(sender, out TodoItemViewModel? previousItem)", todoRowVisuals, StringComparison.Ordinal);
        Assert.Contains("previousItem.PropertyChanged -= propertyChangedHandler;", todoRowVisuals, StringComparison.Ordinal);

        string todoCode = Read("src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs");
        Assert.Contains("_notesAutosaveTimer.Tick -= NotesAutosaveTimer_Tick;", todoCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave2_QuickCaptureClipboardRemoved_PreservesPersistenceContracts()
    {
        Assert.False(File.Exists(TestPaths.FromRepository("src/DeskBox/Services/QuickCaptureClipboardService.cs")));
        Assert.False(File.Exists(TestPaths.FromRepository("src/DeskBox/Features/QuickCapture/QuickCaptureClipboardRuntime.cs")));
        Assert.False(File.Exists(TestPaths.FromRepository("src/DeskBox/Helpers/QuickCaptureClipboardActivationHelper.cs")));

        string surfaceXaml = Read("src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml");
        Assert.DoesNotContain("QuickCapture.Tab.Recent", surfaceXaml, StringComparison.Ordinal);

        string iconHelper = Read("src/DeskBox/Helpers/IconHelper.cs");
        Assert.Contains("DecodePixelType.Physical", iconHelper, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave2_IconHelper_ClearCacheScopeEvictsShellThumbnailsAndSkipsLohIconBytes()
    {
        const string scope = "wave2-test-scope";
        string mediaKey = $"thumb:{scope}|C:\\fake\\img.png:1:decode=96";
        string shellKey = $"shell-thumb:{scope}|C:\\fake\\doc.pdf:1:decode=96";
        string sharedKey = "shell-thumb:shared|C:\\fake\\keep.pdf:1:decode=96";

        var completedNullTask = Task.FromResult<Microsoft.UI.Xaml.Media.Imaging.BitmapImage?>(null);
        try
        {
            IconHelper.IndexThumbnailTaskForTest(mediaKey, completedNullTask);
            IconHelper.IndexThumbnailTaskForTest(shellKey, completedNullTask);
            IconHelper.IndexThumbnailTaskForTest(sharedKey, completedNullTask);

            Assert.True(IconHelper.HasThumbnailCacheEntryForTest(mediaKey));
            Assert.True(IconHelper.HasThumbnailCacheEntryForTest(shellKey));
            Assert.True(IconHelper.HasThumbnailCacheEntryForTest(sharedKey));

            IconHelper.ClearCacheScope(scope);

            Assert.False(IconHelper.HasThumbnailCacheEntryForTest(mediaKey));
            Assert.False(IconHelper.HasThumbnailCacheEntryForTest(shellKey));
            Assert.True(IconHelper.HasThumbnailCacheEntryForTest(sharedKey));
        }
        finally
        {
            IconHelper.ClearCacheScope(scope);
            IconHelper.ClearCacheScope("shared");
        }

        // LOH BMPv5 payloads (>= 85,000 bytes) for per-path keys must not be retained in s_iconBytesCache,
        // while compact shared "ext:" icons remain cached.
        string lohPathKey = "source:C:\\fake\\app.exe|path:C:\\fake\\app.exe:0:False:False:1:source";
        string extKey = "ext:.wave2test";
        byte[] lohBytes = new byte[IconHelper.MaxNonSharedIconBytePayloadLength + 128];
        byte[] smallBytes = [1, 2, 3, 4];
        try
        {
            IconHelper.StoreCachedIconBytes(lohPathKey, lohBytes);
            IconHelper.StoreCachedIconBytes(extKey, smallBytes);

            Assert.False(IconHelper.HasCachedIconBytesForTest(lohPathKey));
            Assert.True(IconHelper.HasCachedIconBytesForTest(extKey));
        }
        finally
        {
            IconHelper.RemoveCachedIconBytesForTest(lohPathKey);
            IconHelper.RemoveCachedIconBytesForTest(extKey);
        }
    }

    [Fact]
    public async Task Wave2_TodoStoreAndViewModel_StripTombstonesCacheSnapshotsAndDeltaUndo()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Wave2Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            // 1. Sibling QuickCapture thumbnail resolution on TodoAttachmentViewModel.
            string qcRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "quick-capture")).FullName;
            string imagesDir = Directory.CreateDirectory(Path.Combine(qcRoot, "images")).FullName;
            string thumbsDir = Directory.CreateDirectory(Path.Combine(qcRoot, "thumbnails")).FullName;
            string fullImage = Path.Combine(imagesDir, "abc123hash.jpg");
            string thumbImage = Path.Combine(thumbsDir, "abc123hash.png");
            await File.WriteAllBytesAsync(fullImage, [1, 2, 3]);
            await File.WriteAllBytesAsync(thumbImage, [4, 5, 6]);
            Assert.Equal(thumbImage, DeskBox.ViewModels.TodoAttachmentViewModel.ResolvePreviewSourcePath(fullImage));

            // 2. TodoWidgetStore stripped tombstones + shared read-only snapshot cache + StoreChanged event.
            string widgetsRoot = Directory.CreateDirectory(Path.Combine(tempRoot, "widgets")).FullName;
            var store = new TodoWidgetStore(widgetsRoot, "wave2-todo");
            int storeChangedCount = 0;
            void OnChanged(string path, string id)
            {
                if (string.Equals(id, "wave2-todo", StringComparison.Ordinal))
                {
                    storeChangedCount++;
                }
            }

            TodoWidgetStore.StoreChanged += OnChanged;
            try
            {
                await store.SaveAsync(new TodoWidgetData
                {
                    Items =
                    [
                        new TodoItem
                        {
                            Id = "live-1",
                            Text = "Keep me",
                            Notes = "Live markdown notes"
                        },
                        new TodoItem
                        {
                            Id = "deleted-1",
                            Text = "Huge deleted title",
                            Notes = new string('X', 8192),
                            Steps = [new TodoStep { Text = "old step" }],
                            Attachments = [new TodoAttachment { FilePath = fullImage }],
                            IsDeleted = true
                        }
                    ]
                });
                Assert.Equal(1, storeChangedCount);

                TodoWidgetData snap1 = await store.LoadReadOnlySnapshotAsync();
                TodoWidgetData snap2 = await store.LoadReadOnlySnapshotAsync();
                Assert.Same(snap1, snap2);

                TodoItem persistedTombstone = Assert.Single(snap1.Items, item => item.IsDeleted);
                Assert.Equal("deleted-1", persistedTombstone.Id);
                Assert.Equal(string.Empty, persistedTombstone.Text);
                Assert.Null(persistedTombstone.Notes);
                Assert.Empty(persistedTombstone.Steps);
                Assert.Empty(persistedTombstone.Attachments);

                // LoadAsync returns isolated mutable clones while sharing the cached snapshot.
                TodoWidgetData mutable = await store.LoadAsync();
                Assert.NotSame(snap1, mutable);
                mutable.Items[0].Text = "Mutated in memory only";
                Assert.Equal("Keep me", snap1.Items.Single(item => item.Id == "live-1").Text);
            }
            finally
            {
                TodoWidgetStore.StoreChanged -= OnChanged;
            }

            // 3. TodoWidgetViewModel VisibleItemsSource memoization + Delta Undo preserving concurrent edits.
            var config = new WidgetConfig
            {
                Id = "wave2-vm",
                WidgetKind = WidgetKind.Todo,
                Name = "Todo"
            };
            var vmStore = new TodoWidgetStore(widgetsRoot, config.Id);
            var localization = TestServices.CreateLocalizationService();
            using var vm = new DeskBox.ViewModels.TodoWidgetViewModel(vmStore, localization, config);
            await vm.InitializeAsync();

            var first = await vm.AddItemAsync("First task");
            var second = await vm.AddItemAsync("Second task");
            Assert.NotNull(first);
            Assert.NotNull(second);

            object[] sourceA = vm.VisibleItemsSource;
            object[] sourceB = vm.VisibleItemsSource;
            Assert.Same(sourceA, sourceB);

            // Delete "First task", then concurrently edit "Second task" BEFORE undoing the delete!
            Assert.True(await vm.DeleteItemAsync(first!.Id));
            Assert.True(await vm.UpdateItemTextAsync(second!.Id, "Second task edited after delete"));

            Assert.True(await vm.UndoLastActionAsync());
            Assert.Equal(2, vm.Items.Count);
            Assert.Contains(vm.Items, entry => entry.Id == first.Id && entry.Text == "First task");
            Assert.Contains(vm.Items, entry => entry.Id == second.Id && entry.Text == "Second task edited after delete");
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
    public void Wave2_TodoReminderAndSearch_DeadlineWakeupAndBodyTruncation()
    {
        DateTimeOffset now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        // No due items -> null wakeup (timer disarmed, 0 background ticks).
        Assert.Null(TodoReminderService.FindEarliestNextWakeup(
            "w1",
            [new TodoItem { Id = "plain", Text = "No due date" }],
            now,
            defaultOffsetMinutes: 15));

        // Upcoming reminder in 8 minutes -> exact target timestamp & 8-minute clamped delay.
        var upcoming = new TodoItem
        {
            Id = "due-soon",
            Text = "Call team",
            DueDate = now.AddMinutes(23),
            ReminderOffsetMinutes = 15
        };
        DateTimeOffset? nextWakeup = TodoReminderService.FindEarliestNextWakeup("w1", [upcoming], now, 15);
        Assert.Equal(now.AddMinutes(8), nextWakeup);
        Assert.Equal(TimeSpan.FromMinutes(8), TodoReminderService.ComputeClampedWakeupDelay(now, nextWakeup));

        // Far-future reminder (3 hours out) clamps to the 15-minute drift ceiling.
        Assert.Equal(
            TodoReminderService.MaximumWakeupDelay,
            TodoReminderService.ComputeClampedWakeupDelay(now, now.AddHours(3)));

        // SearchEngineService body truncation caps oversized bodies without splitting surrogate pairs.
        string hugeBody = new('A', SearchEngineService.MaxIndexedBodyCharacters + 500);
        string? truncated = SearchEngineService.TruncateIndexedBody(hugeBody);
        Assert.NotNull(truncated);
        Assert.Equal(SearchEngineService.MaxIndexedBodyCharacters, truncated!.Length);
    }

    [Fact]
    public void Wave3_ShellChangeNotifyAndFileSurface_ZeroStorageSearchAndFilteredBroadcast()
    {
        string watcherSource = Read("src/DeskBox/Services/FolderWatcherService.cs");
        Assert.DoesNotContain("Windows.Storage.Search", watcherSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageItemQueryResult", watcherSource, StringComparison.Ordinal);
        Assert.DoesNotContain("IncludeSubdirectories = true", watcherSource, StringComparison.Ordinal);
        Assert.Contains("ShellChangeNotificationService.Shared.Subscribe", watcherSource, StringComparison.Ordinal);

        string shellNotifySource = Read("src/DeskBox/Platform/ShellChangeNotificationService.cs");
        Assert.DoesNotContain("fRecursive = true", shellNotifySource, StringComparison.Ordinal);
        Assert.Contains("fRecursive = false", shellNotifySource, StringComparison.Ordinal);

        var (userDesktop, publicDesktop) = FileService.GetDesktopPaths();
        if (!string.IsNullOrWhiteSpace(userDesktop))
        {
            Assert.Equal(
                ShellChangeNotificationService.DesktopRegistrationKey,
                ShellChangeNotificationService.GetRegistrationKey(userDesktop));
            Assert.NotEqual(
                ShellChangeNotificationService.DesktopRegistrationKey,
                ShellChangeNotificationService.GetRegistrationKey(Path.Combine(userDesktop, "SubFolder")));
        }

        if (!string.IsNullOrWhiteSpace(publicDesktop))
        {
            Assert.Equal(
                ShellChangeNotificationService.DesktopRegistrationKey,
                ShellChangeNotificationService.GetRegistrationKey(publicDesktop));
        }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
        {
            Assert.NotEqual(
                ShellChangeNotificationService.DesktopRegistrationKey,
                ShellChangeNotificationService.GetRegistrationKey(appData));
        }

        const string root = @"C:\Users\simon\Desktop";
        const string rootSep = @"C:\Users\simon\Desktop\";

        // Unrelated path & deep nested path must be rejected before allocating notifications.
        Assert.False(ShellChangeNotificationService.TryClassifyShellEvent(
            root, rootSep, ShellChangeNotificationService.ShcneCreate,
            @"D:\Other\file.txt".AsSpan(), ReadOnlySpan<char>.Empty,
            out _, out _));
        Assert.False(ShellChangeNotificationService.TryClassifyShellEvent(
            root, rootSep, ShellChangeNotificationService.ShcneUpdateItem,
            @"C:\Users\simon\Desktop\SubFolder\Nested\file.txt".AsSpan(), ReadOnlySpan<char>.Empty,
            out _, out _));

        // Direct child folder desktop.ini -> FolderIconChanged on child directory.
        Assert.True(ShellChangeNotificationService.TryClassifyShellEvent(
            root, rootSep, ShellChangeNotificationService.ShcneUpdateItem,
            @"C:\Users\simon\Desktop\ProjectAlpha\desktop.ini".AsSpan(), ReadOnlySpan<char>.Empty,
            out ShellFolderNotification iconNotice, out _));
        Assert.Equal(ShellFolderNotificationKind.FolderIconChanged, iconNotice.Kind);
        Assert.Equal(@"C:\Users\simon\Desktop\ProjectAlpha", iconNotice.Path);

        // In-folder rename -> Renamed; move out -> Deleted; move in -> Created.
        Assert.True(ShellChangeNotificationService.TryClassifyShellEvent(
            root, rootSep, ShellChangeNotificationService.ShcneRenameItem,
            @"C:\Users\simon\Desktop\old.txt".AsSpan(),
            @"C:\Users\simon\Desktop\new.txt".AsSpan(),
            out ShellFolderNotification renamedNotice, out _));
        Assert.Equal(ShellFolderNotificationKind.Renamed, renamedNotice.Kind);
        Assert.Equal(@"C:\Users\simon\Desktop\new.txt", renamedNotice.Path);
        Assert.Equal(@"C:\Users\simon\Desktop\old.txt", renamedNotice.OldPath);

        Assert.True(ShellChangeNotificationService.TryClassifyShellEvent(
            root, rootSep, ShellChangeNotificationService.ShcneRenameItem,
            @"C:\Users\simon\Desktop\moved-out.txt".AsSpan(),
            @"D:\Archive\moved-out.txt".AsSpan(),
            out ShellFolderNotification movedOutNotice, out _));
        Assert.Equal(ShellFolderNotificationKind.Deleted, movedOutNotice.Kind);

        Assert.True(ShellChangeNotificationService.TryClassifyShellEvent(
            root, rootSep, ShellChangeNotificationService.ShcneRenameItem,
            @"D:\Staging\moved-in.txt".AsSpan(),
            @"C:\Users\simon\Desktop\moved-in.txt".AsSpan(),
            out ShellFolderNotification movedInNotice, out _));
        Assert.Equal(ShellFolderNotificationKind.Created, movedInNotice.Kind);

        // Watched root UpdateDir or RenameFolder -> DirectoryReload.
        Assert.True(ShellChangeNotificationService.TryClassifyShellEvent(
            root, rootSep, ShellChangeNotificationService.ShcneUpdateDir,
            @"C:\Users\simon\Desktop".AsSpan(), ReadOnlySpan<char>.Empty,
            out ShellFolderNotification reloadNotice, out _));
        Assert.Equal(ShellFolderNotificationKind.DirectoryReload, reloadNotice.Kind);

        Assert.True(ShellChangeNotificationService.TryClassifyShellEvent(
            @"D:\Work\Box", @"D:\Work\Box\", ShellChangeNotificationService.ShcneRenameFolder,
            @"D:\Work\Box".AsSpan(), @"D:\Work\BoxRenamed".AsSpan(),
            out ShellFolderNotification rootRenamedNotice, out _));
        Assert.Equal(ShellFolderNotificationKind.DirectoryReload, rootRenamedNotice.Kind);

        // FileItemSurface filters irrelevant WidgetViewModel PropertyChanged broadcasts.
        Assert.False(DeskBox.Controls.FileItemSurface.ShouldNotifyPresentationForLayoutProperty("IsLoading"));
        Assert.False(DeskBox.Controls.FileItemSurface.ShouldNotifyPresentationForLayoutProperty("WidgetOpacity"));
        Assert.False(DeskBox.Controls.FileItemSurface.ShouldNotifyPresentationForLayoutProperty("Name"));
        Assert.True(DeskBox.Controls.FileItemSurface.ShouldNotifyPresentationForLayoutProperty(null));
        Assert.True(DeskBox.Controls.FileItemSurface.ShouldNotifyPresentationForLayoutProperty("ListItemMargin"));
        Assert.True(DeskBox.Controls.FileItemSurface.ShouldNotifyPresentationForLayoutProperty("IconTilePadding"));
        Assert.True(DeskBox.Controls.FileItemSurface.ShouldNotifyPresentationForLayoutProperty("ShowFileItemPathTooltips"));
    }

    [Fact]
    public async Task Wave3_ShellChangeNotify_ScopedNonRecursiveRegistrationAndChildDirectoryIconInvalidation()
    {
        const uint shcnfPathWAndFlush = 0x0005 | 0x1000;

        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.ShellScopeTests", Guid.NewGuid().ToString("N"));
        string dirA = Path.Combine(tempRoot, "RootA");
        string dirB = Path.Combine(tempRoot, "RootB");
        string dirBNested = Path.Combine(dirB, "Sub", "Deep");
        string unrelatedDir = Path.Combine(tempRoot, "UnrelatedAppDataLike");
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirBNested);
        Directory.CreateDirectory(unrelatedDir);

        DispatcherQueueController? controller;
        try
        {
            controller = DispatcherQueueController.CreateOnDedicatedThread();
        }
        catch (COMException)
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
            return;
        }

        try
        {
            var receivedA = new ConcurrentQueue<ShellFolderNotification>();
            var receivedB = new ConcurrentQueue<ShellFolderNotification>();
            var directCreatedTcs = new TaskCompletionSource<ShellFolderNotification>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            IDisposable? subA1 = null;
            IDisposable? subA2 = null;
            IDisposable? subB = null;

            await RunOnDispatcherAsync(controller.DispatcherQueue, () =>
            {
                subA1 = ShellChangeNotificationService.Shared.Subscribe(
                    dirA,
                    controller.DispatcherQueue,
                    n => receivedA.Enqueue(n));
                subA2 = ShellChangeNotificationService.Shared.Subscribe(
                    dirA,
                    controller.DispatcherQueue,
                    n => receivedA.Enqueue(n));
                subB = ShellChangeNotificationService.Shared.Subscribe(
                    dirB,
                    controller.DispatcherQueue,
                    n =>
                    {
                        receivedB.Enqueue(n);
                        if (n.Kind == ShellFolderNotificationKind.Created)
                        {
                            directCreatedTcs.TrySetResult(n);
                        }
                    });
            });

            try
            {
                Assert.NotNull(subA1);
                Assert.NotNull(subA2);
                Assert.NotNull(subB);
                Assert.True(ShellChangeNotificationService.Shared.IsNativeRegistered);
                Assert.Equal(2, ShellChangeNotificationService.Shared.ActiveRootRegistrationCount);

                // Disposing one duplicate subscriber keeps RootA's native registration alive.
                await RunOnDispatcherAsync(controller.DispatcherQueue, () => subA1!.Dispose());
                subA1 = null;
                Assert.Equal(2, ShellChangeNotificationService.Shared.ActiveRootRegistrationCount);

                // Disposing the last RootA subscriber immediately deregisters RootA while RootB stays active.
                await RunOnDispatcherAsync(controller.DispatcherQueue, () => subA2!.Dispose());
                subA2 = null;
                Assert.Equal(1, ShellChangeNotificationService.Shared.ActiveRootRegistrationCount);
                Assert.True(ShellChangeNotificationService.Shared.IsNativeRegistered);

                string afterUnsubFile = Path.Combine(dirA, "after-unsub.txt");
                string unrelatedFile = Path.Combine(unrelatedDir, "unrelated.txt");
                string nestedFile = Path.Combine(dirBNested, "nested.txt");
                string directFile = Path.Combine(dirB, "direct.txt");
                File.WriteAllText(afterUnsubFile, "a");
                File.WriteAllText(unrelatedFile, "u");
                File.WriteAllText(nestedFile, "n");
                File.WriteAllText(directFile, "d");

                SHChangeNotifyPath(ShellChangeNotificationService.ShcneCreate, shcnfPathWAndFlush, afterUnsubFile, null);
                SHChangeNotifyPath(ShellChangeNotificationService.ShcneCreate, shcnfPathWAndFlush, unrelatedFile, null);
                SHChangeNotifyPath(ShellChangeNotificationService.ShcneCreate, shcnfPathWAndFlush, nestedFile, null);
                SHChangeNotifyPath(ShellChangeNotificationService.ShcneCreate, shcnfPathWAndFlush, directFile, null);

                Task completed = await Task.WhenAny(directCreatedTcs.Task, Task.Delay(TimeSpan.FromSeconds(3)));
                Assert.Same(directCreatedTcs.Task, completed);

                ShellFolderNotification delivered = await directCreatedTcs.Task;
                Assert.Equal(ShellFolderNotificationKind.Created, delivered.Kind);
                Assert.Equal(directFile, delivered.Path);
                Assert.Empty(receivedA);
                Assert.DoesNotContain(receivedB, n =>
                    string.Equals(n.Path, nestedFile, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(n.Path, unrelatedFile, StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                await RunOnDispatcherAsync(controller.DispatcherQueue, () =>
                {
                    subA1?.Dispose();
                    subA2?.Dispose();
                    subB?.Dispose();
                });
            }

            Assert.Equal(0, ShellChangeNotificationService.Shared.ActiveRootRegistrationCount);
            Assert.False(ShellChangeNotificationService.Shared.IsNativeRegistered);

            // Also verify SHCNE_UPDATEITEM (Updated) on an existing child directory queues FolderIconChanged.
            string childFolder = Path.Combine(dirB, "CustomizedChildFolder");
            Directory.CreateDirectory(childFolder);
            using var watcher = new FolderWatcherService(controller.DispatcherQueue);
            typeof(FolderWatcherService)
                .GetProperty(nameof(FolderWatcherService.WatchedPath))!
                .SetValue(watcher, dirB);
            int generation = (int)typeof(FolderWatcherService)
                .GetField("_watchGeneration", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(watcher)!;
            MethodInfo onShellNotice = typeof(FolderWatcherService)
                .GetMethod("OnShellFolderNotification", BindingFlags.Instance | BindingFlags.NonPublic)!;
            FieldInfo pendingIconPathsField = typeof(FolderWatcherService)
                .GetField("_pendingIconPaths", BindingFlags.Instance | BindingFlags.NonPublic)!;

            onShellNotice.Invoke(
                watcher,
                [new ShellFolderNotification(ShellFolderNotificationKind.Updated, Path.Combine(dirB, "direct.txt")), generation]);
            var pendingIcons = (HashSet<string>)pendingIconPathsField.GetValue(watcher)!;
            Assert.Empty(pendingIcons);

            onShellNotice.Invoke(
                watcher,
                [new ShellFolderNotification(ShellFolderNotificationKind.Updated, childFolder), generation]);
            Assert.Contains(childFolder, pendingIcons);
        }
        finally
        {
            await controller.ShutdownQueueAsync();
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
                // Best-effort temp cleanup.
            }
        }
    }

    private static Task RunOnDispatcherAsync(DispatcherQueue queue, Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }))
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue onto DispatcherQueue."));
        }

        return tcs.Task;
    }

    [DllImport("shell32.dll", EntryPoint = "SHChangeNotify", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotifyPath(
        int wEventId,
        uint uFlags,
        string dwItem1,
        string? dwItem2);

    [Fact]
    public void Wave3_CustomWidgetMetrics_ZeroColdBootGpuHandlesAndSpanEngineKeyExtraction()
    {
        var evalCtx = new CustomWidgetEvaluationContext();
        Assert.Same(CustomWidgetSystemMetricsSnapshot.Empty, evalCtx.SystemMetrics);

        var config = new WidgetConfig
        {
            Id = "wave3-custom-pomodoro",
            WidgetKind = WidgetKind.Custom,
            Name = "Pomodoro"
        };
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Wave3Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new CustomWidgetDataStore(config.Id, tempRoot);
            using var vm = new DeskBox.ViewModels.CustomWidgetViewModel(
                config,
                TestServices.CreateLocalizationService(),
                store);

            // Constructing non-systemMetrics CustomWidgetViewModel must not eagerly open PDH/DXGI GPU samplers.
            if (CustomWidgetSystemMetricsSource.Shared.ActiveSubscriberCount == 0)
            {
                Assert.False(CustomWidgetSystemMetricsSource.Shared.HasOpenGpuSamplers);
            }
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

        ReadOnlySpan<char> engineKey = CustomWidgetNativeMetrics.GpuEngineCounterSampler.ExtractEngineKeySpan(
            "pid_4812_luid_0x00000000_0x00012345_phys_0_eng_0_engtype_3D".AsSpan());
        Assert.Equal("phys_0_eng_0_engtype_3D", engineKey.ToString());
        Assert.Equal("default", CustomWidgetNativeMetrics.GpuEngineCounterSampler.ExtractEngineKeySpan(ReadOnlySpan<char>.Empty).ToString());

        string rendererSource = Read("src/DeskBox/Controls/WidgetContents/CustomWidgetNativeRenderer.cs");
        Assert.Contains("SetOrAppendPoint(", rendererSource, StringComparison.Ordinal);
        Assert.Contains("_solidBrushCache", rendererSource, StringComparison.Ordinal);
        Assert.Contains("_sparklineBrushCache", rendererSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave3_GlanceWeatherMusic_LazyCalendarMemoizedForecastsAndLightweightProcessQuery()
    {
        // 1. Glance lightweight CalendarSurfaceRoot uses x:Load="{x:Bind IsCalendarElementLoaded}" without WinUI CalendarView.
        string glanceXaml = Read("src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml");
        Assert.Contains("x:Name=\"CalendarLayoutRoot\"", glanceXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CalendarSurfaceRoot\"", glanceXaml, StringComparison.Ordinal);
        Assert.Contains("x:Load=\"{x:Bind IsCalendarElementLoaded}\"", glanceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<CalendarView", glanceXaml, StringComparison.Ordinal);

        // 2. WeatherWidgetViewModel memoizes DailyForecastItemsSource and HourlyForecastItemsSource.
        var weatherConfig = new WidgetConfig
        {
            Id = "wave3-weather",
            WidgetKind = WidgetKind.Weather,
            Name = "Weather"
        };
        using var weatherVm = new DeskBox.ViewModels.WeatherWidgetViewModel(
            weatherConfig,
            new WeatherService(),
            TestServices.CreateLocalizationService());
        weatherVm.ApplyWeatherData(new WeatherData
        {
            LocationName = "Hangzhou",
            Current = new WeatherCurrent
            {
                Temperature = 22,
                ApparentTemperature = 21,
                WeatherCode = 0,
                IsDay = 1,
                Humidity = 55,
                WindSpeed = 12
            },
            Daily = new WeatherDaily
            {
                Time = ["2026-10-31", "2026-11-01"],
                WeatherCode = [0, 1],
                TemperatureMax = [24, 25],
                TemperatureMin = [15, 16],
                PrecipitationProbabilityMax = [10, 20]
            },
            Hourly = new WeatherHourly
            {
                Time = ["2026-10-31T12:00", "2026-10-31T13:00"],
                Temperature = [22, 23],
                PrecipitationProbability = [0, 10],
                WeatherCode = [0, 1]
            }
        });

        Assert.Equal(2, weatherVm.DailyForecastItemsSource.Length);
        Assert.Same(weatherVm.DailyForecastItemsSource, weatherVm.DailyForecastItemsSource);
        Assert.Same(weatherVm.HourlyForecastItemsSource, weatherVm.HourlyForecastItemsSource);

        // 3. CitySearchService retains zero static city list or Timer while keeping local pinyin search intact.
        string citySearchSource = Read("src/DeskBox/Services/CitySearchService.cs");
        Assert.DoesNotContain("s_predefined", citySearchSource, StringComparison.Ordinal);
        Assert.DoesNotContain("s_idleReleaseTimer", citySearchSource, StringComparison.Ordinal);
        List<WeatherCitySearchResult> hzMatches = CitySearchService.SearchLocal("hz", isEn: false);
        Assert.NotEmpty(hzMatches);

        // 4. MusicVolumeService resolves current process name via lightweight Win32 QueryFullProcessImageNameW,
        // and MusicWidgetViewModel.MediaInfo decodes covers at physical pixel width without CloneStream().
        string currentProcName = MusicVolumeService.GetProcessName((uint)Environment.ProcessId);
        Assert.False(string.IsNullOrWhiteSpace(currentProcName));

        string musicMediaSource = Read("src/DeskBox/ViewModels/MusicWidgetViewModel.MediaInfo.cs");
        Assert.DoesNotContain("CloneStream()", musicMediaSource, StringComparison.Ordinal);
        Assert.Contains("DecodePixelType = DecodePixelType.Physical", musicMediaSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave4_BornHibernatedCapsules_NeverMaterializeExpandedViewsOnColdBootOrIdleWarmup()
    {
        string contentWindow = Read("src/DeskBox/Views/ContentWidgetWindow.xaml.cs");
        Assert.Contains("if (CurrentContent is DeskBox.Controls.WidgetContents.WidgetContentAdapterBase)", contentWindow, StringComparison.Ordinal);

        string collapseSource = Read("src/DeskBox/Views/WidgetWindowBase.Collapse.cs");
        Assert.Contains("if (!urgent", collapseSource, StringComparison.Ordinal);
        Assert.Contains("HasUserCompactExpansionIntent", collapseSource, StringComparison.Ordinal);
        Assert.Contains("ArmCollapsedContentHibernationTimer();", collapseSource, StringComparison.Ordinal);

        string contentHost = Read("src/DeskBox/Controls/WidgetShellContentHost.cs");
        Assert.Contains("content is not DeskBox.Controls.WidgetContents.WidgetContentAdapterBase", contentHost, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave4_WidgetTextShadow_SuppressesCompositionForOpaquePlatesAndReleasesMarkdownSnapshot()
    {
        Assert.True(DeskBox.Helpers.WidgetTextShadow.ShouldSuppressForOpaquePlate(
            0.95,
            SettingsService.WidgetMaterialTypeSolid,
            hasImageBackground: false));
        Assert.True(DeskBox.Helpers.WidgetTextShadow.ShouldSuppressForOpaquePlate(
            0.85,
            SettingsService.WidgetMaterialTypeMica,
            hasImageBackground: false));
        Assert.False(DeskBox.Helpers.WidgetTextShadow.ShouldSuppressForOpaquePlate(
            0.65,
            SettingsService.WidgetMaterialTypeMica,
            hasImageBackground: false));
        Assert.False(DeskBox.Helpers.WidgetTextShadow.ShouldSuppressForOpaquePlate(
            1.0,
            "None",
            hasImageBackground: false));
        Assert.False(DeskBox.Helpers.WidgetTextShadow.ShouldSuppressForOpaquePlate(
            1.0,
            SettingsService.WidgetMaterialTypeSolid,
            hasImageBackground: true));

        string markdownSource = Read("src/DeskBox/Controls/MarkdownDocumentView.cs");
        Assert.Contains("ClearShadowMirrorSnapshot();", markdownSource, StringComparison.Ordinal);
        Assert.Contains("WidgetTextShadow.IsActiveFor(_shadowMirror)", markdownSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave4_NativeAndXamlAmplifiers_DeduplicateResourcesDeferIdleVisualsAndDecoupleOutOfProcessSettings()
    {
        Assert.NotEqual(
            DeskBox.ViewModels.TodoAttachmentViewModel.TodoPreviewCacheScope,
            DeskBox.ViewModels.TodoAttachmentViewModel.QuickCapturePreviewCacheScope);

        string titleIconXaml = Read("src/DeskBox/Controls/WidgetTitleIcon.xaml");
        Assert.DoesNotContain("<UserControl.Resources>", titleIconXaml, StringComparison.Ordinal);

        string titleIconCode = Read("src/DeskBox/Controls/WidgetTitleIcon.xaml.cs");
        Assert.Contains("s_sharedMonoResources", titleIconCode, StringComparison.Ordinal);
        Assert.Contains("s_sharedColorSvgSources", titleIconCode, StringComparison.Ordinal);

        string fileItemXaml = Read("src/DeskBox/Controls/FileItemSurface.xaml");
        Assert.Contains("x:Name=\"IconActivityRing\"", fileItemXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ListActivityRing\"", fileItemXaml, StringComparison.Ordinal);
        Assert.Contains("x:Load=\"{x:Bind IsActivityActive, Mode=OneWay}\"", fileItemXaml, StringComparison.Ordinal);

        string shellXaml = Read("src/DeskBox/Controls/WidgetShell.xaml");
        Assert.Contains("x:Name=\"CompactPreviousButton\"\r\n                    x:Load=\"False\"",
            shellXaml.ReplaceLineEndings("\r\n"),
            StringComparison.Ordinal);

        string groupingSource = Read("src/DeskBox/Views/WidgetWindowBase.Grouping.cs");
        Assert.DoesNotContain("QueueGroupDetachPreviewWarmup(presentation);", groupingSource, StringComparison.Ordinal);

        string dropTargetSource = Read("src/DeskBox/Helpers/NativeDropTarget.cs");
        Assert.Contains("NativeDropImageManager.TryDragEnter(", dropTargetSource, StringComparison.Ordinal);

        string backdropSource = Read("src/DeskBox/Views/WidgetWindowBase.Backdrop.cs");
        Assert.Contains("AcquireOrConfigureSharedMicaSlot(", backdropSource, StringComparison.Ordinal);
        Assert.Contains("AcquireOrConfigureSharedAcrylicSlot(", backdropSource, StringComparison.Ordinal);

        string appSource = Read("src/DeskBox/App.xaml.cs");
        Assert.Contains("IsSettingsOpen: IsSettingsHostBlockingMemoryCleanup || GetVisibleSettingsWindowHandle() != IntPtr.Zero,", appSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave5_ColdStartActivation_DrainsStaleMarkersWithoutAutoSpawningSettingsHost()
    {
        string appSource = Read("src/DeskBox/App.xaml.cs");
        int start = appSource.IndexOf("private async Task CompleteExternalActivationInitializationAsync()", StringComparison.Ordinal);
        int end = appSource.IndexOf("private async Task DrainPendingForwardedActivationsOnlyAsync()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);

        string initMethod = appSource[start..end];
        int elseIdx = initMethod.IndexOf("else", StringComparison.Ordinal);
        Assert.True(elseIdx > 0);
        string elseBranch = initMethod[elseIdx..];
        Assert.Contains("DrainPendingNativeNotificationActivations();", elseBranch, StringComparison.Ordinal);
        Assert.Contains("await DrainPendingForwardedActivationsOnlyAsync();", elseBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("HandleExternalActivationAsync", elseBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave5_RuntimeGcDatasAndConserveMemory_AreConfiguredAcrossBothHostsAndCompactingReclaimer()
    {
        string mainProj = Read("src/DeskBox/DeskBox.csproj");
        string settingsProj = Read("src/DeskBox.Settings/DeskBox.Settings.csproj");
        Assert.Contains("<GarbageCollectionAdaptationMode>1</GarbageCollectionAdaptationMode>", mainProj, StringComparison.Ordinal);
        Assert.Contains("Include=\"System.GC.ConserveMemory\" Value=\"9\"", mainProj, StringComparison.Ordinal);
        Assert.Contains("<GarbageCollectionAdaptationMode>1</GarbageCollectionAdaptationMode>", settingsProj, StringComparison.Ordinal);
        Assert.Contains("Include=\"System.GC.ConserveMemory\" Value=\"9\"", settingsProj, StringComparison.Ordinal);

        string reclaimer = Read("src/DeskBox/Services/MemoryReclaimer.cs");
        Assert.Contains("GCLargeObjectHeapCompactionMode.CompactOnce", reclaimer, StringComparison.Ordinal);
        Assert.Contains("compacting: true", reclaimer, StringComparison.Ordinal);

        string quiescence = Read("src/DeskBox/App.QuiescenceWorkingSetTrim.cs");
        Assert.Contains("SchedulePostStartupMemorySettle()", quiescence, StringComparison.Ordinal);
        Assert.Contains("\"post-startup-settle\"", quiescence, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave5_CrossHeapCcwTeardown_UnregistersNativeCallbacksAndSeversVisualTreesOnDispose()
    {
        string markdownView = Read("src/DeskBox/Controls/MarkdownDocumentView.cs");
        Assert.Contains("UnregisterPropertyChangedCallback(FontSizeProperty, _fontSizeCallbackToken);", markdownView, StringComparison.Ordinal);
        Assert.Contains("UnregisterPropertyChangedCallback(ForegroundProperty, _foregroundCallbackToken);", markdownView, StringComparison.Ordinal);
        Assert.Contains("UnregisterPropertyChangedCallback(VisibilityProperty, _visibilityCallbackToken);", markdownView, StringComparison.Ordinal);

        string markdownEditor = Read("src/DeskBox/Controls/MarkdownSourceEditor.xaml.cs");
        Assert.Contains("internal void ReleaseVisualHandlers()", markdownEditor, StringComparison.Ordinal);
        Assert.Contains("Bindings.StopTracking();", markdownEditor, StringComparison.Ordinal);

        string inlineEditor = Read("src/DeskBox/Controls/WidgetInlineEditor.xaml.cs");
        Assert.Contains("EditorTextBoxElement.RemoveHandler(", inlineEditor, StringComparison.Ordinal);

        string todoContent = Read("src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs");
        Assert.Contains("UnregisterColorFilterHandledEvents();", todoContent, StringComparison.Ordinal);
        Assert.Contains("TodoInlineEditor.ReleaseVisualHandlers();", todoContent, StringComparison.Ordinal);
        Assert.Contains("ReleaseDetailNotesAndStepsVisualHandlers();", todoContent, StringComparison.Ordinal);
        Assert.Contains("Content = null;", todoContent, StringComparison.Ordinal);

        string quickCapture = Read("src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs");
        Assert.Contains("DetailMarkdownEditor.ReleaseVisualHandlers();", quickCapture, StringComparison.Ordinal);
        Assert.Contains("DetailMarkdownView.ReleaseVisualHandlers();", quickCapture, StringComparison.Ordinal);
        Assert.Contains("Content = null;", quickCapture, StringComparison.Ordinal);

        string fileSurface = Read("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        Assert.Contains("Root.RemoveHandler(UIElement.DragOverEvent, _rootHandledDragOverHandler);", fileSurface, StringComparison.Ordinal);
        Assert.Contains("ItemsGrid.ItemsSource = null;", fileSurface, StringComparison.Ordinal);
        Assert.Contains("Content = null;", fileSurface, StringComparison.Ordinal);

        string fileItemSurface = Read("src/DeskBox/Controls/FileItemSurface.xaml.cs");
        Assert.Contains("DetachPointerMovedHandler();", fileItemSurface, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave5_D3dkmtGpuSamplerAndStreamingJsonl_EliminatePdhAndArrayPoolRetention()
    {
        using var vramSampler = new CustomWidgetNativeMetrics.DxgiVramSampler();
        using var gpuSampler = new CustomWidgetNativeMetrics.GpuEngineCounterSampler();
        if (vramSampler.TrySampleVideoMemory(out _, out _, out _, out _) && vramSampler.HasAdapterLuid)
        {
            Assert.True(gpuSampler.TrySampleGpuPercent(true, vramSampler.AdapterLuid, out double firstPct));
            Assert.InRange(firstPct, 0.0, 100.0);
            Assert.True(gpuSampler.TrySampleGpuPercent(true, vramSampler.AdapterLuid, out double secondPct));
            Assert.InRange(secondPct, 0.0, 100.0);
        }

        string tempDir = Path.Combine(Path.GetTempPath(), "DeskBox.Wave5JsonlTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string claudePath = Path.Combine(tempDir, "claude.jsonl");
            string bigBlob = new('A', 96 * 1024);
            File.WriteAllText(
                claudePath,
                "{\"type\":\"user\",\"message\":{\"content\":\"" + bigBlob + "\"}}\n" +
                "{\"type\":\"assistant\",\"timestamp\":\"2026-10-31T01:00:00Z\",\"message\":{\"id\":\"msg_mega_1\",\"model\":\"claude-sonnet-4-5\",\"content\":[{\"type\":\"text\",\"text\":\"" + bigBlob + "\"}],\"usage\":{\"input_tokens\":1200,\"output_tokens\":350,\"cache_creation_input_tokens\":100,\"cache_read_input_tokens\":4000}}}\n" +
                "{\"type\":\"assistant\",\"timestamp\":\"2026-10-31T01:01:00Z\",\"message\":{\"id\":\"msg_norm_2\",\"model\":\"claude-opus-4-6\",\"usage\":{\"input_tokens\":500,\"output_tokens\":200,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0}}}\n");

            var claudeSamples = CustomWidgetAiMonitorEngine.ParseSingleClaudeJsonlFile(claudePath, DateTime.UtcNow);
            Assert.Equal(2, claudeSamples.Count);
            Assert.Contains(claudeSamples, s => s.InputTokens == 1200 && s.OutputTokens == 350 && s.CacheReadTokens == 4000);
            Assert.Contains(claudeSamples, s => s.InputTokens == 500 && s.OutputTokens == 200);

            string codexPath = Path.Combine(tempDir, "codex.jsonl");
            File.WriteAllText(
                codexPath,
                "{\"type\":\"response_item\",\"payload\":{\"output\":\"" + bigBlob + "\"}}\n" +
                "{\"timestamp\":\"2026-10-31T01:05:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":10000,\"cached_input_tokens\":6000,\"output_tokens\":800}},\"rate_limits\":{\"plan_type\":\"pro\",\"primary\":{\"used_percent\":42.5,\"resets_in_seconds\":3600}}}}\n");

            var codexSummary = CustomWidgetAiMonitorEngine.ParseSingleCodexJsonlFile(codexPath, DateTime.UtcNow, 1, 2);
            Assert.Equal(4000, codexSummary.InputTokens);
            Assert.Equal(6000, codexSummary.CachedInputTokens);
            Assert.Equal(800, codexSummary.OutputTokens);
            Assert.NotNull(codexSummary.LatestRateLimit);
            Assert.Equal("pro", codexSummary.LatestRateLimit!.PlanType);
            Assert.Equal(42.5, codexSummary.LatestRateLimit.PrimaryUsedPercent);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Wave5_ShellDeferredRealizationAndBoundedBackgrounds_StayLazyUntilNeeded()
    {
        string shellXaml = Read("src/DeskBox/Controls/WidgetShell.xaml").ReplaceLineEndings("\r\n");
        Assert.Contains("x:Name=\"GroupTitleSwitcher\"\r\n                    x:Load=\"False\"", shellXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FeedbackPresenter\"\r\n            x:Load=\"False\"", shellXaml, StringComparison.Ordinal);

        Assert.Equal(320, DeskBox.Helpers.WidgetBackgroundImageSourceFactory.ResolveBoundedDecodePixels(240));
        Assert.Equal(512, DeskBox.Helpers.WidgetBackgroundImageSourceFactory.ResolveBoundedDecodePixels(480));
        Assert.Equal(1280, DeskBox.Helpers.WidgetBackgroundImageSourceFactory.ResolveBoundedDecodePixels(2400));

        string glancePalette = Read("src/DeskBox/Services/GlanceImagePaletteService.cs");
        Assert.DoesNotContain("StorageFile.GetFileFromPathAsync", glancePalette, StringComparison.Ordinal);
        Assert.DoesNotContain("new List<(", glancePalette, StringComparison.Ordinal);

        byte[] twoColorPixels = new byte[16 * 4];
        for (int i = 0; i < 8; i++)
        {
            twoColorPixels[(i * 4) + 0] = 20;
            twoColorPixels[(i * 4) + 1] = 40;
            twoColorPixels[(i * 4) + 2] = 220;
            twoColorPixels[(i * 4) + 3] = 255;
        }
        for (int i = 8; i < 16; i++)
        {
            twoColorPixels[(i * 4) + 0] = 210;
            twoColorPixels[(i * 4) + 1] = 150;
            twoColorPixels[(i * 4) + 2] = 30;
            twoColorPixels[(i * 4) + 3] = 255;
        }
        GlanceImagePalette? palette = GlanceImagePaletteService.ExtractPalette(twoColorPixels);
        Assert.NotNull(palette);
    }

    [Fact]
    public void Wave6_ResidentHostLocalizationFilterAndCleanedKeys_DropPureSettingsUiKeysOnly()
    {
        Assert.True(LocalizationService.IsPureSettingsUiKey("Settings.Performance.Header"));
        Assert.True(LocalizationService.IsPureSettingsUiKey("Settings.About.Title"));
        Assert.False(LocalizationService.IsPureSettingsUiKey("Settings.WindowTitle"));
        Assert.False(LocalizationService.IsPureSettingsUiKey("Settings.DragDropPermission.MismatchWarning"));
        Assert.False(LocalizationService.IsPureSettingsUiKey("Settings.Hotkey.Conflict"));
        Assert.False(LocalizationService.IsPureSettingsUiKey("Settings.Update.AvailableTitle"));
        Assert.False(LocalizationService.IsPureSettingsUiKey("QuickCapture.Title"));

        string zhJson = Read("src/DeskBox/Strings/zh-CN.json");
        var dict = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(zhJson)!;
        Assert.DoesNotContain("QuickCapture.TabClipboard", dict.Keys);
        Assert.DoesNotContain("QuickCapture.ClearClipboardData", dict.Keys);
        int fullCount = dict.Count;

        LocalizationService.FilterDictionaryInPlace(dict);
        int dropped = fullCount - dict.Count;
        Assert.True(dropped >= 750, $"Expected >= 750 pure Settings-UI keys dropped, got {dropped}");
        Assert.Contains("Settings.WindowTitle", dict.Keys);
        Assert.Contains("QuickCapture.ClearDataDescriptionWithCount", dict.Keys);
        Assert.DoesNotContain("{1}", dict["QuickCapture.ClearDataDescriptionWithCount"], StringComparison.Ordinal);
    }

    [Fact]
    public void Wave6_ClaudeMegaLineStateMachineAndCityPinyinAndLunarLeap_HandleEdgeCasesAccurately()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "DeskBox.Wave6JsonlTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string claudePath = Path.Combine(tempDir, "claude_nested.jsonl");
            string bigToolOutput = "{\"usage\":{\"input_tokens\":999999,\"output_tokens\":888888}} " + new string('X', 80 * 1024);
            string escapedToolOutput = bigToolOutput.Replace("\"", "\\\"");
            string megaLine =
                "{\"type\":\"assistant\",\"timestamp\":\"2026-10-31T02:00:00Z\"," +
                "\"toolUseResult\":{\"usage\":{\"input_tokens\":777777,\"output_tokens\":666666}}," +
                "\"message\":{\"id\":\"msg_wave6_1\",\"model\":\"claude-sonnet-4-5\"," +
                "\"content\":[{\"type\":\"text\",\"text\":\"" + escapedToolOutput + "\"}]," +
                "\"usage\":{\"input_tokens\":1234,\"output_tokens\":567,\"cache_creation_input_tokens\":89,\"cache_read_input_tokens\":2000}}}\n";
            File.WriteAllText(claudePath, megaLine);

            var samples = CustomWidgetAiMonitorEngine.ParseSingleClaudeJsonlFile(claudePath, DateTime.UtcNow);
            Assert.Single(samples);
            Assert.Equal(1234, samples[0].InputTokens);
            Assert.Equal(567, samples[0].OutputTokens);
            Assert.Equal(89, samples[0].CacheWriteTokens);
            Assert.Equal(2000, samples[0].CacheReadTokens);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }

        Assert.True(CitySearchService.MatchesPinyinInitials("shijiazhuang", "sjz"));
        Assert.True(CitySearchService.MatchesPinyinInitials("haerbin", "heb"));
        Assert.True(CitySearchService.MatchesPinyinInitials("harbin", "heb"));
        Assert.True(CitySearchService.MatchesPinyinInitials("wulumuqi", "wlmq"));
        Assert.True(CitySearchService.MatchesPinyinInitials("huhehaote", "hhht"));
        Assert.False(CitySearchService.MatchesPinyinInitials("shanghai", "sjz"));

        Assert.Equal(6, CustomWidgetCountdownEngine.GetLunarLeapLogicalMonth(2025));

        string searchPopupSource = Read("src/DeskBox/Views/SearchPopupWindow.xaml.cs");
        Assert.Contains("ResultsRepeater.ElementClearing += OnResultsElementClearing;", searchPopupSource, StringComparison.Ordinal);
        Assert.Contains("RecommendedAppsRepeater.ElementClearing += OnRecommendedAppsElementClearing;", searchPopupSource, StringComparison.Ordinal);
        Assert.Contains("Close();", searchPopupSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Wave7_ExternalizedJsonPayloadsAndWinUiExExclusion_KeepAssemblyLeanAndStreamingFunctional()
    {
        string mainProj = Read("src/DeskBox/DeskBox.csproj");
        Assert.Contains("<Content Include=\"Assets\\Cities\\cities.json\">", mainProj, StringComparison.Ordinal);
        Assert.Contains("<Content Include=\"Strings\\zh-CN.json\">", mainProj, StringComparison.Ordinal);
        Assert.DoesNotContain("<EmbeddedResource Include=\"Assets\\Cities\\cities.json\"", mainProj, StringComparison.Ordinal);
        Assert.DoesNotContain("<EmbeddedResource Include=\"Strings\\", mainProj, StringComparison.Ordinal);

        string settingsProj = Read("src/DeskBox.Settings/DeskBox.Settings.csproj");
        Assert.Contains("<PackageReference Include=\"WinUIEx\" Version=\"2.9.3\" />", settingsProj, StringComparison.Ordinal);
        Assert.Contains("ExcludeUnusedWinUIExReferencesFromSettingsHost", settingsProj, StringComparison.Ordinal);
        Assert.Contains("ExcludeUnusedWinUIExReferencePathsFromSettingsHost", settingsProj, StringComparison.Ordinal);
        Assert.Contains("ExcludeUnusedWinUIExPriFromSettingsHost", settingsProj, StringComparison.Ordinal);

        var localization = TestServices.CreateLocalizationService();
        string windowTitle = localization.T("Settings.WindowTitle");
        Assert.False(string.IsNullOrWhiteSpace(windowTitle));
        Assert.NotEqual("Settings.WindowTitle", windowTitle);
        Assert.Equal("Wave7.NonExistentKey.ForFallback", localization.T("Wave7.NonExistentKey.ForFallback"));

        using var cityService = new CitySearchService();
        Assert.False(cityService.HasCachedCitiesForTesting);
        List<WeatherCitySearchResult> popular = cityService.GetGlobalPopularCities("zh", maxCount: 4);
        Assert.NotEmpty(popular);
        Assert.True(cityService.HasCachedCitiesForTesting);

        CitySearchService.ReleaseIdleCache(force: true);
        Assert.False(cityService.HasCachedCitiesForTesting);

        _ = cityService.GetGlobalPopularCities("en", maxCount: 2);
        Assert.True(cityService.HasCachedCitiesForTesting);
        cityService.Dispose();
        Assert.False(cityService.HasCachedCitiesForTesting);
    }

    [Fact]
    public void Wave7_MonitorOcclusionEvaluation_DistinguishesMaximizedFullscreenDesktopAndMultiMonitor()
    {
        IntPtr monPrimary = (IntPtr)0x1001;
        IntPtr monSecondary = (IntPtr)0x1002;
        var monitors = new Dictionary<IntPtr, MonitorOcclusionTracker.MonitorBounds>
        {
            [monPrimary] = new(monPrimary, 0, 0, 1920, 1080, 0, 0, 1920, 1040),
            [monSecondary] = new(monSecondary, 1920, 0, 3840, 1080, 1920, 0, 3840, 1040)
        };

        var candidates = new List<MonitorOcclusionTracker.WindowOcclusionCandidate>
        {
            new(
                (IntPtr)0x2001,
                monPrimary,
                IsVisible: true,
                IsMinimized: false,
                IsCloaked: false,
                IsZoomed: true,
                IsDeskBoxWindow: false,
                IsToolOverlay: false,
                "Chrome_WidgetWin_1",
                0,
                0,
                1920,
                1040),
            new(
                (IntPtr)0x2002,
                monSecondary,
                IsVisible: true,
                IsMinimized: false,
                IsCloaked: false,
                IsZoomed: false,
                IsDeskBoxWindow: false,
                IsToolOverlay: false,
                "Notepad",
                2100,
                120,
                2900,
                720)
        };

        HashSet<IntPtr> occluded = MonitorOcclusionTracker.EvaluateOccludedMonitors(
            monitors,
            candidates,
            foregroundClassName: "Chrome_WidgetWin_1",
            isForegroundDeskBoxWindow: false,
            isFullscreenAppActive: false,
            foregroundMonitorHandle: monPrimary);
        Assert.Single(occluded, monPrimary);
        Assert.DoesNotContain(monSecondary, occluded);

        var nearFullSecondary = new MonitorOcclusionTracker.WindowOcclusionCandidate(
            (IntPtr)0x2003,
            monSecondary,
            IsVisible: true,
            IsMinimized: false,
            IsCloaked: false,
            IsZoomed: false,
            IsDeskBoxWindow: false,
            IsToolOverlay: false,
            "ApplicationFrameWindow",
            1920,
            0,
            3840,
            1030);
        Assert.True(MonitorOcclusionTracker.DoesCandidateOccludeMonitor(nearFullSecondary, monitors[monSecondary]));

        Assert.Empty(MonitorOcclusionTracker.EvaluateOccludedMonitors(
            monitors,
            candidates,
            foregroundClassName: "WorkerW",
            isForegroundDeskBoxWindow: false,
            isFullscreenAppActive: false,
            foregroundMonitorHandle: monPrimary));
        Assert.Empty(MonitorOcclusionTracker.EvaluateOccludedMonitors(
            monitors,
            candidates,
            foregroundClassName: "Progman",
            isForegroundDeskBoxWindow: false,
            isFullscreenAppActive: false,
            foregroundMonitorHandle: monPrimary));
        Assert.Empty(MonitorOcclusionTracker.EvaluateOccludedMonitors(
            monitors,
            candidates,
            foregroundClassName: "WinUIDesktopWin32WindowClass",
            isForegroundDeskBoxWindow: true,
            isFullscreenAppActive: false,
            foregroundMonitorHandle: monPrimary));

        HashSet<IntPtr> fullscreenOccluded = MonitorOcclusionTracker.EvaluateOccludedMonitors(
            monitors,
            [],
            foregroundClassName: "UnrealWindow",
            isForegroundDeskBoxWindow: false,
            isFullscreenAppActive: true,
            foregroundMonitorHandle: monSecondary);
        Assert.Single(fullscreenOccluded, monSecondary);

        // Z-order awareness: when a DeskBox widget window is above a maximized
        // foreign window on the same monitor, the monitor is NOT occluded.
        MonitorOcclusionTracker.WindowOcclusionCandidate[] deskBoxAboveMaximized =
        [
            new(new IntPtr(301), monPrimary, IsVisible: true, IsMinimized: false, IsCloaked: false, IsZoomed: false, IsDeskBoxWindow: true, IsToolOverlay: false, "WinUIDesktopWin32WindowClass", 100, 100, 420, 320),
            new(new IntPtr(302), monPrimary, IsVisible: true, IsMinimized: false, IsCloaked: false, IsZoomed: true, IsDeskBoxWindow: false, IsToolOverlay: false, "Chrome_WidgetWin_1", 0, 0, 1920, 1040)
        ];
        Assert.Empty(MonitorOcclusionTracker.EvaluateOccludedMonitors(
            monitors,
            deskBoxAboveMaximized,
            foregroundClassName: "Chrome_WidgetWin_1",
            isForegroundDeskBoxWindow: false,
            isFullscreenAppActive: false,
            foregroundMonitorHandle: monPrimary));

        // Conversely, when the maximized foreign window is above the DeskBox
        // widget in Z-order, the monitor IS occluded.
        MonitorOcclusionTracker.WindowOcclusionCandidate[] maximizedAboveDeskBox =
        [
            new(new IntPtr(302), monPrimary, IsVisible: true, IsMinimized: false, IsCloaked: false, IsZoomed: true, IsDeskBoxWindow: false, IsToolOverlay: false, "Chrome_WidgetWin_1", 0, 0, 1920, 1040),
            new(new IntPtr(301), monPrimary, IsVisible: true, IsMinimized: false, IsCloaked: false, IsZoomed: false, IsDeskBoxWindow: true, IsToolOverlay: false, "WinUIDesktopWin32WindowClass", 100, 100, 420, 320)
        ];
        Assert.Single(
            MonitorOcclusionTracker.EvaluateOccludedMonitors(
                monitors,
                maximizedAboveDeskBox,
                foregroundClassName: "Chrome_WidgetWin_1",
                isForegroundDeskBoxWindow: false,
                isFullscreenAppActive: false,
                foregroundMonitorHandle: monPrimary),
            monPrimary);
    }

    [Fact]
    public async Task Wave7_SettingsLayoutDirtySplitAndBackupCacheAndLazyAutoOrgWatcher_EliminateIdleDiskAndHandleChurn()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox.Wave7RuntimeTests", Guid.NewGuid().ToString("N"));
        string appRoot = Path.Combine(tempRoot, "root");
        string dataDir = Path.Combine(appRoot, "data");
        string recoveryDir = Path.Combine(tempRoot, "recovery");
        Directory.CreateDirectory(dataDir);
        try
        {
            var settingsService = new SettingsService(dataDir);
            await settingsService.LoadAsync();
            settingsService.Settings.WidgetLayout.Widgets.Add(new WidgetConfig
            {
                Id = "wave7-todo",
                WidgetKind = WidgetKind.Todo,
                Name = "Todo",
                X = 120,
                Y = 100,
                Width = 320,
                Height = 260,
                IsVisible = true
            });
            Assert.True(await settingsService.SaveCheckedAsync(notifySubscribers: false));

            long settingsWritesBefore = settingsService.SettingsFileWriteCountForTesting;
            long layoutWritesBefore = settingsService.LayoutFileWriteCountForTesting;

            settingsService.Settings.WidgetLayout.Widgets[0].X = 280;
            settingsService.Settings.WidgetLayout.Widgets[0].Y = 200;
            await settingsService.SaveAsync(notifySubscribers: false);

            Assert.Equal(layoutWritesBefore + 1, settingsService.LayoutFileWriteCountForTesting);
            Assert.Equal(settingsWritesBefore, settingsService.SettingsFileWriteCountForTesting);

            settingsService.Settings.Theme = "Dark";
            Assert.True(await settingsService.SaveCheckedAsync(notifySubscribers: false));
            Assert.Equal(settingsWritesBefore + 1, settingsService.SettingsFileWriteCountForTesting);

            var backupService = new DeskBoxDataBackupService(appRoot, recoveryDir);
            string? snapshotPath = await backupService.CreateAutomaticSnapshotIfDueAsync();
            Assert.False(string.IsNullOrWhiteSpace(snapshotPath));
            Assert.True(File.Exists(snapshotPath!));

            int scansAfterCreate = backupService.AutomaticSnapshotDirectoryScanCountForTesting;
            Assert.Null(await backupService.CreateAutomaticSnapshotIfDueAsync());
            Assert.Null(await backupService.CreateAutomaticSnapshotIfDueAsync());
            Assert.Null(await backupService.CreateAutomaticSnapshotIfDueAsync());
            Assert.Equal(scansAfterCreate, backupService.AutomaticSnapshotDirectoryScanCountForTesting);

            string fakeDesktopDir = Path.Combine(tempRoot, "nonexistent-desktop");
            using var autoOrgWatcher = new DesktopAutoOrganizationWatcher(
                settingsService,
                organizerService: null!,
                widgetManager: null!,
                desktopPathProvider: () => fakeDesktopDir);
            autoOrgWatcher.Start();
            Assert.False(autoOrgWatcher.IsWatcherAllocatedForTesting);
            Assert.False(autoOrgWatcher.IsRetryPumpActiveForTesting);
            Assert.False(Directory.Exists(fakeDesktopDir));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }

        Assert.Equal(TimeSpan.FromSeconds(5), HookWatchdogPolicy.Default.TickInterval);

        string wmMemorySource = Read("src/DeskBox/Services/WidgetManager.Memory.cs");
        Assert.Contains("ApplyMonitorOcclusionState(", wmMemorySource, StringComparison.Ordinal);
        Assert.DoesNotContain("loadedWindows.Any(window => window.Visible", wmMemorySource, StringComparison.Ordinal);

        string appSource = Read("src/DeskBox/App.xaml.cs");
        int idleTickStart = appSource.IndexOf("private void VisibleIdleMemoryMaintenanceTimer_Tick(", StringComparison.Ordinal);
        int idleTickEnd = appSource.IndexOf("private bool ConfigureVisibleIdleMemoryTracker(", idleTickStart, StringComparison.Ordinal);
        Assert.True(idleTickStart >= 0 && idleTickEnd > idleTickStart);
        Assert.DoesNotContain("Win32Helper.TrimWorkingSet();", appSource[idleTickStart..idleTickEnd], StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
