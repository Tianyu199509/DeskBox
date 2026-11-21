// Copyright (c) DeskBox. All rights reserved.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

/// <summary>
/// Low-level keyboard hook used by the hotkey recorder dialog. While active and
/// while the host settings window owns foreground focus, it reports physical key
/// transitions on the UI dispatcher and swallows the input so the OS cannot
/// steal chords like Win+V or Alt+Space mid-recording — the same approach
/// PowerToys uses for its shortcut editor. Bare Tab passes through so the
/// dialog remains keyboard-navigable, and bare Space/Enter pass through while a
/// dialog button owns focus so buttons stay operable from the keyboard without
/// breaking Alt+Space / Win+Space / Ctrl+Space chord capture.
/// Lifecycle mirrors <see cref="ReservedHotkeyHookService"/>: the hook lives on
/// a dedicated message-pump thread.
/// </summary>
internal sealed class HotkeyRecorderHookService : IDisposable
{
    internal readonly record struct RecordedKeyEvent(
        uint VirtualKey,
        bool IsKeyDown,
        bool IsFocusLostReset = false);

    internal readonly record struct HookEvaluationResult(
        bool Swallow,
        bool InjectShellMask,
        RecordedKeyEvent? DispatchedEvent);

    internal static readonly IntPtr InjectedEventTag = new(0x44425243);

    private const int ErrorInvalidState = 87;
    private const int ErrorTimeout = 1460;
    private const int StartupTimeoutMilliseconds = 1500;
    private const int StopTimeoutMilliseconds = 500;
    private const int ForcedStopTimeoutMilliseconds = 150;
    private const ushort InternalMaskVirtualKey = 0xE8;
    private const uint LowerIlInjectedFlag = 0x02;
    private const uint VirtualKeyTab = 0x09;
    private const uint VirtualKeyReturn = 0x0D;
    private const uint VirtualKeyMenu = 0x12;
    private const uint VirtualKeySpace = 0x20;
    private const uint VirtualKeyLeftWin = 0x5B;
    private const uint VirtualKeyRightWin = 0x5C;
    private const uint VirtualKeyLeftMenu = 0xA4;
    private const uint VirtualKeyRightMenu = 0xA5;

    private readonly object _sync = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcherQueue;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueHandler _drainPendingEventsHandler;
    private readonly Win32Helper.LowLevelKeyboardProc _keyboardHookProc;
    private readonly ConcurrentQueue<RecordedKeyEvent> _pendingEvents = new();
    // Accessed exclusively on the dedicated hook thread (or deterministic test harness).
    private readonly HashSet<uint> _swallowedDownKeys = [];
    private bool _shellMaskSentForStroke;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hookHandle;
    private IntPtr _targetWindowHandle;
    private int _startupError;
    private int _drainScheduled;
    private long _lifecycleGeneration;
    private volatile bool _passthroughRequested;
    private volatile bool _isHostWindowActive = true;
    private bool _disposed;

