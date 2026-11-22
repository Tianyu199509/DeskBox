namespace DeskBox.Services;

public static class StartupServiceFactory
{
    public static IStartupService Create(AppDistributionService distribution, SettingsService? settingsService = null)
    {
#if DESKBOX_STORE
        _ = distribution;
        _ = settingsService;
        return new StoreStartupService();
#else
        var effective = distribution ?? AppDistributionService.Current;
        return effective.IsMicrosoftStore || effective.IsPackaged
            ? new StoreStartupService()
            : new DirectStartupService(settingsService);
#endif
    }
}
