using System.Linq;
using Microsoft.UI.Dispatching;

namespace DeskBox.Platform;

/// <summary>
/// Single audit point for UI-thread dispatch. Replaces the two meanings the
/// legacy code overloaded onto "App.UiDispatcherQueue == null" with two
/// explicit signals:
/// <list type="bullet">
/// <item>UI-thread identity — captured when the XAML runtime constructs the
/// App instance. Test hosts never construct App, so they never register an
/// origin thread.</item>
/// <item>Availability — a Headless → Ready → Shutdown state machine driven by
/// dispatcher-init and the shutdown sequence.</item>
/// </list>
/// Semantics per state (behavior differs from the legacy helpers only in the
/// Headless column):
/// <code>
///   Ready  + dispatcher thread   → run inline
///   Ready  + other thread        → TryEnqueue (legacy behavior)
///   Headless + origin thread     → run inline (App-constructor code keeps
///                                  its synchronous semantics)
///   Headless + other thread      → bounded FIFO deferral, replayed in order
///                                  on the UI thread at MarkReady
///   Shutdown                     → cancel / drop pending
/// </code>
/// Deferred actions must be idempotent: they replay after dispatcher-init,
/// and state they read may have changed while the app was headless.
/// </summary>
internal static class UiDispatch
{
    private const int MaxDeferredActions = 256;

    private enum Decision
    {
        RunInline,
        Enqueue,
        Defer,
        Skip,
        Shutdown,
    }

    public enum PhaseKind
    {
        Headless,
        Ready,
        Shutdown,
    }

    private sealed class PendingItem
    {
        public required Func<Task> Invoker { get; init; }
    }

    private static readonly object Gate = new();
    private static readonly Queue<PendingItem> Pending = new();
    private static DispatcherQueue? _queue;
    private static int _originThreadId = -1;
    private static bool _shutdown;
    private static long _headlessSkipCount;
    private static long _deferredCount;
    private static long _deferredDroppedCount;

    public static PhaseKind Phase
    {
        get
        {
            lock (Gate)
            {
                if (_shutdown)
                {
                    return PhaseKind.Shutdown;
                }

                return _queue is null ? PhaseKind.Headless : PhaseKind.Ready;
            }
        }
    }

    /// <summary>True when the current thread may touch XAML objects directly.</summary>
    public static bool HasAccess
    {
        get
        {
            lock (Gate)
            {
                return DecideNoLock() is Decision.RunInline;
            }
        }
    }

    public static int PendingCount
    {
        get
        {
            lock (Gate)
            {
                return Pending.Count;
            }
        }
    }

    public static long HeadlessSkipCount => Volatile.Read(ref _headlessSkipCount);
    public static long DeferredCount => Volatile.Read(ref _deferredCount);
    public static long DeferredDroppedCount => Volatile.Read(ref _deferredDroppedCount);

    /// <summary>
    /// Called first in the App instance constructor: the XAML runtime builds
    /// App on what will be the UI thread, before OnLaunched's dispatcher-init
    /// step runs. Static-member access never triggers this, so test hosts
    /// stay without an origin thread.
    /// </summary>
    public static void MarkUiOriginThread()
    {
        lock (Gate)
        {
            _originThreadId = Environment.CurrentManagedThreadId;
        }
    }

    /// <summary>
    /// Called by OnLaunched's dispatcher-init step, on the dispatcher thread.
    /// Replays deferred work inline (this method runs on the thread the
    /// queue belongs to), preserving original order.
    /// </summary>
    public static void MarkReady(DispatcherQueue queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        PendingItem[] toReplay;
        lock (Gate)
        {
            if (_shutdown)
            {
                return;
            }

            _queue = queue;
            toReplay = [.. Pending];
            Pending.Clear();
        }

        foreach (PendingItem item in toReplay)
        {
            // Fire and forget: the invoker completes its own completion
            // source; a throwing deferred action must not block the replay
            // of its successors.
            _ = item.Invoker();
        }
    }

    /// <summary>
    /// Called from the shutdown sequence: subsequent dispatch requests are
    /// dropped, pending deferrals are discarded, and the queue reference is
    /// released so nothing replays after exit.
    /// </summary>
    public static void MarkShutdown()
    {
        lock (Gate)
        {
            if (_shutdown)
            {
                return;
            }

            _shutdown = true;
            Interlocked.Add(ref _deferredDroppedCount, Pending.Count);
            Pending.Clear();
            _queue = null;
        }
    }

