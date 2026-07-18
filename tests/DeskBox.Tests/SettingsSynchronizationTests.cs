using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class SettingsSynchronizationTests
{
    [Fact]
    public void LanguageChangedRefreshesLocalizedValuesAndRaisesOnce()
    {
        using var scope = new TempSettingsScope();
        var settingsService = new SettingsService(scope.RootPath);
        settingsService.Settings.Language = SettingsService.LanguageEnglish;
        var localizationService = new LocalizationService(settingsService);
        int languageChangedCount = 0;
        localizationService.LanguageChanged += () => languageChangedCount++;

        string englishMaterial = localizationService.T("Settings.Material.Mica");
        string englishWeatherSkin = localizationService.T("Weather.Skin.Rich");

        localizationService.SetLanguage(SettingsService.LanguageChinese);

        Assert.Equal(1, languageChangedCount);
        Assert.Equal(SettingsService.LanguageChinese, settingsService.Settings.Language);
        Assert.NotEqual(englishMaterial, localizationService.T("Settings.Material.Mica"));
        Assert.NotEqual(englishWeatherSkin, localizationService.T("Weather.Skin.Rich"));
    }

    [Fact]
    public async Task SaveAsyncNotifiesSubscribersAndPersistsSettingsSnapshot()
    {
        using var scope = new TempSettingsScope();
        var settingsService = new SettingsService(scope.RootPath);
        bool settingsChanged = false;
        settingsService.SettingsChanged += () => settingsChanged = true;

        settingsService.Settings.WidgetCapsuleModeEnabled = true;
        settingsService.Settings.TodoShowThisWeekTab = true;
        settingsService.Settings.TodoDefaultFilter = SettingsService.TodoDefaultFilterThisWeek;
        settingsService.Settings.WeatherSkin = SettingsService.WeatherSkinRich;

        await settingsService.SaveAsync();

        Assert.True(settingsChanged);

        var reloadedService = new SettingsService(scope.RootPath);
        await reloadedService.LoadAsync();

        Assert.True(reloadedService.Settings.WidgetCapsuleModeEnabled);
        Assert.Equal(SettingsService.TodoDefaultFilterThisWeek, reloadedService.Settings.TodoDefaultFilter);
        Assert.Equal(SettingsService.WeatherSkinRich, reloadedService.Settings.WeatherSkin);
    }

    private sealed class TempSettingsScope : IDisposable
    {
        public TempSettingsScope()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "DeskBoxTests", Guid.NewGuid().ToString("N"));
        }

        public string RootPath { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch
            {
            }
        }
    }
}
