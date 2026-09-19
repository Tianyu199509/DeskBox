using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Services;

namespace DeskBox.Core.Persistence;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(SyncStateDocument),
    TypeInfoPropertyName = "SyncStateDocument")]
internal sealed partial class SyncStateJsonContext : JsonSerializerContext
{
}

/// <summary>
/// data/sync/state.json — per-domain pull cursors, the account's epoch, and
/// the account summary (sync-protocol-contract §8). Device-domain protocol
/// state: never backed up, never synced.
/// </summary>
public sealed class SyncStateDocument
{
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Server-side user id once an account is linked; null when
    /// sync is unconfigured.</summary>
    public string? AccountId { get; set; }

    /// <summary>Per-domain pull state keyed by the wire domain name
    /// (todo-data / quick-capture-data / widget-style).</summary>
    public Dictionary<string, SyncDomainState> Domains { get; set; } = new();
}

public sealed class SyncDomainState
{
    /// <summary>Opaque per-domain pull cursor issued by the server (§4).</summary>
    public string Cursor { get; set; } = string.Empty;

    /// <summary>Account epoch last seen on pull; a mismatch switches the
    /// domain to full-snapshot replace (§4).</summary>
    public long Epoch { get; set; }

    public DateTimeOffset? LastSyncAtUtc { get; set; }
}

/// <summary>
/// File owner for <see cref="SyncStateDocument"/>. Dumb durable holder —
/// the engine (a later module) owns the semantics; this class owns atomic
/// reads/writes plus corruption recovery.
/// </summary>
public sealed class SyncStateStore
{
    private readonly string _statePath;

    public SyncStateStore(string? statePath = null)
    {
        _statePath = statePath ?? Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "sync",
            "state.json");
    }

    public Task<SyncStateDocument> LoadAsync() =>
        ResilientJsonStore.LoadAsync(
            _statePath,
            static json => JsonSerializer.Deserialize(
                json, SyncStateJsonContext.Default.SyncStateDocument)
                ?? new SyncStateDocument(),
            static () => new SyncStateDocument(),
            "SyncState");

    public Task SaveAsync(SyncStateDocument document) =>
        ResilientJsonStore.SaveAsync(
            _statePath,
            JsonSerializer.SerializeToUtf8Bytes(
                document, SyncStateJsonContext.Default.SyncStateDocument));

    /// <inheritdoc cref="WidgetLayoutStore.SaveCheckedAsync"/>
    public async Task<bool> SaveCheckedAsync(SyncStateDocument document)
    {
        try
        {
            await SaveAsync(document);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log($"[SyncState] Save failed: {ex}");
            return false;
        }
    }
}
