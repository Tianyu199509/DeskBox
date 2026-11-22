using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DeskBox.Platform;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace DeskBox.Services.Distribution;

internal sealed record PrimaryInstanceOwnerInfo(
    AppDistributionChannel Channel,
    int ProcessId,
    string RootPath,
    string ExecutablePath,
    string GracefulExitEventName)
{
    internal string FormatPayload()
    {
        var sb = new StringBuilder(256);
        sb.Append("channel=").AppendLine(Channel == AppDistributionChannel.MicrosoftStore ? "Store" : "Direct");
        sb.Append("pid=").Append(ProcessId).AppendLine();
        sb.Append("root=").AppendLine(RootPath ?? string.Empty);
        sb.Append("exe=").AppendLine(ExecutablePath ?? string.Empty);
        sb.Append("exitEvent=").AppendLine(GracefulExitEventName ?? string.Empty);
        return sb.ToString();
    }

    internal static bool TryParsePayload(string? payload, out PrimaryInstanceOwnerInfo? info)
    {
        info = null;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        AppDistributionChannel? channel = null;
        int pid = 0;
        string root = string.Empty;
        string exe = string.Empty;
        string exitEvent = string.Empty;

        foreach (string rawLine in payload.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            switch (key)
            {
                case "channel":
                    if (string.Equals(value, "Store", StringComparison.OrdinalIgnoreCase))
                    {
                        channel = AppDistributionChannel.MicrosoftStore;
                    }
                    else if (string.Equals(value, "Direct", StringComparison.OrdinalIgnoreCase))
                    {
                        channel = AppDistributionChannel.Direct;
                    }
                    break;
                case "pid":
                    int.TryParse(value, out pid);
                    break;
                case "root":
                    root = value;
                    break;
                case "exe":
                    exe = value;
                    break;
                case "exitEvent":
                    exitEvent = value;
                    break;
            }
        }

        if (channel is null || pid <= 0)
        {
            return false;
        }

        info = new PrimaryInstanceOwnerInfo(channel.Value, pid, root, exe, exitEvent);
        return true;
    }
}

internal sealed record ChannelCoexistenceState(
    bool OtherChannelInstalled,
    AppDistributionChannel CurrentChannel,
    AppDistributionChannel OtherChannel,
    string? OtherChannelDetailPath);

internal sealed record LegacyRunningChannelProcess(
    AppDistributionChannel Channel,
    int ProcessId,
    string ExecutablePath);

/// <summary>
/// Cross-channel (Direct desktop installer vs. Microsoft Store MSIX) detection,
/// primary-instance identity publication/handoff, and sibling-channel active
/// managed-folder discovery for orphan-cleanup safety.
/// </summary>
internal static class ChannelCoexistenceService
{
    internal const string StorePackageIdentityName = "D1FC332A.DeskBoxWidgets";
    private const string StorePackagesSubkey =
        @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";
    private const string DirectInstallSubkey = @"Software\DeskBox\DirectInstall";
    private const string DirectUninstallSubkey =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{5E052824-3456-427E-9759-3BCAE078A1D3}_is1";
    private const string DirectWowUninstallSubkey =
        @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\{5E052824-3456-427E-9759-3BCAE078A1D3}_is1";

    internal static SafeFileHandle? PublishPrimaryInstanceOwner(
        DeskBoxDataPathService dataPaths,
        AppDistributionChannel channel,
        int processId,
        string executablePath,
        Action<string>? log = null)
    {
        var info = new PrimaryInstanceOwnerInfo(
            channel,
            processId,
            dataPaths.RootPath,
            executablePath,
            dataPaths.BuildGracefulExitEventName(processId));
        return SingleInstanceAndActivationInterop.TryCreatePrimaryOwnerSharedMapping(
            dataPaths.PrimaryOwnerMappingName,
            info.FormatPayload(),
            log);
    }

