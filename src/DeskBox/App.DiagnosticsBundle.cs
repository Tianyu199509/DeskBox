using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using DeskBox.Core.Persistence;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private DeskBoxDiagnosticsBundleService? _diagnosticsBundleService;
    public DeskBoxDiagnosticsBundleService DiagnosticsBundleService =>
        _diagnosticsBundleService ??= Services.DiagnosticsBundleService;

    public DeskBoxDiagnosticSnapshot CreateDiagnosticSnapshot()
    {
        AppRuntimeHealthSnapshot? runtimeHealth = DiagnosticsService?.GetRuntimeHealthSnapshot(
            EverythingSearchService,
            WidgetManager?.GetFolderWatcherHealthSnapshots());
        DeskBoxWidgetManagerDiagnostic widgetManager =
            WidgetManager?.CreateDiagnosticsSnapshot() ?? DeskBoxWidgetManagerDiagnostic.Empty;
        IReadOnlyList<DeskBoxDisplayDiagnostic> displays = GetDisplayDiagnostics();
        SettingsPersistenceFailure? saveFailure = SettingsService.LastPersistenceFailure;
        ShortcutNativeDiagnosticState shortcutNative =
            ShortcutNativeBackend.CaptureDiagnosticState();
        var startupLedger = new DeskBoxStartupLedgerDiagnostic(
            StormGuardActive: _startupStormAssessment?.SuppressFileMutatingStartup == true,
            AssessmentReason: _startupStormAssessment?.Reason,
            PreviousLaunchStartedAtUtc: _startupStormAssessment?.PreviousLaunchStartedAtUtc);
        var callbackRadarCounts = Platform.CallbackDepth.SnapshotWindowOperationCounts();
        var callbackRadar = new DeskBoxCallbackRadarDiagnostic(
            callbackRadarCounts.Values.Sum(),
            callbackRadarCounts,
            Platform.UiDispatch.PostAfterCallbackDroppedCount);
        RecoveryOfferMarker? recoveryOfferMarker = RecoveryOfferStore.TryRead();
        var recoveryOffer = new DeskBoxRecoveryOfferDiagnostic(
            recoveryOfferMarker is not null,
            recoveryOfferMarker?.DetectedAtUtc.ToUniversalTime(),
            recoveryOfferMarker?.SnapshotCreatedAtUtc.ToUniversalTime(),
            recoveryOfferMarker?.SnapshotAppVersion,
            recoveryOfferMarker?.SnapshotWidgetCount ?? 0,
            _lastRecoveryOfferOutcome.ToString());

        return new DeskBoxDiagnosticSnapshot(
            SchemaVersion: 9,
            GeneratedAtUtc: DateTimeOffset.UtcNow,
            AppVersion: GetDiagnosticVersion(),
            DistributionChannel: DistributionService.ChannelName,
            IsPackaged: DistributionService.IsPackaged,
            OperatingSystem: RuntimeInformation.OSDescription,
            ProcessArchitecture: RuntimeInformation.ProcessArchitecture.ToString(),
            UiCulture: CultureInfo.CurrentUICulture.Name,
            Hotkeys: BuildHotkeyDiagnostics(),
            Settings: new DeskBoxSettingsDiagnostic(
                SettingsService.LastLoadRecoveryState,
                SettingsService.HasPendingSave,
                saveFailure?.Operation,
                saveFailure?.OccurredAt.ToUniversalTime()),
            RecoveryOffer: recoveryOffer,
            ShortcutNative: new DeskBoxShortcutNativeDiagnostic(
                shortcutNative.SelectedBackend,
                shortcutNative.ModuleName,
                shortcutNative.ModuleExists,
                shortcutNative.ModuleArchitecture,
                shortcutNative.ModuleSha256,
                shortcutNative.LoadAttempted,
                shortcutNative.LoadState,
                shortcutNative.AbiVersion,
                shortcutNative.Capabilities),
            RuntimeHealth: runtimeHealth,
            WidgetManager: widgetManager,
            Displays: displays,
            StorageMigration: WidgetManager?.StorageMigrationDiagnostic,
            StartupLedger: startupLedger,
            CallbackRadar: callbackRadar);
    }

    internal DeskBoxHotkeyDiagnostic BuildHotkeyDiagnostics()
    {
        GlobalHotkeyGesture toggleGesture = GlobalHotkeyService?.CurrentGesture ??
            GlobalHotkeyService.NormalizeGesture(
                SettingsService.Settings.GlobalHotkeyModifiers,
                SettingsService.Settings.GlobalHotkeyKey);
        GlobalHotkeyGesture searchGesture = SearchHotkeyService?.CurrentGesture ??
            GlobalHotkeyService.NormalizeGesture(
                SettingsService.Settings.SearchHotkeyModifiers,
                SettingsService.Settings.SearchHotkeyKey);
        GlobalHotkeyGesture clipboardGesture = ClipboardHotkeyService?.CurrentGesture ??
            GlobalHotkeyService.NormalizeActivation(
                SettingsService.Settings.Clipboard.ClipboardHotkeyActivationKind,
                SettingsService.Settings.Clipboard.ClipboardHotkeyModifiers,
                SettingsService.Settings.Clipboard.ClipboardHotkeyKey).Gesture;

        return new DeskBoxHotkeyDiagnostic(
            SettingsService.Settings.GlobalHotkeyEnabled,
            GlobalHotkeyService?.IsRegistered == true,
            (int)toggleGesture.Modifiers,
            toggleGesture.VirtualKey,
            GlobalHotkeyService?.ReceivedCount ?? 0,
            GlobalHotkeyService?.InvocationCount ?? 0,
            GlobalHotkeyService?.DispatchFailureCount ?? 0,
            GlobalHotkeyService?.UsesReservedHook == true,
            GlobalHotkeyService?.ReservedHookThreadId ?? 0,
            GlobalHotkeyService?.ReservedHookLastErrorCode ?? 0,
            GlobalHotkeyService?.ReservedHookTriggerCount ?? 0,
            GlobalHotkeyService?.ReservedHookPostFailureCount ?? 0,
            GlobalHotkeyService?.ReservedHookInputFailureCount ?? 0,
            !string.IsNullOrWhiteSpace(GlobalHotkeyService?.LastError),
            SettingsService.Settings.SearchHotkeyEnabled,
            SearchHotkeyService?.IsRegistered == true,
            (int)searchGesture.Modifiers,
            searchGesture.VirtualKey,
            ClipboardEnabled: SettingsService.Settings.Clipboard.ClipboardHotkeyEnabled,
            ClipboardRegistered: ClipboardHotkeyService?.IsRegistered == true,
            ClipboardModifiers: (int)clipboardGesture.Modifiers,
            ClipboardVirtualKey: clipboardGesture.VirtualKey,
            ClipboardReceivedCount: ClipboardHotkeyService?.ReceivedCount ?? 0,
            ClipboardInvocationCount: ClipboardHotkeyService?.InvocationCount ?? 0,
            ClipboardDispatchFailureCount: ClipboardHotkeyService?.DispatchFailureCount ?? 0,
            ClipboardUsesReservedHook: ClipboardHotkeyService?.UsesReservedHook == true,
            ClipboardRegistrationFailed: !string.IsNullOrWhiteSpace(ClipboardHotkeyService?.LastError));
    }

    private static IReadOnlyList<DeskBoxDisplayDiagnostic> GetDisplayDiagnostics()
    {
        try
        {
            return Win32Helper.GetMonitorWorkAreaInfos()
                .Select((display, index) => new DeskBoxDisplayDiagnostic(
                    index + 1,
                    display.IsPrimary,
                    display.DpiScale,
                    ToDiagnosticRect(display.Monitor),
                    ToDiagnosticRect(display.WorkArea)))
                .ToArray();
        }
        catch (Exception ex)
        {
            Log($"[DiagnosticsBundle] Display enumeration failed: {ex.Message}");
            return [];
        }
    }

    private static DeskBoxDiagnosticRect ToDiagnosticRect(Win32Helper.RECT bounds)
    {
        return new DeskBoxDiagnosticRect(
            bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top);
    }

    private static string GetDiagnosticVersion()
    {
        Assembly assembly = typeof(App).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                   ?.InformationalVersion
               ?? assembly.GetName().Version?.ToString()
               ?? "unknown";
    }
}
