namespace DeskBox.Services;

public static class AppUpdateServiceFactory
{
    public static IAppUpdateService Create(AppDistributionService distribution)
    {
#if DESKBOX_STORE
        _ = distribution;
        return new StoreAppUpdateService();
#else
        var effective = distribution ?? AppDistributionService.Current;
        return effective.IsMicrosoftStore || effective.IsPackaged
            ? new StoreAppUpdateService()
            : new AppUpdateService();
#endif
    }
}
