namespace DeskBox.Tests;

public sealed class AotStage5B4B2B2B1ContractTests
{
    [Fact]
    public void TodoStepsScenario_IsNativeAotOnlyPhaseBoundAndPreviewRootOnly()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string persistence = ReadRepositoryFile(
            "src/DeskBox/App.AotTodoStepsPersistenceSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains("TodoStepsPersistenceRestart", source, StringComparison.Ordinal);
        Assert.Contains(
            "DESKBOX_AOT_MANAGED_UI_TODO_STEPS_PHASE",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Mutate", source, StringComparison.Ordinal);
        Assert.Contains("VerifyDelete", source, StringComparison.Ordinal);
        Assert.Contains("Postflight", source, StringComparison.Ordinal);
        Assert.Contains("todo-steps-persistence-restart", source, StringComparison.Ordinal);
        Assert.Contains("DeskBoxDataPathService.Current", persistence, StringComparison.Ordinal);
        Assert.Contains("IsDevelopmentRoot", persistence, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceScenario_AddsStepThroughRealDetailInputAndProductPath()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotStepsPersistenceSmoke.cs");
        string product = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DetailNotesAndSteps.cs");
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml");

        Assert.Contains("DetailNewStepTextBox.Text", surface, StringComparison.Ordinal);
        Assert.Contains("AddDetailStepAsync", surface, StringComparison.Ordinal);
        Assert.Contains("EnsureDetailItemPersistedAsync", product, StringComparison.Ordinal);
        Assert.Contains("ViewModel.AddStepAsync", product, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailStepsItemsControl\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("new TodoStep", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("TodoWidgetStore.SaveAsync", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceScenario_UsesRealizedRowAndAwaitableProductEditCompletionDeletePaths()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotStepsPersistenceSmoke.cs");
        string product = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.DetailNotesAndSteps.cs");

        Assert.Contains("DetailStepsItemsControl.ContainerFromIndex", surface, StringComparison.Ordinal);
        Assert.Contains("FindAotTodoStepDescendant<TextBox>", surface, StringComparison.Ordinal);
        Assert.Contains("FindAotTodoStepDescendant<CheckBox>", surface, StringComparison.Ordinal);
        Assert.Contains("FindAotTodoStepDescendant<Button>", surface, StringComparison.Ordinal);
        Assert.Contains("SaveDetailStepTextAsync", surface, StringComparison.Ordinal);
        Assert.Contains("SetDetailStepCompletedAsync", surface, StringComparison.Ordinal);
        Assert.Contains("DeleteDetailStepAsync", surface, StringComparison.Ordinal);
        Assert.Contains("WaitForAotTodoStepProjectionAsync", surface, StringComparison.Ordinal);
        Assert.Contains("await SaveDetailStepTextAsync(textBox)", product, StringComparison.Ordinal);
        Assert.Contains("await SetDetailStepCompletedAsync(checkBox)", product, StringComparison.Ordinal);
        Assert.Contains("await DeleteDetailStepAsync(element)", product, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoStepDataContext_UsesNativeAotGeneratedBindableProviderOnlyWhenNeeded()
    {
        string bindable = ReadRepositoryFile(
            "src/DeskBox/ViewModels/TodoViewModels.AotBindableProperties.cs");
        string stepViewModel = ReadRepositoryFile(
            "src/DeskBox/ViewModels/TodoStepViewModel.cs");

        Assert.Equal(3, CountOccurrences(
            bindable,
            "[WinRT.GeneratedBindableCustomProperty]"));
        Assert.Contains("partial class TodoStepViewModel", bindable, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class TodoStepViewModel", stepViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("TodoAttachmentViewModel", bindable, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoStepItemsSource_ProjectsTypedCollectionThroughObjectArrayAndRefreshes()
    {
        string itemViewModel = ReadRepositoryFile(
            "src/DeskBox/ViewModels/TodoItemViewModel.cs");
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml");
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains(
            "public object[] StepItemsSource",
            itemViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "Steps.Cast<object>().ToArray()",
            itemViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnPropertyChanged(nameof(StepItemsSource))",
            itemViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding StepItemsSource}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains("TodoItemViewModel.cs", audit, StringComparison.Ordinal);
        Assert.Contains("StepItemsSource", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_RecordsStoreViewModelAndRealStepRowProjection()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotTodoStepsPersistenceSmoke.cs");
        string coreEvidence = ReadRepositoryFile(
            "src/DeskBox/App.AotTodoPersistenceSmoke.cs");

        Assert.Contains("AotManagedUiTodoStepsPersistenceEvidence", source, StringComparison.Ordinal);
        Assert.Contains("Before", source, StringComparison.Ordinal);
        Assert.Contains("AfterStepMutation", source, StringComparison.Ordinal);
        Assert.Contains("AfterStepDelete", source, StringComparison.Ordinal);
        Assert.Contains("After", source, StringComparison.Ordinal);
        Assert.Contains("StepCompletionRoundTripObserved", source, StringComparison.Ordinal);
        Assert.Contains("StepUiItemCount", coreEvidence, StringComparison.Ordinal);
        Assert.Contains("StepUiContainerRealized", coreEvidence, StringComparison.Ordinal);
        Assert.Contains("StepUiText", coreEvidence, StringComparison.Ordinal);
        Assert.Contains("StepUiIsChecked", coreEvidence, StringComparison.Ordinal);
        Assert.Contains("List<AotManagedUiTodoStepEvidence> Steps", coreEvidence, StringComparison.Ordinal);
        Assert.Contains(
            "await surface.WaitForAotTodoStepProjectionAsync",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Evidence_CapturesTheOwnedTodoStepStoreInsteadOfTheCoreFixtureStore()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotTodoStepsPersistenceSmoke.cs");
        string coreEvidence = ReadRepositoryFile(
            "src/DeskBox/App.AotTodoPersistenceSmoke.cs");

        Assert.Contains(
            "CaptureAotManagedUiTodoStateAsync(\n            surface,\n            AotManagedUiTodoStepsWidgetId)",
            source.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains(
            "CaptureAotManagedUiTodoStateAsync(\n            TodoWidgetContent surface,\n            string widgetId)",
            coreEvidence.Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains("new TodoWidgetStore(widgetId)", coreEvidence, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerScenario_AllowsOnlyTheThreeFixedOwnedTodoSurfaces()
    {
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotTodoPersistenceSmoke.cs");

        Assert.Contains("aot-5b4b2b2a-todo", manager, StringComparison.Ordinal);
        Assert.Contains("aot-5b4b2b2b1-todo-steps", manager, StringComparison.Ordinal);
        Assert.Contains("aot-5b4b2b2b2-todo-attachments", manager, StringComparison.Ordinal);
        Assert.Contains("AotTodoStepsPersistenceOwnedWidgetId", manager, StringComparison.Ordinal);
        Assert.Contains("_contentWidgets.TryGetValue", manager, StringComparison.Ordinal);
        Assert.Contains("TodoWidgetContentAdapter", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_ExecutesThreeFreshTodoStepProcessesAndArchivesSession()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("TodoStepsPersistenceRestart", script, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_TODO_STEPS_PHASE", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-TodoStepsPersistencePhase", script, StringComparison.Ordinal);
        Assert.Contains("todoStepsNaturalExit", script, StringComparison.Ordinal);
        Assert.Contains("archivedTodoStepsSessionPath", script, StringComparison.Ordinal);
        Assert.Contains("sessionPath = $archivedTodoStepsSessionPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_ComparesReloadCompletionDeleteAndPostflight()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("mutate.todoStepsPersistence.after", script, StringComparison.Ordinal);
        Assert.Contains("verifyDelete.todoStepsPersistence.before", script, StringComparison.Ordinal);
        Assert.Contains("verifyDelete.todoStepsPersistence.afterStepMutation", script, StringComparison.Ordinal);
        Assert.Contains("verifyDelete.todoStepsPersistence.afterStepDelete", script, StringComparison.Ordinal);
        Assert.Contains("postflight.todoStepsPersistence.before", script, StringComparison.Ordinal);
        Assert.Contains("final-todo.json", script, StringComparison.Ordinal);
        Assert.Contains("StepCompletionRoundTripObserved", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OuterRunner_KeepsProductionFingerprintRuntimeLogExactProcessAndCleanupGates()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintAfter", script, StringComparison.Ordinal);
        Assert.Contains("runtimeFailureLogLines", script, StringComparison.Ordinal);
        Assert.Contains("todoStepsPreviewProcessesAfter", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", script, StringComparison.Ordinal);
        Assert.Contains("previewRootCleaned", script, StringComparison.Ordinal);
        Assert.Contains(".deskbox-aot-managed-ui-owned.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoStepsScenario_DoesNotEnterAttachmentsReminderRecurrenceOrOsMatrices()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotTodoStepsPersistenceSmoke.cs");
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotStepsPersistenceSmoke.cs");
        string combined = source + surface;

        Assert.DoesNotContain("AddAttachment", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteAttachment", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("AttachmentStorageService", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SetDueDate", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRecurrence", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FileOpenPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Launcher.Launch", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateWidget", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveWidget", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoStepsScenario_ReusesSingleSourceGeneratedResultWriter()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Equal(1, CountOccurrences(source, "JsonSerializer.Serialize("));
        Assert.Contains(
            "AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public AotManagedUiTodoStepsPersistenceEvidence? TodoStepsPersistence",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Deserialize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingTodoCoreScenario_RemainsIndependentAndRunnable()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("TodoPersistenceRestart", source, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiTodoPersistenceScenario", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-TodoPersistencePhase", script, StringComparison.Ordinal);
        Assert.Contains("aot-5b4b2b2a-todo", script, StringComparison.Ordinal);
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
    public void Stage5B4B2B2B1_ProfileSchemaProjectAndLauncherAdvanceTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B1", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("Todo steps", project, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditFreezesTodoStepSourcesProductPathsScopeProjectionAndWarnings()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("stage5B4B2B2B1SourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B1RequiredRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B1RequiredSurfacePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B1RequiredProductPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B1RequiredSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B1ForbiddenScopePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B1JsonSerializeCallCount", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B1SourceWarningMessages", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B1ExpectedWmc1510Count", audit, StringComparison.Ordinal);
        Assert.Contains("StepItemsSource", audit, StringComparison.Ordinal);
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
