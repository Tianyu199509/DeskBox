namespace DeskBox.Tests;

public sealed class AotStage5B3AContractTests
{
    [Fact]
    public void MusicVolumeReadSmoke_IsNativeAotOnlyAndRequiresThePreviewRoot()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotMusicVolumeReadSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE", source, StringComparison.Ordinal);
        Assert.Contains(
            "DeskBoxDataPathService.AotPreviewRootEnvironmentVariable",
            source,
            StringComparison.Ordinal);
        Assert.Contains("IsDevelopmentRoot", source, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", source, StringComparison.Ordinal);
        Assert.Contains("aot-music-volume-read-smoke", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeReadSmoke_UsesBothProductGettersAndTheDetailedNativeReadBoundary()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotMusicVolumeReadSmoke.cs");

        Assert.Contains("new MusicVolumeService()", source, StringComparison.Ordinal);
        Assert.Contains("GetSystemMasterVolumeAsync()", source, StringComparison.Ordinal);
        Assert.Contains("GetVolumeAsync(", source, StringComparison.Ordinal);
        Assert.Contains("MusicVolumeNativeBackend.GetSystemVolume()", source, StringComparison.Ordinal);
        Assert.Contains("MusicVolumeNativeBackend.GetSnapshot(", source, StringComparison.Ordinal);
        Assert.Contains("MusicVolumeBackendPolicy.Current", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeReadSmoke_RejectsEverySystemAndSessionSetter()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotMusicVolumeReadSmoke.cs");

        Assert.DoesNotContain("TrySetSystemMasterVolumeAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySetSessionVolumeAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MusicVolumeNativeBackend.SetSystemVolume", source, StringComparison.Ordinal);
        Assert.DoesNotContain("MusicVolumeNativeBackend.SetSessionVolume", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeReadSmoke_RequiresRealNativeSuccessAndCapturesEndpointEvidence()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotMusicVolumeReadSmoke.cs");

        Assert.Contains("nativeSystem.Success", source, StringComparison.Ordinal);
        Assert.Contains("nativeSnapshot.Success", source, StringComparison.Ordinal);
        Assert.Contains("AttemptedPhases", source, StringComparison.Ordinal);
        Assert.Contains("OperationHResult", source, StringComparison.Ordinal);
        Assert.Contains("DeviceHResult", source, StringComparison.Ordinal);
        Assert.Contains("SystemHResult", source, StringComparison.Ordinal);
        Assert.Contains("SessionHResult", source, StringComparison.Ordinal);
        Assert.Contains("MatchKind", source, StringComparison.Ordinal);
        Assert.Contains("default-audio-endpoint", source, StringComparison.Ordinal);
        Assert.Contains("double.IsFinite", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeReadSmoke_ProvesNoSystemVolumeMutationAcrossTheScenario()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotMusicVolumeReadSmoke.cs");

        Assert.Contains("NativeSystemVolumeBefore", source, StringComparison.Ordinal);
        Assert.Contains("NativeSystemVolumeAfter", source, StringComparison.Ordinal);
        Assert.Contains("SystemVolumeTolerance", source, StringComparison.Ordinal);
        Assert.Contains("system-volume-unchanged", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeReadSmoke_RecordsAuditedBinaryAndSourceGeneratedJsonEvidence()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotMusicVolumeReadSmoke.cs");

        Assert.Contains("ShortcutNativeBackend.CaptureDiagnosticState()", source, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeModule.Default", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeFeature.IsDynamicCodeSupported", source, StringComparison.Ordinal);
        Assert.Contains("ModulePath", source, StringComparison.Ordinal);
        Assert.Contains("ModuleHandle", source, StringComparison.Ordinal);
        Assert.Contains("ModuleSha256", source, StringComparison.Ordinal);
        Assert.Contains("ExecutableSha256", source, StringComparison.Ordinal);
        Assert.Contains("AotMusicVolumeReadSmokeJsonContext.Default.AotMusicVolumeReadSmokeResult", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppLaunch_SchedulesTheOptInMusicReadSmokeAfterSuccessfulInitialization()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml.cs");

        int completed = app.IndexOf(
            "Log(\"OnLaunched completed successfully\");",
            StringComparison.Ordinal);
        int smoke = app.IndexOf(
            "StartAotMusicVolumeReadSmokeIfRequested();",
            StringComparison.Ordinal);

        Assert.True(completed >= 0 && smoke > completed);
    }

    [Fact]
    public void MusicVolumeReadSmokeScript_IsolatesOtherSmokesAndValidatesTrustedEvidence()
    {
        string script = ReadRepositoryFile("scripts/run-aot-music-volume-read-smoke.ps1");

        Assert.Contains("DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE", script, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_SHORTCUT_SMOKE", script, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_SHELL_SMOKE", script, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE", script, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_MUSIC_VOLUME_BACKEND", script, StringComparison.Ordinal);
        Assert.Contains("start-aot-preview.ps1", script, StringComparison.Ordinal);
        Assert.Contains("executableSha256", script, StringComparison.Ordinal);
        Assert.Contains("rustNativeSha256", script, StringComparison.Ordinal);
        Assert.Contains("nativeSystemVolumeBefore", script, StringComparison.Ordinal);
        Assert.Contains("nativeSystemVolumeAfter", script, StringComparison.Ordinal);
        Assert.Contains("Get-DirectoryStateFingerprint", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", script, StringComparison.Ordinal);
        Assert.Contains("session.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAotSmokeScript_ClearsAndRestoresEveryOtherOptIn()
    {
        string[] scripts =
        [
            "scripts/run-aot-shortcut-smoke.ps1",
            "scripts/run-aot-shell-smoke.ps1",
            "scripts/run-aot-quick-access-mutation-smoke.ps1"
        ];
        string[] environmentVariables =
        [
            "DESKBOX_AOT_SHORTCUT_SMOKE",
            "DESKBOX_AOT_SHELL_SMOKE",
            "DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE",
            "DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE"
        ];
        string[] previousVariables =
        [
            "previousShortcutSmoke",
            "previousShellSmoke",
            "previousMutationSmoke",
            "previousMusicReadSmoke"
        ];

        foreach (string path in scripts)
        {
            string script = ReadRepositoryFile(path);
            foreach (string environmentVariable in environmentVariables)
            {
                Assert.Contains(environmentVariable, script, StringComparison.Ordinal);
            }
            foreach (string previousVariable in previousVariables)
            {
                Assert.Contains(previousVariable, script, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void AuditProjectAndLauncher_AdvanceToStage5B3A()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3ASourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3AMissingRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3AUnsafeMutationPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3AMissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3AExpectedWmc1510Count = 1241", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("music-volume read-only smoke", project, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
