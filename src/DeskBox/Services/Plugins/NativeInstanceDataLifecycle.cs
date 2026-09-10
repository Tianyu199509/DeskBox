using DeskBox.Models;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Logical widget deletion (lifecycle 2 of 3, audit round 21): removes the
/// package's persistent instance data root AFTER the WidgetConfig deletion
/// has committed. Runtime destroy deliberately KEEPS this root — transient
/// teardowns (group switches, content rebuilds) reuse the same instance —
/// so data removal is owned exclusively by this explicit deletion.
///
/// The instance directory name is derived deterministically from the
/// instance id (SHA-256 storage key), and the publisher segment is resolved
/// by scanning, because the same instance id may have been served by
/// different publishers (dev pilot → official) across upgrades.
///
/// Idempotent and best-effort: a missing root is a no-op, and a locked
/// directory (in-flight sync, antivirus) is logged, never thrown — widget
/// deletion must not fail because of native data cleanup.
/// </summary>
internal static class NativeInstanceDataLifecycle
{
    public static Task DeleteAsync(string packageId, string instanceId, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        return Task.Run(() => DeleteCore(packageId, instanceId, dataDirectory));
    }

    private static void DeleteCore(string packageId, string instanceId, string dataDirectory)
    {
        try
        {
            string packagesRoot = Path.Combine(dataDirectory, "packages");
            if (!Directory.Exists(packagesRoot)) return;
            string instanceDirName = NativePackageIdentity.InstanceStorageKey(instanceId);
            foreach (string publisherDir in Directory.EnumerateDirectories(packagesRoot))
            {
                string candidate = Path.Combine(publisherDir, packageId, "instances", instanceDirName);
                if (!Directory.Exists(candidate)) continue;
                DeleteWithRetry(candidate);
                App.Log($"[NativePackage] deleted instance data root for {packageId}/{instanceId}");
            }
        }
        catch (Exception error)
        {
            // Best-effort: an orphaned root is inert; widget deletion must
            // not fail because of native data cleanup.
            App.Log($"[NativePackage] instance data cleanup failed for {instanceId}: {error.Message}");
        }
    }

    private static void DeleteWithRetry(string path)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException && attempt < 2)
            {
                Thread.Sleep(150);
            }
        }
    }
}
