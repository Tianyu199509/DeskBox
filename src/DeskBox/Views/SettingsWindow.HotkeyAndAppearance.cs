using DeskBox.Contracts;
using DeskBox.Controls;
using Microsoft.UI.Xaml.Automation;
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

        if (DeskBox.Helpers.ImeCompositionGuard.IsComposingOrWithinGrace())
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
        if (sender is Control control &&
            (DeskBox.Helpers.TransientInputFocusGuard.HasOpenPopupsInXamlRoot(control.XamlRoot) ||
             DeskBox.Helpers.TransientInputFocusGuard.IsWindowsTextInputExperienceWindow(Win32Helper.GetForegroundWindow())))
        {
            return;
        }

        // Clear search results and restore the saved city name if the user didn't select anything.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (sender is Control liveControl &&
                (DeskBox.Helpers.TransientInputFocusGuard.HasOpenPopupsInXamlRoot(liveControl.XamlRoot) ||
                 DeskBox.Helpers.TransientInputFocusGuard.IsWindowsTextInputExperienceWindow(Win32Helper.GetForegroundWindow())))
            {
                return;
            }

            _weatherSettingsViewModel.ClearCitySuggestions();
            _weatherSettingsViewModel.RestoreCitySearchText();
        });
    }

    private async void ChangeGlobalHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        if (SettingsRoot.XamlRoot is null ||
            _hotkeyActions.GetRuntimeState() is not { } hotkeyState)
        {
            return;
        }

        GlobalHotkeyActivation current = hotkeyState.CurrentActivation;
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
                _localizationService.T("Settings.GlobalHotkey.Preset.DoubleShift"),
                GlobalHotkeyActivation.FromDoubleTapModifier(HotkeyModifierKeys.Shift)),
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
            _hotkeyActions,
            HotkeyRecorderDialog.Scope.Global,
            current,
            presets,
            ApplyGlobalHotkeyActivationFromRecorderAsync,
            GetGlobalHotkeyRecorderConflict);
        await dialog.ShowAsync();
    }

    private async Task<string?> ApplyGlobalHotkeyActivationFromRecorderAsync(
        GlobalHotkeyActivation activation)
    {
        if (_hotkeyActions.GetRuntimeState() is null)
        {
            return _localizationService.T("Settings.GlobalHotkey.Status.Unavailable");
        }

        (bool applied, string? applyError) = await _hotkeyActions.TryApplyActivationAsync(activation);
        string? error = applied
            ? null
            : applyError ?? _localizationService.T(
                "Settings.GlobalHotkey.Status.Unregistered");
        if (error is null)
        {
            ViewModel.RefreshGlobalHotkeyState();
            RefreshGlobalHotkeyControls();
        }

        return error;
    }

    private string? GetGlobalHotkeyRecorderConflict(GlobalHotkeyActivation activation)
    {
        // Deep review R13-3 (R14 hot-path pass): this check runs on every
        // captured keystroke, so it must not take a pipe round trip (the
        // R10/R11 modal-loop wedge family). The predicate is exactly the
        // service's: the search hotkey's current activation normalized from
        // the same settings fields both processes mirror.
        var settings = _settingsService.Settings;
        if (FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Clipboard) &&
            settings.Clipboard.ClipboardHotkeyEnabled)
        {
            GlobalHotkeyActivation clipboardActivation = GlobalHotkeyService.NormalizeActivation(
                settings.Clipboard.ClipboardHotkeyActivationKind,
                settings.Clipboard.ClipboardHotkeyModifiers,
                settings.Clipboard.ClipboardHotkeyKey);
            if (GlobalHotkeyService.DoActivationsConflict(activation, clipboardActivation))
            {
                return _localizationService.T("Settings.Clipboard.Hotkey.Status.ClipboardHotkeyConflict");
            }
        }
        var search = settings.Search;
        if (!search.SearchHotkeyEnabled)
        {
            return null;
        }

        GlobalHotkeyActivation searchActivation = GlobalHotkeyService.NormalizeActivation(
            search.SearchHotkeyActivationKind,
            search.SearchHotkeyModifiers,
            search.SearchHotkeyKey);
        return GlobalHotkeyService.DoActivationsConflict(activation, searchActivation)
            ? _localizationService.T("Settings.GlobalHotkey.Status.SearchHotkeyConflict")
            : null;
    }

    private async void DesktopDoubleClickToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingHotkeyControls || sender is not ToggleSwitch toggle)
        {
            return;
        }

        DesktopDoubleClickToggleResult result =
            _hotkeyActions.TrySetDesktopDoubleClickEnabled(toggle.IsOn);

        if (!result.Applied)
        {
            App.Log($"[DesktopDoubleClick] Settings toggle failed error={result.ErrorCode}");
            await ShowInfoDialogAsync(
                _localizationService.T("Settings.GlobalHotkey.Dialog.FailedTitle"),
                _localizationService.T("Settings.DesktopDoubleClick.Status.Unavailable"));
        }

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

    // ── System visual-effect deep link (material section) ─────────
    // Mirrors the Windows-wide transparency switch read-only: an in-app write
    // would land in the MSIX copy-on-write private hive and silently revert
    // after the next sign-in, so the button opens the OS surface instead.
    // The appearance editor owns the status projection (it is the section's
    // DataContext); the Win32 probe stays here on the shell and pushes in.

    public void RefreshSystemAppearanceStates()
    {
        bool? transparencyOn =
            Win32Helper.TryGetSystemTransparencyEffectsEnabled(out bool transparencyEnabled)
                ? transparencyEnabled
                : null;
        _appearanceSettingsViewModel.UpdateSystemEffectStates(transparencyOn);
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

    private void SettingsRoot_PointerPressedHandled(object sender, PointerRoutedEventArgs e)
    {
        if (!e.Handled &&
            e.GetCurrentPoint(SettingsRoot).Properties.PointerUpdateKind == Microsoft.UI.Input.PointerUpdateKind.XButton1Pressed &&
            !HasOpenOverlayPopup() &&
            TryNavigateBack())
        {
            e.Handled = true;
            return;
        }

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
            GlobalHotkeyActivation currentActivation = ViewModel.GetCurrentGlobalHotkeyActivation();
            KeyVisualState visualState = _settingsService.Settings.Core.GlobalHotkeyEnabled &&
                _hotkeyActions.GetRuntimeState() is { IsRegistered: false }
                ? KeyVisualState.Warning
                : KeyVisualState.Normal;
            GlobalHotkeyCaptureButton.Content = KeyVisualControl.CreateInlineButtonContent(
                currentActivation,
                _interactionSettingsViewModel.HotkeyText,
                showEditIcon: true,
                state: visualState,
                doubleTapBadgeText: _localizationService.T("Settings.HotkeyRecorder.DoubleTapBadge"));
            AutomationProperties.SetName(GlobalHotkeyCaptureButton, _interactionSettingsViewModel.HotkeyText);
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
        if (sender is not FrameworkElement anchor)
        {
            _appearanceSettingsViewModel.ClearPerWidgetBackgrounds();
            return;
        }

        var flyout = new Flyout
        {
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight
        };

        var promptText = new TextBlock
        {
            MaxWidth = 280,
            Text = _localizationService.T("Settings.WidgetBackground.PerWidget.ConfirmPrompt"),
            TextWrapping = TextWrapping.Wrap
        };

        var cancelButton = new Button
        {
            MinHeight = 32,
            Content = _localizationService.T("Common.Cancel")
        };
        if (SettingsRoot.Resources.TryGetValue("ActionButtonStyle", out object? actionStyleObj) &&
            actionStyleObj is Style actionStyle)
        {
            cancelButton.Style = actionStyle;
        }
        cancelButton.Click += (_, _) => flyout.Hide();

        var confirmButton = new Button
        {
            MinHeight = 32,
            Content = _localizationService.T("Settings.WidgetBackground.PerWidget.Clear")
        };
        if (SettingsRoot.Resources.TryGetValue("DangerActionButtonStyle", out object? dangerStyleObj) &&
            dangerStyleObj is Style dangerStyle)
        {
            confirmButton.Style = dangerStyle;
        }
        confirmButton.Click += (_, _) =>
        {
            flyout.Hide();
            _appearanceSettingsViewModel.ClearPerWidgetBackgrounds();
        };

        var buttonRow = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right,
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        buttonRow.Children.Add(cancelButton);
        buttonRow.Children.Add(confirmButton);

        var contentPanel = new StackPanel
        {
            MinWidth = 220,
            MaxWidth = 300,
            Spacing = 12,
            RequestedTheme = anchor.ActualTheme
        };
        contentPanel.Children.Add(promptText);
        contentPanel.Children.Add(buttonRow);

        flyout.Content = contentPanel;
        flyout.ShowAt(anchor);
    }

    private void AppearanceSection_ChooseBackgroundImageRequested(object? sender, RoutedEventArgs e) =>
        ChooseWidgetBackgroundImage_Click(sender ?? this, e);

    private void AppearanceSection_ClearPerWidgetBackgroundsRequested(object? sender, RoutedEventArgs e) =>
        ClearPerWidgetBackgroundsButton_Click(sender ?? this, e);

    private void AppearanceSection_OpenSystemTransparencyRequested(object? sender, RoutedEventArgs e) =>
        OpenSystemTransparencySettingsButton_Click(sender ?? this, e);

    private void AppearanceSection_SliderDragStarted(object? sender, EventArgs e) =>
        BeginAppearanceSliderDrag();

    private void AppearanceSection_SliderDragCompleted(object? sender, EventArgs e) =>
        CommitAppearanceSliderDrag();

    private void RefreshDisplaysEntrySummary()
    {
        if (DisplaysEntryCard is not { } card)
        {
            return;
        }

        IReadOnlyList<WidgetScreenInfo> screens = WidgetScreenCatalog.Capture();
        WidgetScreenInfo? primary = screens.FirstOrDefault(s => s.IsPrimary) ?? screens.FirstOrDefault();
        card.Description = Views.SettingsSections.DisplaySettingsSection.BuildEntrySummary(
            _localizationService,
            screens.Count,
            primary?.DisplayName);
    }

    private void RefreshAiSettingsEntrySummary()
    {
        if (AiSettingsEntryCard is { } card)
        {
            card.Description = Views.SettingsSections.AiSettingsSection.BuildEntrySummary(_localizationService);
        }
    }
}
