using System.Globalization;
using System.Text;
using System.Text.Json;
using DeskBox.FileSafety;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

/// <summary>
/// P2：外部 Agent 写入默认暂存、显式提交边界的回归测试。
/// 通过 RootOverride 隔离到临时目录，测试结束删除自己创建的目录。
/// </summary>
public sealed class CustomWidgetAgentStageStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _previousRoot;
    private readonly CultureInfo _previousUiCulture;

    public CustomWidgetAgentStageStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "DeskBoxStageTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _previousRoot = CustomWidgetAgentStageStore.RootOverride;
        CustomWidgetAgentStageStore.RootOverride = _root;
        _previousUiCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("zh-CN");
    }

    public void Dispose()
    {
        CultureInfo.CurrentUICulture = _previousUiCulture;
        CustomWidgetAgentStageStore.RootOverride = _previousRoot;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // 临时目录清理失败不影响测试结果。
        }
    }

    private static string Stage(string? widgetId, string manifest, string? logicJs = null, string? localesJson = null)
        => CustomWidgetAgentStageStore.Stage(new AgentStageRequest(
            widgetId,
            manifest,
            logicJs,
            logicJs is not null,
            localesJson,
            localesJson is not null,
            false));

    private string MetaPath(string stageId) => Path.Combine(_root, stageId, "meta.txt");

    private string ManifestPath(string stageId) => Path.Combine(_root, stageId, "manifest.json");

    [Fact]
    public void Stage_ThenConsume_WithSameManifest_SucceedsOnlyOnce()
    {
        const string manifest = "{\"id\":\"deskbox.test\",\"schemaVersion\":1}";
        string stageId = Stage("widget-1", manifest);

        Assert.Equal(32, stageId.Length);
        Assert.True(File.Exists(ManifestPath(stageId)));

        bool ok = CustomWidgetAgentStageStore.TryConsume(stageId, manifest, out string? widgetId, out string error);

        Assert.True(ok, error);
        Assert.Equal("widget-1", widgetId);
        Assert.False(Directory.Exists(Path.Combine(_root, stageId)));

        bool second = CustomWidgetAgentStageStore.TryConsume(stageId, manifest, out _, out string secondError);
        Assert.False(second);
        Assert.Contains("不存在或已被提交", secondError);
    }

    [Fact]
    public void Consume_RejectsDifferentManifest_AndKeepsStageUsable()
    {
        const string manifest = "{\"id\":\"deskbox.test\",\"schemaVersion\":1}";
        const string tampered = "{\"id\":\"deskbox.test\",\"schemaVersion\":2}";
        string stageId = Stage(null, manifest);

        bool ok = CustomWidgetAgentStageStore.TryConsume(stageId, tampered, out _, out string error);
        Assert.False(ok);
        Assert.Contains("不一致", error);
        Assert.True(File.Exists(MetaPath(stageId)));

        bool retry = CustomWidgetAgentStageStore.TryConsume(stageId, manifest, out string? widgetId, out _);
        Assert.True(retry);
        Assert.Null(widgetId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("not-a-valid-stage-id")]
    public void Consume_RejectsMissingOrMalformedStageId(string? stageId)
    {
        bool ok = CustomWidgetAgentStageStore.TryConsume(stageId, "{}", out _, out string error);
        Assert.False(ok);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Stage_PreservesFullBundle_AndListPendingReturnsIt()
    {
        const string manifest = "{\"id\":\"deskbox.test\",\"schemaVersion\":1}";
        const string logic = "export function onInit(ctx) { ctx.computed.done = true; }";
        const string locales = "{\"zh-CN\":{\"t\":\"标题\"}}";
        string stageId = Stage("widget-3", manifest, logic, locales);

        var pending = CustomWidgetAgentStageStore.ListPending();
        Assert.Single(pending);
        Assert.Equal(stageId, pending[0].StageId);
        Assert.Equal("widget-3", pending[0].WidgetId);

        bool read = CustomWidgetAgentStageStore.TryReadBundle(stageId, out AgentStageBundle? bundle, out string error);
        Assert.True(read, error);
        Assert.NotNull(bundle);
        Assert.Equal(manifest, bundle.ManifestJson);
        Assert.Equal(logic, bundle.LogicJs);
        Assert.Equal(locales, bundle.LocalesJson);
        Assert.True(bundle.HasLogicJs);
        Assert.True(bundle.HasLocales);
        // 读取 bundle 不消费批次，仍可提交
        Assert.True(File.Exists(MetaPath(stageId)));
    }

    [Fact]
    public void TryReadBundle_RejectsMissingStage()
    {
        bool read = CustomWidgetAgentStageStore.TryReadBundle("0123456789abcdef0123456789abcdef",
            out AgentStageBundle? bundle, out string error);
        Assert.False(read);
        Assert.Null(bundle);
        Assert.Contains("不存在或已被提交", error);
    }

    [Fact]
    public void ManifestHash_IsStableAndContentSensitive()
    {
        string first = CustomWidgetAgentStageStore.ComputeManifestHash("{\"a\":1}");
        string again = CustomWidgetAgentStageStore.ComputeManifestHash("{\"a\":1}");
        string different = CustomWidgetAgentStageStore.ComputeManifestHash("{\"a\":2}");

        Assert.Equal(first, again);
        Assert.NotEqual(first, different);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void CleanupStale_KeepsFreshStageFiles()
    {
        string stageId = Stage("widget-2", "{}");

        int removed = CustomWidgetAgentStageStore.CleanupStale();

        Assert.Equal(0, removed);
        Assert.True(File.Exists(ManifestPath(stageId)));
    }

    [Fact]
    public void TryDiscard_RemovesPendingStage_WithoutConsumingIt()
    {
        const string manifest = "{\"id\":\"deskbox.test\",\"schemaVersion\":1}";
        string stageId = Stage("widget-4", manifest);
        Assert.Single(CustomWidgetAgentStageStore.ListPending());

        bool discarded = CustomWidgetAgentStageStore.TryDiscard(stageId, out string error);

        Assert.True(discarded, error);
        Assert.False(Directory.Exists(Path.Combine(_root, stageId)));
        Assert.Empty(CustomWidgetAgentStageStore.ListPending());
        // 忽略不等于已提交：同一 stageId 不能再被拿来落盘。
        Assert.False(CustomWidgetAgentStageStore.TryConsume(stageId, manifest, out _, out string consumeError));
        Assert.Contains("不存在或已被提交", consumeError);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void TryDiscard_RejectsMissingOrMalformedStageId(string? stageId)
    {
        bool discarded = CustomWidgetAgentStageStore.TryDiscard(stageId, out string error);
        Assert.False(discarded);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void TryConsume_RecordsCommittedState_ForIdempotentRetry()
    {
        const string manifest = "{\"id\":\"deskbox.test\",\"schemaVersion\":1}";
        string stageId = Stage("widget-4b", manifest);

        Assert.False(CustomWidgetAgentStageStore.TryGetCommitted(stageId, out _));

        Assert.True(CustomWidgetAgentStageStore.TryConsume(stageId, manifest, out _, out string error), error);

        // 消费后应立即能查到幂等记录：重试提交走的是「已提交」而不是「stageId 不存在」。
        Assert.True(CustomWidgetAgentStageStore.TryGetCommitted(stageId, out string? committedWidgetId));
        Assert.Equal("widget-4b", committedWidgetId);
        Assert.False(CustomWidgetAgentStageStore.TryConsume(stageId, manifest, out _, out _));
    }

    [Fact]
    public void RecordCommitted_OverwritesWidgetId_AfterRealUpsertResolvesIt()
    {
        const string manifest = "{\"id\":\"deskbox.test\",\"schemaVersion\":1}";
        // 暂存时 widgetId 为空（新格子由 manifest.id 决定），提交后才知道真实 id。
        string stageId = Stage(null, manifest);
        Assert.True(CustomWidgetAgentStageStore.TryConsume(stageId, manifest, out string? stagedWidgetId, out _));
        Assert.Null(stagedWidgetId);

        CustomWidgetAgentStageStore.RecordCommitted(stageId, "custom.resolved");

        Assert.True(CustomWidgetAgentStageStore.TryGetCommitted(stageId, out string? resolved));
        Assert.Equal("custom.resolved", resolved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("../escape")]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void TryGetCommitted_RejectsUnknownOrMalformedStageId(string? stageId)
    {
        Assert.False(CustomWidgetAgentStageStore.TryGetCommitted(stageId, out string? widgetId));
        Assert.Null(widgetId);
    }

    [Fact]
    public void TryDiscard_DoesNotCreateCommittedRecord()
    {
        string stageId = Stage("widget-4c", "{}");
        Assert.True(CustomWidgetAgentStageStore.TryDiscard(stageId, out _));

        // 拒绝不等于已提交：不能因为用户按了「忽略」就让重试提交拿到成功结果。
        Assert.False(CustomWidgetAgentStageStore.TryGetCommitted(stageId, out _));
    }

    [Fact]
    public void Changed_FiresOnStageConsumeAndDiscard()
    {
        int notifications = 0;
        Action handler = () => notifications++;
        CustomWidgetAgentStageStore.Changed += handler;
        try
        {
            string first = Stage("widget-5", "{}");
            Assert.True(CustomWidgetAgentStageStore.TryConsume(first, "{}", out _, out _));
            string second = Stage("widget-6", "{}");
            Assert.True(CustomWidgetAgentStageStore.TryDiscard(second, out _));

            Assert.Equal(4, notifications);
        }
        finally
        {
            CustomWidgetAgentStageStore.Changed -= handler;
        }
    }

    [Fact]
    public async Task BridgeStageOps_ListGetDiscard_RoundTripWithoutTouchingDesktop()
    {
        const string manifest = "{\"id\":\"deskbox.test\",\"schemaVersion\":1}";
        string stageId = Stage("widget-7", manifest);

        await using var bridge = new DeskBoxAgentBridgeServer(
            "DeskBox_Stage_Op_Test_" + Guid.NewGuid().ToString("N"),
            static () => null);

        using (JsonDocument listDoc = JsonDocument.Parse(
            await bridge.DispatchRequestJsonAsync("{\"op\":\"stage_list\"}")))
        {
            Assert.True(listDoc.RootElement.GetProperty("ok").GetBoolean());
            JsonElement result = listDoc.RootElement.GetProperty("result");
            Assert.Equal(1, result.GetProperty("count").GetInt32());
            Assert.Equal(stageId, result.GetProperty("stages")[0].GetProperty("stageId").GetString());
        }

        using (JsonDocument getDoc = JsonDocument.Parse(
            await bridge.DispatchRequestJsonAsync("{\"op\":\"stage_get\",\"stageId\":\"" + stageId + "\"}")))
        {
            Assert.Equal(manifest, getDoc.RootElement.GetProperty("result").GetProperty("manifestJson").GetString());
        }

        using (JsonDocument discardDoc = JsonDocument.Parse(
            await bridge.DispatchRequestJsonAsync("{\"op\":\"stage_discard\",\"stageId\":\"" + stageId + "\"}")))
        {
            Assert.True(discardDoc.RootElement.GetProperty("ok").GetBoolean());
            JsonElement result = discardDoc.RootElement.GetProperty("result");
            Assert.True(result.GetProperty("discarded").GetBoolean());
            Assert.False(result.GetProperty("committed").GetBoolean());
        }

        Assert.Empty(CustomWidgetAgentStageStore.ListPending());
    }

    /// <summary>
    /// 回归：MCP 工具定义里写了 stageId / commit，但适配器曾经把两者丢掉，
    /// 导致外部 Agent 按文档「确认后再提交」时永远无法闭环。
    /// </summary>
    [Fact]
    public async Task McpUpsert_ForwardsStageIdAndCommit_ToBridgePayload()
    {
        const string stageId = "0123456789abcdef0123456789abcdef";
        string capturedJson = string.Empty;

        Func<Action<Utf8JsonWriter>, CancellationToken, Task<string?>> sender = (writePayload, _) =>
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writePayload(writer);
                writer.WriteEndObject();
            }

            capturedJson = Encoding.UTF8.GetString(buffer.ToArray());
            return Task.FromResult<string?>("{\"ok\":true,\"result\":{\"staged\":false,\"committed\":true}}");
        };

        string request = """
            {"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"deskbox_upsert_widget","arguments":{"widgetId":"custom.x","manifestJson":"{\"manifestVersion\":1,\"id\":\"custom.x\"}","commit":true,"stageId":"0123456789abcdef0123456789abcdef"}}}
            """;

        string? response = await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync(
            request,
            pipeNameOverride: "unused",
            cancellationToken: CancellationToken.None,
            directBridgeSender: sender);

        Assert.NotNull(response);
        using JsonDocument payload = JsonDocument.Parse(capturedJson);
        Assert.Equal("upsert", payload.RootElement.GetProperty("op").GetString());
        Assert.True(payload.RootElement.GetProperty("commit").GetBoolean());
        Assert.Equal(stageId, payload.RootElement.GetProperty("stageId").GetString());
        Assert.False(payload.RootElement.TryGetProperty("reveal", out _));
    }

    /// <summary>
    /// 端到端往返：MCP 适配器产出的 payload 必须能被真实 Bridge 反序列化理解，
    /// 且 manager 不可用时只报错、不消费暂存批次。
    /// 单段测试（payload 正确 / Bridge 能解析）都过，中间字段名对不上照样是断的。
    /// </summary>
    [Fact]
    public async Task McpUpsertPayload_RoundTripsThroughBridge_WithoutConsumingStage()
    {
        const string manifest = "{\"id\":\"deskbox.test\",\"schemaVersion\":1}";
        string stageId = Stage("widget-9", manifest);
        string capturedPayload = string.Empty;

        Func<Action<Utf8JsonWriter>, CancellationToken, Task<string?>> sender = (writePayload, _) =>
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writePayload(writer);
                writer.WriteEndObject();
            }

            capturedPayload = Encoding.UTF8.GetString(buffer.ToArray());
            return Task.FromResult<string?>(null);
        };

        string escapedManifest = manifest.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string request =
            "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/call\",\"params\":{\"name\":\"deskbox_upsert_widget\"," +
            "\"arguments\":{\"widgetId\":\"widget-9\",\"manifestJson\":\"" + escapedManifest + "\"," +
            "\"commit\":true,\"stageId\":\"" + stageId + "\"}}}";

        await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync(
            request,
            pipeNameOverride: "unused",
            cancellationToken: CancellationToken.None,
            directBridgeSender: sender);

        Assert.False(string.IsNullOrWhiteSpace(capturedPayload));

        await using var bridge = new DeskBoxAgentBridgeServer(
            "DeskBox_Stage_RoundTrip_" + Guid.NewGuid().ToString("N"),
            static () => null);

        using JsonDocument response = JsonDocument.Parse(
            await bridge.DispatchRequestJsonAsync(capturedPayload));

        // manager 未就绪 → 明确报错，而不是假装成功。
        Assert.False(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(response.RootElement.GetProperty("error").GetString()));

        // 关键：失败路径不能把批次吃掉，用户确认后还要能重试。
        Assert.True(File.Exists(MetaPath(stageId)));
        Assert.Single(CustomWidgetAgentStageStore.ListPending());
        Assert.False(CustomWidgetAgentStageStore.TryGetCommitted(stageId, out _));
    }

    [Fact]
    public async Task BridgeStageCommit_WhenManagerUnavailable_KeepsStagePending()
    {
        const string manifest = "{\"id\":\"deskbox.test\",\"schemaVersion\":1}";
        string stageId = Stage("widget-8", manifest);

        await using var bridge = new DeskBoxAgentBridgeServer(
            "DeskBox_Stage_Commit_Test_" + Guid.NewGuid().ToString("N"),
            static () => null);

        string response = await bridge.DispatchRequestJsonAsync(
            "{\"op\":\"stage_commit\",\"stageId\":\"" + stageId + "\"}");
        using JsonDocument doc = JsonDocument.Parse(response);
        Assert.False(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("WidgetManager", doc.RootElement.GetProperty("error").GetString());

        // 提交失败不能吞掉用户还没确认的内容。
        Assert.Single(CustomWidgetAgentStageStore.ListPending());
        Assert.Equal(stageId, CustomWidgetAgentStageStore.ListPending()[0].StageId);
    }

    [Fact]
    public void TryFindLatestPendingBundle_ResolvesByStageId_WidgetId_AndManifestId()
    {
        const string manifest = "{\"id\":\"custom.hydration_tracker\",\"name\":\"喝水打卡\",\"schemaVersion\":1}";
        string stageId = Stage("widget-instance-42", manifest, "export function onInit() {}");

        Assert.True(CustomWidgetAgentStageStore.TryFindLatestPendingBundle(stageId, out AgentStageBundle? byStageId));
        Assert.NotNull(byStageId);
        Assert.Equal(stageId, byStageId.StageId);

        Assert.True(CustomWidgetAgentStageStore.TryFindLatestPendingBundle("widget-instance-42", out AgentStageBundle? byWidgetId));
        Assert.NotNull(byWidgetId);
        Assert.Equal(stageId, byWidgetId.StageId);

        Assert.True(CustomWidgetAgentStageStore.TryFindLatestPendingBundle("custom.hydration_tracker", out AgentStageBundle? byManifestId));
        Assert.NotNull(byManifestId);
        Assert.Equal(stageId, byManifestId.StageId);

        Assert.False(CustomWidgetAgentStageStore.TryFindLatestPendingBundle("non-existent-widget", out _));
    }
}
