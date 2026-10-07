using System.Diagnostics.CodeAnalysis;
using Microsoft.UI.Xaml;
using WinRT;

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

    // FindName returns an untyped IInspectable; CsWinRT resolves the RCW
    // class for it by looking the runtime class name up through reflection
    // (WinRT.Runtime's FindTypeByName). Under Native AOT a projected control
    // with no kept reflection metadata resolves through the trim fallback
    // (typeNameToBaseTypeNameMapping) to a base class, and ComWrappers caches
    // that wrapper per native identity — so neither this re-wrap
    // (MarshalInspectable<T>.FromAbi lands on the same cached RCW and casts)
    // nor any other later cast can recover the exact type; they throw
    // InvalidCastException. Correctness therefore depends on the exact type
    // being reflection-visible before the first wrap: types the XAML compiler
    // or the app's own IL reference directly (Button, ListView, ...) already
    // are; a control only ever touched through this generic path is not and
    // must be rooted explicitly — see FindCloudBackupPasswordBox (P1-1).
    private T? FindCreatedSectionElement<T>(string tag, string name) where T : class
    {
        FrameworkElement? element = FindCreatedSectionElement(tag, name);
        if (element is null or T)
        {
            return element as T;
        }
        return MarshalInspectable<T>.FromAbi(((IWinRTObject)element).NativeObject.ThisPtr);
    }

    private global::DeskBox.Views.SettingsSections.AppearanceSettingsSection AppearanceSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.AppearanceSettingsSection>("Appearance", "AppearanceSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AppearanceMaterialSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("AppearanceMaterialSettings", "AppearanceMaterialSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AppearanceDensitySettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("AppearanceDensitySettings", "AppearanceDensitySettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AppearanceWindowSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("AppearanceWindowSettings", "AppearanceWindowSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AppearanceAnimationSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("AppearanceAnimationSettings", "AppearanceAnimationSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel WidgetGroupsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("WidgetGroups", "WidgetGroupsSection")!;
    private global::DeskBox.Views.SettingsSections.CapsuleModeSettingsSection CapsuleModeSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.CapsuleModeSettingsSection>("CapsuleMode", "CapsuleModeSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel CapsuleOverridesSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("CapsuleOverridesSettings", "CapsuleOverridesSettingsSection")!;
    private global::DeskBox.Views.SettingsSections.FileWidgetSettingsSection AppearanceDetailSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.FileWidgetSettingsSection>("AppearanceDetail", "AppearanceDetailSection")!;
    private global::DeskBox.Views.SettingsSections.DesktopOrganizationSettingsSection DesktopOrganizationSettingsSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.DesktopOrganizationSettingsSection>("DesktopOrganizationSettings", "DesktopOrganizationSettingsSection")!;
    private global::DeskBox.Views.SettingsSections.DisplaySettingsSection DisplaysSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.DisplaySettingsSection>("Displays", "DisplaysSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FileDisplaySettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("FileDisplaySettings", "FileDisplaySettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FileStorageSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("FileStorageSettings", "FileStorageSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.InfoBar ManagedStoragePathWarningBorder =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.InfoBar>("FileStorageSettings", "ManagedStoragePathWarningBorder")!;
    private global::Microsoft.UI.Xaml.Controls.TextBlock ManagedStoragePathWarningText =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.TextBlock>("FileStorageSettings", "ManagedStoragePathWarningText")!;
    private global::Microsoft.UI.Xaml.Controls.TextBlock DragOutWin10Note =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.TextBlock>("FileStorageSettings", "DragOutWin10Note")!;
    private global::Microsoft.UI.Xaml.Controls.ToggleSwitch DragOutModifierTipToggle =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ToggleSwitch>("FileStorageSettings", "DragOutModifierTipToggle")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel PathActionsPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("FileStorageSettings", "PathActionsPanel")!;
    private global::Microsoft.UI.Xaml.Controls.Button OpenPathButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("FileStorageSettings", "OpenPathButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button PinQuickAccessButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("FileStorageSettings", "PinQuickAccessButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button ChangePathButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("FileStorageSettings", "ChangePathButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button CleanupStorageButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("FileStorageSettings", "CleanupStorageButton")!;
    private global::Microsoft.UI.Xaml.Controls.ToggleSwitch ManagedStorageDesktopShortcutToggle =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ToggleSwitch>("FileStorageSettings", "ManagedStorageDesktopShortcutToggle")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FileStackSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("FileStackSettings", "FileStackSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.ListView FileStackRulesListView =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ListView>("FileStackSettings", "FileStackRulesListView")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel InteractionSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("Interaction", "InteractionSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel InteractionWindowSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("InteractionWindowSettings", "InteractionWindowSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel GlobalHotkeyPresetButtonsPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("InteractionWindowSettings", "GlobalHotkeyPresetButtonsPanel")!;
    private global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton GlobalHotkeyPresetF7Button =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("InteractionWindowSettings", "GlobalHotkeyPresetF7Button")!;
    private global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton GlobalHotkeyPresetDoubleControlButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("InteractionWindowSettings", "GlobalHotkeyPresetDoubleControlButton")!;
    private global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton GlobalHotkeyPresetAltSpaceButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("InteractionWindowSettings", "GlobalHotkeyPresetAltSpaceButton")!;
    private global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton GlobalHotkeyPresetWinSpaceButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("InteractionWindowSettings", "GlobalHotkeyPresetWinSpaceButton")!;
    private global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton GlobalHotkeyPresetWindowsTapButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("InteractionWindowSettings", "GlobalHotkeyPresetWindowsTapButton")!;
    private global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton GlobalHotkeyPresetCopilotKeyButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>("InteractionWindowSettings", "GlobalHotkeyPresetCopilotKeyButton")!;
    private global::Microsoft.UI.Xaml.Controls.Grid GlobalHotkeyCustomRow =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Grid>("InteractionWindowSettings", "GlobalHotkeyCustomRow")!;
    private global::Microsoft.UI.Xaml.Controls.Grid GlobalHotkeyActionsPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Grid>("InteractionWindowSettings", "GlobalHotkeyActionsPanel")!;
    private global::Microsoft.UI.Xaml.Controls.Button GlobalHotkeyCaptureButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("InteractionWindowSettings", "GlobalHotkeyCaptureButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button ResetGlobalHotkeyButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("InteractionWindowSettings", "ResetGlobalHotkeyButton")!;
    private global::Microsoft.UI.Xaml.Controls.InfoBar GlobalHotkeyReservedWarning =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.InfoBar>("InteractionWindowSettings", "GlobalHotkeyReservedWarning")!;
    private global::Microsoft.UI.Xaml.Controls.ToggleSwitch DesktopDoubleClickToggle =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ToggleSwitch>("InteractionWindowSettings", "DesktopDoubleClickToggle")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel ManagedStorageSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("ManagedStorage", "ManagedStorageSection")!;
    private global::Microsoft.UI.Xaml.Controls.TextBlock ManagedStorageSummaryText =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.TextBlock>("ManagedStorage", "ManagedStorageSummaryText")!;
    private global::Microsoft.UI.Xaml.Controls.Button ManagedStorageRefreshButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("ManagedStorage", "ManagedStorageRefreshButton")!;
    private global::Microsoft.UI.Xaml.Controls.Border ManagedStorageEmptyState =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Border>("ManagedStorage", "ManagedStorageEmptyState")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel ManagedStorageFolderList =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("ManagedStorage", "ManagedStorageFolderList")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FeatureWidgetsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("FeatureWidgets", "FeatureWidgetsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FeatureWidgetList =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("FeatureWidgets", "FeatureWidgetList")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel QuickCaptureSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("QuickCaptureSettings", "QuickCaptureSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.ToggleSwitch QuickCaptureClipboardToggle =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ToggleSwitch>("QuickCaptureSettings", "QuickCaptureClipboardToggle")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel TodoSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("TodoSettings", "TodoSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel MusicSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("MusicSettings", "MusicSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel WeatherSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("WeatherSettings", "WeatherSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.AutoSuggestBox WeatherCitySearchBox =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.AutoSuggestBox>("WeatherSettings", "WeatherCitySearchBox")!;
    private global::DeskBox.Views.SettingsSections.GlanceWidgetSettingsSection GlanceSettingsSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.GlanceWidgetSettingsSection>("GlanceSettings", "GlanceSettingsSection")!;
    private global::DeskBox.Views.SettingsSections.SearchSettingsSection SearchSettingsSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.SearchSettingsSection>("SearchSettings", "SearchSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel PerformanceSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("PerformanceSettings", "PerformanceSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel MaintenanceSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("Maintenance", "MaintenanceSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel BackupRestoreSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("BackupRestoreSettings", "BackupRestoreSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.Button RestoreDataBackupButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("BackupRestoreSettings", "RestoreDataBackupButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button ExportDataBackupButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("BackupRestoreSettings", "ExportDataBackupButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button CreateBackupSnapshotButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("BackupRestoreSettings", "CreateBackupSnapshotButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button OpenBackupFolderButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("BackupRestoreSettings", "OpenBackupFolderButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button RefreshBackupSnapshotsButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("BackupRestoreSettings", "RefreshBackupSnapshotsButton")!;
    private global::Microsoft.UI.Xaml.Controls.TextBlock BackupSnapshotSummaryText =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.TextBlock>("BackupRestoreSettings", "BackupSnapshotSummaryText")!;
    private global::Microsoft.UI.Xaml.Controls.ListView BackupSnapshotsList =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ListView>("BackupRestoreSettings", "BackupSnapshotsList")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel DataHealthSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("DataHealthSettings", "DataHealthSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.TextBlock AttachmentHealthSummaryText =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.TextBlock>("DataHealthSettings", "AttachmentHealthSummaryText")!;
    private global::Microsoft.UI.Xaml.Controls.Button CheckAttachmentHealthButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("DataHealthSettings", "CheckAttachmentHealthButton")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel ResetSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("ResetSettings", "ResetSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel CompatibilityDiagnosticsSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("CompatibilityDiagnosticsSettings", "CompatibilityDiagnosticsSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.Button ExportDiagnosticsButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("CompatibilityDiagnosticsSettings", "ExportDiagnosticsButton")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AboutSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("About", "AboutSection")!;
    private global::Microsoft.UI.Xaml.Controls.Grid AboutInfoGrid =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Grid>("About", "AboutInfoGrid")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AboutRightPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("About", "AboutRightPanel")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel AboutInfoActionsPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("About", "AboutInfoActionsPanel")!;
    private global::Microsoft.UI.Xaml.Controls.Button AboutMeButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("About", "AboutMeButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button AboutWebsiteButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("About", "AboutWebsiteButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button OneClickUpdateButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("About", "OneClickUpdateButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button ViewReleaseNotesButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("About", "ViewReleaseNotesButton")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel UpdateActionsPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("About", "UpdateActionsPanel")!;
    private global::Microsoft.UI.Xaml.Controls.Button OpenManualUpdateDownloadButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("About", "OpenManualUpdateDownloadButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button StoreSupportButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("About", "StoreSupportButton")!;
    // P1-1 (incident 2026-09-30 §4): the cloud-backup PasswordBox is the one
    // deferred-section control the XAML compiler never references — it has no
    // x:Bind and no managed handler, so SettingsWindow.g.cs / XamlTypeInfo
    // contain no typed reference to it, and it is never constructed in C#.
    // Under Native AOT that left the projected type trimmed out of reflection,
    // so every typed lookup wrapped the native control as a base class and the
    // getter threw InvalidCastException ("Specified cast is not valid") on
    // 测试连接/保存密码 — retail only, because JIT builds resolve the name
    // through full reflection. DynamicDependency roots the projected type so
    // WinRT.Runtime's FindTypeByName resolves it and the FIRST (and identity-
    // cached) wrapper is exact; the typed path never reaches the cast.
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.All,
        typeof(global::Microsoft.UI.Xaml.Controls.PasswordBox))]
    private global::Microsoft.UI.Xaml.Controls.PasswordBox? FindCloudBackupPasswordBox() =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.PasswordBox>("CloudBackupSettings", "CloudBackupPasswordBox");

    // Nullable by contract: the section is created on demand (DeferredSections),
    // so a consumer can run before creation or against a torn-down window.
    // Callers must null-guard; a visible section's lookup is typed-correct.
    private global::Microsoft.UI.Xaml.Controls.PasswordBox? CloudBackupPasswordBox => FindCloudBackupPasswordBox();
}
