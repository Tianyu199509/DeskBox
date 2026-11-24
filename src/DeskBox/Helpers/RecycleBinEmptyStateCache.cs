using DeskBox.Platform;
using Microsoft.UI.Dispatching;

namespace DeskBox.Helpers;

/// <summary>
/// Process-wide, UI-thread-free empty-state cache for the Recycle Bin namespace
/// shortcut menu. Shell Recycle Bin queries (<c>SHQueryRecycleBinW</c>) can
/// stall for seconds across large <c>$Recycle.Bin</c> stores, sleeping disks,
/// or stalled network/cloud volumes, and running COM/Shell queries on the UI
/// STA thread while a <see cref="Microsoft.UI.Xaml.Controls.MenuFlyout"/> popup
/// is opening or closing can freeze the message pump or fault XAML popup visual
/// transitions (<c>0xC000027B</c>).
/// <para>
/// Callers read <see cref="TryGetKnownEmptyState"/> synchronously in memory
/// before <c>MenuFlyout.ShowAt</c>; all Shell probing runs single-flight on the
/// thread pool, kept fresh by a zero-polling <c>SHChangeNotifyRegister</c>
/// watcher once any Recycle Bin shortcut is hydrated or right-clicked.
/// </para>
/// </summary>
internal sealed class RecycleBinEmptyStateCache : IDisposable
{
    internal delegate bool TryQueryHasAnyItemsDelegate(out bool hasItems);

    internal delegate bool TryStartWatcherDelegate(
        Action onChanged,
        out ShellRecycleBinInterop.RecycleBinChangeWatcherHandle handle);

    internal delegate void StopWatcherDelegate(
        ShellRecycleBinInterop.RecycleBinChangeWatcherHandle handle);

    internal const long DefaultUnwatchedTtlMs = 2_000;
    internal const long DefaultWatchedSoftRefreshMs = 15_000;
    internal const int DefaultChangeDebounceMs = 200;

    internal static RecycleBinEmptyStateCache Shared { get; } = CreateDefault();

    private readonly object _gate = new();
    private readonly TryQueryHasAnyItemsDelegate _tryQueryHasAnyItems;
    private readonly TryStartWatcherDelegate _tryStartWatcher;
    private readonly StopWatcherDelegate _stopWatcher;
    private readonly Func<long> _clockTick64;
    private readonly long _unwatchedTtlMs;
    private readonly long _watchedSoftRefreshMs;
    private readonly int _changeDebounceMs;
    private readonly bool _useLiveSubscribedFastPath;

    private bool? _knownIsEmpty;
    private long _knownAtTick64;
    private long _mutationEpoch;
    private Task<bool?>? _inFlightRefreshTask;
    private bool _followUpRefreshRequested;
    private bool _watcherStartAttempted;
    private bool _watcherAttached;
    private ShellRecycleBinInterop.RecycleBinChangeWatcherHandle _watcherHandle;
    private DispatcherQueue? _watcherDispatcher;
    private Timer? _debounceTimer;
    private bool _isDisposed;

    internal RecycleBinEmptyStateCache(
        TryQueryHasAnyItemsDelegate tryQueryHasAnyItems,
        TryStartWatcherDelegate? tryStartWatcher = null,
        StopWatcherDelegate? stopWatcher = null,
        Func<long>? clockTick64 = null,
        long unwatchedTtlMs = DefaultUnwatchedTtlMs,
        long watchedSoftRefreshMs = DefaultWatchedSoftRefreshMs,
        int changeDebounceMs = DefaultChangeDebounceMs,
        bool useLiveSubscribedFastPath = false)
    {
        ArgumentNullException.ThrowIfNull(tryQueryHasAnyItems);
        _tryQueryHasAnyItems = tryQueryHasAnyItems;
        _tryStartWatcher = tryStartWatcher ?? ShellRecycleBinInterop.TryStartRecycleBinChangeWatcher;
        _stopWatcher = stopWatcher ?? ShellRecycleBinInterop.StopRecycleBinChangeWatcher;
        _clockTick64 = clockTick64 ?? (static () => Environment.TickCount64);
        _unwatchedTtlMs = Math.Max(1, unwatchedTtlMs);
        _watchedSoftRefreshMs = Math.Max(1, watchedSoftRefreshMs);
        _changeDebounceMs = Math.Max(1, changeDebounceMs);
        _useLiveSubscribedFastPath = useLiveSubscribedFastPath;
    }

    private static RecycleBinEmptyStateCache CreateDefault()
    {
        var instance = new RecycleBinEmptyStateCache(
            ShellRecycleBinInterop.TryQueryHasAnyItems,
            useLiveSubscribedFastPath: true);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => instance.Dispose();
        return instance;
    }

    internal bool IsWatcherAttached
    {
        get
        {
            lock (_gate)
            {
                return _watcherAttached;
            }
        }
    }

