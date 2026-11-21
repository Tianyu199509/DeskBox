using System.Globalization;
using System.Text;
using DeskBox.Models;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class CustomWidgetCapabilityAndEngineTests
{
    [Fact]
    public async Task TodoHostBridge_SupportsOver50Items_DueIso_3TierPriority_Toggle_Cycle_Tombstone_AndRefresher()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-todo-bridge-{Guid.NewGuid():N}");
        string dataDir = Path.Combine(tempRoot, "data");
        string widgetsRoot = Path.Combine(dataDir, "widgets");
        Directory.CreateDirectory(widgetsRoot);
        DeviceIdentity.DataRootOverride = dataDir;

        var refreshedWidgetIds = new List<string>();
        CustomWidgetHostSnapshotService.RegisterTodoWidgetRefresher(id =>
        {
            refreshedWidgetIds.Add(id);
            return Task.CompletedTask;
        });

        try
        {
            // 1. Add with no configured Todo widgets falls back to "todo-default", parses local dueIso and 3-tier priority.
            bool addedHigh = await CustomWidgetHostSnapshotService.AddTodoFromCustomWidgetAsync(
                settingsService: null,
                title: "Ship capability bridge",
                dueIso: "2026-11-18",
                priority: "high",
                widgetsDataRootOverride: widgetsRoot);
            Assert.True(addedHigh);
            Assert.Contains("todo-default", refreshedWidgetIds);

            bool addedMed = await CustomWidgetHostSnapshotService.AddTodoFromCustomWidgetAsync(
                settingsService: null,
                title: "Review PR telemetry",
                dueIso: "2026-11-19",
                priority: "medium",
                widgetsDataRootOverride: widgetsRoot);
            Assert.True(addedMed);

            // Seed > 60 additional items directly to confirm cap raised from 50 -> 200.
            var store = new TodoWidgetStore(widgetsRoot, "todo-default");
            TodoWidgetData rawData = await store.LoadAsync();
            for (int i = 0; i < 65; i++)
            {
                rawData.Items.Add(new TodoItem
                {
                    Id = $"bulk-{i}",
                    Text = $"Bulk task {i}",
                    SortOrder = 10 + i
                });
            }
            await store.SaveAsync(rawData);

            CustomWidgetTodoSnapshot snap = await CustomWidgetHostSnapshotService.CaptureTodoSnapshotAsync(
                settingsService: null,
                maxItems: 200,
                widgetsDataRootOverride: widgetsRoot);
            Assert.Equal(67, snap.TotalCount);
            Assert.Equal(67, snap.Records.Count);

            CustomWidgetRecord highRec = Assert.Single(snap.Records, r => r.GetField("title") == "Ship capability bridge");
            Assert.Equal("high", highRec.GetField("priority"));
            Assert.Equal("2026-11-18", highRec.GetField("dueIso"));
            Assert.Equal("todo-default", highRec.GetField("widgetId"));

            CustomWidgetRecord medRec = Assert.Single(snap.Records, r => r.GetField("title") == "Review PR telemetry");
            Assert.Equal("medium", medRec.GetField("priority"));
            Assert.Equal("2026-11-19", medRec.GetField("dueIso"));

            // 2. Toggle completion & verify HasDueTime=false persisted cleanly.
            TodoWidgetData loadedAfterAdd = await store.LoadAsync();
            TodoItem highEntity = Assert.Single(loadedAfterAdd.Items, x => x.Id == highRec.Id);
            Assert.False(highEntity.HasDueTime);

            Assert.True(await CustomWidgetHostSnapshotService.ToggleTodoFromCustomWidgetAsync(null, highRec.Id, widgetsRoot));
            CustomWidgetTodoSnapshot afterToggle = await CustomWidgetHostSnapshotService.CaptureTodoSnapshotAsync(null, 200, widgetsRoot);
            Assert.Equal("true", Assert.Single(afterToggle.Records, r => r.Id == highRec.Id).GetField("done"));

            // 3. Cycle priority normal -> medium -> high -> normal on a bulk item.
            Assert.True(await CustomWidgetHostSnapshotService.CycleTodoPriorityFromCustomWidgetAsync(null, "bulk-0", widgetsRoot));
            CustomWidgetTodoSnapshot afterCycle1 = await CustomWidgetHostSnapshotService.CaptureTodoSnapshotAsync(null, 200, widgetsRoot);
            Assert.Equal("medium", Assert.Single(afterCycle1.Records, r => r.Id == "bulk-0").GetField("priority"));

            Assert.True(await CustomWidgetHostSnapshotService.CycleTodoPriorityFromCustomWidgetAsync(null, "bulk-0", widgetsRoot));
            CustomWidgetTodoSnapshot afterCycle2 = await CustomWidgetHostSnapshotService.CaptureTodoSnapshotAsync(null, 200, widgetsRoot);
            Assert.Equal("high", Assert.Single(afterCycle2.Records, r => r.Id == "bulk-0").GetField("priority"));

            // 4. Remove creates tombstone rather than hard deletion, and ClearCompleted honors dueIsoFilter.
            Assert.True(await CustomWidgetHostSnapshotService.RemoveTodoFromCustomWidgetAsync(null, medRec.Id, widgetsRoot));
            TodoWidgetData afterRemoveRaw = await store.LoadAsync();
            TodoItem removedEntity = Assert.Single(afterRemoveRaw.Items, x => x.Id == medRec.Id);
            Assert.True(removedEntity.IsDeleted);

            int clearedOtherDate = await CustomWidgetHostSnapshotService.ClearCompletedTodosFromCustomWidgetAsync(null, "2026-12-01", widgetsRoot);
            Assert.Equal(0, clearedOtherDate);

            int clearedTargetDate = await CustomWidgetHostSnapshotService.ClearCompletedTodosFromCustomWidgetAsync(null, "2026-11-18", widgetsRoot);
            Assert.Equal(1, clearedTargetDate);

            TodoWidgetData finalRaw = await store.LoadAsync();
            Assert.True(Assert.Single(finalRaw.Items, x => x.Id == highRec.Id).IsDeleted);
        }
        finally
        {
            CustomWidgetHostSnapshotService.RegisterTodoWidgetRefresher(null);
            DeviceIdentity.DataRootOverride = null;
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void AudioService_BuildAmbientNoiseWav_PreservesContinuousCrossfadeSeamWithoutZeroNotch()
    {
        foreach (string mode in new[] { "pink", "brown", "rain" })
        {
            byte[] wav = CustomWidgetAudioService.BuildAmbientNoiseWav(mode);
            Assert.True(wav.Length > 44 + 200);
            short firstSample = BitConverter.ToInt16(wav, 44);
            short secondSample = BitConverter.ToInt16(wav, 46);
            short lastSample = BitConverter.ToInt16(wav, wav.Length - 2);
            // Deleting the 128-sample zero notch prevents sample[0] from collapsing to 0 across loops.
            Assert.True(Math.Abs((int)firstSample) + Math.Abs((int)secondSample) + Math.Abs((int)lastSample) > 0);
        }
    }

    [Fact]
    public void RssAndAtomParser_HandlesXhtmlContent_PreservesCodeAngleBrackets_ResolvesRelativeLinks_AndParsesRfc822Tz()
    {
        const string atomFeed = """
            <?xml version="1.0" encoding="utf-8"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <title type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml">Engineering &amp; <b>Rust</b> Blog</div></title>
              <entry>
                <title>Zero-cost &lt;T&gt; &amp; Vec&lt;u8&gt; spans</title>
                <link rel="alternate" href="/posts/42" />
                <author><name>Alice</name><uri>https://example.com/alice</uri><email>alice@example.com</email></author>
                <updated>Mon, 17 Nov 2026 12:30:00 PST</updated>
                <content type="xhtml"><div xmlns="http://www.w3.org/1999/xhtml"><p>Use <code>Span&lt;T&gt;</code> &amp; keep <b>speed</b>.</p></div></content>
              </entry>
            </feed>
            """;

        var (atomState, atomItems) = CustomWidgetNetworkSourceEngine.ParseRssOrAtomXml(
            atomFeed,
            maxItems: 10,
            baseUri: new Uri("https://blog.example.com/atom.xml"));

        Assert.Equal("Engineering & Rust Blog", atomState["feedTitle"].GetString());
        CustomWidgetRecord entry = Assert.Single(atomItems);
        Assert.Equal("Zero-cost <T> & Vec<u8> spans", entry.GetField("title"));
        Assert.Equal("https://blog.example.com/posts/42", entry.GetField("url"));
        Assert.Equal("Alice", entry.GetField("author"));
        Assert.Contains("Span<T>", entry.GetField("summary"), StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(entry.GetField("publishedAt")));

        const string rssFeed = """
            <?xml version="1.0" encoding="utf-8"?>
            <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
              <channel>
                <title>Feed Title</title>
                <item>
                  <title>Encoded Content First</title>
                  <content:encoded><![CDATA[<p>Rich <strong>body</strong> survives.</p>]]></content:encoded>
                  <description />
                  <link>https://example.com/a?x=1&amp;y=2</link>
                </item>
              </channel>
            </rss>
            """;

        var (_, rssItems) = CustomWidgetNetworkSourceEngine.ParseRssOrAtomXml(rssFeed, maxItems: 10);
        CustomWidgetRecord rssItem = Assert.Single(rssItems);
        Assert.Contains("Rich body survives.", rssItem.GetField("summary"), StringComparison.Ordinal);
        Assert.Equal("https://example.com/a?x=1&y=2", rssItem.GetField("url"));
    }

    [Fact]
    public async Task WeatherSource_UnknownExplicitCityReturnsExplicitErrorSnapshot()
    {
        var weatherSource = new CustomWidgetWeatherSource(settingsService: null)
        {
            SearchCityOverride = (_, _, _) => Task.FromResult(new List<WeatherGeocodingItem>())
        };

        CustomWidgetWeatherSnapshot snap = await weatherSource.FetchSnapshotAsync(
            "AtlantisNowhereCity999",
            cultureName: "en-US");

        Assert.False(snap.HasData);
        Assert.Equal("error", snap.Status);
        Assert.Equal("AtlantisNowhereCity999", snap.City);
        Assert.Contains("AtlantisNowhereCity999", snap.ErrorMessage ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void PomodoroAndUtilityEngines_LocalizeDurationsLocalZoneJwtAndHighPrecisionTimestamps()
    {
        var state = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CustomWidgetPomodoroEngine.KeyTodayDate] = "2026-11-17",
            [CustomWidgetPomodoroEngine.KeyTodayMinutes] = "85"
        };
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var now = new DateTimeOffset(new DateTime(2026, 11, 17, 12, 0, 0, DateTimeKind.Local));

        Assert.Equal("1 小时 25 分钟", CustomWidgetPomodoroEngine.ResolveToken("todayDurationText", state, settings, now, "zh-CN"));
        Assert.Equal("1h 25m", CustomWidgetPomodoroEngine.ResolveToken("todayDurationText", state, settings, now, "en-US"));
        Assert.Equal("1時間25分", CustomWidgetPomodoroEngine.ResolveToken("todayDurationText", state, settings, now, "ja-JP"));

        Assert.Equal("UTC", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("utc", "zh-CN"));
        Assert.Equal("本地", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("local", "zh-CN"));
        Assert.Equal("ローカル", CustomWidgetUtilityEngine.ResolveTimeZoneCityName("local", "ja-JP"));

        var sec = CustomWidgetUtilityEngine.ExecuteTextTransform("1700000000", "timestamptodate");
        var us = CustomWidgetUtilityEngine.ExecuteTextTransform("1700000000000000", "timestamptodate");
        var ns = CustomWidgetUtilityEngine.ExecuteTextTransform("1700000000000000000", "timestamptodate");
        Assert.True(sec.Succeeded);
        Assert.Equal(sec.ResultText, us.ResultText);
        Assert.Equal(sec.ResultText, ns.ResultText);

        string header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string payload = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"sub\":\"123\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var jaJwt = CustomWidgetUtilityEngine.ExecuteTextTransform($"{header}.{payload}.sig", "jwtdecode", now, "ja-JP");
        Assert.True(jaJwt.Succeeded);
        Assert.Contains("デコードのみ", jaJwt.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public void CountdownEngine_GatesHolidaysForNonZh_PinsToggle_FixesLunarYear_AddsDeltaText_AndRestsOnWeekend()
    {
        var enState = new Dictionary<string, string>(StringComparer.Ordinal);
        var enCols = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
        var sat = new DateTimeOffset(new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Local));

        Assert.True(CustomWidgetCountdownEngine.EnsureSeeded(enState, enCols, sat, cultureName: "en-US"));
        Assert.Equal(4, enCols[CustomWidgetCountdownEngine.CollectionName].Count);

        CustomWidgetCountdownSnapshot enSnap = CustomWidgetCountdownEngine.ComputeSnapshot(
            enState, new Dictionary<string, string>(), enCols, sat, cultureName: "en-US");
        Assert.Null(enSnap.NextHoliday);
        Assert.NotNull(enSnap.HeroEvent);
        Assert.Equal("New Year's Day", enSnap.HeroEvent!.Title);
        Assert.Equal(CustomWidgetWorkdayPhase.AfterWork, enSnap.Workday.Phase);
        Assert.Equal(100.0, enSnap.Workday.Percent);
        Assert.StartsWith("in ", enSnap.HeroEvent.DeltaText, StringComparison.Ordinal);

        // PinEvent toggles pinned on/off cleanly
        string heroId = enSnap.HeroEvent.Id;
        Assert.True(CustomWidgetCountdownEngine.PinEvent(enCols, heroId));
        Assert.Equal("0", enCols[CustomWidgetCountdownEngine.CollectionName].Single(r => r.Id == heroId).GetField("pinned"));
        Assert.True(CustomWidgetCountdownEngine.PinEvent(enCols, heroId));
        Assert.Equal("1", enCols[CustomWidgetCountdownEngine.CollectionName].Single(r => r.Id == heroId).GetField("pinned"));

        // Lunar anniversary uses lunar year (lunar 2024-01-01 -> 2026-01-01 = Year 2)
        var lunarCols = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
        var lunarNow = new DateTimeOffset(new DateTime(2026, 1, 20, 12, 0, 0, DateTimeKind.Local));
        CustomWidgetCountdownEngine.UpsertEvent(
            lunarCols,
            eventId: null,
            title: "Lunar Birthday",
            emoji: "\uE734",
            dateIso: "2024-01-01",
            calendar: CustomWidgetCountdownEngine.CalendarLunar,
            repeat: CustomWidgetCountdownEngine.RepeatYearly,
            pinned: true,
            isLunarLeap: false,
            now: lunarNow,
            cultureName: "zh-CN");
        CustomWidgetCountdownSnapshot lunarSnap = CustomWidgetCountdownEngine.ComputeSnapshot(
            new Dictionary<string, string>(),
            new Dictionary<string, string>(),
            lunarCols,
            lunarNow,
            cultureName: "zh-CN");
        Assert.Contains("2 周年", lunarSnap.HeroEvent!.HeroSubtitle, StringComparison.Ordinal);
    }

    [Fact]
    public void AiMonitorEngine_CodexMultiDaySession_BucketsIncrementalDeltasPerDay_AndFocusValueRespectsMode()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"deskbox-codex-multiday-{Guid.NewGuid():N}");
        string codexDir = Path.Combine(tempRoot, "sessions");
        Directory.CreateDirectory(codexDir);

        string? prevClaudeEnv = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        string? prevCodexEnv = Environment.GetEnvironmentVariable("CODEX_HOME");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(tempRoot, "custom-claude"));
            Environment.SetEnvironmentVariable("CODEX_HOME", tempRoot);

            Assert.Equal(
                Path.Combine(tempRoot, "custom-claude", "projects"),
                CustomWidgetAiMonitorSource.ResolveClaudeProjectsDirectory(@"C:\Users\Fallback"));
            Assert.Equal(
                Path.Combine(tempRoot, "sessions"),
                CustomWidgetAiMonitorSource.ResolveCodexSessionsDirectory(@"C:\Users\Fallback"));

            string rolloutFile = Path.Combine(codexDir, "rollout-multiday.jsonl");
            var now = new DateTimeOffset(new DateTime(2026, 10, 31, 14, 0, 0, DateTimeKind.Local));
            string day1Ts = new DateTimeOffset(new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Local)).ToString("O", CultureInfo.InvariantCulture);
            string day2Ts = new DateTimeOffset(new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Local)).ToString("O", CultureInfo.InvariantCulture);

            string day1Line = "{\"timestamp\":\"" + day1Ts + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":10000,\"cached_input_tokens\":2000,\"output_tokens\":1000}}}}\n";
            File.WriteAllText(rolloutFile, day1Line, Encoding.UTF8);
            File.SetLastWriteTimeUtc(rolloutFile, now.UtcDateTime.AddMinutes(-5));

            var cache = new Dictionary<string, CustomWidgetAiMonitorEngine.CachedCodexFileSummary>(StringComparer.OrdinalIgnoreCase);
            CustomWidgetAiMonitorEngine.AnalyzeCodexDirectory(codexDir, enabled: true, now: now, fileCache: cache, cultureName: "zh-CN");

            string day2Line = "{\"timestamp\":\"" + day2Ts + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":15000,\"cached_input_tokens\":3000,\"output_tokens\":1500}}}}\n";
            File.AppendAllText(rolloutFile, day2Line, Encoding.UTF8);
            File.SetLastWriteTimeUtc(rolloutFile, now.UtcDateTime);

            var (codexSnap, sessions, codexFiles) = CustomWidgetAiMonitorEngine.AnalyzeCodexDirectory(
                codexDir, enabled: true, now: now, fileCache: cache, cultureName: "zh-CN");
            CustomWidgetAiLocalSummary summary = CustomWidgetAiMonitorEngine.BuildLocalSummary(
                claudeFileSummaries: Array.Empty<CustomWidgetAiMonitorEngine.CachedClaudeFileSummary>(),
                codexSessions: sessions,
                claudeFiles: 0,
                codexFiles: codexFiles,
                now: now);

            // Day 1 had 11,000 tokens; Day 2 had 5,500 incremental delta tokens -> Today must be 5,500, NOT 16,500!
            Assert.Equal(5_500, summary.TodayTokens);
            Assert.Equal(16_500, summary.Last30DaysTokens);

            var full = new CustomWidgetAiMonitorSnapshot(
                AllProviders: [codexSnap with { HasLiveData = true, PrimaryWindow = new CustomWidgetAiWindowQuota("5h", 35, now.AddHours(2), "2h", AiQuotaHealth.Warning) }],
                ActiveProviders: [codexSnap with { HasLiveData = true, PrimaryWindow = new CustomWidgetAiWindowQuota("5h", 35, now.AddHours(2), "2h", AiQuotaHealth.Warning) }],
                LocalSummary: summary,
                UpdatedAt: now);

            var state = new Dictionary<string, string>(StringComparer.Ordinal) { ["selectedProvider"] = "codex" };
            Assert.Equal("65%", CustomWidgetAiMonitorEngine.ResolveToken("focus.value", full, state, new Dictionary<string, string> { ["quotaDisplayMode"] = "remaining" }, "en-US"));
            Assert.Equal("35%", CustomWidgetAiMonitorEngine.ResolveToken("focus.value", full, state, new Dictionary<string, string> { ["quotaDisplayMode"] = "used" }, "en-US"));
            Assert.Equal("true", CustomWidgetAiMonitorEngine.ResolveToken("hasLocalCliData", full, state, new Dictionary<string, string>(), "en-US"));
            Assert.Equal("true", CustomWidgetAiMonitorEngine.ResolveToken("focus.isWindowQuota", full, state, new Dictionary<string, string>(), "en-US"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", prevClaudeEnv);
            Environment.SetEnvironmentVariable("CODEX_HOME", prevCodexEnv);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
