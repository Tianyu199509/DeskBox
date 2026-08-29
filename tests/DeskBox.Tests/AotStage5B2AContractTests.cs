namespace DeskBox.Tests;

public sealed class AotStage5B2AContractTests
{
    [Fact]
    public void ShellSmokeRunner_IsNativeAotOnlyAndRequiresThePreviewRoot()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotShellSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_SHELL_SMOKE", source, StringComparison.Ordinal);
        Assert.Contains("ExplorerQuickAccessReadOnly", source, StringComparison.Ordinal);
        Assert.Contains(
            "DeskBoxDataPathService.AotPreviewRootEnvironmentVariable",
            source,
            StringComparison.Ordinal);
        Assert.Contains("IsDevelopmentRoot", source, StringComparison.Ordinal);
        Assert.Contains("aot-shell-smoke", source, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplorerSmoke_UsesTheProductServiceAndProvesTheLaunchedProbeEffect()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotShellSmoke.cs");
        string service = ReadRepositoryFile("src/DeskBox/Helpers/ExplorerShellLaunchService.cs");

        Assert.Contains("ExplorerShellLaunchService.TryOpen(", source, StringComparison.Ordinal);
        Assert.Contains("ExplorerShellLaunchNativeCallResult? explorerNative", source, StringComparison.Ordinal);
        Assert.Contains("explorer-launch-probe.cmd", source, StringComparison.Ordinal);
        Assert.Contains("explorer-launch-marker.txt", source, StringComparison.Ordinal);
        Assert.Contains("explorerNative.AttemptedPhases", source, StringComparison.Ordinal);
        Assert.Contains("explorer-launch-marker", source, StringComparison.Ordinal);
        Assert.Contains("out ExplorerShellLaunchNativeCallResult? nativeResult", service, StringComparison.Ordinal);
        Assert.Contains("nativeResult = ExplorerShellLaunchNativeBackend.TryOpen(", service, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickAccessSmoke_UsesThePublicReadPathAndNeverCallsMutationOperations()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotShellSmoke.cs");

        Assert.Contains(
            "ExplorerQuickAccessHelper.GetQuickAccessPinStateAsync(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "QuickAccessNativeBackend.Invoke(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("QuickAccessNativeOperation.QueryPinState", source, StringComparison.Ordinal);
        Assert.Contains("quick-access-query-before", source, StringComparison.Ordinal);
        Assert.Contains("quick-access-query-after", source, StringComparison.Ordinal);
        Assert.Contains("quick-access-native-query", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryPinFolderToQuickAccess", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TryUnpinFolderFromQuickAccess", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QuickAccessNativeOperation.Pin,", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QuickAccessNativeOperation.Unpin", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellSmokeEvidence_RecordsBothPoliciesAndTheLoadedAuditedModule()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotShellSmoke.cs");

        Assert.Contains("ExplorerShellLaunchBackendPolicy.Current", source, StringComparison.Ordinal);
        Assert.Contains("QuickAccessBackendPolicy.Current", source, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeBackend.CaptureDiagnosticState()", source, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeModule.Default", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeFeature.IsDynamicCodeSupported", source, StringComparison.Ordinal);
        Assert.Contains("ModulePath", source, StringComparison.Ordinal);
        Assert.Contains("ModuleSha256", source, StringComparison.Ordinal);
        Assert.Contains("ModuleHandle", source, StringComparison.Ordinal);
        Assert.Contains("AbiVersion", source, StringComparison.Ordinal);
        Assert.Contains("Capabilities", source, StringComparison.Ordinal);
        Assert.Contains("AotShellSmokeJsonContext.Default.AotShellSmokeResult", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppLaunch_SchedulesTheShellSmokeOnlyAfterSuccessfulInitialization()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml.cs");

        int completed = app.IndexOf(
            "Log(\"OnLaunched completed successfully\");",
            StringComparison.Ordinal);
        int shellSmoke = app.IndexOf("StartAotShellSmokeIfRequested();", StringComparison.Ordinal);

        Assert.True(completed >= 0 && shellSmoke > completed);
    }

    [Fact]
    public void ShellSmokeScript_IsolatedlyLaunchesWaitsAndCleansTheExactAotProcess()
    {
        string script = ReadRepositoryFile("scripts/run-aot-shell-smoke.ps1");

        Assert.Contains("DESKBOX_AOT_SHELL_SMOKE", script, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_SHORTCUT_SMOKE", script, StringComparison.Ordinal);
        Assert.Contains("previousShellSmoke", script, StringComparison.Ordinal);
        Assert.Contains("previousShortcutSmoke", script, StringComparison.Ordinal);
        Assert.Contains("start-aot-preview.ps1", script, StringComparison.Ordinal);
        Assert.Contains("result.json", script, StringComparison.Ordinal);
        Assert.Contains("Completed", script, StringComparison.Ordinal);
        Assert.Contains("TimeoutSeconds", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", script, StringComparison.Ordinal);
        Assert.Contains("finally", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellSmokeScript_RequiresActualEffectsHashesAndUnchangedProductionData()
    {
        string script = ReadRepositoryFile("scripts/run-aot-shell-smoke.ps1");

        Assert.Contains("executableSha256", script, StringComparison.Ordinal);
        Assert.Contains("rustNativeSha256", script, StringComparison.Ordinal);
        Assert.Contains("ModuleSha256", script, StringComparison.Ordinal);
        Assert.Contains("ModulePath", script, StringComparison.Ordinal);
        Assert.Contains("ExplorerMarkerExists", script, StringComparison.Ordinal);
        Assert.Contains("QuickAccessStateBefore", script, StringComparison.Ordinal);
        Assert.Contains("QuickAccessStateAfter", script, StringComparison.Ordinal);
        Assert.Contains("QuickAccessNativeState", script, StringComparison.Ordinal);
        Assert.Contains("Get-DirectoryStateFingerprint", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("Production data changed", script, StringComparison.Ordinal);
        Assert.Contains("session.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditAndProject_DeclareStage5B2AAsTheCurrentProfile()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B2ASourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B2AMissingRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B2AMissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B2AUnsafeMutationPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B2AExpectedWmc1510Count = 1241", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("Explorer and Quick Access read-only", project, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
