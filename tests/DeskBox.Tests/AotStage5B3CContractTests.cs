namespace DeskBox.Tests;

public sealed class AotStage5B3CContractTests
{
    [Fact]
    public void MusicVolumeSessionMutationSmoke_IsNativeAotOnlyAndRequiresControlledFixture()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotMusicVolumeSessionMutationSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains(
            "DESKBOX_AOT_MUSIC_VOLUME_SESSION_MUTATION_SMOKE",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DESKBOX_AOT_MUSIC_VOLUME_SESSION_FIXTURE_PID",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "deskbox-audio-session-fixture",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "DeskBoxDataPathService.AotPreviewRootEnvironmentVariable",
            source,
            StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", source, StringComparison.Ordinal);
        Assert.Contains("RefusedUntrustedFixture", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioSessionFixture_IsRustOwnedSilentAndParentBound()
    {
        string manifest = ReadRepositoryFile(
            "native/deskbox-audio-session-fixture/Cargo.toml");
        string source = ReadRepositoryFile(
            "native/deskbox-audio-session-fixture/src/main.rs");

        Assert.Contains("deskbox-audio-session-fixture", manifest, StringComparison.Ordinal);
        Assert.Contains("PlaySoundW", source, StringComparison.Ordinal);
        Assert.Contains("SND_LOOP", source, StringComparison.Ordinal);
        Assert.Contains("write_silent_wave", source, StringComparison.Ordinal);
        Assert.Contains("--parent-pid", source, StringComparison.Ordinal);
        Assert.Contains("PROCESS_SYNCHRONIZE", source, StringComparison.Ordinal);
        Assert.Contains("WaitForSingleObject", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeSessionMutationSmoke_CoversReadChangeFailureAndIndependentRecovery()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotMusicVolumeSessionMutationSmoke.cs");

        Assert.Contains("ReadMatchedSession", source, StringComparison.Ordinal);
        Assert.Contains("ChangeRestore", source, StringComparison.Ordinal);
        Assert.Contains("ChangeThenFail", source, StringComparison.Ordinal);
        Assert.Contains("ChangeThenAwaitExternalRecovery", source, StringComparison.Ordinal);
        Assert.Contains("RecoverOriginal", source, StringComparison.Ordinal);
        Assert.Contains("AwaitingExternalRecovery", source, StringComparison.Ordinal);
        Assert.Contains(
            "intentional-after-session-volume-change",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Timeout.InfiniteTimeSpan", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeSessionMutationSmoke_UsesProductSessionGetterAndSetterOnly()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotMusicVolumeSessionMutationSmoke.cs");

        Assert.Contains("new MusicVolumeService()", source, StringComparison.Ordinal);
        Assert.Contains("GetVolumeAsync(", source, StringComparison.Ordinal);
        Assert.Contains("TrySetSessionVolumeAsync(", source, StringComparison.Ordinal);
        Assert.Contains("MusicVolumeNativeBackend.GetSnapshot(", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MusicVolumeNativeBackend.SetSessionVolume",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TrySetSystemMasterVolumeAsync",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "MusicVolumeNativeBackend.SetSystemVolume",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeSessionMutationSmoke_PersistsIdentityAndOriginalBeforeMutation()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotMusicVolumeSessionMutationSmoke.cs");

        int persist = source.IndexOf(
            "PersistAndReadBackMusicVolumeSessionRecoveryIntent",
            StringComparison.Ordinal);
        int setter = source.IndexOf("TrySetSessionVolumeAsync(", StringComparison.Ordinal);
        Assert.True(persist >= 0 && setter > persist);
        Assert.Contains("session-recovery-intent.json", source, StringComparison.Ordinal);
        Assert.Contains("SourceAppUserModelId", source, StringComparison.Ordinal);
        Assert.Contains("SourceDisplayName", source, StringComparison.Ordinal);
        Assert.Contains("FixtureProcessId", source, StringComparison.Ordinal);
        Assert.Contains("OriginalSessionVolume", source, StringComparison.Ordinal);
        Assert.Contains("ProbeSessionVolume", source, StringComparison.Ordinal);
        Assert.Contains("File.Move(temporaryPath, path, overwrite: true)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeSessionMutationSmoke_RequiresMatchedSessionForRecoveryAndCleanup()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotMusicVolumeSessionMutationSmoke.cs");

        int restore = source.IndexOf(
            "RestoreOriginalMusicVolumeSessionAsync",
            StringComparison.Ordinal);
        int matched = source.IndexOf("recovery-original-session-verified", restore, StringComparison.Ordinal);
        int delete = source.IndexOf("File.Delete(recoveryIntentPath)", matched, StringComparison.Ordinal);
        Assert.True(restore >= 0 && matched > restore && delete > matched);
        Assert.Contains("HasSessionVolume", source, StringComparison.Ordinal);
        Assert.Contains("ExpectedSessionMatchKind", source, StringComparison.Ordinal);
        Assert.Contains("session-disappeared-intent-preserved", source, StringComparison.Ordinal);
        Assert.Contains("RecoveryIntentPreserved", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeSessionMutationSmoke_ProvesSystemVolumeNeverChanges()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotMusicVolumeSessionMutationSmoke.cs");

        Assert.Contains("InitialSystemVolume", source, StringComparison.Ordinal);
        Assert.Contains("FinalSystemVolume", source, StringComparison.Ordinal);
        Assert.Contains("SystemVolumeTolerance", source, StringComparison.Ordinal);
        Assert.Contains("system-volume-unchanged", source, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicVolumeSessionMutationSmoke_RecordsTrustedNativeAndGeneratedJsonEvidence()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotMusicVolumeSessionMutationSmoke.cs");

        Assert.Contains("ShortcutNativeBackend.CaptureDiagnosticState()", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeFeature.IsDynamicCodeSupported", source, StringComparison.Ordinal);
        Assert.Contains("OperationHResult", source, StringComparison.Ordinal);
        Assert.Contains("AttemptedPhases", source, StringComparison.Ordinal);
        Assert.Contains("MatchKind", source, StringComparison.Ordinal);
        Assert.Contains(
            "AotMusicVolumeSessionMutationSmokeJsonContext.Default.AotMusicVolumeSessionMutationSmokeResult",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "AotMusicVolumeSessionMutationSmokeJsonContext.Default.AotMusicVolumeSessionRecoveryIntent",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppLaunch_SchedulesTheOptInSessionMutationSmokeAfterOtherMusicSmokes()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml.cs");
        int systemMutation = app.IndexOf(
            "StartAotMusicVolumeMutationSmokeIfRequested();",
            StringComparison.Ordinal);
        int sessionMutation = app.IndexOf(
            "StartAotMusicVolumeSessionMutationSmokeIfRequested();",
            StringComparison.Ordinal);

        Assert.True(systemMutation >= 0 && sessionMutation > systemMutation);
    }

    [Fact]
    public void SessionMutationScript_OwnsFixtureAndSixPhaseRecoveryEvidence()
    {
        string script = ReadRepositoryFile(
            "scripts/run-aot-music-volume-session-mutation-smoke.ps1");

        Assert.Contains("deskbox-audio-session-fixture.exe", script, StringComparison.Ordinal);
        Assert.Contains("cargo", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-WindowStyle Hidden", script, StringComparison.Ordinal);
        Assert.Contains("fixtureProcess.Id", script, StringComparison.Ordinal);
        Assert.Contains("ChangeThenAwaitExternalRecovery", script, StringComparison.Ordinal);
        Assert.Contains("RecoverOriginal", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactFixtureProcess", script, StringComparison.Ordinal);
        Assert.Contains("postflight", script, StringComparison.Ordinal);
        Assert.Contains(
            "-Scenario \"RecoverOriginal\"",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "System master volume changed across the session-only recovery matrix",
            script,
            StringComparison.Ordinal);
        int postflightPhase = script.IndexOf("-Phase \"postflight\"", StringComparison.Ordinal);
        int postflightAssertion = script.IndexOf(
            "Assert-MusicVolumeSessionMutationResult",
            postflightPhase,
            StringComparison.Ordinal);
        int postflightVolumeCheck = script.IndexOf(
            "if ($null -ne $originalSessionVolume",
            postflightAssertion,
            StringComparison.Ordinal);
        Assert.True(
            postflightPhase >= 0 &&
            postflightAssertion > postflightPhase &&
            postflightVolumeCheck > postflightAssertion);
        Assert.DoesNotContain(
            "-RequireNoRecoveryIntent",
            script[postflightAssertion..postflightVolumeCheck],
            StringComparison.Ordinal);
        Assert.Contains("session.json", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("systemVolume", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryAotSmokeScript_ClearsAndRestoresAllSixOptIns()
    {
        string[] scripts =
        [
            "scripts/run-aot-shortcut-smoke.ps1",
            "scripts/run-aot-shell-smoke.ps1",
            "scripts/run-aot-quick-access-mutation-smoke.ps1",
            "scripts/run-aot-music-volume-read-smoke.ps1",
            "scripts/run-aot-music-volume-mutation-smoke.ps1",
            "scripts/run-aot-music-volume-session-mutation-smoke.ps1"
        ];
        string[] variables =
        [
            "DESKBOX_AOT_SHORTCUT_SMOKE",
            "DESKBOX_AOT_SHELL_SMOKE",
            "DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE",
            "DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE",
            "DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE",
            "DESKBOX_AOT_MUSIC_VOLUME_SESSION_MUTATION_SMOKE"
        ];
        string[] previousVariables =
        [
            "previousShortcutSmoke",
            "previousShellSmoke",
            "previousMutationSmoke",
            "previousMusicReadSmoke",
            "previousMusicMutationSmoke",
            "previousMusicSessionMutationSmoke"
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
    public void AuditProjectAndLauncher_AdvanceToStage5B3C()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3CSourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3CMissingRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3CUnsafeMutationPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3CMissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B3CExpectedWmc1510Count = 1241", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("session-volume", project, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
