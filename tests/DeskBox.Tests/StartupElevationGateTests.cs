using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class StartupElevationGateTests
{
    [Theory]
    [InlineData(false, false, true)]   // UAC on, Explorer standard: relaunch works
    [InlineData(false, true, false)]   // UAC on but Explorer elevated: parent handover would stay elevated
    [InlineData(true, false, false)]   // UAC off: no reliable standard-user context to promise
    [InlineData(true, true, false)]    // UAC off and Explorer elevated (the typical UAC-off machine)
    public void ComputeCanRelaunchUnelevated_OnlyPromisesARelaunchIntoAStandardUserContext(
        bool isUacDisabled,
        bool isExplorerElevated,
        bool expected)
    {
        Assert.Equal(
            expected,
            DragDropPermissionService.ComputeCanRelaunchUnelevated(isUacDisabled, isExplorerElevated));
    }

    [Fact]
    public void StartupElevationAssessment_UnrelaunchableWhenExplorerIsElevated()
    {
        // Guards against regressing to Diagnose's looser NeedsRelaunch gate
        // (which would offer a restart that lands on an elevated instance
        // again whenever Explorer itself runs elevated).
        bool uacOffWithElevatedExplorer = DragDropPermissionService.ComputeCanRelaunchUnelevated(
            isUacDisabled: true,
            isExplorerElevated: true);
        bool uacOnWithElevatedExplorer = DragDropPermissionService.ComputeCanRelaunchUnelevated(
            isUacDisabled: false,
            isExplorerElevated: true);

        Assert.False(uacOffWithElevatedExplorer);
        Assert.False(uacOnWithElevatedExplorer);
    }

    [Fact]
    public void WaitForProcessExitLimited_ResolvesEntryPointsAndHandlesLiveAndMissingProcesses()
    {
        // Entry-point probe + behavior test: PID 0 / negative PID returns AlreadyExited,
        // and waiting 0ms on the current live process returns TimedOut without throwing.
        Assert.Equal(
            DragDropPermissionNativeMethods.CrossIntegrityProcessWaitOutcome.AlreadyExited,
            DragDropPermissionNativeMethods.WaitForProcessExitLimited(0, 50, out _));

        Assert.Equal(
            DragDropPermissionNativeMethods.CrossIntegrityProcessWaitOutcome.TimedOut,
            DragDropPermissionNativeMethods.WaitForProcessExitLimited(Environment.ProcessId, 0, out int win32Err));
        Assert.Equal(0, win32Err);
    }

    [Fact]
    public void ShortcutRunAsFlag_ClearsInPlaceWhilePreservingTargetArgumentsAndIcon()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-lnk-runas-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string lnkPath = Path.Combine(tempDir, "DeskBoxCustom.lnk");
            string targetExe = Environment.ProcessPath ?? Path.Combine(Environment.SystemDirectory, "notepad.exe");
            string customIcon = Path.Combine(Environment.SystemDirectory, "shell32.dll");
            Exception? threadError = null;

            var staThread = new Thread(() =>
            {
                try
                {
                    DragDropPermissionService.CreateOrUpdateShortcut(
                        lnkPath,
                        targetExe,
                        "--jump-action open-settings",
                        customIcon);

                    Assert.False(DragDropPermissionService.TryGetShortcutRunAsFlag(lnkPath));

                    Assert.True(DragDropPermissionService.TrySetShortcutRunAsFlag(
                        lnkPath,
                        enableRunAs: true,
                        out bool setModified,
                        out string? setErr), setErr);
                    Assert.True(setModified);
                    Assert.True(DragDropPermissionService.TryGetShortcutRunAsFlag(lnkPath));

                    Assert.True(DragDropPermissionService.TryClearShortcutRunAsFlag(
                        lnkPath,
                        out bool clearModified,
                        out string? clearErr), clearErr);
                    Assert.True(clearModified);
                    Assert.False(DragDropPermissionService.TryGetShortcutRunAsFlag(lnkPath));

                    // Second clear is an idempotent no-op.
                    Assert.True(DragDropPermissionService.TryClearShortcutRunAsFlag(
                        lnkPath,
                        out bool secondModified,
                        out _));
                    Assert.False(secondModified);

                    Assert.True(DragDropPermissionService.TryReadShortcutWithCSharp(
                        lnkPath,
                        out string preservedTarget,
                        out string preservedArgs));
                    Assert.Equal(Path.GetFullPath(targetExe), Path.GetFullPath(preservedTarget), ignoreCase: true);
                    Assert.Equal("--jump-action open-settings", preservedArgs);
                }
                catch (Exception ex)
                {
                    threadError = ex;
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            Assert.True(staThread.Join(TimeSpan.FromSeconds(10)));
            if (threadError is not null)
            {
                throw new AggregateException(threadError);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void BuildUnelevatedRelaunchArguments_StripsOldAwaitPidQuotesTokensAndPreservesFlags()
    {
        string interactive = DeskBox.App.BuildUnelevatedRelaunchArguments(
            [@"C:\Program Files\DeskBox\DeskBox.exe", "--await-parent-exit", "111", "--jump", "my folder"],
            currentProcessId: 4321,
            includeStartupFlagWhenMissing: false);
        Assert.Equal("--await-parent-exit 4321 --jump \"my folder\"", interactive);

        string startupTask = DeskBox.App.BuildUnelevatedRelaunchArguments(
            [@"C:\Program Files\DeskBox\DeskBox.exe", "--await-parent-exit=999"],
            currentProcessId: 8765,
            includeStartupFlagWhenMissing: true);
        Assert.Equal("--await-parent-exit 8765 --startup", startupTask);

        Assert.True(DeskBox.App.IsAutomatedProbeOrSmokeLaunch(
            [@"C:\DeskBox.exe", "--aot-shortcut-smoke"],
            devDataRootOverride: string.Empty));
        Assert.True(DeskBox.App.IsAutomatedProbeOrSmokeLaunch(
            [@"C:\DeskBox.exe"],
            devDataRootOverride: @"C:\temp\probe"));
        Assert.False(DeskBox.App.IsAutomatedProbeOrSmokeLaunch(
            [@"C:\DeskBox.exe", "--startup"],
            devDataRootOverride: string.Empty));
    }

    [Fact]
    public void AppConstructor_RunsPreMutexElevationSelfHealBeforeSingleInstanceKernelObjects()
    {
        string appSource = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/App.xaml.cs"));
        int ctorStart = appSource.IndexOf("public App()", StringComparison.Ordinal);
        Assert.True(ctorStart >= 0);

        int assessIndex = appSource.IndexOf(
            "_startupElevationAssessment = DragDropPermissionService.AssessStartupElevation();",
            ctorStart,
            StringComparison.Ordinal);
        int eventIndex = appSource.IndexOf(
            "_activationEvent = new EventWaitHandle(",
            ctorStart,
            StringComparison.Ordinal);
        int mutexIndex = appSource.IndexOf(
            "_singleInstanceMutex = new Mutex(",
            ctorStart,
            StringComparison.Ordinal);

        Assert.True(assessIndex > ctorStart);
        Assert.True(eventIndex > assessIndex, "Elevation self-heal must execute before creating _activationEvent.");
        Assert.True(mutexIndex > assessIndex, "Elevation self-heal must execute before creating _singleInstanceMutex.");
    }

    [Fact]
    public void AllSecondaryXamlDragSurfaces_GuardAgainstElevatedStartDragCrash()
    {
        string[] guardedFiles =
        [
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs",
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DragDrop.cs",
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs",
            "src/DeskBox/Controls/WidgetContents/ClipboardWidgetContent.xaml.cs",
            "src/DeskBox/Controls/WidgetGroupTitleSwitcher.xaml.cs",
            "src/DeskBox/Views/SettingsWindow.Navigation.cs",
            "src/DeskBox/Views/SettingsSections/CustomWidgetSettingsSection.xaml.cs"
        ];

        foreach (string relativePath in guardedFiles)
        {
            string source = File.ReadAllText(TestPaths.FromRepository(relativePath));
            Assert.Contains(
                "DragDropPermissionService.IsCurrentProcessElevatedCached()",
                source,
                StringComparison.Ordinal);
        }
    }
}
