using System.Runtime.InteropServices;

namespace DeskBox.Platform;

internal readonly record struct DesktopIconPoint(int X, int Y);

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
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const uint SvgioAllView = 0x00000002;
    private const uint ShgdnForParsing = 0x00008000;
    private const uint SvsiPositionItem = 0x00000080;
    private const int ShcneUpdateDir = 0x00001000;
    private const uint ShcnfPathW = 0x0005;
    private const uint ShcnfFlush = 0x1000;
    private const int BoundsTolerancePx = 64;

    private static readonly int[] RestorePollDelaysMs = [80, 140, 220, 320, 450];

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
                SHChangeNotify(ShcneUpdateDir, ShcnfPathW | ShcnfFlush, dir, 0);
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
