namespace DeskBox.Models;

public readonly record struct ClipboardTabEntry(string Id, bool Enabled);

/// <summary>
/// Settings slice for the first-party Clipboard feature widget and its
/// background capture daemon.
/// </summary>
public sealed class ClipboardSettingsSlice
{
    public const string ClickCopyOnSinglePasteOnDouble = "CopyOnSinglePasteOnDouble";
    public const string ClickPasteOnSingle = "PasteOnSingle";
    public const string ClickCopyOnDouble = "CopyOnDouble";
    public const string ClickCopyOnSingle = "CopyOnSingle";

    public const string DensityCompact = "Compact";
    public const string DensityStandard = "Standard";
    public const string DensityRelaxed = "Relaxed";

    public const string ViewModeStandard = "Standard";
    public const string ViewModeCompact = "Compact";

    public const string TabDisplayIconAndText = "IconAndText";
    public const string TabDisplayIconOnly = "IconOnly";
    public const string TabDisplayTextOnly = "TextOnly";

    public const string TabAll = "All";
    public const string TabText = "Text";
    public const string TabImages = "Images";
    public const string TabLinks = "Links";
    public const string TabFiles = "Files";
    public const string TabPinned = "Pinned";

    public const string DefaultTabLayout = "All,Text,Images,Links,Files,Pinned";

    public static IReadOnlyList<string> DefaultTabOrder { get; } =
    [
        TabAll,
        TabText,
        TabImages,
        TabLinks,
        TabFiles,
        TabPinned
    ];

    public static string NormalizeTabDisplayMode(string? mode)
    {
        if (string.Equals(mode?.Trim(), TabDisplayIconOnly, StringComparison.OrdinalIgnoreCase))
        {
            return TabDisplayIconOnly;
        }

        if (string.Equals(mode?.Trim(), TabDisplayTextOnly, StringComparison.OrdinalIgnoreCase))
        {
            return TabDisplayTextOnly;
        }

        return TabDisplayIconAndText;
    }

    public static IReadOnlyList<ClipboardTabEntry> ParseTabLayout(string? rawLayout)
    {
        var entries = new List<ClipboardTabEntry>(DefaultTabOrder.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(rawLayout))
        {
            foreach (string rawToken in rawLayout.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                bool enabled = true;
                string token = rawToken;
                if (token.StartsWith('-') || token.StartsWith('!'))
                {
                    enabled = false;
                    token = token[1..].Trim();
                }
                else if (token.StartsWith('+'))
                {
                    token = token[1..].Trim();
                }

                string? canonicalId = TryResolveCanonicalTabId(token);
                if (canonicalId is null || !seen.Add(canonicalId))
                {
                    continue;
                }

                entries.Add(new ClipboardTabEntry(canonicalId, enabled));
            }
        }

        foreach (string defaultId in DefaultTabOrder)
        {
            if (seen.Add(defaultId))
            {
                entries.Add(new ClipboardTabEntry(defaultId, Enabled: true));
            }
        }

        if (entries.Count > 0 && entries.All(static entry => !entry.Enabled))
        {
            entries[0] = entries[0] with { Enabled = true };
        }

        return entries;
    }

    public static string SerializeTabLayout(IReadOnlyList<ClipboardTabEntry> entries)
    {
        if (entries is null || entries.Count == 0)
        {
            return DefaultTabLayout;
        }

        string candidate = string.Join(
            ",",
            entries.Select(static entry => entry.Enabled ? entry.Id : "-" + entry.Id));
        IReadOnlyList<ClipboardTabEntry> normalized = ParseTabLayout(candidate);
        return string.Join(
            ",",
            normalized.Select(static entry => entry.Enabled ? entry.Id : "-" + entry.Id));
    }

    public static string NormalizeTabLayout(string? rawLayout) =>
        SerializeTabLayout(ParseTabLayout(rawLayout));

    public static bool IsDefaultTabLayout(string? rawLayout) =>
        string.Equals(NormalizeTabLayout(rawLayout), DefaultTabLayout, StringComparison.Ordinal);

    public static string SetTabEnabled(string? rawLayout, string tabId, bool enabled)
    {
        string? canonicalId = TryResolveCanonicalTabId(tabId);
        IReadOnlyList<ClipboardTabEntry> current = ParseTabLayout(rawLayout);
        if (canonicalId is null)
        {
            return SerializeTabLayout(current);
        }

        int enabledCount = current.Count(static entry => entry.Enabled);
        var updated = new List<ClipboardTabEntry>(current.Count);
        foreach (ClipboardTabEntry entry in current)
        {
            if (string.Equals(entry.Id, canonicalId, StringComparison.Ordinal))
            {
                bool nextEnabled = enabled || (entry.Enabled && enabledCount <= 1);
                updated.Add(entry with { Enabled = nextEnabled });
            }
            else
            {
                updated.Add(entry);
            }
        }

        return SerializeTabLayout(updated);
    }

