#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private const string AotMusicVolumeReadSmokeEnvironmentVariable =
        "DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE";
    private const string AotMusicVolumeReadSmokeDirectoryName =
        "aot-music-volume-read-smoke";
    private const double SystemVolumeTolerance = 0.005;
    private const string MusicVolumeProbeAppUserModelId = "DeskBox.Aot.ReadOnly.Probe";
    private const string MusicVolumeProbeDisplayName = "DeskBox AOT Read Only Probe";

    private void StartAotMusicVolumeReadSmokeIfRequested()
    {
        string? configuredScenario = Environment.GetEnvironmentVariable(
            AotMusicVolumeReadSmokeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredScenario))
        {
            return;
        }

        if (!Enum.TryParse(
                configuredScenario.Trim(),
                ignoreCase: true,
                out AotMusicVolumeReadSmokeScenario scenario) ||
            !Enum.IsDefined(scenario))
        {
            Log($"[AotMusicVolumeReadSmoke] Unsupported scenario '{configuredScenario}'.");
            return;
        }

        _ = RunAotMusicVolumeReadSmokeAsync(scenario);
    }

    private async Task RunAotMusicVolumeReadSmokeAsync(
        AotMusicVolumeReadSmokeScenario scenario)
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
                "[AotMusicVolumeReadSmoke] RefusedNonPreviewRoot: the smoke runner " +
                "requires an explicit isolated Native AOT preview root.");
            return;
        }

        string smokeParent = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotMusicVolumeReadSmokeDirectoryName));
        string smokeRoot = Path.GetFullPath(Path.Combine(
            smokeParent,
            GetMusicVolumeReadScenarioDirectoryName(scenario)));
        if (!IsPathEqualOrInside(smokeParent, smokeRoot))
        {
            Log($"[AotMusicVolumeReadSmoke] Refused unsafe evidence root '{smokeRoot}'.");
            return;
        }

        if (Directory.Exists(smokeRoot))
        {
            Directory.Delete(smokeRoot, recursive: true);
        }
        Directory.CreateDirectory(smokeRoot);

        string resultPath = Path.Combine(smokeRoot, "result.json");
        var result = new AotMusicVolumeReadSmokeResult
        {
            SchemaVersion = 1,
            Scenario = scenario.ToString(),
            State = "Running",
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            ExecutablePath = Environment.ProcessPath,
            PreviewDataRoot = dataPaths.RootPath,
            EvidenceRoot = smokeRoot,
            ResultPath = resultPath,
            IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            SourceAppUserModelId = MusicVolumeProbeAppUserModelId,
            SourceDisplayName = MusicVolumeProbeDisplayName,
            Steps = []
        };
        CaptureMusicVolumeReadNativeEvidence(result);
        WriteMusicVolumeReadSmokeResult(resultPath, result);

        try
        {
            if (scenario != AotMusicVolumeReadSmokeScenario.SystemAndSnapshotReadOnly)
            {
                throw new ArgumentOutOfRangeException(nameof(scenario));
            }

            await RunSystemAndSnapshotReadOnlyAsync(result);
            CaptureMusicVolumeReadNativeEvidence(result);

            RequireMusic(
                result,
                !result.IsDynamicCodeSupported,
                "runtime-native-aot",
                "Native AOT unexpectedly reported dynamic-code support.");
            RequireMusic(
                result,
                string.Equals(result.SelectedBackend, "Rust", StringComparison.Ordinal),
                "music-volume-backend-rust",
                $"Expected the Rust music-volume backend, found '{result.SelectedBackend}'.");
            RequireMusic(
                result,
                string.Equals(result.LoadState, "Loaded", StringComparison.Ordinal),
                "module-loaded",
                $"Expected the native module to be loaded, found '{result.LoadState}'.");
            RequireMusic(
                result,
                !string.IsNullOrWhiteSpace(result.ModuleHandle) &&
                result.ModuleHandle != "0x0",
                "module-handle",
                "Native module handle is zero.");
            RequireMusic(
                result,
                result.AbiVersion == 2,
                "module-abi",
                $"Unexpected ABI {result.AbiVersion}.");
            RequireMusic(
                result,
                (result.Capabilities.GetValueOrDefault() &
                 ShortcutNativeModule.MusicVolumeCapability) != 0,
                "module-music-volume-capability",
                $"Music-volume capability is absent from mask 0x{result.Capabilities.GetValueOrDefault():X}.");

            result.State = "Completed";
            result.Success = true;
        }
        catch (Exception ex)
        {
            CaptureMusicVolumeReadNativeEvidence(result);
            result.State = "Failed";
            result.Success = false;
            result.Error = ex.ToString();
            Log($"[AotMusicVolumeReadSmoke] Scenario {scenario} failed: {ex}");
        }
        finally
        {
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            WriteMusicVolumeReadSmokeResult(resultPath, result);
            Log(
                $"[AotMusicVolumeReadSmoke] Scenario={scenario} state={result.State} " +
                $"success={result.Success} result='{resultPath}'");
        }
    }

    private static async Task RunSystemAndSnapshotReadOnlyAsync(
        AotMusicVolumeReadSmokeResult result)
    {
        MusicVolumeNativeCallResult nativeSystem =
            MusicVolumeNativeBackend.GetSystemVolume();
        result.NativeSystemBefore = AotMusicVolumeNativeEvidence.From(nativeSystem);
        result.NativeSystemVolumeBefore = nativeSystem.SystemVolume;
        RequireMusic(
            result,
            nativeSystem.Success,
            "default-audio-endpoint",
            "The direct Rust system getter did not acquire a usable default audio endpoint: " +
            $"failure={nativeSystem.Failure}, detail={nativeSystem.Detail}, " +
            $"status={nativeSystem.Status}, HRESULT=0x{nativeSystem.OperationHResult:X8}.");
        RequireHealthySystemRead(result, nativeSystem, "native-system-before");

        var service = new MusicVolumeService();
        result.ProductSystemVolume = await service.GetSystemMasterVolumeAsync();
        RequireNormalizedVolume(
            result,
            result.ProductSystemVolume,
            "product-system-volume");

        MusicVolumeSnapshot productSnapshot = await service.GetVolumeAsync(
            MusicVolumeProbeAppUserModelId,
            MusicVolumeProbeDisplayName);
        result.ProductSnapshotSystemVolume = productSnapshot.SystemVolume;
        result.ProductSnapshotSessionVolume = productSnapshot.SessionVolume;
        result.ProductSnapshotHasSessionVolume = productSnapshot.HasSessionVolume;
        RequireNormalizedVolume(
            result,
            productSnapshot.SystemVolume,
            "product-snapshot-system-volume");
        RequireNormalizedVolume(
            result,
            productSnapshot.SessionVolume,
            "product-snapshot-session-volume");

        MusicVolumeNativeCallResult nativeSnapshot =
            MusicVolumeNativeBackend.GetSnapshot(
                MusicVolumeProbeAppUserModelId,
                MusicVolumeProbeDisplayName);
        result.NativeSnapshot = AotMusicVolumeNativeEvidence.From(nativeSnapshot);
        RequireMusic(
            result,
            nativeSnapshot.Success,
            "native-snapshot-success",
            "The direct Rust snapshot getter failed: " +
            $"failure={nativeSnapshot.Failure}, detail={nativeSnapshot.Detail}, " +
            $"status={nativeSnapshot.Status}, HRESULT=0x{nativeSnapshot.OperationHResult:X8}.");
        RequireMusic(
            result,
            nativeSnapshot.Status == 0 &&
            nativeSnapshot.OperationHResult >= 0 &&
            nativeSnapshot.DeviceHResult >= 0 &&
            nativeSnapshot.SystemHResult >= 0 &&
            nativeSnapshot.SessionHResult >= 0,
            "native-snapshot-hresults",
            "The Rust snapshot returned an unsuccessful endpoint/system/session HRESULT.");
        RequireMusic(
            result,
            (nativeSnapshot.AttemptedPhases & 0x1FU) == 0x1FU,
            "native-snapshot-phases",
            $"The Rust snapshot did not attempt every required read phase: 0x{nativeSnapshot.AttemptedPhases:X}.");
        RequireNormalizedVolume(
            result,
            nativeSnapshot.SystemVolume,
            "native-snapshot-system-volume");
        RequireNormalizedVolume(
            result,
            nativeSnapshot.SessionVolume,
            "native-snapshot-session-volume");
        RequireMusic(
            result,
            nativeSnapshot.HasSessionVolume
                ? nativeSnapshot.MatchKind is >= 1 and <= 7 &&
                  (nativeSnapshot.AttemptedPhases & 0x20U) != 0
                : nativeSnapshot.MatchKind == 0 &&
                  (nativeSnapshot.AttemptedPhases & 0x20U) == 0,
            "native-snapshot-session-shape",
            "Session match kind, phase mask, and HasSessionVolume are inconsistent.");

        result.SessionMatchObserved = nativeSnapshot.HasSessionVolume;
        RequireMusic(
            result,
            productSnapshot.HasSessionVolume == nativeSnapshot.HasSessionVolume,
            "product-native-session-presence",
            "The product and direct native snapshot disagree about session presence.");
        if (nativeSnapshot.HasSessionVolume)
        {
            RequireMusic(
                result,
                Math.Abs(
                    productSnapshot.SessionVolume - nativeSnapshot.SessionVolume) <=
                    SystemVolumeTolerance,
                "product-native-session-volume",
                "The product and direct native session volumes differ beyond tolerance.");
        }

        MusicVolumeNativeCallResult nativeSystemAfter =
            MusicVolumeNativeBackend.GetSystemVolume();
        result.NativeSystemAfter = AotMusicVolumeNativeEvidence.From(nativeSystemAfter);
        result.NativeSystemVolumeAfter = nativeSystemAfter.SystemVolume;
        RequireMusic(
            result,
            nativeSystemAfter.Success,
            "native-system-after-success",
            "The final direct Rust system getter failed.");
        RequireHealthySystemRead(result, nativeSystemAfter, "native-system-after");

        double[] systemReadings =
        [
            result.ProductSystemVolume,
            result.ProductSnapshotSystemVolume,
            nativeSnapshot.SystemVolume,
            result.NativeSystemVolumeAfter
        ];
        RequireMusic(
            result,
            systemReadings.All(
                value => Math.Abs(value - result.NativeSystemVolumeBefore) <=
                         SystemVolumeTolerance),
            "system-volume-unchanged",
            "The system master volume changed while the read-only scenario was running.");
    }

    private static void RequireHealthySystemRead(
        AotMusicVolumeReadSmokeResult result,
        MusicVolumeNativeCallResult nativeSystem,
        string stepPrefix)
    {
        RequireMusic(
            result,
            nativeSystem.Status == 0 &&
            nativeSystem.OperationHResult >= 0 &&
            nativeSystem.DeviceHResult >= 0 &&
            nativeSystem.SystemHResult >= 0,
            $"{stepPrefix}-hresults",
            "The Rust system getter returned an unsuccessful endpoint/system HRESULT.");
        RequireMusic(
            result,
            (nativeSystem.AttemptedPhases & 0x0FU) == 0x0FU,
            $"{stepPrefix}-phases",
            $"The Rust system getter did not attempt every required phase: 0x{nativeSystem.AttemptedPhases:X}.");
        RequireNormalizedVolume(result, nativeSystem.SystemVolume, $"{stepPrefix}-volume");
    }

    private static void RequireNormalizedVolume(
        AotMusicVolumeReadSmokeResult result,
        double value,
        string step)
    {
        RequireMusic(
            result,
            double.IsFinite(value) && value is >= 0.0 and <= 1.0,
            step,
            $"Volume {value} is not finite and normalized to [0,1].");
    }

    private static void CaptureMusicVolumeReadNativeEvidence(
        AotMusicVolumeReadSmokeResult result)
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

    private static void RequireMusic(
        AotMusicVolumeReadSmokeResult result,
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

    private static void WriteMusicVolumeReadSmokeResult(
        string resultPath,
        AotMusicVolumeReadSmokeResult result)
    {
        string temporaryPath = resultPath + ".tmp";
        string json = JsonSerializer.Serialize(
            result,
            AotMusicVolumeReadSmokeJsonContext.Default.AotMusicVolumeReadSmokeResult);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, resultPath, overwrite: true);
    }

    private static string GetMusicVolumeReadScenarioDirectoryName(
        AotMusicVolumeReadSmokeScenario scenario) =>
        scenario switch
        {
            AotMusicVolumeReadSmokeScenario.SystemAndSnapshotReadOnly =>
                "system-and-snapshot-read-only",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
}

