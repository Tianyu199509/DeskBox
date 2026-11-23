using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetOfficialTemplateCleanlinessTests
{
    private static readonly Regex CjkCharacterRegex = new(@"[\u4e00-\u9fff]", RegexOptions.Compiled);
    private static readonly Regex SegoeFluentPuaRegex = new(
        @"[\uE000-\uF8FF]|\\u[Ee][0-9A-Fa-f]{3}|\\u[Ff][0-8][0-9A-Fa-f]{2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MechanicalElementIdRegex = new(
        @"^(?:grid|card|row|col|stack|txt|btn|ibtn|badge|prog|spark|wrap|scroll|zstack|list|inp|sel|chk|tog|exp|sep|div|el|n|node)(?:_\d+)?$|_\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    [Fact]
    public void AllBuiltInTemplates_UseCanonicalIdsZeroDiagnosticsZeroDomDriftAndCompleteLocales()
    {
        IReadOnlyList<string> ids = CustomWidgetTemplates.BuiltInTemplateIds;
        Assert.Equal(14, ids.Count);
        Assert.Equal(14, CustomWidgetTemplates.Presets.Count);

        Assert.Equal(
            ids.OrderBy(static x => x, StringComparer.Ordinal),
            CustomWidgetTemplates.Presets.Select(static p => p.Id).OrderBy(static x => x, StringComparer.Ordinal));
        foreach (string starterId in new[]
                 {
                     CustomWidgetTemplates.HydrationStarterId,
                     CustomWidgetTemplates.AiInboxStarterId,
                     CustomWidgetTemplates.NewsRssStarterId,
                     CustomWidgetTemplates.AgentHudTemplateId
                 })
        {
            Assert.Contains(starterId, ids, StringComparer.Ordinal);
        }

        var domDriftErrors = new List<string>();
        foreach (string templateId in ids)
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(templateId);
            Assert.False(string.IsNullOrWhiteSpace(bundle.ManifestJson), $"Missing manifestJson for '{templateId}'.");
            Assert.NotNull(bundle.Locales);
            Assert.False(
                CjkCharacterRegex.IsMatch(bundle.ManifestJson),
                $"Template '{templateId}' raw manifest.json must not contain hardcoded CJK strings; move all user-facing strings to locales/*.json.");
            Assert.False(
                SegoeFluentPuaRegex.IsMatch(bundle.ManifestJson),
                $"Template '{templateId}' raw manifest.json must not contain raw or escaped Segoe Fluent PUA glyphs; use semantic icon names instead.");
            if (!string.IsNullOrWhiteSpace(bundle.LogicJs))
            {
                Assert.False(
                    SegoeFluentPuaRegex.IsMatch(bundle.LogicJs),
                    $"Template '{templateId}' logic.js must not contain raw or escaped Segoe Fluent PUA glyphs; use semantic icon names instead.");
            }

            using (JsonDocument rawDoc = JsonDocument.Parse(bundle.ManifestJson))
            {
                JsonElement rawRoot = rawDoc.RootElement;
                Assert.True(
                    rawRoot.TryGetProperty("schemaVersion", out JsonElement svEl) &&
                    svEl.ValueKind == JsonValueKind.Number &&
                    svEl.GetInt32() == 1,
                    $"Template '{templateId}' must declare \"schemaVersion\": 1.");
                Assert.False(
                    rawRoot.TryGetProperty("view", out _),
                    $"Template '{templateId}' must not contain legacy top-level \"view\".");
                Assert.True(
                    rawRoot.TryGetProperty("root", out JsonElement rootIdEl) &&
                    rootIdEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(rootIdEl.GetString()),
                    $"Template '{templateId}' must declare a non-empty top-level string \"root\".");
                Assert.True(
                    rawRoot.TryGetProperty("elements", out JsonElement elementsMapEl) &&
                    elementsMapEl.ValueKind == JsonValueKind.Object,
                    $"Template '{templateId}' must declare a top-level \"elements\" object map.");
                foreach (JsonProperty elemProp in elementsMapEl.EnumerateObject())
                {
                    Assert.False(
                        MechanicalElementIdRegex.IsMatch(elemProp.Name),
                        $"Template '{templateId}' element ID '{elemProp.Name}' looks mechanical or auto-numbered; use semantic element IDs.");
                }
            }

            var workspace = new StudioWorkspaceEngine(bundle.ManifestJson, bundle.LogicJs, bundle.Locales);
            string beforeNorm = workspace.GetManifestJson(indented: true)!;
            bool mutatedByNormalizer = workspace.NormalizeDomInPlace();
            if (mutatedByNormalizer)
            {
                string afterNorm = workspace.GetManifestJson(indented: true)!;
                string[] bLines = beforeNorm.Replace("\r\n", "\n").Split('\n');
                string[] aLines = afterNorm.Replace("\r\n", "\n").Split('\n');
                int diffLine = 0;
                while (diffLine < bLines.Length && diffLine < aLines.Length && bLines[diffLine] == aLines[diffLine])
                {
                    diffLine++;
                }
                string beforeCtx = diffLine < bLines.Length ? bLines[diffLine].Trim() : "<eof>";
                string afterCtx = diffLine < aLines.Length ? aLines[diffLine].Trim() : "<eof>";
                domDriftErrors.Add($"{templateId}@L{diffLine + 1}: '{beforeCtx}' -> '{afterCtx}'");
            }

            CustomWidgetWorkspaceValidationResult validation = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
                bundle.ManifestJson,
                bundle.LogicJs,
                bundle.Locales);

            Assert.True(
                validation.IsValid,
                $"Template '{templateId}' failed validation: {string.Join("; ", validation.Diagnostics.Select(d => $"{d.Path}: {d.Message}"))}");
            Assert.True(
                validation.Diagnostics.Count == 0,
                $"Template '{templateId}' must have 0 diagnostics (including warnings), found: {string.Join("; ", validation.Diagnostics.Select(d => $"{d.Path}: {d.Message}"))}");

            CustomWidgetCompilerResult compiled = CustomWidgetCompilerPipeline.Compile(
                bundle.ManifestJson,
                bundle.LogicJs,
                bundle.Locales,
                runOnInitDryRun: true);
            Assert.Empty(compiled.HardDiagnostics);
            if (compiled.ActionableSoftDiagnostics.Count > 0)
            {
                domDriftErrors.Add(
                    $"{templateId} [softDiag]: " +
                    string.Join("; ", compiled.ActionableSoftDiagnostics.Select(d => $"{d.Code}@{d.Path}: {d.Message}")));
            }

            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(bundle.ManifestJson);
            Assert.NotNull(parsed.Manifest);
            Assert.Equal(1, parsed.Manifest!.SchemaVersion);
            Assert.Equal($"deskbox.{templateId}", parsed.Manifest.Id);
            Assert.Equal("1.0.0", parsed.Manifest.Version);
            Assert.Equal("DeskBox", parsed.Manifest.Author);
            IReadOnlyList<CustomWidgetValidationDiagnostic> smokeFindings =
                StudioBehavioralSmokeVerifier.Verify(parsed.Manifest, bundle.ManifestJson, bundle.LogicJs, bundle.Locales, null);
            Assert.DoesNotContain(smokeFindings, static d => d.IsError);
            Assert.Equal("${$i18n.widgetName}", parsed.Manifest.Name);
            Assert.Equal("${$i18n.widgetDescription}", parsed.Manifest.Description);
            Assert.True(
                CustomWidgetTemplates.IsEmojiIcon(parsed.Manifest.Icon),
                $"Template '{templateId}' top-level icon '{parsed.Manifest.Icon}' must be a pure Emoji (no PUA glyph).");
            if (parsed.Manifest.Capsule is { } capsule &&
                !string.IsNullOrWhiteSpace(capsule.Glyph) &&
                !capsule.Glyph.Contains("${", StringComparison.Ordinal))
            {
                Assert.True(
                    CustomWidgetTemplates.IsEmojiIcon(capsule.Glyph),
                    $"Template '{templateId}' static capsule.glyph '{capsule.Glyph}' must be an Emoji.");
            }

            Assert.StartsWith("DeskBox", parsed.Manifest.Author, StringComparison.Ordinal);
            Assert.True(CustomWidgetManifestMetadataStamper.ShouldAllowAutomaticOfficialUpgrade(bundle.ManifestJson));
            string stampedJson = CustomWidgetManifestMetadataStamper.StampProvenance(
                bundle.ManifestJson,
                origin: CustomWidgetOriginKinds.Official,
                isCustomized: false);
            CustomWidgetParseResult stampedParsed = CustomWidgetManifestParser.Parse(stampedJson);
            Assert.NotNull(stampedParsed.Manifest);
            Assert.Equal(CustomWidgetOriginKinds.Official, stampedParsed.Manifest!.Origin);

            CustomWidgetLocaleCatalog locales = bundle.Locales!;
            Assert.Equal(13, locales.Locales.Count);
            Assert.NotNull(locales.Meta);

            string baseLocaleCode = locales.Meta!.BaseLocale;
            Assert.True(
                locales.Locales.TryGetValue(baseLocaleCode, out IReadOnlyDictionary<string, string>? baseEntries),
                $"Template '{templateId}' is missing baseLocale '{baseLocaleCode}' dictionary.");

            var baseKeySet = new HashSet<string>(baseEntries!.Keys, StringComparer.Ordinal);
            var metaKeySet = new HashSet<string>(locales.Meta.Hashes.Keys, StringComparer.Ordinal);
            Assert.True(
                baseKeySet.SetEquals(metaKeySet),
                $"Template '{templateId}' _meta.json hashes key set differs from '{baseLocaleCode}.json' " +
                $"(missing: [{string.Join(", ", baseKeySet.Except(metaKeySet))}], " +
                $"orphan: [{string.Join(", ", metaKeySet.Except(baseKeySet))}]).");

            foreach ((string key, string baseVal) in baseEntries)
            {
                Assert.Equal(
                    CustomWidgetLocaleMeta.ComputeTextHash(baseVal),
                    locales.Meta.Hashes[key]);
            }

            HashSet<string>? baselineKeys = null;
            foreach ((string culture, IReadOnlyDictionary<string, string> entries) in locales.Locales)
            {
                Assert.True(
                    entries.TryGetValue("widgetName", out string? widgetName) && !string.IsNullOrWhiteSpace(widgetName),
                    $"Template '{templateId}' locale '{culture}' is missing non-empty 'widgetName'.");
                Assert.True(
                    entries.TryGetValue("widgetDescription", out string? widgetDesc) && !string.IsNullOrWhiteSpace(widgetDesc),
                    $"Template '{templateId}' locale '{culture}' is missing non-empty 'widgetDescription'.");

                var currentKeys = new HashSet<string>(entries.Keys, StringComparer.Ordinal);
                if (baselineKeys is null)
                {
                    baselineKeys = currentKeys;
                }
                else
                {
                    Assert.True(
                        baselineKeys.SetEquals(currentKeys),
                        $"Template '{templateId}' locale '{culture}' key set differs from baseline.");
                }

                foreach ((string key, string baseVal) in baseEntries)
                {
                    if (entries.TryGetValue(key, out string? localizedVal))
                    {
                        Assert.True(
                            CustomWidgetI18nEngine.HasMatchingPlaceholders(baseVal, localizedVal),
                            $"Template '{templateId}' locale '{culture}' key '{key}' has mismatched positional placeholders: " +
                            $"base='{baseVal}' vs localized='{localizedVal}'.");
                    }
                }
            }
        }

        Assert.True(
            domDriftErrors.Count == 0,
            "Templates not at AST canonical fixed point:\n" + string.Join("\n", domDriftErrors));
    }

    [Fact]
    public void AllBuiltInTemplates_ManifestJsonHasNoLegacyKeysOrStringlyScalarDefaults()
    {
        foreach (string templateId in CustomWidgetTemplates.BuiltInTemplateIds)
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(templateId);
            using JsonDocument doc = JsonDocument.Parse(bundle.ManifestJson);
            JsonElement root = doc.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);

            foreach (string forbiddenRootKey in new[] { "glyph", "backdrop", "i18n", "locales", "code", "view" })
            {
                Assert.False(
                    root.TryGetProperty(forbiddenRootKey, out _),
                    $"Template '{templateId}' still contains legacy root key '{forbiddenRootKey}'.");
            }

            if (root.TryGetProperty("popovers", out JsonElement popoversEl))
            {
                Assert.Equal(JsonValueKind.Object, popoversEl.ValueKind);
                foreach (JsonProperty popoverProp in popoversEl.EnumerateObject())
                {
                    Assert.False(
                        popoverProp.Value.TryGetProperty("view", out _) ||
                        popoverProp.Value.TryGetProperty("children", out _) ||
                        popoverProp.Value.TryGetProperty("child", out _),
                        $"Template '{templateId}' popover '{popoverProp.Name}' must use flat 'root' instead of legacy nested body.");
                    Assert.True(
                        popoverProp.Value.TryGetProperty("root", out JsonElement popRootEl) &&
                        popRootEl.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrWhiteSpace(popRootEl.GetString()),
                        $"Template '{templateId}' popover '{popoverProp.Name}' must declare a non-empty string 'root'.");
                    AssertNoLegacyVisibleProperty(templateId, $"$.popovers.{popoverProp.Name}", popoverProp.Value);
                }
            }

            if (root.TryGetProperty("elements", out JsonElement elementsEl))
            {
                Assert.Equal(JsonValueKind.Object, elementsEl.ValueKind);
                foreach (JsonProperty elemProp in elementsEl.EnumerateObject())
                {
                    AssertNoLegacyVisibleProperty(templateId, $"$.elements.{elemProp.Name}", elemProp.Value);
                }
            }

            if (root.TryGetProperty("settings", out JsonElement settingsEl))
            {
                Assert.Equal(JsonValueKind.Array, settingsEl.ValueKind);
                foreach (JsonElement setting in settingsEl.EnumerateArray())
                {
                    string settingKey = setting.TryGetProperty("key", out JsonElement keyEl) && keyEl.ValueKind == JsonValueKind.String
                        ? keyEl.GetString() ?? "?"
                        : "?";
                    string settingType = setting.TryGetProperty("type", out JsonElement typeEl) && typeEl.ValueKind == JsonValueKind.String
                        ? typeEl.GetString() ?? string.Empty
                        : string.Empty;

                    if (!setting.TryGetProperty("default", out JsonElement defaultEl))
                    {
                        continue;
                    }

                    if (string.Equals(settingType, "toggle", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.True(
                            defaultEl.ValueKind is JsonValueKind.True or JsonValueKind.False,
                            $"Template '{templateId}' toggle setting '{settingKey}' must use native JSON boolean default, got {defaultEl.ValueKind}.");
                    }
                    else if (string.Equals(settingType, "number", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.Equal(JsonValueKind.Number, defaultEl.ValueKind);
                    }
                }
            }
        }
    }

    [Fact]
    public void AllBuiltInTemplates_LogicJsSidecarExistsOnlyWhenReferencedByManifest()
    {
        foreach (string templateId in CustomWidgetTemplates.BuiltInTemplateIds)
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(templateId);
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(bundle.ManifestJson);
            Assert.NotNull(parsed.Manifest);

            if (!string.IsNullOrWhiteSpace(bundle.LogicJs))
            {
                Assert.NotNull(parsed.Manifest!.Script);
                bool referencesScriptOrComputed =
                    bundle.ManifestJson.Contains("$computed.", StringComparison.Ordinal) ||
                    bundle.ManifestJson.Contains("script.run", StringComparison.Ordinal);
                Assert.True(
                    referencesScriptOrComputed,
                    $"Template '{templateId}' ships logic.js but never references '$computed.*' or 'script.run' in manifest.json.");
            }
        }

        foreach (string scriptlessId in new[]
        {
            CustomWidgetTemplates.AiPulseTemplateId,
            CustomWidgetTemplates.CountdownTemplateId
        })
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(scriptlessId);
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(bundle.ManifestJson);
            Assert.Null(bundle.LogicJs);
            Assert.NotNull(parsed.Manifest);
            Assert.Null(parsed.Manifest!.Script);
        }
    }

    [Fact]
    public void ManifestParser_InheritsOptionsViaOptionsFromBetweenSettings()
    {
        const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "deskbox.test.options-from",
              "name": "OptionsFrom Test",
              "version": "1.0.0",
              "settings": [
                {
                  "key": "city1",
                  "type": "select",
                  "label": "Primary City",
                  "default": "Asia/Shanghai",
                  "options": [
                    { "value": "Asia/Shanghai", "label": "Shanghai" },
                    { "value": "America/New_York", "label": "New York" }
                  ]
                },
                {
                  "key": "city2",
                  "type": "select",
                  "label": "Secondary City",
                  "default": "UTC",
                  "options": [
                    { "value": "UTC", "label": "UTC" }
                  ],
                  "optionsFrom": "city1"
                }
              ],
              "root": "city_select",
              "elements": {
                "city_select": {
                  "type": "Select",
                  "bind": "$settings.city2"
                }
              }
            }
            """;

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
        Assert.True(parsed.IsValid);
        Assert.Empty(parsed.Diagnostics);
        Assert.NotNull(parsed.Manifest);

        CustomWidgetSettingDefinition city2 = Assert.Single(parsed.Manifest!.Settings, static s => s.Key == "city2");
        Assert.Equal(["UTC", "Asia/Shanghai", "America/New_York"], city2.Options.Select(static o => o.Value).ToArray());
        Assert.Equal(["UTC", "Asia/Shanghai", "America/New_York"], parsed.Manifest.View.Options.Select(static o => o.Value).ToArray());
    }

    [Fact]
    public void ManifestParser_InheritsOptionsFromBoundSettingWhenControlOmitsInlineOptions()
    {
        const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "deskbox.test.bound-options",
              "name": "Bound Options Test",
              "version": "1.0.0",
              "settings": [
                {
                  "key": "unit",
                  "type": "select",
                  "label": "Unit",
                  "default": "c",
                  "options": [
                    { "value": "c", "label": "C" },
                    { "value": "f", "label": "F" }
                  ]
                }
              ],
              "root": "main",
              "popovers": {
                "prefs": {
                  "title": "Preferences",
                  "width": 260,
                  "root": "prefs_body"
                }
              },
              "elements": {
                "main": {
                  "type": "Stack",
                  "children": ["unit_select"]
                },
                "unit_select": {
                  "type": "Select",
                  "bind": "$settings.unit"
                },
                "prefs_body": {
                  "type": "Stack",
                  "children": ["unit_segmented"]
                },
                "unit_segmented": {
                  "type": "Segmented",
                  "bind": "$settings.unit"
                }
              }
            }
            """;

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
        Assert.True(parsed.IsValid);
        Assert.Empty(parsed.Diagnostics);
        Assert.NotNull(parsed.Manifest);

        CustomWidgetNodeSpec viewSelect = Assert.Single(parsed.Manifest!.View.Children);
        Assert.Equal("Select", viewSelect.Type);
        Assert.Equal(2, viewSelect.Options.Count);
        Assert.Equal(["c", "f"], viewSelect.Options.Select(o => o.Value).ToArray());

        CustomWidgetNodeSpec popover = Assert.Single(parsed.Manifest.Popovers);
        CustomWidgetNodeSpec popoverBody = Assert.Single(popover.Children);
        CustomWidgetNodeSpec popoverSegmented = Assert.Single(popoverBody.Children);
        Assert.Equal("Segmented", popoverSegmented.Type);
        Assert.Equal(2, popoverSegmented.Options.Count);
        Assert.Equal(["c", "f"], popoverSegmented.Options.Select(o => o.Value).ToArray());
    }

    [Fact]
    public void AllBuiltInTemplates_HaveBalancedRootTopPaddingAndFullyLocalizedSettings()
    {
        foreach (string templateId in CustomWidgetTemplates.BuiltInTemplateIds)
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(templateId);
            CustomWidgetManifest manifest = CustomWidgetManifestParser.Parse(bundle.ManifestJson).Manifest!
                .WithLocales(bundle.Locales);

            Microsoft.UI.Xaml.Thickness pad = DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer
                .ResolveRootSafePadding(manifest.View.Padding);
            Assert.True(pad.Left > 0, $"Template '{templateId}' should have positive horizontal root padding.");
            Assert.Equal(pad.Left, pad.Right);
            Assert.Equal(pad.Left, pad.Top);

            foreach (string culture in new[] { "zh-CN", "en-US" })
            {
                IReadOnlyList<CustomWidgetSettingDefinition> localized = manifest.ResolveLocalizedSettings(culture);
                foreach (CustomWidgetSettingDefinition def in localized)
                {
                    Assert.DoesNotContain("$i18n", def.Label, StringComparison.OrdinalIgnoreCase);
                    if (!string.IsNullOrEmpty(def.Placeholder))
                    {
                        Assert.DoesNotContain("$i18n", def.Placeholder, StringComparison.OrdinalIgnoreCase);
                    }
                    foreach (CustomWidgetOptionSpec opt in def.Options)
                    {
                        Assert.DoesNotContain("$i18n", opt.Label, StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task FocusDialAndAudioContract_ParsesTrackIdSrcAndCueAliasesAndPlaysDistinctAmbientLoops()
    {
        byte[] rainWav = CustomWidgetAudioService.BuildAmbientNoiseWav("rain");
        byte[] brownWav = CustomWidgetAudioService.BuildAmbientNoiseWav("brown");
        Assert.True(rainWav.Length > 44);
        Assert.Equal(rainWav.Length, brownWav.Length);
        Assert.False(rainWav.AsSpan().SequenceEqual(brownWav), "Rain and brown ambient loops must be distinct.");

        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"deskbox-focus-dial-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.FocusDialTemplateId);
            var store = new CustomWidgetDataStore("focus-dial-test", tempDir);
            await store.WriteWorkspaceBundleAsync(bundle.ManifestJson, bundle.LogicJs, locales: bundle.Locales);

            var settingsSvc = new SettingsService(System.IO.Path.Combine(tempDir, "settings.json"));
            var locSvc = new LocalizationService(settingsSvc);
            using var vm = new DeskBox.ViewModels.CustomWidgetViewModel(
                new WidgetConfig { Id = "focus-dial-test", Name = "Focus Dial" },
                locSvc,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vm.InitializeAsync();

            CustomWidgetNodeSpec soundGrid = vm.Manifest.View.Children[1];
            Assert.Equal("Grid", soundGrid.Type);
            Assert.Equal(4, soundGrid.Children.Count);

            CustomWidgetEvaluationContext initialCtx = vm.CreateEvaluationContext();
            Assert.Equal("30", initialCtx.State["durationMin"]);
            Assert.Equal("off", initialCtx.State["noiseMode"]);
            Assert.True(initialCtx.Computed.ContainsKey("dialShapes"));

            await vm.ExecuteActionsAsync(
                soundGrid.Children[0].OnClick,
                vm.CreateEvaluationContext());

            Assert.True(vm.AudioService.IsTrackPlaying("ambient"));
            Assert.False(vm.AudioService.IsTrackPlaying("sub"));
            Assert.Equal(0.70, vm.AudioService.GetTrackVolume("ambient"), precision: 2);

            await vm.ExecuteActionsAsync(
                soundGrid.Children[3].OnClick,
                vm.CreateEvaluationContext());

            Assert.True(vm.AudioService.IsTrackPlaying("ambient"));
            Assert.True(vm.AudioService.IsTrackPlaying("sub"));
            Assert.Equal(0.70, vm.AudioService.GetTrackVolume("ambient"), precision: 2);
            Assert.Equal(0.34, vm.AudioService.GetTrackVolume("sub"), precision: 2);

            CustomWidgetNodeSpec mixerPopover = Assert.Single(
                vm.Manifest.Popovers,
                static p => string.Equals(p.Id, "mixer", StringComparison.OrdinalIgnoreCase));
            CustomWidgetNodeSpec mixerSlider = mixerPopover.Children[0].Children[0].Children
                .First(static n => string.Equals(n.Type, "Slider", StringComparison.OrdinalIgnoreCase));
            await vm.ExecuteActionsAsync(mixerSlider.OnChange, vm.CreateEvaluationContext().WithEventValue("35"));

            Assert.Equal("35", vm.State["level"]);
            Assert.Equal(0.35, vm.AudioService.GetTrackVolume("ambient"), precision: 2);
            Assert.Equal(0.17, vm.AudioService.GetTrackVolume("sub"), precision: 2);

            CustomWidgetNodeSpec card = Assert.Single(
                vm.Manifest.View.Children,
                static n => string.Equals(n.Type, "Card", StringComparison.OrdinalIgnoreCase));
            CustomWidgetNodeSpec dialZStack = Assert.Single(
                card.Children,
                static n => string.Equals(n.Type, "ZStack", StringComparison.OrdinalIgnoreCase));
            CustomWidgetNodeSpec interactiveCanvas = Assert.Single(
                dialZStack.Children,
                static n => string.Equals(n.Type, "Canvas", StringComparison.OrdinalIgnoreCase) && n.OnPointerDown.Count > 0);
            await vm.ExecuteActionsAsync(
                interactiveCanvas.OnPointerDown,
                vm.CreateEvaluationContext().WithPointerEvent("dial", 104, 28, 0.5, 0.14, isPressed: true));
            await vm.ExecuteActionsAsync(
                interactiveCanvas.OnPointerUp,
                vm.CreateEvaluationContext().WithPointerEvent("dial", 104, 28, 0.5, 0.14, isPressed: false));
            Assert.Equal("45", vm.State["durationMin"]);
            Assert.Equal("tick", vm.AudioService.LastPlayedCue);

            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions("""[{ "type": "audio.cue", "cue": "bell", "volume": "50" }]"""));
            Assert.Equal("bell", vm.AudioService.LastPlayedCue);

            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions("""[{ "type": "script.run", "fn": "toggleMute" }]"""));
            Assert.Equal("off", vm.State["noiseMode"]);
            Assert.False(vm.AudioService.IsTrackPlaying("ambient"));
            Assert.False(vm.AudioService.IsTrackPlaying("sub"));
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AdaptiveSparklineAndBackdrop_UsesFixedWidthColumnsBottomBaselineAndGridDotModes()
    {
        int narrowSlots = DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.ComputeSparklineSlotCount(140);
        int wideSlots = DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.ComputeSparklineSlotCount(300);
        Assert.True(wideSlots > narrowSlots);

        const double width = 150.0;
        const double height = 28.0;
        double bottomY = height - 1.0;

        var emptyResult = DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer
            .ComputeAdaptiveSparklinePoints([], width, height, 0, 100);
        Assert.All(emptyResult.LinePoints, pt => Assert.Equal(bottomY, pt.Y, precision: 3));
        Assert.Empty(emptyResult.AreaPoints);
        Assert.Equal(bottomY, emptyResult.CursorPoint.Y, precision: 3);

        var twoSamples = DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer
            .ComputeAdaptiveSparklinePoints([62.1, 62.3], width, height, 0, 100);
        var fiveSamples = DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer
            .ComputeAdaptiveSparklinePoints([60.0, 61.0, 62.0, 62.1, 62.3], width, height, 0, 100);

        // Column count and horizontal step density stay constant as data accumulates.
        Assert.Equal(twoSamples.LinePoints.Length, fiveSamples.LinePoints.Length);
        double step2 = twoSamples.LinePoints[1].X - twoSamples.LinePoints[0].X;
        double step5 = fiveSamples.LinePoints[1].X - fiveSamples.LinePoints[0].X;
        Assert.Equal(step2, step5, precision: 4);

        // Leading empty columns remain flat on the bottom baseline, and tiny 0.2% variance does not swing full-height.
        int slotCount = twoSamples.LinePoints.Length;
        for (int i = 0; i < slotCount - 2; i++)
        {
            Assert.Equal(bottomY, twoSamples.LinePoints[i].Y, precision: 3);
        }
        double deltaY = Math.Abs(twoSamples.LinePoints[^1].Y - twoSamples.LinePoints[^2].Y);
        Assert.True(deltaY < 1.0, $"Tiny 0.2% delta should not swing full height (got deltaY={deltaY}).");

        // Backdrop modes: grid, dots, lines, none
        Assert.Equal("grid", DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.NormalizeSparklineBackdropMode("grid", true));
        Assert.Equal("dots", DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.NormalizeSparklineBackdropMode("dots", true));
        Assert.Equal("lines", DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.NormalizeSparklineBackdropMode("lines", true));
        Assert.Equal("none", DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.NormalizeSparklineBackdropMode("none", true));
        Assert.Equal("none", DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.NormalizeSparklineBackdropMode("grid", false));
    }

    [Fact]
    public async System.Threading.Tasks.Task SystemPulseModuleReorderAndSixDotGrip_InvalidatesRetainedTreeAndUsesEqualPitchCircles()
    {
        (Windows.Foundation.Point[] centers, double dotRadius, Windows.Foundation.Size bounds) =
            DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer.ComputeSixDotGripSpec();
        Assert.Equal(6, centers.Length);
        Assert.True(dotRadius > 0.8 && dotRadius < 2.0);
        Assert.True(bounds.Width > 0 && bounds.Height > 0);

        double dx = centers[1].X - centers[0].X;
        double dy1 = centers[2].Y - centers[0].Y;
        double dy2 = centers[4].Y - centers[2].Y;
        Assert.Equal(dx, dy1, precision: 4);
        Assert.Equal(dx, dy2, precision: 4);

        string tempDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"deskbox-sys-pulse-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(tempDir);
        try
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.SystemPulseTemplateId);
            var store = new CustomWidgetDataStore("sys-pulse-reorder", tempDir);
            await store.WriteWorkspaceBundleAsync(bundle.ManifestJson, bundle.LogicJs, locales: bundle.Locales);
            var settingsSvc = new SettingsService(System.IO.Path.Combine(tempDir, "settings.json"));
            var locSvc = new LocalizationService(settingsSvc);
            using var vm = new DeskBox.ViewModels.CustomWidgetViewModel(
                new WidgetConfig { Id = "sys-pulse-reorder", Name = "System Pulse" },
                locSvc,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vm.InitializeAsync();

            CustomWidgetSettingDefinition modulesDef = Assert.Single(vm.Manifest.Settings, s => s.Key == "modules");
            var renderer = new DeskBox.Controls.WidgetContents.CustomWidgetNativeRenderer(vm);
            renderer.RegisterModuleStackInvalidationPatcher(
                "modules",
                modulesDef.DefaultValue,
                vm.Settings["modules"],
                markRetainedRootForTest: true);
            Assert.True(renderer.TryPatchRetainedTree());

            string reordered = CustomWidgetModuleListHelper.ReorderEnabledModule(
                vm.Settings["modules"],
                modulesDef.Options,
                "gpu",
                0);
            Assert.StartsWith("gpu:1,cpu:1,", reordered, StringComparison.Ordinal);
            await vm.SetSettingValueAsync("modules", reordered);

            // Changing module order must force TryPatchRetainedTree() to return false so ModuleStack rebuilds!
            Assert.False(renderer.TryPatchRetainedTree());
        }
        finally
        {
            try { System.IO.Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void AuthoringPromptsAndCapabilityCatalog_NeverTeachUnknownBuiltInRuntimeTokens()
    {
        var promptBlocks = new (string Label, string Content)[]
        {
            ("ArchetypeCatalog(zh)", CustomWidgetArchetypeCatalog.SelectPromptBlueprintBlock(null, "新建组件", isChinese: true)),
            ("ArchetypeCatalog(en)", CustomWidgetArchetypeCatalog.SelectPromptBlueprintBlock(null, "Create widget", isChinese: false)),
            ("CapabilityRegistry.Tier0(zh,host)", CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock(isChinese: true, includeHostStyleRules: true)),
            ("CapabilityRegistry.Tier0(zh,plain)", CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock(isChinese: true, includeHostStyleRules: false)),
            ("CapabilityRegistry.Tier0(en,host)", CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock(isChinese: false, includeHostStyleRules: true)),
            ("CapabilityRegistry.Tier0(en,plain)", CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock(isChinese: false, includeHostStyleRules: false)),
        };

        var pipeTokenPattern = new System.Text.RegularExpressions.Regex(
            @"(?<ns>\$(?:sys|weather|calendar|device|music|todo|pomodoro|pomo|countdown|ai|clock|drives|clipboard))\.(?<list>[A-Za-z0-9_.]+(?:\|[A-Za-z0-9_.]+)*)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        var violations = new List<string>();
        foreach ((string label, string content) in promptBlocks)
        {
            Assert.False(string.IsNullOrWhiteSpace(content), $"Expected non-empty prompt block for {label}.");
            foreach (System.Text.RegularExpressions.Match match in pipeTokenPattern.Matches(content))
            {
                string ns = match.Groups["ns"].Value;
                string[] parts = match.Groups["list"].Value.Split('|', StringSplitOptions.RemoveEmptyEntries);
                foreach (string rawPart in parts)
                {
                    string part = rawPart.TrimEnd('.');
                    if (string.IsNullOrEmpty(part))
                    {
                        continue;
                    }

                    var diagnostics = new List<CustomWidgetValidationDiagnostic>();
                    CustomWidgetRuntimeTokens.ValidateBuiltInTokensInExpression($"${{{ns}.{part}}}", label, diagnostics);
                    foreach (CustomWidgetValidationDiagnostic diag in diagnostics.Where(static d => d.IsError))
                    {
                        violations.Add($"{label}: {diag.Message}");
                    }
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "Authoring prompts taught unknown built-in runtime tokens:\n" + string.Join('\n', violations));
    }

    [Fact]
    public void AllFourteenOfficialTemplates_ValidateCleanlyWithDryRunAndNeverExportDeadOnSettingsChangedHook()
    {
        Assert.Equal(14, CustomWidgetTemplates.BuiltInTemplateIds.Count);
        foreach (string id in CustomWidgetTemplates.BuiltInTemplateIds)
        {
            CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(id);
            Assert.True(
                CustomWidgetScriptValidator.ValidateWorkspaceBundle(
                    bundle.ManifestJson,
                    bundle.LogicJs,
                    bundle.Locales,
                    runOnInitDryRun: true).IsValid,
                $"Official template '{id}' failed dry-run validation.");
            if (!string.IsNullOrWhiteSpace(bundle.LogicJs))
            {
                Assert.DoesNotContain("function onSettingsChanged", bundle.LogicJs, StringComparison.Ordinal);
            }
        }
    }

    private static void AssertNoLegacyVisibleProperty(string templateId, string path, JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        Assert.False(
            node.TryGetProperty("visible", out _),
            $"Template '{templateId}' node '{path}' still uses legacy 'visible' instead of 'when'.");

        if (node.TryGetProperty("children", out JsonElement childrenEl) && childrenEl.ValueKind == JsonValueKind.Array)
        {
            int idx = 0;
            foreach (JsonElement child in childrenEl.EnumerateArray())
            {
                AssertNoLegacyVisibleProperty(templateId, $"{path}.children[{idx}]", child);
                idx++;
            }
        }

        foreach (string nestedProp in new[] { "itemTemplate", "actionButton", "pane" })
        {
            if (node.TryGetProperty(nestedProp, out JsonElement nestedEl) && nestedEl.ValueKind == JsonValueKind.Object)
            {
                AssertNoLegacyVisibleProperty(templateId, $"{path}.{nestedProp}", nestedEl);
            }
        }
    }
}
