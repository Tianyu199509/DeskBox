using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.Win32;

namespace DeskBox.Services;

public sealed partial class DirectStartupService : IStartupService
{
    private const string AppName = "DeskBox";
    private readonly object _registrationLock = new();
    private readonly IDirectStartupTaskBackend _taskBackend;
    private readonly IDirectStartupRunEntryStore _runEntryStore;
    private readonly Func<string?> _executablePathProvider;
    private readonly string? _legacyShortcutPath;
    private readonly Func<string, string?> _shortcutTargetReader;
    private readonly Action<string> _shortcutDelete;
    private readonly Action<string> _log;
    private readonly Func<bool> _runEntryApprovedProvider;

    public DirectStartupService(SettingsService? settingsService = null)
        : this(
            new DirectStartupTaskBackend(),
            new RegistryStartupRunEntryStore(),
            () => AppExecutablePath.ResolveMainExecutablePath(),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                AppName + ".lnk"),
            path => ShortcutHelper.ReadStoredMetadata(path)?.TargetPath,
            File.Delete,
            null,
            null,
            settingsService is null ? null : () => settingsService.Settings.AutoStartMode,
            settingsService is null ? null : mode =>
            {
                settingsService.Settings.AutoStartMode = mode;
                settingsService.SaveDebounced();
            })
    {
    }

    internal DirectStartupService(
        IDirectStartupTaskBackend taskBackend,
        IDirectStartupRunEntryStore runEntryStore,
        Func<string?> executablePathProvider,
        string? legacyShortcutPath = null,
        Func<string, string?>? shortcutTargetReader = null,
        Action<string>? shortcutDelete = null,
        Action<string>? logger = null,
        Func<bool>? runEntryApprovedProvider = null,
        Func<StartupMode?>? modeProvider = null,
        Action<StartupMode>? modeWriter = null)
    {
        _taskBackend = taskBackend;
        _runEntryStore = runEntryStore;
        _executablePathProvider = executablePathProvider;
        _legacyShortcutPath = legacyShortcutPath;
        _shortcutTargetReader = shortcutTargetReader ?? (_ => null);
        _shortcutDelete = shortcutDelete ?? (_ => { });
        _log = logger ?? (message =>
            global::DeskBox.App.Log($"[DirectStartupService] {message}"));
        _runEntryApprovedProvider = runEntryApprovedProvider ?? IsRunEntryApproved;
        _modeProvider = modeProvider;
        _modeWriter = modeWriter;
    }


    public StartupRegistrationState GetState()
    {
        lock (_registrationLock)
        {
            return GetStateCore();
        }
    }

    private StartupRegistrationState GetStateCore()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
                return StartupRegistrationState.BlockedOrFailed;

            string? runValue = _runEntryStore.Read();
            bool ownsRun = IsCommandOwnedBy(runValue, executablePath);
            // An old Windows Startup apps opt-out must survive migration.
            if (ownsRun && !_runEntryApprovedProvider())
                return StartupRegistrationState.DisabledByUser;

            DirectStartupTaskRegistration? task = _taskBackend.Read();
            if (task?.IsOwnedBy(executablePath) == true)
                return task.Enabled
                    ? StartupRegistrationState.Enabled
                    : StartupRegistrationState.DisabledByTaskScheduler;

            // A failed migration leaves the existing Run registration usable.
            if (ownsRun || IsLegacyShortcutOwnedBy(executablePath))
                return StartupRegistrationState.Enabled;

            if (_taskBackend.ReadFailed)
                return StartupRegistrationState.BlockedOrFailed;

            return (!string.IsNullOrWhiteSpace(runValue) && !IsSameInstallCompanionCommand(runValue, executablePath)) ||
                   (task is not null && !IsSameInstallCompanionTask(task, executablePath))
                ? StartupRegistrationState.PathMismatch
                : StartupRegistrationState.NotRegistered;
        }
        catch
        {
            return StartupRegistrationState.BlockedOrFailed;
        }
    }

    public bool IsEnabled() => GetState() == StartupRegistrationState.Enabled;


    public string? GetRunValue()
    {
        lock (_registrationLock)
        {
            return GetRunValueCore();
        }
    }

    private string? GetRunValueCore()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            DirectStartupTaskRegistration? task = _taskBackend.Read();
            if (executablePath is not null && task?.IsOwnedBy(executablePath) == true)
                return task.CommandLine;
            return _runEntryStore.Read() ?? task?.CommandLine;
        }
        catch
        {
            return null;
        }
    }


    public StartupOperationResult Enable()
    {
        lock (_registrationLock)
        {
            return EnableCore();
        }
    }

    private StartupOperationResult EnableCore()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
                return new(StartupRegistrationState.BlockedOrFailed,
                    "Cannot enable startup: the executable path is unavailable.");

            if (IsCommandOwnedBy(_runEntryStore.Read(), executablePath) &&
                !_runEntryApprovedProvider())
            {
                string cleanupError = RemoveOwnedAlternativeRegistrations(executablePath);
                return new(StartupRegistrationState.DisabledByUser, cleanupError);
            }

            StartupMode mode = GetActiveMode(executablePath) ?? ResolveMode(executablePath);
            StartupOperationResult result = mode == StartupMode.ScheduledTask
                ? EnableTaskAndRemoveLegacyEntries(executablePath)
                : EnableStandardAndRemoveAlternatives(executablePath);
            if (result.IsEnabled)
                SaveMode(result.EffectiveMode ?? mode);
            return result;
        }
        catch (Exception ex)
        {
            Log($"Failed to enable startup: {ex.Message}");
            return new(StartupRegistrationState.BlockedOrFailed, ex.Message);
        }
    }

    private StartupOperationResult EnableTaskAndRemoveLegacyEntries(string executablePath)
    {
        string? existingRun = _runEntryStore.Read();
        if (!string.IsNullOrWhiteSpace(existingRun) &&
            !IsCommandOwnedBy(existingRun, executablePath) &&
            !IsSameInstallCompanionCommand(existingRun, executablePath) &&
            CommandTargetExists(existingRun))
            return new(StartupRegistrationState.PathMismatch,
                "The Run entry belongs to another DeskBox installation.");

        DirectStartupTaskRegistration? existingTask = _taskBackend.Read();
        if (existingTask is not null && !existingTask.IsOwnedBy(executablePath) &&
            !IsSameInstallCompanionTask(existingTask, executablePath) &&
            File.Exists(existingTask.ExecutablePath))
            return new(StartupRegistrationState.PathMismatch,
                "The startup task belongs to another DeskBox installation.");

        if (!TryEnableScheduledTask(executablePath))
        {
            string taskError = _taskBackend.LastError;
            StartupOperationResult fallback = EnableStandardAndRemoveAlternatives(executablePath);
            return fallback with
            {
                ErrorMessage = CombineErrors(taskError, fallback.ErrorMessage),
                UsedFallback = fallback.IsEnabled
            };
        }

        try
        {
            // Remove the shortcut before Run, so a failed shortcut cleanup
            // leaves the previous standard registration available for rollback.
            if (!DeleteLegacyStartupShortcutIfOwnedBy(executablePath, out string shortcutError))
                throw new IOException(shortcutError);
            if (!string.IsNullOrWhiteSpace(existingRun) &&
                !IsCommandOwnedBy(existingRun, executablePath) &&
            !IsSameInstallCompanionCommand(existingRun, executablePath) &&
                !CommandTargetExists(existingRun) &&
                string.Equals(_runEntryStore.Read(), existingRun, StringComparison.Ordinal))
            {
                _runEntryStore.Delete();
                if (string.Equals(_runEntryStore.Read(), existingRun, StringComparison.Ordinal))
                    throw new IOException("The obsolete Run entry still exists after removal.");
            }
            else
            {
                DeleteLegacyRunEntryIfOwnedBy(executablePath);
            }
        }
        catch (Exception ex)
        {
            // If an old launch path cannot be removed, roll back the new task
            // instead of deliberately leaving two active registrations.
            string rollbackError = (existingTask is null
                ? _taskBackend.TryDelete()
                : _taskBackend.TryRestore(existingTask))
                ? string.Empty : _taskBackend.LastError;
            rollbackError = CombineErrors(rollbackError, RestoreRunValue(existingRun));
            return new(StartupRegistrationState.BlockedOrFailed,
                CombineErrors(ex.Message, rollbackError));
        }

        Log("Startup enabled through the least-privilege logon task");
        return new(StartupRegistrationState.Enabled, EffectiveMode: StartupMode.ScheduledTask);
    }

    public StartupOperationResult Disable()
    {
        lock (_registrationLock)
        {
            return DisableCore();
        }
    }

    private StartupOperationResult DisableCore()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
            {
                const string message =
                    "Cannot disable startup: the executable path is unavailable.";
                Log(message);
                return new StartupOperationResult(
                    StartupRegistrationState.BlockedOrFailed,
                    message);
            }

            List<string> failures = [];
            DirectStartupTaskRegistration? task = _taskBackend.Read();
            if (task is not null && (task.IsOwnedBy(executablePath) || IsSameInstallCompanionTask(task, executablePath)) && !_taskBackend.TryDelete())
            {
                string failure =
                    $"Failed to delete the owned startup task: {_taskBackend.LastError}";
                failures.Add(failure);
                Log(failure);
            }

            try
            {
                DeleteLegacyRunEntryIfOwnedBy(executablePath);
            }
            catch (Exception ex)
            {
                failures.Add($"Failed to delete the owned Run entry: {ex.Message}");
            }

            if (!DeleteLegacyStartupShortcutIfOwnedBy(executablePath, out string shortcutError))
            {
                failures.Add(shortcutError);
            }

            Log("Startup disabled");
            StartupRegistrationState state = GetState();
            return new StartupOperationResult(state, string.Join("; ", failures));
        }
        catch (Exception ex)
        {
            Log($"Failed to disable startup: {ex.Message}");
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message);
        }
    }


    /// <summary>
    /// Migrates only existing enabled startup registrations. Never creates an
    /// entry for a user who has not enabled startup, or re-enables a disabled task.
    /// </summary>
    internal void TryMigrateLegacyRegistration()
    {
        lock (_registrationLock)
        {
            TryMigrateLegacyRegistrationCore();
        }
    }

    private void TryMigrateLegacyRegistrationCore()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
                return;

            DirectStartupTaskRegistration? task = _taskBackend.Read();
            bool ownsTask = task?.IsOwnedBy(executablePath) == true;
            string? runValue = _runEntryStore.Read();
            bool ownsRun = IsCommandOwnedBy(runValue, executablePath);
            bool ownsShortcut = IsLegacyShortcutOwnedBy(executablePath);
            Log($"Startup migration inspection: executable='{executablePath}' " +
                $"runTarget='{ExtractExecutablePath(runValue) ?? "none"}' " +
                $"ownsTask={ownsTask} ownsRun={ownsRun} ownsShortcut={ownsShortcut} " +
                $"process64Bit={Environment.Is64BitProcess}");
            if (!ownsTask && !ownsRun && !ownsShortcut &&
                !(task is { Enabled: true } && IsSameInstallCompanionTask(task, executablePath)) &&
                !IsSameInstallCompanionCommand(runValue, executablePath))
                return;

            if (ownsRun && !_runEntryApprovedProvider())
            {
                string error = RemoveOwnedAlternativeRegistrations(executablePath);
                Log(CombineErrors("Preserved Windows-disabled startup registration.", error));
                return;
            }

            if (ownsTask && !task!.Enabled)
            {
                // Keep the task disabled; older owned paths must not bypass it.
                DeleteLegacyRunEntryIfOwnedBy(executablePath);
                DeleteLegacyStartupShortcutIfOwnedBy(executablePath);
                Log("Preserved task disabled by the user.");
                return;
            }

            StartupMode mode = GetActiveMode(executablePath) ?? ResolveMode(executablePath);
            SaveMode(mode);
            if (mode == StartupMode.Standard && ownsRun && !ownsTask && !ownsShortcut)
                return;
            if (mode == StartupMode.ScheduledTask && ownsTask && _taskBackend.IsPreferred(task!, executablePath) &&
                !ownsRun && !ownsShortcut)
                return;

            StartupOperationResult result = EnableCore();
            Log(result.State == StartupRegistrationState.Enabled
                ? $"Startup registration reconciled using {result.EffectiveMode}"
                : $"Startup migration deferred; previous registration preserved: {result.ErrorMessage}");
        }
        catch (Exception ex)
        {
            Log($"Startup migration failed: {ex.Message}");
        }
    }

    private bool TryEnableScheduledTask(string executablePath)
    {
        DirectStartupTaskRegistration? existing = _taskBackend.Read();
        if (existing is not null && !existing.IsOwnedBy(executablePath))
        {
            if (!IsSameInstallCompanionTask(existing, executablePath) && File.Exists(existing.ExecutablePath))
            {
                Log(
                    $"Preserved startup task owned by another installation: " +
                    $"'{existing.ExecutablePath}'");
                return false;
            }

            Log(
                $"Taking over the orphaned startup task pointing at a missing " +
                $"target: '{existing.ExecutablePath}'");
        }

        if (existing is not null && _taskBackend.IsPreferred(existing, executablePath))
        {
            return true;
        }

        bool registered = _taskBackend.TryRegister(executablePath);
        if (!registered)
        {
            Log($"Failed to register the preferred startup task: {_taskBackend.LastError}");
        }
        return registered;
    }

    /// <summary>
    /// Windows' Startup apps page disables entries by flipping a bit under
    /// Explorer\StartupApproved instead of deleting the Run value; the entry
    /// counts as enabled unless that state explicitly disables it.
    /// </summary>
    private static bool IsRunEntryApproved()
    {
        try
        {
            using RegistryKey? key = RegistryStartupRunEntryStore.OpenCurrentUserKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
                writable: false);
            if (key?.GetValue(AppName) is byte[] state && state.Length > 0)
            {
                return (state[0] & 1) == 0;
            }

            return true;
        }
        catch
        {
            return true;
        }
    }

    private static bool CommandTargetExists(string commandLine)
    {
        string? target = ExtractExecutablePath(commandLine);
        return !string.IsNullOrWhiteSpace(target) && File.Exists(target);
    }

    private string RemoveOwnedAlternativeRegistrations(string executablePath)
    {
        List<string> failures = [];
        DirectStartupTaskRegistration? task = _taskBackend.Read();
        if (task is not null &&
            task.IsOwnedBy(executablePath) &&
            !_taskBackend.TryDelete())
        {
            string failure =
                $"Failed to remove the superseded startup task: {_taskBackend.LastError}";
            failures.Add(failure);
            Log(failure);
        }

        if (!DeleteLegacyStartupShortcutIfOwnedBy(
                executablePath,
                out string shortcutError))
        {
            failures.Add(shortcutError);
        }

        return string.Join("; ", failures);
    }

    private static string CombineErrors(string first, string second)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return second;
        }

        if (string.IsNullOrWhiteSpace(second))
        {
            return first;
        }

        return $"{first}; {second}";
    }

    private void DeleteLegacyRunEntryIfOwnedBy(string executablePath)
    {
        if (IsCommandOwnedBy(_runEntryStore.Read(), executablePath) ||
            IsSameInstallCompanionCommand(_runEntryStore.Read(), executablePath))
        {
            _runEntryStore.Delete();
            if (IsCommandOwnedBy(_runEntryStore.Read(), executablePath) ||
            IsSameInstallCompanionCommand(_runEntryStore.Read(), executablePath))
            {
                throw new IOException("The owned Run entry still exists after removal.");
            }
            Log("Removed and verified the owned legacy Run entry");
        }
    }

    private bool IsLegacyShortcutOwnedBy(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(_legacyShortcutPath) ||
            !File.Exists(_legacyShortcutPath))
        {
            return false;
        }

        try
        {
            return DirectStartupTaskBackend.PathsEqual(
                _shortcutTargetReader(_legacyShortcutPath),
                executablePath);
        }
        catch
        {
            return false;
        }
    }

    private void DeleteLegacyStartupShortcutIfOwnedBy(string executablePath) =>
        _ = DeleteLegacyStartupShortcutIfOwnedBy(executablePath, out _);

    private bool DeleteLegacyStartupShortcutIfOwnedBy(
        string executablePath,
        out string error)
    {
        error = string.Empty;
        if (!IsLegacyShortcutOwnedBy(executablePath) ||
            string.IsNullOrWhiteSpace(_legacyShortcutPath))
        {
            return true;
        }

        try
        {
            _shortcutDelete(_legacyShortcutPath);
            return true;
        }
        catch (Exception ex)
        {
            error =
                $"Failed to delete the owned legacy startup shortcut: {ex.Message}";
            Log(error);
            return false;
        }
    }

    private string? GetExecutablePath()
    {
        string? executablePath = _executablePathProvider();
        return string.IsNullOrWhiteSpace(executablePath)
            ? null
            : Path.GetFullPath(executablePath);
    }

    internal static bool IsCommandOwnedBy(
        string? commandLine,
        string executablePath)
    {
        string? commandExecutablePath = ExtractExecutablePath(commandLine);
        return DirectStartupTaskBackend.PathsEqual(
            commandExecutablePath,
            executablePath);
    }

    private static bool IsSameInstallCompanionCommand(
        string? commandLine,
        string executablePath) =>
        AppExecutablePath.IsSameInstallCompanionExecutable(
            ExtractExecutablePath(commandLine),
            executablePath);

    private static bool IsSameInstallCompanionTask(
        DirectStartupTaskRegistration task,
        string executablePath) =>
        AppExecutablePath.IsSameInstallCompanionExecutable(
            task.ExecutablePath,
            executablePath);

    private static string? ExtractExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        string trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            int closingQuote = trimmed.IndexOf('"', 1);
            return closingQuote > 1 ? trimmed[1..closingQuote] : null;
        }

        int separator = trimmed.IndexOfAny([' ', '\t']);
        return separator < 0 ? trimmed : trimmed[..separator];
    }

    private void Log(string message) => _log(message);

    public StartupOperationResult SetEnabled(bool enabled)
    {
        if (enabled)
        {
            return Enable();
        }

        return Disable();
    }
}

