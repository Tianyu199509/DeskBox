using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DeskBox.Controls.WidgetContents;
using DeskBox.Models;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

/// <summary>
/// Task ⑦ (deskbox.dev-toolbox) coverage: offline JWT decode with exp/nbf diagnostics,
/// Unicode/JSON escape round-trips (CJK + emoji surrogate pairs), Base64 tolerance,
/// second-vs-millisecond timestamp disambiguation, UUID formats & case,
/// clipboard sniffer kind detection, the <c>${fn(...)}</c> expression bridge,
/// <c>$transform.jwtState</c>, and full validation of the official template manifest.
/// All operations are 100% in-memory — zero network, zero reflection.
/// </summary>
public sealed class CustomWidgetDevToolboxTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 11, 4, 8, 0, 0, TimeSpan.Zero);

    private static string ToBase64Url(string json)
    {
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        return b64.TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string BuildJwt(string payloadJson, string headerJson = "{\"alg\":\"HS256\",\"typ\":\"JWT\"}") =>
        $"{ToBase64Url(headerJson)}.{ToBase64Url(payloadJson)}.sig-n0t-ver1fied";

    // ─────────────── jwtdecode: exp past / future / missing / invalid ───────────────

    [Fact]
    public void JwtDecode_FutureExp_ReportsValidWithCountdown()
    {
        long exp = FixedNow.ToUnixTimeSeconds() + (2 * 3600 + 15 * 60); // 2h 15m left
        string jwt = BuildJwt($"{{\"sub\":\"u123\",\"exp\":{exp}}}");

        CustomWidgetTransformResult tr = CustomWidgetUtilityEngine.ExecuteTextTransform(
            jwt, "jwtDecode", FixedNow, "zh-CN");

        Assert.True(tr.Succeeded, tr.ErrorMessage);
        Assert.Equal("jwtdecode", tr.Operation);
        Assert.Equal("valid", tr.JwtState);
        Assert.Equal("valid", tr.ResolveToken("jwtState"));
        Assert.Equal("valid", tr.ResolveToken("jwtStatus"));
        Assert.Contains("✅ 未过期 · 剩余 2 小时 15 分", tr.ResultText, StringComparison.Ordinal);
        Assert.Contains("exp:", tr.ResultText, StringComparison.Ordinal);
        Assert.Contains("仅解码 · 未验证签名", tr.ResultText, StringComparison.Ordinal);
        Assert.Contains("HEADER:", tr.ResultText, StringComparison.Ordinal);
        Assert.Contains("PAYLOAD:", tr.ResultText, StringComparison.Ordinal);
        Assert.Contains("\"alg\": \"HS256\"", tr.ResultText, StringComparison.Ordinal);
        Assert.Contains("\"sub\": \"u123\"", tr.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public void JwtDecode_ExpiredToken_ReportsElapsedDaysAndWarningState()
    {
        long exp = FixedNow.ToUnixTimeSeconds() - (3 * 24 * 3600); // expired 3 days ago
        string jwt = BuildJwt($"{{\"sub\":\"u123\",\"exp\":{exp}}}");

        CustomWidgetTransformResult tr = CustomWidgetUtilityEngine.ExecuteTextTransform(
            jwt, "jwtDecode", FixedNow, "zh-CN");

        Assert.True(tr.Succeeded);
        Assert.Equal("expired", tr.JwtState);
        Assert.Contains("⚠️ 已过期 3 天", tr.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public void JwtDecode_NearlyExpiredToken_UsesHourMinuteGranularity()
    {
        long exp = FixedNow.ToUnixTimeSeconds() - 23 * 3600; // expired 23h ago
        string jwt = BuildJwt($"{{\"exp\":{exp}}}");

        CustomWidgetTransformResult tr = CustomWidgetUtilityEngine.ExecuteTextTransform(
            jwt, "jwtDecode", FixedNow, "en-US");

        Assert.True(tr.Succeeded);
        Assert.Equal("expired", tr.JwtState);
        Assert.Contains("Expired 23h ago", tr.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public void JwtDecode_MissingExp_DegradesToNoExpState()
    {
        string jwt = BuildJwt("{\"sub\":\"u123\",\"iat\":1761900000}");

        CustomWidgetTransformResult tr = CustomWidgetUtilityEngine.ExecuteTextTransform(
            jwt, "jwtDecode", FixedNow, "zh-CN");

        Assert.True(tr.Succeeded);
        Assert.Equal("noexp", tr.JwtState);
        Assert.Contains("无 exp 声明", tr.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public void JwtDecode_FutureNbf_IsCalledOutInStatusLine()
    {
        long nbf = FixedNow.ToUnixTimeSeconds() + 3600;
        long exp = FixedNow.ToUnixTimeSeconds() + 7200;
        string jwt = BuildJwt($"{{\"nbf\":{nbf},\"exp\":{exp}}}");

        CustomWidgetTransformResult tr = CustomWidgetUtilityEngine.ExecuteTextTransform(
            jwt, "jwtDecode", FixedNow, "zh-CN");

        Assert.True(tr.Succeeded);
        Assert.Equal("valid", tr.JwtState);
        Assert.Contains("nbf 未生效", tr.ResultText, StringComparison.Ordinal);
    }

    [Fact]
    public void JwtDecode_InvalidInputs_FailGracefullyWithInvalidState()
    {
        foreach (string bad in new[] { "not-a-jwt", "eyJ.eyJ.sig", "a.b.c.d.e", "////.////" })
        {
            CustomWidgetTransformResult tr = CustomWidgetUtilityEngine.ExecuteTextTransform(
                bad, "jwtDecode", FixedNow, "zh-CN");
            Assert.False(tr.Succeeded, bad);
            Assert.Equal("invalid", tr.JwtState);
            Assert.Contains("JWT", tr.ErrorMessage, StringComparison.Ordinal);
        }
    }

    // ─────────────── Unicode escape round-trips (CJK + emoji surrogates) ───────────────

    [Fact]
    public void UnicodeEscape_RoundTripsChineseAndEmojiAndPreservesAscii()
    {
        const string source = "中文 deskbox 🧰 ok";

        string escaped = CustomWidgetUtilityEngine.ExecuteTextTransform(source, "unicodeEscape").ResultText;
        Assert.Equal(@"\u4e2d\u6587 deskbox \ud83e\uddf0 ok", escaped);

        string restored = CustomWidgetUtilityEngine.ExecuteTextTransform(escaped, "unicodeUnescape").ResultText;
        Assert.Equal(source, restored);
    }

    [Fact]
    public void UnicodeUnescape_DecodesSurrogatePairsAndKeepsUnknownEscapesVerbatim()
    {
        string restored = CustomWidgetUtilityEngine.ExecuteTextTransform(
            @"\u8bf7\u6c42\u5931\u8d25 \uD83E\uDDF0 C:\users\neither", "unicodeUnescape").ResultText;
        Assert.Equal("请求失败 🧰 C:\\users\\neither", restored);
    }

    [Fact]
    public void UnicodeUnescape_ToleratesMalformedHexSequences()
    {
        // \u12 stops early (no full quad) and must stay literal, not crash.
        string restored = CustomWidgetUtilityEngine.ExecuteTextTransform(
            "keep \\u12 and \\u4e2d", "unicodeUnescape").ResultText;
        Assert.Equal("keep \\u12 and 中", restored);
    }

    // ─────────────── JSON string escape / unescape ───────────────

    [Fact]
    public void JsonEscapeAndUnescape_RoundTripQuotesBackslashAndControlChars()
    {
        const string raw = "a\"b\\c\nd\te 中文";

        string escaped = CustomWidgetUtilityEngine.ExecuteTextTransform(raw, "jsonEscape").ResultText;
        Assert.Equal("a\\\"b\\\\c\\nd\\te 中文", escaped);
        Assert.DoesNotContain("\\u", escaped, StringComparison.Ordinal); // non-ASCII stays UTF-8

        string restored = CustomWidgetUtilityEngine.ExecuteTextTransform(escaped, "jsonUnescape").ResultText;
        Assert.Equal(raw, restored);

        string fromQuotedLiteral = CustomWidgetUtilityEngine.ExecuteTextTransform(
            "\"" + escaped + "\"", "jsonUnescape").ResultText;
        Assert.Equal(raw, fromQuotedLiteral);
    }

    [Fact]
    public void JsonUnescape_DecodesUnicodeEscapesInsideJsonString()
    {
        string restored = CustomWidgetUtilityEngine.ExecuteTextTransform(
            "\"\\u8bf7\\u6c42\\u5931\\u8d25\"", "jsonUnescape").ResultText;
        Assert.Equal("请求失败", restored);
    }

    // ─────────────── Base64 tolerance + degraded errors ───────────────

    [Fact]
    public void Base64Decode_AcceptsUrlSafeAndUnpaddedInput()
    {
        string urlSafe = ToBase64Url("{\"ok\":true}");
        string decoded = CustomWidgetUtilityEngine.ExecuteTextTransform(urlSafe, "base64Decode").ResultText;
        Assert.Equal("{\"ok\":true}", decoded);
    }

    [Fact]
    public void Base64Decode_InvalidInput_ReturnsDegradedErrorMessage()
    {
        CustomWidgetTransformResult tr = CustomWidgetUtilityEngine.ExecuteTextTransform("!!!not-base64!!!", "base64Decode");
        Assert.False(tr.Succeeded);
        Assert.Contains("Invalid Base64 input", tr.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void JsonPretty_InvalidJson_ReturnsDegradedFailure()
    {
        CustomWidgetTransformResult tr = CustomWidgetUtilityEngine.ExecuteTextTransform("{\"a\":1,", "jsonPretty");
        Assert.False(tr.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(tr.ErrorMessage));
    }

    [Fact]
    public void JsonPretty4_UsesFourSpaceIndent()
    {
        CustomWidgetTransformResult pretty4 = CustomWidgetUtilityEngine.ExecuteTextTransform(
            "{\"a\":{\"b\":1}}", "jsonPretty4");
        Assert.True(pretty4.Succeeded);
        Assert.Contains("\n    \"a\"", pretty4.ResultText, StringComparison.Ordinal);
        Assert.Contains("\n        \"b\"", pretty4.ResultText, StringComparison.Ordinal);

        CustomWidgetTransformResult pretty2 = CustomWidgetUtilityEngine.ExecuteTextTransform(
            "{\"a\":{\"b\":1}}", "jsonPretty");
        Assert.Contains("\n  \"a\"", pretty2.ResultText, StringComparison.Ordinal);
    }

    // ─────────────── Timestamp seconds vs milliseconds + reverse direction ───────────────

    [Fact]
    public void TimestampToDate_DisambiguatesSecondsAndMillisecondsByMagnitude()
    {
        string expectedLocal = DateTimeOffset.FromUnixTimeSeconds(1700000000)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        CustomWidgetTransformResult seconds = CustomWidgetUtilityEngine.ExecuteTextTransform(
            "1700000000", "timestampToDate");
        Assert.True(seconds.Succeeded);
        Assert.Equal(expectedLocal, seconds.ResultText);

        CustomWidgetTransformResult millis = CustomWidgetUtilityEngine.ExecuteTextTransform(
            "1700000000000", "timestampToDate");
        Assert.True(millis.Succeeded);
        Assert.Equal(expectedLocal, millis.ResultText);
    }

    [Fact]
    public void DateToTimestamp_RoundTripsLocalDateAndNowInBothPrecisions()
    {
        var now = new DateTimeOffset(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
        string seconds = CustomWidgetUtilityEngine.ExecuteTextTransform("now", "dateToTimestamp", now).ResultText;
        string millis = CustomWidgetUtilityEngine.ExecuteTextTransform("now", "dateToTimestampMs", now).ResultText;

        Assert.Equal(now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), seconds);
        Assert.Equal(now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), millis);
        Assert.Equal(10, seconds.Length);
        Assert.Equal(13, millis.Length);

        string parsedBack = CustomWidgetUtilityEngine.ExecuteTextTransform(millis, "timestampToDate", now).ResultText;
        Assert.Equal(CustomWidgetUtilityEngine.ExecuteTextTransform(seconds, "timestampToDate", now).ResultText, parsedBack);

        CustomWidgetTransformResult bad = CustomWidgetUtilityEngine.ExecuteTextTransform("not-a-date", "dateToTimestamp");
        Assert.False(bad.Succeeded);
    }

    // ─────────────── UUID formats & case ───────────────

    [Fact]
    public void UuidOps_ProduceCanonicalDashNoDashAndCaseVariants()
    {
        CustomWidgetTransformResult uuid = CustomWidgetUtilityEngine.ExecuteTextTransform("", "uuid");
        Assert.True(uuid.Succeeded);
        Assert.Equal(36, uuid.ResultText.Length);
        Assert.Equal(4, uuid.ResultText.Count(c => c == '-'));
        Assert.Equal(uuid.ResultText.ToLowerInvariant(), uuid.ResultText);

        Assert.Matches("^[0-9a-f]{32}$",
            CustomWidgetUtilityEngine.ExecuteTextTransform("", "uuidNoDash").ResultText);
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{4}-[0-9A-F]{12}$",
            CustomWidgetUtilityEngine.ExecuteTextTransform("", "uuidUpper").ResultText);
        Assert.Matches("^[0-9A-F]{32}$",
            CustomWidgetUtilityEngine.ExecuteTextTransform("", "uuidNoDashUpper").ResultText);
    }

    [Fact]
    public void NormalizeTextOp_MapsDeveloperAliases()
    {
        Assert.Equal("jwtdecode", CustomWidgetUtilityEngine.NormalizeTextOp("JWT"));
        Assert.Equal("jwtdecode", CustomWidgetUtilityEngine.NormalizeTextOp("decodeJwt"));
        Assert.Equal("unicodeunescape", CustomWidgetUtilityEngine.NormalizeTextOp("unicodeToZh"));
        Assert.Equal("unicodeescape", CustomWidgetUtilityEngine.NormalizeTextOp("textToUnicode"));
        Assert.Equal("uuid", CustomWidgetUtilityEngine.NormalizeTextOp("uuid4"));
        Assert.Equal("uuidnodash", CustomWidgetUtilityEngine.NormalizeTextOp("uuid-no-dash"));
        Assert.Equal("jsonpretty4", CustomWidgetUtilityEngine.NormalizeTextOp("prettyJson4"));
        Assert.Equal("datetotimestampms", CustomWidgetUtilityEngine.NormalizeTextOp("nowMs"));

        foreach (string op in new[]
                 {
                     "jwtdecode", "unicodeunescape", "unicodeescape", "jsonescape", "jsonunescape",
                     "jsonpretty4", "datetotimestampms", "uuidnodash", "uuidupper", "uuidnodashupper"
                 })
        {
            Assert.True(CustomWidgetUtilityEngine.IsSupportedTextTransform(op), op);
        }
    }

    // ─────────────── Clipboard sniffer kind detection ───────────────

    [Fact]
    public void ClassifyClipboardText_DetectsJwtUnicodeUrlEncodedAndBase64()
    {
        long exp = FixedNow.ToUnixTimeSeconds() + 600;
        Assert.Equal("jwt", CustomWidgetClipboardSnapshot.CreateFromText(
            BuildJwt($"{{\"sub\":\"u\",\"exp\":{exp}}}")).Kind);

        Assert.Equal("unicodeEscape", CustomWidgetClipboardSnapshot.CreateFromText(
            @"\u8bf7\u6c42\u5931\u8d25").Kind);
        Assert.Equal("urlEncoded", CustomWidgetClipboardSnapshot.CreateFromText(
            "%E4%B8%AD%E6%96%87%E6%8A%A5%E6%96%87").Kind);
        Assert.Equal("base64", CustomWidgetClipboardSnapshot.CreateFromText(
            Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"token\":\"eyJhbGciOi.demo.payload.value\"}"))).Kind);

        // Prior kinds keep working
        Assert.Equal("json", CustomWidgetClipboardSnapshot.CreateFromText("{\"a\":1}").Kind);
        Assert.Equal("timestamp", CustomWidgetClipboardSnapshot.CreateFromText("1762236000").Kind);
        Assert.Equal("url", CustomWidgetClipboardSnapshot.CreateFromText("https://deskbox.fun/widget").Kind);
        Assert.Equal("hexColor", CustomWidgetClipboardSnapshot.CreateFromText("#3B82F6").Kind);

        // Plain words must never be misflagged as base64 (no symbols, too short)
        Assert.Equal("text", CustomWidgetClipboardSnapshot.CreateFromText("hello world clipboard").Kind);
        Assert.Equal("number", CustomWidgetClipboardSnapshot.CreateFromText("3.14").Kind);
    }

    [Fact]
    public void KindLabel_ResolvesNewKindsBilingually()
    {
        var snap = CustomWidgetClipboardSnapshot.CreateFromText(BuildJwt("{\"exp\":9999999999}"));
        Assert.Equal("JWT 令牌", snap.ResolveToken("kindLabel", "zh-CN"));
        Assert.Equal("JWT Token", snap.ResolveToken("kindLabel", "en-US"));
        Assert.Equal("Unicode 转义", CustomWidgetClipboardSnapshot.CreateFromText("\\u4e2d")
            .ResolveToken("kindLabel", "zh-CN"));
    }

    // ─────────────── ${fn(...)} expression bridge ───────────────

    [Fact]
    public void ExpressionEvaluator_CallsOfflineUtilityFunctionsInline()
    {
        var ctx = new CustomWidgetEvaluationContext
        {
            ClockOverride = FixedNow,
            CultureName = "zh-CN"
        };

        Assert.Equal("中文", CustomWidgetExpressionEvaluator.Interpolate(
            "${unicodeunescape('\\u4e2d\\u6587')}", ctx));
        Assert.Equal(@"\u4e2d\u6587", CustomWidgetExpressionEvaluator.Interpolate(
            "${unicodeescape('中文')}", ctx));
        Assert.Equal("a\\\"b", CustomWidgetExpressionEvaluator.Interpolate(
            "${jsonescape('a\"b')}", ctx));

        long exp = FixedNow.ToUnixTimeSeconds() - 3600;
        string jwt = BuildJwt($"{{\"sub\":\"u\",\"exp\":{exp}}}");
        string decoded = CustomWidgetExpressionEvaluator.Interpolate(
            $"${{jwtdecode('{jwt}')}}", ctx);
        Assert.Contains("已过期", decoded, StringComparison.Ordinal);
        Assert.Contains("\"sub\": \"u\"", decoded, StringComparison.Ordinal);

        Assert.Matches("^[0-9a-f-]{36}$", CustomWidgetExpressionEvaluator.Interpolate("${uuid()}", ctx));
        Assert.Matches("^[0-9A-F-]{36}$", CustomWidgetExpressionEvaluator.Interpolate("${uuid('upper')}", ctx));
        Assert.Matches("^[0-9a-f]{32}$", CustomWidgetExpressionEvaluator.Interpolate("${uuidnodash()}", ctx));

        string b64 = CustomWidgetExpressionEvaluator.Interpolate("${base64encode('DeskBox')}", ctx);
        Assert.Equal("DeskBox", CustomWidgetExpressionEvaluator.Interpolate(
            $"${{base64decode('{b64}')}}", ctx));

        string expectedLocal = DateTimeOffset.FromUnixTimeSeconds(1700000000)
            .ToLocalTime()
            .ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        Assert.Equal(expectedLocal, CustomWidgetExpressionEvaluator.Interpolate(
            "${timestamptodate(1700000000)}", ctx));
        Assert.Equal(
            FixedNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            CustomWidgetExpressionEvaluator.Interpolate("${datetotimestampms()}", ctx));
    }

    [Fact]
    public void ExpressionEvaluator_UtilityFunctionFailuresAndUnknownsReturnEmpty()
    {
        var ctx = new CustomWidgetEvaluationContext { CultureName = "en-US" };

        // Invalid base64 fails -> empty string, never a raw exception message.
        Assert.Equal(string.Empty, CustomWidgetExpressionEvaluator.Interpolate(
            "${base64decode('%%%')}", ctx));
        // Unknown function names are not expressions -> empty fallback.
        Assert.Equal(string.Empty, CustomWidgetExpressionEvaluator.Interpolate(
            "${definitelynotarealfunction('x')}", ctx));
        // State token reads keep working (no shadowing).
        var stateCtx = new CustomWidgetEvaluationContext
        {
            CultureName = "en-US",
            State = new Dictionary<string, string> { ["jwtdecode"] = "state-value" }
        };
        Assert.Equal("state-value", CustomWidgetExpressionEvaluator.Interpolate("${state.jwtdecode}", stateCtx));
    }

    [Fact]
    public void CapabilityRegistry_AcceptsJwtStateTokenAndKeeps27Modules()
    {
        Assert.Equal(37, CustomWidgetCapabilityRegistry.All.Count);

        var diagnostics = new List<CustomWidgetValidationDiagnostic>();
        CustomWidgetCapabilityRegistry.ValidateBuiltInTokensInExpression(
            "${$transform.jwtState} / ${$transform.status} / ${$clipboard.kindLabel}",
            "$.test",
            diagnostics);
        Assert.Empty(diagnostics.Where(d => d.Code == "UnknownBuiltInToken"));

        CustomWidgetCapabilityRegistry.ValidateBuiltInTokensInExpression(
            "${$transform.jwtValidityState}", "$.test", diagnostics);
        Assert.Contains(diagnostics, d => d.Code == "UnknownBuiltInToken");
    }

    // ─────────────── Official template manifest: zero-warning validation ───────────────

    [Fact]
    public void DevToolboxManifest_ParsesWithZeroErrorsAndZeroWarnings()
    {
        CustomWidgetTemplateBundle bundle =
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.DevToolboxTemplateId);
        Assert.False(bundle.Locales.IsEmpty);

        CustomWidgetManifest rawParsed = CustomWidgetManifestParser.Parse(bundle.ManifestJson).Manifest!;
        Assert.Empty(rawParsed.I18n);

        CustomWidgetParseResult result = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            bundle.ManifestJson,
            bundle.LogicJs,
            bundle.Locales);

        Assert.NotNull(result.Manifest);
        Assert.Empty(result.Diagnostics);

        CustomWidgetManifest manifest = result.Manifest!;
        Assert.Equal("deskbox.dev-toolbox", manifest.Id);
        Assert.Equal("🛠️", manifest.Icon);
        Assert.Equal("🛠️", manifest.Capsule?.Glyph);
        Assert.Equal(344, manifest.DefaultSize.Width);
        Assert.Equal(356, manifest.DefaultSize.Height);
        Assert.Equal(288, manifest.MinSize.Width);
        Assert.Equal(280, manifest.MinSize.Height);

        // 13-language i18n with identical key sets
        string[] expectedLocales =
        [
            "ar-SA", "bn-BD", "de-DE", "en-US", "es-ES", "fr-FR", "hi-IN",
            "ja-JP", "ko-KR", "pt-BR", "ru-RU", "zh-CN", "zh-TW"
        ];
        Assert.Equal(
            expectedLocales.OrderBy(l => l, StringComparer.Ordinal),
            manifest.I18n.Keys.OrderBy(l => l, StringComparer.Ordinal));
        IReadOnlyDictionary<string, string> en = manifest.I18n["en-US"];
        foreach ((string locale, IReadOnlyDictionary<string, string> dict) in manifest.I18n)
        {
            Assert.Equal(en.Keys.OrderBy(k => k, StringComparer.Ordinal), dict.Keys.OrderBy(k => k, StringComparer.Ordinal));
            Assert.All(dict.Values, v => Assert.False(string.IsNullOrWhiteSpace(v)));
        }

        // Clipboard sniffer source declared; all 4 settings keys present
        Assert.Contains(manifest.Sources, s => s.Type == "clipboard");
        Assert.Equal(
            new[] { "autoCopyResult", "enableClipboardSniffer", "jsonIndent", "uuidCase" },
            manifest.Settings.Select(s => s.Key).ToArray());
    }

    private static (CustomWidgetScriptExecutionResult Exec, CustomWidgetEvaluationContext Ctx) ExecuteDevToolboxScript(
        CustomWidgetCompiledScript compiled,
        string fn,
        Dictionary<string, string> state,
        Dictionary<string, string> settings,
        IReadOnlyDictionary<string, string> i18n,
        CustomWidgetClipboardSnapshot clipboard,
        IReadOnlyDictionary<string, string>? args = null,
        DateTimeOffset? now = null,
        string culture = "zh-CN")
    {
        CustomWidgetScriptExecutionResult exec = CustomWidgetScriptEngine.Execute(
            compiled,
            fn,
            new CustomWidgetScriptExecutionInput
            {
                ClockOverride = now ?? FixedNow,
                CultureName = culture,
                I18n = i18n,
                State = state,
                Settings = settings,
                Clipboard = clipboard,
                Args = args,
                AllowCopy = true
            });
        Assert.True(exec.IsSuccess, exec.ErrorMessage);

        foreach (string removed in exec.StateRemoved)
        {
            state.Remove(removed);
        }

        foreach ((string key, string val) in exec.StateSet)
        {
            state[key] = val;
        }

        var evalCtx = new CustomWidgetEvaluationContext
        {
            ClockOverride = now ?? FixedNow,
            CultureName = culture,
            I18n = i18n,
            State = state,
            Settings = settings,
            Clipboard = clipboard,
            Computed = exec.Computed
        };
        return (exec, evalCtx);
    }

    private static Dictionary<string, string> CreateDevToolboxState(string draft = "", string tab = "json") =>
        new(StringComparer.Ordinal)
        {
            ["draft"] = draft,
            ["output"] = "",
            ["activeTab"] = tab,
            ["editing"] = "false",
            ["focusSeq"] = "0",
            ["stepLabel"] = "",
            ["historyJson"] = "[]",
            ["jwtMode"] = "false",
            ["jwtView"] = "payload",
            ["jwtToken"] = "",
            ["jwtHeaderJson"] = "",
            ["jwtPayloadJson"] = "",
            ["structKind"] = "",
            ["kvUnit"] = "s",
            ["lastSelfCopied"] = "",
            ["dismissedClip"] = "",
            ["copySeq"] = "0",
            ["errorMsg"] = "",
            ["errorOffset"] = "-1",
            ["errorSeq"] = "0",
            ["autoCopyOverride"] = ""
        };

    [Fact]
    public void DevToolboxManifest_KeyExpressions_ResolveBySnifferKindAndJwtState()
    {
        CustomWidgetTemplateBundle bundle =
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.DevToolboxTemplateId);
        CustomWidgetWorkspaceValidationResult validated = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            bundle.ManifestJson,
            bundle.LogicJs,
            bundle.Locales);
        Assert.NotNull(validated.Manifest);
        Assert.NotNull(validated.CompiledScript);
        CustomWidgetManifest manifest = validated.Manifest!;
        CustomWidgetCompiledScript compiled = validated.CompiledScript!;
        IReadOnlyDictionary<string, string> zhI18n = manifest.ResolveI18nDictionary("zh-CN");

        long validExp = FixedNow.ToUnixTimeSeconds() + 3600;
        CustomWidgetClipboardSnapshot jwtSnap = CustomWidgetClipboardSnapshot.CreateFromText(
            BuildJwt($"{{\"sub\":\"u\",\"exp\":{validExp}}}"));

        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["autoCopyResult"] = "true",
            ["enableClipboardSniffer"] = "true",
            ["jsonIndent"] = "2",
            ["uuidCase"] = "lower"
        };

        // 1. Structured JWT clipboard shows compact handoff strip with "载入并解析"
        Dictionary<string, string> state = CreateDevToolboxState();
        (_, CustomWidgetEvaluationContext ctx) = ExecuteDevToolboxScript(
            compiled, "onInit", state, settings, zhI18n, jwtSnap);

        Assert.Equal("true", CustomWidgetExpressionEvaluator.Interpolate("${$computed.showClipBanner}", ctx));
        Assert.Equal("JWT", CustomWidgetExpressionEvaluator.Interpolate("${$computed.clipKindLabel}", ctx));
        CustomWidgetNodeSpec clipCard = manifest.View.Children[0];
        Assert.Equal("clip-accordion", clipCard.Id);
        CustomWidgetNodeSpec clipActionBtn = clipCard.Children[0].Children[2];
        Assert.Equal("Button", clipActionBtn.Type);
        Assert.Equal("载入并解析", CustomWidgetExpressionEvaluator.Interpolate(clipActionBtn.Text!, ctx));

        // 2. Plain text clipboard stays non-intrusive ("粘贴" only, never auto-formats or errors)
        CustomWidgetClipboardSnapshot plainSnap = CustomWidgetClipboardSnapshot.CreateFromText("hello plain note");
        (_, ctx) = ExecuteDevToolboxScript(compiled, "onInit", state, settings, zhI18n, plainSnap);
        Assert.Equal("纯文本", CustomWidgetExpressionEvaluator.Interpolate("${$computed.clipKindLabel}", ctx));
        Assert.Equal("粘贴", CustomWidgetExpressionEvaluator.Interpolate(clipActionBtn.Text!, ctx));

        (_, ctx) = ExecuteDevToolboxScript(compiled, "loadClipboard", state, settings, zhI18n, plainSnap);
        Assert.Equal("hello plain note", state["draft"]);
        Assert.Equal(string.Empty, state["errorMsg"]);
        Assert.Equal("false", CustomWidgetExpressionEvaluator.Interpolate("${$computed.showClipBanner}", ctx));

        // 3. Self-copy loop protection: clipboard matching lastSelfCopied stays suppressed
        state = CreateDevToolboxState();
        state["lastSelfCopied"] = jwtSnap.Text;
        (_, ctx) = ExecuteDevToolboxScript(compiled, "onInit", state, settings, zhI18n, jwtSnap);
        Assert.Equal("false", CustomWidgetExpressionEvaluator.Interpolate("${$computed.showClipBanner}", ctx));

        // 4. Sniffer disabled -> hides banner and switches empty capsule to idle UUID v7 generator
        settings["enableClipboardSniffer"] = "false";
        settings["jsonIndent"] = "4";
        settings["uuidCase"] = "upper";
        state = CreateDevToolboxState();
        (_, ctx) = ExecuteDevToolboxScript(compiled, "onInit", state, settings, zhI18n, jwtSnap);
        Assert.Equal("false", CustomWidgetExpressionEvaluator.Interpolate("${$computed.showClipBanner}", ctx));
        Assert.Equal("UUID v7", CustomWidgetExpressionEvaluator.Interpolate(manifest.Capsule!.Badge, ctx));
        Assert.Equal("空闲 · 生成并复制 UUID v7", CustomWidgetExpressionEvaluator.Interpolate(manifest.Capsule.Summary, ctx));
        Assert.Equal("false", CustomWidgetExpressionEvaluator.Interpolate("${$computed.hasDraft}", ctx));

        // 5. jsonIndent=4 and uuidCase=upper govern quick transforms
        state["draft"] = "{\"a\":{\"b\":1}}";
        (_, ctx) = ExecuteDevToolboxScript(
            compiled, "runTransform", state, settings, zhI18n, plainSnap,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = "jsonPretty" });
        Assert.Contains("\n    \"a\"", state["draft"], StringComparison.Ordinal);

        ExecuteDevToolboxScript(
            compiled, "runTransform", state, settings, zhI18n, plainSnap,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = "uuidv7" });
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-7[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}$", state["draft"]);

        ExecuteDevToolboxScript(
            compiled, "runTransform", state, settings, zhI18n, plainSnap,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = "uuidv4" });
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-4[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}$", state["draft"]);

        ExecuteDevToolboxScript(
            compiled, "runTransform", state, settings, zhI18n, plainSnap,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = "uuidNodash" });
        Assert.Matches("^[0-9A-F]{32}$", state["draft"]);

        // 6. Expired JWT shows clean Payload JSON + danger status + warning capsule, with Header/Raw switching
        string expiredJwt = BuildJwt($"{{\"sub\":\"u99\",\"exp\":{FixedNow.ToUnixTimeSeconds() - 120}}}");
        state["draft"] = expiredJwt;
        (_, ctx) = ExecuteDevToolboxScript(
            compiled, "runTransform", state, settings, zhI18n, plainSnap,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = "jwtDecode" });
        Assert.Equal("true", state["jwtMode"]);
        Assert.Equal("danger", CustomWidgetExpressionEvaluator.Interpolate("${$computed.jwtStatusColor}", ctx));
        Assert.Equal("true", CustomWidgetExpressionEvaluator.Interpolate(manifest.Capsule.BadgeIsWarning, ctx));
        using (JsonDocument payloadDoc = JsonDocument.Parse(state["draft"]))
        {
            Assert.Equal("u99", payloadDoc.RootElement.GetProperty("sub").GetString());
        }

        (_, ctx) = ExecuteDevToolboxScript(
            compiled, "setJwtView", state, settings, zhI18n, plainSnap,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["view"] = "header" });
        using (JsonDocument headerDoc = JsonDocument.Parse(state["draft"]))
        {
            Assert.Equal("HS256", headerDoc.RootElement.GetProperty("alg").GetString());
        }

        ExecuteDevToolboxScript(
            compiled, "setJwtView", state, settings, zhI18n, plainSnap,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["view"] = "raw" });
        Assert.Equal(expiredJwt, state["draft"]);
    }

    [Fact]
    public void UuidV7JwtPartsAndHtmlOps_ExecuteCleanlyInUtilityEngine()
    {
        CustomWidgetTransformResult v7 = CustomWidgetUtilityEngine.ExecuteTextTransform("", "uuidv7");
        Assert.True(v7.Succeeded, v7.ErrorMessage);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", v7.ResultText);

        Assert.Matches("^[0-9a-f]{12}7[0-9a-f]{3}[89ab][0-9a-f]{15}$",
            CustomWidgetUtilityEngine.ExecuteTextTransform("", "uuidv7nodash").ResultText);
        Assert.Matches("^[0-9A-F]{8}-[0-9A-F]{4}-7[0-9A-F]{3}-[89AB][0-9A-F]{3}-[0-9A-F]{12}$",
            CustomWidgetUtilityEngine.ExecuteTextTransform("", "uuidv7upper").ResultText);

        string jwt = BuildJwt($"{{\"sub\":\"dev-1\",\"exp\":{FixedNow.ToUnixTimeSeconds() + 3600}}}");
        CustomWidgetTransformResult payload = CustomWidgetUtilityEngine.ExecuteTextTransform(jwt, "jwtPayload", FixedNow, "zh-CN");
        Assert.True(payload.Succeeded, payload.ErrorMessage);
        Assert.Equal("valid", payload.JwtState);
        using (JsonDocument pDoc = JsonDocument.Parse(payload.ResultText))
        {
            Assert.Equal("dev-1", pDoc.RootElement.GetProperty("sub").GetString());
        }

        CustomWidgetTransformResult header = CustomWidgetUtilityEngine.ExecuteTextTransform(jwt, "jwtHeader", FixedNow, "zh-CN");
        Assert.True(header.Succeeded, header.ErrorMessage);
        using (JsonDocument hDoc = JsonDocument.Parse(header.ResultText))
        {
            Assert.Equal("HS256", hDoc.RootElement.GetProperty("alg").GetString());
        }

        const string htmlRaw = "<div data-id=\"1\" & 'x'></div>";
        CustomWidgetTransformResult encoded = CustomWidgetUtilityEngine.ExecuteTextTransform(htmlRaw, "htmlEncode");
        Assert.True(encoded.Succeeded, encoded.ErrorMessage);
        Assert.Contains("&lt;div", encoded.ResultText, StringComparison.Ordinal);

        CustomWidgetTransformResult decoded = CustomWidgetUtilityEngine.ExecuteTextTransform(encoded.ResultText, "htmlDecode");
        Assert.True(decoded.Succeeded, decoded.ErrorMessage);
        Assert.Equal(htmlRaw, decoded.ResultText);
    }

    [Fact]
    public void WorkspaceHistoryAndSyntaxLocator_PreserveDraftAndUndoCleanly()
    {
        CustomWidgetTemplateBundle bundle =
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.DevToolboxTemplateId);
        CustomWidgetWorkspaceValidationResult validated = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            bundle.ManifestJson,
            bundle.LogicJs,
            bundle.Locales);
        Assert.NotNull(validated.Manifest);
        Assert.NotNull(validated.CompiledScript);
        IReadOnlyDictionary<string, string> zhI18n = validated.Manifest!.ResolveI18nDictionary("zh-CN");
        CustomWidgetClipboardSnapshot emptyClip = CustomWidgetClipboardSnapshot.CreateFromText(string.Empty);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["autoCopyResult"] = "false",
            ["enableClipboardSniffer"] = "false",
            ["jsonIndent"] = "2",
            ["uuidCase"] = "lower"
        };

        const string originalJson = "{\"b\": 2, \"a\": 1}";
        Dictionary<string, string> state = CreateDevToolboxState(draft: originalJson);
        ExecuteDevToolboxScript(
            validated.CompiledScript!, "runTransform", state, settings, zhI18n, emptyClip,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = "jsonMinify" });
        string minified = state["draft"];
        Assert.Equal("{\"b\":2,\"a\":1}", minified);

        (_, CustomWidgetEvaluationContext ctx) = ExecuteDevToolboxScript(
            validated.CompiledScript!, "runTransform", state, settings, zhI18n, emptyClip,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = "base64Encode" });
        Assert.Equal("true", CustomWidgetExpressionEvaluator.Interpolate("${$computed.canUndo}", ctx));
        Assert.Equal("true", CustomWidgetExpressionEvaluator.Interpolate("${$computed.hasTrail0}", ctx));
        Assert.Equal("true", CustomWidgetExpressionEvaluator.Interpolate("${$computed.hasTrail1}", ctx));

        ExecuteDevToolboxScript(validated.CompiledScript!, "undoStep", state, settings, zhI18n, emptyClip);
        Assert.Equal(minified, state["draft"]);

        ExecuteDevToolboxScript(
            validated.CompiledScript!, "jumpHistory", state, settings, zhI18n, emptyClip,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["index"] = "0" });
        Assert.Equal(originalJson, state["draft"]);

        // Invalid JSON keeps user input intact while reporting line/col and caret offset
        const string brokenJson = "{\n  \"a\": 1,\n}";
        state = CreateDevToolboxState(draft: brokenJson);
        (_, ctx) = ExecuteDevToolboxScript(
            validated.CompiledScript!, "runTransform", state, settings, zhI18n, emptyClip,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = "jsonPretty" });

        Assert.Equal(brokenJson, state["draft"]);
        Assert.Equal("true", CustomWidgetExpressionEvaluator.Interpolate("${$computed.hasError}", ctx));
        Assert.Contains("3", state["errorMsg"], StringComparison.Ordinal);
        Assert.True(int.TryParse(state["errorOffset"], out int errOffset) && errOffset >= 0);

        ExecuteDevToolboxScript(validated.CompiledScript!, "focusError", state, settings, zhI18n, emptyClip);
        Assert.Equal("true", state["editing"]);
    }

    [Fact]
    public void QuickJsDigestsAndCapsuleAction_MatchDotNetVectorsAndDualMode()
    {
        CustomWidgetTemplateBundle bundle =
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.DevToolboxTemplateId);
        CustomWidgetWorkspaceValidationResult validated = CustomWidgetScriptValidator.ValidateWorkspaceBundle(
            bundle.ManifestJson,
            bundle.LogicJs,
            bundle.Locales);
        IReadOnlyDictionary<string, string> zhI18n = validated.Manifest!.ResolveI18nDictionary("zh-CN");
        CustomWidgetClipboardSnapshot emptyClip = CustomWidgetClipboardSnapshot.CreateFromText(string.Empty);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["autoCopyResult"] = "false",
            ["enableClipboardSniffer"] = "false",
            ["jsonIndent"] = "2",
            ["uuidCase"] = "lower"
        };

        const string sample = "DeskBox 开发者工具箱";
        byte[] sampleUtf8 = Encoding.UTF8.GetBytes(sample);
        string expectedSha256 = Convert.ToHexString(SHA256.HashData(sampleUtf8)).ToLowerInvariant();
        string expectedMd5 = Convert.ToHexString(MD5.HashData(sampleUtf8)).ToLowerInvariant();

        Dictionary<string, string> state = CreateDevToolboxState(draft: sample);
        ExecuteDevToolboxScript(
            validated.CompiledScript!, "runTransform", state, settings, zhI18n, emptyClip,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = "sha256" });
        Assert.Equal(expectedSha256, state["draft"]);

        state = CreateDevToolboxState(draft: sample);
        ExecuteDevToolboxScript(
            validated.CompiledScript!, "runTransform", state, settings, zhI18n, emptyClip,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["op"] = "md5" });
        Assert.Equal(expectedMd5, state["draft"]);

        // Idle capsule: generates UUID v7 and copies it
        state = CreateDevToolboxState();
        (CustomWidgetScriptExecutionResult idleCapExec, _) = ExecuteDevToolboxScript(
            validated.CompiledScript!, "runCapsuleAction", state, settings, zhI18n, emptyClip);
        Assert.Matches("^[0-9a-f]{8}-[0-9a-f]{4}-7[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", state["draft"]);
        Assert.Contains(idleCapExec.UiOps, op => op.Type == "copy" && op.Text == state["draft"]);

        // Active structured capsule: parses JSON and copies formatted output
        state = CreateDevToolboxState(draft: "{\"k\":1}");
        (CustomWidgetScriptExecutionResult activeCapExec, _) = ExecuteDevToolboxScript(
            validated.CompiledScript!, "runCapsuleAction", state, settings, zhI18n, emptyClip);
        Assert.Contains("\n  \"k\": 1", state["draft"], StringComparison.Ordinal);
        Assert.Contains(activeCapExec.UiOps, op => op.Type == "copy" && op.Text == state["draft"]);
    }

    [Fact]
    public void CodeWorkspaceHelpers_FormatLineNumbersAndTokenizeJson()
    {
        Assert.Equal("1", CustomWidgetNativeRenderer.FormatCodeLineNumbers(null));
        Assert.Equal("1", CustomWidgetNativeRenderer.FormatCodeLineNumbers(string.Empty));
        Assert.Equal("1", CustomWidgetNativeRenderer.FormatCodeLineNumbers("{\"ok\":true}"));
        Assert.Equal("1\n2\n3", CustomWidgetNativeRenderer.FormatCodeLineNumbers("{\n  \"a\": 1\n}"));

        const string sampleJson = "{\"k\":\"v\",\"n\":-12.5,\"b\":true}";
        List<CustomWidgetNativeRenderer.CodeHighlightToken> tokens =
            CustomWidgetNativeRenderer.TokenizeCodeForHighlight(sampleJson, "auto");

        Assert.Equal(sampleJson, string.Concat(tokens.Select(static t => t.Text)));
        Assert.Contains(tokens, t => t.Role == "key" && t.Text == "\"k\"");
        Assert.Contains(tokens, t => t.Role == "string" && t.Text == "\"v\"");
        Assert.Contains(tokens, t => t.Role == "number" && t.Text == "-12.5");
        Assert.Contains(tokens, t => t.Role == "literal" && t.Text == "true");
        Assert.Contains(tokens, t => t.Role == "punct");

        // Plain-text mode leaves content unsplit
        List<CustomWidgetNativeRenderer.CodeHighlightToken> plainTokens =
            CustomWidgetNativeRenderer.TokenizeCodeForHighlight(sampleJson, "plain");
        CustomWidgetNativeRenderer.CodeHighlightToken single = Assert.Single(plainTokens);
        Assert.Equal("default", single.Role);
        Assert.Equal(sampleJson, single.Text);
    }

    private static IEnumerable<CustomWidgetNodeSpec> FindButtons(CustomWidgetNodeSpec node)
    {
        if (node.Type.Equals("Button", StringComparison.OrdinalIgnoreCase))
        {
            yield return node;
        }

        foreach (CustomWidgetNodeSpec child in node.Children)
        {
            foreach (CustomWidgetNodeSpec found in FindButtons(child))
            {
                yield return found;
            }
        }
    }

    // ─────────────── Preset & gallery registration ───────────────

    [Fact]
    public void DevToolboxTemplate_IsRegisteredInPresetsAndCatalog()
    {
        Assert.Equal("dev-toolbox", CustomWidgetTemplates.DevToolboxTemplateId);
        Assert.Contains(CustomWidgetTemplates.Presets,
            p => p.Id == CustomWidgetTemplates.DevToolboxTemplateId &&
                 p.NameKey == "CustomWidget.Template.DevToolbox.Name" &&
                 p.DefaultWidth == 344 && p.DefaultHeight == 356);
        CustomWidgetTemplateBundle bundle = CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.DevToolboxTemplateId);
        Assert.False(string.IsNullOrWhiteSpace(bundle.ManifestJson));
        Assert.Same(
            bundle.ManifestJson,
            CustomWidgetTemplates.GetTemplateBundle(CustomWidgetTemplates.DevToolboxTemplateId).ManifestJson);

        // Offline catalog merge surfaces every preset + starter with Emoji IconGlyph and monochrome Fluent fallback
        Assert.True(CustomWidgetTemplates.Presets.All(p =>
            CustomWidgetTemplates.IsEmojiIcon(p.IconGlyph) &&
            CustomWidgetTemplates.IsSegoeFluentGlyph(p.MonochromeFallbackGlyph)));
    }

    [Fact]
    public void HostStrings_CarryDevToolboxAndNewCapabilityKeysInAllLocales()
    {
        string[] locales =
        [
            "ar-SA", "bn-BD", "de-DE", "en-US", "es-ES", "fr-FR", "hi-IN",
            "ja-JP", "ko-KR", "pt-BR", "ru-RU", "zh-CN", "zh-TW"
        ];
        string[] requiredKeys =
        [
            "CustomWidget.Template.DevToolbox.Name",
            "CustomWidget.Template.DevToolbox.Description",
            "CustomWidget.Template.DevToolbox.Tag",
            "CustomWidget.Cap.clipboard-workspace.Member.jwtdecode-unicodeescape-unicodeunescape-jsonesca.Desc",
            "CustomWidget.Cap.clipboard-workspace.Member.jwtdecode-clipboard-text-unicodeunescape-state-d.Desc",
            "CustomWidget.ApiDocs.Member.jwtdecode-unicodeunescape-uuid-base64decode.Desc"
        ];

        string? root = null;
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DeskBox.sln")))
            {
                root = dir.FullName;
                break;
            }
        }

        Assert.NotNull(root);
        foreach (string locale in locales)
        {
            using JsonDocument doc = JsonDocument.Parse(
                File.ReadAllText(Path.Combine(root, "src", "DeskBox", "Strings", locale + ".json")));
            var keys = new HashSet<string>(
                doc.RootElement.EnumerateObject().Select(p => p.Name),
                StringComparer.Ordinal);
            foreach (string key in requiredKeys)
            {
                Assert.True(keys.Contains(key), $"{locale} missing {key}");
            }
        }
    }
}
