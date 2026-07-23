using CommunityToolkit.WinUI.Animations;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Windows.UI;
using WinRT.Interop;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow
{
    private void SetupStep4()
    {
        // Hotkey toggle
        Step4HotkeyToggle.Toggled -= Step4HotkeyToggle_Toggled;
        Step4HotkeyToggle.IsOn = _settingsService.Settings.GlobalHotkeyEnabled;
        Step4HotkeyToggle.Toggled += Step4HotkeyToggle_Toggled;

        // Search hotkey toggle
        Step4SearchHotkeyToggle.Toggled -= Step4SearchHotkeyToggle_Toggled;
        Step4SearchHotkeyToggle.IsOn = _settingsService.Settings.SearchHotkeyEnabled;
        Step4SearchHotkeyToggle.Toggled += Step4SearchHotkeyToggle_Toggled;
        RefreshSearchHotkeyText();

        // Startup toggle
        Step4StartupToggle.Toggled -= Step4StartupToggle_Toggled;
        Step4StartupToggle.IsOn = StartupService.IsEnabled();
        Step4StartupToggle.Toggled += Step4StartupToggle_Toggled;

        // Storage path & pin
        SetupStep4Storage();

        RefreshHotkeyChangeButton();
        Step4HotkeyChangeButton.IsEnabled = Step4HotkeyToggle.IsOn;

        if (Step4HotkeyToggle.IsOn && !_isAnimating)
        {
            StartKeycapPulse();
        }
    }

    private void RefreshHotkeyChangeButton()
    {
        if (_isRecordingHotkey)
        {
            return;
        }

        string hotkeyText = GlobalHotkeyService.FormatGesture(
            GlobalHotkeyService.NormalizeGesture(
                _settingsService.Settings.GlobalHotkeyModifiers,
                _settingsService.Settings.GlobalHotkeyKey),
            _localizationService);

        Step4KeycapText.Text = hotkeyText;
        Step4HotkeyChangeButton.Content = hotkeyText;
    }

    private void RefreshSearchHotkeyText()
    {
        string searchText = GlobalHotkeyService.FormatGesture(
            new GlobalHotkeyGesture(
                (HotkeyModifierKeys)_settingsService.Settings.SearchHotkeyModifiers,
                _settingsService.Settings.SearchHotkeyKey),
            _localizationService);
        Step4SearchHotkeyText.Text = searchText;
    }

    private void Step4HotkeyChange_Click(object sender, RoutedEventArgs e)
    {
        BeginHotkeyRecording();
    }

    private void BeginHotkeyRecording()
    {
        _isRecordingHotkey = true;
        Step4HotkeyChangeButton.Content = _localizationService.T("Onboarding.Step4.HotkeyRecording");
        Step4HotkeyChangeButton.Focus(FocusState.Programmatic);
    }

    private void EndHotkeyRecording()
    {
        _isRecordingHotkey = false;
        RefreshHotkeyChangeButton();
    }

    private async Task ApplyRecordedHotkeyAsync(GlobalHotkeyGesture gesture)
    {
        EndHotkeyRecording();
        if (App.Current.GlobalHotkeyService is not { } hotkeyService)
        {
            return;
        }

        if (!hotkeyService.TryApplyGesture(gesture, out string? error))
        {
            if (RootGrid.XamlRoot is not null)
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = _localizationService.T("Settings.GlobalHotkey.Dialog.FailedTitle"),
                    CloseButtonText = _localizationService.T("Common.Ok"),
                    DefaultButton = ContentDialogButton.Close,
                    Content = new TextBlock
                    {
                        Text = error ?? _localizationService.T("Settings.GlobalHotkey.Status.Unregistered"),
                        TextWrapping = TextWrapping.Wrap
                    }
                };
                await dialog.ShowAsync();
            }
        }

        RefreshHotkeyChangeButton();
    }

    private static HotkeyModifierKeys GetPressedHotkeyModifiers()
    {
        var modifiers = HotkeyModifierKeys.None;
        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Control))
        {
            modifiers |= HotkeyModifierKeys.Control;
        }
        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Menu))
        {
            modifiers |= HotkeyModifierKeys.Alt;
        }
        if (Win32Helper.IsKeyPressed(Windows.System.VirtualKey.Shift))
        {
            modifiers |= HotkeyModifierKeys.Shift;
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
            Windows.System.VirtualKey.RightShift;
    }

    private void OnHotkeyKeyDown(Windows.System.VirtualKey key)
    {
        if (!_isRecordingHotkey)
        {
            return;
        }

        if (key == Windows.System.VirtualKey.Escape)
        {
            EndHotkeyRecording();
            return;
        }

        if (IsModifierKey(key))
        {
            return;
        }

        var gesture = new GlobalHotkeyGesture(
            GetPressedHotkeyModifiers(),
            (int)key);
        _ = ApplyRecordedHotkeyAsync(gesture);
    }

    private void Step4HotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        if (App.Current.GlobalHotkeyService is { } globalHotkeyService)
        {
            globalHotkeyService.SetEnabled(toggle.IsOn);
        }
        else
        {
            _settingsService.Settings.GlobalHotkeyEnabled = toggle.IsOn;
            _settingsService.SaveDebounced();
        }
        Step4HotkeyChangeButton.IsEnabled = toggle.IsOn;

        if (toggle.IsOn)
        {
            StartKeycapPulse();
        }
        else
        {
            _keycapPulseStoryboard?.Stop();
            _keycapPulseStoryboard = null;
            SetElementTransform(Step4Keycap);
        }
    }

    private void Step4SearchHotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        _settingsService.Settings.SearchHotkeyEnabled = toggle.IsOn;
        _settingsService.SaveDebounced();
    }

    private void Step4StartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle)
        {
            return;
        }

        StartupService.SetEnabled(toggle.IsOn);
        _settingsService.Settings.AutoStart = toggle.IsOn;
        _settingsService.SaveDebounced();
    }

    private void StartKeycapPulse()
    {
        _keycapPulseStoryboard?.Stop();

        var transform = GetElementTransform(Step4Keycap);
        var storyboard = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever,
            AutoReverse = true
        };

        var scaleUpX = new DoubleAnimation
        {
            From = 1,
            To = 1.05,
            Duration = new Duration(TimeSpan.FromMilliseconds(750)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(scaleUpX, transform);
        Storyboard.SetTargetProperty(scaleUpX, "ScaleX");
        storyboard.Children.Add(scaleUpX);

        var scaleUpY = new DoubleAnimation
        {
            From = 1,
            To = 1.05,
            Duration = new Duration(TimeSpan.FromMilliseconds(750)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(scaleUpY, transform);
        Storyboard.SetTargetProperty(scaleUpY, "ScaleY");
        storyboard.Children.Add(scaleUpY);

        _keycapPulseStoryboard = storyboard;
        storyboard.Begin();
    }

    // ════════════════════════════════════════════════════════════
    //  Step 5: Ready Summary
    // ════════════════════════════════════════════════════════════
}
