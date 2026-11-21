using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Models;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed record StudioBriefUpdateSpec
{
    public bool Reset { get; init; }
    public string? Purpose { get; init; }
    public IReadOnlyList<string> AddConfirmed { get; init; } = [];
    public IReadOnlyList<string> AddRejected { get; init; } = [];
    public IReadOnlyList<string> AddStylePreferences { get; init; } = [];

    public bool HasAnyChange =>
        Reset ||
        !string.IsNullOrWhiteSpace(Purpose) ||
        AddConfirmed.Count > 0 ||
        AddRejected.Count > 0 ||
        AddStylePreferences.Count > 0;
}

public sealed record CustomWidgetDesignBrief
{
    public string Purpose { get; init; } = string.Empty;
    public IReadOnlyList<string> ConfirmedRequirements { get; init; } = [];
    public IReadOnlyList<string> RejectedIdeas { get; init; } = [];
    public IReadOnlyList<string> StylePreferences { get; init; } = [];

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Purpose) &&
        ConfirmedRequirements.Count == 0 &&
        RejectedIdeas.Count == 0 &&
        StylePreferences.Count == 0;
}

public sealed record StudioPersistedRevision
{
    public required string RevisionId { get; init; }
    public long CreatedAtUtcTicks { get; init; } = DateTimeOffset.UtcNow.UtcTicks;
    public string Source { get; init; } = "ai";
    public string ChangeSummary { get; init; } = string.Empty;
    public required string ManifestSha256 { get; init; }
    public bool IsCommitted { get; init; }
    public required string ManifestJson { get; init; }
    public string? LogicJs { get; init; }
    public CustomWidgetLocaleCatalog? Locales { get; init; }
    public string? CoalesceGroupKey { get; init; }
    public string? BriefMarkdown { get; init; }
}

public sealed record StudioActionChip(
    string Label,
    string Prompt,
    string Kind = "prompt",
    string? TargetProperty = null,
    string? TargetValue = null);

public sealed record StudioClarifyOptionSpec(
    string Value,
    string Label,
    bool Recommended = false);

public sealed record StudioClarifyQuestionSpec(
    string Id,
    string Header,
    IReadOnlyList<StudioClarifyOptionSpec> Options,
    bool MultiSelect = false,
    bool AllowCustomInput = true,
    string? Placeholder = null);

public sealed record StudioChatMessage
{
    public required string Id { get; init; }
    public long CreatedAtUtcTicks { get; init; } = DateTimeOffset.UtcNow.UtcTicks;
    public required string Role { get; init; }
    public required string Kind { get; init; }
    public required string Text { get; init; }
    public string? LinkedRevisionId { get; init; }
    public bool IsSuperseded { get; init; }
    public string? TargetNodePath { get; init; }
    public string? TargetNodeLabel { get; init; }
    public string? ExecutionSummary { get; init; }
    public string? ThinkingText { get; init; }
    public IReadOnlyList<StudioStreamStepItem> ExecutionSteps { get; init; } = [];
    public IReadOnlyList<string> DiffPills { get; init; } = [];
    public IReadOnlyList<string> Suggestions { get; init; } = [];
    public IReadOnlyList<StudioActionChip> ActionChips { get; init; } = [];
    public IReadOnlyList<StudioClarifyQuestionSpec> ClarifyQuestions { get; init; } = [];
    public string? SubmittedSummary { get; init; }
    public string? LimitationNote { get; init; }
}

public sealed record StudioDraftSnapshot
{
    public long UpdatedAtUtcTicks { get; init; } = DateTimeOffset.UtcNow.UtcTicks;
    public string? TargetWidgetId { get; init; }
    public bool IsExternalImportSession { get; init; }
    public string SelectedEmoji { get; init; } = string.Empty;
    public string StartDraftIdea { get; init; } = string.Empty;
    public string RefineDraftIdea { get; init; } = string.Empty;
    public int SelectedRevisionIndex { get; init; }
    public string BriefMarkdown { get; init; } = string.Empty;
    public string? PreviewSampleDataJson { get; init; }
    public IReadOnlyList<string> PinnedCapabilityIds { get; init; } = [];
    public IReadOnlyList<string> LoadedCapabilityPackIds { get; init; } = [];
    public IReadOnlyList<StudioPersistedRevision> Revisions { get; init; } = [];
    public IReadOnlyList<StudioChatMessage> ChatMessages { get; init; } = [];
}

public sealed record StudioLoadedSession(
    string BriefMarkdown,
    CustomWidgetDesignBrief ParsedBrief,
    IReadOnlyList<StudioPersistedRevision> Revisions,
    IReadOnlyList<StudioChatMessage> ChatMessages,
    StudioDraftSnapshot? PendingDraft,
    bool WasReconciledWithExternalEdit);

/// <summary>
/// Manages per-widget co-creation persistence under <c>data/custom-widgets/{widgetId}/</c>:
/// <list type="bullet">
/// <item><c>brief.md</c>: Compact shared Markdown Design Brief (backed up, excluded from share packages).</item>
/// <item><c>revisions.json</c>: Bounded manifest revision snapshots (max 20, up to 8 pinned committed slots, content-addressed <c>localeBlobs</c>).</item>
/// <item><c>studio-chat.json</c>: Recent 16 conversational messages (local-only, excluded from backups &amp; exports).</item>
/// <item><c>studio-draft.json</c> &amp; <c>_studio-draft/</c>: Uncommitted Studio session recovery &amp; clean discard.</item>
/// </list>
/// Uses <see cref="JsonDocument"/> and <see cref="Utf8JsonWriter"/> exclusively for NativeAOT safety.
/// </summary>
public sealed class CustomWidgetStudioSessionStore
{
    public const string BriefFileName = "brief.md";
    public const string RevisionsFileName = "revisions.json";
    public const string ChatFileName = "studio-chat.json";
    public const string DraftFileName = "studio-draft.json";
    public const string CreateDraftFolderName = "_studio-draft";

    public const int MaxRevisions = 20;
    public const int MaxPinnedCommittedRevisions = 8;
    public const int MaxChatMessages = 16;
    public const int MaxBriefBytes = 4096;
    public const int MaxBulletsPerBriefSection = 10;

    private static readonly JsonWriterOptions IndentedJsonOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonDocumentOptions PermissiveJsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly string? _rootDataDirectoryOverride;

    public static CustomWidgetStudioSessionStore Shared { get; } = new();

    public CustomWidgetStudioSessionStore(string? rootDataDirectory = null)
    {
        _rootDataDirectoryOverride = string.IsNullOrWhiteSpace(rootDataDirectory)
            ? null
            : Path.GetFullPath(rootDataDirectory);
    }

    public string WidgetsRootDirectory
    {
        get
        {
            string baseDir = _rootDataDirectoryOverride ?? DeskBoxDataPathService.Current.DataDirectory;
            string root = Path.Combine(baseDir, CustomWidgetDataStore.SubdirectoryName);
            Directory.CreateDirectory(root);
            return root;
        }
    }

    public string GetWidgetDirectory(string widgetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        return Path.Combine(WidgetsRootDirectory, SanitizeFolderName(widgetId));
    }

    public string GetCreateDraftDirectory() =>
        Path.Combine(WidgetsRootDirectory, CreateDraftFolderName);

