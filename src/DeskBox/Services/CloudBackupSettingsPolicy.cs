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
    /// Vault key for the provider secret. Scoped by provider + server host +
    /// username so reconfiguring to a different endpoint never reuses a
    /// stale credential.
    /// </summary>
    internal static string CredentialKey(CloudBackupOptions options)
    {
        string host = Uri.TryCreate(options.ServerUrl, UriKind.Absolute, out Uri? uri)
            ? uri.Host
            : "invalid";
        return $"{options.Provider}:{options.Username}@{host}";
    }

    private static string NormalizeRemotePath(string? remotePath)
    {
        string trimmed = remotePath?.Trim().Trim('/') ?? string.Empty;
        return trimmed.Length == 0 ? "DeskBox/backups" : trimmed;
    }
}
