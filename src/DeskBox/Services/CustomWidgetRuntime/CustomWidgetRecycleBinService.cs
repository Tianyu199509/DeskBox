using System.Globalization;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using Microsoft.UI.Dispatching;

namespace DeskBox.Services;

internal sealed record CustomWidgetRecycleBinState(
    long ItemCount,
    byte[]? EmptyIconPng,
    byte[]? FullIconPng)
{
    public static CustomWidgetRecycleBinState Empty { get; } = new(0, null, null);

    public bool IsEmpty => ItemCount <= 0;

    public string CountBadgeText => ItemCount switch
    {
        <= 0 => string.Empty,
        > 999 => "999+",
        _ => ItemCount.ToString(CultureInfo.InvariantCulture)
    };
}

internal sealed record CustomWidgetRecycleBatchOutcome(
    int RecycledCount,
    int BlockedCount,
    int FailedCount,
    IReadOnlyList<(string ParentDirectory, string ItemName)> RecycledItems);

/// <summary>
/// Host bridge for safe Custom Widget desktop integrations (Search Popup, Desktop Toast HUD,
/// and Shell Recycle Bin deletion/restoration) configured once by the application host at startup
/// so Views, Controls, ViewModels, and Services stay decoupled from global application singletons.
/// </summary>
internal static class CustomWidgetHostActionBridge
{
    public static Action<string?>? OpenSearchPopupHandler { get; set; }

    public static Func<DesktopToastRequest, bool>? ShowDesktopToastHandler { get; set; }

    public static Func<string, IntPtr, Task<bool>>? RecycleEntryHandler { get; set; }

    public static Func<string, string, bool>? RestoreEntryHandler { get; set; }

    public static Func<string, bool>? CanRecyclePathOverride { get; set; }

    public static bool RequestOpenSearchPopup(string? initialQuery = null)
    {
        if (OpenSearchPopupHandler is not { } handler)
        {
            return false;
        }

        handler(string.IsNullOrWhiteSpace(initialQuery) ? null : initialQuery.Trim());
        return true;
    }

    public static bool TryShowDesktopToast(DesktopToastRequest request) =>
        ShowDesktopToastHandler?.Invoke(request) ?? false;
}

/// <summary>
/// Ref-counted, event-driven Recycle Bin state projection and undoable drag-delete coordinator
/// for <c>RecycleBin</c> Custom Widget cards. Uses <c>SHChangeNotifyRegister</c> on the Recycle Bin
/// namespace PIDL (zero polling) while at least one card is mounted, and tears down cleanly when idle.
/// </summary>
internal sealed class CustomWidgetRecycleBinService
{
    public static CustomWidgetRecycleBinService Shared { get; } = new();

    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private Timer? _debounceTimer;
    private ShellRecycleBinInterop.RecycleBinChangeWatcherHandle _watcherHandle;
    private DispatcherQueue? _watcherDispatcher;
    private CustomWidgetRecycleBinState _current = CustomWidgetRecycleBinState.Empty;
    private bool _hasQueriedOnce;
    private byte[]? _cachedEmptyIconPng;
    private byte[]? _cachedFullIconPng;
    private int _queryInFlight;

    public CustomWidgetRecycleBinState Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public bool TryGetLiveSubscribedItemCount(out long count)
    {
        lock (_gate)
        {
            if (_subscriptions.Count > 0 && _hasQueriedOnce)
            {
                count = _current.ItemCount;
                return true;
            }
        }

        count = 0;
        return false;
    }

    public IDisposable Subscribe(
        Action<CustomWidgetRecycleBinState> onStateChanged,
        DispatcherQueue? dispatcherQueue = null,
        bool attachNativeWatcher = true)
    {
        ArgumentNullException.ThrowIfNull(onStateChanged);

        var sub = new Subscription(this, onStateChanged, dispatcherQueue);
        CustomWidgetRecycleBinState? snapshotToDeliver = null;
        bool shouldStartWatcher = false;
        bool shouldQueryInitial = false;

        lock (_gate)
        {
            _subscriptions.Add(sub);
            if (_hasQueriedOnce)
            {
                snapshotToDeliver = _current;
            }
            else
            {
                shouldQueryInitial = true;
            }

            if (attachNativeWatcher && !_watcherHandle.IsValid)
            {
                shouldStartWatcher = true;
            }
        }

        if (shouldStartWatcher)
        {
            if (dispatcherQueue is not null && !dispatcherQueue.HasThreadAccess)
            {
                _ = dispatcherQueue.TryEnqueue(EnsureNativeWatcherOnCallerThread);
            }
            else
            {
                EnsureNativeWatcherOnCallerThread();
            }
        }

        if (snapshotToDeliver is not null)
        {
            sub.Deliver(snapshotToDeliver);
        }

        if (shouldQueryInitial)
        {
            RefreshNow();
        }

        return sub;
    }

