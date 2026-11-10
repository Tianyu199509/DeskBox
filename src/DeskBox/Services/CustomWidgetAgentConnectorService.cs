using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace DeskBox.Services;

public enum McpConfigFormat
{
    StandardJson,
    VsCodeJson,
    CodexToml,
    OpenCodeJson,
    DeepSeekHarnessYaml,
    CliRegistration
}

public enum AgentPlaybookKind
{
    CreateNewWidget,
    ModifyExistingWidget,
    PushLiveHudTelemetry
}

public sealed record AgentBridgeSelfTestResult(
    bool McpProtocolOk,
    int McpToolCount,
    bool PipeOnline,
    int? HostPid,
    string WidgetsRoot,
    long ElapsedMs,
    string? ErrorMessage);

/// <summary>
/// Builds external AI Agent setup prompts and manual MCP configuration snippets (Store alias vs
/// Direct/Debug executable path), generates Workshop/HUD prompts, opens widget workspaces in
/// external editors, and runs local MCP + Named Pipe host diagnostics without modifying any
/// third-party client configuration files.
/// </summary>
public static class CustomWidgetAgentConnectorService
{
    public const string StoreExecutionAlias = "deskbox.exe";
    public const string PublicGuideUrl = "https://github.com/Tianyu199509/deskbox-agent";

