namespace DeskBox.Tests;

public sealed class AotStage5B4C1C1ContractTests
{
    [Fact]
    public void Scenario_IsNativeAotOnlyPhasedOwnedAndNormallyShutDown()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", shared, StringComparison.Ordinal);
        Assert.Contains("PickerClipboardStorageItemsPersistenceRestart", shared, StringComparison.Ordinal);
        Assert.Contains("picker-clipboard-storage-items-persistence-restart", shared, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_PICKER_CLIPBOARD_PHASE", shared, StringComparison.Ordinal);
        Assert.Contains("aot-5b4c1c1-file", shared, StringComparison.Ordinal);
        Assert.Contains("CaptureAotManagedUiPickerClipboardAsync", shared, StringComparison.Ordinal);
        Assert.Contains("public AotManagedUiPickerClipboardEvidence? PickerClipboard", shared, StringComparison.Ordinal);
        Assert.Contains("NormalShutdownRequested = true", shared, StringComparison.Ordinal);
        Assert.Contains("ShutdownApplicationAsync()", shared, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(shared, "JsonSerializer.Serialize("));
    }

    [Fact]
    public void ProductPicker_UsesModernWindowIdApiAndRejectsInvalidOwner()
    {
        string service = ReadRepositoryFile(
            "src/DeskBox/Services/FileOpenPickerService.cs");

        Assert.Contains("Microsoft.Windows.Storage.Pickers", service, StringComparison.Ordinal);
        Assert.Contains("GetWindowIdFromWindow(", service, StringComparison.Ordinal);
        Assert.Contains("new FileOpenPicker(ownerWindowId)", service, StringComparison.Ordinal);
        Assert.Contains("SuggestedStartLocation = PickerLocationId.Desktop", service, StringComparison.Ordinal);
        Assert.Contains("picker.SuggestedFolder = normalizedFolder", service, StringComparison.Ordinal);
        Assert.Contains("await picker.PickMultipleFilesAsync()", service, StringComparison.Ordinal);
        Assert.Contains("ownerHwnd == IntPtr.Zero", service, StringComparison.Ordinal);
        Assert.Contains("!Win32Helper.IsWindow(ownerHwnd)", service, StringComparison.Ordinal);
        Assert.DoesNotContain("InitializeWithWindow", service, StringComparison.Ordinal);
        Assert.DoesNotContain("GetForegroundWindow", service, StringComparison.Ordinal);
    }

    [Fact]
    public void FileSurface_PassesExactHostOwnerAndKeepsNormalPickerRoute()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        string method = SliceBetween(
            surface,
            "private async Task PickAndImportFilesAsync()",
            "private async Task RunAsync");

