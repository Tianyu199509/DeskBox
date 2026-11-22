using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.Services.Distribution;
using Microsoft.Win32.SafeHandles;

namespace DeskBox;

public partial class App
{
    private static SafeFileHandle? _primaryOwnerMapping;
    private static EventWaitHandle? _gracefulExitEvent;
    private static RegisteredWaitHandle? _gracefulExitRegistration;
    private ChannelMigrationOutcome _startupChannelMigrationOutcome = ChannelMigrationOutcome.None;

    /// <summary>
    /// Handles a single-instance mutex collision when the active primary
    /// process belongs to the other distribution channel (Direct vs. Microsoft
    /// Store). Returns <c>true</c> only when the user confirmed switching and
    /// this process successfully acquired primary mutex ownership after the
    /// previous edition exited cleanly.
    /// </summary>
    private bool TryHandleCrossChannelCollision(
        bool isUnattendedLaunch,
        out Mutex? acquiredMutex)
    {
        acquiredMutex = null;
        if (DeskBoxDataPathService.Current.IsDevelopmentRoot)
        {
            return false;
        }

        PrimaryInstanceOwnerInfo? owner = ChannelCoexistenceService.TryReadPrimaryInstanceOwner(
            DeskBoxDataPathService.Current,
            Log);
        if (owner is null)
        {
            return TryHandleLegacyOppositeChannelCollision(isUnattendedLaunch, out acquiredMutex);
        }

        if (owner.Channel == DistributionService.CurrentChannel)
        {
            return false;
        }

        if (isUnattendedLaunch)
        {
            Log(
                $"[ChannelHandoff] Unattended launch ({DistributionService.ChannelName}) deferred to running " +
                $"{owner.Channel} instance (pid={owner.ProcessId}).");
            _nativeNotificationService?.Dispose();
            DrainLogQueue();
            Environment.Exit(0);
        }

        string culture = LocalizationService.ResolvePersistedOrAmbientCultureName();
        bool isRtl = CustomWidgetLocaleCatalog.IsRightToLeftCulture(culture);
        string runningLabel = GetLocalizedChannelDisplayName(culture, owner.Channel);
        string currentLabel = GetLocalizedChannelDisplayName(culture, DistributionService.CurrentChannel);
        string title = LocalizationService.TranslateForCulture(culture, "ChannelCoexistence.SwitchPrompt.Title");
        string bodyTemplate = LocalizationService.TranslateForCulture(culture, "ChannelCoexistence.SwitchPrompt.Body");
        string body = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            bodyTemplate,
            runningLabel,
            currentLabel);

        bool shouldSwitch = SingleInstanceAndActivationInterop.ShowPreUiConfirmation(title, body, yesNo: true, isRtl: isRtl);
        if (!shouldSwitch)
        {
            Log($"[ChannelHandoff] User kept running {owner.Channel} instance (pid={owner.ProcessId}); exiting {DistributionService.ChannelName}.");
            _nativeNotificationService?.Dispose();
            DrainLogQueue();
            Environment.Exit(0);
        }

        (Mutex? handedOffMutex, bool acquired) =
            ChannelCoexistenceService.TrySignalGracefulExitAndAcquirePrimaryMutex(
                owner,
                DeskBoxDataPathService.Current.SingleInstanceMutexName,
                timeoutMs: 15000,
                Log);
        if (acquired && handedOffMutex is not null)
        {
            acquiredMutex = handedOffMutex;
            Log($"[ChannelHandoff] Primary ownership transferred from {owner.Channel} (pid={owner.ProcessId}) to {DistributionService.ChannelName}.");
            return true;
        }

