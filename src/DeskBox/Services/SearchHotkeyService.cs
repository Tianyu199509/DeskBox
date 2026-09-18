using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using Windows.System;

namespace DeskBox.Services;

/// <summary>
/// Manages the global hotkey for invoking the search popup. Ordinary gestures
/// use the standard Win32 RegisterHotKey + WM_HOTKEY mechanism; the opt-in
/// Alt+Space preset rides the reserved low-level hook, matching how the main
/// hotkey handles system-reserved gestures.
/// </summary>
public sealed class SearchHotkeyService : IDisposable
{
    private const int SearchHotkeyId = 0x4444;
    private const uint WmReservedSearchHotkey = 0x8444;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private static readonly UIntPtr SubclassId = new(0x4444);

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly Func<Task> _invokeAsync;
    private readonly Win32Helper.SubclassProc _subclassProc;
    private readonly ReservedHotkeyHookService _reservedHotkeyHook = new();
    private IntPtr _windowHandle;
    private bool _isSubclassInstalled;
    private bool _isRegistered;
    private bool _usesReservedHook;
    private bool _isInvoking;
    private long _receivedSequence;
    private long _invocationSequence;
    private long _dispatchFailureSequence;

    public static GlobalHotkeyGesture AltSpaceGesture { get; } = new(
        HotkeyModifierKeys.Alt,
        (int)VirtualKey.Space);

