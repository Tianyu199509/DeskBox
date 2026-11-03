using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed record StudioBriefUpdateSpec
{
    public string? Purpose { get; init; }
    public IReadOnlyList<string> AddConfirmed { get; init; } = [];
    public IReadOnlyList<string> AddRejected { get; init; } = [];
    public IReadOnlyList<string> AddStylePreferences { get; init; } = [];

    public bool HasAnyChange =>
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
}

public sealed record StudioChatMessage
{
    public required string Id { get; init; }
    public long CreatedAtUtcTicks { get; init; } = DateTimeOffset.UtcNow.UtcTicks;
    public required string Role { get; init; }
    public required string Kind { get; init; }
    public required string Text { get; init; }
    public string? LinkedRevisionId { get; init; }
    public string? TargetNodePath { get; init; }
    public string? TargetNodeLabel { get; init; }
    public IReadOnlyList<string> DiffPills { get; init; } = [];
    public IReadOnlyList<string> Suggestions { get; init; } = [];
}

public sealed record StudioDraftSnapshot
{
    public long UpdatedAtUtcTicks { get; init; } = DateTimeOffset.UtcNow.UtcTicks;
    public string? TargetWidgetId { get; init; }
    public string SelectedEmoji { get; init; } = string.Empty;
    public string StartDraftIdea { get; init; } = string.Empty;
    public string RefineDraftIdea { get; init; } = string.Empty;
    public int SelectedRevisionIndex { get; init; }
    public string BriefMarkdown { get; init; } = string.Empty;
    public string? PreviewSampleDataJson { get; init; }
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
/// <item><c>revisions.json</c>: Bounded manifest revision snapshots (max 20, up to 8 pinned committed slots).</item>
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

    public static string ComputeManifestSha256(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return string.Empty;
        }

        string normalized = manifestJson.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexStringLower(hash);
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

        string purpose = !string.IsNullOrWhiteSpace(updates.Purpose)
            ? TruncateLine(updates.Purpose.Trim(), 240)
            : existing.Purpose;

