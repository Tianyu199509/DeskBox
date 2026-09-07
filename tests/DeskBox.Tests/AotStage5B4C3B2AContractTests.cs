namespace DeskBox.Tests;

public sealed class AotStage5B4C3B2AContractTests
{
    [Fact]
    public void ProductParser_AcceptsBothPayloadGrammarsAndDelegatesToOneRouter()
    {
        string app = Read("src/DeskBox/App.xaml.cs");

        Assert.Contains("arguments.Split(", app, StringComparison.Ordinal);
        Assert.Contains("['&', ';']", app, StringComparison.Ordinal);
        Assert.Contains("RouteTodoNotificationActivationAsync(", app, StringComparison.Ordinal);
        Assert.Contains("TodoNotificationActivationRouter.RouteAsync(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("CompleteTodoReminderFromNotificationAsync(", app, StringComparison.Ordinal);
        Assert.DoesNotContain("SnoozeTodoReminderFromNotificationAsync(", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Router_CoversOpenCompleteCurrentAndLegacySnoozeWithoutSilentFallback()
    {
        string router = Read("src/DeskBox/Services/TodoNotificationActivationRouter.cs");

        Assert.Contains("ActionComplete = \"complete\"", router, StringComparison.Ordinal);
        Assert.Contains("ActionSnooze = \"snooze\"", router, StringComparison.Ordinal);
        Assert.Contains("LegacyActionSnooze10 = \"snooze10\"", router, StringComparison.Ordinal);
        Assert.Contains("Snooze10Minutes = \"10m\"", router, StringComparison.Ordinal);
        Assert.Contains("Snooze30Minutes = \"30m\"", router, StringComparison.Ordinal);
        Assert.Contains("Snooze1Hour = \"1h\"", router, StringComparison.Ordinal);
        Assert.Contains("SnoozeTomorrow = \"tomorrow\"", router, StringComparison.Ordinal);
        Assert.Contains("DispositionRejectedUnsupportedSnooze", router, StringComparison.Ordinal);
        Assert.Contains("CompleteAsync(", router, StringComparison.Ordinal);
        Assert.Contains("SnoozeUntilAsync(", router, StringComparison.Ordinal);
        Assert.Contains("GetTomorrowAtNine(", router, StringComparison.Ordinal);
    }

    [Fact]
    public void Scenario_IsNativeAotOnlyIsolatedThreePhaseAndSourceGenerated()
    {
        string fixture = Read("src/DeskBox/App.AotTodoNotificationActivationSmoke.cs");
        string app = Read("src/DeskBox/App.xaml.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", fixture, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_TODO_NOTIFICATION_ACTIVATION_SMOKE", fixture, StringComparison.Ordinal);
        Assert.Contains("DeterministicActionRouting", fixture, StringComparison.Ordinal);
        Assert.Contains("Guid.TryParseExact(runId, \"N\"", fixture, StringComparison.Ordinal);
        Assert.Contains("dataPaths.IsDevelopmentRoot", fixture, StringComparison.Ordinal);
        Assert.Contains("RouteAndPersist", fixture, StringComparison.Ordinal);
        Assert.Contains("VerifyAndClear", fixture, StringComparison.Ordinal);
        Assert.Contains("Postflight", fixture, StringComparison.Ordinal);
        Assert.Contains("NormalShutdownRequested = true", fixture, StringComparison.Ordinal);
        Assert.Contains("ShutdownApplicationAsync()", fixture, StringComparison.Ordinal);
        Assert.Contains("StartAotTodoNotificationActivationSmokeIfRequested();", app, StringComparison.Ordinal);
        Assert.Equal(1, Count(fixture, "JsonSerializer.Serialize("));
    }

    [Fact]
    public void Matrix_FreezesGrammarActionsRejectionsPersistenceAndNoExternalBoundary()
    {
        string fixture = Read("src/DeskBox/App.AotTodoNotificationActivationSmoke.cs");
        string router = Read("src/DeskBox/Services/TodoNotificationActivationRouter.cs");
        string runner = Read("scripts/run-aot-todo-notification-activation-smoke.ps1");
        string constrained = fixture + router + runner;

        foreach (string step in new[]
                 {
                     "semicolon-body-open-routed",
                     "ampersand-grammar-compatible",
                     "complete-action-idempotent",
                     "snooze-10m-persisted-and-idempotent",
                     "snooze-30m-persisted-and-idempotent",
                     "snooze-1h-persisted-and-idempotent",
                     "snooze-tomorrow-persisted-and-idempotent",
                     "legacy-snooze10-compatible",
                     "invalid-inputs-rejected-without-mutation",
                     "cross-process-action-state-reloaded",
                     "activation-store-cleared",
                     "postflight-empty-and-stable"
                 })
        {
            Assert.Contains(step, fixture, StringComparison.Ordinal);
        }

        Assert.Contains("SystemNotificationAttempted = false", fixture, StringComparison.Ordinal);
        Assert.Contains("ExternalActivationAttempted = false", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("AppNotificationManager", constrained, StringComparison.Ordinal);
        Assert.DoesNotContain("AppInstance.GetCurrent()", constrained, StringComparison.Ordinal);
        Assert.DoesNotContain("RedirectActivation", constrained, StringComparison.Ordinal);
        Assert.DoesNotContain("deskbox_native_", constrained, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_RequiresThreeProcessesContinuityIsolationArchiveAndOwnedCleanup()
    {
        string runner = Read("scripts/run-aot-todo-notification-activation-smoke.ps1");
        string managedRunner = Read("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("profile 56 / schema 53", runner, StringComparison.Ordinal);
        Assert.Contains("Invoke-TodoNotificationActivationPhase", runner, StringComparison.Ordinal);
        Assert.Contains("processIdsDistinct", runner, StringComparison.Ordinal);
        Assert.Contains("executableHashesMatch", runner, StringComparison.Ordinal);
        Assert.Contains("routeAndPersistRoutes = 18", runner, StringComparison.Ordinal);
        Assert.Contains("verifyAndClearRoutes = 2", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintAfter", runner, StringComparison.Ordinal);
        Assert.Contains("Wait-NaturalPreviewExit", runner, StringComparison.Ordinal);
        Assert.Contains("activation-session.json", runner, StringComparison.Ordinal);
        Assert.Contains("Refusing to clean an unowned Todo notification activation root", runner, StringComparison.Ordinal);
        Assert.Contains("TodoNotificationActionRouting", managedRunner, StringComparison.Ordinal);
        Assert.Contains("run-aot-todo-notification-activation-smoke.ps1", managedRunner, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditProfile_AdvancesAndFreezesTheExistingRustAbi()
    {
        string audit = Read("scripts/publish-aot-audit.ps1");
        string launcher = Read("scripts/start-aot-preview.ps1");
        string project = Read("src/DeskBox/DeskBox.csproj");
        string rust = Read("native/deskbox-native/src/lib.rs");
        string report = Read("docs/architecture/stage-reports/aot-stage-5b-4c3b2a-report.md");
        string roadmap = Read("docs/architecture/rust-native-aot-roadmap.md");

        Assert.Contains("$auditProfileVersion = 59", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B2ARequiredScenarioPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B2AMissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B2ARustAbiUnchanged", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 59", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B2A", project, StringComparison.Ordinal);
        Assert.Contains("real Windows notification click provenance", project, StringComparison.Ordinal);
        Assert.Contains("assert_eq!(deskbox_native_capabilities(), 511);", rust, StringComparison.Ordinal);
        Assert.Equal(10, Count(rust, "#[unsafe(no_mangle)]"));
        Assert.Contains("profile 55 / schema 52", report, StringComparison.Ordinal);
        Assert.Contains("2520cacfa69c4024b7210bc8629330dd", report, StringComparison.Ordinal);
        Assert.Contains("5B-4C3B2B1", report, StringComparison.Ordinal);
        Assert.Contains("profile 56 / schema 53", roadmap, StringComparison.Ordinal);
        Assert.Contains("5B-4C3B2A 已完成", roadmap, StringComparison.Ordinal);
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
