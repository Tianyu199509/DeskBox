namespace DeskBox.Platform;

/// <summary>
/// App-data file primitives for bounded lyrics cache files under
/// <c>%LOCALAPPDATA%\DeskBox\cache\lyrics\</c>.
/// </summary>
internal static class LyricsCacheFileOps
{
    internal static void ReplaceCacheFileAtomically(string tempFilePath, string destinationFilePath)
    {
        if (!destinationFilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to replace a non-JSON lyrics cache path.");
        }

        File.Move(tempFilePath, destinationFilePath, overwrite: true);
    }

    internal static void DeleteCacheFile(string cacheFilePath)
    {
        if (!cacheFilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to delete a non-JSON lyrics cache path.");
        }

        File.Delete(cacheFilePath);
    }
}
