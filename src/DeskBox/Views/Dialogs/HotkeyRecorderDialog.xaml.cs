// Copyright (c) DeskBox. All rights reserved.

using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace DeskBox.Views.Dialogs;

/// <summary>
/// PowerToys-style activation-shortcut editor. A low-level keyboard hook feeds
/// every key event into <see cref="HotkeyRecordingSession"/> while the dialog
/// is open, so OS-reserved chords (Win+V, Alt+Space, …) are captured instead of
/// stolen by the shell. The dialog itself stays dumb about hotkey storage: the
/// caller supplies presets, a conflict checker, and the apply callback.
/// </summary>
public sealed partial class HotkeyRecorderDialog : ContentDialog
{
    public enum Scope
    {
        Global,
        Search
    }

    public sealed record Preset(
        string Label,
        GlobalHotkeyActivation Activation,
        string? ToolTip = null);

    private const string WindowsLogoPathData =
        "M0.2 1.2 L5.0 0.5 L5.0 5.4 L0.2 5.4 Z " +
        "M5.6 0.3 L10.6 0.3 L10.6 5.4 L5.6 5.4 Z " +
        "M0.2 6.0 L5.0 6.0 L5.0 10.7 L0.2 10.0 Z " +
        "M5.6 6.0 L10.6 6.0 L10.6 10.9 L5.6 10.9 Z";

