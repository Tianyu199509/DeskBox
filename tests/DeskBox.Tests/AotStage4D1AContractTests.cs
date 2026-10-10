using System.Text.RegularExpressions;

namespace DeskBox.Tests;

public sealed class AotStage4D1AContractTests
{
    [Fact]
    public void DispatcherQueueOptions_UsesGenericStaticSize()
    {
        string source = ReadRepositoryFile("src/DeskBox/Platform/Win32Helper.cs");

        Assert.Contains(
            "Marshal.SizeOf<DispatcherQueueOptions>()",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Marshal.SizeOf(typeof(DispatcherQueueOptions))",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MarkdownTaskState_UsesThePublicMarkdigTaskListContract()
    {
        string source = ReadRepositoryFile("src/DeskBox/Controls/MarkdownDocumentView.cs");

        Assert.Contains("using Markdig.Extensions.TaskLists;", source, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(
                @"FirstChild is TaskList (?<name>\w+)[\s\S]*?return \k<name>\.Checked;",
                RegexOptions.CultureInvariant),
            source);
        Assert.DoesNotContain("GetType().Name", source, StringComparison.Ordinal);
        Assert.DoesNotContain("GetProperty(\"Checked\")", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchHistoryAndFavorites_UseTheExistingTypedRecommendationModel()
    {
        // The favorites/recent panel was dead UI (never made visible) and has
        // been removed along with its projection/handlers; the typed model and
        // its JSON history persistence stay. Lock the removal so the dead
        // x:Bind surface does not quietly return.
        string source = ReadRepositoryFile("src/DeskBox/Views/SearchPopupWindow.xaml.cs");

        Assert.DoesNotContain("new SearchRecommendationItem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("DataContext is SearchRecommendationItem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new { Title", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GetType().GetProperty(\"Title\")",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AotAudit_RequiresTheStage4D1ATargetFilesToRemainWarningFree()
    {
        string script = ReadRepositoryFile("scripts/publish-aot-audit.ps1");

        Assert.Contains("$auditProfileVersion = 59", script, StringComparison.Ordinal);
        Assert.Contains("schemaVersion = 55", script, StringComparison.Ordinal);
        Assert.Contains("stage4D1AWarningMessages", script, StringComparison.Ordinal);
        Assert.Contains(
            "Stage 4D-1A target files still produce AOT analysis warnings",
            script,
            StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(TestPaths.FromRepository(relativePath));
    }
}
