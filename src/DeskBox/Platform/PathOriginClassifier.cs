using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

internal readonly record struct SourceOriginSnapshot(
    InboundSourceProtectionKind InboundProtection,
    bool HasMoveProtectedRoot,
    bool HasUnhydratedCloudPlaceholder,
    bool HasApplicationCacheSource,
    bool HasTransientTempOrMsixCacheSource,
    bool PrefersShortcutByDefault = false)
{
    public static SourceOriginSnapshot Empty => default;

    public bool IsProtectedCopyByDefault =>
        InboundProtection != InboundSourceProtectionKind.None ||
        HasUnhydratedCloudPlaceholder ||
        HasApplicationCacheSource ||
        HasTransientTempOrMsixCacheSource;

    public bool IsMoveProtectedSystemOrSharedSource =>
        InboundProtection == InboundSourceProtectionKind.SystemOrSharedProtected ||
        HasMoveProtectedRoot;
}

/// <summary>
/// Unified gesture-cached classifier for drag-and-drop filesystem origins and
/// volume identity. Resolves <c>subst</c> drive aliases, NTFS directory mount
/// points (<c>GetVolumePathNameW</c> + <c>GetVolumeNameForVolumeMountPointW</c>),
/// external/virtual buses, cloud placeholders, chat-client attachment caches,
/// and MSIX/archive transient staging roots while bounding hover-path latency
/// so UI/OLE <c>DragOver</c> frames remain zero-I/O after initial resolution.
/// </summary>
internal static class PathOriginClassifier
{
    private const long VolumeCacheTtlMs = 30_000;
    private const long DosDeviceCacheTtlMs = 30_000;
    private const long GestureSnapshotTtlMs = 5_000;
    private const int MaxVolumeCacheEntries = 512;
    private const int MaxSubstHops = 4;

    private readonly record struct CachedVolumeIdentity(
        string VolumeIdentity,
        string MountPoint,
        bool IsNonRootMountExternalMedia,
        long ExpiresTick64);

    private readonly record struct CachedDosDeviceTarget(
        string? Target,
        long ExpiresTick64);

    private sealed record GestureSnapshotCacheEntry(
        IReadOnlyList<string>? OriginalReference,
        string[] NormalizedPaths,
        SourceOriginSnapshot Snapshot,
        long ExpiresTick64);

    private static readonly ConcurrentDictionary<string, CachedVolumeIdentity> s_volumeCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<char, CachedDosDeviceTarget> s_dosDeviceCache = new();

    private static volatile GestureSnapshotCacheEntry? s_gestureSnapshot;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetVolumePathNameW(
        string lpszFileName,
        char* lpszVolumePathName,
        uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint,
        char* lpszVolumeName,
        uint cchBufferLength);

    internal static void ResetCachesForTests()
    {
        s_volumeCache.Clear();
        s_dosDeviceCache.Clear();
        s_gestureSnapshot = null;
    }

