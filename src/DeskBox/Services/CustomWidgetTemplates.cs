using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Models;

namespace DeskBox.Services;

public sealed record CustomWidgetPresetInfo(
    string Id,
    string NameKey,
    string DescriptionKey,
    string DefaultTitle,
    string IconGlyph,
    int DefaultWidth,
    int DefaultHeight,
    string BadgeColorHex = "#3B82F6",
    string FeatureTagKey = "");

public sealed record CustomWidgetStarterSpec(
    string Id,
    string LabelKey,
    string PromptKey,
    string Emoji,
    string ManifestJson);

/// <summary>
/// Built-in Custom Widget preset templates, AI Agent Skill (<c>SKILL.md</c> +
/// <c>manifest.schema.json</c>) exporter, AI prompt builders, and <c>.dbbox</c>
/// template package import/export service.
/// </summary>
public static class CustomWidgetTemplates
{
    public const string WorkLoggerTemplateId = "work-logger";
    public const string SystemPulseTemplateId = "sys-pulse";
    public const string PomodoroTemplateId = "pomodoro";
    public const string CountdownTemplateId = "countdown";
    public const string AiPulseTemplateId = "ai-pulse";
    public const string MilestoneTrackerTemplateId = "milestone-tracker";
    public const string QuickSnippetsTemplateId = "quick-snippets";
    public const string AgentHudTemplateId = "agent-hud";
    public const string HydrationStarterId = "hydration";
    public const string ReleaseChecklistStarterId = "release-checklist";
    public const string MiniHardwareStarterId = "mini-hardware";
    public const string FileDropStarterId = "file-basket";
    public const string NewsRssStarterId = "news-rss";
    public const string AiInboxStarterId = "ai-inbox";

    public static IReadOnlyList<CustomWidgetPresetInfo> Presets { get; } =
    [
        new(
            SystemPulseTemplateId,
            "CustomWidget.Template.SystemPulse.Name",
            "CustomWidget.Template.SystemPulse.Description",
            "系统监控",
            "\uE9D9",
            336,
            436,
            "#2563EB",
            "CustomWidget.Template.SystemPulse.Tag"),
        new(
            PomodoroTemplateId,
            "CustomWidget.Template.Pomodoro.Name",
            "CustomWidget.Template.Pomodoro.Description",
            "番茄时钟",
            "\uE916",
            276,
            312,
            "#EA580C",
            "CustomWidget.Template.Pomodoro.Tag"),
        new(
            CountdownTemplateId,
            "CustomWidget.Template.Countdown.Name",
            "CustomWidget.Template.Countdown.Description",
            "倒数日",
            "\uE787",
            308,
            336,
            "#0284C7",
            "CustomWidget.Template.Countdown.Tag"),
        new(
            AiPulseTemplateId,
            "CustomWidget.Template.AiPulse.Name",
            "CustomWidget.Template.AiPulse.Description",
            "AI 监控",
            "\uE950",
            324,
            320,
            "#0284C7",
            "CustomWidget.Template.AiPulse.Tag")
    ];

    public static bool IsLegacyOfficialDefaultEmoji(string? templateOrManifestId, string? emoji)
    {
        if (string.IsNullOrWhiteSpace(templateOrManifestId) || string.IsNullOrWhiteSpace(emoji))
        {
            return false;
        }

        string trimmedId = templateOrManifestId.Trim();
        string normalizedId = trimmedId.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase)
            ? trimmedId["deskbox.".Length..]
            : trimmedId;
        string trimmedEmoji = emoji.Trim();

