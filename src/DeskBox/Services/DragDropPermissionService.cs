using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
#if !DESKBOX_NATIVE_AOT
using System.Text;
#endif
using DeskBox.Helpers;
using Microsoft.Win32;

namespace DeskBox.Services;

public enum DragDropDiagnosticSeverity
{
    Ok,
    Warning,
    Error
}

public enum DragDropDiagnosticIssue
{
    None,
    UacDisabled,
    PermissionMismatch,
    AppCompatIssue,
    StartupShortcutIssue
}

public sealed record DragDropPermissionDiagnostic(
    DragDropDiagnosticSeverity Severity,
    DragDropDiagnosticIssue Issue,
    string Summary,
    string Detail,
    string CurrentProcessIntegrity,
    string ExplorerIntegrity,
    string UacStatus,
    string AppCompatStatus,
    string StartupStatus,
    string ShortcutStatus,
    bool IsCurrentProcessElevated,
    bool IsExplorerElevated,
    bool IsUacDisabled,
    bool HasAppCompatIssue,
    bool HasStartupIssue,
    bool HasShortcutIssue,
    bool NeedsRelaunch);

public sealed record DragDropPermissionRepairResult(
    bool Success,
    bool NeedsRelaunch,
    int RepairedCount,
    string FailureMessage,
    bool RequiresStartupSettings);

public static class DragDropPermissionService
{
    private const string AppCompatLayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    private const string UacPolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string AppName = "DeskBox";
    private const uint TokenQuery = 0x0008;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int SecurityMandatoryLowRid = 0x1000;
    private const int SecurityMandatoryMediumRid = 0x2000;
    private const int SecurityMandatoryHighRid = 0x3000;
    private const int SecurityMandatorySystemRid = 0x4000;
    private const int SecurityMandatoryProtectedProcessRid = 0x5000;

    private static readonly string[] IncompatibleAppCompatTokens =
    [
        "RUNASADMIN",
        "WIN95",
        "WIN98",
        "WIN2000",
        "WINXPSP2",
        "WINXPSP3",
        "VISTARTM",
        "VISTASP1",
        "VISTASP2",
        "WIN7RTM",
        "WIN8RTM"
    ];

