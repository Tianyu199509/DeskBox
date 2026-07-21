using Microsoft.Extensions.DependencyInjection;

namespace DeskBox.Services;

/// <summary>
/// Central DI registration for all core DeskBox services.
/// All services use Singleton lifetime (desktop app = single process).
/// </summary>
public static class ServiceRegistry
{
    /// <summary>
    /// Registers all core application services into the given service collection.
    /// </summary>
    public static IServiceCollection AddDeskBoxServices(this IServiceCollection services)
    {
        // ── Core infrastructure ──────────────────────────────────────────
        services.AddSingleton<SettingsService>();
        services.AddSingleton<SettingsMigrationPipeline>();
        services.AddSingleton<DeskBoxDataBackupService>();
        services.AddSingleton<DeskBoxAttachmentHealthService>();
        services.AddSingleton<FileService>();
        services.AddSingleton<ResizeGuideOverlayService>();

        // ── Feature services ─────────────────────────────────────────────
        services.AddSingleton<OrganizerService>(sp =>
            new OrganizerService(
                sp.GetRequiredService<SettingsService>(),
                sp.GetRequiredService<FileService>()));
        services.AddSingleton<QuickCaptureService>(_ => new QuickCaptureService());
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<ThemeService>();

        // ── Weather ──────────────────────────────────────────────────────
        services.AddSingleton<WeatherService>();
        services.AddSingleton<CitySearchService>();

        // ── Update (factory-based) ───────────────────────────────────────
        services.AddSingleton<IAppUpdateService>(_ =>
            AppUpdateServiceFactory.Create(AppDistributionService.Current));

        return services;
    }
}
