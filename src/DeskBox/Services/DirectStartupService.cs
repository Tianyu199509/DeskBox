using DeskBox.Helpers;
using DeskBox.Models;
using Microsoft.Win32;

namespace DeskBox.Services;

public sealed partial class DirectStartupService : IStartupService
{
    private const string AppName = "DeskBox";
    private const string LegacyShortcutFileName = AppName + ".lnk";
    private static readonly TimeSpan CrossProcessLockTimeout = TimeSpan.FromSeconds(15);

    private readonly object _registrationLock = new();
    private readonly string? _crossProcessMutexName;
    private readonly IDirectStartupTaskBackend _taskBackend;
    private readonly IDirectStartupRunEntryStore _runEntryStore;
    private readonly Func<string?> _executablePathProvider;
    private readonly string? _legacyShortcutPath;
    private readonly string? _legacyCommonShortcutPath;
    private readonly Func<string, string?> _shortcutTargetReader;
    private readonly Action<string> _shortcutDelete;
    private readonly Action<string> _log;
    private readonly Func<bool> _runEntryApprovedProvider;
    private readonly Func<bool> _shortcutApprovedProvider;

    public DirectStartupService(SettingsService? settingsService = null)
        : this(
            new DirectStartupTaskBackend(),
            new RegistryStartupRunEntryStore(),
            () => AppExecutablePath.ResolveMainExecutablePath(),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                LegacyShortcutFileName),
            path => ShortcutHelper.ReadStoredMetadata(path)?.TargetPath,
            File.Delete,
            null,
            null,
            settingsService is null ? null : () => settingsService.Settings.Core.AutoStartMode,
            settingsService is null ? null : mode =>
            {
                DeskBox.Platform.UiDispatch.RunOrDefer(() =>
                {
                    if (settingsService.Settings.Core.AutoStartMode == mode)
                    {
                        return;
                    }

                    settingsService.Settings.Core.AutoStartMode = mode;
                    settingsService.SaveDebounced();
                });
            },
            ResolveDistinctCommonStartupShortcutPath(),
            IsStartupFolderShortcutApproved,
            $"DeskBox_StartupReg_Mutex_{DeskBoxDataPathService.Current.InstanceScope}")
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
        Action<StartupMode>? modeWriter = null,
        string? legacyCommonShortcutPath = null,
        Func<bool>? shortcutApprovedProvider = null,
        string? crossProcessMutexName = null)
    {
        _taskBackend = taskBackend;
        _runEntryStore = runEntryStore;
        _executablePathProvider = executablePathProvider;
        _legacyShortcutPath = legacyShortcutPath;
        _legacyCommonShortcutPath =
            !string.IsNullOrWhiteSpace(legacyCommonShortcutPath) &&
            !DirectStartupTaskBackend.PathsEqual(legacyCommonShortcutPath, legacyShortcutPath)
                ? legacyCommonShortcutPath
                : null;
        _shortcutTargetReader = shortcutTargetReader ?? (_ => null);
        _shortcutDelete = shortcutDelete ?? (_ => { });
        _log = logger ?? (message =>
            global::DeskBox.App.Log($"[DirectStartupService] {message}"));
        _runEntryApprovedProvider = runEntryApprovedProvider ?? IsRunEntryApproved;
        _shortcutApprovedProvider = shortcutApprovedProvider ?? (() => true);
        _modeProvider = modeProvider;
        _modeWriter = modeWriter;
        _crossProcessMutexName = crossProcessMutexName;
    }

    private T ExecuteSerialized<T>(Func<T> action)
    {
        lock (_registrationLock)
        {
            if (string.IsNullOrWhiteSpace(_crossProcessMutexName))
            {
                return action();
            }

            Mutex? mutex = null;
            bool acquired = false;
            try
            {
                mutex = new Mutex(initiallyOwned: false, _crossProcessMutexName);
                try
                {
                    acquired = mutex.WaitOne(CrossProcessLockTimeout);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;
                }

                if (!acquired)
                {
                    Log("Timed out waiting for cross-process startup registration mutex; continuing serialized locally.");
                }

                return action();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Log($"Cross-process startup mutex unavailable ({ex.Message}); continuing serialized locally.");
                return action();
            }
            finally
            {
                if (mutex is not null)
                {
                    if (acquired)
                    {
                        try { mutex.ReleaseMutex(); }
                        catch (ApplicationException) { }
                    }

                    mutex.Dispose();
                }
            }
        }
    }

    public StartupRegistrationState GetState() =>
        ExecuteSerialized(GetStateCore);

    public StartupOperationResult GetStatus() =>
        ExecuteSerialized(GetStatusCore);

    private StartupOperationResult GetStatusCore()
    {
        StartupRegistrationState state = GetStateCore();
        if (state != StartupRegistrationState.Enabled)
        {
            return new StartupOperationResult(state);
        }

        string? executablePath = GetExecutablePath();
        StartupMode? activeMode = GetActiveMode(executablePath);
        return new StartupOperationResult(
            state,
            EffectiveMode: activeMode,
            UsedFallback: activeMode == StartupMode.Standard);
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
            bool ownsUserShortcut = IsLegacyShortcutOwnedBy(_legacyShortcutPath, executablePath);
            bool ownsCommonShortcut = IsLegacyShortcutOwnedBy(_legacyCommonShortcutPath, executablePath);

            // An existing Windows Startup apps opt-out (Run or Startup folder)
            // must survive migration and never be bypassed.
            if ((ownsRun && !_runEntryApprovedProvider()) ||
                (ownsUserShortcut && !_shortcutApprovedProvider()))
            {
                return StartupRegistrationState.DisabledByUser;
            }

            DirectStartupTaskRegistration? task = _taskBackend.Read();
            if (task?.IsOwnedBy(executablePath) == true)
            {
                return task.Enabled
                    ? StartupRegistrationState.Enabled
                    : StartupRegistrationState.DisabledByTaskScheduler;
            }

            // A fallback or pre-migration standard registration remains usable.
            if (ownsRun || ownsUserShortcut || ownsCommonShortcut)
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

    public string? GetRunValue() =>
        ExecuteSerialized(GetRunValueCore);

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
        (StartupOperationResult result, StartupMode? modeToPersist) =
            ExecuteSerialized(EnableCoreWithDeferredMode);
        if (modeToPersist is { } mode)
        {
            PersistModeOutsideLock(mode);
        }

        return result;
    }

    private (StartupOperationResult Result, StartupMode? ModeToPersist) EnableCoreWithDeferredMode()
    {
        StartupOperationResult result = EnableCore(out StartupMode? modeToPersist);
        return (result, modeToPersist);
    }

    private StartupOperationResult EnableCore(out StartupMode? modeToPersist)
    {
        modeToPersist = null;
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
                return new(StartupRegistrationState.BlockedOrFailed,
                    "Cannot enable startup: the executable path is unavailable.");

            string? currentRun = _runEntryStore.Read();
            bool ownsRun = IsCommandOwnedBy(currentRun, executablePath) ||
                IsSameInstallCompanionCommand(currentRun, executablePath);
            bool ownsUserShortcut = IsLegacyShortcutOwnedBy(_legacyShortcutPath, executablePath);
            if ((ownsRun && !_runEntryApprovedProvider()) ||
                (ownsUserShortcut && !_shortcutApprovedProvider()))
            {
                string cleanupError = RemoveOwnedAlternativeRegistrations(
                    executablePath,
                    preserveRun: ownsRun,
                    preserveUserShortcut: ownsUserShortcut);
                return new(StartupRegistrationState.DisabledByUser, cleanupError);
            }

            // Unified Scheme A policy: always prefer the fast least-privilege
            // logon task via direct COM, and transparently fall back to the
            // verified standard Run entry if Task Scheduler is blocked or denied.
            StartupOperationResult result = EnableTaskAndRemoveLegacyEntries(executablePath);
            if (result.IsEnabled)
            {
                StartupMode effectiveMode = result.EffectiveMode ?? StartupMode.ScheduledTask;
                _selectedMode = effectiveMode;
                modeToPersist = effectiveMode;
            }

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
            // Remove shortcuts before Run, so a failed shortcut cleanup leaves
            // the previous standard registration available for rollback.
            if (!DeleteAllLegacyStartupShortcutsIfOwnedBy(executablePath, out string shortcutError))
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
                ? _taskBackend.TryDelete(executablePath)
                : _taskBackend.TryRestore(existingTask))
                ? string.Empty : _taskBackend.LastError;
            rollbackError = CombineErrors(rollbackError, RestoreRunValue(existingRun));
            return new(StartupRegistrationState.BlockedOrFailed,
                CombineErrors(ex.Message, rollbackError));
        }

        Log("Startup enabled through the least-privilege logon task");
        return new(StartupRegistrationState.Enabled, EffectiveMode: StartupMode.ScheduledTask);
    }

    public StartupOperationResult Disable() =>
        ExecuteSerialized(DisableCore);

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
            bool hasOwnedOrCompanionTask =
                (task is not null && (task.IsOwnedBy(executablePath) || IsSameInstallCompanionTask(task, executablePath))) ||
                _taskBackend.HasCurrentUserLegacyTask(executablePath);
            if (hasOwnedOrCompanionTask && !_taskBackend.TryDelete(executablePath))
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

            if (!DeleteAllLegacyStartupShortcutsIfOwnedBy(executablePath, out string shortcutError))
            {
                failures.Add(shortcutError);
            }

            Log("Startup disabled");
            StartupRegistrationState state = GetStateCore();
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
    /// Reconciles existing enabled startup registrations to the single preferred
    /// fast logon task (or preserves verified standard fallback when tasks are
    /// blocked). Never creates an entry for a user who has not enabled startup,
    /// and never bypasses a user/Windows disable decision.
    /// </summary>
    internal void TryMigrateLegacyRegistration()
    {
        StartupMode? modeToPersist = ExecuteSerialized(TryMigrateLegacyRegistrationCore);
        if (modeToPersist is { } mode)
        {
            PersistModeOutsideLock(mode);
        }
    }

    private StartupMode? TryMigrateLegacyRegistrationCore()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
                return null;

            DirectStartupTaskRegistration? task = _taskBackend.Read();
            bool ownsTask = task?.IsOwnedBy(executablePath) == true;
            bool isCompanionTask = task is not null && IsSameInstallCompanionTask(task, executablePath);
            bool hasLegacyTaskTwin = _taskBackend.HasCurrentUserLegacyTask(executablePath);
            string? runValue = _runEntryStore.Read();
            bool ownsRun = IsCommandOwnedBy(runValue, executablePath);
            bool isCompanionRun = IsSameInstallCompanionCommand(runValue, executablePath);
            bool ownsUserShortcut = IsLegacyShortcutOwnedBy(_legacyShortcutPath, executablePath);
            bool ownsCommonShortcut = IsLegacyShortcutOwnedBy(_legacyCommonShortcutPath, executablePath);
            bool ownsAnyShortcut = ownsUserShortcut || ownsCommonShortcut;

            Log($"Startup migration inspection: executable='{executablePath}' " +
                $"runTarget='{ExtractExecutablePath(runValue) ?? "none"}' " +
                $"ownsTask={ownsTask} hasLegacyTaskTwin={hasLegacyTaskTwin} " +
                $"ownsRun={ownsRun} ownsUserShortcut={ownsUserShortcut} ownsCommonShortcut={ownsCommonShortcut} " +
                $"process64Bit={Environment.Is64BitProcess}");

            if (!ownsTask && !ownsRun && !ownsAnyShortcut && !hasLegacyTaskTwin &&
                !isCompanionTask && !isCompanionRun)
            {
                return null;
            }

            // Respect Windows Startup apps opt-out on Run or Startup folder.
            bool runDisabledByWindows = (ownsRun || isCompanionRun) && !_runEntryApprovedProvider();
            bool shortcutDisabledByWindows = ownsUserShortcut && !_shortcutApprovedProvider();
            if (runDisabledByWindows || shortcutDisabledByWindows)
            {
                string error = RemoveOwnedAlternativeRegistrations(
                    executablePath,
                    preserveRun: runDisabledByWindows,
                    preserveUserShortcut: shortcutDisabledByWindows);
                Log(CombineErrors("Preserved Windows-disabled startup registration.", error));
                return null;
            }

            if ((ownsTask || isCompanionTask) && !task!.Enabled)
            {
                // Keep a disabled scheduled task disabled; older owned paths must not bypass it.
                DeleteLegacyRunEntryIfOwnedBy(executablePath);
                DeleteAllLegacyStartupShortcutsIfOwnedBy(executablePath);
                Log("Preserved task disabled by the user.");
                return null;
            }

            // Already cleanly on the single preferred SID-scoped logon task.
            if (ownsTask &&
                _taskBackend.IsPreferred(task!, executablePath) &&
                !hasLegacyTaskTwin &&
                !ownsRun &&
                !isCompanionRun &&
                !ownsAnyShortcut)
            {
                _selectedMode = StartupMode.ScheduledTask;
                return StartupMode.ScheduledTask;
            }

            // If Task Scheduler cannot even be queried right now, keep an existing
            // working Run entry untouched instead of failing or duplicating.
            if (_taskBackend.ReadFailed && ownsRun && !ownsAnyShortcut)
            {
                Log($"Task Scheduler read failed ({_taskBackend.LastError}); preserving existing Run registration.");
                _selectedMode = StartupMode.Standard;
                return StartupMode.Standard;
            }

            StartupOperationResult result = EnableCore(out StartupMode? modeToPersist);
            Log(result.State == StartupRegistrationState.Enabled
                ? $"Startup registration reconciled using {result.EffectiveMode} (fallback={result.UsedFallback})"
                : $"Startup migration deferred; previous registration preserved: {result.ErrorMessage}");
            return modeToPersist;
        }
        catch (Exception ex)
        {
            Log($"Startup migration failed: {ex.Message}");
            return null;
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

        if (existing is not null &&
            _taskBackend.IsPreferred(existing, executablePath) &&
            !_taskBackend.HasCurrentUserLegacyTask(executablePath))
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
    /// Windows' Startup apps page disables entries by flipping bit 0 under
    /// Explorer\StartupApproved\Run instead of deleting the Run value; the entry
    /// counts as enabled unless that state explicitly disables it.
    /// </summary>
    private static bool IsRunEntryApproved() =>
        IsStartupApprovedEntryEnabled(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            AppName);

    private static bool IsStartupFolderShortcutApproved() =>
        IsStartupApprovedEntryEnabled(
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder",
            LegacyShortcutFileName);

    internal static bool IsStartupApprovedByteEnabled(byte[]? state) =>
        state is null || state.Length == 0 || (state[0] & 1) == 0;

    private static bool IsStartupApprovedEntryEnabled(string registryPath, string valueName)
    {
        try
        {
            using RegistryKey? key = RegistryStartupRunEntryStore.OpenCurrentUserKey(
                registryPath,
                writable: false);
            return IsStartupApprovedByteEnabled(key?.GetValue(valueName) as byte[]);
        }
        catch
        {
            return true;
        }
    }

    private static string? ResolveDistinctCommonStartupShortcutPath()
    {
        try
        {
            string commonStartup = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);
            if (string.IsNullOrWhiteSpace(commonStartup))
            {
                return null;
            }

            return Path.Combine(commonStartup, LegacyShortcutFileName);
        }
        catch
        {
            return null;
        }
    }

    private static bool CommandTargetExists(string commandLine)
    {
        string? target = ExtractExecutablePath(commandLine);
        return !string.IsNullOrWhiteSpace(target) && File.Exists(target);
    }

    private string RemoveOwnedAlternativeRegistrations(
        string executablePath,
        bool preserveRun = true,
        bool preserveUserShortcut = false)
    {
        List<string> failures = [];
        DirectStartupTaskRegistration? task = _taskBackend.Read();
        bool hasOwnedOrCompanionTask =
            (task is not null && (task.IsOwnedBy(executablePath) || IsSameInstallCompanionTask(task, executablePath))) ||
            _taskBackend.HasCurrentUserLegacyTask(executablePath);
        if (hasOwnedOrCompanionTask && !_taskBackend.TryDelete(executablePath))
        {
            string failure =
                $"Failed to remove the superseded startup task: {_taskBackend.LastError}";
            failures.Add(failure);
            Log(failure);
        }

        if (!preserveRun)
        {
            try
            {
                DeleteLegacyRunEntryIfOwnedBy(executablePath);
            }
            catch (Exception ex)
            {
                failures.Add($"Failed to remove the superseded Run entry: {ex.Message}");
            }
        }

        if (!preserveUserShortcut &&
            !DeleteLegacyStartupShortcutIfOwnedBy(_legacyShortcutPath, executablePath, out string userShortcutError))
        {
            failures.Add(userShortcutError);
        }

        if (!DeleteLegacyStartupShortcutIfOwnedBy(_legacyCommonShortcutPath, executablePath, out string commonShortcutError))
        {
            failures.Add(commonShortcutError);
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
        string? current = _runEntryStore.Read();
        if (IsCommandOwnedBy(current, executablePath) ||
            IsSameInstallCompanionCommand(current, executablePath))
        {
            _runEntryStore.Delete();
            string? afterDelete = _runEntryStore.Read();
            if (IsCommandOwnedBy(afterDelete, executablePath) ||
                IsSameInstallCompanionCommand(afterDelete, executablePath))
            {
                throw new IOException("The owned Run entry still exists after removal.");
            }
            Log("Removed and verified the owned legacy Run entry");
        }
    }

    private bool IsLegacyShortcutOwnedBy(string? shortcutPath, string executablePath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            !File.Exists(shortcutPath))
        {
            return false;
        }

        try
        {
            string? target = _shortcutTargetReader(shortcutPath);
            return DirectStartupTaskBackend.PathsEqual(target, executablePath) ||
                   AppExecutablePath.IsSameInstallCompanionExecutable(target, executablePath);
        }
        catch
        {
            return false;
        }
    }

    private void DeleteAllLegacyStartupShortcutsIfOwnedBy(string executablePath) =>
        _ = DeleteAllLegacyStartupShortcutsIfOwnedBy(executablePath, out _);

    private bool DeleteAllLegacyStartupShortcutsIfOwnedBy(
        string executablePath,
        out string error)
    {
        List<string> failures = [];
        if (!DeleteLegacyStartupShortcutIfOwnedBy(_legacyShortcutPath, executablePath, out string userError))
        {
            failures.Add(userError);
        }

        if (!DeleteLegacyStartupShortcutIfOwnedBy(_legacyCommonShortcutPath, executablePath, out string commonError))
        {
            failures.Add(commonError);
        }

        error = string.Join("; ", failures);
        return failures.Count == 0;
    }

    private bool DeleteLegacyStartupShortcutIfOwnedBy(
        string? shortcutPath,
        string executablePath,
        out string error)
    {
        error = string.Empty;
        if (!IsLegacyShortcutOwnedBy(shortcutPath, executablePath) ||
            string.IsNullOrWhiteSpace(shortcutPath))
        {
            return true;
        }

        try
        {
            _shortcutDelete(shortcutPath);
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
