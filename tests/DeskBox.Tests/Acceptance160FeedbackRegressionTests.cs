using System.Net;
using System.Text;
using System.Text.Json;
using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.Services;
using Windows.Foundation;

namespace DeskBox.Tests;

public sealed class Acceptance160FeedbackRegressionTests
{
    [Fact]
    public void AppExecutablePath_ResolvesMainDeskBoxExe_FromSettingsAndUpdaterCompanions()
    {
        const string installDir = @"C:\Program Files\DeskBox";
        string mainExe = Path.Combine(installDir, "DeskBox.exe");
        string settingsExe = Path.Combine(installDir, "DeskBox.Settings.exe");
        string updaterExe = Path.Combine(installDir, "DeskBox.Updater.exe");

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            mainExe,
            settingsExe,
            updaterExe
        };

        Assert.True(AppExecutablePath.IsCompanionExecutablePath(settingsExe));
        Assert.True(AppExecutablePath.IsCompanionExecutablePath(updaterExe));
        Assert.False(AppExecutablePath.IsCompanionExecutablePath(mainExe));

        Assert.Equal(
            mainExe,
            AppExecutablePath.ResolveMainExecutablePath(
                processPathOverride: settingsExe,
                baseDirectoryOverride: installDir,
                fileExists: existing.Contains));
        Assert.Equal(
            mainExe,
            AppExecutablePath.ResolveMainExecutablePath(
                processPathOverride: updaterExe,
                baseDirectoryOverride: installDir,
                fileExists: existing.Contains));
        Assert.Equal(
            mainExe,
            AppExecutablePath.ResolveMainExecutablePath(
                processPathOverride: mainExe,
                baseDirectoryOverride: installDir,
                fileExists: existing.Contains));
    }

    [Fact]
    public void DirectStartupService_CompanionSettingsHostResolvesMainExe_AndSelfHealsCompanionTask()
    {
        string tempInstallDir = Path.Combine(Path.GetTempPath(), $"deskbox-startup-comp-{Guid.NewGuid():N}");
        string otherInstallDir = Path.Combine(Path.GetTempPath(), $"deskbox-startup-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempInstallDir);
        Directory.CreateDirectory(otherInstallDir);

        try
        {
            string mainExe = Path.Combine(tempInstallDir, "DeskBox.exe");
            string settingsExe = Path.Combine(tempInstallDir, "DeskBox.Settings.exe");
            string foreignExe = Path.Combine(otherInstallDir, "DeskBox.exe");
            File.WriteAllText(mainExe, "stub");
            File.WriteAllText(settingsExe, "stub");
            File.WriteAllText(foreignExe, "stub");

            // 1. Existing task registered by main DeskBox.exe is recognized as Enabled inside DeskBox.Settings.exe
            var backend = new StubTaskBackend
            {
                Registration = CreateTask(mainExe)
            };
            var runStore = new StubRunStore();
            StartupMode? mode = StartupMode.ScheduledTask;
            var settingsHostStartup = new DirectStartupService(
                backend,
                runStore,
                () => AppExecutablePath.ResolveMainExecutablePath(processPathOverride: settingsExe),
                logger: static _ => { },
                runEntryApprovedProvider: static () => true,
                modeProvider: () => mode,
                modeWriter: m => mode = m);

            Assert.Equal(StartupRegistrationState.Enabled, settingsHostStartup.GetState());
            Assert.True(settingsHostStartup.Disable().State == StartupRegistrationState.NotRegistered);
            StartupOperationResult reenabled = settingsHostStartup.Enable();
            Assert.Equal(StartupRegistrationState.Enabled, reenabled.State);
            Assert.Equal(mainExe, backend.Registration?.ExecutablePath);

            // 2. Self-heal: if an earlier buggy companion run registered DeskBox.Settings.exe in the same directory,
            // it does NOT lock out with PathMismatch and cleanly repairs to DeskBox.exe on Enable or Migration.
            backend.Registration = CreateTask(settingsExe);
            Assert.Equal(StartupRegistrationState.NotRegistered, settingsHostStartup.GetState());

            settingsHostStartup.TryMigrateLegacyRegistration();
            Assert.Equal(StartupRegistrationState.Enabled, settingsHostStartup.GetState());
            Assert.Equal(mainExe, backend.Registration?.ExecutablePath);

            // 3. A genuinely separate installation on disk still returns PathMismatch.
            backend.Registration = CreateTask(foreignExe);
            Assert.Equal(StartupRegistrationState.PathMismatch, settingsHostStartup.GetState());
            Assert.Equal(StartupRegistrationState.PathMismatch, settingsHostStartup.Enable().State);
        }
        finally
        {
            try { Directory.Delete(tempInstallDir, recursive: true); } catch { }
            try { Directory.Delete(otherInstallDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void WrapFlowPanel_DoesNotPhantomWrapAt125PercentDpiFloatRounding_AndExpandsWhenConstrained()
    {
        // Exact 125% DPI (0.8 DIP step) widths where (double)w1 + 16 + (double)w2 + 16 + (double)w3
        // = 251.20000457763672 while (float)251.2 = 251.1999969482422.
        Size[] sizes =
        [
            new Size(94.4f, 32f),
            new Size(62.4f, 32f),
            new Size(62.4f, 32f)
        ];

        Size measuredUnbounded = WrapFlowPanel.ComputeMeasuredSize(
            sizes,
            availableWidth: double.PositiveInfinity,
            horizontalSpacing: 16,
            verticalSpacing: 6);
        Assert.Equal(32, measuredUnbounded.Height, precision: 3);

        // Arrange with exact measured Size AND with a slightly layout-rounded narrower slot (-0.4 DIP)
        var rects = new Rect[sizes.Length];
        double arrangedHeightExact = WrapFlowPanel.ComputeArrangement(
            sizes,
            measuredUnbounded,
            horizontalSpacing: 16,
            verticalSpacing: 6,
            rects);
        Assert.Equal(32, arrangedHeightExact, precision: 3);
        Assert.All(rects, r => Assert.Equal(0, r.Y, precision: 3));

        double arrangedHeightLayoutRounded = WrapFlowPanel.ComputeArrangement(
            sizes,
            new Size(251.2f - 0.4f, 32f),
            horizontalSpacing: 16,
            verticalSpacing: 6,
            rects);
        Assert.Equal(32, arrangedHeightLayoutRounded, precision: 3);
        Assert.All(rects, r => Assert.Equal(0, r.Y, precision: 3));

        // When constrained to 180 DIPs inside an Auto column, Row 2 wraps to Y = 38 (total height 70)
        // and ComputeMeasuredSize with constrainedUnboundedWidth=180 reports Height=70 while keeping Width>=251.2.
        double arrangedNarrowHeight = WrapFlowPanel.ComputeArrangement(
            sizes,
            new Size(180, 32),
            horizontalSpacing: 16,
            verticalSpacing: 6,
            rects);
        Assert.Equal(70, arrangedNarrowHeight, precision: 3);
        Assert.Equal(0, rects[0].Y, precision: 3);
        Assert.Equal(0, rects[1].Y, precision: 3);
        Assert.Equal(38, rects[2].Y, precision: 3);

        Size remeasuredConstrained = WrapFlowPanel.ComputeMeasuredSize(
            sizes,
            availableWidth: double.PositiveInfinity,
            horizontalSpacing: 16,
            verticalSpacing: 6,
            constrainedUnboundedWidth: 180);
        Assert.Equal(70, remeasuredConstrained.Height, precision: 3);
        Assert.True(remeasuredConstrained.Width >= 251.2);
    }

    [Fact]
    public void MultiCheckBoxSettingsCards_AndClipboardHotkeyCallbackIsolation_MatchContracts()
    {
        string repoRoot = FindRepoRoot();
        string clipboardXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "DeskBox", "Views", "SettingsSections", "ClipboardSettingsSection.xaml"));
        string settingsXaml = File.ReadAllText(Path.Combine(repoRoot, "src", "DeskBox", "Views", "SettingsWindow.xaml"));
        string clipboardHotkeyCs = File.ReadAllText(Path.Combine(repoRoot, "src", "DeskBox", "Services", "ClipboardHotkeyService.cs"));

        // CaptureTypesCard uses vertical stretch layout + WrapFlowPanel so wrapped rows are never clipped
        int captureCardIdx = clipboardXaml.IndexOf("x:Name=\"CaptureTypesCard\"", StringComparison.Ordinal);
        Assert.True(captureCardIdx >= 0);
        string captureCardHeader = clipboardXaml.Substring(captureCardIdx, 360);
        Assert.Contains("ContentAlignment=\"Vertical\"", captureCardHeader, StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment=\"Stretch\"", captureCardHeader, StringComparison.Ordinal);

        // QuickCapture VisibleCategories & Todo FooterDisplay also use WrapFlowPanel instead of non-wrapping horizontal StackPanel
        int qcIdx = settingsXaml.IndexOf("Settings.QuickCapture.Tabs.Description", StringComparison.Ordinal);
        Assert.True(qcIdx > 0);
        string qcWindow = settingsXaml.Substring(Math.Max(0, qcIdx - 320), 900);
        Assert.Contains("ContentAlignment=\"Vertical\"", qcWindow, StringComparison.Ordinal);
        Assert.Contains("<controls:WrapFlowPanel", qcWindow, StringComparison.Ordinal);

        int todoFooterIdx = settingsXaml.IndexOf("Settings.Todo.FooterDisplay.Title", StringComparison.Ordinal);
        Assert.True(todoFooterIdx > 0);
        string todoFooterWindow = settingsXaml.Substring(Math.Max(0, todoFooterIdx - 320), 900);
        Assert.Contains("ContentAlignment=\"Vertical\"", todoFooterWindow, StringComparison.Ordinal);
        Assert.Contains("<controls:WrapFlowPanel", todoFooterWindow, StringComparison.Ordinal);

        // ClipboardHotkeyService must escape WindowSubclassProc via PostAfterCallback instead of inline RunOrDefer
        Assert.DoesNotContain("UiDispatch.RunOrDefer", clipboardHotkeyCs, StringComparison.Ordinal);
        Assert.Contains("UiDispatch.PostAfterCallback", clipboardHotkeyCs, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StudioAi_DeepSeekFlashThinkingDisable_ExhaustionAutoRetry_AndArrayContentBlocks_WorkEndToEnd()
    {
        // 1. WriteVendorThinkingParameters sends thinking.type="disabled" for deepseek-flash on both custom and deepseek presets
        var aiCheck = new DeskBoxAiService();
        foreach (string providerId in new[] { CustomWidgetAiGeneratorService.CustomProviderId, "deepseek" })
        {
            ResolvedAiInvocation resolved = aiCheck.ResolveEffectiveSettings(
                providerId,
                endpointOverride: "https://relay.example/v1/chat/completions",
                modelOverride: "deepseek-flash",
                apiKeyOverride: "sk-test",
                thinkingOverride: AiThinkingMode.Disabled);
            byte[] payload = DeskBoxAiService.BuildChatRequestPayload(
                resolved,
                systemPrompt: "sys",
                userPrompt: "usr",
                assistantPrefill: null,
                repairPrompt: null,
                stream: true,
                includeThinkingParams: true,
                isProbe: false,
                includeExplicitStudioMaxTokens: true);
            using JsonDocument doc = JsonDocument.Parse(payload);
            Assert.Equal(16384, doc.RootElement.GetProperty("max_tokens").GetInt32());
            Assert.Equal("disabled", doc.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        }

        // 2. SSE array-typed content blocks + final message fallback + <thinking> inline tags
        var sseContent = new StringBuilder();
        var sseReasoning = new StringBuilder();
        const string arrayDeltaChunk = """
        {"choices":[{"delta":{"content":[{"type":"thinking","thinking":"step 1"},{"type":"text","text":"<thinking>step 2</thinking>{\"ok\":true}"}]},"finish_reason":"stop"}]}
        """;
        Assert.True(DeskBoxAiService.TryAppendSseDelta(arrayDeltaChunk, sseContent, sseReasoning, out string? sseErr, out string? finishReason));
        Assert.Null(sseErr);
        Assert.Equal("stop", finishReason);
        Assert.Equal("step 1", sseReasoning.ToString());
        CustomWidgetAiGeneratorService.SplitInlineThinkBlock(sseContent.ToString(), out string inlineThink, out string visibleJson, out bool unclosed);
        Assert.False(unclosed);
        Assert.Equal("step 2", inlineThink);
        Assert.Equal("{\"ok\":true}", visibleJson);

        // 3. Non-streaming Responses API / array content extraction
        const string responsesPayload = """
        {"output":[{"type":"reasoning","summary":"plan"},{"type":"message","content":[{"type":"output_text","text":"{\"ready\":1}"}]}]}
        """;
        Assert.True(DeskBoxAiService.TryExtractNonStreamingReply(responsesPayload, out string respContent, out string respReason));
        Assert.Equal("{\"ready\":1}", respContent);
        Assert.Equal("plan", respReason);

        // 4. End-to-end Studio turn on deepseek-flash (ThinkingMode.Auto):
        // Attempt 1 exhausts output with reasoning_content only (finish_reason="length", content="") ->
        // DeskBox automatically retries Attempt 2 with thinking.type="disabled" and succeeds!
        string tempDir = Path.Combine(Path.GetTempPath(), $"deskbox-dsflash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var credStore = new CustomWidgetAiCredentialStore(tempDir);
            credStore.Upsert(new AiProviderCredentialEntry(
                ProviderId: CustomWidgetAiGeneratorService.CustomProviderId,
                ApiKey: "sk-test-flash",
                CustomEndpoint: "https://relay.example/v1/chat/completions",
                CustomModel: "deepseek-flash",
                ThinkingMode: "auto"));

            var requestBodies = new List<string>();
            using var httpClient = new HttpClient(new DelegateHttpMessageHandler(async (req, ct) =>
            {
                string reqJson = await req.Content!.ReadAsStringAsync(ct);
                requestBodies.Add(reqJson);

                if (requestBodies.Count == 1)
                {
                    const string exhaustedSse =
                        "data: {\"choices\":[{\"delta\":{\"reasoning_content\":\"Thinking deeply about typing test WPM state...\"},\"finish_reason\":null}]}\n\n" +
                        "data: {\"choices\":[{\"delta\":{},\"finish_reason\":\"length\"}]}\n\n" +
                        "data: [DONE]\n\n";
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(exhaustedSse, Encoding.UTF8, "text/event-stream")
                    };
                }

                const string successSse =
                    "data: {\"choices\":[{\"delta\":{\"content\":\"{\\\"kind\\\":\\\"edit\\\",\\\"reply\\\":\\\"已生成打字速度测试格子\\\",\\\"changeSummary\\\":\\\"新增打字测速\\\",\\\"manifest\\\":{\\\"schemaVersion\\\":1,\\\"id\\\":\\\"deskbox.typing.speed\\\",\\\"name\\\":\\\"打字测速\\\",\\\"icon\\\":\\\"⌨️\\\",\\\"defaultSize\\\":{\\\"width\\\":320,\\\"height\\\":220},\\\"view\\\":{\\\"type\\\":\\\"stack\\\",\\\"children\\\":[{\\\"type\\\":\\\"text\\\",\\\"text\\\":\\\"60 WPM\\\"}]}}}\"},\"finish_reason\":\"stop\"}]}\n\n" +
                    "data: [DONE]\n\n";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(successSse, Encoding.UTF8, "text/event-stream")
                };
            }));

            var aiService = new DeskBoxAiService(credStore, httpClient: httpClient);
            var generator = new CustomWidgetAiGeneratorService(aiService);
            StudioTurnResult result = await generator.RunStudioTurnAsync(
                providerId: CustomWidgetAiGeneratorService.CustomProviderId,
                userMessage: "做一个打字速度测试格子",
                currentManifestJson: null,
                briefMarkdown: string.Empty,
                recentMessages: [],
                healthReport: null,
                targetNodePath: null,
                preferredEmoji: "⌨️",
                allowSampleData: false,
                uiLanguage: "zh-CN");

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(2, requestBodies.Count);

            using JsonDocument req1 = JsonDocument.Parse(requestBodies[0]);
            Assert.False(req1.RootElement.TryGetProperty("thinking", out _));

            using JsonDocument req2 = JsonDocument.Parse(requestBodies[1]);
            Assert.Equal("disabled", req2.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DeskBox.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    private static DirectStartupTaskRegistration CreateTask(string exePath, bool enabled = true) =>
        new(
            exePath,
            DirectStartupTaskBackend.StartupArguments,
            "S-1-5-21-test",
            "S-1-5-21-test",
            "InteractiveToken",
            "LeastPrivilege",
            DirectStartupTaskBackend.InteractiveTaskPriority,
            Enabled: enabled,
            ExecutionTimeLimit: "PT0S",
            MultipleInstancesPolicy: "IgnoreNew",
            StartWhenAvailable: true,
            DisallowStartIfOnBatteries: false,
            StopIfGoingOnBatteries: false,
            RunOnlyIfIdle: false,
            TriggerDelay: string.Empty);

    private sealed class StubTaskBackend : IDirectStartupTaskBackend
    {
        public string LastError => "stub error";
        public bool ReadFailed => false;
        public DirectStartupTaskRegistration? Registration { get; set; }

        public DirectStartupTaskRegistration? Read() => Registration;

        public bool IsPreferred(DirectStartupTaskRegistration registration, string executablePath) =>
            registration.IsOwnedBy(executablePath) && registration.Enabled;

        public bool TryRegister(string executablePath)
        {
            Registration = CreateTask(executablePath);
            return true;
        }

        public bool TryDelete()
        {
            Registration = null;
            return true;
        }

        public bool TryRestore(DirectStartupTaskRegistration registration)
        {
            Registration = registration;
            return true;
        }
    }

    private sealed class StubRunStore : IDirectStartupRunEntryStore
    {
        public string? Value { get; set; }
        public string? Read() => Value;
        public void Write(string commandLine) => Value = commandLine;
        public void Delete() => Value = null;
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
