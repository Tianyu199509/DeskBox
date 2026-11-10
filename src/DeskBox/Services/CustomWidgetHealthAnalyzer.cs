using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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

public sealed record CustomWidgetHealthIssue(
    string Code,
    string Message,
    string? SuggestedPrompt,
    bool IsWarning = true);

public sealed record CustomWidgetHealthReport(
    int TargetWidth,
    int TargetHeight,
    double MeasuredWidth,
    double MeasuredHeight,
    int VerticalOverflowPx,
    int MaxCardNestingDepth,
    IReadOnlyList<CustomWidgetHealthIssue> Issues,
    string PrivacySafeDataShapeSummary)
{
    public bool HasIssues => Issues.Count > 0;
}

/// <summary>
/// Host-enforced safety gates and deterministic layout/theme/reference telemetry for Studio co-creation:
/// 1) P0 User Data Safety Gate (blocks accidental loss of populated <c>store.json</c> collections/state);
/// 2) Structural AST Diff between revisions (detects silent drift or dropped i18n locales);
/// 3) Live Health &amp; Privacy-Safe Data Shape Telemetry injected into subsequent AI turns.
/// </summary>
public static class CustomWidgetHealthAnalyzer
{
    private static readonly HashSet<string> EphemeralRuntimeStateKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "aiStatus", "aiError", "aiUpdatedAt",
        "networkStatus", "networkError",
        "sourceStatus", "sourceError", "sourceUpdatedAt", "lastUpdated",
        "feedTitle", "feedCount", "topHeadline",
        "transformStatus", "transformOutput", "transformError",
        "httpStatus", "httpOk", "httpError",
        "fileText", "fileWriteStatus", "fileWriteError",
        "commandStatus", "commandExitCode", "commandStdout", "commandStderr"
    };

    private static bool IsEphemeralRuntimeStateKey(string key) =>
        EphemeralRuntimeStateKeys.Contains(key) ||
        key.StartsWith("sourceStatus.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("sourceError.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("fileText.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("commandStdout.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("commandExitCode.", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("__tableSort.", StringComparison.OrdinalIgnoreCase);

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
        CollectReferencedCollectionNames(candidateManifest.View, candidateCollections);
        foreach (CustomWidgetNodeSpec pop in candidateManifest.Popovers)
        {
            CollectReferencedCollectionNames(pop, candidateCollections);
        }

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
            CollectNodeTypes(current.View, initialNodes);
            foreach (CustomWidgetNodeSpec pop in current.Popovers)
            {
                CollectNodeTypes(pop, initialNodes);
            }
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
        CollectNodeTypes(previous.View, oldNodes);
        foreach (CustomWidgetNodeSpec pop in previous.Popovers)
        {
            CollectNodeTypes(pop, oldNodes);
        }
        CollectNodeTypes(current.View, newNodes);
        foreach (CustomWidgetNodeSpec pop in current.Popovers)
        {
            CollectNodeTypes(pop, newNodes);
        }

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

    public static CustomWidgetHealthReport AnalyzeHealth(
        string rawManifestJson,
        CustomWidgetManifest manifest,
        CustomWidgetStorePayload? storePayload,
        double measuredWidth = 0,
        double measuredHeight = 0,
        string? cultureName = null,
        string? logicJs = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var issues = new List<CustomWidgetHealthIssue>();

        int targetW = manifest.DefaultSize.Width;
        int targetH = manifest.DefaultSize.Height;
        // Account for ~38px widget title chrome inside frame
        double availableBodyHeight = Math.Max(100, targetH - 38);
        int verticalOverflow = 0;

        bool hasRootScrollableList = HasRootFlexibleListOrScroll(manifest.View);
        if (measuredHeight > 0 && !hasRootScrollableList && measuredHeight > availableBodyHeight + 24)
        {
            verticalOverflow = (int)Math.Round(measuredHeight - availableBodyHeight);
            issues.Add(new CustomWidgetHealthIssue(
                Code: "layout.vertical_overflow",
                Message: LocalizationService.FormatForCulture(
                    cultureName,
                    "CustomWidget.Health.VerticalOverflowMessageFormat",
                    verticalOverflow),
                SuggestedPrompt: LocalizationService.FormatForCulture(
                    cultureName,
                    "CustomWidget.Health.VerticalOverflowAdviceFormat",
                    verticalOverflow)));
        }

        if (TryDetectDuplicateInnerHeader(manifest, cultureName, out CustomWidgetHealthIssue? dupHeaderIssue) &&
            dupHeaderIssue is not null)
        {
            issues.Add(dupHeaderIssue);
        }

        int maxCardDepth = ComputeMaxCardNestingDepth(manifest.View, 0);
        if (maxCardDepth >= 3)
        {
            issues.Add(new CustomWidgetHealthIssue(
                Code: "visual.card_nesting",
                Message: LocalizationService.FormatForCulture(
                    cultureName,
                    "CustomWidget.Health.CardNestingMessageFormat",
                    maxCardDepth),
                SuggestedPrompt: LocalizationService.TranslateForCulture(
                    cultureName,
                    "CustomWidget.Health.CardNestingAdvice")));
        }

        var hardcodedHex = new List<string>();
        CollectHardcodedContrastHazardColors(manifest.View, hardcodedHex);
        if (hardcodedHex.Count > 0)
        {
            string sample = hardcodedHex[0];
            issues.Add(new CustomWidgetHealthIssue(
                Code: "theme.hardcoded_hex",
                Message: LocalizationService.FormatForCulture(
                    cultureName,
                    "CustomWidget.Health.HardcodedHexMessageFormat",
                    sample),
                SuggestedPrompt: LocalizationService.FormatForCulture(
                    cultureName,
                    "CustomWidget.Health.HardcodedHexAdviceFormat",
                    sample)));
        }

        if (manifest.I18n.Count >= 2)
        {
            var allKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var dict in manifest.I18n.Values)
            {
                foreach (string k in dict.Keys)
                {
                    allKeys.Add(k);
                }
            }

            var incompleteLocales = manifest.I18n
                .Where(kvp => kvp.Value.Count < allKeys.Count)
                .Select(kvp => kvp.Key)
                .ToList();
            if (incompleteLocales.Count > 0)
            {
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "i18n.incomplete",
                    Message: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.IncompleteI18nMessageFormat",
                        string.Join(", ", incompleteLocales)),
                    SuggestedPrompt: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.IncompleteI18nAdviceFormat",
                        string.Join(", ", incompleteLocales))));
            }
        }

        foreach (CustomWidgetHealthIssue deadIssue in DetectDeadControlsAndUnreadMutations(manifest, cultureName, logicJs).Take(2))
        {
            issues.Add(deadIssue);
        }

        if (TryDetectHardcodedParameterCandidate(manifest, cultureName, out CustomWidgetHealthIssue? settingsHint) &&
            settingsHint is not null)
        {
            issues.Add(settingsHint);
        }

        if (!string.IsNullOrWhiteSpace(rawManifestJson))
        {
            CustomWidgetParseResult hintCheck = CustomWidgetManifestParser.ValidateWithRuntimeHints(
                rawManifestJson,
                logicJs: logicJs,
                localeMap: manifest.I18n);
            foreach (CustomWidgetValidationDiagnostic diag in hintCheck.Diagnostics.Take(2))
            {
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "manifest.hint",
                    Message: diag.Message,
                    SuggestedPrompt: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.HintAdviceFormat",
                        diag.Path,
                        diag.Message)));
            }
        }

        string dataShape = BuildPrivacySafeDataShapeSummary(manifest, storePayload);
        return new CustomWidgetHealthReport(
            TargetWidth: targetW,
            TargetHeight: targetH,
            MeasuredWidth: Math.Round(measuredWidth, 1),
            MeasuredHeight: Math.Round(measuredHeight, 1),
            VerticalOverflowPx: verticalOverflow,
            MaxCardNestingDepth: maxCardDepth,
            Issues: issues,
            PrivacySafeDataShapeSummary: dataShape);
    }

    public static string BuildPrivacySafeDataShapeSummary(
        CustomWidgetManifest manifest,
        CustomWidgetStorePayload? storePayload)
    {
        var sb = new StringBuilder();
        sb.Append($"Widget DefaultSize: {manifest.DefaultSize.Width}x{manifest.DefaultSize.Height}. ");

        var stateKeys = new HashSet<string>(manifest.State.Keys, StringComparer.Ordinal);
        if (storePayload is not null)
        {
            foreach (string k in storePayload.State.Keys)
            {
                if (!IsEphemeralRuntimeStateKey(k))
                {
                    stateKeys.Add(k);
                }
            }
        }

        if (stateKeys.Count > 0)
        {
            sb.Append("State keys: [").Append(string.Join(", ", stateKeys.OrderBy(x => x))).Append("]. ");
        }

        var allColNames = new HashSet<string>(manifest.Collections.Keys, StringComparer.Ordinal);
        if (storePayload is not null)
        {
            foreach (string k in storePayload.Collections.Keys)
            {
                allColNames.Add(k);
            }
        }

        if (allColNames.Count > 0)
        {
            sb.Append("Collections: ");
            var parts = new List<string>();
            foreach (string col in allColNames.OrderBy(x => x))
            {
                int count = 0;
                var fieldNames = new HashSet<string>(StringComparer.Ordinal);
                if (storePayload is not null &&
                    storePayload.Collections.TryGetValue(col, out List<CustomWidgetRecord>? recs))
                {
                    foreach (CustomWidgetRecord r in recs)
                    {
                        if (r.IsDeleted)
                        {
                            continue;
                        }
                        count++;
                        foreach (string fk in r.Fields.Keys)
                        {
                            fieldNames.Add(fk);
                        }
                    }
                }

                string fieldsStr = fieldNames.Count > 0
                    ? $"fields=[{string.Join(", ", fieldNames.OrderBy(x => x))}]"
                    : "fields=[]";
                parts.Add($"{col}(count={count}, {fieldsStr})");
            }
            sb.Append(string.Join("; ", parts)).Append('.');
        }

        if (storePayload is not null)
        {
            var runtimeNotes = new List<string>();
            if (storePayload.State.TryGetValue("sourceStatus", out string? srcStatus) &&
                !string.IsNullOrWhiteSpace(srcStatus))
            {
                runtimeNotes.Add($"sourceStatus={srcStatus}");
            }
            if (storePayload.State.TryGetValue("sourceError", out string? srcErr) &&
                !string.IsNullOrWhiteSpace(srcErr))
            {
                runtimeNotes.Add($"sourceError=<external_api_sample>{SanitizeTelemetrySample(srcErr)}</external_api_sample>");
            }
            foreach ((string sk, string sv) in storePayload.State)
            {
                if (sk.StartsWith("sourceError.", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(sv))
                {
                    runtimeNotes.Add($"{sk}=<external_api_sample>{SanitizeTelemetrySample(sv)}</external_api_sample>");
                }
            }
            if (storePayload.State.TryGetValue("aiError", out string? aiErr) &&
                !string.IsNullOrWhiteSpace(aiErr))
            {
                runtimeNotes.Add($"aiError=<external_api_sample>{SanitizeTelemetrySample(aiErr)}</external_api_sample>");
            }

            if (runtimeNotes.Count > 0)
            {
                sb.Append(" Runtime diagnostics: ").Append(string.Join(", ", runtimeNotes)).Append('.');
            }
        }

        return sb.ToString().Trim();
    }

    private static string SanitizeTelemetrySample(string raw)
    {
        string trimmed = raw.Replace("\r", " ", StringComparison.Ordinal)
                            .Replace("\n", " ", StringComparison.Ordinal)
                            .Replace("</external_api_sample>", "[/external_api_sample]", StringComparison.OrdinalIgnoreCase)
                            .Trim();
        return trimmed.Length > 240 ? trimmed[..240] + "…" : trimmed;
    }

    public static string FormatTelemetryPromptBlock(CustomWidgetHealthReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("## Live Host Telemetry & Data Shape (Privacy-Safe)");
        sb.Append("- ").AppendLine(report.PrivacySafeDataShapeSummary);
        if (report.MeasuredHeight > 0)
        {
            sb.AppendFormat(
                CultureInfo.InvariantCulture,
                "- Rendered Content Height: {0:0}px inside {1}x{2} frame{3}.",
                report.MeasuredHeight,
                report.TargetWidth,
                report.TargetHeight,
                report.VerticalOverflowPx > 0 ? $" (VERTICAL OVERFLOW +{report.VerticalOverflowPx}px — make layout more compact!)" : " (fits cleanly)");
            sb.AppendLine();
        }

        if (report.Issues.Count > 0)
        {
            sb.AppendLine("- Detected Health Warnings to keep in mind:");
            foreach (CustomWidgetHealthIssue issue in report.Issues)
            {
                sb.Append("  * [").Append(issue.Code).Append("] ").AppendLine(issue.Message);
            }
        }

        return sb.ToString().Trim();
    }

    private static bool HasRootFlexibleListOrScroll(CustomWidgetNodeSpec root)
    {
        if (string.Equals(root.Type, "Scroll", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(root.Type, "List", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(root.Type, "Table", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(root.Type, "Stack", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(root.Orientation, "horizontal", StringComparison.OrdinalIgnoreCase))
        {
            return root.Children.Any(c =>
                string.Equals(c.Type, "List", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Type, "Table", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Type, "Scroll", StringComparison.OrdinalIgnoreCase));
        }

        return false;
    }

    private static bool TryDetectDuplicateInnerHeader(
        CustomWidgetManifest manifest,
        string? cultureName,
        out CustomWidgetHealthIssue? issue)
    {
        issue = null;
        string normalizedWidgetTitle = NormalizeHeaderComparableText(manifest.ResolveLocalizedText(manifest.Name));
        if (string.IsNullOrWhiteSpace(normalizedWidgetTitle))
        {
            normalizedWidgetTitle = NormalizeHeaderComparableText(manifest.Name);
        }

        if (string.IsNullOrWhiteSpace(normalizedWidgetTitle))
        {
            return false;
        }

        CustomWidgetNodeSpec root = manifest.View;
        if (root.Children.Count == 0)
        {
            return false;
        }

        CustomWidgetNodeSpec first = root.Children[0];
        string? candidateRaw = null;

        if (string.Equals(first.Type, "Text", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(first.Style, "title", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(first.Style, "subtitle", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(first.Style, "bodyStrong", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(first.Style, "metric", StringComparison.OrdinalIgnoreCase)))
        {
            candidateRaw = first.Text ?? first.Title;
        }
        else if ((string.Equals(first.Type, "Grid", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(first.Type, "Stack", StringComparison.OrdinalIgnoreCase) ||
                  string.Equals(first.Type, "Card", StringComparison.OrdinalIgnoreCase)) &&
                 first.Children.Count > 0)
        {
            CustomWidgetNodeSpec? firstText = first.Children.FirstOrDefault(static c =>
                string.Equals(c.Type, "Text", StringComparison.OrdinalIgnoreCase));
            if (firstText is not null)
            {
                candidateRaw = firstText.Text ?? firstText.Title;
            }
        }

        if (string.IsNullOrWhiteSpace(candidateRaw))
        {
            return false;
        }

        string resolvedCandidate = manifest.ResolveLocalizedText(candidateRaw);
        string normalizedCandidate = NormalizeHeaderComparableText(resolvedCandidate);
        if (string.IsNullOrWhiteSpace(normalizedCandidate) ||
            !string.Equals(normalizedCandidate, normalizedWidgetTitle, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        issue = new CustomWidgetHealthIssue(
            Code: "layout.duplicate_header",
            Message: LocalizationService.TranslateForCulture(
                cultureName,
                "CustomWidget.Health.DupHeaderMessage"),
            SuggestedPrompt: LocalizationService.TranslateForCulture(
                cultureName,
                "CustomWidget.Health.DupHeaderAdvice"));
        return true;
    }

    private static string NormalizeHeaderComparableText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(raw.Length);
        foreach (char ch in raw)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }
        return sb.ToString();
    }

    private static int ComputeMaxCardNestingDepth(CustomWidgetNodeSpec? node, int currentCardDepth)
    {
        if (node is null)
        {
            return currentCardDepth;
        }

        int nextDepth = string.Equals(node.Type, "Card", StringComparison.OrdinalIgnoreCase)
            ? currentCardDepth + 1
            : currentCardDepth;
        int max = nextDepth;

        max = Math.Max(max, ComputeMaxCardNestingDepth(node.ItemTemplate, nextDepth));
        foreach (CustomWidgetNodeSpec c in node.Children)
        {
            max = Math.Max(max, ComputeMaxCardNestingDepth(c, nextDepth));
        }
        return max;
    }

    private static void CollectHardcodedContrastHazardColors(
        CustomWidgetNodeSpec? node,
        List<string> results,
        bool hasExplicitSurface = false)
    {
        if (node is null)
        {
            return;
        }

        bool nodeHasSurface = hasExplicitSurface ||
                              !string.IsNullOrWhiteSpace(node.Background) ||
                              node.LinearGradient is not null;

        if (!nodeHasSurface)
        {
            CheckHexHazard(node.Color, results);
            CheckHexHazard(node.Tone, results);
            CheckHexHazard(node.TextColor, results);
        }

        CollectHardcodedContrastHazardColors(node.ItemTemplate, results, nodeHasSurface);
        foreach (CustomWidgetNodeSpec c in node.Children)
        {
            CollectHardcodedContrastHazardColors(c, results, nodeHasSurface);
        }
    }

    private static void CheckHexHazard(string? raw, List<string> results)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        string trimmed = raw.Trim();
        if (!trimmed.StartsWith('#'))
        {
            return;
        }

        string hex = trimmed[1..];
        if (hex.Length == 3)
        {
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }
        else if (hex.Length == 8)
        {
            if (int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int alpha) && alpha < 90)
            {
                return; // Low-alpha tint is safe across themes
            }
            hex = hex[2..];
        }

        if (hex.Length == 6 &&
            int.TryParse(hex[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int r) &&
            int.TryParse(hex[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int g) &&
            int.TryParse(hex[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int b))
        {
            double luminance = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
            // Near-white (> 0.85) or near-black (< 0.18) hardcoded hex breaks in Light or Dark mode
            if (luminance > 0.85 || luminance < 0.18)
            {
                if (!results.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    results.Add(trimmed);
                }
            }
        }
    }

    private static void CollectNodeTypes(CustomWidgetNodeSpec? node, HashSet<string> types)
    {
        if (node is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(node.Type))
        {
            types.Add(node.Type);
        }

        CollectNodeTypes(node.ItemTemplate, types);
        foreach (CustomWidgetNodeSpec h in node.HoverActions)
        {
            CollectNodeTypes(h, types);
        }
        foreach (CustomWidgetNodeSpec c in node.Children)
        {
            CollectNodeTypes(c, types);
        }
    }

    private static void CollectReferencedCollectionNames(CustomWidgetNodeSpec? node, HashSet<string> collections)
    {
        if (node is null)
        {
            return;
        }

        void TryAddCol(string? rawCol)
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
                    collections.Add(clean);
                }
            }
        }

        TryAddCol(node.Collection ?? node.Items);
        TryAddCol(node.SuggestionsSource);

        CollectActionCollections(node.OnClick, collections);
        CollectActionCollections(node.OnSubmit, collections);
        CollectActionCollections(node.OnChange, collections);
        CollectActionCollections(node.OnDropActions, collections);
        CollectActionCollections(node.OnRowClick, collections);
        CollectActionCollections(node.OnItemClick, collections);
        CollectActionCollections(node.OnReorder, collections);
        CollectActionCollections(node.OnInlineCommit, collections);
        CollectReferencedCollectionNames(node.ItemTemplate, collections);
        foreach (CustomWidgetNodeSpec h in node.HoverActions)
        {
            CollectReferencedCollectionNames(h, collections);
        }
        foreach (CustomWidgetNodeSpec c in node.Children)
        {
            CollectReferencedCollectionNames(c, collections);
        }
    }

    private static void CollectActionCollections(IReadOnlyList<CustomWidgetActionSpec>? actions, HashSet<string> collections)
    {
        if (actions is null)
        {
            return;
        }

        foreach (CustomWidgetActionSpec act in actions)
        {
            if (!string.IsNullOrWhiteSpace(act.Collection))
            {
                collections.Add(act.Collection);
            }
            CollectActionCollections(act.Then, collections);
        }
    }

    private static void CollectBoundOrWrittenStateKeys(CustomWidgetManifest manifest, HashSet<string> keys)
    {
        void VisitActions(IReadOnlyList<CustomWidgetActionSpec>? actions)
        {
            if (actions is null)
            {
                return;
            }

            foreach (CustomWidgetActionSpec act in actions)
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
                VisitActions(act.Then);
            }
        }

        void VisitNode(CustomWidgetNodeSpec? node)
        {
            if (node is null)
            {
                return;
            }

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

            VisitActions(node.OnClick);
            VisitActions(node.OnSubmit);
            VisitActions(node.OnChange);
            VisitActions(node.OnDropActions);
            VisitActions(node.OnRowClick);
            VisitActions(node.OnItemClick);
            VisitActions(node.OnReorder);
            VisitActions(node.OnInlineCommit);
            VisitNode(node.ItemTemplate);
            foreach (CustomWidgetNodeSpec h in node.HoverActions)
            {
                VisitNode(h);
            }
            foreach (CustomWidgetNodeSpec c in node.Children)
            {
                VisitNode(c);
            }
        }

        VisitNode(manifest.View);
        foreach (CustomWidgetNodeSpec pop in manifest.Popovers)
        {
            VisitNode(pop);
        }
        VisitActions(manifest.OnDrop?.Actions);
    }

    private static readonly Regex StateTokenReadRegex = new(
        @"\bstate\.([A-Za-z0-9_]+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Detects non-functional "facade" controls (buttons/tiles without actions, or actions mutating
    /// dummy state keys that no UI node, source, or follow-up action ever reads).
    /// </summary>
    public static IReadOnlyList<CustomWidgetHealthIssue> DetectDeadControlsAndUnreadMutations(
        CustomWidgetManifest manifest,
        string? cultureName = null,
        string? logicJs = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var issues = new List<CustomWidgetHealthIssue>();
        var readOrBoundKeys = new HashSet<string>(StringComparer.Ordinal);
        var mutatedKeys = new List<(string Key, string Path)>();

        void RecordExprReads(string? expr)
        {
            if (string.IsNullOrWhiteSpace(expr))
            {
                return;
            }

            foreach (Match m in StateTokenReadRegex.Matches(expr))
            {
                readOrBoundKeys.Add(m.Groups[1].Value);
            }
        }

        void ScanActions(IReadOnlyList<CustomWidgetActionSpec>? actions, string path)
        {
            if (actions is null)
            {
                return;
            }

            for (int i = 0; i < actions.Count; i++)
            {
                CustomWidgetActionSpec act = actions[i];
                string actPath = $"{path}[{i}]";
                RecordExprReads(act.Key);
                RecordExprReads(act.Value);
                RecordExprReads(act.TargetId);
                RecordExprReads(act.Text);
                RecordExprReads(act.Message);
                RecordExprReads(act.Url);
                RecordExprReads(act.Path);
                RecordExprReads(act.Guard);
                RecordExprReads(act.Prompt);
                RecordExprReads(act.Arg);
                RecordExprReads(act.Body);
                RecordExprReads(act.WorkingDirectory);
                RecordExprReads(act.PopoverId);
                RecordExprReads(act.FromIndex);
                RecordExprReads(act.ToIndex);
                if (act.ItemFields is not null)
                {
                    foreach (string v in act.ItemFields.Values)
                    {
                        RecordExprReads(v);
                    }
                }
                if (act.Headers is not null)
                {
                    foreach (string hv in act.Headers.Values)
                    {
                        RecordExprReads(hv);
                    }
                }
                if (act.Args is not null)
                {
                    foreach (string av in act.Args.Values)
                    {
                        RecordExprReads(av);
                    }
                }
                if (act.CommandArgs is not null)
                {
                    foreach (string ca in act.CommandArgs)
                    {
                        RecordExprReads(ca);
                    }
                }

                if ((string.Equals(act.Type, "state.set", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(act.Type, "state.toggle", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(act.Type, "state.increment", StringComparison.OrdinalIgnoreCase)) &&
                    !string.IsNullOrWhiteSpace(act.Key))
                {
                    string normKey = act.Key.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                        ? act.Key["state.".Length..]
                        : act.Key;
                    if (!string.IsNullOrWhiteSpace(normKey) &&
                        !normKey.StartsWith("pomo.", StringComparison.OrdinalIgnoreCase) &&
                        !normKey.StartsWith("countdown.", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(normKey, "currentTask", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(normKey, "selectedProviderId", StringComparison.OrdinalIgnoreCase))
                    {
                        mutatedKeys.Add((normKey, actPath));
                    }
                }

                ScanActions(act.Then, $"{actPath}.then");
            }
        }

        void ScanNode(CustomWidgetNodeSpec? node, string path)
        {
            if (node is null)
            {
                return;
            }

            if ((string.Equals(node.Type, "Button", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "IconButton", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(node.Type, "ControlTile", StringComparison.OrdinalIgnoreCase)) &&
                node.OnClick.Count == 0 &&
                string.IsNullOrWhiteSpace(node.Popover))
            {
                string label = node.Title ?? node.Text ?? node.Tooltip ?? node.Type;
                issues.Add(new CustomWidgetHealthIssue(
                    Code: "interaction.dead_button",
                    Message: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.DeadButtonMessageFormat",
                        label,
                        node.Type),
                    SuggestedPrompt: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.DeadButtonAdviceFormat",
                        label,
                        path)));
            }

            if (!string.IsNullOrWhiteSpace(node.Bind) &&
                !node.Bind.StartsWith("$settings.", StringComparison.OrdinalIgnoreCase))
            {
                string normBind = node.Bind.StartsWith("state.", StringComparison.OrdinalIgnoreCase)
                    ? node.Bind["state.".Length..]
                    : node.Bind;
                if (!string.IsNullOrWhiteSpace(normBind))
                {
                    readOrBoundKeys.Add(normBind);
                }
            }

            RecordExprReads(node.When);
            RecordExprReads(node.Text);
            RecordExprReads(node.Title);
            RecordExprReads(node.Subtitle);
            RecordExprReads(node.Value);
            RecordExprReads(node.Icon);
            RecordExprReads(node.Src);
            RecordExprReads(node.Color);
            RecordExprReads(node.Tone);
            RecordExprReads(node.TextColor);
            RecordExprReads(node.Background);
            RecordExprReads(node.BorderColor);
            RecordExprReads(node.CenterValue);
            RecordExprReads(node.CenterTitle);
            RecordExprReads(node.ElementsSource);
            RecordExprReads(node.SuggestionsSource);
            RecordExprReads(node.Progress);
            RecordExprReads(node.Indeterminate);
            RecordExprReads(node.Filter);
            RecordExprReads(node.FilterEquals);
            RecordExprReads(node.Collection);
            RecordExprReads(node.Items);
            RecordExprReads(node.Sparkline);
            RecordExprReads(node.Unit);
            RecordExprReads(node.FileName);
            RecordExprReads(node.FilePath);

            foreach (CustomWidgetTableColumnSpec col in node.TableColumns)
            {
                RecordExprReads(col.Header);
                RecordExprReads(col.Field);
                RecordExprReads(col.Color);
            }
            foreach (CustomWidgetChartSeriesSpec s in node.Series)
            {
                RecordExprReads(s.Name);
                RecordExprReads(s.ValueField);
                RecordExprReads(s.Color);
            }
            foreach (CustomWidgetCanvasElementSpec el in node.Elements)
            {
                RecordExprReads(el.X);
                RecordExprReads(el.Y);
                RecordExprReads(el.Width);
                RecordExprReads(el.Height);
                RecordExprReads(el.Radius);
                RecordExprReads(el.Rx);
                RecordExprReads(el.Ry);
                RecordExprReads(el.X2);
                RecordExprReads(el.Y2);
                RecordExprReads(el.Points);
                RecordExprReads(el.Data);
                RecordExprReads(el.Text);
                RecordExprReads(el.Fill);
                RecordExprReads(el.Stroke);
            }

            ScanActions(node.OnClick, $"{path}.onClick");
            ScanActions(node.OnSubmit, $"{path}.onSubmit");
            ScanActions(node.OnChange, $"{path}.onChange");
            ScanActions(node.OnDropActions, $"{path}.onDrop");
            ScanActions(node.OnRowClick, $"{path}.onRowClick");
            ScanActions(node.OnItemClick, $"{path}.onItemClick");
            ScanActions(node.OnReorder, $"{path}.onReorder");
            ScanActions(node.OnInlineCommit, $"{path}.onInlineCommit");

            ScanNode(node.ItemTemplate, $"{path}.itemTemplate");
            for (int i = 0; i < node.HoverActions.Count; i++)
            {
                ScanNode(node.HoverActions[i], $"{path}.hoverActions[{i}]");
            }
            for (int i = 0; i < node.Children.Count; i++)
            {
                ScanNode(node.Children[i], $"{path}.children[{i}]");
            }
        }

        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            RecordExprReads(src.Url);
            RecordExprReads(src.City);
            RecordExprReads(src.Path);
            RecordExprReads(src.Command);
            RecordExprReads(src.WorkingDirectory);
            if (src.Headers is not null)
            {
                foreach (string hv in src.Headers.Values)
                {
                    RecordExprReads(hv);
                }
            }
            if (src.Arguments is not null)
            {
                foreach (string ca in src.Arguments)
                {
                    RecordExprReads(ca);
                }
            }
        }

        if (manifest.Capsule is { } cap)
        {
            RecordExprReads(cap.Title);
            RecordExprReads(cap.Summary);
            RecordExprReads(cap.Badge);
            RecordExprReads(cap.Progress);
            RecordExprReads(cap.BadgeIsWarning);
            ScanActions(cap.PrimaryAction?.OnClick, "$.capsule.primaryAction.onClick");
        }

        if (!string.IsNullOrWhiteSpace(logicJs))
        {
            foreach (Match jsMatch in Regex.Matches(
                         logicJs,
                         @"\bctx\.state(?:\.([A-Za-z_][A-Za-z0-9_]*)|\[['""]([^'""]+)['""]\])"))
            {
                string jsKey = jsMatch.Groups[1].Success ? jsMatch.Groups[1].Value : jsMatch.Groups[2].Value;
                if (!string.IsNullOrWhiteSpace(jsKey))
                {
                    readOrBoundKeys.Add(jsKey);
                }
            }
        }

        ScanNode(manifest.View, "$.view");
        for (int i = 0; i < manifest.Popovers.Count; i++)
        {
            CustomWidgetNodeSpec pop = manifest.Popovers[i];
            string popKey = pop.PopoverId ?? pop.Id ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ScanNode(pop, $"$.popovers.{popKey}");
        }
        ScanActions(manifest.OnDrop?.Actions, "$.onDrop.actions");

        bool skipUnreadCheckBecauseExternalScriptNotLoaded =
            manifest.Script is not null && string.IsNullOrWhiteSpace(logicJs);

        var reportedUnread = new HashSet<string>(StringComparer.Ordinal);
        if (!skipUnreadCheckBecauseExternalScriptNotLoaded)
        {
            foreach ((string key, string actPath) in mutatedKeys)
            {
                if (!readOrBoundKeys.Contains(key) && reportedUnread.Add(key))
                {
                    issues.Add(new CustomWidgetHealthIssue(
                        Code: "interaction.unread_state",
                        Message: LocalizationService.FormatForCulture(
                            cultureName,
                            "CustomWidget.Health.UnreadStateMessageFormat",
                            key,
                            actPath),
                        SuggestedPrompt: LocalizationService.FormatForCulture(
                            cultureName,
                            "CustomWidget.Health.UnreadStateAdviceFormat",
                            key,
                            actPath)));
                }
            }
        }

        return issues;
    }

    private static bool TryDetectHardcodedParameterCandidate(
        CustomWidgetManifest manifest,
        string? cultureName,
        out CustomWidgetHealthIssue? issue)
    {
        issue = null;
        if (manifest.Settings.Count > 0 || manifest.Sources.Count == 0)
        {
            return false;
        }

        foreach (CustomWidgetSourceSpec src in manifest.Sources)
        {
            if (string.Equals(src.Type, "weather", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(src.City) &&
                !src.City.Contains("settings.", StringComparison.OrdinalIgnoreCase))
            {
                issue = new CustomWidgetHealthIssue(
                    Code: "settings.hardcoded_parameter",
                    Message: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.HardcodedSettingMessageFormat",
                        "weather.city",
                        src.City.Trim()),
                    SuggestedPrompt: LocalizationService.TranslateForCulture(
                        cultureName,
                        "CustomWidget.Health.HardcodedSettingAdvice"),
                    IsWarning: false);
                return true;
            }

            if ((string.Equals(src.Type, "httpJson", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(src.Type, "rss", StringComparison.OrdinalIgnoreCase)) &&
                !string.IsNullOrWhiteSpace(src.Url) &&
                !src.Url.Contains("settings.", StringComparison.OrdinalIgnoreCase))
            {
                string sourceLabel = !string.IsNullOrWhiteSpace(src.Id) ? src.Id : src.Type;
                issue = new CustomWidgetHealthIssue(
                    Code: "settings.hardcoded_parameter",
                    Message: LocalizationService.FormatForCulture(
                        cultureName,
                        "CustomWidget.Health.HardcodedSettingMessageFormat",
                        $"{sourceLabel}.url",
                        src.Url.Trim()),
                    SuggestedPrompt: LocalizationService.TranslateForCulture(
                        cultureName,
                        "CustomWidget.Health.HardcodedSettingAdvice"),
                    IsWarning: false);
                return true;
            }
        }

        return false;
    }
}
