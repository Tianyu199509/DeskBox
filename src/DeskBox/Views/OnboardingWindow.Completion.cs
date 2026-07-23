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
    private void SetupStep5()
    {
        // Widgets summary
        var enabledWidgets = new List<string>();
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Todo))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step2.TodoTitle"));
        }
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.QuickCapture))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step2.QuickCaptureTitle"));
        }
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Music))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step2.MusicTitle"));
        }
        if (FeatureWidgetSettings.IsEnabled(_settingsService.Settings, WidgetKind.Weather))
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step2.WeatherTitle"));
        }
        if (_settingsService.Settings.SearchHotkeyEnabled)
        {
            enabledWidgets.Add(_localizationService.T("Onboarding.Step2.SearchTitle"));
        }
        Step5WidgetsSummary.Text = enabledWidgets.Count > 0
            ? string.Join(" · ", enabledWidgets)
            : _localizationService.T("Onboarding.Step5.NoWidgets");

        // Appearance summary
        string themeLabel = _settingsService.Settings.Theme switch
        {
            "Light" => _localizationService.T("Onboarding.Step3.ThemeLight"),
            "Dark" => _localizationService.T("Onboarding.Step3.ThemeDark"),
            _ => _localizationService.T("Onboarding.Step3.ThemeSystem")
        };
        string materialLabel = _settingsService.Settings.WidgetMaterialType switch
        {
            "Acrylic" => _localizationService.T("Onboarding.Step3.MaterialAcrylic"),
            "Solid" => _localizationService.T("Onboarding.Step3.MaterialSolid"),
            _ => _localizationService.T("Onboarding.Step3.MaterialMica")
        };
        Step5AppearanceSummary.Text = $"{themeLabel} · {materialLabel}";

        // Daily use summary
        string hotkeySummary = _settingsService.Settings.GlobalHotkeyEnabled
            ? _localizationService.T("Onboarding.Step5.SummaryHotkeyOn")
            : _localizationService.T("Onboarding.Step5.SummaryHotkeyOff");
        string startupSummary = StartupService.IsEnabled()
            ? _localizationService.T("Onboarding.Step5.SummaryStartupOn")
            : _localizationService.T("Onboarding.Step5.SummaryStartupOff");
        Step5DailySummary.Text = $"{hotkeySummary} · {startupSummary}";

        // Storage summary
        string path = SettingsService.NormalizeManagedStorageRootPath(_settingsService.Settings.DefaultManagedStorageRootPath);
        var pinState = ExplorerQuickAccessHelper.GetQuickAccessPinState(path, out _);
        bool isPinned = pinState == QuickAccessPinState.Pinned;
        string pinStatus = isPinned
            ? _localizationService.T("Onboarding.Step5.SummaryPinned")
            : _localizationService.T("Onboarding.Step5.SummaryNotPinned");
        Step5StorageSummary.Text = $"{System.IO.Path.GetFileName(path)} · {pinStatus}";

        // Start search demo animation
        if (!_isAnimating)
        {
            StartSearchDemoAnimation();
        }
    }

    // ════════════════════════════════════════════════════════════
    //  Localization
    // ════════════════════════════════════════════════════════════

    private void OnLanguageChanged()
    {
        Title = _localizationService.T("Onboarding.WindowTitle");
        Localized.RefreshAll(_localizationService);
        PrepareIntroContent();
        SetupStep(animate: false);
        UpdateFooterState();
    }

    // ════════════════════════════════════════════════════════════
    //  Intro Sequence (preserved from original)
    // ════════════════════════════════════════════════════════════
}