    internal static string ResolveSubstPath(
        string? path,
        Func<char, string?>? dosDeviceProbe = null)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        string current = DriveMediaInterop.StripExtendedPathPrefix(path.Trim());
        for (int hop = 0; hop < MaxSubstHops; hop++)
        {
            if (current.Length < 2 ||
                !char.IsAsciiLetter(current[0]) ||
                current[1] != ':')
            {
                break;
            }

            char driveLetter = char.ToUpperInvariant(current[0]);
            string? target = dosDeviceProbe is not null
                ? dosDeviceProbe(driveLetter)
                : GetDosDeviceTargetCached(driveLetter);
            if (string.IsNullOrWhiteSpace(target))
            {
                break;
            }

            string trimmedTarget = target.Trim();
            string mappedPrefix;
            if (trimmedTarget.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
            {
                mappedPrefix = trimmedTarget[4..];
                if (mappedPrefix.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
                {
                    mappedPrefix = @"\\" + mappedPrefix[4..];
                }
            }
            else if (TryExtractLanmanUncPrefix(trimmedTarget, driveLetter, out string lanmanUnc))
            {
                mappedPrefix = lanmanUnc;
            }
            else
            {
                break;
            }

            mappedPrefix = mappedPrefix.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            if (mappedPrefix.Length == 0)
            {
                break;
            }

            string remainder = current.Length > 2
                ? current[2..].TrimStart(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar)
                : string.Empty;
            string next = remainder.Length > 0
                ? mappedPrefix + Path.DirectorySeparatorChar + remainder
                : mappedPrefix + Path.DirectorySeparatorChar;
            if (string.Equals(next, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = next;
        }

        return current;
    }

    internal static string GetVolumeIdentity(
        string? path,
        Func<char, string?>? dosDeviceProbe = null,
        Func<string, string?>? volumeMountPointProbe = null,
        Func<string, string?>? volumeGuidProbe = null)
    {
        return ResolveVolumeDetails(
            path,
            dosDeviceProbe,
            volumeMountPointProbe,
            volumeGuidProbe).VolumeIdentity;
    }

    private static (string VolumeIdentity, string MountPoint, bool IsNonRootMountExternalMedia) ResolveVolumeDetails(
        string? path,
        Func<char, string?>? dosDeviceProbe,
        Func<string, string?>? volumeMountPointProbe,
        Func<string, string?>? volumeGuidProbe)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (string.Empty, string.Empty, false);
        }

        string resolved = ResolveSubstPath(path, dosDeviceProbe);
        if (resolved.Length == 0)
        {
            return (string.Empty, string.Empty, false);
        }

        // Normalize UNC share roots purely lexically (\\SERVER\SHARE\) so
        // unreachable or sleeping SMB shares never block volume comparisons.
        if (resolved.StartsWith(@"\\", StringComparison.Ordinal) &&
            !resolved.StartsWith(@"\\?\", StringComparison.Ordinal) &&
            !resolved.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            return (NormalizeUncShareRoot(resolved), string.Empty, false);
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(resolved);
        }
        catch
        {
            return (string.Empty, string.Empty, false);
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return (NormalizeUncShareRoot(fullPath), string.Empty, false);
        }

        bool canUseCache = dosDeviceProbe is null &&
            volumeMountPointProbe is null &&
            volumeGuidProbe is null;
        string cacheKey = GetDirectoryLookupKey(fullPath);
        long now = Environment.TickCount64;
        if (canUseCache &&
            s_volumeCache.TryGetValue(cacheKey, out CachedVolumeIdentity cached) &&
            now < cached.ExpiresTick64)
        {
            return (cached.VolumeIdentity, cached.MountPoint, cached.IsNonRootMountExternalMedia);
        }

        string fallbackRoot = NormalizeDriveRootFallback(fullPath);
        string? mountPoint = volumeMountPointProbe is not null
            ? volumeMountPointProbe(fullPath)
            : QueryVolumeMountPoint(fullPath);
        if (string.IsNullOrWhiteSpace(mountPoint))
        {
            return (fallbackRoot, string.Empty, false);
        }

        string normalizedMountPoint = EnsureTrailingBackslash(mountPoint.Trim());
        string? volumeGuid = volumeGuidProbe is not null
            ? volumeGuidProbe(normalizedMountPoint)
            : QueryVolumeGuid(normalizedMountPoint);
        string identity = !string.IsNullOrWhiteSpace(volumeGuid)
            ? EnsureTrailingBackslash(volumeGuid.Trim()).ToUpperInvariant()
            : normalizedMountPoint.ToUpperInvariant();

        bool nonRootExternal = false;
        if (canUseCache &&
            !string.IsNullOrWhiteSpace(volumeGuid) &&
            normalizedMountPoint.Length > 3)
        {
            nonRootExternal = DriveMediaInterop.IsVolumeGuidExternalMedia(volumeGuid);
        }

        if (canUseCache && identity.Length > 0)
        {
            if (s_volumeCache.Count >= MaxVolumeCacheEntries)
            {
                s_volumeCache.Clear();
            }

            s_volumeCache[cacheKey] = new CachedVolumeIdentity(
                identity,
                normalizedMountPoint,
                nonRootExternal,
                now + VolumeCacheTtlMs);
        }

        return (identity, normalizedMountPoint, nonRootExternal);
    }

    internal static bool AreSameVolume(
        string? sourcePath,
        string? destinationPath,
        TimeSpan? probeTimeout = null,
        Func<char, string?>? dosDeviceProbe = null,
        Func<string, string?>? volumeMountPointProbe = null,
        Func<string, string?>? volumeGuidProbe = null)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) ||
            string.IsNullOrWhiteSpace(destinationPath))
        {
            return false;
        }

