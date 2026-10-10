#if DESKBOX_NATIVE_AOT
namespace DeskBox.Features.Backup;

// The backup settings family (local backups, cloud backups and the
// compatibility-diagnostics section, batch 49) keeps its runtime {Binding}
// surface and binds through section-level DataContext switches. Expose only
// the properties used by that XAML surface in NativeAOT builds, mirroring
// the Glance/Music/FileStack/QuickCapture/Todo/Weather editor bridge
// pattern. RemoteSnapshotItems is intentionally absent: the snapshot
// ListView's ItemsSource is assigned in code-behind (an object[] snapshot),
// so no {Binding} ever reads the collection property.
[WinRT.GeneratedBindableCustomProperty([
    nameof(ActionsEnabled),
    nameof(AvailableIntervalOptions),
    nameof(AvailableLocalIntervalOptions),
    nameof(AvailableLocalRetentionOptions),
    nameof(AvailableProviderOptions),
    nameof(AvailableRetentionOptions),
    nameof(CanRepairDragDrop),
    nameof(CanResyncRuntime),
    nameof(ConnectionStatusText),
    nameof(CredentialStatusText),
    nameof(DragDropAppCompatText),
    nameof(DragDropDetailText),
    nameof(DragDropExplorerText),
    nameof(DragDropProcessText),
    nameof(DragDropRepairStatusText),
    nameof(DragDropShortcutText),
    nameof(DragDropStartupText),
    nameof(DragDropSummaryText),
    nameof(DragDropUacText),
    nameof(HttpWarningText),
    nameof(LocalBackupEnabled),
    nameof(LocalDirectoryDisplayText),
    nameof(LocalFallbackWarningText),
    nameof(IncludeQuickCaptureData),
    nameof(RemotePath),
    nameof(RuntimeDetailText),
    nameof(RuntimeSummaryText),
    nameof(SelectedIntervalMinutes),
    nameof(SelectedLocalIntervalMinutes),
    nameof(SelectedLocalRetentionCount),
    nameof(SelectedProvider),
    nameof(SelectedRetentionCount),
    nameof(ServerUrl),
    nameof(ShowHttpWarning),
    nameof(ShowLocalFallbackWarning),
    nameof(ShowStoreUninstallNotice),
    nameof(ShowWebDavFields),
    nameof(StatusText),
    nameof(IncludeTodoData),
    nameof(Username),
    nameof(IncludeWidgetStyle)
], [])]
public sealed partial class BackupSettingsViewModel
{
}
#endif
