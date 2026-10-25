namespace DeskBox.Models;

/// <summary>
/// Automatic data snapshot scheduling and retention.
/// </summary>
public sealed class BackupSettingsSlice
{
    /// <summary>
    /// Whether DeskBox creates automatic data snapshots on a schedule.
    /// </summary>
    public bool AutomaticBackupEnabled { get; set; } = true;

    /// <summary>
    /// Minutes between automatic snapshots; always one of the preset values in
    /// <see cref="Services.DataBackupSettingsPolicy.SupportedIntervalMinutes"/>.
    /// </summary>
    public int AutomaticBackupIntervalMinutes { get; set; } = 24 * 60;

    /// <summary>
    /// How many automatic snapshots to keep; always one of the preset values in
    /// <see cref="Services.DataBackupSettingsPolicy.SupportedRetentionCounts"/>.
    /// </summary>
    public int AutomaticBackupRetentionCount { get; set; } = 7;

    /// <summary>
    /// Custom directory for automatic snapshots. Empty means the default
    /// recovery directory outside the app-data root.
    /// </summary>
    public string AutomaticBackupDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Ownership transfer used when applying an external (settings-host)
    /// snapshot: this domain is written only by main-side actors (the
    /// backup-settings coordinator on router commands, restore path
    /// rebasing), so the host's copy is always stale-or-equal and the
    /// main-side state must survive the apply (deep review R13).
    /// </summary>
    internal void CopyFrom(BackupSettingsSlice other)
    {
        ArgumentNullException.ThrowIfNull(other);

        AutomaticBackupEnabled = other.AutomaticBackupEnabled;
        AutomaticBackupIntervalMinutes = other.AutomaticBackupIntervalMinutes;
        AutomaticBackupRetentionCount = other.AutomaticBackupRetentionCount;
        AutomaticBackupDirectory = other.AutomaticBackupDirectory;
    }
}
