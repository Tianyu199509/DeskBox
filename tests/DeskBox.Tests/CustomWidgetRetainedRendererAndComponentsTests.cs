using System.Buffers.Binary;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Microsoft.UI.Xaml;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetRetainedRendererAndComponentsTests
{
    private static readonly string[] RemovedBespokeCardTypes =
    [
        "GpuCard",
        "ProcessList",
        "PomodoroCard",
        "CountdownHero",
        "CountdownList",
        "TimeProgressCard",
        "AiFocusCard",
        "AiRadarList",
        "AiTokensCard",
        "AiTrendCard"
    ];

    private static readonly string[] GoldenNativeComponentTypes =
    [
        "ZStack",
        "ItemsGrid",
        "SelectorBar",
        "FlipView",
        "SplitView",
        "SplitButton",
        "DropDownButton",
        "InfoBadge",
        "InfoBar",
        "TeachingTip",
        "RichText",
        "CodeBlock",
        "RadialGauge",
        "MediaPlayer"
    ];

    [Fact]
    public void HostManagedChrome_CategoryClassifier_AndRootSafePadding_ResolveCleanly()
    {
        const string defaultCardJson = """
        {
          "schemaVersion": 1,
          "id": "test.standard-card",
          "name": "Standard Card Widget",
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "children": ["query_input", "hello_text"]
            },
            "query_input": { "type": "Input", "bind": "state.query", "placeholder": "Search..." },
            "hello_text": { "type": "Text", "text": "Hello" }
          }
        }
        """;

        string autoFixedStandard = CustomWidgetManifestAutoFixer.AutoWireMissingCapabilities(defaultCardJson);
        CustomWidgetParseResult standardParsed = CustomWidgetManifestParser.Parse(autoFixedStandard);
        Assert.True(standardParsed.IsValid);
        Assert.NotNull(standardParsed.Manifest);
        Assert.False(CustomWidgetChromeCategoryClassifier.IsFullBleedRootLayout(standardParsed.Manifest));
        Assert.Equal(WidgetChromeCategory.Interactive, CustomWidgetChromeCategoryClassifier.Classify(standardParsed.Manifest));
        Assert.True(string.IsNullOrWhiteSpace(standardParsed.Manifest!.View.Padding));

        const string legacyImmersiveJson = """
        {
          "schemaVersion": 1,
          "id": "test.legacy-immersive",
          "name": "Photo Wall",
          "chrome": "immersive",
          "root": "main",
          "elements": {
            "main": {
              "type": "ZStack",
              "padding": "0",
              "children": ["photo_img"]
            },
            "photo_img": { "type": "Image", "src": "${state.currentPhoto}", "stretch": "uniformToFill" }
          }
        }
        """;

        string autoFixedFullBleed = CustomWidgetManifestAutoFixer.AutoWireMissingCapabilities(legacyImmersiveJson);

        CustomWidgetParseResult fullBleedParsed = CustomWidgetManifestParser.Parse(autoFixedFullBleed);
        Assert.True(fullBleedParsed.IsValid, string.Join("; ", fullBleedParsed.Diagnostics.Select(static d => $"{d.Path}: {d.Message}")));
        Assert.NotNull(fullBleedParsed.Manifest);
        Assert.True(CustomWidgetChromeCategoryClassifier.IsFullBleedRootLayout(fullBleedParsed.Manifest));
        Assert.Equal(WidgetChromeCategory.Display, CustomWidgetChromeCategoryClassifier.Classify(fullBleedParsed.Manifest));
        Assert.Equal("0", fullBleedParsed.Manifest!.View.Padding);

        const string arbitraryLegacyChromeJson = """
        {
          "schemaVersion": 1,
          "id": "test.legacy-chrome-ignored",
          "name": "Legacy Chrome Ignored",
          "chrome": "floating",
          "root": "main",
          "elements": {
            "main": { "type": "Stack", "children": ["ok_text"] },
            "ok_text": { "type": "Text", "text": "OK" }
          }
        }
        """;
        CustomWidgetParseResult ignoredChrome = CustomWidgetManifestParser.Parse(arbitraryLegacyChromeJson);
        Assert.True(ignoredChrome.IsValid);
        Assert.DoesNotContain(ignoredChrome.Diagnostics, static d => d.Path == "$.chrome");

        string tempChromeDir = Path.Combine(Path.GetTempPath(), $"deskbox-chrome-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempChromeDir);
        try
        {
            var settingsSvc = new SettingsService(tempChromeDir);
            settingsSvc.Settings.DisplayWidgetChromeMode = "Overlay";
            settingsSvc.Settings.InteractiveWidgetChromeMode = "Standard";
            var resolver = new WidgetChromeModeResolver(settingsSvc);
            WidgetContentDescriptor descriptor = new WidgetContentFactory(new LocalizationService(settingsSvc)).GetDescriptor(WidgetKind.Custom);
            var widget = new WidgetConfig { Id = "custom.photo", WidgetKind = WidgetKind.Custom };
            Assert.Equal(
                WidgetChromeMode.Overlay,
                resolver.Resolve(widget, descriptor, WidgetChromeCategory.Display));
            Assert.Equal(
                WidgetChromeMode.Standard,
                resolver.Resolve(widget, descriptor, WidgetChromeCategory.Interactive));

            WidgetChromeModeNames.SetOverrideMode(widget, WidgetChromeMode.Compact);
            Assert.Equal(
                WidgetChromeMode.Compact,
                resolver.Resolve(widget, descriptor, WidgetChromeCategory.Display));
        }
        finally
        {
            try { Directory.Delete(tempChromeDir, recursive: true); } catch { }
        }

        Thickness fullBleedNullPad = CustomWidgetNativeRenderer.ResolveRootSafePadding(null, isFullBleedRoot: true);
        Thickness fullBleedZeroPad = CustomWidgetNativeRenderer.ResolveRootSafePadding("0", isFullBleedRoot: true);
        Thickness fullBleedCustomPad = CustomWidgetNativeRenderer.ResolveRootSafePadding("6,8", isFullBleedRoot: true);
        Thickness standardNullPad = CustomWidgetNativeRenderer.ResolveRootSafePadding(null, isFullBleedRoot: false);
        Thickness standardZeroPad = CustomWidgetNativeRenderer.ResolveRootSafePadding("0", isFullBleedRoot: false);

        Assert.Equal(new Thickness(0), fullBleedNullPad);
        Assert.Equal(new Thickness(0), fullBleedZeroPad);
        Assert.Equal(new Thickness(6, 8, 6, 8), fullBleedCustomPad);
        Assert.Equal(new Thickness(12, 10, 12, 12), standardNullPad);
        Assert.Equal(new Thickness(0), standardZeroPad);
    }

    [Fact]
    public void GoldenNativeComponents_Canvas2_ContextMenu_AndRemovedBespokeCards_ValidateEndToEnd()
    {
        Assert.Contains("StatCard", CustomWidgetManifestParser.AllSupportedNodeTypes);
        foreach (string golden in GoldenNativeComponentTypes)
        {
            Assert.Contains(golden, CustomWidgetManifestParser.AllSupportedNodeTypes);
        }

        foreach (string removed in RemovedBespokeCardTypes)
        {
            Assert.DoesNotContain(removed, CustomWidgetManifestParser.AllSupportedNodeTypes);
            CustomWidgetParseResult rejected = CustomWidgetManifestParser.Parse($$"""
            {
              "schemaVersion": 1,
              "id": "test.removed.{{removed.ToLowerInvariant()}}",
              "name": "Removed Card Check",
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "children": ["removed_child"]
                },
                "removed_child": { "type": "{{removed}}" }
              }
            }
            """);
            Assert.False(rejected.IsValid);
            Assert.Contains(rejected.Diagnostics, static d => d.IsError && d.Path == "#removed_child");
        }

        const string richManifestJson = """
        {
          "schemaVersion": 1,
          "id": "test.golden-suite",
          "name": "Golden Native Suite",
          "chrome": "immersive",
          "state": {
            "activeTab": "overview",
            "slideIdx": "0",
            "paneOpen": "true",
            "tipOpen": "false",
            "gaugeVal": "68",
            "lastPreset": "balanced"
          },
          "collections": {
            "cards": { "maxItems": 50 }
          },
          "root": "main",
          "elements": {
            "main": {
              "type": "ZStack",
              "padding": "0",
              "radialGradient": {
                "centerX": 0.5,
                "centerY": 0.2,
                "radiusX": 0.8,
                "radiusY": 0.8,
                "stops": [
                  { "offset": 0.0, "color": "#3338BDF8" },
                  { "offset": 1.0, "color": "#00000000" }
                ]
              },
              "contextMenu": [
                {
                  "label": "Play Chime",
                  "icon": "\uE768",
                  "onClick": [{ "type": "audio.cue", "value": "bell", "volume": "0.7" }]
                },
                { "isSeparator": true, "label": "-" },
                {
                  "label": "Toggle Guide",
                  "onClick": [{ "type": "tip.toggle", "popoverId": "guide-tip" }]
                }
              ],
              "children": ["main_split"]
            },
            "main_split": {
              "type": "SplitView",
              "isOpen": "${state.paneOpen}",
              "displayMode": "compactInline",
              "paneWidth": 160,
              "compactPaneWidth": 40,
              "pane": "split_pane",
              "children": ["content_stack"]
            },
            "split_pane": {
              "type": "Stack",
              "padding": "8",
              "children": ["pane_badge"]
            },
            "pane_badge": { "type": "InfoBadge", "value": "3", "tone": "accent" },
            "content_stack": {
              "type": "Stack",
              "padding": "12",
              "spacing": 8,
              "children": [
                "tab_bar",
                "status_bar",
                "latency_text",
                "sample_code",
                "main-gauge",
                "actions_row",
                "cards_grid",
                "cards_flip",
                "ambient_player",
                "gauge_canvas",
                "guide-tip"
              ]
            },
            "tab_bar": {
              "type": "SelectorBar",
              "bind": "state.activeTab",
              "options": [
                { "value": "overview", "label": "Overview", "icon": "\uE80F" },
                { "value": "media", "label": "Media", "icon": "\uE768" }
              ]
            },
            "status_bar": {
              "type": "InfoBar",
              "severity": "success",
              "title": "Ready",
              "text": "Native controls active",
              "isClosable": true
            },
            "latency_text": {
              "type": "RichText",
              "spans": [
                { "text": "Latency ", "color": "secondary" },
                { "text": "12ms", "fontWeight": "bold", "monospace": true, "color": "#10B981" }
              ]
            },
            "sample_code": {
              "type": "CodeBlock",
              "language": "json",
              "text": "{\"ok\":true}",
              "showCopyButton": true
            },
            "main-gauge": {
              "type": "RadialGauge",
              "id": "main-gauge",
              "value": "${state.gaugeVal}",
              "min": 0,
              "max": 100,
              "unit": "%",
              "showTicks": true
            },
            "actions_row": {
              "type": "Stack",
              "orientation": "horizontal",
              "spacing": 8,
              "children": ["preset_btn", "audio_btn"]
            },
            "preset_btn": {
              "type": "SplitButton",
              "text": "Apply Preset",
              "onClick": [{ "type": "tip.open", "popoverId": "guide-tip" }],
              "menuItems": [
                {
                  "label": "Balanced",
                  "onClick": [{ "type": "state.set", "key": "lastPreset", "value": "balanced" }]
                }
              ]
            },
            "audio_btn": {
              "type": "DropDownButton",
              "text": "Ambient Audio",
              "menuItems": [
                {
                  "label": "Start Rain",
                  "onClick": [{ "type": "audio.play", "id": "rain", "url": "noise:rain", "loop": true, "volume": "0.5" }]
                },
                {
                  "label": "Stop All",
                  "onClick": [{ "type": "audio.stopAll" }]
                }
              ]
            },
            "cards_grid": {
              "type": "ItemsGrid",
              "collection": "cards",
              "minItemWidth": 110,
              "spacing": 6,
              "itemTemplate": "grid_card_tpl"
            },
            "grid_card_tpl": {
              "type": "Card",
              "children": ["grid_card_title"]
            },
            "grid_card_title": { "type": "Text", "text": "${item.title}" },
            "cards_flip": {
              "type": "FlipView",
              "collection": "cards",
              "height": 90,
              "showPager": true,
              "itemTemplate": "flip_card_tpl"
            },
            "flip_card_tpl": {
              "type": "Stack",
              "children": ["flip_card_title"]
            },
            "flip_card_title": { "type": "Text", "text": "${item.title}" },
            "ambient_player": {
              "type": "MediaPlayer",
              "src": "noise:pink",
              "height": 48,
              "autoPlay": false,
              "loop": true,
              "muted": true,
              "showTransportControls": true
            },
            "gauge_canvas": {
              "type": "Canvas",
              "height": 120,
              "viewBox": "0,0,200,120",
              "onPointerDown": [{ "type": "state.set", "key": "lastPreset", "value": "down" }],
              "onPointerMove": [{ "type": "state.set", "key": "lastPreset", "value": "move" }],
              "onPointerUp": [{ "type": "state.set", "key": "lastPreset", "value": "up" }],
              "elements": [
                {
                  "type": "arc",
                  "cx": "100",
                  "cy": "60",
                  "r": "44",
                  "startAngle": "-135",
                  "sweepAngle": "210",
                  "stroke": "#38BDF8",
                  "strokeWidth": 8,
                  "strokeCap": "round"
                },
                {
                  "type": "circle",
                  "cx": "100",
                  "cy": "60",
                  "r": "20",
                  "radialGradient": {
                    "centerX": 0.5,
                    "centerY": 0.5,
                    "radiusX": 0.5,
                    "radiusY": 0.5,
                    "stops": [
                      { "offset": 0.0, "color": "#FF38BDF8" },
                      { "offset": 1.0, "color": "#0038BDF8" }
                    ]
                  }
                }
              ]
            },
            "guide-tip": {
              "type": "TeachingTip",
              "id": "guide-tip",
              "target": "main-gauge",
              "title": "Gauge Tip",
              "subtitle": "Realtime telemetry arc",
              "isOpen": "${state.tipOpen}",
              "closeButtonText": "Got it",
              "actionButton": "guide_tip_action"
            },
            "guide_tip_action": {
              "type": "Button",
              "text": "Dismiss",
              "onClick": [{ "type": "tip.close", "popoverId": "guide-tip" }]
            }
          }
        }
        """;

        CustomWidgetParseResult result = CustomWidgetManifestParser.Parse(richManifestJson);
        Assert.True(result.IsValid, string.Join("; ", result.Diagnostics.Select(static d => $"{d.Path}: {d.Message}")));
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Manifest);
        Assert.True(CustomWidgetChromeCategoryClassifier.IsFullBleedRootLayout(result.Manifest));
        Assert.NotNull(result.Manifest!.View.RadialGradient);
        Assert.Equal(3, result.Manifest.View.ContextMenu.Count);
    }

    [Fact]
    public void VirtualTelemetryCollections_ProjectStructuredRecordsAcrossDomains()
    {
        var sysSnapshot = new CustomWidgetSystemMetricsSnapshot
        {
            TopProcessesByMemory =
            [
                new CustomWidgetProcessEntry("Code.exe", 4, 3.2, 1_500_000_000L, "3.2%", "1.4 GB", 12.0, 82.5, "Code"),
                new CustomWidgetProcessEntry("chrome.exe", 8, 9.5, 900_000_000L, "9.5%", "858 MB", 36.0, 50.0, "chrome")
            ],
            TopProcessesByCpu =
            [
                new CustomWidgetProcessEntry("ffmpeg.exe", 1, 64.0, 420_000_000L, "64.0%", "400 MB", 90.0, 24.0, "ffmpeg"),
                new CustomWidgetProcessEntry("Code.exe", 4, 3.2, 1_500_000_000L, "3.2%", "1.4 GB", 12.0, 82.5, "Code")
            ]
        };

        DateTimeOffset fixedNow = new(2026, 11, 11, 15, 30, 0, TimeSpan.FromHours(8));
        var aiProviders = new List<CustomWidgetAiProviderSnapshot>
        {
            new(
                Id: "claude",
                Name: "Claude",
                ShortName: "Claude",
                Monogram: "C",
                BrandHex: "#D97757",
                BillingKind: AiProviderBillingKind.WindowQuota,
                IsLocalCli: true,
                IsConfigured: true,
                HasLiveData: true,
                PlanBadge: "Max",
                Health: AiQuotaHealth.Healthy,
                PrimaryWindow: new CustomWidgetAiWindowQuota("5h", UsedPercent: 32.0, fixedNow.AddHours(2), "2h", AiQuotaHealth.Healthy),
                SecondaryWindow: null,
                CurrencySymbol: "$",
                WalletBalance: 0,
                WalletFormatted: "",
                WalletBreakdown: "",
                WalletRemainingPercent: null,
                RadarValueText: "68%",
                RadarSubtext: "2h left",
                ErrorMessage: null,
                UpdatedAt: fixedNow)
        };
        var aiSnapshot = new CustomWidgetAiMonitorSnapshot(
            AllProviders: aiProviders,
            ActiveProviders: aiProviders,
            LocalSummary: new CustomWidgetAiLocalSummary(
                TodayTokens: 145_000,
                TodayCostUsd: 1.25,
                CacheHitPercent: 78.0,
                Last30DaysTokens: 2_400_000,
                Last30DaysCostUsd: 18.4,
                DetectedClaudeFiles: 3,
                DetectedCodexFiles: 1,
                Last28Days:
                [
                    new CustomWidgetAiDailyBucket(new DateOnly(2026, 11, 10), 80_000, 0.65),
                    new CustomWidgetAiDailyBucket(new DateOnly(2026, 11, 11), 145_000, 1.25)
                ]),
            UpdatedAt: fixedNow);

        var countdownCollections = new Dictionary<string, List<CustomWidgetRecord>>(StringComparer.Ordinal);
        CustomWidgetCountdownEngine.UpsertEvent(
            countdownCollections,
            eventId: "ev-launch",
            title: "v2.0 Launch",
            emoji: "🚀",
            dateIso: "2026-11-20",
            calendar: CustomWidgetCountdownEngine.CalendarSolar,
            repeat: CustomWidgetCountdownEngine.RepeatNone,
            pinned: true,
            isLunarLeap: false,
            now: fixedNow,
            cultureName: "en-US");
        CustomWidgetCountdownEngine.UpsertEvent(
            countdownCollections,
            eventId: "ev-review",
            title: "Design Review",
            emoji: "🎨",
            dateIso: "2026-11-14",
            calendar: CustomWidgetCountdownEngine.CalendarSolar,
            repeat: CustomWidgetCountdownEngine.RepeatNone,
            pinned: false,
            isLunarLeap: false,
            now: fixedNow,
            cultureName: "en-US");

        var memCtx = new CustomWidgetEvaluationContext
        {
            SystemMetrics = sysSnapshot,
            AiMonitor = aiSnapshot,
            Collections = countdownCollections,
            State = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["processSort"] = "memory",
                [CustomWidgetAiMonitorEngine.KeyTrendMetric] = "tokens"
            },
            Settings = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["topProcessCount"] = "2",
                ["quotaDisplay"] = "remaining",
                ["progressScope"] = "workAndYear"
            },
            ClockOverride = fixedNow,
            CultureName = "en-US"
        };

        Assert.True(CustomWidgetExpressionEvaluator.TryResolveVirtualCollection("${$sys.topProcesses}", memCtx, out IReadOnlyList<CustomWidgetRecord> memProcs));
        Assert.Equal(2, memProcs.Count);
        Assert.Equal("1.4 GB", memProcs[0].Fields["valueText"]);
        Assert.Equal("×4", memProcs[0].Fields["subtitle"]);

        CustomWidgetEvaluationContext cpuCtx = memCtx with
        {
            State = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["processSort"] = "cpu",
                [CustomWidgetAiMonitorEngine.KeyTrendMetric] = "cost"
            }
        };
        Assert.True(CustomWidgetExpressionEvaluator.TryResolveVirtualCollection("$sys.topProcesses", cpuCtx, out IReadOnlyList<CustomWidgetRecord> cpuProcs));
        Assert.Equal("64.0%", cpuProcs[0].Fields["valueText"]);

        Assert.True(CustomWidgetExpressionEvaluator.TryResolveVirtualCollection("${$ai.providers}", memCtx, out IReadOnlyList<CustomWidgetRecord> providers));
        CustomWidgetRecord claudeRow = Assert.Single(providers);
        Assert.Equal("claude", claudeRow.Id);
        Assert.Equal("68.0", claudeRow.Fields["percent"]);
        Assert.Equal("true", claudeRow.Fields["isSelected"]);

        Assert.True(CustomWidgetExpressionEvaluator.TryResolveVirtualCollection("${$ai.dailyTrend}", memCtx, out IReadOnlyList<CustomWidgetRecord> tokenBuckets));
        Assert.Equal(2, tokenBuckets.Count);
        Assert.Equal("145", tokenBuckets[1].Fields["value"]);

        Assert.True(CustomWidgetExpressionEvaluator.TryResolveVirtualCollection("$ai.dailyTrend", cpuCtx, out IReadOnlyList<CustomWidgetRecord> costBuckets));
        Assert.Equal("1.250", costBuckets[1].Fields["value"]);

        Assert.True(CustomWidgetExpressionEvaluator.TryResolveVirtualCollection("${$countdown.items}", memCtx, out IReadOnlyList<CustomWidgetRecord> allEvents));
        Assert.Equal(2, allEvents.Count);
        Assert.True(CustomWidgetExpressionEvaluator.TryResolveVirtualCollection("${$countdown.secondaryItems}", memCtx, out IReadOnlyList<CustomWidgetRecord> secondaryEvents));
        Assert.Single(secondaryEvents);

        Assert.True(CustomWidgetExpressionEvaluator.TryResolveVirtualCollection("${$countdown.progressBars}", memCtx, out IReadOnlyList<CustomWidgetRecord> workYearBars));
        Assert.Equal(["work", "year"], workYearBars.Select(static r => r.Id).ToArray());

        CustomWidgetEvaluationContext allFourCtx = memCtx with
        {
            Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["progressScope"] = "allFour" }
        };
        Assert.True(CustomWidgetExpressionEvaluator.TryResolveVirtualCollection("$countdown.progressBars", allFourCtx, out IReadOnlyList<CustomWidgetRecord> allFourBars));
        Assert.Equal(["work", "week", "month", "year"], allFourBars.Select(static r => r.Id).ToArray());

        CustomWidgetEvaluationContext bothCtx = memCtx with
        {
            Settings = new Dictionary<string, string>(StringComparer.Ordinal) { ["progressScope"] = "both" }
        };
        Assert.True(CustomWidgetExpressionEvaluator.TryResolveVirtualCollection("$countdown.progressBars", bothCtx, out IReadOnlyList<CustomWidgetRecord> bothBars));
        Assert.Equal(["month", "year"], bothBars.Select(static r => r.Id).ToArray());
    }

    [Fact]
    public async Task RuntimeViewModel_ExecutesCountdownUpsertTeachingTipPointerGestureAndAudioActions()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "DeskBoxPhase2Vm_" + Guid.NewGuid().ToString("N"));
        try
        {
            string widgetId = "test.runtime.actions";
            string manifestJson = $$"""
            {
              "schemaVersion": 1,
              "id": "{{widgetId}}",
              "name": "Runtime Actions Test",
              "script": { "entry": "logic.js" },
              "state": {
                "draftTitle": "Ship Native Renderer",
                "draftEmoji": "⚡",
                "draftDate": "2026-12-01",
                "draftCalendar": "solar",
                "draftRepeat": "none",
                "draftPinned": "true",
                "pointerSummary": ""
              },
              "collections": {
                "events": { "maxItems": 50 }
              },
              "sources": [
                { "type": "countdown", "intervalMs": 60000 }
              ],
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "children": ["summary_text"]
                },
                "summary_text": { "type": "Text", "text": "${state.pointerSummary}" }
              }
            }
            """;

            const string logicJs = """
            export function onPadGesture(ctx) {
              const ev = ctx.event || {};
              ctx.state.pointerSummary = `${ev.id}:${ev.x},${ev.y}|${ev.normX},${ev.normY}|${ev.isPressed}`;
            }
            """;

            var store = new CustomWidgetDataStore(widgetId, Path.Combine(tempRoot, "widgets"));
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs, locales: null);

            var settingsSvc = new SettingsService(Path.Combine(tempRoot, "settings"));
            var locService = new LocalizationService(settingsSvc);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = widgetId, Name = "Runtime Actions Test", WidgetKind = WidgetKind.Custom },
                locService,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vm.InitializeAsync();

            var tipEvents = new List<(string Mode, string TipId)>();
            vm.TeachingTipCommandRequested += (mode, id) => tipEvents.Add((mode, id));

            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions("""
                [
                  { "type": "tip.open", "popoverId": "intro-tip" },
                  { "type": "tip.toggle", "popoverId": "intro-tip" },
                  { "type": "tip.close", "popoverId": "intro-tip" },
                  { "type": "countdown.upsert" },
                  { "type": "audio.cue", "value": "pop", "volume": "0.6" },
                  { "type": "audio.play", "id": "ambient", "url": "noise:brown", "loop": true, "volume": "0.5" },
                  { "type": "audio.setVolume", "id": "ambient", "value": "0.35" }
                ]
                """));

            Assert.Equal(
                [("open", "intro-tip"), ("toggle", "intro-tip"), ("close", "intro-tip")],
                tipEvents);

            Assert.True(vm.Collections.TryGetValue("events", out List<CustomWidgetRecord>? events));
            Assert.Contains(events!, static r => r.Fields.TryGetValue("title", out string? t) && t == "Ship Native Renderer");
            Assert.Equal(string.Empty, vm.State["draftTitle"]);

            Assert.Equal("pop", vm.AudioService.LastPlayedCue);
            Assert.Equal(1, vm.AudioService.CuePlayCount);
            Assert.True(vm.AudioService.IsTrackPlaying("ambient"));
            Assert.Equal(0.35, vm.AudioService.GetTrackVolume("ambient"), precision: 3);

            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions("""[{ "type": "audio.pause", "id": "ambient" }]"""));
            Assert.False(vm.AudioService.IsTrackPlaying("ambient"));

            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions("""[{ "type": "audio.stopAll" }]"""));
            Assert.Empty(vm.AudioService.ActiveTrackIds);

            CustomWidgetEvaluationContext pointerCtx = vm
                .CreateEvaluationContext()
                .WithPointerEvent("xy-pad", x: 60, y: 30, normX: 0.5, normY: 0.25, isPressed: true);
            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions("""[{ "type": "script.run", "fn": "onPadGesture" }]"""),
                pointerCtx);

            Assert.Equal("xy-pad:60,30|0.5,0.25|true", vm.State["pointerSummary"]);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("tick")]
    [InlineData("pop")]
    [InlineData("soft")]
    [InlineData("alert")]
    [InlineData("bell")]
    public void AudioService_SynthesizesValidRiffPcm16MonoWavForCues(string cueName)
    {
        byte[] wav = CustomWidgetAudioService.BuildSynthesizedCueWav(cueName);
        AssertValidPcm16MonoWav(wav, minSampleCount: 1000);
    }

    [Theory]
    [InlineData("white")]
    [InlineData("pink")]
    [InlineData("brown")]
    [InlineData("rain")]
    public void AudioService_SynthesizesValidRiffPcm16MonoWavForAmbientNoises(string noiseKind)
    {
        byte[] wav = CustomWidgetAudioService.BuildAmbientNoiseWav(noiseKind);
        AssertValidPcm16MonoWav(wav, minSampleCount: 44100 * 2);

        // Verify seamless loop-boundary wrap-around without a forced zero-notch volume dip.
        short firstSample = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(44, 2));
        short lastSample = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(wav.Length - 2, 2));
        Assert.True(
            Math.Abs((int)firstSample - (int)lastSample) < 8000,
            $"Loop boundary discontinuity too large: first={firstSample}, last={lastSample}.");
    }

    private static void AssertValidPcm16MonoWav(byte[] wav, int minSampleCount)
    {
        Assert.NotNull(wav);
        Assert.True(wav.Length >= 44 + (minSampleCount * 2));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));

        short audioFormat = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(20, 2));
        short channels = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22, 2));
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24, 4));
        short bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34, 2));
        int dataBytes = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40, 4));

        Assert.Equal(1, audioFormat);
        Assert.Equal(1, channels);
        Assert.Equal(44100, sampleRate);
        Assert.Equal(16, bitsPerSample);
        Assert.Equal(wav.Length - 44, dataBytes);

        bool hasNonZeroSample = false;
        ReadOnlySpan<byte> pcm = wav.AsSpan(44);
        for (int offset = 0; offset + 1 < pcm.Length; offset += 2)
        {
            if (BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset, 2)) != 0)
            {
                hasNonZeroSample = true;
                break;
            }
        }

        Assert.True(hasNonZeroSample);
    }

    [Fact]
    public async Task SettingsTitleFallback_DynamicMediaOpacityExpressions_LocalFolderSource_AndOverflowLint_ValidateAndAuthorizeCleanly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-media-wall-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string albumDir = Path.Combine(tempDir, "album");
            Directory.CreateDirectory(albumDir);
            string cnPhotoPath = Path.Combine(albumDir, "春日风景 01.jpg");
            string cnVideoPath = Path.Combine(albumDir, "夜景延时.mp4");
            File.WriteAllBytes(cnPhotoPath, [0xFF, 0xD8, 0xFF, 0xD9]);
            File.WriteAllBytes(cnVideoPath, [0x00, 0x00, 0x00, 0x18]);

            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.media-wall",
              "name": "光影墙",
              "origin": "local",
              "defaultSize": { "width": 320, "height": 240 },
              "settings": [
                {
                  "key": "mediaItems",
                  "type": "text",
                  "title": "本地图片/视频列表",
                  "placeholder": "每行一条路径或目录\nC:\\Pictures\\a.jpg|封面"
                }
              ],
              "collections": ["slides"],
              "sources": [
                {
                  "id": "albumDir",
                  "type": "localFolder",
                  "path": "${$settings.mediaItems}",
                  "targetCollection": "slides",
                  "extensions": ["image/*", "video/*"],
                  "maxItems": 50
                }
              ],
              "state": {
                "fadeOpacity": "0.85",
                "isMuted": "true",
                "showControls": "false"
              },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "padding": "0",
                  "children": ["photo_img", "video_player"]
                },
                "photo_img": {
                  "type": "Image",
                  "src": "${state.currentPhoto}",
                  "height": 210,
                  "stretch": "uniformToFill",
                  "opacity": "${state.fadeOpacity}"
                },
                "video_player": {
                  "type": "MediaPlayer",
                  "src": "${state.currentVideo}",
                  "height": 64,
                  "muted": "${state.isMuted}",
                  "showTransport": "${state.showControls}"
                }
              }
            }
            """;

            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
            Assert.True(parsed.IsValid, string.Join("; ", parsed.Diagnostics.Select(static d => $"{d.Path}: {d.Message}")));
            Assert.NotNull(parsed.Manifest);

            // 1. settings "title" fallback + multi-line media setting detection
            CustomWidgetSettingDefinition mediaSetting = Assert.Single(parsed.Manifest!.Settings);
            Assert.Equal("本地图片/视频列表", mediaSetting.Label);
            Assert.True(CustomWidgetOnboardingHelper.IsMultilineTextSetting(mediaSetting));
            Assert.True(CustomWidgetOnboardingHelper.IsMultilineMediaOrPathListSetting(mediaSetting));
            Assert.Equal((false, false), CustomWidgetOnboardingHelper.ClassifyPathSetting(mediaSetting, string.Empty));

            string appended = CustomWidgetOnboardingHelper.AppendLinesToMultilineSetting(
                string.Empty,
                [cnPhotoPath, cnVideoPath, cnPhotoPath]);
            Assert.Equal(2, appended.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);

            // 2. Dynamic expressions on Image / MediaPlayer + root vertical Stack overflow lint warning
            CustomWidgetNodeSpec imgNode = parsed.Manifest.View.Children[0];
            CustomWidgetNodeSpec videoNode = parsed.Manifest.View.Children[1];
            Assert.Equal("${state.fadeOpacity}", imgNode.OpacityExpression);
            Assert.Equal("${state.isMuted}", videoNode.MutedExpression);
            Assert.Equal("${state.showControls}", videoNode.ShowTransportExpression);
            Assert.Contains(parsed.Diagnostics, static d => !d.IsError && d.Path == "#main");

            // 3. localFolder source parses cleanly
            CustomWidgetSourceSpec folderSource = Assert.Single(parsed.Manifest.Sources);
            Assert.Equal(CustomWidgetSourceTypes.LocalFolder, folderSource.Type);
            Assert.Equal("slides", folderSource.TargetCollection);
            Assert.Equal(2, folderSource.Extensions.Count);

            // 4. UTF-8 percent-encoded file:/// URI authorization + folder expansion + localFolder population
            string widgetId = $"test-media-wall-{Guid.NewGuid():N}";
            CustomWidgetTrustStore.Shared.RegisterWidgetProvenance(widgetId, trustedLocalAuthoring: true);
            var store = new CustomWidgetDataStore(widgetId, Path.Combine(tempDir, "widgets"));
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs: null, locales: null);

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            var locService = new LocalizationService(settingsSvc);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = widgetId, Name = "光影墙", WidgetKind = WidgetKind.Custom },
                locService,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            try
            {
                await vm.InitializeAsync();
                await vm.SetSettingValueAsync("mediaItems", albumDir);

                string fileUri = new Uri(cnPhotoPath).AbsoluteUri;
                Assert.Contains('%', fileUri);
                Assert.Equal(Path.GetFullPath(cnPhotoPath), vm.ResolveAuthorizedImagePath(fileUri));
                Assert.Empty(vm.ResolveAuthorizedImagePath("%SYSTEMROOT%\\System32\\cmd.exe"));

                IReadOnlyDictionary<string, string> expandedSettings = vm.BuildEffectiveScriptSettings();
                Assert.Contains(cnPhotoPath, expandedSettings["mediaItems"], StringComparison.OrdinalIgnoreCase);
                Assert.Contains(cnVideoPath, expandedSettings["mediaItems"], StringComparison.OrdinalIgnoreCase);

                Assert.True(vm.Collections.TryGetValue("slides", out List<CustomWidgetRecord>? slides));
                Assert.Equal(2, slides!.Count);
            }
            finally
            {
                CustomWidgetTrustStore.Shared.RemoveWidget(widgetId);
            }
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void StudioAuthoringAi_HonorsExplicitDeepSeekFlashThinking_AndCoercesAutoAfterExhaustedRoutes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-ai-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-test",
                CustomEndpoint: "https://relay.example/v1/chat/completions",
                CustomModel: "deepseek-flash",
                ThinkingMode: "enabled"));

            var aiService = new DeskBoxAiService(credStore);
            ResolvedAiInvocation studioFlash = aiService.ResolveEffectiveSettingsForProfile(
                "deepseek",
                AiTaskProfile.StudioAuthoring,
                out _);
            Assert.Equal(AiThinkingMode.Enabled, studioFlash.ThinkingMode);

            var customRoute = new ResolvedAiInvocation(
                CustomWidgetAiGeneratorService.FindProviderPreset(CustomWidgetAiGeneratorService.CustomProviderId),
                "https://relay.example/v1/chat/completions",
                "glm-4.5-air",
                "sk-test",
                AiThinkingMode.Auto,
                UsesCustomEndpoint: true);
            aiService.MarkThinkingExhaustedRoute(customRoute);

            ResolvedAiInvocation coercedAfterExhaustion = aiService.CoerceSafeStudioAuthoringInvocation(customRoute);
            Assert.Equal(AiThinkingMode.Disabled, coercedAfterExhaustion.ThinkingMode);

            ResolvedAiInvocation explicitEnabledOverride = aiService.CoerceSafeStudioAuthoringInvocation(
                customRoute with { ThinkingMode = AiThinkingMode.Enabled });
            Assert.Equal(AiThinkingMode.Enabled, explicitEnabledOverride.ThinkingMode);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task HostAppearanceProfile_DensityScaling_ExplicitSpacing_AndCornerRadii_ResolveCleanly()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "test.density-and-corners",
          "name": "Density Test",
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "children": ["explicitZero", "explicitTen", "implicitDefault"]
            },
            "explicitZero": { "type": "Stack", "id": "explicitZero", "spacing": 0, "children": [] },
            "explicitTen": { "type": "Stack", "id": "explicitTen", "spacing": 10, "children": [] },
            "implicitDefault": { "type": "Stack", "id": "implicitDefault", "children": [] }
          }
        }
        """;

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(manifestJson);
        Assert.True(parsed.IsValid);
        Assert.NotNull(parsed.Manifest);
        Assert.False(parsed.Manifest!.View.HasExplicitSpacing);
        Assert.True(parsed.Manifest.View.Children[0].HasExplicitSpacing);
        Assert.Equal(0, parsed.Manifest.View.Children[0].Spacing);
        Assert.True(parsed.Manifest.View.Children[1].HasExplicitSpacing);
        Assert.Equal(10, parsed.Manifest.View.Children[1].Spacing);
        Assert.False(parsed.Manifest.View.Children[2].HasExplicitSpacing);
        Assert.Equal(8, parsed.Manifest.View.Children[2].Spacing);

        // Standard preset (0.56 / 0.40 / 0.60) keeps exact 1.0 multipliers and 4/6/8 corner radii
        var standardSettings = new AppSettings();
        SettingsService.ApplyLayoutDensityPreset(standardSettings, SettingsService.LayoutDensityStandard);
        standardSettings.WidgetShell.WidgetCornerPreference = SettingsService.WidgetCornerPreferenceRound;
        var standardProfile = CustomWidgetHostAppearanceProfile.FromSettings(standardSettings);
        Assert.Equal(1.0, standardProfile.DensityMultiplier, 3);
        Assert.Equal(1.0, standardProfile.HorizontalSpacingMultiplier, 3);
        Assert.Equal(1.0, standardProfile.VerticalSpacingMultiplier, 3);
        Assert.Equal((4.0, 6.0, 8.0), standardProfile.ResolveCornerRadii());
        Assert.Equal(new Thickness(12, 10, 12, 12), CustomWidgetNativeRenderer.ResolveRootSafePadding(null, false, standardProfile.DensityMultiplier));

        // Compact + Square preset shrinks default spacing/padding and zeroes inner radii while preserving explicit spacing
        var compactSettings = new AppSettings();
        SettingsService.ApplyLayoutDensityPreset(compactSettings, SettingsService.LayoutDensityCompact);
        compactSettings.WidgetShell.WidgetCornerPreference = SettingsService.WidgetCornerPreferenceSquare;
        var compactProfile = CustomWidgetHostAppearanceProfile.FromSettings(compactSettings);
        Assert.True(compactProfile.DensityMultiplier < 1.0);
        Assert.Equal((0.0, 0.0, 0.0), compactProfile.ResolveCornerRadii());
        Thickness compactRootPad = CustomWidgetNativeRenderer.ResolveRootSafePadding(null, false, compactProfile.DensityMultiplier);
        Assert.True(compactRootPad.Left < 12 && compactRootPad.Top < 10);
        Assert.Equal(new Thickness(16, 8, 16, 8), CustomWidgetNativeRenderer.ResolveRootSafePadding("16,8", false, compactProfile.DensityMultiplier));

        // Relaxed + Large preset expands default spacing/padding and maps inner radii to 5/8/10
        var relaxedSettings = new AppSettings();
        SettingsService.ApplyLayoutDensityPreset(relaxedSettings, SettingsService.LayoutDensityRelaxed);
        relaxedSettings.WidgetShell.WidgetCornerPreference = SettingsService.WidgetCornerPreferenceLarge;
        var relaxedProfile = CustomWidgetHostAppearanceProfile.FromSettings(relaxedSettings);
        Assert.True(relaxedProfile.DensityMultiplier > 1.0);
        Assert.Equal((5.0, 8.0, 10.0), relaxedProfile.ResolveCornerRadii());

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-density-renderer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new CustomWidgetDataStore("test-density", Path.Combine(tempDir, "widgets"));
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs: null, locales: null);
            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            var locSvc = new LocalizationService(settingsSvc);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "test-density", Name = "Density", WidgetKind = WidgetKind.Custom },
                locSvc,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vm.InitializeAsync();

            CustomWidgetHostAppearanceProfile activeProfile = compactProfile;
            var renderer = new CustomWidgetNativeRenderer(vm, appearanceProvider: () => activeProfile);

            // Explicit spacing=0 and spacing=10 remain exact under both Compact and Relaxed density
            Assert.Equal(0.0, renderer.ResolveEffectiveSpacing(parsed.Manifest.View.Children[0]));
            Assert.Equal(10.0, renderer.ResolveEffectiveSpacing(parsed.Manifest.View.Children[1]));
            double compactImplicitSpacing = renderer.ResolveEffectiveSpacing(parsed.Manifest.View.Children[2]);
            Assert.True(compactImplicitSpacing < 8.0);

            activeProfile = relaxedProfile;
            Assert.Equal(0.0, renderer.ResolveEffectiveSpacing(parsed.Manifest.View.Children[0]));
            Assert.Equal(10.0, renderer.ResolveEffectiveSpacing(parsed.Manifest.View.Children[1]));
            double relaxedImplicitSpacing = renderer.ResolveEffectiveSpacing(parsed.Manifest.View.Children[2]);
            Assert.True(relaxedImplicitSpacing > 8.0);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void UnifiedSettingsFormBuilder_DetectsNumericParameters_AndMapsSemanticIcons()
    {
        var explicitNum = new CustomWidgetSettingDefinition("foo", "number", "Foo", "15", []);
        Assert.True(CustomWidgetSettingsFormBuilder.IsNumericParameterDefinition(explicitNum, out double explicitDefault));
        Assert.Equal(15.0, explicitDefault);

        var inferredInterval = new CustomWidgetSettingDefinition("refreshIntervalSec", "text", "Refresh", "30", []);
        Assert.True(CustomWidgetSettingsFormBuilder.IsNumericParameterDefinition(inferredInterval, out double inferredDefault));
        Assert.Equal(30.0, inferredDefault);
        Assert.Equal("\uE823", CustomWidgetSettingsFormBuilder.ResolveSettingIconGlyph(inferredInterval));

        var nonNumericText = new CustomWidgetSettingDefinition("apiUrl", "text", "Endpoint", "https://example.com", []);
        Assert.False(CustomWidgetSettingsFormBuilder.IsNumericParameterDefinition(nonNumericText, out _));

        var secretDef = new CustomWidgetSettingDefinition("apiKey", "secret", "API Key", "", []);
        Assert.Equal("\uE8D7", CustomWidgetSettingsFormBuilder.ResolveSettingIconGlyph(secretDef));

        var folderDef = new CustomWidgetSettingDefinition("watchFolder", "folder", "Folder", "", []);
        Assert.Equal("\uE8B7", CustomWidgetSettingsFormBuilder.ResolveSettingIconGlyph(folderDef));
    }

    [Fact]
    public async Task CrossWidgetStructuredDrop_HandlesTodoQuickCaptureAndText_InScriptAndDeclarativeActions()
    {
        CustomWidgetDropAcceptHelper.ParseAcceptModes("any", out bool anyFiles, out bool anyText, out bool anyItems, out _);
        Assert.True(anyFiles && anyText && anyItems);

        CustomWidgetDropAcceptHelper.ParseAcceptModes(".png,.mp4", out bool extFiles, out bool extText, out bool extItems, out HashSet<string>? exts);
        Assert.True(extFiles && !extText && !extItems);
        Assert.True(CustomWidgetDropAcceptHelper.MatchesFileExtensionFilter(@"C:\clip\demo.MP4", exts));
        Assert.False(CustomWidgetDropAcceptHelper.MatchesFileExtensionFilter(@"C:\clip\readme.txt", exts));

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-structured-drop-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string scriptManifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.drop-inbox",
              "name": "跨格子收件箱",
              "script": { "entry": "logic.js", "tickIntervalMs": 0 },
              "state": { "lastKind": "", "lastSummary": "" },
              "collections": { "inbox": { "maxItems": 50 } },
              "onDrop": {
                "accept": "any",
                "actions": [
                  { "type": "script.run", "fn": "onDropReceived" }
                ]
              },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "children": ["drop_zone"]
                },
                "drop_zone": { "type": "DropZone", "accept": "any", "title": "拖入待办或随记" }
              }
            }
            """;

            const string scriptLogicJs = """
            export function onDropReceived(ctx) {
              ctx.state.lastKind = ctx.event.kind || '';
              const items = Array.isArray(ctx.event.items) ? ctx.event.items : [];
              ctx.state.lastSummary = `${ctx.event.kind}:${items.length}:${ctx.event.text}`;
              for (const item of items) {
                ctx.collections.append('inbox', {
                  id: item.id,
                  title: item.title,
                  text: item.text,
                  source: item.source
                });
              }
            }
            """;

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            var locSvc = new LocalizationService(settingsSvc);

            var scriptStore = new CustomWidgetDataStore("w-drop-script", Path.Combine(tempDir, "widgets"));
            await scriptStore.WriteWorkspaceBundleAsync(scriptManifestJson, scriptLogicJs, locales: null);
            using var scriptVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "w-drop-script", Name = "收件箱", WidgetKind = WidgetKind.Custom },
                locSvc,
                scriptStore,
                dispatcherQueue: null);
            await scriptVm.InitializeAsync();
            Assert.True(scriptVm.AcceptsDrops);
            Assert.True(scriptVm.AcceptsFileDrops);

            var todoDrop = new CustomWidgetStructuredDropPayload(
                Kind: "todo",
                Text: "整理周报\n修复构建脚本",
                Items:
                [
                    new CustomWidgetDropItemRecord("t-1", "整理周报", "整理周报", Body: "周五前提交", IsCompleted: false, Source: "todo"),
                    new CustomWidgetDropItemRecord("t-2", "修复构建脚本", "修复构建脚本", Body: null, IsCompleted: true, Source: "todo")
                ]);
            Assert.True(await scriptVm.HandleStructuredDropAsync(todoDrop));
            Assert.Equal("todo", scriptVm.State["lastKind"]);
            Assert.Equal(2, scriptVm.Collections["inbox"].Count);
            Assert.Equal("整理周报", scriptVm.Collections["inbox"][0].GetField("title"));
            Assert.Equal("todo", scriptVm.Collections["inbox"][1].GetField("source"));

            // Declarative multi-item drop without logic.js
            const string declarativeManifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.drop-decl",
              "name": "声明式收集器",
              "collections": { "captured": { "maxItems": 50 } },
              "onDrop": {
                "accept": "items,text",
                "actions": [
                  {
                    "type": "collection.append",
                    "collection": "captured",
                    "item": {
                      "title": "${$event.title}",
                      "text": "${$event.text}",
                      "kind": "${$event.kind}"
                    }
                  }
                ]
              },
              "root": "drop_zone",
              "elements": {
                "drop_zone": { "type": "DropZone", "accept": "items,text", "title": "Drop" }
              }
            }
            """;

            var declStore = new CustomWidgetDataStore("w-drop-decl", Path.Combine(tempDir, "widgets"));
            await declStore.WriteWorkspaceBundleAsync(declarativeManifestJson, logicJs: null, locales: null);
            using var declVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "w-drop-decl", Name = "收集器", WidgetKind = WidgetKind.Custom },
                locSvc,
                declStore,
                dispatcherQueue: null);
            await declVm.InitializeAsync();
            Assert.True(declVm.AcceptsDrops);
            Assert.False(declVm.AcceptsFileDrops);

            var qcDrop = new CustomWidgetStructuredDropPayload(
                Kind: "quickCapture",
                Text: "灵感片段",
                Items:
                [
                    new CustomWidgetDropItemRecord("qc-1", "设计灵感", "Fluent 玻璃阴影参数记录", Body: "Fluent 玻璃阴影参数记录", Source: "quickCapture")
                ]);
            Assert.True(await declVm.HandleStructuredDropAsync(qcDrop));
            Assert.Single(declVm.Collections["captured"]);
            Assert.Equal("设计灵感", declVm.Collections["captured"][0].GetField("title"));
            Assert.Equal("quickCapture", declVm.Collections["captured"][0].GetField("kind"));

            var textDrop = new CustomWidgetStructuredDropPayload(
                Kind: "text",
                Text: "https://deskbox.fun/docs",
                Items:
                [
                    new CustomWidgetDropItemRecord("txt-1", "https://deskbox.fun/docs", "https://deskbox.fun/docs", Url: "https://deskbox.fun/docs", Source: "url")
                ]);
            Assert.True(await declVm.HandleStructuredDropAsync(textDrop));
            Assert.Equal(2, declVm.Collections["captured"].Count);
            Assert.Equal("text", declVm.Collections["captured"][1].GetField("kind"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ScriptTickLifecycle_SuspendsWhenHiddenOrStaticCapsule_StaysAliveForActiveJobs_AndCatchesUpOnResume()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-tick-lifecycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string staticCapsuleManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.tick-static-cap",
              "name": "静态胶囊定时组件",
              "script": { "entry": "logic.js", "tickIntervalMs": 1500 },
              "state": { "ticks": "0" },
              "capsule": {
                "title": "静态标题",
                "summary": "固定摘要"
              },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Text",
                  "text": "Ticks: ${state.ticks}"
                }
              }
            }
            """;

            const string tickJs = """
            export function onTick(ctx) {
              const next = Number(ctx.state.ticks || '0') + 1;
              ctx.state.ticks = String(next);
            }
            """;

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            var locSvc = new LocalizationService(settingsSvc);

            var staticStore = new CustomWidgetDataStore("w-tick-static", Path.Combine(tempDir, "widgets"));
            await staticStore.WriteWorkspaceBundleAsync(staticCapsuleManifest, tickJs, locales: null);
            using var staticVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "w-tick-static", Name = "TickStatic", WidgetKind = WidgetKind.Custom },
                locSvc,
                staticStore,
                dispatcherQueue: null);
            await staticVm.InitializeAsync();

            Assert.Equal(1500, staticVm.ResolveEffectiveScriptTickIntervalMs());
            Assert.Equal(0, staticVm.ScriptTickExecutionCount);

            // Hidden without background work -> suspended (0ms), then catch up immediately upon becoming visible
            staticVm.OnWindowVisibilityChanged(false);
            Assert.Equal(0, staticVm.ResolveEffectiveScriptTickIntervalMs());

            staticVm.OnWindowVisibilityChanged(true);
            Assert.Equal(1500, staticVm.ResolveEffectiveScriptTickIntervalMs());
            Assert.Equal(1, staticVm.ScriptTickExecutionCount);
            Assert.Equal("1", staticVm.State["ticks"]);

            // Collapsed to static capsule without background work -> suspended (0ms)
            staticVm.OnCompactStateChanged(true);
            Assert.Equal(0, staticVm.ResolveEffectiveScriptTickIntervalMs());

            // Starting an active background job keeps onTick alive while collapsed, and releasing it suspends again
            using (CustomWidgetJobBus.ActiveJobHandle job = staticVm.JobBus.BeginJob("compress", "job-1"))
            {
                Assert.True(staticVm.HasActiveBackgroundWork);
                Assert.Equal(1500, staticVm.ResolveEffectiveScriptTickIntervalMs());
            }
            Assert.False(staticVm.HasActiveBackgroundWork);
            Assert.Equal(0, staticVm.ResolveEffectiveScriptTickIntervalMs());

            // Expanding from static capsule runs immediate catch-up tick
            long beforeExpandTicks = staticVm.ScriptTickExecutionCount;
            staticVm.OnCompactStateChanged(false);
            Assert.Equal(beforeExpandTicks + 1, staticVm.ScriptTickExecutionCount);

            // Dynamic capsule referencing $computed.* stays active while collapsed
            const string dynamicCapsuleManifest = """
            {
              "schemaVersion": 1,
              "id": "custom.tick-dyn-cap",
              "name": "动态胶囊定时组件",
              "script": { "entry": "logic.js", "tickIntervalMs": 1000 },
              "state": { "ticks": "0" },
              "capsule": {
                "title": "运行中",
                "badge": "${state.ticks}"
              },
              "root": "main",
              "elements": {
                "main": { "type": "Text", "text": "${state.ticks}" }
              }
            }
            """;

            var dynStore = new CustomWidgetDataStore("w-tick-dyn", Path.Combine(tempDir, "widgets"));
            await dynStore.WriteWorkspaceBundleAsync(dynamicCapsuleManifest, tickJs, locales: null);
            using var dynVm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "w-tick-dyn", Name = "TickDyn", WidgetKind = WidgetKind.Custom },
                locSvc,
                dynStore,
                dispatcherQueue: null);
            await dynVm.InitializeAsync();
            dynVm.OnCompactStateChanged(true);
            Assert.Equal(1000, dynVm.ResolveEffectiveScriptTickIntervalMs());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task SearchEngineService_IndexesCustomWidgetsAndCollectionRecords_AndInvalidatesOnStoreChange()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-search-custom-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            settingsSvc.Settings.Search.SearchIncludeDeskBoxContent = true;
            string widgetsRoot = Path.Combine(tempDir, "data");

            settingsSvc.Settings.WidgetLayout.Widgets.Add(new WidgetConfig
            {
                Id = "cw-reading-list",
                Name = "灵感书签库",
                WidgetKind = WidgetKind.Custom,
                IsDisabled = false
            });

            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.reading-vault",
              "name": "灵感书签库",
              "description": "收藏架构设计与 WinUI 性能优化文章",
              "icon": "\uE734",
              "collections": { "bookmarks": { "maxItems": 100 } },
              "root": "main",
              "elements": {
                "main": { "type": "Text", "text": "Bookmarks" }
              }
            }
            """;

            var customStore = new CustomWidgetDataStore("cw-reading-list", rootDataDirectory: widgetsRoot);
            await customStore.WriteWorkspaceBundleAsync(manifestJson, logicJs: null, locales: null);
            var initialPayload = new CustomWidgetStorePayload();
            initialPayload.Collections["bookmarks"] =
            [
                new CustomWidgetRecord
                {
                    Id = "bm-1",
                    Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["title"] = "DirectComposition AlphaMask 深度指南",
                        ["notes"] = "文字阴影与毛玻璃可读性",
                        ["url"] = "https://deskbox.fun/alphamask"
                    }
                }
            ];
            await customStore.SaveAsync(initialPayload);

            var locSvc = new LocalizationService(settingsSvc);
            var everything = new EverythingSearchService(settingsSvc);
            var qcStore = new QuickCaptureStore(Path.Combine(tempDir, "qc"));
            await qcStore.SaveAsync(new QuickCaptureStoreData());
            var qcSvc = new QuickCaptureService(qcStore);

            using var engine = new SearchEngineService(
                settingsSvc,
                locSvc,
                everything,
                qcSvc,
                customWidgetsRootDataDirectory: widgetsRoot);

            SearchResponse widgetHitResp = await engine.SearchAsync("灵感书签库");
            SearchResultItem widgetHit = Assert.Single(
                widgetHitResp.RankedItems,
                i => i.Kind == SearchResultKind.CustomWidget && i.CustomWidgetItemId is null);
            Assert.Equal("cw-reading-list", widgetHit.CustomWidgetId);
            Assert.Equal("\uE734", widgetHit.Glyph);

            SearchResponse itemHitResp = await engine.SearchAsync("AlphaMask");
            SearchResultItem itemHit = Assert.Single(
                itemHitResp.RankedItems,
                i => i.Kind == SearchResultKind.CustomWidget && i.CustomWidgetItemId == "bm-1");
            Assert.Equal("cw-reading-list", itemHit.CustomWidgetId);
            Assert.Contains("DirectComposition AlphaMask", itemHit.Title, StringComparison.Ordinal);

            // Live store mutation invalidates DeskBox content snapshot via CustomWidgetDataStore.StoreChanged
            initialPayload.Collections["bookmarks"].Add(new CustomWidgetRecord
            {
                Id = "bm-2",
                Fields = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["title"] = "QuickJS 字节码沙箱零拷贝通信",
                    ["summary"] = "跨组件拖拽事件总线"
                }
            });
            await customStore.SaveAsync(initialPayload);

            SearchResponse updatedResp = await engine.SearchAsync("QuickJS 字节码沙箱");
            Assert.Contains(
                updatedResp.RankedItems,
                i => i.Kind == SearchResultKind.CustomWidget &&
                     i.CustomWidgetId == "cw-reading-list" &&
                     i.CustomWidgetItemId == "bm-2");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Renderer_InterpolatesDynamicColumnsOnGridAndItemsGrid_AndReconcilesOnComputedChange()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-dyn-grid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.dynamic-timetable",
              "name": "Dynamic Timetable",
              "script": { "entry": "logic.js" },
              "state": { "showWeekend": false },
              "collections": {
                "slots": [
                  { "id": "s1", "fields": { "title": "Math" } },
                  { "id": "s2", "fields": { "title": "Physics" } }
                ]
              },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "spacing": 6,
                  "children": ["schedule-grid", "day-cells"]
                },
                "schedule-grid": {
                  "id": "schedule-grid",
                  "type": "Grid",
                  "columns": "${$computed.dayCols}",
                  "rows": "${$computed.periodRows}",
                  "children": ["c0", "c1", "c6"]
                },
                "c0": { "id": "c0", "type": "Text", "text": "P1", "row": 0, "column": 0 },
                "c1": { "id": "c1", "type": "Text", "text": "Mon", "row": 0, "column": 1 },
                "c6": { "id": "c6", "type": "Text", "text": "Sat", "row": 1, "column": 6 },
                "day-cells": {
                  "id": "day-cells",
                  "type": "ItemsGrid",
                  "collection": "slots",
                  "columnsCount": "${$computed.itemsCols}",
                  "itemTemplate": "slot_tpl"
                },
                "slot_tpl": { "type": "Text", "text": "${$item.title}" }
              }
            }
            """;

            const string logicJs = """
            export function onInit(ctx) {
              const showWknd = !!ctx.state.showWeekend;
              ctx.computed.dayCols = showWknd ? "Auto,*,*,*,*,*,*,*" : "Auto,*,*,*,*,*";
              ctx.computed.periodRows = "Auto,*,*";
              ctx.computed.itemsCols = showWknd ? "7" : "5";
            }
            export function toggleWeekend(ctx) {
              ctx.state.showWeekend = !ctx.state.showWeekend;
              onInit(ctx);
            }
            """;

            var store = new CustomWidgetDataStore("custom.dynamic-timetable", Path.Combine(tempDir, "widgets"));
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs, locales: null);

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            var locSvc = new LocalizationService(settingsSvc);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "custom.dynamic-timetable", Name = "Dynamic Timetable", WidgetKind = WidgetKind.Custom },
                locSvc,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vm.InitializeAsync();

            var defaultProfile = CustomWidgetHostAppearanceProfile.FromSettings(new AppSettings());
            var renderer = new CustomWidgetNativeRenderer(vm, appearanceProvider: () => defaultProfile);

            CustomWidgetParseResult parse = CustomWidgetManifestParser.Parse(manifestJson);
            Assert.NotNull(parse.Manifest?.View);
            CustomWidgetNodeSpec gridSpec = parse.Manifest!.View!.Children[0];
            CustomWidgetNodeSpec itemsGridSpec = parse.Manifest.View.Children[1];

            int observedCols = 0;
            int observedRows = 0;
            int observedLastChildCol = -1;
            int observedItemsCols = 0;
            bool observedHasStarRow = false;

            renderer.RegisterRetainedPatcherForTest(evalCtx =>
            {
                string colsText = CustomWidgetExpressionEvaluator.Interpolate(gridSpec.Columns!, evalCtx).Trim();
                string rowsText = CustomWidgetExpressionEvaluator.Interpolate(gridSpec.Rows!, evalCtx).Trim();
                var plan = CustomWidgetNativeRenderer.ComputeGridTrackAndPlacementPlan(gridSpec, colsText, rowsText);
                observedCols = plan.Columns.Count;
                observedRows = plan.Rows.Count;
                observedLastChildCol = plan.Placements[^1].Column;
                observedHasStarRow = plan.HasStarRow;
                observedItemsCols = CustomWidgetNativeRenderer.ResolveItemsGridExplicitColumns(itemsGridSpec.Columns, evalCtx);
            });

            Assert.True(renderer.TryPatchRetainedTree());
            Assert.Equal(6, observedCols);
            Assert.Equal(3, observedRows);
            Assert.True(observedHasStarRow);
            Assert.Equal(5, observedLastChildCol); // column=6 clamps into 0..5 when 6 tracks active
            Assert.Equal(5, observedItemsCols);

            // White-space separated tracks are also supported alongside comma-separated tracks
            var spacePlan = CustomWidgetNativeRenderer.ComputeGridTrackAndPlacementPlan(
                gridSpec,
                "Auto * * *",
                "Auto *");
            Assert.Equal(4, spacePlan.Columns.Count);
            Assert.Equal(2, spacePlan.Rows.Count);

            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions("""[{ "type": "script.run", "fn": "toggleWeekend" }]"""));

            Assert.True(renderer.TryPatchRetainedTree());
            Assert.Equal(8, observedCols);
            Assert.Equal(6, observedLastChildCol); // now column=6 is un-clamped cleanly
            Assert.Equal(7, observedItemsCols);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Validator_TracksComputedInGridAndItemsGridColumns_AndFlagsInvalidDryRunShapes()
    {
        const string validManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.valid-dyn-grid",
          "name": "Valid Dyn Grid",
          "script": { "entry": "logic.js" },
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "children": ["g", "ig"]
            },
            "g": {
              "id": "g",
              "type": "Grid",
              "columns": "${$computed.dayCols}",
              "rows": "${$computed.periodRows}",
              "children": ["lbl"]
            },
            "lbl": { "id": "lbl", "type": "Text", "text": "Header" },
            "ig": {
              "id": "ig",
              "type": "ItemsGrid",
              "collection": "rows",
              "columnsCount": "${$computed.itemsCols}",
              "itemTemplate": "row_tpl"
            },
            "row_tpl": { "type": "Text", "text": "${$item.title}" }
          },
          "collections": { "rows": [{ "id": "r1", "fields": { "title": "A" } }] }
        }
        """;

        const string validLogicJs = """
        export function onInit(ctx) {
          ctx.computed.dayCols = "Auto,*,*,*,*,*";
          ctx.computed.periodRows = "Auto,*";
          ctx.computed.itemsCols = "5";
        }
        """;

        CustomWidgetWorkspaceValidationResult validRes = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            validManifest,
            validLogicJs);
        Assert.True(validRes.IsValid, string.Join(" | ", validRes.Diagnostics.Select(d => $"{d.Code}:{d.Message}")));
        Assert.DoesNotContain(validRes.Diagnostics, d => d.IsError);

        const string invalidGridTracksJs = """
        export function onInit(ctx) {
          ctx.computed.dayCols = "not-a-valid-track";
          ctx.computed.periodRows = "Auto,*";
          ctx.computed.itemsCols = "5";
        }
        """;
        CustomWidgetWorkspaceValidationResult badGridRes = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            validManifest,
            invalidGridTracksJs);
        Assert.False(badGridRes.IsValid);
        Assert.Contains(badGridRes.Diagnostics, d => d.IsError && string.Equals(d.Code, "InvalidComputedGridTracks", StringComparison.Ordinal));

        const string invalidItemsColsJs = """
        export function onInit(ctx) {
          ctx.computed.dayCols = "Auto,*,*,*,*,*";
          ctx.computed.periodRows = "Auto,*";
          ctx.computed.itemsCols = "99";
        }
        """;
        CustomWidgetWorkspaceValidationResult badItemsRes = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            validManifest,
            invalidItemsColsJs);
        Assert.False(badItemsRes.IsValid);
        Assert.Contains(badItemsRes.Diagnostics, d => d.IsError && string.Equals(d.Code, "InvalidComputedItemsGridColumns", StringComparison.Ordinal));
    }

    [Fact]
    public void GridTrackNormalizer_AndDynamicPlacementPlan_NormalizeFrUnitsClampSpansAndFlagOutOfRangeChildren()
    {
        Assert.True(CustomWidgetGridTrackNormalizer.TryNormalizeTrackSpecs("1fr 2fr auto", out string normalized));
        Assert.Equal("*,2*,Auto", normalized);

        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.grid-norm-shrink",
          "name": "Grid Norm Shrink",
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "children": ["g"]
            },
            "g": {
              "id": "g",
              "type": "Grid",
              "columns": "1fr 2fr auto",
              "rows": "Auto,*",
              "children": ["spanning", "trailing"]
            },
            "spanning": { "id": "spanning", "type": "Text", "text": "Span", "row": 0, "column": 1, "rowSpan": 3, "columnSpan": 3 },
            "trailing": { "id": "trailing", "type": "Text", "text": "Tail", "row": 0, "column": 2 }
          }
        }
        """;

        CustomWidgetParseResult parse = CustomWidgetManifestParser.Parse(manifestJson);
        Assert.True(parse.IsValid, string.Join(" | ", parse.Diagnostics.Select(d => d.Message)));
        CustomWidgetNodeSpec gridSpec = parse.Manifest!.View!.Children[0];
        Assert.Equal("*,2*,Auto", gridSpec.Columns);

        var threeColPlan = CustomWidgetNativeRenderer.ComputeGridTrackAndPlacementPlanCore(gridSpec, "*,*,*", "*,*");
        Assert.Equal(2, threeColPlan.Placements.Count);
        Assert.False(threeColPlan.OutOfRange[0]);
        Assert.False(threeColPlan.OutOfRange[1]);
        Assert.Equal(2, threeColPlan.Placements[0].ColumnSpan);
        Assert.Equal(2, threeColPlan.Placements[0].RowSpan);

        // Shrink Grid to 2 cols x 1 row -> trailing child (column=2) becomes OutOfRange, spans clamp into 2x1
        var shrunkPlan = CustomWidgetNativeRenderer.ComputeGridTrackAndPlacementPlanCore(gridSpec, "*,*", "Auto");
        Assert.False(shrunkPlan.OutOfRange[0]);
        Assert.True(shrunkPlan.OutOfRange[1]);
        Assert.Equal(1, shrunkPlan.Placements[0].ColumnSpan);
        Assert.Equal(1, shrunkPlan.Placements[0].RowSpan);

        // Grow back -> OutOfRange recovers cleanly
        var regrownPlan = CustomWidgetNativeRenderer.ComputeGridTrackAndPlacementPlanCore(gridSpec, "*,*,*", "*,*");
        Assert.False(regrownPlan.OutOfRange[1]);
    }

    [Fact]
    public void ScriptValidator_FlagsMissingExportedActionHandler_AndDeclarativeBrokenExpression()
    {
        const string scriptedManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.missing-export-check",
          "name": "Missing Export",
          "script": { "entry": "logic.js" },
          "root": "main",
          "elements": {
            "main": {
              "type": "Stack",
              "children": ["btn"]
            },
            "btn": {
              "id": "btn",
              "type": "Button",
              "label": "Run",
              "onClick": [{ "type": "script.run", "fn": "missingHandler" }]
            }
          }
        }
        """;

        const string logicJs = """
        export function onInit(ctx) {}
        """;

        CustomWidgetWorkspaceValidationResult missingExportRes = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            scriptedManifest,
            logicJs);
        Assert.False(missingExportRes.IsValid);
        Assert.Contains(
            missingExportRes.Diagnostics,
            d => d.IsError && string.Equals(d.Code, "UnknownScriptFunction", StringComparison.Ordinal));

        const string declarativeBadManifest = """
        {
          "schemaVersion": 1,
          "id": "custom.decl-dryrun-check",
          "name": "Declarative DryRun",
          "state": { "cols": "not-valid-tracks" },
          "root": "g",
          "elements": {
            "g": {
              "type": "Grid",
              "columns": "${$state.cols}",
              "rows": "Auto,*",
              "children": ["lbl"]
            },
            "lbl": { "id": "lbl", "type": "Text", "text": "Cell" }
          }
        }
        """;

        CustomWidgetWorkspaceValidationResult declRes = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            declarativeBadManifest,
            logicJs: null);
        Assert.False(declRes.IsValid);
        Assert.Contains(
            declRes.Diagnostics,
            d => d.IsError && string.Equals(d.Code, "InvalidComputedGridTracks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ViewModelUserSeedCarryover_ExcludesUnmodifiedSampleData_AndCarriesUserAddedRecordsAndFriendlyNodeTags()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-user-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.user-seed-test",
              "name": "User Seed Test",
              "collections": {
                "notes": {
                  "fields": [{ "name": "title", "type": "text" }]
                }
              },
              "state": { "customDraft": "" },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "children": ["t"]
                },
                "t": { "id": "t", "type": "Text", "text": "${$state.customDraft}" }
              }
            }
            """;

            var store = new CustomWidgetDataStore("custom.user-seed-test", Path.Combine(tempDir, "widgets"));
            await store.WriteManifestJsonAsync(manifestJson);

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            var locSvc = new LocalizationService(settingsSvc);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "custom.user-seed-test", Name = "User Seed Test", WidgetKind = WidgetKind.Custom },
                locSvc,
                store: store,
                dispatcherQueue: null,
                isPreviewOnly: true,
                settingsService: settingsSvc);
            await vm.InitializeAsync();

            bool appliedSample = vm.ApplyPreviewSampleDataJson(
                """{"collections":{"notes":[{"title":"Sample Note"}]},"state":{"filter":"all"}}""",
                seedOnlyIfEmptyOrSample: true);
            Assert.True(appliedSample);
            Assert.Null(vm.BuildUserEnteredStoreSeedPayload());

            // User adds a real record and sets a custom state key
            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions(
                    """[{"type":"collection.append","collection":"notes","fields":{"title":"Real User Note"}},{"type":"state.set","key":"customDraft","value":"hello"}]"""),
                vm.CreateEvaluationContext());

            // Re-applying sample data with seedOnlyIfEmptyOrSample preserves user-mutated data
            vm.ApplyPreviewSampleDataJson(
                """{"collections":{"notes":[{"title":"Overwrite Attempt"}]},"state":{"customDraft":"overwritten"}}""",
                seedOnlyIfEmptyOrSample: true);
            Assert.Equal("hello", vm.State["customDraft"]);

            CustomWidgetStorePayload? userSeed = vm.BuildUserEnteredStoreSeedPayload();
            Assert.NotNull(userSeed);
            Assert.Equal("hello", userSeed!.State["customDraft"]);
            Assert.False(userSeed.State.ContainsKey("filter"));
            Assert.True(userSeed.Collections.TryGetValue("notes", out List<CustomWidgetRecord>? carriedNotes));
            CustomWidgetRecord carriedNote = Assert.Single(carriedNotes!);
            Assert.Equal("Real User Note", carriedNote.Fields["title"]);

            string friendlyTag = CustomWidgetNativeRenderer.FormatPickedNodeTag("Button", "Save", locSvc);
            string friendlyType = CustomWidgetNativeRenderer.GetFriendlyNodeTypeLabel("Button", locSvc);
            Assert.Contains(friendlyType, friendlyTag, StringComparison.Ordinal);
            Assert.Contains("Save", friendlyTag, StringComparison.Ordinal);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SegmentedProgressBar_PillBadge_AndDividedList_ComputeAndPatchCleanly()
    {
        // Pure deterministic segment math for tiny positive balances vs 0% and 100%
        Assert.Equal(0, CustomWidgetSegmentedBar.ComputeFilledSegmentCount(0.0, 0.0, 100.0, 42));
        Assert.Equal(1, CustomWidgetSegmentedBar.ComputeFilledSegmentCount(1.55, 0.0, 100.0, 42));
        Assert.Equal(41, CustomWidgetSegmentedBar.ComputeFilledSegmentCount(98.0, 0.0, 100.0, 42));
        Assert.Equal(42, CustomWidgetSegmentedBar.ComputeFilledSegmentCount(100.0, 0.0, 100.0, 42));
        Assert.InRange(CustomWidgetSegmentedBar.ComputeTargetSegmentCount(310.0, 3.8, 2.5), 44, 52);

        Assert.True(CustomWidgetNativeRenderer.IsSegmentedProgressBarVariant("segmented"));
        Assert.True(CustomWidgetNativeRenderer.IsSegmentedProgressBarVariant("pills"));
        Assert.False(CustomWidgetNativeRenderer.IsSegmentedProgressBarVariant("standard"));

        Assert.True(CustomWidgetNativeRenderer.IsDividedListVariant("divided"));
        Assert.True(CustomWidgetNativeRenderer.IsDividedOrFlushListVariant("divided"));
        Assert.True(CustomWidgetNativeRenderer.IsDividedOrFlushListVariant("flush"));
        Assert.False(CustomWidgetNativeRenderer.IsDividedOrFlushListVariant("standard"));
    }

    [Fact]
    public async Task ProgressBarIndeterminate_DynamicMenuTokens_AndComputedRichTextRuns_EvaluateAndPatchLive()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-ai-inbox-renderer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            const string manifestJson = """
            {
              "schemaVersion": 1,
              "id": "custom.ai-inbox-primitives",
              "name": "AI Inbox Primitives",
              "script": { "entry": "logic.js", "tickIntervalMs": 0 },
              "state": {
                "aiStatus": "idle",
                "selectedLang": "en-US",
                "mode": "before"
              },
              "root": "main",
              "elements": {
                "main": {
                  "type": "Stack",
                  "children": ["prog", "lang_dd", "diff_rt"]
                },
                "prog": { "type": "ProgressBar", "value": "45", "height": 3, "indeterminate": "${state.aiStatus == 'running'}" },
                "lang_dd": {
                  "type": "DropDownButton",
                  "text": "Lang",
                  "menuItems": [
                    { "label": "English", "icon": "${$computed.checkIcon}", "onClick": [{ "type": "state.set", "key": "selectedLang", "value": "en-US" }] }
                  ]
                },
                "diff_rt": { "type": "RichText", "items": "${$computed.diffSpans}", "wrap": true }
              }
            }
            """;

            const string logicJs = """
            export function onInit(ctx) {
              ctx.computed.checkIcon = ctx.state.selectedLang === 'en-US' ? '\uE73E' : '';
              ctx.computed.diffSpans = ctx.state.mode === 'after'
                ? [
                    { id: 's0', text: 'old ', color: 'danger', strikethrough: 'true', bold: 'false' },
                    { id: 's1', text: 'updated', color: 'success', strikethrough: 'false', bold: 'true' }
                  ]
                : [
                    { id: 's0', text: 'initial', color: 'default', strikethrough: 'false', bold: 'false' }
                  ];
            }
            export function setAfterState(ctx) {
              ctx.state.aiStatus = 'running';
              ctx.state.mode = 'after';
              onInit(ctx);
            }
            """;

            CustomWidgetParseResult parse = CustomWidgetManifestParser.Parse(manifestJson);
            Assert.True(parse.IsValid, string.Join("; ", parse.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));
            Assert.NotNull(parse.Manifest?.View);

            CustomWidgetNodeSpec progressSpec = parse.Manifest!.View!.Children[0];
            CustomWidgetNodeSpec dropDownSpec = parse.Manifest.View.Children[1];
            CustomWidgetNodeSpec richTextSpec = parse.Manifest.View.Children[2];

            Assert.True(CustomWidgetNativeRenderer.HasDynamicMenuTokens(dropDownSpec.MenuItems));

            var store = new CustomWidgetDataStore("custom.ai-inbox-primitives", Path.Combine(tempDir, "widgets"));
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs, locales: null);

            var settingsSvc = new SettingsService(Path.Combine(tempDir, "settings"));
            var locSvc = new LocalizationService(settingsSvc);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "custom.ai-inbox-primitives", Name = "Primitives", WidgetKind = WidgetKind.Custom },
                locSvc,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsSvc);
            await vm.InitializeAsync();

            var defaultProfile = CustomWidgetHostAppearanceProfile.FromSettings(new AppSettings());
            var renderer = new CustomWidgetNativeRenderer(vm, appearanceProvider: () => defaultProfile);

            bool observedIndeterminate = false;
            double observedMinHeight = 0;
            IReadOnlyList<CustomWidgetNativeRenderer.ResolvedRichTextRunSpec> observedRuns = [];

            renderer.RegisterRetainedPatcherForTest(evalCtx =>
            {
                (bool isIndet, _, double minH) = CustomWidgetNativeRenderer.ResolveStandardProgressBarState(progressSpec, evalCtx);
                observedIndeterminate = isIndet;
                observedMinHeight = minH;
                observedRuns = CustomWidgetNativeRenderer.ResolveComputedRichTextRuns(richTextSpec, evalCtx);
            });

            Assert.True(renderer.TryPatchRetainedTree());
            Assert.False(observedIndeterminate);
            Assert.Equal(3.0, observedMinHeight);
            CustomWidgetNativeRenderer.ResolvedRichTextRunSpec initialRun = Assert.Single(observedRuns);
            Assert.Equal("initial", initialRun.Text);
            Assert.False(initialRun.Strikethrough);

            await vm.ExecuteActionsAsync(
                CustomWidgetManifestParser.ParseDispatchedActions("""[{ "type": "script.run", "fn": "setAfterState" }]"""));

            Assert.True(renderer.TryPatchRetainedTree());
            Assert.True(observedIndeterminate);
            Assert.Equal(2, observedRuns.Count);
            Assert.Equal("old ", observedRuns[0].Text);
            Assert.True(observedRuns[0].Strikethrough);
            Assert.Equal("danger", observedRuns[0].Color);
            Assert.Equal("updated", observedRuns[1].Text);
            Assert.True(observedRuns[1].Bold);
            Assert.Equal("success", observedRuns[1].Color);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task AiInboxTemplate_LanguageSwap_DictionaryCard_SourceAnchor_DiffSpans_AndTodoSync_WorkEndToEnd()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-ai-inbox-v4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings.json"));
            // Pin zh-CN: the assertions below expect Chinese UI strings, and the default
            // Language=System follows the machine UI culture (en-US on CI runners).
            settingsService.Settings.Language = SettingsService.LanguageChinese;
            var localization = new LocalizationService(settingsService);
            var store = new CustomWidgetDataStore("ai-inbox-v4-test", Path.Combine(tempDir, "widgets"));
            var credStore = new CustomWidgetAiCredentialStore(Path.Combine(tempDir, "creds"));
            var prefsStore = new CustomWidgetStudioPreferencesStore(Path.Combine(tempDir, "studio-prefs.json"));

            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: "deepseek",
                ApiKey: "sk-live-test-key",
                CustomEndpoint: string.Empty,
                CustomModel: "deepseek-chat",
                ThinkingMode: "auto"));
            prefsStore.SetPreferredAiProviderId("deepseek");

            string nextAiContentJson = string.Empty;
            using var httpClient = new HttpClient(new DelegateHttpMessageHandler((_req, _ct) =>
            {
                string envelope = JsonSerializer.Serialize(new
                {
                    choices = new[]
                    {
                        new { message = new { content = nextAiContentJson } }
                    }
                });
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(envelope, Encoding.UTF8, "application/json")
                });
            }));

            var aiService = new DeskBoxAiService(credStore, prefsStore, httpClient);
            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "ai-inbox-v4-test", Name = "AI 翻译与润色", WidgetKind = WidgetKind.Custom },
                localization,
                store: store,
                dispatcherQueue: null,
                notificationServiceProvider: null,
                isPreviewOnly: false,
                aiService: aiService,
                preferredAiProviderResolver: () => prefsStore.PreferredAiProviderId,
                settingsService: settingsService);

            CustomWidgetTemplateBundle inboxBundle =
                CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.AiInboxStarterId, "zh-CN");
            Assert.NotNull(inboxBundle.Locales);
            Assert.False(Regex.IsMatch(inboxBundle.ManifestJson, @"[\u4e00-\u9fff]"));

            var ws = new StudioWorkspaceEngine(inboxBundle.ManifestJson, inboxBundle.LogicJs, inboxBundle.Locales);
            string beforeNorm = ws.GetManifestJson(indented: true)!;
            bool drifted = ws.NormalizeDomInPlace();
            if (drifted)
            {
                string afterNorm = ws.GetManifestJson(indented: true)!;
                string[] bLines = beforeNorm.Replace("\r\n", "\n").Split('\n');
                string[] aLines = afterNorm.Replace("\r\n", "\n").Split('\n');
                int diffIdx = 0;
                while (diffIdx < bLines.Length && diffIdx < aLines.Length && bLines[diffIdx] == aLines[diffIdx]) diffIdx++;
                Assert.Fail(
                    $"ai-inbox DOM drift at L{diffIdx + 1}: " +
                    $"'{(diffIdx < bLines.Length ? bLines[diffIdx].Trim() : "<eof>")}' -> " +
                    $"'{(diffIdx < aLines.Length ? aLines[diffIdx].Trim() : "<eof>")}'");
            }

            CustomWidgetWorkspaceValidationResult wsValidation = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
                inboxBundle.ManifestJson,
                inboxBundle.LogicJs,
                inboxBundle.Locales);
            Assert.True(wsValidation.IsValid, string.Join("; ", wsValidation.Diagnostics.Select(d => $"{d.Path}: {d.Message}")));
            Assert.Empty(wsValidation.Diagnostics);

            CustomWidgetCompilerResult compileRes = CustomWidgetCompilerPipeline.Compile(
                inboxBundle.ManifestJson,
                inboxBundle.LogicJs,
                inboxBundle.Locales,
                runOnInitDryRun: true);
            Assert.Empty(compileRes.HardDiagnostics);
            Assert.Empty(compileRes.ActionableSoftDiagnostics);

            _ = CustomWidgetI18nEngine.AutoCompileIfLocalized(
                inboxBundle.ManifestJson,
                inboxBundle.LogicJs,
                inboxBundle.Locales,
                "zh-CN",
                out CustomWidgetLocaleCatalog? updatedCat);
            Assert.NotNull(updatedCat);
            Assert.Equal(13, updatedCat!.Locales.Count);
            foreach ((string culture, IReadOnlyDictionary<string, string> entries) in updatedCat.Locales)
            {
                Assert.True(entries.Count == 153, $"Locale '{culture}' expected 153 keys after AutoCompile, got {entries.Count}.");
            }

            Thickness rootPad = CustomWidgetNativeRenderer.ResolveRootSafePadding(
                CustomWidgetManifestParser.Parse(inboxBundle.ManifestJson).Manifest!.View.Padding);
            Assert.Equal(12, rootPad.Left);
            Assert.Equal(12, rootPad.Right);
            Assert.Equal(12, rootPad.Top);

            await store.WriteWorkspaceBundleAsync(
                inboxBundle.ManifestJson,
                inboxBundle.LogicJs,
                locales: inboxBundle.Locales);
            await vm.InitializeAsync();

            static CustomWidgetNodeSpec? FindAiRunButton(CustomWidgetNodeSpec node)
            {
                if (string.Equals(node.Type, "Button", StringComparison.OrdinalIgnoreCase) &&
                    node.OnClick.Any(static a => string.Equals(a.Type, "ai.run", StringComparison.OrdinalIgnoreCase)) &&
                    node.OnClick.Any(static a => string.Equals(a.Fn, "prepareDraftRun", StringComparison.Ordinal)))
                {
                    return node;
                }

                foreach (CustomWidgetNodeSpec child in node.Children)
                {
                    if (FindAiRunButton(child) is { } found)
                    {
                        return found;
                    }
                }

                return null;
            }

            CustomWidgetNodeSpec runButton = FindAiRunButton(vm.Manifest.View!)!;
            Assert.NotNull(runButton);

            // 1. Language detection & bidirectional swap in Translate mode
            await vm.ExecuteActionsAsync(
            [
                new CustomWidgetActionSpec("state.set", Key: "draft", Value: "Hello world"),
                new CustomWidgetActionSpec("script.run", Fn: "onTabChanged")
            ]);
            CustomWidgetEvaluationContext detectedCtx = vm.CreateEvaluationContext();
            Assert.Contains("自动", detectedCtx.Computed["srcLangBtnText"].GetString()!, StringComparison.Ordinal);
            Assert.Contains("英语", detectedCtx.Computed["srcLangBtnText"].GetString()!, StringComparison.Ordinal);

            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("script.run", Fn: "swapLanguages")]);
            Assert.Equal("zh-CN", vm.State["sourceLang"]);
            Assert.Equal("en-US", vm.State["overrideLang"]);

            // Restore auto pair before testing dictionary lookup
            await vm.ExecuteActionsAsync(
            [
                new CustomWidgetActionSpec("state.set", Key: "sourceLang", Value: "auto"),
                new CustomWidgetActionSpec("state.set", Key: "overrideLang", Value: string.Empty)
            ]);

            // 2. Short-word Dictionary Card + 1-line Source Anchor edit/cancel round-trip
            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("state.set", Key: "draft", Value: "resilient")]);
            nextAiContentJson = """
            {"result":"有韧性的；能迅速恢复的","tag":"形容词","insight":"近义词：tough, adaptable","phonetic":"/rɪˈzɪliənt/","pos":"adj.","exampleSrc":"Systems must remain resilient.","exampleDst":"系统必须保持韧性。"}
            """;
            await vm.ExecuteActionsAsync(runButton.OnClick!);

            Assert.Equal("resilient", vm.State["dictWord"]);
            Assert.Equal("/rɪˈzɪliənt/", vm.State["dictPhonetic"]);
            Assert.Equal("adj.", vm.State["dictPos"]);
            Assert.Equal("false", vm.State["isEditingSource"]);
            Assert.Equal(string.Empty, vm.State["draft"]);
            Assert.Equal("resilient", vm.State["lastSourceText"]);
            Assert.Equal("true", vm.CreateEvaluationContext().Computed["isDictResult"].GetString());

            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("script.run", Fn: "openSourceEditor")]);
            Assert.Equal("true", vm.State["isEditingSource"]);
            Assert.Equal("resilient", vm.State["draft"]);

            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("script.run", Fn: "cancelSourceEdit")]);
            Assert.Equal("false", vm.State["isEditingSource"]);

            // 3. Polish mode word-level Diff spans
            await vm.ExecuteActionsAsync(
            [
                new CustomWidgetActionSpec("state.set", Key: "mode", Value: "polish"),
                new CustomWidgetActionSpec("script.run", Fn: "onModeChanged"),
                new CustomWidgetActionSpec("state.set", Key: "draft", Value: "Please send file soon")
            ]);
            nextAiContentJson = """
            {"result":"Could you please send the file soon?","tag":"商务正式","insight":"语气更为礼貌得体"}
            """;
            await vm.ExecuteActionsAsync(runButton.OnClick!);

            Assert.True(CustomWidgetNativeRenderer.TryResolveComputedRecords(
                "${$computed.diffSpans}",
                vm.CreateEvaluationContext(),
                out IReadOnlyList<CustomWidgetRecord> diffRecords));
            Assert.True(diffRecords.Count >= 2);
            Assert.Contains(diffRecords, r => r.GetField("strikethrough") == "true");
            Assert.Contains(diffRecords, r => r.GetField("bold") == "true");

            // 4. Todos mode extraction + DeskBox todo.add host sync
            await vm.ExecuteActionsAsync(
            [
                new CustomWidgetActionSpec("state.set", Key: "mode", Value: "todos"),
                new CustomWidgetActionSpec("script.run", Fn: "onModeChanged"),
                new CustomWidgetActionSpec("state.set", Key: "draft", Value: "明天完成接口联调并补齐单元测试")
            ]);
            nextAiContentJson = """
            {"result":"- [ ] 完成接口联调\n- [ ] 补齐单元测试","tag":"待办","insight":"共 2 项任务"}
            """;
            await vm.ExecuteActionsAsync(runButton.OnClick!);

            Assert.True(CustomWidgetNativeRenderer.TryResolveComputedRecords(
                "${$computed.todoItems}",
                vm.CreateEvaluationContext(),
                out IReadOnlyList<CustomWidgetRecord> todoRecords));
            Assert.Equal(2, todoRecords.Count);

            await vm.ExecuteActionsAsync([new CustomWidgetActionSpec("script.run", Fn: "syncTodosToDeskBox")]);
            Assert.Equal("true", vm.State["todosSynced"]);

            string isolatedWidgetsRoot = Path.Combine(settingsService.DataDirectoryPath, "widgets");
            CustomWidgetTodoSnapshot todoSnap = await CustomWidgetHostSnapshotService.CaptureTodoSnapshotAsync(
                settingsService,
                maxItems: 50,
                widgetsDataRootOverride: isolatedWidgetsRoot);
            Assert.Contains(todoSnap.Records, r => r.GetField("title") == "完成接口联调");
            Assert.Contains(todoSnap.Records, r => r.GetField("title") == "补齐单元测试");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> sendAsync)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => sendAsync(request, cancellationToken);
    }
}
