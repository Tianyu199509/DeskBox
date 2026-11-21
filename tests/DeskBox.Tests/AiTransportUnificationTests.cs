using System.Reflection;
using DeskBox.Services;
using Xunit;

namespace DeskBox.Tests;

public sealed class AiTransportUnificationTests
{
    [Fact]
    public void AiProviderCatalog_HasUniqueIdsAndMatchesFacadeProviders()
    {
        IReadOnlyList<CustomWidgetAiProviderPreset> catalog = AiProviderCatalog.SupportedProviders;

        Assert.NotEmpty(catalog);
        Assert.Contains(catalog, p => string.Equals(p.Id, AiProviderCatalog.CustomProviderId, StringComparison.Ordinal));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetAiProviderPreset preset in catalog)
        {
            Assert.False(string.IsNullOrWhiteSpace(preset.Id));
            Assert.False(string.IsNullOrWhiteSpace(preset.DefaultDisplayName));
            Assert.True(seen.Add(preset.Id), $"Duplicate provider id detected in AiProviderCatalog: '{preset.Id}'.");
        }

        Assert.Equal(catalog.Select(p => p.Id), DeskBoxAiService.SupportedProviders.Select(p => p.Id));
        Assert.Equal(catalog.Select(p => p.Id), CustomWidgetAiGeneratorService.SupportedProviders.Select(p => p.Id));
        Assert.Same(catalog, CustomWidgetAiGeneratorService.SupportedProviders);
        Assert.Same(catalog, DeskBoxAiService.SupportedProviders);
    }

    [Fact]
    public void AiChatTransportClient_ParseModelListJson_ParsesOpenAiAndGeminiAndFiltersNonChatModels()
    {
        const string openAiJson = """
        {
          "data": [
            { "id": "gpt-4o" },
            { "id": "text-embedding-3-large" },
            { "id": "tts-1" },
            { "id": "whisper-1" },
            { "id": "dall-e-3" },
            { "id": "gpt-4o-mini" }
          ]
        }
        """;

        List<string> openAiModels = AiChatTransportClient.ParseModelListJson(openAiJson);
        Assert.Equal(["gpt-4o", "gpt-4o-mini"], openAiModels);

        const string geminiJson = """
        {
          "models": [
            { "name": "models/gemini-2.5-flash" },
            { "name": "models/text-embedding-004" },
            { "name": "models/gemini-2.5-pro" }
          ]
        }
        """;

        List<string> geminiModels = AiChatTransportClient.ParseModelListJson(geminiJson);
        Assert.Equal(["gemini-2.5-flash", "gemini-2.5-pro"], geminiModels);

        Assert.False(AiChatTransportClient.IsNonChatModelId("gpt-4o"));
        Assert.True(AiChatTransportClient.IsNonChatModelId("tts-1"));
        Assert.True(AiChatTransportClient.IsNonChatModelId("whisper-1"));
        Assert.True(AiChatTransportClient.IsNonChatModelId("text-embedding-3-small"));
        Assert.True(AiChatTransportClient.IsNonChatModelId("dall-e-3"));
    }

    [Fact]
    public void CustomWidgetAiGeneratorService_DoesNotDeclareDuplicateTransportHelpers()
    {
        const BindingFlags DeclaredMembers =
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Static |
            BindingFlags.Instance |
            BindingFlags.DeclaredOnly;

        string[] unifiedMethods =
        [
            "SendProbeCompletionAsync",
            "ParseModelListJson",
            "IsNonChatModelId",
        ];

        Type generatorType = typeof(CustomWidgetAiGeneratorService);
        Type transportType = typeof(AiChatTransportClient);

        foreach (string methodName in unifiedMethods)
        {
            Assert.Empty(generatorType.GetMethods(DeclaredMembers).Where(m => m.Name == methodName));
            Assert.NotEmpty(transportType.GetMethods(DeclaredMembers).Where(m => m.Name == methodName));
        }
    }
}
