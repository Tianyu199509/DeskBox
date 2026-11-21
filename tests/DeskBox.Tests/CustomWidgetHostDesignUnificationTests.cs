using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.ViewModels;
using Windows.UI;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetHostDesignUnificationTests
{
    [Fact]
    public void ContainerSurfacePolicy_StripsOpaqueHex_RetainsSemanticAndTransparent()
    {
        Assert.True(CustomWidgetNativeRenderer.ShouldStripContainerRawBackground("#0F172A", out bool opaqueTransparent));
        Assert.False(opaqueTransparent);

        Assert.True(CustomWidgetNativeRenderer.ShouldStripContainerRawBackground("rgba(15, 23, 42, 0.85)", out bool rgbaTransparent));
        Assert.False(rgbaTransparent);

        Assert.False(CustomWidgetNativeRenderer.ShouldStripContainerRawBackground("transparent", out bool explicitTransparent));
        Assert.True(explicitTransparent);

        Assert.False(CustomWidgetNativeRenderer.ShouldStripContainerRawBackground("subtle", out bool semanticTransparent));
        Assert.False(semanticTransparent);
    }

    [Fact]
    public void SafeTextColorEvaluation_CoercesNeutralHex_AndPreservesChromaticContrast()
    {
        // Light grey/white neutral hex is coerced to theme-adaptive "default" text instead of washing out in Light theme
        (bool usesThemeLight, string semanticLight, _) = CustomWidgetNativeRenderer.EvaluateSafeTextColorToken("#E2E8F0", isDark: false);
        Assert.True(usesThemeLight);
        Assert.Equal("default", semanticLight);

        // Mid slate neutral hex is coerced to theme-adaptive "secondary"
        (bool usesThemeSecondary, string semanticSecondary, _) = CustomWidgetNativeRenderer.EvaluateSafeTextColorToken("#94A3B8", isDark: false);
        Assert.True(usesThemeSecondary);
        Assert.Equal("secondary", semanticSecondary);

        // High-chroma cyan stays chromatic and darkens when bright on Light backgrounds
        Assert.True(CustomWidgetNativeRenderer.TryParseRawColor("#38BDF8", out Color rawCyan));
        (bool usesThemeCyan, _, Color safeLightCyan) = CustomWidgetNativeRenderer.EvaluateSafeTextColorToken("#38BDF8", isDark: false);
        Assert.False(usesThemeCyan);
        Assert.True(CustomWidgetNativeRenderer.RelativeLuminance(safeLightCyan) < CustomWidgetNativeRenderer.RelativeLuminance(rawCyan));

        // Dark saturated blue lightens on Dark backgrounds for legibility
        Assert.True(CustomWidgetNativeRenderer.TryParseRawColor("#1E3A8A", out Color rawNavy));
        (bool usesThemeNavy, _, Color safeDarkNavy) = CustomWidgetNativeRenderer.EvaluateSafeTextColorToken("#1E3A8A", isDark: true);
        Assert.False(usesThemeNavy);
        Assert.True(CustomWidgetNativeRenderer.RelativeLuminance(safeDarkNavy) > CustomWidgetNativeRenderer.RelativeLuminance(rawNavy));
    }

    [Fact]
    public async Task HostTypographyRamp_AndStatUnitFormatting_MatchNativeScale()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.typography-ramp",
          "name": "Typography Ramp",
          "icon": "📐",
          "capsule": { "title": "Ramp", "summary": "OK", "glyph": "📐" },
          "root": "root",
          "elements": {
            "root": {
              "type": "Stack",
              "background": "#0F172A",
              "padding": "12,10,12,12",
              "children": ["sample_text"]
            },
            "sample_text": {
              "type": "Text",
              "text": "Sample"
            }
          }
        }
        """;

        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-typo-ramp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var settingsService = new SettingsService(Path.Combine(tempDir, "settings"));
            var locService = new LocalizationService(settingsService);
            var store = new CustomWidgetDataStore("custom_typography_ramp", Path.Combine(tempDir, "widgets"));
            await store.WriteWorkspaceBundleAsync(manifestJson, logicJs: null, locales: null);

            using var vm = new CustomWidgetViewModel(
                new WidgetConfig { Id = "custom_typography_ramp", Name = "Typography Ramp", WidgetKind = WidgetKind.Custom },
                locService,
                store: store,
                dispatcherQueue: null,
                settingsService: settingsService);
            await vm.InitializeAsync();

            var defaultProfile = CustomWidgetHostAppearanceProfile.FromSettings(new AppSettings());
            var renderer = new CustomWidgetNativeRenderer(vm, appearanceProvider: () => defaultProfile);

            Assert.Equal(10.5, renderer.CaptionFontSize);
            Assert.Equal(11.5, renderer.BodyFontSize);
            Assert.Equal(12.5, renderer.SubtitleFontSize);
            Assert.Equal(16.0, renderer.MetricFontSize);
            Assert.Equal(21.0, renderer.HeroFontSize);

            Assert.Equal(10.5, renderer.SnapLegacyFontSizeToHostTier(9.0));
            Assert.Equal(11.5, renderer.SnapLegacyFontSizeToHostTier(12.0));
            Assert.Equal(12.5, renderer.SnapLegacyFontSizeToHostTier(14.0));
            Assert.Equal(16.0, renderer.SnapLegacyFontSizeToHostTier(18.0));
            Assert.Equal(21.0, renderer.SnapLegacyFontSizeToHostTier(28.0));

            Assert.Equal("74%", CustomWidgetNativeRenderer.FormatStatValueWithUnit("74", "%"));
            Assert.Equal("74%", CustomWidgetNativeRenderer.FormatStatValueWithUnit("74%", "%"));
            Assert.Equal("12 ms", CustomWidgetNativeRenderer.FormatStatValueWithUnit("12", "ms"));
            Assert.Equal("42", CustomWidgetNativeRenderer.FormatStatValueWithUnit("42", null));

            // Normal Stack keeps host chrome/safe padding even if legacy background is present
            CustomWidgetManifest parsed = CustomWidgetManifestParser.Parse(manifestJson).Manifest!;
            Assert.False(CustomWidgetChromeCategoryClassifier.IsFullBleedRootLayout(parsed));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ScriptEngine_PopulatesLiveEnvSysAndDeviceTelemetry_AndMemTokenAliases()
    {
        var sysSnapshot = new CustomWidgetSystemMetricsSnapshot
        {
            CpuPercent = 37,
            MemoryPercent = 74,
            MemoryUsedGb = 23.7,
            MemoryTotalGb = 32.0,
            GpuPercent = 18,
            GpuName = "NVIDIA GeForce RTX 4070",
            GpuVramUsedGb = 3.1,
            GpuVramTotalGb = 12.0,
            NetDownText = "15 KB/s",
            NetUpText = "640 KB/s",
            BatteryPercent = 91,
            HasBattery = true,
            BatteryOnAc = true
        };
        Assert.Equal("74", sysSnapshot.ResolveToken("mem.percent"));
        Assert.Equal("23.7 / 32 GB", sysSnapshot.ResolveToken("mem.summaryText"));

        var deviceSnapshot = new CustomWidgetDeviceStateSnapshot
        {
            VolumePercent = 65,
            IsMuted = false,
            AudioDeviceName = "Studio Monitors",
            BrightnessPercent = 80,
            BrightnessSupported = true,
            WifiEnabled = true,
            BluetoothEnabled = false,
            NetworkConnected = true,
            KeepAwakeActive = true
        };

        const string logicJs = """
        export function onInit(ctx) {
          ctx.computed.cpu = ctx.env.sys.cpuPercent;
          ctx.computed.mem = ctx.env.sys.memorySummary;
          ctx.computed.gpu = ctx.env.sys.gpuName;
          ctx.computed.vram = ctx.env.sys.gpuVramSummary;
          ctx.computed.net = `${ctx.env.sys.netDown} / ${ctx.env.sys.netUp}`;
          ctx.computed.vol = ctx.env.device.volume;
          ctx.computed.dev = ctx.env.device.audioDeviceName;
          ctx.computed.wifi = ctx.env.device.wifiEnabled;
          ctx.computed.awake = ctx.env.device.keepAwake;
        }
        """;

        CustomWidgetScriptCompileResult compiled = CustomWidgetScriptEngine.Compile(logicJs);
        Assert.True(compiled.IsSuccess, compiled.ErrorMessage);

        CustomWidgetScriptExecutionResult res = CustomWidgetScriptEngine.Execute(
            compiled.Compiled!,
            "onInit",
            new CustomWidgetScriptExecutionInput
            {
                CultureName = "en-US",
                SystemMetrics = sysSnapshot,
                DeviceState = deviceSnapshot
            });

        Assert.True(res.IsSuccess, res.ErrorMessage);
        Assert.Equal("37", res.Computed["cpu"].GetString());
        Assert.Equal("23.7 / 32 GB", res.Computed["mem"].GetString());
        Assert.Equal("NVIDIA GeForce RTX 4070", res.Computed["gpu"].GetString());
        Assert.Equal("3.1 / 12 GB", res.Computed["vram"].GetString());
        Assert.Equal("15 KB/s / 640 KB/s", res.Computed["net"].GetString());
        Assert.Equal("65", res.Computed["vol"].GetString());
        Assert.Equal("Studio Monitors", res.Computed["dev"].GetString());
        Assert.Equal("true", res.Computed["wifi"].GetString());
        Assert.Equal("true", res.Computed["awake"].GetString());
    }

    [Fact]
    public void HealthAnalyzer_FlagsInvalidScriptSourcesStateComputedMismatchSystemControlAndContainerOverride()
    {
        const string badManifestJson = """
        {
          "schemaVersion": 1,
          "id": "custom.audit-sample",
          "name": "Audit Sample",
          "icon": "🧪",
          "state": { "cpuLabel": "0%" },
          "script": { "entry": "logic.js", "tickIntervalMs": 2000 },
          "capsule": { "title": "Audit", "summary": "OK", "glyph": "🧪" },
          "root": "root",
          "elements": {
            "root": {
              "type": "Stack",
              "background": "#0F172A",
              "padding": "12",
              "children": ["cpu_label", "restart_wifi_btn"]
            },
            "cpu_label": {
              "type": "Text",
              "text": "${$computed.cpuLabel}"
            },
            "restart_wifi_btn": {
              "type": "Button",
              "text": "Restart Wifi",
              "onClick": { "type": "system.control", "key": "wifi.unsupportedAction" }
            }
          }
        }
        """;

        const string badLogicJs = """
        export function onInit(ctx) {
          const snap = ctx.sources.systemMetrics;
          ctx.state.cpuLabel = snap?.cpu ?? "0%";
        }
        """;

        CustomWidgetManifest parsed = CustomWidgetManifestParser.Parse(badManifestJson).Manifest!;
        CustomWidgetHealthReport report = CustomWidgetHealthAnalyzer.AnalyzeHealth(
            badManifestJson,
            parsed,
            storePayload: null,
            logicJs: badLogicJs);

        Assert.Contains(report.Issues, i => i.Code == "script.invalid_ctx_sources");
        Assert.Contains(report.Issues, i => i.Code == "script.state_computed_mismatch");
        Assert.Contains(report.Issues, i => i.Code == "interaction.invalid_system_control");
        Assert.Contains(report.Issues, i => i.Code == "style.container_surface_override");
    }

    [Fact]
    public void PromptBuilders_InjectConfiguredDefaultWidgetSize_AndOmitChromeOverrides()
    {
        string studioPrompt = CustomWidgetTemplates.BuildStudioCoCreationUserPrompt(
            "做一个桌面倒计时小组件",
            uiLanguage: "zh-CN",
            defaultWidgetWidth: 320,
            defaultWidgetHeight: 450);

        Assert.Contains("320", studioPrompt, StringComparison.Ordinal);
        Assert.Contains("450", studioPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"chrome\": \"immersive\"", studioPrompt, StringComparison.Ordinal);

        string enSystemPrompt = CustomWidgetTemplates.BuildStudioCoCreationSystemPrompt(
            uiLanguage: "en-US");
        string enUserPrompt = CustomWidgetTemplates.BuildStudioCoCreationUserPrompt(
            "Build a daily focus counter",
            uiLanguage: "en-US",
            defaultWidgetWidth: 310,
            defaultWidgetHeight: 420);

        Assert.Contains("310", enUserPrompt, StringComparison.Ordinal);
        Assert.Contains("420", enUserPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"chrome\": \"immersive\"", enSystemPrompt, StringComparison.Ordinal);
        Assert.DoesNotContain("\"chrome\": \"immersive\"", enUserPrompt, StringComparison.Ordinal);
    }
}
