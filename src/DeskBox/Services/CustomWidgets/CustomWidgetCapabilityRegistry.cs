using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskBox.Models;

namespace DeskBox.Services;

public enum CustomWidgetCapabilityCategory
{
    DataSource = 0,
    Component = 1,
    Action = 2,
    ConfigAndSecurity = 3
}

public sealed record CustomWidgetApiMemberDoc(
    string Name,
    string Kind,
    string DescriptionZh,
    string DescriptionEn);

public sealed record CustomWidgetCapabilityPack(
    string Id,
    string TitleZh,
    string TitleEn,
    string SummaryZh,
    string SummaryEn,
    IReadOnlyList<string> MemberCapabilityIds)
{
    public string GetTitle(bool isChinese) => isChinese ? TitleZh : TitleEn;
    public string GetSummary(bool isChinese) => isChinese ? SummaryZh : SummaryEn;
}

public sealed record CustomWidgetCapabilitySpec(
    string Id,
    string Emoji,
    string Glyph,
    CustomWidgetCapabilityCategory Category,
    string TitleZh,
    string TitleEn,
    string SummaryZh,
    string SummaryEn,
    IReadOnlyList<string> RequiredCapabilityIds,
    IReadOnlyList<string> TriggerNodeTypes,
    IReadOnlyList<string> TriggerSourceOrActionTypes,
    IReadOnlyList<string> TriggerTokenPrefixes,
    IReadOnlyList<string> TriggerKeywords,
    IReadOnlyList<CustomWidgetApiMemberDoc> Members,
    string CompactContractMarkdown,
    string GoldenSnippetJson)
{
    // Second recall axis: visual-composition pack (grid / immersive / canvas / multimedia ...).
    // Empty means the module is a functional contract rather than a composition pack.
    public string CompositionGroup { get; init; } = string.Empty;

    internal bool HasRemoteTitleZh { get; init; }
    internal bool HasRemoteTitleEn { get; init; }
    internal bool HasRemoteSummaryZh { get; init; }
    internal bool HasRemoteSummaryEn { get; init; }

    public string GetTitle(bool isChinese) => isChinese ? TitleZh : TitleEn;
    public string GetSummary(bool isChinese) => isChinese ? SummaryZh : SummaryEn;

    /// <summary>
    /// Culture-aware title. Resolves <c>CustomWidget.Cap.{Id}.Title</c> from the shared
    /// localization catalog (13 locales); the in-code Zh/En pair stays as the fallback
    /// when the key is missing. Key alignment is pinned by CustomWidgetApiDocsLocalizationTests.
    /// </summary>
    public string GetTitle(string? cultureName)
    {
        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(cultureName ?? string.Empty);
        if ((isChinese && HasRemoteTitleZh) || (!isChinese && HasRemoteTitleEn))
        {
            return isChinese ? TitleZh : TitleEn;
        }
#if DESKBOX_STORE
        if (string.Equals(Id, "local-files-and-commands", StringComparison.OrdinalIgnoreCase))
        {
            return isChinese ? TitleZh : TitleEn;
        }
#endif
        string key = $"CustomWidget.Cap.{Id}.Title";
        string resolved = LocalizationService.TranslateForCulture(cultureName, key);
        return resolved != key
            ? resolved
            : (isChinese ? TitleZh : TitleEn);
    }

    public string GetSummary(string? cultureName)
    {
        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(cultureName ?? string.Empty);
        if ((isChinese && HasRemoteSummaryZh) || (!isChinese && HasRemoteSummaryEn))
        {
            return isChinese ? SummaryZh : SummaryEn;
        }
#if DESKBOX_STORE
        if (string.Equals(Id, "local-files-and-commands", StringComparison.OrdinalIgnoreCase))
        {
            return isChinese ? SummaryZh : SummaryEn;
        }
#endif
        string key = $"CustomWidget.Cap.{Id}.Summary";
        string resolved = LocalizationService.TranslateForCulture(cultureName, key);
        return resolved != key
            ? resolved
            : (isChinese ? SummaryZh : SummaryEn);
    }

    public string GetMemberDescription(CustomWidgetApiMemberDoc member, string? cultureName)
    {
        string key = $"CustomWidget.Cap.{Id}.Member.{CustomWidgetCapabilityRegistry.SlugifyKey(member.Name)}.Desc";
        string resolved = LocalizationService.TranslateForCulture(cultureName, key);
        return resolved != key
            ? resolved
            : (CustomWidgetRemotePromptService.IsChineseCulture(cultureName ?? string.Empty) ? member.DescriptionZh : member.DescriptionEn);
    }
}

/// <summary>
/// Single Source of Truth (SSOT) capability &amp; API registry for DeskBox Custom Widgets.
/// Powers:
/// 1) 3-Tier anti-hallucination prompt assembly (Tier 0 directory + Tier 1 atomic contracts + Must-Use context pins);
/// 2) AST-driven auto-repair capability backfill &amp; <c>$namespace.*</c> Did-You-Mean validation;
/// 3) Studio interactive <c>@</c> / <c>+</c> Capability Selector and <c>API_REFERENCE.md</c> / interactive API Reference dialog;
/// 4) External Agent MCP <c>deskbox_get_capability_spec</c> lookup.
/// </summary>
public static partial class CustomWidgetCapabilityRegistry
{
    /// <summary>
    /// Deterministic key slug shared by code and tests: lowercase, non-alphanumeric runs
    /// collapse to '-', trim, truncate to 48 chars, trim again; empty becomes "x".
    /// Must stay byte-compatible with CustomWidgetApiDocsLocalizationTests.
    /// </summary>
    public static string SlugifyKey(string raw)
    {
        string s = raw.ToLowerInvariant();
        var sb = new System.Text.StringBuilder(s.Length);
        bool pendingDash = false;
        foreach (char c in s)
        {
            bool alnum = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
            if (alnum)
            {
                if (pendingDash && sb.Length > 0)
                {
                    sb.Append('-');
                }
                pendingDash = false;
                sb.Append(c);
            }
            else
            {
                pendingDash = true;
            }
        }

        string slug = sb.Length == 0 ? "x" : sb.ToString();
        if (slug.Length > 48)
        {
            slug = slug[..48].TrimEnd('-');
        }

        return slug.Length == 0 ? "x" : slug;
    }

    public const int DefaultAutoSelectedCap = 5;

    private sealed record RegistrySnapshot(
        IReadOnlyList<CustomWidgetCapabilitySpec> BaseAll,
        IReadOnlyList<CustomWidgetCapabilityPatchDto>? AppliedPatches,
        IReadOnlyList<CustomWidgetCapabilitySpec> All,
        IReadOnlyDictionary<string, CustomWidgetCapabilitySpec> ById);

    private static readonly Lock s_gate = new();
    private static RegistrySnapshot? s_pinnedSnapshot;
    private static WeakReference<RegistrySnapshot>? s_weakSnapshot;

    internal static bool IsRegistryMaterialized
    {
        get
        {
            if (Volatile.Read(ref s_pinnedSnapshot) is not null)
            {
                return true;
            }

            lock (s_gate)
            {
                return s_pinnedSnapshot is not null ||
                       (s_weakSnapshot is not null && s_weakSnapshot.TryGetTarget(out _));
            }
        }
    }

    private static RegistrySnapshot GetSnapshot()
    {
        IReadOnlyList<CustomWidgetCapabilityPatchDto> activePatches =
            CustomWidgetRemotePromptService.Shared.GetEffectivePromptPack().CapabilityPatches;

        if (Volatile.Read(ref s_pinnedSnapshot) is { } pinned &&
            ArePatchListsEquivalent(pinned.AppliedPatches, activePatches))
        {
            return pinned;
        }

        lock (s_gate)
        {
            if (s_pinnedSnapshot is { } existing &&
                ArePatchListsEquivalent(existing.AppliedPatches, activePatches))
            {
                return existing;
            }

            IReadOnlyList<CustomWidgetCapabilitySpec>? baseSpecs = s_pinnedSnapshot?.BaseAll;
            if (baseSpecs is null &&
                s_weakSnapshot is not null &&
                s_weakSnapshot.TryGetTarget(out RegistrySnapshot? revived))
            {
                if (ArePatchListsEquivalent(revived.AppliedPatches, activePatches))
                {
                    Volatile.Write(ref s_pinnedSnapshot, revived);
                    return revived;
                }

                baseSpecs = revived.BaseAll;
            }

            baseSpecs ??= BuildRegistry();
            IReadOnlyList<CustomWidgetCapabilitySpec> effectiveAll = ApplyCapabilityPatches(baseSpecs, activePatches);
            var byId = new Dictionary<string, CustomWidgetCapabilitySpec>(effectiveAll.Count, StringComparer.OrdinalIgnoreCase);
            foreach (CustomWidgetCapabilitySpec spec in effectiveAll)
            {
                byId[spec.Id] = spec;
            }

            var created = new RegistrySnapshot(baseSpecs, activePatches, effectiveAll, byId);
            s_weakSnapshot = new WeakReference<RegistrySnapshot>(created);
            Volatile.Write(ref s_pinnedSnapshot, created);
            return created;
        }
    }

    private static bool ArePatchListsEquivalent(
        IReadOnlyList<CustomWidgetCapabilityPatchDto>? left,
        IReadOnlyList<CustomWidgetCapabilityPatchDto>? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        int leftCount = left?.Count ?? 0;
        int rightCount = right?.Count ?? 0;
        return leftCount == 0 && rightCount == 0;
    }

    /// <summary>
    /// Merges remote <see cref="CustomWidgetCapabilityPatchDto"/> overrides onto a base capability list:
    /// filters out <c>Deprecated == true</c> capsules, applies non-empty field overrides to matching built-in IDs,
    /// and appends brand-new capability capsules dynamically.
    /// </summary>
    public static IReadOnlyList<CustomWidgetCapabilitySpec> ApplyCapabilityPatches(
        IReadOnlyList<CustomWidgetCapabilitySpec> baseSpecs,
        IReadOnlyList<CustomWidgetCapabilityPatchDto>? patches)
    {
        if (patches is null || patches.Count == 0)
        {
            return baseSpecs;
        }

        var merged = new List<CustomWidgetCapabilitySpec>(baseSpecs.Count + patches.Count);
        merged.AddRange(baseSpecs);
        var deprecatedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CustomWidgetCapabilityPatchDto? patch in patches)
        {
            if (patch is null || string.IsNullOrWhiteSpace(patch.Id))
            {
                continue;
            }

            string trimmedId = patch.Id.Trim();
            string resolvedId = ResolveCapabilityAlias(trimmedId);
            int existingIndex = merged.FindIndex(c =>
                string.Equals(c.Id, resolvedId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Id, trimmedId, StringComparison.OrdinalIgnoreCase));

            if (patch.Deprecated)
            {
                deprecatedIds.Add(trimmedId);
                deprecatedIds.Add(resolvedId);
                if (existingIndex >= 0)
                {
                    deprecatedIds.Add(merged[existingIndex].Id);
                    merged.RemoveAt(existingIndex);
                }
                continue;
            }

            bool hasTitleZh = !string.IsNullOrWhiteSpace(patch.TitleZh);
            bool hasTitleEn = !string.IsNullOrWhiteSpace(patch.TitleEn);
            bool hasSummaryZh = !string.IsNullOrWhiteSpace(patch.SummaryZh);
            bool hasSummaryEn = !string.IsNullOrWhiteSpace(patch.SummaryEn);

            if (existingIndex >= 0)
            {
                CustomWidgetCapabilitySpec current = merged[existingIndex];
                merged[existingIndex] = current with
                {
                    TitleZh = hasTitleZh ? patch.TitleZh!.Trim() : current.TitleZh,
                    TitleEn = hasTitleEn ? patch.TitleEn!.Trim() : current.TitleEn,
                    SummaryZh = hasSummaryZh ? patch.SummaryZh!.Trim() : current.SummaryZh,
                    SummaryEn = hasSummaryEn ? patch.SummaryEn!.Trim() : current.SummaryEn,
                    CompactContractMarkdown = !string.IsNullOrWhiteSpace(patch.CompactContractMarkdown)
                        ? patch.CompactContractMarkdown
                        : current.CompactContractMarkdown,
                    GoldenSnippetJson = !string.IsNullOrWhiteSpace(patch.GoldenSnippetJson)
                        ? patch.GoldenSnippetJson
                        : current.GoldenSnippetJson,
                    TriggerKeywords = MergeTriggerKeywords(current.TriggerKeywords, patch.TriggerKeywords),
                    HasRemoteTitleZh = current.HasRemoteTitleZh || hasTitleZh,
                    HasRemoteTitleEn = current.HasRemoteTitleEn || hasTitleEn,
                    HasRemoteSummaryZh = current.HasRemoteSummaryZh || hasSummaryZh,
                    HasRemoteSummaryEn = current.HasRemoteSummaryEn || hasSummaryEn
                };
            }
            else
            {
                string fallbackTitleZh = hasTitleZh
                    ? patch.TitleZh!.Trim()
                    : (hasTitleEn ? patch.TitleEn!.Trim() : trimmedId);
                string fallbackTitleEn = hasTitleEn
                    ? patch.TitleEn!.Trim()
                    : (hasTitleZh ? patch.TitleZh!.Trim() : trimmedId);
                string fallbackSummaryZh = hasSummaryZh
                    ? patch.SummaryZh!.Trim()
                    : (hasSummaryEn ? patch.SummaryEn!.Trim() : string.Empty);
                string fallbackSummaryEn = hasSummaryEn
                    ? patch.SummaryEn!.Trim()
                    : (hasSummaryZh ? patch.SummaryZh!.Trim() : string.Empty);

                merged.Add(new CustomWidgetCapabilitySpec(
                    Id: trimmedId,
                    Emoji: "🧩",
                    Glyph: "\uE71D",
                    Category: CustomWidgetCapabilityCategory.Component,
                    TitleZh: fallbackTitleZh,
                    TitleEn: fallbackTitleEn,
                    SummaryZh: fallbackSummaryZh,
                    SummaryEn: fallbackSummaryEn,
                    RequiredCapabilityIds: [],
                    TriggerNodeTypes: [],
                    TriggerSourceOrActionTypes: [],
                    TriggerTokenPrefixes: [],
                    TriggerKeywords: MergeTriggerKeywords([], patch.TriggerKeywords),
                    Members: [],
                    CompactContractMarkdown: patch.CompactContractMarkdown ?? string.Empty,
                    GoldenSnippetJson: patch.GoldenSnippetJson ?? string.Empty)
                {
                    HasRemoteTitleZh = hasTitleZh,
                    HasRemoteTitleEn = hasTitleEn,
                    HasRemoteSummaryZh = hasSummaryZh,
                    HasRemoteSummaryEn = hasSummaryEn
                });
            }
        }

