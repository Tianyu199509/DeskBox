namespace DeskBox.Tests;

public sealed class AotStage5B4C3B2B2AContractTests
{
    [Fact]
    public void ProductRoute_WaitsForContentAndReportsPresentationAndVisibleRefresh()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string router = Read(
            "src/DeskBox/Services/TodoNotificationActivationRouter.cs");
        string manager = Read(
            "src/DeskBox/Services/WidgetManager.FeatureWidgets.cs");
        string surface = Read(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml.cs");

        Assert.Contains("DispositionTargetUnavailable", router, StringComparison.Ordinal);
        Assert.Contains("bool TargetPresented", router, StringComparison.Ordinal);
        Assert.Contains("bool RefreshCompleted", router, StringComparison.Ordinal);
        Assert.Contains("Task<bool>> showTargetAsync", router, StringComparison.Ordinal);
        Assert.Contains("Task<bool>> refreshAsync", router, StringComparison.Ordinal);
        Assert.Contains("await window.ContentReadyTask;", manager, StringComparison.Ordinal);
        Assert.Contains("WaitForTodoReminderSurfaceLoadedAsync", manager, StringComparison.Ordinal);
        Assert.Contains("WaitForTodoReminderSurfaceCommitAsync", manager, StringComparison.Ordinal);
        Assert.Contains("CompositionTarget.Rendering += rendering;", manager, StringComparison.Ordinal);
        Assert.Contains("TodoReminderTargetPresentationResult", manager, StringComparison.Ordinal);
        Assert.Contains("surfaceReady &&", manager, StringComparison.Ordinal);
        Assert.Contains("public bool RevealReminderItem", surface, StringComparison.Ordinal);
        Assert.Contains("await window.ContentReadyTask;", app, StringComparison.Ordinal);
        Assert.Contains("await adapter.RefreshAsync();", app, StringComparison.Ordinal);
        Assert.Contains(
            "await WidgetManager.WaitForTodoReminderSurfaceCommitAsync(todoContent);",
            app,
            StringComparison.Ordinal);
        Assert.Contains("targetPresented={result.TargetPresented}", app, StringComparison.Ordinal);
        Assert.Contains("refreshCompleted={result.RefreshCompleted}", app, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_UsesRealTodoSurfaceButLabelsControlledInputHonestly()
    {
        string fixture = Read(
            "src/DeskBox/App.AotTodoNotificationSurfaceSmoke.cs");
        string app = Read("src/DeskBox/App.xaml.cs");

        foreach (string token in new[]
                 {
                     "DESKBOX_AOT_TODO_NOTIFICATION_SURFACE_SMOKE",
                     "TodoNotificationSurfaceRouting",
                     "Stage = \"5B-4C3B2B2A\"",
                     "RouteTodoNotificationActivationAsync(",
                     "CaptureAotTodoNotificationSurfaceHostAsync(",
                     "body-visible-item-located",
                     "complete-visible-refresh-proved",
                     "snooze-user-input-visible-refresh-proved",
                     "SystemNotificationAttempted = false",
                     "ExternalWindowsActivationAttempted = false",
                     "UserClickVerified = false",
                     "controlled-input-not-mislabeled-as-real-click",
                     "ShutdownApplicationAsync()"
                 })
        {
            Assert.Contains(token, fixture, StringComparison.Ordinal);
        }

        Assert.Contains(
            "StartAotTodoNotificationSurfaceSmokeIfRequested();",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AppNotificationManager", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("deskbox_native_", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_RequiresAuditedOneProcessIsolationNaturalExitAndOwnedCleanup()
    {
        string runner = Read(
            "scripts/run-aot-todo-notification-surface-smoke.ps1");
        string managedRunner = Read("scripts/run-aot-managed-ui-smoke.ps1");

        foreach (string token in new[]
                 {
                     "$requiredAuditProfileVersion = 58",
                     "$requiredSummarySchemaVersion = 55",
                     "[Guid]::NewGuid().ToString(\"N\")",
                     "-AllowEarlyExit",
                     "-StartupWaitSeconds 1",
                     "Wait-NaturalPreviewExit",
                     "ProcessCount = 1",
                     "NaturalExitCount = 1",
                     "Production data changed during the Todo notification surface smoke",
                     "Refusing to clean an unowned Todo notification surface root",
                     "Remove-Item -LiteralPath $resolvedRoot -Recurse -Force",
                     "surface-session.json"
                 })
        {
            Assert.Contains(token, runner, StringComparison.Ordinal);
        }

        Assert.Contains("TodoNotificationSurfaceRouting", managedRunner, StringComparison.Ordinal);
        Assert.Contains(
            "run-aot-todo-notification-surface-smoke.ps1",
            managedRunner,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_ReusesManagedUiSourceGeneratedJsonInventory()
    {
        string fixture = Read(
            "src/DeskBox/App.AotTodoNotificationSurfaceSmoke.cs");
        string managed = Read("src/DeskBox/App.AotManagedUiSmoke.cs");
        string baseline = Read(
            "tests/DeskBox.Tests/JsonSerializationBaselineContractTests.cs");

        Assert.DoesNotContain("JsonSerializer.Serialize(", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializerContext", fixture, StringComparison.Ordinal);
        Assert.Contains(
            "AotTodoNotificationSurfaceEvidence? TodoNotificationSurface",
            managed,
            StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(28, actual.Count)", baseline, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(64, actual.Values.Sum())", baseline, StringComparison.Ordinal);
        Assert.Contains(
            "Assert.Equal(26, actualContextOwners.Length)",
            baseline,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_FreezesB2B2AScopeWithoutChangingRustOrProfile()
    {
        string audit = Read("scripts/publish-aot-audit.ps1");
        string project = Read("src/DeskBox/DeskBox.csproj");
        string report = Read(
            "docs/architecture/aot-stage-5b-4c3b2b2a-report.md");
        string roadmap = Read("docs/architecture/rust-native-aot-roadmap.md");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B2B2AMissingScenarioPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B2B2AMissingProductPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B2B2AMissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B2B2AForbiddenScopePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B2B2ARustAbiUnchanged", audit, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B2A", project, StringComparison.Ordinal);
        Assert.Contains(
            "real Windows notification click provenance",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "69402a1914814f778abdfc29daf1b4f5",
            report,
            StringComparison.Ordinal);
        Assert.Contains("UserClickVerified=false", report, StringComparison.Ordinal);
        Assert.Contains("5B-4C3B2B2B1", report, StringComparison.Ordinal);
        Assert.Contains("5B-4C3B2B2A 已完成", roadmap, StringComparison.Ordinal);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
