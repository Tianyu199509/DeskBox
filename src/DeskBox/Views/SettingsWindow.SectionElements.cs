using Microsoft.UI.Xaml;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    // Names inside a DataTemplate belong to that template, not the Window.
    // Looking up a control never creates its section. Visited roots are reused.
    private FrameworkElement? FindCreatedSectionElement(string tag, string name)
    {
        if (!_settingsSectionElements.TryGetValue(tag, out FrameworkElement? root))
        {
            return null;
        }
        return root.Name == name ? root : root.FindName(name) as FrameworkElement;
    }

    private global::DeskBox.Views.SettingsSections.AppearanceSettingsSection AppearanceSection =>
        (global::DeskBox.Views.SettingsSections.AppearanceSettingsSection)FindCreatedSectionElement("Appearance", "AppearanceSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AppearanceMaterialSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("AppearanceMaterialSettings", "AppearanceMaterialSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AppearanceDensitySettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("AppearanceDensitySettings", "AppearanceDensitySettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AppearanceWindowSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("AppearanceWindowSettings", "AppearanceWindowSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AppearanceAnimationSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("AppearanceAnimationSettings", "AppearanceAnimationSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel WidgetGroupsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("WidgetGroups", "WidgetGroupsSection")!;
    private global::DeskBox.Views.SettingsSections.CapsuleModeSettingsSection CapsuleModeSection =>
        (global::DeskBox.Views.SettingsSections.CapsuleModeSettingsSection)FindCreatedSectionElement("CapsuleMode", "CapsuleModeSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel CapsuleBehaviorSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("CapsuleBehaviorSettings", "CapsuleBehaviorSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel CapsuleArrangementSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("CapsuleArrangementSettings", "CapsuleArrangementSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel CapsuleAnimationSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("CapsuleAnimationSettings", "CapsuleAnimationSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel CapsuleOverridesSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("CapsuleOverridesSettings", "CapsuleOverridesSettingsSection")!;
    private global::DeskBox.Views.SettingsSections.FileWidgetSettingsSection AppearanceDetailSection =>
        (global::DeskBox.Views.SettingsSections.FileWidgetSettingsSection)FindCreatedSectionElement("AppearanceDetail", "AppearanceDetailSection")!;
    private global::DeskBox.Views.SettingsSections.DesktopOrganizationSettingsSection DesktopOrganizationSettingsSection =>
        (global::DeskBox.Views.SettingsSections.DesktopOrganizationSettingsSection)FindCreatedSectionElement("DesktopOrganizationSettings", "DesktopOrganizationSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FileDisplaySettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("FileDisplaySettings", "FileDisplaySettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FileStorageSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("FileStorageSettings", "FileStorageSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.Border ManagedStoragePathWarningBorder =>
        (global::Microsoft.UI.Xaml.Controls.Border)FindCreatedSectionElement("FileStorageSettings", "ManagedStoragePathWarningBorder")!;
    private global::Microsoft.UI.Xaml.Controls.TextBlock ManagedStoragePathWarningText =>
        (global::Microsoft.UI.Xaml.Controls.TextBlock)FindCreatedSectionElement("FileStorageSettings", "ManagedStoragePathWarningText")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel PathActionsPanel =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("FileStorageSettings", "PathActionsPanel")!;
    private global::Microsoft.UI.Xaml.Controls.Button OpenPathButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("FileStorageSettings", "OpenPathButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button PinQuickAccessButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("FileStorageSettings", "PinQuickAccessButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button ChangePathButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("FileStorageSettings", "ChangePathButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button CleanupStorageButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("FileStorageSettings", "CleanupStorageButton")!;
    private global::Microsoft.UI.Xaml.Controls.ToggleSwitch ManagedStorageDesktopShortcutToggle =>
        (global::Microsoft.UI.Xaml.Controls.ToggleSwitch)FindCreatedSectionElement("FileStorageSettings", "ManagedStorageDesktopShortcutToggle")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FileStackSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("FileStackSettings", "FileStackSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.ListView FileStackRulesListView =>
        (global::Microsoft.UI.Xaml.Controls.ListView)FindCreatedSectionElement("FileStackSettings", "FileStackRulesListView")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel InteractionSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("Interaction", "InteractionSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel InteractionWindowSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("InteractionWindowSettings", "InteractionWindowSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel GlobalHotkeyPresetButtonsPanel =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("InteractionWindowSettings", "GlobalHotkeyPresetButtonsPanel")!;
    private global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton GlobalHotkeyPresetF7Button =>
        (global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)FindCreatedSectionElement("InteractionWindowSettings", "GlobalHotkeyPresetF7Button")!;
    private global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton GlobalHotkeyPresetDoubleControlButton =>
        (global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)FindCreatedSectionElement("InteractionWindowSettings", "GlobalHotkeyPresetDoubleControlButton")!;
    private global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton GlobalHotkeyPresetAltSpaceButton =>
        (global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)FindCreatedSectionElement("InteractionWindowSettings", "GlobalHotkeyPresetAltSpaceButton")!;
    private global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton GlobalHotkeyPresetWinSpaceButton =>
        (global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)FindCreatedSectionElement("InteractionWindowSettings", "GlobalHotkeyPresetWinSpaceButton")!;
    private global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton GlobalHotkeyPresetWindowsTapButton =>
        (global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)FindCreatedSectionElement("InteractionWindowSettings", "GlobalHotkeyPresetWindowsTapButton")!;
    private global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton GlobalHotkeyPresetCopilotKeyButton =>
        (global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton)FindCreatedSectionElement("InteractionWindowSettings", "GlobalHotkeyPresetCopilotKeyButton")!;
    private global::Microsoft.UI.Xaml.Controls.Grid GlobalHotkeyCustomRow =>
        (global::Microsoft.UI.Xaml.Controls.Grid)FindCreatedSectionElement("InteractionWindowSettings", "GlobalHotkeyCustomRow")!;
    private global::Microsoft.UI.Xaml.Controls.Grid GlobalHotkeyActionsPanel =>
        (global::Microsoft.UI.Xaml.Controls.Grid)FindCreatedSectionElement("InteractionWindowSettings", "GlobalHotkeyActionsPanel")!;
    private global::Microsoft.UI.Xaml.Controls.Button GlobalHotkeyCaptureButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("InteractionWindowSettings", "GlobalHotkeyCaptureButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button ResetGlobalHotkeyButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("InteractionWindowSettings", "ResetGlobalHotkeyButton")!;
    private global::Microsoft.UI.Xaml.Controls.InfoBar GlobalHotkeyReservedWarning =>
        (global::Microsoft.UI.Xaml.Controls.InfoBar)FindCreatedSectionElement("InteractionWindowSettings", "GlobalHotkeyReservedWarning")!;
    private global::Microsoft.UI.Xaml.Controls.ToggleSwitch DesktopDoubleClickToggle =>
        (global::Microsoft.UI.Xaml.Controls.ToggleSwitch)FindCreatedSectionElement("InteractionWindowSettings", "DesktopDoubleClickToggle")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel ManagedStorageSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("ManagedStorage", "ManagedStorageSection")!;
    private global::Microsoft.UI.Xaml.Controls.TextBlock ManagedStorageSummaryText =>
        (global::Microsoft.UI.Xaml.Controls.TextBlock)FindCreatedSectionElement("ManagedStorage", "ManagedStorageSummaryText")!;
    private global::Microsoft.UI.Xaml.Controls.Button ManagedStorageRefreshButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("ManagedStorage", "ManagedStorageRefreshButton")!;
    private global::Microsoft.UI.Xaml.Controls.Border ManagedStorageEmptyState =>
        (global::Microsoft.UI.Xaml.Controls.Border)FindCreatedSectionElement("ManagedStorage", "ManagedStorageEmptyState")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel ManagedStorageFolderList =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("ManagedStorage", "ManagedStorageFolderList")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FeatureWidgetsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("FeatureWidgets", "FeatureWidgetsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FeatureWidgetList =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("FeatureWidgets", "FeatureWidgetList")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel QuickCaptureSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("QuickCaptureSettings", "QuickCaptureSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.ToggleSwitch QuickCaptureClipboardToggle =>
        (global::Microsoft.UI.Xaml.Controls.ToggleSwitch)FindCreatedSectionElement("QuickCaptureSettings", "QuickCaptureClipboardToggle")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel TodoSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("TodoSettings", "TodoSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel MusicSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("MusicSettings", "MusicSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel WeatherSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("WeatherSettings", "WeatherSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.AutoSuggestBox WeatherCitySearchBox =>
        (global::Microsoft.UI.Xaml.Controls.AutoSuggestBox)FindCreatedSectionElement("WeatherSettings", "WeatherCitySearchBox")!;
    private global::DeskBox.Views.SettingsSections.GlanceWidgetSettingsSection GlanceSettingsSection =>
        (global::DeskBox.Views.SettingsSections.GlanceWidgetSettingsSection)FindCreatedSectionElement("GlanceSettings", "GlanceSettingsSection")!;
    private global::DeskBox.Views.SettingsSections.SearchSettingsSection SearchSettingsSection =>
        (global::DeskBox.Views.SettingsSections.SearchSettingsSection)FindCreatedSectionElement("SearchSettings", "SearchSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel PerformanceSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("PerformanceSettings", "PerformanceSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel MaintenanceSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("Maintenance", "MaintenanceSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel BackupRestoreSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("BackupRestoreSettings", "BackupRestoreSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.Button RestoreDataBackupButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("BackupRestoreSettings", "RestoreDataBackupButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button ExportDataBackupButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("BackupRestoreSettings", "ExportDataBackupButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button CreateBackupSnapshotButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("BackupRestoreSettings", "CreateBackupSnapshotButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button OpenBackupFolderButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("BackupRestoreSettings", "OpenBackupFolderButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button RefreshBackupSnapshotsButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("BackupRestoreSettings", "RefreshBackupSnapshotsButton")!;
    private global::Microsoft.UI.Xaml.Controls.TextBlock BackupSnapshotSummaryText =>
        (global::Microsoft.UI.Xaml.Controls.TextBlock)FindCreatedSectionElement("BackupRestoreSettings", "BackupSnapshotSummaryText")!;
    private global::Microsoft.UI.Xaml.Controls.ListView BackupSnapshotsList =>
        (global::Microsoft.UI.Xaml.Controls.ListView)FindCreatedSectionElement("BackupRestoreSettings", "BackupSnapshotsList")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel DataHealthSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("DataHealthSettings", "DataHealthSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.TextBlock AttachmentHealthSummaryText =>
        (global::Microsoft.UI.Xaml.Controls.TextBlock)FindCreatedSectionElement("DataHealthSettings", "AttachmentHealthSummaryText")!;
    private global::Microsoft.UI.Xaml.Controls.Button CheckAttachmentHealthButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("DataHealthSettings", "CheckAttachmentHealthButton")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel ResetSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("ResetSettings", "ResetSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel CompatibilityDiagnosticsSettingsSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("CompatibilityDiagnosticsSettings", "CompatibilityDiagnosticsSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.Button ExportDiagnosticsButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("CompatibilityDiagnosticsSettings", "ExportDiagnosticsButton")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AboutSection =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("About", "AboutSection")!;
    private global::Microsoft.UI.Xaml.Controls.Grid AboutInfoGrid =>
        (global::Microsoft.UI.Xaml.Controls.Grid)FindCreatedSectionElement("About", "AboutInfoGrid")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AboutRightPanel =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("About", "AboutRightPanel")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AboutInfoActionsPanel =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("About", "AboutInfoActionsPanel")!;
    private global::Microsoft.UI.Xaml.Controls.Button AboutMeButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("About", "AboutMeButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button AboutWebsiteButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("About", "AboutWebsiteButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button OneClickUpdateButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("About", "OneClickUpdateButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button ViewReleaseNotesButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("About", "ViewReleaseNotesButton")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel UpdateActionsPanel =>
        (global::Microsoft.UI.Xaml.Controls.StackPanel)FindCreatedSectionElement("About", "UpdateActionsPanel")!;
    private global::Microsoft.UI.Xaml.Controls.Button OpenManualUpdateDownloadButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("About", "OpenManualUpdateDownloadButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button StoreSupportButton =>
        (global::Microsoft.UI.Xaml.Controls.Button)FindCreatedSectionElement("About", "StoreSupportButton")!;
    private global::Microsoft.UI.Xaml.Controls.PasswordBox CloudBackupPasswordBox =>
        (global::Microsoft.UI.Xaml.Controls.PasswordBox)FindCreatedSectionElement("CloudBackupSettings", "CloudBackupPasswordBox")!;
}
