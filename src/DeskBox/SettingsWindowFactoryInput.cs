using DeskBox.Contracts;
using DeskBox.Services;
using DeskBox.Features.Backup;
using DeskBox.Features.Search;
using DeskBox.Features.Todo;
using DeskBox.ViewModels;
using DeskBox.Views;

namespace DeskBox;

/// <summary>
/// Everything the shared settings-window editor factory needs (batch
/// 3c-3b): consumed identically by the main process (coordinators wired to
/// live services) and the settings-host process (coordinators over a remote
/// persistence sink). The 18 editor view models built here are
/// process-neutral — they only see SettingsService, LocalizationService,
/// and delegate parameters.
/// </summary>
internal sealed record SettingsWindowFactoryInput(
    SettingsService SettingsService,
    ThemeService ThemeService,
    LocalizationService LocalizationService,
    TodoSettingsCoordinator TodoSettings,
    FeatureWidgetsSettingsCoordinator FeatureWidgetsSettings,
    SearchSettingsCoordinator SearchSettings,
    IBackupSettings BackupSettings,
    IBackupRestoreActions BackupRestoreActions,
    IQuickCaptureSettings QuickCaptureSettings,
    IBackupCommands BackupCommands,
    AppearanceSettingsCoordinator AppearanceSettings,
    CapsuleSettingsCoordinator CapsuleSettings,
    InteractionSettingsCoordinator InteractionSettings,
    FileDisplaySettingsCoordinator FileDisplaySettings,
    FileStackSettingsCoordinator FileStackSettings,
    GroupNavigationSettingsCoordinator GroupNavigationSettings,
    ManagedStorageSettingsCoordinator ManagedStorageSettings,
    MaintenanceSettingsCoordinator MaintenanceSettings,
    PerformanceSettingsCoordinator PerformanceSettings,
    Contracts.ISettingsHotkeyActions HotkeyActions,
    ISettingsWidgetActions WidgetActions,
    ISettingsRestoreActions RestoreActions,
    ISettingsAppActions AppActions,
    ISettingsDataActions DataActions,
    IAppUpdateService? AppUpdateService,
    Func<Action, bool> TryEnqueueOnUi)
{
    internal SettingsWindow CreateWindow()
    {
        string T(string key) => LocalizationService.T(key);
        string Format(string key, object[] args) => LocalizationService.Format(key, args);
        void ReportError(string prefix, Exception ex) => App.Log($"{prefix} {ex}");

        return new SettingsWindow(
            SettingsService, ThemeService, LocalizationService,
            new TodoSettingsViewModel(
                TodoSettings,
                T,
                Format,
                ex => ReportError("[TodoSettings] Update failed:", ex)),
            new DeskBox.Features.Weather.WeatherSettingsViewModel(
                FeatureWidgetsSettings,
                T,
                Format,
                ex => ReportError("[WeatherSettings] Update failed:", ex)),
            new SearchSettingsViewModel(
                SearchSettings,
                TryEnqueueOnUi,
                ex => ReportError("[SearchSettings] Operation failed:", ex)),
            new BackupSettingsViewModel(
                BackupSettings,
                TryEnqueueOnUi,
                ex => ReportError("[BackupSettings] Operation failed:", ex),
                localize: T,
                format: Format,
                isStoreChannel: AppDistributionService.Current.IsMicrosoftStore),
            BackupRestoreActions,
            QuickCaptureSettings,
            new DeskBox.Features.QuickCapture.QuickCaptureSettingsViewModel(
                QuickCaptureSettings,
                T,
                Format,
                message => App.Log(message),
                ex => ReportError("[QuickCapture] Settings operation failed:", ex)),
            SearchSettings,
            BackupCommands,
            new DeskBox.Features.Appearance.AppearanceSettingsViewModel(AppearanceSettings, T),
            new DeskBox.Features.Capsule.CapsuleSettingsViewModel(CapsuleSettings, T, Format),
            new DeskBox.Features.Interaction.InteractionSettingsViewModel(InteractionSettings, T),
            new DeskBox.Features.FileDisplay.FileDisplaySettingsViewModel(FileDisplaySettings),
            new DeskBox.Features.FileStack.FileStackSettingsViewModel(FileStackSettings, T, Format),
            new DeskBox.Features.GroupNavigation.GroupNavigationSettingsViewModel(GroupNavigationSettings, T),
            new DeskBox.Features.FeatureWidgets.FeatureWidgetsSettingsViewModel(FeatureWidgetsSettings, T),
            new DeskBox.Features.Music.MusicSettingsViewModel(FeatureWidgetsSettings, T),
            new DeskBox.Features.ManagedStorage.ManagedStorageSettingsViewModel(ManagedStorageSettings, T),
            new DeskBox.Features.Maintenance.MaintenanceSettingsViewModel(MaintenanceSettings),
            new DeskBox.Features.Performance.PerformanceSettingsViewModel(
                PerformanceSettings,
                InteractionSettings,
                T,
                () => LocalizationService.IsChinese),
            HotkeyActions,
            WidgetActions,
            RestoreActions,
            AppActions,
            DataActions,
            AppUpdateService);
    }
}
