// Copyright (c) DeskBox. All rights reserved.

using System.Diagnostics;
using DeskBox.Helpers;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Services;

/// <summary>
/// One window's participation in a shared batch tray animation.
/// The batch driver moves the HWND physically; the owning window keeps
/// its own GPU-driven opacity/scale Composition animations and its own
/// completion logic.
/// </summary>
public sealed class WidgetTrayBatchAnimationEntry
{
    public required IntPtr WindowHandle { get; init; }
    public required int BaseX { get; init; }
    public required int BaseY { get; init; }
    public required double FromOffsetX { get; init; }
    public required double FromOffsetY { get; init; }
    public required double ToOffsetX { get; init; }
    public required double ToOffsetY { get; init; }

    /// <summary>Returns false when the owning window started a newer animation.</summary>
    public required Func<bool> IsValid { get; init; }

    /// <summary>Invoked once on the UI thread after the final frame commits.</summary>
    public required Action Completed { get; init; }
}

/// <summary>
/// Drives a batch of widget slide animations from a single
/// CompositionTarget.Rendering handler with a single shared clock,
/// committing all window positions in one DeferWindowPos transaction per
/// frame so every window moves in lockstep (no staggered "wave").
/// Physical movement semantics are preserved: HWNDs still travel off the
/// screen; only the commit mechanism changes from N independent
/// SetWindowPos calls to one atomic batch commit.
/// </summary>
public sealed class WidgetTrayBatchAnimationDriver
{
    private const uint MoveFlags =
        Win32Helper.SWP_NOSIZE | Win32Helper.SWP_NOZORDER | Win32Helper.SWP_NOACTIVATE;

    private readonly List<WidgetTrayBatchAnimationEntry> _entries = new();
    private readonly Action<string> _log;
    private Stopwatch? _stopwatch;
    private double _durationMs = 1;
    private string _easingIntensity = string.Empty;
    private bool _isShowing;
    private int _remainingDelayFrames;
    private bool _isRunning;

    public WidgetTrayBatchAnimationDriver(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    public bool IsRunning => _isRunning;

    /// <summary>
    /// Starts a shared batch run. Any previously running batch is cancelled
    /// first (its entries complete nothing; window-side generation checks
    /// keep state consistent).
    /// </summary>
    public void Start(
        IReadOnlyList<WidgetTrayBatchAnimationEntry> entries,
        int durationMs,
        string easingIntensity,
        bool isShowing,
        int startDelayFrames)
    {
        Cancel();
        if (entries.Count == 0)
        {
            return;
        }

        _entries.AddRange(entries);
        _durationMs = Math.Max(1, durationMs);
        _easingIntensity = easingIntensity;
        _isShowing = isShowing;
        _remainingDelayFrames = Math.Max(0, startDelayFrames);
        _stopwatch = null;
        _isRunning = true;
        CompositionTarget.Rendering -= OnRenderingFrame;
        CompositionTarget.Rendering += OnRenderingFrame;
        _log(
            $"[BatchAnim] Start count={_entries.Count} durationMs={_durationMs} " +
            $"mode={(isShowing ? "show" : "hide")} delayFrames={_remainingDelayFrames}");
    }

    public void Cancel()
    {
        if (!_isRunning)
        {
            return;
        }

        _isRunning = false;
        _entries.Clear();
        _stopwatch = null;
        CompositionTarget.Rendering -= OnRenderingFrame;
        _log("[BatchAnim] Cancelled");
    }

    private void OnRenderingFrame(object sender, object e)
    {
        try
        {
            if (!_isRunning)
            {
                return;
            }

            // Drop entries whose window started a newer animation meanwhile.
            for (int i = _entries.Count - 1; i >= 0; i--)
            {
                if (!_entries[i].IsValid())
                {
                    _entries.RemoveAt(i);
                }
            }

            if (_entries.Count == 0)
            {
                Cancel();
                return;
            }

            // Give freshly shown windows a couple of frames to commit their
            // first surface before the shared clock starts (same semantics
            // as the per-window PlayAfterContentReady wait).
            if (_remainingDelayFrames > 0)
            {
                _remainingDelayFrames--;
                return;
            }

            _stopwatch ??= Stopwatch.StartNew();

            double rawProgress = Math.Clamp(_stopwatch.Elapsed.TotalMilliseconds / _durationMs, 0.0, 1.0);
            double easedProgress = WidgetAnimationSettings.Ease(rawProgress, _easingIntensity, _isShowing);

            MoveEntriesFrame(easedProgress);

            if (rawProgress < 1.0)
            {
                return;
            }

            FinishBatch();
        }
        catch (Exception ex)
        {
            // Never let a frame exception escape the Rendering callback.
            App.Log($"[WidgetTrayBatchAnimationDriver] Frame exception: {ex.Message}\n{ex.StackTrace}");
            FinishBatch();
        }
    }

    private void MoveEntriesFrame(double easedProgress)
    {
        IntPtr hdwp = Win32Helper.BeginDeferWindowPos(_entries.Count);
        if (hdwp == IntPtr.Zero)
        {
            // Fallback: move windows individually (old behavior).
            foreach (var entry in _entries)
            {
                var (x, y) = GetEntryFramePosition(entry, easedProgress);
                Win32Helper.SetWindowPos(entry.WindowHandle, IntPtr.Zero, x, y, 0, 0, MoveFlags);
            }

            return;
        }

        foreach (var entry in _entries)
        {
            var (x, y) = GetEntryFramePosition(entry, easedProgress);
            IntPtr next = Win32Helper.DeferWindowPos(
                hdwp, entry.WindowHandle, IntPtr.Zero, x, y, 0, 0, MoveFlags);
            if (next == IntPtr.Zero)
            {
                // Commit whatever was deferred so far; the next frame
                // re-attempts the full batch.
                break;
            }

            hdwp = next;
        }

        Win32Helper.EndDeferWindowPos(hdwp);
    }

    private static (int X, int Y) GetEntryFramePosition(
        WidgetTrayBatchAnimationEntry entry,
        double easedProgress)
    {
        double offsetX = Lerp(entry.FromOffsetX, entry.ToOffsetX, easedProgress);
        double offsetY = Lerp(entry.FromOffsetY, entry.ToOffsetY, easedProgress);
        return (
            entry.BaseX + (int)Math.Round(offsetX),
            entry.BaseY + (int)Math.Round(offsetY));
    }

    private void FinishBatch()
    {
        var completed = _entries.ToArray();
        Cancel();
        foreach (var entry in completed)
        {
            try
            {
                entry.Completed();
            }
            catch (Exception ex)
            {
                App.Log($"[WidgetTrayBatchAnimationDriver] Completed exception: {ex.Message}");
            }
        }
    }

    private static double Lerp(double from, double to, double progress)
    {
        return from + (to - from) * progress;
    }
}