    private static readonly System.Collections.Frozen.FrozenSet<string> VolatileRootManifestProperties =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            "origin",
            "isCustomized",
            "forkedFromId",
            "forkedFromVersion",
            "createdAtUtc",
            "updatedAtUtc",
            "minAppVersion"
        ],
        StringComparer.Ordinal);

    public static string ComputeManifestSha256(
        string? manifestJson,
        string? logicJs = null,
        CustomWidgetLocaleCatalog? locales = null)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return string.Empty;
        }

        string normalized = manifestJson.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        string localesSha = locales is { HasAnyLocale: true } ? locales.ComputeSha256() : string.Empty;
        if (!string.IsNullOrWhiteSpace(logicJs) || !string.IsNullOrWhiteSpace(localesSha))
        {
            var sb = new StringBuilder(normalized.Length + (logicJs?.Length ?? 0) + localesSha.Length + 32);
            sb.Append(normalized);
            if (!string.IsNullOrWhiteSpace(logicJs))
            {
                sb.Append("\n\0LOGIC:\n")
                  .Append(logicJs.Replace("\r\n", "\n", StringComparison.Ordinal).Trim());
            }
            if (!string.IsNullOrWhiteSpace(localesSha))
            {
                sb.Append("\n\0LOCALES:\n")
                  .Append(localesSha);
            }
            normalized = sb.ToString();
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Computes a canonical SHA-256 fingerprint that ignores host-stamped provenance/timestamp fields
    /// (<c>origin</c>, <c>isCustomized</c>, <c>forkedFrom*</c>, <c>createdAtUtc</c>, <c>updatedAtUtc</c>, <c>minAppVersion</c>)
    /// and syncs the <c>script</c> entry with <paramref name="logicJs"/> so committing an active Studio revision
    /// updates that revision in-place rather than creating phantom empty revisions.
    /// </summary>
    public static string ComputeSemanticBundleSha256(
        string? manifestJson,
        string? logicJs = null,
        CustomWidgetLocaleCatalog? locales = null)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return string.Empty;
        }

        string canonicalManifest;
        try
        {
            string syncedJson = CustomWidgetTemplates.SyncManifestScriptPropertyJson(manifestJson, logicJs);
            using JsonDocument doc = JsonDocument.Parse(syncedJson, PermissiveJsonOptions);
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(
                       ms,
                       new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                WriteCanonicalSemanticJson(writer, doc.RootElement, isRoot: true);
            }
            canonicalManifest = Encoding.UTF8.GetString(ms.ToArray());
        }
        catch
        {
            canonicalManifest = manifestJson.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        }

        return ComputeManifestSha256(canonicalManifest, logicJs, locales);
    }

    private static void WriteCanonicalSemanticJson(Utf8JsonWriter writer, JsonElement el, bool isRoot)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty prop in el.EnumerateObject().OrderBy(static p => p.Name, StringComparer.Ordinal))
                {
                    if (isRoot && VolatileRootManifestProperties.Contains(prop.Name))
                    {
                        continue;
                    }

                    writer.WritePropertyName(prop.Name);
                    WriteCanonicalSemanticJson(writer, prop.Value, isRoot: false);
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in el.EnumerateArray())
                {
                    WriteCanonicalSemanticJson(writer, item, isRoot: false);
                }
                writer.WriteEndArray();
                break;

            default:
                el.WriteTo(writer);
                break;
        }
    }

    public static CustomWidgetDesignBrief ParseBriefMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new CustomWidgetDesignBrief();
        }

        var purposeLines = new List<string>();
        var confirmed = new List<string>();
        var rejected = new List<string>();
        var style = new List<string>();

        int currentSection = 0; // 0 = purpose/root, 1 = confirmed, 2 = rejected, 3 = style
        string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                string heading = line.TrimStart('#').Trim();
                if (ContainsAnyIgnoreCase(heading, "已确认", "Confirmed", "Requirements", "核心需求"))
                {
                    currentSection = 1;
                }
                else if (ContainsAnyIgnoreCase(heading, "否决", "不要", "Rejected", "Avoid", "Non-Goals"))
                {
                    currentSection = 2;
                }
                else if (ContainsAnyIgnoreCase(heading, "视觉", "交互", "风格", "Style", "Visual", "UX"))
                {
                    currentSection = 3;
                }
                else if (ContainsAnyIgnoreCase(heading, "用途", "定位", "Purpose", "Goal", "Design Brief", "设计简报"))
                {
                    currentSection = 0;
                }
                continue;
            }

            string item = TrimBulletPrefix(line);
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            switch (currentSection)
            {
                case 1:
                    AddUniqueLimited(confirmed, item, MaxBulletsPerBriefSection);
                    break;
                case 2:
                    AddUniqueLimited(rejected, item, MaxBulletsPerBriefSection);
                    break;
                case 3:
                    AddUniqueLimited(style, item, MaxBulletsPerBriefSection);
                    break;
                default:
                    purposeLines.Add(TruncateLine(item, 180));
                    break;
            }
        }

        string purpose = string.Join(" ", purposeLines).Trim();
        if (purpose.Length > 280)
        {
            purpose = purpose[..280].TrimEnd();
        }

        return new CustomWidgetDesignBrief
        {
            Purpose = purpose,
            ConfirmedRequirements = confirmed,
            RejectedIdeas = rejected,
            StylePreferences = style
        };
    }

    public static CustomWidgetDesignBrief ApplyBriefUpdates(
        CustomWidgetDesignBrief existing,
        StudioBriefUpdateSpec? updates)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (updates is null || !updates.HasAnyChange)
        {
            return existing;
        }

        CustomWidgetDesignBrief baseline = updates.Reset ? new CustomWidgetDesignBrief() : existing;
        string purpose = !string.IsNullOrWhiteSpace(updates.Purpose)
            ? TruncateLine(updates.Purpose.Trim(), 240)
            : baseline.Purpose;

        var confirmed = new List<string>(baseline.ConfirmedRequirements);
        var rejected = new List<string>(baseline.RejectedIdeas);
        var style = new List<string>(baseline.StylePreferences);

        foreach (string raw in updates.AddRejected)
        {
            string cleaned = TruncateLine(TrimBulletPrefix(raw), 120);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            confirmed.RemoveAll(x => string.Equals(x, cleaned, StringComparison.OrdinalIgnoreCase));
            style.RemoveAll(x => string.Equals(x, cleaned, StringComparison.OrdinalIgnoreCase));
            AddRecentUnique(rejected, cleaned, MaxBulletsPerBriefSection);
        }

        foreach (string raw in updates.AddConfirmed)
        {
            string cleaned = TruncateLine(TrimBulletPrefix(raw), 120);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            rejected.RemoveAll(x => string.Equals(x, cleaned, StringComparison.OrdinalIgnoreCase));
            AddRecentUnique(confirmed, cleaned, MaxBulletsPerBriefSection);
        }

        foreach (string raw in updates.AddStylePreferences)
        {
            string cleaned = TruncateLine(TrimBulletPrefix(raw), 120);
            if (string.IsNullOrWhiteSpace(cleaned))
            {
                continue;
            }

            rejected.RemoveAll(x => string.Equals(x, cleaned, StringComparison.OrdinalIgnoreCase));
            AddRecentUnique(style, cleaned, MaxBulletsPerBriefSection);
        }

        return new CustomWidgetDesignBrief
        {
            Purpose = purpose,
            ConfirmedRequirements = confirmed,
            RejectedIdeas = rejected,
            StylePreferences = style
        };
    }

    public static string FormatBriefMarkdown(CustomWidgetDesignBrief brief, bool preferEnglish = false)
    {
        ArgumentNullException.ThrowIfNull(brief);
        if (brief.IsEmpty)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine(preferEnglish ? "# Purpose" : "# 用途定位");
        if (!string.IsNullOrWhiteSpace(brief.Purpose))
        {
            sb.AppendLine(TruncateLine(brief.Purpose, 240));
        }
        sb.AppendLine();

        if (brief.ConfirmedRequirements.Count > 0)
        {
            sb.AppendLine(preferEnglish ? "## Confirmed Requirements" : "## 已确认需求");
            foreach (string item in brief.ConfirmedRequirements.Take(MaxBulletsPerBriefSection))
            {
                sb.Append("- ").AppendLine(TruncateLine(item, 120));
            }
            sb.AppendLine();
        }

        if (brief.RejectedIdeas.Count > 0)
        {
            sb.AppendLine(preferEnglish ? "## Rejected Ideas" : "## 明确否决项");
            foreach (string item in brief.RejectedIdeas.Take(MaxBulletsPerBriefSection))
            {
                sb.Append("- ").AppendLine(TruncateLine(item, 120));
            }
            sb.AppendLine();
        }

        if (brief.StylePreferences.Count > 0)
        {
            sb.AppendLine(preferEnglish ? "## Style Preferences" : "## 视觉与交互偏好");
            foreach (string item in brief.StylePreferences.Take(MaxBulletsPerBriefSection))
            {
                sb.Append("- ").AppendLine(TruncateLine(item, 120));
            }
        }

        return ClampUtf8Length(sb.ToString().Trim(), MaxBriefBytes);
    }

    public static IReadOnlyList<StudioPersistedRevision> TrimRevisions(
        IReadOnlyList<StudioPersistedRevision> revisions,
        int maxTotal = MaxRevisions,
        int maxPinnedCommitted = MaxPinnedCommittedRevisions)
    {
        ArgumentNullException.ThrowIfNull(revisions);
        if (revisions.Count <= maxTotal)
        {
            return revisions.ToList();
        }

        var keepIndices = new HashSet<int>();
        // Always keep the latest revision
        keepIndices.Add(revisions.Count - 1);

        // Pin up to maxPinnedCommitted most-recent committed revisions
        int pinnedCount = 0;
        for (int i = revisions.Count - 1; i >= 0 && pinnedCount < maxPinnedCommitted; i--)
        {
            if (revisions[i].IsCommitted)
            {
                keepIndices.Add(i);
                pinnedCount++;
            }
        }

        // Fill remaining slots with most recent revisions (FIFO eviction of older transient turns)
        for (int i = revisions.Count - 1; i >= 0 && keepIndices.Count < maxTotal; i--)
        {
            keepIndices.Add(i);
        }

        var result = new List<StudioPersistedRevision>(keepIndices.Count);
        for (int i = 0; i < revisions.Count; i++)
        {
            if (keepIndices.Contains(i))
            {
                result.Add(revisions[i]);
            }
        }

        return result;
    }

    public static IReadOnlyList<StudioPersistedRevision> MarkCommittedRevision(
        IReadOnlyList<StudioPersistedRevision> existingRevisions,
        string committedManifestJson,
        string source = "manual",
        string? changeSummary = null,
        string? committedLogicJs = null,
        CustomWidgetLocaleCatalog? committedLocales = null)
    {
        ArgumentNullException.ThrowIfNull(existingRevisions);
        CustomWidgetLocaleCatalog? normalizedLocales = committedLocales is { HasAnyLocale: true }
            ? committedLocales
            : null;
        string sha = ComputeManifestSha256(committedManifestJson, committedLogicJs, normalizedLocales);
        string semanticSha = ComputeSemanticBundleSha256(committedManifestJson, committedLogicJs, normalizedLocales);
        var list = existingRevisions.ToList();

        if (list.Count > 0)
        {
            StudioPersistedRevision last = list[^1];
            CustomWidgetLocaleCatalog? lastLocales = last.Locales is { HasAnyLocale: true } ? last.Locales : null;
            bool isSameSemanticBundle =
                string.Equals(last.ManifestSha256, sha, StringComparison.Ordinal) ||
                string.Equals(
                    ComputeSemanticBundleSha256(last.ManifestJson, last.LogicJs, lastLocales),
                    semanticSha,
                    StringComparison.Ordinal);
            if (isSameSemanticBundle)
            {
                list[^1] = last with
                {
                    IsCommitted = true,
                    ManifestSha256 = sha,
                    ManifestJson = committedManifestJson,
                    LogicJs = string.IsNullOrWhiteSpace(committedLogicJs) ? null : committedLogicJs,
                    Locales = normalizedLocales,
                    ChangeSummary = string.IsNullOrWhiteSpace(changeSummary) ? last.ChangeSummary : changeSummary!
                };
                return TrimRevisions(list);
            }
        }

        string nextId = $"rev-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
        StudioPersistedRevision? previous = list.Count > 0 ? list[^1] : null;
        var nextRevision = new StudioPersistedRevision
        {
            RevisionId = nextId,
            CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
            Source = source,
            ChangeSummary = changeSummary ?? string.Empty,
            ManifestSha256 = sha,
            IsCommitted = true,
            ManifestJson = committedManifestJson,
            LogicJs = string.IsNullOrWhiteSpace(committedLogicJs) ? null : committedLogicJs,
            Locales = normalizedLocales
        };
        list.Add(ShareUnchangedArtifacts(nextRevision, previous));

        return TrimRevisions(list);
    }

    public static IReadOnlyList<StudioChatMessage> TrimChatMessages(
        IReadOnlyList<StudioChatMessage> messages,
        int maxMessages = MaxChatMessages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count <= maxMessages)
        {
            return messages.ToList();
        }

        return messages.Skip(messages.Count - maxMessages).ToList();
    }

    public StudioLoadedSession LoadOrReconcileWidgetSession(
        string widgetId,
        string? currentDiskManifestJson,
        string? externalEditSummaryLabel = null,
        string? currentDiskLogicJs = null,
        CustomWidgetLocaleCatalog? currentDiskLocales = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        string dir = GetWidgetDirectory(widgetId);
        string briefPath = Path.Combine(dir, BriefFileName);
        string revisionsPath = Path.Combine(dir, RevisionsFileName);
        string chatPath = Path.Combine(dir, ChatFileName);
        string logicPath = Path.Combine(dir, CustomWidgetDataStore.LogicScriptFileName);
        string localesDir = Path.Combine(dir, CustomWidgetDataStore.LocalesDirectoryName);

        string? effectiveDiskLogicJs = currentDiskLogicJs;
        if (effectiveDiskLogicJs is null && File.Exists(logicPath))
        {
            effectiveDiskLogicJs = SafeReadAllText(logicPath);
        }
        if (string.IsNullOrWhiteSpace(effectiveDiskLogicJs))
        {
            effectiveDiskLogicJs = null;
        }

        CustomWidgetLocaleCatalog? effectiveDiskLocales = currentDiskLocales;
        if (effectiveDiskLocales is null && Directory.Exists(localesDir))
        {
            effectiveDiskLocales = CustomWidgetLocaleCatalog.LoadFromDirectory(localesDir);
        }
        if (effectiveDiskLocales is { HasAnyLocale: false })
        {
            effectiveDiskLocales = null;
        }

        string briefMarkdown = File.Exists(briefPath)
            ? ClampUtf8Length(SafeReadAllText(briefPath), MaxBriefBytes)
            : string.Empty;
        CustomWidgetDesignBrief parsedBrief = ParseBriefMarkdown(briefMarkdown);
        List<StudioPersistedRevision> revisions = File.Exists(revisionsPath)
            ? ParseRevisionsJson(SafeReadAllText(revisionsPath))
            : [];
        List<StudioChatMessage> chatMessages = File.Exists(chatPath)
            ? ParseChatMessagesJson(SafeReadAllText(chatPath))
            : [];
        StudioDraftSnapshot? pendingDraft = TryLoadWidgetDraft(widgetId);

        bool wasReconciled = false;
        if (!string.IsNullOrWhiteSpace(currentDiskManifestJson))
        {
            string diskSha = ComputeManifestSha256(
                currentDiskManifestJson,
                effectiveDiskLogicJs,
                effectiveDiskLocales);
            if (revisions.Count == 0)
            {
                revisions.Add(new StudioPersistedRevision
                {
                    RevisionId = "rev-initial",
                    CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                    Source = "template",
                    ChangeSummary = string.Empty,
                    ManifestSha256 = diskSha,
                    IsCommitted = true,
                    ManifestJson = currentDiskManifestJson,
                    LogicJs = effectiveDiskLogicJs,
                    Locales = effectiveDiskLocales
                });
            }
            else if (!string.Equals(revisions[^1].ManifestSha256, diskSha, StringComparison.Ordinal))
            {
                StudioPersistedRevision last = revisions[^1];
                CustomWidgetLocaleCatalog? lastLocales = last.Locales is { HasAnyLocale: true } ? last.Locales : null;
                bool isSameSemanticBundle = string.Equals(
                    ComputeSemanticBundleSha256(last.ManifestJson, last.LogicJs, lastLocales),
                    ComputeSemanticBundleSha256(currentDiskManifestJson, effectiveDiskLogicJs, effectiveDiskLocales),
                    StringComparison.Ordinal);
                if (isSameSemanticBundle)
                {
                    revisions[^1] = last with
                    {
                        IsCommitted = true,
                        ManifestSha256 = diskSha,
                        ManifestJson = currentDiskManifestJson,
                        LogicJs = effectiveDiskLogicJs,
                        Locales = effectiveDiskLocales
                    };
                    Directory.CreateDirectory(dir);
                    CustomWidgetStorageFileOps.ReplaceTextFileAtomically(revisionsPath, SerializeRevisionsJson(revisions));
                }
                else
                {
                    wasReconciled = true;
                    StudioPersistedRevision externalRevision = new StudioPersistedRevision
                    {
                        RevisionId = $"rev-ext-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}",
                        CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                        Source = "external",
                        ChangeSummary = string.IsNullOrWhiteSpace(externalEditSummaryLabel)
                            ? "[外部修改]"
                            : externalEditSummaryLabel!,
                        ManifestSha256 = diskSha,
                        IsCommitted = true,
                        ManifestJson = currentDiskManifestJson,
                        LogicJs = effectiveDiskLogicJs,
                        Locales = effectiveDiskLocales
                    };
                    revisions.Add(ShareUnchangedArtifacts(externalRevision, revisions.Count > 0 ? revisions[^1] : null));
                    IReadOnlyList<StudioPersistedRevision> trimmed = TrimRevisions(revisions);
                    revisions = trimmed.ToList();
                    Directory.CreateDirectory(dir);
                    CustomWidgetStorageFileOps.ReplaceTextFileAtomically(revisionsPath, SerializeRevisionsJson(revisions));
                }
            }
        }

        return new StudioLoadedSession(
            briefMarkdown,
            parsedBrief,
            TrimRevisions(revisions),
            TrimChatMessages(chatMessages),
            pendingDraft,
            wasReconciled);
    }

    public void SaveCommittedWidgetSession(
        string widgetId,
        string? briefMarkdown,
        IReadOnlyList<StudioPersistedRevision> revisions,
        IReadOnlyList<StudioChatMessage> chatMessages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        ArgumentNullException.ThrowIfNull(revisions);
        ArgumentNullException.ThrowIfNull(chatMessages);

        string dir = GetWidgetDirectory(widgetId);
        Directory.CreateDirectory(dir);

        string briefPath = Path.Combine(dir, BriefFileName);
        string cleanedBrief = ClampUtf8Length(briefMarkdown?.Trim() ?? string.Empty, MaxBriefBytes);
        if (cleanedBrief.Length > 0)
        {
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(briefPath, cleanedBrief);
        }
        else
        {
            CustomWidgetStorageFileOps.DeleteFileIfExists(briefPath);
        }

        IReadOnlyList<StudioPersistedRevision> trimmedRevisions = TrimRevisions(revisions);
        CustomWidgetStorageFileOps.ReplaceTextFileAtomically(
            Path.Combine(dir, RevisionsFileName),
            SerializeRevisionsJson(trimmedRevisions));

        IReadOnlyList<StudioChatMessage> trimmedChat = TrimChatMessages(chatMessages);
        if (trimmedChat.Count > 0)
        {
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(
                Path.Combine(dir, ChatFileName),
                SerializeChatMessagesJson(trimmedChat));
        }
        else
        {
            CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, ChatFileName));
        }

        // Committed session supersedes any uncommitted recovery draft
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, DraftFileName));
    }

    public void PromoteCreateSessionToWidget(
        string newWidgetId,
        string? briefMarkdown,
        IReadOnlyList<StudioPersistedRevision> revisions,
        IReadOnlyList<StudioChatMessage> chatMessages)
    {
        SaveCommittedWidgetSession(newWidgetId, briefMarkdown, revisions, chatMessages);
        DiscardCreateDraft();
    }

    public void SaveWidgetDraft(string widgetId, StudioDraftSnapshot draft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        ArgumentNullException.ThrowIfNull(draft);

        string dir = GetWidgetDirectory(widgetId);
        Directory.CreateDirectory(dir);
        CustomWidgetStorageFileOps.ReplaceTextFileAtomically(
            Path.Combine(dir, DraftFileName),
            SerializeDraftJson(draft with { TargetWidgetId = widgetId }));
    }

    public StudioDraftSnapshot? TryLoadWidgetDraft(string widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return null;
        }

        string path = Path.Combine(GetWidgetDirectory(widgetId), DraftFileName);
        return File.Exists(path) ? ParseDraftJson(SafeReadAllText(path)) : null;
    }

    public void DiscardWidgetDraft(string widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return;
        }

        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(GetWidgetDirectory(widgetId), DraftFileName));
    }

    public void SaveCreateDraft(StudioDraftSnapshot draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        string dir = GetCreateDraftDirectory();
        Directory.CreateDirectory(dir);
        CustomWidgetStorageFileOps.ReplaceTextFileAtomically(
            Path.Combine(dir, DraftFileName),
            SerializeDraftJson(draft with { TargetWidgetId = null }));
    }

    public StudioDraftSnapshot? TryLoadCreateDraft()
    {
        string path = Path.Combine(GetCreateDraftDirectory(), DraftFileName);
        return File.Exists(path) ? ParseDraftJson(SafeReadAllText(path)) : null;
    }

    public void DiscardCreateDraft()
    {
        string dir = GetCreateDraftDirectory();
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, DraftFileName));
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, BriefFileName));
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, RevisionsFileName));
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, ChatFileName));
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, CustomWidgetDataStore.ManifestFileName));
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, CustomWidgetDataStore.LogicScriptFileName));
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, "manifest.schema.json"));
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, "SKILL.md"));
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, "AGENTS.md"));
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, "CLAUDE.md"));
        try
        {
            string draftLocalesDir = Path.Combine(dir, CustomWidgetDataStore.LocalesDirectoryName);
            if (Directory.Exists(draftLocalesDir))
            {
                CustomWidgetStorageFileOps.DeleteDirectoryRecursive(draftLocalesDir);
            }
            CustomWidgetStorageFileOps.DeleteEmptyDirectoryIfExists(dir);
        }
        catch
        {
        }
    }

    public void ClearWidgetCoCreationHistory(string widgetId)
    {
        if (string.IsNullOrWhiteSpace(widgetId))
        {
            return;
        }

        string dir = GetWidgetDirectory(widgetId);
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, BriefFileName));
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, RevisionsFileName));
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, ChatFileName));
        CustomWidgetStorageFileOps.DeleteFileIfExists(Path.Combine(dir, DraftFileName));
    }

    public static string SerializeRevisionsJson(IReadOnlyList<StudioPersistedRevision> revisions)
    {
        Dictionary<string, IReadOnlyDictionary<string, string>> blobs = CollectLocaleBlobs(revisions);
        Dictionary<string, string> scriptBlobs = CollectScriptBlobs(revisions);
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            if (blobs.Count > 0)
            {
                WriteLocaleBlobsObject(writer, blobs);
            }
            if (scriptBlobs.Count > 0)
            {
                WriteScriptBlobsObject(writer, scriptBlobs);
            }
            writer.WritePropertyName("revisions");
            WriteRevisionsArray(writer, revisions);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static List<StudioPersistedRevision> ParseRevisionsJson(string? rawJson)
    {
        var result = new List<StudioPersistedRevision>();
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return result;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson, PermissiveJsonOptions);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                Dictionary<string, IReadOnlyDictionary<string, string>> blobs = ReadLocaleBlobsObject(doc.RootElement);
                Dictionary<string, string> scriptBlobs = ReadScriptBlobsObject(doc.RootElement);
                if (doc.RootElement.TryGetProperty("revisions", out JsonElement arr) &&
                    arr.ValueKind == JsonValueKind.Array)
                {
                    ReadRevisionsArray(arr, blobs, scriptBlobs, result);
                }
            }
        }
        catch
        {
        }

        return result;
    }

    public static string SerializeChatMessagesJson(IReadOnlyList<StudioChatMessage> messages)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WritePropertyName("messages");
            WriteChatMessagesArray(writer, messages);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static List<StudioChatMessage> ParseChatMessagesJson(string? rawJson)
    {
        var result = new List<StudioChatMessage>();
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return result;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson, PermissiveJsonOptions);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("messages", out JsonElement arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                ReadChatMessagesArray(arr, result);
            }
        }
        catch
        {
        }

        return result;
    }

    public static string SerializeDraftJson(StudioDraftSnapshot draft)
    {
        IReadOnlyList<StudioPersistedRevision> trimmedRevisions = TrimRevisions(draft.Revisions);
        Dictionary<string, IReadOnlyDictionary<string, string>> blobs = CollectLocaleBlobs(trimmedRevisions);
        Dictionary<string, string> scriptBlobs = CollectScriptBlobs(trimmedRevisions);

        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 2);
            writer.WriteNumber("updatedAtUtcTicks", draft.UpdatedAtUtcTicks);
            if (!string.IsNullOrWhiteSpace(draft.TargetWidgetId))
            {
                writer.WriteString("targetWidgetId", draft.TargetWidgetId);
            }
            writer.WriteBoolean("isExternalImportSession", draft.IsExternalImportSession);
            writer.WriteString("selectedEmoji", draft.SelectedEmoji);
            writer.WriteString("startDraftIdea", draft.StartDraftIdea);
            writer.WriteString("refineDraftIdea", draft.RefineDraftIdea);
            writer.WriteNumber("selectedRevisionIndex", draft.SelectedRevisionIndex);
            writer.WriteString("briefMarkdown", ClampUtf8Length(draft.BriefMarkdown, MaxBriefBytes));
            if (!string.IsNullOrWhiteSpace(draft.PreviewSampleDataJson))
            {
                writer.WriteString("previewSampleDataJson", draft.PreviewSampleDataJson);
            }
            if (draft.PinnedCapabilityIds.Count > 0)
            {
                writer.WritePropertyName("pinnedCapabilityIds");
                writer.WriteStartArray();
                foreach (string capId in draft.PinnedCapabilityIds)
                {
                    if (!string.IsNullOrWhiteSpace(capId))
                    {
                        writer.WriteStringValue(capId);
                    }
                }
                writer.WriteEndArray();
            }
            if (draft.LoadedCapabilityPackIds.Count > 0)
            {
                writer.WritePropertyName("loadedCapabilityPackIds");
                writer.WriteStartArray();
                foreach (string packId in draft.LoadedCapabilityPackIds)
                {
                    if (!string.IsNullOrWhiteSpace(packId))
                    {
                        writer.WriteStringValue(packId);
                    }
                }
                writer.WriteEndArray();
            }
            if (blobs.Count > 0)
            {
                WriteLocaleBlobsObject(writer, blobs);
            }
            if (scriptBlobs.Count > 0)
            {
                WriteScriptBlobsObject(writer, scriptBlobs);
            }
            writer.WritePropertyName("revisions");
            WriteRevisionsArray(writer, trimmedRevisions);
            writer.WritePropertyName("messages");
            WriteChatMessagesArray(writer, TrimChatMessages(draft.ChatMessages));
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static StudioDraftSnapshot? ParseDraftJson(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(rawJson, PermissiveJsonOptions);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            Dictionary<string, IReadOnlyDictionary<string, string>> blobs = ReadLocaleBlobsObject(root);
            Dictionary<string, string> scriptBlobs = ReadScriptBlobsObject(root);
            var revisions = new List<StudioPersistedRevision>();
            if (root.TryGetProperty("revisions", out JsonElement revArr) && revArr.ValueKind == JsonValueKind.Array)
            {
                ReadRevisionsArray(revArr, blobs, scriptBlobs, revisions);
            }

            var messages = new List<StudioChatMessage>();
            if (root.TryGetProperty("messages", out JsonElement msgArr) && msgArr.ValueKind == JsonValueKind.Array)
            {
                ReadChatMessagesArray(msgArr, messages);
            }

            if (revisions.Count == 0 && messages.Count == 0 &&
                string.IsNullOrWhiteSpace(GetStringOrDefault(root, "startDraftIdea")))
            {
                return null;
            }

            return new StudioDraftSnapshot
            {
                UpdatedAtUtcTicks = GetInt64OrDefault(root, "updatedAtUtcTicks", DateTimeOffset.UtcNow.UtcTicks),
                TargetWidgetId = GetOptionalString(root, "targetWidgetId"),
                IsExternalImportSession = GetBoolOrDefault(root, "isExternalImportSession"),
                SelectedEmoji = GetStringOrDefault(root, "selectedEmoji"),
                StartDraftIdea = GetStringOrDefault(root, "startDraftIdea"),
                RefineDraftIdea = GetStringOrDefault(root, "refineDraftIdea"),
                SelectedRevisionIndex = Math.Clamp(
                    GetInt32OrDefault(root, "selectedRevisionIndex", Math.Max(0, revisions.Count - 1)),
                    0,
                    Math.Max(0, revisions.Count - 1)),
                BriefMarkdown = ClampUtf8Length(GetStringOrDefault(root, "briefMarkdown"), MaxBriefBytes),
                PreviewSampleDataJson = GetOptionalString(root, "previewSampleDataJson"),
                PinnedCapabilityIds = ReadStringArray(root, "pinnedCapabilityIds"),
                LoadedCapabilityPackIds = ReadStringArray(root, "loadedCapabilityPackIds"),
                Revisions = revisions,
                ChatMessages = messages
            };
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> CollectLocaleBlobs(
        IReadOnlyList<StudioPersistedRevision> revisions)
    {
        var blobs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (StudioPersistedRevision rev in revisions)
        {
            if (rev.Locales is not { HasAnyLocale: true })
            {
                continue;
            }

            foreach (KeyValuePair<string, IReadOnlyDictionary<string, string>> kv in rev.Locales.Locales)
            {
                if (kv.Value.Count == 0 || !CustomWidgetLocaleCatalog.IsValidCultureCode(kv.Key))
                {
                    continue;
                }

                string hash16 = CustomWidgetLocaleCatalog.ComputeLocaleDictionaryHash16(kv.Value);
                blobs.TryAdd(hash16, kv.Value);
            }
        }

        return blobs;
    }

    private static void WriteLocaleBlobsObject(
        Utf8JsonWriter writer,
        Dictionary<string, IReadOnlyDictionary<string, string>> blobs)
    {
        writer.WritePropertyName("localeBlobs");
        writer.WriteStartObject();
        foreach (KeyValuePair<string, IReadOnlyDictionary<string, string>> blob in
                 blobs.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            writer.WritePropertyName(blob.Key);
            writer.WriteStartObject();
            foreach (KeyValuePair<string, string> entry in
                     blob.Value.OrderBy(static p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteString(entry.Key, entry.Value);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static Dictionary<string, IReadOnlyDictionary<string, string>> ReadLocaleBlobsObject(JsonElement root)
    {
        var blobs = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (!root.TryGetProperty("localeBlobs", out JsonElement blobsEl) ||
            blobsEl.ValueKind != JsonValueKind.Object)
        {
            return blobs;
        }

        foreach (JsonProperty blobProp in blobsEl.EnumerateObject())
        {
            if (blobProp.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            IReadOnlyDictionary<string, string> entries =
                CustomWidgetLocaleCatalog.ParseSingleLocaleElement(blobProp.Value);
            if (entries.Count > 0)
            {
                blobs[blobProp.Name] = entries;
            }
        }

        return blobs;
    }

    public static StudioPersistedRevision ShareUnchangedArtifacts(
        StudioPersistedRevision current,
        StudioPersistedRevision? previous)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (previous is null)
        {
            return current;
        }

        string manifestJson = string.Equals(current.ManifestJson, previous.ManifestJson, StringComparison.Ordinal)
            ? previous.ManifestJson
            : current.ManifestJson;
        string? logicJs = current.LogicJs is not null &&
            previous.LogicJs is not null &&
            string.Equals(current.LogicJs, previous.LogicJs, StringComparison.Ordinal)
            ? previous.LogicJs
            : current.LogicJs;
        CustomWidgetLocaleCatalog? locales = AreLocaleCatalogsEqual(current.Locales, previous.Locales)
            ? previous.Locales
            : current.Locales;

        if (ReferenceEquals(manifestJson, current.ManifestJson) &&
            ReferenceEquals(logicJs, current.LogicJs) &&
            ReferenceEquals(locales, current.Locales))
        {
            return current;
        }

        return current with
        {
            ManifestJson = manifestJson,
            LogicJs = logicJs,
            Locales = locales
        };
    }

    private static bool AreLocaleCatalogsEqual(CustomWidgetLocaleCatalog? a, CustomWidgetLocaleCatalog? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null || a.Locales.Count != b.Locales.Count)
        {
            return false;
        }

        if (!AreLocaleMetasEqual(a.Meta, b.Meta))
        {
            return false;
        }

        foreach (KeyValuePair<string, IReadOnlyDictionary<string, string>> kv in a.Locales)
        {
            if (!b.Locales.TryGetValue(kv.Key, out IReadOnlyDictionary<string, string>? bDict) ||
                kv.Value.Count != bDict.Count)
            {
                return false;
            }

            foreach (KeyValuePair<string, string> entry in kv.Value)
            {
                if (!bDict.TryGetValue(entry.Key, out string? bValue) ||
                    !string.Equals(entry.Value, bValue, StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool AreLocaleMetasEqual(CustomWidgetLocaleMeta? a, CustomWidgetLocaleMeta? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null ||
            a.SchemaVersion != b.SchemaVersion ||
            !string.Equals(a.BaseLocale, b.BaseLocale, StringComparison.OrdinalIgnoreCase) ||
            a.Hashes.Count != b.Hashes.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, string> kv in a.Hashes)
        {
            if (!b.Hashes.TryGetValue(kv.Key, out string? bHash) ||
                !string.Equals(kv.Value, bHash, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    internal static string ComputeScriptHash16(string script)
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(script));
        return Convert.ToHexStringLower(hash)[..16];
    }

    private static Dictionary<string, string> CollectScriptBlobs(IReadOnlyList<StudioPersistedRevision> revisions)
    {
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (StudioPersistedRevision rev in revisions)
        {
            if (string.IsNullOrWhiteSpace(rev.LogicJs))
            {
                continue;
            }

            string hash16 = ComputeScriptHash16(rev.LogicJs);
            blobs.TryAdd(hash16, rev.LogicJs);
        }

        return blobs;
    }

    private static void WriteScriptBlobsObject(Utf8JsonWriter writer, Dictionary<string, string> blobs)
    {
        writer.WritePropertyName("scriptBlobs");
        writer.WriteStartObject();
        foreach (KeyValuePair<string, string> blob in blobs.OrderBy(static p => p.Key, StringComparer.Ordinal))
        {
            writer.WriteString(blob.Key, blob.Value);
        }
        writer.WriteEndObject();
    }

    private static Dictionary<string, string> ReadScriptBlobsObject(JsonElement root)
    {
        var blobs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("scriptBlobs", out JsonElement blobsEl) ||
            blobsEl.ValueKind != JsonValueKind.Object)
        {
            return blobs;
        }

        foreach (JsonProperty blobProp in blobsEl.EnumerateObject())
        {
            if (blobProp.Value.ValueKind == JsonValueKind.String)
            {
                string? script = blobProp.Value.GetString();
                if (!string.IsNullOrWhiteSpace(script))
                {
                    blobs[blobProp.Name] = script;
                }
            }
        }

        return blobs;
    }

    private static void WriteRevisionsArray(Utf8JsonWriter writer, IReadOnlyList<StudioPersistedRevision> revisions)
    {
        writer.WriteStartArray();
        foreach (StudioPersistedRevision rev in revisions)
        {
            writer.WriteStartObject();
            writer.WriteString("revisionId", rev.RevisionId);
            writer.WriteNumber("createdAtUtcTicks", rev.CreatedAtUtcTicks);
            writer.WriteString("source", rev.Source);
            writer.WriteString("changeSummary", rev.ChangeSummary);
            writer.WriteString("manifestSha256", rev.ManifestSha256);
            writer.WriteBoolean("isCommitted", rev.IsCommitted);
            if (!string.IsNullOrWhiteSpace(rev.CoalesceGroupKey))
            {
                writer.WriteString("coalesceGroupKey", rev.CoalesceGroupKey);
            }
            if (!string.IsNullOrWhiteSpace(rev.BriefMarkdown))
            {
                writer.WriteString("briefMarkdown", ClampUtf8Length(rev.BriefMarkdown, MaxBriefBytes));
            }
            writer.WriteString("manifestJson", rev.ManifestJson);
            if (!string.IsNullOrWhiteSpace(rev.LogicJs))
            {
                writer.WriteString("scriptRef", ComputeScriptHash16(rev.LogicJs));
            }

            if (rev.Locales is { HasAnyLocale: true })
            {
                writer.WritePropertyName("localeRefs");
                writer.WriteStartObject();
                foreach (KeyValuePair<string, IReadOnlyDictionary<string, string>> kv in
                         rev.Locales.Locales.OrderBy(static p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    if (kv.Value.Count == 0 || !CustomWidgetLocaleCatalog.IsValidCultureCode(kv.Key))
                    {
                        continue;
                    }

                    string hash16 = CustomWidgetLocaleCatalog.ComputeLocaleDictionaryHash16(kv.Value);
                    writer.WriteString(kv.Key, hash16);
                }
                writer.WriteEndObject();

                if (rev.Locales.Meta is not null)
                {
                    writer.WritePropertyName("localeMeta");
                    rev.Locales.Meta.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void ReadRevisionsArray(
        JsonElement arr,
        Dictionary<string, IReadOnlyDictionary<string, string>> blobs,
        Dictionary<string, string> scriptBlobs,
        List<StudioPersistedRevision> target)
    {
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string manifestJson = GetStringOrDefault(item, "manifestJson");
            if (string.IsNullOrWhiteSpace(manifestJson))
            {
                continue;
            }

            string? logicJs = null;
            string? scriptRef = GetOptionalString(item, "scriptRef");
            if (!string.IsNullOrWhiteSpace(scriptRef) &&
                scriptBlobs.TryGetValue(scriptRef, out string? blobScript) &&
                !string.IsNullOrWhiteSpace(blobScript))
            {
                logicJs = blobScript;
            }

            CustomWidgetLocaleCatalog? locales = ReadRevisionLocales(item, blobs);

            string sha = GetStringOrDefault(item, "manifestSha256");
            if (string.IsNullOrWhiteSpace(sha))
            {
                sha = ComputeManifestSha256(manifestJson, logicJs, locales);
            }

            var revision = new StudioPersistedRevision
            {
                RevisionId = GetOptionalString(item, "revisionId") ?? $"rev-{target.Count + 1}",
                CreatedAtUtcTicks = GetInt64OrDefault(item, "createdAtUtcTicks", DateTimeOffset.UtcNow.UtcTicks),
                Source = GetOptionalString(item, "source") ?? "ai",
                ChangeSummary = GetStringOrDefault(item, "changeSummary"),
                ManifestSha256 = sha,
                IsCommitted = GetBoolOrDefault(item, "isCommitted"),
                ManifestJson = manifestJson,
                LogicJs = logicJs,
                Locales = locales,
                CoalesceGroupKey = GetOptionalString(item, "coalesceGroupKey"),
                BriefMarkdown = GetOptionalString(item, "briefMarkdown")
            };
            target.Add(ShareUnchangedArtifacts(revision, target.Count > 0 ? target[^1] : null));
        }
    }

    private static CustomWidgetLocaleCatalog? ReadRevisionLocales(
        JsonElement item,
        Dictionary<string, IReadOnlyDictionary<string, string>> blobs)
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("localeRefs", out JsonElement refsEl) &&
            refsEl.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty refProp in refsEl.EnumerateObject())
            {
                if (!CustomWidgetLocaleCatalog.IsValidCultureCode(refProp.Name) ||
                    refProp.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                string? hash16 = refProp.Value.GetString();
                if (!string.IsNullOrWhiteSpace(hash16) &&
                    blobs.TryGetValue(hash16, out IReadOnlyDictionary<string, string>? dict) &&
                    dict.Count > 0)
                {
                    map[refProp.Name] = dict;
                }
            }
        }

        CustomWidgetLocaleMeta? meta = null;
        if (item.TryGetProperty("localeMeta", out JsonElement metaEl) &&
            metaEl.ValueKind == JsonValueKind.Object)
        {
            meta = CustomWidgetLocaleMeta.ParseElement(metaEl);
        }

        return map.Count > 0 || meta is not null
            ? new CustomWidgetLocaleCatalog(map, meta)
            : null;
    }

    private static void WriteChatMessagesArray(Utf8JsonWriter writer, IReadOnlyList<StudioChatMessage> messages)
    {
        writer.WriteStartArray();
        foreach (StudioChatMessage msg in messages)
        {
            writer.WriteStartObject();
            writer.WriteString("id", msg.Id);
            writer.WriteNumber("createdAtUtcTicks", msg.CreatedAtUtcTicks);
            writer.WriteString("role", msg.Role);
            writer.WriteString("kind", msg.Kind);
            writer.WriteString("text", msg.Text);
            if (!string.IsNullOrWhiteSpace(msg.LinkedRevisionId))
            {
                writer.WriteString("linkedRevisionId", msg.LinkedRevisionId);
            }
            if (!string.IsNullOrWhiteSpace(msg.TargetNodePath))
            {
                writer.WriteString("targetNodePath", msg.TargetNodePath);
            }
            if (!string.IsNullOrWhiteSpace(msg.TargetNodeLabel))
            {
                writer.WriteString("targetNodeLabel", msg.TargetNodeLabel);
            }
            if (msg.DiffPills.Count > 0)
            {
                writer.WritePropertyName("diffPills");
                writer.WriteStartArray();
                foreach (string pill in msg.DiffPills)
                {
                    writer.WriteStringValue(pill);
                }
                writer.WriteEndArray();
            }
            if (msg.IsSuperseded)
            {
                writer.WriteBoolean("isSuperseded", true);
            }
            if (!string.IsNullOrWhiteSpace(msg.ExecutionSummary))
            {
                writer.WriteString("executionSummary", msg.ExecutionSummary);
            }
            if (!string.IsNullOrWhiteSpace(msg.ThinkingText))
            {
                writer.WriteString("thinkingText", msg.ThinkingText);
            }
            if (msg.ExecutionSteps.Count > 0)
            {
                writer.WritePropertyName("executionSteps");
                writer.WriteStartArray();
                foreach (StudioStreamStepItem step in msg.ExecutionSteps)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", step.Id);
                    writer.WriteString("state", step.State.ToString());
                    writer.WriteString("label", step.Label);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (msg.Suggestions.Count > 0)
            {
                writer.WritePropertyName("suggestions");
                writer.WriteStartArray();
                foreach (string s in msg.Suggestions)
                {
                    writer.WriteStringValue(s);
                }
                writer.WriteEndArray();
            }
            if (msg.ActionChips.Count > 0)
            {
                writer.WritePropertyName("actionChips");
                writer.WriteStartArray();
                foreach (StudioActionChip chip in msg.ActionChips)
                {
                    writer.WriteStartObject();
                    writer.WriteString("label", chip.Label);
                    writer.WriteString("prompt", chip.Prompt);
                    if (!string.IsNullOrWhiteSpace(chip.Kind))
                    {
                        writer.WriteString("kind", chip.Kind);
                    }
                    if (!string.IsNullOrWhiteSpace(chip.TargetProperty))
                    {
                        writer.WriteString("targetProperty", chip.TargetProperty);
                    }
                    if (!string.IsNullOrWhiteSpace(chip.TargetValue))
                    {
                        writer.WriteString("targetValue", chip.TargetValue);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (msg.ClarifyQuestions.Count > 0)
            {
                writer.WritePropertyName("clarifyQuestions");
                writer.WriteStartArray();
                foreach (StudioClarifyQuestionSpec q in msg.ClarifyQuestions)
                {
                    writer.WriteStartObject();
                    writer.WriteString("id", q.Id);
                    writer.WriteString("header", q.Header);
                    if (q.MultiSelect)
                    {
                        writer.WriteBoolean("multiSelect", true);
                    }
                    if (!q.AllowCustomInput)
                    {
                        writer.WriteBoolean("allowCustomInput", false);
                    }
                    if (!string.IsNullOrWhiteSpace(q.Placeholder))
                    {
                        writer.WriteString("placeholder", q.Placeholder);
                    }
                    writer.WritePropertyName("options");
                    writer.WriteStartArray();
                    foreach (StudioClarifyOptionSpec opt in q.Options)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("value", opt.Value);
                        writer.WriteString("label", opt.Label);
                        if (opt.Recommended)
                        {
                            writer.WriteBoolean("recommended", true);
                        }
                        writer.WriteEndObject();
                    }
                    writer.WriteEndArray();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (!string.IsNullOrWhiteSpace(msg.SubmittedSummary))
            {
                writer.WriteString("submittedSummary", msg.SubmittedSummary);
            }
            if (!string.IsNullOrWhiteSpace(msg.LimitationNote))
            {
                writer.WriteString("limitationNote", msg.LimitationNote);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void ReadChatMessagesArray(JsonElement arr, List<StudioChatMessage> target)
    {
        foreach (JsonElement item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string text = GetStringOrDefault(item, "text");
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            target.Add(new StudioChatMessage
            {
                Id = GetOptionalString(item, "id") ?? $"msg-{target.Count + 1}",
                CreatedAtUtcTicks = GetInt64OrDefault(item, "createdAtUtcTicks", DateTimeOffset.UtcNow.UtcTicks),
                Role = GetOptionalString(item, "role") ?? "user",
                Kind = GetOptionalString(item, "kind") ?? "prompt",
                Text = text,
                LinkedRevisionId = GetOptionalString(item, "linkedRevisionId"),
                IsSuperseded = GetBoolOrDefault(item, "isSuperseded"),
                TargetNodePath = GetOptionalString(item, "targetNodePath"),
                TargetNodeLabel = GetOptionalString(item, "targetNodeLabel"),
                ExecutionSummary = GetOptionalString(item, "executionSummary"),
                ThinkingText = GetOptionalString(item, "thinkingText"),
                ExecutionSteps = ReadExecutionStepsArray(item),
                DiffPills = ReadStringArray(item, "diffPills"),
                Suggestions = ReadStringArray(item, "suggestions"),
                ActionChips = ReadActionChipsArray(item),
                ClarifyQuestions = ParseClarifyQuestionsFromObject(item, maxQuestions: 4),
                SubmittedSummary = GetOptionalString(item, "submittedSummary"),
                LimitationNote = GetOptionalString(item, "limitationNote")
            });
        }
    }

    public static List<StudioClarifyQuestionSpec> ParseClarifyQuestionsFromObject(JsonElement obj, int maxQuestions = 3)
    {
        var list = new List<StudioClarifyQuestionSpec>();
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return list;
        }

        JsonElement arr = default;
        if ((!obj.TryGetProperty("questions", out arr) || arr.ValueKind != JsonValueKind.Array) &&
            (!obj.TryGetProperty("clarifyQuestions", out arr) || arr.ValueKind != JsonValueKind.Array))
        {
            return list;
        }

        int qIdx = 0;
        foreach (JsonElement qEl in arr.EnumerateArray())
        {
            if (list.Count >= maxQuestions)
            {
                break;
            }

            if (qEl.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string header =
                GetOptionalString(qEl, "header") ??
                GetOptionalString(qEl, "question") ??
                GetOptionalString(qEl, "title") ??
                GetOptionalString(qEl, "label") ??
                string.Empty;
            header = TruncateLine(header, 48);
            if (string.IsNullOrWhiteSpace(header))
            {
                continue;
            }

            JsonElement optsEl = default;
            if ((!qEl.TryGetProperty("options", out optsEl) || optsEl.ValueKind != JsonValueKind.Array) &&
                (!qEl.TryGetProperty("choices", out optsEl) || optsEl.ValueKind != JsonValueKind.Array))
            {
                continue;
            }

            var options = new List<StudioClarifyOptionSpec>(4);
            bool anyRecommended = false;
            foreach (JsonElement optEl in optsEl.EnumerateArray())
            {
                if (options.Count >= 5)
                {
                    break;
                }

                if (optEl.ValueKind == JsonValueKind.String)
                {
                    string rawOpt = TruncateLine(optEl.GetString() ?? string.Empty, 32);
                    if (!string.IsNullOrWhiteSpace(rawOpt))
                    {
                        options.Add(new StudioClarifyOptionSpec(rawOpt, rawOpt, Recommended: false));
                    }
                }
                else if (optEl.ValueKind == JsonValueKind.Object)
                {
                    string label =
                        GetOptionalString(optEl, "label") ??
                        GetOptionalString(optEl, "title") ??
                        GetOptionalString(optEl, "text") ??
                        GetOptionalString(optEl, "value") ??
                        string.Empty;
                    label = TruncateLine(label, 32);
                    if (string.IsNullOrWhiteSpace(label))
                    {
                        continue;
                    }

                    string val = TruncateLine(GetOptionalString(optEl, "value") ?? label, 64);
                    bool rec = GetBoolOrDefault(optEl, "recommended") || GetBoolOrDefault(optEl, "default");
                    if (rec)
                    {
                        anyRecommended = true;
                    }

                    options.Add(new StudioClarifyOptionSpec(val, label, rec));
                }
            }

            if (options.Count == 0)
            {
                continue;
            }

            if (!anyRecommended)
            {
                options[0] = options[0] with { Recommended = true };
            }

            qIdx++;
            string id = GetOptionalString(qEl, "id")?.Trim() is { Length: > 0 } rawId
                ? rawId
                : $"q{qIdx}";
            bool multi = GetBoolOrDefault(qEl, "multiSelect") ||
                         GetBoolOrDefault(qEl, "multiple") ||
                         GetBoolOrDefault(qEl, "isMultiSelect");
            bool allowCustom = !qEl.TryGetProperty("allowCustomInput", out JsonElement acEl)
                ? (!qEl.TryGetProperty("allowOther", out JsonElement aoEl) || aoEl.ValueKind != JsonValueKind.False)
                : acEl.ValueKind != JsonValueKind.False;
            string? placeholder = GetOptionalString(qEl, "placeholder");

            list.Add(new StudioClarifyQuestionSpec(
                Id: id,
                Header: header,
                Options: options,
                MultiSelect: multi,
                AllowCustomInput: allowCustom,
                Placeholder: placeholder));
        }

        return list;
    }

    private static List<StudioActionChip> ReadActionChipsArray(JsonElement obj)
    {
        var list = new List<StudioActionChip>();
        if (!obj.TryGetProperty("actionChips", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (JsonElement el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            string? label = GetOptionalString(el, "label");
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            list.Add(new StudioActionChip(
                Label: label,
                Prompt: GetStringOrDefault(el, "prompt"),
                Kind: GetOptionalString(el, "kind") ?? "prompt",
                TargetProperty: GetOptionalString(el, "targetProperty"),
                TargetValue: GetOptionalString(el, "targetValue")));
        }

        return list;
    }

    private static List<StudioStreamStepItem> ReadExecutionStepsArray(JsonElement obj)
    {
        var list = new List<StudioStreamStepItem>();
        if (obj.TryGetProperty("executionSteps", out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in arr.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string id = GetStringOrDefault(el, "id");
                string label = GetStringOrDefault(el, "label");
                if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                string rawState = GetStringOrDefault(el, "state");
                StudioStreamStepState state = Enum.TryParse(rawState, ignoreCase: true, out StudioStreamStepState parsedState)
                    ? parsedState
                    : StudioStreamStepState.Completed;
                list.Add(new StudioStreamStepItem(id, state, label));
            }
        }
        return list;
    }

    private static List<string> ReadStringArray(JsonElement obj, string propName)
    {
        var list = new List<string>();
        if (obj.TryGetProperty(propName, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String && el.GetString() is { Length: > 0 } s)
                {
                    list.Add(s);
                }
            }
        }
        return list;
    }

    private static string? GetOptionalString(JsonElement obj, string propName) =>
        obj.TryGetProperty(propName, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    private static string GetStringOrDefault(JsonElement obj, string propName) =>
        GetOptionalString(obj, propName) ?? string.Empty;

    private static long GetInt64OrDefault(JsonElement obj, string propName, long fallback) =>
        obj.TryGetProperty(propName, out JsonElement el) && el.TryGetInt64(out long v) ? v : fallback;

    private static int GetInt32OrDefault(JsonElement obj, string propName, int fallback) =>
        obj.TryGetProperty(propName, out JsonElement el) && el.TryGetInt32(out int v) ? v : fallback;

    private static bool GetBoolOrDefault(JsonElement obj, string propName) =>
        obj.TryGetProperty(propName, out JsonElement el) &&
        (el.ValueKind == JsonValueKind.True ||
         (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out bool b) && b));

    private static string SafeReadAllText(string path)
    {
        try
        {
            return File.ReadAllText(path, Encoding.UTF8);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string SanitizeFolderName(string widgetId) =>
        CustomWidgetDataStore.GetSafeWidgetFileName(widgetId);

    private static string TrimBulletPrefix(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith("- ", StringComparison.Ordinal) ||
            s.StartsWith("* ", StringComparison.Ordinal) ||
            s.StartsWith("• ", StringComparison.Ordinal))
        {
            return s[2..].Trim();
        }
        return s;
    }

    private static void AddUniqueLimited(List<string> list, string item, int maxCount)
    {
        if (list.Count >= maxCount)
        {
            return;
        }
        if (!list.Any(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase)))
        {
            list.Add(TruncateLine(item, 120));
        }
    }

    private static void AddRecentUnique(List<string> list, string item, int maxCount)
    {
        list.RemoveAll(x => string.Equals(x, item, StringComparison.OrdinalIgnoreCase));
        list.Add(item);
        while (list.Count > maxCount)
        {
            list.RemoveAt(0);
        }
    }

    private static string TruncateLine(string text, int maxChars)
    {
        string singleLine = text.Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return singleLine.Length <= maxChars ? singleLine : singleLine[..maxChars].TrimEnd();
    }

    private static string ClampUtf8Length(string text, int maxBytes)
    {
        if (string.IsNullOrEmpty(text) || Encoding.UTF8.GetByteCount(text) <= maxBytes)
        {
            return text;
        }

        int len = Math.Min(text.Length, maxBytes);
        while (len > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, len)) > maxBytes)
        {
            len -= 32;
        }
        return len <= 0 ? string.Empty : text[..len].TrimEnd();
    }

    private static bool ContainsAnyIgnoreCase(string text, params string[] terms)
    {
        foreach (string term in terms)
        {
            if (text.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
