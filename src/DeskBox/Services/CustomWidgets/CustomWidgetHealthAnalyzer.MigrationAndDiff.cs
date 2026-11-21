using DeskBox.Models;

namespace DeskBox.Services;

public sealed record CustomWidgetDroppedCollectionRisk(
    string OldCollectionKey,
    int LiveRecordCount,
    string? SuggestedNewCollectionKey);

public sealed record CustomWidgetDataSafetyReport(
    IReadOnlyList<CustomWidgetDroppedCollectionRisk> DroppedCollections,
    IReadOnlyList<string> DroppedNonEmptyStateKeys,
    IReadOnlyList<string>? PromotedStateKeys = null)
{
    public bool HasDataLossRisk => DroppedCollections.Count > 0 || DroppedNonEmptyStateKeys.Count > 0;
}

public sealed record CustomWidgetStructuralDiff(
    IReadOnlyList<string> AddedNodeTypes,
    IReadOnlyList<string> RemovedNodeTypes,
    IReadOnlyList<string> AddedCollections,
    IReadOnlyList<string> RemovedCollections,
    IReadOnlyList<string> AddedStateKeys,
    IReadOnlyList<string> RemovedStateKeys,
    IReadOnlyList<string> DroppedLocales,
    CustomWidgetSizeSpec? OldSize,
    CustomWidgetSizeSpec? NewSize,
    bool AddedScript = false,
    bool UpdatedScript = false,
    bool UpdatedCapsule = false)
{
    public bool SizeChanged =>
        OldSize is not null && NewSize is not null &&
        (OldSize.Width != NewSize.Width || OldSize.Height != NewSize.Height);

    public bool HasUnexpectedRegression =>
        DroppedLocales.Count > 0 || RemovedCollections.Count > 0;
}

public static partial class CustomWidgetHealthAnalyzer
{
    private static bool IsEphemeralRuntimeStateKey(string key) =>
        CustomWidgetRuntimeTokens.IsEphemeralStateKey(key);

