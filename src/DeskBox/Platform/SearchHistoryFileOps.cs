namespace DeskBox.Platform;

/// <summary>
/// App-data file primitives for atomic persistence of the search history JSON store.
/// </summary>
internal static class SearchHistoryFileOps
{
    internal static void ReplaceStoreFileAtomically(string tempFilePath, string destinationFilePath)
    {
        if (!destinationFilePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Refusing to replace a non-JSON search history store path.");
        }

        File.Move(tempFilePath, destinationFilePath, overwrite: true);
    }

    internal static void TryDeleteTempFile(string tempFilePath)
    {
        if (string.IsNullOrWhiteSpace(tempFilePath) ||
            !tempFilePath.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            if (File.Exists(tempFilePath))
            {
                File.Delete(tempFilePath);
            }
        }
        catch
        {
        }
    }
}
