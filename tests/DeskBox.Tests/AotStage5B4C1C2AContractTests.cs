namespace DeskBox.Tests;

public sealed class AotStage5B4C1C2AContractTests
{
    [Fact]
    public void Scenario_IsNativeAotOnlyPhasedOwnedAndNormallyShutDown()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", shared, StringComparison.Ordinal);
        Assert.Contains("NativeDropPersistenceRestart", shared, StringComparison.Ordinal);
        Assert.Contains("native-drop-persistence-restart", shared, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_NATIVE_DROP_PHASE", shared, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiNativeDropWidgetId", shared, StringComparison.Ordinal);
        Assert.Contains("bool isNativeDrop", shared, StringComparison.Ordinal);
        Assert.Contains("? AotManagedUiNativeDropWidgetId", shared, StringComparison.Ordinal);
        Assert.Contains("CaptureAotManagedUiNativeDropAsync", shared, StringComparison.Ordinal);
        Assert.Contains("public AotManagedUiNativeDropEvidence? NativeDrop", shared, StringComparison.Ordinal);
        Assert.Contains("NormalShutdownRequested = true", shared, StringComparison.Ordinal);
        Assert.Contains("ShutdownApplicationAsync()", shared, StringComparison.Ordinal);
        string finalization = SliceBetween(
            shared,
            "        finally",
            "private async Task CaptureAotManagedUiTrayAndWidgetsAsync(");
        Assert.Contains("AotManagedUiNativeDropScenario)", finalization, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(shared, "JsonSerializer.Serialize("));
    }

    [Fact]
    public void ProductBridge_ObservesNativeEnterOverLeaveAndResolvesLiveDropIntent()
    {
        string bridge = ReadRepositoryFile(
            "src/DeskBox/Views/ContentWidgetWindow.NativeDragDrop.cs");
        string target = ReadRepositoryFile(
            "src/DeskBox/Helpers/NativeDropTarget.cs");
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");

        Assert.Contains("DragEnterEvent +=", bridge, StringComparison.Ordinal);
        Assert.Contains("DragOverEvent +=", bridge, StringComparison.Ordinal);
        Assert.Contains("DragLeaveEvent +=", bridge, StringComparison.Ordinal);
        Assert.Contains("ObserveNativeFileDragPointer", bridge, StringComparison.Ordinal);
        Assert.Contains("file.ClearDragSessionVisualState()", bridge, StringComparison.Ordinal);
        Assert.Contains("bool copyWhenMapped", bridge, StringComparison.Ordinal);
        Assert.Contains("NativeDropEffectPolicy.ResolveFeedbackEffect(", target, StringComparison.Ordinal);
        Assert.Contains("copyRequested", target, StringComparison.Ordinal);
        Assert.Contains("_defaultMoveProvider()", target, StringComparison.Ordinal);
        Assert.Contains("DropEvent?.Invoke(", target, StringComparison.Ordinal);
        Assert.Contains("bool? copyWhenMapped = null", surface, StringComparison.Ordinal);
        Assert.Contains("copyWhenMapped switch", surface, StringComparison.Ordinal);
        Assert.Contains("FileDropIntentPolicy.ResolveMappedTransfer(", surface, StringComparison.Ordinal);
        Assert.Contains("bool? moveWhenMapped = mapped", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void NativePointerFallback_ClearsStaleTargetsAndMaintainsExternalPreviewUsingRealScreenBounds()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml.cs");
        string method = SliceBetween(
            surface,
            "internal void ObserveNativeDragPointer(",
            "private static bool IsSameDragPayload(");

        Assert.Contains("HasActiveChildDropTargetVisual", method, StringComparison.Ordinal);
        Assert.Contains("IsScreenPointInsideElement(Root, screenX, screenY)", method, StringComparison.Ordinal);
        Assert.Contains("TransformToVisual(null)", method, StringComparison.Ordinal);
        Assert.Contains("RasterizationScale", method, StringComparison.Ordinal);
        Assert.Contains("ClearDragSessionVisualState();", method, StringComparison.Ordinal);
        Assert.Contains("ClearFolderDropTarget();", method, StringComparison.Ordinal);
        Assert.Contains("ClearStackMemberDropTarget();", method, StringComparison.Ordinal);
        Assert.Contains("IReadOnlyList<string>? pathHints = null", method, StringComparison.Ordinal);
        Assert.Contains("WidgetItem? nativeTarget = null", method, StringComparison.Ordinal);
        Assert.Contains("ApplyNativeFolderDropTarget(nativeTarget)", method, StringComparison.Ordinal);
        Assert.Contains("ApplyNativeStackDropTarget(nativeStack)", method, StringComparison.Ordinal);
        Assert.Contains("UpdateExternalDropPreview(", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SetFolderDropTarget", method, StringComparison.Ordinal);
        Assert.DoesNotContain("SetStackMemberDropTarget", method, StringComparison.Ordinal);
    }

    [Fact]
    public void AotProbe_UsesRegisteredGeneratedCcwAndNativeHdropVtable()
    {
        string probe = ReadRepositoryFile(
            "src/DeskBox/Views/ContentWidgetWindow.AotNativeDropSmoke.cs");

        Assert.Contains("{ IsRegistered: true }", probe, StringComparison.Ordinal);
        Assert.Contains("AcquireAotSmokeInterfacePointer()", probe, StringComparison.Ordinal);
        Assert.Contains("delegate* unmanaged[Stdcall]", probe, StringComparison.Ordinal);
        Assert.Contains("AotNativeHDropDataObject", probe, StringComparison.Ordinal);
        Assert.Contains("UnmanagedCallersOnly", probe, StringComparison.Ordinal);
        Assert.Contains("NativeFormatEtc*", probe, StringComparison.Ordinal);
        Assert.Contains("FileDropClipboardFormat = 15", probe, StringComparison.Ordinal);
        Assert.Contains("GlobalAlloc(", probe, StringComparison.Ordinal);
        Assert.Contains("Marshal.WriteInt32(locked, 16, 1)", probe, StringComparison.Ordinal);
        Assert.Contains("InvokeAotNativeDragLeaveCallback()", probe, StringComparison.Ordinal);
        Assert.DoesNotContain("Marshal.GetDelegateForFunctionPointer", probe, StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_RequiresExactScenarioPhaseRunIdAndPreviewRoot()
    {
        string fixture = ReadRepositoryFile(
            "src/DeskBox/Services/AotNativeDropFixture.cs");

        Assert.Contains("NativeDropPersistenceRestart", fixture, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_NATIVE_DROP_PHASE", fixture, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_NATIVE_DROP_RUN_ID", fixture, StringComparison.Ordinal);
        Assert.Contains("aot-5b4c1c2a-file", fixture, StringComparison.Ordinal);
        Assert.Contains("runId is not { Length: 32 }", fixture, StringComparison.Ordinal);
        Assert.Contains("dataPaths.IsDevelopmentRoot", fixture, StringComparison.Ordinal);
        Assert.Contains("configuredPreviewRoot", fixture, StringComparison.Ordinal);
        Assert.Contains("IsPathEqualOrInside(dataPaths.RootPath, fixtureRoot)", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCALAPPDATA", fixture, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProgressEvidence_RequiresTopLayerAcrylicAndReleasedOleCallback()
    {
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotNativeDropSmoke.cs");
        string probe = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotNativeDropSmoke.cs");
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.xaml");

        Assert.Contains("OleCallbackReleasedBeforeProgress", scenario, StringComparison.Ordinal);
        Assert.Contains("ProgressCardVisibleAboveDragVisual", scenario, StringComparison.Ordinal);
        Assert.Contains("CanvasZIndex >= 1000", scenario, StringComparison.Ordinal);
        Assert.Contains("TranslationZ >= 64", scenario, StringComparison.Ordinal);
        Assert.Contains("BackgroundIsAcrylicBrush", scenario, StringComparison.Ordinal);
        Assert.Contains("GetAotNativeFolderVisualState(", probe, StringComparison.Ordinal);
        Assert.Contains("thickness.Left >= 0.5", probe, StringComparison.Ordinal);
        Assert.Contains("borderBrush.Color.A > 0", probe, StringComparison.Ordinal);
        Assert.Contains("Canvas.GetZIndex(ImportProgressCard)", probe, StringComparison.Ordinal);
        Assert.Contains("background is AcrylicBrush", probe, StringComparison.Ordinal);
        Assert.Contains("Canvas.ZIndex=\"1000\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Translation=\"0,0,64\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SystemControlAcrylicElementBrush", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void Scenario_ProvesPointerLeaveCopyMoveRestartAndMarksPhysicalBoundary()
    {
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotNativeDropSmoke.cs");

        Assert.Contains("ProgrammaticGeneratedCcwHDrop", scenario, StringComparison.Ordinal);
        Assert.Contains("PhysicalExplorerMouseVerified = false", scenario, StringComparison.Ordinal);
        Assert.Contains("NativeDropScreenPointClearedStaleFolderHighlight", scenario, StringComparison.Ordinal);
        Assert.Contains("NativeDropLeaveClearedFolderHighlight", scenario, StringComparison.Ordinal);
        Assert.Contains("NativeDropCopyMoveSemanticsVerified", scenario, StringComparison.Ordinal);
        Assert.Contains("NativeDropRestartMutationVerified", scenario, StringComparison.Ordinal);
        Assert.Contains("NativeDropPostflightVerified", scenario, StringComparison.Ordinal);
        Assert.Contains("SHA256.HashData(stream)", scenario, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", scenario, StringComparison.Ordinal);
        Assert.DoesNotContain("mouse_event", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void Runner_DrivesThreeProcessesLargeFileHashesAndSafeCleanup()
    {
        string master = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");
        string runner = ReadRepositoryFile("scripts/run-aot-native-drop-smoke.ps1");

        Assert.Contains("NativeDropPersistenceRestart", master, StringComparison.Ordinal);
        Assert.Contains("run-aot-native-drop-smoke.ps1", master, StringComparison.Ordinal);
        Assert.Contains("$largeFileLength = 384MB", runner, StringComparison.Ordinal);
        Assert.Contains("Invoke-NativeDropPhase", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"Mutate\"", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"VerifyRestore\"", runner, StringComparison.Ordinal);
        Assert.Contains("-Phase \"Postflight\"", runner, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", runner, StringComparison.Ordinal);
        Assert.Contains("Refusing to clean an unowned native-drop root", runner, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $resolvedRoot -Recurse -Force", runner, StringComparison.Ordinal);
        Assert.Contains("Wait-NaturalPreviewExit", runner, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", runner, StringComparison.Ordinal);
        Assert.Contains("profile 56 / schema 53", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditProfile_AdvancesAndFreezesNewScopeWithoutRustExpansion()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");
        string rust = ReadRepositoryFile("native/deskbox-native/src/lib.rs");

        Assert.Contains("$auditProfileVersion = 62", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1C2ARequiredProductPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1C2AMissingSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1C2AForbiddenScopePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1C2ARustAbiUnchanged", audit, StringComparison.Ordinal);
        Assert.Contains("$sourceFile -eq $stage5B4C1ASourceFiles[4]", audit, StringComparison.Ordinal);
        Assert.Contains("$pattern -eq 'NativeDrop'", audit, StringComparison.Ordinal);
        Assert.Contains("C1C2A applies its own narrow gate", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 62", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("assert_eq!(deskbox_native_capabilities(), 1023);", rust, StringComparison.Ordinal);
        Assert.Equal(10, CountOccurrences(rust, "#[unsafe(no_mangle)]"));
    }

    [Fact]
    public void StageScope_KeepsPhysicalExplorerAndGlobalClipboardDeferred()
    {
        string combined =
            ReadRepositoryFile("src/DeskBox/App.AotNativeDropSmoke.cs") +
            ReadRepositoryFile("src/DeskBox/Services/AotNativeDropFixture.cs") +
            ReadRepositoryFile("src/DeskBox/Views/ContentWidgetWindow.AotNativeDropSmoke.cs") +
            ReadRepositoryFile("scripts/run-aot-native-drop-smoke.ps1");

        Assert.DoesNotContain("Clipboard.SetContent", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Clipboard.GetContent", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SendInput", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("mouse_event", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("deskbox_native_", combined, StringComparison.Ordinal);
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
