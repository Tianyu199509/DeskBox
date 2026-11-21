using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using Windows.System;

namespace DeskBox.Services;

/// <summary>
/// Manages the global hotkey for summoning the cursor/caret-following Clipboard
/// Quick-Paste popup. Ordinary chords use Win32 <c>RegisterHotKey</c>; system-reserved
/// and modifier double-tap activations ride <see cref="ReservedHotkeyHookService"/>.
/// </summary>
public sealed class ClipboardHotkeyService : IDisposable, IHookHealthProbeTarget, IHookRegistrationMaintenanceTarget
{
    private const int ClipboardHotkeyId = 0x5656;
    private const uint WmReservedClipboardHotkey = 0x8566;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private static readonly UIntPtr SubclassId = new(0x5656);

    private readonly SettingsService _settingsService;
    private readonly LocalizationService _localizationService;
    private readonly Func<Task> _invokeAsync;
    private readonly Win32Helper.SubclassProc _subclassProc;
    private readonly ReservedHotkeyHookService _reservedHotkeyHook = new();
    private readonly DoubleTapChordGate _doubleTapChordGate = new();
    private IntPtr _windowHandle;
    private bool _isSubclassInstalled;
    private bool _isRegistered;
    private bool _usesReservedHook;
    private bool _isInvoking;
    private volatile bool _pendingInvoke;
    private bool _suspendedForRecording;
    private long _receivedSequence;
    private long _invocationSequence;
    private long _dispatchFailureSequence;

    public static GlobalHotkeyGesture DefaultGesture { get; } = new(
        HotkeyModifierKeys.Alt,
        ClipboardSettingsSlice.DefaultHotkeyKey);

    public ClipboardHotkeyService(
        SettingsService settingsService,
        LocalizationService localizationService,
        Func<Task> invokeAsync)
    {
        _settingsService = settingsService;
        _localizationService = localizationService;
        _invokeAsync = invokeAsync;
        _subclassProc = CallbackDepth.WrapSubclassProc(WindowSubclassProc);
    }

    public event Action? RegistrationChanged;

    public bool IsRegistered => _isRegistered &&
        (!_usesReservedHook || _reservedHotkeyHook.IsActive);

    public bool UsesReservedHook => _usesReservedHook && IsRegistered;

    public bool IsSuspendedForRecording => _suspendedForRecording;

    public string? LastError { get; private set; }

    public long ReceivedCount => Interlocked.Read(ref _receivedSequence);

    public long InvocationCount => Interlocked.Read(ref _invocationSequence);

    public long DispatchFailureCount => Interlocked.Read(ref _dispatchFailureSequence);

    public GlobalHotkeyActivation CurrentActivation => GlobalHotkeyService.NormalizeActivation(
        _settingsService.Settings.Clipboard.ClipboardHotkeyActivationKind,
        _settingsService.Settings.Clipboard.ClipboardHotkeyModifiers,
        _settingsService.Settings.Clipboard.ClipboardHotkeyKey);

    public GlobalHotkeyGesture CurrentGesture => CurrentActivation.Gesture;