    /// <summary>
    /// Lazily attaches the zero-polling Shell Recycle Bin change watcher on
    /// <paramref name="uiDispatcher"/> (when supplied) and ensures the initial
    /// background probe is running. Never blocks the caller.
    /// </summary>
    internal void EnsureWarm(DispatcherQueue? uiDispatcher)
    {
        bool tryAttachWatcher = false;
        bool triggerRefresh = false;

        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            if (!_watcherAttached && !_watcherStartAttempted && uiDispatcher is not null)
            {
                _watcherStartAttempted = true;
                tryAttachWatcher = true;
            }

            long now = _clockTick64();
            if (_knownIsEmpty is null || IsStaleUnsafe(now))
            {
                triggerRefresh = _inFlightRefreshTask is null;
            }
        }

        if (tryAttachWatcher && uiDispatcher is not null)
        {
            if (uiDispatcher.HasThreadAccess)
            {
                TryAttachWatcherOnCurrentThread(uiDispatcher);
            }
            else if (!uiDispatcher.TryEnqueue(() => TryAttachWatcherOnCurrentThread(uiDispatcher)))
            {
                lock (_gate)
                {
                    if (!_watcherAttached)
                    {
                        _watcherStartAttempted = false;
                    }
                }
            }
        }

        if (triggerRefresh)
        {
            _ = StartOrJoinRefreshAsync(forceFollowUp: false);
        }
    }

    /// <summary>
    /// Reads the cached Recycle Bin empty state in memory without performing
    /// any Shell or disk I/O. Returns <see langword="false"/> when the state is
    /// not yet known or has expired without an active Shell watcher (callers
    /// must keep the menu verb enabled in that case).
    /// </summary>
    internal bool TryGetKnownEmptyState(out bool isEmpty)
    {
        // Fast path: if the Custom Widget Recycle Bin service already holds a
        // live watched count, prefer its immediate in-memory snapshot.
        if (_useLiveSubscribedFastPath &&
            Services.CustomWidgetRecycleBinService.Shared.TryGetLiveSubscribedItemCount(out long liveCount))
        {
            isEmpty = liveCount <= 0;
            return true;
        }

        bool triggerRefresh = false;
        bool result;
        bool resolvedEmpty = false;

        lock (_gate)
        {
            if (_isDisposed)
            {
                isEmpty = false;
                return false;
            }

            long now = _clockTick64();
            if (_knownIsEmpty is not { } known)
            {
                triggerRefresh = _inFlightRefreshTask is null;
                result = false;
            }
            else if (!_watcherAttached && (now - _knownAtTick64) >= _unwatchedTtlMs)
            {
                // Without an active SHChangeNotify watcher, never trust a stale
                // empty snapshot across a multi-second gap (a file deleted in
                // Explorer in the interim would otherwise see a disabled verb).
                _knownIsEmpty = null;
                triggerRefresh = _inFlightRefreshTask is null;
                result = false;
            }
            else
            {
                resolvedEmpty = known;
                result = true;
                if (_watcherAttached &&
                    (now - _knownAtTick64) >= _watchedSoftRefreshMs &&
                    _inFlightRefreshTask is null)
                {
                    triggerRefresh = true;
                }
            }
        }

        if (triggerRefresh)
        {
            _ = StartOrJoinRefreshAsync(forceFollowUp: false);
        }

        isEmpty = resolvedEmpty;
        return result;
    }

    /// <summary>
    /// Immediately records a known post-mutation state (for example right after
    /// emptying all bins or sending items to the Recycle Bin) while advancing
    /// the epoch so an older in-flight background probe cannot overwrite it.
    /// </summary>
    internal void MarkKnownState(bool isEmpty)
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _mutationEpoch++;
            _knownIsEmpty = isEmpty;
            _knownAtTick64 = _clockTick64();
            if (_inFlightRefreshTask is not null)
            {
                _followUpRefreshRequested = true;
            }
        }
    }

    /// <summary>
    /// Invalidates any cached snapshot and schedules an immediate background
    /// refresh. While the refresh runs, <see cref="TryGetKnownEmptyState"/>
    /// reports unknown so the menu row stays enabled rather than risking a
    /// stale disabled state.
    /// </summary>
    internal void InvalidateAndRefresh()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _mutationEpoch++;
            _knownIsEmpty = null;
        }

        _ = StartOrJoinRefreshAsync(forceFollowUp: true);
    }

    /// <summary>
    /// Runs or joins a fresh background probe bounded by <paramref name="timeout"/>.
    /// Returns <see langword="true"/> only when the probe completes in time and
    /// confirms every Recycle Bin is empty; timeouts and query failures return
    /// <see langword="false"/> so callers never block indefinitely or suppress
    /// a working verb.
    /// </summary>
    internal async Task<bool> QueryFreshIsKnownEmptyBoundedAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (_useLiveSubscribedFastPath &&
            Services.CustomWidgetRecycleBinService.Shared.TryGetLiveSubscribedItemCount(out long liveCount))
        {
            return liveCount <= 0;
        }

        Task<bool?> refreshTask = StartOrJoinRefreshAsync(forceFollowUp: true);
        if (timeout <= TimeSpan.Zero)
        {
            return refreshTask.IsCompletedSuccessfully && refreshTask.Result == true;
        }

        try
        {
            bool? knownEmpty = await refreshTask
                .WaitAsync(timeout, cancellationToken)
                .ConfigureAwait(false);
            return knownEmpty == true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    internal void NotifyShellChanged()
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _mutationEpoch++;
            _knownIsEmpty = null;
            if (_inFlightRefreshTask is not null)
            {
                _followUpRefreshRequested = true;
            }

            _debounceTimer ??= new Timer(
                static state =>
                {
                    var owner = (RecycleBinEmptyStateCache)state!;
                    _ = owner.StartOrJoinRefreshAsync(forceFollowUp: true);
                },
                this,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            _debounceTimer.Change(
                TimeSpan.FromMilliseconds(_changeDebounceMs),
                Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        ShellRecycleBinInterop.RecycleBinChangeWatcherHandle handleToStop = default;
        DispatcherQueue? watcherDispatcher = null;
        Timer? debounceTimer = null;

        lock (_gate)
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _mutationEpoch++;
            _knownIsEmpty = null;
            _followUpRefreshRequested = false;
            debounceTimer = _debounceTimer;
            _debounceTimer = null;

            if (_watcherAttached || _watcherHandle.IsValid)
            {
                handleToStop = _watcherHandle;
                watcherDispatcher = _watcherDispatcher;
                _watcherHandle = default;
                _watcherDispatcher = null;
                _watcherAttached = false;
            }
        }

        debounceTimer?.Dispose();

        if (handleToStop.IsValid || handleToStop.Hwnd != IntPtr.Zero || handleToStop.RegistrationId != 0)
        {
            if (watcherDispatcher is not null && !watcherDispatcher.HasThreadAccess)
            {
                if (!watcherDispatcher.TryEnqueue(() => _stopWatcher(handleToStop)))
                {
                    ShellRecycleBinInterop.UnregisterRecycleBinChangeNotify(handleToStop.RegistrationId);
                }
            }
            else
            {
                _stopWatcher(handleToStop);
            }
        }
    }

    private bool IsStaleUnsafe(long nowTick64) =>
        _watcherAttached
            ? (nowTick64 - _knownAtTick64) >= _watchedSoftRefreshMs
            : (nowTick64 - _knownAtTick64) >= _unwatchedTtlMs;

    private void TryAttachWatcherOnCurrentThread(DispatcherQueue uiDispatcher)
    {
        lock (_gate)
        {
            if (_isDisposed || _watcherAttached)
            {
                return;
            }
        }

        if (!_tryStartWatcher(NotifyShellChanged, out ShellRecycleBinInterop.RecycleBinChangeWatcherHandle handle) ||
            !handle.IsValid)
        {
            lock (_gate)
            {
                if (!_watcherAttached)
                {
                    _watcherStartAttempted = false;
                }
            }
            return;
        }

        bool stopImmediately = false;
        lock (_gate)
        {
            if (_isDisposed || _watcherAttached)
            {
                stopImmediately = true;
            }
            else
            {
                _watcherHandle = handle;
                _watcherDispatcher = uiDispatcher;
                _watcherAttached = true;
            }
        }

        if (stopImmediately)
        {
            _stopWatcher(handle);
        }
    }

    internal Task<bool?> StartOrJoinRefreshAsync(bool forceFollowUp)
    {
        lock (_gate)
        {
            if (_isDisposed)
            {
                return Task.FromResult<bool?>(null);
            }

            if (_inFlightRefreshTask is { } existing)
            {
                if (forceFollowUp)
                {
                    _followUpRefreshRequested = true;
                }

                return existing;
            }

            _inFlightRefreshTask = Task.Run(RunRefreshLoop);
            return _inFlightRefreshTask;
        }
    }

    private bool? RunRefreshLoop()
    {
        while (true)
        {
            long epochBefore;
            lock (_gate)
            {
                if (_isDisposed)
                {
                    _inFlightRefreshTask = null;
                    _followUpRefreshRequested = false;
                    return null;
                }

                epochBefore = _mutationEpoch;
                _followUpRefreshRequested = false;
            }

            bool querySucceeded;
            bool hasItems = false;
            try
            {
                querySucceeded = _tryQueryHasAnyItems(out hasItems);
            }
            catch
            {
                querySucceeded = false;
            }

            lock (_gate)
            {
                if (_isDisposed)
                {
                    _inFlightRefreshTask = null;
                    _followUpRefreshRequested = false;
                    return null;
                }

                if (_mutationEpoch == epochBefore)
                {
                    if (querySucceeded)
                    {
                        _knownIsEmpty = !hasItems;
                        _knownAtTick64 = _clockTick64();
                    }
                    else
                    {
                        _knownIsEmpty = null;
                    }
                }

                if (_followUpRefreshRequested)
                {
                    _followUpRefreshRequested = false;
                    continue;
                }

                bool? completedValue = _knownIsEmpty;
                _inFlightRefreshTask = null;
                return completedValue;
            }
        }
    }
}
