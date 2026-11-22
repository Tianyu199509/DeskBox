using System.Text;
using Microsoft.Win32;

namespace DeskBox.Services;

internal readonly record struct RegistryEnvironmentEntry(
    string RawValue,
    bool ExpandVariables);

internal sealed record EnvironmentRefreshPlan(
    IReadOnlyDictionary<string, string> VariablesToSet,
    IReadOnlyList<string> VariablesToRemove,
    IReadOnlySet<string> ManagedRegistryNames);

/// <summary>
/// Keeps DeskBox's in-process environment block synchronized with the live
/// Windows Machine/User environment registry keys on <c>WM_SETTINGCHANGE</c>
/// ("Environment") and scrubs inherited launcher/IDE/venv contamination at
/// startup so child applications launched locally behave identically to
/// Explorer desktop launches.
/// </summary>
internal static class ProcessEnvironmentSyncService
{
    private const string MachineEnvironmentSubKey =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string UserEnvironmentSubKey = @"Environment";
    private const string VolatileEnvironmentSubKey = @"Volatile Environment";
    private const int SettingChangeCoalesceDelayMs = 300;

    private static readonly HashSet<string> s_protectedVariableNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "ALLUSERSPROFILE",
        "APPDATA",
        "CLIENTNAME",
        "CommonProgramFiles",
        "CommonProgramFiles(x86)",
        "CommonProgramW6432",
        "COMPUTERNAME",
        "ComSpec",
        "DriverData",
        "HOMEDRIVE",
        "HOMEPATH",
        "LOCALAPPDATA",
        "LOGONSERVER",
        "NUMBER_OF_PROCESSORS",
        "OS",
        "PROCESSOR_ARCHITECTURE",
        "PROCESSOR_ARCHITEW6432",
        "PROCESSOR_IDENTIFIER",
        "PROCESSOR_LEVEL",
        "PROCESSOR_REVISION",
        "ProgramData",
        "ProgramFiles",
        "ProgramFiles(x86)",
        "ProgramW6432",
        "PUBLIC",
        "SESSIONNAME",
        "SystemDrive",
        "SystemRoot",
        "USERDNSDOMAIN",
        "USERDOMAIN",
        "USERDOMAIN_ROAMINGPROFILE",
        "USERNAME",
        "USERPROFILE",
        "windir"
    };

    private static readonly string[] s_parentContaminationVariableNames =
    [
        "ELECTRON_RUN_AS_NODE",
        "__COMPAT_LAYER",
        "VIRTUAL_ENV",
        "VIRTUAL_ENV_PROMPT",
        "CONDA_PREFIX",
        "CONDA_DEFAULT_ENV",
        "CONDA_PROMPT_MODIFIER",
        "CONDA_SHLVL",
        "PYTHONHOME",
        "PYTHONPATH",
        "PYTHONSTARTUP",
        "NODE_OPTIONS"
    ];

    private static readonly string[] s_childLaunchImmediateScrubNames =
    [
        "ELECTRON_RUN_AS_NODE",
        "__COMPAT_LAYER"
    ];

    private static HashSet<string> s_managedRegistryNames = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> s_registryConfiguredScrubExceptions = new(StringComparer.OrdinalIgnoreCase);
    private static int s_activeChildLaunchScopes;
    private static Dictionary<string, string>? s_savedChildLaunchVariables;
    private static long s_refreshGeneration;

    /// <summary>
    /// Synchronizes all mutations of the current process environment block so
    /// concurrent child launches and <c>WM_SETTINGCHANGE</c> refreshes never
    /// race on <see cref="Environment.SetEnvironmentVariable(string, string?)"/>.
    /// </summary>
    internal static object SyncGate { get; } = new();

    /// <summary>
    /// Temporarily scrubs child-breaking launcher variables (<c>ELECTRON_RUN_AS_NODE</c>,
    /// <c>__COMPAT_LAYER</c>) around a local <c>ShellExecuteEx</c> launch using
    /// reference counting so concurrent STA workers never restore a variable
    /// while a sibling launch is still in flight, and never hold <see cref="SyncGate"/>
    /// across modal UAC or SmartScreen UI.
    /// </summary>
    internal static IDisposable? AcquireChildLaunchContaminationScope()
    {
        lock (SyncGate)
        {
            if (s_activeChildLaunchScopes == 0)
            {
                Dictionary<string, string>? saved = null;
                foreach (string name in s_childLaunchImmediateScrubNames)
                {
                    if (s_registryConfiguredScrubExceptions.Contains(name))
                    {
                        continue;
                    }

                    string? current = Environment.GetEnvironmentVariable(name);
                    if (current is null)
                    {
                        continue;
                    }

                    saved ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    saved[name] = current;
                    Environment.SetEnvironmentVariable(name, null);
                }

                if (saved is null)
                {
                    return null;
                }

                s_savedChildLaunchVariables = saved;
            }
            else if (s_savedChildLaunchVariables is null)
            {
                return null;
            }

            s_activeChildLaunchScopes++;
            return new ChildLaunchContaminationReleaseScope();
        }
    }

    internal static void ScheduleRefreshFromSettingChange()
    {
        long generation = Interlocked.Increment(ref s_refreshGeneration);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(SettingChangeCoalesceDelayMs).ConfigureAwait(false);
                if (Interlocked.Read(ref s_refreshGeneration) != generation)
                {
                    return;
                }

                RefreshProcessEnvironmentFromRegistry("WM_SETTINGCHANGE");
            }
            catch (Exception ex)
            {
                App.Log($"[EnvSync] Coalesced refresh failed: {ex.Message}");
            }
        });
    }

    internal static void RefreshProcessEnvironmentFromRegistry(string trigger)
    {
        try
        {
            Dictionary<string, RegistryEnvironmentEntry> machineVars =
                ReadRegistryEnvironmentKey(Registry.LocalMachine, MachineEnvironmentSubKey);
            Dictionary<string, RegistryEnvironmentEntry> volatileVars =
                ReadVolatileEnvironmentKeys();
            Dictionary<string, RegistryEnvironmentEntry> userVars =
                ReadRegistryEnvironmentKey(Registry.CurrentUser, UserEnvironmentSubKey);

            bool environmentMutated = false;
            lock (SyncGate)
            {
                Dictionary<string, string> currentVars = SnapshotCurrentProcessEnvironment();
                EnvironmentRefreshPlan plan = BuildRefreshPlan(
                    machineVars,
                    userVars,
                    volatileVars,
                    currentVars,
                    s_managedRegistryNames);

                var configuredExceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string scrubName in s_childLaunchImmediateScrubNames)
                {
                    if (plan.ManagedRegistryNames.Contains(scrubName))
                    {
                        configuredExceptions.Add(scrubName);
                    }
                }

                s_registryConfiguredScrubExceptions = configuredExceptions;
                s_managedRegistryNames = new HashSet<string>(
                    plan.ManagedRegistryNames,
                    StringComparer.OrdinalIgnoreCase);

                if (s_savedChildLaunchVariables is not null)
                {
                    foreach (string removed in plan.VariablesToRemove)
                    {
                        s_savedChildLaunchVariables.Remove(removed);
                    }

                    foreach (string updated in plan.VariablesToSet.Keys)
                    {
                        s_savedChildLaunchVariables.Remove(updated);
                    }
                }

                foreach (string name in plan.VariablesToRemove)
                {
                    Environment.SetEnvironmentVariable(name, null);
                }

                foreach ((string name, string value) in plan.VariablesToSet)
                {
                    if (s_activeChildLaunchScopes > 0 &&
                        !s_registryConfiguredScrubExceptions.Contains(name) &&
                        IsImmediateChildScrubName(name))
                    {
                        continue;
                    }

                    Environment.SetEnvironmentVariable(name, value);
                }

                if (plan.VariablesToSet.Count > 0 || plan.VariablesToRemove.Count > 0)
                {
                    environmentMutated = true;
                    App.Log(
                        $"[EnvSync] Synced process environment ({trigger}): " +
                        $"updated={plan.VariablesToSet.Count} removed={plan.VariablesToRemove.Count}");
                }
            }

            if (environmentMutated)
            {
                // Retire any idle out-of-process context-menu server so the next
                // native verb ("Open in Terminal", "Git Bash Here", "Open") inherits
                // the updated PATH and user environment rather than a stale snapshot.
                DeskBox.Helpers.ShellContextMenuProxy.ShutdownIdleServerIfInactive();
            }
        }
        catch (Exception ex)
        {
            App.Log($"[EnvSync] Failed to refresh process environment ({trigger}): {ex.Message}");
        }
    }

    internal static EnvironmentRefreshPlan BuildRefreshPlan(
        IReadOnlyDictionary<string, RegistryEnvironmentEntry> machineVars,
        IReadOnlyDictionary<string, RegistryEnvironmentEntry> userVars,
        IReadOnlyDictionary<string, RegistryEnvironmentEntry> volatileVars,
        IReadOnlyDictionary<string, string> currentProcessVars,
        IReadOnlySet<string>? previouslyManagedRegistryNames = null)
    {
        ArgumentNullException.ThrowIfNull(machineVars);
        ArgumentNullException.ThrowIfNull(userVars);
        ArgumentNullException.ThrowIfNull(volatileVars);
        ArgumentNullException.ThrowIfNull(currentProcessVars);

        var lookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in currentProcessVars)
        {
            if (IsProtectedVariableName(key) && !string.IsNullOrEmpty(value))
            {
                lookup[key] = value;
            }
        }

        Dictionary<string, string> expandedMachine = ExpandLayer(machineVars, lookup);
        foreach ((string key, string value) in expandedMachine)
        {
            if (!IsProtectedVariableName(key))
            {
                lookup[key] = value;
            }
        }

        // Volatile + User layers share user-level precedence over Machine, while
        // User overrides Volatile when both define the same non-path variable.
        var combinedUserRaw = new Dictionary<string, RegistryEnvironmentEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach ((string key, RegistryEnvironmentEntry entry) in volatileVars)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                combinedUserRaw[key] = entry;
            }
        }

        foreach ((string key, RegistryEnvironmentEntry entry) in userVars)
        {
            if (!string.IsNullOrWhiteSpace(key))
            {
                combinedUserRaw[key] = entry;
            }
        }

        // Seed non-path user variables into lookup in two passes first so user
        // Path referencing %JAVA_HOME%, %NVM_SYMLINK%, or %GOPATH% defined in
        // HKCU\Environment expands against the fresh user values rather than stale ones.
        var userNonPathRaw = new Dictionary<string, RegistryEnvironmentEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach ((string key, RegistryEnvironmentEntry entry) in combinedUserRaw)
        {
            if (!IsPathMergeKey(key))
            {
                userNonPathRaw[key] = entry;
            }
        }

        Dictionary<string, string> expandedUserNonPath = ExpandLayer(userNonPathRaw, lookup);
        foreach ((string key, string value) in expandedUserNonPath)
        {
            if (!IsProtectedVariableName(key))
            {
                lookup[key] = value;
            }
        }

        var desired = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var managedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, string value) in expandedMachine)
        {
            if (IsProtectedVariableName(key) || string.IsNullOrEmpty(value))
            {
                continue;
            }

            desired[key] = value;
            managedNames.Add(key);
        }

        foreach ((string key, string value) in expandedUserNonPath)
        {
            if (IsProtectedVariableName(key))
            {
                continue;
            }

            if (string.IsNullOrEmpty(value))
            {
                desired.Remove(key);
                managedNames.Remove(key);
                continue;
            }

            desired[key] = value;
            managedNames.Add(key);
        }

        foreach ((string key, RegistryEnvironmentEntry entry) in combinedUserRaw)
        {
            if (!IsPathMergeKey(key) || IsProtectedVariableName(key))
            {
                continue;
            }

            string expandedUserPath = ExpandVariablesTwoPass(entry.RawValue, lookup);
            string mergedPath = desired.TryGetValue(key, out string? machinePath)
                ? MergePathSegments(machinePath, expandedUserPath)
                : expandedUserPath;
            if (!string.IsNullOrEmpty(mergedPath))
            {
                desired[key] = mergedPath;
                lookup[key] = mergedPath;
                managedNames.Add(key);
            }
        }

        var toSet = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string desiredValue) in desired)
        {
            if (!currentProcessVars.TryGetValue(key, out string? currentValue) ||
                !string.Equals(currentValue, desiredValue, StringComparison.Ordinal))
            {
                toSet[key] = desiredValue;
            }
        }

        var toRemoveSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (previouslyManagedRegistryNames is not null)
        {
            foreach (string previousKey in previouslyManagedRegistryNames)
            {
                if (!IsProtectedVariableName(previousKey) &&
                    !managedNames.Contains(previousKey) &&
                    currentProcessVars.ContainsKey(previousKey))
                {
                    toRemoveSet.Add(previousKey);
                }
            }
        }

        foreach (string contaminationKey in s_parentContaminationVariableNames)
        {
            if (!IsProtectedVariableName(contaminationKey) &&
                !managedNames.Contains(contaminationKey) &&
                currentProcessVars.ContainsKey(contaminationKey))
            {
                toRemoveSet.Add(contaminationKey);
            }
        }

        List<string> toRemove = [.. toRemoveSet];
        toRemove.Sort(StringComparer.OrdinalIgnoreCase);
        return new EnvironmentRefreshPlan(toSet, toRemove, managedNames);
    }

    internal static bool IsProtectedVariableName(string name)
    {
        return string.IsNullOrWhiteSpace(name) ||
               name.StartsWith("DESKBOX_", StringComparison.OrdinalIgnoreCase) ||
               s_protectedVariableNames.Contains(name);
    }

    private static bool IsPathMergeKey(string name)
    {
        return name.Equals("Path", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("LibPath", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Os2LibPath", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsImmediateChildScrubName(string name)
    {
        foreach (string candidate in s_childLaunchImmediateScrubNames)
        {
            if (name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<string, string> ExpandLayer(
        IReadOnlyDictionary<string, RegistryEnvironmentEntry> rawEntries,
        IReadOnlyDictionary<string, string> baseLookup)
    {
        var workingLookup = new Dictionary<string, string>(baseLookup, StringComparer.OrdinalIgnoreCase);
        var expanded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, RegistryEnvironmentEntry entry) in rawEntries)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            expanded[key] = entry.RawValue;
            if (!IsProtectedVariableName(key))
            {
                workingLookup[key] = entry.RawValue;
            }
        }

        for (int pass = 0; pass < 2; pass++)
        {
            foreach ((string key, RegistryEnvironmentEntry entry) in rawEntries)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                string value = entry.ExpandVariables || IsPathMergeKey(key)
                    ? ExpandPercentTokens(entry.RawValue, workingLookup)
                    : entry.RawValue;
                expanded[key] = value;
                if (!IsProtectedVariableName(key))
                {
                    workingLookup[key] = value;
                }
            }
        }

        return expanded;
    }

    private static string ExpandVariablesTwoPass(
        string rawValue,
        IReadOnlyDictionary<string, string> lookup)
    {
        string firstPass = ExpandPercentTokens(rawValue, lookup);
        return ExpandPercentTokens(firstPass, lookup);
    }

    internal static string ExpandPercentTokens(
        string value,
        IReadOnlyDictionary<string, string> lookup)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains('%'))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 32);
        int index = 0;
        while (index < value.Length)
        {
            int openPercent = value.IndexOf('%', index);
            if (openPercent < 0)
            {
                builder.Append(value, index, value.Length - index);
                break;
            }

            builder.Append(value, index, openPercent - index);
            if (openPercent + 1 >= value.Length)
            {
                builder.Append('%');
                break;
            }

            int closePercent = value.IndexOf('%', openPercent + 1);
            if (closePercent < 0)
            {
                builder.Append(value, openPercent, value.Length - openPercent);
                break;
            }

            int tokenLength = closePercent - openPercent - 1;
            if (tokenLength == 0)
            {
                builder.Append("%%");
                index = closePercent + 1;
                continue;
            }

            string tokenName = value.Substring(openPercent + 1, tokenLength);
            if (lookup.TryGetValue(tokenName, out string? replacement))
            {
                builder.Append(replacement);
                index = closePercent + 1;
            }
            else
            {
                // Preserve unknown %TOKENS% verbatim and allow the closing '%'
                // to act as the opening delimiter of a subsequent "%KNOWN%" token.
                builder.Append('%').Append(tokenName);
                index = closePercent;
            }
        }

        return builder.ToString();
    }

    private static string MergePathSegments(string machinePath, string userPath)
    {
        string trimmedMachine = machinePath.Trim().TrimEnd(';');
        string trimmedUser = userPath.Trim().TrimStart(';');
        if (string.IsNullOrEmpty(trimmedMachine))
        {
            return userPath;
        }

        if (string.IsNullOrEmpty(trimmedUser))
        {
            return machinePath;
        }

        return string.Concat(trimmedMachine, ";", trimmedUser);
    }

    private static Dictionary<string, RegistryEnvironmentEntry> ReadVolatileEnvironmentKeys()
    {
        Dictionary<string, RegistryEnvironmentEntry> result =
            ReadRegistryEnvironmentKey(Registry.CurrentUser, VolatileEnvironmentSubKey);
        try
        {
            int sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            Dictionary<string, RegistryEnvironmentEntry> sessionVolatile =
                ReadRegistryEnvironmentKey(
                    Registry.CurrentUser,
                    $"{VolatileEnvironmentSubKey}\\{sessionId}");
            foreach ((string key, RegistryEnvironmentEntry entry) in sessionVolatile)
            {
                result[key] = entry;
            }
        }
        catch
        {
        }

        return result;
    }

    private static Dictionary<string, RegistryEnvironmentEntry> ReadRegistryEnvironmentKey(
        RegistryKey root,
        string subKeyPath)
    {
        var entries = new Dictionary<string, RegistryEnvironmentEntry>(
            StringComparer.OrdinalIgnoreCase);
        try
        {
            using RegistryKey? key = root.OpenSubKey(subKeyPath, writable: false);
            if (key is null)
            {
                return entries;
            }

            foreach (string valueName in key.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(valueName))
                {
                    continue;
                }

                RegistryValueKind kind;
                try
                {
                    kind = key.GetValueKind(valueName);
                }
                catch
                {
                    continue;
                }

                if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                {
                    continue;
                }

                object? rawObj = key.GetValue(
                    valueName,
                    defaultValue: null,
                    RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (rawObj is string rawString)
                {
                    entries[valueName] = new RegistryEnvironmentEntry(
                        rawString,
                        ExpandVariables: kind == RegistryValueKind.ExpandString);
                }
            }
        }
        catch
        {
        }

        return entries;
    }

    private static Dictionary<string, string> SnapshotCurrentProcessEnvironment()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        System.Collections.IDictionary raw = Environment.GetEnvironmentVariables();
        foreach (System.Collections.DictionaryEntry entry in raw)
        {
            if (entry.Key is string key &&
                !string.IsNullOrWhiteSpace(key) &&
                entry.Value is string value)
            {
                snapshot[key] = value;
            }
        }

        return snapshot;
    }

    private sealed class ChildLaunchContaminationReleaseScope : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            lock (SyncGate)
            {
                if (s_activeChildLaunchScopes > 0)
                {
                    s_activeChildLaunchScopes--;
                }

                if (s_activeChildLaunchScopes == 0 && s_savedChildLaunchVariables is not null)
                {
                    foreach ((string name, string value) in s_savedChildLaunchVariables)
                    {
                        Environment.SetEnvironmentVariable(name, value);
                    }

                    s_savedChildLaunchVariables = null;
                }
            }
        }
    }
}
