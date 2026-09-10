extern alias GlancePkg;

using System.Globalization;
using System.Text.Json;
using DeskBox.Services;
using PackageStrings = GlancePkg::DeskBox.GlancePackage.Services.PackageStrings;

namespace DeskBox.Tests;

public sealed class NativeGlanceLocalizationCoverageTests : IDisposable
{
    private static readonly (string PackageKey, string HostKey)[] UiKeys =
    [
        ("menuNextBackground", "Glance.Actions.Next"),
        ("menuPauseRotation", "Glance.Actions.Pause"),
        ("menuSettings", "Settings.Title"),
        ("festivalToggle", "Glance.Festivals.Title"),
        ("traditionalToggle", "Glance.TraditionalCalendar.Title"),
        ("toggleOff", "Settings.Toggle.Off"),
        ("toggleOn", "Settings.Toggle.On")
    ];

    // All Configure/Get calls stay in this class (one xUnit collection), since
    // the production package owns a single static table.
    private readonly string _root = Directory.CreateTempSubdirectory("DeskBox-glance-localization-").FullName;
    private static string RepositoryRoot
    {
        get
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "DeskBox.sln")))
                    return directory.FullName;
            }
            throw new DirectoryNotFoundException("DeskBox repository root was not found.");
        }
    }

    private static string PackageRoot => Path.Combine(RepositoryRoot, "src", "DeskBox.GlancePackage");

    [Fact]
    public void EveryHostCultureShipsAllUiKeysWithExistingHostTranslations()
    {
        var localization = TestServices.CreateLocalizationService();
        foreach (string locale in localization.AvailableLanguageSettings
                     .Where(language => language != LocalizationService.LanguageSystem))
        {
            using JsonDocument host = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(RepositoryRoot, "src", "DeskBox", "Strings", $"{locale}.json")));
            using JsonDocument package = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(PackageRoot, "strings", $"{locale}.json")));

            PackageStrings.Configure(CultureInfo.GetCultureInfo(locale), PackageRoot);
            foreach (var (packageKey, hostKey) in UiKeys)
            {
                string? expected = host.RootElement.GetProperty(hostKey).GetString();
                Assert.False(string.IsNullOrWhiteSpace(expected), $"Host: {locale}/{hostKey}");
                Assert.True(package.RootElement.TryGetProperty(packageKey, out JsonElement entry),
                    $"Missing package translation: {locale}/{packageKey}");
                Assert.Equal(expected, entry.GetString());
                Assert.Equal(expected, PackageStrings.Get(packageKey, "中文内联默认"));
            }
        }
    }

    [Fact]
    public void PartialLocaleOverridesOnlyPresentKeysAndKeepsEnglishForEveryOtherUiKey()
    {
        CopyEnglishTable();
        WriteTable("fr-FR", """{"menuSettings":"Paramètres"}""");

        PackageStrings.Configure(CultureInfo.GetCultureInfo("fr-FR"), _root);

        Assert.Equal("Paramètres", PackageStrings.Get("menuSettings", "设置"));
        AssertEnglishUiKeysExcept("menuSettings");
    }

    [Theory]
    [InlineData(null)] // Missing file.
    [InlineData("")]
    [InlineData("{\"menuSettings\":\"incomplete\",")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("\"text\"")]
    [InlineData("{}")]
    public void MissingOrBrokenLocaleKeepsTheEntireEnglishBase(string? content)
    {
        CopyEnglishTable();
        if (content is not null) WriteTable("fr-FR", content);

        PackageStrings.Configure(CultureInfo.GetCultureInfo("fr-FR"), _root);

        AssertEnglishUiKeysExcept();
    }

    [Fact]
    public void InvalidTranslationValuesKeepEnglishWithoutDiscardingValidSiblings()
    {
        CopyEnglishTable();
        WriteTable("fr-FR", """
            {
              "menuNextBackground": null,
              "menuPauseRotation": 12,
              "menuSettings": "Paramètres",
              "festivalToggle": {},
              "traditionalToggle": [],
              "toggleOff": "",
              "toggleOn": "  "
            }
            """);

        PackageStrings.Configure(CultureInfo.GetCultureInfo("fr-FR"), _root);

        Assert.Equal("Paramètres", PackageStrings.Get("menuSettings", "设置"));
        AssertEnglishUiKeysExcept("menuSettings");
    }

    [Fact]
    public void ReconfigureReplacesPreviousLocaleInsteadOfKeepingStaleTranslations()
    {
        CopyEnglishTable();
        WriteTable("zh-CN", """{"menuSettings":"设置"}""");
        PackageStrings.Configure(CultureInfo.GetCultureInfo("zh-CN"), _root);
        Assert.Equal("设置", PackageStrings.Get("menuSettings", "missing"));

        PackageStrings.Configure(CultureInfo.GetCultureInfo("de-DE"), _root);
        AssertEnglishUiKeysExcept();

        PackageStrings.Configure(CultureInfo.GetCultureInfo("en-US"), _root);
        AssertEnglishUiKeysExcept();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{broken")]
    [InlineData("[]")]
    public void MissingOrBrokenEnglishStillAllowsValidLocaleAndLastResortForUnknownKeys(string? content)
    {
        if (content is not null) WriteTable("en-US", content);
        WriteTable("fr-FR", """{"menuSettings":"Paramètres"}""");

        PackageStrings.Configure(CultureInfo.GetCultureInfo("fr-FR"), _root);

        Assert.Equal("Paramètres", PackageStrings.Get("menuSettings", "设置"));
        Assert.Equal("caller fallback", PackageStrings.Get("unknownKey", "caller fallback"));
    }

    private void CopyEnglishTable() =>
        WriteTable("en-US", File.ReadAllText(Path.Combine(PackageRoot, "strings", "en-US.json")));

    private void WriteTable(string locale, string content)
    {
        Directory.CreateDirectory(Path.Combine(_root, "strings"));
        File.WriteAllText(Path.Combine(_root, "strings", $"{locale}.json"), content);
    }

    private static void AssertEnglishUiKeysExcept(string? except = null)
    {
        using JsonDocument english = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(PackageRoot, "strings", "en-US.json")));
        foreach (var (packageKey, _) in UiKeys)
        {
            if (packageKey == except) continue;
            Assert.Equal(english.RootElement.GetProperty(packageKey).GetString(),
                PackageStrings.Get(packageKey, "中文内联默认"));
        }
    }

    public void Dispose()
    {
        PackageStrings.Configure(CultureInfo.GetCultureInfo("en-US"), PackageRoot);
        Directory.Delete(_root, recursive: true);
    }
}
