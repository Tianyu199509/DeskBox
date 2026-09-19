using System.Text.Json;
using System.Text.Json.Nodes;
using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// Projects widget STYLE settings into a portable backup document and
/// patches them back onto a settings.json file (roadmap §10, "格子样式"
/// domain). Style syncs; layout does not — absolute positions, monitor
/// topology, capsule ordering/free placements and internal version
/// counters are deliberately absent from the whitelists below.
///
/// The projection works on the serialized JSON DOM, not the typed model:
/// settings.json is a flat facade schema, so whitelists are expressed in
/// wire names and any untouched content round-trips byte-for-byte.
/// </summary>
internal static class WidgetStyleBackupProjection
{
    internal const int DocumentSchemaVersion = 1;
    internal const string DocumentKind = "widget-style";

    private static readonly FileService s_fileService = new();

    /// <summary>
    /// Flat settings.json keys carrying widget-shell style/display
    /// preferences — the wire-name whitelist for both directions.
    /// Excluded on purpose: widgetCapsuleBarOrder (ordering = layout),
    /// widgetCapsuleFreePlacements (coordinates), widgetCompactSettingsVersion
    /// (internal schema counter), and every WidgetLayoutSettingsSlice key.
    /// </summary>
    // internal (not private) so the drift ratchet in
    // WidgetStyleProjectionContractTests can enumerate the whitelist.
    internal static readonly HashSet<string> ShellKeys = new(StringComparer.Ordinal)
    {
        "defaultWidgetWidth", "defaultWidgetHeight",
        "widgetOpacity", "widgetMaterialType", "widgetMaterialIntensity",
        "widgetForegroundMode", "widgetForegroundColor",
        "widgetBorderColorMode", "widgetBorderStyle",
        "widgetCornerPreference",
        "widgetAnimationEffect", "widgetAnimationSpeed",
        "widgetAnimationSlideDirection", "widgetAnimationEasingIntensity",
        "widgetLayerMode",
        "keepWidgetsVisibleOnShowDesktop",
        "displayWidgetChromeMode", "interactiveWidgetChromeMode",
        "widgetCollapseBehavior",
        // JsonPropertyName("widgetCapsuleModeEnabled") on the facade — the
        // wire name differs from the slice property name.
        "widgetCapsuleModeEnabled",
        "widgetCompactWidthMode", "widgetCompactExpansionDirection",
        "widgetCapsuleArrangementMode",
        "widgetCapsuleBarSpacing", "widgetCapsuleBarPlacement",
        "widgetCapsuleBarDirection",
        "widgetCollapsedStyle", "widgetCompactContentMode",
        "widgetCompactHideSensitiveContent",
        "widgetCompactAnimationEffect", "widgetCompactAnimationDurationMs",
        "widgetCompactExpandDelayMs", "widgetCompactCollapseDelayMs",
        "widgetCompactMediaCornerMode",
        "widgetTitleIconMode", "showHoverButtons", "widgetHoverButtonActions",
        "resizeSnapEnabled", "widgetSnapSpacing", "focusClickedWidgetOnRaise",
        "iconSize", "textSize", "layoutDensity",
        "layoutDensityScale", "horizontalSpacingScale", "verticalSpacingScale"
    };

    /// <summary>
    /// Per-widget style fields whitelisted out of each WidgetConfig element:
    /// display/title/sort preferences only. Geometry (x, y, width, height,
    /// position*, compactPlacement), file bindings (mappedFolderPath, items,
    /// fileAddedAt*), state (isVisible, isDisabled, *Locked) and metadata
    /// never leave the device.
    /// </summary>
    internal static readonly HashSet<string> WidgetKeys = new(StringComparer.Ordinal)
    {
        "name", "isDefaultTitle", "viewMode", "iconSizeOverride",
        "isCollapsed", "compactWidth", "sortMode", "sortDescending"
    };

    internal sealed record ApplyResult(
        bool Applied,
        int ShellFieldsPatched,
        int WidgetsPatched,
        string? SkippedReason);

    /// <summary>
    /// Builds the widget-style document from the live settings object.
    /// Returns the UTF-8 JSON payload stored as widget-style.json inside a
    /// scoped backup archive.
    /// </summary>
    internal static byte[] Serialize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        JsonObject settingsDom = JsonSerializer
            .SerializeToNode(settings, SettingsJsonContext.Default.AppSettings)!
            .AsObject();

