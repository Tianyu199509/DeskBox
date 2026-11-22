using System.Text.Json.Serialization;
using DeskBox.Models;

namespace DeskBox.Services;

public enum StartupRegistrationState
{
    Enabled,
    DisabledByUser,
    NotRegistered,
    PathMismatch,
    Pending,
    BlockedOrFailed,
    DisabledByTaskScheduler
}

public sealed record StartupOperationResult(
    StartupRegistrationState State,
    string ErrorMessage = "",
    StartupMode? EffectiveMode = null,
    bool UsedFallback = false)
{
    [JsonIgnore]
    public bool IsEnabled => State == StartupRegistrationState.Enabled;

    [JsonIgnore]
    public bool RequiresSystemSettings =>
        State == StartupRegistrationState.DisabledByUser;
}

public interface IStartupService
{
    StartupRegistrationState GetState();
    StartupOperationResult GetStatus() => new(GetState());
    bool IsEnabled();
    string? GetRunValue();
    StartupOperationResult Enable();
    StartupOperationResult Disable();
    StartupOperationResult SetEnabled(bool enabled);
    Task<StartupOperationResult> GetStatusAsync() => Task.Run(GetStatus);
    Task<StartupOperationResult> EnableAsync() => Task.Run(Enable);
    Task<StartupOperationResult> DisableAsync() => Task.Run(Disable);
    Task<StartupOperationResult> SetEnabledAsync(bool enabled) => Task.Run(() => SetEnabled(enabled));
}