    internal static PrimaryInstanceOwnerInfo? TryReadPrimaryInstanceOwner(
        DeskBoxDataPathService dataPaths,
        Action<string>? log = null,
        Func<int, bool>? processAliveProbe = null,
        int? currentProcessIdOverride = null,
        bool allowCurrentProcess = false)
    {
        string? raw = SingleInstanceAndActivationInterop.TryReadPrimaryOwnerSharedMapping(
            dataPaths.PrimaryOwnerMappingName,
            log);
        if (!PrimaryInstanceOwnerInfo.TryParsePayload(raw, out PrimaryInstanceOwnerInfo? info) || info is null)
        {
            return null;
        }

        int currentPid = currentProcessIdOverride ?? Environment.ProcessId;
        if (!allowCurrentProcess && info.ProcessId == currentPid)
        {
            log?.Invoke($"[ChannelHandoff] Ignoring stale primary-owner mapping matching current pid={info.ProcessId}.");
            return null;
        }

        Func<int, bool> aliveCheck = processAliveProbe ?? IsProcessAlive;
        if (!aliveCheck(info.ProcessId))
        {
            log?.Invoke($"[ChannelHandoff] Ignoring stale primary-owner mapping for exited pid={info.ProcessId}.");
            return null;
        }

        return info;
    }

