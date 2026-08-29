namespace DeskBox.Tests;

public sealed class AotStage5B1ContractTests
{
    [Fact]
    public void ShortcutSmokeRunner_IsNativeAotOnlyAndRequiresThePreviewRoot()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotShortcutSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_SHORTCUT_SMOKE", source, StringComparison.Ordinal);
        Assert.Contains(
            "DeskBoxDataPathService.AotPreviewRootEnvironmentVariable",
            source,
            StringComparison.Ordinal);
        Assert.Contains("IsDevelopmentRoot", source, StringComparison.Ordinal);
        Assert.Contains("aot-shortcut-smoke", source, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreSmoke_UsesTheRealProductWriteReadAndResolveEntryPoints()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotShortcutSmoke.cs");

        Assert.Contains(
            "DragDropPermissionService.CreateOrUpdateShortcut(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ShortcutHelper.CreateOrUpdateFolderShortcut(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("ShortcutHelper.ReadStoredMetadata(", source, StringComparison.Ordinal);
        Assert.Contains("ShortcutHelper.Resolve(", source, StringComparison.Ordinal);
        Assert.Contains("InvalidateStoredMetadataCache", source, StringComparison.Ordinal);
        Assert.Contains("core-create-application", source, StringComparison.Ordinal);
        Assert.Contains("core-overwrite-application", source, StringComparison.Ordinal);
        Assert.Contains("core-resolve-valid", source, StringComparison.Ordinal);
        Assert.Contains("core-resolve-missing", source, StringComparison.Ordinal);
        Assert.Contains("core-corrupt-read", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SmokeEvidence_RecordsTheLoadedAuditedRustModuleAndAotRuntime()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotShortcutSmoke.cs");

        Assert.Contains("ShortcutNativeBackend.CaptureDiagnosticState()", source, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeModule.Default", source, StringComparison.Ordinal);
        Assert.Contains("ModulePath", source, StringComparison.Ordinal);
        Assert.Contains("ModuleHandle", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeFeature.IsDynamicCodeSupported", source, StringComparison.Ordinal);
        Assert.Contains("SelectedBackend", source, StringComparison.Ordinal);
        Assert.Contains("ModuleSha256", source, StringComparison.Ordinal);
        Assert.Contains("AbiVersion", source, StringComparison.Ordinal);
        Assert.Contains("Capabilities", source, StringComparison.Ordinal);
        Assert.Contains("result.json", source, StringComparison.Ordinal);
        Assert.Contains("JsonSerializerContext", source, StringComparison.Ordinal);
    }

    [Fact]
    public void UiSmoke_UsesARealTrayOwnerAndTheProductBrokenShortcutFlow()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotShortcutSmoke.cs");

        Assert.Contains("UiValid", source, StringComparison.Ordinal);
        Assert.Contains("UiCancel", source, StringComparison.Ordinal);
        Assert.Contains("UiDelete", source, StringComparison.Ordinal);
        Assert.Contains("UiRepair", source, StringComparison.Ordinal);
        Assert.Contains("File.Move(targetPath, replacementPath)", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "File.WriteAllText(replacementPath",
            source,
            StringComparison.Ordinal);
        Assert.Contains("WindowNative.GetWindowHandle(_trayWindow)", source, StringComparison.Ordinal);
        Assert.Contains("ownerHwnd == IntPtr.Zero", source, StringComparison.Ordinal);
        Assert.Contains(
            "ShortcutHelper.ResolveBrokenShortcutWithShellUi(",
            source,
            StringComparison.Ordinal);
        Assert.Contains("AwaitingShellUi", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppLaunch_SchedulesTheOptInSmokeOnlyAfterSuccessfulInitialization()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml.cs");

        int completed = app.IndexOf(
            "Log(\"OnLaunched completed successfully\");",
            StringComparison.Ordinal);
        int smoke = app.IndexOf("StartAotShortcutSmokeIfRequested();", StringComparison.Ordinal);

        Assert.True(completed >= 0 && smoke > completed);
    }

    [Fact]
    public void SmokeScript_ScopesTheScenarioAndWaitsForStructuredEvidence()
    {
        string script = ReadRepositoryFile("scripts/run-aot-shortcut-smoke.ps1");

        Assert.Contains("DESKBOX_AOT_SHORTCUT_SMOKE", script, StringComparison.Ordinal);
        Assert.Contains("previousShortcutSmoke", script, StringComparison.Ordinal);
        Assert.Contains("finally", script, StringComparison.Ordinal);
        Assert.Contains("start-aot-preview.ps1", script, StringComparison.Ordinal);
        Assert.Contains("result.json", script, StringComparison.Ordinal);
        Assert.Contains("AwaitingShellUi", script, StringComparison.Ordinal);
        Assert.Contains("Completed", script, StringComparison.Ordinal);
        Assert.Contains("TimeoutSeconds", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", script, StringComparison.Ordinal);
        Assert.Contains("finally", script, StringComparison.Ordinal);
    }

    [Fact]
    public void SmokeScript_RejectsWrongBinaryOrRustEvidenceAndRechecksProductionData()
    {
        string script = ReadRepositoryFile("scripts/run-aot-shortcut-smoke.ps1");

        Assert.Contains("executableSha256", script, StringComparison.Ordinal);
        Assert.Contains("rustNativeSha256", script, StringComparison.Ordinal);
        Assert.Contains("ModuleSha256", script, StringComparison.Ordinal);
        Assert.Contains("ModulePath", script, StringComparison.Ordinal);
        Assert.Contains("Get-DirectoryStateFingerprint", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("Production data changed", script, StringComparison.Ordinal);
        Assert.Contains("session.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditAndProject_KeepStage5B1GatesUnderTheCurrentProfile()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B1SourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B1MissingRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B1MissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B1UnsafeRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B1ExpectedWmc1510Count = 1213", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("shortcut AOT-to-Rust smoke", project, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
