namespace DeskBox.Services.Plugins;

/// <summary>
/// One-shot legacy data migration (D3 product migration): copies the
/// built-in Glance widget store file verbatim into the native package's
/// instance data root before the first native create. From then on the
/// package owns its data; the built-in store keeps serving the built-in
/// widget until the oracle comparison deletes it.
///
/// The copy is byte-for-byte on purpose: no JsonSerializer runs here (the
/// host's frozen JSON call baseline stays untouched), and the package-side
/// reader defaults every field its file version does not carry (files as old
/// as v7 exist on real machines).
/// </summary>
internal static class NativeWidgetDataMigration
{
    internal const string DataFileName = "glance-data.json";

    internal static void TryMigrate(string publisherFingerprint, string packageId, string instanceId, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        try
        {
            string instanceRoot = new NativePackageIdentity(publisherFingerprint, packageId)
                .ResolveInstanceDataRoot(dataDirectory, instanceId);
            string target = Path.Combine(instanceRoot, DataFileName);
            if (File.Exists(target)) return; // idempotent: package data wins once migrated
            string source = Path.Combine(
                dataDirectory, "glance", "widgets",
                $"{GlanceWidgetStore.GetSafeWidgetFileName(instanceId)}.json");
            if (!File.Exists(source)) return; // fresh instance: package defaults apply
            Directory.CreateDirectory(instanceRoot);
            File.Copy(source, target);
            App.Log($"[NativePackage] migrated legacy glance data for instance {instanceId}");
        }
        catch (Exception error)
        {
            // Migration is best-effort: a failed copy must never block native
            // widget creation, it just means the instance starts from defaults.
            App.LogVerbose($"[NativePackage] glance data migration failed for {instanceId}: {error.Message}");
        }
    }
}
