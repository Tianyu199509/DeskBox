namespace DeskBox.Services;

internal enum ShortcutTargetKind
{
    LocalFileSystem,
    Unc,
    NetworkDrive,
    UriOrShellNamespace,
    Unknown
}

internal enum ShortcutTargetStatus
{
    Existing,
    Missing,
    Unverifiable
}

internal readonly record struct ShortcutTargetProbeResult(
    ShortcutTargetKind Kind,
    ShortcutTargetStatus Status,
    bool TargetIsDirectory)
{
    internal bool IsBroken => Status == ShortcutTargetStatus.Missing;
}

/// <summary>
/// Classifies shortcut targets without treating an unavailable network target
/// as a definitively broken link. Network probing belongs to Explorer, which
/// can handle credentials, reconnects and shell namespace targets correctly.
/// </summary>
internal static class ShortcutTargetProbe
{
    internal static ShortcutTargetProbeResult Probe(
        string shortcutPath,
        string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(shortcutPath) || !File.Exists(shortcutPath))
        {
            return new(
                ShortcutTargetKind.Unknown,
                ShortcutTargetStatus.Missing,
                TargetIsDirectory: false);
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return new(
                ShortcutTargetKind.UriOrShellNamespace,
                ShortcutTargetStatus.Unverifiable,
                TargetIsDirectory: false);
        }

        string expandedTarget;
        try
        {
            expandedTarget = Environment.ExpandEnvironmentVariables(
                targetPath.Trim());
        }
        catch
        {
            return new(
                ShortcutTargetKind.Unknown,
                ShortcutTargetStatus.Unverifiable,
                TargetIsDirectory: false);
        }

        ShortcutTargetKind kind = Classify(expandedTarget);
        if (kind is ShortcutTargetKind.Unc or ShortcutTargetKind.NetworkDrive or
            ShortcutTargetKind.UriOrShellNamespace or ShortcutTargetKind.Unknown)
        {
            return new(kind, ShortcutTargetStatus.Unverifiable, TargetIsDirectory: false);
        }

        try
        {
            // Target existence is the dispatch input (#459, feedback 246):
            // a shortcut to an existing local target — file or directory —
            // uses the default verb.
            bool targetIsDirectory = Directory.Exists(expandedTarget);
            return new(
                kind,
                File.Exists(expandedTarget) || targetIsDirectory
                    ? ShortcutTargetStatus.Existing
                    : ShortcutTargetStatus.Missing,
                targetIsDirectory);
        }
        catch
        {
            return new(kind, ShortcutTargetStatus.Unverifiable, TargetIsDirectory: false);
        }
    }

    internal static ShortcutTargetKind Classify(string? targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            return ShortcutTargetKind.Unknown;
        }

        string candidate;
        try
        {
            candidate = Environment.ExpandEnvironmentVariables(targetPath.Trim());
        }
        catch
        {
            candidate = targetPath.Trim();
        }
        if (IsUncPath(candidate))
        {
            return ShortcutTargetKind.Unc;
        }

        if (Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri) &&
            !uri.IsFile)
        {
            return ShortcutTargetKind.UriOrShellNamespace;
        }

        if (!Path.IsPathFullyQualified(candidate))
        {
            return ShortcutTargetKind.Unknown;
        }

        if (IsNetworkDrive(candidate))
        {
            return ShortcutTargetKind.NetworkDrive;
        }

        return ShortcutTargetKind.LocalFileSystem;
    }

    internal static bool IsUncPath(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               (path.StartsWith(@"\\", StringComparison.Ordinal) ||
                path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNetworkDrive(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrWhiteSpace(root))
            {
                return true;
            }

            DriveType driveType = new DriveInfo(root).DriveType;
            return driveType is DriveType.Network or
                DriveType.NoRootDirectory or
                DriveType.Unknown;
        }
        catch
        {
            // An unavailable mapped drive is still unverifiable. Returning
            // true keeps it out of the local missing-target branch.
            return true;
        }
    }
}