    public void NotifyShellChanged()
    {
        lock (_gate)
        {
            if (_subscriptions.Count == 0)
            {
                return;
            }

            _debounceTimer ??= new Timer(
                static state => ((CustomWidgetRecycleBinService)state!).QueryInBackground(),
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _debounceTimer.Change(TimeSpan.FromMilliseconds(320), Timeout.InfiniteTimeSpan);
        }
    }

    public void RefreshNow()
    {
        _ = Task.Run(QueryInBackground);
    }

    public async Task<bool> EmptyAllBinsConfirmedAsync(IntPtr ownerHwnd)
    {
        bool emptied = await Task.Run(() => ShellRecycleBinInterop.TryEmptyAllBinsConfirmed(ownerHwnd))
            .ConfigureAwait(false);
        if (emptied)
        {
            PublishCountOverride(0);
            RefreshNow();
        }

        return emptied;
    }

    internal void PublishCountOverride(long itemCount)
    {
        List<Subscription> targets;
        CustomWidgetRecycleBinState next;
        lock (_gate)
        {
            next = new CustomWidgetRecycleBinState(
                Math.Max(0, itemCount),
                _cachedEmptyIconPng,
                _cachedFullIconPng);
            _current = next;
            _hasQueriedOnce = true;
            targets = [.. _subscriptions];
        }

        foreach (Subscription sub in targets)
        {
            sub.Deliver(next);
        }
    }

    public async Task<CustomWidgetRecycleBatchOutcome> RecycleDroppedFilesWithUndoAsync(
        IReadOnlyList<string> rawPaths,
        IntPtr ownerHwnd,
        string? cultureName = null)
    {
        if (rawPaths is not { Count: > 0 })
        {
            return new CustomWidgetRecycleBatchOutcome(0, 0, 0, []);
        }

        string culture = string.IsNullOrWhiteSpace(cultureName)
            ? LocalizationService.ResolveAmbientCultureName()
            : cultureName;

        var recycledItems = new List<(string ParentDirectory, string ItemName)>(rawPaths.Count);
        int blockedCount = 0;
        int failedCount = 0;
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Func<string, IntPtr, Task<bool>> deleteHandler = CustomWidgetHostActionBridge.RecycleEntryHandler
            ?? (static (path, hwnd) => new FileService().DeleteEntryAsync(path, recycle: true, ownerHandle: hwnd));

        foreach (string rawPath in rawPaths)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                continue;
            }

            string trimmed = CustomWidgetDropZoneCoordinator.TryUnwrapFileUriToLocalPath(rawPath.Trim());
            if (string.IsNullOrWhiteSpace(trimmed) ||
                CustomWidgetNetworkSecurity.IsBlockedUncOrRemoteFileUri(trimmed) ||
                DropStagingFileOps.IsUnderVirtualDropsDirectory(trimmed))
            {
                blockedCount++;
                continue;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(trimmed);
            }
            catch
            {
                failedCount++;
                continue;
            }

            if (!seenPaths.Add(fullPath))
            {
                continue;
            }

            bool existsBefore = File.Exists(fullPath) || Directory.Exists(fullPath);
            if (!existsBefore)
            {
                failedCount++;
                continue;
            }

            if (IsProtectedFromWidgetRecycle(fullPath))
            {
                blockedCount++;
                continue;
            }

            bool canRecycle = CustomWidgetHostActionBridge.CanRecyclePathOverride?.Invoke(fullPath)
                ?? (!FileService.IsNetworkVolumePath(fullPath) &&
                    FileOperationNativeMethods.CanHeadlessRecyclePath(fullPath));
            if (!canRecycle)
            {
                blockedCount++;
                continue;
            }

            string normalizedTrimmed = Path.TrimEndingDirectorySeparator(fullPath);
            string? parentDir = Path.GetDirectoryName(normalizedTrimmed);
            string itemName = Path.GetFileName(normalizedTrimmed);
            if (string.IsNullOrWhiteSpace(parentDir) || string.IsNullOrWhiteSpace(itemName))
            {
                blockedCount++;
                continue;
            }

            try
            {
                bool deleted = await deleteHandler(fullPath, ownerHwnd).ConfigureAwait(false);
                bool goneAfter = !File.Exists(fullPath) && !Directory.Exists(fullPath);
                if (deleted && (goneAfter || CustomWidgetHostActionBridge.RecycleEntryHandler is not null))
                {
                    recycledItems.Add((parentDir, itemName));
                }
                else
                {
                    failedCount++;
                }
            }
            catch (Exception ex)
            {
                App.Log($"[CustomWidget.RecycleBin] DeleteEntryAsync failed for '{fullPath}': {ex.Message}");
                failedCount++;
            }
        }

