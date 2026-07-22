using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Manages the global hotkey for invoking the search popup.
/// Operates independently from the main F7 show/hide hotkey.
/// </summary>
public sealed class SearchHotkeyService : IDisposable
{
    private const int SearchHotkeyId = 0x4444;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private static readonly UIntPtr SubclassId = new(0x4444);

    private readonly SettingsService _settingsService;
    private readonly Func<Task> _invokeAsync;
    private readonly Win32Helper.SubclassProc _subclassProc;
    private readonly Win32Helper.LowLevelKeyboardProc _keyboardHookProc;
    private IntPtr _windowHandle;
    private IntPtr _keyboardHookHandle;
    private bool _isSubclassInstalled;
    private bool _isRegistered;
    private bool _isInvoking;
    private bool _hookGestureIsDown;

    public SearchHotkeyService(
        SettingsService settingsService,
        Func<Task> invokeAsync)
    {
        _settingsService = settingsService;
        _invokeAsync = invokeAsync;
        _subclassProc = WindowSubclassProc;
        _keyboardHookProc = KeyboardHookProc;
    }

    public bool IsRegistered => _isRegistered;

    public GlobalHotkeyGesture CurrentGesture => GlobalHotkeyService.NormalizeGesture(
        _settingsService.Settings.SearchHotkeyModifiers,
        _settingsService.Settings.SearchHotkeyKey);

    public void Attach(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        Detach();
        _windowHandle = windowHandle;
        _isSubclassInstalled = Win32Helper.SetWindowSubclass(
            _windowHandle, _subclassProc, SubclassId, UIntPtr.Zero);
        RefreshRegistration();
    }

    public void Detach()
    {
        Unregister();
        if (_isSubclassInstalled && _windowHandle != IntPtr.Zero)
        {
            Win32Helper.RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
        }

        _isSubclassInstalled = false;
        _windowHandle = IntPtr.Zero;
        UninstallKeyboardHook();
    }

    public void RefreshRegistration()
    {
        Unregister();

        if (_windowHandle == IntPtr.Zero || !_settingsService.Settings.SearchHotkeyEnabled)
        {
            UninstallKeyboardHook();
            return;
        }

        var gesture = CurrentGesture;
        if (!GlobalHotkeyService.IsValidGesture(gesture))
        {
            UninstallKeyboardHook();
            return;
        }

        if (Register(_windowHandle, gesture))
        {
            _isRegistered = true;
            App.Log($"[SearchHotkey] Registered gesture={FormatGesture(gesture)}; hookCompanion=active");
        }
        else
        {
            App.Log($"[SearchHotkey] Failed to register gesture={FormatGesture(gesture)}; hookFallback=active");
        }

        // Always keep the low-level hook installed as a companion path: Windows does
        // not deliver WM_HOTKEY to a non-elevated process while an elevated app owns
        // the foreground (UIPI), but WH_KEYBOARD_LL still receives the keystroke.
        // The hook suppresses the gesture key, so both paths never double-fire.
        InstallKeyboardHook();
    }

    public bool TryApplyGesture(GlobalHotkeyGesture gesture)
    {
        gesture = GlobalHotkeyService.NormalizeGesture((int)gesture.Modifiers, gesture.VirtualKey);
        if (!GlobalHotkeyService.IsValidGesture(gesture))
        {
            return false;
        }

        var settings = _settingsService.Settings;
        settings.SearchHotkeyModifiers = (int)gesture.Modifiers;
        settings.SearchHotkeyKey = gesture.VirtualKey;
        _settingsService.SaveDebounced();
        RefreshRegistration();
        return true;
    }

    public void SetEnabled(bool enabled)
    {
        if (_settingsService.Settings.SearchHotkeyEnabled == enabled)
        {
            return;
        }

        _settingsService.Settings.SearchHotkeyEnabled = enabled;
        _settingsService.SaveDebounced();
        RefreshRegistration();
    }