internal interface IDirectStartupRunEntryStore
{
    string? Read();

    void Write(string commandLine);

    void Delete();
}

internal sealed class RegistryStartupRunEntryStore : IDirectStartupRunEntryStore
{
    private const string RegistryKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private readonly string _registryKeyPath;
    private readonly string _valueName;

    internal RegistryStartupRunEntryStore(
        string registryKeyPath = RegistryKeyPath,
        string valueName = "DeskBox")
    {
        _registryKeyPath = registryKeyPath;
        _valueName = valueName;
    }

    internal static RegistryKey? OpenCurrentUserKey(string path, bool writable)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        // Use the same explicit user identity as the logon task. In particular,
        // deferred work must not depend on a previously cached HKCU handle.
        using RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
        return users.OpenSubKey($"{sid}\\{path}", writable);
    }

    public string? Read()
    {
        using RegistryKey? key = OpenCurrentUserKey(
            _registryKeyPath,
            writable: false);
        return key?.GetValue(_valueName) as string;
    }

    public void Delete()
    {
        using RegistryKey? key = OpenCurrentUserKey(
            _registryKeyPath,
            writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    public void Write(string commandLine)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        string sid = identity.User?.Value ??
            throw new InvalidOperationException("The current Windows user SID is unavailable.");
        using RegistryKey users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Default);
        using RegistryKey key = users.CreateSubKey($"{sid}\\{_registryKeyPath}", writable: true)
            ?? throw new IOException("The current user's Run key could not be opened.");
        key.SetValue(_valueName, commandLine, RegistryValueKind.String);
    }
}
