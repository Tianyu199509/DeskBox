using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Shapes;
using System.Runtime.InteropServices;
using Windows.System;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private void EditableSettingsTextBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.Tag = textBox.Text;
        }
    }

    private void EditableSettingsTextBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || sender is not TextBox textBox)
        {
            return;
        }

        if (textBox.Tag is string originalText)
        {
            textBox.Text = originalText;
        }

        SettingsRoot.Focus(FocusState.Programmatic);
        e.Handled = true;
    }

    private void WeatherCitySearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Suppress search when a city is being selected (SuggestionChosen → TextChanged → QuerySubmitted chain)
        if (_isSelectingCity || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        _ = ViewModel.UpdateWeatherCitySuggestionsAsync(sender.Text);
    }

    private void WeatherCitySearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is WeatherCitySearchResult result)
        {
            _isSelectingCity = true;
            ViewModel.SelectWeatherCity(result);
            // Reset on next dispatch cycle, after TextChanged and QuerySubmitted have fired
            DispatcherQueue.TryEnqueue(() => _isSelectingCity = false);
        }
    }

    private void WeatherCitySearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        // If a suggestion was chosen, SuggestionChosen already handled it
        if (args.ChosenSuggestion is not null)
        {
            return;
        }

        // User pressed Enter without selecting a suggestion — pick the first match
        if (!string.IsNullOrWhiteSpace(args.QueryText) && ViewModel.WeatherCitySuggestions.Count > 0)
        {
            _isSelectingCity = true;
            ViewModel.SelectWeatherCity(ViewModel.WeatherCitySuggestions[0]);
            DispatcherQueue.TryEnqueue(() => _isSelectingCity = false);
        }
    }

    private void WeatherCitySearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Clear search results and restore the saved city name if the user didn't select anything.
        DispatcherQueue.TryEnqueue(() =>
        {
            ViewModel.ClearWeatherCitySuggestions();
            ViewModel.RestoreWeatherCitySearchText();
        });
    }

    private void ChangeGlobalHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        BeginHotkeyRecording();
    }

    private async void GlobalHotkeyPresetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingHotkeyControls ||
            sender is not ToggleButton { Tag: string preset })
        {
            return;
        }

        GlobalHotkeyGesture fallbackGesture =
            App.Current.GlobalHotkeyService?.CurrentGesture ??
            GlobalHotkeyService.NormalizeGesture(
                _settingsService.Settings.GlobalHotkeyModifiers,
                _settingsService.Settings.GlobalHotkeyKey);
        GlobalHotkeyActivation activation = preset switch
        {
            "F7" => GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                HotkeyModifierKeys.None,
                (int)VirtualKey.F7)),
            "DoubleControl" => new GlobalHotkeyActivation(
                HotkeyActivationKind.DoubleControl,
                fallbackGesture),
            "AltSpace" => GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                HotkeyModifierKeys.Alt,
                (int)VirtualKey.Space)),
            "WinSpace" => GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                HotkeyModifierKeys.Windows,
                (int)VirtualKey.Space)),
            "WindowsTap" => new GlobalHotkeyActivation(
                HotkeyActivationKind.WindowsTap,
                fallbackGesture),
            "CopilotKey" => GlobalHotkeyActivation.FromChord(GlobalHotkeyService.CopilotKeyGesture),
            _ => default
        };
        if (!GlobalHotkeyService.IsValidActivation(activation))
        {
            RefreshGlobalHotkeyControls();
            return;
        }

        await ApplyGlobalHotkeyActivationAsync(activation);
    }

    private async void DesktopDoubleClickToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingHotkeyControls || sender is not ToggleSwitch toggle)
        {
            return;
        }

        bool applied;
        int errorCode = 0;
        if (App.Current.DesktopDoubleClickActivationService is { } service)
        {
            applied = service.TrySetEnabled(toggle.IsOn, out errorCode);
        }
        else
        {
            _settingsService.Settings.DesktopDoubleClickEnabled = toggle.IsOn;
            _settingsService.SaveDebounced();
            applied = true;
        }

        if (!applied)
        {
            App.Log($"[DesktopDoubleClick] Settings toggle failed error={errorCode}");
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.GlobalHotkey.Dialog.FailedTitle"),
                _localizationService.T("Settings.DesktopDoubleClick.Status.Unavailable"));
        }

        RefreshGlobalHotkeyControls();
    }

    private void GlobalHotkeyCaptureButton_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_isRecordingHotkey)
        {
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            EndHotkeyRecording();
            e.Handled = true;
            return;
        }

        // Win+Space capture injects an unassigned key so Windows treats the
        // Windows key as part of a chord instead of opening Start. It is an
        // implementation detail, not the shortcut the user is recording.
        if (ReservedHotkeyHookService.IsInternalMaskKey((int)e.Key))
        {
            e.Handled = true;
            return;
        }

        if (IsModifierKey(e.Key))
        {
            e.Handled = true;
            return;
        }

        var gesture = new DeskBox.Models.GlobalHotkeyGesture(
            GetPressedHotkeyModifiers(),
            (int)e.Key);
        _ = ApplyRecordedHotkeyAsync(gesture);
        e.Handled = true;
    }

    private void GlobalHotkeyCaptureButton_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_isRecordingHotkey)
        {
            EndHotkeyRecording();
        }
    }

    private async void ResetGlobalHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.GlobalHotkeyService is not { } hotkeyService)
        {
            return;
        }

        if (!hotkeyService.ResetToDefault(out string? error))
        {
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.GlobalHotkey.Dialog.FailedTitle"),
                error ?? _localizationService.T("Settings.GlobalHotkey.Status.Unregistered"));
        }

        ViewModel.RefreshGlobalHotkeyState();
        RefreshGlobalHotkeyControls();
    }

    private void AppearanceSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Slider)
        {
            return;
        }

        BeginAppearanceSliderDrag();
    }

    private void AppearanceSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        CommitAppearanceSliderDrag();
    }

    private void AppearanceSlider_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitAppearanceSliderDrag();
    }

    private void AppearanceSlider_ManipulationStarted(object sender, ManipulationStartedRoutedEventArgs e)
    {
        BeginAppearanceSliderDrag();
    }

    private void AppearanceSlider_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
    {
        CommitAppearanceSliderDrag();
    }

    private void AppearanceSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (sender is Slider slider && slider.FocusState == FocusState.Pointer)
        {
            BeginAppearanceSliderDrag();
            KeepSliderThumbExpanded(slider);
        }
    }

    private void SettingsRoot_PointerPressedHandled(object sender, PointerRoutedEventArgs e)
    {
        if (TryFindAncestor<Slider>(e.OriginalSource as DependencyObject, out var slider))
        {
            BeginAppearanceSliderDrag();
            _pressedAppearanceSliders.Add(slider);
            KeepSliderThumbExpanded(slider);
        }
    }

    private void SettingsRoot_PointerReleasedHandled(object sender, PointerRoutedEventArgs e)
    {
        CommitAppearanceSliderDrag();
    }

    private void BeginAppearanceSliderDrag()
    {
        _isAppearanceSliderDragging = true;
        ViewModel.SuppressAppearanceNotifications = true;
        ViewModel.DeferAppearancePersistence = true;
    }

    private void CommitAppearanceSliderDrag()
    {
        if (!_isAppearanceSliderDragging)
        {
            return;
        }

        _isAppearanceSliderDragging = false;
        ViewModel.DeferAppearancePersistence = false;
        ViewModel.SuppressAppearanceNotifications = false;
        ResetPressedAppearanceSliders();
        ViewModel.CommitAppearanceChanges();
    }

    private void KeepSliderThumbExpanded(Slider slider)
    {
        foreach (var ellipse in FindDescendants<Ellipse>(slider))
        {
            if (ellipse.Name != "SliderInnerThumb")
            {
                continue;
            }

            if (ellipse.RenderTransform is CompositeTransform transform)
            {
                transform.ScaleX = 1.167;
                transform.ScaleY = 1.167;
            }
        }
    }

    private void ResetPressedAppearanceSliders()
    {
        foreach (var slider in _pressedAppearanceSliders.ToList())
        {
            foreach (var ellipse in FindDescendants<Ellipse>(slider))
            {
                if (ellipse.Name != "SliderInnerThumb")
                {
                    continue;
                }

                if (ellipse.RenderTransform is CompositeTransform transform)
                {
                    transform.ScaleX = 1.0;
                    transform.ScaleY = 1.0;
                }
            }
        }

        _pressedAppearanceSliders.Clear();
    }

    private void BeginHotkeyRecording()
    {
        if (_isRecordingHotkey)
        {
            return;
        }

        _isRecordingHotkey = true;
        int captureError = 0;
        if (!_isSubclassInstalled ||
            !_hotkeyRecordingHook.TryStart(
                _hWnd,
                WmReservedHotkeyCapture,
                out captureError))
        {
            App.Log(
                $"[GlobalHotkey] Recording hook unavailable; ordinary gestures remain available " +
                $"error={captureError}");
        }

        GlobalHotkeyCaptureButton.Content = _localizationService.T("Settings.GlobalHotkey.Recording");
        GlobalHotkeyCaptureButton.Focus(FocusState.Programmatic);
    }

    private void EndHotkeyRecording()
    {
        _isRecordingHotkey = false;
        _hotkeyRecordingHook.Stop();
        RefreshGlobalHotkeyControls();
    }

    private void RefreshGlobalHotkeyControls()
    {
        if (GlobalHotkeyCaptureButton is null)
        {
            return;
        }

        GlobalHotkeyActivation activation =
            App.Current.GlobalHotkeyService?.CurrentActivation ??
            ViewModel.GetCurrentGlobalHotkeyActivation();
        _isRefreshingHotkeyControls = true;
        try
        {
            if (!_isRecordingHotkey)
            {
                GlobalHotkeyCaptureButton.Content = ViewModel.GlobalHotkeyText;
            }

            GlobalHotkeyPresetF7Button.IsChecked =
                activation.Kind == HotkeyActivationKind.Chord &&
                activation.Gesture.Modifiers == HotkeyModifierKeys.None &&
                activation.Gesture.VirtualKey == (int)VirtualKey.F7;
            GlobalHotkeyPresetDoubleControlButton.IsChecked =
                activation.Kind == HotkeyActivationKind.DoubleControl;
            GlobalHotkeyPresetAltSpaceButton.IsChecked =
                activation.Kind == HotkeyActivationKind.Chord &&
                activation.Gesture.Modifiers == HotkeyModifierKeys.Alt &&
                activation.Gesture.VirtualKey == (int)VirtualKey.Space;
            GlobalHotkeyPresetWinSpaceButton.IsChecked =
                activation.Kind == HotkeyActivationKind.Chord &&
                activation.Gesture.Modifiers == HotkeyModifierKeys.Windows &&
                activation.Gesture.VirtualKey == (int)VirtualKey.Space;
            GlobalHotkeyPresetWindowsTapButton.IsChecked =
                activation.Kind == HotkeyActivationKind.WindowsTap;
            GlobalHotkeyPresetCopilotKeyButton.IsChecked =
                activation.Kind == HotkeyActivationKind.Chord &&
                activation.Gesture.Equals(GlobalHotkeyService.CopilotKeyGesture);
            DesktopDoubleClickToggle.IsOn =
                _settingsService.Settings.DesktopDoubleClickEnabled;
        }
        finally
        {
            _isRefreshingHotkeyControls = false;
        }
    }

    private async Task ApplyRecordedHotkeyAsync(DeskBox.Models.GlobalHotkeyGesture gesture)
    {
        await ApplyGlobalHotkeyActivationAsync(
            GlobalHotkeyActivation.FromChord(gesture));
    }

    private async Task ApplyGlobalHotkeyActivationAsync(GlobalHotkeyActivation activation)
    {
        EndHotkeyRecording();
        if (App.Current.GlobalHotkeyService is not { } hotkeyService)
        {
            RefreshGlobalHotkeyControls();
            return;
        }

        bool requiresConfirmation =
            activation.Kind == HotkeyActivationKind.WindowsTap ||
            (activation.Kind == HotkeyActivationKind.Chord &&
             GlobalHotkeyService.IsReservedSystemGesture(activation.Gesture));
        if (requiresConfirmation &&
            !activation.Equals(hotkeyService.CurrentActivation) &&
            !await ConfirmReservedHotkeyOverrideAsync(activation))
        {
            ViewModel.RefreshGlobalHotkeyState();
            RefreshGlobalHotkeyControls();
            return;
        }

        if (!hotkeyService.TryApplyActivation(activation, out string? error))
        {
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.GlobalHotkey.Dialog.FailedTitle"),
                error ?? _localizationService.T("Settings.GlobalHotkey.Status.Unregistered"));
        }

        ViewModel.RefreshGlobalHotkeyState();
        RefreshGlobalHotkeyControls();
    }

    private async Task<bool> ConfirmReservedHotkeyOverrideAsync(
        GlobalHotkeyActivation activation)
    {
        if (SettingsRoot.XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = SettingsRoot.XamlRoot,
            Title = GlobalHotkeyService.FormatActivation(
                activation,
                _localizationService),
            PrimaryButtonText = _localizationService.T("Common.Enable"),
            CloseButtonText = _localizationService.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = GetReservedHotkeyWarning(activation),
                TextWrapping = TextWrapping.Wrap
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private string GetReservedHotkeyWarning(GlobalHotkeyActivation activation)
    {
        if (activation.Kind == HotkeyActivationKind.WindowsTap)
        {
            return _localizationService.T("Settings.GlobalHotkey.WindowsTapWarning");
        }

        if (activation.Gesture.Modifiers == HotkeyModifierKeys.Alt)
        {
            return _localizationService.T("Settings.GlobalHotkey.AltSpaceWarning");
        }

        return _localizationService.T("Settings.GlobalHotkey.ReservedWarning");
    }

    private DeskBox.Models.HotkeyModifierKeys GetPressedHotkeyModifiers()
    {
        var modifiers = DeskBox.Models.HotkeyModifierKeys.None;
        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            modifiers |= DeskBox.Models.HotkeyModifierKeys.Control;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu))
        {
            modifiers |= DeskBox.Models.HotkeyModifierKeys.Alt;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            modifiers |= DeskBox.Models.HotkeyModifierKeys.Shift;
        }

        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.LeftWindows) ||
            Win32Helper.IsKeyPressed(Windows.System.VirtualKey.RightWindows))
        {
            modifiers |= DeskBox.Models.HotkeyModifierKeys.Windows;
        }

        return modifiers;
    }

    private static bool IsModifierKey(Windows.System.VirtualKey key)
    {
        return key is
            Windows.System.VirtualKey.Control or
            Windows.System.VirtualKey.LeftControl or
            Windows.System.VirtualKey.RightControl or
            Windows.System.VirtualKey.Menu or
            Windows.System.VirtualKey.LeftMenu or
            Windows.System.VirtualKey.RightMenu or
            Windows.System.VirtualKey.Shift or
            Windows.System.VirtualKey.LeftShift or
            Windows.System.VirtualKey.RightShift or
            Windows.System.VirtualKey.LeftWindows or
            Windows.System.VirtualKey.RightWindows;
    }

    private static bool TryFindAncestor<T>(DependencyObject? source, out T result) where T : DependencyObject
    {
        var current = source;
        while (current is not null)
        {
            if (current is T typed)
            {
                result = typed;
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        result = null!;
        return false;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject parent) where T : DependencyObject
    {
        int childCount = VisualTreeHelper.GetChildrenCount(parent);
        for (int index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T typedChild)
            {
                yield return typedChild;
            }

            foreach (var nestedChild in FindDescendants<T>(child))
            {
                yield return nestedChild;
            }
        }
    }

}
