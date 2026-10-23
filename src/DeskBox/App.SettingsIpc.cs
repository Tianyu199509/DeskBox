namespace DeskBox;

using DeskBox.Services;

/// <summary>
/// Main-process side of the settings-host IPC pipe (batch 3c): owns the
/// server, the method router over the in-process facades, and the reload
/// push that follows every durable local save — carrying the host commit id
/// of the last applied external snapshot so the settings host can
/// echo-suppress its own writes.
/// </summary>
public partial class App
{
    private SettingsIpcServer? _settingsIpcServer;
    private SettingsIpcMethodRouter? _settingsIpcRouter;

    private void StartSettingsIpcServer()
    {
        if (_settingsIpcServer is not null)
        {
            return;
        }

        var router = new SettingsIpcMethodRouter(
            SettingsService,
            new InProcessSettingsHotkeyActions(
                GlobalHotkeyService,
                SearchHotkeyService,
                DesktopDoubleClickActivationService,
                SettingsService),
            new InProcessSettingsDataActions(
                QuickCaptureService ?? throw new InvalidOperationException("Quick Capture service is not initialized."),
                LocalizationService));
        _settingsIpcRouter = router;
        _settingsIpcServer = SettingsIpcServer.Start(
            DeskBoxDataPathService.Current.SettingsIpcPipeName,
            router.DispatchAsync);

        SettingsService.LocalPersistCompleted += OnSettingsIpcLocalPersistCompleted;
        GlobalHotkeyService.RegistrationChanged += OnSettingsIpcHotkeyRegistrationChanged;
        Log("[SettingsIpc] Server started");
    }

    private void OnSettingsIpcLocalPersistCompleted(long mainCommitId)
    {
        long? hostCommitId = _settingsIpcRouter?.LastAppliedHostCommitId;
        _ = PushSettingsIpcEventAsync(
            SettingsIpcMethods.SettingsReloaded,
            SettingsIpcReloadPayload.Compose(mainCommitId, hostCommitId));
    }

    private void OnSettingsIpcHotkeyRegistrationChanged()
    {
        _ = PushSettingsIpcEventAsync(SettingsIpcMethods.HotkeyRegistrationChanged, null);
    }

    private async Task PushSettingsIpcEventAsync(string method, string? payload)
    {
        try
        {
            if (_settingsIpcServer is { } server)
            {
                await server.SendEventAsync(method, payload);
            }
        }
        catch (Exception ex)
        {
            Log($"[SettingsIpc] Event push failed for {method}: {ex.Message}");
        }
    }
}
