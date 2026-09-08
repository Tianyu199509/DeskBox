using System.Collections.Concurrent;
using System.Text;

namespace DeskBox.Services.Plugins;

/// <summary>Shared bounded file operations. Package content is untrusted, including directory metadata.</summary>
internal static class PluginPackageStorage
{
    private static readonly ConcurrentDictionary<string, object> Locks = new(StringComparer.OrdinalIgnoreCase);

    internal static object Gate(string root) => Locks.GetOrAdd(Path.GetFullPath(root), _ => new object());

    internal static string UnderRoot(string root, string relative)
    {
        string fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        string path = Path.GetFullPath(Path.Combine(fullRoot, relative));
        if (!path.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("package path escapes the install root");
        }
        return path;
    }

    internal static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"package tree contains a reparse point: {path}");
        }
    }

    internal static void CopySnapshot(string source, string destination, PluginVerificationLimits limits)
    {
        (List<string> files, List<string> failures) = PluginPackageVerifier.WalkPackageTree(source, limits);
        if (failures.Count != 0)
        {
            throw new InvalidDataException(string.Join("; ", failures));
        }
        long remaining = limits.MaxTotalExpandedBytes;
        foreach (string file in files)
        {
            string relative = Path.GetRelativePath(source, file);
            string target = UnderRoot(destination, relative);
            RejectReparsePoint(file);
            using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            long maximum = Math.Min(limits.MaxSingleFileBytes, remaining);
            if (relative == "manifest.json") maximum = Math.Min(maximum, limits.MaxManifestBytes);
            if (relative == "package.integrity") maximum = Math.Min(maximum, limits.MaxIntegrityBytes);
            if (input.Length > maximum)
            {
                throw new InvalidDataException($"package file exceeds the input budget: {relative}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            remaining -= CopyBounded(input, output, maximum);
            output.Flush(flushToDisk: true);
        }
    }

    internal static string ReadText(string path, long maximum)
    {
        RejectReparsePoint(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} exceeds the {maximum}-byte input budget");
        }
        using var output = new MemoryStream();
        CopyBounded(input, output, maximum);
        return Encoding.UTF8.GetString(output.ToArray()).TrimStart('\uFEFF');
    }

    private static long CopyBounded(Stream input, Stream output, long maximum)
    {
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (read > maximum - total)
            {
                throw new InvalidDataException("package exceeds the remaining input budget");
            }
            output.Write(buffer, 0, read);
            total += read;
        }
        return total;
    }

    internal static void WriteAtomically(string path, byte[] bytes)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write(bytes);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Deletes only a checked child of the given root, never traversing junctions or symlinks.</summary>
    internal static void DeleteDirectory(string root, string directory)
    {
        string checkedDirectory = UnderRoot(root, Path.GetRelativePath(root, directory));
        if (!Directory.Exists(checkedDirectory)) return;
        if ((File.GetAttributes(checkedDirectory) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(checkedDirectory);
            return;
        }
        foreach (string entry in Directory.EnumerateFileSystemEntries(checkedDirectory))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                DeleteDirectory(root, entry);
            }
            else
            {
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                {
                    File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                }
                File.Delete(entry);
            }
        }
        Directory.Delete(checkedDirectory);
    }
}

