namespace DeskBox.Tests;

public sealed class AotStage5B4B2C1ContractTests
{
    [Fact]
    public void GlanceScenario_IsNativeAotOnlyPhaseBoundAndPreviewRootOnly()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string scenario = ReadRepositoryFile("src/DeskBox/App.AotGlancePersistenceSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", shared, StringComparison.Ordinal);
        Assert.Contains("GlancePersistenceRestart", shared, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_GLANCE_PHASE", shared, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_GLANCE_FIXTURE", shared, StringComparison.Ordinal);
        Assert.Contains("Mutate", shared, StringComparison.Ordinal);
        Assert.Contains("VerifyRestore", shared, StringComparison.Ordinal);
        Assert.Contains("Postflight", shared, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", shared, StringComparison.Ordinal);
        Assert.Contains("IsAotManagedUiPathEqualOrInside", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void LocalImageMutation_ReusesOrdinarySettingsProductPolicy()
    {
        string policy = ReadRepositoryFile("src/DeskBox/Services/GlanceWidgetSettingsPolicy.cs");
        string settings = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/GlanceWidgetSettingsSection.xaml.cs");
        string viewModel = ReadRepositoryFile("src/DeskBox/ViewModels/GlanceWidgetViewModel.cs");
        string scenario = ReadRepositoryFile("src/DeskBox/App.AotGlancePersistenceSmoke.cs");

        Assert.Contains("public static void SetLocalImageFiles", policy, StringComparison.Ordinal);
        Assert.Contains("GlanceWidgetSettingsPolicy.SetLocalImageFiles(", settings, StringComparison.Ordinal);
        Assert.Contains("GlanceWidgetSettingsPolicy.ClearLocalSource(_settings)", settings, StringComparison.Ordinal);
        Assert.Contains("public Task SetLocalImageFilesAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("GlanceWidgetSettingsPolicy.SetLocalImageFiles(settings, imagePaths)", viewModel, StringComparison.Ordinal);
        Assert.Contains("await viewModel.SetLocalImageFilesAsync([fixturePath])", scenario, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Deserialize", scenario, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllText", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void Preferences_UseExistingDisplayLayoutAndPlaybackProductPaths()
    {
        string scenario = ReadRepositoryFile("src/DeskBox/App.AotGlancePersistenceSmoke.cs");
        string viewModel = ReadRepositoryFile("src/DeskBox/ViewModels/GlanceWidgetViewModel.cs");

        Assert.Contains("SetDisplayElementAsync", scenario, StringComparison.Ordinal);
        Assert.Contains("SetLayoutAsync", scenario, StringComparison.Ordinal);
        Assert.Contains("SetPhotoPlaybackAsync", scenario, StringComparison.Ordinal);
        Assert.Contains("return _store.UpdateAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("GlanceWidgetSettingsPolicy.SetDisplayElement", viewModel, StringComparison.Ordinal);
        Assert.Contains("GlanceWidgetSettingsPolicy.SetLayout", viewModel, StringComparison.Ordinal);
        Assert.Contains("GlanceWidgetSettingsPolicy.SetPhotoPlayback", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void GlanceRuntimeBinding_HasOneNarrowAotProviderWithExactPropertyList()
    {
        string bindable = ReadRepositoryFile(
            "src/DeskBox/ViewModels/GlanceWidgetViewModel.AotBindableProperties.cs");
        string viewModel = ReadRepositoryFile("src/DeskBox/ViewModels/GlanceWidgetViewModel.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", bindable, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(bindable, "[WinRT.GeneratedBindableCustomProperty"));
        Assert.Equal(33, CountOccurrences(bindable, "nameof("));
        Assert.Contains("nameof(IsEditorialLayout)", bindable, StringComparison.Ordinal);
        Assert.Contains("nameof(ReadabilityOpacity)", bindable, StringComparison.Ordinal);
        Assert.Contains("nameof(ShowPhotoControls)", bindable, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class GlanceWidgetViewModel", viewModel, StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceEvidence_ObservesDecodedImageBrushLayoutReadabilityAndActions()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.xaml");
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.AotPersistenceSmoke.cs");

        Assert.Contains("x:Name=\"ImmersiveLayoutRoot\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CenteredLayoutRoot\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EditorialLayoutRoot\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"CalendarLayoutRoot\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WaitForAotGlanceSurfaceAsync", surface, StringComparison.Ordinal);
        Assert.Contains("_decodedImagePath", surface, StringComparison.Ordinal);
        Assert.Contains("active.Background as ImageBrush", surface, StringComparison.Ordinal);
        Assert.Contains("UriSource?.LocalPath", surface, StringComparison.Ordinal);
        Assert.Contains("ReadabilityLayer.Visibility", surface, StringComparison.Ordinal);
        Assert.Contains("ActionLayer.Visibility", surface, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(DataContext, _viewModel)", surface, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagerHost_AllowsOnlyFixedOwnedGlanceWidgetAndRealAdapter()
    {
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotGlancePersistenceSmoke.cs");

        Assert.Contains("aot-5b4b2c1-glance", manager, StringComparison.Ordinal);
        Assert.Contains("_contentWidgets.TryGetValue", manager, StringComparison.Ordinal);
        Assert.Contains("window.ContentReadyTask", manager, StringComparison.Ordinal);
        Assert.Contains("GlanceWidgetContentAdapter", manager, StringComparison.Ordinal);
        Assert.Contains("adapter.View is GlanceWidgetContent", manager, StringComparison.Ordinal);
        Assert.Contains("adapter.ViewModel", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedTrayFixtureRouting_SelectsGlanceKindAndOwnedId()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Contains("bool isGlancePersistence", shared, StringComparison.Ordinal);
        Assert.Contains("? AotManagedUiGlanceWidgetId", shared, StringComparison.Ordinal);
        Assert.Contains("? WidgetKind.Glance", shared, StringComparison.Ordinal);
        Assert.Contains("public AotManagedUiGlancePersistenceEvidence? GlancePersistence", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void PerWidgetStore_RemainsSourceGeneratedAndLocalCatalogDoesNotReadImageBytes()
    {
        string store = ReadRepositoryFile("src/DeskBox/Services/GlanceWidgetStore.cs");
        string imageService = ReadRepositoryFile("src/DeskBox/Services/GlanceImageService.cs");

        Assert.Contains("[JsonSerializable(", store, StringComparison.Ordinal);
        Assert.Contains("typeof(GlanceWidgetData)", store, StringComparison.Ordinal);
        Assert.Contains("GlancePreferencesJsonContext.Default.Preferences", store, StringComparison.Ordinal);
        Assert.Contains("CreateLocalImages", imageService, StringComparison.Ordinal);
        Assert.Contains("File.Exists", imageService, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllBytes", imageService, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadAllBytesAsync", imageService, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_SeedsValidOwnedPngAndOfflinePerWidgetBaseline()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("glance-local.png", script, StringComparison.Ordinal);
        Assert.Contains("[System.IO.File]::WriteAllBytes", script, StringComparison.Ordinal);
        Assert.Contains("[Convert]::FromBase64String", script, StringComparison.Ordinal);
        Assert.Contains("glance\\widgets", script, StringComparison.Ordinal);
        Assert.Contains("aot-5b4b2c1-glance.json", script, StringComparison.Ordinal);
        Assert.Contains("backgroundSource = \"LocalFiles\"", script, StringComparison.Ordinal);
        Assert.Contains("localImagePaths = @()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_UsesThreeFreshProcessesHashesEqualityAndCleanupGates()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("Invoke-GlancePersistencePhase", script, StringComparison.Ordinal);
        Assert.Contains("glanceNaturalExit", script, StringComparison.Ordinal);
        Assert.Contains("$processIds | Sort-Object -Unique", script, StringComparison.Ordinal);
        Assert.Contains("phaseExecutableHashes", script, StringComparison.Ordinal);
        Assert.Contains("fixtureSha256Before", script, StringComparison.Ordinal);
        Assert.Contains("fixtureSha256After", script, StringComparison.Ordinal);
        Assert.Contains("surface.activeImageUri", script, StringComparison.Ordinal);
        Assert.Contains("surface.immersiveLayoutVisible", script, StringComparison.Ordinal);
        Assert.Contains("surface.calendarLayoutVisible", script, StringComparison.Ordinal);
        Assert.Contains("Assert-GlanceStateEqual", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("glancePreviewProcessesAfter", script, StringComparison.Ordinal);
        Assert.Contains("previewRootCleaned", script, StringComparison.Ordinal);
        Assert.Contains("final-glance.json", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Scenario_DoesNotEnterOnlineNetworkLocationPickerFolderOrRustPaths()
    {
        string scenario = ReadRepositoryFile("src/DeskBox/App.AotGlancePersistenceSmoke.cs");
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/GlanceWidgetContent.AotPersistenceSmoke.cs");
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotGlancePersistenceSmoke.cs");
        string combined = scenario + surface + manager;

        Assert.DoesNotContain("RefreshOnline", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("HttpClient", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FileOpenPicker", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FolderPicker", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "GlanceBackgroundSource.LocalFolder",
            combined,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("NativeBackend", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("LibraryImport", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CreateWidget", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("RemoveWidget", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Scenario_ReusesSingleSourceGeneratedResultWriter()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Equal(1, CountOccurrences(shared, "JsonSerializer.Serialize("));
        Assert.Contains(
            "AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult",
            shared,
            StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializer.Deserialize", shared, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingManagedUiScenarios_RemainIndependentAndRunnable()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        foreach (string scenario in new[]
        {
            "BasicReadOnly",
            "DeepSettingsReadOnly",
            "SettingsWidgetPersistenceRestart",
            "QuickCapturePersistenceRestart",
            "TodoPersistenceRestart",
            "TodoStepsPersistenceRestart",
            "TodoAttachmentsPersistenceRestart"
        })
        {
            Assert.Contains(scenario, shared, StringComparison.Ordinal);
            Assert.Contains(scenario, script, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RustAbi_RemainsAtVersionTwoWithCurrentCapabilitiesAndExports()
    {
        string native = ReadRepositoryFile("native/deskbox-native/src/lib.rs");
        string header = ReadRepositoryFile("native/include/deskbox_native.h");

        Assert.Contains("DESKBOX_NATIVE_ABI_VERSION: u32 = 2", native, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_NATIVE_CAPABILITY_QUICK_ACCESS_V1: u64 = 1 << 7", native, StringComparison.Ordinal);
        Assert.Equal(10, CountOccurrences(native, "pub const DESKBOX_NATIVE_CAPABILITY_"));
        Assert.Equal(10, CountOccurrences(native, "#[unsafe(no_mangle)]"));
        Assert.DoesNotContain("glance", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stage5B4B2C1_ProfileSchemaProjectAndLauncherAdvanceTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 62", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C1", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 62", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("Glance owned-local-image/preferences/reload/restore/postflight", project, StringComparison.Ordinal);
    }

    [Fact]
    public void AuditFreezesGlanceRunnerSurfaceProductManagerScopeAndWarnings()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("stage5B4B2C1SourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C1RequiredRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C1RequiredSurfacePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C1RequiredProductPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C1RequiredManagerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C1RequiredSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C1ForbiddenScopePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C1GeneratedBindableCount", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C1BindablePropertyCount", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C1JsonSerializeCallCount", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C1SourceWarningMessages", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B2C1ExpectedWmc1510Count", audit, StringComparison.Ordinal);
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