        if (probeTimeout is not { } timeout ||
            (dosDeviceProbe is null &&
             volumeMountPointProbe is null &&
             volumeGuidProbe is null &&
             AreBothVolumeIdentitiesWarm(sourcePath, destinationPath)))
        {
            return CompareVolumeIdentitiesCore(
                sourcePath,
                destinationPath,
                dosDeviceProbe,
                volumeMountPointProbe,
                volumeGuidProbe);
        }

        try
        {
            Task<bool> probeTask = Task.Run(() => CompareVolumeIdentitiesCore(
                sourcePath,
                destinationPath,
                dosDeviceProbe,
                volumeMountPointProbe,
                volumeGuidProbe));
            return probeTask.Wait(timeout) && probeTask.GetAwaiter().GetResult();
        }
        catch
        {
            // Timed-out or faulted volume probes fail closed to cross-volume
            // (Copy) so UI hover never stalls and originals are never deleted.
            return false;
        }
    }

    internal static bool AreAllOnSameVolume(
        IEnumerable<string>? sourcePaths,
        string? destinationPath,
        TimeSpan? probeTimeout = null,
        Func<char, string?>? dosDeviceProbe = null,
        Func<string, string?>? volumeMountPointProbe = null,
        Func<string, string?>? volumeGuidProbe = null)
    {
        if (sourcePaths is null)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            return !sourcePaths.Any(static path => !string.IsNullOrWhiteSpace(path));
        }

        string[] materialized = sourcePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        if (materialized.Length == 0)
        {
            return true;
        }

        if (probeTimeout is not { } timeout ||
            (dosDeviceProbe is null &&
             volumeMountPointProbe is null &&
             volumeGuidProbe is null &&
             AreAllVolumeIdentitiesWarm(materialized, destinationPath)))
        {
            string destIdentity = GetVolumeIdentity(
                destinationPath,
                dosDeviceProbe,
                volumeMountPointProbe,
                volumeGuidProbe);
            if (destIdentity.Length == 0)
            {
                return false;
            }

            foreach (string source in materialized)
            {
                string srcIdentity = GetVolumeIdentity(
                    source,
                    dosDeviceProbe,
                    volumeMountPointProbe,
                    volumeGuidProbe);
                if (srcIdentity.Length == 0 ||
                    !string.Equals(srcIdentity, destIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        try
        {
            Task<bool> probeTask = Task.Run(() => AreAllOnSameVolume(
                materialized,
                destinationPath,
                probeTimeout: null,
                dosDeviceProbe,
                volumeMountPointProbe,
                volumeGuidProbe));
            return probeTask.Wait(timeout) && probeTask.GetAwaiter().GetResult();
        }
        catch
        {
            return false;
        }
    }

    internal static SourceOriginSnapshot ClassifySources(
        IEnumerable<string>? sourcePaths,
        TimeSpan? probeTimeout = null,
        Func<char, DriveType>? driveTypeProbe = null,
        Func<char, (StorageBusKind BusKind, bool RemovableMedia)>? busProbe = null,
        Func<string, bool>? cloudPlaceholderProbe = null)
    {
        if (sourcePaths is null)
        {
            return SourceOriginSnapshot.Empty;
        }

        bool canUseGestureCache = driveTypeProbe is null &&
            busProbe is null &&
            cloudPlaceholderProbe is null;
        long now = Environment.TickCount64;
        GestureSnapshotCacheEntry? cached = s_gestureSnapshot;

        if (canUseGestureCache &&
            sourcePaths is IReadOnlyList<string> pathList)
        {
            if (pathList.Count == 0)
            {
                return SourceOriginSnapshot.Empty;
            }

            if (cached is not null &&
                now < cached.ExpiresTick64 &&
                (ReferenceEquals(cached.OriginalReference, pathList) ||
                 SequenceMatchesOrdinalIgnoreCase(cached.NormalizedPaths, pathList)))
            {
                return cached.Snapshot;
            }
        }

        string[] normalizedBatch = sourcePaths
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .ToArray();
        if (normalizedBatch.Length == 0)
        {
            return SourceOriginSnapshot.Empty;
        }

        if (canUseGestureCache &&
            cached is not null &&
            now < cached.ExpiresTick64 &&
            SequenceMatchesOrdinalIgnoreCase(cached.NormalizedPaths, normalizedBatch))
        {
            return cached.Snapshot;
        }

        SourceOriginSnapshot snapshot;
        if (probeTimeout is not { } timeout)
        {
            snapshot = ClassifySourcesUncached(
                normalizedBatch,
                driveTypeProbe,
                busProbe,
                cloudPlaceholderProbe);
        }
        else
        {
            try
            {
                Task<SourceOriginSnapshot> probeTask = Task.Run(() => ClassifySourcesUncached(
                    normalizedBatch,
                    driveTypeProbe,
                    busProbe,
                    cloudPlaceholderProbe));
                if (probeTask.Wait(timeout))
                {
                    snapshot = probeTask.GetAwaiter().GetResult();
                }
                else
                {
                    // Do not cache the timed-out conservative result as final:
                    // when the background task finishes it populates the volume
                    // and drive caches, and the next hover frame reads the real
                    // classification without blocking.
                    _ = probeTask.ContinueWith(
                        completed =>
                        {
                            if (canUseGestureCache && completed.IsCompletedSuccessfully)
                            {
                                s_gestureSnapshot = new GestureSnapshotCacheEntry(
                                    sourcePaths as IReadOnlyList<string>,
                                    normalizedBatch,
                                    completed.Result,
                                    Environment.TickCount64 + GestureSnapshotTtlMs);
                            }
                        },
                        TaskScheduler.Default);
                    return new SourceOriginSnapshot(
                        InboundSourceProtectionKind.ExternalRemovableOrNetwork,
                        HasMoveProtectedRoot: DriveMediaInterop.IsProtectedDragOutSource(normalizedBatch),
                        HasUnhydratedCloudPlaceholder: false,
                        HasApplicationCacheSource: false,
                        HasTransientTempOrMsixCacheSource: false);
                }
            }
            catch
            {
                return new SourceOriginSnapshot(
                    InboundSourceProtectionKind.ExternalRemovableOrNetwork,
                    HasMoveProtectedRoot: DriveMediaInterop.IsProtectedDragOutSource(normalizedBatch),
                    HasUnhydratedCloudPlaceholder: false,
                    HasApplicationCacheSource: false,
                    HasTransientTempOrMsixCacheSource: false);
            }
        }

        if (canUseGestureCache)
        {
            s_gestureSnapshot = new GestureSnapshotCacheEntry(
                sourcePaths as IReadOnlyList<string>,
                normalizedBatch,
                snapshot,
                Environment.TickCount64 + GestureSnapshotTtlMs);
        }

        return snapshot;
    }

    private static SourceOriginSnapshot ClassifySourcesUncached(
        string[] rawPaths,
        Func<char, DriveType>? driveTypeProbe,
        Func<char, (StorageBusKind BusKind, bool RemovableMedia)>? busProbe,
        Func<string, bool>? cloudPlaceholderProbe)
    {
        InboundSourceProtectionKind protection = InboundSourceProtectionKind.None;
        bool hasMoveProtectedRoot = false;
        bool hasCloudPlaceholder = false;
        bool hasAppCache = false;
        bool hasTransientTemp = false;
        int inspectedCount = 0;
        bool allBundledExecutables = true;

        foreach (string raw in rawPaths)
        {
            string substResolved = driveTypeProbe is null && busProbe is null
                ? ResolveSubstPath(raw)
                : DriveMediaInterop.StripExtendedPathPrefix(raw.Trim());
            if (substResolved.Length == 0)
            {
                continue;
            }

            inspectedCount++;
            if (allBundledExecutables &&
                (driveTypeProbe is not null ||
                 busProbe is not null ||
                 !DriveMediaInterop.IsInstalledOrBundledExecutablePath(substResolved)))
            {
                allBundledExecutables = false;
            }

            InboundSourceProtectionKind itemProtection = DriveMediaInterop.ClassifyInboundDropSources(
                [substResolved],
                driveTypeProbe,
                busProbe);
            if (itemProtection == InboundSourceProtectionKind.SystemOrSharedProtected)
            {
                protection = InboundSourceProtectionKind.SystemOrSharedProtected;
                hasMoveProtectedRoot = true;
            }
            else if (itemProtection == InboundSourceProtectionKind.ExternalRemovableOrNetwork &&
                     protection == InboundSourceProtectionKind.None)
            {
                protection = InboundSourceProtectionKind.ExternalRemovableOrNetwork;
            }

            if (!hasMoveProtectedRoot &&
                DriveMediaInterop.IsProtectedDragOutSource([substResolved]))
            {
                hasMoveProtectedRoot = true;
            }

            if (DriveMediaInterop.IsProtectedApplicationCachePath(substResolved))
            {
                hasAppCache = true;
                if (protection == InboundSourceProtectionKind.None)
                {
                    protection = InboundSourceProtectionKind.ExternalRemovableOrNetwork;
                }
            }

            if (DropStagingFileOps.IsMsixPackageTransientDropPath(substResolved) ||
                DropStagingFileOps.IsExternalTransientTempDropPath(substResolved, hasShellIdList: true))
            {
                hasTransientTemp = true;
                if (protection == InboundSourceProtectionKind.None)
                {
                    protection = InboundSourceProtectionKind.ExternalRemovableOrNetwork;
                }
            }

            bool isCloudPlaceholder = cloudPlaceholderProbe is not null
                ? cloudPlaceholderProbe(substResolved)
                : (driveTypeProbe is null &&
                   busProbe is null &&
                   DriveMediaInterop.IsUnhydratedCloudPlaceholderPath(substResolved));
            if (isCloudPlaceholder)
            {
                hasCloudPlaceholder = true;
                if (protection == InboundSourceProtectionKind.None)
                {
                    protection = InboundSourceProtectionKind.ExternalRemovableOrNetwork;
                }
            }

            // Check NTFS directory mount points (e.g. C:\Mount\CameraSD -> USB SD volume)
            // when the host drive letter itself was classified as internal fixed media.
            if (protection == InboundSourceProtectionKind.None &&
                driveTypeProbe is null &&
                busProbe is null)
            {
                (_, _, bool nonRootMountExternal) = ResolveVolumeDetails(
                    substResolved,
                    dosDeviceProbe: null,
                    volumeMountPointProbe: null,
                    volumeGuidProbe: null);
                if (nonRootMountExternal)
                {
                    protection = InboundSourceProtectionKind.ExternalRemovableOrNetwork;
                }
            }
        }

        return new SourceOriginSnapshot(
            protection,
            hasMoveProtectedRoot,
            hasCloudPlaceholder,
            hasAppCache,
            hasTransientTemp,
            PrefersShortcutByDefault: inspectedCount > 0 && allBundledExecutables);
    }

    private static bool CompareVolumeIdentitiesCore(
        string sourcePath,
        string destinationPath,
        Func<char, string?>? dosDeviceProbe,
        Func<string, string?>? volumeMountPointProbe,
        Func<string, string?>? volumeGuidProbe)
    {
        string srcIdentity = GetVolumeIdentity(
            sourcePath,
            dosDeviceProbe,
            volumeMountPointProbe,
            volumeGuidProbe);
        string dstIdentity = GetVolumeIdentity(
            destinationPath,
            dosDeviceProbe,
            volumeMountPointProbe,
            volumeGuidProbe);
        return srcIdentity.Length > 0 &&
               dstIdentity.Length > 0 &&
               string.Equals(srcIdentity, dstIdentity, StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreBothVolumeIdentitiesWarm(string sourcePath, string destinationPath) =>
        IsVolumeIdentityWarm(sourcePath) && IsVolumeIdentityWarm(destinationPath);

    private static bool AreAllVolumeIdentitiesWarm(string[] sourcePaths, string destinationPath)
    {
        if (!IsVolumeIdentityWarm(destinationPath))
        {
            return false;
        }

        foreach (string sourcePath in sourcePaths)
        {
            if (!IsVolumeIdentityWarm(sourcePath))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVolumeIdentityWarm(string rawPath)
    {
        string stripped = DriveMediaInterop.StripExtendedPathPrefix(rawPath.Trim());
        if (stripped.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return true;
        }

        if (stripped.Length >= 2 &&
            char.IsAsciiLetter(stripped[0]) &&
            stripped[1] == ':')
        {
            char drive = char.ToUpperInvariant(stripped[0]);
            long now = Environment.TickCount64;
            if (!s_dosDeviceCache.TryGetValue(drive, out CachedDosDeviceTarget dosCached) ||
                now >= dosCached.ExpiresTick64)
            {
                return false;
            }

            string resolved = ResolveSubstPath(stripped);
            if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return true;
            }

            try
            {
                string key = GetDirectoryLookupKey(Path.GetFullPath(resolved));
                return s_volumeCache.TryGetValue(key, out CachedVolumeIdentity volCached) &&
                       now < volCached.ExpiresTick64;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static string? GetDosDeviceTargetCached(char upperDriveLetter)
    {
        long now = Environment.TickCount64;
        if (s_dosDeviceCache.TryGetValue(upperDriveLetter, out CachedDosDeviceTarget cached) &&
            now < cached.ExpiresTick64)
        {
            return cached.Target;
        }

        string? target = DriveMediaInterop.QueryDosDeviceTarget(upperDriveLetter);
        s_dosDeviceCache[upperDriveLetter] = new CachedDosDeviceTarget(
            target,
            now + DosDeviceCacheTtlMs);
        return target;
    }

    private static unsafe string? QueryVolumeMountPoint(string fullPath)
    {
        const int BufferChars = 1024;
        char* buffer = stackalloc char[BufferChars];
        string extended = StorageMigrationNativeMethods.Extended(fullPath);
        if (GetVolumePathNameW(extended, buffer, BufferChars) ||
            GetVolumePathNameW(fullPath, buffer, BufferChars))
        {
            string result = DriveMediaInterop.StripExtendedPathPrefix(new string(buffer));
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        return null;
    }

    private static unsafe string? QueryVolumeGuid(string mountPointWithTrailingSlash)
    {
        const int BufferChars = 128;
        char* buffer = stackalloc char[BufferChars];
        if (GetVolumeNameForVolumeMountPointW(mountPointWithTrailingSlash, buffer, BufferChars))
        {
            string guid = new string(buffer);
            return string.IsNullOrWhiteSpace(guid) ? null : guid;
        }

        return null;
    }

    internal static bool TryExtractLanmanUncPrefix(
        string? dosTarget,
        char upperDriveLetter,
        out string uncPrefix)
    {
        uncPrefix = string.Empty;
        if (string.IsNullOrWhiteSpace(dosTarget))
        {
            return false;
        }

        const string LanmanPrefix = @"\Device\LanmanRedirector\";
        string trimmed = dosTarget.Trim();
        if (!trimmed.StartsWith(LanmanPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Format 1: \Device\LanmanRedirector\;Z:0000000000012345\server\share
        // Format 2: \Device\LanmanRedirector\server\share
        string rest = trimmed[LanmanPrefix.Length..];
        if (rest.StartsWith(';'))
        {
            int slashAfterSession = rest.IndexOf('\\');
            if (slashAfterSession < 0 || slashAfterSession + 1 >= rest.Length)
            {
                return false;
            }

            _ = upperDriveLetter;
            rest = rest[(slashAfterSession + 1)..];
        }

        rest = rest.Trim('\\', '/');
        if (rest.Length == 0 || !rest.Contains('\\'))
        {
            return false;
        }

        uncPrefix = @"\\" + rest;
        return true;
    }

    internal static string GetDirectoryLookupKey(string fullPath, Func<string, bool>? directoryExistsProbe = null)
    {
        if (fullPath.EndsWith(Path.DirectorySeparatorChar) ||
            fullPath.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return EnsureTrailingBackslash(fullPath);
        }

        string trimmed = Path.TrimEndingDirectorySeparator(fullPath);
        bool isDirectory = directoryExistsProbe is not null
            ? directoryExistsProbe(trimmed)
            : Directory.Exists(trimmed);
        if (isDirectory)
        {
            // Keep the directory itself as the cache key so an NTFS volume mount
            // point (e.g. C:\Mounts\CameraSD -> USB SD card) never poisons its
            // parent key (C:\Mounts\) for sibling local directories.
            return EnsureTrailingBackslash(trimmed);
        }

        string? parent = Path.GetDirectoryName(trimmed);
        return string.IsNullOrWhiteSpace(parent)
            ? EnsureTrailingBackslash(fullPath)
            : EnsureTrailingBackslash(parent);
    }

    private static string NormalizeDriveRootFallback(string fullPath)
    {
        try
        {
            string? root = Path.GetPathRoot(fullPath);
            return string.IsNullOrWhiteSpace(root)
                ? string.Empty
                : EnsureTrailingBackslash(root).ToUpperInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeUncShareRoot(string uncPath)
    {
        string normalized = uncPath.Replace('/', '\\');
        if (!normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> afterSlashes = normalized.AsSpan(2);
        int serverEnd = afterSlashes.IndexOf('\\');
        if (serverEnd <= 0)
        {
            return string.Empty;
        }

        ReadOnlySpan<char> server = afterSlashes[..serverEnd];
        ReadOnlySpan<char> afterServer = afterSlashes[(serverEnd + 1)..];
        if (afterServer.IsEmpty)
        {
            return string.Empty;
        }

        int shareEnd = afterServer.IndexOf('\\');
        ReadOnlySpan<char> share = shareEnd >= 0
            ? afterServer[..shareEnd]
            : afterServer;
        if (share.IsEmpty)
        {
            return string.Empty;
        }

        return string.Concat(@"\\", server, @"\", share).ToUpperInvariant() + '\\';
    }

    private static string EnsureTrailingBackslash(string path) =>
        path.EndsWith('\\') ? path : path + '\\';

    private static bool SequenceMatchesOrdinalIgnoreCase(
        string[] cached,
        IReadOnlyList<string> candidate)
    {
        if (cached.Length != candidate.Count)
        {
            return false;
        }

        for (int i = 0; i < cached.Length; i++)
        {
            if (!string.Equals(cached[i], candidate[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
