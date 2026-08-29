namespace DeskBox.Tests;

public sealed class AotStage4E4ContractTests
{
    [Fact]
    public void FileWidgetSettingsSection_DeclaresTypedViewModelDependencyProperty()
    {
        string code = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml.cs");

        Assert.Contains("using DeskBox.ViewModels;", code, StringComparison.Ordinal);
        Assert.Contains(
            "public static readonly DependencyProperty ViewModelProperty",
            code,
            StringComparison.Ordinal);
        Assert.Contains("nameof(ViewModel)", code, StringComparison.Ordinal);
        Assert.Contains("typeof(SettingsViewModel)", code, StringComparison.Ordinal);
        Assert.Contains("new PropertyMetadata(null)", code, StringComparison.Ordinal);
        Assert.Contains("public SettingsViewModel? ViewModel", code, StringComparison.Ordinal);
        Assert.Contains("(SettingsViewModel?)GetValue(ViewModelProperty)", code, StringComparison.Ordinal);
        Assert.Contains("SetValue(ViewModelProperty, value);", code, StringComparison.Ordinal);
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
                "{x:Bind ViewModel.FileStackSettingsSummaryText, Mode=OneWay}"));
        Assert.Equal(
            1,
            CountOccurrences(
                xaml,
                "{x:Bind ViewModel.AvailableFileWidgetFolderOpenBehaviorOptionItems, Mode=OneWay}"));
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
                "{x:Bind ViewModel.FileStacksEnabled, Mode=TwoWay}"));
        Assert.Equal(
            1,
            CountOccurrences(
                xaml,
                "{x:Bind ViewModel.SelectedFileWidgetFolderOpenBehavior, Mode=TwoWay}"));
    }

    [Fact]
    public void FileWidgetSettingsSection_HasNoLegacyRuntimeBindings()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/FileWidgetSettingsSection.xaml");

        Assert.DoesNotContain("{Binding ", xaml, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(xaml, "{x:Bind ViewModel."));
    }

    [Fact]
    public void SettingsWindow_AssignsAndClearsTheTypedBridgeAroundViewModelLifetime()
    {
        string code = ReadRepositoryFile("src/DeskBox/Views/SettingsWindow.xaml.cs");

        int rootAssignment = code.IndexOf(
            "SettingsRoot.DataContext = ViewModel;",
            StringComparison.Ordinal);
        int bridgeAssignment = code.IndexOf(
            "AppearanceDetailSection.ViewModel = ViewModel;",
            StringComparison.Ordinal);
        int bridgeClear = code.IndexOf(
            "AppearanceDetailSection.ViewModel = null;",
            StringComparison.Ordinal);
        int viewModelDispose = code.IndexOf("ViewModel.Dispose();", StringComparison.Ordinal);

        Assert.True(rootAssignment >= 0 && bridgeAssignment > rootAssignment);
        Assert.True(bridgeClear >= 0 && bridgeClear < viewModelDispose);
    }

    [Fact]
    public void SettingsViewModel_NotifiesAllFiveCompiledBindingLeaves()
    {
        string fileStack = ReadRepositoryFile(
            "src/DeskBox/ViewModels/SettingsViewModel.FileStackOptions.cs");
        string featureOptions = ReadRepositoryFile(
            "src/DeskBox/ViewModels/SettingsViewModel.FeatureOptions.cs");
        string selectionOptions = ReadRepositoryFile(
            "src/DeskBox/ViewModels/SettingsViewModel.SelectionOptions.cs");

        Assert.Contains("OnPropertyChanged(nameof(FileStackSettingsSummaryText));", fileStack, StringComparison.Ordinal);
        Assert.Contains("OnPropertyChanged(nameof(FileStackAutoStacking));", fileStack, StringComparison.Ordinal);
        Assert.Contains("SetProperty(", featureOptions, StringComparison.Ordinal);
        Assert.Contains("_selectedFileWidgetFolderOpenBehavior", featureOptions, StringComparison.Ordinal);
        Assert.Contains(
            "OnPropertyChanged(nameof(AvailableFileWidgetFolderOpenBehaviorOptions));",
            selectionOptions,
            StringComparison.Ordinal);
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
        Assert.Equal(2, CountOccurrences(app, "{Binding "));
        Assert.Equal(1, CountOccurrences(contentWindow, "{Binding "));
    }

    [Fact]
    public void AotAudit_DeclaresTheStage4E4ViewModelBridgeContract()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
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

        Assert.Contains("$stage4E4MaximumWmc1510Count = 1224", audit, StringComparison.Ordinal);
        Assert.Contains("Stage 4E-4 WMC1510 count regressed above its ceiling", audit, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("five typed ViewModel bridge bindings", project, StringComparison.OrdinalIgnoreCase);
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
