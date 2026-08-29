namespace DeskBox.Tests;

public sealed class AotStage5B4B2AContractTests
{
    [Fact]
    public void PersistenceScenario_IsNativeAotOnlyPhaseBoundAndPreviewRootOnly()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains("SettingsWidgetPersistenceRestart", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_PERSISTENCE_PHASE", source, StringComparison.Ordinal);
        Assert.Contains("Mutate", source, StringComparison.Ordinal);
        Assert.Contains("VerifyRestore", source, StringComparison.Ordinal);
        Assert.Contains("Postflight", source, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", source, StringComparison.Ordinal);
        Assert.Contains("settings-widget-persistence-restart", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceScenario_ExercisesRealSettingsViewModelAndExplicitFlush()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Contains("settingsWindow.ViewModel", source, StringComparison.Ordinal);
        Assert.Contains("ShowFileExtensions", source, StringComparison.Ordinal);
        Assert.Contains("FileNameLineCount", source, StringComparison.Ordinal);
        Assert.Contains("TextSize", source, StringComparison.Ordinal);
        Assert.Contains("SelectedTrayIconStyle", source, StringComparison.Ordinal);
        Assert.Contains("FlushPendingSaveAsync(", source, StringComparison.Ordinal);
        Assert.Contains("notifySubscribers: false", source, StringComparison.Ordinal);
        Assert.Contains("SettingsPersistenceFlushed", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceScenario_UsesFixedOwnedWidgetProductPathsAndLiveHwndBounds()
    {
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotPersistenceSmoke.cs");
        string window = ReadRepositoryFile(
            "src/DeskBox/Views/WidgetWindowBase.AotPersistenceSmoke.cs");

        Assert.Contains("aot-5b4a-file", manager, StringComparison.Ordinal);
        Assert.Contains("_fileWidgets.TryGetValue", manager, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ToggleViewMode()", manager, StringComparison.Ordinal);
        Assert.Contains("SetWidgetPositionLocked", manager, StringComparison.Ordinal);
        Assert.Contains("SetWidgetSizeLocked", manager, StringComparison.Ordinal);
        Assert.Contains("ApplyAotPersistenceSmokeBounds", manager, StringComparison.Ordinal);
        Assert.Contains("GetActualWindowBounds()", window, StringComparison.Ordinal);
        Assert.Contains("DisplayArea.GetFromRect", window, StringComparison.Ordinal);
        Assert.Contains("MoveWindowWithoutPersisting", window, StringComparison.Ordinal);
        Assert.Contains("CapturePositionAnchor", window, StringComparison.Ordinal);
        Assert.Contains("UpdateConfigBoundsFromPhysical", window, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceEvidence_RecordsBeforeAfterConfigViewModelAndLiveHostFields()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotPersistenceSmoke.cs");

        Assert.Contains("AotManagedUiPersistenceEvidence", source, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiPersistenceStateEvidence", source, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiPersistenceWidgetEvidence", source, StringComparison.Ordinal);
        Assert.Contains("Before", source, StringComparison.Ordinal);
        Assert.Contains("After", source, StringComparison.Ordinal);
        Assert.Contains("FlushSucceeded", source, StringComparison.Ordinal);
        Assert.Contains("NormalShutdownRequested", source, StringComparison.Ordinal);
        Assert.Contains("ViewModelName", manager, StringComparison.Ordinal);
        Assert.Contains("ViewModelViewMode", manager, StringComparison.Ordinal);
        Assert.Contains("WindowHandle", manager, StringComparison.Ordinal);
        Assert.Contains("ActualBounds", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceScenario_ReusesSingleSourceGeneratedJsonResult()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Equal(1, CountOccurrences(source, "JsonSerializer.Serialize("));
        Assert.Contains("AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult", source, StringComparison.Ordinal);
        Assert.Contains("public AotManagedUiPersistenceEvidence? Persistence", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Deserialize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_ExecutesThreeFreshAotProcessesAndRequiresNaturalExit()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("[ValidateSet(\"BasicReadOnly\", \"DeepSettingsReadOnly\"", script, StringComparison.Ordinal);
        Assert.Contains("\"SettingsWidgetPersistenceRestart\"", script, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_PERSISTENCE_PHASE", script, StringComparison.Ordinal);
        Assert.Contains("Invoke-PersistencePhase", script, StringComparison.Ordinal);
        Assert.Contains("Mutate", script, StringComparison.Ordinal);
        Assert.Contains("VerifyRestore", script, StringComparison.Ordinal);
        Assert.Contains("Postflight", script, StringComparison.Ordinal);
        Assert.Contains("Wait-NaturalPreviewExit", script, StringComparison.Ordinal);
        Assert.Contains("naturalExit", script, StringComparison.Ordinal);
        Assert.Contains("normalShutdownRequested", script, StringComparison.Ordinal);
        Assert.Contains("archivedPersistenceSessionPath", script, StringComparison.Ordinal);
        Assert.Contains("sessionPath = $archivedPersistenceSessionPath", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_ComparesEveryPersistedFieldAcrossProcessesAndRestoredBaseline()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("Assert-PersistenceStateEqual", script, StringComparison.Ordinal);
        Assert.Contains("showFileExtensions", script, StringComparison.Ordinal);
        Assert.Contains("fileNameLineCount", script, StringComparison.Ordinal);
        Assert.Contains("textSize", script, StringComparison.Ordinal);
        Assert.Contains("trayIconStyle", script, StringComparison.Ordinal);
        Assert.Contains("fileWidget", script, StringComparison.Ordinal);
        Assert.Contains("searchWidget", script, StringComparison.Ordinal);
        Assert.Contains("viewModelName", script, StringComparison.Ordinal);
        Assert.Contains("actualBounds", script, StringComparison.Ordinal);
        Assert.Contains("mutate.persistence.after", script, StringComparison.Ordinal);
        Assert.Contains("verifyRestore.persistence.before", script, StringComparison.Ordinal);
        Assert.Contains("postflight.persistence.before", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_KeepsOwnedRootProductionFingerprintAndRuntimeFailureGates()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains(".deskbox-aot-managed-ui-owned.json", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintAfter", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $resolvedDataRoot -Recurse -Force", script, StringComparison.Ordinal);
        Assert.Contains("runtimeFailureLogLines", script, StringComparison.Ordinal);
        Assert.Contains("Unhandled exception:", script, StringComparison.Ordinal);
        Assert.Contains("previewProcessesAfter", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistenceScenario_DoesNotEnterDeferredContentOrOsInteractionMatrices()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotPersistenceSmoke.cs");
        string combined = source + manager;

        Assert.DoesNotContain("QuickCaptureStore", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("TodoWidgetStore", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("GlanceWidgetStore", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("WeatherService", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("ShortcutHelper", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateWidget", manager, StringComparison.Ordinal);
        Assert.DoesNotContain("RemoveWidget", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonInventory_RemainsAtTwentyFiveFilesSixtyCallsAndTwentyThreeContexts()
    {
        string baseline = ReadRepositoryFile("tests/DeskBox.Tests/JsonSerializationBaselineContractTests.cs");

        Assert.Contains("Assert.Equal(28, actual.Count);", baseline, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(64, actual.Values.Sum());", baseline, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(26, actualContextOwners.Length);", baseline, StringComparison.Ordinal);
    }

    [Fact]
    public void Stage5B4B2A_ProfileSchemaProjectAndLauncherAdvanceTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2A", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("persistence", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuditFreezesPersistenceSourcesPhasesFlushNaturalExitAndWarnings()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("stage5B4B2ASourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2ARequiredRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2ARequiredManagerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2ARequiredBoundsPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2ARequiredSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2AForbiddenScopePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2AJsonSerializeCallCount", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2ASourceWarningMessages", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2AExpectedWmc1510Count = 1241", audit, StringComparison.Ordinal);
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
