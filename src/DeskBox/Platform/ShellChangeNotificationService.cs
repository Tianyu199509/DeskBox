using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace DeskBox.Platform;

internal enum ShellFolderNotificationKind
{
    Created,
    Deleted,
    Renamed,
    Updated,
    DirectoryReload,
    FolderIconChanged,
}

internal readonly record struct ShellFolderNotification(
    ShellFolderNotificationKind Kind,
    string? Path = null,
    string? OldPath = null);

/// <summary>
/// Shared process-wide Shell change notification listener backed by a single hidden
/// top-level tool window and scoped non-recursive <c>SHChangeNotifyRegister</c> entries.
/// Desktop roots share a non-recursive <c>CSIDL_DESKTOP</c> registration (covering personal
/// and public desktop items plus global desktop refresh broadcasts without subscribing to
/// unrelated drives or <c>%AppData%</c>), while other folder widgets register their own
/// directory PIDL non-recursively. Replaces per-widget
/// <c>Windows.Storage.Search.StorageItemQueryResult</c> broker threads and recursive
/// <c>desktop.ini</c> <see cref="FileSystemWatcher"/> instances.
/// </summary>
internal sealed class ShellChangeNotificationService
{
    public static ShellChangeNotificationService Shared { get; } = new();

    internal const string DesktopRegistrationKey = "::CSIDL_DESKTOP_NONRECURSIVE::";

    private const string WindowClassName = "DeskBoxShellNotifyMessageWindow";
    private const uint ShellNotifyMessage = 0x8000 + 0x0145; // WM_APP + 0x145
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x00000080;

    private const int CsidlDesktop = 0x0000;
    private const int ShcnrfShellLevel = 0x0002;
    private const int ShcnrfNewDelivery = 0x8000;

    internal const int ShcneRenameItem = 0x00000001;
    internal const int ShcneCreate = 0x00000002;
    internal const int ShcneDelete = 0x00000004;
    internal const int ShcneMkDir = 0x00000008;
    internal const int ShcneRmDir = 0x00000010;
    internal const int ShcneUpdateDir = 0x00001000;
    internal const int ShcneUpdateItem = 0x00002000;
    internal const int ShcneRenameFolder = 0x00020000;

    private const int SubscribedShellEvents =
        ShcneRenameItem |
        ShcneCreate |
        ShcneDelete |
        ShcneMkDir |
        ShcneRmDir |
        ShcneUpdateDir |
        ShcneUpdateItem |
        ShcneRenameFolder;

    private const int MaxShellPathChars = 520;

    private static readonly object s_classLock = new();
    private static bool s_windowClassRegistered;

    private readonly object _gate = new();
    private readonly Dictionary<string, RootRegistrationState> _rootRegistrations =
        new(StringComparer.OrdinalIgnoreCase);
    private Subscription[] _subscriptions = [];
    private DispatcherQueue? _hostDispatcher;
    private IntPtr _windowHandle;

    private ShellChangeNotificationService()
    {
    }

    public int ActiveSubscriberCount
    {
        get
        {
            lock (_gate)
            {
                return _subscriptions.Length;
            }
        }
    }

    internal int ActiveRootRegistrationCount
    {
        get
        {
            lock (_gate)
            {
                int count = 0;
                foreach (RootRegistrationState state in _rootRegistrations.Values)
                {
                    if (state.NativeRegistrationId != 0)
                    {
                        count++;
                    }
                }

                return count;
            }
        }
    }

