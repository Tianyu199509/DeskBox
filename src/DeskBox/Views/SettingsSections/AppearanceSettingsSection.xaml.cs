using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views.SettingsSections;

public sealed partial class AppearanceSettingsSection : UserControl
{
    private bool _windowShadowSyncing;

    private LocalizationService Localization => App.Current.LocalizationService;

    public AppearanceSettingsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public event EventHandler<SettingsSectionNavigationRequestedEventArgs>? NavigationRequested;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshWindowShadowToggle();
    }

    private void NestedSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        // Drill-down rows are Buttons, but the 格子组-style entry cards are
        // toolkit SettingsCards (ButtonBase, not Button); match the shell's
        // FrameworkElement guard so both navigate.
        if (sender is FrameworkElement { Tag: string sectionTag })
        {
            NavigationRequested?.Invoke(this, new SettingsSectionNavigationRequestedEventArgs(sectionTag));
        }
    }

    private void AccentPresetButton_Click(object sender, RoutedEventArgs e)
    {
        // The section's DataContext is the appearance editor; the custom
        // accent write stays on the settings shell (theme service state), so
        // the preset pick surfaces as an editor event the shell handles.
        if (DataContext is not DeskBox.Features.Appearance.AppearanceSettingsViewModel editor ||
            sender is not Button { Tag: string hex } ||
            !AccentColorHelper.TryParseHex(hex, out var color))
        {
            return;
        }

        editor.NotifyAccentPresetPicked(AccentColorHelper.ToHex(color));
    }

    // The toggle mirrors the system-wide "show shadows under windows" effect,
    // not a DeskBox preference — the source of truth is always the OS.
    private void RefreshWindowShadowToggle()
    {
        _windowShadowSyncing = true;
        try
        {
            // A failed read is "unknown", not "off" — leave the toggle where
            // it is instead of showing a state the OS may not have.
            if (Win32Helper.TryGetWindowDropShadowEnabled(out bool enabled))
            {
                WindowShadowToggle.IsOn = enabled;
            }
        }
        finally
        {
            _windowShadowSyncing = false;
        }
    }

    private async void WindowShadowToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_windowShadowSyncing)
        {
            return;
        }

        bool target = WindowShadowToggle.IsOn;
        if (!await ConfirmWindowShadowChangeAsync())
        {
            RefreshWindowShadowToggle();
            return;
        }

        // SPIF_SENDCHANGE broadcasts WM_SETTINGCHANGE to every top-level
        // window and waits — a hung window anywhere in the session costs its
        // full timeout on the calling thread, so the write+readback runs on a
        // worker while the toggle is disabled.
        WindowShadowToggle.IsEnabled = false;
        try
        {
            (bool setOk, int errorCode, bool? applied) = await Task.Run(() =>
            {
                if (!Win32Helper.TrySetWindowDropShadowEnabled(target, out int error))
                {
                    return (false, error, (bool?)null);
                }

                bool? readback = Win32Helper.TryGetWindowDropShadowEnabled(out bool value)
                    ? value
                    : null;
                return (true, 0, readback);
            });

            if (!setOk)
            {
                App.Log($"[Settings] SPI_SETDROPSHADOW failed: 0x{errorCode:X8}");
                RefreshWindowShadowToggle();
                await ShowWindowShadowFailureAsync(errorCode);
                return;
            }

            // Some sessions (e.g. remote/restricted desktops) accept the call
            // but do not actually flip the effect — sync the toggle with what
            // the OS reports.
            if (applied.HasValue && applied.Value != target)
            {
                App.Log("[Settings] SPI_SETDROPSHADOW ignored by session; state unchanged");
                RefreshWindowShadowToggle();
                await ShowWindowShadowFailureAsync(0);
                return;
            }
        }
        finally
        {
            WindowShadowToggle.IsEnabled = true;
        }

        // Turning shadows on gives the idle peer order a purpose again; the
        // queue itself early-outs when shadows are off, so one call covers
        // both directions of the toggle.
        App.Current.WidgetManager?.QueueIdleWidgetZOrderNormalization(
            "window-shadow-toggled");
        RefreshWindowShadowToggle();
    }

    private async Task<bool> ConfirmWindowShadowChangeAsync()
    {
        if (XamlRoot is null)
        {
            return false;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.T("Settings.WindowShadow.Confirm.Title"),
            PrimaryButtonText = Localization.T("Common.Continue"),
            CloseButtonText = Localization.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = Localization.T("Settings.WindowShadow.Confirm.Body"),
                TextWrapping = TextWrapping.Wrap
            }
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowWindowShadowFailureAsync(int errorCode)
    {
        if (XamlRoot is null)
        {
            return;
        }

        string body = errorCode != 0
            ? Localization.Format("Settings.WindowShadow.ApplyFailed", $"0x{errorCode:X8}")
            : Localization.T("Settings.WindowShadow.ApplyFailedNoEffect");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.T("Settings.WindowShadow.Title"),
            CloseButtonText = Localization.T("Common.Ok"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = body,
                TextWrapping = TextWrapping.Wrap
            }
        };

        await dialog.ShowAsync();
    }
}