    private readonly LocalizationService _localization;
    private readonly Scope _scope;
    private readonly GlobalHotkeyActivation _initialActivation;
    private readonly IReadOnlyList<Preset> _presets;
    private readonly Func<GlobalHotkeyActivation, Task<string?>> _applyAsync;
    private readonly Func<GlobalHotkeyGesture, string?> _conflictChecker;
    private readonly HotkeyRecordingSession _session = new();
    private readonly HotkeyRecorderHookService _hook;
    private readonly List<ToggleButton> _presetButtons = [];
    private readonly TextBlock _emptyHint;
    private GlobalHotkeyActivation? _selectedActivation;
    private int _selectedPresetIndex = -1;
    private int _buttonFocusCount;
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
        Scope scope,
        GlobalHotkeyActivation initialActivation,
        IReadOnlyList<Preset> presets,
        Func<GlobalHotkeyActivation, Task<string?>> applyAsync,
        Func<GlobalHotkeyGesture, string?> conflictChecker)
    {
        InitializeComponent();
        XamlRoot = xamlRoot;
        _hook = new HotkeyRecorderHookService(DispatcherQueue);
        // Any command button (presets, Save/Cancel/Reset/Clear) that owns
        // focus must keep Enter/Space for itself — the recording hook would
        // otherwise swallow the activation press into the chord. The dialog
        // template's buttons are part of this visual tree, so the bubbled
        // GotFocus/LostFocus pair covers every button.
        GotFocus += (_, e) => AdjustButtonPassthrough(e.OriginalSource, +1);
        LostFocus += (_, e) => AdjustButtonPassthrough(e.OriginalSource, -1);
        _localization = localization;
        _scope = scope;
        _initialActivation = initialActivation;
        _presets = presets;
        _applyAsync = applyAsync;
        _conflictChecker = conflictChecker;

        Title = localization.T("Settings.HotkeyRecorder.Title");
        PrimaryButtonText = localization.T("Common.Save");
        CloseButtonText = localization.T("Common.Cancel");
        RuleHintTextBlock.Text = localization.T("Settings.HotkeyRecorder.RuleHint");
        ResetButton.Content = localization.T("Settings.HotkeyRecorder.Reset");
        ClearButton.Content = localization.T("Settings.HotkeyRecorder.Clear");
        PresetsLabel.Text = localization.T("Settings.GlobalHotkey.PresetsTitle");
        _emptyHint = new TextBlock
        {
            Text = localization.T("Settings.HotkeyRecorder.PressKeys"),
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            VerticalAlignment = VerticalAlignment.Center
        };

        BuildPresets();

        _selectedActivation = IsSelectableActivation(initialActivation)
            ? initialActivation
            : null;
        _selectedPresetIndex = FindPresetIndex(_selectedActivation);
        UpdatePresetChecks();
        RenderKeys();
        Revalidate();
    }

    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        _activeRecorder = this;

        // Suspend the live registrations first so neither hotkey fires nor its
        // reserved hook eats the keys we are about to record.
        App.Current.GlobalHotkeyService?.SuspendForRecording();
        App.Current.SearchHotkeyService?.SuspendForRecording();

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
        App.Current.SearchHotkeyService?.ResumeAfterRecording();
        App.Current.GlobalHotkeyService?.ResumeAfterRecording();
    }

    /// <summary>
    /// Closes the recorder dialog (if one is capturing) so its low-level
    /// keyboard hook and hotkey suspensions are released. Called when the
    /// settings window hides or really closes while a recording is in
    /// progress: those paths never raise ContentDialog.Closing on their own,
    /// and a leaked hook swallows every keystroke system-wide.
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
            // Programmatic Hide always raises Closing, which runs the full
            // teardown through TeardownCapture.
            recorder.Hide();
        }
        catch (Exception ex)
        {
            // XAML teardown races a closing window; stop the hook directly so
            // input is released even if the dialog itself is already dead.
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

        _selectedPresetIndex = -1;
        _selectedActivation = _session.HasGesture
            ? GlobalHotkeyActivation.FromChord(_session.Gesture)
            : null;
        UpdatePresetChecks();
        RenderKeys();
        Revalidate();
    }

    private void ResetButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Clear();
        _selectedActivation = IsSelectableActivation(_initialActivation)
            ? _initialActivation
            : null;
        _selectedPresetIndex = FindPresetIndex(_selectedActivation);
        UpdatePresetChecks();
        RenderKeys();
        Revalidate();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _session.Clear();
        _selectedActivation = null;
        _selectedPresetIndex = -1;
        UpdatePresetChecks();
        RenderKeys();
        Revalidate();
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
            Revalidate();
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
            // Presets stay one-word compact; hardware requirements (e.g. the
            // Copilot key) ride along as tooltips instead of label suffixes.
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
        UpdatePresetChecks();
        RenderKeys();
        Revalidate();
    }

    private void AdjustButtonPassthrough(object focusedElement, int delta)
    {
        if (focusedElement is not ButtonBase)
        {
            return;
        }

        _buttonFocusCount = Math.Max(0, _buttonFocusCount + delta);
        _hook.PassthroughRequested = _buttonFocusCount > 0;
    }

    private int FindPresetIndex(GlobalHotkeyActivation? activation)
    {
        if (activation is not { } selected)
        {
            return -1;
        }

        for (int index = 0; index < _presets.Count; index++)
        {
            if (_presets[index].Activation.Equals(selected))
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

    private void RenderKeys()
    {
        KeysPanel.Children.Clear();

        if (_selectedActivation is { Kind: not HotkeyActivationKind.Chord } presetActivation)
        {
            KeysPanel.Children.Add(CreateKeyCap(new TextBlock
            {
                Text = GlobalHotkeyService.FormatActivation(presetActivation, _localization)
            }));
            return;
        }

        HotkeyModifierKeys modifiers = HotkeyModifierKeys.None;
        int primaryKey = 0;
        if (_selectedActivation is { Kind: HotkeyActivationKind.Chord } selected)
        {
            modifiers = selected.Gesture.Modifiers;
            primaryKey = selected.Gesture.VirtualKey;
        }
        else if (_session.HeldModifiers != HotkeyModifierKeys.None)
        {
            modifiers = _session.HeldModifiers;
        }

        if (modifiers == HotkeyModifierKeys.None && primaryKey <= 0)
        {
            KeysPanel.Children.Add(_emptyHint);
            return;
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Windows))
        {
            KeysPanel.Children.Add(CreateKeyCap(CreateWindowsKeyContent()));
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Control))
        {
            KeysPanel.Children.Add(CreateKeyCap(new TextBlock { Text = "Ctrl" }));
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Alt))
        {
            KeysPanel.Children.Add(CreateKeyCap(new TextBlock { Text = "Alt" }));
        }

        if (modifiers.HasFlag(HotkeyModifierKeys.Shift))
        {
            KeysPanel.Children.Add(CreateKeyCap(new TextBlock { Text = "Shift" }));
        }

        if (primaryKey > 0)
        {
            KeysPanel.Children.Add(CreateKeyCap(new TextBlock
            {
                Text = GlobalHotkeyService.FormatVirtualKey(primaryKey)
            }));
        }
    }

    private Border CreateKeyCap(FrameworkElement content)
    {
        if (content is TextBlock textBlock)
        {
            textBlock.FontSize = 13;
            textBlock.HorizontalAlignment = HorizontalAlignment.Center;
            textBlock.VerticalAlignment = VerticalAlignment.Center;
        }
        else if (content is IconElement icon)
        {
            icon.HorizontalAlignment = HorizontalAlignment.Center;
            icon.VerticalAlignment = VerticalAlignment.Center;
        }

        return new Border
        {
            Style = (Style)Resources["KeyCapStyle"],
            Child = content
        };
    }

    private static FrameworkElement CreateWindowsKeyContent()
    {
        if (XamlBindingHelper.ConvertValue(typeof(Geometry), WindowsLogoPathData)
                is Geometry geometry)
        {
            return new PathIcon
            {
                Data = geometry,
                Width = 13,
                Height = 13
            };
        }

        return new TextBlock { Text = "Win" };
    }

    private void Revalidate()
    {
        string? error = null;
        string? warning = null;
        bool canSave = false;

        if (_selectedActivation is { } activation)
        {
            if (activation.Kind == HotkeyActivationKind.Chord)
            {
                GlobalHotkeyGesture gesture = activation.Gesture;
                if (!GlobalHotkeyService.IsValidGesture(gesture))
                {
                    error = _localization.T("Settings.HotkeyRecorder.InvalidGesture");
                }
                else if (_scope == Scope.Search &&
                         gesture.Modifiers.HasFlag(HotkeyModifierKeys.Windows))
                {
                    // SearchHotkeyService registers through RegisterHotKey
                    // without MOD_WIN; the Win chords stay unavailable there.
                    error = _localization.T("Settings.HotkeyRecorder.WindowsUnsupported");
                }
                else if (_conflictChecker(gesture) is { } conflict)
                {
                    error = conflict;
                }
                else
                {
                    canSave = true;
                    warning = GetWarningText(activation);
                }
            }
            else
            {
                canSave = _scope == Scope.Global;
                if (!canSave)
                {
                    error = _localization.T("Settings.HotkeyRecorder.InvalidGesture");
                }
                else
                {
                    warning = GetWarningText(activation);
                }
            }
        }

        ErrorInfoBar.Message = error ?? string.Empty;
        ErrorInfoBar.IsOpen = error is not null;
        WarningInfoBar.Message = warning ?? string.Empty;
        WarningInfoBar.IsOpen = warning is not null;
        IsPrimaryButtonEnabled = canSave && !_isApplying;
    }

    private string? GetWarningText(GlobalHotkeyActivation activation)
    {
        if (activation.Kind == HotkeyActivationKind.WindowsTap)
        {
            return _localization.T("Settings.GlobalHotkey.WindowsTapWarning");
        }

        if (activation.Kind != HotkeyActivationKind.Chord)
        {
            return null;
        }

        if (GlobalHotkeyService.IsReservedSystemGesture(activation.Gesture))
        {
            return activation.Gesture.Modifiers == HotkeyModifierKeys.Alt
                ? _localization.T("Settings.GlobalHotkey.AltSpaceWarning")
                : _localization.T("Settings.GlobalHotkey.ReservedWarning");
        }

        return GlobalHotkeyService.IsRiskyGesture(activation.Gesture)
            ? _localization.T("Settings.HotkeyRecorder.RiskyWarning")
            : null;
    }

    private static bool IsSelectableActivation(GlobalHotkeyActivation activation)
    {
        return activation.Kind != HotkeyActivationKind.Chord ||
               activation.Gesture.VirtualKey > 0;
    }
}