        string timeoutBody = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            LocalizationService.TranslateForCulture(culture, "ChannelCoexistence.SwitchTimeout.Body"),
            runningLabel);
        SingleInstanceAndActivationInterop.ShowPreUiConfirmation(title, timeoutBody, yesNo: false, isRtl: isRtl);
        Log($"[ChannelHandoff] Timed out waiting for {owner.Channel} instance (pid={owner.ProcessId}) to release primary mutex.");
        _nativeNotificationService?.Dispose();
        DrainLogQueue();
        Environment.Exit(0);
        return false;
    }

    private bool TryHandleLegacyOppositeChannelCollision(
        bool isUnattendedLaunch,
        out Mutex? acquiredMutex)
    {
        acquiredMutex = null;
        LegacyRunningChannelProcess? legacy =
            ChannelCoexistenceService.TryDetectLegacyRunningOppositeChannelProcess(DistributionService.CurrentChannel);
        if (legacy is null)
        {
            return false;
        }

        if (isUnattendedLaunch)
        {
            Log(
                $"[ChannelHandoff] Unattended launch ({DistributionService.ChannelName}) deferred to running legacy " +
                $"{legacy.Channel} instance (pid={legacy.ProcessId}).");
            _nativeNotificationService?.Dispose();
            DrainLogQueue();
            Environment.Exit(0);
        }

        string culture = LocalizationService.ResolvePersistedOrAmbientCultureName();
        bool isRtl = CustomWidgetLocaleCatalog.IsRightToLeftCulture(culture);
        string title = LocalizationService.TranslateForCulture(culture, "ChannelCoexistence.LegacySwitchPrompt.Title");
        string bodyTemplate = LocalizationService.TranslateForCulture(culture, "ChannelCoexistence.LegacySwitchPrompt.Body");
        string currentLabel = GetLocalizedChannelDisplayName(culture, DistributionService.CurrentChannel);

        while (true)
        {
            string runningLabel = GetLocalizedChannelDisplayName(culture, legacy.Channel);
            string body = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                bodyTemplate,
                runningLabel,
                currentLabel);

            bool retry = SingleInstanceAndActivationInterop.ShowPreUiRetryPrompt(title, body, isRtl: isRtl);
            if (!retry)
            {
                Log($"[ChannelHandoff] User cancelled switching away from legacy {legacy.Channel} instance (pid={legacy.ProcessId}); exiting {DistributionService.ChannelName}.");
                _nativeNotificationService?.Dispose();
                DrainLogQueue();
                Environment.Exit(0);
            }

            (Mutex? retryMutex, bool acquired) = SingleInstanceAndActivationInterop.TryAcquireSingleInstanceMutexWithTimeout(
                DeskBoxDataPathService.Current.SingleInstanceMutexName,
                timeoutMs: 2500,
                Log);
            if (acquired && retryMutex is not null)
            {
                acquiredMutex = retryMutex;
                Log($"[ChannelHandoff] Acquired primary ownership after legacy {legacy.Channel} instance exited.");
                return true;
            }

            LegacyRunningChannelProcess? stillLegacy =
                ChannelCoexistenceService.TryDetectLegacyRunningOppositeChannelProcess(DistributionService.CurrentChannel);
            if (stillLegacy is null)
            {
                (Mutex? finalMutex, bool finalAcquired) = SingleInstanceAndActivationInterop.TryAcquireSingleInstanceMutexWithTimeout(
                    DeskBoxDataPathService.Current.SingleInstanceMutexName,
                    timeoutMs: 1000,
                    Log);
                if (finalAcquired && finalMutex is not null)
                {
                    acquiredMutex = finalMutex;
                    return true;
                }

                return false;
            }

            legacy = stillLegacy;
        }
    }

    private void InitializePrimaryChannelOwnershipAndMigration(bool isInteractiveLaunch)
    {
        DeskBoxDataPathService dataPaths = DeskBoxDataPathService.Current;
        int currentPid = Environment.ProcessId;
        string exePath = AppExecutablePath.ResolveMainExecutablePath() ?? string.Empty;

        _primaryOwnerMapping = ChannelCoexistenceService.PublishPrimaryInstanceOwner(
            dataPaths,
            DistributionService.CurrentChannel,
            currentPid,
            exePath,
            Log);

        string exitEventName = dataPaths.BuildGracefulExitEventName(currentPid);
        _gracefulExitEvent = SingleInstanceAndActivationInterop.CreateOrOpenActivationEvent(exitEventName, Log);
        if (_gracefulExitEvent is not null)
        {
            _gracefulExitRegistration = ThreadPool.RegisterWaitForSingleObject(
                _gracefulExitEvent,
                static (_, _) =>
                {
                    Log("[ChannelHandoff] Received cross-channel graceful-exit signal.");
                    var dispatcher = App.UiDispatcherQueue;
                    if (dispatcher is not null && dispatcher.TryEnqueue(() => Current.ExitApplication()))
                    {
                        return;
                    }

                    // If triggered before UI initialization finished, exit cleanly
                    // so the requesting edition can take over promptly.
                    DrainLogQueue();
                    Environment.Exit(0);
                },
                null,
                Timeout.Infinite,
                executeOnlyOnce: true);
        }

        _startupChannelMigrationOutcome = ChannelMigrationService.RunStartupChannelMigration(
            dataPaths,
            DistributionService.CurrentChannel,
            isInteractiveLaunch,
            log: Log);
    }

    private void ShowPostChannelMigrationNoticeIfNeeded()
    {
        if (IsStartupMode || IsShuttingDown)
        {
            return;
        }

        ChannelMigrationOutcome outcome = _startupChannelMigrationOutcome;
        if (outcome.Kind == ChannelMigrationOutcomeKind.SkippedNewerSchema)
        {
            if (outcome.NotifiedPreUi)
            {
                return;
            }

            AppDistributionChannel sourceChannel = DistributionService.CurrentChannel == AppDistributionChannel.MicrosoftStore
                ? AppDistributionChannel.Direct
                : AppDistributionChannel.MicrosoftStore;
            string sourceLabel = LocalizationService.T(
                sourceChannel == AppDistributionChannel.MicrosoftStore
                    ? "ChannelCoexistence.ChannelName.Store"
                    : "ChannelCoexistence.ChannelName.Direct");
            string skippedMessage = string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                LocalizationService.T("ChannelCoexistence.NewerSchemaSkipped.Body"),
                sourceLabel);
            DesktopToastHudService.Shared.TryShow(
                new DesktopToastRequest(
                    HintId: $"channel-migration-{outcome.Kind}",
                    Message: skippedMessage,
                    Severity: DesktopToastSeverity.Warning,
                    Priority: DesktopToastPriority.ContextualGuide,
                    DurationMs: 9000),
                SettingsService);
            return;
        }

        if (outcome.Kind is not (
            ChannelMigrationOutcomeKind.MigratedFromDirectToStoreInitial or
            ChannelMigrationOutcomeKind.MigratedFromDirectToStoreNewer or
            ChannelMigrationOutcomeKind.ImportedFromStoreToDirect))
        {
            return;
        }

        bool isDirectToStore = outcome.Kind is
            ChannelMigrationOutcomeKind.MigratedFromDirectToStoreInitial or
            ChannelMigrationOutcomeKind.MigratedFromDirectToStoreNewer;

        string messageKey = isDirectToStore
            ? (outcome.OtherChannelInstalled
                ? "ChannelCoexistence.PostMigration.DirectToStoreUninstallHint"
                : "ChannelCoexistence.PostMigration.DirectToStoreDone")
            : (outcome.OtherChannelInstalled
                ? "ChannelCoexistence.PostMigration.StoreToDirectUninstallHint"
                : "ChannelCoexistence.PostMigration.StoreToDirectDone");

        string? actionText = outcome.OtherChannelInstalled
            ? LocalizationService.T("ChannelCoexistence.Action.OpenInstalledApps")
            : (isDirectToStore
                ? LocalizationService.T("ChannelCoexistence.Action.OpenGeneralSettings")
                : null);

        Func<Task>? onAction = outcome.OtherChannelInstalled
            ? () =>
            {
                ChannelCoexistenceService.TryOpenWindowsInstalledAppsSettings(Log);
                return Task.CompletedTask;
            }
            : (isDirectToStore
                ? () =>
                {
                    UiDispatcherQueue?.TryEnqueue(() => ShowSettings("General"));
                    return Task.CompletedTask;
                }
                : null);

        DesktopToastHudService.Shared.TryShow(
            new DesktopToastRequest(
                HintId: $"channel-migration-{outcome.Kind}",
                Message: LocalizationService.T(messageKey),
                Severity: DesktopToastSeverity.Info,
                Priority: DesktopToastPriority.ContextualGuide,
                ActionButtonText: actionText,
                OnActionAsync: onAction,
                DurationMs: 9000),
            SettingsService);
    }

    private static void DisposeChannelCoexistenceResources()
    {
        _gracefulExitRegistration?.Unregister(null);
        _gracefulExitRegistration = null;
        _gracefulExitEvent?.Dispose();
        _gracefulExitEvent = null;
        _primaryOwnerMapping?.Dispose();
        _primaryOwnerMapping = null;
    }

    private static string GetLocalizedChannelDisplayName(string culture, AppDistributionChannel channel) =>
        LocalizationService.TranslateForCulture(
            culture,
            channel == AppDistributionChannel.MicrosoftStore
                ? "ChannelCoexistence.ChannelName.Store"
                : "ChannelCoexistence.ChannelName.Direct");
}
