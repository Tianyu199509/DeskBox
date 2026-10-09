using DeskBox.Contracts;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using DeskBox.Views.Dialogs;
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
using Microsoft.Windows.Storage.Pickers;
using Windows.System;

namespace DeskBox.Views;

public sealed partial class SettingsWindow
{
    private void WeatherCitySearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        // Suppress search when a city is being selected (SuggestionChosen → TextChanged → QuerySubmitted chain)
        if (_isSelectingCity || args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        // The search state machine (debounce, cancellation, the search
        // service) stays on the shell; it pushes the results into the
        // section editor (batch 48).
        _ = ViewModel.UpdateWeatherCitySuggestionsAsync(sender.Text);
    }

    private void WeatherCitySearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is WeatherCitySearchResult result)
        {
            _isSelectingCity = true;
            _weatherSettingsViewModel.SelectCity(result);
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
        if (!string.IsNullOrWhiteSpace(args.QueryText) && _weatherSettingsViewModel.HasCitySuggestions)
        {
            _isSelectingCity = true;
            _weatherSettingsViewModel.TrySelectFirstCitySuggestion();
            DispatcherQueue.TryEnqueue(() => _isSelectingCity = false);
        }
    }

    private void WeatherCitySearchBox_LostFocus(object sender, RoutedEventArgs e)
    {
        // Clear search results and restore the saved city name if the user didn't select anything.
        DispatcherQueue.TryEnqueue(() =>
        {
            _weatherSettingsViewModel.ClearCitySuggestions();
            _weatherSettingsViewModel.RestoreCitySearchText();
        });
    }

    private async void ChangeGlobalHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null ||
            App.Current.GlobalHotkeyService is not { } hotkeyService)
        {
            return;
        }

        GlobalHotkeyActivation current = hotkeyService.CurrentActivation;
        HotkeyRecorderDialog.Preset[] presets =
        [
            new(
                _localizationService.T("Settings.GlobalHotkey.Preset.F7"),
                GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                    HotkeyModifierKeys.None,
                    (int)VirtualKey.F7))),
            new(
                _localizationService.T("Settings.GlobalHotkey.Preset.DoubleControl"),
                new GlobalHotkeyActivation(
                    HotkeyActivationKind.DoubleControl,
                    current.Gesture)),
            new(
                _localizationService.T("Settings.GlobalHotkey.Preset.AltSpace"),
                GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                    HotkeyModifierKeys.Alt,
                    (int)VirtualKey.Space))),
            new(
                _localizationService.T("Settings.GlobalHotkey.Preset.WinSpace"),
                GlobalHotkeyActivation.FromChord(new GlobalHotkeyGesture(
                    HotkeyModifierKeys.Windows,
                    (int)VirtualKey.Space))),
            new(
                _localizationService.T("Settings.GlobalHotkey.Preset.WindowsTap"),
                new GlobalHotkeyActivation(
                    HotkeyActivationKind.WindowsTap,
                    current.Gesture)),
            new(
                _localizationService.T("Settings.GlobalHotkey.Preset.CopilotKey"),
                GlobalHotkeyActivation.FromChord(GlobalHotkeyService.CopilotKeyGesture),
                // The chord is the hardware Copilot key's gesture — without
                // that key the preset registers fine but can never fire.
                _localizationService.T("Settings.GlobalHotkey.Preset.CopilotKey.Tooltip")),
        ];

        var dialog = new HotkeyRecorderDialog(
            SettingsRoot.XamlRoot,
            _localizationService,
            HotkeyRecorderDialog.Scope.Global,
            current,
            presets,
            ApplyGlobalHotkeyActivationFromRecorderAsync,
            GetGlobalHotkeyRecorderConflict);
        await dialog.ShowAsync();
    }

    private Task<string?> ApplyGlobalHotkeyActivationFromRecorderAsync(
        GlobalHotkeyActivation activation)
    {
        if (App.Current.GlobalHotkeyService is not { } hotkeyService)
        {
            return Task.FromResult<string?>(
                _localizationService.T("Settings.GlobalHotkey.Status.Unavailable"));
        }

        string? error = hotkeyService.TryApplyActivation(activation, out string? applyError)
            ? null
            : applyError ?? _localizationService.T(
                "Settings.GlobalHotkey.Status.Unregistered");
        if (error is null)
        {
            ViewModel.RefreshGlobalHotkeyState();
            RefreshGlobalHotkeyControls();
        }

        return Task.FromResult(error);
    }

    private string? GetGlobalHotkeyRecorderConflict(GlobalHotkeyGesture gesture)
    {
        return GlobalHotkeyService.IsGestureOwnedBySearchHotkey(gesture)
            ? _localizationService.T("Settings.GlobalHotkey.Status.SearchHotkeyConflict")
            : null;
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

    // ── System visual-effect deep links (material section) ────────
    // Both cards mirror Windows-wide switches read-only: an in-app write
    // would land in the MSIX copy-on-write private hive and silently revert
    // after the next sign-in, so the buttons open the OS surfaces instead.
    // The appearance editor owns the status projections (it is the section's
    // DataContext); the Win32 probes stay here on the shell and push in.

    public void RefreshSystemAppearanceStates()
    {
        bool? shadowOn = Win32Helper.TryGetWindowDropShadowEnabled(out bool shadowsEnabled)
            ? shadowsEnabled
            : null;
        bool? transparencyOn =
            Win32Helper.TryGetSystemTransparencyEffectsEnabled(out bool transparencyEnabled)
                ? transparencyEnabled
                : null;
        _appearanceSettingsViewModel.UpdateSystemEffectStates(shadowOn, transparencyOn);
    }

    private async void OpenSystemTransparencySettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:colors")))
            {
                App.Log("[Settings] Windows color settings could not be opened.");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Settings] Failed to open Windows color settings: {ex.Message}");
        }
    }

    private void OpenWindowShadowSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using System.Diagnostics.Process? process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "SystemPropertiesPerformance.exe",
                    UseShellExecute = true
                });
            if (process is null)
            {
                App.Log("[Settings] Windows performance options could not be opened.");
            }
        }
        catch (Exception ex)
        {
            App.Log($"[Settings] Failed to open Windows performance options: {ex.Message}");
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

    private void RefreshGlobalHotkeyControls()
    {
        if (GlobalHotkeyCaptureButton is null)
        {
            return;
        }

        _isRefreshingHotkeyControls = true;
        try
        {
            GlobalHotkeyCaptureButton.Content = _interactionSettingsViewModel.HotkeyText;
            DesktopDoubleClickToggle.IsOn =
                _settingsService.Settings.DesktopDoubleClickEnabled;
        }
        finally
        {
            _isRefreshingHotkeyControls = false;
        }
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

    private async void ChooseWidgetBackgroundImage_Click(object sender, RoutedEventArgs e)
    {
        bool panorama = _appearanceSettingsViewModel.WidgetBackgroundMode ==
            WidgetBackgroundModeKinds.Panorama;
        string? pickedPath = await PickWidgetBackgroundImageFromSettingsAsync();
        if (pickedPath is null)
        {
            return;
        }

        WidgetTitleIconAssetResult result = panorama
            ? await WidgetTitleIconAssetStore.Current.CopyPanoramaBackgroundImageAsync(pickedPath)
            : await WidgetTitleIconAssetStore.Current.CopyUnifiedBackgroundImageAsync(pickedPath);
        if (result != WidgetTitleIconAssetResult.Copied)
        {
            string feedbackKey = result switch
            {
                WidgetTitleIconAssetResult.TooLarge => "Widget.CustomIcon.TooLarge",
                WidgetTitleIconAssetResult.UnsupportedFormat => "Widget.CustomIcon.UnsupportedFormat",
                _ => "Widget.CustomIcon.ImageFailed"
            };
            App.Log(
                $"[Settings] Global background pick rejected ({result}): {pickedPath}");
            await ShowInfoDialogAsync(
                _localizationService.T("Widget.CustomBackground.Title"),
                _localizationService.T(feedbackKey));
            return;
        }

        string storedFileName = panorama
            ? WidgetTitleIconAssetStore.GetStoredPanoramaFileName(pickedPath)
            : WidgetTitleIconAssetStore.GetStoredBackgroundFileName(pickedPath);
        if (panorama)
        {
            _appearanceSettingsViewModel.SetPanoramaBackgroundImage(storedFileName);
        }
        else
        {
            _appearanceSettingsViewModel.SetUnifiedBackgroundImage(storedFileName);
        }
    }

    private async Task<string?> PickWidgetBackgroundImageFromSettingsAsync()
    {
        try
        {
            return await FileOpenPickerService.PickSingleFileAsync(
                _hWnd,
                [".png", ".jpg", ".jpeg", ".bmp", ".ico", ".svg"],
                PickerLocationId.PicturesLibrary);
        }
        catch (Exception ex)
        {
            App.Log($"[Settings] Global background picker failed: {ex.Message}");
            return null;
        }
    }

    private void ClearPerWidgetBackgroundsButton_Click(object sender, RoutedEventArgs e)
    {
        _appearanceSettingsViewModel.ClearPerWidgetBackgrounds();
    }
}
