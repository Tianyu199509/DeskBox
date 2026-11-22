using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskBox.Services;

/// <summary>
/// Coordinates explicit restore operations and external drag-outs with the
/// desktop watcher. Entries are scoped to exact destination paths; completed
/// moves additionally require the observed file fingerprint to match, and
/// pending entries require evidence that the observed file is that
/// operation's arrival (the source vanished — a same-volume move keeps its
/// original timestamps — or the destination was created after registration).
/// When a ledger path is configured the claim set survives process restarts.
/// </summary>
internal sealed class DesktopAutoOrganizationSuppressionRegistry
{
    // The longest watcher deferral preset is 12 hours; entries must outlive
    // it plus margin or a suppressed file is organized anyway. Evidence-
    // gated entries cannot false-positive on an unrelated file that merely
    // shares the claimed path, so a long lifetime is safe.
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(13);

    // A drag-out's unmaterialized destinations get at least this pending
    // window; the actual expiry is the maximum of this floor and the
    // caller-provided organization-delay horizon so slow arrivals still out-
    // live the watcher's deferral on every preset tier.
    private static readonly TimeSpan DraggedArrivalPendingLifetime =
        TimeSpan.FromSeconds(90);

    // Buffer added on top of the configured organization delay so entry
    // expiry covers the watcher's settle window, stability probes and retry
    // defers that follow the deferral deadline.
    private static readonly TimeSpan EvaluationHorizonMargin =
        TimeSpan.FromHours(1);

    // Creation-time comparison tolerance for file-system timestamp
    // granularity and clock skew between capture and observation.
    private static readonly TimeSpan ArrivalCreationTolerance =
        TimeSpan.FromSeconds(2);

    private const string DraggedArrivalOperationId = "drag-out";
    private const int LedgerVersion = 1;

    // Bounds the persisted ledger so a runaway writer cannot turn it back
    // into a multi-megabyte store. Real activity stays in the tens of
    // entries; eviction order is closest-to-expiry-first, which only ever
    // drops claims with the least remaining protection.
    internal const int MaxEntries = 2048;

    private readonly object _gate = new();
    private readonly Dictionary<string, SuppressionEntry> _entries =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly TimeSpan _lifetime;
    private readonly TimeSpan _evaluationMargin;
    private readonly TimeSpan _pendingLifetime;
    private readonly string? _ledgerPath;
    private Task _persistTail = Task.CompletedTask;
    private bool _ledgerLoaded;

    public DesktopAutoOrganizationSuppressionRegistry(
        Func<DateTimeOffset>? utcNow = null,
        TimeSpan? lifetime = null,
        string? ledgerPath = null,
        TimeSpan? evaluationMargin = null,
        TimeSpan? pendingLifetime = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _lifetime = lifetime ?? DefaultLifetime;
        _evaluationMargin = evaluationMargin ?? EvaluationHorizonMargin;
        _pendingLifetime = pendingLifetime ?? DraggedArrivalPendingLifetime;
        _ledgerPath = ledgerPath;
        // The ledger is loaded on first use instead of at construction:
        // startup used to pay a synchronous full-file deserialize that the
        // first real consumer (the watcher's first evaluation, or a manual
        // restore) would not need until much later.
    }

    public void BeginOperation(
        string operationId,
        IEnumerable<FileService.FileTransferPlan> plans)
    {
        DateTimeOffset now = _utcNow();
        DateTimeOffset expiresAt = now + _lifetime;
        lock (_gate)
        {
            EnsureLedgerLoadedLocked();
            RemoveExpiredEntriesLocked();
            foreach (FileService.FileTransferPlan plan in plans)
            {
                string? destination = NormalizePath(plan.DestinationPath);
                if (destination is null)
                {
                    continue;
                }

                // Restore operations are moves: the destination proves itself
                // as this operation's arrival because the source vanished or
                // because it was created during the operation window. An
                // unrelated file that happens to sit at the claimed path
                // before the operation lands is never suppressed.
                _entries[destination] = new SuppressionEntry(
                    operationId,
                    expiresAt,
                    Fingerprint: null,
                    IsPending: true,
                    SourcePath: NormalizePath(plan.SourcePath),
                    RegisteredAt: now,
                    RequiresArrivalEvidence: true);
            }

            EvictOverflowLocked();
            PersistLedgerLocked();
        }
    }

