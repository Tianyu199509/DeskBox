using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Xunit;

namespace DeskBox.Tests;

public sealed class SystemPulseRecycleBinAndSearchTests
{
    [Fact]
    public async Task SystemPulseTemplate_RaisesSparklinesBy50Percent_Preserves564Height_AndUpgradesLegacyModuleSettings()
    {
        CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.SystemPulseTemplateId);
        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(bundle.ManifestJson);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));

        CustomWidgetManifest manifest = parsed.Manifest!.WithLocales(bundle.Locales);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal(344, manifest.DefaultSize.Width);
        Assert.Equal(564, manifest.DefaultSize.Height);

        CustomWidgetNodeSpec moduleStack = Assert.Single(manifest.View.Children);
        Assert.Equal("ModuleStack", moduleStack.Type);
        Assert.Equal("utility", moduleStack.PinTrailingGroup);

        CustomWidgetNodeSpec cpuCard = Assert.Single(moduleStack.Children, c => c.Id == "cpu");
        CustomWidgetNodeSpec memCard = Assert.Single(moduleStack.Children, c => c.Id == "memory");
        CustomWidgetNodeSpec gpuCard = Assert.Single(moduleStack.Children, c => c.Id == "gpu");
        CustomWidgetNodeSpec binNode = Assert.Single(moduleStack.Children, c => c.Id == "recycleBin");
        CustomWidgetNodeSpec searchNode = Assert.Single(moduleStack.Children, c => c.Id == "search");

        Assert.Equal(42, Assert.Single(cpuCard.Children, c => c.Type == "Sparkline").Height);
        Assert.Equal(42, Assert.Single(memCard.Children, c => c.Type == "Sparkline").Height);
        Assert.Equal(40, Assert.Single(gpuCard.Children, c => c.Type == "Sparkline").Height);

        Assert.Equal("RecycleBin", binNode.Type);
        Assert.Equal("utility", binNode.PairGroup);
        Assert.Equal("*", binNode.PairWidth);
        Assert.Equal("utility", searchNode.PairGroup);
        Assert.Equal("Auto", searchNode.PairWidth);

        // Existing user's saved v4.1.0 module list must automatically append recycleBin & search as enabled.
        CustomWidgetSettingDefinition modulesDef = Assert.Single(manifest.Settings, s => s.Key == "modules");
        IReadOnlyList<CustomWidgetModuleEntry> migrated = CustomWidgetModuleListHelper.ParseEntries(
            "cpu:1,memory:1,gpu:1,processes:1,network:1,storage:1",
            modulesDef.Options);
        Assert.Contains(migrated, e => e.Id == "recycleBin" && e.Enabled);
        Assert.Contains(migrated, e => e.Id == "search" && e.Enabled);

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-syspulse-bin-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new CustomWidgetDataStore("syspulse-bin-check", tempDir);
            await store.WriteWorkspaceBundleAsync(bundle.ManifestJson, bundle.LogicJs, locales: bundle.Locales);
            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var locSvc = new LocalizationService(settingsSvc);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "syspulse-bin-check", Name = "System Monitor" },
                locSvc,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vm.InitializeAsync();

            Assert.True(vm.HasRecycleBinDropTarget);
            Assert.False(vm.AcceptsFileDrops);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ModuleStackRowPlanner_PairsUtilityAsLeftWideRightAuto_PreventsCrossGroupPairing_AndPinsTrailingGroup()
    {
        CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.SystemPulseTemplateId);
        CustomWidgetManifest manifest = CustomWidgetManifestParser.Parse(bundle.ManifestJson).Manifest!;
        CustomWidgetNodeSpec moduleStack = Assert.Single(manifest.View.Children);
        var byId = moduleStack.Children.ToDictionary(c => c.Id!, StringComparer.OrdinalIgnoreCase);

        // 1. Default order: recycleBin (*) + search (Auto) paired at end and pinned to footer
        List<(string Id, CustomWidgetNodeSpec Node)> defaultEnabled =
            moduleStack.Children.Select(c => (c.Id!, c)).ToList();
        var defaultPlans = CustomWidgetNativeRenderer.PlanModuleStackRows(
            defaultEnabled,
            moduleStack.PinTrailingGroup,
            isRoot: true,
            out bool defaultPinned);
        Assert.True(defaultPinned);
        var lastRow = defaultPlans[^1];
        Assert.True(lastRow.IsPair);
        Assert.Equal(GridUnitType.Star, lastRow.LeftWidth.GridUnitType);
        Assert.Equal(GridUnitType.Auto, lastRow.RightWidth.GridUnitType);

        // 2. Swapping search before recycleBin flips column widths to Auto, * while remaining pinned
        List<(string Id, CustomWidgetNodeSpec Node)> swapped =
        [
            ("cpu", byId["cpu"]),
            ("search", byId["search"]),
            ("recycleBin", byId["recycleBin"])
        ];
        var swappedPlans = CustomWidgetNativeRenderer.PlanModuleStackRows(
            swapped,
            moduleStack.PinTrailingGroup,
            isRoot: true,
            out bool swappedPinned);
        Assert.True(swappedPinned);
        Assert.Equal(2, swappedPlans.Count);
        // CPU ("metrics") must NOT accidentally pair with Search ("utility")!
        Assert.False(swappedPlans[0].IsPair);
        Assert.True(swappedPlans[1].IsPair);
        Assert.Equal(GridUnitType.Auto, swappedPlans[1].LeftWidth.GridUnitType);
        Assert.Equal(GridUnitType.Star, swappedPlans[1].RightWidth.GridUnitType);

        // 3. Disabling recycleBin leaves search solo (full-width *) and still pinned when trailing
        List<(string Id, CustomWidgetNodeSpec Node)> soloSearch =
        [
            ("cpu", byId["cpu"]),
            ("search", byId["search"])
        ];
        var soloPlans = CustomWidgetNativeRenderer.PlanModuleStackRows(
            soloSearch,
            moduleStack.PinTrailingGroup,
            isRoot: true,
            out bool soloPinned);
        Assert.True(soloPinned);
        Assert.False(soloPlans[^1].IsPair);
        Assert.Equal(GridUnitType.Star, soloPlans[^1].LeftWidth.GridUnitType);

        // 4. Moving utility row to the top unpins footer so it flows naturally at index 0
        List<(string Id, CustomWidgetNodeSpec Node)> movedTop =
        [
            ("recycleBin", byId["recycleBin"]),
            ("search", byId["search"]),
            ("gpu", byId["gpu"])
        ];
        var topPlans = CustomWidgetNativeRenderer.PlanModuleStackRows(
            movedTop,
            moduleStack.PinTrailingGroup,
            isRoot: true,
            out bool topPinned);
        Assert.False(topPinned);
        Assert.True(topPlans[0].IsPair);
    }

    [Fact]
    public async Task DeskBoxSearchAction_InvokesSearchPopupCallback_AndResolvesSearchShortcutAndSlotPairedTokens()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-search-act-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.SystemPulseTemplateId);
            var store = new CustomWidgetDataStore("syspulse-search-test", tempDir);
            await store.WriteWorkspaceBundleAsync(bundle.ManifestJson, bundle.LogicJs, locales: bundle.Locales);

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings.json"));
            settingsSvc.Settings.Search.SearchHotkeyEnabled = true;
            settingsSvc.Settings.Search.SearchHotkeyActivationKind = HotkeyActivationKind.Chord;
            settingsSvc.Settings.Search.SearchHotkeyModifiers = (int)HotkeyModifierKeys.Alt;
            settingsSvc.Settings.Search.SearchHotkeyKey = 0x20; // Space

            var locSvc = new LocalizationService(settingsSvc);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "syspulse-search-test", Name = "System Monitor" },
                locSvc,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vm.InitializeAsync();

            int openCount = 0;
            string? capturedQuery = "uninitialized";
            vm.OpenSearchPopupOverride = q =>
            {
                openCount++;
                capturedQuery = q;
            };

            CustomWidgetEvaluationContext soloCtx = vm.CreateEvaluationContext();
            Assert.NotEmpty(soloCtx.SearchShortcutText);
            Assert.Equal(
                soloCtx.SearchShortcutText,
                CustomWidgetExpressionEvaluator.Interpolate("${$sys.searchShortcut}", soloCtx));
            Assert.Equal(
                "false",
                CustomWidgetExpressionEvaluator.Interpolate("${$slot.paired}", soloCtx));
            Assert.Equal(
                "true",
                CustomWidgetExpressionEvaluator.Interpolate("${$slot.paired}", soloCtx.WithPairedSlot(true)));

            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("deskbox.search")]);
            Assert.Equal(1, openCount);
            Assert.Null(capturedQuery);

            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("app.openSearch", Text: "invoice.pdf")]);
            Assert.Equal(2, openCount);
            Assert.Equal("invoice.pdf", capturedQuery);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task RecycleBinService_RecyclesDroppedFilesWithOneClickUndoToast_AndBlocksNonRecyclableVolumes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-recycle-undo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        Func<string, IntPtr, Task<bool>>? prevRecycle = CustomWidgetHostActionBridge.RecycleEntryHandler;
        Func<string, string, bool>? prevRestore = CustomWidgetHostActionBridge.RestoreEntryHandler;
        Func<DesktopToastRequest, bool>? prevToast = CustomWidgetHostActionBridge.ShowDesktopToastHandler;
        Func<string, bool>? prevCanRecycle = CustomWidgetHostActionBridge.CanRecyclePathOverride;

        try
        {
            string fileA = Path.Combine(tempDir, "draft-notes.txt");
            File.WriteAllText(fileA, "hello");

            var deletedPaths = new List<string>();
            var restoredPairs = new List<(string Parent, string Name)>();
            DesktopToastRequest? capturedToast = null;

            CustomWidgetHostActionBridge.CanRecyclePathOverride = static _ => true;
            CustomWidgetHostActionBridge.RecycleEntryHandler = (path, _) =>
            {
                deletedPaths.Add(path);
                File.Delete(path);
                return Task.FromResult(true);
            };
            CustomWidgetHostActionBridge.RestoreEntryHandler = (parent, name) =>
            {
                restoredPairs.Add((parent, name));
                File.WriteAllText(Path.Combine(parent, name), "restored");
                return true;
            };
            CustomWidgetHostActionBridge.ShowDesktopToastHandler = req =>
            {
                capturedToast = req;
                return true;
            };

            CustomWidgetRecycleBatchOutcome outcome = await CustomWidgetRecycleBinService.Shared
                .RecycleDroppedFilesWithUndoAsync([fileA], IntPtr.Zero, "zh-CN");

            Assert.Equal(1, outcome.RecycledCount);
            Assert.Equal(0, outcome.BlockedCount);
            Assert.False(File.Exists(fileA));
            Assert.NotNull(capturedToast);
            Assert.Contains("draft-notes.txt", capturedToast!.Message, StringComparison.Ordinal);
            Assert.NotNull(capturedToast.OnActionAsync);

            // Triggering the Undo button on the toast HUD restores the recycled file!
            await capturedToast.OnActionAsync!();
            Assert.Single(restoredPairs);
            Assert.Equal("draft-notes.txt", restoredPairs[0].Name);
            Assert.True(File.Exists(fileA));

            // Non-recyclable volume must be blocked without invoking RecycleEntryHandler
            string fileB = Path.Combine(tempDir, "network-share-item.txt");
            File.WriteAllText(fileB, "keep safe");
            deletedPaths.Clear();
            capturedToast = null;
            CustomWidgetHostActionBridge.CanRecyclePathOverride = static _ => false;

            CustomWidgetRecycleBatchOutcome blockedOutcome = await CustomWidgetRecycleBinService.Shared
                .RecycleDroppedFilesWithUndoAsync([fileB], IntPtr.Zero, "zh-CN");

            Assert.Equal(0, blockedOutcome.RecycledCount);
            Assert.Equal(1, blockedOutcome.BlockedCount);
            Assert.Empty(deletedPaths);
            Assert.True(File.Exists(fileB));
            Assert.NotNull(capturedToast);
            Assert.Equal(DesktopToastPriority.SafetyWarning, capturedToast!.Priority);
        }
        finally
        {
            CustomWidgetHostActionBridge.RecycleEntryHandler = prevRecycle;
            CustomWidgetHostActionBridge.RestoreEntryHandler = prevRestore;
            CustomWidgetHostActionBridge.ShowDesktopToastHandler = prevToast;
            CustomWidgetHostActionBridge.CanRecyclePathOverride = prevCanRecycle;
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
