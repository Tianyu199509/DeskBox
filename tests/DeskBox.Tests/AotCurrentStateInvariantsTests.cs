using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DeskBox.Tests;

/// <summary>
/// Consolidated current-state Native AOT, XAML binding, Rust ABI/CRT, and distribution
/// invariants. Replaces the 48 historical per-stage AotStage*ContractTests receipt files.
/// </summary>
public sealed class AotCurrentStateInvariantsTests
{
    private static readonly HashSet<string> SupportedLocalizedTags = new(StringComparer.Ordinal)
    {
        "toolkit:SettingsCard|HeaderKey",
        "toolkit:SettingsCard|DescriptionKey",
        "toolkit:SettingsExpander|HeaderKey",
        "toolkit:SettingsExpander|DescriptionKey",
        "TextBox|HeaderKey",
        "InfoBar|HeaderKey",
        "InfoBar|DescriptionKey",
    };

    [Fact]
    public void BindableSettingsViewModelInventory_CoversEveryDirectSettingsBinding()
    {
        string registrationSource = Read("src/DeskBox/ViewModels/SettingsViewModel.AotBindableProperties.cs");
        string[] xamlPaths =
        [
            "src/DeskBox/Views/SettingsWindow.xaml",
            .. Directory.GetFiles(
                    TestPaths.FromRepository("src/DeskBox/Views/SettingsSections"),
                    "*.xaml",
                    SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(TestPaths.FromRepository("."), path).Replace('\\', '/'))
        ];

        HashSet<string> publicViewModelProperties = typeof(DeskBox.ViewModels.SettingsViewModel)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        string[] xamlDirectProperties = xamlPaths
            .SelectMany(path => Regex.Matches(
                    Read(path),
                    @"\{Binding\s+(?:Path=)?(?<path>[A-Za-z_][A-Za-z0-9_]*)\b")
                .Select(match => match.Groups["path"].Value))
            .Where(publicViewModelProperties.Contains)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        string[] generatedBindableProperties = Regex.Matches(
                registrationSource,
                @"nameof\((?<name>[A-Za-z_][A-Za-z0-9_]*)\)")
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(xamlDirectProperties, generatedBindableProperties);
        Assert.DoesNotContain("Command", registrationSource, StringComparison.Ordinal);
    }

    [Fact]
    public void AllAotBindablePropertyBridges_MapToPublicInstancePropertiesOnTargetTypes()
    {
        string[] bridgeFiles = Directory.GetFiles(
            TestPaths.FromRepository("src/DeskBox"),
            "*.AotBindableProperties.cs",
            SearchOption.AllDirectories);
        Assert.NotEmpty(bridgeFiles);

        var deskBoxAssembly = typeof(DeskBox.ViewModels.SettingsViewModel).Assembly;
        var blockPattern = new Regex(
            @"\[WinRT\.GeneratedBindableCustomProperty\(\[(?<body>[\s\S]*?)\]\s*,[\s\S]*?\)\]\s*(?:public|internal)\s+(?:sealed\s+)?partial\s+class\s+(?<class>\w+)",
            RegexOptions.CultureInvariant);

        foreach (string file in bridgeFiles)
        {
            string source = File.ReadAllText(file);
            Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
            Match nsMatch = Regex.Match(source, @"namespace\s+(?<ns>[A-Za-z0-9_.]+)");
            Assert.True(nsMatch.Success, $"Missing namespace in {Path.GetFileName(file)}");
            string ns = nsMatch.Groups["ns"].Value;

            foreach (Match block in blockPattern.Matches(source))
            {
                string className = block.Groups["class"].Value;
                Type? targetType = deskBoxAssembly.GetType($"{ns}.{className}");
                Assert.NotNull(targetType);

                HashSet<string> publicProperties = targetType!
                    .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
                    .Select(p => p.Name)
                    .ToHashSet(StringComparer.Ordinal);

                string[] registeredNames = Regex.Matches(block.Groups["body"].Value, @"nameof\((?:[A-Za-z_][A-Za-z0-9_]*\.)?(?<prop>[A-Za-z_][A-Za-z0-9_]*)\)")
                    .Select(m => m.Groups["prop"].Value)
                    .ToArray();
                Assert.NotEmpty(registeredNames);
                foreach (string prop in registeredNames)
                {
                    Assert.Contains(prop, publicProperties);
                }
            }
        }
    }

