using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SearchResultRankerTests
{
    [Fact]
    public void MergeAndRank_DeduplicatesCanonicalPathsAndKeepsBestResult()
    {
        const string path = @"C:\Users\test\Documents\report.pdf";
        var results = new[]
        {
            new SearchResultItem
            {
                Kind = SearchResultKind.File,
                Title = "report",
                DetailPath = path.ToUpperInvariant(),
                RelevanceScore = 75
            },
            new SearchResultItem
            {
                Kind = SearchResultKind.File,
                Title = "report.pdf",
                DetailPath = path,
                RelevanceScore = 95
            },
            new SearchResultItem
            {
                Kind = SearchResultKind.QuickCapture,
                Title = "report notes",
                QuickCaptureItemId = "note-1",
                RelevanceScore = 70
            }
        };

        var ranked = SearchResultRanker.MergeAndRank(results, "report", 20);

        Assert.Equal(2, ranked.Count);
        Assert.Equal("report.pdf", ranked[0].Title);
        Assert.Equal("note-1", ranked[1].QuickCaptureItemId);
    }

    [Fact]
    public void MergeAndRank_DeprioritizesCacheAndPartialFiles()
    {
        var results = new[]
        {
            new SearchResultItem
            {
                Kind = SearchResultKind.File,
                Title = "report.pdf",
                DetailPath = @"C:\Users\test\Documents\report.pdf",
                RelevanceScore = 80
            },
            new SearchResultItem
            {
                Kind = SearchResultKind.File,
                Title = "report.crdownload",
                DetailPath = @"C:\Users\test\Downloads\report.crdownload",
                RelevanceScore = 100
            },
            new SearchResultItem
            {
                Kind = SearchResultKind.File,
                Title = "report.json",
                DetailPath = @"C:\Users\test\AppData\Local\Temp\Cache\report.json",
                RelevanceScore = 100
            }
        };

        var ranked = SearchResultRanker.MergeAndRank(results, "rep", 20);

        Assert.Equal("report.pdf", ranked[0].Title);
        Assert.All(ranked.Skip(1), item => Assert.True(item.RelevanceScore < ranked[0].RelevanceScore));
    }

    [Fact]
    public void History_StoresOnlyOneRecentResultPerIdentity()
    {
        string root = Path.Combine(Path.GetTempPath(), "DeskBox.Tests", Guid.NewGuid().ToString("N"));
        string storePath = Path.Combine(root, "search-history.json");

        try
        {
            var history = new SearchHistoryService(storePath);
            string path = Path.Combine(root, "sample.txt");
            history.RecordResult(new SearchResultItem
            {
                Kind = SearchResultKind.File,
                Title = "sample.txt",
                DetailPath = path,
                RelevanceScore = 80
            });
            history.RecordResult(new SearchResultItem
            {
                Kind = SearchResultKind.File,
                Title = "sample renamed display.txt",
                DetailPath = path,
                RelevanceScore = 90
            });

            var reloaded = new SearchHistoryService(storePath);
            var recent = Assert.Single(reloaded.RecentResults);
            Assert.Equal("sample renamed display.txt", recent.Title);

            reloaded.ClearAllHistory();
            Assert.Empty(reloaded.RecentResults);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
