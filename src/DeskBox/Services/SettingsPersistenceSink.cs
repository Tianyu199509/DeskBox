namespace DeskBox.Services;

using DeskBox.Models;

/// <summary>
/// One settings.json persistence pass handed to the sink, identified by a
/// process-local commit id. The id is the cross-process echo-suppression
/// token: the main process returns it with its reload push so the settings
/// host can recognize the round-trip of its own write.
/// </summary>
public sealed record SettingsPersistenceCommit(
    long CommitId,
    AppSettings Snapshot,
    bool WidgetLayoutMerged);

/// <summary>
/// Seam for settings.json persistence (settings-process split, batch 3a).
/// The default implementation commits through the local resilient store pair
/// exactly as before; a settings host running in its own process installs a
/// forwarding sink that ships the snapshot to the main process instead, so
/// every existing SaveDebounced/SaveAsync call site works unchanged in both
/// processes.
/// </summary>
/// <remarks>
/// Forwarding sinks must apply cross-process: the caller holds the
/// per-directory commit gate while <see cref="SaveAsync"/> runs, so a sink
/// that synchronously re-enters a same-process SettingsService apply path
/// will deadlock on that gate.
/// </remarks>
public interface ISettingsPersistenceSink
{
    /// <summary>
    /// True when the snapshot is durably committed (or accepted by the main
    /// process); false must mean nothing persisted.
    /// </summary>
    Task<bool> SaveAsync(SettingsService owner, SettingsPersistenceCommit commit);
}
