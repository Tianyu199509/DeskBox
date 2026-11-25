using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;

namespace DeskBox.Tests;

public sealed class Post160FeedbackRegressionTests
{
    [Fact]
    public void NativeAotWinRtApis_NeverPassCollectionExpressionsSynthesizingReadOnlySingleElementList()
    {
        // GH#656 / #1278: Roslyn compiles single-element collection expressions ([item])
        // targeting IEnumerable<T> into <>z__ReadOnlySingleElementList<T>, which lacks a
        // static CsWinRT IIterable<T> CCW entry under PublishAot=true and throws InvalidCastException.
        string clipboardSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/ClipboardHistoryService.cs"));
        string fileServiceSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/FileService.cs"));
        string calendarSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/GlanceTraditionalCalendarService.cs"));

        Assert.DoesNotContain("SetStorageItems([", clipboardSource, StringComparison.Ordinal);
        Assert.Contains("SetStorageItems(new Windows.Storage.IStorageItem[]", clipboardSource, StringComparison.Ordinal);

        Assert.DoesNotContain("RetrievePropertiesAsync([", fileServiceSource, StringComparison.Ordinal);
        Assert.Contains("RetrievePropertiesAsync(s_systemKindPropertyNames)", fileServiceSource, StringComparison.Ordinal);

        Assert.DoesNotContain("new WinCalendar([", calendarSource, StringComparison.Ordinal);
        Assert.Contains("new WinCalendar(new string[]", calendarSource, StringComparison.Ordinal);
    }

    [Fact]
    public void CanTrustPointerOwnership_RejectsPeerDeskBoxOcclusionEvenWithRecentRoutedEvidence()
    {
        // #1264 / #1268: An upper expanded capsule occluding a lower collapsed capsule must
        // block hover ownership even while the 860ms routed pointer authority window is active.
        Assert.False(WidgetCompactInteractionPolicy.CanTrustPointerOwnership(
            isPointerPhysicallyInside: true,
            nativeRootCanReceivePointer: false,
            hasRecentRoutedPointerEvidence: true,
            isOccludedByPeerDeskBoxWindow: true));

        Assert.True(WidgetCompactInteractionPolicy.CanTrustPointerOwnership(
            isPointerPhysicallyInside: true,
            nativeRootCanReceivePointer: false,
            hasRecentRoutedPointerEvidence: true,
            isOccludedByPeerDeskBoxWindow: false));
    }

    [Fact]
    public void ShortcutWorkingDirectoryRepair_StripsSurroundingQuotesBeforeClassification()
    {
        // GH#663: Windows shell links frequently store quoted WorkingDirectory values for paths
        // with spaces (e.g. "\"C:\\Removed Folder\\bin\"").
        var brokenQuoted = new ShortcutInfo(
            TargetPath: "\"C:\\Windows\\System32\\notepad.exe\"",
            Description: string.Empty,
            Arguments: string.Empty,
            WorkingDirectory: "\"C:\\DeskBox_NonExistent_Quoted_Dir_663\\bin\"",
            IconLocation: string.Empty,
            IconIndex: 0);

        Assert.True(FileService.HasBrokenShortcutWorkingDirectory(brokenQuoted));
    }

    [Fact]
    public async Task HookHealthWatchdog_SkipsCanaryProbeWhenForegroundIsUipiBlocked()
    {
        // #1283 / #1286: UIPI legitimately blocks WH_KEYBOARD_LL/WH_MOUSE_LL and SendInput
        // echoes while an elevated window owns the foreground.
        int probeCount = 0;
        int recoverCount = 0;
        var target = new FakeHookTarget
        {
            OnProbeAsync = _ =>
            {
                probeCount++;
                return Task.FromResult(false);
            },
            OnRecover = () => recoverCount++
        };

        using var watchdog = new HookHealthWatchdog(
            dispatcherQueue: null,
            log: _ => { });
        watchdog.Watch(() => target);

        await watchdog.RunCycleOnceAsync(
            inputKnown: true,
            lastInputTick: 20_000,
            nowTicks: 21_000,
            foregroundUipiBlocked: true);

        Assert.Equal(0, probeCount);
        Assert.Equal(0, recoverCount);
    }

