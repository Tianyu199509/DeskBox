using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using DeskBox.Core.Persistence;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using Microsoft.Win32;

namespace DeskBox;

public partial class App
{
    private DeskBoxDiagnosticsBundleService? _diagnosticsBundleService;
    public DeskBoxDiagnosticsBundleService DiagnosticsBundleService =>
        _diagnosticsBundleService ??= Services.DiagnosticsBundleService;

    public DeskBoxDiagnosticSnapshot CreateDiagnosticSnapshot() =>
        CreateDiagnosticSnapshotCore(includeUiState: true);

    public async Task<DeskBoxDiagnosticSnapshot> CreateDiagnosticSnapshotSafeAsync(TimeSpan uiTimeout)
    {
        if (Platform.UiDispatch.HasAccess)
        {
            return CreateDiagnosticSnapshotCore(includeUiState: true);
        }

        try
        {
            return await Platform.UiDispatch
                .RunAsync(() => Task.FromResult(CreateDiagnosticSnapshotCore(includeUiState: true)))
                .WaitAsync(uiTimeout)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            Log($"[Diagnostics] UI thread did not respond within {uiTimeout.TotalMilliseconds:0}ms; capturing UI-free diagnostic snapshot.");
            return CreateDiagnosticSnapshotCore(includeUiState: false);
        }
    }

    private DeskBoxDiagnosticSnapshot CreateDiagnosticSnapshotCore(bool includeUiState)
    {
        AppRuntimeHealthSnapshot? runtimeHealth = DiagnosticsService?.GetRuntimeHealthSnapshot(
            EverythingSearchService,
            includeUiState ? WidgetManager?.GetFolderWatcherHealthSnapshots() : null);
        DeskBoxWidgetManagerDiagnostic widgetManager = includeUiState
            ? (WidgetManager?.CreateDiagnosticsSnapshot() ?? DeskBoxWidgetManagerDiagnostic.Empty)
            : DeskBoxWidgetManagerDiagnostic.Empty;
        IReadOnlyList<DeskBoxDisplayDiagnostic> displays = includeUiState
            ? GetDisplayDiagnostics()
            : [];
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
            SchemaVersion: 10,
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
            CallbackRadar: callbackRadar,
            RecentInteractionBreadcrumbs: Platform.InteractionBreadcrumbRecorder.Snapshot());
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

    private static string GetAppCompatReport()
    {
        string exePath = Path.Combine(AppContext.BaseDirectory, "DeskBox.exe");
        string? currentUser = GetAppCompatLayerValue(Registry.CurrentUser, exePath);
        string? localMachine = GetAppCompatLayerValue(Registry.LocalMachine, exePath);

        return $"exe='{exePath}' hkcu={(string.IsNullOrWhiteSpace(currentUser) ? "none" : currentUser)} " +
               $"hklm={(string.IsNullOrWhiteSpace(localMachine) ? "none" : localMachine)}";
    }

    private static string GetUacPolicyReport()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\System");
            object? enableLua = key?.GetValue("EnableLUA");
            object? consentPrompt = key?.GetValue("ConsentPromptBehaviorAdmin");
            object? promptOnSecureDesktop = key?.GetValue("PromptOnSecureDesktop");
            object? validateAdminCodeSignatures = key?.GetValue("ValidateAdminCodeSignatures");

            return $"EnableLUA={FormatRegistryValue(enableLua)} " +
                   $"ConsentPromptBehaviorAdmin={FormatRegistryValue(consentPrompt)} " +
                   $"PromptOnSecureDesktop={FormatRegistryValue(promptOnSecureDesktop)} " +
                   $"ValidateAdminCodeSignatures={FormatRegistryValue(validateAdminCodeSignatures)}";
        }
        catch (Exception ex)
        {
            return $"unknown error={ex.Message}";
        }
    }

    private static string FormatRegistryValue(object? value)
    {
        return value is null ? "missing" : value.ToString() ?? "unknown";
    }

    private static string? GetAppCompatLayerValue(RegistryKey? rootKey, string exePath)
    {
        if (rootKey is null)
        {
            return null;
        }

        try
        {
            using var key = rootKey.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
            if (key?.GetValue(exePath) is string value && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }
        catch (Exception ex)
        {
            return $"error:{ex.Message}";
        }

        return null;
    }
}
