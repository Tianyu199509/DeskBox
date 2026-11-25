using System.Runtime.InteropServices;

namespace DeskBox.Platform;

internal readonly record struct DesktopIconPoint(int X, int Y);

internal readonly record struct MovedDesktopItem(
    string SourcePath,
    string DesktopDirectory,
    bool WasDirectory,
    bool IsPublicDesktop = false);

internal enum ShellMoveNotificationPlanKind
{
    SameVolumeFileRename,
    SameVolumeFolderRename,
    DeleteSourceAndCreateDestination,
}

internal readonly record struct GhostPurgePassOutcome(
    IReadOnlyList<string> PurgedCanonicalPaths,
    bool EscalatedRefresh)
{
    public static GhostPurgePassOutcome None { get; } = new([], EscalatedRefresh: false);

    public bool AnyPurged => PurgedCanonicalPaths.Count > 0;
}

/// <summary>
/// Reads and restores Windows Explorer desktop icon coordinates through the
/// documented Shell COM <c>IShellWindows(SWC_DESKTOP) -&gt; IFolderView</c>
/// pipeline. Uses zero cross-process memory allocation or writes into
/// <c>explorer.exe</c> and matches desktop items by canonical parsing path
/// (<c>SHGDN_FORPARSING</c>) so personal and public desktop items as well as
/// same-stem entries (e.g. folder <c>WeChat</c> vs <c>WeChat.lnk</c>) are
/// never confused.
/// </summary>
internal static unsafe partial class DesktopIconPositionInterop
{
    private const uint CoInitApartmentThreaded = 0x2;
    private const uint ClsctxInprocServer = 0x1;
    private const uint ClsctxLocalServer = 0x4;
    private const int CsidlDesktop = 0x0000;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvgioAllView = 0x00000002;
    private const uint ShgdnForParsing = 0x00008000;
    private const uint SvsiPositionItem = 0x00000080;
    private const int ShcneCreate = 0x00000002;
    private const int ShcneDelete = 0x00000004;
    private const int ShcneMkDir = 0x00000008;
    private const int ShcneRmDir = 0x00000010;
    private const int ShcneUpdateDir = 0x00001000;
    private const int ShcneRenameFolder = 0x00020000;
    private const uint ShcnfIdList = 0x0000;
    private const uint ShcnfPathW = 0x0005;
    private const uint ShcnfFlush = 0x1000;
    private const uint ShcnfFlushNoWait = 0x3000;
    private const uint LvmGetEditControl = 0x1000 + 24;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int BoundsTolerancePx = 64;

    private static readonly int[] RestorePollDelaysMs = [80, 140, 220, 320, 450];
    internal static readonly int[] GhostPurgePollDelaysMs = [100, 300, 750];
    internal const int GhostPurgeEscalationFollowUpDelayMs = 600;
    internal const long ShellViewRefreshCooldownMs = 1200;

    private static readonly CoalescedGhostPurgeBatchQueue s_ghostPurgeQueue = new();
    private static int s_activePositionRestoreCount;
    private static long s_lastShellViewRefreshTick64 = long.MinValue;

    private static readonly Guid ClsidShellWindows =
        new("9BA05972-F6A8-11CF-A442-00A0C90A8F39");

    private static readonly Guid IidIShellWindows =
        new("85CB6900-4D95-11CF-960C-0080C7F4EE85");

    private static readonly Guid IidIServiceProvider =
        new("6D5140C1-7436-11CE-8034-00AA006009FA");

    private static readonly Guid SidSTopLevelBrowser =
        new("4C96BE40-915C-11CF-99D3-00AA004AE837");

    private static readonly Guid IidIShellBrowser =
        new("000214E2-0000-0000-C000-000000000046");

    private static readonly Guid IidIFolderView =
        new("CDE725B0-CCC9-4519-917E-325D72FAB4CE");

    private static readonly Guid IidIShellFolder =
        new("000214E6-0000-0000-C000-000000000046");

