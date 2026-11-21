using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Applies the product default for autostart exactly once. Desktop widgets are
/// only useful once they come back with the session, so an installation whose
/// user never made a choice is switched on a single time; from then on the
/// choice belongs to the user, and any state Windows reports as disabled is
/// left exactly as it is.
/// </summary>
internal static class AutoStartDefaultPolicy
{
    internal static bool ShouldApply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return !settings.Core.AutoStartDefaultApplied;
    }

    /// <summary>
    /// Returns the registration state the settings mirror should record.
    /// Only a never-registered startup is enabled — a task that exists but is
    /// disabled (Task Manager, policy, another installation) is never
    /// overridden, and a Windows-consented enable reports <see
    /// cref="StartupRegistrationState.Pending"/>.
    /// </summary>
    internal static StartupRegistrationState Resolve(IStartupService startupService)
    {
        ArgumentNullException.ThrowIfNull(startupService);
        StartupRegistrationState state = startupService.GetState();
        return state == StartupRegistrationState.NotRegistered
            ? startupService.Enable().State
            : state;
    }

    internal static async Task<StartupOperationResult> ResolveAsync(IStartupService startupService)
    {
        ArgumentNullException.ThrowIfNull(startupService);
        StartupOperationResult status = await startupService.GetStatusAsync().ConfigureAwait(false);
        return status.State == StartupRegistrationState.NotRegistered
            ? await startupService.EnableAsync().ConfigureAwait(false)
            : status;
    }

    internal static bool IsEnabledState(StartupRegistrationState state) =>
        state is StartupRegistrationState.Enabled or StartupRegistrationState.Pending;

    /// <summary>
    /// A blocked or failed attempt must not consume the one-time default: the
    /// next launch retries once the machine condition clears. Every other
    /// outcome (including every deliberate user or Windows disable) is final
    /// and still marks the default as applied.
    /// </summary>
    internal static bool ShouldMarkApplied(StartupRegistrationState effectiveState) =>
        effectiveState != StartupRegistrationState.BlockedOrFailed;

    internal static bool HasAutoStartDefaultAppliedProperty(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "autoStartDefaultApplied", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            // Deserialization already succeeded; treat unexpected shapes as absent.
        }

        return false;
    }

    /// <summary>
    /// Legacy settings.json files written before <c>autoStartDefaultApplied</c>
    /// existed omit the property completely. When such a profile explicitly has
    /// <c>autoStart=false</c>, mark the one-time default as already applied so
    /// upgrading does not silently re-enable autostart. Modern installs whose
    /// first attempt hit <see cref="StartupRegistrationState.BlockedOrFailed"/>
    /// persist explicit <c>"autoStartDefaultApplied": false</c> and keep retrying.
    /// </summary>
    internal static bool TryPreserveLegacyOptOut(
        AppSettings settings,
        bool loadedFromDisk,
        bool hadAutoStartDefaultAppliedProperty)
    {
        if (loadedFromDisk &&
            !hadAutoStartDefaultAppliedProperty &&
            !settings.Core.AutoStart &&
            !settings.Core.AutoStartDefaultApplied)
        {
            settings.Core.AutoStartDefaultApplied = true;
            return true;
        }

        return false;
    }
}
