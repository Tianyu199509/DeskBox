using System.Text;
using System.Text.Json;
using DeskBox.Services;

namespace DeskBox.Tests;

public sealed class AgentConnectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "deskbox-connection-" + Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(_root, "home");

    public AgentConnectionTests()
    {
        Directory.CreateDirectory(Home);
    }

    [Fact]
    public void McpCommandGeneration_DistinguishesStoreAliasFromDirectAndSettingsHost()
    {
        // 1. Store / MSIX packaged channel always uses the AppExecutionAlias deskbox.exe
        Assert.Equal(
            "deskbox.exe",
            CustomWidgetAgentConnectorService.ResolveMcpCommandExecutable(isPackagedOverride: true));
        Assert.Equal(
            "deskbox.exe --mcp",
            CustomWidgetAgentConnectorService.FormatMcpCommandLine(isPackagedOverride: true));

        // 2. Direct / Debug unpackaged channel uses quoted full path
        const string directExe = @"C:\Program Files\DeskBox\DeskBox.exe";
        Assert.Equal(
            directExe,
            CustomWidgetAgentConnectorService.ResolveMcpCommandExecutable(
                isPackagedOverride: false,
                currentProcessPathOverride: directExe));
        Assert.Equal(
            "\"C:\\Program Files\\DeskBox\\DeskBox.exe\" --mcp",
            CustomWidgetAgentConnectorService.FormatMcpCommandLine(
                isPackagedOverride: false,
                currentProcessPathOverride: directExe));

        // 3. Companion settings host (DeskBox.Settings.exe) resolves to sibling DeskBox.exe
        const string settingsExe = @"D:\Apps\DeskBox 正式版\DeskBox.Settings.exe";
        const string expectedSibling = @"D:\Apps\DeskBox 正式版\DeskBox.exe";
        Assert.Equal(
            expectedSibling,
            CustomWidgetAgentConnectorService.ResolveDeskBoxExecutablePath(
                currentProcessPathOverride: settingsExe));
        Assert.Equal(
            $"\"{expectedSibling}\" --mcp",
            CustomWidgetAgentConnectorService.FormatMcpCommandLine(
                isPackagedOverride: false,
                currentProcessPathOverride: settingsExe));
    }

    [Theory]
    [InlineData("C:\\Users\\O'Brien\\DeskBox.exe")]
    [InlineData("C:\\测试\\😀\\DeskBox.exe")]
    public void SetupPromptAndSnippets_HandleSpecialPathsAndDoNotModifyThirdPartyFiles(string command)
    {
        string codexConfig = Path.Combine(Home, ".codex", "config.toml");
        Directory.CreateDirectory(Path.GetDirectoryName(codexConfig)!);
        File.WriteAllText(codexConfig, "model = \"gpt-5-codex\"\n");
        string[] before = Snapshot();

        string promptZh = CustomWidgetAgentConnectorService.BuildMcpSetupPrompt("zh-CN", exePathOverride: command);
        Assert.Contains($"\"{command}\" --mcp", promptZh, StringComparison.Ordinal);
        Assert.Contains("deskbox_get_widget_spec", promptZh, StringComparison.Ordinal);
        Assert.DoesNotContain("dbx_", promptZh, StringComparison.Ordinal);

        string promptStoreEn = CustomWidgetAgentConnectorService.BuildMcpSetupPrompt(
            "en-US",
            isPackagedOverride: true);
        Assert.Contains("deskbox.exe --mcp", promptStoreEn, StringComparison.Ordinal);
        Assert.Contains("deskbox_get_widget_spec", promptStoreEn, StringComparison.Ordinal);

        using (JsonDocument stdDoc = JsonDocument.Parse(
                   CustomWidgetAgentConnectorService.BuildMcpSnippet(McpConfigFormat.StandardJson, command)))
        {
            JsonElement srv = stdDoc.RootElement.GetProperty("mcpServers").GetProperty("deskbox");
            Assert.Equal(command, srv.GetProperty("command").GetString());
            Assert.Equal("--mcp", Assert.Single(srv.GetProperty("args").EnumerateArray()).GetString());
        }

        using (JsonDocument vsDoc = JsonDocument.Parse(
                   CustomWidgetAgentConnectorService.BuildMcpSnippet(McpConfigFormat.VsCodeJson, command)))
        {
            JsonElement srv = vsDoc.RootElement.GetProperty("servers").GetProperty("deskbox");
            Assert.Equal("stdio", srv.GetProperty("type").GetString());
            Assert.Equal(command, srv.GetProperty("command").GetString());
        }

        string toml = CustomWidgetAgentConnectorService.BuildMcpSnippet(McpConfigFormat.CodexToml, command);
        Assert.Contains("[mcp_servers.deskbox]", toml, StringComparison.Ordinal);
        Assert.Contains("args = [\"--mcp\"]", toml, StringComparison.Ordinal);

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public async Task SkillPackageAndWorkspaceDocs_WriteUtf8WithoutBomAndHaveValidFrontmatter()
    {
        Assert.True(AgentSkillPackage.HasValidFrontmatter(AgentSkillPackage.Markdown));
        Assert.Contains("deskbox_get_widget_spec", AgentSkillPackage.Markdown, StringComparison.Ordinal);
        Assert.Contains("STAGE-BY-DEFAULT", AgentSkillPackage.Markdown, StringComparison.Ordinal);

        string workspace = Path.Combine(Home, "workspace");
        await CustomWidgetTemplates.EnsureAiAgentWorkspaceDocsAsync(workspace);
        byte[] skillBytes = await File.ReadAllBytesAsync(Path.Combine(workspace, "SKILL.md"));
        Assert.Equal(new byte[] { 45, 45, 45 }, skillBytes.Take(3));
        using JsonDocument schema = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(workspace, "manifest.schema.json")));
        Assert.Equal(JsonValueKind.Object, schema.RootElement.ValueKind);
    }

    [Fact]
    public async Task McpInitialization_ProvidesRuntimeGuidanceWithoutAnInstalledSkill()
    {
        string? response = await DeskBoxAgentCliEntry.HandleMcpRpcMessageAsync(
            "\uFEFF" + """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26"}}""");
        using JsonDocument doc = JsonDocument.Parse(response!);
        string? instructions = doc.RootElement.GetProperty("result").GetProperty("instructions").GetString();
        Assert.Contains("deskbox_get_widget_spec", instructions, StringComparison.Ordinal);
        Assert.Contains("confirmation", instructions, StringComparison.Ordinal);
        Assert.Contains("stageId", instructions, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramMainAndManifest_InterceptMcpBeforeMutexAndDeclareStoreExecutionAlias()
    {
        string programCs = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Program.cs"));
        int interceptIdx = programCs.IndexOf("DeskBoxAgentCliEntry.TryIntercept", StringComparison.Ordinal);
        int appStartIdx = programCs.IndexOf("Application.Start", StringComparison.Ordinal);
        Assert.True(interceptIdx > 0 && appStartIdx > interceptIdx,
            "DeskBoxAgentCliEntry.TryIntercept must run before Application.Start and single-instance mutex acquisition.");

        string manifestXml = File.ReadAllText(TestPaths.FromRepository("src/DeskBox/Package.appxmanifest"));
        Assert.Contains("Category=\"windows.appExecutionAlias\"", manifestXml, StringComparison.Ordinal);
        Assert.Contains("<uap5:ExecutionAlias Alias=\"deskbox.exe\" />", manifestXml, StringComparison.Ordinal);
    }

    private string[] Snapshot() => Directory.GetFiles(_root, "*", SearchOption.AllDirectories)
        .OrderBy(p => p, StringComparer.Ordinal)
        .Select(p => p + "|" + File.GetLastWriteTimeUtc(p).Ticks + "|" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p))))
        .ToArray();

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
