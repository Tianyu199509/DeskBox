namespace DeskBox.Tests;

public sealed class AotStage5B4C1B2AContractTests
{
    [Fact]
    public void Scenario_IsNativeAotOnlyPhaseRunBoundAndGenerated()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string scenario = ReadRepositoryFile("src/DeskBox/App.AotShellMoveSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", shared, StringComparison.Ordinal);
        Assert.Contains("#if DESKBOX_NATIVE_AOT", scenario, StringComparison.Ordinal);
        Assert.Contains("ShellMovePersistenceRestart", shared, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_SHELL_MOVE_PHASE", shared, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiShellMoveCompensatePhase", shared, StringComparison.Ordinal);
        Assert.Contains("CaptureAotManagedUiShellMoveAsync", shared, StringComparison.Ordinal);
        Assert.Contains("public AotManagedUiShellMoveEvidence? ShellMove", shared, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(shared, "JsonSerializer.Serialize("));
        Assert.Contains(
            "AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult",
            shared,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_RequiresExactScenarioPhaseLowercaseRunAndOwnedDualRoots()
    {
        string fixture = ReadRepositoryFile("src/DeskBox/Services/AotShellMoveFixture.cs");

        Assert.Contains("ShellMovePersistenceRestart", fixture, StringComparison.Ordinal);
        Assert.Contains("aot-5b4c1b2a-file", fixture, StringComparison.Ordinal);
        Assert.Contains("phase is not \"Mutate\"", fixture, StringComparison.Ordinal);
        Assert.Contains("not \"Compensate\"", fixture, StringComparison.Ordinal);
        Assert.Contains("value is { Length: 32 }", fixture, StringComparison.Ordinal);
        Assert.Contains("character is >= '0' and <= '9'", fixture, StringComparison.Ordinal);
        Assert.Contains(">= 'a' and <= 'f'", fixture, StringComparison.Ordinal);
        Assert.Contains("WidgetRootDirectoryName", fixture, StringComparison.Ordinal);
        Assert.Contains("DesktopRootDirectoryName", fixture, StringComparison.Ordinal);
        Assert.Contains("IsPathEqualOrInside", fixture, StringComparison.Ordinal);
        Assert.Contains("TryGetOwnedDesktopPath", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCALAPPDATA", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DesktopDirectory", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductOwnerChain_PropagatesRealHostFromSurfaceToShellApi()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs");
        string dragFallback = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        string viewModel = ReadRepositoryFile(
            "src/DeskBox/ViewModels/WidgetViewModel.Operations.cs");
        string organizer = ReadRepositoryFile("src/DeskBox/Services/OrganizerService.cs");
        string fileService = ReadRepositoryFile("src/DeskBox/Services/FileService.cs");

        Assert.Contains("ownerWindowHandle: _hostWindowHandle", surface, StringComparison.Ordinal);
        Assert.Contains("ownerWindowHandle: _hostWindowHandle", dragFallback, StringComparison.Ordinal);
        Assert.Contains("IntPtr ownerWindowHandle = default", viewModel, StringComparison.Ordinal);
        Assert.Contains("ownerWindowHandle);", viewModel, StringComparison.Ordinal);
        Assert.Contains("IntPtr ownerWindowHandle = default", organizer, StringComparison.Ordinal);
        Assert.Contains("ownerWindowHandle);", organizer, StringComparison.Ordinal);
        Assert.Contains("WindowHandle = ownerWindowHandle", fileService, StringComparison.Ordinal);
        Assert.Contains("SHFileOperation(ref fileOperation)", fileService, StringComparison.Ordinal);
    }

    [Fact]
    public void MenuProbe_UsesRealSingleMultiMenusAutomationAndProductFeedback()
    {
        string probe = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotShellMoveSmoke.cs");

        Assert.Contains("CreateItemFlyout(selectedItems[0])", probe, StringComparison.Ordinal);
        Assert.Contains("CreateMultiSelectionFlyout()", probe, StringComparison.Ordinal);
        Assert.Contains("Widget.MoveBackToDesktop", probe, StringComparison.Ordinal);
        Assert.Contains("_hostWindowHandle.ToInt64()", probe, StringComparison.Ordinal);
        Assert.Contains("MenuFlyoutItemAutomationPeer", probe, StringComparison.Ordinal);
        Assert.Contains("PatternInterface.Invoke", probe, StringComparison.Ordinal);
        Assert.Contains("IInvokeProvider", probe, StringComparison.Ordinal);
        Assert.Contains("invokeProvider.Invoke()", probe, StringComparison.Ordinal);
        Assert.Contains("FeedbackRequested += OnFeedbackRequested", probe, StringComparison.Ordinal);
        Assert.Contains("file-move-desktop", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("ViewModel.MoveItemsBackToDesktopAsync", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void FileService_PreservesRealShellPathAndRecordsAllReturnBranches()
    {
        string fileService = ReadRepositoryFile("src/DeskBox/Services/FileService.cs");
        string fixture = ReadRepositoryFile("src/DeskBox/Services/AotShellMoveFixture.cs");

        Assert.Contains("MoveEntriesWithShellProgress(", fileService, StringComparison.Ordinal);
        Assert.Contains("AotShellMoveFixture.TryExecute", fileService, StringComparison.Ordinal);
        Assert.Contains("AotShellMoveFixture.GetRecoveryProbeDelay", fileService, StringComparison.Ordinal);
        Assert.Contains("AotShellMoveFixture.ReturnedOutcome", fileService, StringComparison.Ordinal);
        Assert.Contains("AotShellMoveFixture.RecoveredPendingOutcome", fileService, StringComparison.Ordinal);
        Assert.Contains("AotShellMoveFixture.ExtendedWaitOutcome", fileService, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(15)", fileService, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromMilliseconds(150)", fixture, StringComparison.Ordinal);
        Assert.Contains("Thread.Sleep(800)", fixture, StringComparison.Ordinal);
        Assert.Contains("FileService.IsCompletedShellMove", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void ControlledBranches_AreExactOwnedAndCoverRealPartialCancelLate()
    {
        string fixture = ReadRepositoryFile("src/DeskBox/Services/AotShellMoveFixture.cs");

        Assert.Contains("case RealMode", fixture, StringComparison.Ordinal);
        Assert.Contains("executeRealShellMove()", fixture, StringComparison.Ordinal);
        Assert.Contains("case PartialMode", fixture, StringComparison.Ordinal);
        Assert.Contains("paths.PartialFirstSourcePath", fixture, StringComparison.Ordinal);
        Assert.Contains("case CancelMode", fixture, StringComparison.Ordinal);
        Assert.Contains("case LateMode", fixture, StringComparison.Ordinal);
        Assert.Contains("SimulatedOperationsAborted = true", fixture, StringComparison.Ordinal);
        Assert.Contains("ownerWindowHandle == IntPtr.Zero", fixture, StringComparison.Ordinal);
        Assert.Contains("expectedSource", fixture, StringComparison.Ordinal);
        Assert.Contains("expectedDestination", fixture, StringComparison.Ordinal);
        Assert.Contains("unsupported owned selection shape", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void AppMatrix_ProvesMenusOwnersPartialCancelLateHistoryRestartAndRestore()
    {
        string scenario = ReadRepositoryFile("src/DeskBox/App.AotShellMoveSmoke.cs");

        Assert.Equal(4, CountOccurrences(scenario, "InvokeAotShellMoveBackToDesktopAsync("));
        Assert.Contains("ShellMoveMenuMatrixCompleted", scenario, StringComparison.Ordinal);
        Assert.Contains("LateTaskPendingWhenProductReturned", scenario, StringComparison.Ordinal);
        Assert.Contains("RecoveredPendingOutcome", scenario, StringComparison.Ordinal);
        Assert.Contains("[1, 0, 1, 1]", scenario, StringComparison.Ordinal);
        Assert.Contains("case \"VerifyRestore\"", scenario, StringComparison.Ordinal);
        Assert.Contains("case \"Postflight\"", scenario, StringComparison.Ordinal);
        Assert.Contains("case \"Compensate\"", scenario, StringComparison.Ordinal);
        Assert.Contains("RecentOrganizationHistory.Clear()", scenario, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData(stream)", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_UsesUniqueOwnedRootsThreeProcessesHashesAndSafeCleanup()
    {
        string master = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");
        string runner = ReadRepositoryFile(
            "scripts/run-aot-shell-move-persistence-smoke.ps1");

        Assert.Contains("ShellMovePersistenceRestart", master, StringComparison.Ordinal);
        Assert.Contains("run-aot-shell-move-persistence-smoke.ps1", master, StringComparison.Ordinal);
        Assert.Contains("[Guid]::NewGuid().ToString(\"N\")", runner, StringComparison.Ordinal);
        Assert.Contains("shell-move-preview-$runId", runner, StringComparison.Ordinal);
        Assert.Contains("$DataRoot-Recovery", runner, StringComparison.Ordinal);
        Assert.Contains("Invoke-ShellMovePhase", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"Mutate\"", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"VerifyRestore\"", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"Postflight\"", runner, StringComparison.Ordinal);
        Assert.Contains("mutate-independent-disk", runner, StringComparison.Ordinal);
        Assert.Contains("verify-restore-independent-hashes", runner, StringComparison.Ordinal);
        Assert.Contains("postflight-independent-hashes", runner, StringComparison.Ordinal);
        Assert.Contains("$processIds | Sort-Object -Unique", runner, StringComparison.Ordinal);
        Assert.Contains("$phaseExecutableHashes | Sort-Object -Unique", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", runner, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $resolvedRoot -Recurse -Force", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void FailurePath_UsesIndependentCompensationAndPreservesOwnedIdentity()
    {
        string runner = ReadRepositoryFile(
            "scripts/run-aot-shell-move-persistence-smoke.ps1");

        Assert.Contains("$safetyVerified = $false", runner, StringComparison.Ordinal);
        Assert.Contains("$safetyVerified = $true", runner, StringComparison.Ordinal);
        Assert.Contains("if (-not $safetyVerified)", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"Compensate\"", runner, StringComparison.Ordinal);
        Assert.Contains("compensation-independent-disk", runner, StringComparison.Ordinal);
        Assert.Contains("compensation-independent-hashes", runner, StringComparison.Ordinal);
        Assert.Contains("owned preview/recovery roots and run ID", runner, StringComparison.Ordinal);
        Assert.Contains("were preserved for recovery", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void StageScope_KeepsRustAbiAndDefersPropertiesPickerAndPhysicalDrag()
    {
        string combined =
            ReadRepositoryFile("src/DeskBox/App.AotShellMoveSmoke.cs") +
            ReadRepositoryFile("src/DeskBox/Services/AotShellMoveFixture.cs") +
            ReadRepositoryFile(
                "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotShellMoveSmoke.cs");
        string rust = ReadRepositoryFile("native/deskbox-native/src/lib.rs");

        Assert.DoesNotContain("FileOpenPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeDrop", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowFileProperties", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("IFileOperation", combined, StringComparison.Ordinal);
        Assert.Contains("assert_eq!(deskbox_native_capabilities(), 511);", rust, StringComparison.Ordinal);
        Assert.Equal(10, CountOccurrences(rust, "#[unsafe(no_mangle)]"));
    }

    [Fact]
    public void Stage5B4C1B2A_ProfileSchemaProjectReportAndRoadmapAdvanceTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");
        string report = ReadRepositoryFile(
            "docs/architecture/stage-reports/aot-stage-5b-4c1b2a-report.md");
        string roadmap = ReadRepositoryFile(
            "docs/architecture/rust-native-aot-roadmap.md");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1B2A", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredRustCapabilities = 511", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredRustExportCount = 10", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("5B-4C1B2A 已完成", report, StringComparison.Ordinal);
        Assert.Contains("真实 owner HWND", report, StringComparison.Ordinal);
        Assert.Contains("5B-4C1B2A", roadmap, StringComparison.Ordinal);
        Assert.Contains("profile 49 / schema 46", roadmap, StringComparison.Ordinal);
        Assert.Contains("5B-4C1B2B", roadmap, StringComparison.Ordinal);
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
