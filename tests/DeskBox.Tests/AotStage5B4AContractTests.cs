namespace DeskBox.Tests;

public sealed class AotStage5B4AContractTests
{
    [Fact]
    public void ManagedUiSmoke_IsNativeAotOnlyAndRequiresPreviewRoot()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains("DESKBOX_AOT_MANAGED_UI_SMOKE", source, StringComparison.Ordinal);
        Assert.Contains("BasicReadOnly", source, StringComparison.Ordinal);
        Assert.Contains(
            "DeskBoxDataPathService.AotPreviewRootEnvironmentVariable",
            source,
            StringComparison.Ordinal);
        Assert.Contains("RefusedNonPreviewRoot", source, StringComparison.Ordinal);
        Assert.Contains("aot-managed-ui-smoke", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedUiSmoke_ProvesTrayAndSeededWidgetRestoreWithoutCreatingWidgets()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Contains("aot-5b4a-file", source, StringComparison.Ordinal);
        Assert.Contains("aot-5b4a-search", source, StringComparison.Ordinal);
        Assert.Contains("_trayIcon.TrayIcon.WindowHandle", source, StringComparison.Ordinal);
        Assert.Contains("WidgetManager.CreateDiagnosticsSnapshot()", source, StringComparison.Ordinal);
        Assert.Contains("WidgetKind.File", source, StringComparison.Ordinal);
        Assert.Contains("WidgetKind.Search", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateWidgetOfKindAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateManagedWidgetAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingsService.Save", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedUiSmoke_NavigatesAllSixMainSettingsSectionsThroughProductEntry()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Contains("ShowSettings(sectionTag)", source, StringComparison.Ordinal);
        foreach (string section in new[]
                 {
                     "General",
                     "Appearance",
                     "FeatureWidgets",
                     "Interaction",
                     "Maintenance",
                     "About"
                 })
        {
            Assert.Contains($"\"{section}\"", source, StringComparison.Ordinal);
        }

        Assert.Contains("CaptureAotSmokeSnapshot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsWindowDiagnostic_RequiresVisibleHwndLoadedRootAndCurrentSection()
    {
        string source = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.AotSmoke.cs");
        string navigation = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.Navigation.cs");

        Assert.Contains("#if DESKBOX_NATIVE_AOT", source, StringComparison.Ordinal);
        Assert.Contains("WindowNative.GetWindowHandle(this)", source, StringComparison.Ordinal);
        Assert.Contains("_appWindow.IsVisible", source, StringComparison.Ordinal);
        Assert.Contains("SettingsRoot.XamlRoot", source, StringComparison.Ordinal);
        Assert.Contains("_currentSettingsSection", source, StringComparison.Ordinal);
        Assert.Contains("_settingsSectionElements", source, StringComparison.Ordinal);
        Assert.Contains("SettingsNavigationView.SelectedItem", source, StringComparison.Ordinal);
        Assert.Contains("SettingsSearchBox.ItemsSource = null;", navigation, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SettingsSearchBox.ItemsSource = Array.Empty<SettingsSearchResult>();",
            navigation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedUiSmoke_OpensSearchWithLocalizedGuaranteedActionAndWaitsForResults()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Contains("Search.Action.OpenSettings", source, StringComparison.Ordinal);
        Assert.Contains("OpenSearchPopupWithQuery(searchQuery)", source, StringComparison.Ordinal);
        Assert.Contains("WaitForManagedUiSearchAsync", source, StringComparison.Ordinal);
        Assert.Contains("HasOpenSettingsAction", source, StringComparison.Ordinal);
        Assert.Contains("SearchCompleted", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordQuery", source, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenSelected", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchWindowDiagnostic_ExercisesEveryFilterAndDoubleClicksEverySortColumn()
    {
        string source = ReadRepositoryFile("src/DeskBox/Views/SearchPopupWindow.AotSmoke.cs");

        foreach (string filter in new[]
                 {
                     "All",
                     "FilesAndFolders",
                     "Apps",
                     "Images",
                     "Documents",
                     "DeskBox"
                 })
        {
            Assert.Contains($"\"{filter}\"", source, StringComparison.Ordinal);
        }

        foreach (string handler in new[]
                 {
                     "SortNameHeader_Click",
                     "SortSizeHeader_Click",
                     "SortDateHeader_Click",
                     "SortTypeHeader_Click"
                 })
        {
            Assert.Equal(2, CountOccurrences(source, handler + "("));
        }

        Assert.Contains("ResultFilterComboBox.SelectedItem", source, StringComparison.Ordinal);
        Assert.Contains("ResultFilterBar.Visibility", source, StringComparison.Ordinal);
        Assert.Contains("SortHeaderRow.Visibility", source, StringComparison.Ordinal);
        Assert.Contains("ActionId == \"open-settings\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void LocaleDiagnostic_LoadsEveryShippedDictionaryWithoutChangingLanguage()
    {
        string source = ReadRepositoryFile("src/DeskBox/Services/LocalizationService.cs");

        Assert.Contains("CaptureAotSmokeResourceDiagnostics", source, StringComparison.Ordinal);
        foreach (string locale in new[]
                 {
                     "zh-CN", "zh-TW", "en-US", "ja-JP", "de-DE", "pt-BR",
                     "hi-IN", "es-ES", "fr-FR", "ar-SA", "bn-BD", "ru-RU"
                 })
        {
            Assert.Contains($"\"{locale}\"", source, StringComparison.Ordinal);
        }

        Assert.Contains("Window.Settings.Title", source, StringComparison.Ordinal);
        Assert.Contains("Search.Action.OpenSettings", source, StringComparison.Ordinal);
        string diagnostic = source[
            source.IndexOf("CaptureAotSmokeResourceDiagnostics", StringComparison.Ordinal)..];
        Assert.DoesNotContain("SetLanguage(", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void ManagedUiSmoke_WritesOnlySourceGeneratedStructuredEvidence()
    {
        string source = ReadRepositoryFile("src/DeskBox/App.AotManagedUiSmoke.cs");

        Assert.Equal(1, CountOccurrences(source, "JsonSerializer.Serialize("));
        Assert.Contains(
            "AotManagedUiSmokeJsonContext.Default.AotManagedUiSmokeResult",
            source,
            StringComparison.Ordinal);
        Assert.Contains("JsonSourceGenerationMode.Metadata", source, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonSerializerOptions", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Deserialize", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AppLaunch_SchedulesManagedUiSmokeAfterAllNativeBoundarySmokes()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml.cs");
        int sessionMutation = app.IndexOf(
            "StartAotMusicVolumeSessionMutationSmokeIfRequested();",
            StringComparison.Ordinal);
        int managedUi = app.IndexOf(
            "StartAotManagedUiSmokeIfRequested();",
            StringComparison.Ordinal);

        Assert.True(sessionMutation >= 0 && managedUi > sessionMutation);
    }

    [Fact]
    public void ManagedUiScript_SeedsOwnedPreviewAndValidatesEvidenceAndProductionIsolation()
    {
        string script = ReadRepositoryFile("scripts/run-aot-managed-ui-smoke.ps1");

        Assert.Contains("DESKBOX_AOT_MANAGED_UI_SMOKE", script, StringComparison.Ordinal);
        Assert.Contains("BasicReadOnly", script, StringComparison.Ordinal);
        Assert.Contains("aot-5b4a-file", script, StringComparison.Ordinal);
        Assert.Contains("aot-5b4a-search", script, StringComparison.Ordinal);
        Assert.Contains("HasCompletedOnboarding", script, StringComparison.Ordinal);
        Assert.Contains("FeatureWidgetEnabledStates", script, StringComparison.Ordinal);
        Assert.Contains("SearchSaveHistory", script, StringComparison.Ordinal);
        Assert.Contains("Get-DirectoryStateFingerprint", script, StringComparison.Ordinal);
        Assert.Contains("Stop-ExactPreviewProcess", script, StringComparison.Ordinal);
        Assert.Contains("productionDataFingerprintBefore", script, StringComparison.Ordinal);
        Assert.Contains("session.json", script, StringComparison.Ordinal);
        Assert.Contains("settingsSections", script, StringComparison.Ordinal);
        Assert.Contains("filterTransitions", script, StringComparison.Ordinal);
        Assert.Contains("sortTransitions", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAotSmokeScript_ClearsAndRestoresAllSevenOptIns()
    {
        string[] scripts =
        [
            "scripts/run-aot-shortcut-smoke.ps1",
            "scripts/run-aot-shell-smoke.ps1",
            "scripts/run-aot-quick-access-mutation-smoke.ps1",
            "scripts/run-aot-music-volume-read-smoke.ps1",
            "scripts/run-aot-music-volume-mutation-smoke.ps1",
            "scripts/run-aot-music-volume-session-mutation-smoke.ps1",
            "scripts/run-aot-managed-ui-smoke.ps1"
        ];

        foreach (string scriptPath in scripts)
        {
            string source = ReadRepositoryFile(scriptPath);
            Assert.Contains("DESKBOX_AOT_MANAGED_UI_SMOKE", source, StringComparison.Ordinal);
            Assert.Contains("previousManagedUiSmoke", source, StringComparison.Ordinal);
            Assert.Contains("DESKBOX_AOT_MUSIC_VOLUME_SESSION_MUTATION_SMOKE", source, StringComparison.Ordinal);
            Assert.Contains("DESKBOX_AOT_MUSIC_VOLUME_MUTATION_SMOKE", source, StringComparison.Ordinal);
            Assert.Contains("DESKBOX_AOT_MUSIC_VOLUME_READ_SMOKE", source, StringComparison.Ordinal);
            Assert.Contains("DESKBOX_AOT_QUICK_ACCESS_MUTATION_SMOKE", source, StringComparison.Ordinal);
            Assert.Contains("DESKBOX_AOT_SHELL_SMOKE", source, StringComparison.Ordinal);
            Assert.Contains("DESKBOX_AOT_SHORTCUT_SMOKE", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Stage5B4A_ProfileSchemaProjectAndLauncherAreAdvancedTogether()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string launcher = ReadRepositoryFile("scripts/start-aot-preview.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$auditProfileVersion = 59", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage5B4A", audit, StringComparison.Ordinal);
        Assert.Contains("$RequiredAuditProfileVersion = 59", launcher, StringComparison.Ordinal);
        Assert.Contains("$RequiredSummarySchemaVersion = 55", launcher, StringComparison.Ordinal);
        Assert.Contains("stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("managed UI", project, StringComparison.OrdinalIgnoreCase);
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
