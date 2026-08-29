namespace DeskBox.Tests;

public sealed class AotStage5B4C1B2BContractTests
{
    [Fact]
    public void Scenario_IsNativeAotOnlyReadOnlyOwnedAndGenerated()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string scenario = ReadRepositoryFile("src/DeskBox/App.AotFilePropertiesSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", shared, StringComparison.Ordinal);
        Assert.Contains("#if DESKBOX_NATIVE_AOT", scenario, StringComparison.Ordinal);
        Assert.Contains("FilePropertiesReadOnly", shared, StringComparison.Ordinal);
        Assert.Contains("file-properties-read-only", shared, StringComparison.Ordinal);
        Assert.Contains("aot-5b4c1b2b-file", shared, StringComparison.Ordinal);
        Assert.Contains("CaptureAotManagedUiFilePropertiesAsync", shared, StringComparison.Ordinal);
        Assert.Contains("public AotManagedUiFilePropertiesEvidence? FileProperties", shared, StringComparison.Ordinal);
        Assert.Contains("NormalShutdownRequested = true", shared, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(shared, "JsonSerializer.Serialize("));
    }

    [Fact]
    public void ProductPath_PassesHostHwndDirectlyWithoutForegroundGuessing()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.SelectionAndMenus.cs");
        string method = SliceBetween(
            surface,
            "private void ShowFileProperties(WidgetItem item)",
            "private MenuFlyout CreateContentAreaFlyout()");

        Assert.Contains("ShellContextMenuHelper.ShowProperties(", method, StringComparison.Ordinal);
        Assert.Contains("_hostWindowHandle,", method, StringComparison.Ordinal);
        Assert.Contains("item.Path", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetForegroundWindow", method, StringComparison.Ordinal);
        Assert.DoesNotContain("GetAncestor", method, StringComparison.Ordinal);
    }

    [Fact]
    public void ShellHelper_UsesRealShObjectPropertiesAndTracksExactAotCall()
    {
        string helper = ReadRepositoryFile("src/DeskBox/Helpers/ShellContextMenuHelper.cs");

        Assert.Contains("SHObjectProperties(", helper, StringComparison.Ordinal);
        Assert.Contains("SHOP_FILEPATH", helper, StringComparison.Ordinal);
        Assert.Contains("AotFilePropertiesFixture.TryBeginInvocation", helper, StringComparison.Ordinal);
        Assert.Contains("AotFilePropertiesFixture.RecordInvocationResult", helper, StringComparison.Ordinal);
        Assert.Contains("bool invoked = SHObjectProperties", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("TaskDialog", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_RequiresExactScenarioRunPreviewRootTargetAndOwner()
    {
        string fixture = ReadRepositoryFile(
            "src/DeskBox/Services/AotFilePropertiesFixture.cs");

        Assert.Contains("FilePropertiesReadOnly", fixture, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_FILE_PROPERTIES_RUN_ID", fixture, StringComparison.Ordinal);
        Assert.Contains("aot-5b4c1b2b-file", fixture, StringComparison.Ordinal);
        Assert.Contains("value is { Length: 32 }", fixture, StringComparison.Ordinal);
        Assert.Contains("character is >= '0' and <= '9'", fixture, StringComparison.Ordinal);
        Assert.Contains(">= 'a' and <= 'f'", fixture, StringComparison.Ordinal);
        Assert.Contains("dataPaths.IsDevelopmentRoot", fixture, StringComparison.Ordinal);
        Assert.Contains("configuredPreviewRoot", fixture, StringComparison.Ordinal);
        Assert.Contains("ownerWindowHandle == IntPtr.Zero", fixture, StringComparison.Ordinal);
        Assert.Contains("!PathsEqual(targetPath, paths.TargetPath)", fixture, StringComparison.Ordinal);
        Assert.Contains("permits exactly one product invocation", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCALAPPDATA", fixture, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DialogObserver_UsesUniqueTitleOwnerChainAndControlledClose()
    {
        string fixture = ReadRepositoryFile(
            "src/DeskBox/Services/AotFilePropertiesFixture.cs");
        string win32 = ReadRepositoryFile("src/DeskBox/Helpers/Win32Helper.cs");

        Assert.Contains("CaptureVisibleTopLevelWindowHandles", fixture, StringComparison.Ordinal);
        Assert.Contains("ObserveAndCloseOwnedDialogAsync", fixture, StringComparison.Ordinal);
        Assert.Contains("baselineWindowHandles.Contains", fixture, StringComparison.Ordinal);
        Assert.Contains("title.Contains(targetName", fixture, StringComparison.Ordinal);
        Assert.Contains("\"#32770\"", fixture, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.GW_OWNER", fixture, StringComparison.Ordinal);
        Assert.Contains("Win32Helper.GA_ROOTOWNER", fixture, StringComparison.Ordinal);
        Assert.Contains("CaptureObservedWindow", fixture, StringComparison.Ordinal);
        Assert.Contains("AotFilePropertiesObservedWindowSnapshot", fixture, StringComparison.Ordinal);
        Assert.Contains("WmClose", fixture, StringComparison.Ordinal);
        Assert.Contains("WindowDestroyedAfterClose", fixture, StringComparison.Ordinal);
        Assert.Contains("public const uint GW_OWNER = 4", win32, StringComparison.Ordinal);
    }

    [Fact]
    public void MenuProbe_UsesRealSingleItemMenuAutomationAndBackgroundObserver()
    {
        string probe = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotFilePropertiesSmoke.cs");

        Assert.Contains("CreateItemFlyout(target)", probe, StringComparison.Ordinal);
        Assert.Contains("Common.Properties", probe, StringComparison.Ordinal);
        Assert.Contains("_hostWindowHandle == IntPtr.Zero", probe, StringComparison.Ordinal);
        Assert.Contains("MenuFlyoutItemAutomationPeer", probe, StringComparison.Ordinal);
        Assert.Contains("PatternInterface.Invoke", probe, StringComparison.Ordinal);
        Assert.Contains("IInvokeProvider", probe, StringComparison.Ordinal);
        Assert.Contains("Task.Run(() =>", probe, StringComparison.Ordinal);
        Assert.Contains("ObserveAndCloseOwnedDialogAsync", probe, StringComparison.Ordinal);
        Assert.Contains("invokeProvider.Invoke()", probe, StringComparison.Ordinal);
        Assert.Contains("WaitForInvocationResultAsync", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("ShowFileProperties(target)", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void AppEvidence_ProvesSurfaceMenuInvocationOwnerDialogCloseAndHash()
    {
        string scenario = ReadRepositoryFile("src/DeskBox/App.AotFilePropertiesSmoke.cs");

        Assert.Contains("WaitForAotLocalFileSurfaceAsync", scenario, StringComparison.Ordinal);
        Assert.Contains("InvokeAotFilePropertiesAsync", scenario, StringComparison.Ordinal);
        Assert.Contains("FilePropertiesOwnedBaselineVerified", scenario, StringComparison.Ordinal);
        Assert.Contains("FilePropertiesMenuInvoked", scenario, StringComparison.Ordinal);
        Assert.Contains("FilePropertiesInvocationVerified", scenario, StringComparison.Ordinal);
        Assert.Contains("menu.Invocation.OwnerWindowHandle == host.WindowHandle", scenario, StringComparison.Ordinal);
        Assert.Contains("menu.Dialog.ExpectedOwner.IsWindow", scenario, StringComparison.Ordinal);
        Assert.Contains("menu.Dialog.DirectOwner.IsWindow", scenario, StringComparison.Ordinal);
        Assert.Contains("menu.Dialog.RootOwner.IsWindow", scenario, StringComparison.Ordinal);
        Assert.Contains("FilePropertiesDialogObserved", scenario, StringComparison.Ordinal);
        Assert.Contains("FilePropertiesDialogClosed", scenario, StringComparison.Ordinal);
        Assert.Contains("RemainingMatchingDialogCount == 0", scenario, StringComparison.Ordinal);
        Assert.Contains("FilePropertiesPostflightVerified", scenario, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData(stream)", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_UsesFreshOwnedTargetStableAuditNaturalExitAndFingerprint()
    {
        string master = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");
        string runner = ReadRepositoryFile("scripts/run-aot-file-properties-smoke.ps1");

        Assert.Contains("FilePropertiesReadOnly", master, StringComparison.Ordinal);
        Assert.Contains("run-aot-file-properties-smoke.ps1", master, StringComparison.Ordinal);
        Assert.Contains("[Guid]::NewGuid().ToString(\"N\")", runner, StringComparison.Ordinal);
        Assert.Contains("file-properties-preview-$runId", runner, StringComparison.Ordinal);
        Assert.Contains("profile 49 / schema 46", runner, StringComparison.Ordinal);
        Assert.Contains("Refusing to replace an existing file Properties preview root", runner, StringComparison.Ordinal);
        Assert.Contains("Refusing to replace an existing file Properties recovery root", runner, StringComparison.Ordinal);
        Assert.Contains("properties-$runId.txt", runner, StringComparison.Ordinal);
        Assert.Contains("-AllowEarlyExit", runner, StringComparison.Ordinal);
        Assert.Contains("Wait-NaturalPreviewExit", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", runner, StringComparison.Ordinal);
        Assert.Contains("targetSha256Before", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_ValidatesDialogOwnerCloseLogsArchiveAndSafeCleanup()
    {
        string runner = ReadRepositoryFile("scripts/run-aot-file-properties-smoke.ps1");

        Assert.Contains("directOwnerWindowHandle", runner, StringComparison.Ordinal);
        Assert.Contains("rootOwnerWindowHandle", runner, StringComparison.Ordinal);
        Assert.Contains("windowDestroyedAfterClose", runner, StringComparison.Ordinal);
        Assert.Contains("FilePropertiesDialogObserved", runner, StringComparison.Ordinal);
        Assert.Contains("runtimeFailureLogLines", runner, StringComparison.Ordinal);
        Assert.Contains("fixture-state.json", runner, StringComparison.Ordinal);
        Assert.Contains("file-properties-session.json", runner, StringComparison.Ordinal);
        Assert.Contains("Refusing to clean an unowned file Properties preview root", runner, StringComparison.Ordinal);
        Assert.Contains("ownedRecoveryRootCleaned", runner, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $resolvedRoot -Recurse -Force", runner, StringComparison.Ordinal);
        Assert.Contains("The exact owned preview/recovery", runner, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void StageScope_KeepsRustAbiAndDefersPickerPhysicalDragAndShellMutation()
    {
        string combined =
            ReadRepositoryFile("src/DeskBox/App.AotFilePropertiesSmoke.cs") +
            ReadRepositoryFile("src/DeskBox/Services/AotFilePropertiesFixture.cs") +
            ReadRepositoryFile(
                "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotFilePropertiesSmoke.cs") +
            ReadRepositoryFile("scripts/run-aot-file-properties-smoke.ps1");
        string rust = ReadRepositoryFile("native/deskbox-native/src/lib.rs");

        Assert.DoesNotContain("FileOpenPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeDrop", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("IFileOperation", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SHFileOperation", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("deskbox_native_", combined, StringComparison.Ordinal);
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
        Assert.Contains("stage5B4C1B2BMissingRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1B2BForbiddenScopePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1B2BRustAbiUnchanged", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1B2BExpectedWmc1510Count = 1241", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("actual SHObjectProperties dialog", project, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportAndRoadmap_RecordCompletedBoundaryAndNextPickerStage()
    {
        string report = ReadRepositoryFile(
            "docs/architecture/aot-stage-5b-4c1b2b-report.md");
        string roadmap = ReadRepositoryFile(
            "docs/architecture/rust-native-aot-roadmap.md");

        Assert.Contains("5B-4C1B2B 已完成", report, StringComparison.Ordinal);
        Assert.Contains("SHObjectProperties", report, StringComparison.Ordinal);
        Assert.Contains("WM_CLOSE", report, StringComparison.Ordinal);
        Assert.Contains("证据边界", report, StringComparison.Ordinal);
        Assert.Contains("5B-4C1C", report, StringComparison.Ordinal);
        Assert.Contains("5B-4C1B2B", roadmap, StringComparison.Ordinal);
        Assert.Contains("profile 49 / schema 46", roadmap, StringComparison.Ordinal);
        Assert.Contains("5B-4C1C", roadmap, StringComparison.Ordinal);
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
