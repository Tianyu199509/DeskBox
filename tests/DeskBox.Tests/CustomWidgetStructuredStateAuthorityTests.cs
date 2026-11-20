using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetStructuredStateAuthorityTests
{
    [Fact]
    public void FlatView_PreservesNumericAndBooleanTypes_WhenHostEnginesWriteStrings()
    {
        var json = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["remainingSec"] = CustomWidgetScriptEngine.CreateNumberJsonElement(1500),
            ["enabled"] = CustomWidgetScriptEngine.CreateBooleanJsonElement(true)
        };
        var view = new CustomWidgetStructuredStateView(json);

        view["remainingSec"] = "1499";
        view["enabled"] = "false";

        Assert.Equal(JsonValueKind.Number, json["remainingSec"].ValueKind);
        Assert.Equal(1499, json["remainingSec"].GetInt32());
        Assert.Equal(JsonValueKind.False, json["enabled"].ValueKind);
        Assert.Equal("1499", view["remainingSec"]);
        Assert.Equal("false", view["enabled"]);
    }

    [Fact]
    public void FlatView_AllowsTypeChange_WhenWrittenValueIsNotNumeric()
    {
        var json = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["counter"] = CustomWidgetScriptEngine.CreateNumberJsonElement(5)
        };
        var view = new CustomWidgetStructuredStateView(json);

        view["counter"] = "paused";

        Assert.Equal(JsonValueKind.String, json["counter"].ValueKind);
        Assert.Equal("paused", view["counter"]);
    }

    [Fact]
    public void FlatView_ExposesDictionarySemantics_OverStructuredStore()
    {
        var json = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["alpha"] = CustomWidgetScriptEngine.CreateNumberJsonElement(1),
            ["beta"] = CustomWidgetScriptEngine.CreateStringJsonElement("two")
        };
        var view = new CustomWidgetStructuredStateView(json);

        Assert.Equal(2, view.Count);
        Assert.True(view.ContainsKey("alpha"));
        Assert.True(view.TryGetValue("beta", out string? beta));
        Assert.Equal("two", beta);
        Assert.False(view.TryGetValue("missing", out _));
        Assert.Throws<KeyNotFoundException>(() => view["missing"]);

        var pairs = new List<KeyValuePair<string, string>>(view);
        Assert.Equal(2, pairs.Count);
        Assert.Contains(pairs, p => p.Key == "alpha" && p.Value == "1");

        Assert.True(view.Remove("alpha"));
        Assert.False(view.ContainsKey("alpha"));
        view.Clear();
        Assert.Empty(json);
    }

    [Fact]
    public async Task HostStateSet_OverNumericManifestDefault_KeepsNativeNumber()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-state-authority-" + Guid.NewGuid().ToString("N"));
        try
        {
            string settingsDir = Path.Combine(tempRoot, "settings");
            string widgetsDir = Path.Combine(tempRoot, "widgets");
            Directory.CreateDirectory(settingsDir);
            Directory.CreateDirectory(widgetsDir);

            var settingsService = new SettingsService(settingsDir);
            var localizationService = new LocalizationService(settingsService);

            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.state-authority",
              "name": "State Authority",
              "state": { "counter": 5, "jobs": [] },
              "root": "root",
              "elements": {
                "root": { "type": "Text", "text": "${state.counter}" }
              }
            }
            """;

            var store = new CustomWidgetDataStore("custom_state_authority", widgetsDir);
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs: null, locales: null);

            var config = new WidgetConfig
            {
                Id = "custom_state_authority",
                Name = "State Authority",
                WidgetKind = WidgetKind.Custom
            };

            using var vm = new CustomWidgetViewModel(config, localizationService, store);
            await vm.InitializeAsync();

            Assert.Equal(JsonValueKind.Number, vm.StateJson["counter"].ValueKind);

            await vm.ExecuteActionsAsync(
            [
                new CustomWidgetActionSpec("state.set", Key: "counter", Value: "6")
            ]);

            Assert.Equal(JsonValueKind.Number, vm.StateJson["counter"].ValueKind);
            Assert.Equal(6, vm.StateJson["counter"].GetInt32());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task PreviewManifestEdit_PreservesStructuredRuntimeState()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-state-preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            string settingsDir = Path.Combine(tempRoot, "settings");
            string widgetsDir = Path.Combine(tempRoot, "widgets");
            Directory.CreateDirectory(settingsDir);
            Directory.CreateDirectory(widgetsDir);

            var settingsService = new SettingsService(settingsDir);
            var localizationService = new LocalizationService(settingsService);

            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.state-preview",
              "name": "State Preview",
              "state": { "jobs": [], "stats": { "done": 0 } },
              "root": "root",
              "elements": {
                "root": { "type": "Text", "text": "${state.jobs.length}" }
              }
            }
            """;

            var store = new CustomWidgetDataStore("custom_state_preview", widgetsDir);
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs: null, locales: null);

            var config = new WidgetConfig
            {
                Id = "custom_state_preview",
                Name = "State Preview",
                WidgetKind = WidgetKind.Custom
            };

            using var vm = new CustomWidgetViewModel(config, localizationService, store);
            await vm.InitializeAsync();

            using JsonDocument jobsDoc = JsonDocument.Parse("""[{ "name": "clip.mp4", "progress": 40 }]""");
            await vm.ExecuteActionsAsync(
            [
                new CustomWidgetActionSpec("state.set", Key: "jobs", RawJsonValue: jobsDoc.RootElement.Clone())
            ]);

            Assert.Equal(JsonValueKind.Array, vm.StateJson["jobs"].ValueKind);

            vm.ApplyPreviewManifestJson(manifestJson, preserveRuntimeData: true);

            Assert.Equal(JsonValueKind.Array, vm.StateJson["jobs"].ValueKind);
            Assert.Equal(1, vm.StateJson["jobs"].GetArrayLength());
            Assert.Equal("clip.mp4", vm.StateJson["jobs"][0].GetProperty("name").GetString());

            CustomWidgetEvaluationContext evalCtx = vm.CreateEvaluationContext();
            Assert.Equal(
                "1",
                CustomWidgetExpressionEvaluator.Interpolate("${state.jobs.length}", evalCtx));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CollectionStructuredJsonFields_RoundTripThroughStoreQuickJsActionsAndExpressions()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-coll-json-" + Guid.NewGuid().ToString("N"));
        try
        {
            string settingsDir = Path.Combine(tempRoot, "settings");
            string widgetsDir = Path.Combine(tempRoot, "widgets");
            Directory.CreateDirectory(settingsDir);
            Directory.CreateDirectory(widgetsDir);

            var settingsService = new SettingsService(settingsDir);
            var localizationService = new LocalizationService(settingsService);

            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.collection-json-roundtrip",
              "name": "Collection JSON Roundtrip",
              "script": { "entry": "logic.js", "tickIntervalMs": 0 },
              "collections": {
                "tasks": { "fields": ["title", "done", "priority", "tags", "meta"] }
              },
              "root": "root",
              "elements": {
                "root": { "type": "Text", "text": "${$computed.summary}" }
              }
            }
            """;

            const string logicJs = """
            export function onInit(ctx) {
              const items = ctx.collections.get("tasks");
              const openHigh = items.filter((t) => t.done === false && t.priority >= 2).length;
              ctx.computed.summary = String(openHigh);
            }

            export function toggleTask(ctx) {
              const id = String((ctx.event && ctx.event.arg) || "");
              const item = ctx.collections.get("tasks").find((t) => t.id === id);
              if (item) {
                ctx.collections.update("tasks", id, {
                  done: !item.done,
                  priority: item.priority + 1
                });
              }
            }
            """;

            var store = new CustomWidgetDataStore("custom_coll_json", widgetsDir);
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs, locales: null);

            var config = new WidgetConfig
            {
                Id = "custom_coll_json",
                Name = "Collection JSON Roundtrip",
                WidgetKind = WidgetKind.Custom
            };

            using (var vm = new CustomWidgetViewModel(config, localizationService, store))
            {
                await vm.InitializeAsync();

                using JsonDocument appendFieldsDoc = JsonDocument.Parse(
                    """{ "title": "Ship V2", "done": false, "priority": 2, "tags": ["arch", "qjs"], "meta": { "points": 5 } }""");
                var fieldsMap = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                foreach (JsonProperty prop in appendFieldsDoc.RootElement.EnumerateObject())
                {
                    fieldsMap[prop.Name] = prop.Value.Clone();
                }

                await vm.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec(
                        "collection.append",
                        Collection: "tasks",
                        ItemFieldsJson: fieldsMap)
                ]);

                CustomWidgetRecord added = Assert.Single(vm.Collections["tasks"]);
                Assert.Equal(JsonValueKind.False, added.FieldsJson["done"].ValueKind);
                Assert.Equal(JsonValueKind.Number, added.FieldsJson["priority"].ValueKind);
                Assert.Equal(2, added.FieldsJson["priority"].GetInt32());
                Assert.Equal(JsonValueKind.Array, added.FieldsJson["tags"].ValueKind);
                Assert.Equal(JsonValueKind.Object, added.FieldsJson["meta"].ValueKind);

                CustomWidgetEvaluationContext itemCtx = vm.CreateEvaluationContext() with { CurrentItem = added };
                Assert.Equal("OPEN", CustomWidgetExpressionEvaluator.Interpolate("${$item.done ? 'DONE' : 'OPEN'}", itemCtx));
                Assert.Equal("2", CustomWidgetExpressionEvaluator.Interpolate("${$item.tags.length}", itemCtx));
                Assert.Equal("5", CustomWidgetExpressionEvaluator.Interpolate("${$item.meta.points}", itemCtx));

                await vm.ExecuteActionsAsync(
                [
                    new CustomWidgetActionSpec("script.run", Fn: "toggleTask", Arg: added.Id)
                ]);

                CustomWidgetRecord updated = Assert.Single(vm.Collections["tasks"]);
                Assert.Equal(JsonValueKind.True, updated.FieldsJson["done"].ValueKind);
                Assert.Equal(3, updated.FieldsJson["priority"].GetInt32());
            }

            CustomWidgetStorePayload reloaded = await store.ReloadFromDiskAsync();
            CustomWidgetRecord persisted = Assert.Single(reloaded.Collections["tasks"]);
            Assert.Equal(JsonValueKind.True, persisted.FieldsJson["done"].ValueKind);
            Assert.Equal(3, persisted.FieldsJson["priority"].GetInt32());
            Assert.Equal(2, persisted.FieldsJson["tags"].GetArrayLength());
            Assert.Equal(5, persisted.FieldsJson["meta"].GetProperty("points").GetInt32());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public void QuickJsEnvBridge_ExposesIdleHolidayWeatherClipboardAndTransformSnapshots()
    {
        const string source = """
        export function onInit(ctx) {
          ctx.computed.idle = ctx.env.idleSeconds;
          ctx.computed.dayType = ctx.env.dayType;
          ctx.computed.isHoliday = ctx.env.isHoliday;
          ctx.computed.holidayName = ctx.env.holidayName;
          ctx.computed.tempNum = ctx.env.weather.tempNum;
          ctx.computed.clipKind = ctx.env.clipboard.kind;
          ctx.computed.jwtState = ctx.env.transform.jwtState;
        }
        """;

        CustomWidgetScriptCompileResult compiled = CustomWidgetScriptEngine.Compile(source);
        Assert.True(compiled.IsSuccess, compiled.ErrorMessage);
        Assert.NotNull(compiled.Compiled);

        var octFirst = new DateTimeOffset(2026, 10, 1, 9, 30, 0, TimeSpan.FromHours(8));
        var weather = new CustomWidgetWeatherSnapshot
        {
            HasData = true,
            Temperature = 23.5,
            City = "上海"
        };
        var clipboard = CustomWidgetClipboardSnapshot.CreateFromText("{\"hello\":\"world\"}");
        var transform = new CustomWidgetTransformResult(
            Succeeded: true,
            Operation: "jwtDecode",
            SourcePath: "",
            OutputPath: "",
            OutputName: "",
            SizeText: "",
            ResultText: "{}",
            ErrorMessage: "",
            JwtState: "valid");

        CustomWidgetScriptExecutionResult exec = CustomWidgetScriptEngine.Execute(
            compiled.Compiled!,
            "onInit",
            new CustomWidgetScriptExecutionInput
            {
                ClockOverride = octFirst,
                IdleSecondsOverride = 42.5,
                CultureName = "zh-CN",
                Weather = weather,
                Clipboard = clipboard,
                TransformResult = transform
            });

        Assert.True(exec.IsSuccess, exec.ErrorMessage);
        Assert.Equal(42.5, exec.Computed["idle"].GetDouble());
        Assert.Equal("holiday", exec.Computed["dayType"].GetString());
        Assert.True(exec.Computed["isHoliday"].GetBoolean());
        Assert.Equal("国庆节", exec.Computed["holidayName"].GetString());
        Assert.Equal(23.5, exec.Computed["tempNum"].GetDouble());
        Assert.Equal("json", exec.Computed["clipKind"].GetString());
        Assert.Equal("valid", exec.Computed["jwtState"].GetString());
    }
}