using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Sync;
using Xunit;

namespace DeskBox.Tests;

/// <summary>
/// Projection contract for the 2C PR-1 plumbing (sync-protocol-contract
/// §3.3): payloads ride the domain stores' own wire profile, file paths
/// leave the device only in collection-relative form, and the style domain
/// reuses the backup whitelist rather than a second list.
/// </summary>
public sealed class SyncProjectionTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(), "deskbox-sync-projection-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
        }
    }

    private string WriteAttachment(string name, byte[] bytes)
    {
        string path = Path.Combine(_tempRoot, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static string SerializeEnvelope(SyncEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, SyncJsonContext.Default.SyncEnvelope);

    // ── todo-data ─────────────────────────────────────────────────────

    [Fact]
    public async Task TodoItem_PayloadMatchesDomainWireShape()
    {
        var item = new TodoItem
        {
            Text = "buy milk",
            IsCompleted = true,
            ColorMarker = "red",
            DueDate = new DateTimeOffset(2026, 9, 20, 8, 0, 0, TimeSpan.Zero),
            // Stores normalize this on save; the projection backfills when
            // it meets an unnormalized record.
            DeviceId = DeviceIdentity.Id
        };

        SyncEnvelope envelope = await SyncProjection.FromTodoItemAsync(
            item,
            "widget-9",
            Path.Combine(_tempRoot, "widgets", "widget-9", "attachments"));

        Assert.Equal(SyncDomains.TodoData, envelope.Domain);
        // §2.1: the todo collection id IS the widget id.
        Assert.Equal("widget-9", envelope.CollectionId);
        Assert.Equal(item.Id, envelope.EntityId);
        Assert.Equal(DeviceIdentity.Id, envelope.DeviceId);
        Assert.False(string.IsNullOrEmpty(envelope.OperationId));
        Assert.Equal(SyncProjection.TodoSchemaVersion, envelope.SchemaVersion);

        // The payload is the domain record in the domain store's own
        // profile — camelCase keys, identical to what todo.json would hold.
        JsonObject payload = envelope.Payload!;
        Assert.Equal(item.Id, payload["id"]!.GetValue<string>());
        Assert.Equal("buy milk", payload["text"]!.GetValue<string>());
        Assert.True(payload["isCompleted"]!.GetValue<bool>());
        Assert.False(payload.ContainsKey("IsCompleted"));
        Assert.False(payload.ContainsKey("DeviceId"));
        Assert.Equal(DeviceIdentity.Id, payload["deviceId"]!.GetValue<string>());
    }

    [Fact]
    public async Task TodoItem_ManagedAttachment_RelativePathAndBlobRef()
    {
        byte[] bytes = [1, 2, 3, 4, 5];
        string absolutePath = WriteAttachment(
            Path.Combine("widgets", "w1", "attachments", "report.pdf"), bytes);
        string expectedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var item = new TodoItem
        {
            Text = "with attachment",
            Attachments =
            [
                new TodoAttachment
                {
                    FilePath = absolutePath,
                    DisplayName = "report.pdf",
                    StorageMode = TodoAttachment.ManagedStorageMode
                }
            ]
        };

        SyncEnvelope envelope = await SyncProjection.FromTodoItemAsync(
            item,
            "w1",
            Path.Combine(_tempRoot, "widgets", "w1", "attachments"));

        // Payload path is collection-relative — the absolute path stays home.
        Assert.Equal(
            "attachments/report.pdf",
            envelope.Payload!["attachments"]![0]!["filePath"]!.GetValue<string>());
        string wire = SerializeEnvelope(envelope);
        Assert.DoesNotContain(_tempRoot.Replace("\\", "\\\\"), wire);
        Assert.DoesNotContain("C:\\\\", wire);

        SyncAttachmentRef blob = Assert.Single(envelope.Attachments);
        Assert.Equal("report.pdf", blob.Name);
        Assert.Equal(expectedHash, blob.BlobId);
        Assert.Equal(bytes.LongLength, blob.Size);
    }

    [Fact]
    public async Task TodoItem_LinkedAttachment_KeepsBasenameOnly()
    {
        var item = new TodoItem
        {
            Text = "linked",
            Attachments =
            [
                new TodoAttachment
                {
                    FilePath = @"D:\secret\folder\private.docx",
                    DisplayName = "private.docx",
                    StorageMode = TodoAttachment.LinkedStorageMode
                }
            ]
        };

        SyncEnvelope envelope = await SyncProjection.FromTodoItemAsync(
            item,
            "w1",
            Path.Combine(_tempRoot, "widgets", "w1", "attachments"));

        // Basename survives as a display stub; the absolute path never
        // leaves the device, and a linked file earns no blob reference.
        Assert.Equal(
            "private.docx",
            envelope.Payload!["attachments"]![0]!["filePath"]!.GetValue<string>());
        Assert.Equal(
            TodoAttachment.LinkedStorageMode,
            envelope.Payload!["attachments"]![0]!["storageMode"]!.GetValue<string>());
        Assert.Empty(envelope.Attachments);
        Assert.DoesNotContain("secret", SerializeEnvelope(envelope));
    }

    [Fact]
    public async Task TodoItem_MissingManagedFile_RecordKeptNoBlobRef()
    {
        var item = new TodoItem
        {
            Text = "dangling",
            Attachments =
            [
                new TodoAttachment
                {
                    FilePath = Path.Combine(_tempRoot, "gone.pdf"),
                    DisplayName = "gone.pdf",
                    StorageMode = TodoAttachment.ManagedStorageMode
                }
            ]
        };

        SyncEnvelope envelope = await SyncProjection.FromTodoItemAsync(
            item,
            "w1",
            _tempRoot);

        // The record still projects (the receiving side degrades the
        // attachment display) — only the blob reference is skipped.
        Assert.Equal(
            "attachments/gone.pdf",
            envelope.Payload!["attachments"]![0]!["filePath"]!.GetValue<string>());
        Assert.Empty(envelope.Attachments);
    }

    [Fact]
    public async Task TodoItem_ManagedAttachmentOutsideRoot_DegradesToStub()
    {
        // A "managed" record whose path escapes the managed root (tampered
        // or badly imported data) must never be opened for hashing — it
        // degrades to the linked-style basename stub (§5 boundary).
        string secretPath = WriteAttachment(
            Path.Combine("outside", "secret.pdf"), [7, 7, 7]);
        string secretHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData([7, 7, 7])).ToLowerInvariant();
        var item = new TodoItem
        {
            Text = "tampered",
            Attachments =
            [
                new TodoAttachment
                {
                    FilePath = secretPath,
                    DisplayName = "secret.pdf",
                    StorageMode = TodoAttachment.ManagedStorageMode
                }
            ]
        };

        SyncEnvelope envelope = await SyncProjection.FromTodoItemAsync(
            item,
            "w1",
            Path.Combine(_tempRoot, "widgets", "w1", "attachments"));

        Assert.Equal(
            "secret.pdf",
            envelope.Payload!["attachments"]![0]!["filePath"]!.GetValue<string>());
        Assert.Empty(envelope.Attachments);
        Assert.DoesNotContain(secretHash, SerializeEnvelope(envelope));
    }

    [Fact]
    public async Task TodoItem_Deleted_ProjectsTombstoneWithoutBlobReads()
    {
        // §4: a deleted record is a tombstone — no payload, and its
        // attachments are never even opened for hashing.
        string filePath = WriteAttachment(
            Path.Combine("widgets", "w1", "attachments", "gone.pdf"), [1]);
        var item = new TodoItem
        {
            Text = "deleted",
            IsDeleted = true,
            Attachments =
            [
                new TodoAttachment
                {
                    FilePath = filePath,
                    StorageMode = TodoAttachment.ManagedStorageMode
                }
            ]
        };

        SyncEnvelope envelope = await SyncProjection.FromTodoItemAsync(
            item,
            "w1",
            Path.Combine(_tempRoot, "widgets", "w1", "attachments"));

        Assert.True(envelope.Deleted);
        Assert.Null(envelope.Payload);
        Assert.Empty(envelope.Attachments);
        Assert.Equal(item.Id, envelope.EntityId);
        Assert.Equal("w1", envelope.CollectionId);
    }

    // ── quick-capture-data ────────────────────────────────────────────

    [Fact]
    public async Task QuickCaptureItem_ImagePath_IsCollectionRelative()
    {
        byte[] bytes = [9, 9, 9];
        string imagePath = WriteAttachment(
            Path.Combine("quick-capture", "images", "aabbcc.png"), bytes);
        var item = new QuickCaptureItem
        {
            Type = QuickCaptureItemType.Image,
            ImagePath = imagePath
        };

        SyncEnvelope envelope = await SyncProjection.FromQuickCaptureItemAsync(
            item,
            Path.Combine(_tempRoot, "quick-capture", "attachments"),
            Path.Combine(_tempRoot, "quick-capture", "images"));

        Assert.Equal(SyncDomains.QuickCaptureData, envelope.Domain);
        Assert.Equal(SyncDomains.QuickCaptureCollection, envelope.CollectionId);
        Assert.Equal(item.Id, envelope.EntityId);
        Assert.Equal("images/aabbcc.png", envelope.Payload!["imagePath"]!.GetValue<string>());
        // The enum rides the domain profile's string form.
        Assert.Equal("Image", envelope.Payload!["type"]!.GetValue<string>());
        Assert.Single(envelope.Attachments);
    }

    [Fact]
    public async Task QuickCaptureItem_Deleted_ProjectsTombstone()
    {
        // §4: deleted envelopes carry no payload — the record body and its
        // image bytes never cross the boundary.
        string imagePath = WriteAttachment(
            Path.Combine("quick-capture", "images", "ccdd.png"), [2, 2]);
        var item = new QuickCaptureItem
        {
            IsDeleted = true,
            Body = "gone",
            ImagePath = imagePath
        };

        SyncEnvelope envelope = await SyncProjection.FromQuickCaptureItemAsync(
            item,
            Path.Combine(_tempRoot, "quick-capture", "attachments"),
            Path.Combine(_tempRoot, "quick-capture", "images"));

        Assert.True(envelope.Deleted);
        Assert.Null(envelope.Payload);
        Assert.Empty(envelope.Attachments);
    }

    [Fact]
    public async Task QuickCaptureItem_ImageOutsideImageRoot_DegradesToStub()
    {
        // An imagePath escaping the managed image root is kept as a
        // display-stub basename only — its bytes never leave the device.
        string secretImage = WriteAttachment("secret.png", [5, 5, 5]);
        var item = new QuickCaptureItem
        {
            Type = QuickCaptureItemType.Image,
            ImagePath = secretImage
        };

        SyncEnvelope envelope = await SyncProjection.FromQuickCaptureItemAsync(
            item,
            Path.Combine(_tempRoot, "quick-capture", "attachments"),
            Path.Combine(_tempRoot, "quick-capture", "images"));

        Assert.Equal(
            "secret.png",
            envelope.Payload!["imagePath"]!.GetValue<string>());
        Assert.Empty(envelope.Attachments);
    }

    // ── widget-style ──────────────────────────────────────────────────

    [Fact]
    public void WidgetStyle_ShellAndPerWidgetEntities()
    {
        var settings = new AppSettings
        {
            WidgetOpacity = 0.5
        };
        settings.Widgets.Add(new WidgetConfig
        {
            Id = "w1",
            WidgetKind = WidgetKind.Todo,
            ViewMode = ViewMode.List,
            MappedFolderPath = @"D:\secret\folder",
            X = 123
        });

        List<SyncEnvelope> envelopes = SyncProjection.FromWidgetStyle(settings).ToList();

        SyncEnvelope shell = Assert.Single(envelopes, e => e.EntityId == SyncDomains.ShellEntityId);
        Assert.Equal(SyncDomains.WidgetStyle, shell.Domain);
        Assert.Equal(SyncDomains.WidgetStyleCollection, shell.CollectionId);
        Assert.Equal(0.5, shell.Payload!["widgetOpacity"]!.GetValue<double>());

        SyncEnvelope widget = Assert.Single(envelopes, e => e.EntityId == "w1");
        Assert.Equal("List", widget.Payload!["viewMode"]!.GetValue<string>());
        // Whitelist only: geometry and file bindings never leave the device.
        Assert.False(widget.Payload!.ContainsKey("x"));
        Assert.False(widget.Payload!.ContainsKey("mappedFolderPath"));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(widget.Payload));
    }

    // ── tombstone ─────────────────────────────────────────────────────

    [Fact]
    public void Tombstone_CarriesNoPayloadAndNoBlobs()
    {
        SyncEnvelope envelope = SyncProjection.Tombstone(
            SyncDomains.TodoData, "w1", "entity-9");

        Assert.True(envelope.Deleted);
        Assert.Null(envelope.Payload);
        Assert.Empty(envelope.Attachments);
        Assert.Equal("entity-9", envelope.EntityId);
        Assert.False(string.IsNullOrEmpty(envelope.OperationId));
    }

    [Fact]
    public void Tombstone_RejectsUnknownDomain()
    {
        Assert.Throws<ArgumentException>(
            () => SyncProjection.Tombstone("settings", "c", "e"));
    }
}
