using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Platform;

namespace DeskBox.Services;

public enum McpConfigFormat
{
    StandardJson,
    VsCodeJson,
    CodexToml,
    DeepSeekHarnessYaml,
    StreamableHttpJson,
    OpenCodeJson,
    CliRegistration
}

public enum AgentPlaybookKind
{
    CreateNewWidget,
    ModifyExistingWidget,
    PushLiveHudTelemetry
}

public sealed record ExternalAgentClientStatus(
    string ClientId,
    string DisplayName,
    bool IsInstalled,
    bool IsConfigured,
    string DetailPath,
    string ActionKind,
    string TargetSummary = "");

public sealed record AgentSkillLocationStatus(
    string Id,
    string DisplayName,
    string SkillRootDirectory,
    string SkillFilePath,
    bool ToolDetected,
    bool IsInstalled);

public sealed record AgentBridgeSelfTestResult(
    bool McpProtocolOk,
    int McpToolCount,
    bool PipeOnline,
    int? HostPid,
    string WidgetsRoot,
    bool HttpEnabled,
    bool HttpOnline,
    int HttpPort,
    long ElapsedMs,
    string? ErrorMessage);

/// <summary>
/// Discovers local AI coding agents, IDEs, and desktop MCP clients, generates multi-format
/// universal configs (Standard JSON, VS Code JSON, Codex TOML, Streamable HTTP MCP, OpenCode, CLI),
/// non-destructively merges into arbitrary <c>.json</c>/<c>.toml</c> files with <c>.deskbox.bak</c>
/// backups, manages global/custom <c>SKILL.md</c> packages, prepares zero-MCP workspaces, and runs
/// end-to-end Named Pipe + Loopback HTTP diagnostics.
/// </summary>
public static class CustomWidgetAgentConnectorService
{
    public const string ClientCursor = "cursor";
    public const string ClientClaudeCode = "claude-code";
    public const string ClientCodex = "codex";
    public const string ClientVsCode = "vscode";
    public const string ClientTrae = "trae";
    public const string ClientWindsurf = "windsurf";
    public const string ClientAntigravity = "antigravity";
    public const string ClientCline = "cline";
    public const string ClientClaudeDesktop = "claude-desktop";
    public const string ClientWorkBuddy = "workbuddy";
    public const string ClientDeepSeekHarness = "deepseek-harness";
    public const string ClientUniversalSkill = "universal-skill";

