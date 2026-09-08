namespace DeskBox.Tests;

public sealed class AotStage5B4C3B1ContractTests
{
    [Fact]
    public void Scenario_IsNativeAotOnlyIsolatedPhasedAndNormallyShutDown()
    {
        string app = Read("src/DeskBox/App.AotTodoNotificationLifecycleSmoke.cs");
        string launch = Read("src/DeskBox/App.xaml.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", app, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_TODO_NOTIFICATION_SMOKE", app, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_TODO_NOTIFICATION_PHASE", app, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_TODO_NOTIFICATION_RUN_ID", app, StringComparison.Ordinal);
        Assert.Contains("RealDisplayAndCleanup", app, StringComparison.Ordinal);
        Assert.Contains("ShowAndInspect", app, StringComparison.Ordinal);
        Assert.Contains("Cleanup", app, StringComparison.Ordinal);
        Assert.Contains("Postflight", app, StringComparison.Ordinal);
        Assert.Contains("Guid.TryParseExact(runId, \"N\"", app, StringComparison.Ordinal);
        Assert.Contains("dataPaths.IsDevelopmentRoot", app, StringComparison.Ordinal);
        Assert.Contains("configuredPreviewRoot", app, StringComparison.Ordinal);
        Assert.Contains("NormalShutdownRequested = true", app, StringComparison.Ordinal);
        Assert.Contains("ShutdownApplicationAsync()", app, StringComparison.Ordinal);
        Assert.Contains("StartAotTodoNotificationLifecycleSmokeIfRequested();", launch, StringComparison.Ordinal);
        Assert.Equal(1, Count(app, "JsonSerializer.Serialize("));
    }

    [Fact]
    public void ProductNotificationBoundary_ExposesExactHistoryAndCleanupWithoutBroadDeletion()
    {
        string service = Read("src/DeskBox/Services/NativeAppNotificationService.cs");

        Assert.Contains("public bool IsRegistered => _isRegistered;", service, StringComparison.Ordinal);
        Assert.Contains("AppNotificationManager.Default.GetAllAsync()", service, StringComparison.Ordinal);
        Assert.Contains("NativeAppNotificationSnapshot", service, StringComparison.Ordinal);
        Assert.Contains("RemoveByTagAndGroupAsync(string tag, string group)", service, StringComparison.Ordinal);
        Assert.Contains("AppNotificationManager.Default.RemoveByTagAndGroupAsync(tag, group)", service, StringComparison.Ordinal);
        Assert.Contains("public bool Unregister()", service, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveAllAsync", service, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveByGroupAsync", service, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_UsesActualTodoCompositionForSingleAndAggregatePayloads()
    {
        string app = Read("src/DeskBox/App.AotTodoNotificationLifecycleSmoke.cs");
        string product = Read("src/DeskBox/App.xaml.cs");

        Assert.Contains("TryShowNativeTodoReminderNotification(", app, StringComparison.Ordinal);
        Assert.Contains("new TodoReminderNotification(", app, StringComparison.Ordinal);
        Assert.Contains("new NativeAppNotificationOptions(result.SingleTag, result.Group)", app, StringComparison.Ordinal);
        Assert.Contains("new NativeAppNotificationOptions(result.AggregateTag, result.Group)", app, StringComparison.Ordinal);
        Assert.Contains("notification.Count == 1", product, StringComparison.Ordinal);
        Assert.Contains("TodoReminderActionComplete", product, StringComparison.Ordinal);
        Assert.Contains("TodoReminderActionSnooze", product, StringComparison.Ordinal);
        Assert.Contains("TodoReminderSnoozeTomorrow", product, StringComparison.Ordinal);
        Assert.Contains("options) == true", product, StringComparison.Ordinal);
    }

    [Fact]
    public void PayloadInspection_RequiresLaunchActionsFourSnoozeOptionsAndNoAggregateActions()
    {
        string app = Read("src/DeskBox/App.AotTodoNotificationLifecycleSmoke.cs");

        Assert.Contains("XDocument.Parse(snapshot.Payload", app, StringComparison.Ordinal);
        Assert.Contains("ParseAotTodoNotificationArguments(launch)", app, StringComparison.Ordinal);
        Assert.Contains("['&', ';']", app, StringComparison.Ordinal);
        Assert.Contains("hint-inputId", app, StringComparison.Ordinal);
        Assert.Contains("single-payload-actions-and-snooze-options-exact", app, StringComparison.Ordinal);
        Assert.Contains("aggregate-payload-has-no-actions", app, StringComparison.Ordinal);
        Assert.Contains("payload.Actions.Count != 2", app, StringComparison.Ordinal);
        Assert.Contains("payload.Actions.Count == 0", app, StringComparison.Ordinal);
        foreach (string selection in new[] { "10m", "30m", "1h", "tomorrow" })
        {
            Assert.Contains(selection, ProductAndFixture(app), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Runner_RequiresThreeProcessesRealDisplayExactCleanupIsolationAndNoActivation()
    {
        string runner = Read("scripts/run-aot-todo-notification-smoke.ps1");
        string managedRunner = Read("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("profile 56 / schema 53", runner, StringComparison.Ordinal);
        Assert.Contains("Invoke-TodoNotificationPhase", runner, StringComparison.Ordinal);
        Assert.Contains("processIdsDistinct", runner, StringComparison.Ordinal);
        Assert.Contains("executableHashesMatch", runner, StringComparison.Ordinal);
        Assert.Contains("realSystemNotificationsShown = 2", runner, StringComparison.Ordinal);
        Assert.Contains("exactTagGroupCleanup = $true", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintAfter", runner, StringComparison.Ordinal);
        Assert.Contains("Wait-NaturalPreviewExit", runner, StringComparison.Ordinal);
        Assert.Contains("Refusing to clean an unowned Todo notification root", runner, StringComparison.Ordinal);
        Assert.Contains("[Notification] Native notification activated", runner, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveAllAsync", runner, StringComparison.Ordinal);
        Assert.Contains("TodoNotificationDisplayCleanup", managedRunner, StringComparison.Ordinal);
        Assert.Contains("run-aot-todo-notification-smoke.ps1", managedRunner, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditProfile_AdvancesWithoutRustExpansion()
    {
        string audit = Read("scripts/publish-aot-audit.ps1");
        string launcher = Read("scripts/start-aot-preview.ps1");
        string project = Read("src/DeskBox/DeskBox.csproj");
        string rust = Read("native/deskbox-native/src/lib.rs");

        Assert.Contains("$auditProfileVersion = 62", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B1RequiredScenarioPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B1MissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B1RustAbiUnchanged", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 62", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("assert_eq!(deskbox_native_capabilities(), 1023);", rust, StringComparison.Ordinal);
        Assert.Equal(10, Count(rust, "#[unsafe(no_mangle)]"));
    }

    private static string ProductAndFixture(string fixture) =>
        fixture +
        Read("src/DeskBox/App.xaml.cs") +
        Read("src/DeskBox/Services/TodoNotificationActivationRouter.cs");

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
