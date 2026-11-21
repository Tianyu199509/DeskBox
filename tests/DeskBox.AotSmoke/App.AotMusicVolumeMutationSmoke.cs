#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private const string AotMusicVolumeMutationSmokeEnvironmentVariable =
        "DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE";
    private const string AotMusicVolumeMutationSmokeDirectoryName =
        "aot-music-volume-mutation-smoke";
    private const string AotMusicVolumeRecoveryIntentFileName = "recovery-intent.json";
    private const double MusicVolumeMutationTolerance = 0.005;
    private const double MusicVolumeProbeDelta = 0.05;

    private void StartAotMusicVolumeMutationSmokeIfRequested()
    {
        string? configuredScenario = Environment.GetEnvironmentVariable(
            AotMusicVolumeMutationSmokeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredScenario))
        {
            return;
        }

        if (!Enum.TryParse(
                configuredScenario.Trim(),
                ignoreCase: true,
                out AotMusicVolumeMutationSmokeScenario scenario) ||
            !Enum.IsDefined(scenario))
        {
            Log(
                $"[AotMusicVolumeMutationSmoke] Unsupported scenario " +
                $"'{configuredScenario}'.");
            return;
        }

        _ = RunAotMusicVolumeMutationSmokeAsync(scenario);
    }

    private async Task RunAotMusicVolumeMutationSmokeAsync(
        AotMusicVolumeMutationSmokeScenario scenario)
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
                "[AotMusicVolumeMutationSmoke] RefusedNonPreviewRoot: the smoke runner " +
                "requires an explicit isolated Native AOT preview root.");
            return;
        }

        string mutationParent = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotMusicVolumeMutationSmokeDirectoryName));
        string scenarioRoot = Path.GetFullPath(Path.Combine(
            mutationParent,
            GetMusicVolumeMutationScenarioDirectoryName(scenario)));
        string recoveryIntentPath = Path.GetFullPath(Path.Combine(
            mutationParent,
            AotMusicVolumeRecoveryIntentFileName));
        if (!IsPathEqualOrInside(dataPaths.RootPath, mutationParent) ||
            !IsPathEqualOrInside(mutationParent, scenarioRoot) ||
            !IsPathEqualOrInside(mutationParent, recoveryIntentPath) ||
            PathsEqual(mutationParent, scenarioRoot))
        {
            Log(
                $"[AotMusicVolumeMutationSmoke] Refused unsafe evidence paths; " +
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
        var result = new AotMusicVolumeMutationSmokeResult
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
            IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            Steps = []
        };
        CaptureMusicVolumeMutationNativeEvidence(result);
        WriteMusicVolumeMutationJsonAtomically(
            resultPath,
            result,
            AotMusicVolumeMutationSmokeJsonContext.Default.AotMusicVolumeMutationSmokeResult);

        AotMusicVolumeRecoveryIntent? activeIntent = null;
        Exception? scenarioFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            if (scenario == AotMusicVolumeMutationSmokeScenario.RecoverOriginal)
            {
                await RecoverOriginalMusicVolumeAsync(
                    recoveryIntentPath,
                    dataPaths.RootPath,
                    result);
            }
            else
            {
                activeIntent = await RunMusicVolumeMutationAsync(
                    scenario,
                    recoveryIntentPath,
                    dataPaths.RootPath,
                    resultPath,
                    result);
            }

            CaptureMusicVolumeMutationNativeEvidence(result);
            RequireMusicVolumeMutationRuntime(result);
        }
        catch (Exception ex)
        {
            scenarioFailure = ex;
            Log($"[AotMusicVolumeMutationSmoke] Scenario {scenario} failed: {ex}");
        }
        finally
        {
            if (activeIntent is null &&
                scenario != AotMusicVolumeMutationSmokeScenario.RecoverOriginal &&
                result.RecoveryIntentPersisted &&
                File.Exists(recoveryIntentPath))
            {
                try
                {
                    activeIntent = ReadMusicVolumeMutationJson(
                        recoveryIntentPath,
                        AotMusicVolumeMutationSmokeJsonContext.Default.AotMusicVolumeRecoveryIntent);
                    ValidateMusicVolumeRecoveryIntent(activeIntent, dataPaths.RootPath);
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
                    await RestoreOriginalMusicVolumeAsync(
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
                        "[AotMusicVolumeMutationSmoke] System-volume restoration failed; " +
                        $"original={activeIntent.OriginalVolume}, " +
                        $"intent='{recoveryIntentPath}': {ex}");
                }
            }

            CaptureMusicVolumeMutationNativeEvidence(result);
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            result.Success = scenarioFailure is null && cleanupFailure is null;
            result.State = result.Success ? "Completed" : "Failed";
            result.Error = CombineMusicVolumeMutationErrors(
                scenarioFailure,
                cleanupFailure);
            result.RecoveryIntentPreserved = File.Exists(recoveryIntentPath);
            WriteMusicVolumeMutationJsonAtomically(
                resultPath,
                result,
                AotMusicVolumeMutationSmokeJsonContext.Default.AotMusicVolumeMutationSmokeResult);
            Log(
                $"[AotMusicVolumeMutationSmoke] Scenario={scenario} state={result.State} " +
                $"success={result.Success} cleanup={result.CleanupSucceeded} " +
                $"intentPreserved={result.RecoveryIntentPreserved} result='{resultPath}'");
        }
    }

    private static async Task<AotMusicVolumeRecoveryIntent> RunMusicVolumeMutationAsync(
        AotMusicVolumeMutationSmokeScenario scenario,
        string recoveryIntentPath,
        string previewDataRoot,
        string resultPath,
        AotMusicVolumeMutationSmokeResult result)
    {
        MusicVolumeNativeCallResult initial = ReadHealthySystemVolume(
            result,
            "initial-system-volume");
        result.NativeInitial = AotMusicVolumeNativeEvidence.From(initial);
        result.OriginalVolume = initial.SystemVolume;
        result.ProbeVolume = SelectMusicVolumeProbe(initial.SystemVolume);
        RequireMusicVolumeMutation(
            result,
            Math.Abs(result.ProbeVolume - result.OriginalVolume) >= 0.04 &&
            result.ProbeVolume is > 0.0 and < 1.0,
            "probe-volume-separated",
            "The selected probe volume is not a safe non-edge change.");

        var intent = new AotMusicVolumeRecoveryIntent
        {
            SchemaVersion = 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            PreviewDataRoot = previewDataRoot,
            OriginalVolume = result.OriginalVolume,
            ProbeVolume = result.ProbeVolume,
            OriginatingProcessId = Environment.ProcessId,
            OriginatingExecutablePath = Environment.ProcessPath
        };
        intent = PersistAndReadBackMusicVolumeRecoveryIntent(
            recoveryIntentPath,
            intent,
            previewDataRoot,
            result);

        var service = new MusicVolumeService();
        result.ProbeRequestSucceeded =
            await service.TrySetSystemMasterVolumeAsync(intent.ProbeVolume);
        RequireMusicVolumeMutation(
            result,
            result.ProbeRequestSucceeded,
            "product-probe-setter-succeeded",
            $"The product system-volume setter rejected probe {intent.ProbeVolume}.");

        MusicVolumeNativeCallResult probe = await WaitForMusicVolumeAsync(
            intent.ProbeVolume,
            TimeSpan.FromSeconds(5));
        result.NativeProbe = AotMusicVolumeNativeEvidence.From(probe);
        result.ObservedProbeVolume = probe.SystemVolume;
        RequireHealthyMusicVolumeRead(result, probe, "probe-system-volume");
        RequireMusicVolumeMutation(
            result,
            Math.Abs(probe.SystemVolume - intent.ProbeVolume) <=
                MusicVolumeMutationTolerance,
            "probe-volume-verified",
            $"The Rust getter observed {probe.SystemVolume}, expected {intent.ProbeVolume}.");
        CaptureMusicVolumeMutationNativeEvidence(result);
        RequireMusicVolumeMutationRuntime(result);

        switch (scenario)
        {
            case AotMusicVolumeMutationSmokeScenario.ChangeRestore:
                break;

            case AotMusicVolumeMutationSmokeScenario.ChangeThenFail:
                throw new InvalidOperationException(
                    "intentional-after-system-volume-change: exercising App finally recovery.");

            case AotMusicVolumeMutationSmokeScenario.ChangeThenAwaitExternalRecovery:
                CaptureMusicVolumeMutationNativeEvidence(result);
                RequireMusicVolumeMutationRuntime(result);
                result.State = "AwaitingExternalRecovery";
                WriteMusicVolumeMutationJsonAtomically(
                    resultPath,
                    result,
                    AotMusicVolumeMutationSmokeJsonContext.Default.AotMusicVolumeMutationSmokeResult);
                await Task.Delay(Timeout.InfiniteTimeSpan);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        return intent;
    }

    private static AotMusicVolumeRecoveryIntent
        PersistAndReadBackMusicVolumeRecoveryIntent(
            string recoveryIntentPath,
            AotMusicVolumeRecoveryIntent intent,
            string previewDataRoot,
            AotMusicVolumeMutationSmokeResult result)
    {
        WriteMusicVolumeMutationJsonAtomically(
            recoveryIntentPath,
            intent,
            AotMusicVolumeMutationSmokeJsonContext.Default.AotMusicVolumeRecoveryIntent);
        result.RecoveryIntentPersisted = true;
        result.RecoveryIntentPreserved = true;

        AotMusicVolumeRecoveryIntent persisted = ReadMusicVolumeMutationJson(
            recoveryIntentPath,
            AotMusicVolumeMutationSmokeJsonContext.Default.AotMusicVolumeRecoveryIntent);
        ValidateMusicVolumeRecoveryIntent(persisted, previewDataRoot);
        RequireMusicVolumeMutation(
            result,
            Math.Abs(persisted.OriginalVolume - intent.OriginalVolume) <=
                MusicVolumeMutationTolerance &&
            Math.Abs(persisted.ProbeVolume - intent.ProbeVolume) <=
                MusicVolumeMutationTolerance,
            "recovery-intent-read-back",
            "The durable recovery intent did not round-trip before mutation.");
        return persisted;
    }

    private static async Task RecoverOriginalMusicVolumeAsync(
        string recoveryIntentPath,
        string previewDataRoot,
        AotMusicVolumeMutationSmokeResult result)
    {
        result.RecoveryIntentFound = File.Exists(recoveryIntentPath);
        if (!result.RecoveryIntentFound)
        {
            MusicVolumeNativeCallResult current = ReadHealthySystemVolume(
                result,
                "recovery-no-intent-system-volume");
            result.OriginalVolume = current.SystemVolume;
            result.FinalVolume = current.SystemVolume;
            result.NativeFinal = AotMusicVolumeNativeEvidence.From(current);
            result.CleanupSucceeded = true;
            result.RecoveryIntentPreserved = false;
            RequireMusicVolumeMutation(
                result,
                true,
                "recovery-no-intent",
                string.Empty);
            return;
        }

        AotMusicVolumeRecoveryIntent intent = ReadMusicVolumeMutationJson(
            recoveryIntentPath,
            AotMusicVolumeMutationSmokeJsonContext.Default.AotMusicVolumeRecoveryIntent);
        ValidateMusicVolumeRecoveryIntent(intent, previewDataRoot);
        result.RecoveryIntentLoaded = true;
        result.RecoveryIntentPreserved = true;
        result.OriginalVolume = intent.OriginalVolume;
        result.ProbeVolume = intent.ProbeVolume;

        MusicVolumeNativeCallResult before = ReadHealthySystemVolume(
            result,
            "recovery-current-system-volume");
        result.RecoveryObservedVolume = before.SystemVolume;
        result.NativeRecoveryBefore = AotMusicVolumeNativeEvidence.From(before);
        await RestoreOriginalMusicVolumeAsync(intent, recoveryIntentPath, result);
    }

    private static async Task RestoreOriginalMusicVolumeAsync(
        AotMusicVolumeRecoveryIntent intent,
        string recoveryIntentPath,
        AotMusicVolumeMutationSmokeResult result)
    {
        var service = new MusicVolumeService();
        result.RestoreRequestSucceeded =
            await service.TrySetSystemMasterVolumeAsync(intent.OriginalVolume);
        RequireMusicVolumeMutation(
            result,
            result.RestoreRequestSucceeded,
            "product-restore-setter-succeeded",
            $"The product setter could not restore original volume {intent.OriginalVolume}.");

        MusicVolumeNativeCallResult final = await WaitForMusicVolumeAsync(
            intent.OriginalVolume,
            TimeSpan.FromSeconds(5));
        result.NativeFinal = AotMusicVolumeNativeEvidence.From(final);
        result.FinalVolume = final.SystemVolume;
        RequireHealthyMusicVolumeRead(result, final, "recovery-final-system-volume");
        RequireMusicVolumeMutation(
            result,
            Math.Abs(final.SystemVolume - intent.OriginalVolume) <=
                MusicVolumeMutationTolerance,
            "recovery-original-verified",
            $"The Rust getter observed {final.SystemVolume}, expected restored " +
            $"volume {intent.OriginalVolume}.");

        File.Delete(recoveryIntentPath);
        result.RecoveryIntentPreserved = false;
        result.CleanupSucceeded = true;
        RequireMusicVolumeMutation(
            result,
            !File.Exists(recoveryIntentPath),
            "recovery-intent-cleared-after-verification",
            "The recovery intent still exists after verified restoration.");
    }

    private static MusicVolumeNativeCallResult ReadHealthySystemVolume(
        AotMusicVolumeMutationSmokeResult result,
        string stepPrefix)
    {
        MusicVolumeNativeCallResult native = MusicVolumeNativeBackend.GetSystemVolume();
        RequireHealthyMusicVolumeRead(result, native, stepPrefix);
        return native;
    }

    private static async Task<MusicVolumeNativeCallResult> WaitForMusicVolumeAsync(
        double expected,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        MusicVolumeNativeCallResult latest;
        do
        {
            latest = MusicVolumeNativeBackend.GetSystemVolume();
            if (latest.Success &&
                Math.Abs(latest.SystemVolume - expected) <= MusicVolumeMutationTolerance)
            {
                return latest;
            }

            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        return latest;
    }

    private static void RequireHealthyMusicVolumeRead(
        AotMusicVolumeMutationSmokeResult result,
        MusicVolumeNativeCallResult native,
        string stepPrefix)
    {
        RequireMusicVolumeMutation(
            result,
            native.Success &&
            native.Status == 0 &&
            native.OperationHResult >= 0 &&
            native.DeviceHResult >= 0 &&
            native.SystemHResult >= 0,
            $"{stepPrefix}-hresults",
            "The Rust getter did not acquire a healthy default audio endpoint.");
        RequireMusicVolumeMutation(
            result,
            (native.AttemptedPhases & 0x0FU) == 0x0FU,
            $"{stepPrefix}-phases",
            $"The Rust getter attempted phases 0x{native.AttemptedPhases:X}.");
        RequireMusicVolumeMutation(
            result,
            double.IsFinite(native.SystemVolume) &&
            native.SystemVolume is >= 0.0 and <= 1.0,
            $"{stepPrefix}-normalized",
            $"Volume {native.SystemVolume} is outside [0,1].");
    }

    private static double SelectMusicVolumeProbe(double original) =>
        original <= 0.85
            ? Math.Min(0.95, original + MusicVolumeProbeDelta)
            : Math.Max(0.05, original - MusicVolumeProbeDelta);

    private static void ValidateMusicVolumeRecoveryIntent(
        AotMusicVolumeRecoveryIntent intent,
        string previewDataRoot)
    {
        if (intent.SchemaVersion != 1 ||
            !PathsEqual(intent.PreviewDataRoot, previewDataRoot) ||
            !double.IsFinite(intent.OriginalVolume) ||
            !double.IsFinite(intent.ProbeVolume) ||
            intent.OriginalVolume is < 0.0 or > 1.0 ||
            intent.ProbeVolume is <= 0.0 or >= 1.0 ||
            Math.Abs(intent.ProbeVolume - intent.OriginalVolume) < 0.04)
        {
            throw new InvalidDataException(
                "The durable music-volume recovery intent is invalid; it was preserved.");
        }
    }

    private static void CaptureMusicVolumeMutationNativeEvidence(
        AotMusicVolumeMutationSmokeResult result)
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

    private static void RequireMusicVolumeMutationRuntime(
        AotMusicVolumeMutationSmokeResult result)
    {
        RequireMusicVolumeMutation(
            result,
            !result.IsDynamicCodeSupported,
            "runtime-native-aot",
            "Native AOT unexpectedly reported dynamic-code support.");
        RequireMusicVolumeMutation(
            result,
            string.Equals(result.SelectedBackend, "Rust", StringComparison.Ordinal),
            "music-volume-backend-rust",
            $"Expected Rust, found '{result.SelectedBackend}'.");
        RequireMusicVolumeMutation(
            result,
            string.Equals(result.LoadState, "Loaded", StringComparison.Ordinal),
            "module-loaded",
            $"Expected Loaded, found '{result.LoadState}'.");
        RequireMusicVolumeMutation(
            result,
            !string.IsNullOrWhiteSpace(result.ModuleHandle) &&
            result.ModuleHandle != "0x0",
            "module-handle",
            "Native module handle is zero.");
        RequireMusicVolumeMutation(
            result,
            result.AbiVersion == 2,
            "module-abi",
            $"Unexpected ABI {result.AbiVersion}.");
        RequireMusicVolumeMutation(
            result,
            (result.Capabilities.GetValueOrDefault() &
             ShortcutNativeModule.MusicVolumeCapability) != 0,
            "module-music-volume-capability",
            $"Music-volume capability is absent from 0x{result.Capabilities.GetValueOrDefault():X}.");
    }

    private static void RequireMusicVolumeMutation(
        AotMusicVolumeMutationSmokeResult result,
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

    private static void WriteMusicVolumeMutationJsonAtomically<T>(
        string path,
        T value,
        JsonTypeInfo<T> jsonTypeInfo)
    {
        string temporaryPath = path + ".tmp";
        string json = JsonSerializer.Serialize(value, jsonTypeInfo);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static T ReadMusicVolumeMutationJson<T>(
        string path,
        JsonTypeInfo<T> jsonTypeInfo) where T : class =>
        JsonSerializer.Deserialize(File.ReadAllText(path), jsonTypeInfo) ??
        throw new InvalidDataException($"JSON evidence '{path}' was empty.");

    private static string? CombineMusicVolumeMutationErrors(
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

    private static string GetMusicVolumeMutationScenarioDirectoryName(
        AotMusicVolumeMutationSmokeScenario scenario) =>
        scenario switch
        {
            AotMusicVolumeMutationSmokeScenario.ChangeRestore => "change-restore",
            AotMusicVolumeMutationSmokeScenario.ChangeThenFail => "change-then-fail",
            AotMusicVolumeMutationSmokeScenario.ChangeThenAwaitExternalRecovery =>
                "change-then-await-external-recovery",
            AotMusicVolumeMutationSmokeScenario.RecoverOriginal => "recover-original",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
}

internal enum AotMusicVolumeMutationSmokeScenario
{
    ChangeRestore,
    ChangeThenFail,
    ChangeThenAwaitExternalRecovery,
    RecoverOriginal
}

internal sealed class AotMusicVolumeRecoveryIntent
{
    public int SchemaVersion { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string PreviewDataRoot { get; set; } = string.Empty;
    public double OriginalVolume { get; set; }
    public double ProbeVolume { get; set; }
    public int OriginatingProcessId { get; set; }
    public string? OriginatingExecutablePath { get; set; }
}

internal sealed class AotMusicVolumeMutationSmokeResult
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
    public double OriginalVolume { get; set; }
    public double ProbeVolume { get; set; }
    public double ObservedProbeVolume { get; set; }
    public double RecoveryObservedVolume { get; set; }
    public double FinalVolume { get; set; }
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
    typeof(AotMusicVolumeMutationSmokeResult),
    TypeInfoPropertyName = "AotMusicVolumeMutationSmokeResult")]
[JsonSerializable(
    typeof(AotMusicVolumeRecoveryIntent),
    TypeInfoPropertyName = "AotMusicVolumeRecoveryIntent")]
internal partial class AotMusicVolumeMutationSmokeJsonContext : JsonSerializerContext
{
}
#endif
