using DeskBox.Models;
using DeskBox.Contracts;

namespace DeskBox.Services;

/// <summary>
/// Snapshot of the user-configured cloud-backup state, resolved from
/// <see cref="AppSettings"/> in one place — mirrors
/// <see cref="DataBackupSettingsPolicy"/> for the local snapshot path.
/// </summary>
internal sealed record CloudBackupOptions(
    string Provider,
    string ServerUrl,
    string RemotePath,
    string Username,
    CloudBackupDomain Scope,
    int RetentionCount,
    int IntervalMinutes,
    DateTimeOffset LastSuccessUtc)
{
    internal BackupEndpoint Endpoint => new(Provider, ServerUrl, RemotePath, Username);
    /// <summary>
    /// Provider selected and URL parseable — enough to reach the endpoint.
    /// Backup scope deliberately stays out: probing the server, storing a
    /// credential and listing/downloading remote snapshots (restore side,
    /// whose domain pick happens in the restore dialog) are endpoint-level
    /// operations that must work before the user chooses what to back up.
    /// </summary>
    internal bool HasEndpoint =>
        Provider != CloudBackupSettingsPolicy.ProviderNone &&
        Uri.TryCreate(ServerUrl, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    /// <summary>
    /// Endpoint reachable AND at least one backup domain on — the bar for
    /// actually uploading a snapshot.
    /// </summary>
    internal bool IsConfigured =>
        HasEndpoint &&
        Scope != CloudBackupDomain.None;

    /// <summary>
    /// Plain-HTTP endpoint: credentials travel base64 on a cleartext
    /// channel. Still allowed for LAN NAS endpoints, but the settings UI
    /// surfaces an explicit warning and the credential key includes the
    /// scheme so switching to http always re-prompts for the password.
    /// </summary>
    internal bool UsesPlainHttp =>
        Uri.TryCreate(ServerUrl, UriKind.Absolute, out Uri? uri) &&
        uri.Scheme == Uri.UriSchemeHttp;
}

internal static class CloudBackupSettingsPolicy
{
    // Canonical values/normalization live in Contracts (batch 49) so the
    // backup settings editor shares them without referencing Services.
    internal const string ProviderNone = BackupOptionKinds.ProviderNone;
    internal const string ProviderWebDav = BackupOptionKinds.ProviderWebDav;
    internal const int DefaultRetentionCount = BackupOptionKinds.DefaultCloudRetentionCount;
    internal const int DefaultIntervalMinutes = BackupOptionKinds.DefaultCloudIntervalMinutes;

    /// <summary>Preset interval choices for the settings ComboBox.</summary>
    internal static readonly int[] SupportedIntervalMinutes = BackupOptionKinds.CloudIntervalMinutes;

    /// <summary>Preset retention choices for the settings ComboBox.</summary>
    internal static readonly int[] SupportedRetentionCounts = BackupOptionKinds.CloudRetentionCounts;

    internal static int NormalizeIntervalMinutes(int minutes) =>
        BackupOptionKinds.NormalizeCloudIntervalMinutes(minutes);

    internal static int NormalizeRetentionCount(int count) =>
        BackupOptionKinds.NormalizeCloudRetentionCount(count);

    internal static CloudBackupOptions GetOptions(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        CloudBackupSettingsSlice slice = settings.CloudBackup;
        CloudBackupDomain scope = CloudBackupDomain.None;
        if (slice.CloudBackupTodoDataEnabled)
        {
            scope |= CloudBackupDomain.TodoData;
        }

        if (slice.CloudBackupQuickCaptureDataEnabled)
        {
            scope |= CloudBackupDomain.QuickCaptureData;
        }

        if (slice.CloudBackupWidgetStyleEnabled)
        {
            scope |= CloudBackupDomain.WidgetStyle;
        }

        if (slice.CloudBackupCustomWidgetsEnabled)
        {
            scope |= CloudBackupDomain.CustomWidgets;
        }

        return new CloudBackupOptions(
            Provider: slice.CloudBackupProvider?.Trim().ToLowerInvariant() is { Length: > 0 } provider
                ? provider
                : ProviderNone,
            ServerUrl: slice.CloudBackupServerUrl?.Trim() ?? string.Empty,
            RemotePath: NormalizeRemotePath(slice.CloudBackupRemotePath),
            Username: slice.CloudBackupUsername?.Trim() ?? string.Empty,
            Scope: scope,
            RetentionCount: Math.Clamp(slice.CloudBackupRetentionCount, 1, 50),
            IntervalMinutes: Math.Clamp(slice.CloudBackupIntervalMinutes, 5, 30 * 24 * 60),
            LastSuccessUtc: slice.CloudBackupLastSuccessUtcTicks > 0
                ? new DateTimeOffset(slice.CloudBackupLastSuccessUtcTicks, TimeSpan.Zero)
                : DateTimeOffset.MinValue);
    }

    /// <summary>
    /// Vault key for the provider secret. Scoped by provider + endpoint
    /// (scheme + host + effective port + DAV base path) + username: one
    /// host can reverse-proxy several DAV services/tenants under different
    /// base paths, and the wrong tenant must never receive a stored
    /// secret. The remote path deliberately stays out: it is a folder
    /// choice inside the same endpoint, not an auth boundary.
    /// </summary>
    internal static string CredentialKey(CloudBackupOptions options)
    {
        if (Uri.TryCreate(options.ServerUrl, UriKind.Absolute, out Uri? uri))
        {
            string origin = uri.IsDefaultPort
                ? $"{uri.Scheme}://{uri.Host}"
                : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            string basePath = uri.AbsolutePath.TrimEnd('/');
            return $"{options.Provider}:{options.Username}@{origin.ToLowerInvariant()}{basePath}";
        }

        return $"{options.Provider}:{options.Username}@invalid";
    }

    private static string NormalizeRemotePath(string? remotePath)
    {
        string trimmed = remotePath?.Trim().Trim('/') ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return "DeskBox/backups";
        }

        // The path is joined verbatim into request URIs — a ".." segment
        // would escape the configured folder on the user's own server.
        return trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment is ".." or ".")
            ? "DeskBox/backups"
            : trimmed;
    }
}
