using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Features.Clipboard;
using DeskBox.Platform;

namespace DeskBox.Services;

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = false)]
[JsonSerializable(
    typeof(ClipboardStoreDocument),
    TypeInfoPropertyName = "ClipboardStoreDocument")]
[JsonSerializable(
    typeof(ClipboardHistoryItem),
    TypeInfoPropertyName = "ClipboardHistoryItem")]
[JsonSerializable(
    typeof(ClipboardObservedApp),
    TypeInfoPropertyName = "ClipboardObservedApp")]
[JsonSerializable(
    typeof(ClipboardStorageStats),
    TypeInfoPropertyName = "ClipboardStorageStats")]
internal sealed partial class ClipboardJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Crash-safe JSON persistence for Clipboard history metadata under
/// <c>%DataDirectory%/clipboard/clipboard.json</c>.
/// </summary>
public sealed class ClipboardStore
{
    internal const string DpapiPayloadPrefix = "dpapi:";
    internal const int CurrentVersion = 1;
    private const int MaxObservedAppsInStats = 24;
    private readonly string _rootDirectory;
    private readonly string _storePath;
    private readonly string _exportDirectory;
    private readonly object _encryptedSensitiveItemCacheLock = new();
    private readonly Dictionary<string, (long UpdatedAtUtcTicks, bool IsPinned, int UseCount, string? Note, string? UrlTitle, string? OcrText, ClipboardHistoryItem Encrypted)> _encryptedSensitiveItemCache =
        new(StringComparer.Ordinal);

    public ClipboardStore()
        : this(
            GetDefaultRootDirectory(),
            DeskBoxDataPathService.Current.IsDevelopmentRoot
                ? null
                : Path.Combine(DropStagingFileOps.GetStagingRootDirectory(), "ClipboardExports"))
    {
    }

    internal ClipboardStore(string rootDirectory, string? exportDirectory = null)
    {
        _rootDirectory = rootDirectory;
        Directory.CreateDirectory(_rootDirectory);
        _storePath = Path.Combine(_rootDirectory, "clipboard.json");
        _exportDirectory = string.IsNullOrWhiteSpace(exportDirectory)
            ? Path.Combine(_rootDirectory, "exports")
            : Path.GetFullPath(exportDirectory);
    }

    internal static string GetDefaultRootDirectory() =>
        Path.Combine(DeskBoxDataPathService.Current.DataDirectory, "clipboard");

    internal string RootDirectory => _rootDirectory;

    internal string StorePath => _storePath;

    internal string ImageDirectory => Path.Combine(_rootDirectory, "images");

    internal string ThumbnailDirectory => Path.Combine(_rootDirectory, "thumbnails");

    internal string PayloadDirectory => Path.Combine(_rootDirectory, "payloads");

    internal string DroppedDirectory => Path.Combine(_rootDirectory, "dropped");

    internal string ExportDirectory => _exportDirectory;

    private bool _hasUnparseableFutureSchema;

    public async Task<ClipboardStoreDocument> LoadAsync()
    {
        bool unparseableFuture = false;
        ClipboardStoreDocument doc = await ResilientJsonStore.LoadAsync(
            _storePath,
            json => ResilientJsonStore.DeserializeWithFutureSchemaFallback(
                _storePath,
                json,
                CurrentVersion,
                raw => JsonSerializer.Deserialize(raw, ClipboardJsonContext.Default.ClipboardStoreDocument),
                static d => d.Version,
                Normalize,
                static ver => new ClipboardStoreDocument { Version = ver },
                out unparseableFuture),
            () => new ClipboardStoreDocument(),
            nameof(ClipboardStore)).ConfigureAwait(false);
        _hasUnparseableFutureSchema = unparseableFuture;
        if (_hasUnparseableFutureSchema)
        {
            return doc;
        }

        bool migratedInlinePayloads = false;
        bool migratedSensitiveAtRest = false;
        bool removedUnreadableProtected = false;
        for (int i = doc.Items.Count - 1; i >= 0; i--)
        {
            ClipboardHistoryItem item = doc.Items[i];
            var hydration = await HydrateAndMigrateSensitiveItemAsync(item).ConfigureAwait(false);
            if (!hydration.IsAvailable)
            {
                // Cross-machine or corrupted DPAPI payload: omit gracefully instead of exposing ciphertext.
                doc.Items.RemoveAt(i);
                migratedInlinePayloads = true;
                removedUnreadableProtected = true;
                continue;
            }

            if (hydration.NeedsResave)
            {
                migratedInlinePayloads = true;
                migratedSensitiveAtRest = true;
            }

            if (await ExternalizeItemPayloadsAsync(item).ConfigureAwait(false))
            {
                migratedInlinePayloads = true;
            }

            if (await MigrateLegacyContentHashAsync(item).ConfigureAwait(false))
            {
                migratedInlinePayloads = true;
            }
        }

        if (removedUnreadableProtected || migratedSensitiveAtRest)
        {
            PurgeUnreferencedLocalArtifacts(doc);
        }

        if (migratedInlinePayloads)
        {
            try
            {
                if (migratedSensitiveAtRest)
                {
                    // Unlink cleartext primary and backup before saving so ResilientJsonStore
                    // never rotates portable plaintext secrets into clipboard.json.bak.
                    ClipboardStorageFileOps.TryDeleteFile(ResilientJsonStore.GetBackupPath(_storePath));
                    ClipboardStorageFileOps.TryDeleteFile(_storePath);
                }

                await SaveAsync(doc).ConfigureAwait(false);
            }
            catch
            {
                // Keep migrated in-memory items usable even if opportunistic compaction fails.
            }
        }

        return doc;
    }

