using DeskBox.Contracts;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Legacy data handoff (D3 data-ownership, audits 18-20). Generic host-side
/// machinery: the FEATURE adapter (ILegacyInstanceMigration) resolves and
/// validates the legacy bytes; this class only performs the resilient
/// byte-for-byte sync into the package's instance data root.
///
/// Ownership model until the formal cutover marker exists: the built-in
/// store stays the SINGLE source of truth. Every native create re-syncs,
/// so neither side can strand the other with stale data (the native side
/// commits its own mutations back through the HostApi write-through
/// channel, routed by PackageInstanceRegistry).
///
/// The write uses the same replace-with-fallback algorithm as
/// ResilientJsonStore (GUID temp, 1175 retry ladder, verified in-place
/// fallback), ported synchronous: this runs on the UI thread and must not
/// block on async continuations.
/// </summary>
internal static class NativeWidgetDataMigration
{
    internal static bool TrySync(
        ILegacyInstanceMigration? migration,
        string publisherFingerprint,
        string packageId,
        string instanceId,
        string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        if (migration is null) return false;
        try
        {
            string? legacy = migration.ResolveLegacyContent(dataDirectory, instanceId);
            if (legacy is null)
            {
                // No host-side data for this instance: whatever the package
                // already owns stays untouched.
                return false;
            }
            string instanceRoot = new NativePackageIdentity(publisherFingerprint, packageId)
                .ResolveInstanceDataRoot(dataDirectory, instanceId);
            Directory.CreateDirectory(instanceRoot);
            WriteResilient(Path.Combine(instanceRoot, migration.DataFileName), legacy);
            App.Log($"[NativePackage] synced legacy data for instance {instanceId}");
            return true;
        }
        catch (Exception error)
        {
            // Best-effort: a failed sync must never block native widget
            // creation, it just means the package keeps its current data.
            App.LogVerbose($"[NativePackage] data sync failed for {instanceId}: {error.Message}");
            return false;
        }
    }

    private static void WriteResilient(string path, string content)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temp, content);
            if (File.Exists(path))
            {
                ReplaceOrFallback(temp, path, path + ".bak");
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private static void ReplaceOrFallback(string temp, string path, string backup)
    {
        for (int retry = 0; ; retry++)
        {
            try
            {
                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException error) when (error.HResult == UnableToRemoveReplacedFileHResult)
            {
                if (retry < ReplaceRetryDelayMs.Length)
                {
                    Thread.Sleep(ReplaceRetryDelayMs[retry]);
                    continue;
                }
                if (!File.Exists(temp) || !File.Exists(path))
                {
                    throw;
                }
                byte[] updated = File.ReadAllBytes(temp);
                byte[] original = File.ReadAllBytes(path);
                WriteInPlaceAndVerify(backup, original, "backup");
                WriteInPlaceAndVerify(path, updated, "primary file");
                return;
            }
        }
    }

    // Win32 ERROR_UNABLE_TO_REMOVE_REPLACED (1175), surfaced by File.Replace.
    private const int UnableToRemoveReplacedFileHResult = unchecked((int)0x80070497);
    private static readonly int[] ReplaceRetryDelayMs = [50, 150];

    private static void WriteInPlaceAndVerify(string path, byte[] contents, string description)
    {
        using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(contents, 0, contents.Length);
        stream.Flush(flushToDisk: true);
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(contents))
        {
            throw new IOException($"The {description} could not be verified after the in-place save.");
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}
