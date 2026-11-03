namespace DeskBox.Tests;

public sealed class PerformanceSettingsContractTests
{
    [Fact]
    public void GeneralPage_PlacesPerformanceAfterStartupAndMovesAttachmentStorageToMaintenance()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.xaml");

        int quickCapture = xaml.IndexOf(
            "x:Key=\"QuickCaptureSettingsSectionTemplate\"",
            StringComparison.Ordinal);
        int quickCaptureAttachment = xaml.IndexOf(
            "x:Name=\"QuickCaptureAttachmentStorageModeComboBox\"",
            quickCapture,
            StringComparison.Ordinal);
        int todo = xaml.IndexOf(
            "x:Key=\"TodoSettingsSectionTemplate\"",
            StringComparison.Ordinal);
        int todoAttachment = xaml.IndexOf(
            "x:Name=\"TodoAttachmentStorageModeComboBox\"",
            todo,
            StringComparison.Ordinal);
        int maintenance = xaml.IndexOf(
            "x:Key=\"MaintenanceSectionTemplate\"",
            StringComparison.Ordinal);
        int maintenanceEnd = xaml.IndexOf(
            "x:Key=\"AboutSectionTemplate\"",
            maintenance,
            StringComparison.Ordinal);
        string maintenanceSlice = xaml[maintenance..maintenanceEnd];

        int general = xaml.IndexOf(
            "x:Name=\"GeneralSection\"",
            StringComparison.Ordinal);
        int autoStart = xaml.IndexOf(
            "Settings.AutoStart.Title",
            general,
            StringComparison.Ordinal);
        int silentStartup = xaml.IndexOf(
            "Settings.SilentStartup.Title",
            general,
            StringComparison.Ordinal);
        int performance = xaml.IndexOf(
            "x:Name=\"PerformanceInlineCard\"",
            general,
            StringComparison.Ordinal);
        int aiEntry = xaml.IndexOf(
            "x:Name=\"AiSettingsEntryCard\"",
            general,
            StringComparison.Ordinal);

        Assert.True(quickCapture >= 0 && quickCaptureAttachment > quickCapture);
        Assert.True(todo >= 0 && todoAttachment > todo);
        Assert.DoesNotContain("Settings.AttachmentStorageMode.Title", maintenanceSlice, StringComparison.Ordinal);
        Assert.True(general >= 0 && autoStart > general);
        Assert.True(silentStartup > autoStart);
        Assert.True(performance > silentStartup);
        Assert.True(aiEntry > performance);
        Assert.Contains(
            "ItemsSource=\"{Binding AvailablePerformanceModeOptions}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "controls:SettingsComboBox.Value=\"{Binding SelectedPerformanceMode, Mode=TwoWay}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PerformanceDrillDown_ContainsAllDetailedControlsAndRouteMetadata()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.xaml");
        string window = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.xaml.cs");
        string navigation = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.Navigation.cs");

        Assert.DoesNotContain(
            "x:Key=\"PerformanceSettingsSectionTemplate\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[\"PerformanceSettings\"]",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"PerformanceSettings\" => \"General\"",
            navigation,
            StringComparison.Ordinal);

        int perfExpanderStart = xaml.IndexOf("x:Name=\"PerformanceInlineCard\"", StringComparison.Ordinal);
        int perfExpanderEnd = xaml.IndexOf("</toolkit:SettingsExpander>", perfExpanderStart, StringComparison.Ordinal);
        Assert.True(perfExpanderStart >= 0 && perfExpanderEnd > perfExpanderStart);
        string perfExpanderSlice = xaml[perfExpanderStart..perfExpanderEnd];

        Assert.Contains("SelectedPerformanceMode", perfExpanderSlice, StringComparison.Ordinal);
        Assert.Contains("KeepWarmCacheWhenHidden", perfExpanderSlice, StringComparison.Ordinal);
        Assert.Contains("SelectedPerformanceCacheBudget", perfExpanderSlice, StringComparison.Ordinal);
        Assert.Contains("SelectedVisibleIdleCacheCleanupDelaySeconds", perfExpanderSlice, StringComparison.Ordinal);
        Assert.Contains("IdleWorkingSetTrimEnabled", perfExpanderSlice, StringComparison.Ordinal);
        Assert.Contains("AllowTextMarquee", perfExpanderSlice, StringComparison.Ordinal);
        Assert.Contains("AllowVinylRotation", perfExpanderSlice, StringComparison.Ordinal);
        Assert.Contains("AllowGlanceRotation", perfExpanderSlice, StringComparison.Ordinal);
        Assert.Contains("AllowCompactAmbient", perfExpanderSlice, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SelectedTransientWindowReleaseDelaySeconds",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "EnsureSettingsSectionCreated(visibleSectionTag)",
            navigation,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimePolicy_ControlsRetentionAndOnlyContinuousDecoration()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml.cs");
        string shell = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetShell.xaml.cs");
        string collapse = ReadRepositoryFile(
            "src/DeskBox/Views/WidgetWindowBase.Collapse.cs");
        string musicAdapter = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/MusicWidgetContentAdapter.cs");
        string glance = ReadRepositoryFile(
            "src/DeskBox/ViewModels/GlanceWidgetViewModel.cs");

        Assert.Contains(
            "PerformanceSettingsPolicy.Resolve(app.SettingsService.Settings)",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "BackgroundMemoryCleanupDisabled",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TryRunHiddenWorkingSetTrimAsync(",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ScheduleTransientWindowRelease()",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GC.Collect(",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "TryTrimCurrentProcessWorkingSet",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "RunLongHiddenNoRebuildMaintenance()",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReleaseHiddenCaches()",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReleaseHiddenShellKindCache()",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "ReleaseHiddenMetadataCache()",
            app,
            StringComparison.Ordinal);
        Assert.Contains(
            "ConfigurePerformanceCacheBudget(",
            app,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "BackgroundMemoryCleanupDelaySeconds",
            app,
            StringComparison.Ordinal);
        Assert.Contains("TextMarqueeAnimationsEnabled()", shell, StringComparison.Ordinal);
        Assert.Contains("VinylRotationAnimationsEnabled()", shell, StringComparison.Ordinal);
        Assert.Contains("CompactAmbientAnimationsEnabled()", shell, StringComparison.Ordinal);
        Assert.Contains("AllowGlanceImageAutoRotation", glance, StringComparison.Ordinal);
        Assert.Contains("UpdateRotationTimer();", glance, StringComparison.Ordinal);
        Assert.Contains(
            "IWidgetPerformanceAwareContent",
            musicAdapter,
            StringComparison.Ordinal);

        Assert.Contains(
            "WidgetCompactTransitionVisualProfile.Resolve(",
            collapse,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "PerformanceSettingsPolicy",
            collapse,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PerformanceOptions_ExposeOnlyFiniteCleanupAndActivePresets()
    {
        // Batch 50 moved the performance option tables onto the section
        // editor; the shell partial is gone.
        string viewModel = ReadRepositoryFile(
            "src/DeskBox/Features/Performance/PerformanceSettingsViewModel.cs");

        Assert.DoesNotContain(
            "ModeBestVisual",
            viewModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "CleanupNever",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "PerformanceOptionKinds.ModeBalanced,",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "PerformanceOptionKinds.ModeResourceSaver,",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "AvailableContinuousDecorativeAnimationOptions",
            viewModel,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
