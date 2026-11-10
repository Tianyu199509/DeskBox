using System.Runtime.InteropServices;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using Windows.System;

namespace DeskBox.Services;

public sealed class GlobalHotkeyService : IDisposable, IHookHealthProbeTarget, IHookRegistrationMaintenanceTarget
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

    public static GlobalHotkeyActivation DoubleSpaceActivation { get; } =
        GlobalHotkeyActivation.FromDoubleTapChord(new GlobalHotkeyGesture(
            HotkeyModifierKeys.None,
            (int)VirtualKey.Space));

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
    private bool _suspendedForRecording;
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
        _subclassProc = CallbackDepth.WrapSubclassProc(WindowSubclassProc);
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
    public long ReservedHookStartMenuLeakCount => _reservedHotkeyHook.StartMenuLeakCount;
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
        RefreshRegistrationCore(quiet: false, allowWhileSuspended: false);
    }

    /// <summary>
    /// The registration round-trip. RegisterHotKey has no query API, so the
    /// unregister+register pair doubles as the self-check: the register call's
    /// outcome is the only observable truth about whether the chord is still
    /// deliverable. The quiet mode is used by the watchdog heartbeat so a
    /// healthy periodic refresh neither floods the log nor re-renders the
    /// settings page.
    /// </summary>
    private void RefreshRegistrationCore(bool quiet, bool allowWhileSuspended = false)
    {
        Unregister(quiet);
        LastError = null;

        if (_suspendedForRecording && !allowWhileSuspended)
        {
            if (!quiet)
            {
                App.Log("[GlobalHotkey] RefreshRegistration skipped: suspended for recording");
                NotifyRegistrationChanged();
            }

            return;
        }

        if (!quiet)
        {
            App.Log($"[GlobalHotkey] RefreshRegistration hwnd=0x{_windowHandle.ToInt64():X} enabled={_settingsService.Settings.GlobalHotkeyEnabled} gesture={CurrentGestureText}");
        }

        if (_windowHandle == IntPtr.Zero || !_settingsService.Settings.GlobalHotkeyEnabled)
        {
            App.Log("[GlobalHotkey] RefreshRegistration skipped: handle=0 or disabled");
            if (!quiet)
            {
                NotifyRegistrationChanged();
            }

            return;
        }

        GlobalHotkeyActivation activation = CurrentActivation;
        if (!IsValidActivation(activation))
        {
            App.Log("[GlobalHotkey] RefreshRegistration skipped: invalid activation");
            LastError = _localizationService.T("Settings.GlobalHotkey.Status.Invalid");
            if (!quiet)
            {
                NotifyRegistrationChanged();
            }

            return;
        }

        if (!_isSubclassInstalled)
        {
            App.Log("[GlobalHotkey] RefreshRegistration skipped: window subclass unavailable");
            LastError = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
            if (!quiet)
            {
                NotifyRegistrationChanged();
            }

            return;
        }

        if (TryGetReservedHookMode(activation, out ReservedHotkeyMode reservedMode))
        {
            if (IsReservedHookDisabledByEnvironment())
            {
                App.Log("[GlobalHotkey] Reserved hotkey hook disabled by environment");
                LastError = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
                if (!quiet)
                {
                    NotifyRegistrationChanged();
                }

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
                if (!quiet)
                {
                    App.Log(
                        $"[GlobalHotkey] Registered reserved gesture={CurrentGestureText} " +
                        $"mode=hook hwnd=0x{_windowHandle.ToInt64():X}");
                    NotifyRegistrationChanged();
                }

                return;
            }

            App.Log(
                $"[GlobalHotkey] Reserved hook registration failed gesture={CurrentGestureText} " +
                $"error={hookError}");
            LastError = _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
            if (!quiet)
            {
                NotifyRegistrationChanged();
            }

            return;
        }

        if (Register(_windowHandle, MainHotkeyId, activation.Gesture, out int registerError))
        {
            _isRegistered = true;
            if (!quiet)
            {
                App.Log($"[GlobalHotkey] Registered gesture={CurrentGestureText} hwnd=0x{_windowHandle.ToInt64():X}");
                NotifyRegistrationChanged();
            }

            return;
        }

        App.Log($"[GlobalHotkey] RegisterHotKey failed gesture={CurrentGestureText} error={registerError}");
        LastError = _localizationService.T("Settings.GlobalHotkey.Status.Conflict");
        if (!quiet)
        {
            NotifyRegistrationChanged();
        }
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

        if (IsActivationOwnedBySearchHotkey(activation))
        {
            // Deep review R15 (F6 audit): the ownership check used to apply
            // only to reserved system gestures — an asymmetric defense, since
            // the search side checks the main hotkey for EVERY gesture. No
            // current preset reaches this branch with an unreserved gesture
            // (defense-in-depth), but the two services now mirror each other.
            error = _localizationService.T("Settings.GlobalHotkey.Status.SearchHotkeyConflict");
            return false;
        }

        if (IsActivationOwnedByClipboardHotkey(activation))
        {
            error = _localizationService.T("Settings.Clipboard.Hotkey.Status.ClipboardHotkeyConflict");
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
                RefreshRegistrationCore(quiet: false, allowWhileSuspended: true);
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
        RefreshRegistrationCore(quiet: false, allowWhileSuspended: true);
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
        if (!_suspendedForRecording && !IsRegistered)
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

    /// <summary>
    /// True while a recorder holds the hotkeys suspended (batch R3): the
    /// settings-host death guard watches this so a host that dies mid-
    /// recording cannot leave the global hotkey dead until restart.
    /// </summary>
    public bool IsSuspendedForRecording => _suspendedForRecording;

    string IHookHealthProbeTarget.ProbeName => "global-hotkey";

    // The reserved-gesture path owns a low-level hook that Windows silently
    // removes once its thread misses LowLevelHooksTimeout deliveries, so it
    // participates in callback liveness probing. RegisterHotKey chords deliver
    // WM_HOTKEY, have no callback and no query API, so they cannot be probed;
    // the watchdog keeps them alive with a periodic unregister+register
    // round-trip instead (feedback 308/371 showed chord registrations silently
    // dead in the field while the UI believed them registered).
    bool IHookHealthProbeTarget.HookProbeWanted => _usesReservedHook && _isRegistered;

    bool IHookHealthProbeTarget.HookConfirmedDead => !_reservedHotkeyHook.IsActive;

    long IHookHealthProbeTarget.LastHookCallbackTicks => _reservedHotkeyHook.LastCallbackTicks;

    Task<bool> IHookHealthProbeTarget.ProbeHookAliveAsync(int echoWaitMilliseconds) =>
        _reservedHotkeyHook.ProbeAliveAsync(echoWaitMilliseconds);

    void IHookHealthProbeTarget.RecoverHook() => RefreshRegistration();

    string IHookRegistrationMaintenanceTarget.MaintenanceName => "global-hotkey-registration";

    bool IHookRegistrationMaintenanceTarget.RegistrationMaintenanceWanted =>
        !_suspendedForRecording &&
        _windowHandle != IntPtr.Zero &&
        _settingsService.Settings.GlobalHotkeyEnabled &&
        UsesRegisterHotKeyChord(CurrentActivation);

    bool IHookRegistrationMaintenanceTarget.RegistrationHealthy => IsRegistered;

    void IHookRegistrationMaintenanceTarget.RunRegistrationHeartbeat()
    {
        // The recorder dialog suspends registration so the live hotkey cannot
        // eat the keys being captured; a heartbeat that re-registered
        // mid-recording would break that contract. Both this method and the
        // suspend/resume calls run on the UI dispatcher, so the flag read is
        // race-free.
        if (_suspendedForRecording)
        {
            return;
        }

        bool wasRegistered = IsRegistered;
        string? previousError = LastError;
        RefreshRegistrationCore(quiet: true, allowWhileSuspended: false);
        if (IsRegistered != wasRegistered ||
            !string.Equals(previousError, LastError, StringComparison.Ordinal))
        {
            App.Log(
                $"[GlobalHotkey] Heartbeat refreshed gesture={CurrentGestureText} " +
                $"registered={IsRegistered}");
            NotifyRegistrationChanged();
        }
        else
        {
            // A healthy round-trip produces no state change; keep the routine
            // line out of the default log level while leaving it observable.
            App.LogVerbose(
                $"[GlobalHotkey] Heartbeat refreshed gesture={CurrentGestureText} " +
                $"registered={IsRegistered}");
        }
    }

    /// <summary>
    /// True when the activation rides the plain RegisterHotKey chord path
    /// (the one the watchdog heartbeat keeps alive), as opposed to the
    /// reserved low-level-hook modes the liveness probe covers.
    /// </summary>
    internal static bool UsesRegisterHotKeyChord(GlobalHotkeyActivation activation)
    {
        return IsValidActivation(activation) && !TryGetReservedHookMode(activation, out _);
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
        int normalizedVirtualKey = normalizedKind == HotkeyActivationKind.DoubleTap && virtualKey < 0
            ? 0
            : virtualKey;
        return new GlobalHotkeyActivation(
            normalizedKind,
            NormalizeGesture(modifiers, normalizedVirtualKey));
    }

    public static bool IsReservedSystemGesture(GlobalHotkeyGesture gesture)
    {
        return gesture.VirtualKey == (int)VirtualKey.Space &&
               gesture.Modifiers is
                   HotkeyModifierKeys.Windows or
                   HotkeyModifierKeys.Alt;
    }

    public static bool IsWellKnownWindowsShortcut(GlobalHotkeyGesture gesture)
    {
        if (gesture.Modifiers == (HotkeyModifierKeys.Windows | HotkeyModifierKeys.Shift) &&
            gesture.VirtualKey == (int)VirtualKey.S)
        {
            return true;
        }

        if (gesture.Modifiers != HotkeyModifierKeys.Windows)
        {
            return false;
        }

        return gesture.VirtualKey is
            (int)VirtualKey.A or
            (int)VirtualKey.D or
            (int)VirtualKey.E or
            (int)VirtualKey.G or
            (int)VirtualKey.H or
            (int)VirtualKey.I or
            (int)VirtualKey.K or
            (int)VirtualKey.L or
            (int)VirtualKey.M or
            (int)VirtualKey.N or
            (int)VirtualKey.P or
            (int)VirtualKey.R or
            (int)VirtualKey.S or
            (int)VirtualKey.U or
            (int)VirtualKey.V or
            (int)VirtualKey.W or
            (int)VirtualKey.X or
            (int)VirtualKey.Z or
            (int)VirtualKey.Tab;
    }

    public static bool AreEquivalentActivations(
        GlobalHotkeyActivation first,
        GlobalHotkeyActivation second)
    {
        if (TryGetStandaloneModifierKind(first, out HotkeyModifierKeys firstModifier, out bool firstIsDouble) &&
            TryGetStandaloneModifierKind(second, out HotkeyModifierKeys secondModifier, out bool secondIsDouble))
        {
            return firstModifier == secondModifier && firstIsDouble == secondIsDouble;
        }

        return first.Kind == second.Kind && first.Gesture.Equals(second.Gesture);
    }

    public static bool DoActivationsConflict(
        GlobalHotkeyActivation first,
        GlobalHotkeyActivation second)
    {
        if (!IsValidActivation(first) || !IsValidActivation(second))
        {
            return false;
        }

        bool firstStandalone = TryGetStandaloneModifierKind(first, out HotkeyModifierKeys firstModifier, out _);
        bool secondStandalone = TryGetStandaloneModifierKind(second, out HotkeyModifierKeys secondModifier, out _);
        if (firstStandalone || secondStandalone)
        {
            return firstStandalone && secondStandalone && firstModifier == secondModifier;
        }

        return first.Gesture.Equals(second.Gesture);
    }

    private static bool TryGetStandaloneModifierKind(
        GlobalHotkeyActivation activation,
        out HotkeyModifierKeys modifier,
        out bool isDoubleTap)
    {
        switch (activation.Kind)
        {
            case HotkeyActivationKind.DoubleControl:
                modifier = HotkeyModifierKeys.Control;
                isDoubleTap = true;
                return true;
            case HotkeyActivationKind.WindowsTap:
                modifier = HotkeyModifierKeys.Windows;
                isDoubleTap = false;
                return true;
            case HotkeyActivationKind.DoubleTap when activation.IsModifierOnlyDoubleTap:
                modifier = activation.DoubleTapModifier;
                isDoubleTap = true;
                return true;
            default:
                modifier = HotkeyModifierKeys.None;
                isDoubleTap = false;
                return false;
        }
    }

    internal bool IsActivationOwnedBySearchHotkey(GlobalHotkeyActivation activation)
    {
        if (!_settingsService.Settings.Search.SearchHotkeyEnabled)
        {
            return false;
        }

        GlobalHotkeyActivation searchActivation = App.Current?.SearchHotkeyService?.CurrentActivation
            ?? NormalizeActivation(
                _settingsService.Settings.Search.SearchHotkeyActivationKind,
                _settingsService.Settings.Search.SearchHotkeyModifiers,
                _settingsService.Settings.Search.SearchHotkeyKey);
        return DoActivationsConflict(activation, searchActivation);
    }

    internal bool IsGestureOwnedBySearchHotkey(GlobalHotkeyGesture gesture)
    {
        return IsActivationOwnedBySearchHotkey(GlobalHotkeyActivation.FromChord(gesture));
    }

    internal bool IsActivationOwnedByClipboardHotkey(GlobalHotkeyActivation activation)
    {
        var settings = _settingsService.Settings;
        if (!FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Clipboard) ||
            !settings.Clipboard.ClipboardHotkeyEnabled)
        {
            return false;
        }

        GlobalHotkeyActivation clipboardActivation = NormalizeActivation(
            settings.Clipboard.ClipboardHotkeyActivationKind,
            settings.Clipboard.ClipboardHotkeyModifiers,
            settings.Clipboard.ClipboardHotkeyKey);
        return DoActivationsConflict(activation, clipboardActivation);
    }

    public static bool IsValidActivation(GlobalHotkeyActivation activation)
    {
        if (!Enum.IsDefined(activation.Kind))
        {
            return false;
        }

        return activation.Kind switch
        {
            HotkeyActivationKind.DoubleControl => true,
            HotkeyActivationKind.WindowsTap => true,
            HotkeyActivationKind.Chord => IsValidGesture(activation.Gesture),
            HotkeyActivationKind.DoubleTap => activation.Gesture.VirtualKey <= 0
                ? GlobalHotkeyActivation.IsSingleModifierFlag(activation.Gesture.Modifiers)
                : activation.IsStandaloneSpaceDoubleTap || IsValidGesture(activation.Gesture),
            _ => false
        };
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

    public static string FormatModifier(HotkeyModifierKeys modifier)
    {
        return modifier switch
        {
            HotkeyModifierKeys.Windows => "Win",
            HotkeyModifierKeys.Control => "Ctrl",
            HotkeyModifierKeys.Alt => "Alt",
            HotkeyModifierKeys.Shift => "Shift",
            _ => modifier.ToString()
        };
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
            HotkeyActivationKind.DoubleTap when activation.IsModifierOnlyDoubleTap =>
                activation.DoubleTapModifier == HotkeyModifierKeys.Control
                    ? localization.T("Settings.GlobalHotkey.Preset.DoubleControl")
                    : string.Format(
                        localization.T("Settings.GlobalHotkey.DoubleTapFormat"),
                        FormatModifier(activation.DoubleTapModifier)),
            HotkeyActivationKind.DoubleTap =>
                string.Format(
                    localization.T("Settings.GlobalHotkey.DoubleTapFormat"),
                    FormatGesture(activation.Gesture, localization)),
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
        GlobalHotkeyActivation currentActivation = CurrentActivation;
        App.LogVerbose(
            $"[GlobalHotkey] Received id={receivedId} source={source} " +
            $"gesture={CurrentGestureText}");

        if (currentActivation.Kind == HotkeyActivationKind.DoubleTap &&
            !currentActivation.IsModifierOnlyDoubleTap &&
            !currentActivation.IsStandaloneSpaceDoubleTap &&
            !_doubleTapChordGate.RecordPressAndShouldTrigger(currentActivation.Gesture, Environment.TickCount64))
        {
            return;
        }

        if (releaseStandardModifiers)
        {
            // Clear Ctrl/Alt/Shift states that can become stuck in RDP. The
            // reserved Win+Space path owns its key-up state and never uses this.
            // Deliberately synchronous (callback-isolation exemption, input
            // family, not window family): none of the radar-tracked Win32
            // entry points (SetWindowPos/ShowWindow/SetForegroundWindow/
            // SetWindowLongPtr) is touched here. Deferring to "before the next
            // input" would instead release a modifier combination the user is
            // actively holding — F7 followed by an immediate Ctrl+C would fire
            // as bare keys.
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

        if (_usesReservedHook)
        {
            _ = ProbeWindowsTapStartMenuLeakAsync(invocationId);
        }
    }

    /// <summary>
    /// Detects, for diagnostics only, that a WindowsTap trigger lost the
    /// Start-menu mask race: the mask injection never fails with an error
    /// for a lost race, so a delayed foreground check is the only trace
    /// (feedback 367). Dismissing the just-opened Start menu was rejected
    /// because it is by definition a visible flicker.
    /// </summary>
    private async Task ProbeWindowsTapStartMenuLeakAsync(long invocationId)
    {
        const int probeDelayMilliseconds = 400;
        try
        {
            await Task.Delay(probeDelayMilliseconds);
            if (!_usesReservedHook ||
                !TryGetReservedHookMode(CurrentActivation, out ReservedHotkeyMode mode) ||
                mode != ReservedHotkeyMode.WindowsTap)
            {
                return;
            }

            if (!WindowsTapStartMenuLeakProbe.TryGetForegroundProcessName(
                    out string? foregroundProcessName) ||
                !WindowsTapStartMenuLeakPolicy.IsStartMenuHostProcessName(
                    foregroundProcessName))
            {
                return;
            }

            _reservedHotkeyHook.RecordStartMenuLeak();
            App.Log(
                $"[GlobalHotkey] WindowsTap start-menu mask lost the race " +
                $"id={invocationId} foreground={foregroundProcessName} " +
                $"totalLeaks={_reservedHotkeyHook.StartMenuLeakCount}");
        }
        catch (Exception ex)
        {
            App.Log($"[GlobalHotkey] Start-menu leak probe failed: {ex.Message}");
        }
    }

    private void Unregister(bool quiet = false)
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
                App.Log($"[GlobalHotkey] Reserved hook removal failed: {ex}");
            }

            if (!quiet)
            {
                App.Log($"[GlobalHotkey] Reserved hook removed gesture={CurrentGestureText}");
            }
        }
        else if (_isRegistered && _windowHandle != IntPtr.Zero)
        {
            Win32Helper.UnregisterHotKey(_windowHandle, MainHotkeyId);
            if (!quiet)
            {
                App.Log($"[GlobalHotkey] Unregistered gesture={CurrentGestureText}");
            }
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

    internal static bool TryResolveReservedHookMode(
        GlobalHotkeyActivation activation,
        out ReservedHotkeyMode mode)
    {
        return TryGetReservedHookMode(activation, out mode);
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

        if (activation.Kind == HotkeyActivationKind.DoubleTap &&
            activation.IsModifierOnlyDoubleTap)
        {
            switch (activation.DoubleTapModifier)
            {
                case HotkeyModifierKeys.Control:
                    mode = ReservedHotkeyMode.DoubleControl;
                    return true;
                case HotkeyModifierKeys.Shift:
                    mode = ReservedHotkeyMode.DoubleShift;
                    return true;
                case HotkeyModifierKeys.Alt:
                    mode = ReservedHotkeyMode.DoubleAlt;
                    return true;
                case HotkeyModifierKeys.Windows:
                    mode = ReservedHotkeyMode.DoubleWindows;
                    return true;
            }
        }

        if (activation.IsStandaloneSpaceDoubleTap)
        {
            mode = ReservedHotkeyMode.DoubleSpace;
            return true;
        }

        if (activation.Kind is HotkeyActivationKind.Chord or HotkeyActivationKind.DoubleTap &&
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
               IsOemPunctuationKey(virtualKey) ||
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

    private static bool IsOemPunctuationKey(int virtualKey)
    {
        return virtualKey is
            0xBA or // ;:
            0xBB or // =+
            0xBC or // ,<
            0xBD or // -_
            0xBE or // .>
            0xBF or // /?
            0xC0 or // `~
            0xDB or // [{
            0xDC or // \|
            0xDD or // ]}
            0xDE;   // '"
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

    internal static string FormatVirtualKey(int virtualKey)
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

        return virtualKey switch
        {
            (int)VirtualKey.Space => "Space",
            (int)VirtualKey.Tab => "Tab",
            (int)VirtualKey.Insert => "Insert",
            (int)VirtualKey.Delete => "Delete",
            (int)VirtualKey.Home => "Home",
            (int)VirtualKey.End => "End",
            (int)VirtualKey.PageUp => "Page Up",
            (int)VirtualKey.PageDown => "Page Down",
            0xBA => ";",
            0xBB => "=",
            0xBC => ",",
            0xBD => "-",
            0xBE => ".",
            0xBF => "/",
            0xC0 => "`",
            0xDB => "[",
            0xDC => "\\",
            0xDD => "]",
            0xDE => "'",
            _ => ((VirtualKey)virtualKey).ToString()
        };
    }

    private void NotifyRegistrationChanged()
    {
        RegistrationChanged?.Invoke();
    }
}
