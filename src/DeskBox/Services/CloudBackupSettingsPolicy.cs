using DeskBox.Models;

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
    /// <summary>Provider selected, URL parseable, at least one domain on.</summary>
    internal bool IsConfigured =>
        Provider != CloudBackupSettingsPolicy.ProviderNone &&
        Uri.TryCreate(ServerUrl, UriKind.Absolute, out Uri? uri) &&
        (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
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
    internal const string ProviderNone = "none";
    internal const string ProviderWebDav = "webdav";
    internal const int DefaultRetentionCount = 5;
    internal const int DefaultIntervalMinutes = 24 * 60;

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
    /// Vault key for the provider secret. Scoped by provider + origin
    /// (scheme + host + effective port) + username: Basic credentials are
    /// per-origin, so a port or scheme change must never silently reuse a
    /// secret — and https→http downgrades always re-prompt. The remote
    /// path deliberately stays out: it is a folder choice on the same
    /// origin, not an auth boundary.
    /// </summary>
    internal static string CredentialKey(CloudBackupOptions options)
    {
        if (Uri.TryCreate(options.ServerUrl, UriKind.Absolute, out Uri? uri))
        {
            string origin = uri.IsDefaultPort
                ? $"{uri.Scheme}://{uri.Host}"
                : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            return $"{options.Provider}:{options.Username}@{origin.ToLowerInvariant()}";
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