    [Fact]
    public void AllXamlRuntimeBindings_TargetAotGeneratedBindableProperties()
    {
        string srcRoot = TestPaths.FromRepository("src/DeskBox");
        string separator = Path.DirectorySeparatorChar.ToString();
        var deskBoxAssembly = typeof(DeskBox.ViewModels.SettingsViewModel).Assembly;
        Type[] allAssemblyTypes = deskBoxAssembly.GetTypes();

        var aotBindableProperties = new HashSet<string>(StringComparer.Ordinal);
        var parameterizedPattern = new Regex(
            @"GeneratedBindableCustomProperty\(\[(?<body>[\s\S]*?)\]",
            RegexOptions.CultureInvariant);
        var parameterlessPattern = new Regex(
            @"\[(?:WinRT\.)?GeneratedBindableCustomProperty\s*\]\s*(?:\[[^\]]+\]\s*)*(?:public|internal|private)\s+(?:sealed\s+)?(?:partial\s+)?(?:class|record)\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.CultureInvariant);

        foreach (string csPath in Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories))
        {
            if (csPath.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase) ||
                csPath.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string csSource = File.ReadAllText(csPath);
            foreach (Match block in parameterizedPattern.Matches(csSource))
            {
                foreach (Match propMatch in Regex.Matches(
                             block.Groups["body"].Value,
                             @"nameof\((?:[A-Za-z_][A-Za-z0-9_]*\.)?(?<prop>[A-Za-z_][A-Za-z0-9_]*)\)"))
                {
                    aotBindableProperties.Add(propMatch.Groups["prop"].Value);
                }
            }

            foreach (Match fullTypeMatch in parameterlessPattern.Matches(csSource))
            {
                string shortTypeName = fullTypeMatch.Groups["type"].Value;
                Type[] matchedTypes = allAssemblyTypes
                    .Where(t => string.Equals(t.Name, shortTypeName, StringComparison.Ordinal))
                    .ToArray();
                Assert.NotEmpty(matchedTypes);
                foreach (Type matchedType in matchedTypes)
                {
                    foreach (var prop in matchedType.GetProperties(
                                 System.Reflection.BindingFlags.Public |
                                 System.Reflection.BindingFlags.Instance))
                    {
                        aotBindableProperties.Add(prop.Name);
                    }
                }
            }
        }

        var bindingRegex = new Regex(
            @"\{Binding\s+(?<body>[^}]+)\}",
            RegexOptions.CultureInvariant);
        var leadingPathRegex = new Regex(
            @"^(?:Path=)?(?<prop>[A-Za-z_][A-Za-z0-9_]*)",
            RegexOptions.CultureInvariant);
        HashSet<string> bindingKeywords = new(StringComparer.Ordinal)
        {
            "Mode",
            "Converter",
            "ConverterParameter",
            "FallbackValue",
            "TargetNullValue",
            "UpdateSourceTrigger",
        };

        var missingBindings = new List<string>();
        foreach (string xamlPath in Directory.EnumerateFiles(srcRoot, "*.xaml", SearchOption.AllDirectories))
        {
            if (xamlPath.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase) ||
                xamlPath.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string xaml = File.ReadAllText(xamlPath);
            string relPath = Path.GetRelativePath(srcRoot, xamlPath).Replace('\\', '/');
            foreach (Match match in bindingRegex.Matches(xaml))
            {
                string body = match.Groups["body"].Value.Trim();
                if (body.Contains("ElementName=", StringComparison.Ordinal) ||
                    body.Contains("RelativeSource=", StringComparison.Ordinal))
                {
                    continue;
                }

                Match pathMatch = leadingPathRegex.Match(body);
                if (!pathMatch.Success)
                {
                    continue;
                }

                string propName = pathMatch.Groups["prop"].Value;
                if (bindingKeywords.Contains(propName))
                {
                    continue;
                }

                if (!aotBindableProperties.Contains(propName))
                {
                    missingBindings.Add($"{relPath}: {{Binding {body}}}");
                }
            }
        }

        Assert.True(
            missingBindings.Count == 0,
            $"Every runtime XAML {{Binding}} must target an AOT-generated bindable property. Missing: {string.Join("; ", missingBindings)}");
    }

    [Fact]
    public void LocalizedAttachedProperties_OnlyTargetSupportedTypedControlsWithoutReflection()
    {
        string source = Read("src/DeskBox/Services/Localized.cs");

        Assert.Contains("using CommunityToolkit.WinUI.Controls;", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SetObjectProperty", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetType().GetProperty", source, StringComparison.Ordinal);

        string quickCaptureSource = Read("src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs");
        Assert.DoesNotContain(".GetProperties()", quickCaptureSource, StringComparison.Ordinal);
        Assert.DoesNotContain("property.GetValue(ex)", quickCaptureSource, StringComparison.Ordinal);

        IReadOnlyDictionary<string, int> usages = ReadLocalizedXamlUsages();
        Assert.NotEmpty(usages);
        foreach (string key in usages.Keys)
        {
            Assert.Contains(key, SupportedLocalizedTags);
        }
    }

