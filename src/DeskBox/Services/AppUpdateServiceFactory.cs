namespace DeskBox.Services;

public static class AppUpdateServiceFactory
{
    public static IAppUpdateService Create(AppDistributionService distribution)
    {
        var effective = distribution ?? AppDistributionService.Current;
        return effective.IsMicrosoftStore || effective.IsPackaged
            ? new StoreAppUpdateService()
            : new AppUpdateService();
    }
}
