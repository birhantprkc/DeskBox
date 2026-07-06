namespace DeskBox.Services;

public static class StartupServiceFactory
{
    public static IStartupService Create(AppDistributionService distribution)
    {
        return distribution.IsMicrosoftStore
            ? new StoreStartupService()
            : new DirectStartupService();
    }
}
