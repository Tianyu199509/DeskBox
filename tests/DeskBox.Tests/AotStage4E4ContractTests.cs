namespace DeskBox.Tests;

public sealed class AotStage4E4ContractTests
{
    [Fact]
    public void FileWidgetSettingsSection_DeclaresTypedViewModelDependencyProperty()
    {
        string code = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml.cs");

        Assert.Contains("using DeskBox.Features.FileStack;", code, StringComparison.Ordinal);
        Assert.Contains("using DeskBox.Features.FeatureWidgets;", code, StringComparison.Ordinal);
        Assert.Contains("using DeskBox.Features.Interaction;", code, StringComparison.Ordinal);
        Assert.Contains(
            "public static readonly DependencyProperty FileStackProperty",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "public static readonly DependencyProperty FeatureWidgetsProperty",
            code,
            StringComparison.Ordinal);
        Assert.Contains(
            "public static readonly DependencyProperty InteractionProperty",
            code,
            StringComparison.Ordinal);
        Assert.Contains("nameof(FileStack)", code, StringComparison.Ordinal);
        Assert.Contains("nameof(FeatureWidgets)", code, StringComparison.Ordinal);
        Assert.Contains("nameof(Interaction)", code, StringComparison.Ordinal);
        Assert.Contains("typeof(FileStackSettingsViewModel)", code, StringComparison.Ordinal);
        Assert.Contains("typeof(FeatureWidgetsSettingsViewModel)", code, StringComparison.Ordinal);
        Assert.Contains("typeof(InteractionSettingsViewModel)", code, StringComparison.Ordinal);
        Assert.Contains("new PropertyMetadata(null)", code, StringComparison.Ordinal);
        Assert.Contains("public FileStackSettingsViewModel? FileStack", code, StringComparison.Ordinal);
        Assert.Contains("public FeatureWidgetsSettingsViewModel? FeatureWidgets", code, StringComparison.Ordinal);
        Assert.Contains("public InteractionSettingsViewModel? Interaction", code, StringComparison.Ordinal);
    }

    [Fact]
    public void FileWidgetSettingsSection_LeavesBridgeTrackingToGeneratedBindings()
    {
        string code = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml.cs");

        Assert.DoesNotContain("OnViewModelChanged", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Bindings.Update()", code, StringComparison.Ordinal);
    }

    [Fact]
    public void FileWidgetSettingsSection_UsesThreeObservableOneWayBindings()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml");

        Assert.Equal(
            1,
            CountOccurrences(
                xaml,
                "{x:Bind FileStack.SettingsSummaryText, Mode=OneWay}"));
        Assert.Equal(
            1,
            CountOccurrences(
                xaml,
                "{x:Bind FeatureWidgets.AvailableFolderOpenBehaviorOptionItems, Mode=OneWay}"));
    }

    [Fact]
    public void FileWidgetSettingsSection_PreservesTwoAttachedPropertyTwoWayBindings()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml");

