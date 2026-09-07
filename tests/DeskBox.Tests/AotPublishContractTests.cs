using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DeskBox.Tests;

public sealed class AotPublishContractTests
{
    [Fact]
    public void AuditProfile_IsOptInAndPropagatesAotToUpdater()
    {
        XDocument project = XDocument.Load(TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"));

        XElement defaultAuditProperty = project
            .Descendants("DeskBoxAotAudit")
            .Single(element => element.Attribute("Condition") is not null);
        Assert.Equal("false", defaultAuditProperty.Value);

        XElement auditGroup = project
            .Descendants("PropertyGroup")
            .Single(element => (string?)element.Attribute("Condition") == "'$(DeskBoxAotAudit)' == 'true'");
        Assert.Equal("true", auditGroup.Element("PublishAot")?.Value);
        Assert.Equal("true", auditGroup.Element("SelfContained")?.Value);
        Assert.Equal("false", auditGroup.Element("WindowsAppSDKSelfContained")?.Value);
        Assert.Equal("false", auditGroup.Element("PublishSingleFile")?.Value);

        XElement nativeAotGroup = project
            .Descendants("PropertyGroup")
            .Single(element => (string?)element.Attribute("Condition") == "'$(PublishAot)' == 'true'");
        Assert.Contains(
            "DESKBOX_NATIVE_AOT",
            nativeAotGroup.Element("DefineConstants")?.Value,
            StringComparison.Ordinal);

        XElement publishUpdater = project
            .Descendants("Target")
            .Single(element => (string?)element.Attribute("Name") == "PublishDeskBoxUpdater");
        string properties = Assert.IsType<string>((string?)publishUpdater.Element("MSBuild")?.Attribute("Properties"));
        Assert.Contains("DeskBoxAotAudit=$(DeskBoxAotAudit)", properties, StringComparison.Ordinal);
        Assert.Contains("PublishAot=$(PublishAot)", properties, StringComparison.Ordinal);
        Assert.Contains(
            "IlcUseEnvironmentalTools=$(IlcUseEnvironmentalTools)",
            properties,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AuditProfile_DisablesDefaultJsonReflectionOnlyForAuditBuilds()
    {
        foreach (string projectPath in new[]
                 {
                     "src/DeskBox/DeskBox.csproj",
                     "src/DeskBox.Abstractions/DeskBox.Abstractions.csproj",
                     "src/DeskBox.Updater/DeskBox.Updater.csproj"
                 })
        {
            XDocument project = XDocument.Load(TestPaths.FromRepository(projectPath));
            XElement reflectionSwitch = Assert.Single(
                project.Descendants("JsonSerializerIsReflectionEnabledByDefault"));

            Assert.Equal("false", reflectionSwitch.Value);
            Assert.Equal(
                "'$(DeskBoxAotAudit)' == 'true'",
                (string?)reflectionSwitch.Parent?.Attribute("Condition"));
        }

        string script = File.ReadAllText(
            TestPaths.FromRepository("scripts/publish-aot-audit.ps1"));
        const string reflectionArgument =
            "\"-p:JsonSerializerIsReflectionEnabledByDefault=$($jsonSerializerIsReflectionEnabledByDefault.ToString().ToLowerInvariant())\"";

        Assert.Contains(
            "$jsonSerializerIsReflectionEnabledByDefault = $false",
            script,
            StringComparison.Ordinal);
        Assert.Equal(
            2,
            Regex.Matches(script, Regex.Escape(reflectionArgument)).Count);
        Assert.Contains("jsonSerializer = [ordered]@{", script, StringComparison.Ordinal);
        Assert.Contains(
            "reflectionEnabledByDefault = $jsonSerializerIsReflectionEnabledByDefault",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AuditProfile_DoesNotCopyManagedUpdaterBuildOutput()
    {
        XDocument project = XDocument.Load(TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"));

        foreach (string targetName in new[] { "BuildDeskBoxUpdater", "CopyDeskBoxUpdater" })
        {
            XElement target = project
                .Descendants("Target")
                .Single(element => (string?)element.Attribute("Name") == targetName);
            string condition = Assert.IsType<string>((string?)target.Attribute("Condition"));
            Assert.Contains("'$(PublishAot)' != 'true'", condition, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NativeAotConfigurationValidation_AllowsCompleteX64AndArm64Combinations()
    {
        ProcessResult ordinaryJit = await RunAotConfigurationValidationAsync(
            "Platform=x64",
            "RuntimeIdentifier=win-x64");
        Assert.Equal(0, ordinaryJit.ExitCode);

        ProcessResult directAot = await RunAotConfigurationValidationAsync(
            "PublishAot=true",
            "DeskBoxRustNative=true",
            "Platform=x64",
            "RuntimeIdentifier=win-x64");
        Assert.Equal(0, directAot.ExitCode);

        ProcessResult auditAot = await RunAotConfigurationValidationAsync(
            "DeskBoxAotAudit=true",
            "DeskBoxRustNative=true",
            "Platform=x64",
            "RuntimeIdentifier=win-x64");
        Assert.Equal(0, auditAot.ExitCode);

        ProcessResult arm64Aot = await RunAotConfigurationValidationAsync(
            "PublishAot=true",
            "DeskBoxRustNative=true",
            "Platform=ARM64",
            "RuntimeIdentifier=win-arm64");
        Assert.Equal(0, arm64Aot.ExitCode);
    }

    [Theory]
    [InlineData("PublishAot=true", "DeskBoxRustNative=false")]
    [InlineData("DeskBoxAotAudit=true", "DeskBoxRustNative=false")]
    public async Task NativeAotConfigurationValidation_RejectsMissingRustModule(
        string aotProperty,
        string rustProperty)
    {
        ProcessResult result = await RunAotConfigurationValidationAsync(
            aotProperty,
            rustProperty,
            "Platform=x64",
            "RuntimeIdentifier=win-x64");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "requires DeskBoxRustNative=true",
            result.Output,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("x64", "win-arm64")]
    [InlineData("ARM64", "win-x64")]
    public async Task NativeAotConfigurationValidation_RejectsMismatchedArchitecture(
        string platform,
        string runtimeIdentifier)
    {
        ProcessResult result = await RunAotConfigurationValidationAsync(
            "PublishAot=true",
            "DeskBoxRustNative=true",
            $"Platform={platform}",
            $"RuntimeIdentifier={runtimeIdentifier}");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "requires a matching Platform/RuntimeIdentifier pair",
            result.Output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task AotAudit_RejectsArm64BeforeResolvingToolsOrTouchingArtifacts()
    {
        string scriptPath = TestPaths.FromRepository("scripts/publish-aot-audit.ps1");
        string script = File.ReadAllText(scriptPath);
        int guard = script.IndexOf("if ($Platform -ne \"x64\")", StringComparison.Ordinal);
        int dotnetResolution = script.IndexOf("$dotnet = if", StringComparison.Ordinal);
        int artifactCleanup = script.IndexOf("Remove-Item -LiteralPath $runRoot", StringComparison.Ordinal);

        Assert.InRange(guard, 0, dotnetResolution - 1);
        Assert.InRange(guard, 0, artifactCleanup - 1);
        Assert.Contains("$rustNativeEnabled = $true", script, StringComparison.Ordinal);
        Assert.DoesNotContain("$rustNativeEnabled = $Platform -eq \"x64\"", script, StringComparison.Ordinal);

        ProcessResult result = await RunProcessAsync(
            "powershell.exe",
            "-NoProfile",
            "-ExecutionPolicy",
            "Bypass",
            "-File",
            scriptPath,
            "-Platform",
            "ARM64",
            "-DotNetPath",
            Path.Combine(Path.GetTempPath(), $"missing-dotnet-{Guid.NewGuid():N}.exe"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "currently supports only x64",
            result.Output,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "explicitly selected dotnet host does not exist",
            result.Output,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToolchainsAndAuditScript_ArePinnedAndValidateNativeOutput()
    {
        using JsonDocument globalJson = JsonDocument.Parse(
            File.ReadAllText(TestPaths.FromRepository("global.json")));
        JsonElement sdk = globalJson.RootElement.GetProperty("sdk");
        Assert.Equal("10.0.303", sdk.GetProperty("version").GetString());
        Assert.Equal("latestPatch", sdk.GetProperty("rollForward").GetString());

        string rustToolchain = File.ReadAllText(TestPaths.FromRepository("rust-toolchain.toml"));
        Assert.Contains("channel = \"1.96.0\"", rustToolchain, StringComparison.Ordinal);
        Assert.Contains("x86_64-pc-windows-msvc", rustToolchain, StringComparison.Ordinal);

        string script = File.ReadAllText(TestPaths.FromRepository("scripts/publish-aot-audit.ps1"));
        Assert.Contains("-p:DeskBoxAotAudit=true", script, StringComparison.Ordinal);
        Assert.Contains("coreclr.dll", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DeskBox.Updater.dll", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Get-PeMachine", script, StringComparison.Ordinal);
        Assert.Contains("Move-Item", script, StringComparison.Ordinal);
        Assert.DoesNotContain(".codex-temp", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gitDirty", script, StringComparison.Ordinal);
        Assert.Contains("workingTreeFingerprint", script, StringComparison.Ordinal);
        Assert.Contains("dotnetSdkVersion", script, StringComparison.Ordinal);
        Assert.Contains("--artifacts-path", script, StringComparison.Ordinal);
        Assert.Contains("buildArtifactsDirectory", script, StringComparison.Ordinal);
        Assert.Contains("DeskBox.pdb", script, StringComparison.Ordinal);
        Assert.Contains("DeskBox.Updater.pdb", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RustNativeStage3C2_ImplementsReadWriteAndWindowsUiAbi2Capabilities()
    {
        string workspace = File.ReadAllText(TestPaths.FromRepository("native/Cargo.toml"));
        string crate = File.ReadAllText(TestPaths.FromRepository("native/deskbox-native/Cargo.toml"));
        string source = File.ReadAllText(TestPaths.FromRepository("native/deskbox-native/src/lib.rs"));
        string shortcutSource = File.ReadAllText(
            TestPaths.FromRepository("native/deskbox-native/src/shortcut.rs"));
        string header = File.ReadAllText(TestPaths.FromRepository("native/include/deskbox_native.h"));
        string buildScript = File.ReadAllText(TestPaths.FromRepository("scripts/build-rust-native.ps1"));
        string contract = File.ReadAllText(
            TestPaths.FromRepository("docs/architecture/shortcut-native-abi-v2.md"));
        string lockFile = File.ReadAllText(TestPaths.FromRepository("native/Cargo.lock"));

        Assert.Contains("members = [", workspace, StringComparison.Ordinal);
        Assert.Contains("\"deskbox-native\"", workspace, StringComparison.Ordinal);
        Assert.Contains("\"deskbox-audio-session-fixture\"", workspace, StringComparison.Ordinal);
        Assert.DoesNotContain("\"deskbox-search-core\"", workspace, StringComparison.Ordinal);
        Assert.Contains("panic = \"abort\"", workspace, StringComparison.Ordinal);
        Assert.Contains("crate-type = [\"cdylib\"]", crate, StringComparison.Ordinal);
        Assert.Contains("rust-version = \"1.96\"", crate, StringComparison.Ordinal);
        Assert.Contains("windows = { version = \"0.62.2\"", crate, StringComparison.Ordinal);
        Assert.Contains("\"Win32_System_Com\"", crate, StringComparison.Ordinal);
        Assert.Contains("\"Win32_UI_Shell\"", crate, StringComparison.Ordinal);
        Assert.Contains("pub const DESKBOX_NATIVE_ABI_VERSION: u32 = 2", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_NATIVE_CAPABILITY_SHORTCUT_READ_STORED_RAW_V2", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_NATIVE_CAPABILITY_SHORTCUT_READ_EFFECTIVE_DIAGNOSTIC_V2", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_NATIVE_CAPABILITY_SHORTCUT_WRITE_V2", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_NATIVE_CAPABILITY_SHORTCUT_RESOLVE_WITH_UI_V2", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_NATIVE_CAPABILITY_MUSIC_VOLUME_V1", source, StringComparison.Ordinal);
        Assert.DoesNotContain("pub const DESKBOX_NATIVE_CAPABILITIES: u64 = 0", source, StringComparison.Ordinal);
        Assert.Contains("pub extern \"C\" fn deskbox_native_abi_version()", source, StringComparison.Ordinal);
        Assert.Contains("pub extern \"C\" fn deskbox_native_capabilities()", source, StringComparison.Ordinal);
        Assert.Contains("pub unsafe extern \"C\" fn deskbox_shortcut_read_v2(", source, StringComparison.Ordinal);
        Assert.Contains("pub unsafe extern \"C\" fn deskbox_shortcut_resolve_no_ui_v2(", source, StringComparison.Ordinal);
        Assert.Contains("pub unsafe extern \"C\" fn deskbox_shortcut_write_v2(", source, StringComparison.Ordinal);
        Assert.Contains("pub unsafe extern \"C\" fn deskbox_shortcut_resolve_with_ui_v2(", source, StringComparison.Ordinal);
        Assert.Contains("shortcut::read_shortcut", source, StringComparison.Ordinal);
        Assert.Contains("shortcut::resolve_shortcut", source, StringComparison.Ordinal);
        Assert.Contains("shortcut::write_shortcut", source, StringComparison.Ordinal);
        Assert.Contains("shortcut::resolve_shortcut_with_ui", source, StringComparison.Ordinal);
        Assert.Contains("CoInitializeEx", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("RPC_E_CHANGED_MODE", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("SLGP_RAWPATH", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("SLR_NO_UI", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("SLR_NOSEARCH", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("SLR_UPDATE", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("SLR_OFFER_DELETE_WITHOUT_FILE", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_SHORTCUT_PHASE_RESOLVE", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_SHORTCUT_PHASE_SAVE", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("SetPath", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("SetDescription", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("SetArguments", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("SetWorkingDirectory", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("SetIconLocation", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("DIAGNOSTIC_ARGUMENT_CAPACITY: usize = 512", shortcutSource, StringComparison.Ordinal);
        Assert.Contains("#define DESKBOX_NATIVE_ABI_VERSION 2u", header, StringComparison.Ordinal);
        Assert.Contains("#define DESKBOX_NATIVE_CAPABILITIES_STAGE_3C2", header, StringComparison.Ordinal);
        Assert.Contains("#define DESKBOX_NATIVE_CAPABILITIES_STAGE_4C", header, StringComparison.Ordinal);
        Assert.Contains("#define DESKBOX_NATIVE_CAPABILITIES_STAGE_4D4A", header, StringComparison.Ordinal);
        Assert.Contains("#define DESKBOX_NATIVE_CAPABILITIES_STAGE_4D4B", header, StringComparison.Ordinal);
        Assert.Contains("#define DESKBOX_NATIVE_CAPABILITIES_STAGE_5B4C1B1", header, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_NATIVE_CAPABILITY_RECYCLE_BIN_V1", header, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_NATIVE_CAPABILITIES DESKBOX_NATIVE_CAPABILITIES_STAGE_5B4C1B1", header, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_SHORTCUT_READ_REQUEST_V2_SIZE_64 144u", header, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_SHORTCUT_READ_RESULT_V2_SIZE_64 136u", header, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_SHORTCUT_RESOLVE_REQUEST_V2_SIZE_64 192u", header, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_SHORTCUT_WRITE_REQUEST_V2_SIZE_64 144u", header, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_SHORTCUT_WRITE_RESULT_V2_SIZE_64 96u", header, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_SHORTCUT_UI_RESOLVE_REQUEST_V2_SIZE_64 64u", header, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_SHORTCUT_UI_RESOLVE_RESULT_V2_SIZE_64 64u", header, StringComparison.Ordinal);
        Assert.Contains("typedef struct DeskBoxShortcutReadRequestV2", header, StringComparison.Ordinal);
        Assert.Contains("typedef struct DeskBoxShortcutReadResultV2", header, StringComparison.Ordinal);
        Assert.Contains("typedef struct DeskBoxShortcutResolveRequestV2", header, StringComparison.Ordinal);
        Assert.Contains("typedef struct DeskBoxShortcutWriteRequestV2", header, StringComparison.Ordinal);
        Assert.Contains("typedef struct DeskBoxShortcutWriteResultV2", header, StringComparison.Ordinal);
        Assert.Contains("typedef struct DeskBoxShortcutUiResolveRequestV2", header, StringComparison.Ordinal);
        Assert.Contains("typedef struct DeskBoxShortcutUiResolveResultV2", header, StringComparison.Ordinal);
        Assert.Contains("deskbox_native_abi_version(void)", header, StringComparison.Ordinal);
        Assert.Contains("deskbox_native_capabilities(void)", header, StringComparison.Ordinal);
        Assert.Contains("deskbox_shortcut_read_v2(", header, StringComparison.Ordinal);
        Assert.Contains("deskbox_shortcut_resolve_no_ui_v2(", header, StringComparison.Ordinal);
        Assert.Contains("deskbox_shortcut_write_v2(", header, StringComparison.Ordinal);
        Assert.Contains("deskbox_shortcut_resolve_with_ui_v2(", header, StringComparison.Ordinal);
        Assert.Contains("--locked", buildScript, StringComparison.Ordinal);
        Assert.Contains("x86_64-pc-windows-msvc", buildScript, StringComparison.Ordinal);
        Assert.Contains("ReadContract", buildScript, StringComparison.Ordinal);
        Assert.Contains("Rust native ABI mismatch", buildScript, StringComparison.Ordinal);
        Assert.Contains("Rust native Stage 5B-4C1B2B capability mismatch: expected 511", buildScript, StringComparison.Ordinal);
        Assert.Contains("deskbox_shortcut_read_v2", buildScript, StringComparison.Ordinal);
        Assert.Contains("deskbox_shortcut_resolve_no_ui_v2", buildScript, StringComparison.Ordinal);
        Assert.Contains("deskbox_shortcut_write_v2", buildScript, StringComparison.Ordinal);
        Assert.Contains("deskbox_shortcut_resolve_with_ui_v2", buildScript, StringComparison.Ordinal);
        Assert.Contains("deskbox_explorer_shell_launch_v1", buildScript, StringComparison.Ordinal);
        Assert.Contains("deskbox_quick_access_v1", buildScript, StringComparison.Ordinal);
        Assert.Contains("CargoTargetDirectory", buildScript, StringComparison.Ordinal);
        Assert.Contains("--target-dir", buildScript, StringComparison.Ordinal);
        Assert.Contains("ValidateOnly", buildScript, StringComparison.Ordinal);
        Assert.Contains("能力掩码", contract, StringComparison.Ordinal);
        Assert.Contains("RPC_E_CHANGED_MODE", contract, StringComparison.Ordinal);
        Assert.Contains("SLR_NO_UI | SLR_NOSEARCH", contract, StringComparison.Ordinal);
        Assert.Contains(
            "SLR_UPDATE | SLR_NOSEARCH | SLR_OFFER_DELETE_WITHOUT_FILE",
            contract,
            StringComparison.Ordinal);
        Assert.Contains("name = \"windows\"", lockFile, StringComparison.Ordinal);
        Assert.Contains("version = \"0.62.2\"", lockFile, StringComparison.Ordinal);
    }

    [Fact]
    public void RustNativeStage3C2_HasSafeExplicitProductLoaderWithoutFallback()
    {
        string loader = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Helpers/ShortcutNativeBackend.cs"));
        string helper = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Helpers/ShortcutHelper.cs"));
        string dragDrop = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/DragDropPermissionService.cs"));
        string testProject = File.ReadAllText(
            TestPaths.FromRepository("tests/DeskBox.Tests/DeskBox.Tests.csproj"));
        string differentialTests = File.ReadAllText(
            TestPaths.FromRepository("tests/DeskBox.Tests/ShortcutNativeDifferentialTests.cs"));
        string fileNavigation = File.ReadAllText(
            TestPaths.FromRepository(
                "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.Navigation.cs"));

        Assert.Contains("DESKBOX_SHORTCUT_BACKEND", loader, StringComparison.Ordinal);
        Assert.Contains("RuntimeFeature.IsDynamicCodeSupported", loader, StringComparison.Ordinal);
        Assert.Contains("return ShortcutBackendMode.Rust", loader, StringComparison.Ordinal);
        Assert.Contains("LoadLibraryExW", loader, StringComparison.Ordinal);
        Assert.Contains("LoadLibrarySearchDllLoadDir", loader, StringComparison.Ordinal);
        Assert.Contains("LoadLibrarySearchSystem32", loader, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(AppContext.BaseDirectory, DllName)", loader, StringComparison.Ordinal);
        Assert.Contains("NativeLibrary.TryGetExport", loader, StringComparison.Ordinal);
        Assert.Contains("delegate* unmanaged[Cdecl]", loader, StringComparison.Ordinal);
        Assert.Contains("CapabilityUnavailable", loader, StringComparison.Ordinal);
        Assert.Contains("WriteCapability", loader, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeWriteCallResult", loader, StringComparison.Ordinal);
        Assert.Contains("ResolveWithUiCapability", loader, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeUiResolveCallResult", loader, StringComparison.Ordinal);
        Assert.DoesNotContain("DllImport(\"deskbox_native", loader, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LibraryImport(\"deskbox_native", loader, StringComparison.OrdinalIgnoreCase);

        int urlDispatch = helper.IndexOf(".Equals(\".url\"", StringComparison.Ordinal);
        int backendDispatch = helper.IndexOf("ShortcutBackendPolicy.Current", StringComparison.Ordinal);
        Assert.True(urlDispatch >= 0 && backendDispatch > urlDispatch);
        Assert.Contains("ShortcutNativeBackend.ReadStoredRaw", helper, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeBackend.ResolveNoUi", helper, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeBackend.WriteShortcut", helper, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeBackend.WriteShellNamespaceShortcut", helper, StringComparison.Ordinal);
        Assert.Contains("CreateShellApplicationShortcut", helper, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeBackend.ResolveWithUi", helper, StringComparison.Ordinal);
        Assert.Contains("Explicit Rust", helper, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeBackend.ReadEffectiveDiagnostic", dragDrop, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeBackend.WriteShortcut", dragDrop, StringComparison.Ordinal);
        Assert.Contains("return native.Success", dragDrop, StringComparison.Ordinal);

        Assert.Contains("BuildDeskBoxNativeTestModule", testProject, StringComparison.Ordinal);
        Assert.Contains("ShortcutNativeDifferentialTests", differentialTests, StringComparison.Ordinal);
        Assert.Contains("StoredRaw_ConcurrentReadsMatchCSharpOracle", differentialTests, StringComparison.Ordinal);
        Assert.Contains("PidlOnlyShortcut", differentialTests, StringComparison.Ordinal);
        Assert.Contains("Write_AllFieldsAndNegativeIconIndexMatchCSharpOracle", differentialTests, StringComparison.Ordinal);
        Assert.Contains("ApplicationShortcutWriteInvalidatesStoredMetadataCache", differentialTests, StringComparison.Ordinal);
        Assert.Contains("FolderShortcutWriteCreatesParentAndInvalidatesStoredMetadataCache", differentialTests, StringComparison.Ordinal);
        Assert.Contains("ResolveWithUi_ValidShortcutForwardsOwnerAndFrozenFlags", differentialTests, StringComparison.Ordinal);
        Assert.Contains("ApplicationShortcutUiResolveKeepsLinkAndInvalidatesStoredMetadataCache", differentialTests, StringComparison.Ordinal);
        Assert.Contains("await OpenFileItemAsync(item)", fileNavigation, StringComparison.Ordinal);
    }

    [Fact]
    public void RustNativeStage3C3_ClosesAotComAndOwnerlessUiPathsWithoutDiagnosticLoads()
    {
        string loader = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Helpers/ShortcutNativeBackend.cs"));
        string helper = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Helpers/ShortcutHelper.cs"));
        string dragDrop = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/DragDropPermissionService.cs"));
        string operations = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/WidgetViewModel.Operations.cs"));
        string fileService = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/FileService.cs"));
        string diagnostics = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/Services/DeskBoxDiagnosticsBundleService.cs"));

        Assert.Contains("#if DESKBOX_NATIVE_AOT", loader, StringComparison.Ordinal);
        Assert.Contains("#if !DESKBOX_NATIVE_AOT", helper, StringComparison.Ordinal);
        Assert.Contains("#if !DESKBOX_NATIVE_AOT", dragDrop, StringComparison.Ordinal);
        Assert.DoesNotContain("public void OpenItem(WidgetItem item)", operations, StringComparison.Ordinal);
        Assert.Contains(
            "public FileService.OpenItemResult OpenItem(WidgetItem item, IntPtr ownerHwnd)",
            operations,
            StringComparison.Ordinal);
        Assert.Contains(
            "public static OpenItemResult OpenItem(WidgetItem item, IntPtr ownerHwnd)",
            fileService,
            StringComparison.Ordinal);
        Assert.Contains(
            "public static async Task<OpenItemResult> OpenItemAsync(",
            File.ReadAllText(TestPaths.FromRepository(
                "src/DeskBox/Services/FileService.OpenItem.cs")),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "OpenItem(WidgetItem item, IntPtr ownerHwnd = default)",
            fileService,
            StringComparison.Ordinal);

        int captureStart = loader.IndexOf(
            "internal static ShortcutNativeDiagnosticState CaptureDiagnosticState()",
            StringComparison.Ordinal);
        int captureEnd = loader.IndexOf(
            "internal static ShortcutNativeCallResult ReadStoredRaw",
            captureStart,
            StringComparison.Ordinal);
        Assert.True(captureStart >= 0 && captureEnd > captureStart);
        string diagnosticCapture = loader[captureStart..captureEnd];
        Assert.Contains("TryGetCachedDefault", diagnosticCapture, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData", loader, StringComparison.Ordinal);
        Assert.DoesNotContain("ShortcutNativeModule.Default", diagnosticCapture, StringComparison.Ordinal);
        Assert.DoesNotContain(".Detail", diagnosticCapture, StringComparison.Ordinal);
        Assert.Contains("DeskBoxShortcutNativeDiagnostic", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void RustNativeStage7A_RemainsOptInAndCopiesTheSelectedArchitectureModule()
    {
        XDocument project = XDocument.Load(TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"));

        XElement defaultRustProperty = project
            .Descendants("DeskBoxRustNative")
            .Single(element => element.Attribute("Condition") is not null);
        Assert.Equal("false", defaultRustProperty.Value);

        XElement buildTarget = project
            .Descendants("Target")
            .Single(element => (string?)element.Attribute("Name") == "BuildDeskBoxRustNative");
        string buildCondition = Assert.IsType<string>((string?)buildTarget.Attribute("Condition"));
        string platformErrors = string.Join(
            Environment.NewLine,
            buildTarget.Elements("Error").Select(element => (string?)element.Attribute("Text")));
        Assert.Contains("'$(DeskBoxRustNative)' == 'true'", buildCondition, StringComparison.Ordinal);
        Assert.Contains("RuntimeIdentifier=win-x64 or win-arm64", platformErrors, StringComparison.Ordinal);
        Assert.Contains("Platform=x64, ARM64", platformErrors, StringComparison.Ordinal);
        Assert.Contains("matching Platform and RuntimeIdentifier", platformErrors, StringComparison.Ordinal);

        XElement noRidSegment = project
            .Descendants("DeskBoxRustNativeRuntimeSegment")
            .Single(element => (string?)element.Attribute("Condition") == "'$(RuntimeIdentifier)' == ''");
        XElement intermediateDirectory = project.Descendants("DeskBoxRustNativeIntermediateDir").Single();
        XElement cargoTargetDirectory = project.Descendants("DeskBoxRustNativeCargoTargetDir").Single();
        Assert.Equal("no-rid", noRidSegment.Value);
        Assert.Contains("$(DeskBoxRustNativeRuntimeSegment)", intermediateDirectory.Value, StringComparison.Ordinal);
        Assert.False(intermediateDirectory.Value.EndsWith('\\'));
        Assert.Contains("$(DeskBoxRustNativeIntermediateDir)", cargoTargetDirectory.Value, StringComparison.Ordinal);
        Assert.Contains(
            "-CargoTargetDirectory \"$(DeskBoxRustNativeCargoTargetDir)\"",
            (string?)buildTarget.Element("Exec")?.Attribute("Command"),
            StringComparison.Ordinal);

        Assert.Contains(
            project.Descendants("Target"),
            element => (string?)element.Attribute("Name") == "CopyDeskBoxRustNativeToOutput");
        Assert.Contains(
            project.Descendants("Target"),
            element => (string?)element.Attribute("Name") == "CopyDeskBoxRustNativeToPublish");
    }

    [Fact]
    public void AotAudit_RequiresAndFingerprintsTheX64RustModule()
    {
        string script = File.ReadAllText(TestPaths.FromRepository("scripts/publish-aot-audit.ps1"));

        Assert.Contains("DeskBoxRustNative=$($rustNativeEnabled", script, StringComparison.Ordinal);
        Assert.Contains("deskbox_native.dll", script, StringComparison.Ordinal);
        Assert.Contains("deskbox_native.pdb", script, StringComparison.Ordinal);
        Assert.Contains("-ValidateOnly", script, StringComparison.Ordinal);
        Assert.Contains("abiVersion = $rustAbiVersion", script, StringComparison.Ordinal);
        Assert.Contains("capabilities = $rustCapabilities", script, StringComparison.Ordinal);
        Assert.Contains("requiredExports = @($rustRequiredExports)", script, StringComparison.Ordinal);
        Assert.Contains("publishedNativeModules", script, StringComparison.Ordinal);
        Assert.Contains("publishMatchesStaging", script, StringComparison.Ordinal);
        Assert.Contains("exactly one root-level deskbox_native.dll", script, StringComparison.Ordinal);
        Assert.DoesNotContain("deskbox_search_core.dll", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", script, StringComparison.Ordinal);
        Assert.Contains("auditProfileVersion = 60", script, StringComparison.Ordinal);
        Assert.Contains("warningCodeCounts", script, StringComparison.Ordinal);
        Assert.Contains("targetedWarningCounts", script, StringComparison.Ordinal);
        Assert.Contains("workingTreeFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("workingTreeFingerprintAfter", script, StringComparison.Ordinal);
        Assert.Contains("sourceStableDuringAudit", script, StringComparison.Ordinal);
        Assert.Contains("core.quotepath=false", script, StringComparison.Ordinal);
        Assert.Contains("DeskBoxRustNativeCargoTargetDir", script, StringComparison.Ordinal);
        Assert.Contains("Get-PeImports", script, StringComparison.Ordinal);
        Assert.Contains("imports = @($imports)", script, StringComparison.Ordinal);
        Assert.Contains("cargo metadata", script, StringComparison.Ordinal);
        Assert.Contains("lockedPackageCount", script, StringComparison.Ordinal);
        Assert.Contains("lockedPackages = @($rustLockedPackages)", script, StringComparison.Ordinal);
        Assert.Contains("explicitOptInEnvironmentVariable = \"DESKBOX_SHORTCUT_BACKEND\"", script, StringComparison.Ordinal);
        Assert.Contains("fallbackOnNativeFailure = $false", script, StringComparison.Ordinal);
        Assert.Contains("nativeAotCompileTimeDefine = \"DESKBOX_NATIVE_AOT\"", script, StringComparison.Ordinal);
        Assert.Contains("allowedWarningCodes", script, StringComparison.Ordinal);
        Assert.Contains("shortcutAlwaysThrowMessages", script, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderPickerService+FileOpenDialog", script, StringComparison.Ordinal);
        Assert.Contains("musicVolumeAlwaysThrowMessages", script, StringComparison.Ordinal);
        Assert.DoesNotContain("MusicVolumeService+MMDeviceEnumeratorComObject", script, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("src/DeskBox/ViewModels/SearchPopupViewModel.cs", 15)]
    [InlineData("src/DeskBox/ViewModels/SettingsViewModel.cs", 75)]
    public void AotSensitiveViewModels_UseObservablePartialProperties(
        string relativePath,
        int expectedCount)
    {
        string source = File.ReadAllText(TestPaths.FromRepository(relativePath));

        Assert.False(
            Regex.IsMatch(source, @"\[ObservableProperty\]\s+private\s+"),
            $"{relativePath} still contains field-based ObservableProperty declarations.");
        Assert.Equal(
            expectedCount,
            Regex.Matches(source, @"\[ObservableProperty\]\s+public\s+partial\s+").Count);
    }

    [Fact]
    public void WinRtAbiTypes_ArePartial()
    {
        var expectedDeclarations = new Dictionary<string, string[]>
        {
            ["src/DeskBox/Controls/MarkdownDocumentView.cs"] =
                ["public sealed partial class MarkdownDocumentView"],
            ["src/DeskBox/Controls/WidgetItemTemplateSelector.cs"] =
                ["public sealed partial class WidgetItemTemplateSelector"],
            ["src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml.cs"] =
            [
                "public sealed partial class GlanceBoolToVisibilityConverter",
                "public sealed partial class GlanceInverseBoolToVisibilityConverter",
                "public sealed partial class GlanceBoolToFontWeightConverter"
            ],
            ["src/DeskBox/Controls/WidgetContents/WeatherWidgetContent.xaml.cs"] =
            [
                "internal sealed partial class TempBarMarginConverter",
                "internal sealed partial class BoolToVisibilityConverter"
            ]
        };

        foreach ((string relativePath, string[] declarations) in expectedDeclarations)
        {
            string source = File.ReadAllText(TestPaths.FromRepository(relativePath));
            foreach (string declaration in declarations)
            {
                Assert.Contains(declaration, source, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void SettingsViewModel_ConstructorSuppressesObservablePropertyCallbacks()
    {
        string source = File.ReadAllText(
            TestPaths.FromRepository("src/DeskBox/ViewModels/SettingsViewModel.cs"));
        int constructorStart = source.IndexOf("public SettingsViewModel(", StringComparison.Ordinal);
        Assert.True(constructorStart >= 0);

        int suppressionStart = source.IndexOf("_isRestoringDefaults = true;", constructorStart, StringComparison.Ordinal);
        Assert.True(suppressionStart >= 0);

        int firstMigratedAssignment = source.IndexOf("AutoStart = StartupService.IsEnabled();", constructorStart, StringComparison.Ordinal);
        int lastMigratedAssignment = source.IndexOf(
            "WeatherShowPressure = settings.WeatherShowPressure;",
            constructorStart,
            StringComparison.Ordinal);
        int suppressionEnd = source.IndexOf("_isRestoringDefaults = false;", suppressionStart, StringComparison.Ordinal);
        int nextMember = source.IndexOf("[RelayCommand]", constructorStart, StringComparison.Ordinal);

        Assert.True(firstMigratedAssignment >= 0);
        Assert.True(lastMigratedAssignment >= 0);
        Assert.True(nextMember >= 0);
        Assert.InRange(suppressionStart, constructorStart, firstMigratedAssignment - 1);
        Assert.InRange(suppressionEnd, lastMigratedAssignment + 1, nextMember - 1);
    }

    private static Task<ProcessResult> RunAotConfigurationValidationAsync(params string[] properties)
    {
        var arguments = new List<string>
        {
            "msbuild",
            TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"),
            "-nologo",
            "-t:ValidateDeskBoxNativeAotConfiguration",
            "-p:Configuration=Release"
        };
        arguments.AddRange(properties.Select(property => $"-p:{property}"));

        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        return RunProcessAsync(dotnet, arguments.ToArray());
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start(), $"Failed to start '{fileName}'.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        string output = string.Concat(
            await standardOutput,
            Environment.NewLine,
            await standardError);
        return new ProcessResult(process.ExitCode, output);
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
