using System.Diagnostics;
using DeskBox.Platform;
using Microsoft.Win32;

namespace DeskBox.Services;

/// <summary>
/// Passively forwards file previews to an already-running PowerToys Peek
/// instance. Peek gained a command-line activation surface in PowerToys
/// 0.96 (microsoft/PowerToys#38754): launching
/// "PowerToys.Peek.UI.exe &lt;path&gt;" opens a preview, re-sending the same
/// path closes it, and a different path switches — the same contract the
/// QuickLook pipe exposes, so the WidgetManager session state machine can
/// drive both backends unchanged.
///
/// Like QuickLookPreviewService this never discovers, installs, or
/// configures the other app: a running Peek module process is the only
/// availability signal (the PowerToys runner keeps that process alive
/// exactly while the module is enabled, which also honors a user who
/// disabled Peek). Version probing fails closed because a pre-0.96
/// install also runs the same process and would silently ignore the path
/// argument while spawning a useless extra process on every keypress.
///
/// Security note: the activation forwards a locally validated file path
/// (existence-checked; quote-free by Windows path rules) to a fixed,
/// resolved-by-probe executable through ShellUnelevatedLaunch — no shell,
/// no user-controlled executable, no unescaped concatenation. Audited
/// against the command-injection false-positive family in
/// artifacts/mimosa-command-injection-audit-20261021/report.md.
/// </summary>
public sealed class PowerToysPeekPreviewService
{
    internal const string PeekProcessName = "PowerToys.Peek.UI";
    internal const string PeekExecutableName = "PowerToys.Peek.UI.exe";

    // Command-line activation shipped in PowerToys 0.96 (merged 2025-11).
    private static readonly Version MinimumVersion = new(0, 96);

    // Peek's window takes a few dozen ms to appear after a toggle
    // activation; a visibility probe inside that window would misread an
    // open preview as closed and end a live session, so switches that
    // follow a successful toggle within this period are sent unprobed.
    internal const int ToggleGracePeriodMs = 300;

    private const string WinUi3AppsFolder = "WinUI3Apps";
    private const long NegativeProbeCacheTtlMs = 10_000;
    private const long NegativeProcessProbeTtlMs = 1_500;

    // Positive resolution cache (executable path that passed the version
    // gate), per process lifetime. Negative results use a short 10-second
    // TTL so repeated Space presses without PowerToys do not re-scan all
    // Uninstall registry hives synchronously on the UI thread, while an
    // in-session PowerToys install or upgrade still becomes active shortly.
    private static string? s_cachedExecutablePath;
    private static long s_lastNegativeProbeTicks;
    private static bool s_hasNegativeProbeCache;

    // Cached running PID + elevation state + short negative-lookup TTL so
    // Spacebar presses probe liveness via OpenProcess instead of taking
    // multiple full OS process-table LOH snapshots on the UI thread.
    private static int s_cachedRunningPid;
    private static int s_cachedPidElevationState; // 0 = unknown, 1 = standard, 2 = elevated
    private static long s_lastNegativeProcessProbeTicks;
    private static bool s_hasNegativeProcessProbeCache;

    private static bool s_integrityHintLogged;

    // UI-thread confined, like every other preview call site.
    private long _lastToggleStartedTicks;

    public bool CanPreview(string? path) =>
        QuickLookPreviewService.IsPreviewablePath(path) && IsAvailable();

    public Task<bool> TryToggleAsync(string path)
    {
        if (!QuickLookPreviewService.IsPreviewablePath(path))
        {
            App.Log($"[Peek] Path not previewable: '{path}'.");
            return Task.FromResult(false);
        }

        bool sent = TryActivate(path);
        if (sent)
        {
            _lastToggleStartedTicks = Environment.TickCount64;
        }

        return Task.FromResult(sent);
    }

    /// <summary>
    /// Changes the item shown by an existing Peek preview. Peek has no
    /// explicit switch command — re-sending a different path switches —
    /// but it also has no "ignore while closed" behavior, so the window
    /// visibility probe keeps a user-closed preview from being reopened
    /// by selection follow updates.
    /// </summary>
    public Task<bool> TrySwitchAsync(string path)
    {
        if (!QuickLookPreviewService.IsPreviewablePath(path))
        {
            App.Log($"[Peek] Path not previewable: '{path}'.");
            return Task.FromResult(false);
        }

        bool send = ShouldSendSwitch(
            IsPeekWindowVisible(),
            _lastToggleStartedTicks,
            Environment.TickCount64);
        if (!send)
        {
            App.Log("[Peek] Preview window not visible; switch suppressed.");
            return Task.FromResult(false);
        }

        return Task.FromResult(TryActivate(path));
    }

