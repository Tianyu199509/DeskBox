using System.Runtime.InteropServices;

namespace DeskBox.Platform;

internal enum ElevatedOutboundTargetKind
{
    None,
    Desktop,
    ExplorerFolder,
    UnsupportedShellNamespace,
    ForeignWindow,
}

internal readonly record struct ElevatedOutboundTargetInfo(
    ElevatedOutboundTargetKind Kind,
    string DestinationFolderPath,
    string DisplayName);

/// <summary>
/// Resolves external Shell/window drop targets and positions dropped Desktop
/// icons when DeskBox runs in an elevated desktop session where WinUI's XAML
/// drag source (<c>CanDragItems</c> / <c>StartDragAsync</c>) is unavailable.
/// All COM calls use raw vtable function pointers so JIT and NativeAOT execute
/// identical code paths without RCW marshaling.
/// </summary>
internal static unsafe partial class ElevatedShellOutboundDropInterop
{
    private const uint CoInitApartmentThreaded = 0x2;
    private const uint ClsctxInprocServer = 0x1;
    private const uint ClsctxLocalServer = 0x4;
    private const uint GaRoot = 2;
    private const int SwcDesktop = 8;
    private const int SwfoNeedDispatch = 1;
    private const ushort VtI4 = 3;
    private const int GpfidlDefault = 0x0000;
    private const int ShcneUpdateDir = 0x00001000;
    private const uint ShcnfPathW = 0x0005;
    private const uint ShcnfFlush = 0x1000;
    private const uint SvsiSelect = 0x00000001;
    private const uint SvsiDeselectOthers = 0x00000004;
    private const uint SvsiEnsureVisible = 0x00000008;
    private const uint SvsiPositionItem = 0x00000080;
    private const int MaxAncestorDepth = 16;

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

