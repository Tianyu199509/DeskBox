using System.Text.Json;

namespace DeskBox.MusicPackage.Services;

/// <summary>
/// Package-local persistence primitives, a faithful sync port of the host's
/// ResilientJsonStore durability algorithm (audit round 19: do not run a
/// weakened reimplementation - the host's File.Replace fallback exists
/// because ERROR_UNABLE_TO_REMOVE_REPLACED was observed on real packaged
/// store data):
///   - atomic writes: GUID temp file + File.Replace(ignoreMetadataErrors)
///     with a 1175 retry ladder, then a verified in-place fallback
///   - resilient reads: primary -> quarantine corrupt -> backup, and a
///     successful backup read RESTORES the primary so the recovery chain
///     never degrades to "good primary + corrupt backup"
/// </summary>
internal static class PackageFileStore
{
    // Win32 ERROR_UNABLE_TO_REMOVE_REPLACED (1175), surfaced by File.Replace.
    private const int UnableToRemoveReplacedFileHResult = unchecked((int)0x80070497);
    private static readonly int[] ReplaceRetryDelayMs = [50, 150];

    internal static string? TryReadText(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                string content = File.ReadAllText(path);
                if (IsValidObject(content))
                {
                    return content;
                }
            }
            catch
            {
                // Torn/unreadable primary falls through to the backup path.
            }
            QuarantineCorruptFile(path);
        }

        string backup = path + ".bak";
        if (!File.Exists(backup))
        {
            return null;
        }
        try
        {
            string content = File.ReadAllText(backup);
            if (!IsValidObject(content))
            {
                return null;
            }
            // Restore the primary from the good backup so the NEXT torn write
            // still has a valid backup to fall back to.
            RestorePrimary(path, content);
            return content;
        }
        catch
        {
            return null;
        }
    }

    internal static void WriteAtomically(string path, Action<Utf8JsonWriter> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = File.Create(temp))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                write(writer);
            }
            if (File.Exists(path))
            {
                ReplaceOrFallback(temp, path, path + ".bak");
            }
            else
            {
                File.Move(temp, path);
            }
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private static void ReplaceOrFallback(string temp, string path, string backup)
    {
        for (int retry = 0; ; retry++)
        {
            try
            {
                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException error) when (error.HResult == UnableToRemoveReplacedFileHResult)
            {
                if (retry < ReplaceRetryDelayMs.Length)
                {
                    Thread.Sleep(ReplaceRetryDelayMs[retry]);
                    continue;
                }
                // 1175 documents that source and destination keep their
                // names; guard that before using the non-atomic path.
                if (!File.Exists(temp) || !File.Exists(path))
                {
                    throw;
                }
                SaveInPlaceWithVerifiedBackup(path, backup, temp);
                return;
            }
        }
    }

    private static void SaveInPlaceWithVerifiedBackup(string path, string backup, string temp)
    {
        byte[] updated = File.ReadAllBytes(temp);
        byte[] original = File.ReadAllBytes(path);
        WriteAllBytesInPlace(backup, original);
        VerifyFileContents(backup, original, "backup");
        WriteAllBytesInPlace(path, updated);
        VerifyFileContents(path, updated, "primary file");
    }

    private static void WriteAllBytesInPlace(string path, byte[] contents)
    {
        using var stream = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.WriteThrough);
        stream.Write(contents, 0, contents.Length);
        stream.Flush(flushToDisk: true);
    }

    private static void VerifyFileContents(string path, byte[] expected, string description)
    {
        byte[] actual = File.ReadAllBytes(path);
        if (!actual.AsSpan().SequenceEqual(expected))
        {
            throw new IOException($"The {description} could not be verified after the in-place save.");
        }
    }

    private static void QuarantineCorruptFile(string path)
    {
        try
        {
            string quarantined = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            File.Move(path, quarantined);
            PackageLog.Write($"[MusicPackage] quarantined corrupt file as '{Path.GetFileName(quarantined)}'");
        }
        catch
        {
            // Best-effort preservation only.
        }
    }

    private static void RestorePrimary(string path, string content)
    {
        string temp = $"{path}.{Guid.NewGuid():N}.recovery.tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    private static bool IsValidObject(string content)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}


