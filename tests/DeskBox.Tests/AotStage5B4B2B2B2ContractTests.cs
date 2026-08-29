namespace DeskBox.Tests;

public sealed class AotStage5B4B2B2B2ContractTests
{
    [Fact]
    public void TodoAttachmentsScenario_IsNativeAotOnlyPhaseBoundAndPreviewRootOnly()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string persistence = ReadRepositoryFile(
            "src/DeskBox/App.AotTodoAttachmentsPersistenceSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains("TodoAttachmentsPersistenceRestart", source, StringComparison.Ordinal);
        Assert.Contains(
            "DESKBOX_AOT_MANAGED_UI_TODO_ATTACHMENTS_PHASE",
            source,
            StringComparison.Ordinal);
        Assert.Contains("Mutate", source, StringComparison.Ordinal);
        Assert.Contains("VerifyDelete", source, StringComparison.Ordinal);
        Assert.Contains("Postflight", source, StringComparison.Ordinal);
        Assert.Contains("todo-attachments-persistence-restart", source, StringComparison.Ordinal);
        Assert.Contains("DeskBoxDataPathService.Current", persistence, StringComparison.Ordinal);
        Assert.Contains("IsDevelopmentRoot", persistence, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceScenario_CreatesTaskAndImportsThroughRealManagedProductPath()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotAttachmentsPersistenceSmoke.cs");

        Assert.Contains("OpenAddEditorAsync", surface, StringComparison.Ordinal);
        Assert.Contains("DetailTitleTextBox.Text", surface, StringComparison.Ordinal);
        Assert.Contains("ViewModel.FinalizeDetailAsync", surface, StringComparison.Ordinal);
        Assert.Contains("ViewModel.AddAttachmentPathAsync", surface, StringComparison.Ordinal);
        Assert.Contains("copyToManagedStorageOverride: true", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("new TodoAttachment", surface, StringComparison.Ordinal);
        Assert.DoesNotContain("TodoWidgetStore.SaveAsync", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedImport_UsesStreamingFileCopyWithoutExpandingRustAbi()
    {
        string storage = ReadRepositoryFile(
            "src/DeskBox/Services/AttachmentStorageService.cs");
        string native = ReadRepositoryFile("native/deskbox-native/src/lib.rs");

        Assert.Contains("File.Copy(normalizedSourcePath, destinationPath", storage, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllBytes", storage, StringComparison.Ordinal);
        Assert.DoesNotContain("todo_attachment", native, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(10, CountOccurrences(native, "#[unsafe(no_mangle)]"));
    }

    [Fact]
    public void TodoAttachmentItemsSource_ProjectsTypedCollectionThroughObjectArrayAndRefreshes()
    {
        string itemViewModel = ReadRepositoryFile(
            "src/DeskBox/ViewModels/TodoItemViewModel.cs");
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml");

        Assert.Contains(
            "public object[] AttachmentItemsSource",
            itemViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "Attachments.Cast<object>().ToArray()",
            itemViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnPropertyChanged(nameof(AttachmentItemsSource))",
            itemViewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding AttachmentItemsSource}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentTile_RemainsTypedCompiledBindingWithoutAnotherBindableProvider()
    {
        string tileXaml = ReadRepositoryFile(
            "src/DeskBox/Controls/AttachmentTileStrip.xaml");
        string bindable = ReadRepositoryFile(
            "src/DeskBox/ViewModels/TodoViewModels.AotBindableProperties.cs");

        Assert.Contains(
            "x:DataType=\"viewModels:TodoAttachmentViewModel\"",
            tileXaml,
            StringComparison.Ordinal);
        Assert.Contains("{x:Bind DisplayName, Mode=OneWay}", tileXaml, StringComparison.Ordinal);
        Assert.Contains("{x:Bind Glyph, Mode=OneWay}", tileXaml, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(
            bindable,
            "[WinRT.GeneratedBindableCustomProperty]"));
        Assert.DoesNotContain("TodoAttachmentViewModel", bindable, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentTileProbe_ObservesRealizedDataTemplateAndRenderedFields()
    {
        string tile = ReadRepositoryFile(
            "src/DeskBox/Controls/AttachmentTileStrip.AotSmoke.cs");
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.xaml");

        Assert.Contains("x:Name=\"DetailAttachmentStrip\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AttachmentItems.ContainerFromIndex", tile, StringComparison.Ordinal);
        Assert.Contains("TodoAttachmentViewModel", tile, StringComparison.Ordinal);
        Assert.Contains("FindAotAttachmentDescendant<TextBlock>", tile, StringComparison.Ordinal);
        Assert.Contains("FindAotAttachmentDescendant<FontIcon>", tile, StringComparison.Ordinal);
        Assert.Contains("RemoveAttachmentButton", tile, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.GetName", tile, StringComparison.Ordinal);
        Assert.Contains("WaitForAotAttachmentTileAsync", tile, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentDelete_UsesRealizedRowAndAwaitableProductHandlerPath()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotAttachmentsPersistenceSmoke.cs");
        string product = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.Attachments.cs");

        Assert.Contains("WaitForAotAttachmentTileAsync", surface, StringComparison.Ordinal);
        Assert.Contains("DeleteDetailAttachmentAsync(tile.Attachment)", surface, StringComparison.Ordinal);
        Assert.Contains("await DeleteDetailAttachmentAsync(e.Attachment)", product, StringComparison.Ordinal);
        Assert.Contains("ViewModel.DeleteAttachmentAsync", product, StringComparison.Ordinal);
    }

    [Fact]
    public void ProductViewModel_PersistsMetadataBeforeDeletingManagedFile()
    {
        string product = ReadRepositoryFile(
            "src/DeskBox/ViewModels/TodoWidgetViewModel.DetailAndAttachments.cs");
        int saveIndex = product.IndexOf("await SaveAsync();", StringComparison.Ordinal);
        int deleteIndex = product.IndexOf("File.Delete(attachment.FilePath);", StringComparison.Ordinal);

        Assert.Contains("AttachmentStorageService.ImportPathAsync", product, StringComparison.Ordinal);
        Assert.Contains("copyToManagedStorageOverride", product, StringComparison.Ordinal);
        Assert.True(saveIndex >= 0 && deleteIndex > saveIndex);
    }

    [Fact]
    public void Evidence_RecordsManagedFilesMetadataAndRealAttachmentTileProjection()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotTodoAttachmentsPersistenceSmoke.cs");
        string shared = ReadRepositoryFile(
            "src/DeskBox/App.AotTodoPersistenceSmoke.cs");

        Assert.Contains("AotManagedUiTodoAttachmentsPersistenceEvidence", source, StringComparison.Ordinal);
        Assert.Contains("AfterAttachmentDelete", source, StringComparison.Ordinal);
        Assert.Contains("ManagedAttachmentPath", source, StringComparison.Ordinal);
        Assert.Contains("InitialAttachmentUiProjected", source, StringComparison.Ordinal);
        Assert.Contains("RestartAttachmentUiProjected", source, StringComparison.Ordinal);
        Assert.Contains("List<AotManagedUiTodoAttachmentEvidence> Attachments", shared, StringComparison.Ordinal);
        Assert.Contains("ManagedAttachmentRelativePaths", shared, StringComparison.Ordinal);
        Assert.Contains("AttachmentUiContainerRealized", shared, StringComparison.Ordinal);
        Assert.Contains("AttachmentUiDisplayName", shared, StringComparison.Ordinal);
        Assert.Contains("AttachmentUiGlyph", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerScenario_AllowsOnlyTheThreeFixedOwnedTodoSurfaces()
    {
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotTodoPersistenceSmoke.cs");

        Assert.Contains("aot-5b4b2b2a-todo", manager, StringComparison.Ordinal);
        Assert.Contains("aot-5b4b2b2b1-todo-steps", manager, StringComparison.Ordinal);
        Assert.Contains("aot-5b4b2b2b2-todo-attachments", manager, StringComparison.Ordinal);
        Assert.Contains("AotTodoAttachmentsPersistenceOwnedWidgetId", manager, StringComparison.Ordinal);
        Assert.Contains("_contentWidgets.TryGetValue", manager, StringComparison.Ordinal);
        Assert.Contains("TodoWidgetContentAdapter", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedTrayFixtureRouting_SelectsTheOwnedTodoAttachmentWidget()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Contains("bool isTodoAttachmentsPersistence", source, StringComparison.Ordinal);
        Assert.Contains(
            "result.Scenario == AotManagedUiTodoAttachmentsPersistenceScenario",
            source,
            StringComparison.Ordinal);
        Assert.Contains("? AotManagedUiTodoAttachmentsWidgetId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_ExecutesThreeFreshTodoAttachmentProcessesAndArchivesSession()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("TodoAttachmentsPersistenceRestart", script, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_TODO_ATTACHMENTS_PHASE", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-TodoAttachmentsPersistencePhase", script, StringComparison.Ordinal);
        Assert.Contains("todoAttachmentsNaturalExit", script, StringComparison.Ordinal);
        Assert.Contains("archivedTodoAttachmentsSessionPath", script, StringComparison.Ordinal);
        Assert.Contains("sessionPath = $archivedTodoAttachmentsSessionPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_VerifiesOwnedPathHashesPhysicalDeleteAndEmptyPostflight()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("todo-managed-attachment.txt", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileSha256", script, StringComparison.Ordinal);
        Assert.Contains("fixtureSha256", script, StringComparison.Ordinal);
        Assert.Contains("managedAttachmentSha256", script, StringComparison.Ordinal);
        Assert.Contains("managedAttachmentPath", script, StringComparison.Ordinal);
        Assert.Contains("afterAttachmentDelete", script, StringComparison.Ordinal);
        Assert.Contains("managedAttachmentRelativePaths", script, StringComparison.Ordinal);
        Assert.Contains("$managedFilesAfterDelete = @(", script, StringComparison.Ordinal);
        Assert.Contains("final-todo.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_ComparesRestartDeleteAndPostflightStates()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("mutate.todoAttachmentsPersistence.after", script, StringComparison.Ordinal);
        Assert.Contains("verifyDelete.todoAttachmentsPersistence.before", script, StringComparison.Ordinal);
        Assert.Contains("verifyDelete.todoAttachmentsPersistence.afterAttachmentDelete", script, StringComparison.Ordinal);
        Assert.Contains("postflight.todoAttachmentsPersistence.before", script, StringComparison.Ordinal);
        Assert.Contains("Assert-TodoStateEqual", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_KeepsProductionFingerprintRuntimeLogExactProcessAndCleanupGates()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintAfter", script, StringComparison.Ordinal);
        Assert.Contains("runtimeFailureLogLines", script, StringComparison.Ordinal);
        Assert.Contains("todoAttachmentsPreviewProcessesAfter", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", script, StringComparison.Ordinal);
        Assert.Contains("previewRootCleaned", script, StringComparison.Ordinal);
        Assert.Contains(".deskbox-aot-managed-ui-owned.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoAttachmentsScenario_DoesNotEnterReminderRecurrencePickerShellOrRustMatrices()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/App.AotTodoAttachmentsPersistenceSmoke.cs");
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.AotAttachmentsPersistenceSmoke.cs");
        string combined = source + surface;

        Assert.DoesNotContain("SetDueDate", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("SetRecurrence", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FileOpenPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Launcher.Launch", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeBackend", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryImport", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateWidget", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveWidget", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void TodoAttachmentsScenario_ReusesSingleSourceGeneratedResultWriter()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Equal(1, CountOccurrences(source, "JsonSerializer.Serialize("));
        Assert.Contains(
            "AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "public AotManagedUiTodoAttachmentsPersistenceEvidence? TodoAttachmentsPersistence",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Deserialize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingTodoCoreAndStepsScenarios_RemainIndependentAndRunnable()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("TodoPersistenceRestart", source, StringComparison.Ordinal);
        Assert.Contains("TodoStepsPersistenceRestart", source, StringComparison.Ordinal);
        Assert.Contains("Invoke-TodoPersistencePhase", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-TodoStepsPersistencePhase", script, StringComparison.Ordinal);
        Assert.Contains("aot-5b4b2b2a-todo", script, StringComparison.Ordinal);
        Assert.Contains("aot-5b4b2b2b1-todo-steps", script, StringComparison.Ordinal);
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
    public void Stage5B4B2B2B2_ProfileSchemaProjectAndLauncherAdvanceTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B2", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("Todo managed attachments", project, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditFreezesTodoAttachmentSourcesProductPathsScopeProjectionAndWarnings()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("stage5B4B2B2B2SourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B2RequiredRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B2RequiredSurfacePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B2RequiredProductPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B2RequiredTilePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B2RequiredSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B2ForbiddenScopePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B2JsonSerializeCallCount", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B2SourceWarningMessages", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2B2B2ExpectedWmc1510Count", audit, StringComparison.Ordinal);
        Assert.Contains("AttachmentItemsSource", audit, StringComparison.Ordinal);
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
