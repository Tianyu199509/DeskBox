namespace DeskBox.Tests;

public sealed class AotStage5B2BContractTests
{
    [Fact]
    public void MutationRunner_IsNativeAotOnlyAndUsesAStablePreviewFixture()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotQuickAccessMutationSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE", source, StringComparison.Ordinal);
        Assert.Contains("PinUnpin", source, StringComparison.Ordinal);
        Assert.Contains("CompensateUnpin", source, StringComparison.Ordinal);
        Assert.Contains(
            "DeskBoxDataPathService.AotPreviewRootEnvironmentVariable",
            source,
            StringComparison.Ordinal);
        Assert.Contains("IsDevelopmentRoot", source, StringComparison.Ordinal);
        Assert.Contains("aot-quick-access-mutation-smoke", source, StringComparison.Ordinal);
        Assert.Contains("mutation-target", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Directory.Delete(targetFolder", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PinUnpinScenario_UsesProductMutationsAndProvesPublicAndNativeTransitions()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotQuickAccessMutationSmoke.cs");

        Assert.Contains(
            "ExplorerQuickAccessHelper.TryPinFolderToQuickAccessAsync(targetFolder)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccessAsync(targetFolder)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExplorerQuickAccessHelper.GetQuickAccessPinStateAsync(targetFolder)",
            source,
            StringComparison.Ordinal);
        Assert.Contains("QuickAccessNativeOperation.QueryPinState", source, StringComparison.Ordinal);
        Assert.Contains("mutation-initial-not-pinned", source, StringComparison.Ordinal);
        Assert.Contains("mutation-pin-request", source, StringComparison.Ordinal);
        Assert.Contains("mutation-pinned-public", source, StringComparison.Ordinal);
        Assert.Contains("mutation-pinned-native", source, StringComparison.Ordinal);
        Assert.Contains("mutation-unpin-request", source, StringComparison.Ordinal);
        Assert.Contains("mutation-unpinned-public", source, StringComparison.Ordinal);
        Assert.Contains("mutation-unpinned-native", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QuickAccessNativeOperation.Pin,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QuickAccessNativeOperation.Unpin", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MutationRunner_AlwaysCompensatesInsideFinallyAndRequiresNotPinnedAtExit()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotQuickAccessMutationSmoke.cs")
            .ReplaceLineEndings("\n");

        int tryIndex = source.IndexOf("try\n        {", StringComparison.Ordinal);
        int finallyIndex = source.IndexOf("finally\n        {", StringComparison.Ordinal);
        int cleanupIndex = source.IndexOf("RunCompensatingUnpinAsync", StringComparison.Ordinal);

        Assert.True(tryIndex >= 0 && finallyIndex > tryIndex && cleanupIndex > finallyIndex);
        Assert.Contains("cleanup-unpin-request", source, StringComparison.Ordinal);
        Assert.Contains("cleanup-final-not-pinned", source, StringComparison.Ordinal);
        Assert.Contains("cleanup-native-not-pinned", source, StringComparison.Ordinal);
        Assert.Contains("FinalPublicState", source, StringComparison.Ordinal);
        Assert.Contains("FinalNativeState", source, StringComparison.Ordinal);
        Assert.Contains("CleanupSucceeded", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CompensationScenario_AcceptsAnExistingPinButStillRequiresFinalNotPinned()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotQuickAccessMutationSmoke.cs");

        Assert.Contains("AotQuickAccessMutationScenario.CompensateUnpin", source, StringComparison.Ordinal);
        Assert.Contains("compensation-initial-state-readable", source, StringComparison.Ordinal);
        Assert.Contains("initial.State != QuickAccessPinState.Unknown", source, StringComparison.Ordinal);
        Assert.Contains("cleanup-final-not-pinned", source, StringComparison.Ordinal);
        Assert.Contains("cleanup-native-not-pinned", source, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureScenarios_CoverInProcessFinallyAndForcedTerminationRecovery()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotQuickAccessMutationSmoke.cs");

        Assert.Contains("AotQuickAccessMutationScenario.PinThenFail", source, StringComparison.Ordinal);
        Assert.Contains(
            "AotQuickAccessMutationScenario.PinThenAwaitExternalCompensation",
            source,
            StringComparison.Ordinal);
        Assert.Contains("intentional-after-pin", source, StringComparison.Ordinal);
        Assert.Contains("AwaitingExternalCompensation", source, StringComparison.Ordinal);
        Assert.Contains("WriteQuickAccessMutationSmokeResult(resultPath, result)", source, StringComparison.Ordinal);
        Assert.Contains("Task.Delay(Timeout.InfiniteTimeSpan)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MutationEvidence_RecordsTheLoadedAuditedRustBoundary()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotQuickAccessMutationSmoke.cs");

        Assert.Contains("QuickAccessBackendPolicy.Current", source, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeBackend.CaptureDiagnosticState()", source, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeModule.Default", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeFeature.IsDynamicCodeSupported", source, StringComparison.Ordinal);
        Assert.Contains("ModulePath", source, StringComparison.Ordinal);
        Assert.Contains("ModuleSha256", source, StringComparison.Ordinal);
        Assert.Contains("ModuleHandle", source, StringComparison.Ordinal);
        Assert.Contains("AbiVersion", source, StringComparison.Ordinal);
        Assert.Contains("Capabilities", source, StringComparison.Ordinal);
        Assert.Contains(
            "AotQuickAccessMutationSmokeJsonContext.Default.AotQuickAccessMutationSmokeResult",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppLaunch_SchedulesMutationSmokeOnlyAfterSuccessfulInitialization()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml.cs");

        int completed = app.IndexOf(
            "Log(\"OnLaunched completed successfully\");",
            StringComparison.Ordinal);
        int mutation = app.IndexOf(
            "StartAotQuickAccessMutationSmokeIfRequested();",
            StringComparison.Ordinal);

        Assert.True(completed >= 0 && mutation > completed);
    }

    [Fact]
    public void MutationScript_RunsPreflightMainAndPostflightWithExactProcessCleanup()
    {
        string script = ReadRepositoryFile("scripts/run-aot-quick-access-mutation-smoke.ps1");

        Assert.Contains("DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE", script, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_SHORTCUT_SMOKE", script, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_SHELL_SMOKE", script, StringComparison.Ordinal);
        Assert.Contains("previousMutationSmoke", script, StringComparison.Ordinal);
        Assert.Contains("start-aot-preview.ps1", script, StringComparison.Ordinal);
        Assert.Contains("CompensateUnpin", script, StringComparison.Ordinal);
        Assert.Contains("PinUnpin", script, StringComparison.Ordinal);
        Assert.Contains("preflight", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("postflight", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("finally", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-MutationScenario", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MutationScript_HardGatesCompensationTransitionsHashesAndProductionIsolation()
    {
        string script = ReadRepositoryFile("scripts/run-aot-quick-access-mutation-smoke.ps1");

        Assert.Contains("CleanupSucceeded", script, StringComparison.Ordinal);
        Assert.Contains("FinalPublicState", script, StringComparison.Ordinal);
        Assert.Contains("FinalNativeState", script, StringComparison.Ordinal);
        Assert.Contains("NotPinned", script, StringComparison.Ordinal);
        Assert.Contains("InitialPublicState", script, StringComparison.Ordinal);
        Assert.Contains("PinnedPublicState", script, StringComparison.Ordinal);
        Assert.Contains("PinnedNativeState", script, StringComparison.Ordinal);
        Assert.Contains("executableSha256", script, StringComparison.Ordinal);
        Assert.Contains("rustNativeSha256", script, StringComparison.Ordinal);
        Assert.Contains("Get-DirectoryStateFingerprint", script, StringComparison.Ordinal);
        Assert.Contains("Production data changed", script, StringComparison.Ordinal);
        Assert.Contains("session.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MutationScript_ActuallyExercisesBothFailureModesAndRequiresPinnedRecovery()
    {
        string script = ReadRepositoryFile("scripts/run-aot-quick-access-mutation-smoke.ps1");

        Assert.Contains("in-process-failure", script, StringComparison.Ordinal);
        Assert.Contains("forced-termination", script, StringComparison.Ordinal);
        Assert.Contains("recovery", script, StringComparison.Ordinal);
        Assert.Contains("Assert-InProcessFailureResult", script, StringComparison.Ordinal);
        Assert.Contains("Assert-ForcedTerminationResult", script, StringComparison.Ordinal);
        Assert.Contains("PinThenFail", script, StringComparison.Ordinal);
        Assert.Contains("PinThenAwaitExternalCompensation", script, StringComparison.Ordinal);
        Assert.Contains("-RequireInitiallyPinned", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditAndProject_DeclareStage5B2BAsTheCurrentProfile()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B2BSourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B2BMissingRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B2BMissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B2BUnsafeRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B2BExpectedWmc1510Count = 1241", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("Quick Access pin/unpin", project, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
