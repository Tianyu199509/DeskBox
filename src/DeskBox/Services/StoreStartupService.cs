using Windows.ApplicationModel;

namespace DeskBox.Services;

public sealed class StoreStartupService : IStartupService
{
    private const string StartupTaskId = "DeskBoxStartupTask";

    public StartupRegistrationState GetState()
    {
        try
        {
            StartupTask task = GetStartupTask();
            return MapState(task.State);
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[StoreStartupService] Failed to query startup state: {ex.Message}");
            return StartupRegistrationState.BlockedOrFailed;
        }
    }

    public StartupOperationResult GetStatus() => new(GetState());

    public async Task<StartupOperationResult> GetStatusAsync()
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(StartupTaskId).AsTask().ConfigureAwait(false);
            return new StartupOperationResult(MapState(task.State));
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[StoreStartupService] Failed to query startup state: {ex.Message}");
            return new StartupOperationResult(StartupRegistrationState.BlockedOrFailed, ex.Message);
        }
    }

    public bool IsEnabled() => GetState() == StartupRegistrationState.Enabled;

    public string? GetRunValue() => null;

    public StartupOperationResult Enable()
    {
        try
        {
            StartupTask task = GetStartupTask();
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

                        StartupService.NotifyStateChanged();
                    }, TaskScheduler.Default);
                    return new StartupOperationResult(StartupRegistrationState.Pending);
                }

                // StartupTask.RequestEnableAsync requires the UI thread when a
                // dispatcher is active; marshal onto it rather than calling from MTA.
                StartupTaskState requestedState = RequestEnableOnUiOrCurrentAsync(task).GetAwaiter().GetResult();
                global::DeskBox.App.Log($"[StoreStartupService] StartupTask enable completed: {requestedState}");
                return MapRequestedEnableState(requestedState);
            }

            return MapNonEnableableState(task.State);
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[StoreStartupService] Failed to enable startup: {ex.Message}");
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message);
        }
    }

    public async Task<StartupOperationResult> EnableAsync()
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(StartupTaskId).AsTask().ConfigureAwait(false);
            if (task.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy)
            {
                return new StartupOperationResult(StartupRegistrationState.Enabled);
            }

            if (task.State == StartupTaskState.Disabled)
            {
                StartupTaskState requestedState = await RequestEnableOnUiOrCurrentAsync(task).ConfigureAwait(false);
                global::DeskBox.App.Log($"[StoreStartupService] StartupTask enable completed: {requestedState}");
                return MapRequestedEnableState(requestedState);
            }

            return MapNonEnableableState(task.State);
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
            StartupTask task = GetStartupTask();
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

    public async Task<StartupOperationResult> DisableAsync()
    {
        try
        {
            StartupTask task = await StartupTask.GetAsync(StartupTaskId).AsTask().ConfigureAwait(false);
            if (task.State == StartupTaskState.Enabled)
            {
                task.Disable();
            }

            StartupOperationResult status = await GetStatusAsync().ConfigureAwait(false);
            StartupService.NotifyStateChanged();
            return status;
        }
        catch (Exception ex)
        {
            global::DeskBox.App.Log($"[StoreStartupService] Failed to disable startup: {ex.Message}");
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message);
        }
    }

    public StartupOperationResult SetEnabled(bool enabled) =>
        enabled ? Enable() : Disable();

    public Task<StartupOperationResult> SetEnabledAsync(bool enabled) =>
        enabled ? EnableAsync() : DisableAsync();

    internal static StartupRegistrationState MapState(StartupTaskState state) =>
        state switch
        {
            StartupTaskState.Enabled => StartupRegistrationState.Enabled,
            StartupTaskState.EnabledByPolicy => StartupRegistrationState.Enabled,
            StartupTaskState.DisabledByUser => StartupRegistrationState.DisabledByUser,
            StartupTaskState.Disabled => StartupRegistrationState.NotRegistered,
            StartupTaskState.DisabledByPolicy => StartupRegistrationState.BlockedOrFailed,
            _ => StartupRegistrationState.BlockedOrFailed
        };

    internal static StartupOperationResult MapRequestedEnableState(StartupTaskState requestedState) =>
        requestedState switch
        {
            StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy =>
                new StartupOperationResult(StartupRegistrationState.Enabled),
            StartupTaskState.DisabledByUser =>
                new StartupOperationResult(StartupRegistrationState.DisabledByUser),
            StartupTaskState.Disabled =>
                new StartupOperationResult(
                    StartupRegistrationState.NotRegistered,
                    "Windows did not enable the DeskBox startup task."),
            StartupTaskState.DisabledByPolicy =>
                new StartupOperationResult(
                    StartupRegistrationState.BlockedOrFailed,
                    "Windows policy has disabled the DeskBox startup task."),
            _ => new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                $"Unexpected StartupTask state: {requestedState}")
        };

    private static StartupOperationResult MapNonEnableableState(StartupTaskState state)
    {
        if (state == StartupTaskState.DisabledByUser)
        {
            const string message =
                "Windows Startup apps has disabled the DeskBox startup task.";
            global::DeskBox.App.Log($"[StoreStartupService] {message}");
            return new StartupOperationResult(StartupRegistrationState.DisabledByUser);
        }

        string failure = $"StartupTask cannot be enabled from app state: {state}";
        global::DeskBox.App.Log($"[StoreStartupService] {failure}");
        return new StartupOperationResult(StartupRegistrationState.BlockedOrFailed, failure);
    }

    private static async Task<StartupTaskState> RequestEnableOnUiOrCurrentAsync(StartupTask task)
    {
        if (DeskBox.Platform.UiDispatch.Phase == DeskBox.Platform.UiDispatch.PhaseKind.Ready &&
            !DeskBox.Platform.UiDispatch.HasAccess)
        {
            return await DeskBox.Platform.UiDispatch.RunAsync(
                async () => await task.RequestEnableAsync().AsTask()).ConfigureAwait(false);
        }

        return await task.RequestEnableAsync().AsTask().ConfigureAwait(false);
    }

    private static StartupTask GetStartupTask()
    {
        // Execute off any captured UI SynchronizationContext so querying
        // StartupTask.GetAsync from WinUI's STA thread cannot deadlock.
        return Task.Run(
            async () => await StartupTask.GetAsync(StartupTaskId)).GetAwaiter().GetResult();
    }
}