    public HotkeyRecorderHookService(
        Microsoft.UI.Dispatching.DispatcherQueue? uiDispatcherQueue)
    {
        _uiDispatcherQueue = uiDispatcherQueue;
        _drainPendingEventsHandler = DrainPendingEvents;
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
    /// Host window handle that owns the recorder dialog. When non-zero, the
    /// hook passes input through untouched whenever foreground focus belongs to
    /// another window so external apps are never keyboard-locked.
    /// </summary>
    public IntPtr TargetWindowHandle
    {
        get => Volatile.Read(ref _targetWindowHandle);
        set => Volatile.Write(ref _targetWindowHandle, value);
    }

    /// <summary>
    /// Mirrors the host window's XAML activation state so mouse-driven focus
    /// loss suspends capture immediately alongside the Win32 foreground check.
    /// </summary>
    public bool IsHostWindowActive
    {
        get => _isHostWindowActive;
        set => _isHostWindowActive = value;
    }

    /// <summary>
    /// When a dialog button owns focus, bare Space and Enter must reach it
    /// instead of being recorded, so controls stay keyboard-operable.
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
            ClearPendingEvents();
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
            ClearPendingEvents();
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
            IntPtr hookHandle = Interlocked.Exchange(ref _hookHandle, IntPtr.Zero);
            if (hookHandle != IntPtr.Zero && !Win32Helper.UnhookWindowsHookEx(hookHandle))
            {
                RecordLastWin32Error();
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

        ClearPendingEvents();
        lock (_sync)
        {
            if (ReferenceEquals(_thread, thread) && !thread.IsAlive)
            {
                _thread = null;
                _threadId = 0;
                Volatile.Write(ref _hookHandle, IntPtr.Zero);
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

            // Windows silently drops WH_KEYBOARD_LL hooks that miss
            // LowLevelHooksTimeout under CPU contention; match
            // ReservedHotkeyHookService's elevated pump priority.
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            }
            catch
            {
            }

            _swallowedDownKeys.Clear();
            _shellMaskSentForStroke = false;
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
                ? Marshal.GetLastPInvokeError()
                : 0;

            bool startupWasCancelled;
            lock (_sync)
            {
                startupWasCancelled = _disposed || generation != _lifecycleGeneration;
                if (!startupWasCancelled)
                {
                    _threadId = threadId;
                    Volatile.Write(ref _hookHandle, installedHook);
                    installedHook = IntPtr.Zero;
                    _startupError = startupError;
                }
            }

            ready.TrySetResult(true);
            if (startupWasCancelled || Volatile.Read(ref _hookHandle) == IntPtr.Zero)
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
            IntPtr hookToUnhook = installedHook != IntPtr.Zero
                ? installedHook
                : (ReferenceEquals(_thread, Thread.CurrentThread)
                    ? Interlocked.Exchange(ref _hookHandle, IntPtr.Zero)
                    : IntPtr.Zero);
            if (hookToUnhook != IntPtr.Zero)
            {
                Win32Helper.UnhookWindowsHookEx(hookToUnhook);
            }

            GC.KeepAlive(_keyboardHookProc);
            _swallowedDownKeys.Clear();
            _shellMaskSentForStroke = false;
            lock (_sync)
            {
                if (ReferenceEquals(_thread, Thread.CurrentThread))
                {
                    _thread = null;
                    _threadId = 0;
                    Volatile.Write(ref _hookHandle, IntPtr.Zero);
                }
            }

            ready.TrySetResult(true);
        }
    }

    private unsafe IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        IntPtr hookHandle = Volatile.Read(ref _hookHandle);
        try
        {
            if (nCode < 0 ||
                lParam == IntPtr.Zero ||
                (wParam != Win32Helper.WM_KEYDOWN &&
                 wParam != Win32Helper.WM_SYSKEYDOWN &&
                 wParam != Win32Helper.WM_KEYUP &&
                 wParam != Win32Helper.WM_SYSKEYUP))
            {
                return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
            }

            ref readonly Win32Helper.KBDLLHOOKSTRUCT data =
                ref *(Win32Helper.KBDLLHOOKSTRUCT*)lParam;

            bool isInjected = (data.flags & (Win32Helper.LLKHF_INJECTED | LowerIlInjectedFlag)) != 0 ||
                              data.dwExtraInfo == InjectedEventTag;
            bool isTargetForeground = !isInjected && _isHostWindowActive && IsTargetWindowForeground();
            bool hasUnmaskedShellModifierDown = isTargetForeground && HasUnmaskedShellModifierPhysicallyDown();

            HookEvaluationResult evaluation = ProcessHookKeyEvent(
                nCode,
                wParam,
                data.vkCode,
                data.flags,
                data.dwExtraInfo,
                isTargetForeground,
                hasUnmaskedShellModifierDown);

            if (evaluation.InjectShellMask)
            {
                _ = Win32Helper.TrySendTaggedKeyPress(
                    InternalMaskVirtualKey,
                    InjectedEventTag,
                    out _);
            }

            if (evaluation.DispatchedEvent is { } captured)
            {
                EnqueueKeyEvent(captured);
            }

            return evaluation.Swallow
                ? (IntPtr)1
                : Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }
        catch
        {
            return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
        }
    }

