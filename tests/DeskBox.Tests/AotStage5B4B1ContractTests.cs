namespace DeskBox.Tests;

public sealed class AotStage5B4B1ContractTests
{
    private static readonly string[] DeepSettingsRoutes =
    [
        "AppearanceDetail",
        "CapsuleMode",
        "WidgetGroups",
        "FileDisplaySettings",
        "ManagedStorage",
        "FileStackSettings",
        "DesktopOrganizationSettings",
        "QuickCaptureSettings",
        "TodoSettings",
        "MusicSettings",
        "WeatherSettings",
        "GlanceSettings",
        "SearchSettings",
        "AppearanceMaterialSettings",
        "AppearanceDensitySettings",
        "AppearanceWindowSettings",
        "AppearanceAnimationSettings",
        "CapsuleBehaviorSettings",
        "CapsuleArrangementSettings",
        "CapsuleAnimationSettings",
        "CapsuleOverridesSettings",
        "BackupRestoreSettings",
        "DataHealthSettings",
        "CompatibilityDiagnosticsSettings",
        "PerformanceSettings"
    ];

    [Fact]
    public void ManagedUiRunner_AddsDeepSettingsReadOnlyWithoutWeakeningBasicReadOnly()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Contains("BasicReadOnly", source, StringComparison.Ordinal);
        Assert.Contains("DeepSettingsReadOnly", source, StringComparison.Ordinal);
        Assert.Contains("CaptureAotManagedUiDeepSettingsAsync", source, StringComparison.Ordinal);
        Assert.Contains("DeepSettingsCompleted", source, StringComparison.Ordinal);
        Assert.Contains("RuntimeFeature.IsDynamicCodeSupported", source, StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSettingsDiagnostic_ExercisesEveryPreviouslyUncoveredRoute()
    {
        string source = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.AotDeepSmoke.cs");

        Assert.Contains("ExerciseAotDeepReadOnlySettingsAsync", source, StringComparison.Ordinal);
        foreach (string route in DeepSettingsRoutes)
        {
            Assert.Equal(1, CountOccurrences(source, $"\"{route}\""));
        }

        Assert.Contains("NavigateToSettingsSection(sectionTag)", source, StringComparison.Ordinal);
        Assert.Contains("_settingsSectionElements", source, StringComparison.Ordinal);
        Assert.Contains("SettingsNavigationView.SelectedItem", source, StringComparison.Ordinal);
        Assert.Contains("SettingsBreadcrumbBar.ItemsSource", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSettingsDiagnostic_UsesNonEmptyProductSearchAndActivatesExactNestedPage()
    {
        string source = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.AotDeepSmoke.cs");
        string navigation = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.Navigation.cs");

        Assert.Contains("Settings.DataBackup.Title", source, StringComparison.Ordinal);
        Assert.Contains("UpdateSettingsSearchSuggestions(searchQuery)", source, StringComparison.Ordinal);
        Assert.Contains("SettingsSearchBox.ItemsSource", source, StringComparison.Ordinal);
        Assert.Contains("BackupRestoreSettings", source, StringComparison.Ordinal);
        Assert.Contains("ActivateSettingsSearchResult", source, StringComparison.Ordinal);
        Assert.Contains("ActivateSettingsSearchResult(result, sender)", navigation, StringComparison.Ordinal);
        Assert.Contains("NavigateToSettingsSection(result.SectionTag)", navigation, StringComparison.Ordinal);
        Assert.Contains("ScheduleSettingsSearchTarget(result)", navigation, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSettingsDiagnostic_ProvesBreadcrumbProjectionAndParentReturn()
    {
        string source = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.AotDeepSmoke.cs");

        Assert.Contains("CapsuleBehaviorSettings", source, StringComparison.Ordinal);
        Assert.Contains("CapsuleMode", source, StringComparison.Ordinal);
        Assert.Contains("BreadcrumbItems", source, StringComparison.Ordinal);
        Assert.Contains("NavigateFromSettingsBreadcrumbItem", source, StringComparison.Ordinal);
        Assert.Contains("BreadcrumbParentReturned", source, StringComparison.Ordinal);
        Assert.Contains("WaitForAotFileStackRuleProjectionAsync", source, StringComparison.Ordinal);
        Assert.Contains("WaitForAotBackupSnapshotProjectionAsync", source, StringComparison.Ordinal);
        Assert.Contains("DeepSettings route begin", source, StringComparison.Ordinal);
        Assert.Contains("DeepSettings route completed", source, StringComparison.Ordinal);
        Assert.Contains("FileStackRuleCount", source, StringComparison.Ordinal);
        Assert.Contains("BackupSnapshotCount", source, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepSettingsProjection_UsesAotSafeObjectVectorsAndGeneratedBindableItems()
    {
        string navigation = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.Navigation.cs");
        string window = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.xaml.cs");
        string maintenance = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.Maintenance.cs");
        string ruleEditor = ReadRepositoryFile("src/DeskBox/ViewModels/FileStackCustomRuleEditor.cs");
        string settingsOption = ReadRepositoryFile("src/DeskBox/Models/SettingsOption.cs");
        string capsuleOptions = ReadRepositoryFile(
            "src/DeskBox/ViewModels/SettingsViewModel.CapsuleOptions.cs");
        string groupNavigation = ReadRepositoryFile(
            "src/DeskBox/ViewModels/SettingsViewModel.GroupNavigation.cs");
        string weatherData = ReadRepositoryFile("src/DeskBox/Models/WeatherData.cs");
        string fileWidgetXaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml");
        string fileStackOptions = ReadRepositoryFile(
            "src/DeskBox/ViewModels/SettingsViewModel.FileStackOptions.cs");
        string featureOptions = ReadRepositoryFile(
            "src/DeskBox/ViewModels/SettingsViewModel.FeatureOptions.cs");
        string selectionOptions = ReadRepositoryFile(
            "src/DeskBox/ViewModels/SettingsViewModel.SelectionOptions.cs");
        string weatherOptions = ReadRepositoryFile(
            "src/DeskBox/ViewModels/SettingsViewModel.WeatherOptions.cs");
        string hotkeyAndAppearance = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.HotkeyAndAppearance.cs");
        string xaml = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.xaml");
        string capsuleXaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml");
        string capsuleCodeBehind = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/CapsuleModeSettingsSection.xaml.cs");
        string bindableViewModel = ReadRepositoryFile(
            "src/DeskBox/ViewModels/SettingsViewModel.AotBindableProperties.cs");

        Assert.Contains("matches.Cast<object>().ToArray()", navigation, StringComparison.Ordinal);
        Assert.Contains("SettingsBreadcrumbBar.ItemsSource = new object[]", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsSearchBox.ItemsSource = matches;", navigation, StringComparison.Ordinal);
        Assert.Contains("rows.Cast<object>().ToArray()", maintenance, StringComparison.Ordinal);
        Assert.DoesNotContain("ItemsSource = Array.Empty<BackupSnapshotListItem>()", maintenance, StringComparison.Ordinal);
        Assert.Equal(3, CountOccurrences(window, "[WinRT.GeneratedBindableCustomProperty]"));
        Assert.Contains("private sealed partial record SettingsSearchResult", window, StringComparison.Ordinal);
        Assert.Contains("private sealed partial record SettingsBreadcrumbItem", window, StringComparison.Ordinal);
        Assert.Contains("private sealed partial record BackupSnapshotListItem", window, StringComparison.Ordinal);
        Assert.Contains("[WinRT.GeneratedBindableCustomProperty]", ruleEditor, StringComparison.Ordinal);
        Assert.Contains("public sealed partial class SettingsOption", settingsOption, StringComparison.Ordinal);
        Assert.Contains(
            "public sealed partial record CapsuleOverrideSettingsItem",
            capsuleOptions,
            StringComparison.Ordinal);
        Assert.Contains(
            "public sealed partial record WidgetGroupSettingsItem",
            groupNavigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "public sealed partial record WidgetGroupMemberSettingsItem",
            groupNavigation,
            StringComparison.Ordinal);
        Assert.Contains(
            "public sealed partial class WeatherCitySearchResult",
            weatherData,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsOn=\"{x:Bind ViewModel.FileStacksEnabled, Mode=TwoWay}\"",
            fileWidgetXaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{x:Bind ViewModel.AvailableFileWidgetFolderOpenBehaviorOptionItems, Mode=OneWay}\"",
            fileWidgetXaml,
            StringComparison.Ordinal);
        Assert.Contains("AvailableFileStackPopoverLayoutOptions", fileStackOptions, StringComparison.Ordinal);
        Assert.Contains(
            "AvailableFileWidgetFolderOpenBehaviorOptions.Cast<object>().ToArray()",
            featureOptions,
            StringComparison.Ordinal);
        Assert.Contains("nameof(AvailableFileWidgetFolderOpenBehaviorOptionItems)", selectionOptions, StringComparison.Ordinal);
        Assert.Contains(
            "nameof(AvailableFileWidgetFolderOpenBehaviorOptionItems)",
            selectionOptions,
            StringComparison.Ordinal);
        Assert.Contains(
            "ObservableCollection<WeatherCitySearchResult> WeatherCitySuggestions",
            weatherOptions,
            StringComparison.Ordinal);
        Assert.Contains(
            "WeatherCitySuggestions.Cast<object>().ToArray()",
            weatherOptions,
            StringComparison.Ordinal);
        Assert.Contains("RefreshWeatherCitySuggestionItems()", weatherOptions, StringComparison.Ordinal);
        Assert.Contains("WeatherCitySuggestions[0]", hotkeyAndAppearance, StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{Binding WeatherCitySuggestionItems}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ItemsSource=\"{x:Bind ViewModel.FileStackCustomRules, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Equal(305, CountOccurrences(bindableViewModel, "nameof("));
        Assert.DoesNotContain("nameof(WidgetCapsuleModeEnabled)", bindableViewModel, StringComparison.Ordinal);
        Assert.Contains("nameof(SelectedWidgetCapsuleBarPlacement)", bindableViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(ResetAllCapsuleOverridesCommand)", bindableViewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("nameof(ResetCapsuleWidthOverridesCommand)", bindableViewModel, StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{x:Bind ViewModel.ResetAllCapsuleOverridesCommand, Mode=OneWay}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "Command=\"{x:Bind ViewModel.ResetCapsuleWidthOverridesCommand, Mode=OneWay}\"",
            capsuleXaml,
            StringComparison.Ordinal);
        Assert.Contains("ViewModelProperty", capsuleCodeBehind, StringComparison.Ordinal);
        Assert.Contains("CapsuleModeSection.ViewModel = ViewModel", window, StringComparison.Ordinal);
    }

    [Fact]
    public void BindableSettingsViewModelInventory_CoversEveryDirectSettingsBinding()
    {
        string settingsWindowPath = TestPaths.FromRepository("src/DeskBox/Views/SettingsWindow.xaml");
        string settingsSectionsPath = TestPaths.FromRepository("src/DeskBox/Views/SettingsSections");
        string[] xamlFiles =
        [
            settingsWindowPath,
            .. Directory.GetFiles(settingsSectionsPath, "*.xaml", SearchOption.TopDirectoryOnly)
        ];
        HashSet<string> publicViewModelProperties = typeof(DeskBox.ViewModels.SettingsViewModel)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(property => property.Name)
            .ToHashSet(StringComparer.Ordinal);
        string[] requiredBindingProperties = xamlFiles
            .SelectMany(path => System.Text.RegularExpressions.Regex.Matches(
                File.ReadAllText(path),
                @"\{Binding\s+(?:Path=)?([A-Za-z_][A-Za-z0-9_]*)"))
            .Select(match => match.Groups[1].Value)
            .Where(publicViewModelProperties.Contains)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] generatedBindableProperties = System.Text.RegularExpressions.Regex.Matches(
                ReadRepositoryFile("src/DeskBox/ViewModels/SettingsViewModel.AotBindableProperties.cs"),
                @"nameof\(([A-Za-z_][A-Za-z0-9_]*)\)")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(requiredBindingProperties, generatedBindableProperties);
    }

    [Fact]
    public void DeepSettingsEvidence_IsAddedToTheExistingSourceGeneratedResult()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Equal(1, CountOccurrences(source, "JsonSerializer.Serialize("));
        Assert.Contains("AotManagedUiDeepSettingsEvidence", source, StringComparison.Ordinal);
        Assert.Contains("DeepSettings", source, StringComparison.Ordinal);
        Assert.Contains("SearchSuggestions", source, StringComparison.Ordinal);
        Assert.Contains("PageTransitions", source, StringComparison.Ordinal);
        Assert.Contains("FileStackRuleCount", source, StringComparison.Ordinal);
        Assert.Contains("BackupSnapshotCount", source, StringComparison.Ordinal);
        Assert.Contains("AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedUiScript_SupportsDeepScenarioAndKeepsOwnedProductionIsolation()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("[ValidateSet(\"BasicReadOnly\", \"DeepSettingsReadOnly\"", script, StringComparison.Ordinal);
        Assert.Contains("\"RecycleBinMenuPersistenceRestart\"", script, StringComparison.Ordinal);
        Assert.Contains("deep-settings-read-only", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", script, StringComparison.Ordinal);
        Assert.Contains(".deskbox-aot-managed-ui-owned.json", script, StringComparison.Ordinal);
        Assert.Contains("deepSettings", script, StringComparison.Ordinal);
        Assert.Contains("pageTransitions", script, StringComparison.Ordinal);
        Assert.Contains("searchSuggestions", script, StringComparison.Ordinal);
        Assert.Contains("aot-5b4b1-design", script, StringComparison.Ordinal);
        Assert.Contains("runtimeFailureLogLines", script, StringComparison.Ordinal);
        Assert.Contains("Unhandled exception:", script, StringComparison.Ordinal);
        Assert.Contains("[DataBackup] Snapshot inventory failed:", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DeepScenario_RemainsReadOnlyAndDoesNotEnterStage5B4B2Mutations()
    {
        string source = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.AotDeepSmoke.cs");
        string runner = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.DoesNotContain("SettingsService.Save", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateWidget", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteWidget", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TrySet", source, StringComparison.Ordinal);
        Assert.DoesNotContain("QuickCaptureStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("TodoWidgetStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DeepSettingsReadOnlyMutation", runner, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonInventory_RemainsAtTheStage5B4AOneCallBoundary()
    {
        string baseline = ReadRepositoryFile("tests/DeskBox.Tests/JsonSerializationBaselineContractTests.cs");
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Contains("Assert.Equal(28, actual.Count);", baseline, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(64, actual.Values.Sum());", baseline, StringComparison.Ordinal);
        Assert.Contains("\"src/DeskBox/App.AotManagedUiSmoke.cs\"", baseline, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(source, "JsonSerializer.Serialize("));
    }

    [Fact]
    public void Stage5B4B1_ProfileSchemaProjectAndLauncherAdvanceTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 58", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("deep settings", project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AuditFreezesDeepSettingsSourcesRoutesProjectionAndWarnings()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("stage5B4B1SourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1RequiredRunnerPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1RequiredSettingsPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1RequiredProjectionPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1RequiredInventoryPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1RequiredBindableTypePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1RequiredFileStackXamlPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1RequiredFileWidgetProjectionPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1RequiredWeatherProjectionPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1RequiredCommandXamlPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1RequiredCapsuleCommandXamlPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1RequiredCapsuleCodeBehindPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1ExpectedBindableViewModelPropertyCount = 305", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1RequiredSmokeScriptPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1MissingRoutePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1UnsafeMutationPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1SourceWarningMessages", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4B1ExpectedWmc1510Count = 1241", audit, StringComparison.Ordinal);
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
