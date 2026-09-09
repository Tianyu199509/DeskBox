using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DeskBox.Views.SettingsSections;

/// <summary>
/// 番茄钟的全局节奏设置。数值写入共享设置，并由统一策略限制合法范围。
/// </summary>
public sealed partial class PomodoroSettingsSection : UserControl
{
    private bool _isLoading;

    public PomodoroSettingsSection()
    {
        InitializeComponent();
        ApplyNumberBoxRanges();
        Loaded += OnLoaded;
    }

    private SettingsService Settings => App.Current.SettingsService;
    private LocalizationService Localization => App.Current.LocalizationService;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshFromSettings();
    }

    private void ApplyNumberBoxRanges()
    {
        RoundCountNumberBox.Minimum = PomodoroSettingsPolicy.MinRoundCount;
        RoundCountNumberBox.Maximum = PomodoroSettingsPolicy.MaxRoundCount;
        FocusMinutesNumberBox.Minimum = PomodoroSettingsPolicy.MinFocusMinutes;
        FocusMinutesNumberBox.Maximum = PomodoroSettingsPolicy.MaxFocusMinutes;
        ShortBreakMinutesNumberBox.Minimum = PomodoroSettingsPolicy.MinShortBreakMinutes;
        ShortBreakMinutesNumberBox.Maximum = PomodoroSettingsPolicy.MaxShortBreakMinutes;
        LongBreakMinutesNumberBox.Minimum = PomodoroSettingsPolicy.MinLongBreakMinutes;
        LongBreakMinutesNumberBox.Maximum = PomodoroSettingsPolicy.MaxLongBreakMinutes;
    }

    /// <summary>
    /// 重新读取当前设置，并同步页面中的数值、摘要与无障碍文本。
    /// </summary>
    public void RefreshFromSettings()
    {
        var settings = Settings.Settings;
        _isLoading = true;
        try
        {
            RoundCountNumberBox.Value =
                PomodoroSettingsPolicy.NormalizeRoundCount(settings.PomodoroRoundCount);
            FocusMinutesNumberBox.Value =
                PomodoroSettingsPolicy.NormalizeFocusMinutes(settings.PomodoroFocusMinutes);
            ShortBreakMinutesNumberBox.Value =
                PomodoroSettingsPolicy.NormalizeShortBreakMinutes(
                    settings.PomodoroShortBreakMinutes);
            LongBreakMinutesNumberBox.Value =
                PomodoroSettingsPolicy.NormalizeLongBreakMinutes(
                    settings.PomodoroLongBreakMinutes);
            CompletionSoundToggle.IsOn =
                settings.PomodoroCompletionSoundEnabled;
            CompletionNotificationToggle.IsOn =
                settings.PomodoroCompletionNotificationEnabled;
        }
        finally
        {
            _isLoading = false;
        }

        RefreshLocalizedContent();
    }

    private void RefreshLocalizedContent()
    {
        AutomationProperties.SetName(
            RoundCountNumberBox,
            Localization.T("Settings.Pomodoro.RoundCount.Title"));
        AutomationProperties.SetHelpText(
            RoundCountNumberBox,
            Localization.T("Settings.Pomodoro.RoundCount.Description"));
        AutomationProperties.SetName(
            FocusMinutesNumberBox,
            Localization.T("Settings.Pomodoro.FocusMinutes.Title"));
        AutomationProperties.SetHelpText(
            FocusMinutesNumberBox,
            Localization.T("Settings.Pomodoro.FocusMinutes.Description"));
        AutomationProperties.SetName(
            ShortBreakMinutesNumberBox,
            Localization.T("Settings.Pomodoro.ShortBreakMinutes.Title"));
        AutomationProperties.SetHelpText(
            ShortBreakMinutesNumberBox,
            Localization.T("Settings.Pomodoro.ShortBreakMinutes.Description"));
        AutomationProperties.SetName(
            LongBreakMinutesNumberBox,
            Localization.T("Settings.Pomodoro.LongBreakMinutes.Title"));
        AutomationProperties.SetHelpText(
            LongBreakMinutesNumberBox,
            Localization.T("Settings.Pomodoro.LongBreakMinutes.Description"));
        AutomationProperties.SetName(
            CompletionSoundToggle,
            Localization.T("Settings.Pomodoro.CompletionSound.Title"));
        AutomationProperties.SetHelpText(
            CompletionSoundToggle,
            Localization.T("Settings.Pomodoro.CompletionSound.Description"));
        AutomationProperties.SetName(
            CompletionNotificationToggle,
            Localization.T("Settings.Pomodoro.CompletionNotification.Title"));
        AutomationProperties.SetHelpText(
            CompletionNotificationToggle,
            Localization.T("Settings.Pomodoro.CompletionNotification.Description"));

        var settings = Settings.Settings;
        ScheduleSummaryText.Text = Localization.Format(
            "Settings.Pomodoro.Summary",
            PomodoroSettingsPolicy.NormalizeFocusMinutes(settings.PomodoroFocusMinutes),
            PomodoroSettingsPolicy.NormalizeShortBreakMinutes(
                settings.PomodoroShortBreakMinutes),
            PomodoroSettingsPolicy.NormalizeLongBreakMinutes(
                settings.PomodoroLongBreakMinutes),
            PomodoroSettingsPolicy.NormalizeRoundCount(settings.PomodoroRoundCount));
    }

    private void RoundCountNumberBox_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        var settings = Settings.Settings;
        CommitValue(
            sender,
            args.NewValue,
            settings.PomodoroRoundCount,
            PomodoroSettingsPolicy.NormalizeRoundCount,
            value => settings.PomodoroRoundCount = value);
    }

    private void FocusMinutesNumberBox_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        var settings = Settings.Settings;
        CommitValue(
            sender,
            args.NewValue,
            settings.PomodoroFocusMinutes,
            PomodoroSettingsPolicy.NormalizeFocusMinutes,
            value => settings.PomodoroFocusMinutes = value);
    }

    private void ShortBreakMinutesNumberBox_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        var settings = Settings.Settings;
        CommitValue(
            sender,
            args.NewValue,
            settings.PomodoroShortBreakMinutes,
            PomodoroSettingsPolicy.NormalizeShortBreakMinutes,
            value => settings.PomodoroShortBreakMinutes = value);
    }

    private void LongBreakMinutesNumberBox_ValueChanged(
        NumberBox sender,
        NumberBoxValueChangedEventArgs args)
    {
        var settings = Settings.Settings;
        CommitValue(
            sender,
            args.NewValue,
            settings.PomodoroLongBreakMinutes,
            PomodoroSettingsPolicy.NormalizeLongBreakMinutes,
            value => settings.PomodoroLongBreakMinutes = value);
    }

    private void CompletionSoundToggle_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        AppSettings settings = Settings.Settings;
        if (settings.PomodoroCompletionSoundEnabled == CompletionSoundToggle.IsOn)
        {
            return;
        }

        settings.PomodoroCompletionSoundEnabled = CompletionSoundToggle.IsOn;
        Settings.SaveDebounced();
    }

    private void CompletionNotificationToggle_Toggled(
        object sender,
        RoutedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        AppSettings settings = Settings.Settings;
        if (settings.PomodoroCompletionNotificationEnabled ==
            CompletionNotificationToggle.IsOn)
        {
            return;
        }

        settings.PomodoroCompletionNotificationEnabled =
            CompletionNotificationToggle.IsOn;
        Settings.SaveDebounced();
    }

    private void CommitValue(
        NumberBox numberBox,
        double proposedValue,
        int currentValue,
        Func<int, int> normalize,
        Action<int> assign)
    {
        if (_isLoading)
        {
            return;
        }

        int normalizedCurrent = normalize(currentValue);
        int normalizedValue = double.IsNaN(proposedValue) || double.IsInfinity(proposedValue)
            ? normalizedCurrent
            : normalize((int)Math.Round(
                Math.Clamp(proposedValue, int.MinValue, int.MaxValue),
                MidpointRounding.AwayFromZero));

        if (double.IsNaN(numberBox.Value) || numberBox.Value != normalizedValue)
        {
            _isLoading = true;
            try
            {
                numberBox.Value = normalizedValue;
            }
            finally
            {
                _isLoading = false;
            }
        }

        if (currentValue != normalizedValue)
        {
            assign(normalizedValue);
            Settings.SaveDebounced();
        }

        RefreshLocalizedContent();
    }
}
