namespace DeskBox.Tests;

/// <summary>
/// Freezes the complete WidgetConfig.Metadata key inventory through the
/// central WidgetMetadataKeys registry (pluginization roadmap section 7,
/// stage 2 data hygiene). Every key a family owns must be aliased here;
/// new keys are added together with their family constant, and the
/// QuickCaptureMasterPaneWidth literal exists exactly once (it used to be
/// declared privately in two files).
/// </summary>
public sealed class WidgetMetadataKeysContractTests
{
    [Fact]
    public void Registry_AliasesEveryKnownMetadataKey()
    {
        string registry = File.ReadAllText(TestPaths.SourceFile(
            "src/DeskBox/Services/WidgetMetadataKeys.cs"));

        string[] requiredAliases =
        [
            "ChromeMode = WidgetChromeModeNames.MetadataKey",
            "CollapseBehavior = WidgetCollapseBehaviorNames.MetadataKey",
            "FolderOpenBehavior = FileWidgetFolderOpenBehaviorNames.MetadataKey",
            "WidgetForegroundMode = WidgetForegroundSettings.ModeOverrideMetadataKey",
            "WidgetForegroundColor = WidgetForegroundSettings.ColorOverrideMetadataKey",
            "WidgetTextEdgeMode = WidgetForegroundSettings.EdgeOverrideMetadataKey",
            "FileStacksEnabled = WidgetFileStackSettings.EnabledOverrideMetadataKey",
            "FileStackGroupBy = WidgetFileStackSettings.GroupByOverrideMetadataKey",
            "FileStackThreshold = WidgetFileStackSettings.ThresholdOverrideMetadataKey",
            "FileStackOrderBy = WidgetFileStackSettings.OrderByOverrideMetadataKey",
            "FileStackOpenMode = WidgetFileStackSettings.OpenModeOverrideMetadataKey",
            "FileStackDisabledGroups = WidgetFileStackSettings.DisabledStacksMetadataKey",
            "FileStackNameOverrides = WidgetFileStackSettings.StackNameOverridesMetadataKey",
            "FileStackGroupOrder = WidgetFileStackSettings.StackOrderMetadataKey",
            "FileStackMemberOverrides = WidgetFileStackSettings.StackMemberOverridesMetadataKey",
            "WeatherViewMode = WeatherWidgetViewModeSettings.MetadataKey",
            "TodoMasterPaneWidth = \"Todo.MasterPaneWidth\"",
            "TodoTitleEditorHeight = \"Todo.TitleEditorHeight\"",
            "QuickCaptureMasterPaneWidth = \"QuickCaptureMasterPaneWidth\"",
        ];

        foreach (string alias in requiredAliases)
        {
            Assert.Contains(alias, registry, StringComparison.Ordinal);
        }

        Assert.Equal(19, requiredAliases.Length);
    }

    [Fact]
    public void QuickCaptureMasterPaneWidth_DeclaredExactlyOnce()
    {
        // The literal used to be declared privately in both
        // QuickCaptureSurfaceContent and the dead QuickCaptureWidgetWindow;
        // both now consume the registry constant.
        string[] consumers =
        [
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs",
            "src/DeskBox/Views/QuickCaptureWidgetWindow.ResponsiveDetail.cs",
        ];

        foreach (string consumer in consumers)
        {
            string source = File.ReadAllText(TestPaths.SourceFile(consumer));
            Assert.DoesNotContain(
                "const string MasterPaneWidthMetadataKey",
                source,
                StringComparison.Ordinal);
            Assert.Contains(
                "WidgetMetadataKeys.QuickCaptureMasterPaneWidth",
                source,
                StringComparison.Ordinal);
        }
    }
}
