using DeskBox.Helpers;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DeskBox.Tests;

public sealed class AotStage4D4AContractTests
{
    [Theory]
    [InlineData(null, true, 0)]
    [InlineData("csharp", true, 0)]
    [InlineData("rust", true, 1)]
    [InlineData(" RUST ", true, 1)]
    [InlineData(null, false, 1)]
    public void ExplorerShellBackendPolicy_PreservesJitDefaultAndForcesRustWithoutDynamicCode(
        string? configuredValue,
        bool isDynamicCodeSupported,
        int expected)
    {
        Assert.Equal(
            (ExplorerShellLaunchBackendMode)expected,
            ExplorerShellLaunchBackendPolicy.Resolve(configuredValue, isDynamicCodeSupported));
    }

    [Fact]
    public void ExplorerLaunch_KeepsTheJitOracleButCompilesItOutOfNativeAot()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/Helpers/ExplorerShellLaunchService.cs");

        Assert.Contains("ExplorerShellLaunchBackendPolicy.Current", source, StringComparison.Ordinal);
        Assert.Contains("ExplorerShellLaunchNativeBackend.TryOpen", source, StringComparison.Ordinal);
        Assert.Contains("#if !DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);

        var oracleBlock = new Regex(
            @"#if !DESKBOX_NATIVE_AOT\s+private static bool TryOpenCSharp\([\s\S]*?dynamic shell[\s\S]*?#endif",
            RegexOptions.CultureInvariant);
        Assert.Matches(oracleBlock, source);
        Assert.DoesNotContain(
            "dynamic ",
            oracleBlock.Replace(source, string.Empty),
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAbi_AddsTheExplorerShellLaunchV1CapabilityAndExport()
    {
        string header = ReadRepositoryFile("native/include/deskbox_native.h");
        string rust = ReadRepositoryFile("native/deskbox-native/src/lib.rs");
        string launch = ReadRepositoryFile(
            "native/deskbox-native/src/explorer_shell_launch.rs");

        Assert.Contains(
            "DESKBOX_NATIVE_CAPABILITY_EXPLORER_SHELL_LAUNCH_V1 (1ull << 6)",
            header,
            StringComparison.Ordinal);
        Assert.Contains("DeskBoxExplorerShellLaunchRequestV1", header, StringComparison.Ordinal);
        Assert.Contains("DeskBoxExplorerShellLaunchResultV1", header, StringComparison.Ordinal);
        Assert.Contains("deskbox_explorer_shell_launch_v1(", header, StringComparison.Ordinal);

        Assert.Contains("mod explorer_shell_launch;", rust, StringComparison.Ordinal);
        Assert.Contains(
            "DESKBOX_NATIVE_CAPABILITY_EXPLORER_SHELL_LAUNCH_V1",
            rust,
            StringComparison.Ordinal);
        Assert.Contains("deskbox_explorer_shell_launch_v1(", rust, StringComparison.Ordinal);
        Assert.Contains("assert_eq!(deskbox_native_capabilities(), 511);", rust, StringComparison.Ordinal);
        Assert.Contains("IShellDispatch", launch, StringComparison.Ordinal);
        Assert.Contains("local_shell.Windows()", launch, StringComparison.Ordinal);
        Assert.Contains("IShellWindows", launch, StringComparison.Ordinal);
        Assert.Contains("FindWindowSW", launch, StringComparison.Ordinal);
        Assert.Contains("IWebBrowser", launch, StringComparison.Ordinal);
        Assert.Contains("desktop.Document()", launch, StringComparison.Ordinal);
        Assert.Contains("IShellFolderViewDual", launch, StringComparison.Ordinal);
        Assert.Contains("document.Application()", launch, StringComparison.Ordinal);
        Assert.Contains("explorer_shell.ShellExecute", launch, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedExplorerShellStructures_MatchTheFrozenX64Abi()
    {
        Assert.Equal(
            96,
            Marshal.SizeOf<ShortcutNativeModule.NativeExplorerShellLaunchRequest>());
        Assert.Equal(
            88,
            Marshal.SizeOf<ShortcutNativeModule.NativeExplorerShellLaunchResult>());
        Assert.Equal(
            16,
            Marshal.OffsetOf<ShortcutNativeModule.NativeExplorerShellLaunchRequest>(
                nameof(ShortcutNativeModule.NativeExplorerShellLaunchRequest.Path)).ToInt32());
        Assert.Equal(
            48,
            Marshal.OffsetOf<ShortcutNativeModule.NativeExplorerShellLaunchResult>(
                nameof(ShortcutNativeModule.NativeExplorerShellLaunchResult.OperationSucceeded))
                .ToInt32());
    }

    [Fact]
    public void RustExplorerShellLaunch_RealModuleExposesTheCapabilityAndExport()
    {
        ShortcutNativeLoadResult load = ShortcutNativeModule.Default;

        Assert.True(load.Success, $"{load.Failure}: {load.Detail}");
        Assert.NotNull(load.Module);
        Assert.NotEqual(0, load.Module!.ModuleHandle);
        Assert.NotEqual(
            0UL,
            load.Module.Capabilities & ShortcutNativeModule.ExplorerShellLaunchCapability);
        Assert.True(
            NativeLibrary.TryGetExport(
                load.Module.ModuleHandle,
                "deskbox_explorer_shell_launch_v1",
                out nint exportAddress));
        Assert.NotEqual(0, exportAddress);
    }

    [Fact]
    public void NativeBuildAndAotAudit_RequireTheStage4D4AContract()
    {
        string buildScript = ReadRepositoryFile("scripts/build-rust-native.ps1");
        string auditScript = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("deskbox_explorer_shell_launch_v1", buildScript, StringComparison.Ordinal);
        Assert.Contains("Rust native Stage 5B-4C1B2B capability mismatch: expected 511", buildScript, StringComparison.Ordinal);

        Assert.Contains("$auditProfileVersion = 61", auditScript, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", auditScript, StringComparison.Ordinal);
        Assert.Contains("stage4D4AWarningMessages", auditScript, StringComparison.Ordinal);
        Assert.Contains("ExplorerShellLaunchService.cs", auditScript, StringComparison.Ordinal);
        Assert.Contains("explorerShellBackendPolicy", auditScript, StringComparison.Ordinal);
        Assert.Contains(
            "Stage 4D-4A Explorer-shell boundary produced AOT warnings",
            auditScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAotBuild_RequiresTheExplorerShellRustBoundary()
    {
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("Explorer-shell", project, StringComparison.Ordinal);
        Assert.Contains("DeskBoxRustNative=true", project, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
