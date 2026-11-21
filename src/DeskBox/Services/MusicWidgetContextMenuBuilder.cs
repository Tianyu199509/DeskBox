using DeskBox.Contracts;
using DeskBox.Models;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Services;

internal static class MusicWidgetContextMenuBuilder
{
    private static readonly (string Value, string Key)[] SpectrumStyleOptions =
    [
        (MusicSpectrumStyles.Auto, "Settings.Music.SpectrumStyle.Auto"),
        (MusicSpectrumStyles.SymmetricWave, "Settings.Music.SpectrumStyle.SymmetricWave"),
        (MusicSpectrumStyles.WaveformScrub, "Settings.Music.SpectrumStyle.WaveformScrub"),
        (MusicSpectrumStyles.PeakHoldBars, "Settings.Music.SpectrumStyle.PeakHoldBars"),
        (MusicSpectrumStyles.AuroraSilk, "Settings.Music.SpectrumStyle.AuroraSilk"),
        (MusicSpectrumStyles.MatrixLed, "Settings.Music.SpectrumStyle.MatrixLed"),
        (MusicSpectrumStyles.VinylRadialHalo, "Settings.Music.SpectrumStyle.VinylRadialHalo"),
        (MusicSpectrumStyles.Off, "Settings.Music.SpectrumStyle.Off")
    ];

    private static readonly (string Value, string Key)[] DisplayModeOptions =
    [
        (MusicDisplayModes.Auto, "Settings.Music.DisplayMode.Auto"),
        (MusicDisplayModes.Cover, "Settings.Music.DisplayMode.Cover"),
        (MusicDisplayModes.Controls, "Settings.Music.DisplayMode.Controls"),
        (MusicDisplayModes.RecordVertical, "Settings.Music.DisplayMode.RecordVertical"),
        (MusicDisplayModes.RecordHorizontal, "Settings.Music.DisplayMode.RecordHorizontal")
    ];

    public static void Append(
        MenuFlyout flyout,
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        ArgumentNullException.ThrowIfNull(flyout);
        ArgumentNullException.ThrowIfNull(settingsService);
        ArgumentNullException.ThrowIfNull(localizationService);

        flyout.Items.Add(CreateSpectrumMenu(settingsService, localizationService));
        flyout.Items.Add(CreateDisplayModeMenu(settingsService, localizationService));
        flyout.Items.Add(new MenuFlyoutSeparator());
    }

    internal static bool ApplySpectrumStyle(SettingsService settingsService, string? style)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        string normalized = SettingsService.NormalizeMusicSpectrumStyle(style);
        MusicSettingsSlice music = settingsService.Settings.Music;
        if (string.Equals(music.MusicSpectrumStyle, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        music.MusicSpectrumStyle = normalized;
        settingsService.SaveDebounced();
        return true;
    }

    internal static bool ApplyDisplayMode(SettingsService settingsService, string? mode)
    {
        ArgumentNullException.ThrowIfNull(settingsService);
        string normalized = SettingsService.NormalizeMusicDisplayMode(mode);
        MusicSettingsSlice music = settingsService.Settings.Music;
        if (string.Equals(music.MusicDisplayMode, normalized, StringComparison.Ordinal))
        {
            return false;
        }

        music.MusicDisplayMode = normalized;
        settingsService.SaveDebounced();
        return true;
    }

    private static MenuFlyoutSubItem CreateSpectrumMenu(
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Settings.Music.SpectrumStyle.Title"),
            Icon = new FontIcon { Glyph = "\uE9D5" }
        };

        string current = SettingsService.NormalizeMusicSpectrumStyle(
            settingsService.Settings.Music.MusicSpectrumStyle);

        foreach ((string value, string key) in SpectrumStyleOptions)
        {
            string capturedValue = value;
            var item = new RadioMenuFlyoutItem
            {
                Text = localizationService.T(key),
                GroupName = "MusicSpectrumStyle",
                IsChecked = string.Equals(current, capturedValue, StringComparison.Ordinal)
            };
            item.Click += (_, _) => ApplySpectrumStyle(settingsService, capturedValue);
            menu.Items.Add(item);
        }

        return menu;
    }

    private static MenuFlyoutSubItem CreateDisplayModeMenu(
        SettingsService settingsService,
        LocalizationService localizationService)
    {
        var menu = new MenuFlyoutSubItem
        {
            Text = localizationService.T("Settings.Music.DisplayMode.Title"),
            Icon = new FontIcon { Glyph = "\uE8B9" }
        };

        string current = SettingsService.NormalizeMusicDisplayMode(
            settingsService.Settings.Music.MusicDisplayMode);

        foreach ((string value, string key) in DisplayModeOptions)
        {
            string capturedValue = value;
            var item = new RadioMenuFlyoutItem
            {
                Text = localizationService.T(key),
                GroupName = "MusicDisplayMode",
                IsChecked = string.Equals(current, capturedValue, StringComparison.Ordinal)
            };
            item.Click += (_, _) => ApplyDisplayMode(settingsService, capturedValue);
            menu.Items.Add(item);
        }

        return menu;
    }
}