    public static string MoveTab(string? rawLayout, string tabId, int delta)
    {
        string? canonicalId = TryResolveCanonicalTabId(tabId);
        List<ClipboardTabEntry> list = [.. ParseTabLayout(rawLayout)];
        if (canonicalId is null || delta == 0)
        {
            return SerializeTabLayout(list);
        }

        int index = list.FindIndex(entry => string.Equals(entry.Id, canonicalId, StringComparison.Ordinal));
        int targetIndex = index + delta;
        if (index < 0 || targetIndex < 0 || targetIndex >= list.Count)
        {
            return SerializeTabLayout(list);
        }

        ClipboardTabEntry moving = list[index];
        list.RemoveAt(index);
        list.Insert(targetIndex, moving);
        return SerializeTabLayout(list);
    }

    public static string ReorderTabs(string? rawLayout, IReadOnlyList<string> orderedTabIds)
    {
        IReadOnlyList<ClipboardTabEntry> current = ParseTabLayout(rawLayout);
        if (orderedTabIds is null || orderedTabIds.Count == 0)
        {
            return SerializeTabLayout(current);
        }

        var enabledMap = current.ToDictionary(
            static entry => entry.Id,
            static entry => entry.Enabled,
            StringComparer.OrdinalIgnoreCase);

        var reordered = new List<ClipboardTabEntry>(current.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string rawId in orderedTabIds)
        {
            string? canonicalId = TryResolveCanonicalTabId(rawId);
            if (canonicalId is null || !seen.Add(canonicalId))
            {
                continue;
            }

            bool isEnabled = !enabledMap.TryGetValue(canonicalId, out bool existingEnabled) || existingEnabled;
            reordered.Add(new ClipboardTabEntry(canonicalId, isEnabled));
        }

        foreach (ClipboardTabEntry remaining in current)
        {
            if (seen.Add(remaining.Id))
            {
                reordered.Add(remaining);
            }
        }

        return SerializeTabLayout(reordered);
    }

    public static string GetTabGlyph(string tabId) => TryResolveCanonicalTabId(tabId) switch
    {
        TabText => "\uE8E4",
        TabImages => "\uEB9F",
        TabLinks => "\uE71B",
        TabFiles => "\uE8B7",
        TabPinned => "\uE718",
        _ => "\uE71D"
    };

    public static string GetTabLocalizationKey(string tabId) => TryResolveCanonicalTabId(tabId) switch
    {
        TabText => "Clipboard.Filter.Text",
        TabImages => "Clipboard.Filter.Images",
        TabLinks => "Clipboard.Filter.Links",
        TabFiles => "Clipboard.Filter.Files",
        TabPinned => "Clipboard.Filter.Pinned",
        _ => "Clipboard.Filter.All"
    };

    private static string? TryResolveCanonicalTabId(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        ReadOnlySpan<char> trimmed = token.AsSpan().Trim();
        foreach (string candidate in DefaultTabOrder)
        {
            if (trimmed.Equals(candidate.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    public const string SecretProtectionMask = "Mask";
    public const string SecretProtectionSkip = "Skip";
    public const string SecretProtectionNone = "None";

    public const string PopupFollowCaret = "FollowCaret";
    public const string PopupFollowMouse = "FollowMouse";
    public const string PopupScreenCenter = "ScreenCenter";

    public const string DoubleCopyNone = "None";
    public const string DoubleCopyPin = "Pin";
    public const string DoubleCopyQuickCapture = "SaveToQuickCapture";

    public const int DefaultHotkeyKey = 0x56; // VirtualKey.V (Alt + V)

    public bool ClipboardCaptureText { get; set; } = true;

    public bool ClipboardCaptureImages { get; set; } = true;

    public bool ClipboardCaptureFiles { get; set; } = true;

    public int ClipboardMaxImageSizeMb { get; set; } = 32;

    public int ClipboardMaxTextSizeMb { get; set; } = 4;

    public string ClipboardClickBehavior { get; set; } = ClickCopyOnSinglePasteOnDouble;

    public bool ClipboardDefaultPastePlainText { get; set; }

    public bool ClipboardMoveToTopOnReuse { get; set; }

    public string ClipboardViewMode { get; set; } = ViewModeStandard;

    public string ClipboardTabDisplayMode { get; set; } = TabDisplayIconAndText;

    public string ClipboardTabLayout { get; set; } = DefaultTabLayout;

    public string ClipboardCardDensity { get; set; } = DensityStandard;

    public bool ClipboardShowSourceApp { get; set; } = true;

    public bool ClipboardIgnoreSystemPrivacyFormats { get; set; } = true;

    public string ClipboardSensitiveProtectionMode { get; set; } = SecretProtectionMask;

    public List<string> ClipboardExcludedApps { get; set; } = [];

    public int ClipboardMaxItems { get; set; } = 500;

    public int ClipboardRetentionDays { get; set; } = 30;

    public bool ClipboardHotkeyEnabled { get; set; } = true;

    public HotkeyActivationKind ClipboardHotkeyActivationKind { get; set; } = HotkeyActivationKind.Chord;

    public int ClipboardHotkeyModifiers { get; set; } = (int)HotkeyModifierKeys.Alt;

    public int ClipboardHotkeyKey { get; set; } = DefaultHotkeyKey;

    public string ClipboardPopupPlacementMode { get; set; } = PopupFollowCaret;

    public string ClipboardDoubleCopyAction { get; set; } = DoubleCopyNone;

    public bool ClipboardFetchUrlTitles { get; set; }
}
