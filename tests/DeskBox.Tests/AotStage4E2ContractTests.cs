namespace DeskBox.Tests;

public sealed class AotStage4E2ContractTests
{
    [Fact]
    public void MusicTransportIcon_UsesExactlySevenTypedForegroundBindings()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/MusicTransportIcon.xaml");

        Assert.Equal(7, CountOccurrences(xaml, "{x:Bind Foreground, Mode=OneWay}"));
        Assert.DoesNotContain(
            "{Binding Foreground, ElementName=Root}",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetInlineEditor_UsesTypedOneWayBindingsForPresentationProperties()
    {
        string xaml = ReadRepositoryFile("src/DeskBox/Controls/WidgetInlineEditor.xaml");

        Assert.Equal(1, CountOccurrences(xaml, "{x:Bind TitleFontSize, Mode=OneWay}"));
        Assert.Equal(1, CountOccurrences(xaml, "{x:Bind Title, Mode=OneWay}"));
        Assert.Equal(1, CountOccurrences(xaml, "{x:Bind EditorFontSize, Mode=OneWay}"));
        Assert.Equal(1, CountOccurrences(xaml, "{x:Bind CancelText, Mode=OneWay}"));
        Assert.Equal(2, CountOccurrences(xaml, "{x:Bind CommandFontSize, Mode=OneWay}"));
        Assert.Equal(1, CountOccurrences(xaml, "{x:Bind SaveText, Mode=OneWay}"));
        Assert.DoesNotContain("ElementName=InlineEditorRoot", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void WidgetInlineEditor_TextBinding_RemainsImmediateTwoWay()
    {
        string xaml = ReadRepositoryFile("src/DeskBox/Controls/WidgetInlineEditor.xaml");

        Assert.Equal(
            1,
            CountOccurrences(
                xaml,
                "{x:Bind Text, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"));
        Assert.DoesNotContain(
            "{Binding Text, ElementName=InlineEditorRoot, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void LeafControls_RetainTheirDependencyPropertyAndInteractionContracts()
    {
        string musicCode = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/MusicTransportIcon.xaml.cs");
        string editorCode = ReadRepositoryFile("src/DeskBox/Controls/WidgetInlineEditor.xaml.cs");

        Assert.Contains("KindProperty", musicCode, StringComparison.Ordinal);
        Assert.Contains("OnKindChanged", musicCode, StringComparison.Ordinal);
        Assert.Contains("ApplyKind();", musicCode, StringComparison.Ordinal);
        Assert.Contains("TextProperty", editorCode, StringComparison.Ordinal);
        Assert.Contains("SaveRequested?.Invoke(this, e);", editorCode, StringComparison.Ordinal);
        Assert.Contains("CancelRequested?.Invoke(this, e);", editorCode, StringComparison.Ordinal);
        Assert.Contains("EditorKeyDown?.Invoke(this, e);", editorCode, StringComparison.Ordinal);
    }

    [Fact]
    public void InlineEditorConsumers_SaveAndResetThroughTheTextDependencyProperty()
    {
        string quickCapture = ReadRepositoryFile(
            "src/DeskBox/Views/QuickCaptureWidgetWindow.Editing.cs");
        string todo = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/TodoWidgetContent.EditingAndUndo.cs");

        Assert.Contains("string body = QuickCaptureInlineEditor.Text;", quickCapture, StringComparison.Ordinal);
        Assert.Contains("QuickCaptureInlineEditor.Text = string.Empty;", quickCapture, StringComparison.Ordinal);
        Assert.Contains("UpdateItemTextAsync(item.Id, TodoInlineEditor.Text)", todo, StringComparison.Ordinal);
        Assert.Contains("TodoInlineEditor.Text = string.Empty;", todo, StringComparison.Ordinal);
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
    public void AotAudit_DeclaresTheStage4E2LeafBindingContract()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 62", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E2SourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E2LegacyBindingSourceMatches", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E2MissingCompiledBindings", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E2MissingBehaviorPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E2SourceWarningMessages", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void AotAudit_PreservesTheStage4E2Wmc1510Ceiling()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$stage4E2MaximumWmc1510Count = 870", audit, StringComparison.Ordinal);
        Assert.Contains("Stage 4E-2 WMC1510 count regressed above its ceiling", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAotBuild_DeclaresTheStage4E2XamlBoundary()
    {
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("twenty-two prior leaf compiled bindings", project, StringComparison.OrdinalIgnoreCase);
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
