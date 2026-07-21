using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SearchEngineRecommendationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "DeskBox-SearchRecommendations-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetRecommendationsAsync_PrioritizesEveryWidgetShortcut()
    {
        Directory.CreateDirectory(_root);
        string firstShortcut = Path.Combine(_root, "First App.lnk");
        string secondShortcut = Path.Combine(_root, "Second App.lnk");
        await File.WriteAllTextAsync(firstShortcut, string.Empty);
        await File.WriteAllTextAsync(secondShortcut, string.Empty);

        var settings = new SettingsService(Path.Combine(_root, "settings"));
        settings.Settings.Widgets.Add(new WidgetConfig
        {
            Name = "Apps",
            WidgetKind = WidgetKind.File,
            Items =
            [
                new WidgetItemConfig { Path = firstShortcut, SortOrder = 0 },
                new WidgetItemConfig { Path = secondShortcut, SortOrder = 1 }
            ]
        });

        var localization = new LocalizationService(settings);
        var index = new SearchIndexService(settings, Path.Combine(_root, "index.json"));
        using var engine = new SearchEngineService(
            settings,
            localization,
            index,
            new WindowsIndexSearchService(settings));

        var recommendations = await engine.GetRecommendationsAsync();

        Assert.True(recommendations.Count >= 2);
        Assert.Equal(firstShortcut, recommendations[0].DetailPath);
        Assert.Equal(secondShortcut, recommendations[1].DetailPath);
        Assert.All(recommendations.Take(2), item => Assert.Equal(SearchResultKind.File, item.Kind));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for files that may still be scanned by Windows.
        }
    }
}