        var confirmed = new List<string>(existing.ConfirmedRequirements);
        var rejected = new List<string>(existing.RejectedIdeas);
        var style = new List<string>(existing.StylePreferences);

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
        string? changeSummary = null)
    {
        ArgumentNullException.ThrowIfNull(existingRevisions);
        string sha = ComputeManifestSha256(committedManifestJson);
        var list = existingRevisions.ToList();

        if (list.Count > 0 && string.Equals(list[^1].ManifestSha256, sha, StringComparison.Ordinal))
        {
            StudioPersistedRevision last = list[^1];
            list[^1] = last with
            {
                IsCommitted = true,
                ManifestJson = committedManifestJson,
                ChangeSummary = string.IsNullOrWhiteSpace(changeSummary) ? last.ChangeSummary : changeSummary!
            };
            return TrimRevisions(list);
        }

        string nextId = $"rev-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}";
        list.Add(new StudioPersistedRevision
        {
            RevisionId = nextId,
            CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
            Source = source,
            ChangeSummary = changeSummary ?? string.Empty,
            ManifestSha256 = sha,
            IsCommitted = true,
            ManifestJson = committedManifestJson
        });

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
        string? externalEditSummaryLabel = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(widgetId);
        string dir = GetWidgetDirectory(widgetId);
        string briefPath = Path.Combine(dir, BriefFileName);
        string revisionsPath = Path.Combine(dir, RevisionsFileName);
        string chatPath = Path.Combine(dir, ChatFileName);

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
            string diskSha = ComputeManifestSha256(currentDiskManifestJson);
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
                    ManifestJson = currentDiskManifestJson
                });
            }
            else if (!string.Equals(revisions[^1].ManifestSha256, diskSha, StringComparison.Ordinal))
            {
                wasReconciled = true;
                revisions.Add(new StudioPersistedRevision
                {
                    RevisionId = $"rev-ext-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}",
                    CreatedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks,
                    Source = "external",
                    ChangeSummary = string.IsNullOrWhiteSpace(externalEditSummaryLabel)
                        ? "[外部修改]"
                        : externalEditSummaryLabel!,
                    ManifestSha256 = diskSha,
                    IsCommitted = true,
                    ManifestJson = currentDiskManifestJson
                });
                IReadOnlyList<StudioPersistedRevision> trimmed = TrimRevisions(revisions);
                revisions = trimmed.ToList();
                Directory.CreateDirectory(dir);
                CustomWidgetStorageFileOps.ReplaceTextFileAtomically(revisionsPath, SerializeRevisionsJson(revisions));
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
        try
        {
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
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
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
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("revisions", out JsonElement arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                ReadRevisionsArray(arr, result);
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                ReadRevisionsArray(doc.RootElement, result);
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
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, IndentedJsonOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteNumber("updatedAtUtcTicks", draft.UpdatedAtUtcTicks);
            if (!string.IsNullOrWhiteSpace(draft.TargetWidgetId))
            {
                writer.WriteString("targetWidgetId", draft.TargetWidgetId);
            }
            writer.WriteString("selectedEmoji", draft.SelectedEmoji);
            writer.WriteString("startDraftIdea", draft.StartDraftIdea);
            writer.WriteString("refineDraftIdea", draft.RefineDraftIdea);
            writer.WriteNumber("selectedRevisionIndex", draft.SelectedRevisionIndex);
            writer.WriteString("briefMarkdown", ClampUtf8Length(draft.BriefMarkdown, MaxBriefBytes));
            if (!string.IsNullOrWhiteSpace(draft.PreviewSampleDataJson))
            {
                writer.WriteString("previewSampleDataJson", draft.PreviewSampleDataJson);
            }
            writer.WritePropertyName("revisions");
            WriteRevisionsArray(writer, TrimRevisions(draft.Revisions));
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

            var revisions = new List<StudioPersistedRevision>();
            if (root.TryGetProperty("revisions", out JsonElement revArr) && revArr.ValueKind == JsonValueKind.Array)
            {
                ReadRevisionsArray(revArr, revisions);
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
                SelectedEmoji = GetStringOrDefault(root, "selectedEmoji"),
                StartDraftIdea = GetStringOrDefault(root, "startDraftIdea"),
                RefineDraftIdea = GetStringOrDefault(root, "refineDraftIdea"),
                SelectedRevisionIndex = Math.Clamp(
                    GetInt32OrDefault(root, "selectedRevisionIndex", Math.Max(0, revisions.Count - 1)),
                    0,
                    Math.Max(0, revisions.Count - 1)),
                BriefMarkdown = ClampUtf8Length(GetStringOrDefault(root, "briefMarkdown"), MaxBriefBytes),
                PreviewSampleDataJson = GetOptionalString(root, "previewSampleDataJson"),
                Revisions = revisions,
                ChatMessages = messages
            };
        }
        catch
        {
            return null;
        }
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
            writer.WriteString("manifestJson", rev.ManifestJson);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void ReadRevisionsArray(JsonElement arr, List<StudioPersistedRevision> target)
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

            string sha = GetStringOrDefault(item, "manifestSha256");
            if (string.IsNullOrWhiteSpace(sha))
            {
                sha = ComputeManifestSha256(manifestJson);
            }

            target.Add(new StudioPersistedRevision
            {
                RevisionId = GetOptionalString(item, "revisionId") ?? $"rev-{target.Count + 1}",
                CreatedAtUtcTicks = GetInt64OrDefault(item, "createdAtUtcTicks", DateTimeOffset.UtcNow.UtcTicks),
                Source = GetOptionalString(item, "source") ?? "ai",
                ChangeSummary = GetStringOrDefault(item, "changeSummary"),
                ManifestSha256 = sha,
                IsCommitted = GetBoolOrDefault(item, "isCommitted"),
                ManifestJson = manifestJson
            });
        }
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
                TargetNodePath = GetOptionalString(item, "targetNodePath"),
                TargetNodeLabel = GetOptionalString(item, "targetNodeLabel"),
                DiffPills = ReadStringArray(item, "diffPills"),
                Suggestions = ReadStringArray(item, "suggestions")
            });
        }
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

    private static string SanitizeFolderName(string widgetId)
    {
        var sb = new StringBuilder(widgetId.Length);
        foreach (char ch in widgetId.Trim())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.')
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append('_');
            }
        }

        string candidate = sb.ToString().Trim('.');
        return string.IsNullOrWhiteSpace(candidate) ? "widget" : candidate;
    }

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
