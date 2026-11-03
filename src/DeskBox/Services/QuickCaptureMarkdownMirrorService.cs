using System.Text;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Exports Quick Capture items as clean UTF-8 Markdown files (with YAML frontmatter
/// and <c>assets/</c> attachment copies) and maintains an optional debounced one-way
/// local Markdown mirror folder compatible with Obsidian / Logseq / Typora.
/// </summary>
public sealed class QuickCaptureMarkdownMirrorService : IDisposable
{
    private const string MirrorFileTagMarker = "__qc_";
    private static readonly UTF8Encoding s_utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly TimeSpan s_syncDebounce = TimeSpan.FromMilliseconds(600);

    private readonly QuickCaptureService _quickCaptureService;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _debounceLock = new();
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    public QuickCaptureMarkdownMirrorService(QuickCaptureService quickCaptureService)
    {
        ArgumentNullException.ThrowIfNull(quickCaptureService);
        _quickCaptureService = quickCaptureService;
        _quickCaptureService.Changed += OnQuickCaptureChanged;
    }

    public void ScheduleSync()
    {
        if (_disposed)
        {
            return;
        }

        CancellationToken token;
        lock (_debounceLock)
        {
            if (_disposed)
            {
                return;
            }

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = new CancellationTokenSource();
            token = _debounceCts.Token;
        }

        _ = RunDebouncedSyncAsync(token);
    }

    public async Task<int> SyncNowAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return 0;
        }

        await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            QuickCaptureStoreData snapshot = await _quickCaptureService.GetDataAsync().ConfigureAwait(false);
            string? mirrorRoot = snapshot.MirrorFolderPath;
            if (string.IsNullOrWhiteSpace(mirrorRoot))
            {
                return 0;
            }

            string normalizedRoot = Path.GetFullPath(mirrorRoot.Trim());
            Directory.CreateDirectory(normalizedRoot);

            var activeItems = snapshot.Items
                .Where(item => item is not null && !item.IsDeleted && !item.IsRecent)
                .ToList();

            var expectedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int writtenCount = 0;

            foreach (QuickCaptureItem item in activeItems)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string shortId = GetShortId(item.Id);
                string fileName = BuildMirrorFileName(item, shortId);
                expectedFileNames.Add(fileName);

                List<string> copiedRelativeAssets = await CopyItemAssetsAsync(
                    item,
                    normalizedRoot,
                    shortId,
                    cancellationToken).ConfigureAwait(false);
                string markdown = FormatItemMarkdown(item, includeFrontmatter: true, copiedRelativeAssets);
                string targetPath = Path.Combine(normalizedRoot, fileName);

                if (await WriteTextIfChangedAsync(targetPath, markdown, cancellationToken).ConfigureAwait(false))
                {
                    writtenCount++;
                }
            }

            // Clean up only stale DeskBox-owned '*__qc_<shortId>.md' files (renamed or deleted notes).
            // Never touch user-authored Markdown files in the same folder.
            foreach (string existingFile in Directory.EnumerateFiles(normalizedRoot, $"*{MirrorFileTagMarker}*.md", SearchOption.TopDirectoryOnly))
            {
                string existingName = Path.GetFileName(existingFile);
                if (!IsOwnedMirrorFileName(existingName) || expectedFileNames.Contains(existingName))
                {
                    continue;
                }

                try
                {
                    QuickCaptureStorageFileOps.DeleteOwnedMirrorFile(normalizedRoot, existingFile);
                }
                catch (Exception ex)
                {
                    App.Log($"[QuickCaptureMirror] Failed to remove stale mirror file '{existingFile}': {ex.Message}");
                }
            }

            return writtenCount;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[QuickCaptureMirror] Mirror sync failed: {ex.Message}");
            return 0;
        }
        finally
        {
            _syncGate.Release();
        }
    }

    public static async Task<IReadOnlyList<string>> ExportItemsToDirectoryAsync(
        IReadOnlyList<QuickCaptureItem> items,
        string targetDirectory,
        string fallbackFilePrefix = "Note",
        CancellationToken cancellationToken = default)
    {
        if (items is null || items.Count == 0 || string.IsNullOrWhiteSpace(targetDirectory))
        {
            return [];
        }

        string normalizedDir = Path.GetFullPath(targetDirectory.Trim());
        Directory.CreateDirectory(normalizedDir);

        var exportedPaths = new List<string>(items.Count);
        foreach (QuickCaptureItem item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (item is null || item.IsDeleted)
            {
                continue;
            }

            string shortId = GetShortId(item.Id);
            List<string> copiedRelativeAssets = await CopyItemAssetsAsync(
                item,
                normalizedDir,
                shortId,
                cancellationToken).ConfigureAwait(false);
            string markdown = FormatItemMarkdown(item, includeFrontmatter: true, copiedRelativeAssets);

            string baseName = ResolveReadableBaseName(item, fallbackFilePrefix);
            string destinationPath = FileService.GetAvailablePath(Path.Combine(normalizedDir, $"{baseName}.md"));
            await File.WriteAllTextAsync(destinationPath, markdown, s_utf8NoBom, cancellationToken).ConfigureAwait(false);
            exportedPaths.Add(destinationPath);
        }

        return exportedPaths;
    }

    public static string FormatItemMarkdown(
        QuickCaptureItem item,
        bool includeFrontmatter,
        IReadOnlyList<string>? relativeAssetPaths = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        var sb = new StringBuilder();
        if (includeFrontmatter)
        {
            DateTimeOffset created = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt;
            DateTimeOffset updated = item.UpdatedAt == default ? created : item.UpdatedAt;

            sb.AppendLine("---");
            sb.Append("id: ").AppendLine(EscapeYamlScalar(item.Id));
            if (!string.IsNullOrWhiteSpace(item.Title))
            {
                sb.Append("title: ").AppendLine(EscapeYamlScalar(item.Title.Trim()));
            }

            sb.Append("created: ").AppendLine(created.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz"));
            sb.Append("updated: ").AppendLine(updated.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:sszzz"));
            if (item.IsPinned)
            {
                sb.AppendLine("pinned: true");
            }

            if (item.ArchivedAt.HasValue)
            {
                sb.AppendLine("archived: true");
            }

            if (item.Tags is { Count: > 0 })
            {
                sb.AppendLine("tags:");
                foreach (string tag in item.Tags.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    sb.Append("  - ").AppendLine(EscapeYamlScalar(tag.Trim()));
                }
            }

            sb.AppendLine("---");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(item.Title))
        {
            sb.Append("# ").AppendLine(item.Title.Trim());
            sb.AppendLine();
        }

        string body = item.Body ?? string.Empty;
        if (item.Type == QuickCaptureItemType.Image && string.Equals(body.Trim(), "Image", StringComparison.OrdinalIgnoreCase))
        {
            body = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            sb.AppendLine(body.TrimEnd());
        }

        if (relativeAssetPaths is { Count: > 0 })
        {
            if (sb.Length > 0 && !sb.ToString().EndsWith(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal))
            {
                sb.AppendLine();
            }

            foreach (string relPath in relativeAssetPaths)
            {
                string fileName = Path.GetFileName(relPath);
                bool isImage = IsImageExtension(Path.GetExtension(fileName));
                if (isImage)
                {
                    sb.Append("![").Append(EscapeMarkdownBracket(fileName)).Append("](").Append(relPath).AppendLine(")");
                }
                else
                {
                    sb.Append("- [").Append(EscapeMarkdownBracket(fileName)).Append("](").Append(relPath).AppendLine(")");
                }
            }
        }

        return sb.ToString();
    }

    private void OnQuickCaptureChanged()
    {
        ScheduleSync();
    }

    private async Task RunDebouncedSyncAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(s_syncDebounce, token).ConfigureAwait(false);
            await SyncNowAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer change or disposed.
        }
    }

    private static async Task<List<string>> CopyItemAssetsAsync(
        QuickCaptureItem item,
        string rootDirectory,
        string shortId,
        CancellationToken cancellationToken)
    {
        var relativePaths = new List<string>();
        if (item.Attachments is not { Count: > 0 } && string.IsNullOrWhiteSpace(item.ImagePath))
        {
            return relativePaths;
        }

        var candidates = new List<(string SourcePath, string DisplayName)>();
        var seenSource = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (item.Attachments is { Count: > 0 })
        {
            foreach (TodoAttachment attachment in item.Attachments)
            {
                if (!string.IsNullOrWhiteSpace(attachment.FilePath) &&
                    File.Exists(attachment.FilePath) &&
                    seenSource.Add(attachment.FilePath))
                {
                    string display = string.IsNullOrWhiteSpace(attachment.DisplayName)
                        ? Path.GetFileName(attachment.FilePath)
                        : attachment.DisplayName;
                    candidates.Add((attachment.FilePath, display));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(item.ImagePath) &&
            File.Exists(item.ImagePath) &&
            seenSource.Add(item.ImagePath))
        {
            candidates.Add((item.ImagePath, Path.GetFileName(item.ImagePath)));
        }

        if (candidates.Count == 0)
        {
            return relativePaths;
        }

        string assetsDir = Path.Combine(rootDirectory, "assets");
        Directory.CreateDirectory(assetsDir);

        foreach ((string sourcePath, string displayName) in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                string sanitizedName = FileService.SanitizeFileSystemName(displayName);
                if (string.IsNullOrWhiteSpace(sanitizedName))
                {
                    sanitizedName = $"asset{Path.GetExtension(sourcePath)}";
                }

                if (string.IsNullOrWhiteSpace(Path.GetExtension(sanitizedName)) &&
                    !string.IsNullOrWhiteSpace(Path.GetExtension(sourcePath)))
                {
                    sanitizedName += Path.GetExtension(sourcePath);
                }

                string targetAssetName = $"{shortId}_{sanitizedName}";
                string targetAssetPath = Path.Combine(assetsDir, targetAssetName);

                var srcInfo = new FileInfo(sourcePath);
                var dstInfo = new FileInfo(targetAssetPath);
                if (!dstInfo.Exists || dstInfo.Length != srcInfo.Length)
                {
                    await Task.Run(() => QuickCaptureStorageFileOps.CopyManagedFile(sourcePath, targetAssetPath, overwrite: true), cancellationToken)
                        .ConfigureAwait(false);
                }

                relativePaths.Add($"assets/{Uri.EscapeDataString(targetAssetName)}");
            }
            catch (Exception ex)
            {
                App.Log($"[QuickCaptureMirror] Asset copy skipped for '{sourcePath}': {ex.Message}");
            }
        }

        return relativePaths;
    }

    private static async Task<bool> WriteTextIfChangedAsync(
        string filePath,
        string content,
        CancellationToken cancellationToken)
    {
        if (File.Exists(filePath))
        {
            try
            {
                string existing = await File.ReadAllTextAsync(filePath, s_utf8NoBom, cancellationToken).ConfigureAwait(false);
                if (string.Equals(existing, content, StringComparison.Ordinal))
                {
                    return false;
                }
            }
            catch
            {
                // Rewrite below.
            }
        }

        await File.WriteAllTextAsync(filePath, content, s_utf8NoBom, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string BuildMirrorFileName(QuickCaptureItem item, string shortId)
    {
        DateTimeOffset created = item.CreatedAt == default ? DateTimeOffset.UtcNow : item.CreatedAt;
        string fallback = created.ToLocalTime().ToString("yyyy-MM-dd_HHmm");
        string baseName = ResolveReadableBaseName(item, fallback);
        return $"{baseName}{MirrorFileTagMarker}{shortId}.md";
    }

    private static string ResolveReadableBaseName(QuickCaptureItem item, string fallbackPrefix)
    {
        string? rawCandidate = !string.IsNullOrWhiteSpace(item.Title)
            ? item.Title
            : item.Body?
                .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(rawCandidate))
        {
            rawCandidate = rawCandidate.TrimStart('#', '-', '*', '>', ' ', '\t');
        }

        string sanitized = FileService.SanitizeFileSystemName(rawCandidate);
        if (sanitized.Length > 36)
        {
            sanitized = sanitized[..36].Trim().TrimEnd('.');
        }

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = FileService.SanitizeFileSystemName(fallbackPrefix);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                sanitized = "Note";
            }
        }

        return sanitized;
    }

    private static string GetShortId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "00000000";
        }

        string cleaned = FileService.SanitizeFileSystemName(id.Trim());
        return cleaned.Length <= 8 ? cleaned : cleaned[..8];
    }

    private static bool IsOwnedMirrorFileName(string fileName)
    {
        if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ReadOnlySpan<char> stem = fileName.AsSpan(0, fileName.Length - 3);
        int markerIndex = stem.LastIndexOf(MirrorFileTagMarker.AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        ReadOnlySpan<char> idPart = stem[(markerIndex + MirrorFileTagMarker.Length)..];
        return idPart.Length is >= 4 and <= 32;
    }

    private static bool IsImageExtension(string? ext)
    {
        return !string.IsNullOrWhiteSpace(ext) &&
               (ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".webp", StringComparison.OrdinalIgnoreCase));
    }

    private static string EscapeYamlScalar(string value)
    {
        string escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }

    private static string EscapeMarkdownBracket(string value)
    {
        return value
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _quickCaptureService.Changed -= OnQuickCaptureChanged;
        lock (_debounceLock)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        _syncGate.Dispose();
    }
}
