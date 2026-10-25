namespace DeskBox.Models;

/// <summary>
/// User-configured cloud backup (roadmap §10). Holds only non-secret
/// provider configuration — the password/app-token lives in the OS
/// credential store via <see cref="Services.ICredentialStore"/> and is
/// never serialized here.
/// </summary>
public sealed class CloudBackupSettingsSlice
{
    /// <summary>Configured provider id: "none" or "webdav" in v1.</summary>
    public string CloudBackupProvider { get; set; } = "none";

    /// <summary>WebDAV endpoint base URI (e.g. the server root or a dav subpath).</summary>
    public string CloudBackupServerUrl { get; set; } = string.Empty;

    /// <summary>Remote directory for snapshots, relative to the server base.</summary>
    public string CloudBackupRemotePath { get; set; } = "DeskBox/backups";

    /// <summary>Account name; pairs with the vault-stored secret. Non-secret itself.</summary>
    public string CloudBackupUsername { get; set; } = string.Empty;

    /// <summary>Independent toggle: todo data + its attachments.</summary>
    public bool CloudBackupTodoDataEnabled { get; set; }

    /// <summary>Independent toggle: quick-capture data + its attachments.</summary>
    public bool CloudBackupQuickCaptureDataEnabled { get; set; }

    /// <summary>Independent toggle: widget style projection (never layout/positions).</summary>
    public bool CloudBackupWidgetStyleEnabled { get; set; }

    /// <summary>How many remote snapshots to keep.</summary>
    public int CloudBackupRetentionCount { get; set; } = 5;

    /// <summary>Minutes between scheduled uploads.</summary>
    public int CloudBackupIntervalMinutes { get; set; } = 24 * 60;

    /// <summary>UTC ticks of the last successful upload; 0 = never.</summary>
    public long CloudBackupLastSuccessUtcTicks { get; set; }

    /// <summary>
    /// UTC ticks of the last failed upload attempt; 0 = none recorded.
    /// Cleared on the next success and on destination change so a stale
    /// failure never haunts an endpoint that hasn't run yet.
    /// </summary>
    public long CloudBackupLastFailureUtcTicks { get; set; }

    /// <summary>
    /// UTC ticks of the last upload the server accepted but whose directory
    /// listing never confirmed; 0 = last upload verified or none yet.
    /// Cleared on the next verified upload and on destination change —
    /// the flag describes the newest run against the current endpoint.
    /// </summary>
    public long CloudBackupLastUnverifiedUtcTicks { get; set; }

    /// <summary>
    /// Ownership transfer used when applying an external (settings-host)
    /// snapshot: this domain is written only by main-side actors (the
    /// backup-settings coordinator on router commands, the cloud-backup
    /// runtime's success/failure bookkeeping), so the host's copy is
    /// always stale-or-equal and the main-side state must survive the
    /// apply (deep review R13).
    /// </summary>
    internal void CopyFrom(CloudBackupSettingsSlice other)
    {
        ArgumentNullException.ThrowIfNull(other);

        CloudBackupProvider = other.CloudBackupProvider;
        CloudBackupServerUrl = other.CloudBackupServerUrl;
        CloudBackupRemotePath = other.CloudBackupRemotePath;
        CloudBackupUsername = other.CloudBackupUsername;
        CloudBackupTodoDataEnabled = other.CloudBackupTodoDataEnabled;
        CloudBackupQuickCaptureDataEnabled = other.CloudBackupQuickCaptureDataEnabled;
        CloudBackupWidgetStyleEnabled = other.CloudBackupWidgetStyleEnabled;
        CloudBackupRetentionCount = other.CloudBackupRetentionCount;
        CloudBackupIntervalMinutes = other.CloudBackupIntervalMinutes;
        CloudBackupLastSuccessUtcTicks = other.CloudBackupLastSuccessUtcTicks;
        CloudBackupLastFailureUtcTicks = other.CloudBackupLastFailureUtcTicks;
        CloudBackupLastUnverifiedUtcTicks = other.CloudBackupLastUnverifiedUtcTicks;
    }
}
