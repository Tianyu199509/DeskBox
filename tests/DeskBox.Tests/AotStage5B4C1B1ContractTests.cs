namespace DeskBox.Tests;

public sealed class AotStage5B4C1B1ContractTests
{
    [Fact]
    public void Scenario_IsNativeAotOnlyPhaseBoundAndUsesSingleGeneratedWriter()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string scenario = ReadRepositoryFile("src/DeskBox/App.AotRecycleBinSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", shared, StringComparison.Ordinal);
        Assert.Contains("#if DESKBOX_NATIVE_AOT", scenario, StringComparison.Ordinal);
        Assert.Contains("RecycleBinMenuPersistenceRestart", shared, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_RECYCLE_BIN_PHASE", shared, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiRecycleBinCompensatePhase", shared, StringComparison.Ordinal);
        Assert.Contains("CaptureAotManagedUiRecycleBinAsync", shared, StringComparison.Ordinal);
        Assert.Contains("public AotManagedUiRecycleBinEvidence? RecycleBin", shared, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(shared, "JsonSerializer.Serialize("));
        Assert.Contains(
            "AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult",
            shared,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_RequiresExactScenarioPhaseLowercaseRunIdentityAndOwnedTree()
    {
        string fixture = ReadRepositoryFile("src/DeskBox/Services/AotRecycleBinFixture.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", fixture, StringComparison.Ordinal);
        Assert.Contains("RecycleBinMenuPersistenceRestart", fixture, StringComparison.Ordinal);
        Assert.Contains("aot-5b4c1b1-file", fixture, StringComparison.Ordinal);
        Assert.Contains("phase is not \"Mutate\"", fixture, StringComparison.Ordinal);
        Assert.Contains("not \"Compensate\"", fixture, StringComparison.Ordinal);
        Assert.Contains("value is { Length: 32 }", fixture, StringComparison.Ordinal);
        Assert.Contains("character is >= '0' and <= '9'", fixture, StringComparison.Ordinal);
        Assert.Contains(">= 'a' and <= 'f'", fixture, StringComparison.Ordinal);
        Assert.Contains("single-{runId}", fixture, StringComparison.Ordinal);
        Assert.Contains("multi-file-{runId}", fixture, StringComparison.Ordinal);
        Assert.Contains("multi-folder-{runId}", fixture, StringComparison.Ordinal);
        Assert.Contains("payload-{runId}", fixture, StringComparison.Ordinal);
        Assert.Contains("IsPathEqualOrInside", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCALAPPDATA", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DesktopDirectory", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductDeleteChain_RemainsRealMenuSurfaceViewModelAndShellDelete()
    {
        string menu = ReadRepositoryFile("src/DeskBox/Controls/FileItemMenuBuilder.cs");
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs");
        string surfaceOperations = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        string viewModel = ReadRepositoryFile(
            "src/DeskBox/ViewModels/WidgetViewModel.Operations.cs");
        string fileService = ReadRepositoryFile("src/DeskBox/Services/FileService.cs");

        Assert.Contains("CreateItemFlyout", menu, StringComparison.Ordinal);
        Assert.Contains("CreateMultiSelectionFlyout", menu, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(menu, "Widget.MoveToRecycleBin"));
        Assert.Equal(
            2,
            CountOccurrences(menu, "await actions.DeleteItemsAsync(actions.GetSelectedItems())"));
        Assert.Contains("FileItemMenuBuilder.CreateItemFlyout", surface, StringComparison.Ordinal);
        Assert.Contains("FileItemMenuBuilder.CreateMultiSelectionFlyout", surface, StringComparison.Ordinal);
        Assert.Contains("items => DeleteItemsAsync(items)", surface, StringComparison.Ordinal);
        Assert.Contains("bool permanently = false", surfaceOperations, StringComparison.Ordinal);
        Assert.Contains("public async Task<FileDeleteBatchResult> DeleteItemsAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("_fileService.DeleteEntryAsync(", viewModel, StringComparison.Ordinal);
        Assert.Contains("DeleteEntryWithShell(normalizedPath, ownerHandle, allowUndo: true)", fileService, StringComparison.Ordinal);
        Assert.Contains("SHFileOperation(ref operation)", fileService, StringComparison.Ordinal);
        Assert.Contains("FofAllowUndo", fileService, StringComparison.Ordinal);
    }

    [Fact]
    public void MenuProbe_InvokesRealSingleAndMultiMenuAutomationAndCapturesFeedback()
    {
        string probe = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotRecycleBinSmoke.cs");

        Assert.Contains("CreateItemFlyout(selectedItems[0])", probe, StringComparison.Ordinal);
        Assert.Contains("CreateMultiSelectionFlyout()", probe, StringComparison.Ordinal);
        Assert.Contains("Widget.MoveToRecycleBin", probe, StringComparison.Ordinal);
        Assert.Contains("MenuFlyoutItemAutomationPeer", probe, StringComparison.Ordinal);
        Assert.Contains("PatternInterface.Invoke", probe, StringComparison.Ordinal);
        Assert.Contains("IInvokeProvider", probe, StringComparison.Ordinal);
        Assert.Contains("invokeProvider.Invoke()", probe, StringComparison.Ordinal);
        Assert.Contains("FeedbackRequested += OnFeedbackRequested", probe, StringComparison.Ordinal);
        Assert.Contains("file-delete", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.DeleteItemsAsync", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void RustBoundary_EnumeratesFullBinAndRestoresOnlyOneExactIdentity()
    {
        string rust = ReadRepositoryFile("native/deskbox-native/src/recycle_bin.rs");

        Assert.Contains("const RECYCLE_BIN_CSIDL: i32 = 10", rust, StringComparison.Ordinal);
        Assert.Contains("System.Recycle.DeletedFrom", rust, StringComparison.Ordinal);
        Assert.Contains("item.Name()", rust, StringComparison.Ordinal);
        Assert.Contains("GetFullPathNameW", rust, StringComparison.Ordinal);
        Assert.Contains("CompareStringOrdinal", rust, StringComparison.Ordinal);
        Assert.Contains("let mut restore_item: Option<FolderItem> = None", rust, StringComparison.Ordinal);
        Assert.Contains("if result.matched_count != 1", rust, StringComparison.Ordinal);
        Assert.Contains("const RESTORE_VERB: &str = \"undelete\"", rust, StringComparison.Ordinal);
        Assert.True(
            rust.IndexOf("result.enumerate_hresult = DESKBOX_NATIVE_S_OK", StringComparison.Ordinal) <
            rust.IndexOf("item.InvokeVerb(&verb)", StringComparison.Ordinal));
        Assert.DoesNotContain("SHEmptyRecycleBin", rust, StringComparison.Ordinal);
        Assert.DoesNotContain("IFileOperation", rust, StringComparison.Ordinal);
        Assert.DoesNotContain("$Recycle.Bin", rust, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAbi_HeaderRustManagedAndBuildContractAdvanceTogether()
    {
        string header = ReadRepositoryFile("native/include/deskbox_native.h");
        string rust = ReadRepositoryFile("native/deskbox-native/src/lib.rs");
        string managed = ReadRepositoryFile("src/DeskBox/Helpers/RecycleBinNativeBackend.cs");
        string build = ReadRepositoryFile("scripts/build-rust-native.ps1");

        Assert.Contains("DESKBOX_NATIVE_CAPABILITY_RECYCLE_BIN_V1 (1ull << 8)", header, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_RECYCLE_BIN_REQUEST_V1_SIZE_64 80u", header, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_RECYCLE_BIN_RESULT_V1_SIZE_64 104u", header, StringComparison.Ordinal);
        Assert.Contains("DeskBoxRecycleBinRequestV1", header, StringComparison.Ordinal);
        Assert.Contains("DeskBoxRecycleBinResultV1", header, StringComparison.Ordinal);
        Assert.Contains("deskbox_recycle_bin_v1", header, StringComparison.Ordinal);
        Assert.Contains("assert_eq!(deskbox_native_capabilities(), 511);", rust, StringComparison.Ordinal);
        Assert.Equal(9, CountOccurrences(rust, "pub const DESKBOX_NATIVE_CAPABILITY_"));
        Assert.Equal(10, CountOccurrences(rust, "#[unsafe(no_mangle)]"));
        Assert.Contains("RecycleBinCapability = 1UL << 8", managed, StringComparison.Ordinal);
        Assert.Contains("deskbox_recycle_bin_v1", managed, StringComparison.Ordinal);
        Assert.Contains("result.Reserved5 != 0", managed, StringComparison.Ordinal);
        Assert.Contains("expected 511", build, StringComparison.Ordinal);
        Assert.Contains("deskbox_recycle_bin_v1", build, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedBackend_RejectsInvalidIdentityEnvelopeAndInconsistentRestore()
    {
        string managed = ReadRepositoryFile("src/DeskBox/Helpers/RecycleBinNativeBackend.cs");

        Assert.Contains("Enum.IsDefined(operation)", managed, StringComparison.Ordinal);
        Assert.Contains("Path.IsPathFullyQualified(originalName)", managed, StringComparison.Ordinal);
        Assert.Contains("originalName.Contains(Path.DirectorySeparatorChar)", managed, StringComparison.Ordinal);
        Assert.Contains("!value.Contains('\\0')", managed, StringComparison.Ordinal);
        Assert.Contains("NativeLibrary.TryGetExport", managed, StringComparison.Ordinal);
        Assert.Contains("returnedStatus != result.Status", managed, StringComparison.Ordinal);
        Assert.Contains("result.RestoredCount > result.MatchedCount", managed, StringComparison.Ordinal);
        Assert.Contains("result.MatchedCount != 1 || result.RestoredCount != 1", managed, StringComparison.Ordinal);
        Assert.Contains("InvalidNativeResult", managed, StringComparison.Ordinal);
    }

    [Fact]
    public void AppMatrix_UsesMenusCrossProcessQueriesExactRestoreAndHashes()
    {
        string scenario = ReadRepositoryFile("src/DeskBox/App.AotRecycleBinSmoke.cs");

        Assert.Contains("InvokeAotRecycleBinMenuDeleteAsync", scenario, StringComparison.Ordinal);
        Assert.Contains("WaitForAotLocalFileSurfaceAsync", scenario, StringComparison.Ordinal);
        Assert.Contains("RecycleBinSingleMenuDeleteCompleted", scenario, StringComparison.Ordinal);
        Assert.Contains("RecycleBinMultiMenuDeleteCompleted", scenario, StringComparison.Ordinal);
        Assert.Contains("case \"VerifyRestore\"", scenario, StringComparison.Ordinal);
        Assert.Contains("case \"Postflight\"", scenario, StringComparison.Ordinal);
        Assert.Contains("case \"Compensate\"", scenario, StringComparison.Ordinal);
        Assert.Contains("RecycleBinNativeOperation.Query", scenario, StringComparison.Ordinal);
        Assert.Contains("RecycleBinNativeOperation.Restore", scenario, StringComparison.Ordinal);
        Assert.Contains("query.MatchedCount == (exists ? 0U : 1U)", scenario, StringComparison.Ordinal);
        Assert.Contains("restore.MatchedCount == 1", scenario, StringComparison.Ordinal);
        Assert.Contains("restore.RestoredCount == 1", scenario, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData(stream)", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_UsesUniqueRootThreeProcessesExactHashesAndSafeCleanup()
    {
        string runner = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("[Guid]::NewGuid().ToString(\"N\")", runner, StringComparison.Ordinal);
        Assert.Contains("recycle-preview-$recycleBinRunId", runner, StringComparison.Ordinal);
        Assert.Contains("$DataRoot-Recovery", runner, StringComparison.Ordinal);
        Assert.Contains("Refusing to replace an existing Recycle Bin preview root", runner, StringComparison.Ordinal);
        Assert.Contains("Refusing to replace an existing Recycle Bin recovery root", runner, StringComparison.Ordinal);
        Assert.Contains("Invoke-RecycleBinPhase", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"Mutate\"", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"VerifyRestore\"", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"Postflight\"", runner, StringComparison.Ordinal);
        Assert.Contains("mutate-independent-disk", runner, StringComparison.Ordinal);
        Assert.Contains("verify-restore-independent-disk", runner, StringComparison.Ordinal);
        Assert.Contains("postflight-independent-disk", runner, StringComparison.Ordinal);
        Assert.Contains("$processIds | Sort-Object -Unique", runner, StringComparison.Ordinal);
        Assert.Contains("$phaseExecutableHashes | Sort-Object -Unique", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", runner, StringComparison.Ordinal);
        Assert.Contains("foreach ($property in @(\"relativePath\", \"length\", \"sha256\"))", runner, StringComparison.Ordinal);
        Assert.Contains("Refusing to clean an unowned Recycle Bin preview root", runner, StringComparison.Ordinal);
        Assert.Contains("Refusing to clean an unowned Recycle Bin recovery root", runner, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force", runner, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $resolvedRecoveryRoot -Recurse -Force", runner, StringComparison.Ordinal);
        Assert.Contains("recoveryRootCleaned", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void FailurePath_UsesIndependentCompensationAndPreservesRecoveryIdentityOnFailure()
    {
        string runner = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("$recycleSafetyVerified = $false", runner, StringComparison.Ordinal);
        Assert.Contains("$recycleSafetyVerified = $true", runner, StringComparison.Ordinal);
        Assert.Contains("if ($recycleSafetyVerified)", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"Compensate\"", runner, StringComparison.Ordinal);
        Assert.Contains("compensation-independent-disk", runner, StringComparison.Ordinal);
        Assert.Contains("owned preview/recovery roots and run ID", runner, StringComparison.Ordinal);
        Assert.Contains("were preserved for recovery", runner, StringComparison.Ordinal);
        Assert.Contains("RecycleBinCompensationIdentityQueried", runner, StringComparison.Ordinal);
        Assert.Contains("RecycleBinCompensationCompleted", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void StageScope_DefersShellProgressPropertiesPickerAndPhysicalDrag()
    {
        string combined =
            ReadRepositoryFile("src/DeskBox/App.AotRecycleBinSmoke.cs") +
            ReadRepositoryFile("src/DeskBox/Services/AotRecycleBinFixture.cs") +
            ReadRepositoryFile(
                "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotRecycleBinSmoke.cs") +
            ReadRepositoryFile("native/deskbox-native/src/recycle_bin.rs");

        Assert.DoesNotContain("FileOpenPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeDrop", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("IFileOperation", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("MoveEntriesWithShellProgress", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("useShellProgress: true", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowFileProperties", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SHEmptyRecycleBin", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage5B4C1B1_ProfileSchemaProjectReportsAndRoadmapAdvanceTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");
        string report = ReadRepositoryFile(
            "docs/architecture/aot-stage-5b-4c1b1-report.md");
        string abi = ReadRepositoryFile(
            "docs/architecture/recycle-bin-native-abi-v1.md");
        string roadmap = ReadRepositoryFile(
            "docs/architecture/rust-native-aot-roadmap.md");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1B1", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredRustCapabilities = 511", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredRustExportCount = 10", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("Recycle Bin", project, StringComparison.Ordinal);
        Assert.Contains("5B-4C1B1 已完成", report, StringComparison.Ordinal);
        Assert.Contains("精确恢复", report, StringComparison.Ordinal);
        Assert.Contains("deskbox_recycle_bin_v1", abi, StringComparison.Ordinal);
        Assert.Contains("5B-4C1B1", roadmap, StringComparison.Ordinal);
        Assert.Contains("profile 49 / schema 46", roadmap, StringComparison.Ordinal);
        Assert.Contains("5B-4C1B2", roadmap, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string value, string token)
    {
        int count = 0;
        int index = 0;
        while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