    public static HashSet<string> GetHostMirroredCollectionNames(CustomWidgetManifest? manifest)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (manifest is null)
        {
            return result;
        }

        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            if (string.Equals(src.Type, "todo", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(string.IsNullOrWhiteSpace(src.TargetCollection) ? "todos" : src.TargetCollection.Trim());
            }
            else if (string.Equals(src.Type, "storageDrives", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(string.IsNullOrWhiteSpace(src.TargetCollection) ? "drives" : src.TargetCollection.Trim());
            }
            else if (string.Equals(src.Type, "weather", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(string.IsNullOrWhiteSpace(src.TargetCollection) ? "forecast" : src.TargetCollection.Trim());
            }
        }

        if (CapsuleContainsToken(manifest.Capsule, "todo."))
        {
            result.Add("todos");
        }
        if (CapsuleContainsToken(manifest.Capsule, "drives."))
        {
            result.Add("drives");
        }
        if (CapsuleContainsToken(manifest.Capsule, "weather."))
        {
            result.Add("forecast");
        }

        return result;
    }

    private static bool CapsuleContainsToken(CustomWidgetCapsuleSpec? capsule, string tokenPrefix)
    {
        if (capsule is null)
        {
            return false;
        }

        bool Has(string? s) => !string.IsNullOrEmpty(s) && s.Contains(tokenPrefix, StringComparison.OrdinalIgnoreCase);
        return Has(capsule.Title) ||
               Has(capsule.Summary) ||
               Has(capsule.Glyph) ||
               Has(capsule.Badge) ||
               Has(capsule.BadgeIsWarning) ||
               Has(capsule.Progress) ||
               Has(capsule.PrimaryAction?.Icon) ||
               Has(capsule.PrimaryAction?.Tooltip);
    }

    public static CustomWidgetDataSafetyReport EvaluateStoreDataSafety(
        CustomWidgetManifest? previousManifest,
        CustomWidgetStorePayload? liveStore,
        CustomWidgetManifest candidateManifest)
    {
        ArgumentNullException.ThrowIfNull(candidateManifest);
        if (liveStore is null)
        {
            return new([], []);
        }

        var candidateCollections = new HashSet<string>(candidateManifest.Collections.Keys, StringComparer.Ordinal);
        CollectReferencedCollectionNames(candidateManifest, candidateCollections);

        var oldDeclaredCollections = previousManifest is not null
            ? new HashSet<string>(previousManifest.Collections.Keys, StringComparer.Ordinal)
            : new HashSet<string>(liveStore.Collections.Keys, StringComparer.Ordinal);

        List<string> newlyIntroducedCollections = candidateCollections
            .Where(c => !oldDeclaredCollections.Contains(c))
            .ToList();

        var droppedCollections = new List<CustomWidgetDroppedCollectionRisk>();
        HashSet<string> hostMirroredCollections = GetHostMirroredCollectionNames(previousManifest);
        foreach ((string colKey, List<CustomWidgetRecord> records) in liveStore.Collections)
        {
            if (hostMirroredCollections.Contains(colKey))
            {
                continue;
            }

            int activeCount = records.Count(r => !r.IsDeleted);
            if (activeCount <= 0)
            {
                continue;
            }

            if (!candidateCollections.Contains(colKey))
            {
                string? suggestedTarget = newlyIntroducedCollections.Count == 1
                    ? newlyIntroducedCollections[0]
                    : null;
                droppedCollections.Add(new CustomWidgetDroppedCollectionRisk(colKey, activeCount, suggestedTarget));
            }
        }

        var candidateSettingKeys = new HashSet<string>(
            candidateManifest.Settings
                .Where(static s => !string.IsNullOrWhiteSpace(s.Key) &&
                                   !string.Equals(s.Type, "secret", StringComparison.OrdinalIgnoreCase))
                .Select(static s => s.Key),
            StringComparer.Ordinal);

        var candidateStateKeys = new HashSet<string>(candidateManifest.State.Keys, StringComparer.Ordinal);
        CollectBoundOrWrittenStateKeys(candidateManifest, candidateStateKeys);

        var droppedStateKeys = new List<string>();
        var promotedStateKeys = new List<string>();
        foreach ((string stateKey, string val) in liveStore.State)
        {
            if (string.IsNullOrWhiteSpace(val) || IsEphemeralRuntimeStateKey(stateKey))
            {
                continue;
            }

            // Unmodified manifest default values are not user-recorded data.
            if (previousManifest is not null &&
                previousManifest.State.TryGetValue(stateKey, out string? prevDefault) &&
                string.Equals(val, prevDefault, StringComparison.Ordinal))
            {
                continue;
            }

            // Only flag if previous manifest declared/used it and candidate completely dropped it
            bool wasInOld = previousManifest is null ||
                            previousManifest.State.ContainsKey(stateKey);
            if (wasInOld && !candidateStateKeys.Contains(stateKey))
            {
                if (candidateSettingKeys.Contains(stateKey))
                {
                    promotedStateKeys.Add(stateKey);
                    continue;
                }

                droppedStateKeys.Add(stateKey);
            }
        }

        return new CustomWidgetDataSafetyReport(droppedCollections, droppedStateKeys, promotedStateKeys);
    }

    public static async Task MigratePromotedStateKeysToSettingsAsync(
        CustomWidgetDataStore store,
        CustomWidgetManifest? previousManifest,
        CustomWidgetManifest candidateManifest)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(candidateManifest);

        var candidateSettingsByKey = new Dictionary<string, CustomWidgetSettingDefinition>(StringComparer.Ordinal);
        foreach (CustomWidgetSettingDefinition s in candidateManifest.Settings)
        {
            if (!string.IsNullOrWhiteSpace(s.Key) &&
                !string.Equals(s.Type, "secret", StringComparison.OrdinalIgnoreCase))
            {
                candidateSettingsByKey[s.Key] = s;
            }
        }

        if (candidateSettingsByKey.Count == 0)
        {
            return;
        }

        var candidateStateKeys = new HashSet<string>(candidateManifest.State.Keys, StringComparer.Ordinal);
        CollectBoundOrWrittenStateKeys(candidateManifest, candidateStateKeys);

        CustomWidgetStorePayload payload = await store.LoadAsync();
        bool changed = false;
        foreach (string stateKey in payload.State.Keys.ToList())
        {
            if (candidateStateKeys.Contains(stateKey) ||
                !candidateSettingsByKey.ContainsKey(stateKey))
            {
                continue;
            }

            string val = payload.State[stateKey];
            bool wasDefault = previousManifest is not null &&
                              previousManifest.State.TryGetValue(stateKey, out string? prevDefault) &&
                              string.Equals(val, prevDefault, StringComparison.Ordinal);
            if (!string.IsNullOrWhiteSpace(val) &&
                !wasDefault &&
                !payload.Settings.ContainsKey(stateKey))
            {
                payload.Settings[stateKey] = val;
            }

            payload.State.Remove(stateKey);
            payload.StateJson.Remove(stateKey);
            changed = true;
        }

        if (changed)
        {
            await store.SaveAsync(payload);
        }
    }

