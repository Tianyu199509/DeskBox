using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Features.Clipboard;
using DeskBox.Platform;

namespace DeskBox.Services.Backup;

/// <summary>
/// Converts machine-bound DPAPI/HMAC clipboard state into a portable snapshot during
/// explicit manual backup exports (<c>includeClipboard: true</c>), and rebases
/// <c>clipboard/dropped/</c> file paths during cross-machine / cross-profile restores.
/// Uses <see cref="JsonNode"/> DOM transforms (zero <c>JsonSerializer</c> inventory impact)
/// and delegates file mutations to <see cref="ClipboardStorageFileOps"/>.
/// </summary>
internal static class PortableBackupProcessor
{
    private static readonly JsonWriterOptions CompactJsonOptions = new()
    {
        Indented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly string[] MachineBoundSecretRelativePaths =
    [
        "widget-permissions.json",
        "widget-permissions.json.bak",
        "widget-permissions.key",
        "widget-secrets.json",
        "widget-secrets.json.bak",
        "ai-credentials.json",
        "ai-credentials.json.bak",
        Path.Combine("clipboard", "hmac-key.bin")
    ];

    public static void StripMachineBoundSecretsIfUntrustedRestore(
        string stagedDataDirectory,
        string archivePath,
        string recoveryRootPath,
        string? manifestKind,
        params string?[] additionalTrustedLocalRoots)
    {
        if (!Directory.Exists(stagedDataDirectory))
        {
            return;
        }

        bool isLocalRecoveryKind =
            string.Equals(manifestKind, "automatic", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(manifestKind, "pre-restore", StringComparison.OrdinalIgnoreCase);
        if (isLocalRecoveryKind && !string.IsNullOrWhiteSpace(archivePath))
        {
            string fullArchive = Path.GetFullPath(archivePath);
            if (IsUnderTrustedRoot(fullArchive, recoveryRootPath))
            {
                return;
            }

            if (additionalTrustedLocalRoots is { Length: > 0 })
            {
                for (int i = 0; i < additionalTrustedLocalRoots.Length; i++)
                {
                    if (IsUnderTrustedRoot(fullArchive, additionalTrustedLocalRoots[i]))
                    {
                        return;
                    }
                }
            }
        }

        foreach (string rel in MachineBoundSecretRelativePaths)
        {
            ClipboardStorageFileOps.TryDeleteFile(Path.Combine(stagedDataDirectory, rel));
        }
    }

    private static bool IsUnderTrustedRoot(string fullArchivePath, string? candidateRoot)
    {
        if (string.IsNullOrWhiteSpace(candidateRoot))
        {
            return false;
        }

        try
        {
            string normalizedRoot = TrimSeparators(Path.GetFullPath(candidateRoot)) + Path.DirectorySeparatorChar;
            return fullArchivePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static async Task MaterializePortableClipboardSnapshotAsync(
        string snapshotDataDirectory,
        CancellationToken cancellationToken = default)
    {
        string clipboardRoot = Path.Combine(snapshotDataDirectory, "clipboard");
        if (!Directory.Exists(clipboardRoot))
        {
            return;
        }

        // Remove machine-bound HMAC key so the target PC re-keys deterministically on first load.
        ClipboardStorageFileOps.TryDeleteFile(Path.Combine(clipboardRoot, "hmac-key.bin"));

        string storePath = Path.Combine(clipboardRoot, "clipboard.json");
        if (!File.Exists(storePath))
        {
            return;
        }

        string rawJson = await File.ReadAllTextAsync(storePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        if (JsonNode.Parse(rawJson) is not JsonObject rootObj ||
            rootObj["items"] is not JsonArray itemsArr)
        {
            return;
        }

        string imagesDir = Path.Combine(clipboardRoot, "images");
        string payloadsDir = Path.Combine(clipboardRoot, "payloads");
        var keptImages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keptPayloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = itemsArr.Count - 1; i >= 0; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (itemsArr[i] is not JsonObject itemObj)
            {
                itemsArr.RemoveAt(i);
                continue;
            }

            if (!TryUnprotectInlineField(itemObj, "textContent", out string? plainInlineText) ||
                !TryUnprotectInlineField(itemObj, "htmlContent", out _) ||
                !TryUnprotectInlineField(itemObj, "rtfContent", out _) ||
                !TryUnprotectInlineField(itemObj, "ocrText", out _))
            {
                itemsArr.RemoveAt(i);
                continue;
            }

            if (!await TryConvertSpillPayloadToPlainAsync(itemObj, "textPayloadFileName", "txt", "txt", payloadsDir, keptPayloads, cancellationToken).ConfigureAwait(false) ||
                !await TryConvertSpillPayloadToPlainAsync(itemObj, "htmlPayloadFileName", "html", "html", payloadsDir, keptPayloads, cancellationToken).ConfigureAwait(false) ||
                !await TryConvertSpillPayloadToPlainAsync(itemObj, "rtfPayloadFileName", "rtf", "rtf", payloadsDir, keptPayloads, cancellationToken).ConfigureAwait(false))
            {
                itemsArr.RemoveAt(i);
                continue;
            }

            string kindStr = ReadKindString(itemObj);
            if (string.Equals(kindStr, "Image", StringComparison.OrdinalIgnoreCase) || kindStr == "1")
            {
                string? imgLeaf = ReadString(itemObj, "imageFileName");
                if (!string.IsNullOrWhiteSpace(imgLeaf))
                {
                    string safeLeaf = Path.GetFileName(imgLeaf);
                    string imgPath = Path.Combine(imagesDir, safeLeaf);
                    if (File.Exists(imgPath))
                    {
                        if (!ClipboardStorageFileOps.TryReadMediaBytes(imgPath, out byte[] plainPng))
                        {
                            itemsArr.RemoveAt(i);
                            continue;
                        }

                        string sha256 = Convert.ToHexStringLower(SHA256.HashData(plainPng));
                        string ext = Path.GetExtension(safeLeaf);
                        if (string.IsNullOrWhiteSpace(ext))
                        {
                            ext = ".png";
                        }

                        string portableLeaf = $"{sha256}{ext}";
                        string portablePath = Path.Combine(imagesDir, portableLeaf);
                        if (!string.Equals(safeLeaf, portableLeaf, StringComparison.OrdinalIgnoreCase) ||
                            ClipboardStorageFileOps.IsDpapiProtectedMediaFile(imgPath))
                        {
                            await ClipboardStorageFileOps.WriteBytesAtomicallyAsync(portablePath, plainPng, cancellationToken)
                                .ConfigureAwait(false);
                        }

                        itemObj["contentHash"] = $"img:{sha256}";
                        itemObj["imageFileName"] = portableLeaf;
                        itemObj["thumbnailFileName"] = null;
                        keptImages.Add(portableLeaf);
                    }
                }
            }
            else if (string.Equals(kindStr, "Files", StringComparison.OrdinalIgnoreCase) || kindStr == "2")
            {
                if (itemObj["filePaths"] is JsonArray fpArr && fpArr.Count > 0)
                {
                    List<string> paths = [];
                    foreach (JsonNode? fp in fpArr)
                    {
                        if (fp is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
                        {
                            paths.Add(s);
                        }
                    }

                    if (paths.Count > 0)
                    {
                        itemObj["contentHash"] = ClipboardContentHashing.ComputeFilesContentHash(paths, hmacKey: null);
                    }
                }
            }
            else
            {
                string? fullText = null;
                string? txtPayloadLeaf = ReadString(itemObj, "textPayloadFileName");
                if (!string.IsNullOrWhiteSpace(txtPayloadLeaf))
                {
                    string txtPayloadPath = Path.Combine(payloadsDir, Path.GetFileName(txtPayloadLeaf));
                    if (File.Exists(txtPayloadPath))
                    {
                        fullText = await File.ReadAllTextAsync(txtPayloadPath, Encoding.UTF8, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }

                fullText ??= plainInlineText;
                if (!string.IsNullOrWhiteSpace(fullText))
                {
                    string trimmed = fullText.Trim();
                    itemObj["summary"] = ClipboardClassifier.BuildSummary(trimmed);
                    itemObj["contentHash"] = ClipboardContentHashing.ComputeNormalizedTextContentHash(
                        trimmed.AsSpan(),
                        hmacKey: null);
                }
            }
        }

        if (Directory.Exists(imagesDir))
        {
            ClipboardStorageFileOps.DeleteOrphanedFiles(imagesDir, keptImages);
        }

        if (Directory.Exists(payloadsDir))
        {
            ClipboardStorageFileOps.DeleteOrphanedFiles(payloadsDir, keptPayloads);
        }

        string thumbsDir = Path.Combine(clipboardRoot, "thumbnails");
        if (Directory.Exists(thumbsDir))
        {
            ClipboardStorageFileOps.DeleteOrphanedFiles(thumbsDir, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        }

        using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms, CompactJsonOptions))
        {
            rootObj.WriteTo(writer);
        }

        ClipboardStorageFileOps.WriteTextAtomically(storePath, Encoding.UTF8.GetString(ms.ToArray()));
    }

    public static Task RebaseClipboardPathsAsync(
        string stagedDataDirectory,
        string? sourceDataPath,
        string targetDataDirectory,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<string> oldPrefixes = string.IsNullOrWhiteSpace(sourceDataPath)
            ? []
            : [TrimSeparators(sourceDataPath) + Path.DirectorySeparatorChar];
        StoreUserAssetMigrationFileOps.RebaseClipboardStoreFiles(
            stagedDataDirectory,
            targetDataDirectory,
            oldPrefixes,
            tryUseMigratedHmacKey: false);
        return Task.CompletedTask;
    }

    public static async Task EnsureClipboardProtectedAtRestAsync(string dataDirectory)
    {
        string clipboardDir = Path.Combine(dataDirectory, "clipboard");
        if (!File.Exists(Path.Combine(clipboardDir, "clipboard.json")))
        {
            return;
        }

        try
        {
            _ = await new ClipboardStore(clipboardDir).LoadAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            App.Log($"[DataBackup] Clipboard at-rest protection after restore skipped: {ex.Message}");
        }
    }

    private static bool TryUnprotectInlineField(JsonObject itemObj, string propertyName, out string? plainText)
    {
        plainText = ReadString(itemObj, propertyName);
        if (string.IsNullOrEmpty(plainText) ||
            !plainText.StartsWith(ClipboardStore.DpapiPayloadPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        string cipher = plainText[ClipboardStore.DpapiPayloadPrefix.Length..];
        string unprotected = DpapiSecretProtector.UnprotectBase64ToUtf8(cipher);
        if (string.IsNullOrEmpty(unprotected))
        {
            plainText = null;
            return false;
        }

        plainText = unprotected;
        itemObj[propertyName] = unprotected;
        return true;
    }

    private static async Task<bool> TryConvertSpillPayloadToPlainAsync(
        JsonObject itemObj,
        string propertyName,
        string prefix,
        string extension,
        string payloadsDir,
        HashSet<string> keptPayloads,
        CancellationToken cancellationToken)
    {
        string? fileName = ReadString(itemObj, propertyName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return true;
        }

        string safeLeaf = Path.GetFileName(fileName);
        string fullPath = Path.Combine(payloadsDir, safeLeaf);
        if (!File.Exists(fullPath))
        {
            return true;
        }

        string raw = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        string plain = raw;
        if (raw.StartsWith(ClipboardStore.DpapiPayloadPrefix, StringComparison.Ordinal))
        {
            plain = DpapiSecretProtector.UnprotectBase64ToUtf8(raw[ClipboardStore.DpapiPayloadPrefix.Length..]);
            if (string.IsNullOrEmpty(plain))
            {
                return false;
            }
        }

        byte[] utf8 = Encoding.UTF8.GetBytes(plain);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(utf8));
        string portableLeaf = $"{prefix}_{sha256}.{extension}";
        string portablePath = Path.Combine(payloadsDir, portableLeaf);
        await ClipboardStorageFileOps.WriteBytesIfMissingAsync(portablePath, utf8, cancellationToken)
            .ConfigureAwait(false);
        itemObj[propertyName] = portableLeaf;
        keptPayloads.Add(portableLeaf);
        return true;
    }

    private static string ReadKindString(JsonObject itemObj)
    {
        if (itemObj["kind"] is JsonValue val)
        {
            if (val.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }

            if (val.TryGetValue(out int n))
            {
                return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return "Text";
    }

    private static string? ReadString(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static string TrimSeparators(string path) =>
        path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
