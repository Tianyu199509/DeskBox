#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private const string AotQuickAccessMutationSmokeEnvironmentVariable =
        "DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE";
    private const string AotQuickAccessMutationSmokeDirectoryName =
        "aot-quick-access-mutation-smoke";
    private const string AotQuickAccessMutationTargetDirectoryName = "mutation-target";

    private void StartAotQuickAccessMutationSmokeIfRequested()
    {
        string? configuredScenario = Environment.GetEnvironmentVariable(
            AotQuickAccessMutationSmokeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredScenario))
        {
            return;
        }

        if (!Enum.TryParse(
                configuredScenario.Trim(),
                ignoreCase: true,
                out AotQuickAccessMutationScenario scenario) ||
            !Enum.IsDefined(scenario))
        {
            Log(
                $"[AotQuickAccessMutationSmoke] Unsupported scenario " +
                $"'{configuredScenario}'.");
            return;
        }

        _ = RunAotQuickAccessMutationSmokeAsync(scenario);
    }

    private async Task RunAotQuickAccessMutationSmokeAsync(
        AotQuickAccessMutationScenario scenario)
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
                "[AotQuickAccessMutationSmoke] RefusedNonPreviewRoot: the smoke runner " +
                "requires an explicit isolated Native AOT preview root.");
            return;
        }

        string mutationParent = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotQuickAccessMutationSmokeDirectoryName));
        string targetFolder = Path.GetFullPath(Path.Combine(
            mutationParent,
            AotQuickAccessMutationTargetDirectoryName));
        string scenarioRoot = Path.GetFullPath(Path.Combine(
            mutationParent,
            GetQuickAccessMutationScenarioDirectoryName(scenario)));
        if (!IsPathEqualOrInside(dataPaths.RootPath, mutationParent) ||
            !IsPathEqualOrInside(mutationParent, targetFolder) ||
            !IsPathEqualOrInside(mutationParent, scenarioRoot) ||
            PathsEqual(targetFolder, scenarioRoot))
        {
            Log(
                $"[AotQuickAccessMutationSmoke] Refused unsafe fixture paths; " +
                $"parent='{mutationParent}', target='{targetFolder}', " +
                $"scenario='{scenarioRoot}'.");
            return;
        }

        Directory.CreateDirectory(mutationParent);
        Directory.CreateDirectory(targetFolder);
        if (Directory.Exists(scenarioRoot))
        {
            Directory.Delete(scenarioRoot, recursive: true);
        }
        Directory.CreateDirectory(scenarioRoot);

        string resultPath = Path.Combine(scenarioRoot, "result.json");
        var result = new AotQuickAccessMutationSmokeResult
        {
            SchemaVersion = 1,
            Scenario = scenario.ToString(),
            State = "Running",
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            ExecutablePath = Environment.ProcessPath,
            PreviewDataRoot = dataPaths.RootPath,
            MutationParent = mutationParent,
            TargetFolder = targetFolder,
            ScenarioRoot = scenarioRoot,
            ResultPath = resultPath,
            IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            Steps = []
        };
        CaptureQuickAccessMutationNativeEvidence(result);
        WriteQuickAccessMutationSmokeResult(resultPath, result);

        Exception? scenarioFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            switch (scenario)
            {
                case AotQuickAccessMutationScenario.PinUnpin:
                    await RunQuickAccessPinUnpinMutationAsync(targetFolder, result);
                    break;

                case AotQuickAccessMutationScenario.PinThenFail:
                    await PinAndProveQuickAccessMutationAsync(targetFolder, result);
                    CaptureQuickAccessMutationNativeEvidence(result);
                    RequireQuickAccessMutationRuntime(result);
                    throw new InvalidOperationException(
                        "intentional-after-pin: exercising App finally compensation.");

                case AotQuickAccessMutationScenario.PinThenAwaitExternalCompensation:
                    await PinThenAwaitExternalCompensationAsync(
                        targetFolder,
                        resultPath,
                        result);
                    break;

                case AotQuickAccessMutationScenario.CompensateUnpin:
                    await ReadCompensationInitialStateAsync(targetFolder, result);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(scenario));
            }

            CaptureQuickAccessMutationNativeEvidence(result);
            RequireQuickAccessMutationRuntime(result);
        }
        catch (Exception ex)
        {
            scenarioFailure = ex;
            Log($"[AotQuickAccessMutationSmoke] Scenario {scenario} failed: {ex}");
        }
        finally
        {
            try
            {
                await RunCompensatingUnpinAsync(targetFolder, result);
                result.CleanupSucceeded = true;
            }
            catch (Exception ex)
            {
                cleanupFailure = ex;
                result.CleanupSucceeded = false;
                result.CleanupError = ex.ToString();
                Log(
                    $"[AotQuickAccessMutationSmoke] Compensation failed for " +
                    $"'{targetFolder}': {ex}");
            }

            CaptureQuickAccessMutationNativeEvidence(result);
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            result.Success = scenarioFailure is null && cleanupFailure is null;
            result.State = result.Success ? "Completed" : "Failed";
            result.Error = CombineQuickAccessMutationErrors(scenarioFailure, cleanupFailure);
            WriteQuickAccessMutationSmokeResult(resultPath, result);
            Log(
                $"[AotQuickAccessMutationSmoke] Scenario={scenario} state={result.State} " +
                $"success={result.Success} cleanup={result.CleanupSucceeded} " +
                $"target='{targetFolder}' result='{resultPath}'");
        }
    }

    private static async Task RunQuickAccessPinUnpinMutationAsync(
        string targetFolder,
        AotQuickAccessMutationSmokeResult result)
    {
        await PinAndProveQuickAccessMutationAsync(targetFolder, result);
        await UnpinAndProveQuickAccessMutationAsync(targetFolder, result);
    }

    private static async Task PinAndProveQuickAccessMutationAsync(
        string targetFolder,
        AotQuickAccessMutationSmokeResult result)
    {
        Directory.CreateDirectory(targetFolder);
        QuickAccessStateResult initial =
            await ExplorerQuickAccessHelper.GetQuickAccessPinStateAsync(targetFolder);
        result.InitialPublicState = initial.State.ToString();
        result.InitialPublicError = initial.Error;
        RequireQuickAccessMutation(
            result,
            initial.State == QuickAccessPinState.NotPinned &&
            string.IsNullOrWhiteSpace(initial.Error),
            "mutation-initial-not-pinned",
            $"Expected NotPinned before mutation; state={initial.State}, error={initial.Error}.");

        QuickAccessOperationResult pin =
            await ExplorerQuickAccessHelper.TryPinFolderToQuickAccessAsync(targetFolder);
        result.PinRequestSucceeded = pin.Succeeded;
        result.PinRequestError = pin.Error;
        RequireQuickAccessMutation(
            result,
            pin.Succeeded && string.IsNullOrWhiteSpace(pin.Error),
            "mutation-pin-request",
            $"Product pin failed; succeeded={pin.Succeeded}, error={pin.Error}.");

        QuickAccessStateResult pinned = await WaitForQuickAccessStateAsync(
            targetFolder,
            QuickAccessPinState.Pinned,
            TimeSpan.FromSeconds(20));
        result.PinnedPublicState = pinned.State.ToString();
        result.PinnedPublicError = pinned.Error;
        RequireQuickAccessMutation(
            result,
            pinned.State == QuickAccessPinState.Pinned &&
            string.IsNullOrWhiteSpace(pinned.Error),
            "mutation-pinned-public",
            $"Public query did not observe Pinned; state={pinned.State}, error={pinned.Error}.");

        QuickAccessNativeCallResult pinnedNative = QueryQuickAccessNative(targetFolder);
        CapturePinnedNativeEvidence(result, pinnedNative);
        RequireQuickAccessMutation(
            result,
            pinnedNative.Success && pinnedNative.PinState == QuickAccessPinState.Pinned,
            "mutation-pinned-native",
            $"Native query did not observe Pinned; failure={pinnedNative.Failure}, " +
            $"detail={pinnedNative.Detail}.");
    }

    private static async Task UnpinAndProveQuickAccessMutationAsync(
        string targetFolder,
        AotQuickAccessMutationSmokeResult result)
    {
        QuickAccessOperationResult unpin =
            await ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccessAsync(targetFolder);
        result.UnpinRequestSucceeded = unpin.Succeeded;
        result.UnpinRequestError = unpin.Error;
        RequireQuickAccessMutation(
            result,
            unpin.Succeeded && string.IsNullOrWhiteSpace(unpin.Error),
            "mutation-unpin-request",
            $"Product unpin failed; succeeded={unpin.Succeeded}, error={unpin.Error}.");

        QuickAccessStateResult unpinned = await WaitForQuickAccessStateAsync(
            targetFolder,
            QuickAccessPinState.NotPinned,
            TimeSpan.FromSeconds(20));
        result.UnpinnedPublicState = unpinned.State.ToString();
        result.UnpinnedPublicError = unpinned.Error;
        RequireQuickAccessMutation(
            result,
            unpinned.State == QuickAccessPinState.NotPinned &&
            string.IsNullOrWhiteSpace(unpinned.Error),
            "mutation-unpinned-public",
            $"Public query did not observe NotPinned; state={unpinned.State}, " +
            $"error={unpinned.Error}.");

        QuickAccessNativeCallResult unpinnedNative = QueryQuickAccessNative(targetFolder);
        CaptureUnpinnedNativeEvidence(result, unpinnedNative);
        RequireQuickAccessMutation(
            result,
            unpinnedNative.Success &&
            unpinnedNative.PinState == QuickAccessPinState.NotPinned,
            "mutation-unpinned-native",
            $"Native query did not observe NotPinned; failure={unpinnedNative.Failure}, " +
            $"detail={unpinnedNative.Detail}.");
    }

    private static async Task PinThenAwaitExternalCompensationAsync(
        string targetFolder,
        string resultPath,
        AotQuickAccessMutationSmokeResult result)
    {
        await PinAndProveQuickAccessMutationAsync(targetFolder, result);
        CaptureQuickAccessMutationNativeEvidence(result);
        RequireQuickAccessMutationRuntime(result);
        result.State = "AwaitingExternalCompensation";
        WriteQuickAccessMutationSmokeResult(resultPath, result);
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static async Task ReadCompensationInitialStateAsync(
        string targetFolder,
        AotQuickAccessMutationSmokeResult result)
    {
        Directory.CreateDirectory(targetFolder);
        QuickAccessStateResult initial =
            await ExplorerQuickAccessHelper.GetQuickAccessPinStateAsync(targetFolder);
        result.InitialPublicState = initial.State.ToString();
        result.InitialPublicError = initial.Error;
        RequireQuickAccessMutation(
            result,
            initial.State != QuickAccessPinState.Unknown &&
            string.IsNullOrWhiteSpace(initial.Error),
            "compensation-initial-state-readable",
            $"Compensation could not read initial state; state={initial.State}, " +
            $"error={initial.Error}.");
    }

    private static async Task RunCompensatingUnpinAsync(
        string targetFolder,
        AotQuickAccessMutationSmokeResult result)
    {
        Directory.CreateDirectory(targetFolder);
        QuickAccessOperationResult cleanup =
            await ExplorerQuickAccessHelper.TryUnpinFolderFromQuickAccessAsync(targetFolder);
        result.CleanupUnpinRequestSucceeded = cleanup.Succeeded;
        result.CleanupUnpinRequestError = cleanup.Error;
        RequireQuickAccessMutation(
            result,
            cleanup.Succeeded && string.IsNullOrWhiteSpace(cleanup.Error),
            "cleanup-unpin-request",
            $"Compensating product unpin failed; succeeded={cleanup.Succeeded}, " +
            $"error={cleanup.Error}.");

        QuickAccessStateResult final = await WaitForQuickAccessStateAsync(
            targetFolder,
            QuickAccessPinState.NotPinned,
            TimeSpan.FromSeconds(20));
        result.FinalPublicState = final.State.ToString();
        result.FinalPublicError = final.Error;
        RequireQuickAccessMutation(
            result,
            final.State == QuickAccessPinState.NotPinned &&
            string.IsNullOrWhiteSpace(final.Error),
            "cleanup-final-not-pinned",
            $"Compensation did not reach public NotPinned; state={final.State}, " +
            $"error={final.Error}.");

        QuickAccessNativeCallResult finalNative = QueryQuickAccessNative(targetFolder);
        CaptureFinalNativeEvidence(result, finalNative);
        RequireQuickAccessMutation(
            result,
            finalNative.Success && finalNative.PinState == QuickAccessPinState.NotPinned,
            "cleanup-native-not-pinned",
            $"Compensation did not reach native NotPinned; failure={finalNative.Failure}, " +
            $"detail={finalNative.Detail}.");
    }

    private static async Task<QuickAccessStateResult> WaitForQuickAccessStateAsync(
        string targetFolder,
        QuickAccessPinState expected,
        TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        QuickAccessStateResult latest = default;
        do
        {
            Directory.CreateDirectory(targetFolder);
            latest = await ExplorerQuickAccessHelper.GetQuickAccessPinStateAsync(targetFolder);
            if (latest.State == expected && string.IsNullOrWhiteSpace(latest.Error))
            {
                return latest;
            }

            await Task.Delay(200);
        }
        while (DateTime.UtcNow < deadline);

        return latest;
    }

    private static QuickAccessNativeCallResult QueryQuickAccessNative(string targetFolder) =>
        QuickAccessNativeBackend.Invoke(
            QuickAccessNativeOperation.QueryPinState,
            targetFolder,
            string.Empty,
            string.Empty);

    private static void CapturePinnedNativeEvidence(
        AotQuickAccessMutationSmokeResult result,
        QuickAccessNativeCallResult native)
    {
        result.PinnedNativeSuccess = native.Success;
        result.PinnedNativeState = native.PinState.ToString();
        result.PinnedNativeFailure = native.Failure.ToString();
        result.PinnedNativeDetail = native.Detail;
        result.PinnedNativeStatus = native.Status;
        result.PinnedNativeOperationHResult = native.OperationHResult;
        result.PinnedNativeAttemptedPhases = native.AttemptedPhases;
    }

    private static void CaptureUnpinnedNativeEvidence(
        AotQuickAccessMutationSmokeResult result,
        QuickAccessNativeCallResult native)
    {
        result.UnpinnedNativeSuccess = native.Success;
        result.UnpinnedNativeState = native.PinState.ToString();
        result.UnpinnedNativeFailure = native.Failure.ToString();
        result.UnpinnedNativeDetail = native.Detail;
        result.UnpinnedNativeStatus = native.Status;
        result.UnpinnedNativeOperationHResult = native.OperationHResult;
        result.UnpinnedNativeAttemptedPhases = native.AttemptedPhases;
    }

    private static void CaptureFinalNativeEvidence(
        AotQuickAccessMutationSmokeResult result,
        QuickAccessNativeCallResult native)
    {
        result.FinalNativeSuccess = native.Success;
        result.FinalNativeState = native.PinState.ToString();
        result.FinalNativeFailure = native.Failure.ToString();
        result.FinalNativeDetail = native.Detail;
        result.FinalNativeStatus = native.Status;
        result.FinalNativeOperationHResult = native.OperationHResult;
        result.FinalNativeAttemptedPhases = native.AttemptedPhases;
    }

    private static void CaptureQuickAccessMutationNativeEvidence(
        AotQuickAccessMutationSmokeResult result)
    {
        ShortcutNativeDiagnosticState diagnostic =
            ShortcutNativeBackend.CaptureDiagnosticState();
        result.QuickAccessBackend = QuickAccessBackendPolicy.Current.ToString();
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

    private static void RequireQuickAccessMutationRuntime(
        AotQuickAccessMutationSmokeResult result)
    {
        RequireQuickAccessMutation(
            result,
            !result.IsDynamicCodeSupported,
            "runtime-native-aot",
            "Native AOT unexpectedly reported dynamic-code support.");
        RequireQuickAccessMutation(
            result,
            string.Equals(result.QuickAccessBackend, "Rust", StringComparison.Ordinal),
            "quick-access-backend-rust",
            $"Expected the Rust Quick Access backend, found '{result.QuickAccessBackend}'.");
        RequireQuickAccessMutation(
            result,
            string.Equals(result.LoadState, "Loaded", StringComparison.Ordinal),
            "module-loaded",
            $"Expected the native module to be loaded, found '{result.LoadState}'.");
        RequireQuickAccessMutation(
            result,
            !string.IsNullOrWhiteSpace(result.ModuleHandle) && result.ModuleHandle != "0x0",
            "module-handle",
            "Native module handle is zero.");
        RequireQuickAccessMutation(
            result,
            result.AbiVersion == 2,
            "module-abi",
            $"Unexpected ABI {result.AbiVersion}.");
        RequireQuickAccessMutation(
            result,
            (result.Capabilities.GetValueOrDefault() & 0x80UL) == 0x80UL,
            "module-capabilities",
            $"Quick Access capability is missing from mask " +
            $"0x{result.Capabilities.GetValueOrDefault():X}.");
    }

    private static void RequireQuickAccessMutation(
        AotQuickAccessMutationSmokeResult result,
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

    private static void WriteQuickAccessMutationSmokeResult(
        string resultPath,
        AotQuickAccessMutationSmokeResult result)
    {
        string temporaryPath = resultPath + ".tmp";
        string json = JsonSerializer.Serialize(
            result,
            AotQuickAccessMutationSmokeJsonContext.Default.AotQuickAccessMutationSmokeResult);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, resultPath, overwrite: true);
    }

    private static string? CombineQuickAccessMutationErrors(
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
               $"{Environment.NewLine}Compensation failure:{Environment.NewLine}{cleanupFailure}";
    }

    private static string GetQuickAccessMutationScenarioDirectoryName(
        AotQuickAccessMutationScenario scenario) =>
        scenario switch
        {
            AotQuickAccessMutationScenario.PinUnpin => "pin-unpin",
            AotQuickAccessMutationScenario.PinThenFail => "pin-then-fail",
            AotQuickAccessMutationScenario.PinThenAwaitExternalCompensation =>
                "pin-then-await-external-compensation",
            AotQuickAccessMutationScenario.CompensateUnpin => "compensate-unpin",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
}

internal enum AotQuickAccessMutationScenario
{
    PinUnpin,
    PinThenFail,
    PinThenAwaitExternalCompensation,
    CompensateUnpin
}

internal sealed class AotQuickAccessMutationSmokeResult
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
    public string MutationParent { get; set; } = string.Empty;
    public string TargetFolder { get; set; } = string.Empty;
    public string ScenarioRoot { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public bool IsDynamicCodeSupported { get; set; }
    public string? QuickAccessBackend { get; set; }
    public string? ModuleName { get; set; }
    public string? ModulePath { get; set; }
    public string? ModuleSha256 { get; set; }
    public string? ModuleHandle { get; set; }
    public bool LoadAttempted { get; set; }
    public string? LoadState { get; set; }
    public uint? AbiVersion { get; set; }
    public ulong? Capabilities { get; set; }
    public string? InitialPublicState { get; set; }
    public string? InitialPublicError { get; set; }
    public bool PinRequestSucceeded { get; set; }
    public string? PinRequestError { get; set; }
    public string? PinnedPublicState { get; set; }
    public string? PinnedPublicError { get; set; }
    public bool PinnedNativeSuccess { get; set; }
    public string? PinnedNativeState { get; set; }
    public string? PinnedNativeFailure { get; set; }
    public string? PinnedNativeDetail { get; set; }
    public uint PinnedNativeStatus { get; set; }
    public int PinnedNativeOperationHResult { get; set; }
    public uint PinnedNativeAttemptedPhases { get; set; }
    public bool UnpinRequestSucceeded { get; set; }
    public string? UnpinRequestError { get; set; }
    public string? UnpinnedPublicState { get; set; }
    public string? UnpinnedPublicError { get; set; }
    public bool UnpinnedNativeSuccess { get; set; }
    public string? UnpinnedNativeState { get; set; }
    public string? UnpinnedNativeFailure { get; set; }
    public string? UnpinnedNativeDetail { get; set; }
    public uint UnpinnedNativeStatus { get; set; }
    public int UnpinnedNativeOperationHResult { get; set; }
    public uint UnpinnedNativeAttemptedPhases { get; set; }
    public bool CleanupUnpinRequestSucceeded { get; set; }
    public string? CleanupUnpinRequestError { get; set; }
    public bool CleanupSucceeded { get; set; }
    public string? CleanupError { get; set; }
    public string? FinalPublicState { get; set; }
    public string? FinalPublicError { get; set; }
    public bool FinalNativeSuccess { get; set; }
    public string? FinalNativeState { get; set; }
    public string? FinalNativeFailure { get; set; }
    public string? FinalNativeDetail { get; set; }
    public uint FinalNativeStatus { get; set; }
    public int FinalNativeOperationHResult { get; set; }
    public uint FinalNativeAttemptedPhases { get; set; }
    public List<string> Steps { get; set; } = [];
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(AotQuickAccessMutationSmokeResult),
    TypeInfoPropertyName = "AotQuickAccessMutationSmokeResult")]
internal partial class AotQuickAccessMutationSmokeJsonContext : JsonSerializerContext
{
}
#endif
