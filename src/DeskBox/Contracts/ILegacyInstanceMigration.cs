namespace DeskBox.Contracts;

/// <summary>
/// Host-internal port for an official package's legacy data handoff
/// (pluginization official-widget-packages plan, audit round 20 §18): the
/// generic plugin layer (pilot, data sync, HostApi write-through bridge)
/// routes through this interface so it never knows feature specifics —
/// each feature implements it and registers an OfficialPackageBinding.
///
/// Host-internal port, NOT the future public extension capability API:
/// official packages are first-party trusted code; third-party packages
/// will need a capability-scoped, validated surface on top.
///
/// Implementations live beside the feature's authoritative store (e.g.
/// GlanceInstanceMigration beside GlanceWidgetStore) and own their legacy
/// recovery chain end to end.
/// </summary>
public interface ILegacyInstanceMigration
{
    /// <summary>
    /// The file name (inside the package's instance data root) the synced
    /// legacy bytes are written to. Feature-owned because the package's
    /// reader defines it.
    /// </summary>
    string DataFileName { get; }

    /// <summary>
    /// Prepares a small package-wide legacy data handoff at native creation.
    /// Runs on the caller's thread: record source metadata only; the package
    /// must perform any bulk copying asynchronously before reading its cache.
    /// </summary>
    void PreparePackageData(string dataDirectory, string packageDataRoot) { }

    /// <summary>
    /// Resolves the feature's legacy persisted bytes for this instance
    /// (primary, backups, single-instance legacy stores — feature-owned
    /// recovery chain). Returns null when the host has no data; the
    /// package keeps whatever it already owns. Implementations must
    /// validate content semantically before returning it.
    /// </summary>
    string? ResolveLegacyContent(string dataDirectory, string instanceId);

    /// <summary>
    /// Commits a native settings patch into the feature's authoritative
    /// store. Returns false when the patch is malformed or the instance is
    /// not owned by this feature — the caller then treats the write as
    /// rejected.
    /// </summary>
    bool TryApplyPatch(string instanceId, string jsonPatch);

    /// <summary>
    /// Observes successfully persisted changes to one instance's authority.
    /// The caller dispatches notifications to the widget UI thread and
    /// disposes the subscription when the native instance is destroyed.
    /// Adapters without live synchronization may return null.
    /// </summary>
    IDisposable? SubscribeChanges(string instanceId, Action changed) => null;
}
