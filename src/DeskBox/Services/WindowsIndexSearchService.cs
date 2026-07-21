using DeskBox.Models;
using Windows.Storage;
using Windows.Storage.Search;

namespace DeskBox.Services;

/// <summary>
/// Layer 2 search provider backed by the Windows Search Index.
/// Uses WinRT <see cref="StorageFolder"/> queries with
/// <see cref="IndexerOption.UseIndexerWhenAvailable"/> so indexed locations are served
/// from the system index (fast, content-aware) while non-indexed locations gracefully
/// fall back to a filesystem walk. Results are merged into the unified search response
/// by <see cref="SearchEngineService"/>.
/// </summary>
public sealed class WindowsIndexSearchService
{
    private static readonly TimeSpan SearchBudget = TimeSpan.FromMilliseconds(900);
    private readonly SettingsService _settingsService;

    public WindowsIndexSearchService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    /// <summary>
    /// Queries the Windows Search Index across the user's indexed libraries.
    /// </summary>
    public async Task<IReadOnlyList<SearchResultItem>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default)
    {
        var results = new List<SearchResultItem>();
        if (string.IsNullOrWhiteSpace(query) || maxResults <= 0)
        {
            return results;
        }

        string normalizedQuery = query.Trim();

        using var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCts.CancelAfter(SearchBudget);

        var rootTasks = GetIndexedRoots()
            .Select(root => SearchFolderAsync(root, normalizedQuery, maxResults, budgetCts.Token))
            .ToArray();

        IReadOnlyList<SearchResultItem>[] batches = await Task.WhenAll(rootTasks);
        results.AddRange(batches.SelectMany(batch => batch));

        return results
            .OrderByDescending(r => r.RelevanceScore)
            .ThenByDescending(r => r.ModifiedAt)
            .Take(maxResults)
            .ToList();
    }

    private static async Task<IReadOnlyList<SearchResultItem>> SearchFolderAsync(
        string rootPath, string query, int maxResults, CancellationToken cancellationToken)
    {
        var results = new List<SearchResultItem>();

        try
        {
            StorageFolder root = await StorageFolder.GetFolderFromPathAsync(rootPath);

            var options = new QueryOptions(CommonFileQuery.OrderByName, new[] { "*" })
            {
                IndexerOption = IndexerOption.UseIndexerWhenAvailable,
                UserSearchFilter = query,
                FolderDepth = FolderDepth.Deep
            };

            StorageFileQueryResult queryResult = root.CreateFileQueryWithOptions(options);
            IReadOnlyList<StorageFile> files = await queryResult
                .GetFilesAsync(0, (uint)maxResults)
                .AsTask(cancellationToken);

            foreach (StorageFile file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                string directory = Path.GetDirectoryName(file.Path) ?? string.Empty;
                results.Add(new SearchResultItem
                {
                    Kind = SearchResultKind.File,
                    Title = file.DisplayName,
                    Subtitle = directory,
                    DetailPath = file.Path,
                    ModifiedAt = file.DateCreated,
                    RelevanceScore = ComputeRelevance(file.DisplayName, query)
                });
            }

            if (results.Count < maxResults && !cancellationToken.IsCancellationRequested)
            {
                var folderOptions = new QueryOptions(CommonFolderQuery.DefaultQuery)
                {
                    IndexerOption = IndexerOption.UseIndexerWhenAvailable,
                    UserSearchFilter = query,
                    FolderDepth = FolderDepth.Deep
                };
                StorageFolderQueryResult folderQuery = root.CreateFolderQueryWithOptions(folderOptions);
                IReadOnlyList<StorageFolder> folders = await folderQuery
                    .GetFoldersAsync(0, (uint)(maxResults - results.Count))
                    .AsTask(cancellationToken);

                foreach (StorageFolder folder in folders)
                {
                    results.Add(new SearchResultItem
                    {
                        Kind = SearchResultKind.Folder,
                        Title = folder.DisplayName,
                        Subtitle = Path.GetDirectoryName(folder.Path) ?? string.Empty,
                        DetailPath = folder.Path,
                        ModifiedAt = folder.DateCreated,
                        RelevanceScore = ComputeRelevance(folder.DisplayName, query),
                        Glyph = "\uE8B7"
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Propagate cancellation quietly.
        }
        catch (Exception ex)
        {
            App.Log($"[WindowsIndex] Search failed for '{rootPath}': {ex.Message}");
        }

        return results;
    }

    /// <summary>
    /// Returns the set of library roots that the Windows Search Index typically covers.
    /// </summary>
    private List<string> GetIndexedRoots()
    {
        var roots = new List<string>();

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            string[] defaultDirs =
            [
                Path.Combine(userProfile, "Desktop"),
                Path.Combine(userProfile, "Documents"),
                Path.Combine(userProfile, "Downloads"),
                Path.Combine(userProfile, "Pictures"),
                Path.Combine(userProfile, "Music"),
                Path.Combine(userProfile, "Videos")
            ];

            roots.AddRange(defaultDirs.Where(Directory.Exists));
        }

        roots.AddRange(_settingsService.Settings.SearchCustomIndexPaths
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)));

        foreach (var widget in _settingsService.Settings.Widgets
                     .Where(widget => widget.WidgetKind == WidgetKind.File && !widget.IsDisabled))
        {
            if (!string.IsNullOrWhiteSpace(widget.MappedFolderPath) &&
                Directory.Exists(widget.MappedFolderPath))
            {
                roots.Add(widget.MappedFolderPath);
            }
        }

        string[] applicationRoots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
        ];
        roots.AddRange(applicationRoots.Where(path =>
            !string.IsNullOrWhiteSpace(path) && Directory.Exists(path)));

        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static double ComputeRelevance(string fileName, string query)
    {
        if (fileName.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 95.0;
        }

        if (fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 75.0;
        }

        string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
        if (nameWithoutExt.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 85.0;
        }

        if (nameWithoutExt.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 65.0;
        }

        if (fileName.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return 45.0;
        }

        return 35.0;
    }
}
