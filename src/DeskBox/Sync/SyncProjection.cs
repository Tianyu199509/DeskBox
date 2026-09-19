using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Sync;

/// <summary>
/// Local domain record → <see cref="SyncEnvelope"/> projection
/// (sync-protocol-contract §3.3). Pure transformation, no I/O beyond
/// hashing attachment bytes for blob references.
///
/// Path discipline: payload file paths are always collection-relative
/// (<c>attachments/report.pdf</c>, <c>images/&lt;hash&gt;.png</c>) — a local
/// absolute path never crosses the wire. Managed attachments additionally
/// earn a content-addressed blob reference; linked attachments keep only
/// their basename as a display stub (the record still renders, degraded).
/// </summary>
public static class SyncProjection
{
    /// <summary>Per-domain payload schema versions (envelope.schema_version,
    /// §3.1). Bump the domain's version only when its payload shape changes.</summary>
    public const int TodoSchemaVersion = 1;
    public const int QuickCaptureSchemaVersion = 1;
    public const int WidgetStyleSchemaVersion = 1;

    /// <summary>todo-data: one collection per widget — the collection id IS
    /// the widget id (§2.1), so independently created todo widgets on two
    /// devices never merge. <paramref name="managedAttachmentRoot"/> is the
    /// todo store's attachment directory — only paths inside it may be read
    /// for blob references (§5 boundary).</summary>
    public static async Task<SyncEnvelope> FromTodoItemAsync(
        TodoItem item,
        string widgetId,
        string managedAttachmentRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedAttachmentRoot);

        // §4: a deleted record is a tombstone — no payload, and its bytes
        // are never even opened for hashing.
        if (item.IsDeleted)
        {
            return Tombstone(SyncDomains.TodoData, widgetId, item.Id);
        }

        JsonObject payload = JsonSerializer
            .SerializeToNode(item, TodoJsonContext.Default.TodoItem)!
            .AsObject();

        List<SyncAttachmentRef> blobRefs = await ProjectAttachmentsAsync(
            payload["attachments"] as JsonArray,
            managedAttachmentRoot,
            cancellationToken);

