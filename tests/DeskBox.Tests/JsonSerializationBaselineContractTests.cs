using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class JsonSerializationBaselineContractTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(
        Path.GetTempPath(),
        "DeskBox.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ProductionInventory_IsFrozenAtTwentyEightFilesAndSixtyFourCalls()
    {
        var expected = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["src/DeskBox/App.AotHotkeySmoke.cs"] = 1,
            ["src/DeskBox/App.AotShortcutSmoke.cs"] = 1,
            ["src/DeskBox/App.AotShellSmoke.cs"] = 1,
            ["src/DeskBox/App.AotQuickAccessMutationSmoke.cs"] = 1,
            ["src/DeskBox/App.AotMusicVolumeReadSmoke.cs"] = 1,
            ["src/DeskBox/App.AotMusicVolumeMutationSmoke.cs"] = 2,
            ["src/DeskBox/App.AotMusicVolumeSessionMutationSmoke.cs"] = 2,
            ["src/DeskBox/App.AotManagedUiSmoke.cs"] = 1,
            ["src/DeskBox/App.AotTodoRecurrenceReminderSmoke.cs"] = 1,
            ["src/DeskBox/App.AotTodoNotificationLifecycleSmoke.cs"] = 1,
            ["src/DeskBox/App.AotTodoNotificationActivationSmoke.cs"] = 1,
            ["src/DeskBox/App.AotTodoNotificationForwardingSmoke.cs"] = 1,
            ["src/DeskBox/Services/AppUpdateService.cs"] = 2,
            ["src/DeskBox/Services/CitySearchService.cs"] = 1,
            ["src/DeskBox/Services/DeskBoxAttachmentHealthService.cs"] = 1,
            ["src/DeskBox/Services/DeskBoxDataBackupService.cs"] = 11,
            ["src/DeskBox/Services/DeskBoxDiagnosticsBundleService.cs"] = 1,
            ["src/DeskBox/Services/DesktopOrganizationRecoveryStore.cs"] = 2,
            ["src/DeskBox/Services/GlanceImageService.cs"] = 2,
            ["src/DeskBox/Services/GlanceWidgetStore.cs"] = 7,
            ["src/DeskBox/Services/LocalizationService.cs"] = 1,
            ["src/DeskBox/Services/NativeNotificationActivationEnvelopeStore.cs"] = 2,
            ["src/DeskBox/Services/QuickCaptureStore.cs"] = 2,
            ["src/DeskBox/Services/SearchHistoryService.cs"] = 2,
            ["src/DeskBox/Services/SettingsService.cs"] = 2,
            ["src/DeskBox/Services/TodoWidgetStore.cs"] = 2,
            ["src/DeskBox/Services/WeatherService.cs"] = 5,
            ["src/DeskBox/Services/WidgetFileStackSettings.cs"] = 7
        };

        Dictionary<string, int> actual = ProductionSourceFiles()
            .Select(path => new
            {
                Path = RepositoryRelativePath(path),
                Count = Regex.Matches(
                    File.ReadAllText(path),
                    @"JsonSerializer\.(?:Serialize|Deserialize)(?:Async)?\b").Count
            })
            .Where(item => item.Count > 0)
            .ToDictionary(item => item.Path, item => item.Count, StringComparer.Ordinal);

        Assert.Equal(expected.Keys.Order(), actual.Keys.Order());
        foreach ((string path, int expectedCount) in expected)
        {
            Assert.Equal(expectedCount, actual[path]);
        }

        Assert.Equal(28, actual.Count);
        Assert.Equal(64, actual.Values.Sum());

        string[] expectedContextOwners =
        [
            "src/DeskBox/App.AotHotkeySmoke.cs",
            "src/DeskBox/App.AotManagedUiSmoke.cs",
            "src/DeskBox/App.AotMusicVolumeMutationSmoke.cs",
            "src/DeskBox/App.AotMusicVolumeReadSmoke.cs",
            "src/DeskBox/App.AotMusicVolumeSessionMutationSmoke.cs",
            "src/DeskBox/App.AotQuickAccessMutationSmoke.cs",
            "src/DeskBox/App.AotShellSmoke.cs",
            "src/DeskBox/App.AotShortcutSmoke.cs",
            "src/DeskBox/App.AotTodoNotificationActivationSmoke.cs",
            "src/DeskBox/App.AotTodoNotificationForwardingSmoke.cs",
            "src/DeskBox/App.AotTodoNotificationLifecycleSmoke.cs",
            "src/DeskBox/App.AotTodoRecurrenceReminderSmoke.cs",
            "src/DeskBox/Services/AppUpdateService.cs",
            "src/DeskBox/Services/DeskBoxDataBackupService.cs",
            "src/DeskBox/Services/DeskBoxDiagnosticsBundleService.cs",
            "src/DeskBox/Services/DesktopOrganizationRecoveryStore.cs",
            "src/DeskBox/Services/GlanceImageService.cs",
            "src/DeskBox/Services/GlanceWidgetStore.cs",
            "src/DeskBox/Services/LocalizationService.cs",
            "src/DeskBox/Services/NativeNotificationActivationEnvelopeStore.cs",
            "src/DeskBox/Services/QuickCaptureStore.cs",
            "src/DeskBox/Services/SearchHistoryService.cs",
            "src/DeskBox/Services/SettingsService.cs",
            "src/DeskBox/Services/TodoWidgetStore.cs",
            "src/DeskBox/Services/WeatherService.cs",
            "src/DeskBox/Services/WidgetFileStackSettings.cs"
        ];
        string[] actualContextOwners = ProductionSourceFiles()
            .Where(path => File.ReadAllText(path).Contains(
                "JsonSerializerContext",
                StringComparison.Ordinal))
            .Select(RepositoryRelativePath)
            .Order()
            .ToArray();

        Assert.Equal(26, actualContextOwners.Length);
        Assert.Equal(expectedContextOwners, actualContextOwners);
    }

    [Fact]
    public void NonGenericStringEnumConverters_AreEliminated()
    {
        var converterPattern = new Regex(@"new\s+JsonStringEnumConverter\s*\(\s*\)");
        string[] actualOwners = ProductionSourceFiles()
            .Where(path => converterPattern.IsMatch(File.ReadAllText(path)))
            .Select(RepositoryRelativePath)
            .Order()
            .ToArray();
        int converterCount = ProductionSourceFiles()
            .Sum(path => converterPattern.Matches(File.ReadAllText(path)).Count);

        Assert.Empty(actualOwners);
        Assert.Equal(0, converterCount);
        Assert.DoesNotContain(
            ProductionSourceFiles(),
            path => File.ReadAllText(path).Contains("JsonStringEnumConverter<", StringComparison.Ordinal));
    }

    [Fact]
    public void PhaseOneLeafCalls_UseSourceGeneratedTypeInfoAndPreserveOptionProfiles()
    {
        string appUpdate = ReadSource("src/DeskBox/Services/AppUpdateService.cs");
        string citySearch = ReadSource("src/DeskBox/Services/CitySearchService.cs");
        string weather = ReadSource("src/DeskBox/Services/WeatherService.cs");
        string localization = ReadSource("src/DeskBox/Services/LocalizationService.cs");
        string diagnostics = ReadSource(
            "src/DeskBox/Services/DeskBoxDiagnosticsBundleService.cs");

        string[] expectedTypeInfoReferences =
        [
            "AppUpdateJsonContext.Default.UpdateManifest",
            "AppUpdateJsonContext.Default.GitHubRelease",
            "WeatherJsonContext.Default.PredefinedCityList",
            "WeatherJsonContext.Default.GeocodingResult",
            "WeatherJsonContext.Default.OpenMeteoWeather",
            "WeatherJsonContext.Default.MsnWeather",
            "LocalizationJsonContext.Default.LocalizedStrings",
            "DiagnosticsJsonContext.Default.DiagnosticSnapshot"
        ];
        string allPhaseOneSources = string.Join(
            Environment.NewLine,
            appUpdate,
            citySearch,
            weather,
            localization,
            diagnostics);

        foreach (string reference in expectedTypeInfoReferences)
        {
            Assert.Single(
                Regex.Matches(allPhaseOneSources, Regex.Escape(reference)).Cast<Match>());
        }

        Assert.DoesNotContain("JsonSerializerOptions", allPhaseOneSources, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new JsonStringEnumConverter()",
            allPhaseOneSources,
            StringComparison.Ordinal);

        Assert.Contains("JsonSerializerDefaults.Web", appUpdate, StringComparison.Ordinal);
        Assert.Contains("PropertyNameCaseInsensitive = true", appUpdate, StringComparison.Ordinal);
        Assert.Contains("PropertyNameCaseInsensitive = true", weather, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryKeyPolicy", localization, StringComparison.Ordinal);
        Assert.Contains(
            "PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase",
            diagnostics,
            StringComparison.Ordinal);
        Assert.Contains("UseStringEnumConverter = true", diagnostics, StringComparison.Ordinal);
        Assert.Contains("WriteIndented = true", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseTwoStoreCalls_UseSourceGeneratedTypeInfoAndPreserveOptionProfiles()
    {
        string settings = ReadSource("src/DeskBox/Services/SettingsService.cs");
        string quickCapture = ReadSource("src/DeskBox/Services/QuickCaptureStore.cs");
        string todo = ReadSource("src/DeskBox/Services/TodoWidgetStore.cs");
        string glancePreferences = ReadSource("src/DeskBox/Services/GlanceWidgetStore.cs");
        string glanceCatalog = ReadSource("src/DeskBox/Services/GlanceImageService.cs");
        string widgetMetadata = ReadSource(
            "src/DeskBox/Services/WidgetFileStackSettings.cs");
        string allPhaseTwoSources = string.Join(
            Environment.NewLine,
            settings,
            quickCapture,
            todo,
            glancePreferences,
            glanceCatalog,
            widgetMetadata);

        var expectedTypeInfoReferences = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SettingsJsonContext.Default.AppSettings"] = 2,
            ["QuickCaptureJsonContext.Default.StoreData"] = 2,
            ["TodoJsonContext.Default.StoreData"] = 2,
            ["GlancePreferencesJsonContext.Default.Preferences"] = 7,
            ["GlanceImageCatalogJsonContext.Default.ImageCatalog"] = 2,
            ["WidgetMetadataJsonContext.Default.StringMap"] = 2,
            ["WidgetMetadataJsonContext.Default.StringListMap"] = 2,
            ["WidgetMetadataJsonContext.Default.StringList"] = 3
        };
        foreach ((string reference, int expectedCount) in expectedTypeInfoReferences)
        {
            Assert.Equal(
                expectedCount,
                Regex.Matches(
                    allPhaseTwoSources,
                    Regex.Escape(reference) + @"\b").Count);
        }

        Assert.Equal(22, expectedTypeInfoReferences.Values.Sum());
        Assert.DoesNotContain("JsonSerializerOptions", allPhaseTwoSources, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "new JsonStringEnumConverter()",
            allPhaseTwoSources,
            StringComparison.Ordinal);

        foreach (string stringEnumStore in
                 new[] { settings, quickCapture, todo, glancePreferences })
        {
            Assert.Contains(
                "PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase",
                stringEnumStore,
                StringComparison.Ordinal);
            Assert.Contains("UseStringEnumConverter = true", stringEnumStore, StringComparison.Ordinal);
            Assert.Contains("WriteIndented = true", stringEnumStore, StringComparison.Ordinal);
        }

        Assert.Contains(
            "PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase",
            glanceCatalog,
            StringComparison.Ordinal);
        Assert.Contains("WriteIndented = true", glanceCatalog, StringComparison.Ordinal);
        Assert.DoesNotContain("UseStringEnumConverter", glanceCatalog, StringComparison.Ordinal);
        Assert.DoesNotContain("PropertyNamingPolicy", widgetMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain("DictionaryKeyPolicy", widgetMetadata, StringComparison.Ordinal);
        Assert.DoesNotContain("UseStringEnumConverter", widgetMetadata, StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseThreeASearchAndRecoveryCalls_UseSourceGeneratedTypeInfoAndPreserveOptionProfiles()
    {
        string searchHistory = ReadSource("src/DeskBox/Services/SearchHistoryService.cs");
        string desktopRecovery = ReadSource(
            "src/DeskBox/Services/DesktopOrganizationRecoveryStore.cs");
        string allPhaseThreeASources = string.Join(
            Environment.NewLine,
            searchHistory,
            desktopRecovery);

        var expectedTypeInfoReferences = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SearchHistoryJsonContext.Default.PersistedData"] = 2,
            ["DesktopRecoveryJsonContext.Default.RecoveryJournal"] = 2
        };
        foreach ((string reference, int expectedCount) in expectedTypeInfoReferences)
        {
            Assert.Equal(
                expectedCount,
                Regex.Matches(
                    allPhaseThreeASources,
                    Regex.Escape(reference) + @"\b").Count);
        }

        Assert.Equal(4, expectedTypeInfoReferences.Values.Sum());
        Assert.DoesNotContain("JsonSerializerOptions", allPhaseThreeASources, StringComparison.Ordinal);
        Assert.DoesNotContain("JsonStringEnumConverter", allPhaseThreeASources, StringComparison.Ordinal);

        foreach (string camelCaseSource in new[] { searchHistory, desktopRecovery })
        {
            Assert.Contains(
                "PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase",
                camelCaseSource,
                StringComparison.Ordinal);
            Assert.Contains(
                "GenerationMode = JsonSourceGenerationMode.Metadata",
                camelCaseSource,
                StringComparison.Ordinal);
        }

        Assert.Contains("WriteIndented = true", searchHistory, StringComparison.Ordinal);
        Assert.Contains("WriteIndented = true", desktopRecovery, StringComparison.Ordinal);
        Assert.DoesNotContain("UseStringEnumConverter", searchHistory, StringComparison.Ordinal);
        Assert.DoesNotContain("UseStringEnumConverter", desktopRecovery, StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseThreeBUserDataCalls_UseCompatibleSourceGeneratedTypeInfo()
    {
        string attachmentHealth = ReadSource(
            "src/DeskBox/Services/DeskBoxAttachmentHealthService.cs");
        string backup = ReadSource("src/DeskBox/Services/DeskBoxDataBackupService.cs");

        var expectedAttachmentTypeInfoReferences = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["s_quickCaptureDataJsonContext.StoreData"] = 1,
            ["s_todoDataJsonContext.StoreData"] = 1
        };
        var expectedBackupTypeInfoReferences = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["s_settingsDataJsonContext.AppSettings"] = 1,
            ["s_quickCaptureDataJsonContext.StoreData"] = 3,
            ["s_todoDataJsonContext.StoreData"] = 3
        };
        foreach ((string reference, int expectedCount) in expectedAttachmentTypeInfoReferences)
        {
            Assert.Equal(
                expectedCount,
                Regex.Matches(attachmentHealth, Regex.Escape(reference) + @"\b").Count);
        }

        foreach ((string reference, int expectedCount) in expectedBackupTypeInfoReferences)
        {
            Assert.Equal(
                expectedCount,
                Regex.Matches(backup, Regex.Escape(reference) + @"\b").Count);
        }

        Assert.Equal(
            9,
            expectedAttachmentTypeInfoReferences.Values.Sum() +
            expectedBackupTypeInfoReferences.Values.Sum());
        foreach (string source in new[] { attachmentHealth, backup })
        {
            Assert.Contains("WriteIndented = true", source, StringComparison.Ordinal);
            Assert.Contains(
                "PropertyNamingPolicy = JsonNamingPolicy.CamelCase",
                source,
                StringComparison.Ordinal);
            Assert.Contains("PropertyNameCaseInsensitive = true", source, StringComparison.Ordinal);
            Assert.DoesNotContain("JsonStringEnumConverter", source, StringComparison.Ordinal);
            Assert.Contains("JsonTypeInfo<T> jsonTypeInfo", source, StringComparison.Ordinal);
        }

        Assert.Contains(
            "JsonSerializer.Deserialize(File.ReadAllText(path), jsonTypeInfo)",
            attachmentHealth,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (JsonSerializer.Deserialize(json, jsonTypeInfo) is null)",
            backup,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseThreeCBackupControlCalls_UseSourceGeneratedTypeInfoAndPreserveOptionProfile()
    {
        string backup = ReadSource("src/DeskBox/Services/DeskBoxDataBackupService.cs");
        int contextStart = backup.IndexOf("[JsonSourceGenerationOptions(", StringComparison.Ordinal);
        Assert.True(contextStart >= 0);
        string backupContext = backup[contextStart..];

        var expectedTypeInfoReferences = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["BackupJsonContext.Default.BackupManifest"] = 3,
            ["BackupJsonContext.Default.PendingRestoreMarker"] = 3
        };
        foreach ((string reference, int expectedCount) in expectedTypeInfoReferences)
        {
            Assert.Equal(
                expectedCount,
                Regex.Matches(backup, Regex.Escape(reference) + @"\b").Count);
        }

        Assert.Equal(6, expectedTypeInfoReferences.Values.Sum());
        Assert.Contains("public sealed partial class DeskBoxDataBackupService", backup);
        Assert.Contains(
            "GenerationMode = JsonSourceGenerationMode.Metadata",
            backupContext,
            StringComparison.Ordinal);
        Assert.Contains(
            "PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase",
            backupContext,
            StringComparison.Ordinal);
        Assert.Contains("WriteIndented = true", backupContext, StringComparison.Ordinal);
        Assert.DoesNotContain("PropertyNameCaseInsensitive", backupContext, StringComparison.Ordinal);
        Assert.DoesNotContain("UseStringEnumConverter", backupContext, StringComparison.Ordinal);
        Assert.Contains(
            "typeof(DeskBoxBackupManifest),\r\n" +
            "        TypeInfoPropertyName = \"BackupManifest\"",
            backupContext.ReplaceLineEndings("\r\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "typeof(DeskBoxBackupFileManifest),\r\n" +
            "        TypeInfoPropertyName = \"BackupFileManifest\"",
            backupContext.ReplaceLineEndings("\r\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "typeof(PendingRestoreMarker),\r\n" +
            "        TypeInfoPropertyName = \"PendingRestoreMarker\"",
            backupContext.ReplaceLineEndings("\r\n"),
            StringComparison.Ordinal);
        Assert.DoesNotContain("s_jsonOptions", backup, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "JsonSerializer.DeserializeAsync<DeskBoxBackupManifest>",
            backup,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "JsonSerializer.Deserialize<PendingRestoreMarker>",
            backup,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GenericJsonHelpers_HaveFiniteFrozenTypeWhitelists()
    {
        string attachmentHealth = ReadSource(
            "src/DeskBox/Services/DeskBoxAttachmentHealthService.cs");
        string backup = ReadSource("src/DeskBox/Services/DeskBoxDataBackupService.cs");

        Assert.Equal(
            ["QuickCaptureStoreData", "TodoWidgetData"],
            GenericTypeArguments(attachmentHealth, "ReadJson"));
        Assert.Equal(
            ["AppSettings", "QuickCaptureStoreData", "TodoWidgetData"],
            GenericTypeArguments(backup, "ValidateJsonFileIfPresent"));
        Assert.Contains(
            "private static T ReadJson<T>(string path, JsonTypeInfo<T> jsonTypeInfo)",
            attachmentHealth,
            StringComparison.Ordinal);
        Assert.Contains(
            "private static void ValidateJsonFileIfPresent<T>(\r\n" +
            "        string path,\r\n" +
            "        JsonTypeInfo<T> jsonTypeInfo)",
            backup.ReplaceLineEndings("\r\n"),
            StringComparison.Ordinal);

        Assert.Single(
            Regex.Matches(
                backup,
                @"await\s+WritePendingRestoreMarkerAtomicallyAsync\s*\(").Cast<Match>());
        Assert.Contains(
            "private static async Task WritePendingRestoreMarkerAtomicallyAsync(",
            backup,
            StringComparison.Ordinal);
        Assert.Contains("var marker = new PendingRestoreMarker(", backup, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteJsonAtomicallyAsync", backup, StringComparison.Ordinal);
        Assert.DoesNotContain("WritePendingRestoreMarkerAtomicallyAsync<T>", backup, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchHistoryGolden_KeepsCamelCaseNumericEnumAndForwardCompatibility()
    {
        Directory.CreateDirectory(_tempRoot);
        string storePath = Path.Combine(_tempRoot, "search-history.json");
        var history = new SearchHistoryService(storePath);
        history.RecordResult(new SearchResultItem
        {
            Kind = SearchResultKind.Folder,
            Title = "Saved folder",
            DetailPath = @"C:\Saved"
        });

        using (JsonDocument saved = JsonDocument.Parse(File.ReadAllText(storePath)))
        {
            Assert.True(saved.RootElement.TryGetProperty("recentResults", out JsonElement results));
            Assert.False(saved.RootElement.TryGetProperty("RecentResults", out _));
            JsonElement result = Assert.Single(results.EnumerateArray());
            Assert.Equal(JsonValueKind.Number, result.GetProperty("kind").ValueKind);
            Assert.Equal((int)SearchResultKind.Folder, result.GetProperty("kind").GetInt32());
        }

        File.WriteAllText(
            storePath,
            """
            {
              "recentResults": [
                {
                  "identity": "folder|C:\\Saved",
                  "kind": 4,
                  "title": "Legacy folder",
                  "futureResultField": "ignored"
                }
              ],
              "futureRootField": { "enabled": true }
            }
            """);

        var reloaded = new SearchHistoryService(storePath);
        SearchRecommendationItem recent = Assert.Single(reloaded.RecentResults);
        Assert.Equal(SearchResultKind.Folder, recent.Kind);
        Assert.Equal("Legacy folder", recent.Title);
        Assert.Empty(reloaded.RecentQueries);
        Assert.Empty(reloaded.FavoriteQueries);
    }

    [Fact]
    public async Task DesktopRecoveryGolden_KeepsCamelCaseAndMissingUnknownFieldBehavior()
    {
        Directory.CreateDirectory(_tempRoot);
        string journalPath = Path.Combine(_tempRoot, "desktop-recovery.json");
        var store = new DesktopOrganizationRecoveryStore(journalPath);
        await store.SaveAsync(new DesktopOrganizationRecoveryJournal
        {
            TransactionId = "current",
            StartedAt = DateTimeOffset.Parse("2026-08-20T00:00:00Z"),
            CreatedWidgetIds = ["widget"],
            Items =
            [
                new DesktopOrganizationRecoveryItem
                {
                    SourcePath = @"C:\Source\one.txt",
                    DestinationPath = @"C:\Target\one.txt",
                    TargetWidgetId = "widget",
                    Completed = true
                }
            ]
        });

        string savedJson = await File.ReadAllTextAsync(journalPath);
        Assert.Contains("\n  \"transactionId\"", savedJson, StringComparison.Ordinal);
        using (JsonDocument saved = JsonDocument.Parse(savedJson))
        {
            Assert.True(saved.RootElement.TryGetProperty("createdWidgetIds", out _));
            Assert.False(saved.RootElement.TryGetProperty("CreatedWidgetIds", out _));
        }

        await File.WriteAllTextAsync(
            journalPath,
            """
            {
              "transactionId": "legacy",
              "items": [
                {
                  "sourcePath": "C:\\Source\\legacy.txt",
                  "destinationPath": "C:\\Target\\legacy.txt",
                  "targetWidgetId": "legacy-widget",
                  "completed": true,
                  "futureItemField": 1
                }
              ],
              "futureRootField": true
            }
            """);

        DesktopOrganizationRecoveryJournal loaded = Assert.IsType<DesktopOrganizationRecoveryJournal>(
            await store.LoadAsync());
        Assert.Equal("legacy", loaded.TransactionId);
        Assert.NotEqual(default, loaded.StartedAt);
        Assert.Empty(loaded.CreatedWidgetIds);
        Assert.True(Assert.Single(loaded.Items).Completed);
    }

    [Fact]
    public async Task StringEnumStoreGoldens_WriteNamesAndReadLegacyIntegers()
    {
        string quickDirectory = Path.Combine(_tempRoot, "quick");
        var quickStore = new QuickCaptureStore(quickDirectory);
        await quickStore.SaveAsync(new QuickCaptureStoreData
        {
            CurrentView = QuickCaptureViewMode.Pinned
        });

        using (JsonDocument quickJson = JsonDocument.Parse(
                   await File.ReadAllTextAsync(quickStore.StorePath)))
        {
            JsonElement currentView = quickJson.RootElement.GetProperty("currentView");
            Assert.Equal(JsonValueKind.String, currentView.ValueKind);
            Assert.Equal("Pinned", currentView.GetString());
            Assert.False(quickJson.RootElement.TryGetProperty("CurrentView", out _));
        }

        await File.WriteAllTextAsync(
            quickStore.StorePath,
            """
            {
              "currentView": 2,
              "futureRootField": "ignored"
            }
            """);
        QuickCaptureStoreData legacyQuick = await new QuickCaptureStore(quickDirectory).LoadAsync();
        Assert.Equal(QuickCaptureViewMode.Recent, legacyQuick.CurrentView);
        Assert.Equal(4, legacyQuick.Version);
        Assert.Empty(legacyQuick.Items);
        Assert.Empty(legacyQuick.RecentItems);

        var glanceStore = new GlanceWidgetStore(Path.Combine(_tempRoot, "glance"));
        await glanceStore.SaveAsync(new GlanceWidgetData
        {
            Layout = GlanceLayoutMode.Editorial,
            Transition = GlanceTransitionMode.SlideFade
        });

        using (JsonDocument glanceJson = JsonDocument.Parse(
                   await File.ReadAllTextAsync(glanceStore.StorePath)))
        {
            Assert.Equal("Editorial", glanceJson.RootElement.GetProperty("layout").GetString());
            Assert.Equal("SlideFade", glanceJson.RootElement.GetProperty("transition").GetString());
            Assert.Equal(JsonValueKind.String, glanceJson.RootElement.GetProperty("layout").ValueKind);
        }

        await File.WriteAllTextAsync(
            glanceStore.StorePath,
            """
            {
              "layout": 2,
              "transition": 2,
              "futurePreferenceField": true
            }
            """);
        GlanceWidgetData legacyGlance = await new GlanceWidgetStore(
            Path.Combine(_tempRoot, "glance")).LoadAsync();
        Assert.Equal(GlanceLayoutMode.Editorial, legacyGlance.Layout);
        Assert.Equal(GlanceTransitionMode.SlideFade, legacyGlance.Transition);
        Assert.Equal(GlanceWidgetData.CurrentVersion, legacyGlance.Version);
    }

    [Fact]
    public async Task GlanceImageCatalogGolden_ReadsNumericEnumsWithMissingAndUnknownFields()
    {
        string cacheDirectory = Path.Combine(_tempRoot, "glance-cache");
        string imageDirectory = Directory.CreateDirectory(
            Path.Combine(cacheDirectory, "images")).FullName;
        string imagePath = Path.Combine(imageDirectory, "legacy.jpg");
        await File.WriteAllBytesAsync(imagePath, [1, 2, 3]);
        string escapedImagePath = JsonSerializer.Serialize(imagePath);
        await File.WriteAllTextAsync(
            Path.Combine(cacheDirectory, "catalog.json"),
            $$"""
            [
              {
                "id": "legacy-cities",
                "localPath": {{escapedImagePath}},
                "cachedAtUtc": "2026-08-20T00:00:00+00:00",
                "onlineCategory": 2,
                "onlineProvider": 0,
                "futureCatalogField": { "ignored": true }
              }
            ]
            """);

        var service = new GlanceImageService(cacheDirectory);
        IReadOnlyList<GlanceImageInfo> images = await service.LoadCachedOnlineImagesAsync(
            GlanceOnlineImageProvider.Wikimedia,
            GlanceOnlineImageCategory.Cities);

        GlanceImageInfo image = Assert.Single(images);
        Assert.Equal("legacy-cities", image.Id);
        Assert.Equal(GlanceOnlineImageProvider.Wikimedia, image.OnlineProvider);
        Assert.Equal(GlanceOnlineImageCategory.Cities, image.OnlineCategory);
        Assert.Null(image.Title);
    }

    private static string[] GenericTypeArguments(string source, string methodName) =>
        Regex.Matches(source, $@"\b{Regex.Escape(methodName)}<(?<type>[^>]+)>")
            .Select(match => match.Groups["type"].Value)
            .Where(type => !string.Equals(type, "T", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToArray();

    private static IEnumerable<string> ProductionSourceFiles()
    {
        string projectDirectory = TestPaths.FromRepository("src/DeskBox");
        return Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path =>
            {
                string relative = Path.GetRelativePath(projectDirectory, path)
                    .Replace(Path.DirectorySeparatorChar, '/');
                return !relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) &&
                       !relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) &&
                       !relative.StartsWith("AppPackages/", StringComparison.OrdinalIgnoreCase);
            });
    }

    private static string RepositoryRelativePath(string path) =>
        Path.GetRelativePath(TestPaths.FromRepository("."), path)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static string ReadSource(string relativePath) =>
        File.ReadAllText(TestPaths.FromRepository(relativePath));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup for files briefly held by antivirus/indexing.
        }
    }
}
