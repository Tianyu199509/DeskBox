using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed partial class LocalizationResourceContractTests
{
    private static readonly string[] SupportedLocales =
    [
        "en-US",
        "zh-CN",
        "zh-TW",
        "ja-JP",
        "de-DE",
        "pt-BR",
        "hi-IN",
        "es-ES",
        "fr-FR",
        "ar-SA",
        "bn-BD",
        "ru-RU",
        "ko-KR"
    ];

    [Fact]
    public void JsonLocales_HaveIdenticalKeysValuesAndPlaceholders()
    {
        string root = FindRepositoryRoot();
        string stringsDirectory = Path.Combine(root, "src", "DeskBox", "Strings");
        string[] actualLocales = Directory.EnumerateFiles(stringsDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(locale => locale, StringComparer.Ordinal)
            .ToArray()!;

        Assert.Equal(
            SupportedLocales.OrderBy(locale => locale, StringComparer.Ordinal),
            actualLocales);

        IReadOnlyDictionary<string, string> english = ReadJsonLocale(
            Path.Combine(stringsDirectory, "en-US.json"));

        foreach (string locale in SupportedLocales)
        {
            IReadOnlyDictionary<string, string> localized = ReadJsonLocale(
                Path.Combine(stringsDirectory, locale + ".json"));

            Assert.Equal(
                english.Keys.OrderBy(key => key, StringComparer.Ordinal),
                localized.Keys.OrderBy(key => key, StringComparer.Ordinal));

            foreach ((string key, string englishValue) in english)
            {
                string localizedValue = localized[key];
                Assert.False(
                    string.IsNullOrWhiteSpace(localizedValue),
                    $"{locale}:{key} has an empty translation.");
                Assert.Equal(
                    GetPlaceholderIndexes(englishValue),
                    GetPlaceholderIndexes(localizedValue));
            }
        }
    }

    [Fact]
    public void PackagedResources_DeclareEverySupportedLocale()
    {
        string root = FindRepositoryRoot();
        string projectDirectory = Path.Combine(root, "src", "DeskBox");
        var manifest = XDocument.Load(Path.Combine(projectDirectory, "Package.appxmanifest"));
        string[] manifestLocales = manifest.Descendants()
            .Where(element => element.Name.LocalName == "Resource")
            .Select(element => (string?)element.Attribute("Language"))
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Cast<string>()
            .ToArray();

        Assert.Equal(SupportedLocales, manifestLocales);

        foreach (string locale in SupportedLocales)
        {
            string resourcePath = Path.Combine(
                projectDirectory,
                "Resources",
                locale,
                "Resources.resw");
            Assert.True(File.Exists(resourcePath), $"Missing packaged resource: {locale}");

            var resource = XDocument.Load(resourcePath);
            Dictionary<string, string> values = resource.Descendants("data")
                .ToDictionary(
                    element => (string)element.Attribute("name")!,
                    element => element.Element("value")?.Value ?? string.Empty,
                    StringComparer.Ordinal);

            Assert.Equal(
                new[] { "AppDescription", "AppDisplayName" },
                values.Keys.OrderBy(key => key, StringComparer.Ordinal));
            Assert.All(values.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        }
    }

    [Fact]
    public void JsonLocales_ContainNoTemplateLeftoversOrControlCharacters()
    {
        // Authoring scripts interpolate quote helpers (${LQ}/${RQ}) into
        // values; a leak ships those literals to users (2026-10-10
        // incident: Settings.AutoStart.Mode.Description across 12 locales).
        // Control characters (\u0001 etc.) are always authoring dirt.
        string root = FindRepositoryRoot();
        string stringsDirectory = Path.Combine(root, "src", "DeskBox", "Strings");

        foreach (string file in Directory.EnumerateFiles(stringsDirectory, "*.json"))
        {
            IReadOnlyDictionary<string, string> locale = ReadJsonLocale(file);
            foreach ((string key, string value) in locale)
            {
                bool documentsExpressionSyntax =
                    key.StartsWith("CustomWidget.ApiDocs.", StringComparison.Ordinal) ||
                    key.StartsWith("CustomWidget.Cap.", StringComparison.Ordinal);
                bool hasTemplateLeak = documentsExpressionSyntax
                    ? value.Contains("${LQ}", StringComparison.Ordinal) || value.Contains("${RQ}", StringComparison.Ordinal)
                    : value.Contains("${", StringComparison.Ordinal);
                Assert.False(
                    hasTemplateLeak,
                    $"{Path.GetFileName(file)}:{key} contains a template placeholder leak.");
                Assert.False(
                    value.IndexOf('\u0001') >= 0 || value.IndexOf('\u0000') >= 0 || value.IndexOf('\u000b') >= 0,
                    $"{Path.GetFileName(file)}:{key} contains a control character.");
            }
        }
    }

    [Fact]
    public void UserFacingFailureSurfaces_DoNotExposeRawExceptionMessages()
    {
        string root = FindRepositoryRoot();
        string[] relativePaths =
        [
            "src/DeskBox/Controls/DesktopOrganizationTaskView.Actions.cs",
            "src/DeskBox/Controls/DesktopOrganizationTaskView.xaml.cs",
            "src/DeskBox/Controls/WidgetContents/QuickCaptureSurfaceContent.xaml.cs",
            "src/DeskBox/Views/SettingsSections/DesktopOrganizationSettingsSection.xaml.cs",
            "src/DeskBox/Views/SettingsWindow.StorageAndUpdates.cs"
        ];

        foreach (string relativePath in relativePaths)
        {
            string content = File.ReadAllText(Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));

            Assert.DoesNotContain("ResultInfo.Message = ex.Message", content, StringComparison.Ordinal);
            Assert.DoesNotContain("RuleStatusInfo.Message = ex.Message", content, StringComparison.Ordinal);
            Assert.DoesNotContain("RaiseFeedback(ex.Message", content, StringComparison.Ordinal);
            Assert.DoesNotContain("ShowStatusToast(ex.Message", content, StringComparison.Ordinal);
        }
    }

    private static IReadOnlyDictionary<string, string> ReadJsonLocale(string path)
    {
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
            ?? throw new InvalidDataException($"Localization file is empty: {path}");
    }

    private static string[] GetPlaceholderIndexes(string value)
    {
        return CompositePlaceholderRegex().Matches(value)
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(index => index, StringComparer.Ordinal)
            .ToArray();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "src", "DeskBox", "DeskBox.csproj")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("DeskBox repository root was not found.");
    }

    [GeneratedRegex(@"\{(\d+)(?:[^{}]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex CompositePlaceholderRegex();

    [GeneratedRegex(
        @"(?:\b\w+:)?(?:Localized\.(?:Key|HeaderKey|DescriptionKey|PlaceholderKey|PlaceholderTextKey|ToolTipKey|AutomationNameKey|OnContentKey|OffContentKey)|InfoTip\.(?:TitleKey|BodyKey))\s*=\s*""([^""]+)""",
        RegexOptions.CultureInvariant)]
    private static partial Regex XamlLocalizedKeyAttributeRegex();

    [Fact]
    public void EveryXamlLocalizedKeyAndInfoTipKey_ExistsInBaselineLocales()
    {
        string root = FindRepositoryRoot();
        string projectDirectory = Path.Combine(root, "src", "DeskBox");
        string stringsDirectory = Path.Combine(projectDirectory, "Strings");

        IReadOnlyDictionary<string, string> enUs = ReadJsonLocale(Path.Combine(stringsDirectory, "en-US.json"));
        IReadOnlyDictionary<string, string> zhCn = ReadJsonLocale(Path.Combine(stringsDirectory, "zh-CN.json"));

        var extractedKeys = new List<(string RelativeFile, string Key)>();
        foreach (string xamlPath in Directory.EnumerateFiles(projectDirectory, "*.xaml", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(projectDirectory, xamlPath);
            string[] segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(segment =>
                    segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
                    segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string xamlContent = File.ReadAllText(xamlPath);
            foreach (Match match in XamlLocalizedKeyAttributeRegex().Matches(xamlContent))
            {
                string key = match.Groups[1].Value.Trim();
                if (key.Length == 0 || key.StartsWith('{'))
                {
                    continue;
                }

                extractedKeys.Add((relativePath, key));
            }
        }

        Assert.NotEmpty(extractedKeys);

        foreach ((string relativeFile, string key) in extractedKeys)
        {
            Assert.True(
                enUs.TryGetValue(key, out string? enValue) && !string.IsNullOrWhiteSpace(enValue),
                $"XAML file '{relativeFile}' references missing or empty en-US key '{key}'.");
            Assert.True(
                zhCn.TryGetValue(key, out string? zhValue) && !string.IsNullOrWhiteSpace(zhValue),
                $"XAML file '{relativeFile}' references missing or empty zh-CN key '{key}'.");
        }
    }

    [Fact]
    public void ResidentHostKeyFilter_RetainsClipboardAndContentEditorDesktopKeys()
    {
        string root = FindRepositoryRoot();
        string stringsDirectory = Path.Combine(root, "src", "DeskBox", "Strings");

        // Verify the resident-host key predicate directly on both representative subtree paths
        // and actual baseline locale keys without mutating the process-wide static locale cache.
        Assert.False(LocalizationService.IsPureSettingsUiKey("Settings.Clipboard.DoubleCopy.Action.OpenPaste"));
        Assert.False(LocalizationService.IsPureSettingsUiKey("Settings.Clipboard.Hotkey.Status.Conflict"));
        Assert.False(LocalizationService.IsPureSettingsUiKey("Settings.Clipboard.DoubleCopy.Pin"));
        Assert.False(LocalizationService.IsPureSettingsUiKey("Settings.Clipboard.Hotkey.Status.GlobalHotkeyConflict"));
        Assert.False(LocalizationService.IsPureSettingsUiKey("Settings.ContentEditor.EnterBehavior.EnterSaves"));

        Assert.True(LocalizationService.IsPureSettingsUiKey("Settings.About.Description"));
        Assert.True(LocalizationService.IsPureSettingsUiKey("Settings.About.Version"));
        Assert.True(LocalizationService.IsPureSettingsUiKey("Settings.Appearance.Theme"));

        foreach (string locale in new[] { "en-US", "zh-CN" })
        {
            var dictionary = new Dictionary<string, string>(
                ReadJsonLocale(Path.Combine(stringsDirectory, locale + ".json")),
                StringComparer.Ordinal)
            {
                ["Settings.Clipboard.DoubleCopy.Action.OpenPaste"] = "Open paste",
                ["Settings.Clipboard.Hotkey.Status.Conflict"] = "Conflict"
            };

            Assert.True(dictionary.ContainsKey("Settings.About.Description"));
            Assert.True(dictionary.ContainsKey("Settings.About.Version"));

            LocalizationService.FilterDictionaryInPlace(dictionary);

            Assert.True(dictionary.ContainsKey("Settings.Clipboard.DoubleCopy.Action.OpenPaste"));
            Assert.True(dictionary.ContainsKey("Settings.Clipboard.Hotkey.Status.Conflict"));
            Assert.True(dictionary.ContainsKey("Settings.Clipboard.DoubleCopy.Pin"));
            Assert.True(dictionary.ContainsKey("Settings.Clipboard.Hotkey.Status.GlobalHotkeyConflict"));
            Assert.True(dictionary.ContainsKey("Settings.ContentEditor.EnterBehavior.EnterSaves"));

            Assert.False(dictionary.ContainsKey("Settings.About.Description"));
            Assert.False(dictionary.ContainsKey("Settings.About.Version"));
            Assert.DoesNotContain(
                dictionary.Keys,
                key => key.StartsWith("Settings.About.", StringComparison.Ordinal));
        }
    }
}
