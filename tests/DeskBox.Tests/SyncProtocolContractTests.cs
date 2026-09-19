using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Sync;
using Xunit;

namespace DeskBox.Tests;

/// <summary>
/// Sync-protocol legislation for the 2C PR-1 plumbing
/// (sync-protocol-contract §2.3/§3.1/§10): the envelope's snake_case wire
/// shape is pinned key-by-key, domain records carry no protocol state, the
/// domain vocabulary matches the backup manifest names, and the settings
/// save hot path has zero sync references.
/// </summary>
public sealed class SyncProtocolContractTests
{
    private static string ReadSource(string repoRelativePath) =>
        File.ReadAllText(TestPaths.FromRepository(repoRelativePath));

    // ── §3.1 wire shape ───────────────────────────────────────────────

    [Fact]
    public void Envelope_SerializesWithExactContractKeys()
    {
        var envelope = new SyncEnvelope
        {
            Domain = SyncDomains.TodoData,
            CollectionId = "widget-1",
            EntityId = "entity-1",
            SchemaVersion = 1,
            Deleted = false,
            DeviceId = "device-a",
            OperationId = "op-1",
            Payload = new JsonObject { ["id"] = "entity-1" },
            Attachments =
            [
                new SyncAttachmentRef { Name = "a.pdf", BlobId = "ab12", Size = 7 }
            ]
        };

        JsonObject wire = JsonNode
            .Parse(JsonSerializer.Serialize(envelope, SyncJsonContext.Default.SyncEnvelope))!
            .AsObject();

        Assert.Equal(
            [
                "attachments", "collection_id", "deleted", "device_id",
                "domain", "entity_id", "operation_id", "payload",
                "schema_version"
            ],
            wire.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("todo-data", wire["domain"]!.GetValue<string>());
        Assert.Equal("widget-1", wire["collection_id"]!.GetValue<string>());
        Assert.Equal("entity-1", wire["entity_id"]!.GetValue<string>());
        Assert.Equal(1, wire["schema_version"]!.GetValue<int>());
        Assert.Equal("device-a", wire["device_id"]!.GetValue<string>());
        Assert.Equal("op-1", wire["operation_id"]!.GetValue<string>());

        JsonObject attachment = Assert.Single(wire["attachments"]!.AsArray())!.AsObject();
        Assert.Equal(
            ["blob_id", "name", "size"],
            attachment.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.Equal("ab12", attachment["blob_id"]!.GetValue<string>());
    }

    [Fact]
    public void Envelope_ToleratesUnknownWireFields()
    {
        // A newer build's envelope must still parse — unknown fields ride
        // along untouched (§3.1 forward-compat).
        SyncEnvelope? envelope = JsonSerializer.Deserialize(
            """
            {"domain":"todo-data","collection_id":"w","entity_id":"e",
             "schema_version":99,"deleted":false,"device_id":"d",
             "operation_id":"op","payload":null,"attachments":[],
             "future_wire_field":{"nested":true}}
            """,
            SyncJsonContext.Default.SyncEnvelope);

        Assert.NotNull(envelope);
        Assert.Equal(99, envelope!.SchemaVersion);
        Assert.Equal("todo-data", envelope.Domain);
    }

    // ── §2.3 domain purity ────────────────────────────────────────────

    [Fact]
    public void DomainRecords_CarryOnlyTheFourEmbeddedSyncFields()
    {
        // The four embedded fields (Id/DeviceId/UpdatedAt/IsDeleted) are
        // domain members with independent local uses. Anything that smells
        // like protocol state (revision/cursor/operation/epoch/server)
        // belongs in data/sync/ — never on a domain record.
        string[] forbiddenTokens =
        [
            "revision", "cursor", "operation", "epoch", "server", "outbox", "envelope"
        ];
        Type[] domainTypes =
        [
            typeof(TodoItem), typeof(TodoStep), typeof(TodoAttachment),
            typeof(QuickCaptureItem), typeof(QuickCaptureStoreData),
            typeof(TodoWidgetData)
        ];

        var offenders = new List<string>();
        foreach (Type type in domainTypes)
        {
            foreach (PropertyInfo property in type.GetProperties(
                         BindingFlags.Instance | BindingFlags.Public))
            {
                string name = property.Name.ToLowerInvariant();
                if (forbiddenTokens.Any(name.Contains))
                {
                    offenders.Add($"{type.Name}.{property.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Protocol state must not enter domain records:\n" +
            string.Join('\n', offenders));
    }

    // ── §2.1 domain vocabulary ────────────────────────────────────────

    [Fact]
    public void SyncDomainNames_MatchBackupManifestNames()
    {
        // The same semantic boundary carries both transports; the wire
        // vocabulary must not fork.
        Assert.Equal(
            SyncDomains.TodoData,
            CloudBackupDomains.ToManifestName(CloudBackupDomain.TodoData));
        Assert.Equal(
            SyncDomains.QuickCaptureData,
            CloudBackupDomains.ToManifestName(CloudBackupDomain.QuickCaptureData));
        Assert.Equal(
            SyncDomains.WidgetStyle,
            CloudBackupDomains.ToManifestName(CloudBackupDomain.WidgetStyle));
    }

    // ── §10.7 hot-path isolation ──────────────────────────────────────

    [Fact]
    public void SettingsSaveHotPath_HasNoSyncReferences()
    {
        // The sync engine is event-driven off its own queue — the settings
        // debounce/save path must stay free of sync code entirely.
        string[] hotPathSources =
        [
            "src/DeskBox/Services/SettingsService.cs",
            "src/DeskBox/Services/WidgetLayoutStore.cs",
            "src/DeskBox/Services/ResilientJsonStore.cs",
            "src/DeskBox/Services/DesktopOrganizationHistoryStore.cs"
        ];
        string[] forbidden =
        [
            "DeskBox.Sync", "SyncOutbox", "SyncStateStore",
            "SyncRevisionsStore", "SyncEnvelope", "SyncProjection"
        ];

        var offenders = new List<string>();
        foreach (string path in hotPathSources)
        {
            string source = ReadSource(path);
            foreach (string token in forbidden.Where(source.Contains))
            {
                offenders.Add($"{path} references {token}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The settings save hot path must stay free of sync code:\n" +
            string.Join('\n', offenders));
    }
}
