// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Contracts;
using DeskBox.Controls;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Views.Dialogs;

/// <summary>
/// PowerToys-style activation-shortcut editor with optional double-tap
/// recording and recommended presets. A low-level keyboard hook feeds every key
/// event into <see cref="HotkeyRecordingSession"/> while the dialog is open, so
/// OS-reserved chords (Win+V, Alt+Space, …) are captured instead of stolen by
/// the shell.
/// </summary>
public sealed partial class HotkeyRecorderDialog : ContentDialog
{
    public enum Scope
    {
        Global,
        Search,
        Clipboard
    }

    public sealed record Preset(
        string Label,
        GlobalHotkeyActivation Activation,
        string? ToolTip = null);

    private readonly LocalizationService _localization;
    private readonly ISettingsHotkeyActions _hotkeyActions;
    internal Scope RecorderScope { get; }
    private readonly GlobalHotkeyActivation _initialActivation;
    private readonly IReadOnlyList<Preset> _presets;
    private readonly Func<GlobalHotkeyActivation, Task<string?>> _applyAsync;
    private readonly Func<GlobalHotkeyActivation, string?> _conflictChecker;
    private readonly HotkeyRecordingSession _session = new();
    private readonly HotkeyRecorderHookService _hook;
    private readonly List<ToggleButton> _presetButtons = [];
    private readonly TextBlock _emptyHint;
    private readonly string _doubleTapBadgeText;
    private GlobalHotkeyActivation? _selectedActivation;
    private int _selectedPresetIndex = -1;
    private readonly HashSet<ButtonBase> _keyboardFocusedButtons = [];
    private bool _isDoubleTapMode;
    private bool _isSyncingToggle;
    private bool _isApplying;

    /// <summary>
    /// The one recorder currently capturing (a XamlRoot hosts a single
    /// ContentDialog at a time). The settings window hides or closes through
    /// paths that never raise <see cref="ContentDialog.Closing"/> — the
    /// dialog's only hook teardown — so it can force the recorder shut.
    /// </summary>
    private static HotkeyRecorderDialog? _activeRecorder;

    public HotkeyRecorderDialog(
        XamlRoot xamlRoot,
        LocalizationService localization,
        ISettingsHotkeyActions hotkeyActions,
        Scope scope,
        GlobalHotkeyActivation initialActivation,
        IReadOnlyList<Preset> presets,
        Func<GlobalHotkeyActivation, Task<string?>> applyAsync,
        Func<GlobalHotkeyGesture, string?> conflictChecker)
        : this(
            xamlRoot,
            localization,
            hotkeyActions,
            scope,
            initialActivation,
            presets,
            applyAsync,
            activation => GlobalHotkeyService.IsValidActivation(activation) &&
                          activation.Kind is HotkeyActivationKind.Chord or HotkeyActivationKind.DoubleTap &&
                          !activation.IsModifierOnlyDoubleTap
                ? conflictChecker(activation.Gesture)
                : null)
    {
    }

    public HotkeyRecorderDialog(
        XamlRoot xamlRoot,
        LocalizationService localization,
        ISettingsHotkeyActions hotkeyActions,
        Scope scope,
        GlobalHotkeyActivation initialActivation,
        IReadOnlyList<Preset> presets,
        Func<GlobalHotkeyActivation, Task<string?>> applyAsync,
        Func<GlobalHotkeyActivation, string?> conflictChecker)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        _hook = new HotkeyRecorderHookService(DispatcherQueue);
        // Any command button (presets, Save/Cancel/Reset/Clear/DoubleTap) that owns
        // focus must keep bare Enter/Space for itself while modifier+Space chords
        // continue to capture inside HotkeyRecorderHookService.
        GotFocus += (_, e) => OnElementGotFocus(e.OriginalSource);
        LostFocus += (_, e) => OnElementLostFocus(e.OriginalSource);
        ActualThemeChanged += (_, _) => UpdateEmptyHintForeground();

        _localization = localization;
        _hotkeyActions = hotkeyActions;
        RecorderScope = scope;
        _initialActivation = initialActivation;
        _presets = presets;
        _applyAsync = applyAsync;
        _conflictChecker = conflictChecker;
        _doubleTapBadgeText = localization.T("Settings.HotkeyRecorder.DoubleTapBadge");