    private static readonly Guid IidIEnumIdList =
        new("000214F2-0000-0000-C000-000000000046");

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeVariant
    {
        public ushort Vt;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public nint Data1;
        public nint Data2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct StrRetUnion
    {
        [FieldOffset(0)]
        public nint OleStr;

        [FieldOffset(0)]
        public uint Offset;

        [FieldOffset(0)]
        public fixed byte CStr[260];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StrRet
    {
        public uint Type;
        public StrRetUnion Data;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out nint ppv);

    [LibraryImport("ole32.dll")]
    private static partial void CoTaskMemFree(nint pv);

    [LibraryImport("shlwapi.dll", EntryPoint = "StrRetToBufW")]
    private static partial int StrRetToBufW(
        ref StrRet pstr,
        nint pidl,
        char* pszBuf,
        uint cchBuf);

    [LibraryImport(
        "shell32.dll",
        EntryPoint = "SHChangeNotify",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(
        int wEventId,
        uint uFlags,
        string? dwItem1,
        nint dwItem2);

    [LibraryImport(
        "shell32.dll",
        EntryPoint = "SHChangeNotify",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotifyPaths(
        int wEventId,
        uint uFlags,
        string? dwItem1,
        string? dwItem2);

    [LibraryImport("shell32.dll", EntryPoint = "SHChangeNotify")]
    private static partial void SHChangeNotifyPidl(
        int wEventId,
        uint uFlags,
        nint dwItem1,
        nint dwItem2);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetSpecialFolderLocation")]
    private static partial int SHGetSpecialFolderLocation(
        nint hwndReserve,
        int csidl,
        out nint ppidl);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetIDListFromObject")]
    private static partial int SHGetIDListFromObject(
        nint punk,
        out nint ppidl);

    [LibraryImport("shell32.dll", EntryPoint = "ILCombine")]
    private static partial nint ILCombine(
        nint pidl1,
        nint pidl2);

    [LibraryImport(
        "user32.dll",
        EntryPoint = "FindWindowExW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(
        nint hWndParent,
        nint hWndChildAfter,
        string? lpszClass,
        string? lpszWindow);

    [LibraryImport("user32.dll", EntryPoint = "GetClientRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint hWnd, out NativeRect lpRect);

    internal static IReadOnlyDictionary<string, DesktopIconPoint> TryCaptureDesktopPositions(
        IReadOnlyCollection<string> normalizedPaths,
        int timeoutMs = 1500)
    {
        if (normalizedPaths is null || normalizedPaths.Count == 0)
        {
            return new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase);
        }

        var targetSet = new HashSet<string>(normalizedPaths, StringComparer.OrdinalIgnoreCase);
        return RunBoundedSta(
            "DeskBox.DesktopIconCapture",
            timeoutMs,
            () => CaptureOnStaThread(targetSet),
            fallback: new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase));
    }

    internal static IReadOnlySet<string> TryRestoreDesktopPositions(
        IReadOnlyDictionary<string, DesktopIconPoint> desiredPositionsByPath,
        int timeoutMs = 2200)
    {
        if (desiredPositionsByPath is null || desiredPositionsByPath.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var snapshot = new Dictionary<string, DesktopIconPoint>(
            desiredPositionsByPath,
            StringComparer.OrdinalIgnoreCase);
        return RunBoundedSta(
            "DeskBox.DesktopIconRestore",
            timeoutMs,
            () => RestoreOnStaThread(snapshot, timeoutMs),
            fallback: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    internal static bool IsPointWithinViewBounds(
        DesktopIconPoint point,
        int left,
        int top,
        int right,
        int bottom)
    {
        if (right <= left || bottom <= top)
        {
            return true;
        }

        return point.X >= left - BoundsTolerancePx &&
               point.X <= right + BoundsTolerancePx &&
               point.Y >= top - BoundsTolerancePx &&
               point.Y <= bottom + BoundsTolerancePx;
    }

    internal static ShellMoveNotificationPlanKind ClassifyShellMoveNotification(
        string? oldPath,
        string? newPath,
        bool isDirectory,
        string? personalDesktopPath,
        string? publicDesktopPath,
        Func<string, string?>? finalPathResolver = null)
    {
        string? normalizedOld = TryNormalizeEntryPath(oldPath);
        string? normalizedNew = TryNormalizeEntryPath(newPath);
        if (normalizedOld is null || normalizedNew is null)
        {
            return isDirectory
                ? ShellMoveNotificationPlanKind.SameVolumeFolderRename
                : ShellMoveNotificationPlanKind.SameVolumeFileRename;
        }

        if (FilterMovedDesktopItems(
                [(normalizedOld, normalizedNew, isDirectory)],
                personalDesktopPath,
                publicDesktopPath,
                finalPathResolver).Count > 0)
        {
            return ShellMoveNotificationPlanKind.DeleteSourceAndCreateDestination;
        }

        if (!AreSameVolumeRoot(normalizedOld, normalizedNew, finalPathResolver))
        {
            return ShellMoveNotificationPlanKind.DeleteSourceAndCreateDestination;
        }

        return isDirectory
            ? ShellMoveNotificationPlanKind.SameVolumeFolderRename
            : ShellMoveNotificationPlanKind.SameVolumeFileRename;
    }

    internal static bool TryNotifyShellItemMoved(string oldPath, string newPath)
    {
        bool isDirectory = !string.IsNullOrWhiteSpace(newPath) && Directory.Exists(newPath);
        string personalDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        ShellMoveNotificationPlanKind plan = ClassifyShellMoveNotification(
            oldPath,
            newPath,
            isDirectory,
            personalDesktop,
            publicDesktop);
        switch (plan)
        {
            case ShellMoveNotificationPlanKind.DeleteSourceAndCreateDestination:
                SHChangeNotify(
                    isDirectory ? ShcneRmDir : ShcneDelete,
                    ShcnfPathW | ShcnfFlushNoWait,
                    oldPath,
                    0);
                SHChangeNotify(
                    isDirectory ? ShcneMkDir : ShcneCreate,
                    ShcnfPathW | ShcnfFlushNoWait,
                    newPath,
                    0);
                return true;

            case ShellMoveNotificationPlanKind.SameVolumeFolderRename:
                SHChangeNotifyPaths(
                    ShcneRenameFolder,
                    ShcnfPathW | ShcnfFlushNoWait,
                    oldPath,
                    newPath);
                return true;

            default:
                return false;
        }
    }

    internal static void NotifyDesktopNamespaceIfMatch(string? directory)
    {
        if (IsRecognizedDesktopDirectory(directory))
        {
            NotifyDesktopNamespaceUpdated(flushBlocking: false);
        }
    }

    internal static void NotifyDesktopNamespaceUpdated(bool flushBlocking = false)
    {
        _ = flushBlocking;
        if (SHGetSpecialFolderLocation(0, CsidlDesktop, out nint desktopRootPidl) != 0 ||
            desktopRootPidl == 0)
        {
            return;
        }

        try
        {
            // Always use SHCNF_FLUSHNOWAIT (0x3000): plain SHCNF_FLUSH (0x1000)
            // blocks synchronously on every SHChangeNotifyRegister window in the
            // session and can stall for HungAppTimeout (~5s) if any third-party
            // shell/cloud tray listener is unresponsive.
            SHChangeNotifyPidl(ShcneUpdateDir, ShcnfIdList | ShcnfFlushNoWait, desktopRootPidl, 0);
        }
        catch
        {
        }
        finally
        {
            CoTaskMemFree(desktopRootPidl);
        }
    }

    internal static bool IsRecognizedDesktopDirectory(
        string? directoryPath,
        string? personalDesktopPath = null,
        string? publicDesktopPath = null,
        Func<string, string?>? finalPathResolver = null)
    {
        string? normalized = TryNormalizeDirectoryPath(directoryPath);
        if (normalized is null)
        {
            return false;
        }

        string effectivePersonal = personalDesktopPath ??
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string effectivePublic = publicDesktopPath ??
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        HashSet<string> personalAliases = BuildDirectoryAliasSet(effectivePersonal, finalPathResolver);
        HashSet<string> publicAliases = BuildDirectoryAliasSet(effectivePublic, finalPathResolver);
        if (personalAliases.Contains(normalized) || publicAliases.Contains(normalized))
        {
            return true;
        }

        string? resolvedCandidate = (finalPathResolver ?? TryResolveFinalDirectoryPath)(normalized);
        return resolvedCandidate is not null &&
               (personalAliases.Contains(resolvedCandidate) || publicAliases.Contains(resolvedCandidate));
    }

    internal static bool IsRealDesktopInlineRenameActive(string? watchedDesktopPath)
    {
        if (!IsRecognizedDesktopDirectory(watchedDesktopPath))
        {
            return false;
        }

        try
        {
            nint listViewHwnd = FindVerifiedDesktopListViewHwnd();
            return listViewHwnd != 0 && IsListViewInlineRenameActive(listViewHwnd);
        }
        catch
        {
            return false;
        }
    }

    internal static IReadOnlyList<MovedDesktopItem> FilterMovedDesktopItems(
        IEnumerable<(string SourcePath, string DestinationPath, bool WasDirectory)> movedOperations,
        string? personalDesktopPath,
        string? publicDesktopPath,
        Func<string, string?>? finalPathResolver = null)
    {
        HashSet<string> personalAliases = BuildDirectoryAliasSet(personalDesktopPath, finalPathResolver);
        HashSet<string> publicAliases = BuildDirectoryAliasSet(publicDesktopPath, finalPathResolver);
        if (personalAliases.Count == 0 && publicAliases.Count == 0)
        {
            return [];
        }

        var matched = new List<MovedDesktopItem>();
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string sourcePath, string destinationPath, bool wasDirectory) in movedOperations)
        {
            string? normalizedDestination = TryNormalizeEntryPath(destinationPath);
            if (normalizedDestination is null ||
                !TryMatchDesktopSource(
                    sourcePath,
                    personalAliases,
                    publicAliases,
                    finalPathResolver,
                    out string normalizedSource,
                    out string sourceParent,
                    out bool isPublicDesktopSource) ||
                !seenSources.Add(normalizedSource))
            {
                continue;
            }

            string? destinationParent = TryNormalizeDirectoryPath(Path.GetDirectoryName(normalizedDestination));
            bool staysOnDesktop = false;
            if (destinationParent is not null)
            {
                staysOnDesktop = personalAliases.Contains(destinationParent) ||
                                 publicAliases.Contains(destinationParent);
                if (!staysOnDesktop)
                {
                    string? resolvedDestinationParent =
                        (finalPathResolver ?? TryResolveFinalDirectoryPath)(destinationParent);
                    if (resolvedDestinationParent is not null)
                    {
                        staysOnDesktop = personalAliases.Contains(resolvedDestinationParent) ||
                                         publicAliases.Contains(resolvedDestinationParent);
                    }
                }
            }

            if (staysOnDesktop)
            {
                continue;
            }

            matched.Add(new MovedDesktopItem(
                normalizedSource,
                sourceParent,
                wasDirectory,
                IsPublicDesktop: isPublicDesktopSource));
        }

        return matched;
    }

    internal static IReadOnlyList<MovedDesktopItem> FilterRemovedDesktopItems(
        IEnumerable<(string SourcePath, bool WasDirectory)> removedOperations,
        string? personalDesktopPath,
        string? publicDesktopPath,
        Func<string, string?>? finalPathResolver = null)
    {
        HashSet<string> personalAliases = BuildDirectoryAliasSet(personalDesktopPath, finalPathResolver);
        HashSet<string> publicAliases = BuildDirectoryAliasSet(publicDesktopPath, finalPathResolver);
        if (personalAliases.Count == 0 && publicAliases.Count == 0)
        {
            return [];
        }

        var matched = new List<MovedDesktopItem>();
        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string sourcePath, bool wasDirectory) in removedOperations)
        {
            if (!TryMatchDesktopSource(
                    sourcePath,
                    personalAliases,
                    publicAliases,
                    finalPathResolver,
                    out string normalizedSource,
                    out string sourceParent,
                    out bool isPublicDesktopSource) ||
                !seenSources.Add(normalizedSource))
            {
                continue;
            }

            matched.Add(new MovedDesktopItem(
                normalizedSource,
                sourceParent,
                wasDirectory,
                IsPublicDesktop: isPublicDesktopSource));
        }

        return matched;
    }

    private static bool TryMatchDesktopSource(
        string? sourcePath,
        HashSet<string> personalAliases,
        HashSet<string> publicAliases,
        Func<string, string?>? finalPathResolver,
        out string normalizedSource,
        out string sourceParent,
        out bool isPublicDesktopSource)
    {
        normalizedSource = string.Empty;
        sourceParent = string.Empty;
        isPublicDesktopSource = false;

        string? candidateSource = TryNormalizeEntryPath(sourcePath);
        if (candidateSource is null)
        {
            return false;
        }

        string? candidateParent = TryNormalizeDirectoryPath(Path.GetDirectoryName(candidateSource));
        if (candidateParent is null)
        {
            return false;
        }

        bool isPersonal = personalAliases.Contains(candidateParent);
        bool isPublic = !isPersonal && publicAliases.Contains(candidateParent);
        if (!isPersonal && !isPublic)
        {
            string? resolvedParent = (finalPathResolver ?? TryResolveFinalDirectoryPath)(candidateParent);
            if (resolvedParent is not null)
            {
                isPersonal = personalAliases.Contains(resolvedParent);
                isPublic = !isPersonal && publicAliases.Contains(resolvedParent);
            }
        }

        if (!isPersonal && !isPublic)
        {
            return false;
        }

        normalizedSource = candidateSource;
        sourceParent = candidateParent;
        isPublicDesktopSource = isPublic;
        return true;
    }

    /// <summary>
    /// Flushes explicit Shell delete + directory-update notifications (including
    /// the virtual <c>CSIDL_DESKTOP</c> root) when items physically move off the
    /// Windows Desktop, and enqueues them into a single coalesced background
    /// <c>IFolderView</c> sweep worker across Explorer's async enumeration/thumbnail
    /// window, escalating to <c>IShellView::Refresh()</c> if a stale ghost child
    /// PIDL survives <c>SHCNE_DELETE</c>.
    /// </summary>
    internal static void NotifyMovedOffRealDesktopIfNeeded(
        IEnumerable<(string SourcePath, string DestinationPath, bool WasDirectory)> movedOperations)
    {
        if (movedOperations is null)
        {
            return;
        }

        string personalDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        HashSet<string> personalAliases = BuildDirectoryAliasSet(personalDesktop, finalPathResolver: null);
        HashSet<string> publicAliases = BuildDirectoryAliasSet(publicDesktop, finalPathResolver: null);
        IReadOnlyList<MovedDesktopItem> candidates = FilterMovedDesktopItems(
            movedOperations,
            personalDesktop,
            publicDesktop);
        DispatchMissingDesktopItems(candidates, personalAliases, publicAliases);
    }

    internal static void NotifyRemovedFromRealDesktopIfNeeded(
        IEnumerable<(string SourcePath, bool WasDirectory)> removedOperations)
    {
        if (removedOperations is null)
        {
            return;
        }

        string personalDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        HashSet<string> personalAliases = BuildDirectoryAliasSet(personalDesktop, finalPathResolver: null);
        HashSet<string> publicAliases = BuildDirectoryAliasSet(publicDesktop, finalPathResolver: null);
        IReadOnlyList<MovedDesktopItem> candidates = FilterRemovedDesktopItems(
            removedOperations,
            personalDesktop,
            publicDesktop);
        DispatchMissingDesktopItems(candidates, personalAliases, publicAliases);
    }

    internal static void NotifyShellItemDeleted(string? path, bool wasDirectory)
    {
        string? normalized = TryNormalizeEntryPath(path);
        if (normalized is null)
        {
            return;
        }

        string personalDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string publicDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        HashSet<string> personalAliases = BuildDirectoryAliasSet(personalDesktop, finalPathResolver: null);
        HashSet<string> publicAliases = BuildDirectoryAliasSet(publicDesktop, finalPathResolver: null);
        IReadOnlyList<MovedDesktopItem> desktopMatches = FilterRemovedDesktopItems(
            [(normalized, wasDirectory)],
            personalDesktop,
            publicDesktop);
        if (desktopMatches.Count > 0)
        {
            DispatchMissingDesktopItems(desktopMatches, personalAliases, publicAliases);
            return;
        }

        try
        {
            SHChangeNotify(
                wasDirectory ? ShcneRmDir : ShcneDelete,
                ShcnfPathW | ShcnfFlushNoWait,
                normalized,
                0);
            string? parentDir = TryNormalizeDirectoryPath(Path.GetDirectoryName(normalized));
            if (parentDir is not null && Directory.Exists(parentDir))
            {
                SHChangeNotify(ShcneUpdateDir, ShcnfPathW | ShcnfFlushNoWait, parentDir, 0);
            }
        }
        catch
        {
        }
    }

    private static void DispatchMissingDesktopItems(
        IReadOnlyList<MovedDesktopItem> candidates,
        HashSet<string> personalAliases,
        HashSet<string> publicAliases)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        var missingItems = new List<MovedDesktopItem>(candidates.Count);
        var affectedDesktopDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (MovedDesktopItem item in candidates)
        {
            if (File.Exists(item.SourcePath) || Directory.Exists(item.SourcePath))
            {
                continue;
            }

            missingItems.Add(item);
            affectedDesktopDirs.Add(item.DesktopDirectory);
            try
            {
                SHChangeNotify(
                    item.WasDirectory ? ShcneRmDir : ShcneDelete,
                    ShcnfPathW | ShcnfFlushNoWait,
                    item.SourcePath,
                    0);
            }
            catch
            {
            }
        }

        if (missingItems.Count == 0)
        {
            return;
        }

        foreach (string desktopDir in affectedDesktopDirs)
        {
            try
            {
                if (Directory.Exists(desktopDir))
                {
                    SHChangeNotify(ShcneUpdateDir, ShcnfPathW | ShcnfFlushNoWait, desktopDir, 0);
                }
            }
            catch
            {
            }
        }

        NotifyDesktopNamespaceUpdated();
        if (s_ghostPurgeQueue.Enqueue(missingItems, personalAliases, publicAliases))
        {
            _ = Task.Run(RunCoalescedGhostPurgeWorker);
        }
    }

    internal static bool ShouldEscalateToShellViewRefresh(
        IEnumerable<string> currentGhostCanonicalPaths,
        IReadOnlySet<string> previouslyPurgedCanonicalPaths)
    {
        if (previouslyPurgedCanonicalPaths.Count == 0)
        {
            return false;
        }

        foreach (string path in currentGhostCanonicalPaths)
        {
            if (previouslyPurgedCanonicalPaths.Contains(path))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool CanEscalateShellViewRefresh(
        bool inlineRenameActive,
        int activePositionRestoreCount,
        long nowTick64,
        long lastRefreshTick64,
        long cooldownMs = ShellViewRefreshCooldownMs)
    {
        if (inlineRenameActive || activePositionRestoreCount > 0)
        {
            return false;
        }

        if (lastRefreshTick64 != long.MinValue &&
            nowTick64 - lastRefreshTick64 < cooldownMs)
        {
            return false;
        }

        return true;
    }

    internal static void ExecuteGhostPurgeSchedule(
        IReadOnlyList<int> pollDelaysMs,
        int escalationFollowUpDelayMs,
        Action<int> sleep,
        Func<bool> hasAnyMissingSource,
        Func<IReadOnlySet<string>, GhostPurgePassOutcome> runPass)
    {
        var previouslyPurged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        GhostPurgePassOutcome lastOutcome = GhostPurgePassOutcome.None;

        for (int i = 0; i < pollDelaysMs.Count; i++)
        {
            sleep(pollDelaysMs[i]);
            if (!hasAnyMissingSource())
            {
                return;
            }

            // Never exit early on an empty pass: Explorer's CDefView ~250-500 ms
            // batch timer or out-of-process thumbnail/ADS callbacks can commit a
            // late ghost entry after the initial 100 ms probe saw a clean view.
            lastOutcome = runPass(previouslyPurged);
            foreach (string path in lastOutcome.PurgedCanonicalPaths)
            {
                previouslyPurged.Add(path);
            }
        }

        // If a ghost only surfaced on the final scheduled pass (or inline rename /
        // active icon restore / refresh cooldown temporarily deferred escalation),
        // run one follow-up pass so stubborn late ghosts still receive Refresh.
        if (lastOutcome.AnyPurged &&
            !lastOutcome.EscalatedRefresh &&
            escalationFollowUpDelayMs > 0)
        {
            sleep(escalationFollowUpDelayMs);
            if (hasAnyMissingSource())
            {
                _ = runPass(previouslyPurged);
            }
        }
    }

    private static void RunCoalescedGhostPurgeWorker()
    {
        static bool PathCurrentlyExists(string path) =>
            File.Exists(path) || Directory.Exists(path);

        try
        {
            while (true)
            {
                int cycleGeneration = s_ghostPurgeQueue.BeginCycle();
                ExecuteGhostPurgeSchedule(
                    GhostPurgePollDelaysMs,
                    GhostPurgeEscalationFollowUpDelayMs,
                    Thread.Sleep,
                    () => s_ghostPurgeQueue.HasPendingMissing(PathCurrentlyExists),
                    previouslyPurged =>
                    {
                        (MovedDesktopItem[] stillMissing,
                         HashSet<string> personalAliases,
                         HashSet<string> publicAliases) =
                            s_ghostPurgeQueue.SnapshotPendingMissing(PathCurrentlyExists);
                        if (stillMissing.Length == 0)
                        {
                            return GhostPurgePassOutcome.None;
                        }

                        var purgedSnapshot = new HashSet<string>(
                            previouslyPurged,
                            StringComparer.OrdinalIgnoreCase);
                        return RunBoundedSta(
                            "DeskBox.DesktopGhostPurge",
                            timeoutMs: 1400,
                            () => PurgeGhostDesktopItemsOnStaThread(
                                stillMissing,
                                personalAliases,
                                publicAliases,
                                purgedSnapshot),
                            fallback: GhostPurgePassOutcome.None);
                    });

                if (!s_ghostPurgeQueue.CompleteCycleAndShouldContinue(
                        cycleGeneration,
                        PathCurrentlyExists))
                {
                    return;
                }
            }
        }
        catch
        {
            s_ghostPurgeQueue.ResetOnFault();
        }
    }

    internal sealed class CoalescedGhostPurgeBatchQueue
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, (MovedDesktopItem Item, int Generation)> _pendingBySource =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _personalAliases = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _publicAliases = new(StringComparer.OrdinalIgnoreCase);
        private int _generation;
        private bool _workerActive;

        public bool Enqueue(
            IReadOnlyList<MovedDesktopItem> items,
            IEnumerable<string> personalAliases,
            IEnumerable<string> publicAliases)
        {
            if (items.Count == 0)
            {
                return false;
            }

            lock (_gate)
            {
                int nextGeneration = ++_generation;
                foreach (MovedDesktopItem item in items)
                {
                    _pendingBySource[item.SourcePath] = (item, nextGeneration);
                }

                _personalAliases.UnionWith(personalAliases);
                _publicAliases.UnionWith(publicAliases);
                if (_workerActive)
                {
                    return false;
                }

                _workerActive = true;
                return true;
            }
        }

        public int BeginCycle()
        {
            lock (_gate)
            {
                return _generation;
            }
        }

        public bool HasPendingMissing(Func<string, bool> pathExists)
        {
            lock (_gate)
            {
                PruneExistingLocked(pathExists);
                return _pendingBySource.Count > 0;
            }
        }

        public (MovedDesktopItem[] MissingItems, HashSet<string> PersonalAliases, HashSet<string> PublicAliases)
            SnapshotPendingMissing(Func<string, bool> pathExists)
        {
            lock (_gate)
            {
                PruneExistingLocked(pathExists);
                if (_pendingBySource.Count == 0)
                {
                    return ([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                }

                MovedDesktopItem[] items = _pendingBySource.Values
                    .Select(static entry => entry.Item)
                    .ToArray();
                return (
                    items,
                    new HashSet<string>(_personalAliases, StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(_publicAliases, StringComparer.OrdinalIgnoreCase));
            }
        }

        public bool CompleteCycleAndShouldContinue(
            int cycleStartGeneration,
            Func<string, bool> pathExists)
        {
            lock (_gate)
            {
                foreach ((string key, (MovedDesktopItem item, int gen)) in _pendingBySource.ToArray())
                {
                    if (gen <= cycleStartGeneration || pathExists(item.SourcePath))
                    {
                        _pendingBySource.Remove(key);
                    }
                }

                if (_pendingBySource.Count > 0)
                {
                    return true;
                }

                _personalAliases.Clear();
                _publicAliases.Clear();
                _workerActive = false;
                return false;
            }
        }

        public void ResetOnFault()
        {
            lock (_gate)
            {
                _pendingBySource.Clear();
                _personalAliases.Clear();
                _publicAliases.Clear();
                _workerActive = false;
            }
        }

        private void PruneExistingLocked(Func<string, bool> pathExists)
        {
            foreach ((string key, (MovedDesktopItem item, _)) in _pendingBySource.ToArray())
            {
                if (pathExists(item.SourcePath))
                {
                    _pendingBySource.Remove(key);
                }
            }
        }
    }

    internal static Dictionary<string, MovedDesktopItem> BuildGhostCandidateLookup(
        IReadOnlyList<MovedDesktopItem> missingItems,
        IReadOnlyCollection<string> personalAliases,
        IReadOnlyCollection<string> publicAliases)
    {
        var byCandidatePath = new Dictionary<string, MovedDesktopItem>(
            missingItems.Count * 2,
            StringComparer.OrdinalIgnoreCase);
        foreach (MovedDesktopItem item in missingItems)
        {
            if (File.Exists(item.SourcePath) || Directory.Exists(item.SourcePath))
            {
                continue;
            }

            byCandidatePath[item.SourcePath] = item;
            string fileName = Path.GetFileName(item.SourcePath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            IReadOnlyCollection<string> scopeAliases = item.IsPublicDesktop
                ? publicAliases
                : personalAliases;
            foreach (string aliasDir in scopeAliases)
            {
                string? aliasCandidate = TryNormalizeEntryPath(Path.Combine(aliasDir, fileName));
                if (aliasCandidate is not null &&
                    !File.Exists(aliasCandidate) &&
                    !Directory.Exists(aliasCandidate))
                {
                    byCandidatePath.TryAdd(aliasCandidate, item);
                }
            }
        }

        return byCandidatePath;
    }

    private static GhostPurgePassOutcome PurgeGhostDesktopItemsOnStaThread(
        IReadOnlyList<MovedDesktopItem> missingItems,
        HashSet<string> personalAliases,
        HashSet<string> publicAliases,
        HashSet<string> previouslyPurgedCanonicalPaths)
    {
        if (missingItems.Count == 0 || !TryAcquireDesktopFolderView(out DesktopViewHandles handles))
        {
            return GhostPurgePassOutcome.None;
        }

        try
        {
            Dictionary<string, MovedDesktopItem> bySourcePath = BuildGhostCandidateLookup(
                missingItems,
                personalAliases,
                publicAliases);
            if (bySourcePath.Count == 0)
            {
                return GhostPurgePassOutcome.None;
            }

            List<(string Path, nint ChildPidl)> ghosts = EnumerateDesktopItems(
                handles.FolderView,
                handles.ShellFolder,
                bySourcePath.Keys);
            if (ghosts.Count == 0)
            {
                return GhostPurgePassOutcome.None;
            }

            nint desktopFolderPidl = 0;
            try
            {
                _ = SHGetIDListFromObject(handles.ShellFolder, out desktopFolderPidl);
                var dirsToRefresh = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var purgedCanonicalPaths = new List<string>(ghosts.Count);

                foreach ((string ghostPath, nint childPidl) in ghosts)
                {
                    if (File.Exists(ghostPath) ||
                        Directory.Exists(ghostPath) ||
                        !bySourcePath.TryGetValue(ghostPath, out MovedDesktopItem info) ||
                        File.Exists(info.SourcePath) ||
                        Directory.Exists(info.SourcePath))
                    {
                        continue;
                    }

                    dirsToRefresh.Add(info.DesktopDirectory);
                    int eventId = info.WasDirectory ? ShcneRmDir : ShcneDelete;
                    if (desktopFolderPidl != 0 && childPidl != 0)
                    {
                        nint fullPidl = ILCombine(desktopFolderPidl, childPidl);
                        if (fullPidl != 0)
                        {
                            try
                            {
                                SHChangeNotifyPidl(eventId, ShcnfIdList | ShcnfFlushNoWait, fullPidl, 0);
                            }
                            finally
                            {
                                CoTaskMemFree(fullPidl);
                            }
                        }
                    }

                    SHChangeNotify(eventId, ShcnfPathW | ShcnfFlushNoWait, ghostPath, 0);
                    if (!string.Equals(ghostPath, info.SourcePath, StringComparison.OrdinalIgnoreCase))
                    {
                        SHChangeNotify(eventId, ShcnfPathW | ShcnfFlushNoWait, info.SourcePath, 0);
                    }

                    purgedCanonicalPaths.Add(info.SourcePath);
                }

                if (purgedCanonicalPaths.Count == 0)
                {
                    return GhostPurgePassOutcome.None;
                }

                if (desktopFolderPidl != 0)
                {
                    SHChangeNotifyPidl(ShcneUpdateDir, ShcnfIdList | ShcnfFlushNoWait, desktopFolderPidl, 0);
                }
                else
                {
                    NotifyDesktopNamespaceUpdated();
                }

                foreach (string desktopDir in dirsToRefresh)
                {
                    if (Directory.Exists(desktopDir))
                    {
                        SHChangeNotify(ShcneUpdateDir, ShcnfPathW | ShcnfFlushNoWait, desktopDir, 0);
                    }
                }

                bool escalatedRefresh = false;
                long nowTick64 = Environment.TickCount64;
                long lastRefreshTick64 = Interlocked.Read(ref s_lastShellViewRefreshTick64);
                if (ShouldEscalateToShellViewRefresh(purgedCanonicalPaths, previouslyPurgedCanonicalPaths) &&
                    CanEscalateShellViewRefresh(
                        IsShellViewInlineRenameActive(handles.ShellView),
                        Volatile.Read(ref s_activePositionRestoreCount),
                        nowTick64,
                        lastRefreshTick64))
                {
                    escalatedRefresh = TryRefreshDesktopShellView(handles.ShellView);
                    if (escalatedRefresh)
                    {
                        Interlocked.Exchange(ref s_lastShellViewRefreshTick64, Environment.TickCount64);
                        App.Log(
                            $"[DesktopIconPosition] Escalated to IShellView::Refresh() after " +
                            $"{purgedCanonicalPaths.Count} ghost desktop item(s) survived SHCNE_DELETE.");
                    }
                }
                else
                {
                    App.Log(
                        $"[DesktopIconPosition] Purged {purgedCanonicalPaths.Count} stale desktop view ghost item(s) after move-off.");
                }

                return new GhostPurgePassOutcome(purgedCanonicalPaths, escalatedRefresh);
            }
            finally
            {
                if (desktopFolderPidl != 0)
                {
                    CoTaskMemFree(desktopFolderPidl);
                }

                FreeChildPidls(ghosts);
            }
        }
        finally
        {
            handles.Release();
        }
    }

    private static bool TryRefreshDesktopShellView(nint shellView)
    {
        if (shellView == 0)
        {
            return false;
        }

        nint* svVtbl = *(nint**)shellView;
        // IShellView::Refresh sits at slot 8 (3 IUnknown + 2 IOleWindow + 3 IShellView).
        var refresh = (delegate* unmanaged[Stdcall]<nint, int>)svVtbl[8];
        return refresh(shellView) >= 0;
    }

    private static bool IsShellViewInlineRenameActive(nint shellView)
    {
        if (shellView == 0)
        {
            return false;
        }

        nint* svVtbl = *(nint**)shellView;
        // IShellView::GetWindow (inherited from IOleWindow) sits at slot 3.
        var getWindow = (delegate* unmanaged[Stdcall]<nint, nint*, int>)svVtbl[3];
        nint defViewHwnd = 0;
        if (getWindow(shellView, &defViewHwnd) < 0 || defViewHwnd == 0)
        {
            return false;
        }

        nint listViewHwnd = FindWindowExW(defViewHwnd, 0, "SysListView32", null);
        return listViewHwnd != 0 && IsListViewInlineRenameActive(listViewHwnd);
    }

    private static bool IsListViewInlineRenameActive(nint listViewHwnd)
    {
        if (listViewHwnd == 0 || !Win32Helper.IsWindowVisible(listViewHwnd))
        {
            return false;
        }

        IntPtr delivered = Win32Helper.SendMessageTimeout(
            listViewHwnd,
            LvmGetEditControl,
            UIntPtr.Zero,
            IntPtr.Zero,
            SmtoAbortIfHung,
            80,
            out UIntPtr editHandleValue);
        if (delivered == IntPtr.Zero || editHandleValue == UIntPtr.Zero)
        {
            return false;
        }

        IntPtr editHwnd = (IntPtr)(nint)editHandleValue;
        return Win32Helper.IsWindowVisible(editHwnd);
    }

    private static nint FindVerifiedDesktopListViewHwnd()
    {
        IntPtr shellWindow = Win32Helper.GetShellWindow();
        uint shellProcessId = 0;
        if (shellWindow != IntPtr.Zero)
        {
            Win32Helper.GetWindowThreadProcessId(shellWindow, out shellProcessId);
        }

        nint foundListView = 0;
        Win32Helper.EnumWindows((hWnd, _) =>
        {
            if (hWnd == IntPtr.Zero || !IsVerifiedDesktopHostRoot(hWnd, shellWindow, shellProcessId))
            {
                return true;
            }

            nint defView = FindDesktopDefViewChild(hWnd);
            if (defView == 0)
            {
                return true;
            }

            nint listView = FindWindowExW(defView, 0, "SysListView32", null);
            if (listView != 0)
            {
                foundListView = listView;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return foundListView;
    }

    private static bool IsVerifiedDesktopHostRoot(
        IntPtr rootHwnd,
        IntPtr shellWindow,
        uint shellProcessId)
    {
        Span<char> classBuffer = stackalloc char[64];
        int length = Win32Helper.GetClassName(rootHwnd, classBuffer);
        if (length <= 0)
        {
            return false;
        }

        ReadOnlySpan<char> className = classBuffer[..length];
        if (!className.Equals("Progman", StringComparison.Ordinal) &&
            !className.Equals("WorkerW", StringComparison.Ordinal))
        {
            return false;
        }

        if (shellWindow == IntPtr.Zero || shellProcessId == 0)
        {
            return true;
        }

        Win32Helper.GetWindowThreadProcessId(rootHwnd, out uint rootProcessId);
        return rootProcessId == 0 || rootProcessId == shellProcessId;
    }

    private static nint FindDesktopDefViewChild(nint hostHwnd)
    {
        nint direct = FindWindowExW(hostHwnd, 0, "SHELLDLL_DefView", null);
        if (direct != 0)
        {
            return direct;
        }

        nint childWorker = FindWindowExW(hostHwnd, 0, "WorkerW", null);
        while (childWorker != 0)
        {
            nint nested = FindWindowExW(childWorker, 0, "SHELLDLL_DefView", null);
            if (nested != 0)
            {
                return nested;
            }

            childWorker = FindWindowExW(hostHwnd, childWorker, "WorkerW", null);
        }

        return 0;
    }

    private static HashSet<string> BuildDirectoryAliasSet(
        string? directoryPath,
        Func<string, string?>? finalPathResolver)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? normalized = TryNormalizeDirectoryPath(directoryPath);
        if (normalized is null)
        {
            return aliases;
        }

        aliases.Add(normalized);
        string? resolved = (finalPathResolver ?? TryResolveFinalDirectoryPath)(normalized);
        string? normalizedResolved = TryNormalizeDirectoryPath(resolved);
        if (normalizedResolved is not null)
        {
            aliases.Add(normalizedResolved);
        }

        return aliases;
    }

    private static string? TryResolveFinalDirectoryPath(string normalizedDirectoryPath)
    {
        if (!OperatingSystem.IsWindows() || !Directory.Exists(normalizedDirectoryPath))
        {
            return null;
        }

        return Win32Helper.TryGetFinalPath(normalizedDirectoryPath, out string finalPath)
            ? TryNormalizeDirectoryPath(finalPath)
            : null;
    }

    private static bool AreSameVolumeRoot(
        string firstPath,
        string secondPath,
        Func<string, string?>? finalPathResolver)
    {
        string effectiveFirst = ResolveEffectiveParentOrPath(firstPath, finalPathResolver);
        string effectiveSecond = ResolveEffectiveParentOrPath(secondPath, finalPathResolver);
        string? firstRoot = TryNormalizeDirectoryPath(Path.GetPathRoot(effectiveFirst));
        string? secondRoot = TryNormalizeDirectoryPath(Path.GetPathRoot(effectiveSecond));
        return firstRoot is not null &&
               secondRoot is not null &&
               string.Equals(firstRoot, secondRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveEffectiveParentOrPath(
        string normalizedPath,
        Func<string, string?>? finalPathResolver)
    {
        string? parent = TryNormalizeDirectoryPath(Path.GetDirectoryName(normalizedPath));
        if (parent is null)
        {
            return normalizedPath;
        }

        string? resolvedParent = (finalPathResolver ?? TryResolveFinalDirectoryPath)(parent);
        return TryNormalizeDirectoryPath(resolvedParent) ?? parent;
    }

    private static string? TryNormalizeDirectoryPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

    private static string? TryNormalizeEntryPath(string? path) =>
        TryNormalizeDirectoryPath(path);

    private static T RunBoundedSta<T>(
        string threadName,
        int timeoutMs,
        Func<T> action,
        T fallback)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            int initHr = CoInitializeEx(0, CoInitApartmentThreaded);
            bool shouldUninit = initHr is 0 or 1;
            try
            {
                completion.TrySetResult(action());
            }
            catch
            {
                completion.TrySetResult(fallback);
            }
            finally
            {
                if (shouldUninit)
                {
                    CoUninitialize();
                }
            }
        })
        {
            IsBackground = true,
            Name = threadName,
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        return completion.Task.Wait(Math.Max(150, timeoutMs))
            ? completion.Task.Result
            : fallback;
    }

    private static Dictionary<string, DesktopIconPoint> CaptureOnStaThread(
        HashSet<string> targetPaths)
    {
        var captured = new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase);
        if (!TryAcquireDesktopFolderView(out DesktopViewHandles handles))
        {
            return captured;
        }

        try
        {
            if (IsAutoArrangeEnabled(handles.FolderView))
            {
                return captured;
            }

            List<(string Path, nint ChildPidl)> entries = EnumerateDesktopItems(
                handles.FolderView,
                handles.ShellFolder,
                targetPaths);
            try
            {
                nint* fvVtbl = *(nint**)handles.FolderView;
                // IFolderView::GetItemPosition sits at slot 11.
                var getItemPosition = (delegate* unmanaged[Stdcall]<
                    nint,
                    nint,
                    NativePoint*,
                    int>)fvVtbl[11];

                foreach ((string path, nint childPidl) in entries)
                {
                    NativePoint pt = default;
                    if (getItemPosition(handles.FolderView, childPidl, &pt) >= 0)
                    {
                        captured[path] = new DesktopIconPoint(pt.X, pt.Y);
                    }
                }
            }
            finally
            {
                FreeChildPidls(entries);
            }
        }
        finally
        {
            handles.Release();
        }

        return captured;
    }

    private static HashSet<string> RestoreOnStaThread(
        Dictionary<string, DesktopIconPoint> desiredPositionsByPath,
        int totalTimeoutMs)
    {
        Interlocked.Increment(ref s_activePositionRestoreCount);
        try
        {
            return RestoreOnStaThreadCore(desiredPositionsByPath, totalTimeoutMs);
        }
        finally
        {
            Interlocked.Decrement(ref s_activePositionRestoreCount);
        }
    }

    private static HashSet<string> RestoreOnStaThreadCore(
        Dictionary<string, DesktopIconPoint> desiredPositionsByPath,
        int totalTimeoutMs)
    {
        var handledPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        NotifyAffectedDesktopDirectories(desiredPositionsByPath.Keys);

        if (!TryAcquireDesktopFolderView(out DesktopViewHandles handles))
        {
            return handledPaths;
        }

        try
        {
            if (IsAutoArrangeEnabled(handles.FolderView))
            {
                return handledPaths;
            }

            bool hasViewBounds = TryGetDesktopViewBounds(handles.ShellView, out NativeRect viewBounds);
            var remaining = new Dictionary<string, DesktopIconPoint>(StringComparer.OrdinalIgnoreCase);
            foreach ((string path, DesktopIconPoint pt) in desiredPositionsByPath)
            {
                if (hasViewBounds &&
                    !IsPointWithinViewBounds(pt, viewBounds.Left, viewBounds.Top, viewBounds.Right, viewBounds.Bottom))
                {
                    // Stale coordinate from a disconnected display: treat as
                    // consumed so it is cleared instead of hiding the icon
                    // outside the visible desktop.
                    handledPaths.Add(path);
                    continue;
                }

                remaining[path] = pt;
            }

            if (remaining.Count == 0)
            {
                return handledPaths;
            }

            nint* fvVtbl = *(nint**)handles.FolderView;
            // IFolderView::SelectAndPositionItems sits at slot 16.
            var selectAndPosition = (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                nint*,
                NativePoint*,
                uint,
                int>)fvVtbl[16];

            long deadlineTick = Environment.TickCount64 + Math.Max(200, totalTimeoutMs - 100);
            int attempt = 0;

            while (remaining.Count > 0)
            {
                List<(string Path, nint ChildPidl)> matched = EnumerateDesktopItems(
                    handles.FolderView,
                    handles.ShellFolder,
                    remaining.Keys);
                try
                {
                    if (matched.Count > 0)
                    {
                        var pidls = new nint[matched.Count];
                        var points = new NativePoint[matched.Count];
                        for (int i = 0; i < matched.Count; i++)
                        {
                            DesktopIconPoint pt = remaining[matched[i].Path];
                            pidls[i] = matched[i].ChildPidl;
                            points[i] = new NativePoint { X = pt.X, Y = pt.Y };
                        }

                        int hr;
                        fixed (nint* pPidls = pidls)
                        fixed (NativePoint* pPoints = points)
                        {
                            hr = selectAndPosition(
                                handles.FolderView,
                                (uint)matched.Count,
                                pPidls,
                                pPoints,
                                SvsiPositionItem);
                        }

                        if (hr >= 0)
                        {
                            for (int i = 0; i < matched.Count; i++)
                            {
                                handledPaths.Add(matched[i].Path);
                                remaining.Remove(matched[i].Path);
                            }
                        }
                    }
                }
                finally
                {
                    FreeChildPidls(matched);
                }

                if (remaining.Count == 0)
                {
                    break;
                }

                long nowTick = Environment.TickCount64;
                if (nowTick >= deadlineTick || attempt >= RestorePollDelaysMs.Length)
                {
                    break;
                }

                int delayMs = (int)Math.Min(
                    RestorePollDelaysMs[attempt++],
                    Math.Max(1, deadlineTick - nowTick));
                Thread.Sleep(delayMs);
            }
        }
        finally
        {
            handles.Release();
        }

        return handledPaths;
    }

    private static void NotifyAffectedDesktopDirectories(IEnumerable<string> paths)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                directories.Add(dir);
            }
        }

        foreach (string dir in directories)
        {
            try
            {
                SHChangeNotify(ShcneUpdateDir, ShcnfPathW | ShcnfFlushNoWait, dir, 0);
                NotifyDesktopNamespaceIfMatch(dir);
            }
            catch
            {
            }
        }
    }

    private static bool IsAutoArrangeEnabled(nint folderView)
    {
        nint* fvVtbl = *(nint**)folderView;
        // IFolderView::GetAutoArrange sits at slot 14; returns S_OK (0) when enabled.
        var getAutoArrange = (delegate* unmanaged[Stdcall]<nint, int>)fvVtbl[14];
        return getAutoArrange(folderView) == 0;
    }

    private static bool TryGetDesktopViewBounds(nint shellView, out NativeRect bounds)
    {
        bounds = default;
        nint* svVtbl = *(nint**)shellView;
        // IShellView::GetWindow (inherited from IOleWindow) sits at slot 3.
        var getWindow = (delegate* unmanaged[Stdcall]<nint, nint*, int>)svVtbl[3];
        nint defViewHwnd = 0;
        if (getWindow(shellView, &defViewHwnd) < 0 || defViewHwnd == 0)
        {
            return false;
        }

        nint listViewHwnd = FindWindowExW(defViewHwnd, 0, "SysListView32", null);
        nint targetHwnd = listViewHwnd != 0 ? listViewHwnd : defViewHwnd;
        return GetClientRect(targetHwnd, out bounds) &&
               bounds.Right > bounds.Left &&
               bounds.Bottom > bounds.Top;
    }

    private static List<(string Path, nint ChildPidl)> EnumerateDesktopItems(
        nint folderView,
        nint shellFolder,
        IEnumerable<string> targetPaths)
    {
        var targets = targetPaths as HashSet<string>
            ?? new HashSet<string>(targetPaths, StringComparer.OrdinalIgnoreCase);
        var matched = new List<(string Path, nint ChildPidl)>();
        if (targets.Count == 0)
        {
            return matched;
        }

        nint* fvVtbl = *(nint**)folderView;
        // IFolderView::Items sits at slot 8.
        var itemsMethod = (delegate* unmanaged[Stdcall]<
            nint,
            uint,
            Guid*,
            nint*,
            int>)fvVtbl[8];
        Guid iidEnum = IidIEnumIdList;
        nint enumPtr = 0;
        if (itemsMethod(folderView, SvgioAllView, &iidEnum, &enumPtr) < 0 || enumPtr == 0)
        {
            return matched;
        }

        try
        {
            nint* enumVtbl = *(nint**)enumPtr;
            // IEnumIDList::Next sits at slot 3.
            var nextMethod = (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                nint*,
                uint*,
                int>)enumVtbl[3];

            nint* sfVtbl = *(nint**)shellFolder;
            // IShellFolder::GetDisplayNameOf sits at slot 11.
            var getDisplayNameOf = (delegate* unmanaged[Stdcall]<
                nint,
                nint,
                uint,
                StrRet*,
                int>)sfVtbl[11];

            char* pathBuffer = stackalloc char[2048];
            while (matched.Count < targets.Count)
            {
                nint childPidl = 0;
                uint fetched = 0;
                int hr = nextMethod(enumPtr, 1, &childPidl, &fetched);
                if (hr != 0 || fetched == 0 || childPidl == 0)
                {
                    if (childPidl != 0)
                    {
                        CoTaskMemFree(childPidl);
                    }

                    break;
                }

                string? parsingPath = TryReadParsingPath(
                    shellFolder,
                    getDisplayNameOf,
                    childPidl,
                    pathBuffer,
                    2048);
                if (!string.IsNullOrWhiteSpace(parsingPath) && targets.Contains(parsingPath))
                {
                    matched.Add((parsingPath, childPidl));
                }
                else
                {
                    CoTaskMemFree(childPidl);
                }
            }
        }
        finally
        {
            Marshal.Release(enumPtr);
        }

        return matched;
    }

    private static string? TryReadParsingPath(
        nint shellFolder,
        delegate* unmanaged[Stdcall]<nint, nint, uint, StrRet*, int> getDisplayNameOf,
        nint childPidl,
        char* buffer,
        uint bufferChars)
    {
        StrRet strRet = default;
        if (getDisplayNameOf(shellFolder, childPidl, ShgdnForParsing, &strRet) < 0)
        {
            return null;
        }

        buffer[0] = '\0';
        if (StrRetToBufW(ref strRet, childPidl, buffer, bufferChars) < 0)
        {
            return null;
        }

        string raw = new(buffer);
        if (string.IsNullOrWhiteSpace(raw) || raw.StartsWith("::", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
        }
        catch
        {
            return null;
        }
    }

    private static void FreeChildPidls(List<(string Path, nint ChildPidl)> entries)
    {
        for (int i = 0; i < entries.Count; i++)
        {
            if (entries[i].ChildPidl != 0)
            {
                CoTaskMemFree(entries[i].ChildPidl);
            }
        }
    }

    private struct DesktopViewHandles
    {
        public nint ShellWindows;
        public nint DesktopDispatch;
        public nint ServiceProvider;
        public nint ShellBrowser;
        public nint ShellView;
        public nint FolderView;
        public nint ShellFolder;

        public void Release()
        {
            if (ShellFolder != 0) { Marshal.Release(ShellFolder); ShellFolder = 0; }
            if (FolderView != 0) { Marshal.Release(FolderView); FolderView = 0; }
            if (ShellView != 0) { Marshal.Release(ShellView); ShellView = 0; }
            if (ShellBrowser != 0) { Marshal.Release(ShellBrowser); ShellBrowser = 0; }
            if (ServiceProvider != 0) { Marshal.Release(ServiceProvider); ServiceProvider = 0; }
            if (DesktopDispatch != 0) { Marshal.Release(DesktopDispatch); DesktopDispatch = 0; }
            if (ShellWindows != 0) { Marshal.Release(ShellWindows); ShellWindows = 0; }
        }
    }

    private static bool TryAcquireDesktopFolderView(out DesktopViewHandles handles)
    {
        handles = default;
        try
        {
            int hr = CoCreateInstance(
                in ClsidShellWindows,
                0,
                ClsctxLocalServer | ClsctxInprocServer,
                in IidIShellWindows,
                out handles.ShellWindows);
            if (hr < 0 || handles.ShellWindows == 0)
            {
                handles.Release();
                return false;
            }

            NativeVariant varLoc = default;
            NativeVariant varLocRoot = default;
            int desktopHwndInt = 0;
            nint* swVtbl = *(nint**)handles.ShellWindows;
            // IShellWindows::FindWindowSW sits at slot 15.
            var findWindowSw = (delegate* unmanaged[Stdcall]<
                nint,
                NativeVariant*,
                NativeVariant*,
                int,
                int*,
                int,
                nint*,
                int>)swVtbl[15];
            nint foundDesktop = 0;
            hr = findWindowSw(
                handles.ShellWindows,
                &varLoc,
                &varLocRoot,
                SwcDesktop,
                &desktopHwndInt,
                SwfoNeedDispatch,
                &foundDesktop);
            handles.DesktopDispatch = foundDesktop;
            if (hr < 0 || handles.DesktopDispatch == 0)
            {
                handles.Release();
                return false;
            }

            Guid iidSp = IidIServiceProvider;
            if (ComVtable.QueryInterface(
                    handles.DesktopDispatch,
                    ref iidSp,
                    out handles.ServiceProvider) < 0 ||
                handles.ServiceProvider == 0)
            {
                handles.Release();
                return false;
            }

            nint* spVtbl = *(nint**)handles.ServiceProvider;
            var queryService = (delegate* unmanaged[Stdcall]<
                nint,
                Guid*,
                Guid*,
                nint*,
                int>)spVtbl[3];
            Guid sidTopBrowser = SidSTopLevelBrowser;
            Guid iidShellBrowser = IidIShellBrowser;
            nint foundBrowser = 0;
            if (queryService(
                    handles.ServiceProvider,
                    &sidTopBrowser,
                    &iidShellBrowser,
                    &foundBrowser) < 0 ||
                foundBrowser == 0)
            {
                handles.Release();
                return false;
            }

            handles.ShellBrowser = foundBrowser;

            nint* sbVtbl = *(nint**)handles.ShellBrowser;
            var queryActiveShellView = (delegate* unmanaged[Stdcall]<
                nint,
                nint*,
                int>)sbVtbl[15];
            nint foundView = 0;
            if (queryActiveShellView(handles.ShellBrowser, &foundView) < 0 ||
                foundView == 0)
            {
                handles.Release();
                return false;
            }

            handles.ShellView = foundView;

            Guid iidFolderView = IidIFolderView;
            if (ComVtable.QueryInterface(
                    handles.ShellView,
                    ref iidFolderView,
                    out handles.FolderView) < 0 ||
                handles.FolderView == 0)
            {
                handles.Release();
                return false;
            }

            nint* fvVtbl = *(nint**)handles.FolderView;
            // IFolderView::GetFolder sits at slot 5.
            var getFolder = (delegate* unmanaged[Stdcall]<
                nint,
                Guid*,
                nint*,
                int>)fvVtbl[5];
            Guid iidShellFolder = IidIShellFolder;
            nint foundFolder = 0;
            if (getFolder(handles.FolderView, &iidShellFolder, &foundFolder) < 0 ||
                foundFolder == 0)
            {
                handles.Release();
                return false;
            }

            handles.ShellFolder = foundFolder;
            return true;
        }
        catch
        {
            handles.Release();
            return false;
        }
    }
}
