using System.Text.Json;
using DeskBox.Services;

namespace DeskBox.Tests;

/// <summary>
/// Pins the alignment between the in-code capability/doc Zh-En tables and the shared
/// localization catalog (CustomWidget.Cap.* / CustomWidget.ApiDocs.* / CustomWidget.Health.*).
/// Key derivation (<see cref="CustomWidgetCapabilityRegistry.SlugifyKey"/>) and value
/// round-trips are asserted so reordering or editing the tables without updating
/// Strings/*.json (or vice versa) fails loudly instead of silently falling back.
/// </summary>
public sealed class CustomWidgetApiDocsLocalizationTests
{
    [Fact]
    public void CapabilityCatalog_KeysResolveToInCodeValues()
    {
        IReadOnlyDictionary<string, string> zh = ReadLocale("zh-CN");
        IReadOnlyDictionary<string, string> en = ReadLocale("en-US");

        foreach (CustomWidgetCapabilitySpec spec in CustomWidgetCapabilityRegistry.All)
        {
            string titleKey = $"CustomWidget.Cap.{spec.Id}.Title";
            string summaryKey = $"CustomWidget.Cap.{spec.Id}.Summary";
            Assert.Equal(spec.TitleZh, zh[titleKey]);
            Assert.Equal(spec.TitleEn, en[titleKey]);
            Assert.Equal(spec.SummaryZh, zh[summaryKey]);
            Assert.Equal(spec.SummaryEn, en[summaryKey]);

            foreach (CustomWidgetApiMemberDoc member in spec.Members)
            {
                string key = $"CustomWidget.Cap.{spec.Id}.Member." +
                    $"{CustomWidgetCapabilityRegistry.SlugifyKey(member.Name)}.Desc";
                Assert.Equal(member.DescriptionZh, zh[key]);
                Assert.Equal(member.DescriptionEn, en[key]);
            }
        }
    }

    [Fact]
    public void ApiDocsKeyFamilies_ExistInCatalogWithExpectedShape()
    {
        IReadOnlyDictionary<string, string> en = ReadLocale("en-US");

        // Reference-entry docs (ComponentDocs/ActionDocs/ExpressionDocs/EndpointDocs tables)
        Assert.Equal(17, en.Keys.Count(static k =>
            k.StartsWith("CustomWidget.ApiDocs.", StringComparison.Ordinal) &&
            k.EndsWith(".Title", StringComparison.Ordinal) &&
            k.Contains(".Member.", StringComparison.Ordinal) is false));
        Assert.Equal(17, en.Keys.Count(static k =>
            k.StartsWith("CustomWidget.ApiDocs.", StringComparison.Ordinal) && k.EndsWith(".Group", StringComparison.Ordinal)));
        Assert.Equal(17, en.Keys.Count(static k =>
            k.StartsWith("CustomWidget.ApiDocs.", StringComparison.Ordinal) && k.EndsWith(".Summary", StringComparison.Ordinal) &&
            k.Contains(".Member.", StringComparison.Ordinal) is false));

        // Doc-table member descriptions
        Assert.Equal(59, en.Keys.Count(static k =>
            k.StartsWith("CustomWidget.ApiDocs.Member.", StringComparison.Ordinal) && k.EndsWith(".Desc", StringComparison.Ordinal)));

        // Manual prose + categories + snippet samples + agent placeholders
        Assert.Equal(12, en.Keys.Count(static k => k.StartsWith("CustomWidget.ApiDocs.Manual.", StringComparison.Ordinal)));
        Assert.Equal(5, en.Keys.Count(static k => k.StartsWith("CustomWidget.ApiDocs.Category.", StringComparison.Ordinal)));
        Assert.Equal(46, en.Keys.Count(static k => k.StartsWith("CustomWidget.ApiDocs.Sample.", StringComparison.Ordinal)));
        Assert.True(en.ContainsKey("CustomWidget.Agent.Placeholder.EditExisting"));
        Assert.True(en.ContainsKey("CustomWidget.Agent.Placeholder.CreateNew"));

        // Health check keys with placeholder parity against en values
        Assert.Equal(20, en.Keys.Count(static k => k.StartsWith("CustomWidget.Health.", StringComparison.Ordinal)));
    }

    [Fact]
    public void ReferenceManual_IsFullyLocalizedPerCulture()
    {
        // en manual must resolve every prose/sample key: no CJK may leak through,
        // except inside verbatim code spans (`...` / ${...}) where example
        // expressions may keep their original string literals.
        string en = CustomWidgetCapabilityRegistry.GenerateApiReferenceMarkdown("en-US");
        Assert.Contains("## Architecture & Layout Conventions", en, StringComparison.Ordinal);
        string proseOnly = System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Replace(en, @"\$\{[^}]*\}", " "),
            "`[^`]*`",
            " ");
        Assert.DoesNotContain(proseOnly, static c => c is >= '\u4e00' and <= '\u9fff');

        // zh manual keeps the canonical Simplified prose (source of truth).
        string zh = CustomWidgetCapabilityRegistry.GenerateApiReferenceMarkdown("zh-CN");
        Assert.Contains("## 架构与布局规范", zh, StringComparison.Ordinal);
        Assert.DoesNotContain("积木", zh, StringComparison.Ordinal);

        // Category display names resolve per culture through the catalog.
        Assert.Equal(
            "Data Sources",
            CustomWidgetCapabilityRegistry.GetCategoryDisplayName(CustomWidgetCapabilityCategory.DataSource, "en-US"));
        Assert.Equal(
            "数据源",
            CustomWidgetCapabilityRegistry.GetCategoryDisplayName(CustomWidgetCapabilityCategory.DataSource, "zh-CN"));
    }

    private static IReadOnlyDictionary<string, string> ReadLocale(string locale)
    {
        string? root = FindRepositoryRoot();
        Assert.NotNull(root);
        string path = Path.Combine(root, "src", "DeskBox", "Strings", locale + ".json");
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
        {
            result[prop.Name] = prop.Value.GetString() ?? string.Empty;
        }

        return result;
    }

    private static string? FindRepositoryRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "AGENTS.md")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return dir;
    }
}
