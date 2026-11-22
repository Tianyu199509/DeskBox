using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Contracts;
using DeskBox.Platform;
using Windows.System;

namespace DeskBox.Services;

/// <summary>
/// Manages the global hotkey for invoking the search popup. Ordinary gestures
/// use the standard Win32 RegisterHotKey + WM_HOTKEY mechanism; system-reserved
/// and modifier-tap activations ride the reserved low-level hook, matching how
/// the main hotkey handles reserved gestures.
/// </summary>
public sealed class SearchHotkeyService : IDisposable, IHookHealthProbeTarget, IHookRegistrationMaintenanceTarget, ISearchHotkeyController
{
    private const int SearchHotkeyId = 0x4444;
    private const uint WmReservedSearchHotkey = 0x8444;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private static readonly UIntPtr SubclassId = new(0x4444);

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
        _settingsService.Settings.Search.SearchHotkeyActivationKind,
        _settingsService.Settings.Search.SearchHotkeyModifiers,
        _settingsService.Settings.Search.SearchHotkeyKey);

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

    /// <summary>
    /// The registration round-trip. RegisterHotKey has no query API, so the
    /// unregister+register pair doubles as the self-check; the quiet mode is
    /// used by the watchdog heartbeat so a healthy periodic refresh does not
    /// flood the log.
    /// </summary>
    private void RefreshRegistrationCore(bool quiet, bool allowWhileSuspended = false)
    {
        Unregister();
        LastError = null;

        if (_suspendedForRecording && !allowWhileSuspended)
        {
            return;
        }

        if (_windowHandle == IntPtr.Zero || !_settingsService.Settings.Search.SearchHotkeyEnabled)
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
                LastError = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
                App.Log("[SearchHotkey] Failed to install tray window subclass");
                return;
            }
        }

        GlobalHotkeyActivation activation = CurrentActivation;
        if (!GlobalHotkeyService.IsValidActivation(activation))
        {
            LastError = _localizationService.T("Settings.GlobalHotkey.Status.Invalid");
            return;
        }

        if (IsActivationOwnedByMainHotkey(activation))
        {
            LastError = _localizationService.T("Settings.Search.Hotkey.Status.GlobalHotkeyConflict");
            App.Log($"[SearchHotkey] Activation {CurrentGestureText} is owned by the main hotkey; staying unregistered");
            return;
        }

        if (TryGetReservedSearchHookMode(activation, out ReservedHotkeyMode reservedMode))
        {
            ApplyReservedHookMode(reservedMode, quiet);
            return;
        }

        if (Register(_windowHandle, activation.Gesture))
        {
            _isRegistered = true;
            if (!quiet)
            {
                App.Log($"[SearchHotkey] Registered gesture={CurrentGestureText}");
            }

            return;
        }

        LastError = _localizationService.T("Settings.GlobalHotkey.Status.Conflict");
        App.Log($"[SearchHotkey] Failed to register gesture={CurrentGestureText} (may be in use by another app)");
    }

    private static bool TryGetReservedSearchHookMode(
        GlobalHotkeyActivation activation,
        out ReservedHotkeyMode mode)
    {
        if (activation.Kind == HotkeyActivationKind.Chord &&
            activation.Gesture.Equals(AltSpaceGesture))
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
                reservedMode,
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
            if (!quiet)
            {
                App.Log($"[SearchHotkey] Registered reserved gesture={CurrentGestureText} mode=hook");
            }

            return;
        }

        LastError = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
        App.Log($"[SearchHotkey] Reserved hook registration failed error={hookError}");
    }

    internal bool IsGestureOwnedByMainHotkey()
    {
        return IsGestureOwnedByMainHotkey(AltSpaceGesture);
    }

    public bool IsGestureOwnedByMainHotkey(GlobalHotkeyGesture gesture)
    {
        return IsActivationOwnedByMainHotkey(GlobalHotkeyActivation.FromChord(gesture));
    }

    public bool IsActivationOwnedByMainHotkey(GlobalHotkeyActivation activation)
    {
        if (!_settingsService.Settings.Core.GlobalHotkeyEnabled)
        {
            return false;
        }

        GlobalHotkeyActivation mainActivation = App.Current?.GlobalHotkeyService?.CurrentActivation
            ?? GlobalHotkeyService.NormalizeActivation(
                _settingsService.Settings.Core.GlobalHotkeyActivationKind,
                _settingsService.Settings.Core.GlobalHotkeyModifiers,
                _settingsService.Settings.Core.GlobalHotkeyKey);
        return GlobalHotkeyService.DoActivationsConflict(activation, mainActivation);
    }

    public bool IsActivationOwnedByClipboardHotkey(GlobalHotkeyActivation activation)
    {
        var settings = _settingsService.Settings;
        if (!FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Clipboard) ||
            !settings.Clipboard.ClipboardHotkeyEnabled)
        {
            return false;
        }

        GlobalHotkeyActivation clipboardActivation = GlobalHotkeyService.NormalizeActivation(
            settings.Clipboard.ClipboardHotkeyActivationKind,
            settings.Clipboard.ClipboardHotkeyModifiers,
            settings.Clipboard.ClipboardHotkeyKey);
        return GlobalHotkeyService.DoActivationsConflict(activation, clipboardActivation);
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
        return TryApplyActivation(GlobalHotkeyActivation.FromChord(gesture), out error);
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

        if (IsActivationOwnedByMainHotkey(activation))
        {
            error = _localizationService.T("Settings.Search.Hotkey.Status.GlobalHotkeyConflict");
            return false;
        }

        if (IsActivationOwnedByClipboardHotkey(activation))
        {
            error = _localizationService.T("Settings.Clipboard.Hotkey.Status.ClipboardHotkeyConflict");
            return false;
        }

        var settings = _settingsService.Settings;
        HotkeyActivationKind previousKind = settings.Search.SearchHotkeyActivationKind;
        int previousModifiers = settings.Search.SearchHotkeyModifiers;
        int previousVirtualKey = settings.Search.SearchHotkeyKey;
        GlobalHotkeyActivation previousActivation = GlobalHotkeyService.NormalizeActivation(
            previousKind,
            previousModifiers,
            previousVirtualKey);
        bool isCurrentActivation = activation.Equals(previousActivation);
        bool shouldBeActive = _windowHandle != IntPtr.Zero && settings.Search.SearchHotkeyEnabled;

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

        settings.Search.SearchHotkeyActivationKind = activation.Kind;
        settings.Search.SearchHotkeyModifiers = (int)activation.Gesture.Modifiers;
        settings.Search.SearchHotkeyKey = activation.Gesture.VirtualKey;

        if (!shouldBeActive)
        {
            _settingsService.SaveDebounced();
            return true;
        }

        // As with the main hotkey, the real RegisterHotKey call is the commit
        // point. Restore both settings and registration if the requested
        // gesture is already owned by another process or hotkey id.
        RefreshRegistrationCore(quiet: false, allowWhileSuspended: true);
        if (IsRegistered)
        {
            _settingsService.SaveDebounced();
            RegistrationChanged?.Invoke();
            return true;
        }

        string registrationError = LastError ??
            _localizationService.T("Settings.GlobalHotkey.Status.Conflict");
        settings.Search.SearchHotkeyActivationKind = previousKind;
        settings.SearchHotkeyModifiers = previousModifiers;
        settings.SearchHotkeyKey = previousVirtualKey;
        RefreshRegistration();
        if (!_suspendedForRecording && !IsRegistered)
        {
            App.Log(
                $"[SearchHotkey] Rollback registration failed previousGesture=" +
                $"{FormatGesture(previousActivation.Gesture)}");
        }

        RegistrationChanged?.Invoke();
        error = registrationError;
        return false;
    }

    public void SetEnabled(bool enabled)
    {
        if (_settingsService.Settings.Search.SearchHotkeyEnabled == enabled)
        {
            return;
        }

        _settingsService.Settings.Search.SearchHotkeyEnabled = enabled;
        _settingsService.SaveDebounced();
        RefreshRegistration();
        RegistrationChanged?.Invoke();
    }

    /// <summary>
    /// Drops the current registration (including the reserved hook) while the
    /// recorder dialog captures input, so the live hotkey cannot fire or eat
    /// the keys being recorded. Settings are untouched;
    /// <see cref="ResumeAfterRecording"/> restores registration from them.
    /// </summary>
    public void SuspendForRecording()
    {
        _suspendedForRecording = true;
        Unregister();
    }

    public void ResumeAfterRecording()
    {
        _suspendedForRecording = false;
        RefreshRegistration();
    }

    string IHookHealthProbeTarget.ProbeName => "search-hotkey";

    bool IHookHealthProbeTarget.HookProbeWanted => _usesReservedHook && _isRegistered;

    bool IHookHealthProbeTarget.HookConfirmedDead => !_reservedHotkeyHook.IsActive;

    long IHookHealthProbeTarget.LastHookCallbackTicks => _reservedHotkeyHook.LastCallbackTicks;

    Task<bool> IHookHealthProbeTarget.ProbeHookAliveAsync(int echoWaitMilliseconds) =>
        _reservedHotkeyHook.ProbeAliveAsync(echoWaitMilliseconds);

    void IHookHealthProbeTarget.RecoverHook() => RefreshRegistration();

    string IHookRegistrationMaintenanceTarget.MaintenanceName => "search-hotkey-registration";

    bool IHookRegistrationMaintenanceTarget.RegistrationMaintenanceWanted =>
        !_suspendedForRecording &&
        _windowHandle != IntPtr.Zero &&
        _settingsService.Settings.Search.SearchHotkeyEnabled &&
        UsesRegisterHotKeyChord(CurrentActivation);

    bool IHookRegistrationMaintenanceTarget.RegistrationHealthy => IsRegistered;

    void IHookRegistrationMaintenanceTarget.RunRegistrationHeartbeat()
    {
        // Suspend-aware for the same reason as the main hotkey: the recorder
        // dialog must be able to capture keys without the live hotkey firing.
        // Both this method and suspend/resume run on the UI dispatcher.
        if (_suspendedForRecording)
        {
            return;
        }

        bool wasRegistered = IsRegistered;
        RefreshRegistrationCore(quiet: true, allowWhileSuspended: false);
        if (IsRegistered != wasRegistered)
        {
            // A silent unregister path (handle lost while disabled) can leave
            // IsRegistered false without an error line; the flip itself is the
            // signal worth the default log level.
            App.Log(
                $"[SearchHotkey] Heartbeat refreshed gesture={CurrentGestureText} " +
                $"registered={IsRegistered}");
            RegistrationChanged?.Invoke();
        }
        else
        {
            App.LogVerbose(
                $"[SearchHotkey] Heartbeat refreshed gesture={CurrentGestureText} " +
                $"registered={IsRegistered}");
        }
    }

    /// <summary>
    /// True when the activation rides the plain RegisterHotKey path (the one the
    /// watchdog heartbeat keeps alive); reserved system chords and modifier-tap
    /// modes ride the low-level hook that the liveness probe covers instead.
    /// </summary>
    internal static bool UsesRegisterHotKeyChord(GlobalHotkeyActivation activation)
    {
        return GlobalHotkeyService.UsesRegisterHotKeyChord(activation);
    }

    internal static bool UsesRegisterHotKeyChord(GlobalHotkeyGesture gesture)
    {
        return GlobalHotkeyService.UsesRegisterHotKeyChord(
            GlobalHotkeyActivation.FromChord(gesture));
    }

    private IntPtr WindowSubclassProc(IntPtr hWnd, uint message, UIntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (message == WmReservedSearchHotkey && _usesReservedHook)
        {
            QueueHotkeyInvocation(releaseStandardModifiers: false);
            return IntPtr.Zero;
        }

        if (message == GlobalHotkeyService.WmHotkey &&
            wParam.ToUInt32() == SearchHotkeyId)
        {
            QueueHotkeyInvocation(releaseStandardModifiers: true);
            return IntPtr.Zero;
        }

        return Win32Helper.DefSubclassProc(hWnd, message, wParam, lParam);
    }

    private void QueueHotkeyInvocation(bool releaseStandardModifiers)
    {
        long receivedId = Interlocked.Increment(ref _receivedSequence);
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
            // Clear Ctrl/Alt/Shift states that can become stuck in RDP.
            // Deliberately synchronous (callback-isolation exemption, input
            // family, not window family — see GlobalHotkeyService for the full
            // rationale): deferring to "before the next input" would release a
            // combination the user is actively holding.
            Win32Helper.ReleaseAllModifiers();
        }

        if (App.UiDispatcherQueue.TryEnqueue(() =>
        {
            _ = InvokeAsync();
        }))
        {
            return;
        }

        Interlocked.Increment(ref _dispatchFailureSequence);
        App.Log($"[SearchHotkey] UI dispatch rejected id={receivedId}");
    }

    private async Task InvokeAsync()
    {
        if (_isInvoking)
        {
            // Deep review R15 (F6 audit): dropping a concurrent press broke
            // the toggle semantic — a quick double-tap (show then hide) had
            // its second half silently swallowed while the first invoke was
            // still in flight across its await yield. Coalesce instead: one
            // pending re-run max, drained when the current invoke completes.
            _pendingInvoke = true;
            return;
        }

        _isInvoking = true;
        long invocationId = Interlocked.Increment(ref _invocationSequence);
        App.Log($"[SearchHotkey] Triggered id={invocationId}");
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
            App.Log($"[SearchHotkey] Invocation failed: {ex}");
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

        if (modifiers.HasFlag(HotkeyModifierKeys.Windows))
        {
            value |= ModWin;
        }

        return value;
    }

    private static string FormatGesture(GlobalHotkeyGesture gesture)
    {
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

        parts.Add($"VK:{gesture.VirtualKey:X2}");
        return string.Join("+", parts);
    }

    public void Dispose()
    {
        Detach();
        _reservedHotkeyHook.Dispose();
    }
}
