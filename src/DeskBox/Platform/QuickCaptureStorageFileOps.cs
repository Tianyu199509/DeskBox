namespace DeskBox.Platform;

/// <summary>
/// Confines file-system mutations for Quick Capture managed attachment buckets
/// and DeskBox-owned Markdown mirror files (<c>*__qc_&lt;shortId&gt;.md</c> and
/// <c>assets/&lt;shortId&gt;_*</c>) inside <c>DeskBox.Platform</c>.
/// </summary>
internal static class QuickCaptureStorageFileOps
{
    private const string MirrorFileTagMarker = "__qc_";

    public static void CopyManagedFile(string sourcePath, string destinationPath, bool overwrite)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(sourcePath, destinationPath, overwrite);
    }

    public static bool DeleteOwnedMirrorFile(string mirrorRootDirectory, string filePath)
    {
        if (string.IsNullOrWhiteSpace(mirrorRootDirectory) || string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        string fullRoot = Path.GetFullPath(mirrorRootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string fullFile = Path.GetFullPath(filePath);
        if (!fullFile.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string fileName = Path.GetFileName(fullFile);
        if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ||
            !fileName.Contains(MirrorFileTagMarker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!File.Exists(fullFile))
        {
            return false;
        }

        File.Delete(fullFile);
        return true;
    }
}
