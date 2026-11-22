#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private const string AotMusicVolumeSessionMutationSmokeEnvironmentVariable =
        "DESKBOX_AOT_MUSIC_VOLUME_SESSION_MUTATION_SMOKE";
    private const string AotMusicVolumeSessionFixturePidEnvironmentVariable =
        "DESKBOX_AOT_MUSIC_VOLUME_SESSION_FIXTURE_PID";
    private const string AotMusicVolumeSessionMutationSmokeDirectoryName =
        "aot-music-volume-session-mutation-smoke";
    private const string AotMusicVolumeSessionRecoveryIntentFileName =
        "session-recovery-intent.json";
    private const string ControlledFixtureProcessName =
        "deskbox-audio-session-fixture";
    private const string ControlledFixtureSourceAppUserModelId =
        "DeskBox.Aot.Controlled.Session.Identity";
    private const string ControlledFixtureSourceDisplayName =
        ControlledFixtureProcessName;
    private const uint ExpectedSessionMatchKind = 4;
    private const double MusicVolumeSessionMutationTolerance = 0.005;
    private const double MusicVolumeSessionSystemVolumeTolerance = 0.005;
    private const double MusicVolumeSessionProbeDelta = 0.08;

    private void StartAotMusicVolumeSessionMutationSmokeIfRequested()
    {
        string? configuredScenario = Environment.GetEnvironmentVariable(
            AotMusicVolumeSessionMutationSmokeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredScenario))
        {
            return;
        }

        if (!Enum.TryParse(
                configuredScenario.Trim(),
                ignoreCase: true,
                out AotMusicVolumeSessionMutationSmokeScenario scenario) ||
            !Enum.IsDefined(scenario))
        {
            Log(
                $"[AotMusicVolumeSessionMutationSmoke] Unsupported scenario " +
                $"'{configuredScenario}'.");
            return;
        }

        string? configuredFixturePid = Environment.GetEnvironmentVariable(
            AotMusicVolumeSessionFixturePidEnvironmentVariable);
        if (!int.TryParse(configuredFixturePid, out int fixtureProcessId) ||
            fixtureProcessId <= 0 ||
            !IsTrustedControlledAudioFixture(fixtureProcessId))
        {
            Log(
                "[AotMusicVolumeSessionMutationSmoke] RefusedUntrustedFixture: " +
                "the runner requires exactly one live controlled Rust audio fixture.");
            return;
        }

        _ = RunAotMusicVolumeSessionMutationSmokeAsync(scenario, fixtureProcessId);
    }

    private static bool IsTrustedControlledAudioFixture(int fixtureProcessId)
    {
        Process[] candidates = [];
        try
        {
            candidates = Process.GetProcessesByName(ControlledFixtureProcessName);
            return candidates.Length == 1 &&
                   candidates[0].Id == fixtureProcessId &&
                   !candidates[0].HasExited &&
                   string.Equals(
                       candidates[0].ProcessName,
                       ControlledFixtureProcessName,
                       StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
        finally
        {
            foreach (Process candidate in candidates)
            {
                candidate.Dispose();
            }
        }
    }

    private async Task RunAotMusicVolumeSessionMutationSmokeAsync(
        AotMusicVolumeSessionMutationSmokeScenario scenario,
        int fixtureProcessId)
    {
        await Task.Yield();

        DeskBoxDataPathService dataPaths = DeskBoxDataPathService.Current;
        string? configuredPreviewRoot = Environment.GetEnvironmentVariable(
            DeskBoxDataPathService.AotPreviewRootEnvironmentVariable);
        if (!dataPaths.IsDevelopmentRoot ||
            string.IsNullOrWhiteSpace(configuredPreviewRoot) ||
            !PathsEqual(dataPaths.RootPath, configuredPreviewRoot))
        {
            Log(
                "[AotMusicVolumeSessionMutationSmoke] RefusedNonPreviewRoot: " +
                "the smoke runner requires an explicit isolated Native AOT preview root.");
            return;
        }

        string mutationParent = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotMusicVolumeSessionMutationSmokeDirectoryName));
        string scenarioRoot = Path.GetFullPath(Path.Combine(
            mutationParent,
            GetMusicVolumeSessionMutationScenarioDirectoryName(scenario)));
        string recoveryIntentPath = Path.GetFullPath(Path.Combine(
            mutationParent,
            AotMusicVolumeSessionRecoveryIntentFileName));
        if (!IsPathEqualOrInside(dataPaths.RootPath, mutationParent) ||
            !IsPathEqualOrInside(mutationParent, scenarioRoot) ||
            !IsPathEqualOrInside(mutationParent, recoveryIntentPath) ||
            PathsEqual(mutationParent, scenarioRoot))
        {
            Log(
                "[AotMusicVolumeSessionMutationSmoke] Refused unsafe evidence paths; " +
                $"parent='{mutationParent}', scenario='{scenarioRoot}', " +
                $"intent='{recoveryIntentPath}'.");
            return;
        }

        Directory.CreateDirectory(mutationParent);
        if (Directory.Exists(scenarioRoot))
        {
            Directory.Delete(scenarioRoot, recursive: true);
        }
        Directory.CreateDirectory(scenarioRoot);

        string resultPath = Path.Combine(scenarioRoot, "result.json");
        var result = new AotMusicVolumeSessionMutationSmokeResult
        {
            SchemaVersion = 1,
            Scenario = scenario.ToString(),
            State = "Running",
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            ExecutablePath = Environment.ProcessPath,
            PreviewDataRoot = dataPaths.RootPath,
            EvidenceRoot = scenarioRoot,
            ResultPath = resultPath,
            RecoveryIntentPath = recoveryIntentPath,
            RecoveryIntentPreserved = File.Exists(recoveryIntentPath),
            FixtureProcessId = fixtureProcessId,
            FixtureProcessName = ControlledFixtureProcessName,
            SourceAppUserModelId = ControlledFixtureSourceAppUserModelId,
            SourceDisplayName = ControlledFixtureSourceDisplayName,
            IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            Steps = []
        };
        CaptureMusicVolumeSessionMutationNativeEvidence(result);
        WriteMusicVolumeSessionMutationJsonAtomically(
            resultPath,
            result,
            AotMusicVolumeSessionMutationSmokeJsonContext.Default.AotMusicVolumeSessionMutationSmokeResult);

        AotMusicVolumeSessionRecoveryIntent? activeIntent = null;
        Exception? scenarioFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            switch (scenario)
            {
                case AotMusicVolumeSessionMutationSmokeScenario.ReadMatchedSession:
                    await ReadMatchedMusicVolumeSessionAsync(
                        recoveryIntentPath,
                        result);
                    break;

                case AotMusicVolumeSessionMutationSmokeScenario.RecoverOriginal:
                    await RecoverOriginalMusicVolumeSessionAsync(
                        recoveryIntentPath,
                        dataPaths.RootPath,
                        fixtureProcessId,
                        result);
                    break;

                default:
                    activeIntent = await RunMusicVolumeSessionMutationAsync(
                        scenario,
                        recoveryIntentPath,
                        dataPaths.RootPath,
                        resultPath,
                        fixtureProcessId,
                        result);
                    break;
            }

            CaptureMusicVolumeSessionMutationNativeEvidence(result);
            RequireMusicVolumeSessionMutationRuntime(result);
        }
        catch (Exception ex)
        {
            scenarioFailure = ex;
            Log($"[AotMusicVolumeSessionMutationSmoke] Scenario {scenario} failed: {ex}");
        }
        finally
        {
            bool isMutationScenario = scenario is
                AotMusicVolumeSessionMutationSmokeScenario.ChangeRestore or
                AotMusicVolumeSessionMutationSmokeScenario.ChangeThenFail or
                AotMusicVolumeSessionMutationSmokeScenario.ChangeThenAwaitExternalRecovery;
            if (activeIntent is null &&
                isMutationScenario &&
                result.RecoveryIntentPersisted &&
                File.Exists(recoveryIntentPath))
            {
                try
                {
                    activeIntent = ReadMusicVolumeSessionMutationJson(
                        recoveryIntentPath,
                        AotMusicVolumeSessionMutationSmokeJsonContext.Default.AotMusicVolumeSessionRecoveryIntent);
                    ValidateMusicVolumeSessionRecoveryIntent(
                        activeIntent,
                        dataPaths.RootPath,
                        fixtureProcessId);
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                    result.CleanupSucceeded = false;
                    result.CleanupError = ex.ToString();
                    result.RecoveryIntentPreserved = true;
                }
            }

            if (activeIntent is not null && cleanupFailure is null)
            {
                try
                {
                    await RestoreOriginalMusicVolumeSessionAsync(
                        activeIntent,
                        recoveryIntentPath,
                        result);
                }
                catch (Exception ex)
                {
                    cleanupFailure = ex;
                    result.CleanupSucceeded = false;
                    result.CleanupError = ex.ToString();
                    result.RecoveryIntentPreserved = File.Exists(recoveryIntentPath);
                    Log(
                        "[AotMusicVolumeSessionMutationSmoke] Session-volume restoration " +
                        $"failed; original={activeIntent.OriginalSessionVolume}, " +
                        $"intent='{recoveryIntentPath}': {ex}");
                }
            }

            CaptureMusicVolumeSessionMutationNativeEvidence(result);
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            result.Success = scenarioFailure is null && cleanupFailure is null;
            result.State = result.Success ? "Completed" : "Failed";
            result.Error = CombineMusicVolumeSessionMutationErrors(
                scenarioFailure,
                cleanupFailure);
            result.RecoveryIntentPreserved = File.Exists(recoveryIntentPath);
            WriteMusicVolumeSessionMutationJsonAtomically(
                resultPath,
                result,
                AotMusicVolumeSessionMutationSmokeJsonContext.Default.AotMusicVolumeSessionMutationSmokeResult);
            Log(
                $"[AotMusicVolumeSessionMutationSmoke] Scenario={scenario} " +
                $"state={result.State} success={result.Success} " +
                $"cleanup={result.CleanupSucceeded} " +
                $"intentPreserved={result.RecoveryIntentPreserved} " +
                $"result='{resultPath}'");
        }
    }

    private static async Task ReadMatchedMusicVolumeSessionAsync(
        string recoveryIntentPath,
        AotMusicVolumeSessionMutationSmokeResult result)
    {
        result.RecoveryIntentFound = File.Exists(recoveryIntentPath);
        RequireMusicVolumeSessionMutation(
            result,
            !result.RecoveryIntentFound,
            "read-no-stale-recovery-intent",
            "A stale session recovery intent exists; read-only preflight refused it.");

        MusicVolumeSessionRead initial = await ReadHealthyMatchedMusicVolumeSessionAsync(
            result,
            "read-matched-session");
        result.NativeInitial = AotMusicVolumeNativeEvidence.From(initial.Native);
        result.NativeFinal = AotMusicVolumeNativeEvidence.From(initial.Native);
        result.InitialSystemVolume = initial.Native.SystemVolume;
        result.FinalSystemVolume = initial.Native.SystemVolume;
        result.OriginalSessionVolume = initial.Native.SessionVolume;
        result.FinalSessionVolume = initial.Native.SessionVolume;
        result.CleanupSucceeded = true;
        result.RecoveryIntentPreserved = false;
        RequireMusicVolumeSessionMutation(
            result,
            true,
            "read-matched-session-completed",
            string.Empty);
    }

    private static async Task<AotMusicVolumeSessionRecoveryIntent>
        RunMusicVolumeSessionMutationAsync(
            AotMusicVolumeSessionMutationSmokeScenario scenario,
            string recoveryIntentPath,
            string previewDataRoot,
            string resultPath,
            int fixtureProcessId,
            AotMusicVolumeSessionMutationSmokeResult result)
    {
        MusicVolumeSessionRead initial = await ReadHealthyMatchedMusicVolumeSessionAsync(
            result,
            "initial-session");
        result.NativeInitial = AotMusicVolumeNativeEvidence.From(initial.Native);
        result.InitialSystemVolume = initial.Native.SystemVolume;
        result.OriginalSessionVolume = initial.Native.SessionVolume;
        result.ProbeSessionVolume = SelectMusicVolumeSessionProbe(
            result.OriginalSessionVolume);
        RequireMusicVolumeSessionMutation(
            result,
            Math.Abs(
                result.ProbeSessionVolume - result.OriginalSessionVolume) >= 0.06 &&
            result.ProbeSessionVolume is > 0.0 and < 1.0,
            "probe-session-volume-separated",
            "The selected session probe is not a safe non-edge change.");

        var intent = new AotMusicVolumeSessionRecoveryIntent
        {
            SchemaVersion = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            PreviewDataRoot = previewDataRoot,
            SourceAppUserModelId = ControlledFixtureSourceAppUserModelId,
            SourceDisplayName = ControlledFixtureSourceDisplayName,
            FixtureProcessId = fixtureProcessId,
            InitialSystemVolume = result.InitialSystemVolume,
            OriginalSessionVolume = result.OriginalSessionVolume,
            ProbeSessionVolume = result.ProbeSessionVolume,
            OriginatingProcessId = Environment.ProcessId,
            OriginatingExecutablePath = Environment.ProcessPath
        };
        intent = PersistAndReadBackMusicVolumeSessionRecoveryIntent(
            recoveryIntentPath,
            intent,
            previewDataRoot,
            fixtureProcessId,
            result);

        var service = new MusicVolumeService();
        result.ProbeRequestSucceeded = await service.TrySetSessionVolumeAsync(
            intent.SourceAppUserModelId,
            intent.SourceDisplayName,
            intent.ProbeSessionVolume);
        RequireMusicVolumeSessionMutation(
            result,
            result.ProbeRequestSucceeded,
            "product-session-probe-setter-succeeded",
            $"The product session setter rejected probe {intent.ProbeSessionVolume}.");

        MusicVolumeNativeCallResult probe = await WaitForMusicVolumeSessionAsync(
            intent.SourceAppUserModelId,
            intent.SourceDisplayName,
            intent.ProbeSessionVolume,
            TimeSpan.FromSeconds(5));
        RequireHealthyMatchedNativeMusicVolumeSession(
            result,
            probe,
            "probe-session");
        MusicVolumeSnapshot productProbe = await service.GetVolumeAsync(
            intent.SourceAppUserModelId,
            intent.SourceDisplayName);
        RequireMatchingProductMusicVolumeSession(
            result,
            productProbe,
            probe,
            "product-probe-session");
        result.NativeProbe = AotMusicVolumeNativeEvidence.From(probe);
        result.ObservedProbeSessionVolume = probe.SessionVolume;
        RequireMusicVolumeSessionMutation(
            result,
            Math.Abs(probe.SessionVolume - intent.ProbeSessionVolume) <=
                MusicVolumeSessionMutationTolerance,
            "probe-session-volume-verified",
            $"The Rust getter observed {probe.SessionVolume}, expected " +
            $"{intent.ProbeSessionVolume}.");
        RequireSystemVolumeUnchanged(
            result,
            intent.InitialSystemVolume,
            probe.SystemVolume,
            "probe-system-volume-unchanged");
        CaptureMusicVolumeSessionMutationNativeEvidence(result);
        RequireMusicVolumeSessionMutationRuntime(result);

        switch (scenario)
        {
            case AotMusicVolumeSessionMutationSmokeScenario.ChangeRestore:
                break;

            case AotMusicVolumeSessionMutationSmokeScenario.ChangeThenFail:
                throw new InvalidOperationException(
                    "intentional-after-session-volume-change: " +
                    "exercising App finally recovery.");

            case AotMusicVolumeSessionMutationSmokeScenario
                .ChangeThenAwaitExternalRecovery:
                CaptureMusicVolumeSessionMutationNativeEvidence(result);
                RequireMusicVolumeSessionMutationRuntime(result);
                result.State = "AwaitingExternalRecovery";
                WriteMusicVolumeSessionMutationJsonAtomically(
                    resultPath,
                    result,
                    AotMusicVolumeSessionMutationSmokeJsonContext.Default.AotMusicVolumeSessionMutationSmokeResult);
                await Task.Delay(Timeout.InfiniteTimeSpan);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        return intent;
    }

    private static AotMusicVolumeSessionRecoveryIntent
        PersistAndReadBackMusicVolumeSessionRecoveryIntent(
            string recoveryIntentPath,
            AotMusicVolumeSessionRecoveryIntent intent,
            string previewDataRoot,
            int fixtureProcessId,
            AotMusicVolumeSessionMutationSmokeResult result)
    {
        WriteMusicVolumeSessionMutationJsonAtomically(
            recoveryIntentPath,
            intent,
            AotMusicVolumeSessionMutationSmokeJsonContext.Default.AotMusicVolumeSessionRecoveryIntent);
        result.RecoveryIntentPersisted = true;
        result.RecoveryIntentPreserved = true;

        AotMusicVolumeSessionRecoveryIntent persisted =
            ReadMusicVolumeSessionMutationJson(
                recoveryIntentPath,
                AotMusicVolumeSessionMutationSmokeJsonContext.Default.AotMusicVolumeSessionRecoveryIntent);
        ValidateMusicVolumeSessionRecoveryIntent(
            persisted,
            previewDataRoot,
            fixtureProcessId);
        RequireMusicVolumeSessionMutation(
            result,
            Math.Abs(
                persisted.OriginalSessionVolume - intent.OriginalSessionVolume) <=
                MusicVolumeSessionMutationTolerance &&
            Math.Abs(
                persisted.ProbeSessionVolume - intent.ProbeSessionVolume) <=
                MusicVolumeSessionMutationTolerance,
            "session-recovery-intent-read-back",
            "The durable session recovery intent did not round-trip before mutation.");
        return persisted;
    }

    private static async Task RecoverOriginalMusicVolumeSessionAsync(
        string recoveryIntentPath,
        string previewDataRoot,
        int fixtureProcessId,
        AotMusicVolumeSessionMutationSmokeResult result)
    {
        result.RecoveryIntentFound = File.Exists(recoveryIntentPath);
        if (!result.RecoveryIntentFound)
        {
            MusicVolumeSessionRead current =
                await ReadHealthyMatchedMusicVolumeSessionAsync(
                    result,
                    "recovery-no-intent-session");
            result.NativeFinal = AotMusicVolumeNativeEvidence.From(current.Native);
            result.InitialSystemVolume = current.Native.SystemVolume;
            result.FinalSystemVolume = current.Native.SystemVolume;
            result.OriginalSessionVolume = current.Native.SessionVolume;
            result.FinalSessionVolume = current.Native.SessionVolume;
            result.CleanupSucceeded = true;
            result.RecoveryIntentPreserved = false;
            RequireMusicVolumeSessionMutation(
                result,
                true,
                "recovery-no-intent",
                string.Empty);
            return;
        }

        AotMusicVolumeSessionRecoveryIntent intent =
            ReadMusicVolumeSessionMutationJson(
                recoveryIntentPath,
                AotMusicVolumeSessionMutationSmokeJsonContext.Default.AotMusicVolumeSessionRecoveryIntent);
        ValidateMusicVolumeSessionRecoveryIntent(
            intent,
            previewDataRoot,
            fixtureProcessId);
        result.RecoveryIntentLoaded = true;
        result.RecoveryIntentPreserved = true;
        result.InitialSystemVolume = intent.InitialSystemVolume;
        result.OriginalSessionVolume = intent.OriginalSessionVolume;
        result.ProbeSessionVolume = intent.ProbeSessionVolume;

        MusicVolumeSessionRead before = await ReadHealthyMatchedMusicVolumeSessionAsync(
            result,
            "recovery-current-session");
        result.RecoveryObservedSessionVolume = before.Native.SessionVolume;
        result.NativeRecoveryBefore = AotMusicVolumeNativeEvidence.From(before.Native);
        RequireSystemVolumeUnchanged(
            result,
            intent.InitialSystemVolume,
            before.Native.SystemVolume,
            "recovery-current-system-volume-unchanged");
        await RestoreOriginalMusicVolumeSessionAsync(
            intent,
            recoveryIntentPath,
            result);
    }

    private static async Task RestoreOriginalMusicVolumeSessionAsync(
        AotMusicVolumeSessionRecoveryIntent intent,
        string recoveryIntentPath,
        AotMusicVolumeSessionMutationSmokeResult result)
    {
        MusicVolumeNativeCallResult before = MusicVolumeNativeBackend.GetSnapshot(
            intent.SourceAppUserModelId,
            intent.SourceDisplayName);
        RequireHealthyMatchedNativeMusicVolumeSession(
            result,
            before,
            "recovery-before-restore-session");
        RequireSystemVolumeUnchanged(
            result,
            intent.InitialSystemVolume,
            before.SystemVolume,
            "recovery-before-restore-system-volume-unchanged");

        var service = new MusicVolumeService();
        result.RestoreRequestSucceeded = await service.TrySetSessionVolumeAsync(
            intent.SourceAppUserModelId,
            intent.SourceDisplayName,
            intent.OriginalSessionVolume);
        RequireMusicVolumeSessionMutation(
            result,
            result.RestoreRequestSucceeded,
            "product-session-restore-setter-succeeded",
            $"The product setter could not restore session volume " +
            $"{intent.OriginalSessionVolume}.");

        MusicVolumeNativeCallResult final = await WaitForMusicVolumeSessionAsync(
            intent.SourceAppUserModelId,
            intent.SourceDisplayName,
            intent.OriginalSessionVolume,
            TimeSpan.FromSeconds(5));
        RequireHealthyMatchedNativeMusicVolumeSession(
            result,
            final,
            "recovery-final-session");
        MusicVolumeSnapshot productFinal = await service.GetVolumeAsync(
            intent.SourceAppUserModelId,
            intent.SourceDisplayName);
        RequireMatchingProductMusicVolumeSession(
            result,
            productFinal,
            final,
            "product-recovery-final-session");
        result.NativeFinal = AotMusicVolumeNativeEvidence.From(final);
        result.FinalSystemVolume = final.SystemVolume;
        result.FinalSessionVolume = final.SessionVolume;
        RequireMusicVolumeSessionMutation(
            result,
            Math.Abs(final.SessionVolume - intent.OriginalSessionVolume) <=
                MusicVolumeSessionMutationTolerance,
            "recovery-original-session-verified",
            $"The Rust getter observed {final.SessionVolume}, expected restored " +
            $"session volume {intent.OriginalSessionVolume}.");
        RequireSystemVolumeUnchanged(
            result,
            intent.InitialSystemVolume,
            final.SystemVolume,
            "system-volume-unchanged");

        File.Delete(recoveryIntentPath);
        result.RecoveryIntentPreserved = false;
        result.CleanupSucceeded = true;
        RequireMusicVolumeSessionMutation(
            result,
            !File.Exists(recoveryIntentPath),
            "session-recovery-intent-cleared-after-verification",
            "The session recovery intent still exists after verified restoration.");
    }

    private static async Task<MusicVolumeSessionRead>
        ReadHealthyMatchedMusicVolumeSessionAsync(
            AotMusicVolumeSessionMutationSmokeResult result,
            string stepPrefix)
    {
        var service = new MusicVolumeService();
        MusicVolumeSnapshot product = await service.GetVolumeAsync(
            ControlledFixtureSourceAppUserModelId,
            ControlledFixtureSourceDisplayName);
        MusicVolumeNativeCallResult native = MusicVolumeNativeBackend.GetSnapshot(
            ControlledFixtureSourceAppUserModelId,
            ControlledFixtureSourceDisplayName);
        RequireHealthyMatchedNativeMusicVolumeSession(result, native, stepPrefix);
        RequireMatchingProductMusicVolumeSession(
            result,
            product,
            native,
            $"product-{stepPrefix}");
        return new MusicVolumeSessionRead(product, native);
    }

    private static async Task<MusicVolumeNativeCallResult>
        WaitForMusicVolumeSessionAsync(
            string sourceAppUserModelId,
            string sourceDisplayName,
            double expected,
            TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        MusicVolumeNativeCallResult latest;
        do
        {
            latest = MusicVolumeNativeBackend.GetSnapshot(
                sourceAppUserModelId,
                sourceDisplayName);
            if (latest.Success &&
                latest.HasSessionVolume &&
                latest.MatchKind == ExpectedSessionMatchKind &&
                Math.Abs(latest.SessionVolume - expected) <=
                    MusicVolumeSessionMutationTolerance)
            {
                return latest;
            }

            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        return latest;
    }

    private static void RequireHealthyMatchedNativeMusicVolumeSession(
        AotMusicVolumeSessionMutationSmokeResult result,
        MusicVolumeNativeCallResult native,
        string stepPrefix)
    {
        RequireMusicVolumeSessionMutation(
            result,
            native.Success &&
            native.Status == 0 &&
            native.OperationHResult >= 0 &&
            native.DeviceHResult >= 0 &&
            native.SystemHResult >= 0 &&
            native.SessionHResult >= 0,
            $"{stepPrefix}-hresults",
            "The Rust getter did not acquire a healthy endpoint and session.");
        RequireMusicVolumeSessionMutation(
            result,
            (native.AttemptedPhases & 0x3FU) == 0x3FU,
            $"{stepPrefix}-phases",
            $"The Rust getter attempted phases 0x{native.AttemptedPhases:X}.");
        if (!native.HasSessionVolume || native.MatchKind != ExpectedSessionMatchKind)
        {
            throw new InvalidOperationException(
                "session-disappeared-intent-preserved: the controlled fixture " +
                $"session was not matched by kind {ExpectedSessionMatchKind}; " +
                $"actual kind={native.MatchKind}, hasSession={native.HasSessionVolume}.");
        }
        result.Steps.Add($"{stepPrefix}-matched-session-kind-{ExpectedSessionMatchKind}");
        RequireMusicVolumeSessionMutation(
            result,
            double.IsFinite(native.SystemVolume) &&
            native.SystemVolume is >= 0.0 and <= 1.0 &&
            double.IsFinite(native.SessionVolume) &&
            native.SessionVolume is >= 0.0 and <= 1.0,
            $"{stepPrefix}-normalized",
            "The Rust getter returned a volume outside [0,1].");
    }

    private static void RequireMatchingProductMusicVolumeSession(
        AotMusicVolumeSessionMutationSmokeResult result,
        MusicVolumeSnapshot product,
        MusicVolumeNativeCallResult native,
        string stepPrefix)
    {
        RequireMusicVolumeSessionMutation(
            result,
            product.HasSessionVolume &&
            double.IsFinite(product.SystemVolume) &&
            double.IsFinite(product.SessionVolume) &&
            product.SystemVolume is >= 0.0 and <= 1.0 &&
            product.SessionVolume is >= 0.0 and <= 1.0,
            $"{stepPrefix}-normalized",
            "The product getter did not return a normalized matched session.");
        RequireMusicVolumeSessionMutation(
            result,
            Math.Abs(product.SystemVolume - native.SystemVolume) <=
                MusicVolumeSessionSystemVolumeTolerance &&
            Math.Abs(product.SessionVolume - native.SessionVolume) <=
                MusicVolumeSessionMutationTolerance,
            $"{stepPrefix}-agrees-with-native",
            "The product and direct Rust getters disagree.");
    }

    private static void RequireSystemVolumeUnchanged(
        AotMusicVolumeSessionMutationSmokeResult result,
        double initial,
        double current,
        string step)
    {
        RequireMusicVolumeSessionMutation(
            result,
            Math.Abs(current - initial) <= MusicVolumeSessionSystemVolumeTolerance,
            step,
            $"System volume changed from {initial} to {current} during a session-only test.");
    }

    private static double SelectMusicVolumeSessionProbe(double original) =>
        original <= 0.85
            ? Math.Min(0.95, original + MusicVolumeSessionProbeDelta)
            : Math.Max(0.05, original - MusicVolumeSessionProbeDelta);

    private static void ValidateMusicVolumeSessionRecoveryIntent(
        AotMusicVolumeSessionRecoveryIntent intent,
        string previewDataRoot,
        int fixtureProcessId)
    {
        if (intent.SchemaVersion != 1 ||
            !PathsEqual(intent.PreviewDataRoot, previewDataRoot) ||
            !string.Equals(
                intent.SourceAppUserModelId,
                ControlledFixtureSourceAppUserModelId,
                StringComparison.Ordinal) ||
            !string.Equals(
                intent.SourceDisplayName,
                ControlledFixtureSourceDisplayName,
                StringComparison.Ordinal) ||
            intent.FixtureProcessId != fixtureProcessId ||
            !IsTrustedControlledAudioFixture(fixtureProcessId) ||
            !double.IsFinite(intent.InitialSystemVolume) ||
            !double.IsFinite(intent.OriginalSessionVolume) ||
            !double.IsFinite(intent.ProbeSessionVolume) ||
            intent.InitialSystemVolume is < 0.0 or > 1.0 ||
            intent.OriginalSessionVolume is < 0.0 or > 1.0 ||
            intent.ProbeSessionVolume is <= 0.0 or >= 1.0 ||
            Math.Abs(
                intent.ProbeSessionVolume - intent.OriginalSessionVolume) < 0.06)
        {
            throw new InvalidDataException(
                "The durable music-volume session recovery intent is invalid; " +
                "it was preserved.");
        }
    }

    private static void CaptureMusicVolumeSessionMutationNativeEvidence(
        AotMusicVolumeSessionMutationSmokeResult result)
    {
        ShortcutNativeDiagnosticState diagnostic =
            ShortcutNativeBackend.CaptureDiagnosticState();
        result.SelectedBackend = MusicVolumeBackendPolicy.Current.ToString();
        result.ModuleName = diagnostic.ModuleName;
        result.ModuleSha256 = diagnostic.ModuleSha256;
        result.LoadAttempted = diagnostic.LoadAttempted;
        result.LoadState = diagnostic.LoadState;
        result.AbiVersion = diagnostic.AbiVersion;
        result.Capabilities = diagnostic.Capabilities;
        result.IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported;

        if (ShortcutNativeModule.TryGetCachedDefault(out ShortcutNativeLoadResult? cached) &&
            cached?.Success == true)
        {
            result.ModulePath = cached.Module!.ModulePath;
            result.ModuleHandle = $"0x{cached.Module.ModuleHandle:X}";
        }
        else
        {
            result.ModulePath = Path.Combine(
                AppContext.BaseDirectory,
                ShortcutNativeModule.DllName);
            result.ModuleHandle = "0x0";
        }

        if (diagnostic.LoadAttempted)
        {
            ShortcutNativeLoadResult defaultLoad = ShortcutNativeModule.Default;
            if (defaultLoad.Success)
            {
                result.ModulePath = defaultLoad.Module!.ModulePath;
                result.ModuleHandle = $"0x{defaultLoad.Module.ModuleHandle:X}";
            }
        }

        result.ExecutableSha256 = ComputeFileSha256(result.ExecutablePath);
    }

    private static void RequireMusicVolumeSessionMutationRuntime(
        AotMusicVolumeSessionMutationSmokeResult result)
    {
        RequireMusicVolumeSessionMutation(
            result,
            !result.IsDynamicCodeSupported,
            "runtime-native-aot",
            "Native AOT unexpectedly reported dynamic-code support.");
        RequireMusicVolumeSessionMutation(
            result,
            string.Equals(result.SelectedBackend, "Rust", StringComparison.Ordinal),
            "music-volume-backend-rust",
            $"Expected Rust, found '{result.SelectedBackend}'.");
        RequireMusicVolumeSessionMutation(
            result,
            string.Equals(result.LoadState, "Loaded", StringComparison.Ordinal),
            "module-loaded",
            $"Expected Loaded, found '{result.LoadState}'.");
        RequireMusicVolumeSessionMutation(
            result,
            !string.IsNullOrWhiteSpace(result.ModuleHandle) &&
            result.ModuleHandle != "0x0",
            "module-handle",
            "Native module handle is zero.");
        RequireMusicVolumeSessionMutation(
            result,
            result.AbiVersion == 2,
            "module-abi",
            $"Unexpected ABI {result.AbiVersion}.");
        RequireMusicVolumeSessionMutation(
            result,
            (result.Capabilities.GetValueOrDefault() &
             ShortcutNativeModule.MusicVolumeCapability) != 0,
            "module-music-volume-capability",
            $"Music-volume capability is absent from " +
            $"0x{result.Capabilities.GetValueOrDefault():X}.");
    }

    private static void RequireMusicVolumeSessionMutation(
        AotMusicVolumeSessionMutationSmokeResult result,
        bool condition,
        string step,
        string failureMessage)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{step}: {failureMessage}");
        }

        result.Steps.Add(step);
    }

    private static void WriteMusicVolumeSessionMutationJsonAtomically<T>(
        string path,
        T value,
        JsonTypeInfo<T> jsonTypeInfo)
    {
        string temporaryPath = path + ".tmp";
        string json = JsonSerializer.Serialize(value, jsonTypeInfo);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static T ReadMusicVolumeSessionMutationJson<T>(
        string path,
        JsonTypeInfo<T> jsonTypeInfo) where T : class =>
        JsonSerializer.Deserialize(File.ReadAllText(path), jsonTypeInfo) ??
        throw new InvalidDataException($"JSON evidence '{path}' was empty.");

    private static string? CombineMusicVolumeSessionMutationErrors(
        Exception? scenarioFailure,
        Exception? cleanupFailure)
    {
        if (scenarioFailure is null)
        {
            return cleanupFailure?.ToString();
        }

        if (cleanupFailure is null)
        {
            return scenarioFailure.ToString();
        }

        return $"Scenario failure:{Environment.NewLine}{scenarioFailure}" +
               $"{Environment.NewLine}Recovery failure:{Environment.NewLine}{cleanupFailure}";
    }

    private static string GetMusicVolumeSessionMutationScenarioDirectoryName(
        AotMusicVolumeSessionMutationSmokeScenario scenario) =>
        scenario switch
        {
            AotMusicVolumeSessionMutationSmokeScenario.ReadMatchedSession =>
                "read-matched-session",
            AotMusicVolumeSessionMutationSmokeScenario.ChangeRestore =>
                "change-restore",
            AotMusicVolumeSessionMutationSmokeScenario.ChangeThenFail =>
                "change-then-fail",
            AotMusicVolumeSessionMutationSmokeScenario
                .ChangeThenAwaitExternalRecovery =>
                "change-then-await-external-recovery",
            AotMusicVolumeSessionMutationSmokeScenario.RecoverOriginal =>
                "recover-original",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
}

internal enum AotMusicVolumeSessionMutationSmokeScenario
{
    ReadMatchedSession,
    ChangeRestore,
    ChangeThenFail,
    ChangeThenAwaitExternalRecovery,
    RecoverOriginal
}

internal sealed record MusicVolumeSessionRead(
    MusicVolumeSnapshot Product,
    MusicVolumeNativeCallResult Native);

internal sealed class AotMusicVolumeSessionRecoveryIntent
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string PreviewDataRoot { get; set; } = string.Empty;
    public string SourceAppUserModelId { get; set; } = string.Empty;
    public string SourceDisplayName { get; set; } = string.Empty;
    public int FixtureProcessId { get; set; }
    public double InitialSystemVolume { get; set; }
    public double OriginalSessionVolume { get; set; }
    public double ProbeSessionVolume { get; set; }
    public int OriginatingProcessId { get; set; }
    public string? OriginatingExecutablePath { get; set; }
}