    internal HookEvaluationResult ProcessHookKeyEvent(
        int nCode,
        IntPtr wParam,
        uint vkCode,
        uint flags,
        IntPtr extraInfo,
        bool isTargetForeground,
        bool hasUnmaskedShellModifierDown)
    {
        bool isKeyDown = wParam == Win32Helper.WM_KEYDOWN ||
                         wParam == Win32Helper.WM_SYSKEYDOWN;
        bool isKeyUp = wParam == Win32Helper.WM_KEYUP ||
                       wParam == Win32Helper.WM_SYSKEYUP;
        if (nCode < 0 || (!isKeyDown && !isKeyUp))
        {
            return default;
        }

        if ((flags & (Win32Helper.LLKHF_INJECTED | LowerIlInjectedFlag)) != 0 ||
            extraInfo == InjectedEventTag)
        {
            return default;
        }

        // Never swallow keystrokes destined for other windows when the host
        // settings window is deactivated or no longer owns foreground focus.
        if (!isTargetForeground)
        {
            if (_swallowedDownKeys.Count > 0)
            {
                _swallowedDownKeys.Clear();
                _shellMaskSentForStroke = false;
                return new HookEvaluationResult(
                    Swallow: false,
                    InjectShellMask: false,
                    DispatchedEvent: new RecordedKeyEvent(0, IsKeyDown: false, IsFocusLostReset: true));
            }

            return default;
        }

        // Never swallow a key-up for a key whose key-down occurred before the
        // hook was installed or was passed through to the dialog.
        if (isKeyUp && !_swallowedDownKeys.Contains(vkCode))
        {
            if (_swallowedDownKeys.Count == 0)
            {
                _shellMaskSentForStroke = false;
            }

            return default;
        }

        bool hasSwallowedModifiers = HasSwallowedModifiers();

        // Bare Tab keeps dialog navigation working; bare Space/Enter reach a
        // focused dialog button instead of being recorded, while modifier+Space,
        // modifier+Enter, and modifier+Tab are always captured as chords.
        // Only apply on key-down: any key-up reaching this point had its
        // key-down swallowed and must also be swallowed + removed cleanly even
        // if the modifier key was released first.
        if (isKeyDown &&
            !hasSwallowedModifiers &&
            !hasUnmaskedShellModifierDown &&
            (vkCode == VirtualKeyTab ||
             (_passthroughRequested &&
              vkCode is VirtualKeyReturn or VirtualKeySpace)))
        {
            return default;
        }

        if (isKeyDown)
        {
            bool injectShellMask = false;
            if (!_shellMaskSentForStroke &&
                (IsShellMenuModifierKey(vkCode) ||
                 hasUnmaskedShellModifierDown ||
                 HasSwallowedShellMenuModifiers()))
            {
                _shellMaskSentForStroke = true;
                injectShellMask = true;
            }

            bool isFirstKeyDown = _swallowedDownKeys.Add(vkCode);
            return new HookEvaluationResult(
                Swallow: true,
                InjectShellMask: injectShellMask,
                DispatchedEvent: isFirstKeyDown
                    ? new RecordedKeyEvent(vkCode, IsKeyDown: true)
                    : null);
        }

        _swallowedDownKeys.Remove(vkCode);
        if (_swallowedDownKeys.Count == 0)
        {
            _shellMaskSentForStroke = false;
        }

        return new HookEvaluationResult(
            Swallow: true,
            InjectShellMask: false,
            DispatchedEvent: new RecordedKeyEvent(vkCode, IsKeyDown: false));
    }

    private void EnqueueKeyEvent(RecordedKeyEvent captured)
    {
        if (_uiDispatcherQueue is null)
        {
            return;
        }

        _pendingEvents.Enqueue(captured);
        if (Interlocked.Exchange(ref _drainScheduled, 1) == 0 &&
            !_uiDispatcherQueue.TryEnqueue(_drainPendingEventsHandler))
        {
            Interlocked.Exchange(ref _drainScheduled, 0);
        }
    }

    private void DrainPendingEvents()
    {
        Interlocked.Exchange(ref _drainScheduled, 0);
        while (_pendingEvents.TryDequeue(out RecordedKeyEvent captured))
        {
            KeyCaptured?.Invoke(captured);
        }
    }

    private void ClearPendingEvents()
    {
        while (_pendingEvents.TryDequeue(out _))
        {
        }

        Interlocked.Exchange(ref _drainScheduled, 0);
    }

    private bool IsTargetWindowForeground()
    {
        IntPtr targetHwnd = Volatile.Read(ref _targetWindowHandle);
        if (targetHwnd == IntPtr.Zero)
        {
            return true;
        }

        IntPtr foreground = Win32Helper.GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        return foreground == targetHwnd ||
               Win32Helper.GetAncestor(foreground, Win32Helper.GA_ROOTOWNER) == targetHwnd;
    }

    private bool HasUnmaskedShellModifierPhysicallyDown()
    {
        return IsUnmaskedPhysicalDown(VirtualKeyLeftWin) ||
               IsUnmaskedPhysicalDown(VirtualKeyRightWin) ||
               IsUnmaskedPhysicalDown(VirtualKeyLeftMenu) ||
               IsUnmaskedPhysicalDown(VirtualKeyRightMenu) ||
               IsUnmaskedPhysicalDown(VirtualKeyMenu);
    }

    private bool IsUnmaskedPhysicalDown(uint vkCode)
    {
        return !_swallowedDownKeys.Contains(vkCode) &&
               Win32Helper.IsKeyDown((int)vkCode);
    }

    private bool HasSwallowedModifiers()
    {
        foreach (uint vkCode in _swallowedDownKeys)
        {
            if (HotkeyRecordingSession.TryGetModifierFlag(vkCode, out _))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasSwallowedShellMenuModifiers()
    {
        foreach (uint vkCode in _swallowedDownKeys)
        {
            if (IsShellMenuModifierKey(vkCode))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsShellMenuModifierKey(uint vkCode)
    {
        return vkCode is
            VirtualKeyLeftWin or
            VirtualKeyRightWin or
            VirtualKeyMenu or
            VirtualKeyLeftMenu or
            VirtualKeyRightMenu;
    }

    private void RecordLastWin32Error()
    {
        int errorCode = Marshal.GetLastPInvokeError();
        lock (_sync)
        {
            if (_startupError == 0)
            {
                _startupError = errorCode == 0 ? 31 : errorCode;
            }
        }
    }
}
