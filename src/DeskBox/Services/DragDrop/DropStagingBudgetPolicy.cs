namespace DeskBox.Services.DragDrop;

/// <summary>
/// Computes free-space-aware materialization and staging byte budgets for
/// inbound virtual streams (<c>FileContents</c>) and transient archive/cache
/// drops (<c>%TEMP%</c>, <c>INetCache</c>). Replaces hardcoded 256 MB / 512 MB
/// ceilings with volume-aware bounds while reserving safety headroom so a single
/// drop can never starve the host drive.
/// </summary>
internal static class DropStagingBudgetPolicy
{
    internal const long SafetyReserveBytes = 512L * 1024 * 1024;
    internal const long CriticalReserveBytes = 64L * 1024 * 1024;
    internal const long FallbackUnknownDriveBudgetBytes = 2L * 1024 * 1024 * 1024;

    internal static long ResolveDynamicByteBudget(
        string stagingDirectory,
        long? availableFreeSpaceOverride = null,
        long? explicitLimitBytes = null)
    {
        if (explicitLimitBytes is > 0)
        {
            return explicitLimitBytes.Value;
        }

        long? availableFree = availableFreeSpaceOverride ?? TryQueryAvailableFreeSpace(stagingDirectory);
        if (availableFree is null)
        {
            return FallbackUnknownDriveBudgetBytes;
        }

        long freeBytes = availableFree.Value;
        if (freeBytes <= CriticalReserveBytes)
        {
            return 0;
        }

        if (freeBytes <= SafetyReserveBytes)
        {
            return freeBytes - CriticalReserveBytes;
        }

        return freeBytes - SafetyReserveBytes;
    }

    private static long? TryQueryAvailableFreeSpace(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            string fullPath = Path.GetFullPath(directory);
            string? root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrWhiteSpace(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return null;
            }

            var driveInfo = new DriveInfo(root);
            return driveInfo.IsReady ? driveInfo.AvailableFreeSpace : null;
        }
        catch
        {
            return null;
        }
    }
}
