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

    public static void DeleteOwnedMirrorAssetsForShortId(string mirrorRootDirectory, string shortId)
    {
        if (string.IsNullOrWhiteSpace(mirrorRootDirectory) || string.IsNullOrWhiteSpace(shortId))
        {
            return;
        }

        foreach (char ch in shortId)
        {
            if (!char.IsAsciiLetterOrDigit(ch))
            {
                return;
            }
        }

        try
        {
            string assetsDir = Path.Combine(Path.GetFullPath(mirrorRootDirectory), "assets");
            if (!Directory.Exists(assetsDir))
            {
                return;
            }

            foreach (string assetFile in Directory.EnumerateFiles(assetsDir, $"{shortId}_*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(assetFile);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    public static void TryCleanupTempPasteFiles(string tempRootDirectory, IEnumerable<string> paths)
    {
        string tempRoot;
        try
        {
            tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tempRootDirectory))
                + Path.DirectorySeparatorChar;
        }
        catch
        {
            return;
        }

        foreach (string rawPath in paths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            try
            {
                string fullPath = Path.GetFullPath(rawPath);
                if (fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }
            catch
            {
            }
        }
    }

    public static void TryPruneStaleTempPasteFiles(string tempDir, DateTime cutoffUtc)
    {
        try
        {
            foreach (string file in Directory.EnumerateFiles(tempDir, "Screenshot_*.png", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoffUtc)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }
}
