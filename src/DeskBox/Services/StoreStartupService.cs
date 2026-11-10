using Windows.ApplicationModel;

namespace DeskBox.Services;

public sealed class StoreStartupService : IStartupService
{
    private const string StartupTaskId = "DeskBoxStartupTask";

    public StartupRegistrationState GetState()
    {
        try
        {
            var task = GetStartupTask();
            return task.State switch
            {
                StartupTaskState.Enabled => StartupRegistrationState.Enabled,
                StartupTaskState.EnabledByPolicy => StartupRegistrationState.Enabled,
                StartupTaskState.DisabledByUser => StartupRegistrationState.DisabledByUser,
                StartupTaskState.Disabled => StartupRegistrationState.NotRegistered,
                StartupTaskState.DisabledByPolicy => StartupRegistrationState.BlockedOrFailed,
                _ => StartupRegistrationState.BlockedOrFailed
            };
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[StoreStartupService] Failed to query startup state: {ex.Message}");
            return StartupRegistrationState.BlockedOrFailed;
        }
    }

    public bool IsEnabled() => GetState() == StartupRegistrationState.Enabled;

    public string? GetRunValue() => null;

    public StartupOperationResult Enable()
    {
        try
        {
            var task = GetStartupTask();
            if (task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
            {
                return new StartupOperationResult(StartupRegistrationState.Enabled);
            }

            if (task.State == StartupTaskState.Disabled)
            {
                if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
                {
                    // RequestEnableAsync may show a consent dialog that pumps
                    // UI messages; never synchronously block the STA UI thread.
                    _ = task.RequestEnableAsync().AsTask().ContinueWith(t =>
                    {
                        if (t.IsCompletedSuccessfully)
                        {
                            global::DeskBox.App.Log($"[StoreStartupService] StartupTask enable requested: {t.Result}");
                        }
                        else if (t.IsFaulted)
                        {
                            global::DeskBox.App.Log($"[StoreStartupService] StartupTask enable failed: {t.Exception?.GetBaseException()?.Message}");
                        }
                    }, TaskScheduler.Default);
                    return new StartupOperationResult(StartupRegistrationState.Pending);
                }

                StartupTaskState requestedState = Task.Run(
                    async () => await task.RequestEnableAsync()).GetAwaiter().GetResult();
                global::DeskBox.App.Log($"[StoreStartupService] StartupTask enable completed: {requestedState}");
                return requestedState switch
                {
                    StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy =>
                        new StartupOperationResult(StartupRegistrationState.Enabled),
                    StartupTaskState.DisabledByUser =>
                        new StartupOperationResult(StartupRegistrationState.DisabledByUser),
                    _ => new StartupOperationResult(StartupRegistrationState.Pending)
                };
            }

            if (task.State == StartupTaskState.DisabledByUser)
            {
                const string message =
                    "Windows Startup apps has disabled the DeskBox startup task.";
                global::DeskBox.App.Log($"[StoreStartupService] {message}");
                return new StartupOperationResult(
                    StartupRegistrationState.DisabledByUser);
            }

            string failure =
                $"StartupTask cannot be enabled from app state: {task.State}";
            global::DeskBox.App.Log($"[StoreStartupService] {failure}");
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                failure);
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[StoreStartupService] Failed to enable startup: {ex.Message}");
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message);
        }
    }

    public StartupOperationResult Disable()
    {
        try
        {
            var task = GetStartupTask();
            if (task.State == StartupTaskState.Enabled)
            {
                task.Disable();
            }

            return new StartupOperationResult(GetState());
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[StoreStartupService] Failed to disable startup: {ex.Message}");
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message);
        }
    }

    public StartupOperationResult SetEnabled(bool enabled)
    {
        if (enabled)
        {
            return Enable();
        }

        return Disable();
    }

    private static StartupTask GetStartupTask()
    {
        // Execute off any captured UI SynchronizationContext so querying
        // StartupTask.GetAsync from WinUI's STA thread cannot deadlock.
        return Task.Run(
            async () => await StartupTask.GetAsync(StartupTaskId)).GetAwaiter().GetResult();
    }
}