    [Fact]
    public async Task TodoAllFilter_MoveItemAsync_ReordersActiveAndCompletedPartitionsIndependently()
    {
        // #1275 / #1301: When an earlier item in Items is completed (index 0) and active items
        // sit after it in Items, dragging the top active item below the last active item in
        // TodoFilter.All must not anchor on the completed item at index 0 and bounce back.
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxTodo1275_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var store = new TodoWidgetStore(tempRoot, "todo-1275");
            var settingsService = new SettingsService();
            settingsService.Settings.TodoShowCompletedTasks = true;
            var localization = new LocalizationService(settingsService);
            var config = new WidgetConfig
            {
                Id = "todo-1275",
                Name = "Todo",
                WidgetKind = WidgetKind.Todo
            };
            var vm = new TodoWidgetViewModel(store, localization, config, settingsService);
            await vm.InitializeAsync();

            TodoItemViewModel? c = await vm.AddItemAsync("Task C");
            TodoItemViewModel? b = await vm.AddItemAsync("Task B");
            TodoItemViewModel? a = await vm.AddItemAsync("Task A");
            Assert.NotNull(c);
            Assert.NotNull(b);
            Assert.NotNull(a);

            // Complete C so Completed sits in VisibleItems after Active [A, B], while C was created first.
            await vm.SetCompletedAsync(c.Id, true);
            vm.SetFilter(TodoFilter.All);

            string[] initialOrder = vm.VisibleItems.Select(x => x.Text).ToArray();
            Assert.Equal(new[] { "Task A", "Task B", "Task C" }, initialOrder);

            // Drag first active item ("Task A") below last active item ("Task B") -> targetVisibleIndex = 1.
            Assert.True(await vm.MoveItemAsync(a.Id, targetVisibleIndex: 1));

            string[] reorderedActive = vm.VisibleItems.Select(x => x.Text).ToArray();
            Assert.Equal(new[] { "Task B", "Task A", "Task C" }, reorderedActive);

            // Also verify completed partition manual reorder persists instead of snapping back by CompletedAt.
            TodoItemViewModel? d = await vm.AddItemAsync("Task D");
            Assert.NotNull(d);
            await Task.Delay(15);
            await vm.SetCompletedAsync(d.Id, true);
            // Newly completed D lands at top of completed section: [Task B, Task A, Task D, Task C].
            Assert.Equal(new[] { "Task B", "Task A", "Task D", "Task C" }, vm.VisibleItems.Select(x => x.Text).ToArray());

            // Move D below C (targetVisibleIndex = 3 after removing D from [B, A, D, C] -> [B, A, C]).
            Assert.True(await vm.MoveItemAsync(d.Id, targetVisibleIndex: 3));
            Assert.Equal(new[] { "Task B", "Task A", "Task C", "Task D" }, vm.VisibleItems.Select(x => x.Text).ToArray());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TrayHideAndLayerClear_NeverDefaultShowWindowTrueOnHiddenWidgets()
    {
        // #1261: Deferred compact layer restore and ClearTopMost must never pass SWP_SHOWWINDOW
        // on a hidden widget 27ms after batch hide completes.
        string collapse = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Collapse.cs"));
        string layerService = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetLayerService.cs"));

        int prepareStart = collapse.IndexOf("protected void PrepareCompactHostForTrayHide()", StringComparison.Ordinal);
        int prepareGuard = collapse.IndexOf("if (!_collapseInitialized", prepareStart, StringComparison.Ordinal);
        int cancelInPrepare = collapse.IndexOf("CancelDeferredExpandedLayerRestore();", prepareStart, StringComparison.Ordinal);
        Assert.InRange(cancelInPrepare, prepareStart, prepareGuard);

        int restoreStart = collapse.IndexOf("private void RestoreLayerAfterExpandedState()", StringComparison.Ordinal);
        int restoreEnd = collapse.IndexOf("private void ScheduleSmartCollapse", restoreStart, StringComparison.Ordinal);
        string restoreBody = collapse[restoreStart..restoreEnd];
        Assert.Contains("WidgetLayerService.MoveToDesktopBottom(HWnd, showWindow: false);", restoreBody, StringComparison.Ordinal);

        int clearStart = layerService.IndexOf("public static void ClearTopMost(IntPtr windowHandle)", StringComparison.Ordinal);
        int clearEnd = layerService.IndexOf("public static void HoldTemporaryTopMost(", clearStart, StringComparison.Ordinal);
        string clearBody = layerService[clearStart..clearEnd];
        Assert.Contains("showWindow: false", clearBody, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveToDesktopBottom(windowHandle);", clearBody, StringComparison.Ordinal);
    }

    [Fact]
    public void StorePackagedOpenTargetResolution_PreservesNonAppDataJunctionsAndFiltersMissingWorkingDirectory()
    {
        // GH#663 + Store MSIX FL-01: Packaged Store launch must not dereference user directory
        // junctions outside %LocalAppData%/%AppData%, while stripping quoted/deleted working dirs.
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBox_StoreLaunch_" + Guid.NewGuid().ToString("N"));
        string realDir = Path.Combine(tempRoot, "real");
        string realFile = Path.Combine(realDir, "demo.txt");
        Directory.CreateDirectory(realDir);
        File.WriteAllText(realFile, "hello");
        try
        {
            string resolvedOpen = Win32Helper.ResolveEffectiveOpenTargetAndDirectory(
                realFile,
                out string openDir,
                isPackagedOverride: true);
            Assert.Equal(realFile, resolvedOpen, ignoreCase: true);
            Assert.Equal(realDir, openDir, ignoreCase: true);

            string missingWorkDir = $"\"{Path.Combine(tempRoot, "deleted_dir")}\"";
            string resolvedLocal = Win32Helper.ResolveEffectiveLocalLaunchPaths(
                realFile,
                missingWorkDir,
                out string? effectiveWorkDir,
                isPackagedOverride: true);
            Assert.Equal(realFile, resolvedLocal, ignoreCase: true);
            Assert.Null(effectiveWorkDir);

            string quotedExistingDir = $"\"{realDir}\"";
            _ = Win32Helper.ResolveEffectiveLocalLaunchPaths(
                realFile,
                quotedExistingDir,
                out string? validWorkDir,
                isPackagedOverride: true);
            Assert.Equal(realDir, validWorkDir, ignoreCase: true);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CustomWidgetRebuildNativeTree_AllowsInPlaceRetainedPatchBeforeInputFlyoutDeferral()
    {
        // GH#664: Typing in <input> holds HasOpenFlyout=true, which must only block destructive
        // full-root replacement AFTER TryPatchRetainedTree() has had a chance to update live siblings.
        string contentSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/CustomWidgetContent.xaml.cs"));
        int rebuildStart = contentSource.IndexOf("public void RebuildNativeTree(bool forceFullRebuild = false)", StringComparison.Ordinal);
        int rebuildEnd = contentSource.IndexOf("private bool CanPatchExistingRootInPlace()", rebuildStart, StringComparison.Ordinal);
        string body = contentSource[rebuildStart..rebuildEnd];

        int patchCall = body.IndexOf("CanPatchExistingRootInPlace() && _renderer.TryPatchRetainedTree()", StringComparison.Ordinal);
        int inputFlyoutGuard = body.IndexOf("_renderer.HasOpenFlyout || DeskBox.Helpers.ImeCompositionGuard.IsComposingOrWithinGrace()", StringComparison.Ordinal);
        int buildRootCall = body.IndexOf("_renderer.BuildRootElement()", StringComparison.Ordinal);

        Assert.True(patchCall > 0);
        Assert.InRange(inputFlyoutGuard, patchCall, buildRootCall);
    }

    private sealed class FakeHookTarget : IHookHealthProbeTarget
    {
        public string ProbeName => "FakeHook";
        public bool HookProbeWanted => true;
        public bool HookConfirmedDead => false;
        public long LastHookCallbackTicks => 1_000;
        public required Func<int, Task<bool>> OnProbeAsync { get; init; }
        public required Action OnRecover { get; init; }

        public Task<bool> ProbeHookAliveAsync(int timeoutMs) => OnProbeAsync(timeoutMs);
        public void RecoverHook() => OnRecover();
    }
}
