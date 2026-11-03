namespace DeskBox.Platform;

/// <summary>
/// Confines staged file-system commits and temp cleanup for user-initiated
/// Todo spreadsheet exports (<c>.xlsx</c> / <c>.csv</c>) inside <c>DeskBox.Platform</c>.
/// </summary>
internal static class TodoExportFileOps
{
    public static string CreateStagedExportPath(string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        string fullTarget = Path.GetFullPath(targetPath);
        string? directory = Path.GetDirectoryName(fullTarget);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return Path.Combine(
            directory ?? Path.GetTempPath(),
            $".deskbox-todo-export-{Guid.NewGuid():N}.tmp");
    }

    public static void CommitStagedExport(string stagedPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        string fullTarget = Path.GetFullPath(targetPath);
        File.Move(stagedPath, fullTarget, overwrite: true);
    }

    public static void TryDeleteStagedExport(string? stagedPath)
    {
        if (string.IsNullOrWhiteSpace(stagedPath))
        {
            return;
        }

        try
        {
            if (File.Exists(stagedPath))
            {
                File.Delete(stagedPath);
            }
        }
        catch
        {
            // Ignore transient cleanup failures on temporary staging files.
        }
    }
}
