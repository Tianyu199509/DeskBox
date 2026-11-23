using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DeskBox.Controls.WidgetContents;
using DeskBox.Helpers;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetTemplateAuditRegressionTests
{
    private static readonly string[] AllSupportedCultures =
    [
        "ar-SA", "bn-BD", "de-DE", "en-US", "es-ES", "fr-FR",
        "hi-IN", "ja-JP", "ko-KR", "pt-BR", "ru-RU", "zh-CN", "zh-TW"
    ];

    private static readonly Regex CjkHanRegex = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);

    [Fact]
    public void OfficialTemplates_AllFourteenAcrossAllThirteenLocales_MatchGalleryTitlesAndLeakZeroRawExpressionsOrChinese()
    {
        foreach (string templateId in CustomWidgetTemplates.BuiltInTemplateIds)
        {
            foreach (string culture in AllSupportedCultures)
            {
                CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(templateId, culture);
                Assert.NotNull(bundle.Locales);
                CustomWidgetManifest? parsed = CustomWidgetManifestParser.Parse(bundle.ManifestJson).Manifest;
                Assert.NotNull(parsed);
                CustomWidgetManifest localized = parsed.WithLocales(bundle.Locales!);

                string manifestTitle = localized.ResolveLocalizedName(culture);
                string? galleryTitle = CustomWidgetTemplates.ResolveOfficialLocalizedName(templateId, culture);

                Assert.False(string.IsNullOrWhiteSpace(manifestTitle), $"{templateId} ({culture}) resolved blank title.");
                Assert.Equal(manifestTitle, galleryTitle);
                Assert.DoesNotContain("${", manifestTitle, StringComparison.Ordinal);

                if (!culture.StartsWith("zh", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(culture, "ja-JP", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.False(
                        CjkHanRegex.IsMatch(manifestTitle),
                        $"{templateId} ({culture}) title '{manifestTitle}' leaked CJK characters.");
                }
            }
        }
    }

    [Fact]
    public void WorldClockSettings_ResolveAllTimezoneCityExpressionsPerCulture()
    {
        CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(
            CustomWidgetTemplates.WorldClockTemplateId,
            "en-US");
        CustomWidgetManifest parsed = CustomWidgetManifestParser.Parse(bundle.ManifestJson).Manifest!;
        CustomWidgetManifest localized = parsed.WithLocales(bundle.Locales!);

        foreach (string culture in new[] { "zh-CN", "en-US", "ja-JP", "fr-FR", "ar-SA" })
        {
            IReadOnlyList<CustomWidgetSettingDefinition> settings = localized.ResolveLocalizedSettings(culture);
            foreach (CustomWidgetSettingDefinition def in settings)
            {
                Assert.DoesNotContain("${", def.Label, StringComparison.Ordinal);
                Assert.DoesNotContain("${", def.Description, StringComparison.Ordinal);
                foreach (CustomWidgetOptionSpec opt in def.Options)
                {
                    Assert.False(string.IsNullOrWhiteSpace(opt.Label));
                    Assert.DoesNotContain("${", opt.Label, StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void AutoCompileIfLocalized_PreservesScriptReferencedKeysAcrossAllOfficialTemplates()
    {
        foreach (string templateId in CustomWidgetTemplates.BuiltInTemplateIds)
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(templateId);
            if (string.IsNullOrWhiteSpace(bundle.LogicJs) || bundle.Locales is null)
            {
                continue;
            }

            IReadOnlyDictionary<string, string> zhBefore = bundle.Locales.Locales["zh-CN"];
            _ = CustomWidgetI18nEngine.AutoCompileIfLocalized(
                bundle.ManifestJson,
                bundle.LogicJs,
                bundle.Locales,
                "zh-CN",
                out CustomWidgetLocaleCatalog? updatedCatalog);

            Assert.NotNull(updatedCatalog);
            IReadOnlyDictionary<string, string> zhAfter = updatedCatalog!.Locales["zh-CN"];
            foreach (string key in zhBefore.Keys)
            {
                Assert.True(
                    zhAfter.ContainsKey(key),
                    $"AutoCompileIfLocalized dropped locale key '{key}' from '{templateId}'.");
            }
        }
    }

    [Fact]
    public async Task PackageServiceAndProtocolActivation_DefaultToDbxwidgetAndSupportLegacyFallbacks()
    {
        Assert.Equal(".dbxwidget", CustomWidgetPackageService.SharePackageExtension);
        Assert.Contains(".dbxwidget", CustomWidgetPackageService.SupportedImportExtensions, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(".json", CustomWidgetPackageService.SupportedImportExtensions, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(".deskbox-widget", CustomWidgetPackageService.SupportedImportExtensions, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(".dbbox", CustomWidgetPackageService.SupportedImportExtensions, StringComparer.OrdinalIgnoreCase);

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-dbxwidget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.FocusDialTemplateId);
            string dbxPath = Path.Combine(tempDir, "focus-dial.dbxwidget");
            await CustomWidgetPackageService.ExportSharePackageFromBundleAsync(
                bundle.ManifestJson,
                bundle.LogicJs,
                bundle.Locales,
                dbxPath,
                sanitizeForSharing: true);

            Assert.True(File.Exists(dbxPath));

            string? extractedUri = DeskBoxProtocolActivationService.TryExtractProtocolUri(["DeskBox.exe", dbxPath]);
            Assert.NotNull(extractedUri);
            Assert.True(DeskBoxProtocolActivationService.TryParseProtocolUri(extractedUri, out DeskBoxProtocolIntent? intent, out string? error), error);
            Assert.Equal(Path.GetFullPath(dbxPath), intent!.LocalPackageFilePath);

            CustomWidgetImportedBundle importedDbx = await CustomWidgetPackageService.ReadPackageBundleAsync(dbxPath);
            Assert.Equal("deskbox.focus-dial", importedDbx.Manifest.Id);
            Assert.NotNull(importedDbx.Locales);

            string legacyPath = Path.Combine(tempDir, "focus-dial.deskbox-widget");
            File.Copy(dbxPath, legacyPath);
            await Assert.ThrowsAsync<NotSupportedException>(() => CustomWidgetPackageService.ReadPackageBundleAsync(legacyPath));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AudioPlayAndSetVolume_TreatsIntegerPercentStringsDeterministicallyAndNoOneHundredPercentBlastAtOnePercent()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-vol-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.FocusDialTemplateId);
            var store = new CustomWidgetDataStore("vol-percent-test", tempDir);
            await store.WriteWorkspaceBundleAsync(bundle.ManifestJson, bundle.LogicJs, locales: bundle.Locales);

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var locSvc = new LocalizationService(settingsSvc);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "vol-percent-test", Name = "Focus Dial" },
                locSvc,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vm.InitializeAsync();

            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions(
                    """[{ "type": "audio.play", "trackId": "ambient", "src": "noise:rain", "volume": "1" }]"""));
            Assert.Equal(0.01, vm.AudioService.GetTrackVolume("ambient"), precision: 3);

            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions(
                    """[{ "type": "audio.setVolume", "trackId": "ambient", "value": "70" }]"""));
            Assert.Equal(0.70, vm.AudioService.GetTrackVolume("ambient"), precision: 3);

            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions(
                    """[{ "type": "audio.stop", "trackId": "ambient" }]"""));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AiCredentialStore_NonDestructiveWithUpdate_PreservesCustomEndpointModelThinkingAndModels()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-cred-preserve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "openai",
                ApiKey: "sk-initial-key",
                Region: "global",
                PlanTier: "plus",
                Enabled: true,
                CustomEndpoint: "https://api.example-proxy.com/v1",
                CustomModel: "gpt-4.1-mini",
                ThinkingMode: "off",
                FetchedModels: ["gpt-4.1-mini", "gpt-4.1"]));

            AiProviderCredentialEntry existing = credStore.Get("openai")!;
            credStore.Upsert(existing with { ApiKey = "sk-rotated-key" });

            AiProviderCredentialEntry updated = credStore.Get("openai")!;
            Assert.Equal("sk-rotated-key", updated.ApiKey);
            Assert.Equal("https://api.example-proxy.com/v1", updated.CustomEndpoint);
            Assert.Equal("gpt-4.1-mini", updated.CustomModel);
            Assert.Equal("off", updated.ThinkingMode);
            Assert.Equal(["gpt-4.1-mini", "gpt-4.1"], updated.FetchedModels);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void PackagedFileActivation_HandlesDbxwidgetPathsContainingSpacesOnColdAndWarmStart()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"DeskBox Spaced User {Guid.NewGuid():N}");
        string subDir = Path.Combine(tempRoot, "My Downloaded Widgets");
        Directory.CreateDirectory(subDir);
        try
        {
            string pkgPath = Path.Combine(subDir, "Focus Dial Pro v2.dbxwidget");
            File.WriteAllText(pkgPath, "{}");

            string? builtUri = DeskBoxProtocolActivationService.BuildImportWidgetFileUri(pkgPath);
            Assert.NotNull(builtUri);
            Assert.StartsWith("deskbox://import-widget-file?", builtUri, StringComparison.Ordinal);
            Assert.DoesNotContain(' ', builtUri!);

            string? extractedFromUri = DeskBoxProtocolActivationService.TryExtractProtocolUri([], builtUri);
            Assert.Equal(builtUri, extractedFromUri);

            string? extractedFromRawSpacedPath = DeskBoxProtocolActivationService.TryExtractProtocolUri([], pkgPath);
            Assert.NotNull(extractedFromRawSpacedPath);
            Assert.True(DeskBoxProtocolActivationService.TryParseProtocolUri(
                extractedFromRawSpacedPath,
                out DeskBoxProtocolIntent? intent,
                out string? err), err);
            Assert.Equal(Path.GetFullPath(pkgPath), intent!.LocalPackageFilePath);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Win10AndWin11GlyphNormalization_NeverEmitsMissingFontCodepoints()
    {
        string[] nonExistentPua = ["\uE9C4", "\uE9C0", "\uE91D", "\uEAFD", "\uE9CB"];
        foreach (string bad in nonExistentPua)
        {
            string win11 = CustomWidgetNativeRenderer.NormalizeFluentGlyphForOs(bad, isWindows11OrLater: true);
            string win10 = CustomWidgetNativeRenderer.NormalizeFluentGlyphForOs(bad, isWindows11OrLater: false);
            Assert.DoesNotContain(win11, nonExistentPua);
            Assert.DoesNotContain(win10, nonExistentPua);
        }

        Assert.Equal("\uE890", CustomWidgetNativeRenderer.NormalizeFluentGlyphForOs("\uED1A", isWindows11OrLater: false));
        Assert.Equal("\uE8AB", CustomWidgetNativeRenderer.NormalizeFluentGlyphForOs("\uE7C2", isWindows11OrLater: false));

        for (int wmo = 0; wmo <= 99; wmo++)
        {
            string dayGlyph = WeatherCodeMapper.GetGlyph(wmo, isDay: true);
            string nightGlyph = WeatherCodeMapper.GetGlyph(wmo, isDay: false);
            Assert.DoesNotContain(dayGlyph, nonExistentPua);
            Assert.DoesNotContain(nightGlyph, nonExistentPua);
        }
    }

    [Fact]
    public void HalfAndQuarterHourIanaTimezones_ResolveDeterministicallyInExpressions()
    {
        var ctx = new CustomWidgetEvaluationContext
        {
            CultureName = "en-US"
        };

        foreach (string zone in new[] { "Asia/Kathmandu", "Asia/Kolkata", "Asia/Tehran", "America/St_Johns" })
        {
            string formatted = CustomWidgetExpressionEvaluator.Interpolate($"${{tz('{zone}', 'HH:mm', '0')}}", ctx);
            Assert.Matches(@"^\d{2}:\d{2}$", formatted);
        }
    }

    [Fact]
    public async Task FocusDialColdStartReset_AndGlobalAmbientAudioPreemption_PreventSilentStuckUiAndDualNoise()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-audio-arb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.FocusDialTemplateId);
            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var locSvc = new LocalizationService(settingsSvc);

            var storeA = new CustomWidgetDataStore("focus-a", tempDir);
            await storeA.WriteWorkspaceBundleAsync(bundle.ManifestJson, bundle.LogicJs, locales: bundle.Locales);
            await storeA.SaveAsync(new CustomWidgetStorePayload
            {
                State = new Dictionary<string, string>(StringComparer.Ordinal) { ["noiseMode"] = "rain" }
            });

            using var vmA = new CustomWidgetViewModel(
                new WidgetConfig { Id = "focus-a", Name = "Focus A" },
                locSvc,
                store: storeA,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vmA.InitializeAsync();

            // Cold start with persisted noiseMode="rain" while silent must reset to "off"
            Assert.Equal("off", vmA.CreateEvaluationContext().State["noiseMode"]);

            var storeB = new CustomWidgetDataStore("focus-b", tempDir);
            await storeB.WriteWorkspaceBundleAsync(bundle.ManifestJson, bundle.LogicJs, locales: bundle.Locales);
            using var vmB = new CustomWidgetViewModel(
                new WidgetConfig { Id = "focus-b", Name = "Focus B" },
                locSvc,
                store: storeB,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vmB.InitializeAsync();

            bool startedA = CustomWidgetAudioService.Shared.PlayTrack("focus-a:ambient", "noise:rain", 0.5, loop: true, ambientOwnerId: "focus-a");
            if (startedA)
            {
                await vmA.ExecuteActionsAsync(
                    CustomWidgetManifestParser.ParseDispatchedActions(
                        """[{ "type": "state.set", "key": "noiseMode", "value": "rain" }]"""));
                Assert.Equal("rain", vmA.CreateEvaluationContext().State["noiseMode"]);

                // Starting ambient audio on owner B preempts owner A and resets vmA's noiseMode to "off"
                _ = CustomWidgetAudioService.Shared.PlayTrack("focus-b:ambient", "noise:pink", 0.5, loop: true, ambientOwnerId: "focus-b");
                Assert.Equal("off", vmA.CreateEvaluationContext().State["noiseMode"]);
            }
        }
        finally
        {
            CustomWidgetAudioService.Shared.StopTracksForOwner("focus-a");
            CustomWidgetAudioService.Shared.StopTracksForOwner("focus-b");
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task DirtyInputState_ReprojectsComputedBeforeOnSubmitActionInterpolation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-devtool-dirty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.dirty-input-reproject",
              "name": "Dirty Input Reproject",
              "script": { "entry": "logic.js", "tickIntervalMs": 0 },
              "state": { "draft": "", "result": "" },
              "root": "main",
              "elements": {
                "main": { "type": "Text", "text": "${state.result}" }
              }
            }
            """;
            const string logicJs = """
            export function onInit(ctx) {
              ctx.computed.trimmedDraft = String((ctx.state && ctx.state.draft) || '').trim();
            }
            """;
            var store = new CustomWidgetDataStore("devtool-dirty", tempDir);
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs);

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var locSvc = new LocalizationService(settingsSvc);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "devtool-dirty", Name = "Dirty Input Reproject" },
                locSvc,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vm.InitializeAsync();

            // Simulate user typing into Input (SetStateValueWithoutRebuild) then immediately pressing Enter
            vm.SetStateValueWithoutRebuild("draft", "  {\"hello\":1}  ");
            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions(
                    """[{ "type": "text.transform", "format": "jsonPretty", "text": "${$computed.trimmedDraft}", "key": "result" }]"""));

            string output = vm.CreateEvaluationContext().State.GetValueOrDefault("result") ?? string.Empty;
            Assert.Contains("\"hello\"", output, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task LiveTodoViewModelExternalMutations_PreserveDetailSelectionAndAdvanceRecurrence()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-todo-ext-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings.json"));
            var locSvc = new LocalizationService(settingsSvc);
            var todoStore = new TodoWidgetStore(Path.Combine(tempDir, "widgets"));
            var config = new WidgetConfig { Id = "todo-live-1", Name = "Todo", WidgetKind = WidgetKind.Todo };
            var vm = new TodoWidgetViewModel(todoStore, locSvc, config, settingsSvc);
            await vm.InitializeAsync();

            Assert.True(await vm.AddExternalItemAsync("Keep detail open", DateTimeOffset.UtcNow, TodoPriority.High));
            Assert.True(await vm.AddExternalItemAsync("Daily standup", DateTimeOffset.UtcNow.Date, TodoPriority.Medium));

            TodoItemViewModel openDetailTarget = vm.Items.First(i => i.Text == "Keep detail open");
            TodoItemViewModel recurringTarget = vm.Items.First(i => i.Text == "Daily standup");
            Assert.True(await vm.SetRecurrenceAsync(recurringTarget.Id, TodoRecurrenceMode.Daily));
            Assert.Same(openDetailTarget, vm.OpenDetail(openDetailTarget.Id));

            Assert.True(await vm.ToggleExternalItemAsync(recurringTarget.Id));

            // Detail pane on unrelated item stays open, and recurring task spawned its next occurrence!
            Assert.Same(openDetailTarget, vm.SelectedDetailItem);
            Assert.True(recurringTarget.IsCompleted);
            Assert.Equal(3, vm.Items.Count);
            Assert.Contains(vm.Items, i => !i.IsCompleted && i.Text == "Daily standup" && i.Id != recurringTarget.Id);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
