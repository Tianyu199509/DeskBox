using DeskBox.Services;

namespace DeskBox.Tests;

internal static class TestServices
{
    public static LocalizationService CreateLocalizationService(string language = SettingsService.LanguageEnglish)
    {
        var settingsService = new SettingsService();
        settingsService.Settings.Language = language;
        return new LocalizationService(settingsService);
    }

    public static WidgetContentFactory CreateWidgetContentFactory()
    {
        return new WidgetContentFactory(CreateLocalizationService());
    }
}
