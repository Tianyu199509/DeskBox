using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetContractConsistencyTests
{
    private static readonly Regex SegoeFluentPuaRegex = new(
        @"[\uE000-\uF8FF]|\\u[Ee][0-9A-Fa-f]{3}|\\u[Ff][0-8][0-9A-Fa-f]{2}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void AllCapabilityModulesAndSkillCards_UseCanonicalSchemaContracts_WithoutLegacyDrift()
    {
        IReadOnlyList<CustomWidgetCapabilitySpec> modules = CustomWidgetCapabilityRegistry.All;
        Assert.Equal(37, modules.Count);

        foreach (CustomWidgetCapabilitySpec mod in modules)
        {
            Assert.False(
                mod.GoldenSnippetJson.Contains("\"visible\"", StringComparison.Ordinal),
                $"Capability '{mod.Id}' goldenSnippetJson uses legacy 'visible' instead of canonical 'when'.");
            Assert.False(
                mod.GoldenSnippetJson.Contains("\"view\"", StringComparison.Ordinal),
                $"Capability '{mod.Id}' goldenSnippetJson must use canonical schemaVersion 1 flat graph ('root' + 'elements') without legacy 'view'.");
            Assert.False(
                mod.CompactContractMarkdown.Contains("\"view\":", StringComparison.Ordinal),
                $"Capability '{mod.Id}' compactContractMarkdown still documents legacy '\"view\":' instead of canonical flat graph ('root' + 'elements').");
            Assert.False(
                SegoeFluentPuaRegex.IsMatch(mod.GoldenSnippetJson),
                $"Capability '{mod.Id}' goldenSnippetJson must not contain raw or escaped Segoe Fluent PUA glyphs.");
            Assert.False(
                SegoeFluentPuaRegex.IsMatch(mod.CompactContractMarkdown),
                $"Capability '{mod.Id}' compactContractMarkdown must not contain raw or escaped Segoe Fluent PUA glyphs.");
            Assert.False(
                mod.CompactContractMarkdown.Contains("patch_manifest", StringComparison.OrdinalIgnoreCase) ||
                mod.CompactContractMarkdown.Contains("manifestOps", StringComparison.OrdinalIgnoreCase),
                $"Capability '{mod.Id}' compactContractMarkdown still references deleted 'patch_manifest' / 'manifestOps'.");
            Assert.False(
                mod.GoldenSnippetJson.Contains("patch_manifest", StringComparison.OrdinalIgnoreCase) ||
                mod.GoldenSnippetJson.Contains("manifestOps", StringComparison.OrdinalIgnoreCase),
                $"Capability '{mod.Id}' goldenSnippetJson still references deleted 'patch_manifest' / 'manifestOps'.");
            Assert.False(
                mod.CompactContractMarkdown.Contains("actionId", StringComparison.Ordinal),
                $"Capability '{mod.Id}' compactContractMarkdown still documents legacy 'actionId' instead of unified 'id'/'jobKey'.");
            Assert.False(
                mod.CompactContractMarkdown.Contains("bodyText", StringComparison.Ordinal),
                $"Capability '{mod.Id}' compactContractMarkdown still documents legacy 'bodyText' instead of unified 'text'/'data'.");

            using JsonDocument snippetDoc = JsonDocument.Parse(mod.GoldenSnippetJson);
            JsonElement root = snippetDoc.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.False(
                root.TryGetProperty("view", out _),
                $"Capability '{mod.Id}' goldenSnippetJson must not declare a legacy 'view' property.");

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty prop in root.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.GetRawText();
            }

            string fullManifestJson;
            if (root.TryGetProperty("root", out JsonElement rootIdEl) &&
                rootIdEl.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(rootIdEl.GetString()) &&
                root.TryGetProperty("elements", out JsonElement elemsEl) &&
                elemsEl.ValueKind == JsonValueKind.Object)
            {
                dict["schemaVersion"] = "1";
                dict.TryAdd("id", JsonSerializer.Serialize($"custom.test-{mod.Id}"));
                dict.TryAdd("name", JsonSerializer.Serialize(mod.TitleEn));
                string entries = string.Join(",\n", dict.Select(kv => $"\"{kv.Key}\": {kv.Value}"));
                fullManifestJson = "{\n" + entries + "\n}";
            }
            else if (root.TryGetProperty("type", out _))
            {
                fullManifestJson = $$"""
                {
                  "schemaVersion": 1,
                  "id": "custom.test-{{mod.Id}}",
                  "name": {{JsonSerializer.Serialize(mod.TitleEn)}},
                  "root": "snippet_root",
                  "elements": {
                    "snippet_root": {{mod.GoldenSnippetJson}}
                  }
                }
                """;
            }
            else
            {
                dict["schemaVersion"] = "1";
                dict.TryAdd("id", JsonSerializer.Serialize($"custom.test-{mod.Id}"));
                dict.TryAdd("name", JsonSerializer.Serialize(mod.TitleEn));
                dict["root"] = "\"placeholder_text\"";
                dict["elements"] = """{ "placeholder_text": { "type": "Text", "text": "ok" } }""";
                string entries = string.Join(",\n", dict.Select(kv => $"\"{kv.Key}\": {kv.Value}"));
                fullManifestJson = "{\n" + entries + "\n}";
            }

            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(fullManifestJson);
            Assert.True(
                parsed.IsValid,
                $"Capability '{mod.Id}' snippet failed manifest parse: {string.Join("; ", parsed.Diagnostics.Select(d => d.Message))}");
            Assert.Equal(1, parsed.Manifest!.SchemaVersion);
        }

        foreach (CustomWidgetCapabilitySpec spec in CustomWidgetCapabilityRegistry.All)
        {
            Assert.False(
                spec.CompactContractMarkdown.Contains("\"visible\":", StringComparison.Ordinal),
                $"Capability '{spec.Id}' still uses legacy '\"visible\":' instead of '\"when\":'.");
        }
    }

    [Fact]
    public async Task StructuredJsonState_PersistsAndMutatesNatively_AcrossValidatorStoreAndViewModel()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-state-json-" + Guid.NewGuid().ToString("N"));
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
              "id": "custom.batch-queue",
              "name": "Batch Queue",
              "state": {
                "jobs": [],
                "stats": { "done": 2, "active": false },
                "counter": 5
              },
              "script": { "entry": "logic.js", "tickIntervalMs": 0 },
              "capsule": {
                "title": "Queue",
                "summary": "${state.jobs.length} items",
                "badge": "${state.counter}"
              },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "padding": "12,10,12,12",
                  "children": ["summary_text", "jobs_list"]
                },
                "summary_text": {
                  "type": "Text",
                  "text": "${state.jobs.length} jobs · done=${state.stats.done}"
                },
                "jobs_list": {
                  "type": "List",
                  "items": "${state.jobs}",
                  "itemTemplate": "job_row"
                },
                "job_row": {
                  "type": "Text",
                  "text": "${$item.name}"
                }
              }
            }
            """;

            const string logicJs = """
            export function onInit(ctx) {
              if (!Array.isArray(ctx.state.jobs)) {
                throw new Error('Expected ctx.state.jobs to be a native array');
              }
              if (ctx.state.stats.done !== 2 || ctx.state.stats.active !== false || ctx.state.counter !== 5) {
                throw new Error('Expected structured state scalars/objects to preserve native types');
              }
            }

            export function enqueueJob(ctx) {
              ctx.state.jobs.push({ id: 'j1', name: ctx.args.fileName, progress: 40 });
              ctx.state.stats.done += 1;
              ctx.state.stats.active = true;
              ctx.state.counter += 10;
            }
            """;

            CustomWidgetWorkspaceValidationResult validation = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
                manifestJson,
                logicJs,
                runOnInitDryRun: true);
            Assert.True(
                validation.IsValid,
                string.Join("; ", validation.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));

            string widgetInstanceId = "custom_batch_test";
            var store = new CustomWidgetDataStore(widgetInstanceId, widgetsDir);
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs, locales: null);

            var config = new WidgetConfig
            {
                Id = widgetInstanceId,
                Name = "Batch Queue",
                WidgetKind = WidgetKind.Custom
            };

            using var vm = new CustomWidgetViewModel(config, localizationService, store);
            await vm.InitializeAsync();
            Assert.Null(vm.LastScriptError);

            vm.OnCompactStateChanged(collapsed: true);
            Assert.Equal("0 items", vm.CapsuleSummary);
            Assert.Equal("5", vm.CapsuleBadge);

            await vm.ExecuteActionsAsync(
            [
                new CustomWidgetActionSpec(
                    Type: "script.run",
                    Fn: "enqueueJob",
                    Args: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["fileName"] = "clip.mp4"
                    })
            ]);

            Assert.Null(vm.LastScriptError);
            Assert.Equal("1 items", vm.CapsuleSummary);
            Assert.Equal("15", vm.CapsuleBadge);

            CustomWidgetEvaluationContext evalCtx = vm.CreateEvaluationContext();
            Assert.Equal(
                "1 jobs · done=3",
                CustomWidgetExpressionEvaluator.Interpolate("${state.jobs.length} jobs · done=${state.stats.done}", evalCtx));
            Assert.Equal(
                "clip.mp4",
                CustomWidgetExpressionEvaluator.Interpolate("${state.jobs[0].name}", evalCtx));

            IReadOnlyList<CustomWidgetRecord> stateRecords = CustomWidgetScriptEngine.ProjectComputedCollection(
                "$state.jobs",
                vm.Computed,
                vm.StateJson);
            Assert.Single(stateRecords);
            Assert.Equal("clip.mp4", stateRecords[0].GetField("name"));

            // Verify native structured JSON persisted cleanly in store.json without stringification
            CustomWidgetStorePayload reloaded = await store.ReloadFromDiskAsync();
            Assert.NotNull(reloaded.StateJson);
            Assert.Equal(JsonValueKind.Array, reloaded.StateJson!["jobs"].ValueKind);
            Assert.Equal(1, reloaded.StateJson["jobs"].GetArrayLength());
            Assert.Equal("clip.mp4", reloaded.StateJson["jobs"][0].GetProperty("name").GetString());
            Assert.Equal(JsonValueKind.Number, reloaded.StateJson["counter"].ValueKind);
            Assert.Equal(15, reloaded.StateJson["counter"].GetInt32());
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CommandRunnerAndJobBus_SupportIdleWatchdog_CarriageReturnProgress_AndCancellation()
    {
        var streamedLines = new List<string>();

        // 1) Long-running command producing periodic CR/LF output resets idle watchdog and completes
        CustomWidgetCommandOutcome activeOutcome = await CustomWidgetCommandRunner.ExecuteAsync(
            command: "powershell",
            argumentList:
            [
                "-NoProfile",
                "-Command",
                "[Console]::Write(\"step1`rstep2`n\"); Start-Sleep -Milliseconds 180; [Console]::WriteLine('done-ok')"
            ],
            workingDirectory: null,
            timeoutMs: 0,
            longRunning: true,
            onOutputLine: (line, _, _) =>
            {
                lock (streamedLines)
                {
                    streamedLines.Add(line);
                }
            },
            idleStallTimeoutMs: 4000);

        Assert.True(activeOutcome.Succeeded, activeOutcome.ErrorMessage);
        Assert.False(activeOutcome.TimedOut);
        Assert.False(activeOutcome.Cancelled);
        lock (streamedLines)
        {
            Assert.Contains("step1", streamedLines);
            Assert.Contains("step2", streamedLines);
            Assert.Contains("done-ok", streamedLines);
        }

        // 2) Silent long-running command triggers idle-stall watchdog
        CustomWidgetCommandOutcome stalledOutcome = await CustomWidgetCommandRunner.ExecuteAsync(
            command: "powershell",
            argumentList: ["-NoProfile", "-Command", "Start-Sleep -Seconds 5"],
            workingDirectory: null,
            timeoutMs: 0,
            longRunning: true,
            idleStallTimeoutMs: 350);

        Assert.False(stalledOutcome.Succeeded);
        Assert.True(stalledOutcome.TimedOut);
        Assert.False(stalledOutcome.Cancelled);

        // 3) JobBus cancellation kills active process immediately and produces unified res JSON
        using var jobBus = new CustomWidgetJobBus();
        using CustomWidgetJobBus.ActiveJobHandle handle = jobBus.BeginJob("encode-1", "act-encode");
        Assert.True(jobBus.IsRunning("encode-1"));

        Task<CustomWidgetCommandOutcome> runningTask = CustomWidgetCommandRunner.ExecuteAsync(
            command: "powershell",
            argumentList: ["-NoProfile", "-Command", "Start-Sleep -Seconds 10"],
            workingDirectory: null,
            timeoutMs: 0,
            ct: handle.Token,
            longRunning: true);

        await Task.Delay(150);
        int cancelledCount = jobBus.Cancel("encode-1");
        Assert.Equal(1, cancelledCount);

        CustomWidgetCommandOutcome cancelledOutcome = await runningTask;
        Assert.True(cancelledOutcome.Cancelled);
        Assert.False(cancelledOutcome.TimedOut);

        string resJson = CustomWidgetJobBus.BuildCommandResultPayloadJson(
            "act-encode",
            "encode-1",
            cancelledOutcome);
        using JsonDocument doc = JsonDocument.Parse(resJson);
        JsonElement root = doc.RootElement;
        Assert.Equal("act-encode", root.GetProperty("id").GetString());
        Assert.Equal("encode-1", root.GetProperty("jobKey").GetString());
        Assert.Equal("command.run", root.GetProperty("type").GetString());
        Assert.False(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("cancelled").GetBoolean());
        Assert.False(root.GetProperty("timedOut").GetBoolean());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("meta").ValueKind);
    }

    [Fact]
    public async Task LocalFilePrimitives_SupportListDir_SafeManage_PathSettings_AndBatchFileDrop()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "deskbox-local-io-" + Guid.NewGuid().ToString("N"));
        try
        {
            string settingsDir = Path.Combine(tempRoot, "settings");
            string widgetsDir = Path.Combine(tempRoot, "widgets");
            string mediaDir = Path.Combine(tempRoot, "media");
            Directory.CreateDirectory(settingsDir);
            Directory.CreateDirectory(widgetsDir);
            Directory.CreateDirectory(mediaDir);

            var settingsService = new SettingsService(settingsDir);
            var localizationService = new LocalizationService(settingsService);

            string fileA = Path.Combine(mediaDir, "alpha.mp4");
            string fileB = Path.Combine(mediaDir, "beta.mkv");
            string fileTxt = Path.Combine(mediaDir, "notes.txt");
            await File.WriteAllBytesAsync(fileA, [1, 2, 3, 4]);
            await File.WriteAllBytesAsync(fileB, [5, 6, 7, 8, 9]);
            await File.WriteAllTextAsync(fileTxt, "hello");

            // 1) file.listDir with multi-glob pattern
            CustomWidgetDirectoryListOutcome listRes = await CustomWidgetLocalFileEngine.ListDirectoryAsync(
                mediaDir,
                pattern: "*.mp4;*.mkv",
                recursive: false,
                maxItems: 50);
            Assert.True(listRes.Succeeded, listRes.ErrorMessage);
            Assert.Equal(2, listRes.Files.Count);
            Assert.Contains(listRes.Files, e => e.Name == "alpha.mp4" && e.SizeBytes == 4);
            Assert.Contains(listRes.Files, e => e.Name == "beta.mkv" && e.SizeBytes == 5);

            // 2) file.manage copy / rename + strict permanent delete rejection
            string subDir = Path.Combine(mediaDir, "sub");
            Directory.CreateDirectory(subDir);
            string copyDest = Path.Combine(subDir, "alpha-copy.mp4");
            CustomWidgetFileManageOutcome copyRes = await CustomWidgetLocalFileEngine.ManageFileAsync(
                "copy",
                fileA,
                copyDest);
            Assert.True(copyRes.Succeeded, copyRes.ErrorMessage);
            Assert.True(File.Exists(copyDest));

            CustomWidgetFileManageOutcome renameRes = await CustomWidgetLocalFileEngine.ManageFileAsync(
                "rename",
                copyDest,
                "alpha-renamed.mp4");
            Assert.True(renameRes.Succeeded, renameRes.ErrorMessage);
            Assert.True(File.Exists(Path.Combine(subDir, "alpha-renamed.mp4")));

            CustomWidgetFileManageOutcome forbiddenDelete = await CustomWidgetLocalFileEngine.ManageFileAsync(
                "delete",
                fileTxt,
                null);
            Assert.False(forbiddenDelete.Succeeded);
            Assert.Contains("recycle", forbiddenDelete.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(fileTxt));

            // 3) Explicit "file" and "folder" settings + multi-file ctx.event.files batch drop
            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.transcoder",
              "name": "Transcoder",
              "settings": [
                { "key": "ffmpegBin", "type": "file", "label": "FFmpeg", "accept": ".exe", "default": "ffmpeg.exe" },
                { "key": "outFolder", "type": "folder", "label": "Output Folder", "default": "" }
              ],
              "state": { "queued": [] },
              "script": { "entry": "logic.js", "tickIntervalMs": 0 },
              "onDrop": {
                "storage": "reference",
                "action": { "type": "script.run", "fn": "onFilesDropped" }
              },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "padding": "12,10,12,12",
                  "children": ["status_text"]
                },
                "status_text": {
                  "type": "Text",
                  "text": "${$settings.ffmpegBin} -> ${$settings.outFolder} (${state.queued.length})"
                }
              }
            }
            """;

            const string logicJs = """
            export function onInit(ctx) {}

            export function onFilesDropped(ctx) {
              const batch = ctx.event?.files ?? [];
              ctx.state.queued = batch.map(f => ({ name: f.name, path: f.path, size: f.sizeBytes }));
            }
            """;

            CustomWidgetWorkspaceValidationResult parsed = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
                manifestJson,
                logicJs);
            Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
            Assert.Equal(".exe", parsed.Manifest!.Settings[0].Accept);
            Assert.Equal(
                (true, true),
                CustomWidgetOnboardingHelper.ClassifyPathSetting(parsed.Manifest.Settings[0], "ffmpeg.exe"));
            Assert.Equal(
                (true, false),
                CustomWidgetOnboardingHelper.ClassifyPathSetting(parsed.Manifest.Settings[1], string.Empty));

            var store = new CustomWidgetDataStore("custom_transcoder", widgetsDir);
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs, locales: null);

            var config = new WidgetConfig
            {
                Id = "custom_transcoder",
                Name = "Transcoder",
                WidgetKind = WidgetKind.Custom
            };

            using var vm = new CustomWidgetViewModel(config, localizationService, store);
            await vm.InitializeAsync();

            bool handled = await vm.HandleDropZoneFilesAsync([fileA, fileB, fileTxt], []);
            Assert.True(handled);
            Assert.Null(vm.LastScriptError);

            CustomWidgetEvaluationContext evalCtx = vm.CreateEvaluationContext();
            Assert.Equal("3", CustomWidgetExpressionEvaluator.Interpolate("${state.queued.length}", evalCtx));
            Assert.Equal("alpha.mp4", CustomWidgetExpressionEvaluator.Interpolate("${state.queued[0].name}", evalCtx));
            Assert.Equal("beta.mkv", CustomWidgetExpressionEvaluator.Interpolate("${state.queued[1].name}", evalCtx));
            Assert.Equal("notes.txt", CustomWidgetExpressionEvaluator.Interpolate("${state.queued[2].name}", evalCtx));
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
