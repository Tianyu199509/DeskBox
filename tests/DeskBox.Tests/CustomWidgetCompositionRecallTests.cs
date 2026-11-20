using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

public sealed class CustomWidgetCompositionRecallTests
{
    [Theory]
    [InlineData("做一个极简的股票看板")]
    [InlineData("帮我做一个桌面时钟")]
    [InlineData("a minimal stock ticker bar")]
    [InlineData("做一个 2048 小游戏")]
    [InlineData("帮我优化这个格子的布局与交互细节")]
    public void GenericOrVisualPrompts_AlwaysRecallCompositionPacks(string prompt)
    {
        var (_, autoSpecs) = CustomWidgetCapabilityRegistry.SelectCapabilitiesForTurn(
            currentManifestJson: null,
            userPrompt: prompt);

        int compositionCount = autoSpecs.Count(s => !string.IsNullOrWhiteSpace(s.CompositionGroup));
        Assert.True(
            compositionCount >= 2,
            $"Prompt '{prompt}' recalled only {compositionCount} composition packs: [{string.Join(", ", autoSpecs.Select(s => s.Id))}]");
    }

    [Fact]
    public void GamePrompt_RecallsScriptSandbox_NotOnlyCanvas()
    {
        var (_, autoSpecs) = CustomWidgetCapabilityRegistry.SelectCapabilitiesForTurn(
            currentManifestJson: null,
            userPrompt: "做一个 2048 小游戏");

        Assert.Contains(autoSpecs, s => s.Id == "script-sandbox");
    }

    [Fact]
    public void CompositionTags_AreOnlyOnVisualPacks_AndParseFromRegistry()
    {
        var tagged = CustomWidgetCapabilityRegistry.All
            .Where(s => !string.IsNullOrWhiteSpace(s.CompositionGroup))
            .Select(s => s.Id)
            .ToList();

        Assert.Contains("visual-containers", tagged);
        Assert.Contains("rich-controls-and-shells", tagged);
        Assert.Contains("native-media-and-audio", tagged);
        Assert.DoesNotContain("forms-inputs", tagged);
        Assert.DoesNotContain("collections-lists", tagged);
        Assert.DoesNotContain("script-sandbox", tagged);
    }

    [Fact]
    public void ClipboardChannel_OptsOutOfCompositionQuota_ToStayWithinPromptDiet()
    {
        var (_, withQuota) = CustomWidgetCapabilityRegistry.SelectCapabilitiesForTurn(
            currentManifestJson: null,
            userPrompt: "Build a daily hydration tracker widget");
        var (_, withoutQuota) = CustomWidgetCapabilityRegistry.SelectCapabilitiesForTurn(
            currentManifestJson: null,
            userPrompt: "Build a daily hydration tracker widget",
            compositionQuota: 0);

        Assert.True(withQuota.Count(s => !string.IsNullOrWhiteSpace(s.CompositionGroup)) >= 2);
        Assert.True(withoutQuota.Count <= withQuota.Count);

        string systemPrompt = CustomWidgetTemplates.BuildStudioCoCreationSystemPrompt(
            uiLanguage: "en-US");
        string userPrompt = CustomWidgetTemplates.BuildStudioCoCreationUserPrompt(
            "Build a daily hydration tracker widget",
            uiLanguage: "en-US");
        Assert.InRange(systemPrompt.Length + userPrompt.Length, 1500, 32_000);
    }

    [Fact]
    public void FunctionalPrompt_StillGetsFunctionalContracts_AlongsideCompositionQuota()
    {
        var (_, autoSpecs) = CustomWidgetCapabilityRegistry.SelectCapabilitiesForTurn(
            currentManifestJson: null,
            userPrompt: "监听本地文件夹并把新增文件批量重命名后写入 json");

        Assert.Contains(autoSpecs, s => s.Id == "local-files-and-commands");
        Assert.True(autoSpecs.Count(s => !string.IsNullOrWhiteSpace(s.CompositionGroup)) >= 1);
    }
}