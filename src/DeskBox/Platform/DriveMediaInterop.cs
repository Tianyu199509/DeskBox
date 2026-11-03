using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DeskBox.Platform;

internal enum InboundSourceProtectionKind
{
    None = 0,
    ExternalRemovableOrNetwork = 1,
    SystemOrSharedProtected = 2,
}

internal enum StorageBusKind : uint
{
    Unknown = 0,
    Scsi = 1,
    Atapi = 2,
    Ata = 3,
    Ieee1394 = 4,
    Ssa = 5,
    Fibre = 6,
    Usb = 7,
    Raid = 8,
    IScsi = 9,
    Sas = 10,
    Sata = 11,
    Sd = 12,
    Mmc = 13,
    Virtual = 14,
    FileBackedVirtual = 15,
    Spaces = 16,
    Nvme = 17,
}

/// <summary>
/// Classifies drag-and-drop source paths by underlying storage medium and
/// system folder protection so default Move-in / FollowWindows-out policies
/// never delete originals from USB drives, external USB SSDs/HDDs, network
/// shares, Public Desktop, Start Menu, or OS installation directories.
/// </summary>
internal static class DriveMediaInterop
{
    private const uint FileShareReadWrite = 0x00000001u | 0x00000002u;
    private const uint OpenExisting = 3u;
    private const uint IoctlStorageQueryProperty = 0x002D1400u;
    private const long DriveCacheTtlMs = 30_000;

    private static readonly ConcurrentDictionary<char, CachedDriveClassification> s_driveCache = new();

