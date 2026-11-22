namespace DeskBox.Services;

/// <summary>
/// Compatibility facade for startup registration. The concrete implementation is selected by app distribution channel.
/// </summary>
public static class StartupService
{
    private static IStartupService s_current = StartupServiceFactory.Create(AppDistributionService.Current);

    public static event Action? StateChanged;

    public static IStartupService Current => s_current;

    internal static void NotifyStateChanged() => StateChanged?.Invoke();

    public static void Configure(IStartupService startupService)
    {
        s_current = startupService;
    }

    /// <summary>
    /// Check if DeskBox is registered for auto-start.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            return s_current.IsEnabled();
        }
        catch
        {
            return false;
        }
    }

    public static StartupRegistrationState GetState()
    {
        try
        {
            return s_current.GetState();
        }
        catch
        {
            return StartupRegistrationState.BlockedOrFailed;
        }
    }

    public static StartupOperationResult GetStatus()
    {
        try
        {
            return s_current.GetStatus();
        }
        catch (Exception ex)
        {
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message);
        }
    }

    public static Task<StartupOperationResult> GetStatusAsync()
    {
        try
        {
            return s_current.GetStatusAsync();
        }
        catch (Exception ex)
        {
            return Task.FromResult(new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message));
        }
    }

    public static string? GetRunValue()
    {
        try
        {
            return s_current.GetRunValue();
        }
        catch
        {
            return null;
        }
    }

    public static StartupOperationResult Enable()
    {
        try
        {
            return s_current.Enable();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupService] Failed to enable startup: {ex.Message}");
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message);
        }
    }

    public static StartupOperationResult Disable()
    {
        try
        {
            return s_current.Disable();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupService] Failed to disable startup: {ex.Message}");
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message);
        }
    }

    public static StartupOperationResult SetEnabled(bool enabled)
    {
        try
        {
            return s_current.SetEnabled(enabled);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupService] Failed to set startup: {ex.Message}");
            return new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message);
        }
    }

    public static Task<StartupOperationResult> SetEnabledAsync(bool enabled)
    {
        try
        {
            return s_current.SetEnabledAsync(enabled);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupService] Failed to set startup: {ex.Message}");
            return Task.FromResult(new StartupOperationResult(
                StartupRegistrationState.BlockedOrFailed,
                ex.Message));
        }
    }
}
