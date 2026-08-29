namespace DeskBox.Tests;

public sealed class AotStage5B4B2B2AContractTests
{
    [Fact]
    public void TodoScenario_IsNativeAotOnlyPhaseBoundAndPreviewRootOnly()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string persistence = ReadRepositoryFile(
            "src/DeskBox/App.AotTodoPersistenceSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains("TodoPersistenceRestart", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_TODO_PHASE", source, StringComparison.Ordinal);
        Assert.Contains("Mutate", source, StringComparison.Ordinal);
        Assert.Contains("VerifyDelete", source, StringComparison.Ordinal);
        Assert.Contains("Postflight", source, StringComparison.Ordinal);
        Assert.Contains("todo-persistence-restart", source, StringComparison.Ordinal);
        Assert.Contains("DeskBoxDataPathService.Current", persistence, StringComparison.Ordinal);
        Assert.Contains("IsDevelopmentRoot", source, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceScenario_UsesRealDetailCreateAndTitleSavePaths()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotPersistenceSmoke.cs");

        Assert.Contains("OpenAddEditorAsync", surface, StringComparison.Ordinal);
        Assert.Contains("DetailTitleTextBox.Text", surface, StringComparison.Ordinal);
        Assert.Contains("ViewModel.FinalizeDetailAsync", surface, StringComparison.Ordinal);
        Assert.Contains("SaveDetailEditorsAsync", surface, StringComparison.Ordinal);
        Assert.Contains("AotTodoInitialTitle", surface, StringComparison.Ordinal);
        Assert.Contains("AotTodoPersistedTitle", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("new TodoItem", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("TodoWidgetStore.SaveAsync", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoCoreDataContexts_UseNativeAotGeneratedBindableProviders()
    {
        string bindable = ReadRepositoryFile(
            "src/DeskBox/ViewModels/TodoViewModels.AotBindableProperties.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", bindable, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(
            bindable,
            "[WinRT.GeneratedBindableCustomProperty]"));
        Assert.Contains("partial class TodoWidgetViewModel", bindable, StringComparison.Ordinal);
        Assert.Contains("partial class TodoItemViewModel", bindable, StringComparison.Ordinal);
        Assert.Contains("partial class TodoStepViewModel", bindable, StringComparison.Ordinal);
        Assert.DoesNotContain("TodoAttachmentViewModel", bindable, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoListItemsSource_ProjectsTypedCollectionThroughObjectArrayAndRefreshes()
    {
        string viewModel = ReadRepositoryFile(
            "src/DeskBox/ViewModels/TodoWidgetViewModel.cs");
        string filtering = ReadRepositoryFile(
            "src/DeskBox/ViewModels/TodoWidgetViewModel.FilteringAndAppearance.cs");
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml");

        Assert.Contains(
            "public object[] VisibleItemsSource",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "VisibleItems.Cast<object>().ToArray()",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnPropertyChanged(nameof(VisibleItemsSource))",
            filtering,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding VisibleItemsSource}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ItemsSource=\"{Binding VisibleItems}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceScenario_ExercisesRealSixHundredMillisecondNotesAutoSave()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotPersistenceSmoke.cs");
        string product = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DetailNotesAndSteps.cs");

        Assert.Contains("Interval = TimeSpan.FromMilliseconds(600)", product, StringComparison.Ordinal);
        Assert.Contains("BeginNotesEditingAsync", surface, StringComparison.Ordinal);
        Assert.Contains("DetailNotesEditor.Text", surface, StringComparison.Ordinal);
        Assert.Contains("ScheduleNotesAutoSave", surface, StringComparison.Ordinal);
        Assert.Contains("private void ScheduleNotesAutoSave()", product, StringComparison.Ordinal);
        Assert.Contains("_notesAutosaveTimer.IsEnabled", surface, StringComparison.Ordinal);
        Assert.Contains("_notesOriginalText", surface, StringComparison.Ordinal);
        Assert.Contains("_notesSaveGate.CurrentCount", surface, StringComparison.Ordinal);
        Assert.Contains("WaitForAotTodoAutoSaveAsync", surface, StringComparison.Ordinal);
        Assert.Contains("AutoSaveObserved", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("DetailNotesEditor.SourceTextBox.Text", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("NotesAutosaveTimer_Tick(", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceScenario_UsesExplicitNotesCompletionAndProductDeletePaths()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotPersistenceSmoke.cs");

        Assert.Contains("SaveActiveNotesAsync(keepEditing: false)", surface, StringComparison.Ordinal);
        Assert.Contains("SetCompletedWithFeedbackAsync", surface, StringComparison.Ordinal);
        Assert.Contains("DeleteItemAsync", surface, StringComparison.Ordinal);
        Assert.Contains("OpenDetailItemAsync", surface, StringComparison.Ordinal);
        Assert.Contains("ExplicitNotesSaved", surface, StringComparison.Ordinal);
        Assert.Contains("CompletionRoundTripObserved", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("File.Delete", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("TodoWidgetStore", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerScenario_UsesOnlyFixedOwnedTodoSurfaceAndLiveHost()
    {
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotTodoPersistenceSmoke.cs");

        Assert.Contains("aot-5b4b2b2a-todo", manager, StringComparison.Ordinal);
        Assert.Contains("_contentWidgets.TryGetValue", manager, StringComparison.Ordinal);
        Assert.Contains("window.ContentReadyTask", manager, StringComparison.Ordinal);
        Assert.Contains("window.CurrentContent is TodoWidgetContentAdapter", manager, StringComparison.Ordinal);
        Assert.Contains("adapter.View is TodoWidgetContent", manager, StringComparison.Ordinal);
        Assert.Contains("WindowHandle", manager, StringComparison.Ordinal);
        Assert.Contains("WindowContentRoot?.XamlRoot", manager, StringComparison.Ordinal);
        Assert.Contains("Visible", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_RecordsStoreUiDetailNotesAndCoreTaskState()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotTodoPersistenceSmoke.cs");

        Assert.Contains("AotManagedUiTodoPersistenceEvidence", source, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiTodoStateEvidence", source, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiTodoItemEvidence", source, StringComparison.Ordinal);
        Assert.Contains("Before", source, StringComparison.Ordinal);
        Assert.Contains("AfterExplicitSave", source, StringComparison.Ordinal);
        Assert.Contains("After", source, StringComparison.Ordinal);
        Assert.Contains("StoreFileExists", source, StringComparison.Ordinal);
        Assert.Contains("SurfaceItemCount", source, StringComparison.Ordinal);
        Assert.Contains("DetailItemId", source, StringComparison.Ordinal);
        Assert.Contains("Notes", source, StringComparison.Ordinal);
        Assert.Contains("IsCompleted", source, StringComparison.Ordinal);
        Assert.Contains("StepCount", source, StringComparison.Ordinal);
        Assert.Contains("AttachmentCount", source, StringComparison.Ordinal);
        Assert.Contains("HasDueDate", source, StringComparison.Ordinal);
        Assert.Contains("HasRecurrence", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoScenario_ReusesSingleSourceGeneratedResultWriter()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Equal(1, CountOccurrences(source, "JsonSerializer.Serialize("));
        Assert.Contains(
            "AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public AotManagedUiTodoPersistenceEvidence? TodoPersistence",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Deserialize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_ExecutesThreeFreshTodoProcessesAndArchivesSession()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("TodoPersistenceRestart", script, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_TODO_PHASE", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-TodoPersistencePhase", script, StringComparison.Ordinal);
        Assert.Contains("Mutate", script, StringComparison.Ordinal);
        Assert.Contains("VerifyDelete", script, StringComparison.Ordinal);
        Assert.Contains("Postflight", script, StringComparison.Ordinal);
        Assert.Contains("Wait-NaturalPreviewExit", script, StringComparison.Ordinal);
        Assert.Contains("todoNaturalExit", script, StringComparison.Ordinal);
        Assert.Contains("archivedTodoSessionPath", script, StringComparison.Ordinal);
        Assert.Contains("sessionPath = $archivedTodoSessionPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_ComparesReloadExplicitSaveDeleteAndPostflight()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("Assert-TodoStateEqual", script, StringComparison.Ordinal);
        Assert.Contains("mutate.todoPersistence.after", script, StringComparison.Ordinal);
        Assert.Contains("verifyDelete.todoPersistence.before", script, StringComparison.Ordinal);
        Assert.Contains("verifyDelete.todoPersistence.after", script, StringComparison.Ordinal);
        Assert.Contains("postflight.todoPersistence.before", script, StringComparison.Ordinal);
        Assert.Contains("afterExplicitSave", script, StringComparison.Ordinal);
        Assert.Contains("final-todo.json", script, StringComparison.Ordinal);
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
        Assert.Contains("todoPreviewProcessesAfter", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", script, StringComparison.Ordinal);
        Assert.Contains(".deskbox-aot-managed-ui-owned.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoScenario_DoesNotEnterDeferredStepsAttachmentsReminderRecurrenceOrOsMatrices()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotTodoPersistenceSmoke.cs");
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotTodoPersistenceSmoke.cs");
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotPersistenceSmoke.cs");
        string combined = source + manager + surface;

        Assert.DoesNotContain("AddStepAsync", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SetStepCompletedAsync", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("AddAttachmentAsync", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteAttachmentAsync", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SetDueDate", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRecurrence", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("GlanceWidgetStore", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("WeatherService", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FileOpenPicker", combined, StringComparison.Ordinal);
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
    public void Stage5B4B2B2A_ProfileSchemaProjectAndLauncherAdvanceTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2A", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("Todo", project, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditFreezesTodoSourcesPhasesUiStoreScopeAndWarnings()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("stage5B4B2B2ASourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2ARequiredRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2ARequiredSurfacePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2ARequiredManagerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2ARequiredSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2AForbiddenScopePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2AJsonSerializeCallCount", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2ASourceWarningMessages", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2AExpectedWmc1510Count = 1241", audit, StringComparison.Ordinal);
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
