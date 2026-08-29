namespace DeskBox.Tests;

public sealed class AotStage5B4C3B2B2BContractTests
{
    [Fact]
    public void ProductActivation_RecordsWindowsSourceAndPreservesItThroughEnvelope()
    {
        string app = Read("src/DeskBox/App.xaml.cs");
        string notifications = Read(
            "src/DeskBox/Services/NativeAppNotificationService.cs");
        string envelope = Read(
            "src/DeskBox/Services/NativeNotificationActivationEnvelopeStore.cs");

        foreach (string token in new[]
                 {
                     "public enum NativeAppNotificationActivationSource",
                     "NotificationInvokedEvent = 1",
                     "CurrentAppInstance = 2",
                     "DateTimeOffset CapturedAtUtc = default",
                     "int SourceProcessId = 0",
                     "string? EnvelopeId = null",
                     "NativeAppNotificationActivationSource.NotificationInvokedEvent",
                     "DateTimeOffset.UtcNow",
                     "Environment.ProcessId"
                 })
        {
            Assert.Contains(token, notifications, StringComparison.Ordinal);
        }

        foreach (string token in new[]
                 {
                     "ActivationSource = activation.Source",
                     "CreatedAtUtc = activation.CapturedAtUtc == default",
                     "SourceProcessId = activation.SourceProcessId",
                     "Enum.IsDefined(envelope.ActivationSource)"
                 })
        {
            Assert.Contains(token, envelope, StringComparison.Ordinal);
        }

        foreach (string token in new[]
                 {
                     "OnNativeNotificationActivationObserved(activation);",
                     "OnTodoNotificationActivationRouteObserved(activation, result);",
                     "NativeAppNotificationActivationSource.CurrentAppInstance",
                     "envelope.ActivationSource",
                     "envelope.CreatedAtUtc",
                     "envelope.SourceProcessId",
                     "envelope.EnvelopeId"
                 })
        {
            Assert.Contains(token, app, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Fixture_RequiresGenuineRunningAndColdWindowsClicksOnRealTodoSurface()
    {
        string fixture = Read(
            "src/DeskBox/App.AotTodoNotificationUserClickSmoke.cs");
        string managed = Read("src/DeskBox/App.AotManagedUiSmoke.cs");
        string app = Read("src/DeskBox/App.xaml.cs");

        foreach (string token in new[]
                 {
                     "DESKBOX_AOT_TODO_NOTIFICATION_USER_CLICK_SMOKE",
                     "RealWindowsNotificationUserClick",
                     "RunningMatrix",
                     "ColdSeed",
                     "ColdConsume",
                     "Postflight",
                     "Stage = \"5B-4C3B2B2B\"",
                     "OnNativeNotificationActivationObserved(",
                     "OnTodoNotificationActivationRouteObserved(",
                     "NativeAppNotificationActivationSource.NotificationInvokedEvent or",
                     "TryShowNativeTodoReminderNotification(",
                     "TimeSpan.FromMinutes(10)",
                     "matchingRoutes.Count == 1",
                     "{caseName}-real-todo-surface-state-exact",
                     "SystemNotificationAttempted = true",
                     "ExternalWindowsActivationObserved = true",
                     "UserClickVerified = true",
                     "RemoveByTagAndGroupAsync(",
                     "ShutdownApplicationAsync()"
                 })
        {
            Assert.Contains(token, fixture, StringComparison.Ordinal);
        }

        Assert.Contains(
            "AotTodoNotificationUserClickEvidence? TodoNotificationUserClick",
            managed,
            StringComparison.Ordinal);
        Assert.Contains(
            "StartAotTodoNotificationUserClickSmokeIfRequested();",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AppNotificationManager", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UIAutomation", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deskbox_native_", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_SeparatesHumanClicksAndProtectsDataLifecycle()
    {
        string runner = Read(
            "scripts/run-aot-todo-notification-user-click-smoke.ps1");

        foreach (string token in new[]
                 {
                     "$requiredAuditProfileVersion = 58",
                     "$requiredSummarySchemaVersion = 55",
                     "[switch]$IncludeColdStart",
                     "-AllowEarlyExit",
                     "Wait-InteractiveResult",
                     "Wait-NaturalPreviewExit",
                     "NotificationInvokedEvent",
                     "CurrentAppInstance",
                     "activationCount",
                     "routeCount",
                     "userInput.todoSnooze",
                     "Set-ColdActivationUserEnvironment",
                     "Restore-ColdActivationUserEnvironment",
                     "productionDataFingerprintBefore",
                     "productionDataFingerprintAfter",
                     "Refusing to replace an existing, production, or unowned click root",
                     "Remove-Item -LiteralPath $resolvedRoot -Recurse -Force",
                     "runningUserClicksVerified = $true",
                     "coldStartUserClickVerified = if ($IncludeColdStart)",
                     "previewRootCleaned = $previewRootCleaned"
                 })
        {
            Assert.Contains(token, runner, StringComparison.Ordinal);
        }

        Assert.Contains("请点击第 1/3 条通知", Read(
            "src/DeskBox/App.AotTodoNotificationUserClickSmoke.cs"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", runner, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("UIAutomation", runner, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evidence_ReusesSourceGeneratedJsonAndDoesNotExpandRustAbi()
    {
        string fixture = Read(
            "src/DeskBox/App.AotTodoNotificationUserClickSmoke.cs");
        string managed = Read("src/DeskBox/App.AotManagedUiSmoke.cs");
        string rust = Read("native/deskbox-native/src/lib.rs");

        Assert.DoesNotContain("JsonSerializer.Serialize(", fixture, StringComparison.Ordinal);
        Assert.Contains(
            "JsonSerializer.Serialize(",
            managed,
            StringComparison.Ordinal);
        Assert.Contains(
            "assert_eq!(deskbox_native_capabilities(), 511);",
            rust,
            StringComparison.Ordinal);
        Assert.Equal(
            10,
            CountOccurrences(rust, "#[unsafe(no_mangle)]"));
    }

    [Fact]
    public void Audit_FreezesUnifiedRealClickStageAtCurrentProfile()
    {
        string audit = Read("scripts/publish-aot-audit.ps1");
        string project = Read("src/DeskBox/DeskBox.csproj");

        foreach (string token in new[]
                 {
                     "$auditProfileVersion = 58",
                     "schemaVersion = 55",
                     "stage5B4C3B2B2BMissingScenarioPatterns",
                     "stage5B4C3B2B2BMissingProductPatterns",
                     "stage5B4C3B2B2BMissingSmokeScriptPatterns",
                     "stage5B4C3B2B2BForbiddenScopePatterns",
                     "stage5B4C3B2B2BRustAbiUnchanged",
                     "stage5B4C3B2B2BScenarioJsonSerializeCallCount",
                     "stage5B4C3B2B2BManagedUiJsonSerializeCallCount",
                     "stage5B4C3B2B2BExpectedWmc1510Count = 1213"
                 })
        {
            Assert.Contains(token, audit, StringComparison.Ordinal);
        }

        Assert.Contains(
            "Native AOT stage 5B-4C3B2B2B",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "genuine Notification Center body/Complete/Snooze",
            project,
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