    private void InstallKeyboardHook()
    {
        if (_keyboardHookHandle != IntPtr.Zero)
        {
            return;
        }

        _keyboardHookHandle = Win32Helper.SetWindowsHookEx(
            Win32Helper.WH_KEYBOARD_LL,
            _keyboardHookProc,
            Win32Helper.GetModuleHandle(null),
            0);
        if (_keyboardHookHandle == IntPtr.Zero)
        {
            App.Log($"[SearchHotkey] Failed to install low-level keyboard hook error={Marshal.GetLastWin32Error()}");
            return;
        }

        App.Log("[SearchHotkey] Low-level keyboard hook installed");
    }

    private void UninstallKeyboardHook()
    {
        if (_keyboardHookHandle == IntPtr.Zero)
        {
            return;
        }

        Win32Helper.UnhookWindowsHookEx(_keyboardHookHandle);
        _keyboardHookHandle = IntPtr.Zero;
        _hookGestureIsDown = false;
        App.Log("[SearchHotkey] Low-level keyboard hook removed");
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        bool isKeyDown = wParam == Win32Helper.WM_KEYDOWN || wParam == Win32Helper.WM_SYSKEYDOWN;
        bool isKeyUp = wParam == Win32Helper.WM_KEYUP || wParam == Win32Helper.WM_SYSKEYUP;
        if (nCode < 0 ||
            !_settingsService.Settings.SearchHotkeyEnabled ||
            (!isKeyDown && !isKeyUp))
        {
            return Win32Helper.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        var data = Marshal.PtrToStructure<Win32Helper.KBDLLHOOKSTRUCT>(lParam);
        var gesture = CurrentGesture;
        if (!GlobalHotkeyService.IsValidGesture(gesture) ||
            data.vkCode != (uint)gesture.VirtualKey)
        {
            return Win32Helper.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        if (isKeyUp)
        {
            _hookGestureIsDown = false;
            return (IntPtr)1;
        }

        if (!GlobalHotkeyService.AreCurrentModifiersPressed(gesture.Modifiers))
        {
            return Win32Helper.CallNextHookEx(_keyboardHookHandle, nCode, wParam, lParam);
        }

        if (_hookGestureIsDown)
        {
            return (IntPtr)1;
        }

        _hookGestureIsDown = true;
        App.UiDispatcherQueue.TryEnqueue(async () =>
        {
            await InvokeAsync();
        });
        return (IntPtr)1;
    }

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (message == GlobalHotkeyService.WmHotkey &&
            wParam.ToUInt32() == SearchHotkeyId)
        {
            App.UiDispatcherQueue.TryEnqueue(async () =>
            {
                await InvokeAsync();
            });
            return IntPtr.Zero;
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private async Task InvokeAsync()
    {
        if (_isInvoking)
        {
            return;
        }

        _isInvoking = true;
        App.Log("[SearchHotkey] Triggered");
        try
        {
            await _invokeAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[SearchHotkey] Invocation failed: {ex}");
        }
        finally
        {
            _isInvoking = false;
        }
    }

    private void Unregister()
    {
        if (_isRegistered && _windowHandle != IntPtr.Zero)
        {
            Win32Helper.UnregisterHotKey(_windowHandle, SearchHotkeyId);
        }

        _isRegistered = false;
    }

    private static bool Register(IntPtr windowHandle, GlobalHotkeyGesture gesture)
    {
        return Win32Helper.RegisterHotKey(
            windowHandle,
            SearchHotkeyId,
            ToWin32Modifiers(gesture.Modifiers) | ModNoRepeat,
            (uint)gesture.VirtualKey);
    }

    private static uint ToWin32Modifiers(HotkeyModifierKeys modifiers)
    {
        uint value = 0;
        if (modifiers.HasFlag(HotkeyModifierKeys.Alt))
        {
            value |= ModAlt;
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Control))
        {
            value |= ModControl;
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Shift))
        {
            value |= ModShift;
        }

        return value;
    }

    private static string FormatGesture(GlobalHotkeyGesture gesture)
    {
        var parts = new List<string>();
        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Control))
        {
            parts.Add("Ctrl");
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Alt))
        {
            parts.Add("Alt");
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add($"VK:{gesture.VirtualKey:X2}");
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        Detach();
    }
}