        var shell = new JsonObject();
        foreach (string key in ShellKeys)
        {
            if (settingsDom.TryGetPropertyValue(key, out JsonNode? value) && value is not null)
            {
                shell[key] = value.DeepClone();
            }
        }

        var widgets = new JsonObject();
        if (settingsDom.TryGetPropertyValue("widgets", out JsonNode? widgetsNode) &&
            widgetsNode is JsonArray widgetsArray)
        {
            foreach (JsonNode? node in widgetsArray)
            {
                if (node is not JsonObject element ||
                    element["id"] is not JsonValue idValue ||
                    !idValue.TryGetValue(out string? id) ||
                    string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var style = new JsonObject();
                foreach (string key in WidgetKeys)
                {
                    if (element.TryGetPropertyValue(key, out JsonNode? value))
                    {
                        style[key] = value?.DeepClone();
                    }
                }

                // widgetKind is carried for verification only — it is never
                // applied; a kind mismatch skips the widget defensively.
                if (element.TryGetPropertyValue("widgetKind", out JsonNode? kindNode))
                {
                    style["widgetKind"] = kindNode?.DeepClone();
                }

                widgets[id] = style;
            }
        }

        var document = new JsonObject
        {
            ["schemaVersion"] = DocumentSchemaVersion,
            ["kind"] = DocumentKind,
            ["createdAtUtc"] = DateTimeOffset.UtcNow,
            ["sourceDeviceId"] = DeviceIdentity.Id,
            ["shell"] = shell,
            ["widgets"] = widgets
        };
        // JsonNode.ToJsonString stays on STJ's non-generic DOM path — the
        // generic SerializeToUtf8Bytes<JsonObject> overload carries
        // RequiresUnreferencedCode/RequiresDynamicCode and trips the AOT
        // audit's unexpected-warning gate.
        return System.Text.Encoding.UTF8.GetBytes(document.ToJsonString());
    }

