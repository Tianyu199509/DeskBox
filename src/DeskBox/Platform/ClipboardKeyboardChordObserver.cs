using System.Runtime.InteropServices;

namespace DeskBox.Platform;

internal enum ClipboardChordObservation
{
    None,
    DoubleCopyTriggered,
    ExternalPasteTriggered
}

/// <summary>
/// Pure, allocation-free state machine for physical Double-<c>Ctrl+C</c> detection
/// and external <c>Ctrl+V</c> / <c>Shift+Insert</c> paste-queue advancement.
/// </summary>
internal sealed class ClipboardKeyboardChordStateMachine
{
    internal const uint VkBack = 0x08;
    internal const uint VkShift = 0x10;
    internal const uint VkControl = 0x11;
    internal const uint VkMenu = 0x12;
    internal const uint VkInsert = 0x2D;
    internal const uint VkC = 0x43;
    internal const uint VkV = 0x56;
    internal const uint VkLWin = 0x5B;
    internal const uint VkRWin = 0x5C;
    internal const uint VkLShift = 0xA0;
    internal const uint VkRShift = 0xA1;
    internal const uint VkLControl = 0xA2;
    internal const uint VkRControl = 0xA3;
    internal const uint VkLMenu = 0xA4;
    internal const uint VkRMenu = 0xA5;

    public const long MinDoubleCopyIntervalMs = 60;
    public const long MaxDoubleCopyIntervalMs = 480;

    private bool _lCtrl;
    private bool _rCtrl;
    private bool _lShift;
    private bool _rShift;
    private bool _lAlt;
    private bool _rAlt;
    private bool _lWin;
    private bool _rWin;
    private bool _cHeld;
    private bool _pasteKeyHeld;
    private long _lastFirstCopyTickMs;
    private bool _hasFirstCopy;

    public bool IsControlDown => _lCtrl || _rCtrl;
    public bool IsShiftDown => _lShift || _rShift;
    public bool IsAltDown => _lAlt || _rAlt;
    public bool IsWinDown => _lWin || _rWin;

    public void Reset()
    {
        _lCtrl = _rCtrl = false;
        _lShift = _rShift = false;
        _lAlt = _rAlt = false;
        _lWin = _rWin = false;
        _cHeld = false;
        _pasteKeyHeld = false;
        _lastFirstCopyTickMs = 0;
        _hasFirstCopy = false;
    }

    public void SyncPhysicalModifiers(
        bool lCtrl,
        bool rCtrl,
        bool lShift,
        bool rShift,
        bool lAlt,
        bool rAlt,
        bool lWin,
        bool rWin)
    {
        _lCtrl = lCtrl;
        _rCtrl = rCtrl;
        _lShift = lShift;
        _rShift = rShift;
        _lAlt = lAlt;
        _rAlt = rAlt;
        _lWin = lWin;
        _rWin = rWin;
    }

    public ClipboardChordObservation ProcessKeyEvent(
        uint vkCode,
        bool isKeyDown,
        bool isInjected,
        long timestampMs,
        bool observeDoubleCopy,
        bool observeExternalPaste)
    {
        if (isInjected)
        {
            return ClipboardChordObservation.None;
        }

        if (TryUpdateModifier(vkCode, isKeyDown))
        {
            // Releasing Ctrl does not invalidate a fast Ctrl+C -> Ctrl+C double tap,
            // so users can either keep Ctrl held (Ctrl + C, C) or tap Ctrl+C twice!
            return ClipboardChordObservation.None;
        }

        if (!isKeyDown)
        {
            if (vkCode == VkC)
            {
                _cHeld = false;
            }
            else if (vkCode is VkV or VkInsert)
            {
                _pasteKeyHeld = false;
            }

            return ClipboardChordObservation.None;
        }

        // Physical key-down of a non-modifier key:
        if (vkCode == VkC)
        {
            // Ignore keyboard auto-repeat while 'C' remains physically depressed.
            if (_cHeld)
            {
                return ClipboardChordObservation.None;
            }

            _cHeld = true;
            if (observeDoubleCopy && IsControlDown && !IsShiftDown && !IsAltDown && !IsWinDown)
            {
                if (_hasFirstCopy)
                {
                    long delta = timestampMs - _lastFirstCopyTickMs;
                    if (delta >= MinDoubleCopyIntervalMs && delta <= MaxDoubleCopyIntervalMs)
                    {
                        _hasFirstCopy = false;
                        _lastFirstCopyTickMs = 0;
                        return ClipboardChordObservation.DoubleCopyTriggered;
                    }
                }

                _hasFirstCopy = true;
                _lastFirstCopyTickMs = timestampMs;
                return ClipboardChordObservation.None;
            }

            _hasFirstCopy = false;
            return ClipboardChordObservation.None;
        }

        // Any other non-modifier key breaks an armed Ctrl+C double-press sequence.
        _hasFirstCopy = false;

        bool isCtrlVPaste = vkCode == VkV && IsControlDown && !IsAltDown && !IsWinDown;
        bool isShiftInsertPaste = vkCode == VkInsert && IsShiftDown && !IsControlDown && !IsAltDown && !IsWinDown;
        if (observeExternalPaste && (isCtrlVPaste || isShiftInsertPaste))
        {
            if (_pasteKeyHeld)
            {
                return ClipboardChordObservation.None;
            }

            _pasteKeyHeld = true;
            return ClipboardChordObservation.ExternalPasteTriggered;
        }

        return ClipboardChordObservation.None;
    }

