namespace DeskBox.Tests;

public sealed class AotStage4E0ContractTests
{
    [Fact]
    public void SearchHistoryEntry_RemainsAnImmutableDisplayModel()
    {
        string source = ReadRepositoryFile("src/DeskBox/Models/SearchModels.cs");

        Assert.Contains("public sealed class SearchHistoryEntry", source, StringComparison.Ordinal);
        Assert.Contains("public required string Query { get; init; }", source, StringComparison.Ordinal);
        Assert.Contains("public required string DeleteLabel { get; init; }", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchHistoryRefresh_RebuildsEntriesInsteadOfMutatingThem()
    {
        string source = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/SearchWidgetContent.xaml.cs");

        Assert.Contains("_recentQueries.Clear();", source, StringComparison.Ordinal);
        Assert.Contains("_recentQueries.Add(new SearchHistoryEntry", source, StringComparison.Ordinal);
        Assert.Contains("Query = query", source, StringComparison.Ordinal);
        Assert.Contains("DeleteLabel = deleteLabel", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchHistoryTemplate_UsesExactlySixOneTimeBindings()
    {
        string xaml = ReadRepositoryFile(
            "src/DeskBox/Controls/WidgetContents/SearchWidgetContent.xaml");

        Assert.Equal(4, CountOccurrences(xaml, "{x:Bind Query, Mode=OneTime}"));
        Assert.Equal(2, CountOccurrences(xaml, "{x:Bind DeleteLabel, Mode=OneTime}"));
        Assert.DoesNotContain("{x:Bind Query, Mode=OneWay}", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("{x:Bind DeleteLabel, Mode=OneWay}", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void AotAudit_DeclaresTheStage4E0SearchHistoryContract()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 62", audit, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E0SourceFiles", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E0LegacyOneWaySourceMatches", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E0MissingOneTimeBindings", audit, StringComparison.Ordinal);
        Assert.Contains("stage4E0Wmc1506WarningMessages", audit, StringComparison.Ordinal);
        Assert.Contains(
            "Stage 4E-0 search history bindings produced WMC1506 warnings",
            audit,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AotAudit_NoLongerAllowsWmc1506Anywhere()
    {
        string audit = ReadRepositoryFile("scripts/publish-aot-audit.ps1");
        string allowedWarnings = ReadSection(
            audit,
            "$allowedWarningCodes = @(",
            "$unexpectedWarningCodes = @(");

        Assert.DoesNotContain("WMC1506", allowedWarnings, StringComparison.Ordinal);
        Assert.Contains("WMC1510", allowedWarnings, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAotBuild_DeclaresTheStage4E0XamlBoundary()
    {
        string project = ReadRepositoryFile("src/DeskBox/DeskBox.csproj");

        Assert.Contains("Native AOT stage 5B-4C3B2B1", project, StringComparison.Ordinal);
        Assert.Contains("XAML", project, StringComparison.OrdinalIgnoreCase);
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

    private static string ReadSection(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing section start: {startMarker}");
        int end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"Missing section end: {endMarker}");
        return source[start..end];
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
