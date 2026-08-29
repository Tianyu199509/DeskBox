namespace DeskBox.Services;

public sealed partial class FileService
{
    private const int MaxReparsePointHops = 64;

    /// <summary>
    /// Resolves every existing directory junction or symbolic link in
    /// <paramref name="path"/> without asking Windows to traverse the complete
    /// link chain in one operation.
    /// </summary>
    /// <remarks>
    /// RedirectionGuard can reject a normal traversal of a user-created mount
    /// point with ERROR_UNTRUSTED_MOUNT_POINT. Reading the reparse point itself
    /// and resolving one hop at a time keeps the system mitigation enabled while
    /// producing a physical path that normal file APIs can use.
    /// </remarks>
    public static bool TryResolveExistingPathForTraversal(
        string path,
        out string resolvedPath)
    {
        return TryResolvePathSegments(
            path,
            allowMissingSuffix: false,
            visitedReparsePoints: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolvedPath: out resolvedPath);
    }

    /// <summary>
    /// Returns whether the path itself is a directory junction or symbolic
    /// link. This inspects the link rather than traversing it.
    /// </summary>
    public static bool IsFileSystemLink(string path)
    {
        try
        {
            string normalizedPath = Path.GetFullPath(path);
            FileAttributes attributes = File.GetAttributes(normalizedPath);
            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                return false;
            }

            FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                ? new DirectoryInfo(normalizedPath)
                : new FileInfo(normalizedPath);
            return info.ResolveLinkTarget(returnFinalTarget: false) is not null;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or
            System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool TryResolvePathWithMissingSuffix(
        string path,
        out string resolvedPath)
    {
        return TryResolvePathSegments(
            path,
            allowMissingSuffix: true,
            visitedReparsePoints: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            resolvedPath: out resolvedPath);
    }

    private static bool TryResolvePathSegments(
        string path,
        bool allowMissingSuffix,
        HashSet<string> visitedReparsePoints,
        out string resolvedPath)
    {
        resolvedPath = string.Empty;

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }

        string? root = Path.GetPathRoot(fullPath);
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        string current = root;
        string remainder = fullPath[root.Length..];
        string[] segments = remainder.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);

        for (int index = 0; index < segments.Length; index++)
        {
            string candidate = Path.Combine(current, segments[index]);
            FileAttributes attributes;
            try
            {
                // GetAttributes opens the reparse point itself. It therefore
                // remains usable when following that point is blocked by
                // RedirectionGuard.
                attributes = File.GetAttributes(candidate);
            }
            catch (Exception ex) when (
                allowMissingSuffix &&
                ex is FileNotFoundException or DirectoryNotFoundException)
            {
                for (int suffixIndex = index; suffixIndex < segments.Length; suffixIndex++)
                {
                    current = Path.Combine(current, segments[suffixIndex]);
                }

                resolvedPath = NormalizeResolvedPath(current);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or
                System.Security.SecurityException)
            {
                return false;
            }

            if ((attributes & FileAttributes.ReparsePoint) == 0)
            {
                current = candidate;
                continue;
            }

            if (visitedReparsePoints.Count >= MaxReparsePointHops)
            {
                return false;
            }

            string normalizedCandidate = NormalizeResolvedPath(candidate);
            if (!visitedReparsePoints.Add(normalizedCandidate))
            {
                return false;
            }

            FileSystemInfo? target;
            try
            {
                FileSystemInfo info = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(candidate)
                    : new FileInfo(candidate);
                target = info.ResolveLinkTarget(returnFinalTarget: false);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or
                ArgumentException or NotSupportedException or
                System.Security.SecurityException)
            {
                return false;
            }

            if (target is null)
            {
                // Not every reparse point is a filesystem link (for example,
                // cloud placeholders). Keep those paths intact.
                current = candidate;
                continue;
            }

            if (!TryResolvePathSegments(
                    target.FullName,
                    allowMissingSuffix: false,
                    visitedReparsePoints: visitedReparsePoints,
                    resolvedPath: out current))
            {
                return false;
            }
        }

        resolvedPath = NormalizeResolvedPath(current);
        return true;
    }

    private static string NormalizeResolvedPath(string path)
    {
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
}
