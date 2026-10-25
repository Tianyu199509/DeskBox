namespace DeskBox.Views;

/// <summary>
/// Save-failure notice surface (deep review R13, R12 debt ①): the settings
/// host wires <see cref="ShowSaveFailedBanner"/> to
/// SettingsService.PersistenceFailed and <see cref="DismissSaveFailedBanner"/>
/// to the next successful LocalPersistCompleted. Text resolves at show time
/// so a language switch mid-session keeps the banner readable; the in-process
/// smoke-harness settings window never wires these, so the banner stays
/// hidden there by construction.
/// </summary>
public sealed partial class SettingsWindow
{
    internal void ShowSaveFailedBanner()
    {
        SaveFailedInfoBar.Title = Services.Localized.T("Settings.Persistence.SaveFailedTitle");
        SaveFailedInfoBar.Message = Services.Localized.T("Settings.Persistence.SaveFailedBody");
        SaveFailedInfoBar.IsOpen = true;
    }

    internal void DismissSaveFailedBanner() => SaveFailedInfoBar.IsOpen = false;
}