    private static readonly Guid IidIPersistFolder2 =
        new("1AC3D9F0-175C-11D1-95BE-00609797EA4F");

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

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static partial int GetClassNameW(
        nint hWnd,
        char* lpClassName,
        int nMaxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetParent")]
    private static partial nint GetParent(nint hWnd);

    [LibraryImport("user32.dll", EntryPoint = "GetAncestor")]
    private static partial nint GetAncestor(nint hWnd, uint gaFlags);

    [LibraryImport("user32.dll", EntryPoint = "GetShellWindow")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static partial uint GetWindowThreadProcessId(
        nint hWnd,
        out uint lpdwProcessId);

    [LibraryImport("user32.dll", EntryPoint = "IsWindowVisible")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint hWnd);

    [LibraryImport(
        "user32.dll",
        EntryPoint = "FindWindowExW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(
        nint hWndParent,
        nint hWndChildAfter,
        string? lpszClass,
        string? lpszWindow);

    [LibraryImport("user32.dll", EntryPoint = "ScreenToClient")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ScreenToClient(nint hWnd, ref NativePoint lpPoint);

    [LibraryImport("ole32.dll", EntryPoint = "CoInitializeEx")]
    private static partial int CoInitializeEx(nint pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll", EntryPoint = "CoUninitialize")]
    private static partial void CoUninitialize();

    [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance")]
    private static partial int CoCreateInstance(
        in Guid rclsid,
        nint pUnkOuter,
        uint dwClsContext,
        in Guid riid,
        out nint ppv);

    [LibraryImport("ole32.dll", EntryPoint = "CoTaskMemFree")]
    private static partial void CoTaskMemFree(nint pv);

    [LibraryImport("shell32.dll", EntryPoint = "SHGetPathFromIDListEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SHGetPathFromIDListEx(
        nint pidl,
        char* pszPath,
        uint cchPath,
        int uOpts);

    [LibraryImport(
        "shell32.dll",
        EntryPoint = "ILCreateFromPathW",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ILCreateFromPathW(string pszPath);

    [LibraryImport("shell32.dll", EntryPoint = "ILFindLastID")]
    private static partial nint ILFindLastID(nint pidl);

    [LibraryImport(
        "shell32.dll",
        EntryPoint = "SHChangeNotify",
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial void SHChangeNotify(
        int wEventId,
        uint uFlags,
        string? dwItem1,
        nint dwItem2);

    internal static ElevatedOutboundTargetKind ClassifyWindowClasses(
        IReadOnlyList<string> ancestorClasses,
        string rootClass,
        bool matchesShellPid,
        bool isCurrentProcess)
    {
        if (isCurrentProcess || string.IsNullOrWhiteSpace(rootClass))
        {
            return ElevatedOutboundTargetKind.None;
        }

        if (IsIgnoredShellChromeClass(rootClass))
        {
            return ElevatedOutboundTargetKind.None;
        }

        for (int i = 0; i < ancestorClasses.Count; i++)
        {
            if (IsIgnoredShellChromeClass(ancestorClasses[i]))
            {
                return ElevatedOutboundTargetKind.None;
            }
        }

        if (IsExplorerWindowClass(rootClass))
        {
            if (IsExplorerNonFolderChromeHierarchy(ancestorClasses))
            {
                return ElevatedOutboundTargetKind.None;
            }

            return ElevatedOutboundTargetKind.ExplorerFolder;
        }

        // Standard file dialogs (#32770) host a SHELLDLL_DefView inside a
        // third-party process; never mistake them for the Explorer desktop.
        if (!string.Equals(rootClass, "#32770", StringComparison.OrdinalIgnoreCase))
        {
            bool hasDesktopClass = IsDesktopHostClass(rootClass);
            if (!hasDesktopClass)
            {
                for (int i = 0; i < ancestorClasses.Count; i++)
                {
                    if (IsDesktopHostClass(ancestorClasses[i]))
                    {
                        hasDesktopClass = true;
                        break;
                    }
                }
            }

            if (hasDesktopClass)
            {
                return matchesShellPid
                    ? ElevatedOutboundTargetKind.Desktop
                    : ElevatedOutboundTargetKind.None;
            }
        }

        return ElevatedOutboundTargetKind.ForeignWindow;
    }

    internal static nint ResolveTargetTabWindow(nint hitHwnd, nint rootHwnd)
    {
        if (rootHwnd == 0)
        {
            return 0;
        }

        nint current = hitHwnd;
        for (int depth = 0; current != 0 && depth < MaxAncestorDepth; depth++)
        {
            string className = ReadWindowClassName(current);
            if (string.Equals(
                    className,
                    "ShellTabWindowClass",
                    StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            if (current == rootHwnd)
            {
                break;
            }

            current = GetParent(current);
        }

        // Only fall back to searching child ShellTabWindowClass windows when
        // the caller queried the root window directly (hitHwnd == rootHwnd or 0).
        // When hitHwnd is a distinct non-tab child (such as Win11's XAML Island
        // tab strip or command bar), returning the active tab would silently
        // drop files into Tab 1 when the user released over inactive Tab 2.
        if (hitHwnd != 0 && hitHwnd != rootHwnd)
        {
            return 0;
        }

        nint firstTab = 0;
        nint child = 0;
        for (int guard = 0; guard < 64; guard++)
        {
            child = FindWindowExW(rootHwnd, child, "ShellTabWindowClass", null);
            if (child == 0)
            {
                break;
            }

            if (firstTab == 0)
            {
                firstTab = child;
            }

            if (IsWindowVisible(child))
            {
                return child;
            }
        }

        return firstTab;
    }

    private static readonly object s_explorerProbeGate = new();
    private static AutoResetEvent? s_explorerProbeSignal;
    private static Thread? s_explorerProbeWorker;
    private static bool s_hasPendingExplorerProbe;
    private static nint s_pendingProbeRootHwnd;
    private static nint s_pendingProbeTabHwnd;
    private static Action<nint, nint, ElevatedOutboundTargetInfo>? s_pendingProbeCallback;
    private static int s_syncStaCallInFlight;
    private static string? s_cachedDesktopPath;

    internal static bool TryClassifyImmediateOutboundTarget(
        IntPtr hitHwnd,
        IntPtr rootHwnd,
        out IntPtr resolvedRootHwnd,
        out IntPtr targetTabHwnd,
        out ElevatedOutboundTargetInfo immediateTarget)
    {
        targetTabHwnd = IntPtr.Zero;
        nint hit = hitHwnd;
        nint root = rootHwnd != IntPtr.Zero
            ? rootHwnd
            : (hit != 0 ? GetAncestor(hit, GaRoot) : 0);
        if (root == 0 && hit != 0)
        {
            root = hit;
        }

        resolvedRootHwnd = root;
        if (root == 0 || !IsWindowVisible(root))
        {
            immediateTarget = new ElevatedOutboundTargetInfo(
                ElevatedOutboundTargetKind.None,
                string.Empty,
                string.Empty);
            return true;
        }

        GetWindowThreadProcessId(root, out uint targetPid);
        bool isCurrentProcess = targetPid != 0 &&
            targetPid == (uint)Environment.ProcessId;
        if (isCurrentProcess)
        {
            immediateTarget = new ElevatedOutboundTargetInfo(
                ElevatedOutboundTargetKind.None,
                string.Empty,
                string.Empty);
            return true;
        }

        string rootClass = ReadWindowClassName(root);
        var ancestorClasses = new List<string>(8);
        nint current = hit != 0 ? hit : root;
        for (int depth = 0; current != 0 && depth < MaxAncestorDepth; depth++)
        {
            string className = ReadWindowClassName(current);
            if (className.Length > 0 &&
                !ancestorClasses.Contains(className, StringComparer.OrdinalIgnoreCase))
            {
                ancestorClasses.Add(className);
            }

            if (current == root)
            {
                break;
            }

            current = GetParent(current);
        }

        nint shellWindow = GetShellWindow();
        bool matchesShellPid = true;
        if (shellWindow != 0)
        {
            GetWindowThreadProcessId(shellWindow, out uint shellPid);
            matchesShellPid = shellPid == 0 ||
                targetPid == 0 ||
                shellPid == targetPid;
        }

        ElevatedOutboundTargetKind classVerdict = ClassifyWindowClasses(
            ancestorClasses,
            rootClass,
            matchesShellPid,
            isCurrentProcess: false);
        switch (classVerdict)
        {
            case ElevatedOutboundTargetKind.None:
                immediateTarget = new ElevatedOutboundTargetInfo(
                    ElevatedOutboundTargetKind.None,
                    string.Empty,
                    string.Empty);
                return true;

            case ElevatedOutboundTargetKind.Desktop:
            {
                string desktopPath = GetCachedDesktopDirectoryPath();
                if (string.IsNullOrWhiteSpace(desktopPath))
                {
                    immediateTarget = new ElevatedOutboundTargetInfo(
                        ElevatedOutboundTargetKind.None,
                        string.Empty,
                        string.Empty);
                    return true;
                }

                immediateTarget = new ElevatedOutboundTargetInfo(
                    ElevatedOutboundTargetKind.Desktop,
                    desktopPath,
                    GetFolderDisplayName(desktopPath));
                return true;
            }

            case ElevatedOutboundTargetKind.ExplorerFolder:
                targetTabHwnd = ResolveTargetTabWindow(hit, root);
                immediateTarget = default;
                return false;

            default:
                immediateTarget = new ElevatedOutboundTargetInfo(
                    ElevatedOutboundTargetKind.ForeignWindow,
                    string.Empty,
                    string.Empty);
                return true;
        }
    }

    internal static void RequestExplorerWindowTargetProbe(
        IntPtr rootHwnd,
        IntPtr targetTabHwnd,
        Action<nint, nint, ElevatedOutboundTargetInfo> onCompleted)
    {
        if (rootHwnd == IntPtr.Zero || onCompleted is null)
        {
            return;
        }

        lock (s_explorerProbeGate)
        {
            s_pendingProbeRootHwnd = rootHwnd;
            s_pendingProbeTabHwnd = targetTabHwnd;
            s_pendingProbeCallback = onCompleted;
            s_hasPendingExplorerProbe = true;

            if (s_explorerProbeSignal is null)
            {
                s_explorerProbeSignal = new AutoResetEvent(initialState: false);
            }

            if (s_explorerProbeWorker is null)
            {
                var worker = new Thread(ExplorerProbeStaWorkerLoop)
                {
                    IsBackground = true,
                    Name = "DeskBox.ElevatedExplorerProbeSta",
                };
                worker.SetApartmentState(ApartmentState.STA);
                s_explorerProbeWorker = worker;
                worker.Start();
            }

            s_explorerProbeSignal.Set();
        }
    }

    private static void ExplorerProbeStaWorkerLoop()
    {
        while (true)
        {
            s_explorerProbeSignal?.WaitOne();

            while (true)
            {
                nint rootHwnd;
                nint tabHwnd;
                Action<nint, nint, ElevatedOutboundTargetInfo>? callback;
                lock (s_explorerProbeGate)
                {
                    if (!s_hasPendingExplorerProbe)
                    {
                        break;
                    }

                    rootHwnd = s_pendingProbeRootHwnd;
                    tabHwnd = s_pendingProbeTabHwnd;
                    callback = s_pendingProbeCallback;
                    s_hasPendingExplorerProbe = false;
                    s_pendingProbeCallback = null;
                }

                var resolved = new ElevatedOutboundTargetInfo(
                ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                string.Empty,
                string.Empty);
                try
                {
                    resolved = QueryExplorerWindowTargetCore(rootHwnd, tabHwnd);
                }
                catch
                {
                    resolved = new ElevatedOutboundTargetInfo(
                        ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                        string.Empty,
                        string.Empty);
                }

                try
                {
                    callback?.Invoke(rootHwnd, tabHwnd, resolved);
                }
                catch
                {
                }
            }
        }
    }

    private static string GetCachedDesktopDirectoryPath()
    {
        if (s_cachedDesktopPath is { } cached)
        {
            return cached;
        }

        try
        {
            string raw = Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);
            if (string.IsNullOrWhiteSpace(raw) || !Directory.Exists(raw))
            {
                return string.Empty;
            }

            string normalized = Path.GetFullPath(raw);
            s_cachedDesktopPath = normalized;
            return normalized;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static ElevatedOutboundTargetInfo ResolveOutboundTarget(
        IntPtr hitHwnd,
        IntPtr rootHwnd,
        int explorerQueryTimeoutMs = 450)
    {
        if (TryClassifyImmediateOutboundTarget(
                hitHwnd,
                rootHwnd,
                out IntPtr resolvedRoot,
                out IntPtr targetTabHwnd,
                out ElevatedOutboundTargetInfo immediate))
        {
            return immediate;
        }

        return QueryExplorerWindowTargetOnSta(
            resolvedRoot,
            targetTabHwnd,
            explorerQueryTimeoutMs);
    }

    internal static bool TryPositionDesktopItemsNearCursor(
        IReadOnlyList<string> destinationPaths,
        int screenX,
        int screenY,
        int timeoutMs = 1500)
    {
        if (destinationPaths is null || destinationPaths.Count == 0)
        {
            return false;
        }

        string[] existingPaths = destinationPaths
            .Where(path =>
                !string.IsNullOrWhiteSpace(path) &&
                (File.Exists(path) || Directory.Exists(path)))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (existingPaths.Length == 0)
        {
            return false;
        }

        if (Interlocked.CompareExchange(ref s_syncStaCallInFlight, 1, 0) != 0)
        {
            return false;
        }

        string desktopDir = Environment.GetFolderPath(
            Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrWhiteSpace(desktopDir))
        {
            SHChangeNotify(
                ShcneUpdateDir,
                ShcnfPathW | ShcnfFlush,
                desktopDir,
                0);
        }

        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            bool positioned = false;
            try
            {
                positioned = ExecuteDesktopPositioningCore(
                    existingPaths,
                    screenX,
                    screenY);
            }
            catch
            {
                positioned = false;
            }
            finally
            {
                Interlocked.Exchange(ref s_syncStaCallInFlight, 0);
                completion.TrySetResult(positioned);
            }
        })
        {
            IsBackground = true,
            Name = "DeskBox.ElevatedDesktopPosition",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        return completion.Task.Wait(Math.Max(100, timeoutMs)) &&
               completion.Task.Result;
    }

    private static ElevatedOutboundTargetInfo QueryExplorerWindowTargetOnSta(
        nint rootHwnd,
        nint targetTabHwnd,
        int timeoutMs)
    {
        if (Interlocked.CompareExchange(ref s_syncStaCallInFlight, 1, 0) != 0)
        {
            return new ElevatedOutboundTargetInfo(
                ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                string.Empty,
                string.Empty);
        }

        var completion = new TaskCompletionSource<ElevatedOutboundTargetInfo>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            var resolved = new ElevatedOutboundTargetInfo(
                ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                string.Empty,
                string.Empty);
            try
            {
                resolved = QueryExplorerWindowTargetCore(rootHwnd, targetTabHwnd);
            }
            catch
            {
                resolved = new ElevatedOutboundTargetInfo(
                    ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                    string.Empty,
                    string.Empty);
            }
            finally
            {
                Interlocked.Exchange(ref s_syncStaCallInFlight, 0);
                completion.TrySetResult(resolved);
            }
        })
        {
            IsBackground = true,
            Name = "DeskBox.ElevatedExplorerTarget",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        if (!completion.Task.Wait(Math.Max(80, timeoutMs)))
        {
            return new ElevatedOutboundTargetInfo(
                ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                string.Empty,
                string.Empty);
        }

        return completion.Task.Result;
    }

    private static ElevatedOutboundTargetInfo QueryExplorerWindowTargetCore(
        nint rootHwnd,
        nint targetTabHwnd)
    {
        int coInitHr = CoInitializeEx(0, CoInitApartmentThreaded);
        bool shouldUninit = coInitHr is 0 or 1;
        if (coInitHr < 0)
        {
            return new ElevatedOutboundTargetInfo(
                ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                string.Empty,
                string.Empty);
        }

        nint shellWindows = 0;
        try
        {
            int hr = CoCreateInstance(
                in ClsidShellWindows,
                0,
                ClsctxLocalServer | ClsctxInprocServer,
                in IidIShellWindows,
                out shellWindows);
            if (hr < 0 || shellWindows == 0)
            {
                return new ElevatedOutboundTargetInfo(
                    ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                    string.Empty,
                    string.Empty);
            }

            nint* swVtbl = *(nint**)shellWindows;
            var getCount = (delegate* unmanaged[Stdcall]<nint, int*, int>)swVtbl[7];
            var getItem = (delegate* unmanaged[Stdcall]<
                nint,
                NativeVariant,
                nint*,
                int>)swVtbl[8];

            int count = 0;
            if (getCount(shellWindows, &count) < 0 || count <= 0)
            {
                return new ElevatedOutboundTargetInfo(
                    ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                    string.Empty,
                    string.Empty);
            }

            ElevatedOutboundTargetInfo? fallbackRootMatch = null;
            int maxItems = Math.Min(count, 128);
            for (int index = 0; index < maxItems; index++)
            {
                NativeVariant varIndex = default;
                varIndex.Vt = VtI4;
                varIndex.Data1 = index;

                nint itemDispatch = 0;
                if (getItem(shellWindows, varIndex, &itemDispatch) < 0 ||
                    itemDispatch == 0)
                {
                    continue;
                }

                try
                {
                    if (TryInspectExplorerBrowserItem(
                            itemDispatch,
                            rootHwnd,
                            targetTabHwnd,
                            out bool exactTabMatch,
                            out ElevatedOutboundTargetInfo candidate))
                    {
                        if (exactTabMatch)
                        {
                            return candidate;
                        }

                        fallbackRootMatch ??= candidate;
                    }
                }
                finally
                {
                    Marshal.Release(itemDispatch);
                }
            }

            return fallbackRootMatch ?? new ElevatedOutboundTargetInfo(
                ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                string.Empty,
                string.Empty);
        }
        finally
        {
            if (shellWindows != 0)
            {
                Marshal.Release(shellWindows);
            }

            if (shouldUninit)
            {
                CoUninitialize();
            }
        }
    }

    private static bool TryInspectExplorerBrowserItem(
        nint itemDispatch,
        nint rootHwnd,
        nint targetTabHwnd,
        out bool exactTabMatch,
        out ElevatedOutboundTargetInfo info)
    {
        exactTabMatch = false;
        info = default;

        nint serviceProvider = 0;
        nint shellBrowser = 0;
        nint shellView = 0;
        nint folderView = 0;
        nint persistFolder2 = 0;
        nint folderPidl = 0;
        try
        {
            Guid iidServiceProvider = IidIServiceProvider;
            if (ComVtable.QueryInterface(
                    itemDispatch,
                    ref iidServiceProvider,
                    out serviceProvider) < 0 ||
                serviceProvider == 0)
            {
                return false;
            }

            // IServiceProvider::QueryService sits at slot 3.
            nint* spVtbl = *(nint**)serviceProvider;
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
                    serviceProvider,
                    &sidTopBrowser,
                    &iidShellBrowser,
                    &foundBrowser) < 0 ||
                foundBrowser == 0)
            {
                return false;
            }

            shellBrowser = foundBrowser;

            // IOleWindow::GetWindow sits at slot 3 on IShellBrowser.
            nint* sbVtbl = *(nint**)shellBrowser;
            var getBrowserWindow = (delegate* unmanaged[Stdcall]<
                nint,
                nint*,
                int>)sbVtbl[3];
            nint browserHwnd = 0;
            if (getBrowserWindow(shellBrowser, &browserHwnd) < 0 ||
                browserHwnd == 0)
            {
                return false;
            }

            bool matchesTab = targetTabHwnd != 0 && browserHwnd == targetTabHwnd;
            bool matchesRoot = browserHwnd == rootHwnd ||
                GetAncestor(browserHwnd, GaRoot) == rootHwnd;
            if (!matchesTab && !matchesRoot)
            {
                return false;
            }

            // IShellBrowser::QueryActiveShellView sits at slot 15.
            var queryActiveShellView = (delegate* unmanaged[Stdcall]<
                nint,
                nint*,
                int>)sbVtbl[15];
            nint activeView = 0;
            if (queryActiveShellView(shellBrowser, &activeView) < 0 ||
                activeView == 0)
            {
                return false;
            }

            shellView = activeView;
            if (!matchesTab)
            {
                // On multi-tab Windows 11 Explorer, inactive tabs share the
                // same root CabinetWClass window while keeping their
                // SHELLDLL_DefView hidden. Ignore hidden views in the root
                // fallback so an inactive tab never steals the resolution.
                nint* svVtbl = *(nint**)shellView;
                var getViewWindow = (delegate* unmanaged[Stdcall]<
                    nint,
                    nint*,
                    int>)svVtbl[3];
                nint viewHwnd = 0;
                if (getViewWindow(shellView, &viewHwnd) >= 0 &&
                    viewHwnd != 0 &&
                    !IsWindowVisible(viewHwnd))
                {
                    return false;
                }
            }

            exactTabMatch = matchesTab;

            Guid iidFolderView = IidIFolderView;
            if (ComVtable.QueryInterface(
                    shellView,
                    ref iidFolderView,
                    out folderView) < 0 ||
                folderView == 0)
            {
                info = new ElevatedOutboundTargetInfo(
                    ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                    string.Empty,
                    string.Empty);
                return true;
            }

            // IFolderView::GetFolder sits at slot 5.
            nint* fvVtbl = *(nint**)folderView;
            var getFolder = (delegate* unmanaged[Stdcall]<
                nint,
                Guid*,
                nint*,
                int>)fvVtbl[5];
            Guid iidPersistFolder2 = IidIPersistFolder2;
            nint foundPersist = 0;
            if (getFolder(folderView, &iidPersistFolder2, &foundPersist) < 0 ||
                foundPersist == 0)
            {
                info = new ElevatedOutboundTargetInfo(
                    ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                    string.Empty,
                    string.Empty);
                return true;
            }

            persistFolder2 = foundPersist;

            // IPersistFolder2::GetCurFolder sits at slot 5
            // (3 IUnknown + 1 IPersist::GetClassID + 1 IPersistFolder::Initialize).
            nint* pfVtbl = *(nint**)persistFolder2;
            var getCurFolder = (delegate* unmanaged[Stdcall]<
                nint,
                nint*,
                int>)pfVtbl[5];
            nint curPidl = 0;
            if (getCurFolder(persistFolder2, &curPidl) < 0 || curPidl == 0)
            {
                info = new ElevatedOutboundTargetInfo(
                    ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                    string.Empty,
                    string.Empty);
                return true;
            }

            folderPidl = curPidl;
            char* pathBuffer = stackalloc char[1024];
            if (!SHGetPathFromIDListEx(
                    folderPidl,
                    pathBuffer,
                    1024,
                    GpfidlDefault))
            {
                info = new ElevatedOutboundTargetInfo(
                    ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                    string.Empty,
                    string.Empty);
                return true;
            }

            string folderPath = new(pathBuffer);
            if (string.IsNullOrWhiteSpace(folderPath) ||
                !Directory.Exists(folderPath))
            {
                info = new ElevatedOutboundTargetInfo(
                    ElevatedOutboundTargetKind.UnsupportedShellNamespace,
                    string.Empty,
                    string.Empty);
                return true;
            }

            string normalized = Path.GetFullPath(folderPath);
            info = new ElevatedOutboundTargetInfo(
                ElevatedOutboundTargetKind.ExplorerFolder,
                normalized,
                GetFolderDisplayName(normalized));
            return true;
        }
        finally
        {
            if (folderPidl != 0) CoTaskMemFree(folderPidl);
            if (persistFolder2 != 0) Marshal.Release(persistFolder2);
            if (folderView != 0) Marshal.Release(folderView);
            if (shellView != 0) Marshal.Release(shellView);
            if (shellBrowser != 0) Marshal.Release(shellBrowser);
            if (serviceProvider != 0) Marshal.Release(serviceProvider);
        }
    }

    private static bool ExecuteDesktopPositioningCore(
        string[] destinationPaths,
        int screenX,
        int screenY)
    {
        int coInitHr = CoInitializeEx(0, CoInitApartmentThreaded);
        bool shouldUninit = coInitHr is 0 or 1;
        if (coInitHr < 0)
        {
            return false;
        }

        nint shellWindows = 0;
        nint desktopDispatch = 0;
        nint serviceProvider = 0;
        nint shellBrowser = 0;
        nint shellView = 0;
        nint folderView = 0;
        var fullPidls = new List<nint>(destinationPaths.Length);
        try
        {
            int hr = CoCreateInstance(
                in ClsidShellWindows,
                0,
                ClsctxLocalServer | ClsctxInprocServer,
                in IidIShellWindows,
                out shellWindows);
            if (hr < 0 || shellWindows == 0)
            {
                return false;
            }

            // IShellWindows::FindWindowSW sits at slot 15.
            NativeVariant varLoc = default;
            NativeVariant varLocRoot = default;
            int desktopHwndInt = 0;
            nint* swVtbl = *(nint**)shellWindows;
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
                shellWindows,
                &varLoc,
                &varLocRoot,
                SwcDesktop,
                &desktopHwndInt,
                SwfoNeedDispatch,
                &foundDesktop);
            desktopDispatch = foundDesktop;
            if (hr < 0 || desktopDispatch == 0)
            {
                return false;
            }

            Guid iidServiceProvider = IidIServiceProvider;
            if (ComVtable.QueryInterface(
                    desktopDispatch,
                    ref iidServiceProvider,
                    out serviceProvider) < 0 ||
                serviceProvider == 0)
            {
                return false;
            }

            nint* spVtbl = *(nint**)serviceProvider;
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
                    serviceProvider,
                    &sidTopBrowser,
                    &iidShellBrowser,
                    &foundBrowser) < 0 ||
                foundBrowser == 0)
            {
                return false;
            }

            shellBrowser = foundBrowser;

            nint* sbVtbl = *(nint**)shellBrowser;
            var queryActiveShellView = (delegate* unmanaged[Stdcall]<
                nint,
                nint*,
                int>)sbVtbl[15];
            nint foundView = 0;
            if (queryActiveShellView(shellBrowser, &foundView) < 0 ||
                foundView == 0)
            {
                return false;
            }

            shellView = foundView;

            Guid iidFolderView = IidIFolderView;
            if (ComVtable.QueryInterface(
                    shellView,
                    ref iidFolderView,
                    out folderView) < 0 ||
                folderView == 0)
            {
                return false;
            }

            // Resolve the desktop DefView / SysListView32 coordinate space.
            nint* svVtbl = *(nint**)shellView;
            var getViewWindow = (delegate* unmanaged[Stdcall]<
                nint,
                nint*,
                int>)svVtbl[3];
            nint defViewHwnd = 0;
            _ = getViewWindow(shellView, &defViewHwnd);
            nint listViewHwnd = defViewHwnd != 0
                ? FindWindowExW(defViewHwnd, 0, "SysListView32", null)
                : 0;
            nint coordHwnd = listViewHwnd != 0
                ? listViewHwnd
                : (defViewHwnd != 0 ? defViewHwnd : desktopHwndInt);

            NativePoint anchorClient = new() { X = screenX, Y = screenY };
            if (coordHwnd != 0)
            {
                _ = ScreenToClient(coordHwnd, ref anchorClient);
            }

            anchorClient.X = Math.Max(8, anchorClient.X - 24);
            anchorClient.Y = Math.Max(8, anchorClient.Y - 24);

            var childPidls = new List<nint>(destinationPaths.Length);
            foreach (string path in destinationPaths)
            {
                nint fullPidl = ILCreateFromPathW(path);
                if (fullPidl == 0)
                {
                    continue;
                }

                fullPidls.Add(fullPidl);
                nint childPidl = ILFindLastID(fullPidl);
                if (childPidl != 0)
                {
                    childPidls.Add(childPidl);
                }
            }

            if (childPidls.Count == 0)
            {
                return false;
            }

            nint* fvVtbl = *(nint**)folderView;

            // IFolderView::GetDefaultSpacing sits at slot 13; GetAutoArrange at
            // slot 14 (returns S_OK == 0 when Auto-Arrange is enabled);
            // SelectAndPositionItems sits at slot 16.
            var getDefaultSpacing = (delegate* unmanaged[Stdcall]<
                nint,
                NativePoint*,
                int>)fvVtbl[13];
            var getAutoArrange = (delegate* unmanaged[Stdcall]<
                nint,
                int>)fvVtbl[14];
            var selectAndPosition = (delegate* unmanaged[Stdcall]<
                nint,
                uint,
                nint*,
                NativePoint*,
                uint,
                int>)fvVtbl[16];

            NativePoint spacing = new() { X = 96, Y = 96 };
            if (getDefaultSpacing(folderView, &spacing) < 0 ||
                spacing.X <= 16 ||
                spacing.Y <= 16)
            {
                spacing = new NativePoint { X = 96, Y = 96 };
            }

            bool autoArrangeEnabled = getAutoArrange(folderView) == 0;
            int itemCount = childPidls.Count;
            nint[] childArray = childPidls.ToArray();
            NativePoint[] points = new NativePoint[itemCount];
            const int columns = 4;
            for (int i = 0; i < itemCount; i++)
            {
                int col = i % columns;
                int row = i / columns;
                points[i] = new NativePoint
                {
                    X = anchorClient.X + (col * spacing.X),
                    Y = anchorClient.Y + (row * spacing.Y),
                };
            }

            uint flags = SvsiSelect | SvsiDeselectOthers | SvsiEnsureVisible;
            if (!autoArrangeEnabled)
            {
                flags |= SvsiPositionItem;
            }

            fixed (nint* pChildren = childArray)
            fixed (NativePoint* pPoints = points)
            {
                hr = selectAndPosition(
                    folderView,
                    (uint)itemCount,
                    pChildren,
                    autoArrangeEnabled ? null : pPoints,
                    flags);
            }

            return hr >= 0;
        }
        finally
        {
            for (int i = 0; i < fullPidls.Count; i++)
            {
                if (fullPidls[i] != 0)
                {
                    CoTaskMemFree(fullPidls[i]);
                }
            }

            if (folderView != 0) Marshal.Release(folderView);
            if (shellView != 0) Marshal.Release(shellView);
            if (shellBrowser != 0) Marshal.Release(shellBrowser);
            if (serviceProvider != 0) Marshal.Release(serviceProvider);
            if (desktopDispatch != 0) Marshal.Release(desktopDispatch);
            if (shellWindows != 0) Marshal.Release(shellWindows);
            if (shouldUninit) CoUninitialize();
        }
    }

    private static string ReadWindowClassName(nint hWnd)
    {
        if (hWnd == 0)
        {
            return string.Empty;
        }

        char* buffer = stackalloc char[256];
        int length = GetClassNameW(hWnd, buffer, 256);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }

    private static string GetFolderDisplayName(string normalizedFolderPath)
    {
        string trimmed = Path.TrimEndingDirectorySeparator(normalizedFolderPath);
        string leaf = Path.GetFileName(trimmed);
        return !string.IsNullOrWhiteSpace(leaf)
            ? leaf
            : normalizedFolderPath;
    }

    private static bool IsExplorerWindowClass(string className) =>
        string.Equals(className, "CabinetWClass", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "ExploreWClass", StringComparison.OrdinalIgnoreCase);

    private static bool IsExplorerNonFolderChromeHierarchy(IReadOnlyList<string> ancestorClasses)
    {
        bool insideShellTab = false;
        bool hasXamlIslandBridge = false;
        for (int i = 0; i < ancestorClasses.Count; i++)
        {
            string cls = ancestorClasses[i];
            if (string.Equals(cls, "ShellTabWindowClass", StringComparison.OrdinalIgnoreCase))
            {
                insideShellTab = true;
            }
            else if (string.Equals(cls, "SysTreeView32", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(cls, "NamespaceTreeControl", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(cls, "Address Band Root", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(cls, "Breadcrumb Parent", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(cls, "ReBarWindow32", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(cls, "ToolbarWindow32", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(cls, "TravelBand", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            else if (cls.EndsWith(".Content.DesktopChildSiteBridge", StringComparison.OrdinalIgnoreCase) ||
                     cls.EndsWith(".InputSite.WindowClass", StringComparison.OrdinalIgnoreCase))
            {
                hasXamlIslandBridge = true;
            }
        }

        return hasXamlIslandBridge && !insideShellTab;
    }

    private static bool IsDesktopHostClass(string className) =>
        string.Equals(className, "Progman", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "WorkerW", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase);

    private static bool IsIgnoredShellChromeClass(string className) =>
        string.Equals(className, "Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "Shell_SecondaryTrayWnd", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "TopLevelWindowForOverflowXamlIsland", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "TaskListThumbnailWnd", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "MultitaskingViewFrame", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, "ForegroundStaging", StringComparison.OrdinalIgnoreCase);
}
