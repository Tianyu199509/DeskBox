namespace DeskBox.Tests;

public sealed class AotStage4E3ContractTests
{
    [Fact]
    public void AttachmentTiles_DeclareExactlyOneTypedItemTemplate()
    {
        string xaml = ReadRepositoryFile("src/DeskBox/Controls/AttachmentTileStrip.xaml");

        Assert.Equal(
            1,
            CountOccurrences(
                xaml,
                "<DataTemplate x:DataType=\"viewModels:TodoAttachmentViewModel\">"));
        Assert.Contains(
            "xmlns:viewModels=\"using:DeskBox.ViewModels\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentTiles_UseExactlySevenObservableCompiledBindings()
    {
        string xaml = ReadRepositoryFile("src/DeskBox/Controls/AttachmentTileStrip.xaml");

        Assert.Equal(3, CountOccurrences(xaml, "{x:Bind DisplayName, Mode=OneWay}"));
        Assert.Equal(1, CountOccurrences(xaml, "{x:Bind Glyph, Mode=OneWay}"));
        Assert.Equal(1, CountOccurrences(xaml, "{x:Bind FileIconVisibility, Mode=OneWay}"));
        Assert.Equal(1, CountOccurrences(xaml, "{x:Bind Thumbnail, Mode=OneWay}"));
        Assert.Equal(1, CountOccurrences(xaml, "{x:Bind ThumbnailVisibility, Mode=OneWay}"));
        Assert.DoesNotContain("{Binding ", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentThumbnailModel_RaisesBothVisibilityNotifications()
    {
        string model = ReadRepositoryFile("src/DeskBox/ViewModels/TodoAttachmentViewModel.cs");

        Assert.Contains(
            "public sealed class TodoAttachmentViewModel : ObservableObject",
            model,
            StringComparison.Ordinal);
        Assert.Contains("if (SetProperty(ref _thumbnail, value))", model, StringComparison.Ordinal);
        Assert.Contains(
            "OnPropertyChanged(nameof(ThumbnailVisibility));",
            model,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnPropertyChanged(nameof(FileIconVisibility));",
            model,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AttachmentTiles_RetainLazyLoadingAndInteractionEvents()
    {
        string code = ReadRepositoryFile("src/DeskBox/Controls/AttachmentTileStrip.xaml.cs");

        Assert.Contains("AttachmentTile_DataContextChanged", code, StringComparison.Ordinal);
        Assert.Contains("await EnsureThumbnailAsync(args.NewValue);", code, StringComparison.Ordinal);
        Assert.Contains("await attachment.EnsureThumbnailAsync();", code, StringComparison.Ordinal);
        Assert.Contains("OpenRequested?.Invoke(this, new AttachmentTileEventArgs(attachment));", code, StringComparison.Ordinal);
        Assert.Contains("RemoveRequested?.Invoke(this, new AttachmentTileEventArgs(attachment));", code, StringComparison.Ordinal);
        Assert.Contains("SetRemoveButtonVisible(tile, CanRemove);", code, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchPopup_DeclaresFourTypedDataTemplates()
    {
        string xaml = ReadRepositoryFile("src/DeskBox/Views/SearchPopupWindow.xaml");

        Assert.Contains("xmlns:models=\"using:DeskBox.Models\"", xaml, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(xaml, "x:DataType=\"models:SearchTabItem\""));
        Assert.Equal(1, CountOccurrences(xaml, "x:DataType=\"models:SearchResultItem\""));
        Assert.Equal(2, CountOccurrences(xaml, "x:DataType=\"models:SearchRecommendationItem\""));

        int recommendedApps = xaml.IndexOf("x:Name=\"RecommendedAppsRepeater\"", StringComparison.Ordinal);
        int resultTemplate = xaml.IndexOf("x:DataType=\"models:SearchResultItem\"", StringComparison.Ordinal);
        int favorites = xaml.IndexOf("x:Name=\"FavoritesRepeater\"", StringComparison.Ordinal);
        Assert.True(recommendedApps >= 0 && resultTemplate > recommendedApps && resultTemplate < favorites);
    }

    [Fact]
    public void SearchPopup_UsesStaticTabContentWithoutAPagedResultCount()
    {
        string xaml = ReadRepositoryFile("src/DeskBox/Views/SearchPopupWindow.xaml");

        Assert.Equal(1, CountOccurrences(xaml, "{x:Bind Glyph, Mode=OneTime}"));
        Assert.Equal(1, CountOccurrences(xaml, "{x:Bind DisplayName, Mode=OneTime}"));
        Assert.Equal(0, CountOccurrences(xaml, "{x:Bind Count, Mode=OneWay}"));
        Assert.Equal(1, CountOccurrences(xaml, "{x:Bind Icon, Mode=OneTime}"));
        Assert.Equal(1, CountOccurrences(xaml, "{x:Bind AppDisplayName, Mode=OneTime}"));
        Assert.Equal(2, CountOccurrences(xaml, "{x:Bind Title, Mode=OneTime}"));
        Assert.DoesNotContain("{Binding ", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchTemplateModels_MatchTheirNotificationLifetimes()
    {
        string models = ReadRepositoryFile("src/DeskBox/Models/SearchModels.cs");

        Assert.Contains("public sealed class SearchTabItem : INotifyPropertyChanged", models, StringComparison.Ordinal);
        Assert.Contains("PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));", models, StringComparison.Ordinal);
        Assert.Contains("public required string DisplayName { get; init; }", models, StringComparison.Ordinal);
        Assert.Contains("public ImageSource? Icon { get; set; }", models, StringComparison.Ordinal);
        Assert.Contains("public string AppDisplayName =>", models, StringComparison.Ordinal);
        Assert.Contains("public sealed class SearchRecommendationItem", models, StringComparison.Ordinal);
        Assert.Contains("public required string Title { get; init; }", models, StringComparison.Ordinal);
    }

    [Fact]
    public void RecommendedAppIcons_RetainTheirExplicitLazyRefreshChain()
    {
        string code = ReadRepositoryFile("src/DeskBox/Views/SearchPopupWindow.xaml.cs");

        Assert.Contains(
            "RecommendedAppsRepeater.ElementPrepared += OnRecommendedAppsElementPrepared;",
            code,
            StringComparison.Ordinal);
        Assert.Contains("private void OnRecommendedAppsElementPrepared", code, StringComparison.Ordinal);
        Assert.Contains("private void RefreshRecommendedAppIcons()", code, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(code, "image.Source = item.Icon;"));
        Assert.Contains("RefreshRecommendedAppIcons();", code, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeDataContextAndStyleBindings_RemainExplicitlyDeferred()
    {
        string app = ReadRepositoryFile("src/DeskBox/App.xaml");
        string contentWindow = ReadRepositoryFile("src/DeskBox/Views/ContentWidgetWindow.xaml");
        Assert.Equal(2, CountOccurrences(app, "{Binding "));
        Assert.Equal(1, CountOccurrences(contentWindow, "{Binding "));
    }

    [Fact]
    public void AotAudit_DeclaresTheStage4E3TypedTemplateContract()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 58", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E3SourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E3LegacyBindingSourceMatches", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E3MissingCompiledBindings", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E3MissingDataTypes", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E3MissingBehaviorPatterns", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E3SourceWarningMessages", audit, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAotBuild_DeclaresTheStage4E3WarningReduction()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("$stage4E3MaximumWmc1510Count = 1241", audit, StringComparison.Ordinal);
        Assert.Contains("Stage 4E-3 WMC1510 count regressed above its ceiling", audit, StringComparison.Ordinal);
        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("fourteen typed DataTemplate bindings", project, StringComparison.OrdinalIgnoreCase);
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
