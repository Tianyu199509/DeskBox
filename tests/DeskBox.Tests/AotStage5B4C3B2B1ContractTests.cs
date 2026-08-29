namespace DeskBox.Tests;

public sealed class AotStage5B4C3B2B1ContractTests
{
    [Fact]
    public void ProductForwarding_StoresTheFullTypedActivationAndDrainsOnlyWhenReady()
    {
        string app = Read("src/DeskBox/App.xaml.cs");

        Assert.Contains(
            "NativeAppNotificationActivation? nativeNotificationActivation",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "PendingNativeNotificationActivationStore.Store(nativeNotificationActivation)",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "CompleteExternalActivationInitializationAsync()",
            app,
            StringComparison.Ordinal);
        Assert.Contains("_activationEvent?.WaitOne(0)", app, StringComparison.Ordinal);
        Assert.Contains("DrainPendingNativeNotificationActivations()", app, StringComparison.Ordinal);
        Assert.Contains(
            "Forwarded activation drain yielded after",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "PendingNativeNotificationActivationStore.HasPendingActivation",
            app,
            StringComparison.Ordinal);
        foreach (string token in new[]
                 {
                     "new NativeAppNotificationActivation(",
                     "envelope.ActivationSource",
                     "envelope.CreatedAtUtc",
                     "envelope.SourceProcessId",
                     "envelope.EnvelopeId"
                 })
        {
            Assert.Contains(token, app, StringComparison.Ordinal);
        }
        Assert.Contains(
            "OnPendingNativeNotificationActivationRejected(",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TryGetCurrentNativeNotificationActivationArguments",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "StorePendingNativeNotificationActivationArguments",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TakePendingNativeNotificationActivationArguments",
            app,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnvelopeStore_IsAtomicBoundedSourceGeneratedAndLegacyCompatible()
    {
        string store = Read(
            "src/DeskBox/Services/NativeNotificationActivationEnvelopeStore.cs");

        Assert.Contains("pending-notification-activations", store, StringComparison.Ordinal);
        Assert.Contains("pending-notification-activation.txt", store, StringComparison.Ordinal);
        Assert.Contains("File.Move(tempPath, finalPath, overwrite: false)", store, StringComparison.Ordinal);
        Assert.Contains("WriteDisposition.Duplicate", store, StringComparison.Ordinal);
        Assert.Contains("MaxEnvelopeBytes", store, StringComparison.Ordinal);
        Assert.Contains("MaxUserInputEntries", store, StringComparison.Ordinal);
        Assert.Contains("HasPendingActivation", store, StringComparison.Ordinal);
        Assert.Contains("RecoverAbandonedClaims();", store, StringComparison.Ordinal);
        Assert.Contains("Process.GetProcessById(processId)", store, StringComparison.Ordinal);
        Assert.Contains("IsLegacyArgumentsOnly", store, StringComparison.Ordinal);
        Assert.Contains(
            "NativeNotificationActivationEnvelopeJsonContext.Default.Envelope",
            store,
            StringComparison.Ordinal);
        Assert.Equal(2, Count(store, "JsonSerializer."));
    }

    [Fact]
    public void Fixture_FreezesColdDrainAndRealSecondaryForwardingWithoutSystemNotification()
    {
        string fixture = Read(
            "src/DeskBox/App.AotTodoNotificationForwardingSmoke.cs");
        string app = Read("src/DeskBox/App.xaml.cs");

        foreach (string token in new[]
                 {
                     "#if DESKBOX_NATIVE_AOT",
                     "EnvelopeAndSingleInstance",
                     "SeedColdStart",
                     "ColdStartConsume",
                     "PrimaryAwait",
                     "SecondaryForward",
                     "Postflight",
                     "TryGetAotTodoNotificationForwardingActivation()",
                     "atomic-store-duplicate-and-corrupt-seeded",
                     "cold-start-drain-preserved-user-input",
                     "cold-start-mutation-persisted",
                     "live-second-instance-forwarding-persisted",
                     "postflight-state-reloaded-and-spool-empty",
                     "SystemNotificationAttempted = false",
                     "ExternalWindowsActivationAttempted = false",
                     "ShutdownApplicationAsync()"
                 })
        {
            Assert.Contains(token, fixture, StringComparison.Ordinal);
        }

        Assert.Contains(
            "ShouldSuppressAotTodoNotificationForwardingSystemNotification()",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "StartAotTodoNotificationForwardingSmokeIfRequested();",
            app,
            StringComparison.Ordinal);
        Assert.Equal(1, Count(fixture, "JsonSerializer.Serialize("));
        Assert.DoesNotContain("AppNotificationManager", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("deskbox_native_", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_RequiresFiveProcessesExactRedirectIsolationArchiveAndOwnedCleanup()
    {
        string runner = Read(
            "scripts/run-aot-todo-notification-forwarding-smoke.ps1");
        string managedRunner = Read("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("$requiredAuditProfileVersion = 58", runner, StringComparison.Ordinal);
        Assert.Contains("$requiredSummarySchemaVersion = 55", runner, StringComparison.Ordinal);
        Assert.Contains("-NoStop", runner, StringComparison.Ordinal);
        Assert.Contains("-ExpectExistingInstance", runner, StringComparison.Ordinal);
        Assert.Contains("ExistingInstanceActivated", runner, StringComparison.Ordinal);
        Assert.Contains("Count -eq 5", runner, StringComparison.Ordinal);
        Assert.Contains("processIdsDistinct", runner, StringComparison.Ordinal);
        Assert.Contains("executableHashesMatch", runner, StringComparison.Ordinal);
        Assert.Contains("typedUserInputPreserved = $true", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintAfter", runner, StringComparison.Ordinal);
        Assert.Contains("Wait-NaturalPreviewExit", runner, StringComparison.Ordinal);
        Assert.Contains("forwarding-session.json", runner, StringComparison.Ordinal);
        Assert.Contains(
            "Refusing to clean an unowned Todo notification forwarding root",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("TodoNotificationEnvelopeForwarding", managedRunner, StringComparison.Ordinal);
        Assert.Contains(
            "run-aot-todo-notification-forwarding-smoke.ps1",
            managedRunner,
            StringComparison.Ordinal);
    }

    [Fact]
    public void JsonInventory_AdvancesOnlyForTheEnvelopeStoreAndAotEvidence()
    {
        string baseline = Read(
            "tests/DeskBox.Tests/JsonSerializationBaselineContractTests.cs");
        string rust = Read("native/deskbox-native/src/lib.rs");

        Assert.Contains("TwentyEightFilesAndSixtyFourCalls", baseline, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(28, actual.Count)", baseline, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(64, actual.Values.Sum())", baseline, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(26, actualContextOwners.Length)", baseline, StringComparison.Ordinal);
        Assert.Contains(
            "App.AotTodoNotificationForwardingSmoke.cs\"] = 1",
            baseline,
            StringComparison.Ordinal);
        Assert.Contains(
            "NativeNotificationActivationEnvelopeStore.cs\"] = 2",
            baseline,
            StringComparison.Ordinal);
        Assert.Contains("assert_eq!(deskbox_native_capabilities(), 511);", rust, StringComparison.Ordinal);
        Assert.Equal(10, Count(rust, "#[unsafe(no_mangle)]"));
    }

    [Fact]
    public void AuditProfile_ProjectAndRoadmapAdvanceToB2B1AndKeepB2B2Deferred()
    {
        string audit = Read("scripts/publish-aot-audit.ps1");
        string launcher = Read("scripts/start-aot-preview.ps1");
        string project = Read("src/DeskBox/DeskBox.csproj");
        string report = Read("docs/architecture/aot-stage-5b-4c3b2b1-report.md");
        string roadmap = Read("docs/architecture/rust-native-aot-roadmap.md");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B2B1RequiredScenarioPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B2B1MissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C3B2B1RustAbiUnchanged", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B2A", project, StringComparison.Ordinal);
        Assert.Contains("real Windows notification click", project, StringComparison.Ordinal);
        Assert.Contains("profile 56 / schema 53", report, StringComparison.Ordinal);
        Assert.Contains("5B-4C3B2B2", report, StringComparison.Ordinal);
        Assert.Contains("profile 56 / schema 53", roadmap, StringComparison.Ordinal);
        Assert.Contains("5B-4C3B2B2A 已完成", roadmap, StringComparison.Ordinal);
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