    public static string ResolveDeskBoxExecutablePath(
        string? overrideExePath = null,
        string? currentProcessPathOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideExePath))
        {
            return overrideExePath.Trim();
        }

        if (!string.IsNullOrWhiteSpace(currentProcessPathOverride))
        {
            string trimmedOverride = currentProcessPathOverride.Trim();
            if (AppExecutablePath.IsCompanionExecutablePath(trimmedOverride))
            {
                string dir = Path.GetDirectoryName(trimmedOverride) ?? string.Empty;
                return Path.Combine(dir, AppExecutablePath.MainExecutableFileName);
            }

            return trimmedOverride;
        }

        return AppExecutablePath.ResolveMainExecutablePath() ?? AppExecutablePath.MainExecutableFileName;
    }

    public static string ResolveMcpCommandExecutable(
        string? overrideExePath = null,
        bool? isPackagedOverride = null,
        string? currentProcessPathOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideExePath))
        {
            return overrideExePath.Trim();
        }

        bool isPackaged = isPackagedOverride ?? AppDistributionService.Current.IsPackaged;
        if (isPackaged)
        {
            return StoreExecutionAlias;
        }

        return ResolveDeskBoxExecutablePath(
            overrideExePath: null,
            currentProcessPathOverride: currentProcessPathOverride);
    }

    public static string FormatMcpCommandLine(
        string? overrideExePath = null,
        bool? isPackagedOverride = null,
        string? currentProcessPathOverride = null)
    {
        string exe = ResolveMcpCommandExecutable(overrideExePath, isPackagedOverride, currentProcessPathOverride);
        if (string.Equals(exe, StoreExecutionAlias, StringComparison.OrdinalIgnoreCase))
        {
            return $"{StoreExecutionAlias} --mcp";
        }

        return $"\"{exe}\" --mcp";
    }

    public static string GetAppVersionString() =>
        typeof(CustomWidgetAgentConnectorService).Assembly.GetName().Version?.ToString(3) ?? "1.6.0";

    public static string BuildMcpSetupPrompt(
        string? uiLanguage = null,
        string? exePathOverride = null,
        bool? isPackagedOverride = null,
        string? currentProcessPathOverride = null)
    {
        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(uiLanguage);
        string commandLine = FormatMcpCommandLine(exePathOverride, isPackagedOverride, currentProcessPathOverride);
        string version = GetAppVersionString();

        if (isChinese)
        {
            return $$"""
                请为当前使用的客户端配置 DeskBox 本地 MCP 服务。

                服务名：deskbox
                启动命令：{{commandLine}}
                传输方式：stdio
                DeskBox 版本：{{version}}
                接入说明与 Skill：{{PublicGuideUrl}}（无法访问时忽略，不影响接入）

                要求：
                - 使用该客户端官方支持的方式添加，保留已有配置与其他 MCP 服务，不代写信任记录。
                - 如果客户端支持 Skill，可从上述仓库安装配套 Skill；Skill 不是必需的。
                - 没有本机配置权限或需要我在界面操作时，告诉我具体步骤。
                - 需要刷新、重启或新建会话时说明，不要自行关闭客户端。

                完成后分别报告：配置是否写入、客户端是否加载、调用 deskbox_get_widget_spec 是否成功。
                本次只完成接入与只读检查，不创建、修改或提交任何格子。
                """;
        }

        return $$"""
            Please configure the local DeskBox MCP server for the current client.

            Server name: deskbox
            Command: {{commandLine}}
            Transport: stdio
            DeskBox version: {{version}}
            Setup guide & Skill: {{PublicGuideUrl}} (optional; ignore if unreachable)

            Requirements:
            - Use this client's official configuration mechanism, preserve existing settings and other MCP servers, and do not modify client trust stores directly.
            - If the client supports Skills, you may install the companion Skill from the repository above; the Skill is optional.
            - If you lack local configuration permissions or need me to act in the UI, give me exact steps.
            - Let me know if a refresh, restart, or new session is required; do not close the running client yourself.

            When finished, report separately: whether the configuration was written, whether the client loaded it, and whether calling deskbox_get_widget_spec succeeded.
            Only complete setup and read-only verification in this step; do not create, modify, or commit any desktop widgets.
            """;
    }

    public static string? TryFindCursorExecutable(string? localAppDataOverride = null)
    {
        string localAppData = localAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(localAppData, "Programs", "cursor", "Cursor.exe"),
            Path.Combine(programFiles, "Cursor", "Cursor.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? TryFindVsCodeExecutable(string? localAppDataOverride = null)
    {
        string localAppData = localAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(localAppData, "Programs", "Microsoft VS Code", "Code.exe"),
            Path.Combine(programFiles, "Microsoft VS Code", "Code.exe"),
            Path.Combine(localAppData, "Programs", "Microsoft VS Code Insiders", "Code - Insiders.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? TryFindTraeExecutable(string? localAppDataOverride = null)
    {
        string localAppData = localAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(localAppData, "Programs", "Trae CN", "Trae CN.exe"),
            Path.Combine(localAppData, "Programs", "Trae", "Trae.exe"),
            Path.Combine(programFiles, "Trae CN", "Trae CN.exe"),
            Path.Combine(programFiles, "Trae", "Trae.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? TryFindWindsurfExecutable(string? localAppDataOverride = null)
    {
        string localAppData = localAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(localAppData, "Programs", "Windsurf", "Windsurf.exe"),
            Path.Combine(programFiles, "Windsurf", "Windsurf.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string BuildCursorMcpDeepLink(
        string? exePathOverride = null,
        bool? isPackagedOverride = null)
    {
        string exePath = ResolveMcpCommandExecutable(exePathOverride, isPackagedOverride);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("command", exePath);
            writer.WriteStartArray("args");
            writer.WriteStringValue("--mcp");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        string base64 = Convert.ToBase64String(ms.ToArray());
        return $"cursor://anysphere.cursor-deeplink/mcp/install?name=deskbox&config={Uri.EscapeDataString(base64)}";
    }

    public static string BuildVsCodeMcpDeepLink(
        string? exePathOverride = null,
        bool? isPackagedOverride = null)
    {
        string exePath = ResolveMcpCommandExecutable(exePathOverride, isPackagedOverride);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", "deskbox");
            writer.WriteString("type", "stdio");
            writer.WriteString("command", exePath);
            writer.WriteStartArray("args");
            writer.WriteStringValue("--mcp");
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        string json = Encoding.UTF8.GetString(ms.ToArray());
        return $"vscode:mcp/install?{Uri.EscapeDataString(json)}";
    }

    public static string BuildStandardMcpConfigJson(
        string? exePathOverride = null,
        bool? isPackagedOverride = null) =>
        BuildMcpSnippet(McpConfigFormat.StandardJson, exePathOverride, isPackagedOverride);

    public static string BuildMcpSnippet(
        McpConfigFormat format,
        string? exePathOverride = null,
        bool? isPackagedOverride = null,
        string? currentProcessPathOverride = null)
    {
        string exePath = ResolveMcpCommandExecutable(exePathOverride, isPackagedOverride, currentProcessPathOverride);

        switch (format)
        {
            case McpConfigFormat.VsCodeJson:
            {
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(
                           ms,
                           new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    writer.WriteStartObject();
                    writer.WriteStartObject("servers");
                    writer.WriteStartObject("deskbox");
                    writer.WriteString("type", "stdio");
                    writer.WriteString("command", exePath);
                    writer.WriteStartArray("args");
                    writer.WriteStringValue("--mcp");
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }

            case McpConfigFormat.CodexToml:
            {
                string escaped = exePath
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal);
                return $"[mcp_servers.deskbox]\ncommand = \"{escaped}\"\nargs = [\"--mcp\"]";
            }

            case McpConfigFormat.OpenCodeJson:
            {
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(
                           ms,
                           new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    writer.WriteStartObject();
                    writer.WriteStartObject("mcp");
                    writer.WriteStartObject("deskbox");
                    writer.WriteString("type", "local");
                    writer.WriteStartArray("command");
                    writer.WriteStringValue(exePath);
                    writer.WriteStringValue("--mcp");
                    writer.WriteEndArray();
                    writer.WriteBoolean("enabled", true);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }

            case McpConfigFormat.DeepSeekHarnessYaml:
            {
                string escaped = exePath
                    .Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal);
                var sb = new StringBuilder();
                sb.AppendLine("# DeepSeek Harness (cordis.patch.yml)");
                sb.AppendLine("\"@deepseek-ai/dsh-mcp-client\":");
                sb.AppendLine("  servers:");
                sb.AppendLine("    deskbox:");
                sb.AppendLine("      transport: stdio");
                sb.AppendLine($"      command: \"{escaped}\"");
                sb.AppendLine("      args:");
                sb.AppendLine("        - \"--mcp\"");
                return sb.ToString().TrimEnd();
            }

            case McpConfigFormat.CliRegistration:
            {
                string quotedOrAlias = string.Equals(exePath, StoreExecutionAlias, StringComparison.OrdinalIgnoreCase)
                    ? StoreExecutionAlias
                    : $"\"{exePath}\"";
                var sb = new StringBuilder();
                sb.AppendLine("# Claude Code");
                sb.AppendLine($"claude mcp add -s user deskbox -- {quotedOrAlias} --mcp");
                sb.AppendLine();
                sb.AppendLine("# OpenAI Codex CLI");
                sb.AppendLine($"codex mcp add deskbox -- {quotedOrAlias} --mcp");
                sb.AppendLine();
                sb.AppendLine("# Google Gemini CLI");
                sb.AppendLine($"gemini mcp add -s user deskbox {quotedOrAlias} --mcp");
                return sb.ToString().TrimEnd();
            }

            case McpConfigFormat.StandardJson:
            default:
            {
                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(
                           ms,
                           new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    writer.WriteStartObject();
                    writer.WriteStartObject("mcpServers");
                    writer.WriteStartObject("deskbox");
                    writer.WriteString("command", exePath);
                    writer.WriteStartArray("args");
                    writer.WriteStringValue("--mcp");
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }

    public static async Task<AgentBridgeSelfTestResult> RunBridgeSelfTestAsync(
        string? pipeNameOverride = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // 1. Verify MCP JSON-RPC initialize + tools/list locally
        bool mcpOk = false;
        int toolCount = 0;
        try
        {
            string? initResp = await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""",
                pipeNameOverride,
                cancellationToken);
            string? toolsResp = await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync(
                """{"jsonrpc":"2.0","id":2,"method":"tools/list"}""",
                pipeNameOverride,
                cancellationToken);

            if (!string.IsNullOrEmpty(initResp) && !string.IsNullOrEmpty(toolsResp))
            {
                using JsonDocument toolsDoc = JsonDocument.Parse(toolsResp);
                if (toolsDoc.RootElement.TryGetProperty("result", out JsonElement r) &&
                    r.TryGetProperty("tools", out JsonElement toolsArr) &&
                    toolsArr.ValueKind == JsonValueKind.Array)
                {
                    toolCount = toolsArr.GetArrayLength();
                    mcpOk = toolCount > 0;
                }
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new AgentBridgeSelfTestResult(
                false, 0, false, null, CustomWidgetDataStore.GetWidgetsRootDirectory(),
                sw.ElapsedMilliseconds, ex.Message);
        }

        // 2. Ping live Named Pipe host
        bool pipeOnline = false;
        int? hostPid = null;
        string widgetsRoot = CustomWidgetDataStore.GetWidgetsRootDirectory();
        string? errorMsg = null;

        string? pingResp = await DeskBoxAgentBridgeClient.TrySendAsync(
            w => w.WriteString("op", "ping"),
            pipeNameOverride,
            connectTimeoutMs: 1500,
            cancellationToken: cancellationToken);

        if (!string.IsNullOrWhiteSpace(pingResp))
        {
            try
            {
                using JsonDocument pingDoc = JsonDocument.Parse(pingResp);
                if (pingDoc.RootElement.TryGetProperty("ok", out JsonElement okEl) && okEl.ValueKind == JsonValueKind.True &&
                    pingDoc.RootElement.TryGetProperty("result", out JsonElement resEl))
                {
                    pipeOnline = true;
                    if (resEl.TryGetProperty("pid", out JsonElement pidEl) && pidEl.TryGetInt32(out int pid))
                    {
                        hostPid = pid;
                    }
                    if (resEl.TryGetProperty("widgetsRoot", out JsonElement wrEl) && wrEl.ValueKind == JsonValueKind.String)
                    {
                        widgetsRoot = wrEl.GetString() ?? widgetsRoot;
                    }
                }
            }
            catch
            {
            }
        }
        else
        {
            errorMsg = LocalizationService.TranslateForCulture(
                LocalizationService.ResolvePersistedOrAmbientCultureName(),
                "CustomWidget.AgentHub.SelfTest.PipeUnreachable");
        }

        sw.Stop();
        return new AgentBridgeSelfTestResult(
            McpProtocolOk: mcpOk,
            McpToolCount: toolCount,
            PipeOnline: pipeOnline,
            HostPid: hostPid,
            WidgetsRoot: widgetsRoot,
            ElapsedMs: sw.ElapsedMilliseconds,
            ErrorMessage: errorMsg);
    }

    public static string BuildStudioAgentMcpPrompt(
        string userIdea,
        string? uiLanguage = null,
        string? targetWidgetId = null,
        string? targetWidgetName = null,
        string? currentManifestJson = null,
        string? manifestFilePath = null,
        string? briefMarkdown = null,
        string? targetNodeContext = null,
        IReadOnlyList<string>? pinnedCapabilityIds = null,
        bool isCapsuleViewport = false)
    {
        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(uiLanguage);
        string goal = string.IsNullOrWhiteSpace(userIdea)
            ? LocalizationService.TranslateForCulture(
                uiLanguage,
                string.IsNullOrWhiteSpace(targetWidgetId)
                    ? "CustomWidget.Agent.Placeholder.CreateNew"
                    : "CustomWidget.Agent.Placeholder.EditExisting")
            : userIdea.Trim();

        bool hasExisting = !string.IsNullOrWhiteSpace(targetWidgetId) || !string.IsNullOrWhiteSpace(currentManifestJson);
        string wid = string.IsNullOrWhiteSpace(targetWidgetId) ? string.Empty : targetWidgetId.Trim();
        string wName = string.IsNullOrWhiteSpace(targetWidgetName) ? wid : targetWidgetName.Trim();

        var sb = new StringBuilder();
        if (isChinese)
        {
            if (hasExisting)
            {
                string targetDesc = !string.IsNullOrEmpty(wid)
                    ? $"「{wName}」（widgetId: `{wid}`）"
                    : (!string.IsNullOrWhiteSpace(wName) ? $"「{wName}」" : "当前预览中的桌面格子");
                sb.AppendLine($"请使用 `deskbox` MCP 工具帮我升级改造 DeskBox 桌面自定义格子 {targetDesc}：");
            }
            else
            {
                sb.AppendLine("请使用 `deskbox` MCP 工具帮我设计并创建一个原生 Windows 11 Fluent 风格的 DeskBox 桌面自定义格子：");
            }

            sb.AppendLine($"需求：{goal}");
            if (!string.IsNullOrWhiteSpace(uiLanguage) &&
                (uiLanguage.StartsWith("zh-TW", StringComparison.OrdinalIgnoreCase) ||
                 uiLanguage.StartsWith("zh-HK", StringComparison.OrdinalIgnoreCase) ||
                 uiLanguage.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase)))
            {
                sb.AppendLine("目标界面语言：繁體中文（zh-TW），请确保所有用户可见文案采用繁體中文。");
            }
            if (isCapsuleViewport)
            {
                sb.AppendLine("当前关注视图：胶囊条紧凑视图（`capsule` 字段）。");
            }
            if (!string.IsNullOrWhiteSpace(targetNodeContext))
            {
                sb.AppendLine($"指定修改节点：{targetNodeContext.Trim()}");
            }
            if (pinnedCapabilityIds is { Count: > 0 })
            {
                sb.AppendLine($"优先使用的能力模块：{string.Join(", ", pinnedCapabilityIds.Select(id => $"`@{id}`"))}（可调用 `deskbox_get_capability_spec` 查看字段契约）");
            }
            if (!string.IsNullOrWhiteSpace(briefMarkdown))
            {
                sb.AppendLine();
                sb.AppendLine("设计纪要（请保持一致）：");
                sb.AppendLine(briefMarkdown.Trim());
            }

            sb.AppendLine();
            sb.AppendLine("执行步骤（优先使用 `deskbox` MCP 工具，工具名通常为 `deskbox_*` 或 `mcp__deskbox__deskbox_*`）：");
            sb.AppendLine("1. 先调用 `deskbox_get_widget_spec`（可传 `detail: \"compact\"` 省上下文；若需能力详情可调用 `deskbox_get_capability_spec`；如需实时读取工坊当前选中节点与草稿状态也可调用 `deskbox_get_studio_context`）。");
            if (!string.IsNullOrEmpty(wid))
            {
                sb.AppendLine($"2. 调用 `deskbox_list_widgets`（传入 `widgetId: \"{wid}\"`）查看当前格子配置，或直接基于下方当前 `manifest.json` 修改。");
                sb.AppendLine($"3. 修改后先调用 `deskbox_validate_manifest` 确保零报错，再调用 `deskbox_upsert_widget`（务必传入 `widgetId: \"{wid}\"`）。该调用默认只进入工坊预览暂存并返回 `stageId`，不会改动桌面；请把 `stageId` 告诉我（我可在工坊预览舞台直接点击「保存修改」，或在对话中回复确认后由你再带 `commit: true`、同一 `stageId` 与完全相同的 `manifestJson` 提交到桌面）。");
            }
            else
            {
                sb.AppendLine("2. 编写 `manifest.json`（使用 `\"id\": \"custom.<slug>\"`）后调用 `deskbox_validate_manifest` 确保零报错。");
                sb.AppendLine("3. 调用 `deskbox_upsert_widget`。默认只进入工坊预览暂存并返回 `stageId`，不会改动桌面；请把 `stageId` 告诉我（我可在工坊预览舞台直接点击「放到桌面」，或在对话中回复确认后由你再带 `commit: true`、同一 `stageId` 与完全相同的 `manifestJson` 提交到桌面）。");
            }
            if (!string.IsNullOrWhiteSpace(manifestFilePath))
            {
                sb.AppendLine($"   （备选：工坊工作区路径为 `{manifestFilePath}`；草稿工作区保存后只刷新预览舞台。若该路径属于已放置的格子实例，请优先使用上面的暂存流程。）");
            }
            sb.AppendLine();
            sb.AppendLine("重要提示：优先直接调用 `deskbox_*`（或 `mcp__deskbox__deskbox_*`）MCP 工具（若工具被延迟加载，先用 ToolSearch 搜索 `mcp__deskbox` 或 `deskbox`；也可通过 Skill 工具加载 `deskbox-widget`）。切勿在当前代码仓库中搜索无关源码或运行目录扫描。");
            if (!string.IsNullOrWhiteSpace(manifestFilePath))
            {
                string wsDir = Path.GetDirectoryName(manifestFilePath) ?? string.Empty;
                sb.AppendLine("若当前会话未加载 `deskbox_*` 工具，可使用以下零扫描回退方案（工坊草稿工作区预览模式）完成任务：");
                sb.AppendLine($"- 规范参考（已包含全部组件与表达式白名单）：调用 Skill 工具加载 `deskbox-widget`，或读取 `{Path.Combine(wsDir, "SKILL.md")}`");
                sb.AppendLine($"- 在草稿工作区编辑并保存 `{manifestFilePath}`，工坊会在约 200ms 内完成校验并刷新预览舞台；这一步不会写入桌面（若需命令行离线校验也可参考 `\"{ResolveMcpCommandExecutable()}\" widget validate --file \"{manifestFilePath}\"`）。");
                sb.AppendLine($"- 例外：若 `{manifestFilePath}` 位于已放到桌面的 `<widgetId>` 实例目录（工坊编辑态），直接保存属于手动本地编辑，也可能同步刷新该桌面实例；需要「先审阅再生效」时请改用 MCP 暂存流程。");
                sb.AppendLine("- 保存完成后在回复中告知我即可，我会在工坊预览舞台确认并点击「放到桌面 / 保存」；草稿工作区下只有这一步才会真正改动桌面。");
            }

            if (!string.IsNullOrWhiteSpace(currentManifestJson))
            {
                sb.AppendLine();
                sb.AppendLine("当前 `manifest.json`：");
                sb.AppendLine("```json");
                sb.AppendLine(currentManifestJson.Trim());
                sb.AppendLine("```");
            }

            return sb.ToString().Trim();
        }

        if (hasExisting)
        {
            string targetDesc = !string.IsNullOrEmpty(wid)
                ? $"\"{wName}\" (widgetId: `{wid}`)"
                : (!string.IsNullOrWhiteSpace(wName) ? $"\"{wName}\"" : "the active studio widget");
            sb.AppendLine($"Please use the `deskbox` MCP tools to update my DeskBox desktop widget {targetDesc}:");
        }
        else
        {
            sb.AppendLine("Please use the `deskbox` MCP tools to design and create a native Windows 11 Fluent DeskBox desktop widget:");
        }

        sb.AppendLine($"Goal: {goal}");
        if (!string.IsNullOrWhiteSpace(uiLanguage))
        {
            sb.AppendLine($"Target UI language: {CustomWidgetRemotePromptService.DescribeTargetLanguage(uiLanguage)}");
        }
        if (isCapsuleViewport)
        {
            sb.AppendLine("Active viewport focus: compact capsule bar (`capsule` block).");
        }
        if (!string.IsNullOrWhiteSpace(targetNodeContext))
        {
            sb.AppendLine($"Target node to modify: {targetNodeContext.Trim()}");
        }
        if (pinnedCapabilityIds is { Count: > 0 })
        {
            sb.AppendLine($"Pinned capabilities: {string.Join(", ", pinnedCapabilityIds.Select(id => $"`@{id}`"))} (inspect via `deskbox_get_capability_spec`)");
        }
        if (!string.IsNullOrWhiteSpace(briefMarkdown))
        {
            sb.AppendLine();
            sb.AppendLine("Design Brief:");
            sb.AppendLine(briefMarkdown.Trim());
        }

        sb.AppendLine();
        sb.AppendLine("Workflow (prefer `deskbox` MCP tools, typically named `deskbox_*` or `mcp__deskbox__deskbox_*`):");
        sb.AppendLine("1. Call `deskbox_get_widget_spec` (pass `detail: \"compact\"` for token-lean context; use `deskbox_get_capability_spec` for individual capabilities, or `deskbox_get_studio_context` to inspect the live Workshop draft & selected node).");
        if (!string.IsNullOrEmpty(wid))
        {
            sb.AppendLine($"2. Inspect via `deskbox_list_widgets` (`widgetId: \"{wid}\"`) or update the current `manifest.json` below.");
            sb.AppendLine($"3. Validate with `deskbox_validate_manifest`, then call `deskbox_upsert_widget` (pass `widgetId: \"{wid}\"`). It validates and STAGES the change in the Workshop preview (desktop unchanged) and returns a `stageId`; share the `stageId` with me so I can either click `Save changes` in the Workshop or confirm in chat for you to call again with `commit: true`, the same `stageId` and the exact same `manifestJson`.");
        }
        else
        {
            sb.AppendLine("2. Author `manifest.json` (`\"id\": \"custom.<slug>\"`) and verify zero errors with `deskbox_validate_manifest`.");
            sb.AppendLine("3. Call `deskbox_upsert_widget` to validate and STAGE the widget in the Workshop preview (desktop unchanged, returns a `stageId`); share the `stageId` with me so I can either click `Place on desktop` in the Workshop or confirm in chat for you to call again with `commit: true`, the same `stageId` and the exact same `manifestJson`.");
        }
        if (!string.IsNullOrWhiteSpace(manifestFilePath))
        {
            sb.AppendLine($"   (Optional workspace path: `{manifestFilePath}` — in a draft workspace saving it refreshes the preview only. Prefer the staged flow above if this is a live widget folder.)");
        }
        sb.AppendLine();
        sb.AppendLine("IMPORTANT: Prefer the `deskbox_*` (or `mcp__deskbox__deskbox_*`) MCP tools directly (if tools are deferred, ToolSearch for `mcp__deskbox` or `deskbox` first, or load the `deskbox-widget` Skill). Do NOT search or scan unrelated source files in the current repository.");
        if (!string.IsNullOrWhiteSpace(manifestFilePath))
        {
            string wsDir = Path.GetDirectoryName(manifestFilePath) ?? string.Empty;
            sb.AppendLine("If `deskbox_*` tools are not loaded in this session, use the built-in Widget Workshop draft-preview mode:");
            sb.AppendLine($"- Spec reference (full component/expression whitelist): load the `deskbox-widget` Skill or read `{Path.Combine(wsDir, "SKILL.md")}`");
            sb.AppendLine($"- Edit and save `{manifestFilePath}` in the draft workspace; Widget Workshop validates it and refreshes the preview within ~200ms. This does NOT write the desktop (no extra command required; optional CLI check: `\"{ResolveMcpCommandExecutable()}\" widget validate --file \"{manifestFilePath}\"`).");
            sb.AppendLine($"- Exception: if `{manifestFilePath}` lives inside an already-placed `<widgetId>` folder (Workshop edit mode), a direct save is a manual local edit and can also refresh that live desktop instance; use the MCP staged flow when review-before-apply is required.");
            sb.AppendLine("- Then let me know so I can confirm and click `Place on desktop` / `Save changes`; in a draft workspace only that user action writes to the desktop.");
        }

        if (!string.IsNullOrWhiteSpace(currentManifestJson))
        {
            sb.AppendLine();
            sb.AppendLine("Current `manifest.json`:");
            sb.AppendLine("```json");
            sb.AppendLine(currentManifestJson.Trim());
            sb.AppendLine("```");
        }

        return sb.ToString().Trim();
    }

    public static string BuildKickoffPrompt(
        AgentPlaybookKind kind,
        string? uiLanguage = null,
        string? targetWidgetId = null,
        string? targetWidgetName = null,
        string? userIdea = null)
    {
        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(uiLanguage);
        string exePath = ResolveMcpCommandExecutable();

        switch (kind)
        {
            case AgentPlaybookKind.ModifyExistingWidget:
                return BuildStudioAgentMcpPrompt(
                    userIdea ?? string.Empty,
                    uiLanguage,
                    targetWidgetId: string.IsNullOrWhiteSpace(targetWidgetId) ? "<widgetId>" : targetWidgetId,
                    targetWidgetName: targetWidgetName);

            case AgentPlaybookKind.PushLiveHudTelemetry:
            {
                if (isChinese)
                {
                    return $$"""
                        在接下来执行编码、构建或测试任务的过程中，请把你的关键阶段进度实时同步到我的 DeskBox 桌面「AI 进度看板」（`widgetId: "agent-hud"`）：
                        - 请在关键节点调用 `deskbox` MCP 的 `deskbox_push_widget_data` 工具，传入 `widgetId: "agent-hud"`、`state: {"agentName": "Agent", "taskTitle": "当前任务名", "statusText": "正在执行的步骤", "progress": "0-100", "isBusy": "true/false"}`，并传入 `collection: "events"`, `mode: "prepend"`, `items: [{"tag": "STEP", "tone": "accent", "text": "阶段进展摘要"}]` 追加实时动态（任务结束时将 `progress` 设为 `"100"`、`isBusy` 设为 `"false"`）。
                        - 若当前会话未加载 `deskbox` MCP 工具，请先提醒我重启 Agent 客户端以加载 MCP，或使用 CLI 命令推送：`"{{exePath}}" widget push --id agent-hud --set taskTitle="任务名" --set statusText="执行中" --set progress=60 --set isBusy=true --prepend events:tag=STEP,tone=accent,text="完成阶段构建"`（切勿在当前代码仓库中搜索无关源码）。
                        """;
                }

                return $$"""
                    While executing coding, build, or test tasks in this session, stream your live progress to my DeskBox desktop AI Progress HUD (`widgetId: "agent-hud"`):
                    - Call `deskbox_push_widget_data` at key milestones with `widgetId: "agent-hud"`, `state: {"agentName": "Agent", "taskTitle": "...", "statusText": "...", "progress": "0-100", "isBusy": "true/false"}`, and `collection: "events"`, `mode: "prepend"`, `items: [{"tag": "STEP", "tone": "accent", "text": "..."}]` (set `progress: "100"`, `isBusy: "false"` when finished).
                    - If `deskbox` MCP tools are not loaded in this session, remind me to restart the Agent client or use the CLI fallback: `"{{exePath}}" widget push --id agent-hud --set taskTitle="Task" --set statusText="Running" --set progress=60 --set isBusy=true --prepend events:tag=STEP,tone=accent,text="Milestone complete"` (do NOT scan unrelated repo files).
                    """;
            }

            case AgentPlaybookKind.CreateNewWidget:
            default:
                return BuildStudioAgentMcpPrompt(
                    userIdea ?? string.Empty,
                    uiLanguage);
        }
    }

    public static bool TryOpenWorkspaceInEditor(string workspaceDirectory, string preferredEditor = "auto")
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            return false;
        }

        Directory.CreateDirectory(workspaceDirectory);
        string manifestFile = Path.Combine(workspaceDirectory, "manifest.json");
        string editor = preferredEditor.Trim().ToLowerInvariant();

        try
        {
            if (editor is "cursor" or "auto")
            {
                string? cursorExe = TryFindCursorExecutable();
                if (cursorExe is not null)
                {
                    StartEditorProcess(cursorExe, workspaceDirectory, manifestFile);
                    return true;
                }
                if (editor == "cursor")
                {
                    return false;
                }
            }

            if (editor is "trae" or "auto")
            {
                string? traeExe = TryFindTraeExecutable();
                if (traeExe is not null)
                {
                    StartEditorProcess(traeExe, workspaceDirectory, manifestFile);
                    return true;
                }
                if (editor == "trae")
                {
                    return false;
                }
            }

            if (editor is "windsurf" or "auto")
            {
                string? windsurfExe = TryFindWindsurfExecutable();
                if (windsurfExe is not null)
                {
                    StartEditorProcess(windsurfExe, workspaceDirectory, manifestFile);
                    return true;
                }
                if (editor == "windsurf")
                {
                    return false;
                }
            }

            if (editor is "vscode" or "auto")
            {
                string? codeExe = TryFindVsCodeExecutable();
                if (codeExe is not null)
                {
                    StartEditorProcess(codeExe, workspaceDirectory, manifestFile);
                    return true;
                }
                if (editor == "vscode")
                {
                    return false;
                }
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{workspaceDirectory}\"",
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryOpenWorkspaceInTerminal(string workspaceDirectory)
    {
        if (string.IsNullOrWhiteSpace(workspaceDirectory))
        {
            return false;
        }

        Directory.CreateDirectory(workspaceDirectory);
        try
        {
            string wtCandidate = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft",
                "WindowsApps",
                "wt.exe");
            if (File.Exists(wtCandidate))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = wtCandidate,
                    Arguments = $"-d \"{workspaceDirectory}\"",
                    UseShellExecute = true
                });
                return true;
            }
        }
        catch
        {
        }

        try
        {
            string escaped = workspaceDirectory.Replace("'", "''", StringComparison.Ordinal);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoExit -Command \"Set-Location -LiteralPath '{escaped}'\"",
                WorkingDirectory = workspaceDirectory,
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool RevealPathInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
                return true;
            }

            string? dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private static void StartEditorProcess(string exePath, string workspaceDirectory, string manifestFile)
    {
        string args = File.Exists(manifestFile)
            ? $"\"{workspaceDirectory}\" \"{manifestFile}\""
            : $"\"{workspaceDirectory}\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            UseShellExecute = false
        });
    }
}