    private static readonly Lazy<string[]> s_protectedSystemRoots = new(BuildProtectedSystemRoots);
    private static readonly Lazy<string[]> s_protectedDragOutRoots = new(BuildProtectedDragOutRoots);
    private static readonly Lazy<string> s_localAppDataPrefix = new(static () =>
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(localAppData) + '\\';
    });
    private static readonly Lazy<string> s_personalDesktopNormalized = new(static () =>
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktop))
        {
            return string.Empty;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(StripExtendedPathPrefix(desktop.Trim())));
        }
        catch
        {
            return string.Empty;
        }
    });
    private static readonly Lazy<string[]> s_userLooseDropRoots = new(BuildUserLooseDropRoots);
    private static readonly Lazy<string> s_defaultDeskBoxRoot = new(static () =>
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(Path.Combine(userProfile, "DeskBox"));
    });
    private static readonly Lazy<string> s_tempPathNormalized = new(static () =>
    {
        string temp = Path.GetTempPath();
        return string.IsNullOrWhiteSpace(temp)
            ? string.Empty
            : Path.TrimEndingDirectorySeparator(temp);
    });

    private readonly record struct CachedDriveClassification(bool IsExternalMedia, long ExpiresTick64);

    [StructLayout(LayoutKind.Sequential)]
    private struct StoragePropertyQuery
    {
        public int PropertyId;
        public int QueryType;
        public uint AdditionalParameters;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool DeviceIoControl(
        IntPtr hDevice,
        uint dwIoControlCode,
        ref StoragePropertyQuery lpInBuffer,
        uint nInBufferSize,
        byte* lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern unsafe uint QueryDosDeviceW(
        string lpDeviceName,
        char* lpTargetPath,
        uint ucchMax);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string lpFileName);

    private const uint InvalidFileAttributes = 0xFFFFFFFFu;
    // Note: FILE_ATTRIBUTE_UNPINNED (0x00100000) is intentionally excluded —
    // OneDrive sets UNPINNED on fully hydrated local files that were once
    // "Free up space"-eligible as well as empty KFM directories. Only genuine
    // recall/offline markers indicate unhydrated cloud placeholders.
    internal const uint UnhydratedCloudAttributeMask =
        0x00001000u | // FILE_ATTRIBUTE_OFFLINE
        0x00040000u | // FILE_ATTRIBUTE_RECALL_ON_OPEN
        0x00400000u;  // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS

    internal static string StripExtendedPathPrefix(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[8..];
        }

        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return path[4..];
        }

        return path;
    }

    internal static bool IsExternalBusKind(StorageBusKind busKind) =>
        busKind is StorageBusKind.Usb or
            StorageBusKind.Sd or
            StorageBusKind.Mmc or
            StorageBusKind.Ieee1394 or
            StorageBusKind.Virtual or
            StorageBusKind.FileBackedVirtual;

    /// <summary>
    /// Returns whether <paramref name="fullPath"/> sits inside a chat client's
    /// received-file / message-attachment storage root (WeChat 3.x/4.x, WeCom,
    /// QQ / NTQQ), where moving originals out breaks historical message links.
    /// </summary>
    internal static bool IsProtectedApplicationCachePath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        string normalized = StripExtendedPathPrefix(fullPath.Trim()).Replace('/', '\\');
        if (normalized.Contains(@"\xwechat_files\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.Contains(@"\WeChat Files\", StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains(@"\FileStorage\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.Contains(@"\WXWork\", StringComparison.OrdinalIgnoreCase) &&
            (normalized.Contains(@"\FileStorage\", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains(@"\Cache\File\", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains(@"\Cache\Image\", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains(@"\Cache\Video\", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (normalized.Contains(@"\nt_qq\nt_data\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (normalized.Contains(@"\Tencent Files\", StringComparison.OrdinalIgnoreCase) &&
            (normalized.Contains(@"\FileRecv\", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains(@"\Image\", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Feishu / Lark chat attachment & image cache
        if ((normalized.Contains(@"\LarkShell\", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains(@"\Feishu\", StringComparison.OrdinalIgnoreCase)) &&
            (normalized.Contains(@"\sdk_storage\", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains(@"\FileStorage\", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains(@"\Downloads\", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains(@"\Image\", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // DingTalk chat attachment & image cache
        if (normalized.Contains(@"\DingTalk\", StringComparison.OrdinalIgnoreCase) &&
            (normalized.Contains(@"\StandardStorage\", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains(@"\FileRecv\", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains(@"\Image\", StringComparison.OrdinalIgnoreCase) ||
             normalized.Contains(@"\Cache\", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Telegram Desktop tdata internal cache
        if (normalized.Contains(@"\Telegram Desktop\tdata\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    internal static bool HasUnhydratedCloudFileAttributes(uint attributes) =>
        attributes != InvalidFileAttributes &&
        (attributes & UnhydratedCloudAttributeMask) != 0;

    internal static bool IsPathUnderPersonalUserDesktop(
        string? fullPath,
        string? userDesktopOverride = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        if (userDesktopOverride is null)
        {
            string cachedDesktop = s_personalDesktopNormalized.Value;
            if (string.IsNullOrEmpty(cachedDesktop))
            {
                return false;
            }

            try
            {
                string normalizedCandidate = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(StripExtendedPathPrefix(fullPath.Trim())));
                if (string.Equals(normalizedCandidate, cachedDesktop, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return normalizedCandidate.StartsWith(
                    cachedDesktop + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        string desktop = userDesktopOverride;
        if (string.IsNullOrWhiteSpace(desktop))
        {
            return false;
        }

        try
        {
            string normalizedDesktop = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(StripExtendedPathPrefix(desktop.Trim())));
            string normalizedCandidate = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(StripExtendedPathPrefix(fullPath.Trim())));
            if (string.Equals(normalizedCandidate, normalizedDesktop, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return normalizedCandidate.StartsWith(
                normalizedDesktop + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsUnhydratedCloudPlaceholderPath(
        string? fullPath,
        string? userDesktopOverride = null,
        Func<string, uint>? fileAttributesProbe = null)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        string stripped = StripExtendedPathPrefix(fullPath.Trim());
        if (stripped.Length < 2 || stripped.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return false;
        }

        // Decision 3A: files on the user's personal Desktop (including OneDrive
        // KFM Desktop backup) align with native local Desktop Move semantics on
        // the same volume rather than being forced to Copy as an external drive.
        if (IsPathUnderPersonalUserDesktop(stripped, userDesktopOverride))
        {
            return false;
        }

        uint attributes = fileAttributesProbe is not null
            ? fileAttributesProbe(stripped)
            : GetFileAttributesW(StorageMigrationNativeMethods.Extended(stripped));
        return HasUnhydratedCloudFileAttributes(attributes);
    }

    /// <summary>
    /// Returns whether any path in <paramref name="sourcePaths"/> lives under
    /// Start Menu, Public Desktop, or OS/Program Files roots where advertising
    /// OLE <c>Move</c> on drag-out would remove shared/system shortcuts or
    /// trigger UAC failures.
    /// </summary>
    internal static bool IsProtectedDragOutSource(IEnumerable<string>? sourcePaths)
    {
        if (sourcePaths is null)
        {
            return false;
        }

        string[] roots = s_protectedDragOutRoots.Value;
        if (roots.Length == 0)
        {
            return false;
        }

        foreach (string rawPath in sourcePaths)
        {
            string substResolved = PathOriginClassifier.ResolveSubstPath(rawPath);
            string candidate = string.IsNullOrWhiteSpace(substResolved) ? rawPath : substResolved;
            if (!TryNormalizeStandardPath(candidate, out string normalized))
            {
                continue;
            }

            if (IsUnderAnyRoot(normalized, roots) ||
                IsDriveProgramInstallPath(normalized) ||
                IsMoveProtectedExecutableOrInstallPath(normalized))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Classifies inbound drop sources so default <c>Move</c> mode can
    /// automatically preserve originals on external USB/SD/1394 drives,
    /// network/UNC shares, optical media, Public Desktop, Start Menu, and OS
    /// installation directories unless the user explicitly overrides with Shift.
    /// </summary>
    internal static InboundSourceProtectionKind ClassifyInboundDropSources(
        IEnumerable<string>? sourcePaths,
        Func<char, DriveType>? driveTypeProbe = null,
        Func<char, (StorageBusKind BusKind, bool RemovableMedia)>? busProbe = null)
    {
        if (sourcePaths is null)
        {
            return InboundSourceProtectionKind.None;
        }

        InboundSourceProtectionKind result = InboundSourceProtectionKind.None;
        string[] systemRoots = s_protectedSystemRoots.Value;

        foreach (string rawPath in sourcePaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string stripped = StripExtendedPathPrefix(rawPath.Trim());
            if (stripped.Length == 0)
            {
                continue;
            }

            if (stripped.StartsWith(@"\\.\"))
            {
                return InboundSourceProtectionKind.SystemOrSharedProtected;
            }

            if (stripped.StartsWith(@"\\", StringComparison.Ordinal))
            {
                result = InboundSourceProtectionKind.ExternalRemovableOrNetwork;
                continue;
            }

            if (!TryNormalizeStandardPath(stripped, out string fullPath))
            {
                continue;
            }

            if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                result = InboundSourceProtectionKind.ExternalRemovableOrNetwork;
                continue;
            }

            if (IsUnderAnyRoot(fullPath, systemRoots) ||
                IsDriveProgramInstallPath(fullPath) ||
                IsMoveProtectedExecutableOrInstallPath(fullPath))
            {
                return InboundSourceProtectionKind.SystemOrSharedProtected;
            }

            if (IsProtectedApplicationCachePath(fullPath))
            {
                result = InboundSourceProtectionKind.ExternalRemovableOrNetwork;
                continue;
            }

            if (driveTypeProbe is null &&
                busProbe is null &&
                IsUnhydratedCloudPlaceholderPath(fullPath))
            {
                result = InboundSourceProtectionKind.ExternalRemovableOrNetwork;
                continue;
            }

            if (fullPath.Length >= 2 &&
                char.IsAsciiLetter(fullPath[0]) &&
                fullPath[1] == ':')
            {
                char driveLetter = char.ToUpperInvariant(fullPath[0]);
                bool isExternalDrive = driveTypeProbe is not null || busProbe is not null
                    ? EvaluateDriveExternalUncached(driveLetter, driveTypeProbe, busProbe)
                    : IsDriveExternalCached(driveLetter);
                if (isExternalDrive)
                {
                    result = InboundSourceProtectionKind.ExternalRemovableOrNetwork;
                }
            }
        }

        return result;
    }

    internal static bool ContainsProtectedInboundCopySource(IEnumerable<string>? sourcePaths) =>
        ClassifyInboundDropSources(sourcePaths) != InboundSourceProtectionKind.None;

    private static bool IsDriveExternalCached(char upperDriveLetter)
    {
        long now = Environment.TickCount64;
        if (s_driveCache.TryGetValue(upperDriveLetter, out CachedDriveClassification cached) &&
            now < cached.ExpiresTick64)
        {
            return cached.IsExternalMedia;
        }

        bool isExternal = EvaluateDriveExternalUncached(
            upperDriveLetter,
            driveTypeProbe: null,
            busProbe: null);
        s_driveCache[upperDriveLetter] = new CachedDriveClassification(
            isExternal,
            now + DriveCacheTtlMs);
        return isExternal;
    }

    private static bool EvaluateDriveExternalUncached(
        char upperDriveLetter,
        Func<char, DriveType>? driveTypeProbe,
        Func<char, (StorageBusKind BusKind, bool RemovableMedia)>? busProbe)
    {
        if (driveTypeProbe is null &&
            busProbe is null &&
            upperDriveLetter == GetSystemBootDriveLetter())
        {
            return false;
        }

        DriveType driveType;
        try
        {
            driveType = driveTypeProbe is not null
                ? driveTypeProbe(upperDriveLetter)
                : new DriveInfo($"{upperDriveLetter}:\\").DriveType;
        }
        catch
        {
            driveType = DriveType.Unknown;
        }

        if (driveType is DriveType.Removable or DriveType.Network or DriveType.CDRom)
        {
            return true;
        }

        if (driveType != DriveType.Fixed)
        {
            return false;
        }

        (StorageBusKind busKind, bool removableMedia) = busProbe is not null
            ? busProbe(upperDriveLetter)
            : QueryDriveStorageBus(upperDriveLetter);
        if (removableMedia || IsExternalBusKind(busKind))
        {
            return true;
        }

        return busKind == StorageBusKind.Unknown &&
            busProbe is null &&
            IsVirtualFilesystemDosDevice(upperDriveLetter);
    }

    private static char GetSystemBootDriveLetter()
    {
        try
        {
            string sysDir = Environment.SystemDirectory;
            if (sysDir.Length >= 2 &&
                char.IsAsciiLetter(sysDir[0]) &&
                sysDir[1] == ':')
            {
                return char.ToUpperInvariant(sysDir[0]);
            }
        }
        catch
        {
        }

        return '\0';
    }

    internal static bool IsVirtualFilesystemTargetDevicePath(string? dosTarget)
    {
        if (string.IsNullOrWhiteSpace(dosTarget))
        {
            return false;
        }

        string trimmed = dosTarget.Trim();
        // Local subst aliases (\??\C:\...) map onto a local directory rather than a virtual FS.
        if (trimmed.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (trimmed.StartsWith(@"\Device\HarddiskVolume", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith(@"\Device\HarddiskDmVolumes\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return trimmed.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase);
    }

    internal static unsafe string? QueryDosDeviceTarget(char upperDriveLetter)
    {
        char* buffer = stackalloc char[512];
        uint chars = QueryDosDeviceW($"{upperDriveLetter}:", buffer, 512);
        if (chars == 0)
        {
            return null;
        }

        string target = new string(buffer);
        return string.IsNullOrWhiteSpace(target) ? null : target;
    }

    private static bool IsVirtualFilesystemDosDevice(char upperDriveLetter)
    {
        return IsVirtualFilesystemTargetDevicePath(QueryDosDeviceTarget(upperDriveLetter));
    }

    internal static bool IsVolumeGuidExternalMedia(string? volumeGuidPath)
    {
        if (string.IsNullOrWhiteSpace(volumeGuidPath))
        {
            return false;
        }

        string devicePath = volumeGuidPath.Trim().TrimEnd('\\', '/');
        if (!devicePath.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) &&
            !devicePath.StartsWith(@"\\.\Volume{", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        (StorageBusKind busKind, bool removableMedia) = QueryDevicePathStorageBus(devicePath);
        return removableMedia || IsExternalBusKind(busKind);
    }

    private static (StorageBusKind BusKind, bool RemovableMedia) QueryDriveStorageBus(
        char upperDriveLetter)
    {
        return QueryDevicePathStorageBus($"\\\\.\\{upperDriveLetter}:");
    }

    private static unsafe (StorageBusKind BusKind, bool RemovableMedia) QueryDevicePathStorageBus(
        string devicePath)
    {
        IntPtr handle = CreateFileW(
            devicePath,
            dwDesiredAccess: 0,
            FileShareReadWrite,
            IntPtr.Zero,
            OpenExisting,
            dwFlagsAndAttributes: 0,
            IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return (StorageBusKind.Unknown, false);
        }

        try
        {
            var query = new StoragePropertyQuery
            {
                PropertyId = 0, // StorageDeviceProperty
                QueryType = 0,  // PropertyStandardQuery
                AdditionalParameters = 0,
            };
            byte* buffer = stackalloc byte[128];
            if (!DeviceIoControl(
                    handle,
                    IoctlStorageQueryProperty,
                    ref query,
                    (uint)sizeof(StoragePropertyQuery),
                    buffer,
                    128,
                    out uint bytesReturned,
                    IntPtr.Zero) ||
                bytesReturned < 32)
            {
                return (StorageBusKind.Unknown, false);
            }

            // STORAGE_DEVICE_DESCRIPTOR layout:
            // [0..3]   ULONG Version
            // [4..7]   ULONG Size
            // [10]     BOOLEAN RemovableMedia
            // [28..31] STORAGE_BUS_TYPE BusType
            uint descriptorVersion = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(buffer);
            uint descriptorSize = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(buffer + 4);
            if (descriptorVersion < 28 || descriptorSize < 32)
            {
                return (StorageBusKind.Unknown, false);
            }

            bool removableMedia = buffer[10] != 0;
            uint rawBusType = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<uint>(buffer + 28);
            return ((StorageBusKind)rawBusType, removableMedia);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool TryNormalizeStandardPath(string rawPath, out string fullPath)
    {
        fullPath = string.Empty;
        string stripped = StripExtendedPathPrefix(rawPath.Trim());
        if (string.IsNullOrWhiteSpace(stripped))
        {
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(stripped);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUnderAnyRoot(string fullPath, string[] rootsWithTrailingSep)
    {
        foreach (string root in rootsWithTrailingSep)
        {
            if (fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                (fullPath.Length == root.Length - 1 &&
                 root.AsSpan(0, root.Length - 1).Equals(fullPath.AsSpan(), StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsDriveProgramInstallPath(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        string normalized = StripExtendedPathPrefix(fullPath.Trim()).Replace('/', '\\');
        if (normalized.Length >= 3 &&
            char.IsAsciiLetter(normalized[0]) &&
            normalized[1] == ':' &&
            normalized[2] == '\\')
        {
            ReadOnlySpan<char> afterRoot = normalized.AsSpan(3);
            if (afterRoot.StartsWith(@"Program Files\", StringComparison.OrdinalIgnoreCase) ||
                afterRoot.Equals("Program Files", StringComparison.OrdinalIgnoreCase) ||
                afterRoot.StartsWith(@"Program Files (x86)\", StringComparison.OrdinalIgnoreCase) ||
                afterRoot.Equals("Program Files (x86)", StringComparison.OrdinalIgnoreCase) ||
                afterRoot.StartsWith(@"WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
                afterRoot.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase) ||
                afterRoot.StartsWith(@"XboxGames\", StringComparison.OrdinalIgnoreCase) ||
                afterRoot.StartsWith(@"SteamLibrary\steamapps\common\", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (normalized.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\scoop\apps\", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains(@"\chocolatey\lib\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Squirrel / Electron per-user installs: %LocalAppData%\<App>\app-<ver>\<App>.exe
        string localPrefix = s_localAppDataPrefix.Value;
        if (!string.IsNullOrEmpty(localPrefix) &&
            normalized.StartsWith(localPrefix, StringComparison.OrdinalIgnoreCase) &&
            normalized.Contains(@"\app-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    internal static bool IsMoveProtectedExecutableOrInstallPath(
        string? rawPath,
        Func<string, bool>? appExecLinkProbe = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        string stripped = StripExtendedPathPrefix(rawPath.Trim());
        if (!stripped.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryNormalizeStandardPath(stripped, out string fullPath))
        {
            return false;
        }

        string fileNameNoExt = Path.GetFileNameWithoutExtension(fullPath);
        if (IsLikelyStandaloneInstallerFileName(fileNameNoExt))
        {
            return false;
        }

        if (IsUnderAnyRoot(fullPath, s_protectedSystemRoots.Value) ||
            IsDriveProgramInstallPath(fullPath))
        {
            return true;
        }

        if (appExecLinkProbe is not null)
        {
            return appExecLinkProbe(fullPath);
        }

        return StorageMigrationNativeMethods.IsAppExecutionAliasReparsePoint(fullPath);
    }

    internal static bool IsInstalledOrBundledExecutablePath(
        string? rawPath,
        Func<string, bool>? appExecLinkProbe = null)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return false;
        }

        string stripped = StripExtendedPathPrefix(rawPath.Trim());
        if (!stripped.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!TryNormalizeStandardPath(stripped, out string fullPath))
        {
            return false;
        }

        string fileNameNoExt = Path.GetFileNameWithoutExtension(fullPath);
        if (IsLikelyStandaloneInstallerFileName(fileNameNoExt))
        {
            return false;
        }

        if (IsMoveProtectedExecutableOrInstallPath(fullPath, appExecLinkProbe))
        {
            return true;
        }

        string? parentDir = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(parentDir) ||
            IsUserLooseDropRootDirectory(parentDir) ||
            !Directory.Exists(parentDir))
        {
            return false;
        }

        return DirectoryHasCompanionAppBundleArtifacts(parentDir, fileNameNoExt);
    }

    private static bool IsLikelyStandaloneInstallerFileName(string fileNameNoExt)
    {
        if (string.IsNullOrWhiteSpace(fileNameNoExt))
        {
            return false;
        }

        return fileNameNoExt.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
            fileNameNoExt.EndsWith("setup", StringComparison.OrdinalIgnoreCase) ||
            fileNameNoExt.Contains("_setup", StringComparison.OrdinalIgnoreCase) ||
            fileNameNoExt.Contains("-setup", StringComparison.OrdinalIgnoreCase) ||
            fileNameNoExt.EndsWith("installer", StringComparison.OrdinalIgnoreCase) ||
            fileNameNoExt.Contains("_installer", StringComparison.OrdinalIgnoreCase) ||
            fileNameNoExt.Contains("-installer", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUserLooseDropRootDirectory(string directoryPath)
    {
        string normalized = Path.TrimEndingDirectorySeparator(directoryPath);
        foreach (string root in s_userLooseDropRoots.Value)
        {
            if (string.Equals(normalized, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        string defaultDeskBoxRoot = s_defaultDeskBoxRoot.Value;
        if (!string.IsNullOrEmpty(defaultDeskBoxRoot) &&
            (string.Equals(normalized, defaultDeskBoxRoot, StringComparison.OrdinalIgnoreCase) ||
             normalized.StartsWith(
                 defaultDeskBoxRoot + Path.DirectorySeparatorChar,
                 StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        string tempPath = s_tempPathNormalized.Value;
        if (!string.IsNullOrEmpty(tempPath) &&
            (string.Equals(normalized, tempPath, StringComparison.OrdinalIgnoreCase) ||
             normalized.StartsWith(tempPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static string[] BuildUserLooseDropRoots()
    {
        Environment.SpecialFolder[] looseFolders =
        [
            Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.MyDocuments,
            Environment.SpecialFolder.UserProfile,
        ];
        var list = new List<string>(5);
        foreach (Environment.SpecialFolder sf in looseFolders)
        {
            string p = Environment.GetFolderPath(sf);
            if (!string.IsNullOrWhiteSpace(p))
            {
                list.Add(Path.TrimEndingDirectorySeparator(p));
            }
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            list.Add(Path.TrimEndingDirectorySeparator(Path.Combine(userProfile, "Downloads")));
        }

        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool DirectoryHasCompanionAppBundleArtifacts(
        string parentDir,
        string exeBaseName)
    {
        try
        {
            if (File.Exists(Path.Combine(parentDir, exeBaseName + ".runtimeconfig.json")) ||
                File.Exists(Path.Combine(parentDir, exeBaseName + ".deps.json")) ||
                File.Exists(Path.Combine(parentDir, "resources.pak")) ||
                File.Exists(Path.Combine(parentDir, "chrome_elf.dll")) ||
                File.Exists(Path.Combine(parentDir, "UnityPlayer.dll")) ||
                Directory.Exists(Path.Combine(parentDir, "resources")))
            {
                return true;
            }

            int inspected = 0;
            foreach (string entry in Directory.EnumerateFiles(parentDir))
            {
                if (++inspected > 64)
                {
                    break;
                }

                if (entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    entry.EndsWith(".pak", StringComparison.OrdinalIgnoreCase) ||
                    entry.EndsWith(".asar", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    private static string[] BuildProtectedDragOutRoots() => BuildProtectedCommonRoots();

    private static string[] BuildProtectedSystemRoots() => BuildProtectedCommonRoots();

    private static string[] BuildProtectedCommonRoots()
    {
        Environment.SpecialFolder[] folders =
        [
            Environment.SpecialFolder.StartMenu,
            Environment.SpecialFolder.CommonStartMenu,
            Environment.SpecialFolder.Programs,
            Environment.SpecialFolder.CommonPrograms,
            Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
        ];

        var extraPaths = new List<string>();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            extraPaths.Add(Path.Combine(localAppData, "Programs"));
            extraPaths.Add(Path.Combine(localAppData, "Microsoft", "WindowsApps"));
            extraPaths.Add(Path.Combine(localAppData, "Microsoft", "WinGet", "Packages"));
        }

        string? programW6432 = Environment.GetEnvironmentVariable("ProgramW6432");
        if (!string.IsNullOrWhiteSpace(programW6432))
        {
            extraPaths.Add(programW6432);
        }

        string? programFilesX86Env = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!string.IsNullOrWhiteSpace(programFilesX86Env))
        {
            extraPaths.Add(programFilesX86Env);
        }

        return NormalizeRootPaths(folders.Select(Environment.GetFolderPath).Concat(extraPaths));
    }

    private static string[] NormalizeRootPaths(IEnumerable<string?> rawPaths)
    {
        return rawPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path =>
            {
                try
                {
                    return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) +
                           Path.DirectorySeparatorChar;
                }
                catch
                {
                    return string.Empty;
                }
            })
            .Where(static path => !string.IsNullOrEmpty(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
