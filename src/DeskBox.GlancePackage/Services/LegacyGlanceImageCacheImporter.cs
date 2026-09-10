using System.Text.Json;

namespace DeskBox.GlancePackage.Services;

/// <summary>
/// Imports the host's version-1 legacy-glance-cache.json hint. Call and await
/// before the first package cache read/refresh. No host types or JSON reflection.
/// Missing/non-image entries are expired cache entries and are skipped. A catalog
/// is committed only after all usable entries are copied; I/O failure leaves the
/// hint intact so a later call can retry. Existing usable package data wins.
/// </summary>
internal static class LegacyGlanceImageCacheImporter
{
    internal const int MaximumEntries = 256;
    internal const long MaximumImageBytes = 18L * 1024 * 1024;
    private const int MaximumCatalogBytes = 2 * 1024 * 1024;
    // Match the host's legal cache capacity: 72 images, each at most 18 MiB.
    // This is a disk budget; image copying still uses a 64 KiB buffer.
    internal const long MaximumTotalBytes = 72 * MaximumImageBytes;

    internal static Task ImportAsync(string packageDataRoot, CancellationToken cancellationToken = default) =>
        ImportAsync(packageDataRoot, sourceRoot: null, cancellationToken);

    // Test seam: sourceRoot is the legacy cache/glance directory, not the data
    // directory. Production resolves exactly the same value from the host hint.
    internal static Task ImportAsync(string packageDataRoot, string? sourceRoot,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => ImportCoreAsync(packageDataRoot, sourceRoot, cancellationToken), cancellationToken);