        return normalizedId.ToLowerInvariant() switch
        {
            SystemPulseTemplateId => trimmedEmoji == "📊",
            PomodoroTemplateId => trimmedEmoji == "🍅",
            CountdownTemplateId => trimmedEmoji == "⏳",
            AiPulseTemplateId => trimmedEmoji == "🤖",
            _ => false
        };
    }

    public static CustomWidgetPresetInfo? MatchPresetOrNull(string? templateOrManifestId)
    {
        if (string.IsNullOrWhiteSpace(templateOrManifestId))
        {
            return null;
        }

        string trimmed = templateOrManifestId.Trim();
        string normalized = trimmed.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase)
            ? trimmed["deskbox.".Length..]
            : trimmed;
        return Presets.FirstOrDefault(p =>
            string.Equals(p.Id, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static CustomWidgetPresetInfo GetPreset(string? templateId) =>
        MatchPresetOrNull(templateId) ?? Presets[0];

    public static bool IsSegoeFluentGlyph(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = CustomWidgetManifestParser.NormalizeGlyph(value);
        return normalized.Length == 1 && normalized[0] is >= '\uE000' and <= '\uF8FF';
    }

    public static bool IsEmojiIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = CustomWidgetManifestParser.NormalizeGlyph(value);
        return !IsSegoeFluentGlyph(normalized) && WidgetTitleIconCustomization.IsValidEmoji(normalized);
    }

    /// <summary>
    /// Rewrites top-level <c>"icon"</c> (and static <c>"capsule.glyph"</c>) inside a raw
    /// <c>manifest.json</c> string without reflection so NativeAOT builds stay warning-free.
    /// </summary>
    public static string WithUpdatedIconJson(string rawManifestJson, string newIcon)
    {
        if (string.IsNullOrWhiteSpace(rawManifestJson) || string.IsNullOrWhiteSpace(newIcon))
        {
            return rawManifestJson;
        }

        string normalizedIcon = newIcon.Trim();
        using JsonDocument doc = JsonDocument.Parse(
            rawManifestJson,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return rawManifestJson;
        }

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
            bool wroteIcon = false;
            bool hasExistingIconProp = doc.RootElement.TryGetProperty("icon", out _);

            foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(prop.Name, "icon", StringComparison.Ordinal))
                {
                    writer.WriteString("icon", normalizedIcon);
                    wroteIcon = true;
                    continue;
                }

                if (string.Equals(prop.Name, "capsule", StringComparison.Ordinal) &&
                    prop.Value.ValueKind == JsonValueKind.Object)
                {
                    writer.WritePropertyName("capsule");
                    writer.WriteStartObject();
                    bool wroteCapsuleGlyph = false;
                    foreach (JsonProperty capProp in prop.Value.EnumerateObject())
                    {
                        if (string.Equals(capProp.Name, "glyph", StringComparison.Ordinal))
                        {
                            string existingGlyph = capProp.Value.ValueKind == JsonValueKind.String
                                ? capProp.Value.GetString() ?? string.Empty
                                : string.Empty;
                            if (existingGlyph.Contains("${", StringComparison.Ordinal))
                            {
                                capProp.WriteTo(writer);
                            }
                            else
                            {
                                writer.WriteString("glyph", normalizedIcon);
                            }
                            wroteCapsuleGlyph = true;
                            continue;
                        }

                        capProp.WriteTo(writer);
                    }

                    if (!wroteCapsuleGlyph)
                    {
                        writer.WriteString("glyph", normalizedIcon);
                    }

                    writer.WriteEndObject();
                    continue;
                }

                prop.WriteTo(writer);

                if (!hasExistingIconProp &&
                    !wroteIcon &&
                    (string.Equals(prop.Name, "description", StringComparison.Ordinal) ||
                     string.Equals(prop.Name, "name", StringComparison.Ordinal)))
                {
                    writer.WriteString("icon", normalizedIcon);
                    wroteIcon = true;
                }
            }

            if (!wroteIcon)
            {
                writer.WriteString("icon", normalizedIcon);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static string ResolveEffectiveTemplateJson(string? templateId)
    {
        if (CustomWidgetRemoteCatalogService.Shared.TryGetRemoteTemplateJson(templateId, out string? remoteJson) &&
            !string.IsNullOrWhiteSpace(remoteJson))
        {
            return remoteJson;
        }

        return GetTemplateJson(templateId);
    }

    public static string GetTemplateJson(string? templateId) =>
        (templateId?.StartsWith("deskbox.", StringComparison.OrdinalIgnoreCase) == true
            ? templateId["deskbox.".Length..]
            : templateId)?.ToLowerInvariant() switch
        {
            SystemPulseTemplateId => SystemPulseManifestJson,
            PomodoroTemplateId => PomodoroManifestJson,
            CountdownTemplateId => CountdownManifestJson,
            AiPulseTemplateId => AiPulseManifestJson,
            MilestoneTrackerTemplateId => MilestoneTrackerManifestJson,
            QuickSnippetsTemplateId => QuickSnippetsManifestJson,
            WorkLoggerTemplateId => WorkLoggerManifestJson,
            AgentHudTemplateId => AgentHudStarterManifestJson,
            HydrationStarterId => HydrationStarterManifestJson,
            AiInboxStarterId => AiInboxStarterManifestJson,
            ReleaseChecklistStarterId => ReleaseChecklistStarterManifestJson,
            MiniHardwareStarterId => MiniHardwareStarterManifestJson,
            FileDropStarterId => FileDropStarterManifestJson,
            NewsRssStarterId => Localized.IsActiveChinese
                ? NewsRssStarterManifestJson
                : NewsRssStarterManifestJson.Replace(
                    "https://sspai.com/feed",
                    "https://hnrss.org/frontpage",
                    StringComparison.Ordinal),
            _ => SystemPulseManifestJson
        };

    public static IReadOnlyList<CustomWidgetStarterSpec> StudioStarters { get; } =
    [
        new(
            HydrationStarterId,
            "CustomWidget.AiWizard.Chip.Water.Label",
            "CustomWidget.AiWizard.Chip.Water.Prompt",
            "💧",
            HydrationStarterManifestJson),
        new(
            AiInboxStarterId,
            "CustomWidget.Studio.Starter.AiInbox.Label",
            "CustomWidget.Studio.Starter.AiInbox.Prompt",
            "⚡",
            AiInboxStarterManifestJson),
        new(
            ReleaseChecklistStarterId,
            "CustomWidget.Studio.Starter.Checklist.Label",
            "CustomWidget.Studio.Starter.Checklist.Prompt",
            "✅",
            ReleaseChecklistStarterManifestJson),
        new(
            NewsRssStarterId,
            "CustomWidget.Studio.Starter.NewsRss.Label",
            "CustomWidget.Studio.Starter.NewsRss.Prompt",
            "📰",
            NewsRssStarterManifestJson),
        new(
            AgentHudTemplateId,
            "CustomWidget.Studio.Starter.AgentHud.Label",
            "CustomWidget.Studio.Starter.AgentHud.Prompt",
            "🤖",
            AgentHudStarterManifestJson),
        new(
            MiniHardwareStarterId,
            "CustomWidget.AiWizard.Chip.Monitor.Label",
            "CustomWidget.AiWizard.Chip.Monitor.Prompt",
            "📊",
            MiniHardwareStarterManifestJson),
        new(
            FileDropStarterId,
            "CustomWidget.AiWizard.Chip.Links.Label",
            "CustomWidget.AiWizard.Chip.Links.Prompt",
            "🗂️",
            FileDropStarterManifestJson)
    ];

    public static async Task ExportPackageAsync(string widgetId, string destinationDbboxPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDbboxPath);
        _ = await CustomWidgetPackageService.ExportSharePackageAsync(widgetId, destinationDbboxPath);
    }

    public static Task<(CustomWidgetManifest Manifest, string RawJson)> ReadPackageOrJsonAsync(string filePath) =>
        CustomWidgetPackageService.ReadPackageOrJsonAsync(filePath);

    /// <summary>
    /// Writes the AI Agent <c>SKILL.md</c> and <c>manifest.schema.json</c> into
    /// the widgets workspace directory (and optionally a user-chosen target folder)
    /// so external AI Agents (Codex, Claude Code, Cursor, etc.) can build and hot-reload
    /// DeskBox Custom Widgets immediately.
    /// </summary>
    public static async Task<string> EnsureAiAgentWorkspaceDocsAsync(
        string? targetDirectory = null,
        bool includeWorkspaceAgentGuides = true)
    {
        string workspaceDir = string.IsNullOrWhiteSpace(targetDirectory)
            ? CustomWidgetDataStore.GetWidgetsRootDirectory()
            : targetDirectory!;
        Directory.CreateDirectory(workspaceDir);

        string skillPath = Path.Combine(workspaceDir, "SKILL.md");
        string apiRefPath = Path.Combine(workspaceDir, "API_REFERENCE.md");
        string schemaPath = Path.Combine(workspaceDir, "manifest.schema.json");

        await File.WriteAllTextAsync(skillPath, AiAgentSkillMarkdown, Encoding.UTF8);
        await File.WriteAllTextAsync(
            apiRefPath,
            CustomWidgetCapabilityRegistry.GenerateApiReferenceMarkdown(Localized.IsActiveChinese),
            Encoding.UTF8);
        await File.WriteAllTextAsync(schemaPath, ManifestJsonSchema, Encoding.UTF8);
        await CustomWidgetSkillCatalog.WriteReferenceDocsAsync(workspaceDir);

        if (includeWorkspaceAgentGuides)
        {
            string exePath = CustomWidgetAgentConnectorService.ResolveDeskBoxExecutablePath();
            var rootGuide = new StringBuilder();
            rootGuide.AppendLine("# DeskBox Custom Widgets Workspace");
            rootGuide.AppendLine();
            rootGuide.AppendLine("This directory is DeskBox's live Windows 11 desktop widget workspace.");
            rootGuide.AppendLine("Any AI coding agent (Claude Code, Codex, Cursor, Trae, Windsurf, VS Code, Antigravity, Aider, OpenCode, DeepSeek Harness) can create or edit widgets here **even without MCP configured**.");
            rootGuide.AppendLine();
            rootGuide.AppendLine("## Workspace Layout & Zero-Config Hot Reload");
            rootGuide.AppendLine("- Full component, expression & UX specification: `SKILL.md` (read this first!).");
            rootGuide.AppendLine("- Complete 26-module capability & token API reference with configuration snippets: `API_REFERENCE.md`.");
            rootGuide.AppendLine("- Modular WinUI component & pattern recipes: `references/*.md` (`forms-and-inputs.md`, `metrics-and-microcharts.md`, `collections-and-filtering.md`, `live-sources-and-drops.md`, `ai-micro-workflows.md`).");
            rootGuide.AppendLine("- JSON Schema (Draft 2020-12): `manifest.schema.json`.");
            rootGuide.AppendLine("- Each desktop widget lives in `<widgetId>/manifest.json` (plus user runtime data in `<widgetId>/store.json` — never overwrite `store.json` unless requested; read/update `<widgetId>/brief.md` to stay aligned with the user's confirmed design brief and rejected ideas).");
            rootGuide.AppendLine("- Saving an existing `<widgetId>/manifest.json` hot-reloads the live Windows 11 desktop widget within **200ms**.");
            rootGuide.AppendLine();
            rootGuide.AppendLine("## DeskBox Native CLI (Works in Any Terminal)");
            rootGuide.Append("- Check live bridge status: `\"").Append(exePath).AppendLine("\" widget status`");
            rootGuide.Append("- List all desktop widgets: `\"").Append(exePath).AppendLine("\" widget list`");
            rootGuide.Append("- Validate a manifest: `\"").Append(exePath).AppendLine("\" widget validate --file <path/to/manifest.json>`");
            rootGuide.Append("- Create or update live widget on desktop: `\"").Append(exePath).AppendLine("\" widget apply --file <path/to/manifest.json>` (add `--id <widgetId>` to update specific instance)");
            rootGuide.Append("- Capture real WinUI 3 PNG screenshot for visual QA: `\"").Append(exePath).AppendLine("\" widget preview --id <widgetId> --out preview.png`");
            rootGuide.Append("- Push real-time state/events: `\"").Append(exePath).AppendLine("\" widget push --id agent-hud --set taskTitle=\"Build\" --set statusText=\"Running\" --set progress=65`");

            string rootMd = rootGuide.ToString();
            await File.WriteAllTextAsync(Path.Combine(workspaceDir, "AGENTS.md"), rootMd, Encoding.UTF8);
            await File.WriteAllTextAsync(Path.Combine(workspaceDir, "CLAUDE.md"), rootMd, Encoding.UTF8);
        }

        return workspaceDir;
    }

    /// <summary>
    /// Prepares an isolated widget workspace directory for external coding agents
    /// (Cursor, VS Code, Claude Code, Codex, Antigravity) with live <c>manifest.schema.json</c>,
    /// <c>SKILL.md</c>, <c>AGENTS.md</c>, and <c>CLAUDE.md</c> so saving <c>manifest.json</c>
    /// triggers instant 200ms hot-reload on the desktop and in Studio.
    /// </summary>
    public static async Task<string> EnsureAgentWorkspacePreparedAsync(
        string widgetDirectory,
        string? widgetId = null,
        string? initialIdea = null)
    {
        Directory.CreateDirectory(widgetDirectory);
        await EnsureAiAgentWorkspaceDocsAsync(widgetDirectory, includeWorkspaceAgentGuides: false);

        string exePath = Environment.ProcessPath ?? "DeskBox.exe";
        if (exePath.EndsWith("DeskBox.Settings.exe", StringComparison.OrdinalIgnoreCase))
        {
            string siblingExe = Path.Combine(Path.GetDirectoryName(exePath) ?? string.Empty, "DeskBox.exe");
            if (File.Exists(siblingExe))
            {
                exePath = siblingExe;
            }
        }

        var agentInstructions = new StringBuilder();
        agentInstructions.AppendLine("# DeskBox Custom Widget Live Workspace");
        agentInstructions.AppendLine();
        agentInstructions.AppendLine("- Primary file to edit: `manifest.json` (in this directory).");
        agentInstructions.AppendLine("- Shared design memory (if present): `brief.md` (read before editing; keep confirmed requirements & rejected ideas in sync).");
        agentInstructions.AppendLine("- Full component & expression reference: `SKILL.md` + `API_REFERENCE.md` + `references/*.md`.");
        agentInstructions.AppendLine("- JSON Schema for validation & IntelliSense: `manifest.schema.json`.");
        agentInstructions.AppendLine("- Saving `manifest.json` hot-reloads the DeskBox widget within 200ms.");
        if (!string.IsNullOrWhiteSpace(widgetId))
        {
            agentInstructions.Append("- Bound Widget ID: `").Append(widgetId).AppendLine("`");
            agentInstructions.Append("- Validate via CLI: `\"").Append(exePath).AppendLine("\" widget validate --file manifest.json`");
            agentInstructions.Append("- Capture visual PNG preview: `\"").Append(exePath)
                .Append("\" widget preview --id ").Append(widgetId).AppendLine(" --out preview.png`");
            agentInstructions.Append("- Push live state from scripts/agents: `\"").Append(exePath)
                .Append("\" widget push --id ").Append(widgetId).AppendLine(" --set status=\"Running\"`");
        }
        if (!string.IsNullOrWhiteSpace(initialIdea))
        {
            agentInstructions.AppendLine();
            agentInstructions.AppendLine("## Current User Goal");
            agentInstructions.AppendLine(initialIdea.Trim());
        }

        string mdContent = agentInstructions.ToString();
        await File.WriteAllTextAsync(Path.Combine(widgetDirectory, "AGENTS.md"), mdContent, Encoding.UTF8);
        await File.WriteAllTextAsync(Path.Combine(widgetDirectory, "CLAUDE.md"), mdContent, Encoding.UTF8);
        return widgetDirectory;
    }

    private static string StripReasoningBlocks(string text)
    {
        int start = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        while (start >= 0)
        {
            int end = text.IndexOf("</think>", start + 7, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                break;
            }

            text = string.Concat(text.AsSpan(0, start), text.AsSpan(end + 8));
            start = text.IndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        }

        return text.Trim();
    }

    /// <summary>
    /// Extracts a JSON object from raw clipboard text, automatically stripping
    /// Markdown <c>```json ... ```</c> code fences, <c>&lt;think&gt;</c> blocks, or surrounding prose.
    /// </summary>
    public static string ExtractJsonFromClipboardText(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return string.Empty;
        }

        string trimmed = StripReasoningBlocks(rawText.Trim());

        // 1. Check for Markdown fenced code blocks (```json ... ``` or ``` ... ```)
        int fenceStart = trimmed.IndexOf("```", StringComparison.Ordinal);
        while (fenceStart >= 0)
        {
            int lineBreak = trimmed.IndexOf('\n', fenceStart + 3);
            if (lineBreak < 0)
            {
                break;
            }

            int fenceEnd = trimmed.IndexOf("```", lineBreak + 1, StringComparison.Ordinal);
            if (fenceEnd > lineBreak)
            {
                string candidate = trimmed[(lineBreak + 1)..fenceEnd].Trim();
                string extractedInFence = ExtractBalancedJsonObject(candidate);
                if (!string.IsNullOrWhiteSpace(extractedInFence))
                {
                    return extractedInFence;
                }
            }

            fenceStart = fenceEnd >= 0
                ? trimmed.IndexOf("```", fenceEnd + 3, StringComparison.Ordinal)
                : -1;
        }

        // 2. Fallback: find outermost balanced { ... } object in plain or mixed text
        return ExtractBalancedJsonObject(trimmed);
    }

    private static string ExtractBalancedJsonObject(string text)
    {
        int firstBrace = text.IndexOf('{');
        if (firstBrace < 0)
        {
            return string.Empty;
        }

        int depth = 0;
        bool inString = false;
        bool escaped = false;
        for (int i = firstBrace; i < text.Length; i++)
        {
            char c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[firstBrace..(i + 1)].Trim();
                }
            }
        }

        // If unbalanced (e.g. truncated), return from firstBrace to last '}' if any so parser emits a clear diagnostic
        int lastBrace = text.LastIndexOf('}');
        return lastBrace > firstBrace
            ? text[firstBrace..(lastBrace + 1)].Trim()
            : string.Empty;
    }

    /// <summary>
    /// Builds the system prompt for direct OpenAI-compatible API generation, combining
    /// target language rules and optional remote OTA prompt overrides (<c>prompts.v1.json</c>).
    /// </summary>
    public static string BuildDirectApiSystemPrompt(
        string? uiLanguage = null,
        CustomWidgetEffectivePromptPack? promptPack = null)
    {
        CustomWidgetEffectivePromptPack pack = promptPack ?? CustomWidgetRemotePromptService.Shared.GetEffectivePromptPack();
        string targetLanguage = CustomWidgetRemotePromptService.DescribeTargetLanguage(uiLanguage);

        if (!string.IsNullOrWhiteSpace(pack.SystemPromptOverride))
        {
            var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["TARGET_LANGUAGE"] = targetLanguage,
                ["EXTRA_DESIGN_RULES"] = pack.ExtraDesignRules
            };
            string rendered = CustomWidgetRemotePromptService.ReplaceNamedTokens(pack.SystemPromptOverride, tokens).Trim();
            if (!string.IsNullOrWhiteSpace(pack.ExtraDesignRules) &&
                !pack.SystemPromptOverride.Contains("{{EXTRA_DESIGN_RULES}}", StringComparison.Ordinal) &&
                !rendered.Contains(pack.ExtraDesignRules, StringComparison.Ordinal))
            {
                rendered = $"{rendered}\n{pack.ExtraDesignRules}";
            }

            return rendered;
        }

        var sb = new StringBuilder();
        sb.Append("You are DeskBox Studio's native WinUI 3 declarative widget architect. ")
          .Append("Return ONLY a single valid JSON object (wrapped in ```json ... ``` or raw JSON) conforming strictly to DeskBox's schemaVersion 1 specification. ")
          .Append("Never invent unsupported node types or action types (runtime AI micro-tasks use action \"ai.run\", in-memory text ops use \"text.transform\", and plain-text file drops expose ${$drop.text}). ")
          .Append("Respect Windows 11 Fluent 2 visual hierarchy: NEVER add a duplicate inner widget-title header bar at the top of `view` (the host frame already renders `icon`, `name`, collapse, and menu controls); keep sibling Bento sections visually uniform; calibrate `defaultSize.height` to fit all initial sections without clipping (use `Tabs` when combining 3+ panels); and never add redundant submit/apply buttons next to live `Slider` or `Toggle` controls. ")
          .Append("Copy language priority: (1) explicit language requested by the user > (2) natural language of the user's prompt > (3) current DeskBox UI language (")
          .Append(targetLanguage)
          .Append("). Keep user widgets as clean single-language strings (omit \"i18n\" unless multi-language support is explicitly requested).");

        if (!string.IsNullOrWhiteSpace(pack.ExtraDesignRules))
        {
            sb.Append('\n').Append(pack.ExtraDesignRules);
        }

        return sb.ToString();
    }

    /// <summary>
    /// Builds a self-contained prompt for any conversational AI (Doubao, DeepSeek,
    /// ChatGPT, Claude, etc.) to generate or modify a DeskBox Custom Widget <c>manifest.json</c>.
    /// </summary>
    public static string BuildChatAiPrompt(
        string userIdea,
        string? existingManifestJson = null,
        string? preferredEmoji = null,
        string? uiLanguage = null,
        CustomWidgetEffectivePromptPack? promptPack = null,
        string? briefMarkdown = null,
        string? targetNodePath = null,
        IReadOnlyList<string>? pinnedCapabilityIds = null)
    {
        CustomWidgetEffectivePromptPack pack = promptPack ?? CustomWidgetRemotePromptService.Shared.GetEffectivePromptPack();
        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(uiLanguage);
        string targetLanguage = CustomWidgetRemotePromptService.DescribeTargetLanguage(uiLanguage);
        string normalizedIdea = string.IsNullOrWhiteSpace(userIdea)
            ? (isChinese ? "做一个实用、简洁美观的桌面效率工具格子" : "Build a clean, practical desktop productivity widget")
            : userIdea.Trim();
        string? customEmoji = IsEmojiIcon(preferredEmoji) ? preferredEmoji!.Trim() : null;
        string demoEmoji = customEmoji ?? "💧";

        (IReadOnlyList<CustomWidgetCapabilitySpec> pinnedSpecs, IReadOnlyList<CustomWidgetCapabilitySpec> autoSpecs) =
            CustomWidgetCapabilityRegistry.SelectCapabilitiesForTurn(
                existingManifestJson,
                normalizedIdea,
                briefMarkdown,
                pinnedCapabilityIds);
        string tier0DirectoryBlock = CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock(isChinese);
        string selectedCapsulesBlock = CustomWidgetCapabilityRegistry.FormatSelectedCapsulesPromptBlock(pinnedSpecs, autoSpecs);
        string pinnedOnlyBlock = pinnedSpecs.Count > 0
            ? CustomWidgetCapabilityRegistry.FormatSelectedCapsulesPromptBlock(pinnedSpecs, [])
            : string.Empty;

        if (isChinese)
        {
            string taskIntro = string.IsNullOrWhiteSpace(existingManifestJson)
                ? "请根据我的需求，为我设计并输出一份完整的 DeskBox 自定义格子 `manifest.json` 配置文件。"
                : "请基于我现有的格子 `manifest.json` 配置，按我的修改需求进行升级调整，并输出修改后的完整 `manifest.json`（请尽量保留原有 state 与 collections 字段名以免丢失已有记录）。";
            string iconRule = customEmoji is not null
                ? $"4. **指定专属图标**：请将顶层 `\"icon\"` 与 `\"capsule.glyph\"` 设为 `\"{customEmoji}\"`。"
                : "4. **独特 Emoji 图标**：请根据格子主题精心挑选一个高辨识度的单个 Unicode Emoji 作为顶层 `\"icon\"` 与 `\"capsule.glyph\"`，不要使用千篇一律的默认符号。";
            string languageRule =
                $"6. **界面文案语言规则**：按优先级决定所有可见文案（`name`、`description`、`capsule`、`view` 文本）的语言：① 用户明确指定的语言 > ② 用户需求描述所使用的语言 > ③ 当前客户端界面语言（{targetLanguage}）。直接输出单语言纯文本字符串，除非用户明确要求多语言切换，否则不要生成 `\"i18n\"` 字典。";
            string existingSection = !string.IsNullOrWhiteSpace(existingManifestJson)
                ? $"## 当前格子的现有 `manifest.json`\n```json\n{existingManifestJson.Trim()}\n```"
                : $$"""
## 最小合法结构参考
```json
{
  "schemaVersion": 1,
  "id": "custom.demo",
  "name": "示例格子",
  "description": "一句话说明用途",
  "icon": "{{demoEmoji}}",
  "defaultSize": { "width": 340, "height": 400 },
  "state": { "count": "0", "draft": "" },
  "collections": { "items": { "maxItems": 200, "sort": "newestFirst" } },
  "capsule": {
    "title": "示例 · ${state.count}",
    "summary": "${collections.items.latest.text ?? '点击展开查看'}",
    "glyph": "{{demoEmoji}}",
    "badge": "${state.count}"
  },
  "view": {
    "type": "Stack",
    "spacing": 10,
    "padding": "12,10,12,12",
    "children": []
  }
}
```
""";

            if (!string.IsNullOrWhiteSpace(pack.WebPromptTemplateZh))
            {
                string renderedOtaZh = CustomWidgetRemotePromptService.ReplaceNamedTokens(
                    pack.WebPromptTemplateZh,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["TASK_INTRO"] = taskIntro,
                        ["USER_IDEA"] = normalizedIdea,
                        ["ICON_RULE"] = iconRule,
                        ["LANGUAGE_RULE"] = languageRule,
                        ["EXTRA_DESIGN_RULES"] = pack.ExtraDesignRules,
                        ["EXISTING_MANIFEST_SECTION"] = existingSection,
                        ["SKILL_MARKDOWN"] = pack.SkillMarkdown,
                        ["TARGET_LANGUAGE"] = targetLanguage
                    }).Trim();
                return !string.IsNullOrWhiteSpace(pinnedOnlyBlock)
                    ? $"{renderedOtaZh}\n\n{pinnedOnlyBlock}"
                    : renderedOtaZh;
            }

            var sb = new StringBuilder();
            sb.AppendLine("你现在是 DeskBox 原生桌面小组件（WinUI 3 声明式引擎）设计专家。");
            sb.AppendLine(taskIntro);
            sb.AppendLine();
            sb.AppendLine("## 我的需求");
            sb.AppendLine(normalizedIdea);
            sb.AppendLine();
            sb.AppendLine("## 输出铁律（务必严格遵守）");
            sb.AppendLine("1. **只输出一个完整的 JSON 代码块**（使用 ```json 包裹），JSON 内严禁包含 `//` 注释或尾随逗号。");
            sb.AppendLine("2. 顶层必须包含：`\"schemaVersion\": 1`、`\"id\"`（英文短横线如 `\"custom.water-tracker\"`）、`\"name\"`（简短名称）、`\"description\"`（一句话简介）、`\"icon\"`（贴合功能的单个彩色 Emoji，如 `\"💧\"`、`\"📝\"`、`\"📊\"`、`\"🎯\"`、`\"🚀\"`、`\"⏳\"`、`\"💡\"`）、`\"defaultSize\": { \"width\": 340, \"height\": 420 }`、`\"capsule\"`（折叠成桌面胶囊时的动态摘要）与 `\"view\"`（主界面节点树）。");
            sb.AppendLine("3. **严禁杜撰不存在的组件、内置变量或动作类型**！严禁制作仅修改未绑定 dummy `state` 的假按钮！只能使用下方规范列表中的真实组件、`$namespace.*` 变量与系统动作。");
            sb.AppendLine(iconRule);
            sb.AppendLine("5. **原生玻璃视觉与无重复标题栏规范**：DeskBox 宿主外框已自带顶部标题栏（显示 `icon`、`name`、折叠与菜单按钮），`view` 内部顶部**严禁重复绘制静态组件标题栏**（局部刷新按钮应内嵌于业务分区行或 `Tabs` 旁）；同级分区保持统一的 Bento 微卡片层级（严禁在 `Card` 内嵌套超过 2 层 `Card`）；合理设定 `defaultSize.height` 确保首屏内容完整显示（超过 3 个独立功能面板优先使用 `Tabs`）；实时 `Slider` / `Toggle` 直接通过 `onChange`（读取 `${$value}`）即时生效，严禁在旁边放置多余的“同步/应用”按钮。");
            sb.AppendLine(languageRule);
            if (!string.IsNullOrWhiteSpace(pack.ExtraDesignRules))
            {
                sb.AppendLine(pack.ExtraDesignRules);
            }
            sb.AppendLine();
            sb.AppendLine(tier0DirectoryBlock);
            if (!string.IsNullOrWhiteSpace(selectedCapsulesBlock))
            {
                sb.AppendLine();
                sb.AppendLine(selectedCapsulesBlock);
            }
            sb.AppendLine();
            sb.AppendLine(pack.SkillMarkdown);
            if (!string.IsNullOrWhiteSpace(briefMarkdown))
            {
                sb.AppendLine();
                sb.AppendLine("## 该格子的设计简报与已确认偏好 (`brief.md`)");
                sb.AppendLine(briefMarkdown.Trim());
            }
            if (!string.IsNullOrWhiteSpace(targetNodePath))
            {
                sb.AppendLine();
                sb.AppendLine($"## 当前点选聚焦节点：`{targetNodePath}`（请优先修改该节点区域，保持其余结构稳定）");
            }
            sb.AppendLine();
            sb.AppendLine(existingSection);
            return sb.ToString().Trim();
        }
        else
        {
            string taskIntroEn = string.IsNullOrWhiteSpace(existingManifestJson)
                ? "Design and output a complete DeskBox Custom Widget `manifest.json` configuration based on my request below."
                : "Refine and upgrade my existing DeskBox widget `manifest.json` according to my request below, and output the complete updated `manifest.json` (preserve existing `state` and `collections` field names whenever possible so saved records are retained).";
            string iconRuleEn = customEmoji is not null
                ? $"4. **Required Emoji Icon**: Set both top-level `\"icon\"` and `\"capsule.glyph\"` to `\"{customEmoji}\"`."
                : "4. **Distinctive Emoji Icon**: Pick a single expressive Unicode Emoji matching the widget's purpose for both top-level `\"icon\"` and `\"capsule.glyph\"`.";
            string languageRuleEn =
                $"6. **UI Copy Language Priority**: Choose the language for all visible text (`name`, `description`, `capsule`, `view` labels/placeholders) in this strict order: (1) explicit language requested by the user > (2) natural language of the user's request > (3) current DeskBox UI language ({targetLanguage}). Output clean single-language plain strings (do NOT emit an `\"i18n\"` dictionary unless multi-language switching is explicitly requested).";
            string existingSectionEn = !string.IsNullOrWhiteSpace(existingManifestJson)
                ? $"## Current Existing `manifest.json`\n```json\n{existingManifestJson.Trim()}\n```"
                : $$"""
## Minimal Valid Structure Reference
```json
{
  "schemaVersion": 1,
  "id": "custom.demo",
  "name": "Quick Tracker",
  "description": "One-sentence summary of the widget",
  "icon": "{{demoEmoji}}",
  "defaultSize": { "width": 340, "height": 400 },
  "state": { "count": "0", "draft": "" },
  "collections": { "items": { "maxItems": 200, "sort": "newestFirst" } },
  "capsule": {
    "title": "Tracker · ${state.count}",
    "summary": "${collections.items.latest.text ?? 'Click to expand'}",
    "glyph": "{{demoEmoji}}",
    "badge": "${state.count}"
  },
  "view": {
    "type": "Stack",
    "spacing": 10,
    "padding": "12,10,12,12",
    "children": []
  }
}
```
""";

            if (!string.IsNullOrWhiteSpace(pack.WebPromptTemplateEn))
            {
                string renderedOtaEn = CustomWidgetRemotePromptService.ReplaceNamedTokens(
                    pack.WebPromptTemplateEn,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["TASK_INTRO"] = taskIntroEn,
                        ["USER_IDEA"] = normalizedIdea,
                        ["ICON_RULE"] = iconRuleEn,
                        ["LANGUAGE_RULE"] = languageRuleEn,
                        ["EXTRA_DESIGN_RULES"] = pack.ExtraDesignRules,
                        ["EXISTING_MANIFEST_SECTION"] = existingSectionEn,
                        ["SKILL_MARKDOWN"] = pack.SkillMarkdown,
                        ["TARGET_LANGUAGE"] = targetLanguage
                    }).Trim();
                return !string.IsNullOrWhiteSpace(pinnedOnlyBlock)
                    ? $"{renderedOtaEn}\n\n{pinnedOnlyBlock}"
                    : renderedOtaEn;
            }

            var sb = new StringBuilder();
            sb.AppendLine("You are a DeskBox native desktop widget architect (WinUI 3 declarative engine on Windows 11).");
            sb.AppendLine(taskIntroEn);
            sb.AppendLine();
            sb.AppendLine("## User Request");
            sb.AppendLine(normalizedIdea);
            sb.AppendLine();
            sb.AppendLine("## Strict Output Rules");
            sb.AppendLine("1. **Output ONLY a single complete JSON code block** (wrapped in ```json ... ```). Never include `//` comments or trailing commas inside JSON.");
            sb.AppendLine("2. Top-level object MUST include: `\"schemaVersion\": 1`, `\"id\"` (kebab-case like `\"custom.water-tracker\"`), `\"name\"` (concise title), `\"description\"` (one-line summary), `\"icon\"` (single expressive Emoji such as `\"💧\"`, `\"📝\"`, `\"📊\"`, `\"🎯\"`, `\"🚀\"`), `\"defaultSize\": { \"width\": 340, \"height\": 420 }`, `\"capsule\"` (collapsed desktop capsule summary), and `\"view\"` (root node tree).");
            sb.AppendLine("3. **Never invent unsupported node types, built-in tokens, or action types**, and NEVER create facade buttons that only mutate unread dummy `state` keys! Use only documented `view.type`, `$namespace.*` tokens, and `action.type` contracts below.");
            sb.AppendLine(iconRuleEn);
            sb.AppendLine("5. **Native Glass & No-Duplicate-Header Discipline**: DeskBox's host frame already renders the top header bar (`icon`, `name`, collapse, and menu). NEVER add a duplicate static widget-title header bar at the top of `view`! Keep sibling Bento sections visually consistent (never nest `Card` > 2 levels), size `defaultSize.height` to fit all initial sections cleanly (use `Tabs` for 3+ distinct panels), and bind live `Slider`/`Toggle` controls directly via `onChange` (`${$value}`) without redundant apply/sync buttons.");
            sb.AppendLine(languageRuleEn);
            if (!string.IsNullOrWhiteSpace(pack.ExtraDesignRules))
            {
                sb.AppendLine(pack.ExtraDesignRules);
            }
            sb.AppendLine();
            sb.AppendLine(tier0DirectoryBlock);
            if (!string.IsNullOrWhiteSpace(selectedCapsulesBlock))
            {
                sb.AppendLine();
                sb.AppendLine(selectedCapsulesBlock);
            }
            sb.AppendLine();
            sb.AppendLine(pack.SkillMarkdown);
            if (!string.IsNullOrWhiteSpace(briefMarkdown))
            {
                sb.AppendLine();
                sb.AppendLine("## Shared Design Brief (`brief.md`)");
                sb.AppendLine(briefMarkdown.Trim());
            }
            if (!string.IsNullOrWhiteSpace(targetNodePath))
            {
                sb.AppendLine();
                sb.AppendLine($"## Target Selected Node: `{targetNodePath}` (prioritize editing this node while preserving unrelated layout)");
            }
            sb.AppendLine();
            sb.AppendLine(existingSectionEn);
            return sb.ToString().Trim();
        }
    }

    /// <summary>
    /// Builds a one-click repair prompt containing exact validation diagnostics, AST-backfilled
    /// capability contracts, and optional live pre-flight probe telemetry so the AI can fix
    /// schema, token, or live API mismatches automatically.
    /// </summary>
    public static string BuildChatAiRepairPrompt(
        string brokenJson,
        IReadOnlyList<CustomWidgetValidationDiagnostic> diagnostics,
        string? uiLanguage = null,
        CustomWidgetEffectivePromptPack? promptPack = null,
        string? preflightFeedback = null)
    {
        CustomWidgetEffectivePromptPack pack = promptPack ?? CustomWidgetRemotePromptService.Shared.GetEffectivePromptPack();
        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(uiLanguage);
        string targetLanguage = CustomWidgetRemotePromptService.DescribeTargetLanguage(uiLanguage);

        var diagSb = new StringBuilder();
        foreach (CustomWidgetValidationDiagnostic diag in diagnostics)
        {
            diagSb.Append("- `").Append(diag.Path).Append("`: ").AppendLine(diag.Message);
        }
        if (!string.IsNullOrWhiteSpace(preflightFeedback))
        {
            if (diagSb.Length > 0) diagSb.AppendLine();
            diagSb.AppendLine(preflightFeedback.Trim());
        }
        string diagBlock = diagSb.ToString().TrimEnd();

        IReadOnlyList<CustomWidgetCapabilitySpec> backfilledSpecs =
            CustomWidgetCapabilityRegistry.FindBackfillCapabilitiesForRepair(brokenJson, diagnostics);
        string backfillBlock = backfilledSpecs.Count > 0
            ? CustomWidgetCapabilityRegistry.FormatSelectedCapsulesPromptBlock([], backfilledSpecs)
            : string.Empty;

        if (isChinese)
        {
            if (!string.IsNullOrWhiteSpace(pack.RepairPromptTemplateZh))
            {
                string renderedOtaRepairZh = CustomWidgetRemotePromptService.ReplaceNamedTokens(
                    pack.RepairPromptTemplateZh,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["DIAGNOSTICS"] = diagBlock,
                        ["BROKEN_JSON"] = brokenJson.Trim(),
                        ["EXTRA_DESIGN_RULES"] = pack.ExtraDesignRules,
                        ["TARGET_LANGUAGE"] = targetLanguage
                    }).Trim();
                return !string.IsNullOrWhiteSpace(backfillBlock)
                    ? $"{renderedOtaRepairZh}\n\n{backfillBlock}"
                    : renderedOtaRepairZh;
            }

            var sb = new StringBuilder();
            sb.AppendLine("你刚才生成的 DeskBox 自定义格子 `manifest.json` 未通过本地引擎校验或预检探测，问题如下：");
            sb.AppendLine();
            sb.AppendLine(diagBlock);
            if (!string.IsNullOrWhiteSpace(backfillBlock))
            {
                sb.AppendLine();
                sb.AppendLine(backfillBlock);
            }
            sb.AppendLine();
            sb.Append("请严格对照上方 DeskBox 原生契约白名单修复所有问题，保持原有文案语言（若尚未指定文案语言则使用 ").Append(targetLanguage).AppendLine("），**仅输出修正后的完整 ```json 代码块**（严禁加注释、严禁造假按钮或使用未定义的组件/变量）：");
            if (!string.IsNullOrWhiteSpace(pack.ExtraDesignRules))
            {
                sb.AppendLine(pack.ExtraDesignRules);
            }
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(brokenJson.Trim());
            sb.AppendLine("```");
            return sb.ToString().Trim();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(pack.RepairPromptTemplateEn))
            {
                string renderedOtaRepairEn = CustomWidgetRemotePromptService.ReplaceNamedTokens(
                    pack.RepairPromptTemplateEn,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["DIAGNOSTICS"] = diagBlock,
                        ["BROKEN_JSON"] = brokenJson.Trim(),
                        ["EXTRA_DESIGN_RULES"] = pack.ExtraDesignRules,
                        ["TARGET_LANGUAGE"] = targetLanguage
                    }).Trim();
                return !string.IsNullOrWhiteSpace(backfillBlock)
                    ? $"{renderedOtaRepairEn}\n\n{backfillBlock}"
                    : renderedOtaRepairEn;
            }

            var sb = new StringBuilder();
            sb.AppendLine("The DeskBox Custom Widget `manifest.json` you generated failed local validation or live pre-flight verification with the following findings:");
            sb.AppendLine();
            sb.AppendLine(diagBlock);
            if (!string.IsNullOrWhiteSpace(backfillBlock))
            {
                sb.AppendLine();
                sb.AppendLine(backfillBlock);
            }
            sb.AppendLine();
            sb.Append("Fix every issue above strictly against the DeskBox capability contracts above, preserve the widget's existing copy language (defaulting to ").Append(targetLanguage).AppendLine(" if unset), and **output ONLY the corrected complete ```json code block** (no comments, no facade buttons, no unsupported node/token types):");
            if (!string.IsNullOrWhiteSpace(pack.ExtraDesignRules))
            {
                sb.AppendLine(pack.ExtraDesignRules);
            }
            sb.AppendLine();
            sb.AppendLine("```json");
            sb.AppendLine(brokenJson.Trim());
            sb.AppendLine("```");
            return sb.ToString().Trim();
        }
    }

    /// <summary>
    /// Builds the compact co-creation system prompt for multi-turn iterative Studio sessions.
    /// </summary>
    public static string BuildStudioCoCreationSystemPrompt(
        string? uiLanguage = null,
        CustomWidgetEffectivePromptPack? promptPack = null)
    {
        CustomWidgetEffectivePromptPack pack = promptPack ?? CustomWidgetRemotePromptService.Shared.GetEffectivePromptPack();
        string targetLanguage = CustomWidgetRemotePromptService.DescribeTargetLanguage(uiLanguage);

        var sb = new StringBuilder();
        sb.AppendLine("You are DeskBox Studio's native WinUI 3 widget co-creation partner on Windows 11.");
        sb.AppendLine("In every turn, respond with ONLY a single valid JSON envelope object (raw JSON or ```json ... ``` fence, zero comments, zero trailing commas):");
        sb.AppendLine("""
{
  "kind": "edit",
  "reply": "Short friendly 1-2 sentence explanation in the user's language",
  "changeSummary": "Concise revision label (<= 24 chars)",
  "briefUpdates": {
    "purpose": "Optional 1-line widget purpose",
    "addConfirmed": ["Newly confirmed requirement"],
    "addRejected": ["Explicitly rejected idea"],
    "addStylePreferences": ["Confirmed visual/layout preference"]
  },
  "suggestions": ["Optional 1-3 short clickable next-step prompts (<= 18 chars each)"],
  "sampleData": null,
  "manifest": { "schemaVersion": 1, "...": "Complete valid manifest when kind == 'edit', or null when kind is 'answer' or 'clarify'" }
}
""");
        sb.AppendLine("Rules:");
        sb.AppendLine("1. Choose `kind`:");
        sb.AppendLine("   - `\"edit\"` (DEFAULT for creation/refinement requests): output the complete valid `manifest` object.");
        sb.AppendLine("   - `\"answer\"`: when the user is only asking a question or seeking advice without wanting to mutate the widget yet (`\"manifest\": null`).");
        sb.AppendLine("   - `\"clarify\"`: ONLY when a request is genuinely ambiguous with conflicting directions; ask 1 brief question in `reply` + 2-3 clickable choices in `suggestions` (`\"manifest\": null`). Otherwise prefer `\"edit\"` with smart defaults.");
        sb.AppendLine("2. Surgical Edit & Layout Discipline: When refining an existing `manifest.json`, preserve existing `id`, `state` keys, `collections` names, `sources`, and `i18n` locales unless asked to change them. NEVER add a duplicate inner widget-title header bar at the top of `view` (the host frame already renders `icon` + `name` + controls). Keep sibling Bento sections visually uniform (max 2 `Card` levels), calibrate `defaultSize.height` when adding new sections (or organize 3+ panels into `Tabs`), use two-way live `Slider` (`value` + `onChange` with `${$value}`) without redundant apply/sync buttons, and avoid hardcoded near-black/near-white `#RRGGBB` colors.");
        sb.AppendLine("3. Never invent unsupported node types or action types; strictly follow the Component Quick Index and Matched WinUI Recipes provided in the user message.");
        sb.Append("4. Copy language priority: (1) explicit language requested by user > (2) natural language of user's message > (3) current DeskBox UI language (")
          .Append(targetLanguage)
          .AppendLine(").");
        if (!string.IsNullOrWhiteSpace(pack.ExtraDesignRules))
        {
            sb.AppendLine(pack.ExtraDesignRules);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Assembles the artifact-centric multi-turn co-creation user prompt with compact core index,
    /// deterministically routed WinUI skill cards, <c>brief.md</c>, live telemetry, clicked node focus,
    /// and recent short-term conversation context.
    /// </summary>
    public static string BuildStudioCoCreationUserPrompt(
        string userMessage,
        string? currentManifestJson = null,
        string? briefMarkdown = null,
        IReadOnlyList<StudioChatMessage>? recentMessages = null,
        CustomWidgetHealthReport? healthReport = null,
        string? targetNodePath = null,
        string? preferredEmoji = null,
        bool allowSampleData = false,
        string? uiLanguage = null,
        IReadOnlyList<string>? pinnedCapabilityIds = null)
    {
        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(uiLanguage);
        string normalizedPrompt = string.IsNullOrWhiteSpace(userMessage)
            ? (isChinese ? "帮我优化这个格子的布局与交互细节" : "Refine this widget's layout and interaction craft")
            : userMessage.Trim();

        (IReadOnlyList<CustomWidgetCapabilitySpec> pinnedSpecs, IReadOnlyList<CustomWidgetCapabilitySpec> autoSpecs) =
            CustomWidgetCapabilityRegistry.SelectCapabilitiesForTurn(
                currentManifestJson,
                normalizedPrompt,
                briefMarkdown,
                pinnedCapabilityIds);

        IReadOnlyList<CustomWidgetSkillCard> selectedSkills = CustomWidgetSkillCatalog.SelectSkillsForTurn(
            currentManifestJson,
            normalizedPrompt,
            briefMarkdown);

        var sb = new StringBuilder();
        sb.AppendLine(CustomWidgetCapabilityRegistry.BuildTier0DirectoryPromptBlock(isChinese));
        string capsulesBlock = CustomWidgetCapabilityRegistry.FormatSelectedCapsulesPromptBlock(pinnedSpecs, autoSpecs);
        if (!string.IsNullOrWhiteSpace(capsulesBlock))
        {
            sb.AppendLine();
            sb.AppendLine(capsulesBlock);
        }
        sb.AppendLine();
        sb.AppendLine(CustomWidgetSkillCatalog.FormatSelectedSkillsPromptBlock(selectedSkills));

        if (!string.IsNullOrWhiteSpace(briefMarkdown))
        {
            sb.AppendLine();
            sb.AppendLine("## Current Widget Design Brief (`brief.md`) — Honor Confirmed & Rejected Rules");
            sb.AppendLine(briefMarkdown.Trim());
        }

        if (healthReport is not null)
        {
            sb.AppendLine();
            sb.AppendLine(CustomWidgetHealthAnalyzer.FormatTelemetryPromptBlock(healthReport));
        }

        if (!string.IsNullOrWhiteSpace(targetNodePath))
        {
            sb.AppendLine();
            sb.Append("## Focused Target Node Clicked by User: `")
              .Append(targetNodePath.Trim())
              .AppendLine("` (Apply surgical changes primarily to this component/subtree while keeping surrounding layout stable!)");
        }

        if (IsEmojiIcon(preferredEmoji))
        {
            sb.AppendLine();
            sb.Append("## Preferred Widget Emoji Icon: `\"")
              .Append(preferredEmoji!.Trim())
              .AppendLine("\"` (use for `icon` and static `capsule.glyph`).");
        }

        if (allowSampleData)
        {
            sb.AppendLine();
            sb.AppendLine("## Preview Sample Data (`sampleData`)");
            sb.AppendLine("Because this is a brand-new widget with no saved user data yet, if `manifest` declares `collections`, include 2-5 realistic preview records in `\"sampleData\": { \"collections\": { \"<collectionName>\": [{ ... }] } }` so charts/lists/heatmaps look alive immediately in Studio preview.");
        }

        if (!string.IsNullOrWhiteSpace(currentManifestJson))
        {
            sb.AppendLine();
            sb.AppendLine("## Current `manifest.json` (Source of Truth)");
            sb.AppendLine("```json");
            sb.AppendLine(currentManifestJson.Trim());
            sb.AppendLine("```");
        }

        if (recentMessages is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("## Recent Co-Creation Conversation Context (Last Turns)");
            foreach (StudioChatMessage msg in recentMessages.TakeLast(6))
            {
                string speaker = string.Equals(msg.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                    ? $"Assistant ({msg.Kind})"
                    : "User";
                string nodeTag = !string.IsNullOrWhiteSpace(msg.TargetNodePath)
                    ? $" [target: {msg.TargetNodePath}]"
                    : string.Empty;
                sb.Append("- ").Append(speaker).Append(nodeTag).Append(": ").AppendLine(msg.Text.Trim());
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Current User Turn");
        sb.AppendLine(normalizedPrompt);
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Builds a ready-to-run instruction for local AI Coding Agents (Cursor, Claude Code, Codex)
    /// referencing exact local file paths for instant 200ms desktop hot-reload.
    /// </summary>
    public static string BuildLocalAgentPrompt(
        string userIdea,
        string skillFilePath,
        string? targetManifestPath,
        string workspaceDirectory,
        string? preferredEmoji = null,
        string? uiLanguage = null,
        IReadOnlyList<string>? pinnedCapabilityIds = null)
    {
        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(uiLanguage);
        string targetLanguage = CustomWidgetRemotePromptService.DescribeTargetLanguage(uiLanguage);
        string normalizedIdea = string.IsNullOrWhiteSpace(userIdea)
            ? (isChinese ? "设计一个美观实用的原生桌面小组件" : "Design a clean, practical native desktop widget")
            : userIdea.Trim();
        string? customEmoji = IsEmojiIcon(preferredEmoji) ? preferredEmoji!.Trim() : null;
        IReadOnlyList<CustomWidgetCapabilitySpec> pinnedSpecs =
            CustomWidgetCapabilityRegistry.ExpandWithDependencies(pinnedCapabilityIds);

        var sb = new StringBuilder();
        if (isChinese)
        {
            sb.AppendLine("请先阅读 DeskBox 原生自定义格子开发规范、API 字典与 JSON Schema：");
            sb.Append("- 规范文档：`").Append(skillFilePath).AppendLine("`");
            sb.Append("- 能力与 API 参考手册：`").Append(Path.Combine(workspaceDirectory, "API_REFERENCE.md")).AppendLine("`");
            sb.Append("- Schema：`").Append(Path.Combine(workspaceDirectory, "manifest.schema.json")).AppendLine("`");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(targetManifestPath))
            {
                sb.Append("然后直接编辑并保存目标格子的配置文件（保存后 DeskBox 桌面会在 200ms 内实时热重载）：`")
                  .Append(targetManifestPath)
                  .AppendLine("`");
            }
            else
            {
                sb.Append("然后在工作区目录 `")
                  .Append(workspaceDirectory)
                  .AppendLine("` 下生成一份符合规范的 `custom-widget.manifest.json` 文件，告诉我文件路径以便一键导入（若该目录下已有实例子文件夹，也可直接修改对应子文件夹中的 `manifest.json` 触发实时热重载）。");
            }

            sb.AppendLine();
            sb.Append("需求描述：").AppendLine(normalizedIdea);
            if (pinnedSpecs.Count > 0)
            {
                sb.Append("指定启用能力模块：")
                  .AppendLine(string.Join("、", pinnedSpecs.Select(static p => $"`@{p.Id}` ({p.TitleZh})")));
            }
            sb.Append("语言规则：优先遵循需求中明确指定的语言；否则与需求描述语言一致；若不明确则使用当前界面语言（")
              .Append(targetLanguage)
              .AppendLine("）。日常单语言格子直接输出纯文本字符串，无需生成 `\"i18n\"`。");
            if (customEmoji is not null)
            {
                sb.Append("图标要求：请将顶层 `\"icon\"` 与 `\"capsule.glyph\"` 设为 `")
                  .Append(customEmoji)
                  .AppendLine("`。");
            }
            else
            {
                sb.AppendLine("图标要求：请为顶层 `\"icon\"` 与 `\"capsule.glyph\"` 设置一个贴合格子用途的单个彩色 Emoji（如 💧、📝、📊、🎯、🚀、💡），让该格子在桌面和设置中拥有独特图标。");
            }
        }
        else
        {
            sb.AppendLine("First read the DeskBox native Custom Widget specification, API Reference, and JSON Schema:");
            sb.Append("- Specification: `").Append(skillFilePath).AppendLine("`");
            sb.Append("- Capability & API Reference: `").Append(Path.Combine(workspaceDirectory, "API_REFERENCE.md")).AppendLine("`");
            sb.Append("- JSON Schema: `").Append(Path.Combine(workspaceDirectory, "manifest.schema.json")).AppendLine("`");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(targetManifestPath))
            {
                sb.Append("Then edit and save the target widget manifest directly (DeskBox hot-reloads on the desktop within 200ms): `")
                  .Append(targetManifestPath)
                  .AppendLine("`");
            }
            else
            {
                sb.Append("Then create a valid `custom-widget.manifest.json` inside `")
                  .Append(workspaceDirectory)
                  .AppendLine("` and share the file path for one-click import (or edit an existing widget folder's `manifest.json` directly for live hot-reload).");
            }

            sb.AppendLine();
            sb.Append("Goal: ").AppendLine(normalizedIdea);
            if (pinnedSpecs.Count > 0)
            {
                sb.Append("Required Capabilities to Integrate: ")
                  .AppendLine(string.Join(", ", pinnedSpecs.Select(static p => $"`@{p.Id}` ({p.TitleEn})")));
            }
            sb.Append("Language rule: Follow any explicit language requested in the goal; otherwise match the goal's language; otherwise use the active DeskBox UI language (")
              .Append(targetLanguage)
              .AppendLine("). Output clean single-language plain strings unless multi-language switching is explicitly requested.");
            if (customEmoji is not null)
            {
                sb.Append("Icon requirement: Set both top-level `\"icon\"` and `\"capsule.glyph\"` to `")
                  .Append(customEmoji)
                  .AppendLine("`.");
            }
            else
            {
                sb.AppendLine("Icon requirement: Set both top-level `\"icon\"` and `\"capsule.glyph\"` to a single expressive Unicode Emoji matching the widget's purpose (e.g. 💧, 📝, 📊, 🎯, 🚀, 💡).");
            }
        }

        return sb.ToString().Trim();
    }

    public const string WorkLoggerManifestJson = """
{
  "schemaVersion": 1,
  "id": "deskbox.work-logger",
  "name": "工作记录",
  "version": "1.1.0",
  "author": "DeskBox",
  "description": "随手记录每日工作进展、会议纪要与灵感，支持拖入文件自动关联并一键复制 Markdown 周报。",
  "icon": "📝",
  "minSize": { "width": 300, "height": 340 },
  "defaultSize": { "width": 360, "height": 460 },
  "i18n": {
    "en-US": {
      "widgetName": "Work Logger",
      "widgetDescription": "Capture daily progress, meeting notes, and ideas with drag-and-drop file linking and 1-click Markdown export.",
      "setDropMode": "File Drop Behavior",
      "optDropLink": "Link original file path (saves disk space)",
      "optDropCopy": "Copy file into widget vault",
      "setDefaultTag": "Default Category",
      "tagAll": "All",
      "tagProgress": "Progress",
      "tagMeeting": "Meeting",
      "tagIdea": "Idea",
      "tagArchive": "Archive",
      "dropLinkedPrefix": "Linked file: ",
      "capsuleTitlePrefix": "Work Log · Today ",
      "capsuleTitleSuffix": "",
      "capsuleEmptySummary": "Drop files or type a quick note",
      "inputPlaceholder": "Log what you're working on (Enter to save, or drop files)…",
      "addBtn": "Log",
      "exportBtn": "Weekly",
      "exportTooltip": "Copy logs as Markdown",
      "emptyTitle": "No work logs yet",
      "emptySubtitle": "Type progress above and press Enter, or drag files directly into the widget",
      "deleteTooltip": "Delete entry",
      "footerTodayPrefix": "Today ",
      "footerMid": " · Total ",
      "footerCountSuffix": "",
      "footerDropPrefix": "Drop: ",
      "footerDropCopy": "Copy",
      "footerDropLink": "Link"
    },
    "zh-CN": {
      "widgetName": "工作记录",
      "widgetDescription": "随手记录每日工作进展、会议纪要与灵感，支持拖入文件自动关联并一键复制 Markdown 周报。",
      "setDropMode": "拖入文件处理方式",
      "optDropLink": "关联原文件路径（不占空间）",
      "optDropCopy": "复制文件副本到格子数据仓",
      "setDefaultTag": "默认记录分类",
      "tagAll": "全部",
      "tagProgress": "进展",
      "tagMeeting": "会议",
      "tagIdea": "灵感",
      "tagArchive": "归档",
      "dropLinkedPrefix": "关联文件：",
      "capsuleTitlePrefix": "工作记录 · 今日 ",
      "capsuleTitleSuffix": " 条",
      "capsuleEmptySummary": "拖拽文件或输入内容快速记录",
      "inputPlaceholder": "记录当下的工作事项（回车提交，或直接拖入文件）…",
      "addBtn": "记一笔",
      "exportBtn": "周报",
      "exportTooltip": "一键复制 Markdown 格式工作记录",
      "emptyTitle": "还没有工作记录",
      "emptySubtitle": "在上方输入进展并按回车，或直接把文件拖进格子自动关联",
      "deleteTooltip": "删除此条记录",
      "footerTodayPrefix": "今日已记 ",
      "footerMid": " 条 · 共 ",
      "footerCountSuffix": " 条",
      "footerDropPrefix": "拖入模式：",
      "footerDropCopy": "复制副本",
      "footerDropLink": "路径关联"
    },
    "zh-TW": {
      "widgetName": "工作紀錄",
      "widgetDescription": "隨手記錄每日工作進展、會議紀要與靈感，支援拖入檔案自動關聯並一鍵複製 Markdown 週報。",
      "setDropMode": "拖入檔案處理方式",
      "optDropLink": "關聯原檔案路徑（不佔空間）",
      "optDropCopy": "複製檔案副本到格子資料倉",
      "setDefaultTag": "預設紀錄分類",
      "tagAll": "全部",
      "tagProgress": "進展",
      "tagMeeting": "會議",
      "tagIdea": "靈感",
      "tagArchive": "封存",
      "dropLinkedPrefix": "關聯檔案：",
      "capsuleTitlePrefix": "工作紀錄 · 今日 ",
      "capsuleTitleSuffix": " 筆",
      "capsuleEmptySummary": "拖曳檔案或輸入內容快速記錄",
      "inputPlaceholder": "記錄當下的工作事項（Enter 送出，或直接拖入檔案）…",
      "addBtn": "記一筆",
      "exportBtn": "週報",
      "exportTooltip": "一鍵複製 Markdown 格式工作紀錄",
      "emptyTitle": "還沒有工作紀錄",
      "emptySubtitle": "在上方輸入進展並按 Enter，或直接將檔案拖進格子自動關聯",
      "deleteTooltip": "刪除此筆紀錄",
      "footerTodayPrefix": "今日已記 ",
      "footerMid": " 筆 · 共 ",
      "footerCountSuffix": " 筆",
      "footerDropPrefix": "拖入模式：",
      "footerDropCopy": "複製副本",
      "footerDropLink": "路徑關聯"
    },
    "ja-JP": {
      "widgetName": "業務ログ",
      "widgetDescription": "日々の進捗・議事録・アイデアを手軽に記録。ファイルドロップ連携と Markdown 週報コピーに対応。",
      "setDropMode": "ドロップ時の動作",
      "optDropLink": "元ファイルのパスをリンク（省容量）",
      "optDropCopy": "ウィジェット保管庫へコピー",
      "setDefaultTag": "既定のカテゴリ",
      "tagAll": "すべて",
      "tagProgress": "進捗",
      "tagMeeting": "会議",
      "tagIdea": "着想",
      "tagArchive": "保管",
      "dropLinkedPrefix": "関連ファイル：",
      "capsuleTitlePrefix": "業務ログ · 今日 ",
      "capsuleTitleSuffix": " 件",
      "capsuleEmptySummary": "ファイルをドロップまたは入力して記録",
      "inputPlaceholder": "作業内容を記録（Enter で保存、またはファイルをドロップ）…",
      "addBtn": "記録",
      "exportBtn": "週報",
      "exportTooltip": "Markdown 形式でコピー",
      "emptyTitle": "記録はまだありません",
      "emptySubtitle": "上で入力して Enter を押すか、ファイルをドロップしてください",
      "deleteTooltip": "この記録を削除",
      "footerTodayPrefix": "今日 ",
      "footerMid": " 件 · 計 ",
      "footerCountSuffix": " 件",
      "footerDropPrefix": "ドロップ：",
      "footerDropCopy": "コピー",
      "footerDropLink": "リンク"
    },
    "ko-KR": {
      "widgetName": "업무 로그",
      "widgetDescription": "일일 진행 상황, 회의록, 아이디어를 간편하게 기록하고 파일 드롭 연결 및 Markdown 주간 보고서 복사를 지원합니다.",
      "setDropMode": "파일 드롭 처리 방식",
      "optDropLink": "원본 경로 연결 (용량 절약)",
      "optDropCopy": "위젯 보관함으로 사본 복사",
      "setDefaultTag": "기본 분류",
      "tagAll": "전체",
      "tagProgress": "진행",
      "tagMeeting": "회의",
      "tagIdea": "아이디어",
      "tagArchive": "보관",
      "dropLinkedPrefix": "연결된 파일: ",
      "capsuleTitlePrefix": "업무 로그 · 오늘 ",
      "capsuleTitleSuffix": "건",
      "capsuleEmptySummary": "파일을 드롭하거나 내용을 입력해 기록하세요",
      "inputPlaceholder": "현재 업무 내용을 기록하세요 (Enter 저장 또는 파일 드롭)…",
      "addBtn": "기록",
      "exportBtn": "주간보고",
      "exportTooltip": "Markdown 형식으로 복사",
      "emptyTitle": "아직 업무 기록이 없습니다",
      "emptySubtitle": "위에 내용을 입력하고 Enter를 누르거나 파일을 드롭하세요",
      "deleteTooltip": "이 기록 삭제",
      "footerTodayPrefix": "오늘 ",
      "footerMid": "건 · 총 ",
      "footerCountSuffix": "건",
      "footerDropPrefix": "드롭 모드: ",
      "footerDropCopy": "사본 복사",
      "footerDropLink": "경로 연결"
    }
  },
  "settings": [
    {
      "key": "dropMode",
      "type": "choice",
      "label": "${$i18n.setDropMode}",
      "default": "link",
      "options": [
        { "value": "link", "label": "${$i18n.optDropLink}" },
        { "value": "copy", "label": "${$i18n.optDropCopy}" }
      ]
    },
    {
      "key": "defaultTag",
      "type": "choice",
      "label": "${$i18n.setDefaultTag}",
      "default": "progress",
      "options": [
        { "value": "progress", "label": "${$i18n.tagProgress}" },
        { "value": "meeting", "label": "${$i18n.tagMeeting}" },
        { "value": "idea", "label": "${$i18n.tagIdea}" },
        { "value": "archive", "label": "${$i18n.tagArchive}" }
      ]
    }
  ],
  "state": {
    "draftText": "",
    "activeTag": "progress",
    "filterTag": "all"
  },
  "collections": {
    "logs": { "maxItems": 500, "sort": "newestFirst" }
  },
  "onDrop": {
    "accept": "files",
    "mode": "$settings.dropMode",
    "action": {
      "type": "collection.prepend",
      "collection": "logs",
      "item": {
        "text": "${$i18n.dropLinkedPrefix}${$drop.name}",
        "tag": "${state.activeTag}",
        "fileName": "${$drop.name}",
        "filePath": "${$drop.path}",
        "storageMode": "${$drop.mode}"
      }
    }
  },
  "capsule": {
    "title": "${$i18n.capsuleTitlePrefix}${collections.logs.todayCount}${$i18n.capsuleTitleSuffix}",
    "summary": "${collections.logs.latest.text ?? $i18n.capsuleEmptySummary}",
    "glyph": "📝",
    "badge": "${collections.logs.todayCount}",
    "enableBounceOnUpdate": true
  },
  "view": {
    "type": "Stack",
    "spacing": 10,
    "padding": "12,10,12,12",
    "children": [
      {
        "type": "Card",
        "tone": "subtle",
        "padding": "10,10,10,10",
        "children": [
          {
            "type": "Stack",
            "spacing": 8,
            "children": [
              {
                "type": "Segmented",
                "bind": "state.activeTag",
                "options": [
                  { "value": "progress", "label": "${$i18n.tagProgress}" },
                  { "value": "meeting", "label": "${$i18n.tagMeeting}" },
                  { "value": "idea", "label": "${$i18n.tagIdea}" },
                  { "value": "archive", "label": "${$i18n.tagArchive}" }
                ]
              },
              {
                "type": "Grid",
                "columns": "1*,Auto",
                "spacing": 8,
                "children": [
                  {
                    "type": "Input",
                    "column": 0,
                    "placeholder": "${$i18n.inputPlaceholder}",
                    "bind": "state.draftText",
                    "onSubmit": [
                      {
                        "type": "collection.prepend",
                        "guard": "${state.draftText != ''}",
                        "collection": "logs",
                        "item": {
                          "text": "${state.draftText}",
                          "tag": "${state.activeTag}"
                        }
                      },
                      {
                        "type": "state.set",
                        "key": "draftText",
                        "value": ""
                      }
                    ]
                  },
                  {
                    "type": "Button",
                    "column": 1,
                    "icon": "\uE710",
                    "text": "${$i18n.addBtn}",
                    "variant": "accent",
                    "onClick": [
                      {
                        "type": "collection.prepend",
                        "guard": "${state.draftText != ''}",
                        "collection": "logs",
                        "item": {
                          "text": "${state.draftText}",
                          "tag": "${state.activeTag}"
                        }
                      },
                      {
                        "type": "state.set",
                        "key": "draftText",
                        "value": ""
                      }
                    ]
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "type": "Grid",
        "columns": "1*,Auto",
        "spacing": 8,
        "children": [
          {
            "type": "Segmented",
            "column": 0,
            "bind": "state.filterTag",
            "options": [
              { "value": "all", "label": "${$i18n.tagAll}" },
              { "value": "progress", "label": "${$i18n.tagProgress}" },
              { "value": "meeting", "label": "${$i18n.tagMeeting}" },
              { "value": "idea", "label": "${$i18n.tagIdea}" },
              { "value": "archive", "label": "${$i18n.tagArchive}" }
            ]
          },
          {
            "type": "Button",
            "column": 1,
            "icon": "\uE8C8",
            "text": "${$i18n.exportBtn}",
            "tooltip": "${$i18n.exportTooltip}",
            "variant": "subtle",
            "onClick": {
              "type": "collection.exportMarkdown",
              "collection": "logs"
            }
          }
        ]
      },
      {
        "type": "List",
        "items": "logs",
        "filter": "${state.filterTag == 'all' || state.filterTag == '全部' || $item.tag == state.filterTag}",
        "emptyState": {
          "icon": "\uE70F",
          "title": "${$i18n.emptyTitle}",
          "subtitle": "${$i18n.emptySubtitle}"
        },
        "hoverActions": [
          {
            "type": "Button",
            "icon": "\uE74D",
            "variant": "ghost",
            "tooltip": "${$i18n.deleteTooltip}",
            "onClick": {
              "type": "collection.remove",
              "collection": "logs",
              "targetId": "${$item.id}"
            }
          }
        ],
        "itemTemplate": {
          "type": "Stack",
          "spacing": 6,
          "children": [
            {
              "type": "Stack",
              "orientation": "horizontal",
              "spacing": 6,
              "children": [
                {
                  "type": "Badge",
                  "text": "${$item.tag == 'progress' ? $i18n.tagProgress : ($item.tag == 'meeting' ? $i18n.tagMeeting : ($item.tag == 'idea' ? $i18n.tagIdea : ($item.tag == 'archive' ? $i18n.tagArchive : $item.tag)))}",
                  "tone": "accent"
                },
                {
                  "type": "Text",
                  "text": "${$item.timeText}",
                  "style": "caption",
                  "color": "muted"
                }
              ]
            },
            {
              "type": "Text",
              "text": "${$item.text}",
              "style": "body",
              "maxLines": 4
            },
            {
              "type": "FileChip",
              "$when": "${$item.filePath != ''}",
              "fileName": "${$item.fileName}",
              "filePath": "${$item.filePath}",
              "storageMode": "${$item.storageMode}"
            }
          ]
        }
      },
      {
        "type": "Grid",
        "columns": "1*,Auto",
        "children": [
          {
            "type": "Text",
            "column": 0,
            "text": "${$i18n.footerTodayPrefix}${collections.logs.todayCount}${$i18n.footerMid}${collections.logs.count}${$i18n.footerCountSuffix}",
            "style": "caption",
            "color": "muted"
          },
          {
            "type": "Text",
            "column": 1,
            "text": "${$i18n.footerDropPrefix}${$settings.dropMode == 'copy' ? $i18n.footerDropCopy : $i18n.footerDropLink}",
            "style": "caption",
            "color": "muted"
          }
        ]
      }
    ]
  }
}
""";

    public const string PomodoroManifestJson = """
{
  "schemaVersion": 1,
  "id": "deskbox.pomodoro",
  "name": "番茄时钟",
  "version": "4.1.0",
  "author": "DeskBox",
  "description": "极简呼吸圆环表盘、心流 +5m 顺延与到点系统通知，支持按需启用专注事项、今日目标与复盘记录。",
  "icon": "\uE916",
  "minSize": { "width": 248, "height": 292 },
  "defaultSize": { "width": 276, "height": 312 },
  "i18n": {
    "en-US": {
      "widgetName": "Pomodoro Focus",
      "widgetDescription": "Minimalist breathing dial, +5m flow extension, and native toast notifications with optional task, daily goal, and session log.",
      "setModules": "Visible Modules & Order",
      "modTimer": "Pomodoro Dial",
      "modTask": "Current Focus Task",
      "modGoal": "Daily Goal & Streak",
      "modHistory": "Today's Sessions",
      "setFocusMin": "Focus Duration",
      "optFocus15": "15 min (Quick start)",
      "optFocus25": "25 min (Classic Pomodoro)",
      "optFocus35": "35 min (Extended focus)",
      "optFocus45": "45 min (Deep flow)",
      "optFocus60": "60 min (Immersion)",
      "setBreakPreset": "Short / Long Break",
      "optBreak3_10": "3m short · 10m long (Compact)",
      "optBreak5_15": "5m short · 15m long (Every 4 rounds)",
      "optBreak5_20": "5m short · 20m long (Relaxed)",
      "optBreak10_25": "10m short · 25m long (Deep recovery)",
      "setDailyGoal": "Daily Focus Goal",
      "optGoal4": "4 sessions (~2 hours)",
      "optGoal6": "6 sessions (~3 hours)",
      "optGoal8": "8 sessions (~4 hours · Recommended)",
      "optGoal10": "10 sessions (Sprint)",
      "optGoal12": "12 sessions (Full day)",
      "setFlowMode": "Phase Transition",
      "optFlowSemi": "Auto-start break after focus (Recommended)",
      "optFlowManual": "Start every phase manually",
      "optFlowAuto": "Continuous auto-cycle",
      "setNotifyMode": "Completion Alert",
      "optNotifySound": "System notification + chime",
      "optNotifySilent": "Silent notification",
      "optNotifyNone": "Muted (visual state only)",
      "taskPlaceholder": "What are you focusing on right now…",
      "todayFocusedPrefix": "Focused today ",
      "roundsSuffix": " rounds",
      "todayLogTitle": "Today's Log",
      "copyReviewBtn": "Copy Log",
      "copyReviewTooltip": "Copy focus sessions as Markdown",
      "emptySessionsTitle": "No focus sessions yet today",
      "emptySessionsSub": "Completed focus rounds appear here automatically",
      "deleteSessionTooltip": "Delete session"
    },
    "zh-CN": {
      "widgetName": "番茄时钟",
      "widgetDescription": "极简呼吸圆环表盘、心流 +5m 顺延与到点系统通知，支持按需启用专注事项、今日目标与复盘记录。",
      "setModules": "展示内容与顺序",
      "modTimer": "番茄计时核心",
      "modTask": "当前专注事项",
      "modGoal": "今日目标与连胜",
      "modHistory": "今日专注记录",
      "setFocusMin": "专注时长",
      "optFocus15": "15 分钟（轻量起步）",
      "optFocus25": "25 分钟（标准番茄）",
      "optFocus35": "35 分钟（进阶专注）",
      "optFocus45": "45 分钟（深度心流）",
      "optFocus60": "60 分钟（沉浸攻坚）",
      "setBreakPreset": "短休息 / 长休息",
      "optBreak3_10": "短休 3m · 长休 10m（紧凑）",
      "optBreak5_15": "短休 5m · 长休 15m（标准每 4 轮）",
      "optBreak5_20": "短休 5m · 长休 20m（充分放空）",
      "optBreak10_25": "短休 10m · 长休 25m（深度恢复）",
      "setDailyGoal": "每日专注目标",
      "optGoal4": "4 个番茄（约 2 小时）",
      "optGoal6": "6 个番茄（约 3 小时）",
      "optGoal8": "8 个番茄（约 4 小时 · 推荐）",
      "optGoal10": "10 个番茄（高强度冲刺）",
      "optGoal12": "12 个番茄（全天投入）",
      "setFlowMode": "阶段流转方式",
      "optFlowSemi": "专注后自动休息（推荐）",
      "optFlowManual": "每阶段结束手动开始",
      "optFlowAuto": "专注与休息全自动循环",
      "setNotifyMode": "到点提醒方式",
      "optNotifySound": "系统通知 + 轻柔提示音",
      "optNotifySilent": "仅系统通知（静音）",
      "optNotifyNone": "静默（仅卡片状态切换）",
      "taskPlaceholder": "写下当前专注的一件事…",
      "todayFocusedPrefix": "今日专注 ",
      "roundsSuffix": " 轮",
      "todayLogTitle": "今日记录",
      "copyReviewBtn": "复制复盘",
      "copyReviewTooltip": "复制 Markdown 格式专注记录",
      "emptySessionsTitle": "今日尚未完成专注",
      "emptySessionsSub": "完成一轮专注倒计时后会自动记录",
      "deleteSessionTooltip": "删除此条记录"
    },
    "zh-TW": {
      "widgetName": "番茄時鐘",
      "widgetDescription": "極簡呼吸圓環錶盤、心流 +5m 順延與到點系統通知，支援按需啟用專注事項、今日目標與復盤紀錄。",
      "setModules": "顯示內容與順序",
      "modTimer": "番茄計時核心",
      "modTask": "目前專注事項",
      "modGoal": "今日目標與連勝",
      "modHistory": "今日專注紀錄",
      "setFocusMin": "專注時長",
      "optFocus15": "15 分鐘（輕量起步）",
      "optFocus25": "25 分鐘（標準番茄）",
      "optFocus35": "35 分鐘（進階專注）",
      "optFocus45": "45 分鐘（深度心流）",
      "optFocus60": "60 分鐘（沉浸攻堅）",
      "setBreakPreset": "短休息 / 長休息",
      "optBreak3_10": "短休 3m · 長休 10m（緊湊）",
      "optBreak5_15": "短休 5m · 長休 15m（標準每 4 輪）",
      "optBreak5_20": "短休 5m · 長休 20m（充分放空）",
      "optBreak10_25": "短休 10m · 長休 25m（深度恢復）",
      "setDailyGoal": "每日專注目標",
      "optGoal4": "4 個番茄（約 2 小時）",
      "optGoal6": "6 個番茄（約 3 小時）",
      "optGoal8": "8 個番茄（約 4 小時 · 推薦）",
      "optGoal10": "10 個番茄（高強度衝刺）",
      "optGoal12": "12 個番茄（全天投入）",
      "setFlowMode": "階段流轉方式",
      "optFlowSemi": "專注後自動休息（推薦）",
      "optFlowManual": "每階段結束手動開始",
      "optFlowAuto": "專注與休息全自動循環",
      "setNotifyMode": "到點提醒方式",
      "optNotifySound": "系統通知 + 輕柔提示音",
      "optNotifySilent": "僅系統通知（靜音）",
      "optNotifyNone": "靜默（僅卡片狀態切換）",
      "taskPlaceholder": "寫下目前專注的一件事…",
      "todayFocusedPrefix": "今日專注 ",
      "roundsSuffix": " 輪",
      "todayLogTitle": "今日紀錄",
      "copyReviewBtn": "複製復盤",
      "copyReviewTooltip": "複製 Markdown 格式專注紀錄",
      "emptySessionsTitle": "今日尚未完成專注",
      "emptySessionsSub": "完成一輪專注倒數後會自動記錄",
      "deleteSessionTooltip": "刪除此筆紀錄"
    },
    "ja-JP": {
      "widgetName": "ポモドーロ",
      "widgetDescription": "ミニマルな円形ダイヤル、+5分延長、システム通知に対応。集中タスク・目標・履歴モジュールを自在に切替。",
      "setModules": "表示項目と順序",
      "modTimer": "ポモドーロタイマー",
      "modTask": "現在の集中タスク",
      "modGoal": "今日の目標と連続記録",
      "modHistory": "今日の集中履歴",
      "setFocusMin": "集中時間",
      "optFocus15": "15分（ウォームアップ）",
      "optFocus25": "25分（標準ポモドーロ）",
      "optFocus35": "35分（しっかり集中）",
      "optFocus45": "45分（ディープフロー）",
      "optFocus60": "60分（没頭モード）",
      "setBreakPreset": "短い休憩 / 長い休憩",
      "optBreak3_10": "短3m · 長10m（コンパクト）",
      "optBreak5_15": "短5m · 長15m（標準4セット毎）",
      "optBreak5_20": "短5m · 長20m（ゆったり）",
      "optBreak10_25": "短10m · 長25m（しっかり回復）",
      "setDailyGoal": "1日の目標セット数",
      "optGoal4": "4 セット（約2時間）",
      "optGoal6": "6 セット（約3時間）",
      "optGoal8": "8 セット（約4時間・推奨）",
      "optGoal10": "10 セット（集中スプリント）",
      "optGoal12": "12 セット（終日モード）",
      "setFlowMode": "フェーズ移行方式",
      "optFlowSemi": "集中後に自動で休憩開始（推奨）",
      "optFlowManual": "各フェーズを手動で開始",
      "optFlowAuto": "集中と休憩を自動ループ",
      "setNotifyMode": "完了時の通知",
      "optNotifySound": "システム通知 + サウンド",
      "optNotifySilent": "システム通知のみ（消音）",
      "optNotifyNone": "通知なし（カード表示のみ）",
      "taskPlaceholder": "今集中することを入力…",
      "todayFocusedPrefix": "今日の集中 ",
      "roundsSuffix": " 回",
      "todayLogTitle": "今日の記録",
      "copyReviewBtn": "記録コピー",
      "copyReviewTooltip": "Markdown 形式で集中記録をコピー",
      "emptySessionsTitle": "今日の完了セッションはありません",
      "emptySessionsSub": "1セット完了すると自動的に記録されます",
      "deleteSessionTooltip": "この記録を削除"
    },
    "ko-KR": {
      "widgetName": "포모도로 타이머",
      "widgetDescription": "미니멀 원형 다이얼, +5분 몰입 연장, 시스템 알림 및 집중 작업·일일 목표·기록 모듈을 지원합니다.",
      "setModules": "표시 항목 및 순서",
      "modTimer": "포모도로 타이머",
      "modTask": "현재 집중 작업",
      "modGoal": "오늘의 목표 및 연속 달성",
      "modHistory": "오늘의 집중 기록",
      "setFocusMin": "집중 시간",
      "optFocus15": "15분 (가벼운 시작)",
      "optFocus25": "25분 (표준 포모도로)",
      "optFocus35": "35분 (심화 집중)",
      "optFocus45": "45분 (딥 플로우)",
      "optFocus60": "60분 (완전 몰입)",
      "setBreakPreset": "짧은 휴식 / 긴 휴식",
      "optBreak3_10": "짧은 3m · 긴 10m (컴팩트)",
      "optBreak5_15": "짧은 5m · 긴 15m (4라운드마다 표준)",
      "optBreak5_20": "짧은 5m · 긴 20m (여유)",
      "optBreak10_25": "짧은 10m · 긴 25m (깊은 회복)",
      "setDailyGoal": "일일 집중 목표",
      "optGoal4": "4 세션 (약 2시간)",
      "optGoal6": "6 세션 (약 3시간)",
      "optGoal8": "8 세션 (약 4시간 · 권장)",
      "optGoal10": "10 세션 (고강도 스프린트)",
      "optGoal12": "12 세션 (종일 몰입)",
      "setFlowMode": "단계 전환 방식",
      "optFlowSemi": "집중 후 자동 휴식 시작 (권장)",
      "optFlowManual": "매 단계 수동 시작",
      "optFlowAuto": "집중·휴식 전체 자동 순환",
      "setNotifyMode": "완료 알림 방식",
      "optNotifySound": "시스템 알림 + 알림음",
      "optNotifySilent": "시스템 알림만 (무음)",
      "optNotifyNone": "무음 (카드 상태만 전환)",
      "taskPlaceholder": "지금 집중할 한 가지 일을 적어보세요…",
      "todayFocusedPrefix": "오늘 집중 ",
      "roundsSuffix": " 라운드",
      "todayLogTitle": "오늘 기록",
      "copyReviewBtn": "회고 복사",
      "copyReviewTooltip": "Markdown 형식으로 집중 기록 복사",
      "emptySessionsTitle": "오늘 완료한 집중 세션이 없습니다",
      "emptySessionsSub": "집중 라운드를 완료하면 자동으로 기록됩니다",
      "deleteSessionTooltip": "이 기록 삭제"
    }
  },
  "settings": [
    {
      "key": "modules",
      "type": "moduleList",
      "label": "${$i18n.setModules}",
      "default": "timer:1,task:0,goal:0,history:0",
      "options": [
        { "value": "timer", "label": "${$i18n.modTimer}" },
        { "value": "task", "label": "${$i18n.modTask}" },
        { "value": "goal", "label": "${$i18n.modGoal}" },
        { "value": "history", "label": "${$i18n.modHistory}" }
      ]
    },
    {
      "key": "focusMinutes",
      "type": "choice",
      "label": "${$i18n.setFocusMin}",
      "default": "25",
      "options": [
        { "value": "15", "label": "${$i18n.optFocus15}" },
        { "value": "25", "label": "${$i18n.optFocus25}" },
        { "value": "35", "label": "${$i18n.optFocus35}" },
        { "value": "45", "label": "${$i18n.optFocus45}" },
        { "value": "60", "label": "${$i18n.optFocus60}" }
      ]
    },
    {
      "key": "breakPreset",
      "type": "choice",
      "label": "${$i18n.setBreakPreset}",
      "default": "5,15",
      "options": [
        { "value": "3,10", "label": "${$i18n.optBreak3_10}" },
        { "value": "5,15", "label": "${$i18n.optBreak5_15}" },
        { "value": "5,20", "label": "${$i18n.optBreak5_20}" },
        { "value": "10,25", "label": "${$i18n.optBreak10_25}" }
      ]
    },
    {
      "key": "dailyGoal",
      "type": "choice",
      "label": "${$i18n.setDailyGoal}",
      "default": "8",
      "options": [
        { "value": "4", "label": "${$i18n.optGoal4}" },
        { "value": "6", "label": "${$i18n.optGoal6}" },
        { "value": "8", "label": "${$i18n.optGoal8}" },
        { "value": "10", "label": "${$i18n.optGoal10}" },
        { "value": "12", "label": "${$i18n.optGoal12}" }
      ]
    },
    {
      "key": "flowMode",
      "type": "choice",
      "label": "${$i18n.setFlowMode}",
      "default": "semi",
      "options": [
        { "value": "semi", "label": "${$i18n.optFlowSemi}" },
        { "value": "manual", "label": "${$i18n.optFlowManual}" },
        { "value": "auto", "label": "${$i18n.optFlowAuto}" }
      ]
    },
    {
      "key": "notifyMode",
      "type": "choice",
      "label": "${$i18n.setNotifyMode}",
      "default": "toast_sound",
      "options": [
        { "value": "toast_sound", "label": "${$i18n.optNotifySound}" },
        { "value": "toast_silent", "label": "${$i18n.optNotifySilent}" },
        { "value": "none", "label": "${$i18n.optNotifyNone}" }
      ]
    }
  ],
  "state": {
    "currentTask": ""
  },
  "collections": {
    "sessions": { "maxItems": 100, "sort": "newestFirst" }
  },
  "sources": [
    { "type": "pomodoro", "intervalMs": 1000 }
  ],
  "capsule": {
    "title": "${$pomodoro.capsuleTitle}",
    "summary": "${$pomodoro.capsuleSummary}",
    "glyph": "${$pomodoro.glyph}",
    "badge": "${$pomodoro.capsuleBadge}",
    "progress": "${$pomodoro.progressPercent}",
    "enableBounceOnUpdate": false
  },
  "view": {
    "type": "ModuleStack",
    "bind": "settings.modules",
    "spacing": 10,
    "padding": "12,6,12,12",
    "children": [
      {
        "id": "timer",
        "type": "PomodoroCard"
      },
      {
        "id": "task",
        "type": "Input",
        "placeholder": "${$i18n.taskPlaceholder}",
        "bind": "state.currentTask"
      },
      {
        "id": "goal",
        "type": "Stack",
        "spacing": 6,
        "children": [
          { "type": "Divider" },
          {
            "type": "Grid",
            "columns": "1*,Auto",
            "children": [
              {
                "type": "Text",
                "column": 0,
                "text": "${$i18n.todayFocusedPrefix}${$pomodoro.todayDurationText}",
                "style": "bodyStrong"
              },
              {
                "type": "Text",
                "column": 1,
                "text": "${$pomodoro.streakBadge} · ${$pomodoro.todayCompleted}/${$pomodoro.dailyGoal}${$i18n.roundsSuffix}",
                "style": "caption",
                "color": "secondary",
                "verticalAlignment": "center"
              }
            ]
          },
          {
            "type": "ProgressBar",
            "progress": "${$pomodoro.goalPercent}",
            "color": "accent",
            "warningThreshold": 999,
            "dangerThreshold": 999
          }
        ]
      },
      {
        "id": "history",
        "type": "Stack",
        "spacing": 6,
        "children": [
          { "type": "Divider" },
          {
            "type": "Grid",
            "columns": "1*,Auto",
            "children": [
              {
                "type": "Text",
                "column": 0,
                "text": "${$i18n.todayLogTitle}",
                "style": "caption",
                "color": "secondary",
                "verticalAlignment": "center"
              },
              {
                "type": "Button",
                "column": 1,
                "icon": "\uE8C8",
                "text": "${$i18n.copyReviewBtn}",
                "variant": "ghost",
                "tooltip": "${$i18n.copyReviewTooltip}",
                "onClick": {
                  "type": "collection.exportMarkdown",
                  "collection": "sessions"
                }
              }
            ]
          },
          {
            "type": "List",
            "items": "sessions",
            "maxLines": 5,
            "filter": "${$item.date == $pomodoro.todayDate}",
            "emptyState": {
              "icon": "\uE823",
              "title": "${$i18n.emptySessionsTitle}",
              "subtitle": "${$i18n.emptySessionsSub}"
            },
            "hoverActions": [
              {
                "type": "Button",
                "icon": "\uE74D",
                "variant": "ghost",
                "tooltip": "${$i18n.deleteSessionTooltip}",
                "onClick": {
                  "type": "collection.remove",
                  "collection": "sessions",
                  "targetId": "${$item.id}"
                }
              }
            ],
            "itemTemplate": {
              "type": "Grid",
              "columns": "Auto,1*,Auto",
              "spacing": 8,
              "children": [
                {
                  "type": "Text",
                  "column": 0,
                  "text": "${$item.timeText}",
                  "style": "caption",
                  "color": "muted",
                  "verticalAlignment": "center"
                },
                {
                  "type": "Text",
                  "column": 1,
                  "text": "${$item.task}",
                  "style": "body",
                  "maxLines": 1,
                  "verticalAlignment": "center"
                },
                {
                  "type": "Text",
                  "column": 2,
                  "text": "${$item.duration}",
                  "style": "caption",
                  "color": "secondary",
                  "verticalAlignment": "center"
                }
              ]
            }
          }
        ]
      }
    ]
  }
}
""";

    public const string SystemPulseManifestJson = """
{
  "schemaVersion": 1,
  "id": "deskbox.sys-pulse",
  "name": "系统监控",
  "version": "3.1.0",
  "author": "DeskBox",
  "description": "实时监测 CPU、内存、GPU 显存与运行中应用排行，支持自由拖拽卡片顺序与按需开关模块。",
  "icon": "\uE9D9",
  "minSize": { "width": 276, "height": 220 },
  "defaultSize": { "width": 336, "height": 436 },
  "i18n": {
    "en-US": {
      "widgetName": "System Monitor",
      "widgetDescription": "Live CPU, RAM, GPU VRAM, network throughput, and top running apps with drag-to-reorder modules.",
      "setModules": "Visible Modules & Order",
      "modCpu": "CPU Load Trend",
      "modMemory": "Memory Usage Trend",
      "modGpu": "GPU Core & VRAM",
      "modProcesses": "Running Apps Ranking",
      "modNetwork": "Live Network Speed",
      "modStorage": "Disk Capacity & System Status",
      "setTopProc": "Top Apps Count",
      "optTop3": "Top 3",
      "optTop5": "Top 5",
      "optTop8": "Top 8",
      "optTop10": "Top 10",
      "setProcSort": "Default App Sort",
      "optSortMem": "By Memory",
      "optSortCpu": "By CPU",
      "setRefresh": "Sampling Interval",
      "optRef1s": "1 sec (Fast)",
      "optRef2s": "2 sec (Balanced)",
      "optRef5s": "5 sec (Low power)",
      "setDrive": "Target Drive",
      "optDriveAuto": "Auto (System drive)",
      "setWarn": "High Load Alert Threshold",
      "optWarn75": "75% (Sensitive)",
      "optWarn85": "85% (Standard)",
      "optWarn95": "95% (Peak only)",
      "optWarnOff": "Disable color alert",
      "capMemLabel": "RAM",
      "highLoadBadge": "High Load",
      "cpuSubtitle": "Live processor load",
      "memTitle": "Memory",
      "procTitle": "Running Apps",
      "netDown": "↓ Down",
      "netUp": "↑ Up",
      "diskTitlePrefix": "Disk (",
      "diskSubFree": "Free ",
      "diskSubOf": " GB / "
    },
    "zh-CN": {
      "widgetName": "系统监控",
      "widgetDescription": "实时监测 CPU、内存、GPU 显存与运行中应用排行，支持自由拖拽卡片顺序与按需开关模块。",
      "setModules": "展示内容与顺序",
      "modCpu": "CPU 负载趋势",
      "modMemory": "内存占用趋势",
      "modGpu": "GPU 核心与显存",
      "modProcesses": "运行中应用排行",
      "modNetwork": "实时上下行网速",
      "modStorage": "磁盘容量与系统状态",
      "setTopProc": "应用排行数量",
      "optTop3": "前 3 名",
      "optTop5": "前 5 名",
      "optTop8": "前 8 名",
      "optTop10": "前 10 名",
      "setProcSort": "应用默认排序",
      "optSortMem": "按内存占用",
      "optSortCpu": "按 CPU 占用",
      "setRefresh": "采样刷新频率",
      "optRef1s": "1 秒（高频）",
      "optRef2s": "2 秒（均衡推荐）",
      "optRef5s": "5 秒（省电静默）",
      "setDrive": "监控目标磁盘",
      "optDriveAuto": "自动（系统主盘）",
      "setWarn": "高负载变色阈值",
      "optWarn75": "75%（灵敏）",
      "optWarn85": "85%（标准）",
      "optWarn95": "95%（仅满载）",
      "optWarnOff": "关闭变色告警",
      "capMemLabel": "内存",
      "highLoadBadge": "高负载",
      "cpuSubtitle": "处理器实时负载",
      "memTitle": "内存",
      "procTitle": "运行中应用",
      "netDown": "↓ 下行",
      "netUp": "↑ 上行",
      "diskTitlePrefix": "磁盘 (",
      "diskSubFree": "可用 ",
      "diskSubOf": " GB / 共 "
    },
    "zh-TW": {
      "widgetName": "系統監控",
      "widgetDescription": "即時監測 CPU、記憶體、GPU 視訊記憶體與執行中應用程式排行，支援自由拖曳卡片順序與按需開關模組。",
      "setModules": "顯示內容與順序",
      "modCpu": "CPU 負載趨勢",
      "modMemory": "記憶體佔用趨勢",
      "modGpu": "GPU 核心與視訊記憶體",
      "modProcesses": "執行中應用程式排行",
      "modNetwork": "即時上下行網速",
      "modStorage": "磁碟容量與系統狀態",
      "setTopProc": "應用程式排行數量",
      "optTop3": "前 3 名",
      "optTop5": "前 5 名",
      "optTop8": "前 8 名",
      "optTop10": "前 10 名",
      "setProcSort": "應用程式預設排序",
      "optSortMem": "依記憶體佔用",
      "optSortCpu": "依 CPU 佔用",
      "setRefresh": "取樣重新整理頻率",
      "optRef1s": "1 秒（高頻）",
      "optRef2s": "2 秒（均衡推薦）",
      "optRef5s": "5 秒（省電靜默）",
      "setDrive": "監控目標磁碟",
      "optDriveAuto": "自動（系統主磁碟）",
      "setWarn": "高負載變色閾值",
      "optWarn75": "75%（靈敏）",
      "optWarn85": "85%（標準）",
      "optWarn95": "95%（僅滿載）",
      "optWarnOff": "關閉變色告警",
      "capMemLabel": "記憶體",
      "highLoadBadge": "高負載",
      "cpuSubtitle": "處理器即時負載",
      "memTitle": "記憶體",
      "procTitle": "執行中應用程式",
      "netDown": "↓ 下行",
      "netUp": "↑ 上行",
      "diskTitlePrefix": "磁碟 (",
      "diskSubFree": "可用 ",
      "diskSubOf": " GB / 共 "
    },
    "ja-JP": {
      "widgetName": "システムモニター",
      "widgetDescription": "CPU・メモリ・GPU VRAM・実行中アプリランキングをリアルタイム監視。モジュールの並び替えと表示切替に対応。",
      "setModules": "表示項目と順序",
      "modCpu": "CPU 負荷推移",
      "modMemory": "メモリ使用率推移",
      "modGpu": "GPU コアと VRAM",
      "modProcesses": "実行中アプリ順位",
      "modNetwork": "リアルタイム通信速度",
      "modStorage": "ディスク容量とシステム状態",
      "setTopProc": "アプリ表示件数",
      "optTop3": "上位 3 件",
      "optTop5": "上位 5 件",
      "optTop8": "上位 8 件",
      "optTop10": "上位 10 件",
      "setProcSort": "既定の並び順",
      "optSortMem": "メモリ使用量順",
      "optSortCpu": "CPU 使用率順",
      "setRefresh": "更新間隔",
      "optRef1s": "1 秒（高頻度）",
      "optRef2s": "2 秒（バランス推奨）",
      "optRef5s": "5 秒（省電力）",
      "setDrive": "監視対象ドライブ",
      "optDriveAuto": "自動（システムドライブ）",
      "setWarn": "高負荷アラート閾値",
      "optWarn75": "75%（高感度）",
      "optWarn85": "85%（標準）",
      "optWarn95": "95%（ピーク時のみ）",
      "optWarnOff": "色変化アラートをオフ",
      "capMemLabel": "メモリ",
      "highLoadBadge": "高負荷",
      "cpuSubtitle": "プロセッサ負荷",
      "memTitle": "メモリ",
      "procTitle": "実行中のアプリ",
      "netDown": "↓ 受信",
      "netUp": "↑ 送信",
      "diskTitlePrefix": "ディスク (",
      "diskSubFree": "空き ",
      "diskSubOf": " GB / 計 "
    },
    "ko-KR": {
      "widgetName": "시스템 모니터",
      "widgetDescription": "CPU, 메모리, GPU VRAM 및 실행 중인 앱 순위를 실시간 모니터링하며 모듈 순서 변경과 표시 전환을 지원합니다.",
      "setModules": "표시 항목 및 순서",
      "modCpu": "CPU 부하 추이",
      "modMemory": "메모리 사용 추이",
      "modGpu": "GPU 코어 및 VRAM",
      "modProcesses": "실행 중인 앱 순위",
      "modNetwork": "실시간 네트워크 속도",
      "modStorage": "디스크 용량 및 시스템 상태",
      "setTopProc": "앱 순위 표시 개수",
      "optTop3": "상위 3개",
      "optTop5": "상위 5개",
      "optTop8": "상위 8개",
      "optTop10": "상위 10개",
      "setProcSort": "앱 기본 정렬",
      "optSortMem": "메모리 사용량순",
      "optSortCpu": "CPU 사용률순",
      "setRefresh": "갱신 주기",
      "optRef1s": "1초 (고빈도)",
      "optRef2s": "2초 (균형 권장)",
      "optRef5s": "5초 (절전)",
      "setDrive": "모니터링 대상 디스크",
      "optDriveAuto": "자동 (시스템 드라이브)",
      "setWarn": "고부하 경고 임계값",
      "optWarn75": "75% (민감)",
      "optWarn85": "85% (표준)",
      "optWarn95": "95% (최대 부하만)",
      "optWarnOff": "색상 경고 끄기",
      "capMemLabel": "메모리",
      "highLoadBadge": "고부하",
      "cpuSubtitle": "실시간 프로세서 부하",
      "memTitle": "메모리",
      "procTitle": "실행 중인 앱",
      "netDown": "↓ 다운",
      "netUp": "↑ 업",
      "diskTitlePrefix": "디스크 (",
      "diskSubFree": "여유 ",
      "diskSubOf": " GB / 총 "
    }
  },
  "settings": [
    {
      "key": "modules",
      "type": "moduleList",
      "label": "${$i18n.setModules}",
      "default": "cpu:1,memory:1,gpu:1,processes:1,network:1,storage:0",
      "options": [
        { "value": "cpu", "label": "${$i18n.modCpu}" },
        { "value": "memory", "label": "${$i18n.modMemory}" },
        { "value": "gpu", "label": "${$i18n.modGpu}" },
        { "value": "processes", "label": "${$i18n.modProcesses}" },
        { "value": "network", "label": "${$i18n.modNetwork}" },
        { "value": "storage", "label": "${$i18n.modStorage}" }
      ]
    },
    {
      "key": "topProcessCount",
      "type": "choice",
      "label": "${$i18n.setTopProc}",
      "default": "5",
      "options": [
        { "value": "3", "label": "${$i18n.optTop3}" },
        { "value": "5", "label": "${$i18n.optTop5}" },
        { "value": "8", "label": "${$i18n.optTop8}" },
        { "value": "10", "label": "${$i18n.optTop10}" }
      ]
    },
    {
      "key": "defaultProcessSort",
      "type": "choice",
      "label": "${$i18n.setProcSort}",
      "default": "memory",
      "options": [
        { "value": "memory", "label": "${$i18n.optSortMem}" },
        { "value": "cpu", "label": "${$i18n.optSortCpu}" }
      ]
    },
    {
      "key": "refreshRate",
      "type": "choice",
      "label": "${$i18n.setRefresh}",
      "default": "2000",
      "options": [
        { "value": "1000", "label": "${$i18n.optRef1s}" },
        { "value": "2000", "label": "${$i18n.optRef2s}" },
        { "value": "5000", "label": "${$i18n.optRef5s}" }
      ]
    },
    {
      "key": "targetDrive",
      "type": "choice",
      "label": "${$i18n.setDrive}",
      "default": "auto",
      "options": [
        { "value": "auto", "label": "${$i18n.optDriveAuto}" }
      ]
    },
    {
      "key": "warnThreshold",
      "type": "choice",
      "label": "${$i18n.setWarn}",
      "default": "85",
      "options": [
        { "value": "75", "label": "${$i18n.optWarn75}" },
        { "value": "85", "label": "${$i18n.optWarn85}" },
        { "value": "95", "label": "${$i18n.optWarn95}" },
        { "value": "999", "label": "${$i18n.optWarnOff}" }
      ]
    }
  ],
  "state": {
    "processSort": ""
  },
  "sources": [
    { "type": "systemMetrics", "intervalMs": 2000 }
  ],
  "capsule": {
    "title": "CPU ${$sys.cpu.percent}% · ${$i18n.capMemLabel} ${$sys.memory.percent}% · GPU ${$sys.gpu.percent}%",
    "summary": "↓ ${$sys.net.downText} · ↑ ${$sys.net.upText}",
    "glyph": "\uE9D9",
    "progress": "${$sys.cpu.percent}",
    "badge": "${$sys.cpu.percent >= $settings.warnThreshold ? $i18n.highLoadBadge : ''}",
    "badgeIsWarning": "${$sys.cpu.percent >= $settings.warnThreshold}"
  },
  "view": {
    "type": "Stack",
    "spacing": 8,
    "padding": "12,8,12,12",
    "children": [
      {
        "type": "ModuleStack",
        "bind": "settings.modules",
        "spacing": 8,
        "children": [
          {
            "id": "cpu",
            "type": "StatCard",
            "compactPair": true,
            "color": "accent",
            "title": "CPU",
            "value": "${$sys.cpu.percent}%",
            "subtitle": "${$i18n.cpuSubtitle}",
            "progress": "${$sys.cpu.percent}",
            "sparkline": "$sys.cpu.history",
            "warningThreshold": "${$settings.warnThreshold}"
          },
          {
            "id": "memory",
            "type": "StatCard",
            "compactPair": true,
            "color": "violet",
            "title": "${$i18n.memTitle}",
            "value": "${$sys.memory.percent}%",
            "subtitle": "${$sys.memory.summaryText}",
            "progress": "${$sys.memory.percent}",
            "sparkline": "$sys.memory.history",
            "warningThreshold": "${$settings.warnThreshold}"
          },
          {
            "id": "gpu",
            "type": "GpuCard",
            "title": "GPU",
            "color": "emerald",
            "warningThreshold": "${$settings.warnThreshold}"
          },
          {
            "id": "processes",
            "type": "ProcessList",
            "title": "${$i18n.procTitle}"
          },
          {
            "id": "network",
            "type": "Card",
            "tone": "subtle",
            "cornerRadius": 6,
            "padding": "12,9,12,9",
            "children": [
              {
                "type": "Grid",
                "columns": "1*,Auto,1*",
                "spacing": 12,
                "children": [
                  {
                    "type": "Grid",
                    "column": 0,
                    "columns": "Auto,1*",
                    "spacing": 6,
                    "children": [
                      {
                        "type": "Text",
                        "column": 0,
                        "text": "${$i18n.netDown}",
                        "style": "caption",
                        "color": "secondary",
                        "verticalAlignment": "center"
                      },
                      {
                        "type": "Text",
                        "column": 1,
                        "text": "${$sys.net.downText}",
                        "style": "bodyStrong",
                        "horizontalAlignment": "right",
                        "verticalAlignment": "center"
                      }
                    ]
                  },
                  {
                    "type": "Text",
                    "column": 1,
                    "text": "·",
                    "style": "caption",
                    "color": "muted",
                    "verticalAlignment": "center"
                  },
                  {
                    "type": "Grid",
                    "column": 2,
                    "columns": "Auto,1*",
                    "spacing": 6,
                    "children": [
                      {
                        "type": "Text",
                        "column": 0,
                        "text": "${$i18n.netUp}",
                        "style": "caption",
                        "color": "secondary",
                        "verticalAlignment": "center"
                      },
                      {
                        "type": "Text",
                        "column": 1,
                        "text": "${$sys.net.upText}",
                        "style": "bodyStrong",
                        "horizontalAlignment": "right",
                        "verticalAlignment": "center"
                      }
                    ]
                  }
                ]
              }
            ]
          },
          {
            "id": "storage",
            "type": "Card",
            "tone": "subtle",
            "cornerRadius": 6,
            "padding": "12,10,12,10",
            "spacing": 8,
            "children": [
              {
                "type": "ProgressRow",
                "title": "${$i18n.diskTitlePrefix}${$sys.disk.selectedName})",
                "subtitle": "${$i18n.diskSubFree}${$sys.disk.selectedFreeGb}${$i18n.diskSubOf}${$sys.disk.selectedTotalGb} GB",
                "value": "${$sys.disk.selectedPercent}%",
                "progress": "${$sys.disk.selectedPercent}",
                "warningThreshold": "${$settings.warnThreshold}"
              },
              {
                "type": "KeyValueRow",
                "title": "${$sys.status.label}",
                "value": "${$sys.status.value}"
              }
            ]
          }
        ]
      }
    ]
  }
}
""";

    public const string CountdownManifestJson = """
{
  "schemaVersion": 1,
  "id": "deskbox.countdown",
  "name": "倒数日",
  "version": "2.1.0",
  "author": "DeskBox",
  "description": "追踪重要倒数日、发薪日与纪念日，支持公农历自动循环、正数日累计与时光点阵进度。",
  "icon": "\uE787",
  "minSize": { "width": 260, "height": 200 },
  "defaultSize": { "width": 308, "height": 336 },
  "i18n": {
    "en-US": {
      "widgetName": "Countdown",
      "widgetDescription": "Track deadlines, paydays, and anniversaries with recurring solar/lunar schedules, days-since count-up, and dot-matrix time progress.",
      "setModules": "Visible Modules & Order",
      "modHero": "Pinned Hero Card",
      "modList": "Upcoming Milestones",
      "modYearProgress": "Dot-Matrix Time Progress",
      "setExpired": "After One-Time Date Passes",
      "optExpCountup": "Switch to days-since count-up (Day N)",
      "optExpArchive": "Dim and move to bottom (N days ago)",
      "setSort": "Sort Order",
      "optSortSmart": "Smart sort by remaining days",
      "optSortCustom": "Keep manual order",
      "setProgressScope": "Dot-Matrix Scope",
      "optScopeYear": "Current year (36 dots)",
      "optScopeMonth": "Current month (daily dots)",
      "optScopeBoth": "Show both year and month"
    },
    "zh-CN": {
      "widgetName": "倒数日",
      "widgetDescription": "追踪重要倒数日、发薪日与纪念日，支持公农历自动循环、正数日累计与时光点阵进度。",
      "setModules": "显示内容与顺序",
      "modHero": "置顶焦点卡",
      "modList": "近期日程列表",
      "modYearProgress": "时光点阵进度",
      "setExpired": "非循环到期后处理",
      "optExpCountup": "自动转为正数累计（已经 N 天）",
      "optExpArchive": "淡化归档至列表底部（已过 N 天）",
      "setSort": "列表排序方式",
      "optSortSmart": "按剩余天数智能排序",
      "optSortCustom": "按添加顺序排列",
      "setProgressScope": "时光点阵范围",
      "optScopeYear": "本年进度（36 格点阵）",
      "optScopeMonth": "本月逐日点阵",
      "optScopeBoth": "同时显示本年与本月"
    },
    "zh-TW": {
      "widgetName": "倒數日",
      "widgetDescription": "追蹤重要倒數日、發薪日與紀念日，支援公農曆自動循環、正數日累計與時光點陣進度。",
      "setModules": "顯示內容與順序",
      "modHero": "置頂焦點卡",
      "modList": "近期日程清單",
      "modYearProgress": "時光點陣進度",
      "setExpired": "非循環到期後處理",
      "optExpCountup": "自動轉為正數累計（已經 N 天）",
      "optExpArchive": "淡化封存至清單底部（已過 N 天）",
      "setSort": "清單排序方式",
      "optSortSmart": "依剩餘天數智慧排序",
      "optSortCustom": "依新增順序排列",
      "setProgressScope": "時光點陣範圍",
      "optScopeYear": "本年進度（36 格點陣）",
      "optScopeMonth": "本月逐日點陣",
      "optScopeBoth": "同時顯示本年與本月"
    },
    "ja-JP": {
      "widgetName": "カウントダウン",
      "widgetDescription": "大切な予定・給料日・記念日を追跡。新暦・旧暦の自動繰り返し、経過日数カウント、ドット進捗表示に対応。",
      "setModules": "表示項目と順序",
      "modHero": "ピン留めフォーカスカード",
      "modList": "予定リスト",
      "modYearProgress": "ドット進捗表示",
      "setExpired": "単発イベント経過後の動作",
      "optExpCountup": "経過日数カウントに切替（N日目）",
      "optExpArchive": "リスト下部に控えめ表示（N日前）",
      "setSort": "並び順",
      "optSortSmart": "残り日数でスマート並び替え",
      "optSortCustom": "追加順を維持",
      "setProgressScope": "ドット進捗の範囲",
      "optScopeYear": "今年の進捗（36ドット）",
      "optScopeMonth": "今月の日別ドット",
      "optScopeBoth": "今年と今月の両方を表示"
    },
    "ko-KR": {
      "widgetName": "디데이 카운트다운",
      "widgetDescription": "중요한 디데이, 급여일, 기념일을 추적하며 양·음력 자동 반복, 경과일 누적, 도트 시간 진행률을 지원합니다.",
      "setModules": "표시 항목 및 순서",
      "modHero": "상단 포커스 카드",
      "modList": "다가오는 일정 목록",
      "modYearProgress": "도트 시간 진행률",
      "setExpired": "일회성 일정 만료 후 처리",
      "optExpCountup": "경과일 누적으로 자동 전환 (N일째)",
      "optExpArchive": "목록 하단에 흐리게 보관 (N일 지남)",
      "setSort": "목록 정렬 방식",
      "optSortSmart": "남은 일수 기준 스마트 정렬",
      "optSortCustom": "추가한 순서대로 표시",
      "setProgressScope": "도트 진행률 범위",
      "optScopeYear": "올해 진행률 (36칸 도트)",
      "optScopeMonth": "이번 달 일별 도트",
      "optScopeBoth": "올해와 이번 달 함께 표시"
    }
  },
  "settings": [
    {
      "key": "modules",
      "type": "moduleList",
      "label": "${$i18n.setModules}",
      "default": "hero:1,list:1,yearProgress:0",
      "options": [
        { "value": "hero", "label": "${$i18n.modHero}" },
        { "value": "list", "label": "${$i18n.modList}" },
        { "value": "yearProgress", "label": "${$i18n.modYearProgress}" }
      ]
    },
    {
      "key": "expiredMode",
      "type": "choice",
      "label": "${$i18n.setExpired}",
      "default": "countup",
      "options": [
        { "value": "countup", "label": "${$i18n.optExpCountup}" },
        { "value": "archive", "label": "${$i18n.optExpArchive}" }
      ]
    },
    {
      "key": "sortMode",
      "type": "choice",
      "label": "${$i18n.setSort}",
      "default": "smart",
      "options": [
        { "value": "smart", "label": "${$i18n.optSortSmart}" },
        { "value": "custom", "label": "${$i18n.optSortCustom}" }
      ]
    },
    {
      "key": "progressScope",
      "type": "choice",
      "label": "${$i18n.setProgressScope}",
      "default": "year",
      "options": [
        { "value": "year", "label": "${$i18n.optScopeYear}" },
        { "value": "month", "label": "${$i18n.optScopeMonth}" },
        { "value": "both", "label": "${$i18n.optScopeBoth}" }
      ]
    }
  ],
  "state": {},
  "collections": {
    "events": { "maxItems": 200, "sort": "newestFirst" }
  },
  "sources": [
    { "type": "countdown", "intervalMs": 60000 }
  ],
  "capsule": {
    "title": "${$countdown.capsuleTitle}",
    "summary": "${$countdown.capsuleSummary}",
    "glyph": "${$countdown.capsuleGlyph}",
    "badge": "${$countdown.capsuleBadge}",
    "badgeIsWarning": "${$countdown.capsuleIsWarning}",
    "progress": "${$countdown.capsuleProgress}"
  },
  "view": {
    "type": "ModuleStack",
    "bind": "settings.modules",
    "spacing": 10,
    "padding": "12,8,12,12",
    "children": [
      {
        "id": "hero",
        "type": "CountdownHero"
      },
      {
        "id": "list",
        "type": "CountdownList"
      },
      {
        "id": "yearProgress",
        "type": "TimeProgressCard"
      }
    ]
  }
}
""";

    public const string AiPulseManifestJson = """
{
  "schemaVersion": 1,
  "id": "deskbox.ai-pulse",
  "name": "AI 监控",
  "version": "2.1.0",
  "author": "DeskBox",
  "description": "实时监控 Claude Code / Codex 本地会话窗口配额与智谱 GLM / DeepSeek / Kimi / OpenRouter 账户余额。",
  "icon": "\uE950",
  "minSize": { "width": 284, "height": 200 },
  "defaultSize": { "width": 324, "height": 320 },
  "i18n": {
    "en-US": {
      "widgetName": "AI Monitor",
      "widgetDescription": "Monitor Claude Code & Codex session rate-limit windows alongside Zhipu GLM, DeepSeek, Kimi, and OpenRouter account balances.",
      "setModules": "Visible Modules & Order",
      "modFocus": "Focused Quota Card",
      "modRadar": "Multi-Provider Radar",
      "modTokens": "Local Token & Cache Insights",
      "modTrend": "14-Day Usage Trend",
      "setQuotaMode": "Window Quota Display",
      "optQuotaRem": "Show remaining % (e.g., 85% left)",
      "optQuotaUsed": "Show used % (e.g., 15% used)",
      "setRefresh": "Auto-Polling Interval",
      "optRef1m": "Every 1 minute (Recommended)",
      "optRef5m": "Every 5 minutes",
      "optRef15m": "Every 15 minutes"
    },
    "zh-CN": {
      "widgetName": "AI 监控",
      "widgetDescription": "实时监控 Claude Code / Codex 本地会话窗口配额与智谱 GLM / DeepSeek / Kimi / OpenRouter 账户余额。",
      "setModules": "显示内容与顺序",
      "modFocus": "焦点额度主卡",
      "modRadar": "多平台速览列表",
      "modTokens": "本地 Token 与缓存透视",
      "modTrend": "近 14 日消耗柱状图",
      "setQuotaMode": "窗口配额百分比语义",
      "optQuotaRem": "显示剩余百分比（如 85% 剩余）",
      "optQuotaUsed": "显示已用百分比（如 15% 已用）",
      "setRefresh": "自动轮询刷新间隔",
      "optRef1m": "每 1 分钟（推荐）",
      "optRef5m": "每 5 分钟",
      "optRef15m": "每 15 分钟"
    },
    "zh-TW": {
      "widgetName": "AI 監控",
      "widgetDescription": "即時監控 Claude Code / Codex 本機工作階段視窗配額與智譜 GLM / DeepSeek / Kimi / OpenRouter 帳戶餘額。",
      "setModules": "顯示內容與順序",
      "modFocus": "焦點額度主卡",
      "modRadar": "多平台速覽清單",
      "modTokens": "本機 Token 與快取透視",
      "modTrend": "近 14 日消耗長條圖",
      "setQuotaMode": "視窗配額百分比語意",
      "optQuotaRem": "顯示剩餘百分比（如 85% 剩餘）",
      "optQuotaUsed": "顯示已用百分比（如 15% 已用）",
      "setRefresh": "自動輪詢重新整理間隔",
      "optRef1m": "每 1 分鐘（推薦）",
      "optRef5m": "每 5 分鐘",
      "optRef15m": "每 15 分鐘"
    },
    "ja-JP": {
      "widgetName": "AI モニター",
      "widgetDescription": "Claude Code / Codex のセッション枠使用量と GLM / DeepSeek / Kimi / OpenRouter の残高をリアルタイム監視。",
      "setModules": "表示項目と順序",
      "modFocus": "フォーカス枠カード",
      "modRadar": "マルチプラットフォーム一覧",
      "modTokens": "ローカルトークンとキャッシュ内訳",
      "modTrend": "直近14日間の消費グラフ",
      "setQuotaMode": "枠パーセント表示",
      "optQuotaRem": "残り率を表示（例：85% 残り）",
      "optQuotaUsed": "使用率を表示（例：15% 使用）",
      "setRefresh": "自動更新間隔",
      "optRef1m": "1 分ごと（推奨）",
      "optRef5m": "5 分ごと",
      "optRef15m": "15 分ごと"
    },
    "ko-KR": {
      "widgetName": "AI 모니터",
      "widgetDescription": "Claude Code / Codex 로컬 세션 한도 및 GLM / DeepSeek / Kimi / OpenRouter 계정 잔액을 실시간으로 모니터링합니다.",
      "setModules": "표시 항목 및 순서",
      "modFocus": "포커스 한도 카드",
      "modRadar": "멀티 플랫폼 요약 목록",
      "modTokens": "로컬 토큰 및 캐시 분석",
      "modTrend": "최근 14일 사용량 차트",
      "setQuotaMode": "세션 한도 퍼센트 표시",
      "optQuotaRem": "남은 비율 표시 (예: 85% 남음)",
      "optQuotaUsed": "사용 비율 표시 (예: 15% 사용)",
      "setRefresh": "자동 갱신 주기",
      "optRef1m": "1분마다 (권장)",
      "optRef5m": "5분마다",
      "optRef15m": "15분마다"
    }
  },
  "settings": [
    {
      "key": "modules",
      "type": "moduleList",
      "label": "${$i18n.setModules}",
      "default": "focus:1,radar:1,tokens:0,trend:0",
      "options": [
        { "value": "focus", "label": "${$i18n.modFocus}" },
        { "value": "radar", "label": "${$i18n.modRadar}" },
        { "value": "tokens", "label": "${$i18n.modTokens}" },
        { "value": "trend", "label": "${$i18n.modTrend}" }
      ]
    },
    {
      "key": "quotaDisplayMode",
      "type": "choice",
      "label": "${$i18n.setQuotaMode}",
      "default": "remaining",
      "options": [
        { "value": "remaining", "label": "${$i18n.optQuotaRem}" },
        { "value": "used", "label": "${$i18n.optQuotaUsed}" }
      ]
    },
    {
      "key": "refreshIntervalMs",
      "type": "choice",
      "label": "${$i18n.setRefresh}",
      "default": "60000",
      "options": [
        { "value": "60000", "label": "${$i18n.optRef1m}" },
        { "value": "300000", "label": "${$i18n.optRef5m}" },
        { "value": "900000", "label": "${$i18n.optRef15m}" }
      ]
    }
  ],
  "state": {
    "selectedProvider": "",
    "trendMetric": "tokens"
  },
  "sources": [
    { "type": "aiMonitor", "intervalMs": 60000 }
  ],
  "capsule": {
    "title": "${$ai.capsuleTitle}",
    "summary": "${$ai.capsuleSummary}",
    "glyph": "\uE950",
    "badge": "${$ai.capsuleBadge}",
    "badgeIsWarning": "${$ai.capsuleIsWarning}",
    "progress": "${$ai.capsuleProgress}"
  },
  "view": {
    "type": "ModuleStack",
    "bind": "settings.modules",
    "spacing": 10,
    "padding": "12,8,12,12",
    "children": [
      {
        "id": "focus",
        "type": "AiFocusCard"
      },
      {
        "id": "radar",
        "type": "AiRadarList"
      },
      {
        "id": "tokens",
        "type": "AiTokensCard"
      },
      {
        "id": "trend",
        "type": "AiTrendCard"
      }
    ]
  }
}
""";

    public const string MilestoneTrackerManifestJson = """
{
  "schemaVersion": 1,
  "id": "deskbox.milestone-tracker",
  "name": "项目里程碑",
  "version": "1.1.0",
  "author": "DeskBox",
  "description": "轻量追踪关键节点与交付物，支持拖入交付文件直接绑定到里程碑列表。",
  "icon": "🎯",
  "minSize": { "width": 280, "height": 300 },
  "defaultSize": { "width": 340, "height": 420 },
  "i18n": {
    "en-US": {
      "widgetName": "Milestone Tracker",
      "widgetDescription": "Track key project milestones and deliverables, with drag-and-drop file attachment.",
      "setDropMode": "File Drop Behavior",
      "optDropLink": "Link original file path",
      "optDropCopy": "Copy file to widget folder",
      "dropDeliverablePrefix": "Deliverable: ",
      "capTitlePrefix": "Milestones · ",
      "capTitleSuffix": "",
      "capEmptySummary": "Add key milestones or drop deliverables",
      "prioP0": "P0 Critical",
      "prioP1": "P1 Key",
      "prioP2": "P2 Normal",
      "inputPlaceholder": "Enter milestone goal…",
      "addBtn": "Add",
      "emptyTitle": "No milestones yet",
      "emptySubtitle": "Add phase goals above or drag deliverable files into the widget"
    },
    "zh-CN": {
      "widgetName": "项目里程碑",
      "widgetDescription": "轻量追踪关键节点与交付物，支持拖入交付文件直接绑定到里程碑列表。",
      "setDropMode": "拖入文件处理方式",
      "optDropLink": "关联原文件路径",
      "optDropCopy": "复制副本到格子目录",
      "dropDeliverablePrefix": "交付件：",
      "capTitlePrefix": "项目里程碑 · ",
      "capTitleSuffix": " 项",
      "capEmptySummary": "添加关键节点或拖入交付物",
      "prioP0": "P0 核心",
      "prioP1": "P1 重点",
      "prioP2": "P2 常规",
      "inputPlaceholder": "输入关键里程碑目标…",
      "addBtn": "添加",
      "emptyTitle": "尚无里程碑节点",
      "emptySubtitle": "添加阶段目标，或把交付文档拖入格子归档"
    },
    "zh-TW": {
      "widgetName": "專案里程碑",
      "widgetDescription": "輕量追蹤關鍵節點與交付物，支援拖入交付檔案直接綁定到里程碑清單。",
      "setDropMode": "拖入檔案處理方式",
      "optDropLink": "關聯原檔案路徑",
      "optDropCopy": "複製副本到格子目錄",
      "dropDeliverablePrefix": "交付件：",
      "capTitlePrefix": "專案里程碑 · ",
      "capTitleSuffix": " 項",
      "capEmptySummary": "新增關鍵節點或拖入交付物",
      "prioP0": "P0 核心",
      "prioP1": "P1 重點",
      "prioP2": "P2 常規",
      "inputPlaceholder": "輸入關鍵里程碑目標…",
      "addBtn": "新增",
      "emptyTitle": "尚無里程碑節點",
      "emptySubtitle": "新增階段目標，或將交付文件拖入格子歸檔"
    },
    "ja-JP": {
      "widgetName": "マイルストーン",
      "widgetDescription": "重要な節目と成果物を軽快に管理。成果物ファイルのドロップ紐付けに対応。",
      "setDropMode": "ドロップ時の動作",
      "optDropLink": "元ファイルのパスをリンク",
      "optDropCopy": "ウィジェットフォルダにコピー",
      "dropDeliverablePrefix": "成果物：",
      "capTitlePrefix": "マイルストーン · ",
      "capTitleSuffix": " 件",
      "capEmptySummary": "節目の追加や成果物のドロップが可能",
      "prioP0": "P0 最重要",
      "prioP1": "P1 重要",
      "prioP2": "P2 通常",
      "inputPlaceholder": "主要なマイルストーン目標を入力…",
      "addBtn": "追加",
      "emptyTitle": "マイルストーンはまだありません",
      "emptySubtitle": "フェーズ目標を追加するか、成果物ファイルをドロップしてください"
    },
    "ko-KR": {
      "widgetName": "마일스톤 트래커",
      "widgetDescription": "주요 프로젝트 이정표와 산출물을 추적하며 파일 드롭 연결을 지원합니다.",
      "setDropMode": "파일 드롭 처리 방식",
      "optDropLink": "원본 파일 경로 연결",
      "optDropCopy": "위젯 폴더로 사본 복사",
      "dropDeliverablePrefix": "산출물: ",
      "capTitlePrefix": "마일스톤 · ",
      "capTitleSuffix": "개",
      "capEmptySummary": "주요 이정표를 추가하거나 산출물을 드롭하세요",
      "prioP0": "P0 핵심",
      "prioP1": "P1 중요",
      "prioP2": "P2 일반",
      "inputPlaceholder": "주요 마일스톤 목표 입력…",
      "addBtn": "추가",
      "emptyTitle": "등록된 마일스톤이 없습니다",
      "emptySubtitle": "단계별 목표를 추가하거나 산출물 문서를 드롭하세요"
    }
  },
  "settings": [
    {
      "key": "dropMode",
      "type": "choice",
      "label": "${$i18n.setDropMode}",
      "default": "link",
      "options": [
        { "value": "link", "label": "${$i18n.optDropLink}" },
        { "value": "copy", "label": "${$i18n.optDropCopy}" }
      ]
    }
  ],
  "state": {
    "draftMilestone": "",
    "priority": "P1"
  },
  "collections": {
    "milestones": { "maxItems": 200, "sort": "newestFirst" }
  },
  "onDrop": {
    "accept": "files",
    "mode": "$settings.dropMode",
    "action": {
      "type": "collection.prepend",
      "collection": "milestones",
      "item": {
        "text": "${$i18n.dropDeliverablePrefix}${$drop.name}",
        "tag": "${state.priority}",
        "fileName": "${$drop.name}",
        "filePath": "${$drop.path}",
        "storageMode": "${$drop.mode}"
      }
    }
  },
  "capsule": {
    "title": "${$i18n.capTitlePrefix}${collections.milestones.count}${$i18n.capTitleSuffix}",
    "summary": "${collections.milestones.latest.text ?? $i18n.capEmptySummary}",
    "glyph": "🎯",
    "badge": "${collections.milestones.count}",
    "enableBounceOnUpdate": true
  },
  "view": {
    "type": "Stack",
    "spacing": 10,
    "padding": "12,10,12,12",
    "children": [
      {
        "type": "Card",
        "tone": "subtle",
        "padding": "10,10,10,10",
        "children": [
          {
            "type": "Stack",
            "spacing": 8,
            "children": [
              {
                "type": "Segmented",
                "bind": "state.priority",
                "options": [
                  { "value": "P0", "label": "${$i18n.prioP0}" },
                  { "value": "P1", "label": "${$i18n.prioP1}" },
                  { "value": "P2", "label": "${$i18n.prioP2}" }
                ]
              },
              {
                "type": "Grid",
                "columns": "1*,Auto",
                "spacing": 8,
                "children": [
                  {
                    "type": "Input",
                    "column": 0,
                    "placeholder": "${$i18n.inputPlaceholder}",
                    "bind": "state.draftMilestone",
                    "onSubmit": [
                      {
                        "type": "collection.prepend",
                        "guard": "${state.draftMilestone != ''}",
                        "collection": "milestones",
                        "item": {
                          "text": "${state.draftMilestone}",
                          "tag": "${state.priority}"
                        }
                      },
                      {
                        "type": "state.set",
                        "key": "draftMilestone",
                        "value": ""
                      }
                    ]
                  },
                  {
                    "type": "Button",
                    "column": 1,
                    "icon": "\uE710",
                    "text": "${$i18n.addBtn}",
                    "variant": "accent",
                    "onClick": [
                      {
                        "type": "collection.prepend",
                        "guard": "${state.draftMilestone != ''}",
                        "collection": "milestones",
                        "item": {
                          "text": "${state.draftMilestone}",
                          "tag": "${state.priority}"
                        }
                      },
                      {
                        "type": "state.set",
                        "key": "draftMilestone",
                        "value": ""
                      }
                    ]
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "type": "List",
        "items": "milestones",
        "emptyState": {
          "icon": "\uE73A",
          "title": "${$i18n.emptyTitle}",
          "subtitle": "${$i18n.emptySubtitle}"
        },
        "hoverActions": [
          {
            "type": "Button",
            "icon": "\uE74D",
            "variant": "ghost",
            "onClick": {
              "type": "collection.remove",
              "collection": "milestones",
              "targetId": "${$item.id}"
            }
          }
        ],
        "itemTemplate": {
          "type": "Stack",
          "spacing": 6,
          "children": [
            {
              "type": "Stack",
              "orientation": "horizontal",
              "spacing": 6,
              "children": [
                {
                  "type": "Badge",
                  "text": "${$item.tag}",
                  "tone": "accent"
                },
                {
                  "type": "Text",
                  "text": "${$item.timeText}",
                  "style": "caption",
                  "color": "muted"
                }
              ]
            },
            {
              "type": "Text",
              "text": "${$item.text}",
              "style": "body"
            },
            {
              "type": "FileChip",
              "$when": "${$item.filePath != ''}",
              "fileName": "${$item.fileName}",
              "filePath": "${$item.filePath}",
              "storageMode": "${$item.storageMode}"
            }
          ]
        }
      }
    ]
  }
}
""";

    public const string QuickSnippetsManifestJson = """
{
  "schemaVersion": 1,
  "id": "deskbox.quick-snippets",
  "name": "快捷复制板",
  "version": "1.1.0",
  "author": "DeskBox",
  "description": "收纳高频回复话术、终端命令与 AI 提示词，桌面点按一下立即写入剪贴板。",
  "icon": "📋",
  "minSize": { "width": 280, "height": 300 },
  "defaultSize": { "width": 340, "height": 420 },
  "i18n": {
    "en-US": {
      "widgetName": "Quick Snippets",
      "widgetDescription": "Keep frequent replies, CLI commands, and AI prompts ready for 1-click desktop clipboard copy.",
      "setDefaultCat": "Default Category",
      "catAll": "All",
      "catPhrase": "Phrases",
      "catCli": "CLI",
      "catPrompt": "Prompts",
      "capTitlePrefix": "Snippets · ",
      "capTitleSuffix": "",
      "capEmptySummary": "Save frequent commands, addresses, or prompts for 1-click copy",
      "inputPlaceholder": "Enter frequent text, command, or prompt (Enter to save)…",
      "saveBtn": "Save",
      "emptyTitle": "No snippets saved yet",
      "emptySubtitle": "Store email addresses, Git commands, or AI prompts for instant copy",
      "deleteTooltip": "Delete",
      "copyBtn": "Copy"
    },
    "zh-CN": {
      "widgetName": "快捷复制板",
      "widgetDescription": "收纳高频回复话术、终端命令与 AI 提示词，桌面点按一下立即写入剪贴板。",
      "setDefaultCat": "默认新建分类",
      "catAll": "全部",
      "catPhrase": "常用语",
      "catCli": "命令行",
      "catPrompt": "提示词",
      "capTitlePrefix": "快捷复制板 · ",
      "capTitleSuffix": " 条",
      "capEmptySummary": "添加高频口令、地址或提示词一键复制",
      "inputPlaceholder": "输入常用文本、命令或提示词（回车保存）…",
      "saveBtn": "保存",
      "emptyTitle": "还没有快捷片段",
      "emptySubtitle": "把常用的邮箱地址、Git 命令或 AI 提示词存进来，点一下即可复制",
      "deleteTooltip": "删除",
      "copyBtn": "复制"
    },
    "zh-TW": {
      "widgetName": "快捷複製板",
      "widgetDescription": "收納高頻回覆話術、終端機指令與 AI 提示詞，桌面點按一下立即寫入剪貼簿。",
      "setDefaultCat": "預設新建分類",
      "catAll": "全部",
      "catPhrase": "常用語",
      "catCli": "命令列",
      "catPrompt": "提示詞",
      "capTitlePrefix": "快捷複製板 · ",
      "capTitleSuffix": " 則",
      "capEmptySummary": "新增高頻口令、地址或提示詞一鍵複製",
      "inputPlaceholder": "輸入常用文字、指令或提示詞（Enter 儲存）…",
      "saveBtn": "儲存",
      "emptyTitle": "還沒有快捷片段",
      "emptySubtitle": "將常用的信箱地址、Git 指令或 AI 提示詞存進來，點一下即可複製",
      "deleteTooltip": "刪除",
      "copyBtn": "複製"
    },
    "ja-JP": {
      "widgetName": "クイックスニペット",
      "widgetDescription": "定型文・ターミナルコマンド・AI プロンプトを整理し、ワンクリックでクリップボードへコピー。",
      "setDefaultCat": "既定のカテゴリ",
      "catAll": "すべて",
      "catPhrase": "定型文",
      "catCli": "コマンド",
      "catPrompt": "プロンプト",
      "capTitlePrefix": "スニペット · ",
      "capTitleSuffix": " 件",
      "capEmptySummary": "よく使うコマンドやプロンプトをワンクリックコピー",
      "inputPlaceholder": "定型文・コマンド・プロンプトを入力（Enter で保存）…",
      "saveBtn": "保存",
      "emptyTitle": "スニペットはまだありません",
      "emptySubtitle": "メールアドレス、Git コマンド、AI プロンプトを登録してワンクリックコピー",
      "deleteTooltip": "削除",
      "copyBtn": "コピー"
    },
    "ko-KR": {
      "widgetName": "빠른 스니펫 보드",
      "widgetDescription": "자주 쓰는 문구, 터미널 명령어, AI 프롬프트를 저장하고 한 번 클릭으로 클립보드에 복사합니다.",
      "setDefaultCat": "기본 분류",
      "catAll": "전체",
      "catPhrase": "상용구",
      "catCli": "명령어",
      "catPrompt": "프롬프트",
      "capTitlePrefix": "스니펫 · ",
      "capTitleSuffix": "개",
      "capEmptySummary": "자주 쓰는 명령어나 프롬프트를 저장해 원클릭 복사하세요",
      "inputPlaceholder": "자주 쓰는 텍스트, 명령어, 프롬프트 입력 (Enter 저장)…",
      "saveBtn": "저장",
      "emptyTitle": "저장된 스니펫이 없습니다",
      "emptySubtitle": "이메일 주소, Git 명령어, AI 프롬프트를 저장해 한 번에 복사하세요",
      "deleteTooltip": "삭제",
      "copyBtn": "복사"
    }
  },
  "settings": [
    {
      "key": "defaultCategory",
      "type": "choice",
      "label": "${$i18n.setDefaultCat}",
      "default": "phrase",
      "options": [
        { "value": "phrase", "label": "${$i18n.catPhrase}" },
        { "value": "cli", "label": "${$i18n.catCli}" },
        { "value": "prompt", "label": "${$i18n.catPrompt}" }
      ]
    }
  ],
  "state": {
    "draftSnippet": "",
    "activeCategory": "phrase",
    "filterCategory": "all"
  },
  "collections": {
    "snippets": { "maxItems": 300, "sort": "newestFirst" }
  },
  "capsule": {
    "title": "${$i18n.capTitlePrefix}${collections.snippets.count}${$i18n.capTitleSuffix}",
    "summary": "${collections.snippets.latest.text ?? $i18n.capEmptySummary}",
    "glyph": "📋",
    "badge": "${collections.snippets.count}",
    "enableBounceOnUpdate": true
  },
  "view": {
    "type": "Stack",
    "spacing": 10,
    "padding": "12,10,12,12",
    "children": [
      {
        "type": "Card",
        "tone": "subtle",
        "padding": "10,10,10,10",
        "children": [
          {
            "type": "Stack",
            "spacing": 8,
            "children": [
              {
                "type": "Segmented",
                "bind": "state.activeCategory",
                "options": [
                  { "value": "phrase", "label": "${$i18n.catPhrase}" },
                  { "value": "cli", "label": "${$i18n.catCli}" },
                  { "value": "prompt", "label": "${$i18n.catPrompt}" }
                ]
              },
              {
                "type": "Grid",
                "columns": "1*,Auto",
                "spacing": 8,
                "children": [
                  {
                    "type": "Input",
                    "column": 0,
                    "placeholder": "${$i18n.inputPlaceholder}",
                    "bind": "state.draftSnippet",
                    "onSubmit": [
                      {
                        "type": "collection.prepend",
                        "guard": "${state.draftSnippet != ''}",
                        "collection": "snippets",
                        "item": {
                          "text": "${state.draftSnippet}",
                          "tag": "${state.activeCategory}"
                        }
                      },
                      {
                        "type": "state.set",
                        "key": "draftSnippet",
                        "value": ""
                      }
                    ]
                  },
                  {
                    "type": "Button",
                    "column": 1,
                    "icon": "\uE710",
                    "text": "${$i18n.saveBtn}",
                    "variant": "accent",
                    "onClick": [
                      {
                        "type": "collection.prepend",
                        "guard": "${state.draftSnippet != ''}",
                        "collection": "snippets",
                        "item": {
                          "text": "${state.draftSnippet}",
                          "tag": "${state.activeCategory}"
                        }
                      },
                      {
                        "type": "state.set",
                        "key": "draftSnippet",
                        "value": ""
                      }
                    ]
                  }
                ]
              }
            ]
          }
        ]
      },
      {
        "type": "Segmented",
        "bind": "state.filterCategory",
        "options": [
          { "value": "all", "label": "${$i18n.catAll}" },
          { "value": "phrase", "label": "${$i18n.catPhrase}" },
          { "value": "cli", "label": "${$i18n.catCli}" },
          { "value": "prompt", "label": "${$i18n.catPrompt}" }
        ]
      },
      {
        "type": "List",
        "items": "snippets",
        "filter": "${state.filterCategory == 'all' || state.filterCategory == '全部' || $item.tag == state.filterCategory}",
        "emptyState": {
          "icon": "\uE8C8",
          "title": "${$i18n.emptyTitle}",
          "subtitle": "${$i18n.emptySubtitle}"
        },
        "hoverActions": [
          {
            "type": "Button",
            "icon": "\uE74D",
            "variant": "ghost",
            "tooltip": "${$i18n.deleteTooltip}",
            "onClick": {
              "type": "collection.remove",
              "collection": "snippets",
              "targetId": "${$item.id}"
            }
          }
        ],
        "itemTemplate": {
          "type": "Grid",
          "columns": "1*,Auto",
          "spacing": 8,
          "children": [
            {
              "type": "Stack",
              "column": 0,
              "spacing": 4,
              "children": [
                {
                  "type": "Badge",
                  "text": "${$item.tag == 'phrase' ? $i18n.catPhrase : ($item.tag == 'cli' ? $i18n.catCli : ($item.tag == 'prompt' ? $i18n.catPrompt : $item.tag))}",
                  "tone": "accent"
                },
                {
                  "type": "Text",
                  "text": "${$item.text}",
                  "style": "body",
                  "maxLines": 3
                }
              ]
            },
            {
              "type": "Button",
              "column": 1,
              "icon": "\uE8C8",
              "text": "${$i18n.copyBtn}",
              "variant": "subtle",
              "onClick": {
                "type": "clipboard.copy",
                "text": "${$item.text}"
              }
            }
          ]
        }
      }
    ]
  }
}
""";

    public const string HydrationStarterManifestJson = """
{
  "schemaVersion": 1,
  "id": "custom.hydration-tracker",
  "name": "今日饮水打卡",
  "version": "1.1.0",
  "author": "DeskBox Studio",
  "description": "轻量记录每日饮水量与打卡时刻，支持胶囊进度预览。",
  "icon": "💧",
  "minSize": { "width": 268, "height": 280 },
  "defaultSize": { "width": 316, "height": 368 },
  "i18n": {
    "en-US": {
      "widgetName": "Hydration Tracker",
      "widgetDescription": "Log daily water intake and check-in times with live capsule progress.",
      "setDailyGoal": "Daily Goal (ml)",
      "capTitlePrefix": "Water · ",
      "capSubPrefix": "Logged ",
      "capSubSuffix": " times today",
      "cardTitle": "Today's Hydration",
      "cardSubGoal": "Goal ",
      "cardSubMid": " ml · Today ",
      "cardSubEnd": " cups",
      "noteWarm": "Glass of water",
      "noteQuick": "Quick sip",
      "resetTooltip": "Reset today's intake",
      "emptyTitle": "No water logged today",
      "emptySubtitle": "Tap a button above to log a cup",
      "deleteTooltip": "Delete record"
    },
    "zh-CN": {
      "widgetName": "今日饮水打卡",
      "widgetDescription": "轻量记录每日饮水量与打卡时刻，支持胶囊进度预览。",
      "setDailyGoal": "每日目标 (ml)",
      "capTitlePrefix": "饮水 · ",
      "capSubPrefix": "今日已打卡 ",
      "capSubSuffix": " 次",
      "cardTitle": "今日饮水进度",
      "cardSubGoal": "目标 ",
      "cardSubMid": " ml · 今日 ",
      "cardSubEnd": " 杯",
      "noteWarm": "温水一杯",
      "noteQuick": "随手畅饮",
      "resetTooltip": "归零今日计数",
      "emptyTitle": "今天还没有喝水记录",
      "emptySubtitle": "点击上方按钮快速记一杯",
      "deleteTooltip": "删除记录"
    },
    "zh-TW": {
      "widgetName": "今日飲水打卡",
      "widgetDescription": "輕量記錄每日飲水量與打卡時刻，支援膠囊進度預覽。",
      "setDailyGoal": "每日目標 (ml)",
      "capTitlePrefix": "飲水 · ",
      "capSubPrefix": "今日已打卡 ",
      "capSubSuffix": " 次",
      "cardTitle": "今日飲水進度",
      "cardSubGoal": "目標 ",
      "cardSubMid": " ml · 今日 ",
      "cardSubEnd": " 杯",
      "noteWarm": "溫水一杯",
      "noteQuick": "隨手暢飲",
      "resetTooltip": "歸零今日計數",
      "emptyTitle": "今天還沒有喝水紀錄",
      "emptySubtitle": "點擊上方按鈕快速記一杯",
      "deleteTooltip": "刪除紀錄"
    },
    "ja-JP": {
      "widgetName": "水分補給トラッカー",
      "widgetDescription": "毎日の水分摂取量と時刻を手軽に記録。カプセル進捗プレビュー対応。",
      "setDailyGoal": "1日の目標 (ml)",
      "capTitlePrefix": "水分補給 · ",
      "capSubPrefix": "今日 ",
      "capSubSuffix": " 回記録",
      "cardTitle": "今日の水分補給",
      "cardSubGoal": "目標 ",
      "cardSubMid": " ml · 今日 ",
      "cardSubEnd": " 杯",
      "noteWarm": "コップ1杯",
      "noteQuick": "こまめに補給",
      "resetTooltip": "今日の記録をリセット",
      "emptyTitle": "今日の記録はまだありません",
      "emptySubtitle": "上のボタンをタップして1杯記録しましょう",
      "deleteTooltip": "記録を削除"
    },
    "ko-KR": {
      "widgetName": "오늘의 수분 섭취",
      "widgetDescription": "일일 수분 섭취량과 시간을 간편하게 기록하며 캡슐 진행률 미리보기를 지원합니다.",
      "setDailyGoal": "일일 목표 (ml)",
      "capTitlePrefix": "수분 · ",
      "capSubPrefix": "오늘 ",
      "capSubSuffix": "회 기록",
      "cardTitle": "오늘의 수분 섭취량",
      "cardSubGoal": "목표 ",
      "cardSubMid": " ml · 오늘 ",
      "cardSubEnd": "잔",
      "noteWarm": "물 한 잔",
      "noteQuick": "가볍게 한 모금",
      "resetTooltip": "오늘 기록 초기화",
      "emptyTitle": "오늘 아직 물 마신 기록이 없습니다",
      "emptySubtitle": "위 버튼을 눌러 빠르게 한 잔 기록하세요",
      "deleteTooltip": "기록 삭제"
    }
  },
  "settings": [
    {
      "key": "dailyGoalMl",
      "type": "number",
      "label": "${$i18n.setDailyGoal}",
      "default": "2000",
      "min": 500,
      "max": 5000
    }
  ],
  "state": {
    "todayMl": "500"
  },
  "collections": {
    "logs": { "maxItems": 120, "sort": "newestFirst" }
  },
  "capsule": {
    "title": "${$i18n.capTitlePrefix}${state.todayMl} / ${$settings.dailyGoalMl} ml",
    "summary": "${$i18n.capSubPrefix}${collections.logs.todayCount}${$i18n.capSubSuffix}",
    "glyph": "💧",
    "badge": "${state.todayMl}ml",
    "enableBounceOnUpdate": true
  },
  "view": {
    "type": "Stack",
    "spacing": 8,
    "padding": "10,6,10,10",
    "children": [
      {
        "type": "StatCard",
        "title": "${$i18n.cardTitle}",
        "value": "${state.todayMl} ml",
        "subtitle": "${$i18n.cardSubGoal}${$settings.dailyGoalMl}${$i18n.cardSubMid}${collections.logs.todayCount}${$i18n.cardSubEnd}",
        "progress": "${state.todayMl == '0' ? 0 : 45}",
        "color": "cyan"
      },
      {
        "type": "Grid",
        "columns": "1*,1*,Auto",
        "spacing": 6,
        "children": [
          {
            "type": "Button",
            "column": 0,
            "icon": "\uE710",
            "text": "+250 ml",
            "variant": "accent",
            "onClick": [
              { "type": "state.increment", "key": "todayMl", "value": "250" },
              {
                "type": "collection.prepend",
                "collection": "logs",
                "item": { "amount": "250 ml", "note": "warm" }
              }
            ]
          },
          {
            "type": "Button",
            "column": 1,
            "icon": "\uE710",
            "text": "+350 ml",
            "variant": "standard",
            "onClick": [
              { "type": "state.increment", "key": "todayMl", "value": "350" },
              {
                "type": "collection.prepend",
                "collection": "logs",
                "item": { "amount": "350 ml", "note": "quick" }
              }
            ]
          },
          {
            "type": "Button",
            "column": 2,
            "icon": "\uE72C",
            "tooltip": "${$i18n.resetTooltip}",
            "variant": "subtle",
            "onClick": { "type": "state.set", "key": "todayMl", "value": "0" }
          }
        ]
      },
      {
        "type": "List",
        "items": "logs",
        "emptyState": {
          "icon": "\uE823",
          "title": "${$i18n.emptyTitle}",
          "subtitle": "${$i18n.emptySubtitle}"
        },
        "hoverActions": [
          {
            "type": "Button",
            "icon": "\uE74D",
            "variant": "ghost",
            "tooltip": "${$i18n.deleteTooltip}",
            "onClick": {
              "type": "collection.remove",
              "collection": "logs",
              "targetId": "${$item.id}"
            }
          }
        ],
        "itemTemplate": {
          "type": "Grid",
          "columns": "Auto,1*,Auto",
          "spacing": 8,
          "children": [
            { "type": "Badge", "column": 0, "text": "${$item.amount}", "tone": "accent" },
            { "type": "Text", "column": 1, "text": "${$item.note == 'warm' ? $i18n.noteWarm : ($item.note == 'quick' ? $i18n.noteQuick : $item.note)}", "style": "body", "maxLines": 1 },
            { "type": "Text", "column": 2, "text": "${$item.timeText}", "style": "caption", "color": "muted" }
          ]
        }
      }
    ]
  }
}
""";

    public const string ReleaseChecklistStarterManifestJson = """
{
  "schemaVersion": 1,
  "id": "custom.release-checklist",
  "name": "发版核对清单",
  "version": "1.1.0",
  "author": "DeskBox Studio",
  "description": "敏捷上线前必查事项清单，支持回车速记、一键勾选完成与过滤查看。",
  "icon": "✅",
  "minSize": { "width": 280, "height": 300 },
  "defaultSize": { "width": 332, "height": 396 },
  "i18n": {
    "en-US": {
      "widgetName": "Release Checklist",
      "widgetDescription": "Pre-launch verification checklist with quick Enter capture, 1-click completion, and status filters.",
      "capTitlePrefix": "Checklist · ",
      "capTitleSuffix": "",
      "capEmptySummary": "Expand to add release check items",
      "inputPlaceholder": "Add check item (Enter to add)…",
      "addBtn": "Add",
      "filterAll": "All",
      "filterTodo": "To Check",
      "filterDone": "Done",
      "emptyTitle": "No items in this filter",
      "emptySubtitle": "Add version bumps, regression checks, or backup verifications",
      "removeTooltip": "Remove",
      "toggleTooltip": "Toggle completion"
    },
    "zh-CN": {
      "widgetName": "发版核对清单",
      "widgetDescription": "敏捷上线前必查事项清单，支持回车速记、一键勾选完成与过滤查看。",
      "capTitlePrefix": "核对清单 · ",
      "capTitleSuffix": " 项",
      "capEmptySummary": "点击展开添加上线检查项",
      "inputPlaceholder": "添加待核对项（回车添加）…",
      "addBtn": "添加",
      "filterAll": "全部",
      "filterTodo": "待核对",
      "filterDone": "已完成",
      "emptyTitle": "当前筛选下暂无检查项",
      "emptySubtitle": "输入版本号验证、回归测试或备份确认事项",
      "removeTooltip": "移除",
      "toggleTooltip": "切换完成状态"
    },
    "zh-TW": {
      "widgetName": "發版核對清單",
      "widgetDescription": "敏捷上線前必查事項清單，支援 Enter 速記、一鍵勾選完成與篩選檢視。",
      "capTitlePrefix": "核對清單 · ",
      "capTitleSuffix": " 項",
      "capEmptySummary": "點擊展開新增上線檢查項",
      "inputPlaceholder": "新增待核對項（Enter 新增）…",
      "addBtn": "新增",
      "filterAll": "全部",
      "filterTodo": "待核對",
      "filterDone": "已完成",
      "emptyTitle": "目前篩選下暫無檢查項",
      "emptySubtitle": "輸入版本號驗證、回歸測試或備份確認事項",
      "removeTooltip": "移除",
      "toggleTooltip": "切換完成狀態"
    },
    "ja-JP": {
      "widgetName": "リリースチェックリスト",
      "widgetDescription": "リリース前の必須確認リスト。Enter で素早く追加し、ワンクリックで完了切替・絞り込み表示。",
      "capTitlePrefix": "チェックリスト · ",
      "capTitleSuffix": " 件",
      "capEmptySummary": "展開して確認項目を追加",
      "inputPlaceholder": "確認項目を追加（Enter で追加）…",
      "addBtn": "追加",
      "filterAll": "すべて",
      "filterTodo": "未確認",
      "filterDone": "完了",
      "emptyTitle": "該当する項目はありません",
      "emptySubtitle": "バージョン確認・回帰テスト・バックアップ確認などを入力",
      "removeTooltip": "削除",
      "toggleTooltip": "完了状態を切り替え"
    },
    "ko-KR": {
      "widgetName": "릴리스 체크리스트",
      "widgetDescription": "배포 전 필수 점검 목록으로 Enter 빠른 추가, 원클릭 완료 체크 및 필터 보기를 지원합니다.",
      "capTitlePrefix": "체크리스트 · ",
      "capTitleSuffix": "개",
      "capEmptySummary": "펼쳐서 배포 점검 항목 추가",
      "inputPlaceholder": "점검 항목 추가 (Enter 추가)…",
      "addBtn": "추가",
      "filterAll": "전체",
      "filterTodo": "대기",
      "filterDone": "완료",
      "emptyTitle": "현재 필터에 항목이 없습니다",
      "emptySubtitle": "버전 확인, 회귀 테스트, 백업 점검 항목을 입력하세요",
      "removeTooltip": "제거",
      "toggleTooltip": "완료 상태 전환"
    }
  },
  "state": {
    "draftTask": "",
    "filter": "all"
  },
  "collections": {
    "tasks": { "maxItems": 200, "sort": "newestFirst" }
  },
  "capsule": {
    "title": "${$i18n.capTitlePrefix}${collections.tasks.count}${$i18n.capTitleSuffix}",
    "summary": "${collections.tasks.latest.title ?? $i18n.capEmptySummary}",
    "glyph": "✅",
    "badge": "${collections.tasks.count}",
    "enableBounceOnUpdate": true
  },
  "view": {
    "type": "Stack",
    "spacing": 8,
    "padding": "10,6,10,10",
    "children": [
      {
        "type": "Grid",
        "columns": "1*,Auto",
        "spacing": 6,
        "children": [
          {
            "type": "Input",
            "column": 0,
            "placeholder": "${$i18n.inputPlaceholder}",
            "bind": "state.draftTask",
            "onSubmit": [
              {
                "type": "collection.prepend",
                "guard": "${state.draftTask != ''}",
                "collection": "tasks",
                "item": { "title": "${state.draftTask}", "done": "false" }
              },
              { "type": "state.set", "key": "draftTask", "value": "" }
            ]
          },
          {
            "type": "Button",
            "column": 1,
            "icon": "\uE710",
            "text": "${$i18n.addBtn}",
            "variant": "accent",
            "onClick": [
              {
                "type": "collection.prepend",
                "guard": "${state.draftTask != ''}",
                "collection": "tasks",
                "item": { "title": "${state.draftTask}", "done": "false" }
              },
              { "type": "state.set", "key": "draftTask", "value": "" }
            ]
          }
        ]
      },
      {
        "type": "Segmented",
        "bind": "state.filter",
        "options": [
          { "value": "all", "label": "${$i18n.filterAll}" },
          { "value": "todo", "label": "${$i18n.filterTodo}" },
          { "value": "done", "label": "${$i18n.filterDone}" }
        ]
      },
      {
        "type": "List",
        "items": "tasks",
        "filter": "${state.filter == 'all' || (state.filter == 'done' && $item.done == 'true') || (state.filter == 'todo' && $item.done != 'true')}",
        "emptyState": {
          "icon": "\uE73E",
          "title": "${$i18n.emptyTitle}",
          "subtitle": "${$i18n.emptySubtitle}"
        },
        "hoverActions": [
          {
            "type": "Button",
            "icon": "\uE74D",
            "variant": "ghost",
            "tooltip": "${$i18n.removeTooltip}",
            "onClick": {
              "type": "collection.remove",
              "collection": "tasks",
              "targetId": "${$item.id}"
            }
          }
        ],
        "itemTemplate": {
          "type": "Grid",
          "columns": "Auto,1*",
          "spacing": 8,
          "children": [
            {
              "type": "Button",
              "column": 0,
              "icon": "${$item.done == 'true' ? '\uE73E' : '\uEA3A'}",
              "variant": "${$item.done == 'true' ? 'accent' : 'subtle'}",
              "tooltip": "${$i18n.toggleTooltip}",
              "onClick": {
                "type": "collection.update",
                "collection": "tasks",
                "targetId": "${$item.id}",
                "item": {
                  "done": "${$item.done == 'true' ? 'false' : 'true'}"
                }
              }
            },
            {
              "type": "Text",
              "column": 1,
              "text": "${$item.title}",
              "style": "${$item.done == 'true' ? 'body' : 'bodyStrong'}",
              "color": "${$item.done == 'true' ? 'muted' : 'default'}",
              "maxLines": 2
            }
          ]
        }
      }
    ]
  }
}
""";

    public const string AgentHudStarterManifestJson = """
{
  "schemaVersion": 1,
  "id": "deskbox.agent-hud",
  "name": "Agent 状态看板",
  "version": "1.1.0",
  "author": "DeskBox",
  "description": "面向 Claude Code、Cursor、Codex 与自动化脚本的桌面常驻任务伴侣，支持 MCP / CLI 实时推送任务进度与事件流。",
  "icon": "🤖",
  "minSize": { "width": 288, "height": 292 },
  "defaultSize": { "width": 336, "height": 376 },
  "i18n": {
    "en-US": {
      "widgetName": "Agent Status HUD",
      "widgetDescription": "Always-on desktop companion for Claude Code, Cursor, Codex, and scripts with live MCP / CLI task progress and event stream.",
      "defaultTask": "Waiting for external Agent task updates…",
      "defaultStatus": "Standby",
      "defaultUpdated": "Just now",
      "progressTitle": "Execution Progress",
      "streamHeaderPrefix": "Live Event Stream (",
      "clearBtn": "Clear",
      "emptyTitle": "No events pushed yet",
      "emptySubtitle": "Push live updates via MCP tool deskbox_push_widget_data or DeskBox widget push"
    },
    "zh-CN": {
      "widgetName": "Agent 状态看板",
      "widgetDescription": "面向 Claude Code、Cursor、Codex 与自动化脚本的桌面常驻任务伴侣，支持 MCP / CLI 实时推送任务进度与事件流。",
      "defaultTask": "等待外部 Agent 推送任务状态…",
      "defaultStatus": "就绪待命",
      "defaultUpdated": "刚刚",
      "progressTitle": "执行进度",
      "streamHeaderPrefix": "实时事件流 (",
      "clearBtn": "清空",
      "emptyTitle": "尚无推送记录",
      "emptySubtitle": "通过 MCP 工具 deskbox_push_widget_data 或 DeskBox widget push 实时同步进度"
    },
    "zh-TW": {
      "widgetName": "Agent 狀態看板",
      "widgetDescription": "面向 Claude Code、Cursor、Codex 與自動化腳本的桌面常駐任務伴侶，支援 MCP / CLI 即時推送任務進度與事件流。",
      "defaultTask": "等待外部 Agent 推送任務狀態…",
      "defaultStatus": "就緒待命",
      "defaultUpdated": "剛剛",
      "progressTitle": "執行進度",
      "streamHeaderPrefix": "即時事件流 (",
      "clearBtn": "清空",
      "emptyTitle": "尚無推送紀錄",
      "emptySubtitle": "透過 MCP 工具 deskbox_push_widget_data 或 DeskBox widget push 即時同步進度"
    },
    "ja-JP": {
      "widgetName": "Agent ステータス HUD",
      "widgetDescription": "Claude Code・Cursor・Codex・自動化スクリプト向け常駐タスクボード。MCP / CLI で進捗とイベントをリアルタイム受信。",
      "defaultTask": "外部 Agent からのタスク更新を待機中…",
      "defaultStatus": "待機中",
      "defaultUpdated": "たった今",
      "progressTitle": "実行進捗",
      "streamHeaderPrefix": "イベントストリーム (",
      "clearBtn": "クリア",
      "emptyTitle": "イベント履歴はまだありません",
      "emptySubtitle": "MCP ツール deskbox_push_widget_data または DeskBox widget push で進捗を送信"
    },
    "ko-KR": {
      "widgetName": "Agent 상태 보드",
      "widgetDescription": "Claude Code, Cursor, Codex 및 자동화 스크립트를 위한 데스크톱 상주 보드로 MCP / CLI 실시간 진행률과 이벤트 스트림을 지원합니다.",
      "defaultTask": "외부 Agent의 작업 상태 푸시 대기 중…",
      "defaultStatus": "대기 중",
      "defaultUpdated": "방금 전",
      "progressTitle": "실행 진행률",
      "streamHeaderPrefix": "실시간 이벤트 스트림 (",
      "clearBtn": "비우기",
      "emptyTitle": "아직 수신된 기록이 없습니다",
      "emptySubtitle": "MCP 도구 deskbox_push_widget_data 또는 DeskBox widget push로 진행 상황을 동기화하세요"
    }
  },
  "state": {
    "agentName": "Agent Bridge",
    "taskTitle": "",
    "statusText": "",
    "progress": "0",
    "isBusy": "false",
    "updatedAt": ""
  },
  "collections": {
    "events": { "maxItems": 80, "sort": "newestFirst" }
  },
  "capsule": {
    "title": "${state.agentName} · ${state.statusText != '' ? state.statusText : $i18n.defaultStatus}",
    "summary": "${state.taskTitle != '' ? state.taskTitle : $i18n.defaultTask}",
    "glyph": "🤖",
    "badge": "${state.progress != '0' ? state.progress : collections.events.count}",
    "enableBounceOnUpdate": true
  },
  "view": {
    "type": "Stack",
    "spacing": 8,
    "padding": "10,6,10,10",
    "children": [
      {
        "type": "Card",
        "tone": "subtle",
        "padding": "12,10,12,10",
        "children": [
          {
            "type": "Stack",
            "spacing": 6,
            "children": [
              {
                "type": "Grid",
                "columns": "Auto,1*,Auto",
                "spacing": 8,
                "children": [
                  {
                    "type": "ProgressRing",
                    "column": 0,
                    "size": 16,
                    "indeterminate": "${state.isBusy == 'true'}",
                    "value": "${state.progress}"
                  },
                  {
                    "type": "Text",
                    "column": 1,
                    "text": "${state.agentName}",
                    "style": "bodyStrong",
                    "maxLines": 1
                  },
                  {
                    "type": "Badge",
                    "column": 2,
                    "text": "${state.statusText != '' ? state.statusText : $i18n.defaultStatus}",
                    "tone": "${state.isBusy == 'true' ? 'accent' : 'subtle'}"
                  }
                ]
              },
              {
                "type": "Text",
                "text": "${state.taskTitle != '' ? state.taskTitle : $i18n.defaultTask}",
                "style": "body",
                "color": "secondary",
                "maxLines": 2
              },
              {
                "type": "ProgressRow",
                "title": "${$i18n.progressTitle}",
                "subtitle": "${state.updatedAt != '' ? state.updatedAt : $i18n.defaultUpdated}",
                "value": "${state.progress}%",
                "progress": "${state.progress}"
              }
            ]
          }
        ]
      },
      {
        "type": "Grid",
        "columns": "1*,Auto",
        "spacing": 6,
        "children": [
          {
            "type": "Text",
            "column": 0,
            "text": "${$i18n.streamHeaderPrefix}${collections.events.count})",
            "style": "caption",
            "color": "muted"
          },
          {
            "type": "Button",
            "column": 1,
            "icon": "\uE74D",
            "text": "${$i18n.clearBtn}",
            "variant": "ghost",
            "onClick": { "type": "collection.clear", "collection": "events" }
          }
        ]
      },
      {
        "type": "List",
        "items": "events",
        "emptyState": {
          "icon": "\uE950",
          "title": "${$i18n.emptyTitle}",
          "subtitle": "${$i18n.emptySubtitle}"
        },
        "itemTemplate": {
          "type": "Grid",
          "columns": "Auto,1*,Auto",
          "spacing": 8,
          "children": [
            {
              "type": "Badge",
              "column": 0,
              "text": "${$item.tag ?? 'INFO'}",
              "tone": "${$item.tone ?? 'subtle'}"
            },
            {
              "type": "Text",
              "column": 1,
              "text": "${$item.text}",
              "style": "body",
              "maxLines": 2
            },
            {
              "type": "Text",
              "column": 2,
              "text": "${$item.timeText}",
              "style": "caption",
              "color": "muted"
            }
          ]
        }
      }
    ]
  }
}
""";

    public const string MiniHardwareStarterManifestJson = """
{
  "schemaVersion": 1,
  "id": "custom.mini-hardware",
  "name": "精简硬件速览",
  "version": "1.1.0",
  "author": "DeskBox Studio",
  "description": "双栏展示 CPU、内存实时曲线与上下行网速。",
  "icon": "📊",
  "minSize": { "width": 276, "height": 208 },
  "defaultSize": { "width": 324, "height": 244 },
  "i18n": {
    "en-US": {
      "widgetName": "Compact Hardware Monitor",
      "widgetDescription": "Two-column live CPU & RAM sparklines with real-time network download/upload speeds.",
      "cpuTitle": "Processor CPU",
      "memTitle": "Physical RAM",
      "netDown": "Down",
      "netUp": "Up"
    },
    "zh-CN": {
      "widgetName": "精简硬件速览",
      "widgetDescription": "双栏展示 CPU、内存实时曲线与上下行网速。",
      "cpuTitle": "处理器 CPU",
      "memTitle": "物理内存",
      "netDown": "下行",
      "netUp": "上行"
    },
    "zh-TW": {
      "widgetName": "精簡硬體速覽",
      "widgetDescription": "雙欄顯示 CPU、記憶體即時曲線與上下行網速。",
      "cpuTitle": "處理器 CPU",
      "memTitle": "實體記憶體",
      "netDown": "下行",
      "netUp": "上行"
    },
    "ja-JP": {
      "widgetName": "コンパクトハードウェア監視",
      "widgetDescription": "CPU・メモリのリアルタイムグラフと通信速度を2列でコンパクトに表示。",
      "cpuTitle": "プロセッサ CPU",
      "memTitle": "物理メモリ",
      "netDown": "受信",
      "netUp": "送信"
    },
    "ko-KR": {
      "widgetName": "컴팩트 하드웨어 모니터",
      "widgetDescription": "2열 레이아웃으로 CPU, 메모리 실시간 그래프와 네트워크 속도를 표시합니다.",
      "cpuTitle": "프로세서 CPU",
      "memTitle": "물리 메모리",
      "netDown": "다운로드",
      "netUp": "업로드"
    }
  },
  "sources": [
    { "type": "systemMetrics", "intervalMs": 2000 }
  ],
  "capsule": {
    "title": "CPU ${$sys.cpu.percent}% · RAM ${$sys.memory.percent}%",
    "summary": "↓ ${$sys.net.downText}  ↑ ${$sys.net.upText}",
    "glyph": "📊",
    "badge": "${$sys.cpu.percent}%"
  },
  "view": {
    "type": "Stack",
    "spacing": 8,
    "padding": "10,6,10,10",
    "children": [
      {
        "type": "Grid",
        "columns": "1*,1*",
        "spacing": 8,
        "children": [
          {
            "type": "StatCard",
            "column": 0,
            "title": "${$i18n.cpuTitle}",
            "value": "${$sys.cpu.percent}%",
            "progress": "${$sys.cpu.percent}",
            "sparkline": "$sys.cpu.history",
            "color": "accent",
            "warningThreshold": 75,
            "dangerThreshold": 90
          },
          {
            "type": "StatCard",
            "column": 1,
            "title": "${$i18n.memTitle}",
            "value": "${$sys.memory.percent}%",
            "subtitle": "${$sys.memory.summaryText}",
            "progress": "${$sys.memory.percent}",
            "sparkline": "$sys.memory.history",
            "color": "emerald",
            "warningThreshold": 80,
            "dangerThreshold": 92
          }
        ]
      },
      {
        "type": "Card",
        "tone": "subtle",
        "padding": "10,8,10,8",
        "children": [
          {
            "type": "Grid",
            "columns": "1*,1*",
            "spacing": 8,
            "children": [
              {
                "type": "KeyValueRow",
                "column": 0,
                "icon": "\uE896",
                "title": "${$i18n.netDown}",
                "value": "${$sys.net.downText}"
              },
              {
                "type": "KeyValueRow",
                "column": 1,
                "icon": "\uE898",
                "title": "${$i18n.netUp}",
                "value": "${$sys.net.upText}"
              }
            ]
          }
        ]
      }
    ]
  }
}
""";

    public const string FileDropStarterManifestJson = """
{
  "schemaVersion": 1,
  "id": "custom.file-basket",
  "name": "临时文件中转篮",
  "version": "1.1.0",
  "author": "DeskBox Studio",
  "description": "把常用文档、脚本或截图拖入格子即可暂存，点击直接打开，右键定位所在目录。",
  "icon": "🗂️",
  "minSize": { "width": 276, "height": 260 },
  "defaultSize": { "width": 324, "height": 348 },
  "i18n": {
    "en-US": {
      "widgetName": "File Drop Basket",
      "widgetDescription": "Drag documents, scripts, or screenshots into the basket for quick staging — click to open, right-click to reveal.",
      "capTitlePrefix": "File Basket · ",
      "capTitleSuffix": "",
      "capEmptySummary": "Drag any files here for quick staging",
      "hintText": "Drag and drop files directly into the window",
      "clearBtn": "Clear",
      "emptyTitle": "Basket is empty",
      "emptySubtitle": "Drop loose desktop files here to keep your workspace clean",
      "revealTooltip": "Show in File Explorer",
      "removeTooltip": "Remove reference"
    },
    "zh-CN": {
      "widgetName": "临时文件中转篮",
      "widgetDescription": "把常用文档、脚本或截图拖入格子即可暂存，点击直接打开，右键定位所在目录。",
      "capTitlePrefix": "中转篮 · ",
      "capTitleSuffix": " 个文件",
      "capEmptySummary": "拖拽任意文件到此暂存",
      "hintText": "直接拖拽文件到窗口内即可收录",
      "clearBtn": "清空",
      "emptyTitle": "文件篮还是空的",
      "emptySubtitle": "把桌面零散文件拖进来，保持桌面清爽",
      "revealTooltip": "在资源管理器中定位",
      "removeTooltip": "移除引用"
    },
    "zh-TW": {
      "widgetName": "暫存檔案中轉籃",
      "widgetDescription": "將常用文件、腳本或截圖拖入格子即可暫存，點擊直接開啟，右鍵定位所在目錄。",
      "capTitlePrefix": "中轉籃 · ",
      "capTitleSuffix": " 個檔案",
      "capEmptySummary": "拖曳任意檔案到此暫存",
      "hintText": "直接拖曳檔案到視窗內即可收錄",
      "clearBtn": "清空",
      "emptyTitle": "檔案籃還是空的",
      "emptySubtitle": "將桌面零散檔案拖進來，保持桌面清爽",
      "revealTooltip": "在檔案總管中定位",
      "removeTooltip": "移除參照"
    },
    "ja-JP": {
      "widgetName": "ファイルバスケット",
      "widgetDescription": "よく使う書類・スクリプト・スクショをドロップして一時保管。クリックで開き、右クリックで場所を表示。",
      "capTitlePrefix": "バスケット · ",
      "capTitleSuffix": " 件",
      "capEmptySummary": "ファイルをドロップして一時保管",
      "hintText": "ウィンドウ内にファイルをドラッグ＆ドロップ",
      "clearBtn": "クリア",
      "emptyTitle": "バスケットは空です",
      "emptySubtitle": "デスクトップ上の散らかったファイルをまとめて整理しましょう",
      "revealTooltip": "エクスプローラーで表示",
      "removeTooltip": "参照を削除"
    },
    "ko-KR": {
      "widgetName": "임시 파일 바구니",
      "widgetDescription": "문서, 스크립트, 스크린샷을 드래그해 임시 보관하고 클릭으로 열거나 우클릭으로 폴더를 엽니다.",
      "capTitlePrefix": "파일 바구니 · ",
      "capTitleSuffix": "개",
      "capEmptySummary": "파일을 여기로 드래그해 임시 보관하세요",
      "hintText": "창 안으로 파일을 바로 드래그하여 추가",
      "clearBtn": "비우기",
      "emptyTitle": "파일 바구니가 비어 있습니다",
      "emptySubtitle": "바탕화면의 흩어진 파일을 모아 깔끔하게 유지하세요",
      "revealTooltip": "탐색기에서 위치 열기",
      "removeTooltip": "참조 제거"
    }
  },
  "collections": {
    "files": { "maxItems": 120, "sort": "newestFirst" }
  },
  "onDrop": {
    "storage": "reference",
    "action": {
      "type": "collection.prepend",
      "collection": "files",
      "item": {
        "fileName": "${$drop.name}",
        "filePath": "${$drop.path}",
        "storageMode": "${$drop.mode}"
      }
    }
  },
  "capsule": {
    "title": "${$i18n.capTitlePrefix}${collections.files.count}${$i18n.capTitleSuffix}",
    "summary": "${collections.files.latest.fileName ?? $i18n.capEmptySummary}",
    "glyph": "🗂️",
    "badge": "${collections.files.count}",
    "enableBounceOnUpdate": true
  },
  "view": {
    "type": "Stack",
    "spacing": 8,
    "padding": "10,6,10,10",
    "children": [
      {
        "type": "Grid",
        "columns": "1*,Auto",
        "spacing": 8,
        "children": [
          {
            "type": "Text",
            "column": 0,
            "text": "${$i18n.hintText}",
            "style": "caption",
            "color": "muted"
          },
          {
            "type": "Button",
            "column": 1,
            "icon": "\uE74D",
            "text": "${$i18n.clearBtn}",
            "variant": "ghost",
            "onClick": { "type": "collection.clear", "collection": "files" }
          }
        ]
      },
      {
        "type": "List",
        "items": "files",
        "emptyState": {
          "icon": "\uE8B7",
          "title": "${$i18n.emptyTitle}",
          "subtitle": "${$i18n.emptySubtitle}"
        },
        "hoverActions": [
          {
            "type": "Button",
            "icon": "\uE8DA",
            "variant": "ghost",
            "tooltip": "${$i18n.revealTooltip}",
            "onClick": { "type": "file.reveal", "path": "${$item.filePath}" }
          },
          {
            "type": "Button",
            "icon": "\uE74D",
            "variant": "ghost",
            "tooltip": "${$i18n.removeTooltip}",
            "onClick": {
              "type": "collection.remove",
              "collection": "files",
              "targetId": "${$item.id}"
            }
          }
        ],
        "itemTemplate": {
          "type": "FileChip",
          "fileName": "${$item.fileName}",
          "filePath": "${$item.filePath}",
          "storageMode": "${$item.storageMode}"
        }
      }
    ]
  }
}
""";

    public const string NewsRssStarterManifestJson = """
{
  "schemaVersion": 1,
  "id": "custom.news-rss",
  "name": "热点资讯订阅",
  "version": "1.1.0",
  "author": "DeskBox",
  "description": "自动拉取 RSS / Atom 订阅资讯，离线缓存秒开，点击即可打开原文阅读。",
  "icon": "📰",
  "minSize": { "width": 300, "height": 320 },
  "defaultSize": { "width": 348, "height": 420 },
  "i18n": {
    "en-US": {
      "widgetName": "RSS News Reader",
      "widgetDescription": "Auto-sync RSS / Atom feeds with instant offline cache and 1-click article launch.",
      "setFeedUrl": "RSS / Atom Feed URL (https://)",
      "defaultFeedTitle": "Tech News",
      "defaultTopHeadline": "Click Refresh in the top-right corner for the latest stories",
      "syncing": "Syncing latest stories…",
      "updatedPrefix": "Updated ",
      "refreshBtn": "Refresh",
      "refreshTooltip": "Fetch latest feed items now",
      "emptyTitle": "Waiting for feed sync",
      "emptySubtitle": "Click Refresh above, or configure any HTTPS RSS URL in widget options",
      "openArticleTooltip": "Open article"
    },
    "zh-CN": {
      "widgetName": "热点资讯订阅",
      "widgetDescription": "自动拉取 RSS / Atom 订阅资讯，离线缓存秒开，点击即可打开原文阅读。",
      "setFeedUrl": "RSS / Atom 订阅地址 (https://)",
      "defaultFeedTitle": "少数派",
      "defaultTopHeadline": "点击右上角刷新获取最新内容",
      "syncing": "正在同步最新资讯…",
      "updatedPrefix": "更新于 ",
      "refreshBtn": "刷新",
      "refreshTooltip": "立即拉取最新订阅",
      "emptyTitle": "正在等待资讯同步",
      "emptySubtitle": "点击右上角「刷新」拉取订阅，或在选项中更换任意 HTTPS RSS 地址",
      "openArticleTooltip": "打开原文"
    },
    "zh-TW": {
      "widgetName": "熱點資訊訂閱",
      "widgetDescription": "自動擷取 RSS / Atom 訂閱資訊，離線快取秒開，點擊即可開啟原文閱讀。",
      "setFeedUrl": "RSS / Atom 訂閱網址 (https://)",
      "defaultFeedTitle": "科技資訊",
      "defaultTopHeadline": "點擊右上角重新整理取得最新內容",
      "syncing": "正在同步最新資訊…",
      "updatedPrefix": "更新於 ",
      "refreshBtn": "重新整理",
      "refreshTooltip": "立即擷取最新訂閱",
      "emptyTitle": "正在等待資訊同步",
      "emptySubtitle": "點擊右上角「重新整理」擷取訂閱，或在選項中更換任意 HTTPS RSS 網址",
      "openArticleTooltip": "開啟原文"
    },
    "ja-JP": {
      "widgetName": "RSS ニュース購読",
      "widgetDescription": "RSS / Atom フィードを自動取得。オフラインキャッシュで高速表示し、クリックで記事を開きます。",
      "setFeedUrl": "RSS / Atom フィード URL (https://)",
      "defaultFeedTitle": "テックニュース",
      "defaultTopHeadline": "右上の「更新」をクリックして最新記事を取得",
      "syncing": "最新記事を同期中…",
      "updatedPrefix": "更新 ",
      "refreshBtn": "更新",
      "refreshTooltip": "最新フィードを今すぐ取得",
      "emptyTitle": "フィード同期を待機中",
      "emptySubtitle": "右上の「更新」を押すか、オプションで任意の HTTPS RSS URL に変更してください",
      "openArticleTooltip": "記事を開く"
    },
    "ko-KR": {
      "widgetName": "RSS 뉴스 구독",
      "widgetDescription": "RSS / Atom 피드를 자동으로 가져오며 오프라인 캐시와 원클릭 원문 열기를 지원합니다.",
      "setFeedUrl": "RSS / Atom 구독 주소 (https://)",
      "defaultFeedTitle": "테크 뉴스",
      "defaultTopHeadline": "우측 상단 새로고침을 눌러 최신 글을 불러오세요",
      "syncing": "최신 소식 동기화 중…",
      "updatedPrefix": "업데이트 ",
      "refreshBtn": "새로고침",
      "refreshTooltip": "지금 최신 피드 가져오기",
      "emptyTitle": "피드 동기화 대기 중",
      "emptySubtitle": "우측 상단 '새로고침'을 누르거나 옵션에서 원하는 HTTPS RSS 주소로 변경하세요",
      "openArticleTooltip": "원문 열기"
    }
  },
  "settings": [
    {
      "key": "feedUrl",
      "type": "text",
      "label": "${$i18n.setFeedUrl}",
      "default": "https://sspai.com/feed"
    }
  ],
  "state": {
    "feedTitle": "",
    "feedCount": "0",
    "topHeadline": "",
    "sourceStatus": "idle",
    "sourceUpdatedAt": "--:--"
  },
  "collections": {
    "articles": { "maxItems": 20, "sort": "newestFirst" }
  },
  "sources": [
    {
      "id": "mainFeed",
      "type": "rss",
      "url": "${$settings.feedUrl}",
      "targetCollection": "articles",
      "intervalMs": 900000,
      "maxItems": 15
    }
  ],
  "capsule": {
    "title": "${state.feedTitle != '' ? state.feedTitle : $i18n.defaultFeedTitle}",
    "summary": "${collections.articles.latest.title ?? (state.topHeadline != '' ? state.topHeadline : $i18n.defaultTopHeadline)}",
    "glyph": "📰",
    "badge": "${collections.articles.count}"
  },
  "view": {
    "type": "Stack",
    "spacing": 8,
    "padding": "10,6,10,10",
    "children": [
      {
        "type": "Grid",
        "columns": "1*,Auto",
        "spacing": 8,
        "children": [
          {
            "type": "Stack",
            "column": 0,
            "spacing": 2,
            "children": [
              {
                "type": "Text",
                "text": "${state.feedTitle != '' ? state.feedTitle : $i18n.defaultFeedTitle}",
                "style": "bodyStrong",
                "maxLines": 1
              },
              {
                "type": "Text",
                "text": "${state.sourceStatus == 'loading' ? $i18n.syncing : $i18n.updatedPrefix}${state.sourceStatus == 'loading' ? '' : (state.sourceUpdatedAt ?? '--:--')}",
                "style": "caption",
                "color": "muted",
                "maxLines": 1
              }
            ]
          },
          {
            "type": "Button",
            "column": 1,
            "icon": "\uE72C",
            "text": "${$i18n.refreshBtn}",
            "variant": "subtle",
            "tooltip": "${$i18n.refreshTooltip}",
            "onClick": { "type": "source.refresh", "targetId": "mainFeed" }
          }
        ]
      },
      {
        "type": "List",
        "items": "articles",
        "emptyState": {
          "icon": "📰",
          "title": "${$i18n.emptyTitle}",
          "subtitle": "${$i18n.emptySubtitle}"
        },
        "hoverActions": [
          {
            "type": "Button",
            "icon": "\uE8A7",
            "variant": "ghost",
            "tooltip": "${$i18n.openArticleTooltip}",
            "onClick": { "type": "url.open", "url": "${$item.url}" }
          }
        ],
        "itemTemplate": {
          "type": "Card",
          "tone": "subtle",
          "padding": "9,7,9,7",
          "onClick": { "type": "url.open", "url": "${$item.url}" },
          "children": [
            {
              "type": "Stack",
              "spacing": 3,
              "children": [
                {
                  "type": "Text",
                  "text": "${$item.title}",
                  "style": "bodyStrong",
                  "maxLines": 2
                },
                {
                  "type": "Grid",
                  "columns": "1*,Auto",
                  "spacing": 6,
                  "children": [
                    {
                      "type": "Text",
                      "column": 0,
                      "text": "${$item.summary}",
                      "style": "caption",
                      "color": "secondary",
                      "maxLines": 1
                    },
                    {
                      "type": "Text",
                      "column": 1,
                      "text": "${$item.publishedAt}",
                      "style": "caption",
                      "color": "muted",
                      "maxLines": 1
                    }
                  ]
                }
              ]
            }
          ]
        }
      }
    ]
  }
}
""";

    public const string AiInboxStarterManifestJson = """
{
  "schemaVersion": 1,
  "id": "custom.ai-inbox",
  "name": "闪念智能提炼",
  "version": "1.0.0",
  "author": "DeskBox",
  "description": "随手输入零散想法、会议速记或直接拖入文本文件，AI 秒级提炼一句话摘要与可执行待办。",
  "icon": "⚡",
  "minSize": { "width": 292, "height": 320 },
  "defaultSize": { "width": 348, "height": 420 },
  "i18n": {
    "en-US": {
      "widgetName": "AI Quick Refine",
      "widgetDescription": "Type rough thoughts, meeting notes, or drop a text file — AI extracts a one-line summary and actionable checklist in seconds.",
      "capTitlePrefix": "AI Refine · ",
      "capTitleSuffix": "",
      "capEmptySummary": "Drop a note or type messy thoughts to refine",
      "inputPlaceholder": "Paste rough notes or drop a .txt / .md file…",
      "refineBtn": "Refine",
      "refineTooltip": "Extract summary and action items via AI",
      "runningBadge": "Refining…",
      "summaryLabel": "AI Summary",
      "copySummaryTooltip": "Copy summary",
      "filterAll": "All",
      "filterTodo": "To Do",
      "filterDone": "Done",
      "clearBtn": "Clear",
      "emptyTitle": "Ready to refine your thoughts",
      "emptySubtitle": "Type rough ideas above and press Enter, or drag a .txt / .md file into the widget",
      "toggleTooltip": "Toggle completion",
      "removeTooltip": "Remove item",
      "aiPromptPrefix": "Distill the following rough notes into a concise one-sentence \"summary\" and 1-6 concrete actionable \"items\" (each with concise \"title\", 2-6 char category \"tag\", and \"done\": \"false\"):"
    },
    "zh-CN": {
      "widgetName": "闪念智能提炼",
      "widgetDescription": "随手输入零散想法、会议速记或直接拖入文本文件，AI 秒级提炼一句话摘要与可执行待办。",
      "capTitlePrefix": "闪念提炼 · ",
      "capTitleSuffix": " 项",
      "capEmptySummary": "输入零散想法或拖入文本一键提炼",
      "inputPlaceholder": "输入零散笔记，或直接拖入 .txt / .md 文件…",
      "refineBtn": "提炼",
      "refineTooltip": "使用 AI 极速提炼摘要与待办清单",
      "runningBadge": "提炼中…",
      "summaryLabel": "核心摘要",
      "copySummaryTooltip": "复制核心摘要",
      "filterAll": "全部",
      "filterTodo": "待办",
      "filterDone": "已完成",
      "emptyTitle": "把杂乱信息交给 AI 提炼",
      "emptySubtitle": "在上方输入会议速记按回车，或直接拖入 .txt / .md 文本文件",
      "toggleTooltip": "切换完成状态",
      "removeTooltip": "移除此项",
      "aiPromptPrefix": "请将以下零散内容提炼为一句精炼的中文摘要 \"summary\"，以及 1 到 6 条可直接执行的行动项 \"items\"（每项包含简短 \"title\"、2-4 字分类标签 \"tag\"，以及 \"done\": \"false\"）："
    },
    "zh-TW": {
      "widgetName": "閃念智慧提煉",
      "widgetDescription": "隨手輸入零散想法、會議速記或直接拖入文字檔，AI 秒級提煉一句話摘要與可執行待辦。",
      "capTitlePrefix": "閃念提煉 · ",
      "capTitleSuffix": " 項",
      "capEmptySummary": "輸入零散想法或拖入文字檔一鍵提煉",
      "inputPlaceholder": "輸入零散筆記，或直接拖入 .txt / .md 檔案…",
      "refineBtn": "提煉",
      "refineTooltip": "使用 AI 極速提煉摘要與待辦清單",
      "runningBadge": "提煉中…",
      "summaryLabel": "核心摘要",
      "copySummaryTooltip": "複製核心摘要",
      "filterAll": "全部",
      "filterTodo": "待辦",
      "filterDone": "已完成",
      "emptyTitle": "把雜亂資訊交給 AI 提煉",
      "emptySubtitle": "在上方輸入會議速記按 Enter，或直接拖入 .txt / .md 文字檔",
      "toggleTooltip": "切換完成狀態",
      "removeTooltip": "移除此項",
      "aiPromptPrefix": "請將以下零散內容提煉為一句精煉的繁體中文摘要 \"summary\"，以及 1 到 6 條可直接執行的行動項 \"items\"（每項包含簡短 \"title\"、2-4 字分類標籤 \"tag\"，以及 \"done\": \"false\"）："
    },
    "ja-JP": {
      "widgetName": "AI クイック整理",
      "widgetDescription": "思いつきや議事メモの入力、テキストファイルのドロップから、AI が要約と実行タスクを瞬時に抽出します。",
      "capTitlePrefix": "AI 整理 · ",
      "capTitleSuffix": " 件",
      "capEmptySummary": "メモを入力またはファイルをドロップして整理",
      "inputPlaceholder": "断片的なメモを入力、または .txt / .md をドロップ…",
      "refineBtn": "整理",
      "refineTooltip": "AI で要約とアクション項目を抽出",
      "runningBadge": "整理中…",
      "summaryLabel": "AI 要約",
      "copySummaryTooltip": "要約をコピー",
      "filterAll": "すべて",
      "filterTodo": "未完了",
      "filterDone": "完了",
      "emptyTitle": "断片メモを AI で構造化",
      "emptySubtitle": "上でメモを入力して Enter を押すか、.txt / .md ファイルをドロップしてください",
      "toggleTooltip": "完了状態を切り替え",
      "removeTooltip": "削除",
      "aiPromptPrefix": "以下のメモを簡潔な1文の要約 \"summary\" と、1〜6件の具体的な実行アイテム \"items\"（簡潔な \"title\"、短い分類 \"tag\"、\"done\": \"false\"）に整理してください："
    },
    "ko-KR": {
      "widgetName": "AI 빠른 핵심 정리",
      "widgetDescription": "흩어진 메모나 회의록을 입력하거나 텍스트 파일을 드롭하면 AI가 한 줄 요약과 실행 할 일을 즉시 추출합니다.",
      "capTitlePrefix": "AI 정리 · ",
      "capTitleSuffix": "개",
      "capEmptySummary": "메모 입력 또는 텍스트 파일을 드롭해 정리하세요",
      "inputPlaceholder": "메모를 입력하거나 .txt / .md 파일을 드롭하세요…",
      "refineBtn": "정리",
      "refineTooltip": "AI로 핵심 요약 및 실행 항목 추출",
      "runningBadge": "정리 중…",
      "summaryLabel": "핵심 요약",
      "copySummaryTooltip": "요약 복사",
      "filterAll": "전체",
      "filterTodo": "할 일",
      "filterDone": "완료",
      "emptyTitle": "복잡한 메모를 AI로 깔끔하게",
      "emptySubtitle": "위에 내용을 입력하고 Enter를 누르거나 .txt / .md 파일을 드롭하세요",
      "toggleTooltip": "완료 상태 전환",
      "removeTooltip": "항목 삭제",
      "aiPromptPrefix": "다음 메모를 간결한 한 문장 요약 \"summary\"와 1~6개의 실행 가능한 \"items\"(간결한 \"title\", 짧은 분류 \"tag\", \"done\": \"false\")로 정리하세요:"
    }
  },
  "state": {
    "draft": "",
    "summary": "",
    "filter": "all",
    "aiStatus": "idle",
    "aiError": "",
    "aiUpdatedAt": ""
  },
  "collections": {
    "items": { "maxItems": 150, "sort": "newestFirst" }
  },
  "onDrop": {
    "storage": "reference",
    "action": [
      {
        "type": "state.set",
        "key": "draft",
        "value": "${$drop.text != '' ? $drop.text : $drop.name}"
      },
      {
        "type": "ai.run",
        "guard": "${$drop.text != ''}",
        "prompt": "${$i18n.aiPromptPrefix}\n[${$drop.name}]\n${$drop.text}",
        "stateMap": { "summary": "summary" },
        "collection": "items",
        "mode": "prepend",
        "itemsPath": "items",
        "fieldMap": {
          "title": "title",
          "tag": "tag",
          "done": "done"
        },
        "maxItems": 6,
        "then": {
          "type": "state.set",
          "key": "draft",
          "value": ""
        }
      }
    ]
  },
  "capsule": {
    "title": "${$i18n.capTitlePrefix}${collections.items.count}${$i18n.capTitleSuffix}",
    "summary": "${state.summary != '' ? state.summary : (collections.items.latest.title ?? $i18n.capEmptySummary)}",
    "glyph": "⚡",
    "badge": "${collections.items.count}",
    "enableBounceOnUpdate": true
  },
  "view": {
    "type": "Stack",
    "spacing": 8,
    "padding": "10,6,10,10",
    "children": [
      {
        "type": "Grid",
        "columns": "1*,Auto",
        "spacing": 6,
        "children": [
          {
            "type": "Input",
            "column": 0,
            "placeholder": "${$i18n.inputPlaceholder}",
            "bind": "state.draft",
            "onSubmit": {
              "type": "ai.run",
              "guard": "${state.draft != ''}",
              "prompt": "${$i18n.aiPromptPrefix}\n${state.draft}",
              "stateMap": { "summary": "summary" },
              "collection": "items",
              "mode": "prepend",
              "itemsPath": "items",
              "fieldMap": {
                "title": "title",
                "tag": "tag",
                "done": "done"
              },
              "maxItems": 6,
              "then": {
                "type": "state.set",
                "key": "draft",
                "value": ""
              }
            }
          },
          {
            "type": "Button",
            "column": 1,
            "icon": "⚡",
            "text": "${state.aiStatus == 'running' ? $i18n.runningBadge : $i18n.refineBtn}",
            "tooltip": "${$i18n.refineTooltip}",
            "variant": "accent",
            "onClick": {
              "type": "ai.run",
              "guard": "${state.draft != ''}",
              "prompt": "${$i18n.aiPromptPrefix}\n${state.draft}",
              "stateMap": { "summary": "summary" },
              "collection": "items",
              "mode": "prepend",
              "itemsPath": "items",
              "fieldMap": {
                "title": "title",
                "tag": "tag",
                "done": "done"
              },
              "maxItems": 6,
              "then": {
                "type": "state.set",
                "key": "draft",
                "value": ""
              }
            }
          }
        ]
      },
      {
        "type": "Grid",
        "visible": "${state.aiStatus == 'running'}",
        "columns": "Auto,1*",
        "spacing": 8,
        "children": [
          {
            "type": "ProgressRing",
            "column": 0,
            "size": 14,
            "indeterminate": true
          },
          {
            "type": "Text",
            "column": 1,
            "text": "${$i18n.runningBadge}",
            "style": "caption",
            "color": "accent",
            "maxLines": 1
          }
        ]
      },
      {
        "type": "Card",
        "visible": "${state.aiStatus == 'error' && state.aiError != ''}",
        "tone": "warning",
        "padding": "9,6,9,6",
        "children": [
          {
            "type": "Text",
            "text": "${state.aiError}",
            "style": "caption",
            "color": "warning",
            "maxLines": 2
          }
        ]
      },
      {
        "type": "Card",
        "visible": "${state.summary != ''}",
        "tone": "subtle",
        "padding": "10,8,10,8",
        "children": [
          {
            "type": "Grid",
            "columns": "1*,Auto",
            "spacing": 6,
            "children": [
              {
                "type": "Stack",
                "column": 0,
                "spacing": 2,
                "children": [
                  {
                    "type": "Text",
                    "text": "${$i18n.summaryLabel}${state.aiUpdatedAt != '' ? ' · ' : ''}${state.aiUpdatedAt}",
                    "style": "caption",
                    "color": "accent",
                    "maxLines": 1
                  },
                  {
                    "type": "Text",
                    "text": "${state.summary}",
                    "style": "body",
                    "maxLines": 2
                  }
                ]
              },
              {
                "type": "Button",
                "column": 1,
                "icon": "\uE8C8",
                "variant": "ghost",
                "tooltip": "${$i18n.copySummaryTooltip}",
                "onClick": {
                  "type": "clipboard.copy",
                  "text": "${state.summary}"
                }
              }
            ]
          }
        ]
      },
      {
        "type": "Grid",
        "columns": "1*,Auto",
        "spacing": 6,
        "children": [
          {
            "type": "Segmented",
            "column": 0,
            "bind": "state.filter",
            "options": [
              { "value": "all", "label": "${$i18n.filterAll}" },
              { "value": "todo", "label": "${$i18n.filterTodo}" },
              { "value": "done", "label": "${$i18n.filterDone}" }
            ]
          },
          {
            "type": "Button",
            "column": 1,
            "icon": "\uE74D",
            "text": "${$i18n.clearBtn}",
            "variant": "ghost",
            "onClick": [
              { "type": "collection.clear", "collection": "items" },
              { "type": "state.set", "key": "summary", "value": "" }
            ]
          }
        ]
      },
      {
        "type": "List",
        "items": "items",
        "filter": "${state.filter == 'all' || (state.filter == 'done' && $item.done == 'true') || (state.filter == 'todo' && $item.done != 'true')}",
        "emptyState": {
          "icon": "⚡",
          "title": "${$i18n.emptyTitle}",
          "subtitle": "${$i18n.emptySubtitle}"
        },
        "hoverActions": [
          {
            "type": "Button",
            "icon": "\uE74D",
            "variant": "ghost",
            "tooltip": "${$i18n.removeTooltip}",
            "onClick": {
              "type": "collection.remove",
              "collection": "items",
              "targetId": "${$item.id}"
            }
          }
        ],
        "itemTemplate": {
          "type": "Grid",
          "columns": "Auto,Auto,1*",
          "spacing": 7,
          "children": [
            {
              "type": "Button",
              "column": 0,
              "icon": "${$item.done == 'true' ? '\uE73E' : '\uEA3A'}",
              "variant": "${$item.done == 'true' ? 'accent' : 'subtle'}",
              "tooltip": "${$i18n.toggleTooltip}",
              "onClick": {
                "type": "collection.update",
                "collection": "items",
                "targetId": "${$item.id}",
                "item": {
                  "done": "${$item.done == 'true' ? 'false' : 'true'}"
                }
              }
            },
            {
              "type": "Badge",
              "column": 1,
              "visible": "${$item.tag != ''}",
              "text": "${$item.tag}",
              "tone": "${$item.done == 'true' ? 'subtle' : 'accent'}"
            },
            {
              "type": "Text",
              "column": 2,
              "text": "${$item.title}",
              "style": "${$item.done == 'true' ? 'body' : 'bodyStrong'}",
              "color": "${$item.done == 'true' ? 'muted' : 'default'}",
              "maxLines": 2
            }
          ]
        }
      }
    ]
  }
}
""";

    public static string AiAgentSkillMarkdown =>
        CustomWidgetRemotePromptService.Shared.GetEffectivePromptPack().SkillMarkdown;

    public const string DefaultAiAgentSkillMarkdown = """
---
name: deskbox-widget
description: Design, create, visually verify, hot-reload, and push live telemetry to native WinUI 3 Custom Widgets (manifest.json) in DeskBox on Windows 11.
---

# DeskBox Native Custom Widget Developer Guide (AI Agent Skill)

DeskBox Custom Widgets render **100% native WinUI 3 Fluent 2 controls** from a single declarative `manifest.json` file located at:
`%LOCALAPPDATA%\DeskBox\data\custom-widgets\<widgetId>\manifest.json`

Whenever you save changes to `manifest.json`, DeskBox's `FileSystemWatcher` automatically validates and hot-reloads the widget on the desktop within 200ms.

## 1. Architecture & Fluent 2 Design Rules
- **Zero WebView2 / Zero HTML**: Every node in `view` maps to native WinUI 3 controls (`StackPanel`, `Grid`, `Border`, `TextBlock`, `TextBox`, `Button`, `ToggleSwitch`, `ProgressRing`, `Polyline` sparkline, etc.) and inherits DeskBox's Windows 11 Mica/Acrylic backdrop, theme palette, and compact capsule animations.
- **Windows 11 Native Restraint & No Duplicate Header**: DeskBox's host window already provides a translucent glass surface and renders the top header bar (`icon`, `name`, collapse toggle, and context menu). **NEVER add a duplicate static widget-title header bar at the top of `view`**. Keep sibling sections visually consistent (`Card` with `tone`: `"subtle"` or `"default"`, max 2 nested `Card` levels), size `defaultSize.height` so all initial sections fit without vertical clipping (use `Tabs` when combining 3+ panels), and keep root `padding` compact (`"10,6,10,10"` or `"12,8,12,12"`) with `spacing` at `6`–`10`.
- **Dual-Surface Continuity & Emoji Identity**: Always set `"id"` to `"custom.<kebab-slug>"` (`deskbox.*` is reserved for official built-in presets), set top-level `"icon"` and `"capsule.glyph"` to a single expressive Unicode Emoji matching the widget's purpose (e.g., `"💧"`, `"📝"`, `"📊"`, `"🎯"`, `"🚀"`, `"🤖"`, `"📰"`, `"🔥"`), and define a `"capsule"` block so the collapsed desktop capsule displays live status.
- **Copy Language Strategy**: For user-created widgets, write clean single-language plain strings matching the user's requested/input language (or active DeskBox UI language). Official or shared multi-language templates may optionally declare `"i18n": { "en-US": { "title": "..." }, "zh-CN": { "title": "..." } }` and reference `${$i18n.title}`.

## 2. Expression Syntax (`${...}`) & Declarative Data Sources (`sources`)
- `state.<key>`: Reads reactive state defined in `"state"`.
- `$settings.<key>`: Reads user-configurable preferences defined in `"settings"`.
- `$i18n.<key>`: Reads localized copy from optional `"i18n"` locale dictionaries (live-switched with DeskBox UI language, falling back to `en-US`).
- `collections.<name>.count` / `collections.<name>.todayCount` / `collections.<name>.latest.<field>`: Reads persistent collection aggregates.
- `$item.<field>` / `$index` (0-based) / `$rank` (1-based: `"1"`, `"2"`, `"3"`, ideal for rankings and leaderboards) / `$value` (selected option value inside `Segmented.onChange`): Inside a `List`'s `itemTemplate` or `hoverActions`, reads the current record (`id`, `timeText`, `dateText`, `isToday`, plus custom fields).
- `$drop.name` / `$drop.path` / `$drop.mode` / `$drop.text`: Inside `onDrop.action`, reads dropped file metadata (`$drop.text` safely extracts up to 8 KB of plain text when the user drops `.txt`, `.md`, `.csv`, `.json`, `.log`, `.yml`, `.xml`, `.ini`, or `.toml` files — ideal for drag-to-summarize micro-agent widgets).
- **Host Telemetry & Native Sources**:
  - `$weather.*` (`$weather.city`, `$weather.temp`, `$weather.tempText`, `$weather.condition`, `$weather.icon`, `$weather.rangeText`, `$weather.humidity`, `$weather.wind`, `$weather.uv`, `$weather.sunrise`, `$weather.sunset`) + 7-day `forecast` collection (`$item.dayLabel`, `$item.icon`, `$item.condition`, `$item.high`, `$item.low`, `$item.precipProb`) when `"sources": [{ "type": "weather", "city": "${$settings.city ?? ''}", "targetCollection": "forecast", "intervalMs": 1800000 }]` (zero API key required!).
  - `$calendar.*` (`$calendar.today`, `$calendar.monthTitle`, `$calendar.weekday`, `$calendar.lunar`, `$calendar.lunarTitle`, `$calendar.festival`, `$calendar.subtitle`, `$calendar.selectedDate`) when `"sources": [{ "type": "calendar", "intervalMs": 60000 }]`.
  - `$clock.*` (`$clock.fullTime`, `$clock.utcTime`, `$clock.localOffset`, `$clock.unixSeconds`, `$clock.iso`) + multi-timezone function `tz('<zoneId>', '<format>')` (e.g. `${tz('America/New_York', 'HH:mm')}`) when `"sources": [{ "type": "clock", "intervalMs": 1000 }]`.
  - `$device.*` (`$device.volume`, `$device.volumeText`, `$device.muted`, `$device.brightness`, `$device.brightnessText`, `$device.audioDeviceName`, `$device.wifiEnabled`, `$device.wifiStatus`, `$device.bluetoothEnabled`, `$device.bluetoothStatus`, `$device.keepAwake`, `$device.mediaPlaying`, `$device.mediaStatus`, `$device.mediaToggleIcon`, `$device.mediaCover`, `$device.mediaTitle`, `$device.mediaArtist`, `$device.mediaSummary`, `$device.recycleBinCount`, `$device.recycleBinStatus`) when `"sources": [{ "type": "deviceState", "intervalMs": 2000 }]`.
  - `$drives.*` (`$drives.count`, `$drives.summary`, `$drives.primaryPercent`) + `drives` collection (`$item.name`, `$item.title`, `$item.percent`, `$item.freeGb`, `$item.totalGb`, `$item.summary`) when `"sources": [{ "type": "storageDrives", "targetCollection": "drives", "intervalMs": 15000 }]`.
  - `$clipboard.*` (`$clipboard.text`, `$clipboard.preview`, `$clipboard.length`, `$clipboard.lines`, `$clipboard.kind`, `$clipboard.kindLabel`) when `"sources": [{ "type": "clipboard", "intervalMs": 2000 }]`.
  - `$sys.*` (`$sys.cpu.percent`, `$sys.cpu.history`, `$sys.memory.percent`, `$sys.memory.summaryText`, `$sys.memory.history`, `$sys.gpu.name`, `$sys.gpu.percent`, `$sys.gpu.history`, `$sys.gpu.vramSummary`, `$sys.gpu.vramPercent`, `$sys.disk.selectedName`, `$sys.disk.selectedPercent`, `$sys.disk.selectedFreeGb`, `$sys.net.downText`, `$sys.net.upText`, `$sys.uptime.text`, `$sys.battery.statusText`) when `"sources": [{ "type": "systemMetrics", "intervalMs": 2000 }]`.
  - `$pomodoro.*` when `"sources": [{ "type": "pomodoro", "intervalMs": 1000 }]`.
  - `$countdown.*` (supports solar + 1901-2100 Chinese lunar recurring birthdays/anniversaries via `"calendarType": "lunar"`, `"recurrence": "yearly"`) when `"sources": [{ "type": "countdown", "intervalMs": 60000 }]`.
  - `$ai.*` when `"sources": [{ "type": "aiMonitor", "intervalMs": 60000 }]`.
- **Encrypted Secret Vault (`"type": "secret"`) & Live Network Data Sources (`rss` & `httpJson`, HTTPS GET only, interval >= 300000 ms / 5 min)**:
  - Declare `{ "key": "apiKey", "type": "secret", "label": "API Key" }` in `"settings"` to store per-widget tokens in Windows DPAPI (`widget-secrets.json`, never saved to `store.json` or exported in `.dbbox`) and reference `${$secrets.apiKey}` inside `httpJson.headers` (e.g., `"headers": { "Authorization": "Bearer ${$secrets.apiKey}" }`). DeskBox automatically renders an inline setup banner when unconfigured!
  - **RSS 2.0 / Atom 1.0 (`"type": "rss"`)**: Ideal for news, blogs, GitHub releases, Hacker News, V2EX, SSPai (`https://sspai.com/feed`), IT之家 (`https://www.ithome.com/rss/`), etc.!
    Example: `{ "id": "feed", "type": "rss", "url": "https://www.ithome.com/rss/", "targetCollection": "articles", "intervalMs": 900000, "maxItems": 15 }`
    Automatically populates `targetCollection` items with `$item.title`, `$item.summary`, `$item.url`, `$item.publishedAt`, `$item.category`, `$item.author`, and updates `state.feedTitle`, `state.feedCount`, `state.topHeadline`, `state.sourceStatus` (`"idle"` | `"loading"` | `"ok"` | `"auth_required"` | `"error"`), `state.sourceError`, `state.sourceUpdatedAt` (`"HH:mm"`).
  - **Public or Authenticated JSON API (`"type": "httpJson"`)**: Supports `url`, optional `headers` (`${$secrets.*}`), `stateMap`, `targetCollection`, `itemsPath`, `fieldMap`, `maxItems`.
    Example: `{ "id": "api", "type": "httpJson", "url": "https://api.example.com/v1/list?sort=${state.tab}", "intervalMs": 300000, "stateMap": { "total": "data.total" }, "targetCollection": "rows", "itemsPath": "data.items", "fieldMap": { "title": "name", "pct": "changePct", "url": "html_url" }, "maxItems": 15 }`
- Operators supported inside `${...}`: `??` (fallback), `? :` (ternary), `||`, `&&`, `==`, `!=`, `>=`, `<=`, `>`, `<`, and numeric arithmetic `+`, `-`, `*`, `/`, `%` (e.g., `${$index + 1}`, `${$item.turnover / 100000000}`).

## 3. Component Catalog (`view.type`)
- **Layout**:
  - `Stack`: `orientation` (`vertical` | `horizontal`), `spacing`, `padding`, `children`.
  - `Wrap`: Flow wrap panel for pill tags or quick action chips (`spacing`, `padding`, `children`).
  - `Spacer`: Flexible layout spacer (`height` or stretches inside horizontal `Grid`/`Stack`).
  - `ModuleStack`: User-reorderable modular stack bound to a `"type": "moduleList"` setting (`bind`: `"settings.modules"`).
  - `Grid`: `columns` (e.g. `"1*,Auto"`, `"1*,1*"`), `rows`, `spacing`, `children` (with `column`, `row`; multi-item grids auto-flow into implicit rows when `rows`/`row` are omitted).
  - `Tabs`: Multi-page segmented container (`bind`: `"state.activeTab"`, `children` where each tab item specifies `title`, `value`, optional `icon`, and `children`).
  - `Card`: Borderless glass micro-surface with `tone` (`default` | `subtle` | `accent` | `success` | `warning` | `danger`, supports `${...}` expressions), `padding`, optional `onClick` (interactive row/card click with hover highlight), `children`.
  - `Expander`: Native progressive-disclosure collapsible section with `title`, optional `subtitle`, `defaultExpanded` (`true`|`false`), optional `bind` (`"state.expanded"`), `tone`, and `children`.
  - `Scroll`: Vertical scroll container around `children`.
  - `List`: Persistent collection view specified by `items` (collection name), optional `filter` expression, `emptyState` (`icon`, `title`, `subtitle`), `itemTemplate`, and `hoverActions`.
- **Controls**:
  - `Text`: `text`, `style` (`caption` | `body` | `bodyStrong` | `subtitle` | `title` | `hero` | `mono`), `color` (`default` | `secondary` | `muted` | `accent` | `violet` | `emerald` | `cyan` | `success` | `warning` | `danger` | `#RRGGBB`, supports `${...}` expressions!), `maxLines`.
  - `Input`: `placeholder`, `bind` (`"state.draftText"`), `onSubmit` (actions triggered on Enter).
  - `TextArea`: Multi-line native text input with `placeholder`, `bind`, `minHeight`, `maxHeight`, and `onSubmit` (triggered on `Ctrl+Enter`).
  - `Button`: `icon` (Segoe Fluent Icons char/hex or Emoji), `text`, `variant` (`standard` | `accent` | `subtle` | `ghost` | `danger`), `tooltip`, `onClick`.
  - `IconButton`: Compact 24px/28px square icon action button (`icon`, `size`, `variant`: `subtle`|`accent`|`danger`, `tooltip`, `onClick`) ideal for inline bars and `hoverActions`.
  - `Toggle`: Native Windows 11 `ToggleSwitch` with `title` (label), `bind` (`"state.flagKey"`), and optional `onClick` follow-up actions.
  - `CheckBox`: Compact native `CheckBox` row with `bind` or `value`, `text`, `strikethrough` (`true`|`false`), and `onToggle` / `onChange`.
  - `Select`: Native `ComboBox` dropdown selector with optional `title`, `bind` (`"state.category"`), `options` (`[{ "value": "...", "label": "..." }]`), and optional `onChange`.
  - `Slider`: Native horizontal `Slider` with optional `title`, `bind` OR live two-way `value` expression (e.g. `"${$device.volume}"`), `min`, `max`, `step`, `unit` (e.g. `"m"`, `"%"`), and optional `onChange` (`${$value}` = live slider value; never add a redundant submit button next to `Slider`).
  - `Image`: Rounded local/SMTC/HTTPS image view (`src`, `width`, `height`, `stretch`: `uniform`|`uniformToFill`|`fill`, `cornerRadius`).
  - `RingGauge`: Circular KPI gauge with centered metric (`progress`/`value`, `max`, `title`, `subtitle`, `unit`, `size`, `color`).
  - `BarList`: Ranked horizontal proportion bar list bound to `collection` (`titleField`, `valueField`, `subtitleField`, `unit`, `maxItems`, `color`).
  - `NumberBox`: Native `NumberBox` with inline spin buttons, optional `title`, `bind`, `min`, `max`, `step`, and optional `onChange`.
  - `ProgressRing`: Native Windows 11 `ProgressRing` (`size`, `value` 0–100, `indeterminate` boolean expression).
  - `Segmented`: Pill segmented selector bound to `bind` (`"state.activeTag"`), `options` (`[{ "value": "...", "label": "..." }]`), optional `onChange` actions (`$value` = newly selected option).
  - `Badge`: Semantic pill tag with `text` and `tone` (`accent` | `subtle` | `muted` | `emerald` | `violet` | `cyan` | `success` | `warning` | `danger`, supports `${...}` expressions!).
- **High-Craft Semantic, Composite & Micro-Chart Blocks**:
  - `CalendarGrid`: Interactive 7x6 native monthly calendar with month switching, lunar/festival subtitles, `bind` (`"state.selectedDate"`), and event dots from `collection` (`dateField` default `"date"`).
  - `DropZone`: Native visual drag-and-drop + click-to-browse zone (`icon`, `title`, `subtitle`, `format` e.g. `".png,.jpg,.webp"`, `onDrop` actions).
  - `ControlTile`: Windows 11 Quick Settings toggle tile (`icon`, `title`, `subtitle`, `bind` or `value` active boolean expression, `onClick` actions).
  - `Sparkline`: Vector trend chart with soft area fill (`collection` + `valueField` OR `sparkline` comma-separated series, `title`, `unit`, `height`, `color`, `showArea`, `showLatest`).
  - `HeatmapGrid`: GitHub-style daily habit/activity intensity grid (`collection`, `valueField`, optional `dateField`, `days`, `size`, `color`, `title`).
  - `StatCard`: Metric card with `title`, `value`, `subtitle`, `progress`, `sparkline` (`"$sys.cpu.history"` / `"$sys.memory.history"` / `"$sys.gpu.history"`), `color`, `warningThreshold`, `dangerThreshold`.
  - `GpuCard`, `ProcessList`, `PomodoroCard`, `CountdownHero`, `CountdownList`, `TimeProgressCard`, `AiFocusCard`, `AiRadarList`, `AiTokensCard`, `AiTrendCard`.
  - `ProgressRow`: Labeled progress bar row with `title`, `subtitle`, `value`, `progress`, `warningThreshold`, `dangerThreshold`.
  - `KeyValueRow`: Compact metric/info row with `icon`, `title`, `value`.
  - `FileChip`: Interactive file attachment pill (`fileName`, `filePath`, `storageMode`).

## 4. Declarative Actions
- `state.set`: `{ "type": "state.set", "key": "draftText", "value": "" }`
- `state.toggle`: `{ "type": "state.toggle", "key": "flagKey" }`
- `state.increment`: `{ "type": "state.increment", "key": "counterKey", "value": "1" }`
- `collection.prepend` / `collection.append`: `{ "type": "collection.prepend", "collection": "logs", "guard": "${state.draftText != ''}", "item": { "text": "${state.draftText}" } }`
- `collection.update`: `{ "type": "collection.update", "collection": "tasks", "targetId": "${$item.id}", "item": { "done": "${$item.done == 'true' ? 'false' : 'true'}" } }`
- `collection.remove`: `{ "type": "collection.remove", "collection": "logs", "targetId": "${$item.id}" }`
- `collection.clear`: `{ "type": "collection.clear", "collection": "logs" }`
- `collection.exportMarkdown`: `{ "type": "collection.exportMarkdown", "collection": "logs" }`
- `file.transform`: Zero-dependency native local converter (`png` | `jpg` | `webp` | `bmp` | `ico` | `csvToJson` | `jsonToCsv` | `jsonPretty` | `jsonMinify` | `mdToHtml` | `mdToTxt` | `base64Encode` | `base64Decode` | `sha256` | `md5`), populating `$transform.outputPath`, `$transform.outputName`, `$transform.sizeText`, `$transform.resultText`, `state.transformStatus`, `state.transformOutput`.
- `text.transform`: Pure in-memory text processor (`format`: `jsonPretty` | `jsonMinify` | `urlEncode` | `urlDecode` | `base64Encode` | `base64Decode` | `timestampToDate` | `dateToTimestamp` | `uuid` | `upper` | `lower` | `trim` | `wordCount` | `sha256` | `md5`, `text`, output `key`, optional `mode`: `"copy"`).
- `launcher.open`: Safe workflow & protocol launcher (`targetId`/`path`/`url`: standard folders `downloads`|`desktop`|`documents`|`pictures`, whitelisted URIs `vscode://`, `obsidian://`, `ms-screenclip:`, `ms-calculator:`, `https://`, or existing local path).
- `file.pick`: Opens native file picker (`format`: `".png,.jpg"`) and chains `then` with `$drop.*` populated.
- `file.open` / `file.reveal`: `{ "type": "file.open", "path": "${$item.filePath}" }`
- `system.control`: Executes safe Windows control (`targetId`/`key`: `volume.set` | `volume.step` | `volume.muteToggle` | `brightness.set` | `brightness.step` | `audio.output` | `media.playPause` | `media.next` | `media.previous` | `power.keepAwakeToggle` | `power.lock` | `recycleBin.empty` | `radio.wifiToggle` | `radio.bluetoothToggle`, optional `value` supporting `${$value}`).
- `system.openSetting`: Opens `ms-settings:` URI (`key` or `url`, e.g. `"ms-settings:network-wifi"`).
- `reminder.notify` / `toast.show`: Sends Windows 11 system Toast notification (`title`, `message`) + widget banner.
- `url.open`: `{ "type": "url.open", "url": "${$item.url}" }` (opens `https://` link in user's browser)
- `source.refresh`: `{ "type": "source.refresh", "targetId": "feed" }` (triggers immediate manual refresh of `rss` / `httpJson` / `weather` / `deviceState` sources)
- `ai.run`: Runs a fast non-thinking structured AI micro-task inside the widget using DeskBox's global AI provider and maps the JSON output into `state` and/or `collections`:
  `{ "type": "ai.run", "guard": "${state.draft != ''}", "prompt": "Summarize and extract actionable tasks:\n${state.draft}", "stateMap": { "summary": "summary" }, "collection": "items", "mode": "prepend", "itemsPath": "items", "fieldMap": { "title": "title", "tag": "tag", "done": "done" }, "maxItems": 6, "then": { "type": "state.set", "key": "draft", "value": "" } }`
  Automatically updates `state.aiStatus` (`"idle"` | `"running"` | `"error"`), `state.aiError`, and `state.aiUpdatedAt` (`"HH:mm"`). On failure or missing API key, `then` is skipped so user input in `state.draft` is preserved.
- `clipboard.copy`: `{ "type": "clipboard.copy", "text": "..." }`
- `pomodoro.*`, `countdown.*`, `ai.*` built-in domain actions.

## 5. Agent MCP Tools & CLI (`DeskBox.exe`)
When connected via MCP (`DeskBox.exe --mcp`) or CLI (`DeskBox.exe widget <subcommand>`):
- **MCP Tools**:
  - `deskbox_get_widget_spec`: Read this specification, Tier-0 capability directory, and optional JSON Schema.
  - `deskbox_get_capability_spec`: Search or inspect any of the 26 atomic capability modules (`API_REFERENCE.md` single source of truth) with exact tokens and configuration snippets.
  - `deskbox_list_widgets`: List all widgets, or pass `{ "widgetId": "<id>" }` to inspect one widget's live `state` (`sourceStatus`, `sourceError`, `sourceUpdatedAt`), `displayMode`, `collectionCounts`, and `sampleItems`.
  - `deskbox_validate_manifest`: Validate `manifest.json` syntax, components, actions, and state/collection references.
  - `deskbox_upsert_widget`: Create or hot-reload a desktop widget.
  - `deskbox_capture_widget_preview`: Capture a real native WinUI 3 PNG screenshot (`mode`: `"expanded"` by default so full UI is rendered even when the desktop is in Capsule Mode; `"capsule"` for compact strip; `"live"` for raw desktop window).
  - `deskbox_push_widget_data`: Push live state/collection updates (e.g., Ambient Agent HUD).
- **CLI Subcommands**:
  - `DeskBox.exe widget list`
  - `DeskBox.exe widget get --id <widgetId>`
  - `DeskBox.exe widget validate --file manifest.json`
  - `DeskBox.exe widget create --file manifest.json`
  - `DeskBox.exe widget update --id <widgetId> --file manifest.json`
  - `DeskBox.exe widget preview --id <widgetId> --out preview.png [--mode expanded|capsule|live]`
  - `DeskBox.exe widget push --id <widgetId> --set statusText="Running" --set progress=65 --append events:text="Build succeeded"`
""";

    public const string ManifestJsonSchema = """
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://deskbox.app/schemas/custom-widget-manifest.v1.json",
  "title": "DeskBox Custom Widget Manifest",
  "type": "object",
  "required": ["schemaVersion", "id", "name", "view"],
  "properties": {
    "$schema": { "type": "string" },
    "schemaVersion": { "type": "integer", "const": 1 },
    "id": { "type": "string", "minLength": 1 },
    "name": { "type": "string", "minLength": 1 },
    "version": { "type": "string" },
    "author": { "type": "string" },
    "description": { "type": "string" },
    "icon": { "type": "string" },
    "minSize": { "$ref": "#/$defs/sizeSpec" },
    "defaultSize": { "$ref": "#/$defs/sizeSpec" },
    "i18n": {
      "type": "object",
      "additionalProperties": {
        "type": "object",
        "additionalProperties": { "type": "string" }
      }
    },
    "settings": {
      "type": "array",
      "items": { "$ref": "#/$defs/settingDef" }
    },
    "state": {
      "type": "object",
      "additionalProperties": { "type": ["string", "number", "boolean"] }
    },
    "collections": {
      "type": "object",
      "additionalProperties": {
        "type": "object",
        "properties": {
          "maxItems": { "type": "integer", "minimum": 1, "maximum": 5000 },
          "sort": { "type": "string", "enum": ["newestFirst", "oldestFirst"] }
        }
      }
    },
    "sources": {
      "type": "array",
      "items": {
        "type": "object",
        "required": ["type"],
        "properties": {
          "id": { "type": "string" },
          "type": { "type": "string", "enum": ["systemMetrics", "storageDrives", "pomodoro", "countdown", "aiMonitor", "weather", "calendar", "clock", "deviceState", "clipboard", "rss", "httpJson"] },
          "url": { "type": "string" },
          "city": { "type": "string" },
          "headers": { "type": "object", "additionalProperties": { "type": "string" } },
          "targetCollection": { "type": "string" },
          "itemsPath": { "type": "string" },
          "maxItems": { "type": "integer", "minimum": 1, "maximum": 100 },
          "fieldMap": { "type": "object", "additionalProperties": { "type": "string" } },
          "stateMap": { "type": "object", "additionalProperties": { "type": "string" } },
          "intervalMs": { "type": "integer", "minimum": 250 }
        }
      }
    },
    "onDrop": {
      "type": "object",
      "properties": {
        "storage": { "type": "string", "enum": ["reference", "copy", "auto"] },
        "action": { "$ref": "#/$defs/actionOrList" }
      }
    },
    "capsule": {
      "type": "object",
      "properties": {
        "title": { "type": "string" },
        "summary": { "type": "string" },
        "glyph": { "type": "string" },
        "badge": { "type": "string" },
        "progress": { "type": ["string", "number"] },
        "enableBounceOnUpdate": { "type": "boolean" }
      }
    },
    "view": { "$ref": "#/$defs/nodeSpec" }
  },
  "$defs": {
    "sizeSpec": {
      "type": "object",
      "properties": {
        "width": { "type": "integer", "minimum": 160, "maximum": 1600 },
        "height": { "type": "integer", "minimum": 100, "maximum": 1600 }
      }
    },
    "optionSpec": {
      "type": "object",
      "required": ["value", "label"],
      "properties": {
        "value": { "type": "string" },
        "label": { "type": "string" }
      }
    },
    "settingDef": {
      "type": "object",
      "required": ["key", "type", "label"],
      "properties": {
        "key": { "type": "string" },
        "type": { "type": "string", "enum": ["toggle", "choice", "text", "number", "moduleList", "secret"] },
        "label": { "type": "string" },
        "description": { "type": "string" },
        "default": { "type": ["string", "number", "boolean"] },
        "min": { "type": "number" },
        "max": { "type": "number" },
        "options": {
          "type": "array",
          "items": { "$ref": "#/$defs/optionSpec" }
        }
      }
    },
    "actionSpec": {
      "type": "object",
      "required": ["type"],
      "properties": {
        "type": {
          "type": "string",
          "enum": [
            "state.set",
            "state.toggle",
            "state.increment",
            "collection.prepend",
            "collection.append",
            "collection.update",
            "collection.remove",
            "collection.clear",
            "collection.exportMarkdown",
            "file.open",
            "file.reveal",
            "file.transform",
            "file.pick",
            "text.transform",
            "launcher.open",
            "system.control",
            "system.openSetting",
            "url.open",
            "source.refresh",
            "refreshSource",
            "clipboard.copy",
            "toast.show",
            "reminder.notify",
            "pomodoro.toggle",
            "pomodoro.reset",
            "pomodoro.extend",
            "pomodoro.skip",
            "pomodoro.cyclePhase",
            "pomodoro.setPhase",
            "countdown.pin",
            "countdown.delete",
            "ai.refresh",
            "ai.selectProvider",
            "ai.run"
          ]
        },
        "guard": { "type": "string" },
        "key": { "type": "string" },
        "value": { "type": ["string", "number", "boolean"] },
        "collection": { "type": "string" },
        "targetCollection": { "type": "string" },
        "targetId": { "type": "string" },
        "path": { "type": "string" },
        "format": { "type": "string" },
        "maxWidth": { "type": "integer" },
        "quality": { "type": "integer" },
        "url": { "type": "string" },
        "title": { "type": "string" },
        "text": { "type": "string" },
        "message": { "type": "string" },
        "prompt": { "type": "string" },
        "mode": { "type": "string", "enum": ["prepend", "append", "replace", "copy"] },
        "itemsPath": { "type": "string" },
        "maxItems": { "type": "integer", "minimum": 1, "maximum": 50 },
        "stateMap": { "type": "object", "additionalProperties": { "type": "string" } },
        "fieldMap": { "type": "object", "additionalProperties": { "type": "string" } },
        "item": {
          "type": "object",
          "additionalProperties": { "type": ["string", "number", "boolean"] }
        },
        "then": { "$ref": "#/$defs/actionOrList" }
      }
    },
    "actionOrList": {
      "oneOf": [
        { "$ref": "#/$defs/actionSpec" },
        { "type": "array", "items": { "$ref": "#/$defs/actionSpec" } }
      ]
    },
    "emptyStateSpec": {
      "type": "object",
      "properties": {
        "icon": { "type": "string" },
        "title": { "type": "string" },
        "subtitle": { "type": "string" }
      }
    },
    "nodeSpec": {
      "type": "object",
      "required": ["type"],
      "properties": {
        "type": {
          "type": "string",
          "enum": [
            "Stack",
            "Wrap",
            "Spacer",
            "ModuleStack",
            "Grid",
            "Card",
            "Scroll",
            "Text",
            "Badge",
            "Input",
            "Button",
            "Toggle",
            "ProgressRing",
            "Segmented",
            "StatCard",
            "GpuCard",
            "ProcessList",
            "PomodoroCard",
            "CountdownHero",
            "CountdownList",
            "TimeProgressCard",
            "AiFocusCard",
            "AiRadarList",
            "AiTokensCard",
            "AiTrendCard",
            "ProgressRow",
            "KeyValueRow",
            "List",
            "FileChip",
            "EmptyState",
            "Expander",
            "TextArea",
            "IconButton",
            "CheckBox",
            "Select",
            "Slider",
            "NumberBox",
            "Sparkline",
            "HeatmapGrid",
            "CalendarGrid",
            "DropZone",
            "ControlTile",
            "Divider",
            "ProgressBar",
            "Image",
            "Tabs",
            "RingGauge",
            "BarList"
          ]
        },
        "id": { "type": "string" },
        "visible": { "type": ["string", "boolean"] },
        "orientation": { "type": "string", "enum": ["vertical", "horizontal"] },
        "spacing": { "type": "number" },
        "padding": { "type": ["string", "number"] },
        "columns": { "type": "string" },
        "rows": { "type": "string" },
        "column": { "type": "integer" },
        "row": { "type": "integer" },
        "tone": { "type": "string" },
        "variant": { "type": "string" },
        "style": { "type": "string" },
        "color": { "type": "string" },
        "src": { "type": "string" },
        "stretch": { "type": "string", "enum": ["uniform", "uniformToFill", "fill", "none"] },
        "text": { "type": "string" },
        "title": { "type": "string" },
        "subtitle": { "type": "string" },
        "value": { "type": ["string", "number", "boolean"] },
        "placeholder": { "type": "string" },
        "format": { "type": "string" },
        "icon": { "type": "string" },
        "tooltip": { "type": "string" },
        "bind": { "type": "string" },
        "progress": { "type": ["string", "number"] },
        "sparkline": { "type": "string" },
        "size": { "type": "number" },
        "width": { "type": "number" },
        "height": { "type": "number" },
        "minHeight": { "type": "number" },
        "maxHeight": { "type": "number" },
        "min": { "type": "number" },
        "max": { "type": "number" },
        "step": { "type": "number" },
        "unit": { "type": "string" },
        "strikethrough": { "type": "boolean" },
        "defaultExpanded": { "type": "boolean" },
        "showArea": { "type": "boolean" },
        "showLatest": { "type": "boolean" },
        "collection": { "type": "string" },
        "titleField": { "type": "string" },
        "subtitleField": { "type": "string" },
        "valueField": { "type": "string" },
        "dateField": { "type": "string" },
        "days": { "type": "integer" },
        "indeterminate": { "type": ["string", "boolean"] },
        "maxLines": { "type": "integer" },
        "warningThreshold": { "type": "number" },
        "dangerThreshold": { "type": "number" },
        "items": { "type": "string" },
        "filter": { "type": "string" },
        "fileName": { "type": "string" },
        "filePath": { "type": "string" },
        "storageMode": { "type": "string" },
        "options": {
          "type": "array",
          "items": { "$ref": "#/$defs/optionSpec" }
        },
        "emptyState": { "$ref": "#/$defs/emptyStateSpec" },
        "itemTemplate": { "$ref": "#/$defs/nodeSpec" },
        "hoverActions": {
          "type": "array",
          "items": { "$ref": "#/$defs/nodeSpec" }
        },
        "horizontalAlignment": { "type": "string" },
        "verticalAlignment": { "type": "string" },
        "cornerRadius": { "type": "number" },
        "onClick": { "$ref": "#/$defs/actionOrList" },
        "onChange": { "$ref": "#/$defs/actionOrList" },
        "onToggle": { "$ref": "#/$defs/actionOrList" },
        "onSubmit": { "$ref": "#/$defs/actionOrList" },
        "onDrop": { "$ref": "#/$defs/actionOrList" },
        "children": {
          "type": "array",
          "items": { "$ref": "#/$defs/nodeSpec" }
        }
      }
    }
  }
}
""";
}
