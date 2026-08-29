namespace DeskBox.Tests;

public sealed class AotStage4E5ContractTests
{
    [Fact]
    public void SearchResultsTemplate_PreparesTheInternalTypedProjectionFromTheRepeaterIndex()
    {
        string xaml = ReadRepositoryFile("src/DeskBox/Views/SearchPopupWindow.xaml");
        string code = ReadRepositoryFile("src/DeskBox/Views/SearchPopupWindow.xaml.cs");

        Assert.Contains("<controls:SearchResultRowControl/>", xaml, StringComparison.Ordinal);
        Assert.Contains("args.Index < _viewModel.CurrentResults.Count", code, StringComparison.Ordinal);
        Assert.Contains("? _viewModel.CurrentResults[args.Index]", code, StringComparison.Ordinal);
        Assert.Contains("row.PrepareItem(preparedItem);", code, StringComparison.Ordinal);
        Assert.DoesNotContain("row.PrepareItem(row.DataContext as SearchResultItem);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchResultRow_UsesAnActivatorSafeInternalTypedProjection()
    {
        string code = ReadRepositoryFile(
            "src/DeskBox/Controls/SearchResultRowControl.xaml.cs");

        Assert.Contains("internal SearchResultItem? Item { get; private set; }", code, StringComparison.Ordinal);
        Assert.Contains("internal void PrepareItem(SearchResultItem? item)", code, StringComparison.Ordinal);
        Assert.Contains("Item = item;", code, StringComparison.Ordinal);
        Assert.Contains("Bindings.Update();", code, StringComparison.Ordinal);
        Assert.DoesNotContain("DependencyProperty ItemProperty", code, StringComparison.Ordinal);
        Assert.DoesNotContain("public SearchResultItem? Item", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchResultRow_UsesEightManuallyRefreshedOneTimeCompiledBindings()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Controls/SearchResultRowControl.xaml");
        string[] leaves =
        [
            "Title",
            "DisplayGlyph",
            "Icon",
            "Title",
            "Subtitle",
            "TypeDisplay",
            "SizeDisplay",
            "DateDisplay"
        ];

        Assert.DoesNotContain("{Binding ", xaml, StringComparison.Ordinal);
        Assert.Equal(8, CountOccurrences(xaml, "{x:Bind Item."));
        foreach (string leaf in leaves.Distinct(StringComparer.Ordinal))
        {
            int expected = leaf == "Title" ? 2 : 1;
            Assert.Equal(
                expected,
                CountOccurrences(xaml, $"{{x:Bind Item.{leaf}, Mode=OneTime}}"));
        }
    }

    [Fact]
    public void SearchResultItem_RemainsAPlainNonObservableResultModel()
    {
        string models = ReadRepositoryFile("src/DeskBox/Models/SearchModels.cs");
        int start = models.IndexOf("public sealed class SearchResultItem", StringComparison.Ordinal);
        int end = models.IndexOf("public sealed class SearchResultGroup", start, StringComparison.Ordinal);
        string item = models[start..end];

        Assert.DoesNotContain("INotifyPropertyChanged", item, StringComparison.Ordinal);
        Assert.DoesNotContain("PropertyChangedEventHandler", item, StringComparison.Ordinal);
        Assert.DoesNotContain("OnPropertyChanged", item, StringComparison.Ordinal);
        Assert.Contains("public ImageSource? Icon { get; set; }", item, StringComparison.Ordinal);
        Assert.Contains("public string? SizeDisplay { get; set; }", item, StringComparison.Ordinal);
        Assert.Contains("public string? DateDisplay { get; set; }", item, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchResultRow_RetainsExplicitLazyMetadataRefresh()
    {
        string code = ReadRepositoryFile(
            "src/DeskBox/Controls/SearchResultRowControl.xaml.cs");

        Assert.Contains("public void RefreshIconVisuals()", code, StringComparison.Ordinal);
        Assert.Contains("FileIcon.Source = item?.Icon;", code, StringComparison.Ordinal);
        Assert.Contains("FileIcon.Visibility = hasIcon", code, StringComparison.Ordinal);
        Assert.Contains("GlyphBlock.Visibility = hasIcon", code, StringComparison.Ordinal);
        Assert.Contains("SizeText.Text = item?.SizeDisplay;", code, StringComparison.Ordinal);
        Assert.Contains("DateText.Text = item?.DateDisplay;", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultsRepeater_RefreshesEveryPreparedOrRecycledRow()
    {
        string code = ReadRepositoryFile("src/DeskBox/Views/SearchPopupWindow.xaml.cs");
        int handler = code.IndexOf(
            "private void OnResultsElementPrepared",
            StringComparison.Ordinal);
        int enrich = code.IndexOf(
            "private async Task EnrichPreparedResultRowAsync",
            handler,
            StringComparison.Ordinal);
        string body = code[handler..enrich];

        int resolve = body.IndexOf("? _viewModel.CurrentResults[args.Index]", StringComparison.Ordinal);
        int prepare = body.IndexOf("row.PrepareItem(preparedItem);", StringComparison.Ordinal);
        int refresh = body.IndexOf("row.RefreshIconVisuals();", StringComparison.Ordinal);
        Assert.True(resolve >= 0 && prepare > resolve && refresh > prepare);
        Assert.Contains("row.RefreshIconVisuals();", body, StringComparison.Ordinal);
        Assert.Contains("row.SetFileColumnsVisible", body, StringComparison.Ordinal);
        Assert.Contains("row.Item is { IconResolved: false } item", body, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(row.Item, selected)", body, StringComparison.Ordinal);
        Assert.Contains("row.IsSelected = false;", body, StringComparison.Ordinal);
        Assert.Contains("row.IsMultiSelected = row.Item is { } rowItem", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AsyncMetadataRefresh_RejectsARecycledRowWithAnotherItem()
    {
        string code = ReadRepositoryFile("src/DeskBox/Views/SearchPopupWindow.xaml.cs");

        Assert.Contains("await _viewModel.EnsureResultMetadataAsync(item);", code, StringComparison.Ordinal);
        Assert.Contains("DispatcherQueue.TryEnqueue(() => RefreshPreparedResultRow(row, item));", code, StringComparison.Ordinal);
        Assert.Contains("if (ReferenceEquals(row.Item, item))", code, StringComparison.Ordinal);
        Assert.Contains("row.RefreshIconVisuals();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void ResultsRepeater_UsesTypedRowItemForInteractionLookup()
    {
        string code = ReadRepositoryFile("src/DeskBox/Views/SearchPopupWindow.xaml.cs");

        Assert.Contains("FindRowByDataContext(ResultsRepeater, selected)", code, StringComparison.Ordinal);
        Assert.Contains("ResolveResultItem", code, StringComparison.Ordinal);
        Assert.Contains("FindItemRow(element)?.Item ?? FindDataContext<SearchResultItem>(element)", code, StringComparison.Ordinal);
        Assert.Contains("ReferenceEquals(row.Item, data) || ReferenceEquals(row.DataContext, data)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ResultsRepeater.DataContext =", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchPopup_UnhooksRepeaterAndClearsItemsBeforeDisposingViewModel()
    {
        string code = ReadRepositoryFile("src/DeskBox/Views/SearchPopupWindow.xaml.cs");
        int unhook = code.IndexOf(
            "ResultsRepeater.ElementPrepared -= OnResultsElementPrepared;",
            StringComparison.Ordinal);
        int clear = code.IndexOf("ResultsRepeater.ItemsSource = null;", StringComparison.Ordinal);
        int dispose = code.IndexOf("_viewModel.Dispose();", StringComparison.Ordinal);

        Assert.True(unhook >= 0 && clear > unhook && dispose > clear);
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
    public void AotAudit_DeclaresTheStage4E5TypedResultRowContract()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E5SourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E5LegacyBindingSourceMatches", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E5MissingCompiledBindings", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E5MissingItemBridgePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E5UnexpectedPublicItemBridgePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E5ItemRefreshOrderValid", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E5MissingBehaviorPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E5MissingRequiredModelPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E5UnexpectedObservableModelPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E5UnexpectedDataContextOverridePatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E5LifecycleOrderValid", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E5MissingDeferredBindings", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E5SourceWarningMessages", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAotBuild_DeclaresTheStage4E5WarningReduction()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$stage4E4MaximumWmc1510Count = 1224", audit, StringComparison.Ordinal);
        Assert.Contains("$stage4E5ExpectedWmc1510Count = 1213", audit, StringComparison.Ordinal);
        Assert.Contains("Stage 4E-5 WMC1510 count changed", audit, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("eight compiled search-result row bindings", project, StringComparison.OrdinalIgnoreCase);
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
