using DeskBox.Helpers;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DeskBox.Tests;

public sealed class AotStage4D4BContractTests
{
    [Theory]
    [InlineData(null, true, 0)]
    [InlineData("csharp", true, 0)]
    [InlineData("rust", true, 1)]
    [InlineData(" RUST ", true, 1)]
    [InlineData(null, false, 1)]
    public void QuickAccessBackendPolicy_PreservesJitDefaultAndForcesRustWithoutDynamicCode(
        string? configuredValue,
        bool isDynamicCodeSupported,
        int expected)
    {
        Assert.Equal(
            (QuickAccessBackendMode)expected,
            QuickAccessBackendPolicy.Resolve(configuredValue, isDynamicCodeSupported));
    }

    [Fact]
    public void QuickAccessBoundary_KeepsTheJitOracleButCompilesItOutOfNativeAot()
    {
        string helper = ReadRepositoryFile(
            "src/DeskBox/Helpers/ExplorerQuickAccessHelper.cs");

        Assert.Contains("QuickAccessBackendPolicy.Current", helper, StringComparison.Ordinal);
        Assert.Contains("QuickAccessNativeBackend", helper, StringComparison.Ordinal);
        Assert.Contains("#if !DESKBOX_NATIVE_AOT", helper, StringComparison.Ordinal);

        var oracleBlock = new Regex(
            @"#if !DESKBOX_NATIVE_AOT\s+    private static QuickAccessPinState GetQuickAccessPinStateCSharp[\s\S]*?#endif",
            RegexOptions.CultureInvariant);
        Assert.Matches(oracleBlock, helper);
        Assert.DoesNotContain(
            "dynamic ",
            oracleBlock.Replace(helper, string.Empty),
            StringComparison.Ordinal);
    }

