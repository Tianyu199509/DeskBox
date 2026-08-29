namespace DeskBox.Tests;

public sealed class AotStage7C1ContractTests
{
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

        Assert.Contains(
            "<Content Remove=\"Assets\\wechat-qrcode.jpg\" />",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Content Remove=\"Assets\\Support\\*.png\" />",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Content Include=\"Assets\\wechat-qrcode.jpg\" Condition=\"'$(DeskBoxDistribution)' != 'Store'\">",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "<Content Include=\"Assets\\Support\\*.png\" Condition=\"'$(DeskBoxDistribution)' != 'Store'\">",
            project,
            StringComparison.Ordinal);
        // Direct payloads must actually carry the support QR images: without
        // explicit copy metadata the files never reach the publish directory
        // and the support dialog renders empty placeholders.
        Assert.Contains(
            "<CopyToPublishDirectory>Always</CopyToPublishDirectory>",
            project,
            StringComparison.Ordinal);
        Assert.Contains("'wechat-qrcode'", storeAudit, StringComparison.Ordinal);
        Assert.Contains("'Assets/Support/'", storeAudit, StringComparison.Ordinal);
        Assert.Contains(
            "DonationQrCodeVisibility",
            about,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseGates_RequireEverythingSdkAndLicenseInEveryPayload()
    {
        string retail = Read("scripts/publish-aot-retail.ps1");
        string stage7c1 = Read("scripts/build-stage-7c1-distribution.ps1");
        string storeAudit = Read("scripts/audit-store-native-aot-package.ps1");

        foreach (string script in new[] { retail, stage7c1, storeAudit })
        {
            Assert.Contains("EverythingSdk.dll", script, StringComparison.Ordinal);
            Assert.Contains("ThirdParty/Everything/LICENSE.txt", script, StringComparison.Ordinal);
        }

        // The retail gate must list the SDK twice: once in required files and
        // once in the PE architecture check.
        Assert.True(
            retail.Split("EverythingSdk.dll").Length - 1 >= 2,
            "publish-aot-retail.ps1 must gate EverythingSdk.dll in requiredFiles and the PE list.");
        Assert.Contains(
            "$everythingSdkMachine = Get-PeMachine",
            stage7c1,
            StringComparison.Ordinal);
        Assert.Contains(
            "$everythingSdkPe = Get-PeFacts",
            storeAudit,
            StringComparison.Ordinal);
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

        Assert.DoesNotContain(
            "-p:VCToolsInstallDir=",
            script,
            StringComparison.Ordinal);
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
                     "HasClrHeader",
                     "publishPayloadHashesMatch",
                     "DeskBox.pdb",
                     "deskbox_native.pdb",
                     "signingAndWackExecuted = $false"
                 })
        {
            Assert.Contains(token, script, StringComparison.Ordinal);
        }

        System.Text.RegularExpressions.Match minimumVersionMatch =
            System.Text.RegularExpressions.Regex.Match(
                script,
                @"frameworkDependency\.MinVersion\s+-lt\s+\[version\]""(?<version>\d+\.\d+\.\d+\.\d+)""",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        Assert.True(minimumVersionMatch.Success, "Store audit must enforce a Windows App Runtime minimum version.");
        Assert.Equal(new Version(2, 4, 0, 0), Version.Parse(minimumVersionMatch.Groups["version"].Value));
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
                "ShouldInstallWindowsAppRuntime := not IsWindowsAppRuntime24Installed",
                dependencies,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(
        "installer/DeskBox.Dependencies.iss",
        "X64",
        "https://aka.ms/windowsappsdk/2.4/2.4.0/windowsappruntimeinstall-x64.exe",
        "https://download.microsoft.com/download/097dbd99-ea76-49de-994b-eb935c72dcf1/WindowsAppRuntimeInstall-x64.exe")]
    [InlineData(
        "installer/DeskBox.Dependencies.arm64.iss",
        "ARM64",
        "https://aka.ms/windowsappsdk/2.4/2.4.0/windowsappruntimeinstall-arm64.exe",
        "https://download.microsoft.com/download/2f7e2917-37ac-43a3-990e-73838adaf281/WindowsAppRuntimeInstall-arm64.exe")]
    public void DirectInstaller_RequiresWindowsAppRuntime24ForMatchingArchitecture(
        string relativePath,
        string architecture,
        string primaryUrl,
        string fallbackUrl)
    {
        string dependencies = Read(relativePath);

        Assert.Contains("function IsWindowsAppRuntime24Installed: Boolean;", dependencies, StringComparison.Ordinal);
        Assert.DoesNotContain("IsWindowsAppRuntime22Installed", dependencies, StringComparison.Ordinal);
        Assert.Contains($"WindowsAppRuntimeUrl = '{primaryUrl}';", dependencies, StringComparison.Ordinal);
        Assert.Contains($"WindowsAppRuntimeFallbackUrl = '{fallbackUrl}';", dependencies, StringComparison.Ordinal);

        System.Text.RegularExpressions.MatchCollection minimumVersionMatches =
            System.Text.RegularExpressions.Regex.Matches(
                dependencies,
                @"\.Version\s+-ge\s+\[version\]''(?<version>\d+\.\d+\.\d+\.\d+)''",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        Assert.Equal(2, minimumVersionMatches.Count);
        foreach (System.Text.RegularExpressions.Match match in minimumVersionMatches)
        {
            Version minimumVersion = Version.Parse(match.Groups["version"].Value);
            Assert.Equal(new Version(2, 4, 0, 0), minimumVersion);
            Assert.True(new Version(2, 2, 0, 0) < minimumVersion, "Windows App Runtime 2.2 must not satisfy the 2.4 app contract.");
            Assert.True(new Version(2, 4, 0, 0) >= minimumVersion, "Windows App Runtime 2.4 must satisfy the app contract.");
        }

        Assert.Equal(
            2,
            System.Text.RegularExpressions.Regex.Matches(
                dependencies,
                $@"\.Architecture\s+-eq\s+''{architecture}''",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant).Count);
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

    private static string Read(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
