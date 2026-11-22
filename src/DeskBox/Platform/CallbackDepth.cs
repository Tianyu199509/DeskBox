using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DeskBox.Platform;

/// <summary>
/// Thread-static radar for the drag-freeze family's root invariant: window
/// mutation must not run inside a native callback frame (subclass procs, OLE
/// drop-target methods, tray icon commands). Callback entry points bracket
/// themselves with <see cref="Enter"/>/<see cref="Exit"/>; the window-mutating
/// Win32Helper wrappers (SetWindowPos/ShowWindow/SetForegroundWindow/
/// SetWindowLongPtr/EndDeferWindowPos) self-report through
/// <see cref="RecordWindowOperation"/> while the depth is above zero.
/// Counters are always on (a single interlocked increment per violation) so
/// diagnostics bundles capture them in the field; the verbose log line is
/// gated by DESKBOX_VERBOSE_LOG like every other LogVerbose call.
/// The radar is a same-thread detector only: cross-thread synchronous sends
/// into the UI thread are invisible to it and remain a review-time concern.
/// </summary>
internal static class CallbackDepth
{
    [ThreadStatic]
    private static int _depth;

    private static readonly ConcurrentDictionary<string, long>
        _windowOperationCounts = new(StringComparer.Ordinal);

    /// <summary>Current callback nesting depth on this thread. Zero means
    /// the thread is running dispatcher work, not native callback work.</summary>
    public static int Depth => _depth;

    public static void Enter()
    {
        _depth++;
    }

    /// <summary>Paired with <see cref="Enter"/> in a finally block; guarded so
    /// an unbalanced exit cannot drive the depth negative and silence the
    /// radar for the rest of the session.</summary>
    public static void Exit()
    {
        if (_depth > 0)
        {
            _depth--;
        }
    }

    /// <summary>Called by the tracked Win32Helper wrappers. Diagnostic only:
    /// never throws, never blocks the calling window operation. The first
    /// violation of each operation surfaces at the default log level so
    /// field diagnostics see it without verbose enabled; repeats stay
    /// verbose-only to keep hot paths quiet.</summary>
    public static void RecordWindowOperation(
        [CallerMemberName] string? operation = null)
    {
        string key = operation ?? "unknown";
        long newCount = _windowOperationCounts.AddOrUpdate(
            key,
            static _ => 1,
            static (_, count) => count + 1);
        if (newCount == 1)
        {
            App.Log(
                $"[CallbackDepth] Window operation '{key}' ran inside a " +
                "native callback frame; this violates the callback " +
                "isolation invariant.");
        }
        else
        {
            App.LogVerbose(
                $"[CallbackDepth] Window operation '{key}' ran at callback " +
                $"depth {_depth}.");
        }
    }

    public static IReadOnlyDictionary<string, long> SnapshotWindowOperationCounts()
    {
        return _windowOperationCounts.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal);
    }

    /// <summary>
    /// Wraps a subclass proc delegate so every message it receives is
    /// bracketed by <see cref="Enter"/>/<see cref="Exit"/>. Assign the
    /// returned wrapper to the same field that roots the original delegate:
    /// the wrapper's closure keeps the original alive, and the field keeps
    /// the wrapper alive for comctl32's lifetime of the subclass.
    /// </summary>
    public static Win32Helper.SubclassProc WrapSubclassProc(
        Win32Helper.SubclassProc proc)
    {
        return (hWnd, message, wParam, lParam, subclassId, referenceData) =>
        {
            Enter();
            try
            {
                return proc(hWnd, message, wParam, lParam, subclassId, referenceData);
            }
            catch (Exception ex)
            {
                global::DeskBox.App.Log($"[CallbackDepth] Unhandled exception in subclass proc msg=0x{message:X4}: {ex}");
                return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
            }
            finally
            {
                Exit();
            }
        };
    }

    internal static void ResetForTests()
    {
        _depth = 0;
        _windowOperationCounts.Clear();
    }
}