        Assert.Contains("FileOpenPickerService.PickFilesAsync(", method, StringComparison.Ordinal);
        Assert.Contains("_hostWindowHandle,", method, StringComparison.Ordinal);
        Assert.Contains("PickAndImportFilesAsync(suggestedFolder: null)", method, StringComparison.Ordinal);
        Assert.Contains("ImportPathsWithTrackedProgressAsync(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetForegroundWindow", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAncestor", method, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows.Storage.Pickers", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void ClipboardParser_SeparatesGlobalTransportFromReusableDataPackagePath()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        string method = SliceBetween(
            surface,
            "private async Task PasteFromClipboardAsync()",
            "private static DataPackageView? TryGetClipboardContent()");

        Assert.Contains("TryGetClipboardContent()", method, StringComparison.Ordinal);
        Assert.Contains("PasteDataPackageAsync(", method, StringComparison.Ordinal);
        Assert.Contains("includeShellFileDropFallback: true", method, StringComparison.Ordinal);
        Assert.Contains("clipboard?.Contains(StandardDataFormats.StorageItems) == true", method, StringComparison.Ordinal);
        Assert.Contains("await clipboard.GetStorageItemsAsync()", method, StringComparison.Ordinal);
        Assert.Contains("includeShellFileDropFallback &&", method, StringComparison.Ordinal);
        Assert.Contains("ImportPathsWithTrackedProgressAsync(", method, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_RequiresExactScenarioPhaseRunIdAndOwnedPreviewPaths()
    {
        string fixture = ReadRepositoryFile(
            "src/DeskBox/Services/AotPickerClipboardFixture.cs");

        Assert.Contains("PickerClipboardStorageItemsPersistenceRestart", fixture, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_PICKER_CLIPBOARD_PHASE", fixture, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_PICKER_CLIPBOARD_RUN_ID", fixture, StringComparison.Ordinal);
        Assert.Contains("runId is not { Length: 32 }", fixture, StringComparison.Ordinal);
        Assert.Contains("character is not (>= '0' and <= '9')", fixture, StringComparison.Ordinal);
        Assert.Contains("not (>= 'a' and <= 'f')", fixture, StringComparison.Ordinal);
        Assert.Contains("dataPaths.IsDevelopmentRoot", fixture, StringComparison.Ordinal);
        Assert.Contains("configuredPreviewRoot", fixture, StringComparison.Ordinal);
        Assert.Contains("IsPathEqualOrInside(dataPaths.RootPath, fixtureRoot)", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCALAPPDATA", fixture, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DialogObserver_RecordsSystemWindowOwnerChainAndNaturalDestruction()
    {
        string fixture = ReadRepositoryFile(
            "src/DeskBox/Services/AotPickerClipboardFixture.cs");

        Assert.Contains("CaptureVisibleTopLevelWindowHandles", fixture, StringComparison.Ordinal);
        Assert.Contains("ObservePickerDialogAsync", fixture, StringComparison.Ordinal);
        Assert.Contains("baselineWindowHandles.Contains", fixture, StringComparison.Ordinal);
        Assert.Contains("\"#32770\"", fixture, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.GW_OWNER", fixture, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.GA_ROOTOWNER", fixture, StringComparison.Ordinal);
        Assert.Contains("CaptureOwnerChain", fixture, StringComparison.Ordinal);
        Assert.Contains("OwnerChainContainsExpected", fixture, StringComparison.Ordinal);
        Assert.Contains("IsSamePickerWindow(candidate)", fixture, StringComparison.Ordinal);
        Assert.Contains(
            "windowThreadId == candidate.WindowThreadId",
            fixture,
            StringComparison.Ordinal);
        Assert.Contains("processId == candidate.ProcessId", fixture, StringComparison.Ordinal);
        Assert.Contains("WindowDestroyedAfterAction", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("PostMessage", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_UsesRealPickerAndFileFolderStorageItemsWithoutGlobalClipboardMutation()
    {
        string probe = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotPickerClipboardSmoke.cs");

        Assert.Contains("PickAndImportFilesAsync(suggestedFolder)", probe, StringComparison.Ordinal);
        Assert.Contains("ObservePickerDialogAsync(", probe, StringComparison.Ordinal);
        Assert.Contains("GetStorageItemsAsync(normalizedPaths)", probe, StringComparison.Ordinal);
        Assert.Contains("package.SetStorageItems(storageItems)", probe, StringComparison.Ordinal);
        Assert.Contains("DataPackageView view = package.GetView()", probe, StringComparison.Ordinal);
        Assert.Contains("view.Contains(StandardDataFormats.StorageItems)", probe, StringComparison.Ordinal);
        Assert.Contains("await view.GetStorageItemsAsync()", probe, StringComparison.Ordinal);
        Assert.Contains("includeShellFileDropFallback: false", probe, StringComparison.Ordinal);
        Assert.Contains("GlobalClipboardUntouched: true", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard.SetContent", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard.GetContent", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void AppEvidence_CoversCancelSelectStorageItemsRestartHashesAndPostflight()
    {
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotPickerClipboardSmoke.cs");

        Assert.Contains("InteractionState = \"CancelPending\"", scenario, StringComparison.Ordinal);
        Assert.Contains("InteractionState = \"SelectionPending\"", scenario, StringComparison.Ordinal);
        Assert.Contains("InvokeAotFilePickerAsync(", scenario, StringComparison.Ordinal);
        Assert.Contains("PickerCancelNoChangeVerified", scenario, StringComparison.Ordinal);
        Assert.Contains("PickerSelectionImported", scenario, StringComparison.Ordinal);
        Assert.Contains("ImportAotClipboardStorageItemsAsync(", scenario, StringComparison.Ordinal);
        Assert.Contains("ClipboardStorageItemsImported", scenario, StringComparison.Ordinal);
        Assert.Contains("StorageFile", scenario, StringComparison.Ordinal);
        Assert.Contains("StorageFolder", scenario, StringComparison.Ordinal);
        Assert.Contains("PickerClipboardRestartMutationVerified", scenario, StringComparison.Ordinal);
        Assert.Contains("PickerClipboardPostflightVerified", scenario, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData(stream)", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_DrivesTwoRealDialogsThreeProcessesAndSafeOwnedCleanup()
    {
        string master = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");
        string runner = ReadRepositoryFile("scripts/run-aot-picker-clipboard-smoke.ps1");

        Assert.Contains("run-aot-picker-clipboard-smoke.ps1", master, StringComparison.Ordinal);
        Assert.Contains("UIAutomationClient", runner, StringComparison.Ordinal);
        Assert.Contains("Wait-PickerAutomationWindow", runner, StringComparison.Ordinal);
        Assert.Contains("CancelPending", runner, StringComparison.Ordinal);
        Assert.Contains("SelectionPending", runner, StringComparison.Ordinal);
        Assert.Contains("ValuePattern", runner, StringComparison.Ordinal);
        Assert.Contains("InvokePattern", runner, StringComparison.Ordinal);
        Assert.Contains("FindVisibleDialog", runner, StringComparison.Ordinal);
        Assert.Contains(
            "AutomationElement]::FromHandle",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("Get-AutomationElementsById", runner, StringComparison.Ordinal);
        Assert.Contains("BM_CLICK", runner, StringComparison.Ordinal);
        Assert.Contains("WM_SETTEXT", runner, StringComparison.Ordinal);
        Assert.Contains("Invoke-PickerClipboardPhase", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"Mutate\"", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"VerifyRestore\"", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"Postflight\"", runner, StringComparison.Ordinal);
        Assert.Contains(
            "$dataDirectory = Join-Path $DataRoot \"data\"",
            runner,
            StringComparison.Ordinal);
        Assert.Contains(
            "$settingsPath = Join-Path $dataDirectory \"settings.json\"",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 5", runner, StringComparison.Ordinal);
        Assert.Contains(
            "hasResolvedInitialFileWidgetSetup = $true",
            runner,
            StringComparison.Ordinal);
        Assert.Contains("featureWidgetEnabledStates", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", runner, StringComparison.Ordinal);
        Assert.Contains("Refusing to clean an unowned picker/StorageItems", runner, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $resolvedRoot -Recurse -Force", runner, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void StageScope_DefersOlePhysicalDropAndKeepsRustAbiFrozen()
    {
        string combined =
            ReadRepositoryFile("src/DeskBox/App.AotPickerClipboardSmoke.cs") +
            ReadRepositoryFile("src/DeskBox/Services/AotPickerClipboardFixture.cs") +
            ReadRepositoryFile(
                "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotPickerClipboardSmoke.cs") +
            ReadRepositoryFile("scripts/run-aot-picker-clipboard-smoke.ps1");
        string rust = ReadRepositoryFile("native/deskbox-native/src/lib.rs");

        Assert.DoesNotContain("NativeDrop", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IDropTarget", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IFileOperation", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deskbox_native_", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard.SetContent", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard.GetContent", combined, StringComparison.Ordinal);
        Assert.Contains("assert_eq!(deskbox_native_capabilities(), 511);", rust, StringComparison.Ordinal);
        Assert.Equal(10, CountOccurrences(rust, "#[unsafe(no_mangle)]"));
    }

    [Fact]
    public void Audit_ProfileSchemaAndFrozenWarningGatesAdvanceTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1C1MissingRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1C1MissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1C1ForbiddenScopePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1C1RustAbiUnchanged", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1C1ExpectedWmc1510Count = 1241", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("no-global-clipboard-mutation matrix", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportAndRoadmap_RecordCompletedBoundaryAndNextPhysicalDropStage()
    {
        string report = ReadRepositoryFile(
            "docs/architecture/aot-stage-5b-4c1c1-report.md");
        string roadmap = ReadRepositoryFile(
            "docs/architecture/rust-native-aot-roadmap.md");

        Assert.Contains("5B-4C1C1 已完成", report, StringComparison.Ordinal);
        Assert.Contains("FileOpenPicker(WindowId)", report, StringComparison.Ordinal);
        Assert.Contains("全局剪贴板", report, StringComparison.Ordinal);
        Assert.Contains("证据边界", report, StringComparison.Ordinal);
        Assert.Contains("5B-4C1C2", report, StringComparison.Ordinal);
        Assert.Contains("5B-4C1C1", roadmap, StringComparison.Ordinal);
        Assert.Contains("profile 50 / schema 47", roadmap, StringComparison.Ordinal);
        Assert.Contains("5B-4C1C2", roadmap, StringComparison.Ordinal);
    }

    private static string SliceBetween(string value, string start, string end)
    {
        int startIndex = value.IndexOf(start, StringComparison.Ordinal);
        Assert.True(startIndex >= 0, $"Missing start marker '{start}'.");
        int endIndex = value.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(endIndex > startIndex, $"Missing end marker '{end}'.");
        return value[startIndex..endIndex];
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
