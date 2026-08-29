namespace DeskBox.Tests;

public sealed class AotStage5AContractTests
{
    [Fact]
    public void NativeAotPreviewRoot_IsReleaseAotOnlyAndKeepsDebugIsolation()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/Services/DeskBoxDataPathService.cs");

        Assert.Contains(
            "AotPreviewRootEnvironmentVariable = \"DESKBOX_AOT_PREVIEW_DATA_ROOT\"",
            source,
            StringComparison.Ordinal);
        Assert.Contains("#if DEBUG", source, StringComparison.Ordinal);
        Assert.Contains("#elif DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains(
            "Environment.GetEnvironmentVariable(DevelopmentRootEnvironmentVariable)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "Environment.GetEnvironmentVariable(AotPreviewRootEnvironmentVariable)",
            source,
            StringComparison.Ordinal);

        int debugBranch = source.IndexOf("#if DEBUG", StringComparison.Ordinal);
        int aotBranch = source.IndexOf("#elif DESKBOX_NATIVE_AOT", StringComparison.Ordinal);
        int releaseFallback = source.IndexOf("#else", aotBranch, StringComparison.Ordinal);
        Assert.True(debugBranch >= 0 && aotBranch > debugBranch && releaseFallback > aotBranch);
    }

    [Fact]
    public void PreviewLauncher_AcceptsOnlyTheCurrentStableX64AuditSummary()
    {
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");

        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("sourceStableDuringAudit", launcher, StringComparison.Ordinal);
        Assert.Contains("runtimeIdentifier", launcher, StringComparison.Ordinal);
        Assert.Contains("win-x64", launcher, StringComparison.Ordinal);
        Assert.Contains("platform", launcher, StringComparison.Ordinal);
        Assert.Contains("x64", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewLauncher_RefusesTheProductionRootOrAnyDescendant()
    {
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");

        Assert.Contains("Test-PathEqualOrInside", launcher, StringComparison.Ordinal);
        Assert.Contains(
            "Refusing to start Native AOT preview with the production data root",
            launcher,
            StringComparison.Ordinal);
        Assert.Contains("DeskBox-AotPreview", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("UseProductionData", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewLauncher_VerifiesTheExactAuditedExeAndRustDllHashes()
    {
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");

        Assert.Contains("expectedPublishDirectory", launcher, StringComparison.Ordinal);
        Assert.Contains("peImages", launcher, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", launcher, StringComparison.Ordinal);
        Assert.Contains("DeskBox.exe", launcher, StringComparison.Ordinal);
        Assert.Contains("deskbox_native.dll", launcher, StringComparison.Ordinal);
        Assert.Contains("rustNative.publishSha256", launcher, StringComparison.Ordinal);
        Assert.Contains("rustNative.publishMatchesStaging", launcher, StringComparison.Ordinal);
        Assert.Contains("rustNative.abiVersion", launcher, StringComparison.Ordinal);
        Assert.Contains("rustNative.capabilities", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewLauncher_ScopesAndRestoresBothDataRootVariables()
    {
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");

        Assert.Contains("DESKBOX_AOT_PREVIEW_DATA_ROOT", launcher, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_DEV_DATA_ROOT", launcher, StringComparison.Ordinal);
        Assert.Contains("previousAotPreviewRoot", launcher, StringComparison.Ordinal);
        Assert.Contains("previousDevelopmentRoot", launcher, StringComparison.Ordinal);
        Assert.Contains("finally", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewLauncher_StopsOnlyTheExactAuditedExecutable()
    {
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");

        Assert.Contains("Get-AotPreviewProcesses", launcher, StringComparison.Ordinal);
        Assert.Contains(
            "[System.IO.Path]::GetFullPath($_.ExecutablePath)",
            launcher,
            StringComparison.Ordinal);
        Assert.Contains("Stop-Process -Id $_.ProcessId -Force", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ExecutablePath.StartsWith($repoRootPath",
            launcher,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewLauncher_CanVerifyExistingInstanceRedirection()
    {
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");

        Assert.Contains("ExpectExistingInstance", launcher, StringComparison.Ordinal);
        Assert.Contains("existingPreviewProcesses", launcher, StringComparison.Ordinal);
        Assert.Contains("ExistingInstanceActivated", launcher, StringComparison.Ordinal);
        Assert.Contains("must exit after redirecting activation", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewLauncher_RecordsProductionAndPreviewEvidenceWithoutWritingProduction()
    {
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");

        Assert.Contains("Get-DirectoryStateFingerprint", launcher, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", launcher, StringComparison.Ordinal);
        Assert.Contains("productionDataFileCountBefore", launcher, StringComparison.Ordinal);
        Assert.Contains("$records.Sort([System.StringComparer]::Ordinal)", launcher, StringComparison.Ordinal);
        Assert.Contains("path-upper-length-lastwriteutc-v1-ordinal", launcher, StringComparison.Ordinal);
        Assert.DoesNotContain("Sort-Object FullName", launcher, StringComparison.Ordinal);
        Assert.Contains("session.json", launcher, StringComparison.Ordinal);
        Assert.Contains("previewDataRoot", launcher, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditAndProject_DeclareStage5AAsTheCurrentPreviewProfile()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5ASourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5AMissingDataPathPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5AMissingLauncherPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5AUnsafeLauncherPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5AExpectedWmc1510Count = 1213", audit, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("AOT preview data-root isolation", project, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