    /// <summary>
    /// Peek has no close command; re-sending the path the session last
    /// showed toggles the open preview closed. When the window is already
    /// gone there is nothing to send.
    /// </summary>
    public Task<bool> TryCloseAsync(string? currentPath)
    {
        if (string.IsNullOrWhiteSpace(currentPath) || !IsPeekWindowVisible())
        {
            return Task.FromResult(true);
        }

        return Task.FromResult(TryActivate(currentPath));
    }

    internal static bool ShouldSendSwitch(
        bool peekWindowVisible,
        long lastToggleStartedTicks,
        long nowTicks) =>
        lastToggleStartedTicks != 0
            ? nowTicks - lastToggleStartedTicks < ToggleGracePeriodMs ||
              peekWindowVisible
            : peekWindowVisible;

    /// <summary>
    /// Quoted single argument for the shell-integrity launcher, which
    /// builds its own command line from a string. Paths validated by
    /// IsPreviewablePath cannot contain quote characters, so quoting is
    /// the complete escaping.
    /// </summary>
    internal static string BuildShellIntegrityArguments(string path) =>
        string.Concat('"', path, '"');

    // ── Availability: running module process + version gate + integrity ──

    internal bool IsAvailable()
    {
        bool hadCachedExecutable = s_cachedExecutablePath is not null;
        return ResolveExecutablePath() is not null &&
               (!hadCachedExecutable || IsPeekProcessRunning()) &&
               !HasBlockingIntegrityMismatch();
    }

    private static string? ResolveExecutablePath()
    {
        if (s_cachedExecutablePath is { } cached)
        {
            return cached;
        }

        long nowTicks = Environment.TickCount64;
        if (s_hasNegativeProbeCache &&
            nowTicks - Volatile.Read(ref s_lastNegativeProbeTicks) < NegativeProbeCacheTtlMs)
        {
            return null;
        }

        if (!TryGetRunningPeekPid(out _, out string? processPath, capturePath: true))
        {
            RecordNegativeProbe(nowTicks);
            return null;
        }

        string? executablePath =
            FirstExistingExecutable(
                processPath,
                BuildKnownLocationExecutablePath(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
                BuildKnownLocationExecutablePath(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles))) ??
            FirstExistingExecutable(TryFindRegistryExecutablePath());
        if (executablePath is null)
        {
            RecordNegativeProbe(nowTicks);
            return null;
        }

        // The version gate runs on every negative probe: a pre-0.96
        // install must never receive arguments, whatever path resolved.
        if (!IsVersionSufficient(TryResolveVersion(executablePath)))
        {
            RecordNegativeProbe(nowTicks);
            return null;
        }

        s_hasNegativeProbeCache = false;
        s_cachedExecutablePath = executablePath;
        return executablePath;
    }

    private static void RecordNegativeProbe(long nowTicks)
    {
        Volatile.Write(ref s_lastNegativeProbeTicks, nowTicks);
        s_hasNegativeProbeCache = true;
    }

    internal static bool IsVersionSufficient(string? versionText) =>
        Version.TryParse(versionText?.Trim(), out Version? parsed) &&
        parsed >= MinimumVersion;

    private static string? TryResolveVersion(string executablePath)
    {
        try
        {
            string? fileVersion = FileVersionInfo.GetVersionInfo(executablePath).FileVersion;
            if (!string.IsNullOrWhiteSpace(fileVersion))
            {
                return fileVersion;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or
                                   System.IO.IOException)
        {
            // Fall through to the registry version.
        }

        return TryFindRegistryDisplayVersion();
    }

    // ── Activation ──

    private static bool TryActivate(string path)
    {
        string? executablePath = ResolveExecutablePath();
        if (executablePath is null)
        {
            App.Log("[Peek] Peek unavailable (not running, version below 0.96, or integrity mismatch).");
            return false;
        }

        // Re-verify the module process right before activating: once it
        // quits, an unconditional activation would cold-spawn Peek even
        // when the PowerToys module is disabled — the one way this
        // passive forwarder must never "start" PowerToys.
        if (!IsPeekProcessRunning())
        {
            App.Log("[Peek] Peek module process no longer running; activation skipped.");
            return false;
        }

        return ActivateResolvedExecutable(executablePath, path);
    }

    /// <summary>
    /// Every activation is parented to the Explorer shell's standard-user
    /// integrity via ShellUnelevatedLaunch — the same hand-down the
    /// updater uses. For an elevated DeskBox this avoids spawning an
    /// elevated Peek whose activation redirect into the runner's
    /// standard-integrity instance is unverified cross-integrity
    /// territory (and can strand a second preview window). For an
    /// unelevated DeskBox it inherits the live shell environment, which
    /// is exactly what the Explorer-hosted backend of
    /// Win32Helper.OpenFileOrChooseApp prefers for the same reason. There
    /// is deliberately no bare-spawn fallback: without a shell window
    /// there is no standard-integrity parent to inherit, and this passive
    /// forwarder must not cold-start Peek on its own.
    /// </summary>
    private static bool ActivateResolvedExecutable(string executablePath, string path)
    {
        bool started = ShellUnelevatedLaunch.TryStartAtShellIntegrity(
            executablePath,
            BuildShellIntegrityArguments(path),
            message => App.Log($"[Peek] {message}"));
        if (!started)
        {
            InvalidateCachedRunningPid();
            App.Log($"[Peek] Shell-integrity activation failed for '{path}'.");
        }

        return started;
    }

    // ── Process probes ──

    private static bool IsProcessIdAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        nint processHandle = Kernel32NativeMethods.OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId);
        if (processHandle == 0)
        {
            return false;
        }