    public SearchHotkeyService(
        SettingsService settingsService,
        LocalizationService localizationService,
        Func<Task> invokeAsync)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _invokeAsync = invokeAsync;
        _subclassProc = WindowSubclassProc;
    }

    public bool IsRegistered => _isRegistered &&
        (!_usesReservedHook || _reservedHotkeyHook.IsActive);
    public bool UsesReservedHook => _usesReservedHook && IsRegistered;
    public long ReceivedCount => Interlocked.Read(ref _receivedSequence);
    public long InvocationCount => Interlocked.Read(ref _invocationSequence);
    public long DispatchFailureCount => Interlocked.Read(ref _dispatchFailureSequence);

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
        RefreshRegistration();
    }

    public void Detach()
    {
        Unregister();
        RemoveSubclass();
        _windowHandle = IntPtr.Zero;
    }

    public void RefreshRegistration()
    {
        Unregister();

        if (_windowHandle == IntPtr.Zero || !_settingsService.Settings.SearchHotkeyEnabled)
        {
            RemoveSubclass();
            return;
        }

        if (!_isSubclassInstalled)
        {
            _isSubclassInstalled = Win32Helper.SetWindowSubclass(
                _windowHandle,
                _subclassProc,
                SubclassId,
                UIntPtr.Zero);
            if (!_isSubclassInstalled)
            {
                App.Log("[SearchHotkey] Failed to install tray window subclass");
                return;
            }
        }

        var gesture = CurrentGesture;
        if (gesture.Equals(AltSpaceGesture))
        {
            ApplyReservedAltSpaceGesture();
            return;
        }

        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Windows) ||
            !GlobalHotkeyService.IsValidGesture(gesture))
        {
            return;
        }

        if (Register(_windowHandle, gesture))
        {
            _isRegistered = true;
            App.Log($"[SearchHotkey] Registered gesture={FormatGesture(gesture)}");
        }
        else
        {
            App.Log($"[SearchHotkey] Failed to register gesture={FormatGesture(gesture)} (may be in use by another app)");
        }
    }

    private void ApplyReservedAltSpaceGesture()
    {
        if (IsGestureOwnedByMainHotkey())
        {
            App.Log("[SearchHotkey] Alt+Space is owned by the main hotkey; staying unregistered");
            return;
        }

        if (IsReservedHookDisabledByEnvironment())
        {
            App.Log("[SearchHotkey] Reserved hotkey hook disabled by environment");
            return;
        }

        bool hookStarted;
        int hookError;
        try
        {
            hookStarted = _reservedHotkeyHook.TryStart(
                _windowHandle,
                WmReservedSearchHotkey,
                ReservedHotkeyMode.AltSpace,
                out hookError);
        }
        catch (Exception ex)
        {
            hookStarted = false;
            hookError = Marshal.GetHRForException(ex);
            App.Log($"[SearchHotkey] Reserved hook startup threw: {ex}");
        }

        if (hookStarted)
        {
            _isRegistered = true;
            _usesReservedHook = true;
            App.Log("[SearchHotkey] Registered reserved gesture=Alt + Space mode=hook");
            return;
        }

        App.Log($"[SearchHotkey] Reserved hook registration failed error={hookError}");
    }

    internal bool IsGestureOwnedByMainHotkey()
    {
        return App.Current?.GlobalHotkeyService?.CurrentActivation is
            { Kind: HotkeyActivationKind.Chord } activation &&
            activation.Gesture.Equals(AltSpaceGesture);
    }

    private static bool IsReservedHookDisabledByEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable("DESKBOX_DISABLE_RESERVED_HOTKEY_HOOK");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public bool TryApplyGesture(GlobalHotkeyGesture gesture)
    {
        return TryApplyGesture(gesture, out _);
    }

    public bool TryApplyGesture(GlobalHotkeyGesture gesture, out string? error)
    {
        error = null;
        gesture = GlobalHotkeyService.NormalizeGesture((int)gesture.Modifiers, gesture.VirtualKey);
        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Windows) ||
            !GlobalHotkeyService.IsValidGesture(gesture))
        {
            error = _localizationService.T("Settings.GlobalHotkey.Status.Invalid");
            return false;
        }

        if (gesture.Equals(AltSpaceGesture) && IsGestureOwnedByMainHotkey())
        {
            error = _localizationService.T("Settings.Search.Hotkey.Status.GlobalHotkeyConflict");
            return false;
        }

        var settings = _settingsService.Settings;
        int previousModifiers = settings.SearchHotkeyModifiers;
        int previousVirtualKey = settings.SearchHotkeyKey;
        var previousGesture = GlobalHotkeyService.NormalizeGesture(
            previousModifiers,
            previousVirtualKey);
        bool isCurrentGesture = gesture.Equals(previousGesture);
        bool shouldBeActive = _windowHandle != IntPtr.Zero && settings.SearchHotkeyEnabled;

        if (isCurrentGesture)
        {
            if (shouldBeActive && !IsRegistered)
            {
                RefreshRegistration();
                return IsRegistered;
            }

            return true;
        }

        settings.SearchHotkeyModifiers = (int)gesture.Modifiers;
        settings.SearchHotkeyKey = gesture.VirtualKey;

        if (!shouldBeActive)
        {
            _settingsService.SaveDebounced();
            return true;
        }

        // As with the main hotkey, the real RegisterHotKey call is the commit
        // point. Restore both settings and registration if the requested
        // gesture is already owned by another process or hotkey id.
        RefreshRegistration();
        if (IsRegistered)
        {
            _settingsService.SaveDebounced();
            return true;
        }

        settings.SearchHotkeyModifiers = previousModifiers;
        settings.SearchHotkeyKey = previousVirtualKey;
        RefreshRegistration();
        if (!IsRegistered)
        {
            App.Log(
                $"[SearchHotkey] Rollback registration failed previousGesture=" +
                $"{FormatGesture(previousGesture)}");
        }

        return false;
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

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (message == WmReservedSearchHotkey && _usesReservedHook)
        {
            long reservedReceivedId = Interlocked.Increment(ref _receivedSequence);
            // The Alt+Space hook path owns its Alt key-up state and never
            // injects synthetic modifier releases.
            if (App.UiDispatcherQueue.TryEnqueue(() =>
            {
                _ = InvokeAsync();
            }))
            {
                return IntPtr.Zero;
            }

            Interlocked.Increment(ref _dispatchFailureSequence);
            App.Log($"[SearchHotkey] UI dispatch rejected id={reservedReceivedId}");
            return IntPtr.Zero;
        }

        if (message == GlobalHotkeyService.WmHotkey &&
            wParam.ToUInt32() == SearchHotkeyId)
        {
            long receivedId = Interlocked.Increment(ref _receivedSequence);
            // Immediately release all modifier keys to clear any stuck state.
            // In RDP sessions, the modifier key-up event can be lost or delayed,
            // leaving the system thinking Alt is still held.  This would cause
            // every subsequent press of the gesture key (e.g. 'D') to be
            // intercepted as Alt+D by RegisterHotKey, making the key appear dead.
            Win32Helper.ReleaseAllModifiers();

            if (App.UiDispatcherQueue.TryEnqueue(() =>
            {
                _ = InvokeAsync();
            }))
            {
                return IntPtr.Zero;
            }

            Interlocked.Increment(ref _dispatchFailureSequence);
            App.Log($"[SearchHotkey] UI dispatch rejected id={receivedId}");
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
        long invocationId = Interlocked.Increment(ref _invocationSequence);
        App.Log($"[SearchHotkey] Triggered id={invocationId}");
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
        if (_usesReservedHook || _reservedHotkeyHook.IsActive)
        {
            try
            {
                _reservedHotkeyHook.Stop();
            }
            catch (Exception ex)
            {
                App.Log($"[SearchHotkey] Reserved hook removal failed: {ex}");
            }
        }
        else if (_isRegistered && _windowHandle != IntPtr.Zero)
        {
            Win32Helper.UnregisterHotKey(_windowHandle, SearchHotkeyId);
        }

        _isRegistered = false;
        _usesReservedHook = false;
    }

    private void RemoveSubclass()
    {
        if (_isSubclassInstalled && _windowHandle != IntPtr.Zero)
        {
            Win32Helper.RemoveWindowSubclass(_windowHandle, _subclassProc, SubclassId);
        }

        _isSubclassInstalled = false;
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
        _reservedHotkeyHook.Dispose();
    }
}
