using System.Text;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetTests
{
    [Theory]
    [InlineData(CustomWidgetTemplates.SystemPulseTemplateId)]
    [InlineData(CustomWidgetTemplates.PomodoroTemplateId)]
    [InlineData(CustomWidgetTemplates.CountdownTemplateId)]
    [InlineData(CustomWidgetTemplates.AiPulseTemplateId)]
    [InlineData(CustomWidgetTemplates.WorldClockTemplateId)]
    [InlineData(CustomWidgetTemplates.DevToolboxTemplateId)]
    [InlineData(CustomWidgetTemplates.HydrationStarterId)]
    [InlineData(CustomWidgetTemplates.AiInboxStarterId)]
    [InlineData(CustomWidgetTemplates.NewsRssStarterId)]
    [InlineData(CustomWidgetTemplates.AgentHudTemplateId)]
    [InlineData(CustomWidgetTemplates.FocusDialTemplateId)]
    [InlineData(CustomWidgetTemplates.SnippetGridTemplateId)]
    [InlineData(CustomWidgetTemplates.WeatherDeskTemplateId)]
    [InlineData(CustomWidgetTemplates.TodoAgendaTemplateId)]
    public void BuiltInPresetTemplates_ParseWithoutErrors(string templateId)
    {
        Assert.Equal(14, CustomWidgetTemplates.BuiltInTemplateIds.Count);
        CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(templateId);
        Assert.DoesNotContain("\"i18n\"", bundle.ManifestJson, StringComparison.Ordinal);
        Assert.Equal(13, bundle.Locales.Locales.Count);
        Assert.NotNull(bundle.Locales.Meta);

        CustomWidgetManifest rawManifest = CustomWidgetManifestParser.Parse(bundle.ManifestJson).Manifest!;
        Assert.Empty(rawManifest.I18n);

        CustomWidgetParseResult result = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            bundle.ManifestJson,
            bundle.LogicJs,
            bundle.Locales);

        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Manifest);
        Assert.Equal(13, result.Manifest!.I18n.Count);
        Assert.Equal(1, result.Manifest.SchemaVersion);
        Assert.NotEmpty(result.Manifest.Name);
        Assert.NotNull(result.Manifest.Capsule);
        Assert.Contains(result.Manifest.View.Type, new[] { "Stack", "ModuleStack", "Grid", "Tabs" });
        Assert.NotEmpty(result.Manifest.View.Children);
    }

    [Fact]
    public void ManifestParser_ReportsSyntaxErrorOnMalformedJson()
    {
        CustomWidgetParseResult result = CustomWidgetManifestParser.Parse("{ invalid json ");

        Assert.False(result.IsValid);
        Assert.Null(result.Manifest);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void ManifestParser_ReportsDiagnosticWhenRootAndElementsAreMissing()
    {
        const string json = """
        {
          "schemaVersion": 1,
          "id": "test.no-view",
          "name": "No View Widget"
        }
        """;

        CustomWidgetParseResult result = CustomWidgetManifestParser.Parse(json);

        Assert.False(result.IsValid);
        Assert.NotNull(result.Manifest);
        Assert.Contains(result.Diagnostics, d => d.Path == "$.elements" && d.Code == "MissingElementsMap" && d.IsError);
        Assert.Contains(result.Diagnostics, d => d.Path == "$.root" && d.Code == "ElementGraphMissingRoot" && d.IsError);
    }

    [Fact]
    public void ExpressionEvaluator_ResolvesStateSettingsCollectionsAndOperators()
    {
        var record = new CustomWidgetRecord
        {
            Id = "rec-01",
            CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
            Fields = new Dictionary<string, string>
            {
                ["text"] = "完成原生自定义格子引擎",
                ["tag"] = "进展",
                ["fileName"] = "spec.md",
                ["filePath"] = @"D:\docs\spec.md"
            }
        };

        var context = new CustomWidgetEvaluationContext
        {
            Settings = new Dictionary<string, string>
            {
                ["dropMode"] = "link"
            },
            State = new Dictionary<string, string>
            {
                ["activeTag"] = "进展",
                ["filterTag"] = "全部",
                ["draftText"] = ""
            },
            Collections = new Dictionary<string, List<CustomWidgetRecord>>
            {
                ["logs"] = [record]
            }
        };

        Assert.Equal(
            "今日 1 条 · 共 1 条",
            CustomWidgetExpressionEvaluator.Interpolate(
                "今日 ${collections.logs.todayCount} 条 · 共 ${collections.logs.count} 条",
                context));

        Assert.Equal(
            "完成原生自定义格子引擎",
            CustomWidgetExpressionEvaluator.Interpolate(
                "${collections.logs.latest.text ?? '默认摘要'}",
                context));

        Assert.Equal(
            "路径关联",
            CustomWidgetExpressionEvaluator.Interpolate(
                "${$settings.dropMode == 'copy' ? '复制副本' : '路径关联'}",
                context));

        Assert.False(CustomWidgetExpressionEvaluator.EvaluateCondition("${state.draftText != ''}", context));

        CustomWidgetEvaluationContext itemContext = context.WithItem(record, 0);
        Assert.True(CustomWidgetExpressionEvaluator.EvaluateCondition(
            "${state.filterTag == '全部' || $item.tag == state.filterTag}",
            itemContext));
        Assert.Equal("进展", CustomWidgetExpressionEvaluator.Interpolate("${$item.tag}", itemContext));
    }

    [Fact]
    public async Task DataStore_PersistsStoreResolveDropAndFormatsMarkdown()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxCustomWidgetTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CustomWidgetDataStore("custom-test-01", tempRoot);

            string manifestJson = await store.ReadOrCreateManifestJsonAsync(CustomWidgetTemplates.SystemPulseTemplateId);
            Assert.Contains("deskbox.sys-pulse", manifestJson, StringComparison.Ordinal);
            Assert.True(File.Exists(store.ManifestPath));
            Assert.True(Directory.Exists(store.LocalesDirectoryPath));
            Assert.True(File.Exists(Path.Combine(store.LocalesDirectoryPath, "zh-CN.json")));
            Assert.True(File.Exists(Path.Combine(store.LocalesDirectoryPath, "en-US.json")));

            var payload = new CustomWidgetStorePayload();
            payload.Settings["dropMode"] = "copy";
            payload.State["activeTag"] = "会议";
            payload.Collections["logs"] =
            [
                new CustomWidgetRecord
                {
                    Id = "item-1",
                    CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                    Fields = new Dictionary<string, string>
                    {
                        ["text"] = "评审架构方案",
                        ["tag"] = "会议"
                    }
                }
            ];

            await store.SaveAsync(payload);

            var reloadedStore = new CustomWidgetDataStore("custom-test-01", tempRoot);
            CustomWidgetStorePayload loaded = await reloadedStore.LoadAsync();

            Assert.Equal("copy", loaded.Settings["dropMode"]);
            Assert.Equal("会议", loaded.State["activeTag"]);
            Assert.Single(loaded.Collections["logs"]);
            Assert.Equal("评审架构方案", loaded.Collections["logs"][0].GetField("text"));

            string externalFile = Path.Combine(tempRoot, "demo-note.txt");
            await File.WriteAllTextAsync(externalFile, "hello deskbox");

            (string linkPath, string linkFullPath, string linkName, string linkMode) =
                await store.ResolveDroppedFileWithPortablePathAsync(externalFile, "reference");
            Assert.Equal("link", linkMode);
            Assert.Equal("demo-note.txt", linkName);
            Assert.Equal(Path.GetFullPath(externalFile), linkPath);
            Assert.Equal(Path.GetFullPath(externalFile), linkFullPath);

            (string copyPath, string copyFullPath, string copyName, string copyMode) =
                await store.ResolveDroppedFileWithPortablePathAsync(externalFile, "copy");
            Assert.Equal("copy", copyMode);
            Assert.Equal("demo-note.txt", copyName);
            Assert.Equal("attachments/demo-note.txt", copyPath);
            string resolvedCopyPath = store.ResolveRuntimePath(copyPath);
            Assert.Equal(copyFullPath, resolvedCopyPath);
            Assert.StartsWith(store.AttachmentsDirectory, resolvedCopyPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(resolvedCopyPath));

            string markdown = CustomWidgetDataStore.FormatCollectionAsMarkdown(
                "工作记录",
                "logs",
                loaded.Collections["logs"]);
            Assert.Contains("# 工作记录", markdown, StringComparison.Ordinal);
            Assert.Contains("**[会议]** 评审架构方案", markdown, StringComparison.Ordinal);
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
    public async Task Templates_EnsureAiAgentDocsAndReadDbboxPackage()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxCustomTemplateTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            string workspace = await CustomWidgetTemplates.EnsureAiAgentWorkspaceDocsAsync(tempRoot);
            Assert.True(File.Exists(Path.Combine(workspace, "SKILL.md")));
            Assert.True(File.Exists(Path.Combine(workspace, "manifest.schema.json")));

            string jsonPath = Path.Combine(tempRoot, "sample.json");
            await File.WriteAllTextAsync(
                jsonPath,
                CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.SystemPulseTemplateId).ManifestJson);

            (CustomWidgetManifest manifest, string rawJson) = await CustomWidgetTemplates.ReadPackageOrJsonAsync(jsonPath);
            Assert.Equal("deskbox.sys-pulse", manifest.Id);
            Assert.NotEmpty(rawJson);
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
    public void ExtractJsonFromClipboardText_HandlesMarkdownFencesAndEmbeddedExpressions()
    {
        const string aiResponseWithFences = """
        好的，这是为你设计的每日喝水打卡原生格子配置：

        ```json
        {
          "schemaVersion": 1,
          "id": "custom.hydration",
          "name": "喝水打卡",
          "capsule": {
            "title": "今日已喝 ${state.cups} 杯",
            "summary": "目标 {8} 杯"
          },
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "children": ["cups_text"]
            },
            "cups_text": {
              "type": "Text",
              "text": "已喝 ${state.cups} / 8 杯"
            }
          }
        }
        ```

        复制上方代码粘贴回 DeskBox 即可直接上桌！
        """;

        string extracted = CustomWidgetTemplates.ExtractJsonFromClipboardText(aiResponseWithFences);
        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(extracted);

        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));
        Assert.Equal("custom.hydration", parsed.Manifest!.Id);
        Assert.Equal("喝水打卡", parsed.Manifest.Name);

        // Plain unfenced JSON with surrounding chat prose
        string unfenced = "下面是JSON：\n" + extracted + "\n祝使用愉快！";
        string extractedPlain = CustomWidgetTemplates.ExtractJsonFromClipboardText(unfenced);
        Assert.Equal(extracted, extractedPlain);

        Assert.Equal(string.Empty, CustomWidgetTemplates.ExtractJsonFromClipboardText("没有任何代码块"));
    }

    [Fact]
    public void AiPromptBuilders_IncludeSpecsExistingManifestAndRepairDiagnostics()
    {
        string createSystemPrompt = CustomWidgetTemplates.BuildStudioCoCreationSystemPrompt(uiLanguage: "zh-CN");
        string createUserPrompt = CustomWidgetTemplates.BuildStudioCoCreationUserPrompt(
            "做一个番茄钟记录器",
            uiLanguage: "zh-CN");
        Assert.Contains("做一个番茄钟记录器", createUserPrompt, StringComparison.Ordinal);
        Assert.Contains("schemaVersion", createSystemPrompt, StringComparison.Ordinal);
        Assert.Contains("StatCard", createSystemPrompt, StringComparison.Ordinal);

        string editPrompt = CustomWidgetTemplates.BuildStudioCoCreationUserPrompt(
            "增加导出按钮",
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.SystemPulseTemplateId).ManifestJson,
            uiLanguage: "zh-CN");
        Assert.Contains("增加导出按钮", editPrompt, StringComparison.Ordinal);
        Assert.Contains("deskbox.sys-pulse", editPrompt, StringComparison.Ordinal);

        string repairPrompt = CustomWidgetTemplates.BuildChatAiRepairPrompt(
            "{ \"schemaVersion\": 1 }",
            [new CustomWidgetValidationDiagnostic("$.elements", "Missing elements map")]);
        Assert.Contains("$.elements", repairPrompt, StringComparison.Ordinal);
        Assert.Contains("Missing elements map", repairPrompt, StringComparison.Ordinal);

        string agentPrompt = CustomWidgetTemplates.BuildLocalAgentPrompt(
            "优化卡片间距",
            @"C:\Data\widgets\SKILL.md",
            @"C:\Data\widgets\w1\manifest.json",
            @"C:\Data\widgets");
        Assert.Contains(@"C:\Data\widgets\SKILL.md", agentPrompt, StringComparison.Ordinal);
        Assert.Contains(@"C:\Data\widgets\w1\manifest.json", agentPrompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DataStore_ReloadFromDiskAsync_RefreshesCrossProcessWrites()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxCustomReloadTests_" + Guid.NewGuid().ToString("N"));
        try
        {
            var hostStore = new CustomWidgetDataStore("w-sync", tempRoot);
            var initial = new CustomWidgetStorePayload();
            initial.Settings["dropMode"] = "link";
            await hostStore.SaveAsync(initial);

            // Simulate SettingsHost process writing updated preferences to the same store.json
            var settingsProcessStore = new CustomWidgetDataStore("w-sync", tempRoot);
            CustomWidgetStorePayload edited = await settingsProcessStore.ReloadFromDiskAsync();
            edited.Settings["dropMode"] = "copy";
            await settingsProcessStore.SaveAsync(edited);

            // LoadAsync returns cached "link", whereas ReloadFromDiskAsync refreshes to "copy"
            CustomWidgetStorePayload reloaded = await hostStore.ReloadFromDiskAsync();
            Assert.Equal("copy", reloaded.Settings["dropMode"]);
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
    public void FeatureWidgetSettings_CustomLegacyFallback_ReflectsLiveNonDeletedInstances()
    {
        var settings = new AppSettings();
        settings.FeatureWidgetEnabledStates.Clear();

        // No custom widgets -> disabled
        Assert.False(FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Custom));

        // Live active custom widget -> enabled even when FeatureWidgetEnabledStates omits Custom
        var widget = new WidgetConfig
        {
            Id = "custom-live-1",
            WidgetKind = WidgetKind.Custom,
            IsDisabled = false
        };
        settings.Widgets.Add(widget);
        Assert.True(FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Custom));

        // Parked/disabled custom widget -> false
        widget.IsDisabled = true;
        Assert.False(FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Custom));

        // Deleted custom widget -> false
        widget.IsDisabled = false;
        settings.DeletedWidgetIds.Add(widget.Id);
        Assert.False(FeatureWidgetSettings.IsEnabled(settings, WidgetKind.Custom));
    }

    [Fact]
    public void NativeMetrics_ResolveExportsAndReadNonNegativeNetworkTotals()
    {
        Assert.True(DeskBox.Platform.CustomWidgetNativeMetrics.GetSystemTimes(out _, out _, out _));
        var mem = new DeskBox.Platform.CustomWidgetNativeMetrics.MemoryStatusEx
        {
            dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DeskBox.Platform.CustomWidgetNativeMetrics.MemoryStatusEx>()
        };
        Assert.True(DeskBox.Platform.CustomWidgetNativeMetrics.GlobalMemoryStatusEx(ref mem));
        Assert.True(mem.ullTotalPhys > 0);
        _ = DeskBox.Platform.CustomWidgetNativeMetrics.GetSystemPowerStatus(out _);
        Assert.True(DeskBox.Platform.CustomWidgetNativeMetrics.TryReadActiveNetworkTotals(out long rx, out long tx));
        Assert.True(rx >= 0);
        Assert.True(tx >= 0);
    }

    [Fact]
    public void EmojiIconHelpers_DistinguishEmojiFromFluentGlyphsAndRewriteManifestSafely()
    {
        // Segoe Fluent PUA glyphs must never be misclassified as color Emoji
        Assert.True(CustomWidgetTemplates.IsSegoeFluentGlyph("\uE70F"));
        Assert.False(CustomWidgetTemplates.IsEmojiIcon("\uE70F"));
        Assert.False(CustomWidgetTemplates.IsEmojiIcon("\\uE73A"));

        // Color Emojis (surrogate pairs and BMP + VS16) are recognized
        Assert.True(CustomWidgetTemplates.IsEmojiIcon("💧"));
        Assert.True(CustomWidgetTemplates.IsEmojiIcon("🗂️"));
        Assert.True(CustomWidgetTemplates.IsEmojiIcon("⏱️"));

        // All 14 built-in presets carry distinct Emoji icons plus distinct Segoe Fluent PUA monochrome fallbacks
        string[] presetIcons = CustomWidgetTemplates.Presets.Select(p => p.IconGlyph).ToArray();
        Assert.Equal(14, presetIcons.Length);
        Assert.Equal(presetIcons.Length, presetIcons.Distinct(StringComparer.Ordinal).Count());
        Assert.All(presetIcons, icon =>
        {
            Assert.True(CustomWidgetTemplates.IsEmojiIcon(icon));
            Assert.False(CustomWidgetTemplates.IsSegoeFluentGlyph(icon));
        });
        string[] monoGlyphs = CustomWidgetTemplates.Presets.Select(p => p.MonochromeFallbackGlyph).ToArray();
        Assert.Equal(monoGlyphs.Length, monoGlyphs.Distinct(StringComparer.Ordinal).Count());
        Assert.All(monoGlyphs, glyph => Assert.True(CustomWidgetTemplates.IsSegoeFluentGlyph(glyph)));

        // WithUpdatedIconJson updates both top-level icon and static capsule.glyph without touching view
        string updated = CustomWidgetTemplates.WithUpdatedIconJson(
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.HydrationStarterId).ManifestJson,
            "🚀");
        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(updated);
        Assert.True(parsed.IsValid);
        Assert.Equal("🚀", parsed.Manifest!.Icon);
        Assert.Equal("🚀", parsed.Manifest.Capsule!.Glyph);
        Assert.Equal("deskbox.hydration", parsed.Manifest.Id);

        // Preferred emoji is embedded into Studio Co-Creation and Local Agent prompts
        string chatWithEmoji = CustomWidgetTemplates.BuildStudioCoCreationUserPrompt(
            "喝水打卡",
            preferredEmoji: "💧",
            uiLanguage: "zh-CN");
        Assert.Contains("\"icon\"", chatWithEmoji, StringComparison.Ordinal);
        Assert.Contains("💧", chatWithEmoji, StringComparison.Ordinal);

        string agentWithEmoji = CustomWidgetTemplates.BuildLocalAgentPrompt(
            "喝水打卡",
            @"C:\Data\SKILL.md",
            null,
            @"C:\Data",
            preferredEmoji: "💧");
        Assert.Contains("💧", agentWithEmoji, StringComparison.Ordinal);
    }

    [Fact]
    public void GlanceDecodeCalculator_CapsWidthTo1600UnderResourceSaver()
    {
        Assert.Equal(
            GlanceImageDecodeSizeCalculator.MaximumDecodePixelWidth,
            GlanceImageDecodeSizeCalculator.Calculate(2000, 1400, 2, resourceSaver: false));
        Assert.Equal(
            GlanceImageDecodeSizeCalculator.ResourceSaverMaximumDecodePixelWidth,
            GlanceImageDecodeSizeCalculator.Calculate(2000, 1400, 2, resourceSaver: true));
    }

    [Fact]
    public void ModuleListHelper_PreservesOrderGuardsLastEnabledAndReordersDesktopSlots()
    {
        IReadOnlyList<CustomWidgetOptionSpec> defined =
        [
            new("cpu", "CPU"),
            new("memory", "内存"),
            new("gpu", "GPU"),
            new("processes", "应用"),
            new("network", "网速"),
            new("storage", "磁盘")
        ];

        string initial = "cpu:1,memory:1,gpu:1,processes:1,network:0,storage:0";
        IReadOnlyList<CustomWidgetModuleEntry> entries = CustomWidgetModuleListHelper.ParseEntries(initial, defined);
        Assert.Equal(6, entries.Count);
        Assert.Equal(["cpu", "memory", "gpu", "processes"], entries.Where(e => e.Enabled).Select(e => e.Id).ToArray());

        // Drag "processes" (enabled slot 3) up to enabled slot 1 (right after cpu, keeping network/storage disabled)
        string reordered = CustomWidgetModuleListHelper.ReorderEnabledModule(initial, defined, "processes", 1);
        Assert.Equal("cpu:1,processes:1,memory:1,gpu:1,network:0,storage:0", reordered);

        // Move "gpu" down by 1 in full list
        string moved = CustomWidgetModuleListHelper.MoveModule(reordered, defined, "gpu", 1);
        Assert.Equal("cpu:1,processes:1,memory:1,network:0,gpu:1,storage:0", moved);

        // Guard prevents disabling the very last active module
        string oneLeft = "cpu:1,memory:0,gpu:0,processes:0,network:0,storage:0";
        string afterAttempt = CustomWidgetModuleListHelper.SetModuleEnabled(oneLeft, defined, "cpu", enabled: false);
        Assert.True(CustomWidgetModuleListHelper.IsModuleEnabled(afterAttempt, "cpu", defined));
    }

    [Fact]
    public void SystemMonitorPresetV2_DeclaresModuleStackGpuProcessesAndMultiDriveTokens()
    {
        CustomWidgetTemplateBundle sysBundle =
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.SystemPulseTemplateId);
        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(sysBundle.ManifestJson);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(d => d.Message)));

        CustomWidgetManifest manifest = parsed.Manifest!.WithLocales(sysBundle.Locales);
        Assert.Equal("deskbox.sys-pulse", manifest.Id);
        Assert.Equal("${$i18n.widgetName}", manifest.Name);
        Assert.Equal("系统监控", manifest.ResolveLocalizedName("zh-CN"));
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("📊", manifest.Icon);
        Assert.Contains(manifest.Settings, s => s.Key == "modules" && s.Type == "moduleList");
        Assert.Contains(manifest.Settings, s => s.Key == "chartBackdrop" && s.DefaultValue == "grid");

        CustomWidgetNodeSpec moduleStack = Assert.Single(manifest.View.Children);
        Assert.Equal("ModuleStack", moduleStack.Type);
        Assert.Contains(moduleStack.Children, c => c.Id == "cpu" && c.CompactPair);
        Assert.Contains(moduleStack.Children, c => c.Id == "memory" && c.CompactPair);
        Assert.Contains(moduleStack.Children, c => c.Id == "gpu" && c.Type == "Card");
        Assert.Contains(moduleStack.Children, c => c.Id == "processes" && c.Type == "Card");

        // Verify $sys.gpu.*, $sys.disk.selected*, and $sys.status.* resolution
        var snapshot = new CustomWidgetSystemMetricsSnapshot
        {
            GpuName = "RTX 4070",
            GpuPercent = 34,
            GpuVramUsedGb = 5.4,
            GpuVramTotalGb = 12.0,
            GpuVramPercent = 45,
            UptimeText = "2 天 05 小时",
            HasBattery = false,
            DiskByDrive = new Dictionary<string, CustomWidgetDiskMetrics>(StringComparer.OrdinalIgnoreCase)
            {
                ["C:"] = new CustomWidgetDiskMetrics("C:", 62, 180, 476),
                ["D:"] = new CustomWidgetDiskMetrics("D:", 28, 720, 1000)
            }
        };

        var ctx = new CustomWidgetEvaluationContext
        {
            Settings = new Dictionary<string, string> { ["targetDrive"] = "D:", ["warnThreshold"] = "85" },
            SystemMetrics = snapshot
        };

        Assert.Equal("RTX 4070", CustomWidgetExpressionEvaluator.Interpolate("${$sys.gpu.name}", ctx));
        Assert.Equal("5.4 / 12 GB", CustomWidgetExpressionEvaluator.Interpolate("${$sys.gpu.vramSummary}", ctx));
        Assert.Equal("D:", CustomWidgetExpressionEvaluator.Interpolate("${$sys.disk.selectedName}", ctx));
        Assert.Equal("28%", CustomWidgetExpressionEvaluator.Interpolate("${$sys.disk.selectedPercent}%", ctx));
        Assert.NotEmpty(CustomWidgetExpressionEvaluator.Interpolate("${$sys.status.label}", ctx));
        Assert.Equal("2 天 05 小时", CustomWidgetExpressionEvaluator.Interpolate("${$sys.status.value}", ctx));
    }

    [Fact]
    public async Task DataStore_AutomaticallyUpgradesUnmodifiedSystemPulseV1ToV2()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxSysPulseUpgrade_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CustomWidgetDataStore("w-sys-v1", tempRoot);
            const string legacyV1 = """
            {
              "schemaVersion": 1,
              "id": "deskbox.sys-pulse",
              "origin": "official",
              "name": "性能脉冲",
              "version": "0.9.0",
              "root": "main",
              "elements": {
                "main": { "type": "Stack", "children": [] }
              }
            }
            """;
            await File.WriteAllTextAsync(store.ManifestPath, legacyV1);

            string loaded = await store.ReadOrCreateManifestJsonAsync(CustomWidgetTemplates.SystemPulseTemplateId);
            Assert.Contains("\"version\": \"1.0.0\"", loaded, StringComparison.Ordinal);
            Assert.Contains("\"name\": \"${$i18n.widgetName}\"", loaded, StringComparison.Ordinal);
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
    public void NativeMetrics_GpuNameShortenerAndProcessSnapshot_WorkCleanly()
    {
        Assert.Equal(
            "RTX 4070 Super",
            DeskBox.Platform.CustomWidgetNativeMetrics.FormatShortGpuName("NVIDIA GeForce RTX 4070 Super"));
        Assert.Equal(
            "Radeon 780M",
            DeskBox.Platform.CustomWidgetNativeMetrics.FormatShortGpuName("AMD Radeon(TM) 780M Graphics"));

        _ = DeskBox.Platform.CustomWidgetNativeMetrics.TryReadPrimaryGpuVideoMemory(out _, out _, out _, out _);
        using var reader = new DeskBox.Platform.CustomWidgetNativeMetrics.SystemProcessSnapshotReader();
        var samples = new List<DeskBox.Platform.CustomWidgetNativeMetrics.RawProcessSample>();
        Assert.True(reader.TryReadProcesses(samples));
        Assert.NotEmpty(samples);
    }

    [Fact]
    public void PomodoroPresetV2_DefaultsOptionalModulesOffAndDeclaresOrthogonalRadialGauge()
    {
        Assert.Contains(
            CustomWidgetTemplates.Presets,
            p => p.Id == CustomWidgetTemplates.PomodoroTemplateId &&
                 p.IconGlyph == "🍅" &&
                 p.MonochromeFallbackGlyph == "\uE916");

        CustomWidgetTemplateBundle bundle =
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.PomodoroTemplateId);
        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(bundle.ManifestJson);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));

        CustomWidgetManifest manifest = parsed.Manifest!.WithLocales(bundle.Locales);
        Assert.Equal("deskbox.pomodoro", manifest.Id);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("${$i18n.widgetName}", manifest.Name);
        Assert.Equal("专注时段", manifest.ResolveLocalizedName("zh-CN"));
        Assert.Equal("🍅", manifest.Icon);

        CustomWidgetSettingDefinition modulesDef = Assert.Single(
            manifest.Settings,
            s => s.Key == "modules" && s.Type == "moduleList");
        Assert.Equal("timer:1,task:1,goal:1,history:0,health:0", modulesDef.DefaultValue);

        IReadOnlyList<CustomWidgetModuleEntry> defaultEntries =
            CustomWidgetModuleListHelper.ParseEntries(modulesDef.DefaultValue, modulesDef.Options);
        Assert.Equal(["timer", "task", "goal"], defaultEntries.Where(e => e.Enabled).Select(e => e.Id).ToArray());
        Assert.Equal(["history", "health"], defaultEntries.Where(e => !e.Enabled).Select(e => e.Id).ToArray());

        CustomWidgetNodeSpec moduleStack = manifest.View;
        Assert.Equal("ModuleStack", moduleStack.Type);
        Assert.Contains(moduleStack.Children, c => c.Id == "timer" && c.Type == "Card");
        Assert.Contains(moduleStack.Children, c => c.Id == "task");
        Assert.Contains(moduleStack.Children, c => c.Id == "goal");
        Assert.Contains(moduleStack.Children, c => c.Id == "history");
        Assert.Contains(moduleStack.Children, c => c.Id == "health");
    }

    [Fact]
    public async Task PomodoroDataStore_AutoUpgradesLegacyV2ManifestAndCyclesPhases()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxPomoUpgrade_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new CustomWidgetDataStore("pomo-v2", tempRoot);
            await File.WriteAllTextAsync(
                store.ManifestPath,
                """{ "schemaVersion": 1, "id": "deskbox.pomodoro", "origin": "official", "version": "0.8.0", "name": "番茄时钟", "root": "main", "elements": { "main": { "type": "Stack", "children": [] } } }""");

            string upgraded = await store.ReadOrCreateManifestJsonAsync(CustomWidgetTemplates.PomodoroTemplateId);
            Assert.Contains("\"version\": \"1.0.0\"", upgraded, StringComparison.Ordinal);
            Assert.True(Directory.Exists(store.LocalesDirectoryPath));
            Assert.True(File.Exists(Path.Combine(store.LocalesDirectoryPath, "zh-CN.json")));

            await File.WriteAllTextAsync(
                store.ManifestPath,
                """{ "schemaVersion": 1, "id": "deskbox.pomodoro", "origin": "official", "version": "0.9.0", "name": "番茄时钟", "root": "main", "elements": { "main": { "type": "ModuleStack", "children": [] } } }""");
            string upgradedFrom09 = await store.ReadOrCreateManifestJsonAsync(CustomWidgetTemplates.PomodoroTemplateId);
            Assert.Contains("\"version\": \"1.0.0\"", upgradedFrom09, StringComparison.Ordinal);

            var state = new Dictionary<string, string>(StringComparer.Ordinal);
            var settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["focusMinutes"] = "25",
                ["breakPreset"] = "5,15"
            };
            DateTimeOffset now = new(2026, 10, 30, 14, 0, 0, TimeSpan.FromHours(8));

            CustomWidgetPomodoroSnapshot s0 = CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, now, cultureName: "zh-CN");
            Assert.False(s0.IsPaused);
            Assert.Equal("专注", s0.HeaderPillText);
            Assert.Equal(string.Empty, s0.DialSubtext);

            CustomWidgetPomodoroEngine.ToggleStartPause(state, settings, now);
            CustomWidgetPomodoroSnapshot sRun = CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, now.AddMinutes(5), cultureName: "zh-CN");
            Assert.True(sRun.IsRunning);
            Assert.False(sRun.IsPaused);
            Assert.Equal("专注中", sRun.HeaderPillText);
            Assert.Contains("结束", sRun.DialSubtext, StringComparison.Ordinal);

            CustomWidgetPomodoroEngine.ToggleStartPause(state, settings, now.AddMinutes(5));
            CustomWidgetPomodoroSnapshot sPaused = CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, now.AddMinutes(5), cultureName: "zh-CN");
            Assert.False(sPaused.IsRunning);
            Assert.True(sPaused.IsPaused);
            Assert.Equal("已暂停", sPaused.HeaderPillText);
            Assert.Equal("已暂停", sPaused.DialSubtext);

            CustomWidgetPomodoroEngine.CyclePhase(state, settings, now);
            Assert.Equal("shortBreak", state[CustomWidgetPomodoroEngine.KeyPhase]);
            Assert.Equal("短休息", CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, now, cultureName: "zh-CN").HeaderPillText);
            CustomWidgetPomodoroEngine.CyclePhase(state, settings, now);
            Assert.Equal("longBreak", state[CustomWidgetPomodoroEngine.KeyPhase]);
            CustomWidgetPomodoroEngine.CyclePhase(state, settings, now);
            Assert.Equal("focus", state[CustomWidgetPomodoroEngine.KeyPhase]);
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
    public void PomodoroEngine_TracksWallClockDeadlinesExtensionsCyclesAndDailyStreaks()
    {
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["focusMinutes"] = "25",
            ["breakPreset"] = "5,15",
            ["dailyGoal"] = "2",
            ["flowMode"] = "semi",
            ["notifyMode"] = "toast_sound"
        };
        var state = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["currentTask"] = "重构番茄时钟引擎"
        };
        var collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);

        DateTimeOffset t0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.FromHours(8));

        // 1. Initial idle snapshot
        CustomWidgetPomodoroSnapshot idle = CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, t0);
        Assert.Equal("focus", idle.Phase);
        Assert.False(idle.IsRunning);
        Assert.Equal("25:00", idle.TimeText);
        Assert.Equal(1, idle.CycleInSet);

        // 2. Start focus -> advance 10m -> extend +5m -> verify wall-clock zero drift
        CustomWidgetPomodoroEngine.ToggleStartPause(state, settings, t0);
        Assert.True(CustomWidgetPomodoroEngine.IsRunning(state));

        DateTimeOffset t10m = t0.AddMinutes(10);
        CustomWidgetPomodoroSnapshot mid = CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, t10m);
        Assert.Equal("15:00", mid.TimeText);

        CustomWidgetPomodoroEngine.ExtendSeconds(state, settings, 300, t10m);
        CustomWidgetPomodoroSnapshot extended = CustomWidgetPomodoroEngine.ComputeSnapshot(state, settings, t10m);
        Assert.Equal("20:00", extended.TimeText);
        Assert.Equal(30 * 60, extended.TotalSeconds);

        // 3. Expire focus (20m later) -> records 30m session, auto-starts 5m shortBreak ("semi" mode)
        DateTimeOffset tFocusDone = t10m.AddMinutes(20).AddSeconds(1);
        Assert.True(CustomWidgetPomodoroEngine.TryAdvanceExpiredPhase(state, settings, collections, tFocusDone, out var notice1));
        Assert.NotNull(notice1);
        Assert.Equal("shortBreak", notice1!.NextPhase);
        Assert.Equal(1, notice1.CompletedFocusCountToday);
        Assert.Single(collections["sessions"]);
        Assert.Equal("30m", collections["sessions"][0].GetField("duration"));
        Assert.True(CustomWidgetPomodoroEngine.IsRunning(state)); // semi mode auto-starts break

        // 4. Expire shortBreak -> transitions to cycle #2 focus, paused waiting for manual start ("semi" mode)
        DateTimeOffset tBreakDone = tFocusDone.AddMinutes(5).AddSeconds(1);
        Assert.True(CustomWidgetPomodoroEngine.TryAdvanceExpiredPhase(state, settings, collections, tBreakDone, out var notice2));
        Assert.NotNull(notice2);
        Assert.Equal("focus", notice2!.NextPhase);
        Assert.False(CustomWidgetPomodoroEngine.IsRunning(state));

        var ctx = new CustomWidgetEvaluationContext
        {
            Settings = settings,
            State = state,
            Collections = collections,
            ClockOverride = tBreakDone,
            CultureName = "zh-CN"
        };
        Assert.Equal("#2/4 轮", CustomWidgetExpressionEvaluator.Interpolate("${$pomodoro.cycleBadge}", ctx));
        Assert.Equal("1", CustomWidgetExpressionEvaluator.Interpolate("${$pomodoro.todayCompleted}", ctx));
        Assert.Equal("30", CustomWidgetExpressionEvaluator.Interpolate("${$pomodoro.todayMinutes}", ctx));

        // 5. Complete focus #2 -> hits dailyGoal=2 -> increments streakDays to 1
        CustomWidgetPomodoroEngine.ToggleStartPause(state, settings, tBreakDone);
        DateTimeOffset tFocus2Done = tBreakDone.AddMinutes(25).AddSeconds(1);
        Assert.True(CustomWidgetPomodoroEngine.TryAdvanceExpiredPhase(state, settings, collections, tFocus2Done, out _));
        Assert.Equal("1", state[CustomWidgetPomodoroEngine.KeyStreakDays]);

        // 6. Rollover to next day -> resets todayCompleted/todayMinutes while preserving streakDays=1
        DateTimeOffset nextDay = t0.AddDays(1);
        Assert.True(CustomWidgetPomodoroEngine.EnsureTodayRollover(state, settings, nextDay));
        Assert.Equal("0", state[CustomWidgetPomodoroEngine.KeyTodayCompleted]);
        Assert.Equal("1", state[CustomWidgetPomodoroEngine.KeyStreakDays]);
    }

    [Fact]
    public void CustomWidgetContextMenuBuilder_ExposesModuleListDisplaySubmenuAndModifyWithAi()
    {
        string src = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Services/CustomWidgets/CustomWidgetContextMenuBuilder.cs"));
        Assert.Contains("Glance.Display.Title", src, StringComparison.Ordinal);
        Assert.Contains("CustomWidget.Menu.ModifyWithAi", src, StringComparison.Ordinal);
        Assert.DoesNotContain("CustomWidget.Menu.EditWithAi", src, StringComparison.Ordinal);
    }

    [Fact]
    public void CountdownPreset_DefaultsHeroListAndProgressOnAndDeclaresModularCards()
    {
        Assert.Contains(
            CustomWidgetTemplates.Presets,
            p => p.Id == CustomWidgetTemplates.CountdownTemplateId &&
                 p.IconGlyph == "📅" &&
                 p.MonochromeFallbackGlyph == "\uE787");

        CustomWidgetTemplateBundle bundle =
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.CountdownTemplateId);
        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(bundle.ManifestJson);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));

        CustomWidgetManifest manifest = parsed.Manifest!.WithLocales(bundle.Locales);
        Assert.Equal("deskbox.countdown", manifest.Id);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("${$i18n.widgetName}", manifest.Name);
        Assert.Equal("倒数纪念日", manifest.ResolveLocalizedName("zh-CN"));
        Assert.Equal("📅", manifest.Icon);

        CustomWidgetSettingDefinition modulesDef = Assert.Single(
            manifest.Settings,
            s => s.Key == "modules" && s.Type == "moduleList");
        Assert.Equal("hero:1,list:1,yearProgress:1", modulesDef.DefaultValue);

        IReadOnlyList<CustomWidgetModuleEntry> defaultEntries =
            CustomWidgetModuleListHelper.ParseEntries(modulesDef.DefaultValue, modulesDef.Options);
        Assert.Equal(["hero", "list", "yearProgress"], defaultEntries.Where(e => e.Enabled).Select(e => e.Id).ToArray());
        Assert.Equal([], defaultEntries.Where(e => !e.Enabled).Select(e => e.Id).ToArray());

        Assert.Equal("ModuleStack", manifest.View.Type);
        Assert.Contains(manifest.View.Children, c => c.Id == "hero" && c.Type == "Card");
        Assert.Contains(manifest.View.Children, c => c.Id == "list" && c.Type == "Stack");
        Assert.Contains(manifest.View.Children, c => c.Id == "yearProgress" && c.Type == "Card");
    }

    [Fact]
    public void CountdownEngine_ComputesSolarLunarLeapMonthPaydayRolloverAndCountUp()
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["expiredMode"] = "countup",
            ["sortMode"] = "smart"
        };
        var collections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);

        DateTimeOffset now = new(2026, 10, 30, 10, 0, 0, TimeSpan.FromHours(8));

        // 1. Seed once with monochrome Fluent glyphs, and ensure deleting all does NOT re-seed
        Assert.True(CustomWidgetCountdownEngine.EnsureSeeded(state, collections, now));
        Assert.False(CustomWidgetCountdownEngine.EnsureSeeded(state, collections, now));
        Assert.Equal(4, collections[CustomWidgetCountdownEngine.CollectionName].Count);
        Assert.All(
            collections[CustomWidgetCountdownEngine.CollectionName],
            r => Assert.True(CustomWidgetTemplates.IsSegoeFluentGlyph(r.GetField("emoji"))));

        foreach (string id in collections[CustomWidgetCountdownEngine.CollectionName].Select(r => r.Id).ToArray())
        {
            Assert.True(CustomWidgetCountdownEngine.DeleteEvent(collections, id));
        }
        Assert.Empty(collections[CustomWidgetCountdownEngine.CollectionName]);
        Assert.False(CustomWidgetCountdownEngine.EnsureSeeded(state, collections, now));

        // 2. Lunar <-> Solar accuracy including 2025 leap 6th month (闰六月) and Spring Festival
        Assert.True(CustomWidgetCountdownEngine.TryLunarToSolar(2027, 1, 1, isLeapMonth: false, out DateOnly spring2027));
        Assert.Equal(new DateOnly(2027, 2, 6), spring2027);

        Assert.True(CustomWidgetCountdownEngine.TryLunarToSolar(2025, 6, 1, isLeapMonth: false, out DateOnly regularSixth2025));
        Assert.True(CustomWidgetCountdownEngine.TryLunarToSolar(2025, 6, 1, isLeapMonth: true, out DateOnly leapSixth2025));
        Assert.NotEqual(regularSixth2025, leapSixth2025);
        Assert.True(CustomWidgetCountdownEngine.TrySolarToLunar(leapSixth2025, out int ly, out int lm, out int ld, out bool isLeap));
        Assert.Equal(2025, ly);
        Assert.Equal(6, lm);
        Assert.Equal(1, ld);
        Assert.True(isLeap);

        // 3. Monthly payday rollover (today is 2026-10-30 -> 15th rolls to 2026-11-15 = 16 days)
        CustomWidgetCountdownEngine.UpsertEvent(
            collections,
            eventId: null,
            title: "发薪日",
            emoji: "💰",
            dateIso: "2026-10-15",
            calendar: CustomWidgetCountdownEngine.CalendarSolar,
            repeat: CustomWidgetCountdownEngine.RepeatMonthly,
            pinned: false,
            isLunarLeap: false,
            now: now);

        // 4. Non-repeating past milestone -> CountUp ("已经 N 天") vs Archive ("已过 N 天")
        CustomWidgetCountdownEngine.UpsertEvent(
            collections,
            eventId: null,
            title: "入职纪念",
            emoji: "🚀",
            dateIso: "2026-10-20",
            calendar: CustomWidgetCountdownEngine.CalendarSolar,
            repeat: CustomWidgetCountdownEngine.RepeatNone,
            pinned: false,
            isLunarLeap: false,
            now: now);

        // 5. Today event (0 days)
        CustomWidgetCountdownEngine.UpsertEvent(
            collections,
            eventId: null,
            title: "发布日",
            emoji: "🎉",
            dateIso: "2026-10-30",
            calendar: CustomWidgetCountdownEngine.CalendarSolar,
            repeat: CustomWidgetCountdownEngine.RepeatNone,
            pinned: false,
            isLunarLeap: false,
            now: now);

        CustomWidgetCountdownSnapshot snap = CustomWidgetCountdownEngine.ComputeSnapshot(state, settings, collections, now, cultureName: "zh-CN");
        Assert.Equal(3, snap.AllEvents.Count);

        CustomWidgetCountdownEventView payday = Assert.Single(snap.AllEvents, e => e.Title == "发薪日");
        Assert.Equal(new DateOnly(2026, 11, 15), payday.ResolvedTargetDate);
        Assert.Equal(16, payday.DisplayDays);
        Assert.Equal(CountdownDirection.Countdown, payday.Direction);

        CustomWidgetCountdownEventView milestone = Assert.Single(snap.AllEvents, e => e.Title == "入职纪念");
        Assert.Equal(10, milestone.DisplayDays);
        Assert.Equal(CountdownDirection.CountUp, milestone.Direction);
        Assert.Equal("已经", milestone.StatusWord);
        Assert.False(milestone.IsArchivedExpired);

        CustomWidgetCountdownEventView releaseToday = Assert.Single(snap.AllEvents, e => e.Title == "发布日");
        Assert.Equal(0, releaseToday.DisplayDays);
        Assert.Equal(CountdownDirection.Today, releaseToday.Direction);

        // Pin milestone to Hero and switch expiredMode to archive
        Assert.True(CustomWidgetCountdownEngine.PinEvent(collections, milestone.Id));
        settings["expiredMode"] = "archive";
        CustomWidgetCountdownSnapshot archivedSnap = CustomWidgetCountdownEngine.ComputeSnapshot(state, settings, collections, now, cultureName: "zh-CN");
        Assert.Equal(milestone.Id, archivedSnap.HeroEvent!.Id);
        Assert.True(archivedSnap.HeroEvent.IsArchivedExpired);
        Assert.Equal("已过", archivedSnap.HeroEvent.StatusWord);

        // Expression tokens for Capsule Mode
        var evalCtx = new CustomWidgetEvaluationContext
        {
            Settings = settings,
            State = state,
            Collections = collections,
            ClockOverride = now,
            CultureName = "zh-CN"
        };
        Assert.Contains("入职纪念", CustomWidgetExpressionEvaluator.Interpolate("${$countdown.capsuleTitle}", evalCtx), StringComparison.Ordinal);
        Assert.Equal("3", CustomWidgetExpressionEvaluator.Interpolate("${$countdown.count}", evalCtx));
    }

    [Fact]
    public void AiPulsePreset_ConfiguresCountryAwareWalletCurrencyOptimalBalanceAndRemainingSemantics()
    {
        Assert.Equal(
            [
                CustomWidgetTemplates.SystemPulseTemplateId,
                CustomWidgetTemplates.PomodoroTemplateId,
                CustomWidgetTemplates.CountdownTemplateId,
                CustomWidgetTemplates.AiPulseTemplateId,
                CustomWidgetTemplates.WorldClockTemplateId,
                CustomWidgetTemplates.DevToolboxTemplateId,
                CustomWidgetTemplates.FocusDialTemplateId,
                CustomWidgetTemplates.SnippetGridTemplateId,
                CustomWidgetTemplates.WeatherDeskTemplateId,
                CustomWidgetTemplates.TodoAgendaTemplateId,
                CustomWidgetTemplates.HydrationStarterId,
                CustomWidgetTemplates.AiInboxStarterId,
                CustomWidgetTemplates.NewsRssStarterId,
                CustomWidgetTemplates.AgentHudTemplateId
            ],
            CustomWidgetTemplates.Presets.Select(static p => p.Id).ToArray());

        CustomWidgetTemplateBundle zhBundle =
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.AiPulseTemplateId, "zh-CN");
        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(zhBundle.ManifestJson);
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));

        CustomWidgetManifest manifest = parsed.Manifest!.WithLocales(zhBundle.Locales);
        Assert.Equal("deskbox.ai-pulse", manifest.Id);
        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal("${$i18n.widgetName}", manifest.Name);
        Assert.Equal("AI 监控", manifest.ResolveLocalizedName("zh-CN"));
        Assert.Equal("🧠", manifest.Icon);

        CustomWidgetSettingDefinition currencyDef = Assert.Single(
            manifest.Settings,
            s => s.Key == "walletCurrency" && s.Type == "choice");
        Assert.Equal("CNY", currencyDef.DefaultValue);

        CustomWidgetSettingDefinition targetDef = Assert.Single(
            manifest.Settings,
            s => s.Key == "walletTargetBalance" && s.Type == "number");
        Assert.Equal("100", targetDef.DefaultValue);

        CustomWidgetSettingDefinition quotaMode = Assert.Single(manifest.Settings, s => s.Key == "quotaDisplayMode");
        Assert.Equal("remaining", quotaMode.DefaultValue);

        // Country/culture-specific default wallet currencies
        Assert.Equal("USD", CustomWidgetManifestParser.Parse(
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.AiPulseTemplateId, "en-US").ManifestJson)
            .Manifest!.Settings.Single(s => s.Key == "walletCurrency").DefaultValue);
        Assert.Equal("EUR", CustomWidgetManifestParser.Parse(
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.AiPulseTemplateId, "de-DE").ManifestJson)
            .Manifest!.Settings.Single(s => s.Key == "walletCurrency").DefaultValue);
        Assert.Equal("JPY", CustomWidgetManifestParser.Parse(
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.AiPulseTemplateId, "ja-JP").ManifestJson)
            .Manifest!.Settings.Single(s => s.Key == "walletCurrency").DefaultValue);
        Assert.Equal("TWD", CustomWidgetManifestParser.Parse(
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.AiPulseTemplateId, "zh-TW").ManifestJson)
            .Manifest!.Settings.Single(s => s.Key == "walletCurrency").DefaultValue);

        // Wallet optimal-balance scaling fixes DeepSeek ¥1.55 showing 100% bar
        DateTimeOffset now = DateTimeOffset.UtcNow;
        CustomWidgetAiProviderSnapshot deepSeekLow = CustomWidgetAiMonitorEngine.ParseDeepSeekBalanceJson(
            """{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"1.55","granted_balance":"0.00","topped_up_balance":"1.55"}]}""",
            now,
            "zh-CN");
        var settingsCny100 = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["walletCurrency"] = "CNY",
            ["walletTargetBalance"] = "100"
        };
        var (lowPct, lowHealth, lowTone, _) = CustomWidgetAiMonitorEngine.EvaluateEffectiveProviderMetrics(
            deepSeekLow,
            settingsCny100,
            "zh-CN");
        Assert.Equal(AiQuotaHealth.Critical, lowHealth);
        Assert.Equal("danger", lowTone);
        Assert.InRange(lowPct, 1.5, 1.6);

        // Lowering optimal target to 2 CNY turns ¥1.55 into 77.5% Healthy emerald
        var settingsCny2 = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["walletCurrency"] = "CNY",
            ["walletTargetBalance"] = "2"
        };
        var (adjustedPct, adjustedHealth, adjustedTone, _) = CustomWidgetAiMonitorEngine.EvaluateEffectiveProviderMetrics(
            deepSeekLow,
            settingsCny2,
            "zh-CN");
        Assert.Equal(AiQuotaHealth.Healthy, adjustedHealth);
        Assert.Equal("emerald", adjustedTone);
        Assert.InRange(adjustedPct, 77.0, 78.0);

        // Both ai-pulse v1.0.0 and agent-hud v1.0.0 have 0 DOM drift and 0 compiler hard/soft diagnostics
        foreach (string tplId in new[] { CustomWidgetTemplates.AiPulseTemplateId, CustomWidgetTemplates.AgentHudTemplateId })
        {
            CustomWidgetTemplateBundle rawBundle = CustomWidgetTemplates.GetTemplateBundle(tplId, "zh-CN");
            var ws = new StudioWorkspaceEngine(rawBundle.ManifestJson, rawBundle.LogicJs, rawBundle.Locales);
            Assert.False(ws.NormalizeDomInPlace(), $"Template '{tplId}' had unexpected DOM normalization drift.");

            CustomWidgetCompilerResult compiled = CustomWidgetCompilerPipeline.Compile(
                rawBundle.ManifestJson,
                rawBundle.LogicJs,
                rawBundle.Locales,
                runOnInitDryRun: true);
            Assert.Empty(compiled.HardDiagnostics);
            Assert.Empty(compiled.ActionableSoftDiagnostics);
            Assert.Equal(13, rawBundle.Locales!.Locales.Count);
        }
    }

    [Fact]
    public void AiMonitorEngine_EncryptsKeysWithDpapiAndParsesClaudeCodexAndCloudQuotas()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxAiTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        try
        {
            // 1. DPAPI encryption round-trip: plaintext secret never appears on disk
            const string testSecret = "sk-deepseek-super-secret-99887766";
            var credStore = new CustomWidgetAiCredentialStore(tempRoot);
            credStore.Upsert(new AiProviderCredentialEntry(
                CustomWidgetAiMonitorEngine.ProviderDeepSeek,
                testSecret,
                Region: "cn"));

            string diskContent = File.ReadAllText(credStore.FilePath);
            Assert.DoesNotContain(testSecret, diskContent, StringComparison.Ordinal);

            var reloadedStore = new CustomWidgetAiCredentialStore(tempRoot);
            AiProviderCredentialEntry? loaded = reloadedStore.Get(CustomWidgetAiMonitorEngine.ProviderDeepSeek);
            Assert.NotNull(loaded);
            Assert.Equal(testSecret, loaded!.ApiKey);

            // 2. Claude Code JSONL streaming deduplication + 5h block + prompt cache %
            DateTimeOffset now = new(2026, 10, 31, 14, 0, 0, TimeSpan.FromHours(8));
            string claudeDir = Path.Combine(tempRoot, "claude", "projA");
            Directory.CreateDirectory(claudeDir);
            string claudeS1 = Path.Combine(claudeDir, "s1.jsonl");
            File.WriteAllLines(claudeS1,
            [
                // Duplicate streaming chunks for msg_01 -> only final output_tokens=500 should count
                """{"type":"assistant","timestamp":"2026-10-31T04:00:00Z","message":{"id":"msg_01","model":"claude-sonnet-4-6","usage":{"input_tokens":1000,"cache_creation_input_tokens":2000,"cache_read_input_tokens":97000,"output_tokens":50}}}""",
                """{"type":"assistant","timestamp":"2026-10-31T04:00:05Z","message":{"id":"msg_01","model":"claude-sonnet-4-6","usage":{"input_tokens":1000,"cache_creation_input_tokens":2000,"cache_read_input_tokens":97000,"output_tokens":500}}}"""
            ]);
            File.SetLastWriteTimeUtc(claudeS1, now.UtcDateTime.AddMinutes(-5));

            var (claudeSnap, claudeTurns, claudeFileCount) = CustomWidgetAiMonitorEngine.AnalyzeClaudeDirectory(
                claudeDir,
                planTier: "pro",
                enabled: true,
                now: now);
            Assert.Equal(1, claudeFileCount);
            Assert.Single(claudeTurns);
            Assert.Equal(500, claudeTurns[0].OutputTokens);
            Assert.True(claudeSnap.IsConfigured);
            Assert.NotNull(claudeSnap.PrimaryWindow!.ResetsAt); // 12:00 + 5h = 17:00 local -> 3h00m remaining

            // 3. Codex CLI JSONL rate_limits parsing (5h=28% used -> 72% remaining, 7d=40% used -> 60% remaining)
            string codexDir = Path.Combine(tempRoot, "codex", "2026", "10", "31");
            Directory.CreateDirectory(codexDir);
            long reset5h = now.AddHours(2).AddMinutes(30).ToUnixTimeSeconds();
            long reset7d = now.AddDays(4).ToUnixTimeSeconds();
            string codexR1 = Path.Combine(codexDir, "rollout-1.jsonl");
            File.WriteAllLines(codexR1,
            [
                """{"timestamp":"2026-10-31T05:30:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":50000,"cached_input_tokens":40000,"output_tokens":4000,"total_tokens":54000}},"rate_limits":{"plan_type":"pro","primary":{"used_percent":28.0,"window_minutes":300,"resets_at":__R5__},"secondary":{"used_percent":40.0,"window_minutes":10080,"resets_at":__R7__}}}}"""
                    .Replace("__R5__", reset5h.ToString(), StringComparison.Ordinal)
                    .Replace("__R7__", reset7d.ToString(), StringComparison.Ordinal)
            ]);
            File.SetLastWriteTimeUtc(codexR1, now.UtcDateTime.AddMinutes(-5));

            var (codexSnap, codexSessions, codexFileCount) = CustomWidgetAiMonitorEngine.AnalyzeCodexDirectory(
                codexDir,
                enabled: true,
                now: now);
            Assert.Equal(1, codexFileCount);
            Assert.Equal("Pro", codexSnap.PlanBadge);
            Assert.Equal(72.0, codexSnap.PrimaryWindow!.RemainingPercent);
            Assert.Equal(60.0, codexSnap.SecondaryWindow!.RemainingPercent);

            CustomWidgetAiLocalSummary localSummary = CustomWidgetAiMonitorEngine.BuildLocalSummary(
                [new CustomWidgetAiMonitorEngine.CachedClaudeFileSummary(0, 0, claudeTurns)],
                codexSessions,
                claudeFileCount,
                codexFileCount,
                now);
            Assert.Equal(28, localSummary.Last28Days.Count);
            Assert.True(localSummary.TodayTokens > 150_000);
            Assert.True(localSummary.CacheHitPercent > 85.0);

            // 4. Cloud JSON parsers: DeepSeek, Kimi, GLM Coding Plan, OpenRouter
            CustomWidgetAiProviderSnapshot ds = CustomWidgetAiMonitorEngine.ParseDeepSeekBalanceJson(
                """{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"48.50","granted_balance":"10.00","topped_up_balance":"38.50"}]}""",
                now,
                cultureName: "zh-CN");
            Assert.Equal("¥ 48.50", ds.WalletFormatted);
            Assert.Contains("赠金 ¥10.00", ds.WalletBreakdown, StringComparison.Ordinal);

            CustomWidgetAiProviderSnapshot kimi = CustomWidgetAiMonitorEngine.ParseKimiBalanceJson(
                """{"code":0,"data":{"available_balance":32.8,"voucher_balance":5.0,"cash_balance":27.8}}""",
                "cn",
                now,
                cultureName: "zh-CN");
            Assert.Equal("¥ 32.80", kimi.WalletFormatted);

            CustomWidgetAiProviderSnapshot glm = CustomWidgetAiMonitorEngine.ParseGlmQuotaJson(
                $$$"""{"code":200,"data":{"level":"pro","limits":[{"type":"TOKENS_LIMIT","window":"5h","limit":5000000,"usage":1000000,"remaining":4000000,"resetTime":{{{reset5h}}}000}]}}""",
                "cn",
                now,
                cultureName: "zh-CN");
            Assert.Equal(80.0, glm.PrimaryWindow!.RemainingPercent);

            CustomWidgetAiProviderSnapshot orSnap = CustomWidgetAiMonitorEngine.ParseOpenRouterCreditsJson(
                """{"data":{"total_credits":20.0,"total_usage":5.8}}""",
                now,
                cultureName: "zh-CN");
            Assert.Equal("$ 14.20", orSnap.WalletFormatted);
            Assert.Equal(71.0, Math.Round(orSnap.WalletRemainingPercent!.Value));

            // 5. Expression evaluator tokens ($ai.*)
            var fullSnap = new CustomWidgetAiMonitorSnapshot(
                AllProviders: [claudeSnap, codexSnap, glm, ds, kimi, orSnap],
                ActiveProviders: [codexSnap, ds],
                LocalSummary: localSummary,
                UpdatedAt: now);
            var evalCtx = new CustomWidgetEvaluationContext
            {
                AiMonitor = fullSnap,
                Settings = new Dictionary<string, string> { ["quotaDisplayMode"] = "remaining" },
                State = new Dictionary<string, string> { ["selectedProvider"] = "codex" },
                CultureName = "zh-CN"
            };
            Assert.Equal("Codex · 5h 剩余 72%", CustomWidgetExpressionEvaluator.Interpolate("${$ai.capsuleTitle}", evalCtx));
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
    public void AiMonitorEngine_IncrementalByteOffsetTailing_ReadsOnlyAppendedBytesAndMatchesFreshParse()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxAiTailTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);
        try
        {
            DateTimeOffset now = new(2026, 10, 31, 14, 0, 0, TimeSpan.FromHours(8));
            string claudeDir = Path.Combine(tempRoot, "claude", "projTail");
            string codexDir = Path.Combine(tempRoot, "codex", "2026", "10", "31");
            Directory.CreateDirectory(claudeDir);
            Directory.CreateDirectory(codexDir);

            string claudeFile = Path.Combine(claudeDir, "s1.jsonl");
            string codexFile = Path.Combine(codexDir, "rollout-1.jsonl");

            long reset5h = now.AddHours(3).ToUnixTimeSeconds();
            long reset7d = now.AddDays(5).ToUnixTimeSeconds();

            // Initial write: one 18-day-old Claude turn (outside 8-day Turns window, inside 30-day DailyRollups)
            // plus one active today streaming chunk (msg_live_01 with output_tokens=100)
            string initialClaudeContent =
                "{\"type\":\"assistant\",\"timestamp\":\"2026-10-13T04:00:00Z\",\"message\":{\"id\":\"msg_old_01\",\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":4000,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":16000,\"output_tokens\":1000}}}\n" +
                "{\"type\":\"assistant\",\"timestamp\":\"2026-10-31T04:10:00Z\",\"message\":{\"id\":\"msg_live_01\",\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":2000,\"cache_creation_input_tokens\":1000,\"cache_read_input_tokens\":40000,\"output_tokens\":100}}}\n";
            File.WriteAllBytes(claudeFile, Encoding.UTF8.GetBytes(initialClaudeContent));
            File.SetLastWriteTimeUtc(claudeFile, now.UtcDateTime.AddMinutes(-10));

            string initialCodexContent =
                $$$$$"""{"timestamp":"2026-10-31T04:15:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":30000,"cached_input_tokens":20000,"output_tokens":2000,"total_tokens":32000}},"rate_limits":{"plan_type":"pro","primary":{"used_percent":20.0,"window_minutes":300,"resets_at":{{{{{reset5h}}}}}},"secondary":{"used_percent":35.0,"window_minutes":10080,"resets_at":{{{{{reset7d}}}}}}}}}""" + "\n";
            File.WriteAllBytes(codexFile, Encoding.UTF8.GetBytes(initialCodexContent));
            File.SetLastWriteTimeUtc(codexFile, now.UtcDateTime.AddMinutes(-10));

            var claudeCache = new Dictionary<string, CustomWidgetAiMonitorEngine.CachedClaudeFileSummary>(StringComparer.OrdinalIgnoreCase);
            var codexCache = new Dictionary<string, CustomWidgetAiMonitorEngine.CachedCodexFileSummary>(StringComparer.OrdinalIgnoreCase);

            var (_, initialClaudeTurns, _) = CustomWidgetAiMonitorEngine.AnalyzeClaudeDirectory(
                claudeDir,
                planTier: "pro",
                enabled: true,
                now: now,
                fileCache: claudeCache);
            var (_, _, _) = CustomWidgetAiMonitorEngine.AnalyzeCodexDirectory(
                codexDir,
                enabled: true,
                now: now,
                fileCache: codexCache);

            string canonicalClaudePath = new FileInfo(claudeFile).FullName;
            string canonicalCodexPath = new FileInfo(codexFile).FullName;
            Assert.False(claudeCache[canonicalClaudePath].WasIncrementalTail);
            Assert.False(codexCache[canonicalCodexPath].WasIncrementalTail);
            Assert.Single(initialClaudeTurns); // 18-day-old turn is compacted into DailyRollups only
            Assert.Equal(2, claudeCache[canonicalClaudePath].DailyRollups!.Count);

            // Append final streaming chunk for msg_live_01 (output_tokens 100 -> 600) + new msg_live_02
            byte[] appendedClaudeBytes = Encoding.UTF8.GetBytes(
                "{\"type\":\"assistant\",\"timestamp\":\"2026-10-31T04:10:08Z\",\"message\":{\"id\":\"msg_live_01\",\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":2000,\"cache_creation_input_tokens\":1000,\"cache_read_input_tokens\":40000,\"output_tokens\":600}}}\n" +
                "{\"type\":\"assistant\",\"timestamp\":\"2026-10-31T05:00:00Z\",\"message\":{\"id\":\"msg_live_02\",\"model\":\"claude-sonnet-4-6\",\"usage\":{\"input_tokens\":1500,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":50000,\"output_tokens\":800}}}\n");
            using (var appendClaude = new FileStream(claudeFile, FileMode.Append, FileAccess.Write))
            {
                appendClaude.Write(appendedClaudeBytes);
            }
            File.SetLastWriteTimeUtc(claudeFile, now.UtcDateTime.AddMinutes(-2));

            byte[] appendedCodexBytes = Encoding.UTF8.GetBytes(
                $$$$$"""{"timestamp":"2026-10-31T05:05:00Z","type":"event_msg","payload":{"type":"token_count","info":{"total_token_usage":{"input_tokens":80000,"cached_input_tokens":65000,"output_tokens":5500,"total_tokens":85500}},"rate_limits":{"plan_type":"pro","primary":{"used_percent":42.0,"window_minutes":300,"resets_at":{{{{{reset5h}}}}}},"secondary":{"used_percent":48.0,"window_minutes":10080,"resets_at":{{{{{reset7d}}}}}}}}}""" + "\n");
            using (var appendCodex = new FileStream(codexFile, FileMode.Append, FileAccess.Write))
            {
                appendCodex.Write(appendedCodexBytes);
            }
            File.SetLastWriteTimeUtc(codexFile, now.UtcDateTime.AddMinutes(-2));

            var (tailedClaudeSnap, _, claudeCount) = CustomWidgetAiMonitorEngine.AnalyzeClaudeDirectory(
                claudeDir,
                planTier: "pro",
                enabled: true,
                now: now,
                fileCache: claudeCache);
            var (tailedCodexSnap, tailedCodexSessions, codexCount) = CustomWidgetAiMonitorEngine.AnalyzeCodexDirectory(
                codexDir,
                enabled: true,
                now: now,
                fileCache: codexCache);

            Assert.True(claudeCache[canonicalClaudePath].WasIncrementalTail);
            Assert.Equal(appendedClaudeBytes.Length, claudeCache[canonicalClaudePath].LastBytesScanned);
            Assert.True(claudeCache[canonicalClaudePath].LastBytesScanned < new FileInfo(claudeFile).Length);

            Assert.True(codexCache[canonicalCodexPath].WasIncrementalTail);
            Assert.Equal(appendedCodexBytes.Length, codexCache[canonicalCodexPath].LastBytesScanned);
            Assert.True(codexCache[canonicalCodexPath].LastBytesScanned < new FileInfo(codexFile).Length);

            // Compare incremental tailing results against a clean full-file parse
            var freshClaudeCache = new Dictionary<string, CustomWidgetAiMonitorEngine.CachedClaudeFileSummary>(StringComparer.OrdinalIgnoreCase);
            var freshCodexCache = new Dictionary<string, CustomWidgetAiMonitorEngine.CachedCodexFileSummary>(StringComparer.OrdinalIgnoreCase);
            var (freshClaudeSnap, _, _) = CustomWidgetAiMonitorEngine.AnalyzeClaudeDirectory(
                claudeDir,
                planTier: "pro",
                enabled: true,
                now: now,
                fileCache: freshClaudeCache);
            var (freshCodexSnap, freshCodexSessions, _) = CustomWidgetAiMonitorEngine.AnalyzeCodexDirectory(
                codexDir,
                enabled: true,
                now: now,
                fileCache: freshCodexCache);

            Assert.Equal(freshClaudeSnap.PrimaryWindow!.UsedPercent, tailedClaudeSnap.PrimaryWindow!.UsedPercent, precision: 4);
            Assert.Equal(freshClaudeSnap.SecondaryWindow!.UsedPercent, tailedClaudeSnap.SecondaryWindow!.UsedPercent, precision: 4);
            Assert.Equal(58.0, tailedCodexSnap.PrimaryWindow!.RemainingPercent);
            Assert.Equal(freshCodexSnap.PrimaryWindow.RemainingPercent, tailedCodexSnap.PrimaryWindow.RemainingPercent);

            CustomWidgetAiLocalSummary tailedSummary = CustomWidgetAiMonitorEngine.BuildLocalSummary(
                claudeCache.Values,
                tailedCodexSessions,
                claudeCount,
                codexCount,
                now);
            CustomWidgetAiLocalSummary freshSummary = CustomWidgetAiMonitorEngine.BuildLocalSummary(
                freshClaudeCache.Values,
                freshCodexSessions,
                claudeCount,
                codexCount,
                now);

            Assert.Equal(freshSummary.TodayTokens, tailedSummary.TodayTokens);
            Assert.Equal(freshSummary.TodayCostUsd, tailedSummary.TodayCostUsd, precision: 6);
            Assert.Equal(freshSummary.CacheHitPercent, tailedSummary.CacheHitPercent, precision: 4);
            Assert.Equal(freshSummary.Last30DaysTokens, tailedSummary.Last30DaysTokens);
            Assert.Equal(freshSummary.Last30DaysCostUsd, tailedSummary.Last30DaysCostUsd, precision: 6);

            // Truncation / rewrite fallback: replacing with a shorter file falls back to full scan cleanly
            string rewrittenClaude =
                "{\"type\":\"assistant\",\"timestamp\":\"2026-10-31T05:30:00Z\",\"message\":{\"id\":\"msg_rewritten\",\"model\":\"claude-haiku-4-5\",\"usage\":{\"input_tokens\":500,\"cache_creation_input_tokens\":0,\"cache_read_input_tokens\":0,\"output_tokens\":250}}}\n";
            File.WriteAllBytes(claudeFile, Encoding.UTF8.GetBytes(rewrittenClaude));
            File.SetLastWriteTimeUtc(claudeFile, now.UtcDateTime);

            _ = CustomWidgetAiMonitorEngine.AnalyzeClaudeDirectory(
                claudeDir,
                planTier: "pro",
                enabled: true,
                now: now,
                fileCache: claudeCache);
            Assert.False(claudeCache[canonicalClaudePath].WasIncrementalTail);
            Assert.Single(claudeCache[canonicalClaudePath].Turns);
            Assert.Equal(750, claudeCache[canonicalClaudePath].Turns[0].TotalTokens);
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
    public void AiMonitorViewModel_StructuralSnapshotEquality_DistinguishesLiveMetricsFromLayoutChanges()
    {
        DateTimeOffset t0 = new(2026, 10, 31, 14, 0, 0, TimeSpan.FromHours(8));
        DateOnly startDate = new(2026, 10, 18);
        CustomWidgetAiDailyBucket[] bucketsA = Enumerable.Range(0, 14)
            .Select(i => new CustomWidgetAiDailyBucket(startDate.AddDays(i), (i + 1) * 10_000, (i + 1) * 0.15))
            .ToArray();
        CustomWidgetAiDailyBucket[] bucketsB = Enumerable.Range(0, 14)
            .Select(i => new CustomWidgetAiDailyBucket(startDate.AddDays(i), (i + 1) * 25_000, (i + 1) * 0.42))
            .ToArray();

        var claudeA = new CustomWidgetAiProviderSnapshot(
            Id: CustomWidgetAiMonitorEngine.ProviderClaude,
            Name: "Claude Code",
            ShortName: "Claude",
            Monogram: "C",
            BrandHex: "#D97757",
            BillingKind: AiProviderBillingKind.WindowQuota,
            IsLocalCli: true,
            IsConfigured: true,
            HasLiveData: true,
            PlanBadge: "Pro",
            Health: AiQuotaHealth.Healthy,
            PrimaryWindow: new CustomWidgetAiWindowQuota("5H 窗口", 22.0, t0.AddHours(3), "3h 00m 后重置", AiQuotaHealth.Healthy),
            SecondaryWindow: new CustomWidgetAiWindowQuota("7D 周额度", 35.0, t0.AddDays(4), "4天 后重置", AiQuotaHealth.Healthy),
            CurrencySymbol: "$",
            WalletBalance: 0,
            WalletFormatted: string.Empty,
            WalletBreakdown: string.Empty,
            WalletRemainingPercent: null,
            RadarValueText: "78%",
            RadarSubtext: "5h · 3h 00m 后重置",
            ErrorMessage: null,
            UpdatedAt: t0);

        var deepSeekA = CustomWidgetAiMonitorEngine.ParseDeepSeekBalanceJson(
            """{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"48.50","granted_balance":"10.00","topped_up_balance":"38.50"}]}""",
            t0);

        var claudeB = claudeA with
        {
            PlanBadge = "Max 5x",
            Health = AiQuotaHealth.Warning,
            PrimaryWindow = new CustomWidgetAiWindowQuota("5H 窗口", 86.0, t0.AddMinutes(45), "45m 后重置", AiQuotaHealth.Warning),
            SecondaryWindow = new CustomWidgetAiWindowQuota("7D 周额度", 62.0, t0.AddDays(3), "3天 后重置", AiQuotaHealth.Healthy),
            RadarValueText = "14%",
            RadarSubtext = "5h · 45m 后重置",
            UpdatedAt = t0.AddMinutes(1)
        };

        var deepSeekB = CustomWidgetAiMonitorEngine.ParseDeepSeekBalanceJson(
            """{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"9.20","granted_balance":"0.00","topped_up_balance":"9.20"}]}""",
            t0.AddMinutes(1));

        var snapA = new CustomWidgetAiMonitorSnapshot(
            AllProviders: [claudeA, deepSeekA],
            ActiveProviders: [claudeA, deepSeekA],
            LocalSummary: new CustomWidgetAiLocalSummary(120_000, 1.25, 88.0, 950_000, 12.40, 3, 1, bucketsA),
            UpdatedAt: t0);

        var snapB = new CustomWidgetAiMonitorSnapshot(
            AllProviders: [claudeB, deepSeekB],
            ActiveProviders: [claudeB, deepSeekB],
            LocalSummary: new CustomWidgetAiLocalSummary(480_000, 5.60, 92.0, 1_310_000, 16.75, 4, 2, bucketsB),
            UpdatedAt: t0.AddMinutes(1));

        var state = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CustomWidgetAiMonitorEngine.KeySelectedProvider] = CustomWidgetAiMonitorEngine.ProviderClaude
        };

        // Routine 60s metric/health/balance/bucket changes are structurally equal -> live update in place
        Assert.True(CustomWidgetViewModel.AreAiMonitorSnapshotsStructurallyEqual(snapA, snapB, state));

        // Initial Empty -> populated snapshot is structurally different -> full tree build
        Assert.False(CustomWidgetViewModel.AreAiMonitorSnapshotsStructurallyEqual(
            CustomWidgetAiMonitorSnapshot.Empty,
            snapA,
            state));

        // Provider transitioning into ErrorMessage state requires swapping tile layout to error card
        var snapWithError = snapB with
        {
            ActiveProviders = [claudeB, deepSeekB with { ErrorMessage = "HTTP 401 Unauthorized" }]
        };
        Assert.False(CustomWidgetViewModel.AreAiMonitorSnapshotsStructurallyEqual(snapA, snapWithError, state));

        // Active provider list count change requires top pill bar / radar row rebuild
        var snapSingleProvider = snapB with { ActiveProviders = [claudeB] };
        Assert.False(CustomWidgetViewModel.AreAiMonitorSnapshotsStructurallyEqual(snapA, snapSingleProvider, state));
    }

    [Fact]
    public void CountdownViewModel_NextMidnightDelay_TargetsLocalMidnightInsteadOfOneMinutePolling()
    {
        TimeZoneInfo localTz = TimeZoneInfo.Local;
        DateTime localAfternoon = new(2026, 10, 31, 14, 30, 0, DateTimeKind.Unspecified);
        DateTimeOffset afternoonOffset = new(localAfternoon, localTz.GetUtcOffset(localAfternoon));

        TimeSpan afternoonDelay = CustomWidgetViewModel.ComputeCountdownNextMidnightDelay(afternoonOffset);
        Assert.InRange(
            afternoonDelay.TotalMinutes,
            (9 * 60) + 29.9,
            (9 * 60) + 30.1);

        DateTime localJustBeforeMidnight = new(2026, 10, 31, 23, 59, 50, DateTimeKind.Unspecified);
        DateTimeOffset nearMidnightOffset = new(localJustBeforeMidnight, localTz.GetUtcOffset(localJustBeforeMidnight));

        TimeSpan nearMidnightDelay = CustomWidgetViewModel.ComputeCountdownNextMidnightDelay(nearMidnightOffset);
        Assert.InRange(nearMidnightDelay.TotalSeconds, 10.0, 10.5);
    }

    [Fact]
    public void AiMonitor_SiliconFlowMiniMaxAndOneApiParsers_HandleBalancesWindowsAndUnlimitedQuotas()
    {
        DateTimeOffset now = new(2026, 11, 17, 14, 0, 0, TimeSpan.FromHours(8));

        var sfCn = CustomWidgetAiMonitorEngine.ParseSiliconFlowBalanceJson(
            """{"code":20000,"status":true,"data":{"balance":"2.50","chargeBalance":"86.00","totalBalance":"88.50"}}""",
            "cn",
            now,
            "zh-CN");
        Assert.True(sfCn.HasLiveData);
        Assert.Equal(AiProviderBillingKind.WalletBalance, sfCn.BillingKind);
        Assert.Equal(88.50, sfCn.WalletBalance, 2);
        Assert.Equal("¥ 88.50", sfCn.WalletFormatted);
        Assert.Equal("CNY", sfCn.PlanBadge);

        var mmPlan = CustomWidgetAiMonitorEngine.ParseMiniMaxQuotaJson(
            """
            {
              "code": 0,
              "data": {
                "current_interval_total_count": 5000000,
                "current_interval_usage_count": 1000000,
                "current_interval_reset_time": "2026-11-17T16:30:00+08:00",
                "current_weekly_total_count": 35000000,
                "current_weekly_usage_count": 3500000,
                "current_weekly_reset_time": "2026-11-22T00:00:00+08:00"
              }
            }
            """,
            "cn",
            now,
            "zh-CN");
        Assert.True(mmPlan.HasLiveData);
        Assert.Equal(AiProviderBillingKind.WindowQuota, mmPlan.BillingKind);
        Assert.NotNull(mmPlan.PrimaryWindow);
        Assert.NotNull(mmPlan.SecondaryWindow);
        Assert.Equal(80.0, mmPlan.PrimaryWindow!.RemainingPercent, 1);
        Assert.Equal(90.0, mmPlan.SecondaryWindow!.RemainingPercent, 1);

        var mmAuthErr = CustomWidgetAiMonitorEngine.ParseMiniMaxQuotaJson(
            """{"base_resp":{"status_code":1004,"status_msg":"cookie is missing"}}""",
            "cn",
            now,
            "en-US");
        Assert.False(mmAuthErr.HasLiveData);
        Assert.NotNull(mmAuthErr.ErrorMessage);

        Assert.Equal(
            "https://api.myrelay.com",
            CustomWidgetAiMonitorEngine.NormalizeGatewayBaseUrl("https://api.myrelay.com/v1/chat/completions/"));

        var oneApiFinite = CustomWidgetAiMonitorEngine.ParseOneApiBillingJson(
            """{"hard_limit_usd":50.0,"used_usd":12.4,"host":"api.myrelay.com"}""",
            now,
            "zh-CN");
        Assert.Equal(37.6, oneApiFinite.WalletBalance, 2);
        Assert.Equal("$ 37.60", oneApiFinite.WalletFormatted);

        var oneApiUnlimited = CustomWidgetAiMonitorEngine.ParseOneApiBillingJson(
            """{"hard_limit_usd":100000000,"used_usd":18.5,"host":"api.myrelay.com"}""",
            now,
            "zh-CN");
        Assert.Equal("∞", oneApiUnlimited.WalletFormatted);
        var (unlimPct, unlimHealth, _, _) = CustomWidgetAiMonitorEngine.EvaluateEffectiveProviderMetrics(
            oneApiUnlimited,
            new Dictionary<string, string> { ["walletCurrency"] = "USD", ["walletTargetBalance"] = "500" },
            "en-US");
        Assert.Equal(100.0, unlimPct, 1);
        Assert.Equal(AiQuotaHealth.Healthy, unlimHealth);
    }

    [Fact]
    public void AiMonitor_GeminiCliLocalAnalyzer_ParsesSessionsWithoutDoubleCountingCacheAndBuilds28DayRollup()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "deskbox-gemini-test-" + Guid.NewGuid().ToString("N"));
        string chatsDir = Path.Combine(tempDir, "proj-hash-01", "chats");
        Directory.CreateDirectory(chatsDir);
        try
        {
            DateTimeOffset now = new(2026, 11, 17, 15, 0, 0, TimeSpan.FromHours(8));
            string sessionPath = Path.Combine(chatsDir, "session-001.json");
            File.WriteAllText(
                sessionPath,
                """
                {
                  "sessionId": "s-1",
                  "startTime": "2026-11-17T13:10:00+08:00",
                  "messages": [
                    {
                      "type": "gemini",
                      "timestamp": "2026-11-17T13:15:00+08:00",
                      "model": "gemini-2.5-pro",
                      "tokens": {
                        "prompt": 100000,
                        "cached": 80000,
                        "candidates": 5000,
                        "thoughts": 3000,
                        "total": 108000
                      }
                    }
                  ]
                }
                """);

            var cache = new Dictionary<string, CustomWidgetAiMonitorEngine.CachedGeminiFileSummary>(StringComparer.OrdinalIgnoreCase);
            var (snap, files, fileCount) = CustomWidgetAiMonitorEngine.AnalyzeGeminiCliDirectory(
                tempDir,
                enabled: true,
                now: now,
                fileCache: cache,
                cultureName: "zh-CN");

            Assert.Equal(1, fileCount);
            Assert.True(snap.IsConfigured);
            Assert.True(snap.HasLiveData);

            // Uncached input = 100k - 80k = 20k; total = 20k + 80k + 5k + 3k = 108k (never 188k!)
            CustomWidgetAiLocalSummary summary = CustomWidgetAiMonitorEngine.BuildLocalSummary(
                Array.Empty<CustomWidgetAiMonitorEngine.CachedClaudeFileSummary>(),
                [],
                claudeFiles: 0,
                codexFiles: 0,
                now: now,
                geminiSessions: files,
                geminiFiles: fileCount);

            Assert.Equal(108_000, summary.TodayTokens);
            Assert.Equal(80.0, summary.CacheHitPercent, 1);
            Assert.Equal(28, summary.EffectiveLast28Days.Count);
            Assert.Equal(108_000, summary.EffectiveLast28Days[^1].TotalTokens);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void AiMonitor_WalletLedgerAndPaceDetector_TracksDailySpendAcrossTopUpsAndProjectsSchemeDLedger()
    {
        var ledger = new CustomWidgetAiWalletLedger(null, inMemoryOnly: true);
        DateOnly d1 = new(2026, 11, 17);

        Assert.Equal(0.0, ledger.RecordObservation("deepseek", d1, 50.00, "¥"), 3);
        Assert.Equal(3.40, ledger.RecordObservation("deepseek", d1, 46.60, "¥"), 3);
        // User tops up +100 -> spend accumulated earlier today must be preserved, not erased!
        Assert.Equal(3.40, ledger.RecordObservation("deepseek", d1, 146.60, "¥"), 3);
        Assert.Equal(5.00, ledger.RecordObservation("deepseek", d1, 145.00, "¥"), 3);

        DateTimeOffset now = new(2026, 11, 17, 16, 0, 0, TimeSpan.FromHours(8));
        // 55% used when only 30 minutes (10%) of 5h window elapsed -> pace is hot!
        Assert.True(CustomWidgetAiMonitorEngine.EvaluateWindowPaceIsHot(
            usedPercent: 55.0,
            resetsAt: now.AddHours(4).AddMinutes(30),
            now: now,
            windowDuration: TimeSpan.FromHours(5)));
        // 55% used when 4 hours (80%) of 5h window elapsed -> sustainable pace!
        Assert.False(CustomWidgetAiMonitorEngine.EvaluateWindowPaceIsHot(
            usedPercent: 55.0,
            resetsAt: now.AddHours(1),
            now: now,
            windowDuration: TimeSpan.FromHours(5)));

        var dsSnap = CustomWidgetAiMonitorEngine.ParseDeepSeekBalanceJson(
            """{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"1.55","granted_balance":"1.55","topped_up_balance":"0.00"}]}""",
            now,
            "zh-CN") with { WalletTodaySpend = 5.00 };

        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["walletCurrency"] = "CNY",
            ["walletTargetBalance"] = "100"
        };
        string walletLine = CustomWidgetAiMonitorEngine.BuildProviderLedgerSummary(dsSnap, settings, now, "zh-CN");
        Assert.Contains("5.00", walletLine);
        Assert.Contains("¥100", walletLine);

        var buckets28 = Enumerable.Range(0, 28)
            .Select(i => new CustomWidgetAiDailyBucket(
                d1.AddDays(-(27 - i)),
                i == 27 ? 65_000 : (i % 4 == 0 ? 0 : 40_000),
                i == 16 ? 8.50 : (i % 4 == 0 ? 0.0 : 0.45)))
            .ToArray();
        var monSnap = new CustomWidgetAiMonitorSnapshot(
            AllProviders: [dsSnap],
            ActiveProviders: [dsSnap],
            LocalSummary: new CustomWidgetAiLocalSummary(140_000, 1.20, 84.0, 2_100_000, 18.40, 2, 1, buckets28, 1),
            UpdatedAt: now);

        var evalCtx = new CustomWidgetEvaluationContext
        {
            AiMonitor = monSnap,
            Settings = settings,
            CultureName = "zh-CN"
        };
        Assert.True(CustomWidgetExpressionEvaluator.TryResolveVirtualCollection("$ai.daily28", evalCtx, out var d28Records));
        Assert.Equal(28, d28Records.Count);
        Assert.Equal("subtle", d28Records[0].GetField("tone"));
        Assert.Equal("warning", d28Records[16].GetField("tone"));
        Assert.Equal("emerald", d28Records[27].GetField("tone"));
    }
}