        _ = Kernel32NativeMethods.CloseHandle(processHandle);
        return true;
    }

    private static void InvalidateCachedRunningPid()
    {
        Volatile.Write(ref s_cachedRunningPid, 0);
        Volatile.Write(ref s_cachedPidElevationState, 0);
    }

    private static bool TryGetRunningPeekPid(
        out int processId,
        out string? processPath,
        bool capturePath)
    {
        int cachedPid = Volatile.Read(ref s_cachedRunningPid);
        if (!capturePath && cachedPid > 0)
        {
            if (IsProcessIdAlive(cachedPid))
            {
                processId = cachedPid;
                processPath = null;
                return true;
            }

            InvalidateCachedRunningPid();
        }

        long nowTicks = Environment.TickCount64;
        if (Volatile.Read(ref s_hasNegativeProcessProbeCache) &&
            nowTicks - Volatile.Read(ref s_lastNegativeProcessProbeTicks) < NegativeProcessProbeTtlMs)
        {
            processId = 0;
            processPath = null;
            return false;
        }

        int foundPid = 0;
        string? foundPath = null;
        int elevationState = 0;
        try
        {
            Process[] processes = Process.GetProcessesByName(PeekProcessName);
            try
            {
                foreach (Process process in processes)
                {
                    if (process.HasExited)
                    {
                        continue;
                    }

                    if (foundPid == 0)
                    {
                        foundPid = process.Id;
                        if (TryGetProcessElevation(foundPid, out bool elevated))
                        {
                            elevationState = elevated ? 2 : 1;
                        }
                    }

                    if (capturePath && foundPath is null)
                    {
                        foundPath = TryGetProcessPath(process);
                    }
                }
            }
            finally
            {
                DisposeProcesses(processes);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or
                                   System.ComponentModel.Win32Exception or
                                   NotSupportedException)
        {
            // A process can exit while the snapshot is being inspected.
        }

        if (foundPid > 0)
        {
            Volatile.Write(ref s_hasNegativeProcessProbeCache, false);
            Volatile.Write(ref s_cachedPidElevationState, elevationState);
            Volatile.Write(ref s_cachedRunningPid, foundPid);
            processId = foundPid;
            processPath = foundPath;
            return true;
        }

        InvalidateCachedRunningPid();
        Volatile.Write(ref s_lastNegativeProcessProbeTicks, nowTicks);
        Volatile.Write(ref s_hasNegativeProcessProbeCache, true);
        processId = 0;
        processPath = null;
        return false;
    }

    private static bool IsPeekProcessRunning() =>
        TryGetRunningPeekPid(out _, out _, capturePath: false);

    /// <summary>
    /// True when any Peek process owns a visible top-level window. Peek
    /// hides (does not destroy) its window when the preview closes while
    /// the module process stays resident, so plain visibility is the
    /// "preview open" signal.
    /// </summary>
    internal static bool IsPeekWindowVisible()
    {
        if (!TryGetRunningPeekPid(out int runningPid, out _, capturePath: false))
        {
            return false;
        }

        uint targetPid = (uint)runningPid;
        bool visible = false;
        _ = Win32Helper.EnumWindows((hWnd, lParam) =>
        {
            _ = Win32Helper.GetWindowThreadProcessId(hWnd, out uint processId);
            if (processId == targetPid &&
                Win32Helper.IsWindowVisible(hWnd))
            {
                visible = true;
                return false;
            }

            return true;
        }, IntPtr.Zero);
        return visible;
    }

    // ── Integrity mismatch detection & one-time hint ──

    private static bool HasBlockingIntegrityMismatch()
    {
        if (!IsAnyPeekProcessElevated() ||
            ShellUnelevatedLaunch.IsCurrentProcessElevated())
        {
            return false;
        }

        if (!s_integrityHintLogged)
        {
            s_integrityHintLogged = true;
            App.Log("[Peek] Integrity mismatch detected: PowerToys Peek is running as administrator " +
                    "but DeskBox is not. Peek previews stay disabled to avoid spawning a second, " +
                    "unelevated Peek window next to the elevated one. " +
                    "To fix: run PowerToys as a normal user (not as administrator).");
        }

        return true;
    }

    private static bool IsAnyPeekProcessElevated()
    {
        if (!TryGetRunningPeekPid(out int runningPid, out _, capturePath: false))
        {
            return false;
        }

        int cachedState = Volatile.Read(ref s_cachedPidElevationState);
        if (cachedState != 0)
        {
            return cachedState == 2;
        }

        if (TryGetProcessElevation(runningPid, out bool elevated))
        {
            Volatile.Write(ref s_cachedPidElevationState, elevated ? 2 : 1);
            return elevated;
        }

        return false;
    }

    private static void DisposeProcesses(Process[] processes)
    {
        foreach (Process process in processes)
        {
            process.Dispose();
        }
    }

    // ── Path resolution helpers ──

    private static string? FirstExistingExecutable(params string?[] candidates)
    {
        foreach (string? candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                string.Equals(
                    Path.GetFileName(candidate),
                    PeekExecutableName,
                    StringComparison.OrdinalIgnoreCase) &&
                File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? TryGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            // Elevated or store-installed copies can deny module queries.
            return null;
        }
    }

    private static string? BuildKnownLocationExecutablePath(string installRoot) =>
        string.IsNullOrWhiteSpace(installRoot)
            ? null
            : Path.Combine(installRoot, "PowerToys", WinUi3AppsFolder, PeekExecutableName);

    /// <summary>
    /// Traditional uninstall keys miss the store (MSIX) install of
    /// PowerToys; they are only the fallback after the running process's
    /// own image path. Mirrors the EverythingInstallationDetector shape.
    /// </summary>
    private static (string? InstallLocation, string? DisplayVersion)? TryFindPowerToysUninstallEntry()
    {
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? uninstall = baseKey.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null)
                    {
                        continue;
                    }

                    foreach (string subKeyName in uninstall.GetSubKeyNames())
                    {
                        using RegistryKey? appKey = uninstall.OpenSubKey(subKeyName);
                        if (appKey?.GetValue("DisplayName") is not string displayName ||
                            displayName.IndexOf("PowerToys", StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        string? installLocation = appKey.GetValue("InstallLocation") as string;
                        if (string.IsNullOrWhiteSpace(installLocation))
                        {
                            continue;
                        }

                        return (installLocation, appKey.GetValue("DisplayVersion") as string);
                    }
                }
                catch
                {
                    // Missing or inaccessible registry views are normal.
                }
            }
        }

        return null;
    }

    private static string? TryFindRegistryExecutablePath() =>
        TryFindPowerToysUninstallEntry()?.InstallLocation is { } installLocation
            ? Path.Combine(installLocation, WinUi3AppsFolder, PeekExecutableName)
            : null;

    private static string? TryFindRegistryDisplayVersion() =>
        TryFindPowerToysUninstallEntry()?.DisplayVersion;

    // ── Win32 interop (elevation & liveness probe only) ──

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationInformationClass = 20;

    private static bool TryGetProcessElevation(int processId, out bool elevated)
    {
        elevated = false;
        nint processHandle = Kernel32NativeMethods.OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            processId);
        if (processHandle == 0)
        {
            return false;
        }

        try
        {
            if (!AdvApi32NativeMethods.OpenProcessToken(
                    processHandle,
                    TokenQuery,
                    out nint tokenHandle))
            {
                return false;
            }

            try
            {
                if (!AdvApi32NativeMethods.GetTokenInformation(
                        tokenHandle,
                        TokenElevationInformationClass,
                        out AdvApi32NativeMethods.TokenElevation elevation,
                        System.Runtime.InteropServices.Marshal.SizeOf<AdvApi32NativeMethods.TokenElevation>(),
                        out _))
                {
                    return false;
                }

                elevated = elevation.TokenIsElevated != 0;
                return true;
            }
            finally
            {
                _ = Kernel32NativeMethods.CloseHandle(tokenHandle);
            }
        }
        finally
        {
            _ = Kernel32NativeMethods.CloseHandle(processHandle);
        }
    }
}