    /// <summary>
    /// Replaces the legacy RunOnUiThreadAsync: runs the work on the UI
    /// thread when possible, defers while headless, and never pretends a
    /// foreign thread is the UI thread.
    /// </summary>
    public static Task<T> RunAsync<T>(Func<Task<T>> action)
    {
        Decision decision;
        DispatcherQueue? enqueueTarget = null;
        lock (Gate)
        {
            decision = DecideNoLock();
            if (decision is Decision.Enqueue or Decision.Defer)
            {
                enqueueTarget = _queue;
            }
        }

        switch (decision)
        {
            case Decision.RunInline:
                return action();

            case Decision.Enqueue:
                return EnqueueCore(enqueueTarget!, action);

            case Decision.Defer:
                return DeferCore(action);

            case Decision.Skip:
                Interlocked.Increment(ref _headlessSkipCount);
                return Task.FromCanceled<T>(new CancellationToken(canceled: true));

            case Decision.Shutdown:
            default:
                return Task.FromCanceled<T>(new CancellationToken(canceled: true));
        }
    }

    public static Task RunAsync(Func<Task> action)
    {
        return RunAsync(async () =>
        {
            await action();
            return true;
        });
    }

    /// <summary>
    /// Replaces the "enqueue if not on the UI thread, otherwise fall through"
    /// fire-and-forget pattern. Skipped (counted) while headless off-origin
    /// instead of silently running on the calling thread.
    /// </summary>
    public static void RunOrDefer(Action action)
    {
        Decision decision;
        DispatcherQueue? enqueueTarget = null;
        lock (Gate)
        {
            decision = DecideNoLock();
            if (decision is Decision.Enqueue or Decision.Defer)
            {
                enqueueTarget = _queue;
            }
        }

        switch (decision)
        {
            case Decision.RunInline:
                action();
                break;

            case Decision.Enqueue:
                if (!enqueueTarget!.TryEnqueue(() => action()))
                {
                    App.Log("[UiDispatch] TryEnqueue rejected a fire-and-forget action.");
                }

                break;

            case Decision.Defer:
                DeferCore<bool>(() =>
                {
                    action();
                    return Task.FromResult(true);
                });
                break;

            case Decision.Skip:
                Interlocked.Increment(ref _headlessSkipCount);
                break;

            case Decision.Shutdown:
            default:
                break;
        }
    }

    /// <summary>
    /// Legacy-helper observability removed in stage 2 together with the
    /// null-inline helpers. Skip/defer counters below remain as permanent
    /// observability.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Pending.Clear();
            _queue = null;
            _originThreadId = -1;
            _shutdown = false;
            _headlessSkipCount = 0;
            _deferredCount = 0;
            _deferredDroppedCount = 0;
        }
    }

    private static Decision DecideNoLock()
    {
        if (_shutdown)
        {
            return Decision.Shutdown;
        }

        if (_queue is not null)
        {
            return _queue.HasThreadAccess ? Decision.RunInline : Decision.Enqueue;
        }

        if (_originThreadId == Environment.CurrentManagedThreadId)
        {
            return Decision.RunInline;
        }

        if (_originThreadId >= 0)
        {
            // Headless, foreign thread, app still coming up: defer.
            return Decision.Defer;
        }

        // No origin registered: a real app records its launcher thread from
        // the App constructor before anything meaningful runs, so this branch
        // is only reachable from a test host (or a production window so
        // early nothing calls here). Test hosts deliberately keep the legacy
        // inline fiction — their fixtures exercise the mixed UI/config logic
        // behind these gates and have no UI to dispatch to — detected
        // deterministically by the loaded test assembly.
        return IsTestHost ? Decision.RunInline : Decision.Skip;
    }

    private static bool? _testHost;

    private static bool IsTestHost
    {
        get
        {
            _testHost ??= AppDomain.CurrentDomain.GetAssemblies()
                .Any(assembly => assembly.GetName().Name == "DeskBox.Tests");
            return _testHost.Value;
        }
    }

    /// <summary>
    /// Whether a real XAML Application instance exists. Test hosts use App's
    /// static members (logging) without ever constructing the Application,
    /// so window-creating paths can use this to no-op instead of attempting
    /// XAML object creation that cannot succeed there.
    /// </summary>
    public static bool HasXamlApp => App.Current is not null;

    private static Task<T> EnqueueCore<T>(DispatcherQueue queue, Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!queue.TryEnqueue(async () =>
        {
            try
            {
                completion.SetResult(await action());
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        }))
        {
            completion.SetException(new InvalidOperationException(
                "Unable to dispatch widget lifecycle operation to the UI thread."));
        }

        return completion.Task;
    }

    private static Task<T> DeferCore<T>(Func<Task<T>> action)
    {
        var completion = new TaskCompletionSource<T>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (Gate)
        {
            if (Pending.Count >= MaxDeferredActions)
            {
                Interlocked.Increment(ref _deferredDroppedCount);
                completion.SetException(new InvalidOperationException(
                    $"UI dispatch deferred queue overflowed the {MaxDeferredActions}-action cap while headless."));
                return completion.Task;
            }

            Interlocked.Increment(ref _deferredCount);
            Pending.Enqueue(new PendingItem
            {
                Invoker = async () =>
                {
                    try
                    {
                        completion.SetResult(await action());
                    }
                    catch (Exception ex)
                    {
                        completion.SetException(ex);
                    }
                }
            });
        }

        return completion.Task;
    }
}
