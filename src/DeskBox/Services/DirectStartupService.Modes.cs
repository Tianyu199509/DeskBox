using DeskBox.Models;

namespace DeskBox.Services;

public sealed partial class DirectStartupService
{
    private readonly Func<StartupMode?>? _modeProvider;
    private readonly Action<StartupMode>? _modeWriter;
    private StartupMode? _selectedMode;

    public StartupMode Mode =>
        ExecuteSerialized(() =>
        {
            string? executablePath = GetExecutablePath();
            return GetActiveMode(executablePath) ?? ResolveMode(executablePath);
        });

    private StartupMode? GetActiveMode(string? executablePath)
    {
        if (executablePath is null || GetStateCore() != StartupRegistrationState.Enabled)
            return null;
        DirectStartupTaskRegistration? task = _taskBackend.Read();
        if (task?.IsOwnedBy(executablePath) == true && task.Enabled)
            return StartupMode.ScheduledTask;
        return IsCommandOwnedBy(_runEntryStore.Read(), executablePath) ||
               IsLegacyShortcutOwnedBy(_legacyShortcutPath, executablePath) ||
               IsLegacyShortcutOwnedBy(_legacyCommonShortcutPath, executablePath)
            ? StartupMode.Standard : null;
    }

    private StartupMode ResolveMode(string? executablePath)
    {
        StartupMode? selected = _modeProvider?.Invoke() ?? _selectedMode;
        if (selected is StartupMode.Standard or StartupMode.ScheduledTask)
            return selected.Value;

        return executablePath is not null &&
               (IsCommandOwnedBy(_runEntryStore.Read(), executablePath) ||
                IsLegacyShortcutOwnedBy(_legacyShortcutPath, executablePath) ||
                IsLegacyShortcutOwnedBy(_legacyCommonShortcutPath, executablePath))
            ? StartupMode.Standard
            : StartupMode.ScheduledTask;
    }

    private void PersistModeOutsideLock(StartupMode mode)
    {
        if (_modeProvider?.Invoke() != mode)
            _modeWriter?.Invoke(mode);
    }

    private StartupOperationResult EnableStandardAndRemoveAlternatives(string executablePath)
    {
        string? previousRun = _runEntryStore.Read();
        DirectStartupTaskRegistration? previousTask = _taskBackend.Read();
        if (_taskBackend.ReadFailed)
            return new(StartupRegistrationState.BlockedOrFailed, _taskBackend.LastError);
        if ((!string.IsNullOrWhiteSpace(previousRun) &&
                !IsCommandOwnedBy(previousRun, executablePath) &&
                !IsSameInstallCompanionCommand(previousRun, executablePath) &&
                CommandTargetExists(previousRun)) ||
            (previousTask is not null &&
                !previousTask.IsOwnedBy(executablePath) &&
                !IsSameInstallCompanionTask(previousTask, executablePath) &&
                File.Exists(previousTask.ExecutablePath)))
            return new(StartupRegistrationState.PathMismatch, "Another installation owns the startup registration.");

        bool runApproved = _runEntryApprovedProvider();
        bool ownsPreviousRun = IsCommandOwnedBy(previousRun, executablePath);
        // Only short-circuit when our Run entry is ALREADY present for Windows
        // Startup Apps to show (and clean any bypassing task). When Run is absent
        // but a stale StartupApproved\Run opt-out persists, we still restore Run
        // and remove any superseded task before returning DisabledByUser so
        // Windows Startup Apps actually lists DeskBox and no stealth task survives.
        if (!runApproved && ownsPreviousRun)
        {
            string cleanupError = RemoveOwnedAlternativeRegistrations(executablePath, preserveRun: true);
            return new(StartupRegistrationState.DisabledByUser, cleanupError, EffectiveMode: StartupMode.Standard);
        }

        string command = $"\"{executablePath}\" --startup --startup-source=run";
        // Windows Run entries have a documented 260-character command limit.
        if (command.Length > 260)
            return new(StartupRegistrationState.BlockedOrFailed, "The standard startup command exceeds 260 characters.");

        try
        {
            if (!string.Equals(previousRun, command, StringComparison.Ordinal))
                _runEntryStore.Write(command);
            if (!string.Equals(_runEntryStore.Read(), command, StringComparison.Ordinal))
                throw new IOException("The standard startup entry did not match after writing.");
            if (!DeleteAllLegacyStartupShortcutsIfOwnedBy(executablePath, out string shortcutError))
                throw new IOException(shortcutError);
            // Delete any previous task (SID-scoped or legacy twin) last. Any
            // earlier failure leaves it intact.
            if ((previousTask is not null || _taskBackend.HasCurrentUserLegacyTask(executablePath)) &&
                !_taskBackend.TryDelete(executablePath))
                throw new IOException($"The previous startup task could not be removed: {_taskBackend.LastError}");
        }
        catch (Exception ex)
        {
            return new(StartupRegistrationState.BlockedOrFailed,
                CombineErrors(ex.Message, RestoreRunValue(previousRun)));
        }

        if (!runApproved)
        {
            Log("Restored standard Run entry so Windows Startup Apps can re-enable DeskBox");
            return new(StartupRegistrationState.DisabledByUser, EffectiveMode: StartupMode.Standard);
        }

        Log("Startup enabled through the standard Run entry");
        return new(StartupRegistrationState.Enabled, EffectiveMode: StartupMode.Standard);
    }

    private string RestoreRunValue(string? previous)
    {
        try
        {
            if (previous is null)
                _runEntryStore.Delete();
            else if (!string.Equals(_runEntryStore.Read(), previous, StringComparison.Ordinal))
                _runEntryStore.Write(previous);
            return string.Equals(_runEntryStore.Read(), previous, StringComparison.Ordinal)
                ? string.Empty : "The previous Run entry could not be restored.";
        }
        catch (Exception ex)
        {
            return $"Run rollback failed: {ex.Message}";
        }
    }
}