    private static async Task ImportCoreAsync(string packageDataRoot, string? sourceRoot, CancellationToken token)
    {
        var ownedFiles = new List<string>();
        bool committed = false;
        try
        {
            token.ThrowIfCancellationRequested();
            string packageRoot = Path.GetFullPath(packageDataRoot);
            EnsureNoReparsePoints(packageRoot);
            if (!Directory.Exists(packageRoot)) return;
            // The exclusive file handle serializes instances, including different
            // processes. The empty lock file is never a success marker or deleted
            // on release (deleting it would introduce an inode/handle race).
            using FileStream gate = await AcquireLockAsync(packageRoot, token).ConfigureAwait(false);
            string targetRoot = Path.Combine(packageRoot, "cache", "glance");
            string catalogPath = Path.Combine(targetRoot, "catalog.json");
            EnsureNoReparsePoints(catalogPath);
            byte[]? previousCatalog = File.Exists(catalogPath)
                ? await ReadBoundedAsync(catalogPath, MaximumCatalogBytes, token).ConfigureAwait(false) : null;
            if (await HasUsableCatalogAsync(previousCatalog, targetRoot, token).ConfigureAwait(false)) return;

            if (sourceRoot is null)
            {
                string hintPath = Path.Combine(packageRoot, "legacy-glance-cache.json");
                if (!File.Exists(hintPath)) return;
                using JsonDocument hint = JsonDocument.Parse(
                    await ReadBoundedAsync(hintPath, 16 * 1024, token).ConfigureAwait(false));
                if (hint.RootElement.ValueKind != JsonValueKind.Object ||
                    !hint.RootElement.TryGetProperty("version", out var version) ||
                    !version.TryGetInt32(out int number) || number != 1 ||
                    !hint.RootElement.TryGetProperty("sourceRoot", out var source) ||
                    source.ValueKind != JsonValueKind.String) return;
                sourceRoot = source.GetString();
            }
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Path.IsPathFullyQualified(sourceRoot)) return;
            sourceRoot = Path.GetFullPath(sourceRoot);
            EnsureNoReparsePoints(sourceRoot);
            string sourceCatalog = Path.Combine(sourceRoot, "catalog.json");
            if (!File.Exists(sourceCatalog)) return;
            using JsonDocument catalog = JsonDocument.Parse(
                await ReadBoundedAsync(sourceCatalog, MaximumCatalogBytes, token).ConfigureAwait(false));
            if (catalog.RootElement.ValueKind != JsonValueKind.Array ||
                catalog.RootElement.GetArrayLength() is 0 or > MaximumEntries) return;

            string imagesRoot = Path.Combine(targetRoot, "images");
            EnsureNoReparsePoints(imagesRoot);
            Directory.CreateDirectory(imagesRoot);
            var importedEntries = new List<(JsonElement Entry, string Path)>();
            long totalBytes = 0;
            foreach (JsonElement entry in catalog.RootElement.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                string sourcePath = ResolveImagePath(entry, sourceRoot);
                string extension = Path.GetExtension(sourcePath).ToLowerInvariant();
                if (!await IsImageAsync(sourcePath, token).ConfigureAwait(false))
                    continue;
                if (totalBytes + new FileInfo(sourcePath).Length > MaximumTotalBytes)
                    throw new InvalidDataException("Legacy image cache exceeds the migration budget.");
                // Unique names and no-overwrite moves protect every existing
                // package image, even when a previous catalog was damaged.
                string destination = Path.Combine(imagesRoot, $"legacy-{Guid.NewGuid():N}{extension}");
                string temporary = destination + ".tmp";
                ownedFiles.Add(temporary);
                long copied = await CopyImageAsync(sourcePath, temporary, token).ConfigureAwait(false);
                totalBytes += copied;
                if (totalBytes > MaximumTotalBytes)
                    throw new InvalidDataException("Legacy image cache exceeds the migration budget.");
                // Validate the bytes actually copied as well as the source header.
                if (!await HasImageHeaderAsync(temporary, extension, token).ConfigureAwait(false))
                    throw new InvalidDataException("Legacy image changed while copying.");
                EnsureNoReparsePoints(imagesRoot);
                File.Move(temporary, destination);
                ownedFiles.Add(destination);
                importedEntries.Add((entry, destination));
            }

            // Do not publish an empty catalog or completion state when every
            // entry has expired; a later call may find usable legacy data.
            if (importedEntries.Count == 0) return;
            string temporaryCatalog = Path.Combine(targetRoot, $"catalog.{Guid.NewGuid():N}.tmp");
            ownedFiles.Add(temporaryCatalog);
            using (var stream = new FileStream(temporaryCatalog, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, 4096, FileOptions.Asynchronous))
            {
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartArray();
                foreach ((JsonElement entry, string path) in importedEntries)
                {
                    writer.WriteStartObject();
                    foreach (JsonProperty property in entry.EnumerateObject())
                    {
                        if (!property.Name.Equals("localPath", StringComparison.OrdinalIgnoreCase))
                            property.WriteTo(writer);
                    }
                    writer.WriteString("localPath", path);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                await writer.FlushAsync(token).ConfigureAwait(false);
                await stream.FlushAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(catalogPath);
            // A cache writer outside this importer may have acquired fresh data
            // while we copied. Do not replace a catalog changed since entry.
            byte[]? currentCatalog = File.Exists(catalogPath)
                ? await ReadBoundedAsync(catalogPath, MaximumCatalogBytes, token).ConfigureAwait(false) : null;
            if (!SameBytes(previousCatalog, currentCatalog)) return;
            token.ThrowIfCancellationRequested();
            // Same-volume rename is the sole commit point; no in-place fallback.
            if (previousCatalog is null) File.Move(temporaryCatalog, catalogPath);
            else File.Replace(temporaryCatalog, catalogPath, null);
            committed = true;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
                                     JsonException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // Best effort. No completion state is written; the next call retries.
            System.Diagnostics.Debug.WriteLine($"[GlancePackage] Legacy cache import deferred: {error.Message}");
        }
        finally
        {
            if (!committed)
            {
                foreach (string path in ownedFiles)
                {
                    try { EnsureNoReparsePoints(path); File.Delete(path); }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    private static bool SameBytes(byte[]? left, byte[]? right) =>
        left is null ? right is null : right is not null && left.AsSpan().SequenceEqual(right);

    private static async Task<FileStream> AcquireLockAsync(string root, CancellationToken token)
    {
        string path = Path.Combine(root, ".legacy-glance-cache.lock");
        while (true)
        {
            token.ThrowIfCancellationRequested();
            EnsureNoReparsePoints(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                await Task.Delay(50, token).ConfigureAwait(false);
            }
        }
    }

    private static async Task<bool> HasUsableCatalogAsync(byte[]? json, string root, CancellationToken token)
    {
        if (json is null) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return false;
            // Be conservative with catalogs beyond our migration bound: they
            // belong to the package and must not be displaced by legacy data.
            if (document.RootElement.GetArrayLength() > MaximumEntries) return true;
            foreach (JsonElement entry in document.RootElement.EnumerateArray())
            {
                try
                {
                    if (await IsImageAsync(ResolveImagePath(entry, root), token).ConfigureAwait(false)) return true;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { }
            }
        }
        catch (JsonException) { }
        return false;
    }

    private static string ResolveImagePath(JsonElement entry, string root)
    {
        if (entry.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Invalid image entry.");
        string? localPath = null;
        foreach (JsonProperty property in entry.EnumerateObject())
        {
            if (!property.Name.Equals("localPath", StringComparison.OrdinalIgnoreCase)) continue;
            if (localPath is not null || property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Ambiguous image path.");
            localPath = property.Value.GetString();
        }
        if (string.IsNullOrWhiteSpace(localPath)) throw new InvalidDataException("Missing image path.");
        string images = Path.GetFullPath(Path.Combine(root, "images")) + Path.DirectorySeparatorChar;
        string path = Path.GetFullPath(localPath, root);
        if (!path.StartsWith(images, StringComparison.OrdinalIgnoreCase) ||
            path[images.Length..].Contains(':'))
            throw new InvalidDataException("Image path escapes the legacy images directory.");
        EnsureNoReparsePoints(path);
        return path;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        // Inspect every existing ancestor, including the supplied root itself.
        // This rejects junctions/symlinks both in source and destination trees.
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Reparse points are not migration inputs or destinations.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken token)
    {
        EnsureNoReparsePoints(path);
        using var stream = OpenRead(path);
        if (stream.Length > limit) throw new InvalidDataException("Migration JSON exceeds its size limit.");
        using var memory = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (memory.Length + read > limit) throw new InvalidDataException("Migration JSON grew beyond its limit.");
            await memory.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        return memory.ToArray();
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task<long> CopyImageAsync(string source, string destination, CancellationToken token)
    {
        EnsureNoReparsePoints(source);
        EnsureNoReparsePoints(destination);
        using var input = OpenRead(source);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        long expectedLength = input.Length;
        if (expectedLength is <= 0 or > MaximumImageBytes) throw new InvalidDataException("Invalid image size.");
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            total += read;
            if (total > MaximumImageBytes) throw new InvalidDataException("Image exceeds migration size limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
        }
        if (total != expectedLength) throw new InvalidDataException("Image changed while copying.");
        await output.FlushAsync(token).ConfigureAwait(false);
        return total;
    }

    private static async Task<bool> IsImageAsync(string path, CancellationToken token)
    {
        EnsureNoReparsePoints(path);
        try
        {
            // File.Exists masks access and other I/O errors as "missing". Only
            // genuinely absent files and unsupported bytes are safe to skip.
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0 ||
                new FileInfo(path).Length is <= 0 or > MaximumImageBytes) return false;
            return await HasImageHeaderAsync(path, Path.GetExtension(path).ToLowerInvariant(), token).ConfigureAwait(false);
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static async Task<bool> HasImageHeaderAsync(string path, string extension, CancellationToken token)
    {
        EnsureNoReparsePoints(path);
        using var stream = OpenRead(path);
        byte[] header = new byte[12];
        int count = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false,
            cancellationToken: token).ConfigureAwait(false);
        return extension switch
        {
            ".jpg" or ".jpeg" => count >= 3 && header[0] == 0xff && header[1] == 0xd8 && header[2] == 0xff,
            ".png" => count >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            ".gif" => count >= 6 && (header.AsSpan(0, 6).SequenceEqual("GIF87a"u8) || header.AsSpan(0, 6).SequenceEqual("GIF89a"u8)),
            ".webp" => count >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8),
            ".bmp" => count >= 2 && header[0] == 'B' && header[1] == 'M',
            ".tif" or ".tiff" => count >= 4 && (header.AsSpan(0, 4).SequenceEqual(new byte[] { 73, 73, 42, 0 }) ||
                header.AsSpan(0, 4).SequenceEqual(new byte[] { 77, 77, 0, 42 })),
            _ => false,
        };
    }
}