    [Fact]
    public void AuditAndDistributionScripts_AgreeOnCurrentProfileSchemaAndWmc1510Ratchet()
    {
        string audit = Read("scripts/publish-aot-audit.ps1");
        string launcher = Read("scripts/start-aot-preview.ps1");

        Match auditProfile = Regex.Match(audit, @"\$auditProfileVersion\s*=\s*(?<v>\d+)");
        Match auditSchema = Regex.Match(audit, @"schemaVersion\s*=\s*(?<v>\d+)");
        Match launcherProfile = Regex.Match(launcher, @"\$RequiredAuditProfileVersion\s*=\s*(?<v>\d+)");
        Match launcherSchema = Regex.Match(launcher, @"\$RequiredSummarySchemaVersion\s*=\s*(?<v>\d+)");
        Match wmcRatchet = Regex.Match(audit, @"\$maximumWmc1510Count\s*=\s*(?<v>\d+)");

        Assert.True(auditProfile.Success && launcherProfile.Success);
        Assert.True(auditSchema.Success && launcherSchema.Success);
        Assert.Equal(auditProfile.Groups["v"].Value, launcherProfile.Groups["v"].Value);
        Assert.Equal(auditSchema.Groups["v"].Value, launcherSchema.Groups["v"].Value);

        Assert.True(wmcRatchet.Success, "publish-aot-audit.ps1 must define a single $maximumWmc1510Count ratchet.");
        int maxWmc1510 = int.Parse(wmcRatchet.Groups["v"].Value);
        Assert.InRange(maxWmc1510, 0, 802);
    }

    [Fact]
    public void RustAbiSurface_RemainsAtVersion2Capability511AndTenExports()
    {
        string rust = Read("native/deskbox-native/src/lib.rs");
        Assert.Contains("pub const DESKBOX_NATIVE_ABI_VERSION: u32 = 2", rust, StringComparison.Ordinal);
        Assert.Contains("assert_eq!(deskbox_native_capabilities(), 511);", rust, StringComparison.Ordinal);
        Assert.Equal(10, Regex.Matches(rust, Regex.Escape("#[unsafe(no_mangle)]")).Count);
    }

    [Fact]
    public void RustBuildScripts_MapBothWindowsMsvcTargetsAndUseStaticArm64PeContracts()
    {
        string native = Read("scripts/build-rust-native.ps1");
        string pe = Read("scripts/native-pe-contract.ps1");
        string environment = Read("scripts/rust-arm64-msvc-environment.ps1");
        string toolchain = Read("rust-toolchain.toml");

        Assert.Contains("aarch64-pc-windows-msvc", toolchain, StringComparison.Ordinal);
        Assert.Contains("[ValidateSet(\"x64\", \"ARM64\")]", native, StringComparison.Ordinal);
        Assert.Contains("aarch64-pc-windows-msvc", native, StringComparison.Ordinal);
        Assert.Contains("x86_64-pc-windows-msvc", native, StringComparison.Ordinal);
        Assert.Contains("native-pe-contract.ps1", native, StringComparison.Ordinal);
        Assert.Contains("rust-arm64-msvc-environment.ps1", native, StringComparison.Ordinal);
        Assert.Contains("RuntimeProbeExecuted", native, StringComparison.Ordinal);
        Assert.Contains("static-pe-plus-frozen-source-constants", native, StringComparison.Ordinal);
        Assert.Contains("rust-std 1.96.0", native, StringComparison.Ordinal);

        foreach (string token in new[]
                 {
                     "Get-DeskBoxNativePeContract",
                     "0xAA64",
                     "0x8664",
                     "PE32+",
                     "RequiredExports",
                     "missing required exports"
                 })
        {
            Assert.Contains(token, pe, StringComparison.Ordinal);
        }

        foreach (string token in new[]
                 {
                     "Microsoft.VisualStudio.Component.VC.Tools.ARM64",
                     "Microsoft.VisualStudio.Component.VC.Tools.x86.x64",
                     "CARGO_TARGET_AARCH64_PC_WINDOWS_MSVC_LINKER",
                     "CARGO_TARGET_X86_64_PC_WINDOWS_MSVC_LINKER",
                     "WindowsSdkUcrtLibraryDirectory",
                     "WindowsSdkUmLibraryDirectory",
                     "Get-DeskBoxMsvcEnvironment",
                     "Enter-DeskBoxMsvcEnvironment",
                     "Exit-DeskBoxMsvcEnvironment",
                     "Enter-DeskBoxArm64MsvcEnvironment",
                     "Exit-DeskBoxArm64MsvcEnvironment"
                 })
        {
            Assert.Contains(token, environment, StringComparison.Ordinal);
        }

        Assert.Contains("if (@($linkerCandidates).Count -eq 0)", environment, StringComparison.Ordinal);
        Assert.DoesNotContain("if ($linkerCandidates.Count -eq 0)", environment, StringComparison.Ordinal);
    }