    public static async Task MigrateRenamedCollectionsAsync(
        CustomWidgetDataStore store,
        IReadOnlyList<(string OldCollectionKey, string NewCollectionKey)> renames)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (renames.Count == 0)
        {
            return;
        }

        CustomWidgetStorePayload payload = await store.LoadAsync();
        bool changed = false;
        foreach ((string oldKey, string newKey) in renames)
        {
            if (string.IsNullOrWhiteSpace(oldKey) ||
                string.IsNullOrWhiteSpace(newKey) ||
                string.Equals(oldKey, newKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (payload.Collections.TryGetValue(oldKey, out List<CustomWidgetRecord>? existingRecords) &&
                existingRecords.Count > 0)
            {
                if (!payload.Collections.TryGetValue(newKey, out List<CustomWidgetRecord>? dest))
                {
                    dest = [];
                    payload.Collections[newKey] = dest;
                }

                var existingIds = new HashSet<string>(dest.Select(r => r.Id), StringComparer.Ordinal);
                foreach (CustomWidgetRecord rec in existingRecords)
                {
                    if (existingIds.Add(rec.Id))
                    {
                        dest.Add(rec);
                    }
                }

                payload.Collections.Remove(oldKey);
                changed = true;
            }
        }

        if (changed)
        {
            await store.SaveAsync(payload);
        }
    }

    public static CustomWidgetStructuralDiff ComputeStructuralDiff(
        CustomWidgetManifest? previous,
        CustomWidgetManifest current,
        string? previousLogicJs = null,
        string? currentLogicJs = null)
    {
        ArgumentNullException.ThrowIfNull(current);
        bool hadScript = previous?.Script is not null || !string.IsNullOrWhiteSpace(previousLogicJs);
        bool hasScript = current.Script is not null || !string.IsNullOrWhiteSpace(currentLogicJs);
        bool addedScript = !hadScript && hasScript;

        if (previous is null)
        {
            var initialNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectNodeTypes(current, initialNodes);
            return new CustomWidgetStructuralDiff(
                AddedNodeTypes: initialNodes.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
                RemovedNodeTypes: [],
                AddedCollections: current.Collections.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                RemovedCollections: [],
                AddedStateKeys: current.State.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList(),
                RemovedStateKeys: [],
                DroppedLocales: [],
                OldSize: null,
                NewSize: current.DefaultSize,
                AddedScript: hasScript);
        }

        var oldNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectNodeTypes(previous, oldNodes);
        CollectNodeTypes(current, newNodes);

        List<string> addedNodes = newNodes.Except(oldNodes, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();
        List<string> removedNodes = oldNodes.Except(newNodes, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToList();

        List<string> addedCols = current.Collections.Keys.Except(previous.Collections.Keys, StringComparer.Ordinal).OrderBy(x => x).ToList();
        List<string> removedCols = previous.Collections.Keys.Except(current.Collections.Keys, StringComparer.Ordinal).OrderBy(x => x).ToList();

        List<string> addedState = current.State.Keys.Except(previous.State.Keys, StringComparer.Ordinal).OrderBy(x => x).ToList();
        List<string> removedState = previous.State.Keys.Except(current.State.Keys, StringComparer.Ordinal).OrderBy(x => x).ToList();

        List<string> droppedLocales = previous.I18n.Keys
            .Where(loc => !current.I18n.ContainsKey(loc))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        bool updatedScript = hadScript && hasScript &&
                             (!string.Equals(previousLogicJs?.Trim(), currentLogicJs?.Trim(), StringComparison.Ordinal) ||
                              previous.Script != current.Script);
        bool updatedCapsule = !AreCapsulesEqual(previous.Capsule, current.Capsule);

        return new CustomWidgetStructuralDiff(
            AddedNodeTypes: addedNodes,
            RemovedNodeTypes: removedNodes,
            AddedCollections: addedCols,
            RemovedCollections: removedCols,
            AddedStateKeys: addedState,
            RemovedStateKeys: removedState,
            DroppedLocales: droppedLocales,
            OldSize: previous.DefaultSize,
            NewSize: current.DefaultSize,
            AddedScript: addedScript,
            UpdatedScript: updatedScript,
            UpdatedCapsule: updatedCapsule);
    }

    public static IReadOnlyList<string> FormatDiffPills(
        CustomWidgetStructuralDiff diff,
        string? cultureName)
    {
        var pills = new List<string>(6);
        if (diff.AddedScript)
        {
            pills.Add(LocalizationService.FormatForCulture(
                cultureName,
                "CustomWidget.Workshop.Diff.AddedScript"));
        }
        else if (diff.UpdatedScript)
        {
            pills.Add(LocalizationService.FormatForCulture(
                cultureName,
                "CustomWidget.Workshop.Diff.UpdatedScript"));
        }

        if (diff.UpdatedCapsule)
        {
            pills.Add(LocalizationService.FormatForCulture(
                cultureName,
                "CustomWidget.Workshop.Diff.UpdatedCapsule"));
        }

        if (diff.AddedNodeTypes.Count > 0)
        {
            string joined = string.Join(", ", diff.AddedNodeTypes.Take(3));
            pills.Add($"+{joined}");
        }

        if (diff.AddedCollections.Count > 0)
        {
            pills.Add(LocalizationService.FormatForCulture(
                cultureName,
                "CustomWidget.Health.CollectDiffAddedFormat",
                string.Join(",", diff.AddedCollections)));
        }

        if (diff.SizeChanged && diff.NewSize is not null)
        {
            pills.Add($"{diff.NewSize.Width}×{diff.NewSize.Height}");
        }

        if (diff.DroppedLocales.Count > 0)
        {
            pills.Add(LocalizationService.FormatForCulture(
                cultureName,
                "CustomWidget.Health.MissingI18nFormat",
                string.Join(",", diff.DroppedLocales)));
        }

        if (diff.RemovedCollections.Count > 0)
        {
            pills.Add(LocalizationService.FormatForCulture(
                cultureName,
                "CustomWidget.Health.RemovedColFormat",
                string.Join(",", diff.RemovedCollections)));
        }

        return pills;
    }

    private static bool AreCapsulesEqual(CustomWidgetCapsuleSpec? a, CustomWidgetCapsuleSpec? b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (!string.Equals(a.Title, b.Title, StringComparison.Ordinal) ||
            !string.Equals(a.Summary, b.Summary, StringComparison.Ordinal) ||
            !string.Equals(a.Glyph, b.Glyph, StringComparison.Ordinal) ||
            !string.Equals(a.Badge, b.Badge, StringComparison.Ordinal) ||
            !string.Equals(a.BadgeIsWarning, b.BadgeIsWarning, StringComparison.Ordinal) ||
            !string.Equals(a.Progress, b.Progress, StringComparison.Ordinal) ||
            a.EnableBounceOnUpdate != b.EnableBounceOnUpdate)
        {
            return false;
        }

        if (ReferenceEquals(a.PrimaryAction, b.PrimaryAction))
        {
            return true;
        }

        if (a.PrimaryAction is null || b.PrimaryAction is null)
        {
            return false;
        }

        if (!string.Equals(a.PrimaryAction.Icon, b.PrimaryAction.Icon, StringComparison.Ordinal) ||
            !string.Equals(a.PrimaryAction.Tooltip, b.PrimaryAction.Tooltip, StringComparison.Ordinal))
        {
            return false;
        }

        IReadOnlyList<CustomWidgetActionSpec> clicksA = a.PrimaryAction.OnClick ?? [];
        IReadOnlyList<CustomWidgetActionSpec> clicksB = b.PrimaryAction.OnClick ?? [];
        if (clicksA.Count != clicksB.Count)
        {
            return false;
        }

        for (int i = 0; i < clicksA.Count; i++)
        {
            if (!string.Equals(clicksA[i].Type, clicksB[i].Type, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(clicksA[i].Key, clicksB[i].Key, StringComparison.Ordinal) ||
                !string.Equals(clicksA[i].Value, clicksB[i].Value, StringComparison.Ordinal) ||
                !string.Equals(clicksA[i].TargetId, clicksB[i].TargetId, StringComparison.Ordinal) ||
                !string.Equals(clicksA[i].Fn, clicksB[i].Fn, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static void CollectNodeTypes(CustomWidgetManifest manifest, HashSet<string> types)
    {
        foreach (CustomWidgetNodeSpec node in manifest.EnumerateAllNodes())
        {
            if (!string.IsNullOrWhiteSpace(node.Type))
            {
                types.Add(node.Type);
            }
        }
    }

    private static void CollectReferencedCollectionNames(CustomWidgetManifest manifest, HashSet<string> collections)
    {
        static void TryAddCol(string? rawCol, HashSet<string> target)
        {
            if (!string.IsNullOrWhiteSpace(rawCol) &&
                !rawCol.StartsWith("${", StringComparison.Ordinal) &&
                !rawCol.StartsWith("$", StringComparison.Ordinal))
            {
                string clean = rawCol.StartsWith("collections.", StringComparison.OrdinalIgnoreCase)
                    ? rawCol["collections.".Length..]
                    : rawCol;
                if (!string.IsNullOrWhiteSpace(clean))
                {
                    target.Add(clean);
                }
            }
        }

        foreach (CustomWidgetNodeSpec node in manifest.EnumerateAllNodes())
        {
            TryAddCol(node.Collection ?? node.Items, collections);
            TryAddCol(node.SuggestionsSource, collections);
        }

        foreach (CustomWidgetActionSpec act in manifest.EnumerateAllActions())
        {
            if (!string.IsNullOrWhiteSpace(act.Collection))
            {
                collections.Add(act.Collection);
            }
        }
    }

    private static void CollectBoundOrWrittenStateKeys(CustomWidgetManifest manifest, HashSet<string> keys)
    {
        foreach (CustomWidgetNodeSpec node in manifest.EnumerateAllNodes())
        {
            if (!string.IsNullOrWhiteSpace(node.Bind) &&
                !node.Bind.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase))
            {
                string k = node.Bind.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                    ? node.Bind["state.".Length..]
                    : node.Bind;
                if (!string.IsNullOrWhiteSpace(k))
                {
                    keys.Add(k);
                }
            }
        }

        foreach (CustomWidgetActionSpec act in manifest.EnumerateAllActions())
        {
            if (!string.IsNullOrWhiteSpace(act.Key))
            {
                string k = act.Key.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                    ? act.Key["state.".Length..]
                    : act.Key;
                if (!string.IsNullOrWhiteSpace(k))
                {
                    keys.Add(k);
                }
            }

            if (act.StateMap is not null)
            {
                foreach (string sk in act.StateMap.Keys)
                {
                    string k = sk.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                        ? sk["state.".Length..]
                        : sk;
                    if (!string.IsNullOrWhiteSpace(k))
                    {
                        keys.Add(k);
                    }
                }
            }
        }
    }
}
