namespace DeskBox.Contracts;

/// <summary>
/// Capability surface for saving a captured item (text, image, file) into a
/// file widget's managed folder (pluginization roadmap stage 3, cut point 2).
/// Extracted from WidgetManager.FeatureWidgets.cs where the QuickCapture
/// feature reached across to the File widget's folder logic directly.
/// The host implementation resolves the target widget's folder and performs
/// the file write; the QuickCapture feature sees only this contract.
/// </summary>
public interface IFileDropTarget
{
    /// <summary>
    /// Saves a file (already materialized on disk as a temp file) into the
    /// target file widget's managed folder. Returns the destination path,
    /// or null when the target is invalid, disabled, or the folder is not
    /// accessible.
    /// </summary>
    Task<string?> TrySaveFileToWidgetAsync(
        string sourceFilePath,
        string targetWidgetId,
        string? preferredFileName = null);

    /// <summary>
    /// Saves inline text content as a file into the target file widget's
    /// managed folder. Returns the destination path or null on failure.
    /// </summary>
    Task<string?> TrySaveTextToWidgetAsync(
        string text,
        string fileName,
        string targetWidgetId);
}
