namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    public void RefreshDesktopOrganizationState()
    {
        AppearanceDetailSection?.OrganizationSection.Refresh();
        ViewModel.RefreshManagedStorageState();
        _ = RefreshOrphanFoldersSummaryAsync();
    }
}
