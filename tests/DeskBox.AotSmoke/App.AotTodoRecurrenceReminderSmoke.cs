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
    private const string AotTodoRecurrenceReminderSmokeEnvironmentVariable =
        "DESKBOX_AOT_TODO_RECURRENCE_REMINDER_SMOKE";
    private const string AotTodoRecurrenceReminderPhaseEnvironmentVariable =
        "DESKBOX_AOT_TODO_RECURRENCE_REMINDER_PHASE";
    private const string AotTodoRecurrenceReminderRunIdEnvironmentVariable =
        "DESKBOX_AOT_TODO_RECURRENCE_REMINDER_RUN_ID";
    private const string AotTodoRecurrenceReminderScenario =
        "DeterministicStateMatrix";
    private const string AotTodoSeedAndSnoozePhase = "SeedAndSnooze";
    private const string AotTodoSnoozeAndCompletePhase = "SnoozeAndComplete";
    private const string AotTodoNextOccurrencePhase = "NextOccurrence";
    private const string AotTodoRestorePhase = "Restore";
    private const string AotTodoPostflightPhase = "Postflight";
    private const string AotTodoRecurrenceReminderSmokeDirectoryName =
        "aot-todo-recurrence-reminder-smoke";
    private const string AotTodoRecurrenceReminderFixtureDirectoryName =
        "aot-todo-recurrence-reminder-fixture";
    private const string AotTodoWidgetId = "aot-5b4c3a-todo";
    private const string AotTodoRecurringItemId = "aot-5b4c3a-recurring";
    private const string AotTodoDefaultOffsetItemId = "aot-5b4c3a-default-offset";
    private const string AotTodoReminderOffItemId = "aot-5b4c3a-reminder-off";
    private const string AotTodoCompletedControlItemId = "aot-5b4c3a-completed-control";
    private const string AotTodoStaleControlItemId = "aot-5b4c3a-stale-control";
    private const string AotTodoRecurrenceSeriesId = "aot-5b4c3a-series";

    private static readonly DateTimeOffset AotTodoBaseClock =
        new(2026, 8, 24, 1, 0, 0, TimeSpan.Zero);

    private void StartAotTodoRecurrenceReminderSmokeIfRequested()
    {
        string? scenario = Environment.GetEnvironmentVariable(
            AotTodoRecurrenceReminderSmokeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(scenario))
        {
            return;
        }

        string? phase = Environment.GetEnvironmentVariable(
            AotTodoRecurrenceReminderPhaseEnvironmentVariable);
        string? runId = Environment.GetEnvironmentVariable(
            AotTodoRecurrenceReminderRunIdEnvironmentVariable);
        if (!string.Equals(
                scenario.Trim(),
                AotTodoRecurrenceReminderScenario,
                StringComparison.Ordinal) ||
            !IsAotTodoRecurrenceReminderPhase(phase) ||
            !Guid.TryParseExact(runId, "N", out _))
        {
            Log(
                $"[AotTodoRecurrenceReminderSmoke] Refused unsupported request " +
                $"scenario='{scenario}' phase='{phase}' runId='{runId}'.");
            return;
        }

        _ = RunAotTodoRecurrenceReminderSmokeAsync(phase!, runId!);
    }

    private async Task RunAotTodoRecurrenceReminderSmokeAsync(
        string phase,
        string runId)
    {
        await Task.Yield();

        DeskBoxDataPathService dataPaths = DeskBoxDataPathService.Current;
        string? configuredPreviewRoot = Environment.GetEnvironmentVariable(
            DeskBoxDataPathService.AotPreviewRootEnvironmentVariable);
        if (!dataPaths.IsDevelopmentRoot ||
            string.IsNullOrWhiteSpace(configuredPreviewRoot) ||
            !AotTodoPathsEqual(dataPaths.RootPath, configuredPreviewRoot))
        {
            Log(
                "[AotTodoRecurrenceReminderSmoke] RefusedNonPreviewRoot: the " +
                "Todo state matrix requires an explicit isolated Native AOT preview root.");
            return;
        }

        string smokeRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotTodoRecurrenceReminderSmokeDirectoryName));
        string phaseRoot = Path.GetFullPath(Path.Combine(
            smokeRoot,
            phase.ToLowerInvariant()));
        string fixtureRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotTodoRecurrenceReminderFixtureDirectoryName));
        if (!AotTodoIsPathEqualOrInside(dataPaths.RootPath, smokeRoot) ||
            !AotTodoIsPathEqualOrInside(smokeRoot, phaseRoot) ||
            !AotTodoIsPathEqualOrInside(dataPaths.RootPath, fixtureRoot))
        {
            Log(
                $"[AotTodoRecurrenceReminderSmoke] Refused unsafe fixture or " +
                $"result root '{fixtureRoot}' / '{phaseRoot}'.");
            return;
        }

        Directory.CreateDirectory(phaseRoot);
        Directory.CreateDirectory(fixtureRoot);
        string resultPath = Path.Combine(phaseRoot, "result.json");
        var result = new AotTodoRecurrenceReminderSmokeResult
        {
            SchemaVersion = 1,
            Stage = "5B-4C3A",
            Scenario = AotTodoRecurrenceReminderScenario,
            Phase = phase,
            RunId = runId,
            State = "Running",
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            ExecutablePath = Environment.ProcessPath ?? string.Empty,
            PreviewDataRoot = dataPaths.RootPath,
            FixtureRoot = fixtureRoot,
            ResultPath = resultPath,
            IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            NotificationChannel = "CapturedCallbackOnly",
            SystemNotificationAttempted = false,
            Steps = []
        };
        WriteAotTodoRecurrenceReminderResult(resultPath, result);

        try
        {
            await CaptureAotTodoRecurrenceReminderMatrixAsync(result);
            result.ExecutableSha256 = ComputeAotTodoSha256(result.ExecutablePath);
            RequireAotTodo(
                result,
                !result.IsDynamicCodeSupported,
                "runtime-native-aot",
                "Todo recurrence/reminder smoke did not run inside Native AOT.");
            RequireAotTodo(
                result,
                !result.SystemNotificationAttempted &&
                string.Equals(
                    result.NotificationChannel,
                    "CapturedCallbackOnly",
                    StringComparison.Ordinal),
                "no-system-notification",
                "The deterministic matrix must not enter the system notification path.");
            result.Success = true;
            result.State = "Completed";
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.State = "Failed";
            result.Error = ex.ToString();
            Log(
                $"[AotTodoRecurrenceReminderSmoke] Phase {phase} failed: {ex}");
        }
        finally
        {
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            result.NormalShutdownRequested = true;
            WriteAotTodoRecurrenceReminderResult(resultPath, result);
            Log(
                $"[AotTodoRecurrenceReminderSmoke] phase={phase} " +
                $"state={result.State} success={result.Success} result='{resultPath}'");
            await Task.Delay(100);
            await ShutdownApplicationAsync();
        }
    }

    private async Task CaptureAotTodoRecurrenceReminderMatrixAsync(
        AotTodoRecurrenceReminderSmokeResult result)
    {
        string settingsRoot = Path.Combine(result.FixtureRoot, "settings");
        string widgetsRoot = Path.Combine(result.FixtureRoot, "widgets");
        Directory.CreateDirectory(settingsRoot);
        Directory.CreateDirectory(widgetsRoot);

        var settingsService = new SettingsService(settingsRoot);
        await settingsService.LoadAsync();
        ConfigureAotTodoFixtureSettings(settingsService.Settings);
        await settingsService.SaveAsync(notifySubscribers: false);
        RequireAotTodo(
            result,
            settingsService.LastPersistenceFailure is null &&
            settingsService.Settings.TodoReminderEnabled &&
            FeatureWidgetSettings.IsEnabled(
                settingsService.Settings,
                WidgetKind.Todo),
            "fixture-settings-configured",
            "The isolated Todo reminder settings were not persisted.");

        var localizationService = new LocalizationService(settingsService);
        var store = new TodoWidgetStore(widgetsRoot, AotTodoWidgetId);
        DateTimeOffset currentClock = AotTodoBaseClock;
        var callbackNotifications = new List<AotTodoReminderNotificationEvidence>();
        using var reminderService = new TodoReminderService(
            settingsService,
            localizationService,
            dispatcherQueue: null,
            notification => callbackNotifications.Add(
                CaptureAotTodoNotification(notification)),
            widgetId => new TodoWidgetStore(widgetsRoot, widgetId),
            () => currentClock);

        result.FixedBaseClock = AotTodoBaseClock;
        result.Before = await CaptureAotTodoStateAsync(store);

        switch (result.Phase)
        {
            case AotTodoSeedAndSnoozePhase:
                RequireAotTodo(
                    result,
                    result.Before.Items.Count == 0 &&
                    !result.Before.StoreFileExists,
                    "seed-baseline-empty",
                    "The first Todo fixture phase did not start from an empty store.");

                await store.SaveAsync(CreateAotTodoSeedData());
                currentClock = AotTodoBaseClock;
                int initialCount = await reminderService.CheckNowAsync(currentClock);
                result.CheckCounts.Add(initialCount);
                RequireAotTodo(
                    result,
                    initialCount == 2 &&
                    callbackNotifications.Count == 1 &&
                    callbackNotifications[0].Count == 2,
                    "initial-due-candidates-exact",
                    "The initial due scan did not return exactly two candidates in one callback.");

                AotTodoRecurrenceReminderStateEvidence notifiedState =
                    await CaptureAotTodoStateAsync(store);
                RequireAotTodo(
                    result,
                    HasDismissedDue(notifiedState, AotTodoRecurringItemId) &&
                    HasDismissedDue(notifiedState, AotTodoDefaultOffsetItemId) &&
                    !FindAotTodoItem(notifiedState, AotTodoReminderOffItemId)
                        .ReminderLastNotifiedAt.HasValue &&
                    !FindAotTodoItem(notifiedState, AotTodoStaleControlItemId)
                        .ReminderLastNotifiedAt.HasValue,
                    "reminder-controls-skipped",
                    "Reminder-off, completed, stale, or active candidate state was incorrect.");

                result.SnoozeSucceeded = await reminderService.SnoozeAsync(
                    AotTodoWidgetId,
                    AotTodoRecurringItemId,
                    TimeSpan.FromMinutes(10));
                RequireAotTodo(
                    result,
                    result.SnoozeSucceeded,
                    "recurring-snooze-persisted",
                    "The product SnoozeAsync path rejected the recurring item.");

                currentClock = AotTodoBaseClock.AddMinutes(9);
                int beforeSnoozeCount =
                    await reminderService.CheckNowAsync(currentClock);
                result.CheckCounts.Add(beforeSnoozeCount);
                RequireAotTodo(
                    result,
                    beforeSnoozeCount == 0 && callbackNotifications.Count == 1,
                    "snooze-before-deadline-suppressed",
                    "The snoozed item fired before its fixed deadline.");

                result.After = await CaptureAotTodoStateAsync(store);
                AotTodoItemStateEvidence snoozed = FindAotTodoItem(
                    result.After,
                    AotTodoRecurringItemId);
                RequireAotTodo(
                    result,
                    snoozed.SnoozedUntil == AotTodoBaseClock.AddMinutes(10) &&
                    snoozed.SnoozeLastNotifiedAt is null &&
                    HasDismissedDue(result.After, AotTodoRecurringItemId),
                    "snooze-state-durable",
                    "The fixed snooze deadline or persisted due dismissal was incomplete.");
                break;

            case AotTodoSnoozeAndCompletePhase:
                RequireSeededAotTodoState(result, result.Before);
                currentClock = AotTodoBaseClock.AddMinutes(9);
                int restartBeforeSnoozeCount =
                    await reminderService.CheckNowAsync(currentClock);
                result.CheckCounts.Add(restartBeforeSnoozeCount);
                RequireAotTodo(
                    result,
                    restartBeforeSnoozeCount == 0 &&
                    callbackNotifications.Count == 0,
                    "restart-before-snooze-suppressed",
                    "A new process fired the snoozed item before its deadline.");

                currentClock = AotTodoBaseClock.AddMinutes(10);
                int snoozeDeadlineCount =
                    await reminderService.CheckNowAsync(currentClock);
                result.CheckCounts.Add(snoozeDeadlineCount);
                RequireAotTodo(
                    result,
                    snoozeDeadlineCount == 1 &&
                    callbackNotifications.Count == 1 &&
                    callbackNotifications[0].Count == 1 &&
                    string.Equals(
                        callbackNotifications[0].ItemId,
                        AotTodoRecurringItemId,
                        StringComparison.Ordinal),
                    "snooze-deadline-fired-once",
                    "The restarted snooze deadline did not produce exactly one recurring candidate.");

                currentClock = AotTodoBaseClock.AddMinutes(10).AddSeconds(20);
                int snoozeRepeatCount =
                    await reminderService.CheckNowAsync(currentClock);
                result.CheckCounts.Add(snoozeRepeatCount);
                RequireAotTodo(
                    result,
                    snoozeRepeatCount == 0 && callbackNotifications.Count == 1,
                    "snooze-repeat-suppressed",
                    "The same snooze trigger repeated in one service session.");
                result.Intermediate = await CaptureAotTodoStateAsync(store);
                AotTodoItemStateEvidence firedSnooze = FindAotTodoItem(
                    result.Intermediate,
                    AotTodoRecurringItemId);
                RequireAotTodo(
                    result,
                    firedSnooze.SnoozedUntil is null &&
                    firedSnooze.SnoozeLastNotifiedAt ==
                        AotTodoBaseClock.AddMinutes(10),
                    "snooze-trigger-state-persisted",
                    "The product snooze trigger did not clear and persist its state.");

                currentClock = AotTodoBaseClock.AddMinutes(10);
                result.CompleteSucceeded = await reminderService.CompleteAsync(
                    AotTodoWidgetId,
                    AotTodoRecurringItemId);
                RequireAotTodo(
                    result,
                    result.CompleteSucceeded,
                    "recurring-completed",
                    "The product CompleteAsync path rejected the recurring item.");
                result.After = await CaptureAotTodoStateAsync(store);
                AotTodoItemStateEvidence completed = FindAotTodoItem(
                    result.After,
                    AotTodoRecurringItemId);
                AotTodoItemStateEvidence generated = FindAotTodoItem(
                    result.After,
                    completed.GeneratedNextItemId);
                RequireAotTodo(
                    result,
                    completed.IsCompleted &&
                    completed.CompletedAt == AotTodoBaseClock.AddMinutes(10) &&
                    !string.IsNullOrWhiteSpace(completed.GeneratedNextItemId) &&
                    generated.DueDate?.ToUniversalTime() ==
                        AotTodoBaseClock.AddMinutes(5).AddDays(1) &&
                    string.Equals(
                        completed.RecurrenceSeriesId,
                        generated.RecurrenceSeriesId,
                        StringComparison.Ordinal),
                    "next-occurrence-generated",
                    "Completing the recurring task did not generate the linked next day occurrence.");
                RequireAotTodo(
                    result,
                    !generated.IsCompleted &&
                    generated.CompletedAt is null &&
                    generated.ReminderLastNotifiedAt is null &&
                    generated.ReminderDismissedForDueDate is null &&
                    generated.SnoozedUntil is null &&
                    generated.SnoozeLastNotifiedAt is null &&
                    generated.ReminderOffsetMinutes == 5 &&
                    string.Equals(
                        generated.RecurrenceMode,
                        TodoRecurrenceMode.Daily,
                        StringComparison.Ordinal),
                    "next-occurrence-state-reset",
                    "The generated occurrence inherited stale completion or reminder state.");
                break;

            case AotTodoNextOccurrencePhase:
                RequireCompletedAotTodoState(result, result.Before);
                AotTodoItemStateEvidence source = FindAotTodoItem(
                    result.Before,
                    AotTodoRecurringItemId);
                AotTodoItemStateEvidence next = FindAotTodoItem(
                    result.Before,
                    source.GeneratedNextItemId);
                DateTimeOffset nextReminderAt =
                    next.DueDate!.Value.ToUniversalTime().AddMinutes(-5);
                result.NextReminderAt = nextReminderAt;

                currentClock = nextReminderAt.AddSeconds(-1);
                int nextBeforeCount =
                    await reminderService.CheckNowAsync(currentClock);
                result.CheckCounts.Add(nextBeforeCount);
                RequireAotTodo(
                    result,
                    nextBeforeCount == 0 && callbackNotifications.Count == 0,
                    "next-reminder-before-deadline-suppressed",
                    "The generated occurrence fired before its reminder deadline.");

                currentClock = nextReminderAt;
                int nextDueCount = await reminderService.CheckNowAsync(currentClock);
                result.CheckCounts.Add(nextDueCount);
                RequireAotTodo(
                    result,
                    nextDueCount == 1 &&
                    callbackNotifications.Count == 1 &&
                    callbackNotifications[0].Count == 1 &&
                    string.Equals(
                        callbackNotifications[0].ItemId,
                        next.Id,
                        StringComparison.Ordinal),
                    "next-reminder-fired-once",
                    "The generated occurrence did not fire exactly at its fixed reminder deadline.");

                currentClock = nextReminderAt.AddSeconds(20);
                int nextRepeatCount =
                    await reminderService.CheckNowAsync(currentClock);
                result.CheckCounts.Add(nextRepeatCount);
                RequireAotTodo(
                    result,
                    nextRepeatCount == 0 && callbackNotifications.Count == 1,
                    "next-reminder-repeat-suppressed",
                    "The generated occurrence reminder repeated in one service session.");
                result.After = await CaptureAotTodoStateAsync(store);
                AotTodoItemStateEvidence dismissedNext = FindAotTodoItem(
                    result.After,
                    next.Id);
                RequireAotTodo(
                    result,
                    dismissedNext.ReminderLastNotifiedAt == nextReminderAt &&
                    dismissedNext.ReminderDismissedForDueDate == next.DueDate,
                    "next-reminder-dismissal-persisted",
                    "The generated occurrence reminder dismissal was not persisted.");
                break;

            case AotTodoRestorePhase:
                RequireDismissedNextAotTodoState(result, result.Before);
                AotTodoItemStateEvidence restoreSource = FindAotTodoItem(
                    result.Before,
                    AotTodoRecurringItemId);
                AotTodoItemStateEvidence restoreNext = FindAotTodoItem(
                    result.Before,
                    restoreSource.GeneratedNextItemId);
                currentClock = restoreNext.ReminderLastNotifiedAt!.Value
                    .ToUniversalTime()
                    .AddSeconds(30);
                int restoredCount =
                    await reminderService.CheckNowAsync(currentClock);
                result.CheckCounts.Add(restoredCount);
                RequireAotTodo(
                    result,
                    restoredCount == 0 && callbackNotifications.Count == 0,
                    "restart-dismissal-persisted",
                    "A new process repeated a reminder whose due-date dismissal was persisted.");

                await store.ClearAsync();
                result.StoreCleared = true;
                result.After = await CaptureAotTodoStateAsync(store);
                RequireAotTodo(
                    result,
                    result.After.StoreFileExists &&
                    result.After.StoreVersion == 3 &&
                    result.After.Items.Count == 0,
                    "store-cleared",
                    "The product Todo clear path did not persist an empty version 3 store.");
                break;

            case AotTodoPostflightPhase:
                RequireAotTodo(
                    result,
                    result.Before.StoreFileExists &&
                    result.Before.StoreVersion == 3 &&
                    result.Before.Items.Count == 0,
                    "cleanup-restart-empty",
                    "The postflight process did not reload the cleared Todo store.");
                currentClock = AotTodoBaseClock.AddDays(2);
                int postflightCount =
                    await reminderService.CheckNowAsync(currentClock);
                result.CheckCounts.Add(postflightCount);
                result.After = await CaptureAotTodoStateAsync(store);
                RequireAotTodo(
                    result,
                    postflightCount == 0 &&
                    callbackNotifications.Count == 0 &&
                    result.Before.StoreSha256 == result.After.StoreSha256 &&
                    result.After.Items.Count == 0,
                    "cleanup-postflight-empty",
                    "The clean postflight scan changed the empty store or emitted a callback.");
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported Todo recurrence/reminder phase '{result.Phase}'.");
        }

        result.CallbackNotifications = callbackNotifications;
    }

    private static TodoWidgetData CreateAotTodoSeedData()
    {
        DateTimeOffset due = AotTodoBaseClock.AddMinutes(5);
        DateTimeOffset created = AotTodoBaseClock.AddDays(-1);
        return new TodoWidgetData
        {
            Items =
            [
                new TodoItem
                {
                    Id = AotTodoRecurringItemId,
                    Text = "AOT recurring reminder",
                    DueDate = due,
                    Recurrence = new TodoRecurrence
                    {
                        Mode = TodoRecurrenceMode.Daily,
                        AnchorDueDate = due
                    },
                    ReminderOffsetMinutes = 5,
                    RecurrenceSeriesId = AotTodoRecurrenceSeriesId,
                    SortOrder = 0,
                    CreatedAt = created,
                    UpdatedAt = created
                },
                new TodoItem
                {
                    Id = AotTodoDefaultOffsetItemId,
                    Text = "AOT default offset reminder",
                    DueDate = due,
                    ReminderOffsetMinutes = null,
                    SortOrder = 1,
                    CreatedAt = created,
                    UpdatedAt = created
                },
                new TodoItem
                {
                    Id = AotTodoReminderOffItemId,
                    Text = "AOT reminder off control",
                    DueDate = due,
                    ReminderOffsetMinutes = TodoReminderOptions.ReminderOff,
                    SortOrder = 2,
                    CreatedAt = created,
                    UpdatedAt = created
                },
                new TodoItem
                {
                    Id = AotTodoCompletedControlItemId,
                    Text = "AOT completed control",
                    IsCompleted = true,
                    DueDate = due,
                    CompletedAt = created.AddHours(1),
                    ReminderOffsetMinutes = 5,
                    SortOrder = 3,
                    CreatedAt = created,
                    UpdatedAt = created.AddHours(1)
                },
                new TodoItem
                {
                    Id = AotTodoStaleControlItemId,
                    Text = "AOT stale overdue control",
                    DueDate = AotTodoBaseClock.AddMinutes(-2),
                    ReminderOffsetMinutes = 0,
                    SortOrder = 4,
                    CreatedAt = created,
                    UpdatedAt = created
                }
            ]
        };
    }

    private static void ConfigureAotTodoFixtureSettings(AppSettings settings)
    {
        settings.Language = SettingsService.LanguageEnglish;
        settings.TodoReminderEnabled = true;
        settings.TodoDefaultReminderOffsetMinutes = 5;
        FeatureWidgetSettings.SetEnabled(settings, WidgetKind.Todo, true);
        settings.DeletedWidgetIds.Clear();
        settings.Widgets =
        [
            new WidgetConfig
            {
                Id = AotTodoWidgetId,
                Name = "AOT Todo Recurrence Fixture",
                IsDefaultTitle = false,
                WidgetKind = WidgetKind.Todo,
                IsVisible = false,
                IsDisabled = false
            }
        ];
    }

    private static async Task<AotTodoRecurrenceReminderStateEvidence>
        CaptureAotTodoStateAsync(TodoWidgetStore store)
    {
        TodoWidgetData data = await store.LoadAsync();
        bool storeFileExists = File.Exists(store.StorePath);
        return new AotTodoRecurrenceReminderStateEvidence
        {
            StoreVersion = data.Version,
            StoreFileExists = storeFileExists,
            StoreLength = storeFileExists ? new FileInfo(store.StorePath).Length : 0,
            StoreSha256 = storeFileExists
                ? ComputeAotTodoSha256(store.StorePath)
                : string.Empty,
            Items = data.Items
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .Select(item => new AotTodoItemStateEvidence
                {
                    Id = item.Id,
                    Text = item.Text,
                    IsCompleted = item.IsCompleted,
                    DueDate = item.DueDate,
                    RecurrenceMode = TodoRecurrenceMode.Normalize(
                        item.Recurrence?.Mode),
                    RecurrenceAnchorDueDate = item.Recurrence?.AnchorDueDate,
                    CompletedAt = item.CompletedAt,
                    ReminderLastNotifiedAt = item.ReminderLastNotifiedAt,
                    ReminderDismissedForDueDate =
                        item.ReminderDismissedForDueDate,
                    ReminderOffsetMinutes = item.ReminderOffsetMinutes,
                    SnoozedUntil = item.SnoozedUntil,
                    SnoozeLastNotifiedAt = item.SnoozeLastNotifiedAt,
                    RecurrenceSeriesId = item.RecurrenceSeriesId,
                    GeneratedNextItemId = item.GeneratedNextItemId,
                    SortOrder = item.SortOrder,
                    StepCount = item.Steps.Count,
                    AttachmentCount = item.Attachments.Count,
                    CreatedAt = item.CreatedAt,
                    UpdatedAt = item.UpdatedAt
                })
                .ToList()
        };
    }

    private static AotTodoReminderNotificationEvidence CaptureAotTodoNotification(
        TodoReminderNotification notification) =>
        new()
        {
            Title = notification.Title,
            Message = notification.Message,
            Count = notification.Count,
            WidgetId = notification.WidgetId,
            ItemId = notification.ItemId,
            HasTodayDueItem = notification.HasTodayDueItem
        };

    private static void RequireSeededAotTodoState(
        AotTodoRecurrenceReminderSmokeResult result,
        AotTodoRecurrenceReminderStateEvidence state)
    {
        AotTodoItemStateEvidence recurring = FindAotTodoItem(
            state,
            AotTodoRecurringItemId);
        RequireAotTodo(
            result,
            state.StoreFileExists &&
            state.StoreVersion == 3 &&
            state.Items.Count == 5 &&
            recurring.SnoozedUntil == AotTodoBaseClock.AddMinutes(10) &&
            recurring.SnoozeLastNotifiedAt is null &&
            HasDismissedDue(state, AotTodoRecurringItemId) &&
            HasDismissedDue(state, AotTodoDefaultOffsetItemId),
            "seeded-state-reloaded",
            "The second process did not reload the seeded due and snooze state.");
    }

    private static void RequireCompletedAotTodoState(
        AotTodoRecurrenceReminderSmokeResult result,
        AotTodoRecurrenceReminderStateEvidence state)
    {
        AotTodoItemStateEvidence source = FindAotTodoItem(
            state,
            AotTodoRecurringItemId);
        AotTodoItemStateEvidence next = FindAotTodoItem(
            state,
            source.GeneratedNextItemId);
        RequireAotTodo(
            result,
            state.StoreFileExists &&
            state.StoreVersion == 3 &&
            state.Items.Count == 6 &&
            source.IsCompleted &&
            source.CompletedAt == AotTodoBaseClock.AddMinutes(10) &&
            next.DueDate?.ToUniversalTime() ==
                AotTodoBaseClock.AddMinutes(5).AddDays(1) &&
            next.ReminderDismissedForDueDate is null &&
            next.ReminderLastNotifiedAt is null,
            "completed-state-reloaded",
            "The third process did not reload the completed source and clean next occurrence.");
    }

    private static void RequireDismissedNextAotTodoState(
        AotTodoRecurrenceReminderSmokeResult result,
        AotTodoRecurrenceReminderStateEvidence state)
    {
        AotTodoItemStateEvidence source = FindAotTodoItem(
            state,
            AotTodoRecurringItemId);
        AotTodoItemStateEvidence next = FindAotTodoItem(
            state,
            source.GeneratedNextItemId);
        RequireAotTodo(
            result,
            state.StoreFileExists &&
            state.Items.Count == 6 &&
            next.ReminderLastNotifiedAt.HasValue &&
            next.ReminderDismissedForDueDate == next.DueDate,
            "next-dismissal-reloaded",
            "The fourth process did not reload the generated reminder dismissal.");
    }

    private static bool HasDismissedDue(
        AotTodoRecurrenceReminderStateEvidence state,
        string itemId)
    {
        AotTodoItemStateEvidence item = FindAotTodoItem(state, itemId);
        return item.ReminderLastNotifiedAt.HasValue &&
            item.ReminderDismissedForDueDate == item.DueDate;
    }

    private static AotTodoItemStateEvidence FindAotTodoItem(
        AotTodoRecurrenceReminderStateEvidence state,
        string? itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            throw new InvalidOperationException("Expected a non-empty Todo item id.");
        }

        return state.Items.Single(item =>
            string.Equals(item.Id, itemId, StringComparison.Ordinal));
    }

    private static void RequireAotTodo(
        AotTodoRecurrenceReminderSmokeResult result,
        bool condition,
        string step,
        string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{step}: {message}");
        }

        result.Steps.Add(step);
    }

    private static bool IsAotTodoRecurrenceReminderPhase(string? phase) =>
        phase is AotTodoSeedAndSnoozePhase or
            AotTodoSnoozeAndCompletePhase or
            AotTodoNextOccurrencePhase or
            AotTodoRestorePhase or
            AotTodoPostflightPhase;

    private static void WriteAotTodoRecurrenceReminderResult(
        string resultPath,
        AotTodoRecurrenceReminderSmokeResult result)
    {
        string temporaryPath = resultPath + ".tmp";
        string json = JsonSerializer.Serialize(
            result,
            AotTodoRecurrenceReminderJsonContext.Default
                .AotTodoRecurrenceReminderSmokeResult);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, resultPath, overwrite: true);
    }

    private static string ComputeAotTodoSha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static bool AotTodoPathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static bool AotTodoIsPathEqualOrInside(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return AotTodoPathsEqual(normalizedRoot, normalizedCandidate) ||
            normalizedCandidate.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class AotTodoRecurrenceReminderSmokeResult
{
    public int SchemaVersion { get; set; }
    public string Stage { get; set; } = string.Empty;
    public string Scenario { get; set; } = string.Empty;
    public string Phase { get; set; } = string.Empty;
    public string RunId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public bool Success { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int ProcessId { get; set; }
    public string ExecutablePath { get; set; } = string.Empty;
    public string ExecutableSha256 { get; set; } = string.Empty;
    public string PreviewDataRoot { get; set; } = string.Empty;
    public string FixtureRoot { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public bool IsDynamicCodeSupported { get; set; }
    public DateTimeOffset FixedBaseClock { get; set; }
    public DateTimeOffset? NextReminderAt { get; set; }
    public string NotificationChannel { get; set; } = string.Empty;
    public bool SystemNotificationAttempted { get; set; }
    public bool SnoozeSucceeded { get; set; }
    public bool CompleteSucceeded { get; set; }
    public bool StoreCleared { get; set; }
    public bool NormalShutdownRequested { get; set; }
    public List<int> CheckCounts { get; set; } = [];
    public List<AotTodoReminderNotificationEvidence> CallbackNotifications { get; set; } = [];
    public AotTodoRecurrenceReminderStateEvidence Before { get; set; } = new();
    public AotTodoRecurrenceReminderStateEvidence? Intermediate { get; set; }
    public AotTodoRecurrenceReminderStateEvidence After { get; set; } = new();
    public List<string> Steps { get; set; } = [];
    public string? Error { get; set; }
}

internal sealed class AotTodoRecurrenceReminderStateEvidence
{
    public int StoreVersion { get; set; }
    public bool StoreFileExists { get; set; }
    public long StoreLength { get; set; }
    public string StoreSha256 { get; set; } = string.Empty;
    public List<AotTodoItemStateEvidence> Items { get; set; } = [];
}

internal sealed class AotTodoItemStateEvidence
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public DateTimeOffset? DueDate { get; set; }
    public string RecurrenceMode { get; set; } = string.Empty;
    public DateTimeOffset? RecurrenceAnchorDueDate { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? ReminderLastNotifiedAt { get; set; }
    public DateTimeOffset? ReminderDismissedForDueDate { get; set; }
    public int? ReminderOffsetMinutes { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
    public DateTimeOffset? SnoozeLastNotifiedAt { get; set; }
    public string? RecurrenceSeriesId { get; set; }
    public string? GeneratedNextItemId { get; set; }
    public int SortOrder { get; set; }
    public int StepCount { get; set; }
    public int AttachmentCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class AotTodoReminderNotificationEvidence
{
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int Count { get; set; }
    public string? WidgetId { get; set; }
    public string? ItemId { get; set; }
    public bool HasTodayDueItem { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(AotTodoRecurrenceReminderSmokeResult),
    TypeInfoPropertyName = "AotTodoRecurrenceReminderSmokeResult")]
internal partial class AotTodoRecurrenceReminderJsonContext : JsonSerializerContext
{
}
#endif