    public string CurrentGestureText => GlobalHotkeyService.FormatActivation(CurrentActivation, _localizationService);

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
        RefreshRegistrationCore(quiet: false, allowWhileSuspended: false);
    }

    private void RefreshRegistrationCore(bool quiet, bool allowWhileSuspended = false)
    {
        bool wasRegistered = IsRegistered;
        Unregister();
        LastError = null;

        if (_suspendedForRecording && !allowWhileSuspended)
        {
            return;
        }

        if (_windowHandle == IntPtr.Zero ||
            !FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Clipboard) ||
            !_settingsService.Settings.Clipboard.ClipboardHotkeyEnabled)
        {
            RemoveSubclass();
            if (!quiet && wasRegistered)
            {
                RegistrationChanged?.Invoke();
            }

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
                LastError = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
                App.Log("[ClipboardHotkey] Failed to install tray window subclass");
                return;
            }
        }

        GlobalHotkeyActivation activation = CurrentActivation;
        if (!GlobalHotkeyService.IsValidActivation(activation))
        {
            LastError = _localizationService.T("Settings.GlobalHotkey.Status.Invalid");
            return;
        }

        if (TryGetDeskBoxHotkeyConflict(_settingsService.Settings, activation, _localizationService, out string? conflictMsg))
        {
            LastError = conflictMsg;
            App.Log($"[ClipboardHotkey] Activation {CurrentGestureText} conflicts with another DeskBox hotkey");
            if (!quiet && wasRegistered)
            {
                RegistrationChanged?.Invoke();
            }

            return;
        }

        if (TryGetReservedHookMode(activation, out ReservedHotkeyMode reservedMode))
        {
            ApplyReservedHookMode(reservedMode, quiet);
            if (!quiet && IsRegistered != wasRegistered)
            {
                RegistrationChanged?.Invoke();
            }

            return;
        }

        if (Register(_windowHandle, activation.Gesture))
        {
            _isRegistered = true;
            if (!quiet)
            {
                App.Log($"[ClipboardHotkey] Registered gesture={CurrentGestureText}");
                if (!wasRegistered)
                {
                    RegistrationChanged?.Invoke();
                }
            }

            return;
        }

        LastError = _localizationService.T("Settings.GlobalHotkey.Status.Conflict");
        App.Log($"[ClipboardHotkey] Failed to register gesture={CurrentGestureText} (in use by another app)");
        if (!quiet && wasRegistered)
        {
            RegistrationChanged?.Invoke();
        }
    }

    public static bool TryGetDeskBoxHotkeyConflict(
        AppSettings settings,
        GlobalHotkeyActivation activation,
        LocalizationService localization,
        out string? conflictMessage)
    {
        conflictMessage = null;
        if (settings.Core.GlobalHotkeyEnabled)
        {
            GlobalHotkeyActivation mainActivation = GlobalHotkeyService.NormalizeActivation(
                settings.Core.GlobalHotkeyActivationKind,
                settings.Core.GlobalHotkeyModifiers,
                settings.Core.GlobalHotkeyKey);
            if (GlobalHotkeyService.DoActivationsConflict(activation, mainActivation))
            {
                conflictMessage = localization.T("Settings.Clipboard.Hotkey.Status.GlobalHotkeyConflict");
                return true;
            }
        }

        if (settings.Search.SearchHotkeyEnabled)
        {
            GlobalHotkeyActivation searchActivation = GlobalHotkeyService.NormalizeActivation(
                settings.Search.SearchHotkeyActivationKind,
                settings.Search.SearchHotkeyModifiers,
                settings.Search.SearchHotkeyKey);
            if (GlobalHotkeyService.DoActivationsConflict(activation, searchActivation))
            {
                conflictMessage = localization.T("Settings.Clipboard.Hotkey.Status.SearchHotkeyConflict");
                return true;
            }
        }

        return false;
    }

    private static bool TryGetReservedHookMode(
        GlobalHotkeyActivation activation,
        out ReservedHotkeyMode mode)
    {
        if (activation.Kind == HotkeyActivationKind.Chord &&
            activation.Gesture.Equals(SearchHotkeyService.AltSpaceGesture))
        {
            mode = ReservedHotkeyMode.AltSpace;
            return true;
        }

        return GlobalHotkeyService.TryResolveReservedHookMode(activation, out mode);
    }

    private void ApplyReservedHookMode(ReservedHotkeyMode reservedMode, bool quiet)
    {
        if (IsReservedHookDisabledByEnvironment())
        {
            LastError = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
            return;
        }

        bool hookStarted;
        int hookError;
        try
        {
            hookStarted = _reservedHotkeyHook.TryStart(
                _windowHandle,
                WmReservedClipboardHotkey,
                reservedMode,
                out hookError);
        }
        catch (Exception ex)
        {
            hookStarted = false;
            hookError = Marshal.GetHRForException(ex);
            App.Log($"[ClipboardHotkey] Reserved hook startup threw: {ex}");
        }

        if (hookStarted)
        {
            _isRegistered = true;
            _usesReservedHook = true;
            if (!quiet)
            {
                App.Log($"[ClipboardHotkey] Registered reserved gesture={CurrentGestureText} mode=hook");
            }

            return;
        }

        LastError = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
        App.Log($"[ClipboardHotkey] Reserved hook registration failed error={hookError}");
    }

    public bool TryApplyActivation(GlobalHotkeyActivation activation, out string? error)
    {
        error = null;
        activation = GlobalHotkeyService.NormalizeActivation(
            activation.Kind,
            (int)activation.Gesture.Modifiers,
            activation.Gesture.VirtualKey);
        if (!GlobalHotkeyService.IsValidActivation(activation))
        {
            error = _localizationService.T("Settings.GlobalHotkey.Status.Invalid");
            return false;
        }

        AppSettings settings = _settingsService.Settings;
        if (TryGetDeskBoxHotkeyConflict(settings, activation, _localizationService, out string? conflict))
        {
            error = conflict;
            return false;
        }

        ClipboardSettingsSlice clipboard = settings.Clipboard;
        HotkeyActivationKind previousKind = clipboard.ClipboardHotkeyActivationKind;
        int previousModifiers = clipboard.ClipboardHotkeyModifiers;
        int previousVirtualKey = clipboard.ClipboardHotkeyKey;
        GlobalHotkeyActivation previousActivation = GlobalHotkeyService.NormalizeActivation(
            previousKind,
            previousModifiers,
            previousVirtualKey);
        bool isCurrentActivation = activation.Equals(previousActivation);
        bool shouldBeActive = _windowHandle != IntPtr.Zero &&
                              FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Clipboard) &&
                              clipboard.ClipboardHotkeyEnabled;

        if (isCurrentActivation)
        {
            if (shouldBeActive && !IsRegistered)
            {
                RefreshRegistrationCore(quiet: false, allowWhileSuspended: true);
                if (!IsRegistered)
                {
                    error = LastError ?? _localizationService.T("Settings.GlobalHotkey.Status.Conflict");
                    return false;
                }
            }

            return true;
        }

        clipboard.ClipboardHotkeyActivationKind = activation.Kind;
        clipboard.ClipboardHotkeyModifiers = (int)activation.Gesture.Modifiers;
        clipboard.ClipboardHotkeyKey = activation.Gesture.VirtualKey;

        if (!shouldBeActive)
        {
            _settingsService.SaveDebounced();
            return true;
        }

        RefreshRegistrationCore(quiet: false, allowWhileSuspended: true);
        if (IsRegistered)
        {
            _settingsService.SaveDebounced();
            RegistrationChanged?.Invoke();
            return true;
        }

        string registrationError = LastError ?? _localizationService.T("Settings.GlobalHotkey.Status.Conflict");
        clipboard.ClipboardHotkeyActivationKind = previousKind;
        clipboard.ClipboardHotkeyModifiers = previousModifiers;
        clipboard.ClipboardHotkeyKey = previousVirtualKey;
        RefreshRegistration();
        RegistrationChanged?.Invoke();
        error = registrationError;
        return false;
    }

    public void SetEnabled(bool enabled)
    {
        ClipboardSettingsSlice clipboard = _settingsService.Settings.Clipboard;
        if (clipboard.ClipboardHotkeyEnabled == enabled)
        {
            return;
        }

        clipboard.ClipboardHotkeyEnabled = enabled;
        _settingsService.SaveDebounced();
        RefreshRegistration();
        RegistrationChanged?.Invoke();
    }

    public void SuspendForRecording()
    {
        _suspendedForRecording = true;
        Unregister();
    }

    public void ResumeAfterRecording()
    {
        _suspendedForRecording = false;
        RefreshRegistration();
        RegistrationChanged?.Invoke();
    }

    string IHookHealthProbeTarget.ProbeName => "clipboard-hotkey";

    bool IHookHealthProbeTarget.HookProbeWanted => _usesReservedHook && _isRegistered;

    bool IHookHealthProbeTarget.HookConfirmedDead => !_reservedHotkeyHook.IsActive;

    long IHookHealthProbeTarget.LastHookCallbackTicks => _reservedHotkeyHook.LastCallbackTicks;

    Task<bool> IHookHealthProbeTarget.ProbeHookAliveAsync(int echoWaitMilliseconds) =>
        _reservedHotkeyHook.ProbeAliveAsync(echoWaitMilliseconds);

    void IHookHealthProbeTarget.RecoverHook() => RefreshRegistration();

    string IHookRegistrationMaintenanceTarget.MaintenanceName => "clipboard-hotkey-registration";

    bool IHookRegistrationMaintenanceTarget.RegistrationMaintenanceWanted =>
        !_suspendedForRecording &&
        _windowHandle != IntPtr.Zero &&
        FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Clipboard) &&
        _settingsService.Settings.Clipboard.ClipboardHotkeyEnabled &&
        GlobalHotkeyService.UsesRegisterHotKeyChord(CurrentActivation);

    bool IHookRegistrationMaintenanceTarget.RegistrationHealthy => IsRegistered;

    void IHookRegistrationMaintenanceTarget.RunRegistrationHeartbeat()
    {
        if (_suspendedForRecording)
        {
            return;
        }

        bool wasRegistered = IsRegistered;
        RefreshRegistrationCore(quiet: true, allowWhileSuspended: false);
        if (IsRegistered != wasRegistered)
        {
            App.Log($"[ClipboardHotkey] Heartbeat refreshed gesture={CurrentGestureText} registered={IsRegistered}");
            RegistrationChanged?.Invoke();
        }
    }

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (message == WmReservedClipboardHotkey && _usesReservedHook)
        {
            QueueHotkeyInvocation(releaseStandardModifiers: false);
            return IntPtr.Zero;
        }

        if (message == GlobalHotkeyService.WmHotkey &&
            wParam.ToUInt32() == ClipboardHotkeyId)
        {
            QueueHotkeyInvocation(releaseStandardModifiers: true);
            return IntPtr.Zero;
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void QueueHotkeyInvocation(bool releaseStandardModifiers)
    {
        Interlocked.Increment(ref _receivedSequence);
        GlobalHotkeyActivation currentActivation = CurrentActivation;

        if (currentActivation.Kind == HotkeyActivationKind.DoubleTap &&
            !currentActivation.IsModifierOnlyDoubleTap &&
            !currentActivation.IsStandaloneSpaceDoubleTap &&
            !_doubleTapChordGate.RecordPressAndShouldTrigger(currentActivation.Gesture, Environment.TickCount64))
        {
            return;
        }

        if (releaseStandardModifiers)
        {
            Win32Helper.ReleaseAllModifiers();
        }

        // Escape WindowSubclassProc's native CallbackDepth frame before ShowPopupAsync
        // calls SetWindowPos; RunOrDefer would run inline when already on the UI thread.
        UiDispatch.PostAfterCallback(() => _ = InvokeAsync());
    }

    private async Task InvokeAsync()
    {
        if (_isInvoking)
        {
            _pendingInvoke = true;
            return;
        }

        _isInvoking = true;
        Interlocked.Increment(ref _invocationSequence);
        try
        {
            do
            {
                _pendingInvoke = false;
                await _invokeAsync();
            }
            while (_pendingInvoke);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _dispatchFailureSequence);
            App.Log($"[ClipboardHotkey] Invocation failed: {ex}");
        }
        finally
        {
            _isInvoking = false;
        }
    }

    private void Unregister()
    {
        _doubleTapChordGate.Reset();

        if (_usesReservedHook || _reservedHotkeyHook.IsActive)
        {
            try
            {
                _reservedHotkeyHook.Stop();
            }
            catch (Exception ex)
            {
                App.Log($"[ClipboardHotkey] Reserved hook removal failed: {ex}");
            }
        }
        else if (_isRegistered && _windowHandle != IntPtr.Zero)
        {
            Win32Helper.UnregisterHotKey(_windowHandle, ClipboardHotkeyId);
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
            ClipboardHotkeyId,
            ToWin32Modifiers(gesture.Modifiers) | ModNoRepeat,
            (uint)gesture.VirtualKey);
    }

    private static uint ToWin32Modifiers(HotkeyModifierKeys modifiers)
    {
        uint value = 0;
        if (modifiers.HasFlag(HotkeyModifierKeys.Alt)) value |= ModAlt;
        if (modifiers.HasFlag(HotkeyModifierKeys.Control)) value |= ModControl;
        if (modifiers.HasFlag(HotkeyModifierKeys.Shift)) value |= ModShift;
        if (modifiers.HasFlag(HotkeyModifierKeys.Windows)) value |= ModWin;
        return value;
    }

    private static bool IsReservedHookDisabledByEnvironment()
    {
        string? value = Environment.GetEnvironmentVariable("DESKBOX_DISABLE_RESERVED_HOTKEY_HOOK");
        return string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Detach();
        _reservedHotkeyHook.Dispose();
    }
}
