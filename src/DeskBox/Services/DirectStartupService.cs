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

    public DirectStartupService()
        : this(
            new DirectStartupTaskBackend(),
            new RegistryStartupRunEntryStore(),
            () => Environment.ProcessPath,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                AppName + ".lnk"),
            path => ShortcutHelper.ReadStoredMetadata(path)?.TargetPath,
            File.Delete)
    {
    }

    internal DirectStartupService(
        IDirectStartupTaskBackend taskBackend,
        IDirectStartupRunEntryStore runEntryStore,
        Func<string?> executablePathProvider,
        string? legacyShortcutPath = null,
        Func<string, string?>? shortcutTargetReader = null,
        Action<string>? shortcutDelete = null,
        Action<string>? logger = null)
    {
        _taskBackend = taskBackend;
        _runEntryStore = runEntryStore;
        _executablePathProvider = executablePathProvider;
        _legacyShortcutPath = legacyShortcutPath;
        _shortcutTargetReader = shortcutTargetReader ?? (_ => null);
        _shortcutDelete = shortcutDelete ?? (_ => { });
        _log = logger ?? (message =>
            global::DeskBox.App.Log($"[DirectStartupService] {message}"));
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

            DirectStartupTaskRegistration? task = _taskBackend.Read();
            if (task is not null &&
                task.Enabled &&
                task.IsOwnedBy(executablePath))
            {
                return true;
            }

            return IsCommandOwnedBy(_runEntryStore.Read(), executablePath);
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
            DirectStartupTaskRegistration? task = _taskBackend.Read();
            if (executablePath is not null &&
                task is not null &&
                task.Enabled &&
                task.IsOwnedBy(executablePath))
            {
                return task.CommandLine;
            }

            return _runEntryStore.Read() ?? task?.CommandLine;
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
                return;
            }

            if (TryEnableScheduledTask(executablePath))
            {
                DeleteLegacyRunEntryIfOwnedBy(executablePath);
                DeleteLegacyStartupShortcutIfOwnedBy(executablePath);
                return;
            }

            // Registration can be blocked by a damaged Task Scheduler service or
            // policy. Preserve the existing behavior as a non-elevated fallback so
            // enabling startup never silently leaves the user with no registration.
            EnsureLegacyRunFallback(executablePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirectStartupService] Failed to enable startup: {ex.Message}");
        }
    }

    public void Disable()
    {
        try
        {
            string? executablePath = GetExecutablePath();
            if (executablePath is null)
            {
                return;
            }

            DirectStartupTaskRegistration? task = _taskBackend.Read();
            if (task is not null && task.IsOwnedBy(executablePath) && !_taskBackend.TryDelete())
            {
                Log($"Failed to delete the owned startup task: {_taskBackend.LastError}");
            }

            DeleteLegacyRunEntryIfOwnedBy(executablePath);
            DeleteLegacyStartupShortcutIfOwnedBy(executablePath);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DirectStartupService] Failed to disable startup: {ex.Message}");
        }
    }

    /// <summary>
    /// Migrates an existing Run-key or Startup-folder registration only after a
    /// least-privilege scheduled task has been created and read back successfully.
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

            bool ownsRunEntry = IsCommandOwnedBy(_runEntryStore.Read(), executablePath);
            bool ownsShortcut = IsLegacyShortcutOwnedBy(executablePath);
            DirectStartupTaskRegistration? existingTask = _taskBackend.Read();
            bool ownsLegacyTask =
                existingTask is not null &&
                existingTask.IsOwnedBy(executablePath) &&
                !_taskBackend.IsPreferred(existingTask, executablePath);
            if (!ownsRunEntry && !ownsShortcut && !ownsLegacyTask)
            {
                return;
            }

            if (!TryEnableScheduledTask(executablePath))
            {
                Log($"Legacy startup migration deferred: {_taskBackend.LastError}");
                return;
            }

            if (ownsRunEntry)
            {
                _runEntryStore.Delete();
            }
            if (ownsShortcut)
            {
                DeleteLegacyStartupShortcutIfOwnedBy(executablePath);
            }

            Log("Migrated legacy startup registration to the least-privilege logon task");
        }
        catch (Exception ex)
        {
            Log($"Legacy startup migration failed and was preserved: {ex.Message}");
        }
    }

    private bool TryEnableScheduledTask(string executablePath)
    {
        DirectStartupTaskRegistration? existing = _taskBackend.Read();
        if (existing is not null && !existing.IsOwnedBy(executablePath))
        {
            Log(
                $"Preserved startup task owned by another installation: " +
                $"'{existing.ExecutablePath}'");
            return false;
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

    private void EnsureLegacyRunFallback(string executablePath)
    {
        string? existing = _runEntryStore.Read();
        if (!string.IsNullOrWhiteSpace(existing) &&
            !IsCommandOwnedBy(existing, executablePath))
        {
            Log($"Preserved Run entry owned by another installation: '{existing}'");
            return;
        }

        _runEntryStore.Write($"\"{executablePath}\" --startup");
        Log("Using the legacy per-user Run entry because task registration was unavailable");
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
