using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Services;

namespace DeskBox.Core.Persistence;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(SyncRevisionsDocument),
    TypeInfoPropertyName = "SyncRevisionsDocument")]
internal sealed partial class SyncRevisionsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// data/sync/revisions.json — per-entity protocol state keyed by
/// <c>domain/collection_id/entity_id</c> (sync-protocol-contract §2.3/§8).
/// This is where revisions live; domain records never carry them.
/// </summary>
public sealed class SyncRevisionsDocument
{
    public int SchemaVersion { get; set; } = 1;

    public Dictionary<string, SyncRevisionRecord> Entities { get; set; } = new();
}

public sealed class SyncRevisionRecord
{
    /// <summary>Composite key used in <see cref="SyncRevisionsDocument.Entities"/>.</summary>
    public static string Key(string domain, string collectionId, string entityId) =>
        $"{domain}/{collectionId}/{entityId}";

    /// <summary>Server-issued monotonically increasing revision of the
    /// last envelope the server holds for this entity.</summary>
    public long ServerRevision { get; set; }

    /// <summary>The server_revision this client last confirmed — the
    /// optimistic-concurrency base for the next push (§3.2).</summary>
    public long BaseRevision { get; set; }

    /// <summary>Idempotency key of the last accepted push.</summary>
    public string? OperationId { get; set; }
}

/// <summary>
/// File owner for <see cref="SyncRevisionsDocument"/> — dumb durable holder;
/// the engine owns the semantics, this class owns atomic reads/writes plus
/// corruption recovery.
/// </summary>
public sealed class SyncRevisionsStore
{
    private readonly string _revisionsPath;

    public SyncRevisionsStore(string? revisionsPath = null)
    {
        _revisionsPath = revisionsPath ?? Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "sync",
            "revisions.json");
    }

    public Task<SyncRevisionsDocument> LoadAsync() =>
        ResilientJsonStore.LoadAsync(
            _revisionsPath,
            static json => JsonSerializer.Deserialize(
                json, SyncRevisionsJsonContext.Default.SyncRevisionsDocument)
                ?? new SyncRevisionsDocument(),
            static () => new SyncRevisionsDocument(),
            "SyncRevisions");

    public Task SaveAsync(SyncRevisionsDocument document) =>
        ResilientJsonStore.SaveAsync(
            _revisionsPath,
            JsonSerializer.SerializeToUtf8Bytes(
                document, SyncRevisionsJsonContext.Default.SyncRevisionsDocument));

    /// <inheritdoc cref="WidgetLayoutStore.SaveCheckedAsync"/>
    public async Task<bool> SaveCheckedAsync(SyncRevisionsDocument document)
    {
        try
        {
            await SaveAsync(document);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log($"[SyncRevisions] Save failed: {ex}");
            return false;
        }
    }
}
