using System.Text.Json;
using DeskBox.Services;
using DeskBox.Sync;

namespace DeskBox.Core.Persistence;

/// <summary>
/// Durable outbox for the sync engine (sync-protocol-contract §6.3/§8):
/// one file per pending operation under
/// <c>data/sync/outbox/&lt;domain&gt;/&lt;operation_id&gt;.json</c>.
/// Device-domain state — never backed up and never synced itself.
/// Enqueue is atomic via <see cref="ResilientJsonStore"/>; a crash mid-write
/// leaves either the old entry or the new one, never a torn file. Dequeue is
/// the post-accept tombstone; an entry that survives a kill is simply pushed
/// again — operation_id makes the retry idempotent server-side.
/// </summary>
public sealed class SyncOutboxStore
{
    private readonly string _outboxDirectory;

    public SyncOutboxStore(string? outboxDirectory = null)
    {
        _outboxDirectory = outboxDirectory ?? Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "sync",
            "outbox");
    }

    /// <summary>Queues one envelope for push. The file name is the
    /// operation_id, so re-enqueueing the same operation replaces it —
    /// dedup is free at the storage layer too.</summary>
    public Task EnqueueAsync(SyncEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!SyncDomains.IsKnownDomain(envelope.Domain))
        {
            throw new ArgumentException(
                $"Unknown sync domain '{envelope.Domain}'.", nameof(envelope));
        }
        if (string.IsNullOrWhiteSpace(envelope.OperationId) ||
            string.IsNullOrWhiteSpace(envelope.CollectionId) ||
            string.IsNullOrWhiteSpace(envelope.EntityId))
        {
            throw new ArgumentException(
                "Envelope requires operation_id, collection_id and entity_id.",
                nameof(envelope));
        }

        string path = EntryPath(envelope.Domain, envelope.OperationId);
        return ResilientJsonStore.SaveAsync(
            path,
            tempPath => Task.Run(async () =>
            {
                await using var stream = new FileStream(
                    tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await JsonSerializer.SerializeAsync(
                    stream, envelope, SyncJsonContext.Default.SyncEnvelope);
                stream.Flush(flushToDisk: true);
            }));
    }

    /// <summary>All pending envelopes for one domain, in file-name order.
    /// A corrupt entry is quarantined as <c>.corrupt-*</c> and skipped — one
    /// bad file can never wedge the queue (the operation would have been
    /// pushed again anyway: outbox entries are push intents, not data).</summary>
    public async Task<IReadOnlyList<SyncEnvelope>> ListAsync(string domain)
    {
        string directory = DomainDirectory(domain);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var envelopes = new List<SyncEnvelope>();
        foreach (string path in Directory
                     .EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            ResilientJsonLoadResult<SyncEnvelope> result =
                await ResilientJsonStore.LoadWithResultAsync(
                    path,
                    json => JsonSerializer.Deserialize(
                        json, SyncJsonContext.Default.SyncEnvelope)!,
                    static () => null!,
                    "SyncOutbox");
            if ((result.Source is ResilientJsonLoadSource.Primary or
                                  ResilientJsonLoadSource.Backup) &&
                result.Value is { } envelope)
            {
                envelopes.Add(envelope);
            }
        }

        return envelopes;
    }

    /// <summary>Drops a pending operation after the server accepted it.
    /// Returns false when the entry was already gone — a second dequeuer
    /// is not an error, the push was simply confirmed twice.</summary>
    public Task<bool> DequeueAsync(string domain, string operationId)
    {
        string path = EntryPath(domain, operationId);
        if (!File.Exists(path))
        {
            return Task.FromResult(false);
        }

        File.Delete(path);
        return Task.FromResult(true);
    }

    public int PendingCount(string domain)
    {
        string directory = DomainDirectory(domain);
        return Directory.Exists(directory)
            ? Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly).Count()
            : 0;
    }

    private string DomainDirectory(string domain)
    {
        if (!SyncDomains.IsKnownDomain(domain))
        {
            throw new ArgumentException($"Unknown sync domain '{domain}'.", nameof(domain));
        }

        return Path.Combine(_outboxDirectory, domain);
    }

    private string EntryPath(string domain, string operationId) =>
        Path.Combine(DomainDirectory(domain), SanitizeOperationId(operationId) + ".json");

    /// <summary>operation_id is a client GUID — filename-safe by contract —
    /// but the queue is only as trustworthy as its inputs, so anything that
    /// is not <c>[a-zA-Z0-9_-]</c> is rejected outright: silently stripping
    /// could fold two distinct operations onto one file name.</summary>
    private static string SanitizeOperationId(string operationId)
    {
        return operationId.Length > 0 &&
               operationId.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            ? operationId
            : throw new ArgumentException("operation_id is not filename-safe.", nameof(operationId));
    }
}
