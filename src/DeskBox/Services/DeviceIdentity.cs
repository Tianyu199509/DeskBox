namespace DeskBox.Services;

/// <summary>
/// Stable per-device identifier used by sync-layer fields (device_id) on
/// local records. The ID is generated once and persisted in
/// &lt;dataDir&gt;/device.id so it survives restarts and upgrades.
/// </summary>
public static class DeviceIdentity
{
    private static readonly object s_gate = new();
    private static string? s_cached;
    private static string? s_dataRootOverride;

    /// <summary>
    /// Test seam: redirects <see cref="Id"/> to a throwaway root so test
    /// processes never read or write device.id under the real application
    /// data directory. Setting it clears the cached value.
    /// </summary>
    internal static string? DataRootOverride
    {
        set
        {
            lock (s_gate)
            {
                s_dataRootOverride = value;
                s_cached = null;
            }
        }
    }

    /// <summary>This device's ID (generated on first use).</summary>
    public static string Id
    {
        get
        {
            // First-read generation must be single-winner: two concurrent
            // callers racing GetOrCreate would mint different GUIDs, split
            // device_id across records written in the same process, and race
            // the device.id file write itself.
            lock (s_gate)
            {
                s_cached ??= GetOrCreate(
                    s_dataRootOverride ?? DeskBoxDataPathService.Current.DataDirectory);
                return s_cached;
            }
        }
    }

    /// <summary>Read or create the device.id marker under the given data root.</summary>
    internal static string GetOrCreate(string dataDir)
    {
        string path = Path.Combine(dataDir, "device.id");
        try
        {
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path).Trim();
                if (existing.Length >= 8)
                {
                    return existing;
                }
            }
        }
        catch
        {
            // fall through and try to write a fresh one
        }

        string id = Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(dataDir);
            File.WriteAllText(path, id);
        }
        catch
        {
            // A read-only data dir should not break startup: keep going with
            // the generated value for this process lifetime.
        }
        return id;
    }
}
