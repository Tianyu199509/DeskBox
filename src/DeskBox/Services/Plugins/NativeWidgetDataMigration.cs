using System.Text.Json;

namespace DeskBox.Services.Plugins;

/// <summary>
/// Legacy data handoff (D3 data-ownership hardening, audit round 18).
///
/// Ownership model until the formal cutover marker exists: the built-in
/// store stays the SINGLE source of truth for glance settings. Every native
/// create re-syncs the resolved legacy bytes into the package's instance
/// data root, so a failed native create can never strand a stale snapshot,
/// and host-side setting changes always reach the next native session.
///
/// The resolved bytes come from the same candidates the built-in system
/// recovers from: the per-widget store, its .bak, then the single-instance
/// legacy store (and its .bak). Content is validated as a JSON object before
/// copying, byte-for-byte - no re-serialization runs here, and the
/// package-side reader defaults any field the file version does not carry.
/// </summary>
internal static class NativeWidgetDataMigration
{
    internal const string DataFileName = "glance-data.json";

    internal static void TryMigrate(string publisherFingerprint, string packageId, string instanceId, string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        try
        {
            string? legacy = ResolveLegacyContent(dataDirectory, instanceId);
            if (legacy is null)
            {
                // No host-side data for this instance: whatever the package
                // already owns stays untouched.
                return;
            }
            string instanceRoot = new NativePackageIdentity(publisherFingerprint, packageId)
                .ResolveInstanceDataRoot(dataDirectory, instanceId);
            Directory.CreateDirectory(instanceRoot);
            string target = Path.Combine(instanceRoot, DataFileName);
            string temp = target + ".tmp";
            File.WriteAllText(temp, legacy);
            if (File.Exists(target))
            {
                File.Replace(temp, target, target + ".bak");
            }
            else
            {
                File.Move(temp, target);
            }
            App.Log($"[NativePackage] synced legacy glance data for instance {instanceId}");
        }
        catch (Exception error)
        {
            // Best-effort: a failed sync must never block native widget
            // creation, it just means the package keeps its current data.
            App.LogVerbose($"[NativePackage] glance data sync failed for {instanceId}: {error.Message}");
        }
    }

    private static string? ResolveLegacyContent(string dataDirectory, string instanceId)
    {
        string widgetFile = Path.Combine(
            dataDirectory, "glance", "widgets",
            $"{GlanceWidgetStore.GetSafeWidgetFileName(instanceId)}.json");
        string legacyFile = Path.Combine(dataDirectory, "glance", "glance.json");
        foreach (string candidate in new[] { widgetFile, widgetFile + ".bak", legacyFile, legacyFile + ".bak" })
        {
            if (TryReadValidObject(candidate, out string? content))
            {
                return content;
            }
        }
        return null;
    }

    private static bool TryReadValidObject(string path, out string? content)
    {
        content = null;
        try
        {
            if (!File.Exists(path)) return false;
            string text = File.ReadAllText(path);
            using JsonDocument document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            content = text;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
