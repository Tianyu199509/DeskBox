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
}
