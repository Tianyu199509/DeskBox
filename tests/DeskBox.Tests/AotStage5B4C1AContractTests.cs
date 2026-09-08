namespace DeskBox.Tests;

public sealed class AotStage5B4C1AContractTests
{
    [Fact]
    public void LocalFileScenario_IsNativeAotOnlyPhaseBoundAndPreviewRootOnly()
    {
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotLocalFilePersistenceSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", shared, StringComparison.Ordinal);
        Assert.Contains("#if DESKBOX_NATIVE_AOT", scenario, StringComparison.Ordinal);
        Assert.Contains("LocalFileSurfacePersistenceRestart", shared, StringComparison.Ordinal);
        Assert.Contains(
            "DESKBOX_AOT_MANAGED_UI_LOCAL_FILE_PHASE",
            shared,
            StringComparison.Ordinal);
        Assert.Contains("Mutate", shared, StringComparison.Ordinal);
        Assert.Contains("VerifyRestore", shared, StringComparison.Ordinal);
        Assert.Contains("Postflight", shared, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", shared, StringComparison.Ordinal);
        Assert.Contains(
            "AotManagedUiLocalFilePersistenceEvidence",
            scenario,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Fixture_IsExactScenarioPhaseWidgetAndOwnedPreviewTreeOnly()
    {
        string fixture = ReadRepositoryFile(
            "src/DeskBox/Services/AotLocalFileSurfaceFixture.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", fixture, StringComparison.Ordinal);
        Assert.Contains("LocalFileSurfacePersistenceRestart", fixture, StringComparison.Ordinal);
        Assert.Contains("aot-5b4c1a-file", fixture, StringComparison.Ordinal);
        Assert.Contains("local-file-surface", fixture, StringComparison.Ordinal);
        Assert.Contains("widget-root", fixture, StringComparison.Ordinal);
        Assert.Contains("sources", fixture, StringComparison.Ordinal);
        Assert.Contains("phase is not \"Mutate\"", fixture, StringComparison.Ordinal);
        Assert.Contains("IsPathEqualOrInside(dataPaths.RootPath", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("LOCALAPPDATA", fixture, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("DesktopDirectory", fixture, StringComparison.Ordinal);
    }

    [Fact]
    public void SeededTree_HasBaselineNestedCopyAndMoveFixtures()
    {
        string runner = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("$localFileWidgetRoot", runner, StringComparison.Ordinal);
        Assert.Contains("$localFileNestedRoot", runner, StringComparison.Ordinal);
        Assert.Contains("$localFileSourceRoot", runner, StringComparison.Ordinal);
        Assert.Contains("baseline.txt", runner, StringComparison.Ordinal);
        Assert.Contains("nested.txt", runner, StringComparison.Ordinal);
        Assert.Contains("copy-source.txt", runner, StringComparison.Ordinal);
        Assert.Contains("move-source.txt", runner, StringComparison.Ordinal);
        Assert.Contains("fileWidgetFolderOpenBehavior", runner, StringComparison.Ordinal);
        Assert.Contains("Embedded", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void Mutation_UsesRealNavigationCopyMoveRenameAndConflictPaths()
    {
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotLocalFilePersistenceSmoke.cs");

        Assert.Contains("NavigateIntoFolderAsync", scenario, StringComparison.Ordinal);
        Assert.Contains("NavigateUpAsync", scenario, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(scenario, "ImportPathsAsync("));
        Assert.Contains("moveWhenMapped: false", scenario, StringComparison.Ordinal);
        Assert.Contains("moveWhenMapped: true", scenario, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(scenario, "useShellProgress: false"));
        Assert.Equal(2, CountOccurrences(scenario, "RenameItemAsync("));
        Assert.Contains("catch (IOException ex)", scenario, StringComparison.Ordinal);
        Assert.Contains("LocalFileRenameConflictRejected", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void WatcherProbe_UsesExternalOwnedStimulusWithoutManualRefresh()
    {
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotLocalFilePersistenceSmoke.cs");

        Assert.Contains("File.WriteAllTextAsync", scenario, StringComparison.Ordinal);
        Assert.Contains("watcher-created.txt", scenario, StringComparison.Ordinal);
        Assert.Contains("LocalFileWatcherObservedExternalCreate", scenario, StringComparison.Ordinal);
        Assert.Contains("WatcherRemovalObserved", scenario, StringComparison.Ordinal);
        Assert.DoesNotContain("RefreshFolderContentsAsync", scenario, StringComparison.Ordinal);
        Assert.DoesNotContain("ConfigureFolderWatchersAsync", scenario, StringComparison.Ordinal);
    }

    [Fact]
    public void RealSurfaceProbe_RequiresHwndXamlRootContainersAndProjectedNames()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotLocalFileSmoke.cs");
        string manager = ReadRepositoryFile(
            "src/DeskBox/Services/WidgetManager.AotLocalFileSurfaceSmoke.cs");
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotLocalFilePersistenceSmoke.cs");
        string normalizedScenario = scenario.ReplaceLineEndings("\n");
        string runner = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("WaitForAotLocalFileSurfaceAsync", surface, StringComparison.Ordinal);
        Assert.Contains("GetActiveItemsView", surface, StringComparison.Ordinal);
        Assert.Contains("ContainerFromItem", surface, StringComparison.Ordinal);
        Assert.Contains("FileItemSurface", surface, StringComparison.Ordinal);
        Assert.Contains("ItemNameText.Text", surface, StringComparison.Ordinal);
        Assert.Contains("? FolderNavigationText.Text", surface, StringComparison.Ordinal);
        Assert.Contains(": string.Empty", surface, StringComparison.Ordinal);
        Assert.Contains("DataContextMatches", surface, StringComparison.Ordinal);
        Assert.Contains("ProjectedItemCount", surface, StringComparison.Ordinal);
        Assert.Contains("itemsInExpectedOrder", surface, StringComparison.Ordinal);
        Assert.Contains(
            "string[] expectedNamesInOrder = expectedNames.ToArray();",
            surface,
            StringComparison.Ordinal);
        Assert.DoesNotContain(".OrderBy(name => name", surface, StringComparison.Ordinal);
        Assert.Contains(
            "AotLocalFileSurfaceFixture.NestedDirectoryName,\n        \"baseline\"",
            normalizedScenario,
            StringComparison.Ordinal);
        Assert.Contains("@(\"nested\", \"baseline\")", runner, StringComparison.Ordinal);
        Assert.Contains("GetAotLocalFileSurfaceHostAsync", manager, StringComparison.Ordinal);
        Assert.Contains("ContentReadyTask", manager, StringComparison.Ordinal);
        Assert.Contains("WindowHandle", manager, StringComparison.Ordinal);
        Assert.Contains("WindowContentRoot?.XamlRoot", manager, StringComparison.Ordinal);
    }

    [Fact]
    public void NonEmptyFileTemplates_HaveNarrowGeneratedAotProviders()
    {
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/FileItemSurface.AotBindableProperties.cs");
        string item = ReadRepositoryFile(
            "src/DeskBox/Models/WidgetItem.AotBindableProperties.cs");
        string viewModel = ReadRepositoryFile(
            "src/DeskBox/ViewModels/WidgetViewModel.AotBindableProperties.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", surface, StringComparison.Ordinal);
        Assert.Contains("#if DESKBOX_NATIVE_AOT", item, StringComparison.Ordinal);
        Assert.Contains("#if DESKBOX_NATIVE_AOT", viewModel, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(surface, "[WinRT.GeneratedBindableCustomProperty("));
        Assert.Equal(1, CountOccurrences(item, "[WinRT.GeneratedBindableCustomProperty("));
        Assert.Equal(1, CountOccurrences(viewModel, "[WinRT.GeneratedBindableCustomProperty("));
        foreach (string property in new[]
        {
            "IconLayoutVisibility", "ListLayoutVisibility",
            "SurfaceHorizontalAlignment", "SurfaceMargin", "SurfaceMaxWidth",
            "SurfacePadding"
        })
        {
            Assert.Contains($"nameof({property})", surface, StringComparison.Ordinal);
        }
        foreach (string property in new[]
        {
            "FallbackIconVisibility", "FullPath", "Icon", "IconVisibility",
            "Name", "SecondaryInfo"
        })
        {
            Assert.Contains($"nameof({property})", item, StringComparison.Ordinal);
        }
        foreach (string property in new[]
        {
            "CurrentFolderDisplayName", "FolderNavigationVisibility",
            "VisibleItems", "IconViewVisibility", "ListViewVisibility"
        })
        {
            Assert.Contains($"nameof({property})", viewModel, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Evidence_HashesEveryOwnedFileAndUsesSingleGeneratedWriter()
    {
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotLocalFilePersistenceSmoke.cs");
        string shared = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Contains("SHA256.HashData(stream)", scenario, StringComparison.Ordinal);
        Assert.Contains("SearchOption.AllDirectories", scenario, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiLocalFileDiskEntryEvidence", scenario, StringComparison.Ordinal);
        Assert.Contains("RelativePath", scenario, StringComparison.Ordinal);
        Assert.Contains("Length", scenario, StringComparison.Ordinal);
        Assert.Contains("Sha256", scenario, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(shared, "JsonSerializer.Serialize("));
        Assert.Contains(
            "AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult",
            shared,
            StringComparison.Ordinal);
        Assert.Contains(
            "public AotManagedUiLocalFilePersistenceEvidence? LocalFilePersistence",
            shared,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OuterRunner_UsesThreeProcessesIndependentDiskAndIsolationGates()
    {
        string runner = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("Invoke-LocalFilePersistencePhase", runner, StringComparison.Ordinal);
        Assert.Contains("Assert-LocalFileEvidenceState", runner, StringComparison.Ordinal);
        Assert.Contains("Assert-LocalFileStateEqual", runner, StringComparison.Ordinal);
        Assert.Contains("Get-LocalFileFixtureState", runner, StringComparison.Ordinal);
        Assert.Contains("mutate-independent-disk", runner, StringComparison.Ordinal);
        Assert.Contains("verify-restore-independent-disk", runner, StringComparison.Ordinal);
        Assert.Contains("postflight-independent-disk", runner, StringComparison.Ordinal);
        Assert.Contains("localFileNaturalExit", runner, StringComparison.Ordinal);
        Assert.Contains("$processIds | Sort-Object -Unique", runner, StringComparison.Ordinal);
        Assert.Contains("phaseExecutableHashes", runner, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", runner, StringComparison.Ordinal);
        Assert.Contains("localFilePreviewProcessesAfter", runner, StringComparison.Ordinal);
        Assert.Contains("previewRootCleaned", runner, StringComparison.Ordinal);
        Assert.Contains("disk-states.json", runner, StringComparison.Ordinal);
        Assert.Contains("final-fixture", runner, StringComparison.Ordinal);
        Assert.Contains(
            "[bool]$item.isFolder -ne ([string]$item.name -ceq \"nested\")",
            runner,
            StringComparison.Ordinal);
    }

    [Fact]
    public void StageScope_DefersShellPickerDragDropRecycleHotkeysMediaAndNetwork()
    {
        string scenario = ReadRepositoryFile(
            "src/DeskBox/App.AotLocalFilePersistenceSmoke.cs");
        string surface = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/FileSurfaceContent.AotLocalFileSmoke.cs");
        string combined = scenario + surface;

        Assert.DoesNotContain("StorageFile", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("StorageFolder", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FolderPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("FileOpenPicker", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("DataPackage", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("NativeDrop", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteEntryToRecycleBin", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("RegisterHotKey", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("WeatherService", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("HttpClient", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void RustAbi_RemainsUnchangedAndDoesNotAcquireLocalFileSurfaceWork()
    {
        string native = ReadRepositoryFile("native/deskbox-native/src/lib.rs");
        string header = ReadRepositoryFile("native/include/deskbox_native.h");

        Assert.Contains("DESKBOX_NATIVE_ABI_VERSION: u32 = 2", native, StringComparison.Ordinal);
        Assert.Equal(10, CountOccurrences(native, "pub const DESKBOX_NATIVE_CAPABILITY_"));
        Assert.Equal(10, CountOccurrences(native, "#[unsafe(no_mangle)]"));
        Assert.DoesNotContain("local_file", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file_surface", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stage5B4C1A_ProfileSchemaProjectAndAuditAdvanceTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");
        string report = ReadRepositoryFile(
            "docs/architecture/stage-reports/aot-stage-5b-4c1a-report.md");
        string roadmap = ReadRepositoryFile(
            "docs/architecture/rust-native-aot-roadmap.md");

        Assert.Contains("$auditProfileVersion = 62", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4C1A", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 62", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("owned local-file", project, StringComparison.Ordinal);
        Assert.Contains("recycle", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("5B-4C1A 已完成", report, StringComparison.Ordinal);
        Assert.Contains("360/360", report, StringComparison.Ordinal);
        Assert.Contains("2355/2355", report, StringComparison.Ordinal);
        Assert.Contains("5B-4C1B1", report, StringComparison.Ordinal);
        Assert.Contains("5B-4C1A owned 本地文件", roadmap, StringComparison.Ordinal);
        Assert.Contains("profile 46 / schema 43", roadmap, StringComparison.Ordinal);
        Assert.Contains("5B-4C1B1", roadmap, StringComparison.Ordinal);
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
