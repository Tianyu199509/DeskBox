namespace DeskBox.Tests;

public sealed class AotStage5B4C3AContractTests
{
    [Fact]
    public void Scenario_IsNativeAotOnlyIsolatedPhasedAndNormallyShutDown()
    {
        string app = Read("src/DeskBox/App.AotTodoRecurrenceReminderSmoke.cs");
        string launch = Read("src/DeskBox/App.xaml.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", app, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_TODO_RECURRENCE_REMINDER_SMOKE", app, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_TODO_RECURRENCE_REMINDER_PHASE", app, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_TODO_RECURRENCE_REMINDER_RUN_ID", app, StringComparison.Ordinal);
        Assert.Contains("DeterministicStateMatrix", app, StringComparison.Ordinal);
        Assert.Contains("Guid.TryParseExact(runId, \"N\"", app, StringComparison.Ordinal);
        Assert.Contains("dataPaths.IsDevelopmentRoot", app, StringComparison.Ordinal);
        Assert.Contains("configuredPreviewRoot", app, StringComparison.Ordinal);
        Assert.Contains("NormalShutdownRequested = true", app, StringComparison.Ordinal);
        Assert.Contains("ShutdownApplicationAsync()", app, StringComparison.Ordinal);
        Assert.Contains("StartAotTodoRecurrenceReminderSmokeIfRequested();", launch, StringComparison.Ordinal);
        Assert.Equal(1, Count(app, "JsonSerializer.Serialize("));
    }

    [Fact]
    public void Matrix_UsesProductServicesWithFixedClockAndOwnedStores()
    {
        string app = Read("src/DeskBox/App.AotTodoRecurrenceReminderSmoke.cs");
        string reminder = Read("src/DeskBox/Services/TodoReminderService.cs");
        string recurrence = Read("src/DeskBox/Services/TodoRecurrenceService.cs");

        Assert.Contains("new SettingsService(settingsRoot)", app, StringComparison.Ordinal);
        Assert.Contains("new TodoWidgetStore(widgetsRoot, widgetId)", app, StringComparison.Ordinal);
        Assert.Contains("dispatcherQueue: null", app, StringComparison.Ordinal);
        Assert.Contains("() => currentClock", app, StringComparison.Ordinal);
        Assert.Contains("reminderService.CheckNowAsync", app, StringComparison.Ordinal);
        Assert.Contains("reminderService.SnoozeAsync", app, StringComparison.Ordinal);
        Assert.Contains("reminderService.CompleteAsync", app, StringComparison.Ordinal);
        Assert.Contains("Func<DateTimeOffset> clock", reminder, StringComparison.Ordinal);
        Assert.Contains("TodoRecurrenceService.TryCreateNextOccurrence", reminder, StringComparison.Ordinal);
        Assert.Contains("ReminderDismissedForDueDate = null", recurrence, StringComparison.Ordinal);
        Assert.Contains("SnoozedUntil = null", recurrence, StringComparison.Ordinal);
    }

    [Fact]
    public void Matrix_CoversCandidatesControlsSnoozeRecurrenceRestartAndCleanup()
    {
        string app = Read("src/DeskBox/App.AotTodoRecurrenceReminderSmoke.cs");

        foreach (string phase in new[]
                 {
                     "SeedAndSnooze",
                     "SnoozeAndComplete",
                     "NextOccurrence",
                     "Restore",
                     "Postflight"
                 })
        {
            Assert.Contains(phase, app, StringComparison.Ordinal);
        }

        foreach (string step in new[]
                 {
                     "initial-due-candidates-exact",
                     "reminder-controls-skipped",
                     "snooze-before-deadline-suppressed",
                     "snooze-deadline-fired-once",
                     "next-occurrence-generated",
                     "next-occurrence-state-reset",
                     "next-reminder-fired-once",
                     "restart-dismissal-persisted",
                     "store-cleared",
                     "cleanup-postflight-empty"
                 })
        {
            Assert.Contains(step, app, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void NotificationBoundary_IsCallbackOnlyAndNeverEntersSystemDisplay()
    {
        string app = Read("src/DeskBox/App.AotTodoRecurrenceReminderSmoke.cs");
        string runner = Read("scripts/run-aot-todo-recurrence-reminder-smoke.ps1");

        Assert.Contains("CapturedCallbackOnly", app, StringComparison.Ordinal);
        Assert.Contains("SystemNotificationAttempted = false", app, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowTodoReminderNotification(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeAppNotification", app, StringComparison.Ordinal);
        Assert.DoesNotContain("AppNotificationManager", app, StringComparison.Ordinal);
        Assert.Contains("[TodoReminder] Native notification shown", runner, StringComparison.Ordinal);
        Assert.Contains("[TodoReminder] Tray notification fallback shown", runner, StringComparison.Ordinal);
        Assert.Contains("systemNotificationAttempted = $false", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_RequiresFiveProcessesContinuityIsolationArchiveAndOwnedCleanup()
    {
        string runner = Read("scripts/run-aot-todo-recurrence-reminder-smoke.ps1");
        string managedRunner = Read("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("profile 56 / schema 53", runner, StringComparison.Ordinal);
        Assert.Contains("Invoke-TodoRecurrenceReminderPhase", runner, StringComparison.Ordinal);
        Assert.Contains("processIdsDistinct", runner, StringComparison.Ordinal);
        Assert.Contains("executableHashesMatch", runner, StringComparison.Ordinal);
        Assert.Contains("storeSha256", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintAfter", runner, StringComparison.Ordinal);
        Assert.Contains("Wait-NaturalPreviewExit", runner, StringComparison.Ordinal);
        Assert.Contains("Refusing to clean an unowned Todo recurrence/reminder root", runner, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $resolvedRoot -Recurse -Force", runner, StringComparison.Ordinal);
        Assert.Contains("todo-session.json", runner, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", runner, StringComparison.Ordinal);
        Assert.Contains("TodoRecurrenceReminderPersistenceRestart", managedRunner, StringComparison.Ordinal);
        Assert.Contains("run-aot-todo-recurrence-reminder-smoke.ps1", managedRunner, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditProfile_AdvancesWithoutRustExpansion()
    {
        string audit = Read("scripts/publish-aot-audit.ps1");
        string launcher = Read("scripts/start-aot-preview.ps1");
        string project = Read("src/DeskBox/DeskBox.csproj");
        string rust = Read("native/deskbox-native/src/lib.rs");

        Assert.Contains("$auditProfileVersion = 60", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3ARequiredScenarioPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3AMissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3ARustAbiUnchanged", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 60", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("assert_eq!(deskbox_native_capabilities(), 511);", rust, StringComparison.Ordinal);
        Assert.Equal(10, Count(rust, "#[unsafe(no_mangle)]"));
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    private static int Count(string source, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }
}
