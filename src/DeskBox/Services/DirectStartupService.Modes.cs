using DeskBox.Models;

namespace DeskBox.Services;

public sealed partial class DirectStartupService
{
    private readonly Func<StartupMode?>? _modeProvider;
    private readonly Action<StartupMode>? _modeWriter;
    private StartupMode? _selectedMode;

    public StartupMode Mode
    {
        get
        {
            lock (_registrationLock)
            {
                string? executablePath = GetExecutablePath();
                return GetActiveMode(executablePath) ?? ResolveMode(executablePath);
            }
        }
    }

    private StartupMode? GetActiveMode(string? executablePath)
    {
        if (executablePath is null || GetStateCore() != StartupRegistrationState.Enabled)
            return null;
        DirectStartupTaskRegistration? task = _taskBackend.Read();
        if (task?.IsOwnedBy(executablePath) == true && task.Enabled)
            return StartupMode.ScheduledTask;
        return IsCommandOwnedBy(_runEntryStore.Read(), executablePath) || IsLegacyShortcutOwnedBy(executablePath)
            ? StartupMode.Standard : null;
    }

    private StartupMode ResolveMode(string? executablePath)
    {
        StartupMode? selected = _modeProvider?.Invoke() ?? _selectedMode;
        if (selected is StartupMode.Standard or StartupMode.ScheduledTask)
            return selected.Value;

        // Preserve an existing task (including a disabled one) on upgrade.
        // An existing Run entry, shortcut, or a fresh installation uses Standard.
        return executablePath is not null && _taskBackend.Read()?.IsOwnedBy(executablePath) == true
            ? StartupMode.ScheduledTask
            : StartupMode.Standard;
    }

    private void SaveMode(StartupMode mode)
    {
        _selectedMode = mode;
        if (_modeProvider?.Invoke() != mode)
            _modeWriter?.Invoke(mode);
    }

    public StartupOperationResult SetMode(StartupMode mode)
    {
        if (mode is not (StartupMode.Standard or StartupMode.ScheduledTask))
            throw new ArgumentOutOfRangeException(nameof(mode));

        lock (_registrationLock)
        {
            StartupMode previous = Mode;
            StartupRegistrationState state = GetStateCore();
            if (state is StartupRegistrationState.PathMismatch or StartupRegistrationState.BlockedOrFailed)
                return new(state, EffectiveMode: previous);

            // Selecting a method while off must not bypass a Windows opt-out.
            if (state != StartupRegistrationState.Enabled)
            {
                SaveMode(mode);
                return new(state, EffectiveMode: mode);
            }

            string? executablePath = GetExecutablePath();
            if (executablePath is null)
                return new(StartupRegistrationState.BlockedOrFailed, EffectiveMode: previous);
            try
            {
                StartupOperationResult result = mode == StartupMode.ScheduledTask
                    ? EnableTaskAndRemoveLegacyEntries(executablePath)
                    : EnableStandardAndRemoveAlternatives(executablePath);
                if (result.IsEnabled)
                    SaveMode(result.EffectiveMode ?? mode);
                else
                    SaveMode(previous);
                return result with { EffectiveMode = result.EffectiveMode ?? previous };
            }
            catch (Exception ex)
            {
                return new(StartupRegistrationState.BlockedOrFailed, ex.Message, previous);
            }
        }
    }

    private StartupOperationResult EnableStandardAndRemoveAlternatives(string executablePath)
    {
        string? previousRun = _runEntryStore.Read();
        DirectStartupTaskRegistration? previousTask = _taskBackend.Read();
        if (_taskBackend.ReadFailed)
            return new(StartupRegistrationState.BlockedOrFailed, _taskBackend.LastError);
        if ((!string.IsNullOrWhiteSpace(previousRun) &&
                !IsCommandOwnedBy(previousRun, executablePath) && CommandTargetExists(previousRun)) ||
            (previousTask is not null && !previousTask.IsOwnedBy(executablePath) &&
                File.Exists(previousTask.ExecutablePath)))
            return new(StartupRegistrationState.PathMismatch, "Another installation owns the startup registration.");
        bool runApproved = _runEntryApprovedProvider();
        if (!runApproved && (IsCommandOwnedBy(previousRun, executablePath) || previousTask is not null))
            return new(StartupRegistrationState.DisabledByUser);

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
            if (!DeleteLegacyStartupShortcutIfOwnedBy(executablePath, out string shortcutError))
                throw new IOException(shortcutError);
            // Delete the previous task last. Any earlier failure leaves it intact.
            if (previousTask is not null && !_taskBackend.TryDelete())
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
