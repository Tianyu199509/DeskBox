#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using DeskBox.Services;
using DeskBox.Views;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace DeskBox;

public partial class App
{
    private SettingsWindow? _settingsWindow;

    public SettingsWindow? SettingsWindowInstance => _settingsWindow;

    private bool HasInProcessSettingsWindowForSmoke() => _settingsWindow is not null;

    private IntPtr TryGetInProcessSettingsWindowHandleForSmoke() =>
        _settingsWindow is { IsVisibleToUser: true } window
            ? WindowNative.GetWindowHandle(window)
            : IntPtr.Zero;

    private Task StopInProcessSettingsCloudBackupActionsForSmokeAsync() =>
        _settingsWindow?.StopCloudBackupActionsAsync() ?? Task.CompletedTask;

    private void CloseInProcessSettingsWindowForSmokeShutdown()
    {
        _settingsWindow?.CloseForShutdown();
        _settingsWindow = null;
    }

    private void RefreshInProcessSettingsDesktopOrganizationForSmoke() =>
        _settingsWindow?.RefreshDesktopOrganizationState();

    private void OpenInProcessSettingsForSmoke(string? sectionTag = null)
    {
        // The harness drives the settings window inside its own single
        // process; a second-process host cannot exist there.
        CancelBackgroundMemoryCleanup();
        var settingsWindow = _settingsWindow ?? CreateSettingsWindow();
        settingsWindow.ShowWindow();
        if (!string.IsNullOrEmpty(sectionTag))
        {
            settingsWindow.ShowSection(sectionTag);
        }
    }

    private void ShowInProcessGlanceSettingsForSmoke(string widgetId)
    {
        CancelBackgroundMemoryCleanup();
        var settingsWindow = _settingsWindow ?? CreateSettingsWindow();
        settingsWindow.ShowWindow();
        settingsWindow.ShowGlanceSection(widgetId);
    }

    private void QueueInProcessUpdateInstallResultForSmoke(string outcome)
    {
        OpenInProcessSettingsForSmoke("About");
        _settingsWindow?.QueueUpdateInstallResultDialog(outcome);
    }

    private async Task RunInProcessOneClickUpdateFromToastForSmokeAsync()
    {
        if (_settingsWindow is not null)
        {
            await _settingsWindow.RunOneClickUpdateFlowAsync();
        }
    }

    // In-process settings window construction — smoke harness only (batch 4:
    // the retail binary opens settings exclusively in the settings-host
    // process).
    private SettingsWindow CreateSettingsWindow()
    {
        var input = new SettingsWindowFactoryInput(
            SettingsService, ThemeService, LocalizationService,
            _todoSettings ?? throw new InvalidOperationException("Todo settings are not initialized."),
            _featureWidgetsSettings ?? throw new InvalidOperationException("Feature widgets settings are not initialized."),
            _searchSettings ?? throw new InvalidOperationException("Search settings are not initialized."),
            _backupSettings ?? throw new InvalidOperationException("Backup settings are not initialized."),
            _backupRestoreActions ?? throw new InvalidOperationException(
                "Backup restore actions are not initialized."),
            _quickCaptureSettings ?? throw new InvalidOperationException("Quick Capture is not initialized."),
            _backupRuntime ?? throw new InvalidOperationException("Backup runtime is not initialized."),
            _appearanceSettings ?? throw new InvalidOperationException("Appearance settings are not initialized."),
            _capsuleSettings ?? throw new InvalidOperationException("Capsule settings are not initialized."),
            _interactionSettings ?? throw new InvalidOperationException("Interaction settings are not initialized."),
            _fileDisplaySettings ?? throw new InvalidOperationException("File display settings are not initialized."),
            _fileStackSettings ?? throw new InvalidOperationException("File stack settings are not initialized."),
            _groupNavigationSettings ?? throw new InvalidOperationException("Group navigation settings are not initialized."),
            _managedStorageSettings ?? throw new InvalidOperationException("Managed storage settings are not initialized."),
            _maintenanceSettings ?? throw new InvalidOperationException("Maintenance settings are not initialized."),
            _performanceSettings ?? throw new InvalidOperationException("Performance settings are not initialized."),
            CreateSettingsHotkeyActions(),
            new InProcessSettingsWidgetActions(
                WidgetManager,
                SettingsService,
                FileService,
                OrganizerService,
                LocalizationService,
                applyQuickCaptureEnabled: enabled =>
                    (_quickCaptureSettings ?? throw new InvalidOperationException(
                        "Quick Capture is not initialized.")).SetEnabledAsync(enabled, reveal: enabled),
                applyTodoEnabled: enabled =>
                    (_todoSettings ?? throw new InvalidOperationException(
                        "Todo settings are not initialized.")).SetEnabledAsync(enabled),
                openCustomWidgetStudio: (widgetId, initialIdea) => ShowCustomWidgetStudio(widgetId, initialIdea)),
            new InProcessSettingsRestoreActions(
                DataBackupService ?? throw new InvalidOperationException("Backup service is not initialized."),
                AttachmentHealthService ?? throw new InvalidOperationException("Attachment health service is not initialized.")),
            new InProcessSettingsAppActions(this, LocalizationService),
            new InProcessSettingsDataActions(
                QuickCaptureService ?? throw new InvalidOperationException("Quick Capture service is not initialized."),
                LocalizationService,
                clipboardServiceProvider: () => _clipboardHistoryService),
            AppUpdateService,
            action => UiDispatcherQueue.TryEnqueue(() => action()));
        _settingsWindow = input.CreateWindow();
        _settingsWindow.Closed += SettingsWindow_ClosedForApp;
        return _settingsWindow;
    }

    private Contracts.ISettingsHotkeyActions CreateSettingsHotkeyActions() =>
        new InProcessSettingsHotkeyActions(
            GlobalHotkeyService,
            SearchHotkeyService,
            DesktopDoubleClickActivationService,
            SettingsService,
            searchHotkeyProvider: () => _searchHotkeyService,
            clipboardHotkeyProvider: () => _clipboardHotkeyService);

    private void SettingsWindow_ClosedForApp(object sender, WindowEventArgs args)
    {
        // Hide-and-reuse cancels at AppWindow.Closing and never reaches this
        // event; Closed only ever runs for real destruction (shutdown).
        if (sender is SettingsWindow settingsWindow)
        {
            settingsWindow.Closed -= SettingsWindow_ClosedForApp;
        }

        if (ReferenceEquals(_settingsWindow, sender))
        {
            _settingsWindow = null;
        }

        ScheduleLightMemoryCleanup(completedHeavyOperation: true);
        ScheduleBackgroundMemoryCleanup("settings-closed");
    }
}
#endif