    /// <summary>
    /// Patches the style document onto the live data files, atomically via
    /// the same resilient-write path the settings store itself uses. Only
    /// whitelisted keys are written; widgets are matched by id and verified
    /// by widgetKind before any field is touched. Shell keys always land in
    /// settings.json; per-widget keys land wherever the device layout
    /// currently lives — widget-layout.json once it exists, else the legacy
    /// settings.json widgets array (pre-migration profiles, which the layout
    /// store then adopts).
    /// </summary>
    internal static async Task<ApplyResult> ApplyAsync(
        byte[] documentBytes,
        string settingsPath,
        string layoutPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documentBytes);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);

        JsonObject document;
        try
        {
            document = JsonNode.Parse(documentBytes)?.AsObject()
                       ?? throw new InvalidDataException("The widget-style document is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The widget-style document is invalid.", ex);
        }

        if (document["schemaVersion"] is not JsonValue versionValue ||
            !versionValue.TryGetValue(out int version) ||
            version > DocumentSchemaVersion)
        {
            throw new InvalidDataException("The widget-style document schema is not supported.");
        }

        if (document["kind"] is not JsonValue kindValue ||
            !kindValue.TryGetValue(out string? kind) ||
            !string.Equals(kind, DocumentKind, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The widget-style document kind is invalid.");
        }

        if (!File.Exists(settingsPath))
        {
            return new ApplyResult(false, 0, 0, "settings.json does not exist");
        }

        // A previous restore may have died between the two commits — heal
        // the pair from its journal before snapshotting originals for this
        // transaction, otherwise this apply would build on half-applied
        // state and its own rollback would re-corrupt the files.
        await RecoverPendingRestoreAsync(settingsPath, layoutPath, cancellationToken);

        // Keep the original bytes: the settings and layout commits below are
        // two independent stores and cannot be atomic, so a layout-commit
        // failure rolls the settings file back to exactly this content.
        string originalSettingsJson = await File.ReadAllTextAsync(
            settingsPath, cancellationToken);
        JsonObject settingsDom = JsonNode.Parse(originalSettingsJson)?.AsObject()
            ?? throw new InvalidDataException("settings.json is empty.");

        int shellPatched = 0;
        if (document["shell"] is JsonObject shellDoc)
        {
            foreach ((string key, JsonNode? value) in shellDoc)
            {
                if (!ShellKeys.Contains(key))
                {
                    continue;
                }

                settingsDom[key] = value?.DeepClone();
                shellPatched++;
            }
        }

        // The widgets array lives in widget-layout.json once the device store
        // exists; before adoption it is still a settings.json key.
        JsonObject? layoutDom = null;
        JsonArray? widgetsArray = null;
        string? originalLayoutJson = null;
        bool widgetsInLayoutFile = File.Exists(layoutPath);
        if (widgetsInLayoutFile)
        {
            originalLayoutJson = await File.ReadAllTextAsync(
                layoutPath, cancellationToken);
            layoutDom = JsonNode.Parse(originalLayoutJson)?.AsObject()
                ?? throw new InvalidDataException("widget-layout.json is empty.");

            widgetsArray = layoutDom["layout"]?["widgets"] as JsonArray;
        }
        else if (settingsDom.TryGetPropertyValue("widgets", out JsonNode? widgetsNode))
        {
            widgetsArray = widgetsNode as JsonArray;
        }

        int widgetsPatched = 0;
        if (document["widgets"] is JsonObject widgetsDoc &&
            widgetsArray is not null)
        {
            foreach (JsonNode? node in widgetsArray)
            {
                if (node is not JsonObject element ||
                    element["id"] is not JsonValue idValue ||
                    !idValue.TryGetValue(out string? id) ||
                    string.IsNullOrEmpty(id) ||
                    widgetsDoc[id] is not JsonObject style)
                {
                    continue;
                }

                string? docKind = style["widgetKind"] is JsonValue docKindValue &&
                                  docKindValue.TryGetValue(out string? dk) ? dk : null;
                string? localKind = element["widgetKind"] is JsonValue localKindValue &&
                                    localKindValue.TryGetValue(out string? lk) ? lk : null;
                if (!string.Equals(docKind, localKind, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (string key in WidgetKeys)
                {
                    if (style.TryGetPropertyValue(key, out JsonNode? value))
                    {
                        element[key] = value?.DeepClone();
                    }
                }

                widgetsPatched++;
            }
        }

        string patched = settingsDom.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        if (!widgetsInLayoutFile)
        {
            // Single store: the resilient commit is already atomic — no
            // journal needed.
            await ResilientJsonStore.SaveAsync(settingsPath, patched);
            return new ApplyResult(true, shellPatched, widgetsPatched, null);
        }

        // Two stores cannot commit atomically, so journal both originals
        // BEFORE the first commit (the pending marker is written last —
        // its presence means both snapshots are complete on disk). After
        // both commits land, the committed flag is written before cleanup
        // so a crash there cannot roll back a finished transaction.
        await File.WriteAllTextAsync(
            JournalOrigPath(settingsPath), originalSettingsJson, cancellationToken);
        await File.WriteAllTextAsync(
            JournalOrigPath(layoutPath), originalLayoutJson!, cancellationToken);
        await File.WriteAllTextAsync(
            JournalPendingPath(settingsPath),
            DateTimeOffset.UtcNow.ToString("O"),
            cancellationToken);
        try
        {
            await ResilientJsonStore.SaveAsync(settingsPath, patched);
            await ResilientJsonStore.SaveAsync(
                layoutPath,
                layoutDom!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            await File.WriteAllTextAsync(
                JournalCommittedPath(settingsPath), "1", cancellationToken);
        }
        catch
        {
            // Any failure — including a rollback-worthy layout-commit one —
            // goes through the same recovery the next launch would run:
            // restore both originals (primary AND .bak) from the journal.
            await TryRecoverPendingRestoreAsync(settingsPath, layoutPath);
            throw;
        }

        await CleanupJournalAsync(settingsPath, layoutPath);
        return new ApplyResult(true, shellPatched, widgetsPatched, null);
    }

    /// <summary>
    /// Crash recovery for the two-file style-restore transaction. A pending
    /// marker without the committed flag means the commits did not both
    /// land — the journaled originals go back to the live files AND their
    /// .bak siblings: the resilient commit rotates primaries into .bak, so
    /// restoring only the primary would leave the half-applied bytes one
    /// later corruption away from resurrecting. The committed flag means
    /// the transaction finished — only cleanup remains.
    /// </summary>
    private static async Task RecoverPendingRestoreAsync(
        string settingsPath,
        string layoutPath,
        CancellationToken cancellationToken)
    {
        bool pending = File.Exists(JournalPendingPath(settingsPath));
        bool committed = File.Exists(JournalCommittedPath(settingsPath));
        if (!pending && !committed)
        {
            // Snapshots orphaned by a torn journal write — nothing was ever
            // committed, so they are just litter.
            await TryDeleteFileAsync(JournalOrigPath(settingsPath));
            await TryDeleteFileAsync(JournalOrigPath(layoutPath));
            return;
        }

        if (committed)
        {
            await CleanupJournalAsync(settingsPath, layoutPath);
            return;
        }

        // Uncommitted transaction. The pending marker is written only after
        // BOTH snapshots are complete, and this path only exists when the
        // layout file existed at journal time — so a missing snapshot means
        // the journal itself is corrupt. Fail closed: keep every artifact
        // and never touch the live files on a guess.
        string settingsOrig = JournalOrigPath(settingsPath);
        string layoutOrig = JournalOrigPath(layoutPath);
        if (!File.Exists(settingsOrig) || !File.Exists(layoutOrig))
        {
            throw new InvalidDataException(
                "Widget-style restore journal is incomplete: pending marker " +
                "without both original snapshots; live files left untouched.");
        }

        // The journal is deleted ONLY after a complete restore — a partial
        // restore keeps it so the next apply retries to convergence
        // (re-restoring the same snapshots is idempotent).
        await RestoreJournalSnapshotAsync(
            settingsOrig, settingsPath, cancellationToken);
        await RestoreJournalSnapshotAsync(
            layoutOrig, layoutPath, cancellationToken);
        await CleanupJournalAsync(settingsPath, layoutPath);
    }

    private static async Task RestoreJournalSnapshotAsync(
        string origPath,
        string livePath,
        CancellationToken cancellationToken)
    {
        string original = await File.ReadAllTextAsync(origPath, cancellationToken);
        await File.WriteAllTextAsync(livePath, original, cancellationToken);
        await File.WriteAllTextAsync(
            ResilientJsonStore.GetBackupPath(livePath), original, cancellationToken);
    }

    /// <summary>
    /// Recovery on the failure path of a live apply — never masks the
    /// original exception.
    /// </summary>
    private static async Task TryRecoverPendingRestoreAsync(
        string settingsPath,
        string layoutPath)
    {
        try
        {
            await RecoverPendingRestoreAsync(
                settingsPath, layoutPath, CancellationToken.None);
        }
        catch (Exception recoveryException)
        {
            App.Log(
                $"[WidgetStyleRestore] Journal recovery after apply failure " +
                $"also failed: {recoveryException}");
        }
    }

    /// <summary>
    /// Ordered journal cleanup. Pending goes first and the committed flag
    /// LAST, stopping at the first artifact that survives — a "pending-only"
    /// remnant would read as an uncommitted transaction and could roll back
    /// (or delete live files over) finished work.
    /// </summary>
    private static async Task CleanupJournalAsync(string settingsPath, string layoutPath)
    {
        string pending = JournalPendingPath(settingsPath);
        await TryDeleteFileAsync(pending);
        if (File.Exists(pending))
        {
            return;
        }

        await TryDeleteFileAsync(JournalOrigPath(settingsPath));
        await TryDeleteFileAsync(JournalOrigPath(layoutPath));
        await TryDeleteFileAsync(JournalCommittedPath(settingsPath));
    }

    /// <summary>
    /// Quiet headless delete routed through the file-safety kernel (the
    /// module-boundary ratchet forbids raw File.Delete outside owned
    /// domains). Absent paths and failures are both non-fatal here.
    /// </summary>
    private static async Task TryDeleteFileAsync(string path)
    {
        try
        {
            await s_fileService.DeleteEntryAsync(path, recycle: false);
        }
        catch (Exception ex)
        {
            App.Log($"[WidgetStyleRestore] Journal cleanup failed for {path}: {ex}");
        }
    }

    private static string JournalPendingPath(string settingsPath) =>
        settingsPath + ".style-restore.pending";

    private static string JournalCommittedPath(string settingsPath) =>
        settingsPath + ".style-restore.committed";

    private static string JournalOrigPath(string livePath) =>
        livePath + ".style-restore.orig";
}
