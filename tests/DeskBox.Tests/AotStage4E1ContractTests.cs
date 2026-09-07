namespace DeskBox.Tests;

public sealed class AotStage4E1ContractTests
{
    [Fact]
    public void PinStateIcon_UsesExactlyTwoTypedForegroundBindings()
    {
        string xaml = ReadRepositoryFile("src/DeskBox/Controls/PinStateIcon.xaml");

        Assert.Equal(2, CountOccurrences(xaml, "{x:Bind Foreground, Mode=OneWay}"));
        Assert.DoesNotContain(
            "{Binding Foreground, ElementName=Root}",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownSourceEditor_UsesTypedBindingsForItsThreeDependencyProperties()
    {
        string xaml = ReadRepositoryFile("src/DeskBox/Controls/MarkdownSourceEditor.xaml");

        Assert.Contains("FontSize=\"{x:Bind EditorFontSize, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly=\"{x:Bind IsReadOnly, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PlaceholderText=\"{x:Bind PlaceholderText, Mode=OneWay}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding EditorFontSize, ElementName=Root}", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding IsReadOnly, ElementName=Root}", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{Binding PlaceholderText, ElementName=Root}", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SelfTextTooltips_UseNamedElementCompiledBindings()
    {
        string taskView = ReadRepositoryFile(
            "src/DeskBox/Controls/DesktopOrganizationTaskView.xaml");
        string settingsSection = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml");

        Assert.Contains(
            "ToolTipService.ToolTip=\"{x:Bind StoragePathText.Text, Mode=OneWay}\"",
            taskView,
            StringComparison.Ordinal);
        Assert.Contains(
            "ToolTipService.ToolTip=\"{x:Bind RuleDetailPath.Text, Mode=OneWay}\"",
            settingsSection,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ToolTipService.ToolTip=\"{Binding Text, RelativeSource={RelativeSource Self}}\"",
            taskView,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ToolTipService.ToolTip=\"{Binding Text, RelativeSource={RelativeSource Self}}\"",
            settingsSection,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LeafBindingSources_RetainTheirDependencyPropertyAndRefreshContracts()
    {
        string pinCode = ReadRepositoryFile("src/DeskBox/Controls/PinStateIcon.xaml.cs");
        string markdownCode = ReadRepositoryFile("src/DeskBox/Controls/MarkdownSourceEditor.xaml.cs");
        string taskCode = ReadRepositoryFile(
            "src/DeskBox/Controls/DesktopOrganizationTaskView.xaml.cs");
        string settingsCode = ReadRepositoryFile(
            "src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml.cs");

        Assert.Contains("IsPinnedProperty", pinCode, StringComparison.Ordinal);
        Assert.Contains("nameof(IsPinned)", pinCode, StringComparison.Ordinal);
        Assert.Contains("EditorFontSizeProperty", markdownCode, StringComparison.Ordinal);
        Assert.Contains("IsReadOnlyProperty", markdownCode, StringComparison.Ordinal);
        Assert.Contains("PlaceholderTextProperty", markdownCode, StringComparison.Ordinal);
        Assert.Contains("StoragePathText.Text =", taskCode, StringComparison.Ordinal);
        Assert.Contains("RuleDetailPath.Text =", settingsCode, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDataContextAndStyleSetterBindings_RemainDeferred()
    {
        string appXaml = ReadRepositoryFile("src/DeskBox/App.xaml");
        string contentWindow = ReadRepositoryFile("src/DeskBox/Views/ContentWidgetWindow.xaml");

        Assert.Contains("Value=\"{Binding SegmentHeight}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{Binding SegmentTextSize}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("OverlayTitle=\"{Binding DisplayName}\"", contentWindow, StringComparison.Ordinal);
    }

    [Fact]
    public void AotAudit_DeclaresTheStage4E1LeafBindingContract()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 61", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E1SourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E1LegacyBindingSourceMatches", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E1MissingCompiledBindings", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E1SourceWarningMessages", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void AotAudit_RetainsTheStage4E1Wmc1510Ceiling()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$stage4E1MaximumWmc1510Count = 870", audit, StringComparison.Ordinal);
        Assert.Contains("Stage 4E-1 WMC1510 count regressed above its ceiling", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAotBuild_DeclaresTheStage4E1XamlBoundary()
    {
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("leaf compiled bindings", project, StringComparison.OrdinalIgnoreCase);
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
