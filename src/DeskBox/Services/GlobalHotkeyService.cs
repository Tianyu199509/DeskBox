using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using Windows.System;

namespace DeskBox.Services;

public sealed class GlobalHotkeyService : IDisposable
{
    public const uint WmHotkey = 0x0312;
    private const uint WmReservedHotkey = 0x8442;
    private const int MainHotkeyId = 0x4442;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private static readonly UIntPtr SubclassId = new(0x4442);

    // Hardware Copilot keys deliver Left Shift + Win + F23 on most keyboards,
    // so the preset registers that chord instead of adding a new activation kind.
    public static GlobalHotkeyGesture CopilotKeyGesture { get; } = new(
        HotkeyModifierKeys.Windows | HotkeyModifierKeys.Shift,
        (int)VirtualKey.F23);

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly Func<Task> _invokeAsync;
    private readonly Win32Helper.SubclassProc _subclassProc;
    private readonly ReservedHotkeyHookService _reservedHotkeyHook = new();
    private IntPtr _windowHandle;
    private bool _isSubclassInstalled;
    private bool _isRegistered;
    private bool _usesReservedHook;
    private long _receivedSequence;
    private long _invocationSequence;
    private long _dispatchFailureSequence;

    public GlobalHotkeyService(
        SettingsService settingsService,
        LocalizationService localizationService,
        Func<Task> invokeAsync)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _invokeAsync = invokeAsync;
        _subclassProc = WindowSubclassProc;
    }

    public event Action? RegistrationChanged;

    public bool IsRegistered => _isRegistered &&
        (!_usesReservedHook || _reservedHotkeyHook.IsActive);
    public bool UsesReservedHook => _usesReservedHook && IsRegistered;
    public long ReceivedCount => Interlocked.Read(ref _receivedSequence);
    public long InvocationCount => Interlocked.Read(ref _invocationSequence);
    public long DispatchFailureCount => Interlocked.Read(ref _dispatchFailureSequence);
    public uint ReservedHookThreadId => _reservedHotkeyHook.ThreadId;
    public int ReservedHookLastErrorCode => _reservedHotkeyHook.LastErrorCode;
    public long ReservedHookTriggerCount => _reservedHotkeyHook.TriggerCount;
    public long ReservedHookPostFailureCount => _reservedHotkeyHook.PostFailureCount;
    public long ReservedHookInputFailureCount => _reservedHotkeyHook.InputFailureCount;
    public string? LastError { get; private set; }

    public GlobalHotkeyActivation CurrentActivation => NormalizeActivation(
        _settingsService.Settings.GlobalHotkeyActivationKind,
        _settingsService.Settings.GlobalHotkeyModifiers,
        _settingsService.Settings.GlobalHotkeyKey);

    public GlobalHotkeyGesture CurrentGesture => CurrentActivation.Gesture;

    public string CurrentGestureText => FormatActivation(CurrentActivation, _localizationService);

    public void Attach(IntPtr windowHandle)
    {
        App.Log($"[GlobalHotkey] Attach called hwnd=0x{windowHandle.ToInt64():X}");
        if (windowHandle == IntPtr.Zero)
        {
            App.Log("[GlobalHotkey] Attach skipped: windowHandle is Zero");
            return;
        }

        Detach();
        _windowHandle = windowHandle;
        _isSubclassInstalled = Win32Helper.SetWindowSubclass(_windowHandle, _subclassProc, SubclassId, UIntPtr.Zero);
        int subclassError = _isSubclassInstalled ? 0 : Marshal.GetLastWin32Error();
        App.Log($"[GlobalHotkey] Subclass installed={_isSubclassInstalled} error={subclassError}");
        if (!_isSubclassInstalled)
        {
            LastError = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
            NotifyRegistrationChanged();
            return;
        }

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
    }

    public void RefreshRegistration()
    {
        Unregister();
        LastError = null;

        App.Log($"[GlobalHotkey] RefreshRegistration hwnd=0x{_windowHandle.ToInt64():X} enabled={_settingsService.Settings.GlobalHotkeyEnabled} gesture={CurrentGestureText}");

        if (_windowHandle == IntPtr.Zero || !_settingsService.Settings.GlobalHotkeyEnabled)
        {
            App.Log("[GlobalHotkey] RefreshRegistration skipped: handle=0 or disabled");
            NotifyRegistrationChanged();
            return;
        }

        GlobalHotkeyActivation activation = CurrentActivation;
        if (!IsValidActivation(activation))
        {
            App.Log("[GlobalHotkey] RefreshRegistration skipped: invalid activation");
            LastError = _localizationService.T("Settings.GlobalHotkey.Status.Invalid");
            NotifyRegistrationChanged();
            return;
        }

        if (!_isSubclassInstalled)
        {
            App.Log("[GlobalHotkey] RefreshRegistration skipped: window subclass unavailable");
            LastError = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
            NotifyRegistrationChanged();
            return;
        }

        if (TryGetReservedHookMode(activation, out ReservedHotkeyMode reservedMode))
        {
            if (IsReservedHookDisabledByEnvironment())
            {
                App.Log("[GlobalHotkey] Reserved hotkey hook disabled by environment");
                LastError = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
                NotifyRegistrationChanged();
                return;
            }

            bool hookStarted;
            int hookError;
            try
            {
                hookStarted = _reservedHotkeyHook.TryStart(
                    _windowHandle,
                    WmReservedHotkey,
                    reservedMode,
                    out hookError);
            }
            catch (Exception ex)
            {
                hookStarted = false;
                hookError = Marshal.GetHRForException(ex);
                App.Log($"[GlobalHotkey] Reserved hook startup threw: {ex}");
            }

            if (hookStarted)
            {
                _isRegistered = true;
                _usesReservedHook = true;
                App.Log(
                    $"[GlobalHotkey] Registered reserved gesture={CurrentGestureText} " +
                    $"mode=hook hwnd=0x{_windowHandle.ToInt64():X}");
                NotifyRegistrationChanged();
                return;
            }

            App.Log(
                $"[GlobalHotkey] Reserved hook registration failed gesture={CurrentGestureText} " +
                $"error={hookError}");
            LastError = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
            NotifyRegistrationChanged();
            return;
        }

        if (Register(_windowHandle, MainHotkeyId, activation.Gesture, out int registerError))
        {
            _isRegistered = true;
            App.Log($"[GlobalHotkey] Registered gesture={CurrentGestureText} hwnd=0x{_windowHandle.ToInt64():X}");
            NotifyRegistrationChanged();
            return;
        }

        App.Log($"[GlobalHotkey] RegisterHotKey failed gesture={CurrentGestureText} error={registerError}");
        LastError = _localizationService.T("Settings.GlobalHotkey.Status.Conflict");
        NotifyRegistrationChanged();
    }

    public bool TryApplyGesture(GlobalHotkeyGesture gesture, out string? error)
    {
        return TryApplyActivation(GlobalHotkeyActivation.FromChord(gesture), out error);
    }

    public bool TryApplyActivation(GlobalHotkeyActivation activation, out string? error)
    {
        error = null;
        activation = NormalizeActivation(
            activation.Kind,
            (int)activation.Gesture.Modifiers,
            activation.Gesture.VirtualKey);
        if (!IsValidActivation(activation))
        {
            error = _localizationService.T("Settings.GlobalHotkey.Status.Invalid");
            return false;
        }

        if (activation.Kind == HotkeyActivationKind.Chord &&
            IsReservedSystemGesture(activation.Gesture) &&
            IsGestureOwnedBySearchHotkey(activation.Gesture))
        {
            error = _localizationService.T("Settings.GlobalHotkey.Status.SearchHotkeyConflict");
            return false;
        }

        var settings = _settingsService.Settings;
        HotkeyActivationKind previousKind = settings.GlobalHotkeyActivationKind;
        int previousModifiers = settings.GlobalHotkeyModifiers;
        int previousVirtualKey = settings.GlobalHotkeyKey;
        GlobalHotkeyActivation previousActivation = NormalizeActivation(
            previousKind,
            previousModifiers,
            previousVirtualKey);
        bool isCurrentActivation = activation.Equals(previousActivation);
        bool shouldBeActive = _windowHandle != IntPtr.Zero && settings.GlobalHotkeyEnabled;

        if (isCurrentActivation)
        {
            if (shouldBeActive && !IsRegistered)
            {
                RefreshRegistration();
                if (!IsRegistered)
                {
                    error = LastError ?? _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
                    return false;
                }
            }

            return true;
        }

        settings.GlobalHotkeyActivationKind = activation.Kind;
        settings.GlobalHotkeyModifiers = (int)activation.Gesture.Modifiers;
        settings.GlobalHotkeyKey = activation.Gesture.VirtualKey;

        if (!shouldBeActive)
        {
            _settingsService.SaveDebounced();
            return true;
        }

        // The real registration is the commit point. A probe can become stale
        // before the subsequent RegisterHotKey call, so register the requested
        // gesture first and roll back both settings and registration on failure.
        RefreshRegistration();
        if (IsRegistered)
        {
            _settingsService.SaveDebounced();
            return true;
        }

        string registrationError = LastError ??
            _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
        settings.GlobalHotkeyActivationKind = previousKind;
        settings.GlobalHotkeyModifiers = previousModifiers;
        settings.GlobalHotkeyKey = previousVirtualKey;
        RefreshRegistration();
        if (!IsRegistered)
        {
            App.Log(
                $"[GlobalHotkey] Rollback registration failed previousActivation=" +
                $"{FormatActivation(previousActivation, _localizationService)}");
        }

        error = registrationError;
        return false;
    }

    public void SetEnabled(bool enabled)
    {
        if (_settingsService.Settings.GlobalHotkeyEnabled == enabled)
        {
            return;
        }

        _settingsService.Settings.GlobalHotkeyEnabled = enabled;
        _settingsService.SaveDebounced();
        RefreshRegistration();
    }

    public bool ResetToDefault(out string? error)
    {
        var activation = new GlobalHotkeyActivation(
            SettingsService.DefaultGlobalHotkeyActivationKind,
            new GlobalHotkeyGesture(
                (HotkeyModifierKeys)SettingsService.DefaultGlobalHotkeyModifiers,
                SettingsService.DefaultGlobalHotkeyKey));
        return TryApplyActivation(activation, out error);
    }

    public void Dispose()
    {
        Detach();
        _reservedHotkeyHook.Dispose();
    }

    public static GlobalHotkeyGesture NormalizeGesture(int modifiers, int virtualKey)
    {
        var normalizedModifiers = (HotkeyModifierKeys)modifiers &
            (HotkeyModifierKeys.Alt |
             HotkeyModifierKeys.Control |
             HotkeyModifierKeys.Shift |
             HotkeyModifierKeys.Windows);
        return new GlobalHotkeyGesture(normalizedModifiers, virtualKey);
    }

    public static GlobalHotkeyActivation NormalizeActivation(
        HotkeyActivationKind kind,
        int modifiers,
        int virtualKey)
    {
        HotkeyActivationKind normalizedKind = Enum.IsDefined(kind)
            ? kind
            : HotkeyActivationKind.Chord;
        return new GlobalHotkeyActivation(
            normalizedKind,
            NormalizeGesture(modifiers, virtualKey));
    }

    public static bool IsReservedSystemGesture(GlobalHotkeyGesture gesture)
    {
        return gesture.VirtualKey == (int)VirtualKey.Space &&
               gesture.Modifiers is
                   HotkeyModifierKeys.Windows or
                   HotkeyModifierKeys.Alt;
    }

    internal static bool IsGestureOwnedBySearchHotkey(GlobalHotkeyGesture gesture)
    {
        return App.Current?.SearchHotkeyService?.CurrentGesture.Equals(gesture) == true;
    }

    public static bool IsValidActivation(GlobalHotkeyActivation activation)
    {
        if (!Enum.IsDefined(activation.Kind))
        {
            return false;
        }

        return activation.Kind != HotkeyActivationKind.Chord ||
               IsValidGesture(activation.Gesture);
    }

    public static bool IsValidGesture(GlobalHotkeyGesture gesture)
    {
        if (gesture.VirtualKey <= 0)
        {
            return false;
        }

        if (gesture.Modifiers == HotkeyModifierKeys.None)
        {
            return IsFunctionKey(gesture.VirtualKey);
        }

        return IsAllowedPrimaryKey(gesture.VirtualKey);
    }

    public static bool IsRiskyGesture(GlobalHotkeyGesture gesture)
    {
        return IsReservedSystemGesture(gesture) ||
               gesture.Modifiers == HotkeyModifierKeys.None ||
               gesture.VirtualKey is
                   (int)VirtualKey.F1 or
                   (int)VirtualKey.F2 or
                   (int)VirtualKey.F5 or
                   (int)VirtualKey.F11 or
                   (int)VirtualKey.F12;
    }

    public static bool IsRiskyActivation(GlobalHotkeyActivation activation)
    {
        return activation.Kind == HotkeyActivationKind.WindowsTap ||
               (activation.Kind == HotkeyActivationKind.Chord &&
                IsRiskyGesture(activation.Gesture));
    }

    public static string FormatActivation(
        GlobalHotkeyActivation activation,
        LocalizationService localization)
    {
        return activation.Kind switch
        {
            HotkeyActivationKind.DoubleControl =>
                localization.T("Settings.GlobalHotkey.Preset.DoubleControl"),
            HotkeyActivationKind.WindowsTap =>
                localization.T("Settings.GlobalHotkey.Preset.WindowsTap"),
            _ => FormatGesture(activation.Gesture, localization)
        };
    }

    public static string FormatGesture(GlobalHotkeyGesture gesture, LocalizationService localization)
    {
        if (gesture.VirtualKey <= 0)
        {
            return localization.T("Settings.GlobalHotkey.NotSet");
        }

        if (gesture.Equals(CopilotKeyGesture))
        {
            return localization.T("Settings.GlobalHotkey.Preset.CopilotKey");
        }

        var parts = new List<string>();
        if (gesture.Modifiers.HasFlag(HotkeyModifierKeys.Windows))
        {
            parts.Add("Win");
        }

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

        parts.Add(FormatVirtualKey(gesture.VirtualKey));
        return string.Join(" + ", parts);
    }

    private IntPtr WindowSubclassProc(
        IntPtr hWnd,
        uint message,
        UIntPtr wParam,
        IntPtr lParam,
        UIntPtr uIdSubclass,
        UIntPtr dwRefData)
    {
        if (message == WmHotkey && wParam == (UIntPtr)MainHotkeyId)
        {
            QueueHotkeyInvocation("registered", releaseStandardModifiers: true);
            return IntPtr.Zero;
        }

        if (message == WmReservedHotkey && _usesReservedHook)
        {
            QueueHotkeyInvocation("reserved-hook", releaseStandardModifiers: false);
            return IntPtr.Zero;
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void QueueHotkeyInvocation(string source, bool releaseStandardModifiers)
    {
        long receivedId = Interlocked.Increment(ref _receivedSequence);
        App.LogVerbose(
            $"[GlobalHotkey] Received id={receivedId} source={source} " +
            $"gesture={CurrentGestureText}");

        if (releaseStandardModifiers)
        {
            // Clear Ctrl/Alt/Shift states that can become stuck in RDP. The
            // reserved Win+Space path owns its key-up state and never uses this.
            Win32Helper.ReleaseAllModifiers();
        }

        if (App.UiDispatcherQueue.TryEnqueue(() =>
            {
                _ = InvokeHotkeyAsync(source);
            }))
        {
            return;
        }

        Interlocked.Increment(ref _dispatchFailureSequence);
        App.Log(
            $"[GlobalHotkey] UI dispatch rejected id={receivedId} source={source}");
    }

    private async Task InvokeHotkeyAsync(string source)
    {
        long invocationId = Interlocked.Increment(ref _invocationSequence);
        App.Log(
            $"[GlobalHotkey] Triggered id={invocationId} source={source} " +
            $"gesture={CurrentGestureText}");
        try
        {
            await _invokeAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[GlobalHotkey] Invocation failed id={invocationId}: {ex}");
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
                App.Log($"[GlobalHotkey] Reserved hook removal failed: {ex}");
            }
            App.Log($"[GlobalHotkey] Reserved hook removed gesture={CurrentGestureText}");
        }
        else if (_isRegistered && _windowHandle != IntPtr.Zero)
        {
            Win32Helper.UnregisterHotKey(_windowHandle, MainHotkeyId);
            App.Log($"[GlobalHotkey] Unregistered gesture={CurrentGestureText}");
        }

        _isRegistered = false;
        _usesReservedHook = false;
    }

    private static bool Register(
        IntPtr windowHandle,
        int id,
        GlobalHotkeyGesture gesture,
        out int errorCode)
    {
        bool registered = Win32Helper.RegisterHotKey(
            windowHandle,
            id,
            ToWin32Modifiers(gesture.Modifiers) | ModNoRepeat,
            (uint)gesture.VirtualKey);
        errorCode = registered ? 0 : Marshal.GetLastWin32Error();
        return registered;
    }

    private static bool TryGetReservedHookMode(
        GlobalHotkeyActivation activation,
        out ReservedHotkeyMode mode)
    {
        if (activation.Kind == HotkeyActivationKind.DoubleControl)
        {
            mode = ReservedHotkeyMode.DoubleControl;
            return true;
        }

        if (activation.Kind == HotkeyActivationKind.WindowsTap)
        {
            mode = ReservedHotkeyMode.WindowsTap;
            return true;
        }

        if (activation.Kind == HotkeyActivationKind.Chord &&
            activation.Gesture.VirtualKey == (int)VirtualKey.Space)
        {
            if (activation.Gesture.Modifiers == HotkeyModifierKeys.Windows)
            {
                mode = ReservedHotkeyMode.WinSpace;
                return true;
            }

            if (activation.Gesture.Modifiers == HotkeyModifierKeys.Alt)
            {
                mode = ReservedHotkeyMode.AltSpace;
                return true;
            }
        }

        mode = default;
        return false;
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

        if (modifiers.HasFlag(HotkeyModifierKeys.Windows))
        {
            value |= ModWin;
        }

        return value;
    }

    internal static bool AreCurrentModifiersPressed(HotkeyModifierKeys modifiers)
    {
        bool ctrl = Win32Helper.IsKeyDown((int)VirtualKey.Control) ||
                    Win32Helper.IsKeyDown((int)VirtualKey.LeftControl) ||
                    Win32Helper.IsKeyDown((int)VirtualKey.RightControl);
        bool alt = Win32Helper.IsKeyDown((int)VirtualKey.Menu) ||
                   Win32Helper.IsKeyDown((int)VirtualKey.LeftMenu) ||
                   Win32Helper.IsKeyDown((int)VirtualKey.RightMenu);
        bool shift = Win32Helper.IsKeyDown((int)VirtualKey.Shift) ||
                     Win32Helper.IsKeyDown((int)VirtualKey.LeftShift) ||
                     Win32Helper.IsKeyDown((int)VirtualKey.RightShift);
        bool windows = Win32Helper.IsKeyDown((int)VirtualKey.LeftWindows) ||
                       Win32Helper.IsKeyDown((int)VirtualKey.RightWindows);

        return ctrl == modifiers.HasFlag(HotkeyModifierKeys.Control) &&
               alt == modifiers.HasFlag(HotkeyModifierKeys.Alt) &&
               shift == modifiers.HasFlag(HotkeyModifierKeys.Shift) &&
               windows == modifiers.HasFlag(HotkeyModifierKeys.Windows);
    }

    private static bool IsReservedHookDisabledByEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable("DESKBOX_DISABLE_RESERVED_HOTKEY_HOOK");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedPrimaryKey(int virtualKey)
    {
        return IsLetterKey(virtualKey) ||
               IsDigitKey(virtualKey) ||
               IsFunctionKey(virtualKey) ||
               virtualKey is
                   (int)VirtualKey.Space or
                   (int)VirtualKey.Tab or
                   (int)VirtualKey.Insert or
                   (int)VirtualKey.Delete or
                   (int)VirtualKey.Home or
                   (int)VirtualKey.End or
                   (int)VirtualKey.PageUp or
                   (int)VirtualKey.PageDown;
    }

    private static bool IsLetterKey(int virtualKey)
    {
        return virtualKey is >= (int)VirtualKey.A and <= (int)VirtualKey.Z;
    }

    private static bool IsDigitKey(int virtualKey)
    {
        return virtualKey is >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number9 ||
               virtualKey is >= (int)VirtualKey.NumberPad0 and <= (int)VirtualKey.NumberPad9;
    }

    private static bool IsFunctionKey(int virtualKey)
    {
        return virtualKey is >= (int)VirtualKey.F1 and <= (int)VirtualKey.F24;
    }

    private static string FormatVirtualKey(int virtualKey)
    {
        if (virtualKey is >= (int)VirtualKey.A and <= (int)VirtualKey.Z)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= (int)VirtualKey.Number0 and <= (int)VirtualKey.Number9)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= (int)VirtualKey.NumberPad0 and <= (int)VirtualKey.NumberPad9)
        {
            return $"Num {virtualKey - (int)VirtualKey.NumberPad0}";
        }

        if (virtualKey is >= (int)VirtualKey.F1 and <= (int)VirtualKey.F24)
        {
            return $"F{virtualKey - (int)VirtualKey.F1 + 1}";
        }

        return ((VirtualKey)virtualKey) switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Tab => "Tab",
            VirtualKey.Insert => "Insert",
            VirtualKey.Delete => "Delete",
            VirtualKey.Home => "Home",
            VirtualKey.End => "End",
            VirtualKey.PageUp => "Page Up",
            VirtualKey.PageDown => "Page Down",
            _ => ((VirtualKey)virtualKey).ToString()
        };
    }

    private void NotifyRegistrationChanged()
    {
        RegistrationChanged?.Invoke();
    }
}