        return new SyncEnvelope
        {
            Domain = SyncDomains.TodoData,
            CollectionId = widgetId,
            EntityId = item.Id,
            SchemaVersion = TodoSchemaVersion,
            Deleted = item.IsDeleted,
            DeviceId = item.DeviceId ?? DeviceIdentity.Id,
            OperationId = Guid.NewGuid().ToString("N"),
            Payload = payload,
            Attachments = blobRefs
        };
    }

    /// <summary>quick-capture-data: singleton collection (§2.1) — items
    /// merge across devices by construction. <paramref name="managedAttachmentRoot"/>
    /// and <paramref name="imageRoot"/> bound which local paths may be read
    /// for blob references — anything outside is degraded to a basename
    /// stub (§5 boundary).</summary>
    public static async Task<SyncEnvelope> FromQuickCaptureItemAsync(
        QuickCaptureItem item,
        string managedAttachmentRoot,
        string imageRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentException.ThrowIfNullOrWhiteSpace(managedAttachmentRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(imageRoot);

        // §4: a deleted record is a tombstone — no payload, and its bytes
        // are never even opened for hashing.
        if (item.IsDeleted)
        {
            return Tombstone(
                SyncDomains.QuickCaptureData,
                SyncDomains.QuickCaptureCollection,
                item.Id);
        }

        JsonObject payload = JsonSerializer
            .SerializeToNode(item, QuickCaptureJsonContext.Default.QuickCaptureItem)!
            .AsObject();

        List<SyncAttachmentRef> blobRefs = await ProjectAttachmentsAsync(
            payload["attachments"] as JsonArray,
            managedAttachmentRoot,
            cancellationToken);

        // The capture image lives under quick-capture/images/ and is already
        // content-hash named — it rides the blob channel like any managed
        // attachment, with an images/ collection-relative payload path.
        if (TryGetString(payload["imagePath"], out string? imagePath) &&
            !string.IsNullOrEmpty(imagePath))
        {
            string basename = Path.GetFileName(imagePath);
            if (!string.IsNullOrEmpty(basename) &&
                IsInsideManagedRoot(imagePath, imageRoot))
            {
                payload["imagePath"] = $"images/{basename}";
                if (File.Exists(imagePath))
                {
                    blobRefs.Add(await BlobRefAsync(basename, imagePath, cancellationToken));
                }
            }
            else
            {
                // Outside the managed image root (or nameless): keep only a
                // display-stub basename — the bytes stay on this device.
                if (!string.IsNullOrEmpty(basename))
                {
                    App.Log(
                        $"[SyncProjection] Refusing quick-capture image outside " +
                        $"'{imageRoot}': '{imagePath}'");
                }

                payload["imagePath"] = basename ?? string.Empty;
            }
        }

        return new SyncEnvelope
        {
            Domain = SyncDomains.QuickCaptureData,
            CollectionId = SyncDomains.QuickCaptureCollection,
            EntityId = item.Id,
            SchemaVersion = QuickCaptureSchemaVersion,
            Deleted = item.IsDeleted,
            DeviceId = item.DeviceId ?? DeviceIdentity.Id,
            OperationId = Guid.NewGuid().ToString("N"),
            Payload = payload,
            Attachments = blobRefs
        };
    }

    /// <summary>widget-style: singleton collection; the "shell" entity
    /// carries every shell-whitelist key, each widget id carries its style
    /// patch. The whitelist is enforced by the same code path that builds
    /// backup style documents — there is no second list to drift (§3.3).</summary>
    public static IEnumerable<SyncEnvelope> FromWidgetStyle(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        JsonObject document = JsonNode
            .Parse(WidgetStyleBackupProjection.Serialize(settings))!
            .AsObject();

        if (document["shell"] is JsonObject shell)
        {
            yield return new SyncEnvelope
            {
                Domain = SyncDomains.WidgetStyle,
                CollectionId = SyncDomains.WidgetStyleCollection,
                EntityId = SyncDomains.ShellEntityId,
                SchemaVersion = WidgetStyleSchemaVersion,
                DeviceId = DeviceIdentity.Id,
                OperationId = Guid.NewGuid().ToString("N"),
                Payload = (JsonObject)shell.DeepClone()
            };
        }

        if (document["widgets"] is JsonObject widgets)
        {
            foreach ((string widgetId, JsonNode? style) in widgets)
            {
                if (style is not JsonObject styleObject)
                {
                    continue;
                }

                yield return new SyncEnvelope
                {
                    Domain = SyncDomains.WidgetStyle,
                    CollectionId = SyncDomains.WidgetStyleCollection,
                    EntityId = widgetId,
                    SchemaVersion = WidgetStyleSchemaVersion,
                    DeviceId = DeviceIdentity.Id,
                    OperationId = Guid.NewGuid().ToString("N"),
                    Payload = (JsonObject)styleObject.DeepClone()
                };
            }
        }
    }

    /// <summary>Tombstone envelope for a locally deleted entity (§4):
    /// deleted=true, no payload, no blob references.</summary>
    public static SyncEnvelope Tombstone(string domain, string collectionId, string entityId)
    {
        if (!SyncDomains.IsKnownDomain(domain))
        {
            throw new ArgumentException($"Unknown sync domain '{domain}'.", nameof(domain));
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);

        return new SyncEnvelope
        {
            Domain = domain,
            CollectionId = collectionId,
            EntityId = entityId,
            Deleted = true,
            DeviceId = DeviceIdentity.Id,
            OperationId = Guid.NewGuid().ToString("N")
        };
    }

    /// <summary>Rewrites an attachments array in place and collects blob
    /// references for the files that exist locally. "managed" alone does not
    /// earn a blob: the path must provably resolve inside
    /// <paramref name="managedAttachmentRoot"/>, otherwise the record is
    /// degraded to a basename stub and its bytes are never read.</summary>
    private static async Task<List<SyncAttachmentRef>> ProjectAttachmentsAsync(
        JsonArray? payloadAttachments,
        string managedAttachmentRoot,
        CancellationToken cancellationToken)
    {
        var blobRefs = new List<SyncAttachmentRef>();
        if (payloadAttachments is null)
        {
            return blobRefs;
        }

        foreach (JsonNode? node in payloadAttachments)
        {
            if (node is not JsonObject attachment ||
                !TryGetString(attachment["filePath"], out string? filePath) ||
                string.IsNullOrEmpty(filePath))
            {
                continue;
            }

            string basename = Path.GetFileName(filePath);
            bool managed = TryGetString(attachment["storageMode"], out string? mode) &&
                           string.Equals(mode, "managed", StringComparison.OrdinalIgnoreCase);
            if (managed && IsInsideManagedRoot(filePath, managedAttachmentRoot))
            {
                attachment["filePath"] = $"attachments/{basename}";
                if (File.Exists(filePath))
                {
                    blobRefs.Add(await BlobRefAsync(basename, filePath, cancellationToken));
                }
            }
            else
            {
                if (managed)
                {
                    // A "managed" record pointing outside the managed root is
                    // tampered or badly imported data — degrade it like a
                    // linked attachment so its bytes never leave the device.
                    App.Log(
                        $"[SyncProjection] Refusing managed attachment outside " +
                        $"'{managedAttachmentRoot}': '{filePath}'");
                }

                // Linked attachment: the absolute path stays on this device;
                // the basename survives as a display-only stub (§3.3).
                attachment["filePath"] = basename;
            }
        }

        return blobRefs;
    }

    /// <summary>Resolved-path containment: junctions and symlinks must not
    /// smuggle a path into the managed root. Unresolvable identity fails
    /// closed — no containment, no read.</summary>
    private static bool IsInsideManagedRoot(string path, string managedRoot) =>
        FileService.TryIsPathUnderDirectoryResolved(path, managedRoot, out bool inside) &&
        inside;

    private static async Task<SyncAttachmentRef> BlobRefAsync(
        string name,
        string localPath,
        CancellationToken cancellationToken)
    {
        // Stream the hash: managed attachments may be multi-hundred-MB
        // videos, and materializing them as byte[] would spike the LOH the
        // memory work is actively fighting.
        await using var stream = new FileStream(
            localPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return new SyncAttachmentRef
        {
            Name = name,
            BlobId = Convert.ToHexString(hash).ToLowerInvariant(),
            Size = stream.Length
        };
    }

    private static bool TryGetString(JsonNode? node, out string? value)
    {
        value = node is JsonValue jsonValue && jsonValue.TryGetValue(out string? s) ? s : null;
        return node is not null;
    }
}