        Title = localization.T("Settings.HotkeyRecorder.Title");
        PrimaryButtonText = localization.T("Common.Save");
        CloseButtonText = localization.T("Common.Cancel");
        ResetButtonLabel.Text = localization.T("Settings.HotkeyRecorder.Reset");
        ClearButtonLabel.Text = localization.T("Settings.HotkeyRecorder.Clear");
        DoubleTapToggleLabel.Text = localization.T("Settings.HotkeyRecorder.DoubleTap");
        ToolTipService.SetToolTip(
            DoubleTapToggle,
            localization.T("Settings.HotkeyRecorder.DoubleTapTooltip"));
        PresetsLabel.Text = localization.T("Settings.GlobalHotkey.PresetsTitle");

        _emptyHint = new TextBlock
        {
            Text = localization.T("Settings.HotkeyRecorder.PressKeys"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center
        };
        UpdateEmptyHintForeground();

        BuildPresets();

        _selectedActivation = IsSelectableActivation(initialActivation)
            ? initialActivation
            : null;
        _isDoubleTapMode = _selectedActivation?.IsDoubleTap == true;
        SyncDoubleTapToggleVisual();
        UpdateRuleHintText();
        _selectedPresetIndex = FindPresetIndex(_selectedActivation);
        UpdatePresetChecks();
        RevalidateAndRender();
    }

    private void UpdateEmptyHintForeground()
    {
        // Application-scope themed lookups follow the system theme;
        // resolve against the dialog's window-scoped theme instead.
        _emptyHint.Foreground = NeutralInteractionBrush.ResolveThemedResource(
            "TextFillColorSecondaryBrush", this) ??
            new SolidColorBrush(Windows.UI.Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _activeRecorder = this;

        // Suspend the live registrations first so neither hotkey fires nor its
        // reserved hook eats the keys we are about to record.
        _hotkeyActions.SuspendHotkeysForRecording();

        _hook.KeyCaptured += OnKeyCaptured;
        if (!_hook.TryStart(out int errorCode))
        {
            App.Log($"[HotkeyRecorder] Capture hook unavailable error={errorCode}");
            CaptureStatusInfoBar.Message =
                _localization.T("Settings.HotkeyRecorder.HookFailed");
            CaptureStatusInfoBar.IsOpen = true;
        }
    }

    private void OnClosing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        TeardownCapture();
    }

    /// <summary>
    /// Releases the capture hook and resumes the suspended hotkey services.
    /// Idempotent, so every close path (Closing event, forced shutdown) can
    /// call it unconditionally.
    /// </summary>
    private void TeardownCapture()
    {
        if (_activeRecorder == this)
        {
            _activeRecorder = null;
        }

        _hook.KeyCaptured -= OnKeyCaptured;
        _hook.Stop();
        _hotkeyActions.ResumeHotkeysAfterRecording();
    }

    /// <summary>
    /// Closes the recorder dialog (if one is capturing) so its low-level
    /// keyboard hook and hotkey suspensions are released.
    /// </summary>
    internal static void ShutdownActiveRecorder()
    {
        HotkeyRecorderDialog? recorder = _activeRecorder;
        if (recorder is null)
        {
            return;
        }

        try
        {
            recorder.Hide();
        }
        catch (Exception ex)
        {
            App.Log($"[HotkeyRecorder] Forced shutdown failed: {ex.Message}");
            recorder.TeardownCapture();
        }
    }

