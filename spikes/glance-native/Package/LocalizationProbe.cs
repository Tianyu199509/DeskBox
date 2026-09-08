using System.Text.Json;

namespace DeskBox.Glance.NativePackage;

/// <summary>
/// PRI/localization probe. WinAppSDK MrtCore exposes no file-based PRI loading
/// (main app map only); the legacy UWP Windows.ApplicationModel.Resources.Core
/// ResourceManager can load external PRI files but may require package
/// identity. Both paths are attempted and their outcomes recorded - the spike
/// value is the recorded evidence either way.
/// </summary>
internal static class LocalizationProbe
{
    public static async Task RunAsync(string root)
    {
        var outcomes = new List<(string Method, string Result)>();

        string[] priFiles = Directory.Exists(root)
            ? Directory.GetFiles(root, "*.pri", SearchOption.TopDirectoryOnly)
            : [];
        outcomes.Add(("priFilesNextToDll", priFiles.Length == 0
            ? "none"
            : string.Join(",", priFiles.Select(Path.GetFileName))));

        try
        {
            var manager = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager();
            string? value = manager.MainResourceMap?.GetValue("GlanceTitle")?.ValueAsString;
            outcomes.Add(("mrtCoreMainMap", string.IsNullOrEmpty(value) ? "key not found in host main map" : value));
        }
        catch (Exception error)
        {
            outcomes.Add(("mrtCoreMainMap", "failed: " + error.GetType().Name + ": " + error.Message));
        }

        try
        {
            var named = new Microsoft.Windows.ApplicationModel.Resources.ResourceManager("DeskBox.Glance.NativePackage/Resources");
            string? value = named.MainResourceMap?.GetValue("GlanceTitle")?.ValueAsString;
            outcomes.Add(("mrtCoreNamedMap", string.IsNullOrEmpty(value) ? "key not found" : value));
        }
        catch (Exception error)
        {
            outcomes.Add(("mrtCoreNamedMap", "failed: " + error.GetType().Name + ": " + error.Message));
        }

        if (priFiles.Length > 0)
        {
            try
            {
                Windows.Storage.StorageFolder folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(root);
                var files = new List<Windows.Storage.StorageFile>();
                foreach (string pri in priFiles)
                {
                    files.Add(await folder.GetFileAsync(Path.GetFileName(pri)));
                }
                Windows.ApplicationModel.Resources.Core.ResourceManager.Current.LoadPriFiles(files);
                var candidate = Windows.ApplicationModel.Resources.Core.ResourceManager.Current.MainResourceMap
                    .GetValue("DeskBox.Glance.NativePackage/Resources/GlanceTitle");
                string? loaded = candidate?.ValueAsString;
                outcomes.Add(("uwpLoadPriFiles", string.IsNullOrEmpty(loaded) ? "loaded, key not found" : loaded));
            }
            catch (Exception error)
            {
                outcomes.Add(("uwpLoadPriFiles", "failed: " + error.GetType().Name + ": " + error.Message));
            }
        }
        else
        {
            outcomes.Add(("uwpLoadPriFiles", "skipped: no pri file"));
        }

        using var stream = File.Create(Path.Combine(root, "localization-probe.json"));
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        foreach ((string method, string result) in outcomes)
        {
            writer.WriteString(method, result);
        }
        writer.WriteEndObject();
    }
}
