namespace DeskBox.Tests;

/// <summary>
/// Pins the startup resilience contract: the single-instance mutex is taken in
/// the App constructor, so a process that fails or stalls during startup must
/// always terminate instead of lingering without any UI.
/// </summary>
public sealed class StartupResilienceContractTests
{
    [Fact]
    public void FatalStartupPath_ReportsExitsAndKeepsTheLock()
    {
        string startup = Read("src/DeskBox/App.Startup.cs");
        string failStartup = Slice(
            startup,
            "private static void FailStartup",
            "private async Task EnsureStartupProducedUsableSurfaceAsync");

        // Re-entrancy: the message box pumps messages, so the fatal path can be
        // entered again from an unhandled exception while the dialog is up.
        Assert.Contains(
            "Interlocked.Exchange(ref s_startupFailureHandled, 1)",
            failStartup,
            StringComparison.Ordinal);
        // Termination must not depend on the dialog being dismissed.
        Assert.Contains("new Thread(", failStartup, StringComparison.Ordinal);
        Assert.Contains("Thread.Sleep(StartupFailureGraceMs);", failStartup, StringComparison.Ordinal);
        Assert.Contains("Environment.Exit(1);", failStartup, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.ShowFatalError(", failStartup, StringComparison.Ordinal);
        Assert.Contains("DrainLogQueue();", failStartup, StringComparison.Ordinal);
        // The lock is deliberately left to the kernel: releasing it early would
        // let a second instance start while this one is still writing state.
        Assert.DoesNotContain("ReleaseMutex", failStartup, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupWatchdog_CoversStallsOnADedicatedThread()
    {
        string startup = Read("src/DeskBox/App.Startup.cs");
        string watchdog = Slice(
            startup,
            "private static void StartStartupWatchdog",
            "private static void MarkStartupLifelineEstablished");

        Assert.Contains("new Thread(", watchdog, StringComparison.Ordinal);
        Assert.Contains("IsBackground = true", watchdog, StringComparison.Ordinal);
        Assert.Contains("FailStartup(", watchdog, StringComparison.Ordinal);
        // The thread pool can be saturated by startup work, so the watchdog
        // must not depend on it.
        Assert.DoesNotContain("Task.Run", watchdog, StringComparison.Ordinal);
        Assert.DoesNotContain("Timer", watchdog, StringComparison.Ordinal);

        string launch = OnLaunched();
        int armed = launch.IndexOf("StartStartupWatchdog();", StringComparison.Ordinal);
        int tryBody = launch.IndexOf("string? updateInstallOutcome", StringComparison.Ordinal);
        Assert.True(armed >= 0, "OnLaunched must arm the startup watchdog.");
        Assert.True(
            tryBody > armed,
            "The watchdog must be armed before the startup try block.");
    }

    [Fact]
    public void OnLaunched_DegradesFeaturesInsteadOfBlockingStartup()
    {
        string launch = OnLaunched();

        // Widget restoration, first-run widget setup and onboarding were the
        // steps whose failure used to leave the app permanently unopenable.
        Assert.Contains("[Startup] Optional step 'restore-widgets' failed", launch, StringComparison.Ordinal);
        Assert.Contains("\"initial-file-widget-setup\"", launch, StringComparison.Ordinal);
        Assert.Contains("\"onboarding\"", launch, StringComparison.Ordinal);
        Assert.Contains("\"theme-refresh\"", launch, StringComparison.Ordinal);
        Assert.Contains("\"quick-capture-clipboard\"", launch, StringComparison.Ordinal);
        Assert.Contains("\"desktop-auto-organization-watcher\"", launch, StringComparison.Ordinal);
        Assert.Contains("\"display-area-watcher\"", launch, StringComparison.Ordinal);
        Assert.Contains("\"apply-pending-restore\"", launch, StringComparison.Ordinal);
        Assert.Contains("\"desktop-organization-recovery\"", launch, StringComparison.Ordinal);

        // The first-run setup path still signals failure to its caller (it keeps
        // the pending marker for the next launch); the caller is what degrades.
        Assert.Contains("() => EnsureInitialFileWidgetSetupAsync(isInteractiveLaunch: !IsStartupMode)", launch, StringComparison.Ordinal);
        string setup = Slice(
            Read("src/DeskBox/App.xaml.cs"),
            "private async Task EnsureInitialFileWidgetSetupAsync",
            "private async Task<bool> EnsureOnboardingAsync");
        Assert.Contains("throw;", setup, StringComparison.Ordinal);

        // No startup step may short-circuit the whole launch any more.
        Assert.DoesNotContain("throw;", launch, StringComparison.Ordinal);

        // The launch only reports success once a usable surface was verified.
        int surfaceCheck = launch.IndexOf("await EnsureStartupProducedUsableSurfaceAsync();", StringComparison.Ordinal);
        int successLog = launch.IndexOf("OnLaunched completed successfully", StringComparison.Ordinal);
        Assert.True(surfaceCheck >= 0, "OnLaunched must verify it produced a usable surface.");
        Assert.True(
            successLog > surfaceCheck,
            "The usable-surface check must run before the success log.");
    }

    [Fact]
    public void Lifeline_IsTheTraySurfaceAndLoadedWidgetsNotVisibility()
    {
        string tray = Read("src/DeskBox/App.Tray.cs");
        Assert.Contains("private bool IsTraySurfaceUsable()", tray, StringComparison.Ordinal);
        Assert.Contains("IsCreated: true", tray, StringComparison.Ordinal);
        Assert.Contains("trayIcon.TrayIcon.WindowHandle != IntPtr.Zero", tray, StringComparison.Ordinal);
        Assert.Contains("WindowNative.GetWindowHandle(_trayWindow) != IntPtr.Zero", tray, StringComparison.Ordinal);
        Assert.Contains("MarkStartupLifelineEstablished();", tray, StringComparison.Ordinal);

        // Widgets are legitimately hidden (quick-reveal, tray toggle), so the
        // surface check must count loaded widgets rather than visible ones.
        string startup = Read("src/DeskBox/App.Startup.cs");
        string surface = Slice(
            startup,
            "private async Task EnsureStartupProducedUsableSurfaceAsync",
            "private static async Task RunOptionalStartupStepAsync");
        Assert.Contains("WidgetManager?.LoadedWidgetCount > 0", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("HasVisibleWidgets", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("HasVisibleFileWidgets", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupLaunch_FailsQuietlyWhileInteractiveLaunchesKeepTheDialog()
    {
        string startup = Read("src/DeskBox/App.Startup.cs");
        string failStartup = Slice(
            startup,
            "private static void FailStartup",
            "private async Task EnsureStartupProducedUsableSurfaceAsync");

        // A task-triggered launch has no user watching: the fatal path must
        // exit at once (the logon task's restart policy retries) instead of
        // parking a modal dialog on an unattended desktop while the process
        // still owns the single-instance mutex.
        int quietExit = failStartup.IndexOf(
            "if (s_startupLaunchQuietExit)",
            StringComparison.Ordinal);
        int quietExitCall = failStartup.IndexOf(
            "Environment.Exit(1);",
            quietExit,
            StringComparison.Ordinal);
        int graceThread = failStartup.IndexOf(
            "Thread.Sleep(StartupFailureGraceMs);",
            quietExit,
            StringComparison.Ordinal);
        Assert.True(quietExit >= 0, "The fatal path must special-case startup launches.");
        Assert.True(quietExitCall >= 0, "The quiet path must terminate the process.");
        Assert.True(
            graceThread < 0 || quietExitCall < graceThread,
            "The quiet path must exit before the interactive grace thread arms.");

        // Interactive launches keep the visible report.
        Assert.Contains("Win32Helper.ShowFatalError(", failStartup, StringComparison.Ordinal);

        string launch = OnLaunched();
        // The flag must be set before the watchdog arms: the watchdog can call
        // the fatal path at any moment afterwards.
        int flag = launch.IndexOf("s_startupLaunchQuietExit = isStartupLaunch;", StringComparison.Ordinal);
        int armed = launch.IndexOf("StartStartupWatchdog();", StringComparison.Ordinal);
        Assert.True(flag >= 0, "OnLaunched must record whether this is a startup launch.");
        Assert.True(armed > flag, "The quiet-exit flag must be set before the watchdog arms.");
    }

    [Fact]
    public void TrayIconCreation_RetriesThroughTheEarlyLogonWindow()
    {
        string tray = Read("src/DeskBox/App.Tray.cs");

        // CreateTrayIcon itself must not call ForceCreate: the bare call is
        // the crash point observed in the field logs.
        string createTrayIcon = Slice(
            tray,
            "private void CreateTrayIcon()",
            "/// <summary>Attempts per tray creation pass");
        Assert.DoesNotContain("ForceCreate", createTrayIcon, StringComparison.Ordinal);
        Assert.Contains("StartTrayIconCreationWithRetry();", createTrayIcon, StringComparison.Ordinal);

        string retry = Slice(
            tray,
            "private Task<bool> StartTrayIconCreationWithRetry",
            "private bool IsTraySurfaceUsable()");

        // The retry must be bounded: an unbounded loop would keep the startup
        // gate waiting forever when the shell never comes up.
        Assert.Contains("TrayCreationMaxAttempts", retry, StringComparison.Ordinal);
        Assert.Contains("await Task.Delay(TrayCreationRetryDelay);", retry, StringComparison.Ordinal);
        Assert.Contains("MarkStartupLifelineEstablished();", retry, StringComparison.Ordinal);
        Assert.Contains("_trayIcon.ForceCreate(enablesEfficiencyMode: false);", retry, StringComparison.Ordinal);
    }

    [Fact]
    public void WindowShellState_IsTheOnlyPlaceThatTouchesSwitcherAndPresenterState()
    {
        var offenders = new List<string>();
        foreach (string path in EnumerateProductSources())
        {
            string source = File.ReadAllText(path);
            if (source.Contains("IsShownInSwitchers = false", StringComparison.Ordinal) ||
                source.Contains("SetPresenter(", StringComparison.Ordinal))
            {
                offenders.Add(Path.GetFileName(path));
            }
        }

        // Early-logon sessions reject these calls with E_NOTIMPL, so every
        // window must route them through the degrading helper.
        Assert.Equal(new[] { "WindowShellState.cs" }, offenders.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void UnhandledExceptionAndShutdown_AreFatalUntilTheLifelineIsUp()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string handler = Slice(
            app,
            "private void OnUnhandledException",
            "// ─── Search Services ─");

        Assert.Contains("e.Handled = true;", handler, StringComparison.Ordinal);
        Assert.Contains("!IsStartupLifelineEstablished", handler, StringComparison.Ordinal);
        Assert.Contains("FailStartup(", handler, StringComparison.Ordinal);

        string shutdown = Slice(app, "private async Task ShutdownApplicationAsync", "private async Task<bool> ShutdownCoreAsync");
        Assert.Contains("await ShutdownCoreAsync();", shutdown, StringComparison.Ordinal);
        Assert.Contains("finally", shutdown, StringComparison.Ordinal);
        Assert.Contains("Exit();", shutdown, StringComparison.Ordinal);

        // Application.Exit alone leaves the message loop pumping when the
        // deadline path skips dependent teardown (measured on the probe);
        // the skipped path must arm an Environment.Exit watchdog.
        Assert.Contains("Dependent teardown skipped", shutdown, StringComparison.Ordinal);
        Assert.Contains("Environment.Exit(0)", shutdown, StringComparison.Ordinal);
    }

    [Fact]
    public void StartupWatchdog_MeasuresStallNotTotalElapsedTime()
    {
        string startup = Read("src/DeskBox/App.Startup.cs");
        string watchdog = Slice(
            startup,
            "private static void StartStartupWatchdog",
            "private static void MarkStartupLifelineEstablished");

        // A multi-gigabyte data restore legitimately runs past any fixed
        // total-time budget; the watchdog may only kill a startup that has
        // stopped making progress entirely.
        Assert.Contains("s_lastStartupProgressTicks", watchdog, StringComparison.Ordinal);
        Assert.Contains("stalledMs", watchdog, StringComparison.Ordinal);
        Assert.DoesNotContain("waitedMs", watchdog, StringComparison.Ordinal);

        // The long phases must actually mark progress: the backup snapshot
        // copy loop (pre-restore archive) and per-widget restoration.
        string backup = Read("src/DeskBox/Services/DeskBoxDataBackupService.cs");
        Assert.Contains("App.MarkStartupProgress();", backup, StringComparison.Ordinal);
        string manager = Read("src/DeskBox/Services/WidgetManager.cs");
        Assert.Contains("App.MarkStartupProgress();", manager, StringComparison.Ordinal);
    }

    private static string OnLaunched() =>
        Slice(
            Read("src/DeskBox/App.xaml.cs"),
            "protected override async void OnLaunched",
            "FailStartup(\"exception during startup\", ex);");

    private static IEnumerable<string> EnumerateProductSources()
    {
        string root = TestPaths.FromRepository("src/DeskBox");
        return Directory
            .EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsBuildOutput(path));
    }

    private static bool IsBuildOutput(string path)
    {
        string separator = Path.DirectorySeparatorChar.ToString();
        return path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase) ||
            path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase);
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Start marker not found: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"End marker not found: {endMarker}");
        return source[start..end];
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