    public static DragDropPermissionDiagnostic Diagnose(LocalizationService? localizationService = null)
    {
        string currentExePath = GetCurrentExePath();
        ProcessTokenSnapshot currentToken = GetCurrentProcessTokenSnapshot();
        ProcessTokenSnapshot explorerToken = GetExplorerTokenSnapshot();
        UacPolicySnapshot uacPolicy = GetUacPolicySnapshot();
        List<AppCompatEntry> appCompatEntries = GetRelevantAppCompatEntries(currentExePath);
        string? startupValue = StartupService.GetRunValue();
        StartupRegistrationState startupState = StartupService.GetState();
        List<ShortcutProbe> shortcutProbes = GetShortcutProbes(currentExePath, localizationService);

        bool isUacDisabled = uacPolicy.EnableLua == 0;
        bool hasAppCompatIssue = appCompatEntries.Any(entry => ContainsIncompatibleAppCompatToken(entry.Value));
        bool hasStartupIssue =
            IsStartupValueSuspicious(startupValue, currentExePath) ||
            startupState is StartupRegistrationState.PathMismatch or
                StartupRegistrationState.BlockedOrFailed;
        bool hasShortcutIssue = shortcutProbes.Any(probe => probe.HasIssue);
        bool permissionMismatch =
            currentToken.IsElevated == true &&
            explorerToken.IsElevated == false;
        bool needsRelaunch =
            currentToken.IsElevated == true &&
            (!isUacDisabled || explorerToken.IsElevated == false);

        DragDropDiagnosticSeverity severity =
            isUacDisabled || permissionMismatch || hasAppCompatIssue || hasStartupIssue || hasShortcutIssue
                ? DragDropDiagnosticSeverity.Warning
                : DragDropDiagnosticSeverity.Ok;

        DragDropDiagnosticIssue issue;
        string summary;
        string detail;
        if (isUacDisabled)
        {
            issue = DragDropDiagnosticIssue.UacDisabled;
            summary = Text(localizationService, "Settings.DragDropPermission.Summary.UacDisabled");
            detail = Text(localizationService, "Settings.DragDropPermission.Detail.UacDisabled");
        }
        else if (permissionMismatch)
        {
            issue = DragDropDiagnosticIssue.PermissionMismatch;
            summary = Text(localizationService, "Settings.DragDropPermission.Summary.PermissionMismatch");
            detail = Text(localizationService, "Settings.DragDropPermission.Detail.PermissionMismatch");
        }
        else if (hasAppCompatIssue)
        {
            issue = DragDropDiagnosticIssue.AppCompatIssue;
            summary = Text(localizationService, "Settings.DragDropPermission.Summary.AppCompatIssue");
            detail = Text(localizationService, "Settings.DragDropPermission.Detail.AppCompatIssue");
        }
        else if (hasStartupIssue || hasShortcutIssue)
        {
            issue = DragDropDiagnosticIssue.StartupShortcutIssue;
            summary = Text(localizationService, "Settings.DragDropPermission.Summary.StartupShortcutIssue");
            detail = Text(localizationService, "Settings.DragDropPermission.Detail.StartupShortcutIssue");
        }
        else
        {
            issue = DragDropDiagnosticIssue.None;
            summary = Text(localizationService, "Settings.DragDropPermission.Summary.Ok");
            detail = Text(localizationService, "Settings.DragDropPermission.Detail.Ok");
        }

        return new DragDropPermissionDiagnostic(
            severity,
            issue,
            summary,
            detail,
            FormatProcessToken("DeskBox", currentToken, localizationService),
            FormatProcessToken("Explorer", explorerToken, localizationService),
            FormatUacPolicy(uacPolicy, localizationService),
            FormatAppCompatStatus(appCompatEntries, localizationService),
            FormatStartupStatus(
                startupState,
                startupValue,
                currentExePath,
                localizationService),
            FormatShortcutStatus(shortcutProbes, localizationService),
            currentToken.IsElevated == true,
            explorerToken.IsElevated == true,
            isUacDisabled,
            hasAppCompatIssue,
            hasStartupIssue,
            hasShortcutIssue,
            needsRelaunch);
    }

    public static DragDropPermissionRepairResult Repair(SettingsService settingsService)
    {
        string currentExePath = GetCurrentExePath();
        var before = Diagnose();
        int repairedCount = 0;
        List<string> failures = [];
        bool requiresStartupSettings = false;

        foreach (var entry in GetRelevantAppCompatEntries(currentExePath))
        {
            if (!ContainsIncompatibleAppCompatToken(entry.Value))
            {
                continue;
            }

            try
            {
                if (entry.Root.OpenSubKey(AppCompatLayersKey, writable: true) is { } key)
                {
                    using (key)
                    {
                        key.DeleteValue(entry.ExePath, throwOnMissingValue: false);
                    }

                    repairedCount++;
                    App.Log($"[DragDropPermission] Removed AppCompat layer root={entry.RootName} path='{entry.ExePath}' value='{entry.Value}'");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"AppCompat {entry.ExePath}: {ex.Message}");
            }
        }

        try
        {
            StartupRegistrationState startupStateBefore = StartupService.GetState();
            if (settingsService.Settings.AutoStart ||
                startupStateBefore == StartupRegistrationState.Enabled)
            {
                StartupOperationResult startupResult = StartupService.Enable();
                if (startupResult.State == StartupRegistrationState.Enabled)
                {
                    if (startupStateBefore != StartupRegistrationState.Enabled)
                    {
                        repairedCount++;
                    }
                }
                else
                {
                    requiresStartupSettings =
                        startupResult.RequiresSystemSettings;
                    string startupFailure = string.IsNullOrWhiteSpace(
                        startupResult.ErrorMessage)
                        ? $"Startup remained in state {startupResult.State}."
                        : startupResult.ErrorMessage;
                    failures.Add($"Startup: {startupFailure}");
                }
            }
        }
        catch (Exception ex)
        {
            failures.Add($"Startup: {ex.Message}");
        }

        foreach (var shortcut in GetKnownShortcutDefinitions())
        {
            try
            {
                if (!shortcut.EnsureExists && !File.Exists(shortcut.Path))
                {
                    continue;
                }

                CreateOrUpdateShortcut(shortcut.Path, currentExePath, shortcut.Arguments);
                repairedCount++;
                App.Log($"[DragDropPermission] Rewrote shortcut '{shortcut.Path}' target='{currentExePath}' args='{shortcut.Arguments}'");
            }
            catch (Exception ex)
            {
                failures.Add($"Shortcut {shortcut.Path}: {ex.Message}");
            }
        }

        var after = Diagnose();
        string failureMessage = failures.Count == 0 ? string.Empty : string.Join("; ", failures);

        bool needsRelaunch = before.NeedsRelaunch || after.NeedsRelaunch || before.IsCurrentProcessElevated;
        return new DragDropPermissionRepairResult(
            failures.Count == 0,
            needsRelaunch,
            repairedCount,
            failureMessage,
            requiresStartupSettings);
    }

