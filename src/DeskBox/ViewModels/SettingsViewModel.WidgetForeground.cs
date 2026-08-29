using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace DeskBox.ViewModels;

public partial class SettingsViewModel
{
    private string _selectedWidgetForegroundMode = WidgetForegroundSettings.ModeFollowTheme;
    private Color _selectedWidgetForegroundColor =
        AccentColorHelper.FromHex(WidgetForegroundSettings.DefaultCustomColorHex);

    public IReadOnlyList<SettingsOption> AvailableWidgetForegroundModeOptions =>
        WrapOptions(
        [
            new(
                WidgetForegroundSettings.ModeFollowTheme,
                _localizationService.T("Settings.WidgetForeground.FollowTheme")),
            new(
                WidgetForegroundSettings.ModeLight,
                _localizationService.T("Settings.WidgetForeground.Light")),
            new(
                WidgetForegroundSettings.ModeDark,
                _localizationService.T("Settings.WidgetForeground.Dark")),
            new(
                WidgetForegroundSettings.ModeCustom,
                _localizationService.T("Settings.WidgetForeground.Custom"))
        ]);

    public string SelectedWidgetForegroundMode
    {
        get => _selectedWidgetForegroundMode;
        set
        {
            string normalized = WidgetForegroundSettings.NormalizeMode(value);
            if (!SetProperty(ref _selectedWidgetForegroundMode, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(WidgetForegroundCustomColorVisibility));
            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.WidgetForegroundMode = normalized;
            SaveAppearanceChange();
        }
    }

    public Color SelectedWidgetForegroundColor
    {
        get => _selectedWidgetForegroundColor;
        set
        {
            Color opaque = Color.FromArgb(0xFF, value.R, value.G, value.B);
            if (!SetProperty(ref _selectedWidgetForegroundColor, opaque))
            {
                return;
            }

            if (_isRestoringDefaults)
            {
                return;
            }

            _settingsService.Settings.WidgetForegroundColor =
                AccentColorHelper.ToHex(opaque);
            SaveAppearanceChange();
        }
    }

    public Visibility WidgetForegroundCustomColorVisibility =>
        string.Equals(
            SelectedWidgetForegroundMode,
            WidgetForegroundSettings.ModeCustom,
            StringComparison.Ordinal)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void InitializeWidgetForegroundSettings(AppSettings settings)
    {
        _selectedWidgetForegroundMode =
            WidgetForegroundSettings.NormalizeMode(settings.WidgetForegroundMode);
        _selectedWidgetForegroundColor = AccentColorHelper.TryParseHex(
            settings.WidgetForegroundColor,
            out Color color)
            ? Color.FromArgb(0xFF, color.R, color.G, color.B)
            : AccentColorHelper.FromHex(WidgetForegroundSettings.DefaultCustomColorHex);
    }

    private void ApplyWidgetForegroundSettingsSnapshot(AppSettings settings)
    {
        SelectedWidgetForegroundMode = settings.WidgetForegroundMode;
        SelectedWidgetForegroundColor = AccentColorHelper.TryParseHex(
            settings.WidgetForegroundColor,
            out Color color)
            ? color
            : AccentColorHelper.FromHex(WidgetForegroundSettings.DefaultCustomColorHex);
    }

    private void RefreshWidgetForegroundSelectionProperties(bool refreshLocalizedOptions)
    {
        if (refreshLocalizedOptions)
        {
            OnPropertyChanged(nameof(AvailableWidgetForegroundModeOptions));
        }

        OnPropertyChanged(nameof(WidgetForegroundCustomColorVisibility));
    }
}
