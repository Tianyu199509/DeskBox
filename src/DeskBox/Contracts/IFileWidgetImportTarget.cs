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
    /// the target is invalid, disabled, or the folder is not accessible.
    /// Implementations honor cancellation for large transfers (streaming
    /// copy, not a start-only token check).
    /// </summary>
    Task<string?> TryImportFileAsync(
        string sourceFilePath,
        string targetWidgetId,
        string? preferredFileName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports inline text content as a file into the target file widget's
    /// backing folder. Returns the destination path or null on failure.
    /// </summary>
    Task<string?> TryImportTextAsync(
        string text,
        string fileName,
        string targetWidgetId,
        CancellationToken cancellationToken = default);
}

/// <summary>An importable file widget: id, display name, backing folder.</summary>
public sealed record FileWidgetImportTarget(
    string WidgetId,
    string Name,
    string FolderPath);
