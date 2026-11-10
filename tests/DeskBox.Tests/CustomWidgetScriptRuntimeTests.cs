using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetScriptRuntimeTests
{
    [Fact]
    public async Task ColdStart_OnInitPopulatesComputedScalarsAndProjectedCollections_WithoutPollutingStoreJson()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxScriptRuntime_" + Guid.NewGuid().ToString("N"));
        try
        {
            string settingsDir = Path.Combine(tempRoot, "settings");
            string widgetsDir = Path.Combine(tempRoot, "widgets");
            Directory.CreateDirectory(settingsDir);
            Directory.CreateDirectory(widgetsDir);

            var settingsService = new SettingsService(settingsDir);
            settingsService.Settings.Language = SettingsService.LanguageChinese;
            var localizationService = new LocalizationService(settingsService);

            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.calc-demo",
              "name": "计算器",
              "script": { "entry": "logic.js", "tickIntervalMs": 0 },
              "state": { "count": "3" },
              "capsule": {
                "title": "计次 ${$computed.doubleCount}",
                "summary": "${$computed.banner}",
                "glyph": "\uE8EF",
                "badge": "${$computed.doubleCount}",
                "primaryAction": {
                  "icon": "\uE710",
                  "tooltip": "加一",
                  "onClick": { "type": "script.run", "fn": "onIncrement", "arg": "2" }
                }
              },
              "view": {
                "type": "Stack",
                "children": [
                  { "type": "Text", "text": "${$computed.banner}" },
                  {
                    "type": "List",
                    "items": "$computed.rows",
                    "itemTemplate": { "type": "Text", "text": "${$item.label}: ${$item.val}" }
                  },
                  { "type": "Sparkline", "values": "${$computed.series}" }
                ]
              }
            }
            """;

            const string logicJs = """
            function refreshComputed(ctx) {
              const n = Number(ctx.state.count || '0');
              ctx.computed.doubleCount = n * 2;
              ctx.computed.banner = `当前倍数: ${n * 2}`;
              ctx.computed.rows = [
                { id: 'r1', label: '原值', val: n },
                { id: 'r2', label: '双倍', val: n * 2 }
              ];
              ctx.computed.series = [n, n * 2, n * 3];
            }

            export function onInit(ctx) {
              refreshComputed(ctx);
            }

            export function onIncrement(ctx) {
              const step = Number(ctx.event.arg || '1');
              const next = Number(ctx.state.count || '0') + step;
              ctx.state.count = String(next);
              refreshComputed(ctx);
            }
            """;

            var store = new CustomWidgetDataStore("w-calc", widgetsDir);
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs, locales: null);

            var config = new WidgetConfig
            {
                Id = "w-calc",
                Name = "计算器",
                WidgetKind = WidgetKind.Custom
            };

            using var vm = new CustomWidgetViewModel(config, localizationService, store);
            await vm.InitializeAsync();

            Assert.Null(vm.LastScriptError);
            CustomWidgetEvaluationContext evalCtx = vm.CreateEvaluationContext();
            Assert.Equal("当前倍数: 6", CustomWidgetExpressionEvaluator.Interpolate("${$computed.banner}", evalCtx));
            Assert.Equal("6", CustomWidgetExpressionEvaluator.Interpolate("${$computed.doubleCount}", evalCtx));

            IReadOnlyList<CustomWidgetRecord> projectedRows =
                CustomWidgetScriptEngine.ProjectComputedCollection("$computed.rows", vm.Computed);
            Assert.Equal(2, projectedRows.Count);
            Assert.Equal("双倍", projectedRows[1].GetField("label"));
            Assert.Equal("6", projectedRows[1].GetField("val"));

            IReadOnlyList<double> sparkValues =
                CustomWidgetExpressionEvaluator.ResolveHistory("${$computed.series}", evalCtx);
            Assert.Equal([3d, 6d, 9d], sparkValues);

            // Capsule projection + primaryAction
            vm.OnCompactStateChanged(collapsed: true);
            Assert.Equal("计次 6", vm.CapsuleTitle);
            Assert.Equal("当前倍数: 6", vm.CapsuleSummary);
            Assert.Equal("6", vm.CapsuleBadge);
            Assert.True(vm.CapsuleHasPrimaryAction);
            Assert.Equal("\uE710", vm.CapsulePrimaryActionGlyph);
            Assert.Equal("加一", vm.CapsulePrimaryActionTooltip);

            // Trigger capsule primaryAction -> invokes script.run(onIncrement, arg="2") -> count becomes 5, double becomes 10
            await vm.ExecuteCapsulePrimaryActionAsync();
            Assert.Null(vm.LastScriptError);
            Assert.Equal("5", vm.State["count"]);
            Assert.Equal("计次 10", vm.CapsuleTitle);
            Assert.Equal("当前倍数: 10", vm.CapsuleSummary);

            // Verify store.json persisted state.count="5", and NEVER wrote computed keys into store.json
            CustomWidgetStorePayload reloadedStore = await store.ReloadFromDiskAsync();
            Assert.Equal("5", reloadedStore.State["count"]);
            Assert.DoesNotContain("doubleCount", reloadedStore.State.Keys);
            Assert.DoesNotContain("banner", reloadedStore.State.Keys);
            string rawStoreJson = await File.ReadAllTextAsync(store.StorePath);
            Assert.DoesNotContain("doubleCount", rawStoreJson, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task LocalesDirectory_ResolvesInManifestAndScriptCtxT_AndUpdatesOnLanguageSwitch()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxLocales_" + Guid.NewGuid().ToString("N"));
        try
        {
            string settingsDir = Path.Combine(tempRoot, "settings");
            string widgetsDir = Path.Combine(tempRoot, "widgets");
            Directory.CreateDirectory(settingsDir);
            Directory.CreateDirectory(widgetsDir);

            var settingsService = new SettingsService(settingsDir);
            settingsService.Settings.Language = SettingsService.LanguageChinese;
            var localizationService = new LocalizationService(settingsService);

            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.i18n-demo",
              "name": "${$i18n.widgetName}",
              "script": { "entry": "logic.js" },
              "state": { "cups": "4" },
              "capsule": {
                "title": "${$i18n.widgetName}",
                "summary": "${$computed.summaryText}"
              },
              "view": {
                "type": "Stack",
                "children": [
                  { "type": "Text", "text": "${$i18n.header}" },
                  { "type": "Text", "text": "${$computed.summaryText}" }
                ]
              }
            }
            """;

            const string logicJs = """
            export function onInit(ctx) {
              ctx.computed.summaryText = ctx.t('drankFormat', { n: ctx.state.cups || '0' });
            }
            """;

            var locales = new CustomWidgetLocaleCatalog(
                new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["zh-CN"] = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["widgetName"] = "喝水助手",
                        ["header"] = "今日饮水",
                        ["drankFormat"] = "已喝 {n} 杯"
                    },
                    ["en-US"] = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["widgetName"] = "Hydration Buddy",
                        ["header"] = "Today's Water",
                        ["drankFormat"] = "{n} cups logged"
                    }
                });

            CustomWidgetParseResult validation = CustomWidgetManifestParser.ValidateWithRuntimeHints(
                manifestJson,
                logicJs,
                locales);
            Assert.True(validation.IsValid, string.Join("; ", validation.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));
            Assert.DoesNotContain(validation.Diagnostics, d => d.Message.Contains("i18n", StringComparison.OrdinalIgnoreCase));

            var store = new CustomWidgetDataStore("w-i18n", widgetsDir);
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs, locales: locales);

            var config = new WidgetConfig
            {
                Id = "w-i18n",
                Name = "喝水助手",
                WidgetKind = WidgetKind.Custom
            };

            using var vm = new CustomWidgetViewModel(config, localizationService, store);
            await vm.InitializeAsync();
            vm.OnCompactStateChanged(collapsed: true);

            Assert.Equal("喝水助手", vm.CapsuleTitle);
            Assert.Equal("已喝 4 杯", vm.CapsuleSummary);
            Assert.Equal("今日饮水", CustomWidgetExpressionEvaluator.Interpolate("${$i18n.header}", vm.CreateEvaluationContext()));

            // Switch language to English -> sidecar i18n + logic.js ctx.t(...) update immediately
            localizationService.SetLanguage(SettingsService.LanguageEnglish);

            Assert.Equal("Hydration Buddy", vm.CapsuleTitle);
            Assert.Equal("4 cups logged", vm.CapsuleSummary);
            Assert.Equal("Today's Water", CustomWidgetExpressionEvaluator.Interpolate("${$i18n.header}", vm.CreateEvaluationContext()));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ScriptRuntimeError_CapturesLineNumberAndClearsWhenFixedInPreviewBundle()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxScriptErr_" + Guid.NewGuid().ToString("N"));
        try
        {
            string settingsDir = Path.Combine(tempRoot, "settings");
            Directory.CreateDirectory(settingsDir);

            var settingsService = new SettingsService(settingsDir);
            var localizationService = new LocalizationService(settingsService);
            var config = new WidgetConfig
            {
                Id = "w-preview",
                Name = "Preview",
                WidgetKind = WidgetKind.Custom
            };

            using var vm = new CustomWidgetViewModel(
                config,
                localizationService,
                store: new CustomWidgetDataStore("w-preview", tempRoot),
                isPreviewOnly: true);

            CustomWidgetScriptRuntimeError? observedError = null;
            vm.ScriptErrorChanged += err => observedError = err;

            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.err-test",
              "name": "报错测试",
              "script": { "entry": "logic.js" },
              "view": {
                "type": "Stack",
                "children": [
                  {
                    "type": "Button",
                    "text": "触发",
                    "onClick": { "type": "script.run", "fn": "onBrokenClick" }
                  }
                ]
              }
            }
            """;

            const string brokenJs = """
            export function onInit(ctx) {
              ctx.computed.ok = true;
            }
            export function onBrokenClick(ctx) {
              const obj = null;
              ctx.computed.bad = obj.missingProp;
            }
            """;

            vm.ApplyPreviewBundle(manifestJson, brokenJs);
            Assert.Null(vm.LastScriptError);

            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec(Type: "script.run", Fn: "onBrokenClick")]);

            Assert.NotNull(vm.LastScriptError);
            Assert.NotNull(observedError);
            Assert.Equal("onBrokenClick", vm.LastScriptError!.FunctionName);
            Assert.Equal(6, vm.LastScriptError.ErrorLine);

            // Apply fixed script -> clears LastScriptError
            const string fixedJs = """
            export function onInit(ctx) {
              ctx.computed.ok = true;
            }
            export function onBrokenClick(ctx) {
              ctx.computed.bad = 'fixed';
            }
            """;

            vm.ApplyPreviewBundle(manifestJson, fixedJs);
            Assert.Null(vm.LastScriptError);
            Assert.Null(observedError);

            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec(Type: "script.run", Fn: "onBrokenClick")]);
            Assert.Null(vm.LastScriptError);
            Assert.Equal("fixed", CustomWidgetExpressionEvaluator.Interpolate("${$computed.bad}", vm.CreateEvaluationContext()));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