    private bool TryUpdateModifier(uint vkCode, bool isKeyDown)
    {
        switch (vkCode)
        {
            case VkControl:
            case VkLControl:
                _lCtrl = isKeyDown;
                return true;
            case VkRControl:
                _rCtrl = isKeyDown;
                return true;
            case VkShift:
            case VkLShift:
                _lShift = isKeyDown;
                return true;
            case VkRShift:
                _rShift = isKeyDown;
                return true;
            case VkMenu:
            case VkLMenu:
                _lAlt = isKeyDown;
                return true;
            case VkRMenu:
                _rAlt = isKeyDown;
                return true;
            case VkLWin:
                _lWin = isKeyDown;
                return true;
            case VkRWin:
                _rWin = isKeyDown;
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// Pass-through low-level keyboard observer running on its own message-pump thread
/// ONLY when Double-<c>Ctrl+C</c> or Paste Queue auto-advance is active. Never suppresses keys.
/// </summary>
internal sealed class ClipboardKeyboardChordObserver : IDisposable
{
    private const int StartupTimeoutMs = 1200;
    private const int StopTimeoutMs = 400;

    private readonly object _sync = new();
    private readonly ClipboardKeyboardChordStateMachine _stateMachine = new();
    private readonly Win32Helper.LowLevelKeyboardProc _hookProc;
    private readonly Action _onDoubleCopy;
    private readonly Action _onExternalPaste;

    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hookHandle;
    private long _generation;
    private volatile bool _observeDoubleCopy;
    private volatile bool _observeExternalPaste;
    private bool _disposed;

    public ClipboardKeyboardChordObserver(Action onDoubleCopy, Action onExternalPaste)
    {
        _onDoubleCopy = onDoubleCopy;
        _onExternalPaste = onExternalPaste;
        _hookProc = HookCallback;
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

    public void Configure(bool observeDoubleCopy, bool observeExternalPaste)
    {
        _observeDoubleCopy = observeDoubleCopy;
        _observeExternalPaste = observeExternalPaste;

        bool shouldRun = (observeDoubleCopy || observeExternalPaste) && !IsHookDisabledByEnvironment();
        if (shouldRun)
        {
            EnsureStarted();
        }
        else
        {
            Stop();
        }
    }

    private void EnsureStarted()
    {
        lock (_sync)
        {
            if (_disposed || (_hookHandle != IntPtr.Zero && _thread?.IsAlive == true))
            {
                return;
            }
        }

        Stop();

        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread;
        long gen;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _stateMachine.Reset();
            gen = ++_generation;
            thread = new Thread(() => HookThreadMain(ready, gen))
            {
                IsBackground = true,
                Name = "DeskBox.ClipboardChordObserver"
            };
            _thread = thread;
        }

        thread.Start();
        if (!ready.Task.Wait(StartupTimeoutMs))
        {
            Stop();
        }
    }

    public void Stop()
    {
        Thread? thread;
        uint threadId;
        lock (_sync)
        {
            _generation++;
            thread = _thread;
            threadId = _threadId;
        }

        if (thread is null)
        {
            lock (_sync)
            {
                _stateMachine.Reset();
            }

            return;
        }

        if (threadId != 0)
        {
            _ = Win32Helper.PostThreadMessage(threadId, Win32Helper.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
        }

        if (thread != Thread.CurrentThread && !thread.Join(StopTimeoutMs))
        {
            IntPtr hook;
            lock (_sync)
            {
                hook = _hookHandle;
                _hookHandle = IntPtr.Zero;
            }

            if (hook != IntPtr.Zero)
            {
                _ = Win32Helper.UnhookWindowsHookEx(hook);
            }

            if (threadId != 0)
            {
                _ = Win32Helper.PostThreadMessage(threadId, Win32Helper.WM_QUIT, UIntPtr.Zero, IntPtr.Zero);
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

            _stateMachine.Reset();
        }
    }

    private void HookThreadMain(TaskCompletionSource<bool> ready, long generation)
    {
        IntPtr installedHook = IntPtr.Zero;
        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
            lock (_sync)
            {
                if (_disposed || generation != _generation)
                {
                    ready.TrySetResult(true);
                    return;
                }
            }

            uint threadId = Win32Helper.GetCurrentThreadId();
            _ = Win32Helper.PeekMessage(out _, IntPtr.Zero, 0, 0, Win32Helper.PM_NOREMOVE);

            installedHook = Win32Helper.SetWindowsHookEx(
                Win32Helper.WH_KEYBOARD_LL,
                _hookProc,
                Win32Helper.GetModuleHandle(null),
                0);

            bool cancelled;
            lock (_sync)
            {
                cancelled = _disposed || generation != _generation;
                if (!cancelled)
                {
                    _threadId = threadId;
                    _hookHandle = installedHook;
                }
            }

            ready.TrySetResult(true);
            if (installedHook == IntPtr.Zero || cancelled)
            {
                return;
            }

            while (Win32Helper.GetMessage(out Win32Helper.MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                Win32Helper.TranslateMessage(in msg);
                Win32Helper.DispatchMessage(in msg);
            }
        }
        catch
        {
            ready.TrySetResult(true);
        }
        finally
        {
            if (installedHook != IntPtr.Zero)
            {
                _ = Win32Helper.UnhookWindowsHookEx(installedHook);
            }

            lock (_sync)
            {
                if (ReferenceEquals(_thread, Thread.CurrentThread))
                {
                    _thread = null;
                    _threadId = 0;
                    _hookHandle = IntPtr.Zero;
                }

                _stateMachine.Reset();
            }

            ready.TrySetResult(true);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        IntPtr hookHandle;
        lock (_sync)
        {
            hookHandle = _hookHandle;
        }

        bool isKeyDown = wParam == Win32Helper.WM_KEYDOWN || wParam == Win32Helper.WM_SYSKEYDOWN;
        bool isKeyUp = wParam == Win32Helper.WM_KEYUP || wParam == Win32Helper.WM_SYSKEYUP;
        if (nCode >= 0 && (isKeyDown || isKeyUp))
        {
            Win32Helper.KBDLLHOOKSTRUCT data = Marshal.PtrToStructure<Win32Helper.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (data.flags & Win32Helper.LLKHF_INJECTED) != 0;

            ClipboardChordObservation obs;
            lock (_sync)
            {
                if (!injected && isKeyDown &&
                    data.vkCode is ClipboardKeyboardChordStateMachine.VkC
                        or ClipboardKeyboardChordStateMachine.VkV
                        or ClipboardKeyboardChordStateMachine.VkInsert)
                {
                    _stateMachine.SyncPhysicalModifiers(
                        IsKeyPhysicallyDown(ClipboardKeyboardChordStateMachine.VkLControl),
                        IsKeyPhysicallyDown(ClipboardKeyboardChordStateMachine.VkRControl),
                        IsKeyPhysicallyDown(ClipboardKeyboardChordStateMachine.VkLShift),
                        IsKeyPhysicallyDown(ClipboardKeyboardChordStateMachine.VkRShift),
                        IsKeyPhysicallyDown(ClipboardKeyboardChordStateMachine.VkLMenu),
                        IsKeyPhysicallyDown(ClipboardKeyboardChordStateMachine.VkRMenu),
                        IsKeyPhysicallyDown(ClipboardKeyboardChordStateMachine.VkLWin),
                        IsKeyPhysicallyDown(ClipboardKeyboardChordStateMachine.VkRWin));
                }

                obs = _stateMachine.ProcessKeyEvent(
                    data.vkCode,
                    isKeyDown,
                    injected,
                    data.time,
                    _observeDoubleCopy,
                    _observeExternalPaste);
            }

            if (obs == ClipboardChordObservation.DoubleCopyTriggered)
            {
                IntPtr fg = ClipboardPlatformInterop.GetForegroundWindow();
                if (ClipboardPlatformInterop.IsEligibleExternalPasteTarget(fg))
                {
                    ThreadPool.UnsafeQueueUserWorkItem(static cb => cb(), _onDoubleCopy, preferLocal: false);
                }
            }
            else if (obs == ClipboardChordObservation.ExternalPasteTriggered)
            {
                IntPtr fg = ClipboardPlatformInterop.GetForegroundWindow();
                if (ClipboardPlatformInterop.IsEligibleExternalPasteTarget(fg))
                {
                    ThreadPool.UnsafeQueueUserWorkItem(static cb => cb(), _onExternalPaste, preferLocal: false);
                }
            }
        }

        return Win32Helper.CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    private static bool IsKeyPhysicallyDown(uint vkCode) =>
        (Win32Helper.GetAsyncKeyState((int)vkCode) & 0x8000) != 0;

    private static bool IsHookDisabledByEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable("DESKBOX_DISABLE_RESERVED_HOTKEY_HOOK");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
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
}
