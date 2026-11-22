using DeskBox.Helpers;

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
            if (DeskBox.Helpers.ShortcutHelper.IsShellLinkPath(shortcutPath) &&
                DeskBox.Helpers.ShortcutHelper.TryReadEffectiveTargetPath(shortcutPath) is
                    { Length: > 0 } pidlEffectiveTarget)
            {
                return ProbeEffectiveTargetOnly(pidlEffectiveTarget);
            }

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
            if (File.Exists(expandedTarget) || targetIsDirectory)
            {
                return new(kind, ShortcutTargetStatus.Existing, targetIsDirectory);
            }

            // 32-bit installers running under WOW64 often store raw "%ProgramFiles%\..."
            // or "C:\Program Files\..." alongside a KnownFolderDataBlock pointing to
            // ProgramFilesX86. Check the x86 counterpart before declaring the link broken.
            if (TryResolveProgramFilesX86Counterpart(targetPath, expandedTarget, out string x86Counterpart))
            {
                bool x86IsDirectory = Directory.Exists(x86Counterpart);
                if (File.Exists(x86Counterpart) || x86IsDirectory)
                {
                    return new(kind, ShortcutTargetStatus.Existing, x86IsDirectory);
                }
            }

            // Also consult the non-raw IShellLinkW::GetPath(flags: 0) target so
            // KnownFolderDataBlock / PIDL-backed shortcuts resolve accurately without UI.
            if (DeskBox.Helpers.ShortcutHelper.TryReadEffectiveTargetPath(shortcutPath) is
                { Length: > 0 } effectiveTarget &&
                !string.Equals(effectiveTarget, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                string expandedEffective = Environment.ExpandEnvironmentVariables(effectiveTarget.Trim());
                ShortcutTargetKind effectiveKind = Classify(expandedEffective);
                if (effectiveKind is ShortcutTargetKind.Unc or ShortcutTargetKind.NetworkDrive or
                    ShortcutTargetKind.UriOrShellNamespace)
                {
                    return new(effectiveKind, ShortcutTargetStatus.Unverifiable, TargetIsDirectory: false);
                }

                if (effectiveKind == ShortcutTargetKind.LocalFileSystem)
                {
                    bool effectiveIsDirectory = Directory.Exists(expandedEffective);
                    if (File.Exists(expandedEffective) || effectiveIsDirectory)
                    {
                        return new(effectiveKind, ShortcutTargetStatus.Existing, effectiveIsDirectory);
                    }
                }
            }

            return new(kind, ShortcutTargetStatus.Missing, TargetIsDirectory: false);
        }
        catch
        {
            return new(kind, ShortcutTargetStatus.Unverifiable, TargetIsDirectory: false);
        }
    }

    private static ShortcutTargetProbeResult ProbeEffectiveTargetOnly(string effectiveTarget)
    {
        try
        {
            string expandedEffective = Environment.ExpandEnvironmentVariables(effectiveTarget.Trim());
            ShortcutTargetKind effectiveKind = Classify(expandedEffective);
            if (effectiveKind is ShortcutTargetKind.Unc or ShortcutTargetKind.NetworkDrive or
                ShortcutTargetKind.UriOrShellNamespace or ShortcutTargetKind.Unknown)
            {
                return new(effectiveKind, ShortcutTargetStatus.Unverifiable, TargetIsDirectory: false);
            }

            bool effectiveIsDirectory = Directory.Exists(expandedEffective);
            if (File.Exists(expandedEffective) || effectiveIsDirectory)
            {
                return new(effectiveKind, ShortcutTargetStatus.Existing, effectiveIsDirectory);
            }

            return new(effectiveKind, ShortcutTargetStatus.Missing, TargetIsDirectory: false);
        }
        catch
        {
            return new(ShortcutTargetKind.UriOrShellNamespace, ShortcutTargetStatus.Unverifiable, TargetIsDirectory: false);
        }
    }

    /// <summary>
    /// Unified, cache-backed local target resolver shared by
    /// <see cref="ItemDropBehaviorPolicy"/> and <see cref="ShortcutLaunchPolicy"/>
    /// so pure-PIDL, KnownFolderDataBlock, and WOW64 <c>.lnk</c> items agree
    /// identically across hover, drop, and double-click without touching UNC/network paths.
    /// </summary>
    private readonly record struct CachedShortcutResolution(
        bool Success,
        string ResolvedLocalTarget,
        bool TargetIsDirectory,
        bool HasStoredArguments,
        long LastWriteUtcTicks,
        long FileLength,
        long ExpiresTick64);

    private const int ShortcutResolutionCacheTtlMs = 2_000;
    private const int MaxShortcutResolutionCacheEntries = 256;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CachedShortcutResolution> s_localTargetCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool TryGetFileStamp(string path, out bool exists, out long lastWriteUtcTicks, out long length)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                exists = false;
                lastWriteUtcTicks = 0;
                length = 0;
                return true;
            }

            exists = true;
            lastWriteUtcTicks = info.LastWriteTimeUtc.Ticks;
            length = info.Length;
            return true;
        }
        catch
        {
            exists = false;
            lastWriteUtcTicks = 0;
            length = 0;
            return false;
        }
    }

    internal static bool TryResolveShortcutLocalTarget(
        string shortcutPath,
        string? targetPathHint,
        out string resolvedLocalTarget,
        out bool targetIsDirectory,
        out bool hasStoredArguments)
    {
        resolvedLocalTarget = string.Empty;
        targetIsDirectory = false;
        hasStoredArguments = false;

        if (string.IsNullOrWhiteSpace(shortcutPath) ||
            !DeskBox.Helpers.ShortcutHelper.IsShellLinkPath(shortcutPath))
        {
            return false;
        }

        long now = Environment.TickCount64;
        string cacheKey = string.IsNullOrWhiteSpace(targetPathHint)
            ? shortcutPath
            : string.Concat(shortcutPath, "\0", targetPathHint);
        bool hasStamp = TryGetFileStamp(shortcutPath, out bool shortcutExists, out long stampTicks, out long stampLength);
        if (hasStamp &&
            s_localTargetCache.TryGetValue(cacheKey, out CachedShortcutResolution cached) &&
            now < cached.ExpiresTick64 &&
            cached.LastWriteUtcTicks == stampTicks &&
            cached.FileLength == stampLength)
        {
            resolvedLocalTarget = cached.ResolvedLocalTarget;
            targetIsDirectory = cached.TargetIsDirectory;
            hasStoredArguments = cached.HasStoredArguments;
            return cached.Success;
        }

        bool resolved = TryResolveShortcutLocalTargetCore(
            shortcutPath,
            targetPathHint,
            shortcutExists,
            out resolvedLocalTarget,
            out targetIsDirectory,
            out hasStoredArguments);

        if (hasStamp)
        {
            if (s_localTargetCache.Count >= MaxShortcutResolutionCacheEntries)
            {
                s_localTargetCache.Clear();
            }

            s_localTargetCache[cacheKey] = new CachedShortcutResolution(
                resolved,
                resolvedLocalTarget,
                targetIsDirectory,
                hasStoredArguments,
                stampTicks,
                stampLength,
                now + ShortcutResolutionCacheTtlMs);
        }

        return resolved;
    }

    private static bool TryResolveShortcutLocalTargetCore(
        string shortcutPath,
        string? targetPathHint,
        bool shortcutExists,
        out string resolvedLocalTarget,
        out bool targetIsDirectory,
        out bool hasStoredArguments)
    {
        resolvedLocalTarget = string.Empty;
        targetIsDirectory = false;
        hasStoredArguments = false;

        ShortcutInfo? storedMetadata = shortcutExists
            ? DeskBox.Helpers.ShortcutHelper.ReadStoredMetadata(shortcutPath)
            : null;
        hasStoredArguments = !string.IsNullOrWhiteSpace(storedMetadata?.Arguments);

        string? rawCandidate = !string.IsNullOrWhiteSpace(targetPathHint)
            ? targetPathHint
            : storedMetadata?.TargetPath;

        string? fallbackMissingLocalExpanded = null;
        if (!string.IsNullOrWhiteSpace(rawCandidate))
        {
            string expandedRaw = ShortcutLaunchPolicy.ExpandTargetPath(rawCandidate);
            if (expandedRaw.Length == 0 ||
                Classify(expandedRaw) != ShortcutTargetKind.LocalFileSystem)
            {
                return false;
            }

            bool rawIsDirectory = ShortcutLaunchPolicy.IsExpandedTargetDirectory(expandedRaw);
            if (rawIsDirectory || SafeFileExists(expandedRaw))
            {
                resolvedLocalTarget = expandedRaw;
                targetIsDirectory = rawIsDirectory;
                return true;
            }

            if (TryResolveProgramFilesX86Counterpart(rawCandidate, expandedRaw, out string x86Counterpart))
            {
                bool x86IsDirectory = ShortcutLaunchPolicy.IsExpandedTargetDirectory(x86Counterpart);
                if (x86IsDirectory || SafeFileExists(x86Counterpart))
                {
                    resolvedLocalTarget = x86Counterpart;
                    targetIsDirectory = x86IsDirectory;
                    return true;
                }
            }

            fallbackMissingLocalExpanded = expandedRaw;
        }

        if (shortcutExists &&
            DeskBox.Helpers.ShortcutHelper.TryReadEffectiveTargetPath(shortcutPath) is
                { Length: > 0 } effectiveTarget &&
            !string.Equals(effectiveTarget, rawCandidate, StringComparison.OrdinalIgnoreCase))
        {
            string expandedEffective = ShortcutLaunchPolicy.ExpandTargetPath(effectiveTarget);
            if (expandedEffective.Length == 0 ||
                Classify(expandedEffective) != ShortcutTargetKind.LocalFileSystem)
            {
                return false;
            }

            bool effectiveIsDirectory = ShortcutLaunchPolicy.IsExpandedTargetDirectory(expandedEffective);
            resolvedLocalTarget = expandedEffective;
            targetIsDirectory = effectiveIsDirectory;
            return true;
        }

        if (fallbackMissingLocalExpanded is { Length: > 0 })
        {
            resolvedLocalTarget = fallbackMissingLocalExpanded;
            targetIsDirectory = false;
            return true;
        }

        return false;

        static bool SafeFileExists(string path)
        {
            try
            {
                return File.Exists(path);
            }
            catch
            {
                return false;
            }
        }
    }

    internal static bool TryResolveProgramFilesX86Counterpart(
        string? rawTargetPath,
        string expandedTargetPath,
        out string counterpartPath)
    {
        counterpartPath = string.Empty;
        if (string.IsNullOrWhiteSpace(expandedTargetPath))
        {
            return false;
        }

        string? commonX64 = Environment.GetEnvironmentVariable("ProgramW6432") is { Length: > 0 }
            ? Environment.GetEnvironmentVariable("CommonProgramW6432") ??
              Environment.GetEnvironmentVariable("CommonProgramFiles")
            : Environment.GetEnvironmentVariable("CommonProgramFiles");
        string? commonX86 = Environment.GetEnvironmentVariable("CommonProgramFiles(x86)");
        if (TryReplaceDirectoryPrefix(expandedTargetPath, commonX64, commonX86, out counterpartPath))
        {
            return true;
        }

        string? programFilesX64 = Environment.GetEnvironmentVariable("ProgramW6432") ??
                                  Environment.GetEnvironmentVariable("ProgramFiles");
        string? programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (TryReplaceDirectoryPrefix(expandedTargetPath, programFilesX64, programFilesX86, out counterpartPath))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(rawTargetPath))
        {
            string trimmedRaw = rawTargetPath.Trim();
            if (!string.IsNullOrWhiteSpace(commonX86) &&
                HasPrefixWithPathBoundary(trimmedRaw, "%CommonProgramFiles%"))
            {
                counterpartPath = commonX86.TrimEnd('\\', '/') +
                                  Environment.ExpandEnvironmentVariables(trimmedRaw["%CommonProgramFiles%".Length..]);
                return !string.Equals(counterpartPath, expandedTargetPath, StringComparison.OrdinalIgnoreCase);
            }

            if (!string.IsNullOrWhiteSpace(programFilesX86) &&
                HasPrefixWithPathBoundary(trimmedRaw, "%ProgramFiles%"))
            {
                counterpartPath = programFilesX86.TrimEnd('\\', '/') +
                                  Environment.ExpandEnvironmentVariables(trimmedRaw["%ProgramFiles%".Length..]);
                return !string.Equals(counterpartPath, expandedTargetPath, StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    private static bool TryReplaceDirectoryPrefix(
        string fullPath,
        string? sourcePrefix,
        string? replacementPrefix,
        out string replacedPath)
    {
        replacedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(sourcePrefix) ||
            string.IsNullOrWhiteSpace(replacementPrefix))
        {
            return false;
        }

        string normalizedSource = sourcePrefix.TrimEnd('\\', '/');
        string normalizedReplacement = replacementPrefix.TrimEnd('\\', '/');
        if (normalizedSource.Length == 0 ||
            normalizedReplacement.Length == 0 ||
            string.Equals(normalizedSource, normalizedReplacement, StringComparison.OrdinalIgnoreCase) ||
            !HasPrefixWithPathBoundary(fullPath, normalizedSource))
        {
            return false;
        }

        replacedPath = normalizedReplacement + fullPath[normalizedSource.Length..];
        return true;
    }

    private static bool HasPrefixWithPathBoundary(string value, string prefix)
    {
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (value.Length == prefix.Length)
        {
            return true;
        }

        char next = value[prefix.Length];
        return next == '\\' || next == '/';
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
