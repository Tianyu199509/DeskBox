// Copyright (c) DeskBox. All rights reserved.

using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Installs the opt-in system-key hook on a dedicated message-pump thread.
/// The callback only updates a small state machine and posts a window message;
/// all DeskBox work remains on the normal UI dispatch path.
/// </summary>
internal sealed class ReservedHotkeyHookService : IDisposable
{
    private const int ErrorInvalidWindowHandle = 1400;
    private const int ErrorTimeout = 1460;
    private const int StartupTimeoutMilliseconds = 1500;
    private const int StopTimeoutMilliseconds = 500;
    private const int ForcedStopTimeoutMilliseconds = 150;
    internal const ushort InternalMaskVirtualKey = 0xE8;
    private static readonly IntPtr InjectedEventTag = new(0x44425753);

    private readonly object _sync = new();
    private readonly object _stateSync = new();
    private readonly Win32Helper.LowLevelKeyboardProc _keyboardHookProc;
    private WinSpaceHotkeyStateMachine _modifierSpaceStateMachine = new();
    private readonly WindowsTapHotkeyStateMachine _windowsTapStateMachine = new();
    private readonly DoubleControlHotkeyStateMachine _doubleControlStateMachine = new();
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hookHandle;
    private IntPtr _notificationWindow;
    private uint _notificationMessage;
    private int _startupError;
    private int _lastErrorCode;
    private long _triggerCount;
    private long _postFailureCount;
    private long _inputFailureCount;
    private long _lifecycleGeneration;
    private ReservedHotkeyMode _mode = ReservedHotkeyMode.WinSpace;
    private bool _disposed;