        if (deprecatedIds.Count > 0)
        {
            for (int i = 0; i < merged.Count; i++)
            {
                CustomWidgetCapabilitySpec spec = merged[i];
                if (spec.RequiredCapabilityIds.Any(deprecatedIds.Contains))
                {
                    merged[i] = spec with
                    {
                        RequiredCapabilityIds = spec.RequiredCapabilityIds
                            .Where(id => !deprecatedIds.Contains(id))
                            .ToArray()
                    };
                }
            }
        }

        return merged;
    }

    private static IReadOnlyList<string> MergeTriggerKeywords(
        IReadOnlyList<string> existingKeywords,
        IReadOnlyList<string>? patchKeywords)
    {
        if (patchKeywords is null || patchKeywords.Count == 0)
        {
            return existingKeywords;
        }

        var combined = new List<string>(existingKeywords.Count + patchKeywords.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string kw in existingKeywords)
        {
            if (!string.IsNullOrWhiteSpace(kw) && seen.Add(kw.Trim()))
            {
                combined.Add(kw.Trim());
            }
        }

        foreach (string kw in patchKeywords)
        {
            if (!string.IsNullOrWhiteSpace(kw) && seen.Add(kw.Trim()))
            {
                combined.Add(kw.Trim());
            }
        }

        return combined;
    }

    /// <summary>
    /// Releases the strong static pin on the parsed 2,300-line capability registry when
    /// Studio closes so idle desktop widgets do not retain authoring docs in RAM.
    /// </summary>
    public static void ReleaseIdleCache(bool dropWeakReference = false)
    {
        lock (s_gate)
        {
            Volatile.Write(ref s_pinnedSnapshot, null);
            if (dropWeakReference)
            {
                s_weakSnapshot = null;
            }
        }
    }

    public static IReadOnlyList<CustomWidgetCapabilitySpec> All => GetSnapshot().All;

    private static IReadOnlyDictionary<string, CustomWidgetCapabilitySpec> ById => GetSnapshot().ById;

    /// <summary>
    /// 12 consolidated Domain Packs exposed to AI authoring (<c>inspect_capabilities</c> &amp; Tier-2 session cache).
    /// Every one of the 37 underlying atomic capability specs belongs to exactly one Domain Pack, preserving
    /// full backward compatibility for legacy 37-ID lookups and 13-language API docs localization.
    /// </summary>
    public static IReadOnlyList<CustomWidgetCapabilityPack> AllDomainPacks { get; } =
    [
        new(
            Id: "layout-and-overlays",
            TitleZh: "布局容器、弹窗与壳层",
            TitleEn: "Layout, Overlays & Shells",
            SummaryZh: "Stack/Grid/ZStack/Card/Tabs/SelectorBar/SplitView/FlipView/Expander/ModuleStack/Popover/Markdown/RichText/CodeBlock/InfoBar/TeachingTip",
            SummaryEn: "Stack/Grid/ZStack/Card/Tabs/SelectorBar/SplitView/FlipView/Expander/ModuleStack/Popover/Markdown/RichText/CodeBlock/InfoBar/TeachingTip",
            MemberCapabilityIds: ["visual-containers", "popover-and-markdown", "rich-controls-and-shells"]),
        new(
            Id: "lists-and-collections",
            TitleZh: "集合存储、列表与拖放",
            TitleEn: "Lists, Collections & DropZone",
            SummaryZh: "collections CRUD/filter/reorder/inlineEdit/exportMarkdown、List、ItemsGrid、DropZone、FileChip",
            SummaryEn: "collections CRUD/filter/reorder/inlineEdit/exportMarkdown, List, ItemsGrid, DropZone, FileChip",
            MemberCapabilityIds: ["collections-lists", "drop-zone"]),
        new(
            Id: "forms-and-inputs",
            TitleZh: "表单输入、滑块与磁贴控件",
            TitleEn: "Forms, Inputs & Control Tiles",
            SummaryZh: "Input/TextArea/AutoSuggestInput/Select/Segmented/RadioGroup/CheckBox/Toggle/Slider/NumberBox/Rating/DatePicker/TimePicker/ColorPicker/ControlTile",
            SummaryEn: "Input/TextArea/AutoSuggestInput/Select/Segmented/RadioGroup/CheckBox/Toggle/Slider/NumberBox/Rating/DatePicker/TimePicker/ColorPicker/ControlTile",
            MemberCapabilityIds: ["forms-inputs", "advanced-inputs-and-styling", "control-tile"]),
        new(
            Id: "charts-and-canvas",
            TitleZh: "图表、仪表盘与矢量画布",
            TitleEn: "Charts, Gauges & Vector Canvas",
            SummaryZh: "Canvas 2.0 交互矢量画布、Chart(line/bar/area)、DonutChart、Table、Sparkline、BarList、RingGauge、RadialGauge、HeatmapGrid、StatCard",
            SummaryEn: "Canvas 2.0 interactive vector surface, Chart(line/bar/area), DonutChart, Table, Sparkline, BarList, RingGauge, RadialGauge, HeatmapGrid, StatCard",
            MemberCapabilityIds: ["micro-charts", "data-charts-and-tables", "vector-canvas"]),
        new(
            Id: "calendar-and-time",
            TitleZh: "日历、世界时钟、倒数日与番茄钟",
            TitleEn: "Calendar, Clock, Countdown & Pomodoro",
            SummaryZh: "CalendarGrid、calendar 农历节气、clock 多时区 tz()、countdown 倒数日、pomodoro 番茄钟",
            SummaryEn: "CalendarGrid, calendar lunar/festivals, clock tz(), countdown milestones, pomodoro timer",
            MemberCapabilityIds: ["calendar", "calendar-grid", "world-clock", "countdown", "pomodoro"]),
        new(
            Id: "local-files-and-cli",
            TitleZh: "本地文件读写、转换、启动与命令行",
            TitleEn: "Local Files, Transforms, Launcher & CLI",
            SummaryZh: "fileText、localFolder、file.writeText(相对路径+onComplete)、file.listDir/manage/open/reveal/pick/transform、text.transform、launcher.open、command/command.run",
            SummaryEn: "fileText, localFolder, file.writeText(relative path+onComplete), file.listDir/manage/open/reveal/pick/transform, text.transform, launcher.open, command/command.run",
            MemberCapabilityIds: ["local-files-and-commands", "file-transform", "quick-launcher"]),
        new(
            Id: "network-and-feeds",
            TitleZh: "网络接口、RSS、天气与 AI 微任务",
            TitleEn: "HTTP, RSS, Weather & AI Micro-Tasks",
            SummaryZh: "httpJson、rss、http.request、weather 天气、ai.run 嵌入式 AI 微任务、aiMonitor 用量监控",
            SummaryEn: "httpJson, rss, http.request, weather forecast, ai.run embedded AI micro-tasks, aiMonitor quota",
            MemberCapabilityIds: ["http-and-rss-feeds", "interactive-http", "weather", "ai-run", "ai-monitor"]),
        new(
            Id: "system-and-device",
            TitleZh: "系统遥测、磁盘、设备状态与控制",
            TitleEn: "System Metrics, Drives & Device Controls",
            SummaryZh: "systemMetrics($sys.*)、storageDrives($drives.*)、deviceState($device.*)、system.control、system.openSetting、音量亮度控制",
            SummaryEn: "systemMetrics($sys.*), storageDrives($drives.*), deviceState($device.*), system.control, system.openSetting, volume/brightness controls",
            MemberCapabilityIds: ["system-metrics", "storage-drives", "device-state", "system-control", "display-and-audio"]),
        new(
            Id: "media-and-audio",
            TitleZh: "音视频播放、白噪音与系统音乐",
            TitleEn: "Media Player, Audio & Now Playing",
            SummaryZh: "MediaPlayer、audio.play/pause/stop/setVolume/cue(白噪音/雨声)、music SMTC 系统正在播放",
            SummaryEn: "MediaPlayer, audio.play/pause/stop/setVolume/cue (white/pink/brown/rain noise), music SMTC now-playing",
            MemberCapabilityIds: ["native-media-and-audio", "music-now-playing"]),
        new(
            Id: "productivity-and-workspace",
            TitleZh: "待办联动、剪贴板与通知提醒",
            TitleEn: "Todo, Clipboard & Notifications",
            SummaryZh: "todo 待办读写、clipboard 剪贴板文本/截图捕获、reminder.notify 系统通知与 toast.show",
            SummaryEn: "todo sync & actions, clipboard text/image capture, reminder.notify system toasts & toast.show",
            MemberCapabilityIds: ["todo-progress-summary", "clipboard-workspace", "notifications-reminders"]),
        new(
            Id: "script-sandbox",
            TitleZh: "QuickJS ES2023 脚本沙箱",
            TitleEn: "QuickJS ES2023 Script Sandbox",
            SummaryZh: "logic.js (onInit/onTick/onSourceData/script.run)、ctx.state/computed/collections/ui/env/dispatch、异步作业回调",
            SummaryEn: "logic.js (onInit/onTick/onSourceData/script.run), ctx.state/computed/collections/ui/env/dispatch, async job callbacks",
            MemberCapabilityIds: ["script-sandbox"]),
        new(
            Id: "settings-and-secrets",
            TitleZh: "组件配置面板与加密密钥库",
            TitleEn: "Widget Settings & DPAPI Secret Vault",
            SummaryZh: "settings (toggle/choice/text/number/file/folder/moduleList/secret)、${$settings.*}、${$secrets.*} DPAPI 凭据保护",
            SummaryEn: "settings (toggle/choice/text/number/file/folder/moduleList/secret), ${$settings.*}, ${$secrets.*} DPAPI secret vault",
            MemberCapabilityIds: ["widget-settings", "secret-vault"])
    ];

    private static readonly System.Collections.Frozen.FrozenDictionary<string, CustomWidgetCapabilityPack> DomainPackById =
        System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(
            AllDomainPacks,
            static p => p.Id,
            StringComparer.OrdinalIgnoreCase);

    private static readonly System.Collections.Frozen.FrozenDictionary<string, string> MemberCapabilityToPackId =
        BuildMemberCapabilityToPackMap();

    private static System.Collections.Frozen.FrozenDictionary<string, string> BuildMemberCapabilityToPackMap()
    {
        var map = new Dictionary<string, string>(48, StringComparer.OrdinalIgnoreCase);
        foreach (CustomWidgetCapabilityPack pack in AllDomainPacks)
        {
            map[pack.Id] = pack.Id;
            foreach (string memberId in pack.MemberCapabilityIds)
            {
                map[memberId] = pack.Id;
            }
        }

        return System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(map, StringComparer.OrdinalIgnoreCase);
    }

    public static bool TryGetDomainPack(string? idOrAlias, out CustomWidgetCapabilityPack? pack)
    {
        pack = null;
        string? packId = ResolveDomainPackId(idOrAlias);
        return !string.IsNullOrWhiteSpace(packId) && DomainPackById.TryGetValue(packId, out pack);
    }

    public static string? ResolveDomainPackId(string? idOrLegacyAlias)
    {
        if (string.IsNullOrWhiteSpace(idOrLegacyAlias))
        {
            return null;
        }

        string trimmed = idOrLegacyAlias.Trim();
        if (DomainPackById.TryGetValue(trimmed, out CustomWidgetCapabilityPack? directPack))
        {
            return directPack.Id;
        }

        if (MemberCapabilityToPackId.TryGetValue(trimmed, out string? directMemberPack))
        {
            return directMemberPack;
        }

        string aliased = ResolveCapabilityAlias(trimmed);
        if (MemberCapabilityToPackId.TryGetValue(aliased, out string? aliasedPack))
        {
            return aliasedPack;
        }

        return null;
    }

    public static IReadOnlyList<string> ResolveDomainPackIdsForCapabilities(IEnumerable<string>? idsOrAliases)
    {
        if (idsOrAliases is null)
        {
            return [];
        }

        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string raw in idsOrAliases)
        {
            if (ResolveDomainPackId(raw) is { Length: > 0 } packId)
            {
                set.Add(packId);
            }
        }

        return set.Count == 0 ? [] : [.. set];
    }

    public static IReadOnlyList<CustomWidgetCapabilitySpec> ExpandPackOrCapabilityIds(IEnumerable<string>? idsOrPackIds)
    {
        if (idsOrPackIds is null)
        {
            return [];
        }

        var atomicIds = new List<string>();
        foreach (string raw in idsOrPackIds)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            string trimmed = raw.Trim();
            if (DomainPackById.TryGetValue(trimmed, out CustomWidgetCapabilityPack? pack))
            {
                atomicIds.AddRange(pack.MemberCapabilityIds);
            }
            else
            {
                atomicIds.Add(trimmed);
            }
        }

        return ExpandWithDependencies(atomicIds);
    }

    public static string BuildDomainPacksDirectoryPromptBlock(bool isChinese)
    {
        var sb = new StringBuilder(1536);
        sb.AppendLine(isChinese
            ? "### 12 域能力包目录（按需调用 `inspect_capabilities(ids=[...])` 一次性批量查阅，同时兼容全部 37 个细分旧 ID）"
            : "### 12 Domain Capability Packs (batch via `inspect_capabilities(ids=[...])`; all 37 legacy atomic IDs also resolve)");
        foreach (CustomWidgetCapabilityPack pack in AllDomainPacks)
        {
            sb.Append("- `").Append(pack.Id).Append("`: ").AppendLine(pack.GetSummary(isChinese));
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Builds a canonically sorted (KV-cache stable) Tier-2 capability contract message for the active Studio session.
    /// Sorting by pack ID guarantees byte-identical prefix across turns and intra-turn tool rounds.
    /// </summary>
    public static string BuildCachedCapabilityPrefixMessage(
        IEnumerable<string>? packOrCapabilityIds,
        bool isChinese,
        out IReadOnlySet<string> coveredAtomicCapabilityIds)
    {
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        coveredAtomicCapabilityIds = covered;
        if (packOrCapabilityIds is null)
        {
            return string.Empty;
        }

        var sortedPackIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string raw in packOrCapabilityIds)
        {
            if (ResolveDomainPackId(raw) is { Length: > 0 } packId)
            {
                sortedPackIds.Add(packId);
            }
        }

        if (sortedPackIds.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(4096);
        sb.AppendLine(isChinese
            ? "## 会话已加载的 DeskBox 能力域契约（KV 缓存稳定前缀，严格遵循以下精确签名，勿重复调用 `inspect_capabilities` 查阅这些包）"
            : "## Session-Loaded DeskBox Domain Capability Contracts (KV-Cache Stable Prefix — follow exact signatures; do NOT re-query these packs via `inspect_capabilities`)");
        sb.Append(isChinese ? "已加载能力包: " : "Loaded packs: ")
          .AppendLine(string.Join(", ", sortedPackIds.Select(static p => $"`{p}`")));

        foreach (string packId in sortedPackIds)
        {
            if (!DomainPackById.TryGetValue(packId, out CustomWidgetCapabilityPack? pack))
            {
                continue;
            }

            IReadOnlyList<CustomWidgetCapabilitySpec> specs = ExpandWithDependencies(pack.MemberCapabilityIds);
            foreach (CustomWidgetCapabilitySpec spec in specs)
            {
                if (!covered.Add(spec.Id))
                {
                    continue;
                }

                sb.AppendLine();
                sb.Append("### ").Append(spec.Emoji).Append(' ').Append(spec.TitleEn)
                  .Append(" (`").Append(spec.Id).Append("` / pack `").Append(pack.Id).AppendLine("`)");
                sb.AppendLine(spec.CompactContractMarkdown.Trim());
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string ResolveCapabilityAlias(string id) =>
        id.ToLowerInvariant() switch
        {
            "script" or "logic.js" or "logic-js" => "script-sandbox",
            "canvas" or "svg" => "vector-canvas",
            "popover" or "popovers" or "markdown" => "popover-and-markdown",
            "chart" or "charts" or "donutchart" or "table" => "data-charts-and-tables",
            "datepicker" or "timepicker" or "colorpicker" or "autosuggestinput" or "radiogroup" or "rating" or "styling" => "advanced-inputs-and-styling",
            "immersive" or "zstack" or "itemsgrid" or "selectorbar" or "flipview" or "splitview" or "splitbutton" or "dropdownbutton" or "infobar" or "infobadge" or "teachingtip" or "richtext" or "codeblock" or "radialgauge" => "rich-controls-and-shells",
            "mediaplayer" or "audio" or "audio.play" or "audio.cue" or "sound" or "whitenoise" => "native-media-and-audio",
            "http.request" or "webhook" => "interactive-http",
#if DESKBOX_STORE
            "filetext" or "localfolder" or "file.writetext" => "local-files-and-commands",
#else
            "filetext" or "localfolder" or "file.writetext" or "command" or "command.run" => "local-files-and-commands",
#endif
            _ => id
        };

    public static CustomWidgetCapabilitySpec? GetById(string? id) =>
        !string.IsNullOrWhiteSpace(id) && ById.TryGetValue(ResolveCapabilityAlias(id.Trim()), out CustomWidgetCapabilitySpec? spec)
            ? spec
            : null;

    public static IReadOnlyList<CustomWidgetCapabilitySpec> ExpandWithDependencies(IEnumerable<string>? capabilityIds)
    {
        if (capabilityIds is null)
        {
            return [];
        }

        IReadOnlyDictionary<string, CustomWidgetCapabilitySpec> byId = ById;
        var result = new List<CustomWidgetCapabilitySpec>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(string id)
        {
            if (string.IsNullOrWhiteSpace(id) || !visited.Add(id))
            {
                return;
            }

            if (!byId.TryGetValue(ResolveCapabilityAlias(id.Trim()), out CustomWidgetCapabilitySpec? spec))
            {
                return;
            }

            foreach (string dep in spec.RequiredCapabilityIds)
            {
                Visit(dep);
            }

            result.Add(spec);
        }

        foreach (string rawId in capabilityIds)
        {
            Visit(rawId);
        }

        return result;
    }

    private static readonly System.Collections.Frozen.FrozenSet<string> FuzzyIdNoiseTokens =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            "and", "or", "with", "the", "for", "from", "into",
            "widget", "widgets", "capability", "capabilities", "module", "modules"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<CustomWidgetCapabilitySpec> SearchFuzzyUnmatchedId(
        string? rawId,
        int maxPrimaryMatches = 2)
    {
        if (string.IsNullOrWhiteSpace(rawId))
        {
            return [];
        }

        string trimmed = rawId.Trim();
        if (GetById(trimmed) is { } exact)
        {
            return ExpandWithDependencies([exact.Id]);
        }

        int cap = Math.Clamp(maxPrimaryMatches, 1, 5);
        var primaryIds = new List<string>(cap);
        var seenPrimary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void TryCollect(IEnumerable<CustomWidgetCapabilitySpec> candidates)
        {
            foreach (CustomWidgetCapabilitySpec spec in candidates)
            {
                if (primaryIds.Count >= cap)
                {
                    break;
                }

                if (seenPrimary.Add(spec.Id))
                {
                    primaryIds.Add(spec.Id);
                }
            }
        }

        TryCollect(Search(trimmed));

        if (primaryIds.Count == 0)
        {
            string collapsed = trimmed.Replace("-", string.Empty, StringComparison.Ordinal)
                                      .Replace("_", string.Empty, StringComparison.Ordinal);
            if (!string.Equals(collapsed, trimmed, StringComparison.OrdinalIgnoreCase) && collapsed.Length >= 3)
            {
                TryCollect(Search(collapsed));
            }
        }

        if (primaryIds.Count == 0 && trimmed.Contains('-', StringComparison.Ordinal))
        {
            string dotted = trimmed.Replace('-', '.');
            TryCollect(Search(dotted));
        }

        if (primaryIds.Count < cap)
        {
            string[] tokens = trimmed
                .Split(['-', '_', '.', '/', ' ', ':'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static t => t.Length >= 2 && !FuzzyIdNoiseTokens.Contains(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (tokens.Length > 0)
            {
                IEnumerable<CustomWidgetCapabilitySpec> tokenRanked = All
                    .Select(spec =>
                    {
                        int totalScore = 0;
                        int matchedTokens = 0;
                        foreach (string token in tokens)
                        {
                            int s = ScoreQueryMatch(spec, token);
                            if (s > 0)
                            {
                                totalScore += s;
                                matchedTokens++;
                            }
                        }

                        if (matchedTokens > 1)
                        {
                            totalScore += matchedTokens * 8;
                        }

                        return (Spec: spec, Score: totalScore);
                    })
                    .Where(static x => x.Score > 0)
                    .OrderByDescending(static x => x.Score)
                    .Select(static x => x.Spec);

                TryCollect(tokenRanked);
            }
        }

        return primaryIds.Count > 0 ? ExpandWithDependencies(primaryIds) : [];
    }

    public static IReadOnlyList<CustomWidgetCapabilitySpec> Search(string? query, CustomWidgetCapabilityCategory? category = null)
    {
        IEnumerable<CustomWidgetCapabilitySpec> source = All;
        if (category is { } cat)
        {
            source = source.Where(c => c.Category == cat);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return source.ToList();
        }

        string q = query.Trim().ToLowerInvariant();
        return source
            .Select(spec => (
                Spec: spec,
                Score: ScoreQueryMatch(spec, q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Spec)
            .ToList();
    }

    private static int ScoreQueryMatch(CustomWidgetCapabilitySpec spec, string q)
    {
        int score = 0;
        if (spec.Id.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 10;
        if (spec.TitleZh.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 9;
        if (spec.TitleEn.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 9;
        if (spec.SummaryZh.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 5;
        if (spec.SummaryEn.Contains(q, StringComparison.OrdinalIgnoreCase)) score += 5;
        if (spec.TriggerNodeTypes.Any(n => n.Contains(q, StringComparison.OrdinalIgnoreCase))) score += 8;
        if (spec.TriggerSourceOrActionTypes.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase))) score += 8;
        if (spec.TriggerTokenPrefixes.Any(t => t.Contains(q, StringComparison.OrdinalIgnoreCase))) score += 7;
        if (spec.TriggerKeywords.Any(k => k.Contains(q, StringComparison.OrdinalIgnoreCase))) score += 6;
        if (spec.Members.Any(m => m.Name.Contains(q, StringComparison.OrdinalIgnoreCase))) score += 4;
        return score;
    }

    /// <summary>
    /// Tier 0: Ultra-compact global directory + Layout &amp; Interaction Architecture Rules + Anti-Facade Honesty Rule.
    /// Always included so the LLM knows every native capability DeskBox provides and follows standardized UI conventions.
    /// </summary>
    public static string BuildTier0DirectoryPromptBlock(bool isChinese = false, bool includeHostStyleRules = true)
    {
        var sb = new StringBuilder(4096);
        if (isChinese)
        {
            sb.AppendLine("## DeskBox 原生能力与组件总目录 (Schema v1 扁平元素图)");
            sb.AppendLine("- 根字段: `schemaVersion: 1`, `id` (`custom.<slug>`), `name`（多语言包可用 `${$i18n.widgetName}`）, `icon`（优先用表达主题的 Emoji，如 `🌤️`；节点 `icon` 统一用语义名如 `add`/`edit`/`delete`/`export`/`folder`/`play`/`pause`/`refresh`/`save`/`copy`/`search`/`check`/`close`/`settings`/`download`/`upload`/`volume`/`mute`/`pin`/`sparkles`，禁手写 PUA 码）, `defaultSize: { width, height }`, `minSize`, `\"settings\"`, `state`（原生支持布尔/数字/数组/对象）, `collections`, `sources`, `script`, `popovers`（`{ \"popId\": { \"title\", \"width\", \"root\": \"pop_body\" } }`）, `onDrop`, `capsule`, `root`（根元素 ID，如 `\"main\"`）, `elements`（扁平字典 `{ \"<elemId>\": { \"type\": \"...\", \"children\": [\"child_id\"] } }`，容器存子元素 ID 字符串数组，`itemTemplate`/`pane`/`actionButton` 存元素 ID 字符串）。");
            sb.AppendLine("- 布局与导航容器: `Stack` (vertical|horizontal), `ZStack` (叠层), `ModuleStack` (`bind`: `\"settings.modules\"`，子模块声明 `id`，相邻窄卡片可设 `compactPair: true` 自动并排成双列), `Grid` (`columns` 如 `\"*,*\"`/`\"5\"`/`\"${...}\"`, `rows`, `column`, `row`, `columnSpan`, `rowSpan`), `ItemsGrid` (`items`/`collection`, `minItemWidth` 或 `columns`, `itemTemplate`), `Tabs` (`bind`, `children`), `SelectorBar` (`bind`, `options`/`children`), `FlipView` (`items`/`children`, `bind`), `SplitView` (`bind`, `paneWidth`, `displayMode`, `pane`), `Wrap`, `Scroll`, `Card` (`tone`: default|subtle|accent|success|warning|danger，最多嵌套 2 层), `Expander`, `Popover` (配合顶层 `\"popovers\"` 与 `Button.popover`), `Divider`, `Spacer`。");
            sb.Append("- 富文本、媒体与反馈: `Text` (`style`: caption|body|bodyStrong|subtitle|title|hero|mono, `maxLines`, `animateChange`), `RichText` (`spans`: `[{ text, fontWeight, italic, color, mono, href, onClick }]`), `CodeBlock` (`text`/`bind`, `language`, `copyable`, `wrapText`, `autoScrollTail`, `maxHeight`), `Markdown` (`text`, `bind`), `Image` (`src`, `height`, `stretch`), `MediaPlayer` (`src`, `poster`, `autoPlay`, `loop`, `muted`, `volume`, `showTransport`), `Badge`, `InfoBadge` (`value`, `tone`), `InfoBar` (`severity`: informational|success|warning|error, `title`, `subtitle`, `isClosable`), `TeachingTip` (`id`, `target`, `title`, `subtitle`), `KeyValueRow`, `EmptyState`。");
            sb.AppendLine(includeHostStyleRules
                ? "宿主接管毛玻璃底板、统一圆角与明暗主题可读性（严禁给布局容器写不透明 `background`/渐变或中性灰白黑 `textColor`）：支持 `Card.tone`、语义 `color`、`borderColor`/`borderWidth`、`margin`、`minWidth`/`maxWidth`/`maxHeight`、`fontWeight`、`fontFamily`(`mono`)、`opacity`、`shadow`(`none|sm|md`)、`aspectRatio` 及右键 `contextMenu`(`menuItems`)。"
                : "通用属性: `when`, `Card.tone`, `color`, `borderColor`/`borderWidth`, `margin`, `minWidth`/`maxWidth`/`maxHeight`, `fontWeight`, `fontFamily`(`mono`), `opacity`, `shadow`(`none|sm|md`), `aspectRatio`, `contextMenu`(`menuItems`)。");
            sb.AppendLine("- 交互控件: `Input` (`bind`, `onSubmit`), `TextArea`, `AutoSuggestInput` (`bind`, `suggestions`/`suggestionsSource`, `onSubmit`, `onChange`), `Select` (`options`), `Segmented` (`options`), `RadioGroup` (`bind`, `options`, `orientation`, `onChange`), `CheckBox` (`bind`/`value`, `onToggle`), `Toggle` (`bind`/`value`, `onChange`), `Slider` (`value`/`bind`, `min`, `max`, `step`, `unit`, `onChange` 传 `${$value}`), `NumberBox`, `Rating` (`bind`/`value`, `max`, `allowHalfStar`, `onChange`), `DatePicker` (`bind`/`value` `YYYY-MM-DD`, `onChange`), `TimePicker` (`bind`/`value` `HH:mm`, `onChange`), `ColorPicker` (`bind`/`value`, `presets`, `onChange`), `Button` (`variant`: standard|accent|subtle|danger, `popover`, `onClick`), `SplitButton` (`text`, `onClick`, `menuItems`), `DropDownButton` (`text`, `icon`, `menuItems`), `IconButton` (`icon`, `size`: 24|28, `onClick`), `ControlTile`, `List` (`items`, `allowReorder`, `onReorder`, `inlineEditField`, `onInlineCommit`, `itemTemplate`, `hoverActions`), `DropZone` (`title`, `subtitle`, `format`, `onDrop`)。");
            sb.AppendLine("- 图表与矢量画布 2.0: `Canvas` (`viewBox`, `elements` 或 `elementsSource`: `\"${$computed.shapes}\"`，支持 `rect|circle|ellipse|arc|line|polyline|polygon|path|text`、`onItemClick`、`onPointerDown|Move|Up`), `Table` (`collection`/`items`, `columns`, `sortable`, `striped`, `onRowClick`), `DonutChart`, `Chart` (`chartKind`: `line|bar|area`, `collection`/`items`, `xField`, `series`), `RingGauge`, `RadialGauge` (`bind`/`progress`/`value`, `min`, `max`, `startAngle`, `sweepAngle`, `showTicks`+`tickStep`, `animateChange`, `isInteractive`+`onChange`), `BarList` (`collection`/`items`, `titleField`, `valueField`, `subtitleField`, `unit`, `maxItems`, `color`), `CalendarGrid`, `Sparkline`, `HeatmapGrid`, `ProgressBar`, `ProgressRing`, `ProgressRow`, `FileChip`, `StatCard`。");
            const string zhCommonSources = "`weather` (`$weather.*`, `forecast` 集合), `calendar` (`$calendar.lunar|lunarTitle|festival|subtitle|weekday|today`), `clock` (`$clock.*` + `tz('<zoneId>', '<format>')`), `systemMetrics` (`$sys.*` + `${$sys.topProcesses}`), `storageDrives` (`$drives.*` + `drives` 集合), `deviceState` (`$device.*`), `music` (`$music.*`), `todo` (`$todo.totalCount|pendingCount|completedCount|completionPercent|nextTaskTitle|summary` + `todos` 集合), `clipboard` (`$clipboard.text|preview|length|lines`), `aiMonitor` (`$ai.*` + `${$ai.providers}`, `${$ai.dailyTrend}`), `countdown` (`$countdown.*` + `${$countdown.items}`, `${$countdown.progressBars}`), `pomodoro` (`$pomodoro.*`), `httpJson`, `rss`, `fileText`, `localFolder`";
            const string zhCommonActionsPrefix = "`state.set|toggle|increment`（支持 `${$value}` 与原生数字/布尔/对象）, `collection.prepend|append|update|remove|clear|reorder|exportMarkdown`, `popover.open|close|toggle` (`popoverId`), `tip.open|close|toggle` (`targetId`), `audio.cue` (`complete|tick|pop|soft|alert|bell`), `audio.play|pause|stop|setVolume|stopAll` (`trackId`, `src` 支持 `noise:white|pink|brown|rain` 或本地/HTTPS 音频), `script.run` (`fn`, `arg`, `args` -> `ctx.args`, `ctx.event`, `ctx.dispatch`), `http.request` (`id`, `jobKey`, `method`, `url`, `headers`, `body`, `stateMap`, `collection`, `onComplete`), `file.writeText`, `file.listDir`, `file.manage` (`copy|move|rename|recycle`), `clipboard.captureImage`, ";
            const string zhCommonActionsSuffix = "`job.cancel`, `text.transform`, `launcher.open`, `ai.run`, `file.transform`, `file.pick|open|reveal`, `system.control`, `system.openSetting`, `reminder.notify`, `toast.show`, `source.refresh` (`sourceId`), `clipboard.copy`, `url.open`, `todo.add|toggle|remove|cyclePriority|clearCompleted`, `pomodoro.*`, `countdown.upsert|pin|delete`";
#if DESKBOX_STORE
            sb.AppendLine($"- 内置宿主 `sources`: {zhCommonSources}。（微软商店沙箱版不支持 `command` 数据源。）");
            sb.AppendLine($"- 内置宿主 `actions`: {zhCommonActionsPrefix}{zhCommonActionsSuffix}。（微软商店沙箱版不支持 `command.run`。）");
#else
            sb.AppendLine($"- 内置宿主 `sources`: {zhCommonSources}, `command`（桌面直装版：本地 CLI 轮询写入 `stateKey`）。");
            sb.AppendLine($"- 内置宿主 `actions`: {zhCommonActionsPrefix}`command.run` (`id`, `jobKey`, `executable`, `arguments`, `workingDirectory`, `longRunning: true`, `onProgress`, `onComplete`), {zhCommonActionsSuffix}。");
#endif
            sb.AppendLine(BuildDomainPacksDirectoryPromptBlock(isChinese: true));
            if (includeHostStyleRules)
            {
                sb.AppendLine("- 布局、脚本与交互铁律:");
                sb.AppendLine("  1. **宿主接管窗口框，严禁双标题栏**：标题栏、圆角、材质与拖拽由宿主统一控制——禁止声明顶层 `\"chrome\"` 或在根元素内自画标题栏/拖拽条。");
                sb.AppendLine("  2. **视口内边距与防溢出**：多卡片布局在根元素 (`elements[root]`) 用 `\"padding\": \"12,10,12,12\"`；全出血视觉舞台用根 `Grid`(`\"rows\": \"*,Auto\"`) + `\"padding\": \"0\"`。控件实时 `onChange` 传 `${$value}`，禁止旁置多余「确定/同步」按钮；低频配置放入顶层 `\"settings\"` 并确保每个键真实绑定。");
                sb.AppendLine("  3. **`logic.js` 沙箱全量契约与空参防御**：`ctx.state` 原生读写布尔/数字/数组/对象（勿把布尔写成 `\"false\"` 字符串！）；`ctx.collections` 提供 `get(key)`、`add/append(key, item)`、`prepend(key, item)`、`update(key, id, patch)`、`remove(key, id)`、`clear(key)`、`set(key, items)`；`ctx.ui` 提供 `toast(msg)`、`copy(text)`、`notify(title, msg)`、`refreshSource(id)`；`ctx.env` 提供 `locale`（当前界面语言如 `fr-FR`）、`sys`、`device`、`weather`、`clipboard`、`idleSeconds`、`dayType`、`isHoliday`、`isWorkday`、`holidayName`、`transform`、`clockZones`（不存在 `ctx.sources`）。校验器会对 `script.run` 绑定的导出函数执行无参静态干跑，读取 `ctx.args?.xxx` 与 `ctx.event?.item` 时必须使用可选链/空值守卫，并在 `onInit(ctx)` 中初始化全部 `${$computed.*}` 键，同时提供高密度 `capsule`。");
                sb.AppendLine("  4. **外部接口先澄清，严禁空壳假按钮**：未知 URL/鉴权的第三方接口首轮返回 `\"kind\": \"clarify\"`（有公开 URL 可先调 `probeHttp`），严禁造只改无用字符串的假开关。");
            }
            else
            {
                sb.AppendLine("- `logic.js` 沙箱速查：`ctx.collections`(`get|add|append|prepend|update|remove|clear|set`), `ctx.ui`(`toast|copy|notify|refreshSource`), `ctx.env`(`locale|sys|device|weather|clipboard|idleSeconds|dayType|isHoliday|isWorkday|holidayName|transform|clockZones`；无 `ctx.sources`), 空参静态干跑守卫 `ctx.args?.xxx` / `ctx.event?.item`。");
            }
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine("## DeskBox Native Capability & Component Directory (Schema v1 Flat Element Graph)");
        sb.AppendLine("- Root Keys: `schemaVersion: 1`, `id` (`custom.<slug>`), `name` (supports `${$i18n.widgetName}`), `icon` (prefer expressive Emoji e.g. `🌤️`; on UI nodes use semantic icon names like `add`/`edit`/`delete`/`export`/`folder`/`play`/`pause`/`refresh`/`save`/`copy`/`search`/`check`/`close`/`settings`/`download`/`upload`/`volume`/`mute`/`pin`/`sparkles` — never guess PUA hex), `defaultSize: { width, height }`, `minSize`, `\"settings\"`, `state` (native booleans/numbers/arrays/objects), `collections`, `sources`, `script`, `popovers` (`{ \"popId\": { \"title\", \"width\", \"root\": \"pop_body\" } }`), `onDrop`, `capsule`, `root` (root element ID e.g. `\"main\"`), `elements` (flat dictionary `{ \"<elemId>\": { \"type\": \"...\", \"children\": [\"child_id\"] } }` where containers store string ID arrays and `itemTemplate`/`pane`/`actionButton` store element ID strings).");
        sb.AppendLine("- Shell & Layout Containers: `Stack` (vertical|horizontal), `ZStack` (z-order overlay stack), `ModuleStack` (`bind`: `\"settings.modules\"`, drag-reorderable/toggleable child sections each with `id`; set `compactPair: true` on adjacent narrow children to pair them side-by-side in 2 columns), `Grid` (`columns` track list e.g. `\"*,*\"`/`\"5\"`/`\"${...}\"`, `rows`, `column`, `row`, `columnSpan`, `rowSpan`), `ItemsGrid` (`items`/`collection`, `minItemWidth` (alias `minColumnWidth`) or `columnsCount` (alias `columns`), `itemTemplate`), `Tabs` (`bind`, `children`), `SelectorBar` (`bind`, `options` or `children`), `FlipView` (carousel: `items`/`children`, `bind`), `SplitView` (`bind`, `paneWidth`, `displayMode`, explicit `pane` element ID or `[paneId, contentId]` children), `Wrap`, `Scroll`, `Card` (`tone`: default|subtle|accent|success|warning|danger, max 2 nested levels), `Expander`, `Popover` (plus root `\"popovers\"` & `Button.popover`), `Divider`, `Spacer`.");
        sb.Append("- Rich Typography, Media, Feedback & Styling: `Text` (`style`: caption|body|bodyStrong|subtitle|title|hero|mono, `maxLines`; `hero` gets an automatic 150ms opacity change-highlight pulse, `animateChange: true` enables the same pulse on any other style), `RichText` (`spans`: `[{ text, fontWeight, italic, color, mono, href, onClick }]`, `href` dispatches `url.open`), `CodeBlock` (`text`/`bind`, `language`, `copyable`, `wrapText`, `autoScrollTail`, `maxHeight`), `Markdown` (GFM + interactive `- [x]` toggles via `bind`), `Image` (`src`, `height`, `stretch`), `MediaPlayer` (native WinRT video/audio: `src`, `poster`, `autoPlay`, `loop`, `muted`, `volume`, `showTransport`), `Badge`, `InfoBadge` (`value`, `tone`), `InfoBar` (`severity`: informational|success|warning|error, `title`, `subtitle`, `isClosable`), `TeachingTip` (`id`, `target`, `title`, `subtitle`), `KeyValueRow`, `EmptyState`. ");
        sb.AppendLine(includeHostStyleRules
            ? "Host-unified styling (containers inherit Mica/Acrylic glass, unified corner radius, and Light/Dark text legibility — avoid opaque container backgrounds/gradients or neutral hex inks): `Card.tone` (`default|subtle|accent|success|warning|danger`), semantic `color` & chromatic gauge/chart/`Canvas` accents, `borderColor`/`borderWidth`, `margin`, `minWidth`, `maxWidth`, `maxHeight`, `fontWeight`, `fontFamily` (`mono`), `opacity`, `shadow` (`none|sm|md`), `aspectRatio` (width/height, honored only without an explicit `height`), plus universal right-click `contextMenu` (alias `menuItems` on non-button nodes)."
            : "Props: `when`, `Card.tone`, `color`, `borderColor`/`borderWidth`, `margin`, `minWidth`/`maxWidth`/`maxHeight`, `fontWeight`, `fontFamily` (`mono`), `opacity`, `shadow` (`none|sm|md`), `aspectRatio`, `contextMenu` (`menuItems`).");
        sb.AppendLine("- Interactive Controls: `Input` (`bind`, `onSubmit`), `TextArea`, `AutoSuggestInput` (`bind`, `suggestions`/`suggestionsSource`, `onSubmit`, `onChange`), `Select` (`options`), `Segmented` (`options`), `RadioGroup` (`bind`, `options`, `orientation`, `onChange`), `CheckBox` (`bind`/`value`, `onToggle`), `Toggle` (`bind`/`value`, `onChange`), `Slider` (`value`/`bind`, `min`, `max`, `step`, `unit`, `onChange` supporting `${$value}`), `NumberBox`, `Rating` (`bind`/`value`, `max`, `allowHalfStar`, `onChange`), `DatePicker` (`bind`/`value` ISO `YYYY-MM-DD`, `onChange`), `TimePicker` (`bind`/`value` `HH:mm`, `onChange`), `ColorPicker` (`bind`/`value` hex color, `presets`, `onChange`), `Button` (`variant`: standard|accent|subtle|danger, `popover`, `onClick`), `SplitButton` (`text`, `onClick`, `menuItems`), `DropDownButton` (`text`, `icon`, `menuItems`), `IconButton` (`icon` semantic icon name like `add`/`edit`/`delete`/`refresh` or Emoji, `size`: 24|28, `onClick`), `ControlTile`, `List` (`items`, `allowReorder`, `onReorder`, `inlineEditField`, `onInlineCommit`, `itemTemplate`, `hoverActions`), `DropZone` (`title`, `subtitle`, `format`, `onDrop`).");
        sb.AppendLine("- Data Visualization & Vector Canvas 2.0: `Canvas` (pure native vector canvas: `viewBox`, static `elements` or dynamic `elementsSource`: `\"${$computed.shapes}\"` supporting `rect|circle|ellipse|arc|line|polyline|polygon|path|text` with gradients, `strokeCap`, `rotation`, `onItemClick`, and `onPointerDown|Move|Up` passing `{ x, y, normX, normY, isPressed }`), `Table` (`collection`/`items`, `columns`, `sortable`, `striped`, `onRowClick`), `DonutChart`, `Chart` (`chartKind`: `line|bar|area`, `collection`/`items`, `xField`, multi-`series`), `RingGauge`, `RadialGauge` (`bind`/`progress`/`value`, `min`, `max`, `startAngle`, `sweepAngle`, `showTicks` + `tickStep`, `animateChange` (150ms opacity change-highlight, not a numeric tween), `isInteractive` + `onChange` drag-to-set), `BarList` (`collection`/`items`, `titleField`, `valueField`, `subtitleField`, `unit`, `maxItems`, `color`), `CalendarGrid`, `Sparkline`, `HeatmapGrid`, `ProgressBar`, `ProgressRing`, `ProgressRow`, `FileChip`, `StatCard`.");
        const string enCommonSources = "`weather` (`$weather.*`, `forecast` collection), `calendar` (`$calendar.lunar|lunarTitle|festival|subtitle|weekday|today`), `clock` (`$clock.*` + `tz('<zoneId>', '<format>')`), `systemMetrics` (`$sys.*` + `${$sys.topProcesses}`), `storageDrives` (`$drives.*` + `drives` collection), `deviceState` (`$device.*`), `music` (`$music.*`), `todo` (`$todo.totalCount|pendingCount|completedCount|completionPercent|nextTaskTitle|summary` + `todos` collection), `clipboard` (`$clipboard.text|preview|length|lines`), `aiMonitor` (`$ai.*` + `${$ai.providers}`, `${$ai.dailyTrend}`), `countdown` (`$countdown.*` + `${$countdown.items}`, `${$countdown.progressBars}`), `pomodoro` (`$pomodoro.*`), `httpJson`, `rss`, `fileText`, `localFolder`";
        const string enCommonActionsPrefix = "`state.set|toggle|increment` (supports `${$value}` and native booleans/numbers/objects), `collection.prepend|append|update|remove|clear|reorder|exportMarkdown`, `popover.open|close|toggle` (`popoverId`), `tip.open|close|toggle` (`targetId`), `audio.cue` (`cue`: complete|tick|pop|soft|alert|bell), `audio.play|pause|stop|setVolume|stopAll` (`trackId`, `src` supporting `noise:white|pink|brown|rain` or local/HTTPS audio), `script.run` (`fn`, `arg`, structured `args` -> `ctx.args`, `ctx.event` with `files`/`normX`/`normY`, `ctx.dispatch`), `http.request` (`id`, `jobKey`, `method`, `url`, `headers`, `body`, `stateMap`, `collection`, `onComplete`), `file.writeText`, `file.listDir`, `file.manage` (`copy|move|rename|recycle`), `clipboard.captureImage`, ";
        const string enCommonActionsSuffix = "`job.cancel`, `text.transform`, `launcher.open`, `ai.run`, `file.transform`, `file.pick|open|reveal`, `system.control`, `system.openSetting`, `reminder.notify`, `toast.show`, `source.refresh` (`sourceId`), `clipboard.copy`, `url.open`, `todo.add|toggle|remove|cyclePriority|clearCompleted`, `pomodoro.*`, `countdown.upsert|pin|delete`";
#if DESKBOX_STORE
        sb.AppendLine($"- Built-in Host `sources`: {enCommonSources}. (Microsoft Store sandbox edition: `command` source is NOT supported.)");
        sb.AppendLine($"- Built-in Host `actions`: {enCommonActionsPrefix}{enCommonActionsSuffix}. (Microsoft Store sandbox edition: `command.run` is NOT supported.)");
#else
        sb.AppendLine($"- Built-in Host `sources`: {enCommonSources}, `command` (direct build: local CLI stdout polling -> `stateKey`).");
        sb.AppendLine($"- Built-in Host `actions`: {enCommonActionsPrefix}`command.run` (`id`, `jobKey`, `executable`, `arguments`, `workingDirectory`, `longRunning: true`, `onProgress(ctx, p)`, `onComplete`), {enCommonActionsSuffix}.");
#endif
        sb.AppendLine(BuildDomainPacksDirectoryPromptBlock(isChinese: false));
        if (includeHostStyleRules)
        {
            sb.AppendLine("- Layout, Sandbox & Interaction Standards:");
            sb.AppendLine("  1. **Host-Managed Window Chrome & No Duplicate Header**: DeskBox unifies title-bar mode (Standard / Compact / Overlay), corner radius, material, and dragging from user settings — NEVER emit a root `\"chrome\"` key, duplicate inner title bar, or custom drag handle inside the root element.");
            sb.AppendLine("  2. **Root Viewport Inset, Live Controls & Settings Separation**: Multi-section card layouts use `\"padding\": \"12,10,12,12\"` on the root element (`elements[root]`); full-bleed stages use root `Grid` (`\"rows\": \"*,Auto\"`) + `\"padding\": \"0\"`. Live controls (`Slider`, `Toggle`, `Segmented`, `RadioGroup`, `Rating`, `DatePicker`, `TimePicker`, `ColorPicker`) fire `onChange` (`${$value}`) immediately without apply buttons. Put low-frequency options/paths/secrets in root `\"settings\"` and bind every declared setting key.");
            sb.AppendLine("  3. **First-Class `logic.js` Full Sandbox Surface & Dry-Run Null Safety**: `ctx.state` natively persists booleans/numbers/arrays/objects (never stringify booleans as `\"false\"`!); `ctx.collections` exposes `get(key)`, `add/append(key, item)`, `prepend(key, item)`, `update(key, id, patch)`, `remove(key, id)`, `clear(key)`, `set(key, items)`; `ctx.ui` exposes `toast(msg)`, `copy(text)`, `notify(title, msg)`, `refreshSource(id)`; `ctx.env` exposes `locale` (active host UI culture e.g. `fr-FR`), `sys`, `device`, `weather`, `clipboard`, `idleSeconds`, `dayType`, `isHoliday`, `isWorkday`, `holidayName`, `transform`, `clockZones` (`ctx.sources` does NOT exist). Because exported `script.run` handlers are dry-run validated with empty event context at compile time, ALWAYS guard `ctx.args?.foo` and `ctx.event?.item` with optional chaining, initialize all `${$computed.*}` keys in `export function onInit(ctx)`, and include a high-density `\"capsule\"`.");
            sb.AppendLine("  4. **External API Clarification & Anti-Facade Rule**: NEVER invent fake buttons/switches that only mutate unread dummy `state` strings. Return `\"kind\": \"clarify\"` in Turn 1 when a 3rd-party endpoint URL/auth/schema is unknown (or use `probeHttp` when a public URL is given).");
        }
        else
        {
            sb.AppendLine("- `logic.js` Sandbox API: `ctx.collections`(`get|add|append|prepend|update|remove|clear|set`), `ctx.ui`(`toast|copy|notify|refreshSource`), `ctx.env`(`locale|sys|device|weather|clipboard|idleSeconds|dayType|isHoliday|isWorkday|holidayName|transform|clockZones`; NO `ctx.sources`), dry-run optional-chaining guard on `ctx.args?.foo` & `ctx.event?.item`.");
        }
        return sb.ToString().TrimEnd();
    }

    [GeneratedRegex(@"@([A-Za-z0-9][A-Za-z0-9.-]*)", RegexOptions.CultureInvariant)]
    private static partial Regex InlineCapabilityMentionRegex();

    public static IReadOnlyList<string> ExtractMentionedCapabilityIds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains('@'))
        {
            return [];
        }

        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in InlineCapabilityMentionRegex().Matches(text))
        {
            string rawId = match.Groups[1].Value.TrimEnd('.');
            if (ById.TryGetValue(ResolveCapabilityAlias(rawId), out CustomWidgetCapabilitySpec? spec) && seen.Add(spec.Id))
            {
                found.Add(spec.Id);
            }
        }

        return found;
    }

    public static (IReadOnlyList<CustomWidgetCapabilitySpec> PinnedSpecs, IReadOnlyList<CustomWidgetCapabilitySpec> AutoSpecs)
        SelectCapabilitiesForTurn(
            string? currentManifestJson,
            string? userPrompt,
            string? briefMarkdown = null,
            IEnumerable<string>? pinnedCapabilityIds = null,
            int maxAutoCaps = DefaultAutoSelectedCap,
            int compositionQuota = 2)
    {
        var combinedPinIds = new List<string>();
        if (pinnedCapabilityIds is not null)
        {
            combinedPinIds.AddRange(pinnedCapabilityIds);
        }
        combinedPinIds.AddRange(ExtractMentionedCapabilityIds(userPrompt));

        IReadOnlyList<CustomWidgetCapabilitySpec> pinnedExpanded = ExpandWithDependencies(combinedPinIds);
        var excludedIds = new HashSet<string>(pinnedExpanded.Select(static p => p.Id), StringComparer.OrdinalIgnoreCase);

        var autoCandidates = new List<CustomWidgetCapabilitySpec>();
        int cap = Math.Clamp(maxAutoCaps, 2, 8);
        int compositionSlots = Math.Clamp(compositionQuota, 0, cap);
        var scoredComposition = new List<CustomWidgetCapabilitySpec>();

        // 1. AST inspection of existing manifest
        if (!string.IsNullOrWhiteSpace(currentManifestJson))
        {
            CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(currentManifestJson);
            if (parsed.Manifest is { } manifest)
            {
                foreach (CustomWidgetCapabilitySpec spec in MatchFromManifestAst(manifest))
                {
                    if (autoCandidates.Count >= Math.Max(2, cap - 2))
                    {
                        break;
                    }

                    if (excludedIds.Add(spec.Id))
                    {
                        autoCandidates.Add(spec);
                    }
                }
            }
        }

        // 2. Intent keyword scoring across userPrompt + brief.
        // Node type names are deliberately NOT scored here: "list"/"image"/"input"/"tabs"
        // are ordinary words, so matching them against prose produces noise. They are
        // matched against the manifest AST in pass 1 instead.
        string combined = $"{userPrompt ?? string.Empty}\n{briefMarkdown ?? string.Empty}".ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(combined))
        {
            var scored = All
                .Where(c => !excludedIds.Contains(c.Id))
                .Select(c => (
                    Spec: c,
                    Score: c.TriggerKeywords.Count(kw => combined.Contains(kw.ToLowerInvariant(), StringComparison.Ordinal)) * 3 +
                           c.TriggerSourceOrActionTypes.Count(a => combined.Contains(a.ToLowerInvariant(), StringComparison.Ordinal)) * 4))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ToList();

            foreach (var hit in scored)
            {
                if (autoCandidates.Count >= cap)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(hit.Spec.CompositionGroup) &&
                    autoCandidates.Count >= cap - compositionSlots)
                {
                    continue;
                }

                foreach (CustomWidgetCapabilitySpec withDep in ExpandWithDependencies([hit.Spec.Id]))
                {
                    if (excludedIds.Add(withDep.Id))
                    {
                        autoCandidates.Add(withDep);
                    }
                }
            }

            scoredComposition.AddRange(scored
                .Where(static x => !string.IsNullOrWhiteSpace(x.Spec.CompositionGroup))
                .Select(static x => x.Spec));
        }

        // 3. Default starter contracts when creating from scratch with generic prompt and zero pins
        if (pinnedExpanded.Count == 0 && autoCandidates.Count == 0)
        {
            foreach (string fallbackId in new[] { "visual-containers", "rich-controls-and-shells" })
            {
                if (ById.TryGetValue(fallbackId, out CustomWidgetCapabilitySpec? fb) && excludedIds.Add(fb.Id))
                {
                    autoCandidates.Add(fb);
                }
            }
        }

        // 4. Composition quota: the model must always hold visual-composition contracts alongside
        // functional I/O ones, otherwise every request collapses into the same vertical card.
        int compositionCount = autoCandidates.Count(static c => !string.IsNullOrWhiteSpace(c.CompositionGroup));
        if (compositionCount < compositionSlots)
        {
            var compositionPool = scoredComposition
                .Concat(new[] { "visual-containers", "rich-controls-and-shells", "vector-canvas", "native-media-and-audio" }
                    .Where(ById.ContainsKey)
                    .Select(id => ById[id]))
                .ToList();

            foreach (CustomWidgetCapabilitySpec candidate in compositionPool)
            {
                if (compositionCount >= compositionSlots || autoCandidates.Count >= cap)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(candidate.CompositionGroup) || !excludedIds.Add(candidate.Id))
                {
                    continue;
                }

                autoCandidates.Add(candidate);
                compositionCount++;
            }
        }

        return (pinnedExpanded, autoCandidates);
    }

    public static string FormatSelectedCapsulesPromptBlock(
        IReadOnlyList<CustomWidgetCapabilitySpec> pinnedSpecs,
        IReadOnlyList<CustomWidgetCapabilitySpec> autoSpecs)
    {
        var sb = new StringBuilder(4096);

        if (pinnedSpecs.Count > 0)
        {
            sb.AppendLine("## Explicitly Selected Capability Modules by User (MUST Integrate & Follow Exact Signatures)");
            foreach (CustomWidgetCapabilitySpec spec in pinnedSpecs)
            {
                sb.AppendLine();
                sb.Append("### [MUST-USE] ").Append(spec.Emoji).Append(' ').Append(spec.TitleEn)
                  .Append(" (`").Append(spec.Id).AppendLine("`)");
                sb.AppendLine(spec.CompactContractMarkdown.Trim());
            }
        }

        if (autoSpecs.Count > 0)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine("## Matched Atomic Capability Contracts (Follow Exact Signatures)");
            foreach (CustomWidgetCapabilitySpec spec in autoSpecs)
            {
                sb.AppendLine();
                sb.Append("### ").Append(spec.Emoji).Append(' ').Append(spec.TitleEn)
                  .Append(" (`").Append(spec.Id).AppendLine("`)");
                sb.AppendLine(spec.CompactContractMarkdown.Trim());
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// AST Backfill for Auto-Repair: inspects a broken/candidate JSON string and diagnostics to find
    /// any capability module the LLM attempted to use so the repair prompt receives its exact contract.
    /// </summary>
    public static IReadOnlyList<CustomWidgetCapabilitySpec> FindBackfillCapabilitiesForRepair(
        string? candidateManifestJson,
        IReadOnlyList<CustomWidgetValidationDiagnostic>? diagnostics)
    {
        var ids = new List<string>();
        string haystack = candidateManifestJson ?? string.Empty;
        if (diagnostics is not null && diagnostics.Count > 0)
        {
            haystack += "\n" + string.Join("\n", diagnostics.Select(static d => d.Message));
        }

        if (string.IsNullOrWhiteSpace(haystack))
        {
            return [];
        }

        foreach (CustomWidgetCapabilitySpec spec in All)
        {
            bool matched =
                spec.TriggerNodeTypes.Any(t => haystack.Contains($"\"{t}\"", StringComparison.OrdinalIgnoreCase) || haystack.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                spec.TriggerSourceOrActionTypes.Any(a => haystack.Contains($"\"{a}\"", StringComparison.OrdinalIgnoreCase)) ||
                spec.TriggerTokenPrefixes.Any(p => haystack.Contains(p, StringComparison.OrdinalIgnoreCase));

            if (matched)
            {
                ids.Add(spec.Id);
            }
        }

        return ExpandWithDependencies(ids);
    }

    /// <summary>
    /// Validates built-in <c>$namespace.*</c> token paths inside expressions and emits actionable
    /// Did-You-Mean hints when an LLM hallucinates a non-existent token path.
    /// </summary>
    public static void ValidateBuiltInTokensInExpression(
        string? expression,
        string jsonPath,
        List<CustomWidgetValidationDiagnostic> diagnostics) =>
        CustomWidgetRuntimeTokens.ValidateBuiltInTokensInExpression(expression, jsonPath, diagnostics);

    private static IReadOnlyList<CustomWidgetCapabilitySpec> MatchFromManifestAst(CustomWidgetManifest manifest)
    {
        var usedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedSourcesAndActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            if (!string.IsNullOrWhiteSpace(src.Type))
            {
                usedSourcesAndActions.Add(src.Type);
            }
        }

        CollectNodePrimitives(manifest.View, usedNodes, usedSourcesAndActions);
        foreach (CustomWidgetNodeSpec popover in manifest.Popovers)
        {
            usedNodes.Add("Popover");
            CollectNodePrimitives(popover, usedNodes, usedSourcesAndActions);
        }
        if (manifest.Script is not null)
        {
            usedSourcesAndActions.Add("script");
        }
        if (manifest.Capsule?.PrimaryAction is not null)
        {
            CollectActionPrimitives(manifest.Capsule.PrimaryAction.OnClick, usedSourcesAndActions);
        }
        if (manifest.OnDrop is not null)
        {
            usedSourcesAndActions.Add("onDrop");
            CollectActionPrimitives(manifest.OnDrop.Actions, usedSourcesAndActions);
        }
        if (manifest.Settings.Any(static s => string.Equals(s.Type, "secret", StringComparison.OrdinalIgnoreCase)))
        {
            usedSourcesAndActions.Add("secret");
        }

        return All
            .Select(spec => (
                Spec: spec,
                Hits: spec.TriggerNodeTypes.Count(usedNodes.Contains) * 2 +
                      spec.TriggerSourceOrActionTypes.Count(usedSourcesAndActions.Contains) * 3))
            .Where(x => x.Hits > 0)
            .OrderByDescending(x => x.Hits)
            .Select(x => x.Spec)
            .ToList();
    }

    private static void CollectNodePrimitives(
        CustomWidgetNodeSpec? node,
        HashSet<string> nodes,
        HashSet<string> actions)
    {
        if (node is null) return;
        if (!string.IsNullOrWhiteSpace(node.Type)) nodes.Add(node.Type);
        if (!string.IsNullOrWhiteSpace(node.Popover)) actions.Add("popover.open");
        if (!string.IsNullOrWhiteSpace(node.Background) || node.LinearGradient is not null || node.RadialGradient is not null) nodes.Add("RawStyling");
        CollectActionPrimitives(node.OnClick, actions);
        CollectActionPrimitives(node.OnSubmit, actions);
        CollectActionPrimitives(node.OnChange, actions);
        CollectActionPrimitives(node.OnDropActions, actions);
        CollectActionPrimitives(node.OnRowClick, actions);
        CollectActionPrimitives(node.OnItemClick, actions);
        CollectActionPrimitives(node.OnPointerDown, actions);
        CollectActionPrimitives(node.OnPointerMove, actions);
        CollectActionPrimitives(node.OnPointerUp, actions);
        CollectActionPrimitives(node.OnReorder, actions);
        CollectActionPrimitives(node.OnInlineCommit, actions);
        if (node.MenuItems is not null)
        {
            foreach (CustomWidgetMenuItemSpec mi in node.MenuItems)
            {
                CollectActionPrimitives(mi.OnClick, actions);
            }
        }
        CollectNodePrimitives(node.ItemTemplate, nodes, actions);
        foreach (CustomWidgetNodeSpec h in node.HoverActions) CollectNodePrimitives(h, nodes, actions);
        foreach (CustomWidgetNodeSpec c in node.Children) CollectNodePrimitives(c, nodes, actions);
    }

    private static void CollectActionPrimitives(
        IReadOnlyList<CustomWidgetActionSpec>? list,
        HashSet<string> actions)
    {
        if (list is null) return;
        foreach (CustomWidgetActionSpec a in list)
        {
            if (!string.IsNullOrWhiteSpace(a.Type)) actions.Add(a.Type);
            CollectActionPrimitives(a.Then, actions);
        }
    }

    /// <summary>
    /// Generates the formal, standardized <c>API_REFERENCE.md</c> specification from the single source of truth.
    /// </summary>
    public static string GenerateApiReferenceMarkdown(bool isChinese) =>
        GenerateApiReferenceMarkdown(isChinese ? "zh-CN" : "en-US");

    /// <summary>
    /// Generates the formal, standardized <c>API_REFERENCE.md</c> specification from the single source of truth,
    /// localized for <paramref name="cultureName"/> via the shared Strings catalog (13 locales).
    /// </summary>
    public static string GenerateApiReferenceMarkdown(string? cultureName = null)
    {
        var sb = new StringBuilder(24576);
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.deskbox-custom-widget-capability-api-ref"));
        sb.AppendLine();
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.generated-from-the-deskbox-runtime-capab"));
        sb.AppendLine();
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.architecture-layout-conventions"));
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.no-duplicate-inner-header-the-host-windo"));
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.consistent-bento-hierarchy-keep-sib"));
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.direct-live-controls-slider-toggle-and-s"));

        CustomWidgetCapabilityCategory[] order =
        [
            CustomWidgetCapabilityCategory.DataSource,
            CustomWidgetCapabilityCategory.Component,
            CustomWidgetCapabilityCategory.Action,
            CustomWidgetCapabilityCategory.ConfigAndSecurity
        ];

        foreach (CustomWidgetCapabilityCategory cat in order)
        {
            sb.AppendLine();
            sb.Append("## ").AppendLine(GetCategoryDisplayName(cat, cultureName));
            foreach (CustomWidgetCapabilitySpec spec in All.Where(c => c.Category == cat))
            {
                sb.AppendLine();
                sb.Append("### ").Append(spec.Emoji).Append(' ').Append(spec.GetTitle(cultureName))
                  .Append(" (`@").Append(spec.Id).AppendLine("`)");
                sb.AppendLine(spec.GetSummary(cultureName));
                if (spec.RequiredCapabilityIds.Count > 0)
                {
                    sb.Append(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.required-modules"))
                      .AppendLine(string.Join(", ", spec.RequiredCapabilityIds.Select(static d => $"`{d}`")));
                }

                if (spec.Members.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.signature-token-property-kind-specificat"));
                    sb.AppendLine("| :--- | :--- | :--- |");
                    foreach (CustomWidgetApiMemberDoc m in spec.Members)
                    {
                        sb.Append("| `").Append(m.Name).Append("` | ").Append(m.Kind).Append(" | ")
                          .Append(spec.GetMemberDescription(m, cultureName)).AppendLine(" |");
                    }
                }

                if (!string.IsNullOrWhiteSpace(spec.GoldenSnippetJson))
                {
                    sb.AppendLine();
                    sb.AppendLine("```json");
                    sb.AppendLine(LocalizeSnippetSample(spec.GoldenSnippetJson.Trim(), cultureName));
                    sb.AppendLine("```");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.expression-syntax-context-variables"));
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.operators-wrap-expressions-in-supports-n"));
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.event-payload-value-inside-onchange-onto"));
        sb.AppendLine(LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Manual.timezone-function-tz-zoneid-format-suppo"));

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string GetCategoryDisplayName(CustomWidgetCapabilityCategory category, bool isChinese) =>
        GetCategoryDisplayName(category, isChinese ? "zh-CN" : "en-US");

    public static string GetCategoryDisplayName(CustomWidgetCapabilityCategory category, string? cultureName) =>
        category switch
        {
            CustomWidgetCapabilityCategory.DataSource => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Category.Sources"),
            CustomWidgetCapabilityCategory.Component => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Category.Components"),
            CustomWidgetCapabilityCategory.Action => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Category.Actions"),
            CustomWidgetCapabilityCategory.ConfigAndSecurity => LocalizationService.TranslateForCulture(cultureName, "CustomWidget.ApiDocs.Category.Security"),
            _ => string.Empty
        };

    /// <summary>
    /// Display-sample terms embedded in golden/reference snippets. Replaced at RENDER time only —
    /// the raw <c>GoldenSnippetJson</c> fixtures stay canonical (tests + agent contracts keep the zh text).
    /// Key list and zh terms are pinned by CustomWidgetApiDocsLocalizationTests.
    /// </summary>
    private static readonly (string Zh, string Key)[] SnippetSampleTerms =
    [
        ("概览", "CustomWidget.ApiDocs.Sample.01"),
        ("区块 A", "CustomWidget.ApiDocs.Sample.02"),
        ("区块 B", "CustomWidget.ApiDocs.Sample.03"),
        ("主音量", "CustomWidget.ApiDocs.Sample.04"),
        ("内存", "CustomWidget.ApiDocs.Sample.05"),
        ("纽约 ${tz('America/New_York', 'HH:mm')} · 伦敦 ${tz('Europe/London', 'HH:mm')}", "CustomWidget.ApiDocs.Sample.06"),
        ("${$weather.condition}  ${$weather.rangeText} · 湿度 ${$weather.humidity}", "CustomWidget.ApiDocs.Sample.07"),
        ("北京 (UTC+8)", "CustomWidget.ApiDocs.Sample.08"),
        ("纽约 (EST/EDT)", "CustomWidget.ApiDocs.Sample.09"),
        ("伦敦 (GMT/BST)", "CustomWidget.ApiDocs.Sample.10"),
        ("东京 (JST)", "CustomWidget.ApiDocs.Sample.11"),
        ("核心负载", "CustomWidget.ApiDocs.Sample.12"),
        ("高占进程", "CustomWidget.ApiDocs.Sample.13"),
        ("当日暂无日程", "CustomWidget.ApiDocs.Sample.14"),
        ("点击上方日期可切换查看", "CustomWidget.ApiDocs.Sample.15"),
        ("拖入图片或点击选择文件", "CustomWidget.ApiDocs.Sample.16"),
        ("本地转换为目标格式，源文件保持不变", "CustomWidget.ApiDocs.Sample.17"),
        ("蓝牙", "CustomWidget.ApiDocs.Sample.18"),
        ("屏幕常亮", "CustomWidget.ApiDocs.Sample.19"),
        ("回收站", "CustomWidget.ApiDocs.Sample.20"),
        ("近 28 天记录热力分布", "CustomWidget.ApiDocs.Sample.21"),
        ("输入新条目…", "CustomWidget.ApiDocs.Sample.22"),
        ("系统音量", "CustomWidget.ApiDocs.Sample.23"),
        ("屏幕亮度", "CustomWidget.ApiDocs.Sample.24"),
        ("音频输出", "CustomWidget.ApiDocs.Sample.25"),
        ("当前剪贴板 (${$clipboard.kindLabel})", "CustomWidget.ApiDocs.Sample.26"),
        ("${$clipboard.length} 字符", "CustomWidget.ApiDocs.Sample.27"),
        ("输入待处理文本（留空则直接处理剪贴板内容）…", "CustomWidget.ApiDocs.Sample.28"),
        ("JSON 格式化", "CustomWidget.ApiDocs.Sample.29"),
        ("Base64 编码", "CustomWidget.ApiDocs.Sample.30"),
        ("时间戳转换", "CustomWidget.ApiDocs.Sample.31"),
        ("下载目录", "CustomWidget.ApiDocs.Sample.32"),
        ("文档目录", "CustomWidget.ApiDocs.Sample.33"),
        ("屏幕截图", "CustomWidget.ApiDocs.Sample.34"),
        ("计算器", "CustomWidget.ApiDocs.Sample.35"),
        ("拖入图片或点击选择", "CustomWidget.ApiDocs.Sample.36"),
        ("自动转为 ${state.format} 并保存在原目录", "CustomWidget.ApiDocs.Sample.37"),
        ("拆解待办事项", "CustomWidget.ApiDocs.Sample.38"),
        ("发送测试提醒", "CustomWidget.ApiDocs.Sample.39"),
        ("定时提醒", "CustomWidget.ApiDocs.Sample.40"),
        ("预设提醒时间已到。", "CustomWidget.ApiDocs.Sample.41"),
        ("账户可用余额：¥ ${state.balance ?? '--'}", "CustomWidget.ApiDocs.Sample.42"),
        ("显示密度", "CustomWidget.ApiDocs.Sample.43"),
        ("紧凑视图", "CustomWidget.ApiDocs.Sample.44"),
        ("详细视图", "CustomWidget.ApiDocs.Sample.45"),
        ("将以下内容拆解为可执行待办事项(items: title)：\\n${state.draft}", "CustomWidget.ApiDocs.Sample.46")
    ];

    /// <summary>Replaces zh display-sample labels inside a snippet with the active culture's rendering.</summary>
    public static string LocalizeSnippetSample(string snippet, string? cultureName)
    {
        if (string.IsNullOrEmpty(snippet))
        {
            return snippet;
        }

        bool isChinese = CustomWidgetRemotePromptService.IsChineseCulture(
            (cultureName ?? LocalizationService.ResolveAmbientCultureName()));
        if (isChinese)
        {
            return snippet;
        }

        foreach ((string zh, string key) in SnippetSampleTerms.OrderByDescending(static t => t.Zh.Length))
        {
            string localized = LocalizationService.TranslateForCulture(cultureName, key);
            if (!string.IsNullOrEmpty(localized) && !string.Equals(localized, zh, StringComparison.Ordinal))
            {
                snippet = snippet.Replace(zh, localized, StringComparison.Ordinal);
            }
        }

        return snippet;
    }

    public const string EmbeddedRegistryResourceName = "DeskBox.Assets.CustomWidgets.capability-registry.json";

    private static IReadOnlyList<CustomWidgetCapabilitySpec> BuildRegistry()
    {
        using Stream stream = OpenEmbeddedRegistryStream();
        using JsonDocument doc = JsonDocument.Parse(stream);
        if (!doc.RootElement.TryGetProperty("capabilities", out JsonElement capsEl) ||
            capsEl.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Invalid capability-registry.json: missing 'capabilities' array.");
        }

        var list = new List<CustomWidgetCapabilitySpec>(capsEl.GetArrayLength());
        foreach (JsonElement item in capsEl.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var spec = new CustomWidgetCapabilitySpec(
                Id: ReadReqString(item, "id"),
                Emoji: ReadReqString(item, "emoji"),
                Glyph: ReadReqString(item, "glyph"),
                Category: ParseCategory(ReadReqString(item, "category")),
                TitleZh: ReadReqString(item, "titleZh"),
                TitleEn: ReadReqString(item, "titleEn"),
                SummaryZh: ReadReqString(item, "summaryZh"),
                SummaryEn: ReadReqString(item, "summaryEn"),
                RequiredCapabilityIds: ReadStringList(item, "requiredCapabilityIds"),
                TriggerNodeTypes: ReadStringList(item, "triggerNodeTypes"),
                TriggerSourceOrActionTypes: ReadStringList(item, "triggerSourceOrActionTypes"),
                TriggerTokenPrefixes: ReadStringList(item, "triggerTokenPrefixes"),
                TriggerKeywords: ReadStringList(item, "triggerKeywords"),
                Members: ReadMemberDocs(item, "members"),
                CompactContractMarkdown: ReadReqString(item, "compactContractMarkdown"),
                GoldenSnippetJson: ReadReqString(item, "goldenSnippetJson"))
            {
                CompositionGroup = ReadReqString(item, "compositionGroup")
            };

#if DESKBOX_STORE
            if (string.Equals(spec.Id, "local-files-and-commands", StringComparison.OrdinalIgnoreCase))
            {
                spec = spec with
                {
                    TitleZh = "本地文件读写",
                    TitleEn = "Local File Sync",
                    SummaryZh = "受格子级权限开关保护的本地 UTF-8/Markdown 文件监听与写入能力（商店版本不支持本地命令行进程调用）。",
                    SummaryEn = "Permission-gated local UTF-8/Markdown file watching and writing (CLI command execution is disabled in the Microsoft Store edition).",
                    TriggerSourceOrActionTypes = spec.TriggerSourceOrActionTypes
                        .Where(static t => !t.StartsWith("command", StringComparison.OrdinalIgnoreCase))
                        .ToList(),
                    TriggerKeywords = spec.TriggerKeywords
                        .Where(static k => k is not ("命令行" or "终端" or "脚本执行" or "进程" or "command.run" or "cli" or "powershell" or "git status" or "docker"))
                        .ToList(),
                    Members = spec.Members
                        .Where(static m => !m.Name.Contains("command", StringComparison.OrdinalIgnoreCase))
                        .ToList(),
                    CompactContractMarkdown =
                        "- Local File Sync (`LocalFileAccess` permission):\n" +
                        "  - Source: `{ \"id\": \"noteFile\", \"type\": \"fileText\", \"path\": \"${$settings.filePath}\", \"stateKey\": \"fileBody\", \"intervalMs\": 5000 }`\n" +
                        "  - Action: `{ \"type\": \"file.writeText\", \"path\": \"${$settings.filePath}\", \"text\": \"${state.fileBody}\", \"append\": false }`\n" +
                        "  - Note: `command` source and `command.run` action are disabled in the Microsoft Store edition."
                };
            }
#endif

            list.Add(spec);
        }

        return list;
    }

    private static Stream OpenEmbeddedRegistryStream()
    {
        Stream? embedded = typeof(CustomWidgetCapabilityRegistry).Assembly
            .GetManifestResourceStream(EmbeddedRegistryResourceName);
        if (embedded is not null)
        {
            return embedded;
        }

        string diskFallback = Path.Combine(AppContext.BaseDirectory, "Assets", "CustomWidgets", "capability-registry.json");
        if (File.Exists(diskFallback))
        {
            return File.OpenRead(diskFallback);
        }

        throw new InvalidOperationException(
            $"Embedded resource '{EmbeddedRegistryResourceName}' was not found in assembly '{typeof(CustomWidgetCapabilityRegistry).Assembly.FullName}'.");
    }

    private static CustomWidgetCapabilityCategory ParseCategory(string raw) =>
        raw switch
        {
            "DataSource" => CustomWidgetCapabilityCategory.DataSource,
            "Component" => CustomWidgetCapabilityCategory.Component,
            "Action" => CustomWidgetCapabilityCategory.Action,
            "ConfigAndSecurity" => CustomWidgetCapabilityCategory.ConfigAndSecurity,
            _ => throw new InvalidOperationException($"Unknown capability category '{raw}' in capability-registry.json.")
        };

    private static string ReadReqString(JsonElement obj, string propName) =>
        obj.TryGetProperty(propName, out JsonElement val) && val.ValueKind == JsonValueKind.String
            ? val.GetString() ?? string.Empty
            : string.Empty;

    private static IReadOnlyList<string> ReadStringList(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        int len = arr.GetArrayLength();
        if (len == 0)
        {
            return [];
        }

        var list = new List<string>(len);
        foreach (JsonElement el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.String && el.GetString() is { } s)
            {
                list.Add(s);
            }
        }

        return list;
    }

    private static IReadOnlyList<CustomWidgetApiMemberDoc> ReadMemberDocs(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        int len = arr.GetArrayLength();
        if (len == 0)
        {
            return [];
        }

        var list = new List<CustomWidgetApiMemberDoc>(len);
        foreach (JsonElement m in arr.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            list.Add(new CustomWidgetApiMemberDoc(
                Name: ReadReqString(m, "name"),
                Kind: ReadReqString(m, "kind"),
                DescriptionZh: ReadReqString(m, "descriptionZh"),
                DescriptionEn: ReadReqString(m, "descriptionEn")));
        }

        return list;
    }

    public static string BuildCompactCoreComponentIndex(bool isChinese = false, bool includeHostStyleRules = true) => isChinese
        ? $$"""
          ## 核心组件与动作速查索引 (Schema v1 扁平元素图)
          - 根字段: `schemaVersion: 1`, `id` (`custom.<slug>`), `name`, `icon` (Emoji；节点 `icon` 统一用语义名如 `add`/`edit`/`delete`/`export`/`folder`/`play`/`pause`), `defaultSize: { width, height }`, `settings`, `state` (原生支持结构化 JSON), `collections`, `sources`, `script`, `popovers` (`{ "id": { "title", "width", "root": "pop_body" } }`), `onDrop`, `capsule`, `root` (根元素 ID), `elements` (扁平字典 `{ "<id>": { "type", "children": ["..."] } }`)（{{(includeHostStyleRules ? "窗口标题栏、圆角、材质与拖拽由宿主统一接管，禁止声明 `chrome` 或自定义拖拽条" : "严禁声明 `chrome`")}}）。
          - 通用属性与视觉: `when`, `Card.tone` (`default|subtle|accent|success|warning|danger`), `Text.style` (`caption|body|bodyStrong|subtitle|title|hero|mono`), `color`, `borderColor`, `borderWidth`, `margin`, `minWidth`, `maxWidth`, `maxHeight`, `fontWeight`, `fontFamily` (`mono`), `opacity`, `shadow` (`none|sm|md`), `aspectRatio`, `contextMenu` (`menuItems`){{(includeHostStyleRules ? "。容器底板与圆角由宿主统一接管，禁止给容器写死不透明 `background`/渐变或中性灰白黑 `textColor`" : string.Empty)}}。
          - 布局与导航: `Stack`, `ZStack` (叠层), `Grid` (`columns`: 轨道如 `*,*`/列数 `"5"`/动态 `${...}`, `rows`, `column`, `row`, `columnSpan`, `rowSpan`), `ItemsGrid` (`items`, `minItemWidth`/`minColumnWidth` 或 `columnsCount`/`columns`, `itemTemplate`), `Wrap`, `Scroll`, `Card` (`tone`, 最多嵌套 2 层), `Tabs` (`bind`, `children`), `SelectorBar` (`bind`, `options`/`children`), `FlipView` (`items`/`children`, `bind`), `SplitView` (`bind`, `paneWidth`, 显式 `pane` 或 `children`: `[paneId, contentId]`), `Expander`, `Popover`, `ModuleStack` (`bind: "settings.modules"`, 子项支持 `compactPair: true` 双列配对), `Spacer`, `Divider`。
          - 富文本、反馈与音视频: `Text`, `RichText` (`spans`, `href` -> `url.open`), `CodeBlock` (`text`/`bind`, `language`, `copyable`, `autoScrollTail`), `Markdown` (`text`, `bind`), `Image`, `MediaPlayer` (`src`, `poster`, `autoPlay`, `loop`, `muted`, `volume`, `showTransport`), `Badge`, `InfoBadge`, `InfoBar` (`severity`, `title`, `subtitle`), `TeachingTip` (`id`, `target`, `title`, `subtitle`), `KeyValueRow`, `EmptyState`.
          - 交互控件: `Input`, `TextArea`, `AutoSuggestInput`, `Select`, `RadioGroup`, `CheckBox`, `Toggle`, `Slider`, `NumberBox`, `Rating`, `DatePicker`, `TimePicker`, `ColorPicker`, `Segmented`, `Button`, `SplitButton` (`menuItems`), `DropDownButton` (`menuItems`), `IconButton`, `ControlTile`, `DropZone`.
          - 图表与矢量画布 2.0: `Canvas` (`viewBox`, `elements`/`elementsSource`: `rect|circle|ellipse|arc|line|polyline|polygon|path|text`, `onItemClick`, `onPointerDown|Move|Up`), `Table`, `DonutChart`, `Chart` (`line|bar|area`), `List`, `RingGauge`, `RadialGauge` (`bind`/`progress`/`value`, `startAngle`, `sweepAngle`, `showTicks`+`tickStep`, `animateChange`, `isInteractive`+`onChange`), `BarList`, `Sparkline`, `HeatmapGrid`, `CalendarGrid`, `ProgressBar`, `ProgressRing`, `ProgressRow`, `FileChip`, `StatCard`.
          """
#if DESKBOX_STORE
        + "\n- 数据源与虚拟集合: `weather`, `calendar`, `clock`, `systemMetrics` (`${$sys.topProcesses}`), `storageDrives`, `deviceState`, `music`, `todo`, `clipboard`, `aiMonitor` (`${$ai.providers}`, `${$ai.dailyTrend}`), `countdown` (`${$countdown.items}`, `${$countdown.progressBars}`), `pomodoro`, `httpJson`, `rss`, `fileText`, `localFolder`。\n- 常用动作: `state.set|toggle|increment`, `collection.prepend|append|update|remove|clear|reorder|exportMarkdown`, `popover.open|close|toggle`, `tip.open|close|toggle`, `audio.cue`, `audio.play|pause|stop|setVolume|stopAll` (`noise:white|pink|brown|rain`), `script.run` (`fn`, `arg`, `args` -> `ctx.args`, `ctx.event`, `ctx.dispatch`), `http.request`, `file.writeText`, `file.listDir`, `file.manage` (`copy|move|rename|recycle`), `clipboard.captureImage`, `job.cancel`, `ai.run`, `source.refresh`, `system.control`, `file.transform`, `file.pick`, `clipboard.copy`, `url.open`, `file.open`, `file.reveal`, `toast.show`, `todo.add|toggle|remove|cyclePriority|clearCompleted`, `pomodoro.*`, `countdown.upsert|pin|delete`。"
#else
        + "\n- 数据源与虚拟集合: `weather`, `calendar`, `clock`, `systemMetrics` (`${$sys.topProcesses}`), `storageDrives`, `deviceState`, `music`, `todo`, `clipboard`, `aiMonitor` (`${$ai.providers}`, `${$ai.dailyTrend}`), `countdown` (`${$countdown.items}`, `${$countdown.progressBars}`), `pomodoro`, `httpJson`, `rss`, `fileText`, `localFolder`, `command`。\n- 常用动作: `state.set|toggle|increment`, `collection.prepend|append|update|remove|clear|reorder|exportMarkdown`, `popover.open|close|toggle`, `tip.open|close|toggle`, `audio.cue`, `audio.play|pause|stop|setVolume|stopAll` (`noise:white|pink|brown|rain`), `script.run` (`fn`, `arg`, `args` -> `ctx.args`, `ctx.event`, `ctx.dispatch`), `http.request`, `file.writeText`, `file.listDir`, `file.manage` (`copy|move|rename|recycle`), `clipboard.captureImage`, `command.run` (`longRunning`, `onProgress`, `onComplete`), `job.cancel`, `ai.run`, `source.refresh`, `system.control`, `file.transform`, `file.pick`, `clipboard.copy`, `url.open`, `file.open`, `file.reveal`, `toast.show`, `todo.add|toggle|remove|cyclePriority|clearCompleted`, `pomodoro.*`, `countdown.upsert|pin|delete`。"
#endif
        : $$"""
          ## Component Quick Index (All Supported Schema v1 Flat Element Graph Nodes)
          - Root Keys: `schemaVersion: 1`, `id` (`custom.<slug>`), `name`, `icon` (Emoji; node `icon` uses semantic names like `add`/`edit`/`delete`/`export`/`folder`/`play`/`pause`), `defaultSize: { width, height }`, `settings`, `state` (scalars + structured JSON arrays/objects), `collections`, `sources`, `script`, `popovers` (`{ "id": { "title", "width", "root": "pop_body" } }`), `onDrop`, `capsule`, `root` (root element ID), `elements` (flat dictionary `{ "<id>": { "type", "children": ["..."] } }`) ({{(includeHostStyleRules ? "window chrome, corner radius, material, and dragging are host-managed; never emit `chrome` or custom drag bars" : "never emit `chrome`")}}).
          - Common Props & Visuals: `when`, `Card.tone` (`default|subtle|accent|success|warning|danger`), `Text.style` (`caption|body|bodyStrong|subtitle|title|hero|mono`), `color`, `borderColor`, `borderWidth`, `margin`, `minWidth`, `maxWidth`, `maxHeight`, `fontWeight`, `fontFamily` (`mono`), `opacity`, `shadow` (`none|sm|md`), `aspectRatio`, `contextMenu` (`menuItems`){{(includeHostStyleRules ? ". Layout containers inherit host glass and corner radius — do not set opaque `background`/gradients or neutral hardcoded `textColor`" : string.Empty)}}.
          - Layout & Navigation: `Stack`, `ZStack` (overlay), `Grid` (`columns` track specs e.g. `*,*`/count `"5"`/dynamic `${...}`, `rows`, `column`, `row`, `columnSpan`, `rowSpan`), `ItemsGrid` (`items`, `minItemWidth`/`minColumnWidth` or `columnsCount`/`columns`, `itemTemplate`), `Wrap`, `Scroll`, `Card` (`tone`, max 2 nested levels!), `Tabs` (`bind`, `children`), `SelectorBar` (`bind`, `options`/`children`), `FlipView` (`items`/`children`, `bind`), `SplitView` (`bind`, `paneWidth`, explicit `pane` or `children`: `[paneId, contentId]`), `Expander`, `Popover`, `ModuleStack` (`bind: "settings.modules"`, child `compactPair: true` pairs narrow cards side-by-side), `Spacer`, `Divider`.
          - Rich Text, Feedback & Media: `Text`, `RichText` (`spans`, `href` -> `url.open`), `CodeBlock` (`text`/`bind`, `language`, `copyable`, `autoScrollTail`), `Markdown` (`text`, `bind`), `Image`, `MediaPlayer` (`src`, `poster`, `autoPlay`, `loop`, `muted`, `volume`, `showTransport`), `Badge`, `InfoBadge`, `InfoBar` (`severity`, `title`, `subtitle`), `TeachingTip` (`id`, `target`, `title`, `subtitle`), `KeyValueRow`, `EmptyState`.
          - Inputs & Controls: `Input`, `TextArea`, `AutoSuggestInput`, `Select`, `RadioGroup`, `CheckBox`, `Toggle`, `Slider`, `NumberBox`, `Rating`, `DatePicker`, `TimePicker`, `ColorPicker`, `Segmented`, `Button`, `SplitButton` (`menuItems`), `DropDownButton` (`menuItems`), `IconButton`, `ControlTile`, `DropZone`.
          - Vector Canvas 2.0, Charts & Data: `Canvas` (`viewBox`, `elements`/`elementsSource`: `rect|circle|ellipse|arc|line|polyline|polygon|path|text`, `onItemClick`, `onPointerDown|Move|Up`), `Table`, `DonutChart`, `Chart` (`line|bar|area`), `List`, `RingGauge`, `RadialGauge` (`bind`/`progress`/`value`, `startAngle`, `sweepAngle`, `showTicks`+`tickStep`, `animateChange`, `isInteractive`+`onChange`), `BarList`, `Sparkline`, `HeatmapGrid`, `CalendarGrid`, `ProgressBar`, `ProgressRing`, `ProgressRow`, `FileChip`, `StatCard`.
          """
#if DESKBOX_STORE
        + "\n- Sources & Virtual Collections: `weather`, `calendar`, `clock`, `systemMetrics` (`${$sys.topProcesses}`), `storageDrives`, `deviceState`, `music`, `todo`, `clipboard`, `aiMonitor` (`${$ai.providers}`, `${$ai.dailyTrend}`), `countdown` (`${$countdown.items}`, `${$countdown.progressBars}`), `pomodoro`, `httpJson`, `rss`, `fileText`, `localFolder`.\n- Actions: `state.set|toggle|increment`, `collection.prepend|append|update|remove|clear|reorder|exportMarkdown`, `popover.open|close|toggle`, `tip.open|close|toggle`, `audio.cue`, `audio.play|pause|stop|setVolume|stopAll` (`noise:white|pink|brown|rain`), `script.run` (`fn`, `arg`, `args` -> `ctx.args`, `ctx.event`, `ctx.dispatch`), `http.request`, `file.writeText`, `file.listDir`, `file.manage` (`copy|move|rename|recycle`), `clipboard.captureImage`, `job.cancel`, `ai.run`, `source.refresh`, `system.control`, `file.transform`, `file.pick`, `clipboard.copy`, `url.open`, `file.open`, `file.reveal`, `toast.show`, `todo.add|toggle|remove|cyclePriority|clearCompleted`, `pomodoro.*`, `countdown.upsert|pin|delete`.";
#else
        + "\n- Sources & Virtual Collections: `weather`, `calendar`, `clock`, `systemMetrics` (`${$sys.topProcesses}`), `storageDrives`, `deviceState`, `music`, `todo`, `clipboard`, `aiMonitor` (`${$ai.providers}`, `${$ai.dailyTrend}`), `countdown` (`${$countdown.items}`, `${$countdown.progressBars}`), `pomodoro`, `httpJson`, `rss`, `fileText`, `localFolder`, `command`.\n- Actions: `state.set|toggle|increment`, `collection.prepend|append|update|remove|clear|reorder|exportMarkdown`, `popover.open|close|toggle`, `tip.open|close|toggle`, `audio.cue`, `audio.play|pause|stop|setVolume|stopAll` (`noise:white|pink|brown|rain`), `script.run` (`fn`, `arg`, `args` -> `ctx.args`, `ctx.event`, `ctx.dispatch`), `http.request`, `file.writeText`, `file.listDir`, `file.manage` (`copy|move|rename|recycle`), `clipboard.captureImage`, `command.run` (`longRunning`, `onProgress`, `onComplete`), `job.cancel`, `ai.run`, `source.refresh`, `system.control`, `file.transform`, `file.pick`, `clipboard.copy`, `url.open`, `file.open`, `file.reveal`, `toast.show`, `todo.add|toggle|remove|cyclePriority|clearCompleted`, `pomodoro.*`, `countdown.upsert|pin|delete`.";
#endif
}