internal sealed class AotMusicVolumeSessionMutationSmokeResult
{
    public int SchemaVersion { get; set; }
    public string Scenario { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public bool Success { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int ProcessId { get; set; }
    public string? ExecutablePath { get; set; }
    public string? ExecutableSha256 { get; set; }
    public string PreviewDataRoot { get; set; } = string.Empty;
    public string EvidenceRoot { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public string RecoveryIntentPath { get; set; } = string.Empty;
    public bool RecoveryIntentPersisted { get; set; }
    public bool RecoveryIntentFound { get; set; }
    public bool RecoveryIntentLoaded { get; set; }
    public bool RecoveryIntentPreserved { get; set; }
    public int FixtureProcessId { get; set; }
    public string FixtureProcessName { get; set; } = string.Empty;
    public string SourceAppUserModelId { get; set; } = string.Empty;
    public string SourceDisplayName { get; set; } = string.Empty;
    public bool IsDynamicCodeSupported { get; set; }
    public string? SelectedBackend { get; set; }
    public string? ModuleName { get; set; }
    public string? ModulePath { get; set; }
    public string? ModuleSha256 { get; set; }
    public string? ModuleHandle { get; set; }
    public bool LoadAttempted { get; set; }
    public string? LoadState { get; set; }
    public uint? AbiVersion { get; set; }
    public ulong? Capabilities { get; set; }
    public double InitialSystemVolume { get; set; }
    public double FinalSystemVolume { get; set; }
    public double OriginalSessionVolume { get; set; }
    public double ProbeSessionVolume { get; set; }
    public double ObservedProbeSessionVolume { get; set; }
    public double RecoveryObservedSessionVolume { get; set; }
    public double FinalSessionVolume { get; set; }
    public bool ProbeRequestSucceeded { get; set; }
    public bool RestoreRequestSucceeded { get; set; }
    public bool CleanupSucceeded { get; set; }
    public string? CleanupError { get; set; }
    public AotMusicVolumeNativeEvidence? NativeInitial { get; set; }
    public AotMusicVolumeNativeEvidence? NativeProbe { get; set; }
    public AotMusicVolumeNativeEvidence? NativeRecoveryBefore { get; set; }
    public AotMusicVolumeNativeEvidence? NativeFinal { get; set; }
    public List<string> Steps { get; set; } = [];
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(AotMusicVolumeSessionMutationSmokeResult),
    TypeInfoPropertyName = "AotMusicVolumeSessionMutationSmokeResult")]
[JsonSerializable(
    typeof(AotMusicVolumeSessionRecoveryIntent),
    TypeInfoPropertyName = "AotMusicVolumeSessionRecoveryIntent")]
internal partial class AotMusicVolumeSessionMutationSmokeJsonContext :
    JsonSerializerContext
{
}
#endif
