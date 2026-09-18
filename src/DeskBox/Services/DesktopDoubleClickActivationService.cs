// Copyright (c) DeskBox. All rights reserved.

using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed class DesktopDoubleClickActivationService : IDisposable
{
    private const int StartupTimeoutMilliseconds = 1500;
    private const int StopTimeoutMilliseconds = 500;
    private const uint MouseInjectedFlag = 0x00000001;
    private const int SystemMetricDoubleClickWidth = 36;
    private const int SystemMetricDoubleClickHeight = 37;
    private const int MaximumQueuedMouseDowns = 16;

    private readonly object _sync = new();
    private readonly object _stateMachineSync = new();
    private readonly SettingsService _settingsService;
    private readonly Func<DesktopDoubleClickSequence, Task> _invokeAsync;
    private readonly Win32Helper.LowLevelMouseProc _mouseHookProc;
    private readonly DesktopDoubleClickStateMachine _stateMachine;
    private readonly ConcurrentQueue<QueuedMouseDown> _queuedMouseDowns = new();
    private readonly bool _diagnosticsEnabled;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hookHandle;
    private int _startupError;
    private long _lifecycleGeneration;
    private long _triggerCount;
    private long _dispatchFailureCount;
    private long _mouseDownCount;
    private long _injectedMouseDownCount;
    private long _blankMouseDownCount;
    private long _droppedMouseDownCount;
    private int _queuedMouseDownCount;
    private int _inputDrainScheduled;
    private bool _disposed;

    internal DesktopDoubleClickActivationService(
        SettingsService settingsService,
        Func<DesktopDoubleClickSequence, Task> invokeAsync)
    {
        _settingsService = settingsService;
        _invokeAsync = invokeAsync;
        _mouseHookProc = MouseHookProc;
        _diagnosticsEnabled = string.Equals(
            Environment.GetEnvironmentVariable("DESKBOX_DESKTOP_DOUBLE_CLICK_DIAGNOSTICS"),
            "1",
            StringComparison.Ordinal);
        _stateMachine = new DesktopDoubleClickStateMachine(
            Win32Helper.GetDoubleClickTime(),
            Win32Helper.GetSystemMetrics(SystemMetricDoubleClickWidth),
            Win32Helper.GetSystemMetrics(SystemMetricDoubleClickHeight));
    }

    public bool IsActive
    {
        get
        {
            lock (_sync)
            {
                return _hookHandle != IntPtr.Zero && _thread?.IsAlive == true;
            }
        }
    }

    public int LastErrorCode { get; private set; }
    public long TriggerCount => Interlocked.Read(ref _triggerCount);
    public long DispatchFailureCount => Interlocked.Read(ref _dispatchFailureCount);

    public bool TrySetEnabled(bool enabled, out int errorCode)
    {
        errorCode = 0;
        bool previous = _settingsService.Settings.DesktopDoubleClickEnabled;
        if (previous == enabled)
        {
            if (!enabled || IsActive || TryStart(out errorCode))
            {
                return true;
            }

            return false;
        }

        _settingsService.Settings.DesktopDoubleClickEnabled = enabled;
        if (enabled && !TryStart(out errorCode))
        {
            _settingsService.Settings.DesktopDoubleClickEnabled = previous;
            return false;
        }

        if (!enabled)
        {
            Stop();
        }

        _settingsService.SaveDebounced();
        return true;
    }

    public void RefreshRegistration()
    {
        Stop();
        if (!_settingsService.Settings.DesktopDoubleClickEnabled)
        {
            return;
        }

        if (!TryStart(out int errorCode))
        {
            App.Log($"[DesktopDoubleClick] Hook registration failed error={errorCode}");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stop();
    }

    private bool TryStart(out int errorCode)
    {
        errorCode = 0;
        Stop();

        var ready = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread;
        long generation;
        lock (_sync)
        {
            if (_disposed)
            {
                errorCode = 1400;
                return false;
            }

            _startupError = 0;
            LastErrorCode = 0;
            generation = ++_lifecycleGeneration;
            thread = new Thread(() => HookThreadMain(ready, generation))
            {
                IsBackground = true,
                Name = "DeskBox.DesktopDoubleClickHook"
            };
            _thread = thread;
        }

        thread.Start();
        if (!ready.Task.Wait(StartupTimeoutMilliseconds))
        {
            errorCode = 1460;
            LastErrorCode = errorCode;
            Stop();
            return false;
        }

        lock (_sync)
        {
            if (_hookHandle != IntPtr.Zero && _thread?.IsAlive == true)
            {
                App.Log("[DesktopDoubleClick] Hook registered");
                return true;
            }

            errorCode = _startupError == 0 ? 1400 : _startupError;
            LastErrorCode = errorCode;
        }

        Stop();
        return false;
    }

    private void Stop()
    {
        Thread? thread;
        uint threadId;
        IntPtr hookHandle;
        lock (_sync)
        {
            _lifecycleGeneration++;
            thread = _thread;
            threadId = _threadId;
            hookHandle = _hookHandle;
        }

        if (thread is null)
        {
            ResetInputState();
            return;
        }

        if (threadId != 0)
        {
            Win32Helper.PostThreadMessage(
                threadId,
                Win32Helper.WM_QUIT,
                UIntPtr.Zero,
                IntPtr.Zero);
        }

        if (thread != Thread.CurrentThread && !thread.Join(StopTimeoutMilliseconds))
        {
            if (hookHandle != IntPtr.Zero)
            {
                Win32Helper.UnhookWindowsHookEx(hookHandle);
            }

            if (threadId != 0)
            {
                Win32Helper.PostThreadMessage(
                    threadId,
                    Win32Helper.WM_QUIT,
                    UIntPtr.Zero,
                    IntPtr.Zero);
            }

            thread.Join(150);
        }

        lock (_sync)
        {
            if (ReferenceEquals(_thread, thread) && !thread.IsAlive)
            {
                _thread = null;
                _threadId = 0;
                _hookHandle = IntPtr.Zero;
            }
        }

        ResetInputState();
    }

    private void HookThreadMain(TaskCompletionSource<bool> ready, long generation)
    {
        IntPtr installedHook = IntPtr.Zero;
        try
        {
            lock (_sync)
            {
                if (_disposed || generation != _lifecycleGeneration)
                {
                    ready.TrySetResult(true);
                    return;
                }
            }

            uint threadId = Win32Helper.GetCurrentThreadId();
            Win32Helper.PeekMessage(
                out _,
                IntPtr.Zero,
                0,
                0,
                Win32Helper.PM_NOREMOVE);
            installedHook = Win32Helper.SetWindowsMouseHookEx(
                Win32Helper.WH_MOUSE_LL,
                _mouseHookProc,
                Win32Helper.GetModuleHandle(null),
                0);
            int startupError = installedHook == IntPtr.Zero
                ? Marshal.GetLastWin32Error()
                : 0;

            bool cancelled;
            lock (_sync)
            {
                cancelled = _disposed || generation != _lifecycleGeneration;
                if (!cancelled)
                {
                    _threadId = threadId;
                    _hookHandle = installedHook;
                    _startupError = startupError;
                    LastErrorCode = startupError;
                }
            }

            ready.TrySetResult(true);
            if (installedHook == IntPtr.Zero || cancelled)
            {
                return;
            }

            while (Win32Helper.GetMessage(out Win32Helper.MSG message, IntPtr.Zero, 0, 0) > 0)
            {
                Win32Helper.TranslateMessage(in message);
                Win32Helper.DispatchMessage(in message);
            }
        }
        catch (Exception ex)
        {
            int error = Marshal.GetHRForException(ex);
            lock (_sync)
            {
                _startupError = error;
                LastErrorCode = error;
            }
            ready.TrySetResult(true);
            App.Log($"[DesktopDoubleClick] Hook thread failed: {ex}");
        }
        finally
        {
            if (installedHook != IntPtr.Zero)
            {
                Win32Helper.UnhookWindowsHookEx(installedHook);
            }

            lock (_sync)
            {
                if (ReferenceEquals(_thread, Thread.CurrentThread))
                {
                    _thread = null;
                    _threadId = 0;
                    _hookHandle = IntPtr.Zero;
                }
            }
            ResetInputState(generation);
            ready.TrySetResult(true);
        }
    }

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        IntPtr hookHandle;
        long generation;
        lock (_sync)
        {
            hookHandle = _hookHandle;
            generation = _lifecycleGeneration;
        }

        if (nCode < 0 || wParam != (IntPtr)Win32Helper.WM_LBUTTONDOWN)
        {
            return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        Win32Helper.MSLLHOOKSTRUCT data =
            Marshal.PtrToStructure<Win32Helper.MSLLHOOKSTRUCT>(lParam);
        long receivedCount = Interlocked.Increment(ref _mouseDownCount);
        // Do not blanket-reject LLMHF_INJECTED: precision touchpads,
        // accessibility tools and remote-control software may legitimately set it.
        bool isInjected = (data.flags & MouseInjectedFlag) != 0;
        if (isInjected)
        {
            Interlocked.Increment(ref _injectedMouseDownCount);
        }

        QueueMouseDownForValidation(new QueuedMouseDown(
            generation,
            receivedCount,
            data,
            isInjected));
        return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    private void QueueMouseDownForValidation(QueuedMouseDown sample)
    {
        int queuedCount = Interlocked.Increment(ref _queuedMouseDownCount);
        if (queuedCount > MaximumQueuedMouseDowns)
        {
            Interlocked.Decrement(ref _queuedMouseDownCount);
            Interlocked.Increment(ref _droppedMouseDownCount);
            return;
        }

        _queuedMouseDowns.Enqueue(sample);
        ScheduleInputDrain();
    }

    private void ScheduleInputDrain()
    {
        if (Interlocked.Exchange(ref _inputDrainScheduled, 1) != 0)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static service => service.DrainQueuedMouseDowns(),
            this,
            preferLocal: false);
    }

    private void DrainQueuedMouseDowns()
    {
        try
        {
            while (_queuedMouseDowns.TryDequeue(out QueuedMouseDown sample))
            {
                Interlocked.Decrement(ref _queuedMouseDownCount);
                ProcessQueuedMouseDown(sample);
            }
        }
        finally
        {
            Volatile.Write(ref _inputDrainScheduled, 0);
            if (!_queuedMouseDowns.IsEmpty)
            {
                ScheduleInputDrain();
            }
        }
    }

    private void ProcessQueuedMouseDown(QueuedMouseDown sample)
    {
        if (!IsGenerationActive(sample.Generation))
        {
            return;
        }

        // Explorer's list-view hit test may perform cross-process memory IO and
        // wait for Explorer. It must never run inside LowLevelMouseProc because
        // Windows serializes that callback with the global input path.
        bool isDesktopBlank = DesktopBlankHitTest.IsBlankDesktopPoint(sample.Data.pt);
        if (!IsGenerationActive(sample.Generation))
        {
            return;
        }

        if (isDesktopBlank)
        {
            Interlocked.Increment(ref _blankMouseDownCount);
        }

        bool matched;
        DesktopDoubleClickSequence sequence;
        lock (_stateMachineSync)
        {
            matched = _stateMachine.Process(
                sample.Data.pt.X,
                sample.Data.pt.Y,
                sample.Data.time,
                isDesktopBlank,
                out sequence);
        }

        QueueInputDiagnostic(
            sample.ReceivedCount,
            sample.Data,
            isDesktopBlank,
            matched,
            sample.IsInjected
                ? isDesktopBlank ? "injected-desktop" : "injected-non-desktop"
                : isDesktopBlank ? "desktop" : "non-desktop");
        if (!matched || !IsGenerationActive(sample.Generation))
        {
            return;
        }

        Interlocked.Increment(ref _triggerCount);
        if (!App.UiDispatcherQueue.TryEnqueue(() => _ = InvokeAsync(sequence)))
        {
            Interlocked.Increment(ref _dispatchFailureCount);
        }
    }

    private bool IsGenerationActive(long generation)
    {
        lock (_sync)
        {
            return !_disposed &&
                generation == _lifecycleGeneration &&
                _hookHandle != IntPtr.Zero;
        }
    }

    private void ResetInputState(long? expectedGeneration = null)
    {
        lock (_sync)
        {
            if (expectedGeneration.HasValue &&
                expectedGeneration.Value != _lifecycleGeneration)
            {
                return;
            }

            while (_queuedMouseDowns.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _queuedMouseDownCount);
            }

            lock (_stateMachineSync)
            {
                _stateMachine.Reset();
            }
        }
    }

    private void QueueInputDiagnostic(
        long receivedCount,
        Win32Helper.MSLLHOOKSTRUCT data,
        bool isDesktopBlank,
        bool matched,
        string disposition)
    {
        if (!_diagnosticsEnabled)
        {
            return;
        }

        long injectedCount = Interlocked.Read(ref _injectedMouseDownCount);
        long blankCount = Interlocked.Read(ref _blankMouseDownCount);
        long droppedCount = Interlocked.Read(ref _droppedMouseDownCount);
        _ = App.UiDispatcherQueue.TryEnqueue(() => App.Log(
            $"[DesktopDoubleClick.Input] received={receivedCount} " +
            $"point={data.pt.X},{data.pt.Y} eventTime={data.time} " +
            $"flags=0x{data.flags:X} disposition={disposition} " +
            $"blank={isDesktopBlank} matched={matched} " +
            $"blankCount={blankCount} injectedCount={injectedCount} " +
            $"droppedCount={droppedCount}"));
    }

    private async Task InvokeAsync(DesktopDoubleClickSequence sequence)
    {
        try
        {
            await _invokeAsync(sequence);
        }
        catch (Exception ex)
        {
            App.Log($"[DesktopDoubleClick] Invocation failed: {ex}");
        }
    }

    private readonly record struct QueuedMouseDown(
        long Generation,
        long ReceivedCount,
        Win32Helper.MSLLHOOKSTRUCT Data,
        bool IsInjected);
}
