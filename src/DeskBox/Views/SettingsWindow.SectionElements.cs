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
    private global::Microsoft.UI.Xaml.Controls.StackPanel WidgetGroupsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("WidgetGroups", "WidgetGroupsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel? WidgetGroupsOverviewPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("WidgetGroups", "WidgetGroupsOverviewPanel");
    private global::Microsoft.UI.Xaml.Controls.StackPanel? WidgetGroupDetailSubPagePanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("WidgetGroups", "WidgetGroupDetailSubPagePanel");
    [DynamicDependency(
        DynamicallyAccessedMemberTypes.All,
        typeof(global::Microsoft.UI.Xaml.Controls.ContentControl))]
    private global::Microsoft.UI.Xaml.Controls.ContentControl? FindWidgetGroupDetailPresenter() =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ContentControl>("WidgetGroups", "WidgetGroupDetailPresenter");
    private global::Microsoft.UI.Xaml.Controls.ContentControl? WidgetGroupDetailPresenter => FindWidgetGroupDetailPresenter();
    private global::DeskBox.Views.SettingsSections.FileWidgetSettingsSection AppearanceDetailSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.FileWidgetSettingsSection>("AppearanceDetail", "AppearanceDetailSection")!;
    private global::DeskBox.Views.SettingsSections.DisplaySettingsSection DisplaysSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.DisplaySettingsSection>("Displays", "DisplaysSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FileStackSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("FileStackSettings", "FileStackSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.ListView FileStackRulesListView =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ListView>("FileStackSettings", "FileStackRulesListView")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel InteractionSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("Interaction", "InteractionSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel? InteractionOverviewPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("Interaction", "InteractionOverviewPanel");
    private global::Microsoft.UI.Xaml.Controls.StackPanel? CapsuleModeSubPagePanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("Interaction", "CapsuleModeSubPagePanel");
    private global::CommunityToolkit.WinUI.Controls.SettingsCard? CapsuleModeEntryCard =>
        FindCreatedSectionElement<global::CommunityToolkit.WinUI.Controls.SettingsCard>("Interaction", "CapsuleModeEntryCard");
    private global::Microsoft.UI.Xaml.Controls.ComboBox? CapsuleModeEntryComboBox =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ComboBox>("Interaction", "CapsuleModeEntryComboBox");
    private global::DeskBox.Views.SettingsSections.CapsuleModeSettingsSection CapsuleModeSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.CapsuleModeSettingsSection>("Interaction", "CapsuleModeSection")!;
    private global::CommunityToolkit.WinUI.Controls.SettingsCard? DisplaysEntryCard =>
        FindCreatedSectionElement<global::CommunityToolkit.WinUI.Controls.SettingsCard>("Interaction", "DisplaysEntryCard");
    private global::Microsoft.UI.Xaml.Controls.Grid GlobalHotkeyCustomRow =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Grid>("Interaction", "GlobalHotkeyCustomRow")!;
    private global::Microsoft.UI.Xaml.Controls.Grid GlobalHotkeyActionsPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Grid>("Interaction", "GlobalHotkeyActionsPanel")!;
    private global::Microsoft.UI.Xaml.Controls.Button GlobalHotkeyCaptureButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("Interaction", "GlobalHotkeyCaptureButton")!;
    private global::Microsoft.UI.Xaml.Controls.InfoBar GlobalHotkeyReservedWarning =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.InfoBar>("Interaction", "GlobalHotkeyReservedWarning")!;
    private global::Microsoft.UI.Xaml.Controls.ToggleSwitch DesktopDoubleClickToggle =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ToggleSwitch>("Interaction", "DesktopDoubleClickToggle")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FeatureWidgetsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("FeatureWidgets", "FeatureWidgetsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FeatureWidgetsHubHeaderPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("FeatureWidgets", "FeatureWidgetsHubHeaderPanel")!;
    private global::Microsoft.UI.Xaml.Controls.TextBlock FeatureWidgetsHubTitleText =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.TextBlock>("FeatureWidgets", "FeatureWidgetsHubTitleText")!;
    // Segmented is a managed C# control instantiated via XamlTypeInfo and
    // directly referenced by SettingsWindow.g.cs (SelectionChanged + x:Bind);
    // unlike the unreferenced native PasswordBox below, rooting All members
    // here is unnecessary and pulls dead net8.0 reflection helpers into
    // Native AOT codegen (IL3050).
    private global::CommunityToolkit.WinUI.Controls.Segmented FeatureWidgetsHubSegmented =>
        FindCreatedSectionElement<global::CommunityToolkit.WinUI.Controls.Segmented>("FeatureWidgets", "FeatureWidgetsHubSegmented")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel CustomWidgetsToolbarPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("FeatureWidgets", "CustomWidgetsToolbarPanel")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FeatureWidgetListHost =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("FeatureWidgets", "FeatureWidgetListHost")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel CustomWidgetHostPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("FeatureWidgets", "CustomWidgetHostPanel")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel FeatureWidgetList =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("FeatureWidgets", "FeatureWidgetList")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel QuickCaptureSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("QuickCaptureSettings", "QuickCaptureSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.ComboBox QuickCaptureAttachmentStorageModeComboBox =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ComboBox>("QuickCaptureSettings", "QuickCaptureAttachmentStorageModeComboBox")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel TodoSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("TodoSettings", "TodoSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.ComboBox TodoAttachmentStorageModeComboBox =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ComboBox>("TodoSettings", "TodoAttachmentStorageModeComboBox")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel MusicSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("MusicSettings", "MusicSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel WeatherSettingsSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("WeatherSettings", "WeatherSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.AutoSuggestBox WeatherCitySearchBox =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.AutoSuggestBox>("WeatherSettings", "WeatherCitySearchBox")!;
    private global::DeskBox.Views.SettingsSections.GlanceWidgetSettingsSection GlanceSettingsSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.GlanceWidgetSettingsSection>("GlanceSettings", "GlanceSettingsSection")!;
    private global::DeskBox.Views.SettingsSections.CustomWidgetSettingsSection CustomWidgetSettingsSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.CustomWidgetSettingsSection>("CustomWidgetSettings", "CustomWidgetSettingsSection")!;
    private global::DeskBox.Views.SettingsSections.AiSettingsSection AiSettingsSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.AiSettingsSection>("AiSettings", "AiSettingsSection")!;
    private global::DeskBox.Views.SettingsSections.ClipboardSettingsSection ClipboardSettingsSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.ClipboardSettingsSection>("ClipboardSettings", "ClipboardSettingsSection")!;
    private global::DeskBox.Views.SettingsSections.SearchSettingsSection SearchSettingsSection =>
        FindCreatedSectionElement<global::DeskBox.Views.SettingsSections.SearchSettingsSection>("SearchSettings", "SearchSettingsSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel MaintenanceSection =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("Maintenance", "MaintenanceSection")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel? MaintenanceOverviewPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("Maintenance", "MaintenanceOverviewPanel");
    private global::Microsoft.UI.Xaml.Controls.StackPanel? AutomaticBackupSubPagePanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("Maintenance", "AutomaticBackupSubPagePanel");
    private global::Microsoft.UI.Xaml.Controls.StackPanel? CloudBackupSubPagePanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("Maintenance", "CloudBackupSubPagePanel");
    private global::CommunityToolkit.WinUI.Controls.SettingsCard? AutomaticBackupEntryCard =>
        FindCreatedSectionElement<global::CommunityToolkit.WinUI.Controls.SettingsCard>("Maintenance", "AutomaticBackupEntryCard");
    private global::Microsoft.UI.Xaml.Controls.ToggleSwitch? AutomaticBackupEntryToggle =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ToggleSwitch>("Maintenance", "AutomaticBackupEntryToggle");
    private global::CommunityToolkit.WinUI.Controls.SettingsCard? CloudBackupEntryCard =>
        FindCreatedSectionElement<global::CommunityToolkit.WinUI.Controls.SettingsCard>("Maintenance", "CloudBackupEntryCard");
    private global::Microsoft.UI.Xaml.Controls.ToggleSwitch? CloudBackupEntryToggle =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ToggleSwitch>("Maintenance", "CloudBackupEntryToggle");
    private global::Microsoft.UI.Xaml.Controls.Button RestoreDataBackupButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("Maintenance", "RestoreDataBackupButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button ExportDataBackupButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("Maintenance", "ExportDataBackupButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button CreateBackupSnapshotButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("Maintenance", "CreateBackupSnapshotButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button OpenBackupFolderButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("Maintenance", "OpenBackupFolderButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button RefreshBackupSnapshotsButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("Maintenance", "RefreshBackupSnapshotsButton")!;
    private global::Microsoft.UI.Xaml.Controls.TextBlock BackupSnapshotSummaryText =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.TextBlock>("Maintenance", "BackupSnapshotSummaryText")!;
    private global::Microsoft.UI.Xaml.Controls.ListView BackupSnapshotsList =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.ListView>("Maintenance", "BackupSnapshotsList")!;
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
    private global::Microsoft.UI.Xaml.Controls.StackPanel UpdateCheckActionsPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("About", "UpdateCheckActionsPanel")!;
    private global::Microsoft.UI.Xaml.Controls.Button ViewReleaseNotesButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("About", "ViewReleaseNotesButton")!;
    private global::Microsoft.UI.Xaml.Controls.StackPanel UpdateActionsPanel =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.StackPanel>("About", "UpdateActionsPanel")!;
    private global::Microsoft.UI.Xaml.Controls.Button OpenManualUpdateDownloadButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("About", "OpenManualUpdateDownloadButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button StoreSupportButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("About", "StoreSupportButton")!;
    private global::CommunityToolkit.WinUI.Controls.SettingsExpander? AboutDiagnosticsExpander =>
        FindCreatedSectionElement<global::CommunityToolkit.WinUI.Controls.SettingsExpander>("About", "AboutDiagnosticsExpander");
    private global::Microsoft.UI.Xaml.Controls.TextBlock AttachmentHealthSummaryText =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.TextBlock>("About", "AttachmentHealthSummaryText")!;
    private global::Microsoft.UI.Xaml.Controls.Button CheckAttachmentHealthButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("About", "CheckAttachmentHealthButton")!;
    private global::Microsoft.UI.Xaml.Controls.Button ExportDiagnosticsButton =>
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.Button>("About", "ExportDiagnosticsButton")!;

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
        FindCreatedSectionElement<global::Microsoft.UI.Xaml.Controls.PasswordBox>("Maintenance", "CloudBackupPasswordBox");

    // Nullable by contract: the section is created on demand (DeferredSections),
    // so a consumer can run before creation or against a torn-down window.
    // Callers must null-guard; a visible section's lookup is typed-correct.
    private global::Microsoft.UI.Xaml.Controls.PasswordBox? CloudBackupPasswordBox => FindCloudBackupPasswordBox();
}
