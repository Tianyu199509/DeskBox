namespace DeskBox.Contracts;

/// <summary>
/// Host-internal capability port for importing content into a file widget
/// (pluginization roadmap stage 3, cut point 2). Extracted from
/// WidgetManager.FeatureWidgets.cs where the QuickCapture feature reached
/// across directly to the File widget's folder logic.
///
/// Host-internal port, NOT the future public extension capability API: this
/// seam exists so producers depend on an interface instead of the File
/// feature's internals; extension-facing capability contracts will be
/// separate data-oriented types (roadmap section 5, Extension Model hard
/// constraints).
///
/// This is not drag-and-drop: it is the import/sink surface that any
/// producer (QuickCapture today; plugins, AI tooling, clipboard flows,
/// automation later) calls. The destination is a file widget's writable
/// backing folder - either the user-mapped folder or the host-managed
/// storage folder; "managed folder" alone would under-describe it.
/// </summary>
public interface IFileWidgetImportTarget
{
    /// <summary>
    /// Lists the file widgets that are valid import targets right now
    /// (enabled, not deleted, with a resolvable writable backing folder).
    /// The record deliberately carries no raw folder path: a producer that
    /// learned the backing folder could bypass this port and write there
    /// directly. UI that needs to show a location gets display text, not a
    /// usable path capability.
    /// </summary>
    IReadOnlyList<FileWidgetImportTarget> GetImportTargets();

    /// <summary>
    /// The most recently used import target, or null when none was used yet
    /// or the stored target is no longer valid.
    /// </summary>
    FileWidgetImportTarget? GetLastImportTarget();

    /// <summary>
    /// Imports a file already materialized on disk into the target file
    /// widget's backing folder. Returns the destination path, or null when
    /// the target widget is invalid or the input is unusable - which
    /// includes file names with path structure (separators, rooted
    /// segments, ..-traversal), which are rejected rather than
    /// reinterpreted. I/O failures (access denied, disk full) and
    /// cancellation propagate as exceptions. The sink confines every write
    /// inside the widget folder, writes through a temp file, and leaves no
    /// partial destination on failure. Cancellation is honored mid-transfer
    /// (streaming copy, not a start-only token check).
    /// </summary>
    Task<string?> TryImportFileAsync(
        string sourceFilePath,
        string targetWidgetId,
        string? preferredFileName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports inline text content as a file into the target file widget's
    /// backing folder. Returns the destination path, or null when the
    /// target widget is invalid or the input is unusable; I/O failures and
    /// cancellation propagate as exceptions. Same sink discipline as
    /// <see cref="TryImportFileAsync"/>: the file name is confined to the
    /// widget folder and the write is atomic.
    /// </summary>
    Task<string?> TryImportTextAsync(
        string text,
        string fileName,
        string targetWidgetId,
        CancellationToken cancellationToken = default);
}

/// <summary>An importable file widget: id and display name (no raw paths).</summary>
public sealed record FileWidgetImportTarget(
    string WidgetId,
    string Name);