    internal static bool IsProcessAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using Process proc = Process.GetProcessById(processId);
            try
            {
                return !proc.HasExited;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Elevated process queried from Medium IL: PID exists, HasExited is access-denied.
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;
        }
    }

    internal static AppDistributionChannel? ClassifyExecutableChannel(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        string normalized = executablePath.Trim().Replace('/', '\\');
        if (normalized.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase))
        {
            return AppDistributionChannel.MicrosoftStore;
        }

        return AppDistributionChannel.Direct;
    }

    /// <summary>
    /// Fallback detection when the primary instance is an older (pre-1.6.0)
    /// build that does not publish the shared primary-owner mapping. Scans
    /// same-session <c>DeskBox</c> processes and classifies their image path
    /// via <c>QueryFullProcessImageNameW</c>.
    /// </summary>
    internal static LegacyRunningChannelProcess? TryDetectLegacyRunningOppositeChannelProcess(
        AppDistributionChannel currentChannel,
        Func<IEnumerable<(int Pid, string ExecutablePath)>>? processEnumerator = null,
        int? currentProcessIdOverride = null)
    {
        int currentPid = currentProcessIdOverride ?? Environment.ProcessId;
        IEnumerable<(int Pid, string ExecutablePath)> candidates =
            processEnumerator is not null
                ? processEnumerator()
                : EnumerateSameSessionDeskBoxProcesses(currentPid);

        foreach ((int pid, string exePath) in candidates)
        {
            if (pid <= 0 || pid == currentPid)
            {
                continue;
            }

            AppDistributionChannel? candidateChannel = ClassifyExecutableChannel(exePath);
            if (candidateChannel is not null && candidateChannel.Value != currentChannel)
            {
                return new LegacyRunningChannelProcess(candidateChannel.Value, pid, exePath);
            }
        }

        return null;
    }

    private static List<(int Pid, string ExecutablePath)> EnumerateSameSessionDeskBoxProcesses(int currentPid)
    {
        var results = new List<(int Pid, string ExecutablePath)>();
        int currentSessionId;
        try
        {
            using Process current = Process.GetCurrentProcess();
            currentSessionId = current.SessionId;
        }
        catch
        {
            currentSessionId = -1;
        }

        Process[] processes;
        try
        {
            processes = Process.GetProcessesByName("DeskBox");
        }
        catch
        {
            return results;
        }

        foreach (Process proc in processes)
        {
            try
            {
                int pid = proc.Id;
                if (pid == currentPid)
                {
                    continue;
                }

                if (currentSessionId >= 0)
                {
                    try
                    {
                        if (proc.SessionId != currentSessionId)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }
                }

                string? exePath = SingleInstanceAndActivationInterop.TryQueryProcessExecutablePath(pid);
                if (!string.IsNullOrWhiteSpace(exePath))
                {
                    results.Add((pid, exePath));
                }
            }
            catch
            {
            }
            finally
            {
                proc.Dispose();
            }
        }

        return results;
    }

    internal static (Mutex? Mutex, bool Acquired) TrySignalGracefulExitAndAcquirePrimaryMutex(
        PrimaryInstanceOwnerInfo runningOwner,
        string singleInstanceMutexName,
        int timeoutMs = 8000,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(runningOwner.GracefulExitEventName))
        {
            return (null, false);
        }

        using EventWaitHandle? exitEvent = SingleInstanceAndActivationInterop.CreateOrOpenActivationEvent(
            runningOwner.GracefulExitEventName,
            log);
        if (exitEvent is null)
        {
            log?.Invoke($"[ChannelHandoff] Could not open graceful-exit event '{runningOwner.GracefulExitEventName}'.");
            return (null, false);
        }

        try
        {
            exitEvent.Set();
            log?.Invoke(
                $"[ChannelHandoff] Signaled graceful exit to {runningOwner.Channel} instance (pid={runningOwner.ProcessId}).");
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ChannelHandoff] Failed to signal graceful exit: {ex.Message}");
            return (null, false);
        }

        return SingleInstanceAndActivationInterop.TryAcquireSingleInstanceMutexWithTimeout(
            singleInstanceMutexName,
            timeoutMs,
            log);
    }

    internal static string GetDirectDataRoot(string localAppDataPath) =>
        Path.GetFullPath(Path.Combine(localAppDataPath, "DeskBox"));

    internal static string GetStoreDurableDataRoot(string userProfilePath) =>
        Path.GetFullPath(Path.Combine(
            userProfilePath,
            "DeskBox",
            DeskBoxDataPathService.StoreDurableSubdirectoryName));

    internal static string GetSiblingDataRoot(
        bool isCurrentMicrosoftStore,
        string localAppDataPath,
        string userProfilePath) =>
        isCurrentMicrosoftStore
            ? GetDirectDataRoot(localAppDataPath)
            : GetStoreDurableDataRoot(userProfilePath);

    internal static ChannelCoexistenceState DetectOtherChannelInstalled(
        bool isCurrentMicrosoftStore,
        Func<string?>? storePackageProbe = null,
        Func<string?>? directInstallProbe = null)
    {
        AppDistributionChannel current = isCurrentMicrosoftStore
            ? AppDistributionChannel.MicrosoftStore
            : AppDistributionChannel.Direct;
        AppDistributionChannel other = isCurrentMicrosoftStore
            ? AppDistributionChannel.Direct
            : AppDistributionChannel.MicrosoftStore;

        string? detail = isCurrentMicrosoftStore
            ? (directInstallProbe ?? ProbeDirectInstallationLocation)()
            : (storePackageProbe ?? ProbeStorePackageFullName)();

        return new ChannelCoexistenceState(
            !string.IsNullOrWhiteSpace(detail),
            current,
            other,
            detail);
    }

    internal static string? ProbeStorePackageFullName()
    {
        try
        {
            using RegistryKey? packagesKey = Registry.CurrentUser.OpenSubKey(StorePackagesSubkey, writable: false);
            if (packagesKey is null)
            {
                return null;
            }

            string prefix = StorePackageIdentityName + "_";
            foreach (string subkeyName in packagesKey.GetSubKeyNames())
            {
                if (string.Equals(subkeyName, StorePackageIdentityName, StringComparison.OrdinalIgnoreCase) ||
                    subkeyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return subkeyName;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    internal static string? ProbeDirectInstallationLocation()
    {
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    string? directLoc = ReadValidatedInstallDir(baseKey, DirectInstallSubkey, "InstallLocation");
                    if (directLoc is not null)
                    {
                        return directLoc;
                    }

                    string? uninstallLoc = ReadValidatedInstallDir(baseKey, DirectUninstallSubkey, "InstallLocation");
                    if (uninstallLoc is not null)
                    {
                        return uninstallLoc;
                    }

                    string? wowLoc = ReadValidatedInstallDir(baseKey, DirectWowUninstallSubkey, "InstallLocation");
                    if (wowLoc is not null)
                    {
                        return wowLoc;
                    }
                }
                catch
                {
                }
            }
        }

        return null;
    }

    private static string? ReadValidatedInstallDir(RegistryKey baseKey, string subkeyPath, string valueName)
    {
        using RegistryKey? key = baseKey.OpenSubKey(subkeyPath, writable: false);
        string? candidate = key?.GetValue(valueName) as string;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        try
        {
            string normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate.Trim().Trim('"')));
            if (File.Exists(Path.Combine(normalized, "DeskBox.exe")))
            {
                return normalized;
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>
    /// Reads active file-widget paths from the sibling channel's durable data
    /// directory (strictly read-only, zero reflection / zero <c>JsonSerializer</c>).
    /// Used as an orphan-cleanup safety guardrail so folders actively used by the
    /// other installed edition are never offered for deletion.
    /// </summary>
    internal static IReadOnlySet<string> ReadSiblingActiveManagedFolderPaths(
        DeskBoxDataPathService? dataPaths = null)
    {
        DeskBoxDataPathService effectivePaths = dataPaths ?? DeskBoxDataPathService.Current;
        if (effectivePaths.IsDevelopmentRoot)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(localAppData) || string.IsNullOrWhiteSpace(userProfile))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        string siblingRoot = GetSiblingDataRoot(
            effectivePaths.IsMicrosoftStoreRoot,
            localAppData,
            userProfile);
        return ReadActiveManagedFolderPathsFromDataRoot(siblingRoot);
    }

    internal static HashSet<string> ReadActiveManagedFolderPathsFromDataRoot(string siblingRootPath)
    {
        var activePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(siblingRootPath))
        {
            return activePaths;
        }

        string dataDir = Path.Combine(siblingRootPath, "data");
        if (!Directory.Exists(dataDir))
        {
            return activePaths;
        }

        string? siblingDefaultRoot = TryReadDefaultManagedStorageRootFromSettings(
            Path.Combine(dataDir, "settings.json"));

        string layoutPath = Path.Combine(dataDir, "widget-layout.json");
        bool readFromLayout = false;
        if (File.Exists(layoutPath))
        {
            readFromLayout = TryCollectActiveFileWidgetPaths(
                layoutPath,
                rootPropertyName: "layout",
                siblingDefaultRoot,
                activePaths);
        }

        if (!readFromLayout)
        {
            string settingsPath = Path.Combine(dataDir, "settings.json");
            if (File.Exists(settingsPath))
            {
                TryCollectActiveFileWidgetPaths(
                    settingsPath,
                    rootPropertyName: null,
                    siblingDefaultRoot,
                    activePaths);
            }
        }

        return activePaths;
    }

    private static string? TryReadDefaultManagedStorageRootFromSettings(string settingsPath)
    {
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(settingsPath, Encoding.UTF8));
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (TryGetStringProperty(root, "defaultManagedStorageRootPath", "DefaultManagedStorageRootPath", out string? direct) &&
                !string.IsNullOrWhiteSpace(direct))
            {
                return NormalizeFolderKey(direct);
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool TryCollectActiveFileWidgetPaths(
        string jsonPath,
        string? rootPropertyName,
        string? siblingDefaultRoot,
        HashSet<string> activePaths)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(jsonPath, Encoding.UTF8));
            JsonElement container = doc.RootElement;
            if (container.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (rootPropertyName is not null)
            {
                if (!TryGetObjectProperty(container, rootPropertyName, "Layout", out container))
                {
                    return false;
                }
            }

            var deletedIds = new HashSet<string>(StringComparer.Ordinal);
            if (TryGetArrayProperty(container, "deletedWidgetIds", "DeletedWidgetIds", out JsonElement deletedEl))
            {
                foreach (JsonElement idEl in deletedEl.EnumerateArray())
                {
                    if (idEl.ValueKind == JsonValueKind.String && idEl.GetString() is { Length: > 0 } id)
                    {
                        deletedIds.Add(id);
                    }
                }
            }

            if (!TryGetArrayProperty(container, "widgets", "Widgets", out JsonElement widgetsEl))
            {
                return false;
            }

            foreach (JsonElement widgetEl in widgetsEl.EnumerateArray())
            {
                if (widgetEl.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (TryGetStringProperty(widgetEl, "id", "Id", out string? widgetId) &&
                    !string.IsNullOrWhiteSpace(widgetId) &&
                    deletedIds.Contains(widgetId))
                {
                    continue;
                }

                if (!IsFileWidgetElement(widgetEl))
                {
                    continue;
                }

                if (TryGetStringProperty(widgetEl, "mappedFolderPath", "MappedFolderPath", out string? mappedPath) &&
                    !string.IsNullOrWhiteSpace(mappedPath))
                {
                    string? normalized = NormalizeFolderKey(mappedPath);
                    if (normalized is not null)
                    {
                        activePaths.Add(normalized);
                    }
                }

                if (!string.IsNullOrWhiteSpace(siblingDefaultRoot) &&
                    TryGetStringProperty(widgetEl, "managedFolderName", "ManagedFolderName", out string? folderName) &&
                    !string.IsNullOrWhiteSpace(folderName))
                {
                    string? combined = NormalizeFolderKey(Path.Combine(siblingDefaultRoot, folderName));
                    if (combined is not null)
                    {
                        activePaths.Add(combined);
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsFileWidgetElement(JsonElement widgetEl)
    {
        if (widgetEl.TryGetProperty("widgetKind", out JsonElement kindEl) ||
            widgetEl.TryGetProperty("WidgetKind", out kindEl))
        {
            if (kindEl.ValueKind == JsonValueKind.String)
            {
                return string.Equals(kindEl.GetString(), "File", StringComparison.OrdinalIgnoreCase);
            }

            if (kindEl.ValueKind == JsonValueKind.Number && kindEl.TryGetInt32(out int num))
            {
                return num == 0;
            }
        }

        // Legacy entries without widgetKind default to File when they carry a folder path.
        return TryGetStringProperty(widgetEl, "mappedFolderPath", "MappedFolderPath", out string? p) &&
               !string.IsNullOrWhiteSpace(p);
    }

    private static bool TryGetStringProperty(
        JsonElement obj,
        string camelName,
        string pascalName,
        out string? value)
    {
        value = null;
        if ((obj.TryGetProperty(camelName, out JsonElement el) ||
             obj.TryGetProperty(pascalName, out el)) &&
            el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString();
            return true;
        }

        return false;
    }

    private static bool TryGetObjectProperty(
        JsonElement obj,
        string camelName,
        string pascalName,
        out JsonElement value)
    {
        if ((obj.TryGetProperty(camelName, out value) ||
             obj.TryGetProperty(pascalName, out value)) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryGetArrayProperty(
        JsonElement obj,
        string camelName,
        string pascalName,
        out JsonElement value)
    {
        if ((obj.TryGetProperty(camelName, out value) ||
             obj.TryGetProperty(pascalName, out value)) &&
            value.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        value = default;
        return false;
    }

    private static string? NormalizeFolderKey(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
        }
        catch
        {
            return null;
        }
    }

    internal static bool TryOpenWindowsInstalledAppsSettings(Action<string>? log = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "ms-settings:appsfeatures",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            log?.Invoke($"[ChannelCoexistence] Failed to open Installed Apps settings: {ex.Message}");
            return false;
        }
    }
}
