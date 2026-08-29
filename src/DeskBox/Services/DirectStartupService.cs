using DeskBox.Helpers;
using Microsoft.Win32;

namespace DeskBox.Services;

public sealed class DirectStartupService : IStartupService
{
    private const string AppName = "DeskBox";
    private readonly IDirectStartupTaskBackend _taskBackend;
    private readonly IDirectStartupRunEntryStore _runEntryStore;
    private readonly Func<string?> _executablePathProvider;
    private readonly string? _legacyShortcutPath;
    private readonly Func<string, string?> _shortcutTargetReader;
    private readonly Action<string> _shortcutDelete;
    private readonly Action<string> _log;
    private readonly Func<bool> _runEntryApprovedProvider;

    public DirectStartupService()
        : this(
            new DirectStartupTaskBackend(),
            new RegistryStartupRunEntryStore(),
            () => Environment.ProcessPath,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                AppName + ".lnk"),
            path => ShortcutHelper.ReadStoredMetadata(path)?.TargetPath,
            File.Delete,
            null,
            null)
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
        Func<bool>? runEntryApprovedProvider = null)
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
    }

    public bool IsEnabled()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
            {
                return false;
            }

            // The Run entry is the primary registration: visible in Windows'
            // Startup apps and user-toggleable there. When the user disables
            // DeskBox in that UI, the registry value survives but Windows marks
            // it disapproved — honor that state so the in-app toggle agrees.
            if (IsCommandOwnedBy(_runEntryStore.Read(), executablePath) &&
                _runEntryApprovedProvider())
            {
                return true;
            }

            DirectStartupTaskRegistration? task = _taskBackend.Read();
            return task is not null &&
                   task.Enabled &&
                   task.IsOwnedBy(executablePath);
        }
        catch
        {
            return false;
        }
    }

    public string? GetRunValue()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            string? runValue = _runEntryStore.Read();
            if (executablePath is not null &&
                IsCommandOwnedBy(runValue, executablePath) &&
                _runEntryApprovedProvider())
            {
                return runValue;
            }

            DirectStartupTaskRegistration? task = _taskBackend.Read();
            if (executablePath is not null &&
                task is not null &&
                task.Enabled &&
                task.IsOwnedBy(executablePath))
            {
                return task.CommandLine;
            }

            return runValue ?? task?.CommandLine;
        }
        catch
        {
            return null;
        }
    }

    public void Enable()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
            {
                Log("Cannot enable startup: the executable path is unavailable.");
                return;
            }

            if (TryEnableRunEntry(executablePath))
            {
                // Drop any owned scheduled task so logon launches DeskBox once.
                if (_taskBackend.Read() is { } task &&
                    task.IsOwnedBy(executablePath) &&
                    !_taskBackend.TryDelete())
                {
                    Log(
                        $"Failed to remove the superseded startup task: {_taskBackend.LastError}");
                }

                DeleteLegacyStartupShortcutIfOwnedBy(executablePath);
                Log("Startup enabled through the per-user Run entry");
                return;
            }

            if (TryEnableScheduledTask(executablePath))
            {
                DeleteLegacyRunEntryIfOwnedBy(executablePath);
                DeleteLegacyStartupShortcutIfOwnedBy(executablePath);
                return;
            }

            Log(
                "Startup could not be enabled: the Run entry was unavailable " +
                $"and task registration failed: {_taskBackend.LastError}");
        }
        catch (Exception ex)
        {
            Log($"Failed to enable startup: {ex.Message}");
        }
    }

    public void Disable()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
            {
                Log("Cannot disable startup: the executable path is unavailable.");
                return;
            }

            DirectStartupTaskRegistration? task = _taskBackend.Read();
            if (task is not null && task.IsOwnedBy(executablePath) && !_taskBackend.TryDelete())
            {
                Log($"Failed to delete the owned startup task: {_taskBackend.LastError}");
            }

            DeleteLegacyRunEntryIfOwnedBy(executablePath);
            DeleteLegacyStartupShortcutIfOwnedBy(executablePath);
            Log("Startup disabled");
        }
        catch (Exception ex)
        {
            Log($"Failed to disable startup: {ex.Message}");
        }
    }

    /// <summary>
    /// Migrates an owned scheduled task or Startup-folder shortcut to the
    /// per-user Run entry after it has been written and read back successfully.
    /// A failed migration deliberately leaves the old registration untouched.
    /// </summary>
    internal void TryMigrateLegacyRegistration()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
            {
                return;
            }

            DirectStartupTaskRegistration? existingTask = _taskBackend.Read();
            bool ownsTask = existingTask is not null &&
                            existingTask.IsOwnedBy(executablePath);
            bool ownsRunEntry = IsCommandOwnedBy(_runEntryStore.Read(), executablePath);
            bool ownsShortcut = IsLegacyShortcutOwnedBy(executablePath);
            if (!ownsTask && !ownsRunEntry && !ownsShortcut)
            {
                return;
            }

            if (!TryEnableRunEntry(executablePath))
            {
                if (ownsTask)
                {
                    Log(
                        "Startup migration deferred: the Run entry is unavailable, " +
                        $"the scheduled task remains: {_taskBackend.LastError}");
                }

                return;
            }

            if (ownsTask && !_taskBackend.TryDelete())
            {
                Log($"Failed to remove the migrated startup task: {_taskBackend.LastError}");
            }

            if (ownsShortcut)
            {
                DeleteLegacyStartupShortcutIfOwnedBy(executablePath);
            }

            Log("Migrated startup registration to the per-user Run entry");
        }
        catch (Exception ex)
        {
            Log($"Legacy startup migration failed and was preserved: {ex.Message}");
        }
    }

    private bool TryEnableRunEntry(string executablePath)
    {
        string? existing = _runEntryStore.Read();
        if (!string.IsNullOrWhiteSpace(existing) &&
            !IsCommandOwnedBy(existing, executablePath))
        {
            if (CommandTargetExists(existing))
            {
                Log($"Preserved Run entry owned by another installation: '{existing}'");
                return false;
            }

            Log(
                $"Taking over the orphaned Run entry pointing at a missing target: '{existing}'");
        }

        try
        {
            _runEntryStore.Write($"\"{executablePath}\" --startup");
            return true;
        }
        catch (Exception ex)
        {
            Log($"The per-user Run entry could not be written: {ex.Message}");
            return false;
        }
    }

    private bool TryEnableScheduledTask(string executablePath)
    {
        DirectStartupTaskRegistration? existing = _taskBackend.Read();
        if (existing is not null && !existing.IsOwnedBy(executablePath))
        {
            if (File.Exists(existing.ExecutablePath))
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
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
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

    private void DeleteLegacyRunEntryIfOwnedBy(string executablePath)
    {
        if (IsCommandOwnedBy(_runEntryStore.Read(), executablePath))
        {
            _runEntryStore.Delete();
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

    private void DeleteLegacyStartupShortcutIfOwnedBy(string executablePath)
    {
        if (!IsLegacyShortcutOwnedBy(executablePath) ||
            string.IsNullOrWhiteSpace(_legacyShortcutPath))
        {
            return;
        }

        try
        {
            _shortcutDelete(_legacyShortcutPath);
        }
        catch (Exception ex)
        {
            Log($"Failed to delete the owned legacy startup shortcut: {ex.Message}");
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

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Enable();
            return;
        }

        Disable();
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
    private const string AppName = "DeskBox";

    public string? Read()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            RegistryKeyPath,
            writable: false);
        return key?.GetValue(AppName) as string;
    }

    public void Write(string commandLine)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(
            RegistryKeyPath,
            writable: true);
        key.SetValue(AppName, commandLine, RegistryValueKind.String);
    }

    public void Delete()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            RegistryKeyPath,
            writable: true);
        key?.DeleteValue(AppName, throwOnMissingValue: false);
    }
}
