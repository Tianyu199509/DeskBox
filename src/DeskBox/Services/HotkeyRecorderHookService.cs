// Copyright (c) DeskBox. All rights reserved.

using System.Runtime.InteropServices;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Low-level keyboard hook used by the hotkey recorder dialog. While active it
/// reports every real key event on the UI dispatcher and swallows the input so
/// the OS cannot steal chords like Win+V or Alt+Space mid-recording — the same
/// approach PowerToys uses for its shortcut editor. Tab passes through so the
/// dialog remains keyboard-navigable, and Space/Enter pass through while a
/// preset button owns focus so presets stay operable from the keyboard.
/// Lifecycle mirrors <see cref="ReservedHotkeyHookService"/>: the hook lives on
/// a dedicated message-pump thread.
/// </summary>
internal sealed class HotkeyRecorderHookService : IDisposable
{
    internal readonly record struct RecordedKeyEvent(uint VirtualKey, bool IsKeyDown);

    private const int ErrorInvalidState = 87;
    private const int ErrorTimeout = 1460;
    private const int StartupTimeoutMilliseconds = 1500;
    private const int StopTimeoutMilliseconds = 500;
    private const int ForcedStopTimeoutMilliseconds = 150;
    private const uint VirtualKeyTab = 0x09;
    private const uint VirtualKeyReturn = 0x0D;
    private const uint VirtualKeySpace = 0x20;

    private readonly object _sync = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _uiDispatcherQueue;
    private readonly Win32Helper.LowLevelKeyboardProc _keyboardHookProc;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hookHandle;
    private int _startupError;
    private long _lifecycleGeneration;
    private volatile bool _passthroughRequested;
    private bool _disposed;

    public HotkeyRecorderHookService(
        Microsoft.UI.Dispatching.DispatcherQueue uiDispatcherQueue)
    {
        _uiDispatcherQueue = uiDispatcherQueue;
        _keyboardHookProc = KeyboardHookProc;
    }

    /// <summary>Raised on the UI dispatcher for every captured key event.</summary>
    public event Action<RecordedKeyEvent>? KeyCaptured;

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

    /// <summary>
    /// When a preset button owns focus, Space and Enter must reach it instead
    /// of being recorded, so the preset list stays keyboard-operable.
    /// </summary>
    public bool PassthroughRequested
    {
        get => _passthroughRequested;
        set => _passthroughRequested = value;
    }

    public bool TryStart(out int errorCode)
    {
        errorCode = 0;
        var ready = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread;
        long generation;
        lock (_sync)
        {
            if (_disposed)
            {
                errorCode = ErrorInvalidState;
                return false;
            }

            if (_thread?.IsAlive == true)
            {
                errorCode = ErrorTimeout;
                return false;
            }

            _startupError = 0;
            generation = ++_lifecycleGeneration;
            thread = new Thread(() => HookThreadMain(ready, generation))
            {
                IsBackground = true,
                Name = "DeskBox.HotkeyRecorderHook"
            };
            _thread = thread;
        }

        thread.Start();
        if (!ready.Task.Wait(StartupTimeoutMilliseconds))
        {
            errorCode = ErrorTimeout;
            Stop();
            return false;
        }

        lock (_sync)
        {
            if (_hookHandle != IntPtr.Zero && _thread?.IsAlive == true)
            {
                return true;
            }

            errorCode = _startupError == 0 ? ErrorInvalidState : _startupError;
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
        }

        if (thread is null)
        {
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
            }
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

            ready.TrySetResult(true);
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

            ready.TrySetResult(true);
        }
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        IntPtr hookHandle;
        lock (_sync)
        {
            hookHandle = _hookHandle;
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
        if ((data.flags & Win32Helper.LLKHF_INJECTED) != 0)
        {
            return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        // Tab keeps dialog navigation working; Space/Enter reach a focused
        // preset button instead of being recorded as a chord.
        if (data.vkCode == VirtualKeyTab ||
            (_passthroughRequested &&
             data.vkCode is VirtualKeyReturn or VirtualKeySpace))
        {
            return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }

        var captured = new RecordedKeyEvent(data.vkCode, isKeyDown);
        _uiDispatcherQueue.TryEnqueue(() => KeyCaptured?.Invoke(captured));
        return (IntPtr)1;
    }

    private void RecordLastWin32Error()
    {
        int errorCode = Marshal.GetLastWin32Error();
        lock (_sync)
        {
            if (_startupError == 0)
            {
                _startupError = errorCode == 0 ? 31 : errorCode;
            }
        }
    }
}