    public bool IsNativeRegistered
    {
        get
        {
            lock (_gate)
            {
                if (_windowHandle == IntPtr.Zero)
                {
                    return false;
                }

                foreach (RootRegistrationState state in _rootRegistrations.Values)
                {
                    if (state.NativeRegistrationId != 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>
    /// Pre-resolves the Shell PIDL for <paramref name="folderPath"/> on a worker thread
    /// so subsequent UI-thread registration never performs synchronous disk or network I/O.
    /// </summary>
    internal PreparedTarget? PrepareTarget(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return null;
        }

        string normalizedRoot = NormalizeWatchedRoot(folderPath);
        if (string.IsNullOrEmpty(normalizedRoot))
        {
            return null;
        }

        bool isDesktopNamespace = IsDesktopNamespaceRoot(normalizedRoot);
        string registrationKey = isDesktopNamespace ? DesktopRegistrationKey : normalizedRoot;

        bool alreadyRegisteredOrStaged;
        lock (_gate)
        {
            alreadyRegisteredOrStaged =
                _rootRegistrations.TryGetValue(registrationKey, out RootRegistrationState? existing) &&
                (existing.NativeRegistrationId != 0 || existing.StagedPidl != IntPtr.Zero);
        }

        if (alreadyRegisteredOrStaged)
        {
            return new PreparedTarget(normalizedRoot, registrationKey, isDesktopNamespace, IntPtr.Zero);
        }

        IntPtr pidl = CreateRegistrationPidl(normalizedRoot, isDesktopNamespace);
        if (pidl == IntPtr.Zero)
        {
            return null;
        }

        return new PreparedTarget(normalizedRoot, registrationKey, isDesktopNamespace, pidl);
    }

    public IDisposable? Subscribe(
        string folderPath,
        DispatcherQueue? dispatcher,
        Action<ShellFolderNotification> callback) =>
        Subscribe(folderPath, dispatcher, callback, preparedTarget: null);

    internal IDisposable? Subscribe(
        string folderPath,
        DispatcherQueue? dispatcher,
        Action<ShellFolderNotification> callback,
        PreparedTarget? preparedTarget)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(callback);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                return null;
            }

            string normalizedRoot = NormalizeWatchedRoot(folderPath);
            if (string.IsNullOrEmpty(normalizedRoot))
            {
                return null;
            }

            bool isDesktopNamespace = IsDesktopNamespaceRoot(normalizedRoot);
            string registrationKey = isDesktopNamespace ? DesktopRegistrationKey : normalizedRoot;
            string rootWithSeparator = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
                ? normalizedRoot
                : normalizedRoot + Path.DirectorySeparatorChar;

            IntPtr candidatePidl = IntPtr.Zero;
            if (preparedTarget is not null &&
                string.Equals(preparedTarget.RegistrationKey, registrationKey, StringComparison.OrdinalIgnoreCase))
            {
                candidatePidl = preparedTarget.DetachPidl();
            }
            else if (dispatcher is not null && !dispatcher.HasThreadAccess)
            {
                bool needsPidl;
                lock (_gate)
                {
                    needsPidl =
                        !_rootRegistrations.TryGetValue(registrationKey, out RootRegistrationState? existing) ||
                        (existing.NativeRegistrationId == 0 && existing.StagedPidl == IntPtr.Zero);
                }

                if (needsPidl)
                {
                    candidatePidl = CreateRegistrationPidl(normalizedRoot, isDesktopNamespace);
                }
            }

            var subscription = new Subscription(
                this,
                normalizedRoot,
                rootWithSeparator,
                registrationKey,
                callback);

            DispatcherQueue? targetDispatcher;
            try
            {
                lock (_gate)
                {
                    if (_subscriptions.Length == 0 || _hostDispatcher is null)
                    {
                        _hostDispatcher = dispatcher;
                    }
                    targetDispatcher = _hostDispatcher;

                    var updated = new Subscription[_subscriptions.Length + 1];
                    Array.Copy(_subscriptions, updated, _subscriptions.Length);
                    updated[^1] = subscription;
                    _subscriptions = updated;

                    if (!_rootRegistrations.TryGetValue(registrationKey, out RootRegistrationState? state))
                    {
                        state = new RootRegistrationState(registrationKey, normalizedRoot, isDesktopNamespace);
                        _rootRegistrations[registrationKey] = state;
                    }

                    state.SubscriberCount++;
                    if (state.NativeRegistrationId == 0 &&
                        state.StagedPidl == IntPtr.Zero &&
                        candidatePidl != IntPtr.Zero)
                    {
                        state.StagedPidl = candidatePidl;
                        candidatePidl = IntPtr.Zero;
                    }
                }
            }
            finally
            {
                if (candidatePidl != IntPtr.Zero)
                {
                    ILFree(candidatePidl);
                }
            }

            if (targetDispatcher is not null)
            {
                if (targetDispatcher.HasThreadAccess)
                {
                    SyncNativeRegistrationsOnUiThread();

                    bool registered;
                    lock (_gate)
                    {
                        registered =
                            _rootRegistrations.TryGetValue(registrationKey, out RootRegistrationState? active) &&
                            active.NativeRegistrationId != 0;
                    }

                    if (!registered)
                    {
                        subscription.Dispose();
                        return null;
                    }
                }
                else if (!targetDispatcher.TryEnqueue(SyncNativeRegistrationsOnUiThread))
                {
                    subscription.Dispose();
                    return null;
                }
            }

            return subscription;
        }
        finally
        {
            preparedTarget?.Dispose();
        }
    }

    private void Unsubscribe(Subscription subscription)
    {
        DispatcherQueue? targetDispatcher = null;
        bool needsUiSync = false;
        IntPtr abandonedStagedPidl = IntPtr.Zero;

        lock (_gate)
        {
            int index = Array.IndexOf(_subscriptions, subscription);
            if (index < 0)
            {
                return;
            }

            if (_subscriptions.Length == 1)
            {
                _subscriptions = [];
            }
            else
            {
                var updated = new Subscription[_subscriptions.Length - 1];
                if (index > 0)
                {
                    Array.Copy(_subscriptions, 0, updated, 0, index);
                }

                if (index < _subscriptions.Length - 1)
                {
                    Array.Copy(_subscriptions, index + 1, updated, index, _subscriptions.Length - index - 1);
                }

                _subscriptions = updated;
            }

            if (_rootRegistrations.TryGetValue(subscription.RegistrationKey, out RootRegistrationState? state))
            {
                state.SubscriberCount--;
                if (state.SubscriberCount <= 0)
                {
                    if (state.NativeRegistrationId == 0)
                    {
                        abandonedStagedPidl = state.StagedPidl;
                        state.StagedPidl = IntPtr.Zero;
                        _ = _rootRegistrations.Remove(subscription.RegistrationKey);
                    }
                    else
                    {
                        needsUiSync = true;
                    }
                }
            }

            if (_subscriptions.Length == 0 && _windowHandle != IntPtr.Zero)
            {
                needsUiSync = true;
            }

            if (needsUiSync)
            {
                targetDispatcher = _hostDispatcher;
            }
        }

        if (abandonedStagedPidl != IntPtr.Zero)
        {
            ILFree(abandonedStagedPidl);
        }

        if (needsUiSync && targetDispatcher is not null)
        {
            if (targetDispatcher.HasThreadAccess)
            {
                SyncNativeRegistrationsOnUiThread();
            }
            else
            {
                _ = targetDispatcher.TryEnqueue(SyncNativeRegistrationsOnUiThread);
            }
        }
    }

    private void SyncNativeRegistrationsOnUiThread()
    {
        List<uint>? idsToDeregister = null;
        List<IntPtr>? pidlsToFree = null;
        List<(string Key, string SampleRoot, bool IsDesktopNamespace, IntPtr TakenPidl)>? pendingRegistrations = null;

        lock (_gate)
        {
            List<string>? deadKeys = null;
            foreach (KeyValuePair<string, RootRegistrationState> pair in _rootRegistrations)
            {
                RootRegistrationState state = pair.Value;
                if (state.SubscriberCount <= 0)
                {
                    deadKeys ??= [];
                    deadKeys.Add(pair.Key);
                    if (state.NativeRegistrationId != 0)
                    {
                        idsToDeregister ??= [];
                        idsToDeregister.Add(state.NativeRegistrationId);
                        state.NativeRegistrationId = 0;
                    }

                    if (state.StagedPidl != IntPtr.Zero)
                    {
                        pidlsToFree ??= [];
                        pidlsToFree.Add(state.StagedPidl);
                        state.StagedPidl = IntPtr.Zero;
                    }
                }
                else if (state.NativeRegistrationId == 0)
                {
                    pendingRegistrations ??= [];
                    IntPtr takenPidl = state.StagedPidl;
                    state.StagedPidl = IntPtr.Zero;
                    pendingRegistrations.Add((
                        state.RegistrationKey,
                        state.SampleNormalizedRoot,
                        state.IsDesktopNamespace,
                        takenPidl));
                }
            }

            if (deadKeys is not null)
            {
                for (int i = 0; i < deadKeys.Count; i++)
                {
                    _ = _rootRegistrations.Remove(deadKeys[i]);
                }
            }
        }

        if (idsToDeregister is not null)
        {
            for (int i = 0; i < idsToDeregister.Count; i++)
            {
                _ = SHChangeNotifyDeregister(idsToDeregister[i]);
            }
        }

        if (pidlsToFree is not null)
        {
            for (int i = 0; i < pidlsToFree.Count; i++)
            {
                ILFree(pidlsToFree[i]);
            }
        }

        if (pendingRegistrations is { Count: > 0 })
        {
            IntPtr hwnd = EnsureHostWindowOnUiThread();
            if (hwnd == IntPtr.Zero)
            {
                for (int i = 0; i < pendingRegistrations.Count; i++)
                {
                    if (pendingRegistrations[i].TakenPidl != IntPtr.Zero)
                    {
                        ILFree(pendingRegistrations[i].TakenPidl);
                    }
                }
            }
            else
            {
                for (int i = 0; i < pendingRegistrations.Count; i++)
                {
                    (string key, string sampleRoot, bool isDesktopNamespace, IntPtr takenPidl) =
                        pendingRegistrations[i];
                    IntPtr pidl = takenPidl != IntPtr.Zero
                        ? takenPidl
                        : CreateRegistrationPidl(sampleRoot, isDesktopNamespace);
                    try
                    {
                        if (pidl == IntPtr.Zero)
                        {
                            App.Log($"[ShellChangeNotify] Failed to create PIDL for '{sampleRoot}'");
                            continue;
                        }

                        SHChangeNotifyEntry entry = new()
                        {
                            pidl = pidl,
                            fRecursive = false,
                        };

                        uint registrationId = SHChangeNotifyRegister(
                            hwnd,
                            ShcnrfShellLevel | ShcnrfNewDelivery,
                            SubscribedShellEvents,
                            ShellNotifyMessage,
                            1,
                            ref entry);
                        if (registrationId == 0)
                        {
                            App.Log(
                                $"[ShellChangeNotify] SHChangeNotifyRegister returned 0 for '{sampleRoot}' " +
                                $"(err={Marshal.GetLastWin32Error()})");
                            continue;
                        }

                        bool racedOut = false;
                        lock (_gate)
                        {
                            if (_windowHandle == hwnd &&
                                _rootRegistrations.TryGetValue(key, out RootRegistrationState? active) &&
                                active.SubscriberCount > 0 &&
                                active.NativeRegistrationId == 0)
                            {
                                active.NativeRegistrationId = registrationId;
                            }
                            else
                            {
                                racedOut = true;
                            }
                        }

                        if (racedOut)
                        {
                            _ = SHChangeNotifyDeregister(registrationId);
                        }
                    }
                    finally
                    {
                        if (pidl != IntPtr.Zero)
                        {
                            ILFree(pidl);
                        }
                    }
                }
            }
        }

        IntPtr hwndToDestroy = IntPtr.Zero;
        List<IntPtr>? leftoverPidls = null;
        lock (_gate)
        {
            bool anyActiveRegistration = false;
            foreach (RootRegistrationState state in _rootRegistrations.Values)
            {
                if (state.NativeRegistrationId != 0)
                {
                    anyActiveRegistration = true;
                    break;
                }
            }

            if (_subscriptions.Length == 0 && !anyActiveRegistration)
            {
                foreach (RootRegistrationState state in _rootRegistrations.Values)
                {
                    if (state.StagedPidl != IntPtr.Zero)
                    {
                        leftoverPidls ??= [];
                        leftoverPidls.Add(state.StagedPidl);
                        state.StagedPidl = IntPtr.Zero;
                    }
                }

                _rootRegistrations.Clear();
                hwndToDestroy = _windowHandle;
                _windowHandle = IntPtr.Zero;
                _hostDispatcher = null;
            }
        }

        if (leftoverPidls is not null)
        {
            for (int i = 0; i < leftoverPidls.Count; i++)
            {
                ILFree(leftoverPidls[i]);
            }
        }

        if (hwndToDestroy != IntPtr.Zero)
        {
            _ = DestroyWindow(hwndToDestroy);
        }
    }

    private IntPtr EnsureHostWindowOnUiThread()
    {
        lock (_gate)
        {
            if (_windowHandle != IntPtr.Zero)
            {
                return _windowHandle;
            }
        }

        EnsureWindowClassRegistered();
        IntPtr hwnd = CreateWindowExW(
            WsExToolWindow,
            WindowClassName,
            "DeskBoxShellChangeHost",
            WsPopup,
            0,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandleW(null),
            IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            App.Log($"[ShellChangeNotify] Failed to create host window (err={Marshal.GetLastWin32Error()})");
            return IntPtr.Zero;
        }

        IntPtr extraHwnd = IntPtr.Zero;
        lock (_gate)
        {
            if (_windowHandle == IntPtr.Zero)
            {
                _windowHandle = hwnd;
            }
            else
            {
                extraHwnd = hwnd;
                hwnd = _windowHandle;
            }
        }

        if (extraHwnd != IntPtr.Zero)
        {
            _ = DestroyWindow(extraHwnd);
        }

        return hwnd;
    }

    private static IntPtr CreateRegistrationPidl(string normalizedRoot, bool isDesktopNamespace)
    {
        if (isDesktopNamespace)
        {
            int hr = SHGetSpecialFolderLocation(IntPtr.Zero, CsidlDesktop, out IntPtr desktopPidl);
            if (hr == 0 && desktopPidl != IntPtr.Zero)
            {
                return desktopPidl;
            }
        }

        return ILCreateFromPathW(normalizedRoot);
    }

    internal static string GetRegistrationKey(string folderPath)
    {
        string normalizedRoot = NormalizeWatchedRoot(folderPath);
        if (string.IsNullOrEmpty(normalizedRoot))
        {
            return string.Empty;
        }

        return IsDesktopNamespaceRoot(normalizedRoot) ? DesktopRegistrationKey : normalizedRoot;
    }

    internal static bool IsDesktopNamespaceRoot(string normalizedRoot)
    {
        if (string.IsNullOrEmpty(normalizedRoot))
        {
            return false;
        }

        string userDesktop = NormalizeWatchedRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
        if (!string.IsNullOrEmpty(userDesktop) &&
            string.Equals(normalizedRoot, userDesktop, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string publicDesktop = NormalizeWatchedRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
        return !string.IsNullOrEmpty(publicDesktop) &&
               string.Equals(normalizedRoot, publicDesktop, StringComparison.OrdinalIgnoreCase);
    }

    private unsafe void OnShellNotificationMessage(UIntPtr wParam, IntPtr lParam)
    {
        Subscription[] snapshot = Volatile.Read(ref _subscriptions);
        if (snapshot.Length == 0)
        {
            return;
        }

        IntPtr hLock = SHChangeNotification_Lock(
            (IntPtr)(ulong)wParam,
            (uint)((ulong)lParam & 0xFFFFFFFF),
            out IntPtr pppidl,
            out int rawEvent);
        if (hLock == IntPtr.Zero || pppidl == IntPtr.Zero)
        {
            if (hLock != IntPtr.Zero)
            {
                _ = SHChangeNotification_Unlock(hLock);
            }

            return;
        }

        try
        {
            int eventMask = rawEvent & SubscribedShellEvents;
            if (eventMask == 0)
            {
                return;
            }

            IntPtr pidl1 = Marshal.ReadIntPtr(pppidl, 0);
            IntPtr pidl2 = Marshal.ReadIntPtr(pppidl, IntPtr.Size);

            char* buf1 = stackalloc char[MaxShellPathChars];
            char* buf2 = stackalloc char[MaxShellPathChars];

            ReadOnlySpan<char> path1 = TryReadPidlPath(pidl1, buf1, MaxShellPathChars);
            ReadOnlySpan<char> path2 = (eventMask & (ShcneRenameItem | ShcneRenameFolder)) != 0
                ? TryReadPidlPath(pidl2, buf2, MaxShellPathChars)
                : ReadOnlySpan<char>.Empty;

            if (path1.IsEmpty && path2.IsEmpty)
            {
                return;
            }

            for (int i = 0; i < snapshot.Length; i++)
            {
                Subscription sub = snapshot[i];
                if (!sub.IsActive)
                {
                    continue;
                }

                if (TryClassifyShellEvent(
                        sub.NormalizedRoot,
                        sub.RootWithSeparator,
                        eventMask,
                        path1,
                        path2,
                        out ShellFolderNotification primary,
                        out ShellFolderNotification? secondary))
                {
                    sub.Deliver(primary);
                    if (secondary is ShellFolderNotification extra)
                    {
                        sub.Deliver(extra);
                    }
                }
            }
        }
        finally
        {
            _ = SHChangeNotification_Unlock(hLock);
        }
    }

    private static unsafe ReadOnlySpan<char> TryReadPidlPath(IntPtr pidl, char* buffer, uint bufferChars)
    {
        if (pidl == IntPtr.Zero)
        {
            return ReadOnlySpan<char>.Empty;
        }

        buffer[0] = '\0';
        if (!SHGetPathFromIDListEx(pidl, buffer, bufferChars, 0))
        {
            return ReadOnlySpan<char>.Empty;
        }

        return MemoryMarshal.CreateReadOnlySpanFromNullTerminated(buffer);
    }

    internal static string NormalizeWatchedRoot(string folderPath)
    {
        try
        {
            string full = Path.GetFullPath(folderPath.Trim());
            string? pathRoot = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(pathRoot) &&
                string.Equals(full, pathRoot, StringComparison.OrdinalIgnoreCase))
            {
                return full;
            }

            return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static bool TryClassifyShellEvent(
        string normalizedRoot,
        string rootWithSeparator,
        int eventMask,
        ReadOnlySpan<char> rawPath1,
        ReadOnlySpan<char> rawPath2,
        out ShellFolderNotification primary,
        out ShellFolderNotification? secondary)
    {
        primary = default;
        secondary = null;

        PathMatchKind match1 = MatchPathAgainstRoot(
            normalizedRoot,
            rootWithSeparator,
            rawPath1,
            out ReadOnlySpan<char> child1Span);

        if ((eventMask & (ShcneRenameItem | ShcneRenameFolder)) != 0)
        {
            PathMatchKind match2 = MatchPathAgainstRoot(
                normalizedRoot,
                rootWithSeparator,
                rawPath2,
                out ReadOnlySpan<char> child2Span);

            if (match1 == PathMatchKind.WatchedRoot || match2 == PathMatchKind.WatchedRoot)
            {
                primary = new ShellFolderNotification(ShellFolderNotificationKind.DirectoryReload);
                return true;
            }

            bool isDirect1 = match1 == PathMatchKind.DirectChild;
            bool isDirect2 = match2 == PathMatchKind.DirectChild;
            if (isDirect1 && isDirect2)
            {
                primary = new ShellFolderNotification(
                    ShellFolderNotificationKind.Renamed,
                    Path: BuildChildPath(rootWithSeparator, child2Span),
                    OldPath: BuildChildPath(rootWithSeparator, child1Span));
                return true;
            }

            if (isDirect1)
            {
                primary = new ShellFolderNotification(
                    ShellFolderNotificationKind.Deleted,
                    Path: BuildChildPath(rootWithSeparator, child1Span));
                return true;
            }

            if (isDirect2)
            {
                primary = new ShellFolderNotification(
                    ShellFolderNotificationKind.Created,
                    Path: BuildChildPath(rootWithSeparator, child2Span));
                return true;
            }

            if (match1 == PathMatchKind.ChildDesktopIni || match2 == PathMatchKind.ChildDesktopIni)
            {
                ReadOnlySpan<char> iconFolderSpan = match2 == PathMatchKind.ChildDesktopIni ? child2Span : child1Span;
                primary = new ShellFolderNotification(
                    ShellFolderNotificationKind.FolderIconChanged,
                    Path: BuildChildPath(rootWithSeparator, iconFolderSpan));
                return true;
            }

            return false;
        }

        if (match1 == PathMatchKind.None)
        {
            return false;
        }

        if (match1 == PathMatchKind.WatchedRoot)
        {
            if ((eventMask & (ShcneUpdateDir | ShcneUpdateItem | ShcneRmDir | ShcneDelete)) != 0)
            {
                primary = new ShellFolderNotification(ShellFolderNotificationKind.DirectoryReload);
                return true;
            }

            return false;
        }

        if (match1 == PathMatchKind.ChildDesktopIni)
        {
            primary = new ShellFolderNotification(
                ShellFolderNotificationKind.FolderIconChanged,
                Path: BuildChildPath(rootWithSeparator, child1Span));
            return true;
        }

        // match1 == PathMatchKind.DirectChild
        string childFullPath = BuildChildPath(rootWithSeparator, child1Span);
        if ((eventMask & (ShcneCreate | ShcneMkDir)) != 0)
        {
            primary = new ShellFolderNotification(ShellFolderNotificationKind.Created, Path: childFullPath);
            return true;
        }

        if ((eventMask & (ShcneDelete | ShcneRmDir)) != 0)
        {
            primary = new ShellFolderNotification(ShellFolderNotificationKind.Deleted, Path: childFullPath);
            return true;
        }

        if ((eventMask & ShcneUpdateDir) != 0)
        {
            primary = new ShellFolderNotification(ShellFolderNotificationKind.FolderIconChanged, Path: childFullPath);
            return true;
        }

        if ((eventMask & ShcneUpdateItem) != 0)
        {
            primary = new ShellFolderNotification(ShellFolderNotificationKind.Updated, Path: childFullPath);
            return true;
        }

        return false;
    }

    private enum PathMatchKind
    {
        None,
        WatchedRoot,
        DirectChild,
        ChildDesktopIni,
    }

    private static PathMatchKind MatchPathAgainstRoot(
        string normalizedRoot,
        string rootWithSeparator,
        ReadOnlySpan<char> candidate,
        out ReadOnlySpan<char> directChildName)
    {
        directChildName = default;
        if (candidate.IsEmpty)
        {
            return PathMatchKind.None;
        }

        ReadOnlySpan<char> trimmed = candidate;
        if (trimmed.Length > normalizedRoot.Length || !normalizedRoot.EndsWith(Path.DirectorySeparatorChar))
        {
            trimmed = trimmed.TrimEnd(['\\', '/']);
        }

        if (trimmed.Equals(normalizedRoot.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return PathMatchKind.WatchedRoot;
        }

        if (!trimmed.StartsWith(rootWithSeparator.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return PathMatchKind.None;
        }

        ReadOnlySpan<char> remainder = trimmed[rootWithSeparator.Length..];
        if (remainder.IsEmpty)
        {
            return PathMatchKind.WatchedRoot;
        }

        int separatorIndex = remainder.IndexOfAny('\\', '/');
        if (separatorIndex < 0)
        {
            directChildName = remainder;
            return PathMatchKind.DirectChild;
        }

        ReadOnlySpan<char> childFolder = remainder[..separatorIndex];
        ReadOnlySpan<char> nestedRest = remainder[(separatorIndex + 1)..];
        if (!childFolder.IsEmpty &&
            nestedRest.Equals("desktop.ini".AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            directChildName = childFolder;
            return PathMatchKind.ChildDesktopIni;
        }

        return PathMatchKind.None;
    }

    private static string BuildChildPath(string rootWithSeparator, ReadOnlySpan<char> childName)
    {
        return string.Concat(rootWithSeparator, childName);
    }

    private static unsafe void EnsureWindowClassRegistered()
    {
        lock (s_classLock)
        {
            if (s_windowClassRegistered)
            {
                return;
            }

            IntPtr hInstance = GetModuleHandleW(null);
            fixed (char* classNamePtr = WindowClassName)
            {
                WNDCLASSEXW wc = new()
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, UIntPtr, IntPtr, IntPtr>)&ShellNotifyWindowProc,
                    hInstance = hInstance,
                    lpszClassName = (IntPtr)classNamePtr,
                };
                ushort atom = RegisterClassExW(&wc);
                if (atom != 0 || Marshal.GetLastWin32Error() == 1410)
                {
                    s_windowClassRegistered = true;
                }
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static IntPtr ShellNotifyWindowProc(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (msg == ShellNotifyMessage)
        {
            try
            {
                Shared.OnShellNotificationMessage(wParam, lParam);
            }
            catch (Exception ex)
            {
                App.Log($"[ShellChangeNotify] Message dispatch failed: {ex.Message}");
            }

            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    internal sealed class PreparedTarget : IDisposable
    {
        private IntPtr _pidl;

        internal PreparedTarget(
            string normalizedRoot,
            string registrationKey,
            bool isDesktopNamespace,
            IntPtr pidl)
        {
            NormalizedRoot = normalizedRoot;
            RegistrationKey = registrationKey;
            IsDesktopNamespace = isDesktopNamespace;
            _pidl = pidl;
        }

        public string NormalizedRoot { get; }
        public string RegistrationKey { get; }
        public bool IsDesktopNamespace { get; }

        internal IntPtr DetachPidl() => Interlocked.Exchange(ref _pidl, IntPtr.Zero);

        public void Dispose()
        {
            IntPtr pidl = Interlocked.Exchange(ref _pidl, IntPtr.Zero);
            if (pidl != IntPtr.Zero)
            {
                ILFree(pidl);
            }
        }
    }

    private sealed class RootRegistrationState(
        string registrationKey,
        string sampleNormalizedRoot,
        bool isDesktopNamespace)
    {
        public string RegistrationKey { get; } = registrationKey;
        public string SampleNormalizedRoot { get; } = sampleNormalizedRoot;
        public bool IsDesktopNamespace { get; } = isDesktopNamespace;
        public int SubscriberCount { get; set; }
        public uint NativeRegistrationId { get; set; }
        public IntPtr StagedPidl { get; set; }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ShellChangeNotificationService _owner;
        private readonly Action<ShellFolderNotification> _callback;
        private int _disposed;

        public Subscription(
            ShellChangeNotificationService owner,
            string normalizedRoot,
            string rootWithSeparator,
            string registrationKey,
            Action<ShellFolderNotification> callback)
        {
            _owner = owner;
            NormalizedRoot = normalizedRoot;
            RootWithSeparator = rootWithSeparator;
            RegistrationKey = registrationKey;
            _callback = callback;
        }

        public string NormalizedRoot { get; }
        public string RootWithSeparator { get; }
        public string RegistrationKey { get; }
        public bool IsActive => Volatile.Read(ref _disposed) == 0;

        public void Deliver(ShellFolderNotification notification)
        {
            if (!IsActive)
            {
                return;
            }

            try
            {
                _callback(notification);
            }
            catch (Exception ex)
            {
                App.Log($"[ShellChangeNotify] Subscriber callback failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Unsubscribe(this);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SHChangeNotifyEntry
    {
        public IntPtr pidl;
        [MarshalAs(UnmanagedType.Bool)]
        public bool fRecursive;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public IntPtr lpszMenuName;
        public IntPtr lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern unsafe ushort RegisterClassExW(WNDCLASSEXW* lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll")]
    private static extern int SHGetSpecialFolderLocation(IntPtr hwndReserve, int nFolder, out IntPtr ppidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ILCreateFromPathW(string pszPath);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern uint SHChangeNotifyRegister(
        IntPtr hwnd,
        int fSources,
        int fEvents,
        uint wMsg,
        int cEntries,
        ref SHChangeNotifyEntry pshcne);

    [DllImport("shell32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHChangeNotifyDeregister(uint ulID);

    [DllImport("shell32.dll")]
    private static extern IntPtr SHChangeNotification_Lock(
        IntPtr hChange,
        uint dwProcId,
        out IntPtr pppidl,
        out int plEvent);

    [DllImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHChangeNotification_Unlock(IntPtr hLock);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool SHGetPathFromIDListEx(
        IntPtr pidl,
        char* pszPath,
        uint cchPath,
        uint uOpts);
}
