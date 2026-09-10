using DeskBox.Models;
using DeskBox.Services.Plugins;

namespace DeskBox.Services;

// Host composition only. Native package code never references this adapter.
// Shared runtime/ABI implementations remain in Services/Plugins.
internal static class MusicPackageIntegration
{
    private static SettingsService? _settings;
    private static ThemeService? _theme;
    private static LocalizationService? _localization;
    private static int _queued;

    internal static void Initialize(SettingsService settings, ThemeService theme, LocalizationService localization)
    {
        if (_settings is not null) return;
        _settings = settings; _theme = theme; _localization = localization;
        PackageBindingRegistry.Register(new(WidgetKind.Music, MusicInstanceMigration.PackageId, "music", MusicInstanceMigration.Instance));
        settings.SettingsChanged += ScheduleSync;
        theme.AppearanceChanged += ScheduleSync;
        localization.LanguageChanged += ScheduleSync;
        MusicSettingsStore.Current.Persisted += ScheduleSync;
        RunDevelopmentInstall();
    }
    internal static void Dispose()
    {
        if (_settings is null) return;
        _settings.SettingsChanged -= ScheduleSync;
        _theme!.AppearanceChanged -= ScheduleSync;
        _localization!.LanguageChanged -= ScheduleSync;
        MusicSettingsStore.Current.Persisted -= ScheduleSync;
        _settings = null; _theme = null; _localization = null;
    }
    private static void ScheduleSync()
    {
        if (_settings is null || Interlocked.Exchange(ref _queued, 1) != 0) return;
        if (App.UiDispatcherQueue?.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _queued, 0);
            if (_settings is null) return;
            try
            {
                string data = DeskBoxDataPathService.Current.DataDirectory;
                var manager = new PluginPackageManager(Path.Combine(data, "plugins"));
                foreach (var record in manager.GetInstalled().Where(p => p.PackageId == MusicInstanceMigration.PackageId))
                foreach (var widget in _settings.Settings.Widgets.Where(w => w.WidgetKind == WidgetKind.Music))
                {
                    // Only sync live package instances. Creation already performs
                    // the initial migration; closed instances need no work.
                    if (PackageInstanceRegistry.TryResolvePackageId(widget.Id) != record.PackageId) continue;
                    NativeWidgetDataMigration.TrySync(MusicInstanceMigration.Instance,
                        record.PublisherFingerprint, record.PackageId, widget.Id, data);
                }
                NativeHostApiBridge.PushConfigChanged();
            }
            catch (Exception error) { App.Log($"[OfficialPackage] settings sync failed: {error.Message}"); }
        }) != true) Interlocked.Exchange(ref _queued, 0);
    }

    [System.Diagnostics.Conditional("DESKBOX_NATIVE_DEV_PILOT")]
    private static void RunDevelopmentInstall()
    {
#if DESKBOX_NATIVE_DEV_PILOT
        string? root = Environment.GetEnvironmentVariable("DESKBOX_DEV_NATIVE_MUSIC");
        if (string.IsNullOrWhiteSpace(root) || !DeskBoxDataPathService.Current.IsDevelopmentRoot) return;
        try
        {
            string full = Path.GetFullPath(root);
            var verified = PluginPackageVerifier.Verify(full, PluginPackageVerificationPolicy.Store);
            if (!verified.IsValid || verified.ManifestJson is null) throw new InvalidOperationException(string.Join("; ", verified.Failures));
            using var json = System.Text.Json.JsonDocument.Parse(verified.ManifestJson);
            if (json.RootElement.GetProperty("id").GetString() != MusicInstanceMigration.PackageId ||
                json.RootElement.GetProperty("publisher").GetString() != "1bc4f2db8438d2fd296bd48074088ccc726c265712125abbae975063ba719ea4")
                throw new InvalidOperationException("Development Music identity mismatch.");
            var manager = new PluginPackageManager(Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "plugins"));
            var result = manager.Install(full, PluginPackageVerificationPolicy.Development);
            App.Log(result.Succeeded ? $"[OfficialPackage] installed {result.Package!.PackageId} through B1: {result.InstallDirectory}"
                : $"[OfficialPackage] install failed: {string.Join("; ", result.Failures)}");
        }
        catch (Exception error) { App.Log($"[OfficialPackage] development install rejected: {error}"); }
#endif
    }
}





