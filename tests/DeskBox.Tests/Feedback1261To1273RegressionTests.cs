using System.IO.Compression;
using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class Feedback1261To1273RegressionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBoxFeedback1261To1273Tests",
        Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch
            {
            }
        }
    }

    [Theory]
    [InlineData("", "收纳", true)]
    [InlineData("   ", "收纳", true)]
    [InlineData("收纳", "收纳", true)]
    [InlineData("  收纳  ", "收纳", true)]
    [InlineData("notes", "Notes", false)]
    [InlineData("工作资料", "收纳", false)]
    public void WidgetTitleRenamePolicy_SkipsEmptyAndUnchangedNamesWhileAllowingCaseOnlyRename(
        string candidate,
        string initialDisplay,
        bool expectedSkip)
    {
        Assert.Equal(
            expectedSkip,
            WidgetTitleRenamePolicy.ShouldSkipCommitAsUnchanged(candidate, initialDisplay));
    }

    [Theory]
    [InlineData(true, true, false, false, true, false)]   // Blur/LostFocus must revert immediately (#1261/#1267 loop)
    [InlineData(false, false, false, false, true, false)] // Hidden widget must never re-focus (#1261 ghost window)
    [InlineData(false, true, true, false, true, false)]   // Closing widget must not re-focus
    [InlineData(false, true, false, true, true, false)]   // Tray hide in progress must not re-focus
    [InlineData(false, true, false, false, false, false)] // Background window must not steal focus
    [InlineData(false, true, false, false, true, true)]   // Explicit Enter on visible foreground widget keeps editor open
    public void WidgetTitleRenamePolicy_OnlyKeepsEditorOpenForExplicitEnterOnVisibleForegroundWidget(
        bool initiatedByBlur,
        bool isWindowVisible,
        bool isClosing,
        bool isHideInProgress,
        bool ownsForeground,
        bool expectedKeepOpen)
    {
        Assert.Equal(
            expectedKeepOpen,
            WidgetTitleRenamePolicy.ShouldKeepEditorOpenAfterFailure(
                initiatedByBlur,
                isWindowVisible,
                isClosing,
                isHideInProgress,
                ownsForeground));
    }

    [Fact]
    public void ContentWidgetTitleRename_ExpandsCapsuleStretchesOverlayAndCancelsOnHide()
    {
        string commandsSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.Commands.cs"));
        string startRename = SliceBetween(
            commandsSource,
            "private void StartTitleRename()",
            "private void ActivateForTitleRename()");
        Assert.Contains("if (IsWidgetCollapsed)", startRename, StringComparison.Ordinal);
        Assert.Contains("PrepareSurfaceForExternalInteraction();", startRename, StringComparison.Ordinal);

        string lostFocus = SliceBetween(
            commandsSource,
            "private async void TitleRenameEditor_LostFocus(",
            "private async void TitleRenameEditor_KeyDown(");
        Assert.Contains("CommitTitleRenameAsync(initiatedByBlur: true)", lostFocus, StringComparison.Ordinal);

        string commitRename = SliceBetween(
            commandsSource,
            "private async Task CommitTitleRenameAsync(",
            "private void CancelTitleRename()");
        Assert.Contains("WidgetTitleRenamePolicy.ShouldSkipCommitAsUnchanged(", commitRename, StringComparison.Ordinal);
        Assert.Contains("WidgetTitleRenamePolicy.ShouldKeepEditorOpenAfterFailure(", commitRename, StringComparison.Ordinal);

        string windowSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        string prepareHide = SliceBetween(
            windowSource,
            "public bool PrepareTrayHideAnimation(",
            "public void PlayPreparedTrayHideAnimation()");
        Assert.Contains("CancelTitleRename();", prepareHide, StringComparison.Ordinal);

        string hideWindow = SliceBetween(
            windowSource,
            "public void HideWindow()",
            "public void CloseWindow()");
        Assert.Contains("CancelTitleRename();", hideWindow, StringComparison.Ordinal);

        string shellSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetShell.xaml.cs"));
        Assert.Contains("bool collapseOverlayHeader = usesOverlay && !isEditingTitle;", shellSource, StringComparison.Ordinal);
    }

    [Fact]
    public void FileFolderAndStackRename_IsolatesStackPopoverFocusAndDefersMenuRenameUntilClosed()
    {
        string surfaceSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs"));
        string selectRename = SliceBetween(
            surfaceSource,
            "private void SelectItemNameForRename(",
            "private async Task DeleteItemAsync(");
        Assert.Contains("if (ReferenceEquals(textBox, ItemRenameTextBox))", selectRename, StringComparison.Ordinal);

        string onActivated = SliceBetween(
            surfaceSource,
            "internal void OnActivated()",
            "internal void PrepareForReuse()");
        Assert.Contains("_itemRenameTarget is not null", onActivated, StringComparison.Ordinal);
        Assert.Contains("_pendingMenuRenameHandoffDepth > 0", onActivated, StringComparison.Ordinal);

        string menuBuilderSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/FileItemMenuBuilder.cs"));
        string addRenameRow = SliceBetween(
            menuBuilderSource,
            "private static void AddRenameRow(",
            "private static void AddOpenRow(");
        Assert.Contains("flyout.Closed +=", addRenameRow, StringComparison.Ordinal);
        Assert.Contains("AcquireMenuCloseRenameHandoff", addRenameRow, StringComparison.Ordinal);

        string selectionMenusSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs"));
        string createStackFlyout = SliceBetween(
            selectionMenusSource,
            "private MenuFlyout CreateStackFlyout(",
            "MenuFlyoutItem moveUp = CreateMenuItem(");
        Assert.Contains("flyout.Closed +=", createStackFlyout, StringComparison.Ordinal);
        Assert.Contains("AcquireMenuCloseRenameHandoff()", createStackFlyout, StringComparison.Ordinal);

        string stackPopoverSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/StackPopoverHostWindow.cs"));
        string activateInput = SliceBetween(
            stackPopoverSource,
            "internal void ActivateInput(",
            "internal void UpdateBounds(");
        Assert.Contains("TransientInputFocusGuard.ActivateWindowAndFocusIsland(", activateInput, StringComparison.Ordinal);

        string stackPopoverRenameSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/FileSurfaceContent.StackPopoverRename.cs"));
        Assert.Contains("ImeCompositionGuard.AttachAncestors(editor);", stackPopoverRenameSource, StringComparison.Ordinal);
        Assert.Contains("ImeCompositionGuard.AttachAncestors(titleEditor);", stackPopoverRenameSource, StringComparison.Ordinal);

        string contentWindowSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        string blockingFlyout = SliceBetween(
            contentWindowSource,
            "protected override bool HasBlockingFlyoutOpen()",
            "protected override bool OwnsTransientForegroundWindow(");
        Assert.Contains("HasActiveInlineRename: true", blockingFlyout, StringComparison.Ordinal);

        string rustProxySource = File.ReadAllText(
            TestPaths.FromRepository("native/deskbox-thumbnail-proxy/src/main.rs"));
        string customExitBranch = SliceBetween(
            rustProxySource,
            "if selected >= CONTEXT_MENU_CUSTOM_COMMAND_FIRST {",
            "if selected < CONTEXT_MENU_FIRST_COMMAND_ID {");
        Assert.Contains("AllowSetForegroundWindow(ASFW_ANY)", customExitBranch, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopLayerBeddingPolicy_StopsAtShellDesktopFloorAndIgnoresSubProgmanWindows()
    {
        // #1261 / #1267 / #1273: windows sitting strictly below Progman/WorkerW
        // (sub-desktop helpers, wallpaper hosts) can never sit above DefView-owned
        // widgets and must not trigger 5-10s full-group SetWindowPos storms.
        DesktopLayerBeddingWindowKind[] subProgmanHelper =
        [
            DesktopLayerBeddingWindowKind.DeskBoxWindow,
            DesktopLayerBeddingWindowKind.IgnoredWindow,
            DesktopLayerBeddingWindowKind.ShellDesktopWindow,
            DesktopLayerBeddingWindowKind.ForeignAppWindow,
        ];

        Assert.True(DesktopLayerBeddingPolicy.IsBeddedAtDesktopLayer(subProgmanHelper));
        Assert.Null(DesktopLayerBeddingPolicy.FindLeakWitness(subProgmanHelper));

        // Genuine leak: foreign app window sits ABOVE ShellDesktopWindow (between widget and desktop).
        DesktopLayerBeddingWindowKind[] realLeakAboveDesktop =
        [
            DesktopLayerBeddingWindowKind.DeskBoxWindow,
            DesktopLayerBeddingWindowKind.ForeignAppWindow,
            DesktopLayerBeddingWindowKind.ShellDesktopWindow,
        ];

        Assert.False(DesktopLayerBeddingPolicy.IsBeddedAtDesktopLayer(realLeakAboveDesktop));
        Assert.Equal(
            DesktopLayerBeddingWindowKind.ForeignAppWindow,
            DesktopLayerBeddingPolicy.FindLeakWitness(realLeakAboveDesktop));

        string layerServiceSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetLayerService.cs"));
        string findBelow = SliceBetween(
            layerServiceSource,
            "internal static IntPtr FindForeignWindowBelow(",
            "public static bool IsWindowCoveredByForeignWindow(");
        Assert.Contains("DesktopLayerBeddingWindowKind.ShellDesktopWindow", findBelow, StringComparison.Ordinal);

        string findAnchor = SliceBetween(
            layerServiceSource,
            "private static IntPtr FindLowestForeignWindowAnchor(",
            "internal readonly record struct UnsolicitedZOrderSuppressionDecision(");
        Assert.Contains("DesktopLayerBeddingWindowKind.ShellDesktopWindow", findAnchor, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OutOfProcessPickerAndEmergencyDiagnosticsExport_ParseHResultAndExportValidArchive()
    {
        Assert.Equal(
            unchecked((int)0x80070005),
            NativeFileOpenDialog.ParseProxyFailureHResult("hr=0x80070005 Access is denied."));
        Assert.Equal(
            unchecked((int)0x80004005),
            NativeFileOpenDialog.ParseProxyFailureHResult(string.Empty));
        Assert.Equal(
            unchecked((int)0x80004005),
            NativeFileOpenDialog.ParseProxyFailureHResult("malformed error line"));

        Directory.CreateDirectory(_tempRoot);
        var bundleService = new DeskBoxDiagnosticsBundleService();
        string archivePath = await bundleService.ExportEmergencyFromHostAsync(
            _tempRoot,
            "MainIpcTimeout15s");

        Assert.True(File.Exists(archivePath));
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        string[] entryNames = zip.Entries.Select(static e => e.FullName).ToArray();
        Assert.Contains("diagnostics.json", entryNames);
        Assert.Contains("README.txt", entryNames);
        Assert.Contains("DeskBox-sanitized.log", entryNames);
        Assert.Contains("windows-events.json", entryNames);

        using var reader = new StreamReader(zip.GetEntry("diagnostics.json")!.Open());
        string diagnosticsJson = await reader.ReadToEndAsync();
        Assert.Contains("MainIpcTimeout15s", diagnosticsJson, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsAppearanceDetailAndTrayAnimation_AvoidUiThreadDriveProbeAndHonorTrayAnimationToggle()
    {
        string storageSettingsSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.StorageAndUpdates.cs"));
        string warningRefresh = SliceBetween(
            storageSettingsSource,
            "private async Task RefreshManagedStoragePathWarningAsync()",
            "private void RefreshManagedStorageDesktopShortcutState()");
        Assert.Contains("await Task.Run(", warningRefresh, StringComparison.Ordinal);

        string shortcutRefresh = SliceBetween(
            storageSettingsSource,
            "private async Task RefreshManagedStorageDesktopShortcutStateAsync()",
            "private async void AppearanceDetailSection_DesktopShortcutToggled(");
        Assert.Contains("await Task.Run(() => _appActions.HasManagedStorageDesktopShortcut())", shortcutRefresh, StringComparison.Ordinal);

        string interactionSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Interaction.cs"));
        string trayProfile = SliceBetween(
            interactionSource,
            "protected WidgetTrayAnimationProfile GetTrayAnimationProfile(",
            "protected void LogTrayWindow(");
        Assert.DoesNotContain("WindowsCompatibilityService.AreAnimationsEnabled", trayProfile, StringComparison.Ordinal);
        Assert.Contains("!WindowsCompatibilityService.IsHighContrast", trayProfile, StringComparison.Ordinal);

        string coordinatorSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetCompactAnimationCoordinator.cs"));
        Assert.Contains("Win11StallFallbackFrameIntervalMs = 16;", coordinatorSource, StringComparison.Ordinal);

        string focusGuardSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Helpers/TransientInputFocusGuard.cs"));
        string popupBridgeCheck = SliceBetween(
            focusGuardSource,
            "public static bool IsCurrentProcessPopupBridge(",
            "public static bool IsWindowsTextInputExperienceWindow(");
        Assert.DoesNotContain("GetVisiblePopupBridgeWindows()", popupBridgeCheck, StringComparison.Ordinal);
        Assert.DoesNotContain("Win32Helper.IsWindowVisible(", popupBridgeCheck, StringComparison.Ordinal);
        Assert.Contains("Microsoft.UI.Content.PopupWindowSiteBridge", popupBridgeCheck, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepAuditHardening_CoversTodoMenuRenameTopmostWorkerWAndFilteredOutOfProcessPickers()
    {
        // P1-1: Menu teardown & ShellContextMenuProxy desktop-root foreground handoff.
        string boundsSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/WidgetWindowBase.Bounds.cs"));
        string canProceed = SliceBetween(
            boundsSource,
            "private static bool CanProceedAfterTransientMenuForegroundChange(",
            "// ── Backdrop preference");
        Assert.Contains("IsDesktopShellRootWindow(foregroundAtEnqueue)", canProceed, StringComparison.Ordinal);
        Assert.Contains("ShellContextMenuProxy.WasMenuRoundRecentlyActive()", canProceed, StringComparison.Ordinal);

        // P1-2: Out-of-process picker filter normalization, start-folder mapping, stdin EOF & theme sync.
        Assert.Equal(
            "*.png;*.jpg;*.svg",
            NativeFileOpenDialog.NormalizeFileFilterSpec([".png", "jpg", "*.PNG", ".svg"], out bool hasLnk1));
        Assert.False(hasLnk1);

        Assert.Equal(
            "*.lnk;*.exe",
            NativeFileOpenDialog.NormalizeFileFilterSpec([".lnk", ".exe"], out bool hasLnk2));
        Assert.True(hasLnk2);

        Assert.Equal(
            string.Empty,
            NativeFileOpenDialog.NormalizeFileFilterSpec([".png", "*.*"], out bool hasLnkWild));
        Assert.False(hasLnkWild);

        Assert.Equal(
            "shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}",
            FileOpenPickerService.ResolveSuggestedStartFolder(Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder));

        string nativePickerSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Platform/NativeFileOpenDialog.cs"));
        Assert.Contains("RedirectStandardInput = true", nativePickerSource, StringComparison.Ordinal);
        Assert.Contains("\"--watch-stdin\"", nativePickerSource, StringComparison.Ordinal);
        Assert.Contains("\"--theme\"", nativePickerSource, StringComparison.Ordinal);

        // P2-1: Win+D topmost WorkerW must not terminate normal-band floor walk prematurely.
        string layerServiceSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetLayerService.cs"));
        string findAnchor = SliceBetween(
            layerServiceSource,
            "private static IntPtr FindLowestForeignWindowAnchor(",
            "internal readonly record struct UnsolicitedZOrderSuppressionDecision(");
        Assert.Contains("!Win32Helper.IsWindowTopMost(current)", findAnchor, StringComparison.Ordinal);

        // P2-2: Todo context-menu Rename/Edit defers until flyout.Closed and blocks smart-capsule collapse.
        string todoMenusSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/TodoWidgetContent.Menus.cs"));
        string createItemFlyout = SliceBetween(
            todoMenusSource,
            "private MenuFlyout CreateItemFlyout(",
            "var completeItem = CreateTodoContextCommand(");
        Assert.Contains("flyout.Closed +=", createItemFlyout, StringComparison.Ordinal);
        Assert.Contains("TryActivateOwnerForKeyboardInput(this, \"todo-detail-title\")", createItemFlyout, StringComparison.Ordinal);

        string contentWindowSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.xaml.cs"));
        string blockingFlyout = SliceBetween(
            contentWindowSource,
            "protected override bool HasBlockingFlyoutOpen()",
            "protected override bool OwnsTransientForegroundWindow(");
        Assert.Contains("HasActiveInlineOverlay: true", blockingFlyout, StringComparison.Ordinal);
    }

    [Fact]
    public void Feedback1284To1290Sweep_CoversCustomWidgetLocateCrashWatcherDeleteAndQuickCaptureMenus()
    {
        // #1284 / #1289: LocateCustomWidgetAsync must not restart TrayShowAnimation or
        // bind CenterPoint expressions onto RootGrid's handoff Visual while showing.
        string featureWidgetsSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetManager.FeatureWidgets.cs"));
        string locateSlice = SliceBetween(
            featureWidgetsSource,
            "public async Task<bool> LocateCustomWidgetAsync(string widgetId)",
            "public Task RemoveCustomWidgetAsync(");
        Assert.Contains("IsTrayShowAnimationInFlight || existingWindow.IsTrayShowWindowCloaked", locateSlice, StringComparison.Ordinal);
        Assert.Contains("ActiveContentElement: Microsoft.UI.Xaml.UIElement contentRoot", locateSlice, StringComparison.Ordinal);
        Assert.DoesNotContain("window.Content is Microsoft.UI.Xaml.UIElement", locateSlice, StringComparison.Ordinal);

        // Coalesce concurrent RequestCommitAsync calls per UI thread and clear CenterPoint animation.
        string trayControllerSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetTrayAnimationController.cs"));
        Assert.Contains("s_inFlightCommitTask is { IsCompleted: false } existingCommit", trayControllerSource, StringComparison.Ordinal);
        Assert.Contains("visual.StopAnimation(\"CenterPoint\");", trayControllerSource, StringComparison.Ordinal);

        // #1290: TitleBarGrid click while renaming passes initiatedByBlur: true, and
        // RemoveWidgetAsync suspends FileSystemWatcher before deleting managed folder.
        string windowInteractionSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Views/ContentWidgetWindow.WindowInteraction.cs"));
        string titleBarPress = SliceBetween(
            windowInteractionSource,
            "private void TitleBarGrid_PointerPressed(",
            "private void TitleBarGrid_PointerMoved(");
        Assert.Contains("CommitTitleRenameAsync(initiatedByBlur: true)", titleBarPress, StringComparison.Ordinal);

        string widgetManagerSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/WidgetManager.cs"));
        string removeSlice = SliceBetween(
            widgetManagerSource,
            "public async Task RemoveWidgetAsync(",
            "_deletedWidgetIds.Add(widgetId);");
        Assert.Contains("await SuspendFileWidgetWatchersAsync(config.Id);", removeSlice, StringComparison.Ordinal);
        Assert.Contains("await ResumeFileWidgetWatchersIfStoppedAsync(config.Id);", removeSlice, StringComparison.Ordinal);

        // #1284: QuickCapture context menu defers Edit/Export/MirrorFolder until flyout.Closed
        // and resolves host HWND instead of dying PopupWindowSiteBridge foreground HWND.
        string quickBatchSource = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.BatchAndExport.cs"));
        Assert.Contains("AttachDeferredContextFlyoutAction(flyout, editItem", quickBatchSource, StringComparison.Ordinal);
        Assert.Contains("AttachDeferredContextFlyoutAction(\n            flyout,\n            exportMdItem", quickBatchSource.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("TransientInputFocusGuard.IsCurrentProcessPopupBridge(foreground)", quickBatchSource, StringComparison.Ordinal);
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
