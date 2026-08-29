namespace DeskBox.Tests;

public sealed class AotStage5B4B2B1ContractTests
{
    [Fact]
    public void QuickCaptureScenario_IsNativeAotOnlyPhaseBoundAndPreviewRootOnly()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string persistence = ReadRepositoryFile(
            "src/DeskBox/App.AotQuickCapturePersistenceSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains("QuickCapturePersistenceRestart", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_QUICK_CAPTURE_PHASE", source, StringComparison.Ordinal);
        Assert.Contains("Mutate", source, StringComparison.Ordinal);
        Assert.Contains("VerifyDelete", source, StringComparison.Ordinal);
        Assert.Contains("Postflight", source, StringComparison.Ordinal);
        Assert.Contains("quick-capture-persistence-restart", source, StringComparison.Ordinal);
        Assert.Contains("DeskBoxDataPathService.Current", persistence, StringComparison.Ordinal);
        Assert.Contains("IsDevelopmentRoot", source, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeBindingSurfaces_UseNativeAotProvidersAndObjectArrayItemsSource()
    {
        string bindable = ReadRepositoryFile(
            "src/DeskBox/ViewModels/QuickCaptureViewModels.AotBindableProperties.cs");
        string viewModel = ReadRepositoryFile(
            "src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.cs");
        string itemSync = ReadRepositoryFile(
            "src/DeskBox/ViewModels/QuickCaptureWidgetViewModel.ItemSync.cs");
        string window = ReadRepositoryFile(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.xaml");
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", bindable, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(
            bindable,
            "[WinRT.GeneratedBindableCustomProperty]"));
        Assert.Contains(
            "public sealed partial class QuickCaptureWidgetViewModel",
            bindable,
            StringComparison.Ordinal);
        Assert.Contains(
            "public sealed partial class QuickCaptureItemViewModel",
            bindable,
            StringComparison.Ordinal);
        Assert.Contains(
            "public object[] VisibleItemsSource",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "Items.Cast<object>().ToArray()",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnPropertyChanged(nameof(VisibleItemsSource))",
            itemSync,
            StringComparison.Ordinal);

        foreach (string xaml in new[] { window, surface })
        {
            Assert.Contains(
                "ItemsSource=\"{Binding VisibleItemsSource}\"",
                xaml,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "ItemsSource=\"{Binding Items}\"",
                xaml,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SurfaceScenario_UsesMeaningfulDraftAndExplicitPendingSaveFlush()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.AotPersistenceSmoke.cs");

        Assert.Contains("OpenNewDetailAsync", surface, StringComparison.Ordinal);
        Assert.Contains("SetDetailEditorText", surface, StringComparison.Ordinal);
        Assert.Contains("MarkDetailDirty", surface, StringComparison.Ordinal);
        Assert.Contains("HasNewDetailContent", surface, StringComparison.Ordinal);
        Assert.Contains("FlushPendingDetailSaveAsync", surface, StringComparison.Ordinal);
        Assert.Contains("_detailHasUnsavedChanges", surface, StringComparison.Ordinal);
        Assert.Contains("PendingSaveFlushed", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceScenario_ExercisesTheRealSixHundredMillisecondAutoSaveTimer()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.AotPersistenceSmoke.cs");
        string product = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs");

        Assert.Contains("DetailAutoSaveDelayMs = 600", product, StringComparison.Ordinal);
        Assert.Contains("ScheduleDetailAutoSave", surface, StringComparison.Ordinal);
        Assert.Contains("_detailEditRevision", surface, StringComparison.Ordinal);
        Assert.Contains("_detailSavedRevision", surface, StringComparison.Ordinal);
        Assert.Contains("WaitForAotQuickCaptureAutoSaveAsync", surface, StringComparison.Ordinal);
        Assert.Contains("AutoSaveObserved", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailAutoSaveTimer_Tick(", surface, StringComparison.Ordinal);
        Assert.Contains(
            "attachments.Cast<object>().ToArray()",
            product,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceScenario_UsesManagedAttachmentAndProductDeletePaths()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.AotPersistenceSmoke.cs");

        Assert.Contains("ViewModel.AddAttachmentsAsync", surface, StringComparison.Ordinal);
        Assert.Contains("ForceManagedCopy: true", surface, StringComparison.Ordinal);
        Assert.Contains("ViewModel.DeleteAttachmentAsync", surface, StringComparison.Ordinal);
        Assert.Contains("DeleteQuickCaptureItemAsync", surface, StringComparison.Ordinal);
        Assert.Contains("File.Exists", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("QuickCaptureStore", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerScenario_UsesOnlyTheFixedOwnedQuickCaptureSurfaceAndLiveHost()
    {
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotQuickCapturePersistenceSmoke.cs");

        Assert.Contains("aot-5b4b2b1-quick-capture", manager, StringComparison.Ordinal);
        Assert.Contains("_contentWidgets.TryGetValue", manager, StringComparison.Ordinal);
        Assert.Contains("window.CurrentContent is QuickCaptureSurfaceContent", manager, StringComparison.Ordinal);
        Assert.Contains("WindowHandle", manager, StringComparison.Ordinal);
        Assert.Contains("WindowContentRoot?.XamlRoot", manager, StringComparison.Ordinal);
        Assert.Contains("Visible", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_RecordsStoreUiDetailAndManagedAttachmentState()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotQuickCapturePersistenceSmoke.cs");

        Assert.Contains("AotManagedUiQuickCapturePersistenceEvidence", source, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiQuickCaptureStateEvidence", source, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiQuickCaptureItemEvidence", source, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiQuickCaptureAttachmentEvidence", source, StringComparison.Ordinal);
        Assert.Contains("Before", source, StringComparison.Ordinal);
        Assert.Contains("AfterExplicitFlush", source, StringComparison.Ordinal);
        Assert.Contains("AfterAttachmentDelete", source, StringComparison.Ordinal);
        Assert.Contains("After", source, StringComparison.Ordinal);
        Assert.Contains("ManagedAttachmentFileCount", source, StringComparison.Ordinal);
        Assert.Contains("SurfaceItemCount", source, StringComparison.Ordinal);
        Assert.Contains("DetailItemId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCaptureScenario_ReusesTheSingleSourceGeneratedResultWriter()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Equal(1, CountOccurrences(source, "JsonSerializer.Serialize("));
        Assert.Contains(
            "AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public AotManagedUiQuickCapturePersistenceEvidence? QuickCapturePersistence",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Deserialize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_ExecutesThreeFreshQuickCaptureProcessesAndArchivesTheirSession()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("QuickCapturePersistenceRestart", script, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_QUICK_CAPTURE_PHASE", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-QuickCapturePersistencePhase", script, StringComparison.Ordinal);
        Assert.Contains("Mutate", script, StringComparison.Ordinal);
        Assert.Contains("VerifyDelete", script, StringComparison.Ordinal);
        Assert.Contains("Postflight", script, StringComparison.Ordinal);
        Assert.Contains("Wait-NaturalPreviewExit", script, StringComparison.Ordinal);
        Assert.Contains("quickCaptureNaturalExit", script, StringComparison.Ordinal);
        Assert.Contains("archivedQuickCaptureSessionPath", script, StringComparison.Ordinal);
        Assert.Contains("sessionPath = $archivedQuickCaptureSessionPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_ComparesReloadDeletePostflightAndFixtureCleanup()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("Assert-QuickCaptureStateEqual", script, StringComparison.Ordinal);
        Assert.Contains("mutate.quickCapturePersistence.after", script, StringComparison.Ordinal);
        Assert.Contains("verifyDelete.quickCapturePersistence.before", script, StringComparison.Ordinal);
        Assert.Contains("verifyDelete.quickCapturePersistence.after", script, StringComparison.Ordinal);
        Assert.Contains("postflight.quickCapturePersistence.before", script, StringComparison.Ordinal);
        Assert.Contains("managedAttachmentRelativePaths", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileSha256", script, StringComparison.Ordinal);
        Assert.Contains("quick-capture-attachment.txt", script, StringComparison.Ordinal);
        Assert.Contains("previewRootCleaned", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_KeepsProductionFingerprintRuntimeLogAndExactProcessGates()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintAfter", script, StringComparison.Ordinal);
        Assert.Contains("runtimeFailureLogLines", script, StringComparison.Ordinal);
        Assert.Contains("Unhandled exception:", script, StringComparison.Ordinal);
        Assert.Contains("quickCapturePreviewProcessesAfter", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", script, StringComparison.Ordinal);
        Assert.Contains(".deskbox-aot-managed-ui-owned.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickCaptureScenario_DoesNotEnterDeferredTodoGlanceWeatherOrOsMatrices()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotQuickCapturePersistenceSmoke.cs");
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotQuickCapturePersistenceSmoke.cs");
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.AotPersistenceSmoke.cs");
        string combined = source + manager + surface;

        Assert.DoesNotContain("TodoWidgetStore", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("GlanceWidgetStore", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("WeatherService", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FileOpenPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("ShortcutHelper", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Launcher.Launch", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateWidget", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveWidget", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonInventory_RemainsAtTwentyFiveFilesSixtyCallsAndTwentyThreeContexts()
    {
        string baseline = ReadRepositoryFile(
            "tests/DeskBox.Tests/JsonSerializationBaselineContractTests.cs");

        Assert.Contains("Assert.Equal(28, actual.Count);", baseline, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(64, actual.Values.Sum());", baseline, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(26, actualContextOwners.Length);", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage5B4B2B1_ProfileSchemaProjectAndLauncherAdvanceTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B1", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("Quick Capture", project, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditFreezesQuickCaptureSourcesPhasesUiStoreCleanupAndWarnings()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("stage5B4B2B1SourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B1RequiredRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B1RequiredSurfacePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B1RequiredManagerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B1RequiredSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B1ForbiddenScopePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B1JsonSerializeCallCount", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B1SourceWarningMessages", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B1ExpectedWmc1510Count = 1241", audit, StringComparison.Ordinal);
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
