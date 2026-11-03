#if DESKBOX_NATIVE_AOT
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Helpers;
using DeskBox.Services;
using WinRT.Interop;

namespace DeskBox;

public partial class App
{
    private const string AotShortcutSmokeEnvironmentVariable = "DESKBOX_AOT_SHORTCUT_SMOKE";
    private const string AotShortcutSmokeDirectoryName = "aot-shortcut-smoke";

    private void StartAotShortcutSmokeIfRequested()
    {
        string? configuredScenario = Environment.GetEnvironmentVariable(
            AotShortcutSmokeEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredScenario))
        {
            return;
        }

        if (!Enum.TryParse(
                configuredScenario.Trim(),
                ignoreCase: true,
                out AotShortcutSmokeScenario scenario) ||
            !Enum.IsDefined(scenario))
        {
            Log($"[AotShortcutSmoke] Unsupported scenario '{configuredScenario}'.");
            return;
        }

        _ = RunAotShortcutSmokeAsync(scenario);
    }

    private async Task RunAotShortcutSmokeAsync(AotShortcutSmokeScenario scenario)
    {
        // Let OnLaunched return before a native Shell dialog can enter a modal loop.
        await Task.Yield();

        DeskBoxDataPathService dataPaths = DeskBoxDataPathService.Current;
        string? configuredPreviewRoot = Environment.GetEnvironmentVariable(
            DeskBoxDataPathService.AotPreviewRootEnvironmentVariable);
        if (!dataPaths.IsDevelopmentRoot ||
            string.IsNullOrWhiteSpace(configuredPreviewRoot) ||
            !PathsEqual(dataPaths.RootPath, configuredPreviewRoot))
        {
            Log(
                "[AotShortcutSmoke] RefusedNonPreviewRoot: the smoke runner requires " +
                "an explicit isolated Native AOT preview root.");
            return;
        }

        string scenarioName = GetScenarioDirectoryName(scenario);
        string smokeRoot = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotShortcutSmokeDirectoryName,
            scenarioName));
        string expectedSmokeParent = Path.GetFullPath(Path.Combine(
            dataPaths.RootPath,
            AotShortcutSmokeDirectoryName));
        if (!IsPathEqualOrInside(expectedSmokeParent, smokeRoot))
        {
            Log($"[AotShortcutSmoke] Refused unsafe fixture root '{smokeRoot}'.");
            return;
        }

        if (Directory.Exists(smokeRoot))
        {
            Directory.Delete(smokeRoot, recursive: true);
        }
        Directory.CreateDirectory(smokeRoot);

        string resultPath = Path.Combine(smokeRoot, "result.json");
        var result = new AotShortcutSmokeResult
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
        CaptureNativeEvidence(result);
        WriteSmokeResult(resultPath, result);

        try
        {
            if (scenario == AotShortcutSmokeScenario.Core)
            {
                await Task.Run(() => RunCoreShortcutSmoke(result));
            }
            else
            {
                RunUiShortcutSmoke(scenario, result, resultPath);
            }

            CaptureNativeEvidence(result);
            Require(
                !result.IsDynamicCodeSupported,
                "runtime-native-aot",
                "Native AOT unexpectedly reported dynamic-code support.");
            Require(
                string.Equals(result.SelectedBackend, "Rust", StringComparison.Ordinal),
                "backend-rust",
                $"Expected the Rust shortcut backend, found '{result.SelectedBackend}'.");
            Require(
                string.Equals(result.LoadState, "Loaded", StringComparison.Ordinal),
                "module-loaded",
                $"Expected the native module to be loaded, found '{result.LoadState}'.");
            Require(
                !string.IsNullOrWhiteSpace(result.ModuleHandle) && result.ModuleHandle != "0x0",
                "module-handle",
                "Native module handle is zero.");
            Require(result.AbiVersion == 2, "module-abi", $"Unexpected ABI {result.AbiVersion}.");
            Require(
                (result.Capabilities.GetValueOrDefault() & 0x1FUL) == 0x1FUL,
                "module-capabilities",
                $"Shortcut capability mask is incomplete: 0x{result.Capabilities.GetValueOrDefault():X}.");

            result.State = "Completed";
            result.Success = true;
        }
        catch (Exception ex)
        {
            CaptureNativeEvidence(result);
            result.State = "Failed";
            result.Success = false;
            result.Error = ex.ToString();
            Log($"[AotShortcutSmoke] Scenario {scenario} failed: {ex}");
        }
        finally
        {
            result.CompletedAtUtc = DateTimeOffset.UtcNow;
            WriteSmokeResult(resultPath, result);
            Log(
                $"[AotShortcutSmoke] Scenario={scenario} state={result.State} " +
                $"success={result.Success} result='{resultPath}'");
        }
    }

    private static void RunCoreShortcutSmoke(AotShortcutSmokeResult result)
    {
        string root = result.FixtureRoot;
        string applicationLink = Path.Combine(root, "application.lnk");
        string firstTarget = Path.Combine(root, "application-target-one.txt");
        string secondTarget = Path.Combine(root, "application-target-two.txt");
        File.WriteAllText(firstTarget, "first target");
        File.WriteAllText(secondTarget, "second target");

        DragDropPermissionService.CreateOrUpdateShortcut(
            applicationLink,
            firstTarget,
            "--first",
            DragDropPermissionService.DeskBoxIconPath);
        ShortcutInfo? first = ShortcutHelper.ReadStoredMetadata(applicationLink);
        Require(
            first is not null &&
            PathsEqual(first.TargetPath, firstTarget) &&
            first.Arguments == "--first",
            "core-create-application",
            "The first application shortcut metadata did not match.");

        DragDropPermissionService.CreateOrUpdateShortcut(
            applicationLink,
            secondTarget,
            "--second",
            DragDropPermissionService.DeskBoxIconPath);
        ShortcutInfo? second = ShortcutHelper.ReadStoredMetadata(applicationLink);
        Require(
            second is not null &&
            PathsEqual(second.TargetPath, secondTarget) &&
            second.Arguments == "--second",
            "core-overwrite-application",
            "The overwritten application shortcut metadata did not match.");

        ShortcutInfo? resolved = ShortcutHelper.Resolve(applicationLink);
        Require(
            resolved is not null && PathsEqual(resolved.TargetPath, secondTarget),
            "core-resolve-valid",
            "No-UI Resolve did not preserve the valid target metadata.");

        File.Delete(secondTarget);
        ShortcutInfo? missingResolved = ShortcutHelper.Resolve(applicationLink);
        Require(
            missingResolved is not null && PathsEqual(missingResolved.TargetPath, secondTarget),
            "core-resolve-missing",
            "No-UI Resolve did not return stored metadata for the missing target.");

        string firstFolder = Directory.CreateDirectory(
            Path.Combine(root, "folder-target-one")).FullName;
        string secondFolder = Directory.CreateDirectory(
            Path.Combine(root, "folder-target-two")).FullName;
        string folderLink = Path.Combine(root, "folder.lnk");
        ShortcutHelper.CreateOrUpdateFolderShortcut(folderLink, firstFolder, "first folder");
        ShortcutInfo? firstFolderMetadata = ShortcutHelper.ReadStoredMetadata(folderLink);
        Require(
            firstFolderMetadata is not null &&
            PathsEqual(firstFolderMetadata.TargetPath, firstFolder) &&
            firstFolderMetadata.Description == "first folder",
            "core-create-folder",
            "The first folder shortcut metadata did not match.");

        ShortcutHelper.CreateOrUpdateFolderShortcut(folderLink, secondFolder, "second folder");
        ShortcutInfo? secondFolderMetadata = ShortcutHelper.ReadStoredMetadata(folderLink);
        Require(
            secondFolderMetadata is not null &&
            PathsEqual(secondFolderMetadata.TargetPath, secondFolder) &&
            PathsEqual(secondFolderMetadata.WorkingDirectory, secondFolder) &&
            secondFolderMetadata.Description == "second folder",
            "core-overwrite-folder",
            "The overwritten folder shortcut metadata did not match.");

        string corruptLink = Path.Combine(root, "corrupt.lnk");
        File.WriteAllText(corruptLink, "not a shell link");
        ShortcutHelper.InvalidateStoredMetadataCache(corruptLink);
        Require(
            ShortcutHelper.ReadStoredMetadata(corruptLink) is null,
            "core-corrupt-read",
            "A corrupt shortcut unexpectedly produced metadata.");

        result.ApplicationShortcutPath = applicationLink;
        result.FolderShortcutPath = folderLink;
        result.MissingTargetPath = secondTarget;
    }

    private void RunUiShortcutSmoke(
        AotShortcutSmokeScenario scenario,
        AotShortcutSmokeResult result,
        string resultPath)
    {
        if (_trayHost is null)
        {
            throw new InvalidOperationException("The tray owner window is unavailable.");
        }

        nint ownerHwnd = TrayHostWindowHandle;
        if (ownerHwnd == IntPtr.Zero)
        {
            throw new InvalidOperationException("The tray owner HWND is zero.");
        }

        string root = result.FixtureRoot;
        string targetPath = Path.Combine(root, "shell-ui-target.txt");
        string replacementPath = Path.Combine(root, "shell-ui-replacement.txt");
        string shortcutPath = Path.Combine(root, "shell-ui.lnk");
        File.WriteAllText(targetPath, "shell UI target");
        DragDropPermissionService.CreateOrUpdateShortcut(
            shortcutPath,
            targetPath,
            "--shell-ui",
            DragDropPermissionService.DeskBoxIconPath);

        if (scenario == AotShortcutSmokeScenario.UiRepair)
        {
            // Preserve the file identity so Windows distributed-link tracking can
            // update the shortcut without enabling heuristic target search.
            File.Move(targetPath, replacementPath);
        }
        else if (scenario != AotShortcutSmokeScenario.UiValid)
        {
            File.Delete(targetPath);
        }

        result.OwnerHwnd = $"0x{ownerHwnd:X}";
        result.ApplicationShortcutPath = shortcutPath;
        result.MissingTargetPath = targetPath;
        result.ReplacementTargetPath = replacementPath;
        result.State = "AwaitingShellUi";
        CaptureNativeEvidence(result);
        WriteSmokeResult(resultPath, result);

        BrokenShortcutResolution resolution =
            ShortcutHelper.ResolveBrokenShortcutWithShellUi(shortcutPath, ownerHwnd);
        bool shortcutExists = File.Exists(shortcutPath);
        ShortcutInfo? metadata = shortcutExists
            ? ShortcutHelper.ReadStoredMetadata(shortcutPath)
            : null;

        result.UiResolution = resolution.ToString();
        result.UiShortcutExistsAfter = shortcutExists;
        result.UiTargetAfter = metadata?.TargetPath;

        switch (scenario)
        {
            case AotShortcutSmokeScenario.UiValid:
                Require(
                    resolution == BrokenShortcutResolution.ResolvedOrKept &&
                    shortcutExists &&
                    metadata is not null &&
                    PathsEqual(metadata.TargetPath, targetPath),
                    "ui-valid-owner-resolve",
                    "Valid owner-HWND Resolve changed or removed the shortcut.");
                break;

            case AotShortcutSmokeScenario.UiCancel:
                Require(
                    resolution == BrokenShortcutResolution.ResolvedOrKept &&
                    shortcutExists &&
                    metadata is not null &&
                    PathsEqual(metadata.TargetPath, targetPath),
                    "ui-cancel-kept",
                    "Cancel did not keep the original broken shortcut.");
                break;

            case AotShortcutSmokeScenario.UiDelete:
                Require(
                    resolution == BrokenShortcutResolution.ShortcutDeleted && !shortcutExists,
                    "ui-delete-removed",
                    "Delete did not remove the broken shortcut.");
                break;

            case AotShortcutSmokeScenario.UiRepair:
                Require(
                    resolution == BrokenShortcutResolution.ResolvedOrKept &&
                    shortcutExists &&
                    metadata is not null &&
                    !PathsEqual(metadata.TargetPath, targetPath) &&
                    File.Exists(Environment.ExpandEnvironmentVariables(metadata.TargetPath)),
                    "ui-repair-updated",
                    "Repair did not update the shortcut to an existing target.");
                break;
        }
    }

    private static void CaptureNativeEvidence(AotShortcutSmokeResult result)
    {
        ShortcutNativeDiagnosticState diagnostic =
            ShortcutNativeBackend.CaptureDiagnosticState();
        result.SelectedBackend = diagnostic.SelectedBackend;
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

        // Accessing Default here is intentional after an operation: it proves the exact
        // managed loader instance owns a real module handle in this AOT process.
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

    private static string? ComputeFileSha256(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static void Require(
        bool condition,
        string step,
        string failureMessage)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"{step}: {failureMessage}");
        }

        // The stable step identifiers make the structured result independently auditable.
        // The active result is supplied by the caller after each successful assertion.
        AotShortcutSmokeStepSink.Current.Value?.Add(step);
    }

    private static void WriteSmokeResult(string resultPath, AotShortcutSmokeResult result)
    {
        AotShortcutSmokeStepSink.Current.Value = result.Steps;
        string temporaryPath = resultPath + ".tmp";
        string json = JsonSerializer.Serialize(
            result,
            AotShortcutSmokeJsonContext.Default.AotShortcutSmokeResult);
        File.WriteAllText(temporaryPath, json);
        File.Move(temporaryPath, resultPath, overwrite: true);
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPathEqualOrInside(string root, string candidate)
    {
        string normalizedRoot = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        string normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return normalizedCandidate.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
               normalizedCandidate.StartsWith(
                   normalizedRoot + Path.DirectorySeparatorChar,
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string GetScenarioDirectoryName(AotShortcutSmokeScenario scenario) =>
        scenario switch
        {
            AotShortcutSmokeScenario.Core => "core",
            AotShortcutSmokeScenario.UiValid => "ui-valid",
            AotShortcutSmokeScenario.UiCancel => "ui-cancel",
            AotShortcutSmokeScenario.UiDelete => "ui-delete",
            AotShortcutSmokeScenario.UiRepair => "ui-repair",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
}

internal static class AotShortcutSmokeStepSink
{
    internal static readonly AsyncLocal<List<string>?> Current = new();
}

internal enum AotShortcutSmokeScenario
{
    Core,
    UiValid,
    UiCancel,
    UiDelete,
    UiRepair
}

internal sealed class AotShortcutSmokeResult
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
    public string? SelectedBackend { get; set; }
    public string? ModuleName { get; set; }
    public string? ModulePath { get; set; }
    public string? ModuleSha256 { get; set; }
    public string? ModuleHandle { get; set; }
    public bool LoadAttempted { get; set; }
    public string? LoadState { get; set; }
    public uint? AbiVersion { get; set; }
    public ulong? Capabilities { get; set; }
    public string? ApplicationShortcutPath { get; set; }
    public string? FolderShortcutPath { get; set; }
    public string? MissingTargetPath { get; set; }
    public string? ReplacementTargetPath { get; set; }
    public string? OwnerHwnd { get; set; }
    public string? UiResolution { get; set; }
    public bool? UiShortcutExistsAfter { get; set; }
    public string? UiTargetAfter { get; set; }
    public List<string> Steps { get; set; } = [];
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(AotShortcutSmokeResult),
    TypeInfoPropertyName = "AotShortcutSmokeResult")]
internal partial class AotShortcutSmokeJsonContext : JsonSerializerContext
{
}
#endif