        Assert.Equal(
            1,
            CountOccurrences(
                xaml,
                "controls:SettingsComboBox.Value=\"{x:Bind FileStack.OpenMode, Mode=TwoWay}\""));
        Assert.Equal(
            1,
            CountOccurrences(
                xaml,
                "{x:Bind FeatureWidgets.FolderOpenBehavior, Mode=TwoWay}"));
    }

    [Fact]
    public void FileWidgetSettingsSection_HasNoLegacyRuntimeBindings()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml");

        Assert.DoesNotContain("{Binding ", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{x:Bind ViewModel.", xaml, StringComparison.Ordinal);
        Assert.Equal(6, CountOccurrences(xaml, "{x:Bind "));
    }

    [Fact]
    public void SettingsWindow_AssignsAndClearsTheTypedBridgeAroundViewModelLifetime()
    {
        string code = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.xaml.cs");
        string factory = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.DeferredSections.cs");

        int rootCreation = factory.IndexOf("template.LoadContent()", StringComparison.Ordinal);
        int rootAssignment = factory.IndexOf(
            "section.DataContext = ViewModel;",
            StringComparison.Ordinal);
        int bridgeAssignment = factory.IndexOf(
            "fileSettings.FileStack = _fileStackSettingsViewModel;",
            StringComparison.Ordinal);
        int rootAttachment = factory.IndexOf("ContentHost.Children.Add(section);", StringComparison.Ordinal);
        int bridgeClear = code.IndexOf(
            "AppearanceDetailSection.FileStack = null;",
            StringComparison.Ordinal);
        // Match the shell property, not a child such as _searchSettingsViewModel.
        var disposeCall = System.Text.RegularExpressions.Regex.Match(
            code, @"(?m)^[ \t]*ViewModel\.Dispose\(\);");
        Assert.True(disposeCall.Success);
        int viewModelDispose = disposeCall.Index;

        Assert.True(rootCreation >= 0 && rootAssignment > rootCreation);
        Assert.True(bridgeAssignment > rootAssignment && bridgeAssignment < rootAttachment);
        Assert.True(bridgeClear >= 0 && bridgeClear < viewModelDispose);
    }

    [Fact]
    public void SectionEditors_NotifyCurrentCompiledBindingLeaves()
    {
        string fileStack = ReadRepositoryFile(
            "src/DeskBox/Features/FileStack/FileStackSettingsViewModel.cs");
        string featureWidgets = ReadRepositoryFile(
            "src/DeskBox/Features/FeatureWidgets/FeatureWidgetsSettingsViewModel.cs");
        string interaction = ReadRepositoryFile(
            "src/DeskBox/Features/Interaction/InteractionSettingsViewModel.cs");

        Assert.Contains("OnPropertyChanged(nameof(SettingsSummaryText));", fileStack, StringComparison.Ordinal);
        Assert.Contains("SetProperty(ref _autoStacking", fileStack, StringComparison.Ordinal);
        Assert.Contains("SetProperty(", featureWidgets, StringComparison.Ordinal);
        Assert.Contains("_folderOpenBehavior", featureWidgets, StringComparison.Ordinal);
        Assert.Contains(
            "OnPropertyChanged(nameof(AvailableFolderOpenBehaviorOptionItems));",
            featureWidgets,
            StringComparison.Ordinal);
        Assert.Contains("SetProperty(ref _fileItemContextMenuEnabled", interaction, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsComboBox_RetainsQueuedSelectionAndTargetToSourceSemantics()
    {
        string code = ReadRepositoryFile("src/DeskBox/Controls/SettingsComboBox.cs");

        Assert.Contains("DependencyProperty.RegisterAttached(", code, StringComparison.Ordinal);
        Assert.Contains("new PropertyMetadata(null, OnValueChanged)", code, StringComparison.Ordinal);
        Assert.Contains("QueueValueRefresh();", code, StringComparison.Ordinal);
        Assert.Contains("ItemsControl.ItemsSourceProperty", code, StringComparison.Ordinal);
        Assert.Contains("_comboBox.SelectionChanged += OnSelectionChanged;", code, StringComparison.Ordinal);
        Assert.Contains("SetValue(_comboBox, option.Value);", code, StringComparison.Ordinal);
        Assert.Contains("ApplyValueToSelection();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void RemainingRuntimeAndStyleBindings_StayExplicitlyDeferred()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml");
        string contentWindow = ReadRepositoryFile("src/DeskBox/Views/ContentWidgetWindow.xaml");
        Assert.Equal(0, CountOccurrences(app, "{Binding "));
        Assert.Equal(1, CountOccurrences(contentWindow, "{Binding "));
    }

    [Fact]
    public void AotAudit_DeclaresTheStage4E4ViewModelBridgeContract()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 59", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E4SourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E4LegacyBindingSourceMatches", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E4MissingCompiledBindings", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E4MissingViewModelBridgePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E4MissingBehaviorPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E4MissingDeferredBindings", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E4SourceWarningMessages", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAotBuild_DeclaresTheStage4E4WarningReduction()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$stage4E4MaximumWmc1510Count = 802", audit, StringComparison.Ordinal);
        Assert.Contains("Stage 4E-4 WMC1510 count regressed above its ceiling", audit, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("three typed section-editor bridges", project, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DeskBoxRustNative=true", project, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int startIndex = 0;
        while ((startIndex = source.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
    }

    private static string ReadRepositoryFile(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));
}
