using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Platform;

namespace DeskBox.Services.DesktopOrganization;

internal sealed class DesktopIconPositionRecord
{
    public string CurrentPath { get; set; } = string.Empty;
    public string OriginalDesktopPath { get; set; } = string.Empty;
    public int X { get; set; }
    public int Y { get; set; }
    public DateTimeOffset CapturedUtc { get; set; }
}

internal sealed class DesktopIconPositionLedgerData
{
    public List<DesktopIconPositionRecord> Entries { get; set; } = [];
}

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(
    typeof(DesktopIconPositionLedgerData),
    TypeInfoPropertyName = "DesktopIconPositionLedgerData")]
internal sealed partial class DesktopIconPositionJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Machine-local durable ledger storing pre-organization Windows desktop icon
/// coordinates keyed by the item's current managed path. Bounded by age
/// (30 days) and entry count (512) and persisted via <see cref="ResilientJsonStore"/>.
/// </summary>
internal sealed class DesktopIconPositionLedgerStore
{
    internal const int MaxEntries = 512;
    internal static readonly TimeSpan EntryTimeToLive = TimeSpan.FromDays(30);

    private readonly string _ledgerPath;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, DesktopIconPositionRecord>? _entries;

    internal DesktopIconPositionLedgerStore(
        string? ledgerPath = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _ledgerPath = ledgerPath ?? Path.Combine(
            DeskBoxDataPathService.Current.DataDirectory,
            "desktop-icon-positions.json");
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    internal async Task RecordMovesFromDesktopAsync(
        IReadOnlyList<(string CurrentPath, string OriginalDesktopPath, DesktopIconPoint Point)> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Dictionary<string, DesktopIconPositionRecord> entries = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            DateTimeOffset now = _utcNow();
            bool changed = PruneExpiredLocked(entries, now);

            foreach ((string rawCurrent, string rawOriginal, DesktopIconPoint point) in records)
            {
                string? currentPath = NormalizePath(rawCurrent);
                string? originalPath = NormalizePath(rawOriginal);
                if (currentPath is null || originalPath is null)
                {
                    continue;
                }

                entries[currentPath] = new DesktopIconPositionRecord
                {
                    CurrentPath = currentPath,
                    OriginalDesktopPath = originalPath,
                    X = point.X,
                    Y = point.Y,
                    CapturedUtc = now,
                };
                changed = true;
            }

            if (TrimToMaxEntriesLocked(entries))
            {
                changed = true;
            }

            if (changed)
            {
                await SaveLockedAsync(entries).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task RelocatePathsAsync(
        IReadOnlyList<(string OldPath, string NewPath)> moves)
    {
        if (moves.Count == 0)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Dictionary<string, DesktopIconPositionRecord> entries = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            if (entries.Count == 0)
            {
                return;
            }

            bool changed = PruneExpiredLocked(entries, _utcNow());
            foreach ((string rawOld, string rawNew) in moves)
            {
                string? oldPath = NormalizePath(rawOld);
                string? newPath = NormalizePath(rawNew);
                if (oldPath is null ||
                    newPath is null ||
                    string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (entries.Remove(oldPath, out DesktopIconPositionRecord? existing))
                {
                    existing.CurrentPath = newPath;
                    entries[newPath] = existing;
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveLockedAsync(entries).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task RelocateDirectoryPrefixAsync(string oldDirectoryPath, string newDirectoryPath)
    {
        string? oldRoot = NormalizePath(oldDirectoryPath);
        string? newRoot = NormalizePath(newDirectoryPath);
        if (oldRoot is null ||
            newRoot is null ||
            string.Equals(oldRoot, newRoot, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string oldPrefix = oldRoot + Path.DirectorySeparatorChar;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Dictionary<string, DesktopIconPositionRecord> entries = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            if (entries.Count == 0)
            {
                return;
            }

            bool changed = PruneExpiredLocked(entries, _utcNow());
            var updates = new List<(string OldKey, DesktopIconPositionRecord Record)>();
            foreach ((string key, DesktopIconPositionRecord record) in entries)
            {
                if (key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string suffix = key[oldPrefix.Length..];
                    string? updatedPath = NormalizePath(Path.Combine(newRoot, suffix));
                    if (updatedPath is not null)
                    {
                        record.CurrentPath = updatedPath;
                        updates.Add((key, record));
                    }
                }
            }

            if (updates.Count > 0)
            {
                foreach ((string oldKey, _) in updates)
                {
                    entries.Remove(oldKey);
                }

                foreach ((_, DesktopIconPositionRecord record) in updates)
                {
                    entries[record.CurrentPath] = record;
                }

                changed = true;
            }

            if (changed)
            {
                await SaveLockedAsync(entries).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<IReadOnlyDictionary<string, DesktopIconPositionRecord>> LookupByCurrentPathsAsync(
        IEnumerable<string> currentPaths)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Dictionary<string, DesktopIconPositionRecord> entries = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            var result = new Dictionary<string, DesktopIconPositionRecord>(StringComparer.OrdinalIgnoreCase);
            if (entries.Count == 0)
            {
                return result;
            }

            if (PruneExpiredLocked(entries, _utcNow()))
            {
                await SaveLockedAsync(entries).ConfigureAwait(false);
            }

            foreach (string rawPath in currentPaths)
            {
                string? normalized = NormalizePath(rawPath);
                if (normalized is not null && entries.TryGetValue(normalized, out DesktopIconPositionRecord? record))
                {
                    result[normalized] = new DesktopIconPositionRecord
                    {
                        CurrentPath = record.CurrentPath,
                        OriginalDesktopPath = record.OriginalDesktopPath,
                        X = record.X,
                        Y = record.Y,
                        CapturedUtc = record.CapturedUtc,
                    };
                }
            }

            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task RemoveByCurrentPathsAsync(IEnumerable<string> currentPaths)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Dictionary<string, DesktopIconPositionRecord> entries = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            if (entries.Count == 0)
            {
                return;
            }

            bool changed = PruneExpiredLocked(entries, _utcNow());
            foreach (string rawPath in currentPaths)
            {
                string? normalized = NormalizePath(rawPath);
                if (normalized is not null && entries.Remove(normalized))
                {
                    changed = true;
                }
            }

            if (changed)
            {
                await SaveLockedAsync(entries).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<int> CountForTestingAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            Dictionary<string, DesktopIconPositionRecord> entries = await EnsureLoadedLockedAsync().ConfigureAwait(false);
            return entries.Count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<string, DesktopIconPositionRecord>> EnsureLoadedLockedAsync()
    {
        if (_entries is not null)
        {
            return _entries;
        }

        ResilientJsonLoadResult<DesktopIconPositionLedgerData> loaded =
            await ResilientJsonStore.LoadWithResultAsync(
                _ledgerPath,
                static json => JsonSerializer.Deserialize(
                    json,
                    DesktopIconPositionJsonContext.Default.DesktopIconPositionLedgerData)
                    ?? new DesktopIconPositionLedgerData(),
                static () => new DesktopIconPositionLedgerData(),
                "DesktopIconPositions").ConfigureAwait(false);

        var map = new Dictionary<string, DesktopIconPositionRecord>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset now = _utcNow();
        foreach (DesktopIconPositionRecord record in loaded.Value.Entries ?? [])
        {
            string? currentPath = NormalizePath(record.CurrentPath);
            string? originalPath = NormalizePath(record.OriginalDesktopPath);
            if (currentPath is null || originalPath is null)
            {
                continue;
            }

            if (record.CapturedUtc != default && now - record.CapturedUtc > EntryTimeToLive)
            {
                continue;
            }

            map[currentPath] = new DesktopIconPositionRecord
            {
                CurrentPath = currentPath,
                OriginalDesktopPath = originalPath,
                X = record.X,
                Y = record.Y,
                CapturedUtc = record.CapturedUtc == default ? now : record.CapturedUtc,
            };
        }

        TrimToMaxEntriesLocked(map);
        _entries = map;
        return map;
    }

    private static bool PruneExpiredLocked(
        Dictionary<string, DesktopIconPositionRecord> entries,
        DateTimeOffset now)
    {
        List<string>? expiredKeys = null;
        foreach ((string key, DesktopIconPositionRecord record) in entries)
        {
            if (record.CapturedUtc != default && now - record.CapturedUtc > EntryTimeToLive)
            {
                expiredKeys ??= [];
                expiredKeys.Add(key);
            }
        }

        if (expiredKeys is null)
        {
            return false;
        }

        foreach (string key in expiredKeys)
        {
            entries.Remove(key);
        }

        return true;
    }

    private static bool TrimToMaxEntriesLocked(Dictionary<string, DesktopIconPositionRecord> entries)
    {
        if (entries.Count <= MaxEntries)
        {
            return false;
        }

        int removeCount = entries.Count - MaxEntries;
        foreach (string key in entries
            .OrderBy(pair => pair.Value.CapturedUtc)
            .Take(removeCount)
            .Select(pair => pair.Key)
            .ToArray())
        {
            entries.Remove(key);
        }

        return true;
    }

    private async Task SaveLockedAsync(Dictionary<string, DesktopIconPositionRecord> entries)
    {
        var payload = new DesktopIconPositionLedgerData
        {
            Entries = entries.Values
                .OrderByDescending(record => record.CapturedUtc)
                .ToList(),
        };
        try
        {
            await ResilientJsonStore.SaveAsync(
                _ledgerPath,
                async tempPath =>
                {
                    await using var stream = new FileStream(
                        tempPath,
                        FileMode.Create,
                        FileAccess.Write,
                        FileShare.None,
                        bufferSize: 4096,
                        useAsync: true);
                    await JsonSerializer.SerializeAsync(
                        stream,
                        payload,
                        DesktopIconPositionJsonContext.Default.DesktopIconPositionLedgerData).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.LogVerbose($"[DesktopIconPosition] Ledger save failed: {ex.Message}");
        }
    }

    internal static string? NormalizePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rawPath.Trim()));
        }
        catch
        {
            return null;
        }
    }
}