    [Fact]
    public void QuickAccessPublicContract_PreservesSynchronousApisAndDedicatedStaDispatch()
    {
        string helper = ReadRepositoryFile(
            "src/DeskBox/Helpers/ExplorerQuickAccessHelper.cs");

        Assert.Contains("GetQuickAccessPinStateAsync(string folderPath)", helper, StringComparison.Ordinal);
        Assert.Contains("GetQuickAccessPinState(string folderPath, out string? error)", helper, StringComparison.Ordinal);
        Assert.Contains("TryPinFolderToQuickAccessAsync(string folderPath)", helper, StringComparison.Ordinal);
        Assert.Contains("TryPinFolderToQuickAccess(string folderPath, out string? error)", helper, StringComparison.Ordinal);
        Assert.Contains("TryUnpinFolderFromQuickAccessAsync(string folderPath)", helper, StringComparison.Ordinal);
        Assert.Contains("TryUnpinFolderFromQuickAccess(string folderPath, out string? error)", helper, StringComparison.Ordinal);
        Assert.Contains("TaskCreationOptions.RunContinuationsAsynchronously", helper, StringComparison.Ordinal);
        Assert.Contains("thread.SetApartmentState(ApartmentState.STA)", helper, StringComparison.Ordinal);
        Assert.Contains("IsBackground = true", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAbi_AddsTheQuickAccessV1CapabilityAndExport()
    {
        string header = ReadRepositoryFile("native/include/deskbox_native.h");
        string rust = ReadRepositoryFile("native/deskbox-native/src/lib.rs");

        Assert.Contains(
            "DESKBOX_NATIVE_CAPABILITY_QUICK_ACCESS_V1 (1ull << 7)",
            header,
            StringComparison.Ordinal);
        Assert.Contains("DeskBoxQuickAccessRequestV1", header, StringComparison.Ordinal);
        Assert.Contains("DeskBoxQuickAccessResultV1", header, StringComparison.Ordinal);
        Assert.Contains("deskbox_quick_access_v1(", header, StringComparison.Ordinal);

        Assert.Contains("mod quick_access;", rust, StringComparison.Ordinal);
        Assert.Contains("deskbox_quick_access_v1(", rust, StringComparison.Ordinal);
        Assert.Contains("assert_eq!(deskbox_native_capabilities(), 1023);", rust, StringComparison.Ordinal);

        string implementation = ReadRepositoryFile(
            "native/deskbox-native/src/quick_access.rs");
        Assert.Contains("IShellDispatch", implementation, StringComparison.Ordinal);
        Assert.Contains("quick_access.Items()", implementation, StringComparison.Ordinal);
        Assert.Contains("items.Item(&index_variant)", implementation, StringComparison.Ordinal);
        Assert.Contains("FolderItem2", implementation, StringComparison.Ordinal);
        Assert.Contains("ExtendedProperty", implementation, StringComparison.Ordinal);
        Assert.Contains("parent.ParseName", implementation, StringComparison.Ordinal);
        Assert.Contains("item.InvokeVerb", implementation, StringComparison.Ordinal);
        Assert.Contains("CoInitializeEx", implementation, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedQuickAccessStructures_MatchTheFrozenX64Abi()
    {
        Assert.Equal(96, Marshal.SizeOf<ShortcutNativeModule.NativeQuickAccessRequest>());
        Assert.Equal(112, Marshal.SizeOf<ShortcutNativeModule.NativeQuickAccessResult>());
        Assert.Equal(
            16,
            Marshal.OffsetOf<ShortcutNativeModule.NativeQuickAccessRequest>(
                nameof(ShortcutNativeModule.NativeQuickAccessRequest.FolderPath)).ToInt32());
        Assert.Equal(
            48,
            Marshal.OffsetOf<ShortcutNativeModule.NativeQuickAccessRequest>(
                nameof(ShortcutNativeModule.NativeQuickAccessRequest.FolderName)).ToInt32());
        Assert.Equal(
            60,
            Marshal.OffsetOf<ShortcutNativeModule.NativeQuickAccessResult>(
                nameof(ShortcutNativeModule.NativeQuickAccessResult.PinState)).ToInt32());
        Assert.Equal(
            80,
            Marshal.OffsetOf<ShortcutNativeModule.NativeQuickAccessResult>(
                nameof(ShortcutNativeModule.NativeQuickAccessResult.Reserved1)).ToInt32());
    }

    [Fact]
    public void RustQuickAccess_RealModuleExposesTheCapabilityAndExportWithoutMutatingState()
    {
        ShortcutNativeLoadResult load = ShortcutNativeModule.Default;

        Assert.True(load.Success, $"{load.Failure}: {load.Detail}");
        Assert.NotNull(load.Module);
        Assert.NotEqual(0, load.Module!.ModuleHandle);
        Assert.NotEqual(
            0UL,
            load.Module.Capabilities & ShortcutNativeModule.QuickAccessCapability);
        Assert.True(
            NativeLibrary.TryGetExport(
                load.Module.ModuleHandle,
                "deskbox_quick_access_v1",
                out nint exportAddress));
        Assert.NotEqual(0, exportAddress);
    }

    [Fact]
    public void NativeBuildAndAotAudit_RequireTheStage4D4BContract()
    {
        string buildScript = ReadRepositoryFile("scripts/build-rust-native.ps1");
        string auditScript = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("deskbox_quick_access_v1", buildScript, StringComparison.Ordinal);
        Assert.Contains("Rust native Stage 5B-4C1B2B capability mismatch: expected 1023", buildScript, StringComparison.Ordinal);

        Assert.Contains("$auditProfileVersion = 62", auditScript, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", auditScript, StringComparison.Ordinal);
        Assert.Contains("stage4D4BWarningMessages", auditScript, StringComparison.Ordinal);
        Assert.Contains("QuickAccessNativeBackend.cs", auditScript, StringComparison.Ordinal);
        Assert.Contains("quickAccessBackendPolicy", auditScript, StringComparison.Ordinal);
        Assert.Contains(
            "Stage 4D-4B Quick Access boundary produced AOT warnings",
            auditScript,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAotBuild_RequiresTheQuickAccessRustBoundary()
    {
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("Quick Access", project, StringComparison.Ordinal);
        Assert.Contains("DeskBoxRustNative=true", project, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