internal enum AotMusicVolumeReadSmokeScenario
{
    SystemAndSnapshotReadOnly
}

internal sealed class AotMusicVolumeReadSmokeResult
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
    public string SourceAppUserModelId { get; set; } = string.Empty;
    public string SourceDisplayName { get; set; } = string.Empty;
    public double ProductSystemVolume { get; set; }
    public double ProductSnapshotSystemVolume { get; set; }
    public double ProductSnapshotSessionVolume { get; set; }
    public bool ProductSnapshotHasSessionVolume { get; set; }
    public bool SessionMatchObserved { get; set; }
    public double NativeSystemVolumeBefore { get; set; }
    public double NativeSystemVolumeAfter { get; set; }
    public AotMusicVolumeNativeEvidence? NativeSystemBefore { get; set; }
    public AotMusicVolumeNativeEvidence? NativeSnapshot { get; set; }
    public AotMusicVolumeNativeEvidence? NativeSystemAfter { get; set; }
    public List<string> Steps { get; set; } = [];
    public string? Error { get; set; }
}

internal sealed class AotMusicVolumeNativeEvidence
{
    public bool Success { get; set; }
    public string Failure { get; set; } = string.Empty;
    public string Detail { get; set; } = string.Empty;
    public uint Status { get; set; }
    public int OperationHResult { get; set; }
    public uint AttemptedPhases { get; set; }
    public uint MatchKind { get; set; }
    public int ComHResult { get; set; }
    public int CreateHResult { get; set; }
    public int DeviceHResult { get; set; }
    public int SystemHResult { get; set; }
    public int SessionHResult { get; set; }
    public double SystemVolume { get; set; }
    public double SessionVolume { get; set; }
    public bool HasSessionVolume { get; set; }

    internal static AotMusicVolumeNativeEvidence From(
        MusicVolumeNativeCallResult result) =>
        new()
        {
            Success = result.Success,
            Failure = result.Failure.ToString(),
            Detail = result.Detail,
            Status = result.Status,
            OperationHResult = result.OperationHResult,
            AttemptedPhases = result.AttemptedPhases,
            MatchKind = result.MatchKind,
            ComHResult = result.ComHResult,
            CreateHResult = result.CreateHResult,
            DeviceHResult = result.DeviceHResult,
            SystemHResult = result.SystemHResult,
            SessionHResult = result.SessionHResult,
            SystemVolume = result.SystemVolume,
            SessionVolume = result.SessionVolume,
            HasSessionVolume = result.HasSessionVolume
        };
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(AotMusicVolumeReadSmokeResult),
    TypeInfoPropertyName = "AotMusicVolumeReadSmokeResult")]
internal partial class AotMusicVolumeReadSmokeJsonContext : JsonSerializerContext
{
}
#endif
