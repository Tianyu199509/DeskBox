using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Models;
using DeskBox.Protocol;

namespace DeskBox.Services.CommandApi.Handlers;

public sealed record SearchQueryItem(string Kind, string Title, string? DetailPath);

public sealed record SearchQueryResult(
    string Query,
    int TotalResultCount,
    IReadOnlyList<SearchQueryItem> Items);

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SearchQueryResult), TypeInfoPropertyName = "SearchQueryResult")]
[JsonSerializable(typeof(SearchQueryItem), TypeInfoPropertyName = "SearchQueryItem")]
internal sealed partial class SearchJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Runs one search through the live search engine (Everything for files
/// plus DeskBox content: notes, todos, widget titles). Headless: the
/// engine serializes its own state and Everything is reached over its
/// local IPC. When the Everything utility is not running the service
/// surfaces that in the response instead of failing.
/// </summary>
public sealed class SearchQueryHandler : ICommandHandler
{
    private const int MaxLimit = 50;

    public CommandRegistration Registration { get; } = new(
        Method: "search/query",
        ThreadAffinity: CommandThreadAffinity.UiThread,
        Capability: CommandApiProtocol.Capabilities.SearchRead,
        MutatesState: false,
        Destructive: false,
        Summary: "Searches files (via the Everything integration) and DeskBox content (notes, todos, widget titles).",
        Arguments:
        [
            new CommandArgumentDescriptor("query", "string", true, "Search text.", "\"周报\""),
            new CommandArgumentDescriptor("limit", "integer", false,
                $"Maximum number of items to return (1-{MaxLimit}, default 20).", "20"),
        ],
        ExampleRequestJson: """{"jsonrpc":"2.0","id":25,"method":"search/query","params":{"protocolVersion":1,"clientName":"deskbox-cli","arguments":{"query":"周报","limit":10}}}""",
        ExampleResponseJson: """{"result":{"data":{"query":"周报","totalResultCount":1,"items":[{"kind":"File","title":"周报.docx"}]}}}""");

    public async Task<JsonElement> ExecuteAsync(
        JsonElement arguments,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!CommandArguments.TryGetString(arguments, "query", out string query)
            || string.IsNullOrWhiteSpace(query))
        {
            throw CommandValidationException.ValidationFailed(
                "The 'query' argument is required.",
                """Retry with {"query":"<search text>"}.""");
        }

        int limit = 20;
        if (arguments.ValueKind == JsonValueKind.Object
            && arguments.TryGetProperty("limit", out JsonElement limitProperty))
        {
            if (limitProperty.ValueKind != JsonValueKind.Number
                || !limitProperty.TryGetInt32(out limit)
                || limit < 1
                || limit > MaxLimit)
            {
                throw CommandValidationException.ValidationFailed(
                    $"The 'limit' argument must be an integer between 1 and {MaxLimit}.",
                    "Retry with {\"limit\":20} or omit the argument.");
            }
        }

        SearchEngineService? engine = App.Current.EnsureSearchServicesForUserAction();
        if (engine is null)
        {
            throw new CommandValidationException(new CommandErrorPayload
            {
                Code = CommandApiProtocol.ErrorCodes.InternalError,
                Phase = "execute",
                Message = "The search engine is not available (the search feature is disabled in this session).",
                Hint = "Enable the search widget in DeskBox settings (Feature widgets), then retry. File search also requires the Everything utility (voidtools.com) to be running.",
            });
        }

        SearchResponse response = await engine.SearchAsync(query, cancellationToken).ConfigureAwait(false);
        List<SearchQueryItem> items = response.RankedItems
            .Take(limit)
            .Select(item => new SearchQueryItem(
                item.Kind.ToString(),
                item.Title,
                string.IsNullOrWhiteSpace(item.DetailPath) ? null : item.DetailPath))
            .ToList();
        SearchQueryResult result = new(query, response.TotalResultCount, items);
        return JsonSerializer.SerializeToElement(result, SearchJsonContext.Default.SearchQueryResult);
    }
}