    private static readonly HttpClient LoopbackHttpClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(2)
    })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    public static string ResolveDeskBoxExecutablePath(string? overrideExePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overrideExePath))
        {
            return overrideExePath.Trim();
        }

        string currentExe = Environment.ProcessPath ?? "DeskBox.exe";
        if (currentExe.EndsWith("DeskBox.Settings.exe", StringComparison.OrdinalIgnoreCase))
        {
            string dir = Path.GetDirectoryName(currentExe) ?? string.Empty;
            string sibling = Path.Combine(dir, "DeskBox.exe");
            if (File.Exists(sibling))
            {
                return sibling;
            }
        }

        return currentExe;
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

    public static string? TryFindWorkBuddyExecutable(string? localAppDataOverride = null)
    {
        string localAppData = localAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string[] candidates =
        [
            Path.Combine(localAppData, "Programs", "WorkBuddy", "WorkBuddy.exe"),
            Path.Combine(localAppData, "Programs", "CodeBuddy", "CodeBuddy.exe"),
            Path.Combine(programFiles, "WorkBuddy", "WorkBuddy.exe"),
            Path.Combine(programFiles, "CodeBuddy", "CodeBuddy.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static string ResolveDshHomeDirectory(
        string? userProfileOverride = null,
        string? dshHomeOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(dshHomeOverride))
        {
            return dshHomeOverride.Trim();
        }

        if (string.IsNullOrWhiteSpace(userProfileOverride))
        {
            string? envDshHome = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrWhiteSpace(envDshHome))
            {
                return envDshHome.Trim();
            }
        }

        string home = string.IsNullOrWhiteSpace(userProfileOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfileOverride;
        return Path.Combine(home, ".dsh");
    }

    public static string? TryFindDshExecutable(
        string? userProfileOverride = null,
        string? appDataOverride = null,
        string? localAppDataOverride = null)
    {
        string home = userProfileOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appData = appDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string localAppData = localAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            Path.Combine(appData, "npm", "dsh.cmd"),
            Path.Combine(localAppData, "pnpm", "dsh.cmd"),
            Path.Combine(home, ".bun", "bin", "dsh.exe"),
            Path.Combine(home, ".bun", "bin", "dsh.cmd"),
            Path.Combine(home, ".local", "bin", "dsh.exe"),
            Path.Combine(home, ".local", "bin", "dsh.cmd")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    public static IReadOnlyList<ExternalAgentClientStatus> DetectAgentClients(
        string? userProfileOverride = null,
        string? appDataOverride = null,
        string? localAppDataOverride = null,
        string? dshHomeOverride = null)
    {
        string home = string.IsNullOrWhiteSpace(userProfileOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfileOverride;
        string appData = string.IsNullOrWhiteSpace(appDataOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : appDataOverride;
        string localAppData = string.IsNullOrWhiteSpace(localAppDataOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            : localAppDataOverride;

        bool hasOverrideRoots = !string.IsNullOrWhiteSpace(userProfileOverride) ||
                                !string.IsNullOrWhiteSpace(appDataOverride) ||
                                !string.IsNullOrWhiteSpace(localAppDataOverride);

        // 1. Cursor
        string? cursorExe = hasOverrideRoots && localAppDataOverride is null ? null : TryFindCursorExecutable(localAppData);
        string cursorDir = Path.Combine(home, ".cursor");
        string cursorMcpPath = Path.Combine(cursorDir, "mcp.json");
        bool cursorInstalled = cursorExe is not null || Directory.Exists(cursorDir);
        bool cursorConfigured = HasMcpServerInJsonFile(cursorMcpPath, "deskbox");

        // 2. Claude Code
        string claudeDir = Path.Combine(home, ".claude");
        string claudeJsonPath = Path.Combine(home, ".claude.json");
        bool claudeCodeInstalled = Directory.Exists(claudeDir) ||
                                   File.Exists(claudeJsonPath) ||
                                   File.Exists(Path.Combine(home, ".local", "bin", "claude.exe"));
        bool claudeCodeConfigured = HasMcpServerInJsonFile(claudeJsonPath, "deskbox");

        // 3. OpenAI Codex (TOML)
        string codexDir = Path.Combine(home, ".codex");
        string codexTomlPath = Path.Combine(codexDir, "config.toml");
        bool codexInstalled = Directory.Exists(codexDir) || File.Exists(codexTomlPath);
        bool codexConfigured = HasMcpServerInCodexTomlFile(codexTomlPath, "deskbox");

        // 4. VS Code (GitHub Copilot)
        string? vscodeExe = hasOverrideRoots && localAppDataOverride is null ? null : TryFindVsCodeExecutable(localAppData);
        string vscodeUserDir = Path.Combine(appData, "Code", "User");
        string vscodeMcpPath = Path.Combine(vscodeUserDir, "mcp.json");
        bool vscodeInstalled = vscodeExe is not null ||
                               Directory.Exists(vscodeUserDir) ||
                               Directory.Exists(Path.Combine(home, ".vscode"));
        bool vscodeConfigured = HasMcpServerInJsonFile(vscodeMcpPath, "deskbox", "servers") ||
                                HasMcpServerInJsonFile(vscodeMcpPath, "deskbox", "mcpServers");

        // 5. Trae / Trae CN
        string? traeExe = hasOverrideRoots && localAppDataOverride is null ? null : TryFindTraeExecutable(localAppData);
        string traeCnMcp = Path.Combine(appData, "Trae CN", "User", "mcp.json");
        string traeGlobalMcp = Path.Combine(appData, "Trae", "User", "mcp.json");
        bool hasTraeCn = Directory.Exists(Path.Combine(appData, "Trae CN")) ||
                         Directory.Exists(Path.Combine(home, ".trae-cn")) ||
                         (traeExe?.Contains("Trae CN", StringComparison.OrdinalIgnoreCase) ?? false);
        bool traeInstalled = traeExe is not null ||
                             hasTraeCn ||
                             Directory.Exists(Path.Combine(appData, "Trae")) ||
                             Directory.Exists(Path.Combine(home, ".trae"));
        string traeMcpPath = File.Exists(traeCnMcp) || hasTraeCn ? traeCnMcp : traeGlobalMcp;
        bool traeConfigured = HasMcpServerInAnyConfigFile(traeCnMcp, "deskbox") ||
                              HasMcpServerInAnyConfigFile(traeGlobalMcp, "deskbox");

        // 6. Windsurf
        string? windsurfExe = hasOverrideRoots && localAppDataOverride is null ? null : TryFindWindsurfExecutable(localAppData);
        string windsurfDir = Path.Combine(home, ".codeium", "windsurf");
        string windsurfMcpPath = Path.Combine(windsurfDir, "mcp_config.json");
        bool windsurfInstalled = windsurfExe is not null || Directory.Exists(windsurfDir);
        bool windsurfConfigured = HasMcpServerInJsonFile(windsurfMcpPath, "deskbox");

        // 7. Antigravity / Gemini CLI
        string agyDir = Path.Combine(home, ".gemini", "antigravity");
        string geminiDir = Path.Combine(home, ".gemini");
        string agyMcpPath = Path.Combine(agyDir, "mcp_config.json");
        string geminiSettingsPath = Path.Combine(geminiDir, "settings.json");
        bool agyInstalled = Directory.Exists(agyDir) || Directory.Exists(geminiDir);
        string primaryGeminiTarget = Directory.Exists(agyDir) || File.Exists(agyMcpPath)
            ? agyMcpPath
            : geminiSettingsPath;
        bool agyConfigured = HasMcpServerInJsonFile(agyMcpPath, "deskbox") ||
                             HasMcpServerInJsonFile(geminiSettingsPath, "deskbox");

        // 8. Cline / Roo Code
        string clineCfg = Path.Combine(appData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json");
        string rooCfg = Path.Combine(appData, "Code", "User", "globalStorage", "rooveterinaryinc.roo-cline", "settings", "mcp_settings.json");
        bool hasCline = Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(clineCfg)!) ?? string.Empty) || File.Exists(clineCfg);
        bool hasRoo = Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(rooCfg)!) ?? string.Empty) || File.Exists(rooCfg);
        bool clineOrRooInstalled = hasCline || hasRoo;
        string primaryClineTarget = hasRoo && !hasCline ? rooCfg : clineCfg;
        bool clineOrRooConfigured = HasMcpServerInJsonFile(clineCfg, "deskbox") ||
                                    HasMcpServerInJsonFile(rooCfg, "deskbox");

        // 9. Claude Desktop
        string claudeDesktopDir = Path.Combine(appData, "Claude");
        string claudeDesktopCfg = Path.Combine(claudeDesktopDir, "claude_desktop_config.json");
        bool claudeDesktopInstalled = Directory.Exists(claudeDesktopDir) ||
                                      File.Exists(claudeDesktopCfg) ||
                                      Directory.Exists(Path.Combine(localAppData, "AnthropicClaude"));
        bool claudeDesktopConfigured = HasMcpServerInJsonFile(claudeDesktopCfg, "deskbox");

        // 10. WorkBuddy / CodeBuddy
        string? workbuddyExe = hasOverrideRoots && localAppDataOverride is null ? null : TryFindWorkBuddyExecutable(localAppData);
        string workbuddyDir = Path.Combine(home, ".workbuddy");
        string codebuddyDir = Path.Combine(home, ".codebuddy");
        string workbuddyMcpPath = Path.Combine(workbuddyDir, "mcp.json");
        string codebuddyMcpPath = Path.Combine(codebuddyDir, "mcp.json");
        bool workbuddyInstalled = workbuddyExe is not null ||
                                  Directory.Exists(workbuddyDir) ||
                                  Directory.Exists(codebuddyDir) ||
                                  Directory.Exists(Path.Combine(appData, "WorkBuddy")) ||
                                  Directory.Exists(Path.Combine(appData, "CodeBuddy"));
        string primaryWorkBuddyTarget = !Directory.Exists(workbuddyDir) &&
                                        !File.Exists(workbuddyMcpPath) &&
                                        (Directory.Exists(codebuddyDir) || File.Exists(codebuddyMcpPath))
            ? codebuddyMcpPath
            : workbuddyMcpPath;
        bool workbuddyConfigured = HasMcpServerInAnyConfigFile(workbuddyMcpPath, "deskbox") ||
                                   HasMcpServerInAnyConfigFile(codebuddyMcpPath, "deskbox");

        // 11. DeepSeek Harness (dsh)
        string dshHome = ResolveDshHomeDirectory(userProfileOverride, dshHomeOverride);
        string? dshExe = hasOverrideRoots && userProfileOverride is null && appDataOverride is null && localAppDataOverride is null
            ? null
            : TryFindDshExecutable(home, appData, localAppData);
        string dshGlobalPatchPath = Path.Combine(dshHome, "cordis.patch.yml");
        string dshWebProfileDir = Path.Combine(dshHome, "profiles", "web");
        string dshWebPatchPath = Path.Combine(dshWebProfileDir, "cordis.patch.yml");
        string dshMcpServersYaml = Path.Combine(dshHome, "mcp-servers.yaml");
        bool dshInstalled = dshExe is not null ||
                            Directory.Exists(dshHome) ||
                            File.Exists(Path.Combine(dshHome, "settings.yaml"));
        string primaryDshTarget = File.Exists(dshWebPatchPath) ||
                                  (Directory.Exists(dshWebProfileDir) && !File.Exists(dshGlobalPatchPath))
            ? dshWebPatchPath
            : dshGlobalPatchPath;
        bool dshConfigured = HasMcpServerInDshYamlFile(dshGlobalPatchPath, "deskbox") ||
                             HasMcpServerInDshYamlFile(dshWebPatchPath, "deskbox") ||
                             HasMcpServerInDshYamlFile(dshMcpServersYaml, "deskbox");

        return
        [
            new ExternalAgentClientStatus(
                ClientCursor,
                "Cursor",
                cursorInstalled,
                cursorConfigured,
                cursorMcpPath,
                "merge-config",
                "MCP (~/.cursor/mcp.json) + Skill"),
            new ExternalAgentClientStatus(
                ClientClaudeCode,
                "Claude Code",
                claudeCodeInstalled,
                claudeCodeConfigured,
                claudeJsonPath,
                "merge-config",
                "MCP (~/.claude.json) + Skill"),
            new ExternalAgentClientStatus(
                ClientCodex,
                "OpenAI Codex",
                codexInstalled,
                codexConfigured,
                codexTomlPath,
                "merge-toml",
                "MCP (~/.codex/config.toml) + Skill"),
            new ExternalAgentClientStatus(
                ClientDeepSeekHarness,
                "DeepSeek Harness (dsh)",
                dshInstalled,
                dshConfigured,
                primaryDshTarget,
                "merge-yaml",
                "MCP (~/.dsh/cordis.patch.yml) + Skill"),
            new ExternalAgentClientStatus(
                ClientVsCode,
                "VS Code · Copilot",
                vscodeInstalled,
                vscodeConfigured,
                vscodeMcpPath,
                "merge-config",
                "MCP (Code/User/mcp.json) + Skill"),
            new ExternalAgentClientStatus(
                ClientTrae,
                "Trae",
                traeInstalled,
                traeConfigured,
                traeMcpPath,
                "merge-config",
                "MCP (Trae/User/mcp.json) + Skill"),
            new ExternalAgentClientStatus(
                ClientWindsurf,
                "Windsurf",
                windsurfInstalled,
                windsurfConfigured,
                windsurfMcpPath,
                "merge-config",
                "MCP (~/.codeium/windsurf/mcp_config.json)"),
            new ExternalAgentClientStatus(
                ClientAntigravity,
                "Antigravity / Gemini CLI",
                agyInstalled,
                agyConfigured,
                primaryGeminiTarget,
                "merge-config",
                "MCP (~/.gemini) + Universal Skill"),
            new ExternalAgentClientStatus(
                ClientCline,
                "Cline / Roo Code",
                clineOrRooInstalled,
                clineOrRooConfigured,
                primaryClineTarget,
                "merge-config",
                "MCP (cline_mcp_settings.json)"),
            new ExternalAgentClientStatus(
                ClientClaudeDesktop,
                "Claude Desktop",
                claudeDesktopInstalled,
                claudeDesktopConfigured,
                claudeDesktopCfg,
                "merge-config",
                "MCP (claude_desktop_config.json)"),
            new ExternalAgentClientStatus(
                ClientWorkBuddy,
                "WorkBuddy / CodeBuddy",
                workbuddyInstalled,
                workbuddyConfigured,
                primaryWorkBuddyTarget,
                "merge-config",
                "MCP (~/.workbuddy/mcp.json) + Skill")
        ];
    }

    public static IReadOnlyList<AgentSkillLocationStatus> DetectGlobalSkillLocations(
        string? userProfileOverride = null,
        string? dshHomeOverride = null)
    {
        string home = string.IsNullOrWhiteSpace(userProfileOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfileOverride;
        string dshHome = ResolveDshHomeDirectory(userProfileOverride, dshHomeOverride);

        (string Id, string Label, string RootDir, string ParentProbe)[] entries =
        [
            ("universal", "Universal (.agents/skills)", Path.Combine(home, ".agents", "skills"), Path.Combine(home, ".agents")),
            ("claude", "Claude Code (.claude/skills)", Path.Combine(home, ".claude", "skills"), Path.Combine(home, ".claude")),
            ("cursor", "Cursor (.cursor/skills)", Path.Combine(home, ".cursor", "skills"), Path.Combine(home, ".cursor")),
            ("codex", "OpenAI Codex (.codex/skills)", Path.Combine(home, ".codex", "skills"), Path.Combine(home, ".codex")),
            ("dsh", "DeepSeek Harness (.dsh/skills)", Path.Combine(dshHome, "skills"), dshHome),
            ("workbuddy", "WorkBuddy (.workbuddy/skills)", Path.Combine(home, ".workbuddy", "skills"), Path.Combine(home, ".workbuddy"))
        ];

        var list = new List<AgentSkillLocationStatus>(entries.Length);
        foreach (var item in entries)
        {
            string skillFile = Path.Combine(item.RootDir, "deskbox-widget", "SKILL.md");
            bool detected = item.Id == "universal" || Directory.Exists(item.ParentProbe) || File.Exists(skillFile);
            list.Add(new AgentSkillLocationStatus(
                item.Id,
                item.Label,
                item.RootDir,
                skillFile,
                ToolDetected: detected,
                IsInstalled: File.Exists(skillFile)));
        }

        return list;
    }

    public static async Task<int> SyncAllGlobalSkillsAsync(
        string? userProfileOverride = null,
        string? dshHomeOverride = null)
    {
        IReadOnlyList<AgentSkillLocationStatus> locations = DetectGlobalSkillLocations(userProfileOverride, dshHomeOverride);
        int synced = 0;
        foreach (AgentSkillLocationStatus loc in locations)
        {
            if (loc.Id == "universal" || loc.ToolDetected || loc.IsInstalled)
            {
                await InstallAgentSkillAsync(loc.SkillRootDirectory);
                synced++;
            }
        }
        return synced;
    }

    public static string BuildCursorMcpDeepLink(string? exePathOverride = null)
    {
        string exePath = ResolveDeskBoxExecutablePath(exePathOverride);
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
        return $"cursor://anysphere.cursor-deeplink/mcp/install?name=DeskBox&config={Uri.EscapeDataString(base64)}";
    }

    public static string BuildVsCodeMcpDeepLink(string? exePathOverride = null)
    {
        string exePath = ResolveDeskBoxExecutablePath(exePathOverride);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   ms,
                   new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("name", "DeskBox");
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

    public static string BuildStandardMcpConfigJson(string? exePathOverride = null) =>
        BuildMcpSnippet(McpConfigFormat.StandardJson, exePathOverride);

    public static string BuildMcpSnippet(
        McpConfigFormat format,
        string? exePathOverride = null,
        int? httpPortOverride = null,
        string? httpTokenOverride = null)
    {
        string exePath = ResolveDeskBoxExecutablePath(exePathOverride);

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
                string escaped = exePath.Replace("'", "''", StringComparison.Ordinal);
                return $"""
                    [mcp_servers.deskbox]
                    command = '{escaped}'
                    args = ["--mcp"]
                    """;
            }

            case McpConfigFormat.DeepSeekHarnessYaml:
            {
                return BuildDshCordisPatchSnippet("deskbox", exePath).TrimEnd();
            }

            case McpConfigFormat.StreamableHttpJson:
            {
                int port = httpPortOverride ?? CustomWidgetStudioPreferencesStore.Shared.AgentHttpPort;
                string token = !string.IsNullOrWhiteSpace(httpTokenOverride)
                    ? httpTokenOverride!
                    : CustomWidgetStudioPreferencesStore.Shared.GetOrCreateAgentHttpToken();

                using var ms = new MemoryStream();
                using (var writer = new Utf8JsonWriter(
                           ms,
                           new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                {
                    writer.WriteStartObject();
                    writer.WriteStartObject("mcpServers");
                    writer.WriteStartObject("deskbox");
                    writer.WriteString("type", "streamable-http");
                    writer.WriteString("url", $"http://127.0.0.1:{port}/mcp");
                    writer.WriteStartObject("headers");
                    writer.WriteString("Authorization", $"Bearer {token}");
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                return Encoding.UTF8.GetString(ms.ToArray());
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

            case McpConfigFormat.CliRegistration:
            {
                var sb = new StringBuilder();
                sb.AppendLine($"# Claude Code");
                sb.AppendLine($"claude mcp add -s user deskbox -- \"{exePath}\" --mcp");
                sb.AppendLine();
                sb.AppendLine($"# OpenAI Codex CLI");
                sb.AppendLine($"codex mcp add deskbox -- \"{exePath}\" --mcp");
                sb.AppendLine();
                sb.AppendLine($"# Google Gemini CLI");
                sb.AppendLine($"gemini mcp add -s user deskbox \"{exePath}\" --mcp");
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

    public static async Task<string> InstallAgentSkillAsync(string skillRootDirectory)
    {
        string targetDir = Path.Combine(skillRootDirectory, "deskbox-widget");
        Directory.CreateDirectory(targetDir);
        string skillPath = Path.Combine(targetDir, "SKILL.md");
        string schemaPath = Path.Combine(targetDir, "manifest.schema.json");
        await File.WriteAllTextAsync(skillPath, CustomWidgetTemplates.AiAgentSkillMarkdown, Encoding.UTF8);
        await File.WriteAllTextAsync(schemaPath, CustomWidgetTemplates.ManifestJsonSchema, Encoding.UTF8);
        return skillPath;
    }

    public static async Task<(bool Success, string Detail)> ConnectClientAsync(
        string clientId,
        string? exePathOverride = null,
        string? userProfileOverride = null,
        string? appDataOverride = null,
        string? dshHomeOverride = null)
    {
        string home = string.IsNullOrWhiteSpace(userProfileOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : userProfileOverride;
        string appData = string.IsNullOrWhiteSpace(appDataOverride)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : appDataOverride;
        string exePath = ResolveDeskBoxExecutablePath(exePathOverride);

        try
        {
            switch (clientId.ToLowerInvariant())
            {
                case ClientCursor:
                {
                    await InstallAgentSkillAsync(Path.Combine(home, ".cursor", "skills"));
                    string cursorMcpPath = Path.Combine(home, ".cursor", "mcp.json");
                    MergeMcpServerIntoJsonFile(cursorMcpPath, "deskbox", exePath, includeStdioType: false);
                    return (true, cursorMcpPath);
                }

                case ClientClaudeCode:
                {
                    await InstallAgentSkillAsync(Path.Combine(home, ".claude", "skills"));
                    string claudeJsonPath = Path.Combine(home, ".claude.json");
                    MergeMcpServerIntoJsonFile(claudeJsonPath, "deskbox", exePath, includeStdioType: true);
                    return (true, claudeJsonPath);
                }

                case ClientCodex:
                {
                    await InstallAgentSkillAsync(Path.Combine(home, ".codex", "skills"));
                    await InstallAgentSkillAsync(Path.Combine(home, ".agents", "skills"));
                    string codexTomlPath = Path.Combine(home, ".codex", "config.toml");
                    MergeMcpServerIntoCodexTomlFile(codexTomlPath, "deskbox", exePath);
                    return (true, codexTomlPath);
                }

                case ClientVsCode:
                {
                    await InstallAgentSkillAsync(Path.Combine(home, ".agents", "skills"));
                    string vscodeMcpPath = Path.Combine(appData, "Code", "User", "mcp.json");
                    MergeMcpServerIntoJsonFile(
                        vscodeMcpPath,
                        "deskbox",
                        exePath,
                        includeStdioType: true,
                        rootPropertyName: "servers");
                    return (true, vscodeMcpPath);
                }

                case ClientTrae:
                {
                    await InstallAgentSkillAsync(Path.Combine(home, ".agents", "skills"));
                    string traeCnDir = Path.Combine(appData, "Trae CN");
                    string traeCnMcp = Path.Combine(traeCnDir, "User", "mcp.json");
                    string traeGlobalMcp = Path.Combine(appData, "Trae", "User", "mcp.json");
                    string targetPath = Directory.Exists(traeCnDir) || File.Exists(traeCnMcp)
                        ? traeCnMcp
                        : traeGlobalMcp;
                    InjectMcpIntoConfigFile(targetPath, exePath);
                    return (true, targetPath);
                }

                case ClientWindsurf:
                {
                    await InstallAgentSkillAsync(Path.Combine(home, ".agents", "skills"));
                    string windsurfMcpPath = Path.Combine(home, ".codeium", "windsurf", "mcp_config.json");
                    MergeMcpServerIntoJsonFile(windsurfMcpPath, "deskbox", exePath, includeStdioType: false);
                    return (true, windsurfMcpPath);
                }

                case ClientAntigravity:
                {
                    await InstallAgentSkillAsync(Path.Combine(home, ".agents", "skills"));
                    string agyDir = Path.Combine(home, ".gemini", "antigravity");
                    string geminiDir = Path.Combine(home, ".gemini");
                    string? primaryWritten = null;

                    if (Directory.Exists(agyDir) || !Directory.Exists(geminiDir))
                    {
                        string agyMcp = Path.Combine(agyDir, "mcp_config.json");
                        MergeMcpServerIntoJsonFile(agyMcp, "deskbox", exePath, includeStdioType: false);
                        primaryWritten = agyMcp;
                    }

                    if (Directory.Exists(geminiDir))
                    {
                        string geminiSettings = Path.Combine(geminiDir, "settings.json");
                        MergeMcpServerIntoJsonFile(geminiSettings, "deskbox", exePath, includeStdioType: false);
                        primaryWritten ??= geminiSettings;
                    }

                    return (true, primaryWritten!);
                }

                case ClientCline:
                {
                    string clineCfg = Path.Combine(appData, "Code", "User", "globalStorage", "saoudrizwan.claude-dev", "settings", "cline_mcp_settings.json");
                    string rooCfg = Path.Combine(appData, "Code", "User", "globalStorage", "rooveterinaryinc.roo-cline", "settings", "mcp_settings.json");
                    bool hasCline = Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(clineCfg)!) ?? string.Empty) || File.Exists(clineCfg);
                    bool hasRoo = Directory.Exists(Path.GetDirectoryName(Path.GetDirectoryName(rooCfg)!) ?? string.Empty) || File.Exists(rooCfg);

                    string? primary = null;
                    if (hasCline || !hasRoo)
                    {
                        MergeMcpServerIntoJsonFile(clineCfg, "deskbox", exePath, includeStdioType: false);
                        primary = clineCfg;
                    }
                    if (hasRoo)
                    {
                        MergeMcpServerIntoJsonFile(rooCfg, "deskbox", exePath, includeStdioType: false);
                        primary ??= rooCfg;
                    }

                    return (true, primary!);
                }

                case ClientClaudeDesktop:
                {
                    string cfgPath = Path.Combine(appData, "Claude", "claude_desktop_config.json");
                    MergeMcpServerIntoJsonFile(cfgPath, "deskbox", exePath, includeStdioType: false);
                    return (true, cfgPath);
                }

                case ClientWorkBuddy:
                {
                    await InstallAgentSkillAsync(Path.Combine(home, ".workbuddy", "skills"));
                    await InstallAgentSkillAsync(Path.Combine(home, ".agents", "skills"));
                    string wbDir = Path.Combine(home, ".workbuddy");
                    string cbDir = Path.Combine(home, ".codebuddy");
                    string wbMcp = Path.Combine(wbDir, "mcp.json");
                    string cbMcp = Path.Combine(cbDir, "mcp.json");
                    string? primaryWritten = null;

                    if (Directory.Exists(wbDir) || !Directory.Exists(cbDir))
                    {
                        MergeMcpServerIntoJsonFile(wbMcp, "deskbox", exePath, includeStdioType: false);
                        primaryWritten = wbMcp;
                    }

                    if (Directory.Exists(cbDir))
                    {
                        await InstallAgentSkillAsync(Path.Combine(cbDir, "skills"));
                        MergeMcpServerIntoJsonFile(cbMcp, "deskbox", exePath, includeStdioType: false);
                        primaryWritten ??= cbMcp;
                    }

                    return (true, primaryWritten!);
                }

                case ClientDeepSeekHarness:
                {
                    string dshHome = ResolveDshHomeDirectory(userProfileOverride, dshHomeOverride);
                    await InstallAgentSkillAsync(Path.Combine(dshHome, "skills"));
                    await InstallAgentSkillAsync(Path.Combine(home, ".agents", "skills"));
                    string dshGlobalPatch = Path.Combine(dshHome, "cordis.patch.yml");
                    string dshWebProfileDir = Path.Combine(dshHome, "profiles", "web");
                    string dshWebPatch = Path.Combine(dshWebProfileDir, "cordis.patch.yml");
                    string targetPatch = File.Exists(dshWebPatch) ||
                                         (Directory.Exists(dshWebProfileDir) && !File.Exists(dshGlobalPatch))
                        ? dshWebPatch
                        : dshGlobalPatch;

                    MergeMcpServerIntoDshCordisPatchFile(targetPatch, "deskbox", exePath);
                    if (File.Exists(dshWebPatch) &&
                        !string.Equals(targetPatch, dshWebPatch, StringComparison.OrdinalIgnoreCase))
                    {
                        MergeMcpServerIntoDshCordisPatchFile(dshWebPatch, "deskbox", exePath);
                    }

                    return (true, targetPatch);
                }

                case ClientUniversalSkill:
                default:
                {
                    string skillFile = await InstallAgentSkillAsync(Path.Combine(home, ".agents", "skills"));
                    return (true, skillFile);
                }
            }
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Auto-detects format (<c>.toml</c> vs VS Code <c>"servers"</c> JSON vs OpenCode <c>"mcp"</c> JSON
    /// vs Standard <c>"mcpServers"</c> JSON), backs up any existing file to <c>.deskbox.bak</c>,
    /// and non-destructively injects the <c>deskbox</c> MCP entry.
    /// </summary>
    public static void InjectMcpIntoConfigFile(
        string filePath,
        string? exePathOverride = null,
        string serverName = "deskbox")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        string exePath = ResolveDeskBoxExecutablePath(exePathOverride);

        if (filePath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
        {
            MergeMcpServerIntoCodexTomlFile(filePath, serverName, exePath);
            return;
        }

        if (filePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            MergeMcpServerIntoDshCordisPatchFile(filePath, serverName, exePath);
            return;
        }

        string rootProperty = "mcpServers";
        bool includeStdioType = false;
        string lowerName = Path.GetFileName(filePath).ToLowerInvariant();
        if (filePath.Contains(Path.Combine("Code", "User", "mcp.json"), StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains(Path.Combine(".vscode", "mcp.json"), StringComparison.OrdinalIgnoreCase))
        {
            rootProperty = "servers";
            includeStdioType = true;
        }

        if (File.Exists(filePath))
        {
            string raw = File.ReadAllText(filePath, Encoding.UTF8);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                using JsonDocument doc = JsonDocument.Parse(
                    raw,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("servers", out JsonElement srv) &&
                        srv.ValueKind == JsonValueKind.Object &&
                        !doc.RootElement.TryGetProperty("mcpServers", out _))
                    {
                        rootProperty = "servers";
                        includeStdioType = true;
                    }
                    else if (lowerName.Contains("claude", StringComparison.Ordinal))
                    {
                        includeStdioType = lowerName == ".claude.json";
                    }
                }
            }
        }

        MergeMcpServerIntoJsonFile(filePath, serverName, exePath, includeStdioType, rootProperty);
    }

    public static void MergeMcpServerIntoJsonFile(
        string jsonFilePath,
        string serverName,
        string commandPath,
        bool includeStdioType = false,
        string rootPropertyName = "mcpServers")
    {
        JsonDocument? existingDoc = null;
        if (File.Exists(jsonFilePath))
        {
            string raw = File.ReadAllText(jsonFilePath, Encoding.UTF8);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                existingDoc = JsonDocument.Parse(
                    raw,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                TryBackupExistingConfigFile(jsonFilePath);
            }
        }

        using (existingDoc)
        {
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(
                       ms,
                       new JsonWriterOptions
                       {
                           Indented = true,
                           Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                       }))
            {
                writer.WriteStartObject();
                bool wroteTargetMap = false;

                if (existingDoc is not null && existingDoc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (JsonProperty prop in existingDoc.RootElement.EnumerateObject())
                    {
                        if (string.Equals(prop.Name, rootPropertyName, StringComparison.Ordinal) &&
                            prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            writer.WritePropertyName(rootPropertyName);
                            writer.WriteStartObject();
                            foreach (JsonProperty serverProp in prop.Value.EnumerateObject())
                            {
                                if (string.Equals(serverProp.Name, serverName, StringComparison.OrdinalIgnoreCase))
                                {
                                    continue;
                                }
                                serverProp.WriteTo(writer);
                            }

                            WriteMcpServerEntry(writer, serverName, commandPath, includeStdioType);
                            writer.WriteEndObject();
                            wroteTargetMap = true;
                        }
                        else
                        {
                            prop.WriteTo(writer);
                        }
                    }
                }

                if (!wroteTargetMap)
                {
                    writer.WritePropertyName(rootPropertyName);
                    writer.WriteStartObject();
                    WriteMcpServerEntry(writer, serverName, commandPath, includeStdioType);
                    writer.WriteEndObject();
                }

                writer.WriteEndObject();
            }

            string updatedJson = Encoding.UTF8.GetString(ms.ToArray());
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(jsonFilePath, updatedJson);
        }
    }

    public static void MergeMcpServerIntoCodexTomlFile(
        string tomlFilePath,
        string serverName,
        string commandPath)
    {
        string sectionHeader = $"[mcp_servers.{serverName}]";
        string escapedCmd = commandPath.Replace("'", "''", StringComparison.Ordinal);
        string newSection = $"{sectionHeader}\ncommand = '{escapedCmd}'\nargs = [\"--mcp\"]\n";

        if (!File.Exists(tomlFilePath))
        {
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(tomlFilePath, newSection);
            return;
        }

        string existing = File.ReadAllText(tomlFilePath, Encoding.UTF8);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            TryBackupExistingConfigFile(tomlFilePath);
        }

        string[] lines = existing.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var sb = new StringBuilder(existing.Length + 128);
        bool inTargetSection = false;
        bool replaced = false;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (inTargetSection)
                {
                    inTargetSection = false;
                }

                if (string.Equals(trimmed, sectionHeader, StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith($"[mcp_servers.{serverName}.", StringComparison.OrdinalIgnoreCase))
                {
                    if (!replaced)
                    {
                        if (sb.Length > 0 && sb[^1] != '\n')
                        {
                            sb.Append('\n');
                        }
                        sb.Append(newSection);
                        replaced = true;
                    }
                    inTargetSection = true;
                    continue;
                }
            }

            if (!inTargetSection)
            {
                sb.Append(line).Append('\n');
            }
        }

        if (!replaced)
        {
            string current = sb.ToString().TrimEnd();
            sb.Clear();
            if (current.Length > 0)
            {
                sb.Append(current).Append("\n\n");
            }
            sb.Append(newSection);
        }

        CustomWidgetStorageFileOps.ReplaceTextFileAtomically(tomlFilePath, sb.ToString().TrimEnd() + "\n");
    }

    public static string BuildDshCordisPatchSnippet(string serverName, string commandPath)
    {
        string patchId = $"mcp-{serverName}";
        string escapedCmd = commandPath.Replace("'", "''", StringComparison.Ordinal);
        return
            $"- insert:\n" +
            $"    id: {patchId}\n" +
            $"    name: '@deepseek-ai/dsh-mcp-client'\n" +
            $"    config:\n" +
            $"      serverName: {serverName}\n" +
            $"      transport: stdio\n" +
            $"      command: '{escapedCmd}'\n" +
            $"      args:\n" +
            $"        - '--mcp'\n";
    }

    public static void MergeMcpServerIntoDshCordisPatchFile(
        string yamlFilePath,
        string serverName,
        string commandPath)
    {
        string patchId = $"mcp-{serverName}";
        string newBlock = BuildDshCordisPatchSnippet(serverName, commandPath);

        if (!File.Exists(yamlFilePath))
        {
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(yamlFilePath, newBlock);
            return;
        }

        string existing = File.ReadAllText(yamlFilePath, Encoding.UTF8);
        if (!string.IsNullOrWhiteSpace(existing))
        {
            TryBackupExistingConfigFile(yamlFilePath);
        }

        string normalized = existing.Replace("\r\n", "\n", StringComparison.Ordinal);
        string trimmedWhole = normalized.Trim();
        if (string.IsNullOrEmpty(trimmedWhole) || string.Equals(trimmedWhole, "[]", StringComparison.Ordinal))
        {
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(yamlFilePath, newBlock);
            return;
        }

        string[] lines = normalized.Split('\n');
        var segments = new List<(bool IsTopLevelItem, string Text)>();
        var current = new StringBuilder();
        bool currentIsItem = false;

        foreach (string line in lines)
        {
            bool startsTopLevelItem = line.StartsWith("- ", StringComparison.Ordinal) ||
                                      string.Equals(line.TrimEnd(), "-", StringComparison.Ordinal);
            if (startsTopLevelItem)
            {
                if (current.Length > 0)
                {
                    segments.Add((currentIsItem, current.ToString()));
                    current.Clear();
                }
                currentIsItem = true;
            }

            current.Append(line).Append('\n');
        }

        if (current.Length > 0)
        {
            segments.Add((currentIsItem, current.ToString()));
        }

        var outBuilder = new StringBuilder(normalized.Length + newBlock.Length + 16);
        bool replaced = false;

        foreach ((bool isItem, string segText) in segments)
        {
            if (isItem && IsMatchingDshMcpBlock(segText, serverName, patchId))
            {
                if (!replaced)
                {
                    if (outBuilder.Length > 0 && outBuilder[^1] != '\n')
                    {
                        outBuilder.Append('\n');
                    }
                    outBuilder.Append(newBlock);
                    replaced = true;
                }
                continue;
            }

            outBuilder.Append(segText);
        }

        if (!replaced)
        {
            string trimmedOut = outBuilder.ToString().TrimEnd();
            outBuilder.Clear();
            if (trimmedOut.Length > 0)
            {
                outBuilder.Append(trimmedOut).Append('\n');
            }
            outBuilder.Append(newBlock);
        }

        CustomWidgetStorageFileOps.ReplaceTextFileAtomically(yamlFilePath, outBuilder.ToString().TrimEnd() + "\n");
    }

    private static bool IsMatchingDshMcpBlock(string yamlSegment, string serverName, string patchId)
    {
        foreach (string rawLine in yamlSegment.Split('\n'))
        {
            string line = StripYamlInlineComment(rawLine).Trim();
            if (TryMatchYamlKeyValue(line, "id", patchId) ||
                TryMatchYamlKeyValue(line, "serverName", serverName))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasMcpServerInDshYamlFile(string yamlFilePath, string serverName)
    {
        if (!File.Exists(yamlFilePath))
        {
            return false;
        }

        try
        {
            string patchId = $"mcp-{serverName}";
            bool sawServerMapKey = false;
            bool sawMcpMarker = false;

            foreach (string rawLine in File.ReadLines(yamlFilePath, Encoding.UTF8))
            {
                string line = StripYamlInlineComment(rawLine).Trim();
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                if (TryMatchYamlKeyValue(line, "id", patchId) ||
                    TryMatchYamlKeyValue(line, "serverName", serverName))
                {
                    return true;
                }

                if (string.Equals(line, $"{serverName}:", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(line, $"'{serverName}':", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(line, $"\"{serverName}\":", StringComparison.OrdinalIgnoreCase))
                {
                    sawServerMapKey = true;
                }

                if (line.Contains("--mcp", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("dsh-mcp-client", StringComparison.OrdinalIgnoreCase))
                {
                    sawMcpMarker = true;
                }
            }

            return sawServerMapKey && sawMcpMarker;
        }
        catch
        {
            return false;
        }
    }

    private static string StripYamlInlineComment(string line)
    {
        int hash = line.IndexOf(" #", StringComparison.Ordinal);
        if (hash >= 0)
        {
            return line[..hash];
        }

        string trimmedStart = line.TrimStart();
        return trimmedStart.StartsWith('#') ? string.Empty : line;
    }

    private static bool TryMatchYamlKeyValue(string trimmedLine, string expectedKey, string expectedValue)
    {
        int colon = trimmedLine.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        string key = trimmedLine[..colon].Trim();
        if (key.StartsWith("- ", StringComparison.Ordinal))
        {
            key = key[2..].Trim();
        }

        if (!string.Equals(key, expectedKey, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string val = trimmedLine[(colon + 1)..].Trim().Trim('\'', '"');
        return string.Equals(val, expectedValue, StringComparison.OrdinalIgnoreCase);
    }

    public static bool HasMcpServerInAnyConfigFile(string filePath, string serverName = "deskbox")
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        if (filePath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
        {
            return HasMcpServerInCodexTomlFile(filePath, serverName);
        }

        if (filePath.EndsWith(".yml", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            return HasMcpServerInDshYamlFile(filePath, serverName);
        }

        return HasMcpServerInJsonFile(filePath, serverName, "mcpServers") ||
               HasMcpServerInJsonFile(filePath, serverName, "servers") ||
               HasMcpServerInJsonFile(filePath, serverName, "mcp");
    }

    public static bool HasMcpServerInCodexTomlFile(string tomlFilePath, string serverName)
    {
        if (!File.Exists(tomlFilePath))
        {
            return false;
        }

        try
        {
            string targetHeader = $"[mcp_servers.{serverName}]";
            foreach (string line in File.ReadLines(tomlFilePath, Encoding.UTF8))
            {
                string trimmed = line.Trim();
                if (string.Equals(trimmed, targetHeader, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    public static bool HasMcpServerInJsonFile(
        string jsonFilePath,
        string serverName,
        string rootPropertyName = "mcpServers")
    {
        if (!File.Exists(jsonFilePath))
        {
            return false;
        }

        try
        {
            string raw = File.ReadAllText(jsonFilePath, Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            using JsonDocument doc = JsonDocument.Parse(
                raw,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty(rootPropertyName, out JsonElement servers) &&
                servers.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty prop in servers.EnumerateObject())
                {
                    if (string.Equals(prop.Name, serverName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
        }
        catch
        {
        }

        return false;
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
                false, false, 0, sw.ElapsedMilliseconds, ex.Message);
        }

        // 2. Ping live Named Pipe host
        bool pipeOnline = false;
        int? hostPid = null;
        string widgetsRoot = CustomWidgetDataStore.GetWidgetsRootDirectory();
        bool httpEnabled = CustomWidgetStudioPreferencesStore.Shared.AgentHttpEnabled;
        int httpPort = CustomWidgetStudioPreferencesStore.Shared.AgentHttpPort;
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
                    if (resEl.TryGetProperty("httpPort", out JsonElement hpEl) && hpEl.TryGetInt32(out int hp) && hp > 0)
                    {
                        httpPort = hp;
                    }
                    if (resEl.TryGetProperty("httpError", out JsonElement heEl) && heEl.ValueKind == JsonValueKind.String)
                    {
                        errorMsg = heEl.GetString();
                    }
                }
            }
            catch
            {
            }
        }
        else
        {
            errorMsg = "Named Pipe host unreachable.";
        }

        // 3. If Local HTTP Bridge is enabled, verify authenticated loopback HTTP endpoint
        bool httpOnline = false;
        if (httpEnabled && httpPort > 0)
        {
            try
            {
                string token = CustomWidgetStudioPreferencesStore.Shared.GetOrCreateAgentHttpToken();
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{httpPort}/api/v1/health");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using HttpResponseMessage resp = await LoopbackHttpClient.SendAsync(req, cancellationToken);
                httpOnline = resp.IsSuccessStatusCode;
                if (!httpOnline && string.IsNullOrWhiteSpace(errorMsg))
                {
                    errorMsg = $"HTTP {((int)resp.StatusCode)}";
                }
            }
            catch (Exception ex)
            {
                if (string.IsNullOrWhiteSpace(errorMsg))
                {
                    errorMsg = ex.Message;
                }
            }
        }

        sw.Stop();
        return new AgentBridgeSelfTestResult(
            McpProtocolOk: mcpOk,
            McpToolCount: toolCount,
            PipeOnline: pipeOnline,
            HostPid: hostPid,
            WidgetsRoot: widgetsRoot,
            HttpEnabled: httpEnabled,
            HttpOnline: httpOnline,
            HttpPort: httpPort,
            ElapsedMs: sw.ElapsedMilliseconds,
            ErrorMessage: errorMsg);
    }

    public static string BuildHttpPushExample(int port, string bearerToken, bool powerShell)
    {
        string url = $"http://127.0.0.1:{port}/api/v1/widgets/push";
        if (powerShell)
        {
            return $$"""
                $headers = @{ Authorization = "Bearer {{bearerToken}}" }
                $body = @{
                    widgetId   = "agent-hud"
                    state      = @{ agentName = "PowerShell"; taskTitle = "CI Build"; statusText = "Running x64 tests..."; progress = "72"; isBusy = "true" }
                    collection = "events"
                    mode       = "prepend"
                    items      = @(@{ tag = "STEP"; text = "Unit tests started" })
                } | ConvertTo-Json -Depth 5
                Invoke-RestMethod -Method Post -Uri "{{url}}" -Headers $headers -ContentType "application/json; charset=utf-8" -Body $body
                """;
        }

        return $$"""
            curl -X POST "{{url}}" \
              -H "Authorization: Bearer {{bearerToken}}" \
              -H "Content-Type: application/json" \
              -d '{"widgetId":"agent-hud","state":{"agentName":"Agent","taskTitle":"Deploy","statusText":"Running...","progress":"72","isBusy":"true"},"collection":"events","mode":"prepend","items":[{"tag":"INFO","text":"Pipeline running"}]}'
            """;
    }

    public static string BuildKickoffPrompt(
        AgentPlaybookKind kind,
        string? uiLanguage = null,
        string? targetWidgetId = null,
        string? targetWidgetName = null,
        string? userIdea = null)
    {
        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(uiLanguage);
        string exePath = ResolveDeskBoxExecutablePath();
        string widgetsRoot = CustomWidgetDataStore.GetWidgetsRootDirectory();
        string skillPath = Path.Combine(widgetsRoot, "SKILL.md");

        switch (kind)
        {
            case AgentPlaybookKind.ModifyExistingWidget:
            {
                string wid = string.IsNullOrWhiteSpace(targetWidgetId) ? "<widgetId>" : targetWidgetId!;
                string wName = string.IsNullOrWhiteSpace(targetWidgetName) ? wid : targetWidgetName!;
                string manifestPath = string.IsNullOrWhiteSpace(targetWidgetId)
                    ? Path.Combine(widgetsRoot, "<widgetId>", "manifest.json")
                    : CustomWidgetDataStore.ForWidget(wid).ManifestPath;
                string goal = string.IsNullOrWhiteSpace(userIdea)
                    ? (isChinese ? "（在此补充你想修改的布局、配色或交互需求）" : "(Describe the layout, visual, or interaction changes you want here)")
                    : userIdea.Trim();

                if (isChinese)
                {
                    return $"""
                        请帮我升级改造现有的 DeskBox 桌面自定义格子「{wName}」（ID: `{wid}`）：
                        需求：{goal}

                        执行步骤：
                        1. 若已连接 `deskbox` MCP：先调用 `deskbox_get_widget_spec` 阅读组件与表达式白名单，读取 `{manifestPath}`，修改后调用 `deskbox_validate_manifest` 校验、`deskbox_upsert_widget`（传 `widgetId: "{wid}"`）热重载到桌面，并调用 `deskbox_capture_widget_preview` 截图自检深浅色排版对比度。
                        2. 若当前环境未挂载 MCP：请直接阅读 `{skillPath}`，原地编辑 `{manifestPath}`（保存后桌面 200ms 自动热重载，切勿删除同目录的 `store.json`），并运行 `"{exePath}" widget validate --file "{manifestPath}"` 与 `"{exePath}" widget preview --id {wid} --out preview.png` 完成校验与截图验收。
                        """;
                }

                return $"""
                    Please update my existing DeskBox Windows 11 desktop widget "{wName}" (ID: `{wid}`):
                    Goal: {goal}

                    Workflow:
                    1. If the `deskbox` MCP server is connected: call `deskbox_get_widget_spec` first, read `{manifestPath}`, validate changes via `deskbox_validate_manifest`, hot-reload via `deskbox_upsert_widget` (`widgetId: "{wid}"`), and visually inspect with `deskbox_capture_widget_preview`.
                    2. Without MCP: read `{skillPath}`, edit `{manifestPath}` directly (DeskBox hot-reloads within 200ms; preserve `store.json`), and verify via `"{exePath}" widget validate --file "{manifestPath}"` + `"{exePath}" widget preview --id {wid} --out preview.png`.
                    """;
            }

            case AgentPlaybookKind.PushLiveHudTelemetry:
            {
                if (isChinese)
                {
                    return $$"""
                        在接下来执行长耗时编码、构建或测试任务的过程中，请把你的关键阶段进度实时同步到我的 DeskBox 桌面「Agent 状态看板」（`widgetId: "agent-hud"`）：
                        - 若已连接 `deskbox` MCP：在关键节点调用 `deskbox_push_widget_data`，传入 `widgetId: "agent-hud"`、`state: {"agentName": "Agent", "taskTitle": "当前任务名", "statusText": "正在执行的步骤", "progress": "0-100", "isBusy": "true/false"}`，并可通过 `collection: "events"`, `mode: "prepend"`, `items: [{"tag": "STEP", "text": "阶段进展摘要"}]` 追加时间线日志（任务结束时将 `progress` 设为 `"100"`、`isBusy` 设为 `"false"`）。
                        - 若无 MCP，也可直接执行一行终端命令推送：`"{{exePath}}" widget push --id agent-hud --set taskTitle="任务名" --set statusText="执行中" --set progress=60 --prepend events:tag=STEP;text="完成阶段构建"`
                        """;
                }

                return $$"""
                    While executing multi-step coding, build, or test tasks in this session, stream your live progress to my DeskBox desktop Ambient Agent HUD (`widgetId: "agent-hud"`):
                    - With `deskbox` MCP: call `deskbox_push_widget_data` at key milestones with `widgetId: "agent-hud"`, `state: {"agentName": "Agent", "taskTitle": "...", "statusText": "...", "progress": "0-100", "isBusy": "true/false"}`, and optional `collection: "events"`, `mode: "prepend"`, `items: [{"tag": "STEP", "text": "..."}]` (set `progress: "100"`, `isBusy: "false"` when finished).
                    - Without MCP (CLI fallback): run `"{{exePath}}" widget push --id agent-hud --set taskTitle="Task" --set statusText="Running" --set progress=60 --prepend events:tag=STEP;text="Milestone complete"`
                    """;
            }

            case AgentPlaybookKind.CreateNewWidget:
            default:
            {
                string goal = string.IsNullOrWhiteSpace(userIdea)
                    ? (isChinese ? "（在此输入你想做的桌面小工具，例如：极简项目冲刺看板 / 本地端口监控 / 每日习惯打卡）" : "(Describe the desktop widget you want to build, e.g., Sprint Tracker / Port Monitor / Daily Habit Card)")
                    : userIdea.Trim();

                if (isChinese)
                {
                    return $"""
                        请帮我设计并创建一个原生 Windows 11 Fluent 风格的 DeskBox 桌面自定义格子：
                        需求：{goal}

                        执行步骤：
                        1. 若已连接 `deskbox` MCP：先调用 `deskbox_get_widget_spec` 获取完整组件、动作与表达式规范；编写 `manifest.json`（使用 `"id": "custom.<slug>"`）后调用 `deskbox_validate_manifest` 确保零报错；调用 `deskbox_upsert_widget` 创建到桌面；若使用了 `httpJson`/`rss` 数据源，调用 `deskbox_list_widgets`（传 `widgetId`）确认 `state.sourceStatus == "ok"` 且 `sampleItems` 已填充；最后调用 `deskbox_capture_widget_preview`（`mode: "expanded"`，检查深色/浅色模式真实展开态渲染截图），若有文字截断或拥挤请自动微调优化。
                        2. 若当前环境未挂载 MCP：请先阅读 `{skillPath}`，生成 `manifest.json` 后执行 `"{exePath}" widget validate --file <manifest.json>` 校验，再执行 `"{exePath}" widget apply --file <manifest.json>` 部署到桌面并用 `widget get --id <id>` 与 `widget preview --id <id> --mode expanded --out preview.png` 验收数据与截图。
                        """;
                }

                return $"""
                    Design and deploy a native Windows 11 Fluent DeskBox Custom Widget on my desktop:
                    Goal: {goal}

                    Workflow:
                    1. With `deskbox` MCP: call `deskbox_get_widget_spec` first to read the allowed component & action spec -> author `manifest.json` (`"id": "custom.<slug>"`) -> verify zero errors with `deskbox_validate_manifest` -> deploy live via `deskbox_upsert_widget` -> if using `httpJson`/`rss`, call `deskbox_list_widgets` with `widgetId` to verify `state.sourceStatus == "ok"` and `sampleItems` -> visually inspect Dark/Light expanded layout with `deskbox_capture_widget_preview` (`mode: "expanded"`) and refine spacing/truncation if needed.
                    2. Without MCP: read `{skillPath}`, save `manifest.json`, validate via `"{exePath}" widget validate --file <manifest.json>`, deploy via `"{exePath}" widget apply --file <manifest.json>`, and verify data + expanded PNG via `widget get` and `widget preview --mode expanded`.
                    """;
            }
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

    private static void TryBackupExistingConfigFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                CustomWidgetStorageFileOps.CopyFileOverwrite(filePath, filePath + ".deskbox.bak");
            }
        }
        catch
        {
            // Non-fatal best-effort backup before in-place config merge
        }
    }

    private static void WriteMcpServerEntry(
        Utf8JsonWriter writer,
        string serverName,
        string commandPath,
        bool includeStdioType)
    {
        writer.WritePropertyName(serverName);
        writer.WriteStartObject();
        if (includeStdioType)
        {
            writer.WriteString("type", "stdio");
        }
        writer.WriteString("command", commandPath);
        writer.WriteStartArray("args");
        writer.WriteStringValue("--mcp");
        writer.WriteEndArray();
        writer.WriteEndObject();
    }
}
