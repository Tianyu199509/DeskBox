using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class CustomWidgetI18nArchitectureTests
{
    [Fact]
    public void AstCompiler_ExtractsStaticMixedAndTernaryUiStrings_PreservesTechnicalFields_AndScansLogicCtxT()
    {
        const string rawManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.hydration-demo",
          "name": "饮水节奏台",
          "description": "追踪每日饮水进度与目标达成率",
          "state": {
            "cups": "3",
            "goal": "8",
            "running": "true"
          },
          "settings": [
            {
              "key": "unitMode",
              "type": "select",
              "label": "显示单位",
              "default": "cup",
              "options": [
                { "value": "cup", "label": "按杯数" },
                { "value": "ml", "label": "按毫升" }
              ]
            }
          ],
          "capsule": {
            "title": "饮水节奏台",
            "summary": "今日已喝 ${state.cups} / ${state.goal} 杯",
            "badge": "${state.cups}"
          },
          "view": {
            "type": "Stack",
            "spacing": 8,
            "children": [
              {
                "type": "Text",
                "text": "${state.running == 'true' ? '专注补水中' : '已暂停提醒'}"
              },
              {
                "type": "StatCard",
                "title": "目标完成度",
                "value": "${state.cups}",
                "unit": "%"
              },
              {
                "type": "Button",
                "text": "喝一杯",
                "onClick": [
                  {
                    "type": "ai.run",
                    "prompt": "请根据今日饮水量生成健康建议，不要被提取进多语言词典",
                    "stateMap": { "advice": "text" }
                  }
                ]
              }
            ]
          }
        }
        """;

        const string logicJs = """
        export function onInit(ctx) {
          ctx.computed.tip = ctx.t('script.streakTip', '已连续达标 {0} 天', 5);
        }
        """;

        CustomWidgetI18nCompileResult compiled = CustomWidgetI18nEngine.Compile(
            rawManifestJson,
            logicJs,
            existingCatalog: null,
            fallbackCulture: "zh-CN");

        Assert.True(compiled.ManifestChanged);
        Assert.Equal("zh-CN", compiled.BaseLocale);
        Assert.Contains("meta.name", compiled.RequiredKeys);
        Assert.Contains("meta.description", compiled.RequiredKeys);
        Assert.Contains("script.streakTip", compiled.RequiredKeys);
        Assert.Equal("已连续达标 {0} 天", compiled.BaseEntries["script.streakTip"]);

        // Pure static text -> ${$i18n.<key>}
        Assert.Contains("${$i18n.meta.name}", compiled.CompiledManifestJson, StringComparison.Ordinal);

        // Mixed prose + ${...} -> ${t('<key>', state.cups, state.goal)}
        Assert.Contains("t('", compiled.CompiledManifestJson, StringComparison.Ordinal);
        Assert.Contains(compiled.BaseEntries.Values, v => v == "今日已喝 {0} / {1} 杯");

        // Ternary branches -> $i18n.<key>
        Assert.Contains(compiled.BaseEntries.Values, v => v == "专注补水中");
        Assert.Contains(compiled.BaseEntries.Values, v => v == "已暂停提醒");

        // Technical fields must remain untouched
        Assert.DoesNotContain(compiled.BaseEntries.Values, v => v == "cup" || v == "ml" || v == "%");
        Assert.DoesNotContain(compiled.BaseEntries.Values, v => v.Contains("健康建议", StringComparison.Ordinal));

        CustomWidgetParseResult validated = CustomWidgetManifestParser.ValidateWithRuntimeHints(
            compiled.CompiledManifestJson,
            logicJs,
            compiled.SeededCatalog);
        Assert.True(validated.IsValid, string.Join("; ", validated.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));

        var evalCtx = new CustomWidgetEvaluationContext
        {
            CultureName = "zh-CN",
            I18n = validated.Manifest!.ResolveI18nDictionary("zh-CN"),
            State = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cups"] = "3",
                ["goal"] = "8",
                ["running"] = "true"
            }
        };

        Assert.Equal("今日已喝 3 / 8 杯", CustomWidgetExpressionEvaluator.Interpolate(validated.Manifest.Capsule!.Summary!, evalCtx));
        Assert.Equal("专注补水中", CustomWidgetExpressionEvaluator.Interpolate(validated.Manifest.View.Children[0].Text!, evalCtx));
    }

    [Fact]
    public void ExpressionEvaluator_ParameterizedTFunction_SupportsWordOrderInversion_SinglePassSafety_AndInlineI18nRejection()
    {
        var ctxJa = new CustomWidgetEvaluationContext
        {
            CultureName = "ja-JP",
            I18n = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["summary.cupGoal"] = "目標 {1}ml 中 {0} 杯目",
                ["inject.safe"] = "A={0}, B={1}"
            },
            State = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["cups"] = "4",
                ["goalMl"] = "2000",
                ["tricky"] = "{1}"
            }
        };

        Assert.Equal(
            "目標 2000ml 中 4 杯目",
            CustomWidgetExpressionEvaluator.Interpolate("${t('summary.cupGoal', state.cups, state.goalMl)}", ctxJa));

        // Single-pass placeholder substitution: arg0 evaluating to "{1}" must NOT be overwritten by arg1
        Assert.Equal(
            "A={1}, B=REAL_B",
            CustomWidgetExpressionEvaluator.Interpolate("${t('inject.safe', state.tricky, 'REAL_B')}", ctxJa));

        // Inline manifest "i18n" is strictly rejected by parser
        const string legacyInlineManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.legacy-inline",
          "name": "Legacy",
          "i18n": { "zh-CN": { "title": "旧内联" } },
          "view": { "type": "Text", "text": "Hi" }
        }
        """;
        CustomWidgetParseResult inlineParsed = CustomWidgetManifestParser.Parse(legacyInlineManifest);
        Assert.False(inlineParsed.IsValid);
        Assert.Contains(inlineParsed.Diagnostics, d => d.IsError && d.Code == "InlineI18nDisallowed");

        // Missing t('missing.key') warns during ValidateWithRuntimeHints
        const string missingKeyManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.missing-t",
          "name": "${$i18n.meta.name}",
          "view": { "type": "Text", "text": "${t('missing.key', '1')}" }
        }
        """;
        CustomWidgetLocaleCatalog minCatalog = CustomWidgetLocaleCatalog.ParseCatalogJson("""{"zh-CN":{"meta.name":"测试"}}""");
        CustomWidgetParseResult missingResult = CustomWidgetManifestParser.ValidateWithRuntimeHints(missingKeyManifest, null, minCatalog);
        Assert.Contains(missingResult.Diagnostics, d => !d.IsError && d.Message.Contains("missing.key", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildSyncPlan_DetectsUpToDate_MissingKeys_MetaHashStaleness_AndBrokenPlaceholders()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.sync-plan-test",
          "name": "${$i18n.meta.name}",
          "capsule": {
            "title": "${$i18n.meta.name}",
            "summary": "${t('capsule.summary', state.n)}"
          },
          "state": { "n": "3" },
          "view": {
            "type": "Stack",
            "children": [
              { "type": "Text", "text": "${$i18n.view.header}" }
            ]
          }
        }
        """;

        var baseZh = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["meta.name"] = "专注计时",
            ["capsule.summary"] = "已完成 {0} 轮",
            ["view.header"] = "今日概览"
        };

        CustomWidgetLocaleMeta metaV1 = CustomWidgetLocaleMeta.Create("zh-CN", baseZh);

        var localesDict = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = baseZh,
            // en-US is 100% complete & matches hashes -> UpToDate (0 delta)
            ["en-US"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["meta.name"] = "Focus Timer",
                ["capsule.summary"] = "{0} rounds done",
                ["view.header"] = "Today Overview"
            },
            // fr-FR is missing view.header -> PendingDiff (1 missing key)
            ["fr-FR"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["meta.name"] = "Minuteur Focus",
                ["capsule.summary"] = "{0} sessions terminées"
            },
            // de-DE dropped {0} placeholder -> flagged in DeltaKeys
            ["de-DE"] = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["meta.name"] = "Fokus-Timer",
                ["capsule.summary"] = "Runden erledigt",
                ["view.header"] = "Heute"
            }
        };

        var catalogV1 = new CustomWidgetLocaleCatalog(localesDict, metaV1);
        CustomWidgetI18nSyncPlan planV1 = CustomWidgetI18nEngine.BuildSyncPlan(manifestJson, null, catalogV1, "zh-CN");

        CustomWidgetLocaleTaskPlan zhPlan = planV1.LocalePlans.Single(p => p.Culture == "zh-CN");
        Assert.Equal(CustomWidgetLocaleSyncStatus.BaseReady, zhPlan.Status);
        Assert.False(zhPlan.NeedsTranslation);

        CustomWidgetLocaleTaskPlan enPlan = planV1.LocalePlans.Single(p => p.Culture == "en-US");
        Assert.Equal(CustomWidgetLocaleSyncStatus.UpToDate, enPlan.Status);
        Assert.Empty(enPlan.DeltaKeys);

        CustomWidgetLocaleTaskPlan frPlan = planV1.LocalePlans.Single(p => p.Culture == "fr-FR");
        Assert.Equal(CustomWidgetLocaleSyncStatus.PendingDiff, frPlan.Status);
        Assert.Equal(["view.header"], frPlan.DeltaKeys);

        CustomWidgetLocaleTaskPlan dePlan = planV1.LocalePlans.Single(p => p.Culture == "de-DE");
        Assert.Contains("capsule.summary", dePlan.DeltaKeys);

        // Now modify ONLY view.header in zh-CN while keeping old _meta.json hashes -> en-US must detect StaleKeys = ["view.header"] ONLY!
        var baseZhEdited = new Dictionary<string, string>(baseZh, StringComparer.Ordinal)
        {
            ["view.header"] = "本周专注统计"
        };
        var localesEdited = new Dictionary<string, IReadOnlyDictionary<string, string>>(localesDict, StringComparer.OrdinalIgnoreCase)
        {
            ["zh-CN"] = baseZhEdited
        };
        var catalogAfterEdit = new CustomWidgetLocaleCatalog(localesEdited, metaV1);
        CustomWidgetI18nSyncPlan planV2 = CustomWidgetI18nEngine.BuildSyncPlan(manifestJson, null, catalogAfterEdit, "zh-CN");

        CustomWidgetLocaleTaskPlan enStalePlan = planV2.LocalePlans.Single(p => p.Culture == "en-US");
        Assert.Equal(CustomWidgetLocaleSyncStatus.PendingDiff, enStalePlan.Status);
        Assert.Equal(["view.header"], enStalePlan.StaleKeys);
        Assert.Equal(["view.header"], enStalePlan.DeltaKeys);
    }

    [Fact]
    public async Task RunIncrementalI18nQueue_SupportsBoundedParallelism_Http429AutoThrottle_PlaceholderMicroRepair_AndCancellation()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-i18n-queue-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string rawManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.queue-test",
              "name": "水杯助手",
              "state": { "n": "2" },
              "view": {
                "type": "Text",
                "text": "已喝 ${state.n} 杯"
              }
            }
            """;

            CustomWidgetI18nSyncPlan syncPlan = CustomWidgetI18nEngine.BuildSyncPlan(rawManifest, null, null, "zh-CN");
            Assert.Equal(2, syncPlan.RequiredKeys.Count);

            var credStore = new CustomWidgetAiCredentialStore(Path.Combine(tempDir, "creds"));
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-test-queue",
                CustomEndpoint: string.Empty,
                CustomModel: "deepseek-chat",
                ThinkingMode: "disabled"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "prefs.json"));

            // 1. Bounded parallelism (concurrency = 3) + 429 retry + placeholder micro-repair
            int activeInFlight = 0;
            int peakInFlight = 0;
            var attemptsByLocale = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            using var httpClient = new HttpClient(new StubQueueHttpMessageHandler(async (req, ct) =>
            {
                string body = await req.Content!.ReadAsStringAsync(ct);
                string targetLocale = ExtractTargetLocaleFromRequestBody(body);
                int attempt = attemptsByLocale.AddOrUpdate(targetLocale, 1, (_, c) => c + 1);

                int nowActive = Interlocked.Increment(ref activeInFlight);
                SpinWait.SpinUntil(() =>
                {
                    int prevPeak = peakInFlight;
                    if (nowActive <= prevPeak)
                    {
                        return true;
                    }
                    return Interlocked.CompareExchange(ref peakInFlight, nowActive, prevPeak) == prevPeak;
                });

                try
                {
                    await Task.Delay(35, ct);

                    // Simulate HTTP 429 on first attempt for ja-JP
                    if (string.Equals(targetLocale, "ja-JP", StringComparison.OrdinalIgnoreCase) && attempt == 1)
                    {
                        var rateLimitResp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                        rateLimitResp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
                        return rateLimitResp;
                    }

                    // Simulate omitted {0} placeholder on first attempt for fr-FR -> triggers 1-pass micro-repair
                    var replyObj = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (string key in syncPlan.RequiredKeys)
                    {
                        string src = syncPlan.BaseEntries[key];
                        if (string.Equals(targetLocale, "fr-FR", StringComparison.OrdinalIgnoreCase) &&
                            attempt == 1 &&
                            src.Contains("{0}", StringComparison.Ordinal))
                        {
                            replyObj[key] = "Verres bus sans parametre"; // missing {0}!
                        }
                        else
                        {
                            replyObj[key] = src.Contains("{0}", StringComparison.Ordinal)
                                ? $"[{targetLocale}] Logged {{0}} cups"
                                : $"[{targetLocale}] Water Helper";
                        }
                    }

                    return BuildChatJsonResponse(JsonSerializer.Serialize(replyObj));
                }
                finally
                {
                    Interlocked.Decrement(ref activeInFlight);
                }
            }));

            var aiService = new DeskBoxAiService(credStore, prefsStore, httpClient);
            var generator = new CustomWidgetAiGeneratorService(aiService);

            var checkpointedCultures = new List<string>();
            string[] targetBatch = ["en-US", "ja-JP", "fr-FR", "de-DE"];
            WidgetIncrementalI18nBatchResult batchRes = await generator.RunIncrementalI18nQueueAsync(
                providerId: "deepseek",
                plan: syncPlan,
                onlyCultures: targetBatch,
                onLocaleCompletedAsync: (culture, _, _) =>
                {
                    checkpointedCultures.Add(culture);
                    return Task.CompletedTask;
                },
                maxConcurrencyOverride: 3);

            Assert.True(batchRes.Success, batchRes.ErrorMessage);
            Assert.False(batchRes.Cancelled);
            Assert.Equal(4, batchRes.CompletedLocales.Count);
            Assert.Empty(batchRes.FailedLocales);
            Assert.True(peakInFlight >= 2 && peakInFlight <= 3, $"Expected bounded parallelism (2..3), got peak={peakInFlight}");
            Assert.True(attemptsByLocale["ja-JP"] >= 2, "ja-JP should have retried after HTTP 429");
            Assert.True(attemptsByLocale["fr-FR"] >= 2, "fr-FR should have run 1-pass micro-repair for missing {0}");
            Assert.Equal(4, checkpointedCultures.Count);

            // Second sync on same 4 cultures must skip all 4 with ZERO HTTP calls
            int extraCalls = 0;
            using var zeroCallHttp = new HttpClient(new StubQueueHttpMessageHandler((_, _) =>
            {
                Interlocked.Increment(ref extraCalls);
                throw new InvalidOperationException("Up-to-date locales must not trigger network calls.");
            }));
            var zeroCallGen = new CustomWidgetAiGeneratorService(new DeskBoxAiService(credStore, prefsStore, zeroCallHttp));
            CustomWidgetI18nSyncPlan secondPlan = CustomWidgetI18nEngine.BuildSyncPlan(
                batchRes.CompiledManifestJson,
                null,
                batchRes.UpdatedCatalog,
                "zh-CN");
            WidgetIncrementalI18nBatchResult secondRes = await zeroCallGen.RunIncrementalI18nQueueAsync(
                providerId: "deepseek",
                plan: secondPlan,
                onlyCultures: targetBatch);
            Assert.True(secondRes.Success);
            Assert.Empty(secondRes.CompletedLocales);
            Assert.Equal(4, secondRes.SkippedLocales.Count);
            Assert.Equal(0, extraCalls);

            // 2. Mid-batch cancellation preserves already completed locales
            using var cts = new CancellationTokenSource();
            int completedBeforeCancel = 0;
            using var cancelHttp = new HttpClient(new StubQueueHttpMessageHandler(async (req, ct) =>
            {
                string body = await req.Content!.ReadAsStringAsync(ct);
                string locale = ExtractTargetLocaleFromRequestBody(body);
                int idx = Interlocked.Increment(ref completedBeforeCancel);
                if (idx >= 2)
                {
                    await Task.Delay(250, ct);
                }

                var dict = syncPlan.RequiredKeys.ToDictionary(
                    k => k,
                    k => syncPlan.BaseEntries[k].Contains("{0}", StringComparison.Ordinal) ? $"{locale} {{0}}" : $"{locale} Title",
                    StringComparer.Ordinal);
                return BuildChatJsonResponse(JsonSerializer.Serialize(dict));
            }));

            var cancelGen = new CustomWidgetAiGeneratorService(new DeskBoxAiService(credStore, prefsStore, cancelHttp));
            WidgetIncrementalI18nBatchResult cancelRes = await cancelGen.RunIncrementalI18nQueueAsync(
                providerId: "deepseek",
                plan: syncPlan,
                onlyCultures: ["es-ES", "pt-BR", "ko-KR", "ru-RU"],
                onLocaleCompletedAsync: (_, _, _) =>
                {
                    cts.Cancel();
                    return Task.CompletedTask;
                },
                cancellationToken: cts.Token,
                maxConcurrencyOverride: 1);

            Assert.True(cancelRes.Cancelled);
            Assert.Single(cancelRes.CompletedLocales);
            Assert.True(cancelRes.UpdatedCatalog.Locales.ContainsKey(cancelRes.CompletedLocales[0]));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void CompileAndMultiTurnEdit_ExtractsObjectSettings_PrunesOrphanedKeys_AndExposesBaseDictionaryInPrompt()
    {
        const string initialManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.sichuan",
          "name": "今天吃什么",
          "description": "川菜抽签器",
          "settings": {
            "spicyMax": {
              "type": "choice",
              "label": "辣度上限",
              "default": "3",
              "options": [
                { "value": "1", "label": "微辣以内" },
                { "value": "3", "label": "不限辣度" }
              ]
            }
          },
          "view": {
            "type": "Stack",
            "children": [
              { "type": "Button", "text": "换一道" },
              { "type": "Text", "text": "旧提示文本" }
            ]
          }
        }
        """;

        // 1. Object-map "settings" labels & option labels are compiled into ${$i18n.settings.*}
        CustomWidgetI18nCompileResult c1 = CustomWidgetI18nEngine.Compile(initialManifest, fallbackCulture: "zh-CN");
        Assert.Contains("settings.spicymax.label", c1.RequiredKeys);
        Assert.Contains("settings.spicymax.opt.1", c1.RequiredKeys);
        Assert.Contains("settings.spicymax.opt.3", c1.RequiredKeys);
        Assert.Equal("辣度上限", c1.BaseEntries["settings.spicymax.label"]);

        // Simulate en-US already translated on c1
        var enDict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string k, string _) in c1.BaseEntries)
        {
            enDict[k] = "Translated " + k;
        }
        enDict["obsolete.dead.key"] = "ShouldBePruned";
        CustomWidgetLocaleCatalog localizedCatalog = c1.SeededCatalog.WithLocale("en-US", enDict);

        // 2. Co-creation user prompt includes locales/zh-CN.json so AI sees real copy behind ${$i18n.*}
        string userPrompt = CustomWidgetTemplates.BuildStudioCoCreationUserPrompt(
            userMessage: "给胶囊加个换一道按钮，并删掉旧提示文本",
            currentManifestJson: c1.CompiledManifestJson,
            uiLanguage: "zh-CN",
            currentLocales: localizedCatalog);
        Assert.Contains("locales/zh-CN.json", userPrompt, StringComparison.Ordinal);
        Assert.Contains("辣度上限", userPrompt, StringComparison.Ordinal);

        // 3. Multi-turn edit adds plain-string capsule tooltip ("换一道") + new text ("新增川味小贴士") and deletes "旧提示文本"
        string turnReply = $$"""
        {
          "kind": "edit",
          "reply": "已更新胶囊并精简文案。",
          "changeSummary": "更新胶囊按钮",
          "manifest": {
            "schemaVersion": 1,
            "id": "custom.sichuan",
            "name": "${$i18n.meta.name}",
            "description": "${$i18n.meta.description}",
            "capsule": {
              "title": "${$i18n.meta.name}",
              "primaryAction": { "icon": "\uE72C", "tooltip": "换一道" }
            },
            "view": {
              "type": "Stack",
              "children": [
                { "type": "Button", "text": "换一道" },
                { "type": "Text", "text": "新增川味小贴士" }
              ]
            }
          }
        }
        """;

        StudioTurnResult turnRes = CustomWidgetAiGeneratorService.ParseStudioTurnResponse(
            turnReply,
            currentLocales: localizedCatalog,
            fallbackCulture: "zh-CN");
        Assert.True(turnRes.Success, turnRes.ErrorMessage);
        Assert.DoesNotContain("\"tooltip\": \"换一道\"", turnRes.ManifestJson, StringComparison.Ordinal);
        Assert.Contains("$i18n.", turnRes.ManifestJson, StringComparison.Ordinal);
        Assert.NotNull(turnRes.Locales);

        // Pruned orphaned keys ("obsolete.dead.key" and deleted settings/old text keys)
        Assert.False(turnRes.Locales!.Locales["en-US"].ContainsKey("obsolete.dead.key"));
        Assert.Contains(turnRes.Locales.Locales["zh-CN"].Values, v => v == "新增川味小贴士");

        // SyncPlan immediately detects that en-US is missing the 1 brand-new key ("新增川味小贴士")
        CustomWidgetI18nSyncPlan syncPlan = CustomWidgetI18nEngine.BuildSyncPlan(
            turnRes.ManifestJson,
            existingCatalog: turnRes.Locales,
            fallbackCulture: "zh-CN",
            targetLocales: ["zh-CN", "en-US"]);
        Assert.False(syncPlan.ManifestChanged);
        CustomWidgetLocaleTaskPlan enPlan = syncPlan.LocalePlans.Single(p => p.Culture == "en-US");
        Assert.True(enPlan.NeedsTranslation);
        Assert.Single(enPlan.DeltaKeys);
    }

    [Fact]
    public async Task LazyRuntimeLoader_SessionBlobDeduplication_AndShareZipLocales_WorkEndToEnd()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-locales-storage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            CustomWidgetTemplateBundle worldClock = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.WorldClockTemplateId);
            string localesDir = Path.Combine(tempDir, "locales");
            CustomWidgetLocaleCatalog.SaveToDirectory(localesDir, worldClock.Locales);

            // 1. Lazy runtime loader loads ONLY active culture + zh-CN/en-US fallbacks (3 files instead of 13)
            CustomWidgetLocaleCatalog lazyFr = CustomWidgetLocaleCatalog.LoadLazyForRuntimeCulture(localesDir, "fr-FR");
            Assert.True(lazyFr.Locales.Count <= 3);
            Assert.Contains("fr-FR", lazyFr.Locales.Keys);
            Assert.Contains("en-US", lazyFr.Locales.Keys);
            Assert.DoesNotContain("ru-RU", lazyFr.Locales.Keys);

            // 2. SessionStore deduplicates identical locale dictionaries across revisions via content-addressed localeBlobs
            var sessionStore = new CustomWidgetStudioSessionStore(tempDir);
            var revisions = new List<StudioPersistedRevision>();
            for (int i = 1; i <= 5; i++)
            {
                revisions.Add(new StudioPersistedRevision
                {
                    RevisionId = $"r-{i}",
                    Source = "ai",
                    ChangeSummary = $"Turn {i}",
                    ManifestSha256 = CustomWidgetStudioSessionStore.ComputeManifestSha256(worldClock.ManifestJson, worldClock.LogicJs, worldClock.Locales),
                    IsCommitted = i == 1,
                    ManifestJson = worldClock.ManifestJson,
                    LogicJs = worldClock.LogicJs,
                    Locales = worldClock.Locales
                });
            }

            sessionStore.SaveCommittedWidgetSession("w-dedup", "# Brief", revisions, []);
            string rawRevisionsFile = await File.ReadAllTextAsync(
                Path.Combine(sessionStore.GetWidgetDirectory("w-dedup"), CustomWidgetStudioSessionStore.RevisionsFileName),
                Encoding.UTF8);

            using (JsonDocument revDoc = JsonDocument.Parse(rawRevisionsFile))
            {
                Assert.Equal(2, revDoc.RootElement.GetProperty("schemaVersion").GetInt32());
                JsonElement blobs = revDoc.RootElement.GetProperty("localeBlobs");
                int blobCount = blobs.EnumerateObject().Count();
                Assert.Equal(13, blobCount); // 13 unique locale blobs total across all 5 revisions (not 65!)
            }

            StudioLoadedSession reloaded = sessionStore.LoadOrReconcileWidgetSession(
                "w-dedup",
                worldClock.ManifestJson,
                "External",
                currentDiskLogicJs: worldClock.LogicJs,
                currentDiskLocales: worldClock.Locales);
            Assert.Equal(5, reloaded.Revisions.Count);
            Assert.Equal(13, reloaded.Revisions[4].Locales!.Locales.Count);

            // 3. ExportSharePackageAsync writes locales/<culture>.json + locales/_meta.json into .deskbox-widget ZIP
            string widgetId = "test-zip-locales-" + Guid.NewGuid().ToString("N");
            CustomWidgetDataStore widgetStore = CustomWidgetDataStore.ForWidget(widgetId);
            try
            {
                await widgetStore.WriteWorkspaceBundleAsync(worldClock.ManifestJson, worldClock.LogicJs, locales: worldClock.Locales);
                string pkgPath = Path.Combine(tempDir, "world-clock.deskbox-widget");
                _ = await CustomWidgetPackageService.ExportSharePackageAsync(widgetId, pkgPath);

                using (ZipArchive zip = ZipFile.OpenRead(pkgPath))
                {
                    Assert.NotNull(zip.GetEntry("manifest.json"));
                    Assert.NotNull(zip.GetEntry("locales/_meta.json"));
                    Assert.NotNull(zip.GetEntry("locales/zh-CN.json"));
                    Assert.NotNull(zip.GetEntry("locales/en-US.json"));
                    Assert.NotNull(zip.GetEntry("locales/ja-JP.json"));
                }

                CustomWidgetImportedBundle imported = await CustomWidgetPackageService.ReadPackageBundleAsync(pkgPath);
                Assert.NotNull(imported.Locales);
                Assert.Equal(13, imported.Locales!.Locales.Count);
                Assert.NotNull(imported.Locales.Meta);
            }
            finally
            {
                CustomWidgetDataStore.ReleaseWidgetIfUnused(widgetId);
                if (Directory.Exists(widgetStore.WidgetDirectory))
                {
                    Directory.Delete(widgetStore.WidgetDirectory, recursive: true);
                }
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static string ExtractTargetLocaleFromRequestBody(string requestBody)
    {
        string marker = "Target locale:";
        int idx = requestBody.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            string tail = requestBody[(idx + marker.Length)..].TrimStart();
            int space = tail.IndexOfAny([' ', '(', '\\', '\r', '\n', '"']);
            if (space > 0)
            {
                return tail[..space].Trim();
            }
        }

        return "en-US";
    }

    private static HttpResponseMessage BuildChatJsonResponse(string contentJson)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("choices");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WritePropertyName("message");
            writer.WriteStartObject();
            writer.WriteString("content", contentJson);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubQueueHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
