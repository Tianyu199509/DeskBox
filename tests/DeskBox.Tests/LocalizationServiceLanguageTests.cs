using DeskBox.Services;
using System.Reflection;

namespace DeskBox.Tests;

public sealed class LocalizationServiceLanguageTests
{
    public static IEnumerable<object[]> NewLanguages()
    {
        yield return [SettingsService.LanguageChineseTraditional, "zh"];
        yield return [SettingsService.LanguageHindi, "hi"];
        yield return [SettingsService.LanguageSpanish, "es"];
        yield return [SettingsService.LanguageFrench, "fr"];
        yield return [SettingsService.LanguageArabic, "ar"];
        yield return [SettingsService.LanguageBengali, "bn"];
        yield return [SettingsService.LanguageRussian, "ru"];
        yield return [SettingsService.LanguageKorean, "ko"];
    }

    public static IEnumerable<object[]> SupportedLocaleTables()
    {
        yield return ["ZhCn"];
        yield return ["ZhTw"];
        yield return ["JaJp"];
        yield return ["DeDe"];
        yield return ["PtBr"];
        yield return ["HiIn"];
        yield return ["EsEs"];
        yield return ["FrFr"];
        yield return ["ArSa"];
        yield return ["BnBd"];
        yield return ["RuRu"];
        yield return ["KoKr"];
    }

    [Fact]
    public void AvailableLanguages_ContainsRequestedLocales()
    {
        var localization = TestServices.CreateLocalizationService();

        Assert.Contains(SettingsService.LanguageChineseTraditional, localization.AvailableLanguageSettings);
        Assert.Contains(SettingsService.LanguageHindi, localization.AvailableLanguageSettings);
        Assert.Contains(SettingsService.LanguageSpanish, localization.AvailableLanguageSettings);
        Assert.Contains(SettingsService.LanguageFrench, localization.AvailableLanguageSettings);
        Assert.Contains(SettingsService.LanguageArabic, localization.AvailableLanguageSettings);
        Assert.Contains(SettingsService.LanguageBengali, localization.AvailableLanguageSettings);
        Assert.Contains(SettingsService.LanguageRussian, localization.AvailableLanguageSettings);
        Assert.Contains(SettingsService.LanguageKorean, localization.AvailableLanguageSettings);
    }

    [Theory]
    [MemberData(nameof(NewLanguages))]
    public void NewLocale_ResolvesApiCodeAndCoreCopy(string language, string apiCode)
    {
        var localization = TestServices.CreateLocalizationService(language);

        Assert.Equal(language, localization.CurrentCultureName);
        Assert.Equal(apiCode, localization.ApiLanguageCode);
        Assert.NotEqual("Onboarding.Step1.Title", localization.T("Onboarding.Step1.Title"));
        Assert.NotEqual("Common.Paste", localization.T("Common.Paste"));
    }

    [Theory]
    [MemberData(nameof(NewLanguages))]
    public void NewLocale_ContainsEveryEnglishResourceKey(string language, string _)
    {
        var english = GetResourceTable("EnUs");
        var localized = GetResourceTable(language switch
        {
            SettingsService.LanguageHindi => "HiIn",
            SettingsService.LanguageChineseTraditional => "ZhTw",
            SettingsService.LanguageSpanish => "EsEs",
            SettingsService.LanguageFrench => "FrFr",
            SettingsService.LanguageArabic => "ArSa",
            SettingsService.LanguageBengali => "BnBd",
            SettingsService.LanguageRussian => "RuRu",
            SettingsService.LanguageKorean => "KoKr",
            _ => throw new ArgumentOutOfRangeException(nameof(language))
        });

        Assert.Equal(
            english.Keys.OrderBy(key => key),
            localized.Keys.OrderBy(key => key));
    }

    [Theory]
    [MemberData(nameof(SupportedLocaleTables))]
    public void SupportedLocale_ContainsEveryEnglishResourceKey(string propertyName)
    {
        var english = GetResourceTable("EnUs");
        var localized = GetResourceTable(propertyName);

        Assert.Equal(
            english.Keys.OrderBy(key => key),
            localized.Keys.OrderBy(key => key));
    }

    [Theory]
    [MemberData(nameof(NewLanguages))]
    public void NormalizeLanguageSetting_PreservesNewLocale(string language, string _)
    {
        Assert.Equal(language, LocalizationService.NormalizeLanguageSetting(language));
    }

    [Fact]
    public void TraditionalChinese_IsImmediatelyBelowSimplifiedChinese()
    {
        var localization = TestServices.CreateLocalizationService();
        string[] languages = localization.AvailableLanguageSettings.ToArray();
        int simplifiedIndex = Array.IndexOf(languages, SettingsService.LanguageChinese);

        Assert.True(simplifiedIndex >= 0);
        Assert.Equal(SettingsService.LanguageChineseTraditional, languages[simplifiedIndex + 1]);
        Assert.Equal("简体中文", localization.GetLanguageDisplayName(SettingsService.LanguageChinese));
        Assert.Equal("繁體中文", localization.GetLanguageDisplayName(SettingsService.LanguageChineseTraditional));
    }

    [Theory]
    [InlineData("zh-TW", true)]
    [InlineData("zh-HK", true)]
    [InlineData("zh-MO", true)]
    [InlineData("zh-Hant", true)]
    [InlineData("zh_Hant_HK", true)]
    [InlineData("zh-CN", false)]
    [InlineData("zh-SG", false)]
    [InlineData("zh-Hans", false)]
    [InlineData("en-US", false)]
    public void TraditionalChineseCulture_IsRecognized(string cultureName, bool expected)
    {
        Assert.Equal(expected, LocalizationService.IsTraditionalChineseCulture(cultureName));
    }

    [Fact]
    public void InstallLanguageResolver_PrefersPerUserKeyAndFallsBackToMachineDirectInstall()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            [@"HKEY_LOCAL_MACHINE\Software\DeskBox\DirectInstall"] = SettingsService.LanguageEnglish
        };

        Assert.Equal(
            SettingsService.LanguageEnglish,
            LocalizationService.TryResolveSupportedInstallLanguage((key, _) =>
                values.GetValueOrDefault(key)));

        values[@"HKEY_CURRENT_USER\Software\DeskBox\DirectInstall"] = SettingsService.LanguageJapanese;
        Assert.Equal(
            SettingsService.LanguageJapanese,
            LocalizationService.TryResolveSupportedInstallLanguage((key, _) =>
                values.GetValueOrDefault(key)));

        values[@"HKEY_CURRENT_USER\Software\DeskBox"] = SettingsService.LanguageKorean;
        Assert.Equal(
            SettingsService.LanguageKorean,
            LocalizationService.TryResolveSupportedInstallLanguage((key, _) =>
                values.GetValueOrDefault(key)));

        values[@"HKEY_CURRENT_USER\Software\DeskBox"] = "unsupported-locale";
        Assert.Equal(
            SettingsService.LanguageJapanese,
            LocalizationService.TryResolveSupportedInstallLanguage((key, _) =>
                values.GetValueOrDefault(key)));
    }

    private static IReadOnlyDictionary<string, string> GetResourceTable(string propertyName)
    {
        var property = typeof(LocalizationService).GetProperty(
            propertyName,
            BindingFlags.NonPublic | BindingFlags.Static);

        return Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(property?.GetValue(null));
    }
}
