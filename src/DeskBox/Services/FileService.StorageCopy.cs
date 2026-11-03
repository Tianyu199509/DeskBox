using System.Security.Cryptography;
using System.Text;
using System.Buffers;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed partial class FileService
{
    internal static void PublishStorageCopy(string temporary, string destination)
    {
        RequireStorageDirectory(Path.GetDirectoryName(destination)!);
        if (!CanUseAtomicMove(temporary, destination))
            throw new IOException("The temporary copy and destination must be on the same volume.");
        StorageMigrationNativeMethods.PublishCopy(temporary, destination);
    }

    internal static void RequireStorageDestinationAncestry(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current);
             current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0 &&
                    !StorageMigrationNativeMethods.IsHydratedCloudReparsePoint(current, out _))
                    throw new IOException($"A linked destination cannot be used for migration: '{current}'.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static void RequireStorageDirectory(string path)
    {
        RequireStoragePath(path, directory: true);
        // Enumerating, rather than Exists(), preserves locked/offline/access errors.
        using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        _ = entries.MoveNext();
    }

    internal static void RequireStoragePath(string path, bool directory)
    {
        FileAttributes attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Encrypted) != 0 ||
            attributes.HasFlag(FileAttributes.Directory) != directory)
            throw new StorageMigrationException(StorageMigrationProblem.Unsupported,
                $"This migration cannot safely copy a link, EFS-encrypted item or changed item: '{path}'.");

        if ((attributes & FileAttributes.ReparsePoint) != 0 &&
            !StorageMigrationNativeMethods.IsHydratedCloudReparsePoint(path, out bool isUnhydratedCloud))
        {
            throw new StorageMigrationException(
                StorageMigrationProblem.Unsupported,
                isUnhydratedCloud
                    ? $"Cloud-only file must be downloaded locally before migration: '{path}'."
                    : $"This migration cannot safely copy a link, EFS-encrypted item or changed item: '{path}'.");
        }

        // A replaced ancestor must not redirect either a copy or its verification.
        for (string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)));
             !string.IsNullOrEmpty(parent); parent = Path.GetDirectoryName(parent))
        {
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0 &&
                !StorageMigrationNativeMethods.IsHydratedCloudReparsePoint(parent, out _))
                throw new IOException($"A linked ancestor cannot be used for this migration: '{parent}'.");
        }
    }

    internal static string HashStorageFile(string path, CancellationToken token, Action<long>? progress = null)
    {
        RequireStoragePath(path, directory: false);
        using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var aggregate = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            var streams = StorageMigrationNativeMethods.ReadStreams(path).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
            if (streams.Count == 0) streams.Add(("::$DATA", 0));
            foreach (var stream in streams)
            {
                token.ThrowIfCancellationRequested();
                aggregate.AppendData(Encoding.UTF8.GetBytes(stream.Name + "\0" + stream.Length + "\0"));
                string streamPath = stream.Name == "::$DATA" ? path : path + stream.Name;
                using var input = new FileStream(streamPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    buffer.Length, FileOptions.SequentialScan);
                int read;
                while ((read = input.Read(buffer)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    aggregate.AppendData(buffer.AsSpan(0, read));
                    progress?.Invoke(read);
                }
            }
            return Convert.ToHexString(aggregate.GetHashAndReset());
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal static string CopyStorageFileVerified(
        string source, string temporaryDestination, CancellationToken token, Action<long> copyProgress,
        Action<long>? verificationProgress = null, bool verifyDestinationImmediately = true)
    {
        RequireStoragePath(source, directory: false);
        RequireStorageDirectory(Path.GetDirectoryName(temporaryDestination)!);
        // The source cannot be modified, renamed or deleted while copying and hashing.
        using var lease = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        string sourceHash = StorageMigrationNativeMethods.CopyStreamsAndHash(
            source,
            temporaryDestination,
            copyProgress,
            token);
        if (verifyDestinationImmediately &&
            !string.Equals(sourceHash, HashStorageFile(temporaryDestination, token, verificationProgress), StringComparison.Ordinal))
        {
            throw new IOException($"Content verification failed for '{source}'. Both locations have been preserved.");
        }

        return sourceHash;
    }
}