        if (recycledItems.Count > 0)
        {
            long optimisticCount;
            lock (_gate)
            {
                optimisticCount = Math.Max(0, _current.ItemCount) + recycledItems.Count;
            }
            PublishCountOverride(optimisticCount);
            RefreshNow();

            string toastMsg = recycledItems.Count == 1
                ? LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.RecycleBin.ToastMovedSingle",
                    recycledItems[0].ItemName)
                : LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.RecycleBin.ToastMovedMultiple",
                    recycledItems.Count);
            string undoLabel = LocalizationService.TranslateForCulture(
                culture,
                "CustomWidget.RecycleBin.UndoAction");

            IReadOnlyList<(string ParentDirectory, string ItemName)> batchSnapshot = recycledItems.ToArray();
            CustomWidgetHostActionBridge.TryShowDesktopToast(new DesktopToastRequest(
                HintId: string.Empty,
                Message: toastMsg,
                Severity: DesktopToastSeverity.Info,
                Priority: DesktopToastPriority.Routine,
                ActionButtonText: undoLabel,
                OnActionAsync: () => RestoreBatchAsync(batchSnapshot, culture),
                DurationMs: 5500));
        }
        else if (blockedCount > 0)
        {
            string warnMsg = LocalizationService.TranslateForCulture(
                culture,
                "CustomWidget.RecycleBin.BlockedNoBinWarning");
            CustomWidgetHostActionBridge.TryShowDesktopToast(new DesktopToastRequest(
                HintId: string.Empty,
                Message: warnMsg,
                Severity: DesktopToastSeverity.Warning,
                Priority: DesktopToastPriority.SafetyWarning,
                DurationMs: 4500));
        }

        return new CustomWidgetRecycleBatchOutcome(
            recycledItems.Count,
            blockedCount,
            failedCount,
            recycledItems);
    }

    internal async Task<int> RestoreBatchAsync(
        IReadOnlyList<(string ParentDirectory, string ItemName)> items,
        string culture)
    {
        if (items is not { Count: > 0 })
        {
            return 0;
        }

        Func<string, string, bool> restoreHandler = CustomWidgetHostActionBridge.RestoreEntryHandler
            ?? (static (parent, name) =>
                RecycleBinNativeBackend.Invoke(RecycleBinNativeOperation.Restore, parent, name).Success);

        int restored = await Task.Run(() =>
        {
            int okCount = 0;
            for (int i = items.Count - 1; i >= 0; i--)
            {
                (string parent, string name) = items[i];
                try
                {
                    if (restoreHandler(parent, name))
                    {
                        okCount++;
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[CustomWidget.RecycleBin] Restore failed for '{parent}\\{name}': {ex.Message}");
                }
            }

            return okCount;
        }).ConfigureAwait(false);

        if (restored > 0)
        {
            long nextCount;
            lock (_gate)
            {
                nextCount = Math.Max(0, _current.ItemCount - restored);
            }
            PublishCountOverride(nextCount);
            RefreshNow();

            string restoredMsg = restored == 1 && items.Count == 1
                ? LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.RecycleBin.ToastRestoredSingle",
                    items[0].ItemName)
                : LocalizationService.FormatForCulture(
                    culture,
                    "CustomWidget.RecycleBin.ToastRestoredMultiple",
                    restored);
            CustomWidgetHostActionBridge.TryShowDesktopToast(new DesktopToastRequest(
                HintId: string.Empty,
                Message: restoredMsg,
                Severity: DesktopToastSeverity.Success,
                Priority: DesktopToastPriority.Routine,
                DurationMs: 3600));
        }

        return restored;
    }

    private static bool IsProtectedFromWidgetRecycle(string fullPath)
    {
        if (DragDrop.StoreVirtualizedPathPolicy.IsWriteBlockedDestination(fullPath))
        {
            return true;
        }

        try
        {
            string dataDir = DeskBoxDataPathService.Current.DataDirectory;
            if (!string.IsNullOrWhiteSpace(dataDir) && IsInsideRoot(fullPath, dataDir))
            {
                return true;
            }
        }
        catch
        {
        }

        try
        {
            string baseDir = AppContext.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDir) && IsInsideRoot(fullPath, baseDir))
            {
                return true;
            }
        }
        catch
        {
        }

        ReadOnlySpan<Environment.SpecialFolder> systemRoots =
        [
            Environment.SpecialFolder.Windows,
            Environment.SpecialFolder.System,
            Environment.SpecialFolder.SystemX86,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86
        ];

        foreach (Environment.SpecialFolder folder in systemRoots)
        {
            string root = Environment.GetFolderPath(folder);
            if (!string.IsNullOrWhiteSpace(root) && IsInsideRoot(fullPath, root))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInsideRoot(string candidateFullPath, string rootPath)
    {
        string normRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        string normCandidate = Path.TrimEndingDirectorySeparator(candidateFullPath);
        return normCandidate.StartsWith(normRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(normCandidate, normRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void QueryInBackground()
    {
        if (Interlocked.Exchange(ref _queryInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            byte[]? emptyPng;
            byte[]? fullPng;
            lock (_gate)
            {
                emptyPng = _cachedEmptyIconPng;
                fullPng = _cachedFullIconPng;
            }

            emptyPng ??= ShellRecycleBinInterop.ExtractRecycleBinStockIconPngBytes(isEmpty: true, pixelSize: 32);
            fullPng ??= ShellRecycleBinInterop.ExtractRecycleBinStockIconPngBytes(isEmpty: false, pixelSize: 32);

            if (!ShellRecycleBinInterop.TryQueryTotalItemCount(out long count))
            {
                return;
            }

            List<Subscription>? targets = null;
            CustomWidgetRecycleBinState nextState;
            lock (_gate)
            {
                _cachedEmptyIconPng ??= emptyPng;
                _cachedFullIconPng ??= fullPng;
                nextState = new CustomWidgetRecycleBinState(
                    count,
                    _cachedEmptyIconPng,
                    _cachedFullIconPng);
                bool changed = !_hasQueriedOnce ||
                               _current.ItemCount != nextState.ItemCount ||
                               !ReferenceEquals(_current.EmptyIconPng, nextState.EmptyIconPng) ||
                               !ReferenceEquals(_current.FullIconPng, nextState.FullIconPng);
                _current = nextState;
                _hasQueriedOnce = true;
                if (changed && _subscriptions.Count > 0)
                {
                    targets = [.. _subscriptions];
                }
            }

            if (targets is not null)
            {
                foreach (Subscription sub in targets)
                {
                    sub.Deliver(nextState);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _queryInFlight, 0);
        }
    }

    private void EnsureNativeWatcherOnCallerThread()
    {
        lock (_gate)
        {
            if (_subscriptions.Count == 0 || _watcherHandle.IsValid)
            {
                return;
            }

            if (ShellRecycleBinInterop.TryStartRecycleBinChangeWatcher(
                    NotifyShellChanged,
                    out ShellRecycleBinInterop.RecycleBinChangeWatcherHandle handle))
            {
                _watcherHandle = handle;
                _watcherDispatcher = DispatcherQueue.GetForCurrentThread();
            }
        }
    }

    private void Unsubscribe(Subscription sub)
    {
        ShellRecycleBinInterop.RecycleBinChangeWatcherHandle handleToRelease = default;
        DispatcherQueue? watcherDispatcher = null;

        lock (_gate)
        {
            _subscriptions.Remove(sub);
            if (_subscriptions.Count == 0)
            {
                _debounceTimer?.Dispose();
                _debounceTimer = null;
                handleToRelease = _watcherHandle;
                watcherDispatcher = _watcherDispatcher;
                _watcherHandle = default;
                _watcherDispatcher = null;
            }
        }

        if (handleToRelease.IsValid || handleToRelease.Hwnd != IntPtr.Zero || handleToRelease.RegistrationId != 0)
        {
            if (watcherDispatcher is not null && !watcherDispatcher.HasThreadAccess)
            {
                if (!watcherDispatcher.TryEnqueue(() => ShellRecycleBinInterop.StopRecycleBinChangeWatcher(handleToRelease)))
                {
                    ShellRecycleBinInterop.UnregisterRecycleBinChangeNotify(handleToRelease.RegistrationId);
                }
            }
            else
            {
                ShellRecycleBinInterop.StopRecycleBinChangeWatcher(handleToRelease);
            }
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly CustomWidgetRecycleBinService _owner;
        private readonly Action<CustomWidgetRecycleBinState> _callback;
        private readonly DispatcherQueue? _dispatcher;
        private int _disposed;

        public Subscription(
            CustomWidgetRecycleBinService owner,
            Action<CustomWidgetRecycleBinState> callback,
            DispatcherQueue? dispatcher)
        {
            _owner = owner;
            _callback = callback;
            _dispatcher = dispatcher;
        }

        public void Deliver(CustomWidgetRecycleBinState state)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return;
            }

            if (_dispatcher is not null && !_dispatcher.HasThreadAccess)
            {
                _ = _dispatcher.TryEnqueue(() =>
                {
                    if (Volatile.Read(ref _disposed) == 0)
                    {
                        _callback(state);
                    }
                });
                return;
            }

            _callback(state);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.Unsubscribe(this);
            }
        }
    }
}