    private void OnKeyCaptured(HotkeyRecorderHookService.RecordedKeyEvent captured)
    {
        _session.ProcessKey(captured.VirtualKey, captured.IsKeyDown);
        if (_session.CancelRequested)
        {
            Hide();
            return;
        }

        if (_session.HasGesture)
        {
            _selectedActivation = _isDoubleTapMode
                ? GlobalHotkeyActivation.FromDoubleTapChord(_session.Gesture)
                : GlobalHotkeyActivation.FromChord(_session.Gesture);
        }
        else if (_session.HeldModifiers != HotkeyModifierKeys.None)
        {
            // Live modifier hold in progress: clear committed selection so the
            // hero well previews the held modifier keys dynamically.
            _selectedActivation = null;
        }
        else if (_session.HasStandaloneModifier)
        {
            _selectedActivation = _isDoubleTapMode
                ? GlobalHotkeyActivation.FromDoubleTapModifier(_session.LastStandaloneModifier)
                : new GlobalHotkeyActivation(
                    HotkeyActivationKind.Chord,
                    new GlobalHotkeyGesture(_session.LastStandaloneModifier, 0));
        }

        _selectedPresetIndex = FindPresetIndex(_selectedActivation);
        UpdatePresetChecks();
        RevalidateAndRender();
    }

