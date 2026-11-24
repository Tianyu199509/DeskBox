using DeskBox.Core.Persistence;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Views;

namespace DeskBox.Tests;

public sealed class Feedback1253To1259RegressionTests : IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBoxFeedback1253To1259Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        App.TestLogFilePathOverride = null;
        ImeCompositionGuard.ResetForTest();
        if (Directory.Exists(_temporaryRoot))
        {
            try
            {
                Directory.Delete(_temporaryRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Theory]
    [InlineData("todo-quick-add", true)]
    [InlineData("widget-title-rename", true)]
    [InlineData("file-inline-rename", true)]
    [InlineData("routed-pointer", false)]
    public void DesktopPinnedKeyboardActivation_RecognizesWidgetTitleAndFileInlineRenameReasons(
        string reason,
        bool expected)
    {
        Assert.Equal(expected, WidgetWindowBase.IsDesktopPinnedKeyboardActivationReason(reason));
    }

    [Fact]
    public void TransientInputFocusGuard_ClassifiesInputSiteAndGuardsActiveIslandFocus()
    {
        Assert.True(TransientInputFocusGuard.IsInputSiteWindowClass("InputSiteWindowClass"));
        Assert.True(TransientInputFocusGuard.IsInputSiteWindowClass("Microsoft.UI.Input.InputSite.WindowClass"));
        Assert.False(TransientInputFocusGuard.IsInputSiteWindowClass("Microsoft.UI.Content.DesktopChildSiteBridge"));
        Assert.False(TransientInputFocusGuard.IsInputSiteWindowClass("Windows.UI.Composition.DesktopWindowContentBridge"));
        Assert.False(TransientInputFocusGuard.IsFocusInsideContentIsland(IntPtr.Zero));

        string guardSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Helpers/TransientInputFocusGuard.cs"));
        string focusIslandBody = SliceBetween(
            guardSource,
            "public static void FocusContentIsland(",
            "public static bool IsFocusInsideContentIsland(");
        Assert.Contains("IsFocusInsideContentIsland(windowHandle)", focusIslandBody, StringComparison.Ordinal);

        string activateBody = SliceBetween(
            guardSource,
            "public static void ActivateWindowAndFocusIsland(",
            "public static void SetActiveAndFocusIsland(");
        Assert.Contains("Win32Helper.GetForegroundWindow() != windowHandle", activateBody, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.SetForegroundWindow(windowHandle)", activateBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ActivateDesktopPinnedWindow_HoldsReentrancyGuardUntilExit_AndDefersDynamicKeyboardActivation()
    {
        string boundsSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string activatePinned = SliceBetween(
            boundsSource,
            "private void ActivateDesktopPinnedWindow(string reason)",
            "private static bool IsDesktopShellRootWindow(");

        int rebedPos = activatePinned.IndexOf("RebedPinnedSiblingsAfterActivation(", StringComparison.Ordinal);
        int guardResetPos = activatePinned.IndexOf("_desktopPinnedInputActivationInProgress = false;", StringComparison.Ordinal);
        Assert.True(rebedPos > 0 && guardResetPos > rebedPos);
        Assert.DoesNotContain("FocusContentIslandIfForeground(HWnd)", activatePinned, StringComparison.Ordinal);

        string activateKeyboard = SliceBetween(
            boundsSource,
            "internal void ActivateForKeyboardInput(",
            "private void DeferDynamicKeyboardActivation(");
        Assert.Contains("DeferDynamicKeyboardActivation(reason);", activateKeyboard, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivateWindowAndFocusIsland(", activateKeyboard, StringComparison.Ordinal);

        string deferDynamic = SliceBetween(
            boundsSource,
            "private void DeferDynamicKeyboardActivation(string reason)",
            "private void FocusManager_GotFocusForDesktopPinnedLayer(");
        int detachPos = deferDynamic.IndexOf("WidgetLayerService.DetachSingleDynamicWidgetForInteraction(HWnd);", StringComparison.Ordinal);
        int activateIslandPos = deferDynamic.IndexOf("TransientInputFocusGuard.ActivateWindowAndFocusIsland(", StringComparison.Ordinal);
        Assert.True(detachPos > 0 && activateIslandPos > detachPos);

        string quickAddSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/Todo/TodoQuickAddBar.xaml.cs"));
        string openComposer = SliceBetween(
            quickAddSource,
            "public void OpenComposer(bool focus = true)",
            "public void CloseComposer(bool clearDraft)");
        int enqueuePos = openComposer.IndexOf("dispatcher.TryEnqueue(", StringComparison.Ordinal);
        int activateOwnerPos = openComposer.IndexOf("TryActivateOwnerForKeyboardInput(this, \"todo-quick-add\")", StringComparison.Ordinal);
        Assert.True(activateOwnerPos > 0 && enqueuePos > activateOwnerPos);
    }

    [Fact]
    public void WidgetTitleAndFileInlineRename_AvoidPostFocusIslandClobberAndSurfaceFocusSteal()
    {
        string commandsSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        string startRename = SliceBetween(
            commandsSource,
            "private void StartTitleRename()",
            "private TextBox CreateTitleRenameEditor()");
        Assert.Contains("ActivateForKeyboardInput(\"widget-title-rename\", expectTextInput: true);", startRename, StringComparison.Ordinal);
        Assert.DoesNotContain("SetActiveAndFocusIsland(", startRename, StringComparison.Ordinal);

        string fileSurfaceSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));
        string fileActivated = SliceBetween(
            fileSurfaceSource,
            "internal void OnActivated()",
            "internal void OnDeactivated()");
        Assert.Contains("HasActiveEditorOrShellTitleRename(this)", fileActivated, StringComparison.Ordinal);

        string quickCaptureSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs"));
        string quickActivated = SliceBetween(
            quickCaptureSource,
            "internal void OnActivated()",
            "internal async void OnDeactivated()");
        Assert.Contains("IsHostingShellEditingTitle(this)", quickActivated, StringComparison.Ordinal);

        string contentWindowSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        string applySettings = SliceBetween(
            contentWindowSource,
            "private void ApplySettingsChanged(bool appearanceOnly)",
            "private static Windows.UI.Color WithAlpha(");
        Assert.Contains("_titleViewModel.RefreshDisplayName();", applySettings, StringComparison.Ordinal);

        string imeGuardSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Helpers/ImeCompositionGuard.cs"));
        Assert.Contains("tb.LostFocus += Control_LostFocus;", imeGuardSource, StringComparison.Ordinal);
        Assert.Contains("reb.LostFocus += Control_LostFocus;", imeGuardSource, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false, false, false)] // Dynamic mode: Win+D keyboard (no mouse down, pre-Phase-2 non-topmost)
    [InlineData(true, false, false, false)]  // DesktopPinned mode: Win+D keyboard
    [InlineData(false, true, true, false)]   // Dynamic mode: Win+D while holding left mouse button inside fullscreen game
    [InlineData(true, true, true, false)]    // DesktopPinned mode: Win+D while holding mouse inside fullscreen game
    [InlineData(false, false, false, true)]  // Windows key physically down
    public void EvaluateUnsolicitedZOrderSuppression_AllowsShowDesktopAndFullscreenTransitions(
        bool isDesktopPinnedMode,
        bool isAnyMouseButtonDown,
        bool isLeftMouseButtonDown,
        bool isWindowsKeyDown)
    {
        var decision = WidgetLayerService.EvaluateUnsolicitedZOrderSuppression(
            flags: 0,
            isAuthorizedZOrderChangeInProgress: false,
            isAtDesktopLayer: true,
            hasExpandedLease: false,
            isDesktopPinnedMode: isDesktopPinnedMode,
            isQuickRevealMode: false,
            isBoundsInteracting: false,
            isRaisedFromTray: false,
            isWindowValidAndVisible: true,
            isOwnedByDesktopShell: true,
            isTopMostTransitionOrState: false,
            isWindowsKeyDown: isWindowsKeyDown,
            isSelfRecentlyClicked: false,
            isAnyWidgetRecentlyActivated: false,
            isAnyMouseButtonDown: isAnyMouseButtonDown,
            isLeftMouseButtonDown: isLeftMouseButtonDown,
            isPointerOverVerifiedDesktop: isWindowsKeyDown);

        Assert.False(decision.Suppress);
        Assert.False(decision.MayBeDynamicDesktopClick);
    }

    [Fact]
    public void EvaluateUnsolicitedZOrderSuppression_SuppressesDesktopIconAndSiblingWidgetLifts()
    {
        // Dynamic mode left-click on verified desktop: suppress Z-order lift AND signal blank-desktop click candidate.
        var dynamicDesktopLeftClick = WidgetLayerService.EvaluateUnsolicitedZOrderSuppression(
            flags: 0,
            isAuthorizedZOrderChangeInProgress: false,
            isAtDesktopLayer: true,
            hasExpandedLease: false,
            isDesktopPinnedMode: false,
            isQuickRevealMode: false,
            isBoundsInteracting: false,
            isRaisedFromTray: false,
            isWindowValidAndVisible: true,
            isOwnedByDesktopShell: true,
            isTopMostTransitionOrState: false,
            isWindowsKeyDown: false,
            isSelfRecentlyClicked: false,
            isAnyWidgetRecentlyActivated: false,
            isAnyMouseButtonDown: true,
            isLeftMouseButtonDown: true,
            isPointerOverVerifiedDesktop: true);
        Assert.True(dynamicDesktopLeftClick.Suppress);
        Assert.True(dynamicDesktopLeftClick.MayBeDynamicDesktopClick);

        // Dynamic mode sibling widget click: suppress passive sibling lift, not a blank-desktop click.
        var dynamicSiblingClick = WidgetLayerService.EvaluateUnsolicitedZOrderSuppression(
            flags: 0,
            isAuthorizedZOrderChangeInProgress: false,
            isAtDesktopLayer: true,
            hasExpandedLease: false,
            isDesktopPinnedMode: false,
            isQuickRevealMode: false,
            isBoundsInteracting: false,
            isRaisedFromTray: false,
            isWindowValidAndVisible: true,
            isOwnedByDesktopShell: true,
            isTopMostTransitionOrState: false,
            isWindowsKeyDown: false,
            isSelfRecentlyClicked: false,
            isAnyWidgetRecentlyActivated: true,
            isAnyMouseButtonDown: true,
            isLeftMouseButtonDown: true,
            isPointerOverVerifiedDesktop: false);
        Assert.True(dynamicSiblingClick.Suppress);
        Assert.False(dynamicSiblingClick.MayBeDynamicDesktopClick);

        // Dynamic mode self click: allow raise.
        var dynamicSelfClick = WidgetLayerService.EvaluateUnsolicitedZOrderSuppression(
            flags: 0,
            isAuthorizedZOrderChangeInProgress: false,
            isAtDesktopLayer: true,
            hasExpandedLease: false,
            isDesktopPinnedMode: false,
            isQuickRevealMode: false,
            isBoundsInteracting: false,
            isRaisedFromTray: false,
            isWindowValidAndVisible: true,
            isOwnedByDesktopShell: true,
            isTopMostTransitionOrState: false,
            isWindowsKeyDown: false,
            isSelfRecentlyClicked: true,
            isAnyWidgetRecentlyActivated: true,
            isAnyMouseButtonDown: true,
            isLeftMouseButtonDown: true,
            isPointerOverVerifiedDesktop: false);
        Assert.False(dynamicSelfClick.Suppress);
        Assert.False(dynamicSelfClick.MayBeDynamicDesktopClick);

        // DesktopPinned mode desktop icon click: suppress without dynamic raise candidate.
        var pinnedDesktopClick = WidgetLayerService.EvaluateUnsolicitedZOrderSuppression(
            flags: 0,
            isAuthorizedZOrderChangeInProgress: false,
            isAtDesktopLayer: true,
            hasExpandedLease: false,
            isDesktopPinnedMode: true,
            isQuickRevealMode: false,
            isBoundsInteracting: false,
            isRaisedFromTray: false,
            isWindowValidAndVisible: true,
            isOwnedByDesktopShell: true,
            isTopMostTransitionOrState: false,
            isWindowsKeyDown: false,
            isSelfRecentlyClicked: false,
            isAnyWidgetRecentlyActivated: false,
            isAnyMouseButtonDown: true,
            isLeftMouseButtonDown: true,
            isPointerOverVerifiedDesktop: true);
        Assert.True(pinnedDesktopClick.Suppress);
        Assert.False(pinnedDesktopClick.MayBeDynamicDesktopClick);
    }

    [Fact]
    public void IsDesktopHostTopMost_AndBeddingWatchdog_FallbackToLiveOwnerDuringShowDesktop()
    {
        string layerSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetLayerService.cs"));
        string hostTopMost = SliceBetween(
            layerSource,
            "internal static bool IsDesktopHostTopMost(",
            "internal static UnsolicitedZOrderSuppressionDecision EvaluateUnsolicitedZOrderSuppression(");
        Assert.Contains("Win32Helper.GWLP_HWNDPARENT", hostTopMost, StringComparison.Ordinal);

        string zOrderSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetManager.ZOrder.cs"));
        string verifyBedding = SliceBetween(
            zOrderSource,
            "private void VerifyDesktopPinnedBedding(",
            "private void RebedDesktopPinnedWidgetGroup(");
        Assert.Contains("WidgetLayerService.IsDesktopHostTopMost(window.WindowHandle)", verifyBedding, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsHostLaunch_DetectsBareSettingsWithoutPid()
    {
        Assert.True(App.SettingsHostLaunch.IsBareSettingsArgumentWithoutPid(["DeskBox.exe", "--settings"]));
        Assert.True(App.SettingsHostLaunch.IsBareSettingsArgumentWithoutPid(["DeskBox.exe", "--settings", "--open-settings"]));
        Assert.False(App.SettingsHostLaunch.IsBareSettingsArgumentWithoutPid(["DeskBox.exe", "--settings", "4242"]));
        Assert.False(App.SettingsHostLaunch.IsBareSettingsArgumentWithoutPid(["DeskBox.exe", "--open-settings"]));
        Assert.False(App.SettingsHostLaunch.IsBareSettingsArgumentWithoutPid(["DeskBox.exe"]));
    }

    [Fact]
    public async Task DrainLogQueue_SerializesConcurrentWritersWithoutDroppingFinalLines()
    {
        Directory.CreateDirectory(_temporaryRoot);
        string logPath = Path.Combine(_temporaryRoot, "settings-drain-race.log");
        App.TestLogFilePathOverride = logPath;
        App.DrainLogQueue();
        if (File.Exists(logPath))
        {
            File.Delete(logPath);
        }

        const int totalLines = 160;
        Task[] writers = Enumerable.Range(0, totalLines)
            .Select(index => Task.Run(() =>
            {
                App.Log($"[DrainRaceTest] marker-{index:D4}");
                if (index % 4 == 0)
                {
                    App.DrainLogQueue();
                }
            }))
            .ToArray();

        await Task.WhenAll(writers);
        App.DrainLogQueue();

        Assert.True(File.Exists(logPath));
        string written = await File.ReadAllTextAsync(logPath);
        for (int index = 0; index < totalLines; index++)
        {
            Assert.Contains($"marker-{index:D4}", written, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SettingsHostAndCoordinator_GuardPreIpcBootAndFallbackInProcess_AndFixEffectiveThemeTitleBar()
    {
        string programSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Program.cs"));
        int preXamlPos = programSource.IndexOf("App.TryForwardSameChannelSecondaryLaunchBeforeXaml(", StringComparison.Ordinal);
        int comWrappersPos = programSource.IndexOf("ComWrappersSupport.InitializeComWrappers();", StringComparison.Ordinal);
        Assert.True(preXamlPos > 0 && comWrappersPos > preXamlPos);

        string hostSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/App.SettingsHost.cs"));
        string initHost = SliceBetween(
            hostSource,
            "private void InitializeSettingsHost(",
            "private async void LaunchSettingsHostAsync()");
        int unhandledPos = initHost.IndexOf("UnhandledException +=", StringComparison.Ordinal);
        int initComponentPos = initHost.IndexOf("InitializeComponent();", StringComparison.Ordinal);
        Assert.True(unhandledPos > 0 && initComponentPos > unhandledPos);

        string exitHost = SliceBetween(
            hostSource,
            "internal void ExitSettingsHost(bool mainProcessExited)",
            "private static bool IsMainProcessStillAlive(");
        Assert.Contains("dispatcher.TryEnqueue(", exitHost, StringComparison.Ordinal);
        Assert.Contains("Exit();", exitHost, StringComparison.Ordinal);

        string coordinatorSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/App.SettingsHostCoordinator.cs"));
        Assert.Contains("ActivateInProcessSettingsFallback(", coordinatorSource, StringComparison.Ordinal);
        Assert.Contains("CreateInProcessFallbackSettingsWindow()", coordinatorSource, StringComparison.Ordinal);

        string settingsWindowSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml.cs"));
        string themeDarkMethod = SliceBetween(
            settingsWindowSource,
            "private bool IsEffectiveSettingsThemeDark()",
            "private void ApplyTitleBarButtonColors()");
        Assert.Contains("NeutralInteractionBrush.IsDarkTheme(SettingsRoot)", themeDarkMethod, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsRoot.ActualTheme", themeDarkMethod, StringComparison.Ordinal);

        string quickPasteSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ClipboardQuickPasteWindow.xaml.cs"));
        int trackPos = quickPasteSource.IndexOf("_themeService?.TrackWindow(this);", StringComparison.Ordinal);
        int safeBackdropPos = quickPasteSource.IndexOf("WindowsCompatibilityService.ApplySafeBackdrop(this);", StringComparison.Ordinal);
        Assert.True(trackPos > 0 && safeBackdropPos > trackPos);
    }

    [Fact]
    public void LocalizationService_DisableResidentHostKeyFilter_RestoresPurgedSettingsKeysForInProcessFallback()
    {
        try
        {
            Directory.CreateDirectory(_temporaryRoot);
            var settings = new SettingsService(_temporaryRoot);
            var localization = new LocalizationService(settings);
            const string settingsOnlyKey = "Settings.Todo.Layout.Title";
            Assert.True(LocalizationService.IsPureSettingsUiKey(settingsOnlyKey));

            string fullValue = localization.T(settingsOnlyKey);
            Assert.NotEqual(settingsOnlyKey, fullValue);

            LocalizationService.EnableResidentHostKeyFilter();
            Assert.Equal(settingsOnlyKey, localization.T(settingsOnlyKey));

            LocalizationService.DisableResidentHostKeyFilter();

            string restoredValue = localization.T(settingsOnlyKey);
            Assert.Equal(fullValue, restoredValue);
            Assert.NotEqual(settingsOnlyKey, restoredValue);
        }
        finally
        {
            LocalizationService.DisableResidentHostKeyFilter();
        }
    }

    [Fact]
    public async Task WidgetLayoutStore_RecoversValidQuarantinedLayoutFromV159Rollback()
    {
        Directory.CreateDirectory(_temporaryRoot);
        string layoutPath = Path.Combine(_temporaryRoot, "widget-layout.json");
        string quarantinedPath = layoutPath + ".corrupt-20261124034445";
        string layoutJson = """
            {
              "schemaVersion": 2,
              "layout": {
                "widgets": [
                  {
                    "id": "clip-1",
                    "name": "Clipboard",
                    "widgetKind": "Clipboard",
                    "x": 100,
                    "y": 120,
                    "width": 320,
                    "height": 420
                  }
                ],
                "widgetGroups": []
              }
            }
            """;
        await File.WriteAllTextAsync(quarantinedPath, layoutJson);

        // Fresh profile without post-1.6.0 settings must NOT resurrect old quarantines.
        var unverifiedStore = new WidgetLayoutStore(layoutPath);
        WidgetLayoutLoadResult unverifiedResult = await unverifiedStore.LoadAsync(
            new WidgetLayoutSettingsSlice(),
            allowRollbackQuarantineRecovery: false);
        Assert.Empty(unverifiedResult.Data.Widgets);
        Assert.True(File.Exists(quarantinedPath));

        // Simulate 2-launch v1.5.9 rollback (empty schemaVersion: 1 primary + post-1.6.0 quarantine).
        await File.WriteAllTextAsync(
            layoutPath,
            """{"schemaVersion":1,"layout":{"widgets":[],"widgetGroups":[]}}""");

        var store = new WidgetLayoutStore(layoutPath);
        WidgetLayoutLoadResult result = await store.LoadAsync(
            new WidgetLayoutSettingsSlice(),
            allowRollbackQuarantineRecovery: true);

        Assert.True(result.Authoritative);
        Assert.Single(result.Data.Widgets);
        Assert.Equal("clip-1", result.Data.Widgets[0].Id);
        Assert.Equal(WidgetKind.Clipboard, result.Data.Widgets[0].WidgetKind);
        Assert.True(File.Exists(layoutPath));
        // Consumed quarantine file must be deleted so later widget deletions never resurrect.
        Assert.False(File.Exists(quarantinedPath));
    }

    [Fact]
    public async Task TodoWidgetStore_RecoversAllDayTasksAfterV159RollbackStrippedHasDueTime()
    {
        Directory.CreateDirectory(_temporaryRoot);
        var store = new TodoWidgetStore(_temporaryRoot, "todo-test");
        DateTimeOffset localMidnight = new(new DateTime(2026, 11, 25, 0, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 25)));
        DateTimeOffset localEndOfDay = new(new DateTime(2026, 11, 26, 23, 59, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 26)));
        DateTimeOffset localTimed = new(new DateTime(2026, 11, 27, 14, 30, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 27)));
        await File.WriteAllTextAsync(
            store.StorePath,
            $$"""
            {
              "version": 4,
              "items": [
                { "id": "all-day-160", "text": "hasDueTime", "dueDate": "{{localMidnight:O}}" },
                { "id": "all-day-159", "text": "From 1.5.9", "dueDate": "{{localEndOfDay:O}}" },
                { "id": "timed-task", "text": "Meeting", "dueDate": "{{localTimed:O}}" }
              ]
            }
            """);

        TodoWidgetData loaded = await store.LoadAsync();

        TodoItem from160 = Assert.Single(loaded.Items, i => i.Id == "all-day-160");
        TodoItem from159 = Assert.Single(loaded.Items, i => i.Id == "all-day-159");
        TodoItem timed = Assert.Single(loaded.Items, i => i.Id == "timed-task");

        Assert.False(from160.HasDueTime);
        Assert.False(from159.HasDueTime);
        Assert.True(timed.HasDueTime);
    }

    [Fact]
    public async Task SettingsService_MigratesV159SplitLayoutLegacySearchDisableState()
    {
        Directory.CreateDirectory(_temporaryRoot);
        string settingsPath = Path.Combine(_temporaryRoot, "settings.json");
        string layoutPath = Path.Combine(_temporaryRoot, "widget-layout.json");

        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "schemaVersion": 15,
              "searchHotkeyEnabled": true
            }
            """);
        await File.WriteAllTextAsync(
            layoutPath,
            """
            {
              "schemaVersion": 1,
              "layout": {
                "featureWidgetEnabledStates": {
                  "Search": false
                },
                "widgets": [
                  {
                    "id": "legacy-search-1",
                    "name": "Search",
                    "widgetKind": "Search",
                    "x": 80,
                    "y": 80,
                    "width": 280,
                    "height": 90
                  }
                ]
              }
            }
            """);

        var service = new SettingsService(_temporaryRoot);
        await service.LoadAsync();

        Assert.False(service.Settings.Search.SearchHotkeyEnabled);
        Assert.False(FeatureWidgetSettings.IsEnabled(service.Settings, WidgetKind.Search));
        Assert.DoesNotContain(service.Settings.Widgets, w => w.WidgetKind == WidgetKind.Search);
    }

    [Fact]
    public async Task SettingsService_PreservesEnabledV159SearchWidgetAndStampsSchema19AndLayoutSchema3()
    {
        Directory.CreateDirectory(_temporaryRoot);
        string settingsPath = Path.Combine(_temporaryRoot, "settings.json");
        string layoutPath = Path.Combine(_temporaryRoot, "widget-layout.json");

        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "schemaVersion": 15,
              "searchHotkeyEnabled": false
            }
            """);
        await File.WriteAllTextAsync(
            layoutPath,
            """
            {
              "schemaVersion": 1,
              "layout": {
                "featureWidgetEnabledStates": {
                  "Search": true
                },
                "widgets": [
                  {
                    "id": "v159-search-active",
                    "name": "Search",
                    "widgetKind": "Search",
                    "x": 120,
                    "y": 140,
                    "width": 280,
                    "height": 90
                  }
                ]
              }
            }
            """);

        var service = new SettingsService(_temporaryRoot);
        await service.LoadAsync();

        Assert.Equal(19, service.Settings.SchemaVersion);
        Assert.Equal(3, WidgetLayoutStore.CurrentSchemaVersion);
        Assert.True(FeatureWidgetSettings.IsEnabled(service.Settings, WidgetKind.Search));
        WidgetConfig preserved = Assert.Single(service.Settings.Widgets, w => w.Id == "v159-search-active");
        Assert.Equal(WidgetKind.Search, preserved.WidgetKind);
    }

    [Fact]
    public async Task Win10AndWin11DeepAudit_VerifiesZipGlyphChildParentFocusHandoffAndStoreIpcParity()
    {
        // 1. Win10 ZipFolder (\uF012) falls back to MDL2 Package (\uE7B8) to prevent tofu boxes.
        Assert.Equal("\uE7B8", CustomWidgetSemanticIcons.NormalizeFluentGlyphForOs("\uF012", isWindows11OrLater: false));
        Assert.Equal("\uF012", CustomWidgetSemanticIcons.NormalizeFluentGlyphForOs("\uF012", isWindows11OrLater: true));

        // 2. Resident host key filter preserves Settings.AiSettings.EntryCard.Header for desktop AI Custom Widgets.
        Assert.False(LocalizationService.IsPureSettingsUiKey("Settings.AiSettings.EntryCard.Header"));

        // 3. SearchHistoryService scrubs null entries on load so SearchWidgetViewModel never throws ArgumentNullException.
        Directory.CreateDirectory(_temporaryRoot);
        string historyPath = Path.Combine(_temporaryRoot, "search-history.json");
        await File.WriteAllTextAsync(
            historyPath,
            """{"recent":[null,"  ","report"],"favorites":[null,"report"],"recentResults":[null,{"title":"Spec","identity":null}]}""");
        var history = new SearchHistoryService(historyPath);
        Assert.Equal(["report"], history.RecentQueries);
        Assert.Equal(["report"], history.FavoriteQueries);
        Assert.Single(history.RecentResults);

        // 4. SearchSettingsCoordinator exposes DesktopEntryEnabled on SearchSettingsSnapshot for live UI sync.
        var settingsService = new SettingsService(_temporaryRoot);
        FeatureWidgetSettings.SetEnabled(settingsService.Settings, WidgetKind.Search, true);
        var localization = new LocalizationService(settingsService);
        var coordinator = new SearchSettingsCoordinator(
            settingsService,
            localization,
            getConnection: static () => null,
            ensureConnection: static () => null,
            getHotkey: static () => null,
            setContentSearchEnabled: static _ => { });
        Assert.True(coordinator.Read().DesktopEntryEnabled);

        // 5. IPC ReleaseNotes lazy-fetch, GetChildParent WS_CHILD walk, and cross-window focus-steal guard.
        string routerSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/SettingsIpcMethodRouter.cs"));
        string releaseNotesCase = SliceBetween(
            routerSource,
            "case SettingsIpcMethods.AppOpenReleaseNotes:",
            "case SettingsIpcMethods.AppUpdateTrayIcon:");
        Assert.Contains("notes.AllowLazyFetch", releaseNotesCase, StringComparison.Ordinal);
        Assert.Contains("FetchReleaseNotesManifestAsync", releaseNotesCase, StringComparison.Ordinal);

        string hitTestSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/DesktopBlankHitTest.cs"));
        Assert.DoesNotContain("Win32Helper.GetParent(", hitTestSource, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.GetChildParent(", hitTestSource, StringComparison.Ordinal);

        string boundsSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string canProceed = SliceBetween(
            boundsSource,
            "private static bool CanProceedAfterTransientMenuForegroundChange(",
            "// ── Backdrop preference");
        Assert.Contains("foregroundNow != targetWidgetHwnd", canProceed, StringComparison.Ordinal);
        Assert.DoesNotContain("GA_ROOTOWNER", boundsSource, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreAndWin10Compatibility_EnforcesInProcessStoreOwnerHighContrastAndPopupFocusGuards()
    {
        Assert.Equal(
            "Solid",
            WindowsCompatibilityService.ResolveSafeBackdropKind(
                preferMica: true,
                supportsMica: true,
                supportsDesktopAcrylic: true,
                isHighContrast: true));

        Assert.True(TransientInputFocusGuard.IsKnownTextInputProcessOrClass(
            "Windows.UI.Core.CoreWindow",
            "WindowsInternal.ComposableShell.Experiences.TextInput.InputApp"));
        Assert.True(TransientInputFocusGuard.IsKnownTextInputProcessOrClass(
            "XamlExplorerHostIslandWindow",
            "TextInputHost"));
        Assert.False(TransientInputFocusGuard.IsKnownTextInputProcessOrClass(
            "#32770",
            "TextInputHost"));

        string coordinatorSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/App.SettingsHostCoordinator.cs"));
        string storeOwner = SliceBetween(
            coordinatorSource,
            "internal IntPtr ResolveStoreUpdateOwnerWindowHandle()",
            "private static bool IsVisibleNonEmptyOwnerWindow(");
        Assert.DoesNotContain("GetForegroundWindow", storeOwner, StringComparison.Ordinal);
        Assert.DoesNotContain("GetShellWindow", storeOwner, StringComparison.Ordinal);
        Assert.DoesNotContain("TryGetVisibleSettingsHostProcessWindowHandle", storeOwner, StringComparison.Ordinal);

        string createFallback = SliceBetween(
            coordinatorSource,
            "private SettingsWindow CreateInProcessFallbackSettingsWindow()",
            "private void InProcessFallbackSettingsWindow_Closed(");
        Assert.Contains("LocalizationService.DisableResidentHostKeyFilter();", createFallback, StringComparison.Ordinal);

        string boundsSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string gotFocusPopup = SliceBetween(
            boundsSource,
            "private void FocusManager_GotFocusForDesktopPinnedLayer(",
            "private static bool IsVisualDescendantOf(");
        Assert.Contains("FlyoutTextInputActivation.ActivateVisibleBridges(", gotFocusPopup, StringComparison.Ordinal);
    }

    private static string SliceBetween(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found: {endMarker}");
        return source[start..end];
    }
}
