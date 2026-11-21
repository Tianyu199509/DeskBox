#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private const string AotTodoNotificationActivationSmokeEnvironmentVariable =
        "DESKBOX_AOT_TODO_NOTIFICATION_ACTIVATION_SMOKE";
    private const string AotTodoNotificationActivationPhaseEnvironmentVariable =
        "DESKBOX_AOT_TODO_NOTIFICATION_ACTIVATION_PHASE";
    private const string AotTodoNotificationActivationRunIdEnvironmentVariable =
        "DESKBOX_AOT_TODO_NOTIFICATION_ACTIVATION_RUN_ID";
    private const string AotTodoNotificationActivationScenario =
        "DeterministicActionRouting";
    private const string AotTodoNotificationRouteAndPersistPhase = "RouteAndPersist";
    private const string AotTodoNotificationVerifyAndClearPhase = "VerifyAndClear";
    private const string AotTodoNotificationActivationPostflightPhase = "Postflight";
    private const string AotTodoNotificationActivationSmokeDirectoryName =
        "aot-todo-notification-activation-smoke";
    private const string AotTodoNotificationActivationFixtureDirectoryName =
        "aot-todo-notification-activation-fixture";
    private const string AotTodoNotificationActivationWidgetId = "aot-5b4c3b2a-todo";
    private const string AotTodoNotificationOpenItemId = "aot-c3b2a-open";
    private const string AotTodoNotificationCompleteItemId = "aot-c3b2a-complete";
    private const string AotTodoNotificationSnooze10ItemId = "aot-c3b2a-snooze-10m";
    private const string AotTodoNotificationSnooze30ItemId = "aot-c3b2a-snooze-30m";
    private const string AotTodoNotificationSnooze1HourItemId = "aot-c3b2a-snooze-1h";
    private const string AotTodoNotificationSnoozeTomorrowItemId = "aot-c3b2a-snooze-tomorrow";
    private const string AotTodoNotificationLegacySnoozeItemId = "aot-c3b2a-legacy-snooze-10m";

    private static readonly DateTimeOffset AotTodoNotificationActivationClock =
        new(2026, 8, 25, 8, 15, 0, TimeSpan.FromHours(8));
    private static readonly TimeZoneInfo AotTodoNotificationActivationTimeZone =
        TimeZoneInfo.CreateCustomTimeZone(
            "DeskBox.Aot.UTC+08",
            TimeSpan.FromHours(8),
            "DeskBox AOT UTC+08",
            "DeskBox AOT UTC+08");
    private static readonly IReadOnlyDictionary<string, string>
        AotTodoNotificationActivationEmptyUserInput =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    private void StartAotTodoNotificationActivationSmokeIfRequested()
    {
        string? scenario = Environment.GetEnvironmentVariable(
            AotTodoNotificationActivationSmokeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(scenario))
        {
            return;
        }

        string? phase = Environment.GetEnvironmentVariable(
            AotTodoNotificationActivationPhaseEnvironmentVariable);
        string? runId = Environment.GetEnvironmentVariable(
            AotTodoNotificationActivationRunIdEnvironmentVariable);
        if (!string.Equals(
                scenario.Trim(),
                AotTodoNotificationActivationScenario,
                StringComparison.Ordinal) ||
            !IsAotTodoNotificationActivationPhase(phase) ||
            !Guid.TryParseExact(runId, "N", out _))
        {
            Log(
                $"[AotTodoNotificationActivationSmoke] Refused unsupported request " +
                $"scenario='{scenario}' phase='{phase}' runId='{runId}'.");
            return;
        }

        _ = RunAotTodoNotificationActivationSmokeAsync(phase!, runId!);
    }

    private async Task RunAotTodoNotificationActivationSmokeAsync(
        string phase,
        string runId)
    {
        await Task.Yield();

        DeskBoxDataPathService dataPaths = DeskBoxDataPathService.Current;
        string? configuredPreviewRoot = Environment.GetEnvironmentVariable(
            DeskBoxDataPathService.AotPreviewRootEnvironmentVariable);
        if (!dataPaths.IsDevelopmentRoot ||
            string.IsNullOrWhiteSpace(configuredPreviewRoot) ||
            !AotTodoNotificationActivationPathsEqual(
                dataPaths.RootPath,
                configuredPreviewRoot))
        {
            Log(
                "[AotTodoNotificationActivationSmoke] RefusedNonPreviewRoot: " +
                "the action matrix requires an explicit isolated Native AOT preview root.");
            return;
        }

        string smokeRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotTodoNotificationActivationSmokeDirectoryName));
        string phaseRoot = Path.GetFullPath(Path.Combine(
            smokeRoot,
            phase.ToLowerInvariant()));
        string fixtureRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotTodoNotificationActivationFixtureDirectoryName));
        if (!AotTodoNotificationActivationIsPathEqualOrInside(
                dataPaths.RootPath,
                smokeRoot) ||
            !AotTodoNotificationActivationIsPathEqualOrInside(
                smokeRoot,
                phaseRoot) ||
            !AotTodoNotificationActivationIsPathEqualOrInside(
                dataPaths.RootPath,
                fixtureRoot))
        {
            Log(
                $"[AotTodoNotificationActivationSmoke] Refused unsafe fixture or " +
                $"result root '{fixtureRoot}' / '{phaseRoot}'.");
            return;
        }

        Directory.CreateDirectory(phaseRoot);
        Directory.CreateDirectory(fixtureRoot);
        string resultPath = Path.Combine(phaseRoot, "result.json");
        var result = new AotTodoNotificationActivationSmokeResult
        {
            SchemaVersion = 1,
            Stage = "5B-4C3B2A",
            Scenario = AotTodoNotificationActivationScenario,
            Phase = phase,
            RunId = runId,
            State = "Running",
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            ExecutablePath = Environment.ProcessPath ?? string.Empty,
            PreviewDataRoot = dataPaths.RootPath,
            FixtureRoot = fixtureRoot,
            ResultPath = resultPath,
            FixedClock = AotTodoNotificationActivationClock,
            TimeZoneId = AotTodoNotificationActivationTimeZone.Id,
            IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            SystemNotificationAttempted = false,
            ExternalActivationAttempted = false,
            Steps = [],
            Routes = []
        };
        WriteAotTodoNotificationActivationResult(resultPath, result);

        try
        {
            await CaptureAotTodoNotificationActivationMatrixAsync(result);
            result.ExecutableSha256 =
                ComputeAotTodoNotificationActivationSha256(result.ExecutablePath);
            RequireAotTodoNotificationActivation(
                result,
                !result.IsDynamicCodeSupported,
                "runtime-native-aot",
                "Todo notification action routing did not run inside Native AOT.");
            RequireAotTodoNotificationActivation(
                result,
                !result.SystemNotificationAttempted &&
                !result.ExternalActivationAttempted,
                "no-system-notification-or-external-activation",
                "The deterministic action matrix entered an external notification path.");
            result.Success = true;
            result.State = "Completed";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.State = "Failed";
            result.Error = ex.ToString();
            Log(
                $"[AotTodoNotificationActivationSmoke] Phase {phase} failed: {ex}");
        }
        finally
        {
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            result.NormalShutdownRequested = true;
            WriteAotTodoNotificationActivationResult(resultPath, result);
            Log(
                $"[AotTodoNotificationActivationSmoke] phase={phase} " +
                $"state={result.State} success={result.Success} result='{resultPath}'");
            await Task.Delay(100);
            await ShutdownApplicationAsync();
        }
    }

    private async Task CaptureAotTodoNotificationActivationMatrixAsync(
        AotTodoNotificationActivationSmokeResult result)
    {
        string settingsRoot = Path.Combine(result.FixtureRoot, "settings");
        string widgetsRoot = Path.Combine(result.FixtureRoot, "widgets");
        Directory.CreateDirectory(settingsRoot);
        Directory.CreateDirectory(widgetsRoot);

        var settingsService = new SettingsService(settingsRoot);
        await settingsService.LoadAsync();
        ConfigureAotTodoNotificationActivationSettings(settingsService.Settings);
        await settingsService.SaveAsync(notifySubscribers: false);
        RequireAotTodoNotificationActivation(
            result,
            settingsService.LastPersistenceFailure is null &&
            settingsService.Settings.TodoReminderEnabled &&
            FeatureWidgetSettings.IsEnabled(
                settingsService.Settings,
                WidgetKind.Todo),
            "fixture-settings-configured",
            "The isolated Todo activation settings were not persisted.");

        var store = new TodoWidgetStore(
            widgetsRoot,
            AotTodoNotificationActivationWidgetId);
        using var reminderService = new TodoReminderService(
            settingsService,
            new LocalizationService(settingsService),
            dispatcherQueue: null,
            _ => { },
            widgetId => new TodoWidgetStore(widgetsRoot, widgetId),
            () => AotTodoNotificationActivationClock);

        result.Before = await CaptureAotTodoNotificationActivationStateAsync(store);
        switch (result.Phase)
        {
            case AotTodoNotificationRouteAndPersistPhase:
                await CaptureAotTodoNotificationRouteAndPersistAsync(
                    result,
                    store,
                    reminderService);
                break;
            case AotTodoNotificationVerifyAndClearPhase:
                await CaptureAotTodoNotificationVerifyAndClearAsync(
                    result,
                    store,
                    reminderService);
                break;
            case AotTodoNotificationActivationPostflightPhase:
                RequireAotTodoNotificationActivation(
                    result,
                    result.Before.Items.Count == 0 &&
                    result.Before.StoreFileExists,
                    "cleared-store-reloaded",
                    "The postflight process did not reload the cleared Todo store.");
                result.After =
                    await CaptureAotTodoNotificationActivationStateAsync(store);
                RequireAotTodoNotificationActivation(
                    result,
                    result.After.Items.Count == 0 &&
                    result.After.StoreSha256 == result.Before.StoreSha256,
                    "postflight-empty-and-stable",
                    "The postflight process changed the empty Todo store.");
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported Todo notification activation phase '{result.Phase}'.");
        }

        result.After ??=
            await CaptureAotTodoNotificationActivationStateAsync(store);
    }

    private async Task CaptureAotTodoNotificationRouteAndPersistAsync(
        AotTodoNotificationActivationSmokeResult result,
        TodoWidgetStore store,
        TodoReminderService reminderService)
    {
        RequireAotTodoNotificationActivation(
            result,
            result.Before.Items.Count == 0 &&
            !result.Before.StoreFileExists,
            "route-baseline-empty",
            "The first action-routing phase did not start from an empty store.");

        await store.SaveAsync(CreateAotTodoNotificationActivationSeedData());
        AotTodoNotificationActivationStateEvidence seeded =
            await CaptureAotTodoNotificationActivationStateAsync(store);
        RequireAotTodoNotificationActivation(
            result,
            seeded.Items.Count == 7,
            "activation-seed-persisted",
            "The action-routing seed did not contain seven owned items.");

        AotTodoNotificationActivationRouteEvidence semicolonOpen =
            await ExecuteAotTodoNotificationActivationRouteAsync(
                result,
                store,
                reminderService,
                "semicolon-body-open",
                $"itemId={AotTodoNotificationOpenItemId};source=todoReminder;" +
                $"view=today;widgetId={AotTodoNotificationActivationWidgetId}",
                AotTodoNotificationActivationEmptyUserInput);
        RequireAotTodoNotificationActivation(
            result,
            semicolonOpen.Disposition ==
                TodoNotificationActivationRouter.DispositionOpened &&
            semicolonOpen.Succeeded &&
            semicolonOpen.TargetCallbacks.Count == 1 &&
            semicolonOpen.TargetCallbacks[0].PreferTodayFilter &&
            semicolonOpen.BeforeStoreSha256 == semicolonOpen.AfterStoreSha256 &&
            semicolonOpen.ParsedArguments.Count == 4,
            "semicolon-body-open-routed",
            "The real semicolon body payload did not route to the exact Today target.");

        AotTodoNotificationActivationRouteEvidence ampersandOpen =
            await ExecuteAotTodoNotificationActivationRouteAsync(
                result,
                store,
                reminderService,
                "ampersand-body-open",
                $"source=todoReminder&widgetId={AotTodoNotificationActivationWidgetId}" +
                $"&itemId={AotTodoNotificationOpenItemId}&view=all",
                AotTodoNotificationActivationEmptyUserInput);
        RequireAotTodoNotificationActivation(
            result,
            ampersandOpen.Disposition ==
                TodoNotificationActivationRouter.DispositionOpened &&
            ampersandOpen.TargetCallbacks.Count == 1 &&
            !ampersandOpen.TargetCallbacks[0].PreferTodayFilter &&
            ampersandOpen.BeforeStoreSha256 == ampersandOpen.AfterStoreSha256,
            "ampersand-grammar-compatible",
            "The legacy ampersand grammar no longer routes the Todo body.");

        string completeArguments =
            $"action=complete;itemId={AotTodoNotificationCompleteItemId};" +
            $"source=todoReminder;widgetId={AotTodoNotificationActivationWidgetId}";
        AotTodoNotificationActivationRouteEvidence completed =
            await ExecuteAotTodoNotificationActivationRouteAsync(
                result,
                store,
                reminderService,
                "complete",
                completeArguments,
                AotTodoNotificationActivationEmptyUserInput);
        AotTodoNotificationActivationStateEvidence completedState =
            await CaptureAotTodoNotificationActivationStateAsync(store);
        AotTodoNotificationActivationItemEvidence completedItem =
            FindAotTodoNotificationActivationItem(
                completedState,
                AotTodoNotificationCompleteItemId);
        RequireAotTodoNotificationActivation(
            result,
            completed.Disposition ==
                TodoNotificationActivationRouter.DispositionCompleted &&
            completed.Succeeded &&
            completed.RefreshCallbacks.Count == 1 &&
            completedItem.IsCompleted &&
            completedItem.CompletedAt ==
                AotTodoNotificationActivationClock.ToUniversalTime(),
            "complete-action-persisted",
            "The Complete action did not persist the exact fixed-clock state.");
        AotTodoNotificationActivationRouteEvidence completedAgain =
            await ExecuteAotTodoNotificationActivationRouteAsync(
                result,
                store,
                reminderService,
                "complete-repeat",
                completeArguments,
                AotTodoNotificationActivationEmptyUserInput);
        RequireAotTodoNotificationActivation(
            result,
            completedAgain.Succeeded &&
            completedAgain.BeforeStoreSha256 == completedAgain.AfterStoreSha256,
            "complete-action-idempotent",
            "Repeating Complete changed the already-completed store.");

        await CaptureAotTodoNotificationSnoozeRouteAsync(
            result,
            store,
            reminderService,
            AotTodoNotificationSnooze10ItemId,
            TodoNotificationActivationRouter.Snooze10Minutes,
            AotTodoNotificationActivationClock.AddMinutes(10),
            "snooze-10m-persisted-and-idempotent");
        await CaptureAotTodoNotificationSnoozeRouteAsync(
            result,
            store,
            reminderService,
            AotTodoNotificationSnooze30ItemId,
            TodoNotificationActivationRouter.Snooze30Minutes,
            AotTodoNotificationActivationClock.AddMinutes(30),
            "snooze-30m-persisted-and-idempotent");
        await CaptureAotTodoNotificationSnoozeRouteAsync(
            result,
            store,
            reminderService,
            AotTodoNotificationSnooze1HourItemId,
            TodoNotificationActivationRouter.Snooze1Hour,
            AotTodoNotificationActivationClock.AddHours(1),
            "snooze-1h-persisted-and-idempotent");
        await CaptureAotTodoNotificationSnoozeRouteAsync(
            result,
            store,
            reminderService,
            AotTodoNotificationSnoozeTomorrowItemId,
            TodoNotificationActivationRouter.SnoozeTomorrow,
            new DateTimeOffset(
                2026,
                8,
                26,
                9,
                0,
                0,
                TimeSpan.FromHours(8)),
            "snooze-tomorrow-persisted-and-idempotent");

        AotTodoNotificationActivationRouteEvidence legacy =
            await ExecuteAotTodoNotificationActivationRouteAsync(
                result,
                store,
                reminderService,
                "legacy-ampersand-snooze10",
                $"source=todoReminder&action=snooze10&" +
                $"widgetId={AotTodoNotificationActivationWidgetId}&" +
                $"itemId={AotTodoNotificationLegacySnoozeItemId}",
                AotTodoNotificationActivationEmptyUserInput);
        RequireAotTodoNotificationActivation(
            result,
            legacy.Disposition ==
                TodoNotificationActivationRouter.DispositionSnoozed &&
            legacy.SnoozeSelection ==
                TodoNotificationActivationRouter.Snooze10Minutes &&
            legacy.SnoozedUntil ==
                AotTodoNotificationActivationClock.AddMinutes(10),
            "legacy-snooze10-compatible",
            "The legacy snooze10 action no longer maps to ten minutes.");

        string stableHashBeforeInvalid =
            (await CaptureAotTodoNotificationActivationStateAsync(store)).StoreSha256;
        await CaptureRejectedAotTodoNotificationActivationRouteAsync(
            result,
            store,
            reminderService,
            "missing-selection",
            $"source=todoReminder;action=snooze;" +
            $"widgetId={AotTodoNotificationActivationWidgetId};" +
            $"itemId={AotTodoNotificationOpenItemId}",
            AotTodoNotificationActivationEmptyUserInput,
            TodoNotificationActivationRouter.DispositionRejectedUnsupportedSnooze);
        await CaptureRejectedAotTodoNotificationActivationRouteAsync(
            result,
            store,
            reminderService,
            "unsupported-selection",
            $"source=todoReminder;action=snooze;" +
            $"widgetId={AotTodoNotificationActivationWidgetId};" +
            $"itemId={AotTodoNotificationOpenItemId}",
            new Dictionary<string, string>
            {
                [TodoNotificationActivationRouter.SnoozeInputId] = "next-week"
            },
            TodoNotificationActivationRouter.DispositionRejectedUnsupportedSnooze);
        await CaptureRejectedAotTodoNotificationActivationRouteAsync(
            result,
            store,
            reminderService,
            "unsupported-action",
            $"source=todoReminder;action=delete;" +
            $"widgetId={AotTodoNotificationActivationWidgetId};" +
            $"itemId={AotTodoNotificationOpenItemId}",
            AotTodoNotificationActivationEmptyUserInput,
            TodoNotificationActivationRouter.DispositionRejectedUnsupportedAction);
        await CaptureRejectedAotTodoNotificationActivationRouteAsync(
            result,
            store,
            reminderService,
            "missing-target",
            $"source=todoReminder;action=complete;" +
            $"widgetId={AotTodoNotificationActivationWidgetId}",
            AotTodoNotificationActivationEmptyUserInput,
            TodoNotificationActivationRouter.DispositionRejectedMissingTarget);
        await CaptureRejectedAotTodoNotificationActivationRouteAsync(
            result,
            store,
            reminderService,
            "non-todo-source",
            "source=desktop-organization;action=undo;historyId=owned",
            AotTodoNotificationActivationEmptyUserInput,
            TodoNotificationActivationRouter.DispositionNotTodoReminder);
        RequireAotTodoNotificationActivation(
            result,
            (await CaptureAotTodoNotificationActivationStateAsync(store)).StoreSha256 ==
                stableHashBeforeInvalid,
            "invalid-inputs-rejected-without-mutation",
            "A rejected activation changed the Todo store.");

        result.After = await CaptureAotTodoNotificationActivationStateAsync(store);
        RequireAotTodoNotificationActivation(
            result,
            result.After.Items.Count == 7 &&
            result.Routes.Count == 18,
            "route-matrix-complete",
            "The first phase did not capture all eighteen deterministic routes.");
    }

    private async Task CaptureAotTodoNotificationVerifyAndClearAsync(
        AotTodoNotificationActivationSmokeResult result,
        TodoWidgetStore store,
        TodoReminderService reminderService)
    {
        RequireAotTodoNotificationActivation(
            result,
            result.Before.Items.Count == 7 &&
            FindAotTodoNotificationActivationItem(
                result.Before,
                AotTodoNotificationCompleteItemId).IsCompleted &&
            HasAotTodoNotificationSnooze(
                result.Before,
                AotTodoNotificationSnooze10ItemId,
                AotTodoNotificationActivationClock.AddMinutes(10)) &&
            HasAotTodoNotificationSnooze(
                result.Before,
                AotTodoNotificationSnooze30ItemId,
                AotTodoNotificationActivationClock.AddMinutes(30)) &&
            HasAotTodoNotificationSnooze(
                result.Before,
                AotTodoNotificationSnooze1HourItemId,
                AotTodoNotificationActivationClock.AddHours(1)) &&
            HasAotTodoNotificationSnooze(
                result.Before,
                AotTodoNotificationSnoozeTomorrowItemId,
                new DateTimeOffset(
                    2026,
                    8,
                    26,
                    9,
                    0,
                    0,
                    TimeSpan.FromHours(8))),
            "cross-process-action-state-reloaded",
            "The second process did not reload the persisted action matrix.");

        AotTodoNotificationActivationRouteEvidence reopened =
            await ExecuteAotTodoNotificationActivationRouteAsync(
                result,
                store,
                reminderService,
                "restart-ampersand-open",
                $"source=todoReminder&widgetId={AotTodoNotificationActivationWidgetId}" +
                $"&itemId={AotTodoNotificationOpenItemId}&view=today",
                AotTodoNotificationActivationEmptyUserInput);
        RequireAotTodoNotificationActivation(
            result,
            reopened.Succeeded &&
            reopened.TargetCallbacks.Count == 1 &&
            reopened.BeforeStoreSha256 == reopened.AfterStoreSha256,
            "restart-open-routed-without-mutation",
            "The second process did not route the body without changing state.");

        await CaptureRejectedAotTodoNotificationActivationRouteAsync(
            result,
            store,
            reminderService,
            "restart-invalid-selection",
            $"source=todoReminder;action=snooze;" +
            $"widgetId={AotTodoNotificationActivationWidgetId};" +
            $"itemId={AotTodoNotificationOpenItemId}",
            new Dictionary<string, string>
            {
                [TodoNotificationActivationRouter.SnoozeInputId] = "invalid"
            },
            TodoNotificationActivationRouter.DispositionRejectedUnsupportedSnooze);
        RequireAotTodoNotificationActivation(
            result,
            result.Routes.Count == 2,
            "restart-rejection-stable",
            "The restart phase did not retain the expected route count.");

        await store.ClearAsync();
        result.After = await CaptureAotTodoNotificationActivationStateAsync(store);
        RequireAotTodoNotificationActivation(
            result,
            result.After.Items.Count == 0 &&
            result.After.StoreFileExists,
            "activation-store-cleared",
            "The second process did not clear the owned Todo store.");
    }

    private async Task CaptureAotTodoNotificationSnoozeRouteAsync(
        AotTodoNotificationActivationSmokeResult result,
        TodoWidgetStore store,
        TodoReminderService reminderService,
        string itemId,
        string selection,
        DateTimeOffset expectedUntil,
        string step)
    {
        string rawArguments =
            $"action=snooze;itemId={itemId};source=todoReminder;" +
            $"widgetId={AotTodoNotificationActivationWidgetId}";
        var userInput = new Dictionary<string, string>
        {
            [TodoNotificationActivationRouter.SnoozeInputId] = selection
        };
        AotTodoNotificationActivationRouteEvidence first =
            await ExecuteAotTodoNotificationActivationRouteAsync(
                result,
                store,
                reminderService,
                $"snooze-{selection}",
                rawArguments,
                userInput);
        AotTodoNotificationActivationRouteEvidence repeated =
            await ExecuteAotTodoNotificationActivationRouteAsync(
                result,
                store,
                reminderService,
                $"snooze-{selection}-repeat",
                rawArguments,
                userInput);
        AotTodoNotificationActivationStateEvidence state =
            await CaptureAotTodoNotificationActivationStateAsync(store);
        AotTodoNotificationActivationItemEvidence item =
            FindAotTodoNotificationActivationItem(state, itemId);
        RequireAotTodoNotificationActivation(
            result,
            first.Disposition ==
                TodoNotificationActivationRouter.DispositionSnoozed &&
            first.Succeeded &&
            first.SnoozeSelection == selection &&
            first.SnoozedUntil == expectedUntil &&
            first.RefreshCallbacks.Count == 1 &&
            first.ConfirmationCallbacks.SequenceEqual([selection]) &&
            item.SnoozedUntil == expectedUntil &&
            item.ReminderDismissedForDueDate == item.DueDate &&
            repeated.BeforeStoreSha256 == repeated.AfterStoreSha256,
            step,
            $"The {selection} snooze action was not exact or idempotent.");
    }

    private async Task CaptureRejectedAotTodoNotificationActivationRouteAsync(
        AotTodoNotificationActivationSmokeResult result,
        TodoWidgetStore store,
        TodoReminderService reminderService,
        string name,
        string rawArguments,
        IReadOnlyDictionary<string, string> userInput,
        string expectedDisposition)
    {
        AotTodoNotificationActivationRouteEvidence route =
            await ExecuteAotTodoNotificationActivationRouteAsync(
                result,
                store,
                reminderService,
                name,
                rawArguments,
                userInput);
        if (route.Disposition != expectedDisposition ||
            route.Succeeded ||
            route.TargetCallbacks.Count != 0 ||
            route.RefreshCallbacks.Count != 0 ||
            route.ConfirmationCallbacks.Count != 0 ||
            route.BeforeStoreSha256 != route.AfterStoreSha256)
        {
            throw new InvalidOperationException(
                $"Rejected route '{name}' did not remain side-effect free.");
        }
    }

    private async Task<AotTodoNotificationActivationRouteEvidence>
        ExecuteAotTodoNotificationActivationRouteAsync(
            AotTodoNotificationActivationSmokeResult smokeResult,
            TodoWidgetStore store,
            TodoReminderService reminderService,
            string name,
            string rawArguments,
            IReadOnlyDictionary<string, string> userInput)
    {
        AotTodoNotificationActivationStateEvidence before =
            await CaptureAotTodoNotificationActivationStateAsync(store);
        Dictionary<string, string> parsed = ParseNotificationArguments(rawArguments);
        var targets = new List<AotTodoNotificationActivationTargetEvidence>();
        var refreshes = new List<string>();
        var confirmations = new List<string>();
        TodoNotificationActivationRouteResult routeResult =
            await TodoNotificationActivationRouter.RouteAsync(
                parsed,
                userInput,
                reminderService,
                () => AotTodoNotificationActivationClock,
                AotTodoNotificationActivationTimeZone,
                (widgetId, itemId, preferTodayFilter) =>
                {
                    targets.Add(new AotTodoNotificationActivationTargetEvidence
                    {
                        WidgetId = widgetId,
                        ItemId = itemId,
                        PreferTodayFilter = preferTodayFilter
                    });
                    return Task.FromResult(true);
                },
                widgetId =>
                {
                    refreshes.Add(widgetId ?? string.Empty);
                    return Task.FromResult(true);
                },
                selection =>
                {
                    confirmations.Add(selection);
                    return Task.CompletedTask;
                });
        AotTodoNotificationActivationStateEvidence after =
            await CaptureAotTodoNotificationActivationStateAsync(store);
        var evidence = new AotTodoNotificationActivationRouteEvidence
        {
            Name = name,
            RawArguments = rawArguments,
            ParsedArguments = parsed,
            UserInput = userInput.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.OrdinalIgnoreCase),
            Disposition = routeResult.Disposition,
            Succeeded = routeResult.Succeeded,
            WidgetId = routeResult.WidgetId,
            ItemId = routeResult.ItemId,
            Action = routeResult.Action,
            SnoozeSelection = routeResult.SnoozeSelection,
            SnoozedUntil = routeResult.SnoozedUntil,
            TargetRequested = routeResult.TargetRequested,
            RefreshRequested = routeResult.RefreshRequested,
            ConfirmationRequested = routeResult.ConfirmationRequested,
            TargetCallbacks = targets,
            RefreshCallbacks = refreshes,
            ConfirmationCallbacks = confirmations,
            BeforeStoreSha256 = before.StoreSha256,
            AfterStoreSha256 = after.StoreSha256
        };
        smokeResult.Routes.Add(evidence);
        return evidence;
    }

    private static TodoWidgetData CreateAotTodoNotificationActivationSeedData()
    {
        string[] itemIds =
        [
            AotTodoNotificationOpenItemId,
            AotTodoNotificationCompleteItemId,
            AotTodoNotificationSnooze10ItemId,
            AotTodoNotificationSnooze30ItemId,
            AotTodoNotificationSnooze1HourItemId,
            AotTodoNotificationSnoozeTomorrowItemId,
            AotTodoNotificationLegacySnoozeItemId
        ];
        return new TodoWidgetData
        {
            Version = 3,
            Items = itemIds.Select((itemId, index) => new TodoItem
            {
                Id = itemId,
                Text = itemId,
                DueDate = AotTodoNotificationActivationClock.AddHours(2),
                ReminderOffsetMinutes = 5,
                CreatedAt = AotTodoNotificationActivationClock.AddDays(-1),
                UpdatedAt = AotTodoNotificationActivationClock.AddDays(-1),
                SortOrder = index
            }).ToList()
        };
    }

    private static void ConfigureAotTodoNotificationActivationSettings(
        AppSettings settings)
    {
        settings.TodoReminderEnabled = true;
        settings.TodoDefaultReminderOffsetMinutes = 5;
        settings.DeletedWidgetIds = [];
        settings.Widgets =
        [
            new WidgetConfig
            {
                Id = AotTodoNotificationActivationWidgetId,
                Name = "DeskBox Todo activation AOT fixture",
                WidgetKind = WidgetKind.Todo,
                IsDisabled = false
            }
        ];
        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.Todo, true);
    }

    private static async Task<AotTodoNotificationActivationStateEvidence>
        CaptureAotTodoNotificationActivationStateAsync(TodoWidgetStore store)
    {
        TodoWidgetData data = await store.LoadAsync();
        bool storeExists = File.Exists(store.StorePath);
        return new AotTodoNotificationActivationStateEvidence
        {
            StorePath = store.StorePath,
            StoreFileExists = storeExists,
            StoreLength = storeExists ? new FileInfo(store.StorePath).Length : 0,
            StoreSha256 = storeExists
                ? Convert.ToHexString(SHA256.HashData(
                    await File.ReadAllBytesAsync(store.StorePath)))
                : string.Empty,
            Items = data.Items.Select(item =>
                new AotTodoNotificationActivationItemEvidence
                {
                    Id = item.Id,
                    DueDate = item.DueDate,
                    IsCompleted = item.IsCompleted,
                    CompletedAt = item.CompletedAt,
                    UpdatedAt = item.UpdatedAt,
                    ReminderDismissedForDueDate = item.ReminderDismissedForDueDate,
                    SnoozedUntil = item.SnoozedUntil
                }).ToList()
        };
    }

    private static AotTodoNotificationActivationItemEvidence
        FindAotTodoNotificationActivationItem(
            AotTodoNotificationActivationStateEvidence state,
            string itemId)
    {
        return state.Items.Single(item =>
            string.Equals(item.Id, itemId, StringComparison.Ordinal));
    }

    private static bool HasAotTodoNotificationSnooze(
        AotTodoNotificationActivationStateEvidence state,
        string itemId,
        DateTimeOffset expectedUntil)
    {
        AotTodoNotificationActivationItemEvidence item =
            FindAotTodoNotificationActivationItem(state, itemId);
        return item.SnoozedUntil == expectedUntil &&
               item.ReminderDismissedForDueDate == item.DueDate;
    }

    private static bool IsAotTodoNotificationActivationPhase(string? phase)
    {
        return phase is AotTodoNotificationRouteAndPersistPhase or
            AotTodoNotificationVerifyAndClearPhase or
            AotTodoNotificationActivationPostflightPhase;
    }

    private static bool AotTodoNotificationActivationPathsEqual(
        string left,
        string right)
    {
        return string.Equals(
            Path.GetFullPath(left).TrimEnd('\\', '/'),
            Path.GetFullPath(right).TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool AotTodoNotificationActivationIsPathEqualOrInside(
        string root,
        string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd('\\', '/');
        string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd('\\', '/');
        return AotTodoNotificationActivationPathsEqual(
                   normalizedRoot,
                   normalizedCandidate) ||
               normalizedCandidate.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeAotTodoNotificationActivationSha256(string path)
    {
        return string.IsNullOrWhiteSpace(path) || !File.Exists(path)
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    private static void RequireAotTodoNotificationActivation(
        AotTodoNotificationActivationSmokeResult result,
        bool condition,
        string step,
        string error)
    {
        if (!condition)
        {
            throw new InvalidOperationException(error);
        }

        result.Steps.Add(step);
    }

    private static void WriteAotTodoNotificationActivationResult(
        string resultPath,
        AotTodoNotificationActivationSmokeResult result)
    {
        string json = JsonSerializer.Serialize(
            result,
            AotTodoNotificationActivationJsonContext.Default.SmokeResult);
        string tempPath = $"{resultPath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, resultPath, overwrite: true);
    }
}

internal sealed class AotTodoNotificationActivationSmokeResult
{
    public int SchemaVersion { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int ProcessId { get; set; }
    public string ExecutablePath { get; set; } = string.Empty;
    public string ExecutableSha256 { get; set; } = string.Empty;
    public string PreviewDataRoot { get; set; } = string.Empty;
    public string FixtureRoot { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public DateTimeOffset FixedClock { get; set; }
    public string TimeZoneId { get; set; } = string.Empty;
    public bool IsDynamicCodeSupported { get; set; }
    public bool SystemNotificationAttempted { get; set; }
    public bool ExternalActivationAttempted { get; set; }
    public bool NormalShutdownRequested { get; set; }
    public AotTodoNotificationActivationStateEvidence Before { get; set; } = new();
    public AotTodoNotificationActivationStateEvidence? After { get; set; }
    public List<AotTodoNotificationActivationRouteEvidence> Routes { get; set; } = [];
    public List<string> Steps { get; set; } = [];
}

internal sealed class AotTodoNotificationActivationStateEvidence
{
    public string StorePath { get; set; } = string.Empty;
    public bool StoreFileExists { get; set; }
    public long StoreLength { get; set; }
    public string StoreSha256 { get; set; } = string.Empty;
    public List<AotTodoNotificationActivationItemEvidence> Items { get; set; } = [];
}

internal sealed class AotTodoNotificationActivationItemEvidence
{
    public string Id { get; set; } = string.Empty;
    public DateTimeOffset? DueDate { get; set; }
    public bool IsCompleted { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ReminderDismissedForDueDate { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
}

internal sealed class AotTodoNotificationActivationRouteEvidence
{
    public string Name { get; set; } = string.Empty;
    public string RawArguments { get; set; } = string.Empty;
    public Dictionary<string, string> ParsedArguments { get; set; } = [];
    public Dictionary<string, string> UserInput { get; set; } = [];
    public string Disposition { get; set; } = string.Empty;
    public bool Succeeded { get; set; }
    public string? WidgetId { get; set; }
    public string? ItemId { get; set; }
    public string? Action { get; set; }
    public string? SnoozeSelection { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
    public bool TargetRequested { get; set; }
    public bool RefreshRequested { get; set; }
    public bool ConfirmationRequested { get; set; }
    public List<AotTodoNotificationActivationTargetEvidence> TargetCallbacks { get; set; } = [];
    public List<string> RefreshCallbacks { get; set; } = [];
    public List<string> ConfirmationCallbacks { get; set; } = [];
    public string BeforeStoreSha256 { get; set; } = string.Empty;
    public string AfterStoreSha256 { get; set; } = string.Empty;
}

internal sealed class AotTodoNotificationActivationTargetEvidence
{
    public string? WidgetId { get; set; }
    public string? ItemId { get; set; }
    public bool PreferTodayFilter { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(AotTodoNotificationActivationSmokeResult),
    TypeInfoPropertyName = "SmokeResult")]
internal partial class AotTodoNotificationActivationJsonContext :
    JsonSerializerContext
{
}
#endif
