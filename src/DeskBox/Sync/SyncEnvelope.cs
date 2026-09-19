using System.Text.Json.Nodes;

namespace DeskBox.Sync;

/// <summary>
/// Wire envelope for one syncable entity (sync-protocol-contract §3.1).
/// Same shape travels up (outbox → push) and down (pull → projection);
/// it is a wire format, not a domain type — protocol fields
/// (revision/cursor/operation state) never enter domain records.
/// </summary>
public sealed class SyncEnvelope
{
    public string Domain { get; set; } = string.Empty;

    /// <summary>todo-data: the widget id. Singleton domains use the fixed
    /// collection names on <see cref="SyncDomains"/>.</summary>
    public string CollectionId { get; set; } = string.Empty;

    /// <summary>Domain record <c>Id</c>, or <c>"shell"</c>/widget-id in the
    /// widget-style domain.</summary>
    public string EntityId { get; set; } = string.Empty;

    /// <summary>Per-domain payload schema version (§3.1). A receiver that
    /// meets a newer version stores the raw envelope unprojected.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Tombstone flag (§4). Deleted envelopes carry no payload.</summary>
    public bool Deleted { get; set; }

    /// <summary>Id of the device that last wrote the entity.</summary>
    public string? DeviceId { get; set; }

    /// <summary>Client-side idempotency key: retrying the same operation
    /// must not produce a second entity (§3.1).</summary>
    public string OperationId { get; set; } = string.Empty;

    /// <summary>Domain record serialized with the domain store's own
    /// profile (camelCase + string enums) — the payload is literally what
    /// the domain file would hold for this record.</summary>
    public JsonObject? Payload { get; set; }

    /// <summary>Attachment blob references — content-addressed, uploaded
    /// before the envelope that cites them (§5).</summary>
    public List<SyncAttachmentRef> Attachments { get; set; } = [];
}

/// <summary>One attachment blob reference inside an envelope (§5).</summary>
public sealed class SyncAttachmentRef
{
    /// <summary>Collection-relative file name, e.g. <c>report.pdf</c> —
    /// the payload references it as <c>attachments/report.pdf</c>.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Content address: lowercase hex SHA-256 of the bytes.</summary>
    public string BlobId { get; set; } = string.Empty;

    public long Size { get; set; }
}
