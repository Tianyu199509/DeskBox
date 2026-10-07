namespace DeskBox.Tests;

public sealed class PerformanceSettingsContractTests
{
    [Fact]
    public void GeneralPage_PlacesPerformanceAfterStartupAndBeforeAttachmentStorage()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsWindow.xaml");

        int attachment = xaml.IndexOf(
            "Settings.AttachmentStorageMode.Title",
            StringComparison.Ordinal);
        int performance = xaml.IndexOf(
            "Tag=\"PerformanceSettings\"",
            StringComparison.Ordinal);
        int autoStart = xaml.IndexOf(
            "Settings.AutoStart.Title",
            StringComparison.Ordinal);

        Assert.True(attachment >= 0);
        Assert.True(performance > autoStart);
        Assert.True(attachment > performance);
        Assert.True(autoStart >= 0);
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

        Assert.Contains(
            "x:Name=\"PerformanceSettingsSection\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedHiddenCacheCleanupDelaySeconds",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedHiddenCacheCleanupScope",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "AvailableHiddenCacheCleanupScopeOptions",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedVisibleIdleCacheCleanupDelaySeconds",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "SelectedTransientWindowReleaseDelaySeconds",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "SelectedPerformanceCacheBudget",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContinuousDecorativeAnimationsSummaryText",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "ContinuousDecorativeAnimationsDropDown_Click",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(
            "[\"PerformanceSettings\"] = new(\"PerformanceSettings\", \"Settings.Performance.Title\", \"General\", \"General\")",
            window,
            StringComparison.Ordinal);
        Assert.Contains(
            "x:Key=\"PerformanceSettingsSectionTemplate\"",
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
