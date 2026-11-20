#if DESKBOX_NATIVE_AOT && DESKBOX_AOT_SMOKE_HARNESS
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Helpers;
using DeskBox.Services;

namespace DeskBox;

public partial class App
{
    private const string AotShellSmokeEnvironmentVariable = "DESKBOX_AOT_SHELL_SMOKE";
    private const string AotShellSmokeDirectoryName = "aot-shell-smoke";

    private void StartAotShellSmokeIfRequested()
    {
        string? configuredScenario = Environment.GetEnvironmentVariable(
            AotShellSmokeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredScenario))
        {
            return;
        }

        if (!Enum.TryParse(
                configuredScenario.Trim(),
                ignoreCase: true,
                out AotShellSmokeScenario scenario) ||
            !Enum.IsDefined(scenario))
        {
            Log($"[AotShellSmoke] Unsupported scenario '{configuredScenario}'.");
            return;
        }

        _ = RunAotShellSmokeAsync(scenario);
    }

    private async Task RunAotShellSmokeAsync(AotShellSmokeScenario scenario)
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
                "[AotShellSmoke] RefusedNonPreviewRoot: the smoke runner requires " +
                "an explicit isolated Native AOT preview root.");
            return;
        }

        string scenarioName = GetShellScenarioDirectoryName(scenario);
        string smokeParent = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotShellSmokeDirectoryName));
        string smokeRoot = Path.GetFullPath(Path.Combine(smokeParent, scenarioName));
        if (!IsPathEqualOrInside(smokeParent, smokeRoot))
        {
            Log($"[AotShellSmoke] Refused unsafe fixture root '{smokeRoot}'.");
            return;
        }

        if (Directory.Exists(smokeRoot))
        {
            Directory.Delete(smokeRoot, recursive: true);
        }
        Directory.CreateDirectory(smokeRoot);

        string resultPath = Path.Combine(smokeRoot, "result.json");
        var result = new AotShellSmokeResult
        {
            SchemaVersion = 1,
            Scenario = scenario.ToString(),
            State = "Running",
            StartedAtUtc = DateTimeOffset.UtcNow,
            ProcessId = Environment.ProcessId,
            ExecutablePath = Environment.ProcessPath,
            PreviewDataRoot = dataPaths.RootPath,
            FixtureRoot = smokeRoot,
            ResultPath = resultPath,
            IsDynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
            Steps = []
        };
        CaptureShellNativeEvidence(result);
        WriteShellSmokeResult(resultPath, result);

        try
        {
            if (scenario != AotShellSmokeScenario.ExplorerQuickAccessReadOnly)
            {
                throw new ArgumentOutOfRangeException(nameof(scenario));
            }

            await RunExplorerQuickAccessReadOnlySmokeAsync(result);
            CaptureShellNativeEvidence(result);

            RequireShell(
                result,
                !result.IsDynamicCodeSupported,
                "runtime-native-aot",
                "Native AOT unexpectedly reported dynamic-code support.");
            RequireShell(
                result,
                string.Equals(result.ExplorerBackend, "Rust", StringComparison.Ordinal),
                "explorer-backend-rust",
                $"Expected the Rust Explorer backend, found '{result.ExplorerBackend}'.");
            RequireShell(
                result,
                string.Equals(result.QuickAccessBackend, "Rust", StringComparison.Ordinal),
                "quick-access-backend-rust",
                $"Expected the Rust Quick Access backend, found '{result.QuickAccessBackend}'.");
            RequireShell(
                result,
                string.Equals(result.LoadState, "Loaded", StringComparison.Ordinal),
                "module-loaded",
                $"Expected the native module to be loaded, found '{result.LoadState}'.");
            RequireShell(
                result,
                !string.IsNullOrWhiteSpace(result.ModuleHandle) && result.ModuleHandle != "0x0",
                "module-handle",
                "Native module handle is zero.");
            RequireShell(
                result,
                result.AbiVersion == 2,
                "module-abi",
                $"Unexpected ABI {result.AbiVersion}.");
            RequireShell(
                result,
                (result.Capabilities.GetValueOrDefault() & 0xC0UL) == 0xC0UL,
                "module-capabilities",
                $"Explorer/Quick Access capability mask is incomplete: 0x{result.Capabilities.GetValueOrDefault():X}.");

            result.State = "Completed";
            result.Success = true;
        }
        catch (Exception ex)
        {
            CaptureShellNativeEvidence(result);
            result.State = "Failed";
            result.Success = false;
            result.Error = ex.ToString();
            Log($"[AotShellSmoke] Scenario {scenario} failed: {ex}");
        }
        finally
        {
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            WriteShellSmokeResult(resultPath, result);
            Log(
                $"[AotShellSmoke] Scenario={scenario} state={result.State} " +
                $"success={result.Success} result='{resultPath}'");
        }
    }

    private static async Task RunExplorerQuickAccessReadOnlySmokeAsync(
        AotShellSmokeResult result)
    {
        string quickAccessFolder = Directory.CreateDirectory(
            Path.Combine(result.FixtureRoot, "quick-access-read-only-folder")).FullName;
        result.QuickAccessFolderPath = quickAccessFolder;

        QuickAccessStateResult before =
            await ExplorerQuickAccessHelper.GetQuickAccessPinStateAsync(quickAccessFolder);
        result.QuickAccessStateBefore = before.State.ToString();
        result.QuickAccessErrorBefore = before.Error;
        RequireShell(
            result,
            before.State == QuickAccessPinState.NotPinned && string.IsNullOrWhiteSpace(before.Error),
            "quick-access-query-before",
            $"Expected an unpinned fixture before launch; state={before.State}, error={before.Error}.");

        QuickAccessNativeCallResult quickAccessNative = QuickAccessNativeBackend.Invoke(
            QuickAccessNativeOperation.QueryPinState,
            quickAccessFolder,
            string.Empty,
            string.Empty);
        result.QuickAccessNativeSuccess = quickAccessNative.Success;
        result.QuickAccessNativeFailure = quickAccessNative.Failure.ToString();
        result.QuickAccessNativeDetail = quickAccessNative.Detail;
        result.QuickAccessNativeState = quickAccessNative.PinState.ToString();
        result.QuickAccessNativeStatus = quickAccessNative.Status;
        result.QuickAccessNativeOperationHResult = quickAccessNative.OperationHResult;
        result.QuickAccessNativeAttemptedPhases = quickAccessNative.AttemptedPhases;
        result.QuickAccessNativeMatchedItem = quickAccessNative.MatchedItem;
        result.QuickAccessNativeFallbackUsed = quickAccessNative.FallbackUsed;
        RequireShell(
            result,
            quickAccessNative.Success &&
            quickAccessNative.PinState == QuickAccessPinState.NotPinned,
            "quick-access-native-query",
            $"Native read-only query failed; failure={quickAccessNative.Failure}, detail={quickAccessNative.Detail}.");

        string explorerProbePath = Path.Combine(
            result.FixtureRoot,
            "explorer-launch-probe.cmd");
        string explorerMarkerPath = Path.Combine(
            result.FixtureRoot,
            "explorer-launch-marker.txt");
        File.WriteAllText(
            explorerProbePath,
            "@echo off\r\n" +
            ">\"%~dp0explorer-launch-marker.txt\" echo explorer-shell-launch\r\n");
        result.ExplorerProbePath = explorerProbePath;
        result.ExplorerMarkerPath = explorerMarkerPath;

        bool explorerOpened = ExplorerShellLaunchService.TryOpen(
            explorerProbePath,
            result.FixtureRoot,
            "open",
            out string? explorerError,
            out ExplorerShellLaunchNativeCallResult? explorerNative);
        result.ExplorerServiceSucceeded = explorerOpened;
        result.ExplorerError = explorerError;
        result.ExplorerNativeSuccess = explorerNative?.Success;
        result.ExplorerNativeFailure = explorerNative?.Failure.ToString();
        result.ExplorerNativeDetail = explorerNative?.Detail;
        result.ExplorerNativeStatus = explorerNative?.Status;
        result.ExplorerNativeOperationHResult = explorerNative?.OperationHResult;
        result.ExplorerNativeAttemptedPhases = explorerNative?.AttemptedPhases;
        RequireShell(
            result,
            explorerOpened && explorerNative?.Success == true,
            "explorer-product-service",
            $"Explorer product service failed; error={explorerError}, native={explorerNative?.Failure}: {explorerNative?.Detail}.");
        if (explorerNative is null)
        {
            throw new InvalidOperationException(
                "explorer-product-service: the product service returned no native evidence.");
        }
        RequireShell(
            result,
            explorerNative.AttemptedPhases != 0,
            "explorer-native-phases",
            "The Rust Explorer boundary reported no attempted phases.");

        DateTime markerDeadline = DateTime.UtcNow.AddSeconds(10);
        while (!File.Exists(explorerMarkerPath) && DateTime.UtcNow < markerDeadline)
        {
            await Task.Delay(100);
        }
        result.ExplorerMarkerExists = File.Exists(explorerMarkerPath);
        result.ExplorerMarkerText = result.ExplorerMarkerExists
            ? File.ReadAllText(explorerMarkerPath).Trim()
            : null;
        RequireShell(
            result,
            result.ExplorerMarkerExists &&
            string.Equals(
                result.ExplorerMarkerText,
                "explorer-shell-launch",
                StringComparison.Ordinal),
            "explorer-launch-marker",
            "Explorer ShellExecute did not create the expected probe marker.");

        QuickAccessStateResult after =
            await ExplorerQuickAccessHelper.GetQuickAccessPinStateAsync(quickAccessFolder);
        result.QuickAccessStateAfter = after.State.ToString();
        result.QuickAccessErrorAfter = after.Error;
        RequireShell(
            result,
            after.State == QuickAccessPinState.NotPinned && string.IsNullOrWhiteSpace(after.Error),
            "quick-access-query-after",
            $"The read-only fixture state changed; state={after.State}, error={after.Error}.");
    }

    private static void CaptureShellNativeEvidence(AotShellSmokeResult result)
    {
        ShortcutNativeDiagnosticState diagnostic =
            ShortcutNativeBackend.CaptureDiagnosticState();
        result.ExplorerBackend = ExplorerShellLaunchBackendPolicy.Current.ToString();
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
            result.ModulePath = Path.Combine(AppContext.BaseDirectory, ShortcutNativeModule.DllName);
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

    private static void RequireShell(
        AotShellSmokeResult result,
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

    private static void WriteShellSmokeResult(
        string resultPath,
        AotShellSmokeResult result)
    {
        string temporaryPath = resultPath + ".tmp";
        string json = JsonSerializer.Serialize(
            result,
            AotShellSmokeJsonContext.Default.AotShellSmokeResult);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, resultPath, overwrite: true);
    }

    private static string GetShellScenarioDirectoryName(AotShellSmokeScenario scenario) =>
        scenario switch
        {
            AotShellSmokeScenario.ExplorerQuickAccessReadOnly =>
                "explorer-quick-access-read-only",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
}

internal enum AotShellSmokeScenario
{
    ExplorerQuickAccessReadOnly
}

internal sealed class AotShellSmokeResult
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
    public string FixtureRoot { get; set; } = string.Empty;
    public string ResultPath { get; set; } = string.Empty;
    public bool IsDynamicCodeSupported { get; set; }
    public string? ExplorerBackend { get; set; }
    public string? QuickAccessBackend { get; set; }
    public string? ModuleName { get; set; }
    public string? ModulePath { get; set; }
    public string? ModuleSha256 { get; set; }
    public string? ModuleHandle { get; set; }
    public bool LoadAttempted { get; set; }
    public string? LoadState { get; set; }
    public uint? AbiVersion { get; set; }
    public ulong? Capabilities { get; set; }
    public string? ExplorerProbePath { get; set; }
    public string? ExplorerMarkerPath { get; set; }
    public bool ExplorerMarkerExists { get; set; }
    public string? ExplorerMarkerText { get; set; }
    public bool ExplorerServiceSucceeded { get; set; }
    public string? ExplorerError { get; set; }
    public bool? ExplorerNativeSuccess { get; set; }
    public string? ExplorerNativeFailure { get; set; }
    public string? ExplorerNativeDetail { get; set; }
    public uint? ExplorerNativeStatus { get; set; }
    public int? ExplorerNativeOperationHResult { get; set; }
    public uint? ExplorerNativeAttemptedPhases { get; set; }
    public string? QuickAccessFolderPath { get; set; }
    public string? QuickAccessStateBefore { get; set; }
    public string? QuickAccessErrorBefore { get; set; }
    public string? QuickAccessStateAfter { get; set; }
    public string? QuickAccessErrorAfter { get; set; }
    public bool QuickAccessNativeSuccess { get; set; }
    public string? QuickAccessNativeFailure { get; set; }
    public string? QuickAccessNativeDetail { get; set; }
    public string? QuickAccessNativeState { get; set; }
    public uint QuickAccessNativeStatus { get; set; }
    public int QuickAccessNativeOperationHResult { get; set; }
    public uint QuickAccessNativeAttemptedPhases { get; set; }
    public bool QuickAccessNativeMatchedItem { get; set; }
    public bool QuickAccessNativeFallbackUsed { get; set; }
    public List<string> Steps { get; set; } = [];
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(AotShellSmokeResult),
    TypeInfoPropertyName = "AotShellSmokeResult")]
internal partial class AotShellSmokeJsonContext : JsonSerializerContext
{
}
#endif