    [Fact]
    public void X64Audit_UsesOneCompleteExplicitMsvcEnvironmentForNativeAot()
    {
        string script = Read("scripts/publish-aot-audit.ps1");

        foreach (string token in new[]
                 {
                     "Get-DeskBoxMsvcEnvironment -Platform x64",
                     "Enter-DeskBoxMsvcEnvironment",
                     "Exit-DeskBoxMsvcEnvironment",
                     "IlcUseEnvironmentalTools=true",
                     "LinkerDirectory \"dumpbin.exe\""
                 })
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task NativeAotValidation_AcceptsMatchingArm64AndRejectsCrossedPairs()
    {
        ProcessResult valid = await RunValidationAsync("ARM64", "win-arm64");
        Assert.Equal(0, valid.ExitCode);

        ProcessResult wrongRid = await RunValidationAsync("ARM64", "win-x64");
        Assert.NotEqual(0, wrongRid.ExitCode);
        Assert.Contains("matching Platform/RuntimeIdentifier pair", wrongRid.Output, StringComparison.Ordinal);

        ProcessResult wrongPlatform = await RunValidationAsync("x64", "win-arm64");
        Assert.NotEqual(0, wrongPlatform.ExitCode);
        Assert.Contains("matching Platform/RuntimeIdentifier pair", wrongPlatform.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Arm64Audit_IsExplicitlyStaticAndSeparatesBuildEvidenceFromDeviceEvidence()
    {
        string script = Read("scripts/publish-arm64-aot-static-audit.ps1");

        foreach (string token in new[]
                 {
                     "cross-compiled-static-only",
                     "native-arm64-runtime-plus-static",
                     "targetDeviceExecuted = $false",
                     "physicalUserDeviceExecuted = $false",
                     "runtimeAbiProbeExecuted = $runtimeAbiProbeExecuted",
                     "Enter-DeskBoxArm64MsvcEnvironment",
                     "Exit-DeskBoxArm64MsvcEnvironment",
                     "aarch64-pc-windows-msvc",
                     "0xAA64",
                     "IlcUseEnvironmentalTools=true",
                     "sourceStableDuringAudit",
                     "publishMatchesStaging = $true"
                 })
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }

        Assert.Contains("rust-arm64-msvc-environment.ps1", script, StringComparison.Ordinal);
        Assert.Contains("deskbox_native.dll", script, StringComparison.Ordinal);
        Assert.DoesNotContain("deskbox_search_core.dll", script, StringComparison.Ordinal);
        Assert.Contains("DeskBox.Updater.exe", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductLoaders_AcceptX64AndArm64ButRejectOtherProcessArchitectures()
    {
        string shortcut = Read("src/DeskBox/Helpers/ShortcutNativeBackend.cs");

        Assert.Contains(
            "Architecture.X64 or Architecture.Arm64",
            shortcut,
            StringComparison.Ordinal);
        Assert.DoesNotContain("supports only x64", shortcut, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildScripts_LoadTheDllWheneverHostAndTargetArchitecturesMatch()
    {
        string script = Read("scripts/build-rust-native.ps1");
        Assert.Contains("expectedProcessArchitecture", script, StringComparison.Ordinal);
        Assert.Contains("host-and-target-architectures-match", script, StringComparison.Ordinal);
        Assert.Contains("cross-architecture-static-validation-only", script, StringComparison.Ordinal);
        Assert.Contains("runtime-load-plus-static-pe", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Arm64MsvcEnvironment_PrefersNativeHostToolsAndRetainsX64Fallback()
    {
        string script = Read("scripts/rust-arm64-msvc-environment.ps1");

        foreach (string token in new[]
                 {
                     "ProcessArchitecture",
                     "Hostarm64",
                     "Hostx64",
                     "WindowsSdkHostArchitecture",
                     "VSCMD_ARG_HOST_ARCH"
                 })
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TestProject_BuildsRustModulesForTheRequestedArchitectureAndConfiguration()
    {
        string project = Read("tests/DeskBox.Tests/DeskBox.Tests.csproj");

        Assert.Contains("<DeskBoxNativeTestPlatform", project, StringComparison.Ordinal);
        Assert.Contains("-Platform $(DeskBoxNativeTestPlatform)", project, StringComparison.Ordinal);
        Assert.Contains("-Configuration $(Configuration)", project, StringComparison.Ordinal);
        Assert.Contains("rust-arm64-msvc-environment.ps1", project, StringComparison.Ordinal);
        Assert.DoesNotContain("-Platform x64 -Configuration Debug", project, StringComparison.Ordinal);
    }

    [Fact]
    public void GitHubWorkflow_UsesNativeArmRunnerAndUploadsTypedRuntimeEvidence()
    {
        string workflow = Read(".github/workflows/arm64-runtime.yml");
        string runner = Read("scripts/run-arm64-stage-7b-runtime.ps1");
        string gate = Read("tests/DeskBox.Tests/Arm64NativeRuntimeGateTests.cs");

        foreach (string token in new[]
                 {
                     "windows-11-vs2026-arm",
                     "workflow_dispatch",
                     "run-arm64-stage-7b-runtime.ps1",
                     "actions/upload-artifact@v4"
                 })
        {
            Assert.Contains(token, workflow, StringComparison.Ordinal);
        }

        foreach (string token in new[]
                 {
                     "deskbox.arm64-stage7b-runtime-evidence.v1",
                     "github-hosted-arm64-runtime",
                     "targetArchitectureRuntimeExecuted",
                     "physicalUserDeviceExecuted",
                     "interactiveDesktopExecuted",
                     "DESKBOX_REQUIRE_ARM64_RUNTIME_GATE"
                 })
        {
            Assert.Contains(token, runner, StringComparison.Ordinal);
        }

        Assert.Contains("Architecture.Arm64", gate, StringComparison.Ordinal);
        Assert.Contains("ProbeAbiVersion", gate, StringComparison.Ordinal);
        Assert.DoesNotContain("SearchCore", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void RustBuilds_UseExplicitRestoredCrtFlagsAndReportPeDependencies()
    {
        string script = Read("scripts/build-rust-native.ps1");
        foreach (string token in new[]
                 {
                     "[ValidateSet(\"Dynamic\", \"Static\")]",
                     "[string]$CrtLinkage = \"Static\"",
                     "target-feature=+crt-static",
                     "target-feature=-crt-static",
                     "CARGO_ENCODED_RUSTFLAGS",
                     "previousEncodedRustFlags",
                     "previousRustFlags",
                     "VcRuntimeImports",
                     "SizeOfImage"
                 })
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }

        string pe = Read("scripts/native-pe-contract.ps1");
        Assert.Contains("importRva", pe, StringComparison.Ordinal);
        Assert.Contains("import descriptor", pe, StringComparison.Ordinal);
        Assert.Contains("ImportedModules", pe, StringComparison.Ordinal);
        Assert.Contains("SizeOfImage", pe, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedBuilds_IsolateAndForwardTheSelectedCrtLinkage()
    {
        foreach (string relativePath in new[]
                 {
                     "src/DeskBox/DeskBox.csproj",
                     "tests/DeskBox.Tests/DeskBox.Tests.csproj"
                 })
        {
            string project = Read(relativePath);
            Assert.Contains("DeskBoxRustCrtLinkage", project, StringComparison.Ordinal);
            Assert.Contains(">Static</DeskBoxRustCrtLinkage>", project, StringComparison.Ordinal);
            Assert.Contains("-CrtLinkage $(DeskBoxRustCrtLinkage)", project, StringComparison.Ordinal);
            Assert.Contains("$(DeskBoxRustCrtLinkage)", project, StringComparison.Ordinal);
            Assert.Contains("must be Dynamic or Static", project, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Audit_SeparatesArchitectureRecommendationFromProductionDecision()
    {
        string audit = Read("scripts/audit-rust-crt-distribution.ps1");

        foreach (string token in new[]
                 {
                     "deskbox.rust-crt-stage7c0-evidence.v1",
                     "isolated-host-process-load-and-abi-delta",
                     "runtime-ab-plus-static-pe",
                     "cross-compiled-static-pe-only",
                     "staticMinusDynamicFileBytes",
                     "staticMinusDynamicImageBytes",
                     "boundedBelowOneMiB",
                     "recommendationForAuditedPlatforms",
                     "productionDecision = $productionDecision",
                     "combine native x64 and ARM64 runtime evidence",
                     "DeskBoxRustCrtLinkage=Static"
                 })
        {
            Assert.Contains(token, audit, StringComparison.Ordinal);
        }

        Assert.Contains("$productionDecision = \"Pending\"", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void Arm64Workflow_RunsRuntimeAndCrtGatesAndUploadsSeparateEvidence()
    {
        string workflow = Read(".github/workflows/arm64-runtime.yml");

        Assert.Contains("Run Stage 7B native ARM64 gate", workflow, StringComparison.Ordinal);
        Assert.Contains("Run Stage 7C0 ARM64 CRT A/B gate", workflow, StringComparison.Ordinal);
        Assert.Contains("audit-rust-crt-distribution.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("-Platforms ARM64", workflow, StringComparison.Ordinal);
        Assert.Contains("arm64-stage7c0-crt-evidence", workflow, StringComparison.Ordinal);

        string runtimeGate = Read("scripts/run-arm64-stage-7b-runtime.ps1");
        Assert.Contains("-CrtLinkage Static", runtimeGate, StringComparison.Ordinal);
        Assert.Contains("-p:DeskBoxRustCrtLinkage=Static", runtimeGate, StringComparison.Ordinal);
        Assert.Contains("$nativeResult.CrtLinkage -ne \"Static\"", runtimeGate, StringComparison.Ordinal);
        Assert.Contains("$nativeResult.VcRuntimeImports.Count -ne 0", runtimeGate, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreNativeAot_PackagesRustModuleAndRemovesManagedRuntimeMetadata()
    {
        string project = Read("src/DeskBox/DeskBox.csproj");

        foreach (string token in new[]
                 {
                     "PrepareDeskBoxStoreNativeAotPayload",
                     "BeforeTargets=\"_ComputeAppxPackagePayload\"",
                     "DependsOnTargets=\"BuildDeskBoxRustNative\"",
                     "<TargetPath>deskbox_native.dll</TargetPath>",
                     "<TargetPath>deskbox_native.pdb</TargetPath>",
                     "DeskBox.deps.json",
                     "DeskBox.runtimeconfig.json"
                 })
        {
            Assert.Contains(token, project, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void StoreBuild_ExcludesDonationQrAssets()
    {
        string project = Read("src/DeskBox/DeskBox.csproj");
        string storeAudit = Read("scripts/audit-store-native-aot-package.ps1");
        string about = Read("src/DeskBox/ViewModels/SettingsViewModel.AboutAndUpdates.cs");

        Assert.Contains("<Content Remove=\"Assets\\wechat-qrcode.jpg\" />", project, StringComparison.Ordinal);
        Assert.Contains("<Content Remove=\"Assets\\Support\\*.png\" />", project, StringComparison.Ordinal);
        Assert.Contains("<Content Include=\"Assets\\wechat-qrcode.jpg\" Condition=\"'$(DeskBoxDistribution)' != 'Store'\">", project, StringComparison.Ordinal);
        Assert.Contains("<Content Include=\"Assets\\Support\\*.png\" Condition=\"'$(DeskBoxDistribution)' != 'Store'\">", project, StringComparison.Ordinal);
        Assert.Contains("<CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>", project, StringComparison.Ordinal);
        Assert.Contains("'wechat-qrcode'", storeAudit, StringComparison.Ordinal);
        Assert.Contains("'Assets/Support/'", storeAudit, StringComparison.Ordinal);
        Assert.Contains("DonationQrCodeVisibility", about, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseGates_ForbidExternalEverythingSdkDllAndEnforceUniversalPeVcCrtAudit()
    {
        string retail = Read("scripts/publish-aot-retail.ps1");
        string stage7c1 = Read("scripts/build-stage-7c1-distribution.ps1");
        string storeAudit = Read("scripts/audit-store-native-aot-package.ps1");

        Assert.DoesNotContain("EverythingSdk.dll", retail, StringComparison.Ordinal);
        Assert.DoesNotContain("EverythingSdk.dll", stage7c1, StringComparison.Ordinal);
        Assert.Contains("EverythingSdk\\.dll", retail, StringComparison.Ordinal);
        Assert.Contains("H\\.NotifyIcon", retail, StringComparison.Ordinal);
        Assert.Contains("Strings\\zh-CN.json", retail, StringComparison.Ordinal);
        Assert.Contains("Strings\\en-US.json", retail, StringComparison.Ordinal);
        Assert.Contains("'(^|/)EverythingSdk\\.dll$'", stage7c1, StringComparison.Ordinal);
        Assert.Contains("'(^|/)H\\.NotifyIcon(?:\\.WinUI)?\\.dll$'", stage7c1, StringComparison.Ordinal);
        Assert.Contains("\"Strings/zh-CN.json\"", stage7c1, StringComparison.Ordinal);
        Assert.Contains("run-aot-agent-clipboard-companion-smoke.ps1", stage7c1, StringComparison.Ordinal);
        Assert.Contains("'(^|/)H\\.NotifyIcon(?:\\.WinUI)?\\.dll$'", storeAudit, StringComparison.Ordinal);
        Assert.Contains("\"Strings/zh-CN.json\"", storeAudit, StringComparison.Ordinal);
        Assert.Contains("'(^|/)EverythingSdk\\.dll$'", storeAudit, StringComparison.Ordinal);
        Assert.Contains("$packagedPeFiles = @(", storeAudit, StringComparison.Ordinal);
        Assert.Contains("imports non-inbox VC++ runtime DLLs", storeAudit, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreBuild_ExposesNativeAotAndStoreUploadModesWithStableOutputRoot()
    {
        string script = Read("scripts/build-store-msix.ps1");

        foreach (string token in new[]
                 {
                     "[switch]$NativeAot",
                     "StoreUpload",
                     "DeskBoxAotAudit=true",
                     "PublishAot=true",
                     "DeskBoxRustNative=true",
                     "DeskBoxRustCrtLinkage=Static",
                     "UapAppxPackageBuildMode=$PackageBuildMode",
                     "$appxPackageDir",
                     "Get-DeskBoxMsvcEnvironment -Platform $Platform",
                     "Enter-DeskBoxMsvcEnvironment -Toolchain $msvcEnvironment",
                     "Exit-DeskBoxMsvcEnvironment -State $environmentState"
                 })
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("-p:VCToolsInstallDir=", script, StringComparison.Ordinal);
    }

    [Fact]
    public void StoreAudit_RequiresNativePayloadIdentitySymbolsAndStrictAbsenceList()
    {
        string script = Read("scripts/audit-store-native-aot-package.ps1");

        foreach (string token in new[]
                 {
                     "D1FC332A.DeskBoxWidgets",
                     "Microsoft.WindowsAppRuntime.2",
                     "deskbox_native.dll",
                     "DeskBox\\.deps\\.json",
                     "DeskBox\\.runtimeconfig\\.json",
                     "deskbox_search_core",
                     "DeskBox\\.Updater",
                     "DeskBox\\.Settings",
                     "HasClrHeader",
                     "publishPayloadHashesMatch",
                     "DeskBox.pdb",
                     "deskbox_native.pdb",
                     "signingAndWackExecuted = $false"
                 })
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }

        Assert.Contains("Fingerprint", script, StringComparison.Ordinal);

        Match minimumVersionMatch = Regex.Match(
            script,
            @"frameworkDependency\.MinVersion\s+-lt\s+\[version\]""(?<version>\d+\.\d+\.\d+\.\d+)""",
            RegexOptions.CultureInvariant);

        Assert.True(minimumVersionMatch.Success, "Store audit must enforce a Windows App Runtime minimum version.");
        Assert.Equal(new Version(2, 7, 0, 0), Version.Parse(minimumVersionMatch.Groups["version"].Value));
    }

    [Fact]
    public void InstallerSources_SupportNativeAotFallbackAndBundledRuntimeModes()
    {
        foreach (string relativePath in new[]
                 {
                     "installer/DeskBox.iss",
                     "installer/DeskBox.arm64.iss"
                 })
        {
            string installer = Read(relativePath);
            Assert.Contains("#define DeskBoxBundledRuntime 0", installer, StringComparison.Ordinal);
            Assert.Contains("#define DeskBoxNativeAot 0", installer, StringComparison.Ordinal);
            Assert.Contains("#if DeskBoxBundledRuntime", installer, StringComparison.Ordinal);
            Assert.Contains("#elif DeskBoxNativeAot", installer, StringComparison.Ordinal);
            Assert.Contains("deskbox_native.dll", installer, StringComparison.Ordinal);
            Assert.Contains("DeskBox.InstallManifest.txt", installer, StringComparison.Ordinal);
            Assert.Contains("CleanupDeskBoxInstall", installer, StringComparison.Ordinal);
            Assert.Contains("DeskBox.LegacyBundledRuntimeFiles.txt", installer, StringComparison.Ordinal);
            Assert.Contains("Flags: dontcopy", installer, StringComparison.Ordinal);
        }

        string migration = Read("installer/DeskBox.Migration.iss");
        Assert.Contains("if not DirectInstallUpgrade", migration, StringComparison.Ordinal);

        foreach (string relativePath in new[]
                 {
                     "installer/DeskBox.Dependencies.iss",
                     "installer/DeskBox.Dependencies.arm64.iss"
                 })
        {
            string dependencies = Read(relativePath);
            Assert.Contains("#if DeskBoxNativeAot", dependencies, StringComparison.Ordinal);
            Assert.Contains("ShouldInstallDotNetRuntime := False", dependencies, StringComparison.Ordinal);
            Assert.Contains(
                "ShouldInstallWindowsAppRuntime := not IsWindowsAppRuntime27Installed",
                dependencies,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(
        "installer/DeskBox.Dependencies.iss",
        "X64",
        "https://aka.ms/windowsappsdk/2.7/2.7.0/windowsappruntimeinstall-x64.exe",
        "https://download.microsoft.com/download/b79f2091-5e42-4955-91b2-44d1b3f2f845/WindowsAppRuntimeInstall-x64.exe")]
    [InlineData(
        "installer/DeskBox.Dependencies.arm64.iss",
        "ARM64",
        "https://aka.ms/windowsappsdk/2.7/2.7.0/windowsappruntimeinstall-arm64.exe",
        "https://download.microsoft.com/download/3fed4c2e-aaba-42a4-ba38-90a62e99b9a2/WindowsAppRuntimeInstall-arm64.exe")]
    public void DirectInstaller_RequiresWindowsAppRuntime27ForMatchingArchitecture(
        string relativePath,
        string architecture,
        string primaryUrl,
        string fallbackUrl)
    {
        string dependencies = Read(relativePath);

        Assert.Contains("function IsWindowsAppRuntime27Installed: Boolean;", dependencies, StringComparison.Ordinal);
        Assert.DoesNotContain("IsWindowsAppRuntime26Installed", dependencies, StringComparison.Ordinal);
        Assert.DoesNotContain("IsWindowsAppRuntime25Installed", dependencies, StringComparison.Ordinal);
        Assert.Contains($"WindowsAppRuntimeUrl = '{primaryUrl}';", dependencies, StringComparison.Ordinal);
        Assert.Contains($"WindowsAppRuntimeFallbackUrl = '{fallbackUrl}';", dependencies, StringComparison.Ordinal);

        MatchCollection minimumVersionMatches = Regex.Matches(
            dependencies,
            @"\.Version\s+-ge\s+\[version\]''(?<version>\d+\.\d+\.\d+\.\d+)''",
            RegexOptions.CultureInvariant);

        Assert.Equal(2, minimumVersionMatches.Count);
        foreach (Match match in minimumVersionMatches)
        {
            Version minimumVersion = Version.Parse(match.Groups["version"].Value);
            Assert.Equal(new Version(2, 7, 0, 0), minimumVersion);
        }

        Assert.Equal(
            2,
            Regex.Matches(
                dependencies,
                $@"\.Architecture\s+-eq\s+''{architecture}''",
                RegexOptions.CultureInvariant).Count);
    }

    [Fact]
    public void DistributionWorkflow_UsesNativeX64AndArm64RunnersAndPreservesEvidenceBoundaries()
    {
        string workflow = Read(".github/workflows/distribution-audit.yml");
        string orchestrator = Read("scripts/build-stage-7c1-distribution.ps1");

        foreach (string token in new[]
                 {
                     "windows-2025-vs2026",
                     "windows-11-vs2026-arm",
                     "build-stage-7c1-distribution.ps1",
                     "stage7c1-${{ matrix.rid }}-distribution",
                     "Cross-architecture evidence manifest",
                     "physicalUserDeviceExecuted",
                     "signingExecuted",
                     "wackExecuted",
                     "inPlaceUpgradeExecuted"
                 })
        {
            Assert.Contains(token, workflow, StringComparison.Ordinal);
        }

        foreach (string token in new[]
                 {
                     "publish-aot-audit.ps1",
                     "publish-arm64-aot-static-audit.ps1",
                     "DeskBoxNativeAot=1",
                     "DeskBoxBundledRuntime=1",
                     @"Programs\Inno Setup 6\ISCC.exe",
                     "/DMyAppReleaseDir=$directPublishDirectory",
                     "/F$installerOutputBaseName",
                     "DeskBox.InstallManifest.txt",
                     "windowsAppRuntimeDependencySkipped = $true",
                     "$storeBuildArguments = @{",
                     "PackageBuildMode = \"StoreUpload\"",
                     "installerInstallationExecuted = $false",
                     "msixInstallationExecuted = $false",
                     "storeFlightExecuted = $false"
                 })
        {
            Assert.Contains(token, orchestrator, StringComparison.Ordinal);
        }
    }

    private static IReadOnlyDictionary<string, int> ReadLocalizedXamlUsages()
    {
        string projectDirectory = TestPaths.FromRepository("src/DeskBox");
        string separator = Path.DirectorySeparatorChar.ToString();
        var tagRegex = new Regex(
            @"<(?<tag>[A-Za-z_][A-Za-z0-9_.:-]*)\b(?:(?!>).)*svc:Localized\.(?:HeaderKey|DescriptionKey)(?:(?!>).)*>",
            RegexOptions.CultureInvariant | RegexOptions.Singleline);
        var propertyRegex = new Regex(
            @"svc:Localized\.(?<property>HeaderKey|DescriptionKey)=",
            RegexOptions.CultureInvariant);
        var usages = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (string path in Directory.EnumerateFiles(
                     projectDirectory,
                     "*.xaml",
                     SearchOption.AllDirectories))
        {
            if (path.Contains($"{separator}bin{separator}", StringComparison.OrdinalIgnoreCase) ||
                path.Contains($"{separator}obj{separator}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string xaml = File.ReadAllText(path);
            foreach (Match tagMatch in tagRegex.Matches(xaml))
            {
                string tag = tagMatch.Groups["tag"].Value;
                foreach (Match propertyMatch in propertyRegex.Matches(tagMatch.Value))
                {
                    string key = $"{tag}|{propertyMatch.Groups["property"].Value}";
                    usages[key] = usages.GetValueOrDefault(key) + 1;
                }
            }
        }

        return usages;
    }

    private static async Task<ProcessResult> RunValidationAsync(
        string platform,
        string runtimeIdentifier)
    {
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
        var startInfo = new ProcessStartInfo
        {
            FileName = dotnet,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (string argument in new[]
                 {
                     "msbuild",
                     TestPaths.FromRepository("src/DeskBox/DeskBox.csproj"),
                     "-nologo",
                     "-t:ValidateDeskBoxNativeAotConfiguration",
                     "-p:Configuration=Release",
                     "-p:PublishAot=true",
                     "-p:DeskBoxRustNative=true",
                     $"-p:Platform={platform}",
                     $"-p:RuntimeIdentifier={runtimeIdentifier}"
                 })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        Assert.True(process.Start());
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(
            process.ExitCode,
            string.Concat(await stdout, await stderr));
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    private sealed record ProcessResult(int ExitCode, string Output);
}
