using System.Diagnostics;
using System.Net;
using System.Text;
using DeskBox.Models;
using DeskBox.Services;
using DeskBox.Services.Studio;

namespace DeskBox.Tests;

public sealed class RegexReDoSAndHttpRedirectHardeningTests
{
    [Fact]
    public void CustomWidgetI18nEngine_AdversarialBackslashChains_CompletesInLinearTime()
    {
        string backslashes = new('\\', 120);
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "redos-i18n",
          "name": "ReDoS I18n",
          "root": "root",
          "elements": {
            "root": {
              "type": "Text",
              "text": "${t('unclosed_key}"
            }
          }
        }
        """;

        string adversarialManifest = manifestJson.Replace(
            "unclosed_key",
            "unclosed_key" + backslashes,
            StringComparison.Ordinal);

        string adversarialLogicJs = $$"""
        export function onInit(ctx) {
          const a = ctx.t('title.key', '{{backslashes}};
          const b = t(ctx, 'sub.key', "{{backslashes}};
          const c = '{{backslashes}}unterminated;
          const d = `{{backslashes}}unterminated;
        }
        """;

        var catalog = new CustomWidgetLocaleCatalog(
            new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["zh-CN"] = new Dictionary<string, string>(StringComparer.Ordinal) { ["title.key"] = "标题" }
            });

        // Warm up JIT once before measuring linear-time completion.
        _ = CustomWidgetI18nEngine.Compile(manifestJson, "export function onInit(ctx) {}", catalog);

        var sw = Stopwatch.StartNew();
        _ = CustomWidgetI18nEngine.Compile(adversarialManifest, adversarialLogicJs, catalog);
        sw.Stop();

        Assert.True(
            sw.ElapsedMilliseconds < 250,
            $"Expected linear-time completion (< 250ms), took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public void ClipboardClassifier_AdversarialUnderscorePayload_CompletesInLinearTime()
    {
        string adversarial = "prefix_" + new string('_', 24_000) + "=not_a_real_secret";

        // Warm up regex once.
        _ = ClipboardClassifier.IsHighConfidenceSecret("sk-proj-1234567890abcdefghijklmnop");

        var sw = Stopwatch.StartNew();
        bool isSecret = ClipboardClassifier.IsHighConfidenceSecret(adversarial);
        string masked = ClipboardClassifier.MaskSecretForDisplay(adversarial);
        sw.Stop();

        Assert.False(isSecret);
        Assert.NotEmpty(masked);
        Assert.True(
            sw.ElapsedMilliseconds < 200,
            $"Expected linear-time secret scan (< 200ms), took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public void ScriptValidatorAndStudioTransport_AdversarialPatterns_CompleteInLinearTime()
    {
        const string manifestJson = """
        {
          "schemaVersion": 1,
          "id": "redos-script",
          "name": "ReDoS Script",
          "script": { "entry": "logic.js" },
          "settings": [
            { "key": "s1", "type": "text", "label": "S1" },
            { "key": "s2", "type": "text", "label": "S2" },
            { "key": "s3", "type": "text", "label": "S3" },
            { "key": "s4", "type": "text", "label": "S4" },
            { "key": "s5", "type": "text", "label": "S5" },
            { "key": "s6", "type": "text", "label": "S6" },
            { "key": "s7", "type": "text", "label": "S7" },
            { "key": "s8", "type": "text", "label": "S8" },
            { "key": "s9", "type": "text", "label": "S9" },
            { "key": "s10", "type": "text", "label": "S10" },
            { "key": "s11", "type": "text", "label": "S11" },
            { "key": "s12", "type": "text", "label": "S12" },
            { "key": "s13", "type": "text", "label": "S13" },
            { "key": "s14", "type": "text", "label": "S14" },
            { "key": "s15", "type": "text", "label": "S15" },
            { "key": "s16", "type": "text", "label": "S16" },
            { "key": "s17", "type": "text", "label": "S17" },
            { "key": "s18", "type": "text", "label": "S18" }
          ],
          "root": "root",
          "elements": {
            "root": {
              "type": "Button",
              "label": "Run",
              "onClick": [
                { "type": "script.call", "fn": "handleRun", "args": { "itemId": "1" } }
              ]
            }
          }
        }
        """;

        string spaces = new(' ', 2_000);
        string adversarialJs =
            "export function handleRun(ctx) {\n" +
            "  const cfg = ctx.settings;\n" +
            $"  const {{{spaces}a,{spaces}b{spaces}}} = notArgs;\n" +
            "}";

        string conflictNoise = string.Concat(Enumerable.Repeat("<<<<<<< SEARCH\r\nbody line\r\n=======\r\n", 40));
        string fencedInput = $"```deskbox-script-patch\n{conflictNoise}\n```";

        // Warm up once.
        _ = CustomWidgetScriptValidator.ValidateWorkspaceBundle(manifestJson, "export function handleRun(ctx) {}", runOnInitDryRun: false);
        _ = StudioToolTransport.TryExtractBlockToolCalls("```deskbox-script-patch\n```", out _);

        var sw = Stopwatch.StartNew();
        _ = CustomWidgetScriptValidator.ValidateWorkspaceBundle(manifestJson, adversarialJs, runOnInitDryRun: false);
        bool extracted = StudioToolTransport.TryExtractBlockToolCalls(fencedInput, out _);
        sw.Stop();

        Assert.False(extracted);
        Assert.True(
            sw.ElapsedMilliseconds < 300,
            $"Expected non-backtracking linear validation (< 300ms), took {sw.ElapsedMilliseconds}ms.");
    }

    [Fact]
    public async Task SameOriginAuthSafeRedirectHandler_PreservesSameOriginAuth_AndStripsCrossOriginApiKeys()
    {
        var captured = new List<(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string? Body)>();
        var inner = new RecordingInnerHandler(async (req, ct) =>
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, IEnumerable<string>> h in req.Headers)
            {
                headers[h.Key] = string.Join(",", h.Value);
            }

            string? body = req.Content is not null ? await req.Content.ReadAsStringAsync(ct) : null;
            captured.Add((req.Method, req.RequestUri!, headers, body));

            if (string.Equals(req.RequestUri!.AbsolutePath, "/v1/same-origin-hop", StringComparison.Ordinal))
            {
                var sameHop = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                sameHop.Headers.Location = new Uri("https://relay.example.com/v1/chat/completions");
                return sameHop;
            }

            if (string.Equals(req.RequestUri.AbsolutePath, "/v1/cross-origin-hop", StringComparison.Ordinal))
            {
                var crossHop = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                crossHop.Headers.Location = new Uri("https://attacker.example.net/collect");
                return crossHop;
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(new DeskBoxAiService.SameOriginAuthSafeRedirectHandler(inner));

        // 1. Same-origin 307 redirect preserves POST, body, Authorization, and custom x-api-key.
        using (var sameReq = new HttpRequestMessage(HttpMethod.Post, "https://relay.example.com/v1/same-origin-hop")
        {
            Content = new StringContent("{\"model\":\"demo\"}", Encoding.UTF8, "application/json")
        })
        {
            sameReq.Headers.TryAddWithoutValidation("Authorization", "Bearer sk-secret-1");
            sameReq.Headers.TryAddWithoutValidation("x-api-key", "sk-anthropic-secret");

            using HttpResponseMessage sameResp = await client.SendAsync(sameReq);
            Assert.Equal(HttpStatusCode.OK, sameResp.StatusCode);
        }

        Assert.Equal(2, captured.Count);
        Assert.Equal(HttpMethod.Post, captured[1].Method);
        Assert.Equal("{\"model\":\"demo\"}", captured[1].Body);
        Assert.Equal("Bearer sk-secret-1", captured[1].Headers["Authorization"]);
        Assert.Equal("sk-anthropic-secret", captured[1].Headers["x-api-key"]);

        captured.Clear();

        // 2. Cross-origin 307 redirect strips Authorization, x-api-key, api-key, x-goog-api-key AND drops POST body.
        using (var crossReq = new HttpRequestMessage(HttpMethod.Post, "https://relay.example.com/v1/cross-origin-hop")
        {
            Content = new StringContent("{\"prompt\":\"private\"}", Encoding.UTF8, "application/json")
        })
        {
            crossReq.Headers.TryAddWithoutValidation("Authorization", "Bearer sk-secret-2");
            crossReq.Headers.TryAddWithoutValidation("x-api-key", "sk-anthropic-secret-2");
            crossReq.Headers.TryAddWithoutValidation("api-key", "azure-secret-2");
            crossReq.Headers.TryAddWithoutValidation("x-goog-api-key", "gemini-secret-2");
            crossReq.Headers.TryAddWithoutValidation("X-Client-Trace", "keep-benign-header");

            using HttpResponseMessage crossResp = await client.SendAsync(crossReq);
            Assert.Equal(HttpStatusCode.OK, crossResp.StatusCode);
        }

        Assert.Equal(2, captured.Count);
        Assert.Equal(HttpMethod.Get, captured[1].Method);
        Assert.Null(captured[1].Body);
        Assert.False(captured[1].Headers.ContainsKey("Authorization"));
        Assert.False(captured[1].Headers.ContainsKey("x-api-key"));
        Assert.False(captured[1].Headers.ContainsKey("api-key"));
        Assert.False(captured[1].Headers.ContainsKey("x-goog-api-key"));
        Assert.Equal("keep-benign-header", captured[1].Headers["X-Client-Trace"]);
    }

    private sealed class RecordingInnerHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request, cancellationToken);
    }
}
