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
}