    public void CompleteOperation(
        string operationId,
        IEnumerable<string> destinationPaths)
    {
        var destinations = destinationPaths
            .Select(NormalizePath)
            .OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset expiresAt = _utcNow() + _lifetime;
        lock (_gate)
        {
            EnsureLedgerLoadedLocked();
            RemoveExpiredEntriesLocked();
            foreach ((string path, SuppressionEntry entry) in _entries.ToArray())
            {
                if (!string.Equals(entry.OperationId, operationId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!destinations.Contains(path) || !TryCaptureFingerprint(path, out FileFingerprint fingerprint))
                {
                    _entries.Remove(path);
                    continue;
                }

                _entries[path] = entry with
                {
                    ExpiresAt = expiresAt,
                    Fingerprint = fingerprint,
                    IsPending = false
                };
            }

            EvictOverflowLocked();
            PersistLedgerLocked();
        }
    }

    /// <summary>
    /// Registers the desktop destinations a finished drag-out may have
    /// produced. A destination that already exists is pinned by fingerprint;
    /// one that has not materialized yet keeps a pending claim that must
    /// prove it is this drag's arrival. Both stay alive at least as long as
    /// <paramref name="organizationDelay"/> plus margin so the watcher's
    /// deferred evaluation cannot outlive the claim on any delay preset.
    /// </summary>
    /// <param name="organizationDelay">
    /// The configured desktop auto-organization delay; pass
    /// <see cref="DesktopAutoOrganizationPolicy.GetDelay(AppSettings)"/>.
    /// </param>
    public void SuppressDraggedArrivals(
        IEnumerable<(string SourcePath, string DestinationPath)> arrivals,
        TimeSpan organizationDelay = default)
    {
        DateTimeOffset now = _utcNow();
        TimeSpan horizon = organizationDelay + _evaluationMargin;
        DateTimeOffset fingerprintedExpiresAt =
            now + Max(_lifetime, horizon);
        DateTimeOffset pendingExpiresAt =
            now + Max(_pendingLifetime, horizon);
        lock (_gate)
        {
            EnsureLedgerLoadedLocked();
            RemoveExpiredEntriesLocked();
            foreach ((string sourcePath, string destinationPath) in arrivals)
            {
                string? destination = NormalizePath(destinationPath);
                if (destination is null)
                {
                    continue;
                }

                // A pending restore already owns this exact path: its claim
                // resolves through its own CompleteOperation, so a drag-out
                // must not downgrade it to drag evidence rules.
                if (_entries.TryGetValue(destination, out SuppressionEntry? existing) &&
                    !string.Equals(
                        existing.OperationId,
                        DraggedArrivalOperationId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (TryCaptureFingerprint(destination, out FileFingerprint fingerprint))
                {
                    _entries[destination] = new SuppressionEntry(
                        DraggedArrivalOperationId,
                        fingerprintedExpiresAt,
                        fingerprint,
                        IsPending: false,
                        SourcePath: NormalizePath(sourcePath),
                        RegisteredAt: now,
                        RequiresArrivalEvidence: true);
                }
                else
                {
                    _entries[destination] = new SuppressionEntry(
                        DraggedArrivalOperationId,
                        pendingExpiresAt,
                        Fingerprint: null,
                        IsPending: true,
                        SourcePath: NormalizePath(sourcePath),
                        RegisteredAt: now,
                        RequiresArrivalEvidence: true);
                }
            }

            EvictOverflowLocked();
            PersistLedgerLocked();
        }
    }

    public bool TryConsume(string path)
    {
        string? normalized = NormalizePath(path);
        if (normalized is null)
        {
            return false;
        }

        lock (_gate)
        {
            EnsureLedgerLoadedLocked();
            RemoveExpiredEntriesLocked();
            if (!_entries.TryGetValue(normalized, out SuppressionEntry? entry))
            {
                return false;
            }

            if (!entry.IsPending &&
                (!TryCaptureFingerprint(normalized, out FileFingerprint fingerprint) ||
                 fingerprint != entry.Fingerprint))
            {
                // The registered fingerprint no longer matches (file was
                // replaced or edited after registration). Accept if:
                // 1. The source vanished (moved file later edited), OR
                // 2. For a drag-out Copy+Replace where an older occupant existed at
                //    registration and Explorer later overwrote it with the dragged
                //    source, the destination fingerprint now matches the source's
                //    fingerprint (Length + LastWriteTimeUtc).
                if (entry.SourcePath is { Length: > 0 } sourcePath &&
                    ((!File.Exists(sourcePath) && !Directory.Exists(sourcePath)) ||
                     (string.Equals(entry.OperationId, DraggedArrivalOperationId, StringComparison.Ordinal) &&
                      TryCaptureFingerprint(normalized, out FileFingerprint updatedDestFp) &&
                      TryCaptureFingerprint(sourcePath, out FileFingerprint sourceFp) &&
                      updatedDestFp.MatchesCopiedContent(sourceFp))))
                {
                    _entries.Remove(normalized);
                    PersistLedgerLocked();
                    return true;
                }

                _entries.Remove(normalized);
                PersistLedgerLocked();
                return false;
            }

            if (entry.IsPending &&
                entry.RequiresArrivalEvidence &&
                !PendingArrivalMatches(normalized, entry))
            {
                // Evidence failure does not burn the claim: the occupant at
                // the claimed path is organized normally, but a later
                // arrival can still prove itself within the pending window.
                return false;
            }

            _entries.Remove(normalized);
            PersistLedgerLocked();
            return true;
        }
    }

    // A pending claim suppresses only a file this operation could have
    // produced: a move relocates the source (so it vanishes while the
    // destination keeps its original timestamps), while a copy produces a
    // brand-new destination created after registration. Anything else at the
    // claimed path — including an older file that pre-dates the claim — is
    // not this operation's arrival and is organized normally.
    private static bool PendingArrivalMatches(
        string path,
        SuppressionEntry entry)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            if (entry.SourcePath is not null &&
                !File.Exists(entry.SourcePath) &&
                !Directory.Exists(entry.SourcePath))
            {
                return true;
            }

            if (File.GetCreationTimeUtc(path) >=
                entry.RegisteredAt.UtcDateTime - ArrivalCreationTolerance)
            {
                return true;
            }

            // NTFS file-system tunneling (<15s after delete/move) can preserve an
            // older CreationTimeUtc on a freshly copied arrival. Explorer's copy
            // still preserves the source's exact (Length, LastWriteTimeUtc), so
            // matching the source fingerprint proves this file is our arrival
            // without false-matching an unrelated pre-existing occupant.
            if (entry.SourcePath is { Length: > 0 } sourcePath &&
                TryCaptureFingerprint(path, out FileFingerprint destFingerprint) &&
                TryCaptureFingerprint(sourcePath, out FileFingerprint sourceFingerprint) &&
                destFingerprint.MatchesCopiedContent(sourceFingerprint))
            {
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private void RemoveExpiredEntriesLocked()
    {
        DateTimeOffset now = _utcNow();
        foreach (string path in _entries
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _entries.Remove(path);
        }
    }

    /// <summary>
    /// First-use ledger load, always called under <see cref="_gate"/>. The
    /// flag is set before loading so a re-entrant public call cannot retry;
    /// the load itself runs its awaits on the pool for the same deadlock
    /// reasons the constructor load used to.
    /// </summary>
    private void EnsureLedgerLoadedLocked()
    {
        if (_ledgerLoaded || _ledgerPath is null)
        {
            return;
        }

        _ledgerLoaded = true;
        LoadLedger();
        EvictOverflowLocked();
    }

    private void EvictOverflowLocked()
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (string path in _entries
                     .OrderBy(pair => pair.Value.ExpiresAt)
                     .ThenBy(pair => pair.Value.RegisteredAt)
                     .Take(_entries.Count - MaxEntries)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _entries.Remove(path);
        }
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right) =>
        left >= right ? left : right;

    private static string? NormalizePath(string path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path)
                ? null
                : Path.GetFullPath(path);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryCaptureFingerprint(
        string path,
        out FileFingerprint fingerprint)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
            {
                fingerprint = default;
                return false;
            }

            fingerprint = new FileFingerprint(
                file.Length,
                file.LastWriteTimeUtc,
                file.CreationTimeUtc);
            return true;
        }
        catch (IOException)
        {
            fingerprint = default;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            fingerprint = default;
            return false;
        }
    }

    private void LoadLedger()
    {
        try
        {
            ResilientJsonLoadResult<DesktopSuppressionLedger?> result =
                // Run the async load on the pool, not the calling thread:
                // the ctor can run on the UI thread, where the loader's file
                // awaits would post their continuations to the very dispatcher
                // the GetResult() wait blocks — a guaranteed deadlock the
                // startup watchdog then has to kill.
                Task.Run(() =>
                    ResilientJsonStore.LoadWithResultAsync<DesktopSuppressionLedger?>(
                        _ledgerPath!,
                        static json => JsonSerializer.Deserialize(
                            json,
                            DesktopRecoveryJsonContext.Default.SuppressionLedger),
                        static () => null,
                        "DesktopSuppressionLedger"))
                    .GetAwaiter().GetResult();
            if (result.Value?.Entries is not { Count: > 0 } stored)
            {
                return;
            }

            DateTimeOffset now = _utcNow();
            foreach (DesktopSuppressionLedgerEntry storedEntry in stored)
            {
                string? path = NormalizePath(storedEntry.Path);
                if (path is null || storedEntry.ExpiresAtUtc <= now)
                {
                    continue;
                }

                FileFingerprint? fingerprint =
                    storedEntry.FingerprintLength is { } length &&
                    storedEntry.FingerprintLastWriteTimeUtc is { } lastWrite &&
                    storedEntry.FingerprintCreationTimeUtc is { } created
                        ? new FileFingerprint(length, lastWrite, created)
                        : null;
                _entries[path] = new SuppressionEntry(
                    storedEntry.OperationId,
                    storedEntry.ExpiresAtUtc,
                    fingerprint,
                    storedEntry.IsPending,
                    storedEntry.SourcePath is { Length: > 0 } storedSource
                        ? NormalizePath(storedSource)
                        : null,
                    storedEntry.RegisteredAtUtc,
                    storedEntry.RequiresArrivalEvidence);
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[DesktopOrganization] Suppression ledger load failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns a task that completes when every queued ledger persist has
    /// finished. Directory-teardown paths (tests) must await this before
    /// deleting the ledger's directory so an in-flight temp-file write
    /// cannot race the deletion.
    /// </summary>
    public Task WaitForPendingPersistAsync()
    {
        lock (_gate)
        {
            return _persistTail;
        }
    }

    private void PersistLedgerLocked()
    {
        if (_ledgerPath is null)
        {
            return;
        }

        var ledger = new DesktopSuppressionLedger
        {
            Version = LedgerVersion,
            Entries = _entries
                .Select(pair => new DesktopSuppressionLedgerEntry
                {
                    Path = pair.Key,
                    OperationId = pair.Value.OperationId,
                    ExpiresAtUtc = pair.Value.ExpiresAt,
                    IsPending = pair.Value.IsPending,
                    SourcePath = pair.Value.SourcePath,
                    RegisteredAtUtc = pair.Value.RegisteredAt,
                    RequiresArrivalEvidence = pair.Value.RequiresArrivalEvidence,
                    FingerprintLength = pair.Value.Fingerprint?.Length,
                    FingerprintLastWriteTimeUtc =
                        pair.Value.Fingerprint?.LastWriteTimeUtc,
                    FingerprintCreationTimeUtc =
                        pair.Value.Fingerprint?.CreationTimeUtc,
                })
                .ToList()
        };
        string path = _ledgerPath;
        _persistTail = _persistTail.ContinueWith(
            _ => WriteLedgerAsync(path, ledger),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default).Unwrap();
    }

    private static async Task WriteLedgerAsync(
        string path,
        DesktopSuppressionLedger ledger)
    {
        try
        {
            await ResilientJsonStore.SaveAsync(
                path,
                async tempPath =>
                {
                    await using var stream = new FileStream(
                        tempPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None);
                    await JsonSerializer.SerializeAsync(
                        stream,
                        ledger,
                        DesktopRecoveryJsonContext.Default.SuppressionLedger);
                    stream.Flush(flushToDisk: true);
                }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.Log(
                $"[DesktopOrganization] Suppression ledger persist failed: {ex.Message}");
        }
    }

    private sealed record SuppressionEntry(
        string OperationId,
        DateTimeOffset ExpiresAt,
        FileFingerprint? Fingerprint,
        bool IsPending,
        string? SourcePath,
        DateTimeOffset RegisteredAt,
        bool RequiresArrivalEvidence);

    private readonly record struct FileFingerprint(
        long Length,
        DateTime LastWriteTimeUtc,
        DateTime CreationTimeUtc)
    {
        public bool MatchesCopiedContent(FileFingerprint source) =>
            Length == source.Length &&
            Math.Abs((LastWriteTimeUtc - source.LastWriteTimeUtc).Ticks) <= TimeSpan.TicksPerSecond * 2;
    }
}

internal sealed class DesktopSuppressionLedger
{
    public int Version { get; set; }
    public List<DesktopSuppressionLedgerEntry> Entries { get; set; } = [];
}

internal sealed class DesktopSuppressionLedgerEntry
{
    public string Path { get; set; } = string.Empty;
    public string OperationId { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public bool IsPending { get; set; }
    public string? SourcePath { get; set; }
    public DateTimeOffset RegisteredAtUtc { get; set; }
    public bool RequiresArrivalEvidence { get; set; }
    public long? FingerprintLength { get; set; }
    public DateTime? FingerprintLastWriteTimeUtc { get; set; }
    public DateTime? FingerprintCreationTimeUtc { get; set; }
}
