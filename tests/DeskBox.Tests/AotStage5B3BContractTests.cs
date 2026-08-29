namespace DeskBox.Tests;

public sealed class AotStage5B3BContractTests
{
    [Fact]
    public void MusicVolumeMutationSmoke_IsNativeAotOnlyAndRequiresThePreviewRoot()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotMusicVolumeMutationSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE", source, StringComparison.Ordinal);
        Assert.Contains("DeskBoxDataPathService.AotPreviewRootEnvironmentVariable", source, StringComparison.Ordinal);
        Assert.Contains("IsDevelopmentRoot", source, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", source, StringComparison.Ordinal);
        Assert.Contains("aot-music-volume-mutation-smoke", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeMutationSmoke_CoversNormalFailureForcedTerminationAndRecovery()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotMusicVolumeMutationSmoke.cs");

        Assert.Contains("ChangeRestore", source, StringComparison.Ordinal);
        Assert.Contains("ChangeThenFail", source, StringComparison.Ordinal);
        Assert.Contains("ChangeThenAwaitExternalRecovery", source, StringComparison.Ordinal);
        Assert.Contains("RecoverOriginal", source, StringComparison.Ordinal);
        Assert.Contains("AwaitingExternalRecovery", source, StringComparison.Ordinal);
        Assert.Contains("intentional-after-system-volume-change", source, StringComparison.Ordinal);
        Assert.Contains("Timeout.InfiniteTimeSpan", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeMutationSmoke_UsesOnlyTheProductSystemSetterAndDetailedNativeGetter()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotMusicVolumeMutationSmoke.cs");

        Assert.Contains("new MusicVolumeService()", source, StringComparison.Ordinal);
        Assert.Contains("TrySetSystemMasterVolumeAsync", source, StringComparison.Ordinal);
        Assert.Contains("MusicVolumeNativeBackend.GetSystemVolume()", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MusicVolumeNativeBackend.SetSystemVolume", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySetSessionVolumeAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MusicVolumeNativeBackend.SetSessionVolume", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeMutationSmoke_PersistsAndReadBacksIntentBeforeChangingVolume()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotMusicVolumeMutationSmoke.cs");

        int persist = source.IndexOf("PersistAndReadBackMusicVolumeRecoveryIntent", StringComparison.Ordinal);
        int setter = source.IndexOf("TrySetSystemMasterVolumeAsync", StringComparison.Ordinal);
        Assert.True(persist >= 0 && setter > persist);
        Assert.Contains("recovery-intent.json", source, StringComparison.Ordinal);
        Assert.Contains("OriginalVolume", source, StringComparison.Ordinal);
        Assert.Contains("ProbeVolume", source, StringComparison.Ordinal);
        Assert.Contains("MusicVolumeMutationTolerance", source, StringComparison.Ordinal);
        Assert.Contains("File.Move(temporaryPath, path, overwrite: true)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeMutationSmoke_DeletesIntentOnlyAfterVerifiedProductRecovery()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotMusicVolumeMutationSmoke.cs");

        int recoverySetter = source.IndexOf("RestoreOriginalMusicVolumeAsync", StringComparison.Ordinal);
        int recoveredStep = source.IndexOf("recovery-original-verified", recoverySetter, StringComparison.Ordinal);
        int delete = source.IndexOf("File.Delete(recoveryIntentPath)", recoveredStep, StringComparison.Ordinal);
        Assert.True(recoverySetter >= 0 && recoveredStep > recoverySetter && delete > recoveredStep);
        Assert.Contains("CleanupSucceeded", source, StringComparison.Ordinal);
        Assert.Contains("RecoveryIntentPreserved", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeMutationSmoke_RecordsTrustedNativeAndSourceGeneratedEvidence()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotMusicVolumeMutationSmoke.cs");

        Assert.Contains("ShortcutNativeBackend.CaptureDiagnosticState()", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeFeature.IsDynamicCodeSupported", source, StringComparison.Ordinal);
        Assert.Contains("OperationHResult", source, StringComparison.Ordinal);
        Assert.Contains("AttemptedPhases", source, StringComparison.Ordinal);
        Assert.Contains("AotMusicVolumeMutationSmokeJsonContext.Default.AotMusicVolumeMutationSmokeResult", source, StringComparison.Ordinal);
        Assert.Contains("AotMusicVolumeMutationSmokeJsonContext.Default.AotMusicVolumeRecoveryIntent", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppLaunch_SchedulesTheOptInMusicMutationSmokeAfterSuccessfulInitialization()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml.cs");
        int completed = app.IndexOf("Log(\"OnLaunched completed successfully\");", StringComparison.Ordinal);
        int smoke = app.IndexOf("StartAotMusicVolumeMutationSmokeIfRequested();", StringComparison.Ordinal);

        Assert.True(completed >= 0 && smoke > completed);
    }

    [Fact]
    public void MusicVolumeMutationScript_RequiresIndependentRecoveryAndTrustedEvidence()
    {
        string script = ReadRepositoryFile("scripts/run-aot-music-volume-mutation-smoke.ps1");

        Assert.Contains("ChangeRestore", script, StringComparison.Ordinal);
        Assert.Contains("ChangeThenFail", script, StringComparison.Ordinal);
        Assert.Contains("ChangeThenAwaitExternalRecovery", script, StringComparison.Ordinal);
        Assert.Contains("RecoverOriginal", script, StringComparison.Ordinal);
        Assert.Contains("recovery-intent.json", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", script, StringComparison.Ordinal);
        Assert.Contains("postflight", script, StringComparison.Ordinal);
        Assert.Contains("executableSha256", script, StringComparison.Ordinal);
        Assert.Contains("rustNativeSha256", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("Original system volume", script, StringComparison.Ordinal);
        Assert.Contains("session.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAotSmokeScript_ClearsAndRestoresAllFiveOptIns()
    {
        string[] scripts =
        [
            "scripts/run-aot-shortcut-smoke.ps1",
            "scripts/run-aot-shell-smoke.ps1",
            "scripts/run-aot-quick-access-mutation-smoke.ps1",
            "scripts/run-aot-music-volume-read-smoke.ps1",
            "scripts/run-aot-music-volume-mutation-smoke.ps1"
        ];
        string[] variables =
        [
            "DESKBOX_AOT_SHORTCUT_SMOKE",
            "DESKBOX_AOT_SHELL_SMOKE",
            "DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE",
            "DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE",
            "DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE"
        ];
        string[] previousVariables =
        [
            "previousShortcutSmoke",
            "previousShellSmoke",
            "previousMutationSmoke",
            "previousMusicReadSmoke",
            "previousMusicMutationSmoke"
        ];

        foreach (string path in scripts)
        {
            string script = ReadRepositoryFile(path);
            foreach (string variable in variables)
            {
                Assert.Contains(variable, script, StringComparison.Ordinal);
            }
            foreach (string previousVariable in previousVariables)
            {
                Assert.Contains(previousVariable, script, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AuditProjectAndLauncher_AdvanceToStage5B3B()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3BSourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3BMissingRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3BUnsafeMutationPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3BMissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3BExpectedWmc1510Count = 1213", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("system master-volume setter", project, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