    public ReservedHotkeyHookService()
    {
        _keyboardHookProc = KeyboardHookProc;
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

    public uint ThreadId
    {
        get
        {
            lock (_sync)
            {
                return _thread?.IsAlive == true ? _threadId : 0;
            }
        }
    }

    public int LastErrorCode => Volatile.Read(ref _lastErrorCode);
    public long TriggerCount => Interlocked.Read(ref _triggerCount);
    public long PostFailureCount => Interlocked.Read(ref _postFailureCount);
    public long InputFailureCount => Interlocked.Read(ref _inputFailureCount);

    internal static bool IsInternalMaskKey(int virtualKey)
    {
        return virtualKey == InternalMaskVirtualKey;
    }

    public bool TryStart(IntPtr notificationWindow, uint notificationMessage, out int errorCode)
    {
        return TryStart(
            notificationWindow,
            notificationMessage,
            ReservedHotkeyMode.WinSpace,
            out errorCode);
    }

    public bool TryStart(
        IntPtr notificationWindow,
        uint notificationMessage,
        ReservedHotkeyMode mode,
        out int errorCode)
    {
        errorCode = 0;
        if (notificationWindow == IntPtr.Zero || !Win32Helper.IsWindow(notificationWindow))
        {
            errorCode = ErrorInvalidWindowHandle;
            Volatile.Write(ref _lastErrorCode, errorCode);
            return false;
        }

        Stop();

        var ready = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread;
        long generation;
        lock (_sync)
        {
            if (_disposed)
            {
                errorCode = ErrorInvalidWindowHandle;
                return false;
            }

            if (_thread?.IsAlive == true)
            {
                errorCode = ErrorTimeout;
                return false;
            }

            _notificationWindow = notificationWindow;
            _notificationMessage = notificationMessage;
            _startupError = 0;
            Volatile.Write(ref _lastErrorCode, 0);
            ConfigureStateMachine(mode);
            generation = ++_lifecycleGeneration;
            thread = new Thread(() => HookThreadMain(ready, generation))
            {
                IsBackground = true,
                Name = "DeskBox.ReservedHotkeyHook"
            };
            _thread = thread;
        }

        thread.Start();
        if (!ready.Task.Wait(StartupTimeoutMilliseconds))
        {
            errorCode = ErrorTimeout;
            Volatile.Write(ref _lastErrorCode, errorCode);
            Stop();
            return false;
        }

        lock (_sync)
        {
            if (_hookHandle != IntPtr.Zero && _thread?.IsAlive == true)
            {
                return true;
            }

            errorCode = _startupError == 0 ? ErrorInvalidWindowHandle : _startupError;
            Volatile.Write(ref _lastErrorCode, errorCode);
        }

        Stop();
        return false;
    }

    public void Stop()
    {
        Thread? thread;
        uint threadId;
        lock (_sync)
        {
            // Invalidate a hook thread that starts after its caller already
            // timed out. The late thread checks this generation before and
            // after SetWindowsHookEx and exits without entering a message pump.
            _lifecycleGeneration++;
            thread = _thread;
            threadId = _threadId;
            // Make a delayed callback fail open even if the hook thread takes
            // longer than expected to leave its message pump.
            _notificationWindow = IntPtr.Zero;
            _notificationMessage = 0;
        }

        if (thread is null)
        {
            ResetStateMachine();
            return;
        }

        if (threadId != 0)
        {
            if (!Win32Helper.PostThreadMessage(
                    threadId,
                    Win32Helper.WM_QUIT,
                    UIntPtr.Zero,
                    IntPtr.Zero))
            {
                RecordLastWin32Error();
            }
        }

        if (thread != Thread.CurrentThread && !thread.Join(StopTimeoutMilliseconds))
        {
            IntPtr hookHandle;
            lock (_sync)
            {
                hookHandle = _hookHandle;
            }

            if (hookHandle != IntPtr.Zero)
            {
                if (Win32Helper.UnhookWindowsHookEx(hookHandle))
                {
                    lock (_sync)
                    {
                        if (_hookHandle == hookHandle)
                        {
                            _hookHandle = IntPtr.Zero;
                        }
                    }
                }
                else
                {
                    RecordLastWin32Error();
                }
            }

            if (threadId != 0)
            {
                if (!Win32Helper.PostThreadMessage(
                        threadId,
                        Win32Helper.WM_QUIT,
                        UIntPtr.Zero,
                        IntPtr.Zero))
                {
                    RecordLastWin32Error();
                }
            }

            thread.Join(ForcedStopTimeoutMilliseconds);
        }

        lock (_sync)
        {
            if (ReferenceEquals(_thread, thread) && !thread.IsAlive)
            {
                _thread = null;
                _threadId = 0;
                _hookHandle = IntPtr.Zero;
                _notificationWindow = IntPtr.Zero;
                _notificationMessage = 0;
            }
        }

        ResetStateMachine();
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

            installedHook = Win32Helper.SetWindowsHookEx(
                Win32Helper.WH_KEYBOARD_LL,
                _keyboardHookProc,
                Win32Helper.GetModuleHandle(null),
                0);
            int startupError = installedHook == IntPtr.Zero
                ? Marshal.GetLastWin32Error()
                : 0;
            if (startupError != 0)
            {
                Volatile.Write(ref _lastErrorCode, startupError);
            }

            bool startupWasCancelled;
            lock (_sync)
            {
                startupWasCancelled = _disposed || generation != _lifecycleGeneration;
                if (!startupWasCancelled)
                {
                    _threadId = threadId;
                    _hookHandle = installedHook;
                    _startupError = startupError;
                }
            }

            ready.TrySetResult(true);
            if (installedHook == IntPtr.Zero || startupWasCancelled)
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
            int startupError = Marshal.GetHRForException(ex);
            lock (_sync)
            {
                if (_startupError == 0)
                {
                    _startupError = startupError;
                }
            }
            Volatile.Write(ref _lastErrorCode, startupError);
            ready.TrySetResult(true);
        }
        finally
        {
            if (installedHook != IntPtr.Zero)
            {
                Win32Helper.UnhookWindowsHookEx(installedHook);
            }

            ResetStateMachine();
            lock (_sync)
            {
                if (ReferenceEquals(_thread, Thread.CurrentThread))
                {
                    _thread = null;
                    _threadId = 0;
                    _hookHandle = IntPtr.Zero;
                    _notificationWindow = IntPtr.Zero;
                    _notificationMessage = 0;
                }
            }
            ready.TrySetResult(true);
        }
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        IntPtr hookHandle;
        IntPtr notificationWindow;
        uint notificationMessage;
        lock (_sync)
        {
            hookHandle = _hookHandle;
            notificationWindow = _notificationWindow;
            notificationMessage = _notificationMessage;
        }

        bool isKeyDown = wParam == Win32Helper.WM_KEYDOWN ||
                         wParam == Win32Helper.WM_SYSKEYDOWN;
        bool isKeyUp = wParam == Win32Helper.WM_KEYUP ||
                       wParam == Win32Helper.WM_SYSKEYUP;
        if (nCode < 0 || (!isKeyDown && !isKeyUp))
        {
            return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        Win32Helper.KBDLLHOOKSTRUCT data =
            Marshal.PtrToStructure<Win32Helper.KBDLLHOOKSTRUCT>(lParam);
        if ((data.flags & Win32Helper.LLKHF_INJECTED) != 0 ||
            data.dwExtraInfo == InjectedEventTag)
        {
            return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        ReservedHotkeyEventDisposition disposition;
        ReservedHotkeyMode mode;
        lock (_stateSync)
        {
            mode = _mode;
            disposition = mode switch
            {
                ReservedHotkeyMode.DoubleControl =>
                    _doubleControlStateMachine.Process(data.vkCode, isKeyDown, data.time),
                ReservedHotkeyMode.WindowsTap =>
                    _windowsTapStateMachine.Process(data.vkCode, isKeyDown),
                _ => _modifierSpaceStateMachine.Process(data.vkCode, isKeyDown)
            };
        }
        if (disposition == ReservedHotkeyEventDisposition.PassThrough)
        {
            return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        if (disposition is
            ReservedHotkeyEventDisposition.TriggerAndSuppress or
            ReservedHotkeyEventDisposition.TriggerAndPassThrough)
        {
            if (notificationWindow == IntPtr.Zero || notificationMessage == 0)
            {
                CancelSuppression();
                return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
            }

            bool requiresSystemMask = mode is
                ReservedHotkeyMode.WinSpace or
                ReservedHotkeyMode.AltSpace or
                ReservedHotkeyMode.WindowsTap;
            if (requiresSystemMask &&
                !Win32Helper.TrySendTaggedKeyPress(
                    InternalMaskVirtualKey,
                    InjectedEventTag,
                    out int inputError))
            {
                Interlocked.Increment(ref _inputFailureCount);
                Volatile.Write(ref _lastErrorCode, inputError);
                CancelSuppression();
                return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
            }

            bool posted = Win32Helper.PostMessage(
                notificationWindow,
                notificationMessage,
                UIntPtr.Zero,
                IntPtr.Zero);
            if (!posted)
            {
                Interlocked.Increment(ref _postFailureCount);
                RecordLastWin32Error();
                CancelSuppression();
                return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
            }

            Interlocked.Increment(ref _triggerCount);

            if (disposition == ReservedHotkeyEventDisposition.TriggerAndPassThrough)
            {
                return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
            }
        }

        return (IntPtr)1;
    }

    private void CancelSuppression()
    {
        lock (_stateSync)
        {
            if (_mode is ReservedHotkeyMode.WinSpace or ReservedHotkeyMode.AltSpace)
            {
                _modifierSpaceStateMachine.CancelSuppression();
            }
        }
    }

    private void ConfigureStateMachine(ReservedHotkeyMode mode)
    {
        lock (_stateSync)
        {
            _mode = mode;
            _modifierSpaceStateMachine = mode == ReservedHotkeyMode.AltSpace
                ? new WinSpaceHotkeyStateMachine(ReservedHotkeyMode.AltSpace)
                : new WinSpaceHotkeyStateMachine();
            _windowsTapStateMachine.Reset();
            _doubleControlStateMachine.Reset();
        }
    }

    private void ResetStateMachine()
    {
        lock (_stateSync)
        {
            _modifierSpaceStateMachine.Reset();
            _windowsTapStateMachine.Reset();
            _doubleControlStateMachine.Reset();
        }
    }

    private void RecordLastWin32Error()
    {
        int errorCode = Marshal.GetLastWin32Error();
        Volatile.Write(ref _lastErrorCode, errorCode == 0 ? 31 : errorCode);
    }
}