    public static bool TryRelaunchAsExplorerUser()
    {
        string exePath = GetCurrentExePath();
        if (!File.Exists(exePath))
        {
            return false;
        }

        IntPtr shellWindow = GetShellWindow();
        if (shellWindow == IntPtr.Zero)
        {
            return TryShellExecute(exePath);
        }

        GetWindowThreadProcessId(shellWindow, out uint explorerProcessId);
        if (explorerProcessId == 0)
        {
            return TryShellExecute(exePath);
        }

        IntPtr processHandle = OpenProcess(ProcessCreateProcess | ProcessQueryLimitedInformation, false, explorerProcessId);
        if (processHandle == IntPtr.Zero)
        {
            return TryShellExecute(exePath);
        }

        try
        {
            var startupInfo = new StartupInfo();
            startupInfo.cb = Marshal.SizeOf<StartupInfo>();
            var processInformation = new ProcessInformation();
            int attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            IntPtr attributeList = Marshal.AllocHGlobal(attributeListSize);
            try
            {
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                {
                    return TryShellExecute(exePath);
                }

                IntPtr parentProcessValue = processHandle;
                if (!UpdateProcThreadAttribute(
                        attributeList,
                        0,
                        (IntPtr)ProcThreadAttributeParentProcess,
                        ref parentProcessValue,
                        (IntPtr)IntPtr.Size,
                        IntPtr.Zero,
                        IntPtr.Zero))
                {
                    return TryShellExecute(exePath);
                }

                startupInfo.lpAttributeList = attributeList;
                string commandLine = $"\"{exePath}\"";
                bool created = CreateProcess(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent,
                    IntPtr.Zero,
                    Path.GetDirectoryName(exePath),
                    ref startupInfo,
                    out processInformation);

                if (!created)
                {
                    return TryShellExecute(exePath);
                }

                CloseHandle(processInformation.hThread);
                CloseHandle(processInformation.hProcess);
                return true;
            }
            finally
            {
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }
            }
        }
        finally
        {
            CloseHandle(processHandle);
        }
    }

    private static bool TryShellExecute(string exePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory
            });
            return true;
        }
        catch (Exception ex)
        {
            App.Log($"[DragDropPermission] Relaunch failed: {ex}");
            return false;
        }
    }

    private static string GetCurrentExePath()
    {
        return Environment.ProcessPath
            ?? Path.Combine(AppContext.BaseDirectory, "DeskBox.exe");
    }

    private static bool IsStartupValueSuspicious(string? startupValue, string currentExePath)
    {
        if (string.IsNullOrWhiteSpace(startupValue))
        {
            return false;
        }

        string? path = ExtractExecutablePath(startupValue);
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        return !string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentExePath), StringComparison.OrdinalIgnoreCase) ||
               ContainsIncompatibleAppCompatToken(startupValue);
    }

    private static string? ExtractExecutablePath(string commandLine)
    {
        string trimmed = commandLine.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed[0] == '"')
        {
            int endQuote = trimmed.IndexOf('"', 1);
            return endQuote > 1 ? trimmed[1..endQuote] : null;
        }

        int exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex >= 0)
        {
            return trimmed[..(exeIndex + 4)];
        }

        int firstSpace = trimmed.IndexOf(' ');
        return firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
    }

    private static List<AppCompatEntry> GetRelevantAppCompatEntries(string currentExePath)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            currentExePath,
            Path.Combine(AppContext.BaseDirectory, "DeskBox.exe"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "DeskBox",
                "DeskBox.exe")
        };

        string? startupPath = ExtractExecutablePath(StartupService.GetRunValue() ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(startupPath))
        {
            paths.Add(startupPath);
        }

        var entries = new List<AppCompatEntry>();
        AddAppCompatEntries(entries, Registry.CurrentUser, "HKCU", paths);
        AddAppCompatEntries(entries, Registry.LocalMachine, "HKLM", paths);
        return entries;
    }

    private static void AddAppCompatEntries(List<AppCompatEntry> entries, RegistryKey root, string rootName, HashSet<string> relevantPaths)
    {
        try
        {
            using var key = root.OpenSubKey(AppCompatLayersKey);
            if (key is null)
            {
                return;
            }

            foreach (string valueName in key.GetValueNames())
            {
                if (!IsRelevantDeskBoxPath(valueName, relevantPaths))
                {
                    continue;
                }

                if (key.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value))
                {
                    entries.Add(new AppCompatEntry(root, rootName, valueName, value));
                }
            }
        }
        catch (Exception ex)
        {
            App.Log($"[DragDropPermission] Failed to read AppCompat {rootName}: {ex.Message}");
        }
    }

    private static bool IsRelevantDeskBoxPath(string valueName, HashSet<string> relevantPaths)
    {
        if (relevantPaths.Contains(valueName))
        {
            return true;
        }

        string fileName = Path.GetFileName(valueName);
        return string.Equals(fileName, "DeskBox.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsIncompatibleAppCompatToken(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               IncompatibleAppCompatTokens.Any(token => value.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static string FormatAppCompatStatus(List<AppCompatEntry> entries, LocalizationService? localizationService)
    {
        if (entries.Count == 0)
        {
            return Text(localizationService, "Settings.DragDropPermission.Status.NoAppCompatFlags");
        }

        return string.Join(Environment.NewLine, entries.Select(entry =>
            $"{entry.RootName}: {entry.Value} ({entry.ExePath})"));
    }

    private static string FormatStartupStatus(
        StartupRegistrationState startupState,
        string? startupValue,
        string currentExePath,
        LocalizationService? localizationService)
    {
        if (startupState == StartupRegistrationState.DisabledByUser)
        {
            return Text(localizationService, "Settings.AutoStart.WindowsDisabled");
        }

        if (startupState == StartupRegistrationState.Pending)
        {
            return Text(localizationService, "Settings.AutoStart.Pending");
        }

        if (string.IsNullOrWhiteSpace(startupValue))
        {
            return Text(localizationService, "Settings.DragDropPermission.Status.StartupDisabled");
        }

        return IsStartupValueSuspicious(startupValue, currentExePath)
            ? Format(localizationService, "Settings.DragDropPermission.Status.Check", startupValue)
            : Format(localizationService, "Settings.DragDropPermission.Status.Normal", startupValue);
    }

    private static List<ShortcutProbe> GetShortcutProbes(string currentExePath, LocalizationService? localizationService)
    {
        var probes = new List<ShortcutProbe>();
        foreach (var shortcut in GetKnownShortcutDefinitions())
        {
            string shortcutPath = shortcut.Path;
            if (!File.Exists(shortcutPath))
            {
                continue;
            }

            bool hasIssue = false;
            string reason = Text(localizationService, "Settings.DragDropPermission.Status.Present");
            try
            {
                if (TryReadShortcut(shortcutPath, out string? targetPath, out string? arguments))
                {
                    hasIssue =
                        !string.Equals(Path.GetFullPath(targetPath), Path.GetFullPath(currentExePath), StringComparison.OrdinalIgnoreCase) ||
                        !string.Equals(arguments?.Trim() ?? string.Empty, shortcut.Arguments.Trim(), StringComparison.OrdinalIgnoreCase);
                    reason = hasIssue
                        ? Format(localizationService, "Settings.DragDropPermission.Status.Target", $"{targetPath} {arguments}".Trim())
                        : Text(localizationService, "Settings.DragDropPermission.Status.TargetNormal");
                }
                else
                {
                    hasIssue = true;
                    reason = Text(localizationService, "Settings.DragDropPermission.Status.ShortcutTargetUnreadable");
                }
            }
            catch (Exception ex)
            {
                hasIssue = true;
                reason = Format(localizationService, "Settings.DragDropPermission.Status.CheckFailed", ex.Message);
            }

            probes.Add(new ShortcutProbe(shortcutPath, hasIssue, reason));
        }

        return probes;
    }

    private static string[] GetKnownShortcutPaths()
    {
        return GetKnownShortcutDefinitions().Select(definition => definition.Path).ToArray();
    }

    private static ShortcutDefinition[] GetKnownShortcutDefinitions()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
        return
        [
            new ShortcutDefinition(Path.Combine(desktop, $"{AppName}.lnk"), string.Empty, false),
            new ShortcutDefinition(Path.Combine(programs, $"{AppName}.lnk"), string.Empty, true)
        ];
    }

    private static string FormatShortcutStatus(List<ShortcutProbe> probes, LocalizationService? localizationService)
    {
        if (probes.Count == 0)
        {
            return Text(localizationService, "Settings.DragDropPermission.Status.NoShortcuts");
        }

        return string.Join(Environment.NewLine, probes.Select(probe =>
            Format(localizationService,
                probe.HasIssue ? "Settings.DragDropPermission.Status.CheckPath" : "Settings.DragDropPermission.Status.NormalPath",
                probe.Path,
                probe.Reason)));
    }

    private static bool TryReadShortcut(string shortcutPath, out string targetPath, out string arguments)
    {
#if DESKBOX_NATIVE_AOT
        ShortcutNativeCallResult native =
            ShortcutNativeBackend.ReadEffectiveDiagnostic(shortcutPath);
        targetPath = native.Metadata?.TargetPath ?? string.Empty;
        arguments = native.Metadata?.Arguments ?? string.Empty;
        if (!native.Success)
        {
            App.Log(
                $"[DragDropPermission] Rust shortcut read failed for " +
                $"'{shortcutPath}': {native.Failure}; {native.Detail}");
        }

        return native.Success;
#else
        if (ShortcutBackendPolicy.Current == ShortcutBackendMode.Rust)
        {
            ShortcutNativeCallResult native =
                ShortcutNativeBackend.ReadEffectiveDiagnostic(shortcutPath);
            targetPath = native.Metadata?.TargetPath ?? string.Empty;
            arguments = native.Metadata?.Arguments ?? string.Empty;
            if (!native.Success)
            {
                App.Log(
                    $"[DragDropPermission] Explicit Rust shortcut read failed for " +
                    $"'{shortcutPath}': {native.Failure}; {native.Detail}");
            }

            return native.Success;
        }

        return TryReadShortcutWithCSharp(shortcutPath, out targetPath, out arguments);
#endif
    }

#if !DESKBOX_NATIVE_AOT
    internal static bool TryReadShortcutWithCSharp(
        string shortcutPath,
        out string targetPath,
        out string arguments)
    {
        targetPath = string.Empty;
        arguments = string.Empty;

        try
        {
            var shellLink = (IShellLinkW)(object)new ShellLink();
            var persistFile = (System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink;
            persistFile.Load(shortcutPath, 0);

            var pathBuffer = new StringBuilder(260);
            IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<Win32FindDataW>());
            try
            {
                shellLink.GetPath(pathBuffer, pathBuffer.Capacity, data, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(data);
            }

            var argumentBuffer = new StringBuilder(512);
            shellLink.GetArguments(argumentBuffer, argumentBuffer.Capacity);
            targetPath = pathBuffer.ToString().Trim();
            arguments = argumentBuffer.ToString().Trim();
            return !string.IsNullOrWhiteSpace(targetPath);
        }
        catch (Exception ex)
        {
            App.Log($"[DragDropPermission] Failed to read shortcut '{shortcutPath}': {ex.Message}");
            return false;
        }
    }
#endif

    internal static void CreateOrUpdateShortcut(string shortcutPath, string targetPath, string arguments)
    {
        string? directory = Path.GetDirectoryName(shortcutPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string workingDirectory = Path.GetDirectoryName(targetPath) ?? AppContext.BaseDirectory;
        // An empty icon location lets the shell resolve the source file's own
        // icon (embedded icon for executables, type association for documents)
        // instead of stamping every shortcut with the DeskBox branding.
        string iconPath = string.Empty;
#if DESKBOX_NATIVE_AOT
        var metadata = new ShortcutInfo(
            targetPath,
            string.Empty,
            arguments,
            workingDirectory,
            iconPath,
            0);
        ShortcutNativeWriteCallResult native =
            ShortcutNativeBackend.WriteShortcut(shortcutPath, metadata);
        if (!native.Success)
        {
            App.Log(
                $"[DragDropPermission] Rust shortcut write failed for " +
                $"'{shortcutPath}': {native.Failure}; {native.Detail}");
            throw new InvalidOperationException(
                $"Rust shortcut write failed: {native.Failure}; {native.Detail}");
        }
#else
        if (ShortcutBackendPolicy.Current == ShortcutBackendMode.Rust)
        {
            var metadata = new ShortcutInfo(
                targetPath,
                string.Empty,
                arguments,
                workingDirectory,
                iconPath,
                0);
            ShortcutNativeWriteCallResult native =
                ShortcutNativeBackend.WriteShortcut(shortcutPath, metadata);
            if (!native.Success)
            {
                App.Log(
                    $"[DragDropPermission] Explicit Rust shortcut write failed for " +
                    $"'{shortcutPath}': {native.Failure}; {native.Detail}");
                throw new InvalidOperationException(
                    $"Rust shortcut write failed: {native.Failure}; {native.Detail}");
            }
        }
        else
        {
            CreateOrUpdateShortcutWithCSharp(
                shortcutPath,
                targetPath,
                arguments,
                workingDirectory,
                iconPath);
        }
#endif

        ShortcutHelper.InvalidateStoredMetadataCache(shortcutPath);
    }

#if !DESKBOX_NATIVE_AOT
    internal static void CreateOrUpdateShortcutWithCSharp(
        string shortcutPath,
        string targetPath,
        string arguments,
        string workingDirectory,
        string iconPath)
    {
        var shellLink = (IShellLinkW)(object)new ShellLink();
        shellLink.SetPath(targetPath);
        shellLink.SetArguments(arguments);
        shellLink.SetWorkingDirectory(workingDirectory);
        if (!string.IsNullOrEmpty(iconPath))
        {
            shellLink.SetIconLocation(iconPath, 0);
        }

        var persistFile = (System.Runtime.InteropServices.ComTypes.IPersistFile)shellLink;
        persistFile.Save(shortcutPath, true);
    }
#endif

    private static ProcessTokenSnapshot GetCurrentProcessTokenSnapshot()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            IntPtr currentProcess = GetCurrentProcess();
            return CreateProcessTokenSnapshot(
                Environment.ProcessId,
                "DeskBox",
                principal.IsInRole(WindowsBuiltInRole.Administrator),
                currentProcess);
        }
        catch (Exception ex)
        {
            return ProcessTokenSnapshot.Unknown(Environment.ProcessId, "DeskBox", ex.Message);
        }
    }

    private static ProcessTokenSnapshot GetExplorerTokenSnapshot()
    {
        try
        {
            Process? explorer = Process.GetProcessesByName("explorer").FirstOrDefault();
            if (explorer is null)
            {
                return ProcessTokenSnapshot.Unknown(0, "explorer", "not-running");
            }

            IntPtr processHandle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)explorer.Id);
            if (processHandle == IntPtr.Zero)
            {
                return ProcessTokenSnapshot.Unknown(explorer.Id, explorer.ProcessName, $"open-error:{Marshal.GetLastWin32Error()}");
            }

            try
            {
                return CreateProcessTokenSnapshot(explorer.Id, explorer.ProcessName, null, processHandle);
            }
            finally
            {
                CloseHandle(processHandle);
            }
        }
        catch (Exception ex)
        {
            return ProcessTokenSnapshot.Unknown(0, "explorer", ex.Message);
        }
    }

    private static ProcessTokenSnapshot CreateProcessTokenSnapshot(int processId, string processName, bool? isAdminRole, IntPtr processHandle)
    {
        bool? isElevated = TryGetTokenElevation(processHandle, out bool elevated) ? elevated : null;
        string integrity = TryGetIntegrityLevel(processHandle, out string level) ? level : "unknown";
        return new ProcessTokenSnapshot(processId, processName, isAdminRole, isElevated, integrity, null);
    }

    private static string FormatProcessToken(string label, ProcessTokenSnapshot snapshot, LocalizationService? localizationService)
    {
        string elevated = snapshot.IsElevated is null
            ? Text(localizationService, "Settings.DragDropPermission.Status.Unknown")
            : snapshot.IsElevated.Value
                ? Text(localizationService, "Settings.DragDropPermission.Status.Administrator")
                : Text(localizationService, "Settings.DragDropPermission.Status.Standard");
        string adminRole = snapshot.IsAdminRole is null
            ? string.Empty
            : snapshot.IsAdminRole.Value
                ? Text(localizationService, "Settings.DragDropPermission.Status.AdminGroup")
                : Text(localizationService, "Settings.DragDropPermission.Status.NonAdminGroup");
        string error = string.IsNullOrWhiteSpace(snapshot.Error)
            ? string.Empty
            : Format(localizationService, "Settings.DragDropPermission.Status.ErrorSuffix", snapshot.Error);
        string integrity = string.Equals(snapshot.IntegrityLevel, "unknown", StringComparison.OrdinalIgnoreCase)
            ? Text(localizationService, "Settings.DragDropPermission.Status.UnknownValue")
            : snapshot.IntegrityLevel;
        return Format(localizationService, "Settings.DragDropPermission.Status.Process", label, elevated, integrity, adminRole, error);
    }

    private static UacPolicySnapshot GetUacPolicySnapshot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(UacPolicyKey);
            return new UacPolicySnapshot(
                ReadIntValue(key, "EnableLUA"),
                ReadIntValue(key, "ConsentPromptBehaviorAdmin"),
                ReadIntValue(key, "PromptOnSecureDesktop"),
                null);
        }
        catch (Exception ex)
        {
            return new UacPolicySnapshot(null, null, null, ex.Message);
        }
    }

    private static int? ReadIntValue(RegistryKey? key, string name)
    {
        object? value = key?.GetValue(name);
        return value switch
        {
            int intValue => intValue,
            string stringValue when int.TryParse(stringValue, out int parsed) => parsed,
            _ => null
        };
    }

    private static string FormatUacPolicy(UacPolicySnapshot policy, LocalizationService? localizationService)
    {
        if (!string.IsNullOrWhiteSpace(policy.Error))
        {
            return Format(localizationService, "Settings.DragDropPermission.Status.ReadFailed", policy.Error);
        }

        string enableLua = policy.EnableLua switch
        {
            0 => Text(localizationService, "Settings.DragDropPermission.Status.UacNeverNotify"),
            1 => Text(localizationService, "Settings.DragDropPermission.Status.UacEnabled"),
            _ => Text(localizationService, "Settings.DragDropPermission.Status.Unknown")
        };

        return Format(localizationService, "Settings.DragDropPermission.Status.UacDetails", enableLua, FormatNullable(policy.ConsentPromptBehaviorAdmin, localizationService), FormatNullable(policy.PromptOnSecureDesktop, localizationService));
    }

    private static string FormatNullable(int? value, LocalizationService? localizationService)
    {
        return value?.ToString() ?? Text(localizationService, "Settings.DragDropPermission.Status.UnknownValue");
    }

    private static string Text(LocalizationService? localizationService, string key)
    {
        return localizationService?.T(key) ?? LocalizationService.DefaultText(key);
    }

    private static string Format(LocalizationService? localizationService, string key, params object[] args)
    {
        return localizationService?.Format(key, args) ?? LocalizationService.DefaultFormat(key, args);
    }

    private static bool TryGetTokenElevation(IntPtr processHandle, out bool isElevated)
    {
        isElevated = false;
        if (!OpenProcessToken(processHandle, TokenQuery, out IntPtr tokenHandle))
        {
            return false;
        }

        try
        {
            int length = Marshal.SizeOf<TokenElevation>();
            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(tokenHandle, TokenInformationClass.TokenElevation, buffer, length, out _))
                {
                    return false;
                }

                var elevation = Marshal.PtrToStructure<TokenElevation>(buffer);
                isElevated = elevation.TokenIsElevated != 0;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    private static bool TryGetIntegrityLevel(IntPtr processHandle, out string level)
    {
        level = string.Empty;
        if (!OpenProcessToken(processHandle, TokenQuery, out IntPtr tokenHandle))
        {
            return false;
        }

        try
        {
            _ = GetTokenInformation(tokenHandle, TokenInformationClass.TokenIntegrityLevel, IntPtr.Zero, 0, out int length);
            if (length <= 0)
            {
                return false;
            }

            IntPtr buffer = Marshal.AllocHGlobal(length);
            try
            {
                if (!GetTokenInformation(tokenHandle, TokenInformationClass.TokenIntegrityLevel, buffer, length, out _))
                {
                    return false;
                }

                var label = Marshal.PtrToStructure<TokenMandatoryLabel>(buffer);
                IntPtr subAuthorityCount = GetSidSubAuthorityCount(label.Label.Sid);
                if (subAuthorityCount == IntPtr.Zero)
                {
                    return false;
                }

                byte count = Marshal.ReadByte(subAuthorityCount);
                if (count == 0)
                {
                    return false;
                }

                IntPtr integrityRidPointer = GetSidSubAuthority(label.Label.Sid, (uint)(count - 1));
                int integrityRid = Marshal.ReadInt32(integrityRidPointer);
                level = FormatIntegrityLevel(integrityRid);
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    private static string FormatIntegrityLevel(int integrityRid)
    {
        return integrityRid switch
        {
            < SecurityMandatoryLowRid => $"Untrusted(0x{integrityRid:X})",
            < SecurityMandatoryMediumRid => $"Low(0x{integrityRid:X})",
            < SecurityMandatoryHighRid => $"Medium(0x{integrityRid:X})",
            < SecurityMandatorySystemRid => $"High(0x{integrityRid:X})",
            < SecurityMandatoryProtectedProcessRid => $"System(0x{integrityRid:X})",
            _ => $"Protected(0x{integrityRid:X})"
        };
    }

    private sealed record ProcessTokenSnapshot(
        int ProcessId,
        string ProcessName,
        bool? IsAdminRole,
        bool? IsElevated,
        string IntegrityLevel,
        string? Error)
    {
        public static ProcessTokenSnapshot Unknown(int processId, string processName, string error)
        {
            return new ProcessTokenSnapshot(processId, processName, null, null, "unknown", error);
        }
    }

    private sealed record UacPolicySnapshot(
        int? EnableLua,
        int? ConsentPromptBehaviorAdmin,
        int? PromptOnSecureDesktop,
        string? Error);

    private sealed record AppCompatEntry(
        RegistryKey Root,
        string RootName,
        string ExePath,
        string Value);

    private sealed record ShortcutProbe(
        string Path,
        bool HasIssue,
        string Reason);

    private sealed record ShortcutDefinition(
        string Path,
        string Arguments,
        bool EnsureExists);

    private enum TokenInformationClass
    {
        TokenElevation = 20,
        TokenIntegrityLevel = 25
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenElevation
    {
        public int TokenIsElevated;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public SidAndAttributes Label;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

#if !DESKBOX_NATIVE_AOT
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindDataW
    {
        public uint dwFileAttributes;
        public long ftCreationTime;
        public long ftLastAccessTime;
        public long ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cchMaxPath,
            IntPtr pfd,
            uint fFlags);

        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription(string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory(string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments(string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation(string pszIconPath, int iIcon);
        void SetRelativePath(string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath(string pszFile);
    }
#endif

    private const uint ProcessCreateProcess = 0x0080;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const int ProcThreadAttributeParentProcess = 0x00020000;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(
        IntPtr tokenHandle,
        TokenInformationClass tokenInformationClass,
        IntPtr tokenInformation,
        int tokenInformationLength,
        out int returnLength);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint subAuthority);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref int lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        ref IntPtr lpValue,
        IntPtr cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);
}