    private void PurgeUnreferencedLocalArtifacts(ClipboardStoreDocument doc)
    {
        if (_hasUnparseableFutureSchema || doc.Version > CurrentVersion)
        {
            return;
        }

        var referencedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedThumbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedPayloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ClipboardHistoryItem item in doc.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.ImageFileName)) referencedImages.Add(item.ImageFileName);
            if (!string.IsNullOrWhiteSpace(item.ThumbnailFileName)) referencedThumbs.Add(item.ThumbnailFileName);
            if (!string.IsNullOrWhiteSpace(item.TextPayloadFileName)) referencedPayloads.Add(item.TextPayloadFileName);
            if (!string.IsNullOrWhiteSpace(item.HtmlPayloadFileName)) referencedPayloads.Add(item.HtmlPayloadFileName);
            if (!string.IsNullOrWhiteSpace(item.RtfPayloadFileName)) referencedPayloads.Add(item.RtfPayloadFileName);
        }

        ClipboardStorageFileOps.DeleteOrphanedFiles(ImageDirectory, referencedImages);
        ClipboardStorageFileOps.DeleteOrphanedFiles(ThumbnailDirectory, referencedThumbs);
        ClipboardStorageFileOps.DeleteOrphanedFiles(PayloadDirectory, referencedPayloads);
    }

    public async Task SaveAsync(ClipboardStoreDocument document)
    {
        if (_hasUnparseableFutureSchema)
        {
            App.Log($"[ClipboardStore] Skipping write to preserve unparseable newer schema document '{_storePath}'.");
            return;
        }

        document = Normalize(document);
        document.Version = CurrentVersion;
        foreach (ClipboardHistoryItem item in document.Items)
        {
            _ = await ExternalizeItemPayloadsAsync(item).ConfigureAwait(false);
            if (item.IsSensitive && item.Kind == ClipboardItemKind.Image)
            {
                _ = await ProtectSensitiveItemMediaAsync(item).ConfigureAwait(false);
            }
        }

        ClipboardStoreDocument persistedDocument = BuildPersistedDocument(document);

        await ResilientJsonStore.SaveAsync(
            _storePath,
            async tempPath =>
            {
                await using var stream = new FileStream(
                    tempPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None);
                await JsonSerializer.SerializeAsync(
                    stream,
                    persistedDocument,
                    ClipboardJsonContext.Default.ClipboardStoreDocument).ConfigureAwait(false);
            }).ConfigureAwait(false);
    }

    /// <summary>
    /// Spills <see cref="ClipboardHistoryItem.HtmlContent"/>, <see cref="ClipboardHistoryItem.RtfContent"/>,
    /// and large <see cref="ClipboardHistoryItem.TextContent"/> (&gt; 2,048 chars) to content-addressed
    /// UTF-8 files under <c>clipboard/payloads/</c> so <c>clipboard.json</c> and resident models stay compact.
    /// Sensitive payloads (<see cref="ClipboardHistoryItem.IsSensitive"/>) are DPAPI-protected before hitting disk.
    /// </summary>
    internal async Task<bool> ExternalizeItemPayloadsAsync(
        ClipboardHistoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        bool spilled = false;
        bool encrypt = item.IsSensitive;

        if (!string.IsNullOrEmpty(item.HtmlContent))
        {
            try
            {
                string fileName = await WriteTextPayloadIfMissingAsync("html", "html", item.HtmlContent, encrypt, cancellationToken)
                    .ConfigureAwait(false);
                item.HtmlPayloadFileName = fileName;
                item.HtmlContent = null;
                spilled = true;
            }
            catch (CryptographicException)
            {
                // Keep in-memory payload intact rather than writing an unprotected or empty spill file.
            }
        }

        if (!string.IsNullOrEmpty(item.RtfContent))
        {
            try
            {
                string fileName = await WriteTextPayloadIfMissingAsync("rtf", "rtf", item.RtfContent, encrypt, cancellationToken)
                    .ConfigureAwait(false);
                item.RtfPayloadFileName = fileName;
                item.RtfContent = null;
                spilled = true;
            }
            catch (CryptographicException)
            {
                // Keep in-memory payload intact rather than writing an unprotected or empty spill file.
            }
        }

        if (item.TextContent is { } text)
        {
            if (text.Length > ClipboardHistoryItem.InlineTextMaxChars)
            {
                item.CharCount = Math.Max(item.CharCount, text.Length);
                item.LineCount = Math.Max(item.LineCount, ClipboardHistoryItem.CountLines(text));
                try
                {
                    string fileName = await WriteTextPayloadIfMissingAsync("txt", "txt", text, encrypt, cancellationToken)
                        .ConfigureAwait(false);
                    item.TextPayloadFileName = fileName;
                    item.TextContent = TruncateSurrogateSafe(text, ClipboardHistoryItem.InlineTextMaxChars);
                    spilled = true;
                }
                catch (CryptographicException)
                {
                    // Keep in-memory payload intact rather than writing an unprotected or empty spill file.
                }
            }
            else if (string.IsNullOrWhiteSpace(item.TextPayloadFileName))
            {
                item.CharCount = text.Length;
                item.LineCount = ClipboardHistoryItem.CountLines(text);
            }
        }

        return spilled;
    }

    internal async Task<string?> ReadPayloadTextAsync(
        string? payloadFileName,
        CancellationToken cancellationToken = default)
    {
        string? fullPath = ResolveSafePayloadPath(payloadFileName);
        if (fullPath is null || !File.Exists(fullPath))
        {
            return null;
        }

        try
        {
            string raw = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            if (raw.StartsWith(DpapiPayloadPrefix, StringComparison.Ordinal))
            {
                return TryUnprotectTextFromAtRest(raw, out string? plain, out _) ? plain : null;
            }

            return raw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal async Task<(string? Text, bool WasTruncated)> ReadPayloadTextPreviewAsync(
        string? payloadFileName,
        int maxChars,
        CancellationToken cancellationToken = default)
    {
        string? fullPath = ResolveSafePayloadPath(payloadFileName);
        if (fullPath is null || maxChars <= 0 || !File.Exists(fullPath))
        {
            return (null, false);
        }

        try
        {
            await using var fs = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 8192,
                useAsync: true);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            int targetRead = Math.Max(maxChars + 1, DpapiPayloadPrefix.Length);
            var sb = new StringBuilder(Math.Min(targetRead, 16384));
            char[] buffer = new char[4096];
            while (sb.Length < targetRead)
            {
                int toRead = Math.Min(buffer.Length, targetRead - sb.Length);
                int read = await reader.ReadAsync(buffer.AsMemory(0, toRead), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                sb.Append(buffer, 0, read);
                if (sb.Length >= DpapiPayloadPrefix.Length &&
                    StartsWithOrdinal(sb, DpapiPayloadPrefix))
                {
                    // DPAPI ciphertext must be read completely before unprotecting and truncating.
                    string remaining = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                    sb.Append(remaining);
                    if (!TryUnprotectTextFromAtRest(sb.ToString(), out string? decrypted, out _) ||
                        decrypted is null)
                    {
                        return (null, false);
                    }

                    if (decrypted.Length <= maxChars)
                    {
                        return (decrypted, false);
                    }

                    return (TruncateSurrogateSafe(decrypted, maxChars), true);
                }
            }

            bool wasTruncated = sb.Length > maxChars;
            if (wasTruncated)
            {
                sb.Length = maxChars;
                if (sb.Length > 0 && char.IsHighSurrogate(sb[^1]))
                {
                    sb.Length--;
                }
            }

            return (sb.ToString(), wasTruncated);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (null, false);
        }
    }

    /// <summary>
    /// Computes Clipboard storage statistics and observed apps directly from disk without
    /// instantiating <see cref="ClipboardHistoryService"/> or loading <see cref="ClipboardStoreDocument"/> into RAM.
    /// </summary>
    public static Task<ClipboardStorageStats> CalculateDiskStorageStatsAsync(string? rootPath = null)
    {
        string resolvedRoot = string.IsNullOrWhiteSpace(rootPath)
            ? GetDefaultRootDirectory()
            : rootPath;
        return Task.Run(() => CalculateDiskStorageStatsCore(resolvedRoot));
    }

    private static ClipboardStorageStats CalculateDiskStorageStatsCore(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return new ClipboardStorageStats(0, 0, 0, 0, []);
        }

        string storePath = Path.Combine(rootPath, "clipboard.json");
        string backupPath = storePath + ".bak";
        int activeCount = 0;
        int pinnedCount = 0;
        List<ClipboardObservedApp> observedApps = [];

        if (!TryReadStoreHeaderStats(storePath, out activeCount, out pinnedCount, out observedApps) &&
            File.Exists(backupPath))
        {
            _ = TryReadStoreHeaderStats(backupPath, out activeCount, out pinnedCount, out observedApps);
        }

        (int imgCount, long imgBytes) = ClipboardStorageFileOps.MeasureDirectoryFiles(Path.Combine(rootPath, "images"));
        (_, long thumbBytes) = ClipboardStorageFileOps.MeasureDirectoryFiles(Path.Combine(rootPath, "thumbnails"));
        (_, long payloadBytes) = ClipboardStorageFileOps.MeasureDirectoryFiles(Path.Combine(rootPath, "payloads"));
        (_, long droppedBytes) = ClipboardStorageFileOps.MeasureDirectoryFilesRecursive(Path.Combine(rootPath, "dropped"));

        return new ClipboardStorageStats(
            activeCount,
            pinnedCount,
            imgCount,
            imgBytes + thumbBytes + payloadBytes + droppedBytes,
            observedApps);
    }

    public static async Task<int> ClearDiskUnpinnedAsync(string? rootDirectory = null)
    {
        string resolvedRoot = string.IsNullOrWhiteSpace(rootDirectory)
            ? GetDefaultRootDirectory()
            : rootDirectory;
        var store = new ClipboardStore(resolvedRoot);
        if (!Directory.Exists(store.RootDirectory))
        {
            return 0;
        }

        ClipboardStoreDocument doc = await store.LoadAsync().ConfigureAwait(false);
        int removed = doc.Items.RemoveAll(static x => !x.IsPinned);
        var referencedImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedThumbs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var referencedPayloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ClipboardHistoryItem item in doc.Items)
        {
            if (!string.IsNullOrWhiteSpace(item.ImageFileName))
            {
                referencedImages.Add(item.ImageFileName);
            }

            if (!string.IsNullOrWhiteSpace(item.ThumbnailFileName))
            {
                referencedThumbs.Add(item.ThumbnailFileName);
            }

            if (!string.IsNullOrWhiteSpace(item.TextPayloadFileName))
            {
                referencedPayloads.Add(item.TextPayloadFileName);
            }

            if (!string.IsNullOrWhiteSpace(item.HtmlPayloadFileName))
            {
                referencedPayloads.Add(item.HtmlPayloadFileName);
            }

            if (!string.IsNullOrWhiteSpace(item.RtfPayloadFileName))
            {
                referencedPayloads.Add(item.RtfPayloadFileName);
            }
        }

        ClipboardStorageFileOps.DeleteOrphanedFiles(store.ImageDirectory, referencedImages);
        ClipboardStorageFileOps.DeleteOrphanedFiles(store.ThumbnailDirectory, referencedThumbs);
        ClipboardStorageFileOps.DeleteOrphanedFiles(store.PayloadDirectory, referencedPayloads);
        CleanupOrphanedDroppedStorage(store, doc.Items);

        if (removed > 0)
        {
            await store.SaveAsync(doc).ConfigureAwait(false);
        }

        return removed;
    }

    public static async Task<int> ClearDiskAllAsync(string? rootDirectory = null)
    {
        string resolvedRoot = string.IsNullOrWhiteSpace(rootDirectory)
            ? GetDefaultRootDirectory()
            : rootDirectory;
        var store = new ClipboardStore(resolvedRoot);
        if (!Directory.Exists(store.RootDirectory))
        {
            return 0;
        }

        ClipboardStoreDocument doc = await store.LoadAsync().ConfigureAwait(false);
        int removed = doc.Items.Count;
        bool hadObserved = doc.ObservedApps.Count > 0;
        doc.Items.Clear();
        doc.ObservedApps.Clear();

        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        ClipboardStorageFileOps.DeleteOrphanedFiles(store.ImageDirectory, empty);
        ClipboardStorageFileOps.DeleteOrphanedFiles(store.ThumbnailDirectory, empty);
        ClipboardStorageFileOps.DeleteOrphanedFiles(store.PayloadDirectory, empty);
        CleanupOrphanedDroppedStorage(store, []);

        if (removed > 0 || hadObserved || File.Exists(store.StorePath))
        {
            await store.SaveAsync(doc).ConfigureAwait(false);
        }

        return removed;
    }

    internal static void CleanupOrphanedDroppedStorage(
        ClipboardStore store,
        IEnumerable<ClipboardHistoryItem> items)
    {
        var referencedDropped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ClipboardHistoryItem item in items)
        {
            if (item.FilePaths is null || item.FilePaths.Count == 0)
            {
                continue;
            }

            foreach (string path in item.FilePaths)
            {
                if (!string.IsNullOrWhiteSpace(path))
                {
                    referencedDropped.Add(path);
                }
            }
        }

        ClipboardStorageFileOps.DeleteOrphanedDroppedEntries(store.DroppedDirectory, referencedDropped);
    }

    private static bool TryReadStoreHeaderStats(
        string jsonPath,
        out int activeCount,
        out int pinnedCount,
        out List<ClipboardObservedApp> observedApps)
    {
        activeCount = 0;
        pinnedCount = 0;
        observedApps = [];

        if (!File.Exists(jsonPath))
        {
            return false;
        }

        try
        {
            byte[] utf8Bytes = File.ReadAllBytes(jsonPath);
            var reader = new Utf8JsonReader(utf8Bytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    continue;
                }

                if (reader.ValueTextEquals("items"u8))
                {
                    if (!reader.Read() || reader.TokenType != JsonTokenType.StartArray)
                    {
                        reader.TrySkip();
                        continue;
                    }

                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType != JsonTokenType.StartObject)
                        {
                            reader.TrySkip();
                            continue;
                        }

                        bool hasId = false;
                        bool isPinned = false;
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                        {
                            if (reader.TokenType != JsonTokenType.PropertyName)
                            {
                                continue;
                            }

                            if (reader.ValueTextEquals("id"u8))
                            {
                                if (reader.Read() && reader.TokenType == JsonTokenType.String)
                                {
                                    string? id = reader.GetString();
                                    hasId = !string.IsNullOrWhiteSpace(id);
                                }
                            }
                            else if (reader.ValueTextEquals("isPinned"u8))
                            {
                                if (reader.Read())
                                {
                                    isPinned = reader.TokenType == JsonTokenType.True;
                                }
                            }
                            else
                            {
                                reader.Skip();
                            }
                        }

                        if (hasId)
                        {
                            activeCount++;
                            if (isPinned)
                            {
                                pinnedCount++;
                            }
                        }
                    }
                }
                else if (reader.ValueTextEquals("observedApps"u8))
                {
                    if (!reader.Read())
                    {
                        break;
                    }

                    if (reader.TokenType == JsonTokenType.StartArray)
                    {
                        var parsed = new List<ClipboardObservedApp>();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            if (reader.TokenType != JsonTokenType.StartObject)
                            {
                                reader.TrySkip();
                                continue;
                            }

                            string appPath = string.Empty;
                            string appName = string.Empty;
                            string? iconFileName = null;
                            long lastSeenUtcTicks = 0;

                            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
                            {
                                if (reader.TokenType != JsonTokenType.PropertyName)
                                {
                                    continue;
                                }

                                if (reader.ValueTextEquals("appPath"u8))
                                {
                                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                                    {
                                        appPath = reader.GetString() ?? string.Empty;
                                    }
                                }
                                else if (reader.ValueTextEquals("appName"u8))
                                {
                                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                                    {
                                        appName = reader.GetString() ?? string.Empty;
                                    }
                                }
                                else if (reader.ValueTextEquals("iconFileName"u8))
                                {
                                    if (reader.Read() && reader.TokenType == JsonTokenType.String)
                                    {
                                        iconFileName = reader.GetString();
                                    }
                                }
                                else if (reader.ValueTextEquals("lastSeenUtcTicks"u8))
                                {
                                    if (reader.Read() && reader.TokenType == JsonTokenType.Number)
                                    {
                                        reader.TryGetInt64(out lastSeenUtcTicks);
                                    }
                                }
                                else
                                {
                                    reader.Skip();
                                }
                            }

                            if (!string.IsNullOrWhiteSpace(appPath))
                            {
                                parsed.Add(new ClipboardObservedApp
                                {
                                    AppPath = appPath,
                                    AppName = appName,
                                    IconFileName = iconFileName,
                                    LastSeenUtcTicks = lastSeenUtcTicks
                                });
                            }
                        }

                        observedApps = parsed
                            .OrderByDescending(static a => a.LastSeenUtcTicks)
                            .Take(MaxObservedAppsInStats)
                            .ToList();
                    }
                    else
                    {
                        reader.TrySkip();
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            return true;
        }
        catch
        {
            activeCount = 0;
            pinnedCount = 0;
            observedApps = [];
            return false;
        }
    }

    private string? ResolveSafePayloadPath(string? payloadFileName)
    {
        if (string.IsNullOrWhiteSpace(payloadFileName))
        {
            return null;
        }

        string safeLeaf = Path.GetFileName(payloadFileName);
        if (string.IsNullOrWhiteSpace(safeLeaf))
        {
            return null;
        }

        return Path.Combine(PayloadDirectory, safeLeaf);
    }

    private async Task<string> WriteTextPayloadIfMissingAsync(
        string prefix,
        string extension,
        string content,
        bool encryptWithDpapi,
        CancellationToken cancellationToken)
    {
        string storedText;
        if (encryptWithDpapi && !string.IsNullOrEmpty(content))
        {
            storedText = ProtectTextForAtRest(content) ?? string.Empty;
            if (!storedText.StartsWith(DpapiPayloadPrefix, StringComparison.Ordinal))
            {
                throw new CryptographicException("Failed to DPAPI-protect sensitive clipboard spill payload.");
            }
        }
        else
        {
            storedText = content;
        }
        byte[] utf8Bytes = Encoding.UTF8.GetBytes(storedText);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(utf8Bytes));
        string fileName = $"{prefix}_{sha256}.{extension}";
        string fullPath = Path.Combine(PayloadDirectory, fileName);
        await ClipboardStorageFileOps.WriteBytesIfMissingAsync(fullPath, utf8Bytes, cancellationToken)
            .ConfigureAwait(false);
        return fileName;
    }

    private byte[]? _contentHashKey;
    private readonly object _contentHashKeyGate = new();

    /// <summary>
    /// Machine+user-local key for deterministic HMAC content addressing: dedup and
    /// self-write suppression keep working, but persisted hashes no longer confirm
    /// candidate plaintexts offline. DPAPI-bound; a cross-machine restore regenerates
    /// the key (one-time dedup miss against pre-restore captures).
    /// </summary>
    internal byte[] GetOrCreateContentHashKey()
    {
        lock (_contentHashKeyGate)
        {
            if (_contentHashKey is not null)
            {
                return _contentHashKey;
            }

            string keyPath = Path.Combine(_rootDirectory, "hmac-key.bin");
            try
            {
                if (File.Exists(keyPath))
                {
                    byte[] restored = Convert.FromBase64String(
                        DpapiSecretProtector.UnprotectBase64ToUtf8(File.ReadAllText(keyPath)));
                    if (restored.Length == 32)
                    {
                        _contentHashKey = restored;
                        return restored;
                    }
                }
            }
            catch
            {
                // Corrupt or foreign-machine key: fall through and regenerate.
            }

            byte[] fresh = RandomNumberGenerator.GetBytes(32);
            ClipboardStorageFileOps.WriteTextAtomically(
                keyPath,
                DpapiSecretProtector.ProtectUtf8ToBase64(Convert.ToBase64String(fresh)));
            _contentHashKey = fresh;
            return fresh;
        }
    }

    internal async Task<bool> ProtectSensitiveItemMediaAsync(
        ClipboardHistoryItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind != ClipboardItemKind.Image || !item.IsSensitive)
        {
            return true;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(item.ImageFileName))
            {
                string imgPath = Path.Combine(ImageDirectory, Path.GetFileName(item.ImageFileName));
                if (!await ClipboardStorageFileOps.ProtectMediaFileInPlaceAsync(imgPath, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }

            if (!string.IsNullOrWhiteSpace(item.ThumbnailFileName))
            {
                string thumbPath = Path.Combine(ThumbnailDirectory, Path.GetFileName(item.ThumbnailFileName));
                if (!await ClipboardStorageFileOps.ProtectMediaFileInPlaceAsync(thumbPath, cancellationToken).ConfigureAwait(false))
                {
                    return false;
                }
            }

            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private async Task<bool> MigrateLegacyContentHashAsync(ClipboardHistoryItem item)
    {
        string? hash = item.ContentHash;
        if (string.IsNullOrEmpty(hash) || hash.Contains(":h1:", StringComparison.Ordinal))
        {
            return false;
        }

        byte[] key = GetOrCreateContentHashKey();
        if (hash.StartsWith("txt:", StringComparison.Ordinal))
        {
            string? text = !string.IsNullOrWhiteSpace(item.TextPayloadFileName)
                ? await ReadPayloadTextAsync(item.TextPayloadFileName).ConfigureAwait(false)
                : null;
            if (string.IsNullOrEmpty(text))
            {
                text = item.TextContent;
            }

            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            ReadOnlySpan<char> trimmed = text.AsSpan().Trim();
            if (trimmed.IsEmpty)
            {
                return false;
            }

            item.ContentHash = ClipboardContentHashing.ComputeNormalizedTextContentHash(trimmed, key);
            return true;
        }

        if (hash.StartsWith("img:", StringComparison.Ordinal))
        {
            string ext = !string.IsNullOrWhiteSpace(item.ImageFileName)
                ? Path.GetExtension(item.ImageFileName)
                : ".png";
            if (string.IsNullOrWhiteSpace(ext))
            {
                ext = ".png";
            }

            string oldLeaf = !string.IsNullOrWhiteSpace(item.ImageFileName) &&
                             File.Exists(Path.Combine(ImageDirectory, Path.GetFileName(item.ImageFileName)))
                ? Path.GetFileName(item.ImageFileName)
                : (hash["img:".Length..] + ext);
            string imagePath = Path.Combine(ImageDirectory, oldLeaf);
            if (!File.Exists(imagePath) ||
                !ClipboardStorageFileOps.TryReadMediaBytes(imagePath, out byte[] pngBytes))
            {
                return false;
            }

            string newHash = ClipboardContentHashing.ComputeImageContentHash(pngBytes, key);
            string newHashHex = newHash[(newHash.LastIndexOf(':') + 1)..];
            string newLeaf = newHashHex + ext;
            if (!string.Equals(oldLeaf, newLeaf, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryMoveMediaLeaf(ImageDirectory, oldLeaf, newLeaf))
                {
                    return false;
                }

                string oldThumbLeaf = !string.IsNullOrWhiteSpace(item.ThumbnailFileName)
                    ? Path.GetFileName(item.ThumbnailFileName)
                    : oldLeaf;
                string newThumbLeaf = newHashHex + Path.GetExtension(oldThumbLeaf);
                bool hasMovedThumb = File.Exists(Path.Combine(ThumbnailDirectory, oldThumbLeaf)) &&
                    TryMoveMediaLeaf(ThumbnailDirectory, oldThumbLeaf, newThumbLeaf);
                item.ImageFileName = newLeaf;
                item.ThumbnailFileName = hasMovedThumb || File.Exists(Path.Combine(ThumbnailDirectory, newThumbLeaf))
                    ? newThumbLeaf
                    : null;
            }

            item.ContentHash = newHash;
            return true;
        }

        if (hash.StartsWith("files:", StringComparison.Ordinal) && item.FilePaths.Count > 0)
        {
            item.ContentHash = ClipboardContentHashing.ComputeFilesContentHash(item.FilePaths, key);
            return true;
        }

        return false;
    }

    private static bool TryMoveMediaLeaf(string directory, string oldLeaf, string newLeaf)
    {
        string source = Path.Combine(directory, oldLeaf);
        string target = Path.Combine(directory, newLeaf);
        try
        {
            if (!File.Exists(source))
            {
                return File.Exists(target);
            }

            ClipboardStorageFileOps.CopyRawFileOverwrite(source, target);
            ClipboardStorageFileOps.TryDeleteFile(source);
            return true;
        }
        catch
        {
            return File.Exists(target);
        }
    }

    private async Task<(bool IsAvailable, bool NeedsResave)> HydrateAndMigrateSensitiveItemAsync(ClipboardHistoryItem item)
    {
        bool hasInlineDpapi =
            (item.TextContent?.StartsWith(DpapiPayloadPrefix, StringComparison.Ordinal) ?? false) ||
            (item.HtmlContent?.StartsWith(DpapiPayloadPrefix, StringComparison.Ordinal) ?? false) ||
            (item.RtfContent?.StartsWith(DpapiPayloadPrefix, StringComparison.Ordinal) ?? false) ||
            (item.OcrText?.StartsWith(DpapiPayloadPrefix, StringComparison.Ordinal) ?? false);

        string? imagePath = item.IsSensitive && item.Kind == ClipboardItemKind.Image && !string.IsNullOrWhiteSpace(item.ImageFileName)
            ? Path.Combine(ImageDirectory, Path.GetFileName(item.ImageFileName))
            : null;
        string? thumbPath = item.IsSensitive && item.Kind == ClipboardItemKind.Image && !string.IsNullOrWhiteSpace(item.ThumbnailFileName)
            ? Path.Combine(ThumbnailDirectory, Path.GetFileName(item.ThumbnailFileName))
            : null;
        bool hasProtectedMedia = item.IsSensitive &&
            (ClipboardStorageFileOps.IsDpapiProtectedMediaFile(imagePath) ||
             ClipboardStorageFileOps.IsDpapiProtectedMediaFile(thumbPath));

        if (!item.IsSensitive && !hasInlineDpapi && !hasProtectedMedia)
        {
            return (true, false);
        }

        if (!TryUnprotectTextFromAtRest(item.TextContent, out string? plainText, out bool plainLegacyText) ||
            !TryUnprotectTextFromAtRest(item.HtmlContent, out string? plainHtml, out bool plainLegacyHtml) ||
            !TryUnprotectTextFromAtRest(item.RtfContent, out string? plainRtf, out bool plainLegacyRtf) ||
            !TryUnprotectTextFromAtRest(item.OcrText, out string? plainOcr, out bool plainLegacyOcr))
        {
            return (false, false);
        }

        if (hasProtectedMedia)
        {
            if ((imagePath is not null && File.Exists(imagePath) && !ClipboardStorageFileOps.TryReadMediaBytes(imagePath, out _)) ||
                (thumbPath is not null && File.Exists(thumbPath) && !ClipboardStorageFileOps.TryReadMediaBytes(thumbPath, out _)))
            {
                return (false, false);
            }
        }

        bool needsResave = item.IsSensitive &&
            ((plainLegacyText && !string.IsNullOrEmpty(plainText)) ||
             (plainLegacyHtml && !string.IsNullOrEmpty(plainHtml)) ||
             (plainLegacyRtf && !string.IsNullOrEmpty(plainRtf)) ||
             (plainLegacyOcr && !string.IsNullOrEmpty(plainOcr)));

        if (item.IsSensitive)
        {
            var textSpill = await VerifyOrMigrateSensitiveSpillAsync("txt", "txt", item.TextPayloadFileName).ConfigureAwait(false);
            if (!textSpill.IsAvailable) return (false, false);
            if (textSpill.Migrated)
            {
                item.TextPayloadFileName = textSpill.FileName;
                needsResave = true;
            }

            var htmlSpill = await VerifyOrMigrateSensitiveSpillAsync("html", "html", item.HtmlPayloadFileName).ConfigureAwait(false);
            if (!htmlSpill.IsAvailable) return (false, false);
            if (htmlSpill.Migrated)
            {
                item.HtmlPayloadFileName = htmlSpill.FileName;
                needsResave = true;
            }

            var rtfSpill = await VerifyOrMigrateSensitiveSpillAsync("rtf", "rtf", item.RtfPayloadFileName).ConfigureAwait(false);
            if (!rtfSpill.IsAvailable) return (false, false);
            if (rtfSpill.Migrated)
            {
                item.RtfPayloadFileName = rtfSpill.FileName;
                needsResave = true;
            }

            if (item.Kind == ClipboardItemKind.Image &&
                ((imagePath is not null && File.Exists(imagePath) && !ClipboardStorageFileOps.IsDpapiProtectedMediaFile(imagePath)) ||
                 (thumbPath is not null && File.Exists(thumbPath) && !ClipboardStorageFileOps.IsDpapiProtectedMediaFile(thumbPath))))
            {
                if (!await ProtectSensitiveItemMediaAsync(item).ConfigureAwait(false))
                {
                    return (false, false);
                }

                needsResave = true;
            }
        }

        item.TextContent = plainText;
        item.HtmlContent = plainHtml;
        item.RtfContent = plainRtf;
        item.OcrText = plainOcr;
        if (!string.IsNullOrWhiteSpace(plainText))
        {
            string expectedSummary = ClipboardClassifier.BuildSummary(plainText.Trim());
            if (item.IsSensitive &&
                string.Equals(item.Summary, expectedSummary, StringComparison.Ordinal))
            {
                // Persisted summary was still unmasked cleartext on disk.
                needsResave = true;
            }

            item.Summary = expectedSummary;
        }

        return (true, needsResave);
    }

    private async Task<(bool IsAvailable, bool Migrated, string? FileName)> VerifyOrMigrateSensitiveSpillAsync(
        string prefix,
        string extension,
        string? payloadFileName)
    {
        if (string.IsNullOrWhiteSpace(payloadFileName))
        {
            return (true, false, payloadFileName);
        }

        string? fullPath = ResolveSafePayloadPath(payloadFileName);
        if (fullPath is null || !File.Exists(fullPath))
        {
            return (true, false, payloadFileName);
        }

        string raw;
        try
        {
            raw = await File.ReadAllTextAsync(fullPath, Encoding.UTF8).ConfigureAwait(false);
        }
        catch
        {
            return (false, false, payloadFileName);
        }

        if (!TryUnprotectTextFromAtRest(raw, out string? plain, out bool wasPlaintextLegacy) || plain is null)
        {
            return (false, false, payloadFileName);
        }

        if (!wasPlaintextLegacy)
        {
            return (true, false, payloadFileName);
        }

        string newFileName = await WriteTextPayloadIfMissingAsync(prefix, extension, plain, encryptWithDpapi: true, CancellationToken.None)
            .ConfigureAwait(false);
        if (!string.Equals(newFileName, payloadFileName, StringComparison.OrdinalIgnoreCase))
        {
            ClipboardStorageFileOps.TryDeleteFile(fullPath);
        }

        return (true, true, newFileName);
    }

    private ClipboardStoreDocument BuildPersistedDocument(ClipboardStoreDocument source)
    {
        bool hasSensitive = false;
        foreach (ClipboardHistoryItem item in source.Items)
        {
            if (item.IsSensitive)
            {
                hasSensitive = true;
                break;
            }
        }

        if (!hasSensitive)
        {
            return source;
        }

        var items = new List<ClipboardHistoryItem>(source.Items.Count);
        HashSet<string>? activeSensitiveIds = null;
        foreach (ClipboardHistoryItem item in source.Items)
        {
            if (!item.IsSensitive)
            {
                items.Add(item);
                continue;
            }

            activeSensitiveIds ??= new HashSet<string>(StringComparer.Ordinal);
            activeSensitiveIds.Add(item.Id);

            lock (_encryptedSensitiveItemCacheLock)
            {
                if (_encryptedSensitiveItemCache.TryGetValue(item.Id, out var cached) &&
                    cached.UpdatedAtUtcTicks == item.UpdatedAtUtcTicks &&
                    cached.IsPinned == item.IsPinned &&
                    cached.UseCount == item.UseCount &&
                    string.Equals(cached.Note, item.Note, StringComparison.Ordinal) &&
                    string.Equals(cached.UrlTitle, item.UrlTitle, StringComparison.Ordinal) &&
                    string.Equals(cached.OcrText, item.OcrText, StringComparison.Ordinal))
                {
                    items.Add(cached.Encrypted);
                    continue;
                }
            }

            string maskedSummary = item.Kind == ClipboardItemKind.Image
                ? (item.Summary ?? string.Empty)
                : ClipboardClassifier.MaskSecretForDisplay(item.TextContent ?? item.Summary ?? string.Empty);
            var encryptedItem = new ClipboardHistoryItem
            {
                Id = item.Id,
                Kind = item.Kind,
                SubKind = item.SubKind,
                ContentHash = item.ContentHash,
                Summary = maskedSummary,
                TextContent = ProtectTextForAtRest(item.TextContent),
                HtmlContent = ProtectTextForAtRest(item.HtmlContent),
                RtfContent = ProtectTextForAtRest(item.RtfContent),
                TextPayloadFileName = item.TextPayloadFileName,
                HtmlPayloadFileName = item.HtmlPayloadFileName,
                RtfPayloadFileName = item.RtfPayloadFileName,
                CharCount = item.CharCount,
                LineCount = item.LineCount,
                ImageFileName = item.ImageFileName,
                ThumbnailFileName = item.ThumbnailFileName,
                ImageWidth = item.ImageWidth,
                ImageHeight = item.ImageHeight,
                ByteSize = item.ByteSize,
                FilePaths = item.FilePaths.Count > 0 ? [.. item.FilePaths] : [],
                ColorHex = item.ColorHex,
                Note = item.Note,
                UrlTitle = item.UrlTitle,
                CodeLanguage = item.CodeLanguage,
                OcrText = ProtectTextForAtRest(item.OcrText),
                IsSensitive = item.IsSensitive,
                IsPinned = item.IsPinned,
                UseCount = item.UseCount,
                SourceAppName = item.SourceAppName,
                SourceAppPath = item.SourceAppPath,
                SourceAppIconFileName = item.SourceAppIconFileName,
                CreatedAtUtcTicks = item.CreatedAtUtcTicks,
                UpdatedAtUtcTicks = item.UpdatedAtUtcTicks
            };
            lock (_encryptedSensitiveItemCacheLock)
            {
                _encryptedSensitiveItemCache[item.Id] = (
                    item.UpdatedAtUtcTicks,
                    item.IsPinned,
                    item.UseCount,
                    item.Note,
                    item.UrlTitle,
                    item.OcrText,
                    encryptedItem);
            }

            items.Add(encryptedItem);
        }

        lock (_encryptedSensitiveItemCacheLock)
        {
            if (activeSensitiveIds is null)
            {
                _encryptedSensitiveItemCache.Clear();
            }
            else if (_encryptedSensitiveItemCache.Count > activeSensitiveIds.Count)
            {
                foreach (string key in _encryptedSensitiveItemCache.Keys.ToArray())
                {
                    if (!activeSensitiveIds.Contains(key))
                    {
                        _encryptedSensitiveItemCache.Remove(key);
                    }
                }
            }
        }

        return new ClipboardStoreDocument
        {
            Version = source.Version,
            Items = items,
            ObservedApps = source.ObservedApps
        };
    }

    private static string? ProtectTextForAtRest(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return plain;
        }

        if (plain.StartsWith(DpapiPayloadPrefix, StringComparison.Ordinal))
        {
            return plain;
        }

        string protectedBase64 = DpapiSecretProtector.ProtectUtf8ToBase64(plain);
        return !string.IsNullOrEmpty(protectedBase64)
            ? DpapiPayloadPrefix + protectedBase64
            : string.Empty;
    }

    private static bool TryUnprotectTextFromAtRest(
        string? stored,
        out string? plain,
        out bool wasPlaintextLegacy)
    {
        wasPlaintextLegacy = false;
        if (string.IsNullOrEmpty(stored))
        {
            plain = stored;
            return true;
        }

        if (stored.StartsWith(DpapiPayloadPrefix, StringComparison.Ordinal))
        {
            string ciphertext = stored[DpapiPayloadPrefix.Length..];
            if (string.IsNullOrWhiteSpace(ciphertext))
            {
                plain = null;
                return false;
            }

            string unprotected = DpapiSecretProtector.UnprotectBase64ToUtf8(ciphertext);
            if (string.IsNullOrEmpty(unprotected))
            {
                plain = null;
                return false;
            }

            plain = unprotected;
            return true;
        }

        wasPlaintextLegacy = true;
        plain = stored;
        return true;
    }

    private static bool StartsWithOrdinal(StringBuilder sb, string prefix)
    {
        if (sb.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (sb[i] != prefix[i])
            {
                return false;
            }
        }

        return true;
    }

    internal static string TruncateSurrogateSafe(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return text;
        }

        int len = maxChars;
        if (len > 0 && char.IsHighSurrogate(text[len - 1]))
        {
            len--;
        }

        return text[..len];
    }

    private static ClipboardStoreDocument Normalize(ClipboardStoreDocument? doc)
    {
        doc ??= new ClipboardStoreDocument();
        doc.Version = Math.Max(doc.Version, CurrentVersion);
        doc.Items ??= [];
        doc.ObservedApps ??= [];
        doc.Items.RemoveAll(item => item is null || string.IsNullOrWhiteSpace(item.Id));
        doc.ObservedApps.RemoveAll(app => app is null || string.IsNullOrWhiteSpace(app.AppPath));
        return doc;
    }
}