    private void DoubleTapToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_isSyncingToggle)
        {
            return;
        }

        _isDoubleTapMode = DoubleTapToggle.IsChecked == true;
        UpdateRuleHintText();

        if (_selectedActivation is { } current)
        {
            _selectedActivation = ConvertActivationForDoubleTapMode(current, _isDoubleTapMode);
        }

        _selectedPresetIndex = FindPresetIndex(_selectedActivation);
        UpdatePresetChecks();
        RevalidateAndRender();
    }

    private static GlobalHotkeyActivation ConvertActivationForDoubleTapMode(
        GlobalHotkeyActivation current,
        bool doubleTapEnabled)
    {
        if (doubleTapEnabled)
        {
            return current.Kind switch
            {
                HotkeyActivationKind.DoubleControl => current,
                HotkeyActivationKind.DoubleTap => current,
                HotkeyActivationKind.WindowsTap =>
                    GlobalHotkeyActivation.FromDoubleTapModifier(HotkeyModifierKeys.Windows),
                HotkeyActivationKind.Chord when current.Gesture.VirtualKey <= 0 &&
                    GlobalHotkeyActivation.IsSingleModifierFlag(current.Gesture.Modifiers) =>
                    GlobalHotkeyActivation.FromDoubleTapModifier(current.Gesture.Modifiers),
                _ => GlobalHotkeyActivation.FromDoubleTapChord(current.Gesture)
            };
        }

        return current.Kind switch
        {
            HotkeyActivationKind.DoubleControl =>
                new GlobalHotkeyActivation(
                    HotkeyActivationKind.Chord,
                    new GlobalHotkeyGesture(HotkeyModifierKeys.Control, 0)),
            HotkeyActivationKind.DoubleTap when current.IsModifierOnlyDoubleTap =>
                new GlobalHotkeyActivation(
                    HotkeyActivationKind.Chord,
                    new GlobalHotkeyGesture(current.DoubleTapModifier, 0)),
            HotkeyActivationKind.DoubleTap =>
                GlobalHotkeyActivation.FromChord(current.Gesture),
            _ => current
        };
    }

    public static GlobalHotkeyActivation GetDefaultActivation(Scope scope) => scope switch
    {
        Scope.Search => GlobalHotkeyActivation.FromChord(
            DeskBox.Features.Search.SearchSettingsViewModel.DefaultGesture),
        Scope.Clipboard => GlobalHotkeyActivation.FromChord(
            ClipboardHotkeyService.DefaultGesture),
        _ => new GlobalHotkeyActivation(
            SettingsService.DefaultGlobalHotkeyActivationKind,
            new GlobalHotkeyGesture(
                (HotkeyModifierKeys)SettingsService.DefaultGlobalHotkeyModifiers,
                SettingsService.DefaultGlobalHotkeyKey))
    };

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Clear();
        GlobalHotkeyActivation defaultActivation = GetDefaultActivation(RecorderScope);
        _selectedActivation = IsSelectableActivation(defaultActivation)
            ? defaultActivation
            : null;
        _isDoubleTapMode = _selectedActivation?.IsDoubleTap == true;
        SyncDoubleTapToggleVisual();
        UpdateRuleHintText();
        _selectedPresetIndex = FindPresetIndex(_selectedActivation);
        UpdatePresetChecks();
        RevalidateAndRender();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Clear();
        _selectedActivation = null;
        _selectedPresetIndex = -1;
        UpdatePresetChecks();
        RevalidateAndRender();
    }

    private async void OnPrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        if (_selectedActivation is not { } activation)
        {
            return;
        }

        var deferral = args.GetDeferral();
        args.Cancel = true;
        string? error;
        _isApplying = true;
        IsPrimaryButtonEnabled = false;
        try
        {
            error = await _applyAsync(activation);
        }
        catch (Exception ex)
        {
            App.Log($"[HotkeyRecorder] Apply failed: {ex}");
            error = _localization.T("Settings.HotkeyRecorder.ApplyFailed");
        }
        finally
        {
            _isApplying = false;
            RevalidateAndRender();
            deferral.Complete();
        }

        if (error is null)
        {
            Hide();
        }
        else
        {
            ErrorInfoBar.Message = error;
            ErrorInfoBar.IsOpen = true;
        }
    }

    private void BuildPresets()
    {
        if (_presets.Count == 0)
        {
            PresetsSection.Visibility = Visibility.Collapsed;
            return;
        }

        for (int index = 0; index < _presets.Count; index++)
        {
            int capturedIndex = index;
            var button = new ToggleButton
            {
                Style = (Style)Resources["PresetButtonStyle"],
                Content = new TextBlock { Text = _presets[index].Label }
            };
            if (!string.IsNullOrWhiteSpace(_presets[index].ToolTip))
            {
                ToolTipService.SetToolTip(button, _presets[index].ToolTip);
            }

            button.Click += (_, _) => SelectPreset(capturedIndex);
            _presetButtons.Add(button);
            PresetsPanel.Children.Add(button);
        }
    }

    private void SelectPreset(int index)
    {
        _session.Clear();
        _selectedPresetIndex = index;
        _selectedActivation = _presets[index].Activation;
        _isDoubleTapMode = _selectedActivation.Value.IsDoubleTap;
        SyncDoubleTapToggleVisual();
        UpdateRuleHintText();
        UpdatePresetChecks();
        RevalidateAndRender();
    }

    private void SyncDoubleTapToggleVisual()
    {
        _isSyncingToggle = true;
        try
        {
            DoubleTapToggle.IsChecked = _isDoubleTapMode;
        }
        finally
        {
            _isSyncingToggle = false;
        }
    }

    private void UpdateRuleHintText()
    {
        RuleHintTextBlock.Text = _localization.T(
            _isDoubleTapMode
                ? "Settings.HotkeyRecorder.DoubleTapRuleHint"
                : "Settings.HotkeyRecorder.RuleHint");
    }

    private void OnElementGotFocus(object focusedElement)
    {
        // Only keyboard focus (Tab navigation) passes bare Space/Enter through
        // to buttons; clicking Double-Tap/Clear/Reset with a mouse leaves Space
        // available for recording (e.g. Double-tap Space).
        if (focusedElement is ButtonBase { FocusState: FocusState.Keyboard } button)
        {
            _keyboardFocusedButtons.Add(button);
            _hook.PassthroughRequested = _keyboardFocusedButtons.Count > 0;
        }
    }

    private void OnElementLostFocus(object focusedElement)
    {
        if (focusedElement is ButtonBase button && _keyboardFocusedButtons.Remove(button))
        {
            _hook.PassthroughRequested = _keyboardFocusedButtons.Count > 0;
        }
    }

    private int FindPresetIndex(GlobalHotkeyActivation? activation)
    {
        if (activation is not { } selected || !GlobalHotkeyService.IsValidActivation(selected))
        {
            return -1;
        }

        for (int index = 0; index < _presets.Count; index++)
        {
            if (GlobalHotkeyService.AreEquivalentActivations(_presets[index].Activation, selected))
            {
                return index;
            }
        }

        return -1;
    }

    private void UpdatePresetChecks()
    {
        for (int index = 0; index < _presetButtons.Count; index++)
        {
            _presetButtons[index].IsChecked = index == _selectedPresetIndex;
        }
    }

    private void RevalidateAndRender()
    {
        string? error = null;
        string? warning = null;
        bool canSave = false;

        if (_selectedActivation is { } activation)
        {
            if (!GlobalHotkeyService.IsValidActivation(activation))
            {
                if (activation.Kind == HotkeyActivationKind.Chord &&
                    activation.Gesture.VirtualKey <= 0 &&
                    GlobalHotkeyActivation.IsSingleModifierFlag(activation.Gesture.Modifiers))
                {
                    error = _localization.T("Settings.HotkeyRecorder.SingleModifierNeedsDoubleTap");
                }
                else if (activation.Kind == HotkeyActivationKind.Chord &&
                         activation.Gesture.Modifiers == HotkeyModifierKeys.None &&
                         activation.Gesture.VirtualKey == (int)Windows.System.VirtualKey.Space)
                {
                    error = _localization.T("Settings.HotkeyRecorder.SingleSpaceNeedsDoubleTap");
                }
                else
                {
                    error = _localization.T("Settings.HotkeyRecorder.InvalidGesture");
                }
            }
            else if (_conflictChecker(activation) is { } conflict)
            {
                error = conflict;
            }
            else
            {
                canSave = true;
                warning = GetWarningText(activation);
            }
        }

        ErrorInfoBar.Message = error ?? string.Empty;
        ErrorInfoBar.IsOpen = error is not null;
        WarningInfoBar.Message = warning ?? string.Empty;
        WarningInfoBar.IsOpen = warning is not null;
        IsPrimaryButtonEnabled = canSave && !_isApplying;

        KeyVisualState visualState = error is not null
            ? KeyVisualState.Error
            : KeyVisualState.Normal;
        RenderKeys(visualState);
    }

    private void RenderKeys(KeyVisualState visualState)
    {
        KeysPanel.Children.Clear();

        IReadOnlyList<KeyVisualToken> tokens;
        if (_selectedActivation is { } selected)
        {
            tokens = KeyVisualControl.BuildTokens(selected, _doubleTapBadgeText);
        }
        else if (_session.HeldModifiers != HotkeyModifierKeys.None)
        {
            tokens = KeyVisualControl.BuildLiveCaptureTokens(
                _session.HeldModifiers,
                primaryKey: 0,
                isDoubleTap: _isDoubleTapMode,
                _doubleTapBadgeText);
        }
        else
        {
            tokens = [];
        }

        ClearButton.Visibility = tokens.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (tokens.Count == 0)
        {
            KeysPanel.Children.Add(_emptyHint);
            return;
        }

        foreach (KeyVisualToken token in tokens)
        {
            KeysPanel.Children.Add(KeyVisualControl.CreateHeroVisual(token, visualState));
        }
    }

    private string? GetWarningText(GlobalHotkeyActivation activation)
    {
        if (activation.Kind == HotkeyActivationKind.WindowsTap)
        {
            return _localization.T("Settings.GlobalHotkey.WindowsTapWarning");
        }

        if (activation.IsStandaloneSpaceDoubleTap)
        {
            return _localization.T("Settings.GlobalHotkey.DoubleSpaceWarning");
        }

        if (activation.Kind is not (HotkeyActivationKind.Chord or HotkeyActivationKind.DoubleTap) ||
            activation.IsModifierOnlyDoubleTap)
        {
            return null;
        }

        GlobalHotkeyGesture gesture = activation.Gesture;
        if (GlobalHotkeyService.IsReservedSystemGesture(gesture))
        {
            return gesture.Modifiers == HotkeyModifierKeys.Alt
                ? _localization.T("Settings.GlobalHotkey.AltSpaceWarning")
                : _localization.T("Settings.GlobalHotkey.ReservedWarning");
        }

        if (GlobalHotkeyService.IsWellKnownWindowsShortcut(gesture))
        {
            return _localization.T("Settings.HotkeyRecorder.SystemShortcutWarning");
        }

        if (gesture.Modifiers == (HotkeyModifierKeys.Control | HotkeyModifierKeys.Alt))
        {
            return _localization.T("Settings.HotkeyRecorder.AltGrWarning");
        }

        return GlobalHotkeyService.IsRiskyGesture(gesture)
            ? _localization.T("Settings.HotkeyRecorder.RiskyWarning")
            : null;
    }

    private static bool IsSelectableActivation(GlobalHotkeyActivation activation)
    {
        return GlobalHotkeyService.IsValidActivation(activation);
    }
}
